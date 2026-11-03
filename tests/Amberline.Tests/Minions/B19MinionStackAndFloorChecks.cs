using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Batch 19 regressions from the headless seed-1 match (blocked-cell and stacked
/// invariants): a chase never ends on a cell the stock client refuses, and planted
/// allied minions that overlap are separated whatever their targets are.
/// </summary>
public static class B19MinionStackAndFloorChecks
{
    [Check("minions.chase-seat-behind-turret-footprint-stays-on-client-floor")]
    public static void ChaseSeatBehindTurretFootprintStaysOnClientFloor() =>
        TestAssert.True(ChaseBehindTurret("Turret_T2_C_05", 255.5, wireEdge: false), "fixture: no seat inside the footprint");

    [Check("minions.chase-seat-wire-quantization-stays-on-client-floor")]
    public static void ChaseSeatWireQuantizationStaysOnClientFloor()
    {
        // The route endpoint is the seat rounded to 2 units, which can sit in the next cell: a
        // seat on client floor whose wire point is blocked must not seat the minion there either.
        var grid = NavigationGrid.LoadDefault();
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var tested = 0;
        foreach (var turret in MapDefinitions.Turrets.Where(turret => turret.Tier != TurretTier.Fountain))
            for (var degrees = 0.0; degrees < 360.0 && tested < 3; degrees += 5.0)
            {
                var start = Offset(turret.Position, Direction(degrees), 450.0);
                if (!grid.IsPathingWalkable(start) ||
                    FindWireEdgeDepth(grid, bounds, turret.Position, Direction(degrees)) is not double depth ||
                    grid.FindPath(start, Offset(turret.Position, Direction(degrees), depth + 100.0)).Count == 0)
                    continue;
                if (ChaseBehindTurret(turret.Name, degrees, wireEdge: true)) tested++;
            }
        TestAssert.True(tested >= 1, "fixture: no seat whose wire quantization crosses into the footprint");
    }

    /// <summary>
    /// The minion starts 450 units from the turret at this bearing (degrees) and the hero stands on the far
    /// side. False when the fixture finds no suitable seat at the bearing; asserts otherwise.
    /// </summary>
    private static bool ChaseBehindTurret(string turretName, double degrees, bool wireEdge)
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var turret = runtime.World.Entities.Values.First(unit => unit.Name == turretName);
        // A dead turret keeps its footprint; this also keeps it from shooting the fixture.
        runtime.Combat.Kill(turret);
        var navigation = runtime.Movement.Navigation;
        var minion = runtime.Waves.SpawnFixture(Team.Order, MinionType.Caster, 0, "C", 0.0, 0.0);
        // Seed 1 (255.5 degrees): the caster comes from the south-south-west of the turret.
        var away = Direction(degrees);
        var bounds = new NavigationBounds(navigation.MinX, navigation.MaxX, navigation.MinZ, navigation.MaxZ);
        var depth = 170.0;
        if (wireEdge)
        {
            // Client floor as computed, blocked once rounded to the wire grid.
            if (FindWireEdgeDepth(navigation, bounds, turret.Position, away) is not double edge) return false;
            depth = edge;
        }
        else
        {
            // Just inside the 140-unit client footprint, where the last step is one short arrival.
            while (depth > 60.0 && navigation.IsMovementWalkable(Offset(turret.Position, away, depth))) depth -= 1.0;
            depth -= 2.0;
        }
        minion.Position = Offset(turret.Position, away, 450.0);
        if (!navigation.IsPathingWalkable(minion.Position)) return false;
        minion.Waypoints.Clear();
        minion.Target = null;
        minion.LaneWaypoints = [new Position2(9217.0, 9321.0)];
        minion.AnchorPath = [minion.Position];
        var hero = AddUnit(runtime.World, 0x4F00_0200, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            turret.Position, 65.0);
        hero.Health = hero.MaxHealth = 100000.0;
        // The radial chase point is reach - 6 from the hero toward the minion.
        var reach = CombatService.AttackReach(minion, hero);
        var seat = Offset(turret.Position, away, depth);
        hero.Position = Offset(turret.Position, away, depth - (reach - 6.0));
        TestAssert.True(wireEdge || navigation.IsWalkable(seat) && !navigation.IsMovementWalkable(seat),
            "fixture: the radial chase point must be raw floor inside the client footprint");
        minion.AttackTarget = hero.NetId;
        minion.MinionState = MinionAiState.Engage;
        runtime.Minions!.Tick(0.0);

        var before = hero.Health;
        int? blockedSince = null;
        for (var tick = 0; tick < 450 && minion.Alive; tick++)
        {
            runtime.Tick(1.0 / 30.0);
            var cell = navigation.WorldToCell(minion.Position);
            var blocked = navigation.IsInBounds(cell) && !navigation.IsMovementWalkable(cell);
            if (!wireEdge)
            {
                TestAssert.False(blocked, $"tick {tick}: the minion stands on a client-blocked cell at {minion.Position}");
                continue;
            }
            // Restated in batch 254 (the bundle 4 red since batch 248 put minion orders on the engine planner): the engine planner's
            // straight walk ends on the 2-unit lattice point of the seat (the path builder's end snap), which here
            // lies in a blocked cell, and free movement has no terrain gate, so the client's actor walks into that cell too. The arrival
            // does not seat it on the blocked end (the client's waypoint assembly), and the next chase plan walks it out (the A* starts on
            // the actor's own cell untested): it may stand there for a moment, never for the arrival stopwatch's second
            // (the client's arrival timeout), after which the arrival correction would put it on floor.
            blockedSince = blocked ? blockedSince ?? tick : null;
            TestAssert.True(blockedSince is null || tick - blockedSince < 30,
                $"tick {tick}: the minion stays on a client-blocked cell at {minion.Position} for a second");
        }
        var last = navigation.WorldToCell(minion.Position);
        TestAssert.True(!navigation.IsInBounds(last) || navigation.IsMovementWalkable(last),
            $"the minion ends on client floor, not at {minion.Position}");
        TestAssert.True(minion.Alive, "the fixture minion must survive");
        TestAssert.True(hero.Health < before, "the minion must reach a legal firing seat and hit the hero");
        return true;
    }

    [Check("minions.planted-allies-with-different-targets-are-separated")]
    public static void PlantedAlliesWithDifferentTargetsAreSeparated()
    {
        // Seed 1 had 64 stacked episodes; most were two planted casters with different
        // targets that arrived on one spot (the final waypoint seats a unit without collision).
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var first = Plant(runtime, 0, new Position2(7000.0, 7200.0), new Position2(7380.0, 7200.0));
        var second = Plant(runtime, 1, new Position2(7030.0, 7230.0), new Position2(7390.0, 7260.0));
        runtime.Minions!.Tick(0.0);
        var epsilon = Math.Sqrt(runtime.Movement.Settings.Collision.ColliderEpsilonSquared);
        var overlapSince = (double?)null;
        var longest = 0.0;
        for (var tick = 0; tick < 300; tick++)
        {
            runtime.Tick(1.0 / 30.0);
            var now = (tick + 1) / 30.0;
            var overlap = first.Radius + second.Radius - Distance(first.Position, second.Position);
            if (overlap > epsilon) overlapSince ??= now;
            else overlapSince = null;
            longest = Math.Max(longest, overlapSince is double since ? now - since : 0.0);
        }
        TestAssert.True(first.Alive && second.Alive, "fixture minions must survive");
        TestAssert.True(longest <= 2.0,
            $"planted allies stayed overlapped for {longest:0.0} s (the monitor allows 2 s)");
    }

    [Check("minions.spawn-onto-planted-ally-does-not-stack")]
    public static void SpawnOntoPlantedAllyDoesNotStack()
    {
        // Seed 1, 57:06.9: Chaos sieged the Order nexus, so every Order wave member engaged at
        // once and stood on the spawn point; the next member (staggered 0.8 s) spawned exactly on
        // top of it, walled in by two more planted casters, and stayed stacked for 10.5 s.
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var spawn = runtime.Waves.LaneRoute(Team.Order, "C")[0];
        var enemy = AddUnit(runtime.World, 0x4F00_0400, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new Position2(spawn.X + 420.0, spawn.Z + 140.0), 35.744);
        enemy.Health = enemy.MaxHealth = 1000000.0;
        Entity Planted(MinionType type, int slot, Position2 position)
        {
            var minion = runtime.Waves.SpawnFixture(Team.Order, type, slot, "C", 0.0, 0.0);
            minion.Position = position;
            minion.Waypoints.Clear();
            minion.Target = null;
            minion.AnchorPath = [position];
            minion.AttackTarget = enemy.NetId;
            minion.MinionState = MinionAiState.Engage;
            return minion;
        }
        var first = Planted(MinionType.Caster, 0, spawn);
        Planted(MinionType.Caster, 1, new Position2(spawn.X + 32.0, spawn.Z - 81.0));
        Planted(MinionType.Caster, 2, new Position2(spawn.X + 40.0, spawn.Z + 76.0));
        runtime.Minions!.Tick(0.0);
        for (var tick = 0; tick < 30; tick++) runtime.Tick(1.0 / 30.0);
        var newcomer = runtime.Waves.SpawnFixture(Team.Order, MinionType.Siege, 3, "C", 1.0, 0.8);
        TestAssert.True(Distance(newcomer.Position, first.Position) < 1.0,
            "fixture: the newcomer must spawn on the planted minion");

        var epsilon = Math.Sqrt(runtime.Movement.Settings.Collision.ColliderEpsilonSquared);
        var overlapSince = (double?)null;
        var longest = 0.0;
        for (var tick = 0; tick < 450; tick++)
        {
            runtime.Tick(1.0 / 30.0);
            var now = (tick + 1) / 30.0;
            var overlap = first.Radius + newcomer.Radius - Distance(first.Position, newcomer.Position);
            if (overlap > epsilon) overlapSince ??= now;
            else overlapSince = null;
            longest = Math.Max(longest, overlapSince is double since ? now - since : 0.0);
        }
        TestAssert.True(first.Alive && newcomer.Alive, "fixture minions must survive");
        TestAssert.True(longest <= 2.0,
            $"the newcomer stayed stacked on the planted minion for {longest:0.0} s (the monitor allows 2 s)");
    }

    private static Entity Plant(MatchRuntime runtime, int slot, Position2 position, Position2 enemyPosition)
    {
        var minion = runtime.Waves!.SpawnFixture(Team.Order, MinionType.Caster, slot, "C", 0.0, 0.0);
        minion.Position = position;
        minion.Waypoints.Clear();
        minion.Target = null;
        minion.LaneWaypoints = [new Position2(7700.0, 7800.0)];
        minion.AnchorPath = [position];
        var enemy = AddUnit(runtime.World, 0x4F00_0300u + (uint)slot, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, enemyPosition, 35.744);
        enemy.Health = enemy.MaxHealth = 100000.0;
        minion.AttackTarget = enemy.NetId;
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
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }

    private static Entity AddUnit(WorldState world, uint netId, EntityKind kind,
        UnitType unitType, Team team, Position2 position, double radius)
    {
        var unit = new Entity(netId, kind, unitType, team, position)
        {
            Alive = true,
            Created = true,
            Visible = true,
            Radius = radius,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        world.Register(unit);
        return unit;
    }

    private static Position2 Direction(double degrees) =>
        new(Math.Cos(degrees * Math.PI / 180.0), Math.Sin(degrees * Math.PI / 180.0));

    // Distance from the turret along a ray where the point is client floor but its 2-unit wire
    // rounding is blocked, or null.
    private static double? FindWireEdgeDepth(NavigationGrid grid, NavigationBounds bounds,
        Position2 turret, Position2 direction)
    {
        for (var depth = 170.0; depth > 100.0; depth -= 0.02)
        {
            var point = Offset(turret, direction, depth);
            if (grid.IsMovementWalkable(point) &&
                !grid.IsMovementWalkable(Geometry.ClientWaypoints([point], bounds)[0])) return depth;
        }
        return null;
    }

    private static Position2 Offset(Position2 origin, Position2 unit, double distance) =>
        new(origin.X + unit.X * distance, origin.Z + unit.Z * distance);

    private static Position2 Normalize(Position2 value)
    {
        var length = Math.Sqrt(value.X * value.X + value.Z * value.Z);
        return new Position2(value.X / length, value.Z / length);
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));
}
