using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Batch 556 (TT-3): Map4's (Twisted Treeline) minion lanes come from its shipped scene (<see cref="MapLayout.FromScene"/>): the lanes it
/// ships barracks on, each a barracks spawn marker, the scene's <c>__NAV_&lt;lane&gt;0&lt;n&gt;</c> markers and the enemy's marker. The expected
/// points are lane B's independent reading (.artifacts/overnight-20261101/analysis-B-evidence/Map4-scb-centralpoints.txt, scb_central.py).
/// Summoner's Rift's lanes are Map1's markers with the reviewed port's detours and nudges (batch 595, MapLayout.SummonersRift).
/// </summary>
public static class Map4LaneChecks
{
    [Check("map4.lanes-follow-scene-markers")]
    public static void TwistedTreelineLanesAreItsSceneMarkers()
    {
        var layout = MapLayout.For(GameDataPaths.Default.ForMap(GameMaps.TwistedTreeline));
        TestAssert.Equal("L,R", string.Join(",", layout.Lanes), "Map4 ships Barracks_T{1,2}_{L,R}1 only: two lanes, no centre");
        var expected = new (string Lane, int Count, (int Index, string Marker, double X, double Z)[] Points)[]
        {
            ("L", 25,
            [
                (0, "__P_Order_Spawn_Barracks__L01", 1884.6731, 6488.0195), (1, "__NAV_L02", 1009.6252, 6841.3462),
                (8, "__NAV_L09", 4157.1934, 7861.1562), (9, "__NAV_L010", 4572.0928, 7395.0444),
                (23, "__NAV_L024", 11965.3555, 7103.7207), (24, "__P_Chaos_Spawn_Barracks__L01", 11611.2480, 6488.0195),
            ]),
            ("R", 18,
            [
                (0, "__P_Order_Spawn_Barracks__R01", 1883.3738, 6169.2104), (1, "__NAV_R02", 974.3170, 5768.3887),
                (8, "__NAV_R09", 5399.9043, 3351.8975), (9, "__NAV_R010", 6655.4561, 3430.5107),
                (14, "__NAV_R015", 12318.0605, 4138.5376), (16, "__NAV_R017", 12080.3203, 5776.8252),
                (17, "__P_Chaos_Spawn_Barracks__R01", 11609.9492, 6169.2104),
            ]),
        };
        foreach (var (lane, count, points) in expected)
        {
            var spine = layout.LanePaths[lane];
            TestAssert.Equal(count, spine.Count, $"lane {lane}: the Order barracks marker, its __NAV markers, the Chaos barracks marker");
            foreach (var (index, marker, x, z) in points)
            {
                TestAssert.Near(x, spine[index].X, 1e-3, $"lane {lane}[{index}] {marker} X");
                TestAssert.Near(z, spine[index].Z, 1e-3, $"lane {lane}[{index}] {marker} Z");
            }
            // Both lists are numbered from the Order side: the first __NAV marker is the Order barracks' neighbour, the last the Chaos one's.
            TestAssert.True(Distance(spine[1], spine[0]) < Distance(spine[1], spine[^1]), $"lane {lane}: __NAV_{lane}02 is on the Order side");
            TestAssert.True(Distance(spine[^2], spine[^1]) < Distance(spine[^2], spine[0]), $"lane {lane}: the last __NAV marker is on the Chaos side");
            TestAssert.Equal(new LaneSpawnApproach(1, 1), layout.LaneSpawnApproaches[lane], $"lane {lane}: each route ends at the last __NAV marker");
        }
    }

