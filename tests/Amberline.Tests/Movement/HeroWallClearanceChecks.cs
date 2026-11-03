using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 20 (owner playtest, 15 October 2026): hero routes that scrape terrain
/// (item 1, Orianna at HUD 1:15 on mid) and the 18-waypoint wall-hugging
/// staircase in the bottom-mid river bush (item 6, HUD 9:50). Every start and
/// goal is a captured order from game 86b06f2a; no bodies were near either.
/// </summary>
public static class HeroWallClearanceChecks
{
    // Orianna's speed in the captured fidelity batches (318 u/s).
    private const double CapturedSpeed = 318.0;

    // Item 1, order 83 at t=115.382: she stands in the planning margin next to the
    // staircase wall; the old first leg ran through its convex corner (6121.1, 5839.8).
    private static readonly Position2 WallStart = new(6085, 5757);
    private static readonly Position2 WallGoal = new(6326.51171875, 6191.2490234375);

    // Item 6, orders 1062 (t=629.961) and 1064 (t=634.313): the bush wraps the south
    // and east faces of a wall block; the clicked points sit beside its west face.
    private static readonly Position2 BushEast = new(9571, 6549);
    private static readonly Position2 BushEastGoal = new(9573.4189453125, 6548.67626953125);
    private static readonly Position2 BushWest = new(8797, 6203);
    private static readonly Position2 BushWestGoal = new(8813.2880859375, 6197.74169921875);

