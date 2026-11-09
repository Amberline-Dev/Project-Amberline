using System.Globalization;

namespace Amberline.Data;

/// <summary>
/// The client object classes the shipped damage-ratio table distinguishes.
/// Turrets and inhibitors/nexus are both "Building" (obj_AI_Turret and
/// obj_Building return the same dr_*ToBuilding field); every obj_AI_Minion,
/// including pets, wards and jungle monsters, is a "Unit".
/// </summary>
public enum DamageClass
{
    Hero,
    Unit,
    Building,
}

/// <summary>
/// LEVELS/MapN/Constants.var "Damage Ratios" (dr_*). The client copies them in
/// its global character data init, and the hero, minion and turret attack-ratio getters
/// return the attacker-class row for the target's class.
/// </summary>
public sealed record DamageRatios(
    double HeroToHero, double HeroToUnit, double HeroToBuilding,
    double UnitToHero, double UnitToUnit, double UnitToBuilding,
    double BuildingToHero, double BuildingToUnit, double BuildingToBuilding)
{
    public double Ratio(DamageClass attacker, DamageClass target) => (attacker, target) switch
    {
        (DamageClass.Hero, DamageClass.Hero) => HeroToHero,
        (DamageClass.Hero, DamageClass.Unit) => HeroToUnit,
        (DamageClass.Hero, DamageClass.Building) => HeroToBuilding,
        (DamageClass.Unit, DamageClass.Hero) => UnitToHero,
        (DamageClass.Unit, DamageClass.Unit) => UnitToUnit,
        (DamageClass.Unit, DamageClass.Building) => UnitToBuilding,
        (DamageClass.Building, DamageClass.Hero) => BuildingToHero,
        (DamageClass.Building, DamageClass.Unit) => BuildingToUnit,
        (DamageClass.Building, DamageClass.Building) => BuildingToBuilding,
        _ => throw new ArgumentOutOfRangeException(nameof(attacker)),
    };
}

/// <summary>
/// LEVELS/MapN/Constants.var "ai_Target*" and "ai_MinionTargetingHeroBoost": the cost terms and the attacker cap of the
/// native minion target selector. The stock client only initialises them (the selector, FindTargetInAcR, runs in the
/// server), so the shipped file is the only evidence of what the selector weighs.
/// </summary>
public sealed record TargetCostConstants(
    double PerNeighbor, double PerAttacker, double RangeFactor, double PathFactor, double HeroBoost, int MaxAttackers);

/// <summary>Numeric "name = value ; comment" entries of a shipped map Constants.var.</summary>
public sealed class MapConstants
{
    private readonly Dictionary<string, double> _values;

    private MapConstants(string path, Dictionary<string, double> values)
    {
        _values = values;
        DamageRatios = new DamageRatios(
            Required(path, "dr_HeroToHero"), Required(path, "dr_HeroToUnit"),
            Required(path, "dr_HeroToBuilding"),
            Required(path, "dr_UnitToHero"), Required(path, "dr_UnitToUnit"),
            Required(path, "dr_UnitToBuilding"),
            Required(path, "dr_BuildingToHero"), Required(path, "dr_BuildingToUnit"),
            Required(path, "dr_BuildingToBuilding"));
        TargetCost = new TargetCostConstants(
            Required(path, "ai_TargetDistanceFactorPerNeightbor"),
            Required(path, "ai_TargetDistanceFactorPerAttacker"),
            Required(path, "ai_TargetRangeFactor"), Required(path, "ai_TargetPathFactor"),
            Required(path, "ai_MinionTargetingHeroBoost"),
            (int)Required(path, "ai_TargetMaxNumAttackers"));
        AttackSpeedMultiplierFloor = 1.0 + Required(path, "gcd_PercentAttackSpeedModMinimum");
    }

    public DamageRatios DamageRatios { get; }

    /// <summary>
    /// The lowest value an attack-speed multiplier can take: <c>1 + gcd_PercentAttackSpeedModMinimum</c> (-0.95 shipped, so 0.05). The one
    /// floor every unit class reads (heroes, minions, monsters, pets, turrets). The stock client only copies the constant into its global
    /// character data, so applying it to the effective multiplier is the server's reading of "the lowest Attack Speed Percent Mod penalty
    /// can go" (Reconstruction: placed on the final multiplier, before the 0.4 to 5 s period clamp).
    /// </summary>
    public double AttackSpeedMultiplierFloor { get; }

    public TargetCostConstants TargetCost { get; }

    public IReadOnlyDictionary<string, double> Values => _values;

    /// <summary>Loads the patch-126 Summoner's Rift (Map1) constants.</summary>
    public static MapConstants LoadMap1(GameDataPaths? paths = null) =>
        Load(paths ?? GameDataPaths.Default, "Map1");

    public static MapConstants Load(GameDataPaths paths, string mapName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.Under("LEVELS", mapName, "Constants.var");
        if (!File.Exists(path))
            throw new FileNotFoundException("missing map constants", path);

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var comment = line.IndexOf(';');
            var valueLine = comment < 0 ? line : line[..comment];
            var separator = valueLine.IndexOf('=');
            if (separator < 0) continue;
            // Non-numeric entries (none ship today) are not constants this layer owns.
            if (double.TryParse(valueLine[(separator + 1)..].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                values[valueLine[..separator].Trim()] = number;
        }
        // A key the level's file leaves out takes the client's CVar initialiser (the other client build's default): Map3 and Map4 ship no ai_AmbientXP* keys, and Amount 0 pays no ambient XP. A key the file sets keeps its value.
        foreach (var (name, value) in ClientDefaults) values.TryAdd(name, value);
        return new MapConstants(path, values);
    }

    private static readonly (string Name, double Value)[] ClientDefaults =
    [
        ("ai_AmbientXPDelay", 0.0), ("ai_AmbientXPInterval", 5.0), ("ai_AmbientXPAmount", 0.0), ("ai_DisableAmbientXPWhileDead", 0.0),
    ];

    private double Required(string path, string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"map constants do not define {name}: {path}");
}
