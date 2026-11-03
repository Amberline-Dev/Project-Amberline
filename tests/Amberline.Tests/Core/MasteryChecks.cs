using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Tests.Core;

public static class MasteryChecks
{
    [Check("data.masteries.catalog")]
    public static void CatalogReadsShippedTree()
    {
        var catalog = new MasteryCatalog();
        TestAssert.Equal(43, catalog.Definitions.Count);
        TestAssert.Equal(
            new[] { 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
                114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 129,
                130, 131, 132, 133, 134, 135, 136, 137, 140, 143, 144, 145, 146, 147 },
            catalog.Definitions.Select(definition => definition.Id).ToArray());
        TestAssert.Equal(14, catalog.Definitions.Count(definition => definition.GroupId == 1));
        TestAssert.Equal(14, catalog.Definitions.Count(definition => definition.GroupId == 2));
        TestAssert.Equal(15, catalog.Definitions.Count(definition => definition.GroupId == 3));

        var sorcery = catalog.Get(102);
        TestAssert.Equal("Sorcery", sorcery.DisplayName);
        TestAssert.Equal(1, sorcery.GroupId);
        TestAssert.Equal(2, sorcery.Row);
        TestAssert.Equal(4, sorcery.MaxRank);
        TestAssert.Equal(4, sorcery.LevelDescriptions.Count);

        var archaicKnowledge = catalog.Get(107);
        TestAssert.Equal(102, archaicKnowledge.PrerequisiteId);
        TestAssert.Equal(3, archaicKnowledge.Row);
        TestAssert.True(archaicKnowledge.LevelDescriptions[0].Contains("15%", StringComparison.Ordinal));
        TestAssert.True(catalog.TryGet(120, out var presence),
            "allocatable summoner capstone must be present");
        TestAssert.Equal(3, presence!.GroupId);
        TestAssert.True(catalog.TryGet(139, out _) == false,
            "script-only Promote mastery must not be exposed");
        TestAssert.True(catalog.TryGet(148, out _) == false,
            "script-only Stifle mastery must not be exposed");
        TestAssert.Equal(4, catalog.Get(115).MaxRank);
        TestAssert.Equal(1, catalog.Get(123).MaxRank);
        TestAssert.Equal(2, catalog.Get(137).MaxRank);
    }

