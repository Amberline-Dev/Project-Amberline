using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;

namespace Amberline.Tests;

public static class NavigationChecks
{
    [Check("movement-b6-wall-click-reachable-score")]
    public static void WallClickSearchesBeyondDisconnectedFirstFourCells()
    {
        // PATH-02: the old column/row scan consumes these four isolated cells.
        // The client's path search scores start distance AND click distance, continuing
        // for four spiral legs after a reachable candidate, with <= ties.
        var floor = new HashSet<NavCell>
        {
            new(9, 9), new(9, 10), new(9, 11), new(10, 9),
        };
        for (var column = 12; column <= 22; column++) floor.Add(new(column, 10));
        var walls = (from column in Enumerable.Range(0, 30)
                     from row in Enumerable.Range(0, 30)
                     where !floor.Contains(new NavCell(column, row))
                     select (column, row, flags: (ushort)2, height: 0f)).ToArray();
        using var stream = BuildGrid(30, 30, 10, walls);
        var grid = NavigationGrid.Load(stream, new NavigationGridSettings
        {
            CollisionRadius = 0, TurretPathStampRadius = 0, TurretClientStampRadius = 0,
            Turrets = [], Structures = [],
        });
        var start = grid.CellToWorld(new NavCell(22, 10));
        var click = grid.CellToWorld(new NavCell(10, 10));
        var route = grid.FindHeroPath(start, click);
        TestAssert.True(route.Count > 1, "disconnected nearest cells do not hide the reachable corridor");
        TestAssert.Equal(new NavCell(12, 10), grid.WorldToCell(route[^1]),
            "the nearest reachable click score cannot be replaced by a farther cell nearer the start");
        for (var leg = 0; leg + 1 < route.Count; leg++)
            TestAssert.True(grid.HasHeroMovementLineOfSight(route[leg], route[leg + 1]),
                "selected route retains the movement-floor validator");

        // First spiral success is east. With start (4,10), west improves the
        // start score. With start (4,4), (9,10) and (10,9) both score (61,1):
        // the native east/+row/west/-row spiral visits (10,9) later, so it wins.
        using var openStream = BuildGrid(30, 30, 10, (10, 10, (ushort)2, 0f));
        var open = NavigationGrid.Load(openStream, new NavigationGridSettings
        {
            CollisionRadius = 0, TurretPathStampRadius = 0, TurretClientStampRadius = 0,
            Turrets = [], Structures = [],
        });
        foreach (var (origin, expected) in new[]
        {
            (new NavCell(4, 10), new NavCell(9, 10)),
            (new NavCell(4, 4), new NavCell(10, 9)),
        })
        {
            var western = open.FindHeroPath(open.CellToWorld(origin), click);
            TestAssert.True(western.Count > 1);
            TestAssert.Equal(expected, open.WorldToCell(western[^1]),
                "both scores must improve or tie; equal scores select the later spiral cell");
        }
    }

    [Check("movement-b6-wall-click-disconnected-search-bound")]
    public static void WallClickExpandsTheReachableComponentOnlyOnce()
    {
        // A two-cell wall separates the 23x76 start component from a large
        // pocket surrounding the click. Every pocket candidate has incoming
        // edges, so the old per-candidate A* exhausts the start component.
        var walls = (from column in Enumerable.Range(0, 80)
                     from row in Enumerable.Range(0, 80)
                     where row < 2 || row > 77 || column < 2 || column > 75 ||
                           column is 25 or 26 || (column == 40 && row == 40)
                     select (column, row, flags: (ushort)2, height: 0f)).ToArray();
        using var stream = BuildGrid(80, 80, 10, walls);
        var grid = NavigationGrid.Load(stream, new NavigationGridSettings
        {
            CollisionRadius = 0, TurretPathStampRadius = 0, TurretClientStampRadius = 0,
            Turrets = [], Structures = [],
        });
        var route = grid.FindHeroPath(grid.CellToWorld(new NavCell(4, 40)),
            grid.CellToWorld(new NavCell(40, 40)));
        TestAssert.True(route.Count > 1);
        TestAssert.Equal(new NavCell(24, 40), grid.WorldToCell(route[^1]),
            "native dual-distance scoring selects the closest reachable wall edge");
        TestAssert.Equal(1L, grid.ReachabilitySearches, "one component search for this route start");
        TestAssert.Equal(1L, grid.AStarSearches, "only the selected endpoint needs A*");
        TestAssert.True(grid.PathSearchExpansions <= 2L * 23 * 76,
            "flood fill plus selected A* expand at most twice the 23x76 reachable component");
        for (var leg = 0; leg + 1 < route.Count; leg++)
            TestAssert.True(grid.HasHeroMovementLineOfSight(route[leg], route[leg + 1]));
    }

