using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Amberline.Data;

namespace Amberline.Core;

public enum TurretTier
{
    Outer,
    Inner,
    Inhibitor,
    Nexus,
    Fountain,
}

public sealed record TurretDefinition(
    string Name,
    Team Team,
    Position2 Position,
    TurretTier Tier);

public sealed record InhibitorDefinition(
    string Name,
    Team Team,
    Position2 Position,
    double MaxHealth,
    // ObjectCFG PathfindingCollisionRadius: the circle the client stamps into its nav grid when it creates the inhibitor.
    double Radius,
    uint NetId,
    // Batch 599: the attack radius, the bounds of the section's SkinName1 skin ((X extent + Z extent) / 4).
    double AttackRadius,
    // Retail data (Summoner's Rift): ObjectCFG mBaseStaticHPRegen 15 for all six Barracks_* sections, per second.
    double HealthRegen = 15.0);

public sealed record NexusDefinition(
    string Name,
    Team Team,
    Position2 Position,
    double MaxHealth,
    // The attack radius: the bounds of the HQ's SkinName1 skin, (X extent + Z extent) / 4 (batch 599: read from the skin).
    double Radius,
    uint NetId,
    // ObjectCFG PathfindingCollisionRadius: the circle the client stamps into its nav grid when it creates the nexus.
    double PathfindingRadius,
    // Retail data (Summoner's Rift): ObjectCFG mBaseStaticHPRegen 20 for HQ_T1 and HQ_T2, per second. Batch 447: a Dominion nexus has 0.
    double HealthRegen = 20.0);

public readonly record struct LaneSpawnApproach(int Head, int Tail);

/// <summary>
/// The match map's structures and spawns (<see cref="Layout"/>, read from the player's map files, <see cref="MapLayout"/>), the engine
/// constants navigation uses, and Summoner's Rift's layout under the names the checks and tools use.
/// </summary>
public static class MapDefinitions
{
    public const double CollisionRadius = 65.0;
    public const double TurretPathStampRadius = 260.0;
    public const double TurretClientStampRadius = 140.0;
    public const double HeroSpawnY = 190.0;

    /// <summary>Batch 447: the Dominion nexus crystal, wiki (Crystal Scar, Gameplay): "a starting health of 500".</summary>
    public const double CrystalScarNexusMaxHealth = 500.0;

    /// <summary>
    /// A turret's body radius: Summoner's Rift's Order outer turret character's <c>GameplayCollisionRadius</c> (batch 599: read from its
    /// inibin; every shipped turret character carries the same float, 88.4). The spawn gives each turret its own character's.
    /// </summary>
    public static double TurretRadius => Data.GameData.Default.Champion(SummonersRift.TurretCharacter(TurretTier.Outer, Team.Order)).CombatRadius;

    /// <summary>The match map's HQ attack radius (its skin's bounds, <see cref="NexusDefinition.Radius"/>; both teams' skins give the same).</summary>
    public static double NexusRadius => Layout.Nexuses[0].Radius;

    /// <summary>Summoner's Rift's inhibitor attack radius (its skin's bounds, <see cref="InhibitorDefinition.AttackRadius"/>).</summary>
    public static double InhibitorRadius => SummonersRift.Inhibitors[0].AttackRadius;

    /// <summary>
    /// Summoner's Rift's layout (LEVELS/Map1, <see cref="MapLayout.For"/>) whatever map the match plays: the source of the Summoner's Rift
    /// names below.
    /// </summary>
    public static MapLayout SummonersRift => MapLayout.For(Data.GameData.Default.Paths.ForMap(GameMaps.Normal));

    /// <summary>Summoner's Rift's HQ_T1 maximum health (Map1 ObjectCFG <c>mMaxHP</c>).</summary>
    public static double NexusMaxHealth => SummonersRift.Nexuses[0].MaxHealth;

    /// <summary>Summoner's Rift's HQ_T1 and HQ_T2 stamp radii (Map1 ObjectCFG <c>PathfindingCollisionRadius</c>).</summary>
    public static double OrderNexusPathfindingRadius => SummonersRift.Nexuses.Single(nexus => nexus.Team == Team.Order).PathfindingRadius;

    public static double ChaosNexusPathfindingRadius => SummonersRift.Nexuses.Single(nexus => nexus.Team == Team.Chaos).PathfindingRadius;

    // Owner decision, 18 October 2026: the Order spawn/respawn anchor is the exact central point of
    // LEVELS/Map1/Scene/__Spawn_T1.SCB (float32 values widened), no longer the point rounded to whole units.
    public static Position3 HeroSpawn => new(SummonersRift.OrderSpawn.X, HeroSpawnY, SummonersRift.OrderSpawn.Z);

