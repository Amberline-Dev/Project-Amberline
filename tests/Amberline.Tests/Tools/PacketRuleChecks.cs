using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// One positive and one negative check per packet-stream rule, listed in docs/testing.md under "Packet-stream rules",
/// built from the real packet builders. The clean production control is in PacketRuleControlChecks.
/// </summary>
public static class PacketRuleChecks
{
    private const uint Hero1 = 0x40000001, Hero2 = 0x40000002;
    private const uint Turret1 = 0x40000040;
    private const uint Minion1 = 0x40010001, Minion2 = 0x40010002;
    private const uint MissileId = 0x40020005;

    // ---- helpers ----

    internal static PacketObservation Observation(PlaintextPacket packet, double t = 0.0, int peer = 0) =>
        new(t, peer, packet.Channel, packet.Reliability == LeaguePacketReliability.Reliable, packet.Plaintext);

    private static PacketStreamChecker Checker(int peers = 1) => new(peers);

    private static void Feed(PacketStreamChecker checker, double t, int peer, params PlaintextPacket[] packets)
    {
        foreach (var packet in packets) checker.Observe(Observation(packet, t, peer));
    }

    private static void Feed(PacketStreamChecker checker, double t, params PlaintextPacket[] packets) =>
        Feed(checker, t, 0, packets);

    private static PlaintextPacket CreateHero(uint netId) =>
        SpawnPackets.CreateHero(netId, 1, "Annie", "Annie", teamIsOrder: true);