    [Check("navigation.b20-blocked-click-floods-only-as-far-as-needed")]
    public static void WallClickExpandsOnlyTheCellsItNeeds()
    {
        // Batch 20: a click on one wall cell beside the start flooded the whole 98x98 open component
        // before choosing the reachable cell next to it (up to 73 ms on the live map, sixteen times per
        // hero approach and per minion engage). The answer is the same; the flood stops when it has it.
        using var stream = BuildGrid(100, 100, 10, [(14, 40, (ushort)2, 0f)]);
        var grid = NavigationGrid.Load(stream, new NavigationGridSettings
        {
            CollisionRadius = 0, TurretPathStampRadius = 0, TurretClientStampRadius = 0,
            Turrets = [], Structures = [],
        });
        grid.ResetPathQueryCounters();
        var route = grid.FindHeroPath(grid.CellToWorld(new NavCell(10, 40)),
            grid.CellToWorld(new NavCell(14, 40)));
        TestAssert.True(route.Count > 1, "the blocked click still gets a route");
        var end = grid.WorldToCell(route[^1]);
        TestAssert.True(Math.Abs(end.Column - 14) <= 1 && Math.Abs(end.Row - 40) <= 1,
            "the route ends on the reachable cell beside the wall cell");
        TestAssert.Equal(1L, grid.ReachabilitySearches, "one component search for this route start");
        TestAssert.True(grid.PathSearchExpansions < 600L,
            $"the component search stops at the answer instead of flooding 98x98 cells: {grid.PathSearchExpansions}");
    }

    [Check("vision-b6-half-cell-world-ray")]
    public static void VisionSamplesWorldRayWithoutChangingMovementLos()
    {
        using var stream = BuildGrid(4, 4, 50.0, (1, 0, (ushort)0x02, 0.0f));
        var grid = NavigationGrid.Load(stream, new NavigationGridSettings
        {
            CollisionRadius = 0.0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        });
        // VIS-01: offset the report's (49,1)->(101,101) witness by the
        // synthetic grid's (-25,-25) origin. First half-cell sample hits (1,0).
        var source = new Position2(24, -24);
        var target = new Position2(76, 76);
        TestAssert.False(grid.HasVisionLos(source, target), "stock ray samples the corner wall");
        TestAssert.True(grid.HasMovementLineOfSight(source, target), "movement retains Bresenham");
        TestAssert.True(grid.HasPathingLineOfSight(source, target), "pathfinding retains Bresenham");
    }

