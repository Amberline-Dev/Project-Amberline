using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-pounce-midcast-keeps-dash")]
    public static void PounceMidcastKeepsDash()
    {
        foreach (var slot in new[] { 0, 2 })
        {
            using var match = new Match("Nidalee", Team.Order);
            var hero = match.Hero(0);
            match.Cast(0, 3, hero);
            hero.SpellCooldowns[1] = 0;
            // Pounce postpones this attack order (POSTPONE_CURRENT_ORDER). It has to be a real explicit attack order (pursuit state included):
            // the script move captures what the order left, not a bare AttackTarget.
            match.Attack(0, match.Hero(1));
            TestAssert.True(hero.HeroPursuit is not null, "an explicit attack order is in place");
            match.Cast(0, 1, hero);
            var pounce = hero.ForcedMove ?? throw new InvalidOperationException("Pounce did not start");
            match.Clear();
            match.Cast(0, slot, hero);
            TestAssert.True(ReferenceEquals(pounce, hero.ForcedMove) && !pounce.Completed,
                "cougar cast preserves the active Pounce");
            // Playtest B3 #10 (26 Sept 2026): the cast waits for the landing.
            TestAssert.True(hero.PendingCast is not null, "cougar cast queues behind Pounce");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == VisionPackets.WaypointListOpcode),
                "cougar cast cannot send a stationary waypoint over Pounce");
            var before = hero.Position;
            match.Runtime.Movement.Tick(.05);
            TestAssert.True(hero.Position != before, "Pounce continues after cougar cast");
            // 19 October 2026: Pounce is the script's BBMove; its landing and the queued cast share a tick. The queued cast must never fire while the
            // dash is still moving her (playtest B3 #10), and fires in the landing tick once Pounce is retired; the order the dash postponed
            // is not handed back when a cast was queued behind the dash.
            var firedMidDash = false;
            for (var tick = 0; hero.ForcedMove is not null && tick < 30; tick++)
            {
                match.Runtime.Tick(1.0 / 30.0);
                if (hero.ForcedMove is not null && hero.PendingCast is null) firedMidDash = true;
            }
            TestAssert.False(firedMidDash, "the queued cast waits for the landing");
            TestAssert.True(hero.ForcedMove is null, "the landing retires Pounce");
            TestAssert.True(hero.PendingCast is null, "the queued cougar cast fires in the landing tick, after Pounce");
            match.Runtime.Tick(1.0 / 30.0);
            // A cast queued during the displacement is the newer order (PostponedHeroOrders.HasNewerOrder, playtest B3 #10), whichever spell it is: the
            // postponed attack is not handed back. The hand-written Nidalee resumed it for Takedown, which keeps orders; the generic rule has no such case.
            TestAssert.Equal<uint?>(null, hero.AttackTarget, "a queued cast supersedes the postponed attack");
        }

        using var completed = new Match("Nidalee", Team.Order);
        var nidalee = completed.Hero(0);
        completed.Cast(0, 3, nidalee);
        var oldMove = new ForcedMoveState(nidalee.Position, 900, .5, nidalee.NetId, "completed");
        oldMove.Completed = true;
        nidalee.ForcedMove = oldMove;
        completed.Clear();
        // Swipe, a windup spell that takes the hero's order: the instant Takedown sends no anchor at all since it is the script's (it keeps orders).
        completed.Cast(0, 2, nidalee);
        TestAssert.True(completed.Captures[0].Packets.Any(packet => Opcode(packet) == VisionPackets.WaypointListOpcode),
            "completed displacement permits the ordinary cast anchor");
    }

    [Check("spells-live-janna-r-turret-body-clearance")]
    public static void JannaRTurretBodyClearance()
    {
        foreach (var minion in new[] { false, true })
        {
            using var match = new Match("Janna", Team.Order);
            var janna = match.Hero(0);
            var victim = minion
                ? match.Runtime.Waves!.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "C", 0, 0)
                : match.Hero(1);
            var turret = match.Runtime.World.Entities.Values.First(entity => entity.UnitType == UnitType.Turret);
            janna.Position = new Position2(6800, 7000);
            victim.Position = new Position2(7000, 7000);
            if (minion) match.Hero(1).Position = new Position2(9000, 9000);
            turret.Position = new Position2(7400, 7000);
            match.Runtime.Delivery.Flush();
            match.Clear();
            match.Cast(0, 3, janna);
            var move = AwaitForcedMove(match, victim, "MoveAway") ?? throw new InvalidOperationException("Monsoon did not push");
            var clearance = match.Runtime.Movement.BodyClearance(victim, turret) + 4.0;
            // DEV-01, 27 Sept 2026 base-game sweep: owner's 28 Sept retail
            // decision permits crossing the turret, but keeps its landing clear.
            TestAssert.True(move.Destination.X > turret.Position.X &&
                Math.Sqrt((move.Destination.X - turret.Position.X) * (move.Destination.X - turret.Position.X) +
                    (move.Destination.Z - turret.Position.Z) * (move.Destination.Z - turret.Position.Z)) >= clearance,
                "Monsoon crosses the turret body and lands clear beyond it");
            match.Runtime.Movement.Tick(1.0);
            TestAssert.True(match.Runtime.Navigation.IsMovementWalkable(victim.Position) &&
                (minion || match.Runtime.Navigation.IsHeroPathingWalkable(victim.Position)),
                "Monsoon lands on a cell that can start the next route");
            if (!minion)
            {
                victim.HeroAutoAcquire = false;
                match.Runtime.Tick(.8); // retire the completed move and .75s MoveAway buff
                TestAssert.True(victim.ForcedMove is null,
                    "Monsoon must release the victim for ordinary routing");
                TestAssert.False(victim.Buffs.Any(buff => buff.Name == "MoveAway"),
                    "the route probe must begin after Monsoon control expires");
                var landing = victim.Position;
                var escape = new Position2(landing.X - 120.0, landing.Z);
                var route = match.Runtime.Movement.SetMoveOrder(victim.NetId, escape);
                TestAssert.True(route.Count > 1,
                    "the victim must be able to route away from the turret footprint");
                match.Runtime.Tick(.5);
                TestAssert.True(victim.Position != landing,
                    "the routed victim must move away after Monsoon");
            }
        }
    }

    [Check("spells-live-ezreal-e-wall-matches-flash")]
    public static void EzrealArcaneShiftWallMatchesFlash()
    {
        // Playtest B3 #9, 26 Sept 2026: this Arcane Shift backed off toward
        // Ezreal to (4581, 2159) while Flash crossed to (4219, 2161). Both
        // shipped scripts call BBTeleportToPosition at the 475-clamped point,
        // so they now share one body-fitting resolver.
        var origin = new Position2(4744, 2155);
        var aim = new Position2(4269, 2161);
        using var match = new Match("Ezreal", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        hero.SummonerSpells = ["SummonerFlash", "SummonerDot"];
        hero.Position = origin;
        runtime.Delivery.Flush(); match.Clear();
        match.CastGround(0, aim, 2);
        SpellStep(match, .5);
        var shift = hero.Position;
        TestAssert.True(shift.X < 4221,
            $"Arcane Shift lands past the wall like Flash: {shift}");
        TestAssert.True(runtime.Navigation.FitsSummonPlacement(shift,
            runtime.Movement.MovementRadius(hero)), "Arcane Shift keeps the whole body out of terrain");

        hero.Position = origin;
        var flash = new byte[34];
        flash[0] = 0xA2;
        flash[5] = 1; // summoner slot 0
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(flash.AsSpan(offset), (float)aim.X);
            BinaryPrimitives.WriteSingleLittleEndian(flash.AsSpan(offset + 8), (float)aim.Z);
        }
        TestAssert.True(runtime.Spells.HandleCastRequest(hero, flash, runtime.World.Clock.Now()),
            "Flash from the same point is accepted");
        TestAssert.Equal(hero.Position, shift, "Arcane Shift and Flash share one landing");
    }

    [Check("spells-live-forced-move-fog-reentry-dash")]
    public static void ForcedMoveFogReentryDash()
    {
        using var match = new Match("LeeSin", Team.Order);
        var observer = match.Runtime.Session.Participants[0].View;
        var actor = match.Hero(1);
        actor.Position = new Position2(7200, 7000);
        var destination = ChampionSpellSupport.ClientPoint(match.Runtime.Spells, new Position2(7600, 7000));
        var move = new ForcedMoveState(destination, 1000, 1.0, match.Hero(0).NetId, "kick");
        actor.ForcedMove = move;
        observer.Visible.Remove(actor.NetId);
        match.Clear();
        match.Runtime.Effects.SendDash(actor, actor.Position, destination, 1000, 5,
            match.Runtime.World.Clock.SyncId());
        match.Runtime.Delivery.Flush();
        // Batch 276: the reveal's 0xC2 carries the remaining dash (movement data type 1, a 0x67 entry); no 0x67 follows it.
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == VisionPackets.DashOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == actor.NetId)),
            "reentry replaces the queued original-start dash");
        var reentry = EnterOf(match.Captures[0].Packets, actor.NetId);
        TestAssert.Equal(VisionPackets.MovementDataRouteWithSpeed, reentry[7], "the reveal carries the remaining dash");
        var reentryDash = DecodeRevealEntry(reentry);
        var points = reentryDash.GetProperty("waypoints");
        var end = points[points.GetArrayLength() - 1];
        TestAssert.Equal(destination, new Position2(end[0].GetDouble(), end[1].GetDouble()),
            "reentry dash retains the authoritative endpoint");
        TestAssert.Near(5.0, reentryDash.GetProperty("gravity").GetDouble(), 1e-6,
            "reentry kick retains its native dash gravity");
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == VisionPackets.WaypointListOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == actor.NetId)),
            "reentry cannot park a still-displaced actor");

        move.Completed = true;
        observer.Visible.Remove(actor.NetId);
        match.Clear();
        match.Runtime.Delivery.Flush();
        TestAssert.True(EnterOf(match.Captures[0].Packets, actor.NetId)[7] is VisionPackets.MovementDataRoute or
                VisionPackets.MovementDataStop,
            "completed forced move resumes the ordinary reveal movement (a route or a stop)");

        var qTarget = match.Hero(0);
        var qMove = new ForcedMoveState(destination, 1600, 1.0, actor.NetId, "Q2",
            followNetId: qTarget.NetId, maxTrackDistance: 2000);
        actor.ForcedMove = qMove;
        observer.Visible.Remove(actor.NetId);
        match.Clear();
        match.Runtime.Effects.SendDash(actor, actor.Position, destination, 1600, 0,
            match.Runtime.World.Clock.SyncId(), followNetId: qTarget.NetId,
            followDistance: 2000, followBackDistance: 75);
        match.Runtime.Delivery.Flush();
        var follow = DecodeRevealEntry(EnterOf(match.Captures[0].Packets, actor.NetId));
        TestAssert.Equal(qTarget.NetId, follow.GetProperty("follow_netid").GetUInt32(),
            "Q2 reentry lost its visible target follow identity");
        TestAssert.Near(2000, follow.GetProperty("follow_distance").GetDouble(), 1e-6,
            "Q2 reentry lost its native tracking range");
        TestAssert.Near(75, follow.GetProperty("back_distance").GetDouble(), 1e-6,
            "Q2 reentry lost its body-shell arrival distance");

        // playtest B2 BJ3, 26 Sept 2026: the client keeps drawing a dash it
        // saw start after the actor leaves sight. The next reveal teleports
        // the actor to its authoritative landing before the ordinary update.
        var watcher = match.Hero(0);
        var watchPoint = watcher.Position;
        actor.Position = new Position2(7200, 7000);
        var pull = new ForcedMoveState(destination, 1000, 1.0, watcher.NetId, "RocketGrab2");
        actor.ForcedMove = pull;
        match.Runtime.Effects.SendDash(actor, actor.Position, destination, 1000, 5,
            match.Runtime.World.Clock.SyncId());
        match.Runtime.Delivery.Flush();
        TestAssert.True(observer.Visible.Contains(actor.NetId) && pull.DashPresentation,
            "the pull starts in sight");
        watcher.Position = new Position2(1000, 14000);
        match.Runtime.Delivery.Flush();
        TestAssert.False(observer.Visible.Contains(actor.NetId), "the actor leaves sight mid-dash");
        actor.Position = destination;
        actor.ForcedMove = null;
        watcher.Position = watchPoint;
        match.Clear();
        match.Runtime.Delivery.Flush();
        // Batch 291: the teleport is the reveal's own entry (movement data type 2 with the teleport flag, HandleWaypoint), not a 0x64
        // ahead of it.
        var reveal = match.Captures[0].Packets.Where(packet =>
            packet.EntityReferences.Any(reference => reference.NetId == actor.NetId)).ToList();
        TestAssert.False(reveal.Any(packet => Opcode(packet) == VisionPackets.TeleportOpcode), "no 0x64 ahead of the reveal");
        var enter = EnterOf(reveal, actor.NetId);
        TestAssert.Equal(VisionPackets.MovementDataRoute, enter[7], "the reveal of a landed actor after a stale dash is a route entry");
        var landing = DecodeRevealEntry(enter);
        TestAssert.Equal(actor.TeleportId, landing.GetProperty("teleport_id").GetByte(), "the entry carries a fresh teleport id");
        var waypoints = landing.GetProperty("waypoints");
        TestAssert.Equal(1, waypoints.GetArrayLength(), "the teleport is one point");
        TestAssert.Equal(destination, new Position2(waypoints[0][0].GetDouble(), waypoints[0][1].GetDouble()),
            "the teleport lands at the authoritative endpoint");

        var teleportId = actor.TeleportId;
        watcher.Position = new Position2(1000, 14000);
        match.Runtime.Delivery.Flush();
        watcher.Position = watchPoint;
        match.Clear();
        match.Runtime.Delivery.Flush();
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == VisionPackets.TeleportOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == actor.NetId)),
            "a leave without a live dash reveals without a teleport");
        TestAssert.Equal(VisionPackets.MovementDataStop, EnterOf(match.Captures[0].Packets, actor.NetId)[7],
            "and its reveal is the ordinary stop");
        TestAssert.Equal(teleportId, actor.TeleportId, "no teleport id is spent");
    }

    [Check("spells-live-fear-fog-reentry-walk")]
    public static void FearFogReentryRetainsWaypointWalk()
    {
        using var match = new Match("Shaco", Team.Order);
        var observer = match.Runtime.Session.Participants[0].View;
        var actor = match.Hero(1);
        actor.Position = new Position2(7200, 7000);
        var start = actor.Position;
        var destination = ChampionSpellSupport.ClientPoint(match.Runtime.Spells,
            new Position2(7400, 7000));
        actor.ForcedMove = new ForcedMoveState(destination, 325, 1.0,
            match.Hero(0).NetId, "Fear", firstCollision: true);
        observer.Visible.Remove(actor.NetId);
        match.Clear();
        match.Runtime.Effects.SendWaypoints(actor, [start, destination],
            match.Runtime.World.Clock.SyncId());
        match.Runtime.Delivery.Flush();
        // Batch 276: the reveal's 0xC2 carries the walk (movement data type 2, a 0x64 entry); no 0xC1 or 0x67 follows it.
        TestAssert.False(match.Captures[0].Packets.Any(packet =>
            packet.EntityReferences.Any(reference => reference.NetId == actor.NetId) &&
            Opcode(packet) is VisionPackets.DashOpcode or VisionPackets.WaypointListOpcode),
            "fear reveal must replace the queued original-start movement once");
        var walk = EnterOf(match.Captures[0].Packets, actor.NetId);
        TestAssert.Equal(VisionPackets.MovementDataRoute, walk[7],
            "fear uses waypoint walking, not dash presentation");
        var points = DecodeRevealEntry(walk).GetProperty("waypoints");
        var end = points[points.GetArrayLength() - 1];
        TestAssert.Equal(destination, new Position2(end[0].GetDouble(), end[1].GetDouble()),
            "fear reentry retains its remaining destination");
    }
}
