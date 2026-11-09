using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Combat;

/// <summary>
/// The progression tables one map ships (batch 454): <c>LEVELS/MapN/DeathTimes.inibin</c> (the death timer per level and the
/// scaling), <c>ExpCurve.inibin</c> (the XP thresholds) and <c>Mission.inibin</c> (the shared-unit XP shares), and the kill bounty of
/// <c>DATA/Globals/bounty.inibin</c> (batch 498: its [ODIN] section on map 8). The match reads its own
/// map through this. <see cref="ProgressionRules"/> keeps the Summoner's Rift values for the tests and tools that have no map. Maps 1
/// and 2 ship the same numbers in these files, so their values do not change.
/// </summary>
public sealed class MapProgressionRules
{
    /// <summary>The ExpCurve.inibin keys for the thresholds of levels 2..18, in level order (names not recovered; Map1's values confirm it).</summary>
    private static readonly uint[] ExperienceKeys =
    [
        0x315B8ABB, 0x315B8ABC, 0x315B8ABD, 0x315B8ABE, 0x315B8ABF, 0x315B8AC0, 0x315B8AC1, 0x315B8AC2,
        0xB04123F6, 0xB04123F7, 0xB04123F8, 0xB04123F9, 0xB04123FA, 0xB04123FB, 0xB04123FC, 0xB04123FD, 0xB04123FE,
    ];

    /// <summary>Mission.inibin keys 0x286CC4F6..FA: the solo factor, then the shares for two to five recipients.</summary>
    private static readonly uint[] SharedShareKeys = [0x286CC4F6, 0x286CC4F7, 0x286CC4F8, 0x286CC4F9, 0x286CC4FA];

    private readonly double[] _deathTimes;
    private readonly double[] _thresholds;
    private readonly double[] _shares;
    private readonly ProgressionRules.BountyValues _bounty;

    private MapProgressionRules(double[] deathTimes, double scalingStart, double scalingIncrement, double scalingIncrease,
        double scalingCap, double waveRespawnInterval, bool allowDeathTimeMods, double[] thresholds, double[] shares,
        ProgressionRules.BountyValues bounty)
    {
        _deathTimes = deathTimes;
        ScalingStart = scalingStart;
        ScalingIncrement = scalingIncrement;
        ScalingIncrease = scalingIncrease;
        ScalingCap = scalingCap;
        WaveRespawnInterval = waveRespawnInterval;
        AllowDeathTimeMods = allowDeathTimeMods;
        _thresholds = thresholds;
        _shares = shares;
        _bounty = bounty;
    }

    /// <summary>DeathTimes.inibin DeathTimeScaling*StartTime; 0 turns the scaling off (the client's GetDeathTime skips it).</summary>
    public double ScalingStart { get; }
    public double ScalingIncrement { get; }
    public double ScalingIncrease { get; }
    public double ScalingCap { get; }

    /// <summary>
    /// Batch 584: DeathTimes.inibin DeathWaveRespawn*WaveRespawnInterval, read by the client's loader with the
    /// default 0. Only map 8 ships it (12); 0 means no respawn waves.
    /// </summary>
    public double WaveRespawnInterval { get; }

    /// <summary>DeathTimes.inibin AllowDeathTimeMods; absent means on, as Summoner's Rift (which does not ship the key).</summary>
    public bool AllowDeathTimeMods { get; }

    /// <summary>The solo factor of a lane unit's kill XP (Mission.inibin, or the Summoner's Rift value when the map ships none).</summary>
    public double SoloLaneUnitExperience => _shares[0];

