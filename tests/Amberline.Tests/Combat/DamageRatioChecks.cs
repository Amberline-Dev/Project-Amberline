using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Map1 Constants.var dr_* (the character data init and the attack-ratio getters):
/// the shipped class-to-class basic-attack ratios. Source-backed, not a recorded corpus.
/// </summary>
public static class DamageRatioChecks
{
    private static readonly AttackRolls NoRoll = new(1, 1, 1);

    [Check("combat.damage-ratios-shipped-table")]
    public static void ShippedTableIsReadFromConstantsVar()
    {
        // Literal pins of LEVELS/Map1/Constants.var; the Map8 read proves the values are data, not code.
        var map1 = MapConstants.LoadMap1().DamageRatios;
        TestAssert.Equal(new DamageRatios(
            HeroToHero: 1.0, HeroToUnit: 1.0, HeroToBuilding: 1.0,
            UnitToHero: 0.7, UnitToUnit: 1.0, UnitToBuilding: 0.5,
            BuildingToHero: 1.05, BuildingToUnit: 1.105, BuildingToBuilding: 1.0), map1);
        TestAssert.Equal(map1, GameData.Default.LevelConstants.DamageRatios, "GameData exposes the same Map1 table");

        var odin = MapConstants.Load(GameDataPaths.Default, "Map8").DamageRatios;
        TestAssert.Equal(0.85, odin.UnitToHero);
        TestAssert.Equal(0.4, odin.UnitToBuilding);
        TestAssert.Equal(1.1, odin.BuildingToHero);
        TestAssert.Equal(1.0, odin.BuildingToUnit);
    }

    [Check("combat.attack-speed-floor-is-the-shipped-constant")]
    public static void AttackSpeedFloorIsGcdPercentAttackSpeedModMinimum()
    {
        // gcd_PercentAttackSpeedModMinimum -0.95 is a multiplier floor of 0.05; heroes, buffs and neutral spawns all read it from here.
        var constants = MapConstants.LoadMap1();
        TestAssert.Near(0.05, constants.AttackSpeedMultiplierFloor, 1e-12);
        TestAssert.Near(1.0 + constants.Values["gcd_PercentAttackSpeedModMinimum"], constants.AttackSpeedMultiplierFloor, 1e-12);
    }

    [Check("combat.damage-ratios-object-classes")]
    public static void ClientObjectClassesMapToRatioClasses()
    {
        DamageClass Of(UnitType type) => CombatRules.ClassOf(
            new Entity(1, EntityKind.Unknown, type, Team.Order, new Position2(0, 0)));
        TestAssert.Equal(DamageClass.Hero, Of(UnitType.Hero));
        // obj_AI_Minion: lane minions, jungle monsters and every SpawnPet/SpawnMinion unit
        // (this server types pets, wards, clones and traps as UnitType.Monster).
        TestAssert.Equal(DamageClass.Unit, Of(UnitType.Minion));
        TestAssert.Equal(DamageClass.Unit, Of(UnitType.Monster));
        // obj_AI_Turret and obj_Building both read the dr_*ToBuilding field.
        TestAssert.Equal(DamageClass.Building, Of(UnitType.Turret));
        TestAssert.Equal(DamageClass.Building, Of(UnitType.Structure));
    }

