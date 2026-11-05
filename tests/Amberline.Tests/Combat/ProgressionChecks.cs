using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static class ProgressionChecks
{
    [Check("combat.progression.death-timer-boundaries")]
    public static void DeathTimerBoundaries()
    {
        var expectedBase = new[] { 12.0, 14.0, 16.0, 19.0, 22.0, 25.0,
            28.0, 30.0, 32.0, 34.0, 36.0, 38.0, 40.0, 42.0, 44.0,
            46.0, 48.0, 50.0 };
        for (var level = 1; level <= expectedBase.Length; level++)
            TestAssert.Near(expectedBase[level - 1], ProgressionRules.BaseDeathTime(level), 1e-12,
                $"Map1 death time level {level}");

        TestAssert.Near(50.0, ProgressionRules.DeathTime(18, 1499.999), 1e-12);
        TestAssert.Near(50.0, ProgressionRules.DeathTime(18, 1500.0), 1e-12);
        TestAssert.Near(50.0, ProgressionRules.DeathTime(18, 1559.999), 1e-12);
        TestAssert.Near(50.0 * (1.0 + ProgressionRules.DeathTimeScalingIncrease),
            ProgressionRules.DeathTime(18, 1560.0), 1e-12);
        TestAssert.Near(50.0 * Math.Min(ProgressionRules.DeathTimeScalingCap,
            1.0 + 25.0 * ProgressionRules.DeathTimeScalingIncrease),
            ProgressionRules.DeathTime(18, 3000.0), 1e-12);
        TestAssert.Near(75.0, ProgressionRules.DeathTime(18, 3060.0), 1e-12);
        TestAssert.Near(75.0, ProgressionRules.DeathTime(18, 99999.0), 1e-12);
    }

    [Check("combat.progression.bounty-streaks")]
    public static void BountyStreaks()
    {
        TestAssert.Near(300.0, ProgressionRules.HeroBounty(1, 0, false), 1e-9);
        TestAssert.Near(330.0, ProgressionRules.HeroBounty(2, 0, false), 1e-9);
        TestAssert.Near(300.0 * ProgressionRules.MaxKillStreakBonus + 100.0,
            ProgressionRules.HeroBounty(20, 0, true), 1e-12);
        TestAssert.Near(300.0 * Math.Pow(1.0 - ProgressionRules.DeathStreakPenalty, .4),
            ProgressionRules.HeroBounty(1, 1, false), 1e-9);
        TestAssert.Near(400.0, ProgressionRules.HeroBounty(1, 0, true), 1e-9);
        TestAssert.Near(300.0 * ProgressionRules.MinDeathStreakPenalty,
            ProgressionRules.HeroBounty(1, 100, false), 1e-9);
    }

    [Check("combat.progression.assist-window")]
    public static void AssistWindow()
    {
        TestAssert.Near(15.0, CombatService.AssistCreditSeconds, 1e-12,
            "bounty.ini assist duration override");
        TestAssert.Near(.5834, ProgressionRules.GoldPoolForAssist, 1e-12,
            "bounty.ini assist gold pool");
    }

    [Check("combat.progression.shared-unit-xp")]
    public static void SharedUnitExperience()
    {
        TestAssert.Near(100.0, ProgressionRules.SharedUnitExperience(100.0, 1), 1e-12);
        TestAssert.Near(55.0, ProgressionRules.SharedUnitExperience(100.0, 2), 1e-12);
        TestAssert.Near(36.7, ProgressionRules.SharedUnitExperience(100.0, 3), 1e-12); // Mission.inibin rounds 1.10 / 3 to 0.367
        TestAssert.Near(27.5, ProgressionRules.SharedUnitExperience(100.0, 4), 1e-12);
        TestAssert.Near(22.0, ProgressionRules.SharedUnitExperience(100.0, 5), 1e-12);
        TestAssert.Near(0.0, ProgressionRules.SharedUnitExperience(100.0, 0), 1e-12);
        // Playtest B3 #12 (26 Sept 2026): V1.0.0.104 reduces a solo lane-unit
        // kill to 92%; the shared 110% split is unchanged.
        TestAssert.Near(92.0, ProgressionRules.SharedUnitExperience(100.0, 1,
            ProgressionRules.SoloLaneUnitExperience), 1e-12, "solo lane-unit XP");
        TestAssert.Near(55.0, ProgressionRules.SharedUnitExperience(100.0, 2,
            ProgressionRules.SoloLaneUnitExperience), 1e-12, "shared lane-unit XP");
    }

    [Check("combat.progression.champion-xp-rules")]
    public static void ChampionExperienceRules()
    {
        // Playtest B3 #12 (26 Sept 2026): the base is 55% of the victim's
        // next-level requirement (V1.0.0.96, was 50%) and a shared kill adds
        // 10% (V1.0.0.113, was the unsourced 26.1%). Level 18 keeps the
        // synthetic +110 increment: 2150 * 0.55.
        TestAssert.Near(154.0, ProgressionRules.ChampionKillExperienceBase(1), 1e-12);
        TestAssert.Near(214.5, ProgressionRules.ChampionKillExperienceBase(2), 1e-12);
        TestAssert.Near(1122.0, ProgressionRules.ChampionKillExperienceBase(17), 1e-12);
        TestAssert.Near(1182.5, ProgressionRules.ChampionKillExperienceBase(18), 1e-12);
        TestAssert.Near(84.7,
            ProgressionRules.SharedChampionKillExperience(154.0, 2), 1e-12);
        TestAssert.Near(56.4666666666667,
            ProgressionRules.SharedChampionKillExperience(154.0, 3), 1e-12);
        TestAssert.Near(0.0,
            ProgressionRules.SharedChampionKillExperience(154.0, 0), 1e-12);
        TestAssert.Near(154.0,
            ProgressionRules.SharedChampionKillExperience(154.0, 1), 1e-12);
        // Batch 688: V1.0.0.75's S curve on the level delta scales each share (batch 552 had none): even 1, three levels below
        // "negligible" 0.05, three above "almost twice" 1.95, symmetric about 1 (the curve's shape is a Reconstruction).
        TestAssert.Near(1.0, ProgressionRules.ChampionKillLevelDeltaFactor(7, 7), 1e-12);
        TestAssert.Near(0.05, ProgressionRules.ChampionKillLevelDeltaFactor(4, 7), 1e-12);
        TestAssert.Near(1.95, ProgressionRules.ChampionKillLevelDeltaFactor(10, 7), 1e-12);
        for (var delta = 1; delta <= 17; delta++)
        {
            var up = ProgressionRules.ChampionKillLevelDeltaFactor(1 + delta, 1);
            TestAssert.Near(2.0, up + ProgressionRules.ChampionKillLevelDeltaFactor(1, 1 + delta), 1e-12, $"the curve is symmetric at {delta}");
            TestAssert.True(up > ProgressionRules.ChampionKillLevelDeltaFactor(delta, 1) && up < 2.0, $"the curve rises below 2 at {delta}");
        }
    }

}
