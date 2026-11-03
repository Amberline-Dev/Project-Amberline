using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

/// <summary>
/// Map 8's shipped <c>LevelScript.luaobj</c> (The Crystal Scar, LEVELS/Map8/Scripts) running on the production host (batch 443): its init,
/// its <c>CreateLevelProps</c> rows and the natives and constants it reads by name. The host is built the way the match builds it.
/// </summary>
public static class DominionMapLevelScriptChecks
{
    [Check("server.dominion-map-8-level-script-runs-without-a-script-exception")]
    public static void TheCrystalScarLevelScriptInitialisesWithoutAnException()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        host.InitializeNeutralMinionInfo();
        host.OnPostLevelLoad();

        TestAssert.Equal(0, errors.Count, "map 8's LevelScript raises nothing: " + string.Join("; ", errors));
        // CreateLevelProps names four CreateChildTurret rows: two order and two chaos shrines, towerId 1 and 2.
        TestAssert.Equal(4, host.LevelTurrets.Count, "CreateLevelProps creates four shrine rows");
    }

    [Check("server.dominion-map-8-level-script-finds-every-name-it-reads")]
    public static void TheCrystalScarLevelScriptFindsItsNativesAndInputConstants()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        host.InitializeNeutralMinionInfo();
        host.OnPostLevelLoad();
        TestAssert.Equal(0, errors.Count, "no script error before the name check: " + string.Join("; ", errors));

        // The eight INPUT_* constants (LevelScript.luaobj reads them by name) and the natives the map-8 script calls by name.
        var names = host.GlobalNames;
        foreach (var name in new[]
                 {
                     "INPUT_ABILITIES", "INPUT_CAMERALOCKING", "INPUT_CAMERAMOVEMENT", "INPUT_CHAT", "INPUT_MINIMAPMOVEMENT",
                     "INPUT_MOVEMENT", "INPUT_SHOP", "INPUT_SUMMONERSPELLS",
                     "SetInputLockingFlag", "ToggleBarracks", "CreateLevelProps", "GetTurret", "GetHQ", "GetBarracks",
                 })
            TestAssert.True(names.Contains(name, StringComparer.Ordinal), name + " is a global the map 8 script reads");
    }

    [Check("server.dominion-map-8-level-script-creates-its-shrines-preloads-and-timers")]
    public static void TheCrystalScarLevelScriptCreatesWhatItsScriptsAsk()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        host.InitializeNeutralMinionInfo();
        host.OnPostLevelLoad();
        TestAssert.Equal(0, errors.Count, "no script error before the creation list: " + string.Join("; ", errors));

        // The four CreateChildTurret rows: name, character, team and towerId (the shrine's parent object names, batch 443).
        var rows = host.LevelTurrets.Select(row => $"{row.Name} {row.Character} {row.Team} {row.TowerId}")
            .OrderBy(text => text, StringComparer.Ordinal);
        TestAssert.Equal(string.Join("|", new[]
        {
            "Turret_ChaosTurretShrine OdinChaosTurretShrine Chaos 1",
            "Turret_ChaosTurretShrine1 OdinChaosTurretShrine Chaos 2",
            "Turret_OrderTurretShrine OdinOrderTurretShrine Order 1",
            "Turret_OrderTurretShrine1 OdinOrderTurretShrine Order 2",
        }), string.Join("|", rows), "CreateLevelProps' four shrine rows");

        // OnLevelInit preloads eleven characters and twenty-two spells (the list the map 8 script's OnLevelInit asks for).
        // The preloads OnLevelInit and the chunks it runs ask for, in the script's own order: twenty-one characters (OdinQuestIndicator
        // twice) and twenty-three spells. The shipped script is the truth; the research list (batch 441 section 3.1) missed some of them.
        TestAssert.Equal(string.Join(",", new[]
        {
            "character:Blue_Minion_Basic", "character:Blue_Minion_Wizard", "character:Blue_Minion_MechCannon", "character:Blue_Minion_MechMelee",
            "character:Red_Minion_Basic", "character:Red_Minion_Wizard", "character:Red_Minion_MechCannon", "character:Red_Minion_MechMelee",
            "spell:RespawnClassic", "character:crystal_platform", "character:OdinNeutralGuardian", "spell:odinguardianbuff",
            "spell:odinminiontaunt", "spell:odinguardianui", "spell:odinguardianuidamage", "spell:OdinCaptureChannel",
            "character:Odin_Blue_Minion_Caster", "character:OdinBlueSuperminion", "character:Odin_Red_Minion_Caster", "character:OdinRedSuperminion",
            "spell:OdinMinionSpellAttack", "character:OdinSpeedShrine", "spell:OdinSpeedShrineAura", "spell:OdinShrineAura",
            "character:OdinShieldRelic", "character:OdinCenterRelic", "character:OdinQuestIndicator", "character:OdinTestCubeRender",
            "spell:OdinCaptureChannelBomb", "spell:OdinChannelVision", "spell:OdinParticlePHBuff", "spell:OdinScoreSurvivor",
            "character:OdinQuestBuff", "character:OdinQuestIndicator", "spell:OdinQuestBuff", "spell:OdinQuestBuffParticle",
            "spell:OdinQuestIndicator", "spell:OdinQuestParticleRemover", "spell:SpellShieldMarker", "spell:hpbyplayerlevel",
            "spell:burning", "spell:monsterbuffs", "spell:monsterbuffs2", "spell:lifestealattack",
        }), string.Join(",", host.Preloads.Select(entry => entry.Kind + ":" + entry.Name)), "OnLevelInit's preloads");

        // InitTimer('AllowDamageOnBuildings', 1.0): a one-shot, the only timer the map 8 level init arms.
        var damage = host.Timers.Single(timer => timer.Name == "AllowDamageOnBuildings");
        TestAssert.Near(1.0, damage.Interval, 1e-9, "AllowDamageOnBuildings is armed at one second");
        TestAssert.False(damage.Repeat, "AllowDamageOnBuildings is a one-shot");
    }

    [Check("server.dominion-map-8-spawns-no-neutral-camp-its-script-never-chains")]
    public static void TheCrystalScarNeutralCampFileIsNeverChainedSoTheMatchHasNoCamps()
    {
        var (host, errors) = CreateHost();
        host.OnLevelInit(0.0);
        host.InitializeNeutralMinionInfo();
        host.OnPostLevelLoad();
        TestAssert.Equal(0, errors.Count, "no script error before the camp check: " + string.Join("; ", errors));

        // LEVELS/Map8/Scripts/NeutralMinionSpawn.luaobj defines the camp API, but LevelScript.luaobj never loads it (its constants are
        // LoadScriptIntoScript, Data\Scripts\EndOfGame.lua and CreateLevelProps.lua only), so the camp globals are never set and the
        // map's camp reader finds none (batch 443 finding; the file is shipped data no script chains yet).
        TestAssert.False(host.GlobalNames.Contains("NeutralMinionCamps", StringComparer.Ordinal), "NeutralMinionCamps is never set on map 8");
        TestAssert.Equal(0, MapScriptData.NeutralCampsFor(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)).Count,
            "the map 8 camp reader finds no camp");
    }

    [Check("server.dominion-map-8-jungle-builds-with-no-camp")]
    public static void TheCrystalScarJungleSchedulerHasNoCampAndStillBuilds()
    {
        // The match builds its jungle from the map's own camps (JungleScheduler, campIds null). The Crystal Scar has none (batch 443), and
        // a jungle with no camp is a valid jungle: the scheduler must build, not refuse the map.
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var scheduler = new JungleScheduler(new WorldState(), GameData.Default);
        TestAssert.Equal(0, scheduler.Camps.Count, "map 8 has no neutral camp to schedule");
    }

    private static (LevelScriptHost Host, List<string> Errors) CreateHost()
    {
        var errors = new List<string>();
        var host = new LevelScriptHost(new WorldState(), new AiScriptCatalog(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)))
        {
            OnError = failure => errors.Add(failure.Hook + ": " + failure.Message),
            TotalTeamMinionsSpawned = _ => 0,
        };
        return (host, errors);
    }
}
