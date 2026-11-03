using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Stats;

public readonly record struct HeroAttackTiming(
    double Period,
    double Windup,
    double MissileSpeed)
{
    // Kept internal so the public three-field timing value remains source
    // compatible. Applying timing persists the unbounded values needed by
    // clone/pet consumers without making For/ForRefresh mutate an Entity.
    internal double EffectiveAttackSpeed { get; init; }
    internal double RawPeriod { get; init; }
    internal double CastFraction { get; init; }
}

/// <summary>
/// Shared patch-126 basic-attack timing used at spawn and stat refresh.
/// </summary>
public static class HeroAttackTimings
{
    // Map1 Constants.var; the stock helpers.
    private const double BasePeriod = 1.6;
    private const double BaseCastFraction = 0.3;
    private const double FallbackMissileSpeed = 1200.0;

    public static HeroAttackTiming For(Entity hero, GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(gameData);
        if (hero.UnitType != UnitType.Hero)
            throw new ArgumentException("Attack timing requires a hero entity.", nameof(hero));

        var dataName = CharacterName(hero);
        var attackSpeed = hero.AttackSpeedMod;
        if (!double.IsFinite(attackSpeed) || attackSpeed <= 0.0)
            throw new InvalidOperationException("Hero attack speed must be positive and finite before timing.");

        var champion = gameData.Champion(dataName);
        var rawPeriod = BasePeriod * (1.0 + Number(champion.Raw, "Data*AttackDelayOffsetPercent"));
        var period = rawPeriod / attackSpeed;
        if (!double.IsFinite(period) || period <= 0.0)
            throw new InvalidDataException($"Invalid attack period for {hero.ChampionName}.");
        period = Math.Clamp(period, 0.4, 5.0);
        var castFraction = Math.Max(0.0,
            BaseCastFraction + Number(champion.Raw, "Data*AttackDelayCastOffsetPercent"));
        var windup = period * castFraction;
        // Transformed Nidalee keeps the ordinary basic missile; her alternate
        // character data supplies the attack period and cast fraction.
        // The client flies a missile only for CastType 1, 2 and 3 (the cast builder: case 0 runs the effect at once), so the
        // MissileSpeed of a CastType 0 attack spell is unused data. 27 melee champions ship one (Xin Zhao, Jarvan IV, Wukong and Pantheon 20, Udyr
        // and Evelynn 467 ...): read as a flight, Xin Zhao's swing at 120 units landed six seconds after it started (19 October 2026, S-xinzhao).
        // The override path (BbNativeHandlers.AttackMissileSpeed) reads the same rule.
        var attackSpell = gameData.Spell(hero.ChampionName + "BasicAttack");
        var missile = BbChampionModule.CastType(attackSpell) is 1 or 2 or 3 ? attackSpell.MissileSpeed : 0.0;

        if (!double.IsFinite(period) || period <= 0.0 || !double.IsFinite(windup) || windup < 0.0 ||
            !double.IsFinite(missile) || missile < 0.0)
            throw new InvalidDataException($"Invalid attack timing for {hero.ChampionName}.");
        return new HeroAttackTiming(period, windup, missile)
        {
            EffectiveAttackSpeed = attackSpeed,
            RawPeriod = rawPeriod,
            CastFraction = castFraction
        };
    }

    public static HeroAttackTiming Apply(Entity hero, GameData gameData)
    {
        var timing = For(hero, gameData);
        hero.AttackPeriod = timing.Period;
        hero.AttackWindup = timing.Windup;
        hero.MissileSpeed = timing.MissileSpeed;
        hero.AttackTimingBaseline = new AttackTimingBaseline(
            timing.EffectiveAttackSpeed, timing.RawPeriod, timing.CastFraction);
        ApplyAnimationProbabilities(hero, gameData);
        return timing;
    }

    /// <summary>
    /// Refresh uses the same data and limits as spawn, preserving a runtime
    /// missile-speed override such as one supplied by a captured snapshot.
    /// </summary>
    public static HeroAttackTiming ForRefresh(Entity hero, GameData gameData)
    {
        return For(hero, gameData) with { MissileSpeed = ExistingMissile(hero) };
    }

    public static HeroAttackTiming ApplyRefresh(Entity hero, GameData gameData)
    {
        var timing = ForRefresh(hero, gameData);
        hero.AttackPeriod = timing.Period;
        hero.AttackWindup = timing.Windup;
        hero.MissileSpeed = timing.MissileSpeed;
        hero.AttackTimingBaseline = new AttackTimingBaseline(
            timing.EffectiveAttackSpeed, timing.RawPeriod, timing.CastFraction);
        ApplyAnimationProbabilities(hero, gameData);
        return timing;
    }

    private static void ApplyAnimationProbabilities(Entity hero, GameData gameData)
        => hero.BasicAttackAnimationProbabilities = gameData.Champion(CharacterName(hero)).BasicAttackAnimationProbabilities;

    private static string CharacterName(Entity hero)
        => hero.StatCharacterName;

    private static double Number(InibinData data, string key)
        => data.TryGetValue(InibinReader.KeyHash(key), out var value) && value.IsNumeric
            ? value.AsDouble()
            : 0.0;

    private static double ExistingMissile(Entity hero) =>
        double.IsFinite(hero.MissileSpeed) && hero.MissileSpeed >= 0.0
            ? hero.MissileSpeed : FallbackMissileSpeed;
}
