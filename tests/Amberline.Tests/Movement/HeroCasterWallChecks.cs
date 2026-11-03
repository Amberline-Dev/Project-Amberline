using System.Buffers.Binary;
using System.Diagnostics;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>Regressions for a retained hero order facing a broad mobile-body wall.</summary>
public static class HeroCasterWallChecks
{
    private static readonly Position2 CapturedStart =
        new(6478.051433428191, 7485.646878346071);
    private static readonly Position2 CapturedGoal =
        new(7245.923828125, 7611.88037109375);

    private static readonly (Position2 Position, bool Enemy)[] CapturedBodies =
    [
        (new(6891.964149763866, 6983.287554852526), false),
        (new(6911.298001544126, 7065.093318558469), false),
        (new(6852.2053495365335, 7311.494738037236), false),
        (new(7097, 7355), false),
        (new(6941, 7465), false),
        (new(6980.502203662287, 7598.407645464395), false),
        (new(6567.232728244219, 7215.024895941041), false),
        (new(6699, 7057), false),
        (new(7079.630842068838, 7496.634449928181), true),
        (new(6965, 6927), false),
        (new(7115, 7731), true),
    ];

    [Check("movement.hero-closed-caster-ring-retries-through-opening")]
    public static void ClosedWallWaitsThenOneOrderUsesAnOpening()
    {
        var navigation = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var hero = Hero(world, Team.Order, new Position2(7000, 7000));
        var movement = new MovementSystem(world, navigation);
        var bodies = Enumerable.Range(0, 8).Select(index =>
        {
            var angle = index * Math.PI / 4;
            return Body(world, Team.Order, new Position2(
                hero.Position.X + Math.Cos(angle) * 140,
                hero.Position.Z + Math.Sin(angle) * 140));
        }).ToArray();
        var goal = new Position2(7500, 7000);

        var blocked = movement.SetMoveOrder(hero.NetId, goal).ToArray();
        // Playtest B4 #11, 27 Sept 2026: a mobile enclosure keeps a real
        // collision-solved order instead of a stationary planner wait.
        TestAssert.True(blocked.Length > 1,
            "overlapping mobile discs retain a usable recovery route");
        TestAssert.Equal(goal, movement.RetainedHeroGoal(hero)!.Value,
            "the closed wall must retain the original one-click goal");

        bodies[0].Alive = false;
        var start = hero.Position;
        for (var tick = 0; tick < 80 && Distance(hero.Position, goal) > 3; tick++)
        {
            movement.Tick(.1);
            // Batch 61: a flagged hero brings 0.2 of its radius to the pair test, so the bodies never overlap when the thin hero
            // disc meets the minion's full disc (the full-width BodyClearance is not the solver's stop distance for a hero).
            foreach (var body in bodies.Where(body => body.Alive))
                TestAssert.True(Distance(hero.Position, body.Position) >=
                    movement.MoverPairRadius(hero, body) + movement.PairMovementRadius(body, hero) - 1e-3,
                    $"opened ring tick {tick} crossed body {body.NetId}");
        }
        TestAssert.True(Distance(hero.Position, goal) <= 3,
            "one retained order did not resume through the physical opening");
        TestAssert.True(Distance(hero.Position, goal) < Distance(start, goal),
            "opening the ring made no authoritative progress");
    }

    private static (WorldState World, NavigationGrid Navigation, MovementSystem Movement,
        Entity Hero, Entity[] Bodies) CapturedFixture(Team team)
    {
        var world = new WorldState();
        var navigation = NavigationGrid.LoadDefault();
        var hero = Hero(world, team, CapturedStart);
        return (world, navigation, new MovementSystem(world, navigation), hero,
            AddCapturedBodies(world, team));
    }

    private static Entity[] AddCapturedBodies(WorldState world, Team actorTeam) =>
        CapturedBodies.Select((body, index) => Body(world,
            body.Enemy ? Opponent(actorTeam) : actorTeam, body.Position,
            0x4F30_0000u + (uint)index)).ToArray();

