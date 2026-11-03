using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;

namespace Amberline.Tests.Network;

public static class TransportSecurityChecks
{


    [Check("transport.production-limits")]
    public static void ProductionNativeLimitsAreSelectedWithoutChangingGenericDefaults()
    {
        using var generic = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var production = new LeagueEnetHost(0, 1, 8,
            maximumPacketSize: LeagueEnetHost.ProductionMaximumPacketSize,
            maximumWaitingData: LeagueEnetHost.ProductionMaximumWaitingData, bindHost: "127.0.0.1");

        TestAssert.Equal((nuint)0, generic.MaximumPacketSize,
            "generic transport retains ENet's native packet default");
        TestAssert.Equal((nuint)0, generic.MaximumWaitingData,
            "generic transport retains ENet's native waiting-data default");
        TestAssert.Equal((nuint)LeagueEnetHost.ProductionMaximumPacketSize,
            production.MaximumPacketSize);
        TestAssert.Equal((nuint)LeagueEnetHost.ProductionMaximumWaitingData,
            production.MaximumWaitingData);

        using var sender = new LeagueEnetHost(0, 1, 8,
            maximumPacketSize: LeagueEnetHost.ProductionMaximumPacketSize, bindHost: "127.0.0.1");
        var peer = sender.Connect("127.0.0.1", generic.Port, 8);
        TestAssert.Throws<InvalidDataException>(() => peer.Send(1,
            new byte[LeagueEnetHost.ProductionMaximumPacketSize + 1]),
            "the managed send guard rejects oversize data before native allocation");
    }

    [Check("transport.bounded-native-capability")]
    public static void BoundedHostsRequireANamedNativeCapability()
    {
        var header = File.ReadAllText(FindWorkspaceFile(Path.Combine("native", "enet",
            "amberline_enet_bridge.h")));
        var managed = File.ReadAllText(FindWorkspaceFile(Path.Combine("src", "Amberline",
            "Network", "Transport", "LeagueEnetTransport.cs")));
        var legacyStart = header.IndexOf("amberline_enet_host_create(", StringComparison.Ordinal);
        var boundedStart = header.IndexOf("amberline_enet_host_create_bounded(", StringComparison.Ordinal);
        TestAssert.True(legacyStart >= 0 && boundedStart > legacyStart);
        var legacy = header[legacyStart..boundedStart];
        TestAssert.False(legacy.Contains("maximum_packet_size", StringComparison.Ordinal),
            "the legacy native export keeps its seven-argument ABI");
        TestAssert.True(header.Contains("amberline_enet_host_create_bounded", StringComparison.Ordinal) &&
            managed.Contains("amberline_enet_host_create_bounded", StringComparison.Ordinal) &&
            managed.Contains("The loaded ENet bridge does not provide bounded host creation.",
                StringComparison.Ordinal) &&
            managed.Contains("LeagueEnetNative.Deinitialize();", StringComparison.Ordinal),
            "bounded construction must fail explicitly when a stale bridge lacks its named export");
    }

