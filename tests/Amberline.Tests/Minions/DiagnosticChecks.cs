using System.Linq;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class DiagnosticChecks
{
    [Check("minions-ai-trace", Serial = true)]
    public static void MinionTraceUsesSourceCadenceAndShape()
    {
        var world = new WorldState();
        var minion = new Entity(77, EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(10.04, 20.06))
        {
            Alive = true,
            AiState = "march",
            AttackTarget = 91,
        };
        world.Register(minion);
        var log = new RecordingLog();
        var previous = Environment.GetEnvironmentVariable("LOG_MINION_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("LOG_MINION_TRACE", "2");
            var trace = MinionTraceSink.FromEnvironment(world, log);
            TestAssert.True(trace is not null, "trace gate should create a sink");
            trace!.OnMinionAiTick();
            TestAssert.Equal(0, log.Records.Count, "first unsampled AI tick");
            trace.OnMinionAiTick();
            TestAssert.Equal(1, log.Records.Count, "second AI tick should sample");
            TestAssert.Equal("AI", log.Records[0].Category);
            TestAssert.Equal("trace", log.Records[0].Message);
            var properties = log.Records[0].Properties!;
            TestAssert.Equal(77u, properties["netid"]!);
            TestAssert.Equal("march", properties["ai_state"]!);
            var position = (double[])properties["pos"]!;
            TestAssert.Equal(10.0, position[0]);
            TestAssert.Equal(20.1, position[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_MINION_TRACE", previous);
        }
    }

    [Check("minions-collision-diagnostics", Serial = true)]
    public static void CollisionDiagnosticsEmitBoundedOscillationDump()
    {
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        var minion = new Entity(101, EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(0.0, 0.0))
        {
            Alive = true,
            Speed = 100.0,
            Radius = 35.0,
            AiState = "march",
        };
        world.Register(minion);
        var neighbour = new Entity(202, EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(50.0, 0.0))
        {
            Alive = true,
            Speed = 100.0,
            Radius = 35.0,
            AiState = "march",
        };
        world.Register(neighbour);
        var log = new RecordingLog();
        var previous = Environment.GetEnvironmentVariable("LOG_COLLISION");
        try
        {
            Environment.SetEnvironmentVariable("LOG_COLLISION", "1");
            var trace = new CollisionDiagnosticSink(world, log);
            Environment.SetEnvironmentVariable("LOG_COLLISION", null);
            TestAssert.True(!trace.Enabled,
                "collision sink must be disabled while the source gate is off");
            trace.OnMovementTick([]);
            TestAssert.Equal(0, log.Records.Count,
                "collision diagnostics must read the gate at each tick");
            Environment.SetEnvironmentVariable("LOG_COLLISION", "1");
            for (var index = 0; index < 20; index++)
            {
                var start = new Position2(index % 2 == 0 ? 0.0 : 20.0, 0.0);
                var end = new Position2(index % 2 == 0 ? 20.0 : 0.0, 0.0);
                minion.Position = end;
                trace.OnMovementTick(0.1, [
                    new MovementDiagnosticSnapshot(101, [202], 0.0, 0, 0,
                        new Position2(2.0, 0.0), start, end, end, ["collider"], 0, null, false,
                        [202], 100.0, false, 0.1),
                    new MovementDiagnosticSnapshot(202, [], 0.0, 0, 0,
                        Position2.Zero, new Position2(50.0, 0.0), new Position2(50.0, 0.0),
                        new Position2(50.0, 0.0), [], 0, null, false, [], 0.0, false, 0.1),
                ]);
                world.Clock.Advance(0.1);
            }

            var oscillation = log.Records.Single(record => record.Message == "osc");
            TestAssert.Equal("AI", oscillation.Category);
            TestAssert.True(oscillation.Properties!.ContainsKey("ring"),
                "oscillation dump must preserve ring history");
            TestAssert.True(oscillation.Properties.ContainsKey("nbrs"),
                "oscillation dump must preserve neighbour context");
            var ring = (object[])oscillation.Properties["ring"]!;
            TestAssert.Equal(20, ring.Length);
            TestAssert.Equal(13, ((object[])ring[0]!).Length,
                "source ring entries are fixed 13-field arrays");
            var neighbours = (object[])oscillation.Properties["nbrs"]!;
            TestAssert.Equal(1, neighbours.Length,
                "oscillation dump must use the actor query radius");
            var neighbourProperties = (Dictionary<string, object?>)neighbours[0];
            TestAssert.Equal("col", neighbourProperties["cls"]!,
                "neighbor classification must retain collider evidence");

            // A known collider remains in the source dump even when its
            // frame-start position is outside the actor's coarse query. This
            // is needed to replay a rejected classification, not just the
            // neighbours that already passed the query.
            neighbour.Position = new Position2(400.0, 0.0);
            log.Records.Clear();
            trace = new CollisionDiagnosticSink(world, log);
            for (var index = 0; index < 20; index++)
            {
                var start = new Position2(index % 2 == 0 ? 0.0 : 20.0, 0.0);
                var end = new Position2(index % 2 == 0 ? 20.0 : 0.0, 0.0);
                trace.OnMovementTick(0.1, [
                    new MovementDiagnosticSnapshot(101, [202], 0.0, 0, 0,
                        new Position2(2.0, 0.0), start, end, end, ["collider"], 0,
                        0.25, true, [202], 10.0, false, 0.1),
                    new MovementDiagnosticSnapshot(202, [], 0.0, 0, 0,
                        Position2.Zero, neighbour.Position, neighbour.Position,
                        neighbour.Position, [], 0, null, false, [], 0.0, false, 0.1),
                ]);
            }
            var retained = log.Records.Single(record => record.Message == "osc");
            var retainedNeighbours = (object[])retained.Properties!["nbrs"]!;
            TestAssert.Equal(1, retainedNeighbours.Length,
                "known collider must remain in the reproduction dump outside the coarse query");
            var retainedRing = (object[])retained.Properties["ring"]!;
            var retainedFirst = (object[])retainedRing[0]!;
            TestAssert.Equal(0.25, Convert.ToDouble(retainedFirst[10]));
            TestAssert.Equal(1, Convert.ToInt32(retainedFirst[11]));

            // Reproduce the source self-check's rising-edge and cooldown
            // sequence with real forward samples. Empty frames do not clear a
            // ring or its flagged state, so feeding [] here would make the
            // cooldown assertion vacuous.
            log.Records.Clear();
            trace = new CollisionDiagnosticSink(world, log);
            FeedOscillation(trace, 20);
            TestAssert.Equal(1, log.Records.Count(record => record.Message == "osc"),
                "the first oscillation must emit one rising-edge dump");
            FeedForward(trace, 25); // clears the detector edge
            FeedOscillation(trace, 20); // still inside the ten-second cooldown
            TestAssert.Equal(1, log.Records.Count(record => record.Message == "osc"),
                "the second rising edge must respect the dump cooldown");
            FeedForward(trace, 25); // clear the second edge before retrying
            for (var index = 0; index < 35; index++)
                trace.OnMovementTick(0.1, []);
            FeedOscillation(trace, 20); // cooldown expired: a fresh dump is valid
            TestAssert.Equal(2, log.Records.Count(record => record.Message == "osc"),
                "a rising edge after the cooldown must emit a new dump");
            TestAssert.True(log.Records.Count(record => record.Message == "osc_summary") >= 1,
                "collision summary must use accumulated dt, including empty frames");

            // The source logger is best-effort. A sink failure must not escape
            // movement diagnostics and take down the simulation tick.
            var throwingLog = new ThrowingLog();
            var throwing = new CollisionDiagnosticSink(world, throwingLog);
            for (var index = 0; index < 20; index++)
            {
                var start = new Position2(index % 2 == 0 ? 0.0 : 20.0, 0.0);
                var end = new Position2(index % 2 == 0 ? 20.0 : 0.0, 0.0);
                throwing.OnMovementTick(0.1, [
                    new MovementDiagnosticSnapshot(101, [202], 0.0, 0, 0,
                        Position2.Zero, start, end, end, ["collider"]),
                ]);
            }
            TestAssert.True(throwingLog.Attempts > 0,
                "collision diagnostics must attempt the source logger before isolating failure");

            void FeedOscillation(CollisionDiagnosticSink sink, int count)
            {
                for (var index = 0; index < count; index++)
                {
                    var start = new Position2(index % 2 == 0 ? 0.0 : 20.0, 0.0);
                    var end = new Position2(index % 2 == 0 ? 20.0 : 0.0, 0.0);
                    sink.OnMovementTick(0.1, [
                        new MovementDiagnosticSnapshot(101, [202], 0.0, 0, 0,
                            new Position2(2.0, 0.0), start, end, end, ["collider"],
                            0, null, false, [202], 100.0, false, 0.1),
                    ]);
                }
            }

            void FeedForward(CollisionDiagnosticSink sink, int count)
            {
                for (var index = 0; index < count; index++)
                {
                    var start = new Position2(index * 4.0, 0.0);
                    var end = new Position2(index * 4.0 + 4.0, 0.0);
                    sink.OnMovementTick(0.1, [
                        new MovementDiagnosticSnapshot(101, [], 0.0, 0, 0,
                            Position2.Zero, start, end, end, []),
                    ]);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_COLLISION", previous);
        }
    }

    private sealed class RecordingLog : ISessionLogSink
    {
        public List<LogRecord> Records { get; } = new();

        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null) =>
            Records.Add(new LogRecord(category, level, message,
                properties));
    }

    private sealed class ThrowingLog : ISessionLogSink
    {
        public int Attempts { get; private set; }

        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            Attempts++;
            throw new InvalidOperationException("diagnostic sink test failure");
        }
    }

    private sealed record LogRecord(string Category, string Level, string Message,
        IReadOnlyDictionary<string, object?>? Properties);
}
