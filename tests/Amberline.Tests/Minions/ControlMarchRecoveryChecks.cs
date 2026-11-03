using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

public static class ControlMarchRecoveryChecks
{
    [Check("minions-live-control-release-restores-lane-progress")]
    public static void RealControlReleaseResumesUnspentLaneSuffix()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var type in new[] { BuffType.Snare, BuffType.Stun, BuffType.Fear })
        {
            var setup = StartMarch(team);
            using var runtime = setup.Runtime;
            var minion = setup.Minion;
            var hero = setup.Observer;
            var now = runtime.Combat.CurrentTime;
            runtime.Tick(.15);
            TestAssert.True(minion.Created,
                $"{team}/{type}: a real same-team observer knows the wave");
            TestAssert.True(minion.Waypoints.Count > 1,
                $"{team}/{type}: the scheduled march has a live lane suffix");
            var laneGoal = minion.Waypoints[^1];
            var controlPosition = minion.Position;
            var payload = type == BuffType.Fear
                ? new BuffPayload { ActionDisabled = true, MoveSpeedMult = -.4,
                    FearOrigin = minion.Position, NextWander = now }
                : new BuffPayload();
            var buff = runtime.Buffs.AddBuff(minion,
                type == BuffType.Fear ? "Fear" : $"MarchControl{type}", type,
                .6, hero.NetId, payload, canMitigateDuration: false);
            TestAssert.True(buff is not null, $"{team}/{type}: real buff admission");
            if (type != BuffType.Fear)
            {
                TestAssert.Equal(0, minion.Waypoints.Count,
                    $"{team}/{type}: BuffLifecycle cancelled the active route");
                runtime.Tick(.2);
                // Restated in batch 254 (was 1.0): the cancelled route is published as a one-point stop, whose 2-unit anchor the actor
                // is seated on (the client's path follower), less than one 2-unit lattice step per axis away.
                TestAssert.True(Distance(minion.Position, controlPosition) < 2.0 * Math.Sqrt(2.0),
                    $"{team}/{type}: hard control holds authoritative movement");
            }
            else
            {
                // The production Shaco fear timer owns the first wander and
                // invokes StartForcedMove(cancelOrder: true) on this buff.
                runtime.Tick(1.0 / 30.0);
                TestAssert.True(minion.ForcedMove is not null,
                    $"{team}: real fear timer started displacement");
                TestAssert.Equal(0, minion.Waypoints.Count,
                    $"{team}: fear cancelled the active march order");
            }
            TestAssert.False(minion.LaneDone,
                $"{team}/{type}: control is not a lane terminus");

            runtime.Tick(.85);
            TestAssert.True(minion.Buffs.All(active => active != buff),
                $"{team}/{type}: control expired through production ticking");
            TestAssert.True(minion.ForcedMove is null or { Completed: true },
                $"{team}/{type}: fear displacement released ownership");
            var releasedPosition = minion.Position;
            runtime.Tick(1.0);
            TestAssert.False(minion.LaneDone,
                $"{team}/{type}: no premature siege or idle after release");
            TestAssert.True(minion.AttackTarget is null,
                $"{team}/{type}: resume requires no combat target");
            TestAssert.True(minion.Waypoints.Count > 0 && minion.Waypoints[^1] == laneGoal,
                $"{team}/{type}: the resumed route still ends at the original lane goal");
            TestAssert.True(Distance(minion.Position, releasedPosition) > 15.0,
                $"{team}/{type}: the released minion walks again");
            TestAssert.True(Distance(minion.Position, controlPosition) > 15.0,
                $"{team}/{type}: the minion advances from its interrupted march");
            TestAssert.True(runtime.Navigation.IsMovementWalkable(minion.Position) &&
                            minion.Waypoints.All(runtime.Navigation.IsMovementWalkable),
                $"{team}/{type}: the restored lane route remains on movement terrain");
            AssertOutsideStaticBodies(runtime, minion, $"{team}/{type}");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var nested = StartMarch(team);
            using (var runtime = nested.Runtime)
            {
                var minion = nested.Minion;
                var goal = minion.Waypoints[^1];
                TestAssert.True(runtime.Buffs.AddBuff(minion, "NestedMarchSnare",
                    BuffType.Snare, .35, nested.Observer.NetId,
                    canMitigateDuration: false) is not null);
                runtime.Tick(.1);
                TestAssert.True(runtime.Buffs.AddBuff(minion, "NestedMarchStun",
                    BuffType.Stun, .65, nested.Observer.NetId,
                    canMitigateDuration: false) is not null);
                runtime.Tick(.35);
                TestAssert.False(minion.Buffs.Any(buff => buff.Name == "NestedMarchSnare"));
                TestAssert.True(minion.Buffs.Any(buff => buff.Name == "NestedMarchStun"));
                TestAssert.True(minion.Waypoints.Count == 0 && !minion.LaneDone,
                    $"{team}: first control expiry must not resume through nested Stun");
                runtime.Tick(.7);
                TestAssert.True(minion.Waypoints.Count > 0 && minion.Waypoints[^1] == goal &&
                                !minion.LaneDone,
                    $"{team}: nested control releases the same unspent lane suffix");
            }

            var acquired = StartMarch(team);
            using (var runtime = acquired.Runtime)
            {
                var minion = acquired.Minion;
                var goal = minion.Waypoints[^1];
                TestAssert.True(runtime.Buffs.AddBuff(minion, "AggroMarchSnare",
                    BuffType.Snare, .5, acquired.Observer.NetId,
                    canMitigateDuration: false) is not null);
                // Teleport_Target can replace the cancelled public route with
                // this still-rooted stop anchor before target acquisition.
                minion.Waypoints = [minion.Position];
                var targetPoint = VisiblePoint(runtime.Navigation, minion.Position, 300);
                var enemy = new Entity(runtime.World.Allocate(UnitType.Hero),
                    EntityKind.Hero, UnitType.Hero,
                    team == Team.Order ? Team.Chaos : Team.Order, targetPoint)
                {
                    ChampionName = "Annie", Name = "MarchAggroTarget",
                    Alive = true, Created = true, Visible = true,
                    Health = 5000, MaxHealth = 5000, Radius = 65,
                };
                runtime.World.Register(enemy);
                runtime.Delivery.Flush();
                runtime.Tick(.3);
                TestAssert.True(minion.AttackTarget == enemy.NetId,
                    $"{team}: visible target acquired during Snare");
                enemy.Alive = false;
                runtime.Minions!.NotifyTargetLost(enemy.NetId);
                runtime.Tick(.9);
                TestAssert.True(minion.AttackTarget is null && !minion.LaneDone &&
                                minion.Waypoints.Count > 0 && minion.Waypoints[^1] == goal,
                    $"{team}: target loss returns to the saved lane suffix");
                TestAssert.True(runtime.Navigation.IsMovementWalkable(minion.Position),
                    $"{team}: returned march remains on movement terrain");
            }
        }
    }

    [Check("minions-live-teleport-target-release-restores-lane-progress")]
    public static void RealTeleportFreezeReleasesTheMinionLane()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cancel in new[] { false, true })
        {
            var setup = StartMarch(team);
            using var runtime = setup.Runtime;
            var minion = setup.Minion;
            var laneGoal = minion.Waypoints[^1];
            // A caster outside the wave's acquisition band can channel the
            // actual summoner spell without becoming its combat target.
            var caster = new Entity(runtime.World.Allocate(UnitType.Hero),
                EntityKind.Hero, UnitType.Hero, team, new Position2(7000, 7000))
            {
                ChampionName = "Annie", Name = "TeleportLaneCaster",
                Alive = true, Created = true, Visible = true,
                SummonerSpells = ["SummonerTeleport", "SummonerFlash"],
            };
            runtime.World.Register(caster);
            runtime.HeroStats.Refresh(caster);
            caster.Health = caster.MaxHealth;
            caster.Mana = caster.MaxMana;

            var packet = new PacketBuffer(34);
            packet.WriteBytes(new byte[5]);
            packet.WriteByte(1); // summoner slot zero
            for (var index = 0; index < 2; index++)
            {
                packet.WriteSingle(minion.Position.X);
                packet.WriteSingle(0);
                packet.WriteSingle(minion.Position.Z);
            }
            packet.WriteUInt32(minion.NetId);
            TestAssert.True(runtime.Spells.HandleCastRequest(caster, packet.ToArray(),
                runtime.World.Clock.Now()), $"{team}: real Teleport starts");
            TestAssert.True(minion.Buffs.Any(buff => buff.Name == "Teleport_Target"),
                $"{team}: target receives the channel Stun");
            // The freeze is the ordinary buff lifecycle now, not the hand-written FreezeTeleportTarget: AddBuff
            // cancels the public route and sends the client its stationary singleton, and the wave's unspent
            // suffix is retained for the release. The unit's own Waypoints stay empty while the lock holds.
            TestAssert.Equal(0, minion.Waypoints.Count,
                $"{team}: the Teleport lock cancels the public route");
            TestAssert.True(minion.InterruptedLaneWaypoints.Count > 0,
                $"{team}: the Teleport lock retains the wave's unspent lane suffix");

            if (cancel)
            {
                // A new control on the caster interrupts the channel (SummonerTeleport's BuffOnAllowAdd gate), but
                // it is not TeleportCancel. The shipped Teleport_DeathRemoval strips the target lock only on a
                // TeleportCancel cast or the caster's death, so an interrupted channel leaves it running to its own
                // expiry — SummonerTeleport.TargetExecute adds it with Duration 0.1 over its BuffDuration.
                TestAssert.True(runtime.Buffs.AddBuff(caster, "CancelChannelStun",
                    BuffType.Stun, .4, setup.Observer.NetId,
                    canMitigateDuration: false) is not null);
                runtime.Tick(.1);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
                    $"{team}/{cancel}: the interrupting control ends the channel");
                TestAssert.True(minion.Buffs.Any(buff => buff.Name == "Teleport_Target"),
                    $"{team}/{cancel}: an interrupted channel leaves the shipped target lock running");
                runtime.Tick(4.2);
            }
            else runtime.Tick(4.1);
            TestAssert.False(minion.Buffs.Any(buff => buff.Name == "Teleport_Target"),
                $"{team}/{cancel}: the shipped target lock expires at BuffDuration + 0.1");
            TestAssert.False(minion.LaneDone,
                $"{team}/{cancel}: channel end is not a lane terminus");
            var releasedPosition = minion.Position;
            runtime.Tick(.8);
            TestAssert.True(minion.Waypoints.Count > 0 && minion.Waypoints[^1] == laneGoal,
                $"{team}/{cancel}: target resumes its original lane suffix");
            TestAssert.True(Distance(minion.Position, releasedPosition) > 15.0,
                $"{team}/{cancel}: target walks after Teleport release");
            TestAssert.True(runtime.Navigation.IsMovementWalkable(minion.Position),
                $"{team}/{cancel}: released lane march remains on movement terrain");
            AssertOutsideStaticBodies(runtime, minion, $"{team}/{cancel}");
        }
    }

    [Check("minions-live-control-displacement-wall-rejoin-waits-for-safe-path")]
    public static void DisplacementAcrossMapWallDoesNotPublishRawLaneChord()
    {
        var nearSide = new Position2(7395, 7071);
        var farSide = new Position2(8300, 7400);
        var laneHead = new Position2(7394, 7071);
        var laneGoal = new Position2(7393, 7071);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            // Target death uses ordinary ReturnToLane, without a control buff
            // or the new interrupted-march cache. Prove the shared raw
            // fallback cannot publish a wall chord on this independent path.
            AssertOrdinaryTargetLossWaitsForSafeWallRejoin(team,
                farSide, laneHead, laneGoal);

            var setup = StartMarch(team);
            using var runtime = setup.Runtime;
            var minion = setup.Minion;
            var navigation = runtime.Navigation;
            TestAssert.True(navigation.IsMovementWalkable(nearSide) &&
                            navigation.IsMovementWalkable(farSide) &&
                            navigation.IsMovementWalkable(laneHead) &&
                            navigation.IsMovementWalkable(laneGoal),
                $"{team}: both sides and the saved lane tail are open cells");
            TestAssert.True(navigation.HasHeroMovementLineOfSight(nearSide, laneHead) &&
                            !navigation.HasHeroMovementLineOfSight(farSide, laneHead) &&
                            navigation.FindPath(farSide, laneHead).Count > 1,
                $"{team}: direct rejoin crosses a real Map1 wall but a detour exists");
            minion.Position = nearSide;
            minion.Waypoints = [laneHead, laneGoal];
            minion.AnchorPath = [nearSide, laneHead, laneGoal];
            // Batch 211: a displacement carries its status (a stun), as the shipped ones do. This stun ends 0.3 s after the 1.0 s move
            // lands, so OnCanMove releases the AI after the landing, inside the window the connector check below watches (the old
            // hold's release point, now the status's end).
            TestAssert.True(runtime.Buffs.AddBuff(minion, "wall-rejoin-displacement-stun", BuffType.Stun, 1.3,
                setup.Observer.NetId, new BuffPayload { ActionDisabled = true }, canMitigateDuration: false) is not null,
                $"{team}: the displacement's stun is admitted");
            TestAssert.True(runtime.Movement.StartForcedMove(minion.NetId, farSide,
                speed: 1200, duration: 1.0, sourceNetId: setup.Observer.NetId,
                effect: "wall-rejoin-displacement", cancelOrder: true),
                $"{team}: scripted displacement takes ownership of the lane order");
            runtime.Tick(1.1);
            TestAssert.True(minion.ForcedMove is { Completed: true } &&
                            Distance(minion.Position, farSide) <= 1.0,
                $"{team}: forced movement placed the actor on the far side");
            // A spell owner normally retires its completed displacement.
            minion.ForcedMove = null;
            setup.Scout.Position = VisiblePoint(navigation, farSide, 300);
            runtime.Delivery.Flush();
            TestAssert.True(setup.Witness.View.Visible.Contains(minion.NetId),
                $"{team}: the wall-side observer receives subsequent lane orders");
            setup.Capture.Clear();
            var attempts = 0;
            navigation.FindPathOverride = (from, _) =>
            {
                if (Distance(from, farSide) <= 2.0) attempts++;
                return [];
            };
            runtime.Tick(.6);
            TestAssert.True(attempts > 0 && minion.Waypoints.Count == 0 &&
                            !minion.LaneDone,
                $"{team}: failed connector waits instead of publishing a wall chord");
            TestAssert.True(setup.Capture.Waypoints(minion.NetId).All(route =>
                    route.Length <= 1 || RouteClear(navigation, route)),
                $"{team}: failed connector publishes no wall-crossing 0xC1");
            navigation.FindPathOverride = null;
            runtime.Tick(.5);
            TestAssert.True(minion.Waypoints.Count > 0 &&
                            minion.Waypoints[^1] == laneGoal && !minion.LaneDone,
                $"{team}: next scan reconnects to the original lane goal");
            TestAssert.True(RouteClear(navigation,
                    new[] { minion.Position }.Concat(minion.Waypoints).ToArray()) &&
                            RouteClear(navigation, minion.AnchorPath),
                $"{team}: authoritative and anchor routes clear the movement grid");
            var announced = setup.Capture.Waypoints(minion.NetId);
            TestAssert.True(announced.Any(route => route.Length > 1 &&
                            Distance(route[^1], laneGoal) <= 2.0),
                $"{team}: the rejoined suffix is announced on 0xC1");
            TestAssert.True(announced.All(route => route.Length <= 1 ||
                            RouteClear(navigation, route)),
                $"{team}: every emitted 0xC1 segment clears the exact movement grid");
            var resumedAt = minion.Position;
            runtime.Tick(.6);
            TestAssert.True(Distance(minion.Position, resumedAt) > 10.0 &&
                            !minion.LaneDone,
                $"{team}: the recovered route makes authoritative progress around the wall");
        }
    }

    [Check("minions-live-turret-side-lane-return-recovers")]
    public static void TurretSideLaneReturnDoesNotFreeze()
    {
        // Playtest #8, 25 Sept 2026: Chaos minions beside Turret_T2_C_05
        // logged target-lost-wait-for-lane and stood in March with no route.
        var logged = new Position2(8501, 8443);
        var displacedFrom = new Position2(8653, 8673);
        foreach (var displaced in new[] { false, true })
        {
            var label = displaced ? "forced-move release" : "target loss";
            var setup = StartMarch(Team.Chaos);
            using var runtime = setup.Runtime;
            var minion = setup.Minion;
            var navigation = runtime.Navigation;
            TestAssert.True(minion.MinionType == MinionType.Melee, $"{label}: logged melee minion");
            // Logged lane route, continued along the production Chaos mid lane.
            var splice = new Position2(7655, 7755);
            var route = runtime.Waves!.LaneRoute(Team.Chaos, "C");
            var nearest = Enumerable.Range(0, route.Count)
                .MinBy(index => Distance(route[index], splice));
            var lane = new[] { displacedFrom, new Position2(8445, 8563),
                    new Position2(8167, 8223), splice }
                .Concat(route.Skip(nearest + 1)).ToList();
            var laneGoal = lane[^1];
            TestAssert.True(navigation.IsMovementWalkable(logged) &&
                            navigation.IsMovementWalkable(displacedFrom),
                $"{label}: the logged turret-side points are open cells");
            setup.Scout.Position = VisiblePoint(navigation, logged, 500);
            minion.Target = null;
            minion.MinionState = MinionAiState.March;
            minion.LaneDone = false;

            if (!displaced)
            {
                minion.Position = logged;
                minion.Waypoints.Clear();
                minion.AnchorPath = [logged];
                minion.LaneWaypoints = lane.ToList();
                var target = new Entity(runtime.World.Allocate(UnitType.Hero),
                    EntityKind.Hero, UnitType.Hero, Team.Order, VisiblePoint(navigation, logged, 150))
                {
                    ChampionName = "Annie", Name = "TurretSideLaneTarget",
                    Alive = true, Created = true, Visible = true,
                    Health = 5000, MaxHealth = 5000, Radius = 65,
                };
                runtime.World.Register(target);
                runtime.Delivery.Flush();
                // minionLib 0.0 arms TimerFindEnemies on DELAY_FIND_ENEMIES, so Minion.lua 0.2's
                // acquisition runs on that 0.25 s grid; Minion.lua 0.4's AI_ATTACKMOVESTATE arm has
                // meanwhile walked the planted minion forward again (the playtest freeze is over).
                for (var tick = 0; tick < 30 && minion.AttackTarget is null; tick++)
                    runtime.Tick(1.0 / 30.0);
                TestAssert.True(minion.MinionState == MinionAiState.Engage &&
                                minion.AttackTarget == target.NetId,
                    $"{label}: the minion acquires its in-range target");
                TestAssert.True(Distance(minion.Position, logged) <= minion.Speed * 0.3,
                    $"{label}: the planted minion decides inside one find-enemies window");
                TestAssert.True(runtime.Combat.Kill(target, minion.NetId),
                    $"{label}: real target death calls ReturnToLane");
                TestAssert.True(minion.AttackTarget is null,
                    $"{label}: no replacement enemy is nearby");
            }
            else
            {
                minion.Position = displacedFrom;
                minion.Waypoints = lane.Skip(1).ToList();
                minion.AnchorPath = lane.ToList();
                minion.LaneWaypoints.Clear();
                // Batch 211: the displacement carries a status that outlasts its 1.0 s move, so the AI stays held through the move.
                TestAssert.True(runtime.Buffs.AddBuff(minion, "turret-side-displacement-stun", BuffType.Stun, 2.0,
                    setup.Observer.NetId, new BuffPayload { ActionDisabled = true }, canMitigateDuration: false) is not null,
                    $"{label}: the displacement's stun is admitted");
                TestAssert.True(runtime.Movement.StartForcedMove(minion.NetId, logged,
                    speed: 1200, duration: 1.0, sourceNetId: setup.Observer.NetId,
                    effect: "turret-side-displacement", cancelOrder: true),
                    $"{label}: displacement takes ownership of the lane order");
                runtime.Tick(1.1);
                TestAssert.True(minion.ForcedMove is { Completed: true } &&
                                Distance(minion.Position, logged) <= 1.0,
                    $"{label}: forced movement landed at the logged point");
                TestAssert.True(minion.InterruptedLaneWaypoints.Count > 0,
                    $"{label}: the cancelled order kept the unspent lane suffix");
                minion.ForcedMove = null;
            }

            runtime.Tick(1.0);
            TestAssert.True(minion.Waypoints.Count > 0 && minion.Waypoints[^1] == laneGoal &&
                            !minion.LaneDone && minion.AttackTarget is null,
                $"{label}: within a second the minion has a route to the lane goal");
            TestAssert.True(Distance(minion.Position, logged) > 15.0,
                $"{label}: the minion walks away from the turret-side point");
            TestAssert.True(navigation.IsMovementWalkable(minion.Position),
                $"{label}: the recovered march remains on movement terrain");
            AssertOutsideStaticBodies(runtime, minion, label);
        }
    }

    [Check("minions-live-captured-order-nexus-return-clears-structure")]
    public static void CapturedOrderMeleeReturnsPastItsOwnNexus()
    {
        var setup = StartMarch(Team.Order);
        using var runtime = setup.Runtime;
        var minion = setup.Minion;
        var origin = new Position2(1583.623313478836, 371.13661513672514);
        // Retained suffix from the preceding return, before Shaco was chased again.
        List<Position2> lane = [new(747, 1615), new(493, 3281), new(647, 3515),
            new(1097, 3965), new(1077, 4485), new(825, 5715), new(815, 6497),
            new(803, 7565), new(825, 8851), new(879, 10201), new(1103, 11379),
            new(1563, 12237), new(2315, 12879), new(3081, 13251), new(3953, 13375),
            new(5099, 13387), new(6263, 13425), new(7525, 13449), new(8679, 13451), new(9881, 13087)];
        var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus &&
            unit.Team == Team.Order);
        // The Nexus is no body since batch 78 (P3): the stock client stamps its PathfindingCollisionRadius into the nav grid once, at creation,
        // and the march only has to keep out of the stamped cells (a cell is stamped when its centre lies inside the radius, so a point of an
        // unstamped cell is at most half a cell diagonal inside the circle). The old clearance (the Nexus body plus the minion, 400.52) is gone.
        var stamp = MapDefinitions.StructureStamps.Single(item => Distance(item.Center, nexus.Position) < 1.0);
        TestAssert.Near(352.8, stamp.Radius, .05, "the Order Nexus footprint is its pathfinding radius");
        var obstacle = new NavigationObstacle(nexus.Position, stamp.Radius - runtime.Navigation.CellSize * Math.Sqrt(0.5));
        minion.Position = origin;
        minion.Target = null;
        minion.Waypoints.Clear();
        minion.LaneWaypoints = lane.ToList();
        minion.MinionState = MinionAiState.Engage;
        minion.AttackTarget = 0x40000002; // Shaco has left acquisition range.
        minion.LaneDone = false;
        minion.MinionLane = "L";
        setup.Scout.Position = VisiblePoint(runtime.Navigation, origin, 500);
        runtime.Delivery.Flush();
        setup.Capture.Clear();
        runtime.Minions!.NotifyTargetLost(0x40000002);
        runtime.Delivery.Flush();
        AssertClear(new[] { origin }.Concat(minion.Waypoints).ToArray());
        foreach (var route in setup.Capture.Waypoints(minion.NetId)) AssertClear(route);
        for (var tick = 0; tick < 200 && minion.Position.Z < 3500; tick++)
        {
            runtime.Tick(.1);
            TestAssert.True(minion.AttackTarget is null, "lane recovery needs no new enemy");
            TestAssert.True(runtime.Navigation.IsMovementWalkable(minion.Position) &&
                            Distance(minion.Position, nexus.Position) >= obstacle.Radius - 1e-6,
                "the marching minion stays outside its live nexus's stamp");
        }
        TestAssert.True(minion.Position.Z > 3500 && minion.Waypoints[^1] == lane[^1],
            "the minion passes the nexus and resumes its retained lane within twenty seconds");
        foreach (var route in setup.Capture.Waypoints(minion.NetId)) AssertClear(route);

        void AssertClear(IReadOnlyList<Position2> route)
        {
            for (var index = 1; index < route.Count; index++)
                TestAssert.True(runtime.Navigation.SegmentClearOfUnits(route[index - 1], route[index], [obstacle]),
                    $"accepted connector clears nexus: {route[index - 1]} -> {route[index]}");
            if (route.Count < 2) return;
            foreach (var start in new[] { route[0], runtime.Movement.ClientAnchor(route[0]) })
            {
                var position = start;
                var remaining = route.Skip(1).ToList();
                while (remaining.Count > 0)
                {
                    var next = Amberline.Simulation.Movement.MovementSystem.FixedOriginStep(
                        position, remaining, minion.Speed * .1).Position;
                    TestAssert.True(runtime.Navigation.SegmentClearOfUnits(position, next, [obstacle]),
                        "exact and quantized followers clear the nexus");
                    position = next;
                }
            }
        }
    }

    private static void AssertOrdinaryTargetLossWaitsForSafeWallRejoin(Team team,
        Position2 farSide, Position2 laneHead, Position2 laneGoal)
    {
        var setup = StartMarch(team);
        using var runtime = setup.Runtime;
        var minion = setup.Minion;
        var navigation = runtime.Navigation;
        TestAssert.True(navigation.IsMovementWalkable(farSide) &&
                        navigation.IsMovementWalkable(laneHead) &&
                        navigation.IsMovementWalkable(laneGoal) &&
                        !navigation.HasHeroMovementLineOfSight(farSide, laneHead) &&
                        navigation.FindPath(farSide, laneHead).Count > 1,
            $"{team}: ordinary target-loss witness has a blocked direct chord and safe detour");
        minion.Position = farSide;
        minion.Target = null;
        minion.Waypoints.Clear();
        minion.AnchorPath = [farSide];
        minion.LaneWaypoints = [laneHead, laneGoal];
        // minionLib 0.0's OnInit is SetState(AI_IDLE), and Minion.lua 0.4 gives that state its birth
        // scan (FindTargetOrMove) before any march. A March minion would instead walk the seeded
        // tail into a live route first (Minion.lua 0.4's other arm) and only then scan, so the saved
        // tail would no longer be what a target loss returns to — which is this check's subject.
        minion.MinionState = MinionAiState.Idle;
        minion.LaneDone = false;
        setup.Scout.Position = VisiblePoint(navigation, farSide, 500);
        var targetPoint = VisiblePoint(navigation, farSide, 250);
        var target = new Entity(runtime.World.Allocate(UnitType.Hero),
            EntityKind.Hero, UnitType.Hero,
            team == Team.Order ? Team.Chaos : Team.Order, targetPoint)
        {
            ChampionName = "Annie", Name = "LaneReturnWallTarget",
            Alive = true, Created = true, Visible = true,
            Health = 5000, MaxHealth = 5000, Radius = 65,
        };
        runtime.World.Register(target);
        runtime.Delivery.Flush();
        TestAssert.True(setup.Witness.View.Visible.Contains(minion.NetId) &&
                        setup.Witness.View.Visible.Contains(target.NetId),
            $"{team}: same-team witness sees the minion and its live enemy target");
        // Minion.lua 0.4's AI_IDLE arm is the birth scan (FindTargetOrMove), so the acquisition is
        // the first decision frame and the seeded tail is still what Acquire retains.
        runtime.Tick(1.0 / 30.0);
        TestAssert.True(minion.MinionState == MinionAiState.Engage &&
                        minion.AttackTarget == target.NetId &&
                        minion.LaneWaypoints.SequenceEqual(new[] { laneHead, laneGoal }),
            $"{team}: real acquisition retains the known lane suffix");
        setup.Capture.Clear();
        var attempts = 0;
        navigation.FindPathOverride = (from, _) =>
        {
            if (Distance(from, minion.Position) <= 2.0) attempts++;
            return [];
        };
        TestAssert.True(runtime.Combat.Kill(target, minion.NetId),
            $"{team}: real target death calls ordinary ReturnToLane");
        runtime.Delivery.Flush();
        var firstPackets = setup.Capture.Waypoints(minion.NetId);
        TestAssert.True(firstPackets.Count > 0,
            $"{team}: the visible target-loss order was replicated");
        TestAssert.True(firstPackets.All(route => route.Length <= 1 ||
                        RouteClear(navigation, route)),
            $"{team}: ordinary ReturnToLane must not publish a wall-crossing 0xC1");
        TestAssert.True(attempts > 0 && minion.Waypoints.Count == 0 &&
                        !minion.LaneDone && minion.AttackTarget is null,
            $"{team}: blocked ordinary return retains lane intent without an unsafe order");
        navigation.FindPathOverride = null;
        runtime.Tick(.5);
        TestAssert.True(minion.Waypoints.Count > 0 &&
                        minion.Waypoints[^1] == laneGoal && !minion.LaneDone &&
                        RouteClear(navigation,
                            new[] { minion.Position }.Concat(minion.Waypoints).ToArray()),
            $"{team}: ordinary return retries and reconnects to the saved lane goal");
        var announced = setup.Capture.Waypoints(minion.NetId);
        TestAssert.True(announced.Any(route => route.Length > 1 &&
                        Distance(route[^1], laneGoal) <= 2.0) &&
                        announced.All(route => route.Length <= 1 ||
                            RouteClear(navigation, route)),
            $"{team}: retry announces only safe client-quantized connector legs");
        var resumedAt = minion.Position;
        runtime.Tick(.6);
        TestAssert.True(Distance(minion.Position, resumedAt) > 10.0,
            $"{team}: ordinary return makes authoritative progress around the wall");
    }

    internal static (MatchRuntime Runtime, Entity Minion, Entity Observer,
        Entity Scout, ReplicationObserver Witness, CaptureTransport Capture) StartMarch(Team team)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, orderChampion: "Annie", chaosChampion: "Annie",
            solo: true, originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, minionFidelity: true);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null);
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1));
        TestAssert.True(runtime.Session.Start());
        var observer = runtime.Session.Participants.Single().Hero ??
            throw new InvalidOperationException("production observer has no hero");
        observer.Protected = true;
        observer.Position = new Position2(7601.74, 6449.17);
        observer.Waypoints.Clear();
        runtime.Waves!.AutomaticSpawningEnabled = false;
        var now = runtime.Combat.CurrentTime;
        var minion = runtime.Waves.SpawnFixture(team,
            LevelScriptFixtures.Wave(runtime.World, team, "C", 2)[0],
            slot: 0, lane: "C", now: now, scheduledElapsed: now);
        // The real same-team observer keeps a Chaos minion created through
        // Delivery's Known set; its Stun route clear is wire-ready only.
        var direction = minion.Waypoints[0] - minion.Position;
        var length = Distance(direction, Position2.Zero);
        var lateral = length > 1e-9
            ? new Position2(-direction.Z / length, direction.X / length)
            : new Position2(1, 0);
        var offsets = new[]
        {
            new Position2(lateral.X * 900, lateral.Z * 900),
            new Position2(-lateral.X * 900, -lateral.Z * 900), new Position2(900, 0),
            new Position2(-900, 0), new Position2(0, 900), new Position2(0, -900),
        };
        var scoutPoint = offsets.Select(offset => minion.Position + offset)
            .First(point => Visible(runtime.Navigation, point, minion.Position));
        var scout = new Entity(runtime.World.Allocate(UnitType.Hero),
            EntityKind.Hero, UnitType.Hero, team, scoutPoint)
        {
            ChampionName = "Annie", Name = "LaneVisionObserver", Alive = true,
            Created = true, Visible = true, Protected = true,
            Health = 1000, MaxHealth = 1000, Radius = 65,
        };
        runtime.World.Register(scout);
        var capture = new CaptureTransport();
        var witness = new ReplicationObserver(90, team, scout.NetId, capture);
        runtime.Delivery.AddObserver(witness);
        runtime.Delivery.Flush();
        TestAssert.True(witness.View.Known.Contains(minion.NetId) &&
                        witness.View.Visible.Contains(minion.NetId) && minion.Created,
            $"{team}: actual team sight creates the lane actor on wire");
        return (runtime, minion, observer, scout, witness, capture);
    }

    private static double Distance(Position2 first, Position2 second) =>
        double.Hypot(first.X - second.X, first.Z - second.Z);

    internal static Position2 VisiblePoint(NavigationGrid grid, Position2 origin,
        double distance) => new[]
        {
            new Position2(distance, 0), new Position2(-distance, 0),
            new Position2(0, distance), new Position2(0, -distance),
            new Position2(distance * .7, distance * .7),
            new Position2(-distance * .7, -distance * .7),
        }.Select(offset => origin + offset).First(point => Visible(grid, point, origin));

    private static bool Visible(NavigationGrid grid, Position2 point, Position2 origin) =>
        grid.IsMovementWalkable(point) && grid.HasVisionLos(point, origin) &&
        grid.BrushAllowsVision(point, origin);

    private static bool RouteClear(NavigationGrid grid, IReadOnlyList<Position2> route)
    {
        for (var index = 1; index < route.Count; index++)
            if (!grid.HasHeroMovementLineOfSight(route[index - 1], route[index]))
                return false;
        return true;
    }

    private static void AssertOutsideStaticBodies(MatchRuntime runtime, Entity minion,
        string label) => TestAssert.True(runtime.World.Entities.Values
            .Where(other => other != minion && other.Alive &&
                other.UnitType is UnitType.Turret or UnitType.Structure)
            .All(other => Distance(minion.Position, other.Position) + 3.0 >=
                runtime.Movement.BodyClearance(minion, other)),
            $"{label}: resumed body stays outside live building footprints");

    internal sealed class CaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = [];

        public void Send(PlaintextPacket packet) => _packets.Add(packet);

        public void Clear() => _packets.Clear();

        /// <summary>Every packet delivered since the last <see cref="Clear"/>, in send order.</summary>
        public IReadOnlyList<PlaintextPacket> Packets => _packets;

        public IReadOnlyList<Position2[]> Waypoints(uint netId) => _packets
            .Select(packet => packet.Plaintext.Span.ToArray())
            .Where(bytes => bytes.Length >= 9 && bytes[0] == VisionPackets.WaypointListOpcode &&
                            (bytes.Length - 9) % 8 == 0 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == netId)
            .Select(bytes => Enumerable.Range(0, (bytes.Length - 9) / 8)
                .Select(index => new Position2(
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(9 + index * 8)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(13 + index * 8))))
                .ToArray())
            .ToArray();
    }
}