    [Check("core-masteries.page-validation")]
    public static void PageRejectsInvalidAllocationsAndAcceptsEmpty()
    {
        var catalog = new MasteryCatalog();
        var empty = MasteryPage.Create(catalog);
        TestAssert.Equal(0, empty.TotalPoints);
        TestAssert.Equal(0, empty.Allocations.Count);

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [999] = 1 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = -1 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 4 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 3, [136] = 1, [126] = 1, [106] = 3,
                [102] = 4, [105] = 4, [146] = 1, [107] = 1, [101] = 3, [137] = 2,
                [100] = 3, [104] = 3, [143] = 1, [123] = 1 }));

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [102] = 1 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 3, [106] = 3, [136] = 1, [126] = 1, [107] = 1 }));

        var valid = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 3, [106] = 3, [136] = 1, [126] = 1, [102] = 4, [107] = 1 });
        TestAssert.Equal(13, valid.TotalPoints);
        TestAssert.Equal(1, valid.Rank(107));
        TestAssert.Equal(0, valid.Rank(100));
        TestAssert.True(MasteryPage.TryCreate(catalog, valid.Allocations, out _, out _));

        var source = new Dictionary<int, int> { [103] = 1 };
        var isolated = MasteryPage.Create(catalog, source);
        source[103] = 3;
        source[106] = 3;
        TestAssert.Equal(1, isolated.Rank(103));
        TestAssert.Equal(0, isolated.Rank(106));
    }

    [Check("core-masteries.prerequisite-ranks")]
    public static void PageRequiresFullPrerequisiteRanks()
    {
        var catalog = new MasteryCatalog();

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 3, [136] = 1, [126] = 1, [106] = 3,
                [102] = 1, [107] = 1 }));
        var archaic = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 3, [136] = 1, [126] = 1, [106] = 3,
                [102] = 4, [107] = 1 });
        TestAssert.Equal(1, archaic.Rank(107));

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [133] = 1, [110] = 1, [129] = 1, [109] = 3, [112] = 1 }));
        var spirit = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [133] = 1, [110] = 3, [129] = 1, [109] = 3, [112] = 1 });
        TestAssert.Equal(1, spirit.Rank(112));

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [133] = 1, [110] = 3, [129] = 1, [109] = 3, [114] = 1, [127] = 1 }));
        var nimbleness = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [133] = 1, [110] = 3, [129] = 1, [109] = 3, [114] = 4, [127] = 1 });
        TestAssert.Equal(1, nimbleness.Rank(127));

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [133] = 1, [110] = 1, [129] = 1, [109] = 1, [114] = 4, [113] = 1 }));
        var hardened = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [133] = 1, [110] = 1, [129] = 1, [109] = 3, [114] = 2, [113] = 1 });
        TestAssert.Equal(1, hardened.Rank(113));
    }

    [Check("core-masteries.summoner-level-validation")]
    public static void PageValidatesSummonerLevelSeparately()
    {
        var catalog = new MasteryCatalog();
        TestAssert.Throws<ArgumentOutOfRangeException>(() => MasteryPage.Create(catalog, summonerLevel: 0));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [103] = 2 }, summonerLevel: 1));
        TestAssert.True(MasteryPage.TryCreate(catalog, null, out var empty, out var error, summonerLevel: 1));
        TestAssert.Equal(0, empty.TotalPoints);
        TestAssert.True(error is null);
    }

    [Check("core-masteries.effects-zero")]
    public static void EffectsKeepEmptyPageCompatible()
    {
        var effects = MasteryEffects.Calculate(MasteryPage.Empty, championLevel: 18);
        TestAssert.True(effects.IsEmpty);
        TestAssert.Equal(0, effects.Modifiers.Count);
        TestAssert.Equal(1.0, effects.Combat.NonTrueDamageDealtMultiplier);
        TestAssert.Equal(1.0, effects.Combat.NonTrueDamageTakenMultiplier);
        TestAssert.Equal(0.0, effects.Summoner.SmiteGoldBonus);
        TestAssert.Equal(0.0, effects.Economy.ExperiencePercentBonus);
    }

    [Check("core-masteries.effects-stat-formulas")]
    public static void EffectsUseShippedStatFormulas()
    {
        var catalog = new MasteryCatalog();
        var offense = MasteryPage.Create(catalog, new Dictionary<int, int>
        {
            [103] = 3, [106] = 3, [126] = 1, [102] = 4, [105] = 4,
            [146] = 1, [101] = 3, [100] = 3, [104] = 3
        });
        var effects = MasteryEffects.Calculate(offense, championLevel: 18);

        TestAssert.Near(3.0, effects.Modifiers["FlatPhysicalDamageMod"], 1e-12);
        TestAssert.Near(6.0, effects.Modifiers["FlatArmorPenetrationMod"], 1e-12);
        TestAssert.Near(-0.03, effects.Modifiers["PercentCooldownMod"], 1e-12);
        TestAssert.Near(0.0198, effects.Modifiers["FlatCritChanceMod"], 1e-12);
        TestAssert.Near(0.1, effects.Modifiers["FlatCritDamageMod"], 1e-12);
        TestAssert.Near(0.04, effects.Modifiers["PercentAttackSpeedMod"], 1e-12);
        TestAssert.Near(10.8, effects.Modifiers["FlatMagicDamageMod"], 1e-12);
        TestAssert.False(effects.Modifiers.ContainsKey("PercentMagicPenetrationMod"));
        TestAssert.True(effects.Summoner.BurningEmbersWhenIgniteOnCooldown);
        TestAssert.Equal(10.0, effects.Summoner.BurningEmbersAbilityPowerBonus);
    }

    [Check("core-masteries.effects-combat-hooks")]
    public static void EffectsExposeCombatHooksWithoutApplyingThem()
    {
        var catalog = new MasteryCatalog();
        var defense = MasteryPage.Create(catalog, new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1, [125] = 1
        });
        var effects = MasteryEffects.Calculate(defense);

        TestAssert.Near(6.0, effects.Modifiers["FlatArmorMod"], 1e-12);
        TestAssert.Near(6.0, effects.Modifiers["FlatSpellBlockMod"], 1e-12);
        TestAssert.Near(0.02, effects.Modifiers["FlatDodgeMod"], 1e-12);
        TestAssert.Near(0.04, effects.Combat.ArdorAbilityPowerPercent, 1e-12);
        TestAssert.Near(0.04, effects.Combat.ArdorAttackSpeedPercent, 1e-12);
        // Talents/111.luaobj BBSpellBuffAdd Duration is 25000 (a permanent buff).
        TestAssert.Equal(25000.0, effects.Combat.ArdorDurationSeconds);
        TestAssert.Equal(25000.0, effects.Combat.StrengthOfSpiritDurationSeconds);
        TestAssert.Near(0.002, effects.Combat.StrengthOfSpiritHealthRegenPerMaxMana, 1e-12);
        TestAssert.Near(2.0, effects.Combat.HardenSkinPhysicalDamageBlock, 1e-12);
        TestAssert.Equal(5.0, effects.Combat.NimblenessDurationSeconds);
        TestAssert.Near(0.10, effects.Combat.NimblenessMovementSpeedPercent, 1e-12);
        TestAssert.True(effects.Combat.NimblenessOnDodge);
        TestAssert.Near(0.96, effects.Combat.NonTrueDamageTakenMultiplier, 1e-12);
        TestAssert.Near(2.0, effects.Combat.NonHeroAiDamageReduction, 1e-12);
        TestAssert.Equal(1.0, effects.Summoner.FortifySplashDamage);
    }

    /// <summary>
    /// Batch 551: Evasion's dodge is a <c>BBIncStat IncFlatDodgeMod</c> (Talents/114.luaobj), the helper that stores
    /// <c>(1 - delta) x current</c>, so it composes with a script buff's dodge as
    /// <c>1 - (1 - 0.02)(1 - 0.20) = 0.216</c>, not the 0.22 sum it was added to.
    /// </summary>
    [Check("core-masteries.evasion-composes-with-script-dodge")]
    public static void EvasionComposesWithScriptDodge()
    {
        var defense = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1, [125] = 1
        });
        var noItems = new Amberline.Simulation.InventorySlot?[] { null, null, null, null, null, null };
        var buff = new Amberline.Core.Buffs.BuffState("dodge-source",
            new Amberline.Core.Buffs.BuffPayload { Dodge = 0.20 }, expiresAt: 100.0);
        double Dodge(MasteryPage page, bool withBuff) => Amberline.Simulation.Stats.HeroStatCalculator.Calculate(
            GameData.Default, new ItemCatalog(GameData.Default.Paths),
            new Amberline.Simulation.Stats.HeroStatsInput("Annie", 6, noItems,
                withBuff ? [buff] : null, masteries: page)).Dodge;
        TestAssert.Near(0.02, Dodge(defense, withBuff: false), 1e-12, "Evasion alone");
        TestAssert.Near(0.20, Dodge(MasteryPage.Empty, withBuff: true), 1e-12, "the buff alone");
        TestAssert.Near(1.0 - 0.98 * 0.80, Dodge(defense, withBuff: true), 1e-12,
            "Evasion and the buff compose as remaining chance");
    }

    [Check("core-masteries.effects-summoner-economy")]
    public static void EffectsExposeSummonerAndEconomyValues()
    {
        var catalog = new MasteryCatalog();
        var utility = MasteryPage.Create(catalog, new Dictionary<int, int>
        {
            [124] = 1, [134] = 1, [132] = 1, [130] = 1,
            [116] = 4, [115] = 4, [117] = 1, [121] = 3,
            [135] = 2, [147] = 1, [119] = 3, [140] = 1,
            [118] = 3, [122] = 1
        });
        var effects = MasteryEffects.Calculate(utility);

        TestAssert.Near(0.05, effects.Economy.ExperiencePercentBonus, 1e-12);
        TestAssert.Equal(1.0, effects.Economy.GoldPer10Bonus);
        TestAssert.Near(0.033, effects.Economy.RespawnTimePercent, 1e-12);
        TestAssert.Near(-0.06, effects.Modifiers["PercentCooldownMod"], 1e-12);
        TestAssert.Near(0.03, effects.Modifiers["PercentMovementSpeedMod"], 1e-12);
        TestAssert.Near(0.6, effects.Modifiers["FlatMPRegenMod"], 1e-12);
        TestAssert.Near(0.02, effects.Modifiers["PercentMPRegenMod"], 1e-12);
        TestAssert.Near(0.05, effects.Modifiers["PercentMPPoolMod"], 1e-12);
        TestAssert.Equal(4.0, effects.Summoner.ClairvoyanceDurationBonus);
        TestAssert.Equal(5.0, effects.Summoner.ClairvoyanceCooldownBonus);
        TestAssert.Equal(0.5, effects.Summoner.TeleportDelayBonus);
        TestAssert.Equal(5.0, effects.Summoner.TeleportCooldownBonus);
        TestAssert.Equal(30.0, effects.Summoner.PromoteCooldownBonus);
        TestAssert.Equal(15.0, effects.Summoner.FlashCooldownBonus);
        TestAssert.Equal(0.06, effects.Summoner.GhostMovementBonus);
        TestAssert.Near(0.30, effects.Combat.NeutralBuffDurationPercent, 1e-12);
        TestAssert.True(effects.Combat.MonsterBuffs2);
    }
}
