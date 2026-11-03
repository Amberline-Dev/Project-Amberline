using Amberline.Core;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class WaveRetargetResyncChecks
{
    [Check("minions.annie-0525-collision-resync")]
    public static void MovingCollisionDoesNotRepublishEveryFrame()
    {
        // Annie's top wave at HUD 05:25: the native three-pass collision
        // governor permits this 52.4u step from a 32.5u march budget. The
        // difference is separation from a collision-free march, not proof
        // that the stock client's collision path needs a new C1: no separation
        // size ever publishes one (the 119-unit prediction resync was deleted,
        // owner decision 16 Oct 2026, because every minion C1 is a hard snap).
        var fixture = NewResyncFixture(0x40018A25, MinionAiState.March);
        var start = fixture.Entity.Position;
        var goal = new Position2(start.X + 300.0, start.Z + 900.0);
        var capturedStep = new Position2(16.4052554753492, 49.7665047510089);
        var baseStep = new Position2(
            fixture.Entity.Speed * MovementSystem.FixedTickSeconds /
                Math.Sqrt(10.0),
            fixture.Entity.Speed * MovementSystem.FixedTickSeconds * 3.0 /
                Math.Sqrt(10.0));
        fixture.Entity.Waypoints = [goal];
        fixture.Entity.AnchorPath = [start, goal];
        fixture.Entity.LastAnchorTime = 0.0;
        fixture.Ai.Tick(0.0);

        for (var index = 1; index <= 5; index++)
        {
            fixture.Movement.Tick(MovementSystem.FixedTickSeconds);
            fixture.Entity.Position = new Position2(
                start.X + index * capturedStep.X,
                start.Z + index * capturedStep.Z);
            fixture.Entity.SeparationAccumulation = new Position2(
                index * (capturedStep.X - baseStep.X),
                index * (capturedStep.Z - baseStep.Z));
            fixture.Ai.Tick(MovementSystem.FixedTickSeconds);
            TestAssert.Equal(0, fixture.Packets.Orders.Count,
                $"sub-threshold collision frame {index} republished a live C1");
        }

        fixture.Movement.Tick(MovementSystem.FixedTickSeconds);
        // Far past the old 119-unit threshold: still silent.
        for (var index = 6; index <= 40; index++)
        {
            fixture.Entity.Position = new Position2(
                start.X + index * capturedStep.X,
                start.Z + index * capturedStep.Z);
            fixture.Entity.SeparationAccumulation = new Position2(
                index * (capturedStep.X - baseStep.X),
                index * (capturedStep.Z - baseStep.Z));
            fixture.Ai.Tick(MovementSystem.FixedTickSeconds);
        }
        TestAssert.True(Distance(fixture.Entity.SeparationAccumulation, Position2.Zero) > 119.0,
            "the fixture no longer drifts past the deleted prediction threshold");
        TestAssert.Equal(0, fixture.Packets.Orders.Count,
            "a drifting march must not publish a prediction-resync C1");
    }
}
