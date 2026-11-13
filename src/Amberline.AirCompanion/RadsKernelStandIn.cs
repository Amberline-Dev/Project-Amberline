using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Amberline.AirCompanion.Rads;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: retail's 2011 launcher talked to Riot's RADS user kernel, an update service that no longer exists; this stands in for it so
/// the stock launcher runs for the classic start. Holds the mutex the launcher probes and serves its message-mode pipe
/// (design 2026-11-12 §4; the 'Classic launcher start' section of the AIR platform contract). Request: u32 size | u32 requestId | u32 type |
/// payload. Reply: u32 size | u32 type | u32 requestId | u32 hresult | payload. Versions come from the release folders under the root, so the
/// replies always name folders that exist; Repair, Validate and Cancel answer success and do nothing.
/// </summary>
public sealed class RadsKernelStandIn : IDisposable
{
    public const string MutexName = "RADS_USER_KERNEL_MUTEX_NAME";
    public const string PipeName = "rads_named_pipe";
    private const uint NoVersion = 0xFFFFFFFF;   // UpdateProject's "none": the launcher falls back to the release list

    private readonly Mutex mutex;
    private readonly string root, pipeName;
    private readonly Action<string> log;
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="pipeName">DIAGNOSTICS: test seam only (with <paramref name="mutexName"/>): the launcher always uses the two constants.</param>
    public RadsKernelStandIn(string root, Action<string> log, string pipeName = PipeName, string mutexName = MutexName)
    {
        this.root = root; this.log = log; this.pipeName = pipeName;
        mutex = new Mutex(false, mutexName, out var created);
        if (!created)
        {
            mutex.Dispose();
            throw new InvalidOperationException($"the mutex {mutexName} already exists: a RADS user kernel (or another stand-in) is running");
        }
    }

    /// <summary>Completes when the first client connects.</summary>
    public Task Connected => connected.Task;

    /// <summary>Serves one launcher connection at a time and listens again after a disconnect, until cancelled.</summary>
    /// <remarks>ponytail: one pipe instance; the launcher opens one connection (design 2026-11-12 §4). More instances when a second client ever shows up.</remarks>
    public async Task ServeAsync(CancellationToken cancel)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the launcher's pipe is a Windows message-mode pipe");
        while (!cancel.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            try { await pipe.WaitForConnectionAsync(cancel); }
            catch (OperationCanceledException) { return; }
            connected.TrySetResult();
            log("kernel: client connected");
            try
            {
                while (await ReadMessageAsync(pipe, cancel) is { } request)
                {
                    if (request.Length < 12) { log($"kernel: short request ignored ({Convert.ToHexString(request)})"); continue; }
                    var reply = Reply(request, out var what);
                    log($"kernel: {what} -> {Describe(reply)}");
                    await pipe.WriteAsync(reply, cancel);   // one write = one message
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException e) { log($"kernel: pipe error ({e.Message})"); }
            log("kernel: client disconnected; listening again");
        }
    }

    private static async Task<byte[]?> ReadMessageAsync(PipeStream pipe, CancellationToken cancel)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        do
        {
            var n = await pipe.ReadAsync(buffer, cancel);
            if (n == 0) return message.Length == 0 ? null : message.ToArray();
            message.Write(buffer, 0, n);
        } while (!pipe.IsMessageComplete);
        return message.ToArray();
    }

    /// <summary>The reply for one request, in the layout the class summary gives (design 2026-11-12 §4); <paramref name="what"/> is the decoded request for the log.</summary>
    public byte[] Reply(byte[] request, out string what)
    {
        var size = BitConverter.ToUInt32(request, 0);
        var id = BitConverter.ToUInt32(request, 4);
        var type = BitConverter.ToUInt32(request, 8);
        var at = 12;
        uint[] payload;   // the u32s after the hresult
        switch (type)
        {
            case 0x13: what = $"SetLogParameters(level={U32(request, ref at)})"; payload = []; break;
            case 0x05: { var name = Str(request, ref at); var list = Releases("projects", name); what = $"EnumProjectReleases(\"{name}\")"; payload = [(uint)list.Length, .. list]; break; }
            case 0x0D: { var name = Str(request, ref at); var list = Releases("solutions", name); what = $"EnumSolutionReleases(\"{name}\")"; payload = [(uint)list.Length, .. list]; break; }
            case 0x07: { var name = Str(request, ref at); what = $"UpdateProject(\"{name}\")"; payload = [Latest("projects", name)]; break; }
            case 0x0F: { var name = Str(request, ref at); var configuration = Str(request, ref at); what = $"UpdateSolution(\"{name}\", \"{configuration}\")"; payload = [Latest("solutions", name)]; break; }
            case 0x08: what = $"ValidateProject(\"{Str(request, ref at)}\")"; payload = []; break;
            case 0x10: what = $"ValidateSolution(\"{Str(request, ref at)}\")"; payload = []; break;
            case 0x16: what = "Cancel"; payload = []; break;
            case 0x17: what = $"Repair(flag={U32(request, ref at)})"; payload = []; break;
            default: what = $"unknown type 0x{type:X2} payload {Convert.ToHexString(request, 12, Math.Min(request.Length - 12, 64))}"; payload = [0, 0, 0, 0]; break;   // zero-filled, hr=0
        }
        what = $"id={id} type=0x{type:X2} {what}" + (size == request.Length ? "" : $" (size field {size}, message {request.Length} bytes)");
        var reply = new byte[16 + 4 * payload.Length];
        uint[] words = [(uint)reply.Length, type, id, 0, .. payload];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(reply.AsSpan(i * 4), words[i]);
        return reply;
    }

    private static uint? U32(byte[] request, ref int at)
    {
        if (at + 4 > request.Length) return null;
        at += 4;
        return BitConverter.ToUInt32(request, at - 4);
    }

    /// <summary>A RADS string: u32 length including the NUL, then the bytes. Null when the payload is short.</summary>
    private static string? Str(byte[] request, ref int at)
    {
        var length = U32(request, ref at);
        if (length is null || length > (uint)(request.Length - at)) return null;
        var text = Encoding.ASCII.GetString(request, at, (int)length.Value).TrimEnd('\0');
        at += (int)length.Value;
        return text;
    }

    /// <summary>The a.b.c.d folders under &lt;root&gt;\&lt;kind&gt;\&lt;name&gt;\releases, ascending. A name that is not a plain identifier has none.</summary>
    private uint[] Releases(string kind, string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return [];
        var folder = Path.Combine(root, kind, name, "releases");
        if (!Directory.Exists(folder)) return [];
        var versions = new List<uint>();
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            try { versions.Add(RadsLayout.ParseVersion(Path.GetFileName(directory))); }
            catch (ArgumentException) { }   // not a release folder
        }
        versions.Sort();
        return [.. versions];
    }

    private uint Latest(string kind, string? name)
    {
        var list = Releases(kind, name);
        if (list.Length == 0) log($"kernel: warn: no release folder for {kind}\\{name}; answering 0xFFFFFFFF");
        return list.Length == 0 ? NoVersion : list[^1];
    }

    private static string Describe(byte[] reply) =>
        $"[{string.Join(",", Enumerable.Range(0, reply.Length / 4).Select(i => "0x" + BitConverter.ToUInt32(reply, i * 4).ToString("X", CultureInfo.InvariantCulture)))}]";

    public void Dispose() => mutex.Dispose();
}
