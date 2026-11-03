using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>Focused regressions for attack approach selection and melee impact range.</summary>
public static class AttackRangeRegressionChecks
{
    // Batch 61: the mirrored-angle search is deleted for heroes (nothing in the stock client or the shipped scripts picks a firing angle:
    // IssueOrder plans an AttackTo straight to the target's position, PlanChase), and the hero planner ignores NavigationGrid.FindPathOverride.
    // Minions, pets and ghouls keep the search (NOT RETAIL, MovementSystem.ApproachWithin), so the original assertions run on a minion
    // attacker; a hero is asserted to plan exactly once and take the planner's direct route.
    [Check("movement.attack-candidate-uses-shortest-route")]
    public static void AttackCandidateUsesShortestMirroredRoute()
    {
        HeroPlansOnceStraightAtTheTarget();
        var upper = RunMirroredCandidate(aboveTarget: true);
        var lower = RunMirroredCandidate(aboveTarget: false);

        TestAssert.True(upper.RouteLength + 50.0 < upper.RadialRouteLength,
            $"upper approach kept the long radial route: selected={upper.RouteLength:R}, " +
            $"radial={upper.RadialRouteLength:R}");
        TestAssert.True(lower.RouteLength + 50.0 < lower.RadialRouteLength,
            $"lower approach kept the long radial route: selected={lower.RouteLength:R}, " +
            $"radial={lower.RadialRouteLength:R}");
        // The retired assertion that the two scenes select opposite world sides rested on the hero's wire-anchor quantization breaking
        // the exact tie of the two mirrored candidates (the hero always stands on the radial ray, so the +/- offsets are equidistant to
        // the last digit); MovementSystem.ApproachWithin keeps the first candidate on an exact tie by design, so a minion attacker
        // has no side to assert.
        Console.WriteLine(FormattableString.Invariant(
            $"attack-candidate upper selected={upper.RouteLength:R} radial={upper.RadialRouteLength:R}; lower selected={lower.RouteLength:R} radial={lower.RadialRouteLength:R}"));
    }

    [Check("movement.attack-candidate-retries-after-all-routes-reject")]
    public static void BlockedAttackCandidateRetainsTargetForRetry()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.OwnerNetId = 0;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackRange = 100.0;
        var target = AddTarget(runtime.World, 0x4F20_0050,
            new Position2(7600.0, 7000.0));
        var participant = runtime.Session.Participants.Single();
        participant.View.Known.Add(target.NetId);
        participant.View.Visible.Add(target.NetId);
        // Batch 61: the hero planner ignores NavigationGrid.FindPathOverride, so the rejection is produced the way the stock planner
        // meets it: eight wide bodies wall the hero in, every neighbouring cell is occupied for the actor test, and no route to the
        // target's cell exists (a partial or empty plan never reaches attack range).
        var wall = Enumerable.Range(0, 8).Select(index =>
        {
            var angle = index * Math.PI / 4.0;
            var body = AddTarget(runtime.World, 0x4F20_0070u + (uint)index,
                hero.Position + new Position2(Math.Cos(angle) * 170.0, Math.Sin(angle) * 170.0), radius: 120.0);
            body.Team = hero.Team;
            return body;
        }).ToArray();

        runtime.Spells.HeroOrders!.StartAttack(hero, target);
        TestAssert.Equal(target.NetId, hero.AttackTarget ?? 0u,
            "a rejected attack route must retain its target for retry");
        TestAssert.True(hero.LastAttackStopPoint is null,
            "a rejected attack route must not latch a blocked endpoint");

