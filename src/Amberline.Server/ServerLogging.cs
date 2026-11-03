using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;

namespace Amberline.Server;

public interface IServerLogger : ISessionLogSink, IDisposable
{
    void Information(string eventName, IReadOnlyDictionary<string, object?>? properties = null);
    void Information(string category, string eventName,
        IReadOnlyDictionary<string, object?>? properties = null);
    void Warning(string eventName, IReadOnlyDictionary<string, object?>? properties = null);
    void Warning(string category, string eventName,
        IReadOnlyDictionary<string, object?>? properties = null);
    void Error(string eventName, Exception exception,
        IReadOnlyDictionary<string, object?>? properties = null);
    void Error(string category, string eventName, Exception exception,
        IReadOnlyDictionary<string, object?>? properties = null);
    void Flush();
}

/// <summary>Structured per-run JSONL logging for server replay and telemetry.</summary>
public sealed class ServerLogger : IServerLogger
{
    private const int PacketDetailRatePerSecond = 100;
    private const int DefaultPacketBudgetBytes = 64 * 1024 * 1024;
    private const int FullTracePacketBudgetBytes = 256 * 1024 * 1024;
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
        { "t", "cat", "lvl", "msg", "mono_ns", "session_id" };
    private readonly TextWriter _console;
    private readonly TextWriter _fallbackConsole;
    private readonly TextWriter _file;
    private readonly string _sessionId;
    private readonly string _sessionDirectory;
    private readonly string? _clientLogDirectory;
    private readonly string _metadataPath;
    private readonly string? _matchKey;
    private readonly double _t0Epoch;
    private readonly HashSet<string>? _consoleCategories;
    private readonly int _consoleMinimum;
    private readonly bool _fullPacketTrace;
    private readonly int _packetBudgetBytes;
    private readonly Func<long> _monotonicTimestamp;
    private long _lastFlushTicks;
    private long _packetWindowStartTicks;
    private int _packetWindowCount;
    private int _suppressedPacketDetails;
    private int _remainingPacketBytes;
    private bool _packetCutoffReported;
    private bool _fileFailed;
    private bool _consoleFailed;
    private readonly HashSet<string> _fallbackWarnings = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _disposing;
    private bool _resultWritten;

    private ServerLogger(TextWriter console, TextWriter fallbackConsole, TextWriter file,
        string sessionId, double t0Epoch, string sessionDirectory, string metadataPath,
        string? clientLogDirectory, HashSet<string>? consoleCategories, int consoleMinimum,
        bool fullPacketTrace, int packetBudgetBytes, string? matchKey,
        Func<long> monotonicTimestamp)
    {
        _console = console;
        _fallbackConsole = fallbackConsole;
        _file = file;
        _sessionId = sessionId;
        _t0Epoch = t0Epoch;
        _sessionDirectory = sessionDirectory;
        _metadataPath = metadataPath;
        _clientLogDirectory = clientLogDirectory;
        _consoleCategories = consoleCategories;
        _consoleMinimum = consoleMinimum;
        _fullPacketTrace = fullPacketTrace;
        _packetBudgetBytes = packetBudgetBytes;
        _remainingPacketBytes = _packetBudgetBytes;
        _matchKey = matchKey;
        _monotonicTimestamp = monotonicTimestamp;
        _lastFlushTicks = _monotonicTimestamp();
        _packetWindowStartTicks = _lastFlushTicks;
    }

    /// <summary>
    /// The per-run folder under the log directory (<c>meta.json</c>, <c>session.jsonl</c>). DIAGNOSTICS: tool-only, so the scenario host
    /// (Amberline.Tools) writes its trace and manifests beside the session log; nothing in the shipped server reads it.
    /// </summary>
    public string SessionDirectory => _sessionDirectory;

