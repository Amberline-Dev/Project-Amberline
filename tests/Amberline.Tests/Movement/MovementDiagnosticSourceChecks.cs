using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class MovementDiagnosticSourceChecks
{
    [Check("movement.diagnostics-split-clock", Serial = true)]
    public static void SplitHeroAndNpcFramesShareElapsedTimeWithoutLosingNpcBudget()
    {
        WithLogging(() =>
        {
            var world = new WorldState();
            var actor = world.Register(new Entity(0x4000D100, EntityKind.Minion,
                UnitType.Minion, Team.Order, Position2.Zero)
            {
                Alive = true, Radius = 35.744, Speed = 325,
            });
            var log = new RecordingLog();
            using var sink = new CollisionDiagnosticSink(world, log);

            sink.OnMovementTick(.05, .05, []);
            sink.OnMovementTick(.05, .1, []);
            actor.Position = new Position2(10, 0);
            sink.OnMovementTick(.1, .1,
                [Snapshot(actor, Position2.Zero, actor.Position)]);
            TestAssert.Near(.1, sink.DiagnosticElapsed, 1e-9,
                "the NPC frame shares the second hero frame's elapsed time");

            // Even if a caller catches up several hero frames first, this
            // actor's cross-step budget uses its own 100-ms observation gap.
            sink.OnMovementTick(.05, .15, []);
            sink.OnMovementTick(.05, .2, []);
            sink.OnMovementTick(.05, .25, []);
            sink.OnMovementTick(.05, .3, []);
            actor.Position = new Position2(50, 0);
            sink.OnMovementTick(.1, .2,
                [Snapshot(actor, new Position2(40, 0), actor.Position)]);
            TestAssert.Near(.3, sink.DiagnosticElapsed, 1e-9,
                "a late NPC observation cannot rewind the shared trace clock");
            var displacement = log.Events("movement_event").Single(record =>
                Equals(record.Properties["kind"], "authoritative-displacement"));
            TestAssert.False((bool)displacement.Properties["within_step"]!);
            TestAssert.True((bool)displacement.Properties["cross_step"]!);
            TestAssert.Equal(32.5, displacement.Properties["cross_step_budget"]!);
        });
    }

    [Check("movement.diagnostics-authoritative-displacement", Serial = true)]
    public static void OversizedDisplacementCapturesFactsWithoutChangingAuthority()
    {
        WithLogging(() =>
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0.0);
            var actor = new Entity(0x4000D101, EntityKind.Minion, UnitType.Minion,
                Team.Order, Position2.Zero)
            {
                Alive = true,
                Created = true,
                Visible = true,
                Name = "Blue_Minion_MechCannon",
                MinionType = MinionType.Siege,
                MinionLane = "mid",
                Radius = 55.7437,
                Speed = 325.0,
                Waypoints = [new Position2(1000.0, 0.0)],
            };
            world.Register(actor);
            var log = new RecordingLog();
            using var sink = new CollisionDiagnosticSink(world, log);

            for (var index = 0; index < 19; index++)
            {
                var start = new Position2(index * 10.0, 0.0);
                var committed = new Position2((index + 1) * 10.0, 0.0);
                actor.Position = committed;
                sink.OnMovementTick(.1, [Snapshot(actor, start, committed)]);
            }

            var before = new Position2(190.0, 0.0);
            var committedJump = new Position2(700.0, 0.0);
            actor.Position = committedJump;
            sink.OnMovementTick(.1, [Snapshot(actor, before, committedJump,
                collision: new Position2(450.0, 0.0))]);

            TestAssert.Equal(committedJump, actor.Position,
                "diagnostics must not rewrite authoritative position");
            var displacement = log.Events("movement_event").Single(record =>
                Equals(record.Properties["kind"], "authoritative-displacement"));
            TestAssert.Equal("observed-authoritative-displacement",
                displacement.Properties["diagnostic"]!);
            TestAssert.Equal(new[] { 190.0, 0.0 },
                (double[])displacement.Properties["before"]!);
            TestAssert.Equal(new[] { 700.0, 0.0 },
                (double[])displacement.Properties["committed"]!);
            TestAssert.Equal(new[] { 450.0, 0.0 },
                (double[])displacement.Properties["collision"]!);
            TestAssert.True((bool)displacement.Properties["within_step"]!);
            TestAssert.True((bool)displacement.Properties["cross_step"]!);
            TestAssert.Equal("none", displacement.Properties["forced_move_status"]!);
            TestAssert.Equal("Blue_Minion_MechCannon",
                displacement.Properties["unit_name"]!);
            TestAssert.Equal("SIEGE", displacement.Properties["minion_type"]!);
            TestAssert.Equal("ORDER", displacement.Properties["team"]!);
            TestAssert.Equal("mid", displacement.Properties["lane"]!);

            // One bounded detail request is served through the existing
            // twenty-frame ring and remains inside the existing envelope.
            var detail = log.Events("osc").Single();
            TestAssert.Equal(20, ((object[])detail.Properties["frame_context"]!).Length);
            TestAssert.True(Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(detail.Properties)) < CollisionDiagnosticSink.DetailLimitBytes);
            TestAssert.Equal(0, sink.PendingDetailCount);

            // A persistent oversized condition is edge-gated; it cannot
            // consume a detail slot on every fixed frame.
            sink.OnMovementTick(.1, [Snapshot(actor, before, committedJump)]);
            TestAssert.Equal(1, log.Events("movement_event").Count(record =>
                Equals(record.Properties["kind"], "authoritative-displacement")));

            // Clear the edge, then inject a cross-step displacement whose
            // current frame itself stays inside the movement budget.
            var normal = new Position2(710.0, 0.0);
            actor.Position = normal;
            sink.OnMovementTick(.1, [Snapshot(actor, committedJump, normal)]);
            var crossBefore = new Position2(1190.0, 0.0);
            var crossCommitted = new Position2(1200.0, 0.0);
            actor.Position = crossCommitted;
            sink.OnMovementTick(.1, [Snapshot(actor, crossBefore, crossCommitted)]);
            var cross = log.Events("movement_event").Last(record =>
                Equals(record.Properties["kind"], "authoritative-displacement"));
            TestAssert.False((bool)cross.Properties["within_step"]!);
            TestAssert.True((bool)cross.Properties["cross_step"]!);
            TestAssert.Equal(1, log.Events("osc").Count(),
                "the ten-second detail cooldown must remain in force");
            TestAssert.Equal(1, sink.PendingDetailCount,
                "a rate-limited outlier remains pending for a later bounded slot");

            actor.Alive = false;
            sink.OnMovementTick(.1, []);
            TestAssert.Equal(0, sink.RetainedActorCount,
                "dead actors must prune movement history");
            TestAssert.Equal(0, sink.PendingDetailCount,
                "dead actors must prune pending displacement details");
        });
    }

    private static MovementDiagnosticSnapshot Snapshot(Entity actor, Position2 start,
        Position2 committed, Position2? collision = null) =>
        new(actor.NetId, [], 0, 0, 0, Position2.Zero, start, committed, committed,
            [], CollisionCandidatePosition: collision,
            RequestedGoal: actor.Waypoints.Count > 0
                ? actor.Waypoints[^1] : (Position2?)null);

    private static void WithLogging(Action action)
    {
        var previous = Environment.GetEnvironmentVariable("LOG_COLLISION");
        try
        {
            Environment.SetEnvironmentVariable("LOG_COLLISION", "1");
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_COLLISION", previous);
        }
    }

    private sealed class RecordingLog : ISessionLogSink
    {
        public List<Record> Records { get; } = [];

        public IEnumerable<Record> Events(string message) =>
            Records.Where(record => record.Message == message);

        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null) =>
            Records.Add(new Record(message,
                new Dictionary<string, object?>(properties ?? new Dictionary<string, object?>())));
    }

    private sealed record Record(string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class PassiveCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive;
        public bool IsInAttackRange(Entity attacker, Entity target) => false;
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class AlwaysVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class RecordingPackets : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => true;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints,
            long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }
}