    [Check("transport.native-bounded-loopback")]
    public static void BoundedNativeLoopbackCoversReceiveFragmentWaitingAndCleanup()
    {
        const int packetLimit = 8192;
        const int waitingLimit = 16 * 1024;
        using var server = new LeagueEnetHost(0, 1, 8, "127.0.0.1",
            maximumPacketSize: packetLimit, maximumWaitingData: waitingLimit);
        using var client = new LeagueEnetHost(0, 1, 8, "127.0.0.1");
        var clientPeer = client.Connect("127.0.0.1", server.Port, 8);
        LeagueEnetPeer? serverPeer = null;
        var clientConnected = false;
        PumpUntilConnected(server, client, () => clientConnected && serverPeer is not null,
            @event =>
            {
                if (@event.Type != LeagueEnetEventType.Connect || @event.Peer is null) return;
                if (ReferenceEquals(@event.Peer, clientPeer)) clientConnected = true;
                else serverPeer = @event.Peer;
            });

        var boundaryAndFragment = Enumerable.Range(0, packetLimit)
            .Select(index => unchecked((byte)index)).ToArray();
        clientPeer.Send(1, boundaryAndFragment, LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var received = Receive(server, 1000);
        TestAssert.Equal(boundaryAndFragment, received.Payload,
            "a packet exactly at the native boundary survives fragmented receive");
        TestAssert.Equal(0u, checked((uint)serverPeer!.Stats.WaitingDataBytes),
            "disposing a received fragmented packet releases native waiting data");

        clientPeer.Send(1, new byte[packetLimit + 1], LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var rejected = server.Service(1000);
        TestAssert.Equal(LeagueEnetEventType.None, rejected.Type,
            "native oversize fragments are silently rejected before managed delivery");
        TestAssert.Equal(0u, checked((uint)serverPeer.Stats.WaitingDataBytes),
            "native oversize rejection leaves no retained waiting data");
        TestAssert.Equal(5u, serverPeer.Stats.State,
            "native oversize rejection keeps the bounded peer connected");

        // A separate channel proves that the native handler did not retire the
        // peer as the managed oversize fallback would have done.
        clientPeer.Send(2, new byte[] { 0xA5 }, LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var control = Receive(server, 1000);
        TestAssert.Equal(2, control.Channel,
            "independent control traffic survives an oversize channel-1 datagram");
        TestAssert.Equal(new byte[] { 0xA5 }, control.Payload);

        const int smallWaitingLimit = 256;
        using var waitingServer = new LeagueEnetHost(0, 1, 8, "127.0.0.1",
            maximumPacketSize: 128, maximumWaitingData: smallWaitingLimit);
        using var waitingClient = new LeagueEnetHost(0, 1, 8, "127.0.0.1");
        var waitingClientPeer = waitingClient.Connect("127.0.0.1", waitingServer.Port, 8);
        LeagueEnetPeer? waitingServerPeer = null;
        var waitingClientConnected = false;
        PumpUntilConnected(waitingServer, waitingClient,
            () => waitingClientConnected && waitingServerPeer is not null,
            @event =>
            {
                if (@event.Type != LeagueEnetEventType.Connect || @event.Peer is null) return;
                if (ReferenceEquals(@event.Peer, waitingClientPeer)) waitingClientConnected = true;
                else waitingServerPeer = @event.Peer;
            });
        // Exact-boundary control: two 128-byte payloads consume the whole
        // budget, and the accounting includes the event just delivered.
        foreach (var size in new[] { 128, 128 })
            waitingClientPeer.Send(1, new byte[size], LeagueEnetPacketFlags.Reliable);
        waitingClient.Flush();
        var exactFirst = Receive(waitingServer, 1000);
        var exactAccounted = checked((long)exactFirst.Payload.Length +
            (long)waitingServerPeer!.Stats.WaitingDataBytes);
        TestAssert.Equal((long)smallWaitingLimit, exactAccounted,
            "native admission allows payloads exactly at the waiting-data boundary");
        var exactReceived = 1;
        while (true)
        {
            var @event = waitingServer.CheckEvents();
            if (@event.Type == LeagueEnetEventType.None) break;
            if (@event.Type == LeagueEnetEventType.Receive) exactReceived++;
        }
        TestAssert.Equal(2, exactReceived,
            "exact-boundary payloads remain deliverable");
        TestAssert.Equal(0u, checked((uint)waitingServerPeer.Stats.WaitingDataBytes),
            "disposing every exact-boundary packet releases waiting data");

        // Non-dividing, variable-size control: the third payload would cross
        // 256 bytes. It must be rejected before its native packet allocation,
        // rather than admitted as the old pre-admission threshold allowed.
        foreach (var size in new[] { 96, 83, 79 })
            waitingClientPeer.Send(1, new byte[size], LeagueEnetPacketFlags.Reliable);
        waitingClient.Flush();
        var boundaryFirst = Receive(waitingServer, 1000);
        TestAssert.Equal(96, boundaryFirst.Payload.Length,
            "the variable-size boundary sequence preserves packet order");
        var boundaryWaiting = waitingServerPeer.Stats.WaitingDataBytes;
        var boundaryAccounted = checked((long)boundaryFirst.Payload.Length +
            (long)boundaryWaiting);
        TestAssert.Equal(179L, boundaryAccounted,
            "queued plus just-delivered payloads stay below the strict budget");
        TestAssert.Equal(83u, checked((uint)boundaryWaiting),
            "the payload that would cross the budget is rejected before allocation");
        var boundaryReceived = 1;
        while (true)
        {
            var @event = waitingServer.CheckEvents();
            if (@event.Type == LeagueEnetEventType.None) break;
            if (@event.Type == LeagueEnetEventType.Receive) boundaryReceived++;
        }
        TestAssert.Equal(2, boundaryReceived,
            "strict waiting-data admission rejects only the crossing payload");
        TestAssert.Equal(0u, checked((uint)waitingServerPeer.Stats.WaitingDataBytes),
            "disposing every variable-size queued packet releases waiting data");
        TestAssert.Equal(5u, waitingServerPeer.Stats.State,
            "waiting-data rejection keeps the bounded peer connected");

        waitingClientPeer.Send(2, new byte[] { 0xA5 }, LeagueEnetPacketFlags.Reliable);
        waitingClient.Flush();
        var waitingControl = Receive(waitingServer, 1000);
        TestAssert.Equal(2, waitingControl.Channel,
            "valid traffic survives a variable-size waiting-data rejection");
    }

    [Check("network.full-roster-reconnect")]
    public static void TenSeatReconnectSeedsMapKnownStateForBothTeams()
    {
        var champions = ChampionCatalog.PlayableNames.ToArray();
        var roster = Enumerable.Range(0, 10).Select(index =>
            new SessionParticipantDefinition(
                (ulong)(index + 1), (uint)index, $"Player{index + 1}", champions[index],
                index % 2 == 0 ? Team.Order : Team.Chaos)).ToArray();
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        foreach (var index in Enumerable.Range(0, roster.Length))
        {
            var connectionId = 100L + index;
            TestAssert.True(runtime.Session.Authenticate(connectionId,
                runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null);
            runtime.Session.Spawn(connectionId);
            runtime.Session.MarkReady(connectionId);
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "all ten seats cross the start barrier");

        foreach (var index in new[] { 0, 1 })
        {
            var participant = runtime.Session.Participants[index];
            var oldConnection = participant.ConnectionId!.Value;
            var hero = participant.Hero!;
            TestAssert.True(runtime.Session.Disconnect(oldConnection));
            var newConnection = 1000L + index;
            TestAssert.True(runtime.Session.AuthenticateReconnect(newConnection,
                runtime.Session.BuildKeyCheck(participant.PlayerId)) is not null);
            runtime.Session.Receive(newConnection, 1, new byte[] { 0xC5 });
            runtime.Session.Receive(newConnection, 1, new byte[] { 0xC6, 0, 0, 0, 0 });
            runtime.World.Clock.SetVirtual(participant.SpawnAt!.Value);
            runtime.Session.RunSchedulers();
            runtime.Session.Receive(newConnection, 1, new byte[] { 0xA4, 0, 0, 0, 0, 1 });
            runtime.Session.Receive(newConnection, 1, new byte[] { 0xC6, 0, 0, 0, 0 });
            var ready = runtime.Session.RunSchedulers();
            TestAssert.True(participant.Ready, "reconnect loading reaches ready for both teams");
            foreach (var mapEntity in runtime.Session.MapEntities)
                TestAssert.True(participant.View.Known.Contains(mapEntity.NetId),
                    "reconnect seeds the complete map known set before its burst");
            var opposingHeroes = runtime.Session.Participants
                .Where(other => other != participant && other.Team != participant.Team)
                .Select(other => other.Hero!)
                .ToArray();
            TestAssert.True(opposingHeroes.Length > 0,
                "ten-seat reconnect has explicitly selected opposing heroes");
            foreach (var enemyHero in opposingHeroes)
                TestAssert.False(participant.View.Visible.Contains(enemyHero.NetId),
                    "map-known seeding preserves fog for every opposing hero");
            TestAssert.True(ready.Any(packet => packet.ConnectionId == newConnection &&
                packet.Packet.Plaintext.Span[0] == 0xA5),
                "reconnect still emits the direct allied structure burst");
            TestAssert.True(ReferenceEquals(hero, participant.Hero),
                "reconnect keeps the fixed seat hero");
        }
    }

    [Check("network.pending-authentication-deadline", Serial = true)]
    public static void PendingGenerationDropsTrafficAndExpiresAtMonotonicBoundary()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        long monotonic = 0;
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 2, 8, bindHost: "127.0.0.1"), ownsHost: true,
            monotonicTimestamp: () => monotonic);
        using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        var peer = client.Connect("127.0.0.1", network.Port, 8);
        PumpUntil(network, client, () => network.ConnectionIds.Count == 1);

        // Loading/pause/gameplay traffic is silently discarded before auth;
        // neither it nor an ENet keepalive extends this generation deadline.
        peer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
        peer.Ping();
        client.Flush();
        for (var index = 0; index < 4; index++)
        {
            network.Pump(1, flushDelivery: false);
            Drain(client);
        }
        TestAssert.True(runtime.Session.Participant(network.ConnectionIds.Single()) is null,
            "non-auth pre-auth traffic cannot reserve a seat");

        var timeoutTicks = checked((long)Math.Ceiling(
            MatchRuntimeNetwork.AuthenticationTimeoutSeconds * Stopwatch.Frequency));
        monotonic = timeoutTicks - 1;
        peer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        client.Flush();
        PumpUntil(network, client, () => runtime.Session.Participant(network.ConnectionIds.Single()) is not null);
        TestAssert.Equal(1UL, runtime.Session.Participants.Single().PlayerId,
            "a valid channel-zero key authenticates before the deadline");

        // A second generation has a separate deadline and expires on the exact
        // monotonic boundary despite the first seat remaining authenticated.
        monotonic = 0;
        using var idleClient = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        _ = idleClient.Connect("127.0.0.1", network.Port, 8);
        PumpUntil(network, idleClient, () => network.ConnectionIds.Count == 2);
        monotonic = timeoutTicks;
        network.Pump(0, flushDelivery: false);
        Drain(idleClient);
        TestAssert.Equal(1, network.ConnectionIds.Count,
            "no-input pending generations are reclaimed at ten seconds");
        TestAssert.True(runtime.Session.Participants.Single().ConnectionId is long,
            "timeout cleanup cannot detach the authenticated seat");
    }

    private static LeagueEnetEvent Receive(LeagueEnetHost host, int timeoutMilliseconds)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var @event = host.Service(timeoutMilliseconds);
            if (@event.Type == LeagueEnetEventType.Receive) return @event;
        }
        throw new InvalidOperationException("bounded native receive did not arrive");
    }

    private static void PumpUntilConnected(LeagueEnetHost server, LeagueEnetHost client,
        Func<bool> complete, Action<LeagueEnetEvent> observe)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!complete())
        {
            observe(server.Service(20));
            observe(client.Service(20));
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new InvalidOperationException("bounded native peers did not connect");
        }
    }

    private static string FindWorkspaceFile(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var direct = Path.Combine(current.FullName, relative);
                if (File.Exists(direct)) return direct;
                var nested = Path.Combine(current.FullName, "emu", relative);
                if (File.Exists(nested)) return nested;
            }
        }
        throw new FileNotFoundException(relative);
    }

    private static void PumpUntil(MatchRuntimeNetwork network, LeagueEnetHost client,
        Func<bool> complete)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!complete())
        {
            network.Pump(1, flushDelivery: false);
            Drain(client);
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new InvalidOperationException("transport security loopback timed out");
        }
    }

    private static void Drain(LeagueEnetHost host)
    {
        while (host.Service(0).Type != LeagueEnetEventType.None) { }
    }
}
