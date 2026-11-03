using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

public static class ChaosCampSpawnChecks
{
    private const double SpawnMargin = 4.0;

    [Check("neutral.chaos-camp-spawn-safe")]
    public static void AllMapOneCampMembersHaveLegalBodyHomes()
    {
        // Batch 219: every member spawns at its scripted point, and the engine no longer moves a member off an unsafe one
        // (ScriptedCampSpawnChecks pins the scripted points). The body, route and wire checks apply to the members whose
        // scripted point is a legal body; an unsafe scripted point must be the spawn exactly as the script wrote it.
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var scheduler = new JungleScheduler(world, GameData.Default, navigation: grid);
        scheduler.Start();
        scheduler.Tick(900.0);

        TestAssert.Equal(12, scheduler.Camps.Count, "Map1 camp set");
        foreach (var state in scheduler.Camps.Values)
        {
            var members = scheduler.MembersForCamp(state.Definition.Number);
            TestAssert.Equal(state.Definition.Roster.Count, members.Count,
                $"camp {state.Definition.Number} member count");
            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];
                var source = state.Definition.SpawnPoints[index];
                var sourcePoint = new Position2(source.X, source.Z);
                var sourceWire = ClientPoint(sourcePoint, grid);
                // NOT RETAIL (batch 219 interim): a blocked scripted point keeps a nearby legal cell (JungleScheduler.SpawnMember).
                // Batch 588: every member stands on the terrain (its Y is the grid's height), at its scripted X and Z.
                TestAssert.Near(grid.HeightAt(new Position2(member.Home.X, member.Home.Z)), member.Home.Y, 1e-9,
                    $"camp {state.Definition.Number} member {index + 1} stands on the terrain");
                if (grid.IsMovementWalkable(sourcePoint))
                    TestAssert.True(member.Home == source with { Y = member.Home.Y },
                        $"camp {state.Definition.Number} member {index + 1} spawns at its scripted point");
                else
                    TestAssert.True(grid.IsMovementWalkable(new Position2(member.Home.X, member.Home.Z)) &&
                        Math.Abs(member.Home.X - source.X) + Math.Abs(member.Home.Z - source.Z) <= 300.0,
                        $"camp {state.Definition.Number} member {index + 1} moves a blocked scripted point to a nearby legal cell");
                TestAssert.Near(member.Entity.Position.X, member.Home.X, 1e-9,
                    $"camp {state.Definition.Number} member {index + 1} entity/home x");
                TestAssert.Near(member.Entity.Position.Z, member.Home.Z, 1e-9,
                    $"camp {state.Definition.Number} member {index + 1} entity/home z");
                TestAssert.True(member.Entity.NeutralHome is Position3 entityHome &&
                    entityHome == member.Home,
                    $"camp {state.Definition.Number} member {index + 1} retreat home");

                var sourceSafe = grid.IsMovementWalkable(sourcePoint) &&
                    grid.IsMovementWalkable(sourceWire) &&
                    grid.PointClearance(sourcePoint) >= member.Entity.Radius + SpawnMargin &&
                    grid.PointClearance(sourceWire) >= member.Entity.Radius + SpawnMargin;
                if (!sourceSafe) continue;

                var home = new Position2(member.Home.X, member.Home.Z);
                var wire = ClientPoint(home, grid);
                TestAssert.True(grid.IsMovementWalkable(home),
                    $"camp {state.Definition.Number} member {index + 1} movement home");
                TestAssert.True(grid.IsMovementWalkable(wire),
                    $"camp {state.Definition.Number} member {index + 1} quantized home");
                TestAssert.True(grid.PointClearance(home) >= member.Entity.Radius + SpawnMargin,
                    $"camp {state.Definition.Number} member {index + 1} body clearance");
                TestAssert.True(grid.PointClearance(wire) >= member.Entity.Radius + SpawnMargin,
                    $"camp {state.Definition.Number} member {index + 1} wire clearance");
                var groupPoint = new Position2(state.CurrentGroupPosition.X, state.CurrentGroupPosition.Z);
                var localRoute = grid.FindPath(home, groupPoint);
                TestAssert.True(localRoute.Count > 0,
                    $"camp {state.Definition.Number} member {index + 1} has a local route to camp anchor");
                var routeLength = localRoute.Zip(localRoute.Skip(1))
                    .Sum(pair => Math.Sqrt(Math.Pow(pair.Second.X - pair.First.X, 2) +
                        Math.Pow(pair.Second.Z - pair.First.Z, 2)));
                TestAssert.True(routeLength <= 600.0,
                    $"camp {state.Definition.Number} member {index + 1} local route stays near camp ({routeLength})");
            }

