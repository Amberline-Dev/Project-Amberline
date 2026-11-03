using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;

namespace Amberline.Tests.Network;

/// <summary>
/// The wire of lane-minion and pet routes. Batch 270: retail sends every route a script decision asks for, so the
/// 16 October 2026 coalescing (the client snaps a minion to the first waypoint of every 0xC1) is gone.
/// </summary>
public static class MinionWaypointDeliveryChecks
{
    private const byte WaypointList = 0xC1;
    private const byte StopAttack = 0x39;

    // Batch 270: retail sends every route a script decision asks for; ReplicationDelivery.CoalesceMinionWaypoints, which kept only
    // a minion's or pet's last 0xC1 of a flush, is gone, so these checks now pin every route in its queued order.
    [Check("replication.minion-waypoints-every-route-in-order")]
    public static void EveryRouteOfEachMinionReachesTheObserverInOrder()
    {
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        var hero = world.Register(Unit(1, EntityKind.Hero, UnitType.Hero, new(7000, 7000)));
        var first = world.Register(Minion(0x40010001, new(7100, 7000)));
        var second = world.Register(Minion(0x40010002, new(7200, 7000)));
        var transport = new Recording();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVision(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.AddObserver(new ReplicationObserver(1, Team.Order, hero.NetId, transport, ready: true));
        delivery.Flush();
        TestAssert.True(delivery.Observers[0].View.Visible.Contains(first.NetId) &&
                        delivery.Observers[0].View.Visible.Contains(second.NetId),
            "the fixture observer must see both minions before the routes are queued");
        transport.Packets.Clear();

        Position2[] Route(double x) => [new(7100, 7000), new(7100 + x, 7000)];
        delivery.Enqueue(VisionPackets.WaypointList(first.NetId, Route(100), 10));
        delivery.Enqueue(CombatPackets.NpcInstantStopAttack(first.NetId, CombatPackets.LandedAttackFlags));
        delivery.Enqueue(VisionPackets.WaypointList(first.NetId, Route(200), 11));
        delivery.Enqueue(VisionPackets.WaypointList(second.NetId, Route(300), 12));
        delivery.Enqueue(VisionPackets.WaypointList(first.NetId, [new Position2(7100, 7000)], 13));
        delivery.Enqueue(VisionPackets.WaypointList(hero.NetId, Route(400), 14));
        delivery.Enqueue(VisionPackets.WaypointList(hero.NetId, Route(500), 15));
        delivery.Flush();

        var sent = transport.Packets.Where(packet =>
                packet.Plaintext.Span[0] is WaypointList or StopAttack)
            .Select(packet => (Op: packet.Plaintext.Span[0],
                NetId: BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]),
                Sync: packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == WaypointList
                    ? BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) : 0u))
            .ToArray();
        TestAssert.Equal(
            new[]
            {
                (WaypointList, first.NetId, 10u),
                (StopAttack, first.NetId, 0u),
                (WaypointList, first.NetId, 11u),
                (WaypointList, second.NetId, 12u),
                (WaypointList, first.NetId, 13u),
                (WaypointList, hero.NetId, 14u),
                (WaypointList, hero.NetId, 15u),
            },
            sent,
            "a minion receives every route of the flush in its queued order, like a hero");
    }

    [Check("replication.pet-waypoints-every-route-in-order")]
    public static void EveryRouteOfEachPetReachesTheObserverInOrder()
    {
        // The pets (Tibbers, Shaco's clone, Yorick's revenant and ghouls) receive every route too.
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        var hero = world.Register(Unit(1, EntityKind.Hero, UnitType.Hero, new(7000, 7000)));
        var kinds = new[] { EntityKind.Pet, EntityKind.ShacoClone, EntityKind.YorickRevenant, EntityKind.YorickGhoul };
        var pets = kinds.Select((kind, index) => world.Register(
            Unit(0x40030001u + (uint)index, kind, UnitType.Monster, new(7100 + 100 * index, 7000)))).ToArray();
        var transport = new Recording();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVision(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.AddObserver(new ReplicationObserver(1, Team.Order, hero.NetId, transport, ready: true));
        delivery.Flush();
        TestAssert.True(pets.All(pet => delivery.Observers[0].View.Visible.Contains(pet.NetId)),
            "the fixture observer must see every pet before the routes are queued");
        transport.Packets.Clear();

        Position2[] Route(double x) => [new(7100, 7000), new(7100 + x, 7000)];
        uint sync = 10;
        foreach (var pet in pets)
        {
            delivery.Enqueue(VisionPackets.WaypointList(pet.NetId, Route(100), sync++));
            delivery.Enqueue(CombatPackets.NpcInstantStopAttack(pet.NetId, CombatPackets.LandedAttackFlags));
            delivery.Enqueue(VisionPackets.WaypointList(pet.NetId, Route(200), sync++));
            delivery.Enqueue(VisionPackets.WaypointList(pet.NetId, [new Position2(7100, 7000)], sync++));
        }
        delivery.Flush();

        var sent = transport.Packets.Where(packet =>
                packet.Plaintext.Span[0] is WaypointList or StopAttack)
            .Select(packet => (Op: packet.Plaintext.Span[0],
                NetId: BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]),
                Sync: packet.Plaintext.Length >= 9 && packet.Plaintext.Span[0] == WaypointList
                    ? BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) : 0u))
            .ToArray();
        var expected = pets.SelectMany((pet, index) => new[]
        {
            (WaypointList, pet.NetId, 10u + (uint)(index * 3)),
            (StopAttack, pet.NetId, 0u),
            (WaypointList, pet.NetId, 10u + (uint)(index * 3) + 1u),
            (WaypointList, pet.NetId, 10u + (uint)(index * 3) + 2u),
        }).ToArray();
        TestAssert.Equal(expected, sent,
            "a pet receives every route of the flush in its queued order, around its 0x39 stop-attack");
    }

    [Check("replication.minion-spawn-route-is-sent-once")]
    public static void AWaveSpawnRouteAndItsRevealAnchorAreOnePacket()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, minionFidelity: true);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null,
            "observer authentication");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "observer ready");
        TestAssert.True(runtime.Session.Start(), "session start");
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException("observer has no hero");
        var transport = new Recording();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            1, Team.Order, hero.NetId, transport, ready: true, view: participant.View));
        runtime.Delivery.Flush();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        transport.Packets.Clear();

        var minion = runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0);
        runtime.Delivery.Flush();

        // Batch 276: the route is inside the reveal's 0xC2 (movement data type 2, a 0x64 entry), so no 0xC1 reaches the observer.
        TestAssert.Equal(0, transport.Packets.Count(packet => packet.Plaintext.Span[0] == WaypointList &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == minion.NetId),
            "a spawned minion's route reaches its observer once: the reveal carries it, and the route " +
            "queued at the spawn (the same payload under a lower sync id) is not repeated");
        var bytes = transport.Packets.Single(packet => packet.Plaintext.Span[0] == VisionPackets.EnterVisibilityOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == minion.NetId).Plaintext.Span;
        TestAssert.Equal(VisionPackets.MovementDataRoute, bytes[VisionPackets.EnterVisibilityMovementTypeOffset],
            "the reveal carries the route");
        TestAssert.True(((BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) >> 16) & 0x7f) == minion.Waypoints.Count + 1,
            "the one packet carries the position followed by the whole lane route");
        var navigation = NavigationGrid.LoadDefault();
        var first = VisionPackets.Compress(minion.Position,
            new NavigationBounds(navigation.MinX, navigation.MaxX, navigation.MinZ, navigation.MaxZ));
        var flagBytes = minion.Waypoints.Count > 0 ? ((minion.Waypoints.Count - 1) >> 2) + 1 : 0;
        TestAssert.Equal(first.X, BinaryPrimitives.ReadInt16LittleEndian(bytes[(20 + flagBytes)..]),
            "the route starts at the spawn point");
    }

    [Check("replication.minion-spawn-sequence-allied-and-enemy")]
    public static void AlliedAndEnemyLaneMinionsReachTheObserverWithTheSameSpawnSequence()
    {
        // Night 2 (17 Oct 2026). The idle Order hero of the real-client captures saw 588 of 588 Order wave minions drawn
        // 3-25 u off their actor against 44 of 570 Chaos ones. Rerunning the batch-28 build with its duplicate route gave
        // the same 100% (138 of 138), so no spawn sequence causes it: the unit is inside the local camera box, where the
        // client chases the actor with `DeadRecon`, or outside it, where it copies the actor (client_path_follower_re.md,
        // "On-screen render of AI units"). This pins the sequence both teams already get: create, reveal, local
        // reveal, and the live route exactly once, with a fogged Chaos wave created at its first reveal.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, minionFidelity: true);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null,
            "observer authentication");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "observer ready");
        TestAssert.True(runtime.Session.Start(), "session start");
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException("observer has no hero");
        var transport = new Recording();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            1, Team.Order, hero.NetId, transport, ready: true, view: participant.View));
        runtime.Delivery.Flush();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        transport.Packets.Clear();

        byte[] Sequence(uint netId) => transport.Packets
            .Where(packet => packet.Plaintext.Span[0] is 0x80 or 0xC2 or 0xB5 or WaypointList &&
                             packet.EntityReferences.Any(reference => reference.NetId == netId))
            .Select(packet => packet.Plaintext.Span[0]).ToArray();

        var ally = runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0);
        runtime.Delivery.Flush();
        var allied = Sequence(ally.NetId);
        transport.Packets.Clear();

        var enemy = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0.0, 0.0);
        runtime.Delivery.Flush();
        TestAssert.Equal(0, Sequence(enemy.NetId).Length,
            "a Chaos wave in fog sends the Order observer nothing until it is first seen");
        enemy.Position = new Position2(ally.Position.X + 100.0, ally.Position.Z);
        runtime.Delivery.Flush();
        var hostile = Sequence(enemy.NetId);

        // Batch 276: the route is inside the reveal's 0xC2, so no 0xC1 follows the local reveal.
        var expected = new byte[] { 0x80, 0xC2, 0xB5 };
        TestAssert.Equal(expected, allied,
            "an Order wave minion: create, reveal carrying its route once, local reveal (no repeated route)");
        TestAssert.Equal(expected, hostile,
            "a Chaos wave minion at its first reveal gets the same three packets in the same order");
    }

    private static Entity Unit(uint id, EntityKind kind, UnitType type, Position2 position) =>
        new(id, kind, type, Team.Order, position)
        {
            Alive = true, Created = true, Visible = true, Radius = 35.0,
            Health = 400.0, MaxHealth = 400.0,
        };

    private static Entity Minion(uint id, Position2 position)
    {
        var entity = Unit(id, EntityKind.Minion, UnitType.Minion, position);
        entity.MinionType = MinionType.Melee;
        return entity;
    }

    private static ProtocolReplicationServices Services() => new(
        (_, _) => [SpawnPackets.StartSpawn()],
        (_, _) => [SpawnPackets.StartSpawn()],
        (_, _) => [SpawnPackets.StartSpawn()],
        (_, _) => [SpawnPackets.StartSpawn()],
        _ => 1u, new NavigationBounds(0, 14000, 0, 14000));

    private sealed class Recording : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private sealed class OpenVision : IVisionEnvironment
    {
        public double CellSize => 400.0;
        public bool HasVisionLos(Position2 source, Position2 target) => true;
        public bool BrushAllowsVision(Position2 source, Position2 target) => true;
        public int? BrushIdAt(Position2 point) => null;
    }
}
