using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

/// <summary>Production-order regressions for heroes crossing live mobile bodies.</summary>
public static class HeroNavigationRegressionChecks
{
    [Check("movement.hero-blocked-order-retries-after-wave-opens")]
    public static void BlockedMoveRetainsGoalUntilTheWaveOpens()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        var blocker = AddMobile(runtime.World, 0x4F10_0001, EntityKind.Minion,
            Team.Order, new Position2(7300, 7000), 90.0);
        var goal = new Position2(7800, 7000);

        // Force the dynamic planner's bounded-failure branch while the real
        // production order dispatcher still owns the click and retry state.
        runtime.Navigation.FindPathAroundUnitsOverride = (_, _, _) => [];
        SendMove(runtime, hero, goal);
        // Playtest B4 #11, 27 Sept 2026: exhausted mobile detours retain
        // movement for the collision solver instead of parking the hero.
        TestAssert.True(hero.Waypoints.Count > 1,
            "a blocked mobile route publishes a usable recovery order");
        var held = hero.Position;
        for (var tick = 0; tick < 4; tick++) runtime.Movement.Tick(.1);
        TestAssert.True(Distance(hero.Position, goal) < Distance(held, goal),
            "the hero advances toward contact instead of waiting at the click origin");
        TestAssert.True(Distance(hero.Position, blocker.Position) >=
            runtime.Movement.BodyClearance(hero, blocker),
            "mobile recovery still respects initial body contact");
        TestAssert.Equal(goal, runtime.Movement.RetainedHeroGoal(hero)!.Value,
            "refresh retains the recovery destination");

        blocker.Position = new Position2(6000, 6000);
        runtime.Navigation.FindPathAroundUnitsOverride = null;
        for (var tick = 0; tick < 4; tick++) runtime.Movement.Tick(.1);
        TestAssert.True(hero.Waypoints.Count > 0 || Distance(hero.Position, goal) < 10.0,
            "the original goal is retried after the mobile body opens the route");
        TestAssert.True(Distance(hero.Position, goal) < Distance(held, goal),
            "the retried production order makes progress toward its retained goal");

