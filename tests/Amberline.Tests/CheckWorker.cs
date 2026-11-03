using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Amberline.Tests;

/// <summary>
/// A long-lived runner process (<c>--worker</c>) that runs one check at a time, so the
/// process start-up, JIT and shared immutable data are paid once for many checks.
/// The protocol is line based: the parent writes <c>RUN &lt;check&gt;</c> or <c>EXIT</c>
/// to stdin; the worker answers each run with one <see cref="ResultPrefix"/> line
/// carrying the exit code and the check's captured console text. Anything else on
/// stdout or stderr (native code, a crashing runtime) is attached to the check that
/// was running.
/// </summary>
internal static class CheckWorker
{
    internal const string ResultPrefix = "\u0001LEMU-RESULT";

    /// <summary>The worker side: read commands until EXIT or end of input.</summary>
    internal static int Serve(IReadOnlyList<Program.DiscoveredCheck> checks)
    {
        var byName = checks.ToDictionary(check => check.Name, StringComparer.Ordinal);
        var utf8 = new UTF8Encoding(false);
        // The raw handles stay valid while a check redirects Console.Out/In.
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var protocol = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        while (input.ReadLine() is { } line)
        {
            if (line == "EXIT") return 0;
            if (!line.StartsWith("RUN ", StringComparison.Ordinal)) continue;
            var name = line[4..];
            var (exit, output) = byName.TryGetValue(name, out var check)
                ? RunInProcess(check)
                : (2, $"unknown check {name}");
            protocol.WriteLine($"{ResultPrefix}\t{exit}\t{Convert.ToBase64String(utf8.GetBytes(output))}");
        }

        return 0;
    }

    private static (int ExitCode, string Output) RunInProcess(Program.DiscoveredCheck check)
    {
        var environment = Snapshot();
        var directory = Environment.CurrentDirectory;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var capture = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetOut(capture);
        Console.SetError(capture);
        var installedOut = Console.Out;
        var installedError = Console.Error;
        int exit;
        try
        {
            exit = Program.InvokeCheck(check);
        }
        finally
        {
            var leaked = Leaks(environment, directory, installedOut, installedError);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Restore(environment, directory);
            if (leaked.Length != 0)
            {
                capture.WriteLine($"{check.Name}: left process-wide state changed ({leaked}); " +
                                  "mark the check Serial = true, or restore the state in a finally block");
                exit = 1;
            }
        }

        return (exit, capture.ToString());
    }

    private static Dictionary<string, string> Snapshot()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        return variables;
    }

    private static string Leaks(Dictionary<string, string> environment, string directory, TextWriter output, TextWriter error)
    {
        var leaked = new List<string>();
        var now = Snapshot();
        leaked.AddRange(now.Keys.Union(environment.Keys, StringComparer.Ordinal)
            .Where(key => !now.TryGetValue(key, out var value) || !environment.TryGetValue(key, out var before) || value != before)
            .Order(StringComparer.Ordinal).Select(key => "environment variable " + key));
        if (!string.Equals(Environment.CurrentDirectory, directory, StringComparison.Ordinal)) leaked.Add("working directory");
        if (!ReferenceEquals(Console.Out, output) || !ReferenceEquals(Console.Error, error)) leaked.Add("Console output");
        return string.Join(", ", leaked);
    }

    private static void Restore(Dictionary<string, string> environment, string directory)
    {
        var now = Snapshot();
        foreach (var key in now.Keys.Where(key => !environment.ContainsKey(key)))
            Environment.SetEnvironmentVariable(key, null);
        foreach (var pair in environment.Where(pair => !now.TryGetValue(pair.Key, out var value) || value != pair.Value))
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (!string.Equals(Environment.CurrentDirectory, directory, StringComparison.Ordinal))
            Environment.CurrentDirectory = directory;
    }
}

/// <summary>The runner's side of one <c>--worker</c> process.</summary>
internal sealed class WorkerProcess : IDisposable
{
    private const string EndOfStream = "\u0000end-of-stream";

    internal sealed record Outcome(int ExitCode, string Output, bool Lost);

    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();
    private readonly StringBuilder _error = new();

    private WorkerProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => _lines.Add(e.Data ?? EndOfStream);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_error) _error.AppendLine(e.Data);
        };
    }

    public static WorkerProcess Start()
    {
        var startInfo = Program.RunnerStartInfo("--worker");
        startInfo.RedirectStandardInput = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(false);
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        var process = new Process { StartInfo = startInfo };
        var worker = new WorkerProcess(process);
        if (!process.Start()) throw new InvalidOperationException("failed to start a check worker");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return worker;
    }

    /// <summary>The peak working set of the live worker, or 0 when it is gone.</summary>
    public long PeakWorkingSetBytes
    {
        get
        {
            try { _process.Refresh(); return _process.PeakWorkingSet64; }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException) { return 0; }
        }
    }

    /// <summary>Runs one check; a timeout, crash or lost pipe kills the worker and sets <c>Lost</c>.</summary>
    public Outcome Run(string name, double timeoutSeconds)
    {
        lock (_error) _error.Clear();
        var noise = new StringBuilder();
        try
        {
            _process.StandardInput.WriteLine("RUN " + name);
            _process.StandardInput.Flush();
        }
        catch (IOException)
        {
            return Died(name, noise);
        }

        var limit = timeoutSeconds >= int.MaxValue / 1000.0
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : TimeSpan.FromSeconds(timeoutSeconds);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = limit - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || !_lines.TryTake(out var line, remaining))
            {
                Kill();
                DrainInto(noise);
                return new Outcome(1, $"Timed out after {timeoutSeconds.ToString("g", CultureInfo.InvariantCulture)}s " +
                    $"(its console output is lost with the worker; rerun with --parallel 1 for it)\n{noise}{ErrorText()}", true);
            }

            if (line == EndOfStream) return Died(name, noise);
            if (!line.StartsWith(CheckWorker.ResultPrefix, StringComparison.Ordinal))
            {
                noise.AppendLine(line);
                continue;
            }

            var parts = line.Split('\t');
            var exit = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var output = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
            return new Outcome(exit, exit == 0 ? string.Empty : output + noise + ErrorText(), false);
        }
    }

    private Outcome Died(string name, StringBuilder noise)
    {
        Kill();
        DrainInto(noise);
        return new Outcome(1, $"The check worker exited with code {ExitCode()} while running {name}\n{noise}{ErrorText()}", true);
    }

    private int ExitCode()
    {
        try { return _process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private void DrainInto(StringBuilder noise)
    {
        while (_lines.TryTake(out var line))
            if (line != EndOfStream && !line.StartsWith(CheckWorker.ResultPrefix, StringComparison.Ordinal)) noise.AppendLine(line);
    }

    private string ErrorText()
    {
        // Let the asynchronous stderr reader deliver what the dying process wrote.
        if (_process.HasExited) _process.WaitForExit();
        lock (_error) return _error.ToString();
    }

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        _process.WaitForExit();
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.WriteLine("EXIT"); _process.StandardInput.Flush(); }
                catch (IOException) { }
                if (!_process.WaitForExit(5000)) Kill();
            }
        }
        catch (InvalidOperationException) { }
        // The output reader may still deliver a last event, so _lines is left to the GC.
        _process.Dispose();
    }
}