    private static PlaintextPacket CreateMinion(uint netId) =>
        SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(netId, 0x40, new Position2(100, 100), 100, "Minion_Basic",
            "Minion_Basic"), 0.0);

    private static PlaintextPacket HeroStats(uint netId, uint syncId, float maxHp = 500f, float moveSpeed = 325f,
        float attackSpeed = 0.7f, float attackRange = 575f) =>
        CombatPackets.HeroStats(netId, new HeroStatsSnapshot(AttackDamage: 50, Armor: 20, SpellBlock: 30, MaxHp: maxHp,
            MaxMp: 300, MoveSpeed: moveSpeed, AttackRange: attackRange, AttackSpeedMod: attackSpeed), syncId);

    private static PlaintextPacket MinionStats(uint netId, uint syncId, float speed = 325f,
        float attackSpeed = 1f, float hp = 400f) =>
        CombatPackets.MinionStats(netId, hp, 10, speed, attackSpeedMod: attackSpeed, syncId: syncId);

    private static IReadOnlyList<PacketRuleViolation> Of(PacketStreamChecker checker, string kind) =>
        checker.Finish().Where(violation => violation.Kind == kind).ToArray();

    private static void NoViolations(PacketStreamChecker checker, string what)
    {
        var all = checker.Finish();
        TestAssert.True(all.Count == 0, $"{what}: expected none, got {string.Join("; ", all.Select(v => $"{v.Kind} {v.Detail}"))}");
    }

    // ---- rule 1 ----

    [Check("packet-rules.sync-id-regression")]
    public static void SyncIdRegression()
    {
        // Positive: the client applies a channel's block only if stored < incoming (initial 0). An equal or zero
        // syncID is never applied; a lower one is lost when it carries a field the client never received.
        var bad = Checker(2);
        Feed(bad, 0.0, CreateHero(Hero1), HeroStats(Hero1, 5), CreateMinion(Minion1));
        Feed(bad, 1.0, CombatPackets.ReplicationHp(Hero1, 400f, 5, isHero: true)); // equal
        Feed(bad, 2.0, CombatPackets.ReplicationHp(Minion1, 390f, 5)); // LOCAL1 mHP applied at 5
        Feed(bad, 2.5, CombatPackets.MinionAttackSpeed(Minion1, 1.5f, 3)); // lower, and mAttackSpeedMod never applied
        Feed(bad, 0.0, 1, CreateHero(Hero2));
        Feed(bad, 3.0, 1, CombatPackets.ReplicationHp(Hero2, 100f, 0, isHero: true)); // zero: never applied
        var found = Of(bad, PacketRuleKinds.SyncIdRegression);
        TestAssert.Equal(3, found.Count);
        TestAssert.Equal((1.0, 0, (byte)0xCC, (uint?)Hero1), (found[0].T, found[0].Peer, found[0].Opcode, found[0].NetId));
        TestAssert.True(found[0].Detail.Contains("syncID 5 is not greater than 5", StringComparison.Ordinal) &&
            found[0].Detail.Contains("never applied", StringComparison.Ordinal), found[0].Detail);
        TestAssert.True(found[1].NetId == Minion1 && found[1].T == 2.5 &&
            found[1].Detail.Contains("syncID 3 is not greater than 5", StringComparison.Ordinal) &&
            found[1].Detail.Contains("never received", StringComparison.Ordinal), found[1].Detail);
        TestAssert.Equal((3.0, 1, (uint?)Hero2), (found[2].T, found[2].Peer, found[2].NetId));
        TestAssert.True(found[2].Detail.Contains("syncID 0 is not greater than 0", StringComparison.Ordinal), "a first update with syncID 0 is never applied");
        // Nothing else is wrong with that stream: the units were created and replicated.
        TestAssert.Equal(3, bad.Finish().Count);

        // Negative: strictly increasing per unit, independent per unit and per peer, and per replication
        // channel exactly like the client (a lower syncID on a channel whose own last is lower still applies).
        var good = Checker(2);
        Feed(good, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1), CreateMinion(Minion1), MinionStats(Minion1, 1));
        Feed(good, 1.0, CombatPackets.ReplicationHp(Hero1, 400f, 2, isHero: true),
            CombatPackets.ReplicationHp(Hero1, 380f, 3, isHero: true));
        Feed(good, 1.0, CombatPackets.MinionMovementSpeed(Minion1, 300f, 9)); // MAP channel at 9
        Feed(good, 2.0, CombatPackets.MinionAttackSpeed(Minion1, 1.5f, 3)); // LOCAL1 channel last was 1
        Feed(good, 0.0, 1, CreateHero(Hero1), HeroStats(Hero1, 1));
        NoViolations(good, "increasing syncIDs");
        TestAssert.Equal(0L, good.StaleReplicationDropped);

        // Negative, and the shape production really sends: a minion's first-sight template (2), partial updates (3, 4),
        // then the older queued copy of the template (1). The client drops it, but it holds every field already, so
        // it is a stale duplicate (counted), not a lost update.
        var stale = Checker();
        Feed(stale, 0.0, CreateMinion(Minion1), MinionStats(Minion1, 2), CombatPackets.MinionCurrentStats(Minion1, 400f, 1f, 3),
            CombatPackets.MinionMovementSpeed(Minion1, 325f, 4), MinionStats(Minion1, 1));
        NoViolations(stale, "stale duplicate template");
        TestAssert.Equal(1L, stale.StaleReplicationDropped);
    }

    // ---- rule 2 ----

    [Check("packet-rules.replicated-stat-zeroed")]
    public static void ReplicatedStatZeroed()
    {
        // Positive: stats a live unit can never have at zero.
        var zero = Checker();
        Feed(zero, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1, maxHp: 0f));
        Feed(zero, 0.0, CreateHero(Hero2), HeroStats(Hero2, 1, moveSpeed: 0f));
        Feed(zero, 0.0, CreateMinion(Minion1), MinionStats(Minion1, 1, attackSpeed: 0f));
        var found = Of(zero, PacketRuleKinds.ReplicatedStatZeroed);
        TestAssert.Equal(3, found.Count);
        TestAssert.True(found[0].NetId == Hero1 && found[0].Detail.Contains("MaxHp", StringComparison.Ordinal), found[0].Detail);
        TestAssert.True(found[1].NetId == Hero2 && found[1].Detail.Contains("MoveSpeed", StringComparison.Ordinal), found[1].Detail);
        TestAssert.True(found[2].NetId == Minion1 && found[2].Detail.Contains("AttackSpeedMod", StringComparison.Ordinal), found[2].Detail);

        // Positive: a shown unit never got its stat block, and a later 0xCC (for ANY unit) flushes it to zero.
        var freeze = Checker();
        Feed(freeze, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1), CreateMinion(Minion1),
            VisionPackets.EnterVisibilityClient(Minion1, "Minion_Basic", 0, 400, 400, new Position2(100, 100)));
        Feed(freeze, 1.0, CombatPackets.ReplicationHp(Hero1, 400f, 2, isHero: true));
        var flushed = Of(freeze, PacketRuleKinds.ReplicatedStatZeroed);
        TestAssert.Equal(1, flushed.Count);
        TestAssert.Equal((1.0, (uint?)Minion1), (flushed[0].T, flushed[0].NetId));
        TestAssert.True(flushed[0].Detail.Contains("flushes all characters", StringComparison.Ordinal), flushed[0].Detail);
        // A tower shown with only protection flags is the shrine path; a normal tower shown without stats is not.
        var turret = Checker();
        Feed(turret, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1), SpawnPackets.CreateTurret(Turret1, 0x40, "Turret_T1_C_01_A"),
            VisionPackets.EnterLocalVisibility(Turret1, 3000, 3000));
        Feed(turret, 1.0, CombatPackets.ReplicationHp(Hero1, 400f, 2, isHero: true));
        TestAssert.Equal(1, Of(turret, PacketRuleKinds.ReplicatedStatZeroed).Count);

        // Negative: stats replicated before the flush; zero minion speed (ward, trap, TestCube); a unit created but
        // never shown (a neutral camp known in fog); a reveal and its stats in one step; the flags-only shrine path.
        var clean = Checker();
        Feed(clean, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1));
        Feed(clean, 0.0, CreateMinion(Minion1), MinionStats(Minion1, 1),
            VisionPackets.EnterVisibilityClient(Minion1, "Minion_Basic", 0, 400, 400, new Position2(100, 100)));
        Feed(clean, 0.0, CreateMinion(Minion2), MinionStats(Minion2, 1, speed: 0f)); // ward-like, speed 0 by design
        Feed(clean, 0.5, SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(0x40030001, 0x40, default, default, default,
            "Wolf", "Wolf", "Wolf1", "", 300, 0, 0, 0, 0)));
        Feed(clean, 1.0, CreateMinion(0x40010003), VisionPackets.EnterVisibilityClient(0x40010003, "Minion_Basic", 0, 400, 400,
            new Position2(100, 100)), CombatPackets.ReplicationHp(Hero1, 400f, 2, isHero: true), MinionStats(0x40010003, 1));
        Feed(clean, 1.0, SpawnPackets.CreateTurret(Turret1, 0x40, "Turret_OrderTurretShrine_A"),
            CombatPackets.TurretProtectionFlags(Turret1, CombatPackets.SFlagNontargetableAll, 1, invulnerable: 1, targetable: 0),
            VisionPackets.EnterVisibilityClient(Turret1, "Turret_OrderTurretShrine", 0, 3000, 3000, new Position2(0, 0)));
        Feed(clean, 2.0, CombatPackets.ReplicationHp(Hero1, 380f, 3, isHero: true));
        // A hero's attack range of 0 is a value a shipped script asks for (Sanguine Pool's IncFlatAttackRangeMod -450 on Vladimir's 450), and the
        // client stores the replicated mAttackRange unclamped (the client's replicated-stat update).
        Feed(clean, 3.0, HeroStats(Hero1, 4, attackRange: 0f));
        NoViolations(clean, "stats replicated in time");
    }

    // ---- rule 3 ----

    [Check("packet-rules.unknown-unit")]
    public static void UnknownUnit()
    {
        // Positive: attack, 0xCC, enter-visibility, buff caster and cast target of units never created for this peer.
        var bad = Checker(2);
        Feed(bad, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1));
        Feed(bad, 0.0, 1, CreateHero(Hero1), HeroStats(Hero1, 1), CreateMinion(Minion2), MinionStats(Minion2, 1));
        Feed(bad, 1.0, CombatPackets.BasicAttack(Minion2, Hero1, 0x40020001)); // peer 0 never saw Minion2
        Feed(bad, 1.0, CombatPackets.MinionAttackSpeed(Minion2, 1.5f, 2));
        Feed(bad, 1.0, VisionPackets.EnterVisibilityClient(Minion1, "Minion_Basic", 0, 400, 400, new Position2(1, 1)));
        Feed(bad, 1.0, SpellPackets.AddBuff(new BuffAdd(Hero1, 3, 1, 0x1234, 5f, Minion1)));
        Feed(bad, 1.0, SpellPackets.CastSpellAnswer(Hero1, new SpellCastInfo(7, 8, 1, Hero1, 0, default, default,
            [new SpellCastTarget(Minion1, default, 0)], 1, 0, 0, default)));
        var found = Of(bad, PacketRuleKinds.UnknownUnit);
        TestAssert.Equal(5, found.Count);
        TestAssert.True(found.All(violation => violation.Peer == 0 && violation.T == 1.0), "only peer 0 lacks Minion2/Minion1");
        TestAssert.Equal(new uint?[] { Minion2, Minion2, Minion1, Minion1, Minion1 }, found.Select(v => v.NetId).ToArray());
        TestAssert.Equal(new byte[] { 0x0D, 0xCC, 0xC2, 0xBF, 0xBD }, found.Select(v => v.Opcode).ToArray());
        TestAssert.Equal(5, bad.Finish().Count, "the other kinds stay quiet");

        // Negative: created first (also fogged: LeaveVisibility keeps the creation), map buildings, missile-range
        // IDs, zero IDs, and a peer-specific history.
        var good = Checker(2);
        Feed(good, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1), CreateMinion(Minion1), MinionStats(Minion1, 1));
        Feed(good, 0.0, 1, CreateHero(Hero1), HeroStats(Hero1, 1));
        Feed(good, 1.0, VisionPackets.LeaveVisibility(Minion1).ToArray()); // fog: still known
        Feed(good, 2.0, CombatPackets.BasicAttack(Minion1, Hero1, MissileId), // missile-range IDs are not tracked
            CombatPackets.BasicAttack(Hero1, 0, 0), // a target-less attack has no unit to know
            SpellPackets.AddBuff(new BuffAdd(Hero1, 3, 1, 0x1234, 5f, 0)));
        var nexus = MapDefinitions.BuildingNetId("Nexus_T1"); // the client derives these itself
        Feed(good, 2.0, CombatPackets.BuildingStats(nexus, 5500f, CombatPackets.SFlagTargetableToAll, syncId: 1),
            CombatPackets.DampenerState(nexus, 0f, dead: false));
        Feed(good, 2.0, 1, CombatPackets.BasicAttack(Hero1, Hero1, MissileId));
        NoViolations(good, "known units");
    }

    // ---- rule 4 ----

    [Check("packet-rules.channel-or-reliability")]
    public static void ChannelOrReliability()
    {
        var bad = Checker();
        var waypoints = VisionPackets.WaypointList(Hero1, [new Position2(1, 1)], 1);
        Feed(bad, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1));
        // The same bytes on the gameplay channel, a reliable attack, a spawn on the movement channel, an
        // opcode the catalog lacks, a client-only channel, and an unreliable chat envelope.
        bad.Observe(new PacketObservation(1.0, 0, 3, true, waypoints.Plaintext));
        var attack = CombatPackets.BasicAttack(Hero1, Hero1, 0x40020001);
        bad.Observe(new PacketObservation(1.0, 0, attack.Channel, true, attack.Plaintext));
        bad.Observe(new PacketObservation(1.0, 0, 4, true, CreateHero(Hero2).Plaintext));
        bad.Observe(new PacketObservation(1.0, 0, 3, true, new byte[] { 0xFE, 0, 0, 0, 0 }));
        bad.Observe(new PacketObservation(1.0, 0, 1, true, waypoints.Plaintext));
        var chat = ChatPackets.Envelope(0, false, "hi"u8);
        bad.Observe(new PacketObservation(1.0, 0, chat.Channel, false, chat.Plaintext));
        var found = Of(bad, PacketRuleKinds.ChannelOrReliability);
        TestAssert.Equal(6, found.Count);
        TestAssert.Equal(new byte[] { 0xC1, 0x0D, 0x4F, 0xFE, 0xC1, 0 }, found.Select(v => v.Opcode).ToArray());
        TestAssert.True(found[0].Detail.Contains("sent on channel 3; the catalog sends it on channel 4", StringComparison.Ordinal), found[0].Detail);
        TestAssert.True(found[1].Detail.Contains("sent reliably; the catalog sends it unreliably", StringComparison.Ordinal), found[1].Detail);
        TestAssert.True(found[3].Detail.Contains("not in the packet catalog", StringComparison.Ordinal), found[3].Detail);
        TestAssert.True(found[4].Detail.Contains("channel 1", StringComparison.Ordinal), found[4].Detail);
        TestAssert.True(found[5].Detail.Contains("chat", StringComparison.Ordinal), found[5].Detail);

        // Negative: the builders' own delivery, including the unsequenced loading ping, reliable chat, and the
        // opcodes that mean different things per channel (0x65/0x66/0x67 on loading vs gameplay/movement).
        var good = Checker();
        Feed(good, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1), CombatPackets.BasicAttack(Hero1, Hero1, 0x40020001),
            CombatPackets.ApplyDamage(Hero1, Hero1, 5f), waypoints, LoadingPackets.PingLoadInfo(1, 1ul),
            ChatPackets.Envelope(0, true, "hi"u8), SpawnPackets.StartSpawn(), LoadingPackets.RequestReskin(1ul, "Annie"),
            LoadingPackets.RequestRename(1ul, "x"), LoadingPackets.TeamRosterUpdate([1ul], [2ul]),
            VisionPackets.Dash(Hero1, new Position2(0, 0), new Position2(10, 10), 800f, new NavigationBounds(0, 14000, 0, 14000)),
            SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(0x40030001, 0x40, default, default, default,
                "Wolf", "Wolf", "Wolf1", "", 300, 0, 0, 0, 0)));
        NoViolations(good, "builder delivery");
    }

    // ---- optional rule: duplicate creation ----

    [Check("packet-rules.duplicate-creation")]
    public static void DuplicateCreation()
    {
        var bad = Checker();
        Feed(bad, 0.0, CreateMinion(Minion1), CreateMinion(Minion1));
        var found = Of(bad, PacketRuleKinds.DuplicateCreation);
        TestAssert.Equal(1, found.Count);
        TestAssert.Equal((0x80, (uint?)Minion1), (found[0].Opcode, found[0].NetId));

        // Negative: each unit once; a missile-range ID (TestCube, ball) wraps by design and may be created again.
        var good = Checker();
        Feed(good, 0.0, CreateMinion(Minion1), CreateMinion(Minion2), CreateMinion(MissileId), CreateMinion(MissileId));
        NoViolations(good, "single creation");
    }

    // ---- 0xCC parsing ----

    [Check("packet-rules.malformed-replication")]
    public static void MalformedReplication()
    {
        var packet = HeroStats(Hero1, 1).Plaintext.ToArray();
        var checker = Checker();
        Feed(checker, 0.0, CreateHero(Hero1));
        checker.Observe(new PacketObservation(1.0, 0, 3, true, packet[..^3])); // cut mid-value
        checker.Observe(new PacketObservation(1.0, 0, 3, true, packet.Concat(new byte[] { 1 }).ToArray())); // trailing junk
        var found = Of(checker, PacketRuleKinds.MalformedReplication);
        // The second report repeats the first peer/opcode: it is only counted, and Finish says so in one summary row.
        TestAssert.Equal(2, found.Count);
        TestAssert.True(found[0].Peer == 0 && found[0].Detail.Contains("cut off", StringComparison.Ordinal), found[0].Detail);
        TestAssert.True(found[1].Peer == -1 && found[1].Detail.StartsWith("1 further", StringComparison.Ordinal), found[1].Detail);
        TestAssert.Equal(2L, checker.TotalsByKind[PacketRuleKinds.MalformedReplication]);
        // Up to seven zero bytes are the block padding of an encrypted capture and are accepted.
        var padded = Checker();
        Feed(padded, 0.0, CreateHero(Hero1));
        padded.Observe(new PacketObservation(1.0, 0, 3, true, packet.Concat(new byte[7]).ToArray()));
        NoViolations(padded, "zero padding");
    }

    // ---- streaming and bounded memory ----

    [Check("packet-rules.streaming-bounded")]
    public static void StreamingIsBoundedAndDeterministic()
    {
        var checker = Checker(2);
        var minions = Enumerable.Range(0, 30).Select(index => Minion1 + (uint)index).ToArray();
        Feed(checker, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1));
        foreach (var minion in minions) Feed(checker, 0.0, CreateMinion(minion), MinionStats(minion, 1));
        var movement = minions.Select(minion => Observation(VisionPackets.WaypointList(minion, [new Position2(1, 1)], 2))).ToArray();
        var attack = Observation(CombatPackets.BasicAttack(Hero1, minions[0], MissileId));
        var attackPeer1 = attack with { Peer = 1 }; // peer 1 never saw them: reports once, then only counts
        // Warm up, then 310000 packets must not allocate per packet (a match feeds hundreds of thousands).
        for (var round = 0; round < 100; round++) foreach (var packet in movement) checker.Observe(packet);
        checker.Observe(attackPeer1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 10000; round++)
        {
            foreach (var packet in movement) checker.Observe(packet);
            checker.Observe(attack);
            checker.Observe(attackPeer1);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestAssert.True(allocated < 400_000, $"observing 310000 packets allocated {allocated} bytes");

        // A flood of distinct unknown units lists the first 200, counts them all, and says so once at the end.
        var flood = Checker();
        for (var index = 0; index < 5000; index++)
            flood.Observe(Observation(CombatPackets.BasicAttack(0x40010000 + (uint)index, 0, 0x40020001)));
        var report = flood.Finish();
        TestAssert.Equal(PacketStreamChecker.MaxListedPerKind + 1, report.Count);
        TestAssert.Equal(5000L, flood.TotalsByKind[PacketRuleKinds.UnknownUnit]);
        TestAssert.Equal(-1, report[^1].Peer);
        TestAssert.True(report[^1].Detail.Contains("4800 further unknown-unit", StringComparison.Ordinal), report[^1].Detail);

        // Same input, same output, in the same order.
        string Run()
        {
            var again = Checker(2);
            Feed(again, 0.0, CreateHero(Hero1), HeroStats(Hero1, 1));
            Feed(again, 1.0, 1, CombatPackets.BasicAttack(Minion1, Hero1, MissileId), CombatPackets.ReplicationHp(Hero1, 1, 1, isHero: true));
            Feed(again, 2.0, CombatPackets.ReplicationHp(Hero1, 1, 1, isHero: true));
            return string.Join("|", again.Finish().Select(v => $"{v.Kind},{v.T},{v.Peer},{v.Opcode},{v.NetId},{v.Detail}"));
        }
        TestAssert.Equal(Run(), Run());
        TestAssert.True(Run().Length > 0);
    }
}
