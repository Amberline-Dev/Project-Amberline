using System.Text;
using System.Text.Json;

using Amberline.Server;
using Amberline.Simulation.Minions;

namespace Amberline.Tests.Network;

public static class LoggingDiagnosticsChecks
{
    [Check("server.testing-diagnostics-configuration")]
    public static void TestingBudgetsAreBoundedAndRecorded()
    {
        var variables = new[] { "AMBERLINE_PACKET_TRACE", "AMBERLINE_PACKET_TRACE_BUDGET_MB",
            "LOG_FIDELITY_TRACE", "LOG_FIDELITY_BUDGET_MB", "LOG_COLLISION" };
        var saved = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "league-testing-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", "1");
            Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", "3");
            Environment.SetEnvironmentVariable("LOG_COLLISION", "1");
            foreach (var (value, packetMb, fidelityMb) in new[]
                     { ("1024", 1024, 1024), ("1", 1, 1), ("0", 256, 16),
                       ("1025", 256, 16), ("-1", 256, 16), ("invalid", 256, 16) })
            {
                Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE_BUDGET_MB", value);
                Environment.SetEnvironmentVariable("LOG_FIDELITY_BUDGET_MB", value);
                var path = Path.Combine(root, value);
                using (var logger = ServerLogger.Open(path, new StringWriter()))
                    for (var i = 0; i < 101; i++) TestAssert.True(logger.Admit("PKT", "DEBUG"));
                using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory.GetDirectories(path).Single(), "meta.json")));
                var json = metadata.RootElement;
                TestAssert.Equal(packetMb * 1024 * 1024, json.GetProperty("packet_detail_budget_bytes").GetInt32());
                TestAssert.Equal(fidelityMb * 1024 * 1024, FidelityTrace.ConfiguredBudgetBytes);
                TestAssert.Equal(FidelityTrace.ConfiguredBudgetBytes, json.GetProperty("fidelity_trace_budget_bytes").GetInt32());
                TestAssert.Equal(3, json.GetProperty("fidelity_trace_interval_ticks").GetInt32());
                TestAssert.True(json.GetProperty("collision_trace").GetBoolean());
                TestAssert.Equal("none", json.GetProperty("packet_detail_sampling").GetString()!);
                TestAssert.Equal(JsonValueKind.Null, json.GetProperty("packet_preview_bytes").ValueKind);
            }
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Check("server.packet-diagnostics-budget")]
    public static void PacketDetailBudgetAndSuppressionRemainOperational()
    {
        var oldTrace = Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE");
        Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", null);
        var root = Path.Combine(Path.GetTempPath(), "amberline-packet-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var logger = ServerLogger.Open(root, new StringWriter(), packetDetailBudgetBytes: 256))
            {
                TestAssert.True(logger.Admit("PKT", "DEBUG"), "the first packet detail is admitted");
                logger.Write("PKT", "DEBUG", "->", new Dictionary<string, object?>
                {
                    ["raw"] = new string('a', 512),
                    ["sampled"] = true,
                    ["truncated"] = true,
                    ["redacted"] = false,
                });
                logger.Information("SESSION", "after-cutoff");
            }

            var session = Directory.GetDirectories(root).Single();
            var records = ReadRecords(session);
            TestAssert.True(records.Any(record => record.GetProperty("msg").GetString() == "packet.detail.cutoff"),
                "a rejected detail emits one explicit cutoff record");
            TestAssert.True(records.Any(record => record.GetProperty("msg").GetString() == "after-cutoff"),
                "operational records survive packet-detail cutoff");

            var rateRoot = Path.Combine(Path.GetTempPath(), "amberline-packet-rate-" + Guid.NewGuid().ToString("N"));
            try
            {
                long monotonic = 0;
                using (var logger = ServerLogger.Open(rateRoot, new StringWriter(),
                    monotonicTimestamp: () => monotonic))
                {
                    var admitted = 0;
                    for (var index = 0; index < 101; index++)
                    {
                        if (!logger.Admit("PKT", "DEBUG")) continue;
                        admitted++;
                        logger.Write("PKT", "DEBUG", "->", new Dictionary<string, object?>
                        {
                            ["n"] = 1,
                            ["raw"] = "00",
                            ["sampled"] = true,
                            ["truncated"] = false,
                            ["redacted"] = false,
                        });
                    }
                    TestAssert.Equal(100, admitted, "normal packet details are capped at 100 records per second");
                }
                var rateRecords = ReadRecords(Directory.GetDirectories(rateRoot).Single());
                var suppression = rateRecords.Single(record => record.GetProperty("msg").GetString() == "packet.detail.suppressed");
                TestAssert.Equal(1, suppression.GetProperty("count").GetInt32(),
                    "suppressed details are emitted as one aggregate count");
            }
            finally { try { Directory.Delete(rateRoot, recursive: true); } catch { } }

            var traceRoot = Path.Combine(Path.GetTempPath(), "amberline-packet-trace-" +
                Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", "1");
                long monotonic = 0;
                using (var logger = ServerLogger.Open(traceRoot, new StringWriter(),
                    packetDetailBudgetBytes: 256, monotonicTimestamp: () => monotonic))
                {
                    for (var index = 0; index < 101; index++)
                        TestAssert.True(logger.Admit("PKT", "DEBUG"),
                            "full packet trace is unsampled within its storage budget");
                }
                var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                    Directory.GetDirectories(traceRoot).Single(), "meta.json"))).RootElement;
                TestAssert.True(metadata.GetProperty("packet_trace").GetBoolean());
                TestAssert.Equal(256, metadata.GetProperty("packet_detail_budget_bytes").GetInt32());
            }
            finally
            {
                Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", null);
                try { Directory.Delete(traceRoot, recursive: true); } catch { }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", oldTrace);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Check("server.logger-failing-sink")]
    public static void ConsoleFailureUsesOneIndependentFallbackWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-failing-log-" + Guid.NewGuid().ToString("N"));
        var fallback = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var oldCategories = Environment.GetEnvironmentVariable("LOG_CATEGORIES");
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("LOG_CATEGORIES", null);
        try
        {
            Console.SetOut(fallback);
            Console.SetError(new ThrowingWriter());
            using (var logger = ServerLogger.Open(root))
            {
                logger.Information("SESSION", "healthy-file-record");
                logger.Information("SESSION", "second");
                logger.Flush();
            }

            TestAssert.Equal(1, fallback.ToString().Split("WDIAGNOSTIC", StringSplitOptions.None).Length - 1,
                "a failed default console sink emits one bounded fallback warning");
            var session = Directory.GetDirectories(root).Single();
            var file = File.ReadAllText(Path.Combine(session, "session.jsonl"));
            TestAssert.True(file.Contains("healthy-file-record", StringComparison.Ordinal),
                "a failed console sink must not suppress healthy file logging");
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.SetEnvironmentVariable("LOG_LEVEL", oldLevel);
            Environment.SetEnvironmentVariable("LOG_CATEGORIES", oldCategories);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Check("server.logger-file-sink-failures")]
    public static void FileWriteFlushAndDisposeFailuresStayIndependent()
    {
        var oldLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var oldCategories = Environment.GetEnvironmentVariable("LOG_CATEGORIES");
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("LOG_CATEGORIES", null);
        try
        {
            foreach (var failure in Enum.GetValues<FailureKind>())
            {
                var root = Path.Combine(Path.GetTempPath(), "amberline-file-failure-" +
                    Guid.NewGuid().ToString("N"));
                var fallback = new StringWriter();
                var console = new StringWriter();
                try
                {
                    using (var logger = ServerLogger.Open(root, console,
                        fallbackConsole: fallback, fileWriter: new FailingWriter(failure)))
                    {
                        logger.Information("SESSION", "survives-file-failure");
                        logger.Flush();
                    }
                    TestAssert.Equal(1,
                        fallback.ToString().Split("WDIAGNOSTIC", StringSplitOptions.None).Length - 1,
                        $"a {failure} file failure emits one bounded fallback warning");
                    TestAssert.True(console.ToString().Contains("survives-file-failure", StringComparison.Ordinal),
                        $"a {failure} file failure must not suppress console output");
                }
                finally { try { Directory.Delete(root, recursive: true); } catch { } }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", oldLevel);
            Environment.SetEnvironmentVariable("LOG_CATEGORIES", oldCategories);
        }
    }

    [Check("server.logger-secret-redaction")]
    public static void KeySentinelsStayOutOfConsoleAndFileInBothTraceModes()
    {
        const string sentinel = "logging-secret-sentinel";
        var oldKey = Environment.GetEnvironmentVariable("AMBERLINE_MATCH_KEY");
        var oldTrace = Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE");
        var oldLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var oldCategories = Environment.GetEnvironmentVariable("LOG_CATEGORIES");
        Environment.SetEnvironmentVariable("AMBERLINE_MATCH_KEY", sentinel);
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("LOG_CATEGORIES", null);
        try
        {
            foreach (var trace in new string?[] { null, "1" })
            {
                Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", trace);
                var root = Path.Combine(Path.GetTempPath(), "amberline-secret-log-" +
                    Guid.NewGuid().ToString("N"));
                var console = new StringWriter();
                try
                {
                    using (var logger = ServerLogger.Open(root, console))
                    {
                        logger.Information("SESSION", $"event-{sentinel}");
                        logger.Error("SESSION", "exception-event",
                            new InvalidOperationException($"exception-{sentinel}"));
                    }
                    var session = Directory.GetDirectories(root).Single();
                    var file = File.ReadAllText(Path.Combine(session, "session.jsonl"));
                    TestAssert.False(console.ToString().Contains(sentinel, StringComparison.Ordinal),
                        $"event and exception keys are redacted from console ({trace ?? "default"})");
                    TestAssert.False(file.Contains(sentinel, StringComparison.Ordinal),
                        $"event and exception keys are redacted from file ({trace ?? "default"})");
                    TestAssert.True(console.ToString().Contains("[redacted-key]", StringComparison.Ordinal));
                }
                finally { try { Directory.Delete(root, recursive: true); } catch { } }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMBERLINE_MATCH_KEY", oldKey);
            Environment.SetEnvironmentVariable("AMBERLINE_PACKET_TRACE", oldTrace);
            Environment.SetEnvironmentVariable("LOG_LEVEL", oldLevel);
            Environment.SetEnvironmentVariable("LOG_CATEGORIES", oldCategories);
        }
    }

    [Check("server.packet-detail-admission-before-formatting")]
    public static void PacketDetailAdmissionPrecedesExpensiveFormatting()
    {
        var source = FindWorkspaceFile(Path.Combine("src", "Amberline", "Network", "Sessions",
            "MatchRuntimeNetwork.cs"));
        var text = File.ReadAllText(source);
        var start = text.IndexOf("private bool LogIncomingPacket", StringComparison.Ordinal);
        var end = text.IndexOf("private void LogIncomingOpcode", start, StringComparison.Ordinal);
        TestAssert.True(start >= 0 && end > start, "incoming packet logging body remains discoverable");
        var body = text[start..end];
        var admission = body.IndexOf("_log.Admit(\"PKT\", \"DEBUG\")", StringComparison.Ordinal);
        var decode = body.IndexOf("WirePacketDecoder.Decode", StringComparison.Ordinal);
        var hex = body.IndexOf("Convert.ToHexString", StringComparison.Ordinal);
        TestAssert.True(admission >= 0 && decode > admission && hex > admission,
            "packet admission must gate decode and raw hex formatting");
    }


    private static JsonElement[] ReadRecords(string session) => File.ReadLines(Path.Combine(session, "session.jsonl"))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
        .ToArray();

    private static string FindWorkspaceFile(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var direct = Path.Combine(current.FullName, relative);
                if (File.Exists(direct)) return direct;
                var nested = Path.Combine(current.FullName, "emu", relative);
                if (File.Exists(nested)) return nested;
            }
        }
        throw new FileNotFoundException(relative);
    }

    private enum FailureKind
    {
        Write,
        Flush,
        Dispose,
    }

    private sealed class FailingWriter(FailureKind failure) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (failure == FailureKind.Write) throw new IOException("injected file write failure");
        }

        public override void Flush()
        {
            if (failure == FailureKind.Flush) throw new IOException("injected file flush failure");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && failure == FailureKind.Dispose)
                throw new IOException("injected file dispose failure");
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("injected console failure");
    }
}
