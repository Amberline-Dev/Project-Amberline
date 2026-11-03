using Amberline.Core;
using Amberline.Simulation.Encounters;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 446: the encounter leaves, the game actions that create, spawn, mutate and delete a map's encounters and name the squad a
/// unit came from. They run against <see cref="BehaviorTreeContext.Encounters"/>; a context without an encounter service fails the
/// node and reports it once. Each leaf's inputs and outputs are the parameter names of its node in the shipped XML.
/// </summary>
public static partial class BehaviorLeaves
{
    public static readonly Dictionary<string, BehaviorLeaf> EncounterTable = new(StringComparer.Ordinal)
    {
        // Reconstruction: creating an encounter spawns nothing; SpawnSquadFromEncounter spawns the squad (see EncounterService).
        ["CreateEncounterFromDefinition"] = CreateEncounterFromDefinition,
        ["DeleteEncounter"] = DeleteEncounter,
        ["AddMutatorToEncounter"] = AddMutatorToEncounter,
        // Batch 499: SpawnMinionWave's "catchup" mutator (OdinScriptBehaviorTrees.xml:3851-3878) and its lane push (:3889-3896).
        ["UpdateMutator"] = UpdateMutator,
        ["SquadPushLane"] = SquadPushLane,
        ["SpawnSquadFromEncounter"] = SpawnSquadFromEncounter,
        // Reconstruction: a unit no squad spawned has no squad name; the node succeeds with its output unset (a Lua nil).
        ["GetSquadNameOfUnit"] = GetSquadNameOfUnit,
    };

    private static EncounterService? Encounters(BehaviorCall call)
    {
        if (call.Context.Encounters is { } service) return service;
        call.Note("encounters:none", "this map has no encounter service");
        return null;
    }

    private static BehaviorStatus CreateEncounterFromDefinition(BehaviorCall call)
    {
        if (Encounters(call) is not { } service) return BehaviorStatus.Failure;
        var definition = call.Text("EncounterDefinition");
        if (service.CreateEncounter(definition, (int)call.Number("Count"), call.Text("Tags")) is not { } id)
        {
            // Reconstruction, as for a missing mutator (AddMutatorToEncounter): an encounter no shipped file holds (Shrine, PointGuard,
            // RegularWave) succeeds without effect and leaves its id unset, so the nodes after it run. OdinOpeningCeremony's SpawnShrines
            // sequence (OdinScriptBehaviorTrees.xml:4780-4826) otherwise fails before its SetBTInstanceStatus and repeats every tick.
            call.Note("encounter:" + definition, $"no shipped encounter file is named {definition}; the node succeeds without it (Reconstruction)");
            return BehaviorStatus.Success;
        }
        return Set(call, (double)id, "EncounterId");
    }

    private static BehaviorStatus DeleteEncounter(BehaviorCall call) =>
        Encounters(call) is { } service && service.DeleteEncounter((int)call.Number("EncounterId"))
            ? BehaviorStatus.Success
            : BehaviorStatus.Failure;

    private static BehaviorStatus AddMutatorToEncounter(BehaviorCall call)
    {
        if (Encounters(call) is not { } service) return BehaviorStatus.Failure;
        var mutator = call.Text("MutatorDefinition");
        // Batch 448 (Reconstruction): a mutator no shipped file holds (RangedWaveBase, RangedWaveProgression) succeeds without effect.
        // The Initialization sequence that adds it has no MaskFailure around it, and the nodes after it are the ones the designers meant to
        // run (41 LocationIllumination calls, the respawn points), so a failure would abort them.
        if (!service.HasMutator(mutator))
        {
            call.Note("mutator:" + mutator, $"no shipped mutator file is named {mutator}; the node succeeds without it (Reconstruction)");
            return BehaviorStatus.Success;
        }
        if (service.AddMutator((int)call.Number("EncounterId"), mutator, call.Text("Tags"), (int)call.Number("MutationIndex")))
            return BehaviorStatus.Success;
        call.Note("mutator-encounter:" + mutator, $"no encounter is named by the node for {mutator}");
        return BehaviorStatus.Failure;
    }

    private static BehaviorStatus SpawnSquadFromEncounter(BehaviorCall call)
    {
        if (Encounters(call) is not { } service) return BehaviorStatus.Failure;
        if (call.Vector("Position") is not { } at) return BehaviorStatus.Failure;
        // The squad of an encounter that was never created (a missing definition, above) spawns nothing and succeeds.
        if (service.Encounter((int)call.Number("EncounterId")) is null) return BehaviorStatus.Success;
        // Batch 499: the node's clock staggers a wave's units (SmallWave's SpawnDelay, EncounterService.SpawnSquad).
        var squad = service.SpawnSquad((int)call.Number("EncounterId"), new Position2(at.X, at.Z), call.TeamOf("Team"), call.Text("SquadName"),
            call.Now);
        return squad is { } id ? Set(call, (double)id, "SquadId") : BehaviorStatus.Failure;
    }

    // Batch 499: a mutator the encounter does not carry fails the node (its Selector then tries the other branch, :3851-3878), as a missing
    // mutator file at AddMutatorToEncounter succeeds only because that sequence has no MaskFailure (batch 448).
    private static BehaviorStatus UpdateMutator(BehaviorCall call) =>
        Encounters(call) is { } service &&
        service.UpdateMutator((int)call.Number("EncounterId"), call.Text("MutatorDefinition"), (int)call.Number("MutationIndex"))
            ? BehaviorStatus.Success
            : BehaviorStatus.Failure;

    // Batch 499: the squad pushes the lane (Lane, Reverse, PatrolMode); the map's host installs the lane's route and script on its units.
    private static BehaviorStatus SquadPushLane(BehaviorCall call) =>
        Encounters(call) is { } service &&
        service.PushLane((int)call.Number("SquadId"), new SquadLane((int)call.Number("Lane"), call.Flag("Reverse"), call.Flag("PatrolMode")))
            ? BehaviorStatus.Success
            : BehaviorStatus.Failure;

    private static BehaviorStatus GetSquadNameOfUnit(BehaviorCall call)
    {
        if (Encounters(call) is not { } service) return BehaviorStatus.Failure;
        if (call.Unit("TargetUnit") is not { } unit) return BehaviorStatus.Failure;
        return Set(call, service.SquadNameOf(unit), "SquadName");
    }
}
