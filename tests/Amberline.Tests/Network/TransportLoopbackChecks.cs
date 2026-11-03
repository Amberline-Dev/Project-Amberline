using System.Diagnostics;
using Amberline.Network.Transport;

namespace Amberline.Tests;

public static class TransportLoopbackChecks
{
    [Check("transport.loopback")]
    public static void Loopback() => Loopback(enableCrc32: false);

    [Check("transport.loopback-crc32")]
    public static void LoopbackWithChecksum() => Loopback(enableCrc32: true);

    [Check("transport.pending-disconnect-send")]
    public static void ClosingNativePeerDoesNotAbortWorldDelivery()
    {
        using var server = new LeagueEnetHost(0, 1, 2, bindHost: "127.0.0.1");
        using var client = new LeagueEnetHost(0, 1, 2, bindHost: "127.0.0.1");
        var clientPeer = client.Connect("127.0.0.1", server.Port, 2);
        LeagueEnetPeer? serverPeer = null;
        var clientConnected = false;
        PumpUntil(server, client, () => clientConnected && serverPeer is not null, received =>
        {
            if (received.Type != LeagueEnetEventType.Connect) return;
            if (ReferenceEquals(received.Peer, clientPeer)) clientConnected = true;
            else serverPeer = received.Peer;
        });
        TestAssert.Throws<InvalidOperationException>(() => serverPeer!.Send(2, new byte[] { 1 }),
            "a bad channel on a connected peer must still fail");

        // Native service changes a peer's state before MatchRuntimeNetwork
        // drains its disconnect event. Enter that real ENet state directly,
        // leaving the managed generation alive until the event is observed.
        LeagueEnetNative.PeerDisconnect(serverPeer!.Identity, 7);
        TestAssert.Equal(7u, serverPeer.Stats.State);
        TestAssert.False(serverPeer.TrySend(1, new byte[] { 0x41 }, LeagueEnetPacketFlags.Reliable),
            "a closing peer must report the native enqueue rejection");
        serverPeer.Send(1, new byte[] { 0x42 }, LeagueEnetPacketFlags.Reliable);
        var serverDisconnected = false;
        var clientDisconnected = false;
        PumpUntil(server, client, () => serverDisconnected && clientDisconnected, received =>
        {
            if (received.Type != LeagueEnetEventType.Disconnect) return;
            if (ReferenceEquals(received.Peer, serverPeer)) serverDisconnected = true;
            if (ReferenceEquals(received.Peer, clientPeer)) clientDisconnected = true;
        });
        TestAssert.Throws<InvalidOperationException>(() => serverPeer.Send(1, new byte[] { 0x42 }),
            "a retired generation still rejects use after the disconnect event");
    }

