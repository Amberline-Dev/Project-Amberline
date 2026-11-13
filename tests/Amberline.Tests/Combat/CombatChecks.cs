using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;
using Amberline.Tests.Spells;

namespace Amberline.Tests;

public static class CombatChecks
{
    [Check("combat.fear-wander-replaces-finished-segment")]
    public static void FearWanderReplacesTheFinishedSegment()
    {
        var world = new WorldState();
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
        { ChampionName = "Yorick", Health = 1000, MaxHealth = 1000, Speed = 325 });
        hero.Buffs.Add(new BuffState("Fear", new BuffPayload { FearOrigin = hero.Position }, BuffType.Fear, 0, 10.0));
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
        var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
        var orders = new HeroAttackOrderSystem(world, movement, combat,
            new CapturingSpellEffectSink(new CapturingCombatPacketQueue()), GameData.Default, (_, _) => true,
            new NoSpellBuffs());
        orders.WanderFeared(hero, new Position2(7000, 7500));
        var first = hero.ForcedMove;
        TestAssert.True(first is { Effect: "Fear" }, "the first fear pulse starts a wander");
        movement.Tick(1.5);
        TestAssert.True(first!.Completed, "the first wander segment completes");
        orders.WanderFeared(hero, new Position2(7500, 7000));
        TestAssert.False(ReferenceEquals(first, hero.ForcedMove),
            "the next pulse replaces the finished segment: a feared hero keeps wandering for the whole fear");
        TestAssert.True(hero.ForcedMove is { Effect: "Fear" }, "the new wander is a fear move");
    }

    [Check("combat.b11-cancelled-windup-retarget-does-not-consume-period")]
    public static void CancelledWindupRetargetDoesNotConsumePeriod()
    {
        var world = new WorldState();
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
        { ChampionName = "Yorick", Health = 1000, MaxHealth = 1000, AttackDamage = 100,
            AttackRange = 125, AttackPeriod = 1.6, AttackWindup = 1.6 * (0.3 - 0.091666667) });
        var first = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7050, 7000))
        { Health = 1000, MaxHealth = 1000 });
        var second = world.Register(new Entity(3, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7000, 7050))
        { Health = 1000, MaxHealth = 1000 });
        var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
        var orders = new HeroAttackOrderSystem(world, new MovementSystem(world, NavigationGrid.LoadDefault()),
            combat, new CapturingSpellEffectSink(new CapturingCombatPacketQueue()), GameData.Default,
            (_, _) => true, new NoSpellBuffs());
        orders.StartAttack(hero, first);
        TestAssert.True(combat.QueueBasicAttack(hero, first));
        combat.CombatTick(.1, regenerate: false);
        TestAssert.True(hero.AttackCooldown > 1);
        orders.StartAttack(hero, second);
        TestAssert.Equal(0.0, hero.AttackCooldown, "retarget cancels the unfinished swing's period");
        TestAssert.Equal(0, combat.PendingHits.Count);
        combat.CombatTick(.01, regenerate: false);
        var replacement = combat.PendingHits.Single();
        TestAssert.Equal(second.NetId, replacement.TargetNetId);
        TestAssert.Equal(hero.AttackWindup, replacement.WindupRemaining, "replacement starts a full fresh windup");
        TestAssert.Equal(1000.0, first.Health, "canceled swing cannot hit the first target");
        TestAssert.Equal(1, combat.InterruptBasicAttack(hero), "movement uses the same cancellation path");
        TestAssert.True(combat.QueueBasicAttack(hero, first), "a movement cancellation also allows immediate restart");
    }

    [Check("combat.b11-released-attack-keeps-period-and-projectile")]
    public static void ReleasedAttackKeepsPeriodAndProjectile()
    {
        foreach (var ranged in new[] { false, true })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
            { ChampionName = ranged ? "Annie" : "Yorick", Health = 1000, MaxHealth = 1000,
                AttackDamage = 100, AttackRange = 125, AttackPeriod = 1.6, AttackWindup = .3,
                MissileSpeed = ranged ? 100 : 0 });
            var target = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7050, 7000))
            { Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
            TestAssert.True(combat.QueueBasicAttack(hero, target));
            combat.CombatTick(.31, regenerate: false);
            // Batch 741: the melee hit landed at its cast frame (this tick); only the missile is still in the queue.
            TestAssert.Equal(ranged ? 1000.0 : 900.0, target.Health, "a melee hit lands at its cast frame");
            var released = combat.PendingHits.SingleOrDefault();
            TestAssert.Equal(ranged ? 1 : 0, combat.PendingHits.Count);
            var cooldown = hero.AttackCooldown;
            TestAssert.Equal(0, combat.InterruptBasicAttack(hero), "released attacks cannot be canceled");
            TestAssert.Equal(released, combat.PendingHits.SingleOrDefault(), "the launched missile retains its identity");
            TestAssert.Equal(cooldown, hero.AttackCooldown);
            TestAssert.False(combat.QueueBasicAttack(hero, target));
            combat.CombatTick(.51, regenerate: false);
            TestAssert.Equal(900.0, target.Health, "released melee hit or projectile still lands");
            TestAssert.Equal(0, combat.InterruptBasicAttack(hero));
            TestAssert.True(hero.AttackCooldown > 0, "completed hit keeps the remainder of its period");
            TestAssert.False(combat.QueueBasicAttack(hero, target));
        }
    }

    [Check("combat.turret-death-single-native-announcement")]
    public static void StructureDeathsUseOnlyNativeAnnouncements()
    {
        var data = GameData.Default;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        foreach (var heroKiller in new[] { false, true })
        foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
        {
            var world = new WorldState();
            var killer = world.Register(new Entity(1, heroKiller ? EntityKind.Hero : EntityKind.Minion,
                heroKiller ? UnitType.Hero : UnitType.Minion, Team.Order, new(7000, 7000))
            { ChampionName = heroKiller ? "Yorick" : string.Empty, Health = 1000, MaxHealth = 1000 });
            var structure = world.Register(new Entity(2, kind,
                kind == EntityKind.Turret ? UnitType.Turret : UnitType.Structure, Team.Chaos, new(7050, 7000))
            { Health = 1000, MaxHealth = 1000 });
            var queue = new CapturingCombatPacketQueue();
            var combat = new CombatService(world, new TestHooks(),
                new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter()),
                new FixedRandomSource(.5));
            // Batch 314: the map script's HandleDestroyedObject arms the inhibitor's return (SetDisableMinionSpawn, LevelScript's 300 s);
            // this bare combat service has no map script, so the hook does what the script does.
            combat.BarracksChanged = (barracks, destroyed) => { if (destroyed) combat.ArmInhibitorReturn(barracks, 300.0); };
            TestAssert.True(combat.Kill(structure, killer.NetId));
            var opcode = kind == EntityKind.Turret ? (byte)0xA6 : (byte)0x8E;
            var death = queue.Packets.Single(packet => packet.Plaintext.Span[0] == opcode);
            TestAssert.Equal(structure.NetId, BinaryPrimitives.ReadUInt32LittleEndian(death.Plaintext.Span[1..]));
            TestAssert.Equal(killer.NetId, BinaryPrimitives.ReadUInt32LittleEndian(death.Plaintext.Span[5..]));
            TestAssert.False(queue.Packets.Any(packet => packet.Plaintext.Span[0] == 0x4A &&
                packet.Plaintext.Span[5] is 25 or 29 or 34 or 35), "death handlers own structure events");
            if (kind != EntityKind.Inhibitor) continue;
            TestAssert.Equal((byte)0, queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x2D).Plaintext.Span[7]);
            queue.Clear();
            combat.CombatTick(300.0, regenerate: false);
            combat.ProcessRespawns();
            TestAssert.Equal((byte)1, queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x2D).Plaintext.Span[7]);
            TestAssert.False(queue.Packets.Any(packet => packet.Plaintext.Span[0] == 0x4A), "alive state owns respawn event 27");
        }
    }

    [Check("minions-b6-turret-wards")]
    public static void TurretsExcludeVisionWardsButKeepRally()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var name in new[] { "SightWard", "VisionWard", "WriggleLantern" })
        foreach (var wireReady in new[] { true, false })
        {
            var world = new WorldState();
            var turret = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team, new(7000, 7000))
            { Health = 1000, MaxHealth = 1000, AttackRange = 775, AttackDamage = 100 });
            var enemy = MinionDefinitions.Opposing(team);
            var ward = world.Register(new Entity(2, EntityKind.Ward, UnitType.Monster, enemy, new(7010, 7000))
            { Name = name, IsWard = true, Health = 3, MaxHealth = 3 });
            var hero = world.Register(new Entity(3, EntityKind.Hero, UnitType.Hero, enemy, new(7300, 7000))
            { Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new TestHooks(),
                new TestTransport { WireReady = wireReady }, new FixedRandomSource(.5));
            TestAssert.False(combat.CanAttack(turret, ward), $"{team}/{name}: shared eligibility excludes wards");
            TestAssert.False(combat.QueueBasicAttack(turret, ward), "explicit attacks use the same gate");
            TestAssert.Equal(hero, combat.AcquireTurretTarget(turret), "nearer ward cannot beat an eligible champion");
            turret.AttackTarget = ward.NetId;
            combat.CombatTick(.01, regenerate: false);
            // Batch 141 (DATA/Scripts/Turret.luaobj OnTargetLost): the ward loss itself is still
            // immediate — the engine clears the id at once — but the replacement is only taken
            // while the state is AI_HARDIDLE_ATTACKING or AI_TAUNTED. A turret that just started
            // is AI_HARDIDLE, so it discards the champion the same search found and takes it at
            // the first 0.15 s TimerFindEnemies instead. Script = retail: the shipped rule is the
            // state gate; the hand-written machine replaced unconditionally.
            TestAssert.True(turret.AttackTarget is null, "retained ward is dropped at once");
            combat.CombatTick(.14, regenerate: false); // the first TimerFindEnemies deadline
            TestAssert.Equal((uint?)hero.NetId, turret.AttackTarget,
                "the discarded replacement is taken at the first scan");
            TestAssert.True(combat.PendingHits.All(hit => hit.TargetNetId != ward.NetId));
            TestAssert.Equal(3.0, ward.Health, "ward receives no turret damage");
            ward.Name = "SummonerRally";
            ward.IsSummonerBeacon = true;
            TestAssert.True(combat.CanAttack(turret, ward), "Rally retains its existing combat eligibility");
            TestAssert.Equal(ward, combat.AcquireTurretTarget(turret), "Rally remains in the fallback tier");
            turret.AttackTarget = ward.NetId;
            turret.AttackCooldown = 100;
            combat.CombatTick(.01, regenerate: false);
            TestAssert.Equal((uint?)ward.NetId, turret.AttackTarget, "Rally retention remains eligible");
        }
    }

    [Check("minions-b6-turret-scan")]
    public static void IdleTurretScanUsesRepeatingDeadline()
    {
        var world = new WorldState();
        var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
        Entity Turret(uint id, double x) => world.Register(new Entity(id, EntityKind.Turret,
            UnitType.Turret, Team.Order, new(x, 7000))
        { Health = 1000, MaxHealth = 1000, AttackRange = 775, AttackDamage = 10, AttackCooldown = 100 });
        var first = Turret(1, 7000);
        first.AttackCooldown = 0;
        var enemy = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(9000, 7000))
        { Health = 1000, MaxHealth = 1000 });
        combat.CombatTick(.15, regenerate: false); // first empty repeating scan
        enemy.Position = new(7100, 7000);
        combat.CombatTick(.01, regenerate: false);
        var second = Turret(3, 7000); // independent first deadline at .31
        TestAssert.True(first.AttackTarget is null && second.AttackTarget is null,
            "an entrant between deadlines cannot trigger an idle scan");
        TestAssert.True(combat.PendingHits.Count == 0, "idle turret has not fired early");
        combat.CombatTick(.139, regenerate: false);
        TestAssert.True(first.AttackTarget is null && second.AttackTarget is null);
        combat.CombatTick(.001, regenerate: false);
        TestAssert.Equal((uint?)enemy.NetId, first.AttackTarget, "repeating deadline remains at .30, not .31");
        TestAssert.True(second.AttackTarget is null, "new turret waits for its independent .31 deadline");
        TestAssert.Near(99.86, second.AttackCooldown, 1e-8, "cooldown processing runs between scans");
        // Batch 56 (fc6f501, Turret.luaobj): the search tick only sets the target; TurnOnAutoAttack comes from the next timer tick.
        TestAssert.True(combat.PendingHits.Count == 0, "a ready idle turret acquires at the deadline but swings from the next tick");
        var replacement = world.Register(new Entity(4, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new(7200, 7000)) { Health = 1000, MaxHealth = 1000 });
        enemy.Alive = false;
        combat.CombatTick(.01, regenerate: false);
        TestAssert.Equal((uint?)replacement.NetId, first.AttackTarget,
            "target-loss acquisition does not wait for the .45 idle deadline");
        TestAssert.Equal((uint?)replacement.NetId, second.AttackTarget, "second turret first scans at .31");
        var ally = world.Register(new Entity(5, EntityKind.Hero, UnitType.Hero, Team.Order, new(7100, 7000))
        { Health = 1000, MaxHealth = 1000 });
        var aggressor = world.Register(new Entity(6, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(7150, 7000))
        { Health = 1000, MaxHealth = 1000 });
        combat.ApplyHit(aggressor.NetId, ally.NetId, 1, DamageType.True, DamageSourceKind.Basic);
        TestAssert.Equal((uint?)aggressor.NetId, first.AttackTarget, "help retaliation stays immediate between scans");
        first.AttackCooldown = 0;
        combat.CombatTick(.01, regenerate: false);
        TestAssert.True(!combat.PendingHits.Any(hit => hit.SourceNetId == first.NetId),
            "a turret retargeted between timer ticks has no swing flag until its next tick");
        combat.CombatTick(.13, regenerate: false); // first's next timer tick, .45
        TestAssert.True(combat.PendingHits.Any(hit => hit.SourceNetId == first.NetId &&
            hit.TargetNetId == aggressor.NetId), "TurnOnAutoAttack at the next timer tick starts the swing");
        first.AttackCooldown = 0;
        combat.CombatTick(.01, regenerate: false); // .46, between timer ticks: the first swing lands, the second is queued
        var swing = 1000 - aggressor.Health;
        TestAssert.True(swing > 0, "the first swing landed");
        combat.CombatTick(.01, regenerate: false); // .47: the second swing lands
        TestAssert.Near(1000 - 2 * swing, aggressor.Health, 1e-9, "retained-target attacks do not wait for idle scans");
    }

    [Check("combat.lane-script-damage-matrix")]
    public static void LaneMinionScriptsGateDamageByAttackerAndTarget()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in Enum.GetValues<MinionType>())
        {
            var minion = runtime.Waves!.SpawnFixture(team, type, 0, "C", 0, 0);
            minion.Health = minion.MaxHealth = 10000;
            minion.Armor = 0;
            var enemy = MinionDefinitions.Opposing(team);
            foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
            {
                var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Structure),
                    kind, kind == EntityKind.Turret ? UnitType.Turret : UnitType.Structure,
                    enemy, new(7000, 7000)) { Health = 10000, MaxHealth = 10000 });
                foreach (var sourceKind in new[] { DamageSourceKind.Basic, DamageSourceKind.Spell })
                foreach (var damageType in new[] { DamageType.Physical, DamageType.True })
                {
                    target.Health = 10000;
                    TestAssert.True(runtime.Combat.ApplyHit(minion.NetId, target.NetId, 100,
                        damageType, sourceKind));
                    var expected = sourceKind != DamageSourceKind.Basic ? 100 :
                        type == MinionType.Siege && kind == EntityKind.Turret ? 200 :
                        type == MinionType.Super && kind != EntityKind.Turret ? 25 : 100;
                    TestAssert.Near(expected, 10000 - target.Health, 1e-8,
                        $"{team} {type} -> {kind} {sourceKind} {damageType}");
                    if (kind != EntityKind.Turret) continue;
                    minion.Health = 10000;
                    TestAssert.True(runtime.Combat.ApplyHit(target.NetId, minion.NetId, 100,
                        damageType, sourceKind));
                    TestAssert.Near(sourceKind == DamageSourceKind.Basic &&
                        type is MinionType.Siege or MinionType.Super ? 50 : 100,
                        10000 - minion.Health, 1e-8, $"{team} turret -> {type} {sourceKind} {damageType}");
                }
                runtime.World.Remove(target.NetId);
            }
            var ordinary = runtime.Waves!.SpawnFixture(enemy, MinionType.Melee, 0, "C", 0, 0);
            TestAssert.True(runtime.Combat.ApplyHit(ordinary.NetId, minion.NetId, 100,
                DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.Near(9800, minion.Health, 1e-8, "non-turret attacks do not receive TurretShield reduction");
            runtime.World.Remove(minion.NetId);
            runtime.World.Remove(ordinary.NetId);
        }
    }

    [Check("combat.b4-structure-edge-reach-preserves-unit-ranges")]
    public static void StructureEdgeReachPreservesUnitRanges()
    {
        foreach (var attackerKind in new[] { EntityKind.Hero, EntityKind.Minion })
        foreach (var targetKind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus,
                     EntityKind.Hero, EntityKind.Minion, EntityKind.Monster })
        {
            var attacker = new Entity(1, attackerKind,
                attackerKind == EntityKind.Hero ? UnitType.Hero : UnitType.Minion, Team.Order, Position2.Zero)
                { Radius = 65, AttackRange = 110 };
            var type = targetKind switch
            {
                EntityKind.Turret => UnitType.Turret,
                EntityKind.Inhibitor or EntityKind.Nexus => UnitType.Structure,
                EntityKind.Hero => UnitType.Hero,
                EntityKind.Monster => UnitType.Monster,
                _ => UnitType.Minion,
            };
            var target = new Entity(2, targetKind, type, Team.Chaos, Position2.Zero) { Radius = 90 };
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            var expected = type is UnitType.Turret or UnitType.Structure ? 265.0 : 240.0;
            TestAssert.Equal(expected, CombatService.AttackReach(attacker, target), $"{attackerKind}/{targetKind}");
            target.Position = new Position2(expected, 0);
            TestAssert.True(CombatService.InRange(attacker, target), "exact range boundary is admitted");
            target.Position = new Position2(expected + .01, 0);
            TestAssert.False(CombatService.InRange(attacker, target), "beyond range is rejected");
        }
    }

    [Check("combat.b4-gameplay-radius-boundary-matrix")]
    public static void GameplayRadiusBoundaryMatrix()
    {
        var grid = NavigationGrid.LoadDefault();
        var data = GameData.Default;
        var actors = new[] {
            ("Blitzcrank", EntityKind.Hero, UnitType.Hero, 80.0),
            ("Annie", EntityKind.Hero, UnitType.Hero, 55.0),
            ("LeeSin", EntityKind.Hero, UnitType.Hero, 65.0),
            ("Blue_Minion_Basic", EntityKind.Minion, UnitType.Minion, 48.0),
            ("Blue_Minion_MechCannon", EntityKind.Minion, UnitType.Minion, 65.0),
            ("LesserWraith", EntityKind.Neutral, UnitType.Monster, 50.0),
            ("AnnieTibbers", EntityKind.Pet, UnitType.Monster, 80.0),
        };
        foreach (var (name, kind, type, radius) in actors)
        foreach (var targetKind in new[] { EntityKind.Hero, EntityKind.Minion,
                     EntityKind.Neutral, EntityKind.Pet, EntityKind.Turret, EntityKind.Nexus })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var character = data.Champion(name);
            TestAssert.Near(radius, character.CombatRadius, 1e-5, name + " shipped gameplay radius");
            var world = new WorldState();
            var source = world.Register(new Entity(1, kind, type, MinionDefinitions.Opposing(team),
                new(6000, 7000)) {
                Radius = character.PathfindingRadius, CombatRadius = character.CombatRadius,
                SelectionRadius = character.SelectionRadius, AttackRange = character.AttackRange,
                Speed = 325, Health = 1000, MaxHealth = 1000, AttackDamage = 20,
                AttackPeriod = 10, AttackWindup = .2,
            });
            var targetType = targetKind switch {
                EntityKind.Hero => UnitType.Hero, EntityKind.Minion => UnitType.Minion,
                EntityKind.Turret => UnitType.Turret, EntityKind.Nexus => UnitType.Structure,
                _ => UnitType.Monster,
            };
            var targetRadius = targetKind switch {
                EntityKind.Turret => MapDefinitions.TurretRadius,
                EntityKind.Nexus => MapDefinitions.NexusRadius,
                EntityKind.Minion => 48.0, EntityKind.Neutral => 50.0,
                EntityKind.Pet => 80.0, _ => 65.0,
            };
            var target = world.Register(new Entity(2, targetKind, targetType, team, new(7500, 7000)) {
                Radius = targetType is UnitType.Structure or UnitType.Turret ? targetRadius : 31,
                CombatRadius = targetRadius, SelectionRadius = 155,
                PathfindingRadius = targetKind == EntityKind.Nexus
                    ? (team == Team.Order ? MapDefinitions.OrderNexusPathfindingRadius : MapDefinitions.ChaosNexusPathfindingRadius)
                    : 37,
                Health = 1000, MaxHealth = 1000,
            });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
            var originalRange = source.AttackRange;
            var baseReach = originalRange + radius + targetRadius;
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            foreach (var (attackerDelta, targetDelta) in new[] { (0.0, 0.0), (13.0, 0.0), (0.0, 17.0) })
            {
                source.CombatRadius = radius + attackerDelta;
                target.CombatRadius = targetRadius + targetDelta;
                var reach = baseReach + attackerDelta + targetDelta;
                TestAssert.Near(reach, CombatService.AttackReach(source, target), 1e-8);
                foreach (var epsilon in new[] { -.01, 0.0, .01 })
                {
                    // Use an origin to keep the equality probe free of world-coordinate rounding.
                    source.Position = Position2.Zero;
                    target.Position = new(reach + epsilon, 0);
                    TestAssert.Equal(epsilon <= 0, combat.CanAttack(source, target),
                        $"{name}/{team}/{targetKind}: delta {attackerDelta}/{targetDelta}, epsilon {epsilon}");
                }
                source.Radius += 1;
                target.PathfindingRadius += 1;
                target.SelectionRadius += 1;
                TestAssert.Near(reach, CombatService.AttackReach(source, target), 1e-8,
                    "movement and selection geometry do not change basic reach");
            }
            source.CombatRadius = radius;
            target.CombatRadius = targetRadius;
            source.Position = new(6000, 7000);
            target.Position = new(7500, 7000);
            var movement = new MovementSystem(world, grid);
            var plan = movement.ApproachWithin(source, target, baseReach - 6, baseReach);
            TestAssert.Equal(MovementSystem.MoveStatus.Moving, plan.Status, name + " approach");
            TestAssert.True(movement.ApproachPathUsable(plan.Path, source, target, baseReach));
            source.Position = plan.Path[^1];
            TestAssert.True(combat.QueueBasicAttack(source, target, new AttackRolls(1, 1, 1)),
                name + " endpoint admits an attack");
            combat.CombatTick(.2, regenerate: false);
            combat.CombatTick(.01, regenerate: false);
            TestAssert.True(target.Health < 1000, name + " endpoint permits a complete attack");
            foreach (var escape in new[] { false, true })
            {
                source.Position = Position2.Zero;
                target.Position = new(baseReach, 0);
                source.AttackCooldown = 0;
                TestAssert.True(combat.QueueBasicAttack(source, target, new AttackRolls(1, 1, 1)));
                target.Position = new(baseReach + 100, 0);
                TestAssert.True(combat.InCommittedAttackRange(source, target), "committed equality is admitted");
                target.Position = new(baseReach + 100 + (escape ? .01 : -.01), 0);
                TestAssert.Equal(!escape, combat.InCommittedAttackRange(source, target));
                var health = target.Health;
                combat.CombatTick(.2, regenerate: false);
                combat.CombatTick(.01, regenerate: false);
                TestAssert.Equal(!escape, target.Health < health, "only the retained swing completes");
            }
            TestAssert.Equal(originalRange, source.AttackRange, "the replicated range stat is unchanged");
        }
    }

    [Check("combat.b4-gameplay-radius-spawn-propagation")]
    public static void GameplayRadiusSpawnPropagation()
    {
        var data = GameData.Default;
        var grid = NavigationGrid.LoadDefault();
        TestAssert.Equal(0.0, data.Champion("LeeSin").GameplayRadius, "absent raw radius remains absent");
        TestAssert.Equal(65.0, data.Champion("LeeSin").CombatRadius, "combat alone resolves the default");
        TestAssert.Equal(1.0, data.Champion("SightWard").CombatRadius, "boolean-packed radius is not absent");
        foreach (var name in ChampionCatalog.PlayableNames)
        {
            using var runtime = MatchRuntime.CreateProduction(grid, data, orderChampion: name);
            runtime.Session.CreateWorld();
            var hero = runtime.World.Heroes().First(unit => unit.Team == Team.Order);
            // Amumu's Amumu.inibin stores Data*GameplayCollisionRadius 55.0 like Annie's (decoded standalone, 19 October 2026); Alistar's stores
            // 80.0 like Yorick's and Blitzcrank's (decoded standalone, 19 October 2026: a string entry "80.0", as the other two).
            // Every later champion (Cho'Gath, Malphite: 80.0) is pinned to its own shipped radius, or the 65 default when it stores none.
            var shipped = data.Champion(name).GameplayRadius;
            var expected = name switch { "Annie" or "Amumu" => 55.0, "Yorick" or "Blitzcrank" or "Alistar" => 80.0, _ => shipped > 0.0 ? shipped : 65.0 };
            // A radius other than the default is the shipped one: the pin names the data it follows, not only the spawn that reads it.
            if (expected != 65.0) TestAssert.Equal(expected, data.Champion(name).GameplayRadius, name + " shipped Data*GameplayCollisionRadius");
            TestAssert.Equal(expected, hero.CombatRadius, name + " spawn");
            TestAssert.Equal(data.Champion(name).PathfindingRadius, hero.Radius, "pathfinding radius unchanged");
            TestAssert.Equal(data.Champion(name).AttackRange, hero.AttackRange, "range stat unchanged");
            foreach (var kind in new[] { CloneKind.ShacoClone, CloneKind.YorickRevenant })
            {
                hero.CombatRadius = expected + 7;
                var clone = runtime.Clones.Spawn(hero, hero, hero.Position, kind,
                    hero.Health, "Hallucinate", 10, name);
                TestAssert.Equal(expected + 7, clone.CombatRadius, "clones copy the live combat radius");
                TestAssert.Equal(hero.Radius, clone.Radius);
            }
            if (name == "Yorick")
                foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
                {
                    var ghoul = YorickScriptSupport.SpawnUnit(runtime, hero, kind, hero.Position, 0);
                    TestAssert.Equal(1.0, ghoul.CombatRadius, kind + " shipped gameplay radius");
                    TestAssert.Equal(40.0, ghoul.Radius, kind + " pathfinding radius is independent");
                }
            if (name != "Annie") continue;
            foreach (var team in new[] { Team.Order, Team.Chaos })
            foreach (var type in Enum.GetValues<MinionType>())
            {
                var minion = runtime.Waves!.SpawnFixture(team, type, 0, "C", 0, 0);
                TestAssert.Equal(type is MinionType.Melee or MinionType.Caster ? 48.0 : 65.0,
                    minion.CombatRadius, $"{team}/{type} spawn");
            }
            foreach (var structure in runtime.World.Entities.Values.Where(unit =>
                         unit.UnitType is UnitType.Turret or UnitType.Structure))
                TestAssert.Equal(structure.Radius, structure.CombatRadius, "structure attack geometry is retained");
        }
        var world = new WorldState();
        var jungle = new JungleScheduler(world, data, campIds: [3, 9]);
        jungle.Start();
        jungle.Tick(NeutralDefinitions.InitialSpawnSeconds);
        TestAssert.True(world.Entities.Count > 0);
        foreach (var monster in world.Entities.Values)
            TestAssert.Equal(50.0, monster.CombatRadius, "both wraith camps use gameplay radius");
    }

    [Check("combat.b4-turret-attacker-retains-admission")]
    public static void TurretAttackerRetainsAdmission()
    {
        var turret = new Entity(1, EntityKind.Turret, UnitType.Turret, Team.Order, Position2.Zero)
            { AttackRange = 750, Radius = 88.4, CombatRadius = 88.4 };
        foreach (var type in new[] { UnitType.Hero, UnitType.Minion, UnitType.Turret, UnitType.Structure })
        {
            var target = new Entity { UnitType = type, Radius = 31, CombatRadius = 80 };
            var expected = 750 + 31 + (type is UnitType.Turret or UnitType.Structure ? 88.4 : 3);
            TestAssert.Equal(expected, CombatService.AttackReach(turret, target));
            target.CombatRadius += 50;
            turret.CombatRadius += 50;
            TestAssert.Equal(expected, CombatService.AttackReach(turret, target));
            target.Position = new(expected, 0);
            TestAssert.True(CombatService.InRange(turret, target));
            target.Position = new(expected + .01, 0);
            TestAssert.False(CombatService.InRange(turret, target));
        }
    }

    [Check("combat.b4-inhibitor-replay-projection-order")]
    public static void InhibitorReplayProjectionKeepsNativeDurationAndState()
    {
        var cipher = LegacyBlowfish.CreateDefault();
        var packet = CombatPackets.DampenerState(0xFF000077, 300, dead: true);
        var plain = packet.Plaintext.ToArray();
        TestAssert.Equal("2D770000FF2C0100", Convert.ToHexString(plain));
        TestAssert.Equal((ushort)300, BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(5)));
        TestAssert.Equal((byte)0, plain[7]);
        TestAssert.Equal(plain, cipher.Decrypt(packet.ToWireBytes(cipher)));
    }

    [Check("combat.inhibitor-retail-five-minute-recovery")]
    public static void InhibitorKeepsSupersUntilFiveMinutes()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            // The super is the script's answer, and only the barracks edge tells it:
            // ApplyBarracksDestructionBonuses (LevelScript.txt:2405) sets the beneficiary lane's
            // WillSpawnSuperMinion, and ApplyBarracksRespawnReductions (LevelScript.txt:2747) is
            // what clears it again. Drive the same hook the composition root wires up.
            var level = LevelScriptFixtures.Init(world);
            var inhibitor = world.Register(new Entity(1, EntityKind.Inhibitor, UnitType.Structure,
                team, Position2.Zero) { Name = $"Barracks_T{(team == Team.Order ? 1 : 2)}_C1",
                    Health = 4000, MaxHealth = 4000 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            combat.BarracksChanged = (barracks, destroyed) =>
            {
                if (destroyed) level.OnBarracksFell(barracks);
                else level.OnBarracksRestored(barracks);
            };
            // Batch 314: the 300 s is the script's own countdown (DisableBarracksSpawn -> SetDisableMinionSpawn).
            level.DisableMinionSpawn = combat.ArmInhibitorReturn;
            TestAssert.True(combat.Kill(inhibitor, 0));
            TestAssert.Equal(300.0, inhibitor.RespawnAt - combat.CurrentTime);
            combat.CombatTick(240, regenerate: false);
            combat.ProcessRespawns();
            TestAssert.False(inhibitor.Alive);
            TestAssert.True(level.GetMinionSpawnInfo(MinionDefinitions.Opposing(team), "C", 2)
                .Members.Contains(MinionType.Super));
            combat.CombatTick(59.999, regenerate: false);
            combat.ProcessRespawns();
            TestAssert.False(inhibitor.Alive);
            combat.CombatTick(0.001, regenerate: false);
            combat.ProcessRespawns();
            TestAssert.True(inhibitor.Alive);
            TestAssert.Equal(4000.0, inhibitor.Health);
            TestAssert.False(level.GetMinionSpawnInfo(MinionDefinitions.Opposing(team), "C", 2)
                .Members.Contains(MinionType.Super));
        }
    }

    // Batch 183 follow-up (finding 7). The shipped Turret.luaobj asks FindTargetInAcR for a new target and TargetInAttackRange for
    // the held one; a turret swings only inside its attack reach. Acquisition therefore must never reach past attack reach. The
    // shipped inibins (through GameData.Default, both keys stored for every turret) and the engine's StatsFor agree that the two
    // ranges are equal for all ten turret characters, so the band the acquisition ladder could widen into is empty.
    [Check("combat-turret-acquisition-never-exceeds-attack-reach")]
    public static void TurretAcquisitionNeverExceedsAttackReach()
    {
        var rows = new (TurretTier Tier, Team Team, string Character, double ShippedRange, double EngineRange)[]
        {
            (TurretTier.Outer, Team.Order, "OrderTurretNormal", 750.0, 750.0),
            (TurretTier.Outer, Team.Chaos, "ChaosTurretWorm", 750.0, 750.0),
            (TurretTier.Inner, Team.Order, "OrderTurretNormal2", 750.0, 750.0),
            (TurretTier.Inner, Team.Chaos, "ChaosTurretWorm2", 750.0, 750.0),
            (TurretTier.Inhibitor, Team.Order, "OrderTurretDragon", 750.0, 750.0),
            (TurretTier.Inhibitor, Team.Chaos, "ChaosTurretGiant", 750.0, 750.0),
            (TurretTier.Nexus, Team.Order, "OrderTurretAngel", 750.0, 750.0),
            (TurretTier.Nexus, Team.Chaos, "ChaosTurretNormal", 750.0, 750.0),
            // Batch 204: the fountain shrines engage at their shipped 2250 (Data*AttackRange and Data*AcquisitionRange, read
            // like the lane turrets'). The owner's 1250 of batch 11 is withdrawn by the owner decision of 25 October 2026.
            (TurretTier.Fountain, Team.Order, "OrderTurretShrine", 2250.0, 2250.0),
            (TurretTier.Fountain, Team.Chaos, "ChaosTurretShrine", 2250.0, 2250.0),
        };
        foreach (var (tier, team, name, shipped, engine) in rows)
        {
            TestAssert.Equal(name, MapDefinitions.TurretCharacter(tier, team), "the map's character for this tier and team");
            var champion = GameData.Default.Champion(name);
            TestAssert.True(champion.Raw.ContainsKey(InibinReader.KeyHash("Data*AttackRange")) &&
                champion.Raw.ContainsKey(InibinReader.KeyHash("Data*AcquisitionRange")),
                $"{name} stores both ranges (a missing AcquisitionRange would read the loader's 750 default)");
            TestAssert.Equal(shipped, champion.AttackRange, $"{name} shipped AttackRange");
            TestAssert.Equal(shipped, champion.AcquisitionRange, $"{name} shipped AcquisitionRange");
            var stats = TurretRules.StatsFor(tier, team, GameData.Default);
            TestAssert.Equal(engine, stats.AttackRange, $"{name} engine AttackRange");
            TestAssert.Equal(engine, stats.AcquisitionRange, $"{name} engine AcquisitionRange never exceeds its attack reach");
        }
    }

    [Check("combat.b7-dev02-shrine-ranges")]
    public static void ShrineUsesReconstructedAttackAndAcquisitionRanges()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var definition = MapDefinitions.Turrets.Single(t => t.Team == team && t.Tier == TurretTier.Fountain);
            var shrine = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team,
                new(7000, 7000)) { Name = definition.Name });
            TurretRules.Initialize(shrine, 1, GameData.Default);
            // DEV-02: both shipped shrine inibins explicitly store 2250 for both fields.
            var shipped = GameData.Default.Champion(team == Team.Order ? "OrderTurretShrine" : "ChaosTurretShrine");
            TestAssert.Equal(2250.0, shipped.AttackRange, "shipped data fact, not the effective laser range");
            TestAssert.Equal(2250.0, shipped.AcquisitionRange);
            // Batch 204: the effective ranges are the shipped 2250 (they were the owner's 1250 until batch 204).
            TestAssert.Equal(2250.0, shrine.AttackRange);
            TestAssert.Equal(2250.0, shrine.AcquisitionRange);
            TestAssert.Equal(2250.0, TurretRules.StatsFor(TurretTier.Fountain, team, GameData.Default).AttackRange);
            TestAssert.Equal(2250.0, TurretRules.StatsFor(TurretTier.Fountain, team, GameData.Default).AcquisitionRange);
            // Batch 527: the shrine spell is CastType 0, so its stored MissileSpeed 500 is no flight.
            TestAssert.Equal(0.0, shrine.MissileSpeed);
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                MinionDefinitions.Opposing(team), new(8200, 7000))
                { Health = 5000, MaxHealth = 5000, Armor = 1000, Radius = 65 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
            TestAssert.Equal(target, combat.AcquireTurretTarget(shrine), "visible enemy at 1200 is acquired");
            TestAssert.True(combat.QueueBasicAttack(shrine, target, new AttackRolls(1, 1, 1)));
            shrine.AttackCooldown = 100;
            // The shrine's client-decoded windup is 0.16 s: the tick that crosses it lands the hit, which has no flight (batch 741: a
            // CastType 0 attack's effect runs at its cast frame; it landed on the next tick before, and in the first tick at windup 0).
            combat.CombatTick(4, regenerate: false);
            combat.CombatTick(0.05, regenerate: false);
            TestAssert.Equal(4000.0, target.Health, "shrine retains 1000 true damage");
            // Batch 354 (owner, 26 October 2026): the shipped 2250 admission (target radius and three units) is capped by the shrine's
            // perception bubble, 1350 centre to centre (CombatService.FountainSightCap).
            target.Position = new(7000 + 1350, 7000);
            TestAssert.True(combat.CanAttack(shrine, target), "the bubble boundary is inclusive");
            target.Position = new(7000 + 1351, 7000);
            TestAssert.False(combat.CanAttack(shrine, target));
            TestAssert.True(combat.AcquireTurretTarget(shrine) is null);
            shrine.AttackCooldown = 0;
            TestAssert.False(combat.QueueBasicAttack(shrine, target));
        }
    }

    // Batch 354 (owner, 26 October 2026): the boundary is the shrine's perception bubble, 1350 centre to centre, inside its shipped 2250
    // reach (the check was combat.shrine-1250-boundary, then combat.shrine-2250-boundary in batch 204).
    [Check("combat.shrine-perception-bubble-boundary")]
    public static void ShrineRangeBoundaryGatesAcquisitionRetentionAndRetaliation()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var retaliation in new[] { false, true })
        {
            var world = new WorldState();
            var definition = MapDefinitions.Turrets.Single(t => t.Team == team && t.Tier == TurretTier.Fountain);
            var shrine = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team,
                definition.Position) { Name = definition.Name });
            TurretRules.Initialize(shrine, 1, GameData.Default);
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                MinionDefinitions.Opposing(team), shrine.Position)
                { Health = 5000, MaxHealth = 5000, Radius = 65 });
            var ally = world.Register(new Entity(3, EntityKind.Hero, UnitType.Hero, team,
                shrine.Position) { Health = 5000, MaxHealth = 5000 });
            var transport = new TestTransport();
            var combat = new CombatService(world, new TestHooks(), transport, new FixedRandomSource(.5));
            var direction = team == Team.Order ? 1 : -1;
            // Batch 354: the bubble (1350) caps the shipped AttackRange (2250, with the target-radius and +3 admission), so one unit
            // past it is still inside the shipped reach and only the cap refuses it.
            var reach = Amberline.Network.Replication.VisibilityRules.TerrainVisionRange(shrine);
            TestAssert.Equal(1350.0, reach, "the shrine's CharacterData PerceptionBubbleRadius (absent, default 1350)");
            TestAssert.True(reach + 1 < shrine.AttackRange + target.Radius + 3, "the cap, not the shipped range, sets the boundary");
            target.Position = shrine.Position + new Position2(direction * (reach + 1), 0);
            TestAssert.False(combat.CanAttack(shrine, target));
            TestAssert.True(combat.AcquireTurretTarget(shrine) is null);
            TestAssert.False(combat.QueueBasicAttack(shrine, target));
            if (retaliation)
                TestAssert.True(combat.ApplyHit(target.NetId, ally.NetId, 1, DamageType.True, DamageSourceKind.Basic));
            combat.CombatTick(.15, regenerate: false);
            TestAssert.True(shrine.AttackTarget is null, "outside the boundary neither scanning nor help assigns a target");
            TestAssert.Equal(0, transport.TurretTargets.Count, "no outside-range beam assignment");
            TestAssert.Equal(0, combat.PendingHits.Count);
            TestAssert.Equal(5000.0, target.Health);

            // One unit inside also exercises the shared three-unit admission allowance for help calls.
            target.Position = shrine.Position + new Position2(direction * (reach - 1), 0);
            TestAssert.Equal(target, combat.AcquireTurretTarget(shrine));
            if (retaliation)
            {
                ally.HelpCallRemaining = 0;
                TestAssert.True(combat.ApplyHit(target.NetId, ally.NetId, 1, DamageType.True, DamageSourceKind.Basic));
                TestAssert.Equal((uint?)target.NetId, shrine.AttackTarget, "help assigns the in-range target before a scan");
                TestAssert.Equal((shrine.NetId, (uint?)target.NetId), transport.TurretTargets.Last());
            }
            combat.CombatTick(.15, regenerate: false);
            TestAssert.Equal((uint?)target.NetId, shrine.AttackTarget);
            TestAssert.Equal((shrine.NetId, (uint?)target.NetId), transport.TurretTargets.Last());
            if (!retaliation)
            {
                // Batch 56 (fc6f501, Turret.luaobj): a target found by the 0.15 s search is swung at from the next timer
                // tick (TurnOnAutoAttack); one assigned by help already has this tick as its next timer tick.
                TestAssert.Equal(0, combat.PendingHits.Count, "the acquiring tick does not swing");
                combat.CombatTick(.15, regenerate: false);
            }
            TestAssert.Equal(1, combat.PendingHits.Count);
            shrine.AttackCooldown = 100;
            // 0.16 s client-decoded shrine windup (see combat.shrine-data-stats-and-client-cadence).
            combat.CombatTick(shrine.AttackWindup, regenerate: false);
            combat.CombatTick(.05, regenerate: false);
            TestAssert.Equal(4000.0, target.Health, "inside the boundary the laser hits for 1000 true damage");
            TestAssert.Equal(0, combat.PendingHits.Count);

            target.Position = shrine.Position + new Position2(direction * (reach + 1), 0);
            // Batch 56 (fc6f501, Turret.luaobj): leaving range is not a lost target. The next 0.15 s
            // scan drops to AI_HARDIDLE and stops the swing (TurnOffAutoAttack MOVING) but never calls
            // SetTarget, so the id survives — classified difference 2 (script = retail, the hand-written
            // machine cleared it here). Every 0.15 s step contains exactly one timer tick.
            combat.CombatTick(.15, regenerate: false);
            TestAssert.Equal((uint?)target.NetId, shrine.AttackTarget,
                "Turret.luaobj keeps the target id across the range drop (NOT the hand-written clear)");
            TestAssert.Equal((shrine.NetId, (uint?)null), transport.TurretTargets.Last(),
                "range exit clears the beam: TurnOff retires the client's display, not the id");
            if (retaliation)
            {
                ally.HelpCallRemaining = 0;
                TestAssert.True(combat.ApplyHit(target.NetId, ally.NetId, 1, DamageType.True, DamageSourceKind.Basic));
                // The engine's own reach gate drops the call before either machine is asked: the escaped
                // aggressor stands past AttackReach, so the retained id is never reassigned to it.
                TestAssert.Equal((uint?)target.NetId, shrine.AttackTarget,
                    "help cannot reacquire the escaped target (the reach gate drops the call)");
                TestAssert.Equal((shrine.NetId, (uint?)null), transport.TurretTargets.Last());
            }
            combat.CombatTick(.15, regenerate: false);
            TestAssert.Equal(4000.0, target.Health, "no further damage outside reach");
        }
    }

    [Check("combat.b7-dev04-fountain-radius")]
    public static void FountainHealsThroughShippedRadius()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var distance in new[] { 1050.0, 1100.0, 1101.0 })
        {
            var fountain = team == Team.Order ? new Position2(25.902751922607422, 263.5519104003906) : new Position2(13933.7890625, 14170.09375); // Order: exact __Spawn_T1.SCB point (owner decision, 18 October 2026)
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team,
                new(fountain.X + distance, fountain.Z))
                { Health = 100, MaxHealth = 1000, Mana = 100, MaxMana = 800, HealthRegen = 2, ManaRegen = 3 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
            combat.CombatTick(1);
            // Constants.var: radius 1100, one-second tick, 8.5% of maximum HP/MP.
            TestAssert.Near(distance <= 1100 ? 187 : 102, hero.Health, 1e-8);
            TestAssert.Near(distance <= 1100 ? 171 : 103, hero.Mana, 1e-8);
        }
    }

    [Check("combat.b7-dev05-ambient-cadence")]
    public static void AmbientGoldUsesShippedSpendableCadence()
    {
        // Batch 161 (retail footage over the Constants.var comment — the Xerath champion spotlight, October 2011):
        // the HUD gold rises one gold at a time about once a second, never in 6-7 gold jumps, so ambient income is
        // continuous at the shipped rate ai_AmbientGoldAmount / ai_AmbientGoldInterval (6.5 / 5 s = 1.3 g/s) from
        // ai_AmbientGoldDelay, including while dead (Map1's ai_DisableAmbientGoldWhileDead is 0).
        foreach (var alive in new[] { true, false })
        foreach (var coarse in new[] { true, false })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
                { Alive = alive, Health = alive ? 100 : 0, MaxHealth = 100, Gold = 475 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(.5));
            combat.CombatTick(89, regenerate: false);
            TestAssert.Near(475.0, hero.Gold, 1e-8);
            if (coarse) combat.CombatTick(6, regenerate: false);
            else
            {
                for (var second = 90; second <= 95; second++)
                {
                    combat.CombatTick(1, regenerate: false);
                    TestAssert.Near(475.0 + 1.3 * (second - 90), hero.Gold, 1e-8, $"spendable gold at {second}");
                    combat.CombatTick(0, regenerate: false);
                    TestAssert.Near(475.0 + 1.3 * (second - 90), hero.Gold, 1e-8, "no income at a zero-length step");
                }
            }
            TestAssert.Near(481.5, hero.Gold, 1e-8, "five seconds of continuous income including while dead");
            TestAssert.Equal(alive, hero.Alive);
        }
    }

    [Check("combat.shrine-respects-invulnerability-owner-finding-37")]
    public static void ShrineImpactIsStoppedByTheSameInvulnerabilityEveryOtherHitIs()
    {
        // Batch 227 (owner request, 25 October 2026) reverses owner finding #37 on the 2011 record; the check keeps its name, its body
        // now states the 2011 rule. Patch V1.0.0.108 (4 January 2011, the League wiki's patch page): "Shrine (spawn)
        // turrets now ignore all resistances and immunities". A forum thread of 8 January 2011 describes the result: Tryndamere pops his
        // ultimate, jumps into the fountain and is killed at once. The shipped OrderTurretShrineBasicAttack.luaobj and
        // ChaosTurretShrineBasicAttack.luaobj (identical in their bytecode) state the hit: TargetExecute runs BBIf Target CO_IS_TYPE_HERO ->
        // BBApplyDamage 1000 TRUE_DAMAGE DAMAGESOURCE_INTERNALRAW, else the same amount DAMAGESOURCE_RAW.
        //
        // So the shrine's InternalRaw hit ignores Kayle's Intervention (its invulnerability). A plain RAW hit still meets the ordinary gate.
        // Batch 230 narrowed the rule to the fountain shrine: another unit's InternalRaw hit is refused by invulnerability (the Nidalee check),
        // and ApplyHit's self-kill exemption (source == target) still covers Bushwhack's trap as before batch 227.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var definition = MapDefinitions.Turrets.Single(t => t.Team == team && t.Tier == TurretTier.Fountain);
            var shrine = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team, new(7000, 7000))
                { Name = definition.Name });
            TurretRules.Initialize(shrine, 1, GameData.Default);
            shrine.AttackDamage = 17; // the script's 1000 is independent of AD
            var hero = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                MinionDefinitions.Opposing(team), new(7200, 7000))
                { Health = 5000, MaxHealth = 5000, Armor = 1000, SpellBlock = 1000, ChampionName = "Kayle" });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));

            // Kayle's Intervention: Spells/JudicatorIntervention.luaobj sets SetInvulnerable on its target, which
            // BbBuffRuntime.ApplyStatus carries as BuffPayload.Invulnerable.
            hero.Buffs.Add(new BuffState("JudicatorIntervention", new BuffPayload { Invulnerable = true },
                BuffType.Invulnerability, slot: 0, expiresAt: 2.0, casterNetId: hero.NetId));
            TestAssert.True(combat.ApplyHit(shrine.NetId, hero.NetId, 1000.0, DamageType.True,
                DamageSourceKind.InternalRaw), "the shrine's InternalRaw branch ignores Kayle's Intervention (batch 227)");
            TestAssert.Equal(4000.0, hero.Health, "1000 true damage through the invulnerability, ignoring the 1000 armor and spell block");
            hero.Health = 5000.0;
            // The RAW branch is the ordinary hit: the same invulnerability still refuses it.
            TestAssert.False(combat.ApplyHit(shrine.NetId, hero.NetId, 1000.0, DamageType.True,
                DamageSourceKind.Raw), "the RAW branch meets the ordinary invulnerability gate");
            TestAssert.Equal(5000.0, hero.Health, "and is refused");
            hero.Buffs.Clear();
            TestAssert.True(combat.ApplyHit(shrine.NetId, hero.NetId, 1000.0, DamageType.True,
                DamageSourceKind.InternalRaw), "an unprotected hero takes the shrine's hit");
            TestAssert.Equal(4000.0, hero.Health, "the shrine's 1000 true damage with no armor reduction");
            // Tryndamere's Undying Rage and Zilean's Chrono Shift are BuffOnPreDamage hooks, which the fountain shrine's hit skips (batch 227, narrowed in 230).
            // This unit fixture runs no scripts, so the hit lands here; the scripted checks pin the clamp and the revive.
            hero.Buffs.Add(new BuffState("UndyingRage", new BuffPayload(), BuffType.CombatEnhancer,
                slot: 0, expiresAt: 5.0, casterNetId: hero.NetId));
            TestAssert.True(combat.ApplyHit(shrine.NetId, hero.NetId, 1000.0, DamageType.True,
                DamageSourceKind.InternalRaw), "Undying Rage sets no invulnerability, so the InternalRaw hit lands");
            TestAssert.Equal(3000.0, hero.Health, "and lands in full");
            // Batch 230 (narrowed 227): only the shrine's InternalRaw hit ignores immunities. Another unit's (here the hero's own self kill) is refused
            // by a magic immunity as before batch 227. Reconstruction, the same evidence as the invulnerability rule.
            hero.Buffs.Clear();
            hero.Buffs.Add(new BuffState("TestMagicImmune", new BuffPayload { MagicImmune = true }, BuffType.Internal,
                slot: 1, expiresAt: 5.0, casterNetId: hero.NetId));
            hero.Health = 5000.0;
            TestAssert.False(combat.ApplyHit(hero.NetId, hero.NetId, 1000.0, DamageType.Magic,
                DamageSourceKind.InternalRaw), "a non-shrine InternalRaw hit keeps the magic immunity gate");
            TestAssert.Equal(5000.0, hero.Health, "and is refused");
            TestAssert.True(combat.ApplyHit(shrine.NetId, hero.NetId, 1000.0, DamageType.Magic,
                DamageSourceKind.InternalRaw), "the shrine's InternalRaw hit ignores the magic immunity");
        }
    }

    [Check("combat.shrine-script-true-damage")]
    public static void ShrineImpactUsesFixedTrueDamageAndRetailSourceKinds()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in new[] { UnitType.Hero, UnitType.Minion })
        foreach (var armor in new[] { 0.0, 1000.0 })
        {
            var world = new WorldState();
            var definition = MapDefinitions.Turrets.Single(t => t.Team == team && t.Tier == TurretTier.Fountain);
            var shrine = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team,
                new(7000, 7000)) { Name = definition.Name });
            TurretRules.Initialize(shrine, 1, GameData.Default);
            shrine.AttackDamage = 17; // TargetExecute's fixed damage is independent of AD.
            var target = world.Register(new Entity(2, type == UnitType.Hero ? EntityKind.Hero : EntityKind.Minion,
                type, MinionDefinitions.Opposing(team), new(8200, 7000))
                { Health = 5000, MaxHealth = 5000, Armor = armor, GodMode = false, MinionType = MinionType.Super });
            var hooks = new TestHooks();
            var transport = new TestTransport();
            var combat = new CombatService(world, hooks, transport, new FixedRandomSource(0.5));
            TestAssert.Equal(0.0, shrine.MissileSpeed, "the CastType 0 shrine spell does not fly (batch 527)");
            combat.CombatTick(0.15, regenerate: false);
            TestAssert.Equal((uint?)target.NetId, shrine.AttackTarget);
            TestAssert.Equal((shrine.NetId, (uint?)target.NetId), transport.TurretTargets.Last());
            // Batch 56 (fc6f501, Turret.luaobj): the search tick only sets the target; the swing starts on the next timer tick.
            TestAssert.Equal(0, combat.PendingHits.Count, "the acquiring tick does not swing");
            combat.CombatTick(0.15, regenerate: false);
            TestAssert.Equal(1, combat.PendingHits.Count);
            shrine.AttackCooldown = 100;
            if (shrine.AttackWindup > 0)
            {
                // Batch 741: the shrine's CastType 0 hit lands at its cast frame, the windup's end, so the windup is stepped to just before it.
                combat.CombatTick(shrine.AttackWindup - 0.01, regenerate: false);
                TestAssert.Equal(5000.0, target.Health, "no damage during windup");
            }
            combat.CombatTick(0.05, regenerate: false);
            TestAssert.Equal(4000.0, target.Health, $"{team} {type} armor={armor}");
            TestAssert.Equal(0, combat.PendingHits.Count, "shrine has no travelling hit after the cast frame");
            TestAssert.Equal(DamageType.True, hooks.LastDamageType);
            TestAssert.Equal(type == UnitType.Hero ? DamageSourceKind.InternalRaw : DamageSourceKind.Raw,
                hooks.LastDamageSource!.Value);
            TestAssert.Equal(0, hooks.BasicHits, "scripted shrine damage does not invoke ordinary on-hit hooks");
            target.Position = new(10000, 7000);
            // Batch 56 (fc6f501, Turret.luaobj): the out-of-range scan stops the swing at the next
            // 0.15 s tick and keeps the target id (classified difference 2); only the beam retires.
            combat.CombatTick(0.15, regenerate: false);
            TestAssert.Equal((uint?)target.NetId, shrine.AttackTarget,
                "Turret.luaobj keeps the target id across the range drop (NOT the hand-written clear)");
            TestAssert.Equal((shrine.NetId, (uint?)null), transport.TurretTargets.Last(), "beam clears on range exit");
            for (var tick = 0; tick < 140; tick++) combat.CombatTick(0.05, regenerate: false);
            TestAssert.Equal(4000.0, target.Health, "no delayed damage after leaving the beam");
            TestAssert.Equal(0, combat.PendingHits.Count);
            if (type != UnitType.Hero) continue;
            target.Position = new(8200, 7000);
            target.Health = 1000;
            shrine.AttackCooldown = 0;
            TestAssert.True(combat.QueueBasicAttack(shrine, target, new AttackRolls(1, 1, 1)));
            shrine.AttackCooldown = 100;
            if (shrine.AttackWindup > 0) combat.CombatTick(shrine.AttackWindup, regenerate: false);
            combat.CombatTick(0.05, regenerate: false);
            TestAssert.False(target.Alive, "the second shrine impact is lethal");
            var recap = combat.GetLastDeathRecap(target.NetId);
            TestAssert.True(recap is not null, "shrine-only death retains its recap");
            TestAssert.Equal(shrine.NetId, recap!.KillerNetId);
            TestAssert.Equal(DeathRecapSourceType.Tower, recap.KillerSourceType);
            TestAssert.Equal(2, recap.Records.Count, "both nonlethal and lethal shrine hits survive");
            foreach (var record in recap.Records)
            {
                TestAssert.Equal(shrine.NetId, record.AttackerNetId);
                TestAssert.Equal(1000.0, record.TrueDamage);
                TestAssert.Equal(1000.0, record.TotalDamage);
                TestAssert.Equal(DeathRecapOrigin.BasicAttack((byte)team, DeathRecapSourceType.Tower), record.Origin);
            }
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var definition = MapDefinitions.Turrets.First(t => t.Team == team && t.Tier == TurretTier.Outer);
            var turret = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team,
                new(7000, 7000)) { Name = definition.Name });
            TurretRules.Initialize(turret, 1, GameData.Default);
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                MinionDefinitions.Opposing(team), new(7700, 7000)) { Health = 5000, MaxHealth = 5000 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            TestAssert.True(combat.QueueBasicAttack(turret, target, new AttackRolls(1, 1, 1)));
            turret.AttackCooldown = 100;
            if (turret.AttackWindup > 0) combat.CombatTick(turret.AttackWindup, regenerate: false);
            combat.CombatTick(0.05, regenerate: false);
            TestAssert.Equal(5000.0, target.Health, "ordinary turret still travels after windup");
            TestAssert.Equal(turret.MissileSpeed, combat.PendingHits.Single().MissileSpeed);
            TestAssert.True(combat.PendingHits.Single().ProjectilePosition.X > turret.Position.X);
            combat.CombatTick(1, regenerate: false);
            TestAssert.True(target.Health < 5000, "ordinary turret projectile eventually lands");
            TestAssert.Equal(0, combat.PendingHits.Count);
        }
    }

    [Check("combat.living-building-regeneration")]
    public static void LivingBuildingsRegenerateAndReplicateCurrentHealthOnly()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        foreach (var unit in runtime.World.Entities.Values) unit.AttackCooldown = 1000;
        var buildings = runtime.World.Entities.Values.Where(unit =>
            unit.Kind is EntityKind.Inhibitor or EntityKind.Nexus).ToArray();
        var queue = new CapturingCombatPacketQueue();
        var combat = new CombatService(runtime.World, new TestHooks(),
            new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()),
            new FixedRandomSource(0.5));
        foreach (var building in buildings)
        {
            TestAssert.Equal(building.Kind == EntityKind.Nexus ? 20.0 : 15.0, building.HealthRegen);
            if (building.Kind == EntityKind.Inhibitor) TestAssert.Equal(4000.0, building.MaxHealth);
            building.Health = 100;
        }
        combat.CombatTick(1);
        foreach (var building in buildings)
        {
            TestAssert.Equal(building.Kind == EntityKind.Nexus ? 120.0 : 115.0, building.Health);
            var expected = CombatPackets.ReplicationHp(building.NetId, (float)building.Health);
            TestAssert.True(queue.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(expected.Plaintext.Span)),
                "building HP uses the existing current-HP replication without a max-HP field");
            building.Health = building.MaxHealth - 1;
        }
        queue.Clear();
        combat.CombatTick(0.1);
        foreach (var building in buildings)
        {
            TestAssert.Equal(building.MaxHealth, building.Health);
            var expected = CombatPackets.ReplicationHp(building.NetId, (float)building.Health, syncId: 2);
            TestAssert.True(queue.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(expected.Plaintext.Span)),
                "the final capped HP update is sent even inside the regen throttle interval");
        }
        var dead = buildings.First(unit => unit.Kind == EntityKind.Inhibitor);
        TestAssert.True(combat.Kill(dead, 0));
        queue.Clear();
        combat.CombatTick(1);
        TestAssert.False(dead.Alive);
        TestAssert.Equal(0.0, dead.Health);
        TestAssert.False(queue.Packets.Any(packet => packet.Plaintext.Span[0] == 0xCC &&
            packet.EntityReferences.Any(reference => reference.NetId == dead.NetId)), "dead inhibitor gets no regen update");
    }

    [Check("combat.structure-hits-do-not-grant-vamp")]
    public static void StructuresDoNotGrantLifeStealOrSpellVamp()
    {
        var targets = new[]
        {
            (EntityKind.Turret, UnitType.Turret),
            (EntityKind.Inhibitor, UnitType.Structure),
            (EntityKind.Nexus, UnitType.Structure),
            (EntityKind.Hero, UnitType.Hero),
            (EntityKind.Minion, UnitType.Minion),
            (EntityKind.Neutral, UnitType.Minion),
            (EntityKind.YorickGhoul, UnitType.Minion),
        };
        foreach (var (kind, type) in targets)
        foreach (var sourceKind in new[] { DamageSourceKind.Basic, DamageSourceKind.Spell,
            DamageSourceKind.SpellAoe, DamageSourceKind.SpellPersist })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
                { Health = 200, MaxHealth = 1000, LifeSteal = 0.2, SpellVamp = 0.4 });
            var target = world.Register(new Entity(2, kind, type, Team.Chaos, new(7050, 7000))
                { Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 100,
                DamageType.True, sourceKind, vampScale: 1.0));
            TestAssert.Near(900, target.Health, 1e-8, "the sustain exclusion does not suppress damage");
            var excluded = type is UnitType.Turret or UnitType.Structure;
            // Retail sweep B4 COMBAT-01, 27 Sept 2026: AoE vamp uses 0.333, not full vamp.
            TestAssert.Near(200 + (excluded ? 0 : sourceKind == DamageSourceKind.Basic ? 20 :
                sourceKind == DamageSourceKind.SpellAoe ? 13.32 : 40),
                hero.Health, 1e-8, $"{kind} / {sourceKind}");
        }
    }

    [Check("combat.spell-vamp-retail-source-ratios")]
    public static void SpellVampUsesRetailSourceRatios()
    {
        foreach (var sourceKind in new[] { DamageSourceKind.SpellAoe,
                     DamageSourceKind.Spell, DamageSourceKind.SpellPersist })
        foreach (var scale in new[] { 1.0, 0.5, 0.0 })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
                { Health = 200, MaxHealth = 1000, SpellVamp = 0.2 });
            var target = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7050, 7000))
                { Health = 1000, MaxHealth = 1000, SpellBlock = 100 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 200,
                DamageType.Magic, sourceKind, vampScale: scale));
            TestAssert.Near(900, target.Health, 1e-9, "vamp uses the 100 post-mitigation damage");
            TestAssert.Near(200 + (sourceKind == DamageSourceKind.SpellAoe ? 6.66 : 20) * scale,
                hero.Health, 1e-9, "the category ratio is applied once, alongside an explicit caller modifier");
        }
    }

    [Check("combat.dragon-standard-hit-burning")]
    public static void StandardDragonUsesScriptedHitAndRenewedBurning()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, solo: true, readySettleSeconds: 0.0);
        runtime.Session.CreateWorld();
        var observerHero = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
        var position = observerHero.Position;
        var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Pet, UnitType.Minion, Team.Order, position)
        {
            Name = "DragonBurnTarget",
            OwnerNetId = observerHero.NetId,
            Health = 5000.0,
            MaxHealth = 5000.0,
            Armor = 100.0,
            AttackPeriod = 1.0,
            AttackWindup = 0.5,
            Created = true,
            Visible = true,
        });
        var dragon = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.Neutral, UnitType.Monster, Team.Neutral, position)
        {
            Name = "Dragon",
            AttackDamage = 100.0,
            AttackRange = 500.0,
            AttackPeriod = 1.0,
            AttackWindup = 0.0,
            MissileSpeed = 0.0,
            Health = 10000.0,
            MaxHealth = 10000.0,
            Created = true,
            Visible = true,
        });
        // Batch 159 part 2: the Dragon's own character script is what puts the x1.43 on its landed basic attack
        // (CharScriptDragon's CharOnHitUnit 0047-0108: BBMath DamageAmount MO_MULTIPLY 1.43, then Burning) and the
        // Burning it renews. The hand-registered dragon only runs it once it is bound the way a camp spawn binds it.
        TestAssert.True(runtime.Spells.UnitScripts!.BindUnit(dragon, "Dragon"), "the Dragon's character script binds");
        var preexistingBurning = runtime.Buffs.AddBuff(target, "Burning", BuffType.DamageOverTime,
            3.0, observerHero.NetId, new Amberline.Core.Buffs.BuffPayload
            {
                Period = 1.0,
                DamagePerTick = 7.0,
                DamageType = DamageType.True,
                SourceKind = DamageSourceKind.Proc,
                SourceIsPeriodic = true,
            });
        TestAssert.True(preexistingBurning is not null);
        var capture = new CapturingReplicationTransport();
        var observer = new ReplicationObserver(1, Team.Order, observerHero.NetId, capture);
        runtime.Delivery.AddObserver(observer);
        runtime.Delivery.Flush();
        TestAssert.True(observer.View.Known.Contains(target.NetId) &&
            observer.View.Visible.Contains(target.NetId),
            "the production observer knows the allied pet target");
        TestAssert.True(observer.View.Known.Contains(dragon.NetId) &&
            observer.View.Visible.Contains(dragon.NetId),
            "the production observer sees the Dragon at the shared walkable position");
        capture.Packets.Clear();

        TestAssert.True(runtime.Combat.QueueBasicAttack(dragon, target,
            new AttackRolls(1.0, 1.0, 1.0)));
        runtime.Combat.CombatTick(0.001, regenerate: false);
        runtime.Delivery.Flush();

        var expectedHit = 100.0 * 1.43 * CombatRules.ResistanceMultiplier(target.Armor);
        TestAssert.Near(5000.0 - expectedHit, target.Health, 1e-8,
            "Dragon's 1.43 basic-hit multiplier is applied before armor");
        var burning = target.Buffs.Single(buff => buff.Name == "Burning");
        TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Burning"));
        TestAssert.Near(4.0, burning.ExpiresAt, 1e-8);
        TestAssert.Equal(15.0, burning.Payload.DamagePerTick);
        TestAssert.Equal(1.0, burning.Payload.Period);
        TestAssert.Equal(DamageType.True, burning.Payload.DamageType);
        TestAssert.Equal(DamageSourceKind.Proc, burning.Payload.SourceKind);
        TestAssert.True(burning.Payload.SourceIsPeriodic);
        TestAssert.Near(-0.20, burning.Payload.AttackSpeedMult, 1e-8);
        TestAssert.Near(0.80, target.AttackSpeedMod, 1e-8,
            "Burning's multiplicative attack-speed modifier applies to pets");
        TestAssert.Near(1.25, target.AttackPeriod, 1e-8);
        // The observer already holds the pre-existing Burning from its snapshot, so the first Dragon hit is a same-count
        // renewal of a held slot: one native identity packet, not a 0xBF add. It is NpcBuffUpdateCount (0x1F) rather
        // than the 0x32 BuffReplace batch 55 recorded, because Spells/Burning.luaobj's template hands the client its own
        // particle (AutoBuffActivateEffect='Cape_neg_buf.troy') and 0x32 ends in the client's torch restart,
        // which would kill and re-create that particle on every Dragon hit — the blink batch 157 part 3 fixed for the
        // auras (ReplicationDelivery.AsHeldBuffUpdate, BuffPayload.ClientActivationFx). The buff became the Dragon
        // character script's own in batch 159 part 2, which is the first time this template's claim reached the wire.
        TestAssert.Equal(1, BurningPackets(capture, target.NetId, (byte)burning.Slot).Length,
            "the first Dragon hit has one native Burning identity packet");
        TestAssert.Equal(0, capture.Packets.Count(packet => packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode),
            "a held Burning slot is renewed by the silent 0x1F, never added a second time");
        TestAssert.Equal(0, capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x8C),
            "Burning has no server-created duplicate particle");

        // Spells/Burning.luaobj runs its own periodic: its BuffOnUpdateActions is a BBExecutePeriodically
        // (0032-0035: TimeBetweenExecutions 1.0, TrackTimeVar LastTimeExecuted in InstanceVars, ExecuteImmediately
        // false) around the BBApplyDamage of 0039-0056 (TickDamage from InstanceVars, TRUE_DAMAGE, DAMAGESOURCE_PROC).
        // The engine's own ticker must not run it a second time (BuffLifecycle), the first update stamps the clock
        // and the body is one full period later — an instant of GetTime, so the check steps the spell clock with the
        // buff update the way a production step does (SpellSystem.Tick before the lifecycle's own).
        void Step(double dt)
        {
            runtime.Spells.Tick(dt);
            runtime.Buffs.TickBuffs(target, dt);
        }
        // The add step's own buff update runs on the hit's clock (production takes it in the same step as the swing,
        // after the spell phase has counted that step): that sample is what stamps LastTimeExecuted.
        runtime.Buffs.TickBuffs(target, 0.0);
        var beforeTick = target.Health;
        Step(0.99);
        TestAssert.Equal(beforeTick, target.Health,
            "Burning does not deal an immediate or early periodic tick");
        TestAssert.Near(3.01, burning.ExpiresAt, 1e-8);
        Step(0.01);
        TestAssert.Near(beforeTick - 15.0, target.Health, 1e-8,
            "Burning deals one delayed true-damage proc despite armor");
        TestAssert.Near(3.0, burning.ExpiresAt, 1e-8);
        Step(0.25);
        TestAssert.Near(2.75, burning.ExpiresAt, 1e-8);

        capture.Packets.Clear();
        dragon.AttackCooldown = 0.0;
        TestAssert.True(runtime.Combat.QueueBasicAttack(dragon, target,
            new AttackRolls(1.0, 1.0, 1.0)));
        runtime.Combat.CombatTick(0.001, regenerate: false);
        runtime.Delivery.Flush();
        var renewed = target.Buffs.Single(buff => buff.Name == "Burning");
        TestAssert.True(ReferenceEquals(burning, renewed),
            "Burning renewal keeps the existing authoritative buff instance");
        TestAssert.Equal(burning.Slot, renewed.Slot,
            "Burning renewal keeps the native buff slot");
        TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Burning"));
        TestAssert.Near(4.0, renewed.ExpiresAt, 1e-8,
            "a second Dragon hit renews Burning for four seconds");
        TestAssert.Equal(1, BurningPackets(capture, target.NetId, (byte)burning.Slot).Length,
            "renewal sends one identity update without remove/re-add");
        TestAssert.Equal(0, capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x7F),
            "renewal does not remove the native buff first");
        TestAssert.Equal(0, capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x8C),
            "renewal does not create a second particle");

        TestAssert.True(ReferenceEquals(preexistingBurning, renewed),
            "Dragon takes over a pre-existing same-name Burning instance in place");
        // The renewal is the script's own clock kept, not the engine ticker's PeriodElapsed: Burning 0033's
        // LastTimeExecuted lives in the instance's InstanceVars, a BUFF_RENEW_EXISTING hands over only the add's own
        // vars, and the payload reports the scheduled periodic instead of a ticker of its own.
        TestAssert.True(renewed.Payload.ScriptSchedulesPeriodic,
            "a same-instance Dragon renewal keeps the script's own periodic");
        var beforeRenewedTick = target.Health;
        Step(0.74);
        TestAssert.Equal(beforeRenewedTick, target.Health,
            "a fractional renewal does not tick immediately");
        Step(0.01);
        TestAssert.Near(beforeRenewedTick - 15.0, target.Health, 1e-8,
            "the renewed Dragon burn keeps the script's one-second clock");
        Step(3.25);
        runtime.Delivery.Flush();
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Burning"),
            "Burning expires after its renewed four-second lifetime");
        TestAssert.Near(1.0, target.AttackSpeedMod, 1e-8,
            "Burning expiry restores pet attack speed");
        TestAssert.Near(1.0, target.AttackPeriod, 1e-8);

        // A Burning identity packet is a 0xBF add of the name or one of the two silent renewals of the held slot:
        // NpcBuffReplace (0x32, batch 55) or NpcBuffUpdateCount (0x1F), which this template takes since it hands
        // the client its own AutoBuffActivateEffect particle (see the first-hit comment above).
        static PlaintextPacket[] BurningPackets(CapturingReplicationTransport capture, uint targetNetId, byte slot) =>
            capture.Packets.Where(packet => packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode &&
                packet.Plaintext.Span.Length >= 13 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("Burning") ||
                packet.Plaintext.Span[0] is 0x32 or SpellPackets.BuffUpdateCountOpcode &&
                packet.Plaintext.Span.Length >= 18 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == targetNetId &&
                packet.Plaintext.Span[5] == slot).ToArray();
    }
    [Check("combat.turret-buff-growth")]
    public static void PermanentTurretGrowthPreservesTemporaryFlatStatBonuses()
    {
        // Batch 159 part 1: a turret grows through its own buffs, not through a service's stack counter.
        // Spells/TurretBonus.luaobj's BuffOnUpdateStats is three BBIncPermanentStat (+6 flat physical, +1.5 spell
        // block, +1.5 armor) under the add's TickRate 60, so one line lands a minute after the add and one per
        // minute after it (the mutation clock of batch 159 step 1). Every turret character adds that buff in
        // CharOnActivate but the inner and the inhibitor ones add Spells/TurretPreBonus.luaobj first
        // (CharScriptOrderTurretNormal2 / CharScriptChaosTurretWorm2: Duration 420.0, CharScriptOrderTurretDragon /
        // CharScriptChaosTurretGiant: 960.0, both with NextBuffVars.StartDecay 660.1 / 1200.1), and TurretPreBonus's
        // OnBuffDeactivate hands over to TurretBonus for exactly that StartDecay window — the idle minutes before
        // growth are the script's own. The hand-off needs the removal to reach the script (SpellSystem.
        // OnUnitBuffRemoved), the same routing the champion, summoner and item buffs already had.
        // Where the script differs from the old numbers, the script wins: the hand-written ScalingWindow gave the
        // fountain (0, 0) and no growth at all, while CharScriptChaosTurretShrine's own TurretBonus (Duration
        // 25000, TickRate 60) grows the Chaos fountain every minute. OrderTurretShrine is the one character with no
        // shipped CharScript at all, so it grows through nothing and stays where the engine seeds it.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        runtime.Spells.UnitScripts!.Poll();
        var turrets = runtime.World.Entities.Values
            .Where(unit => unit.UnitType == UnitType.Turret && MapDefinitions.TurretTiers.ContainsKey(unit.Name))
            .ToArray();
        void Advance(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 1.0)
            {
                runtime.Spells.Tick(1.0);
                foreach (var turret in turrets) runtime.Buffs.TickBuffs(turret, 1.0);
            }
        }
        // The three lanes of a tier share one character (MapDefinitions.TurretCharacter), so a whole (tier, team)
        // group grows together; the phases below sweep the group and name one representative where a single unit is
        // enough.
        Entity[] Group(TurretTier tier, Team team) => turrets
            .Where(turret => MapDefinitions.TurretTiers[turret.Name] == tier && turret.Team == team)
            .OrderBy(turret => turret.Name, StringComparer.Ordinal).ThenBy(turret => turret.NetId).ToArray();
        Entity One(TurretTier tier, Team team)
        {
            var group = Group(tier, team);
            TestAssert.True(group.Length > 0, $"the map holds a {tier} turret of {team}");
            return group[0];
        }
        var outer = One(TurretTier.Outer, Team.Order);
        var orderShrine = One(TurretTier.Fountain, Team.Order);
        TestAssert.True(outer.BoundCharacter is not null, "the map's turrets are bound to their character scripts");
        TestAssert.True(orderShrine.BoundCharacter is null, "OrderTurretShrine has no shipped character script");
        var baseline = turrets.ToDictionary(turret => turret,
            turret => (turret.AttackDamage, turret.Armor, turret.SpellBlock));
        // A temporary flat payload on one outer, kept live across the growth the way an aura's is (an explicit long
        // lifetime, so the tick below keeps it instead of ending it).
        var payload = new Amberline.Core.Buffs.BuffPayload { AttackDamage = 50, Armor = 10, SpellBlock = 20 };
        BuffLifecycle.ApplyBuffStats(outer, payload);
        outer.Buffs.Add(new BuffState("TemporaryResistance", payload, expiresAt: 100_000.0));
        void Grown(TurretTier tier, Team team, double ticks, string what)
        {
            foreach (var turret in Group(tier, team))
            {
                var temporary = turret.Buffs.Any(buff => buff.HasName("TemporaryResistance"))
                    ? (payload.AttackDamage, payload.Armor, payload.SpellBlock)
                    : (0.0, 0.0, 0.0);
                TestAssert.Near(baseline[turret].AttackDamage + temporary.Item1 + 6.0 * ticks,
                    turret.AttackDamage, 1e-8, what + " (" + turret.Name + " damage)");
                TestAssert.Near(baseline[turret].Armor + temporary.Item2 + 1.5 * ticks,
                    turret.Armor, 1e-8, what + " (" + turret.Name + " armor)");
                TestAssert.Near(baseline[turret].SpellBlock + temporary.Item3 + 1.5 * ticks,
                    turret.SpellBlock, 1e-8, what + " (" + turret.Name + " MR)");
            }
        }
        Advance(61.0);
        TestAssert.Near(baseline[outer].AttackDamage + 50.0 + 6.0, outer.AttackDamage, 1e-8,
            "temporary AD survives TurretBonus's permanent");
        TestAssert.Near(baseline[outer].Armor + 10.0 + 1.5, outer.Armor, 1e-8, "and so does the temporary armor");
        Grown(TurretTier.Outer, Team.Order, 1.0, "one TurretBonus line a minute");
        Grown(TurretTier.Nexus, Team.Order, 1.0, "a nexus turret grows from its own add too");
        Grown(TurretTier.Fountain, Team.Chaos, 1.0,
            "CharScriptChaosTurretShrine's TurretBonus grows the fountain every minute (the old ScalingWindow said none)");
        Grown(TurretTier.Inner, Team.Order, 0.0,
            "an inner turret is still inside its TurretPreBonus idle window (Duration 420.0)");
        Grown(TurretTier.Inhibitor, Team.Order, 0.0, "and an inhibitor turret inside its own (Duration 960.0)");
        Grown(TurretTier.Fountain, Team.Order, 0.0, "the character with no script grows through nothing");
        Advance(420.0);
        TestAssert.Near(baseline[outer].Armor + 10.0 + 12.0, outer.Armor, 1e-8, "temporary armor is still on top");
        Grown(TurretTier.Outer, Team.Order, 8.0, "outer ends its 480.1 s window on the eighth line");
        Grown(TurretTier.Inner, Team.Order, 1.0,
            "TurretPreBonus handed over at 420 s and TurretBonus's first line landed at 480 s");
        Grown(TurretTier.Inhibitor, Team.Order, 0.0, "the inhibitor turret's own idle window runs to 960 s");
        Grown(TurretTier.Nexus, Team.Order, 8.0, "nexus turrets");
        Grown(TurretTier.Fountain, Team.Chaos, 8.0, "the Chaos fountain keeps stacking its own TurretBonus");
        Grown(TurretTier.Fountain, Team.Order, 0.0, "and the Order fountain has no character script to grow it");
        BuffLifecycle.ReverseBuffStats(outer, payload);
        outer.Buffs.Clear();
        Grown(TurretTier.Outer, Team.Order, 8.0, "expiry leaves the permanent growth");
        TestAssert.Equal(0, runtime.Spells.UnitScripts.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", runtime.Spells.UnitScripts.ScriptFailures
                .Select(failure => $"{failure.Script}.{failure.Hook} {failure.Message}")));
    }

    [Check("combat.turret-assist-stats")]
    public static void TurretAssistScriptsAddPermanentDefensesAndShownAttackSpeed()
    {
        // Batch 759 (owner decision of 12 November 2026, "Match the footage"): PersonalTurretAssistBonus keeps its +25 armour and magic resist,
        // but its +0.3 attack speed does not reach the firing period, so a lane turret swings at its record's 1.2 s
        //. Batch 760 (owner, 12 November 2026: the HUD shows 1.083): the +0.3 is
        // the shown multiplier, so the block carries 1.3, the client's target bar 1.083, and the wind-up is the client's 1.2 / 1.3 x 0.139
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        var queue = new CapturingCombatPacketQueue();
        var transport = new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter());
        foreach (var turret in runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Turret))
        {
            var tier = MapDefinitions.TurretTiers[turret.Name];
            var raw = TurretRules.StatsFor(tier, turret.Team, GameData.Default);
            var lane = tier != TurretTier.Fountain;
            TestAssert.Near(raw.Armor + (lane ? 175 : 0), turret.Armor, 1e-8, turret.Name + " assist armor");
            TestAssert.Near(raw.SpellBlock + (lane ? 175 : 0), turret.SpellBlock, 1e-8, turret.Name + " assist MR");
            TestAssert.Near(lane ? 1.3 : 1.0, turret.AttackSpeedMod, 1e-8, turret.Name + " shown attack-speed multiplier");
            TestAssert.Near(lane ? 1.2 : raw.AttackPeriod, turret.AttackPeriod, 1e-8, turret.Name + " period");
            if (lane) TestAssert.Near(1.2 / 1.3 * (0.3 - 0.161), turret.AttackWindup, 1e-8, turret.Name + " windup");
            queue.Clear();
            transport.SendTurretStats(turret);
            var packet = queue.Packets.Single().Plaintext.Span;
            TestAssert.Near(turret.Armor, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packet[23..]), 1e-5);
            TestAssert.Near(turret.SpellBlock, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packet[27..]), 1e-5);
            var shown = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packet[31..]);
            TestAssert.Near(lane ? 1.3 : 1.0, shown, 1e-6, turret.Name + " replicated mAttackSpeedMod");
            var character = MapDefinitions.TurretCharacter(tier, turret.Team);
            if (lane)
                TestAssert.Near(1.083, Math.Round(AttackCadenceChecks.ClientTargetBarAttackSpeed(character, shown), 3), 1e-9,
                    turret.Name + ": the client's target bar shows 1.083 attacks a second");
            TestAssert.Near(AttackCadenceChecks.ClientCastDelay(character, shown), turret.AttackWindup, 1e-6,
                turret.Name + ": the wind-up is the client's cast delay from the block");
            // Growth is the turret's own TurretBonus permanents now (Spells/TurretBonus.luaobj BuffOnUpdateStats),
            // not an engine tick's stack counter: nothing of it runs here, and the script's own permanents stack on
            // top of these (combat.turret-buff-growth pins that).
            TurretRules.Tick(turret, 1800, 0);
            TestAssert.Near(raw.Armor + (lane ? 175 : 0), turret.Armor, 1e-8, "the engine tick applies no growth");
        }
    }

    [Check("combat.turret-backdoor")]
    public static void OnlyNearbyEnemyLaneMinionsRenewTheEightSecondBackdoorWindow()
    {
        // The window is Spells/TurretAssistManager.luaobj's own BuffOnUpdateActions now (batch 159 part 1 deletes the
        // hand-written form it replaces): BBForEachUnitInTargetAreaRandom Center=Owner, Range 1000, Flags
        // 'AffectEnemies AffectMinions AffectBarrackOnly ', MaximumUnitsToPick 1, InclusiveBuffFilter true, and an
        // add of Spells/TurretBackdoorBonus.luaobj (BUFF_RENEW_EXISTING, Duration 8.0) on the Owner for the one it
        // finds. TurretBackdoorBonus's OnBuffActivate takes the 150 flat armor and spell block back that
        // TurretAssistManager's own OnBuffActivate put on the turret, and its OnBuffDeactivate puts them on again: an
        // enemy lane minion in the search is what lifts the protection, and only for as long as a search keeps seeing
        // one. The search's own geometry is the shared BBForEachUnitInTargetArea scan (BbSpellTargeting.Area), so the
        // knife-edge radii of the old hand-written probe are gone with the form that had them.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var role in Enum.GetValues<MinionType>())
        {
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
            runtime.Session.CreateWorld();
            runtime.Spells.UnitScripts!.Poll();
            var enemy = team == Team.Order ? Team.Chaos : Team.Order;
            var turret = runtime.World.Entities.Values.First(unit => unit.UnitType == UnitType.Turret &&
                MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier == TurretTier.Outer &&
                unit.Team == team);
            turret.AttackRange = 0;
            // The script's own activation stack is already on: TurretAssistManager's +150 and
            // PersonalTurretAssistBonus's +25, over the raw inibin defense.
            var rest = turret.Armor;
            var open = rest - TurretRules.BackdoorResistance;
            // The spell block is the same 150, over its own inibin value.
            var openSpellBlock = turret.SpellBlock - TurretRules.BackdoorResistance;
            var near = new Position2(turret.Position.X + 500, turret.Position.Z);
            var far = new Position2(turret.Position.X + 1050, turret.Position.Z);
            void Advance(double seconds)
            {
                for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 0.1)
                {
                    runtime.Spells.Tick(0.1);
                    runtime.Buffs.TickBuffs(turret, 0.1);
                }
            }
            Advance(1.0);
            TestAssert.Equal(rest, turret.Armor, "nothing of the map's own units is in the search");
            // AffectMinions is only half of the filter: AffectBarrackOnly (Spells/TurretAssistManager.luaobj's Flags)
            // keeps a minion-shaped unit no barracks ever spawned out of the search.
            foreach (var kind in new[] { EntityKind.Hero, EntityKind.Pet, EntityKind.Neutral, EntityKind.Ward,
                         EntityKind.ShacoClone, EntityKind.YorickRevenant, EntityKind.YorickGhoul })
            {
                var other = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), kind,
                    UnitType.Minion, enemy, near) { Health = 1000, MaxHealth = 1000 });
                Advance(1.0);
                TestAssert.Equal(rest, turret.Armor, kind + " cannot disable backdoor protection");
                runtime.World.Remove(other.NetId);
            }
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, team, near) { Health = 1000, MaxHealth = 1000,
                    Radius = role is MinionType.Siege or MinionType.Super ? 55.7437 : 35.7437, MinionType = role });
            Advance(1.0);
            TestAssert.Equal(rest, turret.Armor, "allied lane minions do not qualify");
            minion.Team = enemy;
            minion.Alive = false;
            Advance(1.0);
            TestAssert.Equal(rest, turret.Armor, "corpses do not qualify");
            minion.Alive = true;
            minion.Position = far;
            Advance(1.0);
            TestAssert.Equal(rest, turret.Armor, "a lane minion outside the 1000 search range does not qualify");
            minion.Position = near;
            minion.Visible = false;
            Advance(1.0);
            TestAssert.Equal(open, turret.Armor,
                "an unseen enemy lane minion qualifies: the search carries no visibility filter");
            TestAssert.Equal(openSpellBlock, turret.SpellBlock);
            // Repeated presence renews the add (BUFF_RENEW_EXISTING) without another subtraction.
            Advance(3.0);
            TestAssert.Equal(open, turret.Armor, "renewal keeps one TurretBackdoorBonus");
            minion.Position = far;
            Advance(7.5);
            TestAssert.Equal(open, turret.Armor, "the buff lasts eight seconds from the search that saw it");
            Advance(1.0);
            TestAssert.Equal(rest, turret.Armor,
                "OnBuffDeactivate puts the 150 back eight seconds after that search");
            minion.Position = near; // returns between searches
            Advance(0.3);
            TestAssert.Equal(rest, turret.Armor, "a minion arriving between searches waits for the next whole second");
            Advance(0.5);
            TestAssert.Equal(open, turret.Armor, "the next TickRate 1.0 search picks it up");
            minion.Alive = false;
            Advance(8.5);
            TestAssert.Equal(rest, turret.Armor, "minion death ends renewal");
        }
    }

    [Check("combat.turret-ramp")]
    public static void OrdinaryTurretHitsUseTheShippedSourceAndVictimStacks()
    {
        // Spells/TurretDamageManager.luaobj's own BuffOnHitUnit is the ramp (batch 159 part 1 deletes the hand-written
        // markers it replaces). The hook is a script's whole rule: Target is a hero, TurretBuffCount and TargetBuffCount
        // are the TurretDamageMarker stacks the turret and its victim hold from that turret, DamageBonus = 0.9 + 0.2 *
        // (both counts) multiplies DamageAmount, then a marker goes on the turret every hit and on the victim only once
        // the turret's own count reads 3 (BUFF_STACKS_AND_RENEWS, MaxStack 3, Duration 3). All eight lane characters add
        // the buff in CharOnActivate; ChaosTurretShrine adds only TurretBonus and TurretBonusHealth, and
        // OrderTurretShrine has no character script at all, so a fountain hit is the plain one.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        runtime.Spells.UnitScripts!.Poll();
        var queue = new CapturingCombatPacketQueue();
        var combat = new CombatService(runtime.World, runtime.Dependencies.CombatHooks,
            new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()),
            new FixedRandomSource(0.5), runtime.HeroStats, gameData: GameData.Default);
        foreach (var turret in runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Turret).ToArray())
        {
            var tier = MapDefinitions.TurretTiers[turret.Name];
            var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
                UnitType.Hero, turret.Team == Team.Order ? Team.Chaos : Team.Order, new(7050, 7000))
                { Health = 100000, MaxHealth = 100000, Armor = 100 });
            turret.Position = new(7000, 7000);
            turret.Protected = false;
            foreach (var multiplier in new[] { 0.9, 1.1, 1.3, 1.5, 1.7, 1.9, 2.1, 2.1 })
            {
                queue.Clear();
                var before = target.Health;
                TestAssert.True(combat.ApplyHit(turret.NetId, target.NetId, 180, DamageType.Physical, DamageSourceKind.Basic));
                var expected = tier == TurretTier.Fountain ? 90 : 100 * multiplier;
                TestAssert.Near(expected, before - target.Health, 1e-8, turret.Name + " ramp and innate penetration");
                var packet = queue.Packets.Single(p => p.Plaintext.Span[0] == 0x68).Plaintext.Span;
                TestAssert.Near(expected, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packet[14..]), 1e-5);
            }
            runtime.World.Remove(target.NetId);
        }
    }

    [Check("combat.turret-ramp-lifecycle")]
    public static void TurretRampExpiresPerCasterAndOnlyAdvancesOnLandedHeroAttacks()
    {
        // The history of Spells/TurretDamageManager.luaobj's ramp is its own TurretDamageMarker buffs (MaxStack 3,
        // Duration 3, BuffType BUFF_Internal, STACKS_AND_RENEWS so every add restarts the clock) and not a service's
        // dictionary: one on the turret per hit from that turret and one on the victim only once the turret's own count
        // reads 3, both aged by the buff ticker. What follows pins that lifecycle where the hand-written markers stood.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        runtime.Spells.UnitScripts!.Poll();
        var map = runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Turret).ToArray();
        var source = map.Single(unit => unit.Name == "Turret_T1_C_05");
        var second = map.Single(unit => unit.Name == "Turret_T1_C_04");
        // The avoided swing below is a real one, so both turrets stand where their victims are and face enemies.
        source.Position = new(7000, 7000);
        second.Position = new(7000, 7000);
        source.Protected = false;
        second.Protected = false;
        var queue = new CapturingCombatPacketQueue();
        var combat = new CombatService(runtime.World, runtime.Dependencies.CombatHooks,
            new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()),
            new FixedRandomSource(0.5), runtime.HeroStats, gameData: GameData.Default);
        var enemy = source.Team == Team.Order ? Team.Chaos : Team.Order;
        Entity Add(EntityKind kind, UnitType type, Team team) =>
            runtime.World.Register(new Entity(runtime.World.Allocate(type), kind, type, team, new(7000, 7050))
            { Health = 1e6, MaxHealth = 1e6, Armor = 0 });
        var firstHero = Add(EntityKind.Hero, UnitType.Hero, enemy);
        var nextHero = Add(EntityKind.Hero, UnitType.Hero, enemy);
        nextHero.Position = new(7100, 7050);
        int Marks(Entity unit, Entity turret) => unit.Buffs
            .Where(buff => buff.Name == "TurretDamageMarker" && buff.CasterNetId == turret.NetId)
            .Sum(buff => buff.Stacks);
        double Hit(Entity turret, Entity target, double amount = 100, DamageSourceKind kind = DamageSourceKind.Basic)
        {
            var before = target.Health;
            TestAssert.True(combat.ApplyHit(turret.NetId, target.NetId, amount, DamageType.Physical, kind));
            return before - target.Health;
        }
        // Only a landed basic attack on a hero reaches BuffOnHitUnit: a protected or physically immune target takes
        // nothing and leaves no marker, a spell hit and a minion hit leave none either.
        firstHero.Protected = true;
        TestAssert.False(combat.ApplyHit(source.NetId, firstHero.NetId, 100, DamageType.Physical, DamageSourceKind.Basic));
        firstHero.Protected = false;
        firstHero.Buffs.Add(new Amberline.Core.Buffs.BuffState("physical-immune",
            new Amberline.Core.Buffs.BuffPayload { PhysicalImmune = true }));
        TestAssert.False(combat.ApplyHit(source.NetId, firstHero.NetId, 100, DamageType.Physical, DamageSourceKind.Basic));
        firstHero.Buffs.Clear();
        TestAssert.Near(100, Hit(source, firstHero, kind: DamageSourceKind.Spell), 1e-8);
        var minion = Add(EntityKind.Minion, UnitType.Minion, enemy);
        TestAssert.Near(100, Hit(source, minion), 1e-8);
        TestAssert.Equal(0, Marks(source, source), "a spell hit and a minion hit never reach the ramp hook");

        // The ramp: 0.9 + 0.2 * (the turret's own stacks + this victim's). The hook reads both counts before its
        // own adds, so the victim's marker first appears on the fourth hit (the turret's count reads 3 there).
        for (var hit = 1; hit <= 6; hit++)
        {
            TestAssert.Near(100 * (0.9 + 0.2 * (Math.Min(3, hit - 1) + Math.Max(0, Math.Min(3, hit - 4)))),
                Hit(source, firstHero), 1e-8, "ramp hit " + hit);
            TestAssert.Equal(Math.Max(0, Math.Min(3, hit - 3)), Marks(firstHero, source),
                "the victim's marker appears only once the turret's own count reads 3");
        }
        TestAssert.Equal(3, Marks(source, source), "the turret's own marker caps at MaxStack 3");
        // A second turret keeps its own history (the markers name their caster).
        TestAssert.Near(90, Hit(second, firstHero), 1e-8, "each turret ramps on its own markers");
        TestAssert.Equal(1, Marks(second, second), "the second turret starts its own marker");
        TestAssert.Equal(0, Marks(firstHero, second), "and the victim has none from it until its count reads 3");
        TestAssert.Equal(3, Marks(firstHero, source), "and leaves the first turret's victim marker alone");

        // Duration 3, STACKS_AND_RENEWS: a landed hit restarts both clocks. The seventh hit renews the turret's
        // own marker and the victim's together, and a swing on another hero renews the turret's alone.
        TestAssert.Near(210, Hit(source, firstHero), 1e-8, "the ramp caps at 0.9 + 0.2 * 6");
        runtime.Buffs.TickBuffs(source, 2.0);
        runtime.Buffs.TickBuffs(firstHero, 2.0);
        TestAssert.Equal(3, Marks(source, source), "two seconds leave the turret's own marker standing");
        TestAssert.Equal(3, Marks(firstHero, source), "and the victim's");
        TestAssert.Near(150, Hit(source, nextHero), 1e-8, "a fresh victim has none of its own (3 + 0)");
        runtime.Buffs.TickBuffs(firstHero, 1.01);
        TestAssert.Equal(0, Marks(firstHero, source), "three seconds expire the victim's marker");
        TestAssert.Equal(3, Marks(source, source), "the turret's own was renewed by the swing on the other hero");
        runtime.Buffs.TickBuffs(source, 3.01);
        TestAssert.Equal(0, Marks(source, source), "three quiet seconds expire the turret's own marker too");
        TestAssert.Near(90, Hit(source, firstHero), 1e-8, "the ramp starts over from a clean history");

        // A victim that dies takes its markers with it (the buff manager's death removal, the script buff
        // runtime's own answer to a death: TurretDamageMarker does not say PersistsThroughDeath); the turret's stay.
        foreach (var expected in new[] { 110.0, 130, 150 }) TestAssert.Near(expected, Hit(source, firstHero), 1e-8);
        TestAssert.Equal(3, Marks(source, source));
        TestAssert.Equal(1, Marks(firstHero, source), "the victim's marker starts at the turret's third stack");
        TestAssert.True(combat.Kill(firstHero));
        firstHero.Alive = true;
        firstHero.Health = 1e6;
        TestAssert.Equal(0, Marks(firstHero, source), "death retires the victim's markers");
        TestAssert.Equal(3, Marks(source, source), "the turret keeps its own");
        TestAssert.Near(150, Hit(source, firstHero), 1e-8,
            "the victim's history is gone but the turret's is not (3 + 0)");

        // An avoided swing and a swing still in flight never reach the hook.
        var blind = new Amberline.Core.Buffs.BuffState("blind",
            new Amberline.Core.Buffs.BuffPayload { MissChance = 1 });
        source.Buffs.Add(blind);
        var before = firstHero.Health;
        TestAssert.True(combat.QueueBasicAttack(source, firstHero, new AttackRolls(1, 1, 0)));
        combat.CombatTick(0.6, regenerate: false);
        combat.CombatTick(0.001, regenerate: false);
        TestAssert.Equal(before, firstHero.Health, "an avoided attack never reaches the ramp hook");
        source.Buffs.Remove(blind);
        TestAssert.Equal(3, Marks(source, source), "and adds no marker");
    }

    [Check("combat.building-ap-boundaries")]
    public static void BuildingApUsesImpactStatsFormsAndTheExistingCloneOrder()
    {
        var data = GameData.Default;
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data,
            orderChampion: "Nidalee", chaosChampion: "Shaco");
        runtime.Session.CreateWorld();
        var hero = runtime.World.Heroes().Single(unit => unit.Team == Team.Order);
        hero.Level = 12;
        hero.Inventory = [new InventorySlot(3089), new InventorySlot(1036), null, null, null, null];
        // Batch 144: Deathcap's 30 percent is ZhonyasRing's own update loop now, not a HeroStatCalculator multiply. The
        // shipped script reads the flat pool, takes its own prior share back off it and keeps 30 percent of what is
        // left, so the number it settles on only exists after the item poll and a few buff updates have run against a
        // refreshed hero. Batch 150 part 2a samples that loop on the 0.25 s stats grid (BbBuffRuntime.StatsUpdateInterval),
        // not every update: the share is geometric (about 0.23 of the remaining gap per sample), so it takes fifteen
        // samples - five seconds of these 0.01 s steps - to close within the 1e-8 below. Twelve steps only saw the
        // inibin 140 and one sample. This drives the same two phases ProductionMatchComposition.Tick does; the session
        // is not started here, so MatchRuntime.Tick itself would be a no-op.
        for (var step = 0; step < 500; step++)
        {
            runtime.Spells.Tick(0.01);
            foreach (var unit in runtime.World.Heroes()) runtime.Buffs.TickBuffs(unit, 0.01);
            runtime.HeroStats.Refresh(hero);
        }
        foreach (var cougar in new[] { false, true })
        {
            if (cougar) hero.ScriptCharacterData = new ScriptCharacterLayer(1, "Nidalee_Cougar", OverrideSpells: true);
            runtime.HeroStats.Refresh(hero);
            TestAssert.Near(182, hero.AbilityPower, 1e-8, "Deathcap's 30% is part of flat AP");
            var baseAttack = data.StatsAtLevel(data.Champion(cougar ? "Nidalee_Cougar" : "Nidalee"), 12).AttackDamage;
            foreach (var type in new[] { UnitType.Hero, UnitType.Minion, UnitType.Monster, UnitType.Turret, UnitType.Structure })
            {
                var target = new Entity(900, EntityKind.Unknown, type, Team.Chaos, new(7050, 7000));
                // Batch 203: the AP contribution is the shipped APBonusDamageToTowers BuffOnHitUnit's (the hand-written pre-damage branch is
                // gone). Its turret test and its non-AI else branch make buildings the only targets that take it.
                var actual = runtime.Dependencies.CombatHooks.ModifyBasicAttackDamage(hero, target, hero.AttackDamage);
                TestAssert.Near(type is UnitType.Turret or UnitType.Structure ? baseAttack + 182 / 2.5 : hero.AttackDamage,
                    actual, 1e-8, "only buildings receive the AP contribution in either form");
            }
        }
        var tower = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret), EntityKind.Turret,
            UnitType.Turret, Team.Chaos, new(7050, 7000)) { Health = 10000, MaxHealth = 10000 });
        hero.Position = new(7000, 7000);
        hero.AttackWindup = 0.5;
        hero.MissileSpeed = 0;
        var launchDamage = hero.AttackDamage;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, tower, new AttackRolls(1, 1, 1)));
        hero.AbilityPower = 300;
        runtime.Combat.CombatTick(0.6, regenerate: false);
        runtime.Combat.CombatTick(0.001, regenerate: false);
        TestAssert.Near(launchDamage + 120 - 10, 10000 - tower.Health, 1e-8,
            "the on-hit script reads current flat modifiers after the windup");
        var minion = new Entity(901, EntityKind.Minion, UnitType.Minion, Team.Order, hero.Position)
            { ChampionName = "Nidalee", AbilityPower = 1000, AttackDamage = 50 };
        TestAssert.Equal(50.0, runtime.Dependencies.CombatHooks.ModifyBasicAttackDamage(minion, tower, 50));

        // The clone of Hallucinate: its swing against a tower is the script's. HallucinateFull.BuffOnHitUnit zeroes the swing and deals
        // 75 percent of it as Shaco's proc, half of that against a turret (batch 106, row 24: the hand-written scalars of
        // MatchGameplayHooks are deleted), so the clone has to come from the cast that gives it its script buffs.
        using var shacoMatch = new BbM3.Match("Shaco");
        var shaco = shacoMatch.Hero;
        shacoMatch.Learn(1);
        shacoMatch.Refill();
        shacoMatch.Foe.Position = new Position2(12000, 12000);
        TestAssert.True(shacoMatch.Cast(3), "Hallucinate cast accepted");
        for (var tick = 0; tick < 120 && !shacoMatch.Runtime.World.Entities.Values.Any(unit =>
                 unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == shaco.NetId); tick++)
            shacoMatch.Advance(BbM3.Step);
        var clone = shacoMatch.Runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == shaco.NetId);
        // The clone's own stats, as the original check set them on Shaco before the clone existed: 20 bonus attack damage, 100 ability power.
        var shacoBase = data.StatsAtLevel(data.Champion("Shaco"), clone.Level).AttackDamage;
        clone.AttackDamage = shacoBase + 20;
        clone.AbilityPower = 100;
        shaco.AbilityPower = 1000;
        var cloneTower = shacoMatch.Runtime.World.Register(new Entity(shacoMatch.Runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, Team.Chaos, clone.Position + new Position2(50, 0))
            { Health = 10000, MaxHealth = 10000 });
        // Batch 203: the clone's tower bonus is the shipped APBonusDamageToTowers add (Hallucinate's TargetExecute gives it to the clone,
        // HallucinateApplicator too), not the hand-written pre-damage branch. Two buffs run on the swing and the older one goes first;
        // the hook order is in docs/scripting/building-blocks-engine.md. HallucinateFull zeroes the swing and deals 75 percent of it as Shaco's
        // proc, half of that against a turret. The bonus then lands on the zeroed swing: its own 100 AP / 2.5 less the clone's 20 bonus
        // attack damage, not Shaco's later 1000 AP. The proc does not carry it, so the total is 0.375 x the clone's attack damage plus 20.
        TestAssert.True(shacoMatch.Runtime.Combat.ApplyHit(clone.NetId, cloneTower.NetId, clone.AttackDamage,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near(clone.AttackDamage * 0.75 * 0.5 + 20, 10000 - cloneTower.Health, 1e-6,
            "HallucinateFull's 75 percent, halved against a turret, of the swing; then the shipped bonus of 20 on the zeroed swing");
    }

    [Check("combat.hero-building-ap")]
    public static void EveryCurrentChampionUsesTheShippedBuildingApRule()
    {
        var data = GameData.Default;
        foreach (var champion in ChampionCatalog.PlayableNames)
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data,
                orderChampion: champion, chaosChampion: champion);
            runtime.Session.CreateWorld();
            // Batch 293: CharScriptJax.luaobj adds APBonusDamageToTowers in its UpdateSelfBuffActions, not in CharOnActivate like the other
            // script champions, so Jax (batch 273) carries the tower bonus from his first self-update; one step runs it (a holder's first
            // poll runs at once, SpellSystem.StepSelfUpdate).
            runtime.Spells.Tick(1.0 / 30.0);
            foreach (var hero in runtime.World.Heroes())
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "APBonusDamageToTowers"), $"{champion} {hero.Team} carries the tower bonus");
            var queue = new CapturingCombatPacketQueue();
            var combat = new CombatService(runtime.World, runtime.Dependencies.CombatHooks,
                new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()),
                new FixedRandomSource(0.5));
            foreach (var hero in runtime.World.Heroes())
            foreach (int level in new[] { 1, 7, 18 })
            foreach (var (bonusAd, ap) in new[] { (0.0, 0.0), (20.0, 100.0), (40.0, 100.0), (60.0, 100.0) })
            foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
            {
                hero.Level = level;
                hero.Position = new(7000, 7000);
                hero.AttackDamage = data.StatsAtLevel(data.Champion(champion), level).AttackDamage + bonusAd;
                hero.AbilityPower = ap;
                hero.AttackCooldown = hero.AttackWindup = hero.MissileSpeed = 0;
                var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Structure),
                    kind, kind == EntityKind.Turret ? UnitType.Turret : UnitType.Structure,
                    hero.Team == Team.Order ? Team.Chaos : Team.Order, new(7050, 7000))
                    { Health = 10000, MaxHealth = 10000, Armor = 100, SpellBlock = 900 });
                queue.Clear();
                // Batch 336: the expectation is taken before the swing. Caitlyn's seventh stacked hit readies CaitlynHeadshot
                // (CharScriptCaitlyn's CharOnPreAttack), and that add refreshes her stats, so after the swing AttackDamage is her stats'
                // value again, not the bonus this check wrote over it; the swing itself was dealt with the written value.
                var expected = (hero.AttackDamage + Math.Max(0, ap / 2.5 - bonusAd)) / 2;
                TestAssert.True(combat.QueueBasicAttack(hero, target, new AttackRolls(1, 1, 1)));
                combat.CombatTick(0.001, regenerate: false);
                // Production's building damage is float32 on this path: MasterYi level 1 with 40 bonus attack damage hits a turret
                // for 49.10999870300293 (the float32 base 58.21999740600586 plus 40), where the double expectation is 49.10999953746796,
                // 8.3e-7 apart. One float32 step at 49 is 3.8e-6, so the tolerance is a float32 one (as the 0x68 packet check below).
                TestAssert.Near(expected, 10000 - target.Health, 1e-5, $"{champion} {hero.Team} level {level} {kind}");
                var packet = queue.Packets.Single(p => p.Plaintext.Span[0] == 0x68).Plaintext.Span;
                TestAssert.Near(expected, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packet[14..]), 1e-5);
                TestAssert.Equal((byte)0x84, packet[5], "the native result remains an ordinary hit");
                var before = target.Health;
                TestAssert.True(combat.ApplyHit(hero.NetId, target.NetId, 100, DamageType.Physical, DamageSourceKind.Spell));
                TestAssert.Near(50, before - target.Health, 1e-8, "spells do not receive this attack bonus");
                runtime.World.Entities.Remove(target.NetId);
            }
        }
    }

    [Check("combat.injected-base-ad-spell-formulas")]
    public static void SpellFormulasUseInjectedBaseAdForEzrealAndVayne()
    {
        foreach (var (champion, slot, spellName, bonusScale) in new[]
        {
            ("Ezreal", 3, "EzrealTrueshotBarrage", 1.0),
            ("Vayne", 2, "VayneCondemn", 0.5),
        })
        {
            var data = new GameData();
            var championData = data.Champion(champion);
            var defaultBase = GameData.Default.StatsAtLevel(
                GameData.Default.Champion(champion), 1).AttackDamage;
            var injected = championData with { BaseDamage = championData.BaseDamage + 1000.0 };
            ReplaceChampion(data, champion, injected);
            var injectedBase = data.StatsAtLevel(data.Champion(champion), 1).AttackDamage;
            TestAssert.True(injectedBase != defaultBase, $"{champion} injected base AD must differ");
            TestAssert.Equal(defaultBase, GameData.Default.StatsAtLevel(
                GameData.Default.Champion(champion), 1).AttackDamage);

            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data,
                orderChampion: champion, chaosChampion: "Annie", readySettleSeconds: 0.0);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            var hero = runtime.World.Heroes().Single(unit => unit.Team == Team.Order);
            var target = runtime.World.Heroes().Single(unit => unit.Team == Team.Chaos);
            hero.Position = new Position2(7000.0, 7000.0);
            target.Position = hero.Position + new Position2(100.0, 0.0);
            hero.SpellRanks[slot] = 1;
            hero.SpellCooldowns[slot] = 0.0;
            // playtest B4 / retail sweep, 27 Sept 2026: passive stat refreshes
            // must retain the probe's bonus AD through the delayed impact.
            runtime.Buffs.AddBuff(hero, "InjectedAdProbe", BuffType.CombatEnhancer,
                30, hero.NetId, new BuffPayload { AttackDamage = 37.0 });
            TestAssert.Near(injectedBase + 37.0, hero.AttackDamage, 1e-8);
            hero.AbilityPower = 0.0;
            hero.Mana = hero.MaxMana = 100000.0;
            target.Health = target.MaxHealth = 100000.0;
            target.Armor = target.SpellBlock = 0.0;
            target.Protected = false;

            var point = new Position3(target.Position.X, 0.0, target.Position.Z);
            TestAssert.True(runtime.Spells.HandleCastRequest(hero,
                new SpellCastRequest(slot, false, point, point, target.NetId), 0.0),
                $"{champion} spell cast must be accepted");
            for (var i = 0; i < 200 && target.Health == target.MaxHealth; i++)
            {
                runtime.Spells.Tick(0.1);
                runtime.Spells.TickLateChampions(0.1);
            }

            var rankCount = champion == "Ezreal" ? 3 : 5;
            var expected = data.Spell(spellName, rankCount).EffectNumber(1, 1) + 37.0 * bonusScale;
            TestAssert.Near(expected, 100000.0 - target.Health, 1e-8,
                $"{champion} spell must subtract injected rather than default base AD");
        }
    }

    [Check("combat.turret-innate-offense")]
    public static void LaneTurretsKeepShippedPenetrationAndDodgePiercing()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        var queue = new CapturingCombatPacketQueue();
        var combat = new CombatService(runtime.World, new TestHooks(),
            new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()),
            new FixedRandomSource(0));
        var turrets = runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Turret).ToArray();
        TestAssert.Equal(24, turrets.Length);
        // Batch 159: the pen is the character script's own now (UpdateSelfBuffStats of CharScriptOrderTurretNormal
        // .luaobj and its seven siblings: BBIncStat IncPercentArmorPenetrationMod 0.2), so it lands on the unit with
        // the first stats sample of the bound units — one spell update after the character is created, which this
        // poll is — and not at the hand-written Initialize any more. DodgePiercing is different: CharOnActivate's
        // BBSetDodgePiercing writes the entity field at the bind.
        runtime.Spells.UnitScripts!.Poll();
        foreach (var turret in turrets)
        {
            var tier = MapDefinitions.TurretTiers[turret.Name];
            if (tier == TurretTier.Fountain)
            {
                // Neither shrine character states a pen (ChaosTurretShrine's CharScript has no UpdateSelfBuffStats at
                // all; OrderTurretShrine has no shipped script).
                TestAssert.Equal(0.0, turret.ArmorPenetrationPercent, "fountain uses a separate character/spell path");
                continue;
            }
            TestAssert.Equal(0.2, turret.ArmorPenetrationPercent, turret.Name + " gets its shipped 20% penetration");
            TestAssert.True(turret.DodgePiercing, turret.Name + " cannot be dodged");
            TurretRules.Tick(turret, 1800, 0);
            TestAssert.Equal(0.2, turret.ArmorPenetrationPercent, "timed growth preserves innate offense");
            var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Hero),
                EntityKind.Hero, UnitType.Hero, turret.Team == Team.Order ? Team.Chaos : Team.Order,
                new(7050, 7000)) { Health = 1000, MaxHealth = 1000, Armor = 100, Dodge = 1 });
            turret.Position = new(7000, 7000);
            turret.Protected = false;
            queue.Clear();
            TestAssert.True(combat.ApplyHit(turret.NetId, target.NetId, 180, DamageType.Physical, DamageSourceKind.Spell));
            TestAssert.Near(900, target.Health, 1e-8, "20% penetration leaves 80 of 100 armor");
            var damage = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x68).Plaintext.Span;
            TestAssert.Near(100, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(damage[14..]), 1e-5);
            queue.Clear();
            TestAssert.True(combat.QueueBasicAttack(turret, target, new AttackRolls(1, 0, 1)));
            TestAssert.Equal((byte)0, queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x0D).Plaintext.Span[14],
                "guaranteed defender dodge does not choose the dodge animation");
            combat.CancelPendingHits(turret.NetId);
        }
    }

    [Check("combat.nonhero-attack-animations")]
    public static void SpawnedMinionsMonstersAndTibbersUseTheirOwnAttackVariants()
    {
        var data = GameData.Default;
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data,
            orderChampion: "Annie", chaosChampion: "Annie");
        runtime.Session.CreateWorld();
        var sources = new List<(Entity Unit, int[] Slots)>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            foreach (var type in new[] { MinionType.Melee, MinionType.Caster, MinionType.Siege, MinionType.Super })
            {
                var slots = (type, team) switch {
                    (MinionType.Siege, _) or (MinionType.Super, Team.Chaos) => new[] { 0, 0, 1 },
                    (MinionType.Melee, Team.Order) or (MinionType.Super, Team.Order) => new[] { 0, 1, 2 },
                    _ => new[] { 0, 1, 1 },
                };
                sources.Add((runtime.Waves!.SpawnFixture(team, type, 0, "C", 0, 0), slots));
            }
            var owner = runtime.World.Heroes().Single(hero => hero.Team == team);
            sources.Add((AnnieChecks.SummonTibbers(runtime, owner, 1, new(7000, 7000)), new[] { 0, 1, 1 }));
        }
        var jungle = new JungleScheduler(runtime.World, data);
        jungle.Start();
        jungle.Tick(1000);
        foreach (var member in jungle.Members.Values.DistinctBy(member => member.Name))
            sources.Add((member.Entity, member.Name == "LizardElder" ? new[] { 0, 1, 1 } : new[] { 0, 0, 0 }));
        var rng = new AdjustableRandom();
        var queue = new CapturingCombatPacketQueue();
        var combat = new CombatService(runtime.World, new TestHooks(),
            new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()), rng);
        var rolls = new[] { 0.1, 0.5, 0.85 };
        foreach (var (source, slots) in sources)
        {
            TestAssert.True(source.BasicAttackAnimationProbabilities.SequenceEqual(
                data.Champion(source.Name).BasicAttackAnimationProbabilities), source.Name + " loads its own character weights");
            source.Position = new(7000, 7000);
            source.Waypoints.Clear();
            source.Target = null;
            source.AttackWindup = source.MissileSpeed = 0;
            for (int i = 0; i < rolls.Length; i++)
            {
                var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, source.Team == Team.Order ? Team.Chaos : Team.Order,
                    new(7050, 7000)) { Health = 10000, MaxHealth = 10000 });
                source.AttackCooldown = 0;
                rng.Value = rolls[i];
                var calls = rng.Calls;
                queue.Clear();
                TestAssert.True(combat.QueueBasicAttack(source, target, new AttackRolls(1, 1, 1)));
                TestAssert.Equal(slots[i] == 0 ? (byte)0 : (byte)(0x40 + slots[i]),
                    queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x0D).Plaintext.Span[14],
                    $"{source.Name} roll {rolls[i]} selects its native variant");
                TestAssert.Equal(calls + (source.BasicAttackAnimationProbabilities.Count > 1 ? 1 : 0), rng.Calls);
                var damage = CombatRules.BasicAttackDamage(source, target, new AttackRolls(1, 1, 1)).Damage;
                combat.CombatTick(0.001, regenerate: false);
                TestAssert.Near(10000 - damage, target.Health, 1e-8, "animation selection leaves ordinary damage unchanged");
            }
        }
    }

    [Check("combat.hero-karma-boundaries")]
    public static void HeroKarmaMatchesNativeChanceTransitionsAndLifetime()
    {
        var data = GameData.Default;
        TestAssert.Equal(201, data.CriticalKarmaConstants.Count);
        TestAssert.Equal(0f, data.CriticalKarmaConstants[0]);
        TestAssert.Equal(1f, data.CriticalKarmaConstants[200]);
        TestAssert.Equal(0.30814f, data.CriticalKarmaConstants[100]);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var rng = new AdjustableRandom { Value = 0.5 };
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
                new LifecycleStatsHooks(), new LifecycleStatsTransport());
            var queue = new CapturingCombatPacketQueue();
            var combat = new CombatService(world, new TestHooks(),
                new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter()), rng, stats);
            var target = world.Register(new Entity(100, EntityKind.Hero, UnitType.Hero,
                team == Team.Order ? Team.Chaos : Team.Order, new(7050, 7000))
                { Health = 1000, MaxHealth = 1000, Level = 1 });
            Entity Hero(uint id, double chance) => world.Register(new Entity(id, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { ChampionName = "Annie", AttackDamage = 100, CritChance = chance, CritMultiplier = 2,
                    Health = 1000, MaxHealth = 1000, AttackRange = 550, AttackPeriod = 1, Level = 1 });
            bool Critical(Entity hero) => combat.BasicAttackDamage(hero, target).Critical;

            var fast = Hero(1, 0.5);
            TestAssert.False(Critical(fast));
            int calls = rng.Calls;
            foreach (var chance in new[] { 0.0, (double)1e-6f, 1.0, (double)0.999999f })
            {
                fast.CritChance = chance;
                TestAssert.Equal(chance > 0.5, Critical(fast));
                TestAssert.Equal(calls, rng.Calls, "native endpoint gates draw no random number");
            }
            fast.CritChance = 0.5;
            TestAssert.True(Critical(fast), "endpoint gates retained the earlier failed roll");
            TestAssert.False(Critical(Hero(2, 0.5)), "another hero starts without this history");

            var capped = Hero(3, 0.1);
            rng.Value = 0.99;
            for (int i = 0; i < 10; i++) TestAssert.False(Critical(capped));
            rng.Value = 0.13;
            TestAssert.False(Critical(capped), "failure multiplier stays capped at five: 5 * 0.02479 < 0.13");
            rng.Value = 0.12;
            TestAssert.True(Critical(capped));
            TestAssert.False(Critical(capped), "success resets the multiplier to one");

            var increasing = Hero(4, 0.1);
            rng.Value = 0.99;
            for (int i = 0; i < 4; i++) TestAssert.False(Critical(increasing));
            increasing.CritChance = 0.5;
            rng.Value = 0.85;
            TestAssert.True(Critical(increasing), "native increase branch retains the new capped threshold for this roll");
            rng.Value = 0.5;
            TestAssert.False(Critical(increasing));

            var retained = Hero(15, 0.5);
            rng.Value = 0.99;
            for (int i = 0; i < 4; i++) TestAssert.False(Critical(retained));
            retained.CritChance = 0.6;
            rng.Value = 0.91;
            TestAssert.False(Critical(retained), "increase ratio above one retains the old 0.90002 threshold");
            TestAssert.True(Critical(retained), "retained fractional multiplier advances after failure");

            var decreasing = Hero(5, 0.5);
            rng.Value = 0.9;
            TestAssert.False(Critical(decreasing));
            decreasing.CritChance = 0.1;
            rng.Value = 0.07;
            TestAssert.False(Critical(decreasing));
            TestAssert.True(Critical(decreasing), "decreasing chance keeps the accumulated multiplier");
            rng.Value = 0.305;
            TestAssert.False(Critical(Hero(6, 0.4998)), "lookup truncates below the half-percent boundary");
            TestAssert.True(Critical(Hero(7, 0.49999)), "native epsilon crosses the lookup boundary");
            rng.Value = 0.30814f;
            TestAssert.True(Critical(Hero(8, 0.5)), "native success comparison includes equality");
            rng.Value = MathF.BitIncrement(0.30814f);
            TestAssert.False(Critical(Hero(9, 0.5)));

            var blinded = Hero(12, 0.5);
            rng.Value = 0.5;
            TestAssert.False(Critical(blinded));
            var blind = new Amberline.Core.Buffs.BuffState("test-blind",
                new Amberline.Core.Buffs.BuffPayload { MissChance = 1 });
            blinded.Buffs.Add(blind);
            TestAssert.True(combat.BasicAttackDamage(blinded, target).Missed);
            blinded.Buffs.Remove(blind);
            TestAssert.True(Critical(blinded), "a miss did not consume critical history");
            var piercer = Hero(13, 0);
            target.Dodge = 0.5;
            TestAssert.False(combat.BasicAttackDamage(piercer, target).Dodged);
            piercer.DodgePiercing = true;
            TestAssert.False(combat.BasicAttackDamage(piercer, target).Dodged);
            piercer.DodgePiercing = false;
            TestAssert.True(combat.BasicAttackDamage(piercer, target).Dodged, "piercing did not consume dodge history");
            target.Dodge = 0;
            var neutral = world.Register(new Entity(14, EntityKind.Neutral, UnitType.Monster, team, new(7000, 7000))
                { AttackDamage = 100, CritChance = 0.5 });
            rng.Value = 0.6;
            for (int i = 0; i < 6; i++) TestAssert.False(Critical(neutral), "non-hero attacks retain their uniform critical rule");

            var revived = Hero(10, 0.5);
            rng.Value = 0.5;
            TestAssert.False(Critical(revived));
            TestAssert.True(combat.ApplyHit(target.NetId, revived.NetId, 1000, DamageType.True, DamageSourceKind.Spell));
            TestAssert.True(combat.RespawnImmediately(revived));
            revived.CritChance = 0.5;
            TestAssert.True(Critical(revived), "respawning the same hero retains native probability history");

            var queued = Hero(11, 0.5);
            foreach (var critical in new[] { false, true, false })
            {
                queued.AttackCooldown = 0;
                queue.Clear();
                TestAssert.True(combat.QueueBasicAttack(queued, target));
                TestAssert.Equal(critical ? (byte)0x44 : (byte)0,
                    queue.Packets.Single(p => p.Plaintext.Span[0] == 0x0D).Plaintext.Span[14]);
                combat.CombatTick(0.01, regenerate: false);
                TestAssert.Equal(critical ? (byte)0x83 : (byte)0x84,
                    queue.Packets.Single(p => p.Plaintext.Span[0] == 0x68).Plaintext.Span[5]);
            }
            TestAssert.Equal(600.0, target.Health, "live probability results control both queued damage and packets");
        }
    }

    [Check("combat.hero-karma-probability")]
    public static void HeroCriticalAndDodgeRollsKeepNativeCategoryHistory()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { AttackDamage = 100, CritChance = 0.5, CritMultiplier = 2 });
            var other = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                team == Team.Order ? Team.Chaos : Team.Order, new(7050, 7000)));
            var another = world.Register(new Entity(3, EntityKind.Hero, UnitType.Hero, other.Team, other.Position));
            var minion = world.Register(new Entity(4, EntityKind.Minion, UnitType.Minion, other.Team, other.Position));
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            // Shipped Critical100 = 0.30814001: first 0.5 draw fails, second
            // succeeds at twice that threshold, then success resets history.
            TestAssert.False(combat.BasicAttackDamage(hero, other).Critical);
            TestAssert.True(combat.BasicAttackDamage(hero, another).Critical,
                "critical history is shared across enemy heroes, not keyed by victim");
            TestAssert.False(combat.BasicAttackDamage(hero, minion).Critical, "minion critical category starts independently");
            TestAssert.False(combat.BasicAttackDamage(hero, other).Critical, "hero critical success reset its multiplier");
            TestAssert.True(combat.BasicAttackDamage(hero, minion).Critical, "minion critical category retained its failure");
            hero.CritChance = 0;
            other.Dodge = 0.5;
            var alliedMinion = world.Register(new Entity(5, EntityKind.Minion, UnitType.Minion, team, hero.Position)
                { AttackDamage = 100 });
            TestAssert.False(combat.BasicAttackDamage(hero, other).Dodged);
            TestAssert.False(combat.BasicAttackDamage(alliedMinion, other).Dodged, "non-hero attacker has separate dodge history");
            TestAssert.True(combat.BasicAttackDamage(hero, other).Dodged);
            TestAssert.True(combat.BasicAttackDamage(alliedMinion, other).Dodged);
            TestAssert.False(combat.BasicAttackDamage(hero, other).Dodged, "dodge success resets history");
        }
    }

    [Check("combat.shipped-attack-animations")]
    public static void NormalAttacksUseShippedAnimationWeights()
    {
        var data = GameData.Default;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var name in new[] { "Annie", "Orianna", "LeeSin", "Nidalee", "Nidalee_Cougar",
                     "Yorick", "Shaco", "Ezreal", "Vayne", "Janna", "Blitzcrank" })
        {
            var expected = name switch {
                "LeeSin" => new[] { 0, 1, 1, 1, 2, 3, 0 },
                "Orianna" => new[] { 0, 1, 1, 1, 2, 2, 0 },
                "Ezreal" => new[] { 0, 0, 0, 0, 1, 1, 0 },
                _ => new[] { 0, 0, 1, 1, 1, 1, 0 },
            };
            var rolls = new[] { 0.1, 0.45, 0.5, 0.6, 0.8, 0.95, 1.0 };
            for (int i = 0; i < rolls.Length; i++)
            {
                var world = new WorldState();
                var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                    { ChampionName = name == "Nidalee_Cougar" ? "Nidalee" : name,
                        AttackSpeedMod = 1, Health = 1000, MaxHealth = 1000, AttackDamage = 100, AttackRange = 550 });
                if (name == "Nidalee_Cougar")
                    hero.ScriptCharacterData = new ScriptCharacterLayer(1, "Nidalee_Cougar", OverrideSpells: true);
                Amberline.Simulation.Stats.HeroAttackTimings.Apply(hero, data);
                hero.AttackWindup = hero.MissileSpeed = 0;
                var target = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion,
                    team == Team.Order ? Team.Chaos : Team.Order, new(7050, 7000))
                    { Health = 1000, MaxHealth = 1000 });
                var queue = new CapturingCombatPacketQueue();
                var combat = new CombatService(world, new TestHooks(),
                    new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter()),
                    new FixedRandomSource(rolls[i]));
                TestAssert.True(combat.QueueBasicAttack(hero, target, new AttackRolls(1, 1, 1)));
                var slot = queue.Packets.Single(p => p.Plaintext.Span[0] == 0x0D).Plaintext.Span[14];
                TestAssert.Equal(expected[i] == 0 ? (byte)0 : (byte)(0x40 + expected[i]), slot,
                    $"{name}, roll {rolls[i]} uses shipped cumulative animation probabilities");
                combat.CombatTick(0.01, regenerate: false);
                TestAssert.Equal(900.0, target.Health, "animation selection leaves attack damage unchanged");
            }
        }
    }

    [Check("combat.avoided-hit-feedback")]
    public static void MissAndDodgeFeedbackCannotApplyHitEffects()
    {
        var data = GameData.Default;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ranged in new[] { false, true })
        foreach (var dodge in new[] { false, true })
        foreach (var canceled in new[] { false, true })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { AttackDamage = 100, CritChance = 1, CritMultiplier = 2, AttackRange = 550,
                    BasicAttackAnimationProbabilities = data.Champion("LeeSin").BasicAttackAnimationProbabilities,
                    MissileSpeed = ranged ? 500 : 0, AttackWindup = 0.1, AttackPeriod = 1,
                    LifeSteal = 1, Health = 200, MaxHealth = 1000 });
            var miss = new Amberline.Core.Buffs.BuffState("test-miss",
                new Amberline.Core.Buffs.BuffPayload { MissChance = dodge ? 0 : 1 });
            hero.Buffs.Add(miss);
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                team == Team.Order ? Team.Chaos : Team.Order, new(7100, 7000))
                { Health = 700, MaxHealth = 1000, Dodge = dodge ? 1 : 0 });
            var shield = new Amberline.Core.Buffs.BuffState("test-shield",
                new Amberline.Core.Buffs.BuffPayload { DamageShield = 40 });
            target.Buffs.Add(shield);
            var queue = new CapturingCombatPacketQueue();
            var transport = new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter());
            var hooks = new TestHooks();
            var combat = new CombatService(world, hooks, transport, new FixedRandomSource(0.5));
            TestAssert.True(combat.QueueBasicAttack(hero, target, new AttackRolls(0, 0, 0)));
            var attack = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x0D);
            TestAssert.Equal(dodge ? (byte)0x46 : (byte)0x45, attack.Plaintext.Span[14]);
            if (canceled) TestAssert.Equal(1, combat.CancelPendingWindups(hero.NetId));
            combat.CombatTick(0.11, regenerate: false);
            // Batch 741: a melee swing's impact, and so its miss or dodge feedback, is its cast frame (the 0.1 s windup ends in this tick);
            // a missile's comes after its flight.
            TestAssert.Equal(!ranged && !canceled, queue.Packets.Any(packet => packet.Plaintext.Span[0] == 0x68), "no feedback before impact");
            target.Dodge = 0;
            hero.Buffs.Remove(miss);
            combat.CombatTick(0.21, regenerate: false);
            TestAssert.Equal(0, combat.PendingHits.Count);
            var impacts = queue.Packets.Where(packet => packet.Plaintext.Span[0] == 0x68).ToArray();
            TestAssert.Equal(canceled ? 0 : 1, impacts.Length, "canceled attacks have no later outcome");
            if (!canceled)
            {
                TestAssert.Equal(dodge ? (byte)2 : (byte)5, impacts[0].Plaintext.Span[5], "avoided impacts have no attack sound");
                TestAssert.Equal(0f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(impacts[0].Plaintext.Span[14..]));
            }
            TestAssert.Equal((200.0, 700.0, 40.0), (hero.Health, target.Health, shield.Payload.DamageShield),
                "no lifesteal, health damage or shield consumption");
            TestAssert.Equal(0, hooks.BasicHits);
            TestAssert.Equal(0, hooks.UnitsDamaged);
        }
    }

    [Check("combat.critical-hit-feedback")]
    public static void CriticalAttackFeedbackSurvivesWindupAndTravel()
    {
        var data = GameData.Default;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ranged in new[] { false, true })
        foreach (var critical in new[] { false, true })
        foreach (var dieAfterLaunch in new[] { false, true })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { ChampionName = ranged ? "Annie" : "LeeSin", AttackDamage = 100,
                    BasicAttackAnimationProbabilities = data.Champion(ranged ? "Annie" : "LeeSin").BasicAttackAnimationProbabilities,
                    CritChance = critical ? 1 : 0, CritMultiplier = 2.5, AttackRange = 550,
                    MissileSpeed = ranged ? 500 : 0, AttackWindup = 0.1, AttackPeriod = 1,
                    Health = 1000, MaxHealth = 1000 });
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
                team == Team.Order ? Team.Chaos : Team.Order, new(7100, 7000))
                { Health = 1000, MaxHealth = 1000, Armor = 25 });
            var queue = new CapturingCombatPacketQueue();
            var transport = new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter());
            var combat = new CombatService(world, new TestHooks(), transport, new FixedRandomSource(0.5));
            TestAssert.True(combat.QueueBasicAttack(hero, target, new AttackRolls(0.5, 1, 1)));
            var attack = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x0D);
            TestAssert.Equal(critical ? (byte)0x44 : (byte)0x41, attack.Plaintext.Span[14], "critical overrides weighted normal animation");
            combat.CombatTick(0.11, regenerate: false);
            // Batch 741: a melee swing's impact is its cast frame (the 0.1 s windup ends in this tick: the client's cast builder runs a
            // CastType 0 effect at once); a missile's comes after its flight. It used to wait for the next tick.
            TestAssert.Equal(!ranged, queue.Packets.Any(packet => packet.Plaintext.Span[0] == 0x68), "no premature impact");
            hero.CritChance = critical ? 0 : 1;
            hero.CritMultiplier = 7;
            if (dieAfterLaunch) combat.Kill(hero);
            combat.CombatTick(0.21, regenerate: false);
            var expectedDamage = critical ? 200.0 : 80.0;
            TestAssert.Near(expectedDamage, 1000 - target.Health, 1e-8, "damage keeps the original attack roll");
            var hit = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x68);
            TestAssert.Equal(critical ? (byte)0x83 : (byte)0x84, hit.Plaintext.Span[5],
                "impact presentation keeps the original roll even after a stat change or death");
            TestAssert.Near(expectedDamage,
                System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(hit.Plaintext.Span[14..]), 1e-5);
            queue.Clear();
            combat.QueueScriptedHit(hero, target, 10, DamageType.True, DamageSourceKind.Spell, 0);
            combat.CombatTick(0.01, regenerate: false);
            TestAssert.Equal((byte)0x84, queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x68)
                .Plaintext.Span[5], "scripted spell damage does not inherit a previous critical attack");
        }
    }

    [Check("combat.hero-critical-targets")]
    public static void ChampionCriticalStrikesExcludeBuildings()
    {
        var data = GameData.Default;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (kind, type, expectedCritical) in new[] {
            (EntityKind.Turret, UnitType.Turret, false),
            (EntityKind.Inhibitor, UnitType.Structure, false),
            (EntityKind.Nexus, UnitType.Structure, false),
            (EntityKind.Hero, UnitType.Hero, true),
            (EntityKind.Minion, UnitType.Minion, true),
            (EntityKind.Neutral, UnitType.Monster, true),
            (EntityKind.Pet, UnitType.Minion, true),
        })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { AttackDamage = 120, CritChance = 1, CritMultiplier = 2.5, AttackRange = 550,
                    AttackWindup = 0, AttackPeriod = 1, Health = 1000, MaxHealth = 1000 });
            var target = world.Register(new Entity(2, kind, type,
                team == Team.Order ? Team.Chaos : Team.Order, new(7050, 7000))
                { Health = 1000, MaxHealth = 1000, Armor = 20, AttackCooldown = 100 });
            var rolls = new AttackRolls(0, 1, 1);
            var result = CombatRules.BasicAttackDamage(hero, target, rolls);
            TestAssert.Equal(expectedCritical, result.Critical, $"critical eligibility against {kind}");
            TestAssert.Equal(expectedCritical ? 300.0 : 120.0, result.Damage);
            var queue = new CapturingCombatPacketQueue();
            var transport = new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter());
            var combat = new CombatService(world, new TestHooks(), transport, new FixedRandomSource(0.5));
            TestAssert.True(combat.QueueBasicAttack(hero, target, rolls));
            combat.CombatTick(0.01, regenerate: false);
            var expectedDamage = expectedCritical ? 250.0 : 100.0;
            TestAssert.Near(expectedDamage, 1000 - target.Health, 1e-8, "queued strike uses the same critical eligibility");
            var hit = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x68);
            TestAssert.Near(expectedDamage,
                System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(hit.Plaintext.Span[14..]), 1e-5);
        }
    }

    [Check("combat.lane-creep-score")]
    public static void LaneScoreCountsOnlyCreditedEnemyLastHits()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var owned in new[] { false, true })
        foreach (var alive in new[] { false, true })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { Alive = alive, Health = alive ? 100 : 0, MaxHealth = 100 });
            var nearby = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, team, hero.Position));
            var pet = world.Register(new Entity(3, EntityKind.Pet, UnitType.Minion, team, hero.Position)
                { OwnerNetId = hero.NetId });
            var enemyTeam = team == Team.Order ? Team.Chaos : Team.Order;
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            var count = 0;
            foreach (var role in new[] { "Melee", "Caster", "Cannon", "Super" })
            {
                var minion = world.Register(new Entity(world.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, enemyTeam, hero.Position) { Name = role });
                TestAssert.True(combat.Kill(minion, owned ? pet.NetId : hero.NetId));
                TestAssert.Equal(++count, hero.LaneMinionsKilled, $"{role} grants one last hit");
                TestAssert.False(combat.Kill(minion, hero.NetId));
                TestAssert.Equal(count, hero.LaneMinionsKilled, "duplicate death cannot add score");
            }
            foreach (var kind in new[] { EntityKind.Neutral, EntityKind.Pet, EntityKind.Ward, EntityKind.ShacoBox })
            {
                var unit = world.Register(new Entity(world.Allocate(UnitType.Minion), kind,
                    UnitType.Minion, enemyTeam, hero.Position));
                combat.Kill(unit, hero.NetId);
            }
            foreach (var (victimTeam, killer) in new[] { (team, hero.NetId), (enemyTeam, 0u), (enemyTeam, 99999u) })
            {
                var minion = world.Register(new Entity(world.Allocate(UnitType.Minion), EntityKind.Minion,
                    UnitType.Minion, victimTeam, hero.Position));
                combat.Kill(minion, killer);
            }
            var alliedMinion = world.Register(new Entity(world.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, team, hero.Position));
            var damagedMinion = world.Register(new Entity(world.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, enemyTeam, hero.Position) { Health = 100, MaxHealth = 100 });
            TestAssert.True(combat.ApplyHit(hero.NetId, damagedMinion.NetId, 10,
                DamageType.True, DamageSourceKind.Spell));
            combat.RecordSupportAssist(hero, damagedMinion);
            TestAssert.True(combat.Kill(damagedMinion, alliedMinion.NetId));
            TestAssert.Equal(4, hero.LaneMinionsKilled, "other unit types and uncredited/friendly deaths add no lane score");
            TestAssert.Equal(0, nearby.LaneMinionsKilled, "proximity does not award last hits");
            TestAssert.Equal(0, pet.LaneMinionsKilled, "owned-unit last hits belong to the hero");
            TestAssert.Equal(1, hero.NeutralMinionsKilled, "jungle score remains independent");
        }
    }

    [Check("combat.pet-last-hit-score-delivery")]
    public static void PetLastHitsUseHeroDeathCreditAndCountOtherMinions()
    {
        var data = GameData.Default;
        var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var alive in new[] { false, true })
        {
            var world = new WorldState();
            var enemyTeam = team == Team.Order ? Team.Chaos : Team.Order;
            var hero = world.Register(Hero(1, team, 1000));
            hero.Alive = alive; hero.Health = alive ? 1000 : 0;
            var pet = world.Register(new Entity(2, EntityKind.Pet, UnitType.Monster, team, hero.Position)
                { OwnerNetId = hero.NetId });
            var nested = world.Register(new Entity(3, EntityKind.YorickGhoul, UnitType.Monster, team, hero.Position)
                { OwnerNetId = pet.NetId });
            var queue = new CapturingCombatPacketQueue();
            var transport = new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter());
            var combat = new CombatService(world, new TestHooks(), transport, new FixedRandomSource(0.5));
            var otherScore = 0;
            foreach (var kind in new[] { EntityKind.Pet, EntityKind.Ward, EntityKind.ShacoBox,
                         EntityKind.ShacoClone, EntityKind.YorickGhoul, EntityKind.YorickRevenant, EntityKind.Monster,
                         EntityKind.Minion, EntityKind.Neutral })
            {
                var victim = world.Register(new Entity(world.Allocate(UnitType.Monster), kind,
                    kind == EntityKind.Minion ? UnitType.Minion : UnitType.Monster,
                    kind == EntityKind.Neutral ? Team.Neutral : enemyTeam, hero.Position));
                var before = queue.Packets.Count;
                TestAssert.True(combat.Kill(victim, nested.NetId));
                TestAssert.False(combat.Kill(victim, nested.NetId));
                var death = queue.Packets.Skip(before).Single(packet => packet.Plaintext.Span[0] == 0xA6);
                TestAssert.Equal(hero.NetId,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(death.Plaintext.Span[5..]),
                    "native death events must reach the same hero as authoritative last-hit credit");
                TestAssert.False(queue.Packets.Skip(before).Any(packet => packet.Plaintext.Span[0] == 0xAB),
                    "visible deaths already dispatch additive score events");
                if (kind is not (EntityKind.Minion or EntityKind.Neutral)) otherScore++;
                TestAssert.Equal(otherScore, hero.OtherMinionsKilled);
            }
            foreach (var (victimTeam, killer) in new[] {
                         (team, nested.NetId), (enemyTeam, 0u), (enemyTeam, 99999u) })
            {
                var victim = world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Ward,
                    UnitType.Monster, victimTeam, hero.Position));
                combat.Kill(victim, killer);
            }
            TestAssert.Equal(7, hero.OtherMinionsKilled, "expiry, friendly and unknown killers grant no score");
            TestAssert.Equal(1, hero.LaneMinionsKilled);
            TestAssert.Equal(1, hero.NeutralMinionsKilled);
            TestAssert.Equal(0, pet.OtherMinionsKilled);
            TestAssert.Equal(0, nested.OtherMinionsKilled);
        }
    }

    [Check("combat.season-one-armor-penetration")]
    public static void ArmorPenetrationUsesThe2011OrderAndZeroFloor()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var viaPetSpell in new[] { false, true })
        foreach (var (armor, flat, percent, raw, expected) in new[] {
            (120.0, 20.0, 0.4, 160.0, 100.0), // Riot's April 2011 example: 60 effective armor.
            (20.0, 5.0, 0.0, 115.0, 100.0),
            (20.0, 50.0, 0.4, 100.0, 100.0),
            (0.0, 50.0, 0.4, 100.0, 100.0),
            (-20.0, 50.0, 0.4, 120.0, 140.0), // Reduction-created negative armor keeps its bonus.
            (120.0, 0.0, 0.4, 172.0, 100.0),
            (120.0, 20.0, 1.0, 100.0, 100.0),
        })
        {
            var world = new WorldState();
            var source = world.Register(Hero(1, team, 10000));
            source.ArmorPenetrationFlat = flat;
            source.ArmorPenetrationPercent = percent;
            var target = world.Register(Hero(2, team == Team.Order ? Team.Chaos : Team.Order, 10000));
            target.Armor = armor;
            var pet = world.Register(new Entity(3, EntityKind.Pet, UnitType.Minion, team, source.Position)
                { OwnerNetId = source.NetId });
            var queue = new CapturingCombatPacketQueue();
            var data = GameData.Default;
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
                new LifecycleStatsHooks(), new LifecycleStatsTransport());
            var transport = new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter());
            var combat = new CombatService(world, new TestHooks(), transport, new FixedRandomSource(0.5));
            TestAssert.True(combat.ApplyHit(viaPetSpell ? pet.NetId : source.NetId, target.NetId,
                raw, DamageType.Physical, viaPetSpell ? DamageSourceKind.Spell : DamageSourceKind.Basic,
                spellOwnerNetId: viaPetSpell ? source.NetId : null));
            TestAssert.Near(expected, 10000 - target.Health, 1e-8,
                $"armor {armor}, flat {flat}, percent {percent}, pet spell {viaPetSpell}");
            TestAssert.Equal(armor, target.Armor, "penetration does not alter the target's real armor");
            var damage = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0x68);
            TestAssert.Near(expected,
                System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(damage.Plaintext.Span[14..]), 1e-5,
                "the stock damage packet receives the mitigated value");
        }
    }

    [Check("combat.neutral-creep-score")]
    public static void NeutralCreepScoreCreditsLastHitsAndOwners()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var owned in new[] { false, true })
        foreach (var alive in new[] { false, true })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { Alive = alive, Health = alive ? 100 : 0, MaxHealth = 100 });
            var other = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, team, hero.Position));
            var pet = world.Register(new Entity(3, EntityKind.Pet, UnitType.Minion, team, hero.Position)
                { OwnerNetId = hero.NetId });
            var queue = new CapturingCombatPacketQueue();
            var data = GameData.Default;
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
                new LifecycleStatsHooks(), new LifecycleStatsTransport());
            var transport = new ProtocolCombatTransport(queue, stats, _ => true, new ReplicationSyncCounter());
            var combat = new CombatService(world, new TestHooks(), transport, new FixedRandomSource(0.5));
            var count = 0;
            foreach (var name in new[] { "YoungLizard", "LizardElder", "AncientGolem", "GiantWolf",
                         "Wolf", "Wraith", "LesserWraith", "Golem", "SmallGolem", "Dragon", "Worm" })
            {
                var monster = world.Register(new Entity(world.Allocate(UnitType.Monster),
                    EntityKind.Neutral, UnitType.Monster, Team.Neutral, hero.Position) { Name = name });
                queue.Clear();
                TestAssert.True(combat.Kill(monster, owned ? pet.NetId : hero.NetId));
                TestAssert.Equal(++count, hero.NeutralMinionsKilled, $"{name} is one individual jungle kill");
                TestAssert.Equal(0, other.NeutralMinionsKilled, "proximity does not award a last hit");
                var score = queue.Packets.Single(packet => packet.Plaintext.Span[0] == 0xCC);
                var bytes = score.Plaintext.Span;
                TestAssert.Equal(23, bytes.Length);
                TestAssert.Equal((byte)8, bytes[10], "MAP_REPDATA only");
                TestAssert.Equal(hero.NetId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]));
                TestAssert.Equal(0x200u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]));
                TestAssert.Equal(count, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[19..]));
                TestAssert.True(queue.GlobalPackets.Contains(score), "score is published without revealing the monster");
                TestAssert.Equal(LeaguePacketReliability.Reliable, score.Reliability);
                TestAssert.False(combat.Kill(monster, hero.NetId));
                TestAssert.Equal(count, hero.NeutralMinionsKilled, "duplicate death cannot add score");
            }
            foreach (var kind in new[] { EntityKind.Minion, EntityKind.Pet, EntityKind.Ward, EntityKind.ShacoBox })
            {
                var unit = world.Register(new Entity(world.Allocate(UnitType.Minion), kind,
                    UnitType.Minion, team == Team.Order ? Team.Chaos : Team.Order, hero.Position));
                combat.Kill(unit, hero.NetId);
            }
            var uncredited = world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral,
                UnitType.Monster, Team.Neutral, hero.Position));
            combat.Kill(uncredited);
            TestAssert.Equal(count, hero.NeutralMinionsKilled, "non-jungle and uncredited deaths add no neutral score");
        }
    }

    [Check("combat.protection-relocks-when-every-dampener-is-back")]
    public static void HqTowersAndNexusRelockWhenEveryDampenerIsBack()
    {
        // Map1/LevelScript.luaobj: HandleDestroyedObject (proto 0.14) releases the HQ towers when a dampener dies,
        // DeactivateCorrectStructure (proto 0.7) releases the nexus when both HQ towers are dead, and the respawn
        // handler (proto 0.13) runs ApplyBarracksRespawnReductions (proto 0.12), which re-locks the HQ and every standing
        // HQ tower once the team's barracks count is back to 3 (pc 0147-0198 and 0340-0390). Batch 764: the ladder
        // kept the releases for good (the 16 October one-way latch), so after the dampeners returned the nexus and its
        // towers stayed open to minions and heroes.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var side = team == Team.Order ? "T1" : "T2";
            var world = new WorldState();
            Entity Add(uint id, EntityKind kind, UnitType type, string name) => world.Register(
                new Entity(id, kind, type, team, Position2.Zero) { Name = name, Health = 1000, MaxHealth = 1000 });
            var first = Add(1, EntityKind.Turret, UnitType.Turret, $"Turret_{side}_C_01");
            var second = Add(2, EntityKind.Turret, UnitType.Turret, $"Turret_{side}_C_02");
            var inhibitor = Add(3, EntityKind.Inhibitor, UnitType.Structure, $"Barracks_{side}_C1");
            var other = Add(5, EntityKind.Inhibitor, UnitType.Structure, $"Barracks_{side}_L1");
            var nexus = Add(4, EntityKind.Nexus, UnitType.Structure, team == Team.Order ? "HQ_T1" : "HQ_T2");
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            // Batch 314: the map script arms the dampener's return (SetDisableMinionSpawn, 300 s); this bare service has none.
            combat.BarracksChanged = (barracks, destroyed) => { if (destroyed) combat.ArmInhibitorReturn(barracks, 300.0); };
            void Wait(double seconds)
            {
                combat.CombatTick(seconds, regenerate: false);
                combat.ProcessRespawns();
            }
            combat.EngageProtection();
            TestAssert.True(first.Protected && second.Protected && nexus.Protected, $"{team} starts fully protected");

            TestAssert.True(combat.Kill(inhibitor, 0));
            TestAssert.False(first.Protected || second.Protected, "a dampener death releases both HQ towers");
            TestAssert.True(nexus.Protected, "the nexus waits for both HQ towers");
            Wait(300.0);
            TestAssert.True(inhibitor.Alive, "the dampener respawned");
            TestAssert.True(first.Protected && second.Protected, "every dampener is back: the HQ towers are locked again");
            TestAssert.True(nexus.Protected);

            // Two dampeners down; the first one back is not enough.
            TestAssert.True(combat.Kill(inhibitor, 0));
            Wait(100.0);
            TestAssert.True(combat.Kill(other, 0));
            TestAssert.True(combat.Kill(first, 0));
            TestAssert.True(nexus.Protected, "one HQ tower down: the nexus stays protected");
            TestAssert.True(combat.Kill(second, 0));
            TestAssert.False(nexus.Protected, "both HQ towers down: the nexus is vulnerable");
            Wait(200.0);
            TestAssert.True(inhibitor.Alive && !other.Alive, "the first dampener is back, the second is not");
            TestAssert.False(nexus.Protected, "a dampener is still down: the nexus stays open");
            Wait(100.0);
            TestAssert.True(other.Alive, "the second dampener is back");
            TestAssert.True(nexus.Protected, "every dampener is back: the nexus is locked again with both HQ towers gone");
            TestAssert.False(first.Protected || second.Protected, "a fallen HQ tower is not re-locked (GetTurret answers Nil)");

            // The next dampener death opens the nexus again: both HQ towers are gone (proto 0.14 pc 0094-0110).
            TestAssert.True(combat.Kill(other, 0));
            TestAssert.False(nexus.Protected, "a dampener down with both HQ towers gone: the nexus is vulnerable again");
        }
    }

    [Check("combat.turret-backdoor-search-cadence")]
    public static void BackdoorSearchRunsOncePerSecondStartingAtOneSecond()
    {
        // Spells/TurretAssistManager.luaobj runs BBForEachUnitInTargetAreaRandom in BuffOnUpdateActions and every
        // turret character script adds the buff with TickRate 1.0: a search once per second, the first at 1.0 s.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        runtime.Session.CreateWorld();
        runtime.Spells.UnitScripts!.Poll();
        var turret = runtime.World.Entities.Values.First(unit => unit.UnitType == UnitType.Turret &&
            MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier == TurretTier.Outer &&
            unit.Team == Team.Order);
        turret.AttackRange = 0;
        var rest = turret.Armor;
        var open = rest - TurretRules.BackdoorResistance;
        var near = new Position2(turret.Position.X + 500, turret.Position.Z);
        var far = new Position2(turret.Position.X + 1050, turret.Position.Z);
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Chaos, near) { Health = 1000, MaxHealth = 1000, Radius = 35.7437,
                MinionType = MinionType.Melee });
        var clock = 0.0;
        void Advance(double until)
        {
            while (clock + 1e-9 < until)
            {
                runtime.Spells.Tick(0.1);
                runtime.Buffs.TickBuffs(turret, 0.1);
                clock += 0.1;
            }
        }

        Advance(0.8);
        TestAssert.Equal(rest, turret.Armor, "no search before the first TickRate period elapses");
        Advance(1.2);
        TestAssert.Equal(open, turret.Armor, "the 1.0 s search finds the enemy lane minion");
        Advance(1.4);
        minion.Position = far; // leaves between searches
        Advance(8.8);
        TestAssert.Equal(open, turret.Armor, "the buff lasts eight seconds from the 1.0 s search");
        Advance(9.2);
        TestAssert.Equal(rest, turret.Armor,
            "it ends 8 s after the last search that saw the minion (1.0 s), not after the last step that did (1.4 s)");
        minion.Position = near; // returns between searches
        Advance(9.8);
        TestAssert.Equal(rest, turret.Armor, "a minion arriving between searches waits for the next whole second");
        Advance(10.3);
        TestAssert.Equal(open, turret.Armor, "the 10.0 s search picks it up");
    }

    [Check("combat.turret-champion-delta")]
    public static void TurretChampionDeltaScalesPhysicalDamageByRosterBalance()
    {
        // Batch 159 part 1: the champion delta is the turret's own buff chain now. Every lane character adds
        // Spells/TurretChampionDelta.luaobj at the character's creation (CharScriptOrderTurretNormal / Normal2 /
        // Dragon / Angel and their four Chaos counterparts, Duration 25000, TickRate 10) and neither shrine
        // character names it. The add's BuffOnUpdateStats wraps BBExecutePeriodically 10 s (ExecuteImmediately
        // false, so the first line is one period in): it counts the heroes of each team
        // (BBGetNumberOfHeroesOnTeam, IncludeBots true) and adds NegativeTurretDelta when its own team has more,
        // PositiveTurretDelta when it has fewer (BBSpellBuffAdd BUFF_RENEW_EXISTING, Duration 21.0), clearing the
        // other side's buff first; equal clears both. Spells/PositiveTurretDelta.luaobj and Spells/
        // NegativeTurretDelta.luaobj then write IncPercentPhysicalReduction of +/-0.05 ramped min(t - onset, 90) / 90
        // (StartTime of OnBuffActivate) through their own BuffOnUpdateStats, which is the payload
        // CombatService.ApplyReduction sums into every physical hit. The hand-written UpdateTurretChampionDelta and
        // TurretRules.DeltaPhysicalReduction this check used to drive are deleted with the rule they stood for.
        // The steps are 0.25 s — the stats grid of BbBuffRuntime — so a sample lands on every asserted instant.
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Player1", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Player2", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "Player3", "Orianna", Team.Chaos),
        };
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster);
        runtime.Session.CreateWorld();
        runtime.Spells.UnitScripts!.Poll();
        var turrets = runtime.World.Entities.Values
            .Where(unit => unit.UnitType == UnitType.Turret && MapDefinitions.TurretTiers.ContainsKey(unit.Name))
            .ToArray();
        Entity One(TurretTier tier, Team team) => turrets
            .Where(turret => MapDefinitions.TurretTiers[turret.Name] == tier && turret.Team == team)
            .OrderBy(turret => turret.Name, StringComparer.Ordinal).ThenBy(turret => turret.NetId).First();
        var orderTurret = One(TurretTier.Outer, Team.Order);
        var chaosTurret = One(TurretTier.Outer, Team.Chaos);
        var attacker = runtime.World.Heroes().Single(hero => hero.Team == Team.Chaos);
        var orderExtra = runtime.World.Heroes().Where(hero => hero.Team == Team.Order)
            .OrderBy(hero => hero.NetId).Last();
        var clock = 0.0;
        void Advance(double until)
        {
            while (clock + 1e-9 < until)
            {
                runtime.Spells.Tick(0.25);
                foreach (var turret in turrets) runtime.Buffs.TickBuffs(turret, 0.25);
                clock += 0.25;
            }
        }
        double Reduction(Entity turret) => turret.Buffs.Sum(buff => buff.Payload.PercentPhysicalReduction);
        double Taken(Entity turret, DamageType type)
        {
            turret.Health = turret.MaxHealth;
            var before = turret.Health;
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, turret.NetId, 1000, type, DamageSourceKind.Spell),
                "the probe hit lands on the turret");
            return before - turret.Health;
        }
        double Expected(Entity turret, DamageType type, double reduction) => 1000 *
            CombatRules.ResistanceMultiplier(type == DamageType.Physical ? turret.Armor : turret.SpellBlock) *
            (type == DamageType.Physical ? 1.0 - reduction : 1.0);
        void Delta(Entity turret, double reduction, string what)
        {
            TestAssert.Near(reduction, Reduction(turret), 1e-9, what + ": the reduction the script wrote");
            TestAssert.Near(Expected(turret, DamageType.Physical, reduction), Taken(turret, DamageType.Physical),
                1e-6, what + ": the physical hit reads it back");
        }

        // Order has two heroes, Chaos one. Both lane turrets carry the delta manager's own buff from their
        // character, and nothing of it is decided before the first 10 s line.
        TestAssert.True(orderTurret.Buffs.Any(buff => buff.HasName("TurretChampionDelta")),
            "the lane character's own TurretChampionDelta is on the turret");
        Advance(9.5);
        TestAssert.False(orderTurret.Buffs.Any(buff => buff.HasName("NegativeTurretDelta") ||
            buff.HasName("PositiveTurretDelta")), "the first evaluation has not run");
        Delta(orderTurret, 0.0, "no delta before the 10 s evaluation");

        // The ramp starts at the 10 s onset (the delta buff's own StartTime): about half way (45 s) at t = 55.
        Advance(55.0);
        var ramp = 0.05 * (clock - 10.0) / 90.0;
        TestAssert.True(ramp > 0.0245 && ramp < 0.0255, "the ramp is about half of 5 % after 45 s");
        Delta(orderTurret, -ramp, "the Order turret (2 v 1) takes up to 5 % more physical damage, ramped");
        Delta(chaosTurret, ramp, "the Chaos turret (1 v 2) takes up to 5 % less physical damage, ramped");

        Advance(100.1);
        Delta(orderTurret, -0.05, "t = 100: Order turret +5 % physical damage taken");
        Delta(chaosTurret, 0.05, "t = 100: Chaos turret -5 % physical damage taken");
        TestAssert.Near(Expected(orderTurret, DamageType.Magic, 0), Taken(orderTurret, DamageType.Magic), 1e-6,
            "magic damage is not changed");
        TestAssert.Near(1000.0, Taken(orderTurret, DamageType.True), 1e-9, "true damage is not changed");

        // An even roster (1 v 1) clears both deltas at the next evaluation (110 s).
        runtime.World.Remove(orderExtra.NetId);
        Advance(110.2);
        Delta(orderTurret, 0.0, "equal rosters: no delta on the Order turret");
        Delta(chaosTurret, 0.0, "equal rosters: no delta on the Chaos turret");

        // Neither fountain character adds TurretChampionDelta (only the eight lane characters name the buff), so
        // the shrines never carry a delta at any roster shape — the old check's shrine pin, from the buff itself.
        foreach (var turret in turrets.Where(turret => MapDefinitions.TurretTiers[turret.Name] == TurretTier.Fountain))
        {
            TestAssert.False(turret.Buffs.Any(buff => buff.HasName("TurretChampionDelta")),
                turret.Name + " has no TurretChampionDelta of its own");
            TestAssert.False(turret.Buffs.Any(buff => buff.HasName("NegativeTurretDelta") ||
                buff.HasName("PositiveTurretDelta")), turret.Name + " never takes a champion delta");
        }
        TestAssert.Equal(0, runtime.Spells.UnitScripts.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", runtime.Spells.UnitScripts.ScriptFailures
                .Select(failure => $"{failure.Script}.{failure.Hook} {failure.Message}")));
    }

    [Check("combat.shrine-data-stats-and-client-cadence")]
    public static void ShrineStatsAndCadenceComeFromTheShippedInibins()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var name = team == Team.Order ? "OrderTurretShrine" : "ChaosTurretShrine";
            var raw = GameData.Default.Champion(name).Raw;
            // The delay offset is stored in the 16-bit integer segment as 65535. The stock client's float getter
            // (the client's binary value getter) sign-extends
            // that segment ((int)(short)), so it reads -1. AsDouble is that client decode for every consumer; only
            // the raw UInt16 property (the source-backed corpora) still holds the unsigned 65535.
            var offset = raw[InibinReader.KeyHash("Data*AttackDelayOffsetPercent")];
            TestAssert.Equal(InibinValueKind.UInt16, offset.Kind);
            TestAssert.Equal(65535, (int)offset.UInt16);
            TestAssert.Equal(-1.0, offset.AsDouble());
            TestAssert.Equal(-1.0, offset.AsClientNumber());
            // Data*Armor is a stored False; Data*SpellBlock is absent.
            TestAssert.Equal(InibinValueKind.Boolean, raw[InibinReader.KeyHash("Data*Armor")].Kind);
            TestAssert.False(raw.ContainsKey(InibinReader.KeyHash("Data*SpellBlock")));

            // Batch 64 (131c1b67): AttackDelayCastOffsetPercent is a byte/10 value the stock client reads as the float32
            // 1 * 0.1f (the client's raw float lookup), so the cast fraction is 0.3 + 0.1f and the windup 0.4 times that:
            // 0.16 s plus 6e-10, not the decimal 0.16.
            var castFraction = 0.3 + (double)(1f * 0.1f);
            var windup = 0.4 * castFraction;
            TestAssert.Near(0.16, windup, 1e-8, "the shrine windup is 0.16 s to the shipped float32 precision");
            var stats = TurretRules.StatsFor(TurretTier.Fountain, team, GameData.Default);
            TestAssert.Equal(0.0, stats.Armor, "shrine armor is the stored 0, not the 30 fallback");
            TestAssert.Equal(0.0, stats.SpellBlock, "shrine MR is absent (0), not the 30 fallback");
            TestAssert.Equal(9999.0, stats.MaxHealth);
            TestAssert.Equal(999.0, stats.Damage);
            // Raw period 1.6 * (1 - 1) = 0 clamps to the global 0.4 s minimum; cast fraction 0.3 + 0.1 = 0.4.
            TestAssert.Near(0.4, stats.AttackPeriod, 1e-12, "shrine attack period");
            TestAssert.Near(windup, stats.Windup, 1e-12, "shrine windup");
            // Batch 204: the shipped shrine ranges, read from the data (the owner's 1250 is withdrawn).
            TestAssert.Equal(2250.0, stats.AttackRange, "the shipped shrine AttackRange");
            TestAssert.Equal(2250.0, stats.AcquisitionRange);
            // DATA/Spells/{Order,Chaos}TurretShrineBasicAttack.inibin store MissileSpeed 500 with CastType 0, and the client flies a
            // missile only for CastType 1, 2 and 3 (TurretRules.StatsFor, batch 527), so the shrine's attack carries no flight. The
            // hit already landed at the cast frame since 372cf235 (28 September 2026); batch 535 restates this pin.
            var spell = GameData.Default.Spell(name + "BasicAttack");
            TestAssert.Equal(500.0, spell.MissileSpeed, "the stored, unused MissileSpeed");
            TestAssert.Equal(0, BbChampionModule.CastType(spell), "the shipped shrine spell is CastType 0");
            TestAssert.Equal(0.0, stats.MissileSpeed, "a CastType 0 attack does not fly");

            var world = new WorldState();
            var shrine = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, team, new(7000, 7000))
                { Name = $"Turret_{name}" });
            TurretRules.Initialize(shrine, 1, GameData.Default);
            TestAssert.Equal(0.0, shrine.Armor);
            TestAssert.Equal(0.0, shrine.SpellBlock);
            TestAssert.Near(0.4, shrine.AttackPeriod, 1e-12);
            TestAssert.Near(windup, shrine.AttackWindup, 1e-12);
            TestAssert.Near(castFraction, shrine.AttackTimingBaseline!.Value.CastFraction, 1e-12);
            var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, MinionDefinitions.Opposing(team),
                new(7500, 7000)) { Health = 100000, MaxHealth = 100000, Radius = 65 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
            TestAssert.True(combat.QueueBasicAttack(shrine, target, new AttackRolls(1, 1, 1)));
            TestAssert.Near(windup, combat.PendingHits.Single().WindupRemaining, 1e-12, "the laser winds up for 0.16 s");
            TestAssert.Near(0.4, shrine.AttackCooldown, 1e-12, "and fires again after 0.4 s");
        }
    }

    [Check("combat.fountain-heals-allied-minion-units")]
    public static void FountainHealsEverySameTeamMinionActorInItsRadius()
    {
        // Map1 Constants.var sp_RegenRadius heals "units"; the spawn point's update builds its list of same-team
        // base-AI units. Heroes keep their own pass; minion actors join it.
        var fountain = new Position2(25.902751922607422, 263.5519104003906); // exact __Spawn_T1.SCB point (owner decision, 18 October 2026)
        var world = new WorldState();
        Entity Unit(uint id, EntityKind kind, Team team, Position2 position) => world.Register(new Entity(id, kind,
            UnitType.Minion, team, position) { Health = 100, MaxHealth = 1000, Mana = 10, MaxMana = 100 });
        var pet = Unit(1, EntityKind.Pet, Team.Order, fountain + new Position2(500, 0));
        var minion = Unit(2, EntityKind.Minion, Team.Order, fountain + new Position2(1100, 0));
        var outside = Unit(3, EntityKind.Minion, Team.Order, fountain + new Position2(1101, 0));
        var enemy = Unit(4, EntityKind.Minion, Team.Chaos, fountain + new Position2(500, 0));
        var dead = Unit(5, EntityKind.Minion, Team.Order, fountain + new Position2(400, 0));
        dead.Alive = false;
        dead.Health = 0;
        var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
        combat.CombatTick(0.5);
        TestAssert.Equal(100.0, pet.Health, "no tick before a whole second");
        combat.CombatTick(0.5);
        TestAssert.Near(185.0, pet.Health, 1e-9, "8.5 % of max HP per one-second tick");
        TestAssert.Near(18.5, pet.Mana, 1e-9, "and of max mana");
        TestAssert.Near(185.0, minion.Health, 1e-9, "the radius boundary is inclusive");
        TestAssert.Equal(100.0, outside.Health, "outside 1100");
        TestAssert.Equal(100.0, enemy.Health, "the other team's fountain is far away");
        TestAssert.Equal(0.0, dead.Health, "dead units are not healed");
    }

    [Check("combat.protection-transition-notifications")]
    public static void ProtectionTransitionNotifications()
    {
        var world = new WorldState();
        var turret = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, Team.Order, new(0, 0))
        { Name = "Turret_T1_C_01" });
        var inhibitor = world.Register(new Entity(2, EntityKind.Inhibitor, UnitType.Structure, Team.Order, new(0, 0))
        { Name = "Barracks_T1_C1" });
        var changes = new List<(uint NetId, bool Protected)>();
        void Changed(Entity entity) => changes.Add((entity.NetId, entity.Protected));
        ProtectionRules.Recompute(world, true, Changed);
        TestAssert.Equal(new[] { (turret.NetId, true) }, changes.ToArray());
        changes.Clear();
        ProtectionRules.Recompute(world, true, Changed);
        ProtectionRules.Recompute(world, true, Changed);
        TestAssert.Equal(0, changes.Count, "unchanged protection emits no packet intents");
        inhibitor.Alive = false;
        ProtectionRules.Recompute(world, true, Changed);
        TestAssert.Equal(new[] { (turret.NetId, false) }, changes.ToArray());
        changes.Clear();
        inhibitor.Alive = true;
        ProtectionRules.Recompute(world, true, Changed);
        // Map1/LevelScript.luaobj ApplyBarracksRespawnReductions (proto 0.12, run by the respawn handler proto 0.13) re-locks
        // the standing HQ towers once every dampener of the team is back (batch 764; the 16 October latch had expected no change here).
        TestAssert.Equal(new[] { (turret.NetId, true) }, changes.ToArray(), "the last dampener back re-locks the HQ tower");
    }


    [Check("combat.lifecycle")]
    public static void CombatLifecycle()
    {
        var clock = new GameClock();
        clock.SetVirtual(0.0);
        var world = new WorldState(clock);
        var hooks = new TestHooks();
        var transport = new TestTransport();
        var stats = new HeroStatsService(new GameData(), new ItemCatalog(),
            new LifecycleStatsHooks(), new LifecycleStatsTransport());
        var service = new CombatService(world, hooks, transport, new FixedRandomSource(0.5), stats);
        var attacker = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            AttackDamage = 20.0, AttackRange = 100.0, AttackPeriod = 1.0, MissileSpeed = 0.0,
            AttackWindup = 0.0, MaxHealth = 100.0, Health = 100.0,
        };
        var target = new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new Position2(50.0, 0.0))
        {
            MaxHealth = 15.0, Health = 15.0, Armor = 0.0,
        };
        world.Register(attacker);
        world.Register(target);
        TestAssert.True(service.QueueBasicAttack(attacker, target,
            new AttackRolls(0.99, 0.99, 0.99)));
        service.CombatTick(0.01);
        TestAssert.False(target.Alive, "lethal pending hit must use the shared kill path");
        TestAssert.Equal(1, hooks.Deaths);

        const double respawnSeconds = 10.0; // arbitrary scheduled delay; the retail timer is ProgressionRules.DeathTime
        attacker.RespawnAt = service.CurrentTime + respawnSeconds;
        attacker.Alive = false;
        service.CombatTick(respawnSeconds);
        service.ProcessRespawns();
        TestAssert.True(attacker.Alive, "hero respawn must restore life state");
        TestAssert.Near(attacker.MaxHealth, attacker.Health, 1e-9);
        TestAssert.Equal(1, transport.Respawns);
    }

    [Check("combat.lifecycle.damage-recency-stamp")]
    public static void LastDamageAtTracksOnlyPositivePostShieldDamage()
    {
        var world = new WorldState();
        var service = new CombatService(world, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        var source = world.Register(new Entity(10, EntityKind.Hero, UnitType.Hero,
            Team.Order, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 });
        var target = world.Register(new Entity(11, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 });
        target.Buffs.Add(new BuffState("RecencyShield", new BuffPayload { DamageShield = 10.0 }));

        TestAssert.True(service.ApplyHit(source.NetId, target.NetId, 5.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.True(double.IsNegativeInfinity(target.LastDamageAt),
            "fully absorbed damage does not stamp the target");
        TestAssert.True(service.ApplyHit(source.NetId, target.NetId, 20.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(service.CurrentTime, target.LastDamageAt, 1e-12,
            "positive post-shield damage stamps the combat clock");
    }

    [Check("combat.damage.magic-penetration-floor")]
    public static void MagicPenetrationFloorsPositiveResistanceButPreservesNegativeResistance()
    {
        var world = new WorldState();
        var service = new CombatService(world, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        var source = world.Register(new Entity(12, EntityKind.Hero, UnitType.Hero,
            Team.Order, new(0, 0))
        {
            Health = 1000.0, MaxHealth = 1000.0,
            MagicPenetrationPercent = 0.5, MagicPenetrationFlat = 100.0,
        });
        var positive = world.Register(new Entity(13, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new(0, 0))
        { Health = 1000.0, MaxHealth = 1000.0, SpellBlock = 100.0 });
        var negative = world.Register(new Entity(14, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new(0, 0))
        { Health = 1000.0, MaxHealth = 1000.0, SpellBlock = -50.0 });

        TestAssert.True(service.ApplyHit(source.NetId, positive.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(900.0, positive.Health, 1e-9,
            "valid penetration cannot manufacture a negative positive-resistance result");
        TestAssert.True(service.ApplyHit(source.NetId, negative.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(866.6666666666666, negative.Health, 1e-9,
            "already-negative spell block keeps its amplification under penetration");

        // Patch 126 predates the Season 3 pre-season reorder: flat first, then
        // percent, as for armor. Void Staff's 40% plus 20 flat on 100 MR leaves
        // (100 - 20) * 0.6 = 48 effective MR; percent-first would leave 40.
        var voidStaff = world.Register(new Entity(15, EntityKind.Hero, UnitType.Hero,
            Team.Order, new(0, 0))
        {
            Health = 1000.0, MaxHealth = 1000.0,
            MagicPenetrationPercent = 0.4, MagicPenetrationFlat = 20.0,
        });
        var hundredMr = world.Register(new Entity(16, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new(0, 0))
        { Health = 1000.0, MaxHealth = 1000.0, SpellBlock = 100.0 });
        TestAssert.True(service.ApplyHit(voidStaff.NetId, hundredMr.NetId, 148.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(900.0, hundredMr.Health, 1e-9,
            "magic penetration applies flat before percent (48 effective MR)");
        TestAssert.Equal(100.0, hundredMr.SpellBlock, "penetration does not alter the target's real MR");
    }

    [Check("combat.lifecycle.game-over-respawn-boundary")]
    public static void GameOverCeremonyDoesNotRespawnAnAlreadyDeadHero()
    {
        var world = new WorldState();
        var transport = new TestTransport();
        var service = new CombatService(world, new TestHooks(), transport,
            new FixedRandomSource(0.5));
        var killer = Hero(20, Team.Order, 100.0);
        var deadHero = Hero(21, Team.Chaos, 100.0);
        deadHero.Alive = false;
        deadHero.Health = 0.0;
        deadHero.RespawnAt = 1.0;
        var nexus = new Entity(22, EntityKind.Nexus, UnitType.Structure,
            Team.Chaos, new(0, 0)) { Health = 1.0, MaxHealth = 1.0 };
        foreach (var unit in new Entity[] { killer, deadHero, nexus }) world.Register(unit);

        TestAssert.True(service.Kill(nexus, killer.NetId),
            "the lethal Nexus transition enters the end-game ceremony");
        TestAssert.True(service.GameOver, "the match is terminal before ceremony packets");
        service.CombatTick(8.5);
        TestAssert.Equal(0, service.ProcessRespawns(),
            "the post-game respawn pass is suppressed");
        TestAssert.False(deadHero.Alive,
            "a timer expiring during the end-game ceremony cannot re-enter the match");
        TestAssert.Equal(0, transport.Respawns,
            "no respawn packet is emitted after GameOver");
    }

    [Check("combat.passive-gold-schedule")]
    public static void PassiveGoldUsesMap1DelayAndRate()
    {
        // Batch 161 (retail footage — the Xerath champion spotlight, October 2011 — over the Constants.var tick
        // comment): ambient income is continuous at ai_AmbientGoldAmount / ai_AmbientGoldInterval (6.5 / 5 s =
        // 1.3 g/s) from ai_AmbientGoldDelay, so the HUD rises one gold at a time rather than 6-7 every five seconds.
        // DEV-05, 27 Sept 2026 base-game sweep: the coarse step must earn exactly what the fine steps do.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var alive in new[] { true, false })
        foreach (var coarse in new[] { false, true })
        {
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, team, new(7000, 7000))
                { Alive = alive, Health = alive ? 100 : 0, MaxHealth = 100, Gold = 475 });
            var combat = new CombatService(world, new TestHooks(), new TestTransport(),
                new FixedRandomSource(0.5));
            if (coarse)
                combat.CombatTick(110.0, regenerate: false);
            else
            {
                // Seconds of income accrued so far: a step ending exactly on the gate earns nothing.
                foreach (var (dt, seconds) in new[]
                {
                    (89.999, 0.0), (0.001, 0.0), (0.0, 0.0),
                    (4.999, 4.999), (0.001, 5.0), (15.0, 20.0),
                })
                {
                    combat.CombatTick(dt, regenerate: false);
                    TestAssert.Near(475.0 + 1.3 * seconds, hero.Gold, 1e-8,
                        $"{team} alive={alive} ambient gold at {combat.CurrentTime}");
                }
            }
            TestAssert.Near(501.0, hero.Gold, 1e-8, "twenty seconds of continuous income through 110 seconds");
            TestAssert.Equal(alive, hero.Alive, "income does not change life state");
        }
    }

    [Check("combat.passive-gold-item-independence")]
    public static void ItemIncomeIsSeparateFromAmbientTicks()
    {
        var world = new WorldState();
        var slots = Inventory.Empty().ToArray();
        slots[0] = new InventorySlot(3096);
        slots[1] = new InventorySlot(3096);
        slots[2] = new InventorySlot(3093);
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
            { Gold = 475, Inventory = slots, Alive = false, Health = 0 });
        var combat = new CombatService(world, new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
        // Batch 149 step 5: a gold item's income is the window buff its copy opens, not the inventory row (no
        // shipped item record carries FlatGoldPer10Mod). Items/3096.luaobj's OnBuffActivate adds
        // PhilosophersStone and Items/3093.luaobj's adds AvariceBlade; each Spells/<window>.luaobj
        // BuffOnUpdateStats is one BBIncStat IncFlatGoldPer10Mod of Delta 5. Two copies of one item share its
        // window (the second copy's OnBuffActivate finds it and skips the BBSpellBuffAdd BUFF_REPLACE_EXISTING),
        // so this inventory is two windows and 10 gold-per-10 — the distinct-item count CombatService used to
        // take itself. There is no script engine here, so the check drives those two rows directly.
        var windows = new[] { "PhilosophersStone", "AvariceBlade" };
        void SetWindows(bool open)
        {
            foreach (var held in hero.Buffs.Where(buff => windows.Contains(buff.Name)).ToArray())
                hero.Buffs.Remove(held);
            if (!open) return;
            foreach (var name in windows)
                hero.Buffs.Add(new BuffState(name, new BuffPayload { GoldPer10 = 5 }));
        }
        SetWindows(true);
        combat.CombatTick(10.0, regenerate: false);
        // Playtest B3 #12 (26 Sept 2026): item gold-per-10 now shares the
        // ambient 90-second start gate; it previously paid from time zero.
        TestAssert.Near(475.0, hero.Gold, 1e-8,
            "distinct-item income waits for the 90-second gate, including while dead");
        combat.CombatTick(79.999, regenerate: false);
        TestAssert.Near(475.0, hero.Gold, 1e-8, "no item or ambient income before 90 seconds");
        combat.CombatTick(0.001, regenerate: false);
        // Batch 161 (retail footage — the Xerath champion spotlight, October 2011): ambient income is continuous
        // at 1.3 g/s from the gate, so the boundary itself grants nothing and only the seconds after it accrue,
        // independent of item ownership.
        TestAssert.Near(475.0, hero.Gold, 1e-8, "the gate instant grants nothing; income accrues after it");
        combat.CombatTick(4.0, regenerate: false);
        TestAssert.Near(475.0 + (1.3 + 1.0) * 4.0, hero.Gold, 1e-8,
            "item income and ambient income accrue independently");
        // What ends the income is the window's own expiry: Items/3096.luaobj's add is Duration 10 and only its
        // 9-second UpdateSelfBuffActions renewal keeps it up, so a sale stops the renewal and the window pays on
        // for the rest of its ten seconds. This service has no buff clock, so the expiry is the removal here.
        SetWindows(false);
        combat.CombatTick(1.0, regenerate: false);
        TestAssert.Near(475.0 + (1.3 + 1.0) * 4.0 + 1.3, hero.Gold, 1e-8,
            "ambient income continues at the shipped rate after the window closes");
    }

    [Check("combat.economy-fountain")]
    public static void EconomyAndFountainRegression()
    {
        var world = new WorldState();
        var hooks = new TestHooks();
        var transport = new TestTransport();
        var stats = new HeroStatsService(GameData.Default,
            new ItemCatalog(GameData.Default.Paths), new LifecycleStatsHooks(),
            new LifecycleStatsTransport());
        var service = new CombatService(world, hooks, transport,
            new FixedRandomSource(0.5), stats);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(26.0, 264.0))
        {
            ChampionName = "Annie", Alive = false, Gold = 100.0, Experience = 12.0,
            MaxHealth = 1000.0, Health = 1.0, MaxMana = 800.0, Mana = 1.0,
        };
        world.Register(hero);
        world.LocalHeroNetId = hero.NetId;

        service.CombatTick(2.0);
        TestAssert.Near(100.0, hero.Gold, 1e-9,
            "ambient gold has not started at two seconds");
        TestAssert.Near(12.0, hero.Experience, 1e-9, "dead heroes receive no passive experience");

        var turret = new Entity(2, EntityKind.Turret, UnitType.Turret, Team.Chaos,
            new(100.0, 100.0)) { Health = 1.0, MaxHealth = 1.0 };
        world.Register(turret);
        service.Kill(turret);
        TestAssert.Near(100.0 + CombatService.TurretGold,
            hero.Gold, 1e-9, "dead hero receives shared turret gold");
        TestAssert.Near(12.0, hero.Experience, 1e-9, "shared turret gold does not award experience");

        var goldBeforeRespawn = hero.Gold;
        hero.RespawnAt = service.CurrentTime;
        service.ProcessRespawns();
        TestAssert.True(hero.Alive, "respawn restores the dead hero");
        TestAssert.Near(goldBeforeRespawn, hero.Gold, 1e-9, "respawn preserves the wallet");
        TestAssert.Near(12.0, hero.Experience, 1e-9, "respawn preserves experience");

        hero.Position = new(26.0 + CombatService.FountainHealRadius + 1.0, 264.0);
        hero.MaxHealth = 1000.0;
        hero.Health = 100.0;
        hero.MaxMana = 800.0;
        hero.Mana = 200.0;
        hero.HealthRegen = 2.0;
        hero.ManaRegen = 3.0;
        service.CombatTick(1.0);
        TestAssert.Near(102.0, hero.Health, 1e-9, "outside fountain uses ordinary health regen");
        TestAssert.Near(203.0, hero.Mana, 1e-9, "outside fountain uses ordinary mana regen");

        hero.Position = new(26.0, 264.0);
        hero.Health = 700.0;
        hero.Mana = 500.0;
        hero.HealthRegen = 0.0;
        hero.ManaRegen = 0.0;
        // Retail sweep B4, 27 Sept 2026: replace continuous 15%/s with discrete 8.5% ticks.
        service.CombatTick(0.5);
        TestAssert.Near(700.0, hero.Health, 1e-9, "no fountain heal between global ticks");
        TestAssert.Near(500.0, hero.Mana, 1e-9);
        service.CombatTick(0.5);
        TestAssert.Near(785.0, hero.Health, 1e-9, "allied fountain heals 8.5 percent per tick");
        TestAssert.Near(568.0, hero.Mana, 1e-9, "allied fountain restores 8.5 percent mana per tick");
        service.CombatTick(0.75);
        TestAssert.Near(785.0, hero.Health, 1e-9);
        TestAssert.Near(568.0, hero.Mana, 1e-9);
        service.CombatTick(0.25);
        TestAssert.Near(870.0, hero.Health, 1e-9, "successive whole-second tick");
        TestAssert.Near(636.0, hero.Mana, 1e-9);
        hero.Buffs.Add(new BuffState("HealingReduction", new BuffPayload { HealingReceivedMult = -0.5 }));
        service.CombatTick(1.0);
        TestAssert.Near(912.5, hero.Health, 1e-9, "fountain retains the received-healing stage");
        TestAssert.Near(704.0, hero.Mana, 1e-9, "healing reduction does not reduce mana restoration");
        hero.Buffs.Clear();
        hero.Health = 999.0;
        hero.Mana = 799.0;
        service.CombatTick(1.0);
        TestAssert.Near(hero.MaxHealth, hero.Health, 1e-9, "fountain health healing is capped");
        TestAssert.Near(hero.MaxMana, hero.Mana, 1e-9, "fountain mana healing is capped");
        hero.Health = 100.0;
        hero.Mana = 200.0;
        service.CombatTick(2.0);
        TestAssert.Near(270.0, hero.Health, 1e-9, "a coarse step processes both fountain ticks");
        TestAssert.Near(336.0, hero.Mana, 1e-9);

        hero.HealthRegen = 2.0;
        hero.ManaRegen = 3.0;
        hero.Team = Team.Chaos;
        hero.Position = new(26.0, 264.0);
        hero.Health = 100.0;
        hero.Mana = 200.0;
        service.CombatTick(1.0);
        TestAssert.Near(102.0, hero.Health, 1e-9, "enemy fountain does not heal health");
        TestAssert.Near(203.0, hero.Mana, 1e-9, "enemy fountain does not restore mana");

        hero.Alive = false;
        hero.Health = 1.0;
        hero.Mana = 1.0;
        service.CombatTick(1.0);
        TestAssert.Near(1.0, hero.Health, 1e-9, "corpse is not healed");
        TestAssert.Near(1.0, hero.Mana, 1e-9, "corpse is not given mana");
    }

    [Check("combat.owner-cascade")]
    public static void OwnerDeathCascadesAllOwnedKindsExceptBoxes()
    {
        var world = new WorldState();
        var hooks = new TestHooks();
        var transport = new TestTransport();
        var stats = new HeroStatsService(GameData.Default,
            new ItemCatalog(GameData.Default.Paths), new LifecycleStatsHooks(),
            new LifecycleStatsTransport());
        var service = new CombatService(world, hooks, transport,
            new FixedRandomSource(0.5), stats);
        var owner = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0))
        {
            ChampionName = "Annie", MaxHealth = 100.0, Health = 100.0,
        };
        var enemy = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(100, 0))
        {
            ChampionName = "Annie", MaxHealth = 100.0, Health = 100.0,
        };
        var clone = new Entity(3, EntityKind.ShacoClone, UnitType.Monster, Team.Order, new(0, 0))
        {
            OwnerNetId = owner.NetId, MaxHealth = 20.0, Health = 20.0,
            PetState = new PetState(CloneKind.ShacoClone, owner.NetId),
        };
        var ghoul = new Entity(4, EntityKind.YorickGhoul, UnitType.Monster, Team.Order, new(0, 0))
        {
            OwnerNetId = owner.NetId, MaxHealth = 20.0, Health = 20.0,
            PetState = new PetState(CloneKind.YorickGhoul, owner.NetId),
        };
        var box = new Entity(5, EntityKind.ShacoBox, UnitType.Monster, Team.Order, new(0, 0))
        {
            OwnerNetId = owner.NetId, MaxHealth = 20.0, Health = 20.0,
        };
        world.Register(owner);
        world.Register(enemy);
        world.Register(clone);
        world.Register(ghoul);
        world.Register(box);

        service.Kill(owner, enemy.NetId);

        TestAssert.False(owner.Alive, "owner must die through the normal kill path");
        // Owner decision, 18 October 2026: the Shaco clone outlives its owner. Retail (owner research 18 Oct 2026): Yorick's ghouls die with him.
        TestAssert.True(clone.Alive, "owned Shaco clone is not part of the owner death cascade");
        TestAssert.False(ghoul.Alive, "owned Yorick ghoul must be cascaded");
        TestAssert.True(box.Alive, "Shaco boxes are excluded from owner death cascade");
    }

    [Check("combat.interrupt-and-progress")]
    public static void CombatInterruptAndProgress()
    {
        var world = new WorldState();
        var hooks = new TestHooks();
        var transport = new TestTransport();
        var stats = new HeroStatsService(GameData.Default,
            new ItemCatalog(GameData.Default.Paths), new LifecycleStatsHooks(),
            new LifecycleStatsTransport());
        var service = new CombatService(world, hooks, transport,
            new FixedRandomSource(0.5), stats);
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(0, 0))
        {
            ChampionName = "Annie", AttackDamage = 20.0, AttackRange = 100.0,
            AttackPeriod = 1.0, AttackWindup = 0.5, MissileSpeed = 0.0,
            MaxHealth = 100.0, Health = 100.0,
        };
        var target = new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(50, 0))
        {
            MaxHealth = 100.0, Health = 100.0,
        };
        var unsupported = new Entity(3, EntityKind.Monster, UnitType.Monster, Team.Chaos, new(500, 0))
        {
            AttackCooldown = 1.0,
        };
        world.Register(hero);
        world.Register(target);
        world.Register(unsupported);

        hero.AttackCooldown = 0.0;
        TestAssert.True(service.QueueBasicAttack(hero, target,
            new AttackRolls(0.99, 0.99, 0.99)));
        TestAssert.Equal(1, service.InterruptBasicAttack(hero), "positive wind-up is interruptible");
        TestAssert.Equal(1, hooks.Canceled);
        TestAssert.Equal(0, service.PendingHits.Count);

        hero.AttackCooldown = 0.0;
        TestAssert.True(service.QueueBasicAttack(hero, target,
            new AttackRolls(0.99, 0.99, 0.99)));
        service.CombatTick(0.5);
        TestAssert.Equal(0, service.InterruptBasicAttack(hero), "launched shot survives interruption");
        // Batch 741: the melee hit (MissileSpeed 0) landed at its cast frame, the end of this 0.5 s windup; it waited for the next tick before.
        TestAssert.Equal(0, service.PendingHits.Count);
        TestAssert.Near(80.0, target.Health, 1e-9, "the melee hit landed at its cast frame");
        TestAssert.Near(0.5, unsupported.AttackCooldown, 1e-9,
            "cooldown decays before non-basic unit eligibility");

        TestAssert.True(service.AwardHeroProgress(hero, experience: 700.0));
        TestAssert.Equal(3, hero.Level, "multiple level-ups in one award");
        TestAssert.Equal(2, transport.LevelUps);
        TestAssert.Equal(1, transport.GoldExpPackets);
    }

    // Shared by the basic-windup cancellation checks below.
    private static (CombatService Service, TestHooks Hooks, Entity Source, Entity Target) Setup(
        EntityKind sourceKind, UnitType sourceType, bool ranged = false)
    {
        var world = new WorldState();
        var hooks = new TestHooks();
        var sourceTeam = sourceKind == EntityKind.Neutral ? Team.Neutral : Team.Order;
        var targetTeam = sourceTeam == Team.Neutral ? Team.Order : Team.Chaos;
        var source = world.Register(new Entity(1, sourceKind, sourceType,
            sourceTeam, new(7000, 7000))
        {
            AttackDamage = 100.0,
            AttackRange = ranged ? 550.0 : 100.0,
            AttackPeriod = 1.0,
            AttackWindup = 0.5,
            MissileSpeed = ranged ? 500.0 : 0.0,
            Health = 100.0,
            MaxHealth = 100.0,
        });
        var target = world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero,
            targetTeam, new(7050, 7000))
        {
            Health = 1000.0,
            MaxHealth = 1000.0,
        });
        return (new CombatService(world, hooks, new TestTransport(), new FixedRandomSource(0.5)),
            hooks, source, target);
    }

    [Check("combat.death-cancels-basic-windup")]
    public static void DeathCancelsOnlyUnlaunchedBasicWindups()
    {
        foreach (var ranged in new[] { false, true })
        {
            var test = Setup(EntityKind.Hero, UnitType.Hero, ranged);
            TestAssert.True(test.Service.QueueBasicAttack(test.Source, test.Target,
                new AttackRolls(1.0, 1.0, 1.0)), $"{(ranged ? "ranged" : "melee")} attack queues");
            TestAssert.True(test.Service.PendingHits.Single().MissileNetId != 0,
                "a queued basic has a missile identity even before launch");
            TestAssert.True(test.Service.Kill(test.Source),
                $"{(ranged ? "ranged" : "melee")} source dies");
            TestAssert.Equal(0, test.Service.PendingHits.Count,
                "ordinary death cancels the unfinished basic");
            TestAssert.Equal(1, test.Hooks.Canceled,
                "death reports the canceled basic exactly once");
            test.Service.CombatTick(1.0, regenerate: false);
            TestAssert.Near(1000.0, test.Target.Health, 1e-9,
                "an unfinished basic cannot impact after source death");
        }

        foreach (var (kind, type) in new[] {
                     (EntityKind.Minion, UnitType.Minion),
                     (EntityKind.Pet, UnitType.Monster),
                     (EntityKind.Neutral, UnitType.Monster),
                 })
        {
            var test = Setup(kind, type);
            TestAssert.True(test.Service.QueueBasicAttack(test.Source, test.Target,
                new AttackRolls(1.0, 1.0, 1.0)), $"{kind} attack queues");
            TestAssert.True(test.Service.Kill(test.Source), $"{kind} source dies");
            TestAssert.Equal(0, test.Service.PendingHits.Count,
                $"{kind} death cancels its unfinished basic");
            TestAssert.Equal(1, test.Hooks.Canceled, $"{kind} reports cancellation");
        }

        var launched = Setup(EntityKind.Hero, UnitType.Hero, ranged: true);
        TestAssert.True(launched.Service.QueueBasicAttack(launched.Source, launched.Target,
            new AttackRolls(1.0, 1.0, 1.0)));
        launched.Service.CombatTick(0.51, regenerate: false);
        var launchedHit = launched.Service.PendingHits.Single();
        TestAssert.True(launchedHit.WindupRemaining <= 0.0 && launchedHit.MissileNetId != 0,
            "the control crosses into launched missile flight");
        TestAssert.True(launched.Service.Kill(launched.Source));
        TestAssert.Equal(1, launched.Service.PendingHits.Count,
            "source death preserves an already-launched shot");
        TestAssert.Equal(0, launched.Hooks.Canceled,
            "launched shot is not reported as canceled");
        launched.Service.CombatTick(0.2, regenerate: false);
        TestAssert.Near(900.0, launched.Target.Health, 1e-9,
            "launched shot still impacts after source death");

        var scripted = Setup(EntityKind.Hero, UnitType.Hero);
        CombatService? scriptedService = null;
        scripted.Hooks.DeathCallback = dead => scriptedService!.QueueScriptedHit(dead,
            scripted.Target, 25.0, DamageType.True, DamageSourceKind.Spell, 0.5);
        scriptedService = scripted.Service;
        TestAssert.True(scripted.Service.QueueBasicAttack(scripted.Source, scripted.Target,
            new AttackRolls(1.0, 1.0, 1.0)));
        scripted.Service.QueueScriptedHit(scripted.Source, scripted.Target, 40.0,
            DamageType.True, DamageSourceKind.Basic, 0.5);
        scripted.Service.QueueScriptedHit(scripted.Source, scripted.Target, 50.0,
            DamageType.True, DamageSourceKind.Spell, 0.5);
        TestAssert.True(scripted.Service.Kill(scripted.Source));
        TestAssert.Equal(3, scripted.Service.PendingHits.Count,
            "death removes the queued basic but keeps delayed scripted hits");
        TestAssert.Equal(1, scripted.Hooks.Canceled,
            "only the real queued basic reports cancellation");
        TestAssert.True(scripted.Service.PendingHits.All(hit => hit.MissileNetId == 0),
            "the remaining hits are scripted delayed effects");
        scripted.Service.CombatTick(0.6, regenerate: false);
        TestAssert.Near(1000.0, scripted.Target.Health, 1e-9,
            "scripted hits do not resolve during their windup");
        scripted.Service.CombatTick(0.01, regenerate: false);
        TestAssert.Near(885.0, scripted.Target.Health, 1e-9,
            "scripted delayed hits and the death callback retain their lifetimes");
    }

    // Playtest #24, 25 Sept 2026: Map1 ar_StopAttackRangeModifier (+100)
    // leashes every mobile attacker's windup, ranged included.
    [Check("combat.committed-range-leashes-ranged-npc-windups")]
    public static void CommittedRangeLeashesRangedNpcWindups()
    {
        foreach (var (kind, type) in new[] {
                     (EntityKind.Pet, UnitType.Monster),
                     (EntityKind.ShacoClone, UnitType.Monster),
                     (EntityKind.YorickRevenant, UnitType.Monster),
                     (EntityKind.Minion, UnitType.Minion),
                     (EntityKind.Neutral, UnitType.Monster),
                 })
        foreach (var escape in new[] { false, true })
        {
            var test = Setup(kind, type, ranged: true);
            TestAssert.True(test.Service.QueueBasicAttack(test.Source, test.Target,
                new AttackRolls(1.0, 1.0, 1.0)), $"{kind} ranged attack queues");
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            var reach = CombatService.AttackReach(test.Source, test.Target);
            test.Target.Position = new(7000 + reach + (escape ? 110 : 90), 7000);
            test.Service.CombatTick(0.1, regenerate: false);
            TestAssert.Equal(escape ? 0 : 1, test.Service.PendingHits.Count,
                $"{kind} windup {(escape ? "is canceled beyond" : "survives within")} the +100 leash");
            TestAssert.Equal(escape ? 1 : 0, test.Hooks.Canceled, $"{kind} cancellation count");
            for (var tick = 0; tick < 30; tick++) test.Service.CombatTick(0.1, regenerate: false);
            if (escape)
                TestAssert.Near(1000.0, test.Target.Health, 1e-9, $"{kind} canceled windup never lands");
            else
                TestAssert.True(test.Target.Health < 1000.0, $"{kind} committed ranged windup lands");
        }

        // Turrets never leash, and ranged heroes keep theirs in
        // HeroAttackOrderSystem.Chase, so the combat tick leaves both alone.
        foreach (var (kind, type) in new[] {
                     (EntityKind.Turret, UnitType.Turret),
                     (EntityKind.Hero, UnitType.Hero),
                 })
        {
            var test = Setup(kind, type, ranged: true);
            TestAssert.True(test.Service.QueueBasicAttack(test.Source, test.Target,
                new AttackRolls(1.0, 1.0, 1.0)), $"{kind} ranged attack queues");
            test.Target.Position = new(7660, 7000);
            test.Service.CombatTick(0.1, regenerate: false);
            TestAssert.Equal(0, test.Hooks.Canceled, $"{kind} windup is not range-canceled by combat");
            for (var tick = 0; tick < 30; tick++) test.Service.CombatTick(0.1, regenerate: false);
            TestAssert.True(test.Target.Health < 1000.0, $"{kind} ranged attack still lands");
        }
    }


    /// <summary>
    /// Batch 173: the shipped character scripts a bare <see cref="CombatService"/> world's units run in production (batch
    /// 159 part 2), over this check's own world and combat service. The tick corpora had none, so a camp monster's own
    /// <c>CharOnHitUnit</c> (its x1.43) never ran there and their recordings held a hit no script produces. The bind runs
    /// over the fixture's restored state; <see cref="Poll"/> is the units' UpdateSelfBuffStats/Actions sample.
    /// </summary>
    private sealed class TickScripts
    {
        private readonly WorldState _world;
        private readonly BuffLifecycle _lifecycle;
        private readonly BbUnitCharacterModule _units;

        public TickScripts(WorldState world, CombatService combat, TestHooks hooks)
        {
            _world = world;
            var queue = new CapturingCombatPacketQueue();
            var sync = new ReplicationSyncCounter();
            var data = GameData.Default;
            var grid = NavigationGrid.LoadDefault();
            var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
            var effects = new ProtocolSpellEffectSink(queue, sync, bounds,
                (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate, _ => { });
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths),
                new LifecycleStatsHooks(), new LifecycleStatsTransport());
            _lifecycle = new BuffLifecycle(world, stats, new TickBuffServices(effects, world, queue));
            var movement = new MovementSystem(world, grid);
            var (units, sink) = MatchScriptSeams.InstallUnitCharacters(
                UnitCharacterRig.SpellHost(world, movement, new CombatSpellAdapter(combat),
                    effects, new BuffLifecycleAdapter(_lifecycle)));
            _units = units;
            hooks.AttachUnitScripts(sink);
        }

        public void Bind(Entity unit) => MatchScriptSeams.BindUnitCharacter(_units, unit, unit.Name);

        /// <summary>
        /// The script update: only the bound units' UpdateSelfBuffStats/Actions sample. Their buffs are ticked too, but no
        /// other unit's: this rig has no lifecycle of its own, and ticking one over the whole world would advance the
        /// turrets' and the minions' script buffs, which nothing here owns (the turret corpora's own timeline is
        /// <c>TurretScriptTimelineChecks</c>).
        /// </summary>
        public void Poll(double dt)
        {
            _units.Poll();
            foreach (var (unit, _) in _units.Bound.ToArray()) _lifecycle.TickBuffs(unit, dt);
        }
    }

    /// <summary>The buff boundaries the tick rig's character scripts need; the statuses and effects production wires.</summary>
    private sealed class TickBuffServices(ISpellEffectSink effects, WorldState world,
        ICombatPacketQueue queue) : IBuffServices
    {
        public ICombatBuffHooks Combat { get; } = new TickCombatBuffHooks();
        public ICastBuffHooks Casts { get; } = new TickCastHooks();
        public IChampionBuffHooks Champions { get; } = new TickChampionBuffHooks();
        public IBuffEffectHooks Effects { get; } = new BuffEffectService(effects,
            new ProtocolBuffVisionSink(world.Get, world.Allocate, queue), _ => world.Clock.SyncId());
        public IItemBuffHooks Items { get; } = new TickItemBuffHooks();

        private sealed class TickCombatBuffHooks : ICombatBuffHooks
        {
            public bool IsPendingActionBlocked(Entity unit) => unit.Buffs.Any(buff => buff.Payload.ActionDisabled);
            public double HealingMultiplier(Entity unit) => 1.0;
            public void InterruptBasicAttack(Entity unit) { }
            public void ApplyPeriodicDamage(PeriodicDamageRequest request) { }
            public void Kill(Entity unit, uint killerNetId = 0) { }
        }
        private sealed class TickCastHooks : ICastBuffHooks { public void CancelPendingCast(Entity hero) => hero.PendingCast = null; }
        private sealed class TickChampionBuffHooks : IChampionBuffHooks
        {
            public void OnBuffTick(Entity unit, BuffState buff, double dt) { }
            public void OnBuffRemoved(Entity unit, BuffState buff) { }
            public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
        }
        private sealed class TickItemBuffHooks : IItemBuffHooks
        {
            public bool HasItem(Entity unit, int itemId) => false;
            public void SyncItemSlow(Entity target) { }
        }
    }


    /// <summary>
    /// Batch 336: the death corpora run Map1's level script beside combat, wired as ProductionMatchComposition wires it. Since batch 314
    /// the inhibitor's 300 s return is the script's own countdown (HandleDestroyedObject's DisableBarracksSpawn calls SetDisableMinionSpawn,
    /// DISABLE_MINION_SPAWN_BASE_TIME 300), so a bare CombatService arms none and the recorded respawn and 0x2D countdown read 0.
    /// </summary>
    private static LevelScriptHost WireLevelScript(WorldState world, CombatService combat)
    {
        var level = LevelScriptFixtures.Init(world, now: world.Clock.Now());
        combat.BarracksChanged = (barracks, destroyed) =>
        {
            if (destroyed) level.OnBarracksFell(barracks);
            else level.OnBarracksRestored(barracks);
        };
        level.DisableMinionSpawn = combat.ArmInhibitorReturn;
        return level;
    }


    /// <summary>
    /// Batch 336: a barracks minion's death still shares its experience with the enemy heroes near it. Its reward is the level script's spawn
    /// info (the barracks update reads MeleeExpGiven ... and ExperienceRadius), the one source of a unit's death experience; script units pay none.
    /// </summary>
    [Check("combat.lane-minion-death-shares-experience-in-a-match")]
    public static void LaneMinionDeathSharesExperienceInAMatch()
    {
        using var match = new BbM3.Match("Annie");
        Entity? minion = null;
        for (var second = 0; second < 150 && minion is null; second++)
        {
            match.Advance(1.0);
            minion = match.Runtime.World.Entities.Values.FirstOrDefault(unit =>
                unit.Kind == EntityKind.Minion && unit.Alive && unit.Team == match.Hero.Team);
        }
        TestAssert.True(minion is not null, "the first wave spawns");
        TestAssert.True(minion!.ExperienceReward > 0.0, "the wave's minion carries the level script's experience");
        match.Foe.Position = minion.Position + new Position2(100, 0);
        match.FarFoe.Position = new Position2(13000, 13000);
        var experience = match.Foe.Experience;
        TestAssert.True(match.Runtime.Combat.Kill(minion, match.Foe.NetId), "the foe kills the minion");
        TestAssert.Near(experience + minion.ExperienceReward * ProgressionRules.SoloLaneUnitExperience, match.Foe.Experience, 1e-6,
            "the lone enemy hero beside it gets the solo share");
    }

    [Check("combat.progression.credit-production")]
    public static void ProgressionCreditProduction()
    {
        var world = new WorldState();
        var hooks = new TestHooks();
        var transport = new TestTransport();
        var service = new CombatService(world, hooks, transport, new FixedRandomSource(0.5));
        var killer = Hero(1, Team.Order, 100.0);
        var support = Hero(2, Team.Order, 100.0);
        var supportTwo = Hero(5, Team.Order, 100.0);
        var victim = Hero(3, Team.Chaos, 10.0);
        support.DeathStreak = 3;
        var pet = new Entity(4, EntityKind.Pet, UnitType.Monster, Team.Order, new(0, 0))
        {
            OwnerNetId = killer.NetId, Health = 100.0, MaxHealth = 100.0,
        };
        foreach (var unit in new[] { killer, support, supportTwo, victim, pet }) world.Register(unit);

        service.ApplyHit(support.NetId, victim.NetId, 1.0, DamageType.True);
        service.ApplyHit(supportTwo.NetId, victim.NetId, 1.0, DamageType.True);
        service.RecordSupportAssist(support, killer, durationSeconds: 10.0);
        service.RecordSupportAssist(supportTwo, killer, durationSeconds: 10.0);
        service.ApplyHit(killer.NetId, victim.NetId, 20.0, DamageType.True);

        TestAssert.False(victim.Alive, "champion lethal hit must enter death path");
        TestAssert.Equal(1, killer.ChampionKills, "killer counter");
        TestAssert.Equal(1, support.ChampionAssists, "damage/support assist is deduplicated");
        TestAssert.Equal(2, support.DeathStreak, "assist reduces death streak by one");
        TestAssert.Equal(2, hooks.ItemAssists, "item assist hook is production-called");
        TestAssert.Near(400.0, killer.Gold, 1e-9, "first blood bounty");
        TestAssert.Near(400.0 * ProgressionRules.GoldPoolForAssist,
            support.Gold + supportTwo.Gold, 1e-9, "assist pool");
        TestAssert.Near(200.0 * ProgressionRules.GoldPoolForAssist,
            support.Gold, 1e-9, "first assist split");
        TestAssert.Near(200.0 * ProgressionRules.GoldPoolForAssist,
            supportTwo.Gold, 1e-9, "second assist split");
        TestAssert.Near(12.0, transport.LastDeathSeconds, 1e-9,
            "death packet and level-one timer");
        TestAssert.Near(12.0, victim.RespawnAt, 1e-9,
            "scheduled respawn uses packet timer");

        var expiredWorld = new WorldState();
        var expiredHooks = new TestHooks();
        var expiredTransport = new TestTransport();
        var expired = new CombatService(expiredWorld, expiredHooks, expiredTransport,
            new FixedRandomSource(0.5));
        var expiredKiller = Hero(10, Team.Order, 100.0);
        var expiredSupport = Hero(11, Team.Order, 100.0);
        var expiredVictim = Hero(12, Team.Chaos, 10.0);
        foreach (var unit in new[] { expiredKiller, expiredSupport, expiredVictim })
            expiredWorld.Register(unit);
        expired.ApplyHit(expiredSupport.NetId, expiredVictim.NetId, 1.0, DamageType.True);
        expired.RecordSupportAssist(expiredSupport, expiredKiller, durationSeconds: 10.0);
        expired.CombatTick(16.0);
        expired.ApplyHit(expiredKiller.NetId, expiredVictim.NetId, 20.0, DamageType.True);
        TestAssert.Equal(0, expiredSupport.ChampionAssists,
            "expired damage and support markers cannot assist");

        var petWorld = new WorldState();
        var petHooks = new TestHooks();
        var petTransport = new TestTransport();
        var petService = new CombatService(petWorld, petHooks, petTransport,
            new FixedRandomSource(0.5));
        var owner = Hero(20, Team.Order, 100.0);
        var petVictim = Hero(21, Team.Chaos, 10.0);
        var ownerPet = new Entity(22, EntityKind.Pet, UnitType.Monster, Team.Order, new(0, 0))
        {
            OwnerNetId = owner.NetId, Health = 20.0, MaxHealth = 20.0,
        };
        foreach (var unit in new[] { owner, petVictim, ownerPet }) petWorld.Register(unit);
        petService.ApplyHit(ownerPet.NetId, petVictim.NetId, 20.0, DamageType.True);
        TestAssert.Equal(1, owner.ChampionKills, "pet lethal hit resolves recursively to owner");

        var executionWorld = new WorldState();
        var executionHooks = new TestHooks();
        var executionTransport = new TestTransport();
        var executionService = new CombatService(executionWorld, executionHooks,
            executionTransport, new FixedRandomSource(0.5));
        var executionAttacker = Hero(30, Team.Order, 100.0);
        var executionVictim = Hero(31, Team.Chaos, 10.0);
        var executionTurret = new Entity(32, EntityKind.Turret, UnitType.Turret,
            Team.Order, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 };
        foreach (var unit in new[] { executionAttacker, executionVictim, executionTurret })
            executionWorld.Register(unit);
        executionService.ApplyHit(executionAttacker.NetId, executionVictim.NetId, 1.0,
            DamageType.True);
        executionService.ApplyHit(executionTurret.NetId, executionVictim.NetId, 20.0,
            DamageType.True);
        TestAssert.Equal(1, executionAttacker.ChampionKills,
            "turret execution uses recent hero credit");

        var markerOnlyWorld = new WorldState();
        var markerOnlyHooks = new TestHooks();
        var markerOnlyTransport = new TestTransport();
        var markerOnlyService = new CombatService(markerOnlyWorld, markerOnlyHooks,
            markerOnlyTransport, new FixedRandomSource(0.5));
        var markerOnlySource = Hero(33, Team.Order, 100.0);
        var markerOnlyVictim = Hero(34, Team.Chaos, 10.0);
        var markerOnlyTurret = new Entity(35, EntityKind.Turret, UnitType.Turret,
            Team.Order, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 };
        foreach (var unit in new[] { markerOnlySource, markerOnlyVictim, markerOnlyTurret })
            markerOnlyWorld.Register(unit);
        markerOnlyService.RecordSupportAssist(markerOnlySource, markerOnlyVictim);
        markerOnlyService.ApplyHit(markerOnlyTurret.NetId, markerOnlyVictim.NetId,
            20.0, DamageType.True);
        TestAssert.Equal(0, markerOnlySource.ChampionKills,
            "marker-only debuff cannot supply turret execution credit");

        var oldHitWorld = new WorldState();
        var oldHitHooks = new TestHooks();
        var oldHitTransport = new TestTransport();
        var oldHitService = new CombatService(oldHitWorld, oldHitHooks,
            oldHitTransport, new FixedRandomSource(0.5));
        var oldHitSource = Hero(36, Team.Order, 100.0);
        var oldHitVictim = Hero(37, Team.Chaos, 10.0);
        var oldHitTurret = new Entity(38, EntityKind.Turret, UnitType.Turret,
            Team.Order, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 };
        foreach (var unit in new[] { oldHitSource, oldHitVictim, oldHitTurret })
            oldHitWorld.Register(unit);
        oldHitService.ApplyHit(oldHitSource.NetId, oldHitVictim.NetId, 1.0,
            DamageType.True);
        oldHitService.CombatTick(16.0);
        // A fresh debuff marker extends assist eligibility, but not the native
        // last-damage timestamp used for an execution fallback.
        oldHitService.RecordSupportAssist(oldHitSource, oldHitVictim);
        oldHitService.ApplyHit(oldHitTurret.NetId, oldHitVictim.NetId, 20.0,
            DamageType.True);
        TestAssert.Equal(0, oldHitSource.ChampionKills,
            "fresh debuff cannot extend stale turret execution damage credit");

        var neutralWorld = new WorldState();
        var neutralHooks = new TestHooks();
        var neutralTransport = new TestTransport();
        var neutralService = new CombatService(neutralWorld, neutralHooks,
            neutralTransport, new FixedRandomSource(0.5));
        var neutralAttacker = Hero(40, Team.Order, 100.0);
        var neutralVictim = Hero(41, Team.Chaos, 10.0);
        var neutral = new Entity(42, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 };
        foreach (var unit in new[] { neutralAttacker, neutralVictim, neutral })
            neutralWorld.Register(unit);
        neutralService.ApplyHit(neutralAttacker.NetId, neutralVictim.NetId, 1.0,
            DamageType.True);
        neutralService.ApplyHit(neutral.NetId, neutralVictim.NetId, 20.0,
            DamageType.True);
        TestAssert.Equal(1, neutralAttacker.ChampionKills,
            "neutral execution uses recent hero credit");

        var posthumousWorld = new WorldState();
        var posthumousHooks = new TestHooks();
        var posthumousTransport = new TestTransport();
        var posthumousService = new CombatService(posthumousWorld, posthumousHooks,
            posthumousTransport, new FixedRandomSource(0.5));
        var posthumousKiller = Hero(60, Team.Order, 100.0);
        var posthumousSupport = Hero(61, Team.Order, 100.0);
        var posthumousVictim = Hero(62, Team.Chaos, 10.0);
        foreach (var unit in new[] { posthumousKiller, posthumousSupport, posthumousVictim })
            posthumousWorld.Register(unit);
        posthumousService.ApplyHit(posthumousSupport.NetId, posthumousVictim.NetId,
            1.0, DamageType.True);
        posthumousService.RecordSupportAssist(posthumousSupport, posthumousKiller);
        posthumousService.Kill(posthumousSupport);
        posthumousService.ApplyHit(posthumousKiller.NetId, posthumousVictim.NetId,
            20.0, DamageType.True);
        TestAssert.Equal(1, posthumousSupport.ChampionAssists,
            "dead support keeps unexpired damage/support assist");
        TestAssert.Equal(1, posthumousHooks.ItemAssists,
            "dead support receives item assist callback");
        TestAssert.Near(400.0 * ProgressionRules.GoldPoolForAssist,
            posthumousSupport.Gold, 1e-9,
            "dead support receives assist gold");
        // Playtest B3 #12 (26 Sept 2026): 154 * 1.10 / 2 (was 140 * 1.261 / 2).
        TestAssert.Near(84.7, posthumousSupport.Experience, 1e-9,
            "dead valid assist receives its earned shared champion XP");
        TestAssert.Near(84.7, posthumousKiller.Experience, 1e-9,
            "the living killer shares champion XP with the dead assistant");

        var indirectWorld = new WorldState();
        var indirectService = new CombatService(indirectWorld, new TestHooks(),
            new TestTransport(), new FixedRandomSource(0.5));
        var indirectKiller = Hero(63, Team.Order, 100.0);
        var indirectContributor = Hero(64, Team.Order, 100.0);
        var indirectSupport = Hero(65, Team.Order, 100.0);
        var indirectVictim = Hero(66, Team.Chaos, 10.0);
        foreach (var unit in new[] { indirectKiller, indirectContributor,
                     indirectSupport, indirectVictim })
            indirectWorld.Register(unit);
        indirectService.RecordSupportAssist(indirectSupport, indirectContributor);
        indirectService.ApplyHit(indirectContributor.NetId, indirectVictim.NetId,
            1.0, DamageType.True);
        indirectService.ApplyHit(indirectKiller.NetId, indirectVictim.NetId,
            20.0, DamageType.True);
        TestAssert.Equal(1, indirectSupport.ChampionAssists,
            "support marker follows a contributing ally to another killer");

        var turretSupportWorld = new WorldState();
        var turretSupportService = new CombatService(turretSupportWorld,
            new TestHooks(), new TestTransport(), new FixedRandomSource(0.5));
        var turretSupport = Hero(67, Team.Order, 100.0);
        var turretKiller = Hero(68, Team.Order, 100.0);
        var turretVictim = Hero(69, Team.Chaos, 150.0);
        var fortifyTurret = new Entity(70, EntityKind.Turret, UnitType.Turret,
            Team.Order, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 };
        foreach (var unit in new Entity[] { turretSupport, turretKiller,
                     turretVictim, fortifyTurret })
            turretSupportWorld.Register(unit);
        turretSupportService.RecordSupportAssist(turretSupport, fortifyTurret);
        turretSupportService.ApplyHit(fortifyTurret.NetId, turretVictim.NetId,
            100.0, DamageType.True);
        turretSupportService.ApplyHit(turretKiller.NetId, turretVictim.NetId,
            60.0, DamageType.True);
        TestAssert.Equal(1, turretSupport.ChampionAssists,
            "turret damage carries Fortify support to the later hero killer");
    }

    [Check("combat.progression.epic-objective-credit")]
    public static void DragonAndWormCreditOneEpicKillToTheHeroOwner()
    {
        var world = new WorldState();
        var service = new CombatService(world, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        var owner = Hero(71, Team.Order, 100.0);
        var pet = new Entity(72, EntityKind.Pet, UnitType.Monster, Team.Order,
            owner.Position) { OwnerNetId = owner.NetId, Health = 100.0, MaxHealth = 100.0 };
        var dragon = new Entity(73, EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            owner.Position) { Name = "Dragon", Health = 10.0, MaxHealth = 10.0 };
        var worm = new Entity(74, EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            owner.Position) { Name = "Worm", Health = 10.0, MaxHealth = 10.0 };
        var uncredited = new Entity(75, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, owner.Position)
        { Name = "Dragon", Health = 10.0, MaxHealth = 10.0 };
        foreach (var unit in new Entity[] { owner, pet, dragon, worm, uncredited })
            world.Register(unit);

        TestAssert.True(service.Kill(dragon, pet.NetId),
            "a pet last hit resolves the epic objective to its hero owner");
        TestAssert.Equal(1, owner.MatchStatistics.SuperMonsterKills,
            "the Dragon increments the owner epic counter once");
        TestAssert.False(service.Kill(dragon, pet.NetId),
            "a dead Dragon cannot increment the counter again");
        TestAssert.True(service.Kill(worm, owner.NetId),
            "the Worm reaches the same production death hook");
        TestAssert.Equal(2, owner.MatchStatistics.SuperMonsterKills,
            "Dragon and Worm each contribute one epic counter");
        TestAssert.True(service.Kill(uncredited),
            "an uncredited neutral death still follows ordinary cleanup");
        TestAssert.Equal(2, owner.MatchStatistics.SuperMonsterKills,
            "an uncredited neutral death does not invent an epic owner");
    }

    [Check("combat.progression.champion-xp-production")]
    public static void ChampionExperienceProduction()
    {
        var soloWorld = new WorldState();
        var soloService = new CombatService(soloWorld, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        var soloKiller = Hero(80, Team.Order, 100.0);
        var soloVictim = Hero(81, Team.Chaos, 10.0);
        foreach (var unit in new[] { soloKiller, soloVictim }) soloWorld.Register(unit);
        soloService.ApplyHit(soloKiller.NetId, soloVictim.NetId, 20.0, DamageType.True);
        // Playtest B3 #12 (26 Sept 2026): V1.0.0.96 55% of the victim's
        // next-level requirement (was 50%, 140).
        TestAssert.Near(154.0, soloKiller.Experience, 1e-9,
            "level-one solo champion XP uses 55% of the next-level requirement");

        var sharedWorld = new WorldState();
        var sharedService = new CombatService(sharedWorld, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        var sharedKiller = Hero(82, Team.Order, 100.0);
        var sharedNearby = Hero(83, Team.Order, 100.0);
        var sharedVictim = Hero(84, Team.Chaos, 10.0);
        foreach (var unit in new[] { sharedKiller, sharedNearby, sharedVictim })
            sharedWorld.Register(unit);
        sharedService.ApplyHit(sharedKiller.NetId, sharedVictim.NetId, 20.0,
            DamageType.True);
        // Playtest B3 #12: V1.0.0.113 shared kills add 10% (was 1.261).
        TestAssert.Near(84.7, sharedKiller.Experience, 1e-9,
            "shared champion XP uses the 1.10 pool");
        TestAssert.Near(84.7, sharedNearby.Experience, 1e-9,
            "living nearby ally receives shared champion XP");

        var levelWorld = new WorldState();
        var levelHooks = new TestHooks();
        var levelTransport = new TestTransport();
        var levelStats = new HeroStatsService(GameData.Default,
            new ItemCatalog(GameData.Default.Paths), new LifecycleStatsHooks(),
            new LifecycleStatsTransport());
        var levelService = new CombatService(levelWorld, levelHooks, levelTransport,
            new FixedRandomSource(0.5), levelStats);
        var levelKiller = Hero(85, Team.Order, 100.0);
        levelKiller.Experience = 200.0;
        var levelVictim = Hero(86, Team.Chaos, 10.0);
        foreach (var unit in new[] { levelKiller, levelVictim }) levelWorld.Register(unit);
        levelService.ApplyHit(levelKiller.NetId, levelVictim.NetId, 20.0,
            DamageType.True);
        TestAssert.Equal(2, levelKiller.Level,
            "champion XP crosses the shipped level-two threshold");
        TestAssert.Equal(1, levelTransport.LevelUps,
            "champion XP emits a level-up packet");

        var adjustedWorld = new WorldState();
        var adjustedService = new CombatService(adjustedWorld, new TestHooks(),
            new TestTransport(), new FixedRandomSource(0.5));
        var lowRecipient = Hero(87, Team.Order, 100.0);
        var highRecipient = Hero(88, Team.Order, 100.0);
        var adjustedVictim = Hero(89, Team.Chaos, 10.0);
        highRecipient.Level = 18;
        adjustedVictim.Level = 2;
        foreach (var unit in new[] { lowRecipient, highRecipient, adjustedVictim })
            adjustedWorld.Register(unit);
        adjustedService.ApplyHit(lowRecipient.NetId, adjustedVictim.NetId, 20.0,
            DamageType.True);
        // 214.5 * 1.10 / 2 = 117.975 each before the level delta; the victim's level sets the base (V1.0.0.120). Batch 688: each share
        // then takes V1.0.0.75's S curve on its own delta (batch 552 paid both the bare 117.975; before it the invented {1.1, 0.5}).
        TestAssert.Near(117.975 * 2.0 / (1.0 + Math.Pow(39.0, -1.0 / 3.0)), lowRecipient.Experience, 1e-9,
            "a level-one killer of a level-two champion takes the one-level-up factor (1.54)");
        TestAssert.True(highRecipient.Experience < 1e-6 * 117.975,
            $"a level-eighteen ally sixteen levels above the victim takes a negligible share, got {highRecipient.Experience}");

        var deadKillerWorld = new WorldState();
        var deadKillerService = new CombatService(deadKillerWorld, new TestHooks(),
            new TestTransport(), new FixedRandomSource(0.5));
        var deadKiller = Hero(90, Team.Order, 100.0);
        var deadVictim = Hero(91, Team.Chaos, 10.0);
        var executionTurret = new Entity(92, EntityKind.Turret, UnitType.Turret,
            Team.Order, new(0, 0)) { Health = 100.0, MaxHealth = 100.0 };
        foreach (var unit in new Entity[] { deadKiller, deadVictim, executionTurret })
            deadKillerWorld.Register(unit);
        deadKillerService.ApplyHit(deadKiller.NetId, deadVictim.NetId, 1.0,
            DamageType.True);
        TestAssert.True(deadKillerService.Kill(deadKiller),
            "the credited champion killer can die before an execution");
        deadKillerService.ApplyHit(executionTurret.NetId, deadVictim.NetId, 20.0,
            DamageType.True);
        TestAssert.Near(154.0, deadKiller.Experience, 1e-9,
            "a dead credited killer retains earned champion XP");
    }

    [Check("combat.progression.shared-xp-production")]
    public static void SharedUnitExperienceProduction()
    {
        var world = new WorldState();
        var hooks = new TestHooks();
        var transport = new TestTransport();
        var service = new CombatService(world, hooks, transport, new FixedRandomSource(0.5));
        var first = Hero(50, Team.Order, 100.0);
        var second = Hero(51, Team.Order, 100.0);
        var third = Hero(52, Team.Order, 100.0);
        var dead = Hero(53, Team.Order, 100.0);
        var outsideRadius = Hero(55, Team.Order, 100.0);
        third.Position = new Position2(1250.0, 0.0);
        outsideRadius.Position = new Position2(1250.0001, 0.0);
        dead.Alive = false;
        var minion = new Entity(54, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new(0, 0)) { Health = 10.0, MaxHealth = 10.0, ExperienceReward = 100.0 };
        foreach (var unit in new[] { first, second, third, dead, outsideRadius, minion }) world.Register(unit);
        service.ApplyHit(first.NetId, minion.NetId, 20.0, DamageType.True);
        TestAssert.Near(36.7, first.Experience, 1e-9, "first shared XP (Mission.inibin three-way share 0.367)");
        TestAssert.Near(36.7, second.Experience, 1e-9, "second shared XP (Mission.inibin three-way share 0.367)");
        TestAssert.Near(36.7, third.Experience, 1e-9, "third shared XP (Mission.inibin three-way share 0.367)");
        TestAssert.Near(0.0, dead.Experience, 1e-9, "dead hero does not share XP");
        TestAssert.Near(0.0, outsideRadius.Experience, 1e-9,
            "hero outside Map1 lane experience radius does not share XP");

        var neutralWorld = new WorldState();
        var neutralService = new CombatService(neutralWorld, new TestHooks(),
            new TestTransport(), new FixedRandomSource(0.5));
        var orderHero = Hero(56, Team.Order, 100.0);
        var orderNear = Hero(59, Team.Order, 100.0);
        var chaosHero = Hero(57, Team.Chaos, 100.0);
        var orderOutside = Hero(60, Team.Order, 100.0);
        orderNear.Position = new Position2(799.0, 0.0);
        orderOutside.Position = new Position2(801.0, 0.0);
        var neutral = new Entity(58, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, new(0, 0))
        {
            Health = 10.0, MaxHealth = 10.0, ExperienceReward = 100.0,
        };
        foreach (var unit in new[] { orderHero, orderNear, chaosHero, orderOutside, neutral })
            neutralWorld.Register(unit);
        // Production wiring (batch 219): the camp share is the script's GiveExpToNearHeroesFromNeutral, paid in the death report through
        // the combat award (ProductionMatchComposition). The kill path pays no neutral share, so the death report's split runs here,
        // before the kill, with the same award the composition wires.
        LevelScriptHost.ShareNeutralExperience(neutralWorld, neutral, Team.Order, neutral.ExperienceReward, 800.0,
            (hero, experience) => neutralService.AwardHeroProgress(hero, experience: experience));
        neutralService.ApplyHit(orderHero.NetId, neutral.NetId, 20.0, DamageType.True);
        TestAssert.Near(55.0, orderHero.Experience, 1e-9,
            "neutral XP shares only across living heroes on the credited team");
        TestAssert.Near(55.0, orderNear.Experience, 1e-9,
            "the credited team receives neutral XP inside the 800-unit radius");
        TestAssert.Near(0.0, chaosHero.Experience, 1e-9,
            "a nearby hero on the other team receives no neutral XP");
        TestAssert.Near(0.0, orderOutside.Experience, 1e-9,
            "the credited team receives no neutral XP outside the 800-unit radius");

        var petNeutral = new Entity(61, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, new(0, 0))
        {
            Health = 10.0, MaxHealth = 10.0, ExperienceReward = 100.0,
        };
        var ownerPet = new Entity(62, EntityKind.Pet, UnitType.Monster,
            Team.Order, orderHero.Position)
        {
            OwnerNetId = orderHero.NetId, Health = 100.0, MaxHealth = 100.0,
        };
        neutralWorld.Register(ownerPet);
        neutralWorld.Register(petNeutral);
        orderNear.Position = new Position2(5000.0, 5000.0);
        var ownerExperienceBeforePetKill = orderHero.Experience;
        // The death report of a pet's kill splits with the reward team (the pet's team), the same native as above.
        LevelScriptHost.ShareNeutralExperience(neutralWorld, petNeutral, Team.Order, petNeutral.ExperienceReward, 800.0,
            (hero, experience) => neutralService.AwardHeroProgress(hero, experience: experience));
        neutralService.ApplyHit(ownerPet.NetId, petNeutral.NetId, 20.0, DamageType.True);
        TestAssert.Near(ownerExperienceBeforePetKill + 100.0, orderHero.Experience, 1e-9,
            "a pet neutral kill resolves XP to its living owner team");
        TestAssert.Near(0.0, chaosHero.Experience,
            1e-9, "pet credited neutral XP does not cross to the other team");

        // Playtest B3 #12 (26 Sept 2026): V1.0.0.104 gives a lone lane-unit
        // recipient 92% of the base; two sharers still split 110%; a lone
        // neutral kill keeps its full reward.
        var laneWorld = new WorldState();
        var laneService = new CombatService(laneWorld, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        var soloHero = Hero(63, Team.Order, 100.0);
        var pairFirst = Hero(64, Team.Order, 100.0);
        var pairSecond = Hero(65, Team.Order, 100.0);
        pairFirst.Position = new Position2(5000.0, 5000.0);
        pairSecond.Position = new Position2(5000.0, 5000.0);
        var soloMelee = new Entity(66, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new(0, 0)) { Health = 10.0, MaxHealth = 10.0, ExperienceReward = 64.0 };
        var pairMelee = new Entity(67, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new(5000, 5000)) { Health = 10.0, MaxHealth = 10.0, ExperienceReward = 64.0 };
        var soloNeutral = new Entity(68, EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            new(0, 0)) { Health = 10.0, MaxHealth = 10.0, ExperienceReward = 100.0 };
        foreach (var unit in new[] { soloHero, pairFirst, pairSecond, soloMelee, pairMelee, soloNeutral })
            laneWorld.Register(unit);
        laneService.ApplyHit(soloHero.NetId, soloMelee.NetId, 20.0, DamageType.True);
        TestAssert.Near(58.88, soloHero.Experience, 1e-9,
            "a lone melee-minion recipient receives 92% of 64 XP");
        laneService.ApplyHit(pairFirst.NetId, pairMelee.NetId, 20.0, DamageType.True);
        TestAssert.Near(35.2, pairFirst.Experience, 1e-9, "two lane sharers split 110% of 64 XP");
        TestAssert.Near(35.2, pairSecond.Experience, 1e-9, "the second lane sharer gets the same split");
        var soloBeforeNeutral = soloHero.Experience;
        // The lone neutral's death report splits with its reward team (the killer's, Order): one recipient, so the full reward.
        LevelScriptHost.ShareNeutralExperience(laneWorld, soloNeutral, Team.Order, soloNeutral.ExperienceReward, 800.0,
            (hero, experience) => laneService.AwardHeroProgress(hero, experience: experience));
        laneService.ApplyHit(soloHero.NetId, soloNeutral.NetId, 20.0, DamageType.True);
        TestAssert.Near(soloBeforeNeutral + 100.0, soloHero.Experience, 1e-9,
            "a lone neutral kill keeps its full XP");
    }

    [Check("combat.progression.dead-earned-champion-xp")]
    public static void DeadEarnedChampionExperienceAndLiveNearbyEligibility()
    {
        var world = new WorldState();
        var service = new CombatService(world, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5));
        Entity Hero(uint netId, bool alive, double x) =>
            new(netId, EntityKind.Hero, UnitType.Hero, Team.Order, new(x, 0))
            {
                Alive = alive,
                Health = alive ? 100.0 : 0.0,
                MaxHealth = 100.0,
                Level = 1,
            };
        var killer = Hero(1, true, 100);
        var assistant = Hero(2, true, 120);
        var victim = new Entity(3, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new(0, 0)) { Health = 100.0, MaxHealth = 100.0, Level = 1 };
        var nearby = Hero(4, true, 400);
        var deadNearby = Hero(5, false, 450);
        foreach (var hero in new[] { killer, assistant, victim, nearby, deadNearby })
            world.Register(hero);

        // The actual killer and assistant both die before the victim's death
        // event. Earned XP remains eligible for either role while dead.
        TestAssert.True(service.ApplyHit(killer.NetId, victim.NetId, 1.0,
            DamageType.True));
        TestAssert.True(service.Kill(killer), "earned killer can die before reward");
        TestAssert.True(service.ApplyHit(assistant.NetId, victim.NetId, 1.0,
            DamageType.True));
        TestAssert.True(service.Kill(assistant), "earned assistant can die before reward");
        TestAssert.True(service.Kill(victim, killer.NetId));

        // Playtest B3 #12 (26 Sept 2026): 154 * 1.10 / 3 (was 140 * 1.261 / 3
        // = 58.8467).
        const double earnedExperience = 56.46666666666667;
        TestAssert.Near(earnedExperience, killer.Experience, 1e-12,
            "dead credited killer retains champion XP");
        TestAssert.Near(earnedExperience, assistant.Experience, 1e-12,
            "dead qualifying assistant retains champion XP");
        TestAssert.Near(earnedExperience, nearby.Experience, 1e-12,
            "live nearby recipient shares champion XP");
        TestAssert.Near(0.0, deadNearby.Experience, 1e-12,
            "dead nearby nonparticipant is excluded");
        TestAssert.False(killer.Alive, "earned killer remains dead after XP award");
        TestAssert.Near(0.0, killer.Health, 1e-12,
            "earned killer remains at zero health after XP award");
        TestAssert.False(assistant.Alive, "earned assistant remains dead after XP award");
        TestAssert.Near(0.0, assistant.Health, 1e-12,
            "earned assistant remains at zero health after XP award");
    }

    [Check("combat.progression.global-experience")]
    public static void GlobalExperienceReachesTheWholeDestroyingTeam()
    {
        // Playtest B3 #12 (26 Sept 2026): CharacterData GlobalExpGivenOnDeath
        // is paid to every hero on the destroying team, dead included.
        foreach (var definition in MapDefinitions.Turrets.Where(turret =>
                     turret.Tier != TurretTier.Fountain))
        {
            var world = new WorldState();
            var service = new CombatService(world, new TestHooks(), new TestTransport(),
                new FixedRandomSource(0.5));
            var enemyTeam = definition.Team == Team.Order ? Team.Chaos : Team.Order;
            var killer = Hero(1, enemyTeam, 100.0);
            var distant = Hero(2, enemyTeam, 100.0);
            var dead = Hero(3, enemyTeam, 100.0);
            var defender = Hero(4, definition.Team, 100.0);
            distant.Position = new Position2(20000.0, 20000.0);
            dead.Alive = false;
            dead.Health = 0.0;
            var turret = new Entity(5, EntityKind.Turret, UnitType.Turret, definition.Team,
                definition.Position) { Name = definition.Name };
            TurretRules.Initialize(turret, 5, GameData.Default);
            var expected = definition.Tier switch
            {
                TurretTier.Outer => 30.0,
                TurretTier.Inner => 50.0,
                TurretTier.Inhibitor => 70.0,
                _ => 120.0,
            };
            TestAssert.Near(expected, turret.GlobalExperienceReward, 1e-9,
                definition.Name + " reads its archetype GlobalExpGivenOnDeath");
            foreach (var unit in new[] { killer, distant, dead, defender, turret })
                world.Register(unit);
            TestAssert.True(service.Kill(turret, killer.NetId), definition.Name + " dies");
            foreach (var hero in new[] { killer, distant, dead })
            {
                TestAssert.Near(expected, hero.Experience, 1e-9,
                    $"{definition.Name} global XP reaches hero {hero.NetId}");
                TestAssert.Near(CombatService.TurretGold, hero.Gold, 1e-9,
                    $"{definition.Name} team gold is unchanged for hero {hero.NetId}");
            }
            TestAssert.Near(0.0, defender.Experience, 1e-9,
                definition.Name + " grants its own team no XP");
        }

        var wormWorld = new WorldState();
        var wormStats = new HeroStatsService(GameData.Default,
            new ItemCatalog(GameData.Default.Paths), new LifecycleStatsHooks(),
            new LifecycleStatsTransport());
        var wormService = new CombatService(wormWorld, new TestHooks(), new TestTransport(),
            new FixedRandomSource(0.5), wormStats);
        var wormKiller = Hero(10, Team.Order, 100.0);
        var distantAlly = Hero(11, Team.Order, 100.0);
        var deadAlly = Hero(12, Team.Order, 100.0);
        var nearbyEnemy = Hero(13, Team.Chaos, 100.0);
        distantAlly.Position = new Position2(5000.0, 5000.0);
        deadAlly.Alive = false;
        deadAlly.Health = 0.0;
        var stats = NeutralStatsCalculator.Calculate(GameData.Default, "Worm");
        var worm = new Entity(14, EntityKind.Neutral, UnitType.Monster, Team.Neutral, new(0, 0))
        {
            Name = "Worm", Health = 10.0, MaxHealth = 10.0,
            GoldReward = stats.GoldReward,
            GlobalGoldReward = stats.GlobalGoldReward,
            ExperienceReward = stats.ExperienceReward,
            GlobalExperienceReward = stats.GlobalExperienceReward,
        };
        TestAssert.Near(900.0, worm.GlobalExperienceReward, 1e-9, "Worm GlobalExpGivenOnDeath");
        TestAssert.Near(800.0, worm.ExperienceReward, 1e-9, "Worm keeps the Lua local XP");
        foreach (var unit in new[] { wormKiller, distantAlly, deadAlly, nearbyEnemy, worm })
            wormWorld.Register(unit);
        // Production wiring (batch 219): the Worm's local 800 is the script's GiveExpToNearHeroesFromNeutral, paid in its death report
        // before the kill path's reward loop. That loop pays the global 900 to every hero of the reward team, dead included (no shipped
        // script pays GlobalExpGivenOnDeath). Only the killer is a living Order hero within 800 units.
        LevelScriptHost.ShareNeutralExperience(wormWorld, worm, Team.Order, worm.ExperienceReward, 800.0,
            (hero, experience) => wormService.AwardHeroProgress(hero, experience: experience));
        TestAssert.True(wormService.Kill(worm, wormKiller.NetId), "the Worm dies");
        TestAssert.Near(800.0 + 900.0, wormKiller.Experience, 1e-9,
            "the killer keeps the local 800-unit share and gains the global XP");
        TestAssert.Equal(4, wormKiller.Level, "1,700 XP reaches level four");
        TestAssert.Near(900.0, distantAlly.Experience, 1e-9, "a distant ally gains the global XP");
        TestAssert.Near(900.0, deadAlly.Experience, 1e-9, "a dead ally gains the global XP");
        TestAssert.Equal(3, deadAlly.Level, "a dead ally levels from global XP");
        TestAssert.Near(0.0, nearbyEnemy.Experience, 1e-9, "the other team gains no Baron XP");
        TestAssert.Near(25.0 + 300.0, wormKiller.Gold, 1e-9,
            "the killer's native default 25 adds to the 300 global gold");
        TestAssert.Near(300.0, deadAlly.Gold, 1e-9, "global gold still reaches dead allies");
    }

    private static void ReplaceChampion(GameData data, string name, ChampionData champion)
    {
        var field = typeof(GameData).GetField("_champions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GameData champion cache field is missing");
        var cache = (Dictionary<string, ChampionData>)field.GetValue(data)!;
        cache[name] = champion;
    }

    private static Entity Hero(uint netId, Team team, double health) =>
        new(netId, EntityKind.Hero, UnitType.Hero, team, new(0, 0))
        {
            MaxHealth = health, Health = health, Level = 1,
        };

    private static void AssertCapturedPackets(JsonElement expected,
        IReadOnlyList<PlaintextPacket> actual, LegacyBlowfishCipher cipher, string label)
    {
        AssertRetailDeathPackets(actual, label);
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, actual.Select(packet => new
            {
                channel = packet.Channel, flags = (byte)packet.Reliability,
                wire = Convert.ToHexString(packet.ToWireBytes(cipher)).ToLowerInvariant(),
                plaintext = Convert.ToHexString(cipher.Decrypt(packet.ToWireBytes(cipher))).ToLowerInvariant(),
            }).ToArray());
            return;
        }
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " packet count");
        for (var index = 0; index < actual.Count; index++)
        {
            var row = expected[index];
            var packet = actual[index];
            TestAssert.Equal(row.GetProperty("channel").GetByte(), packet.Channel, label + " channel");
            TestAssert.Equal(row.GetProperty("flags").GetByte(), (byte)packet.Reliability, label + " flags");
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("wire").GetString()!),
                packet.ToWireBytes(cipher), label + " wire");
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("plaintext").GetString()!),
                cipher.Decrypt(packet.ToWireBytes(cipher)), label + " padded plaintext");
        }
    }

    private static void AssertRetailDeathPackets(IReadOnlyList<PlaintextPacket> packets, string name)
    {
        if (name.StartsWith("nexus-", StringComparison.Ordinal))
        {
            // Batch 163: the seven locks are EndOfGameCeremony's own calls, in its own order - camera locking,
            // camera movement, abilities, summoner spells, movement, shop, minimap (EndOfGame.txt proto 0.0
            // pc 0046-0073; `combat.shipped-end-game-timeline` pins the same order).
            string[] locks = ["89000000000100000001", "89000000008000000001",
                "89000000000400000001", "89000000000800000001", "89000000000200000001",
                "89000000001000000001", "89000000004000000001"];
            var actual = packets.Where(p => p.Plaintext.Span[0] == 0x89).ToArray();
            TestAssert.Equal(locks, actual.Select(p => Convert.ToHexString(p.Plaintext.Span)).ToArray());
            foreach (var packet in actual)
            {
                TestAssert.Equal((byte)3, packet.Channel);
                TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
            }
        }
        if (name is "neutral-hero-ownerDead0" or "neutral-hero-ownerDead1" or
            "neutral-owned-pet-ownerDead0" or "neutral-owned-pet-ownerDead1")
        {
            TestAssert.Equal((byte)3, packets[3].Channel);
            TestAssert.Equal(LeaguePacketReliability.Reliable, packets[3].Reliability);
            TestAssert.Equal("CC00000000010000000108010000400002000001000000",
                Convert.ToHexString(packets[3].Plaintext.Span), "neutral last-hit score follows death");
        }
    }

    // These scenarios supplied the old caller-owned AoE factor; the shared
    // production damage category now applies it. Preserve the raw request.
    private static double HitCallerScale(DamageSourceKind? kind, double scale) =>
        kind == DamageSourceKind.SpellAoe && scale == 1.0 / 3.0 ? 1.0 : scale;

    private static object? DeathField(Entity entity, string field) => field switch
    {
        "hp" => entity.Health, "max_hp" => entity.MaxHealth, "mana" => entity.Mana,
        "max_mana" => entity.MaxMana, "atk_damage" => entity.AttackDamage, "armor" => entity.Armor,
        "spell_block" => entity.SpellBlock, "atk_period" => entity.AttackPeriod,
        "hp_regen" => entity.HealthRegen, "mp_regen" => entity.ManaRegen,
        "attack_speed_mod" => entity.AttackSpeedMod, "gold" => entity.Gold, "exp" => entity.Experience,
        "level" => entity.Level, "skill_points" => entity.SkillPoints, "alive" => entity.Alive,
        "respawn_at" => entity.RespawnAt, "champion_kills" => entity.ChampionKills,
        "atk_range" => entity.AttackRange, "windup" => entity.AttackWindup,
        "death_time" => entity.DeathTime, "attack_target" => entity.AttackTarget,
        "owner_netid" => entity.OwnerNetId, "gold_reward" => entity.GoldReward,
        "global_gold_reward" => entity.GlobalGoldReward, "xp_reward" => entity.ExperienceReward,
        _ => throw new InvalidOperationException("Unmapped death output: " + field),
    };









    private static string ExpectedOpcodes(JsonElement expected) => string.Join(",",
        expected.EnumerateArray().Select(packet => packet.GetProperty("plaintext").GetString()![..2]));

    private static string ActualOpcodes(IReadOnlyList<PlaintextPacket> actual) => string.Join(",",
        actual.Select(packet => packet.Plaintext.Length == 0 ? "--" : packet.Plaintext.Span[0].ToString("x2")));

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static Entity BuildEntity(JsonElement json)
    {
        var etype = json.GetProperty("etype").GetString() ?? string.Empty;
        var kind = etype switch
        {
            "hero" => EntityKind.Hero,
            "turret" => EntityKind.Turret,
            "minion" => EntityKind.Minion,
            "ward" => EntityKind.Ward,
            "monster" => EntityKind.Monster,
            "neutral" => EntityKind.Neutral,
            _ => EntityKind.Unknown,
        };
        var position = json.GetProperty("pos");
        var entity = new Entity(json.GetProperty("netid").GetUInt32(), kind,
            (UnitType)json.GetProperty("unit_type").GetInt32(),
            (Team)json.GetProperty("team").GetInt32(),
            new Position2(position[0].GetDouble(), position[1].GetDouble()))
        {
            // The hit corpus omits class defaults because they do not affect
            // damage state.  Protocol cases still exercise a full hero-stat
            // refresh after shield removal, so hydrate the same fixture hero
            // defaults used by the source scenarios before applying overrides.
            Speed = kind == EntityKind.Hero ? 325.0 : 0.0,
            AttackRange = kind == EntityKind.Hero ? 625.0 : 0.0,
            Health = json.GetProperty("hp").GetDouble(),
            MaxHealth = json.GetProperty("max_hp").GetDouble(),
            Mana = json.GetProperty("mana").GetDouble(),
            MaxMana = json.GetProperty("max_mana").GetDouble(),
            AttackDamage = json.GetProperty("atk_damage").GetDouble(),
            Armor = json.GetProperty("armor").GetDouble(),
            SpellBlock = json.GetProperty("spell_block").GetDouble(),
            LifeSteal = json.GetProperty("life_steal").GetDouble(),
            SpellVamp = json.GetProperty("spell_vamp").GetDouble(),
            ArmorPenetrationFlat = json.GetProperty("armor_pen_flat").GetDouble(),
            ArmorPenetrationPercent = json.GetProperty("armor_pen_percent").GetDouble(),
            MagicPenetrationFlat = json.GetProperty("magic_pen_flat").GetDouble(),
            MagicPenetrationPercent = json.GetProperty("magic_pen_percent").GetDouble(),
            GodMode = json.GetProperty("god_mode").GetBoolean(),
            Protected = json.GetProperty("protected").GetBoolean(),
            Alive = json.GetProperty("alive").GetBoolean(),
            IsWard = json.GetProperty("is_ward").GetBoolean(),
            Gold = json.GetProperty("gold").GetDouble(),
            Experience = json.GetProperty("exp").GetDouble(),
            GoldReward = json.GetProperty("gold_reward").GetDouble(),
            ExperienceReward = json.GetProperty("xp_reward").GetDouble(),
            SkillPoints = json.GetProperty("skill_points").GetInt32(),
            Level = json.GetProperty("level").GetInt32(),
        };
        foreach (var buffJson in json.GetProperty("buffs").EnumerateArray())
        {
            var payload = new Amberline.Core.Buffs.BuffPayload();
            if (buffJson.GetProperty("payload").TryGetProperty("damage_shield", out var shield)) payload.DamageShield = shield.GetDouble();
            if (buffJson.GetProperty("payload").TryGetProperty("reflect", out var reflect)) payload.Reflect = reflect.GetDouble();
            if (buffJson.GetProperty("payload").TryGetProperty("healing_received_mult", out var reduction)) payload.HealingReceivedMult = reduction.GetDouble();
            entity.Buffs.Add(new Amberline.Core.Buffs.BuffState(
                buffJson.GetProperty("name").GetString() ?? string.Empty,
                payload,
                (Amberline.Core.Buffs.BuffType)buffJson.GetProperty("type").GetInt32(),
                buffJson.GetProperty("slot").GetInt32(),
                buffJson.GetProperty("expires_at").GetDouble(),
                buffJson.GetProperty("caster").GetUInt32()));
        }
        return entity;
    }

    private static Entity BuildTickEntity(JsonElement json)
    {
        var kind = EntityKindExtensions.FromSourceType(json.GetProperty("etype").GetString());
        var position = json.GetProperty("pos");
        var facing = json.GetProperty("facing");
        var entity = new Entity(json.GetProperty("netid").GetUInt32(), kind,
            (UnitType)json.GetProperty("unit_type").GetInt32(),
            (Team)json.GetProperty("team").GetInt32(),
            new Position2(position[0].GetDouble(), position[1].GetDouble()))
        {
            Health = json.GetProperty("hp").GetDouble(),
            MaxHealth = json.GetProperty("max_hp").GetDouble(),
            Mana = json.GetProperty("mana").GetDouble(),
            MaxMana = json.GetProperty("max_mana").GetDouble(),
            AttackDamage = json.GetProperty("atk_damage").GetDouble(),
            AttackRange = json.GetProperty("atk_range").GetDouble(),
            AttackPeriod = json.GetProperty("atk_period").GetDouble(),
            AttackCooldown = json.GetProperty("attack_cd").GetDouble(),
            AttackWindup = json.GetProperty("windup").GetDouble(),
            MissileSpeed = json.GetProperty("missile_speed").GetDouble(),
            Armor = json.GetProperty("armor").GetDouble(),
            SpellBlock = json.GetProperty("spell_block").GetDouble(),
            Gold = json.GetProperty("gold").GetDouble(),
            Experience = json.GetProperty("exp").GetDouble(),
            Alive = json.GetProperty("alive").GetBoolean(),
            Created = json.GetProperty("created").GetBoolean(),
            Visible = json.GetProperty("visible").GetBoolean(),
            Radius = json.GetProperty("radius").GetDouble(),
            Dodge = json.GetProperty("dodge").GetDouble(),
            CritChance = json.GetProperty("crit_chance").GetDouble(),
            CritMultiplier = json.GetProperty("crit_multiplier").GetDouble(),
            DodgePiercing = json.GetProperty("dodge_piercing").GetBoolean(),
            Speed = json.GetProperty("speed").GetDouble(),
            ChampionName = json.GetProperty("champion_name").GetString() ?? "Annie",
            Name = json.GetProperty("name").GetString() ?? string.Empty,
            HealthRegen = json.GetProperty("hp_regen").GetDouble(),
            ManaRegen = json.GetProperty("mp_regen").GetDouble(),
            AiState = json.GetProperty("ai_state").GetString() ?? "march",
            // Standalone combat scenarios seed an already-enabled attack order;
            // they have no minion AI owner to run TimerFindEnemies for them.
            MinionAutoAttackEnabled = json.GetProperty("ai_state").GetString() == "engage",
            NeutralState = json.GetProperty("neutral_state").GetString() ?? "idle",
            Position = new Position2(position[0].GetDouble(), position[1].GetDouble()),
            Facing = new Position2(facing[0].GetDouble(), facing[1].GetDouble()),
            RespawnAt = json.GetProperty("respawn_at").GetDouble(),
        };
        var target = json.GetProperty("attack_target");
        entity.AttackTarget = target.ValueKind == JsonValueKind.Null ? null : target.GetUInt32();
        var death = json.GetProperty("death_time");
        entity.DeathTime = death.ValueKind == JsonValueKind.Null ? null : death.GetDouble();
        foreach (var buffJson in json.GetProperty("buffs").EnumerateArray())
        {
            var payload = new Amberline.Core.Buffs.BuffPayload();
            var payloadJson = buffJson.GetProperty("payload");
            if (payloadJson.TryGetProperty("miss_chance", out var missChance)) payload.MissChance = missChance.GetDouble();
            if (payloadJson.TryGetProperty("action_disabled", out var actionDisabled)) payload.ActionDisabled = actionDisabled.GetBoolean();
            if (payloadJson.TryGetProperty("movement_disabled", out var movementDisabled)) payload.MovementDisabled = movementDisabled.GetBoolean();
            entity.Buffs.Add(new Amberline.Core.Buffs.BuffState(
                buffJson.GetProperty("name").GetString() ?? string.Empty,
                payload,
                (Amberline.Core.Buffs.BuffType)buffJson.GetProperty("type").GetInt32(),
                buffJson.GetProperty("slot").GetInt32(),
                buffJson.GetProperty("expires_at").GetDouble(),
                buffJson.GetProperty("caster").GetUInt32()));
        }
        return entity;
    }

    private static Entity BuildDeathEntity(JsonElement json)
    {
        var kind = EntityKindExtensions.FromSourceType(json.GetProperty("etype").GetString());
        var position = json.GetProperty("position");
        var fields = json.GetProperty("fields");
        var entity = new Entity(json.GetProperty("netid").GetUInt32(), kind,
            (UnitType)json.GetProperty("unitType").GetInt32(),
            (Team)json.GetProperty("team").GetInt32(),
            new Position2(position[0].GetDouble(), position[1].GetDouble()))
        {
            Name = json.GetProperty("name").GetString() ?? string.Empty,
            ChampionName = json.GetProperty("champion").GetString() ?? "Annie",
            Health = fields.GetProperty("hp").GetDouble(),
            MaxHealth = fields.GetProperty("max_hp").GetDouble(),
            Mana = fields.GetProperty("mana").GetDouble(),
            MaxMana = fields.GetProperty("max_mana").GetDouble(),
            AttackDamage = fields.GetProperty("atk_damage").GetDouble(),
            Armor = fields.GetProperty("armor").GetDouble(),
            SpellBlock = fields.GetProperty("spell_block").GetDouble(),
            AttackRange = fields.GetProperty("atk_range").GetDouble(),
            AttackPeriod = fields.GetProperty("atk_period").GetDouble(),
            AttackWindup = fields.GetProperty("windup").GetDouble(),
            HealthRegen = fields.GetProperty("hp_regen").GetDouble(),
            ManaRegen = fields.GetProperty("mp_regen").GetDouble(),
            AttackSpeedMod = fields.GetProperty("attack_speed_mod").GetDouble(),
            Gold = fields.GetProperty("gold").GetDouble(),
            Experience = fields.GetProperty("exp").GetDouble(),
            Level = fields.GetProperty("level").GetInt32(),
            SkillPoints = fields.GetProperty("skill_points").GetInt32(),
            Alive = fields.GetProperty("alive").GetBoolean(),
            RespawnAt = fields.GetProperty("respawn_at").GetDouble(),
            GoldReward = fields.GetProperty("gold_reward").GetDouble(),
            GlobalGoldReward = fields.GetProperty("global_gold_reward").GetDouble(),
            ExperienceReward = fields.GetProperty("xp_reward").GetDouble(),
            OwnerNetId = fields.GetProperty("owner_netid").GetUInt32(),
            ChampionKills = fields.GetProperty("champion_kills").GetInt32(),
        };
        var death = fields.GetProperty("death_time");
        entity.DeathTime = death.ValueKind == JsonValueKind.Null ? null : death.GetDouble();
        var attackTarget = fields.GetProperty("attack_target");
        entity.AttackTarget = attackTarget.ValueKind == JsonValueKind.Null ? null : attackTarget.GetUInt32();
        return entity;
    }

    private static void ApplyTickAction(JsonElement frame, WorldState world,
        CombatService service, TestHooks hooks)
    {
        if (!frame.TryGetProperty("actions", out var actions) || actions.ValueKind == JsonValueKind.Null)
            return;
        foreach (var action in actions.EnumerateArray())
        {
            var kind = action.GetProperty("kind").GetString();
            switch (kind)
            {
                case "created-visible":
                {
                    var unit = world.Get(action.GetProperty("netid").GetUInt32()) ?? throw new InvalidDataException("tick action unit missing");
                    unit.Created = true;
                    unit.Visible = true;
                    break;
                }
                case "position":
                {
                    var unit = world.Get(action.GetProperty("netid").GetUInt32()) ?? throw new InvalidDataException("tick action unit missing");
                    var point = action.GetProperty("position");
                    unit.Position = new Position2(point[0].GetDouble(), point[1].GetDouble());
                    break;
                }
                case "dead":
                {
                    var unit = world.Get(action.GetProperty("netid").GetUInt32()) ?? throw new InvalidDataException("tick action unit missing");
                    unit.Alive = false;
                    unit.Health = 0.0;
                    break;
                }
                case "clear-buffs":
                {
                    var unit = world.Get(action.GetProperty("netid").GetUInt32()) ?? throw new InvalidDataException("tick action unit missing");
                    unit.Buffs.Clear();
                    break;
                }
                case "clear-pending-cast":
                    hooks.ActionBlockedNetId = 0;
                    break;
                default:
                    throw new InvalidDataException($"unknown combat tick action '{kind}'");
            }
        }
    }

    private static Dictionary<string, object?> TickBuffPayload(Amberline.Core.Buffs.BuffState buff)
    {
        var payload = new Dictionary<string, object?>();
        if (buff.Payload.Has(Amberline.Core.Buffs.BuffPayloadField.MissChance)) payload["miss_chance"] = buff.Payload.MissChance;
        if (buff.Payload.ActionDisabled) payload["action_disabled"] = true;
        if (buff.Payload.MovementDisabled) payload["movement_disabled"] = true;
        return payload;
    }

    private static void CompareTickFrame(JsonElement frame, WorldState world,
        CombatService service, string scenario)
    {
        if (FixtureRecorder.Enabled)
        {
            foreach (var row in frame.GetProperty("entities").EnumerateArray())
            {
                var entity = world.Get(row.GetProperty("netid").GetUInt32())
                    ?? throw new InvalidOperationException("Missing tick entity");
                foreach (var property in row.EnumerateObject())
                    CorpusSnapshot.Set(property.Value, property.Name switch
                    {
                        "netid" => (object)entity.NetId, "hp" => entity.Health, "mana" => entity.Mana,
                        "gold" => entity.Gold, "exp" => entity.Experience, "attack_cd" => entity.AttackCooldown,
                        "alive" => entity.Alive, "attack_target" => entity.AttackTarget,
                        "pos" => new[] { entity.Position.X, entity.Position.Z },
                        "facing" => new[] { entity.Facing.X, entity.Facing.Z },
                        "max_hp" => entity.MaxHealth,
                        "max_mana" => entity.MaxMana,
                        "atk_damage" => entity.AttackDamage,
                        "atk_range" => entity.AttackRange,
                        "atk_period" => entity.AttackPeriod,
                        "windup" => entity.AttackWindup,
                        "missile_speed" => entity.MissileSpeed,
                        "armor" => entity.Armor,
                        "spell_block" => entity.SpellBlock,
                        "created" => entity.Created,
                        "visible" => entity.Visible,
                        "radius" => entity.Radius,
                        "dodge" => entity.Dodge,
                        "crit_chance" => entity.CritChance,
                        "crit_multiplier" => entity.CritMultiplier,
                        "dodge_piercing" => entity.DodgePiercing,
                        "speed" => entity.Speed,
                        "champion_name" => entity.ChampionName,
                        "name" => entity.Name,
                        "hp_regen" => entity.HealthRegen,
                        "mp_regen" => entity.ManaRegen,
                        "ai_state" => entity.AiState,
                        "neutral_state" => entity.NeutralState,
                        "respawn_at" => entity.RespawnAt,
                        "etype" => entity.EntityType,
                        "unit_type" => (int)entity.UnitType,
                        "team" => (int)entity.Team,
                        "death_time" => entity.DeathTime,
                        "pending_summon" => null,
                        "buffs" => entity.Buffs.Select(buff => new { name = buff.Name, slot = buff.Slot, type = (int)buff.Type, expires_at = buff.ExpiresAt, caster = buff.CasterNetId, payload = TickBuffPayload(buff) }).ToArray(),
                        _ => throw new InvalidOperationException("Unmapped tick output: " + property.Name),
                    });
            }
            CorpusSnapshot.Set(frame.GetProperty("pendingHits"), service.PendingHits.Select(hit => new object?[]
            {
                hit.WindupRemaining, hit.ProjectilePosition.X, hit.ProjectilePosition.Z,
                hit.SourceNetId, hit.TargetNetId, hit.Damage, hit.MissileSpeed,
                CorpusSnapshot.SourceKind(hit.SourceKind), hit.MissileNetId,
            }).ToArray());
            return;
        }
        foreach (var expected in frame.GetProperty("entities").EnumerateArray())
        {
            var netId = expected.GetProperty("netid").GetUInt32();
            var actual = world.Get(netId) ?? throw new InvalidOperationException($"{scenario}: entity {netId} missing");
            TestAssert.Near(expected.GetProperty("hp").GetDouble(), actual.Health, 1e-8, $"{scenario} hp {netId}");
            TestAssert.Near(expected.GetProperty("mana").GetDouble(), actual.Mana, 1e-8, $"{scenario} mana {netId}");
            TestAssert.Near(expected.GetProperty("gold").GetDouble(), actual.Gold, 1e-8, $"{scenario} gold {netId}");
            TestAssert.Near(expected.GetProperty("exp").GetDouble(), actual.Experience, 1e-8, $"{scenario} exp {netId}");
            TestAssert.Near(expected.GetProperty("attack_cd").GetDouble(), actual.AttackCooldown, 1e-8, $"{scenario} cooldown {netId}");
            TestAssert.Equal(expected.GetProperty("alive").GetBoolean(), actual.Alive, $"{scenario} alive {netId}");
            var expectedTarget = expected.GetProperty("attack_target");
            var actualTarget = expectedTarget.ValueKind == JsonValueKind.Null ? (uint?)null : expectedTarget.GetUInt32();
            TestAssert.Equal(actualTarget, actual.AttackTarget, $"{scenario} target {netId}");
            var position = expected.GetProperty("pos");
            TestAssert.Near(position[0].GetDouble(), actual.Position.X, 1e-8, $"{scenario} x {netId}");
            TestAssert.Near(position[1].GetDouble(), actual.Position.Z, 1e-8, $"{scenario} z {netId}");
            var facing = expected.GetProperty("facing");
            TestAssert.Near(facing[0].GetDouble(), actual.Facing.X, 1e-8, $"{scenario} facing x {netId}");
            TestAssert.Near(facing[1].GetDouble(), actual.Facing.Z, 1e-8, $"{scenario} facing z {netId}");
        }
        var expectedPending = frame.GetProperty("pendingHits").EnumerateArray().ToArray();
        TestAssert.Equal(expectedPending.Length, service.PendingHits.Count, $"{scenario} pending count");
        for (var index = 0; index < expectedPending.Length; index++)
        {
            var expected = expectedPending[index];
            var actual = service.PendingHits[index];
            TestAssert.Near(expected[0].GetDouble(), actual.WindupRemaining, 1e-8, $"{scenario} pending windup {index}");
            TestAssert.Near(expected[1].GetDouble(), actual.ProjectilePosition.X, 1e-8, $"{scenario} pending x {index}");
            TestAssert.Near(expected[2].GetDouble(), actual.ProjectilePosition.Z, 1e-8, $"{scenario} pending z {index}");
            TestAssert.Equal(expected[3].GetUInt32(), actual.SourceNetId, $"{scenario} pending source {index}");
            TestAssert.Equal(expected[4].GetUInt32(), actual.TargetNetId, $"{scenario} pending target {index}");
            TestAssert.Near(expected[5].GetDouble(), actual.Damage, 1e-8, $"{scenario} pending damage {index}");
            TestAssert.Near(expected[6].GetDouble(), actual.MissileSpeed, 1e-8, $"{scenario} pending speed {index}");
            TestAssert.Equal(DamageTypeExtensions.ParseSourceKind(expected[7].GetString()),
                actual.SourceKind, $"{scenario} pending kind {index}");
            if (expected.GetArrayLength() > 8)
                TestAssert.Equal(expected[8].GetUInt32(), actual.MissileNetId,
                    $"{scenario} pending missile {index}");
        }
    }

    private sealed class AdjustableRandom : IRandomSource
    {
        public double Value { get; set; }
        public int Calls { get; private set; }
        public double NextDouble() { Calls++; return Value; }
    }

    private sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    internal sealed class LifecycleStatsHooks : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
            Amberline.Simulation.Stats.HeroAttackTimings.ApplyRefresh(hero, GameData.Default);
        public IReadOnlyList<double> ManaCosts(Entity hero) => new[] { 60.0, 80.0, 25.0, 125.0 };
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire) { }
    }

    internal sealed class LifecycleStatsTransport : IHeroStatsTransport
    {
        public void Push(HeroStatsReplicationSnapshot snapshot) { }
        public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries) { }
    }

    // The no-op combat doubles every combat check runs against; the turret differential
    // (TurretAiDifferentialChecks) reuses them so both of its sides see the same hooks.
    internal sealed class TestHooks : ICombatHooks
    {
        public DamageType LastDamageType { get; private set; }
        public DamageSourceKind? LastDamageSource { get; private set; }
        public int Deaths { get; private set; }
        public int Canceled { get; private set; }
        public int ItemAssists { get; private set; }
        public int BasicHits { get; private set; }
        public int UnitsDamaged { get; private set; }
        public Action<Entity>? DeathCallback { get; set; }
        private BuffLifecycle? _buffLifecycle;
        private IBuffEffectHooks? _buffEffects;
        private ISpellEffectSink? _spellEffects;
        private IBbHookSink? _unitHooks;
        public uint ActionBlockedNetId { get; set; }
        /// <summary>
        /// Batch 173: the shipped unit character hooks the tick rig binds (production's <c>MatchGameplayHooks.Sinks</c>);
        /// <c>ModifyBasicAttackDamage</c> forwards each landed swing's pre-mitigation amount to them, which is where a camp
        /// monster's own <c>CharOnHitUnit</c> x1.43 applies (batch 159 part 2).
        /// </summary>
        public void AttachUnitScripts(IBbHookSink sink) => _unitHooks = sink;
        public void AttachBuffLifecycle(BuffLifecycle lifecycle, IBuffEffectHooks effects)
        {
            _buffLifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            _buffEffects = effects ?? throw new ArgumentNullException(nameof(effects));
        }
        public void AttachEffectSink(ISpellEffectSink effects) =>
            _spellEffects = effects ?? throw new ArgumentNullException(nameof(effects));
        public bool IsInvulnerable(Entity unit) => false;
        public bool IsGodMode(Entity unit) => unit.GodMode;
        public bool IsTargetable(Entity unit, Entity? observer) => unit.Alive && !unit.Buffs.Any(buff => buff.Payload.Untargetable);
        public void OnBoxDamageSensor(Entity? source, Entity target, DamageSourceKind? sourceKind) { }
        public void OnBasicAttackPreDamage(Entity source, Entity target) { }
        public bool IsActionBlocked(Entity unit) => unit.NetId == ActionBlockedNetId;
        public bool ConsumeSpellShield(Entity? source, Entity target, DamageSourceKind? sourceKind, bool force) => false;
        public PreDamageResult PreDamage(Entity? source, Entity target, double amount, DamageType damageType, DamageSourceKind? sourceKind)
        {
            LastDamageType = damageType;
            LastDamageSource = sourceKind;
            return PreDamageResult.Unhandled;
        }
        public double ModifyDamageBeforeMitigation(Entity? source, Entity target, double amount, DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source, double amount, DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public void OnBasicAttackStart(Entity attacker, Entity target) { }
        public void BreakAttackStealth(Entity attacker) { }
        // Batch 159 part 2's CharOnHitUnit (a camp monster's own x1.43) runs from the character sink, the way
        // MatchGameplayHooks.ModifyBasicAttackDamage runs it for a unit a character module owns (batch 173).
        public double ModifyBasicAttackDamage(Entity source, Entity target, double damage) =>
            _unitHooks is { } scripts && scripts.OwnsUnit(source)
                ? scripts.ModifyBasicAttackDamage(source, target, damage, false, BasicAttackOutcome.Normal)
                : damage;
        public double ModifyOutgoingDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyIncomingDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double AbsorbDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double PreprocessItemDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyHealthRegeneration(Entity hero, double amount, double dt) => amount;
        public double ModifyManaRegeneration(Entity hero, double amount, double dt) => amount;
        public void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage, DamageSourceKind? sourceKind, bool reflected, bool sourceIsPeriodic) => UnitsDamaged++;
        public void OnMoltenShieldReflect(Entity source, Entity target) =>
            _spellEffects?.CreateEffect("AnnieSparks.troy", source.Position,
                targetNetId: source.NetId, bindNetId: source.NetId);
        public void OnDamageShieldChanged(Entity target, Amberline.Core.Buffs.BuffState buff, double delta, bool depleted) =>
            _buffEffects?.SendDamageShieldDelta(target, delta);
        public void RemoveBuff(Entity target, int slot)
        {
            if (_buffLifecycle is not null)
            {
                _buffLifecycle.RemoveBuff(target, slot);
                return;
            }
            for (var index = target.Buffs.Count - 1; index >= 0; index--)
            {
                if (target.Buffs[index].Slot != slot) continue;
                target.Buffs.RemoveAt(index);
                break;
            }
        }
        public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type) => BasicHits++;
        public void OnBasicAttackMiss(Entity source) { }
        public void OnPendingHitCanceled(PendingHit hit) => Canceled++;
        public void OnPendingCastCanceled(Entity unit) { }
        public bool TryFinishExistingRevival(Entity unit, uint killerNetId) => false;
        public void OnDeath(Entity unit, uint killerNetId)
        {
            Deaths++;
            DeathCallback?.Invoke(unit);
        }
        public void OnPetDeath(Entity pet) { }
        public void OnNeutralDeath(Entity neutral, uint killerNetId) { }
        public void OnItemKill(Entity? killer, Entity victim) { }
        public void OnItemDeath(Entity hero) { }
        public void OnItemAssist(Entity hero, Entity victim) => ItemAssists++;
        public void OnKill(Entity? killer, Entity victim) { }
        public void OnRespawn(Entity unit) { }
        public void OnLevelChanged(Entity hero) { }
        public void OnNeutralDamaged(Entity target, Entity? source) { }
        public void OnItemPassiveTick(double now, double dt) { }
        public void OnPeriodicDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? sourceKind) { }
        public void OnEndGame(Team winner) { }
        public void HaltAllAi() { }
    }

    internal sealed class TestTransport : ICombatTransport
    {
        public List<(uint Turret, uint? Target)> TurretTargets { get; } = new();
        public int Respawns { get; private set; }
        public int LevelUps { get; private set; }
        public int GoldExpPackets { get; private set; }
        public int GoldPackets { get; private set; }
        public double LastDeathSeconds { get; private set; }
        public bool WireReady { get; set; } = true;
        public bool IsWireReady(Entity unit) => WireReady;
        public void SendBasicAttack(Entity source, Entity target, uint missileNetId, double extraTimeSeconds,
            BasicAttackOutcome outcome, int animationIndex) { }
        public void SendTurretTarget(Entity turret, Entity? target) => TurretTargets.Add((turret.NetId, target?.NetId));
        public void SendTurretTargetHero(Entity turret, Entity? target) { }
        public void SendAggroOutline(Entity unit, uint targetNetId) { }
        public void RevealShrine(Entity shrine, Entity target) { }
        public void SendApplyDamage(uint sourceNetId, Entity? source, Entity target, double damage, DamageType type, byte typeByte) { }
        public void SendHealth(Entity target, double health) { }
        public void SendTurretStats(Entity turret) { }
        public void SendHeroStats(Entity hero) { }
        public void SendDeath(Entity target, uint killerNetId, double durationSeconds, bool becomeZombie = false) =>
            LastDeathSeconds = durationSeconds;
        public void SendTurretDestroyed(Entity turret, uint killerNetId) { }
        public void SendStructureDestroyed(Entity structure, uint killerNetId) { }
        public void SendInputLockingFlag(uint flag, bool locked) { }
        public void SendDisableHudForEndOfGame() { }
        public void SendMoveCameraToPoint(Entity hero, Position3 point, double travelSeconds) { }
        public void SendGreyscaleEnabledWhenDead(Entity hero, bool enabled) { }
        public void SendMainSfxFade(double seconds) { }
        public void SendNexusSkin(Team team, uint skinId) { }
        public void SendMinionFade(Team team, double amount, double seconds) { }
        public void SendEndOfGameEvent(Team winner) { }
        public void SendInhibitorState(Entity inhibitor, bool dead, double respawnSeconds, uint killerNetId = 0) { }
        public void SendGold(Entity killed, Entity hero, double gold) { }
        public void SendLevelUp(Entity hero) => LevelUps++;
        public void SendHeroGold(Entity hero) => GoldPackets++;
        public void SendHeroGoldExp(Entity hero) => GoldExpPackets++;
        public void SendHeroNeutralKills(Entity hero) { }
        public void SendHeroMultiKill(Entity hero, Entity victim, int count) { }
        public void SendChampionDie(Entity victim, uint killerNetId, double goldGiven, IReadOnlyList<Entity> assistants) { }
        public void SendRespawn(Entity hero) => Respawns++;
        public void SendEndGame(Team winner, bool surrendered) { }
        public void SendMinionNoRender(Entity minion) { }
        public void SendStructureProtection(Entity structure) { }
    }

    private sealed class TestBuffServices : IBuffServices
    {
        public TestBuffServices(TestHooks hooks, IBuffEffectHooks effects)
        {
            Combat = new TestBuffCombatHooks(hooks);
            Casts = new TestBuffCastHooks();
            Champions = new TestBuffChampionHooks();
            Effects = effects ?? throw new ArgumentNullException(nameof(effects));
            Items = new TestBuffItemHooks();
        }

        public ICombatBuffHooks Combat { get; }
        public ICastBuffHooks Casts { get; }
        public IChampionBuffHooks Champions { get; }
        public IBuffEffectHooks Effects { get; }
        public IItemBuffHooks Items { get; }
    }

    private sealed class TestBuffCombatHooks(TestHooks hooks) : ICombatBuffHooks
    {
        public bool IsPendingActionBlocked(Entity unit) => hooks.IsActionBlocked(unit);
        public double HealingMultiplier(Entity unit) => 1.0;
        public void InterruptBasicAttack(Entity unit) { }
        public void ApplyPeriodicDamage(PeriodicDamageRequest request) =>
            request.Target.Health = Math.Max(0.0, request.Target.Health - request.Amount);
        public void Kill(Entity unit, uint killerNetId = 0)
        {
            unit.Health = 0.0;
            unit.Alive = false;
        }
    }

    private sealed class TestBuffCastHooks : ICastBuffHooks
    {
        public void CancelPendingCast(Entity hero) { }
    }

    private sealed class TestBuffChampionHooks : IChampionBuffHooks
    {
        public void OnBuffTick(Entity unit, Amberline.Core.Buffs.BuffState buff, double dt) { }
        public void OnBuffRemoved(Entity unit, Amberline.Core.Buffs.BuffState buff) { }
        public void OnBuffRemovedAfterWire(Entity unit, Amberline.Core.Buffs.BuffState buff) { }
    }

    private sealed class TestBuffItemHooks : IItemBuffHooks
    {
        public bool HasItem(Entity unit, int itemId) =>
            unit.Inventory.Any(slot => slot?.ItemId == itemId);
        public void SyncItemSlow(Entity target) { }
    }

    /// <summary>
    /// The buff boundary <see cref="HeroAttackOrderSystem"/> takes (Hero.luaobj's
    /// <c>SpellBuffRemoveType</c> reaches it). These checks never add a buff, so every add is
    /// dropped and every remove reports the buff was not there.
    /// </summary>
    private sealed class NoSpellBuffs : ISpellBuffService
    {
        public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) { }
        public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) { }
        public BuffState? Add(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true) => null;
        public BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, int maxStacks, BuffPayload perStackPayload,
            bool replaceExistingPayload = false, bool canMitigateDuration = true) => null;
        public bool Remove(Entity unit, int slot) => false;
        public bool Has(Entity unit, string name) => false;
        public void Tick(Entity unit, double dt) { }
    }

    private sealed class CapturingSpellEffectSink(ICombatPacketQueue queue) : ISpellEffectSink
    {
        private readonly ICombatPacketQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        private Func<UnitType, uint>? _allocate;
        private readonly NavigationGrid? _navigation;

        public CapturingSpellEffectSink(ICombatPacketQueue queue, Func<UnitType, uint> allocate)
            : this(queue) => _allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));

        public CapturingSpellEffectSink(ICombatPacketQueue queue, Func<UnitType, uint> allocate,
            NavigationGrid navigation)
            : this(queue, allocate) => _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

        public bool IsReferenceReady(Entity unit) => true;

        public void SendCast(Entity caster, SpellCastInfo info, long casterPositionSyncId = 0, bool missileOnlyForCaster = false) =>
            throw new NotSupportedException(nameof(SendCast));
        public void SendMissile(Entity caster, SpellCastInfo info, Position3 position,
            Position3 direction, Position3 velocity, Position3 startPoint, Position3 endPoint,
            Position3? unitPosition = null, Position3? casterPosition = null, bool needsCastAnnouncement = false) =>
            throw new NotSupportedException(nameof(SendMissile));

        public void SendWaypoints(Entity unit, IReadOnlyList<Position2> points, long syncId) =>
            _queue.Enqueue(VisionPackets.WaypointList(unit.NetId, points, checked((uint)syncId)));

        public void SendStopAttack(Entity unit)
        {
            if (CombatPackets.StopAttack(unit) is { } packet) _queue.Enqueue(packet);
        }
        public void SendCooldown(Entity unit, int slot, double seconds, bool summoner = false) =>
            throw new NotSupportedException(nameof(SendCooldown));
        public void SendUpgrade(Entity unit, int slot, int rank, int pointsLeft) =>
            throw new NotSupportedException(nameof(SendUpgrade));
        public void SendFaceDirection(Entity unit, Position3 direction) =>
            throw new NotSupportedException(nameof(SendFaceDirection));
        public void SendCharacterFade(Entity unit, short fadeId, float? amount = null,
            float duration = 0.0f) => throw new NotSupportedException(nameof(SendCharacterFade));
        public void SendTeleport(Entity unit, Position2 destination, byte teleportId, long syncId,
            Position2? staging) => throw new NotSupportedException(nameof(SendTeleport));
        public void SendCamera(Entity unit, Position2 position) =>
            throw new NotSupportedException(nameof(SendCamera));
        public void SendSpawn(Entity unit) => throw new NotSupportedException(nameof(SendSpawn));
        public void SendPetSpawn(Entity unit, SpawnPetSnapshot snapshot) =>
            throw new NotSupportedException(nameof(SendPetSpawn));
        public void SendStats(Entity unit) => throw new NotSupportedException(nameof(SendStats));
        public void SendAttackSpeed(Entity unit) => _queue.Enqueue(CombatPackets.MinionAttackSpeed(
            unit.NetId, (float)unit.AttackSpeedMod));
        public void SendMovementSpeed(Entity unit) => _queue.Enqueue(CombatPackets.MinionMovementSpeed(
            unit.NetId, (float)unit.Speed));
        public void SendHealth(Entity unit, double health) =>
            throw new NotSupportedException(nameof(SendHealth));

        public uint CreateEffect(string effectName, Position2 position, Position2? target = null,
            uint targetNetId = 0, uint bindNetId = 0, Position3 direction = default,
            Position2? ownerPosition = null, Team? alliedTeam = null, string? enemyEffectName = null,
            string? boneName = null, bool persistent = false, uint ownerHeroNetId = 0,
            Func<(Position2 Source, Position2 Target)>? livePositions = null,
            ushort flags = 0, string? targetBoneName = null, UnboundEffectFallback? unbound = null, Team? fowTeam = null)
        {
            if (_allocate is null) throw new NotSupportedException(nameof(CreateEffect));
            if (_navigation is null) throw new NotSupportedException(nameof(CreateEffect));
            var effectNetId = _allocate(UnitType.Missile);
            var metadata = _navigation.Metadata;
            var bounds = new NavigationBounds(metadata.MinX, metadata.MaxX,
                metadata.MinZ, metadata.MaxZ);
            var packet = FxPackets.CreateGroup(
                SpellPackets.BuffNameHash(Path.GetFileNameWithoutExtension(effectName)),
                effectNetId, position, bounds,
                (x, z) => _navigation.HeightAt(new Position2(x, z)),
                target, ownerPosition ?? (targetNetId != 0 ? target : null) ?? position,
                targetNetId, bindNetId, flags: flags, direction: direction,
                boneHash: boneName is null ? 0 : SpellPackets.BuffNameHash(boneName),
                targetBoneHash: targetBoneName is null ? 0 : SpellPackets.BuffNameHash(targetBoneName));
            _queue.Enqueue(packet);
            return effectNetId;
        }
        public void KillEffect(uint effectNetId) =>
            _queue.Enqueue(FxPackets.Kill(effectNetId));

        public void SendBuffDelta(Entity unit, Amberline.Core.Buffs.BuffState buff,
            bool added, int count = 1)
        {
            if (added)
            {
                _queue.Enqueue(SpellPackets.AddBuff(new BuffAdd(
                    unit.NetId, checked((byte)buff.Slot), (byte)buff.Type,
                    SpellPackets.BuffNameHash(buff.Name), Float(buff.ExpiresAt), buff.CasterNetId,
                    checked((byte)count))));
            }
            else
            {
                _queue.Enqueue(SpellPackets.RemoveBuff(unit.NetId,
                    checked((byte)buff.Slot), SpellPackets.BuffNameHash(buff.Name)));
            }
        }

        public void SendShieldDelta(Entity unit, double amount) =>
            _queue.Enqueue(SpellPackets.ModifyShield(unit.NetId, Float(amount)));
        public void PauseAnimation(Entity unit, bool paused) =>
            _queue.Enqueue(SpellPackets.PauseAnimation(unit.NetId, paused));

        private static float Float(double value)
        {
            if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value));
            return (float)value;
        }

    }

    private sealed class CapturingReplicationTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private sealed class CapturingCombatPacketQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public List<PlaintextPacket> GlobalPackets { get; } = new();
        public void Clear() { Packets.Clear(); GlobalPackets.Clear(); }
        public void Enqueue(PlaintextPacket packet, bool global = false)
        {
            Packets.Add(packet);
            if (global) GlobalPackets.Add(packet);
        }
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId,
            Func<uint, PlaintextPacket> buildPacket) => Packets.Add(buildPacket(killerNetId));
    }
}