    // DATA/Characters/HeroSpawnOffsets.inibin, Order1..5 / Chaos1..5, as the stock client reads them:
    // the float32 offsets and signed Facing degrees. Order1*Pos1 is the only entry stored as a byte vector, which the client reads divided by
    // ten, so a one-man Order team spawns 10 units off the fountain centre, where the client draws its spawn beam; the other 29 positions are
    // float vectors. InibinValue.AsClientVector is the decode that session.hero-spawn-table-matches-client-decode pins. Batch 595: read from
    // the player's file (it was a table copied from it).
    private static readonly ConcurrentDictionary<string, InibinData> HeroSpawnOffsets = new(StringComparer.Ordinal);

    /// <summary>
    /// The team's spawn location (the central point of the match map's <c>Scene/__Spawn_T1.SCB</c> / <c>__Spawn_T2.SCB</c>, the anchor
    /// <see cref="InitialHeroSpawn"/> offsets its slots from): where <c>BBTeleportToKeyLocation Location=SPAWN_LOCATION</c> sends a
    /// unit. Only the two playing teams have one.
    /// </summary>
    public static Position2 TeamSpawn(Team team) => Layout.TeamSpawn(team);

    /// <summary>
    /// The team's shop object (<c>orderShop01</c>, <c>chaosShop01</c>; the position the shop reveal sends): the central point of the map's
    /// <c>Scene/orderShop01.SCB</c> / <c>chaosShop01.SCB</c> (<see cref="MapLayout"/>). Batch 460: The Crystal Scar's
    /// own (LEVELS/Map8/Scene; the client reads the .SCB before the .SCO, so the .sco CentralPoint is not used).
    /// </summary>
    public static Position2 ShopPosition(Team team) => Layout.ShopPosition(team);

    /// <summary>
    /// The team's shop object's name: the name its scene mesh carries, the client's object name and netid (<c>orderShop01</c> on Summoner's
    /// Rift; <c>OrderShop01</c> and <c>chaosShop01</c> on Map4, batch 704).
    /// </summary>
    public static string ShopName(Team team) => Layout.ShopName(team);

    /// <summary>
    /// The structure layout of the match's map (<see cref="MapLayout.For"/>, chosen by the map id of the process default,
    /// <see cref="Data.GameData.Default"/>, which a server or headless match configures once for its <c>--map-id</c>).
    /// </summary>
    public static MapLayout Layout
    {
        get
        {
            // The turret rules ask per unit and tick: remember the last default's answer instead of building the cache key each time.
            var paths = Data.GameData.Default.Paths;
            var last = _layout;
            if (last is null || !ReferenceEquals(last.Item1, paths)) _layout = last = Tuple.Create(paths, MapLayout.For(paths));
            return last.Item2;
        }
    }

    private static Tuple<Data.GameDataPaths, MapLayout>? _layout;

    public static (Position2 Position, Position2 Facing) InitialHeroSpawn(
        Team team, int teamSize, int spawnPositionIndex)
    {
        if (teamSize is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(teamSize));
        if (spawnPositionIndex < 0 || spawnPositionIndex >= teamSize)
            throw new ArgumentOutOfRangeException(nameof(spawnPositionIndex));
        if (team is not (Team.Order or Team.Chaos))
            throw new ArgumentOutOfRangeException(nameof(team));
        var paths = Data.GameData.Default.Paths;
        var offsets = HeroSpawnOffsets.GetOrAdd(paths.Root, _ => InibinReader.Load(paths, "Characters/HeroSpawnOffsets.inibin"));
        var section = $"{team}{teamSize}*";
        var slot = offsets[InibinReader.KeyHash($"{section}Pos{spawnPositionIndex + 1}")].AsClientVector();
        // The client's spawn-facing lookup reads Facing<slot> through the float getter with default 0.
        var facing = offsets.TryGetValue(InibinReader.KeyHash($"{section}Facing{spawnPositionIndex + 1}"), out var value)
            ? value.AsClientNumber()
            : 0.0;
        // Batch 447: the anchor is the team's spawn on this map (TeamSpawn), so the Crystal Scar's first heroes start at its own bases.
        var anchor = TeamSpawn(team);
        // The client's spawn-facing lookup rotates (1,0,0) around Y.
        var radians = facing * Math.PI / 180.0;
        return (new Position2(anchor.X + slot[0], anchor.Z + slot[2]),
            new Position2(Math.Cos(radians), -Math.Sin(radians)));
    }