    private static Entity Hero(WorldState world, Team team, Position2 position) =>
        world.Register(new Entity(0x4F20_0001, EntityKind.Hero, UnitType.Hero, team, position)
        {
            Name = "Yorick", ChampionName = "Yorick", OwnerNetId = 0,
            Created = true, Visible = true, Health = 2000, MaxHealth = 2000,
            Radius = 47, Speed = 320,
        });

    private static Entity Body(WorldState world, Team team, Position2 position,
        uint? netId = null, double radius = 35.7437) => world.Register(new Entity(
            netId ?? world.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, team, position)
        {
            Name = "Minion_Wizard", Created = true, Visible = true,
            Health = 5000, MaxHealth = 5000, Radius = radius, Speed = 325,
        });

    private static void AssertRouteAndTicks(NavigationGrid navigation,
        MovementSystem movement, Entity hero, IReadOnlyList<Entity> bodies,
        Position2 exactStart, Position2 goal, IReadOnlyList<Position2> route, string label)
    {
        var obstacles = bodies.Select(body => new NavigationObstacle(body.Position,
            movement.BodyClearance(hero, body))).ToArray();
        TestAssert.Near(94.7437, obstacles[0].Radius, 1e-6,
            $"{label}: captured clearance changed");
        TestAssert.True(navigation.FirstSegmentClearOfUnits(route[0], exactStart,
                route[1], obstacles) && navigation.HasMovementLineOfSight(exactStart, route[1]),
            $"{label}: exact first segment crosses a body or terrain");
        for (var index = 0; index < route.Count - 1; index++)
        {
            TestAssert.True(navigation.SegmentClearOfUnits(route[index], route[index + 1],
                    obstacles, escape: index == 0),
                $"{label}: wire segment {index} crosses a captured body");
            TestAssert.True(index == 0
                    ? navigation.HasMovementLineOfSight(route[index], route[index + 1])
                    : navigation.HasPathingLineOfSight(route[index], route[index + 1]),
                $"{label}: wire segment {index} crosses terrain");
        }

        var previous = hero.Position;
        for (var tick = 0; tick < 80 && Distance(hero.Position, goal) > 3; tick++)
        {
            movement.Tick(.1);
            TestAssert.True(navigation.SegmentClearOfUnits(previous, hero.Position, obstacles),
                $"{label}: fixed-origin tick {tick} crossed a captured body");
            foreach (var body in bodies)
                TestAssert.True(Distance(hero.Position, body.Position) >=
                    movement.BodyClearance(hero, body) - 1e-6,
                    $"{label}: tick {tick} entered body {body.NetId}");
            previous = hero.Position;
        }
        TestAssert.True(Distance(hero.Position, goal) <= 3,
            $"{label}: one retained ground order did not complete");
    }

    private static MatchRuntime StartSolo(string champion = "Annie")
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: champion, chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }

    private static PacketCapture AttachCapture(MatchRuntime runtime, Entity hero)
    {
        var capture = new PacketCapture();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            99, hero.Team, hero.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        capture.Clear();
        return capture;
    }

    private static bool DispatchAttackTo(MatchRuntime runtime, Entity hero, Entity target)
    {
        var payload = new byte[22];
        payload[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        payload[5] = 3;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6), (float)target.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14), (float)target.Position.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), target.NetId);
        return runtime.Session.DispatchGameplay(1, 1, payload, runtime.Dependencies.Gameplay);
    }

    private static void Place(Entity hero, Position2 position)
    {
        hero.Position = position;
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        hero.LastAttackStopPoint = null;
    }

    private static Position2 Unit(Position2 value)
    {
        var length = Distance(value, Position2.Zero);
        return new Position2(value.X / length, value.Z / length);
    }

    private static Team Opponent(Team team) => team == Team.Order ? Team.Chaos : Team.Order;
    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Z - second.Z, 2));

    private sealed class PacketCapture : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = [];

        public void Send(PlaintextPacket packet) => _packets.Add(packet);
        public void Clear() => _packets.Clear();
        public int HeroSingletons(uint netId) => _packets.Count(packet =>
            packet.Plaintext.Span.Length == 17 &&
            packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == netId));
    }
}
