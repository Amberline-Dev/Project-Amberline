using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("movement-b6-follow-dash-wall-landing")]
    public static void FollowingDashPublishesLegalTerminalBeforeTheNextOrder()
    {
        // PATH-01: shipped Map1 floor at (5696.10,4014.81), blocked 130u
        // arrival shell at (5827,4015). Q1 is established on open ground first.
        foreach (var cadence in new[] { .05, .1 })
        foreach (var targetDies in new[] { false, true })
        {
            using var match = new Match("LeeSin", Team.Order, ally: true);
            var lee = match.Hero(0); var target = match.Hero(1);
            match.Cast(0, 0, target); SpellStep(match, .25); SpellStep(match, .5);
            lee.Position = new Position2(6296.10, 4014.81);
            target.Position = new Position2(5696.10, 4014.81);
            lee.Radius = target.Radius = 55; // 55 + 55 + shipped high skin 20 = 130.
            var movement = match.Runtime.Movement;
            TestAssert.True(movement.Navigation.IsMovementWalkable(target.Position));
            TestAssert.False(movement.Navigation.IsMovementWalkable(new Position2(5827, 4015)));
            if (targetDies)
            {
                // Extend the shipped kick-wall ray so Q2's 130u arrival shell
                // lies beyond its narrow blocked cell at (8128.2,7338.15).
                lee.Position = new Position2(7395, 7071);
                target.Position = new Position2(8335, 7413.5);
                TestAssert.True(movement.Navigation.IsMovementWalkable(target.Position));
                TestAssert.False(movement.Navigation.IsMovementWalkable(new Position2(8128.2, 7338.15)));
            }
            // Keep the target visible after Q2 removes Q1's reveal. Without
            // allied sight, the wall correctly causes delivery to replace the
            // full following endpoint with a short fog-safe leg (LEE-04).
            match.Hero(2).Position = target.Position;
            var origin = lee.Position;
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 0, target);
            var move = lee.ForcedMove ?? throw new InvalidOperationException("Q2 did not begin");
            TestAssert.Equal(target.NetId, move.FollowNetId!.Value, "BBMoveToUnit keeps tracking");
            TestAssert.False(move.FirstCollision, "ghosted Q2 can still cross walls");
            TestAssert.Near(2000, move.MaxTrackDistance, 0, "shipped tracking limit");
            TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(target.NetId),
                "allied sight preserves the full following-dash presentation across the wall");
            TestAssert.Equal(movement.ClientAnchor(move.Destination),
                StaticBodyDashEnd(match.Captures[0].Packets), "initial dash publishes the resolved endpoint");
            match.Clear();
            if (targetDies)
            {
                // Advance the actual attached dash into terrain before the production
                // death hook retires it; setting Completed directly misses the bug.
                for (var tick = 0; tick < 1000 && !move.Completed &&
                    movement.Navigation.IsMovementWalkable(lee.Position); tick++) movement.Tick(.001);
                TestAssert.False(move.Completed, "Q2 is still traversing the wall");
                TestAssert.False(movement.Navigation.IsMovementWalkable(lee.Position));
                match.Runtime.Combat.Kill(target, lee.NetId);
                // 19 October 2026: Q2 is the script's BlindMonkQTwoDash move; no death hook retires it, the movement system ends a follow whose
                // target is gone on its next step.
                movement.Tick(cadence);
                TestAssert.True(move.Completed && !move.Succeeded, "target death retires Q2 at the next movement step");
                SpellStep(match, 0);
                TestAssert.True(lee.ForcedMove is null, "Q2 cleanup detaches the retired move");
            }
            else
            {
                for (var tick = 0; tick < 60 && !move.Completed; tick++) movement.Tick(cadence);
                TestAssert.True(move.Completed && move.Succeeded, "following move completes without a later order");
            }
            TestAssert.True(movement.Navigation.IsMovementWalkable(lee.Position));
            TestAssert.True(movement.Navigation.FitsSummonPlacement(lee.Position, movement.MovementRadius(lee)),
                "corrected terminal fits the body and the existing four-unit margin");
            TestAssert.Equal(move.Destination, lee.Position);
            SpellStep(match, 0); match.Runtime.Delivery.Flush();
            // The terminal is the stationary 0xC1 (one waypoint) at the landing. After a landing that did not end with the target's death the
            // script goes on to order Lee at the target (BlindMonkQTwoDash.BuffOnMoveSuccess: BBIssueOrder AI_ATTACKTO, batch 105 row 9), so the
            // last 0xC1 of the stream can be that pursuit route: the stop is the last single-waypoint one, and it comes before the route.
            var stops = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0xC1 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == lee.NetId &&
                Decode(packet).GetProperty("waypoints").GetArrayLength() == 1).ToList();
            TestAssert.True(stops.Count > 0, "the landing published a stationary terminal");
            var stop = stops[^1];
            var points = Decode(stop).GetProperty("waypoints");
            TestAssert.Equal(1, points.GetArrayLength());
            TestAssert.Equal(movement.ClientAnchor(lee.Position),
                new Position2(points[0][0].GetDouble(), points[0][1].GetDouble()),
                "terminal C1 agrees with authoritative landing");
            var landed = lee.Position;
            match.Clear(); match.Move(0, origin);
            TestAssert.Equal(landed, lee.Position, "next order does not emergency-teleport out of terrain");
            TestAssert.True(lee.Waypoints.Count > 1, "next order obtains an ordinary route");
        }
    }

    [Check("spells-leesin-b5-dash-scoped-ghost")]
    public static void LeeDashesOwnGhostPayload()
    {
        foreach (var q in new[] { false, true })
        foreach (var ending in new[] { "complete", "replace", "interrupt", "death" })
        {
            using var match = new Match("LeeSin", Team.Order, ally: true);
            var lee = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
            var origin = lee.Position;
            enemy.Position = origin + new Position2(600, 0);
            ally.Position = origin + new Position2(0, -200);
            var goal = origin + new Position2(0, 200);
            match.Move(2, goal);
            TestAssert.True(ally.Waypoints.Any(point => Math.Abs(point.X - origin.X) > 5),
                "ordinary Lee body diverts the ally's route");
            if (q)
            {
                match.Cast(0, 0, enemy); SpellStep(match, .25); SpellStep(match, .5);
                match.Cast(0, 0, enemy);
            }
            else match.Cast(0, 1, ally);
            // 19 October 2026: the ghosting is the script's own: BlindMonkQTwoDash and BlindMonkWOneDash hold SetGhosted while they run.
            var dashMove = lee.ForcedMove ?? throw new InvalidOperationException("the dash did not start");
            var ghost = lee.Buffs.FirstOrDefault(buff => buff.Name == (q ? "BlindMonkQTwoDash" : "BlindMonkWOneDash"))
                ?? throw new InvalidOperationException("dash has no scoped ghost buff");
            TestAssert.True(ghost.Payload.Ghosted && lee.Buffs.Contains(ghost));
            TestAssert.Equal(1 << 10, match.Runtime.HeroStats.Snapshot(lee).ActionState & (1 << 10),
                "LEE-03: native action state publishes ghosted");
            match.Move(2, goal);
            TestAssert.True(ally.Waypoints.All(point => Math.Abs(point.X - origin.X) <= 5),
                "another hero routes through Lee while his dash owns ghosting");
            var otherGhost = ending == "replace"
                ? match.Runtime.SpellBuffs.Add(lee, "OtherGhost", BuffType.Internal, 10, lee.NetId,
                    new BuffPayload { Ghosted = true }, canMitigateDuration: false)
                : null;
            if (ending == "complete") match.Runtime.Movement.Tick(1);
            else if (ending == "replace")
                match.Runtime.Movement.StartForcedMove(lee.NetId,
                    new ForcedMoveState(origin + new Position2(0, 300), 100, 3, enemy.NetId, "Replacement"));
            else if (ending == "interrupt") dashMove.Completed = true;
            else match.Runtime.Combat.Kill(lee, enemy.NetId);
            // A buff step as well: the dash buff (BlindMonkQTwoDash, BlindMonkWOneDash) leaves at its next update after the move ended, a quarter of a
            // second at most (19 October 2026: the script's own cadence, W32 record row 7 and 11).
            for (var step = 0; step < 10; step++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.False(lee.Buffs.Contains(ghost), ending + " removes only the owned ghost buff");
            if (otherGhost is not null)
            {
                TestAssert.True(lee.Buffs.Contains(otherGhost), "dash cleanup preserves independent ghosting");
                TestAssert.Equal(1 << 10, match.Runtime.HeroStats.Snapshot(lee).ActionState & (1 << 10));
                match.Runtime.SpellBuffs.Remove(lee, otherGhost.Slot);
            }
            TestAssert.Equal(0, match.Runtime.HeroStats.Snapshot(lee).ActionState & (1 << 10));
            if (ending == "complete")
            {
                lee.Position = origin; ally.Position = origin + new Position2(0, -200);
                match.Move(2, goal);
                TestAssert.True(ally.Waypoints.Any(point => Math.Abs(point.X - origin.X) > 5),
                    "completed dash restores Lee as a body obstacle");
            }
        }
    }

    [Check("spells-live-leesin-dash-body-landing")]
    public static void LeeSinDashBodyLanding()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var distance in new[] { 1.0, 600.0 })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = lee.Position + new Position2(distance, 0.0);
            match.Cast(0, 0, enemy);
            runtime.Tick(1.0);
            match.Clear();
            match.Cast(0, 0, enemy);

            var move = lee.ForcedMove ?? throw new InvalidOperationException("Q2 did not start");
            var arrival = lee.Radius + enemy.Radius + runtime.Movement.Settings.Collision.SkinHighCollider;
            foreach (var capture in match.Captures)
            {
                var dash = capture.Packets.Single(packet => Opcode(packet) == 0x67 && packet.Channel == 4);
                var fields = Decode(dash);
                TestAssert.Near(arrival, fields.GetProperty("back_distance").GetDouble(), .01,
                    "Q2 wire back distance matches the server arrival shell");
            }
            if (distance <= arrival)
                TestAssert.Equal(lee.Position, move.Destination,
                    "point-blank Q2 does not invent a backwards landing point");
            else
            {
                TestAssert.True(Distance(move.Destination, enemy.Position) < distance - 1.0,
                    "Q2 lands before the target center");
                enemy.Position += new Position2(75.0, 40.0);
                runtime.Tick(.1);
                TestAssert.Near(arrival, Distance(move.Destination, enemy.Position), 25.0,
                    "Q2 follows a moving target at the same shell distance");
            }
        }

        using var safeguard = new Match("LeeSin", Team.Order, ally: true);
        var safeguardRuntime = safeguard.Runtime;
        var caster = safeguard.Hero(0);
        var ally = safeguard.Hero(2);
        ally.Position = caster.Position + new Position2(600.0, 0.0);
        safeguard.Cast(0, 1, ally);
        var wMove = caster.ForcedMove ?? throw new InvalidOperationException("W1 did not start");
        // W uses the saved script distance, not Q2's inferred arrival shell: BlindMonkWOneDash moves toward the ally's saved position with
        // IdealDistance = BBDistanceBetweenObjects(Lee, ally), the gap between the two bodies (edge to edge since batch 112), so the dash ends in contact.
        var toAlly = Distance(caster.Position, ally.Position);
        var gap = Math.Max(0.0, toAlly - caster.CombatRadius - ally.CombatRadius);
        var requestedLanding = new Position2(caster.Position.X + (ally.Position.X - caster.Position.X) * gap / toAlly,
            caster.Position.Z + (ally.Position.Z - caster.Position.Z) * gap / toAlly);
        var wLanding = safeguardRuntime.Movement.FurthestWalkableLanding(caster.Position, requestedLanding, caster);
        TestAssert.Equal(wLanding, wMove.Destination, "Safeguard resolves the saved ally position");
        safeguardRuntime.Tick(.5);
        TestAssert.True(caster.ForcedMove is null, "Safeguard completes at the script landing");
        TestAssert.Equal(wLanding, caster.Position, "Safeguard reaches the announced endpoint");
        safeguard.Clear();
        var away = caster.Position + new Position2(0.0, 300.0);
        safeguard.Move(0, away);
        TestAssert.True(caster.Waypoints.Count > 0,
            "a move away from the landing ally remains routable");
    }

    [Check("spells-live-ezreal-qw-stop-animation-on-move")]
    public static void EzrealMoveStopsQAndWAnimation()
    {
        foreach (var slot in new[] { 0, 1 })
        foreach (var afterLaunch in new[] { false, true })
        foreach (var order in new byte[] { 2, 7, 3 })
        {
            using var match = new Match("Ezreal", Team.Order);
            var runtime = match.Runtime;
            var ezreal = match.Hero(0);
            match.Cast(0, slot, match.Hero(1));
            TestAssert.True(EzrealFlights.WindingUp(runtime.Spells, ezreal, slot), "Q/W keeps its server cast timer (the child's line)");
            // The hand-written Ezreal advertised an action mask of 0 in the cast-start stats (playtest #7, 25 Sept 2026); the script engine leaves
            // the mask alone like every script champion's cast, and the windup gate (SpellWindup) holds the orders instead.
            if (afterLaunch) runtime.Tick(.26);
            match.Clear();
            var destination = ezreal.Position + new Position2(200.0, 0.0);
            if (order == 2) match.Move(0, destination);
            else SendOrder(match, order, destination, order == 3 ? match.Hero(1).NetId : 0);
            if (!afterLaunch)
            {
                // Playtest #7, 25 Sept 2026: this reverses the old "before
                // launch" expectation. The stock client refuses Stop during
                // the windup; a 0x2B or stationary C1 here cut the gesture and
                // launched the missile from an idle Ezreal. The order is held through the windup (SpellWindup) and applied 0.1 s
                // after it, so nothing reaches the client inside the windup.
                TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x2B),
                    "an order during the uncancellable windup keeps the Q/W clip");
                TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1 &&
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            packet.Plaintext.Span[1..]) == ezreal.NetId),
                    "an order during the windup sends no stationary Ezreal waypoint");
                TestAssert.True(EzrealFlights.WindingUp(runtime.Spells, ezreal, slot),
                    "the held order does not cancel the server launch");
                runtime.Tick(.26);
            }
            else
            {
                TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x2B),
                    "a movement order after launch emits native stop-animation for the Q/W clip");
                if (order != 3)
                    TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
                        "the same movement order emits the authoritative stop waypoint");
            }
            TestAssert.False(EzrealFlights.WindingUp(runtime.Spells, ezreal, slot), "Q/W launches after its normal cast delay");
        }
    }

    [Check("spells-live-cast-gesture-holds-idle-attack")]
    public static void CastGestureHoldsIdleAttack()
    {
        // Playtest B2 AE6 (owner rule, 26 Sept 2026): after a cast that
        // replaces the order, idle acquisition waits max(cast time, designer
        // total time) so its 0x0D cannot cut the Spell clip; any player order
        // acts at once.
        foreach (var (champion, spellName) in new[] { ("Ezreal", "EzrealMysticShot"), ("Annie", "Disintegrate") })
        foreach (var orderStep in new[] { 0, 9 })
        {
            using var match = new Match(champion, Team.Order, autoAcquire: true);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var enemy = match.Hero(1);
            var spell = GameData.Default.Spell(spellName);
            var hold = Math.Max(spell.CastTime, GameData.Default.DesignerTotalTime(spell));
            TestAssert.Near(1.0, hold, 1e-9, $"{spellName}: shipped designer total time");
            TestAssert.True(runtime.Combat.CanAttack(caster, enemy), "the enemy starts inside attack range");
            match.Cast(0, 0, enemy);
            match.Clear();
            var firstSwing = 0;
            for (var step = 1; step <= 45 && firstSwing == 0; step++)
            {
                if (orderStep > 0 && step == orderStep + 1) match.Attack(0, enemy);
                runtime.Tick(1.0 / 30.0);
                if (match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x0D &&
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            packet.Plaintext.Span[1..]) == caster.NetId))
                    firstSwing = step;
            }
            if (orderStep == 0)
            {
                TestAssert.True(firstSwing >= 30,
                    $"{champion}: no idle 0x0D before {hold:0.0} s (first at step {firstSwing})");
                TestAssert.True(firstSwing > 0, $"{champion}: idle acquisition resumes after the hold");
            }
            else
                TestAssert.Equal(orderStep + 1, firstSwing,
                    $"{champion}: an ATTACK_TO at 0.3 s swings at once");
        }

        // The hold covers automatic acquisition only. A holding cast drops an
        // earlier ATTACK_TO and a cast that keeps the order sets no hold, so
        // the explicit target seen under a hold is one a spell hands back
        // (BlindMonkQTwo's landing sets AttackTarget). Its out-of-range chase
        // starts at the next pursuit scan, not after the 1.0 s hold.
        using var handed = new Match("Annie", Team.Order, autoAcquire: true);
        var annie = handed.Hero(0);
        var foe = handed.Hero(1);
        handed.Cast(0, 0, foe);
        // Out of attack range, with the foe held still: only a chase closes it.
        foe.HeroAutoAcquire = false;
        foe.Position = annie.Position + new Position2(900.0, 0.0);
        annie.AttackTarget = foe.NetId;
        for (var step = 0; step < 18; step++) handed.Runtime.Tick(1.0 / 30.0);
        TestAssert.True(Distance(annie.Position, foe.Position) < 860.0,
            "a handed-back explicit target is chased during the idle-attack hold");
    }

    [Check("spells-live-janna-q-release-launch-anchor")]
    public static void JannaQReleaseUsesTestCubeLaunchAnchor()
    {
        using var match = new Match("Janna", Team.Order);
        var runtime = match.Runtime;
        var janna = match.Hero(0);
        var enemy = match.Hero(1);
        var chargeOrigin = janna.Position;
        match.Cast(0, 0, enemy);
        janna.Position = chargeOrigin + new Position2(55.0, 30.0);
        match.Clear();
        match.Cast(0, 0, enemy);
        // The recast releases the wave from the TestCube (BBSpellCast FireWithoutCasting); its missile (0x41) is created when the tier's own cast
        // time has run (the hand-written release sent it with the press).
        match.Runtime.Tick(.5);
        match.Runtime.Delivery.Flush();
        var missile = match.Captures[0].Packets.Single(packet => Opcode(packet) == 0x41);
        var launch = Decode(missile).GetProperty("cast_info").GetProperty("launch_pos");
        TestAssert.Near(chargeOrigin.X, launch[0].GetDouble(), .01,
            "recast SpellCastInfo launch X belongs to the TestCube origin");
        TestAssert.Near(chargeOrigin.Z, launch[2].GetDouble(), .01,
            "recast SpellCastInfo launch Z belongs to the TestCube origin");
        TestAssert.Near(chargeOrigin.X, missile.Plaintext.Span[5..].Length > 0
            ? BinaryFloat(missile.Plaintext.Span, 5) : double.NaN, .01,
            "missile position remains the charge origin");

        using var automatic = new Match("Janna", Team.Order);
        var automaticJanna = automatic.Hero(0);
        var automaticOrigin = automaticJanna.Position;
        automatic.Cast(0, 0, automatic.Hero(1));
        automaticJanna.Position += new Position2(45.0, 20.0);
        automatic.Clear();
        automatic.Runtime.Tick(3.1);
        var automaticMissile = automatic.Captures[0].Packets.Single(packet => Opcode(packet) == 0x41);
        var automaticLaunch = Decode(automaticMissile).GetProperty("cast_info").GetProperty("launch_pos");
        TestAssert.Near(automaticOrigin.X, automaticLaunch[0].GetDouble(), .01,
            "automatic release uses the stored TestCube X");
        TestAssert.Near(automaticOrigin.Z, automaticLaunch[2].GetDouble(), .01,
            "automatic release uses the stored TestCube Z");
    }

    private static double BinaryFloat(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]));

    private static void SendOrder(Match match, byte order, Position2 destination, uint targetNetId)
    {
        var request = new byte[22];
        request[0] = 0x75; request[5] = order;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), match.Hero(0).NetId);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        if (targetNetId != 0)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(18), targetNetId);
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), match.Runtime.Dependencies.Gameplay);
        match.Runtime.Delivery.Flush();
    }
}
