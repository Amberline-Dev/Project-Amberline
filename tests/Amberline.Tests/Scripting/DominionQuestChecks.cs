using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>
/// Batch 471: Dominion's capture point quests in a two-seat map 8 match built by the production composition, each seat with its own
/// observer. The client's quest packet is HandleQuestUpdate (0x90, 396 bytes, read by its quest handler).
/// The layout: objective char[128] at byte 5, tooltip at byte 0x85, reward at byte 0x105, quest type at byte 0x185 (0 PrimaryQuests, 1
/// SecondaryQuests), command at byte 0x186 (0 add, 1 complete, 2 hide), flag at byte 0x187 (add: HandleRollovers; complete: 1 completed, 0
/// failed), quest id at byte 0x188. The rules are the League wiki's (Dominion, Quest page): a set to both teams after 5:00 (capture an enemy-held point,
/// defend one you hold); completing it saps 20 enemy nexus health and gives the living team Mark of the Conqueror; the next set comes
/// 5 minutes after the completion. The shipped pieces are the quest encounters and their scripts (OdinQuestIcon, OdinQuestIcon_Chaos,
/// OdinQuestBuffGiver), the tracker keys (fontconfig game_quest_capture_a..e, game_quest_defend_a..e) and the announcement
/// OnVictoryPointThreshold4 (event 98, "New Capture Point Objective!", the announcer's Quest_QuestReceived).
/// </summary>
public static class DominionQuestChecks
{
    private const double Step = 1.0 / 30.0;
    private const byte QuestOpcode = 0x90;
    private const byte QuestEvent = 98;

    [Check("dominion.quests-go-to-each-team-at-five-minutes", Serial = true, TimeoutSeconds = 900)]
    public static void QuestsGoToEachTeamAtFiveMinutes() => WithMatch((runtime, quests, captures, failures) =>
    {
        Hold(runtime, quests, 1, Team.Order);
        Hold(runtime, quests, 5, Team.Chaos);
        RunTo(runtime, 299.0);
        TestAssert.True(captures.All(capture => capture.Quests.Count == 0 && capture.Events.Count == 0), "no quest goes out before 5:00");
        RunTo(runtime, 300.5);

        var order = Issued(runtime, quests, captures[0], Team.Order);
        var chaos = Issued(runtime, quests, captures[1], Team.Chaos);
        TestAssert.True(order.Target == chaos.Defend && chaos.Target == order.Defend,
            $"each team defends the point the other must capture (Order {order}, Chaos {chaos})");
        // The quest point shows: OdinQuestIndicator (CharScriptOdinQuestIndicator's add to the guardian within 300) on both targets.
        foreach (var point in new[] { order.Target, chaos.Target })
            TestAssert.True(Guardian(runtime, quests, point).Buffs.Any(buff => buff.Name == "OdinQuestIndicator"),
                $"point {Letter(point)}'s guardian holds OdinQuestIndicator");
        TestAssert.True(failures.Records.Count == 0, "no quest script raised: " + string.Join("; ", failures.Records));
    });