    /// <summary>
    /// Loads the tables of the map <paramref name="paths"/> names. Missing death-time, scaling and curve keys throw; missing share
    /// keys fall back to the Summoner's Rift shares (Reconstruction: the wiki and the shipped files do not give Dominion's shares).
    /// </summary>
    public static MapProgressionRules Load(GameDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var deathPath = paths.Under("LEVELS", paths.MapName, "DeathTimes.inibin");
        var expPath = paths.Under("LEVELS", paths.MapName, "ExpCurve.inibin");
        var missionPath = paths.Under("LEVELS", paths.MapName, "Mission.inibin");
        foreach (var file in new[] { deathPath, expPath })
            if (!File.Exists(file)) throw new FileNotFoundException("missing map progression data", file);

        var death = InibinReader.Load(deathPath);
        var deathTimes = new double[18];
        for (var index = 0; index < deathTimes.Length; index++)
            deathTimes[index] = Required(death, deathPath, KeyHash($"TimeDeadPerLevel*Level{index + 1:00}"));
        var allowMods = !death.TryGetValue(KeyHash("AllowDeathTimeMods"), out var mods) || mods.AsClientNumber() != 0.0;

        var exp = InibinReader.Load(expPath);
        var thresholds = new double[18];
        for (var index = 0; index < ExperienceKeys.Length; index++)
        {
            if (!exp.TryGetValue(ExperienceKeys[index], out var value))
                throw new InvalidDataException($"ExpCurve.inibin does not define level {index + 2}: {expPath}");
            thresholds[index + 1] = value.AsClientNumber();
        }

        // Mission.inibin's shares are float32 entries (0.9200000166893005 for 0.92); rounding to the three decimals the file is
        // written with gives the exact double the Summoner's Rift table uses, so Summoner's Rift keeps its values.
        var shares = (double[])ProgressionRules.SharedUnitExperienceShares.Clone();
        if (File.Exists(missionPath))
        {
            var mission = InibinReader.Load(missionPath);
            for (var index = 0; index < SharedShareKeys.Length; index++)
                if (mission.TryGetValue(SharedShareKeys[index], out var value))
                    shares[index] = Math.Round(value.AsClientNumber(), 3, MidpointRounding.AwayFromZero);
        }

        // Batch 498: the kill bounty is the [ODIN] section on The Crystal Scar (Map8: BaseGold 100, KillStreakBonus .3, MaxKillStreakBonus 4,
        // MinDeathStreakPenalty .3); the other maps keep the Summoner's Rift [Global] values.
        var bounty = paths.MapName == GameDataPaths.MapFolder(GameMaps.CrystalScar)
            ? LoadBounty(paths, "ODIN")
            : ProgressionRules.SummonersRiftBounty;

        return new MapProgressionRules(
            deathTimes,
            Required(death, deathPath, KeyHash("DeathTimeScaling*StartTime")),
            Required(death, deathPath, KeyHash("DeathTimeScaling*IncrementTime")),
            Required(death, deathPath, KeyHash("DeathTimeScaling*PercentIncrease")),
            Required(death, deathPath, KeyHash("DeathTimeScaling*PercentCap")),
            death.TryGetValue(KeyHash("DeathWaveRespawn*WaveRespawnInterval"), out var wave) ? wave.AsClientNumber() : 0.0,
            allowMods,
            thresholds,
            shares,
            bounty);
    }

    /// <summary>
    /// The kill-bounty values of one section of <c>DATA/Globals/bounty.inibin</c>. Every key the section needs must be present
    /// (the ODIN section holds all eight).
    /// </summary>
    private static ProgressionRules.BountyValues LoadBounty(GameDataPaths paths, string section)
    {
        var path = paths.Data("Globals/bounty.inibin");
        var data = InibinReader.Load(path);
        double Get(string name) => Required(data, path, KeyHash($"{section}*{name}"));
        return new ProgressionRules.BountyValues(
            Get("BaseGold"), Get("FirstBloodBonus"), Get("KillStreakBonus"), Get("MaxKillStreakBonus"),
            (int)Get("MinKillsForStreak"), Get("DeathStreakPenalty"), (int)Get("MinDeathsForStreak"), Get("MinDeathStreakPenalty"));
    }

    /// <summary>The base death time for a level (1 to 18; out-of-range levels clamp, as the native lookup does).</summary>
    public double BaseDeathTime(int level) => _deathTimes[Math.Clamp(level, 1, _deathTimes.Length) - 1];

