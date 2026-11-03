using System.Globalization;
using System.Text.Json;
using Amberline.Tools.Diagnostics;
using ToolsProgram = Amberline.Tools.Program;

namespace Amberline.Tests;

public static class TriageChecks
{
    [Check("tools.triage-report-sections-and-metrics")]
    public static void SectionsAndMetrics()
    {
        var directory = Directory.CreateTempSubdirectory("triage-check-").FullName;
        try
        {
            WriteCapture(directory);
            var result = TriageAnalysis.Read(directory, 2);
            TestAssert.Near(43, result.HudOffset!.Value, 0);
            TestAssert.Near(4, result.Length, 0);
            foreach (var index in new[] { 0, 1, 2, 7, 12 }) AssertMetric(result, index, 1, 1.5);
            AssertMetric(result, 3, 1, 1);
            AssertMetric(result, 4, 1, 1);
            AssertMetric(result, 5, 1, 0.25);
            AssertMetric(result, 6, 1, 3);
            AssertMetric(result, 8, 1, 50);
            AssertMetric(result, 9, 1, 50);
            AssertMetric(result, 11, 1, 2);
            AssertMetric(result, 13, 1, 2);
            TestAssert.Equal(2L, result.PacketReasons["target-chase"], "observer enqueues count separately; latency pairs do not");
            TestAssert.Equal(1L, result.Invariants["blocked-cell"].Count);
            TestAssert.Equal(1L, result.Invariants["blocked-cell"].Ends);
            TestAssert.Near(1, result.Invariants["blocked-cell"].Duration, 1e-9);
            TestAssert.Near(0, result.Invariants["blocked-cell"].OpenDuration, 0);
            TestAssert.Equal(2L, result.ClientErrors.Values.Single().Count);
            TestAssert.Near(44.75, result.ClientErrors.Values.Single().Time!.Value, 1e-9);
            TestAssert.Near(33, result.ClientClockOffset!.Value, 0);
            TestAssert.Equal(1, result.Marks.Count);
            var mark = result.Marks.Single();
            foreach (var group in new[] { "minion stall", "hero stall", "hero detour", "divergence", "invariant", "client assert/error" })
                TestAssert.True(mark.Worst.ContainsKey(group), group);
            TestAssert.Equal(2L, mark.ClientErrors);
            TestAssert.Equal(1L, mark.InvariantEvents);
            var text = TriageReport.Render(result);
            foreach (var section in new[] { "# Post-game triage", "## Marks", "## Game-wide metrics", "## Invariant summary", "## Client log", "## Look here first" })
                TestAssert.True(text.Contains(section, StringComparison.Ordinal));
            TestAssert.True(text.Contains("00:01.750", StringComparison.Ordinal));
            TestAssert.True(text.Contains("LeeSin / Order / 0x00000001", StringComparison.Ordinal));
            TestAssert.True(text.Contains("Shaco / Chaos / 0x00000002", StringComparison.Ordinal), "a pet named after its champion must not redact the champion");
            TestAssert.True(text.Contains("test-build", StringComparison.Ordinal));
            TestAssert.True(text.Contains("| Minion retained-route stall (s) | 1 | 1.5 | 1.5 | 1.5 | 1.5 |", StringComparison.Ordinal));
            foreach (var secret in new[] { "private-player-placeholder", "PrivateOwner", "192.0.2.1", "example.invalid", "NativeSensitiveName" })
                TestAssert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), "privacy: " + secret);
            TestAssert.True(text.Split('\n').Length <= 150);
            var output = Path.Combine(directory, "report.md");
            TestAssert.Equal(0, ToolsProgram.Main(["triage", directory, "--out", output, "--window", "2"]));
            TestAssert.Equal(text, File.ReadAllText(output));
            TestAssert.Throws<ArgumentException>(() => TriageAnalysis.Read(directory, double.NaN));
        }
        finally { Directory.Delete(directory, recursive: true); }
        AssertHoldsPairOnlyWithTheirOwnResume();
    }

    // A collision park or lane-merge hold is measured only up to its own matching resume.
    // The minion moving on, dying, or the capture ending leaves it unmeasured (counted as
    // a hold without a resume) instead of pairing it with a later unrelated resume.
    private static void AssertHoldsPairOnlyWithTheirOwnResume()
    {
        var directory = Directory.CreateTempSubdirectory("triage-holds-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "meta.json"), """
                {"session_id":"00000000-0000-0000-0000-000000000126","build":"test-build","t0_mono_ns":1000000000}
                """);
            using (var writer = new StreamWriter(Path.Combine(directory, "session.jsonl")))
            {
                // 21: plain park, resume 0.2 s later.
                Resync(100.0, "collision-park", 21);
                Resync(100.2, "collision-resume", 21);
                // 22: parked, then moves on by an unrelated route; a later park and resume
                // measure 0.3 s, not the 1.3 s back to the first park.
                Resync(101.0, "collision-park", 22);
                Row(101.5, "PKT", "->", new() { ["op"] = "0xC1", ["net_id"] = 22, ["sync_id"] = 1, ["waypoints"] = new[] { new[] { 0, 0 }, new[] { 50, 0 } } });
                Resync(102.0, "collision-park", 22);
                Resync(102.3, "collision-resume", 22);
                // 23: parked, dies; a resume after the death pairs with nothing.
                Resync(103.0, "collision-park", 23);
                Row(103.4, "PKT", "->", new() { ["op"] = "0xA6", ["dying_netid"] = 23 });
                Resync(104.0, "collision-resume", 23);
                // 24: parked until the capture ends.
                Resync(105.0, "collision-park", 24);
                // 25: a second park with no movement between keeps the original start.
                Resync(106.0, "collision-park", 25);
                Resync(106.1, "collision-park", 25);
                Resync(106.5, "collision-resume", 25);
                // 31: plain lane-merge hold, 0.4 s.
                Order(110.0, 31, "lane-merge-yield", 1, new[] { 300, 0 });
                Order(110.4, 31, "lane-merge-resume", 2, new[] { 300, 0 }, new[] { 400, 0 });
                // 32: yields, moves on by a chase, yields again; the resume measures 0.2 s.
                Order(111.0, 32, "lane-merge-yield", 1, new[] { 300, 0 });
                Order(111.3, 32, "target-chase", 2, new[] { 300, 0 }, new[] { 310, 0 });
                Order(112.0, 32, "lane-merge-yield", 3, new[] { 310, 0 });
                Order(112.2, 32, "lane-merge-resume", 4, new[] { 310, 0 }, new[] { 400, 0 });
                // 33: yields and dies; a resume after the death pairs with nothing.
                Order(113.0, 33, "lane-merge-yield", 1, new[] { 300, 0 });
                Row(113.5, "PKT", "->", new() { ["op"] = "0xA6", ["dying_netid"] = 33 });
                Order(114.0, 33, "lane-merge-resume", 2, new[] { 300, 0 }, new[] { 400, 0 });
                Row(115, "GAMEPLAY", "match.ended", new());

                void Row(double t, string cat, string msg, Dictionary<string, object?> fields)
                {
                    fields["t"] = t; fields["cat"] = cat; fields["msg"] = msg; fields["mono_ns"] = Mono(t);
                    writer.WriteLine(JsonSerializer.Serialize(fields));
                }
                void Resync(double t, string reason, uint id) =>
                    Row(t, "AI", "movement_event", new() { ["kind"] = "minion-resync", ["reason"] = reason, ["netid"] = id });
                void Order(double t, uint id, string reason, int sync, params int[][] points) => Row(t, "AI", "fidelity_batch", new()
                {
                    ["orders"] = new[] { new { netid = id, kind = "waypoint", reason, sync_id = sync, mono_ns = Mono(t), waypoints = points } },
                });
            }
            var result = TriageAnalysis.Read(directory);
            var parks = result.Metrics[TriageAnalysis.MetricNames[4]];
            TestAssert.Equal(3L, parks.Count, "only matching park/resume pairs are measured");
            TestAssert.Near(1.0, parks.Total, 1e-8);
            TestAssert.Near(0.3, parks.Median, 1e-8);
            TestAssert.Near(0.5, parks.P95, 1e-8);
            TestAssert.Near(0.5, parks.Maximum, 1e-8);
            TestAssert.Equal("25@106;22@102;21@100", string.Join(';', parks.Worst.Select(moment =>
                $"{moment.Unit}@{moment.Time.ToString("0.###", CultureInfo.InvariantCulture)}")), "park starts of the measured pairs");
            var merges = result.Metrics[TriageAnalysis.MetricNames[3]];
            TestAssert.Equal(2L, merges.Count, "only matching yield/resume pairs are measured");
            TestAssert.Near(0.6, merges.Total, 1e-8);
            TestAssert.Near(0.3, merges.Median, 1e-8);
            TestAssert.Near(0.4, merges.P95, 1e-8);
            TestAssert.Near(0.4, merges.Maximum, 1e-8);
            // Park 22 (moved on), 23 (died), 24 (open at the end); yield 32 (moved on), 33 (died).
            TestAssert.Equal(5L, result.CensoredHolds, "holds without a matching resume");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Check("tools.triage-streams-without-loading-whole-file")]
    public static void StreamsWithBoundedState()
    {
        var directory = Directory.CreateTempSubdirectory("triage-stream-").FullName;
        try
        {
            const int rows = 20000;
            var path = Path.Combine(directory, "session.jsonl");
            using (var writer = new StreamWriter(path))
            {
                for (var i = 0; i < rows; i++)
                {
                    var time = i / 10.0;
                    // Many retired ids plus repeated samples exercise both bounds.
                    writer.WriteLine(JsonSerializer.Serialize(new
                    {
                        t = time, cat = "AI", msg = "fidelity_batch",
                        orders = new[] { new { netid = (uint)(i / 100 + 1), kind = "client-report", waypoints = new[] { new[] { 0, 0 }, new[] { 30, 40 } } } },
                    }));
                }
            }
            var before = GC.GetTotalMemory(forceFullCollection: true);
            var result = TriageAnalysis.Read(directory);
            var retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - before;
            var metric = result.Metrics[TriageAnalysis.MetricNames[8]];
            TestAssert.Equal((long)rows, metric.Count);
            TestAssert.Equal(TriageAnalysis.Metric.SampleLimit, metric.RetainedSamples);
            TestAssert.True(metric.Approximate);
            TestAssert.Near(50, metric.Median, 0);
            TestAssert.Near(50, metric.P95, 0);
            TestAssert.True(result.PeakUnits <= 3, "retire entities instead of retaining every historical id");
            TestAssert.True(result.PeakPairsPerUnit <= 128);
            TestAssert.True(retainedBytes < 8 * 1024 * 1024, "the returned report retains summaries, not JSON rows");
            // The production reader itself must be lazy, not just free a loaded
            // file before returning. Its enumerator may consume one line only.
            var reader = typeof(Amberline.Tools.Telemetry.TelemetryAnalyzer).GetMethod("ReadLines",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var lines = (IEnumerable<(int Line, string Text)>)reader.Invoke(null, [path])!;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            using (var enumerator = lines.GetEnumerator())
            {
                TestAssert.True(enumerator.MoveNext());
                TestAssert.Equal(1, enumerator.Current.Line);
            }
            TestAssert.True(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 256 * 1024,
                "reading the first row must not allocate the multi-megabyte file");
            var text = TriageReport.Render(result);
            TestAssert.True(text.Contains("## Marks (+/-30 s)\n\nnone recorded", StringComparison.Ordinal));
            TestAssert.True(text.Contains("## Invariant summary\n\nnone recorded", StringComparison.Ordinal));
            TestAssert.True(text.Contains("server t=", StringComparison.Ordinal));
            File.AppendAllText(path, "{truncated");
            TestAssert.True(TriageAnalysis.Read(directory).IncompleteTail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Check("tools.triage-compare-deltas")]
    public static void CompareDeltas()
    {
        var a = Directory.CreateTempSubdirectory("triage-a-").FullName;
        var b = Directory.CreateTempSubdirectory("triage-b-").FullName;
        var previousOut = Console.Out;
        try
        {
            WriteCapture(a);
            WriteCapture(b, gap: 100);
            var expected = TriageReport.Compare(TriageAnalysis.Read(a), TriageAnalysis.Read(b));
            TestAssert.True(expected.Contains("| Hero client/server gap (units) | 1 / 50 / 50 / 50 / 50 | 1 / 100 / 100 / 100 / 100 | 0 / +50 / +50 / +50 / +50 |", StringComparison.Ordinal));
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            Console.SetOut(output);
            TestAssert.Equal(0, ToolsProgram.Main(["triage", "--compare", a, b]));
            TestAssert.Equal(expected, output.ToString());
            TestAssert.False(expected.Contains("private-player-placeholder", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(previousOut);
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    private static void AssertMetric(TriageAnalysis result, int index, long count, double value)
    {
        var metric = result.Metrics[TriageAnalysis.MetricNames[index]];
        TestAssert.Equal(count, metric.Count, TriageAnalysis.MetricNames[index]);
        TestAssert.Near(value, metric.Median, 1e-8, TriageAnalysis.MetricNames[index]);
        TestAssert.Near(value, metric.P95, 1e-8);
        TestAssert.Near(value, metric.Maximum, 1e-8);
    }

    private static void WriteCapture(string directory, double gap = 50)
    {
        File.WriteAllText(Path.Combine(directory, "meta.json"), """
            {"session_id":"00000000-0000-0000-0000-000000000126","build":"test-build","t0_mono_ns":1000000000}
            """);
        File.WriteAllText(Path.Combine(directory, "match-result.json"), """
            {"Players":[{"NetId":1,"Champion":"LeeSin","Team":"Order","Name":"private-player-placeholder"},{"NetId":2,"Champion":"Shaco","Team":"Chaos"}]}
            """);
        File.WriteAllText(Path.Combine(directory, "client_r3dlog.txt"), """
            000010.000| 1kb| 1kb added| ALWAYS| StartGame
            000011.750| 1kb| 1kb added| ASSERT| failed NativeSensitiveName at C:\Users\PrivateOwner\game.cpp
            000012.000| 2kb| 1kb added| ASSERT| failed NativeSensitiveName at C:\Users\PrivateOwner\game.cpp
            """);
        using var writer = new StreamWriter(Path.Combine(directory, "session.jsonl"));
        Write(43, "PKT", "->", new() { ["op"] = "0x5F" });
        Write(43.5, "PKT", "->", new() { ["op"] = "0x80", ["net_id"] = 10, ["team"] = 100 });
        Snapshot(44);
        Write(44.05, "AI", "movement_event", new() { ["kind"] = "pet-order", ["netid"] = 3, ["unit_name"] = "Shaco" });
        Write(44.1, "AI", "movement_event", new() { ["kind"] = "hero-order", ["netid"] = 1, ["order"] = 2, ["authority"] = new[] { 0, 20 } });
        Write(44.2, "PKT", "->", new() { ["op"] = "0xC1", ["net_id"] = 1, ["sync_id"] = 70, ["waypoints"] = new[] { new[] { 0, 20 }, new[] { 0, 120 }, new[] { 100, 120 }, new[] { 100, 20 } } });
        Write(44.3, "AI", "movement_event", new() { ["kind"] = "minion-resync", ["reason"] = "collision-park", ["netid"] = 10, ["attack_target"] = 20 });
        Write(44.4, "INVARIANT", "blocked-cell", new() { ["unit"] = 10, ["value"] = 1, ["limit"] = 0 });
        Write(44.402, "AI", "movement_event", new() { ["kind"] = "waypoint/1/enqueued", ["outcome"] = "enqueued", ["reason"] = "target-chase", ["netid"] = 10, ["sync_id"] = 77, ["attack_target"] = 20, ["observed_mono_ns"] = Mono(44.402) });
        Write(44.403, "AI", "movement_event", new() { ["kind"] = "waypoint/2/enqueued", ["outcome"] = "enqueued", ["reason"] = "target-chase", ["netid"] = 10, ["sync_id"] = 77, ["attack_target"] = 20, ["observed_mono_ns"] = Mono(44.403) });
        Snapshot(44.5, orders:
        [
            new { netid = 1, kind = "client-report", mono_ns = Mono(44.25), waypoints = new[] { new[] { 0.0, 20.0 }, new[] { gap * 0.6, 20 + gap * 0.8 } } },
            new { netid = 10, kind = "waypoint", reason = "target-chase", sync_id = 77, mono_ns = Mono(44.4), waypoints = new[] { new[] { 0, 0 }, new[] { 10, 0 } } },
            new { netid = 11, kind = "waypoint", reason = "lane-merge-yield", sync_id = 80, mono_ns = Mono(44.5), waypoints = new[] { new[] { 300, 0 } } },
        ]);
        Write(44.75, "PLAYTEST", "mark", new() { ["hero"] = 1, ["champion"] = "LeeSin", ["team"] = "Order", ["hud"] = 1.75,
            ["note"] = "wave private-player-placeholder 192.0.2.1 test@example.invalid C:\\Users\\PrivateOwner\\game" });
        Snapshot(45);
        Write(45.3, "AI", "movement_event", new() { ["kind"] = "minion-resync", ["reason"] = "collision-resume", ["netid"] = 10, ["attack_target"] = 20 });
        Write(45.4, "INVARIANT", "blocked-cell-end", new() { ["unit"] = 10, ["detail"] = new { duration = 1.0 } });
        Snapshot(45.5, orders: [new { netid = 11, kind = "waypoint", reason = "lane-merge-resume", sync_id = 81, mono_ns = Mono(45.5), waypoints = new[] { new[] { 300, 0 }, new[] { 400, 0 } } }]);
        Write(45.55, "PKT", "->", new() { ["op"] = "0x0D", ["attacker_netid"] = 10 });
        Write(45.6, "PKT", "->", new() { ["op"] = "0xA6", ["dying_netid"] = 20 });
        Write(45.6, "PKT", "->", new() { ["op"] = "0xA6", ["dying_netid"] = 20 });
        Write(45.85, "PKT", "->", new() { ["op"] = "0xC1", ["net_id"] = 10, ["sync_id"] = 90, ["waypoints"] = new[] { new[] { 0, 0 }, new[] { 100, 0 } } });
        Snapshot(46, moved: true);
        Write(47, "GAMEPLAY", "match.ended", new());
        Snapshot(100, moved: true);

        void Write(double t, string cat, string msg, Dictionary<string, object?> fields)
        {
            fields["t"] = t; fields["cat"] = cat; fields["msg"] = msg; fields["mono_ns"] = Mono(t);
            writer.WriteLine(JsonSerializer.Serialize(fields));
        }
        void Snapshot(double t, bool moved = false, object[]? orders = null) => Write(t, "AI", "fidelity_batch", new()
        {
            ["orders"] = orders ?? [],
            ["minions"] = new object[]
            {
                new { netid = 10, pos = new[] { moved ? 100 : 0, 0 }, alive = true, attack_target = 20, waypoints = new[] { new[] { 1000, 0 } }, collision = new { collider_ids = new[] { 2 } } },
                new { netid = 11, pos = new[] { moved ? 400 : 300, 0 }, alive = true, waypoints = Array.Empty<int[]>() },
            },
            ["heroes"] = new[] { new { net_id = 1, position = new[] { moved ? 100 : 0, 20 }, waypoints = new[] { new[] { 1000, 20 } } } },
        });
    }
    private static long Mono(double t) => (long)Math.Round((t + 1) * 1e9);
}
