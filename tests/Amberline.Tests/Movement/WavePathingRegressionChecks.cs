using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Focused production-graph checks for crowd routing and lethal target edges.
/// These deliberately use the same movement, hero-order, minion-AI and
/// CombatService instances as a live match so a fixture cannot hide a wiring
/// omission at the composition boundary.
/// </summary>
public static class WavePathingRegressionChecks
{
    [Check("minions.enemy-collision-retargets-blocker")]
    public static void EnemyCollisionRetargetsBlocker()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in new[] { UnitType.Minion, UnitType.Hero })
            AssertCollisionTarget(team, type, "enemy");
    }

    [Check("minions.collision-retarget-controls")]
    public static void CollisionRetargetControls()
    {
        foreach (var control in new[] { "allied", "neutral", "neutral-attacker", "fear", "taunt", "halted",
                     "late-fear", "untargetable", "hidden", "ghosted", "soft-avoidance" })
            AssertCollisionTarget(Team.Order, UnitType.Minion, control);
    }

    private static void AssertCollisionTarget(Team team, UnitType blockerType, string control)
    {
        using var runtime = StartSolo();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var neutral = control is "neutral" or "neutral-attacker";
        var attacksBlocker = control is "enemy" or "neutral-attacker";
        var minion = runtime.Waves.SpawnFixture(team, MinionType.Melee, 0, "C", 0.0, 0.0);
        minion.Position = new Position2(7000, 7000);
        minion.Waypoints.Clear();
        minion.LaneWaypoints = [new Position2(7600, 7000)];
        var enemyTeam = MinionDefinitions.Opposing(team);
        var old = AddUnit(runtime.World, 0x4F00_0100, EntityKind.Minion,
            UnitType.Minion, enemyTeam, new Position2(7400, 7000), 35.744);
        minion.AttackTarget = old.NetId;
        minion.MinionState = MinionAiState.Engage;
        runtime.Minions!.Tick(0.0); // arm the ordinary scan before introducing contact
        var blocker = AddUnit(runtime.World, 0x4F00_0101,
            neutral ? EntityKind.Monster : blockerType == UnitType.Hero ? EntityKind.Hero : EntityKind.Minion,
            neutral ? UnitType.Monster : blockerType,
            control == "allied" ? team : neutral ? Team.Neutral : enemyTeam,
            new Position2(control == "soft-avoidance" ? 7130 : 7100, 7000),
            blockerType == UnitType.Hero ? 65.0 : 35.744);
        blocker.Speed = 325.0; // idle mobile body, without autonomous lane AI
        if (control == "neutral-attacker") blocker.AttackTarget = minion.NetId;
        if (control == "untargetable")
            blocker.Buffs.Add(new BuffState("Untargetable", new BuffPayload { Untargetable = true }));
        if (control == "hidden")
            blocker.Buffs.Add(new BuffState("Hidden", new BuffPayload { Stealthed = true }));
        if (control == "ghosted")
            blocker.Buffs.Add(new BuffState("Ghost", new BuffPayload { Ghosted = true }));
        if (control is "fear" or "taunt" or "halted")
            minion.Buffs.Add(new BuffState("CollisionControl", type: control == "fear"
                ? BuffType.Fear : control == "taunt" ? BuffType.Taunt : BuffType.Stun));
        minion.Waypoints = [new Position2(7300, 7000)];
        minion.AnchorPath = [minion.Position, .. minion.Waypoints];
        var contacted = false;
        runtime.Movement.FixedStepCompleted += (_, steps) => contacted = steps.Any(step =>
            step.NetId == minion.NetId && step.Colliders.Contains(blocker));
        var acquisitions = 0;
        var held = minion.AttackTarget;
        runtime.Minions.AcquireDecisionObserver = (actor, target, _) =>
        {
            if (actor != minion) return;
            // minionLib 0.11's OnCollisionOther reruns FindTargetInAcR and closes on whatever it
            // returns, so every contact re-decides. Acquire re-plans nothing when the choice is the
            // target already being approached (MinionAiEngagementResync.Acquire's same-target
            // return), so a decision is counted here only when it replaces the held target.
            if (target.NetId != held) acquisitions++;
            held = target.NetId;
        };
        runtime.Movement.Tick(MovementSystem.FixedTickSeconds);
        TestAssert.Equal(control is not ("ghosted" or "soft-avoidance" or "halted"), contacted,
            $"{team}/{blockerType}/{control}: actual body admission, without diagnostic logging");
        TestAssert.Equal((uint?)old.NetId, minion.AttackTarget,
            "movement only queues the event; it cannot change a committed step's order");
        if (control == "late-fear")
            minion.Buffs.Add(new BuffState("CollisionControl", type: BuffType.Fear));
        runtime.Minions.Tick(0.0);
        // OnCollisionOther reruns selection; re-selecting the target already
        // being approached re-plans nothing, so only a changed target acquires.
        TestAssert.Equal(attacksBlocker ? 1 : 0, acquisitions,
            $"{control}: a contact that changes the target runs one acquire; others and rejected controls run none");
        TestAssert.Equal((uint?)(attacksBlocker ? blocker.NetId : old.NetId), minion.AttackTarget,
            $"{team}/{blockerType}/{control}: collision decision precedes the next 250 ms scan");
        if (control == "enemy")
        {
            TestAssert.Equal(MinionAiState.Engage, minion.MinionState);
            TestAssert.True(CombatService.InRange(minion, blocker),
                "the retained blocker can be attacked immediately instead of waiting for stuck escape");
            TestAssert.True(minion.StuckElapsed < 0.3, "no multi-second recovery wait");
        }
        var consumed = acquisitions;
        minion.Buffs.Clear();
        runtime.Minions.Tick(0.0);
        TestAssert.Equal(consumed, acquisitions, "a contact is consumed once, including controlled contacts");
        TestAssert.Equal((uint?)(attacksBlocker ? blocker.NetId : old.NetId), minion.AttackTarget,
            "control release does not resurrect a rejected contact");
        if (control == "enemy")
        {
            var before = blocker.Health;
            for (var tick = 0; tick < 10 && blocker.Health == before; tick++) runtime.Tick(0.1);
            TestAssert.True(blocker.Health < before, "production combat hits the blocker within one second");
        }
    }

    [Check("minions.lane-return-never-holds-for-allied-arrival")]
    public static void LaneReturnNeverHoldsForAlliedArrival()
    {
        // Retail Minion.luaobj has no merge stop: FindTargetOrMove (proto 0.3) calls
        // SetStateAndMoveToForwardNav at once and OnTargetLost (minionLib 0.1) re-enters
        // it, whichever allied minion is also routed to the shared lane vertex. The
        // removed non-retail K6 yield held the later arrival; no arrival order may now.
        foreach (var arrival in new[] { "earlier", "later", "equal-lower-id", "equal-higher-id" })
        foreach (var speed in new[] { 325.0, 650.0 })
        {
            using var runtime = StartSolo();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var minion = AddUnit(runtime.World, 0x4F00_0201, EntityKind.Minion,
                UnitType.Minion, Team.Order, runtime.Movement.ClientAnchor(new Position2(7300, 7100)), 35.744);
            minion.MinionType = MinionType.Melee;
            minion.Speed = speed;
            var merge = runtime.Movement.ClientAnchor(new Position2(7300, 7300));
            const uint lostTarget = 0x4F00_0203;
            var stops = 0;
            runtime.Minions!.ClearMovementObserver = (_, _, _, _) => stops++;
            void ReturnToMerge()
            {
                minion.Waypoints.Clear();
                minion.LaneWaypoints = [merge];
                minion.MinionState = MinionAiState.Engage;
                minion.AttackTarget = lostTarget;
                runtime.Minions!.NotifyTargetLost(lostTarget);
            }
            ReturnToMerge();
            TestAssert.True(minion.Waypoints.Count > 0, "uncontested connector is valid");
            var ownDistance = new[] { minion.Position }.Concat(minion.Waypoints)
                .Zip(minion.Waypoints, Distance).Sum();
            var offset = arrival == "earlier" ? 25.0 : arrival == "later" ? -25.0 : 0.0;
            var otherDistance = ownDistance * 325.0 / speed + offset;
            var other = AddUnit(runtime.World,
                arrival == "equal-higher-id" ? 0x4F00_0200u : 0x4F00_0202u,
                EntityKind.Minion, UnitType.Minion, Team.Order,
                new Position2(merge.X - otherDistance, merge.Z), 35.744);
            other.MinionType = MinionType.Melee;
            other.Speed = 325.0;
            other.Waypoints = [merge];
            ReturnToMerge();
            TestAssert.True(minion.Waypoints.Count > 0,
                $"{arrival}/{speed}: the lane return starts at once, whoever reaches the merge first");
            TestAssert.Equal(MinionAiState.March, minion.MinionState,
                $"{arrival}/{speed}: back on forward navigation");
            TestAssert.Equal(0, minion.InterruptedLaneWaypoints.Count,
                $"{arrival}/{speed}: no private lane suffix is parked for a merge gap");
            TestAssert.Equal(0, stops, $"{arrival}/{speed}: no stop is sent for merging");

            // The next target scans (0.25 s) keep the route; nothing yields at the merge.
            for (var tick = 0; tick < 20; tick++)
            {
                runtime.Minions.Tick(0.1);
                TestAssert.True(minion.Waypoints.Count > 0 && minion.InterruptedLaneWaypoints.Count == 0,
                    $"{arrival}/{speed} tick {tick}: the scan parked the route for a merge");
            }
            TestAssert.Equal(0, stops, $"{arrival}/{speed}: the scans send no merge stop");
        }
    }

    [Check("movement.production-friendly-click-never-overlaps")]
    public static void FriendlyBodyClickNeverOverlapsAfterRealTicks()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.OwnerNetId = 0;
        hero.HeroAutoAcquire = false;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.Waypoints.Clear();
        hero.Target = null;

        var body = AddUnit(runtime.World, 0x4F00_0001, EntityKind.Minion,
            UnitType.Minion, Team.Order, new Position2(7350.0, 7000.0), 35.744);
        body.Speed = 325.0; // mobile to the hero collision query, idle in AI
        body.MinionType = null; // not a scheduled minion

        var route = runtime.Movement.SetMoveOrder(hero.NetId, body.Position);
        TestAssert.True(route.Count > 1, "a body click must retain a legal route");
        // The production fixture opts into the movement-only NPC shell.
        // Keep the gameplay radius untouched while measuring collision here.
        // Batch 61: a flagged hero brings 0.2 of its radius to the pair test, so what a hero may never do is overlap
        // the body's disc with its thin disc; the full-width BodyClearance is not the solver's stop distance for a hero.
        var minimum = runtime.Movement.MoverPairRadius(hero, body) + runtime.Movement.PairMovementRadius(body, hero);
        for (var tick = 0; tick < 40; tick++)
        {
            runtime.Tick(0.1);
            TestAssert.True(Distance(hero.Position, body.Position) >= minimum - 1e-6,
                $"hero overlapped friendly body on tick {tick}: " +
                $"distance={Distance(hero.Position, body.Position):R}, minimum={minimum:R}");
        }
    }

    [Check("movement.production-friendly-gap-keeps-short-route")]
    public static void NavigableFriendlyGapKeepsShortClearRoute()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.OwnerNetId = 0;
        hero.HeroAutoAcquire = false;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.Waypoints.Clear();
        hero.Target = null;

        var start = hero.Position;
        var goal = new Position2(7600.0, 7000.0);
        // The direct line intersects the upper body's full-width shell. The two bodies leave a narrow, physically-clear centre
        // gap (about 17 units between the shells), while the legacy whole-cell padding closes it. Batch 61: the hero's route is the
        // stock client planner's, which tests the units at their registered radius on sampled cells and does not inflate for the hero
        // (and the dynamic unit planner behind it is gone for heroes), so the straight walk through the gap is the route; the contact
        // solver keeps the thin hero disc out of the bodies.
        var upper = AddUnit(runtime.World, 0x4F00_0002, EntityKind.Minion,
            UnitType.Minion, Team.Order, new Position2(7300.0, 7080.0), 35.744);
        var lower = AddUnit(runtime.World, 0x4F00_0003, EntityKind.Minion,
            UnitType.Minion, Team.Order, new Position2(7300.0, 6860.0), 35.744);
        upper.Speed = lower.Speed = 325.0;
        upper.MinionType = lower.MinionType = null;

        runtime.Navigation.ResetPathQueryCounters();
        var route = runtime.Movement.SetMoveOrder(hero.NetId, goal);
        TestAssert.True(route.Count > 1, "a navigable gap must produce a route");
        TestAssert.Equal(0L, runtime.Navigation.FindPathAroundUnitsCalls,
            "the hero planner no longer calls the dynamic unit planner (batch 61)");
        var direct = Distance(start, goal);
        var length = route.Zip(route.Skip(1), (first, second) => Distance(first, second)).Sum();
        TestAssert.True(length <= direct * 1.35,
            $"hero took a detour through a navigable gap: route={length:R}, direct={direct:R}");

        var obstacles = new[]
        {
            new NavigationObstacle(upper.Position, runtime.Movement.ContactClearance(hero, upper)),
            new NavigationObstacle(lower.Position, runtime.Movement.ContactClearance(hero, lower)),
        };
        for (var index = 0; index < route.Count - 1; index++)
            TestAssert.True(runtime.Navigation.SegmentClearOfUnits(route[index], route[index + 1], obstacles,
                    escape: index == 0),
                $"gap route segment {index} entered a friendly body's contact distance");
    }

    [Check("movement.production-attack-behind-wave-eventually-hits")]
    public static void ExplicitAttackBehindFriendlyWallEventuallyHits()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.OwnerNetId = 0;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackRange = 625.0;
        hero.AttackDamage = 50.0;
        hero.AttackPeriod = 0.5;
        hero.AttackWindup = 0.1;
        hero.MissileSpeed = 10000.0;

        // The radial firing point is occupied by a friendly wall. The wall
        // has physical gaps at its ends, so a legal attack angle exists.
        for (var index = -1; index <= 1; index++)
        {
            var wall = AddUnit(runtime.World, 0x4F00_0010u + (uint)(index + 1),
                EntityKind.Minion, UnitType.Minion, Team.Order,
                new Position2(7140.0, 7000.0 + index * 80.0), 35.744);
            wall.Speed = 325.0;
            wall.MinionType = null;
        }

        var target = AddUnit(runtime.World, 0x4F00_0020, EntityKind.Minion,
            UnitType.Minion, Team.Chaos, new Position2(7800.0, 7000.0), 35.744);
        target.MinionType = null;
        var before = target.Health;
        runtime.Spells.HeroOrders!.StartAttack(hero, target);

        for (var tick = 0; tick < 240 && target.Health >= before; tick++)
            runtime.Tick(0.1);

        TestAssert.True(target.Health < before,
            "explicit attack remained stuck behind a friendly wave");
        TestAssert.True(runtime.Combat.CanAttack(hero, target),
            "attack should finish at a legal combat endpoint");
    }

    [Check("minions.production-target-loss-returns-to-lane")]
    public static void RealCombatKillReturnsEngagedMinionToLane()
    {
        var runtime = StartSolo();
        var minions = runtime.Waves ?? throw new InvalidOperationException(
            "production match did not compose minion waves");
        var attacker = minions.SpawnFixture(Team.Order, MinionType.Melee, 0,
            "C", now: 0.0, scheduledElapsed: 0.0);
        var dummy = minions.SpawnFixture(Team.Chaos, MinionType.Melee, 1,
            "C", now: 0.0, scheduledElapsed: 0.0);
        var lane = attacker.Waypoints.Take(3).Prepend(attacker.Position).ToList();
        TestAssert.True(lane.Count >= 2, "fixture minion must have a lane route");
        attacker.LaneWaypoints = lane;
        attacker.Waypoints.Clear();
        attacker.Target = null;
        attacker.MinionState = MinionAiState.Engage;
        attacker.AttackTarget = dummy.NetId;
        var before = attacker.Position;

        TestAssert.True(runtime.Combat.Kill(dummy), "real Combat.Kill must kill the dummy");
        TestAssert.False(dummy.Alive, "dummy must be dead before the AI edge is checked");
        TestAssert.True(attacker.AttackTarget is null,
            "target-loss callback must clear the minion target immediately");
        TestAssert.Equal(MinionAiState.March, attacker.MinionState,
            "target-loss callback must leave the minion marching");
        TestAssert.True(attacker.Waypoints.Count > 0,
            "target-loss callback must restore the saved lane route");

        runtime.Tick(0.1);
        TestAssert.True(Distance(before, attacker.Position) > 0.0,
            "a minion whose dummy died must resume lane movement on the next tick");
    }

    [Check("minions.production-merged-wave-return-direct")]
    public static void MergedWaveReturnIsDirect() =>
        // Playtest B3 #5, Blitzcrank game 26 Sept 2026, HUD 6:08.17: ten Order
        // top-lane minions of two merged waves lost their last target on one
        // tick and every rejoin route met at one merge corner. The K6 stagger that
        // answered it is gone (owner decision, 16 Oct 2026): retail returns every one
        // of them to forward navigation at once and leaves any pile-up at the corner
        // to the stock client's collision solver, so no pair-gap floor is asserted.
        AssertDirectLaneReturn("L", new Position2(1561.82, 12238.82),
            new Position2(2315.0, 12879.0), ticks: 100, pairFloor: false,
        [
            (MinionType.Caster, new Position2(1242.0, 11382.0)), // 11a
            (MinionType.Caster, new Position2(1098.0, 11440.0)), // 120
            (MinionType.Caster, new Position2(1443.0, 11380.0)), // 126
            (MinionType.Melee, new Position2(1262.0, 11775.0)), // 12c
            (MinionType.Melee, new Position2(1258.0, 11933.0)), // 132
            (MinionType.Melee, new Position2(1310.0, 11836.0)), // 138
            (MinionType.Caster, new Position2(898.0, 11608.0)), // 13e
            (MinionType.Caster, new Position2(1393.0, 11400.0)), // 144
            (MinionType.Caster, new Position2(1570.0, 11386.0)), // 14a
            (MinionType.Siege, new Position2(1049.0, 11810.0)), // 150
        ]);

    [Check("minions.production-mid-merge-return-direct")]
    public static void MidLaneMergeReturnIsDirect()
    {
        // Playtest B3 #5(b), Lee Sin game 26 Sept 2026: two Order casters lost
        // their target on one tick at HUD 2:37.07 and rejoined through the mid
        // merge vertex 57 route units apart. They closed to 3 units by HUD
        // 2:39.9, when /win ended the match and froze every unit in place.
        // retail sweep B4 open question, 27 Sept 2026: shipped __NAV_C markers.
        // Translate the captured pair with C09; preserve its relative spacing.
        // Both return at once; with the present connector geometry the pair also
        // keeps its half-clearance floor, which this captured case still pins.
        var head = new Position2(6934.052490234375, 7260.125);
        var offset = head - new Position2(6981.74, 7229.17);
        AssertDirectLaneReturn("C", head,
            new Position2(7672.990478515625, 7801.9921875), ticks: 45, pairFloor: true,
        [
            (MinionType.Caster, new Position2(6943.2, 7118.7) + offset), // 0x40010013
            (MinionType.Caster, new Position2(6846.2, 7147.4) + offset), // 0x40010019
        ]);
    }

    [Check("minions.production-occupied-mid-terminal-makes-progress")]
    public static void OccupiedMidTerminalMakesProgressWithoutAnotherWave()
    {
        var runtime = StartSolo();
        var waves = runtime.Waves ?? throw new InvalidOperationException(
            "production match did not compose minion waves");
        waves.AutomaticSpawningEnabled = false;
        // The wave has pushed through mid; retain the nexus objectives ahead.
        foreach (var structure in runtime.World.Entities.Values.Where(entity =>
                     entity.Team == Team.Chaos &&
                     (entity.Kind == EntityKind.Inhibitor ||
                      entity.UnitType == UnitType.Turret &&
                      MapDefinitions.TurretTiers.TryGetValue(entity.Name, out var tier) &&
                      tier is not (TurretTier.Nexus or TurretTier.Fountain))).ToArray())
            runtime.Combat.Kill(structure);
        runtime.Combat.EngageProtection();

        // retail sweep B4 open question, 27 Sept 2026: shipped __NAV_C markers.
        // Rebase the captured parked formation and connector onto C016.
        var terminal = runtime.Movement.ClientAnchor(new Position2(10917.044921875, 10620.0625));
        var offset = terminal - new Position2(10687, 10709);
        TestAssert.True(Distance(waves.LaneRoute(Team.Order, "C")[^1], terminal) <= 2.0,
            "shipped C016 is the production mid-lane terminal");
        // Playtest B4 #10, 27 Sept 2026: retained backtracking connectors,
        // parked wire anchors, and no enemy wave to break the circular wait.
        (uint Id, MinionType Type, Position2 Position)[] placements =
        [
            (0x40010571, MinionType.Caster, new(10681.55, 10679.12)),
            (0x4001057d, MinionType.Melee, new(10756.54, 10728.62)),
            (0x40010589, MinionType.Melee, new(10901.74, 10689.92)),
        ];
        placements = placements.Select(placement =>
            (placement.Id, placement.Type, placement.Position + offset)).ToArray();
        var minions = placements.Select(placement =>
        {
            var stats = Amberline.Simulation.Minions.MinionDefinitions.Stats[placement.Type];
            var minion = AddUnit(runtime.World, placement.Id, EntityKind.Minion,
                UnitType.Minion, Team.Order, placement.Position, stats.Radius);
            minion.MinionType = placement.Type;
            minion.MinionLane = "C";
            minion.Speed = stats.MoveSpeed;
            minion.AttackRange = stats.AttackRange;
            minion.AcquisitionRange = stats.AcquisitionRange;
            minion.Waypoints = [new Position2(10639, 10687) + offset, terminal];
            minion.LaneWaypoints = minion.Waypoints.ToList();
            minion.AnchorPath = [placement.Position];
            // Batch 279: the congestion wait these minions were parked with is deleted (nothing set it since batch 271).
            return minion;
        }).ToArray();
        // Also exercise the control/merge private-suffix representation.
        minions[0].InterruptedLaneWaypoints = minions[0].Waypoints.ToList();
        minions[0].Waypoints.Clear();
        var progressed = new HashSet<uint>();
        for (var tick = 0; tick < 50 && progressed.Count < minions.Length; tick++)
        {
            runtime.Tick(.1);
            for (var first = 0; first < minions.Length; first++)
            {
                var minion = minions[first];
                TestAssert.True(minion.Alive, "progress cannot depend on removing a parked actor");
                var start = placements[first].Position;
                if (minion.Position.X + minion.Position.Z - start.X - start.Z >= 200.0)
                    progressed.Add(minion.NetId);
                // Batch 279: the per-tick body clearance was the parked advance's (B4 #10), which only a congestion-waiting minion took;
                // the wait and that advance are deleted. The minions walk their routes under the client solver, which lets the
                // captured formation touch (it broke the clearance at its first tick), as retail's does.
            }
        }
        TestAssert.True(minions.All(minion => minion.LaneDone &&
                                             minion.InterruptedLaneWaypoints.Count == 0),
            "the occupied terminal must retire every retained lane connector");
        TestAssert.Equal(minions.Length, progressed.Count,
            "all three minions must make strategic forward progress within five seconds without an enemy wave");
    }

    // The retail return rule on the production graph: every returning minion gets its
    // forward-navigation route at the lethal edge and keeps walking it to the merge
    // corner, with no hold, no stop for merging and no parked suffix; never on a client
    // blocked cell; all pass the corner. pairFloor additionally asserts the half pair
    // clearance (83.5 / 2) that the captured two-caster case still keeps.
    private static void AssertDirectLaneReturn(string lane, Position2 laneHead,
        Position2 mergeCorner, int ticks, bool pairFloor,
        (MinionType Type, Position2 Position)[] placements)
    {
        var runtime = StartSolo();
        var waves = runtime.Waves ?? throw new InvalidOperationException(
            "production match did not compose minion waves");
        waves.AutomaticSpawningEnabled = false;
        var route = waves.LaneRoute(Team.Order, lane);
        var head = Enumerable.Range(0, route.Count).MinBy(index => Distance(route[index], laneHead));
        TestAssert.True(head + 2 < route.Count && Distance(route[head], laneHead) <= 10.0 &&
                        Distance(route[head + 1], mergeCorner) <= 10.0,
            $"production lane {lane} no longer merges at the logged corner");
        var merge = route[head + 1];
        var outward = route[head + 2] - merge;
        bool Passed(Entity minion) => (minion.Position.X - merge.X) * outward.X +
            (minion.Position.Z - merge.Z) * outward.Z >= 0.0;
        var dummy = waves.SpawnFixture(Team.Chaos, MinionType.Melee, 0, lane,
            now: 0.0, scheduledElapsed: 0.0);
        var minions = placements.Select((placement, slot) =>
        {
            var minion = waves.SpawnFixture(Team.Order, placement.Type, slot, lane,
                now: 0.0, scheduledElapsed: 0.0);
            minion.Position = placement.Position;
            minion.Waypoints.Clear();
            minion.Target = null;
            minion.AnchorPath = [placement.Position];
            minion.LaneWaypoints = route.Skip(head).ToList();
            minion.MinionState = MinionAiState.Engage;
            minion.AttackTarget = dummy.NetId;
            return minion;
        }).ToArray();
        // A real stop packet before a minion has reached the merge corner would be a
        // hold (the removed lane-merge-yield sent exactly this).
        var earlyStops = new List<uint>();
        runtime.Minions!.ClearMovementObserver = (minion, _, _, _) =>
        {
            if (minions.Contains(minion) && !Passed(minion)) earlyStops.Add(minion.NetId);
        };

        TestAssert.True(runtime.Combat.Kill(dummy), "real Combat.Kill must kill the dummy");
        TestAssert.True(minions.All(minion => minion.AttackTarget is null &&
                                              minion.MinionState == MinionAiState.March),
            "the lethal edge must return every engaged minion to its lane");
        TestAssert.True(minions.All(minion => minion.Waypoints.Count > 0 &&
                                              minion.InterruptedLaneWaypoints.Count == 0),
            "every returning minion must receive its forward route at the lethal edge, " +
            "however close it would reach the merge to another");

        var minimum = runtime.Movement.BodyClearance(minions[0], minions[1]) / 2.0;
        var navigation = runtime.Navigation;
        var passed = new HashSet<uint>();
        for (var tick = 0; tick < ticks; tick++)
        {
            runtime.Tick(0.1);
            var alive = minions.Where(minion => minion.Alive).ToArray();
            foreach (var minion in alive)
            {
                var cell = navigation.WorldToCell(minion.Position);
                TestAssert.True(!navigation.IsInBounds(cell) || navigation.IsMovementWalkable(cell),
                    $"lane {lane} tick {tick}: {minion.NetId:x} stands on a client-blocked cell");
                if (!Passed(minion))
                    TestAssert.True(minion.Waypoints.Count > 0,
                        $"lane {lane} tick {tick}: {minion.NetId:x} has no route before the merge corner");
            }
            if (pairFloor)
                for (var first = 0; first < alive.Length; first++)
                for (var second = first + 1; second < alive.Length; second++)
                {
                    var gap = Distance(alive[first].Position, alive[second].Position);
                    TestAssert.True(gap >= minimum,
                        $"lane {lane} tick {tick}: {alive[first].NetId:x}/{alive[second].NetId:x} " +
                        $"stacked {gap:R} < {minimum:R}");
                }
            foreach (var minion in alive)
                if (Passed(minion)) passed.Add(minion.NetId);
        }
        TestAssert.Equal(0, earlyStops.Count,
            $"lane {lane}: a minion was stopped before the merge corner: {string.Join(",", earlyStops.Select(id => id.ToString("x")))}");
        TestAssert.Equal(minions.Length, passed.Count,
            $"lane {lane}: every returning minion must pass the merge corner within {ticks / 10.0:R}s");
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

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));
}
