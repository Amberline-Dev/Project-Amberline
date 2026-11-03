using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Regression ports for the movement scenarios that first exposed wave-body
/// escape failures.  These checks deliberately exercise the production
/// movement route rather than a second, test-only path solver.
/// </summary>
public static partial class MinionChecks
{
    [Check("minions-b6-help-priority-matrix")]
    public static void HelpPriorityMatrix()
    {
        // Period 12 Nov 2011 ladder: missing slots 2, 3 and 5 (one-based).
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var pair in new[] { (UnitType.Minion, UnitType.Hero),
                     (UnitType.Minion, UnitType.Minion), (UnitType.Hero, UnitType.Minion) })
        foreach (var control in new[] { "lower", "equal", "higher", "higher-before-consume" })
        {
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                orderChampion: "Annie", chaosChampion: "Annie", solo: true,
                originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0.0);
            var world = runtime.World;
            var enemy = MinionDefinitions.Opposing(team);
            Entity Unit(UnitType type, Team unitTeam, double x) => world.Register(new Entity(
                world.Allocate(type), type == UnitType.Hero ? EntityKind.Hero : EntityKind.Minion,
                type, unitTeam, new Position2(x, 7000))
            {
                Health = 1000, MaxHealth = 1000, AttackRange = 200, AcquisitionRange = 750,
                Radius = 35, Speed = 0, MinionType = type == UnitType.Minion ? MinionType.Melee : null,
                Created = true, Visible = true,
            });
            var responder = Unit(UnitType.Minion, team, 7000);
            var victim = Unit(pair.Item2, team, 7010);
            var heroAlly = Unit(UnitType.Hero, team, 7020);
            var aggressor = Unit(pair.Item1, enemy, 7040);
            var current = Unit(control == "equal" ? pair.Item1 : UnitType.Hero, enemy, 7030);
            if (control == "equal") current.AttackTarget = victim.NetId;
            if (control == "higher") current.AttackTarget = heroAlly.NetId;
            responder.AttackTarget = current.NetId;
            responder.MinionState = MinionAiState.Engage;
            // A damage event, not the aggressor's possibly unrelated order, sets its priority.
            aggressor.AttackTarget = null;
            runtime.Combat.ApplyHit(aggressor.NetId, victim.NetId, 1, DamageType.True, DamageSourceKind.Spell);
            TestAssert.True(responder.HelpTarget is null, "single-target spells emit no lane help");
            TestAssert.Equal(0.0, victim.LaneHelpCallRemaining, "excluded spells do not spend the cooldown");
            runtime.Combat.ApplyHit(aggressor.NetId, victim.NetId, 1, DamageType.True, DamageSourceKind.Basic);
            var nominated = control is "lower" or "higher-before-consume";
            TestAssert.Equal(nominated ? (uint?)aggressor.NetId : null, responder.HelpTarget,
                $"{team}/{pair}/{control}: nomination preserves equal/higher targets");
            TestAssert.Equal(1.0, victim.LaneHelpCallRemaining, "cfh_Delay is one second");
            if (control == "higher-before-consume") current.AttackTarget = heroAlly.NetId;
            runtime.Minions!.Tick(.01);
            TestAssert.Equal((uint?)(control == "lower" ? aggressor.NetId : current.NetId),
                responder.AttackTarget, $"{team}/{pair}/{control}: consumption rechecks priority");
            TestAssert.True(responder.HelpTarget is null, "each event is consumed once");
            runtime.Combat.ApplyHit(aggressor.NetId, victim.NetId, 1, DamageType.True, DamageSourceKind.Basic);
            TestAssert.True(responder.HelpTarget is null, "the same victim cannot call again within one second");
        }
    }

    [Check("minions-inhibitor-overrides-all-wave-modes")]
    public static void InhibitorOverrideFollowsNormalAndPopulationComposition()
    {
        // Map1's shipped GetMinionSpawnInfo (LevelScript.txt:1819-1915) owns this decision now:
        // the population modes come from MAX_MINIONS_EVER - GetTotalTeamMinionsSpawned(), and the
        // inhibitor override from the barracks' WillSpawnSuperMinion flag (set at
        // LevelScript.txt:2405/2546 by ApplyBarracksDestructionBonuses). One host per case —
        // LevelScript.txt:1819-1821 computes the mode only on the first ask of a wave (LAST_WAVE).
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var downMask in new[] { 0, 1, 7 })
        foreach (var lane in MapDefinitions.Lanes)
        foreach (var remaining in new[] { 180, 43, 42, 18, 17, 6, 5, 0 })
        {
            var world = InhibitorWorld(downMask, team);
            var affected = downMask == 7 || downMask == 1 && lane == MapDefinitions.Lanes[0];
            var expectedSupers = affected ? (downMask == 7 ? 2 : 1) : remaining is >= 6 and <= 17 ? 2 : 0;
            var expected = Enumerable.Repeat(MinionType.Melee, remaining >= 43 ? 3 : 0)
                .Concat(Enumerable.Repeat(MinionType.Caster, remaining >= 18 ? 3 : 0))
                .Concat(Enumerable.Repeat(MinionType.Siege, remaining >= 43 && !affected ? 1 : 0))
                .Concat(Enumerable.Repeat(MinionType.Super, expectedSupers)).ToArray();
            TestAssert.Equal(expected,
                LevelScriptFixtures.Wave(world, team, lane, 2, teamMinions: 180 - remaining).ToArray(),
                $"{team} lane={lane} down={downMask} remaining={remaining}");
        }
    }

    [Check("minions.b4-retail-acquisition-and-turret-help")]
    public static void RetailAcquisitionAndTurretHelp()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in Enum.GetValues<MinionType>())
        {
            var data = GameData.Default.Champion(MinionDefinitions.CharacterName(type, team));
            var expected = type switch { MinionType.Caster => 700.0, MinionType.Super => 600.0, _ => 750.0 };
            TestAssert.Near(expected, data.AcquisitionRange, 1e-9, $"{team} {type} CharacterData acquisition");
            TestAssert.Near(expected, MinionDefinitions.BaseStats(type, team).AcquisitionRange, 1e-9);
            TestAssert.Equal(type is MinionType.Caster or MinionType.Super,
                data.Raw.TryGetValue(InibinReader.KeyHash("Data*AcquisitionRange"), out _),
                "melee/cannon use the recovered loader default; caster/super explicitly override it");
        }

        foreach (var type in new[] { MinionType.Melee, MinionType.Siege })
        foreach (var offset in new[] { 636.74, 564.56 })
        {
            var grid = NavigationGrid.LoadDefault();
            var world = new WorldState();
            var packets = new FixturePackets();
            var scheduler = new WaveScheduler(world, grid, packets, LevelScriptFixtures.Init(world));
            var minion = scheduler.SpawnFixture(Team.Order, type, 0, "L", 0.0, 0.0);
            var turret = Add(world, 0x40000053, EntityKind.Turret, UnitType.Turret, Team.Chaos,
                MapDefinitions.TurretPositions["Turret_T2_L_03"], 0.0, MapDefinitions.TurretRadius);
            turret.Name = "Turret_T2_L_03";
            var ai = new MinionAiSystem(world, grid, new MovementSystem(world, grid),
                new FixtureCombat(), packets, new AlwaysVision());
            // Sweep the captured lateral miss distances across the turret's
            // approach plane; even the pre-pass samples must acquire it.
            foreach (var along in new[] { -200.0, -100.0, 0.0, 100.0, 200.0 })
            {
                minion.Position = turret.Position + new Position2(along, -offset);
                TestAssert.Equal(turret.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                    $"{type} at captured offset {offset} / {along} must not walk past the tower");
            }
            var nearest = AddMinion(world, 0x40060051, minion.Position + new Position2(50, 0));
            nearest.Team = Team.Chaos;
            TestAssert.Equal(nearest.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "an idle turret has no unconditional priority");
            turret.AttackTarget = minion.NetId;
            TestAssert.Equal(turret.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "a turret attacking an allied minion outranks ordinary nearest targets");
            nearest.AttackTarget = minion.NetId;
            TestAssert.Equal(nearest.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "minion-on-minion help also outranks turret help");
            var ally = Add(world, 0x40060052, EntityKind.Hero, UnitType.Hero,
                Team.Order, minion.Position, 0.0, 65.0);
            nearest.AttackTarget = ally.NetId;
            TestAssert.Equal(nearest.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "champion help retains priority over turret help");
            var enemyHero = Add(world, 0x40060053, EntityKind.Hero, UnitType.Hero,
                Team.Chaos, minion.Position + new Position2(60, 0), 0.0, 65.0);
            enemyHero.AttackTarget = ally.NetId;
            TestAssert.Equal(enemyHero.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "champion attacking champion is first, before minion attacking champion");
            enemyHero.AttackTarget = minion.NetId;
            TestAssert.Equal(nearest.NetId, ai.SelectTarget(minion)?.NetId ?? 0);
            nearest.AttackTarget = minion.NetId;
            TestAssert.Equal(nearest.NetId, ai.SelectTarget(minion)?.NetId ?? 0);
            nearest.AttackTarget = null;
            TestAssert.Equal(turret.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "turret attacking minion precedes champion attacking minion");
            turret.AttackTarget = null;
            TestAssert.Equal(enemyHero.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "champion attacking minion precedes the closest idle minion");
            enemyHero.AttackTarget = null;
            TestAssert.Equal(nearest.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "closest idle minion precedes closest idle champion");
            nearest.Protected = true;
            TestAssert.Equal(enemyHero.NetId, ai.SelectTarget(minion)?.NetId ?? 0,
                "closest idle champion precedes an idle turret");
            nearest.Protected = false;
            nearest.AttackTarget = ally.NetId;
            turret.AttackTarget = minion.NetId;

            using var runtime = MatchRuntime.CreateProduction(grid, GameData.Default,
                orderChampion: "Annie", chaosChampion: "Annie", solo: true,
                originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0.0);
            runtime.World.Register(minion);
            runtime.World.Register(turret);
            runtime.World.Register(nearest);
            runtime.World.Register(ally);
            runtime.World.Register(enemyHero);
            minion.AttackTarget = nearest.NetId;
            minion.MinionState = MinionAiState.Engage; // seed a running AI, not the new OnInit IDLE state
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: DamageSourceKind.Basic);
            TestAssert.True(minion.HelpTarget is null, "turret help cannot replace champion help");
            nearest.AttackTarget = null;
            minion.LaneHelpCallRemaining = 0.0; // a new event after the one-second victim cooldown
            // playtest B4 #3 review, 27 Sept 2026: single-target spells only (2011 wiki).
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: DamageSourceKind.Spell);
            TestAssert.True(minion.HelpTarget is null, "spell damage does not trigger lane help");
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: DamageSourceKind.Basic);
            TestAssert.Equal(turret.NetId, minion.HelpTarget ?? 0,
                "basic turret damage emits the conditional lane help event");
            enemyHero.AttackTarget = minion.NetId;
            runtime.Combat.ApplyHit(enemyHero.NetId, ally.NetId, 1.0, sourceKind: DamageSourceKind.SpellAoe);
            TestAssert.Equal(enemyHero.NetId, minion.HelpTarget ?? 0,
                "champion damage to a champion nominates first-priority help even with a different attack order");
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: DamageSourceKind.Basic);
            TestAssert.Equal(enemyHero.NetId, minion.HelpTarget ?? 0,
                "queued champion-damage help outranks turret help");
            ai.Tick(.01);
            TestAssert.Equal(enemyHero.NetId, minion.AttackTarget ?? 0);
            // LMIN-02, 27 Sept 2026 base-game sweep: the previous immediate
            // turret replacement expectation ignored the accepted champion-help stick.
            minion.LaneHelpCallRemaining = 0.0;
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: DamageSourceKind.Basic);
            TestAssert.True(minion.HelpTarget is null, "accepted champion help keeps its priority for 1.5 seconds");
            foreach (var kind in new[] { DamageSourceKind.SpellAoe, DamageSourceKind.SpellPersist,
                         DamageSourceKind.Proc })
            {
                minion.HelpStickRemaining = 0.0;
                minion.LaneHelpCallRemaining = 0.0;
                enemyHero.AttackTarget = minion.NetId;
                minion.AttackTarget = enemyHero.NetId;
                runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: kind);
                TestAssert.Equal(turret.NetId, minion.HelpTarget ?? 0,
                    $"{kind}: turret help replaces a current champion-on-minion target");
                ai.Tick(.01);
                TestAssert.Equal(turret.NetId, minion.AttackTarget ?? 0,
                    "consumption preserves the turret's fourth priority slot");
                TestAssert.True(minion.HelpTarget is null, "turret help is consumed once");
            }
            minion.HelpStickRemaining = 0.0;
            minion.LaneHelpCallRemaining = 0.0;
            minion.AttackTarget = enemyHero.NetId;
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1.0, sourceKind: DamageSourceKind.Basic);
            TestAssert.Equal(turret.NetId, minion.HelpTarget ?? 0);
            enemyHero.AttackTarget = ally.NetId;
            ai.Tick(.01);
            TestAssert.Equal(enemyHero.NetId, minion.AttackTarget ?? 0,
                "a new champion-on-champion priority before consumption defeats queued turret help");
            TestAssert.True(minion.HelpTarget is null, "rejected turret help is consumed too");
        }
    }

    [Check("minions-fear-retains-forced-route")]
    public static void FearOwnsMovementDuringAiTick()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var minion = AddMinion(world, 0x40060001, new Position2(7000.0, 7000.0));
        minion.AnchorPath = [new Position2(6500.0, 7000.0), minion.Position];
        var fearMove = new ForcedMoveState(new Position2(7300.0, 7000.0), 325.0, .75,
            0x40060002, "Fear", firstCollision: true);
        minion.ForcedMove = fearMove;
        minion.Buffs.Add(new BuffState("Fear", new BuffPayload
        {
            ActionDisabled = true,
            FearOrigin = minion.Position,
        }, BuffType.Fear, expiresAt: .5, casterNetId: 0x40060002));
        var packets = new FixturePackets();
        var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            packets, new WorldMinionVisionService(world, grid,
                _ => Array.Empty<VisionProvider>()));
        ai.Tick(.03);
        TestAssert.Equal(0, packets.Waypoints,
            "lane AI must not publish a route while Fear owns movement");
        TestAssert.True(ReferenceEquals(fearMove, minion.ForcedMove),
            "Fear forced movement remains authoritative for the frame");

        movement.Tick(.75);
        TestAssert.Equal(1, minion.AnchorPath.Count,
            "completed Shaco Fear owns a fresh singleton prediction anchor");
        TestAssert.Equal(movement.ClientAnchor(minion.Position), minion.AnchorPath[0],
            "completed Shaco Fear reanchors at the actual client-grid landing");
    }

    [Check("minions-fear-removal-reanchors-production-cache")]
    public static void FearRemovalReanchorsProductionClientCache()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, orderChampion: "Annie", chaosChampion: "Annie",
            solo: true, originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, minionFidelity: true);
        var world = runtime.World;
        var caster = new Entity(0x40060002, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7000.0, 7000.0))
        {
            ChampionName = "Shaco", Alive = true, Created = true, Visible = true,
            Health = 1000.0, MaxHealth = 1000.0,
        };
        world.Register(caster);
        var minion = AddMinion(world, 0x40060003, new Position2(7000.0, 7000.0));
        minion.AnchorPath = [new Position2(6500.0, 7000.0), minion.Position];
        minion.ForcedMove = new ForcedMoveState(new Position2(7300.0, 7000.0),
            325.0, .75, caster.NetId, "Fear", firstCollision: true);
        var fear = runtime.Buffs.AddBuff(minion, "Fear", BuffType.Fear, .75,
            caster.NetId, new BuffPayload { ActionDisabled = true,
                FearOrigin = minion.Position });
        TestAssert.True(fear is not null);
        minion.Waypoints.Clear();

        // Removal occurs between two movement steps, the captured production ordering.
        runtime.Movement.Tick(MovementSystem.FixedTickSeconds);
        runtime.Buffs.RemoveBuff(minion, fear!.Slot);
        runtime.Movement.Tick(MovementSystem.FixedTickSeconds);

        TestAssert.Equal(1, minion.AnchorPath.Count,
            "Fear removal must replace the old singleton/route anchor");
        TestAssert.Equal(runtime.Movement.ClientAnchor(minion.Position), minion.AnchorPath[0],
            "the lifecycle handoff must use the production client-grid anchor");
    }

    [Check("minions-siege-and-vision-regressions")]
    public static void SiegeOrdersAndBrushProviders()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var packets = new FixturePackets();
        var vision = new WorldMinionVisionService(world, grid,
            _ => Array.Empty<VisionProvider>());
        var minion = new Entity(0x40010001, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(7000.0, 7000.0))
        {
            MinionType = MinionType.Melee,
            Alive = true,
            Created = true,
            Speed = 325.0,
            Radius = 35.75,
            AttackRange = 170.0,
            AcquisitionRange = 450.0,
            Health = 500.0,
            MaxHealth = 500.0,
        };
        var nearbyHero = new Entity(0x40000021, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7100.0, 7000.0))
        {
            Alive = true,
            Created = true,
            Radius = 65.0,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        var farTurret = new Entity(0x40000041, EntityKind.Turret, UnitType.Turret,
            Team.Chaos, new Position2(9000.0, 7000.0))
        {
            Alive = true,
            Created = true,
            Radius = 88.0,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        world.Register(minion);
        world.Register(nearbyHero);
        world.Register(farTurret);
        var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(), packets, vision);
        // minionLib 0.0's OnInit arms TimerFindEnemies on DELAY_FIND_ENEMIES, so Minion.lua 0.2's
        // ordinary FindTargetInAcR pass runs on that 0.25 s grid rather than the first frame.
        for (var tick = 0; tick < 30 && minion.AttackTarget is null; tick++) ai.Tick(1.0 / 30.0);
        TestAssert.Equal(nearbyHero.NetId, minion.AttackTarget ?? 0,
            "an exhausted route must run ordinary acquisition before siege selection");

        // Map1 brush components are intentionally narrower than the hero
        // vision radius.  Use a small all-grass fixture here so the 1200 vs
        // 1350 range boundary is tested independently of Map1 brush shape.
        var visionGrid = CreateSyntheticBrushGrid();
        var targetPosition = new Position2(500.0, 500.0);
        var providerPosition = new Position2(1701.0, 500.0);
        var visionWorld = new WorldState();
        var target = new Entity(0x40000031, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, targetPosition) { Alive = true, Radius = 65.0 };
        var hero = new Entity(0x40000032, EntityKind.Hero, UnitType.Hero,
            Team.Order, providerPosition) { Alive = true, Radius = 65.0 };
        visionWorld.Register(target);
        visionWorld.Register(hero);
        var worldVision = new WorldMinionVisionService(visionWorld, visionGrid,
            _ => Array.Empty<VisionProvider>());
        TestAssert.True(worldVision.HasBrushVision(Team.Order, target),
            "hero brush vision must retain the 1350-unit provider range");

        hero.Alive = false;
        var visionMinion = new Entity(0x40010032, EntityKind.Minion, UnitType.Minion,
            Team.Order, providerPosition) { Alive = true, Radius = 35.0 };
        visionWorld.Register(visionMinion);
        var minionVision = new WorldMinionVisionService(visionWorld, visionGrid,
            _ => Array.Empty<VisionProvider>());
        TestAssert.False(minionVision.HasBrushVision(Team.Order, target),
            "ordinary minions cannot reveal beyond 1200 units");
        visionMinion.Position = new Position2(1700.0, 500.0);
        TestAssert.True(minionVision.HasBrushVision(Team.Order, target),
            "ordinary minions reveal at the shipped 1200-unit boundary");
        visionMinion.Position = providerPosition;
        visionMinion.MinionType = MinionType.Super;
        TestAssert.True(minionVision.HasBrushVision(Team.Order, target),
            "super minions inherit the 1350-unit character default");

        var itemVision = new WorldMinionVisionService(visionWorld, visionGrid,
            _ => new[] { new VisionProvider(providerPosition, 1201.0) });
        visionMinion.Alive = false;
        TestAssert.True(itemVision.HasBrushVision(Team.Order, target),
            "item vision providers must participate in minion brush checks");

        foreach (var kind in new[] { EntityKind.Pet, EntityKind.YorickGhoul,
                     EntityKind.ShacoClone, EntityKind.YorickRevenant, EntityKind.ShacoBox })
        {
            var pet = new Entity(visionWorld.Allocate(UnitType.Monster), kind, UnitType.Monster,
                Team.Order, kind == EntityKind.ShacoBox ? targetPosition + new Position2(690, 0) : providerPosition)
            {
                OwnerNetId = hero.NetId,
            };
            visionWorld.Register(pet);
            TestAssert.True(worldVision.HasBrushVision(Team.Order, target),
                kind + " shares brush sight with minions, including after its owner dies");
            pet.Alive = false;
            TestAssert.False(worldVision.HasBrushVision(Team.Order, target),
                "dead pets cannot retain brush vision");
        }
    }

    [Check("minions-siege-retry")]
    public static void SiegeStructureRecoveryAndBoundedRetry()
    {
        // playtest B4 / retail sweep, 27 Sept 2026: siege follows named lane
        // objectives and validates firing points instead of routing to centres.
        // A dead lane inhibitor must not leave a previously selected tower
        // cached. Both team orderings must continue toward the next live
        // turret in the source structure chain.
        foreach (var (team, enemy, offset) in new[]
        {
            (Team.Order, Team.Chaos, 0.0),
            (Team.Chaos, Team.Order, 5000.0),
        })
        {
            var grid = NavigationGrid.LoadDefault();
            var world = new WorldState();
            var movement = new MovementSystem(world, grid);
            var events = new List<AiEvent>();
            grid.FindPathOverride = (start, destination) => new[] { start, destination };
            var minion = Add(world, 0x40010000u + (uint)offset,
                EntityKind.Minion, UnitType.Minion, team,
                new Position2(7000.0, 7000.0), 325.0, 35.75);
            minion.MinionType = MinionType.Melee;
            minion.MinionLane = "C";
            minion.AttackRange = 110.0;
            minion.Created = true;
            minion.Alive = true;
            AddStructure(world, 0x40020000u + (uint)offset, enemy,
                EntityKind.Inhibitor, UnitType.Structure,
                new Position2(7500.0 + offset, 6500.0), 175.0,
                alive: false);
            var firstTurret = AddStructure(world, 0x40020001u + (uint)offset, enemy,
                EntityKind.Turret, UnitType.Turret,
                new Position2(7600.0, 7000.0), 88.0);
            AddStructure(world, 0x40020002u + (uint)offset, enemy,
                EntityKind.Inhibitor, UnitType.Structure,
                new Position2(9100.0 + offset, 6500.0), 175.0,
                alive: false);
            var nextTurret = AddStructure(world, 0x40020003u + (uint)offset, enemy,
                EntityKind.Turret, UnitType.Turret,
                new Position2(8000.0, 7000.0), 88.0);
            AddStructure(world, 0x40020004u + (uint)offset, enemy,
                EntityKind.Nexus, UnitType.Structure,
                new Position2(11500.0 + offset, 7000.0), 175.0,
                protectedUnit: true);

            firstTurret.Name = $"Turret_T{(enemy == Team.Order ? 1 : 2)}_C_05";
            nextTurret.Name = $"Turret_T{(enemy == Team.Order ? 1 : 2)}_C_04";
            var ai = new MinionAiSystem(world, grid, movement,
                new FixtureCombat(), new RecordingPackets(events),
                new WorldMinionVisionService(world, grid, _ => Array.Empty<VisionProvider>()));
            ai.Tick(0.03);
            TestAssert.True(minion.Waypoints.Count > 0,
                "the first live siege turret must receive a movement route");
            TestAssert.True(Distance(minion.Waypoints[^1], firstTurret.Position) <= CombatService.AttackReach(minion, firstTurret),
                "the first route must end within reach of the live outer turret");

            firstTurret.Alive = false;
            minion.AttackTarget = null;
            minion.Waypoints.Clear();
            minion.Target = null;
            minion.MinionState = MinionAiState.March;
            ai.Tick(0.25);
            TestAssert.True(minion.Waypoints.Count > 0,
                "a dead cached siege target must trigger a replacement route");
            TestAssert.True(Distance(minion.Waypoints[^1], nextTurret.Position) <= CombatService.AttackReach(minion, nextTurret),
                "the replacement must end within reach of the next lane turret");
        }

        // A failed firing-angle search gets one retry after the
        // source one-second window. It must never emit a straight-line
        // fallback or poll the blocked structure at target-scan cadence.
        var retryGrid = NavigationGrid.LoadDefault();
        var retryWorld = new WorldState();
        var retryMovement = new MovementSystem(retryWorld, retryGrid);
        var retryEvents = new List<AiEvent>();
        var retryMinion = Add(retryWorld, 0x40030001, EntityKind.Minion,
            UnitType.Minion, Team.Order, new Position2(7000.0, 7000.0),
            325.0, 35.75);
        retryMinion.MinionType = MinionType.Melee;
        retryMinion.MinionLane = "C";
        retryMinion.AttackRange = 110.0;
        retryMinion.Created = true;
        retryMinion.Alive = true;
        AddStructure(retryWorld, 0x40030002, Team.Chaos,
            EntityKind.Turret, UnitType.Turret, new Position2(7600.0, 7000.0), 88.0).Name = "Turret_T2_C_05";
        var pathCalls = new List<(double Elapsed, Position2 Destination)>();
        var reachable = false;
        MinionAiSystem retryAi = null!;
        retryGrid.FindPathOverride = (start, destination) =>
        {
            pathCalls.Add((retryAi.ElapsedSeconds, destination));
            return reachable
                ? new[] { start, destination }
                : Array.Empty<Position2>();
        };
        retryAi = new MinionAiSystem(retryWorld, retryGrid, retryMovement,
            new FixtureCombat(), new RecordingPackets(retryEvents),
            new WorldMinionVisionService(retryWorld, retryGrid,
                _ => Array.Empty<VisionProvider>()));

        retryAi.Tick(0.03);
        TestAssert.Equal(16, pathCalls.Count,
            "a blocked siege scan must try the bounded 16 firing angles");
        TestAssert.Equal(0, retryEvents.Count,
            "a failed siege route must not emit a straight-line fallback");
        TestAssert.True(retryMinion.Target is null && retryMinion.Waypoints.Count == 0,
            "a failed siege route must leave no phasing order");

        for (var index = 0; index < 29; index++)
        {
            retryAi.Tick(0.03);
        }
        TestAssert.Equal(16, pathCalls.Count,
            "a failed siege route must not retry at every target scan");

        reachable = true;
        for (var index = 0; index < 10; index++)
        {
            retryAi.Tick(0.03);
        }
        TestAssert.Equal(17, pathCalls.Count,
            "the siege route should retry once after one second");
        TestAssert.Equal(1, retryEvents.Count,
            "a successful bounded retry must emit one real route");
        TestAssert.True(pathCalls[^1].Elapsed - pathCalls[0].Elapsed >= 0.99,
            "siege retry fired before the one-second source window");
        TestAssert.True(retryMinion.Waypoints.Count >= 2 && retryMinion.Target is null,
            "a successful siege retry must retain its route");

        retryMinion.Waypoints.Clear();
        retryMinion.Target = null;
        var before = pathCalls.Count;
        for (var index = 0; index < 29; index++)
        {
            retryAi.Tick(0.03);
        }
        TestAssert.Equal(before, pathCalls.Count,
            "route exhaustion must respect the second retry window");
        for (var index = 0; index < 10; index++)
        {
            retryAi.Tick(0.03);
        }
        TestAssert.Equal(before + 1, pathCalls.Count,
            "route exhaustion must receive one bounded reissue");
        var realRoutes = retryEvents.Where(item => item.Kind == "waypoints" &&
            item.Points is { Count: >= 2 }).ToArray();
        TestAssert.Equal(2, realRoutes.Length,
            "same-target siege must emit exactly two real routes");
        TestAssert.True(retryEvents.Count < 5,
            $"same-target siege emitted a packet stream: {retryEvents.Count}");
    }

    [Check("minions-late-wave-overlap", Serial = true)]
    public static void PlantedOverlapCancelsAttackBeforeSeparation()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var events = new List<AiEvent>();
        var target = Add(world, 0x40040001, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7300.0, 7000.0), 0.0, 65.0);
        target.Alive = true;
        target.Created = true;
        var mover = AddMinion(world, 0x40040002, new Position2(7150.0, 7000.0));
        var anchor = AddMinion(world, 0x40040003, new Position2(7150.0, 7000.0));
        mover.AttackTarget = target.NetId;
        anchor.AttackTarget = target.NetId;
        mover.MinionState = MinionAiState.Engage;
        anchor.MinionState = MinionAiState.Engage;
        mover.AnchorPath = [mover.Position];
        anchor.AnchorPath = [anchor.Position];
        // A planted attacker has attacked, so its client is in the attack state and the stop-attack is due.
        mover.ClientAttackArmed = true;

        var ai = new MinionAiSystem(world, grid, movement,
            new FixtureCombat(), new RecordingPackets(events),
            new WorldMinionVisionService(world, grid, _ => Array.Empty<VisionProvider>()));
        ai.Tick(0.03);

        var own = events.Where(item => item.NetId == mover.NetId).ToArray();
        TestAssert.True(own.Length >= 2,
            "a planted overlapping attacker must receive a separating route");
        TestAssert.Equal("stop-attack", own[0].Kind,
            "overlap separation must cancel the old attack first");
        TestAssert.Equal("waypoints", own[1].Kind,
            "overlap separation must send waypoints after attack cancellation");
        TestAssert.True(own[1].Points is { Count: >= 2 },
            "overlap separation route must contain a live movement leg");

        RunProductionLateWaveSoak();
        RunFocusedLateWaveCases(grid);
    }

    [Check("minions-wave-live-budget-and-reset")]
    public static void WaveLiveBudgetBoundariesAndLifecycle()
    {
        // Exercise every shipped late-wave boundary with a real scheduler and
        // one coherent current-live count for both teams and all lanes.
        foreach (var remaining in new[] { 43, 42, 18, 17, 6, 5 })
        {
            var (_, scheduler) = CreateWaveBudgetFixture(remaining);
            scheduler.Start(0.0, MapDefinitions.Lanes);
            var spawned = scheduler.Tick(10.0);
            AssertWaveTypes(spawned, Team.Order, remaining,
                $"remaining={remaining} order");
            AssertWaveTypes(spawned, Team.Chaos, remaining,
                $"remaining={remaining} chaos");
            TestAssert.Equal(0, scheduler.Pending.Count,
                $"remaining={remaining} drains every staggered member");
        }

        // The <=5 branch uses the actual inhibitor state. One down Chaos
        // inhibitor gives Order one super in the affected lane while Chaos
        // falls back to no supers; this also proves the two team snapshots
        // are independent.
        var inhibitorWorld = InhibitorWorld(1, Team.Order);
        AddLiveBudget(inhibitorWorld, Team.Order, 175);
        AddLiveBudget(inhibitorWorld, Team.Chaos, 175);
        // The lethal edge is what tells the script — CombatService raises BarracksChanged, and
        // ApplyBarracksDestructionBonuses (LevelScript.txt:2405/2546) sets WillSpawnSuperMinion
        // on the beneficiary's lane table.
        var inhibitorLevel = LevelScriptFixtures.Init(inhibitorWorld);
        LevelScriptFixtures.FellInhibitors(inhibitorLevel, inhibitorWorld, Team.Order);
        var inhibitorScheduler = new WaveScheduler(inhibitorWorld,
            NavigationGrid.LoadDefault(), new FixturePackets(), inhibitorLevel, firstSpawnSeconds: 0.0);
        inhibitorScheduler.Start(0.0, MapDefinitions.Lanes);
        var inhibitorSpawned = inhibitorScheduler.Tick(10.0);
        var affectedLane = MapDefinitions.Lanes.Single(lane =>
            MinionDefinitions.IsInhibitorDown(Team.Chaos, lane, inhibitorWorld));
        AssertWaveTypes(inhibitorSpawned, Team.Order, 5, "inhibitor fallback order",
            expectInhibitorSuper: true, inhibitorSuperLane: affectedLane);
        AssertWaveTypes(inhibitorSpawned, Team.Chaos, 5, "inhibitor fallback chaos");
        TestAssert.Equal(1,
            inhibitorSpawned.Count(entity => entity.Team == Team.Order &&
                entity.MinionType == MinionType.Super),
            "one down enemy inhibitor grants one Order super in its lane");
        TestAssert.Equal(0,
            inhibitorSpawned.Count(entity => entity.Team == Team.Chaos),
            "the team without a down enemy inhibitor has no fallback supers");

        // A stall of several intervals does not replay the missed waves: the stock barracks update fires once per
        // update and re-anchors its cadence to the trigger time (the client's barracks cadence code; A2 audit 18 October
        // 2026). Only one wave is composed, stamped with the time of the tick that fired it.
        var (_, catchupScheduler) = CreateWaveBudgetFixture(42);
        catchupScheduler.Start(0.0, MapDefinitions.Lanes);
        var catchup = catchupScheduler.Tick(92.0).Concat(catchupScheduler.Tick(94.0)).ToArray();
        TestAssert.True(catchup.Length > 0, "the overdue wave spawns");
        TestAssert.True(catchup.All(entity => entity.MinionType == MinionType.Caster &&
                entity.SpawnScheduledAt >= 92.0 - 1e-9),
            "a stall spawns one caster-only wave stamped at the tick, not four replayed waves");
        TestAssert.Equal(0, catchupScheduler.Pending.Count,
            "the single overdue wave drains after its stagger");
        TestAssert.Equal(0, catchupScheduler.Tick(92.0 + MinionDefinitions.WaveIntervalSeconds - 1.0).Count,
            "the next wave is due a whole interval after the re-anchored trigger");

        // Death lowers the current-live count before the next wave. Mark one
        // pre-existing member dead after the first wave; the next composition
        // therefore returns to the normal six-member lane wave. A fresh world
        // has no inherited count and starts on that same normal composition.
        var (lifecycleWorld, lifecycleScheduler) = CreateWaveBudgetFixture(42);
        lifecycleScheduler.Start(0.0, MapDefinitions.Lanes);
        var first = lifecycleScheduler.Tick(10.0);
        foreach (var entity in first) entity.Alive = false;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var inherited = lifecycleWorld.Entities.Values.First(entity =>
                entity.Team == team && entity.Kind == EntityKind.Minion &&
                entity.Alive);
            inherited.Alive = false;
        }
        // Drain the entire staggered wave; at exactly 30 only slot zero is due.
        var next = lifecycleScheduler.Tick(40.0);
        AssertWaveTypes(next, Team.Order, 43, "after death order");
        AssertWaveTypes(next, Team.Chaos, 43, "after death chaos");

        var resetWorld = new WorldState();
        var resetScheduler = new WaveScheduler(resetWorld,
            NavigationGrid.LoadDefault(), new FixturePackets(), LevelScriptFixtures.Init(resetWorld),
            firstSpawnSeconds: 0.0);
        resetScheduler.Start(0.0, MapDefinitions.Lanes);
        var reset = resetScheduler.Tick(10.0);
        AssertWaveTypes(reset, Team.Order, 180, "fresh world order");
        AssertWaveTypes(reset, Team.Chaos, 180, "fresh world chaos");
    }

    private static (WorldState World, WaveScheduler Scheduler) CreateWaveBudgetFixture(
        int remaining)
    {
        if (remaining is < 0 or > MinionDefinitions.MaxMinionsEver)
            throw new ArgumentOutOfRangeException(nameof(remaining));
        var world = new WorldState();
        var live = MinionDefinitions.MaxMinionsEver - remaining;
        AddLiveBudget(world, Team.Order, live);
        AddLiveBudget(world, Team.Chaos, live);
        return (world, new WaveScheduler(world, NavigationGrid.LoadDefault(),
            new FixturePackets(), LevelScriptFixtures.Init(world), firstSpawnSeconds: 0.0));
    }

    private static void AddLiveBudget(WorldState world, Team team, int count)
    {
        var baseId = team == Team.Order ? 0x60000000u : 0x61000000u;
        for (var index = 0; index < count; index++)
        {
            var entity = new Entity(baseId + (uint)index, EntityKind.Minion,
                UnitType.Minion, team, Position2.Zero)
            {
                Name = $"budget-{team}-{index}",
                MinionType = MinionType.Melee,
                MinionLane = MapDefinitions.Lanes[index % MapDefinitions.Lanes.Count],
                Alive = true,
                Created = true,
                Visible = true,
                Radius = 35.7437,
                Health = 1000.0,
                MaxHealth = 1000.0,
            };
            world.Register(entity);
        }
    }

    private static void AssertWaveTypes(IReadOnlyList<Entity> spawned, Team team,
        int remaining, string label, bool expectInhibitorSuper = false,
        string? inhibitorSuperLane = null)
    {
        var expected = remaining > 42
            ? new[] { MinionType.Melee, MinionType.Melee, MinionType.Melee,
                MinionType.Caster, MinionType.Caster, MinionType.Caster }
            : remaining >= 18
                ? new[] { MinionType.Caster, MinionType.Caster, MinionType.Caster }
                : remaining >= 6
                    ? new[] { MinionType.Super, MinionType.Super }
                    : Array.Empty<MinionType>();
        foreach (var lane in MapDefinitions.Lanes)
        {
            var laneExpected = remaining <= 5 && expectInhibitorSuper &&
                (inhibitorSuperLane is null ||
                 string.Equals(inhibitorSuperLane, lane, StringComparison.Ordinal))
                ? new[] { MinionType.Super }
                : expected;
            var actual = spawned.Where(entity => entity.Team == team &&
                entity.MinionLane == lane).Select(entity => entity.MinionType!.Value)
                .ToArray();
            var expectedLane = laneExpected;
            TestAssert.Equal(expectedLane.Length, actual.Length,
                $"{label} {lane} member count");
            foreach (var type in expectedLane)
            {
                TestAssert.True(actual.Contains(type),
                    $"{label} {lane} contains {type}");
                actual = actual.SkipWhile(item => item != type).Skip(1).ToArray();
            }
        }
    }

    private static void RunFocusedLateWaveCases(NavigationGrid grid)
    {
        // Case 3: a client-parked attacker may retain a private server route.
        // The source still cancels the old attack before replacing that route.
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var events = new List<AiEvent>();
        var target = Add(world, 0x400510u, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7300.0, 7000.0), 0.0, 65.0);
        target.Alive = true;
        target.Created = true;
        var anchor = AddMinion(world, 0x400511u, new Position2(7150.0, 7000.0));
        var mover = AddMinion(world, 0x400512u, new Position2(7150.0, 7000.0));
        anchor.AttackTarget = target.NetId;
        mover.AttackTarget = target.NetId;
        anchor.MinionState = mover.MinionState = MinionAiState.Engage;
        mover.Waypoints = [mover.Position, new Position2(7200.0, 7000.0)];
        mover.AnchorPath = [mover.Position];
        mover.ClientAttackArmed = true; // it has attacked: the client is in the attack state
        var ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(events),
            new AlwaysVision());
        ai.RunEngagementProbe(mover, target);
        var own = events.Where(item => item.NetId == mover.NetId).ToArray();
        TestAssert.Equal("stop-attack", own.ElementAtOrDefault(0)?.Kind,
            "client-parked overlap must cancel the old attack first");
        TestAssert.Equal("waypoints", own.ElementAtOrDefault(1)?.Kind,
            "client-parked overlap must send the private separation route");

        // Case 4: the combat fire gate includes the reviewed 3u float strip,
        // while the ideal stop radius remains strict. A planted overlapping
        // pair inside that strip still needs a bounded separation route.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        events = new List<AiEvent>();
        var turret = AddStructure(world, 0x400520u, Team.Chaos,
            EntityKind.Turret, UnitType.Turret, new Position2(7000.0, 7000.0), 88.0);
        var epsilonDistance = 550.0 + turret.Radius + 1.5;
        anchor = Add(world, 0x400521u, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(turret.Position.X + epsilonDistance, turret.Position.Z),
            325.0, 35.7437);
        mover = Add(world, 0x400522u, EntityKind.Minion, UnitType.Minion,
            Team.Order, anchor.Position, 325.0, 35.7437);
        foreach (var minion in new[] { anchor, mover })
        {
            minion.MinionType = MinionType.Caster;
            minion.Alive = true;
            minion.Created = true;
            minion.AttackRange = 550.0;
            minion.AcquisitionRange = 700.0;
            minion.AttackTarget = turret.NetId;
            minion.MinionState = MinionAiState.Engage;
            minion.AnchorPath = [minion.Position];
        }
        mover.ClientAttackArmed = true; // it has attacked: the client is in the attack state
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(events),
            new AlwaysVision());
        ai.RunEngagementProbe(mover, turret);
        own = events.Where(item => item.NetId == mover.NetId).ToArray();
        TestAssert.Equal("stop-attack", own.ElementAtOrDefault(0)?.Kind,
            $"epsilon-band overlap must cancel the old attack first; events={string.Join(",", events.Select(item => item.Kind + ":" + item.NetId.ToString("X")))} " +
            $"state={mover.AiState} target={mover.AttackTarget?.ToString() ?? "null"} waypoints={mover.Waypoints.Count} " +
            $"distance={Distance(mover.Position, turret.Position):R}");
        TestAssert.Equal("waypoints", own.ElementAtOrDefault(1)?.Kind,
            "epsilon-band overlap must send a separation route");
        TestAssert.True(mover.Waypoints.Count >= 2,
            "epsilon-band overlap must retain a live movement leg");

        // Case 5: a legal movement-grid sidestep can sit inside the wider
        // turret planning padding. Preserve the local route and its body
        // clearance instead of falling back to a straight target order.
        world = new WorldState();
        movement = new MovementSystem(world, grid);
        events = new List<AiEvent>();
        turret = AddStructure(world, 0x400530u, Team.Order,
            EntityKind.Turret, UnitType.Turret, new Position2(5448.36, 6169.1), 88.0);
        anchor = Add(world, 0x400531u, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(5639.0, 6113.0), 325.0, 35.7437);
        mover = Add(world, 0x400532u, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(5633.0, 6103.0), 325.0, 35.7437);
        foreach (var minion in new[] { anchor, mover })
        {
            minion.MinionType = MinionType.Melee;
            minion.Alive = true;
            minion.Created = true;
            minion.AttackRange = 170.0;
            minion.AcquisitionRange = 450.0;
            minion.AttackTarget = turret.NetId;
            minion.MinionState = MinionAiState.Engage;
            minion.AnchorPath = [minion.Position];
        }
        mover.ClientAttackArmed = true; // it has attacked: the client is in the attack state
        ai = new MinionAiSystem(world, grid, movement, new FixtureCombat(),
            new RecordingPackets(events),
            new AlwaysVision());
        ai.RunEngagementProbe(mover, turret);
        own = events.Where(item => item.NetId == mover.NetId).ToArray();
        TestAssert.Equal("stop-attack", own.ElementAtOrDefault(0)?.Kind,
            "turret-padding overlap must cancel the old attack first");
        TestAssert.Equal("waypoints", own.ElementAtOrDefault(1)?.Kind,
            "turret-padding overlap must send the local sidestep");
        var route = own.ElementAtOrDefault(1)?.Points;
        TestAssert.True(route is { Count: >= 2 },
            "turret-padding sidestep must contain a live route");
        var safeRoute = route ?? throw new InvalidOperationException(
            "turret-padding sidestep route unexpectedly missing");
        TestAssert.True(safeRoute.All(grid.IsMovementWalkable),
            "turret-padding route must stay on the movement grid");
        // Batch 225 (retail 140-unit turret footprint; the A* grid no longer dilates turrets): the overlap point lies outside
        // the turret's stamp in the movement view, which the route check above already requires for every point.
        TestAssert.True(grid.IsMovementWalkable(safeRoute[^1]),
            "turret-padding overlap point must sit outside the 140-unit turret stamp");
        TestAssert.True(grid.HasMovementLineOfSight(safeRoute[0], safeRoute[^1]),
            "turret-padding route must retain movement-grid line of sight");
        var bodyObstacle = new NavigationObstacle(anchor.Position,
            movement.BodyClearance(mover, anchor));
        TestAssert.True(grid.SegmentClearOfUnits(safeRoute[0], safeRoute[^1],
                [bodyObstacle], escape: true),
            "turret-padding local route must clear the planted body along its whole segment");
        var gap = Distance(safeRoute[^1], anchor.Position);
        TestAssert.True(gap >= movement.BodyClearance(mover, anchor) - 2.0,
            "turret-padding route must clear the planted body");
        var eventCount = own.Length;
        for (var index = 0; index < 30; index++)
            ai.RunEngagementProbe(mover, turret);
        TestAssert.Equal(eventCount, events.Where(item => item.NetId == mover.NetId)
            .Count(item => item.Kind is "stop-attack" or "waypoints"),
            "an in-flight local sidestep must not become a packet stream");
    }

    private static void RunProductionLateWaveSoak()
    {
        var previousTrace = Environment.GetEnvironmentVariable("LOG_FIDELITY_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", "1");
            var trace = new ProductionTraceLog();
            var runtime = MatchRuntime.CreateProduction(
                NavigationGrid.LoadDefault(), GameData.Default,
                orderChampion: "Annie", chaosChampion: "Annie", solo: true,
                originalRosterCounts: new SessionRosterCounts(1, 0),
                readySettleSeconds: 0.0, minionFidelity: true,
                // Pin combat rolls for phase diagnostics; the earlier
                // unseeded late-wave failure remains unproven as a source
                // regression until this deterministic run reproduces it.
                random: new AnnieChecks.FixedRandomSource(0.5),
                sessionLog: trace);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            TestAssert.True(runtime.Session.Authenticate(1,
                    runtime.Session.BuildKeyCheck(1)) is not null,
                "late-wave production observer authentication");
            _ = runtime.Session.Spawn(1);
            TestAssert.True(runtime.Session.MarkReady(1),
                "late-wave production observer ready");
            TestAssert.True(runtime.Session.Start(),
                "late-wave production session start");
            var participant = runtime.Session.Participants.Single();
            var hero = participant.Hero ?? throw new InvalidOperationException(
                "late-wave production observer has no hero");
            // Match the source observer fixture: protected hero at the real
            // mid-lane fidelity point, with no player movement or target.
            hero.Protected = true;
            hero.Position = new Position2(7601.74, 6449.17);
            hero.Waypoints.Clear();
            hero.Target = null;
            hero.AttackTarget = null;
            runtime.Delivery.AddObserver(new ReplicationObserver(
                1, Team.Order, hero.NetId, new NoRetainTransport(),
                ready: true, view: participant.View));
            runtime.Delivery.Flush();
            trace.Clear();
            TestAssert.True(runtime.Waves!.Started &&
                            runtime.Waves.ActiveLanes.SequenceEqual(["C"]),
                "production fidelity composition must start the mid-lane scheduler");

            var spawnedWaves = new HashSet<int>();
            var seenMinions = new HashSet<uint>();
            var spawnWaveById = new Dictionary<uint, int>();
            var declipTimes = new Dictionary<uint, List<double>>();
            var congestionRouteTimes = new Dictionary<uint, List<double>>();
            var deepOverlapSamples = 0;
            var plantedPairSamples = 0;
            var deepOverlapDetails = new List<string>();
            var deferredPairs = new Dictionary<(uint Left, uint Right), DeferredOverlap>();
            // Pairs planted on the previous tick, and pairs that already used their arrival grace.
            var previouslyPlanted = new HashSet<(uint Left, uint Right)>();
            var arrivalGraceUsed = new HashSet<(uint Left, uint Right)>();
            var deferredDetails = new List<string>();
            var recoveryDetails = new List<string>();
            const double dt = 1.0 / 30.0;
            for (var tick = 0; tick < 359 * 30; tick++)
            {
                var startWindups = runtime.Combat.PendingHits
                    .Where(hit => hit.SourceKind == DamageSourceKind.Basic &&
                                  hit.WindupRemaining > 0.0)
                    .GroupBy(hit => (hit.SourceNetId, hit.TargetNetId))
                    .ToDictionary(group => group.Key,
                        group => group.First().WindupRemaining);
                var startCommitted = new HashSet<(uint Source, uint Target)>();
                foreach (var source in runtime.World.Entities.Values.Where(entity =>
                             entity.UnitType == UnitType.Minion && entity.Alive &&
                             entity.AttackTarget is not null))
                {
                    if (source.AttackTarget is uint targetId &&
                        runtime.World.Get(targetId) is { Alive: true } target &&
                        runtime.Combat.HasCommittedWindup(source, target))
                        startCommitted.Add((source.NetId, targetId));
                }
                runtime.Tick(dt);
                var now = runtime.Combat.CurrentTime;
                var endWindups = runtime.Combat.PendingHits
                    .Where(hit => hit.SourceKind == DamageSourceKind.Basic)
                    .GroupBy(hit => (hit.SourceNetId, hit.TargetNetId))
                    .ToDictionary(group => group.Key,
                        group => group.First().WindupRemaining);
                foreach (var entity in runtime.World.Entities.Values.Where(entity =>
                             entity.UnitType == UnitType.Minion && seenMinions.Add(entity.NetId)))
                {
                    var wave = 1 + (int)Math.Round(
                        (entity.SpawnScheduledAt - MinionDefinitions.FirstWaveSeconds) /
                        MinionDefinitions.WaveIntervalSeconds);
                    spawnWaveById[entity.NetId] = wave;
                    if (wave is >= 6 and <= 8) spawnedWaves.Add(wave);
                }
                foreach (var order in trace.Orders.Skip(trace.ProcessedOrders))
                {
                    var seconds = order.MonotonicNanoseconds / 1_000_000_000.0;
                    if (order.Reason == "overlap-declip")
                    {
                        declipTimes.TryAdd(order.NetId, []);
                        declipTimes[order.NetId].Add(seconds);
                    }
                    // Batch 236: the stuck-site repath ("stuck-repath") is the retail answer to a body in the way; the K3 planner's
                    // "congestion-route" is gone since batch 225 and overlaps rarely happen once routes go round standing bodies.
                    else if (order.Reason is "congestion-route" or "stuck-repath")
                    {
                        congestionRouteTimes.TryAdd(order.NetId, []);
                        congestionRouteTimes[order.NetId].Add(seconds);
                    }
                }
                trace.ProcessedOrders = trace.Orders.Count;
                if (now < 240.0) continue;
                var plantedPairs = new Dictionary<
                    (uint Left, uint Right),
                    (Entity Left, Entity Right, Entity Victim, double Gap,
                        double Radii, int LeftWave, int RightWave)>();
                foreach (var group in runtime.World.Entities.Values
                             .Where(entity => entity.UnitType == UnitType.Minion &&
                                              entity.Alive && entity.AttackTarget is not null &&
                                              entity.Waypoints.Count == 0 && entity.Target is null)
                             .GroupBy(entity => (entity.Team, entity.AttackTarget!.Value)))
                {
                    var attackers = group.ToArray();
                    for (var left = 0; left < attackers.Length; left++)
                    for (var right = left + 1; right < attackers.Length; right++)
                    {
                        if (!spawnWaveById.TryGetValue(attackers[left].NetId, out var leftWave) ||
                            !spawnWaveById.TryGetValue(attackers[right].NetId, out var rightWave) ||
                            (leftWave is not (6 or 7 or 8)) &&
                            (rightWave is not (6 or 7 or 8)))
                            continue;
                        var gap = Distance(attackers[left].Position, attackers[right].Position);
                        var radii = runtime.Movement.PairMovementRadius(attackers[left], attackers[right]) +
                                    runtime.Movement.PairMovementRadius(attackers[right], attackers[left]);
                        var victim = runtime.World.Get(attackers[left].AttackTarget!.Value)!;
                        var key = attackers[left].NetId < attackers[right].NetId
                            ? (attackers[left].NetId, attackers[right].NetId)
                            : (attackers[right].NetId, attackers[left].NetId);
                        var first = key.Item1 == attackers[left].NetId
                            ? attackers[left] : attackers[right];
                        var second = ReferenceEquals(first, attackers[left])
                            ? attackers[right] : attackers[left];
                        var firstWave = first.NetId == attackers[left].NetId
                            ? leftWave : rightWave;
                        var secondWave = first.NetId == attackers[left].NetId
                            ? rightWave : leftWave;
                        plantedPairs[key] = (first, second, victim,
                            gap, radii, firstWave, secondWave);
                    }
                }

                plantedPairSamples += plantedPairs.Count;
                foreach (var (key, deferred) in deferredPairs.ToArray())
                {
                    // Keep the original scan deadline. A new windup cannot
                    // extend a transition that already consumed its grace.
                    if (!plantedPairs.TryGetValue(key, out var pair))
                    {
                        var first = runtime.World.Get(key.Left);
                        var second = runtime.World.Get(key.Right);
                        var reason = first is null || second is null ? "removed" :
                            !first.Alive || !second.Alive ? "death" :
                            first.AttackTarget != deferred.TargetNetId ||
                            second.AttackTarget != deferred.TargetNetId ? "retarget" :
                            first.Waypoints.Count > 0 || second.Waypoints.Count > 0 ||
                            first.Target is not null || second.Target is not null ? "route" :
                            "no-longer-planted";
                        deferredPairs.Remove(key);
                        if (recoveryDetails.Count < 3)
                            recoveryDetails.Add(
                                $"t={now:F3} pair=0x{key.Left:X8}/0x{key.Right:X8} " +
                                $"deferred={deferred.Time:F3} recovery={reason}");
                        continue;
                    }

                    if (pair.Victim.NetId != deferred.TargetNetId)
                    {
                        deferredPairs.Remove(key);
                        if (recoveryDetails.Count < 3)
                            recoveryDetails.Add(
                                $"t={now:F3} pair=0x{key.Left:X8}/0x{key.Right:X8} " +
                                $"deferred={deferred.Time:F3} recovery=retarget");
                        continue;
                    }

                    if (pair.Gap >= 0.5 * pair.Radii)
                    {
                        deferredPairs.Remove(key);
                        if (recoveryDetails.Count < 3)
                            recoveryDetails.Add(
                                $"t={now:F3} pair=0x{key.Left:X8}/0x{key.Right:X8} " +
                                $"deferred={deferred.Time:F3} recovery=separated gap={pair.Gap:F3}");
                        continue;
                    }

                    if (now + 1e-9 >= deferred.Deadline)
                    {
                        var leftAfter = endWindups.TryGetValue(
                            (pair.Left.NetId, deferred.TargetNetId), out var leftValue)
                            ? leftValue : (double?)null;
                        var rightAfter = endWindups.TryGetValue(
                            (pair.Right.NetId, deferred.TargetNetId), out var rightValue)
                            ? rightValue : (double?)null;
                        TestAssert.True(false,
                            $"late-wave deferred overlap did not recover at next AI opportunity: " +
                            $"t={now:F3} pair=0x{key.Left:X8}/0x{key.Right:X8} " +
                            $"deferred={deferred.Time:F3} deadline={deferred.Deadline:F3} " +
                            $"gap={pair.Gap:F3} radii={pair.Radii:F3} " +
                            $"leftWindup={deferred.LeftBeforeWindup:R}->" +
                            $"{deferred.LeftAfterWindup?.ToString("R") ?? "none"}->" +
                            $"{leftAfter?.ToString("R") ?? "none"} " +
                            $"rightWindup={deferred.RightBeforeWindup:R}->" +
                            $"{deferred.RightAfterWindup?.ToString("R") ?? "none"}->" +
                            $"{rightAfter?.ToString("R") ?? "none"}");
                    }
                }

                foreach (var (key, pair) in plantedPairs)
                {
                    if (deferredPairs.ContainsKey(key)) continue;
                    // A committed attack finishes before either actor changes
                    // its seat. Check physical recovery outside that windup.
                    if (runtime.Combat.HasCommittedWindup(pair.Left, pair.Victim) ||
                        runtime.Combat.HasCommittedWindup(pair.Right, pair.Victim)) continue;
                    if (pair.Gap < 0.5 * pair.Radii)
                    {
                        var leftKey = (pair.Left.NetId, pair.Victim.NetId);
                        var rightKey = (pair.Right.NetId, pair.Victim.NetId);
                        double? leftBefore = null;
                        if (startCommitted.Contains(leftKey) &&
                            startWindups.TryGetValue(leftKey, out var leftEntry))
                            leftBefore = leftEntry;
                        double? rightBefore = null;
                        if (startCommitted.Contains(rightKey) &&
                            startWindups.TryGetValue(rightKey, out var rightEntry))
                            rightBefore = rightEntry;
                        var leftCrossed = leftBefore is > 0.0 &&
                            (!endWindups.TryGetValue(leftKey, out var leftRemaining) ||
                             leftRemaining <= 0.0);
                        var rightCrossed = rightBefore is > 0.0 &&
                            (!endWindups.TryGetValue(rightKey, out var rightRemaining) ||
                             rightRemaining <= 0.0);
                        if (leftCrossed || rightCrossed)
                        {
                            var leftAfter = endWindups.TryGetValue(leftKey, out var leftValue)
                                ? leftValue : (double?)null;
                            var rightAfter = endWindups.TryGetValue(rightKey, out var rightValue)
                                ? rightValue : (double?)null;
                            var deferred = new DeferredOverlap(key.Left, key.Right,
                                pair.Victim.NetId, now,
                                leftBefore ?? 0.0, leftAfter,
                                rightBefore ?? 0.0, rightAfter,
                                now + MinionDefinitions.TargetScanPeriodSeconds + dt);
                            deferredPairs[key] = deferred;
                            if (deferredDetails.Count < 3)
                                deferredDetails.Add(
                                    $"t={now:F3} pair=0x{key.Left:X8}/0x{key.Right:X8} " +
                                    $"left={deferred.LeftBeforeWindup:R}->{leftAfter?.ToString("R") ?? "none"} " +
                                    $"right={deferred.RightBeforeWindup:R}->{rightAfter?.ToString("R") ?? "none"} " +
                                    $"deadline={deferred.Deadline:F3}");
                            continue;
                        }
                        // Owner decision, 16 Oct 2026 (K6 removed): retail merges returning
                        // minions without a stagger, so two of them can plant on one spot on the
                        // same frame. Nothing decides before the 250 ms scan (TimerFindEnemies),
                        // so a pair that has just planted gets the same next-scan recovery
                        // contract as the windup case above, once per pair; one that has not
                        // recovered by then still fails.
                        if (!previouslyPlanted.Contains(key) && arrivalGraceUsed.Add(key))
                        {
                            deferredPairs[key] = new DeferredOverlap(key.Left, key.Right,
                                pair.Victim.NetId, now, 0.0, null, 0.0, null,
                                now + MinionDefinitions.TargetScanPeriodSeconds + dt);
                            if (deferredDetails.Count < 3)
                                deferredDetails.Add(
                                    $"t={now:F3} pair=0x{key.Left:X8}/0x{key.Right:X8} " +
                                    $"arrival gap={pair.Gap:F3} deadline={deferredPairs[key].Deadline:F3}");
                            continue;
                        }
                        deepOverlapSamples++;
                        if (deepOverlapDetails.Count < 3)
                        {
                            deepOverlapDetails.Add(
                                $"t={now:F3} left=0x{pair.Left.NetId:X8}/w{pair.LeftWave} " +
                                $"right=0x{pair.Right.NetId:X8}/w{pair.RightWave} " +
                                $"gap={pair.Gap:F3} radii={pair.Radii:F3}");
                        }
                    }
                }
                previouslyPlanted = plantedPairs.Keys.ToHashSet();
            }

            TestAssert.Equal(0, deferredPairs.Count,
                "late-wave soak ended before all deferred overlaps recovered: " +
                string.Join("; ", deferredPairs.Values.Select(pair =>
                    $"0x{pair.Left:X8}/0x{pair.Right:X8} deadline={pair.Deadline:F3}")));
            TestAssert.Equal(9, runtime.Waves.WaveIndex,
                "late-wave production scheduler must finish wave 8");
            TestAssert.Equal(0, runtime.Waves.Pending.Count,
                "late-wave production scheduler must drain all spawns");
            TestAssert.True(spawnedWaves.SetEquals([6, 7, 8]),
                "late-wave production soak must cover waves 6-8");
            // Batch 236: once routes go round standing bodies the overlaps this soak used to repair rarely form, so the premise is
            // the crowd itself: planted co-attackers of waves 6-8 sampled together, or a repair route.
            TestAssert.True(declipTimes.Count > 0 || congestionRouteTimes.Count > 0 || plantedPairSamples > 0,
                "production late-wave soak never exercised a planted crowd, overlap or stuck-repath routing");
            var maxRate = 0;
            foreach (var pair in declipTimes)
            {
                var times = pair.Value;
                for (var left = 0; left < times.Count; left++)
                {
                    var right = left;
                    while (right + 1 < times.Count &&
                           times[right + 1] - times[left] < 1.0) right++;
                    maxRate = Math.Max(maxRate, right - left + 1);
                }
            }
            TestAssert.True(maxRate <= 10,
                $"production late-wave overlap-declip rate exceeded 10/s: {maxRate}");
            TestAssert.Equal(0, deepOverlapSamples,
                "production late-wave minions remained deeply overlapped while planted: " +
                string.Join("; ", deepOverlapDetails) +
                $"; deferred={string.Join("; ", deferredDetails)}" +
                $"; recovery={string.Join("; ", recoveryDetails)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", previousTrace);
        }
    }

    [Check("minions-stale-parking", Serial = true)]
    public static void StalePlantedAttackerReplantsAtBoundedCadence()
    {
        RunProductionStalePlantScenarios();
    }

    private static void RunProductionStalePlantScenarios()
    {
        const double x0 = 7000.0;
        const double z0 = 7000.0;
        const double heading = 320.0 * Math.PI / 180.0;
        var ux = Math.Cos(heading);
        var uz = Math.Sin(heading);
        var stats = MinionDefinitions.BaseStats(MinionType.Melee);
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        var stop = stats.AttackRange + 2 * GameData.Default.Champion("Blue_Minion_Basic").GameplayRadius - 3.0;
        var speed = stats.MoveSpeed;
        Position2 At(double distance) => new(x0 + ux * distance, z0 + uz * distance);

        ProductionPlantRun Run(Func<double, Position2> script, double seconds,
            double replantGap)
        {
            var oldTraceGate = Environment.GetEnvironmentVariable("LOG_FIDELITY_TRACE");
            try
            {
                Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", "1");
                var trace = new ProductionTraceLog();
                var runtime = MatchRuntime.CreateProduction(
                    NavigationGrid.LoadDefault(), GameData.Default,
                    orderChampion: "Annie", chaosChampion: "Annie", solo: true,
                    originalRosterCounts: new SessionRosterCounts(1, 0),
                    readySettleSeconds: 0.0, minionFidelity: true,
                    sessionLog: trace);
                runtime.World.Clock.SetVirtual(0.0);
                runtime.Session.CreateWorld();
                TestAssert.True(runtime.Session.Authenticate(1,
                        runtime.Session.BuildKeyCheck(1)) is not null,
                    "stale-plant production observer authentication");
                runtime.Session.Spawn(1);
                TestAssert.True(runtime.Session.MarkReady(1),
                    "stale-plant production observer ready");
                TestAssert.True(runtime.Session.Start(),
                    "stale-plant production session start");
                var participant = runtime.Session.Participants.Single();
                var observerHero = participant.Hero ?? throw new InvalidOperationException(
                    "stale-plant production observer has no hero");
                observerHero.Protected = true;
                observerHero.Position = new Position2(7601.74, 6449.17);
                observerHero.Waypoints.Clear();
                observerHero.Target = null;
                observerHero.AttackTarget = null;
                var attackPackets = new BasicAttackCaptureTransport();
                runtime.Delivery.AddObserver(new ReplicationObserver(
                    1, Team.Order, observerHero.NetId, attackPackets,
                    ready: true, view: participant.View));
                runtime.Delivery.Flush();
                trace.Clear();

                var attacker = runtime.Waves!.SpawnFixture(Team.Order,
                    MinionType.Melee, 0, "C", 0.0, 0.0);
                attacker.Position = new Position2(x0, z0);
                attacker.Waypoints.Clear();
                attacker.Target = null;
                attacker.AnchorPath = [attacker.Position];
                attacker.LastAnchorTime = 0.0;
                attacker.MinionState = MinionAiState.Engage;

                // The source fixture uses a real melee minion as the
                // scripted body. It is registered through the same wave
                // spawn path so radius, targetability, and class stats remain
                // production values; only its position is scripted per tick.
                var target = runtime.Waves.SpawnFixture(Team.Chaos,
                    MinionType.Melee, 1, "C", 0.0, 0.0);
                target.Created = true;
                target.Visible = true;
                target.Health = 1_000_000_000.0;
                target.MaxHealth = 1_000_000_000.0;
                target.Position = script(0.0);
                target.Waypoints.Clear();
                target.LaneWaypoints.Clear();
                target.Target = null;
                target.AttackTarget = null;
                target.MinionState = MinionAiState.March;
                attacker.AttackTarget = target.NetId;
                attackPackets.SourceNetId = attacker.NetId;
                runtime.Minions!.ReplantMinimumGapSeconds = replantGap;
                var samples = new List<ProductionPlantSample>();
                var orderTimes = new List<double>();
                var orderReasons = new List<string>();
                var processedOrders = 0;
                const double dt = 1.0 / 30.0;
                var frames = (int)Math.Round(seconds / dt);
                for (var frame = 0; frame < frames; frame++)
                {
                    var time = (frame + 1) * dt;
                    target.Position = script(frame * dt);
                    target.Waypoints.Clear();
                    target.Target = null;
                    attackPackets.CurrentTime = time;
                    runtime.Tick(dt);
                    foreach (var order in trace.Orders.Skip(processedOrders))
                    {
                        // The source monkeypatch is installed after
                        // spawn_minion, so its first observed 0xC1 is an AI
                        // order rather than the spawn anchor.
                        if (order.NetId == attacker.NetId &&
                            order.Reason is not ("wave-spawn" or "reveal-anchor"))
                        {
                            orderTimes.Add(order.MonotonicNanoseconds / 1_000_000_000.0);
                            orderReasons.Add(order.Reason);
                        }
                    }
                    processedOrders = trace.Orders.Count;
                    var distance = Distance(attacker.Position, target.Position);
                    var planted = attacker.Waypoints.Count == 0 && attacker.Target is null;
                    // owner decision 27 Sept 2026: edge-to-edge gameplay radii
                    var inFireRange = distance <= CombatService.AttackReach(attacker, target);
                    samples.Add(new ProductionPlantSample(time, distance - stop,
                        planted, inFireRange));
                }

                TestAssert.True(attacker.Alive && target.Alive,
                    "stale-plant production bodies must survive the measured window");
                TestAssert.Equal(target.NetId, attacker.AttackTarget ?? 0,
                    "stale-plant attacker must retain its scripted target");
                return new ProductionPlantRun(samples, orderTimes, orderReasons,
                    attackPackets.Times.Count);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", oldTraceGate);
            }
        }

        var staleScript = (double time) => At(stop + 260.0 +
            (time >= 0.4 ? 40.0 : 0.0));
        var seconds = 0.4 + 260.0 / speed + 3.0;
        var off = Run(staleScript, seconds, double.PositiveInfinity);
        var shipped = Run(staleScript, seconds, 0.5);
        foreach (var run in new[] { off, shipped })
        {
            TestAssert.True(run.Samples[^1].InFireRange && run.AttackCount > 0,
                "target-relative route refresh must reach and attack after the target's 40u step");
            TestAssert.True(run.Samples.Count(sample => sample.Planted && !sample.InFireRange) <= 8,
                "a moved target must not leave its pursuer planted beyond two decision intervals");
        }

        // Endpoint validity now repairs the route before the old 120-unit
        // deadband could produce the archived frozen plant. Exercise both
        // disabled stale-guard controls and the live guard against outcomes.
        foreach (var gap in new[] { double.PositiveInfinity, 0.5, 0.0 })
        {
            // Batch 152: the swing is the script's now. Minion.lua 0.2 arms it from its own
            // DELAY_FIND_ENEMIES scan -- "if TargetInAttackRange(): TurnOnAutoAttack(GetTarget())"
            // (proto 0.2, 0056-0069) -- and minionLib 0.0 OnInit arms that timer for 0.25 s
            // (0003-0007), so the first chance to swing is a quarter second after birth. The old
            // machine armed it from its own engage scan, which ran on the first AI tick. A target
            // three units inside attack reach that recedes at half speed is out of reach by 0.02 s,
            // so the attack here comes from the pursuit keeping it in range, not from a birth scan.
            var receding = Run(time => At(stop + (speed / 2.0) * time), 4.0, gap);
            TestAssert.True(receding.Orders.Count <= 17,
                $"target pursuit exceeded one movement decision per 250ms: {receding.Orders.Count}");
            TestAssert.True(receding.AttackCount > 0,
                "a slower receding target must remain attackable");
        }
        var stationaryOscillation = Run(time =>
        {
            var leg = 80.0 / speed;
            var phase = (time % (2.0 * leg)) / leg;
            var ramp = phase < 1.0 ? phase : 2.0 - phase;
            return At(stop - 20.0 + 80.0 * ramp);
        }, 8.0, 0.5);
        var oscillationOff = Run(time =>
        {
            var leg = 80.0 / speed;
            var phase = (time % (2.0 * leg)) / leg;
            var ramp = phase < 1.0 ? phase : 2.0 - phase;
            return At(stop - 20.0 + 80.0 * ramp);
        }, 8.0, double.PositiveInfinity);
        TestAssert.True(stationaryOscillation.Orders.Count <= oscillationOff.Orders.Count,
            "stale guard added orders to the ordinary band-edge oscillation");
        TestAssert.True(stationaryOscillation.AttackCount >= oscillationOff.AttackCount,
            "stale guard made an oscillating target less attackable");

        var strip = Run(time => At(stop + (time >= 0.5 ? 0.5 : 0.0)), 3.0, 0.5);
        TestAssert.Equal(0, strip.Orders.Count(time => time >= 1.0),
            "a planted minion inside the combat epsilon must not replant");
        TestAssert.True(strip.AttackCount >= 2,
            "a planted minion inside the combat epsilon must keep firing");
    }
    [Check("minions-vertex-jam")]
    public static void SharedVertexPairMakesProgress() => VertexMarchChecks.RunProduction();

    private static NavigationGrid CreateSyntheticBrushGrid()
    {
        const int count = 256;
        const float cellSize = 10.0f;
        const int stride = 56;
        using var stream = new MemoryStream(1 + 8 * sizeof(float) + 2 * sizeof(uint) + count * count * stride);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)2);
            writer.Write(0.0f); // min X
            writer.Write(0.0f); // min Y (unused)
            writer.Write(0.0f); // min Z
            writer.Write(count * cellSize); // max X
            writer.Write(0.0f); // max Y (unused)
            writer.Write(count * cellSize); // max Z
            writer.Write(cellSize);
            writer.Write((uint)count);
            writer.Write((uint)count);
            var cell = new byte[stride];
            cell[50] = 0x01; // grass, with clear vision and walkable flags
            for (var index = 0; index < count * count; index++) writer.Write(cell);
        }
        stream.Position = 0;
        return NavigationGrid.Load(stream, new NavigationGridSettings
        {
            CollisionRadius = 0.0,
            TurretPathStampRadius = 0.0,
            TurretClientStampRadius = 0.0,
            Turrets = Array.Empty<TurretDefinition>(),
            Structures = [],
        });
    }


    [Check("minions-hero-body-blocking")]
    public static void HeroRoutesAroundDenseWaveBodies()
    {
        foreach (var dt in new[] { 1.0 / 30.0, 0.1 })
        {
            var grid = NavigationGrid.LoadDefault();
            var world = new WorldState();
            var movement = new MovementSystem(world, grid);
            var hero = Add(world, 1, EntityKind.Hero, UnitType.Hero, Team.Order,
                new Position2(7300.0, 7000.0), 350.0, 65.0);
            var wallZ = new[] { 6825.0, 6875.0, 6925.0, 6975.0,
                7025.0, 7075.0, 7125.0, 7175.0 };
            for (var index = 0; index < wallZ.Length; index++)
                Add(world, (uint)(10 + index), EntityKind.Minion, UnitType.Minion,
                    Team.Chaos, new Position2(7300.0, wallZ[index]), 325.0, 35.7437).Visible = true;

            var route = movement.SetMoveOrder(hero.NetId, new Position2(7600.0, 7000.0));
            // Batch 61: the stock planner ignores a unit the mover already overlaps (the half-plane test), and this hero
            // stands inside the wall, so it plans the straight chord out of it (the retired planner backed out first and went around);
            // the contact solver then pushes it through the wave.
            TestAssert.True(route.Count >= 2,
                $"dense body order should plan a route, got {route.Count} points: " +
                string.Join(";", route.Select(point => $"({point.X:R},{point.Z:R})")));
            TestAssert.True(route[^1].X > route[0].X, "the planned route leaves the wall toward the click");

            var previous = hero.Position;
            var minimumGap = double.PositiveInfinity;
            var maximumStep = 0.0;
            for (var tick = 0; tick < 240; tick++)
            {
                movement.Tick(dt);
                var step = Distance(previous, hero.Position);
                maximumStep = Math.Max(maximumStep, step);
                previous = hero.Position;
                if (tick < 15) continue;
                foreach (var body in world.Entities.Values.Where(entity => entity.UnitType == UnitType.Minion))
                    minimumGap = Math.Min(minimumGap, Distance(hero.Position, body.Position));
            }

            // The thin flagged hero disc (0.2 of its pathing radius) against the minion's full disc: the figure below which the discs overlap.
            var physical = movement.MoverPairRadius(hero, world.Get(10)!) + movement.PairMovementRadius(world.Get(10)!, hero);
            TestAssert.True(minimumGap >= physical - 1e-3,
                $"hero entered a wave body: minimum gap {minimumGap:R}");
            TestAssert.True(maximumStep <= 350.0 * dt * Math.Pow(1.5, 1.5) + 1e-6,
                $"hero step exceeded collision speed cap: {maximumStep:R}");
            TestAssert.Near(7599.0, hero.Position.X, 1.0,
                $"dense wave hero final X ({dt:R})");
            TestAssert.Near(7001.0, hero.Position.Z, 1.0,
                $"dense wave hero final Z ({dt:R})");
        }
    }

    [Check("minions-hidden-bodies")]
    public static void HiddenEnemyBodiesDoNotShapeHeroRoute()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var hero = Add(world, 1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(7000.0, 7000.0), 350.0, 65.0);
        foreach (var z in new[] { 6925.0, 6975.0, 7025.0, 7075.0 })
        {
            var body = Add(world, 10u + (uint)z, EntityKind.Minion, UnitType.Minion,
                Team.Chaos, new Position2(7300.0, z), 325.0, 35.7437);
            body.Visible = false;
        }

        var route = movement.SetMoveOrder(hero.NetId, new Position2(7600.0, 7000.0),
            visibleTo: _ => false);
        TestAssert.Equal(2, route.Count,
            "hidden enemy bodies should leave the direct client route intact");
    }

    [Check("minions-distant-body-route")]
    public static void DistantBodyDoesNotAlterLongRoute()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var hero = Add(world, 1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(7000.0, 7000.0), 350.0, 65.0);
        // Compare the same hero planner with and without the off-route body.
        // Generic NPC paths deliberately retain cell-centre LOS and no longer
        // describe the exact-origin route accepted for a hero.
        var expected = movement.PlanOrder(hero, new Position2(7000, 9000)).Path;
        TestAssert.True(expected.Count > 1, "unobstructed hero control needs a route");
        Add(world, 2, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new Position2(7300.0, 7000.0), 325.0, 35.7437).Visible = true;

        var actual = movement.SetMoveOrder(hero.NetId, new Position2(7000.0, 9000.0));
        TestAssert.Equal(expected.Count, actual.Count, "distant body changed route length");
        for (var index = 0; index < expected.Count; index++)
        {
            TestAssert.Near(expected[index].X, actual[index].X, 1e-9,
                $"distant route point {index} X");
            TestAssert.Near(expected[index].Z, actual[index].Z, 1e-9,
                $"distant route point {index} Z");
        }

        for (var tick = 0; tick < 220; tick++) movement.Tick(1.0 / 30.0);
        TestAssert.Near(expected[^1].X, hero.Position.X, 1.0,
            $"long route final X expected {expected[^1].X:R} actual {hero.Position.X:R}");
        TestAssert.Near(expected[^1].Z, hero.Position.Z, 1.0,
            $"long route final Z expected {expected[^1].Z:R} actual {hero.Position.Z:R}");
    }

    private static void CheckStats(JsonElement cases)
    {
        // Every figure is the shipped LevelScript's: the barracks rows GetMinionSpawnInfo copies
        // (LevelScript.txt:1694-2025) laid over the character's own base stats. One host per
        // (team, downMask), advanced monotonically through the case's elapsed values so each
        // UpgradeMinionTimer deadline fires exactly once (proto 0.4, LevelScript.txt:936-1606).
        // The rows are per barracks, but this fixture raises every dead inhibitor and the script
        // applies each such event to all three lanes, so one lane stands for the team.
        var items = cases.EnumerateArray().ToArray();
        var gold = items.ToDictionary(
            item => ((Team)item.GetProperty("team").GetInt32(),
                item.GetProperty("downMask").GetInt32(),
                item.GetProperty("elapsed").GetDouble(),
                (MinionType)item.GetProperty("kind").GetInt32()),
            item => item.GetProperty("expected").GetProperty("gold_reward").GetDouble());
        foreach (var group in items.GroupBy(item => ((Team)item.GetProperty("team").GetInt32(),
                     item.GetProperty("downMask").GetInt32())))
        {
            var (team, downMask) = group.Key;
            var world = InhibitorWorld(downMask, team);
            var host = LevelScriptFixtures.Init(world);
            LevelScriptFixtures.FellInhibitors(host, world, team);
            var upgradeInterval = host.Number("UPGRADE_MINION_TIMER", 180.0);
            foreach (var bucket in group.GroupBy(item => item.GetProperty("elapsed").GetDouble())
                         .OrderBy(bucket => bucket.Key))
            {
                host.AdvanceTo(Math.Max(0.0, bucket.Key));
                foreach (var item in bucket)
                {
                    var type = (MinionType)item.GetProperty("kind").GetInt32();
                    var actual = host.EconomyOf(team, MapDefinitions.Lanes[1]).StatsOf(type, team);
                    var expected = item.GetProperty("expected");
                    Near(expected, "hp", actual.MaxHealth, item);
                    Near(expected, "atk_damage", actual.AttackDamage, item);
                    Near(expected, "atk_range", actual.AttackRange, item);
                    Near(expected, "armor", actual.Armor, item);
                    Near(expected, "spell_block", actual.SpellBlock, item);
                    Near(expected, "hp_regen", actual.HealthRegen, item);
                    Near(expected, "attack_speed_mod", actual.AttackSpeedMod, item);
                    Near(expected, "atk_period", actual.AttackPeriod, item);
                    Near(expected, "acquisition_range", actual.AcquisitionRange, item);
                    Near(expected, "missile_speed", actual.MissileSpeed, item);
                    Near(expected, "windup", actual.AttackWindup, item);
                    TestAssert.Near(MinionAttackTimingChecks.ExpectedWindup(type, team),
                        actual.AttackWindup, 1e-8, $"native minion windup: {item}");
                    Near(expected, "speed", actual.MoveSpeed, item);
                    // LevelScript.txt proto 0.11 pc0098-0137 and proto 0.12 pc0295-0330 (owner
                    // decision, 18 October 2026): while an enemy inhibitor is down and the next
                    // UpgradeMinionTimer (proto 0.4 pc0062-0077) has not recomputed the tables,
                    // Archer/Caster/Super GoldGiven are read from MeleeGoldGiven. The corpus was
                    // recorded from the old hand-ported policy, which never applied that defect to
                    // these cases, so its gold_reward is not compared here; the script's own figure
                    // is, and it is the melee gold of the same case group.
                    if (downMask != 0 && bucket.Key < upgradeInterval && type != MinionType.Melee)
                        TestAssert.Near(gold[(team, downMask, bucket.Key, MinionType.Melee)],
                            actual.GoldReward, 1e-9,
                            $"minion stats case {downMask} gold_reward (script defect, LevelScript.txt:2362-2364)");
                    else Near(expected, "gold_reward", actual.GoldReward, item);
                    Near(expected, "xp_reward", actual.ExperienceReward, item);
                }
            }
        }
    }

    private static Entity ReadAiEntity(JsonElement raw)
    {
        var entity = new Entity(raw.GetProperty("netid").GetUInt32(),
            EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString()),
            (UnitType)raw.GetProperty("unit_type").GetInt32(),
            (Team)raw.GetProperty("team").GetInt32(), Point(raw.GetProperty("pos")))
        {
            MinionType = MinionType.Melee,
            Speed = raw.GetProperty("speed").GetDouble(),
            Radius = raw.GetProperty("radius").GetDouble(),
            Health = raw.GetProperty("hp").GetDouble(),
            MaxHealth = raw.GetProperty("max_hp").GetDouble(),
            Alive = raw.GetProperty("alive").GetBoolean(),
            Visible = raw.GetProperty("visible").GetBoolean(),
            Created = raw.GetProperty("created").GetBoolean(),
            AttackRange = raw.GetProperty("atk_range").GetDouble(),
            AcquisitionRange = raw.GetProperty("acquisition_range").GetDouble(),
            AttackCooldown = raw.GetProperty("attack_cd").GetDouble(),
            MinionState = ParseState(raw.GetProperty("ai_state").GetString()!),
            LaneDone = raw.GetProperty("lane_done").GetBoolean(),
            // Batch 279: the congestion_* fields of the corpus are the deleted planner's state (all at their defaults in every case);
            // they are neither read nor compared.
            LastAnchorTime = raw.GetProperty("last_anchor_time").GetDouble(),
            SeparationAccumulation = Point(raw.GetProperty("sep_accum")),
            StuckElapsed = raw.GetProperty("stuck_elapsed").GetDouble(),
            RepathAttempts = raw.GetProperty("repath_attempts").GetInt32(),
            RepathCount = raw.GetProperty("repath_count").GetInt32(),
            Facing = Point(raw.GetProperty("facing")),
        };
        if (raw.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
            entity.AttackTarget = raw.GetProperty("attack_target").GetUInt32();
        entity.Waypoints = Points(raw.GetProperty("waypoints")).ToList();
        entity.LaneWaypoints = Points(raw.GetProperty("lane_waypoints")).ToList();
        entity.AnchorPath = Points(raw.GetProperty("anchor_path")).ToList();
        var targetPoint = raw.GetProperty("target");
        if (targetPoint.ValueKind != JsonValueKind.Null) entity.Target = Point(targetPoint);
        LoadBuffs(entity, raw);
        return entity;
    }

    private static void SetBool(JsonElement payload, string name, Action<bool> setter)
    {
        if (payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            setter(value.GetBoolean());
    }

    private static void LoadBuffs(Entity entity, JsonElement raw)
    {
        if (!raw.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var rawBuff in buffs.EnumerateArray())
        {
            var payload = new BuffPayload();
            if (rawBuff.TryGetProperty("payload", out var rawPayload) &&
                rawPayload.ValueKind == JsonValueKind.Object)
            {
                SetBool(rawPayload, "action_disabled", value => payload.ActionDisabled = value);
                SetBool(rawPayload, "movement_disabled", value => payload.MovementDisabled = value);
                SetBool(rawPayload, "ghosted", value => payload.Ghosted = value);
                SetBool(rawPayload, "invulnerable", value => payload.Invulnerable = value);
                SetBool(rawPayload, "untargetable", value => payload.Untargetable = value);
                SetBool(rawPayload, "stealthed", value => payload.Stealthed = value);
            }
            entity.Buffs.Add(new BuffState(
                rawBuff.GetProperty("name").GetString() ?? "FixtureBuff",
                payload,
                (BuffType)rawBuff.GetProperty("type").GetInt32(),
                rawBuff.GetProperty("slot").GetInt32(),
                rawBuff.GetProperty("expires_at").GetDouble(),
                rawBuff.GetProperty("caster").GetUInt32()));
        }
    }

    private static void CompareAiEntity(JsonElement expected, Entity actual, string label)
    {
        if (FixtureRecorder.Enabled)
        {
            object? P(Position2? p) => p is { } point ? new[] { point.X, point.Z } : null;
            foreach (var field in expected.EnumerateObject())
            {
                object? value = field.Name switch
                {
                    "pos" => P(actual.Position), "speed" => actual.Speed, "radius" => actual.Radius,
                    "hp" => actual.Health, "max_hp" => actual.MaxHealth, "alive" => actual.Alive,
                    "visible" => actual.Visible, "created" => actual.Created, "atk_range" => actual.AttackRange,
                    "acquisition_range" => actual.AcquisitionRange, "attack_target" => actual.AttackTarget,
                    "attack_cd" => actual.AttackCooldown, "ai_state" => actual.AiState,
                    "waypoints" => actual.Waypoints.Select(p => P(p)).ToArray(), "target" => P(actual.Target),
                    "lane_waypoints" => actual.LaneWaypoints.Select(p => P(p)).ToArray(), "lane_done" => actual.LaneDone,
                    "last_anchor_time" => actual.LastAnchorTime,
                    "anchor_path" => actual.AnchorPath.Select(p => P(p)).ToArray(),
                    "sep_accum" => P(actual.SeparationAccumulation), "stuck_elapsed" => actual.StuckElapsed,
                    "repath_attempts" => actual.RepathAttempts, "repath_count" => actual.RepathCount,
                    "facing" => P(actual.Facing),
                    // Identity and buffs are supplied inputs; this harness does not tick buffs. The congestion_* fields are the deleted
                    // planner's (batch 279) and stay as recorded.
                    "netid" or "etype" or "unit_type" or "team" or "buffs" or "congestion_blockers" or "congestion_wait" or
                        "congestion_check_at" or "congestion_order" or "congestion_plan_cursor" or "congestion_attack_point" => field.Value,
                    _ => throw new InvalidOperationException("Unmapped minion output: " + field.Name),
                };
                CorpusSnapshot.Set(field.Value, value);
            }
            return;
        }
        var expectedPosition = Point(expected.GetProperty("pos"));
        TestAssert.Near(expectedPosition.X, actual.Position.X, 1e-7,
            $"{label} position X expected {expectedPosition.X:R} actual {actual.Position.X:R}");
        TestAssert.Near(expectedPosition.Z, actual.Position.Z, 1e-7,
            $"{label} position Z expected {expectedPosition.Z:R} actual {actual.Position.Z:R}");
        TestAssert.Equal(expected.GetProperty("ai_state").GetString(), actual.AiState, label + " state");
        TestAssert.Equal(expected.GetProperty("lane_done").GetBoolean(), actual.LaneDone, label + " lane_done");
        TestAssert.Equal(expected.GetProperty("repath_attempts").GetInt32(), actual.RepathAttempts, label + " repath attempts");
        TestAssert.Equal(expected.GetProperty("repath_count").GetInt32(), actual.RepathCount, label + " repath count");
        var expectedAnchor = expected.GetProperty("last_anchor_time").GetDouble();
        TestAssert.Near(expectedAnchor, actual.LastAnchorTime, 1e-7,
            $"{label} anchor time expected {expectedAnchor:R} actual {actual.LastAnchorTime:R}");
        var expectedStuck = expected.GetProperty("stuck_elapsed").GetDouble();
        TestAssert.Near(expectedStuck, actual.StuckElapsed, 1e-7,
            $"{label} stuck elapsed expected {expectedStuck:R} actual {actual.StuckElapsed:R}");
        ComparePoints(expected.GetProperty("waypoints"), actual.Waypoints, label + " waypoints");
        ComparePoints(expected.GetProperty("lane_waypoints"), actual.LaneWaypoints, label + " lane waypoints");
        ComparePoints(expected.GetProperty("anchor_path"), actual.AnchorPath, label + " anchor path");
        TestAssert.Equal(2, expected.GetProperty("sep_accum").GetArrayLength(), label + " separation point length");
        ComparePoint(expected.GetProperty("sep_accum"), actual.SeparationAccumulation, label + " separation");
        var expectedTarget = expected.GetProperty("target");
        if (expectedTarget.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual.Target is null, label + " target should be null");
        else
            ComparePoint(expectedTarget, actual.Target!.Value, label + " target");
        var expectedAttack = expected.GetProperty("attack_target");
        if (expectedAttack.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual.AttackTarget is null, label + " attack target should be null");
        else
        {
            var expectedAttackId = expectedAttack.GetUInt32();
            if (expectedAttackId == 0)
                TestAssert.True(actual.AttackTarget is null or 0,
                    label + " attack target sentinel should be cleared");
            else
                TestAssert.Equal(expectedAttackId, actual.AttackTarget!.Value,
                    label + " attack target");
        }
    }

    private static void CompareAiEvents(JsonElement expected, IReadOnlyList<AiEvent> actual, string label)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, actual.Select(e =>
            {
                var row = new Dictionary<string, object?> { ["kind"] = e.Kind == "stop-attack" ? "stopAttack" : e.Kind, ["netId"] = e.NetId };
                if (e.Points is not null) row["points"] = e.Points.Select(p => new[] { p.X, p.Z }).ToArray();
                return row;
            }).ToArray());
            return;
        }
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " event count");
        for (var index = 0; index < actual.Count; index++)
        {
            var expectedKind = expected[index].GetProperty("kind").GetString() switch
            {
                "stopAttack" => "stop-attack",
                var kind => kind,
            };
            TestAssert.Equal(expectedKind, actual[index].Kind, label + " event kind");
            TestAssert.Equal(expected[index].GetProperty("netId").GetUInt32(), actual[index].NetId, label + " event netId");
            if (actual[index].Kind != "waypoints") continue;
            ComparePoints(expected[index].GetProperty("points"), actual[index].Points!, label + " event points");
        }
    }

    private static IReadOnlyList<Position2> Points(JsonElement values) =>
        values.EnumerateArray().Select(Point).ToArray();

    private static Position2 Point(JsonElement value) =>
        new(value[0].GetDouble(), value[1].GetDouble());

    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual,
        string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " count");
        for (var index = 0; index < actual.Count; index++) ComparePoint(expected[index], actual[index], $"{label} {index}");
    }

    private static void ComparePoint(JsonElement expected, Position2 actual, string label)
    {
        TestAssert.Near(expected[0].GetDouble(), actual.X, 1e-6,
            $"{label} X expected {expected[0].GetDouble():R} actual {actual.X:R}");
        TestAssert.Near(expected[1].GetDouble(), actual.Z, 1e-6,
            $"{label} Z expected {expected[1].GetDouble():R} actual {actual.Z:R}");
    }

    private static MinionAiState ParseState(string state) => state switch
    {
        "march" => MinionAiState.March,
        "engage" => MinionAiState.Engage,
        // Batch 248: no return state (forward navigation is AI_ATTACKMOVESTATE); an old fixture's "return" reads as march.
        "return" => MinionAiState.March,
        _ => throw new InvalidOperationException($"Unknown fixture state {state}"),
    };

    private sealed record AiEvent(string Kind, uint NetId,
        IReadOnlyList<Position2>? Points, string? Reason = null);
    private sealed class RecordingPackets : IMinionPacketSink
    {
        private readonly List<AiEvent> _events;
        public RecordingPackets(List<AiEvent> events) => _events = events;
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) =>
            _events.Add(new AiEvent("waypoints", minion.NetId, waypoints.ToArray(), reason));
        public void SendStop(Entity minion, long syncId, string reason) =>
            _events.Add(new AiEvent("waypoints", minion.NetId, new[] { minion.Position }, reason));
        public void SendStopAttack(Entity minion, string reason)
        {
            if (CombatPackets.StopAttack(minion) is not null) _events.Add(new AiEvent("stop-attack", minion.NetId, null, reason));
        }
    }

    private static void CheckCompositions(JsonElement cases)
    {
        foreach (var item in cases.EnumerateArray())
        {
            var team = (Team)item.GetProperty("team").GetInt32();
            var downMask = item.GetProperty("downMask").GetInt32();
            var world = InhibitorWorld(downMask, team);
            // Map1's shipped GetMinionSpawnInfo (LevelScript.txt:1819-1915) answers this now.
            // One host per case: the script keeps LAST_WAVE and the cannon cadence in globals, and
            // the case's elapsed time drives that host's own IncreaseCannonMinionSpawnRate timer
            // (LevelScript.txt:3258-3260) instead of a clock comparison against 2090.
            var host = LevelScriptFixtures.Init(world);
            LevelScriptFixtures.FellInhibitors(host, world, team);
            host.Advance(0.0, 0.0);
            var elapsed = item.GetProperty("elapsed").GetDouble();
            if (elapsed > 0.0) host.Advance(elapsed, 0.0);
            var actual = host.GetMinionSpawnInfo(team,
                item.GetProperty("lane").GetString()!,
                item.GetProperty("wave").GetInt32()).Members;
            var expected = item.GetProperty("expected").EnumerateArray()
                .Select(value => (MinionType)value.GetInt32()).ToArray();
            if (FixtureRecorder.Enabled)
            {
                CorpusSnapshot.Set(item.GetProperty("expected"), actual.Select(type => (int)type).ToArray());
                continue;
            }
            TestAssert.Equal(expected, actual.ToArray(),
                $"wave composition case {item.GetProperty("wave").GetInt32()} " +
                $"{item.GetProperty("lane").GetString()} mask {downMask}");
        }
    }

    private static void CheckTargeting(JsonElement cases)
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var item in cases.EnumerateArray())
        {
            var world = new WorldState();
            foreach (var raw in item.GetProperty("units").EnumerateArray())
            {
                var kindName = raw.GetProperty("etype").GetString();
                var entity = new Entity(raw.GetProperty("netid").GetUInt32(),
                    EntityKindExtensions.FromSourceType(kindName),
                    (UnitType)raw.GetProperty("unit_type").GetInt32(),
                    (Team)raw.GetProperty("team").GetInt32(),
                    new Position2(raw.GetProperty("pos")[0].GetDouble(),
                        raw.GetProperty("pos")[1].GetDouble()))
                {
                    Alive = raw.GetProperty("alive").GetBoolean(),
                    Protected = raw.GetProperty("protected").GetBoolean(),
                    Radius = raw.GetProperty("radius").GetDouble(),
                    AttackRange = raw.GetProperty("atk_range").GetDouble(),
                    AcquisitionRange = raw.GetProperty("acquisition_range").GetDouble(),
                    Health = raw.GetProperty("hp").GetDouble(),
                    MaxHealth = raw.GetProperty("max_hp").GetDouble(),
                };
                if (raw.TryGetProperty("minion_type", out var minionType) &&
                    minionType.ValueKind != JsonValueKind.Null)
                    entity.MinionType = (MinionType)minionType.GetInt32();
                LoadBuffs(entity, raw);
                if (raw.TryGetProperty("attack_target", out var attackTarget) &&
                    attackTarget.ValueKind != JsonValueKind.Null)
                    entity.AttackTarget = attackTarget.GetUInt32();
                world.Register(entity);
            }

            var movement = new MovementSystem(world, grid);
            var ai = new MinionAiSystem(world, grid, movement,
                new FixtureCombat(), new FixturePackets(),
                new WorldMinionVisionService(world, grid, _ => Array.Empty<VisionProvider>()));
            var source = world.Get(item.GetProperty("source").GetUInt32())!;
            var actual = ai.SelectTarget(source)?.NetId;
            if (FixtureRecorder.Enabled)
            {
                CorpusSnapshot.Set(item.GetProperty("expected"), actual);
                continue;
            }
            var rawExpected = item.GetProperty("expected");
            uint? expected = rawExpected.ValueKind == JsonValueKind.Null ? null : rawExpected.GetUInt32();
            TestAssert.Equal(expected, actual,
                $"targeting {item.GetProperty("name").GetString()} reviewed priority");
        }
    }

    private static WorldState InhibitorWorld(int downMask, Team spawnedTeam)
    {
        var world = new WorldState();
        var enemy = MinionDefinitions.Opposing(spawnedTeam);
        for (var index = 0; index < MapDefinitions.Lanes.Count; index++)
        {
            var lane = MapDefinitions.Lanes[index];
            world.Register(new Entity((uint)(0x50000000 + index), EntityKind.Inhibitor,
                UnitType.Structure, enemy, Position2.Zero)
            {
                Name = $"Barracks_T{(enemy == Team.Order ? 1 : 2)}_{lane}1",
                Alive = (downMask & (1 << index)) == 0,
                Health = 4000.0,
                MaxHealth = 4000.0,
                Radius = 185.0,
            });
        }
        return world;
    }

    private static void Near(JsonElement expected, string property, double actual, JsonElement item)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected.GetProperty(property), actual);
            return;
        }
        TestAssert.Near(expected.GetProperty(property).GetDouble(), actual, 1e-9,
            $"minion stats case {item.GetProperty("downMask").GetInt32()} {property}");
    }

    private static Entity Add(WorldState world, uint id, EntityKind kind, UnitType unitType,
        Team team, Position2 position, double speed, double radius)
    {
        var entity = new Entity(id, kind, unitType, team, position)
        {
            Speed = speed,
            Radius = radius,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        world.Register(entity);
        return entity;
    }

    private static Entity AddStructure(WorldState world, uint id, Team team,
        EntityKind kind, UnitType unitType, Position2 position, double radius,
        bool alive = true, bool protectedUnit = false)
    {
        var entity = new Entity(id, kind, unitType, team, position)
        {
            Radius = radius,
            Alive = alive,
            Created = true,
            Protected = protectedUnit,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        world.Register(entity);
        return entity;
    }

    private static Entity AddMinion(WorldState world, uint id, Position2 position)
    {
        var entity = Add(world, id, EntityKind.Minion, UnitType.Minion,
            Team.Order, position, 325.0, 35.75);
        entity.MinionType = MinionType.Melee;
        entity.Alive = true;
        entity.Created = true;
        entity.AttackRange = 170.0;
        entity.AcquisitionRange = 450.0;
        return entity;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private sealed class NoRetainTransport : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) { }
    }

    private sealed class BasicAttackCaptureTransport : IReplicationTransport
    {
        public uint SourceNetId { get; set; }
        public double CurrentTime { get; set; }
        public List<double> Times { get; } = [];

        public void Send(PlaintextPacket packet)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 9 || bytes[0] != 0x0D)
                return;
            var source = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(1, 4));
            if (source == SourceNetId)
                Times.Add(CurrentTime);
        }
    }

    private sealed class ProductionTraceLog : ISessionLogSink
    {
        public List<TraceOrder> Orders { get; } = [];
        public int ProcessedOrders { get; set; }

        public void Clear()
        {
            Orders.Clear();
            ProcessedOrders = 0;
        }

        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (message != "fidelity_batch" || properties is null ||
                !properties.TryGetValue("orders", out var raw) ||
                raw is not JsonElement orders || orders.ValueKind != JsonValueKind.Array)
                return;
            foreach (var order in orders.EnumerateArray())
            {
                if (order.TryGetProperty("kind", out var kind) && kind.GetString() == "stop-attack") continue;
                if (!order.TryGetProperty("netid", out var netId) ||
                    !order.TryGetProperty("reason", out var reason) ||
                    !order.TryGetProperty("mono_ns", out var monoNs))
                    continue;
                Orders.Add(new TraceOrder(netId.GetUInt32(), reason.GetString() ?? "",
                    monoNs.GetInt64()));
            }
        }
    }

    private sealed record TraceOrder(uint NetId, string Reason, long MonotonicNanoseconds);

    private sealed record ProductionPlantRun(
        IReadOnlyList<ProductionPlantSample> Samples,
        IReadOnlyList<double> Orders,
        IReadOnlyList<string> OrderReasons,
        int AttackCount);

    private sealed record ProductionPlantSample(
        double Time,
        double Overshoot,
        bool Planted,
        bool InFireRange);

    private sealed record DeferredOverlap(
        uint Left,
        uint Right,
        uint TargetNetId,
        double Time,
        double LeftBeforeWindup,
        double? LeftAfterWindup,
        double RightBeforeWindup,
        double? RightAfterWindup,
        double Deadline);

    private sealed class FixtureCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive &&
            !target.Buffs.Any(buff => buff.Payload.Untargetable || buff.Payload.Invulnerable);

        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // playtest B4 #15 review, 27 Sept 2026: use the shared structure reach.
            CombatService.InRange(attacker, target);

        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class AlwaysVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class FixturePackets : IMinionPacketSink
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
        public int Waypoints { get; private set; }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) =>
            Waypoints++;
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }
}
