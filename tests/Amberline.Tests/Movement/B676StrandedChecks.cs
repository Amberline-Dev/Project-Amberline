using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 676: a hero on a cell the raw navgrid allows but the movement view excludes (a building stamp: the Nexus and the inhibitors at their
/// pathfinding radius, the turrets at 140) is never stranded by the fallback engine (no usable copy of the client's routines; every headless
/// match without the helper). Headless seed 664 on the Basic Tutorial without the opening's Dragon (batch 675, set aside): Ashe's pursuit
/// chord grazed the corner of a stamped cell of HQ_T1 (it is tested from her exact position and walked from her 2-unit lattice anchor), she
/// stood one cell inside at (1569, 2173), and every later order planned nothing, so each was a stop on her own spot until the 1500 s cap.
/// The client's rules ("Hero route planning"): its A* seeds the start cell untested, so a hero
/// beside floor plans out; one lodged deeper gets no route, and the stop's arrival (and every still frame's update tail) puts it on floor
/// with the client.
/// </summary>
public static class B676StrandedChecks
{
    [Check("movement.b676-fallback-hero-plans-out-of-a-stamped-cell")]
    public static void PlansOutOfAStampedCell()
    {
        foreach (var map in new[] { GameMaps.Tutorial, GameMaps.Normal })
        {
            var (grid, nexus) = MapGrid(map);
            // Map 3: the reported cell. Summoner's Rift: the first stamped cell of HQ_T1 beside floor.
            var start = map == GameMaps.Tutorial ? new Position2(1569, 2173)
                : Cells(grid, nexus).Select(grid.CellToWorld).First(point => IsStamped(grid, point) && Neighbours(grid, point).Any(grid.IsMovementWalkable));
            TestAssert.True(IsStamped(grid, start) && !grid.IsPathingWalkable(start), $"map {map}: {start} is raw floor outside the pathing and movement views");
            Walk(grid, map, start, Goal(grid, map), orders: 1);
        }
    }

    [Check("movement.b676-fallback-hero-lodged-in-a-stamp-is-put-on-floor")]
    public static void LodgedHeroIsPutOnFloor()
    {
        foreach (var map in new[] { GameMaps.Tutorial, GameMaps.Normal })
        {
            var (grid, nexus) = MapGrid(map);
            // The first stamped raw-floor cell whose eight neighbours are all outside the movement view: HQ_T1's on map 3, the other
            // structures' and the turrets' (140) after it (Summoner's Rift's HQ_T1 stamp has none).
            var layout = MapLayout.For(GameDataPaths.Default.ForMap(map));
            var stamps = new[] { nexus }.Concat(layout.StructureStamps)
                .Concat(layout.Turrets.Select(turret => new NavigationStamp(turret.Name, turret.Position, MapDefinitions.TurretClientStampRadius)));
            var start = stamps.SelectMany(stamp => Cells(grid, stamp)).Select(grid.CellToWorld)
                .First(point => IsStamped(grid, point) && !Neighbours(grid, point).Any(grid.IsMovementWalkable));
            Walk(grid, map, start, Goal(grid, map), orders: 2);
        }
    }

    // A hero standing at start is ordered to goal (again after a stop, up to orders times) on a fallback engine, and walks there.
    private static void Walk(NavigationGrid grid, int map, Position2 start, Position2 goal, int orders)
    {
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var root = Path.Combine(Path.GetTempPath(), "league-b676-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // No EXE in the data root: the match uses the fallback.
            movement.StartEngine(new GameDataPaths(root), map, null);
            TestAssert.Equal(MovementEngine.Fallback, movement.Engine, "the match uses the fallback");
            var hero = world.Register(new Entity(0x4F00_6760, EntityKind.Hero, UnitType.Hero, Team.Order, start)
            {
                Alive = true, Radius = 35.0, Speed = 340.0,
            });
            for (var order = 1; ; order++)
            {
                var route = movement.SetMoveOrder(hero.NetId, goal);
                if (route.Count > 1) break;
                TestAssert.True(order < orders, FormattableString.Invariant(
                    $"map {map}: order {order} from {hero.Position} (started at {start}) plans nothing, a stop on the hero's own spot"));
                movement.TickHeroes();
                TestAssert.True(grid.IsMovementWalkable(hero.Position), FormattableString.Invariant(
                    $"map {map}: the stop puts the lodged hero on floor: {hero.Position}"));
                TestAssert.True(Distance(hero.Position, start) <= 24 * 31.0, FormattableString.Invariant(
                    $"map {map}: the client moves at most 24 steps: {hero.Position}"));
            }
            for (var frame = 0; frame < 4000 && hero.Waypoints.Count > 0; frame++) movement.TickHeroes();
            TestAssert.True(hero.Waypoints.Count == 0 && Distance(hero.Position, goal) < 3.0, FormattableString.Invariant(
                $"map {map}: the hero walks from {start} to {goal}: at {hero.Position}, {hero.Waypoints.Count} waypoints left"));
        }
        finally
        {
            movement.StopEngine();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // The map's own grid with its own building stamps, and its Order nexus's stamp.
    private static (NavigationGrid Grid, NavigationStamp Nexus) MapGrid(int map)
    {
        var paths = GameDataPaths.Default.ForMap(map);
        var layout = MapLayout.For(paths);
        var grid = NavigationGrid.Load(paths, new NavigationGridSettings { Turrets = layout.Turrets, Structures = layout.StructureStamps });
        return (grid, layout.StructureStamps.Single(stamp => stamp.Name == "HQ_T1"));
    }

    // Floor well away from the Order base: map 3's middle, Summoner's Rift's mid lane.
    private static Position2 Goal(NavigationGrid grid, int map)
    {
        var goal = grid.NearestMovementFloor(map == GameMaps.Tutorial ? new Position2(5000, 5000) : new Position2(4060, 4060), maxCells: 4);
        TestAssert.True(goal is not null, $"map {map}: the goal is on floor");
        return goal!.Value;
    }

    // The cells of the stamp's square, row by row.
    private static IEnumerable<NavCell> Cells(NavigationGrid grid, NavigationStamp stamp)
    {
        var centre = grid.WorldToCell(stamp.Center);
        var reach = (int)(stamp.Radius / grid.CellSize) + 1;
        for (var row = centre.Row - reach; row <= centre.Row + reach; row++)
        for (var column = centre.Column - reach; column <= centre.Column + reach; column++)
            yield return new NavCell(column, row);
    }

    private static bool IsStamped(NavigationGrid grid, Position2 point) => grid.IsWalkable(point) && !grid.IsMovementWalkable(point);

    private static IEnumerable<Position2> Neighbours(NavigationGrid grid, Position2 point)
    {
        var cell = grid.WorldToCell(point);
        for (var dr = -1; dr <= 1; dr++)
        for (var dc = -1; dc <= 1; dc++)
            if (dr != 0 || dc != 0) yield return grid.CellToWorld(new NavCell(cell.Column + dc, cell.Row + dr));
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