    [Check("navigation-map1-definitions")]
    public static void Map1DefinitionsCheck()
    {
        TestAssert.Equal(24, MapDefinitions.Turrets.Count);
        TestAssert.Equal(6, MapDefinitions.Inhibitors.Count);
        TestAssert.Equal(2, MapDefinitions.Nexuses.Count);
        TestAssert.Equal(19, MapDefinitions.LanePaths["L"].Count);
        TestAssert.Equal(19, MapDefinitions.LanePaths["C"].Count);
        TestAssert.Equal(19, MapDefinitions.LanePaths["R"].Count);
        TestAssert.Equal(0xFFF97DB5u, MapDefinitions.BuildingNetId("HQ_T1"));
        TestAssert.Equal(0xFFF02C0Fu, MapDefinitions.BuildingNetId("HQ_T2"));
        TestAssert.Equal(Team.Order, MapDefinitions.Turrets[0].Team);
        TestAssert.Equal(12, MapDefinitions.Turrets.Count(turret => turret.Team == Team.Order));
        TestAssert.Equal(12, MapDefinitions.Turrets.Count(turret => turret.Team == Team.Chaos));
        TestAssert.Near(1341.632568359375, MapDefinitions.TurretPositions["Turret_T1_C_01"].X, 1e-9);
        TestAssert.Near(12442.701171875, MapDefinitions.TurretPositions["Turret_T2_C_01"].Z, 1e-9);
        TestAssert.Equal(6, MapDefinitions.TurretTiers.Count(tier => tier.Value == TurretTier.Inhibitor));
        TestAssert.Equal(6, MapDefinitions.TurretTiers.Count(tier => tier.Value == TurretTier.Inner));
        TestAssert.Equal(6, MapDefinitions.TurretTiers.Count(tier => tier.Value == TurretTier.Outer));
        TestAssert.Equal(4, MapDefinitions.TurretTiers.Count(tier => tier.Value == TurretTier.Nexus));
        TestAssert.Equal(2, MapDefinitions.TurretTiers.Count(tier => tier.Value == TurretTier.Fountain));
        TestAssert.Equal(TurretTier.Fountain,
            MapDefinitions.Turrets.Single(turret => turret.Name == "Turret_OrderTurretShrine").Tier);
        TestAssert.Equal(0xFFD23C3Eu, MapDefinitions.Inhibitors.Single(i => i.Name == "Barracks_T1_L1").NetId);
        TestAssert.Equal(0xFFFF8F1Fu, MapDefinitions.Inhibitors.Single(i => i.Name == "Barracks_T2_C1").NetId);
        TestAssert.Equal(new LaneSpawnApproach(2, 1), MapDefinitions.LaneSpawnApproaches["L"]);
        TestAssert.Equal(new LaneSpawnApproach(2, 2), MapDefinitions.LaneSpawnApproaches["C"]);
        TestAssert.Equal(new LaneSpawnApproach(1, 2), MapDefinitions.LaneSpawnApproaches["R"]);
        TestAssert.Near(91.4874,
            MapDefinitions.MidLaneMinClearanceFromData(type => type == MinionType.Melee ? 35.7437 : 55.7437),
            1e-9);
    }

    // P3, 18 October 2026: the client stamps every turret (140 units), the Nexus and each inhibitor (their pathfinding radius) into its
    // nav cells with flag 0x42 through the client's filled-circle flag writer: a cell is stamped when its centre lies within the
    // radius of the building. An independent double-precision recount against the movement, hero-pathing and strategic views of the real
    // Map1 grid (cells within 0.01 of a boundary are left out: the client counts in float32). Written against the code, not run.
    [Check("navigation.building-stamps-are-the-client-circle")]
    public static void BuildingStampsAreTheClientCircle()
    {
        var grid = NavigationGrid.LoadDefault();
        var circles = new List<(Position2 Center, double Radius)>();
        foreach (var turret in MapDefinitions.Turrets) circles.Add((turret.Position, MapDefinitions.TurretClientStampRadius));
        foreach (var structure in MapDefinitions.StructureStamps) circles.Add((structure.Center, structure.Radius));
        var stamped = 0;
        for (var column = 0; column < grid.CountX; column++)
        for (var row = 0; row < grid.CountY; row++)
        {
            var cell = new NavCell(column, row);
            if (!grid.IsWalkable(cell)) continue;
            var centre = grid.CellToWorld(cell);
            var inside = false;
            var outside = true;
            foreach (var (center, radius) in circles)
            {
                var distance = Math.Sqrt((centre.X - center.X) * (centre.X - center.X) + (centre.Z - center.Z) * (centre.Z - center.Z));
                if (distance <= radius - 0.01) inside = true;
                if (distance <= radius + 0.01) outside = false;
            }
            if (inside)
            {
                stamped++;
                TestAssert.False(grid.IsMovementWalkable(cell), $"cell {cell} is inside a building stamp: movement view");
                TestAssert.False(grid.IsHeroPathingWalkable(cell), $"cell {cell} is inside a building stamp: hero planning view");
                TestAssert.False(grid.IsPathingWalkable(cell), $"cell {cell} is inside a building stamp: strategic view");
            }
            else if (outside)
                TestAssert.True(grid.IsMovementWalkable(cell), $"cell {cell} lies outside every building stamp: movement view");
        }
        TestAssert.True(stamped > 0, "the buildings stamp floor cells");
        // The Order Nexus stamps floor cells beside its mesh walls; the raw view stays terrain.
        var nexus = MapDefinitions.StructureStamps.Single(entry => entry.Name == "HQ_T1");
        var ring = grid.WorldToCell(new Position2(nexus.Center.X + nexus.Radius - 30.0, nexus.Center.Z));
        TestAssert.False(grid.IsMovementWalkable(ring), "a cell just inside the Order Nexus stamp is blocked");
    }

