using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Tests.Core;

/// <summary>
/// The differential oracle of the mastery conversion (batch 140 step 1a, batch 142 step 1c). <see
/// cref="MasteryEffects.Calculate"/> now reads every number from the shipped <c>DATA/Talents/&lt;id&gt;.luaobj</c>
/// scripts; this file keeps the hand-written tables it replaced, the way <c>BbAnnieDifferentialChecks</c> kept the
/// Annie oracle, and compares the two on the pages the mastery checks pin. A difference is a conversion bug unless it
/// is one of the classified findings.
/// </summary>
public static class MasteryEffectsDifferentialChecks
{
    /// <summary>
    /// The hand-written formulas of <c>MasteryEffects</c> until 20 October 2026, kept verbatim as the oracle. They are
    /// not production code and nothing else may read them.
    /// </summary>
    public static class HandWritten
    {
        private static readonly double[] CritDamageByRank = { 0.0, 0.033, 0.066, 0.1 };
        private static readonly double[] ManaPercentByRank = { 0.0, 0.0125, 0.025, 0.0375, 0.05 };
        private static readonly double[] ManaRegenByRank = { 0.0, 0.2, 0.4, 0.6 };
        private static readonly double[] HpManaRegenPercentByRank = { 0.0, 0.02, 0.03, 0.04 };
        private static readonly double[] ArdorByRank = { 0.0, 0.01333, 0.02667, 0.04 };
        private static readonly double[] StrengthOfSpiritByRank = { 0.0, 0.00066, 0.00133, 0.002 };
        private static readonly double[] HardenSkinByRank = { 0.0, 1.0, 1.5, 2.0 };
        private static readonly double[] HavocByRank = { 1.0, 1.04 };
        private static readonly double[] AiDamageByRank = { 0.0, 1.0, 2.0 };
        private static readonly double[] ExperienceByRank = { 0.0, 0.0125, 0.025, 0.0375, 0.05 };
        private static readonly double[] RespawnByRank = { 0.0, 0.033, 0.066, 0.1 };
        private static readonly double[] NeutralBuffDurationByRank = { 0.0, 0.15, 0.30 };