    /// <summary>
    /// Computes the lane spine clearance from the radii supplied by the
    /// loaded minion data; runtime movement should pass its data-backed radius lookup.
    /// </summary>
    public static double MidLaneMinClearanceFromData(Func<MinionType, double> radiusFor)
    {
        ArgumentNullException.ThrowIfNull(radiusFor);
        var melee = radiusFor(MinionType.Melee);
        var siege = radiusFor(MinionType.Siege);
        if (!double.IsFinite(melee) || !double.IsFinite(siege) || melee < 0.0 || siege < 0.0)
            throw new ArgumentOutOfRangeException(nameof(radiusFor), "Minion radii must be finite and non-negative");
        return melee + siege;
    }

    /// <summary>
    /// Summoner's Rift's turret positions by script name, in the turret order (<see cref="SummonersRift"/>). The stock client puts a building
    /// at its mesh's CentralPoint (the building's creation) and the AI turret copies the parent's position
    /// (the client's child-turret creation); the turret spawn packet carries no position.
    /// </summary>
    public static IReadOnlyDictionary<string, Position2> TurretPositions => SummonersRift.TurretPositions;

    /// <summary>
    /// The <c>CreateChildTurret</c> tower id to the tier the engine's turret rules use:
    /// 3 <c>FRONT_TOWER</c> → Outer, 2 <c>MIDDLE_TOWER</c> → Inner, 1 <c>BACK_TOWER</c> →
    /// Inhibitor, 4 <c>HQ_TOWER2</c> and 5 <c>HQ_TOWER1</c> → Nexus, 0 the two fountain shrines →
    /// Fountain. The ids are CreateLevelProps.luaobj's own argument values.
    /// </summary>
    public static TurretTier TurretTierOf(int towerId) => towerId switch
    {
        3 => TurretTier.Outer,
        2 => TurretTier.Inner,
        1 => TurretTier.Inhibitor,
        4 or 5 => TurretTier.Nexus,
        0 => TurretTier.Fountain,
        _ => throw new ArgumentOutOfRangeException(nameof(towerId)),
    };

    /// <summary>The tier of each turret the match map's <c>CreateLevelProps()</c> created, by its script name.</summary>
    public static IReadOnlyDictionary<string, TurretTier> TurretTiers => Layout.TurretTiers;

    /// <summary>
    /// The character the match map's <c>CreateLevelProps()</c> gives every turret of one tier on one team (Summoner's Rift's ten pairs
    /// are each named by exactly one character in the shipped script).
    /// </summary>
    public static string TurretCharacter(TurretTier tier, Team team) => Layout.TurretCharacter(tier, team);

    public static IReadOnlyList<TurretDefinition> Turrets => Layout.Turrets;

    /// <summary>
    /// Summoner's Rift's structure points under their vision names: the HQ and barracks positions (<see cref="SummonersRift"/>) and the Order
    /// fountain, the exact __Spawn_T1 point, the same as <see cref="HeroSpawn"/> (owner decision, 18 October 2026).
    /// </summary>
    public static IReadOnlyDictionary<string, Position2> StructureVisionPositions
    {
        get
        {
            var rift = SummonersRift;
            Position2 Barracks(string name) => rift.Inhibitors.Single(inhibitor => inhibitor.Name == name).Position;
            return ReadOnly(new Dictionary<string, Position2>(StringComparer.Ordinal)
            {
                ["Nexus_T1"] = rift.Nexuses[0].Position,
                ["Inhibitor_T1_Top"] = Barracks("Barracks_T1_L1"),
                ["Inhibitor_T1_Mid"] = Barracks("Barracks_T1_C1"),
                ["Inhibitor_T1_Bot"] = Barracks("Barracks_T1_R1"),
                ["Fountain_T1"] = rift.OrderSpawn,
                ["Inhibitor_T2_Mid"] = Barracks("Barracks_T2_C1"),
                ["Inhibitor_T2_Top"] = Barracks("Barracks_T2_L1"),
                ["Inhibitor_T2_Bot"] = Barracks("Barracks_T2_R1"),
            });
        }
    }

    /// <summary>Summoner's Rift's HQ_T1 and HQ_T2 positions (<see cref="SummonersRift"/>).</summary>
    public static IReadOnlyDictionary<string, Position2> NexusPositions =>
        ReadOnly(SummonersRift.Nexuses.ToDictionary(nexus => nexus.Name, nexus => nexus.Position, StringComparer.Ordinal));

    /// <summary>The match map's two nexuses: Summoner's Rift's HQs, the Crystal Scar's two Odin crystals (batch 447), Map4's scene HQs.</summary>
    public static IReadOnlyList<NexusDefinition> Nexuses => Layout.Nexuses;

