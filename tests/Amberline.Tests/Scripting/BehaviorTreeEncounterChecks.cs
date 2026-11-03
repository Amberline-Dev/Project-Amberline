using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Encounters;

namespace Amberline.Tests;

/// <summary>Batch 446: the encounter service behind the encounter leaves (create, spawn a squad, mutate, delete, squad name).</summary>
public static class BehaviorTreeEncounterChecks
{
    [Check("scripting.behaviour-tree-encounter-leaves")]
    public static void EncounterServiceCreatesSpawnsMutatesAndDeletesFromShippedFiles()
    {
        var catalog = EncounterCatalog.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var world = new WorldState();
        var spawned = new List<string>();
        var service = new EncounterService(catalog, (skin, at, team) =>
        {
            spawned.Add(skin);
            var unit = new Entity(world.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, team, at);
            world.Register(unit);
            return unit;
        });

        // The alias: the capture point's guard is the shipped OdinCapturePointNeutral encounter.
        var encounter = service.CreateEncounter("PointGuard", 1, "");
        TestAssert.True(encounter is not null, "PointGuard resolves to a shipped encounter");
        TestAssert.True(service.Encounter(encounter!.Value)!.Definition.Name == "OdinCapturePointNeutral", "that encounter is OdinCapturePointNeutral");
        TestAssert.True(service.CreateEncounter("NoSuchDefinition", 1, "") is null, "a name no file holds creates nothing");
        TestAssert.True(spawned.Count == 0, "creating an encounter spawns nothing");

        // One squad: one OdinNeutralGuardian at the position, untargetable, named after its encounter.
        var squad = service.SpawnSquad(encounter.Value, new Position2(1000.0, 2000.0), Team.Neutral, "");
        TestAssert.True(squad is not null, "the squad spawns");
        var units = service.Squad(squad!.Value)!.Units;
        TestAssert.Equal(1, units.Count, "one unit: the guardian");
        TestAssert.True(spawned.Count == 1 && spawned[0] == "OdinNeutralGuardian", "the spawn path gets the character record");
        TestAssert.True(units[0].NotTargetableToAlly && units[0].NotTargetableToEnemy, "Targetable 0 makes it untargetable");
        TestAssert.True(units[0].Team == Team.Neutral && units[0].Position.X == 1000.0 && units[0].Position.Z == 2000.0, "neutral, at the position");
        TestAssert.True(service.SquadNameOf(units[0]) == "OdinCapturePointNeutral", "GetSquadNameOfUnit names the squad");

        // Mutators: a shipped file is recorded; an unknown name is refused.
        TestAssert.True(service.AddMutator(encounter.Value, "CapturePointMutator", "", 0), "a shipped mutator is added");
        TestAssert.True(!service.AddMutator(encounter.Value, "NoSuchMutator", "", 0), "an unknown mutator is refused");
        TestAssert.Equal(1, service.Encounter(encounter.Value)!.Mutators.Count, "one mutator is recorded");

        // Deleting the encounter keeps its squad in the world.
        TestAssert.True(service.DeleteEncounter(encounter.Value), "the encounter deletes");
        TestAssert.True(!service.DeleteEncounter(encounter.Value), "a second delete finds nothing");
        TestAssert.True(service.SquadNameOf(units[0]) == "OdinCapturePointNeutral", "the squad outlives its encounter");

        // No spawn path: a squad cannot spawn, and nothing is reported as spawned.
        var bare = new EncounterService(catalog);
        var bareId = bare.CreateEncounter("OdinCapturePointNeutral", 1, "");
        TestAssert.True(bareId is not null && bare.SpawnSquad(bareId.Value, new Position2(0.0, 0.0), Team.Neutral, "") is null,
            "with no spawn path no squad spawns");
    }
}
