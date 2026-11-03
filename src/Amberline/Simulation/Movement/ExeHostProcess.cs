using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Amberline.Data;

namespace Amberline.Simulation.Movement;

/// <summary>A failure of the helper process: it could not start, it exited, or it did not answer in time.</summary>
internal sealed class ExeHostException(string message) : Exception(message);

/// <summary>
/// NOT RETAIL (an addition retail never had: the retail server ran its own copy of this code): the x86 helper <c>native/exehost</c>, which
/// maps the player's own <c>League of Legends.exe</c> read-only after a SHA-256 check and runs the client's movement routines on request
/// over its stdin and stdout pipes (u32 op, u32 length, payload in; u32 length, payload out). One process serves one match.
/// </summary>
internal sealed class ExeHostProcess : IDisposable
{
    /// <summary>The one executable the helper's addresses belong to (the same digest the companion's <c>RetailClients.GameExecutableSha256</c> pins).</summary>
    internal const string GameExecutableSha256 = "AD8FFDFA9620898F4BFE8565A56AA0C2263C107DE152F31B7B007CAE3F631539";

    // A frame takes well under a millisecond and the longest plan a few; a reply this late means the process hangs.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly Process _process;
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly Timer _watchdog;
    private byte[] _reply = new byte[1 << 16];
    private volatile bool _timedOut;

    /// <summary>The helper binary: <c>AMBERLINE_EXEHOST</c>, else <c>exehost.exe</c> beside the server, else the development build.</summary>
    internal static string? Locate()
    {
        var name = OperatingSystem.IsWindows() ? "exehost.exe" : "exehost";
        var configured = Environment.GetEnvironmentVariable("AMBERLINE_EXEHOST");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, name),
                     Path.Combine(Directory.GetCurrentDirectory(), "native", "exehost", "build", name),
                     Path.Combine(Directory.GetCurrentDirectory(), "emu", "native", "exehost", "build", name),
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>Why the helper cannot run this match, or null when it can.</summary>
    internal static string? Unavailable(string? helper, string exe, string grid)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64) return $"the host is {RuntimeInformation.OSArchitecture}; the helper needs x86-64";
        if (helper is null || !File.Exists(helper)) return "exehost is not installed beside the server";
        if (!File.Exists(exe)) return "League of Legends.exe is missing from the game data root";
        if (!IsTheGameExecutable(exe)) return "League of Legends.exe is not the 1.0.0.126 executable (SHA-256 differs)";
        if (!File.Exists(grid)) return "the map's navigation grid is missing";
        return null;
    }

    // The digest of an 8 MB file costs about 50 ms, and every match asks; a file is hashed again when its path, size or write time changes. The
    // helper checks the digest itself as it maps the file.
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), bool> Hashed = new();

    private static bool IsTheGameExecutable(string exe)
    {
        var file = new FileInfo(exe);
        return Hashed.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), _ =>
        {
            using var stream = file.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(GameExecutableSha256, StringComparison.Ordinal);
        });
    }

    /// <summary>Starts the helper on the match's grid and stamps the movement view's blocked cells (the turret stamps) into it.</summary>
    internal ExeHostProcess(string helper, string exe, string gridPath, NavigationGrid grid)
    {
        var info = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(exe);
        info.ArgumentList.Add(gridPath);
        try { _process = Process.Start(info) ?? throw new ExeHostException("exehost did not start"); }
        catch (System.ComponentModel.Win32Exception failure) { throw new ExeHostException("exehost did not start: " + failure.Message); }
        _in = _process.StandardInput.BaseStream;
        _out = _process.StandardOutput.BaseStream;
        _watchdog = new Timer(_ => Kill(timedOut: true));
        try
        {
            var ready = Exchange(null, ReadOnlySpan<byte>.Empty, StartTimeout);
            var width = BitConverter.ToInt32(ready.Span[8..]);
            var height = BitConverter.ToInt32(ready.Span[12..]);
            if (width != grid.CountX || height != grid.CountY)
                throw new ExeHostException($"exehost loaded a {width}x{height} grid, the server a {grid.CountX}x{grid.CountY} one");
            var stamped = Call(2, grid.WalkableMovement.Span);
            if (BitConverter.ToUInt32(stamped.Span[4..]) != 0)
                throw new ExeHostException("the movement view does not block a cell the grid file blocks");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int ProcessId => _process.Id;

    /// <summary>One request and its reply. The reply is valid until the next call.</summary>
    internal ReadOnlyMemory<byte> Call(uint op, ReadOnlySpan<byte> payload) => Exchange(op, payload, CallTimeout);

    private ReadOnlyMemory<byte> Exchange(uint? op, ReadOnlySpan<byte> payload, TimeSpan timeout)
    {
        _watchdog.Change(timeout, Timeout.InfiniteTimeSpan);
        try
        {
            if (op is { } code)
            {
                Span<byte> head = stackalloc byte[8];
                BitConverter.TryWriteBytes(head, code);
                BitConverter.TryWriteBytes(head[4..], payload.Length);
                _in.Write(head);
                _in.Write(payload);
                _in.Flush();
            }
            Span<byte> length = stackalloc byte[4];
            _out.ReadExactly(length);
            var n = BitConverter.ToInt32(length);
            if (n < 0) throw new IOException("bad reply length");
            if (_reply.Length < n) _reply = new byte[Math.Max(n, _reply.Length * 2)];
            _out.ReadExactly(_reply, 0, n);
            return _reply.AsMemory(0, n);
        }
        catch (Exception failure) when (failure is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ExeHostException(Describe(failure));
        }
        finally
        {
            _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private string Describe(Exception failure)
    {
        if (_timedOut) return $"exehost did not answer within {CallTimeout.TotalSeconds:0} s";
        try
        {
            if (_process.WaitForExit(2000))
            {
                var error = _process.StandardError.ReadToEnd().Trim();
                return $"exehost stopped (exit 0x{_process.ExitCode:x}){(error.Length > 0 ? ": " + error : "")}";
            }
        }
        catch (InvalidOperationException) { }
        return "exehost pipe failed: " + failure.Message;
    }

    /// <summary>DIAGNOSTICS: ends the process as a crash would (the restart checks).</summary>
    internal void Kill(bool timedOut = false)
    {
        _timedOut |= timedOut;
        try { _process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        // End of input makes the helper exit by itself (exehost.c ReadExact); a helper that does not is ended.
        try { _in?.Dispose(); } catch (IOException) { }
        try { if (!_process.WaitForExit(1000)) _process.Kill(); } catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
