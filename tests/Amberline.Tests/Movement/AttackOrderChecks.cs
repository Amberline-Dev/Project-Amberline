using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>Source regressions for hero ATTACK_TO and attack-to-move transitions.</summary>
public static class AttackOrderChecks
{
    [Check("movement.hero-stop-order")]
    public static void StockStopAndHoldCancelChasing()
    {
        foreach (var order in new byte[] { 1, 10 })
        {
            var runtime = StartSolo();
            var hero = runtime.Session.Participants.Single().Hero!;
            hero.Position = new Position2(7000, 7000);
            var victim = AddTarget(runtime.World, 0x40010041, EntityKind.Minion,
                UnitType.Minion, Team.Chaos, new Position2(7100, 7000), 35);
            hero.AttackTarget = victim.NetId;
            TestAssert.True(runtime.Combat.QueueBasicAttack(hero, victim));
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId));
            hero.Waypoints = [new Position2(7200, 7000)];
            hero.Target = new Position2(7200, 7000);
            hero.LastAttackStopPoint = hero.Target;
            var position = hero.Position;
            // The stop command must ignore its position field, even if it
            // carries a stale predicted location far from the server hero.
            var packet = ClientGameplayPackets.MoveOrder(hero.NetId, order, new Position2(9000, 9000));
            TestAssert.True(runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span,
                runtime.Dependencies.Gameplay));
            // Batch 271: the stop's one-point path is installed at once (the client's path follower), so the hero stands on its own position's lattice
            // anchor, nowhere near the packet's point.
            TestAssert.Equal(runtime.Movement.ClientAnchor(position), hero.Position);
            TestAssert.True(hero.Target is null && hero.Waypoints.Count == 0 &&
                hero.AttackTarget is null && hero.LastAttackStopPoint is null,
                $"native order {order} must stop movement and targeting");
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId),
                "stop interrupts the unfinished attack windup");
        }
    }

    [Check("movement.attack-move-preserves-committed-windup")]
    public static void RepeatedAttackMovePreservesCommittedWindup()
    {
        var runtime = StartSolo();
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException("hero was not spawned");
        hero.Position = new Position2(7000, 7000);
        hero.AttackRange = 150.0;
        hero.AttackWindup = 0.2;
        hero.AttackPeriod = 1.0;
        hero.MissileSpeed = 0.0;
        var target = AddTarget(runtime.World, 0x40010042, EntityKind.Minion,
            UnitType.Minion, Team.Chaos, new Position2(7100, 7000), 35.0);
        participant.View.Known.Add(target.NetId);
        participant.View.Visible.Add(target.NetId);

        TestAssert.True(DispatchAttackTo(runtime, hero, target),
            "the production ATTACK_TO dispatcher must select the target");
        TestAssert.True(runtime.Combat.CombatTick(0.01).PendingHitsResolved == 0,
            "the production combat loop must leave the fresh windup unresolved");
        TestAssert.True(runtime.Combat.HasCommittedWindup(hero, target),
            "the fixture must enter the committed windup band");
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        target.Position = hero.Position + new Position2(CombatService.AttackReach(hero, target) + 40, 0);
        TestAssert.False(runtime.Combat.CanAttack(hero, target), "target leaves ordinary attack range but stays within the committed leash");
        var destination = new Position2(7300, 7000);

        // Production order dispatch clears HeroPursuit before order-specific
        // handling. Repeating ATTACK_MOVE must still preserve an eligible
        // target's committed windup while accepting the new move goal.
        TestAssert.True(DispatchAttackMove(runtime, hero, destination),
            "the production ATTACK_MOVE dispatcher must accept the order");
        TestAssert.Equal(target.NetId, hero.AttackTarget ?? 0u,
            "a repeated ATTACK_MOVE canceled the eligible committed target");
        TestAssert.True(runtime.Combat.PendingHits.Any(hit =>
                hit.SourceNetId == hero.NetId && hit.TargetNetId == target.NetId),
            "a repeated ATTACK_MOVE canceled the committed basic-attack windup");
        TestAssert.Equal(destination, hero.AttackMoveGoal ?? Position2.Zero,
            "a repeated ATTACK_MOVE did not retain its new destination");
    }

    [Check("movement.repeated-attack-to-preserves-moving-target-windup")]
    public static void RepeatedAttackToKeepsCommittedWindupThroughTargetMotion()
    {
        var runtime = StartSolo();
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException("hero was not spawned");
        hero.Position = new Position2(7000, 7000);
        hero.AttackRange = 150.0;
        hero.AttackWindup = 0.2;
        hero.AttackPeriod = 1.0;
        hero.MissileSpeed = 0.0;
        var target = AddTarget(runtime.World, 0x40010043, EntityKind.Minion,
            UnitType.Minion, Team.Chaos, new Position2(7100, 7000), 35.0);
        participant.View.Known.Add(target.NetId);
        participant.View.Visible.Add(target.NetId);

        TestAssert.True(DispatchAttackTo(runtime, hero, target),
            "the production ATTACK_TO dispatcher must select the moving target");
        runtime.Combat.CombatTick(0.01);
        TestAssert.True(runtime.Combat.HasCommittedWindup(hero, target),
            "the production attack loop must enter the 100-unit committed leash");
        var before = target.Health;

        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        target.Position = hero.Position + new Position2(CombatService.AttackReach(hero, target) + 40, 0);
        TestAssert.False(runtime.Combat.CanAttack(hero, target), "target leaves ordinary attack range but stays within the committed leash");
        TestAssert.True(DispatchAttackTo(runtime, hero, target),
            "a repeated production ATTACK_TO must remain accepted");
        TestAssert.True(runtime.Combat.HasCommittedWindup(hero, target),
            "repeated ATTACK_TO canceled the live-range committed windup");
        runtime.Combat.CombatTick(0.1);
        TestAssert.Equal(before, target.Health,
            "the moving target must not take damage before the windup completes");
        runtime.Combat.CombatTick(0.1);
        // Batch 741: the tick that crosses the windup is the swing's cast frame, where a melee hit lands (the client's cast builder runs a
        // CastType 0 effect at once); it stayed pending for one more tick before.
        TestAssert.True(target.Health < before,
            "the committed ATTACK_TO windup must resolve at its cast frame after target motion");
    }

    [Check("movement.attack-to-stop-distance")]
    public static void AttackToStopsAtCollisionRange()
    {
        var runtime = StartSolo();
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException("hero was not spawned");
        hero.AttackRange = 625.0;
        var turret = AddTarget(runtime.World, 0x40000041, EntityKind.Turret,
            UnitType.Turret, Team.Chaos, new Position2(8548.8, 8289.5), 88.0);
        participant.View.Known.Add(turret.NetId);
        participant.View.Visible.Add(turret.NetId);

        var dx = 8172.0 - turret.Position.X;
        var dz = 7694.0 - turret.Position.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        hero.Position = new Position2(turret.Position.X + dx / distance * 995.0,
            turret.Position.Z + dz / distance * 995.0);
        hero.Waypoints.Clear();
        hero.Target = null;

        TestAssert.True(DispatchAttackTo(runtime, hero, turret),
            "the authenticated production dispatcher must accept ATTACK_TO");
        TestAssert.True(hero.Waypoints.Count > 0,
            "an out-of-range ATTACK_TO must install a chase route");
        var end = hero.Waypoints[^1];
        var endpointDistance = Distance(end, turret.Position);
        // Audit A5, 18 Oct 2026: the route is the client's AttackTo plan (to the target's position, the end cell snapped off its
        // body); the hero stops where combat plants it, at the edge-to-edge reach. Replaces the radial stop 6 units inside reach.
        TestAssert.True(endpointDistance <= CombatService.AttackReach(hero, turret),
            "ATTACK_TO routes the hero to within the shared edge-to-edge structure reach");
        TestAssert.True(endpointDistance > turret.Radius,
            "ATTACK_TO must not route the hero to the target centre");

        var minion = AddTarget(runtime.World, 0x40010041, EntityKind.Minion,
            UnitType.Minion, Team.Chaos, turret.Position, 35.744);
        minion.MinionType = MinionType.Melee;
        participant.View.Known.Add(minion.NetId);
        participant.View.Visible.Add(minion.NetId);
        hero.Position = new Position2(turret.Position.X + dx / distance * 995.0,
            turret.Position.Z + dz / distance * 995.0);
        hero.Waypoints.Clear();
        hero.LastAttackStopPoint = null;
        TestAssert.True(DispatchAttackTo(runtime, hero, minion),
            "the same dispatcher path must accept a minion target");
        endpointDistance = Distance(hero.Waypoints[^1], minion.Position);
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii; audit A5: the client's plan ends next to the body
        TestAssert.True(endpointDistance <= CombatService.AttackReach(hero, minion) && endpointDistance > minion.Radius,
            "ATTACK_TO must use the target collision radius for minions too");

        hero.Position = new Position2(turret.Position.X + dx / distance *
            (hero.AttackRange + turret.Radius - 50.0),
            turret.Position.Z + dz / distance * (hero.AttackRange + turret.Radius - 50.0));
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.LastAttackStopPoint = null;
        var before = hero.Position;
        TestAssert.True(DispatchAttackTo(runtime, hero, turret),
            "an in-range ATTACK_TO must remain accepted");
        TestAssert.Equal(0, hero.Waypoints.Count,
            "an in-range ATTACK_TO must not queue a walk");
        TestAssert.Equal(before, hero.Position,
            "an in-range ATTACK_TO must leave the hero in place");
    }

    [Check("movement.attack-move-continuity")]
    public static void ExistingChaseReissueIsPacketMinimal()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new Amberline.Simulation.Movement.MovementSystem(world, grid);
        var attacker = new Entity(3, EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(7200.0, 7000.0))
        {
            MinionType = MinionType.Melee,
            Alive = true,
            Created = true,
            Speed = 325.0,
            Radius = 35.744,
            AttackRange = 170.0,
            AcquisitionRange = 450.0,
            MinionState = MinionAiState.Engage,
            AttackTarget = 4,
            Waypoints = [new Position2(7400.0, 7000.0)],
        };
        var target = new Entity(4, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(7600.0, 7000.0)) { Alive = true, Radius = 65.0 };
        world.Register(attacker);
        world.Register(target);
        var events = new List<string>();
        var packets = new RecordingPackets(events);
        var ai = new Amberline.Simulation.Minions.MinionAiSystem(world, grid, movement,
            new FixtureCombat(), packets,
            new AlwaysVisibleVision());

        ai.Tick(0.03);
        TestAssert.Equal(1, events.Count,
            "an in-flight chase reissue must emit one waypoint packet");
        TestAssert.Equal("waypoints", events[0],
            "an in-flight chase reissue must not add an attack-cancel packet");

        // A planted attacker must cancel the previous attack before replacing
        // it with a chase route once the target leaves range.
        world = new WorldState();
        movement = new Amberline.Simulation.Movement.MovementSystem(world, grid);
        events = new List<string>();
        attacker = new Entity(5, EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(7000.0, 7000.0))
        {
            MinionType = MinionType.Melee, Alive = true, Created = true,
            Speed = 325.0, Radius = 35.744, AttackRange = 170.0,
            AcquisitionRange = 450.0, MinionState = MinionAiState.Engage,
            AttackTarget = 6, AnchorPath = [new Position2(7000.0, 7000.0)],
            LastAnchorTime = -0.6,
            // Attacking: it has sent its 0x0D, so its client is in the attack state, and the stale-plant stop-attack
            // is sent only for such a unit (N2 K7, and the stop-attack rule of 17 Oct 2026).
            MinionAutoAttackEnabled = true,
            ClientAttackArmed = true,
        };
        target = new Entity(6, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(7600.0, 7000.0)) { Alive = true, Radius = 65.0 };
        world.Register(attacker);
        world.Register(target);
        packets = new RecordingPackets(events);
        ai = new Amberline.Simulation.Minions.MinionAiSystem(world, grid, movement,
            new FixtureCombat(), packets, new AlwaysVisibleVision());
        ai.Tick(0.03);
        TestAssert.True(events.Count >= 2,
            "a planted attacker leaving range must emit cancellation and a chase route");
        TestAssert.Equal("stop-attack", events[0],
            "planted attack-to-chase must cancel attack before movement");
        TestAssert.Equal("waypoints", events[1],
            "planted attack-to-chase must route after cancellation");

        // In-range acquisition plants the first claimant, then only physical
        // overlaps receive fan routes at their current firing radius.
        world = new WorldState();
        movement = new Amberline.Simulation.Movement.MovementSystem(world, grid);
        events = new List<string>();
        target = new Entity(10, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(7300.0, 7000.0)) { Alive = true, Radius = 0.0 };
        world.Register(target);
        var claimants = new[] { 11u, 12u, 13u }.Select(id => new Entity(id,
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(7150.0, 7000.0))
        {
            MinionType = MinionType.Melee, Alive = true, Created = true,
            Speed = 325.0, Radius = 35.744, AttackRange = 170.0,
            AcquisitionRange = 450.0, Waypoints = [new Position2(7400.0, 7000.0)],
            // minionLib 0.0 OnInit starts a minion in AI_IDLE and Minion.lua 0.4's
            // TimerMoveForward arm for that state runs FindTargetOrMove: that birth scan is the
            // acquisition this section measures. A claimant already in AI_ATTACKMOVESTATE has had
            // it and Minion.lua 0.4's arm for that state does not scan again.
            MinionState = MinionAiState.Idle,
        }).ToArray();
        foreach (var claimant in claimants) world.Register(claimant);
        packets = new RecordingPackets(events);
        ai = new Amberline.Simulation.Minions.MinionAiSystem(world, grid, movement,
            new FixtureCombat(), packets, new AlwaysVisibleVision());
        // Batch 279: the stop's one-point path is installed at once (the client's path follower): the claimant stands on its original point's anchor.
        var firstOriginalPosition = movement.ClientAnchor(claimants[0].Position);
        ai.Tick(0.0);
        var routes = packets.Routes.ToArray();
        TestAssert.True(routes.Length >= 3,
            "each in-range claimant must emit its stop or fan route");
        TestAssert.Equal(1, routes[0].Count,
            "the first in-range claimant must receive a singleton stop route");
        TestAssert.Near(firstOriginalPosition.X, routes[0][0].X, 1e-6,
            $"the first claimant stop must preserve its original X position " +
            $"expected={firstOriginalPosition.X:R}, actual={routes[0][0].X:R}");
        TestAssert.Near(firstOriginalPosition.Z, routes[0][0].Z, 1e-6,
            $"the first claimant stop must preserve its original Z position " +
            $"expected={firstOriginalPosition.Z:R}, actual={routes[0][0].Z:R}");
        TestAssert.Equal(0, claimants[0].Waypoints.Count,
            "the first in-range claimant must not receive movement waypoints");
        TestAssert.True(claimants[0].Target is null,
            "the first in-range claimant must not retain a movement target");
        TestAssert.True(claimants.Skip(1).All(claimant =>
            claimant.Waypoints.Count >= 2 || claimant.Target is not null),
            "overlapping claimants must receive bounded fan movement");
        TestAssert.True(routes.Skip(1).All(route => route.Count >= 2 &&
            Math.Abs(Distance(route[^1], target.Position) - 150.0) < 3.0),
            "overlapping claimants must preserve their current 150-unit firing radius");
    }

    private static MatchRuntime StartSolo()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }

    private static Entity AddTarget(WorldState world, uint netId, EntityKind kind,
        UnitType unitType, Team team, Position2 position, double radius) {
        var entity = new Entity(netId, kind, unitType, team, position) {
            Alive = true, Created = true, Visible = true, Radius = radius,
            Health = 1000.0, MaxHealth = 1000.0,
        };
        world.Register(entity);
        return entity;
    }

    private static bool DispatchAttackTo(MatchRuntime runtime, Entity hero, Entity target)
    {
        var payload = new byte[22];
        payload[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        payload[5] = 3;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6),
            BitConverter.SingleToInt32Bits((float)target.Position.X));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(14),
            BitConverter.SingleToInt32Bits((float)target.Position.Z));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), target.NetId);
        return runtime.Session.DispatchGameplay(1, 1, payload, runtime.Dependencies.Gameplay);
    }

    private static bool DispatchAttackMove(MatchRuntime runtime, Entity hero,
        Position2 destination)
    {
        var payload = new byte[22];
        payload[0] = 0x75;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        payload[5] = 7;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6),
            BitConverter.SingleToInt32Bits((float)destination.X));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(14),
            BitConverter.SingleToInt32Bits((float)destination.Z));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), 0);
        return runtime.Session.DispatchGameplay(1, 1, payload, runtime.Dependencies.Gameplay);
    }

    private static double Distance(Position2 first, Position2 second) {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private sealed class AlwaysVisibleVision : Amberline.Simulation.Minions.IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    private sealed class FixtureCombat : Amberline.Simulation.Minions.IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Protected;
        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class RecordingPackets : Amberline.Simulation.Minions.IMinionPacketSink
    {
        private readonly List<string> _events;
        public List<IReadOnlyList<Position2>> Routes { get; } = [];
        public RecordingPackets(List<string> events) => _events = events;
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position) { position = default; return false; }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { _events.Add("waypoints"); Routes.Add(waypoints.ToArray()); }
        public void SendStop(Entity minion, long syncId, string reason) { _events.Add("stop"); Routes.Add([minion.Position]); }
        public void SendStopAttack(Entity minion, string reason)
        {
            if (CombatPackets.StopAttack(minion) is not null) _events.Add("stop-attack");
        }
    }
}
