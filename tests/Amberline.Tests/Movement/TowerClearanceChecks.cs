using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>Captured Map1 turret gaps and rejected sub-cell movement.</summary>
public static class TowerClearanceChecks
{
    private static readonly Position2 Tower =
        MapDefinitions.TurretPositions["Turret_T1_C_04"];

    // Batch 61: heroes step through the client's per-actor update in float32 (24-bit x87 precision), so a step of speed * dt is exact
    // only to the ulp of the coordinate (4.9e-4 between 4096 and 8191, 9.8e-4 above): two ulps of slack, not a behavioural tolerance.
    private const double FloatStepSlack = 2e-3;

    // Batch 61: a frame with a collider (a turret, the Nexus, an inhibitor) runs the client's collider branch, whose speed governor
    // (ClientGovern) only clamps a frame speed whose square leaves the SpeedDeadband around the march squared, so a contact frame may
    // step up to sqrt(1 + SpeedDeadband) of the march; a free frame marches exactly speed * dt (float32 slack only).
    private static double StepBudget(MatchRuntime runtime, Entity actor, double dt) =>
        Math.Max(1.0, actor.Speed) * dt * (runtime.Movement.ColliderIds(actor.NetId).Count > 0
            ? Math.Sqrt(1.0 + runtime.Movement.Settings.Collision.SpeedDeadband) : 1.0) + FloatStepSlack;
    // Batch 61 (heroes on the client's contact solver): a hero is a flagged actor and brings 0.2 of its radius to the pair test,
    // so the distance at which the solver holds it off a turret or structure is ContactClearance, not the full-width
    // BodyClearance the retired body-aware planner kept.

