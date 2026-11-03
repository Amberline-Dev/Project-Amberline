using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 236: a minion's route is planned the way the engine plans every unit's order (the client's order handler and path builder,
/// whose A* closes the cells of a unit that stands still, the client's stuck-actor test), and the solver's stuck site asks for a new path.
/// </summary>
public static class B236MinionRouteChecks
{
    private const uint EnemyId = 0x4F00_2360, AllyId = 0x4F00_2361, WallId = 0x4F00_2370;

    [Check("minions.chase-route-goes-around-a-planted-ally")]
    public static void ChaseRouteGoesAroundAPlantedAlly()
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var enemy = AddUnit(runtime.World, EnemyId, Team.Chaos, new Position2(7700.0, 7200.0));
        enemy.Health = enemy.MaxHealth = 1000000.0;
        // A caster planted on the straight line, firing at the same enemy (500 units, inside its reach).
        var ally = Minion(runtime, MinionType.Caster, 0, new Position2(7200.0, 7200.0), enemy);
        var mover = Minion(runtime, MinionType.Melee, 1, new Position2(7000.0, 7200.0), enemy);
        runtime.Minions!.Tick(0.0);

        TestAssert.True(mover.Waypoints.Count > 0, "the melee minion must close on its target");
        var body = new NavigationObstacle(ally.Position, mover.Radius + ally.Radius);
        var route = new[] { mover.Position }.Concat(mover.Waypoints).ToArray();
        for (var index = 0; index + 1 < route.Length; index++)
            TestAssert.True(runtime.Movement.Navigation.SegmentClearOfUnits(route[index], route[index + 1], [body], escape: false),
                $"the chase route crosses the planted ally's body: {string.Join(" ", route)}");
    }

    [Check("minions.stuck-mover-repaths-when-a-body-stands-on-its-end-point")]
    public static void StuckMoverRepathsWhenABodyStandsOnItsEndPoint()
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        runtime.Minions!.SiegeRoutingEnabled = false;
        // A minion walks to a point a body (no AI of its own) stands on. Its last waypoint is never bent, and no step brings it closer
        // than the two radii, so every frame is refused for no progress and the stuck site asks for a path. The engine's plan ends on
        // the free cell nearest the point. Without it, the mover keeps walking at the point until the 45-frame give-up lets it walk in.
        var end = new Position2(7400.0, 7200.0);
        var mover = runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0);
        mover.Position = new Position2(7000.0, 7200.0);
        mover.Target = null;
        mover.Waypoints = [end];
        mover.AnchorPath = [mover.Position, end];
        mover.LaneWaypoints.Clear();
        mover.MinionState = MinionAiState.March;
        var body = AddUnit(runtime.World, WallId, Team.Order, new Position2(end.X - 10.0, end.Z), mover.Radius);
        var overlapped = 0.0;
        for (var tick = 0; tick < 240; tick++)
        {
            runtime.Tick(1.0 / 30.0);
            overlapped = Math.Max(overlapped, mover.Radius + body.Radius - Distance(mover.Position, body.Position));
        }
        TestAssert.True(overlapped <= 12.0,
            $"the mover walked {overlapped:0.0} units into the body on its end point (give-up counter {mover.RepathCount}, at {mover.Position})");
        // The engine's actor test closes the cells within the body's path radius plus max(R - 1.8 half cell, 15) of its centre, so the
        // new end is the nearest cell outside that disc (it can still be inside the two physical radii: that is the shipped test).
        var route = string.Join(" ", mover.Waypoints);
        TestAssert.True(mover.Waypoints.Count == 0 || Distance(mover.Waypoints[^1], end) > 1.0,
            $"after 8 s the stuck mover still walks to the point the body stands on ({route}, give-up counter {mover.RepathCount})");
    }

    [Check("minions.lane-return-from-a-blocked-cell-starts-at-once")]
    public static void LaneReturnFromABlockedCellStartsAtOnce()
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var navigation = runtime.Movement.Navigation;
        // Seed 1 (batch 236 trace, t=750): an Order caster stood on this client-blocked cell beside its mid turret, where the
        // avoidance steer (batch 225, item 2) had walked it. Its target dies: minionLib OnTargetLost runs FindTargetOrMove, which with
        // no target is SetStateAndMoveToForwardNav, the forward navigation planned from where the unit stands.
        var start = new Position2(5566.5, 6093.6);
        TestAssert.False(navigation.IsMovementWalkable(start), "fixture: the caster stands on a client-blocked cell");
        var dummy = runtime.Waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0.0, 0.0);
        dummy.Position = new Position2(5900.0, 6400.0);
        dummy.Waypoints.Clear();
        dummy.Target = null;
        dummy.AnchorPath = [dummy.Position];
        var caster = Minion(runtime, MinionType.Caster, 1, start, dummy);
        caster.LaneWaypoints = runtime.Waves.LaneRoute(Team.Order, "C").ToList();
        runtime.Minions!.Tick(0.0);

        TestAssert.True(runtime.Combat.Kill(dummy), "the dummy dies");
        TestAssert.True(caster.AttackTarget is null && caster.MinionState == MinionAiState.March,
            "the lethal edge returns the caster to its lane");
        TestAssert.True(caster.Waypoints.Count > 0,
            "the caster on a blocked cell must get its forward route at the lethal edge, not after a wait");
        for (var tick = 0; tick < 15; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(Distance(caster.Position, start) > 10.0,
            $"the caster must walk off the blocked cell at once (at {caster.Position})");
    }

    private static Entity Minion(MatchRuntime runtime, MinionType type, int slot, Position2 position, Entity target)
    {
        var minion = runtime.Waves!.SpawnFixture(Team.Order, type, slot, "C", 0.0, 0.0);
        minion.Position = position;
        minion.Waypoints.Clear();
        minion.Target = null;
        minion.LaneWaypoints = [new Position2(9000.0, 9200.0)];
        minion.AnchorPath = [position];
        minion.AttackTarget = target.NetId;
        minion.MinionState = MinionAiState.Engage;
        return minion;
    }

    private static MatchRuntime StartSolo()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }

    private static Entity AddUnit(WorldState world, uint netId, Team team, Position2 position, double radius = 35.744)
    {
        var unit = new Entity(netId, EntityKind.Minion, UnitType.Minion, team, position)
        {
            Alive = true, Created = true, Visible = true, Radius = radius, Health = 1000.0, MaxHealth = 1000.0,
        };
        world.Register(unit);
        return unit;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));
}
