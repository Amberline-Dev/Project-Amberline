using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

public static class NeutralObservationChecks
{
    [Check("neutral.empty-observation-per-team")]
    public static void EmptyObservationUsesTeamStateAndClientIdentity()
    {
        var world = new WorldState();
        var camp = NeutralDefinitions.Camps[1];
        var orderHero = new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, new Position2(camp.GroupPosition.X, camp.GroupPosition.Z));
        var chaosHero = new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Chaos, new Position2(14_000, 14_000));
        world.Register(orderHero);
        world.Register(chaosHero);

        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [1]);
        scheduler.Start();
        scheduler.Tick(115.0);
        var generationOne = scheduler.MembersForCamp(1).ToArray();
        TestAssert.Equal(camp.Roster.Count, generationOne.Length);

        var orderTransport = new CaptureTransport();
        var chaosTransport = new CaptureTransport();
        var retainedTransport = new CaptureTransport();
        var delivery = new ReplicationDelivery(world, Services(), new OpenVisionEnvironment(),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.NeutralCamps = scheduler.Camps;
        var order = new ReplicationObserver(1, Team.Order, orderHero.NetId,
            orderTransport, clientId: 0);
        var chaos = new ReplicationObserver(2, Team.Chaos, chaosHero.NetId,
            chaosTransport, clientId: 7);
        var retained = new ReplicationObserver(4, Team.Order, orderHero.NetId,
            retainedTransport, ready: false, clientId: 4);
        delivery.AddObserver(order);
        delivery.AddObserver(chaos);
        delivery.AddObserver(retained);
        foreach (var member in generationOne)
        {
            order.View.Known.Add(member.NetId);
            chaos.View.Known.Add(member.NetId);
            retained.View.Known.Add(member.NetId);
        }
        delivery.Flush();
        orderTransport.Clear();
        chaosTransport.Clear();

        foreach (var member in generationOne)
        {
            member.Entity.Alive = false;
            member.Entity.Health = 0.0;
            scheduler.OnDeath(member.Entity, 120.0);
        }
        delivery.Flush();

        var orderEmpty = EmptyPackets(orderTransport.Packets);
        TestAssert.Equal(1, orderEmpty.Count, "visible order team learns empty camp");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(
            orderEmpty[0].Plaintext.Span[5..]), "seat zero is a valid ClientId");
        TestAssert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(
            orderEmpty[0].Plaintext.Span[9..]), "camp index");
        TestAssert.Equal(0, EmptyPackets(chaosTransport.Packets).Count,
            "enemy team does not learn through fog");
        TestAssert.True(scheduler.Camps[1].EmptyObservedTeams.Contains(Team.Order));
        TestAssert.False(scheduler.Camps[1].EmptyObservedTeams.Contains(Team.Chaos));

        chaosHero.Position = new Position2(camp.GroupPosition.X, camp.GroupPosition.Z);
        orderTransport.Clear();
        chaosTransport.Clear();
        delivery.Flush();
        TestAssert.Equal(0, EmptyPackets(orderTransport.Packets).Count,
            "same team does not receive duplicate empty state");
        var chaosEmpty = EmptyPackets(chaosTransport.Packets);
        TestAssert.Equal(1, chaosEmpty.Count, "chaos learns after its own camp visibility");
        TestAssert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(
            chaosEmpty[0].Plaintext.Span[5..]), "recipient ClientId");
        TestAssert.True(scheduler.Camps[1].EmptyObservedTeams.Contains(Team.Chaos));

        // The Order team has already learned the generation. A retained view
        // may receive that state after a temporary ready/LOS gap without
        // requiring another visit to the camp.
        orderHero.Position = new Position2(14_000, 14_000);
        retained.Ready = true;
        retainedTransport.Clear();
        delivery.Flush();
        TestAssert.Equal(1, EmptyPackets(retainedTransport.Packets).Count,
            "learned team state survives a temporary observer gap");

        var lateTransport = new CaptureTransport();
        var late = new ReplicationObserver(3, Team.Order, orderHero.NetId,
            lateTransport, clientId: 9);
        delivery.AddObserver(late);
        delivery.Flush();
        TestAssert.Equal(0, EmptyPackets(lateTransport.Packets).Count,
            "fresh reconnect without a camp registration receives no synthetic body");

        var respawnAt = scheduler.Camps[1].NextSpawnAt ?? throw new InvalidOperationException();
        scheduler.Tick(respawnAt);
        var generationTwo = scheduler.MembersForCamp(1).ToArray();
        TestAssert.Equal(2, scheduler.Camps[1].SpawnCycles);
        foreach (var member in generationTwo)
        {
            order.View.Known.Add(member.NetId);
            chaos.View.Known.Add(member.NetId);
            member.Entity.Alive = false;
            member.Entity.Health = 0.0;
            scheduler.OnDeath(member.Entity, respawnAt + 1.0);
        }
        orderTransport.Clear();
        chaosTransport.Clear();
        lateTransport.Clear();
        retainedTransport.Clear();
        delivery.Flush();
        TestAssert.Equal(0, EmptyPackets(orderTransport.Packets).Count,
            "new generation waits for the order team's renewed visibility");
        TestAssert.Equal(1, EmptyPackets(chaosTransport.Packets).Count,
            "new generation resets chaos delivery");
        TestAssert.Equal(0, EmptyPackets(lateTransport.Packets).Count,
            "late observer still has no real camp registration");
        TestAssert.Equal(0, EmptyPackets(retainedTransport.Packets).Count,
            "retained observer without the new generation registration is not synthesized");

        orderHero.Position = new Position2(camp.GroupPosition.X, camp.GroupPosition.Z);
        orderTransport.Clear();
        chaosTransport.Clear();
        delivery.Flush();
        TestAssert.Equal(1, EmptyPackets(orderTransport.Packets).Count,
            "order learns the renewed generation on its own visibility");
        TestAssert.Equal(0, EmptyPackets(chaosTransport.Packets).Count,
            "chaos does not receive a repeated generation");
    }

    // Batch 244: a team learns a cleared camp when one of its units sees the camp marker by the client's
    // line-of-sight test: within 1000 units, the walk starting the viewer's pathfinding radius
    // plus 200 out. Before, any team provider within its own sight radius (1350 for a hero) saw it.
    [Check("neutral.empty-observation-camp-marker-sight")]
    public static void EmptyObservationFollowsTheCampMarkerSightTest()
    {
        var grid = NavigationGrid.LoadDefault();
        var camp = NeutralDefinitions.Camps[1];
        var marker = new Position2(camp.GroupPosition.X, camp.GroupPosition.Z);
        Position2 At(double degrees, double distance) => marker + new Position2(
            distance * Math.Cos(degrees * Math.PI / 180.0), distance * Math.Sin(degrees * Math.PI / 180.0));
        bool Open(Position2 point) => grid.IsMovementWalkable(point) && grid.BrushIdAt(point) is null &&
            grid.HasVisionLos(point, marker);
        var direction = Enumerable.Range(0, 72).Select(step => step * 5.0)
            .FirstOrDefault(degrees => Open(At(degrees, 900.0)) && Open(At(degrees, 1100.0)), double.NaN);
        TestAssert.False(double.IsNaN(direction), "an open direction with clear sight from 900 and 1100 units");

        var world = new WorldState();
        var orderHero = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, Team.Order, At(direction, 1100.0)));
        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [1]);
        scheduler.Start();
        scheduler.Tick(115.0);
        var members = scheduler.MembersForCamp(1).ToArray();
        var transport = new CaptureTransport();
        var delivery = new ReplicationDelivery(world, Services(), new NavigationGridVisionEnvironment(grid),
            _ => Array.Empty<VisionProvider>(), (_, _) => false);
        delivery.NeutralCamps = scheduler.Camps;
        var observer = new ReplicationObserver(1, Team.Order, orderHero.NetId, transport, clientId: 0);
        delivery.AddObserver(observer);
        foreach (var member in members) observer.View.Known.Add(member.NetId);
        delivery.Flush();
        foreach (var member in members)
        {
            member.Entity.Alive = false;
            member.Entity.Health = 0.0;
            scheduler.OnDeath(member.Entity, 120.0);
        }
        transport.Clear();
        delivery.Flush();
        TestAssert.Equal(0, EmptyPackets(transport.Packets).Count, "a hero 1100 units from the marker does not see the cleared camp");

        orderHero.Position = At(direction, 900.0);
        transport.Clear();
        delivery.Flush();
        TestAssert.Equal(1, EmptyPackets(transport.Packets).Count, "a hero 900 units from the marker sees the cleared camp");
    }

    [Check("neutral.production-empty-observation")]
    public static void ProductionCombatDeathRoutesEmptyCampToVisibleClient()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection,
                session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start());

        var order = session.Participants.Single(participant => participant.Team == Team.Order);
        var chaos = session.Participants.Single(participant => participant.Team == Team.Chaos);
        var orderHero = order.Hero ?? throw new InvalidOperationException("missing order hero");
        var chaosHero = chaos.Hero ?? throw new InvalidOperationException("missing chaos hero");
        var camp = NeutralDefinitions.Camps[1];
        orderHero.Position = new Position2(camp.GroupPosition.X, camp.GroupPosition.Z);
        chaosHero.Position = new Position2(14_000, 14_000);
        var orderCapture = new CaptureTransport();
        var chaosCapture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            order.PlayerId, order.Team, orderHero.NetId, orderCapture,
            ready: true, view: order.View, clientId: order.ClientId));
        runtime.Delivery.AddObserver(new ReplicationObserver(
            chaos.PlayerId, chaos.Team, chaosHero.NetId, chaosCapture,
            ready: true, view: chaos.View, clientId: chaos.ClientId));
        runtime.Delivery.Flush();
        orderCapture.Clear();
        chaosCapture.Clear();

        runtime.Tick(120.0);
        var monsters = runtime.World.Entities.Values
            .Where(entity => entity.Kind == EntityKind.Neutral && entity.CampId == 1)
            .ToArray();
        TestAssert.Equal(camp.Roster.Count, monsters.Length,
            "production scheduler spawned the full camp");
        orderCapture.Clear();
        chaosCapture.Clear();

        foreach (var monster in monsters)
            TestAssert.True(runtime.Combat.Kill(monster, orderHero.NetId),
                "combat death enters the neutral scheduler lifecycle");
        runtime.Delivery.Flush();

        var orderEmpty = EmptyPackets(orderCapture.Packets);
        TestAssert.Equal(1, orderEmpty.Count,
            "production sends one empty state to the visible team");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(
            orderEmpty[0].Plaintext.Span[5..]), "production preserves seat-zero ClientId");
        TestAssert.Equal(0, EmptyPackets(chaosCapture.Packets).Count,
            "production does not leak the empty state through fog");
        var campIds = monsters.Select(monster => monster.NetId).ToHashSet();
        TestAssert.Equal(monsters.Length, chaosCapture.Packets.Count(packet =>
            packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] == 0xA6 &&
            campIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]))),
            "fogged observers receive the dead member lifecycle without losing camp registration");
        TestAssert.Equal(0, chaosCapture.Packets.Count(packet =>
            packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] == 0x54 &&
            campIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]))),
            "fogged observers receive no additional visibility leave on hidden death");
        TestAssert.True(campIds.All(chaos.View.Known.Contains),
            "the hidden observer retains every real camp member registration");
    }

    [Check("neutral.production-reveal-stats-speed")]
    public static void ProductionNeutralRevealRestoresSpeedAndCurrentHealth()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection,
                session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "production neutral speed match starts");
        runtime.Waves!.AutomaticSpawningEnabled = false;

        var order = session.Participants.Single(participant => participant.Team == Team.Order);
        var hero = order.Hero ?? throw new InvalidOperationException("missing order hero");
        var capture = new CaptureTransport();
        var observer = new ReplicationObserver(order.PlayerId, order.Team, hero.NetId,
            capture, ready: true, view: new ReplicationView(), clientId: order.ClientId);
        runtime.Delivery.AddObserver(observer);
        runtime.Combat.RestoreClock(180.0);
        runtime.Tick(0.1);

        var neutral = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Neutral && entity.CampId == 6);
        TestAssert.True(neutral.Speed > 0.0, "shipped Dragon movement speed is positive");
        var hidden = capture.Packets.Where(packet => References(packet, neutral.NetId)).ToArray();
        TestAssert.Equal(1, hidden.Count(packet => packet.Plaintext.Span[0] == 0x66),
            "hidden scheduled neutral receives public registration");
        TestAssert.Equal(0, hidden.Count(packet => packet.Plaintext.Span[0] == 0xCC),
            "hidden scheduled neutral receives no stats packet");
        TestAssert.True(observer.View.Known.Contains(neutral.NetId),
            "public neutral registration marks the hidden member known");

        var anchor = neutral.NeutralGroupPosition!.Value;
        hero.Position = new Position2(anchor.X, anchor.Z);
        capture.Clear();
        runtime.Delivery.Flush();

        AssertNeutralStats(capture.Packets, neutral.NetId, expectedSpeed: (float)neutral.Speed,
            expectedMaxHealth: (float)neutral.MaxHealth, expectedHealth: (float)neutral.Health,
            "first reveal");
        TestAssert.True(observer.View.Visible.Contains(neutral.NetId),
            "first neutral reveal is visible");

        // Let the known neutral leave fog, then reveal it with an effective
        // slow. The full template carries the current speed and max health;
        // the following 23-byte delta carries the injured current health.
        neutral.Position = new Position2(14_000, 14_000);
        runtime.Delivery.Flush();
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 2.1);
        runtime.Delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(neutral.NetId),
            "neutral leaves visibility after the fog grace period");
        capture.Clear();

        neutral.Speed *= 0.5;
        neutral.Health -= 100.0;
        neutral.Position = new Position2(anchor.X, anchor.Z);
        runtime.Delivery.Flush();
        AssertNeutralStats(capture.Packets, neutral.NetId, expectedSpeed: (float)neutral.Speed,
            expectedMaxHealth: (float)neutral.MaxHealth, expectedHealth: (float)neutral.Health,
            "slow re-entry");

        neutral.Position = new Position2(14_000, 14_000);
        runtime.Delivery.Flush();
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 2.1);
        runtime.Delivery.Flush();
        capture.Clear();

        neutral.Speed = NeutralStatsCalculator.Calculate(GameData.Default, neutral.Name).Speed;
        neutral.Health -= 50.0;
        neutral.Position = new Position2(anchor.X, anchor.Z);
        runtime.Delivery.Flush();
        AssertNeutralStats(capture.Packets, neutral.NetId, expectedSpeed: (float)neutral.Speed,
            expectedMaxHealth: (float)neutral.MaxHealth, expectedHealth: (float)neutral.Health,
            "slow restore re-entry");

        neutral.Waypoints = [new Position2(neutral.Position.X + 100, neutral.Position.Z)];
        runtime.Delivery.RemoveObserver(observer.ObserverId);
        var reconnectCapture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            99, order.Team, hero.NetId, reconnectCapture,
            ready: true, view: new ReplicationView(), clientId: order.ClientId));
        runtime.Delivery.Flush();
        AssertNeutralStats(reconnectCapture.Packets, neutral.NetId,
            expectedSpeed: (float)neutral.Speed,
            expectedMaxHealth: (float)neutral.MaxHealth,
            expectedHealth: (float)neutral.Health,
            "fresh observer re-entry");
        // Batch 291 (restated to batch 276): the remaining route is the reveal's own movement data (type 2, a 0x64 entry of more than
        // one point, the client's waypoint handler through its movement data reader); the speed follows in the stats of the same flush.
        TestAssert.True(reconnectCapture.Packets.Any(packet => packet.Plaintext.Length > 15 &&
            packet.Plaintext.Span[0] == VisionPackets.EnterVisibilityOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == neutral.NetId &&
            packet.Plaintext.Span[7] == VisionPackets.MovementDataRoute &&
            ((BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[12..]) >> 16) & 0x7f) > 1),
            "fresh observer's reveal carries the remaining moving route");

        hero.Position = new Position2(1000, 1000);
        TestAssert.True(runtime.Combat.Kill(neutral, hero.NetId), "scheduled Dragon dies");
        runtime.Delivery.Flush();
        reconnectCapture.Clear();
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime +
            NeutralDefinitions.Camps[6].RespawnDelay);
        runtime.Tick(0.1);
        var respawn = runtime.World.Entities.Values.Single(entity => entity.Alive &&
            entity.Kind == EntityKind.Neutral && entity.CampId == 6);
        TestAssert.True(respawn.NetId != neutral.NetId, "camp respawns as a new registered actor");
        TestAssert.False(reconnectCapture.Packets.Any(packet => References(packet, respawn.NetId) &&
            packet.Plaintext.Span[0] == 0xCC), "respawn stats stay hidden before sight");
        reconnectCapture.Clear();
        hero.Position = respawn.Position;
        runtime.Delivery.Flush();
        AssertNeutralStats(reconnectCapture.Packets, respawn.NetId, (float)respawn.Speed,
            (float)respawn.MaxHealth, (float)respawn.Health, "respawn reveal");

        runtime.Combat.RestoreClock(901.0);
        runtime.Tick(0.1);
        var baron = runtime.World.Entities.Values.Single(entity => entity.Alive &&
            entity.Kind == EntityKind.Neutral && entity.CampId == 12);
        TestAssert.Equal(0.0, baron.Speed, "shipped Baron remains stationary");
        reconnectCapture.Clear();
        hero.Position = baron.Position;
        runtime.Delivery.Flush();
        AssertNeutralStats(reconnectCapture.Packets, baron.NetId, 0.0f,
            (float)baron.MaxHealth, (float)baron.Health, "stationary Baron reveal");
    }

    private static void AssertNeutralStats(
        IReadOnlyList<PlaintextPacket> packets,
        uint netId,
        float expectedSpeed,
        float expectedMaxHealth,
        float expectedHealth,
        string phase)
    {
        var neutralPackets = packets.Where(packet =>
            packet.Plaintext.Span.Length > 0 && packet.Plaintext.Span[0] == 0xCC &&
            packet.EntityReferences.Any(reference => reference.NetId == netId)).ToArray();
        var full = neutralPackets.Where(packet => packet.Plaintext.Length == 111).ToArray();
        var current = neutralPackets.Where(packet => packet.Plaintext.Length == 23).ToArray();
        TestAssert.Equal(1, full.Length, $"{phase} emits one full neutral stats packet");
        TestAssert.Equal(1, current.Length, $"{phase} emits one neutral current-HP delta");

        var fullBytes = full[0].Plaintext.Span;
        TestAssert.Equal((byte)0x0A, fullBytes[10],
            $"{phase} full stats selects the neutral replication map");
        // Batch 52 (3d81f55a): the 21-entry minion block is 111 bytes; the movement-speed property id (0x04) and its float
        // are its last eight bytes (they sat at 111 and 115 in the former 119-byte block).
        TestAssert.Equal(0x04u, BinaryPrimitives.ReadUInt32LittleEndian(fullBytes[^8..]),
            $"{phase} full stats selects the movement-speed property");
        TestAssert.Near(expectedMaxHealth,
            BinaryPrimitives.ReadSingleLittleEndian(fullBytes[19..]), 1e-4,
            $"{phase} full stats current-health template");
        TestAssert.Near(expectedMaxHealth,
            BinaryPrimitives.ReadSingleLittleEndian(fullBytes[27..]), 1e-4,
            $"{phase} full stats max health");
        TestAssert.Near(expectedSpeed,
            BinaryPrimitives.ReadSingleLittleEndian(fullBytes[^4..]), 1e-4,
            $"{phase} full stats movement speed");
        TestAssert.Near(expectedHealth,
            BinaryPrimitives.ReadSingleLittleEndian(current[0].Plaintext.Span[19..]), 1e-4,
            $"{phase} current-HP delta");

        var fullIndex = packets.ToList().FindIndex(packet => References(packet, netId) &&
            packet.Plaintext.Length == 111 && packet.Plaintext.Span[0] == 0xCC);
        var currentIndex = packets.ToList().FindIndex(packet => References(packet, netId) &&
            packet.Plaintext.Length == 23 && packet.Plaintext.Span[0] == 0xCC);
        // Batch 291 (restated to batch 276): the reveal's 0xC2 carries the neutral's stop or route itself (movement data), so
        // the full stats and the HP delta follow it and no waypoint reanchor does.
        var enterIndex = packets.ToList().FindIndex(packet =>
            packet.Plaintext.Length > 7 && packet.Plaintext.Span[0] == VisionPackets.EnterVisibilityOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == netId &&
            packet.Plaintext.Span[7] is VisionPackets.MovementDataRoute or VisionPackets.MovementDataStop);
        TestAssert.True(enterIndex >= 0 && enterIndex < fullIndex && fullIndex < currentIndex,
            $"{phase} sends the reveal with its movement, then full stats and the HP delta");
        TestAssert.False(packets.Any(packet => packet.Plaintext.Length >= 5 &&
                packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == netId),
            $"{phase} sends no waypoint reanchor after the reveal");
    }

    private static bool References(PlaintextPacket packet, uint netId) =>
        packet.EntityReferences.Any(reference => reference.NetId == netId);

    private static IReadOnlyList<PlaintextPacket> EmptyPackets(
        IReadOnlyList<PlaintextPacket> packets) =>
        packets.Where(packet => packet.Plaintext.Span.Length > 0 &&
            packet.Plaintext.Span[0] == 0xCB).ToArray();

    private static ProtocolReplicationServices Services() => new(
        (_, _) => Array.Empty<PlaintextPacket>(),
        (_, _) => Array.Empty<PlaintextPacket>(),
        (_, _) => Array.Empty<PlaintextPacket>(),
        (_, _) => Array.Empty<PlaintextPacket>(),
        _ => 1u, new NavigationBounds(0, 14000, 0, 14000));

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
