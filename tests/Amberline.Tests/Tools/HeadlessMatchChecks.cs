using System.Text.Json;
using Amberline.Core;
using Amberline.Tools.Diagnostics;
using ToolsProgram = Amberline.Tools.Program;

namespace Amberline.Tests;

/// <summary>Batch 18: the headless seeded ten-bot match command and its triage additions.</summary>
public static class HeadlessMatchChecks
{
    // Each check plays real matches on the virtual clock into Guid-named temp folders. They are
    // serial: a match is a CPU-bound second-scale process and the wall-clock deadline must not race workers.

    /// <summary>
    /// Batch 452: a map 8 point turret's replicated 0xCC must never carry a syncID the peer already passed (the checker's sync-id-regression, which the
    /// stock client drops). The spawn tick's pending block (sync 1) used to reach the peers after the refresh's 7 to 9: 50 regressions in the first
    /// half minute. The flush now sends a shrine-named point turret's pending blocks before its refresh (ReplicationDelivery.Flush).
    /// </summary>
    [Check("tools.match.dominion-point-turret-sync-ids-do-not-regress", Serial = true)]
    public static void DominionPointTurretSyncIdsDoNotRegress()
    {
        var root = TempRoot();
        try
        {
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(9801, Path.Combine(root, "map8"), 0.5) { MapId = 8 });
            var regressions = report.PacketTotals is { } totals && totals.TryGetValue(PacketRuleKinds.SyncIdRegression, out var count) ? count : 0;
            TestAssert.True(regressions == 0 && report.PacketViolations == 0,
                $"no sync-id-regression on the map 8 point turrets (regressions {regressions}, violations {report.PacketViolations})");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Batch 457, the owner's native Dominion game (client_r3dlog.txt, 28 October 2026): the client binds a CreateTurret (0xA5) only to a turret of
    /// its own scene, and asserts <c>turret</c> for the ten capture-point turrets, which map 8's
    /// scene does not have; every later packet about them reaches nothing. The point turrets are now the client's minions of their character (0x80),
    /// so the checker's unbound-turret rule (a 0xA5 naming no scene turret) and every reference to them stay at zero.
    /// </summary>
    [Check("tools.match.dominion-turrets-are-created-by-names-the-client-binds", Serial = true)]
    public static void DominionTurretsAreCreatedByNamesTheClientBinds()
    {
        var root = TempRoot();
        try
        {
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(9801, Path.Combine(root, "map8"), 0.5) { MapId = 8 });
            var totals = report.PacketTotals ?? new Dictionary<string, long>();
            var unbound = totals.GetValueOrDefault(PacketRuleKinds.UnboundTurret);
            var unknown = totals.GetValueOrDefault(PacketRuleKinds.UnknownUnit);
            TestAssert.True(unbound == 0 && report.PacketViolations == 0,
                $"every map 8 CreateTurret names a scene turret (unbound-turret {unbound}, unknown-unit {unknown}, violations {report.PacketViolations})");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Batch 464, the native Dominion runs (8 and 16 x the replication manager accessor, on the first game
    /// frame): a minion the client creates from a ServerOnly character gets no replication manager (its creation handler), so no peer may receive a
    /// 0xCC naming it. ServerOnly is read here from the shipped inibin as the client's loader reads it: <c>[Data] ServerOnly</c>, defaulting
    /// to <c>NeverRender</c>. Map 8 spawns three OdinSpeedShrines at the start (batch 506).
    /// </summary>
    [Check("tools.match.dominion-server-only-units-get-no-replication", Serial = true)]
    public static void DominionServerOnlyUnitsGetNoReplication()
    {
        var root = TempRoot();
        var serverOnly = new Dictionary<string, bool>(StringComparer.Ordinal);
        var unmanaged = new Dictionary<int, HashSet<uint>>();
        var blocks = new Dictionary<int, int>();
        void Sink(int peer, Amberline.Network.Protocol.PlaintextPacket packet)
        {
            var bytes = packet.Plaintext.Span;
            if (!unmanaged.TryGetValue(peer, out var units)) unmanaged[peer] = units = [];
            // Batch 506 gave the speed shrines their minimap icon, so they go out through the neutral creation (0x66, the skin at byte 0x6E,
            // ProductionMatchComposition.BuildCreation; batch 511: the camp's GroupNumber). Both creations make an obj_AI_Minion from that skin
            // (the 0x80 and 0x66 creation cases; ClientReplicationStore.MinionSkinOffset). This check read 0x80 only.
            var skinAt = bytes.Length == 0 ? 0 : bytes[0] switch { 0x80 => 0x67, 0x66 => 0x6E, _ => 0 };
            if (skinAt > 0 && bytes.Length >= skinAt + 64)
            {
                var skin = System.Text.Encoding.ASCII.GetString(bytes.Slice(skinAt, 64)).Split('\0')[0];
                var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
                if (!serverOnly.TryGetValue(skin, out var flag)) serverOnly[skin] = flag = IsServerOnly(skin);
                if (flag) units.Add(id); else units.Remove(id);
            }
            else if (bytes.Length >= 15 && bytes[0] == 0xCC &&
                     units.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..])))
                blocks[peer] = blocks.GetValueOrDefault(peer) + 1;
        }
        try
        {
            HeadlessMatch.Run(new HeadlessMatchOptions(9801, Path.Combine(root, "map8"), 0.5) { MapId = 8, PacketSink = Sink });
            TestAssert.True(unmanaged.Count > 0 && unmanaged.Values.All(units => units.Count >= 2),
                $"every peer created the ServerOnly speed shrines ({string.Join(", ", unmanaged.Select(pair => $"{pair.Key}:{pair.Value.Count}"))})");
            TestAssert.Equal(0, blocks.Values.Sum(),
                $"0xCC blocks naming a ServerOnly unit, per peer: {string.Join(", ", blocks.Select(pair => $"{pair.Key}:{pair.Value}"))}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool IsServerOnly(string character)
    {
        var path = $"Characters/{character}/{character}.inibin";
        if (!File.Exists(Amberline.Data.GameDataPaths.Default.Data(path))) return false;
        var data = Amberline.Data.InibinReader.LoadData(path);
        double Value(string key, double fallback) =>
            data.TryGetValue(Amberline.Data.InibinReader.KeyHash(key), out var value) ? value.AsClientFloat() : fallback;
        return Value("Data*ServerOnly", Value("Data*NeverRender", 0.0)) != 0.0;
    }

    [Check("tools.match.same-seed-identical-output", Serial = true)]
    public static void SameSeedWritesIdenticalFiles()
    {
        var root = TempRoot();
        try
        {
            var first = HeadlessMatch.Run(new HeadlessMatchOptions(20261015, Path.Combine(root, "a"), 4.0));
            var second = HeadlessMatch.Run(new HeadlessMatchOptions(20261015, Path.Combine(root, "b"), 4.0));
            TestAssert.Equal("cap", first.Outcome, "a four-minute cap ends before any Nexus can fall");
            // session.jsonl and match-result.json carry no wall-clock field, so they must match byte for byte.
            foreach (var file in new[] { "session.jsonl", "match-result.json" })
                TestAssert.True(File.ReadAllBytes(Path.Combine(root, "a", file)).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(root, "b", file))), $"{file} is identical for one seed");
            TestAssert.Equal(first.PacketsChecked, second.PacketsChecked, "the same packets reach the same peers");
            TestAssert.True(first.PacketsChecked > 10_000, "all ten peers received a real stream");
            // meta.json differs only in its wall_clock object.
            JsonElement Meta(string name)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, name, "meta.json")));
                var copy = document.RootElement.Clone();
                return copy;
            }
            string Stable(JsonElement meta) => string.Join(";", meta.EnumerateObject()
                .Where(property => property.Name != "wall_clock").Select(property => property.Name + "=" + property.Value.GetRawText()));
            TestAssert.Equal(Stable(Meta("a")), Stable(Meta("b")), "meta.json is identical outside wall_clock");
            TestAssert.Equal(20261015, Meta("a").GetProperty("seed").GetInt32(), "meta.json names the seed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.seed-reaches-every-roll", Serial = true)]
    public static void DifferentSeedsDiverge()
    {
        var root = TempRoot();
        try
        {
            // Five game minutes: the first wave fights have happened, and the seeds' first different roll is behind us
            // (at three minutes seeds 1 and 2 still play the same match; batch 20 bounded the hero route planning,
            // which moved the first different roll from before four minutes to after it).
            var one = HeadlessMatch.Run(new HeadlessMatchOptions(1, Path.Combine(root, "one"), 5.0));
            var two = HeadlessMatch.Run(new HeadlessMatchOptions(2, Path.Combine(root, "two"), 5.0));
            TestAssert.True(one.Teams["Order"] != two.Teams["Order"] || one.Teams["Chaos"] != two.Teams["Chaos"] ||
                one.PacketsChecked != two.PacketsChecked,
                "two seeds play two different matches (team activity or packet count differs)");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.ten-bots-both-teams-act", Serial = true)]
    public static void BothTeamsActInATenBotMatch()
    {
        var root = TempRoot();
        try
        {
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(1, Path.Combine(root, "m"), 4.0));
            foreach (var team in new[] { "Order", "Chaos" })
            {
                var activity = report.Teams[team];
                TestAssert.True(activity.DamageDealt > 0.0, $"{team} bots dealt damage");
                TestAssert.True(activity.SpellCasts > 0 || activity.LaneMinionsKilled > 0 || activity.Kills > 0,
                    $"{team} bots cast, killed minions or killed heroes");
                TestAssert.True(activity.GoldEarned > 0.0, $"{team} bots earned gold");
                TestAssert.True(activity.LevelSum > 5, $"{team} bots gained levels");
            }
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "m", "meta.json")));
            TestAssert.Equal(10, meta.RootElement.GetProperty("roster").GetArrayLength(), "five bots per side");
            TestAssert.Equal(0L, report.LifecycleViolations, "the lifecycle monitor stays quiet");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.cap-exit-code-and-outputs", Serial = true)]
    public static void CapReachedExitsTwoAndTriageReadsOutput()
    {
        var root = TempRoot();
        try
        {
            var directory = Path.Combine(root, "cap");
            TestAssert.Equal(2, HeadlessMatch.RunCli(["--seed", "11", "--minutes", "0.5", "--out", directory]),
                "reaching the minute cap exits 2");
            foreach (var file in new[] { "session.jsonl", "meta.json", "match-result.json" })
                TestAssert.True(File.Exists(Path.Combine(directory, file)), file + " is written");
            using (var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "match-result.json"))))
            {
                TestAssert.Equal("cap", result.RootElement.GetProperty("outcome").GetString());
                TestAssert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("winner").ValueKind, "no winner at the cap");
                TestAssert.Equal(11, result.RootElement.GetProperty("seed").GetInt32());
            }
            var report = Path.Combine(root, "triage.md");
            TestAssert.Equal(0, ToolsProgram.Main(["triage", directory, "--out", report]), "triage reads a headless match unchanged");
            var text = File.ReadAllText(report);
            TestAssert.True(text.Contains("seed: `11`", StringComparison.Ordinal), "the triage header echoes the seed");
            TestAssert.True(text.Contains("## Packet-rule summary", StringComparison.Ordinal));
            TestAssert.True(text.Contains("Packet checker ran over ", StringComparison.Ordinal));
            TestAssert.True(text.Contains("0x5F StartGame", StringComparison.Ordinal), "HUD time is anchored on StartGame");

            TestAssert.Equal(0, new HeadlessMatchReport(1, "natural", "Order", 1, 1, 0, 0, 0, 0, 0, 0, "", new Dictionary<string, TeamActivity>()).ExitCode);
            TestAssert.Equal(2, new HeadlessMatchReport(1, "cap", null, 1, 1, 0, 0, 0, 0, 0, 0, "", new Dictionary<string, TeamActivity>()).ExitCode);
            TestAssert.Equal(3, new HeadlessMatchReport(1, "crash", null, 1, 1, 0, 0, 0, 0, 0, 0, "", new Dictionary<string, TeamActivity>()).ExitCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.tick-stats-count-budget-overruns")]
    public static void TickStatsCountBudgetOverruns()
    {
        // Batch 20: the headless runner reports the wall time of every simulation step, the number a live
        // server must keep under its 33.3 ms budget (a longer step stops ENet servicing for its duration).
        var empty = TickStats.From([]);
        TestAssert.Equal(0, empty.Steps);
        var stats = TickStats.From(
        [
            (1.0, 2.0, 0L, 0L, 0.0), (2.0, 10.0, 4L, 800L, 0.0), (3.0, 40.0, 30L, 9_000L, 1.5),
            (4.0, 250.0, 120L, 70_000L, 0.0), (5.0, 3.0, 1L, 100L, 0.0),
        ]);
        TestAssert.Equal(5, stats.Steps);
        TestAssert.Equal(2, stats.Over33Ms, "40 ms and 250 ms exceed the 33.3 ms step budget");
        TestAssert.Equal(1, stats.Over100Ms, "only 250 ms exceeds 100 ms");
        TestAssert.Near(250.0, stats.MaxMs, 1e-9);
        TestAssert.Near(4.0, stats.WorstAt, 1e-9, "the worst step is reported by game time");
        TestAssert.Equal(120L, stats.MaxPlannerCalls);
        TestAssert.Equal(155L, stats.TotalPlannerCalls);
        TestAssert.Equal(250.0, stats.Worst![0].Ms, "the worst-step list starts with the slowest step");
        TestAssert.True(stats.ToString().Contains("over100ms=1", StringComparison.Ordinal), "the console line carries the counts");
    }

    [Check("tools.match.natural-end-exits-zero", Serial = true)]
    public static void NexusDeathIsANaturalEnd()
    {
        var root = TempRoot();
        try
        {
            var directory = Path.Combine(root, "natural");
            var killed = false;
            // A real Nexus death through combat (the production end-of-game path), 20 game seconds in.
            var options = new HeadlessMatchOptions(3, directory, 5.0)
            {
                OnStep = runtime =>
                {
                    if (killed || !runtime.Session.Started || runtime.Combat.CurrentTime < 20.0) return;
                    killed = true;
                    var nexus = runtime.World.Entities.Values.Single(entity => entity.Kind == EntityKind.Nexus && entity.Team == Team.Chaos);
                    TestAssert.True(runtime.Combat.Kill(nexus, runtime.Session.Participants[0].Hero!.NetId), "the Nexus dies");
                },
            };
            var report = HeadlessMatch.Run(options);
            TestAssert.Equal("natural", report.Outcome);
            TestAssert.Equal(0, report.ExitCode, "a natural end exits 0");
            TestAssert.Equal("Order", report.Winner, "destroying the Chaos Nexus wins for Order");
            TestAssert.True(report.DurationSeconds is > 19.0 and < 60.0, "the match ended long before the cap: " + report.DurationSeconds);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "match-result.json")));
            TestAssert.Equal("Order", result.RootElement.GetProperty("result").GetProperty("Winner").GetString(), "the live result record is kept");
            TestAssert.Equal(10, result.RootElement.GetProperty("result").GetProperty("Players").GetArrayLength());
            var log = File.ReadAllLines(Path.Combine(directory, "session.jsonl"));
            TestAssert.True(log.Count(line => line.Contains("\"msg\":\"match.ended\"", StringComparison.Ordinal)) == 1, "one match.ended record");
            TestAssert.True(log.Any(line => line.Contains("\"op\":\"0x5F\"", StringComparison.Ordinal)), "StartGame is traced for triage");
            var triage = Path.Combine(root, "triage.md");
            TestAssert.Equal(0, ToolsProgram.Main(["triage", directory, "--out", triage]));
            TestAssert.False(File.ReadAllText(triage).Contains("match end missing", StringComparison.Ordinal), "triage sees the match end");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.crash-exits-three-and-logs-the-exception", Serial = true)]
    public static void CrashIsLoggedAndExitsThree()
    {
        var root = TempRoot();
        try
        {
            var directory = Path.Combine(root, "crash");
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(4, directory, 5.0)
            {
                OnStep = runtime =>
                {
                    if (runtime.Session.Started && runtime.Combat.CurrentTime >= 2.0)
                        throw new InvalidOperationException("deliberate test crash");
                },
            });
            TestAssert.Equal("crash", report.Outcome);
            TestAssert.Equal(3, report.ExitCode);
            var crash = File.ReadAllLines(Path.Combine(directory, "session.jsonl")).Select(line => JsonDocument.Parse(line).RootElement)
                .Single(row => row.GetProperty("msg").GetString() == "match.crash");
            TestAssert.Equal("ERROR", crash.GetProperty("lvl").GetString());
            TestAssert.Equal("System.InvalidOperationException", crash.GetProperty("exc").GetString());
            TestAssert.True(crash.GetProperty("tb").GetString()!.Contains("deliberate test crash", StringComparison.Ordinal));
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "meta.json")));
            TestAssert.Equal("crash", meta.RootElement.GetProperty("outcome").GetString());
            TestAssert.True(File.Exists(Path.Combine(directory, "match-result.json")), "a crashed match still writes its result file");
            TestAssert.Equal(0, ToolsProgram.Main(["triage", directory, "--out", Path.Combine(root, "triage.md")]), "triage reads a crashed match");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.usage-errors-exit-one", Serial = true)]
    public static void BadArgumentsAreUsageErrors()
    {
        TestAssert.Equal(1, HeadlessMatch.RunCli([]), "a seed is required");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "x"]));
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "1", "--speed", "fast"]), "only the virtual clock exists");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "1", "--roster", "Annie", "Orianna"]), "ten champions are required");
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "1", "--minutes", "0"]));
        TestAssert.Equal(1, HeadlessMatch.RunCli(["--seed", "1", "--roster",
            "Annie", "Annie", "LeeSin", "Nidalee", "Yorick", "Shaco", "Ezreal", "Vayne", "Janna", "Blitzcrank"]),
            "a team cannot repeat a champion");
        var roster = HeadlessMatch.Parse(["--seed", "5", "--roster", "annie", "orianna", "leesin", "nidalee", "yorick",
            "shaco", "ezreal", "vayne", "janna", "blitzcrank", "--minutes", "1.5"]);
        TestAssert.Equal(5, roster.Seed);
        TestAssert.Equal(1.5, roster.Minutes);
        TestAssert.Equal(10, HeadlessMatch.ResolveRoster(roster.Roster).Length);
        TestAssert.Equal("LeeSin", HeadlessMatch.ResolveRoster(roster.Roster)[2], "champion names are canonicalised");
    }

    [Check("tools.match.packet-lines-follow-the-shared-schema", Serial = true)]
    public static void PacketViolationsBecomeSchemaLines()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            using (var log = new HeadlessMatch.MatchLog(root, 9))
            {
                HeadlessMatch.WritePacketViolations(log, [
                    new PacketRuleViolation("stats-zeroed", 12.5, 3, 0xCC, 1073741825u, "mHP went to 0"),
                    new PacketRuleViolation("unknown-unit", 13.25, 0, 0x7F, null, "never created")]);
                // One kind over the per-kind cap keeps the log bounded and says what it dropped.
                HeadlessMatch.WritePacketViolations(log, Enumerable.Range(0, 1002)
                    .Select(index => new PacketRuleViolation("flood", 20 + index, 1, 0x80, 5u, "x")).ToArray());
                log.Flush();
            }
            var lines = File.ReadAllLines(Path.Combine(root, "session.jsonl")).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            var first = lines[0];
            TestAssert.Equal("PACKET", first.GetProperty("cat").GetString());
            TestAssert.Equal("WARN", first.GetProperty("lvl").GetString());
            TestAssert.Equal("stats-zeroed", first.GetProperty("msg").GetString());
            TestAssert.Near(12.5, first.GetProperty("t").GetDouble(), 0, "t is the violation's own time");
            TestAssert.Equal(3, first.GetProperty("peer").GetInt32());
            TestAssert.Equal("0xCC", first.GetProperty("opcode").GetString());
            TestAssert.Equal(1073741825u, first.GetProperty("netid").GetUInt32());
            TestAssert.Equal("mHP went to 0", first.GetProperty("detail").GetString());
            TestAssert.Equal(JsonValueKind.Null, lines[1].GetProperty("netid").ValueKind, "a violation without a unit has netid null");
            TestAssert.Equal(1000, lines.Count(line => line.GetProperty("msg").GetString() == "flood"), "lines per kind are capped");
            var dropped = lines.Single(line => line.GetProperty("msg").GetString() == "packet.suppressed");
            TestAssert.Equal("flood", dropped.GetProperty("kind").GetString());
            TestAssert.Equal(2, dropped.GetProperty("count").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("tools.match.triage-packet-section-and-seed", Serial = true)]
    public static void TriageSummarisesPacketLinesAndEchoesTheSeed()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "meta.json"),
                "{\"session_id\":\"00000000-0000-0000-0000-00000000002a\",\"build\":\"test-build\",\"t0_mono_ns\":0,\"seed\":42,\"packets_checked\":1234}");
            var rows = new List<string>
            {
                "{\"t\":10,\"cat\":\"PKT\",\"lvl\":\"DEBUG\",\"msg\":\"->\",\"dir\":\"->\",\"op\":\"0x5F\"}",
            };
            // Seven lines of one kind written out of time order: the five earliest are kept.
            foreach (var time in new[] { 50, 40, 12.5, 30, 20, 11, 60 })
                rows.Add($"{{\"t\":{time.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"cat\":\"PACKET\",\"lvl\":\"WARN\",\"msg\":\"stats-zeroed\",\"peer\":3,\"opcode\":\"0xCC\",\"netid\":1073741825,\"detail\":\"a|b\"}}");
            rows.Add("{\"t\":70,\"cat\":\"PACKET\",\"lvl\":\"WARN\",\"msg\":\"unknown-unit\",\"peer\":0,\"opcode\":\"0x7F\",\"netid\":null,\"detail\":\"\"}");
            rows.Add("{\"t\":99,\"cat\":\"PACKET\",\"lvl\":\"INFO\",\"msg\":\"packet.suppressed\",\"kind\":\"stats-zeroed\",\"count\":4}");
            rows.Add("{\"t\":100,\"cat\":\"GAMEPLAY\",\"lvl\":\"INFO\",\"msg\":\"match.ended\",\"result\":{\"Players\":[]}}");
            // A PACKET line written after the match end (the checker's end-of-match pass) is still counted.
            rows.Add("{\"t\":150,\"cat\":\"PACKET\",\"lvl\":\"WARN\",\"msg\":\"late-kind\",\"peer\":1,\"opcode\":\"0x01\",\"netid\":null,\"detail\":\"end\"}");
            File.WriteAllLines(Path.Combine(root, "session.jsonl"), rows);

            var analysis = TriageAnalysis.Read(root);
            TestAssert.Equal(42L, analysis.Seed);
            TestAssert.Equal(1234L, analysis.PacketsChecked);
            TestAssert.Equal(11L, analysis.PacketViolations["stats-zeroed"].Count, "7 lines + 4 suppressed");
            TestAssert.Equal(1L, analysis.PacketViolations["unknown-unit"].Count);
            TestAssert.Equal(1L, analysis.PacketViolations["late-kind"].Count, "checker lines after the end time count");
            TestAssert.True(analysis.PacketViolations["stats-zeroed"].First.Select(item => item.Time)
                .SequenceEqual(new[] { 11.0, 12.5, 20.0, 30.0, 40.0 }), "the earliest five, in time order");

            var text = TriageReport.Render(analysis);
            TestAssert.True(text.Contains("seed: `42`", StringComparison.Ordinal), "header echoes the seed");
            TestAssert.True(text.Contains("## Packet-rule summary", StringComparison.Ordinal));
            TestAssert.True(text.Contains("| stats-zeroed | 11 |", StringComparison.Ordinal), "counts by kind");
            TestAssert.True(text.Contains("stats-zeroed at 00:01.000: peer 3, 0xCC, 0x40000001 - a\\|b", StringComparison.Ordinal),
                "first violations carry HUD time, peer, opcode, netid and escaped detail");
            TestAssert.False(text.Contains("stats-zeroed at 00:50.000", StringComparison.Ordinal), "only five per kind are listed");

            // A log without checker data says so instead of claiming a clean packet stream.
            var plain = Path.Combine(root, "plain");
            Directory.CreateDirectory(plain);
            File.WriteAllText(Path.Combine(plain, "session.jsonl"), "{\"t\":1,\"cat\":\"SESSION\",\"lvl\":\"INFO\",\"msg\":\"x\"}\n");
            var plainText = TriageReport.Render(TriageAnalysis.Read(plain));
            TestAssert.True(plainText.Contains("No packet-checker coverage recorded", StringComparison.Ordinal));
            TestAssert.False(plainText.Contains("; seed:", StringComparison.Ordinal), "no seed in the header when none is logged");

            // A live game logs its seed in a record property instead of meta.json.
            var live = Path.Combine(root, "live");
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "session.jsonl"),
                "{\"t\":1,\"cat\":\"GAMEPLAY\",\"lvl\":\"INFO\",\"msg\":\"match.started\",\"rng_seed\":987654}\n");
            TestAssert.Equal(987654L, TriageAnalysis.Read(live).Seed, "a live log's rng_seed record is the seed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "amberline-match-" + Guid.NewGuid().ToString("N"));
}
