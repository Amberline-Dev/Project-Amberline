using Amberline.Core;

namespace Amberline.Simulation.Minions;

/// <summary>Data-driven Map1 values for the four shipped minion classes.</summary>
public sealed record MinionStatDefinition(
    MinionType Type,
    string CharacterName,
    double MaxHealth,
    double AttackDamage,
    double AttackRange,
    double Armor,
    double SpellBlock,
    double HealthRegen,
    double AttackSpeedMod,
    double AttackPeriod,
    double AcquisitionRange,
    double MissileSpeed,
    double AttackWindup,
    double MoveSpeed,
    double GoldReward,
    double ExperienceReward,
    double Radius);

/// <summary>
/// The four shipped minion characters' base stats and the constants the script never owns.
/// Runtime state such as destroyed inhibitors is read from an explicit <see cref="WorldState"/>
/// supplied by the caller; this type has no process-global match state. Wave composition, the
/// wave interval, the stagger and **every economy number** — gold and XP, the 180 s upgrade
/// curve, the gold caps and the inhibitor bonuses — are not decided here: Map1's shipped
/// <c>LevelScript.luaobj</c> answers them through <see cref="Ai.LevelScriptHost"/>, and
/// <see cref="Ai.LevelEconomyRow.StatsOf"/> lays them over these base stats.
/// </summary>
public static class MinionDefinitions
{
    public const double FirstWaveSeconds = 90.0;
    public const double WaveIntervalSeconds = 30.0;
    public const double MinionStaggerSeconds = 0.8;
    public const double TargetScanPeriodSeconds = 0.25;
    public const double MaxEngageDistance = 2500.0;
    public const double MinionAggroRadius = 450.0;
    public const double SiegeRetryPeriodSeconds = 1.0;
    public const double SeparationTargetRatio = 1.5;
    public const uint EnemyNodeId = 0x40;
    public const int MaxMinionsEver = 180;

    // GoldReward/ExperienceReward here are the character base the script's *GoldGiven /
    // *ExpGiven replace at spawn; LevelScript owns the live values.
    private static readonly IReadOnlyDictionary<MinionType, MinionStatDefinition> _stats =
        new Dictionary<MinionType, MinionStatDefinition>
        {
            [MinionType.Melee] = new(MinionType.Melee, "Minion_Basic", 445.0, 11.0,
                110.0, 0.0, 0.0, 0.0, 1.0, 0.8, 750.0, 0.0, 0.33336, 325.0,
                22.0, 64.0, 35.7437),
            [MinionType.Caster] = new(MinionType.Caster, "Minion_Wizard", 280.0, 23.0,
                550.0, 0.0, 0.0, 0.0, 1.0, 1.4928, 700.0, 650.0, 0.46082736, 325.0,
                16.0, 32.0, 35.7437),
            [MinionType.Siege] = new(MinionType.Siege, "Minion_MechCannon", 700.0, 40.0,
                300.0, 15.0, 0.0, 0.0, 1.0, 1.0, 750.0, 1200.0, 0.345, 325.0,
                27.0, 100.0, 55.7437),
            [MinionType.Super] = new(MinionType.Super, "Minion_MechMelee", 1500.0, 180.0,
                170.0, 30.0, -30.0, 10.0, 1.0, 1.44, 600.0, 0.0, 0.554976, 325.0,
                27.0, 100.0, 55.5208),
        };

    public static IReadOnlyDictionary<MinionType, MinionStatDefinition> Stats => _stats;

    // The table contains Order defaults. Stock character loading
    // and inherits base timing for attack slots without overrides.
    // All eight lane characters therefore use period * (0.3 + castOffset),
    // including the Chaos-specific melee, cannon and super windups.
    public static MinionStatDefinition BaseStats(MinionType type, Team team = Team.Order)
    {
        if (!_stats.TryGetValue(type, out var result))
            throw new ArgumentOutOfRangeException(nameof(type));
        return team switch
        {
            Team.Order => result,
            Team.Chaos => type switch
            {
                MinionType.Melee => result with { AttackWindup = 0.345336 },
                MinionType.Siege => result with { AttackRange = 280.0, AttackWindup = 0.55 },
                MinionType.Super => result with { AttackWindup = 0.560016 },
                _ => result,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(team)),
        };
    }

    /// <summary>Counts currently live, scheduled lane minions for one team.</summary>
    public static int CurrentLiveTeamMinionCount(WorldState? world, Team team)
    {
        if (team is not (Team.Order or Team.Chaos))
            throw new ArgumentOutOfRangeException(nameof(team));
        if (world is null) return 0;
        return world.Entities.Values.Count(entity =>
            entity.Alive && entity.Team == team && entity.Kind == EntityKind.Minion &&
            entity.UnitType == UnitType.Minion && entity.MinionType is not null &&
            entity.MinionLane is not null &&
            MapDefinitions.Lanes.Contains(entity.MinionLane, StringComparer.Ordinal));
    }

    public static bool IsInhibitorDown(Team team, string lane, WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateLane(lane);
        var prefix = team == Team.Order ? "Barracks_T1_" : "Barracks_T2_";
        var name = prefix + lane + "1";
        return world.Entities.Values.Any(entity => entity.Kind == EntityKind.Inhibitor &&
            string.Equals(entity.Name, name, StringComparison.Ordinal) && !entity.Alive);
    }

    public static Team Opposing(Team team) => team switch
    {
        Team.Order => Team.Chaos,
        Team.Chaos => Team.Order,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    public static string CharacterName(MinionType type, Team team) =>
        (team == Team.Order ? "Blue_" : "Red_") + BaseStats(type).CharacterName;

    private static void ValidateLane(string lane)
    {
        if (!MapDefinitions.Lanes.Contains(lane, StringComparer.Ordinal))
            throw new ArgumentException($"The match's map has no lane '{lane}'.", nameof(lane));
    }
}
