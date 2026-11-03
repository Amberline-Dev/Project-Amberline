using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("protocol.champion-emotes")]
    public static void ChampionEmoteWireContract()
    {
        var packet = EmotePackets.Play(0x40000001, ChampionEmote.Dance);
        TestAssert.Equal("480100004000000000", Convert.ToHexString(packet.Plaintext.Span));
        TestAssert.Equal((byte)3, packet.Channel);
        TestAssert.True(packet.Encrypt);
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
        TestAssert.Equal(new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, 0x40000001),
            packet.EntityReferences.Single());
        TestAssert.Equal(0u, (uint)ChampionEmote.Dance);
        TestAssert.Equal(1u, (uint)ChampionEmote.Taunt);
        TestAssert.Equal(2u, (uint)ChampionEmote.Laugh);
        TestAssert.Equal(3u, (uint)ChampionEmote.Joke);
        TestAssert.Throws<ArgumentOutOfRangeException>(() => EmotePackets.Play(0, ChampionEmote.Dance));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => EmotePackets.Play(1, (ChampionEmote)4));
    }

    [Check("session-live-champion-emotes")]
    public static void ChampionEmotes()
    {
        foreach (var champion in new[] { "LeeSin", "Annie" })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match(champion, team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            bool Send(uint emote, uint? actor = null, byte channel = 1, int length = 9, bool badPadding = false)
            {
                match.Clear();
                var request = new byte[length]; request[0] = EmotePackets.ClientOpcode;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), actor ?? hero.NetId);
                if (length >= 9) BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(5), emote);
                if (badPadding) request[^1] = 1;
                var accepted = false;
                match.Runtime.Session.Receive(1, channel, match.Runtime.Cipher.Encrypt(request), context =>
                    accepted = match.Runtime.Dependencies.Gameplay(context));
                match.Runtime.Delivery.Flush();
                return accepted;
            }
            void NoEmote() => TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                .Any(packet => Opcode(packet) == EmotePackets.ServerOpcode));

            foreach (var emote in new[] { 0u, 1u, 2u, 3u })
            {
                var movingFrom = hero.Position;
                match.Move(0, movingFrom + new Position2(0, 300));
                match.Runtime.Movement.Tick(0.1);
                TestAssert.True(hero.Position.Z > movingFrom.Z,
                    "the champion was moving when the emote arrived");
                hero.AttackTarget = enemy.NetId;
                TestAssert.True(Send(emote), "native emote request is accepted");
                TestAssert.True(hero.Target is null && hero.AttackTarget is null && hero.Waypoints.Count == 0,
                    "emote holds the authoritative champion at its position");
                var stopPosition = hero.Position;
                bool HeroPacket(PlaintextPacket packet) =>
                    packet.EntityReferences.Any(reference => reference.NetId == hero.NetId);
                foreach (var capture in match.Captures)
                {
                    // Batch 437 (owner, 28 October 2026: "Emoting when moving only stops the character"): the walking hero's stop
                    // leaves in the request's flush and the 0x48 waits one simulation step, so the stop lands before the emote
                    // (Reconstruction; batch 420 sent both in one burst, and the stop cancelled the emote).
                    var packets = capture.Packets.ToArray();
                    TestAssert.True(packets.Any(packet => Opcode(packet) == 0xC1 && HeroPacket(packet)),
                        "the walking hero's stop route leaves in the request's own flush");
                    TestAssert.False(packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode),
                        "the 0x48 waits for the next step after the stop");
                }
                match.Runtime.Tick(1.0 / 30.0);
                foreach (var capture in match.Captures)
                {
                    var packets = capture.Packets.ToArray();
                    var reply = packets.Single(packet => Opcode(packet) == EmotePackets.ServerOpcode);
                    TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(reply.Plaintext.Span[1..]));
                    TestAssert.Equal(emote, Decode(reply).GetProperty("emote_id").GetUInt32());
                    var replyAt = Array.FindIndex(packets, packet => Opcode(packet) == EmotePackets.ServerOpcode);
                    var stopAt = Array.FindIndex(packets, packet => Opcode(packet) == 0xC1 && HeroPacket(packet));
                    TestAssert.True(stopAt >= 0 && stopAt < replyAt,
                        "the stop lands before the 0x48 on the next step");
                }
                match.Runtime.Tick(0.2);
                // The movement model's stop arrival (MovementSystem.ArriveOnStop) seats the held hero on its own lattice anchor.
                TestAssert.Equal(match.Runtime.Movement.ClientAnchor(stopPosition), hero.Position,
                    "the stopped hero does not advance along its pre-emote route");
                // The stock emote handler immediately issues AI_HOLD. That
                // follow-up reaches the server after PlayEmote is sent;
                // a second C1 or StopAttack here can interrupt the animation.
                match.Clear();
                var nativeHold = ClientGameplayPackets.MoveOrder(hero.NetId, 1, hero.Position);
                var holdAccepted = false;
                match.Runtime.Session.Receive(1, 1,
                    match.Runtime.Cipher.Encrypt(nativeHold.Plaintext.ToArray()), context =>
                        holdAccepted = match.Runtime.Dependencies.Gameplay(context));
                match.Runtime.Delivery.Flush();
                TestAssert.True(holdAccepted, "native post-emote hold remains accepted");
                TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                    .Any(packet => Opcode(packet) is 0x39 or 0xC1),
                    "post-emote hold does not overwrite the first emote animation");

                match.Clear();
                match.Move(0, stopPosition + new Position2(0, 300));
                var nextRoute = match.Captures[0].Packets.Single(packet =>
                    Opcode(packet) == 0xC1 && packet.EntityReferences.Any(reference =>
                        reference.NetId == hero.NetId)).Plaintext.Span;
                var nextAnchor = match.Runtime.Movement.ClientAnchor(stopPosition);
                TestAssert.Equal((float)nextAnchor.X, BinaryPrimitives.ReadSingleLittleEndian(nextRoute[9..]));
                TestAssert.Equal((float)nextAnchor.Z, BinaryPrimitives.ReadSingleLittleEndian(nextRoute[13..]));
                // Batch 61 (SeatWireAnchorOnFloor): the order seats the hero on the wire anchor just transmitted, as the stock client seats
                // its actor, which is within one two-unit lattice cell of the stop; there is no rewind to an earlier route position.
                TestAssert.Equal(nextAnchor, hero.Position,
                    "the next move starts without an authoritative rewind");
                TestAssert.True(Math.Abs(hero.Position.X - stopPosition.X) < 2.0 &&
                        Math.Abs(hero.Position.Z - stopPosition.Z) < 2.0,
                    "the next move starts within the stop's own lattice cell");
                match.Runtime.Movement.Tick(0.1);
                TestAssert.True(hero.Position.Z > stopPosition.Z,
                    "the next accepted move advances from the emote stop");
            }
            hero.Waypoints.Add(hero.Position + new Position2(100, 0));
            hero.Target = hero.Waypoints[^1];
            match.Clear();
            var realStop = ClientGameplayPackets.MoveOrder(hero.NetId, 10, hero.Position);
            var stopAccepted = false;
            match.Runtime.Session.Receive(1, 1,
                match.Runtime.Cipher.Encrypt(realStop.Plaintext.ToArray()), context =>
                    stopAccepted = match.Runtime.Dependencies.Gameplay(context));
            match.Runtime.Delivery.Flush();
            TestAssert.True(stopAccepted, "a new explicit stop remains accepted");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
                "a real moving stop still reanchors its owner");
            match.Clear();
            stopAccepted = false;
            match.Runtime.Session.Receive(1, 1,
                match.Runtime.Cipher.Encrypt(realStop.Plaintext.ToArray()), context =>
                    stopAccepted = match.Runtime.Dependencies.Gameplay(context));
            match.Runtime.Delivery.Flush();
            TestAssert.True(stopAccepted);
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
                "an explicit stop still corrects a client ahead of the idle server");
            TestAssert.True(Send(0, length: 16), "exact zero cipher padding is accepted");
            TestAssert.False(Send(0, length: 16, badPadding: true)); NoEmote();
            TestAssert.False(Send(0, length: 8)); NoEmote();
            TestAssert.False(Send(uint.MaxValue)); NoEmote();
            TestAssert.False(Send(0, actor: enemy.NetId)); NoEmote();
            TestAssert.False(Send(0, channel: 4)); NoEmote();

            var stun = new BuffState("EmoteTestStun", type: BuffType.Stun);
            hero.Buffs.Add(stun);
            TestAssert.False(Send(0)); NoEmote();
            hero.Buffs.Remove(stun);
            var dash = new ForcedMoveState(hero.Position + new Position2(200, 0), 1000, .2, enemy.NetId, "Q");
            hero.ForcedMove = dash;
            TestAssert.False(Send(0)); NoEmote();
            TestAssert.True(ReferenceEquals(dash, hero.ForcedMove), "emotes cannot cancel a dash");
            hero.ForcedMove = null;
            hero.Alive = false;
            TestAssert.False(Send(0)); NoEmote();
            hero.Alive = true;

            enemy.Position = hero.Position + new Position2(150, 0);
            hero.AttackWindup = 1;
            hero.AttackCooldown = 0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, enemy));
            TestAssert.True(Send(0));
            TestAssert.False(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId),
                "emote interrupts a basic attack still winding up");
            hero.AttackWindup = 0;
            hero.MissileSpeed = 500;
            hero.AttackCooldown = 0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, enemy));
            TestAssert.True(Send(0));
            TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == hero.NetId),
                "emote preserves an already launched attack");

            enemy.Position = new Position2(13000, 13000);
            match.Runtime.Tick(4.6); // Let the shipped attack-reveal lease expire.
            match.Runtime.Delivery.Flush();
            TestAssert.False(match.Runtime.Session.Participants[1].View.Visible.Contains(hero.NetId));
            TestAssert.True(Send(0));
            match.Runtime.Tick(0.07);
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode));
            TestAssert.False(match.Captures[1].Packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode),
                "hidden champions cannot leak emotes to the opposing team");
        }
    }

    [Check("session-live-emote-followup-order-boundaries")]
    public static void EmoteHoldDoesNotMaskNewOrdersOrLateStops()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        hero.Position = new Position2(7000, 7000);
        match.Runtime.World.Clock.SetVirtual(match.Runtime.World.Clock.Now() + 10);

        bool Send(byte[] plaintext)
        {
            match.Clear();
            var accepted = false;
            match.Runtime.Session.Receive(1, 1,
                match.Runtime.Cipher.Encrypt(plaintext), context =>
                    accepted = match.Runtime.Dependencies.Gameplay(context));
            match.Runtime.Delivery.Flush();
            return accepted;
        }

        var request = new byte[EmotePackets.Length];
        request[0] = EmotePackets.ClientOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        hero.Waypoints.Add(new Position2(7300, 7000));
        TestAssert.True(Send(request));
        // Batch 437: a walking hero's stop leaves with the request and its 0x48 waits one step, so the stop lands first.
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode),
            "the walking emote waits one step after its stop");
        TestAssert.True(Send(ClientGameplayPackets.MoveOrder(hero.NetId, 2,
            new Position2(7400, 7000)).Plaintext.ToArray()), "new movement after emote is accepted");
        TestAssert.True(Send(ClientGameplayPackets.MoveOrder(hero.NetId, 1, hero.Position)
                .Plaintext.ToArray()),
            "hold after a new movement order is accepted");
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
            "new movement consumes the emote handshake; its stop still reanchors");

        hero.Waypoints.Add(new Position2(7300, 7000));
        TestAssert.True(Send(request));
        match.Runtime.Tick(0.07);
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode),
            "the deferred emote plays and opens its hold-echo window");
        match.Runtime.World.Clock.Advance(0.6);
        TestAssert.True(Send(ClientGameplayPackets.MoveOrder(hero.NetId, 1, hero.Position)
            .Plaintext.ToArray()));
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
            "a late hold is a normal corrective stop, not an emote echo");
    }

    // Batch 420 (owner finding 79): an emote while the hero walks. The stop leaves in the request's own flush, ahead of the 0x48:
    // the client's AI_HOLD does not clear the path it already follows. Reconstruction: the server order is inferred from the
    // owner's walking emote, not read from the server binary.
    [Check("session-live-emote-stop-precedes-playemote")]
    public static void EmoteStopLeavesAheadOfPlayEmote()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        var movingFrom = hero.Position;
        match.Move(0, movingFrom + new Position2(0, 300));
        match.Runtime.Movement.Tick(0.1);
        TestAssert.True(hero.Position.Z > movingFrom.Z, "the champion was moving when the emote arrived");
        var request = new byte[EmotePackets.Length];
        request[0] = EmotePackets.ClientOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        match.Clear();
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), context =>
            accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        TestAssert.True(accepted, "native emote request is accepted");
        TestAssert.True(hero.Waypoints.Count == 0 && hero.AttackTarget is null, "the emote stops the walking champion");
        // Batch 437 restates batch 420: the walking hero's stop leaves in the request's flush, and the 0x48 leaves on the next step.
        var packets = match.Captures[0].Packets.ToArray();
        var stopAt = Array.FindIndex(packets, packet => Opcode(packet) == 0xC1 &&
            packet.EntityReferences.Any(reference => reference.NetId == hero.NetId));
        TestAssert.True(stopAt >= 0, "the walking hero's stop route leaves in the request's own flush");
        TestAssert.False(packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode),
            "the 0x48 waits one step after the stop (batch 437)");
        match.Runtime.Tick(1.0 / 30.0);
        var later = match.Captures[0].Packets.ToArray();
        var emoteAt = Array.FindIndex(later, packet => Opcode(packet) == EmotePackets.ServerOpcode);
        TestAssert.True(emoteAt > stopAt,
            "the walking hero's 0x48 leaves on the next step, after its stop, so the client does not cancel the emote");
    }

    // Batch 437 (owner, 28 October 2026: "Dancing emote does not loop"): a standing hero has no route or attack to stop, so its
    // emote sends no stop. Batch 420 sent the stop for every emote, and a redundant stop interrupts the animation (overnight
    // polish of 23 September 2026, item 6). The dance then loops until the hero's next order: for ten seconds its client receives no
    // stop, route or animation packet.
    [Check("session-live-emote-standing-dance-sends-no-stop")]
    public static void StandingDanceSendsNoStop()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        match.Hero(1).Position = new Position2(13000, 13000);
        var request = new byte[EmotePackets.Length];
        request[0] = EmotePackets.ClientOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        match.Clear();
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), context =>
            accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        TestAssert.True(accepted, "native emote request is accepted");
        bool HeroControl(PlaintextPacket packet) => (Opcode(packet) is 0x39 or 0xC1 or 0x2B or 0xB8) &&
            packet.EntityReferences.Any(reference => reference.NetId == hero.NetId);
        foreach (var capture in match.Captures)
        {
            TestAssert.True(capture.Packets.Any(packet => Opcode(packet) == EmotePackets.ServerOpcode),
                "the standing emote is broadcast in the request's own flush");
            TestAssert.False(capture.Packets.Any(HeroControl), "a standing hero has no stop, route or animation to send");
        }
        for (var step = 0; step < 300; step++)
        {
            match.Clear();
            match.Runtime.Tick(1.0 / 30.0);
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(HeroControl),
                    "the dance is not interrupted: no stop, route or animation reaches the emoting hero's clients");
        }
    }

    [Check("session-live-stats-update-request")]
    public static void StatsUpdateRequestRepublishesOwnedHero()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        match.Runtime.Session.Participants[0].Loaded = true;
        match.Clear();
        var request = new byte[5];
        request[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        var accepted = false;
        // The native five-byte request is below the legacy cipher block size,
        // so it travels as plaintext (the session preserves that rule).
        match.Runtime.Session.Receive(1, 1, request, context =>
            accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        TestAssert.True(accepted, "stock stats update request is accepted for the owned hero");
        TestAssert.True(match.Captures.SelectMany(capture => capture.Packets)
            .Any(packet => Opcode(packet) == 0xCC && packet.EntityReferences.Any(reference =>
                reference.NetId == hero.NetId)), "stats update request republishes hero stats");

        // RequestStatsUpdate builds the actor from the native Player object,
        // so a client cannot ask for another hero's stats by forging bytes 1..4.
        var forged = new byte[5];
        forged[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(1), match.Hero(1).NetId);
        var forgedAccepted = false;
        match.Runtime.Session.Receive(1, 1, forged, context =>
            forgedAccepted = match.Runtime.Dependencies.Gameplay(context));
        TestAssert.False(forgedAccepted,
            "stats update requests remain restricted to the issuing hero");
    }
}
