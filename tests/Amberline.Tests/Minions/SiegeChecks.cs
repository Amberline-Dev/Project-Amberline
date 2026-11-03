using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Ports the two source siege regressions.  The first check keeps the lane
/// terminus and structure eligibility counterfactual; the second drives the
/// production collision solver against the recorded planted-body exhibits.
/// </summary>
public static partial class SiegeChecks
{
    private const double Tick = 1.0 / 30.0;
    private const double SiegeRadius = 55.74369812011719;
    private const double CasterRadius = 35.74369812011719;

    [Check("minions.b4-terminal-stays-on-lane-to-nexus")]
    public static void TerminalStaysOnLaneToNexus()
    {
        using var runtime = CreateStructureRuntime();
        var nexus = runtime.World.Entities.Values.Single(entity => entity.Name == "HQ_T2");
        var side = runtime.World.Entities.Values.Single(entity => entity.Name == "Turret_T2_L_01");
        ExposeNexus(runtime, nexus, side);
        var start = new Position2(10687.0, 10709.0);
        var minion = PlaceStructureMinion(runtime, MinionType.Melee, "C", start);
        TestAssert.True(Distance(start, side.Position) < Distance(start, nexus.Position),
            "captured terminal has a nearer surviving side-lane turret");
        TestAssert.False(nexus.Protected, "the mid inhibitor and nexus defenses are down");
        runtime.Tick(Tick);
        TestAssert.True(minion.Waypoints.Count > 1, "terminal issues a strategic approach");
        TestAssert.True(Distance(minion.Waypoints[^1], nexus.Position) <= CombatService.AttackReach(minion, nexus),
            "distant strategic routing must progress to the nexus instead of another lane");
        var enemy = runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "L", 0.0, 0.0);
        enemy.Position = minion.Position + new Position2(80.0, 0.0);
        enemy.Waypoints.Clear();
        enemy.LaneWaypoints.Clear();
        enemy.AnchorPath = [enemy.Position];
        enemy.Created = enemy.Visible = true;
        for (var tick = 0; tick < 10; tick++) runtime.Tick(Tick);
        TestAssert.Equal(enemy.NetId, minion.AttackTarget ?? 0,
            "nearby cross-lane combat still interrupts the strategic nexus approach");
    }

    [Check("minions.b4-full-size-melee-cannon-nexus")]
    public static void FullSizeMeleeAndCannonDamageNexus() =>
        AssertFullSizeMeleeAndCannonDamageNexus(Team.Chaos);

    [Check("minions.b4-full-size-melee-cannon-order-nexus")]
    public static void FullSizeMeleeAndCannonDamageOrderNexus() =>
        AssertFullSizeMeleeAndCannonDamageNexus(Team.Order);

    private static void AssertFullSizeMeleeAndCannonDamageNexus(Team defendingTeam)
    {
        foreach (var type in new[] { MinionType.Melee, MinionType.Siege })
        foreach (var capturedStart in new[] { new Position2(12495, 12763), new Position2(12445, 13113) })
        {
            using var runtime = CreateStructureRuntime();
            var chaosNexus = runtime.World.Entities.Values.Single(entity => entity.Name == "HQ_T2");
            foreach (var hero in runtime.World.Heroes()) hero.Position = new Position2(7000, 7000);
            var nexus = runtime.World.Entities.Values.Single(entity =>
                entity.Kind == EntityKind.Nexus && entity.Team == defendingTeam);
            var start = defendingTeam == Team.Chaos ? capturedStart :
                nexus.Position - (capturedStart - chaosNexus.Position);
            ExposeNexus(runtime, nexus);
            var attackerTeam = MinionDefinitions.Opposing(defendingTeam);
            var defendingShrine = runtime.World.Entities.Values.Single(entity =>
                TurretRules.IsFountainTurret(entity) && entity.Team == defendingTeam);
            start = OutsideShrineRange(defendingShrine, start, MinionDefinitions.BaseStats(type, attackerTeam).Radius);
            var minion = PlaceStructureMinion(runtime, type, "R", start, attackerTeam);
            var shrines = runtime.World.Entities.Values.Where(TurretRules.IsFountainTurret).ToArray();
            TestAssert.Equal(2, shrines.Length);
            TestAssert.True(shrines.All(shrine => shrine.Alive), "both shrines start alive");
            var shrine = shrines.Single(entity => entity.Team == defendingTeam);
            // Batch 204: the shipped shrine range is 2250. The captured starts (2372.72/2458.46 and 2176.15/2258.80 away,
            // Chaos/Order) were outside the old 1250 and are inside 2250, so OutsideShrineRange moved them to the range edge.
            TestAssert.False(CombatService.InRange(shrine, minion));
            TestAssert.Near(MinionDefinitions.BaseStats(type, attackerTeam).Radius, minion.Radius, 1e-9,
                "production full-size minion; no half-radius siege counterfactual");
            TestAssert.Equal(1, runtime.World.Entities.Values.Count(entity => entity.MinionType is not null),
                "one attacker per exhibit, with no casters to hide a melee/cannon failure");
            TestAssert.Near(279.43333435058594, nexus.Radius, 1e-9, "loaded Nexus.skn attack geometry");
            // 18 October 2026 (499d0785): the Nexus is no actor on the stock client (its nav-grid stamp writes nav cells only), so it has
            // no movement body; its ObjectCFG pathing geometry is the stamp's footprint and stays separate on each team.
            TestAssert.Near(0.0, runtime.Movement.MovementRadius(nexus), 1e-9, "the Nexus has no movement body");
            TestAssert.Near(defendingTeam == Team.Order ? 352.7778 : 240.7846, nexus.PathfindingRadius ?? nexus.Radius, 1e-9,
                "ObjectCFG nexus pathing geometry remains separate on each team");
            var initialHealth = nexus.Health;
            var prior = minion.Position;
            var stationary = 0;
            var maxStationary = 0;
            var damaged = false;
            for (var tick = 0; tick < 600; tick++)
            {
                runtime.Tick(Tick);
                if (nexus.Health < initialHealth)
                {
                    damaged = true;
                    break;
                }
                var outside = !CombatService.InRange(minion, nexus);
                stationary = outside && Distance(prior, minion.Position) < .01 ? stationary + 1 : 0;
                maxStationary = Math.Max(maxStationary, stationary);
                if (outside && minion.Waypoints.Count > 1)
                    TestAssert.True(Distance(minion.Position, minion.Waypoints[^1]) > 1.0,
                        "out-of-range chase must not repeat a stationary endpoint");
                prior = minion.Position;
            }
            TestAssert.True(damaged, $"{type} from {start} must itself damage the exposed nexus within 20 seconds");
            TestAssert.True(shrines.All(shrine => shrine.Alive), "both shrines remain alive during the siege");
            TestAssert.True(maxStationary < 120,
                $"{type} must not repeat the captured stationary chase for four seconds");
        }
    }

    [Check("minions.b4-structure-approach-rejects-snapped-endpoint")]
    public static void StructureApproachRejectsSnappedEndpoint()
    {
        foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
        {
            var grid = NavigationGrid.LoadDefault();
            var world = new WorldState();
            var minion = world.Register(NewMinion(1000, new Position2(7000, 7000),
                MinionType.Melee, MinionAiState.March, MinionDefinitions.BaseStats(MinionType.Melee).Radius));
            var radius = kind switch { EntityKind.Turret => MapDefinitions.TurretRadius,
                EntityKind.Inhibitor => MapDefinitions.InhibitorRadius, _ => MapDefinitions.NexusRadius };
            var target = world.Register(new Entity(1001, kind,
                kind == EntityKind.Turret ? UnitType.Turret : UnitType.Structure,
                Team.Chaos, new Position2(7500, 7000)) { Radius = radius, Alive = true });
            var movement = new MovementSystem(world, grid);
            var calls = 0;
            grid.FindPathOverride = (start, goal) =>
                ++calls == 1 ? [start, start + new Position2(2, 0)] : [start, goal];
            var reach = CombatService.AttackReach(minion, target);
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            TestAssert.Near(minion.CombatRadius + minion.AttackRange + target.Radius, reach, 1e-9);
            var plan = movement.ApproachWithin(minion, target, reach - 6.0, reach);
            TestAssert.True(calls > 1, $"{kind} rejects the first snapped point and tries another angle");
            TestAssert.Equal(MovementSystem.MoveStatus.Moving, plan.Status);
            TestAssert.True(Distance(plan.Path[^1], target.Position) <= reach,
                "only an endpoint within the combat reach may be committed");
        }
    }

    private static MatchRuntime CreateStructureRuntime()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null);
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1));
        TestAssert.True(runtime.Session.Start());
        runtime.Waves!.AutomaticSpawningEnabled = false;
        return runtime;
    }

    /// <summary>
    /// Batch 204: the shipped shrine range is 2250 (the captured starts were recorded against the old 1250). A start inside
    /// the defending shrine's range moves straight away from the shrine to the range edge, so the exhibit keeps its premise:
    /// the siege minion is not shot by the shrine.
    /// </summary>
    private static Position2 OutsideShrineRange(Entity shrine, Position2 start, double minionRadius)
    {
        var dx = start.X - shrine.Position.X;
        var dy = start.Z - shrine.Position.Z;
        var gap = Math.Sqrt(dx * dx + dy * dy);
        var reach = shrine.AttackRange + shrine.Radius + minionRadius + 10.0;
        if (gap >= reach) return start;
        var scale = reach / Math.Max(gap, 1e-9);
        return new Position2(shrine.Position.X + dx * scale, shrine.Position.Z + dy * scale);
    }

    private static void ExposeNexus(MatchRuntime runtime, Entity nexus, Entity? side = null)
    {
        foreach (var structure in runtime.World.Entities.Values.Where(entity => entity.Team == nexus.Team &&
                     entity.UnitType is UnitType.Turret or UnitType.Structure && entity != nexus && entity != side &&
                     !entity.Name.Contains("Shrine", StringComparison.Ordinal)).ToArray())
            runtime.Combat.Kill(structure);
        runtime.Combat.EngageProtection();
    }

    private static Entity PlaceStructureMinion(MatchRuntime runtime, MinionType type, string lane,
        Position2 start, Team team = Team.Order)
    {
        var minion = runtime.Waves!.SpawnFixture(team, type, 0, lane, 0.0, 0.0);
        minion.Position = start;
        minion.Waypoints.Clear();
        minion.LaneWaypoints.Clear();
        minion.Target = null;
        minion.AttackTarget = null;
        minion.LaneDone = true;
        minion.MinionState = MinionAiState.March;
        minion.Created = minion.Visible = true;
        minion.AnchorPath = [start];
        return minion;
    }

    [Check("minions-lane-terminus-siege")]
    public static void LaneTerminusUsesLiveStructureCounterfactual()
    {
        var blocked = BuildTerminus(siegeRoutingEnabled: false);
        RunAi(blocked, seconds: 4.0);
        TestAssert.Equal(0, blocked.Packets.WaypointOrders.Count,
            "a protected terminus must not emit a siege route");
        TestAssert.True(blocked.Minions.All(minion =>
                Distance(minion.Position, blocked.Terminus) <= 1e-9 &&
                minion.AttackTarget is null && minion.Waypoints.Count == 0),
            "the protected-structure control must keep every parked marcher at the terminus");
        TestAssert.True(blocked.Nexus.Protected,
            "the protected nexus must remain protected in the no-siege control");
        TestAssert.False(blocked.Keep.Protected,
            "the no-siege control must leave the live turret attackable");

        var live = BuildTerminus(siegeRoutingEnabled: true);
        RunAi(live, seconds: 30.0);
        var moved = live.Minions.Count(minion => Distance(minion.Position, live.Terminus) > 1.0);
        TestAssert.Equal(live.Minions.Count, moved,
            "every route-exhausted minion must leave the terminus for the live turret");
        TestAssert.Equal(live.Minions.Count, live.Packets.WaypointOrders
                .Select(order => order.NetId).Distinct().Count(),
            "each route-exhausted minion must receive a real siege route");
        TestAssert.True(live.Minions.Any(minion => minion.AttackTarget == live.Keep.NetId),
            "the siege must eventually acquire the turret it walked toward");
        TestAssert.True(live.Keep.Alive && live.Keep.Health == live.Keep.MaxHealth,
            "this isolated AI check must not invent combat damage while validating siege movement");
        TestAssert.True(live.Nexus.Protected,
            "the nexus protection state is retained until the separate combat owner kills the turret");

        var production = RunProductionSiege();
        TestAssert.False(production.Keep.Alive,
            "the production 30-second siege must damage and kill the surviving turret");
        // Batch 236: the wave plans round its own standing members (the engine planner's actor test), so more of it reaches the nexus
        // and can kill it inside the 30 seconds; a dead nexus reads Protected again. The recompute is pinned by the moment it unprotected.
        TestAssert.True(production.NexusUnprotectedAt is not null,
            "the production protection recompute must unprotect the nexus after the last nexus turret dies");
        TestAssert.True(production.Nexus.Health < production.NexusInitialHealth,
            "the production siege must damage the nexus after the last turret dies");
        TestAssert.Equal(production.Minions.Count, production.FirstMoveAt.Count,
            "every production minion must leave the parked terminus promptly");
        TestAssert.True(production.FirstMoveAt.Values.All(time => time <= 0.2 + 1e-9),
            "every production minion must make its first move within 0.2 seconds");
        // Batch 248 (restated): the old bound of 35 counted a Return state that was set and cleared inside one decision, so it read 0;
        // a return is now the Engage -> AI_ATTACKMOVESTATE transition the script makes (41 on the goal-2 base). Bound: at most one return
        // per minion per four-second anti-kite window of the 30 seconds.
        TestAssert.True(production.ReturnTransitions <= production.Minions.Count * 8,
            $"production siege returned to lane too often ({production.ReturnTransitions} for {production.Minions.Count} minions)");
        TestAssert.True(production.AcquisitionPairs.Count > 0,
            "production siege must record a real structure acquisition");
        TestAssert.True(production.MinimumReacquireInterval >= 4.0 - 1e-9,
            "reacquisition must respect the shipped four-second anti-kite timer");
        TestAssert.True(production.KeepDiedAt is double keepDied &&
                        production.NexusUnprotectedAt is double unprotected &&
                        keepDied <= unprotected + 1e-9,
            $"the nexus must become unprotected only after the surviving turret dies (keep={production.KeepDiedAt?.ToString() ?? "null"}, nexus={production.NexusUnprotectedAt?.ToString() ?? "null"})");

        var badRetain = RunProductionSiege(badRetain: true);
        // Minion.lua 0.2's retention is a same-step re-decide -- "if
        // DistanceBetweenMeAndObject(GetTarget()) > MAX_ENGAGE_DISTANCE: FindTargetOrMove()" on the
        // DELAY_FIND_ENEMIES tick (Minion.lua 0.2 0043-0055 calling Minion.lua 0.3) -- not the old
        // machine's clear-and-rescan. Minion.lua 0.3 re-takes what FindTargetInAcR still sees inside
        // the acquire reach (0020-0026: SetStateAndCloseToTarget on the found target), and this
        // terminus siege holds its structure inside that reach at every scan the radius is short
        // enough to fire on (a caster closes at AcquisitionRange 700 + the structure radius), so
        // AttackTarget never dips through null: the drop-then-re-acquire oscillation this control
        // used to look for cannot form, and neither can an early give-up. What a radius at the
        // acquire reach does produce is a re-close on every find-enemies tick the target is past it,
        // where production's 2500 holds without re-deciding. The control is wired when that cadence
        // differs; if the global never reached the script the two runs would agree here.
        TestAssert.True(badRetain.Recloses > production.Recloses * 1.5,
            $"450-radius retention must re-close on its held structure far more often than production's hold " +
            $"(bad={badRetain.Recloses} of {badRetain.Closes} closes, good={production.Recloses} of {production.Closes})");
        // Structure-safe routes can keep an attacker inside the old retention
        // circle or delay its return to acquisition range. Measure the defect
        // at target loss, independently of that subsequent route geometry.
        TestAssert.Equal(0, production.PrematureLiveTargetDrops,
            "production must retain a live structure through the four-second pursuit window");
    }

    private static TerminusFixture BuildTerminus(bool siegeRoutingEnabled)
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var packets = new RecordingPackets();
        var scheduler = new WaveScheduler(world, grid, packets, LevelScriptFixtures.Init(world));
        scheduler.WarmAllRoutes();
        var route = scheduler.LaneRoute(Team.Chaos, "L");
        var terminus = route[^1];
        var keepPosition = MapDefinitions.TurretPositions["Turret_T1_C_02"];
        var keep = new Entity(0x50000001, EntityKind.Turret, UnitType.Turret,
            Team.Order, keepPosition)
        {
            Name = "Turret_T1_C_02",
            Alive = true,
            Created = true,
            Visible = true,
            Protected = false,
            Radius = MapDefinitions.TurretRadius,
            Health = 1300.0,
            MaxHealth = 1300.0,
            AttackRange = 1200.0,
        };
        var nexus = new Entity(0x50000002, EntityKind.Nexus, UnitType.Structure,
            Team.Order, MapDefinitions.NexusPositions["HQ_T1"])
        {
            Name = "HQ_T1",
            Alive = true,
            Created = true,
            Visible = true,
            Protected = true,
            Radius = MapDefinitions.NexusRadius,
            Health = MapDefinitions.NexusMaxHealth,
            MaxHealth = MapDefinitions.NexusMaxHealth,
        };
        world.Register(keep);
        world.Register(nexus);
        var minions = new List<Entity>();
        for (var wave = 0; wave < 3; wave++)
        {
            var composition = LevelScriptFixtures.Wave(world, Team.Chaos, "L", wave);
            for (var slot = 0; slot < composition.Count; slot++)
            {
                var minion = scheduler.SpawnFixture(Team.Chaos, composition[slot], slot,
                    "L", 0.0, 0.0);
                minion.Position = terminus;
                minion.Waypoints.Clear();
                minion.LaneWaypoints.Clear();
                minion.Target = null;
                minion.AttackTarget = null;
                minion.LaneDone = false;
                minion.MinionState = MinionAiState.March;
                minion.Created = true;
                minion.Visible = true;
                minion.AnchorPath = [terminus];
                minions.Add(minion);
            }
        }
        var movement = new MovementSystem(world, grid);
        var ai = new MinionAiSystem(world, grid, movement, new PassiveCombat(), packets,
            new AlwaysVision());
        // This is the source no-siege-route counterfactual: structure
        // eligibility remains live, but only the siege branch is stubbed.
        ai.SiegeRoutingEnabled = siegeRoutingEnabled;
        return new TerminusFixture(world, movement, ai, minions, keep, nexus,
            terminus, packets);
    }

    private static void RunAi(TerminusFixture fixture, double seconds)
    {
        var ticks = (int)Math.Round(seconds / Tick, MidpointRounding.AwayFromZero);
        for (var index = 0; index < ticks; index++)
        {
            fixture.Ai.Tick(Tick);
            fixture.Movement.Tick(Tick);
        }
    }

    private static ProductionSiege RunProductionSiege(bool badRetain = false)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
                runtime.Session.BuildKeyCheck(1)) is not null,
            "production siege observer authentication");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "production siege observer ready");
        TestAssert.True(runtime.Session.Start(), "production siege session start");

        var route = runtime.Waves!.LaneRoute(Team.Chaos, "L");
        var terminus = route[^1];
        var keep = runtime.World.Entities.Values
            .Where(entity => entity.Team == Team.Order &&
                entity.UnitType == UnitType.Turret &&
                MapDefinitions.TurretTiers.TryGetValue(entity.Name, out var tier) &&
                tier == TurretTier.Nexus)
            .OrderByDescending(entity => Distance(entity.Position, terminus))
            .First();
        var nexus = runtime.World.Entities.Values.Single(entity =>
            entity.Name == "HQ_T1");
        foreach (var structure in runtime.World.Entities.Values.Where(entity =>
                     entity.Team == Team.Order &&
                     (entity.UnitType == UnitType.Turret || entity.UnitType == UnitType.Structure) &&
                     entity.NetId != keep.NetId && entity.NetId != nexus.NetId &&
                     !entity.Name.Contains("Shrine", StringComparison.Ordinal) &&
                     !(MapDefinitions.TurretTiers.TryGetValue(entity.Name, out var tier) &&
                       tier == TurretTier.Fountain)).ToArray())
            runtime.Combat.Kill(structure);
        runtime.Combat.EngageProtection();
        keep.Health = keep.MaxHealth = Math.Min(1300.0, keep.MaxHealth);
        TestAssert.True(keep.Alive && !keep.Protected,
            $"production setup selected an attackable nexus turret ({keep.Name})");
        TestAssert.True(nexus.Protected,
            "production setup must keep the nexus protected behind the surviving nexus turret");

        var minions = new List<Entity>();
        for (var wave = 0; wave < 3; wave++)
        {
            var composition = LevelScriptFixtures.Wave(runtime.World, Team.Chaos, "L", wave);
            for (var slot = 0; slot < composition.Count; slot++)
            {
                var minion = runtime.Waves.SpawnFixture(Team.Chaos, composition[slot],
                    slot, "L", runtime.Combat.CurrentTime, 0.0);
                // The historical 450-radius retention bug lives in this control alone. Retention
                // is the script's now: Minion.lua 0.2 compares DistanceBetweenMeAndObject(GetTarget())
                // against its own MAX_ENGAGE_DISTANCE, the shipped chunk's 2500, so the shorter
                // radius is that global and not an engine-side override (MinionAiSystem had one
                // and it no longer fed anything).
                if (badRetain)
                    runtime.Minions!.Host.InstanceFor(minion)?.SetGlobal("MAX_ENGAGE_DISTANCE", 450.0);
                // playtest B4 / retail sweep, 27 Sept 2026: freeze the old
                // acquisition gate in both retention counterfactuals, so only
                // the retention rule differs between their crowd trajectories.
                if (minion.MinionType is MinionType.Melee or MinionType.Siege)
                    minion.AcquisitionRange = 450.0;
                // This retention counterfactual depends on its original crowd
                // trajectory. Preserve the .5 movement discs in both variants;
                // lane-minion pairs now use the unscaled radius (playtest B2
                // AE5, 26 Sept 2026, replacing the playtest #6 NPC-pair
                // compensation), so halve the radius directly. Retention
                // distances and every timer assertion remain unchanged.
                var originalRadius = minion.Radius;
                minion.Radius *= 0.5;
                TestAssert.Near(originalRadius * 0.5,
                    runtime.Movement.PairMovementRadius(minion, minion), 1e-6,
                    "the siege control retains its original minion-pair movement shell");
                minion.Position = terminus;
                minion.Waypoints.Clear();
                minion.LaneWaypoints.Clear();
                minion.Target = null;
                minion.AttackTarget = null;
                minion.MinionState = MinionAiState.March;
                minion.Created = true;
                minion.Visible = true;
                minion.AnchorPath = [terminus];
                minions.Add(minion);
            }
        }
        var initialPositions = minions.ToDictionary(minion => minion.NetId,
            minion => minion.Position);
        var firstMoveAt = new Dictionary<uint, double>();
        var previousStates = minions.ToDictionary(minion => minion.NetId,
            minion => minion.MinionState);
        var previousTargets = minions.ToDictionary(minion => minion.NetId,
            minion => minion.AttackTarget);
        var acquisitionPairs = new Dictionary<(uint Minion, uint Target), int>();
        var lastAcquisition = new Dictionary<(uint Minion, uint Target), double>();
        var pursuitStarted = new Dictionary<uint, double>();
        var prematureLiveTargetDrops = 0;
        // A close is every time the engine is asked to take or re-take a target; a re-close is one
        // that re-takes the target the minion is already holding, which is what Minion.lua 0.2's
        // retention arm produces when it runs FindTargetOrMove again on a held target (0043-0055).
        var closes = 0;
        var recloses = 0;
        runtime.Minions!.AcquireDecisionObserver = (minion, target, _) =>
        {
            closes++;
            if (minion.AttackTarget == target.NetId) recloses++;
            if (minion.AttackTarget != target.NetId || minion.MinionState != MinionAiState.Engage)
                pursuitStarted[minion.NetId] = runtime.Combat.CurrentTime;
        };
        runtime.Minions.ReturnDecisionObserver = (minion, _) =>
        {
            if (minion.AttackTarget is uint held &&
                runtime.World.Get(held) is { Alive: true, Protected: false } &&
                pursuitStarted.TryGetValue(minion.NetId, out var started) &&
                runtime.Combat.CurrentTime - started < 4.0 - 1e-9)
                prematureLiveTargetDrops++;
        };
        var minimumReacquireInterval = double.PositiveInfinity;
        var returnTransitions = 0;
        double? keepDiedAt = null;
        double? nexusUnprotectedAt = null;
        for (var tick = 0; tick < (int)Math.Round(30.0 / Tick); tick++)
        {
            runtime.Tick(Tick);
            var now = runtime.Combat.CurrentTime;
            foreach (var minion in minions)
            {
                if (!firstMoveAt.ContainsKey(minion.NetId) &&
                    Distance(minion.Position, initialPositions[minion.NetId]) > 1e-3)
                    firstMoveAt[minion.NetId] = now;
                // Batch 248: the return to the lane is AI_ATTACKMOVESTATE (March) from its first moment: a return is an
                // Engage -> March transition.
                if (minion.MinionState == MinionAiState.March &&
                    previousStates[minion.NetId] == MinionAiState.Engage)
                    returnTransitions++;
                if (minion.AttackTarget is uint target &&
                    previousTargets[minion.NetId] != target)
                {
                    var pair = (minion.NetId, target);
                    acquisitionPairs[pair] = acquisitionPairs.GetValueOrDefault(pair) + 1;
                    if (lastAcquisition.TryGetValue(pair, out var previous))
                        minimumReacquireInterval = Math.Min(minimumReacquireInterval, now - previous);
                    lastAcquisition[pair] = now;
                }
                previousStates[minion.NetId] = minion.MinionState;
                previousTargets[minion.NetId] = minion.AttackTarget;
            }
            if (tick == 5)
            {
                var providers = VisibilityRules.BuildTeamProviders(runtime.World, Team.Chaos,
                    Array.Empty<BallProjection>(), _ => Array.Empty<VisionProvider>());
                var keepVisible = VisibilityRules.RevealsEntity(runtime.World, Team.Chaos,
                    new NavigationGridVisionEnvironment(runtime.Navigation), providers,
                    _ => false, keep);
                TestAssert.False(keepVisible,
                    "the public terminus approach starts before team sight reaches the nexus turret");
                TestAssert.True(minions.All(minion => minion.AttackTarget is null),
                    "unseen strategic approach must not nominate an attack target");
                TestAssert.True(firstMoveAt.Count > 0,
                    "at least one parked minion approaches before team sight reaches the turret");
            }
            if (keepDiedAt is null && !keep.Alive) keepDiedAt = now;
            if (nexusUnprotectedAt is null && !nexus.Protected)
                nexusUnprotectedAt = now;
        }
        return new ProductionSiege(keep, nexus, nexus.MaxHealth,
            minions, firstMoveAt, returnTransitions, acquisitionPairs,
            keepDiedAt, nexusUnprotectedAt, minimumReacquireInterval, prematureLiveTargetDrops,
            closes, recloses);
    }

    private static PinchRun RunPinch(Exhibit exhibit, bool bend, int ticks = 90,
        bool pathingFlag = true, double movementDt = Tick, bool wire = false)
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var sink = new SnapshotSink();
        var packets = new RecordingPackets();
        var movement = new MovementSystem(world, grid, diagnosticSink: sink)
        {
            BendWaypointsEnabled = bend,
        };
        var siege = NewMinion(1000, exhibit.Position, MinionType.Siege,
            MinionAiState.March, SiegeRadius);
        siege.PathingFlag290 = pathingFlag;
        siege.Waypoints = exhibit.Waypoints.ToList();
        siege.AnchorPath = [siege.Position, .. exhibit.Waypoints];
        world.Register(siege);
        for (var index = 0; index < exhibit.Planted.Count; index++)
        {
            var caster = NewMinion((uint)(1001 + index), exhibit.Planted[index],
                MinionType.Caster, MinionAiState.Engage, CasterRadius);
            caster.Waypoints.Clear();
            caster.Target = null;
            world.Register(caster);
        }
        // The AI only subscribes to the movement events here (it publishes recovered routes into the wire view);
        // it needs no tick of its own.
        _ = wire
            ? new MinionAiSystem(world, grid, movement, new PassiveCombat(), packets,
                new AlwaysVision())
            : null;
        var positions = new List<Position2> { siege.Position };
        var routeBefore = siege.Waypoints.ToArray();
        IReadOnlyList<Position2>? bypassRoute = null;
        int? bypassTick = null;
        for (var index = 0; index < ticks; index++)
        {
            movement.Tick(movementDt);
            if (bypassTick is null && sink.Snapshots.Count > 0 &&
                (sink.Snapshots[^1].Branches ?? []).Any(branch =>
                    branch.StartsWith("bypass", StringComparison.Ordinal)))
            {
                bypassTick = index;
                bypassRoute = siege.Waypoints.ToArray();
            }
            packets.CurrentTick = index;
            positions.Add(siege.Position);
        }
        var routeChanged = !routeBefore.SequenceEqual(siege.Waypoints) || bypassRoute is not null;
        var mergedGoal = bypassRoute is not null &&
            bypassRoute[^1] == exhibit.Waypoints[^1];
        return new PinchRun(positions, sink.Snapshots, siege, routeChanged,
            mergedGoal, bypassTick, packets.WaypointOrders.ToArray());
    }

    private static PinchRun RunPolicyProbe(MinionAiState state, Team blockerTeam,
        bool moving)
    {
        var exhibit = new Exhibit("policy", new Position2(8000.0, 8000.0),
            [new Position2(8200.0, 8000.0), new Position2(8500.0, 8000.0)],
            [new Position2(8070.0, 7970.0), new Position2(8070.0, 8030.0)]);
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var sink = new SnapshotSink();
        var movement = new MovementSystem(world, grid, diagnosticSink: sink)
        {
            BendWaypointsEnabled = true,
        };
        var mover = NewMinion(2000, exhibit.Position, MinionType.Siege, state, SiegeRadius);
        mover.Waypoints = exhibit.Waypoints.ToList();
        world.Register(mover);
        for (var index = 0; index < exhibit.Planted.Count; index++)
        {
            var blocker = NewMinion((uint)(2001 + index), exhibit.Planted[index],
                MinionType.Caster, MinionAiState.Engage, CasterRadius);
            blocker.Team = blockerTeam;
            blocker.Waypoints = moving ? [new Position2(8300.0, blocker.Position.Z)] : [];
            blocker.Target = null;
            world.Register(blocker);
        }
        for (var index = 0; index < 30; index++) movement.Tick(Tick);
        return new PinchRun([], sink.Snapshots, mover, false, true, null, []);
    }

    private static Entity NewMinion(uint netId, Position2 position, MinionType type,
        MinionAiState state, double radius)
    {
        var stats = MinionDefinitions.BaseStats(type);
        return new Entity(netId, EntityKind.Minion, UnitType.Minion, Team.Order, position)
        {
            MinionType = type,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = stats.MoveSpeed,
            Radius = radius,
            AttackRange = stats.AttackRange,
            AcquisitionRange = stats.AcquisitionRange,
            Health = stats.MaxHealth,
            MaxHealth = stats.MaxHealth,
            MinionState = state,
        };
    }

    private static int FrozenRun(PinchRun result) =>
        LongestRun(result.Positions.Zip(result.Positions.Skip(1),
            (left, right) => Distance(left, right) < 0.01));

    private static double StallShare(PinchRun result) =>
        result.Positions.Count < 2 ? 0.0 : result.Positions.Zip(result.Positions.Skip(1),
            (left, right) => Distance(left, right) < 0.01).Count(value => value) /
            (double)(result.Positions.Count - 1);

    private static int Phasing(PinchRun result, bool pathingFlag = true)
    {
        var limit = pathingFlag
            ? MovementCollisionSettings.Default.CanCollideRepathLimitFlagged
            : MovementCollisionSettings.Default.CanCollideRepathLimit;
        return result.Snapshots.Count(snapshot => snapshot.RepathCount > limit);
    }

    private static int LongestRun(IEnumerable<bool> values)
    {
        var best = 0;
        var current = 0;
        foreach (var value in values)
        {
            current = value ? current + 1 : 0;
            best = Math.Max(best, current);
        }
        return best;
    }

    private static double Distance(Position2 left, Position2 right) =>
        Math.Sqrt((left.X - right.X) * (left.X - right.X) +
                  (left.Z - right.Z) * (left.Z - right.Z));

    private sealed record TerminusFixture(WorldState World, MovementSystem Movement,
        MinionAiSystem Ai, IReadOnlyList<Entity> Minions, Entity Keep, Entity Nexus,
        Position2 Terminus, RecordingPackets Packets);

    private sealed record ProductionSiege(
        Entity Keep,
        Entity Nexus,
        double NexusInitialHealth,
        IReadOnlyList<Entity> Minions,
        IReadOnlyDictionary<uint, double> FirstMoveAt,
        int ReturnTransitions,
        IReadOnlyDictionary<(uint Minion, uint Target), int> AcquisitionPairs,
        double? KeepDiedAt,
        double? NexusUnprotectedAt,
        double MinimumReacquireInterval,
        int PrematureLiveTargetDrops,
        int Closes,
        int Recloses);

    private sealed record PinchRun(IReadOnlyList<Position2> Positions,
        IReadOnlyList<MovementDiagnosticSnapshot> Snapshots, Entity Entity,
        bool RouteChanged, bool MergedGoal,
        int? BypassTick,
        IReadOnlyList<(int Tick, uint NetId, IReadOnlyList<Position2> Points)> WaypointOrders);

    private sealed record Exhibit(string Name, Position2 Position,
        IReadOnlyList<Position2> Waypoints, IReadOnlyList<Position2> Planted);

    private static readonly Exhibit Reachable = new("reachable", 
        new Position2(12572.94, 2392.59),
        [new Position2(12594.8, 2407.2), new Position2(13122.7, 3473.9)],
        [new Position2(12556.158, 2451.222), new Position2(12620.484, 2435.118)]);

    private static readonly Exhibit MidLeg = new("mid-leg",
        new Position2(12620.23, 2360.29),
        [new Position2(11887.8, 1692.2), new Position2(11025.5, 1252.0),
         new Position2(10099.0, 1090.8)],
        [new Position2(12521.59, 2357.79), new Position2(12586.76, 2465.59)]);

    private static readonly Exhibit FirstWaveClear = new("first-wave-clear",
        new Position2(12619.45, 2579.13),
        [new Position2(12619.0, 2581.0), new Position2(12429.0, 2423.0),
         new Position2(11887.0, 1693.0)],
        [new Position2(12515.0, 2537.0), new Position2(12613.0, 2489.0),
         new Position2(12703.0, 2511.0)]);

    private static readonly Exhibit TerminalQueue = new("terminal",
        new Position2(1135.0, 4731.0), [new Position2(1077.0, 4485.0)],
        [new Position2(1143.0, 4645.0), new Position2(1077.0, 4663.0)]);

    private sealed class SnapshotSink : IMovementDiagnosticSink
    {
        public List<MovementDiagnosticSnapshot> Snapshots { get; } = [];
        public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
            Snapshots.AddRange(snapshots.Where(snapshot => snapshot.NetId == 1000 ||
                                                            snapshot.NetId == 2000));
    }

    private sealed class PassiveCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Protected;
        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class AlwaysVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class RecordingPackets : IMinionPacketSink
    {
        public int CurrentTick { get; set; }
        public List<(int Tick, uint NetId, IReadOnlyList<Position2> Points)> WaypointOrders { get; } = [];
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints,
            long syncId, string reason) => WaypointOrders.Add((CurrentTick, minion.NetId,
                waypoints.ToArray()));
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }
}
