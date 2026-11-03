using System.Buffers.Binary;
using Amberline.Tests;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;

namespace Amberline.Tests.Network;

public static partial class P1NetworkChecks
{
    [Check("network.p1-map-ping-diagnostics")]
    public static void MapPingDiagnosticsUseTheTypedNotificationReader()
    {
        var packet = PingPackets.MapPingNotification(0x4000_0001,
            new Position3(1234.5, 45.75, 6789.25), 0x4000_0002,
            MapPingCategory.Danger, playAudio: false, showChat: true, pingThrottled: true);
        var decoded = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
        TestAssert.True(decoded.HasDecoder);
        TestAssert.Equal("MapPing", decoded.Name);
        TestAssert.Equal("MapPing", WirePacketDecoder.LogName(PingPackets.ServerOpcode, 3));
        TestAssert.Equal(0x4000_0001u, (uint)decoded.Fields["sender_netid"]!);
        TestAssert.Equal(0x4000_0001u, (uint)decoded.Fields["source_netid"]!);
        TestAssert.Equal(0x4000_0002u, (uint)decoded.Fields["target_netid"]!);
        TestAssert.Equal(new[] { 1234.5, 45.75, 6789.25 }, (double[])decoded.Fields["position"]!);
        TestAssert.Equal((byte)MapPingCategory.Danger, (byte)decoded.Fields["category"]!);
        TestAssert.False((bool)decoded.Fields["play_audio"]!);
        TestAssert.True((bool)decoded.Fields["show_chat"]!);
        TestAssert.True((bool)decoded.Fields["ping_throttled"]!);

        var padded = packet.Plaintext.ToArray().Concat(new byte[6]).ToArray();
        TestAssert.Equal(decoded.Fields.Count, WirePacketDecoder.Decode(padded, 3).Fields.Count);
        var badSource = packet.Plaintext.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badSource.AsSpan(21), 0x4000_0003);
        padded[^1] = 1;
        foreach (var malformed in new[] { packet.Plaintext.ToArray()[..^1], badSource, padded })
        {
            var invalid = WirePacketDecoder.Decode(malformed, 3);
            TestAssert.Equal(0, invalid.Fields.Count, "malformed pings keep raw bytes without invented fields");
            TestAssert.Equal(Convert.ToHexString(malformed).ToLowerInvariant(), invalid.Raw);
        }
    }

    [Check("network.p1-map-ping-stock-presentation")]
    public static void StockPingUsesOnlyTheNotificationForGroundAndFog()
    {
        var runtime = CreateStartedFixture(includeSecondOrderSeat: true);
        var session = runtime.Session;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var category in new[] { MapPingCategory.Offense, MapPingCategory.Danger })
        foreach (var inFog in new[] { false, true })
        {
            var sender = session.Participants.First(seat => seat.Team == team);
            var location = session.Participants.First(seat => (seat.Team != team) == inFog).Hero!;
            var position = new Position3(location.Position.X, 60, location.Position.Z);
            var request = PingPackets.MapPingRequest(sender.Hero!.NetId, position, 0, category);
            var expected = session.Participants.Where(seat => seat.Team == team)
                .Select(seat => seat.ConnectionId!.Value).ToArray();
            var views = session.Participants.Select(seat => seat.View.Visible.ToArray()).ToArray();
            var nextEffect = runtime.World.Allocator.Next(UnitType.Missile);
            var bubbles = runtime.World.TimedVisionBubbles.ToArray();
            var output = session.Receive(sender.ConnectionId!.Value, 1, request.ToWireBytes(runtime.Cipher));
            TestAssert.Equal(expected, output.Select(item => item.ConnectionId).ToArray());
            foreach (var item in output)
            {
                TestAssert.True(PingPackets.TryReadNotification(item.Packet.Plaintext.Span, out var ping));
                TestAssert.Equal(new Position3((float)position.X, (float)position.Y,
                    (float)position.Z), ping.Position,
                    "stock notification preserves the request's float32 coordinates");
                TestAssert.Equal(sender.Hero.NetId, ping.SourceNetId);
                TestAssert.Equal(category, ping.Category);
                TestAssert.True(ping.PlayAudio && ping.ShowChat && !ping.PingThrottled);
            }
            TestAssert.Equal(nextEffect, runtime.World.Allocator.Next(UnitType.Missile));
            TestAssert.Equal(bubbles, runtime.World.TimedVisionBubbles.ToArray());
            for (var i = 0; i < views.Length; i++)
                TestAssert.Equal(views[i], session.Participants[i].View.Visible.ToArray());
        }
        runtime.World.Clock.Advance(1.1);
        AssertNoPingEffects(session.RunSchedulers());
    }

    [Check("network.p1-map-ping-stock-reconnect")]
    public static void StockPingDoesNotReplayOnReconnect()
    {
        var runtime = CreateStartedFixture(includeSecondOrderSeat: true);
        var session = runtime.Session;
        var hero = session.Participants[0].Hero!;
        var request = PingPackets.MapPingRequest(0, new Position3(hero.Position.X, 60, hero.Position.Z),
            0, MapPingCategory.Offense);
        TestAssert.Equal(2, session.Receive(11, 1, request.ToWireBytes(runtime.Cipher)).Count);
        TestAssert.True(session.Disconnect(11));
        runtime.World.Clock.SetVirtual(0.1);
        var replay = new List<SessionOutboundPacket>();
        replay.AddRange(session.Receive(15, 0, session.BuildKeyCheck(1)));
        replay.AddRange(session.Receive(15, 1, new byte[] { 0xC5 }));
        replay.AddRange(session.Receive(15, 1, new byte[] { 0xC6, 0, 0, 0, 0 }));
        runtime.World.Clock.SetVirtual(session.Participants[0].SpawnAt!.Value);
        replay.AddRange(session.RunSchedulers());
        replay.AddRange(session.Receive(15, 1, new byte[] { 0xC6, 0, 0, 0, 0 }));
        session.MarkReady(15);
        replay.AddRange(session.RunSchedulers());
        TestAssert.True(session.Participants[0].Spawned && session.Participants[0].Ready);
        TestAssert.False(replay.Any(item => item.Packet.Plaintext.Span[0] == PingPackets.ServerOpcode));
        AssertNoPingEffects(replay);
        TestAssert.Equal(new[] { 15L, 13L }, session.Receive(15, 1, request.ToWireBytes(runtime.Cipher))
            .Select(item => item.ConnectionId).ToArray());
    }

    [Check("network.p1-map-ping-stock-rejection")]
    public static void RejectedPingsAllocateNeitherEffectsNorRateBudget()
    {
        var runtime = CreateStartedFixture(includeSecondOrderSeat: true);
        var session = runtime.Session;
        var sender = session.Participants[0];
        var hero = sender.Hero!;
        var position = new Position3(hero.Position.X, 60, hero.Position.Z);
        var packet = PingPackets.MapPingRequest(0, position, 0, MapPingCategory.Offense);
        var firstId = runtime.World.Allocator.Next(UnitType.Missile);
        var waiting = MatchRuntime.CreateProduction(runtime.Navigation);
        waiting.World.Clock.SetVirtual(0.0);
        waiting.Session.Authenticate(11, waiting.Session.BuildKeyCheck(1));
        waiting.Session.Spawn(11);
        var waitingId = waiting.World.Allocator.Next(UnitType.Missile);
        TestAssert.Equal(0, waiting.Session.Receive(11, 1, packet.ToWireBytes(waiting.Cipher)).Count,
            "pings are gated until the match starts");
        TestAssert.Equal(waitingId, waiting.World.Allocator.Next(UnitType.Missile));
        TestAssert.Equal(0, session.Receive(999, 1, packet.ToWireBytes(runtime.Cipher)).Count);
        TestAssert.Equal(0, session.Receive(11, 3, packet.Plaintext.Span).Count);
        var badActor = packet.Plaintext.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badActor.AsSpan(1), session.Participants[1].Hero!.NetId);
        var badCategory = packet.Plaintext.ToArray();
        badCategory[^1] = 2;
        var nonFinite = packet.Plaintext.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(nonFinite.AsSpan(9), float.NaN);
        var infinity = packet.Plaintext.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(infinity.AsSpan(5), float.PositiveInfinity);
        var outside = PingPackets.MapPingRequest(0,
            new Position3(session.MapBounds.MaxX + 1, 60, position.Z), 0, MapPingCategory.Offense);
        var hidden = PingPackets.MapPingRequest(0, position, session.Participants[1].Hero!.NetId, MapPingCategory.Danger);
        var absent = PingPackets.MapPingRequest(0, position, uint.MaxValue, MapPingCategory.Danger);
        var rejected = new (string Name, byte[] Payload)[]
        {
            ("wrong actor", badActor), ("unsupported category", badCategory),
            ("NaN height", nonFinite), ("infinite X", infinity),
            ("outside map bounds", outside.Plaintext.ToArray()),
            ("hidden enemy target", hidden.Plaintext.ToArray()),
            ("unknown target", absent.Plaintext.ToArray()),
            ("truncated category", packet.Plaintext.ToArray()[..^1]),
        };
        foreach (var (name, malformed) in rejected)
        {
            // Padding the truncated zero-category request would recreate a valid offense ping.
            TestAssert.Equal(0, session.Receive(11, 1, NativeRequestWire.Encrypt(runtime.Cipher, malformed)).Count,
                $"{name} must produce neither a notification nor a marker");
        }
        sender.Spawned = false;
        TestAssert.Equal(0, session.Receive(11, 1, packet.ToWireBytes(runtime.Cipher)).Count);
        sender.Spawned = true;
        TestAssert.Equal(firstId, runtime.World.Allocator.Next(UnitType.Missile));
        for (var i = 0; i < PingPackets.PingBurstCapacity; i++)
            TestAssert.Equal(2, session.Receive(11, 1, packet.ToWireBytes(runtime.Cipher)).Count,
                "invalid attempts do not spend the accepted-ping budget");
        var afterBurst = runtime.World.Allocator.Next(UnitType.Missile);
        TestAssert.Equal(0, session.Receive(11, 1, packet.ToWireBytes(runtime.Cipher)).Count);
        TestAssert.Equal(firstId, afterBurst);
        TestAssert.Equal(afterBurst, runtime.World.Allocator.Next(UnitType.Missile), "a throttled ping allocates no marker");
        runtime.World.Clock.SetVirtual(1.0);
        AssertNoPingEffects(session.RunSchedulers());
        runtime.World.Clock.SetVirtual(PingPackets.PingRefillSeconds);
        sender.Ready = false;
        session.Participants[2].Ready = false;
        TestAssert.Equal(0, session.Receive(11, 1, packet.ToWireBytes(runtime.Cipher)).Count);
        TestAssert.Equal(afterBurst, runtime.World.Allocator.Next(UnitType.Missile),
            "an empty accepted audience needs no effect ID or cleanup state");
        runtime.World.Clock.Advance(1.0);
        AssertNoPingEffects(session.RunSchedulers());
    }

    [Check("network.p1-map-ping-stock-bounds")]
    public static void StockPingKeepsExistingInputBounds()
    {
        var runtime = CreateStartedFixture();
        var session = runtime.Session;
        var position = new Position3((float)(session.MapBounds.MaxX - 1), float.MaxValue,
            (float)(session.MapBounds.MaxZ - 1));
        var request = PingPackets.MapPingRequest(0, position, 0, MapPingCategory.Offense);
        var output = session.Receive(11, 1, request.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(1, output.Count);
        TestAssert.True(PingPackets.TryReadNotification(output[0].Packet.Plaintext.Span, out var ping));
        TestAssert.Equal(position, ping.Position);
    }

    private static void AssertNoPingEffects(IReadOnlyList<SessionOutboundPacket> packets)
    {
        foreach (var packet in packets.Where(item => item.Packet.Plaintext.Span[0] == FxPackets.CreateGroupOpcode))
        {
            var decoded = WirePacketDecoder.Decode(packet.Packet.Plaintext.Span, packet.Packet.Channel);
            foreach (var group in (object?[])decoded.Fields["groups"]!)
            {
                var hash = (uint)((Dictionary<string, object?>)group!)["effect_name_hash"]!;
                TestAssert.False(hash == SpellPackets.BuffNameHash("Cursor_Moveto") ||
                    hash == SpellPackets.BuffNameHash("Cursor_Moveto_red"), "stock pings schedule no custom ground markers");
            }
        }
    }

}
