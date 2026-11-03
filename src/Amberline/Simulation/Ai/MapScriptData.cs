using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Ai;

/// <summary>
/// A map's shipped map scripts read once per process and per map folder, by running them on the batch-139 Lua 5.1 VM
/// exactly the way a match does. The engine used to carry the same facts as hand-written literals — the twelve neutral camps, the experience each
/// monster pays, and the turret roster <c>CreateLevelProps</c> names — and those literals are gone; every consumer reads what the
/// script wrote. The live match has its own <see cref="LevelScriptHost"/> that decides when a camp spawns and respawns; this type only
/// freezes the tables, so a static reader never has to hold a world or a clock. Summoner's Rift (LEVELS/Map1) is the default map; the
/// Harrowing rift (LEVELS/Map2) reads its own script, which builds thirteen camps (camp 13 is the Urf, batch 434).
/// </summary>
public static class MapScriptData
{
    private static readonly ConcurrentDictionary<string, Lazy<Snapshot>> Cache = new(StringComparer.Ordinal);

    /// <summary>The match's map's <c>NeutralMinionCamps</c>, one entry per camp (<see cref="MatchMap"/>).</summary>
    public static IReadOnlyList<LevelNeutralCamp> NeutralCamps => NeutralCampsFor(MatchMap);

    /// <summary>The camps a map's <c>NeutralMinionSpawn.luaobj</c> builds (its <c>NUMBER_OF_CAMPS</c>, one entry each).</summary>
    public static IReadOnlyList<LevelNeutralCamp> NeutralCampsFor(GameDataPaths paths) => SnapshotFor(paths).NeutralCamps;

    /// <summary>The match's map's <c>DefaultNeutralMinionValues</c> experience column, by monster name.</summary>
    public static IReadOnlyDictionary<string, double> NeutralExperience => NeutralExperienceFor(MatchMap);

    /// <summary>A map's <c>DefaultNeutralMinionValues</c> experience column, by monster name.</summary>
    public static IReadOnlyDictionary<string, double> NeutralExperienceFor(GameDataPaths paths) => SnapshotFor(paths).NeutralExperience;

    /// <summary>The match's map's <c>NeutralMinionSpawn</c> <c>HERO_EXP_RADIUS</c>.</summary>
    public static double HeroExperienceRadius => HeroExperienceRadiusFor(MatchMap);

    /// <summary>A map's <c>NeutralMinionSpawn</c> <c>HERO_EXP_RADIUS</c>.</summary>
    public static double HeroExperienceRadiusFor(GameDataPaths paths) => SnapshotFor(paths).HeroExperienceRadius;

    /// <summary>The match's map's <c>CreateChildTurret</c> rows, in the order its script emits them (twenty-four on Summoner's Rift, four on The Crystal Scar).</summary>
    public static IReadOnlyList<LevelTurret> Turrets => TurretsFor(MatchMap);

    /// <summary>A map's <c>CreateChildTurret</c> rows, in the order its <c>CreateLevelProps</c> emits them.</summary>
    public static IReadOnlyList<LevelTurret> TurretsFor(GameDataPaths paths) => SnapshotFor(paths).Turrets;

    /// <summary>
    /// The map a match plays: the process default (<see cref="GameData.Default"/>), which a server or headless match configures once for its
    /// <c>--map-id</c> (<see cref="GameData.ConfigureDefault"/>). Summoner's Rift (LEVELS/Map1) unless it was configured otherwise.
    /// </summary>
    private static GameDataPaths MatchMap => GameData.Default.Paths;

    private static Snapshot SnapshotFor(GameDataPaths paths) =>
        Cache.GetOrAdd(paths.Root + "\n" + paths.MapName, _ => new Lazy<Snapshot>(() => Snapshot.Build(paths))).Value;

    private sealed record Snapshot(
        IReadOnlyList<LevelNeutralCamp> NeutralCamps,
        IReadOnlyDictionary<string, double> NeutralExperience,
        double HeroExperienceRadius,
        IReadOnlyList<LevelTurret> Turrets)
    {
        public static Snapshot Build(GameDataPaths paths)
        {
            // A throwaway world: the scripts' table reads touch no entity. OnLevelInit chains
            // NeutralMinionSpawn and runs NeutralMinionInit (the camp tables); InitializeNeutralMinionInfo
            // adds the unique names and the respawn closures; OnPostLevelLoad runs CreateLevelProps.
            var host = new LevelScriptHost(new WorldState(), new AiScriptCatalog(paths));
            host.OnLevelInit(0.0);
            host.InitializeNeutralMinionInfo();
            host.OnPostLevelLoad();
            return new Snapshot(host.NeutralCamps,
                new ReadOnlyDictionary<string, double>(
                    new Dictionary<string, double>(host.NeutralExperience, StringComparer.Ordinal)),
                host.HeroExperienceRadius,
                host.LevelTurrets);
        }
    }
}