    // Owner decision 18 October 2026 (bot audit): a minion on client floor that the hero planner's grid refuses could never
    // plan its way back to its lane (a caster stood 240 s near (10996, 1740) in a headless match). The escape walks it to the
    // nearest cell the planner accepts, so that projection must always land on one.
    [Check("movement.hero-pathing-floor-projection-lands-on-planner-cells")]
    public static void HeroPathingFloorProjectionLandsOnAPlannerCell()
    {
        var navigation = NavigationGrid.LoadDefault();
        var refused = 0;
        for (var column = 0; column < navigation.CountX; column += 7)
        for (var row = 0; row < navigation.CountY; row += 7)
        {
            var point = navigation.CellToWorld(new NavCell(column, row));
            if (!navigation.IsMovementWalkable(point) || navigation.IsHeroPathingWalkable(point)) continue;
            refused++;
            var floor = navigation.ProjectToHeroPathingFloor(point);
            TestAssert.True(navigation.IsHeroPathingWalkable(floor),
                $"the projection of {point} lands on a cell the hero planner accepts");
            TestAssert.True(Distance(point, floor) <= 50.0 * navigation.CellSize * 1.5,
                $"the projection of {point} stays within its search radius");
        }
        TestAssert.True(refused > 0, "Map1 has client floor that the hero planner refuses (the turret stamps)");
        var accepted = navigation.CellToWorld(new NavCell(navigation.CountX / 2, navigation.CountY / 2));
        if (navigation.IsHeroPathingWalkable(accepted))
            TestAssert.Equal(accepted, navigation.ProjectToHeroPathingFloor(accepted),
                "a point the planner accepts is its own projection");
    }
    // P3, 18 October 2026 (replaces W5's check of the non-retail contact-distance guard): the Nexus is no actor, it is a stamped circle
    // of its pathfinding radius in the client's nav grid (the Nexus's stamp), so a route can end anywhere outside the stamped cells,
    // as the client's end snap does, and never on one. Written against the code, not run (owner rule).
    [Check("movement.hero-route-end-stays-off-stamped-building-cells")]
    public static void AHeroRouteNeverEndsOnAStampedBuildingCell()
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var actor = runtime.Session.Participants.Single().Hero!;
        actor.HeroAutoAcquire = false;
        actor.Speed = 375;
        var nexus = runtime.World.Entities.Values.Single(entity => entity.Name == "HQ_T1");
        var stamp = MapDefinitions.StructureStamps.Single(entry => entry.Name == "HQ_T1");
        var planned = 0;
        foreach (var degrees in new[] { 15.0, 35.0, 55.0, 75.0 })
        foreach (var radius in new[] { 300.0, 335.0, 355.0, 370.0 })
        {
            actor.Position = new Position2(26.0, 264.0);
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;
            var angle = degrees * Math.PI / 180.0;
            var click = new Position2(nexus.Position.X + radius * Math.Cos(angle), nexus.Position.Z + radius * Math.Sin(angle));
            var route = runtime.Movement.SetMoveOrder(actor.NetId, click);
            if (route.Count < 2) continue;
            planned++;
            var end = route[^1];
            TestAssert.True(runtime.Movement.Navigation.IsMovementWalkable(end),
                $"the route to {click} ends at {end}, on a cell the client's nav grid blocks");
            var cell = runtime.Movement.Navigation.CellToWorld(runtime.Movement.Navigation.WorldToCell(end));
            TestAssert.True(Distance(cell, nexus.Position) > stamp.Radius,
                $"the end cell of the route to {click} lies {Distance(cell, nexus.Position):0.0} from the Nexus, inside its stamp {stamp.Radius:0.0}");
        }
        TestAssert.True(planned > 0, "at least one click around the Nexus plans a route");
    }

    // P3: the Nexus and the inhibitors are stamped cells, not registered actors (their nav-grid stamps
    // write nav-cell flags only), so no movement body, no solver neighbour and no planner actor is left of them; a turret keeps its body.
    [Check("movement.nexus-and-inhibitors-have-no-movement-body")]
    public static void NexusAndInhibitorsAreNoActors()
    {
        using var runtime = StartSolo();
        var bodies = runtime.Movement.NpcBodies();
        var hero = runtime.Session.Participants.Single().Hero!;
        foreach (var entity in runtime.World.Entities.Values.Where(entity => entity.Kind is EntityKind.Nexus or EntityKind.Inhibitor))
        {
            TestAssert.Equal(0.0, runtime.Movement.MovementRadius(entity), $"{entity.Name} has no movement radius");
            TestAssert.Equal(0.0, runtime.Movement.PairMovementRadius(entity, hero), $"{entity.Name} has no pair radius");
            TestAssert.False(bodies.Contains(entity), $"{entity.Name} is not a neighbour of the solver");
        }
        foreach (var turret in runtime.World.Entities.Values.Where(entity => entity.Kind == EntityKind.Turret && entity.Alive))
            TestAssert.True(bodies.Contains(turret) && runtime.Movement.MovementRadius(turret) > 0.0,
                $"{turret.Name} stays a registered actor");
    }

    private static readonly Position2 CapturedStart = new(4819.0, 4489.0);
    private static readonly Position2 CapturedGoal =
        new(5046.5009765625, 4805.96337890625);
    private static readonly Position2 CapturedOrder73Start =
        new(5472.4659000585525, 6314.276010409192);
    private static readonly Position2 CapturedOrder73Goal =
        new(5501.6025390625, 5956.431640625);
    private static readonly Position2 CapturedOrder74Start =
        new(5546.462409253352, 6314.99477809345);
    private static readonly Position2 CapturedOrder74Goal =
        new(4828.83203125, 5361.9072265625);
    private static readonly Position2 DistantInnerMidStart =
        new(3715.8172680619837, 1677.0946117024844);
    private static readonly Position2 NearbyInnerMidStart =
        new(4573.35884198809, 4427.21102182405);
    private static readonly Position2 DistantInnerMidGoal =
        new(4773, 4495);
    private static readonly Position2 FirstTrappedInnerMidStart =
        new(4764.202157031926, 4441.215241251972);
    private static readonly Position2 SecondTrappedInnerMidStart =
        new(4743.953841251064, 4444.957462598021);
    private static readonly Position2 ThirdTrappedInnerMidStart =
        new(4747, 4467);
    private static readonly Position2 TrappedInnerMidEscapeGoal =
        new(4598.45947265625, 4304.79296875);
    private static readonly Position2 TrappedInnerMidNorthEastGoal =
        new(5044.5078125, 4750.783203125);
    private static readonly Position2 Entry42Start =
        new(4818.987470372766, 4537.753158373411);
    private static readonly Position2 Entry42Goal =
        new(4769.83984375, 4402.45166015625);
    private static readonly Position2 Entry127Start =
        new(4699.27713238601, 4385.966008259386);
    private static readonly Position2 Entry127Goal =
        new(4843.1904296875, 4473.46240234375);
    private static readonly Position2 FountainStart =
        new(768.6609092979285, 698.3170567628648);
    private static readonly Position2 FountainGoal =
        new(4786.35791015625, 4440.16845703125);
    private static readonly Position2 CapturedWideFallbackStart =
        new(4455.0, 4059.0);
    private static readonly Position2 CapturedWideFallbackGoal =
        new(5049.42333984375, 4637.77294921875);
    // Batch 61: the click is on blocked floor; the stock end-cell snap (SetToNearestGetToAbleCell, vector-verified port) picks the
    // nearest free cell centre, 27 units from the click. The retired server planner stopped at (4947, 4665), 100 units short.
    private static readonly Position2 CapturedWideFallbackResolvedGoal =
        new(5047.0, 4665.0);
    private static readonly Position2 CapturedTowerCircleStart =
        new(4789.0, 4445.0);
    private static readonly Position2 CapturedTowerCircleGoal =
        new(4501.6865234375, 4732.0029296875);
    private static readonly Position2 CapturedTowerCircleReturnStart =
        new(4503.0, 4733.0);
    private static readonly Position2 CapturedTowerCircleReturnGoal =
        new(4811.47900390625, 4447.36865234375);

    [Check("movement.hero-exact-grid-rejects-captured-cell-chord")]
    public static void HeroLosKeepsSubcellOriginForCapturedChord()
    {
        var navigation = NavigationGrid.LoadDefault();
        var firstWaypoint = new Position2(5597.0, 6215.0);
        TestAssert.True(navigation.HasMovementLineOfSight(
                CapturedOrder74Start, firstWaypoint),
            "the old cell-centre movement LOS accepts the captured chord");
        TestAssert.False(navigation.HasHeroMovementLineOfSight(
                CapturedOrder74Start, firstWaypoint),
            "hero movement LOS must traverse the cells crossed by the exact origin");

        var route = navigation.FindHeroPath(CapturedOrder73Start,
            CapturedOrder73Goal);
        TestAssert.True(route.Count > 1,
            "exact hero planning retains a route for the pre-stall order");
        TestAssert.Equal(CapturedOrder73Start, route[0],
            "hero planning retains the authoritative sub-cell origin");
        TestAssert.True(Distance(route[^1], CapturedOrder73Goal) <= 3.0,
            "hero planning retains the requested open-cell endpoint");
    }

    [Check("movement.hero-distant-inner-mid-route-uses-follower-safe-terrain")]
    public static void DistantInnerMidRouteUsesFollowerSafeTerrainFromBothOrigins()
    {
        // Batch 61: the follower is validated at the hero frame step the movement system last ticked with, so the route is planned
        // after one tick at each cadence (the production hero step and the NPC step) and walked at the same one.
        foreach (var dt in new[] { MovementSystem.HeroTickSeconds, MovementSystem.FixedTickSeconds })
        foreach (var (start, goal, expectedOrNull) in new (Position2, Position2, Position2?)[]
        {
            (DistantInnerMidStart, DistantInnerMidGoal, DistantInnerMidGoal),
            (NearbyInnerMidStart, DistantInnerMidGoal, DistantInnerMidGoal),
            (DistantInnerMidGoal, DistantInnerMidStart, DistantInnerMidStart),
            (DistantInnerMidGoal, NearbyInnerMidStart, NearbyInnerMidStart),
            (FountainStart, FountainGoal, new Position2(4787, 4441)),
            (new Position2(4787, 4441), FountainStart, FountainStart),
            // Captured order 110 clicks within the inner turret's contact distance. Batch 61: the end is the stock end-cell snap, which
            // also keeps the contact distance of the turret (W5, non-retail); it is judged by its properties below, not by a position.
            (DistantInnerMidStart, new Position2(4763.1884765625, 4500.9208984375), (Position2?)null),
        })
        {
            using var runtime = StartSolo();
            runtime.Movement.Tick(dt);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = 320;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;

            var staticObstacles = runtime.World.Entities.Values
                .Where(entity => entity.Alive && entity.Speed <= 0.0 &&
                    (entity.UnitType == UnitType.Turret ||
                     entity.UnitType == UnitType.Structure) && entity.Radius > 0.0)
                .Select(entity => new NavigationObstacle(entity.Position,
                    runtime.Movement.ContactClearance(actor, entity)))
                .ToArray();
            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"one-click route was rejected: {start} -> {goal}");
            var bounds = new NavigationBounds(runtime.Navigation.MinX, runtime.Navigation.MaxX,
                runtime.Navigation.MinZ, runtime.Navigation.MaxZ);
            TestAssert.Equal(Geometry.ClientWaypoints(new[] { start }, bounds)[0], route[0],
                "the route begins at the transmitted grid anchor");
            // Batch 61 (SeatWireAnchorOnFloor, NOT RETAIL): the stock client seats its actor on the first waypoint's lattice point,
            // and so does the server's hero; planning moves it onto that anchor (less than one cell of the two-unit lattice) and nowhere else.
            TestAssert.Equal(route[0], actor.Position,
                "planning seats the hero on the transmitted grid anchor");
            TestAssert.True(Distance(start, actor.Position) < 2.0 * Math.Sqrt(2.0),
                "planning moves the authoritative start by less than one cell of the two-unit wire lattice");
            var expectedGoal = expectedOrNull ?? route[^1];
            if (expectedOrNull is null)
            {
                var turret = runtime.World.Entities.Values.Single(entity =>
                    entity.Alive && entity.UnitType == UnitType.Turret && entity.Position == Tower);
                TestAssert.True(Distance(route[^1], goal) <= runtime.Navigation.CellSize * 2.0 + 3.0 &&
                        runtime.Navigation.IsMovementWalkable(route[^1]),
                    $"the snapped end stays within two cells of the click on floor: {goal}");
                TestAssert.True(Distance(route[^1], Tower) >= runtime.Movement.ContactClearance(actor, turret),
                    $"the snapped end keeps the turret's contact distance: {goal}");
            }
            TestAssert.True(Distance(route[^1], expectedGoal) <= 3.0,
                $"one-click route silently snapped short: {start} -> {goal}");
            TestAssert.Equal(goal, runtime.Movement.RetainedHeroGoal(actor)!.Value,
                "the requested long destination remains attached to the route");

            var previous = actor.Position;
            var blocked = new ClientFrameBlockedRun();
            for (var tick = 0; tick < 30.0 / dt && Distance(actor.Position, expectedGoal) > 3.0; tick++)
            {
                runtime.Movement.Tick(dt);
                // The client's free-movement frame has no terrain gate and its route may clip a blocked cell's corner (W8, 18 October
                // 2026): a frame may end on a blocked cell, the hero may not stay there (ClientFrameBlockedRun).
                blocked.Observe(runtime.Navigation.IsMovementWalkable(actor.Position), dt,
                    () => $"step at tick {tick}: {start} -> {goal}, hero at {actor.Position}");
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous,
                        actor.Position, staticObstacles),
                    $"step crossed a static body at tick {tick}: {start} -> {goal}");
                TestAssert.True(Distance(previous, actor.Position) <=
                        StepBudget(runtime, actor, dt),
                    $"step exceeded the fixed movement budget at tick {tick} (dt {dt}): {start} -> {goal}; {previous} -> {actor.Position} = {Distance(previous, actor.Position):R} vs {actor.Speed * dt:R}");
                previous = actor.Position;
            }
            TestAssert.True(Distance(actor.Position, expectedGoal) <= 3.0,
                $"one-click route did not complete: {start} -> {goal}");
        }
    }

    [Check("movement.hero-trapped-inner-mid-escape-from-exact-start")]
    public static void TrappedInnerMidStartEscapesWithoutTinyClick()
    {
        foreach (var dt in new[] { MovementSystem.HeroTickSeconds, MovementSystem.FixedTickSeconds })
        foreach (var (start, goal, speed) in new[]
        {
            // Native retest 76707264: all eight ordinary clicks rejected
            // after Teleport #7, until the player clicked within this cell.
            (new Position2(4745, 4473), new Position2(4311.8525390625, 4747.80859375), 320.0),
            (new Position2(4745, 4473), new Position2(4286.8603515625, 4639.9189453125), 320.0),
            (new Position2(4745, 4473), new Position2(4690.87939453125, 4789.66455078125), 320.0),
            (new Position2(4745, 4473), new Position2(4927.43212890625, 4777.939453125), 320.0),
            (new Position2(4745, 4473), new Position2(5042.6455078125, 4777.6015625), 320.0),
            (new Position2(4745, 4473), new Position2(4078.417724609375, 3287.341796875), 320.0),
            (new Position2(4745, 4473), new Position2(4524.9091796875, 3752.72216796875), 320.0),
            (new Position2(4745, 4473), new Position2(4621.7236328125, 3653.5791015625), 320.0),
            (FirstTrappedInnerMidStart, TrappedInnerMidEscapeGoal, 370.0),
            (FirstTrappedInnerMidStart, TrappedInnerMidNorthEastGoal, 370.0),
            (SecondTrappedInnerMidStart, TrappedInnerMidEscapeGoal, 370.0),
            (SecondTrappedInnerMidStart, TrappedInnerMidNorthEastGoal, 370.0),
            (ThirdTrappedInnerMidStart, TrappedInnerMidEscapeGoal, 370.0),
            (ThirdTrappedInnerMidStart, TrappedInnerMidNorthEastGoal, 370.0),
        })
        {
            using var runtime = StartSolo();
            runtime.Movement.Tick(dt);
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = speed;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;

            var staticObstacles = runtime.World.Entities.Values
                .Where(entity => entity.Alive && entity.Speed <= 0.0 &&
                    (entity.UnitType == UnitType.Turret ||
                     entity.UnitType == UnitType.Structure) && entity.Radius > 0.0)
                .Select(entity => new NavigationObstacle(entity.Position,
                    runtime.Movement.ContactClearance(actor, entity)))
                .ToArray();
            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"one-click trapped-start escape was rejected: {start} -> {goal}");
            TestAssert.Equal(Geometry.ClientWaypoints(new[] { start },
                    new NavigationBounds(runtime.Navigation.MinX, runtime.Navigation.MaxX,
                        runtime.Navigation.MinZ, runtime.Navigation.MaxZ))[0], route[0],
                "escape route begins at the transmitted trapped-start anchor");
            // Batch 61 (SeatWireAnchorOnFloor, NOT RETAIL): the hero is seated on the transmitted anchor, as the stock client seats its
            // actor, so the authoritative start and the transmitted start are one point and the connector must be legal from it.
            TestAssert.Equal(route[0], actor.Position,
                "planning seats the trapped hero on the transmitted anchor and nowhere else");
            TestAssert.True(Distance(start, actor.Position) < 2.0 * Math.Sqrt(2.0),
                "seating moves the trapped start by less than one cell of the two-unit wire lattice");
            // Batch 61: the stock client plans out of a stamped cell (the terrain-only hero planner returns nothing from this start) and
            // lets a frame cross the corner of a blocked cell: its only per-frame terrain gate is the destination cell, and only inside
            // its collider branch. The connector is judged by the cell it lands on, not by the exact cells its chord crosses.
            TestAssert.True(runtime.Navigation.IsMovementWalkable(actor.Position) &&
                    runtime.Navigation.IsMovementWalkable(route[1]),
                "escape connector must start and end on movement floor");
            TestAssert.Equal(goal,
                runtime.Movement.RetainedHeroGoal(actor)!.Value,
                "one-click escape retains its ordinary destination");
            // Captured click 34 is inside terrain. It must escape toward the
            // normal legal approach; the other clicks must reach their point.
            var expectedGoal = runtime.Navigation.IsMovementWalkable(goal)
                ? goal : route[^1];
            TestAssert.True(runtime.Navigation.IsMovementWalkable(expectedGoal) &&
                    Distance(start, expectedGoal) > actor.Radius,
                "trapped-start route must leave the landing for a legal destination");

            var previous = actor.Position;
            var previousBlocked = false;
            for (var tick = 0; tick < 12.0 / dt &&
                 Distance(actor.Position, expectedGoal) > 3.0; tick++)
            {
                runtime.Movement.Tick(dt);
                // The client's free-movement frame has no terrain gate (its actor collision handler tests the destination cell only
                // inside its collider branch), so a frame that clips the corner of a stamped cell may end in it; that is one frame at a
                // turn, never a lodging.
                var blocked = !runtime.Navigation.IsMovementWalkable(actor.Position);
                TestAssert.False(blocked && previousBlocked,
                    $"trapped-start escape stayed in blocked cells at tick {tick}: {start}");
                previousBlocked = blocked;
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous,
                        actor.Position, staticObstacles),
                    $"trapped-start escape crossed a static body at tick {tick}: {start}");
                TestAssert.True(Distance(previous, actor.Position) <=
                        StepBudget(runtime, actor, dt),
                    $"trapped-start escape exceeded its movement budget at tick {tick}: {start}");
                previous = actor.Position;
            }
            TestAssert.True(Distance(actor.Position, expectedGoal) <= 3.0,
                $"trapped-start escape did not complete: {start} -> {goal}");
        }
    }

    [Check("movement.hero-inner-mid-entry-redirect-escapes")]
    public static void InnerMidEntryCanRedirectAfterCapturedTrap()
    {
        foreach (var (entry, entryGoal, trapped, ticks, redirectGoal) in new[]
        {
            (Entry42Start, Entry42Goal, FirstTrappedInnerMidStart, 3,
                TrappedInnerMidEscapeGoal),
            (Entry127Start, Entry127Goal, SecondTrappedInnerMidStart, 2,
                TrappedInnerMidNorthEastGoal),
        })
        {
            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = 370;
            actor.Position = entry;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;

            var entryRoute = runtime.Movement.SetMoveOrder(actor.NetId, entryGoal);
            TestAssert.True(entryRoute.Count > 1,
                $"captured entry route was rejected: {entry} -> {entryGoal}");
            for (var tick = 0; tick < ticks; tick++) runtime.Movement.Tick(0.1);
            // Batch 20: hero chords keep half a cell from terrain, so the entry now bends
            // around the wall corner instead of grazing it at 10 units and is 30-40 units
            // short of the captured trap after the same ticks. Redirect from the captured
            // trapped start itself, which the escape checks above also cover.
            TestAssert.True(Distance(actor.Position, trapped) < Distance(entry, trapped),
                $"captured entry did not approach its trapped start after {ticks} ticks");
            actor.Position = trapped;
            actor.Waypoints.Clear();

            var redirect = runtime.Movement.SetMoveOrder(actor.NetId, redirectGoal);
            TestAssert.True(redirect.Count > 1,
                $"one-click redirect was rejected from captured trap: {trapped}");
            var beforeRedirect = actor.Position;
            for (var tick = 0; tick < 8 &&
                 Distance(actor.Position, beforeRedirect) <= 3.0; tick++)
                runtime.Movement.Tick(0.1);
            TestAssert.True(Distance(actor.Position, beforeRedirect) > 3.0,
                $"captured trap redirect made no progress: {trapped}");
        }
    }

    [Check("movement.hero-long-terrain-routes-remain-safe-on-wire-grid")]
    public static void LongTerrainRoutesValidateTheirTransmittedVertices()
    {
        var navigation = NavigationGrid.LoadDefault();
        var bounds = new NavigationBounds(navigation.MinX, navigation.MaxX,
            navigation.MinZ, navigation.MaxZ);
        foreach (var (start, goal) in new[]
        {
            (new Position2(26, 264), new Position2(7655, 7755)),
            (new Position2(26, 264), new Position2(13343, 4571)),
            (new Position2(13933.79, 14170.09), new Position2(825, 8851)),
        })
        {
            var route = Geometry.ClientWaypoints(navigation.FindHeroPath(start, goal), bounds);
            TestAssert.True(route.Count > 1, $"long hero route must exist: {start} -> {goal}");
            TestAssert.True(navigation.HasHeroMovementLineOfSight(start, route[1]),
                "the exact authoritative first segment must remain clear");
            for (var index = 0; index + 1 < route.Count; index++)
            {
                // Batch 223 item 1: every hero chord is planned on the movement view (the client's chord walk), so the wire check uses it too.
                var clear = navigation.HasHeroMovementLineOfSight(route[index], route[index + 1]);
                TestAssert.True(clear,
                    $"wire conversion invalidated long route segment {index}: {route[index]} -> {route[index + 1]}");
            }
        }
    }

    [Check("movement.hero-captured-orders-complete-with-static-safety")]
    public static void CapturedGroundOrdersCompleteWithoutCellOrBodyViolation()
    {
        foreach (var (start, goal) in new[]
        {
            (CapturedOrder73Start, CapturedOrder73Goal),
            (CapturedOrder74Start, CapturedOrder74Goal),
        })
        {
            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = 370;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;
            var staticObstacles = runtime.World.Entities.Values
                .Where(entity => entity.Alive && entity.Speed <= 0.0 &&
                    (entity.UnitType == UnitType.Turret ||
                     entity.UnitType == UnitType.Structure) &&
                    entity.Radius > 0.0)
                .Select(entity => new NavigationObstacle(entity.Position,
                    runtime.Movement.ContactClearance(actor, entity)))
                .ToArray();
            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"production route was rejected: {start} -> {goal}");

            var previous = actor.Position;
            var blocked = new ClientFrameBlockedRun();
            for (var tick = 0; tick < 100 && Distance(actor.Position, goal) > 3.0; tick++)
            {
                runtime.Movement.Tick(0.1);
                var current = actor.Position;
                // The client commits a frame that ends on a blocked cell when its chord clips a corner (see the order 32 check); the
                // hero may not stay on blocked cells (ClientFrameBlockedRun).
                blocked.Observe(runtime.Navigation.IsMovementWalkable(current), 0.1,
                    () => $"captured step {tick}: {start} -> {goal}, hero at {current}");
                // playtest B4 / retail sweep, 27 Sept 2026: the 88.4-unit
                // turret radius puts captured order 73's exact start 0.24 u
                // inside its shell. Permit only outward escape of that initial
                // overlap; every later step retains the strict body check.
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous,
                        current, staticObstacles, escape: tick == 0),
                    $"captured step {tick} crossed a static structure body");
                TestAssert.True(Distance(previous, current) <=
                        StepBudget(runtime, actor, 0.1),
                    $"captured step {tick} moved farther than one authoritative frame");
                previous = current;
            }
            TestAssert.True(Distance(actor.Position, goal) <= 3.0,
                $"captured order did not complete: {start} -> {goal}");
        }
    }

    [Check("movement.hero-captured-inner-mid-terminal-connection")]
    public static void CapturedInnerMidTerminalConnectionCompletesSafely()
    {
        foreach (var (start, goal, exactGoal) in new[]
        {
            // Captured orders 11 and 64 use movement-floor clicks at the
            // isolated inner-mid terminal and must retain the exact click.
            (new Position2(1678.6795831157374, 1364.279276657972),
                new Position2(4742.55419921875, 4464.53564453125), true),
            (new Position2(4797.583396973064, 2145.0334332346447),
                new Position2(4721.1962890625, 4439.544921875), true),
            // Orders 60 and 71 click blocked floor.  The planner must select
            // a nearby safe terminal while keeping the order moving.
            (new Position2(4803.24646435066, 1557.80002097426),
                new Position2(4784.294921875, 4416.5869140625), false),
            (new Position2(4694.91634408121, 3036.5003010319583),
                new Position2(4782.48486328125, 4427.140625), false),
        })
        {
            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var navigation = runtime.Navigation;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = 370;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;

            TestAssert.True(navigation.IsMovementWalkable(start),
                $"captured start must remain on movement floor: {start}");
            TestAssert.Equal(exactGoal, navigation.IsMovementWalkable(goal),
                $"captured click floor classification changed: {start} -> {goal}");

            var staticObstacles = runtime.World.Entities.Values
                .Where(entity => entity.Alive && entity.Speed <= 0.0 &&
                    (entity.UnitType == UnitType.Turret ||
                     entity.UnitType == UnitType.Structure) && entity.Radius > 0.0)
                .Select(entity => new NavigationObstacle(entity.Position,
                    runtime.Movement.ContactClearance(actor, entity)))
                .ToArray();
            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"captured one-click route was rejected: {start} -> {goal}");
            TestAssert.Equal(runtime.Movement.ClientAnchor(start), route[0],
                "terminal connection preserves the authoritative packet anchor");
            TestAssert.True(navigation.FirstSegmentClearOfUnits(route[0], start,
                    route[1], staticObstacles) &&
                navigation.HasHeroMovementLineOfSight(start, route[1]) &&
                navigation.HasHeroMovementLineOfSight(route[0], route[1]),
                "terminal connection begins safely from exact and wire positions");
            var resolvedGoal = route[^1];
            TestAssert.True(Distance(start, resolvedGoal) > 3.0,
                "terminal recovery must commit a moving route");
            TestAssert.True(navigation.IsMovementWalkable(resolvedGoal),
                $"resolved terminal is off movement floor: {resolvedGoal}");
            if (exactGoal)
                TestAssert.True(Distance(resolvedGoal, goal) <= 3.0,
                    $"exact captured terminal changed: {start} -> {goal}");
            else
                TestAssert.True(Distance(resolvedGoal, goal) <= navigation.CellSize * 2.0 + 3.0,
                    $"blocked captured click resolved too far away: {start} -> {goal}");

            var previous = actor.Position;
            var blocked = new ClientFrameBlockedRun();
            for (var tick = 0; tick < 160 && Distance(actor.Position, resolvedGoal) > 3.0; tick++)
            {
                runtime.Movement.Tick(0.1);
                var current = actor.Position;
                blocked.Observe(navigation.IsMovementWalkable(current), 0.1,
                    () => $"captured terminal route at tick {tick}: {start} -> {goal}, hero at {current}");
                TestAssert.True(navigation.SegmentClearOfUnits(previous, current,
                        staticObstacles),
                    $"captured terminal route crossed a static body at tick {tick}");
                TestAssert.True(Distance(previous, current) <=
                        StepBudget(runtime, actor, 0.1),
                    $"captured terminal route exceeded one fixed movement step at tick {tick}");
                previous = current;
            }
            TestAssert.True(Distance(actor.Position, resolvedGoal) <= 3.0,
                $"one-click captured terminal route did not complete: {start} -> {goal}");
        }
    }

    [Check("movement.hero-captured-order32-passage-short-route")]
    public static void CapturedOrder32UsesShortPassageBothDirections()
    {
        var cases = new[]
        {
            (new Position2(4797.0, 4515.0),
                new Position2(4558.2275390625, 4342.013671875), 160, true),
            (new Position2(4558.2275390625, 4342.013671875),
                new Position2(4797.0, 4515.0), 160, true),
            // Captured order 11 crosses the same passage from a distant
            // west-side approach and retains its complete final tail.
            (new Position2(3896.72202881551, 3856.59143660777),
                new Position2(4900.51171875, 4596.10302734375), 240, false),
            (new Position2(4900.51171875, 4596.10302734375),
                new Position2(3896.72202881551, 3856.59143660777), 240, false),
        };
        foreach (var (start, goal, maxTicks, requireShortRoute) in cases)
        {
            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var navigation = runtime.Navigation;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = 370;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;

            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"captured passage route was rejected: {start} -> {goal}");
            if (requireShortRoute)
                TestAssert.True(route.Zip(route.Skip(1), (first, second) =>
                    (First: first, Second: second)).Any(segment =>
                        navigation.HasHeroMovementLineOfSight(segment.First, segment.Second) &&
                        !navigation.HasHeroPathingLineOfSight(segment.First, segment.Second)),
                $"route did not cross the measured planning margin: {start} -> {goal}");

            var normal = navigation.FindHeroPath(start, goal);
            if (requireShortRoute)
                TestAssert.True(RouteLength(route) <= 450.0,
                    $"order32 route retained the long detour: {RouteLength(route):R}");
            if (requireShortRoute)
                TestAssert.True(RouteLength(normal) > RouteLength(route) + 3.0,
                    $"passage route did not improve on the planning route: " +
                    $"normal={RouteLength(normal):R}, selected={RouteLength(route):R}");

            var staticObstacles = runtime.World.Entities.Values
                .Where(entity => entity.Alive && entity.Speed <= 0.0 &&
                    (entity.UnitType == UnitType.Turret ||
                     entity.UnitType == UnitType.Structure) && entity.Radius > 0.0)
                .Select(entity => new NavigationObstacle(entity.Position,
                    runtime.Movement.ContactClearance(actor, entity)))
                .ToArray();
            var previous = actor.Position;
            var crossedPassageMargin = false;
            for (var tick = 0; tick < maxTicks && Distance(actor.Position, goal) > 3.0; tick++)
            {
                runtime.Movement.Tick(0.1);
                var current = actor.Position;
                var midpoint = new Position2((previous.X + current.X) * 0.5,
                    (previous.Z + current.Z) * 0.5);
                crossedPassageMargin |= Distance(midpoint, Tower) < 500.0 &&
                    midpoint.X > Tower.X && midpoint.Z < Tower.Z &&
                    !navigation.HasHeroPathingLineOfSight(previous, current);
                // The client's own planner gives the 3-point passage route, whose chords clip a blocked cell's corner; its free-movement
                // frame has no terrain gate, so the frame is judged by the cell it ends in.
                TestAssert.True(navigation.IsMovementWalkable(current),
                    $"passage route ended off movement floor at tick {tick}");
                TestAssert.True(navigation.SegmentClearOfUnits(previous, current,
                        staticObstacles),
                    $"passage route crossed a static body at tick {tick}");
                TestAssert.True(Distance(previous, current) <=
                        StepBudget(runtime, actor, 0.1),
                    $"passage route exceeded its fixed step at tick {tick}");
                previous = current;
            }
            TestAssert.True(Distance(actor.Position, goal) <= 3.0,
                $"passage route did not complete: {start} -> {goal}");
            TestAssert.True(crossedPassageMargin,
                $"one-click movement did not cross the right-side passage margin: {start} -> {goal}");
        }
    }

    [Check("movement.hero-inner-mid-turn-safe-fallback-compacts-wide-detour")]
    public static void TurnSafeFallbackCompactsCapturedWideDetour()
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var navigation = runtime.Navigation;
        var actor = runtime.Session.Participants.Single().Hero!;
        actor.HeroAutoAcquire = false;
        actor.Radius = 47;
        actor.Speed = 370;
        actor.Position = CapturedWideFallbackStart;
        actor.Waypoints.Clear();
        actor.Target = null;
        actor.AttackTarget = null;
        actor.AttackMoveGoal = null;
        actor.HeroPursuit = null;
        actor.LastAttackStopPoint = null;

        var route = runtime.Movement.SetMoveOrder(actor.NetId,
            CapturedWideFallbackGoal);
        TestAssert.True(route.Count > 1,
            "captured wide-fallback order was rejected");
        TestAssert.True(Distance(route[^1], CapturedWideFallbackResolvedGoal) <= 3.0,
            "captured wide-fallback order changed its existing resolved endpoint");
        var normal = navigation.FindHeroPath(CapturedWideFallbackStart,
            CapturedWideFallbackGoal);
        TestAssert.True(RouteLength(normal) > RouteLength(route) + 3.0,
            $"compacted route must beat the normal terrain detour: " +
            $"normal={RouteLength(normal):R}, selected={RouteLength(route):R}");
        // The captured route to the retired endpoint (4947, 4665) was bounded by 860; the stock snap lies 100 units further along
        // the same line (4997 -> 5047 at z 4665), so the same compactness bound is 960.
        TestAssert.True(RouteLength(route) <= 960.0,
            $"turn-safe fallback retained the captured wide detour: " +
            $"selected={RouteLength(route):R}");
        TestAssert.True(route.Zip(route.Skip(1), (first, second) =>
                (First: first, Second: second)).Any(segment =>
                navigation.HasHeroMovementLineOfSight(segment.First, segment.Second) &&
                !navigation.HasHeroPathingLineOfSight(segment.First, segment.Second)),
            "compacted fallback must retain a real planning-margin crossing");

        var staticObstacles = runtime.World.Entities.Values
            .Where(entity => entity.Alive && entity.Speed <= 0.0 &&
                (entity.UnitType == UnitType.Turret ||
                 entity.UnitType == UnitType.Structure) && entity.Radius > 0.0)
            .Select(entity => new NavigationObstacle(entity.Position,
                runtime.Movement.ContactClearance(actor, entity)))
            .ToArray();
        var previous = actor.Position;
        for (var tick = 0; tick < 180 &&
             Distance(actor.Position, CapturedWideFallbackResolvedGoal) > 3.0; tick++)
        {
            runtime.Movement.Tick(0.1);
            var current = actor.Position;
            TestAssert.True(navigation.HasHeroMovementLineOfSight(previous, current),
                $"compacted fallback crossed terrain at tick {tick}");
            TestAssert.True(navigation.SegmentClearOfUnits(previous, current,
                    staticObstacles),
                $"compacted fallback crossed a static body at tick {tick}");
            TestAssert.True(Distance(previous, current) <=
                    StepBudget(runtime, actor, 0.1),
                $"compacted fallback exceeded one fixed movement step at tick {tick}");
            previous = current;
        }
        TestAssert.True(Distance(actor.Position, CapturedWideFallbackResolvedGoal) <= 3.0,
            "compacted fallback did not complete the captured resolved route");
    }

    [Check("movement.hero-inner-mid-turret-captured-gap-both-directions")]
    public static void CapturedHeroOrderUsesGapAndCompletesBothDirections()
    {
        var navigation = NavigationGrid.LoadDefault();
        foreach (var name in new[] { "Turret_T1_C_04", "Turret_T2_C_04" })
        {
            var turret = MapDefinitions.TurretPositions[name];
            TestAssert.False(navigation.IsHeroPathingWalkable(turret),
                $"{name} must block the hero pathing view");
            var direction = Normalize(MapDefinitions.TurretPositions[MapDefinitions.TurretFront[name]] - turret);
            var offset = new Position2(direction.X * 300.0, direction.Z * 300.0);
            TestAssert.False(navigation.HasHeroPathingLineOfSight(
                    turret - offset, turret + offset),
                $"{name} direct chord must remain statically blocked");
        }
        foreach (var (start, goal) in new[]
        {
            (CapturedStart, CapturedGoal),
            (CapturedGoal, CapturedStart),
        })
        {
            var legacy = navigation.FindPath(start, goal);
            var hero = navigation.FindHeroPath(start, goal);
            TestAssert.True(hero.Count > 1,
                $"hero route was rejected: {start} -> {goal}");
            // Batch 225 (retail 140-unit turret stamps, batch 223 hero chords): the 260u dilation this counterfactual modelled is
            // gone. The captured gap route and the navgrid route now differ by 27.2 units in both directions (direction 1: legacy
            // 418.5, hero 391.3; direction 2: legacy 390.2, hero 417.3), so the bound is that measured difference, not a shortcut.
            TestAssert.True(legacy.Count > 1 && Math.Abs(RouteLength(hero) - RouteLength(legacy)) <= 30.0,
                $"hero and legacy routes differ by more than the retail chord margin: " +
                $"legacy={RouteLength(legacy):R}, hero={RouteLength(hero):R}");

            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;
            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"production one-click route was rejected: {start} -> {goal}");

            var previous = actor.Position;
            var turretObstacle = new NavigationObstacle(Tower, MapDefinitions.TurretRadius);
            for (var tick = 0; tick < 100 && Distance(actor.Position, goal) > 3.0; tick++)
            {
                runtime.Movement.Tick(0.1);
                TestAssert.True(runtime.Navigation.HasMovementLineOfSight(previous, actor.Position),
                    $"movement grid rejected captured step {tick}: {start} -> {goal}");
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous, actor.Position,
                        new[] { turretObstacle }),
                    $"captured step {tick} crossed the static turret footprint");
                previous = actor.Position;
            }
            TestAssert.True(Distance(actor.Position, goal) <= 3.0,
                $"one-click captured order did not complete: {start} -> {goal}");
        }
    }

    [Check("movement.hero-tower-circle-compacts-guarded-route-both-directions")]
    public static void CapturedTowerCircleCompactsGuardedRouteBothDirections()
    {
        foreach (var (start, goal, expectedGoal, originalCount) in new[]
        {
            (CapturedTowerCircleStart, CapturedTowerCircleGoal,
                new Position2(4503.0, 4733.0), 13),
            (CapturedTowerCircleReturnStart, CapturedTowerCircleReturnGoal,
                new Position2(4813.0, 4449.0), 12),
        })
        {
            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var actor = runtime.Session.Participants.Single().Hero!;
            actor.HeroAutoAcquire = false;
            actor.Radius = 47;
            actor.Speed = 370;
            actor.Position = start;
            actor.Waypoints.Clear();
            actor.Target = null;
            actor.AttackTarget = null;
            actor.AttackMoveGoal = null;
            actor.HeroPursuit = null;
            actor.LastAttackStopPoint = null;

            var route = runtime.Movement.SetMoveOrder(actor.NetId, goal);
            TestAssert.True(route.Count > 1,
                $"captured tower-circle route was rejected: {start} -> {goal}");
            // Batch 61 deleted the bridge compaction (CompactHeroBridgePrefix, GuardTerrainBodyTurns), and W8 (18 October 2026) the
            // follower-safe fallback that had replaced the client's route here by its raw A* cells (13 and 12 vertices): the route is the
            // client planner's own again (four vertices). The vertex count is the planner's business, not the check's. What stays
            // asserted: it is no wide detour, within a quarter of the terrain-only hero route's length.
            TestAssert.True(RouteLength(route) <= RouteLength(runtime.Navigation.FindHeroPath(start, goal)) * 1.25,
                $"guarded tower-circle route is a wide detour: {start} -> {goal}");
            TestAssert.Equal(start, route[0],
                "compaction must retain the authoritative wire anchor");
            TestAssert.True(Distance(route[^1], expectedGoal) <= 3.0,
                $"compaction changed the tower-circle terminal landing: {start} -> {goal}");

            var turretObstacle = new NavigationObstacle(Tower,
                runtime.Movement.ContactClearance(actor,
                    runtime.World.Entities.Values.Single(entity =>
                        entity.Alive && entity.UnitType == UnitType.Turret &&
                        entity.Position == Tower)));
            for (var index = 0; index + 1 < route.Count; index++)
            {
                // The route is the client planner's own: its chords are clear by the client's line of sight on the movement grid
                // (HasMovementLineOfSight; the 65-unit dilated hero-pathing grid the interior segments used to be held to belonged to the
                // retired server planner, batch 61), and the first and last leg, which the hero walks from and to exactly, keep the exact test.
                var terrainClear = index == 0 || index == route.Count - 2
                    ? runtime.Navigation.HasHeroMovementLineOfSight(
                        route[index], route[index + 1])
                    : runtime.Navigation.HasMovementLineOfSight(
                        route[index], route[index + 1]);
                TestAssert.True(terrainClear,
                    $"tower-circle segment is not clear on the client's movement grid {index}");
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(route[index],
                        route[index + 1], new[] { turretObstacle }, escape: index == 0),
                    $"compacted tower-circle segment crossed turret body {index}");
            }

            var previous = actor.Position;
            var blocked = new ClientFrameBlockedRun();
            for (var tick = 0; tick < 100 &&
                 Distance(actor.Position, expectedGoal) > 3.0; tick++)
            {
                runtime.Movement.Tick(0.1);
                TestAssert.True(Distance(previous, actor.Position) <=
                        StepBudget(runtime, actor, 0.1),
                    $"compacted tower-circle step exceeded one movement budget {tick}");
                blocked.Observe(runtime.Navigation.IsMovementWalkable(actor.Position), 0.1,
                    () => $"tower-circle follower at tick {tick}: {start} -> {goal}, hero at {actor.Position}");
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous,
                        actor.Position, new[] { turretObstacle }),
                    $"compacted tower-circle step crossed turret body {tick}");
                previous = actor.Position;
            }
            TestAssert.True(Distance(actor.Position, expectedGoal) <= 3.0,
                $"compacted tower-circle route did not complete: {start} -> {goal}");
        }
    }

    private static MatchRuntime StartSolo()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Yorick", chaosChampion: "Annie", solo: true,
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

    private static double RouteLength(IReadOnlyList<Position2> route) =>
        route.Zip(route.Skip(1), (first, second) => Distance(first, second)).Sum();

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private static Position2 Normalize(Position2 value)
    {
        var length = Distance(value, Position2.Zero);
        return new Position2(value.X / length, value.Z / length);
    }
}
