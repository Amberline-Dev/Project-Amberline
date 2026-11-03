using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Ports the source-backed client collision cases that do not depend on a
/// missing live session capture.  Every case drives the production movement
/// solver and observes its diagnostic boundary; it does not copy the solver
/// into the test project.
/// </summary>
public static partial class CollisionFidelityChecks
{
    private const double Radius = 35.74369812011719;
    private const double Speed = 325.0;
    private const double ClientDt = 1.0 / 30.0;

    [Check("minions-collision-sidestep")]
    public static void HeadOnSidestepDoesNotOscillate()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var walkerStart = new Position2(7648.1, 7802.1);
        var blockerStart = new Position2(7574.7, 7810.5);
        var goal = new Position2(7473.77, 7831.63);
        var walker = Minion(1001, walkerStart, MinionAiState.Engage);
        walker.Waypoints = [goal];
        var blocker = Minion(1002, blockerStart, MinionAiState.Engage);
        blocker.AttackTarget = 999;
        blocker.Waypoints.Clear();
        blocker.Target = null;
        world.Register(walker);
        world.Register(blocker);
        var startDistance = Distance(walkerStart, goal);
        var path = 0.0;
        var previous = walker.Position;
        var lateralSigns = new List<int>();
        var samples = new SnapshotSink();
        var observedMovement = new MovementSystem(world, grid, diagnosticSink: samples);
        for (var tick = 0; tick < 20; tick++)
        {
            observedMovement.Tick();
            var current = walker.Position;
            var delta = current - previous;
            path += Length(delta);
            var ux = (goal.X - walkerStart.X) / startDistance;
            var uz = (goal.Z - walkerStart.Z) / startDistance;
            var lateral = -uz * delta.X + ux * delta.Z;
            var snapshot = samples.Snapshots.Last(item => item.NetId == walker.NetId);
            if (snapshot.ColliderIds.Count > 0 && Math.Abs(lateral) > 1.0)
                lateralSigns.Add(Math.Sign(lateral));
            previous = current;
        }
        var net = Distance(walker.Position, walkerStart);
        var endDistance = Distance(walker.Position, goal);
        var flips = lateralSigns.Zip(lateralSigns.Skip(1), (left, right) => left != right ? 1 : 0).Sum();
        var walkerSnapshots = samples.Snapshots.Where(item => item.NetId == walker.NetId).ToArray();
        var sepBeforeTail = walkerSnapshots.Length == 0 ? 0.0 :
            Length(walkerSnapshots[Math.Max(0, walkerSnapshots.Length * 2 / 3 - 1)]
                .SeparationAccumulation);
        var sep = Length(walker.SeparationAccumulation);
        TestAssert.True(flips <= 2, $"head-on sidestep changed sides {flips} times");
        TestAssert.True(startDistance - endDistance > 120.0,
            $"head-on walker closed only {startDistance - endDistance:R}u");
        TestAssert.True(net > 0.0 && path / net < 2.0,
            $"head-on route oscillated: path={path:R}, net={net:R}");
        TestAssert.True(sep < 200.0,
            "head-on separation accumulated an oscillating detour");
        TestAssert.True(sep - sepBeforeTail < 1.0,
            $"head-on separation kept growing at the tail: {sepBeforeTail:R}->{sep:R}");
        TestAssert.Equal(blockerStart, blocker.Position, "head-on blocker was displaced");
    }

    [Check("minions-client-movement-grid-gate")]
    public static void PerFrameGateUsesClientMovementGrid()
    {
        var grid = NavigationGrid.LoadDefault();
        var turret = MapDefinitions.TurretPositions["Turret_T2_C_05"];
        var start = new Position2(8419.7, 8533.2);
        var goal = new Position2(9010.6, 8429.0);
        TestAssert.True(grid.IsWalkable(start) && grid.IsWalkable(goal),
            "client-grid gate endpoints must be raw-walkable");
        TestAssert.True(grid.PointClearance(start) > 400.0,
            "client-grid gate start must remain open ground");
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var walker = Minion(401, start, MinionAiState.March);
        walker.Waypoints = [goal];
        world.Register(walker);
        var ux = (goal.X - start.X) / 600.0;
        var uz = (goal.Z - start.Z) / 600.0;
        var px = -uz;
        var pz = ux;
        var side = ((turret.X - start.X) * px + (turret.Z - start.Z) * pz) > 0.0 ? -1.0 : 1.0;
        var mid = new Position2(start.X + ux * 250.0, start.Z + uz * 250.0);
        var planted = Minion(402, new Position2(mid.X + px * side * 50.0,
            mid.Z + pz * side * 50.0), MinionAiState.March);
        planted.Waypoints.Clear();
        planted.Target = null;
        world.Register(planted);
        var plantedPosition = planted.Position;
        var nearest = double.PositiveInfinity;
        var frozen = 0;
        for (var tick = 0; tick < 60; tick++)
        {
            var before = walker.Position;
            movement.Tick(ClientDt);
            if (before == walker.Position) frozen++;
            nearest = Math.Min(nearest, Distance(walker.Position, turret));
        }
        TestAssert.True(Distance(walker.Position, start) > 400.0,
            $"client movement gate halted open-ground walker at {Distance(walker.Position, start):R}u");
        TestAssert.Equal(plantedPosition, planted.Position, "client-grid wedge-maker moved");
        TestAssert.True(nearest > MapDefinitions.TurretClientStampRadius,
            $"walker entered the client turret stamp: {nearest:R}u");
        TestAssert.True(frozen < 20, "client-grid route froze every movement frame");
    }

    private static Entity Minion(uint netId, Position2 position, MinionAiState state)
    {
        var entity = new Entity(netId, EntityKind.Minion, UnitType.Minion,
            Team.Order, position)
        {
            MinionType = MinionType.Melee,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = Speed,
            Radius = Radius,
            AttackRange = 170.0,
            AcquisitionRange = 450.0,
            Health = 500.0,
            MaxHealth = 500.0,
            MinionState = state,
        };
        return entity;
    }

    private static double Distance(Position2 left, Position2 right) =>
        Math.Sqrt((left.X - right.X) * (left.X - right.X) +
                  (left.Z - right.Z) * (left.Z - right.Z));

    private static double Length(Position2 value) => Distance(value, Position2.Zero);

    private sealed class SnapshotSink : IMovementDiagnosticSink
    {
        public bool Enabled => true;
        public List<MovementDiagnosticSnapshot> Snapshots { get; } = new();

        public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
            Snapshots.AddRange(snapshots);
    }
}
