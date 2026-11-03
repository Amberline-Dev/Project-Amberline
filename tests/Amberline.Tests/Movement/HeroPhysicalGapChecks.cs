using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>Production hero routes keep recovered body clearance without an extra route shell.</summary>
public static class HeroPhysicalGapChecks
{
    [Check("movement.hero-grid-boundary-endpoints")]
    public static void ExactGridTraversalStopsAtBoundaryEndpoints()
    {
        var navigation = CreateOpenGrid();
        var endpoint = new Position2(500, 500);
        foreach (var start in new[]
        {
            new Position2(300, 700), new Position2(700, 300),
            new Position2(300, 300), new Position2(700, 700),
            new Position2(500, 700), new Position2(700, 500),
            new Position2(300, 500.0000000000001),
            new Position2(500.0000000000001, 300), endpoint,
        })
        {
            TestAssert.True(navigation.HasHeroMovementLineOfSight(start, endpoint) &&
                navigation.HasHeroMovementLineOfSight(endpoint, start),
                $"open-grid boundary segment must work in both directions: {start}");
            TestAssert.True(navigation.HasHeroPathingLineOfSight(start, endpoint) &&
                navigation.HasHeroPathingLineOfSight(endpoint, start),
                $"pathing must not advance past an already reached endpoint axis: {start}");
        }
    }