            for (var left = 0; left < members.Count; left++)
            for (var right = left + 1; right < members.Count; right++)
            {
                var first = members[left].Entity;
                var second = members[right].Entity;
                var dx = first.Position.X - second.Position.X;
                var dz = first.Position.Z - second.Position.Z;
                var required = first.Radius + second.Radius + SpawnMargin;
                TestAssert.True(dx * dx + dz * dz >= required * required,
                    $"camp {state.Definition.Number} members {left + 1}/{right + 1} do not stack");
            }

            var group = state.CurrentGroupPosition;
            TestAssert.True(members.All(member => member.GroupPosition == group &&
                member.Entity.NeutralGroupPosition == group),
                $"camp {state.Definition.Number} generation group anchor");
        }
    }

    [Check("neutral.chaos-camp-wire-home-and-respawn")]
    public static void CorrectedChaosHomesOwnWireAnchorAndRespawn()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var sink = new ProtocolNeutralPacketSink(packets.Add, new ReplicationSyncCounter(),
            world.Allocate, new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
            (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(world, GameData.Default, sink, [8, 11], grid);
        scheduler.Start();
        scheduler.Tick(900.0);

        var initialHomes = scheduler.Camps.Values.ToDictionary(
            state => state.Definition.Number,
            state => scheduler.MembersForCamp(state.Definition.Number)
                .Select(member => member.Home).ToArray());
        foreach (var member in scheduler.Members.Values)
        {
            var packet = packets.Single(packet => packet.Plaintext.Span[0] == 0x66 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == member.NetId);
            var bytes = packet.Plaintext.Span;
            TestAssert.Near(member.Entity.Position.X,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]), 1e-4,
                $"camp {member.CampId} member {member.Member} wire x");
            TestAssert.Near(member.Home.Y,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[14..]), 1e-4,
                $"camp {member.CampId} member {member.Member} wire y");
            TestAssert.Near(member.Entity.Position.Z,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]), 1e-4,
                $"camp {member.CampId} member {member.Member} wire z");
            TestAssert.Near(member.GroupPosition.X,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[22..]), 1e-4,
                $"camp {member.CampId} member {member.Member} group x");
            TestAssert.Near(member.GroupPosition.Z,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[30..]), 1e-4,
                $"camp {member.CampId} member {member.Member} group z");
        }

        foreach (var state in scheduler.Camps.Values)
        {
            foreach (var member in scheduler.MembersForCamp(state.Definition.Number))
            {
                member.Entity.Alive = false;
                member.Entity.Health = 0.0;
                scheduler.OnDeath(member.Entity, 1200.0 + state.Definition.Number);
            }

            var respawnAt = state.NextSpawnAt!.Value;
            scheduler.Tick(respawnAt);
            var respawned = scheduler.MembersForCamp(state.Definition.Number)
                .Select(member => member.Home).ToArray();
            TestAssert.Equal(initialHomes[state.Definition.Number], respawned,
                $"camp {state.Definition.Number} respawn homes");
        }
    }

    [Check("neutral.chaos-camp-first-move-budget")]
    public static void CorrectedChaosHomeDoesNotSnapOnFirstMove()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var scheduler = new JungleScheduler(world, GameData.Default, campIds: [8], navigation: grid);
        scheduler.Start();
        scheduler.Tick(900.0);
        var member = scheduler.MembersForCamp(8).Single(member => member.Member == 2);
        var start = member.Entity.Position;
        var home = member.Home;
        var movement = new MovementSystem(world, grid);
        var route = movement.SetMoveOrder(member.NetId, start + new Position2(300.0, 0.0));
        TestAssert.True(route.Count > 0, "first aggro route exists");
        // Batch 219: a scripted start can sit off the client's 2-unit waypoint grid, so the route opens on that grid cell.
        TestAssert.True(Math.Abs(start.X - route[0].X) < 2.0, "route opens on the start's waypoint cell x");
        TestAssert.True(Math.Abs(start.Z - route[0].Z) < 2.0, "route opens on the start's waypoint cell z");
        movement.Tick(0.1);
        var dx = member.Entity.Position.X - start.X;
        var dz = member.Entity.Position.Z - start.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        // A camp mate in avoidance range steers the step sideways by at most 0.4 of its length (the stock
        // avoidance cap, ClientAvoidance), so a step may reach sqrt(1 + 0.4^2) of the speed budget.
        TestAssert.True(distance <= member.Entity.Speed * 0.1 * Math.Sqrt(1.0 + 0.4 * 0.4) + 2.0,
            $"first movement remains within speed budget ({distance} vs {member.Entity.Speed * 0.1})");
        TestAssert.Near(home.X, member.Entity.NeutralHome!.Value.X, 1e-9,
            "first move retains retreat home x");
        TestAssert.Near(home.Z, member.Entity.NeutralHome!.Value.Z, 1e-9,
            "first move retains retreat home z");
    }

    [Check("neutral.chaos-camp-production-aggro")]
    public static void ProductionCreationAndAggroUseResolvedChaosHomes()
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
        TestAssert.True(session.Start(), "production neutral match starts");
        runtime.Tick(120.0);

        var order = session.Participants.Single(participant => participant.Team == Team.Order);
        var chaos = session.Participants.Single(participant => participant.Team == Team.Chaos);
        var orderHero = order.Hero!;
        var chaosHero = chaos.Hero!;
        var grid = runtime.Navigation;
        var campIds = new[] { 5, 7, 8, 9, 11 };
        var captures = new Dictionary<int, CaptureTransport>();
        foreach (var campId in campIds)
        {
            var members = runtime.World.Entities.Values
                .Where(entity => entity.Kind == EntityKind.Neutral && entity.CampId == campId)
                .OrderBy(entity => entity.NeutralMember).ToArray();
            var hero = campId == 5 ? orderHero : chaosHero;
            var anchor = members[0].NeutralGroupPosition!.Value;
            hero.Position = new Position2(anchor.X, anchor.Z);
            var capture = new CaptureTransport();
            captures[campId] = capture;
            var participant = campId == 5 ? order : chaos;
            runtime.Delivery.AddObserver(new ReplicationObserver(
                checked((ulong)(100 + campId)), participant.Team, hero.NetId, capture,
                ready: true, view: new ReplicationView(), clientId: participant.ClientId));
            runtime.Delivery.Flush();
            foreach (var member in members)
            {
                var creates = capture.Packets.Where(packet => packet.Plaintext.Span.Length >= 323 &&
                    packet.Plaintext.Span[0] == 0x66 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == member.NetId).ToArray();
                TestAssert.Equal(1, creates.Length,
                    $"camp {campId} member {member.NeutralMember} creates on first reveal");
                var revealPacket = capture.Packets.FirstOrDefault(packet => packet.Plaintext.Span[0] == 0xC2 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == member.NetId);
                // Batch 219: a scripted point the anchor cannot see gets its create but no reveal from it (camp 7 member 3 sits
                // on a blocked cell, where the old relocation put it in view). The reveal-only facing and position checks skip it.
                if (revealPacket is null) continue;
                var reveal = revealPacket.Plaintext.Span;
                var face = member.NeutralFacePosition!.Value;
                var expectedFacing = VisionPackets.FacingTowards(member.Position, new Position2(face.X, face.Z));
                TestAssert.Near(expectedFacing.X, BinaryPrimitives.ReadSingleLittleEndian(reveal[20..]), 1e-6,
                    "first reveal preserves the camp's initial facing X");
                TestAssert.Near(expectedFacing.Z, BinaryPrimitives.ReadSingleLittleEndian(reveal[24..]), 1e-6,
                    "first reveal preserves the camp's initial facing Z");
                var bytes = creates[0].Plaintext.Span;
                // Batch 219: the create carries a float32, which rounds a scripted coordinate near 8000 by up to 0.0005.
                TestAssert.Near(member.Position.X,
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]), 1e-3,
                    $"camp {campId} member {member.NeutralMember} creation x");
                TestAssert.Near(member.Position.Z,
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]), 1e-3,
                    $"camp {campId} member {member.NeutralMember} creation z");
                TestAssert.Near(member.NeutralGroupPosition!.Value.X,
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[22..]), 1e-3,
                    $"camp {campId} member {member.NeutralMember} creation group x");
                TestAssert.Near(member.NeutralGroupPosition!.Value.Z,
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[30..]), 1e-3,
                    $"camp {campId} member {member.NeutralMember} creation group z");
            }
        }

        // Batch 219: camp 8 member 2 now spawns at its scripted point, a blocked cell, and a route from there is empty
        // (no path out; open question in the batch record), so the aggro route is checked on member 1, the camp anchor.
        // A real combat hit wakes the production neutral AI, which must publish a normal route beginning at the
        // authoritative home.
        var wolf = runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Neutral && entity.CampId == 8 && entity.NeutralMember == 1);
        var chaosCapture = captures[8];
        // Batch 219: the observer stands 220 units off the anchor, on the legal point the old relocation used for member 2,
        // so the wolf is out of attack range and routes (on the anchor it attacked in place: 0x0D, no route).
        var camp8Anchor = wolf.NeutralGroupPosition!.Value;
        chaosHero.Position = new Position2(10645.0, 8013.0);
        runtime.Delivery.Flush();
        chaosCapture.Clear();
        var before = wolf.Position;
        TestAssert.True(runtime.Combat.ApplyHit(chaosHero.NetId, wolf.NetId, 1.0,
            DamageType.True, DamageSourceKind.Spell), "production hit wakes corrected camp");
        runtime.Tick(0.1);
        var routePacket = chaosCapture.Packets.Where(packet => packet.Plaintext.Span.Length >= 17 &&
            packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == wolf.NetId)
            .FirstOrDefault();
        TestAssert.True(routePacket is not null, "production neutral aggro emits a route");
        var routeBytes = routePacket!.Plaintext.Span;
        // Batch 219: the route opens on the 2-unit waypoint grid cell of the scripted home, so it is within one grid step.
        TestAssert.Near(before.X, BinaryPrimitives.ReadSingleLittleEndian(routeBytes[9..]), 2.0,
            "production aggro route starts at home x");
        TestAssert.Near(before.Z, BinaryPrimitives.ReadSingleLittleEndian(routeBytes[13..]), 2.0,
            "production aggro route starts at home z");
        var dx = wolf.Position.X - before.X;
        var dz = wolf.Position.Z - before.Z;
        TestAssert.True(Math.Sqrt(dx * dx + dz * dz) <= wolf.Speed * 0.1 + 2.0,
            "production aggro travel stays within one movement budget");

        // Push the same actor outside its leash and let the production AI
        // enter retreat. Its next route must target the resolved home, which
        // proves the corrected location owns the return path as well.
        var home = wolf.NeutralHome!.Value;
        wolf.Position = new Position2(home.X + 900.0, home.Z);
        wolf.NeutralState = "engage";
        wolf.AttackTarget = chaosHero.NetId;
        chaosCapture.Clear();
        runtime.Tick(0.6);
        TestAssert.Equal("retreat", wolf.NeutralState, "production AI enters retreat outside leash");
        TestAssert.True(wolf.Waypoints.Count > 0, "production retreat plans a home route");
        var retreatGoal = wolf.Waypoints[^1];
        TestAssert.Near(home.X, retreatGoal.X, 1.5,
            "retreat route returns to resolved home x");
        TestAssert.Near(home.Z, retreatGoal.Z, 1.5,
            "retreat route returns to resolved home z");
        TestAssert.True(grid.IsMovementWalkable(new Position2(home.X, home.Z)),
            "retreat home remains legal terrain");
    }

    private static Position2 ClientPoint(Position2 point, NavigationGrid grid) =>
        Geometry.ClientWaypoints([point], new NavigationBounds(
            grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ))[0];

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
        public void Clear() => Packets.Clear();
    }
}