    // The client clamps a stamped sample outside the grid onto the border cell. The synthetic grid
    // spans x -25..65 and z -25..65 with 10-unit cells. A turret at (-30, 20) with the client radius 5 reaches the sample of column -1
    // (its centre (-30, 20) is 0 away), whose clamp writes border cell (0, 4); that cell's own centre (-20, 20) is 10 away, outside.
    [Check("navigation.stamp-clamps-out-of-grid-samples-to-border")]
    public static void StampClampsOutOfGridSamplesToBorder()
    {
        var settings = new NavigationGridSettings
        {
            CollisionRadius = 10.0,
            TurretPathStampRadius = 15.0,
            TurretClientStampRadius = 5.0,
            Turrets = [new TurretDefinition("test", Team.Order, new Position2(-30.0, 20.0), TurretTier.Outer)],
            Structures = [],
        };
        using var stream = BuildGrid(9, 9, 10.0);
        var grid = NavigationGrid.Load(stream, settings);
        TestAssert.Equal(new NavCell(0, 4), grid.WorldToCell(new Position2(-20.0, 20.0)));
        TestAssert.False(grid.IsMovementWalkable(new NavCell(0, 4)), "the clamped stamp blocks the border cell");
        TestAssert.True(grid.IsMovementWalkable(new NavCell(0, 0)), "a border cell far from the turret stays floor");
    }

    // The client's chord walk tests only the NotPassable bit 0x02 and the bodies, so a hero chord may pass a wall
    // at any distance above zero (batch 223 item 1; the 25-unit margin and the 65-unit dilation are retired from chords). The wall is
    // column 10, rows 4..14 (its top edge at z = 125). The straight chord from (0, 142) to (170, 142) passes 17 units above that edge:
    // inside the 65-unit dilation, outside every blocked cell. The dilated grid walls the whole strip off, so only a movement-view chord
    // can be the direct route.
    [Check("navigation.hero-chord-uses-the-client-chord-view")]
    public static void HeroChordUsesTheClientChordView()
    {
        var settings = new NavigationGridSettings
        {
            CollisionRadius = 65.0,
            TurretPathStampRadius = 0.0,
            TurretClientStampRadius = 0.0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        };
        var wall = new List<(int column, int row, ushort flags, float height)>();
        for (var row = 4; row <= 14; row++) wall.Add((10, row, (ushort)0x02, 0.0f));
        using var stream = BuildGrid(20, 20, 10.0, wall.ToArray());
        var grid = NavigationGrid.Load(stream, settings);
        var start = new Position2(0.0, 142.0);
        var goal = new Position2(170.0, 142.0);
        TestAssert.True(grid.HasHeroMovementLineOfSight(start, goal), "the chord is clear of every NotPassable cell");
        TestAssert.False(grid.HasHeroPathingLineOfSight(start, goal), "the 65-unit dilated view walls the chord off");
        var route = grid.FindHeroPath(start, goal);
        TestAssert.Equal(2, route.Count);
        TestAssert.Equal(start, route[0]);
        TestAssert.True(Math.Sqrt(Math.Pow(route[1].X - goal.X, 2) + Math.Pow(route[1].Z - goal.Z, 2)) <= 2.9,
            "the direct chord ends at the goal's wire point");
    }