    [Check("dominion.quest-capture-completes-rewards-and-renews", Serial = true, TimeoutSeconds = 1500)]
    public static void QuestCaptureCompletesRewardsAndRenews() => WithMatch((runtime, quests, captures, failures) =>
    {
        Hold(runtime, quests, 1, Team.Order);
        Hold(runtime, quests, 5, Team.Chaos);
        RunTo(runtime, 300.5);
        var order = Issued(runtime, quests, captures[0], Team.Order);
        var chaos = Issued(runtime, quests, captures[1], Team.Chaos);
        var chaosNexus = runtime.Combat.DominionNexusHealth(Team.Chaos);

        // DIAGNOSTICS: test seam. Order takes its target through the shipped capture tree: neutral with CaptureProgress past 99 is the tree's
        // "Order Capture" branch (OdinScriptBehaviorTrees.xml:1261-1280).
        quests.Captures[order.Target - 1].SetVariable("CurrentPointOwner", Team.Neutral);
        quests.Captures[order.Target - 1].SetVariable("CaptureProgress", 100.0);
        var done = runtime.Combat.CurrentTime;
        for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);
        TestAssert.True(quests.CapturePointOwners[order.Target] == Team.Order, $"Order captured point {Letter(order.Target)}");

        Completed(captures[0], order, success: true, "Order");
        Completed(captures[1], chaos, success: false, "Chaos");
        var orderHero = Hero(runtime, Team.Order);
        var mark = orderHero.Buffs.FirstOrDefault(buff => buff.Name == "OdinQuestBuff");
        TestAssert.True(mark is not null, "the Order champion holds Mark of the Conqueror (OdinQuestBuff, CharScriptOdinQuestBuff)");
        // A script buff's ExpiresAt holds its remaining time (BbBuffRuntime); the capture landed within the last three ticks.
        TestAssert.True(mark!.ExpiresAt is > 49.85 and <= 50.0,
            $"the mark lasts the script's 50 s (NewDuration 50, no MonsterBuffs mastery), {mark.ExpiresAt:F2} s left");
        TestAssert.True(!Hero(runtime, Team.Chaos).Buffs.Any(buff => buff.Name == "OdinQuestBuff"), "the Chaos champion has no mark");
        var sapped = chaosNexus - runtime.Combat.DominionNexusHealth(Team.Chaos);
        // Batch 505: the sap is 20, the capture costs Chaos 2 (BehaviorLeaves.CaptureNexusDamage), and the three ticks span 0.1 s, shorter than
        // the 2 s drain tick, so at most one application of the drain falls in them; the largest one is 5 (DominionScore.DrainAmount, 5-0-0).
        TestAssert.True(sapped >= 22.0 && sapped <= 27.0,
            $"the Chaos nexus loses 20, 2 for the capture and at most one drain application, lost {sapped:F2}");
        foreach (var point in new[] { order.Target, chaos.Target })
            TestAssert.True(!Guardian(runtime, quests, point).Buffs.Any(buff => buff.Name == "OdinQuestIndicator"),
                $"point {Letter(point)}'s quest indicator is gone (OdinQuestParticleRemover on the indicator's death)");
        TestAssert.True(Guardian(runtime, quests, order.Target).Buffs.Any(buff => buff.Name == "OdinQuestBuffParticle"),
            "the captured point's guardian shows the quest complete particle (OdinQuestBuffParticle)");

        foreach (var capture in captures) capture.Clear();
        // Chaos lost its only point: a set needs a point each team holds, so Chaos takes the Drill back for the next one.
        Hold(runtime, quests, 4, Team.Chaos);
        RunTo(runtime, done + 299.0);
        TestAssert.True(captures.All(capture => capture.Quests.Count == 0), "no new quest in the 5 minutes after the completion");
        RunTo(runtime, done + 300.5);
        TestAssert.True(captures.All(capture => capture.Quests.Count(Add) == 2), "a new set goes to each team 5 minutes after the completion");
        TestAssert.True(failures.Records.Count == 0, "no quest script raised: " + string.Join("; ", failures.Records));
    });

    /// <summary>
    /// Batch 570: the quest target keeps its mark while its point changes hands. Order's target (Chaos's point E) is neutralised and then
    /// captured through the shipped capture tree; batch 673: each change of hands changes the guardian's team in place
    /// (BehaviorTreeQuestHost.GuardianTakesOwner), and CharScriptOdinQuestIndicator marks the guardian only once (CharOnActivate). The neutral
    /// guardian keeps OdinQuestIndicator and each client's odin_point_active.troy on it (OnBuffActivate: one per team, SpecificTeamOnly). The capture ends the
    /// quest: the markers die and OdinQuestParticleRemover clears the mark from the point's guardian, and every mark particle a client got is killed.
    /// The 0x8C carries the name hash at 6, the effect at 25 and the bound unit at 29; the 0x3D the effect at 5.
    /// </summary>
    [Check("dominion.quest-target-keeps-its-mark-through-the-neutralise-until-the-quest-ends", Serial = true, TimeoutSeconds = 1500)]
    public static void QuestTargetKeepsItsMarkThroughTheNeutraliseUntilTheQuestEnds() => WithMatch((runtime, quests, captures, failures) =>
    {
        Hold(runtime, quests, 1, Team.Order);
        Hold(runtime, quests, 5, Team.Chaos);
        RunTo(runtime, 300.5);
        var order = Issued(runtime, quests, captures[0], Team.Order);
        Issued(runtime, quests, captures[1], Team.Chaos);
        var marked = Guardian(runtime, quests, order.Target);
        TestAssert.True(marked.Buffs.Any(buff => buff.Name == Indicator), $"point {Letter(order.Target)}'s guardian holds the quest mark");

        // DIAGNOSTICS: test seam. A Chaos point past 0 is the tree's "Chaos Neutralization" (OdinScriptBehaviorTrees.xml:1233-1255).
        quests.Captures[order.Target - 1].SetVariable("CaptureProgress", 50.0);
        for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);
        TestAssert.True(quests.CapturePointOwners[order.Target] == Team.Neutral, $"point {Letter(order.Target)} is neutralised");
        // Batch 673 (restated): the guardian changes team in place, so the neutral point's guardian is the marked unit and keeps its mark and its
        // mark particles (batch 570 carried both to a replacement).
        var neutral = Guardian(runtime, quests, order.Target);
        TestAssert.True(ReferenceEquals(neutral, marked) && neutral.Team == Team.Neutral, "the neutralised point keeps its guardian, now neutral");
        var mark = neutral.Buffs.FirstOrDefault(buff => buff.Name == Indicator);
        TestAssert.True(mark is { ExpiresAt: > 24000.0 } && mark.Type == BuffType.Aura,
            $"the neutral guardian keeps the mark (BUFF_Aura, 25000 s, {mark?.ExpiresAt:F1} s left, buffs {string.Join(",", neutral.Buffs.Select(b => b.Name))})");
        foreach (var capture in captures)
            TestAssert.True(capture.Marks.Any(created => created.Unit == neutral.NetId && !capture.Killed.Contains(created.Effect)),
                "each client keeps its team's mark particle on the guardian");

        // DIAGNOSTICS: test seam, the "Order Capture" branch (neutral past 99, :1261-1280). The capture completes Order's quest.
        quests.Captures[order.Target - 1].SetVariable("CaptureProgress", 100.0);
        for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);
        TestAssert.True(quests.CapturePointOwners[order.Target] == Team.Order, $"Order captured point {Letter(order.Target)}");
        Completed(captures[0], order, success: true, "Order");
        var captured = Guardian(runtime, quests, order.Target);
        TestAssert.True(ReferenceEquals(captured, neutral) && captured.Team == Team.Order, "the capture keeps the point's guardian, now Order's");
        TestAssert.True(!captured.Buffs.Any(buff => buff.Name == Indicator),
            "the quest's end clears the mark from the point's guardian (OdinQuestParticleRemover on the marker's death)");
        foreach (var capture in captures)
            TestAssert.True(capture.Marks.All(created => capture.Killed.Contains(created.Effect)),
                $"no mark particle outlives the quest (alive: {string.Join(", ", capture.Marks.Where(created => !capture.Killed.Contains(created.Effect)).Select(created => $"0x{created.Effect:X8} on 0x{created.Unit:X8}"))})");
        TestAssert.True(failures.Records.Count == 0, "no quest script raised: " + string.Join("; ", failures.Records));
    });

    private const string Indicator = "OdinQuestIndicator";

    private sealed record QuestPair(int Target, int Defend, uint CaptureId, uint DefendId);

    private static bool Add(byte[] packet) => packet[0x186] == 0;

    // The two adds and the announcement a team's observer received, checked against the client's layout; the points are read from the keys.
    private static QuestPair Issued(MatchRuntime runtime, BehaviorTreeQuestHost quests, Capture capture, Team team)
    {
        var enemy = team == Team.Order ? Team.Chaos : Team.Order;
        TestAssert.True(capture.Events.Count == 1, $"{team}'s client hears one quest announcement (0x4A event 98), got {capture.Events.Count}");
        var source = BinaryPrimitives.ReadUInt32LittleEndian(capture.Events[0].AsSpan(6));
        TestAssert.True(runtime.World.Entities.TryGetValue(source, out var unit) && unit.Team == team,
            $"the announcement's source is a unit of {team} (the @src your team@ text)");
        var adds = capture.Quests.Where(Add).ToList();
        TestAssert.True(adds.Count == 2 && capture.Quests.Count == 2, $"{team}'s client gets two quest adds, got {capture.Quests.Count} quest packets");
        var primary = adds.Single(packet => packet[0x185] == 0);
        var secondary = adds.Single(packet => packet[0x185] == 1);
        var target = Point(Text(primary, 5), "game_quest_capture_");
        var defend = Point(Text(secondary, 5), "game_quest_defend_");
        TestAssert.True(quests.CapturePointOwners[target] == enemy, $"{team}'s capture quest names a point {enemy} holds ({Letter(target)})");
        TestAssert.True(quests.CapturePointOwners[defend] == team, $"{team}'s defend quest names a point {team} holds ({Letter(defend)})");
        foreach (var packet in adds)
        {
            TestAssert.True(Text(packet, 0x85) == "" && Text(packet, 0x105) == "" && packet[0x187] == 0,
                "an add carries no tooltip, no reward and no rollover handling (the shipped ActivateQuest nodes' HandleRollOver false)");
        }
        return new QuestPair(target, defend, Id(primary), Id(secondary));
    }

    private static void Completed(Capture capture, QuestPair pair, bool success, string who)
    {
        var completes = capture.Quests.Where(packet => packet[0x186] == 1).ToList();
        TestAssert.True(completes.Count == 2, $"{who}'s client gets two quest completions, got {completes.Count}");
        TestAssert.True(completes.Any(packet => Id(packet) == pair.CaptureId && packet[0x185] == 0) &&
                        completes.Any(packet => Id(packet) == pair.DefendId && packet[0x185] == 1),
            $"{who}'s completions name its own two quests with their types");
        TestAssert.True(completes.All(packet => packet[0x187] == (success ? 1 : 0)),
            $"{who}'s quests complete as {(success ? "completed (flag 1)" : "failed (flag 0)")}");
    }

    private static uint Id(byte[] packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0x188));

    private static string Text(byte[] packet, int offset)
    {
        var field = packet.AsSpan(offset, 128);
        var end = field.IndexOf((byte)0);
        TestAssert.True(end >= 0, "a quest text field is NUL-terminated within 128 bytes");
        return Encoding.ASCII.GetString(field[..end]);
    }

    private static int Point(string key, string prefix)
    {
        TestAssert.True(key.StartsWith(prefix, StringComparison.Ordinal) && key.Length == prefix.Length + 1 &&
                        key[^1] is >= 'a' and <= 'e', $"the quest text is a shipped tracker key ({prefix}a..e), got '{key}'");
        return key[^1] - 'a' + 1;
    }

    private static char Letter(int point) => (char)('A' + point - 1);

    private static Entity Hero(MatchRuntime runtime, Team team) =>
        runtime.Session.Participants.Single(participant => participant.Team == team).Hero!;

    private static Entity Guardian(MatchRuntime runtime, BehaviorTreeQuestHost quests, int point)
    {
        var at = (Position3)quests.Captures[point - 1].Variable("CapturePosition")!;
        return runtime.World.Entities.Values.Where(unit => unit.Alive && unit.ChampionName == "OdinNeutralGuardian")
            .OrderBy(unit => Math.Abs(unit.Position.X - at.X) + Math.Abs(unit.Position.Z - at.Z)).First();
    }

    // DIAGNOSTICS: test seam. A point a team holds: the tree's owner with its full progress (100 Order, -100 Chaos, the tree's capture
    // thresholds), and the guardian on that team, as the host leaves them after a capture.
    private static void Hold(MatchRuntime runtime, BehaviorTreeQuestHost quests, int point, Team team)
    {
        quests.Captures[point - 1].SetVariable("CurrentPointOwner", team);
        quests.Captures[point - 1].SetVariable("CaptureProgress", team == Team.Order ? 100.0 : -100.0);
        Guardian(runtime, quests, point).Team = team;
    }

    private static void RunTo(MatchRuntime runtime, double seconds)
    {
        while (runtime.Combat.CurrentTime + Step / 2 < seconds) runtime.Tick(Step);
    }


    /// <summary>
    /// The quest packets (0x90) and quest announcements (0x4A event 98) one observer was sent, as raw plaintext; batch 570: the quest mark's
    /// particles it got (0x8C of odin_point_active, effect and bound unit) and every effect it was told to kill (0x3D).
    /// </summary>
    private sealed class Capture : IReplicationTransport
    {
        private static readonly uint MarkHash = SpellPackets.BuffNameHash("odin_point_active");

        public List<byte[]> Quests { get; } = [];
        public List<byte[]> Events { get; } = [];
        public List<(uint Effect, uint Unit)> Marks { get; } = [];
        public HashSet<uint> Killed { get; } = [];

        public void Send(PlaintextPacket packet)
        {
            var bytes = packet.Plaintext.ToArray();
            if (bytes.Length == 0) return;
            if (bytes[0] == QuestOpcode)
            {
                TestAssert.True(bytes.Length == 0x18C, $"a quest packet is the client's 396 bytes, got {bytes.Length}");
                Quests.Add(bytes);
            }
            else if (bytes[0] == 0x4A && bytes.Length > 5 && bytes[5] == QuestEvent) Events.Add(bytes);
            else if (bytes[0] == 0x8C && bytes.Length >= 33 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)) == MarkHash)
                Marks.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(25)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(29))));
            else if (bytes[0] == 0x3D && bytes.Length >= 9) Killed.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5)));
        }

        public void Clear()
        {
            Quests.Clear();
            Events.Clear();
        }
    }

    // DominionRelicPickupChecks' and DominionMapLeftoverChecks' pattern: two Annie seats on map 8, the production composition, one observer
    // per seat; AFK latching off (no client drives the seats over the minutes the quest timers take).
    private static void WithMatch(Action<MatchRuntime, BehaviorTreeQuestHost, Capture[], ScriptFailures> body)
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            var roster = new[]
            {
                new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
                new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            var failures = new ScriptFailures();
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, sessionLog: failures, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            runtime.Session.CreateWorld();
            var captures = new Capture[roster.Length];
            for (var id = 1; id <= roster.Length; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
                var participant = runtime.Session.Participants[id - 1];
                captures[id - 1] = new Capture();
                runtime.Delivery.AddObserver(new ReplicationObserver((ulong)id, participant.Team, participant.Hero!.NetId, captures[id - 1],
                    ready: true, view: participant.View));
            }
            TestAssert.True(runtime.Session.Start(), "the map 8 match starts");
            runtime.Tick(Step);
            var quests = (runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests
                ?? throw new InvalidOperationException("map 8 runs its quest host");
            body(runtime, quests, captures, failures);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>The production composition's script.exception records (a hook run that raised), as the match tool counts them.</summary>
    private sealed class ScriptFailures : ISessionLogSink
    {
        public List<string> Records { get; } = [];

        public bool Admit(string category, string level) => level == "ERROR";

        public void Write(string category, string level, string message, IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (message == "script.exception") Records.Add(string.Join(" ", (properties ?? new Dictionary<string, object?>()).Select(entry => $"{entry.Key}={entry.Value}")));
        }
    }
}