        foreach (var body in wall) body.Alive = false;
        runtime.Spells.HeroOrders.StartAttack(hero, target);
        TestAssert.True(hero.Waypoints.Count > 1,
            "the retained attack target must be retried when navigation recovers");
        TestAssert.True(hero.LastAttackStopPoint is not null,
            "the recovered plan latches its endpoint (control for the rejected one above)");
    }

    [Check("movement.attack-open-field-skips-alternate-routes")]
    public static void OpenFieldAttackUsesRadialPlanOnly()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.OwnerNetId = 0;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackRange = 100.0;
        var target = AddTarget(runtime.World, 0x4F20_0060,
            new Position2(7600.0, 7000.0));
        var participant = runtime.Session.Participants.Single();
        participant.View.Known.Add(target.NetId);
        participant.View.Visible.Add(target.NetId);
        runtime.Navigation.ResetPathQueryCounters();

        // Batch 61: a hero plans with the client planner (one PlanChase per chase), not through NavigationGrid.FindPath, so the plan
        // count is the chase counter and the grid's planner counters stay at zero.
        long ChasePlans() => runtime.Movement.HeroAutomaticRoutes.GetValueOrDefault("chase-plan");
        runtime.Spells.HeroOrders!.StartAttack(hero, target);
        TestAssert.Equal(1L, ChasePlans(),
            "committing the selected open-field plan must not plan it a second time");
        TestAssert.Equal(0L, runtime.Navigation.FindPathCalls + runtime.Navigation.FindPathAroundUnitsCalls,
            "an unobstructed attack must skip alternate unit-route planning");
        runtime.Movement.Tick(0.1);
        TestAssert.Equal(1, hero.Waypoints.Count,
            "movement consumes the anchor while keeping the final approach leg");
        var route = hero.Waypoints;
        runtime.Spells.HeroOrders.Tick(0.2);
        TestAssert.True(ReferenceEquals(route, hero.Waypoints),
            "a live final approach leg must not be replaced at each pursuit scan");
        TestAssert.Equal(1L, ChasePlans(),
            "finishing an unobstructed approach does not restart route planning");
    }

    [Check("movement.production-yorick-melee-wave")]
    public static void ProductionYorickMeleeAttackRoutesThroughFriendlyWave()
    {
        var runtime = StartSolo("Yorick");
        var hero = runtime.Session.Participants.Single().Hero!;
        var participant = runtime.Session.Participants.Single();
        hero.OwnerNetId = 0;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.Waypoints.Clear();
        hero.Target = null;
        var shippedRange = GameData.Default.Champion("Yorick").AttackRange;
        TestAssert.Near(125.0, shippedRange, 1e-6,
            "patch-126 Yorick basic attack range must come from shipped data");
        TestAssert.Near(shippedRange, hero.AttackRange, 1e-6,
            "production Yorick must retain its shipped basic attack range");

        for (var index = -1; index <= 1; index++)
        {
            var wall = new Entity(0x4F20_0020u + (uint)(index + 1), EntityKind.Minion,
                UnitType.Minion, Team.Order, new Position2(7440.0, 7000.0 + index * 80.0))
            {
                Alive = true,
                Created = true,
                Visible = true,
                Radius = 35.744,
                Health = 1000.0,
                MaxHealth = 1000.0,
                Speed = 325.0,
                MinionType = null,
            };
            runtime.World.Register(wall);
        }

        var meleeStats = MinionDefinitions.Stats[MinionType.Melee];
        var target = AddTarget(runtime.World, 0x4F20_0030,
            new Position2(7600.0, 7000.0), radius: meleeStats.Radius);
        target.Speed = meleeStats.MoveSpeed;
        target.MinionType = null; // production body stats, fixture-owned AI
        participant.View.Known.Add(target.NetId);
        participant.View.Visible.Add(target.NetId);
        var before = target.Health;
        runtime.Spells.HeroOrders!.StartAttack(hero, target);
        TestAssert.True(hero.Waypoints.Count > 1,
            "Yorick must receive an initial melee approach through the wall");
        var endpointDistance = Distance(hero.Waypoints[^1], target.Position);
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        TestAssert.True(endpointDistance <= CombatService.AttackReach(hero, target),
            $"Yorick's initial route exceeded attack reach: distance={endpointDistance:R}");
        // Batch 61: the chase plans at the target's position and the end-cell snap puts the end next to the body; a flagged hero brings
        // 0.2 of its pathing radius to the pair test, so the bodies overlap only when the thin hero disc meets the target's disc (the
        // gameplay radii sum is not a collision figure).
        TestAssert.True(endpointDistance >= runtime.Movement.MoverPairRadius(hero, target) +
                runtime.Movement.PairMovementRadius(target, hero),
            $"Yorick's initial route overlapped the target body: distance={endpointDistance:R}");
        var initialRouteLength = hero.Waypoints.Prepend(hero.Position)
            .Zip(hero.Waypoints, (first, second) => Distance(first, second)).Sum();

        var firstHitTick = -1;
        for (var tick = 0; tick < 50 && target.Health >= before; tick++)
        {
            runtime.Tick(0.1);
            if (target.Health < before) firstHitTick = tick;
        }
        TestAssert.True(firstHitTick >= 0,
            $"production Yorick remained stuck behind a friendly melee wave: " +
            $"initialRoute={initialRouteLength:R}");
        TestAssert.True(firstHitTick < 50,
            "production Yorick first-hit time must remain bounded");
        Console.WriteLine(FormattableString.Invariant(
            $"yorick-melee initialRoute={initialRouteLength:R} endpointDistance={endpointDistance:R} firstHitSeconds={(firstHitTick + 1) * 0.1:R}"));
    }

    [Check("combat.committed-melee-windup-survives-target-motion")]
    public static void CommittedMeleeWindupSurvivesTargetMotionButExplicitMoveCancels()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackRange = 150.0;
        hero.AttackDamage = 75.0;
        hero.AttackPeriod = 1.0;
        hero.AttackWindup = 0.2;
        hero.MissileSpeed = 0.0;
        var stopPackets = new RecordingTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(0x4F20_0100,
            hero.Team, hero.NetId, stopPackets, ready: true,
            view: runtime.Session.Participants.Single().View));

        var melee = AddTarget(runtime.World, 0x4F20_0001,
            new Position2(7100.0, 7000.0));
        var meleeHealth = melee.Health;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, melee),
            "in-range melee attack must queue");

        // CombatTick processes pending hits before the ordinary attack loop.
        // A target leaving the current range after a legal attack starts must
        // not revoke that committed windup.
        runtime.Combat.CombatTick(0.1);
        TestAssert.Equal(meleeHealth, melee.Health,
            "positive melee windup must not resolve early");
        // Keep the target inside the source-backed 100-unit committed leash.
        // The production-order regressions cover the repeated-order path;
        // this fixture isolates the windup's bounded target-motion behavior.
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        melee.Position = hero.Position + new Position2(CombatService.AttackReach(hero, melee) + 20, 0);
        runtime.Combat.CombatTick(0.1);
        TestAssert.Equal(meleeHealth, melee.Health,
            "the committed melee windup must remain pending through target motion");
        TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.TargetNetId == melee.NetId),
            "target motion must not cancel a committed melee windup");
        runtime.Delivery.Flush();
        // Any flags: only the unit's id is compared.
        TestAssert.False(stopPackets.Packets.Any(packet =>
                packet.Plaintext.Length == 6 && packet.Plaintext.Span[0] == 0x39 &&
                packet.Plaintext.Span[1..5].SequenceEqual(CombatPackets.NpcInstantStopAttack(hero.NetId, 0).Plaintext.Span[1..5])),
            "target motion must not emit an automatic stop-attack packet");
        runtime.Combat.CombatTick(0.01);
        TestAssert.True(melee.Health < meleeHealth,
            "the committed melee windup must resolve after the target flees");

        hero.AttackCooldown = 0.0;
        var escaped = AddTarget(runtime.World, 0x4F20_0006,
            new Position2(7100.0, 7000.0), radius: 35.0);
        var escapedHealth = escaped.Health;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, escaped),
            "a second in-range melee attack must queue before the escape check");
        runtime.Combat.CombatTick(0.1);
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        escaped.Position = hero.Position + new Position2(CombatService.AttackReach(hero, escaped) + 101, 0);
        TestAssert.False(runtime.Combat.InCommittedAttackRange(hero, escaped),
            "the negative fixture must leave the ordinary range plus 100-unit leash");
        runtime.Combat.CombatTick(0.1);
        TestAssert.Equal(escapedHealth, escaped.Health,
            "a target escaping beyond the committed leash must not be hit");
        TestAssert.False(runtime.Combat.PendingHits.Any(hit =>
                hit.SourceNetId == hero.NetId && hit.TargetNetId == escaped.NetId),
            "a target escaping beyond the committed leash must cancel the windup");

        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackCooldown = 0.0;
        var explicitCancel = AddTarget(runtime.World, 0x4F20_0005,
            new Position2(7100.0, 7000.0));
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, explicitCancel),
            "a second in-range melee attack must queue");
        var move = ClientGameplayPackets.MoveOrder(hero.NetId, 2,
            new Position2(7200.0, 7000.0));
        TestAssert.True(runtime.Session.DispatchGameplay(1, 1, move.Plaintext.Span,
            runtime.Dependencies.Gameplay), "explicit move is accepted");
        TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.TargetNetId == explicitCancel.NetId),
            "explicit move must cancel the unfinished attack");

        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackCooldown = 0.0;
        hero.MissileSpeed = 0.0;
        var completed = AddTarget(runtime.World, 0x4F20_0003,
            new Position2(7100.0, 7000.0));
        var completedHealth = completed.Health;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, completed),
            "second in-range melee attack must queue");
        runtime.Combat.CombatTick(0.1);
        hero.Position = new Position2(7600.0, 7000.0);
        runtime.Combat.CombatTick(0.1);
        TestAssert.Equal(completedHealth, completed.Health,
            "unfinished melee windup must not cross zero after displacement");

        // Complete the windup while still in range, then displace the source;
        // the completed attack must remain authoritative on the next tick.
        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackCooldown = 0.0;
        var landed = AddTarget(runtime.World, 0x4F20_0004,
            new Position2(7100.0, 7000.0));
        var landedHealth = landed.Health;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, landed),
            "completed-range melee attack must queue");
        runtime.Combat.CombatTick(0.2);
        hero.Position = new Position2(7600.0, 7000.0);
        runtime.Combat.CombatTick(0.01);
        TestAssert.True(landed.Health < landedHealth,
            "a melee attack that completed windup in range must resolve");

        hero.Position = new Position2(7000.0, 7000.0);
        hero.AttackCooldown = 0.0;
        hero.MissileSpeed = 1000.0;
        var ranged = AddTarget(runtime.World, 0x4F20_0002,
            new Position2(7100.0, 7000.0));
        var rangedHealth = ranged.Health;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, ranged),
            "in-range ranged attack must queue");
        runtime.Combat.CombatTick(0.2);
        hero.Position = new Position2(7600.0, 7000.0);
        runtime.Combat.CombatTick(0.2);
        TestAssert.True(ranged.Health < rangedHealth,
            "a launched ranged missile must resolve after source displacement");
    }

    private static void HeroPlansOnceStraightAtTheTarget()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.OwnerNetId = 0;
        hero.Position = new Position2(7000.0, 7100.0);
        hero.AttackRange = 300.0;
        hero.Waypoints.Clear();
        hero.Target = null;
        var target = AddTarget(runtime.World, 0x4F20_0012u, new Position2(7600.0, 7000.0));
        var participant = runtime.Session.Participants.Single();
        participant.View.Known.Add(target.NetId);
        participant.View.Visible.Add(target.NetId);
        runtime.Navigation.ResetPathQueryCounters();
        runtime.Spells.HeroOrders!.StartAttack(hero, target);
        TestAssert.Equal(1L, runtime.Movement.HeroAutomaticRoutes.GetValueOrDefault("chase-plan"),
            "a hero plans its chase once: no mirrored-angle candidates");
        TestAssert.Equal(0L, runtime.Navigation.FindPathCalls, "the client planner does not ask the strategic grid planner");
        TestAssert.True(hero.Waypoints.Count > 0, "the chase installs a route");
        TestAssert.True(Distance(hero.Waypoints[^1], target.Position) <= CombatService.AttackReach(hero, target),
            "the hero's planner route ends within attack reach of the target");
    }

    private static (Position2 Endpoint, Position2 Target, double RouteLength,
        double RadialRouteLength) RunMirroredCandidate(bool aboveTarget)
    {
        var runtime = StartSolo();
        var targetPosition = new Position2(7600.0, 7000.0);
        var heroPosition = new Position2(7000.0, aboveTarget ? 7100.0 : 6900.0);
        // A minion attacker: the mirrored-angle search still serves every unit but a hero.
        var hero = runtime.World.Register(new Entity(aboveTarget ? 0x4F20_0020u : 0x4F20_0021u, EntityKind.Minion,
            UnitType.Minion, Team.Order, heroPosition)
        {
            Alive = true, Created = true, Visible = true, Radius = 35.744, Speed = 325.0,
            Health = 1000.0, MaxHealth = 1000.0, AttackRange = 300.0,
        });
        var target = AddTarget(runtime.World, aboveTarget ? 0x4F20_0010u : 0x4F20_0011u,
            targetPosition);

        var baseAngle = Math.Atan2(heroPosition.Z - targetPosition.Z,
            heroPosition.X - targetPosition.X);
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        var reach = CombatService.AttackReach(hero, target);
        var stop = reach - 6.0;
        var radialDestination = new Position2(
            targetPosition.X + Math.Cos(baseAngle) * stop,
            targetPosition.Z + Math.Sin(baseAngle) * stop);
        var radialLength = Distance(heroPosition, new Position2(heroPosition.X,
            heroPosition.Z + 500.0)) + Distance(new Position2(heroPosition.X,
            heroPosition.Z + 500.0), radialDestination);
        var radialPlans = 0;
        runtime.Navigation.FindPathOverride = (start, goal) =>
        {
            if (Distance(goal, radialDestination) <= 12.0)
            {
                radialPlans++;
                return new[] { start, new Position2(start.X, start.Z + 500.0), goal };
            }
            return new[] { start, goal };
        };

        var plan = runtime.Movement.ApproachWithin(hero, target, stop, reach);
        TestAssert.True(radialPlans >= 1,
            "the synthetic route must exercise the radial candidate");
        TestAssert.True(plan.Path.Count > 1,
            "attack pursuit must commit a reachable firing route");
        var routeLength = plan.Path.Zip(plan.Path.Skip(1), (first, second) => Distance(first, second)).Sum();
        return (plan.Path[^1], targetPosition, routeLength, radialLength);
    }

    private static MatchRuntime StartSolo(string orderChampion = "Annie")
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: orderChampion, chaosChampion: "Annie", solo: true,
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

    private static Entity AddTarget(WorldState world, uint netId, Position2 position,
        double radius = 0.0)
    {
        var target = new Entity(netId, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            position)
        {
            Alive = true,
            Created = true,
            Visible = true,
            Radius = radius,
            Health = 1000.0,
            MaxHealth = 1000.0,
        };
        world.Register(target);
        return target;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private sealed class RecordingTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();

        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
