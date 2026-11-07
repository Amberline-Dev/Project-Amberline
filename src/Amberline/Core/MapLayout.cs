using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Core;

/// <summary>
/// One map's structures: its turrets, nexuses and inhibitors (position, health, regeneration), the structure circles the client stamps
/// into its nav grid, the team spawns, the two shop objects and the minion lanes (batch 556). <see cref="For"/> picks it by the map id and
/// reads it from the player's own map files: Summoner's Rift (map 1) and the Harrowing rift (map 2) each from its own scene
/// (<see cref="SummonersRift"/>), the Crystal Scar (map 8) from its scene and Locations.dat (<see cref="CrystalScar"/>), every other map
/// by <see cref="FromScene"/> (batch 595; until then maps 1, 2 and 8 kept literal tables copied from those files).
/// </summary>
public sealed record MapLayout(
    IReadOnlyList<LevelTurret> TurretRows,
    IReadOnlyList<TurretDefinition> Turrets,
    IReadOnlyList<NexusDefinition> Nexuses,
    IReadOnlyList<InhibitorDefinition> Inhibitors,
    IReadOnlyList<NavigationStamp> StructureStamps,
    Position2 OrderSpawn,
    Position2 ChaosSpawn,
    string OrderShopName,
    Position2 OrderShop,
    string ChaosShopName,
    Position2 ChaosShop,
    IReadOnlyList<string> Lanes,
    IReadOnlyDictionary<string, IReadOnlyList<Position2>> LanePaths,
    IReadOnlyDictionary<string, LaneSpawnApproach> LaneSpawnApproaches)
{
    private static readonly ConcurrentDictionary<string, Lazy<MapLayout>> Cache = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, TurretTier>? _tiers;
    private IReadOnlyDictionary<(TurretTier Tier, Team Team), string>? _characters;
    private IReadOnlyDictionary<string, Position2>? _turretPositions;

    /// <summary>The tier of each turret the map's <c>CreateLevelProps()</c> created, by its script name.</summary>
    public IReadOnlyDictionary<string, TurretTier> TurretTiers => _tiers ??= new ReadOnlyDictionary<string, TurretTier>(
        TurretRows.ToDictionary(row => row.Name, row => MapDefinitions.TurretTierOf(row.TowerId), StringComparer.Ordinal));

    /// <summary>Each turret's position by its script name, in the turret order.</summary>
    public IReadOnlyDictionary<string, Position2> TurretPositions => _turretPositions ??= new ReadOnlyDictionary<string, Position2>(
        Turrets.ToDictionary(turret => turret.Name, turret => turret.Position, StringComparer.Ordinal));

    /// <summary>
    /// The character <c>CreateLevelProps()</c> gives every turret of one tier on one team. Each (tier, team) pair the map creates is named
    /// by exactly one character in the shipped script.
    /// </summary>
    public string TurretCharacter(TurretTier tier, Team team) => (_characters ??= TurretRows
        .GroupBy(row => (MapDefinitions.TurretTierOf(row.TowerId), row.Team))
        .ToDictionary(group => group.Key, group => group.Select(row => row.Character).Distinct(StringComparer.Ordinal).Single()))[(tier, team)];

    /// <summary>
    /// The team's spawn location (the central point of the map's <c>Scene/__Spawn_T1.SCB</c> / <c>__Spawn_T2.SCB</c>, the anchor
    /// <see cref="MapDefinitions.InitialHeroSpawn"/> offsets its slots from). Only the two playing teams have one.
    /// </summary>
    public Position2 TeamSpawn(Team team) => team switch
    {
        Team.Order => OrderSpawn,
        Team.Chaos => ChaosSpawn,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    public Position2 ShopPosition(Team team) => team switch
    {
        Team.Order => OrderShop,
        Team.Chaos => ChaosShop,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    public string ShopName(Team team) => team switch
    {
        Team.Order => OrderShopName,
        Team.Chaos => ChaosShopName,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    /// <summary>The layout of the map <paramref name="paths"/> name, by its map id; built once per release and map.</summary>
    public static MapLayout For(GameDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Cache.GetOrAdd(paths.Root + "\n" + paths.MapName, _ => new Lazy<MapLayout>(() => paths.MapId switch
        {
            // Batch 601 (owner decision): the Harrowing rift (map 2) reads its own scene, LEVELS/Map2/Scene, which the client builds that map
            // from, by Summoner's Rift's rules (batch 595 borrowed Map1's layout for it, NOT RETAIL).
            GameMaps.Normal or GameMaps.Harrowing => SummonersRift(paths),
            GameMaps.CrystalScar => CrystalScar(paths),
            _ => FromScene(paths),
        })).Value;
    }

    /// <summary>
    /// A map's layout read from its shipped files (applied to Map1 it gives back Summoner's Rift's layout but for the lane additions of
    /// <see cref="SummonersRift"/>, <c>map4.layout-matches-scene</c>):
    /// <list type="bullet">
    /// <item>every building stands at its exact <c>Scene/&lt;name&gt;.SCB</c> CentralPoint (<see cref="SceneMesh"/>; the building's creation).
    /// The scene's CentralPoint already carries the ObjectCFG <c>Move</c> (Map1's three turrets with one, Turret_T1_C_04, T1_C_05 and
    /// T1_L_02, sit at their vertex footprint plus that Move to 4e-4), so the Move is not added again (the client does not, Map1 audit A10,
    /// 18 October 2026);</item>
    /// <item>the turrets are CreateLevelProps' <c>CreateChildTurret</c> rows (team and tier from the row);</item>
    /// <item>the nexuses are <c>HQ_T1</c> / <c>HQ_T2</c>, the inhibitors every <c>Barracks_T{1,2}_{L,C,R}1</c> mesh the scene ships;</item>
    /// <item>maximum health (<c>mMaxHP</c>, nexus), regeneration (<c>mBaseStaticHPRegen</c>) and the stamp radius
    /// (<c>PathfindingCollisionRadius</c>) come from <c>Scene/CFG/ObjectCFG.cfg</c> through the client's text parser; an inhibitor's maximum
    /// is <c>Constants.var bar_MaxHP</c>, the figure the client's dampener bar reads (the owner's Summoner's Rift rule, 25 October 2026);</item>
    /// <item>the attack radii are the bounds of the skin each building's ObjectCFG section names (<c>SkinName1</c>, <see cref="GameData.SkinHalfWidth"/>;
    /// every map names OrderNexus/ChaosNexus/OrderInhibitor/ChaosInhibitor);</item>
    /// <item>the spawns are <c>__Spawn_T1</c> / <c>__Spawn_T2</c>; the shops are the scene's <c>orderShop01</c> / <c>chaosShop01</c> meshes.
    /// Every scene object is named as its mesh names itself (<see cref="SceneMesh.Name"/>), the exact spelling the client hashes into the
    /// netid (<see cref="MapDefinitions.BuildingNetId"/>; batch 704): Map4's shops are <c>OrderShop01</c> and <c>chaosShop01</c>, the latter
    /// shipped as ChaosShop01.SCB.</item>
    /// <item>batch 556: the lanes are the ones both teams have a <c>Barracks_T{1,2}_{L,C,R}1</c> mesh on (Map4: L and R). A lane's spine
    /// is the Order barracks' <c>__P_Order_Spawn_Barracks__&lt;lane&gt;01</c> marker, the scene's <c>__NAV_&lt;lane&gt;0&lt;n&gt;</c> markers
    /// from n = 2 while the scene has one, and the Chaos barracks' marker: the marker's name ends in 01 where the __NAV list starts at 02,
    /// and the scene numbers each list from the Order side (Map1 and Map4). The lane waves run it as the Summoner's Rift centre lane does:
    /// each team from its own barracks marker to the last __NAV marker before the enemy's (approach 1/1). Reconstruction: retail registers
    /// the barracks marker as the spawn and walks forward nav (Minion.lua SetStateAndMoveToForwardNav); the server half of obj_Barracks that
    /// places the spawn and ends the route is unrecovered.</item>
    /// </list>
    /// Reconstruction: the turret order (the create order, and the Order turrets' perception bubble ids) follows the Summoner's Rift table's
    /// rule: Order before Chaos, the fountain shrine last, the rest by name.
    /// </summary>
    public static MapLayout FromScene(GameDataPaths paths) => FromScene(paths, null, null);

    /// <param name="paths">The map.</param>
    /// <param name="place">Where a scene mesh stands (by its file name); its CentralPoint when null.</param>
    /// <param name="detours">Per lane, the waypoints after the Order barracks marker and before the Chaos one. Each team's route leaves out
    /// the other end's marker and detours (the approach), so it still ends at the last __NAV marker before the enemy's barracks.</param>
    private static MapLayout FromScene(GameDataPaths paths, Func<string, Position2>? place,
        IReadOnlyDictionary<string, (Position2[] Order, Position2[] Chaos)>? detours)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var scene = new SceneFiles(paths);
        place ??= scene.Central;
        Position2 At(string name) => place(scene.Required(name));

        var config = scene.ObjectConfig();
        double Config(string section, string key) => config.TryGet(section, key, out var text)
            ? InibinValue.AtofPrefix(text)
            : throw new InvalidDataException($"{paths.MapName} ObjectCFG.cfg [{section}] has no {key}");
        // Reconstruction (batch 563): Map3's ObjectCFG sets no mBaseStaticHPRegen on any building; an absent key reads the reader's default 0
        // (the client's config getter takes 0 as its fallback), so the tutorial's buildings do not regenerate.
        double Regen(string section) => config.TryGet(section, "mBaseStaticHPRegen", out var text) ? InibinValue.AtofPrefix(text) : 0.0;
        var constants = MapConstants.Load(paths, paths.MapName);
        var inhibitorMaxHealth = constants.Values.TryGetValue("bar_MaxHP", out var bar)
            ? bar
            : throw new InvalidDataException($"{paths.MapName} Constants.var has no bar_MaxHP");

        // Batch 599: a building's attack radius is its ObjectCFG SkinName1 skin's bounds: (X extent + Z extent) / 4.
        var characters = new GameData(paths);
        double SkinRadius(string section) => config.TryGet(section, "SkinName1", out var skin)
            ? characters.SkinHalfWidth(skin)
            : throw new InvalidDataException($"{paths.MapName} ObjectCFG.cfg [{section}] has no SkinName1");

        var rows = MapScriptData.TurretsFor(paths);
        var turrets = TurretsOf(rows, At);
        NexusDefinition Nexus(string mesh, Team team) => new(mesh, team, place(mesh), Config(mesh, "mMaxHP"), SkinRadius(mesh),
            MapDefinitions.BuildingNetId(mesh), Config(mesh, "PathfindingCollisionRadius"), Regen(mesh));
        NexusDefinition[] nexuses = [Nexus(scene.Required("HQ_T1"), Team.Order), Nexus(scene.Required("HQ_T2"), Team.Chaos)];
        var inhibitors = (
            from team in new[] { Team.Order, Team.Chaos }
            from lane in new[] { "L", "C", "R" }
            let mesh = scene.Mesh((team == Team.Order ? "Barracks_T1_" : "Barracks_T2_") + lane + "1")
            where mesh is not null
            select new InhibitorDefinition(mesh, team, place(mesh), inhibitorMaxHealth, Config(mesh, "PathfindingCollisionRadius"),
                MapDefinitions.BuildingNetId(mesh), SkinRadius(mesh), Regen(mesh))).ToArray();
        var orderShop = scene.Required("orderShop01");
        var chaosShop = scene.Required("chaosShop01");
        string[] lanes = [.. new[] { "L", "C", "R" }.Where(lane => scene.Mesh("Barracks_T1_" + lane + "1") is not null && scene.Mesh("Barracks_T2_" + lane + "1") is not null)];
        // Map3 spells its barracks markers with four leading underscores (Scene/____P_Order_Spawn_Barracks__C01.SCB), Map1 and Map4 with two.
        string BarracksMarker(string side, string lane) =>
            scene.Mesh($"__P_{side}_Spawn_Barracks__{lane}01") ?? scene.Required($"____P_{side}_Spawn_Barracks__{lane}01");
        (Position2[] Order, Position2[] Chaos) Detours(string lane) =>
            detours is not null && detours.TryGetValue(lane, out var found) ? found : ([], []);
        IReadOnlyList<Position2> Spine(string lane)
        {
            List<Position2> spine = [place(BarracksMarker("Order", lane)), .. Detours(lane).Order];
            for (var node = 2; scene.Mesh($"__NAV_{lane}0{node}") is { } marker; node++) spine.Add(place(marker));
            spine.AddRange(Detours(lane).Chaos);
            spine.Add(place(BarracksMarker("Chaos", lane)));
            return spine;
        }
        return new MapLayout(rows, turrets, nexuses, inhibitors, StampsOf(nexuses, inhibitors),
            At("__Spawn_T1"), At("__Spawn_T2"), orderShop, place(orderShop), chaosShop, place(chaosShop),
            lanes, new ReadOnlyDictionary<string, IReadOnlyList<Position2>>(lanes.ToDictionary(lane => lane, Spine, StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, LaneSpawnApproach>(lanes.ToDictionary(lane => lane,
                lane => new LaneSpawnApproach(1 + Detours(lane).Order.Length, 1 + Detours(lane).Chaos.Length), StringComparer.Ordinal)));
    }

    /// <summary>
    /// Summoner's Rift (map 1) and the Harrowing rift (map 2, batch 601): <see cref="FromScene"/> applied to the map's own scene, every
    /// mesh at its exact CentralPoint (batch 599, owner decision: the batch-595 rule that kept the old tables' rounding is gone), with an
    /// earlier port's lane additions kept as rules (the same names and offsets on both maps, whose scenes name the same markers):
    /// <list type="bullet">
    /// <item>Reconstruction (the earlier ports of 20 and 29 July 2026), lane detours: one waypoint beside an inhibitor on the Order L and C exits and
    /// the Chaos C and R ones, each fixing one measured tight leg (a 3.7-unit margin by Barracks_T1_L1, an inhibitor detour on the centre
    /// lane, 82.8 units of clearance on the last R leg); they make the approaches 2/1 (L), 2/2 (C) and 1/2 (R).</item>
    /// <item>Reconstruction (the same ports), nudges: the centre lane's two spawns stand off their barracks markers (a detour off the HQ),
    /// and __NAV_L015, __NAV_R04 and __NAV_R07 are moved off the 260-unit turret path stamp (<see cref="MapDefinitions.TurretPathStampRadius"/>)
    /// by a perpendicular sidestep. The batch-556 node correction (WaveScheduler.LaneRoute) leaves the nudged nodes alone: each is pathing
    /// floor (<c>map4.summoners-rift-lanes-unchanged</c>).</item>
    /// </list>
    /// The offsets are the port's own adjustments, not values of the game's files.
    /// </summary>
    private static MapLayout SummonersRift(GameDataPaths paths)
    {
        var scene = new SceneFiles(paths);
        var nudges = new Dictionary<string, Position2>(StringComparer.Ordinal)
        {
            ["__P_Order_Spawn_Barracks__C01"] = new(58.73, 63.33),
            ["__P_Chaos_Spawn_Barracks__C01"] = new(-25.39, -68.29),
            ["__NAV_L015"] = new(-0.1, 10.0),
            ["__NAV_R04"] = new(-0.56, -40.0),
            ["__NAV_R07"] = new(-0.92, 9.96),
        };
        Position2 Place(string mesh) => nudges.TryGetValue(mesh, out var nudge) ? scene.Central(mesh) + nudge : scene.Central(mesh);
        Position2 Beside(string inhibitor, double x, double z) => scene.Central(inhibitor) + new Position2(x, z);
        return FromScene(paths, Place, new Dictionary<string, (Position2[] Order, Position2[] Chaos)>(StringComparer.Ordinal)
        {
            ["L"] = ([Beside("Barracks_T1_L1", 410.7418, 41.355)], []),
            ["C"] = ([Beside("Barracks_T1_C1", -218.1754, 288.8053)], [Beside("Barracks_T2_C1", 155.2999, -280.6791)]),
            ["R"] = ([], [Beside("Barracks_T2_R1", -458.7539, 75.8262)]),
        });
    }

    /// <summary>
    /// The Crystal Scar (map 8), from LEVELS/Map8:
    /// <list type="bullet">
    /// <item>the turrets are its CreateLevelProps rows (the four shrines), at their scene meshes, in <see cref="FromScene"/>'s order;</item>
    /// <item>batch 447: the two nexuses are the crystal props <c>LevelProp_Odin_SOG_{Order,Chaos}_Crystal</c> (the rule the Summoner's HQs
    /// follow, DataAudit), 500 health. Reconstruction: the placement name (the
    /// SCB base name) for the netid, the radius and pathfinding radius (the Summoner's Rift HQs', Map1's ObjectCFG), and no regeneration,
    /// which would cancel the score's drain. No barracks in phase 1 (batch 443) and no structure stamp;</item>
    /// <item>the team spawns are the meshes Locations.dat names <c>OrderStartPoint</c> and <c>ChaosStartPoint</c> (__Spawn_T1 and __Spawn_T2;
    /// the trees read them as OrderSpawnPoint and ChaosSpawnPoint, OdinScriptBehaviorTrees.xml :1789-1802); batch 460: the shops are its
    /// own (the client reads the .SCB before the .SCO, so the .sco CentralPoint is not used);</item>
    /// <item>it keeps Summoner's Rift's lane tables (batch 556: unchanged), which no wave walks: the map's waves are its Odin waves (the
    /// lane waves are off, ProductionMatchComposition).</item>
    /// </list>
    /// Every point is the exact CentralPoint (batch 599; batch 595 kept the old table's four decimals).
    /// </summary>
    private static MapLayout CrystalScar(GameDataPaths paths)
    {
        var scene = new SceneFiles(paths);
        Position2 Place(string mesh) => scene.Central(mesh);
        var rift = For(paths.ForMap(GameMaps.Normal));
        NexusDefinition Crystal(string side, Team team)
        {
            var name = scene.Required($"LevelProp_Odin_SOG_{side}_Crystal");
            var hq = rift.Nexuses.Single(nexus => nexus.Team == team);
            return new NexusDefinition(name, team, Place(name), MapDefinitions.CrystalScarNexusMaxHealth, hq.Radius,
                MapDefinitions.BuildingNetId(name), hq.PathfindingRadius, HealthRegen: 0.0);
        }
        var locations = new NamedLocations(paths);
        var rows = MapScriptData.TurretsFor(paths);
        var orderShop = scene.Required("orderShop01");
        var chaosShop = scene.Required("chaosShop01");
        return new MapLayout(rows, TurretsOf(rows, name => Place(scene.Required(name))),
            [Crystal("Order", Team.Order), Crystal("Chaos", Team.Chaos)], [], [],
            Place(scene.Required(locations.Mesh("OrderStartPoint"))), Place(scene.Required(locations.Mesh("ChaosStartPoint"))),
            orderShop, Place(orderShop), chaosShop, Place(chaosShop), rift.Lanes, rift.LanePaths, rift.LaneSpawnApproaches);
    }

    private static TurretDefinition[] TurretsOf(IReadOnlyList<LevelTurret> rows, Func<string, Position2> at) => rows
        .OrderBy(row => row.Team == Team.Order ? 0 : 1)
        .ThenBy(row => row.TowerId == 0)
        .ThenBy(row => row.Name, StringComparer.Ordinal)
        .Select(row => new TurretDefinition(row.Name, row.Team, at(row.Name), MapDefinitions.TurretTierOf(row.TowerId)))
        .ToArray();

    /// <summary>The nexus and inhibitor circles of <see cref="MapDefinitions.StructureStamps"/>, each its pathfinding radius.</summary>
    internal static IReadOnlyList<NavigationStamp> StampsOf(IReadOnlyList<NexusDefinition> nexuses, IReadOnlyList<InhibitorDefinition> inhibitors) =>
    [
        .. nexuses.Select(nexus => new NavigationStamp(nexus.Name, nexus.Position, nexus.PathfindingRadius)),
        .. inhibitors.Select(inhibitor => new NavigationStamp(inhibitor.Name, inhibitor.Position, inhibitor.Radius)),
    ];

    /// <summary>
    /// A map's <c>Scene</c> folder: its mesh files, looked up case-insensitively, each named as the mesh names itself (<see cref="SceneMesh.Name"/>,
    /// the client's object name and so its netid; batch 704: Map4's ChaosShop01.SCB is <c>chaosShop01</c>).
    /// </summary>
    private sealed class SceneFiles
    {
        private readonly string _folder;
        private readonly string[] _meshes;
        private readonly string _map;

        public SceneFiles(GameDataPaths paths)
        {
            _map = paths.MapName;
            _folder = paths.Under("LEVELS", paths.MapName, "Scene");
            _meshes = CaseInsensitivePaths.EnumerateFiles(_folder, "*.SCB").Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
        }

        public string? Mesh(string name) => _meshes.FirstOrDefault(mesh => string.Equals(mesh, name, StringComparison.OrdinalIgnoreCase)) is { } file
            ? SceneMesh.Name(CaseInsensitivePaths.Resolve(Path.Combine(_folder, file + ".SCB")))
            : null;

        public string Required(string name) => Mesh(name) ?? throw new FileNotFoundException($"{_map} ships no {name}.SCB", _folder);

        public Position2 Central(string mesh)
        {
            var central = SceneMesh.CentralPoint(CaseInsensitivePaths.Resolve(Path.Combine(_folder, mesh + ".SCB")));
            return new Position2(central.X, central.Z);
        }

        public ClientTextConfig ObjectConfig() => ClientTextConfig.Load(CaseInsensitivePaths.Resolve(Path.Combine(_folder, "CFG", "ObjectCFG.cfg")));
    }
}
