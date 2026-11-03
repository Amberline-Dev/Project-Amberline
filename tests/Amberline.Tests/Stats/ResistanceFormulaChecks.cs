using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 340 item 2 (#274/#275): the armor and magic resist stage against the shared code. <c>ComputeAbsorbFactor</c>
/// (the shared spell helper) is |R| / (|R| + 100), negative below zero; the damage taken is 1 minus it, which is the
/// engine's multiplier. The percent penetration natives (the armor and magic ones) clamp to [0, 1] and store
/// (1 - p) * current, so sources compose as remaining resistance.
/// </summary>
public static class ResistanceFormulaChecks
{
    private static float AbsorbFactor(float resistance)
    {
        var factor = Math.Abs(resistance) / (Math.Abs(resistance) + 100.0f);
        return resistance >= 0.0f ? factor : -factor;
    }

    [Check("combat.resistance-multiplier-is-one-minus-the-shared-absorb-factor")]
    public static void ResistanceMultiplierIsTheSharedAbsorbFactor()
    {
        foreach (var resistance in new[] { -60.0, -20.0, 0.0, 15.0, 30.0, 100.0, 250.0 })
            TestAssert.Near(1.0 - AbsorbFactor((float)resistance), CombatRules.ResistanceMultiplier(resistance), 1e-6,
                $"resistance {resistance}: damage taken is 1 - ComputeAbsorbFactor");
    }

    [Check("stats.percent-armor-penetration-composes-as-remaining-armor")]
    public static void PercentArmorPenetrationComposesAsRemainingArmor()
    {
        var itemCatalog = new ItemCatalog(GameData.Default.Paths);
        HeroStats Calculate(params double[] percents) => HeroStatCalculator.Calculate(GameData.Default, itemCatalog,
            new HeroStatsInput("Ashe", 6, new InventorySlot?[] { null, null, null, null, null, null },
                percents.Select((value, index) => new BuffState($"pen-{index}", new BuffPayload { ArmorPenPercent = value }, expiresAt: 100.0))
                    .ToArray()));
        TestAssert.Near(0.4, Calculate(0.4).ArmorPenPercent, 1e-9, "one source is its own share");
        TestAssert.Near(1.0 - 0.6 * 0.9, Calculate(0.4, 0.1).ArmorPenPercent, 1e-9,
            "Last Whisper's 40 and a 10 percent source leave 54 percent of the armor ((1 - p) * current), not 50");
        TestAssert.Near(1.0, Calculate(0.7, 0.6).ArmorPenPercent, 1e-9 + 1.0 - (1.0 - 0.3 * 0.4),
            "two large sources never pass all the armor");
        TestAssert.Near(1.0 - 0.3 * 0.4, Calculate(0.7, 0.6).ArmorPenPercent, 1e-9, "and compose the same way");
        TestAssert.Near(1.0, Calculate(1.5).ArmorPenPercent, 1e-9, "a value above one is clamped to all of it");
    }
}