    // The strategic (NPC route) view carries the client's turret footprint, not a 260-unit disc (batch 223 item 3; commit e7f2ffa
    // had widened it for minion waves). On open floor, the cell (0, 0) lies 56.6 units from the turret: floor for a 15-unit stamp,
    // blocked by a 260-unit one. The cells within 15 units of the turret stay blocked in every view.
    [Check("navigation.strategic-view-uses-the-client-turret-stamp")]
    public static void StrategicViewUsesTheClientTurretStamp()
    {
        var settings = new NavigationGridSettings
        {
            CollisionRadius = 0.0,
            TurretPathStampRadius = 260.0,
            TurretClientStampRadius = 15.0,
            Turrets = [new TurretDefinition("test", Team.Order, new Position2(20.0, 20.0), TurretTier.Outer)],
            Structures = [],
        };
        using var stream = BuildGrid(9, 9, 10.0);
        var grid = NavigationGrid.Load(stream, settings);
        TestAssert.True(grid.IsPathingWalkable(new NavCell(0, 0)), "the strategic view keeps floor 56.6 units from a turret");
        TestAssert.True(grid.IsMovementWalkable(new NavCell(0, 0)), "the movement view keeps floor 56.6 units from a turret");
        TestAssert.False(grid.IsPathingWalkable(new NavCell(3, 4)), "a cell 10 units from the turret is inside its client footprint");
        TestAssert.False(grid.IsMovementWalkable(new NavCell(3, 4)), "the movement view blocks the same footprint cell");
    }

    [Check("navigation-synthetic")]
    public static void SyntheticGrid()
    {
        var settings = new NavigationGridSettings
        {
            CollisionRadius = 10.0,
            TurretPathStampRadius = 15.0,
            TurretClientStampRadius = 5.0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        };
        using var stream = BuildGrid(9, 9, 10.0,
            (column: 4, row: 4, flags: (ushort)0x02, height: 0.0f),
            (column: 2, row: 2, flags: (ushort)0x01, height: 22.0f),
            (column: 2, row: 3, flags: (ushort)0x01, height: 23.0f),
            (column: 5, row: 2, flags: (ushort)0x01, height: 25.0f));
        var grid = NavigationGrid.Load(stream, settings);

        TestAssert.Equal(new NavCell(0, 0), grid.WorldToCell(new Position2(-24.9, -24.9)));
        TestAssert.Equal(new NavCell(0, 0), grid.WorldToCell(new Position2(-25.1, -25.1)));
        TestAssert.True(grid.IsWalkable(new NavCell(0, 0)));
        TestAssert.False(grid.IsWalkable(new NavCell(4, 4)));
        TestAssert.False(grid.IsPathingWalkable(new NavCell(3, 4)));
        TestAssert.Near(0.0, grid.HeightAt(new Position2(15.0, 15.0)), 0.001);
        TestAssert.Near(22.0, grid.HeightAt(new Position2(0.0, 0.0)), 0.001);
        TestAssert.Equal(1, grid.BrushIdAt(new Position2(0.0, 0.0)));
        TestAssert.Equal(1, grid.BrushIdAt(new Position2(4.9, 0.0)));
        TestAssert.True(grid.BrushAllowsVision(new Position2(0.0, 0.0), new Position2(20.0, 20.0)));
        TestAssert.False(grid.BrushAllowsVision(new Position2(0.0, 0.0), new Position2(30.0, 0.0)));

        var path = grid.FindPath(new Position2(-20.0, -20.0), new Position2(55.0, 55.0));
        TestAssert.True(path.Count >= 2);
        TestAssert.Equal(new Position2(-20.0, -20.0), path[0]);
        TestAssert.Equal(new Position2(55.0, 55.0), path[^1]);
        TestAssert.Throws<ArgumentException>(() => grid.WorldToCell(new Position2(double.NaN, 0.0)));

        using var stampedStream = BuildGrid(9, 9, 10.0);
        var stampedSettings = new NavigationGridSettings
        {
            CollisionRadius = settings.CollisionRadius,
            TurretPathStampRadius = settings.TurretPathStampRadius,
            TurretClientStampRadius = settings.TurretClientStampRadius,
            Turrets = [new TurretDefinition("test", Team.Order, new Position2(20.0, 20.0), TurretTier.Outer)],
            Structures = [],
        };
        var stamped = NavigationGrid.Load(stampedStream, stampedSettings);
        TestAssert.True(stamped.IsWalkable(new NavCell(3, 4)));
        // Batch 223 item 3: the strategic view stamps the client's footprint (5 units here), and (3, 4) is 10 units from the turret.
        TestAssert.True(stamped.IsPathingWalkable(new NavCell(3, 4)));
        TestAssert.True(stamped.IsMovementWalkable(new NavCell(3, 4)));
        TestAssert.False(stamped.IsMovementWalkable(new NavCell(4, 4)));
    }

