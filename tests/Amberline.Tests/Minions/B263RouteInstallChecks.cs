using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 263: every minion route goes through the client's install. The client's server-path follower (run for any
/// unit's path) stops the actor (its stop routine zeroes the stored velocity) and seats it on the path's first point.
/// Batch 254 did both for move orders (<c>CommitMoveOrder</c>); the routes the minion AI installs itself (the lane
/// return, the control-release lane, the structure approach, the stuck repath) skipped them.
/// </summary>
public static partial class B263RouteInstallChecks
{
    private static readonly Position2 OffLattice = new(7000.7, 7000.3);
    private static readonly Position2 Walking = new(20.0, 5.0);

    /// <summary>
    /// Batch 279: a minion's stop (ClearMovement: the plant stop, the lane return that finds no lane) is a one-point path, which the
    /// client installs on receipt like every path (the client's path follower): the minion stopped and seated on its anchor at once, not at its next frame's
    /// stop arrival.
    /// </summary>
    [Check("minions.stop-seats-at-once")]
    public static void StopSeatsAtOnce()
    {
        var fixture = new Fixture();
        fixture.Minion.FrameVelocity = Walking;
        var anchor = fixture.Movement.ClientAnchor(fixture.Minion.Position);
        TestAssert.True(anchor != fixture.Minion.Position, "fixture: the minion stands off the lattice");
        // A target run that gives up with no lane left to return to (ReturnToLane, "target-lost").
        var enemy = fixture.Add(0x40027902, Team.Chaos, new Position2(7300, 7300));
        fixture.Minion.AttackTarget = enemy.NetId;
        fixture.Minion.MinionState = MinionAiState.Engage;
        fixture.Minion.Waypoints = [new Position2(7200, 7200)];
        fixture.Ai.NativeSetStateAndMoveToForwardNav(fixture.Minion, MinionAiState.March);
        var stop = fixture.Packets.Orders.SingleOrDefault(order => order.Reason == "stop:target-lost");
        TestAssert.True(stop is not null, "the lane return publishes its stop");
        TestAssert.Equal(anchor, stop!.Position, "the stop seats the minion on its anchor at once (+0x291 = +0xa8)");
        TestAssert.Equal(Position2.Zero, stop.Velocity, "the stop's install stops the minion");
        TestAssert.True(fixture.Minion.Waypoints.Count == 0, "the minion keeps no route");
    }

    private static readonly List<string> Failures = [];

    // Every scenario runs and reports, so one red names each route that skips the install.
    private static void Run(string reason, Action<Fixture> act)
    {
        try { RunOne(reason, act); }
        catch (InvalidOperationException failure) { Failures.Add(failure.Message); }
    }

    private static void RunOne(string reason, Action<Fixture> act)
    {
        var fixture = new Fixture();
        fixture.Minion.FrameVelocity = Walking;
        TestAssert.True(fixture.Movement.ClientAnchor(fixture.Minion.Position) != fixture.Minion.Position,
            "fixture: the minion stands off the lattice");
        act(fixture);
        var routes = fixture.Packets.Orders.Where(order => order.Reason == reason).ToArray();
        TestAssert.True(routes.Length > 0, $"{reason}: the scenario publishes the route");
        foreach (var route in routes)
        {
            TestAssert.True(route.Points.Count > 1, $"{reason}: a route, not a stop");
            TestAssert.Equal(Position2.Zero, route.Velocity, $"{reason}: the route stops the minion first");
            TestAssert.Equal(fixture.Movement.ClientAnchor(route.Points[0]), route.Position,
                $"{reason}: the minion stands on the route's published first point (+0x291 = +0xa8)");
            TestAssert.Equal(fixture.Movement.ClientAnchor(route.Position), route.Position,
                $"{reason}: which is its lattice anchor");
        }
    }

    private sealed class Fixture
    {
        public WorldState World { get; } = new();
        public MovementSystem Movement { get; }
        public MinionAiSystem Ai { get; }
        public Packets Packets { get; } = new();
        public Entity Minion { get; }

        public Fixture()
        {
            var grid = NavigationGrid.LoadDefault();
            Movement = new MovementSystem(World, grid);
            Minion = Add(0x40026301, Team.Order, OffLattice);
            Minion.MinionState = MinionAiState.March;
            Ai = new MinionAiSystem(World, grid, Movement, new Combat(), Packets,
                new WorldMinionVisionService(World, grid, _ => Array.Empty<VisionProvider>()));
        }

        public Entity Add(uint netId, Team team, Position2 position)
        {
            var stats = MinionDefinitions.BaseStats(MinionType.Melee);
            return World.Register(new Entity(netId, EntityKind.Minion, UnitType.Minion, team, position)
            {
                MinionType = MinionType.Melee,
                Alive = true,
                Created = true,
                Visible = true,
                Speed = stats.MoveSpeed,
                Radius = stats.Radius,
                AttackRange = stats.AttackRange,
                AcquisitionRange = stats.AcquisitionRange,
            });
        }
    }

    private sealed record Route(string Reason, IReadOnlyList<Position2> Points, Position2 Position, Position2 Velocity);

    private sealed class Packets : IMinionPacketSink
    {
        public List<Route> Orders { get; } = [];
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        // What the minion stands on, and how it moves, when its client receives the route.
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) =>
            Orders.Add(new Route(reason, waypoints.ToArray(), minion.Position, minion.FrameVelocity));
        public void SendStop(Entity minion, long syncId, string reason) =>
            Orders.Add(new Route("stop:" + reason, [minion.Position], minion.Position, minion.FrameVelocity));
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private sealed class Combat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Protected;
        public bool IsInAttackRange(Entity attacker, Entity target) => CombatService.InRange(attacker, target);
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }
}
