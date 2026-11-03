using Amberline.Core;
using System.Text.Json;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class FidelityTraceChecks
{
    [Check("minions-fidelity-trace-environment-budget", Serial = true)]
    public static void EnvironmentBudgetControlsActualCaptureCutoff()
    {
        var oldRate = Environment.GetEnvironmentVariable("LOG_FIDELITY_TRACE");
        var oldBudget = Environment.GetEnvironmentVariable("LOG_FIDELITY_BUDGET_MB");
        try
        {
            Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", "1");
            Environment.SetEnvironmentVariable("LOG_FIDELITY_BUDGET_MB", "1");
            FidelityTraceCutoff? cutoff = null;
            var published = false;
            using var trace = FidelityTrace.FromEnvironment(new WorldState(),
                publish: _ => published = true, cutoff: value => cutoff = value)!;
            var reason = new string('x', 40000);
            for (var index = 0; index < 32; index++) trace.NoteOrder(1, "waypoint", reason, index, []);
            trace.BeginTick();
            trace.FlushPostVisibility();
            TestAssert.True(cutoff is not null, "the configured 1 MiB budget rejects a larger batch");
            TestAssert.Equal(1024 * 1024, cutoff!.BudgetBytes);
            TestAssert.False(published);
            TestAssert.False(trace.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", oldRate);
            Environment.SetEnvironmentVariable("LOG_FIDELITY_BUDGET_MB", oldBudget);
        }
    }

    [Check("minions-fidelity-trace")]
    public static void TraceIsOptInBoundedAndImmutable()
    {
        var world = new WorldState();
        world.Clock.SetVirtual(12.345678);
        var minion = new Entity(77, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(10.0, 20.0))
        {
            Alive = true,
            Created = true,
            Visible = true,
            AiState = "engage",
            AttackTarget = 9,
        };
        minion.Waypoints.Add(new Position2(30.0, 40.0));
        world.Register(minion);

        FidelityTraceBatch? published = null;
        var trace = new FidelityTrace(world, publish: batch => published = batch);
        var sourceWaypoints = new List<Position2>
        {
            new(10.123456789, 20.987654321),
            new(30.0, 40.0),
        };
        trace.NoteOrder(77, "waypoint", "acquire-chase", 123, sourceWaypoints);
        trace.NoteHeroAttack(88, 99, new Position2(100, 200), new Position2(330, 200),
            "chase-out-of-range", 0, 150, .12, false, true);
        sourceWaypoints[0] = new Position2(-1.0, -1.0);
        trace.OnMovementTick([
            new MovementDiagnosticSnapshot(77, [77], 0.0, 1, 2, new Position2(0, 0)),
        ]);
        trace.BeginTick();
        trace.FlushPostVisibility();

        TestAssert.True(published is not null, "an armed trace must publish after visibility");
        var batch = published!;
        TestAssert.Equal(2, batch.Orders.Count);
        TestAssert.Equal(77u, batch.Orders[0].NetId);
        TestAssert.Equal("acquire-chase", batch.Orders[0].Reason);
        var cancellation = batch.Orders[1];
        TestAssert.Equal("hero-attack-cancel", cancellation.Kind);
        TestAssert.Equal(99u, cancellation.TargetNetId);
        TestAssert.Equal(new Position2(330, 200), cancellation.TargetPosition);
        TestAssert.Equal(.12, cancellation.WindupRemaining);
        TestAssert.Equal(false, cancellation.CanAttack);
        TestAssert.Equal(true, cancellation.InCommittedRange);
        TestAssert.Near((float)10.123456789, batch.Orders[0].Waypoints[0].X, 0.0);
        TestAssert.Near((float)20.987654321, batch.Orders[0].Waypoints[0].Z, 0.0);
        TestAssert.Equal(new[] { 77u }, batch.Minions[0].Collision.ColliderIds);
        TestAssert.Equal(new Position2(30.0, 40.0), batch.Minions[0].Waypoints[0]);
        using var json = JsonDocument.Parse(FidelityTrace.ToJson(batch));
        var jsonMinion = json.RootElement.GetProperty("minions")[0];
        TestAssert.True(jsonMinion.GetProperty("pos").ValueKind == JsonValueKind.Array,
            "trace positions must use the source array schema");
        TestAssert.True(jsonMinion.GetProperty("collision").TryGetProperty("sep_accum", out _),
            "trace collision must retain the full source snapshot");
        TestAssert.True(json.RootElement.GetProperty("orders")[0]
            .TryGetProperty("mono_ns", out _), "trace order uses source mono_ns");

        // A delivery flush without a simulation tick does not leak setup data.
        trace.NoteOrder(77, "waypoint", "setup", 124, [new Position2(1, 1)]);
        trace.FlushPostVisibility();
        TestAssert.Equal(1, trace.PendingOrderCount);
        trace.Reset();
        TestAssert.Equal(0, trace.PendingOrderCount);
    }

    [Check("minions-fidelity-trace-split-movement-cohorts")]
    public static void TraceRetainsEachCohortUntilItsNextMovementFrame()
    {
        var world = new WorldState();
        var hero = world.Register(new Entity(71, EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(100, 100)) { Alive = true });
        var minion = world.Register(new Entity(72, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(120, 100)) { Alive = true });
        var batches = new List<FidelityTraceBatch>();
        using var trace = new FidelityTrace(world, publish: batches.Add);
        var firstGoal = new Position2(200, 100);
        trace.OnMovementTick([new MovementDiagnosticSnapshot(hero.NetId, [], 0, 0, 0,
            Position2.Zero, RequestedGoal: firstGoal)]);
        trace.OnMovementTick([new MovementDiagnosticSnapshot(minion.NetId, [hero.NetId],
            0, 0, 0, Position2.Zero)]);
        trace.BeginTick();
        trace.FlushPostVisibility();
        TestAssert.Equal(firstGoal, batches[0].Heroes!.Single().RequestedGoal,
            "NPC callback preserves the hero's latest movement goal");
        TestAssert.Equal(new[] { hero.NetId }, batches[0].Minions.Single().Collision.ColliderIds);

        hero.Position = new Position2(125, 100);
        var nextGoal = new Position2(300, 100);
        trace.OnMovementTick([new MovementDiagnosticSnapshot(hero.NetId, [], 0, 0, 0,
            Position2.Zero, RequestedGoal: nextGoal)]);
        trace.BeginTick();
        trace.FlushPostVisibility();
        TestAssert.Equal(hero.Position, batches[1].Heroes!.Single().Position,
            "the batch still samples the current world position");
        TestAssert.Equal(nextGoal, batches[1].Heroes!.Single().RequestedGoal);
        TestAssert.Equal(new[] { hero.NetId }, batches[1].Minions.Single().Collision.ColliderIds,
            "a hero-only callback retains the minion's last collision frame");

        TestAssert.True(world.Remove(minion.NetId));
        trace.OnMovementTick([new MovementDiagnosticSnapshot(hero.NetId, [], 0, 0, 0,
            Position2.Zero, RequestedGoal: nextGoal)]);
        world.Register(new Entity(minion.NetId, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(150, 100)) { Alive = true });
        trace.BeginTick();
        trace.FlushPostVisibility();
        TestAssert.Equal(Array.Empty<uint>(), batches[2].Minions.Single().Collision.ColliderIds,
            "a retired minion's collision snapshot is not reused by a new entity");
    }

    [Check("minions-fidelity-trace-cap")]
    public static void TraceDropsAfterBoundedCapacity()
    {
        var world = new WorldState();
        var trace = new FidelityTrace(world, publish: _ => { });
        for (var i = 0; i < 4096 + 3; i++)
            trace.NoteOrder(1, "waypoint", "cap", i, []);
        TestAssert.Equal(4096, trace.PendingOrderCount);
        trace.BeginTick();
        trace.FlushPostVisibility();
        TestAssert.Equal(0, trace.PendingOrderCount);
    }

    [Check("minions-fidelity-trace-downsample")]
    public static void TraceRetainsOrdersBetweenSampledTicks()
    {
        var world = new WorldState();
        var batches = new List<FidelityTraceBatch>();
        var trace = new FidelityTrace(world, 2, batches.Add);
        trace.NoteOrder(1, "waypoint", "first", 1, []);
        trace.BeginTick();
        trace.FlushPostVisibility();
        TestAssert.Equal(0, batches.Count);
        TestAssert.Equal(1, trace.PendingOrderCount);
        trace.NoteOrder(1, "waypoint", "second", 2, []);
        trace.BeginTick();
        trace.FlushPostVisibility();
        TestAssert.Equal(1, batches.Count);
        TestAssert.Equal(2, batches[0].Orders.Count,
            "downsampling must retain orders until the sampled batch");
    }
}