    [Check("navigation-summon-closest-floor-edge-and-ties")]
    public static void SummonClosestFloorEdgeAndTies()
    {
        var settings = new NavigationGridSettings
        {
            CollisionRadius = 0.0,
            TurretPathStampRadius = 0.0,
            TurretClientStampRadius = 0.0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        };
        using var stream = BuildGrid(9, 9, 10.0,
            (column: 4, row: 4, flags: (ushort)0x02, height: 0.0f));
        var grid = NavigationGrid.Load(stream, settings);

        // The blocked center has four equally near body-clear client points.
        // Lower X wins before lower Z, independent of terrain-cell scan order.
        TestAssert.Equal(new Position2(8.0, 20.0),
            grid.ClosestSummonPlacement(new Position2(20.0, 20.0), bodyRadius: 2.0));

        // This raw floor point clears the wall, but its client waypoint rounds
        // into the wall's body margin; choose the next safe client coordinate.
        var nearWall = new Position2(31.5, 20.0);
        TestAssert.True(grid.IsMovementWalkable(nearWall));
        TestAssert.True(grid.PointClearance(nearWall, 6.0) >= 6.0);
        TestAssert.Equal(new Position2(32.0, 20.0),
            grid.ClosestSummonPlacement(nearWall, bodyRadius: 2.0));

        // The first floor cell reaches the map boundary; its raw center cannot
        // hold this body plus margin, so the nearest client point is inset.
        TestAssert.True(grid.IsMovementWalkable(new Position2(-20.0, 0.0)));
        TestAssert.Equal(new Position2(-18.0, 0.0),
            grid.ClosestSummonPlacement(new Position2(-20.0, 0.0), bodyRadius: 2.0));

        // An already legal fractional request remains exact.
        var legal = new Position2(0.25, 0.75);
        TestAssert.Equal(legal, grid.ClosestSummonPlacement(legal, bodyRadius: 2.0));
    }


