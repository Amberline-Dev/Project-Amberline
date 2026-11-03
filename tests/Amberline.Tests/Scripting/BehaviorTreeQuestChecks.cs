using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>Batch 444: the Dominion quest host (map 8 starts its enabled quests and ticks them; maps 1 and 2 run none).</summary>
public static class BehaviorTreeQuestChecks
{
    [Check("scripting.behaviour-tree-quest-host-runs-only-on-map-8")]
    public static void QuestHostStartsTheEnabledQuestsOnMapEightOnly()
    {
        var world = new WorldState();
        TestAssert.True(BehaviorTreeQuestHost.ForMap(GameMaps.Normal, GameDataPaths.Default, world) is null,
            "Summoner's Rift runs no behaviour trees");
        TestAssert.True(BehaviorTreeQuestHost.ForMap(GameMaps.Harrowing, GameDataPaths.Default, world) is null,
            "the Harrowing rift runs no behaviour trees");
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world);
        TestAssert.True(host is not null, "map 8 has a quest host");
        TestAssert.Equal(2, host!.Instances.Count,
            "CapturePointManager and OdinOpeningCeremony start; ScoreboardPrototypeManager is disabled in OdinScript.xml");
    }

    [Check("scripting.behaviour-tree-map8-capture-quest-runs-sixty-seconds")]
    public static void MapEightCaptureQuestRunsSixtySecondsWithoutAnException()
    {
        var world = new WorldState();
        // Batch 448: no test turret seam; the host spawns the point turrets (one per team per point) and GetTurret answers them.
        var notes = new List<string>();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world,
            (key, _) => notes.Add(key), null,
            (skin, at, team) => world.Register(new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at)
            {
                Name = skin,
                ChampionName = skin,
            }))
            ?? throw new InvalidOperationException("map 8 has a quest host");

        const double step = 1.0 / 30.0;
        // 90 s of game time: the opening release is 80 s on this server (NOT RETAIL; the shipped 45), so the ceremony's stop falls inside it.
        const int ticks = 90 * 30;
        for (var tick = 1; tick <= ticks; tick++)
            host.Tick(tick * step, step);

        // CapturePointManager ticks for the whole window; OdinOpeningCeremony turns itself off after its release (SetBTInstanceStatus,
        // OdinScriptBehaviorTrees.xml:4827), now that a missing encounter no longer stops its last sequence.
        TestAssert.True(host.Instances.Single(instance => instance.TreeName == "CapturePointManager").Ticks == ticks,
            "CapturePointManager ticks for 90 s of game time");
        var ceremony = host.Instances.Single(instance => instance.TreeName == "OdinOpeningCeremony");
        var release = Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ReleaseSeconds;
        TestAssert.True(ceremony.Ticks is > 0 && ceremony.Ticks > release * 30 - 1 && ceremony.Ticks < ticks,
            $"OdinOpeningCeremony stops after its release ({ceremony.Ticks} ticks)");
        var capture = host.Instances[0];
        TestAssert.Equal("CapturePointManager", capture.TreeName, "OdinScript.xml lists CapturePointManager first");

        var scene = Path.Combine(new GameDataPaths(null, "Map8").Root, "LEVELS", "Map8", "Scene");
        TestAssert.True(capture.Variable("OrderSpawnPoint") is Position3 orderSpawn
            && orderSpawn == SceneMesh.CentralPoint(Path.Combine(scene, "__Spawn_T1.SCB")),
            "OrderStartPoint resolves to the shipped __Spawn_T1 mesh");
        TestAssert.True(capture.Variable("ChaosSpawnPoint") is Position3 chaosSpawn
            && chaosSpawn == SceneMesh.CentralPoint(Path.Combine(scene, "__Spawn_T2.SCB")),
            "ChaosStartPoint resolves to the shipped __Spawn_T2 mesh");
        // Batch 448: the Order turret of point A stands on Info_PointA, so its ground position is the point's centre (X and Z).
        var pointA = SceneMesh.CentralPoint(Path.Combine(scene, "Info_PointA.SCB"));
        TestAssert.True(capture.Variable("CapturePointAPos") is Position3 pointAPos && pointAPos.X == pointA.X && pointAPos.Z == pointA.Z,
            "GetUnitPosition reads the point turret's ground position (Info_PointA)");
        TestAssert.Near(0.0, (double)capture.Variable("CapturePointAProgress")!, 1e-9,
            "the initialisation sets the capture point variables");

        // The shipped data's gaps, each reported once: the graveyard names Locations.dat does not hold. Batch 446 implemented the
        // encounter nodes, so the script wins over the batch 444 expectation: the initialisation now runs CreateEncounterFromDefinition
        // and MeleeWaveBase, and stops at the first mutator no shipped file holds (RangedWaveBase, OdinScriptBehaviorTrees.xml:2068).
        TestAssert.True(notes.Contains("location:GraveyardA"), "GraveyardA is not in Locations.dat and is reported once");
        TestAssert.True(notes.Contains("mutator:RangedWaveBase"),
            "the log names the first encounter node the initialisation cannot run yet");
        // Batch 448: AddDebugCircle is a DIAGNOSTICS no-op, so OdinOpeningCeremony's debug circle no longer stops it.
        TestAssert.True(!notes.Contains("unimplemented:AddDebugCircle"), "AddDebugCircle is implemented as a debug no-op");
        TestAssert.True(notes.Count == notes.Distinct().Count(), "every note is reported once");
        TestAssert.True(!notes.Any(note => note.StartsWith("turret:", StringComparison.Ordinal)),
            "every slot the quest asks for has a turret in this world");
    }
}