    [Check("combat.damage-ratios-basic-attack-raw")]
    public static void BasicAttackAppliesShippedRatioOnce()
    {
        var hero = Unit(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, 0);
        var minion = Unit(3, EntityKind.Minion, UnitType.Minion, Team.Chaos, 0);
        var monster = Unit(4, EntityKind.Neutral, UnitType.Monster, Team.Chaos, 0);
        var turret = Unit(5, EntityKind.Turret, UnitType.Turret, Team.Chaos, 0);
        var nexus = Unit(6, EntityKind.Nexus, UnitType.Structure, Team.Chaos, 0);
        double Raw(Entity attacker, Entity target) => CombatRules.BasicAttackDamage(attacker, target, NoRoll).Damage;

        // Lane minions: UnitToHero 0.7, UnitToBuilding 0.5, UnitToUnit 1.0.
        var melee = Unit(10, EntityKind.Minion, UnitType.Minion, Team.Order, 11);
        TestAssert.Near(7.7, Raw(melee, hero), 1e-9, "melee minion against a champion");
        TestAssert.Near(5.5, Raw(melee, turret), 1e-9, "melee minion against a turret");
        TestAssert.Near(5.5, Raw(melee, nexus), 1e-9, "melee minion against the nexus");
        TestAssert.Near(11, Raw(melee, minion), 1e-9, "melee minion against a minion");
        TestAssert.Near(11, Raw(melee, monster), 1e-9, "melee minion against a monster");

        // Cannon: 40 x 0.5 here, the MechCannon script restores x2 against turrets (production check).
        var siege = Unit(11, EntityKind.Minion, UnitType.Minion, Team.Order, 40);
        siege.MinionType = MinionType.Siege;
        TestAssert.Near(20, Raw(siege, turret), 1e-9, "siege ratio before its script x2");

        // Jungle monster: UnitToHero 0.7. The character scripts' own CharOnHitUnit x1.43 (CharScriptGolem and its nine
        // siblings, 0039-0063) is not this raw formula's business any more (batch 159 part 2): the raw hit is 18 x 0.7
        // against a champion and 18 against a minion. The script's x1.43 then applies to the minion only: 2011 footage shows a
        // champion taking 18 x 0.7 (batch 758, BbHookRouter.RunHitUnit).
        var wolf = Unit(12, EntityKind.Neutral, UnitType.Monster, Team.Neutral, 18);
        wolf.Name = "wolf";
        TestAssert.Near(18 * 0.7, Raw(wolf, hero), 1e-9, "wolf against a champion");
        TestAssert.Near(18, Raw(wolf, minion), 1e-9, "wolf against a minion keeps only the shipped ratio");

        // Pets are minions (the pet spawn routine calls the minion creator): Unit class.
        foreach (var kind in new[] { EntityKind.Pet, EntityKind.ShacoClone, EntityKind.YorickGhoul, EntityKind.YorickRevenant })
        {
            var pet = Unit(13, kind, UnitType.Monster, Team.Order, 100);
            TestAssert.Near(70, Raw(pet, hero), 1e-9, $"{kind} against a champion");
            TestAssert.Near(50, Raw(pet, turret), 1e-9, $"{kind} against a turret");
            TestAssert.Near(100, Raw(pet, minion), 1e-9, $"{kind} against a minion");
        }

        // Turrets: BuildingToHero 1.05, BuildingToUnit 1.105, BuildingToBuilding 1.0.
        var outer = Unit(14, EntityKind.Turret, UnitType.Turret, Team.Order, 152);
        TestAssert.Near(152 * 1.05, Raw(outer, hero), 1e-9, "outer turret against a champion");
        TestAssert.Near(159.6, Raw(outer, hero), 1e-9);
        TestAssert.Near(152 * 1.105, Raw(outer, minion), 1e-9, "outer turret against a minion");
        TestAssert.Near(152, Raw(outer, turret), 1e-9, "building against building");

        // Heroes deal 1.0 to every class (HeroToHero/Unit/Building).
        var attacker = Unit(15, EntityKind.Hero, UnitType.Hero, Team.Order, 100);
        foreach (var target in new[] { hero, minion, monster, turret, nexus })
            TestAssert.Near(100, Raw(attacker, target), 1e-9, $"hero against {target.UnitType}");

        // The ratio scales the post-crit raw damage (a plain factor, applied once).
        melee.CritChance = 1.0;
        melee.CritMultiplier = 2.0;
        var critical = CombatRules.BasicAttackDamage(melee, hero, new AttackRolls(0, 1, 1));
        TestAssert.True(critical.Critical, "minion crit roll");
        TestAssert.Near(11 * 2.0 * 0.7, critical.Damage, 1e-9);

        // An explicit table (Map8) changes the result: the numbers are not compiled in.
        var odin = MapConstants.Load(GameDataPaths.Default, "Map8").DamageRatios;
        melee.CritChance = 0;
        TestAssert.Near(11 * 0.85, CombatRules.BasicAttackDamage(melee, hero, NoRoll, odin).Damage, 1e-9);
    }

