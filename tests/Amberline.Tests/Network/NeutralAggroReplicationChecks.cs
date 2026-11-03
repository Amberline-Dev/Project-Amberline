using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Network;

public static class NeutralAggroReplicationChecks
{
    [Check("replication.inhibitor-state-reaches-known-offscreen-map-object")]
    public static void InhibitorDeathAndRespawnStateReachBothTeamsOffscreen()
    {
        foreach (var observerTeam in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var definition = MapDefinitions.Inhibitors.First(item => item.Team != observerTeam);
            var inhibitor = world.Register(new Entity(definition.NetId, EntityKind.Inhibitor,
                UnitType.Structure, definition.Team, definition.Position)
            {
                Name = definition.Name,
                Health = definition.MaxHealth,
                MaxHealth = definition.MaxHealth,
            });
            var heroPosition = observerTeam == Team.Order
                ? new Position2(100.0, 100.0) : new Position2(14000.0, 14000.0);
            var knownHero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero,
                observerTeam, heroPosition));
            var unregisteredHero = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                observerTeam, heroPosition));
            var alliedHero = world.Register(new Entity(3, EntityKind.Hero, UnitType.Hero,
                definition.Team, heroPosition));
            var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
                _ => Array.Empty<VisionProvider>(), (_, _) => false);
            var capture = new CaptureTransport();
            var observer = new ReplicationObserver(1, observerTeam, knownHero.NetId, capture);
            // The map-ready burst registers every static building before gameplay starts.
            observer.View.Known.Add(inhibitor.NetId);
            delivery.AddObserver(observer);
            var unregisteredCapture = new CaptureTransport();
            var unregisteredObserver = new ReplicationObserver(2, observerTeam,
                unregisteredHero.NetId, unregisteredCapture);
            delivery.AddObserver(unregisteredObserver);
            var alliedCapture = new CaptureTransport();
            var alliedObserver = new ReplicationObserver(3, definition.Team,
                alliedHero.NetId, alliedCapture);
            alliedObserver.View.Known.Add(inhibitor.NetId);
            delivery.AddObserver(alliedObserver);
            delivery.Flush();
            TestAssert.False(observer.View.Visible.Contains(inhibitor.NetId),
                observerTeam + " remains out of enemy inhibitor sight");
            TestAssert.True(alliedObserver.View.Visible.Contains(inhibitor.NetId),
                "allied inhibitor remains visible by the shared sight rule");
            TestAssert.False(unregisteredObserver.View.Known.Contains(inhibitor.NetId),
                "the negative control has never registered this map object");

            var transport = new ProtocolCombatTransport(delivery, HeroStats(),
                _ => true, new ReplicationSyncCounter());
            inhibitor.Alive = false;
            inhibitor.Health = 0.0;
            capture.Clear();
            unregisteredCapture.Clear();
            alliedCapture.Clear();
            transport.SendInhibitorState(inhibitor, dead: true,
                respawnSeconds: 300.0); // LevelScript DISABLE_MINION_SPAWN_BASE_TIME (batch 314: the script arms the return)
            delivery.Flush();
            TestAssert.False(unregisteredCapture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x2D),
                "global state does not create a never-registered inhibitor");
            var state = capture.Packets.Single(packet => packet.Plaintext.Span[0] == 0x2D);
            // retail sweep B4, 27 Sept 2026: native duration is at 5, state at 7;
            // the old high-bit assertion validated the wrong wire layout.
            AssertDampenerState(state, inhibitor.NetId,
                300, 0);
            TestAssert.Equal(1, alliedCapture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x2D),
                "the allied visible observer still receives the dead state exactly once");

            AssertDampenerState(CombatPackets.DampenerState(inhibitor.NetId, 12.0f, dead: true),
                inhibitor.NetId, 12, 0);

            inhibitor.Alive = true;
            inhibitor.Health = inhibitor.MaxHealth;
            capture.Clear();
            unregisteredCapture.Clear();
            alliedCapture.Clear();
            transport.SendInhibitorState(inhibitor, dead: false, respawnSeconds: 0.0);
            delivery.Flush();
            TestAssert.False(unregisteredCapture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x2D),
                "the live edge still requires a known inhibitor NetId");
            state = capture.Packets.Single(packet => packet.Plaintext.Span[0] == 0x2D);
            // retail sweep B4, 27 Sept 2026: state 1 raises OnDampenerRespawn natively.
            AssertDampenerState(state, inhibitor.NetId, 0, 1);
            TestAssert.False(capture.Packets.Concat(alliedCapture.Packets)
                .Concat(unregisteredCapture.Packets).Any(packet =>
                    packet.Plaintext.Span[0] == 0x4A && packet.Plaintext.Span[5] == 27),
                "respawn must not duplicate the native world event");
            TestAssert.Equal(1, alliedCapture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x2D),
                "the allied visible observer still receives the live state exactly once");
        }
    }

    [Check("replication.neutral-aggro-outline-fog-cleanup")]
    public static void TargetZeroClearsKnownNeutralAfterFogExit()
    {
        var clock = new GameClock();
        clock.SetVirtual(100.0);
        var world = new WorldState(clock);
        var observerHero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero,
            Team.Order, new(26.0, 264.0)));
        var neutral = world.Register(new Entity(0x4003_0007, EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, new(1000.0, 264.0))
        {
            Name = "Golem",
            Health = 100.0,
            MaxHealth = 100.0,
        });
        var hiddenTarget = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, neutral.Position)
        {
            Health = 100.0,
            MaxHealth = 100.0,
        });
        var capture = new CaptureTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.AddObserver(new ReplicationObserver(1, Team.Order, observerHero.NetId, capture));
        var combatTransport = new ProtocolCombatTransport(delivery, HeroStats(),
            _ => true, new ReplicationSyncCounter());

        delivery.Flush();
        TestAssert.True(delivery.Observers.Single().View.Known.Contains(neutral.NetId),
            "the neutral is known before it leaves sight");
        TestAssert.True(delivery.Observers.Single().View.Known.Contains(hiddenTarget.NetId),
            "the hidden target is known before it leaves sight");

        neutral.Position = hiddenTarget.Position = new(10_000.0, 10_000.0);
        delivery.Flush();
        TestAssert.False(delivery.Observers.Single().View.Visible.Contains(neutral.NetId),
            "the neutral leaves observer sight");
        TestAssert.False(delivery.Observers.Single().View.Visible.Contains(hiddenTarget.NetId),
            "the target leaves observer sight");

        capture.Clear();
        combatTransport.SendAggroOutline(neutral, hiddenTarget.NetId);
        delivery.Flush();
        TestAssert.False(capture.Packets.Any(packet => IsAggro(packet, neutral.NetId)),
            "a nonzero target remains fog-filtered");

        capture.Clear();
        combatTransport.SendAggroOutline(neutral, 0);
        delivery.Flush();
        var clear = capture.Packets.Single(packet => IsAggro(packet, neutral.NetId));
        TestAssert.Equal((uint)0, Target(clear),
            "target-zero cleanup reaches the known neutral in fog");

        var unseen = world.Register(new Entity(0x4003_0008, EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, new(12_000.0, 12_000.0))
        {
            Name = "Golem",
            Health = 100.0,
            MaxHealth = 100.0,
        });
        delivery.Flush();
        TestAssert.False(delivery.Observers.Single().View.Known.Contains(unseen.NetId),
            "an unseen neutral is not known");
        capture.Clear();
        combatTransport.SendAggroOutline(unseen, 0);
        delivery.Flush();
        TestAssert.False(capture.Packets.Any(packet => IsAggro(packet, unseen.NetId)),
            "target-zero cleanup still requires a known source");
    }

    private static void AssertDampenerState(PlaintextPacket packet, uint netId,
        int seconds, int state)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(8, bytes.Length, "native dampener packet length");
        TestAssert.Equal((byte)0x2D, bytes[0]);
        TestAssert.Equal(netId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]));
        TestAssert.Equal(seconds, BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..7]) & 0x7FFF);
        TestAssert.Equal(state, bytes[7] & 1);
        TestAssert.Equal((byte)3, packet.Channel);
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
    }

    private static bool IsAggro(PlaintextPacket packet, uint sourceNetId)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 9 && bytes[0] == 0xC8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]) == sourceNetId;
    }

    private static uint Target(PlaintextPacket packet) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..9]);

    private static HeroStatsService HeroStats() => new(
        GameData.Default, new ItemCatalog(GameData.Default.Paths),
        new NoopStatsHooks(), new NoopStatsTransport());

    private static ProtocolReplicationServices Services() => new(
        (_, _) => Array.Empty<PlaintextPacket>(),
        (_, _) => Array.Empty<PlaintextPacket>(),
        (_, _) => Array.Empty<PlaintextPacket>(),
        (_, _) => Array.Empty<PlaintextPacket>(),
        _ => 1u, new NavigationBounds(0, 14000, 0, 14000));

    private sealed class NoopStatsHooks : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) { }
        public IReadOnlyList<double> ManaCosts(Entity hero) => Array.Empty<double>();
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire) { }
    }

    private sealed class NoopStatsTransport : IHeroStatsTransport
    {
        public void Push(HeroStatsReplicationSnapshot snapshot) { }
        public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries) { }
    }

    private sealed class CaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();
        public IReadOnlyList<PlaintextPacket> Packets => _packets;
        public void Send(PlaintextPacket packet) => _packets.Add(packet);
        public void Clear() => _packets.Clear();
    }

    private sealed class OpenVisionEnvironment : IVisionEnvironment
    {
        public double CellSize => 400.0;
        public bool HasVisionLos(Position2 source, Position2 target) => true;
        public bool BrushAllowsVision(Position2 source, Position2 target) => true;
        public int? BrushIdAt(Position2 point) => null;
    }
}
