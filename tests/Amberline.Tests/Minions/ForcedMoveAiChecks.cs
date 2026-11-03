using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;
using static Amberline.Tests.ControlMarchRecoveryChecks;

namespace Amberline.Tests;

/// <summary>
/// Night 2, F-FORCED (17 October 2026). The real-client captures of batches 34 and 35 held 62 lane-minion displacements; 59 of
/// them got a minion AI route inside the dash (target-chase, target-lost, congestion-route) and the stock client, which
/// replaces a dash with the first 0xC1 it gets, was snapped 365-400 u by the next packet. These checks read the bytes the
/// observer of the minion's team receives, per 1/30 s step: while a script displaces a minion the AI sends it nothing, and
/// when the displacement is over it sends one route from the landing point.
/// </summary>
public static class ForcedMoveAiChecks
{
    private const double Step = 1.0 / 30.0;

    internal enum Displacement { Condemn, PowerFist }

    /// <summary>One movement packet the observer received for the unit: 0xC1 (with its points) or the 0x67 dash.</summary>
    internal readonly record struct Sent(int Tick, byte Opcode, Position2[] Points);

    /// <summary>The movement packets of one unit in the capture since its last clear, then clears it.</summary>
    internal static List<Sent> Movement(CaptureTransport capture, uint netId, int tick)
    {
        var sent = Movement(capture.Packets, netId, tick);
        capture.Clear();
        return sent;
    }

