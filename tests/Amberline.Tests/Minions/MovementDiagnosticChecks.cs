using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Server;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class MovementDiagnosticChecks
{
    [Check("movement.diagnostics-minion-hud-sync-and-ai-transitions", Serial = true)]
    public static void MinionEventsKeepHudAndQueuedRouteIdentityWithoutTickSpam()
    {
        WithLogging(() =>
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0);
            var minion = Actor(world, 77, UnitType.Minion);
            minion.AnchorPath.Add(new Position2(8, 12));
            var log = new RecordingLog();
            double? hud = 220.75;
            using var sink = new CollisionDiagnosticSink(world, log) { HudTime = () => hud };
            Step(world, sink, [minion], 1);
            sink.NoteWaypointIntent(minion.NetId, "congestion-route", 123,
                [minion.Position, new Position2(100, 0)]);
            sink.NoteDecision(new MovementDecisionObservation(minion.NetId,
                "minion-congestion", "wait-release", QuantizedAnchor: new Position2(16, 20)));
            var decision = log.Events("movement_event").Last();
            TestAssert.Equal("03:40", decision.Properties["hud_mmss"]!);
            TestAssert.Equal(220.75, decision.Properties["hud_seconds"]!);
            TestAssert.Equal(123L, decision.Properties["last_waypoint_sync_id"]!);
            TestAssert.Equal(8.0, ((double[])decision.Properties["client_anchor"]!)[0]);
            TestAssert.Equal(16.0, ((double[])decision.Properties["quantized_anchor"]!)[0]);

            minion.AiState = "engage";
            minion.AttackTarget = 91;
            Step(world, sink, [minion], 1);
            Step(world, sink, [minion], 1);
            TestAssert.Equal(1, log.Events("minion_ai_transition").Count());
            minion.AttackTarget = 92;
            Step(world, sink, [minion], 1);
            minion.AttackTarget = 93;
            Step(world, sink, [minion], 1);
            TestAssert.Equal(1, log.Events("minion_ai_transition").Count(),
                "rapid target flips retain the latest event without per-tick records");
            Step(world, sink, [minion], 1, 2.1);
            var latest = log.Events("minion_ai_transition").Last();
            TestAssert.Equal(2, log.Events("minion_ai_transition").Count());
            TestAssert.Equal((uint?)93, latest.Properties["to_target"]);
            TestAssert.Equal(2, latest.Properties["coalesced_transitions"]!);

            // Batch 279: the congestion wait is deleted; a state change is the transition left pending at the death.
            minion.AiState = "march";
            Step(world, sink, [minion], 1);
            minion.Alive = false;
            sink.FlushPostVisibility();
            TestAssert.Equal(3, log.Events("minion_ai_transition").Count(),
                "death flushes the last pending transition");
            TestAssert.Equal(0, sink.RetainedMinionAiCount);
        });
    }

    [Check("movement.diagnostics-long-match-fairness", Serial = true)]
    public static void RingsRetainLateWaitsAfterTheOldLifetimeBudget()
    {
        WithLogging(() =>
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0);
            var log = new RecordingLog();
            using var sink = new CollisionDiagnosticSink(world, log);
            var actors = Enumerable.Range(1, 12).Select(index => Actor(world, (uint)index,
                index <= 2 ? UnitType.Hero : UnitType.Minion)).ToArray();
            Step(world, sink, actors, 20);
            var first = log.Events("osc").ToArray();
            TestAssert.Equal(8, first.Length);
            TestAssert.Equal(2, first.Count(record => (uint)record.Properties["netid"]! <= 2),
                "two hero slots precede rotating minion details");
            Step(world, sink, actors, 110);
            TestAssert.Equal(12, log.Events("osc").Select(record => record.Properties["netid"])
                .Distinct().Count(), "pending minions must rotate through the next window");

            // Accelerate only this diagnostic clock; no gameplay/native frame claim.
            for (var index = 0; index < 40; index++) Step(world, sink, actors, 1, 10.0);
            TestAssert.True(log.Events("osc").Count() > 256,
                "detail collection must not have a lifetime exhaustion gate");
            foreach (var actor in actors) actor.Alive = false;
            Step(world, sink, [], 1, 1200.0);
            var lateHero = Actor(world, 90, UnitType.Hero);
            var lateMinion = Actor(world, 91, UnitType.Minion);
            lateMinion.Waypoints.Add(lateMinion.Position); // A parked anchor still retains a goal.
            Step(world, sink, [lateHero, lateMinion], 20);
            TestAssert.True(log.Events("osc").Any(record => (uint)record.Properties["netid"]! == 90));
            TestAssert.True(log.Events("osc").Any(record => (uint)record.Properties["netid"]! == 91));
            TestAssert.Equal(2, sink.RetainedActorCount, "dead actors must retire their rings");

            var times = log.Events("osc").Select(record =>
                (double)record.Properties["movement_elapsed"]!).ToArray();
            foreach (var time in times)
                TestAssert.True(times.Count(other => other > time - 10.0 + 1e-8 && other <= time) <= 8,
                    "every rolling ten-second window has at most eight detail records");
        });
    }

    [Check("movement.diagnostics-record-size-and-neighbours", Serial = true)]
    public static void FullSessionRecordsRetainTwentyFramesWithinTheByteLimit()
    {
        WithLogging(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "amberline-movement-detail-" + Guid.NewGuid().ToString("N"));
            using var logger = ServerLogger.Open(directory, new StringWriter());
            var world = new WorldState();
            world.Clock.SetVirtual(0);
            var hero = Actor(world, 1, UnitType.Hero);
            var neighbours = Enumerable.Range(2, 40).Select(index => Actor(world, (uint)index,
                UnitType.Minion)).ToArray();
            neighbours[^1].Position = new Position2(10000, 10000);
            neighbours[^2].Position = new Position2(9000, 9000);
            using var sink = new CollisionDiagnosticSink(world, logger);
            for (var index = 0; index < 20; index++)
            {
                world.Clock.Advance(.1);
                if (index == 19)
                {
                    sink.NoteDecision(new MovementDecisionObservation(hero.NetId, "hero-planner", "rejected",
                        new Position2(100, 0), Enumerable.Range(0, 1000).Select(point =>
                            new Position2(point, point)).ToArray(), BlockerPosition: neighbours[^2].Position,
                        BlockerRadius: 70, Phase: new string('x', 10000)));
                    sink.NoteDecision(new MovementDecisionObservation(hero.NetId, "hero-planner", "wait",
                        new Position2(100, 0), []));
                }
                sink.OnMovementTick(.1, [Snapshot(hero, .1) with
                {
                    ColliderIds = [neighbours[^1].NetId], QueryRadius = 1,
                    CollisionCandidatePosition = new Position2(.25, .5),
                    Branches = Enumerable.Repeat(new string('b', 10000), 100).ToArray(),
                }]);
            }
            sink.Dispose();
            logger.Dispose();
            var records = Directory.GetFiles(directory, "session.jsonl", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines).Where(line => line.Contains("\"msg\":\"osc\"", StringComparison.Ordinal))
                .ToArray();
            TestAssert.True(records.Length > 0);
            foreach (var line in records)
                TestAssert.True(Encoding.UTF8.GetByteCount(line + Environment.NewLine) <= 16 * 1024,
                    "the envelope and newline count toward the complete detail limit");
            using var document = JsonDocument.Parse(records[^1]);
            var detail = document.RootElement;
            var context = detail.GetProperty("frame_context");
            TestAssert.Equal(20, context.GetArrayLength());
            TestAssert.Equal(13, detail.GetProperty("ring")[0].GetArrayLength());
            TestAssert.True(detail.GetProperty("nbrs").GetArrayLength() <= 16);
            TestAssert.Equal(neighbours[^2].NetId, detail.GetProperty("nbrs")[0].GetProperty("netid").GetUInt32(),
                "the exact-position planner match is retained outside the collision query radius");
            TestAssert.True(detail.GetProperty("nbrs").EnumerateArray().Any(body =>
                body.GetProperty("netid").GetUInt32() == neighbours[^1].NetId),
                "an actual distant collider also takes priority over nearby candidates");
            TestAssert.Equal(9000.0, detail.GetProperty("nbrs")[0].GetProperty("planner_pos")[0].GetDouble());
            TestAssert.Equal("wait", detail.GetProperty("decision").GetProperty("reason").GetString()!);
            TestAssert.Equal(70.0, detail.GetProperty("planner_blocker").GetProperty("clearance_radius").GetDouble());
            TestAssert.Equal(.25, context[0][5].GetDouble());
            TestAssert.True(detail.GetProperty("neighbours_truncated").GetBoolean());
        });
    }

    [Check("movement.diagnostics-cutoff-coalescing-and-pruning", Serial = true)]
    public static void CompactDecisionsContinueAfterTheExplicitBulkCutoff()
    {
        WithLogging(() =>
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0);
            var hero = Actor(world, 1, UnitType.Hero);
            var log = new RecordingLog();
            var cutoffs = new List<FidelityTraceCutoff>();
            using var bulk = new FidelityTrace(world, cutoff: cutoffs.Add, budgetBytes: 1);
            using var sink = new CollisionDiagnosticSink(world, log);
            for (var index = 0; index < 3; index++) { bulk.BeginTick(); bulk.FlushPostVisibility(); }
            TestAssert.Equal(1, cutoffs.Count);
            TestAssert.False(bulk.Enabled);
            TestAssert.Equal(1, cutoffs[0].BudgetBytes);
            TestAssert.True(cutoffs[0].RejectedBatchBytes > 1);
            Step(world, sink, [hero], 1, 1200.0);
            for (var index = 0; index < 20; index++)
                sink.NoteDecision(new MovementDecisionObservation(hero.NetId, "hero-planner", "wait",
                    new Position2(100, 0), [], Phase: "body-follower"));
            TestAssert.Equal(1, log.Events("movement_event").Count(record =>
                (string)record.Properties["kind"]! == "hero-planner"));
            Step(world, sink, [hero], 1, 10.0);
            TestAssert.Equal(19, log.Events("movement_repeat").Single().Properties["repeats"]!);
            TestAssert.True(sink.RetainedSemanticCount > 0);
            sink.NoteDecision(new MovementDecisionObservation(hero.NetId, "hero-planner", "wait",
                new Position2(100, 0), [], Phase: "body-follower"));
            hero.Alive = false;
            sink.FlushPostVisibility();
            TestAssert.Equal(1, log.Events("movement_repeat").Last().Properties["repeats"]!,
                "retirement flushes pending repeats before the periodic summary");
            TestAssert.Equal(0, sink.RetainedSemanticCount);
            TestAssert.Equal(0, sink.RetainedActorCount);
            TestAssert.Equal(0, sink.PendingDetailCount);
            var before = log.Records.Count;
            Environment.SetEnvironmentVariable("LOG_COLLISION", null);
            hero.Alive = true;
            sink.NoteAcceptedHeroOrder(hero, 2, 0, new Position2(100, 0));
            sink.NoteDecision(new MovementDecisionObservation(hero.NetId, "hero-planner", "wait"));
            sink.OnMovementTick(.1, [Snapshot(hero, .1)]);
            TestAssert.Equal(before, log.Records.Count);
            TestAssert.Equal(Position2.Zero, hero.Position, "disabled diagnostics cannot move the actor");
        });
    }

    [Check("movement.diagnostics-validated-orders-and-enqueue-truth", Serial = true)]
    public static void ProductionDiagnosticsObserveValidationAndNativeQueueOutcomes()
    {
        WithLogging(() =>
        {
            var log = new RecordingLog();
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
                solo: true, readySettleSeconds: 0, sessionLog: log);
            runtime.World.Clock.SetVirtual(0);
            runtime.Session.CreateWorld();
            runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1));
            runtime.Session.Spawn(1);
            var seat = runtime.Session.Participants.Single();
            var hero = seat.Hero!;
            var invalid = ClientGameplayPackets.MoveOrder(hero.NetId, 99, hero.Position).Plaintext;
            TestAssert.False(runtime.Dependencies.Gameplay(new SessionGameplayContext(seat, hero, 1,
                invalid, null, null, hero)));
            TestAssert.False(log.Events("movement_event").Any());
            var command = ClientGameplayPackets.MoveOrder(hero.NetId, 10, hero.Position).Plaintext;
            TestAssert.True(runtime.Dependencies.Gameplay(new SessionGameplayContext(seat, hero, 1,
                command, null, null, hero)));
            TestAssert.True(log.Events("movement_event").Any(record =>
                (string)record.Properties["kind"]! == "hero-order"));

            using var network = MatchRuntimeNetwork.CreateEphemeral(runtime, channelLimit: 8);
            var trace = runtime.MovementTrace!;
            var route = new[] { hero.Position, new Position2(hero.Position.X + 10, hero.Position.Z) };
            var packet = VisionPackets.WaypointList(hero.NetId, route, 100);
            trace.NoteWaypointIntent(hero.NetId, "collision-resume", 100, route);
            TestAssert.True(network.TryEnqueueObserved(packet, 1, () => true));
            TestAssert.False(network.TryEnqueueObserved(packet, 2, () => false));
            TestAssert.Throws<InvalidOperationException>(() =>
                network.TryEnqueueObserved(packet, 3, () => throw new InvalidOperationException("enqueue probe")));
            var outcomes = log.Events("movement_event").Where(record =>
                record.Properties.ContainsKey("connection_id") && record.Properties["connection_id"] is not null).ToArray();
            TestAssert.Equal(new[] { "enqueued", "closing-peer-drop", "enqueue-exception" },
                outcomes.Select(record => (string)record.Properties["outcome"]!).ToArray());
            TestAssert.True(outcomes.All(record => (string)record.Properties["reason"]! == "collision-resume"));
            trace.FlushPostVisibility();
            TestAssert.False(log.Events("movement_event").Any(record =>
                record.Properties.GetValueOrDefault("sync_id") is long id && id == 100 &&
                Equals(record.Properties.GetValueOrDefault("outcome"), "no-observed-enqueue")));

            trace.NoteWaypointIntent(hero.NetId, "hidden-route", 101, route);
            trace.FlushPostVisibility();
            TestAssert.True(log.Events("movement_event").Any(record =>
                Equals(record.Properties.GetValueOrDefault("outcome"), "no-observed-enqueue")));
            trace.NoteWaypointIntent(hero.NetId, "first", 102, route);
            trace.NoteWaypointIntent(hero.NetId, "second", 102, route);
            network.TryEnqueueObserved(VisionPackets.WaypointList(hero.NetId, route, 102), 1, () => true);
            TestAssert.Equal("unattributed", log.Events("movement_event").Last().Properties["reason"]!);
            network.Dispose();
            TestAssert.False(trace.Enabled);
            TestAssert.Equal(0, trace.RetainedSemanticCount);
            TestAssert.True(runtime.Movement.DecisionObserver is null);
            TestAssert.True(runtime.Minions!.DecisionObserver is null);
        });
    }

    private static Entity Actor(WorldState world, uint netId, UnitType type) => world.Register(
        new Entity(netId, type == UnitType.Hero ? EntityKind.Hero : EntityKind.Minion,
            type, Team.Order, Position2.Zero) { Alive = true, Radius = 35, Speed = 325 });

    private static MovementDiagnosticSnapshot Snapshot(Entity actor, double dt) =>
        new(actor.NetId, [], 0, 0, 0, Position2.Zero, actor.Position, actor.Position,
            actor.Position, [], Static: true, DeltaTime: dt, RequestedGoal: new Position2(100, 0));

    private static void Step(WorldState world, CollisionDiagnosticSink sink,
        IReadOnlyList<Entity> actors, int count, double dt = .1)
    {
        for (var index = 0; index < count; index++)
        {
            world.Clock.Advance(dt);
            sink.OnMovementTick(dt, actors.Select(actor => Snapshot(actor, dt)).ToArray());
        }
    }

    private static void WithLogging(Action check)
    {
        var previous = Environment.GetEnvironmentVariable("LOG_COLLISION");
        try { Environment.SetEnvironmentVariable("LOG_COLLISION", "1"); check(); }
        finally { Environment.SetEnvironmentVariable("LOG_COLLISION", previous); }
    }

    private sealed class RecordingLog : ISessionLogSink
    {
        public List<Record> Records { get; } = new();
        public IEnumerable<Record> Events(string message) => Records.Where(record => record.Message == message);
        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null) =>
            Records.Add(new Record(message, new Dictionary<string, object?>(properties!)));
    }

    private sealed record Record(string Message, IReadOnlyDictionary<string, object?> Properties);
}
