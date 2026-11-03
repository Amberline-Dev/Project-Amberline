using Amberline.Simulation.Combat;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>Map1 retail target timer and lane-return regressions.</summary>
public static class TargetLifecycleChecks
{
    [Check("minions.target-loss-past-lane-end-does-not-backtrack")]
    public static void TargetLossPastLaneEndDoesNotBacktrack()
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var retarget in new[] { false, true })
        {
            var label = $"{team}/retarget={retarget}";
            var world = new WorldState();
            var movement = new MovementSystem(world, grid);
            var packets = new RecordingPackets();
            var enemyTeam = MinionDefinitions.Opposing(team);
            var definition = MapDefinitions.Nexuses.Single(item => item.Team == enemyTeam);
            var nexus = world.Register(new Entity(definition.NetId, EntityKind.Nexus,
                UnitType.Structure, enemyTeam, definition.Position)
            {
                Name = definition.Name, Alive = true, Created = true,
                Radius = definition.Radius, Health = definition.MaxHealth, MaxHealth = definition.MaxHealth,
                PathfindingRadius = enemyTeam == Team.Order ? MapDefinitions.OrderNexusPathfindingRadius :
                    MapDefinitions.ChaosNexusPathfindingRadius,
            });
            // B12 #9b: 0x400106A9 at target loss, t=1524.496. Mirror the
            // captured base position relative to the opposing nexus.
            var captured = new Position2(11883, 12081);
            var start = team == Team.Order ? captured :
                nexus.Position - (captured - MapDefinitions.NexusPositions["HQ_T2"]);
            var spine = MapDefinitions.LanePaths["C"];
            var endIndex = team == Team.Order ? spine.Count - 3 : 2;
            var terminal = movement.ClientAnchor(spine[endIndex]);
            if (team == Team.Order)
                TestAssert.Equal(new Position2(10917, 10619), terminal, "captured Order mid terminal");
            var previous = movement.ClientAnchor(spine[endIndex + (team == Team.Order ? -1 : 1)]);
            var forward = terminal - previous;
            var length = Distance(terminal, previous);
            var step = new Position2(forward.X * 100 / length, forward.Z * 100 / length);
            var minion = world.Register(Minion(0x400106A9, retarget ? terminal - step - step : start,
                MinionAiState.Engage));
            minion.Team = team;
            minion.MinionLane = "C";
            minion.Radius = MinionDefinitions.BaseStats(MinionType.Melee, team).Radius;
            minion.LaneWaypoints = retarget ? [previous, terminal] : [terminal];
            minion.AnchorPath = [minion.Position];
            var old = world.Register(Enemy(11, minion.Position + step, UnitType.Hero));
            old.Team = enemyTeam;
            minion.AttackTarget = old.NetId;
            using var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
                new AlwaysVisibleVision());
            if (retarget)
            {
                var replacement = world.Register(Enemy(12, terminal + step, UnitType.Minion));
                replacement.Team = enemyTeam;
                old.Alive = false;
                ai.NotifyTargetLost(old.NetId);
                TestAssert.Equal(replacement.NetId, minion.AttackTarget ?? 0,
                    $"{label}: target loss before the endpoint retargets across it");
                TestAssert.True(minion.LaneWaypoints.SequenceEqual(new[] { previous, terminal }),
                    $"{label}: retarget preserves the original terminal suffix");
                minion.Position = start;
                minion.Waypoints.Clear();
                minion.Target = null;
                minion.AnchorPath = [start];
                replacement.Alive = false;
                packets.Events.Clear();
                ai.NotifyTargetLost(replacement.NetId);
            }
            else
            {
                old.Alive = false;
                ai.Tick(0);
            }
            TestAssert.True(minion.LaneDone && minion.LaneWaypoints.Count == 0 &&
                            minion.InterruptedLaneWaypoints.Count == 0,
                $"{label}: target loss retires the spent terminal before any connector");
            for (var tick = 0; tick < 60; tick++)
            {
                movement.Tick(1.0 / 30.0);
                ai.Tick(1.0 / 30.0);
                TestAssert.True(Distance(minion.Position, nexus.Position) >=
                                movement.BodyClearance(minion, nexus) - 1e-6,
                    $"{label}: siege movement retains the living nexus footprint");
            }
            var routes = packets.Events.Where(item => item.Waypoints is { Count: > 1 })
                .Select(item => item.Waypoints!).ToArray();
            TestAssert.True(routes.Length > 0, $"{label}: a real forward siege order was emitted");
            TestAssert.True(routes.All(route => Distance(route[^1], terminal) > grid.CellSize &&
                            Distance(route[^1], nexus.Position) < Distance(start, nexus.Position)),
                $"{label}: no order ends at the backward terminal; siege endpoints advance");
            var obstacle = new NavigationObstacle(nexus.Position, movement.BodyClearance(minion, nexus));
            foreach (var route in routes)
            foreach (var points in new[] { route, (IReadOnlyList<Position2>)Geometry.ClientWaypoints(route,
                         new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ)) })
                for (var index = 1; index < points.Count; index++)
                    TestAssert.True(grid.SegmentClearOfUnits(points[index - 1], points[index], [obstacle]),
                        $"{label}: exact and wire siege connectors clear the living structure");
            TestAssert.True(Distance(minion.Position, nexus.Position) < Distance(start, nexus.Position) - 100,
                $"{label}: authoritative movement makes forward siege progress within two seconds");
        }
    }

    [Check("minions.target-loss-terminal-progress-requires-saved-lane")]
    public static void TerminalProgressRequiresSavedLane()
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var control in new[] { "unfinished", "interrupted", "empty", "other-terminal" })
        {
            var world = new WorldState();
            var movement = new MovementSystem(world, grid);
            var spine = MapDefinitions.LanePaths["C"];
            var endIndex = team == Team.Order ? spine.Count - 3 : 2;
            var terminal = movement.ClientAnchor(spine[endIndex]);
            var previous = movement.ClientAnchor(spine[endIndex + (team == Team.Order ? -1 : 1)]);
            var forward = terminal - previous;
            var offset = new Position2(forward.X * .25, forward.Z * .25);
            var before = control is "unfinished" or "interrupted";
            var minion = world.Register(Minion(10, before ? terminal - offset : terminal + offset,
                MinionAiState.Engage));
            minion.Team = team;
            minion.MinionLane = "C";
            minion.AttackTarget = 99;
            if (control == "unfinished") minion.LaneWaypoints = [terminal];
            if (control == "interrupted") minion.InterruptedLaneWaypoints = [terminal];
            if (control == "other-terminal") minion.LaneWaypoints = [previous];
            // A chase ending at a lane vertex is not a saved strategic suffix.
            if (control == "empty") minion.Waypoints = [terminal];
            using var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
                new RecordingPackets(), new AlwaysVisibleVision());
            ai.NotifyTargetLost(99);
            TestAssert.False(minion.LaneDone, $"{team}/{control}: target loss cannot invent completion");
            if (!before) continue;
            ai.Tick(.25);
            TestAssert.True(!minion.LaneDone && minion.Waypoints.Count > 0 &&
                            Distance(minion.Waypoints[^1], terminal) <= 2,
                $"{team}/{control}: an unfinished or control-cancelled route resumes its terminal");
        }
    }

    [Check("minions.anti-kite-pursuit-timeout")]
    public static void PursuitTimeoutFindsTheAttackingWave()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var minion = world.Register(Minion(10, new(7000, 7000), MinionAiState.March));
        var hero = world.Register(Enemy(11, new(7400, 7000), UnitType.Hero));
        minion.Waypoints = [new(7600, 7000)];
        var ai = new MinionAiSystem(world, grid, new MovementSystem(world, grid),
            new FixtureCombat(), new RecordingPackets(), new AlwaysVisibleVision());
        // minionLib 0.0's OnInit arms TimerFindEnemies on DELAY_FIND_ENEMIES, and Minion.lua 0.2 is
        // a marching minion's only acquisition pass: the scan sits on that 0.25 s grid, not on the
        // single frame the old machine's lazy TargetScanReady allowed.
        for (var tick = 0; tick < 30 && minion.AttackTarget is null; tick++) ai.Tick(1.0 / 30.0);
        TestAssert.Equal(hero.NetId, minion.AttackTarget!.Value);
        var defender = world.Register(Enemy(12, new(7100, 7000), UnitType.Minion));
        defender.AttackTarget = minion.NetId;
        // Minion.lua 0.3's close arms TimerAntiKite at DELAY_ANTIKITE (4.0) from this acquisition,
        // and a chase outside TargetInCancelAttackRange never re-arms it (Minion.lua 0.2's cancel
        // arm), so 3.9 s and 0.2 s straddle the timeout rather than an absolute tick of the check.
        ai.Tick(3.9);
        TestAssert.Equal(hero.NetId, minion.AttackTarget!.Value);
        ai.Tick(.2);
        TestAssert.Equal(defender.NetId, minion.AttackTarget!.Value,
            "four seconds of pursuit reselects the nearby attacking wave");
        ai.Tick(8);
        TestAssert.Equal(defender.NetId, minion.AttackTarget!.Value,
            "an in-range target remains engaged");
    }

    [Check("minions.failed-path-fallback-terrain")]
    public static void FailedPathFallbackCannotCrossTerrain()
    {
        foreach (var blocked in new[] { false, true })
        {
            var grid = NavigationGrid.LoadDefault();
            var world = new WorldState();
            var movement = new MovementSystem(world, grid);
            var packets = new RecordingPackets();
            var minion = Minion(10, blocked ? new(6999, 6757) : new(7000, 7000), MinionAiState.Engage);
            var target = Enemy(11, blocked ? new(6913, 6435) : new(7400, 7000), UnitType.Hero);
            TestAssert.Equal(!blocked, grid.HasMovementLineOfSight(minion.Position, target.Position));
            minion.AttackTarget = target.NetId;
            world.Register(minion); world.Register(target);
            var calls = 0;
            grid.FindPathOverride = (_, _) => { calls++; return []; };
            var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets, new AlwaysVisibleVision());
            ai.Tick(.2);
            TestAssert.True(calls > 0, "the failed navigation branch was reached");
            foreach (var packet in packets.Events.Where(packet => packet.Waypoints is not null))
            {
                var route = packet.Waypoints!;
                for (var index = 1; index < route.Count; index++)
                    TestAssert.True(grid.HasMovementLineOfSight(route[index - 1], route[index]),
                        "a fallback packet cannot invent a path through terrain");
            }
            if (minion.Target is Position2 destination)
                TestAssert.True(grid.HasMovementLineOfSight(minion.Position, destination),
                    "a server-side fallback obeys the same terrain gate");
        }
    }

    [Check("minions.retail-target-lifecycle")]
    public static void TargetCadenceAndInvalidation()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var packets = new RecordingPackets();
        var minion = Minion(10, new Position2(7000.0, 7000.0), MinionAiState.Engage);
        var target = Enemy(11, new Position2(7100.0, 7000.0), UnitType.Hero);
        world.Register(minion);
        world.Register(target);
        minion.AttackTarget = target.NetId;
        var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());

        ai.Tick(0.0); // establishes the first 250 ms target-decision deadline
        target.Position = new Position2(9501.0, 7000.0);
        ai.Tick(0.24);
        TestAssert.Equal(MinionAiState.Engage, minion.MinionState,
            "distance-only target loss must wait for the 0.25 s decision deadline");
        TestAssert.Equal(target.NetId, minion.AttackTarget ?? 0,
            "target must remain attached before the retail timer fires");
        ai.Tick(0.01);
        TestAssert.Equal(MinionAiState.March, minion.MinionState,
            "a target beyond 2500 units must be dropped at the timer deadline");
        TestAssert.Equal<uint?>(null, minion.AttackTarget,
            "distance-invalid target must be cleared");

        // TimerFindEnemies has its own 250 ms phase.  Observe the actual
        // production decision seam so this verifies decisions rather than
        // inferring them from movement or packet side effects.
        var phaseCalls = new List<double>();
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        // minionLib 0.0 OnInit starts a minion in AI_IDLE, and Minion.lua 0.4's TimerMoveForward
        // arm for that state runs FindTargetOrMove — the immediate first scan measured here. A
        // minion already in AI_ATTACKMOVESTATE has had that scan: Minion.lua 0.4's
        // AI_ATTACKMOVESTATE arm is SetStateAndMoveToForwardNav and no scan, so seeding March
        // would measure TimerFindEnemies' phase alone and drop the birth scan entirely.
        minion = Minion(12, new Position2(7000.0, 7000.0), MinionAiState.Idle);
        world.Register(minion);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(), new AlwaysVisibleVision());
        ai.TargetDecisionObserver = entity =>
        {
            if (entity.NetId == minion.NetId) phaseCalls.Add(ai.ElapsedSeconds);
        };
        ai.Tick(0.0);
        TestAssert.Equal(1, phaseCalls.Count, "first target scan must be immediate");
        ai.Tick(0.24);
        TestAssert.Equal(1, phaseCalls.Count, "scan must not repeat before 0.25 seconds");
        ai.Tick(0.01);
        TestAssert.Equal(2, phaseCalls.Count, "scan must repeat at 0.25 seconds");
        TestAssert.Near(0.25, phaseCalls[1], 1e-9, "first repeated scan phase");

        phaseCalls.Clear();
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        minion = Minion(13, new Position2(7000.0, 7000.0), MinionAiState.Idle);
        world.Register(minion);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(), new AlwaysVisibleVision());
        ai.TargetDecisionObserver = entity =>
        {
            if (entity.NetId == minion.NetId) phaseCalls.Add(ai.ElapsedSeconds);
        };
        for (var index = 0; index < 120; index++) ai.Tick(1.0 / 30.0);
        // The birth scan plus one TimerFindEnemies fire per DELAY_FIND_ENEMIES boundary: 120 fixed
        // steps of 1/30 s walk the same phase the two sections above pin (immediate, then every
        // 0.25 s), so the samples are 0 and the sixteen boundaries the accumulator reaches before
        // it stops just under 4.0 s. Seventeen scans. The hand-written machine reported sixteen,
        // counting the sixteen 250 ms intervals rather than the scans that fill them — which is
        // also why the neighbouring sections' counts (immediate + one at 0.25 s; nine over 2.2 s)
        // never matched a sixteen here.
        TestAssert.Equal(17, phaseCalls.Count,
            $"fixed ticks must observe seventeen phase-aligned scans, got {phaseCalls.Count}");
        // The birth scan is observed on the tick it lands on; TimerFindEnemies is armed at 0
        // (minionLib 0.0 OnInit) and fires on its own DELAY_FIND_ENEMIES deadline grid from there,
        // so the deadlines are 0 for the birth scan and 0.25*k after it, each observed on the first
        // step that reaches it. Anchoring the grid on the birth scan's observation instead would
        // shift every timer sample by one step.
        for (var index = 0; index < phaseCalls.Count; index++)
        {
            var due = index * 0.25;
            TestAssert.True(phaseCalls[index] >= due - 1e-6 &&
                phaseCalls[index] <= due + 1.0 / 30.0 + 1e-6,
                $"fixed scan {index} drifted from phase {due:R}: {phaseCalls[index]:R}");
        }

        phaseCalls.Clear();
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        minion = Minion(14, new Position2(7000.0, 7000.0), MinionAiState.Idle);
        world.Register(minion);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(), new AlwaysVisibleVision());
        ai.TargetDecisionObserver = entity =>
        {
            if (entity.NetId == minion.NetId) phaseCalls.Add(ai.ElapsedSeconds);
        };
        var irregularPattern = new[] { 0.07, 0.04, 0.09, 0.02 };
        for (var repeat = 0; repeat < 10; repeat++)
            foreach (var dt in irregularPattern) ai.Tick(dt);
        TestAssert.Equal(9, phaseCalls.Count,
            $"irregular ticks must preserve nine phase-aligned scans, got {phaseCalls.Count}");
        // The same deadline grid as the fixed section above: the birth scan plus one scan per
        // 0.25*k deadline, observed on the first irregular step that reaches it (the pattern's
        // longest step, 0.09 s, bounds how late that observation can be).
        for (var index = 0; index < phaseCalls.Count; index++)
        {
            var due = index * 0.25;
            TestAssert.True(phaseCalls[index] >= due - 1e-6 &&
                phaseCalls[index] <= due + 0.09 + 1e-6,
                $"irregular scan {index} drifted from phase {due:R}: {phaseCalls[index]:R}");
        }

        // Event invalidation is immediate and does not wait for another scan.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        packets = new RecordingPackets();
        minion = Minion(20, new Position2(7000.0, 7000.0), MinionAiState.Engage);
        var old = Enemy(21, new Position2(7100.0, 7000.0), UnitType.Minion);
        var replacement = Enemy(22, new Position2(7120.0, 7000.0), UnitType.Minion);
        world.Register(minion);
        world.Register(old);
        world.Register(replacement);
        minion.AttackTarget = old.NetId;
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        ai.Tick(0.0);
        old.Alive = false;
        ai.Tick(0.01);
        TestAssert.Equal(replacement.NetId, minion.AttackTarget ?? 0,
            $"dead target invalidation must reacquire immediately actual={minion.AttackTarget?.ToString() ?? "null"}");
        TestAssert.Equal(MinionAiState.Engage, minion.MinionState,
            "immediate replacement must remain engaged");

        // Retention is target-relative: acquisition range is only for the
        // first notice, so a live target at 1500 units remains engaged.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        minion = Minion(30, new Position2(7000.0, 7000.0), MinionAiState.Engage);
        target = Enemy(31, new Position2(8500.0, 7000.0), UnitType.Hero);
        minion.AttackTarget = target.NetId;
        world.Register(minion); world.Register(target);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(), new AlwaysVisibleVision());
        ai.Tick(0.0);
        TestAssert.Equal(target.NetId, minion.AttackTarget ?? 0,
            "a live target beyond acquisition range must remain retained");

        // The departure point of the saved lane route is not a second leash.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        minion = Minion(40, new Position2(8000.0, 7000.0), MinionAiState.Engage);
        target = Enemy(41, new Position2(9000.0, 7000.0), UnitType.Minion);
        minion.AttackTarget = target.NetId;
        minion.LaneWaypoints = [new Position2(0.0, 0.0)];
        world.Register(minion); world.Register(target);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(), new AlwaysVisibleVision());
        ai.Tick(0.0);
        TestAssert.Equal(MinionAiState.Engage, minion.MinionState,
            "a live nearby target must not be discarded by a lane departure point");

        // An unchanged periodic validation is state-only and packet-silent.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        packets = new RecordingPackets();
        minion = Minion(50, new Position2(7000.0, 7000.0), MinionAiState.Engage);
        target = Enemy(51, new Position2(7100.0, 7000.0), UnitType.Minion);
        minion.AttackTarget = target.NetId;
        world.Register(minion); world.Register(target);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        ai.Tick(0.0);
        packets.Events.Clear();
        for (var index = 0; index < 30; index++) ai.Tick(1.0 / 30.0);
        TestAssert.Equal(0, packets.Events.Count,
            "unchanged periodic target decisions must not emit redundant routes");

        foreach (var (label, invalidate) in new (string, Action<WorldState, Entity>)[ ]
        {
            ("dead", (_, entity) => entity.Alive = false),
            ("missing", (currentWorld, entity) => currentWorld.Remove(entity.NetId)),
            ("friendly", (_, entity) => entity.Team = Team.Order),
            ("protected", (_, entity) => entity.Protected = true),
        })
        {
            world = new WorldState();
            movement = new MovementSystem(world, grid);
            minion = Minion(60, new Position2(7000.0, 7000.0), MinionAiState.Engage);
            old = Enemy(61, new Position2(7100.0, 7000.0), UnitType.Minion);
            replacement = Enemy(62, new Position2(7120.0, 7000.0), UnitType.Minion);
            minion.AttackTarget = old.NetId;
            world.Register(minion); world.Register(old); world.Register(replacement);
            ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
                new RecordingPackets(), new AlwaysVisibleVision());
            ai.Tick(0.0);
            invalidate(world, old);
            ai.Tick(0.01);
            TestAssert.Equal(replacement.NetId, minion.AttackTarget ?? 0,
                $"{label} target invalidation must reacquire immediately");
        }
    }

    [Check("minions.return-lane-blend")]
    public static void TargetLossCarriesLateralOffset()
    {
        var grid = NavigationGrid.LoadDefault();
        grid.FindPathOverride = static (start, goal) => new[] { start, goal };
        var lane = new[] {
            new Position2(6983.0, 7229.0),
            new Position2(7655.0, 7755.0),
            new Position2(8167.0, 8223.0),
        };
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var packets = new RecordingPackets();
        var target = Enemy(99, new Position2(6500.0, 6900.0), UnitType.Hero);
        world.Register(target);
        var survivors = new[] {
            Minion(31, new Position2(6523.0, 7003.0), MinionAiState.Engage),
            Minion(32, new Position2(6557.0, 6905.0), MinionAiState.Engage),
            Minion(33, new Position2(6909.0, 6995.0), MinionAiState.Engage),
        };
        foreach (var survivor in survivors) {
            survivor.AttackTarget = target.NetId;
            survivor.LaneWaypoints = lane.ToList();
            // Engaged survivors have attacked the target that died, so their clients are in the attack state.
            survivor.ClientAttackArmed = true;
            world.Register(survivor);
        }
        var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        target.Alive = false;
        foreach (var survivor in survivors) ai.Tick(0.01);

        // Retail Minion.luaobj: OnTargetLost calls FindTargetOrMove at once and
        // SetStateAndMoveToForwardNav takes no allied arrival into account. The K6
        // stagger (playtest B3 #5, 26 Sept 2026) is removed (owner decision, 16 Oct
        // 2026), so survivors 31 and 32, which reach lane[1] only 27 units apart in
        // route length, rejoin in the same tick as 33, with nothing held back.
        foreach (var survivor in survivors)
            TestAssert.True(survivor.Waypoints.Count > 0 && survivor.MinionState == MinionAiState.March &&
                            survivor.InterruptedLaneWaypoints.Count == 0,
                $"survivor {survivor.NetId} must rejoin at the target-loss edge with no parked lane suffix");

        foreach (var survivor in survivors) {
            var own = packets.Events.Where(item => item.NetId == survivor.NetId).ToArray();
            TestAssert.Equal(2, own.Length,
                $"target loss must send stop-attack then one rejoin route for {survivor.NetId}");
            TestAssert.Equal("stop-attack", own[0].Kind,
                $"target loss stop ordering for {survivor.NetId}");
            TestAssert.Equal("waypoints", own[1].Kind,
                $"target loss waypoint ordering for {survivor.NetId}");
            TestAssert.True(own[1].Waypoints!.Count >= 3,
                $"rejoin route must preserve its merge and forward tail for {survivor.NetId}");
            TestAssert.Equal(lane[1], own[1].Waypoints![^2],
                $"rejoin route must preserve lane[1] for {survivor.NetId}");
            TestAssert.Equal(lane[2], own[1].Waypoints![^1],
                $"rejoin route must preserve the lane suffix for {survivor.NetId}");
        }
        var joins = survivors.Select(survivor => packets.Events
            .Single(item => item.NetId == survivor.NetId && item.Kind == "waypoints")
            .Waypoints![^3]).ToArray();
        TestAssert.Equal(3, joins.Distinct().Count(),
            "spread survivors must carry distinct lateral offsets into their rejoin");

        // The fight-position leg is routed independently of the saved lane
        // suffix and its bend must survive in the transmitted route.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        var calls = new List<(Position2 Start, Position2 Goal)>();
        grid.FindPathOverride = (start, goal) => {
            calls.Add((start, goal));
            return new[] { start, new Position2(7025.0, 7081.0), goal };
        };
        packets = new RecordingPackets();
        target = Enemy(100, new Position2(7000.0, 7000.0), UnitType.Hero);
        var routed = Minion(101, new Position2(7001.0, 7051.0), MinionAiState.Engage);
        routed.AttackTarget = target.NetId;
        var routedLane = new[] { new Position2(7051.0, 7001.0),
            new Position2(7151.0, 7001.0), new Position2(7251.0, 7001.0) };
        routed.LaneWaypoints = routedLane.ToList();
        world.Register(target); world.Register(routed);
        ai = new MinionAiSystem(world, grid, new MovementSystem(world, grid),
            new FixtureCombat(), packets, new AlwaysVisibleVision());
        target.Alive = false;
        ai.Tick(0.01);
        TestAssert.True(calls.Count >= 2 && calls[1].Start == calls[0].Goal &&
            calls[1].Goal == routedLane[1],
            $"rejoin must route the personal lead then merge at lane[1], calls={calls.Count}");
        var routedPacket = packets.Events.Single(item => item.Kind == "waypoints").Waypoints!;
        TestAssert.True(routedPacket.Contains(new Position2(7025.0, 7081.0)),
            "rejoin must preserve the navgrid bend");
        TestAssert.Equal(routedLane[1], routedPacket[^2]);
        TestAssert.Equal(routedLane[2], routedPacket[^1]);

        // If the minion is level with the first saved vertex, the carried
        // offset collapses to its current position.  The merge route must
        // omit that zero-length duplicate before it reaches the client.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        grid.FindPathOverride = null;
        packets = new RecordingPackets();
        target = Enemy(105, new Position2(7000.0, 7000.0), UnitType.Hero);
        var level = Minion(106, new Position2(7051.0, 7051.0), MinionAiState.Engage);
        level.AttackTarget = target.NetId;
        var levelLane = new[] { new Position2(7051.0, 7001.0),
            new Position2(7151.0, 7001.0), new Position2(7251.0, 7001.0) };
        level.LaneWaypoints = levelLane.ToList();
        world.Register(target); world.Register(level);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        target.Alive = false;
        ai.Tick(0.01);
        var levelRoute = packets.Events.Single(item => item.Kind == "waypoints").Waypoints!;
        TestAssert.True(levelRoute.Zip(levelRoute.Skip(1)).All(pair => pair.First != pair.Second),
            "a level rejoin must not encode an adjacent duplicate waypoint");
        TestAssert.Equal(levelLane[1], levelRoute[^2]);
        TestAssert.Equal(levelLane[2], levelRoute[^1]);

        // A failed full-offset lead may fall back to the half-offset candidate
        // before the shared centerline.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        calls = new List<(Position2 Start, Position2 Goal)>();
        var pathAttempt = 0;
        grid.FindPathOverride = (start, goal) => {
            calls.Add((start, goal));
            pathAttempt++;
            return pathAttempt == 1 ? Array.Empty<Position2>() : new[] { start, goal };
        };
        packets = new RecordingPackets();
        target = Enemy(110, new Position2(7000.0, 7000.0), UnitType.Hero);
        var half = Minion(111, new Position2(7001.0, 7051.0), MinionAiState.Engage);
        half.AttackTarget = target.NetId;
        var halfLane = new[] { new Position2(7051.0, 7001.0),
            new Position2(7151.0, 7001.0), new Position2(7251.0, 7001.0) };
        half.LaneWaypoints = halfLane.ToList();
        world.Register(target); world.Register(half);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        target.Alive = false;
        ai.Tick(0.01);
        TestAssert.True(calls.Count >= 3,
            "rejoin must try the full offset before the half-offset route");
        TestAssert.True(packets.Events.Any(item => item.Kind == "waypoints"),
            "a successful half-offset rejoin must emit a route");

        // When every A* leg fails, restoring the exact saved suffix is safer
        // than dropping the minion's forward navigation.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        calls = new List<(Position2 Start, Position2 Goal)>();
        grid.FindPathOverride = (start, goal) => { calls.Add((start, goal)); return Array.Empty<Position2>(); };
        packets = new RecordingPackets();
        target = Enemy(120, new Position2(7000.0, 7000.0), UnitType.Hero);
        var failed = Minion(121, new Position2(7001.0, 7051.0), MinionAiState.Engage);
        failed.AttackTarget = target.NetId;
        var failedLane = new[] { new Position2(7051.0, 7001.0),
            new Position2(7151.0, 7001.0), new Position2(7251.0, 7001.0) };
        failed.LaneWaypoints = failedLane.ToList();
        world.Register(target); world.Register(failed);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        target.Alive = false;
        ai.Tick(0.01);
        var failedRoute = packets.Events.Single(item => item.Kind == "waypoints").Waypoints!;
        TestAssert.True(calls.Count >= 3 && failedRoute[^3] == failedLane[0] &&
            failedRoute[^2] == failedLane[1] && failedRoute[^1] == failedLane[2],
            "total route failure must restore the original lane suffix");

        // A stale route head that quantizes to the current client position is
        // removed before the merge leg, so no adjacent duplicate is encoded.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        grid.FindPathOverride = static (start, goal) => new[] { start, goal };
        packets = new RecordingPackets();
        target = Enemy(130, new Position2(7000.0, 7000.0), UnitType.Hero);
        var stale = Minion(131, new Position2(7001.0, 7049.1), MinionAiState.Engage);
        stale.AttackTarget = target.NetId;
        stale.LaneWaypoints = [new Position2(7001.0, 7051.0),
            new Position2(7101.0, 7051.0), new Position2(7201.0, 7051.0)];
        world.Register(target); world.Register(stale);
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets,
            new AlwaysVisibleVision());
        target.Alive = false;
        ai.Tick(0.01);
        var staleRoute = packets.Events.Single(item => item.Kind == "waypoints").Waypoints!;
        TestAssert.True(staleRoute.Zip(staleRoute.Skip(1)).All(pair => pair.First != pair.Second),
            "client-identical saved heads must not create adjacent duplicate waypoints");
    }

    private static Entity Minion(uint id, Position2 position, MinionAiState state) =>
        new(id, EntityKind.Minion, UnitType.Minion, Team.Order, position) {
            MinionType = MinionType.Melee, Alive = true, Created = true,
            Speed = 325.0, Radius = 35.744, AttackRange = 110.0,
            AcquisitionRange = 450.0, MinionState = state,
            Health = 1000.0, MaxHealth = 1000.0,
        };

    private static Entity Enemy(uint id, Position2 position, UnitType type) =>
        new(id, type == UnitType.Hero ? EntityKind.Hero : EntityKind.Minion,
            type, Team.Chaos, position) {
            Alive = true, Created = true, Radius = type == UnitType.Hero ? 65.0 : 35.744,
            Health = 1000.0, MaxHealth = 1000.0,
        };

    private sealed class AlwaysVisibleVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class FixtureCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Protected;
        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class RecordingPackets : IMinionPacketSink
    {
        public List<PacketEvent> Events { get; } = [];
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position) { position = default; return false; }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) =>
            Events.Add(new PacketEvent(minion.NetId, "waypoints", waypoints.ToArray()));
        public void SendStop(Entity minion, long syncId, string reason) =>
            Events.Add(new PacketEvent(minion.NetId, "waypoints", [minion.Position]));
        public void SendStopAttack(Entity minion, string reason)
        {
            if (CombatPackets.StopAttack(minion) is not null) Events.Add(new PacketEvent(minion.NetId, "stop-attack", null));
        }
    }

    private sealed record PacketEvent(uint NetId, string Kind, IReadOnlyList<Position2>? Waypoints);
    private static double Distance(Position2 a, Position2 b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