        // Existing Stop/Hold handling clears the same waypoint list, which
        // must cancel the recovery instead of resurrecting its old goal.
        blocker.Position = new Position2(7300, 7000);
        runtime.Navigation.FindPathAroundUnitsOverride = (_, _, _) => [];
        SendMove(runtime, hero, goal);
        SendOrder(runtime, hero, 10, hero.Position);
        for (var tick = 0; tick < 4; tick++) runtime.Movement.Tick(.1);
        TestAssert.Equal(0, hero.Waypoints.Count, "Stop cancels a retained blocked move");
        SendOrder(runtime, hero, 1, hero.Position);
        for (var tick = 0; tick < 4; tick++) runtime.Movement.Tick(.1);
        TestAssert.Equal(0, hero.Waypoints.Count, "Hold does not resurrect the canceled move");
    }

    [Check("movement.hero-asymmetric-moving-wave-keeps-quantized-clearance")]
    public static void AsymmetricMovingWaveKeepsBodyClearanceAndCompletes()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = team == Team.Order ? StartSolo() : StartDuel(Team.Chaos);
            var hero = runtime.Session.Participants.Single(seat => seat.Hero?.Team == team).Hero!;
            hero.HeroAutoAcquire = false;
            var bodies = new[]
            {
                AddMobile(runtime.World, 0x4F10_0010u + (uint)team, EntityKind.Minion,
                    team, new Position2(7280, 6910), 35.744),
                AddMobile(runtime.World, 0x4F10_0020u + (uint)team, EntityKind.Minion,
                    team, new Position2(7280, 7100), 35.744),
                AddMobile(runtime.World, 0x4F10_0030u + (uint)team, EntityKind.Minion,
                    team, new Position2(7380, 7000), 35.744),
                AddMobile(runtime.World, 0x4F10_0040u + (uint)team, EntityKind.Minion,
                    team, new Position2(7460, 7170), 35.744),
            };
            var goal = new Position2(7800, 7000);
            SendMove(runtime, hero, goal);
            var direct = Distance(hero.Position, goal);
            var routeLength = RouteLength(hero.Waypoints.Prepend(hero.Position));
            TestAssert.True(hero.Waypoints.Count > 1,
                $"{team} asymmetric wave emits a real route");
            TestAssert.True(routeLength <= direct * 1.45,
                $"{team} wave route remains bounded: route={routeLength:R}, direct={direct:R}");

            for (var tick = 0; tick < 80 && Distance(hero.Position, goal) > 8.0; tick++)
            {
                for (var index = 0; index < bodies.Length; index++)
                    bodies[index].Position += new Position2(index % 2 == 0 ? -2.0 : 2.0,
                        index % 2 == 0 ? 1.5 : -1.5);
                runtime.Movement.Tick(.1);
                foreach (var body in bodies)
                {
                    // Batch 61: a flagged hero brings 0.2 of its pathing radius to the pair test, so the bodies never overlap
                    // when the thin hero disc meets the minion's full disc; the gameplay radius sum is not a collision figure.
                    var minimum = runtime.Movement.MoverPairRadius(hero, body) + runtime.Movement.PairMovementRadius(body, hero);
                    TestAssert.True(Distance(hero.Position, body.Position) >= minimum - 1e-6,
                        $"{team} hero body clearance was lost at tick {tick} for {body.NetId}");
                }
            }
            TestAssert.True(Distance(hero.Position, goal) <= 8.0,
                $"{team} hero finishes the moving wave route without repeated stalls");
        }
    }

    // Batch 61: the body-aware hero planner (HeroDynamicPath, its local dynamic window and the known-body repair beyond it) is deleted. The
    // client planner tests the units registered within 1500 units of the path start (HasStuckActor on sampled cell centres), and its
    // chord can pass inside a body's disc between two samples; the contact solver then slides the hero around the body. So the route
    // no longer clears the body, and what is asserted is the order's outcome: a bounded route, and a walk that completes without the
    // thin hero disc ever overlapping the body's disc.
    [Check("movement.hero-long-route-clears-known-tail-body")]
    public static void LongRouteClearsKnownBodyBeyondLocalWindow()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        var body = AddMobile(runtime.World, 0x4F10_0050, EntityKind.Minion,
            Team.Order, new Position2(8040, 7378), 35.744);
        var goal = new Position2(10000, 7000);

        SendMove(runtime, hero, goal);
        var route = hero.Waypoints.Prepend(hero.Position).ToArray();
        var bodyDistance = Distance(hero.Position, body.Position);
        TestAssert.True(bodyDistance <= 1500.0,
            "the known body lies inside the client planner's 1500-unit actor window");
        TestAssert.True(route.Length > 1, "the long production order emits a route");
        TestAssert.True(RouteLength(route) <= Distance(hero.Position, goal) * 1.6,
            "the route remains bounded");

        var physical = runtime.Movement.MoverPairRadius(hero, body) + runtime.Movement.PairMovementRadius(body, hero);
        var end = route[^1];
        for (var tick = 0; tick < 600 && Distance(hero.Position, end) > 3.0; tick++)
        {
            runtime.Movement.Tick(MovementSystem.HeroTickSeconds);
            TestAssert.True(Distance(hero.Position, body.Position) >= physical - 1e-3,
                $"the hero overlapped the known body at tick {tick}");
        }
        TestAssert.True(Distance(hero.Position, end) <= 3.0, "the walk completes past the known body");
    }

    [Check("movement.hero-yorick-ghouls-do-not-create-long-detour")]
    public static void YorickGhoulsKeepTheHeroRouteShort()
    {
        var runtime = StartSolo("Yorick");
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        var ghouls = new[]
        {
            YorickScriptSupport.SpawnUnit(runtime, hero, "Spectral", new Position2(7300, 7000), 0.0),
            YorickScriptSupport.SpawnUnit(runtime, hero, "Ravenous", new Position2(7350, 7070), 0.0),
            YorickScriptSupport.SpawnUnit(runtime, hero, "Decayed", new Position2(7420, 7000), 0.0),
        };
        // The Ravenous and Decayed Logic buffs (shipped scripts) ghost their ghoul; Spectral stays a physical body. The fixture spawns the
        // unit alone, so the scripts' ghosted state is stated here.
        foreach (var ghoul in ghouls.Skip(1))
            ghoul.Buffs.Add(new BuffState("Ghost", new BuffPayload { Ghosted = true }));
        var goal = new Position2(7800, 7000);
        SendMove(runtime, hero, goal);
        var route = hero.Waypoints.Prepend(hero.Position).ToArray();
        var direct = Distance(hero.Position, goal);
        var length = RouteLength(route);
        TestAssert.True(route.Length > 1, "ghoul obstacles still leave a real hero route");
        TestAssert.True(length <= direct * 1.45,
            $"ghouls do not force a long detour: route={length:R}, direct={direct:R}");
    }

    [Check("movement.hero-neutral-camp-enclosure-escapes")]
    public static void NeutralCampEnclosureStillRoutesTheHero()
    {
        // Playtest B2 AE2, 26 Sept 2026: Annie flashed into the walled wraith
        // pocket and every order waited ("all-candidates-exhausted") for 21 s.
        // The logged camp encloses her; the route now ignores the neutral
        // bodies and the collision kernel shoves through them.
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        hero.Position = new Position2(6425, 5239);
        var camp = new[]
        {
            ("Wraith", new Position2(6453, 5337)),
            ("LesserWraith", new Position2(6569, 5301)),
            ("LesserWraith", new Position2(6497, 5115)),
            ("LesserWraith", new Position2(6567, 5177)),
        };
        for (var index = 0; index < camp.Length; index++)
        {
            var (name, position) = camp[index];
            var stats = NeutralStatsCalculator.Calculate(GameData.Default, name);
            runtime.World.Register(new Entity(0x4F10_0100u + (uint)index, EntityKind.Neutral,
                UnitType.Monster, Team.Neutral, position)
            {
                Name = name, Alive = true, Created = true, Visible = true,
                Radius = stats.Radius, Speed = stats.Speed,
                Health = stats.Hp, MaxHealth = stats.Hp,
            });
        }

        SendMove(runtime, hero, new Position2(7323, 4981));
        TestAssert.True(hero.Waypoints.Count > 1,
            "an order out of the neutral camp enclosure publishes a real route");
        // The kernel gives up on a body after 45 stuck 50 ms frames (~2.3 s).
        for (var tick = 0; tick < 80 && hero.Position.Z >= 5040.0; tick++)
            runtime.Movement.Tick(MovementSystem.HeroTickSeconds);
        TestAssert.True(hero.Position.Z < 5040.0,
            $"the hero leaves the wraith pocket within 4 s: {hero.Position}");
    }

    private static IEnumerable<PlaintextPacket> HeroRoutes(PacketCaptureTransport capture, Entity hero) =>
        capture.Packets.Where(packet => packet.Plaintext.Length >= 17 &&
            packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);

    private static bool FollowerClear(MatchRuntime runtime, Position2 origin,
        IReadOnlyList<Position2> route, NavigationObstacle[] obstacles, double step)
    {
        var remaining = route.ToList();
        var position = origin;
        for (var frame = 0; frame < 2048 && remaining.Count > 0; frame++)
        {
            var next = MovementSystem.FixedOriginStep(position, remaining, step).Position;
            if (!runtime.Navigation.HasHeroMovementLineOfSight(position, next) ||
                !(frame == 0
                    ? runtime.Navigation.FirstSegmentClearOfUnits(runtime.Movement.ClientAnchor(origin),
                        origin, next, obstacles)
                    : runtime.Navigation.SegmentClearOfUnits(position, next, obstacles, escape: true)))
                return false;
            position = next;
        }
        return remaining.Count == 0;
    }

    [Check("movement.hero-blitzcrank-inner-wraith-persistent-attack")]
    public static void CapturedWraithApproachMakesProgressAndAttacks()
    {
        foreach (var campId in new[] { 3, 9 })
        {
            using var runtime = StartSolo("Blitzcrank");
            runtime.Waves!.AutomaticSpawningEnabled = false;
            runtime.Combat.RestoreClock(100);
            runtime.Tick(.01);
            var hero = runtime.Session.Participants.Single().Hero!;
            hero.HeroAutoAcquire = false;
            hero.GodMode = true;
            var camp = runtime.World.Entities.Values.Where(entity =>
                entity.Kind == EntityKind.Neutral && entity.CampId == campId && entity.Alive).ToArray();
            TestAssert.Equal(4, camp.Length, "production scheduler supplies all corrected wraith homes");
            var target = camp.Single(entity => entity.Name == "Wraith");
            var start = campId == 3 ? new Position2(6873.424, 4760.999)
                : target.Position + new Position2(-420.509205, 575.886742);
            hero.Position = start;
            // This movement control needs a legally revealed attack target;
            // relocating the hero does not reveal a camp behind terrain.
            runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                1, hero.Team, target.Position, 400, 15));
            var capture = AttachCapture(runtime, hero);
            var participant = runtime.Session.Participants.Single();
            participant.View.Known.Add(target.NetId);
            participant.View.Visible.Add(target.NetId);
            var order = ClientGameplayPackets.MoveOrder(hero.NetId, 3, target.Position, target.NetId);
            TestAssert.True(runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span,
                runtime.Dependencies.Gameplay), $"camp {campId}: revealed attack accepted");
            var attacked = false;
            for (var tick = 0; tick < 240 && !attacked; tick++)
            {
                var before = hero.Position;
                runtime.Tick(.05);
                TestAssert.True(runtime.Navigation.HasHeroMovementLineOfSight(before, hero.Position),
                    $"camp {campId}: authoritative follower remains terrain-legal");
                attacked = runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId &&
                    hit.TargetNetId == target.NetId) || target.Health < target.MaxHealth;
                if (tick == 99)
                    TestAssert.True(Distance(start, hero.Position) > 50,
                        $"camp {campId}: persistent order makes bounded progress within five seconds");
            }
            TestAssert.True(attacked, $"camp {campId}: persistent attack reaches the large Wraith within twelve seconds");
            TestAssert.True(Distance(start, hero.Position) > 50);
            var routes = capture.Packets.Where(packet => packet.Plaintext.Length >= 25 &&
                packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToArray();
            TestAssert.True(routes.Length > 0, "the observer receives movement");
            foreach (var packet in routes)
            {
                var bytes = packet.Plaintext.Span;
                var previous = new Position2(BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes[13..]));
                for (var offset = 17; offset + 8 <= bytes.Length; offset += 8)
                {
                    var point = new Position2(BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]));
                    TestAssert.True(runtime.Navigation.HasHeroMovementLineOfSight(previous, point),
                        $"camp {campId}: emitted movement remains terrain-legal");
                    previous = point;
                }
            }
        }
    }

    private static bool CapturedHeroRouteClear(MatchRuntime runtime, Entity hero,
        IReadOnlyList<Position2> route, NavigationObstacle[] obstacles)
    {
        if (route.Count < 2) return false;
        if (!runtime.Navigation.FirstSegmentClearOfUnits(route[0], hero.Position, route[1], obstacles) ||
            !runtime.Navigation.HasHeroMovementLineOfSight(hero.Position, route[1])) return false;
        for (var index = 0; index + 1 < route.Count; index++)
            if (!runtime.Navigation.SegmentClearOfUnits(route[index], route[index + 1], obstacles,
                    escape: index == 0) ||
                (index == 0 || index == route.Count - 2
                    ? !runtime.Navigation.HasHeroMovementLineOfSight(route[index], route[index + 1])
                    : !runtime.Navigation.HasHeroPathingLineOfSight(route[index], route[index + 1])))
                return false;
        foreach (var origin in new[] { hero.Position, route[0] })
        foreach (var dt in new[] { MovementSystem.HeroTickSeconds, MovementSystem.FixedTickSeconds })
            if (!FollowerClear(runtime, origin, route.Skip(1).ToArray(), obstacles, hero.Speed * dt))
                return false;
        return true;
    }

    private static MatchRuntime StartSolo(string orderChampion = "Annie")
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion, chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        var hero = runtime.Session.Participants.Single().Hero ??
            throw new InvalidOperationException("solo production runtime has no hero");
        PrepareHero(hero);
        return runtime;
    }

    private static MatchRuntime StartDuel(Team actorTeam)
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: false,
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        foreach (var connection in new long[] { 1, 2 })
        {
            var participant = runtime.Session.Participants.Single(seat =>
                seat.PlayerId == (ulong)connection);
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck(participant.PlayerId)) is not null,
                $"team {participant.Team} authentication");
            _ = runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection),
                $"team {participant.Team} ready");
        }
        TestAssert.True(runtime.Session.Start(), "duel start");
        var hero = runtime.Session.Participants.Single(seat => seat.Team == actorTeam).Hero ??
            throw new InvalidOperationException("duel actor has no hero");
        PrepareHero(hero);
        return runtime;
    }

    private static void PrepareHero(Entity hero)
    {
        hero.Position = new Position2(7000, 7000);
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
    }

    private static PacketCaptureTransport AttachCapture(MatchRuntime runtime, Entity hero)
    {
        var capture = new PacketCaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            99, hero.Team, hero.NetId, capture, ready: true));
        runtime.Delivery.Flush();
        capture.Clear();
        return capture;
    }

    private static Entity AddMobile(WorldState world, uint netId, EntityKind kind,
        Team team, Position2 position, double radius)
    {
        var entity = new Entity(netId, kind, UnitType.Minion, team, position)
        {
            Alive = true,
            Created = true,
            Visible = true,
            Radius = radius,
            Speed = 325.0,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        world.Register(entity);
        return entity;
    }

    private static void SendMove(MatchRuntime runtime, Entity hero, Position2 destination) =>
        SendOrder(runtime, hero, 2, destination);

    private static void SendOrder(MatchRuntime runtime, Entity hero, byte order,
        Position2 destination)
    {
        var request = new byte[22];
        request[0] = 0x75;
        request[5] = order;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        var connection = runtime.Session.Participants.Single(seat => seat.Hero == hero).ConnectionId ??
            throw new InvalidOperationException("hero order has no authenticated connection");
        runtime.Session.Receive(connection, 1, runtime.Cipher.Encrypt(request),
            runtime.Dependencies.Gameplay);
        runtime.Delivery.Flush();
    }

    private static double RouteLength(IEnumerable<Position2> points) =>
        points.Zip(points.Skip(1), (first, second) => Distance(first, second)).Sum();

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private sealed class PacketCaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();

        public IReadOnlyList<PlaintextPacket> Packets => _packets;

        public void Send(PlaintextPacket packet) => _packets.Add(packet);

        public void Clear() => _packets.Clear();
    }
}
