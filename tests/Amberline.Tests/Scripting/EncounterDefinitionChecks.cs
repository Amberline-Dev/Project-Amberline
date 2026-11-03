using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Encounters;

namespace Amberline.Tests;

/// <summary>Batch 446: the map 8 encounter and mutator files load from the shipped data.</summary>
public static class EncounterDefinitionChecks
{
    [Check("scripting.encounter-definitions-load")]
    public static void MapEightEncounterAndMutatorFilesLoadWithoutError()
    {
        var catalog = EncounterCatalog.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        TestAssert.True(catalog.Errors.Count == 0, "every shipped encounter and mutator file parses: " + string.Join("; ", catalog.Errors));
        TestAssert.Equal(16, catalog.Encounters.Count, "the 16 EncounterDefinition files of Map8/Encounters");
        TestAssert.Equal(7, catalog.Mutators.Count, "the 7 mutator files of Map8/Encounters/Mutators");

        // The capture-point guardian: one monster, BehaviorTreeAI 1, not targetable (OdinCapturePointNeutral.json).
        var guard = catalog.Encounters["OdinCapturePointNeutral"];
        TestAssert.Equal(12.0, guard.BonusValue, "BonusValue");
        TestAssert.Equal("Guard", guard.SquadAi, "SquadAI");
        TestAssert.Equal(1, guard.MonsterGroups.Count, "one monster group");
        var monster = guard.MonsterGroups[0].Monsters.Single();
        TestAssert.Equal("OdinNeutralGuardian", monster.SkinName, "the guardian's character record");
        TestAssert.Equal(1, monster.Count, "one guardian");
        TestAssert.Equal(1, monster.BehaviorTreeAi, "BehaviorTreeAI 1");
        TestAssert.True(!monster.Targetable, "Targetable 0");

        // The capture mutator: a ProgressiveMultiply with three stat mutations (CapturePointMutator.json).
        var mutator = catalog.Mutators["CapturePointMutator"];
        TestAssert.Equal("ProgressiveMultiply", mutator.Type, "the mutator type");
        TestAssert.Equal(3, mutator.Mutations.Count, "three stat mutations");
        TestAssert.Equal("FlatArmorMod", mutator.Mutations[1].Stat, "the second mutation");
        TestAssert.Near(4.0, mutator.Mutations[1].Value, 1e-9, "its value");
    }
}