    [Check("navigation-brush-area-rule-synthetic")]
    public static void BrushAreaRuleSynthetic()
    {
        // 20u cells: a 35u circle at a cell centre covers exactly its 3x3 block.
        var settings = new NavigationGridSettings
        {
            CollisionRadius = 0.0,
            TurretPathStampRadius = 0.0,
            TurretClientStampRadius = 0.0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        };
        using var stream = BuildGrid(12, 12, 20.0,
            (column: 5, row: 3, flags: (ushort)0x01, height: 0.0f),
            (column: 5, row: 4, flags: (ushort)0x01, height: 0.0f),
            (column: 9, row: 3, flags: (ushort)0x01, height: 0.0f),
            (column: 7, row: 5, flags: (ushort)0x01, height: 0.0f),
            (column: 9, row: 6, flags: (ushort)0x01, height: 0.0f),
            (column: 9, row: 7, flags: (ushort)0x01, height: 0.0f),
            (column: 5, row: 8, flags: (ushort)0x01, height: 0.0f),
            (column: 5, row: 9, flags: (ushort)0x01, height: 0.0f),
            (column: 3, row: 8, flags: (ushort)0x02, height: 0.0f),
            (column: 3, row: 9, flags: (ushort)0x02, height: 0.0f));
        var grid = NavigationGrid.Load(stream, settings);
        Position2 Centre(int column, int row) => grid.CellToWorld(new NavCell(column, row));

        // 2 grass of 9 covered cells misses (int)(9 * 0.4f) = 3.
        TestAssert.True(grid.BrushIdAt(Centre(4, 4), 35.0) is null, "2 of 9 is outside brush");
        // Non-grass walls leave the total: 2 of 7 meets (int)(7 * 0.4f) = 2.
        TestAssert.Equal(grid.BrushIdAt(Centre(5, 9)), grid.BrushIdAt(Centre(4, 9), 35.0),
            "walls are excluded from the covered total");
        TestAssert.True(grid.BrushIdAt(Centre(4, 9)) is null && grid.BrushIdAt(Centre(4, 9), 34.9) is null,
            "below 35u only the plain centre cell counts");
        // A lone grass centre cell is 1 of 9: the area rule leaves it outside.
        TestAssert.True(grid.BrushIdAt(Centre(9, 3)) is not null, "centre-cell grass");
        TestAssert.True(grid.BrushIdAt(Centre(9, 3), 35.0) is null, "1 of 9 is outside brush");
        // Two brushes covered (1 + 2 cells) from plain floor: the larger labels it.
        TestAssert.Equal(grid.BrushIdAt(Centre(9, 6)), grid.BrushIdAt(Centre(8, 6), 35.0),
            "the most common covered brush labels the unit");
        TestAssert.True(grid.BrushIdAt(Centre(9, 6)) != grid.BrushIdAt(Centre(7, 5)), "separate brushes");
    }

