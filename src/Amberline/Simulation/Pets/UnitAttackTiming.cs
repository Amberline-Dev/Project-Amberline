using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The attack timing of a unit a script spawns (Tibbers, the voidlings, Heimerdinger's turrets): the character record's own delay offsets over the
/// 1.6 second base period, as the hand-written Annie gave Tibbers. The patch-126 CharacterData loader ignores the legacy raw
/// <c>Data*AttackSpeed</c> field and initialises the live multiplier to one, so the unit keeps the record's delay offsets and nothing else.
/// </summary>
public static class UnitAttackTiming
{
    public static void Apply(Entity unit, ChampionData data)
    {
        var basePeriod = 1.6 * (1.0 + Raw(data, "Data*AttackDelayOffsetPercent"));
        var period = Math.Clamp(basePeriod, 0.4, 5.0);
        var castFraction = Math.Max(0.0, 0.3 + Raw(data, "Data*AttackDelayCastOffsetPercent"));
        unit.AttackSpeedMod = 1.0;
        unit.AttackPeriod = period;
        unit.AttackWindup = period * castFraction;
        unit.AttackTimingBaseline = new AttackTimingBaseline(1.0, basePeriod, castFraction);
        unit.BasicAttackAnimationProbabilities = data.BasicAttackAnimationProbabilities;
    }

    private static double Raw(ChampionData data, string key) =>
        data.Raw.TryGetValue(InibinReader.KeyHash(key), out var value) && value.IsNumeric ? value.AsDouble() : 0.0;
}
