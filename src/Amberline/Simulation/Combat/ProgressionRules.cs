namespace Amberline.Simulation.Combat;

/// <summary>
/// Patch 1.0.0.126 progression constants recovered from the shipped native
/// client and its Map1 DeathTimes data.
/// </summary>
public static class ProgressionRules
{
    public const double HeroBaseBounty = 300.0;
    public const double KillStreakBonus = 0.10;
    public const int MinKillsForStreak = 2;
    // These three values are Float32 entries in bounty.inibin. Keep their
    // promoted values so the x87 bounty path sees the same constants as the
    // native client rather than rounded source literals.
    public const double MaxKillStreakBonus = 1.6670000553131104;
    public const double DeathStreakPenalty = 0.20999999344348907;
    public const int MinDeathsForStreak = 2;
    public const double MinDeathStreakPenalty = 0.05000000074505806;
    public const double FirstBloodBonus = 100.0;
    public const double GoldPoolForAssist = 0.5834;
    public const double SharedUnitExperiencePool = 1.10;
    // V1.0.0.104: "Solo minion kill experience reduced to 92%". Applies to a
    // single lane-unit recipient only; shared kills keep the 110% split and
    // neutral monsters keep their full solo reward.
    public const double SoloLaneUnitExperience = 0.92;
    // Patch notes (League wiki, Experience (champion) history and the V1.0.0.x pages): V1.0.0.96 "Base champion kill experience
    // reduced to 55% from 60% of level up experience"; V1.0.0.120 (22 June 2011) "Experience granted for kills is now determined by
    // the dying champion's level". The first level-difference modifier is V1.0.0.138 (18 April 2012, "modified by 8% per level of
    // difference" from two levels), after 1.0.0.126, so a 126 kill pays every recipient the same share of the victim's base (batch
    // 552 removed the invented 10%-per-level, 50..150% adjustment). No shipped key holds a champion XP value (ExpCurve, Mission,
    // bounty.inibin, Constants.var) and the client's award path is a stub. V1.0.0.113 (General, Experience): "When more than 1
    // champion is sharing experience, 10% extra experience is split among all targets"; the wiki's Experience history files that
    // line under minion kills, the patch note itself names no unit type.
    public const double ChampionKillExperienceFraction = 0.55;
    public const double SharedChampionExperiencePool = 1.10;

    // Map1/DeathTimes.inibin, TimeDeadPerLevel*Level01..Level18.
    private static readonly double[] DeathTimesByLevel =
        [12, 14, 16, 19, 22, 25, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50];

    public const double DeathTimeScalingStart = 1500.0;
    public const double DeathTimeScalingIncrement = 60.0;
    public const double DeathTimeScalingIncrease = 0.019999999552965164;
    public const double DeathTimeScalingCap = 1.5;

    /// <summary>
    /// The bounty.inibin values HeroBounty reads. SummonersRiftBounty holds the [Global] values the constants above name.
    /// MapProgressionRules reads the [ODIN] section for The Crystal Scar (map 8, batch 498).
    /// </summary>
    public readonly record struct BountyValues(double BaseGold, double FirstBloodBonus, double KillStreakBonus,
        double MaxKillStreakBonus, int MinKillsForStreak, double DeathStreakPenalty, int MinDeathsForStreak,
        double MinDeathStreakPenalty);

    public static readonly BountyValues SummonersRiftBounty = new(HeroBaseBounty, FirstBloodBonus, KillStreakBonus,
        MaxKillStreakBonus, MinKillsForStreak, DeathStreakPenalty, MinDeathsForStreak, MinDeathStreakPenalty);

    /// <summary>Computes the hero bounty with the Summoner's Rift values.</summary>
    public static double HeroBounty(int victimKillStreak, int victimDeathStreak, bool firstBlood) =>
        HeroBounty(SummonersRiftBounty, victimKillStreak, victimDeathStreak, firstBlood);

    /// <summary>
    /// Computes the hero bounty with the given bounty values. The native routine applies the streak
    /// multiplier first and adds FirstBloodBonus afterwards.
    /// </summary>
    public static double HeroBounty(BountyValues bounty, int victimKillStreak, int victimDeathStreak, bool firstBlood)
    {
        var multiplier = 1.0;
        if (victimKillStreak >= bounty.MinKillsForStreak)
        {
            multiplier = Math.Min(bounty.MaxKillStreakBonus,
                Math.Pow(1.0 + bounty.KillStreakBonus,
                    victimKillStreak - bounty.MinKillsForStreak + 1));
        }
        else if (victimDeathStreak + 1 >= bounty.MinDeathsForStreak)
        {
            // The exponent is (deathStreak + 1 + 0.4 - MinDeathsForStreak). The 0.4 is the native tuning constant,
            // retained here instead of being rounded away.
            multiplier = Math.Max(bounty.MinDeathStreakPenalty,
                Math.Pow(1.0 - bounty.DeathStreakPenalty,
                    victimDeathStreak + 1.4 - bounty.MinDeathsForStreak));
        }

        return bounty.BaseGold * multiplier + (firstBlood ? bounty.FirstBloodBonus : 0.0);
    }