    /// <summary>
    /// The Crystal Scar's two crystals (batch 447, <see cref="MapLayout"/>), whatever map is loaded (the <see cref="Nexuses"/> list follows the
    /// loaded map).
    /// </summary>
    public static IReadOnlyList<NexusDefinition> CrystalScarNexusList =>
        MapLayout.For(Data.GameData.Default.Paths.ForMap(GameMaps.CrystalScar)).Nexuses;

    /// <summary>The match map's barracks: Summoner's Rift's six, Map4's four. The Crystal Scar has none in phase 1 (batch 443).</summary>
    public static IReadOnlyList<InhibitorDefinition> Inhibitors => Layout.Inhibitors;

    /// <summary>
    /// What the client stamps into its nav grid at creation with flag 0x42 and never takes off again: the Nexuses
    /// and the inhibitors, each a filled circle of its ObjectCFG
    /// <c>PathfindingCollisionRadius</c> around its position. The 140-unit turret stamp is the turret list's own.
    /// </summary>
    public static IReadOnlyList<Data.NavigationStamp> StructureStamps => Layout.StructureStamps;

    /// <summary>
    /// The match map's lane spines, each from the Order spawn to the Chaos one (batch 556; batch 595: every map's from its scene,
    /// <see cref="MapLayout"/>; the Crystal Scar keeps Summoner's Rift's).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Position2>> LanePaths => Layout.LanePaths;

    /// <summary>How many spine points at each end a lane's route leaves out: Order drops <c>Tail</c> at the Chaos end, Chaos <c>Head</c> at the Order end.</summary>
    public static IReadOnlyDictionary<string, LaneSpawnApproach> LaneSpawnApproaches => Layout.LaneSpawnApproaches;

    /// <summary>The match map's minion lanes (Summoner's Rift L, C, R; Map4 L, R), in the order a wave spawns them.</summary>
    public static IReadOnlyList<string> Lanes => Layout.Lanes;

    public static string NavigationGridPath(Data.GameDataPaths paths) => paths.Under("LEVELS", paths.MapName, "AIPath.aimesh_ngrid");

    public static uint BuildingNetId(string placementName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementName);
        var crc = 0xFFFFFFFFu;
        foreach (var character in System.Text.Encoding.ASCII.GetBytes(placementName))
        {
            crc ^= character;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }

        return crc ^ 0xFFFFFFFFu | 0xFF000000u;
    }

    public static IReadOnlyDictionary<string, string> TurretFront { get; } =
        ReadOnly(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Turret_T1_C_04"] = "Turret_T1_C_05", ["Turret_T1_L_02"] = "Turret_T1_L_03",
            ["Turret_T1_R_02"] = "Turret_T1_R_03", ["Turret_T2_C_04"] = "Turret_T2_C_05",
            ["Turret_T2_L_02"] = "Turret_T2_L_03", ["Turret_T2_R_02"] = "Turret_T2_R_03",
            ["Turret_T1_C_03"] = "Turret_T1_C_04", ["Turret_T1_C_06"] = "Turret_T1_L_02",
            ["Turret_T1_C_07"] = "Turret_T1_R_02", ["Turret_T2_C_03"] = "Turret_T2_C_04",
            ["Turret_T2_L_01"] = "Turret_T2_L_02", ["Turret_T2_R_01"] = "Turret_T2_R_02",
        });

    public static IReadOnlyDictionary<string, string> TurretGuardsInhibitor { get; } =
        ReadOnly(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Turret_T1_C_03"] = "Barracks_T1_C1", ["Turret_T1_C_06"] = "Barracks_T1_L1",
            ["Turret_T1_C_07"] = "Barracks_T1_R1", ["Turret_T2_C_03"] = "Barracks_T2_C1",
            ["Turret_T2_L_01"] = "Barracks_T2_L1", ["Turret_T2_R_01"] = "Barracks_T2_R1",
        });

    /// <summary>
    /// Whether the match's map is the Crystal Scar (map 8, Dominion): for the Dominion rules only. The structure layout is
    /// <see cref="Layout"/>, chosen by the map id.
    /// </summary>
    public static bool OnCrystalScar => Data.GameData.Default.Paths.MapId == GameMaps.CrystalScar;

    /// <summary>
    /// Whether the match's map is the Basic Tutorial (map 3): its TutorialScript sets every structure's protection itself
    /// (<c>OnTutorialInit</c>, TutorialScript.txt proto 0.3 pc 0080-0123), so the Summoner's Rift ladder stays out of it.
    /// </summary>
    public static bool OnTutorial => Data.GameData.Default.Paths.MapId == GameMaps.Tutorial;

    private static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values)
        where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(values);
}
