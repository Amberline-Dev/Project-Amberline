using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat.live-loadout-mastery-hooks")]
    public static void LiveLoadoutMasteryHooks()
    {
        using var match = new Match("Annie", Team.Order, ally: true);
        var runtime = match.Runtime;
        var catalog = new MasteryCatalog();
        var target = match.Hero(1);
        target.Masteries = DefensivePage(catalog);
        target.Level = 18;
        target.Inventory = new InventorySlot?[]
        {
            new InventorySlot(1042), null, null, null, null, null
        };
        // A synthetic source buff has no lifetime of its own: the default ExpiresAt of 0 ends it on the first buff
        // update, which the batch-144 live loop now runs. Give them the long finite life the other live fixtures use.
        target.Buffs.Add(new BuffState("regen-one", new BuffPayload { HpRegen = 1.0 }, expiresAt: 100.0));
        target.Buffs.Add(new BuffState("regen-two", new BuffPayload { HpRegen = 1.0 }, expiresAt: 100.0));
        target.Buffs.Add(new BuffState("attack-speed-source", new BuffPayload { AttackSpeedMod = 0.20 }, expiresAt: 100.0));
        runtime.HeroStats.Refresh(target);

        var itemCatalog = new ItemCatalog(runtime.GameData.Paths);
        var noPenetration = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level,
                new InventorySlot?[] { null, null, null, null, null, null },
                masteries: MasteryPage.Empty));
        var archaicPage = MasteryPage.Create(catalog,
            new Dictionary<int, int>
            {
                [103] = 3, [136] = 1, [126] = 1, [106] = 3,
                [102] = 4, [107] = 1
            });
        var archaicOnly = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level,
                new InventorySlot?[] { null, null, null, null, null, null },
                masteries: archaicPage));
        TestAssert.Near(0.0, noPenetration.MagicPenPercent, 1e-12,
            "no loadout has no magic penetration");
        TestAssert.Near(0.15, archaicOnly.MagicPenPercent, 1e-12,
            "Archaic Knowledge contributes its native 15 percent");
        // Batch 144: Void Staff's 40 percent is the VoidStaff buff its own script keeps alive, so the pair is read off
        // a live hero; neither the inventory aggregation nor the calculator carries it any more. Archaic Knowledge is
        // still the mastery page's own 15 percent, which the pure calculator above pins on its own.
        TestAssert.Near(0.49,
            Live(target.ChampionName, target.Level, [3135], archaicPage, hero => hero.MagicPenetrationPercent), 1e-12,
            "Void Staff and Archaic Knowledge compose as native remaining resistance");
        // Batch 144: Ardor is a live buff now. Its own BuffOnUpdateActions scales the pool on every buff update, so the
        // hero has to run a few frames before the number it settles on is on it — the batch-140 non-retail frozen
        // payload (the calculator's hard-coded 1.04) is gone. Batch 150: "a few frames" is no longer enough. The
        // stats hook applies that AS on its own 0.25 s grid (BbBuffRuntime.StatsUpdateInterval) while the actions hook
        // runs on the simulation step, so the first sample carries PercentMod/(1 + PercentMod) of the pool and each
        // later sample walks the share up to PercentMod — the multiply the script is written for. Three seconds is past
        // the ramp (measured: the share is 0.35*0.04/1.04 at the first sample and 0.35*0.04 exactly from 3 s on).
        Advance(match, 3.0);
        runtime.HeroStats.Refresh(target);
        var withMastery = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level, target.Inventory,
                target.Buffs, target.ItemState, target.Runes, target.Masteries));
        var withoutMastery = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level, target.Inventory,
                target.Buffs, target.ItemState, target.Runes, MasteryPage.Empty));
        TestAssert.Near(withMastery.HpRegen, target.HealthRegen, 1e-9,
            "production refresh applies loadout regen");
        TestAssert.Near(withMastery.HpRegen - withoutMastery.HpRegen,
            withMastery.MaxMp * 0.002, 1e-9,
            "Strength of Spirit is added once with multiple regen buffs");
        var levelStats = runtime.GameData.StatsAtLevel(
            runtime.GameData.Champion(target.ChampionName), target.Level);
        // Dagger's shipped PercentAttackSpeedMod is a Float32 value.
        var preArdorAttackSpeedPercent = 0.15000000596046448 + 0.20;
        var expectedAttackSpeed = levelStats.AttackSpeedMod +
                                  preArdorAttackSpeedPercent * 1.04;
        TestAssert.Near(expectedAttackSpeed, target.AttackSpeedMod, 1e-9,
            "Ardor scales the maintained additive base plus bonus attack-speed pool");

        // Batch 144: ZhonyasRing's and Ardor's own scripts are the multipliers now. Each BuffOnUpdateActions reads the
        // flat pool, takes its own prior share back off it and keeps its PercentMod of what is left, so each settles on
        // exactly the multiply this area used to hard-code — 1.30 over the flat pool with Deathcap's 140, 1.04 with
        // Ardor, and 1.30 * 1.04 when both run. Measured on a live hero, because neither number is in the calculator.
        var deathcapFlatAp = itemCatalog.Get(3089)!.Modifier("FlatMagicDamageMod");
        var flatApBasis = Live(target.ChampionName, target.Level, [], MasteryPage.Empty,
            hero => hero.AbilityPower, 20.0);
        TestAssert.Near(flatApBasis * 1.04,
            Live(target.ChampionName, target.Level, [], target.Masteries, hero => hero.AbilityPower, 20.0), 1e-9,
            "Ardor applies to the complete active flat-AP pool");
        TestAssert.Near((flatApBasis + deathcapFlatAp) * 1.30,
            Live(target.ChampionName, target.Level, [3089], MasteryPage.Empty, hero => hero.AbilityPower, 20.0), 1e-9,
            "Deathcap applies its native multiplier to the complete flat-AP pool");
        TestAssert.Near((flatApBasis + deathcapFlatAp) * 1.30 * 1.04,
            Live(target.ChampionName, target.Level, [3089], target.Masteries, hero => hero.AbilityPower, 20.0), 1e-9,
            "Deathcap and Ardor compose sequentially over active flat AP");
        TestAssert.Near(2.0, target.LoadoutMasteries.Combat.HardenSkinPhysicalDamageBlock, 1e-12);

        target.Health = target.MaxHealth = 10000.0;
        target.Protected = false;
        var aiSource = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order, target.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0, SpellBlock = 0.0 });
        var before = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        var expectedTaken = (100.0 - 2.0) *
            CombatRules.ResistanceMultiplier(target.Armor) * 0.96 - 2.0;
        TestAssert.Near(expectedTaken, before - target.Health, 1e-8,
            "Harden Skin is taken before resistance, the CharOnPreDamage talents 125 (x0.96) then 144 (-2) after it");

        target.Health = 10000.0;
        var aiSpellBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Spell));
        TestAssert.Near(100.0 * CombatRules.ResistanceMultiplier(target.Armor) * 0.96 - 2.0,
            aiSpellBefore - target.Health, 1e-8,
            "Harden Skin is a physical basic-hit event, while Defensive still sees AI spell damage");

        target.Health = 10000.0;
        var aiTrueBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(98.0, aiTrueBefore - target.Health, 1e-8,
            "Defensive Mastery (CharOnPreDamage, no damage-type test) reduces true damage too");

        var attacker = match.Hero(0);
        attacker.Masteries = OffensivePage(catalog);
        runtime.HeroStats.Refresh(attacker);
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var minionBefore = minion.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, minion.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near(104.0, minionBefore - minion.Health, 1e-8,
            "Offensive Mastery applies to an actual hero versus a minion");

        var spellVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, SpellBlock = 0.0 });
        var spellBefore = spellVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, spellVictim.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(100.0, spellBefore - spellVictim.Health, 1e-8,
            "Offensive Mastery is an on-hit AI branch, not spell damage");

        var trueVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 100.0 });
        var trueBefore = trueVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, trueVictim.NetId, 100.0,
            DamageType.True, DamageSourceKind.Basic));
        TestAssert.Near(104.0, trueBefore - trueVictim.Health, 1e-8,
            "Offensive Mastery on-hit bonus also applies to true damage");

        var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var turretBefore = turret.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, turret.NetId, 100.0,
            DamageType.True, DamageSourceKind.Basic));
        TestAssert.Near(100.0, turretBefore - turret.Health, 1e-8,
            "Offensive Mastery excludes turrets");

        target.Health = 10000.0;
        var heroSpellBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(100.0 * CombatRules.ResistanceMultiplier(target.SpellBlock) * 0.96,
            heroSpellBefore - target.Health, 1e-8,
            "Tenacity reduces non-true hero damage after resistance");
        target.Health = 10000.0;
        var heroTrueBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 100.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(100.0, heroTrueBefore - target.Health, 1e-8,
            "Tenacity excludes true damage");

        target.Health = 10000.0;
        var turretPhysicalBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near((100.0 - 2.0) * CombatRules.ResistanceMultiplier(target.Armor) * 0.96,
            turretPhysicalBefore - target.Health, 1e-8,
            "Tenacity reduces turret physical damage after Harden Skin");
        target.Health = 10000.0;
        var turretMagicBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, target.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(100.0 * CombatRules.ResistanceMultiplier(target.SpellBlock) * 0.96,
            turretMagicBefore - target.Health, 1e-8,
            "Tenacity also reduces turret magic damage");

        var clone = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.ShacoClone, UnitType.Monster, Team.Order, attacker.Position)
        { OwnerNetId = attacker.NetId, Health = 1000.0, MaxHealth = 1000.0, AttackDamage = 100.0 });
        var cloneVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var cloneBefore = cloneVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(clone.NetId, cloneVictim.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near(100.0, cloneBefore - cloneVictim.Health, 1e-8,
            "clone damage does not inherit owner Offensive Mastery");

        attacker.Masteries = HavocPage(catalog);
        runtime.HeroStats.Refresh(attacker);
        var havocVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var havocBefore = havocVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, havocVictim.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Spell));
        TestAssert.Near(104.0, havocBefore - havocVictim.Health, 1e-8,
            "Havoc increases actual-hero non-true damage");
        var havocTrueVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var havocTrueBefore = havocTrueVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, havocTrueVictim.NetId, 100.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(100.0, havocTrueBefore - havocTrueVictim.Health, 1e-8,
            "Havoc excludes true damage");

        target.Dodge = 1.0;
        var dodge = runtime.Combat.BasicAttackDamage(attacker, target,
            new AttackRolls(Crit: 1.0, Dodge: 0.0, Miss: 1.0));
        TestAssert.True(dodge.Dodged, "target dodge is rolled");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "Nimbleness"),
            "Nimbleness is installed by the production dodge hook");
    }

    /// <summary>
    /// One hero of a throwaway match that bought <paramref name="ids"/> and wears <paramref name="masteries"/>, read
    /// after the purchase and loadout scripts have run (batch 144). The flat-AP composition is the scripts' own
    /// feedback loops — <c>ZhonyasRing</c> keeps 30 percent of the flat pool beside its own share, <c>Ardor</c>
    /// PercentMod of the pool with its own share taken back off — so it is measured on a live hero instead of by
    /// <c>HeroStatCalculator.Calculate</c> over a synthetic inventory. <paramref name="flatAp"/> is one plain buff
    /// payload of flat ability power, the "active" pool the two loops scale.
    /// </summary>
    private static double Live(string champion, int level, int?[] ids, MasteryPage masteries,
        Func<Entity, double> read, double flatAp = 0.0)
    {
        using var match = new Match(champion, Team.Order, ally: true);
        var hero = match.Hero(0);
        hero.Level = level;
        hero.Masteries = masteries;
        var slots = new InventorySlot?[Amberline.Simulation.Inventory.SlotCount];
        for (var i = 0; i < ids.Length && i < slots.Length; i++)
            slots[i] = ids[i] is { } id ? new InventorySlot(id) : null;
        hero.Inventory = slots;
        if (flatAp != 0.0)
            hero.Buffs.Add(new BuffState("flat-ap-stat-test", new BuffPayload { AbilityPower = flatAp }, expiresAt: 100.0));
        // The loadout is on the hero before the loops start reading it back: without this refresh the first samples see a
        // pool that does not have the purchase in it yet and walk up to the multiply from there (ZhonyasRing's own loop
        // contracts by about 0.23 a sample, so three seconds is not enough to erase that start).
        match.Runtime.HeroStats.Refresh(hero);
        // The loops read back through BuffOnUpdateStats on the 0.25 s grid, so their multiply is only on the hero once a
        // few samples have run (same ramp as the attack-speed pool above: 3 s is past it).
        Advance(match, 3.0);
        match.Runtime.HeroStats.Refresh(hero);
        return read(hero);
    }

    [Check("combat.live-loadout-progression-hooks")]
    public static void LiveLoadoutProgressionHooks()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        hero.Masteries = UtilityPage(new MasteryCatalog());
        runtime.HeroStats.Refresh(hero);

        hero.Experience = 0.0;
        TestAssert.True(runtime.Combat.AwardHeroProgress(hero, experience: 100.0));
        TestAssert.Near(105.0, hero.Experience, 1e-9,
            "Awareness applies to production hero XP");

        // Playtest B3 #12 (26 Sept 2026): loadout gold-per-10 now shares the
        // ambient 90-second start gate. Measure just past it (batch 161: ambient
        // income is continuous at 1.3 g/s from the gate — retail footage, the
        // Xerath champion spotlight, October 2011 — so its 0.325 of the quarter
        // second is part of the window and Greed's 0.025 sits beside it).
        var goldBefore = hero.Gold;
        runtime.Combat.CombatTick(1.0, regenerate: false);
        TestAssert.Near(0.0, hero.Gold - goldBefore, 1e-9,
            "Greed waits for the 90-second income gate");
        runtime.Combat.CombatTick(CombatService.PassiveGoldDelay - runtime.Combat.CurrentTime,
            regenerate: false);
        goldBefore = hero.Gold;
        runtime.Combat.CombatTick(0.25, regenerate: false);
        TestAssert.Near(0.025 + 1.3 * 0.25, hero.Gold - goldBefore, 1e-9,
            "Greed is continuous and remains separate from item GP10");

        hero.Alive = false;
        hero.Health = 0.0;
        var now = runtime.Combat.CurrentTime;
        runtime.Combat.ScheduleRespawn(hero);
        TestAssert.Near(now + ProgressionRules.DeathTime(hero.Level, now) * 0.967,
            hero.RespawnAt, 1e-9,
            "Good Hands reduces the authoritative death timer");

        // The three shipped black records are independent loadout inputs:
        // 5366 reduces death time by 5%, 5367 contributes one gold-per-10
        // stat, and 5368 contributes 2% XP. Keep this production path
        // separate from the mastery-only assertions above.
        // 5366 is a Float32 DATA value promoted to this exact double.
        const double rune5366RespawnReduction = 0.05000000074505806;
        hero.Alive = true;
        hero.Health = hero.MaxHealth;
        hero.Runes = RunePage.Create(new RuneCatalog(runtime.GameData.Paths),
            black: new[] { 5366, 5367, 5368 });
        hero.Masteries = MasteryPage.Empty;
        runtime.HeroStats.Refresh(hero);
        TestAssert.Near(1.0, hero.LoadoutGoldPer10, 1e-9,
            "Greed-like rune gold stat is cached separately from item income");
        TestAssert.Near(0.02, hero.LoadoutExperiencePercent, 1e-9,
            "the 5368 rune contributes its shipped XP bonus");
        TestAssert.Near(rune5366RespawnReduction, hero.LoadoutRespawnTimePercent, 1e-12,
            "the 5366 rune reduces death time with the shipped positive modifier");
        hero.Experience = 0.0;
        TestAssert.True(runtime.Combat.AwardHeroProgress(hero, experience: 100.0));
        TestAssert.Near(102.0, hero.Experience, 1e-6,
            "the 5368 rune is applied by the production XP hook");
        var runeGoldBefore = hero.Gold;
        runtime.Combat.CombatTick(0.5, regenerate: false);
        TestAssert.Near(0.05 + 1.3 * 0.5, hero.Gold - runeGoldBefore, 1e-9,
            "rFlatGoldPer10 follows the existing continuous loadout-income path beside the continuous ambient rate");
        hero.Experience = 0.0;
        hero.Alive = false;
        hero.Health = 0.0;
        now = runtime.Combat.CurrentTime;
        hero.RespawnAt = 0.0;
        runtime.Combat.ScheduleRespawn(hero);
        TestAssert.Near(now + ProgressionRules.DeathTime(hero.Level, now) *
            (1.0 - rune5366RespawnReduction),
            hero.RespawnAt, 1e-9,
            "the 5366 rune combines as a positive death-time reduction");
    }

    /// <summary>
    /// The step-1a report and the step-1b live hooks are two independent runs of the same shipped scripts: the report
    /// reads the numbers off a probe world (batch 140 step 1a), the live hooks run the character hooks inside a real hit
    /// (step 1b). This check holds them against each other, so a hook that is dispatched under the wrong name, at the
    /// wrong gate, or with the wrong <c>TalentLevel</c>, shows up as a wrong hit rather than as a plausible number.
    /// </summary>
    [Check("combat.live-loadout-mastery-hooks-match-contribution")]
    public static void LiveLoadoutMasteryHooksMatchTheContributionRecord()
    {
        var catalog = new MasteryCatalog();
        foreach (var (name, page) in new[]
                 {
                     ("defense", DefensivePage(catalog)), ("offense", OffensivePage(catalog)),
                     ("utility", UtilityPage(catalog)), ("havoc", HavocPage(catalog)),
                 })
        {
            var combat = MasteryEffects.Calculate(page, 18).Combat;
            using var match = new Match("Annie", Team.Order, ally: true);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Level = target.Level = 18;
            attacker.Masteries = target.Masteries = page;
            runtime.HeroStats.Refresh(attacker);
            runtime.HeroStats.Refresh(target);
            // The page is bound with the character, not on the first damage it takes (ProductionMatchComposition's
            // HeroCreated runs OwnerFor for exactly that reason), and re-made the first time a hook asks for it after
            // the page or the level changed (BbLoadoutHooks.PageFor). This fixture hands the page in after creation,
            // so that first ask is the first hit below — and the bind installs its live marker buffs (Ardor 111,
            // StrengthOfSpirit 112), whose add runs a hero-stats refresh that takes a pool pinned above the hero's
            // computed maximum back down to it (HeroStatsServices.Refresh's clamp). Bind the way production does
            // first, so the pool the hits measure is the talents' alone.
            runtime.Spells.LoadoutHooks?.OwnerFor(attacker);
            runtime.Spells.LoadoutHooks?.OwnerFor(target);
            target.Health = target.MaxHealth = 100000.0;
            target.Protected = false;

            // A minion source has no mastery of its own, so every change is the target's page. The resistance is the
            // only other factor and it is derived here, not pinned: what is under test is the mastery fields.
            var aiSource = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, Team.Order, target.Position)
            { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0, SpellBlock = 0.0 });
            foreach (var (type, kind) in new[]
                     {
                         (DamageType.Physical, DamageSourceKind.Basic),
                         (DamageType.Physical, DamageSourceKind.Spell),
                         (DamageType.Magic, DamageSourceKind.Spell),
                         (DamageType.True, DamageSourceKind.Spell),
                     })
            {
                target.Health = 10000.0;
                var before = target.Health;
                TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0, type, kind));
                TestAssert.Near(Expected(new MasteryCombatEffects(), combat, 100.0, type, kind,
                        targetIsHero: true, sourceIsAiNonHero: true, targetIsAiNonHero: false,
                        target.Armor, target.SpellBlock),
                    before - target.Health, 1e-8,
                    name + " takes " + type + " " + kind + " from an AI non-hero");
            }

            // The attacker's page alone: a minion target has no masteries and no resistance, so the hit is the
            // attacker's CharOnPreDealDamage and its marker buff's BuffOnHitUnit and nothing else.
            foreach (var (type, kind) in new[]
                     {
                         (DamageType.Physical, DamageSourceKind.Basic),
                         (DamageType.Magic, DamageSourceKind.Spell),
                         (DamageType.True, DamageSourceKind.Basic),
                     })
            {
                var victim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
                { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0, SpellBlock = 0.0 });
                var before = victim.Health;
                TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 100.0, type, kind));
                TestAssert.Near(Expected(combat, new MasteryCombatEffects(), 100.0, type, kind,
                        targetIsHero: false, sourceIsAiNonHero: false, targetIsAiNonHero: true,
                        victim.Armor, victim.SpellBlock),
                    before - victim.Health, 1e-8,
                    name + " deals " + type + " " + kind + " to an AI non-hero");
            }
        }
    }

    /// <summary>
    /// The hand-written mastery application, written as one formula: the attacker's non-true factor and on-hit bonus
    /// before mitigation, the target's flat block before the raw damage is kept, then resistance, then the target's
    /// non-true factor and its AI non-hero block. This is the order the shipped hook names imply and the order
    /// <c>combat.live-loadout-mastery-hooks</c> pins with literals. Resistance is derived here from the target's own
    /// armor and spell block, and true damage takes neither.
    /// </summary>
    private static double Expected(MasteryCombatEffects attacker, MasteryCombatEffects target, double amount,
        DamageType type, DamageSourceKind kind, bool targetIsHero, bool sourceIsAiNonHero, bool targetIsAiNonHero,
        double armor, double spellBlock)
    {
        if (amount <= 0.0) return 0.0;
        if (type != DamageType.True) amount *= attacker.NonTrueDamageDealtMultiplier;
        if (kind == DamageSourceKind.Basic && targetIsAiNonHero)
            amount += attacker.OffensiveMasteryMinionDamageBonus;
        if (targetIsHero && type == DamageType.Physical && kind == DamageSourceKind.Basic)
            amount = Math.Max(0.0, amount - target.HardenSkinPhysicalDamageBlock);
        if (type == DamageType.Physical) amount *= CombatRules.ResistanceMultiplier(armor);
        else if (type == DamageType.Magic) amount *= CombatRules.ResistanceMultiplier(spellBlock);
        if (targetIsHero && type != DamageType.True) amount *= target.NonTrueDamageTakenMultiplier;
        if (targetIsHero && sourceIsAiNonHero)
            amount = Math.Max(0.0, amount - target.NonHeroAiDamageReduction);
        return amount;
    }

    private static MasteryPage DefensivePage(MasteryCatalog catalog) => MasteryPage.Create(catalog,
        new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1, [125] = 1
        });

    private static MasteryPage OffensivePage(MasteryCatalog catalog) => MasteryPage.Create(catalog,
        new Dictionary<int, int>
        {
            [103] = 3, [106] = 3, [102] = 4, [105] = 4,
            [100] = 3, [137] = 2
        });

    private static MasteryPage UtilityPage(MasteryCatalog catalog) => MasteryPage.Create(catalog,
        new Dictionary<int, int>
        {
            [124] = 1, [134] = 1, [132] = 1, [130] = 1,
            [116] = 4, [115] = 4, [117] = 1, [121] = 3,
            [135] = 2, [147] = 1, [119] = 3, [140] = 1,
            [118] = 3, [122] = 1
        });

    private static MasteryPage HavocPage(MasteryCatalog catalog) => MasteryPage.Create(catalog,
        new Dictionary<int, int>
        {
            [103] = 3, [106] = 3, [136] = 1, [126] = 1,
            [102] = 4, [105] = 4, [146] = 1, [101] = 3, [123] = 1
        });
}