    private static Entity Unit(uint netId, EntityKind kind, UnitType type, Team team, double attackDamage) =>
        new(netId, kind, type, team, new Position2(0, 0)) { AttackDamage = attackDamage };
}

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Production composition: the ratio is a raw-damage factor applied at queue time, before the
    /// character scripts' on-hit scalars (cannon x2 vs turrets, super x0.25 vs structures), the turret
    /// ramp and mitigation. Spells and direct ApplyHit calls are not basic-attack ratios.
    /// </summary>
    [Check("combat.damage-ratios-production-basic-attacks")]
    public static void ProductionBasicAttacksUseShippedRatios()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var champion = match.Hero(1);
        var combat = runtime.Combat;

        // Production turrets also attack on their own, so every other turret is parked on a long
        // cooldown to keep each queued swing isolated.
        foreach (var other in runtime.World.Entities.Values.Where(entity => entity.UnitType == UnitType.Turret))
            other.AttackCooldown = 1_000_000;

        // Health lost by the target when this one queued swing lands.
        double Landed(Entity attacker, Entity target)
        {
            target.Armor = 0;
            target.SpellBlock = 0;
            target.MaxHealth = target.Health = 1_000_000;
            attacker.AttackCooldown = 0;
            var before = target.Health;
            TestAssert.True(combat.QueueBasicAttack(attacker, target, new AttackRolls(1, 1, 1)),
                $"{attacker.Kind} queues a basic attack on {target.Kind}");
            for (var i = 0; i < 200 && combat.PendingHits.Count > 0; i++)
                combat.CombatTick(0.05, regenerate: false);
            TestAssert.Equal(0, combat.PendingHits.Count, "the swing resolved");
            attacker.AttackCooldown = 1_000_000;
            return before - target.Health;
        }

        Entity Spawn(Team team, MinionType type, Position2 position)
        {
            var minion = runtime.Waves!.SpawnFixture(team, type, 0, "C", 0, 0);
            minion.Position = position;
            runtime.Delivery.Flush();
            return minion;
        }

        champion.Position = new Position2(7000, 7000);
        var melee = Spawn(Team.Order, MinionType.Melee, champion.Position + new Position2(60, 0));
        TestAssert.Near(MinionDefinitions.BaseStats(MinionType.Melee).AttackDamage, melee.AttackDamage, 1e-9);
        TestAssert.Near(11 * 0.7, Landed(melee, champion), 1e-6, "melee minion hits a champion for 70%");

        // Turret and nexus targets share the Building row (0.5 for a minion).
        var nexus = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Structure),
            EntityKind.Nexus, UnitType.Structure, Team.Chaos, new Position2(7000, 8000)));
        var dummyTurret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, Team.Chaos, new Position2(7000, 6000)));
        dummyTurret.AttackCooldown = 1_000_000;
        melee.Position = nexus.Position + new Position2(0, -60);
        TestAssert.Near(5.5, Landed(melee, nexus), 1e-6, "melee minion hits the nexus for 50%");

        // Cannon: 40 x 0.5 (ratio) x 2 (MechCannon CharOnHitUnit vs turrets) = 40; no script factor vs the nexus.
        var siege = Spawn(Team.Order, MinionType.Siege, dummyTurret.Position + new Position2(0, 150));
        TestAssert.Near(40, siege.AttackDamage, 1e-9);
        TestAssert.Near(40, Landed(siege, dummyTurret), 1e-6, "siege minion keeps its full 40 against a turret");
        siege.Position = nexus.Position + new Position2(0, -150);
        TestAssert.Near(20, Landed(siege, nexus), 1e-6, "siege minion against the nexus: 40 x 0.5");

        // Super minion: 180 x 0.5 (ratio) x 0.25 (StructureDamageReduction) = 22.5 against the nexus.
        var super = Spawn(Team.Order, MinionType.Super, nexus.Position + new Position2(0, -90));
        TestAssert.True(super.Buffs.Any(buff => buff.Name == "StructureDamageReduction"));
        TestAssert.Near(22.5, Landed(super, nexus), 1e-6, "super minion against the nexus");
        super.Position = dummyTurret.Position + new Position2(0, 90);
        TestAssert.Near(180 * 0.5, Landed(super, dummyTurret), 1e-6,
            "StructureDamageReduction covers inhibitor/nexus only; a turret takes 180 x 0.5");

        // Turret: 152 x 1.05 against a champion, then the first-hit ramp (0.9); x1.105 against a minion.
        var turret = runtime.World.Entities.Values.First(entity =>
            entity.Kind == EntityKind.Turret && entity.Team == Team.Order && entity.Alive);
        champion.Position = turret.Position + new Position2(300, 0);
        var raw = combat.BasicAttackDamage(turret, champion, new AttackRolls(1, 1, 1)).Damage;
        TestAssert.Near(turret.AttackDamage * 1.05, raw, 1e-6, "turret queue amount against a champion");
        TestAssert.Near(turret.AttackDamage * 1.05 * 0.9, Landed(turret, champion), 1e-6,
            "the ratio sits before the turret ramp (first hit 0.9)");
        var victim = Spawn(Team.Chaos, MinionType.Melee, turret.Position + new Position2(0, 300));
        TestAssert.Near(turret.AttackDamage * 1.105, Landed(turret, victim), 1e-6, "turret against a lane minion");

        // Direct ApplyHit (spells, scripted effects) is not a basic-attack ratio.
        champion.Health = champion.MaxHealth = 1_000_000;
        TestAssert.True(combat.ApplyHit(melee.NetId, champion.NetId, 100, DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(100, 1_000_000 - champion.Health, 1e-6, "minion spell damage is not scaled by dr_UnitToHero");
    }
}