        public static MasteryContribution Calculate(MasteryPage page, int championLevel = 1)
        {
            if (page.TotalPoints == 0) return MasteryContribution.Empty;

            var modifiers = new Dictionary<string, double>(StringComparer.Ordinal);
            var rank = page.Rank;

            Add(modifiers, "FlatPhysicalDamageMod", rank(100));
            Add(modifiers, "FlatArmorPenetrationMod", rank(101) * 2.0);
            Add(modifiers, "PercentCooldownMod", rank(102) * -0.0075);
            Add(modifiers, "FlatCritChanceMod", rank(103) * 0.0066);
            Add(modifiers, "FlatCritDamageMod", Value(CritDamageByRank, rank(104)));
            Add(modifiers, "PercentAttackSpeedMod", rank(105) * 0.01);
            Add(modifiers, "FlatMagicDamageMod", rank(106) * 0.2 * championLevel);
            Add(modifiers, "PercentMagicPenetrationMod", rank(107) > 0 ? 0.15 : 0.0);
            Add(modifiers, "FlatHPPoolMod", rank(108) * 12.0);
            Add(modifiers, "FlatArmorMod", rank(109) * 2.0);
            Add(modifiers, "FlatSpellBlockMod", rank(110) * 2.0);
            Add(modifiers, "FlatDodgeMod", rank(114) * 0.005);
            Add(modifiers, "PercentMPPoolMod", Value(ManaPercentByRank, rank(115)));
            Add(modifiers, "PercentMovementSpeedMod", rank(119) * 0.01);
            Add(modifiers, "FlatMPRegenMod", Value(ManaRegenByRank, rank(121)));
            Add(modifiers, "PercentCooldownMod", rank(118) * -0.02);
            var regenPercent = Value(HpManaRegenPercentByRank, rank(132));
            Add(modifiers, "PercentMPRegenMod", regenPercent);
            Add(modifiers, "PercentHPRegenMod", regenPercent);

            var combat = new MasteryCombatEffects(
                ArdorAbilityPowerPercent: Value(ArdorByRank, rank(111)),
                ArdorAttackSpeedPercent: Value(ArdorByRank, rank(111)),
                ArdorDurationSeconds: rank(111) > 0 ? 25000.0 : 0.0,
                StrengthOfSpiritHealthRegenPerMaxMana: Value(StrengthOfSpiritByRank, rank(112)),
                StrengthOfSpiritDurationSeconds: rank(112) > 0 ? 25000.0 : 0.0,
                HardenSkinPhysicalDamageBlock: Value(HardenSkinByRank, rank(113)),
                NonTrueDamageDealtMultiplier: Value(HavocByRank, rank(123)),
                NonTrueDamageTakenMultiplier: rank(125) > 0 ? 0.96 : 1.0,
                NonHeroAiDamageReduction: Value(AiDamageByRank, rank(144)),
                NimblenessOnDodge: rank(127) > 0,
                NimblenessDurationSeconds: rank(127) > 0 ? 5.0 : 0.0,
                NimblenessMovementSpeedPercent: rank(127) > 0 ? 0.10 : 0.0,
                NeutralBuffDurationPercent: Value(NeutralBuffDurationByRank, rank(135)),
                MonsterBuffs: rank(135) == 1,
                MonsterBuffs2: rank(135) == 2,
                OffensiveMasteryRank: rank(137),
                OffensiveMasteryMinionDamageBonus: rank(137) * 2.0);

            var summoner = new MasterySummonerEffects(
                SummonerCooldownBonus: rank(120) > 0 ? 0.15 : 0.0,
                ClairvoyanceDurationBonus: rank(122) > 0 ? 4.0 : 0.0,
                ClairvoyanceCooldownBonus: rank(122) > 0 ? 5.0 : 0.0,
                TeleportDelayBonus: rank(124) > 0 ? 0.5 : 0.0,
                TeleportCooldownBonus: rank(124) > 0 ? 5.0 : 0.0,
                PromoteCooldownBonus: rank(124) > 0 ? 30.0 : 0.0,
                SmiteGoldBonus: rank(126) > 0 ? 5.0 : 0.0,
                SmiteCooldownBonus: rank(126) > 0 ? 5.0 : 0.0,
                RevivePreservationBonus: rank(129) > 0 ? 400.0 : 0.0,
                ReviveCooldownBonus: rank(129) > 0 ? 30.0 : 0.0,
                GhostMovementBonus: rank(130) > 0 ? 0.06 : 0.0,
                GhostDurationBonus: rank(130) > 0 ? 1.5 : 0.0,
                BoostCooldownBonus: rank(131) > 0 ? 20.0 : 0.0,
                HealCooldownBonus: rank(133) > 0 ? 30.0 : 0.0,
                ExhaustArmorMod: rank(136) > 0 ? -10.0 : 0.0,
                ExhaustDurationBonus: rank(136) > 0 ? 0.5 : 0.0,
                FlashCooldownBonus: rank(140) > 0 ? 15.0 : 0.0,
                RallyAPMod: rank(143) > 0 ? 70.0 : 0.0,
                RallyDurationBonus: rank(143) > 0 ? 5.0 : 0.0,
                FortifySplashDamage: rank(145) > 0 ? 1.0 : 0.0,
                ClarityManaBonus: rank(147) > 0 ? 1.0 : 0.0,
                BurningEmbersWhenIgniteOnCooldown: rank(146) > 0,
                BurningEmbersAbilityPowerBonus: rank(146) > 0 ? 10.0 : 0.0);

            var economy = new MasteryEconomyEffects(
                ExperiencePercentBonus: Value(ExperienceByRank, rank(116)),
                GoldPer10Bonus: rank(117) > 0 ? 1.0 : 0.0,
                RespawnTimePercent: Value(RespawnByRank, rank(134)));

            return new MasteryContribution(
                new ReadOnlyDictionary<string, double>(modifiers), combat, summoner, economy);
        }

        private static double Value(IReadOnlyList<double> values, int rank) => values[rank];