    /// <summary>
    /// The death timer at mission elapsed time <paramref name="missionElapsedSeconds"/>: the base for the level, multiplied by the
    /// scaling once the scaling is on (StartTime not 0) and past StartTime. Scaling is floored per increment and capped.
    /// </summary>
    public double DeathTime(int level, double missionElapsedSeconds)
    {
        var baseTime = BaseDeathTime(level);
        if (ScalingStart == 0.0 || ScalingIncrement <= 0.0 || !double.IsFinite(missionElapsedSeconds) ||
            missionElapsedSeconds <= ScalingStart)
            return baseTime;
        var steps = Math.Floor((missionElapsedSeconds - ScalingStart) / ScalingIncrement);
        return baseTime * Math.Min(ScalingCap, 1.0 + steps * ScalingIncrease);
    }

    /// <summary>
    /// Batch 584: the death timer with the respawn waves of the death timer manager (
    /// one manager per team). With an interval above 1e-5,
    /// a death more than the interval after the team's wave start (+0x60, 0 at first) starts a new wave now; the timer is the scaled
    /// base less the time since the wave start, so every death of one wave respawns at the same moment (map 8: 26 s for the
    /// first, down to 14 s for one 12 s later). <paramref name="waveStart"/> is the team's wave start, updated in place.
    /// </summary>
    public double DeathTime(int level, double missionElapsedSeconds, ref double waveStart)
    {
        var deathTime = DeathTime(level, missionElapsedSeconds);
        if (WaveRespawnInterval <= 1e-5) return deathTime;
        if (missionElapsedSeconds - WaveRespawnInterval > waveStart) waveStart = missionElapsedSeconds;
        return deathTime - (missionElapsedSeconds - waveStart);
    }

    /// <summary>The cumulative XP threshold for a level (level 1 is 0).</summary>
    public double ExperienceThreshold(int level) => _thresholds[Math.Clamp(level, 1, 18) - 1];

    /// <summary>The XP increment from a level to the next; level 18 adds the synthetic 110 past the last step (as ProgressionRules).</summary>
    public double ExperienceToNextLevel(int level)
    {
        var current = Math.Clamp(level, 1, 18);
        return current < 18
            ? ExperienceThreshold(current + 1) - ExperienceThreshold(current)
            : (_thresholds[17] - _thresholds[16]) + 110.0;
    }

    /// <summary>The champion-kill XP base for the victim's level (55% of its next-level increment; ProgressionRules).</summary>
    public double ChampionKillExperienceBase(int victimLevel) =>
        ExperienceToNextLevel(victimLevel) * ProgressionRules.ChampionKillExperienceFraction;

    /// <summary>ProgressionRules.HeroBounty with this map's bounty.inibin values (the [ODIN] section on The Crystal Scar).</summary>
    public double HeroBounty(int victimKillStreak, int victimDeathStreak, bool firstBlood) =>
        ProgressionRules.HeroBounty(_bounty, victimKillStreak, victimDeathStreak, firstBlood);

    /// <summary>ProgressionRules.SharedUnitExperience with this map's shares (index recipients - 1, two to five recipients).</summary>
    public double SharedUnitExperience(double reward, int recipients, double soloMultiplier = 1.0)
    {
        if (!double.IsFinite(reward) || reward < 0.0)
            throw new ArgumentOutOfRangeException(nameof(reward));
        if (recipients <= 0) return 0.0;
        if (recipients == 1) return reward * soloMultiplier;
        return reward * (recipients <= _shares.Length
            ? _shares[recipients - 1]
            : ProgressionRules.SharedUnitExperiencePool / recipients);
    }

    private static uint KeyHash(string name) => Amberline.Data.InibinReader.KeyHash(name);

    private static double Required(InibinData data, string path, uint key) =>
        data.TryGetValue(key, out var value)
            ? value.AsClientNumber()
            : throw new InvalidDataException($"map progression data does not define key 0x{key:X8}: {path}");
}
