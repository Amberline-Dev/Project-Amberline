using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class HeroMovementCadenceChecks
{
    [Check("movement.forced-move-starts-at-its-own-start-time")]
    public static void ForcedMoveStartsAtItsOwnStartTime()
    {
        foreach (var heroMove in new[] { true, false })
        foreach (var firstCollision in new[] { false, true })
        {
            using var runtime = Start();
            var hero = runtime.Session.Participants.Single().Hero!;
            hero.Position = new Position2(7000, 7000);
            hero.HeroAutoAcquire = false;
            var mover = heroMove ? hero : Body(runtime, new Position2(7000, 7300));
            var origin = mover.Position;
            var cadence = heroMove ? MovementSystem.HeroTickSeconds : MovementSystem.FixedTickSeconds;
            runtime.Tick(cadence - .01);
            var move = new ForcedMoveState(origin + new Position2(58, 0), 1745, 2,
                hero.NetId, "start-time-check", firstCollision: firstCollision);
            TestAssert.True(runtime.Movement.StartForcedMove(mover.NetId, move));

            runtime.Tick(.01);
            TestAssert.Near(origin.X + 17.45, mover.Position.X, 1e-8,
                "only the final 10 ms of the accumulated frame belongs to this move");
            TestAssert.Near(origin.Z, mover.Position.Z, 1e-8);
            TestAssert.Near(1.99, move.Remaining, 1e-9);
            TestAssert.False(move.Completed,
                "the short dash must not finish using time before its start");
            runtime.Tick(cadence);
            TestAssert.True(move.Completed && move.Succeeded);
            TestAssert.Equal(move.Destination, mover.Position);
        }

        foreach (var heroMove in new[] { true, false })
        {
            using var runtime = Start();
            var hero = runtime.Session.Participants.Single().Hero!;
            hero.Position = new Position2(7000, 7000);
            hero.HeroAutoAcquire = false;
            var mover = heroMove ? hero : Body(runtime, new Position2(7000, 7300));
            var origin = mover.Position;
            var cadence = heroMove ? MovementSystem.HeroTickSeconds : MovementSystem.FixedTickSeconds;
            runtime.Tick(cadence - .01);
            TestAssert.True(runtime.Movement.StartForcedMove(mover.NetId,
                origin + new Position2(1000, 0), 1000, 1, hero.NetId, "catch-up-check"));
            var move = mover.ForcedMove!;
            var catchUp = 2 * cadence + .01;
            // Bypass MatchRuntime's 30-Hz subdivision to exercise several
            // accumulated movement steps in one production-driver call.
            runtime.Dependencies.Simulation.Tick(catchUp);
            TestAssert.Near(origin.X + 1000 * catchUp, mover.Position.X, 1e-8,
                "catch-up consumes the first partial step and both later full steps");
            TestAssert.Near(1 - catchUp, move.Remaining, 1e-9);
            TestAssert.False(move.Completed);

            runtime.Tick(cadence - .01);
            var replacementOrigin = mover.Position;
            var replacement = new ForcedMoveState(replacementOrigin + new Position2(58, 0),
                1745, 2, hero.NetId, "replacement-check");
            TestAssert.True(runtime.Movement.StartForcedMove(mover.NetId, replacement));
            runtime.Tick(.01);
            TestAssert.Near(replacementOrigin.X + 17.45, mover.Position.X, 1e-8,
                "a replacement move records its own start within the next frame");
            TestAssert.Near(1.99, replacement.Remaining, 1e-9);
            TestAssert.False(replacement.Completed);
        }
    }

    [Check("movement.forced-move-partial-step-collision-landing")]
    public static void ForcedMovePartialStepReachesAnnouncedCollisionLanding()
    {
        foreach (var heroMove in new[] { true, false })
        foreach (var cadence in new[] { .016, .05, .1, .4 })
        foreach (var obstacle in new[] { "clear", "wall", "tower" })
        {
            using var runtime = Start();
            var hero = runtime.Session.Participants.Single().Hero!;
            hero.HeroAutoAcquire = false;
            hero.Position = new Position2(7000, 7300);
            var origin = new Position2(obstacle == "wall" ? 7600 : 7000, 7000);
            var mover = heroMove ? hero : Body(runtime, origin);
            mover.Position = origin;
            if (obstacle == "tower")
                runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
                    EntityKind.Turret, UnitType.Turret, Team.Order, new Position2(7400, 7000))
                { Alive = true, Radius = MapDefinitions.TurretRadius, Health = 5000, MaxHealth = 5000 });
            var interval = heroMove ? MovementSystem.HeroTickSeconds : MovementSystem.FixedTickSeconds;
            runtime.Tick(interval - .01);
            var destination = origin + new Position2(475, 0);
            const double duration = 475.0 / 2000.0 + .05;
            var announced = runtime.Movement.FirstCollisionDashLanding(origin,
                destination, 2000, duration, mover);
            var move = new ForcedMoveState(destination, 2000, duration, hero.NetId,
                "partial-collision-check", firstCollision: true);
            TestAssert.True(runtime.Movement.StartForcedMove(mover.NetId, move, cancelOrder: true));
            runtime.Tick(.01);
            var direction = announced - origin;
            var length = Distance(origin, announced);
            TestAssert.Near(origin.X + direction.X / length * 20, mover.Position.X, 1e-8,
                "collision projection does not consume time before the move starts");
            TestAssert.Near(origin.Z + direction.Z / length * 20, mover.Position.Z, 1e-8);
            TestAssert.Near(duration - .01, move.Remaining, 1e-9);
            TestAssert.False(move.Completed);
            TestAssert.Equal(announced, runtime.Movement.FirstCollisionDashLanding(mover, move),
                "the fog preview retains the same landing after a partial first step");
            for (var tick = 0; tick < 100 && !move.Completed; tick++)
                // The 400-ms driver call exercises multiple catch-up movement frames.
                runtime.Dependencies.Simulation.Tick(cadence);
            TestAssert.True(move.Completed, $"{obstacle}: the move completes at cadence {cadence}");
            TestAssert.Equal(obstacle == "clear", move.Succeeded,
                "only arrival at the raw destination succeeds; collision retains its callback");
            TestAssert.Equal(destination, move.Destination, "the raw request survives collision");
            TestAssert.Equal(announced, mover.Position,
                "completion reaches the announced destination at every cadence");
            TestAssert.True(runtime.Navigation.IsMovementWalkable(mover.Position));
        }
    }

    [Check("movement.production-hero-50ms-npc-100ms")]
    public static void ProductionUsesSeparateMovementClocks()
    {
        var diagnostics = new ElapsedRecorder();
        using var runtime = Start(diagnostics);
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(7000, 7000);
        hero.Speed = 309;
        hero.HeroAutoAcquire = false;
        hero.Waypoints = [new Position2(7300, 7000)];
        var minion = Body(runtime, new Position2(7000, 7300));
        minion.Waypoints = [new Position2(7300, 7300)];
        minion.ForcedMoveGrace = 0.4;
        minion.StuckElapsed = 0.2;
        minion.RepathCount = 2;
        var npcFrames = 0;
        runtime.Movement.FixedStepCompleted += (dt, steps) =>
        {
            npcFrames++;
            TestAssert.Near(0.1, dt, 1e-9);
            TestAssert.True(steps.Any(step => step.NetId == minion.NetId));
        };

        runtime.Tick(1.0 / 30.0);
        TestAssert.Equal(new Position2(7000, 7000), hero.Position);
        runtime.Tick(1.0 / 30.0);
        TestAssert.Near(7000 + 309 * 0.05, hero.Position.X, 0.01,
            "hero moves after the first 50-ms budget");
        TestAssert.Equal(new Position2(7000, 7300), minion.Position,
            "NPC position stays at its 100-ms frame start");
        TestAssert.Near(0.4, minion.ForcedMoveGrace, 1e-9);
        TestAssert.Near(0.2, minion.StuckElapsed, 1e-9);
        TestAssert.Equal(2, minion.RepathCount);
        TestAssert.Equal(0, npcFrames);

        runtime.Tick(1.0 / 30.0);
        TestAssert.Near(7000 + 309 * 0.1, hero.Position.X, 0.01,
            "the second hero step consumes exactly the remaining 50 ms");
        TestAssert.Near(7000 + 325 * 0.1, minion.Position.X, 0.01,
            "NPC moves once for the same elapsed 100 ms");
        TestAssert.Equal(1, npcFrames);
        TestAssert.Equal(new Position2(7300, 7000), hero.Waypoints[0],
            "the active walking route survives both hero steps");
        TestAssert.Equal(3, diagnostics.Frames.Count);
        TestAssert.Near(.05, diagnostics.Frames[0].Dt, 1e-9);
        TestAssert.Near(.05, diagnostics.Frames[0].Elapsed, 1e-9);
        TestAssert.Near(.05, diagnostics.Frames[1].Dt, 1e-9);
        TestAssert.Near(.1, diagnostics.Frames[1].Elapsed, 1e-9);
        TestAssert.Near(.1, diagnostics.Frames[2].Dt, 1e-9);
        TestAssert.Near(.1, diagnostics.Frames[2].Elapsed, 1e-9,
            "the NPC phase shares elapsed time with the second hero phase");
    }

    [Check("movement.production-split-collision-and-forced-move")]
    public static void HalfStepKeepsNpcAsColliderAndDoesNotAdvanceItsForcedMove()
    {
        using var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(7000, 7000);
        hero.Speed = 309;
        hero.HeroAutoAcquire = false;
        hero.Waypoints = [new Position2(7300, 7000)];
        var blocker = Body(runtime, new Position2(7100, 7000));
        // Batch 61: a flagged hero brings 0.2 of its radius to the pair test, so the distance at which the solver stops it against the
        // blocker is ContactClearance (0.2 r + r + skin), not the full-width BodyClearance of the retired kernel.
        var clearance = runtime.Movement.ContactClearance(hero, blocker);
        blocker.Position = new Position2(7000 + clearance + 5, 7000);
        var blockerStart = blocker.Position;
        var minion = Body(runtime, new Position2(7000, 7300));
        var landing = new Position2(7000, 7400);
        var forced = new ForcedMoveState(landing, 1000, 0.1, hero.NetId, "cadence-check");
        TestAssert.True(runtime.Movement.StartForcedMove(minion.NetId, forced));

        runtime.Tick(1.0 / 30.0);
        runtime.Tick(1.0 / 30.0);
        TestAssert.Equal(blockerStart, blocker.Position);
        TestAssert.Equal(new Position2(7000, 7300), minion.Position);
        TestAssert.False(forced.Completed);
        TestAssert.True(5 < hero.Speed * MovementSystem.HeroTickSeconds,
            "the unopposed hero step would cross the blocker shell");
        TestAssert.True(Distance(hero.Position, blocker.Position) + 1 >=
            clearance,
            "hero collision still sees the NPC during its 50-ms half-step");

        runtime.Tick(1.0 / 30.0);
        TestAssert.True(forced.Completed && forced.Succeeded);
        TestAssert.Equal(landing, minion.Position,
            "NPC forced move consumes exactly one 100-ms frame");
        runtime.Tick(1.0 / 30.0);
        runtime.Tick(1.0 / 30.0);
        TestAssert.Equal(landing, minion.Position);
        TestAssert.Near(0.5, minion.ForcedMoveGrace, 1e-9,
            "the intervening hero half-step does not age NPC forced-move grace");
    }

    private static MatchRuntime Start(IMovementDiagnosticSink? diagnostics = null)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Orianna", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0,
            movementDiagnostics: diagnostics);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null);
        runtime.Session.Spawn(1);
        runtime.Session.MarkReady(1);
        TestAssert.True(runtime.Session.Start());
        return runtime;
    }

    private static Entity Body(MatchRuntime runtime, Position2 position) => runtime.World.Register(
        new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Order, position)
        {
            Alive = true, Created = true, Visible = true, Speed = 325,
            Radius = 35.744, Health = 1000, MaxHealth = 1000
        });

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Z - second.Z, 2));

    private sealed class ElapsedRecorder : IMovementDiagnosticSink
    {
        public List<(double Dt, double Elapsed)> Frames { get; } = [];
        public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots) { }
        public void OnMovementTick(double dt, double elapsed,
            IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
            Frames.Add((dt, elapsed));
    }
}