        private static void Add(IDictionary<string, double> modifiers, string name, double value)
        {
            if (value == 0.0) return;
            modifiers[name] = modifiers.TryGetValue(name, out var previous) ? previous + value : value;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The pages the mastery checks pin, plus one that adds the two the offense page leaves out
    // ------------------------------------------------------------------------------------------------------------

    private static MasteryPage Defense(MasteryCatalog catalog) => MasteryPage.Create(catalog, new Dictionary<int, int>
    {
        [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
        [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
        [145] = 1, [125] = 1
    });

    private static MasteryPage Offense(MasteryCatalog catalog) => MasteryPage.Create(catalog, new Dictionary<int, int>
    {
        [103] = 3, [106] = 3, [126] = 1, [102] = 4, [105] = 4,
        [146] = 1, [101] = 3, [100] = 3, [104] = 3, [107] = 1,
        [123] = 1
    });

    private static MasteryPage Utility(MasteryCatalog catalog) => MasteryPage.Create(catalog, new Dictionary<int, int>
    {
        [124] = 1, [134] = 1, [132] = 1, [130] = 1,
        [116] = 4, [115] = 4, [117] = 1, [121] = 3,
        [135] = 2, [147] = 1, [119] = 3, [140] = 1,
        [118] = 3, [122] = 1
    });

    [Check("core-masteries.effects-summoner-avatar-vars")]
    public static void SummonerFieldsAreTheFeedersAvatarVars()
    {
        // Step 1c: the summoner fields are not a rank table any more, they are the AvatarVars the eleven feeder
        // scripts write in SetVarsByLevel. Pin the write itself, key by key, at the one legal rank.
        var catalog = new MasteryCatalog();
        var all = BbLoadoutReport.Talents(new Dictionary<int, int>
        {
            [120] = 1, [122] = 1, [124] = 1, [126] = 1, [129] = 1, [130] = 1, [131] = 1,
            [133] = 1, [136] = 1, [140] = 1, [143] = 1, [145] = 1, [147] = 1,
        }, 1);

        TestAssert.Near(0.15, all[120].Avatar("SummonerCooldownBonus"), 1e-12, "120");
        TestAssert.Near(4.0, all[122].Avatar("ClairvoyanceDurationBonus"), 1e-12, "122 duration");
        TestAssert.Near(5.0, all[122].Avatar("ClairvoyanceCooldownBonus"), 1e-12, "122 cooldown");
        TestAssert.Near(0.5, all[124].Avatar("TeleportDelayBonus"), 1e-12, "124 delay");
        TestAssert.Near(5.0, all[124].Avatar("TeleportCooldownBonus"), 1e-12, "124 cooldown");
        TestAssert.Near(30.0, all[124].Avatar("PromoteCooldownBonus"), 1e-12, "124 promote");
        TestAssert.Near(5.0, all[126].Avatar("SmiteGoldBonus"), 1e-12, "126 gold");
        TestAssert.Near(5.0, all[126].Avatar("SmiteCooldownBonus"), 1e-12, "126 cooldown");
        TestAssert.Near(400.0, all[129].Avatar("RevivePreservationBonus"), 1e-12, "129 preservation");
        TestAssert.Near(30.0, all[129].Avatar("ReviveCooldownBonus"), 1e-12, "129 cooldown");
        TestAssert.Near(0.06, all[130].Avatar("GhostMovementBonus"), 1e-12, "130 movement");
        TestAssert.Near(1.5, all[130].Avatar("GhostDurationBonus"), 1e-12, "130 duration");
        TestAssert.Near(20.0, all[131].Avatar("BoostCooldownBonus"), 1e-12, "131");
        TestAssert.Near(30.0, all[133].Avatar("HealCooldownBonus"), 1e-12, "133");
        TestAssert.Near(-10.0, all[136].Avatar("ExhaustArmorMod"), 1e-12, "136 armor");
        TestAssert.Near(0.5, all[136].Avatar("ExhaustDurationBonus"), 1e-12, "136 duration");
        TestAssert.Near(15.0, all[140].Avatar("FlashCooldownBonus"), 1e-12, "140");
        TestAssert.Near(70.0, all[143].Avatar("RallyAPMod"), 1e-12, "143 AP");
        TestAssert.Near(5.0, all[143].Avatar("RallyDurationBonus"), 1e-12, "143 duration");
        TestAssert.Near(1.0, all[145].Avatar("FortifySplashDamage"), 1e-12, "145");
        TestAssert.Near(1.0, all[147].Avatar("ManaBonus"), 1e-12, "147");

        // Every feeder is MaxRank 1, so the second entries of the *ByLevel arrays the scripts carry (Clairvoyance's
        // 10, Flash's 30, Revive's 40, Ghost's 3, Rally's 10, Exhaust's 1, Smite's 10, Teleport's 10) are unreachable
        // data: a page can never index them. The hand-written rank table and the scripts therefore agree at every legal
        // rank, which is what the comparison above pins. script = retail.
        foreach (var id in new[] { 120, 122, 124, 126, 129, 130, 131, 133, 136, 140, 143, 145, 146, 147 })
        {
            TestAssert.True(catalog.TryGet(id, out var definition), "catalog knows talent " + id);
            TestAssert.Equal(1, definition!.MaxRank, "talent " + id + " must stay MaxRank 1 for the oracle to hold");
        }
    }

    [Check("core-masteries.effects-script-matches-hand-written")]
    public static void ScriptsAgreeWithTheHandWrittenTables()
    {
        var catalog = new MasteryCatalog();
        foreach (var (name, page) in new[]
                 {
                     ("empty", MasteryPage.Empty),
                     ("offense", Offense(catalog)),
                     ("defense", Defense(catalog)),
                     ("utility", Utility(catalog)),
                 })
        {
            foreach (var level in new[] { 1, 6, 12, 18 })
            {
                var script = MasteryEffects.Calculate(page, level);
                var hand = HandWritten.Calculate(page, level);
                Compare(name + "@" + level, hand, script);
            }
        }

        // A mastery script may only ask the probe world for things the conversion understands. Anything else would
        // have been dropped silently and would show up here rather than as a wrong number.
        TestAssert.Equal(0, BbLoadoutReport.IgnoredNatives.Count,
            "mastery scripts called: " + string.Join(", ", BbLoadoutReport.IgnoredNatives));
    }

    private static void Compare(string where, MasteryContribution expected, MasteryContribution actual)
    {
        TestAssert.Equal(expected.Modifiers.Count, actual.Modifiers.Count, where + ": modifier key count");
        foreach (var (key, value) in expected.Modifiers)
        {
            TestAssert.True(actual.Modifiers.TryGetValue(key, out var found), where + ": missing modifier " + key);
            TestAssert.Near(value, found, 1e-12, where + ": modifier " + key);
        }

        Compare(where, expected.Combat, actual.Combat);
        TestAssert.Equal(expected.Economy, actual.Economy, where + ": economy");
        // The summoner fields now come from the feeders' AvatarVars (step 1c); the oracle keeps the old rank table.
        // Every feeder is MaxRank 1 and SrcValueByLevel is 1-based, so the two agree at every legal rank.
        TestAssert.Equal(expected.Summoner, actual.Summoner, where + ": summoner");
    }

    private static void Compare(string where, MasteryCombatEffects expected, MasteryCombatEffects actual)
    {
        TestAssert.Near(expected.ArdorAbilityPowerPercent, actual.ArdorAbilityPowerPercent, 1e-12, where + ": Ardor AP");
        TestAssert.Near(expected.ArdorAttackSpeedPercent, actual.ArdorAttackSpeedPercent, 1e-12, where + ": Ardor AS");
        TestAssert.Equal(expected.ArdorDurationSeconds, actual.ArdorDurationSeconds, where + ": Ardor duration");
        TestAssert.Near(expected.StrengthOfSpiritHealthRegenPerMaxMana, actual.StrengthOfSpiritHealthRegenPerMaxMana,
            1e-12, where + ": Strength of Spirit");
        TestAssert.Equal(expected.StrengthOfSpiritDurationSeconds, actual.StrengthOfSpiritDurationSeconds,
            where + ": Strength of Spirit duration");
        TestAssert.Near(expected.HardenSkinPhysicalDamageBlock, actual.HardenSkinPhysicalDamageBlock, 1e-12,
            where + ": Harden Skin");
        TestAssert.Near(expected.NonTrueDamageDealtMultiplier, actual.NonTrueDamageDealtMultiplier, 1e-12,
            where + ": Havoc");
        TestAssert.Near(expected.NonTrueDamageTakenMultiplier, actual.NonTrueDamageTakenMultiplier, 1e-12,
            where + ": Tenacity");
        TestAssert.Near(expected.NonHeroAiDamageReduction, actual.NonHeroAiDamageReduction, 1e-12,
            where + ": Defensive");
        TestAssert.Equal(expected.NimblenessOnDodge, actual.NimblenessOnDodge, where + ": Nimbleness on dodge");
        TestAssert.Equal(expected.NimblenessDurationSeconds, actual.NimblenessDurationSeconds,
            where + ": Nimbleness duration");
        TestAssert.Near(expected.NimblenessMovementSpeedPercent, actual.NimblenessMovementSpeedPercent, 1e-12,
            where + ": Nimbleness movement");
        TestAssert.Near(expected.NeutralBuffDurationPercent, actual.NeutralBuffDurationPercent, 1e-12,
            where + ": neutral buff duration");
        TestAssert.Equal(expected.MonsterBuffs, actual.MonsterBuffs, where + ": MonsterBuffs");
        TestAssert.Equal(expected.MonsterBuffs2, actual.MonsterBuffs2, where + ": MonsterBuffs2");
        TestAssert.Equal(expected.OffensiveMasteryRank, actual.OffensiveMasteryRank, where + ": Offensive Mastery rank");
        TestAssert.Near(expected.OffensiveMasteryMinionDamageBonus, actual.OffensiveMasteryMinionDamageBonus, 1e-12,
            where + ": Offensive Mastery bonus");
    }

    [Check("core-masteries.effects-script-damage-probes")]
    public static void DamageHooksLeaveTrueDamageAloneAndKnowTheirOwnShape()
    {
        // Every damage hook of a mastery is gated on the damage type (113 on PHYSICAL, 123 and 125 on not-TRUE, 144 on
        // the attacker's kind), so a true-damage hit must come back untouched, and each hook's own shape must be exact.
        // Havoc (123) is Offense and needs 20 earlier Offense points, so it cannot share the defense page.
        var catalog = new MasteryCatalog();
        var defense = BbLoadoutReport.Talents(Defense(catalog).Allocations, 18);
        var offense = BbLoadoutReport.Talents(Offense(catalog).Allocations, 18);
        var havoc = offense[123];
        var trueHavoc = havoc.Damage("CharOnPreDealDamage", BbDamageProbe.TrueDamage);
        TestAssert.Near(1.0, trueHavoc.Multiplier, 1e-12, "Havoc must not touch true damage (factor)");
        TestAssert.Near(0.0, trueHavoc.Block, 1e-12, "Havoc must not touch true damage (block)");

        foreach (var (id, hook) in new[] { (113, "CharOnBeingHit"), (125, "CharOnPreDamage"), (144, "CharOnPreDamage") })
        {
            var trueDamage = defense[id].Damage(hook, BbDamageProbe.TrueDamage);
            TestAssert.Near(1.0, trueDamage.Multiplier, 1e-12, "talent " + id + " must not touch true damage (factor)");
            TestAssert.Near(0.0, trueDamage.Block, 1e-12, "talent " + id + " must not touch true damage (block)");
        }

        // Each hook's own shape, read on the axis that carries its number: Harden Skin (113) and Defensive (144) are
        // pure flat blocks, so Block is exact; Havoc (123) and Tenacity (125) are pure factors, so Multiplier is.
        TestAssert.Near(2.0, defense[113].Damage("CharOnBeingHit", BbDamageProbe.Physical).Block, 1e-12);
        TestAssert.Near(2.0, defense[144].Damage("CharOnPreDamage", BbDamageProbe.AiNonHero).Block, 1e-12);
        TestAssert.Near(1.04, havoc.Damage("CharOnPreDealDamage", BbDamageProbe.Physical).Multiplier, 1e-12);
        TestAssert.Near(0.96, defense[125].Damage("CharOnPreDamage", BbDamageProbe.Magic).Multiplier, 1e-12);
        // A hook that does not fire leaves the hit alone, and that reads as the neutral (1, 0) on both axes.
        var silent = defense[144].Damage("CharOnPreDamage", BbDamageProbe.Physical);
        TestAssert.Near(1.0, silent.Multiplier, 1e-12, "Defensive only fires against an AI non-hero");
        TestAssert.Near(0.0, silent.Block, 1e-12, "Defensive only fires against an AI non-hero");
    }
}
