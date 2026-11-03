using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-leesin-q-r-q-hidden-moving-target-dash")]
    public static void LeeQ2TracksKickedTargetWithoutStaleFogFollow()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
            CheckKickedTarget(team);
    }

    private static void CheckKickedTarget(Team team)
    {
        using var match = new Match("LeeSin", team, ally: true);
        var runtime = match.Runtime;
        var lee = match.Hero(0);
        var target = match.Hero(1);
        target.Position = new Position2(7370, 7000);
        match.Hero(2).Position = new Position2(7050, 7050);
        runtime.Delivery.Flush();

        match.CastGround(0, target.Position);
        runtime.Tick(.6);
        TestAssert.True(target.Buffs.Any(buff => buff.Name.StartsWith("BlindMonkQOne")),
            "Sonic Wave marks the target before the kick");
        match.Cast(0, 3, target);
        runtime.Tick(.27);
        TestAssert.True(target.ForcedMove is { Completed: false, Effect: "BlindMonkRKick" },
            "Dragon's Rage moves the marked target");

        // Retain the client's known actor template while fog hides the target.
        // The authoritative kick and Q2 follow continue in the world.
        foreach (var seat in new[] { 0, 2 })
        {
            var view = runtime.Session.Participants[seat].View;
            TestAssert.True(view.Known.Contains(target.NetId), "observer knew the marked target");
            view.VisibilityRefreshSuppressed = true;
            view.Visible.Remove(target.NetId);
            TestAssert.True(view.Visible.Contains(lee.NetId), "observer still sees Lee");
        }
        match.Clear();
        match.Cast(0, 0, target);
        var qMove = lee.ForcedMove ?? throw new InvalidOperationException("Q2 did not start");
        TestAssert.Equal("BlindMonkQTwoDash", qMove.Effect, "the recast owns the follow dash");
        TestAssert.False(target.Buffs.Any(buff => buff.Name.StartsWith("BlindMonkQOne")),
            "Q2 consumes the vision-granting mark");
        AssertHiddenDash(match, lee, target);

        match.Clear();
        var oldTarget = target.Position;
        var oldLee = lee.Position;
        runtime.Tick(.05);
        TestAssert.True(target.Position != oldTarget && lee.Position != oldLee,
            "the server advances both the kick and live Q2 follow");
        TestAssert.True(ReferenceEquals(qMove, lee.ForcedMove) && !qMove.Completed,
            "Q2 remains active while its target moves");
        AssertHiddenDash(match, lee, target);

        match.Clear();
        runtime.Delivery.Flush();
        foreach (var seat in new[] { 0, 2 })
            TestAssert.False(LeeDashes(match.Captures[seat].Packets, lee).Any(),
                "unchanged simulation position does not resend a dash");

        foreach (var seat in new[] { 0, 2 })
            runtime.Session.Participants[seat].View.Visible.Add(target.NetId);
        match.Clear();
        runtime.Delivery.Flush();
        foreach (var seat in new[] { 0, 2 })
        {
            var resumed = LeeDashes(match.Captures[seat].Packets, lee).Single();
            TestAssert.Equal(target.NetId, Decode(resumed).GetProperty("follow_netid").GetUInt32(),
                "native follow identity returns only when the target is visible");
        }

        var teleportId = lee.TeleportId;
        var stopAnimation = SpellPackets.StopAnimation(lee.NetId).Plaintext.ToArray();
        var animationStopped = false;
        // 19 October 2026: the dash is the script's BlindMonkQTwoDash move; it is over when Lee's forced move is released.
        for (var frame = 0; frame < 60 && lee.ForcedMove is not null; frame++)
        {
            match.Clear();
            var before = lee.Position;
            runtime.Tick(.05);
            var distance = Math.Sqrt(Math.Pow(lee.Position.X - before.X, 2) +
                Math.Pow(lee.Position.Z - before.Z, 2));
            TestAssert.True(distance <= qMove.Speed * .05 + 3,
                "authoritative Q2 advances within one bounded simulation step");
            TestAssert.Equal(teleportId, lee.TeleportId,
                "Q2 completion does not use teleport state");
            foreach (var seat in new[] { 0, 2 })
            {
                var packets = match.Captures[seat].Packets;
                TestAssert.False(packets.Any(packet => Opcode(packet) == VisionPackets.TeleportOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == lee.NetId)),
                    "Q2 completion never emits Lee teleport");
                animationStopped |= packets.Any(packet =>
                    packet.Plaintext.Span.SequenceEqual(stopAnimation));
            }
        }
        TestAssert.True(lee.ForcedMove is null && qMove.Completed,
            "Q2 reaches completion and releases forced movement");
        TestAssert.True(animationStopped, "Q2 completion releases Lee's animation lock");
    }

    [Check("spells-live-leesin-q2-both-actors-first-sight-follow-restored")]
    public static void LeeQ2BothActorsFirstSightRestoresFollow()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var target = match.Hero(1);
            var observer = runtime.Session.Participants[2].View;
            TestAssert.True(lee.NetId < target.NetId,
                "Lee is processed before his target in the first-sight fixture");
            target.Position = new Position2(7400, 7000);
            match.Hero(2).Position = new Position2(7200, 7000);
            var destination = new Position2(7280, 7000);
            var move = new ForcedMoveState(destination, 1675, 2.5, lee.NetId,
                "BlindMonkQTwoDash", followNetId: target.NetId, maxTrackDistance: 2000);
            lee.ForcedMove = move;
            runtime.Effects.SendDash(lee, lee.Position, destination, move.Speed, 0,
                runtime.World.Clock.SyncId(), followNetId: target.NetId,
                followDistance: 2000, followBackDistance: 120);
            observer.Known.Remove(lee.NetId);
            observer.Known.Remove(target.NetId);
            observer.Visible.Remove(lee.NetId);
            observer.Visible.Remove(target.NetId);
            match.Clear();
            runtime.Delivery.Flush();

            var packets = match.Captures[2].Packets;
            // Batch 276: the short leg is inside Lee's reveal (0xC2 movement data type 1); the restoring 0x67 follows the target's reveal.
            var dashes = LeeDashes(packets, lee);
            var leeReveal = packets.FindIndex(packet => Opcode(packet) == VisionPackets.EnterVisibilityOpcode &&
                packet.EntityReferences.Any(reference => reference.NetId == lee.NetId));
            TestAssert.True(leeReveal >= 0 && dashes.Length == 1 &&
                packets[leeReveal].Plaintext.Span[7] == VisionPackets.MovementDataRouteWithSpeed,
                "first sight sends a short unknown-target leg then restores follow after target creation");
            TestAssert.Equal(0u, DecodeRevealEntry(packets[leeReveal].Plaintext.ToArray()).GetProperty("follow_netid").GetUInt32(),
                "the first dash does not reference an unknown actor");
            TestAssert.Equal(target.NetId, Decode(dashes[0]).GetProperty("follow_netid").GetUInt32(),
                "visible target creation restores native Q2 follow in the same flush");
            var targetReveal = packets.FindIndex(packet => Opcode(packet) == VisionPackets.EnterVisibilityOpcode &&
                packet.EntityReferences.Any(reference => reference.NetId == target.NetId));
            TestAssert.True(targetReveal >= 0 && leeReveal < targetReveal &&
                targetReveal < packets.IndexOf(dashes[0]),
                "follow identity is sent only after the target is registered and revealed");

            match.Clear();
            runtime.Delivery.Flush();
            TestAssert.False(LeeDashes(match.Captures[2].Packets, lee).Any(),
                "a visible stationary target does not repeatedly restart Q2");
        }
    }

    private static void AssertHiddenDash(Match match, Entity lee, Entity target)
    {
        foreach (var seat in new[] { 0, 2 })
        {
            var packets = match.Captures[seat].Packets;
            var dash = LeeDashes(packets, lee).Single();
            var fields = Decode(dash);
            TestAssert.Equal(0u, fields.GetProperty("follow_netid").GetUInt32(),
                "a known but hidden target cannot drive native Q2 follow");
            var points = fields.GetProperty("waypoints");
            var first = points[0];
            var last = points[points.GetArrayLength() - 1];
            var start = new Position2(first[0].GetDouble(), first[1].GetDouble());
            var end = new Position2(last[0].GetDouble(), last[1].GetDouble());
            TestAssert.Near(lee.Position.X, start.X, 3, "dash starts at authoritative Lee position");
            TestAssert.Near(lee.Position.Z, start.Z, 3, "dash starts at authoritative Lee position");
            var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Z - start.Z, 2));
            TestAssert.True(length <= lee.ForcedMove!.Speed * .15 + 4,
                "hidden-target packet projects only Lee's short visible leg");
            TestAssert.False(packets.Any(packet => Opcode(packet) == VisionPackets.EnterVisibilityOpcode &&
                packet.EntityReferences.Any(reference => reference.NetId == target.NetId)),
                "Q2 follow update does not reveal the hidden target");
        }
    }

    private static PlaintextPacket[] LeeDashes(
        IReadOnlyList<PlaintextPacket> packets, Entity lee) => packets.Where(packet =>
        Opcode(packet) == VisionPackets.DashOpcode &&
        packet.EntityReferences.Any(reference => reference.NetId == lee.NetId)).ToArray();
}