    public static ServerLogger Open(string directory, TextWriter? console = null,
        string? clientLogDirectory = null, TextWriter? fallbackConsole = null,
        int? packetDetailBudgetBytes = null, TextWriter? fileWriter = null,
        Func<long>? monotonicTimestamp = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Log directory is required.", nameof(directory));
        Directory.CreateDirectory(directory);

        var monoBefore = Stopwatch.GetTimestamp();
        var t0Epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var monoAfter = Stopwatch.GetTimestamp();
        var sessionId = (Guid.TryParse(Environment.GetEnvironmentVariable("AMBERLINE_SESSION_ID"), out var gameId)
            ? gameId : Guid.NewGuid()).ToString();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var sessionDirectory = Path.Combine(directory, stamp);
        var suffix = 1;
        while (Directory.Exists(sessionDirectory))
            sessionDirectory = Path.Combine(directory, $"{stamp}_{++suffix}");
        Directory.CreateDirectory(sessionDirectory);

        var monoStart = TicksToNanoseconds((monoBefore + monoAfter) / 2);
        var fullPacketTrace = Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE") == "1";
        var configuredPacketBudget = int.TryParse(Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE_BUDGET_MB"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var megabytes) && megabytes is >= 1 and <= 1024
            ? megabytes * 1024 * 1024 : fullPacketTrace ? FullTracePacketBudgetBytes : DefaultPacketBudgetBytes;
        var packetBudgetBytes = packetDetailBudgetBytes ?? configuredPacketBudget;
        if (packetBudgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(packetDetailBudgetBytes));
        var metadata = new Dictionary<string, object?>
        {
            ["t0_epoch"] = t0Epoch,
            ["t0_human"] = DateTimeOffset.FromUnixTimeMilliseconds((long)(t0Epoch * 1000.0)).ToString("O"),
            ["commit"] = ResolveGitCommit(),
            ["platform"] = Environment.OSVersion.ToString(),
            ["session_id"] = sessionId,
            ["build"] = typeof(ServerLogger).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            ["process_id"] = Environment.ProcessId,
            ["mono_ns"] = monoStart,
            ["t0_mono_ns"] = monoStart,
            ["mono_ns_before"] = TicksToNanoseconds(monoBefore),
            ["mono_ns_after"] = TicksToNanoseconds(monoAfter),
            ["mono_ns_span_ns"] = TicksToNanoseconds(monoAfter - monoBefore),
            ["mono_ns_uncertainty_ns"] = TicksToNanoseconds(monoAfter - monoBefore) / 2,
            ["packet_detail_rate_per_second"] = PacketDetailRatePerSecond,
            ["packet_detail_budget_bytes"] = packetBudgetBytes,
            ["packet_trace"] = fullPacketTrace,
            ["packet_detail_replay"] = "partial",
            ["packet_detail_sampling"] = fullPacketTrace ? "none" : "rate_limited",
            ["packet_preview_bytes"] = fullPacketTrace ? null : (int?)32,
            ["fidelity_trace_interval_ticks"] = int.TryParse(Environment.GetEnvironmentVariable("LOG_FIDELITY_TRACE"),
                out var fidelityRate) && fidelityRate > 0 ? fidelityRate : 0,
            ["fidelity_trace_budget_bytes"] = FidelityTrace.ConfiguredBudgetBytes,
            ["collision_trace"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOG_COLLISION")),
        };
        var metadataPath = Path.Combine(sessionDirectory, "meta.json");
        File.WriteAllText(metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

        TextWriter file = fileWriter ?? new StreamWriter(Path.Combine(sessionDirectory, "session.jsonl"),
            append: true, new UTF8Encoding(false)) { AutoFlush = false };
        var categories = Environment.GetEnvironmentVariable("LOG_CATEGORIES")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var minimum = ParseLevel(Environment.GetEnvironmentVariable("LOG_LEVEL"));
        return new ServerLogger(console ?? Console.Error, fallbackConsole ?? Console.Out, file,
            sessionId, t0Epoch, sessionDirectory, metadataPath, clientLogDirectory,
            categories is { Count: > 0 } ? categories : null, minimum, fullPacketTrace,
            packetBudgetBytes, Environment.GetEnvironmentVariable("AMBERLINE_MATCH_KEY"),
            monotonicTimestamp ?? Stopwatch.GetTimestamp);
    }

    public void Information(string eventName, IReadOnlyDictionary<string, object?>? properties = null) =>
        WriteRecord("SESSION", "INFO", eventName, null, properties);

    public void Information(string category, string eventName,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        WriteRecord(category, "INFO", eventName, null, properties);

    public void Warning(string eventName, IReadOnlyDictionary<string, object?>? properties = null) =>
        WriteRecord("SESSION", "WARN", eventName, null, properties);

    public void Warning(string category, string eventName,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        WriteRecord(category, "WARN", eventName, null, properties);

    public void Error(string eventName, Exception exception,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        WriteRecord("SESSION", "ERROR", eventName, exception, properties);

    public void Error(string category, string eventName, Exception exception,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        WriteRecord(category, "ERROR", eventName, exception, properties);

    /// <summary>
    /// Admits packet-detail formatting before decoding or hex conversion. The
    /// default path is bounded to 100 records per wall-clock second; full
    /// trace is unsampled but remains bounded by its larger storage budget.
    /// </summary>
    public bool Admit(string category, string level)
    {
        if (_disposed || category != "PKT" || level != "DEBUG") return !_disposed;
        if (_packetCutoffReported) return false;
        var now = _monotonicTimestamp();
        if (now - _packetWindowStartTicks >= Stopwatch.Frequency)
        {
            EmitSuppressedPacketDetails();
            _packetWindowStartTicks = now;
            _packetWindowCount = 0;
        }
        if (_fullPacketTrace || _packetWindowCount < PacketDetailRatePerSecond)
        {
            _packetWindowCount++;
            return true;
        }
        _suppressedPacketDetails++;
        return false;
    }

    public void MatchCompleted(MatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_disposed || _resultWritten) return;
        _resultWritten = true;
        Information("GAMEPLAY", "match.ended", new Dictionary<string, object?> { ["result"] = result });
        Flush();
        var path = Path.Combine(_sessionDirectory, "match-result.json");
        try
        {
            var contents = JsonSerializer.Serialize(new { session_id = _sessionId, result },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path + ".tmp", contents);
            File.Move(path + ".tmp", path);
            Information("SESSION", "match.result.saved", new Dictionary<string, object?> { ["path"] = path });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Error("SESSION", "match.result.save-failed", exception,
                new Dictionary<string, object?> { ["path"] = path });
        }
    }

    /// <summary>Writes a source-compatible DEBUG/INFO/WARN/ERROR record.</summary>
    public void Write(string category, string level, string message,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Log category is required.", nameof(category));
        if (string.IsNullOrWhiteSpace(level))
            throw new ArgumentException("Log level is required.", nameof(level));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Log message is required.", nameof(message));
        WriteRecord(category, level.ToUpperInvariant(), message, null, properties);
    }

    public void Flush()
    {
        if (_disposed) return;
        try
        {
            if (!_fileFailed) _file.Flush();
            _lastFlushTicks = _monotonicTimestamp();
        }
        catch (Exception exception)
        {
            MarkSinkFailure("file", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposing = true;
        try
        {
            EmitSuppressedPacketDetails(force: true);
            try { CaptureClientLog(); }
            catch (Exception exception) { MarkSinkFailure("capture", exception); }
            try
            {
                if (!_fileFailed) _file.Flush();
            }
            catch (Exception exception)
            {
                MarkSinkFailure("file", exception);
            }
            try { _file.Dispose(); }
            catch (Exception exception) { MarkSinkFailure("file", exception); }
        }
        finally
        {
            _disposed = true;
            _disposing = false;
        }
    }

    /// <summary>Archives a new local client log as an unattributed candidate; seat attribution uses launcher diagnostics.</summary>
    public string? CaptureClientLog(string? releaseDirectory = null)
    {
        var directory = releaseDirectory ?? _clientLogDirectory ??
            Environment.GetEnvironmentVariable("AMBERLINE_CLIENT_LOG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            UpdateCaptureStatus("not_configured", null, null);
            return null;
        }
        if (!Directory.Exists(directory))
        {
            UpdateCaptureStatus("source_missing", null, null);
            return null;
        }

        FileInfo? source;
        try
        {
            var started = DateTimeOffset.FromUnixTimeMilliseconds((long)(_t0Epoch * 1000)).UtcDateTime;
            source = new DirectoryInfo(directory).EnumerateFiles("*_r3dlog.txt")
                .Where(file => file.CreationTimeUtc >= started && file.LastWriteTimeUtc >= started)
                .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CaptureFailure("enumerate_failed", null, exception);
        }
        if (source is null)
        {
            UpdateCaptureStatus("source_missing", null, null);
            return null;
        }

        var destination = Path.Combine(_sessionDirectory, "client_r3dlog.txt");
        try
        {
            string? start = null;
            using (var reader = new StreamReader(source.FullName))
            using (var writer = new StreamWriter(destination, append: false, new UTF8Encoding(false)))
            {
                var first = true;
                while (reader.ReadLine() is { } line)
                {
                    var redacted = RedactClientLine(line, _matchKey);
                    if (first)
                    {
                        start = ParseClientStart(redacted);
                        first = false;
                    }
                    writer.WriteLine(redacted);
                }
            }
            UpdateCaptureStatus("succeeded", source.Name, null, start);
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CaptureFailure("copy_failed", source.Name, exception, destination);
        }
    }

    private string? CaptureFailure(string status, string? source, Exception exception, string? destination = null)
    {
        UpdateCaptureStatus(status, source, exception.GetType().Name);
        WriteRecord("DIAGNOSTIC", "WARN", "client-log.capture-incomplete", null,
            new Dictionary<string, object?>
            {
                ["status"] = status,
                ["source"] = source,
                ["destination"] = destination,
                ["error"] = exception.GetType().Name,
                ["capture_complete"] = false,
            });
        return null;
    }

    private void UpdateCaptureStatus(string status, string? source, string? error, string? start = null)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                File.ReadAllText(_metadataPath)) ?? new();
            metadata["client_r3dlog_capture"] = new Dictionary<string, object?>
            {
                ["status"] = status,
                ["source"] = source,
                ["start"] = start,
                ["error"] = error,
                ["capture_complete"] = status == "succeeded",
                ["attribution"] = "unattributed local candidate; correlate launcher gameId/playerId/processId",
            };
            metadata["client_r3dlog_capture_status"] = status;
            if (source is not null) metadata["client_r3dlog_source"] = source;
            if (start is not null) metadata["client_r3dlog_start"] = start;
            metadata["client_r3dlog_attribution"] = "unattributed local candidate; correlate launcher gameId/playerId/processId";
            File.WriteAllText(_metadataPath,
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MarkSinkFailure("metadata", exception);
        }
    }

    private void WriteRecord(string category, string level, string eventName, Exception? exception,
        IReadOnlyDictionary<string, object?>? properties) =>
        WriteRecordCore(category, level, eventName, exception, properties);

    private void WriteRecordCore(string category, string level, string eventName, Exception? exception,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (_disposed) return;
        try
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (properties is not null)
                foreach (var property in properties)
                    if (!Reserved.Contains(property.Key)) fields[property.Key] = RedactProperty(property.Key, property.Value);
            if (exception is not null)
            {
                fields["exc"] = exception.GetType().FullName;
                fields["tb"] = RedactText(exception.ToString());
            }

            var nowTicks = _monotonicTimestamp();
            var record = new Dictionary<string, object?>(fields, StringComparer.Ordinal)
            {
                ["t"] = Math.Round((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) - _t0Epoch,
                    4, MidpointRounding.ToEven),
                ["cat"] = category,
                ["lvl"] = level,
                ["msg"] = eventName,
                ["mono_ns"] = TicksToNanoseconds(nowTicks),
                ["session_id"] = _sessionId,
            };
            var line = RedactText(JsonSerializer.Serialize(record));
            var packetDetail = category == "PKT" && level == "DEBUG";
            if (packetDetail)
            {
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (_packetCutoffReported || bytes > _remainingPacketBytes)
                {
                    ReportPacketCutoff(bytes);
                    return;
                }
                _remainingPacketBytes -= bytes;
            }
            TryWriteFile(line);
            TryWriteConsole(category, level, eventName, exception);
            if (level is "WARN" or "ERROR" && !_fileFailed) Flush();
            else if (!_fileFailed && (nowTicks - _lastFlushTicks) / (double)Stopwatch.Frequency >= 0.25)
                Flush();
        }
        catch (Exception writeException)
        {
            MarkSinkFailure("record", writeException);
        }
    }

    private void ReportPacketCutoff(int rejectedBytes)
    {
        if (_packetCutoffReported) return;
        _packetCutoffReported = true;
        var written = _packetBudgetBytes - _remainingPacketBytes;
        WriteRecordCore("PKT", "INFO", "packet.detail.cutoff", null,
            new Dictionary<string, object?>
            {
                ["budget_bytes"] = _packetBudgetBytes,
                ["written_bytes"] = written,
                ["rejected_record_bytes"] = rejectedBytes,
                ["suppressed_records"] = _suppressedPacketDetails,
                ["trace"] = _fullPacketTrace,
                ["sampled"] = !_fullPacketTrace,
                ["truncated"] = false,
                ["redacted"] = true,
                ["cutoff"] = true,
                ["capture_complete"] = false,
                ["partial_replay"] = true,
            });
    }

    private void EmitSuppressedPacketDetails(bool force = false)
    {
        if (_suppressedPacketDetails == 0 || !force &&
            _monotonicTimestamp() - _packetWindowStartTicks < Stopwatch.Frequency) return;
        var count = _suppressedPacketDetails;
        _suppressedPacketDetails = 0;
        WriteRecordCore("PKT", "INFO", "packet.detail.suppressed", null,
            new Dictionary<string, object?>
            {
                ["count"] = count,
                ["rate_per_second"] = PacketDetailRatePerSecond,
                ["sampled"] = true,
                ["truncated"] = false,
                ["redacted"] = true,
                ["aggregate"] = true,
                ["partial_replay"] = true,
            });
    }

    private void TryWriteFile(string line)
    {
        if (_fileFailed) return;
        try { _file.WriteLine(line); }
        catch (Exception exception) { MarkSinkFailure("file", exception); }
    }

    private void TryWriteConsole(string category, string level, string eventName, Exception? exception)
    {
        if (_consoleFailed || (_consoleCategories is not null && !_consoleCategories.Contains(category)) ||
            LevelNumber(level) < _consoleMinimum) return;
        try
        {
            var rendered = eventName + (exception is null ? string.Empty : $": {exception.Message}");
            var message = RedactText(rendered).Replace("\r", "\\r").Replace("\n", "\\n");
            _console.WriteLine($"[{level[0]}{category}] {message}");
        }
        catch (Exception error) { MarkSinkFailure("console", error); }
    }

    private void MarkSinkFailure(string sink, Exception exception)
    {
        if (sink == "console")
        {
            if (_consoleFailed) return;
            _consoleFailed = true;
        }
        else if (sink == "file")
        {
            if (_fileFailed) return;
            _fileFailed = true;
        }
        if (!_fallbackWarnings.Add(sink)) return;
        try
        {
            _fallbackConsole.WriteLine($"[WDIAGNOSTIC] {sink} sink failed: {exception.GetType().Name}");
        }
        catch { }
    }

    private object? RedactProperty(string name, object? value)
    {
        if (name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("match_key", StringComparison.OrdinalIgnoreCase))
            return "[redacted]";
        return value is string text ? RedactText(text) : value;
    }

    private string RedactText(string text) => string.IsNullOrEmpty(_matchKey)
        ? text : text.Replace(_matchKey, "[redacted-key]", StringComparison.Ordinal);

    private static string RedactClientLine(string line, string? key)
    {
        if (line.Contains("Command Line", StringComparison.OrdinalIgnoreCase))
            return "[redacted connection command]";
        return string.IsNullOrEmpty(key) ? line : line.Replace(key, "[redacted-key]", StringComparison.Ordinal);
    }

    private static string? ParseClientStart(string line)
    {
        const string prefix = "--- Log started at ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var text = line[prefix.Length..].Trim();
        return DateTime.TryParseExact(text, "ddd MMM dd HH:mm:ss yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("O", CultureInfo.InvariantCulture) : null;
    }

    private static long TicksToNanoseconds(long ticks) =>
        checked((long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency)));

    private static int ParseLevel(string? level) => level?.ToUpperInvariant() switch
    {
        "DEBUG" => 10,
        "WARN" => 30,
        "ERROR" => 40,
        _ => 20,
    };

    private static int LevelNumber(string level) => level switch
    {
        "DEBUG" => 10,
        "WARN" => 30,
        "ERROR" => 40,
        _ => 20,
    };

    private static string ResolveGitCommit()
    {
        try
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
                directory = directory.Parent;
            if (directory is null) return "unknown";
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "rev-parse", "HEAD" },
            });
            if (process is null || !process.WaitForExit(1000)) return "unknown";
            var commit = process.StandardOutput.ReadToEnd().Trim();
            return commit.Length == 40 ? commit : "unknown";
        }
        catch { return "unknown"; }
    }
}