    /// <summary>Returns the Map1 base death time for a champion level.</summary>
    public static double BaseDeathTime(int level)
    {
        var index = Math.Clamp(level, 1, DeathTimesByLevel.Length) - 1;
        return DeathTimesByLevel[index];
    }

    /// <summary>
    /// Returns the native DeathTimerManager value at mission elapsed time.
    /// Scaling begins strictly after StartTime and is floored per increment.
    /// </summary>
    public static double DeathTime(int level, double missionElapsedSeconds)
    {
        var baseTime = BaseDeathTime(level);
        if (!double.IsFinite(missionElapsedSeconds) ||
            missionElapsedSeconds <= DeathTimeScalingStart)
            return baseTime;

        var steps = Math.Floor((missionElapsedSeconds - DeathTimeScalingStart) /
                               DeathTimeScalingIncrement);
        var multiplier = Math.Min(DeathTimeScalingCap,
            1.0 + steps * DeathTimeScalingIncrease);
        return baseTime * multiplier;
    }

    // LEVELS/Map1/Mission.inibin, the five consecutive keys 0x286CC4F6..0x286CC4FA (float segment, the key names are server-only and
    // unrecovered): 0.92, 0.55, 0.367, 0.275, 0.22. They are the 92% solo rule and the 110% pool split over two, three, four and five
    // recipients; the file rounds the three-way share to 0.367 where 1.10 / 3 is 0.36667 (A10 audit, 18 October 2026). Index = recipients - 1.
    public static readonly double[] SharedUnitExperienceShares = [0.92, 0.55, 0.367, 0.275, 0.22];

    /// <summary>
    /// Patch 1.0.0.113's shared-unit XP rule: one recipient receives the base
    /// reward scaled by <paramref name="soloMultiplier"/> (lane units pass
    /// SoloLaneUnitExperience); two to five eligible recipients each take the
    /// Mission.inibin share (the 110% pool split evenly, three ways 0.367).
    /// </summary>
    public static double SharedUnitExperience(double reward, int recipients,
        double soloMultiplier = 1.0)
    {
        if (!double.IsFinite(reward) || reward < 0.0)
            throw new ArgumentOutOfRangeException(nameof(reward));
        if (recipients <= 0) return 0.0;
        if (recipients == 1) return reward * soloMultiplier;
        return reward * (recipients <= SharedUnitExperienceShares.Length
            ? SharedUnitExperienceShares[recipients - 1]
            : SharedUnitExperiencePool / recipients);
    }

    /// <summary>
    /// Returns the champion-kill XP base for the victim's current level:
    /// 55% of the victim's next-level requirement (V1.0.0.96, V1.0.0.120).
    /// Reconstruction: a level-18 victim has no next level in ExpCurve; it keeps
    /// the curve's +110 step past the last threshold.
    /// </summary>
    public static double ChampionKillExperienceBase(int victimLevel) =>
        ExperienceToNextLevel(victimLevel) * ChampionKillExperienceFraction;

    /// <summary>Returns the cumulative Map1 XP threshold for a champion level.</summary>
    public static double ExperienceThreshold(int level) =>
        LevelExperience[Math.Clamp(level, 1, 18) - 1];

    /// <summary>Returns the XP increment from a level to its next level.</summary>
    public static double ExperienceToNextLevel(int level)
    {
        var current = Math.Clamp(level, 1, 18);
        return current < 18
            ? ExperienceThreshold(current + 1) - ExperienceThreshold(current)
            : (LevelExperience[^1] - LevelExperience[^2]) + 110.0;
    }

    /// <summary>
    /// Splits a champion-kill XP pool over a snapshotted recipient set: one recipient takes the base, two or more split 110% evenly
    /// (V1.0.0.113). Every recipient gets the same share whatever its level (no level-difference modifier before V1.0.0.138).
    /// </summary>
    public static double SharedChampionKillExperience(double baseExperience, int recipients)
    {
        if (!double.IsFinite(baseExperience) || baseExperience < 0.0)
            throw new ArgumentOutOfRangeException(nameof(baseExperience));
        if (recipients <= 0) return 0.0;
        return baseExperience * (recipients == 1 ? 1.0 : SharedChampionExperiencePool) /
            recipients;
    }

    private static readonly double[] LevelExperience =
        // Cumulative Map1/ExpCurve.inibin thresholds for levels 1..18.
        [0, 280, 670, 1170, 1780, 2500, 3330, 4270, 5320, 6480,
         7750, 9130, 10620, 12220, 13930, 15750, 17680, 19720];
}