    /// <summary>The movement packets of one unit in a packet list (not cleared).</summary>
    internal static List<Sent> Movement(IEnumerable<PlaintextPacket> packets, uint netId, int tick)
    {
        var sent = new List<Sent>();
        foreach (var packet in packets)
        {
            var bytes = packet.Plaintext.ToArray();
            if (bytes.Length >= 9 && bytes[0] == VisionPackets.WaypointListOpcode && (bytes.Length - 9) % 8 == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == netId)
                sent.Add(new Sent(tick, VisionPackets.WaypointListOpcode, Enumerable.Range(0, (bytes.Length - 9) / 8)
                    .Select(index => new Position2(
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(9 + 8 * index)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(13 + 8 * index)))).ToArray()));
            // Batch 315: a dash is 57 to 61 bytes (its two points are PathToNetworkData's; a short hop is a delta byte).
            else if (bytes.Length >= 57 && bytes[0] == VisionPackets.DashOpcode &&
                     BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)) == netId)
                sent.Add(new Sent(tick, VisionPackets.DashOpcode, []));
        }
        return sent;
    }

    /// <summary>
    /// The movement packets the client handles after the (first) dash, in arrival order. Batch 291 keeps one rule for batch 211 (no
    /// order while the dash runs) and batch 270 (the stationary anchors that wrap a displacement), read from the client: the channel-4
    /// packets of a flush are handled in order, and every route packet goes through the client's server-path follower
    /// (it seats the unit on the path's first point and replaces the path and its speed data), called by the
    /// 0xC1 handler and by the 0x67 entry. A 0xC1 ahead of the 0x67 in the dash's own flush (the stun's
    /// anchor) is therefore replaced by the dash, and one at or after the dash's wire end (the owner's landing anchor) seats a finished
    /// dash; only a 0xC1 after the 0x67 and before its wire end cuts the dash short.
    /// </summary>
    internal static List<Sent> AfterDash(List<Sent> log) =>
        log.SkipWhile(sent => sent.Opcode != VisionPackets.DashOpcode).Skip(1).ToList();

    /// <summary>The packets of a log as "tick:opcode x points", for failure messages.</summary>
    internal static string Describe(IEnumerable<Sent> log) =>
        string.Join(" ", log.Select(sent => $"{sent.Tick}:{sent.Opcode:X2}x{sent.Points.Length}"));

    internal static double Distance(Position2 first, Position2 second) =>
        double.Hypot(first.X - second.X, first.Z - second.Z);

    /// <summary>A march started in the open middle of the lane, where every direction a displacement takes is floor.</summary>
    internal static (MatchRuntime Runtime, Entity Minion, Entity Observer, Entity Scout, ReplicationObserver Witness,
        CaptureTransport Capture) StartMidLaneMarch(Team team)
    {
        var setup = StartMarch(team);
        var route = setup.Runtime.Waves!.LaneRoute(team, "C");
        var index = route.Count / 2;
        setup.Minion.Position = route[index];
        setup.Minion.Waypoints = route.Skip(index + 1).ToList();
        setup.Minion.AnchorPath = route.Skip(index).ToList();
        setup.Scout.Position = VisiblePoint(setup.Runtime.Navigation, route[index], 600);
        setup.Runtime.Delivery.Flush();
        return setup;
    }

    /// <summary>An enemy hero 300 u from the minion.</summary>
    internal static Entity MakeEnemy(MatchRuntime runtime, Entity minion, Team team)
    {
        var enemy = new Entity(runtime.World.Allocate(UnitType.Hero),
            EntityKind.Hero, UnitType.Hero, team == Team.Order ? Team.Chaos : Team.Order,
            VisiblePoint(runtime.Navigation, minion.Position, 300))
        {
            ChampionName = "Annie", Name = "ForcedMoveEnemy", Alive = true, Created = true, Visible = true,
            Health = 5000, MaxHealth = 5000, Radius = 65,
        };
        runtime.World.Register(enemy);
        runtime.Delivery.Flush();
        return enemy;
    }

    /// <summary>An enemy hero in reach: the minion engages it.</summary>
    internal static Entity EngageEnemy(MatchRuntime runtime, Entity minion, Team team, string label)
    {
        var enemy = MakeEnemy(runtime, minion, team);
        runtime.Tick(Step);
        TestAssert.True(minion.MinionState == MinionAiState.Engage && minion.AttackTarget == enemy.NetId,
            $"{label}: the minion engages the enemy hero before it is displaced");
        return enemy;
    }

    /// <summary>
    /// What a spell does to a minion it displaces (Condemn, Power Fist): the stun first, then the forced move and its dash
    /// packet. Returns the move and the announced landing.
    /// </summary>
    internal static (ForcedMoveState Move, Position2 Landing) Displace(
        MatchRuntime runtime, Entity minion, Entity caster, Displacement kind, string label, bool withStun = true)
    {
        var (distance, speed, gravity, stun, effect) = kind == Displacement.Condemn
            ? (475.0, 2000.0, 0.0, 0.25, "VayneCondemn")
            : (80.0, 90.0, 20.0, 0.5, "PowerFistSlow");
        var duration = kind == Displacement.Condemn ? distance / speed + 0.05 : distance / speed;
        // A direction whose whole distance is open floor, away from the caster first.
        var away = minion.Position - caster.Position;
        var length = Distance(minion.Position, caster.Position);
        var baseAngle = Math.Atan2(away.Z / length, away.X / length);
        Position2? landing = null;
        foreach (var turn in new[] { 0.0, Math.PI / 2, -Math.PI / 2, Math.PI, Math.PI / 4, -Math.PI / 4 })
        {
            var requested = minion.Position + new Position2(
                Math.Cos(baseAngle + turn) * distance, Math.Sin(baseAngle + turn) * distance);
            var candidate = runtime.Movement.FirstCollisionDashLanding(minion.Position, requested, speed, duration, minion);
            if (Distance(minion.Position, candidate) >= distance - 3.0)
            {
                landing = candidate;
                break;
            }
        }
        TestAssert.True(landing is not null, $"{label}: an open direction for a {distance} u displacement");
        var end = landing!.Value;
        // The stun comes first, and with it the stationary anchor BuffLifecycle sends a stunned unit.
        if (withStun)
            TestAssert.True(runtime.Buffs.AddBuff(minion, kind == Displacement.Condemn ? "VayneCondemnMissile" : "PowerFistSlow",
                BuffType.Stun, stun, caster.NetId, new BuffPayload { ActionDisabled = true },
                canMitigateDuration: false) is not null, $"{label}: the displacement's stun is admitted");
        TestAssert.True(runtime.Movement.StartForcedMove(minion.NetId, end, speed, duration, caster.NetId, effect,
            cancelOrder: true, firstCollision: kind == Displacement.Condemn), $"{label}: the forced move starts");
        runtime.Effects.SendDash(minion, minion.Position, end, speed, gravity, runtime.World.Clock.SyncId());
        return (minion.ForcedMove!, end);
    }

    /// <summary>
    /// Displaces an engaged lane minion and plays the displacement out step by step, as the spell owner does: the owner retires
    /// the completed move in the step it completes and sends its landing anchor. <paramref name="trigger"/> runs once during
    /// the displacement, on the second step, and delivers an event the AI answers with an order when nothing holds it.
    /// </summary>
    internal static Played Play(Team team, Displacement kind, string trigger, bool ownerSendsLandingAnchor = true)
    {
        var label = $"{team}/{kind}/{trigger}";
        var setup = StartMidLaneMarch(team);
        using var runtime = setup.Runtime;
        var minion = setup.Minion;
        var enemy = trigger == "halted" ? MakeEnemy(runtime, minion, team) : EngageEnemy(runtime, minion, team, label);
        if (trigger == "halted") runtime.Minions!.HaltAI(minion);
        setup.Capture.Clear();
        var (move, landing) = Displace(runtime, minion, enemy, kind, label);
        runtime.Delivery.Flush();
        var log = Movement(setup.Capture, minion.NetId, 0);
        var completeTick = -1;
        var waypointsAtRoute = -1;
        for (var tick = 1; tick <= 70; tick++)
        {
            if (tick == 2) Trigger(runtime, minion, enemy, trigger);
            runtime.Tick(Step);
            log.AddRange(Movement(setup.Capture, minion.NetId, tick));
            // The route the AI sent after the landing is the one the server walks.
            if (completeTick > 0 && waypointsAtRoute < 0 && log.Any(sent => sent.Tick == tick &&
                    sent.Opcode == VisionPackets.WaypointListOpcode && sent.Points.Length > 1))
                waypointsAtRoute = minion.Waypoints.Count;
            if (completeTick < 0 && move.Completed)
            {
                completeTick = tick;
                // TickCondemn / CleanupCompletedMoves: the owner retires the completed move and anchors the unit where it stands.
                landing = minion.Position;
                minion.ForcedMove = null;
                if (ownerSendsLandingAnchor)
                    runtime.Effects.SendWaypoints(minion, [minion.Position], runtime.World.Clock.SyncId());
            }
        }
        TestAssert.True(completeTick > 0, $"{label}: the displacement completes");
        return new Played(log, completeTick, landing, waypointsAtRoute, minion.ForcedMove is null);
    }

    /// <summary>The movement packets of a played displacement, the step the move completed in and where the unit landed.</summary>
    internal sealed record Played(List<Sent> Log, int CompleteTick, Position2 Landing, int WaypointsAtEnd, bool Retired);

    private static void Trigger(MatchRuntime runtime, Entity minion, Entity enemy, string trigger)
    {
        switch (trigger)
        {
            case "none" or "halted":
                break;
            case "target-lost":
                // The target dies: CombatService tells the AI (NotifyTargetLost) in the same step.
                TestAssert.True(runtime.Combat.Kill(enemy, minion.NetId), "the target dies");
                break;
            case "taunt":
                TestAssert.True(runtime.Buffs.AddBuff(minion, "ForcedMoveTaunt", BuffType.Taunt, 0.2, enemy.NetId,
                    canMitigateDuration: false) is not null, "the taunt is admitted");
                break;
            case "help":
                minion.HelpTarget = enemy.NetId;
                minion.HelpRemaining = 1.0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger));
        }
    }

    /// <summary>
    /// The step (1/30 s) in which the displacement's status ends: the stun <see cref="Displace"/> puts on the unit (Condemn 0.25 s,
    /// Power Fist 0.5 s).
    /// </summary>
    private static int StatusEndTick(Displacement kind) =>
        (int)Math.Ceiling((kind == Displacement.Condemn ? 0.25 : 0.5) / Step - 1e-9);

    // Batch 211 (owner, 25 October 2026: follow the script). The AI is held while the displacement's status (the stun) runs, and
    // minionLib OnCanMove sends its one route when that status ends. Before this batch the hold ran until the landing plus 0.1 s,
    // and this check pinned that: no 0xC1 until the landing, then one route from it.
    [Check("minions.forced-move-holds-the-ai-then-one-route-from-the-landing")]
    public static void DisplacedMinionGetsNoOrderThenOneRoute()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { Displacement.Condemn, Displacement.PowerFist })
        {
            var label = $"{team}/{kind}";
            // Batch 211 follow-up: the AI decides when the status ends, but its packet waits for the dash's wire end (the stock client
            // replaces a dash with a 0xC1 it receives), so the order is observed at the completion step, not at the status end.
            var (log, completeTick, _, _, _) = Play(team, kind, "none");
            TestAssert.Equal(1, log.Count(sent => sent.Opcode == VisionPackets.DashOpcode),
                $"{label}: the observer receives the one dash packet");
            // Batch 291: the stun's anchor ahead of the dash (batch 270) is replaced by it (AfterDash); the rule reads the packets after it.
            var after = AfterDash(log);
            var early = after.Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode && sent.Tick < completeTick - 1).ToArray();
            TestAssert.Equal(0, early.Length,
                $"{label}: no 0xC1 while the dash runs, got ticks {string.Join(",", early.Select(sent => sent.Tick))}");
            var released = after.Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode &&
                sent.Tick >= completeTick - 1 && sent.Tick <= completeTick + 1).ToArray();
            // The owner's landing anchor (batch 270) and the AI's order(s).
            TestAssert.True(released.Length is >= 1 and <= 3,
                $"{label}: the landing anchor and the AI's order(s) at the dash's wire end (step {completeTick}), got {released.Length}: {Describe(log)}");
            TestAssert.True(log.Any(sent => sent.Opcode == VisionPackets.WaypointListOpcode && sent.Points.Length > 1 &&
                sent.Tick >= completeTick - 1), $"{label}: a route follows the dash, the server walks it");
        }
    }

    [Check("minions.knockback-order-at-dash-end")]
    public static void CondemnAndPowerFistSendTheirOrderAtTheDashEnd()
    {
        // Batch 211 follow-up: the shipped OnCanMove releases the AI when the status ends (Condemn 0.25 s, Power Fist 0.5 s), but the
        // stock client replaces a dash with the first 0xC1 it receives, so the packet is held until the dash's wire end and starts where
        // the dash lands. The check pins both: the status end comes first, and no packet leaves before the dash ends.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { Displacement.Condemn, Displacement.PowerFist })
        {
            var label = $"{team}/{kind}";
            var (log, completeTick, _, _, _) = Play(team, kind, "none");
            // Batch 291: the orders the client handles after the dash (the stun's anchor ahead of it is replaced by it, AfterDash).
            var orders = AfterDash(log).Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode).ToArray();
            TestAssert.True(orders.Length > 0, $"{label}: the AI orders the minion");
            TestAssert.True(StatusEndTick(kind) <= completeTick,
                $"{label}: the status ends (step {StatusEndTick(kind)}) no later than the dash does (step {completeTick})");
            TestAssert.True(orders[0].Tick >= completeTick - 1,
                $"{label}: no order before the dash ends: the first is at step {orders[0].Tick}, the dash ends at step {completeTick}");
            TestAssert.True(orders[0].Tick <= completeTick + 1,
                $"{label}: the order is sent at the dash's wire end (step {completeTick}), got step {orders[0].Tick}");
        }
    }

    [Check("minions.forced-move-defers-every-ai-entry-point")]
    public static void EveryAiEntryPointWaitsForTheLanding()
    {
        // The events a displaced minion's AI answers with an order when nothing holds it: its stun's end (OnCanMove, in every
        // run), the death of its target (NotifyTargetLost), a taunt (OnTauntBegin/OnTauntEnd) and a call for help. Batch 211: the
        // stun holds the AI until its status ends, so these triggers (delivered in the stun) are answered at the status end, inside
        // the dash, not at the landing.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { Displacement.Condemn, Displacement.PowerFist })
        foreach (var trigger in new[] { "target-lost", "taunt", "help" })
        {
            var label = $"{team}/{kind}/{trigger}";
            var (log, completeTick, _, _, _) = Play(team, kind, trigger);
            // Batch 291: the packets after the dash (the stun's anchor ahead of it is replaced by it, AfterDash).
            var after = AfterDash(log);
            var early = after.Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode && sent.Tick < completeTick - 1).ToArray();
            TestAssert.Equal(0, early.Length,
                $"{label}: no 0xC1 while the dash runs, got ticks {string.Join(",", early.Select(sent => sent.Tick))}");
            var routes = after.Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode &&
                sent.Tick >= completeTick - 1 && sent.Tick <= completeTick + 1).ToArray();
            // The first order can be a stop (OnTauntEnd or the stun's end): the walk that follows is checked below. The owner's landing
            // anchor (batch 270) comes with them.
            TestAssert.True(routes.Length >= 1,
                $"{label}: the AI orders the minion at the dash's wire end (step {completeTick}): {Describe(log)}");
            TestAssert.True(routes.Length <= 3, $"{label}: not a stream of orders ({routes.Length})");
            TestAssert.True(log.Any(sent => sent.Opcode == VisionPackets.WaypointListOpcode && sent.Points.Length > 1),
                $"{label}: the minion walks again");
        }
    }

    // Batch 291 (was minions.forced-move-needs-no-anchor-when-it-lands-on-its-dash, which pinned batch 270's deleted
    // DropRedundantDashAnchors): a lane minion's dash is wrapped like a hero's, by the stun's anchor ahead of it and the owner's anchor at
    // the landing. Neither cuts the dash (AfterDash: the dash replaces the anchor before it, the client's server-path follower; the
    // landing anchor seats the finished dash where it ends). With the AI halted nothing else speaks for the unit.
    [Check("minions.forced-move-anchors-wrap-the-dash-and-a-halted-ai-sends-nothing")]
    public static void LandingAnchorsWrapTheDashAndAHaltedAiSendsNothing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { Displacement.Condemn, Displacement.PowerFist })
        {
            var label = $"{team}/{kind}/halted";
            var (log, completeTick, landing, _, retired) = Play(team, kind, "halted");
            TestAssert.Equal(1, log.Count(sent => sent.Opcode == VisionPackets.DashOpcode), $"{label}: one dash");
            var before = log.TakeWhile(sent => sent.Opcode != VisionPackets.DashOpcode).ToArray();
            TestAssert.True(before.Length == 1 && before[0].Tick == 0 && before[0].Points.Length == 1,
                $"{label}: the stun's stationary anchor ahead of the dash, in its flush ({Describe(log)})");
            var after = AfterDash(log);
            TestAssert.True(after.Count == 1 && after[0].Opcode == VisionPackets.WaypointListOpcode && after[0].Points.Length == 1 &&
                after[0].Tick >= completeTick && Distance(after[0].Points[0], landing) <= 3.0,
                $"{label}: then only the owner's anchor at the landing, at the dash's end (step {completeTick}): {Describe(log)}");
            TestAssert.True(retired, $"{label}: the owner retired the move");
        }
    }

    [Check("minions.forced-move-anchor-stays-when-the-landing-is-elsewhere")]
    public static void CutShortDisplacementKeepsItsLandingAnchor()
    {
        // The dash announced a landing 475 u away and the unit stopped 100 u short of it (a body in the way): the client
        // would stand on the wrong point, so the owner's anchor reaches it.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var label = $"{team}/cut-short";
            var setup = StartMidLaneMarch(team);
            using var runtime = setup.Runtime;
            var minion = setup.Minion;
            var enemy = MakeEnemy(runtime, minion, team);
            runtime.Minions!.HaltAI(minion);
            setup.Capture.Clear();
            var (move, landing) = Displace(runtime, minion, enemy, Displacement.Condemn, label);
            runtime.Delivery.Flush();
            var log = Movement(setup.Capture, minion.NetId, 0);
            var back = (landing - minion.Position);
            var cut = minion.Position + new Position2(back.X * 0.8, back.Z * 0.8);
            for (var tick = 1; tick <= 3; tick++)
            {
                runtime.Tick(Step);
                log.AddRange(Movement(setup.Capture, minion.NetId, tick));
            }
            // The unit is stopped short and the owner retires the move where it stands.
            move.Completed = true;
            minion.Position = cut;
            minion.ForcedMove = null;
            runtime.Effects.SendWaypoints(minion, [minion.Position], runtime.World.Clock.SyncId());
            runtime.Tick(Step);
            log.AddRange(Movement(setup.Capture, minion.NetId, 4));
            // Batch 291: the stun's anchor ahead of the dash (batch 270) is replaced by the dash (AfterDash); after it, the one anchor.
            var anchors = AfterDash(log).Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode).ToArray();
            TestAssert.Equal(1, anchors.Length, $"{label}: the one anchor that the client needs after the dash");
            TestAssert.True(anchors[0].Points.Length == 1 && Distance(anchors[0].Points[0], cut) <= 3.0,
                $"{label}: it is stationary and on the unit's real position");
        }
    }

    /// <summary>
    /// Batch 263 (Gragas's Explosive Cask, match seed 265: packet=16, every one an order during a dash): <c>MoveAwayCollision</c>'s
    /// stun ends inside its own knock-back (the cask: 0.5 s of a 900-unit dash at 900). The shipped buff's <c>OnBuffDeactivate</c>
    /// only gives CanMove, CanAttack and CanCast back, and the client walks its dash to the end, so the knock-back is not cut at the
    /// buff's end: no stop goes out inside the dash, and the AI's order (minionLib <c>OnCanMove</c> at the buff's end) waits for the
    /// move's end like every order of a displaced minion.
    /// </summary>
    [Check("minions.knockback-outlasting-its-buff-keeps-the-dash")]
    public static void KnockbackOutlastingItsBuffKeepsTheDash()
    {
        const double distance = 450.0, speed = 450.0, buffSeconds = 0.5;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var label = team.ToString();
            var setup = StartMidLaneMarch(team);
            using var runtime = setup.Runtime;
            var minion = setup.Minion;
            var enemy = EngageEnemy(runtime, minion, team, label);
            setup.Capture.Clear();
            var away = minion.Position - enemy.Position;
            var baseAngle = Math.Atan2(away.Z, away.X);
            Position2? open = null;
            foreach (var turn in new[] { 0.0, Math.PI / 2, -Math.PI / 2, Math.PI })
            {
                var requested = minion.Position + new Position2(
                    Math.Cos(baseAngle + turn) * distance, Math.Sin(baseAngle + turn) * distance);
                var candidate = runtime.Movement.FirstCollisionDashLanding(minion.Position, requested, speed, distance / speed, minion);
                if (Distance(minion.Position, candidate) >= distance - 3.0) { open = candidate; break; }
            }
            TestAssert.True(open is not null, $"{label}: an open direction for the knock-back");
            var landing = open!.Value;
            TestAssert.True(runtime.Buffs.AddBuff(minion, "MoveAwayCollision", BuffType.Stun, buffSeconds, enemy.NetId,
                new BuffPayload { ActionDisabled = true }, canMitigateDuration: false) is not null, $"{label}: the knock-back's buff");
            TestAssert.True(runtime.Movement.StartForcedMove(minion.NetId, landing, speed, distance / speed, enemy.NetId,
                "MoveAwayCollision", cancelOrder: true), $"{label}: the knock-back starts");
            runtime.Effects.SendDash(minion, minion.Position, landing, speed, 0.0, runtime.World.Clock.SyncId());
            runtime.Delivery.Flush();
            var log = Movement(setup.Capture, minion.NetId, 0);
            var dashEndTick = (int)Math.Ceiling(distance / speed / Step - 1e-9);
            Position2? landed = null;
            for (var tick = 1; tick <= dashEndTick + 40; tick++)
            {
                runtime.Tick(Step);
                log.AddRange(Movement(setup.Capture, minion.NetId, tick));
                if (landed is null && minion.ForcedMove is null or { Completed: true })
                {
                    // BbScriptMoves.Finish: the move's owner retires the completed move and anchors the unit where it landed.
                    landed = minion.Position;
                    minion.ForcedMove = null;
                    runtime.Effects.SendWaypoints(minion, [minion.Position], runtime.World.Clock.SyncId());
                }
            }
            // Batch 291: after the dash (the stun's anchor ahead of it is replaced by it, AfterDash).
            var early = AfterDash(log).Where(sent => sent.Opcode == VisionPackets.WaypointListOpcode && sent.Tick < dashEndTick).ToArray();
            TestAssert.Equal(0, early.Length, $"{label}: no 0xC1 inside the dash (ends at step {dashEndTick}): {Describe(log)}");
            TestAssert.True(landed is { } end && Distance(end, landing) <= 3.0,
                $"{label}: the server's minion lands where the dash does ({landed})");
            TestAssert.True(log.Any(sent => sent.Opcode == VisionPackets.WaypointListOpcode && sent.Points.Length > 1 &&
                    sent.Tick >= dashEndTick), $"{label}: the AI walks the minion again once the dash is over: {Describe(log)}");
        }
    }
}