    [Check("map4.summoners-rift-lanes-unchanged")]
    public static void SummonersRiftLanesStayTheReviewedLiterals()
    {
        var map1 = GameDataPaths.Default.ForMap(GameMaps.Normal);
        var literal = MapLayout.For(map1);
        // Map 8 keeps Summoner's Rift's lane tables (it never walks them: its lane waves are off). Batch 601: map 2 builds its own from its
        // scene, by the same rule; its markers differ from Map1's only at __NAV_L013 (79 units) and __NAV_R05 (5e-4).
        var scar = MapLayout.For(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        TestAssert.True(ReferenceEquals(literal.LanePaths, scar.LanePaths) && ReferenceEquals(literal.Lanes, scar.Lanes) &&
            ReferenceEquals(literal.LaneSpawnApproaches, scar.LaneSpawnApproaches), "map 8 keeps Summoner's Rift's lane tables");
        var autumn = MapLayout.For(GameDataPaths.Default.ForMap(GameMaps.Harrowing));
        TestAssert.False(ReferenceEquals(literal.LanePaths, autumn.LanePaths), "map 2 has lane tables of its own");
        foreach (var lane in literal.Lanes)
        {
            TestAssert.Equal(literal.LaneSpawnApproaches[lane], autumn.LaneSpawnApproaches[lane], $"map 2 lane {lane} approaches");
            TestAssert.Equal(literal.LanePaths[lane].Count, autumn.LanePaths[lane].Count, $"map 2 lane {lane} spine");
            for (var index = 0; index < literal.LanePaths[lane].Count; index++)
                TestAssert.True(Distance(literal.LanePaths[lane][index], autumn.LanePaths[lane][index]) < (lane == "L" && index == 13 ? 80.0 : 1e-3),
                    $"map 2 lane {lane}[{index}] is Map1's but for __NAV_L013");
        }
        TestAssert.True(Distance(literal.LanePaths["L"][13], autumn.LanePaths["L"][13]) > 79.0, "map 2's __NAV_L013 is its own scene's");
        TestAssert.Equal("L,C,R", string.Join(",", literal.Lanes), "Summoner's Rift's three lanes");
        foreach (var lane in literal.Lanes) TestAssert.Equal(19, literal.LanePaths[lane].Count, $"Summoner's Rift lane {lane} spine");
        TestAssert.Equal(new LaneSpawnApproach(2, 1), literal.LaneSpawnApproaches["L"], "L approaches");
        TestAssert.Equal(new LaneSpawnApproach(2, 2), literal.LaneSpawnApproaches["C"], "C approaches");
        TestAssert.Equal(new LaneSpawnApproach(1, 2), literal.LaneSpawnApproaches["R"], "R approaches");

        // The generic scene reader is the rule Summoner's Rift's lanes follow: on Map1 it gives back the centre lane's __NAV_C02..C016 (exactly since
        // batch 599), and the L and R
        // spawns, which are the barracks markers themselves (the Order R one lies inside the HQ_T1 stamp, like Map4's Order markers).
        var scene = MapLayout.FromScene(map1);
        TestAssert.Equal("L,C,R", string.Join(",", scene.Lanes), "Map1's scene ships barracks on all three lanes");
        var centre = literal.LanePaths["C"];
        var markers = scene.LanePaths["C"];
        TestAssert.Equal(centre.Count - 2, markers.Count, "the scene's centre lane is the literal minus its two retained base approaches");
        for (var index = 1; index < markers.Count - 1; index++)
        {
            TestAssert.Near(centre[index + 1].X, markers[index].X, 1e-3, $"__NAV_C0{index + 1} X");
            TestAssert.Near(centre[index + 1].Z, markers[index].Z, 1e-3, $"__NAV_C0{index + 1} Z");
        }
        foreach (var lane in new[] { "L", "R" })
        foreach (var (from, to) in new[] { (literal.LanePaths[lane][0], scene.LanePaths[lane][0]), (literal.LanePaths[lane][^1], scene.LanePaths[lane][^1]) })
            TestAssert.True(Distance(from, to) < 0.01, $"Summoner's Rift lane {lane} spawns at its barracks marker (to the literal's two decimals)");

        // No interior node of a Summoner's Rift lane, on map 1 or map 2, is refused by the NPC pathing grid, so the batch-556 node correction
        // (WaveScheduler.LaneRoute) leaves its routes exactly as they were.
        foreach (var mapId in new[] { GameMaps.Normal, GameMaps.Harrowing })
        {
            var paths = GameDataPaths.Default.ForMap(mapId);
            var layout = MapLayout.For(paths);
            var grid = NavigationGrid.Load(paths, new NavigationGridSettings { Turrets = layout.Turrets, Structures = layout.StructureStamps });
            foreach (var lane in layout.Lanes)
            {
                var spine = layout.LanePaths[lane];
                for (var index = 1; index < spine.Count - 1; index++)
                    TestAssert.True(grid.IsPathingWalkable(spine[index]), $"map {mapId} lane {lane}[{index}] is on pathing floor");
            }
        }

        // Batch 578: the lane end's nearest-leg test (MinionAiSystem.PassedLaneEnd) keeps the half-plane's answer on every floor cell of maps
        // 1 and 2 within MAX_ENGAGE_DISTANCE (Minion.lua's 2500) of the lane. It differs only on another lane or the far jungle (a top-lane
        // Order minion standing in the bottom lane read as past its end), where the lane's minions do not walk.
        foreach (var mapId in new[] { GameMaps.Normal, GameMaps.Harrowing })
        {
            var floor = NavigationGrid.Load(GameDataPaths.Default.ForMap(mapId));
            var mapLayout = MapLayout.For(GameDataPaths.Default.ForMap(mapId));
            foreach (var team in new[] { Team.Order, Team.Chaos })
            foreach (var lane in mapLayout.Lanes)
            {
                var spine = mapLayout.LanePaths[lane];
                var approach = mapLayout.LaneSpawnApproaches[lane];
                var endIndex = team == Team.Order ? spine.Count - 1 - approach.Tail : approach.Head;
                var end = spine[endIndex];
                var forward = end - spine[endIndex + (team == Team.Order ? -1 : 1)];
                var (cells, nearest) = (0, double.PositiveInfinity);
                for (var row = 0; row < floor.CountY; row++)
                for (var column = 0; column < floor.CountX; column++)
                {
                    var cell = new NavCell(column, row);
                    if (!floor.IsMovementWalkable(cell)) continue;
                    var point = floor.CellToWorld(cell);
                    var plane = (point.X - end.X) * forward.X + (point.Z - end.Z) * forward.Z >= 0.0;
                    if (plane == MinionAiSystem.PassedLaneEnd(team, spine, endIndex, point)) continue;
                    cells++;
                    nearest = Math.Min(nearest, DistanceToPolyline(point, spine));
                }
                Console.WriteLine(FormattableString.Invariant($"map {mapId} {team} {lane}: {cells} floor cells answer differently, the nearest {nearest:0} from the lane"));
                TestAssert.True(nearest > MinionDefinitions.MaxEngageDistance,
                    $"map {mapId} {team} {lane}: the lane end keeps its answer within {MinionDefinitions.MaxEngageDistance} of the lane (nearest change {nearest:0})");
            }
        }
    }

    // Serial: it makes Map4 the process's match map, in a process of its own, so no map-1 check sees it.
    [Check("map4.waves-walk-the-two-scene-lanes", Serial = true)]
    public static void TwistedTreelineWavesWalkItsTwoSceneLanes()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.TwistedTreeline));
        var layout = MapDefinitions.Layout;
        TestAssert.Equal("L,R", string.Join(",", MapDefinitions.Lanes), "the match map's lanes are Map4's");
        var grid = NavigationGrid.Load(GameData.Default.Paths);
        var world = new WorldState();
        var level = new LevelScriptHost(world, new AiScriptCatalog(GameData.Default.Paths));
        var waves = new WaveScheduler(world, grid, new QuietPackets(), level, firstSpawnSeconds: 0.0);
        waves.Start(0.0);
        TestAssert.Equal("L,R", string.Join(",", waves.ActiveLanes), "no centre wave is asked for");
        var spawned = waves.Tick(10.0);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var lane in layout.Lanes)
        {
            // Map4's LevelScript (its LEFT_LANE 2 and RIGHT_LANE 0 barracks): three melee and three casters a lane.
            var members = spawned.Where(minion => minion.Team == team && minion.MinionLane == lane).ToArray();
            TestAssert.Equal(3, members.Count(minion => minion.MinionType == MinionType.Melee), $"{team} {lane} first wave melee");
            TestAssert.Equal(3, members.Count(minion => minion.MinionType == MinionType.Caster), $"{team} {lane} first wave casters");
            TestAssert.Equal(6, members.Length, $"{team} {lane} first wave");

            var spine = layout.LanePaths[lane];
            var route = waves.LaneRoute(team, lane);
            var (start, end) = team == Team.Order ? (spine[0], spine[^2]) : (spine[^1], spine[1]);
            TestAssert.True(Distance(route[0], start) < 3.0, $"{team} {lane} starts at its barracks marker");
            TestAssert.True(Distance(route[^1], end) < 3.0, $"{team} {lane} ends at the last __NAV marker before the enemy's barracks");
            // __NAV_R015 lies 105 units from Turret_T2_R_01; the route walks the nearest pathing cell instead of the turret's footprint.
            foreach (var point in route)
            foreach (var turret in layout.Turrets)
                TestAssert.True(Distance(point, turret.Position) >= MapDefinitions.TurretClientStampRadius,
                    $"{team} {lane} route point ({point.X:0},{point.Z:0}) is outside {turret.Name}'s footprint");
        }
        TestAssert.Equal(24, spawned.Count, "two teams, two lanes, six each: no centre wave");

        // The next wave waits the barracks table's WaveSpawnRate, Map4's 35000 (Summoner's Rift's is 30000).
        TestAssert.Equal(0, waves.Tick(34.9).Count, "no second wave before 35 s");
        TestAssert.Equal(1, waves.WaveIndex, "one wave so far");
        TestAssert.True(waves.Tick(35.0).Count > 0, "the second wave comes at 35 s");
        TestAssert.Equal(2, waves.WaveIndex, "two waves");
    }

    /// <summary>
    /// Batch 578 (the owner's game of 1 November: "they go into the jungle sidelane through the bush" by the top towers). Each team's
    /// planned route of each Map4 lane keeps within the lane's corridor (its __NAV spine), and nowhere along it before its end does a
    /// minion count as past the lane's end (<see cref="MinionAiSystem.PassedLaneEnd"/>): the half-plane test alone put the centre of the
    /// L lane past the Order end, so every Order minion that lost a target there was siege-routed straight at the next tower, through the
    /// bush beside Turret_T2_L_02.
    /// </summary>
    [Check("map4.lane-routes-stay-on-the-lane", Serial = true)]
    public static void TwistedTreelineLaneRoutesStayOnTheLane()
    {
        const double Corridor = 400.0;
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.TwistedTreeline));
        try
        {
            var layout = MapDefinitions.Layout;
            var grid = NavigationGrid.Load(GameData.Default.Paths);
            var world = new WorldState();
            var waves = new WaveScheduler(world, grid, new QuietPackets(), new LevelScriptHost(world, new AiScriptCatalog(GameData.Default.Paths)),
                firstSpawnSeconds: 0.0);
            foreach (var team in new[] { Team.Order, Team.Chaos })
            foreach (var lane in layout.Lanes)
            {
                var spine = layout.LanePaths[lane];
                var approach = layout.LaneSpawnApproaches[lane];
                var endIndex = team == Team.Order ? spine.Count - 1 - approach.Tail : approach.Head;
                var route = waves.LaneRoute(team, lane);
                var worst = 0.0;
                for (var index = 1; index < route.Count; index++)
                {
                    var length = Distance(route[index - 1], route[index]);
                    for (var along = 0.0; along < length; along += 25.0)
                    {
                        var point = Lerp(route[index - 1], route[index], along / length);
                        worst = Math.Max(worst, DistanceToPolyline(point, spine));
                        if (Distance(point, spine[endIndex]) > 50.0)
                            TestAssert.False(MinionAiSystem.PassedLaneEnd(team, spine, endIndex, point),
                                $"{team} {lane} ({point.X:0},{point.Z:0}) on the route is not past the lane's end");
                    }
                }
                Console.WriteLine(FormattableString.Invariant($"{team} {lane}: the route keeps within {worst:0} of the __NAV spine"));
                TestAssert.True(worst <= Corridor, $"{team} {lane} route keeps within {Corridor} of its __NAV spine (worst {worst:0})");
                var beyond = Lerp(spine[endIndex + (team == Team.Order ? -1 : 1)], spine[endIndex], 1.5);
                TestAssert.True(MinionAiSystem.PassedLaneEnd(team, spine, endIndex, beyond), $"{team} {lane}: half a leg beyond the end is past it");
            }
            // The owner's game: Order L minions lost their targets at (9316, 7608) under Turret_T2_L_02, and at (6020, 6596) in the centre.
            var left = layout.LanePaths["L"];
            foreach (var point in new[] { new Position2(9316, 7608), new Position2(6020, 6596) })
                TestAssert.False(MinionAiSystem.PassedLaneEnd(Team.Order, left, left.Count - 2, point),
                    $"Order L at ({point.X:0},{point.Z:0}) is mid-lane, not past __NAV_L024");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static double DistanceToPolyline(Position2 point, IReadOnlyList<Position2> line)
    {
        var best = double.PositiveInfinity;
        for (var index = 1; index < line.Count; index++)
        {
            var (a, b) = (line[index - 1], line[index]);
            var ab = b - a;
            var t = Math.Clamp(((point.X - a.X) * ab.X + (point.Z - a.Z) * ab.Z) / (ab.X * ab.X + ab.Z * ab.Z), 0.0, 1.0);
            best = Math.Min(best, Distance(point, Lerp(a, b, t)));
        }
        return best;
    }

    private static Position2 Lerp(Position2 a, Position2 b, double t) => new(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);

    // Serial: InitialHeroSpawn reads the process's match map.
    [Check("map4.hero-spawns-on-floor", Serial = true)]
    public static void HeroSpawnSlotsLandOnFloor()
    {
        // Maps 1, 2 and 8: every shipped slot of every team size is on floor, so the composition's floor step leaves them where they were.
        foreach (var mapId in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.CrystalScar, GameMaps.TwistedTreeline })
        {
            GameData.ConfigureDefault(GameDataPaths.Default.ForMap(mapId));
            var grid = NavigationGrid.Load(GameData.Default.Paths);
            foreach (var team in new[] { Team.Order, Team.Chaos })
            for (var size = 1; size <= 5; size++)
            for (var slot = 0; slot < size; slot++)
            {
                var spawn = MapDefinitions.InitialHeroSpawn(team, size, slot).Position;
                // The composition's step (ProductionMatchComposition HeroCreated).
                var placed = grid.NearestMovementFloor(spawn, maxCells: 4);
                if (mapId == GameMaps.TwistedTreeline && team == Team.Order && size == 5 && slot == 2)
                {
                    // The one exception: HeroSpawnOffsets.inibin Order5 slot 3 (-42.8, -168) is on the pillar south of Map4's fountain. Retail
                    // Twisted Treeline seats three a side; the 5v5 tools match starts it on the nearest floor cell.
                    TestAssert.False(grid.IsMovementWalkable(spawn), "Map4 Order5 slot 3 is on the fountain pillar");
                    TestAssert.True(placed is { } floor && grid.IsMovementWalkable(floor) && Distance(floor, spawn) < 100.0,
                        "it starts on the nearest floor cell");
                    continue;
                }
                TestAssert.True(grid.IsMovementWalkable(spawn), $"map {mapId} {team}{size} slot {slot + 1} is on floor");
                TestAssert.True(placed == spawn, $"map {mapId} {team}{size} slot {slot + 1} keeps its exact point");
            }
        }
    }

    /// <summary>
    /// Batch 559 (TT-6, H12): Bot.luaobj builds Push Lane 0, 1 and 2 on every map (Bot.txt proto 0.4 0076-0095); on Map4 the centre lane is
    /// no lane, so its task's GetMinions answer is empty and its priority stays 0 (TaskPushLane.txt proto 0.1 0046-0049, 0076). A seeded
    /// four-minute 3v3 headless match on Map4: every bot holds the three tasks, Push Lane 1 never rises above 0 nor becomes active, and
    /// each team pushes both of Map4's lanes (0 L, 2 R).
    /// </summary>
    [Check("map4.bots-push-only-the-two-lanes", Serial = true, TimeoutSeconds = 600)]
    public static void TwistedTreelineBotsPushOnlyItsTwoLanes()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-map4-bots-" + Guid.NewGuid().ToString("N"));
        var pushed = new HashSet<(Team Team, int Lane)>();
        var centreTasks = 0L;
        var centreRaised = new List<string>();
        var seats = new Dictionary<Team, int> { [Team.Order] = 0, [Team.Chaos] = 0 };
        try
        {
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(new Amberline.Tools.Diagnostics.HeadlessMatchOptions(559, Path.Combine(root, "map4"), 4.0,
                Amberline.Tools.Diagnostics.HeadlessMatch.DefaultRoster.Take(6).ToArray())
            {
                MapId = GameMaps.TwistedTreeline,
                OnStep = runtime =>
                {
                    if (!runtime.Session.Started || runtime.Bots is not { } bots) return;
                    foreach (var team in new[] { Team.Order, Team.Chaos })
                        seats[team] = runtime.Session.Participants.Count(seat => seat.Team == team);
                    foreach (var seat in runtime.Session.Participants)
                    {
                        if (bots.StateOf(seat.PlayerId) is not { } state) continue;
                        foreach (var task in state.Tasks.Where(task => task.Kind == Amberline.Simulation.Bots.BotTaskKind.PushLane && task.Lane == 1))
                        {
                            centreTasks++;
                            if (task.Priority != 0.0 && centreRaised.Count < 5)
                                centreRaised.Add(FormattableString.Invariant($"{seat.ChampionName} {runtime.Combat.CurrentTime:0.0}s {task.Priority}"));
                        }
                        if (state.Active is { Kind: Amberline.Simulation.Bots.BotTaskKind.PushLane } active) pushed.Add((seat.Team, active.Lane));
                    }
                },
            });
            Console.WriteLine(report.Summary());
            TestAssert.Equal("3,3", $"{seats[Team.Order]},{seats[Team.Chaos]}", "three seats a side");
            TestAssert.True(report.PacketViolations == 0 && report.LifecycleViolations == 0 && report.Outcome == "cap", report.Summary());
            TestAssert.True(centreTasks > 0, "every bot builds its Push Lane 1 task (the script's literal loop)");
            TestAssert.True(centreRaised.Count == 0, "Push Lane 1 keeps priority 0 on Map4: " + string.Join("; ", centreRaised));
            var lanes = string.Join(",", pushed.Select(entry => $"{entry.Team}:{entry.Lane}").Order(StringComparer.Ordinal));
            TestAssert.Equal("Chaos:0,Chaos:2,Order:0,Order:2", lanes, "each team pushes Map4's L (0) and R (2), never the centre");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // Packets are not under test here: the sink only has to accept what the scheduler sends.
    private sealed class QuietPackets : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }
}