    // Batch 61: a hero's route is the stock client planner's and the hero walks it on the client's contact solver as a flagged
    // actor, so it brings 0.2 of its radius to the pair test: the distance at which a hero is stopped against a body
    // is ContactClearance, not the full-width BodyClearance the retired body-aware planner kept. The fixture keeps its eight-unit
    // opening between the full-width discs; the thin hero now crosses it on the client's straight walk.
    [Check("movement.hero-physical-gap-uses-body-clearance")]
    public static void PhysicalGapRemainsPassableAfterWireQuantization()
    {
        foreach (var dt in new[] { MovementSystem.HeroTickSeconds, MovementSystem.FixedTickSeconds })
        foreach (var (team, reverse, angle, offset) in Cases())
        {
            var runtime = Start(team);
            runtime.Movement.Tick(dt);
            var hero = Hero(runtime, team);
            var axis = new Position2(Math.Cos(angle), Math.Sin(angle));
            var normal = new Position2(-axis.Z, axis.X);
            var center = new Position2(7300 + offset.X, 7000 + offset.Z);
            var start = Shift(Shift(center, axis, reverse ? 350 : -350), normal, 30);
            var goal = Shift(Shift(center, axis, reverse ? -350 : 350), normal, 30);
            Place(hero, start);

            var upper = Body(runtime, team, center);
            var lower = Body(runtime, team, center);
            var clearance = runtime.Movement.BodyClearance(hero, upper);
            var contact = runtime.Movement.ContactClearance(hero, upper);
            upper.Position = Shift(center, normal, clearance + 4);
            lower.Position = Shift(center, normal, -clearance - 4);
            var obstacles = Obstacles(contact, upper, lower);

            var route = runtime.Movement.SetMoveOrder(hero.NetId, goal).ToArray();
            var label = $"{team} reverse={reverse} angle={angle:R} offset={offset} dt={dt:R}";
            TestAssert.True(route.Length > 1, $"{label}: an eight-unit physical gap must route");
            AssertRouteClear(runtime, route, obstacles, $"{label}: wire route");
            // The order seats the hero on its wire anchor (batch 61, SeatWireAnchorOnFloor), which is the route's first point.
            AssertRouteClear(runtime, route.Skip(1).Prepend(hero.Position).ToArray(), obstacles,
                $"{label}: authoritative route");
            // The route passes between the two bodies (the crossing lies where both are at least their contact distance away).
            TestAssert.True(CrossesBand(route, center, axis, clearance + 4 - contact),
                $"{label}: route must use the physical opening");

            // The retired body-aware planner kept the full-width clearance from both discs and so refused this opening; the client
            // planner does not inflate for a hero, so its straight walk does pass inside that shell.
            var fullWidth = Obstacles(clearance, upper, lower);
            TestAssert.False(RouteClearOfBodies(runtime.Navigation, route, fullWidth),
                $"{label}: the thin flagged hero's route passes inside the full-width clearance the retired planner kept");

            var previous = hero.Position;
            for (var tick = 0; tick < 5.0 / dt && Distance(hero.Position, goal) > 3; tick++)
            {
                runtime.Movement.Tick(dt);
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous, hero.Position, obstacles),
                    $"{label}: native fixed-origin tick {tick} cut through a body's contact distance");
                AssertBodyClearance(hero, contact, new[] { upper, lower },
                    $"{label}: tick {tick}");
                previous = hero.Position;
            }
            TestAssert.True(Distance(hero.Position, goal) <= 3,
                $"{label}: one order must finish through the open gap");
        }
    }

    // Batch 61: the figure that closes a gap against a hero is the contact distance of the client's solver (the hero's thin 0.2 r plus
    // the body's full radius plus the pair skin), not the full-width BodyClearance of the retired planner, so the fixture overlaps the
    // contact discs. The retired planner either detoured or kept a "retained wait"; the client planner always detours here and a failed
    // plan is a stationary anchor (RetainHeroWait is deleted), so the wait branch is gone.
    [Check("movement.hero-subphysical-gap-remains-blocked")]
    public static void OverlappingBodyClearanceCannotBecomeAPassage()
    {
        foreach (var (team, reverse, angle, offset) in Cases())
        {
            var runtime = Start(team);
            var hero = Hero(runtime, team);
            var axis = new Position2(Math.Cos(angle), Math.Sin(angle));
            var normal = new Position2(-axis.Z, axis.X);
            var center = new Position2(7300 + offset.X, 7000 + offset.Z);
            var start = Shift(Shift(center, axis, reverse ? 350 : -350), normal, 30);
            var goal = Shift(Shift(center, axis, reverse ? -350 : 350), normal, 30);
            Place(hero, start);

            var upper = Body(runtime, team, center);
            var lower = Body(runtime, team, center);
            var contact = runtime.Movement.ContactClearance(hero, upper);
            upper.Position = Shift(center, normal, contact - 4);
            lower.Position = Shift(center, normal, -contact + 4);
            var obstacles = Obstacles(contact, upper, lower);
            // Independent of the skin: a thin hero disc and a full body disc never overlap.
            var physical = runtime.Movement.MoverPairRadius(hero, upper) + runtime.Movement.PairMovementRadius(upper, hero);

            var route = runtime.Movement.SetMoveOrder(hero.NetId, goal).ToArray();
            var label = $"{team} reverse={reverse} angle={angle:R} offset={offset}";
            TestAssert.True(route.Length > 1, $"{label}: the client planner detours around overlapping contact discs");
            // The planner tests whole cells against the body (HasStuckActor) and its smoothing chords may clip the solver's contact
            // circle, which the contact pass then slides the hero around; the bodies themselves are never crossed.
            var bodies = Obstacles(runtime.Movement.MovementRadius(upper), upper, lower);
            AssertRouteClear(runtime, route, bodies, $"{label}: wire detour");
            AssertRouteClear(runtime, route.Skip(1).Prepend(hero.Position).ToArray(), bodies,
                $"{label}: authoritative detour");
            TestAssert.False(CrossesBand(route, center, axis, 4.01),
                $"{label}: overlapping contact discs cannot expose a center passage");

            for (var tick = 0; tick < 60 && hero.Waypoints.Count > 0; tick++)
            {
                runtime.Movement.Tick(.1);
                AssertBodyClearance(hero, physical, new[] { upper, lower },
                    $"{label}: tick {tick}");
            }
            TestAssert.True(Distance(hero.Position, goal) <= 3, $"{label}: the detour completes with one order");
        }
    }

    [Check("movement.hero-wire-and-authoritative-first-leg-clear")]
    public static void ExactSubcellStartCannotHideBehindItsWireAnchor()
    {
        var runtime = Start(Team.Order);
        var hero = Hero(runtime, Team.Order);
        var bounds = new NavigationBounds(runtime.Navigation.MinX, runtime.Navigation.MaxX,
            runtime.Navigation.MinZ, runtime.Navigation.MaxZ);
        var anchor = Geometry.ClientWaypoints([new Position2(7000, 7000)], bounds)[0];
        // This side of the map midpoint truncates upward toward zero. Moving
        // below the anchor keeps both forms on the same wire coordinate.
        var start = new Position2(anchor.X, anchor.Z - 1.9);
        var goal = new Position2(anchor.X + 600, anchor.Z);
        Place(hero, start);
        var body = Body(runtime, Team.Order, anchor);
        var clearance = runtime.Movement.BodyClearance(hero, body);
        body.Position = new Position2(anchor.X + 100, anchor.Z - clearance - .75);
        var obstacles = Obstacles(clearance, body);
        var directWire = Geometry.ClientWaypoints([start, goal], bounds).ToArray();

        TestAssert.True(RouteClearOfBodies(runtime.Navigation, directWire, obstacles),
            "the wire anchor alone makes the direct preimage look clear");
        TestAssert.False(RouteClearOfBodies(runtime.Navigation,
                directWire.Skip(1).Prepend(start).ToArray(), obstacles),
            "the exact authoritative first leg exposes the physical collision");

        var sharedOverlap = new NavigationObstacle(
            new Position2(anchor.X + 1, anchor.Z + 24), 100);
        TestAssert.True(runtime.Navigation.FirstSegmentClearOfUnits(anchor,
                new Position2(anchor.X + 1, anchor.Z - 1),
                new Position2(anchor.X - 200, anchor.Z), [sharedOverlap]),
            "a quantized shared overlap may turn outward within its anchor displacement");
        TestAssert.False(runtime.Navigation.FirstSegmentClearOfUnits(anchor,
                new Position2(anchor.X + 1, anchor.Z - 1),
                new Position2(anchor.X - 2, anchor.Z), [sharedOverlap]),
            "a shared overlap cannot keep deepening beyond its anchor displacement");
        TestAssert.False(runtime.Navigation.FirstSegmentClearOfUnits(anchor,
                new Position2(anchor.X + 2.1, anchor.Z),
                new Position2(anchor.X - 200, anchor.Z), [sharedOverlap]),
            "shared-overlap treatment is bounded to one wire coordinate step");

        var route = runtime.Movement.SetMoveOrder(hero.NetId, goal).ToArray();
        // Batch 61: the order seats the hero on the lattice point its planned path starts on (SeatWireAnchorOnFloor, the point the
        // stock client seats its actor on), so the exact origin IS the wire origin and the subcell preimage cannot hide a collision.
        TestAssert.Equal(anchor, hero.Position, "the order seats the hero on its wire anchor");
        TestAssert.True(route.Length > 1,
            "a wire-only false clear must still route instead of waiting forever");
        AssertRouteClear(runtime, route, obstacles, "replacement wire route");
        AssertRouteClear(runtime, route.Skip(1).Prepend(hero.Position).ToArray(), obstacles,
            "replacement authoritative route");
        for (var tick = 0; tick < 50 && Distance(hero.Position, goal) > 3; tick++)
        {
            runtime.Movement.Tick(.1);
            AssertBodyClearance(hero, clearance, new[] { body }, $"subcell detour tick {tick}");
        }
        TestAssert.True(Distance(hero.Position, goal) <= 3,
            "the safe subcell detour completes without another click");
    }

    // Batch 61: the hero's collision figure is the contact distance of the client's solver (0.2 r + r + skin), and its route is the
    // client planner's, not a phase-safe detour around the full-width discs. The fixture geometry is unchanged (the formerly selected
    // centre turn still cuts the full-width disc on the 32.5-unit phase); the route and the walk are judged against the contact discs.
    [Check("movement.hero-gap-fixed-origin-corner-phase")]
    public static void PhaseShiftedCornerCannotCutBodyClearance()
    {
        var runtime = Start(Team.Chaos);
        var hero = Hero(runtime, Team.Chaos);
        hero.Speed = 325;
        var start = new Position2(7317.190633, 7004.387769);
        var goal = new Position2(6953, 7033);
        Place(hero, start);

        var center = new Position2(7301.83, 7001.27);
        var upper = Body(runtime, Team.Chaos, center);
        var lower = Body(runtime, Team.Chaos, center);
        var clearance = runtime.Movement.BodyClearance(hero, upper);
        var contact = runtime.Movement.ContactClearance(hero, upper);
        upper.Position = new Position2(center.X, center.Z + clearance + 4);
        lower.Position = new Position2(center.X, center.Z - clearance - 4);
        var fullWidth = Obstacles(clearance, upper, lower);
        var obstacles = Obstacles(contact, upper, lower);

        var oldEndpoint = new Position2(7300.990552, 7005.660510);
        TestAssert.False(runtime.Navigation.SegmentClearOfUnits(start, oldEndpoint, fullWidth),
            "fixture must expose the phase-shifted native corner chord of the full-width discs");

        var route = runtime.Movement.SetMoveOrder(hero.NetId, goal).ToArray();
        TestAssert.True(route.Length > 1,
            "the client planner must route through the opening between the bodies");
        AssertRouteClear(runtime, route, obstacles, "phase-safe wire route");
        AssertRouteClear(runtime, route.Skip(1).Prepend(hero.Position).ToArray(), obstacles,
            "phase-safe authoritative route");

        var previous = hero.Position;
        for (var tick = 0; tick < 50 && Distance(hero.Position, goal) > 3; tick++)
        {
            runtime.Movement.Tick(.1);
            TestAssert.True(runtime.Navigation.SegmentClearOfUnits(previous, hero.Position, obstacles),
                $"phase-safe native tick {tick} crossed a body's contact distance");
            AssertBodyClearance(hero, contact, new[] { upper, lower },
                $"phase-safe native tick {tick}");
            previous = hero.Position;
        }
        TestAssert.True(Distance(hero.Position, goal) <= 3,
            "the route completes from the unsafe native phase with one order");
    }

    // Batch 61: the fixed-origin tail validator that re-checked a retained hero route on a speed change (HeroFollowerRemainingClear,
    // HeroRouteClear) is deleted with the body-aware planner; a hero is stepped by the client's own per-frame solver, which reads the
    // speed every frame. The planner also ignores NavigationGrid.FindPathOverride (it plans on the client grid view), so the corner
    // detour of the old fixture cannot be injected. What stays true, and is asserted: the planned route clears the body, a speed
    // change in the middle of the walk neither relocates the hero nor lets it into the body, and the one order completes.
    [Check("movement.hero-speed-change-rechecks-full-corner-tail")]
    public static void SpeedChangeRechecksTheWholeFixedOriginTail()
    {
        var navigation = CreateOpenGrid();
        var bounds = new NavigationBounds(navigation.MinX, navigation.MaxX,
            navigation.MinZ, navigation.MaxZ);
        var corner = Geometry.ClientWaypoints([new Position2(500, 500)], bounds)[0];
        var start = new Position2(corner.X, corner.Z + 977);
        var goal = new Position2(corner.X + 1000, corner.Z);
        var world = new WorldState();
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero,
            Team.Order, start)
        {
            Name = "Annie", Created = true, Visible = true,
            Health = 1000, MaxHealth = 1000, Speed = 325, Radius = 35.7437,
        });
        var body = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion,
            Team.Order, start)
        {
            Name = "Minion_Basic", Created = true, Visible = true,
            Health = 1000, MaxHealth = 1000, Speed = 325, Radius = 52.2563,
        });
        var movement = new MovementSystem(world, navigation);
        var clearance = movement.BodyClearance(hero, body);
        TestAssert.Near(100, clearance, 1e-9, "speed-change fixture body clearance");
        var contact = movement.ContactClearance(hero, body);
        // Beside the middle of the straight walk, two units outside the full-width clearance.
        var length = Distance(start, goal);
        var along = new Position2((goal.X - start.X) / length, (goal.Z - start.Z) / length);
        var side = new Position2(-along.Z, along.X);
        body.Position = Shift(Shift(start, along, length / 2), side, clearance + 2);
        var obstacles = Obstacles(clearance, body);

        var original = movement.SetMoveOrder(hero.NetId, goal).ToArray();
        TestAssert.True(original.Length > 1, "the order routes");
        AssertRouteClear(navigation, original, obstacles, "pre-speed-change route");

        // Change the speed once the hero is a third of the way along the route.
        var changed = false;
        var previous = hero.Position;
        for (var tick = 0; tick < 100 && Distance(hero.Position, goal) > 3; tick++)
        {
            if (!changed && Distance(hero.Position, start) > length / 3)
            {
                hero.Speed = 350;
                changed = true;
            }
            movement.Tick(.1);
            // The solver steps in float32 (the client's x87 precision control), so a step is exact to about 1e-4 units.
            TestAssert.True(Distance(previous, hero.Position) <= 350 * .1 + 1e-3,
                $"speed-change tick {tick} relocated the hero");
            TestAssert.True(navigation.SegmentClearOfUnits(previous, hero.Position, Obstacles(contact, body)),
                $"speed-change native tick {tick} crossed a body's contact distance");
            AssertBodyClearance(hero, contact, new[] { body },
                $"speed-change native tick {tick}");
            previous = hero.Position;
        }
        TestAssert.True(changed, "the speed changed during the walk");
        TestAssert.True(Distance(hero.Position, goal) <= 3,
            "the speed-changed walk completes with its original order");
    }

    [Check("movement.hero-moving-physical-gap-single-attack")]
    public static void OneAttackOrderCrossesAMovingPhysicalGap()
    {
        foreach (var (team, reverse, angle, offset) in Cases())
        {
            var runtime = Start(team, "Yorick");
            var hero = Hero(runtime, team);
            var axis = new Position2(Math.Cos(angle), Math.Sin(angle));
            var travel = reverse ? new Position2(-axis.X, -axis.Z) : axis;
            var normal = new Position2(-travel.Z, travel.X);
            var center = new Position2(7300 + offset.X, 7000 + offset.Z);
            Place(hero, Shift(center, travel, -450));
            hero.AttackRange = 125;
            hero.AttackPeriod = .5;
            hero.AttackWindup = .1;

            var upper = Body(runtime, team, center);
            var lower = Body(runtime, team, center);
            var clearance = runtime.Movement.BodyClearance(hero, upper);
            // Batch 61: a flagged hero brings 0.2 of its radius to the pair test, so what keeps it out of a body is the thin hero disc
            // against the body's full disc (the contact distance adds a skin the walk may press into); the gap between the full-width
            // discs stays the fixture's opening.
            var physical = runtime.Movement.MoverPairRadius(hero, upper) + runtime.Movement.PairMovementRadius(upper, hero);
            upper.Position = Shift(center, normal, clearance + 4);
            lower.Position = Shift(center, normal, -clearance - 4);
            var target = Body(runtime, Opponent(team), Shift(center, travel, 500));
            target.Health = target.MaxHealth = 10000;
            var before = target.Health;
            runtime.Spells.HeroOrders!.StartAttack(hero, target);

            var crossed = false;
            var previous = hero.Position;
            for (var tick = 0; tick < 160 && target.Health >= before; tick++)
            {
                var movingCenter = Shift(center, travel, 12 * Math.Sin(tick * .25));
                upper.Position = Shift(movingCenter, normal, clearance + 4);
                lower.Position = Shift(movingCenter, normal, -clearance - 4);
                Step(runtime);
                TestAssert.True(Distance(previous, hero.Position) <= MaximumCollisionStep(runtime, hero),
                    $"{team} moving gap relocated the hero on tick {tick}");
                AssertBodyClearance(hero, physical, new[] { upper, lower },
                    $"{team} moving gap tick {tick}");
                previous = hero.Position;
                crossed |= Dot(hero.Position - center, travel) > 0;
            }
            TestAssert.True(crossed, $"{team} one attack order crosses the moving gap");
            TestAssert.True(target.Health < before, $"{team} one attack order reaches and hits its target");
        }
    }

    private static (Team Team, bool Reverse, double Angle, Position2 Offset)[] Cases() =>
    [
        (Team.Order, false, 0.0, new Position2(.37, .61)),
        (Team.Chaos, true, 0.0, new Position2(1.83, 1.27)),
        (Team.Order, true, Math.PI / 4, new Position2(.91, 1.73)),
        (Team.Chaos, false, Math.PI / 4, new Position2(1.41, .19)),
    ];

    private static MatchRuntime Start(Team actorTeam, string champion = "Annie")
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: champion, chaosChampion: champion, solo: false, readySettleSeconds: 0);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        foreach (var connection in new long[] { 1, 2 })
        {
            var participant = runtime.Session.Participants.Single(seat =>
                seat.PlayerId == (ulong)connection);
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck(participant.PlayerId)) is not null,
                $"team {participant.Team} authentication");
            runtime.Session.Spawn(connection);
            runtime.Session.MarkReady(connection);
        }
        TestAssert.True(runtime.Session.Start(), "duel start");
        var hero = Hero(runtime, actorTeam);
        hero.HeroAutoAcquire = false;
        Place(hero, hero.Position);
        return runtime;
    }

    private static NavigationGrid CreateOpenGrid()
    {
        const int count = 200;
        const float cellSize = 10;
        const int stride = 56;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)2);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(count * cellSize);
            writer.Write(0f);
            writer.Write(count * cellSize);
            writer.Write(cellSize);
            writer.Write((uint)count);
            writer.Write((uint)count);
            var cell = new byte[stride];
            for (var index = 0; index < count * count; index++) writer.Write(cell);
        }
        stream.Position = 0;
        return NavigationGrid.Load(stream, new NavigationGridSettings
        {
            CollisionRadius = 0,
            TurretPathStampRadius = 0,
            TurretClientStampRadius = 0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        });
    }

    private static Entity Hero(MatchRuntime runtime, Team team) =>
        runtime.Session.Participants.Single(seat => seat.Hero?.Team == team).Hero!;

    private static Entity Body(MatchRuntime runtime, Team team, Position2 position) =>
        runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, team, position)
        {
            Name = "Minion_Basic", Created = true, Visible = true,
            Health = 5000, MaxHealth = 5000, Radius = 35.744, Speed = 325,
        });

    private static NavigationObstacle[] Obstacles(double clearance, params Entity[] bodies) =>
        bodies.Select(body => new NavigationObstacle(body.Position, clearance)).ToArray();

    private static void Place(Entity hero, Position2 position)
    {
        hero.Position = position;
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
    }

    private static void AssertRouteClear(MatchRuntime runtime, IReadOnlyList<Position2> route,
        IReadOnlyList<NavigationObstacle> obstacles, string label)
        => AssertRouteClear(runtime.Navigation, route, obstacles, label);

    private static void AssertRouteClear(NavigationGrid navigation, IReadOnlyList<Position2> route,
        IReadOnlyList<NavigationObstacle> obstacles, string label)
    {
        TestAssert.True(route.Count > 1, $"{label}: route has a live segment");
        for (var index = 0; index < route.Count - 1; index++)
        {
            TestAssert.True(navigation.SegmentClearOfUnits(route[index], route[index + 1],
                    obstacles, escape: index == 0),
                $"{label}: segment {index} crosses a physical body");
            TestAssert.True(index == 0
                    ? navigation.HasMovementLineOfSight(route[index], route[index + 1])
                    : navigation.HasPathingLineOfSight(route[index], route[index + 1]),
                $"{label}: segment {index} crosses terrain");
        }
    }

    private static bool RouteClearOfBodies(NavigationGrid navigation,
        IReadOnlyList<Position2> route, IReadOnlyList<NavigationObstacle> obstacles)
    {
        if (route.Count < 2) return false;
        for (var index = 0; index < route.Count - 1; index++)
            if (!navigation.SegmentClearOfUnits(route[index], route[index + 1], obstacles,
                    escape: index == 0))
                return false;
        return true;
    }

    private static bool CrossesBand(IReadOnlyList<Position2> route, Position2 center,
        Position2 axis, double halfWidth)
    {
        var normal = new Position2(-axis.Z, axis.X);
        for (var index = 0; index < route.Count - 1; index++)
        {
            var first = route[index] - center;
            var second = route[index + 1] - center;
            var firstAlong = Dot(first, axis);
            var secondAlong = Dot(second, axis);
            if ((firstAlong < 0 && secondAlong < 0) || (firstAlong > 0 && secondAlong > 0))
                continue;
            var denominator = secondAlong - firstAlong;
            if (Math.Abs(denominator) <= 1e-9) continue;
            var fraction = -firstAlong / denominator;
            if (fraction < 0 || fraction > 1) continue;
            var crossing = new Position2(first.X + (second.X - first.X) * fraction,
                first.Z + (second.Z - first.Z) * fraction);
            if (Math.Abs(Dot(crossing, normal)) <= halfWidth) return true;
        }
        return false;
    }

    private static void AssertBodyClearance(Entity hero, double clearance,
        IEnumerable<Entity> bodies, string label)
    {
        foreach (var body in bodies)
            TestAssert.True(Distance(hero.Position, body.Position) >= clearance - 1e-6,
                $"{label}: hero entered {body.NetId}'s recovered body clearance");
    }

    private static void Step(MatchRuntime runtime)
    {
        runtime.Spells.HeroOrders!.Tick(.1);
        runtime.Movement.Tick(.1);
        runtime.Combat.CombatTick(.1, regenerate: false);
    }

    private static double MaximumCollisionStep(MatchRuntime runtime, Entity hero) =>
        hero.Speed * .1 * Math.Pow(Math.Sqrt(1 + runtime.Movement.Settings.Collision.SpeedDeadband), 3) + 3;

    private static Team Opponent(Team team) => team == Team.Order ? Team.Chaos : Team.Order;
    private static Position2 Shift(Position2 point, Position2 direction, double distance) =>
        new(point.X + direction.X * distance, point.Z + direction.Z * distance);
    private static double Dot(Position2 first, Position2 second) =>
        first.X * second.X + first.Z * second.Z;
    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Z - second.Z, 2));
}
