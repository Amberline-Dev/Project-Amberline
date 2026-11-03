using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

public static class WaveCorrectionChecks
{
    [Check("movement.wave-correction-occupied-goal-keeps-single-click")]
    public static void OccupiedGoalRemainsAWait()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var goal = new Position2(7300, 7000);
        var crowd = new List<Entity>();
        for (var x = -2; x <= 2; x++)
        for (var z = -2; z <= 2; z++)
            crowd.Add(Body(runtime, goal + new Position2(x * 70, z * 70)));
        var path = runtime.Movement.SetMoveOrder(hero.NetId, goal);
        // Batch 61: the retained wait is deleted. The stock planner moves the end cell off an occupied click to the nearest free cell
        // (SetToNearestGetToAbleCell with the unit-occupancy query), so a click inside a closed crowd plans one leg to the edge of the
        // crowd, outside every body, and does not publish duplicate moving points.
        TestAssert.Equal(2, path.Count, "an unavailable destination publishes one leg to the nearest free cell, no duplicate points");
        var contact = crowd.Select(body => runtime.Movement.MoverPairRadius(hero, body) +
            runtime.Movement.PairMovementRadius(body, hero)).Min();
        TestAssert.True(crowd.All(body => Distance(path[^1], body.Position) >= contact),
            "the snapped end lies outside every body of the crowd");
        TestAssert.True(Distance(path[^1], goal) < Distance(hero.Position, goal),
            "the snapped end is nearer the click than the start");
        TestAssert.Equal(goal, runtime.Movement.RetainedHeroGoal(hero)!.Value, "the route retains the original click");
        for (var tick = 0; tick < 30 && hero.Waypoints.Count > 0; tick++) runtime.Movement.Tick(.1);
        TestAssert.True(Distance(hero.Position, path[^1]) < 5, "the hero walks to the edge of the crowd and stops there");
        // Opening the crowd re-plans nothing (the stock hero keeps its path); the player's next click reaches the point.
        foreach (var body in crowd) body.Alive = false;
        TestAssert.True(runtime.Movement.SetMoveOrder(hero.NetId, goal).Count > 1, "the same click plans once its destination is open");
        for (var tick = 0; tick < 30; tick++) runtime.Movement.Tick(.1);
        TestAssert.True(Distance(hero.Position, goal) < 5, "the same click reaches its destination when it opens");
    }

    [Check("movement.wave-correction-captured-attack-approach")]
    public static void CapturedCrowdAllowsAnAttack()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(6931, 7089);
        // Last transmitted stationary body positions around the repeated
        // zero-length Yorick routes at 145.595s in session e776235e.
        foreach (var point in new[] { new Position2(6935.776, 7192.316), new Position2(7027, 7129),
                     new Position2(7144.137, 7085.796), new Position2(7036.259, 7271.139),
                     new Position2(7114.702, 7172.279) }) Body(runtime, point);
        var target = Body(runtime, new Position2(6943, 7333), Team.Chaos);
        runtime.Spells.HeroOrders!.StartAttack(hero, target);
        var old = hero.Position;
        for (var tick = 0; tick < 120 && target.Health == 5000; tick++)
        {
            Step(runtime);
            TestAssert.True(Distance(old, hero.Position) <= MaximumCollisionStep(runtime, hero),
                $"walking cannot relocate the hero: tick={tick}, from={old}, to={hero.Position}, step={Distance(old, hero.Position)}, speed={hero.Speed}");
            old = hero.Position;
        }
        TestAssert.True(target.Health < 5000, "one attack order must find a firing point around this crowd");
    }

    [Check("movement.wave-correction-wait-is-not-an-attack-approach")]
    public static void PursuitReconsidersAWaitingRoute()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var target = Body(runtime, new Position2(7500, 7000), Team.Chaos);
        runtime.Spells.HeroOrders!.StartAttack(hero, target);
        TestAssert.True(hero.LastAttackStopPoint is not null);
        // Batch 61: the retained wait and the dynamic unit planner behind it (FindPathAroundUnitsOverride) are deleted for heroes; the
        // hero plans the stock client's route to the target's position at every scan (PlanChase) and its contact solver deals with a
        // body in the way, so there is no closed route to wait on. A ghost-proof blocker (which even a ghosted hero cannot pass) stands
        // in the line: pursuit must keep a live route past it and the original attack must still complete.
        var blocker = Body(runtime, new Position2(7300, 7000));
        blocker.GhostProof = true;
        for (var tick = 0; tick < 3; tick++) runtime.Movement.Tick(.1);
        runtime.Spells.HeroOrders.Tick(.2);
        TestAssert.True(hero.Waypoints.Count > 0, "pursuit keeps a live route at the next scan");
        TestAssert.True(hero.LastAttackStopPoint is not null, "the scan latches a usable approach endpoint");
        for (var tick = 0; tick < 100 && target.Health == 5000; tick++) Step(runtime);
        TestAssert.True(target.Health < 5000, "the original attack completes past the blocker");
    }

    [Check("movement.wave-correction-single-click-moving-bodies")]
    public static void SingleClickMovingWave()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var bodies = new[] { Body(runtime, new Position2(7240, 6910)), Body(runtime, new Position2(7250, 7100)),
            Body(runtime, new Position2(7360, 7000)), Body(runtime, new Position2(7470, 7090)) };
        var goal = new Position2(7900, 7000);
        var route = runtime.Movement.SetMoveOrder(hero.NetId, goal).ToArray();
        var predicted = route[0];
        var remaining = new Queue<Position2>(route.Skip(1));
        var updates = 0;
        var contacts = 0;
        runtime.Movement.HeroPathChanged += (_, points) =>
        {
            TestAssert.True(Distance(predicted, points[0]) < 162.5,
                $"automatic route anchor exceeds the native smoothing band: {Distance(predicted, points[0]):F1}");
            predicted = points[0]; remaining = new Queue<Position2>(points.Skip(1)); updates++;
        };
        for (var tick = 0; tick < 160 && Distance(hero.Position, goal) > 5; tick++)
        {
            for (var i = 0; i < bodies.Length; i++)
                bodies[i].Waypoints = [new Position2(bodies[i].Position.X,
                    (tick / 12 + i) % 2 == 0 ? 6800 : 7200)];
            var old = hero.Position;
            // Refresh happens at the beginning of the movement frame.
            runtime.Movement.Tick(.1);
            if (runtime.Movement.ColliderIds(hero.NetId).Count > 0) contacts++;
            var budget = hero.Speed * .1;
            while (remaining.TryPeek(out var next))
            {
                var distance = Distance(predicted, next);
                if (distance > budget)
                {
                    predicted = new Position2(predicted.X + (next.X - predicted.X) * budget / distance,
                        predicted.Z + (next.Z - predicted.Z) * budget / distance); break;
                }
                budget -= distance; predicted = remaining.Dequeue();
            }
            TestAssert.True(Distance(old, hero.Position) <= MaximumCollisionStep(runtime, hero),
                $"single-click walking exceeds its speed budget: tick={tick}, from={old}, to={hero.Position}, step={Distance(old, hero.Position)}, speed={hero.Speed}");
            TestAssert.True(Distance(predicted, hero.Position) < 162.5, "unsent collision drift reaches the native correction band");
        }
        // Batch 61: the automatic route refresh (RefreshHeroOrders) is deleted with the body-aware planner; the stock hero keeps its path
        // and the contact solver deals with the wave. The server publishes a hero's route again only when the solver's deflection passes
        // 119 units or a waypoint was bent (TrackHeroDrift, NOT RETAIL), so a wave that does not push the hero that far publishes nothing
        // (any publication is still checked against the native band above). The wave must touch the hero, though.
        TestAssert.True(contacts > 0, "moving bodies exercise the contact solver");
        TestAssert.True(updates <= (int)runtime.Movement.HeroAutomaticRoutes.Where(pair => pair.Key is "collision-resync" or "bend")
            .Sum(pair => pair.Value), "every published route is a counted collision-resync or bend");
        TestAssert.True(Distance(hero.Position, goal) < 5, "a single click survives a moving wave");
    }

    [Check("movement.wave-correction-cast-stop-sequence")]
    public static void CastStopsUseLiveSequence()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var capture = new Capture();
        runtime.Delivery.AddObserver(new ReplicationObserver(99, hero.Team, hero.NetId, capture, ready: true));
        runtime.World.Clock.SetVirtual(150);
        var route = runtime.Movement.SetMoveOrder(hero.NetId, new Position2(7900, 7000));
        var moveSync = runtime.World.Clock.SyncId();
        runtime.Spells.Effects.SendWaypoints(hero, route, moveSync);
        runtime.Delivery.Flush(); capture.Packets.Clear();
        hero.SpellRanks[1] = 1;
        var request = new byte[34]; request[0] = 0xA2; request[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), 7250);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), 7100);
        }
        TestAssert.True(runtime.Spells.HandleCastRequest(hero, request, 150), "Yorick W cast accepted");
        runtime.Delivery.Flush();
        var stops = capture.Packets.Where(packet => packet.Plaintext.Span[0] == 0xC1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToArray();
        TestAssert.True(stops.Length > 0, "cast emits a movement stop");
        foreach (var stop in stops)
            TestAssert.True(BinaryPrimitives.ReadUInt32LittleEndian(stop.Plaintext.Span[5..]) >= moveSync,
                "the client's move handler rejects a stale sequence and keeps following the old route");
    }

    [Check("movement.wave-correction-replicates-collision-without-input")]
    public static void CollisionCorrectionDoesNotNeedAnotherClick()
    {
        var runtime = Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        var goal = new Position2(7900, 7000);
        // A body outside this observer's planning knowledge still participates
        // in physical collision. The original route remains unchanged while
        // actual collision rejects movement frames underneath it.
        var blocker = Body(runtime, new Position2(7210, 7000), Team.Chaos);
        // Batch 61: a flagged hero brings 0.2 of its radius to the pair test and slides around a lone minion with a deflection far
        // below the 119 units after which the server publishes a hero's route again (TrackHeroDrift, collision-resync), so the hidden
        // body is a wall of nine, 540 units across, that the hero has to walk around.
        var wall = new List<Entity> { blocker };
        for (var k = 1; k <= 4; k++)
        {
            wall.Add(Body(runtime, new Position2(7210, 7000 + k * 60), Team.Chaos));
            wall.Add(Body(runtime, new Position2(7210, 7000 - k * 60), Team.Chaos));
        }
        foreach (var piece in wall) piece.Visible = false;
        // Preserve the original 35.7437-unit hidden movement disc under the
        // production half-NPC and minion-unit collision scales.
        const double hiddenMovementDisc = 35.7437;
        blocker.Radius = hiddenMovementDisc /
            (runtime.Movement.Settings.NpcMovementRadiusScale *
             runtime.Movement.Settings.MinionUnitCollisionScale);
        TestAssert.Near(hiddenMovementDisc,
            runtime.Movement.PairMovementRadius(blocker, hero), 1e-6,
            "the hidden collision control retains its original hero/minion movement disc");
        blocker.Visible = false;
        runtime.Movement.SetMoveOrder(hero.NetId, goal);
        IReadOnlyList<Position2>? correction = null;
        runtime.Movement.HeroPathChanged += (_, path) => correction = path;
        for (var tick = 0; tick < 45 && correction is null; tick++)
        {
            var before = hero.Position;
            runtime.Movement.Tick(.1);
            TestAssert.True(Distance(hero.Position, before) <= MaximumCollisionStep(runtime, hero), "sending correction cannot relocate the hero");
        }
        TestAssert.True(correction is not null, "collision drift must replicate during a single active order");
        TestAssert.True(Distance(correction![0], hero.Position) < 3, "wire correction anchors the current authoritative position");
        TestAssert.Equal(goal, runtime.Movement.RetainedHeroGoal(hero)!.Value, "correction preserves the requested destination");
        correction = null;
        foreach (var piece in wall) piece.Alive = false;
        runtime.Movement.Tick(.1);
        TestAssert.True(correction is null, "unchanged movement does not spam identical corrections");
    }

    // Native squared-speed governor allows a small separation acceleration
    // on each of three passes. Preserve that contract instead of treating
    // ordinary separation as a teleport.
    private static double MaximumCollisionStep(MatchRuntime runtime, Entity hero) =>
        hero.Speed * .1 * Math.Pow(Math.Sqrt(1 + runtime.Movement.Settings.Collision.SpeedDeadband), 3) + 3;

    private static void Step(MatchRuntime runtime)
    {
        runtime.Spells.HeroOrders!.Tick(.1);
        runtime.Movement.Tick(.1);
        runtime.Combat.CombatTick(.1, regenerate: false);
    }

    private static MatchRuntime Start()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Yorick", solo: true, originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0);
        runtime.World.Clock.SetVirtual(0); runtime.Session.CreateWorld();
        runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1));
        runtime.Session.Spawn(1); runtime.Session.MarkReady(1); runtime.Session.Start();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(7000, 7000); hero.HeroAutoAcquire = false;
        hero.Waypoints.Clear(); hero.Target = null;
        return runtime;
    }

    private static Entity Body(MatchRuntime runtime, Position2 point, Team team = Team.Order) => runtime.World.Register(
        new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, team, point)
        { Name = "Minion_Basic", Created = true, Visible = true, Health = 5000, MaxHealth = 5000, Radius = 35.744, Speed = 325 });
    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