    private static void Loopback(bool enableCrc32)
    {
        using var server = new LeagueEnetHost(port: 0, peerLimit: 1, channelLimit: 4, enableCrc32: enableCrc32, bindHost: "127.0.0.1");
        using var client = new LeagueEnetHost(port: 0, peerLimit: 1, channelLimit: 4, enableCrc32: enableCrc32, bindHost: "127.0.0.1");

        TestAssert.True(server.Port != 0, "server must bind an ephemeral UDP port");
        TestAssert.True(client.Port != 0, "client must bind an ephemeral UDP port");
        TestAssert.True(server.Port != 5119 && client.Port != 5119,
            "the transport check must not use the live game port");

        var clientPeer = client.Connect("127.0.0.1", server.Port, channelCount: 2, data: 0x12345678);
        LeagueEnetPeer? serverPeer = null;
        LeagueEnetEvent? clientConnect = null;
        LeagueEnetEvent? serverConnect = null;

        PumpUntil(
            server,
            client,
            () => clientConnect is not null && serverConnect is not null,
            @event =>
            {
                if (@event.Type == LeagueEnetEventType.Connect)
                {
                    if (ReferenceEquals(@event.Peer, clientPeer))
                        clientConnect = @event;
                    else
                    {
                        serverConnect = @event;
                        serverPeer = @event.Peer;
                    }
                }
            });

        TestAssert.True(serverPeer is not null, "server must receive a connect event");
        TestAssert.True(clientConnect!.Peer is not null, "client connect event needs a peer identity");
        TestAssert.Equal(clientPeer.Identity, clientConnect.Peer!.Identity);
        TestAssert.Equal(serverPeer!.Identity, serverConnect!.Peer!.Identity);
        TestAssert.True(clientPeer.Stats.ChannelCount >= 2);
        TestAssert.True(serverPeer.Stats.ChannelCount >= 2);

        var packets = new[]
        {
            (Flags: LeagueEnetPacketFlags.None, Data: new byte[] { 0x10, 0x00, 0x01 }),
            (Flags: LeagueEnetPacketFlags.Reliable, Data: new byte[] { 0x10, 0x01, 0x02, 0x03 }),
            (Flags: LeagueEnetPacketFlags.Unsequenced, Data: new byte[] { 0x10, 0x02, 0x04, 0x05, 0x06 })
        };

        foreach (var packet in packets)
            clientPeer.Send(channel: 1, packet.Data, packet.Flags);
        client.Flush();

        var received = new List<LeagueEnetEvent>();
        PumpUntil(
            server,
            client,
            () => received.Count == packets.Length,
            @event =>
            {
                if (@event.Type == LeagueEnetEventType.Receive)
                    received.Add(@event);
            });

        foreach (var packet in packets)
            TestAssert.True(received.Any(item =>
                    item.Channel == 1 &&
                    item.PacketFlags == packet.Flags &&
                    item.Payload.SequenceEqual(packet.Data)),
                $"every flag variant must arrive with copied payload bytes and native flags ({packet.Flags})");

        clientPeer.Disconnect(data: 0xBEEF);
        var clientDisconnected = false;
        var serverDisconnected = false;
        PumpUntil(
            server,
            client,
            () => clientDisconnected && serverDisconnected,
            @event =>
            {
                if (@event.Type != LeagueEnetEventType.Disconnect)
                    return;
                if (ReferenceEquals(@event.Peer, clientPeer))
                    clientDisconnected = true;
                if (ReferenceEquals(@event.Peer, serverPeer))
                    serverDisconnected = true;
            });

        TestAssert.Throws<InvalidOperationException>(
            () => clientPeer.Send(0, new byte[] { 0xFF }),
            "a disconnected peer wrapper must reject sends");
        TestAssert.Throws<InvalidOperationException>(
            () => { _ = clientPeer.Stats; },
            "a disconnected peer wrapper must reject statistics reads");
        TestAssert.Throws<InvalidOperationException>(
            () => serverPeer.Ping(),
            "a disconnected peer wrapper must reject pings");

        var reconnectedClientPeer = client.Connect("127.0.0.1", server.Port, channelCount: 2);
        LeagueEnetPeer? reconnectedServerPeer = null;
        var reconnectedClient = false;
        var reconnectedServer = false;
        PumpUntil(
            server,
            client,
            () => reconnectedClient && reconnectedServer,
            @event =>
            {
                if (@event.Type != LeagueEnetEventType.Connect || @event.Peer is null)
                    return;
                if (ReferenceEquals(@event.Peer, reconnectedClientPeer))
                    reconnectedClient = true;
                else
                {
                    reconnectedServerPeer = @event.Peer;
                    reconnectedServer = true;
                }
            });

        TestAssert.True(reconnectedServerPeer is not null, "the reused native slot must reconnect");
        TestAssert.False(ReferenceEquals(clientPeer, reconnectedClientPeer),
            "a reused native peer slot must receive a fresh managed wrapper");
        TestAssert.False(clientPeer.ConnectionId == reconnectedClientPeer.ConnectionId,
            "a reused native peer slot must receive a fresh connection generation");
        TestAssert.Throws<InvalidOperationException>(
            () => clientPeer.Send(0, new byte[] { 0xFE }),
            "an old wrapper must stay rejected after native slot reuse");
        reconnectedClientPeer.Send(0, new byte[] { 0x42 }, LeagueEnetPacketFlags.Reliable);

        ConnectThenDisconnectBeforeHandshake();
    }

    private static void ConnectThenDisconnectBeforeHandshake()
    {
        using var server = new LeagueEnetHost(port: 0, peerLimit: 1, channelLimit: 2, bindHost: "127.0.0.1");
        using var client = new LeagueEnetHost(port: 0, peerLimit: 1, channelLimit: 2, bindHost: "127.0.0.1");

        var retired = client.Connect("127.0.0.1", server.Port, channelCount: 1);
        retired.Disconnect();
        var replacement = client.Connect("127.0.0.1", server.Port, channelCount: 1);

        TestAssert.False(ReferenceEquals(retired, replacement),
            "a pre-handshake disconnect must not reuse its managed wrapper");
        TestAssert.False(retired.ConnectionId == replacement.ConnectionId,
            "a pre-handshake reconnect must receive a fresh connection generation");
        TestAssert.Throws<InvalidOperationException>(
            () => retired.Send(0, new byte[] { 0xFD }),
            "the pre-handshake wrapper must stay rejected after reconnect");

        var clientConnected = false;
        var serverConnected = false;
        PumpUntil(
            server,
            client,
            () => clientConnected && serverConnected,
            @event =>
            {
                if (@event.Type != LeagueEnetEventType.Connect)
                    return;
                if (ReferenceEquals(@event.Peer, replacement))
                    clientConnected = true;
                else
                    serverConnected = true;
            });
    }

    private static void PumpUntil(
        LeagueEnetHost server,
        LeagueEnetHost client,
        Func<bool> complete,
        Action<LeagueEnetEvent> observe)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!complete())
        {
            observe(server.Service(20));
            observe(client.Service(20));
            if (Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException("ENet loopback event did not arrive within five seconds");
        }
    }
}