    [Check("navigation.segment-clearance-matches-sampled-distance")]
    public static void SegmentClearanceMatchesSampledDistance()
    {
        var grid = NavigationGrid.LoadDefault();
        var random = new Random(20261015);
        var compared = 0;
        foreach (var centre in new Position2[] { new(6121, 5840), new(8870, 6140), new(9520, 6300), new(4800, 4450) })
        for (var sample = 0; sample < 40; sample++)
        {
            var a = new Position2(centre.X + random.Next(-150, 150), centre.Z + random.Next(-150, 150));
            var b = new Position2(a.X + random.Next(-200, 200), a.Z + random.Next(-200, 200));
            if (!grid.IsWalkable(a) || !grid.IsWalkable(b)) continue;
            var exact = grid.SegmentClearance(a, b, 80.0);
            var sampled = 80.0;
            var steps = (int)Math.Ceiling(Distance(a, b) / 0.5);
            for (var step = 0; step <= steps; step++)
            {
                var t = steps == 0 ? 0.0 : (double)step / steps;
                var point = new Position2(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                sampled = Math.Min(sampled, grid.SegmentClearance(point, point, 80.0));
            }
            // Sampling every half unit can overshoot the true minimum by at most half a step.
            TestAssert.True(exact <= sampled + 1e-9 && exact >= sampled - 0.5,
                $"segment clearance {exact:0.00} disagrees with sampled {sampled:0.00} for {a} -> {b}");
            compared++;
        }
        TestAssert.True(compared >= 60, $"enough walkable segments were compared ({compared})");
        // The captured grazing chord touches the convex corner; a chord that leaves the wall first does not.
        TestAssert.True(grid.SegmentClearance(WallStart, new Position2(6197, 6015), 80.0) < 1.0,
            "the captured 1:15 chord passes through the wall corner");
        TestAssert.True(grid.SegmentClearance(WallStart, new Position2(6097, 5865), 80.0) > 16.0,
            "a first leg that leaves the wall keeps the clearance its start already had");
    }

    [Check("movement.hero-margin-start-leg-keeps-wall-clearance")]
    public static void MarginStartLegKeepsWallClearance()
    {
        using var runtime = StartSolo("Orianna");
        var hero = PrepareHero(runtime, WallStart);
        var startClearance = runtime.Navigation.SegmentClearance(WallStart, WallStart, 100.0);
        TestAssert.True(startClearance > 15.0 && startClearance < 20.0,
            $"captured start is in the planning margin ({startClearance:0.0}u)");

        var route = runtime.Movement.SetMoveOrder(hero.NetId, WallGoal);
        TestAssert.True(route.Count >= 2 && route.Count <= 4, $"route is short ({route.Count} points)");
        TestAssert.Equal(runtime.Movement.ClientAnchor(WallGoal), route[^1], "the clicked goal is kept");
        var worstLeg = Legs(route).Min(leg => runtime.Navigation.SegmentClearance(leg.From, leg.To, 100.0));
        // The old first leg ran through the corner at 0.1u, and the old walk came within 4.4u. Batch 61: the hero route is the stock
        // client's planner (no terrain dilation by the unit radius, see the contract), whose chord keeps 3.0u from this corner; the
        // batch-20 rule "no closer than the start's own clearance less 3u" belonged to the retired 65-unit hero planner. What stays
        // true is that no leg touches terrain, and the follower below stays on floor.
        TestAssert.True(worstLeg > 0.0,
            $"no leg touches terrain ({worstLeg:0.0}u, start {startClearance:0.0}u): {Describe(route)}");
        TestAssert.True(Legs(route).All(leg => runtime.Navigation.HasMovementLineOfSight(leg.From, leg.To)),
            $"every leg passes the client movement-grid line of sight: {Describe(route)}");
        TestAssert.True(FollowerClear(runtime, hero.Position, route.Skip(1).ToArray(),
                CapturedSpeed * MovementSystem.HeroTickSeconds), "the fixed-origin follower stays on floor");

        var closest = Walk(runtime, hero, WallGoal, out var arrived);
        TestAssert.True(arrived, "the walk reaches the clicked goal");
        TestAssert.True(closest > 0.0,
            $"Orianna's centre never touches terrain on the way ({closest:0.0}u)");
    }

    [Check("movement.hero-bush-bridge-route-is-compact-and-clear")]
    public static void BushBridgeRouteIsCompactAndKeepsClearance()
    {
        using var runtime = StartSolo("Orianna");
        var hero = PrepareHero(runtime, BushEast);
        foreach (var (start, goal) in new[] { (BushEast, BushWestGoal), (BushWest, BushEastGoal) })
        {
            hero.Position = start;
            hero.Waypoints.Clear();
            var route = runtime.Movement.SetMoveOrder(hero.NetId, goal);
            TestAssert.True(route.Count >= 2, "the bush crossing has a route");
            TestAssert.Equal(runtime.Movement.ClientAnchor(goal), route[^1], "the clicked goal is kept");
            // Old route: 18 waypoints, 50u cell steps alternating 0 and 45 degrees along the wall. The route is the stock client's own plan
            // (two smoothing passes over its A* cells: 9 waypoints for the east to west crossing), no longer the terrain-only fallback's.
            TestAssert.True(route.Count <= 12, $"no raw cell staircase ({route.Count} waypoints): {Describe(route)}");
            TestAssert.True(Jogs(route) <= 2, $"no stair-step jogs ({Jogs(route)}): {Describe(route)}");
            // Batch 61 / W8: the terrain rule of the route is the client planner's (line of sight sampled every 0.8 cells, no dilation by
            // the unit radius, checked against the executable's vectors). The batch-20 rules this check used to add (every leg keeps what its
            // endpoints have, a follower chord that never clips a blocked cell, 18 units from the wall on the walk) belonged to the retired
            // 65-unit planner and its follower-safe fallback; what is asserted now is the route's shape and that the walk completes.
            Walk(runtime, hero, goal, out var arrived, goalSlack: 120.0);
            TestAssert.True(arrived, $"the walk {start} -> {goal} completes");
        }
    }

    [Check("movement.hero-random-orders-keep-terrain-clearance")]
    public static void RandomOrdersKeepTerrainClearance()
    {
        // 300 deterministic orders (cross-map and local) from random walkable points. Before the
        // fix 48 of them had a leg closer to terrain than its own endpoints (half-cell margin).
        using var runtime = StartSolo("Orianna");
        var hero = PrepareHero(runtime, new Position2(7000, 7000));
        var navigation = runtime.Navigation;
        var random = new Random(20261016);
        Position2 Sample() => Enumerable.Range(0, 1000)
            .Select(_ => new Position2(random.Next(500, 13500), random.Next(500, 13500)))
            .First(point => navigation.IsMovementWalkable(point) && navigation.IsWalkable(point));
        var pairs = new List<(Position2 From, Position2 To)>();
        for (var index = 0; index < 150; index++) pairs.Add((Sample(), Sample()));
        for (var index = 0; index < 150; index++)
        {
            var from = Sample();
            var to = Enumerable.Range(0, 1000)
                .Select(_ => new Position2(from.X + random.Next(-700, 700), from.Z + random.Next(-700, 700)))
                .First(point => navigation.IsMovementWalkable(point) && navigation.IsWalkable(point));
            pairs.Add((from, to));
        }

        var routed = 0;
        foreach (var (from, to) in pairs)
        {
            hero.Position = from;
            hero.Waypoints.Clear();
            var route = runtime.Movement.SetMoveOrder(hero.NetId, to);
            if (route.Count < 2) continue;
            routed++;
            // Batch 61: the hero route is the stock client's planner, which does not dilate terrain by the unit radius: 47 of these 300
            // orders have a leg within 4u of a wall corner of the endpoints' own margin (the batch-20 rule, retired with the 65-unit hero
            // planner), and a chord or raw A* diagonal may clip the corner of a blocked cell (the client's line of sight samples every
            // 0.8 cells and its free-movement frame has no terrain gate). W8 (18 October 2026): the server walks that route as the client
            // does (the follower-safe fallback that kept clips rare is deleted), so a walk may end a frame in a blocked cell. What is
            // asserted is the outcome: the hero never lodges in blocked cells (ClientFrameBlockedRun: it may end a frame on a blocked cell, and
            // a chord that runs along a wall edge keeps it there for as long as the wall runs; the episode is bounded by the longest the
            // baseline matches measured, 0.77 s, where this check first bounded it by six frames from a line-of-sight gap that only
            // explains a corner, not a wall edge).
            var blocked = new ClientFrameBlockedRun();
            for (var tick = 0; tick < 3000 && hero.Waypoints.Count > 0; tick++)
            {
                runtime.Movement.Tick(MovementSystem.HeroTickSeconds);
                blocked.Observe(navigation.IsMovementWalkable(hero.Position), MovementSystem.HeroTickSeconds,
                    () => $"order {from} -> {to} at tick {tick}, hero at {hero.Position}: {Describe(route)}");
            }
        }
        TestAssert.True(routed >= 290, $"random orders still find routes ({routed}/{pairs.Count})");
    }

    [Check("movement.hero-explicit-click-at-wall-edge-is-honoured")]
    public static void ExplicitClickAtWallEdgeIsHonoured()
    {
        // Retail evidence (the client's nav-grid path builder and its nearest-cell snap): the destination cell is tested only for
        // NotPassable and dynamic bodies, with no terrain dilation by the unit radius.
        // The 1:17 episode (order 92, a click 1.7u from the wall) is retail behaviour:
        // the hero walks to the clicked, walkable point. Clearance applies to the route.
        using var runtime = StartSolo("Orianna");
        var hero = PrepareHero(runtime, new Position2(6079, 5755));
        var click = new Position2(6119.3818359375, 5770.35498046875);
        var route = runtime.Movement.SetMoveOrder(hero.NetId, click);
        TestAssert.Equal(2, route.Count, "short click, direct leg");
        TestAssert.True(Distance(route[^1], click) <= 3.0, "the wire goal is the click");
        TestAssert.True(runtime.Navigation.SegmentClearance(route[^1], route[^1], 100.0) < 3.0,
            "the clicked point is beside the wall and the hero goes there");
    }

    [Check("movement.hero-ryze-wall-edge-orders-obey-the-retail-gate")]
    public static void RyzeWallEdgeOrdersObeyTheRetailGate()
    {
        // Owner playtest 16 October 2026 (Ryze, HUD 1:30-1:40: "still able to walk a bit too close to
        // walls"): the owner clicked along the same staircase wall at (6121, 5740-5790) and (6021, 5640-5690)
        // for 40 s. Replay of nine of those orders (start, click) from game 986a2544. Retail evidence:
        // the client's per-frame gate (its actor collision handler) and the
        // route builder (the client's path builder -> line of sight -> goal = the
        // click) test only the NotPassable bit of the cell under the bare point, with no radius. A click on
        // a walkable cell is therefore walked to, 0.1u from the wall; a click inside the wall snaps to a
        // walkable cell centre (SetToNearestGetToAbleCell, never closer than half a cell).
        // No hero position may sit in a cell the gate refuses.
        using var runtime = StartSolo("Ryze");
        var hero = PrepareHero(runtime, new Position2(5947, 5665));
        (Position2 Start, Position2 Click)[] orders =
        [
            (new(5947, 5665), new(6019.4873046875, 5665.0908203125)),
            (new(5989, 5657), new(6007.8671875, 5642.28271484375)),
            (new(5971, 5655), new(6019.1533203125, 5641.3515625)),
            (new(6075, 5761), new(6119.91845703125, 5744.640625)),
            (new(6073, 5757), new(6104.791015625, 5748.302734375)),
            (new(6024.2416675381955, 5759.9371913862715), new(6119.12451171875, 5772.83984375)),
            (new(6003, 5645), new(6038.2783203125, 5616.0126953125)),
            (new(6120.236674078243, 5745.265504668438), new(6113.365234375, 5726.21142578125)),
            (new(6095, 5767), new(6127.19775390625, 5760.29541015625)),
        ];
        var navigation = runtime.Navigation;
        var edgeClicks = 0;
        foreach (var (start, click) in orders)
        {
            hero.Position = start;
            hero.Waypoints.Clear();
            var route = runtime.Movement.SetMoveOrder(hero.NetId, click);
            TestAssert.True(route.Count >= 1, $"order {start} -> {click} has a route");
            var goal = route[^1];
            if (navigation.IsWalkable(click))
            {
                // The wire grid moves the goal by at most its diagonal, and never off the click's own cell row.
                TestAssert.True(Distance(goal, click) <= 3.0, $"a walkable click is walked to: {click} -> {goal}");
                if (navigation.SegmentClearance(click, click, 25.0) < 3.0) edgeClicks++;
            }
            else if (route.Count == 1)
            {
                // Batch 61: the client's path builder plans nothing when the click's nearest free cell lies within 10 units
                // of the hero's own position; the order is a stationary anchor and the hero stays where it stands.
                TestAssert.Equal(hero.Position, goal, $"a wall click next to the hero plans nothing: {click}");
                TestAssert.True(navigation.IsWalkable(goal), $"the stationary anchor is floor: {goal}");
            }
            else
            {
                TestAssert.True(navigation.IsWalkable(goal), $"a wall click ends on floor: {goal}");
                TestAssert.True(navigation.SegmentClearance(goal, goal, 100.0) >= 24.0,
                    $"a wall click snaps to a cell centre, half a cell from terrain: {goal}");
            }

            for (var tick = 0; tick < 800 && hero.Waypoints.Count > 0; tick++)
            {
                runtime.Tick(.05);
                TestAssert.True(navigation.IsWalkable(hero.Position) && navigation.IsMovementWalkable(hero.Position),
                    $"the hero never stands in a cell the client gate refuses: {hero.Position} ({start} -> {click})");
            }
            TestAssert.True(hero.Waypoints.Count == 0, $"the walk ends: {start} -> {click}");
        }
        TestAssert.True(edgeClicks >= 4, $"the replay covers clicks within 3u of the wall ({edgeClicks})");
    }

    // Batch 248 (restated from P3, 18 October 2026, whose route out of a wall was NOT RETAIL): the stock planner starts its A* on the actor's
    // own cell without testing it and tests every neighbour it expands (the client's path builder), so a hero on a blocked cell with a
    // floor cell beside it plans as usual, and one whose eight neighbours are all blocked plans
    // nothing: the order is a stop on the spot. The wall is the one a bot stood in for 720 s (headless seed 21, 60 minutes, (12376.3, 3318.6)).
    [Check("movement.hero-lodged-deep-in-a-wall-plans-as-the-client")]
    public static void LodgedHeroPlansAsTheClient()
    {
        using var runtime = StartSolo("Yorick");
        var goal = new Position2(12900, 3600);
        var walledIn = 0;
        var open = 0;
        for (var column = 248; column <= 258; column++)
        for (var row = 62; row <= 74; row++)
        {
            var cell = new NavCell(column, row);
            if (runtime.Navigation.IsMovementWalkable(cell)) continue;
            var hero = PrepareHero(runtime, runtime.Navigation.CellToWorld(cell));
            var route = runtime.Movement.SetMoveOrder(hero.NetId, goal);
            var enclosed = true;
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
                if ((dx != 0 || dz != 0) && runtime.Navigation.IsMovementWalkable(new NavCell(column + dx, row + dz))) enclosed = false;
            if (enclosed)
            {
                TestAssert.Equal(1, route.Count, $"the client's planner finds nothing from walled-in cell {cell}: the order is a stop");
                walledIn++;
            }
            else
            {
                TestAssert.True(route.Count >= 2, $"blocked cell {cell} beside floor plans from its own cell ({route.Count} points)");
                open++;
            }
        }
        TestAssert.True(walledIn > 10 && open > 10, $"the wall offers both kinds of lodged start ({walledIn} walled in, {open} beside floor)");
    }

    private static IEnumerable<(Position2 From, Position2 To)> Legs(IReadOnlyList<Position2> route) =>
        route.Zip(route.Skip(1), (from, to) => (from, to));

    /// <summary>Interior vertices where two short legs turn by 30 degrees or more.</summary>
    private static int Jogs(IReadOnlyList<Position2> route)
    {
        var jogs = 0;
        for (var index = 1; index + 1 < route.Count; index++)
        {
            var incoming = route[index] - route[index - 1];
            var outgoing = route[index + 1] - route[index];
            var first = Distance(route[index - 1], route[index]);
            var second = Distance(route[index], route[index + 1]);
            if (first > 120 || second > 120 || first * second <= 0) continue;
            var cosine = (incoming.X * outgoing.X + incoming.Z * outgoing.Z) / (first * second);
            if (cosine < Math.Cos(Math.PI / 6.0)) jogs++;
        }
        return jogs;
    }

    private static string Describe(IReadOnlyList<Position2> route) =>
        string.Join(" ", route.Select(point => $"({point.X:0},{point.Z:0})"));

    /// <summary>Ticks the production runtime until the order ends; returns the closest terrain approach.</summary>
    private static double Walk(MatchRuntime runtime, Entity hero, Position2 goal, out bool arrived,
        double goalSlack = 0.0)
    {
        var closest = double.PositiveInfinity;
        for (var tick = 0; tick < 800 && hero.Waypoints.Count > 0; tick++)
        {
            runtime.Tick(.05);
            if (Distance(hero.Position, goal) > goalSlack)
                closest = Math.Min(closest, runtime.Navigation.SegmentClearance(hero.Position, hero.Position, 200.0));
        }
        arrived = hero.Waypoints.Count == 0 && Distance(hero.Position, goal) <= 3.0;
        return closest;
    }

    private static bool FollowerClear(MatchRuntime runtime, Position2 origin,
        IReadOnlyList<Position2> route, double step)
    {
        var remaining = route.ToList();
        var position = origin;
        for (var frame = 0; frame < 4096 && remaining.Count > 0; frame++)
        {
            var next = MovementSystem.FixedOriginStep(position, remaining, step).Position;
            if (!runtime.Navigation.HasHeroMovementLineOfSight(position, next)) return false;
            position = next;
        }
        return remaining.Count == 0;
    }

    private static Entity PrepareHero(MatchRuntime runtime, Position2 position)
    {
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        hero.Speed = CapturedSpeed;
        hero.Position = position;
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        return hero;
    }

    private static MatchRuntime StartSolo(string orderChampion)
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion, chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        runtime.Waves!.AutomaticSpawningEnabled = false;
        return runtime;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));
}