    [Check("navigation-brush-edge-selection-radius-sight")]
    public static void BrushEdgeSelectionRadiusSight()
    {
        // Playtest #10 (25 Sept 2026): the stock client dims a unit at a brush
        // edge by its SelectionRadius (IsInWallOfGrass), so the server must hide
        // it too instead of using the centre cell alone.
        TestAssert.Near(100.0, GameData.Default.Champion("Annie").SelectionRadius, 1e-9);
        TestAssert.Near(115.0, GameData.Default.Champion("Blue_Minion_Basic").SelectionRadius, 1e-9,
            "text-valued radii parse as numbers");

        var grid = NavigationGrid.LoadDefault();
        // Bot-lane brush west edge: 4 of 12 covered cells are grass, (int)(12 * 0.4f) = 4.
        var edge = new Position2(7166.1, 564.8);
        TestAssert.True(grid.BrushIdAt(edge) is null, "the centre cell is plain floor");
        var brush = grid.BrushIdAt(edge, 100.0);
        TestAssert.True(brush is not null, "a 100u unit there is in the brush");

        var outside = new Position2(6666.1, 664.8);
        TestAssert.True(grid.BrushIdAt(outside, 100.0) is null && grid.HasVisionLos(outside, edge),
            "the outside viewer has a clear floor sight line");
        // The same brush's east edge: plain centre cell, same brush by the area rule.
        var brushEdge = new Position2(7596.1, 564.8);
        TestAssert.True(grid.BrushIdAt(brushEdge) is null && grid.BrushIdAt(brushEdge, 100.0) == brush,
            "the east-edge viewer is in the same brush");
        // A viewer on the grass beside the edge hero, 80 units east.
        var beside = new Position2(7246.1, 564.8);
        TestAssert.True(grid.BrushIdAt(beside) is not null, "the beside viewer stands on grass");
        TestAssert.False(grid.HasUnitVision(outside, 100.0, edge, 100.0), "grid: hidden from outside");
        TestAssert.True(grid.HasUnitVision(beside, 100.0, edge, 100.0), "grid: seen from the grass beside it");
        // Batch 253 restates "seen from the east edge" (the old same-label rule): the client's walk
        // hides a target in brush once it reaches a non-grass cell beyond the viewer's SelectionRadius.
        // From the east edge the walk leaves the grass at x 7221, about 375 units out, so the edge hero is hidden.
        TestAssert.False(grid.HasUnitVision(brushEdge, 100.0, edge, 100.0), "grid: hidden from the far edge of the same brush");

        var environment = new NavigationGridVisionEnvironment(grid);
        var world = new WorldState();
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Chaos, edge)
            { SelectionRadius = 100.0 });
        var viewer = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, outside)
            { SelectionRadius = 100.0 });
        bool Revealed() => VisibilityRules.RevealsEntity(world, Team.Order, environment,
            VisibilityRules.BuildTeamProviders(world, Team.Order, Array.Empty<BallProjection>(),
                _ => Array.Empty<VisionProvider>()), _ => false, hero);
        TestAssert.False(Revealed(), "an enemy outside the brush does not see the edge hero");
        viewer.Position = beside;
        TestAssert.True(Revealed(), "an enemy on the grass beside the edge hero sees it");
        viewer.Position = brushEdge;
        TestAssert.False(Revealed(), "an enemy at the far edge of the same brush does not (batch 253, client walk)");
        VisionProvider[] point = [new(brushEdge, 1350.0)];
        TestAssert.False(VisibilityRules.RevealsEntity(world, Team.Order, environment, point, _ => false, hero),
            "a point provider there is outside the brush");
    }

    private static Position2 Point(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());

    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual, string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, $"{label} waypoint count");
        for (var index = 0; index < actual.Count; index++)
        {
            TestAssert.Near(expected[index][0].GetDouble(), actual[index].X, 1e-6, $"{label}[{index}].X");
            TestAssert.Near(expected[index][1].GetDouble(), actual[index].Z, 1e-6, $"{label}[{index}].Z");
        }
    }

    private static void CheckHash(JsonElement expected, ReadOnlySpan<byte> bytes)
    {
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        // Re-recording (owner decision of 28 September 2026): the digests of the stamped grids follow the shipped turret positions
        // (batch 64 moved three), so the recorder writes the current digest instead of asserting the archived one.
        if (FixtureRecorder.Enabled) { CorpusSnapshot.Set(expected, actual); return; }
        TestAssert.Equal(expected.GetString()!, actual);
    }

    private static byte[] IntBytes(IReadOnlyList<int> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(uint))];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint), sizeof(uint)), checked((uint)values[index]));
        return bytes;
    }

    private static byte[] DoubleBytes(IReadOnlyList<double> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(double))];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(index * sizeof(double), sizeof(double)),
                BitConverter.DoubleToInt64Bits(values[index]));
        return bytes;
    }

    private static MemoryStream BuildGrid(
        int countX,
        int countY,
        double cellSize,
        params (int column, int row, ushort flags, float height)[] overrides)
    {
        var bytes = new byte[checked(37 + countX * countY * 56)];
        bytes[0] = 2;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1, 4), -25.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5, 4), 0.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(9, 4), -25.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(13, 4), (float)(-25.0 + countX * cellSize));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(17, 4), 100.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(21, 4), (float)(-25.0 + countY * cellSize));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(25, 4), (float)cellSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(29, 4), (uint)countX);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(33, 4), (uint)countY);
        foreach (var (column, row, flags, height) in overrides)
        {
            var offset = 37 + (row * countX + column) * 56;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 50, 2), flags);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), height);
        }
        return new MemoryStream(bytes, writable: false);
    }
}
