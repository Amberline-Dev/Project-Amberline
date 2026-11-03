using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;

namespace Amberline.Tests.Network;

/// <summary>
/// World announcements the server owns (events 78/79/80 over 0x4A) and the carriers of the ones the
/// stock client raises itself (55 OnLeave from 0xA0, 56 OnReconnect from 0x10).
/// </summary>
public static partial class P1NetworkChecks
{
    private static (MatchRuntime Runtime, DuelSession Session) StartedThreeSeatMatch(int mapId = GameMaps.Normal)
    {
        var paths = GameDataPaths.Default.ForMap(mapId);
        var runtime = mapId == GameMaps.Normal
            ? MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0,
                roster: [new(1, 4, "Order", "Annie", Team.Order),
                         new(2, 5, "Chaos", "Orianna", Team.Chaos),
                         new(3, 6, "Ally", "LeeSin", Team.Order)])
            : MatchRuntime.CreateProduction(NavigationGrid.Load(paths), new GameData(paths), readySettleSeconds: 0.0, mapId: mapId,
                roster: [new(1, 4, "Order", "Annie", Team.Order),
                         new(2, 5, "Chaos", "Orianna", Team.Chaos),
                         new(3, 6, "Ally", "LeeSin", Team.Order)]);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        foreach (var id in new[] { 1L, 2L, 3L })
        {
            session.Authenticate(id, session.BuildKeyCheck((ulong)id));
            session.Spawn(id);
            session.MarkReady(id);
        }
        return (runtime, session);
    }

    private static byte[] WorldEventPayload(SessionOutboundPacket packet, byte eventId)
    {
        var plaintext = packet.Packet.Plaintext.Span;
        return plaintext.Length > 5 && plaintext[0] == 0x4A && plaintext[5] == eventId
            ? plaintext.ToArray() : [];
    }

    [Check("session.world-announcement-minions-spawn")]
    public static void MinionsSpawnIsAnnouncedOnceAtTheFirstWave()
    {
        var (runtime, session) = StartedThreeSeatMatch();
        session.RunSchedulers();
        TestAssert.True(session.Started);
        runtime.Tick(1.25);
        TestAssert.True(session.RunSchedulers().Any(packet => packet.Packet.Plaintext.Span[0] == 0xC9),
            "the HUD clock sync arms the announcements");
        TestAssert.Equal(new byte[] { 79, 80, 78 }, session.Announcements.Select(item => item.EventId).ToArray(),
            "pending announcements in time order");
        TestAssert.Near(90.0, session.Announcements[2].HudTime, 1e-9, "OnMinionsSpawn is due with the first wave");

        // (event id, connection, HUD time when it first reached that connection)
        var sent = new List<(byte Event, long Connection, double Hud, byte[] Payload)>();
        void Step(double seconds)
        {
            runtime.Tick(seconds);
            foreach (var packet in session.RunSchedulers())
            {
                var plaintext = packet.Packet.Plaintext.Span;
                if (plaintext.Length > 5 && plaintext[0] == 0x4A)
                    sent.Add((plaintext[5], packet.ConnectionId, session.HudTime!.Value, plaintext.ToArray()));
            }
        }

        Step(88.0 - session.HudTime!.Value);
        TestAssert.Equal(new byte[] { 79, 80 }, sent.Select(item => item.Event).Distinct().OrderBy(id => id).ToArray(),
            "only the two opening lines are due before the first wave");
        while (session.HudTime!.Value < 91.0) Step(1.0 / 30.0);
        Step(5.0);

        var minions = sent.Where(item => item.Event == 78).ToArray();
        TestAssert.Equal(3, minions.Length, "exactly one OnMinionsSpawn per ready seat, never repeated");
        TestAssert.Equal(new long[] { 1, 2, 3 }, minions.Select(item => item.Connection).OrderBy(id => id).ToArray());
        foreach (var item in minions)
        {
            TestAssert.True(item.Hud >= 90.0 - 1e-9 && item.Hud < 90.2,
                $"OnMinionsSpawn reaches the HUD at the first wave, not at {item.Hud}");
            // 0x4A, zero header id, event 78, source 0, other 0, map 1 (the 79/80 template).
            TestAssert.Equal(Convert.ToHexString([0x4A, 0, 0, 0, 0, 78, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0]),
                Convert.ToHexString(item.Payload), "event 78 payload");
        }
        TestAssert.Equal(3, sent.Count(item => item.Event == 79), "welcome is still sent once per seat");
        TestAssert.Equal(3, sent.Count(item => item.Event == 80), "30 seconds warning is still sent once per seat");
        TestAssert.Equal(new byte[] { 78, 79, 80 }, sent.Select(item => item.Event).Distinct().OrderBy(id => id).ToArray(),
            "the session sends no other world event");
    }

    [Check("session.world-announcement-start-carries-the-match-map-number")]
    public static void TheWelcomeNamesTheMatchMapNotSummonersRift()
    {
        // The client's @MapNumber@ picks the welcome text and voice line (Announcements_CLASSIC.ini:529, 537). The Harrowing rift (map 2)
        // must send 2 in the 79 packet, which is the field the server used to fix at 1. Summoner's Rift keeps 1 (the check above).
        var (runtime, session) = StartedThreeSeatMatch(GameMaps.Harrowing);
        session.RunSchedulers();
        runtime.Tick(1.25);
        session.RunSchedulers();
        byte[]? welcome = null;
        while (session.HudTime!.Value < 31.0)
        {
            runtime.Tick(1.0 / 30.0);
            foreach (var packet in session.RunSchedulers())
            {
                var payload = WorldEventPayload(packet, 79);
                welcome ??= payload.Length == 0 ? null : payload;
            }
        }
        TestAssert.True(welcome is not null, "the welcome (event 79) is sent at HUD 30 s");
        // 0x4A, zero header id, event 79, source 0, other 0, then the i32 map number at offset 14.
        TestAssert.Equal(GameMaps.Harrowing, BitConverter.ToInt32(welcome!, 14), "the welcome carries the match's map id");
    }

    [Check("session.world-announcement-presence-carriers")]
    public static void LeaveAndReconnectUseTheClientRaisedCarriers()
    {
        // Pre-start: nothing to announce.
        var (_, session) = StartedThreeSeatMatch();
        TestAssert.Equal(0, session.BuildExitNotice(1).Count, "no exit notice before the match starts");
        session.RunSchedulers();
        TestAssert.True(session.Started);
        var clientId = session.Participant(1)!.ClientId;
        byte[] Notice(byte opcode) => [opcode, 0, 0, 0, 0, .. BitConverter.GetBytes(clientId)];

        // OnLeave: Exit (0xA0) naming the seat's client id, to every other ready seat only.
        var exit = session.BuildExitNotice(1);
        TestAssert.Equal(new long[] { 2, 3 }, exit.Select(packet => packet.ConnectionId).OrderBy(id => id).ToArray(),
            "the leaving seat is not told about itself");
        foreach (var packet in exit)
        {
            TestAssert.Equal(Convert.ToHexString(Notice(0xA0)),
                Convert.ToHexString(packet.Packet.Plaintext.Span), "Exit carries the client id");
            TestAssert.Equal(LeagueProtocolConstants.VariantChannel, packet.Packet.Channel);
            TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Packet.Reliability);
        }

        // OnReconnect: Reconnect (0x10) naming the seat, never to the returning (still loading) client.
        session.Disconnect(1);
        var returning = session.AuthenticateReconnect(7, session.BuildKeyCheck(1))!;
        TestAssert.False(returning.Ready, "a returning client is still loading");
        var reconnect = session.BuildReconnectNotice(returning);
        TestAssert.Equal(new long[] { 2, 3 }, reconnect.Select(packet => packet.ConnectionId).OrderBy(id => id).ToArray(),
            "only the clients already in game see the return");
        foreach (var packet in reconnect)
            TestAssert.Equal(Convert.ToHexString(Notice(0x10)),
                Convert.ToHexString(packet.Packet.Plaintext.Span), "Reconnect carries the client id");

        // A seat that is itself loading (or detached) gets no banner it cannot render.
        session.Disconnect(3);
        TestAssert.Equal(0, session.BuildExitNotice(2).Count, "no ready seat remains to be told");

        // The client raises events 55 and 56 itself; a 0x4A for them would show every banner twice.
        foreach (var packet in exit.Concat(reconnect))
            TestAssert.False(packet.Packet.Plaintext.Span[0] == 0x4A, "no world event for the client-raised ids");
        TestAssert.Equal("PlayerExited", WirePacketDecoder.LogName(0xA0, LeagueProtocolConstants.VariantChannel));
        TestAssert.Equal("ReconnectAck", WirePacketDecoder.LogName(0x10, LeagueProtocolConstants.VariantChannel));
    }

    [Check("protocol.presence-notice-layouts")]
    public static void PresenceNoticesAreNineByteClientIdPackets()
    {
        foreach (var (packet, opcode) in new[] { (LoadingPackets.PlayerReconnected(0), (byte)0x10),
                     (LoadingPackets.PlayerExited(0xFFFF_FFFFu), (byte)0xA0) })
        {
            var plaintext = packet.Plaintext.Span;
            TestAssert.Equal(9, plaintext.Length, $"0x{opcode:X2} length (5-byte header + client id)");
            TestAssert.Equal(opcode, plaintext[0]);
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(plaintext[1..]), "no unit header");
            TestAssert.Equal(LeagueProtocolConstants.VariantChannel, packet.Channel);
            TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
            TestAssert.Equal(0, packet.EntityReferences.Count, "client ids are not net ids");
        }
        // Client id zero is a real seat (first roster slot), unlike the nonzero-only player ids.
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(LoadingPackets.PlayerReconnected(0).Plaintext.Span[5..]));
        TestAssert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(LoadingPackets.PlayerExited(uint.MaxValue).Plaintext.Span[5..]));
    }

    [Check("network.presence-notices-reach-other-clients", Serial = true)]
    public static void ExitAndReconnectAreAnnouncedToTheOtherClient()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0,
            roster: [new(1, 4, "Leaver", "Annie", Team.Order),
                     new(2, 5, "Watcher", "Orianna", Team.Chaos)]);
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 3, 8, bindHost: "127.0.0.1"), ownsHost: true);
        using var leaverHost = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var watcherHost = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var returnHost = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        runtime.World.Clock.SetVirtual(0.0);
        var leaver = runtime.Session.Participants[0];
        var watcherEvents = new List<LeagueEnetEvent>();
        var returnEvents = new List<LeagueEnetEvent>();

        void Wait(Func<bool> done, params LeagueEnetHost[] hosts)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!done())
            {
                network.Pump(1, 32);
                foreach (var host in hosts)
                    Drain(host, ReferenceEquals(host, watcherHost) ? watcherEvents
                        : ReferenceEquals(host, returnHost) ? returnEvents : null);
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("presence notice check timed out");
            }
        }
        bool Saw(List<LeagueEnetEvent> events, byte opcode) => events.Any(item =>
            DecodeReconnectPayload(runtime.Cipher, item) is { Length: >= 9 } payload && payload[0] == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(5)) == leaver.ClientId);

        var leaverPeer = leaverHost.Connect("127.0.0.1", network.Port, 8);
        Wait(() => network.ConnectionIds.Count == 1, leaverHost);
        var watcherPeer = watcherHost.Connect("127.0.0.1", network.Port, 8);
        Wait(() => network.ConnectionIds.Count == 2, leaverHost, watcherHost);
        leaverPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        watcherPeer.Send(0, runtime.Session.BuildKeyCheck(2), LeagueEnetPacketFlags.Reliable);
        leaverHost.Flush();
        watcherHost.Flush();
        Wait(() => runtime.Session.Participants.All(item => item.ConnectionId is not null), leaverHost, watcherHost);
        var leaverConnection = leaver.ConnectionId!.Value;
        var watcherConnection = runtime.Session.Participants[1].ConnectionId!.Value;
        foreach (var connection in new[] { leaverConnection, watcherConnection })
        {
            runtime.Session.Spawn(connection);
            runtime.Session.MarkReady(connection);
        }
        foreach (var connection in new[] { leaverConnection, watcherConnection })
            runtime.Session.BuildMapReadyPackets(connection);
        TestAssert.True(runtime.Session.Start());
        watcherEvents.Clear();

        // C2S_Exit (0x94): the other client is told once; nothing is sent as a 0x4A world event.
        leaverPeer.Send(1, new byte[] { 0x94, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        leaverHost.Flush();
        Wait(() => leaver.ConnectionId is null && Saw(watcherEvents, 0xA0), leaverHost, watcherHost);
        TestAssert.Equal(1, watcherEvents.Count(item => Saw([item], 0xA0)), "one exit notice");
        TestAssert.False(watcherEvents.Any(item => HasPlainOpcode(runtime.Cipher, item, 0x4A)),
            "OnLeave is raised by the client from 0xA0, not by a world event");

        // The returning client is ready: the watcher sees the return, the returning client does not.
        var returnPeer = returnHost.Connect("127.0.0.1", network.Port, 8);
        Wait(() => network.ConnectionIds.Count == 2, watcherHost, returnHost);
        returnPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        returnHost.Flush();
        Wait(() => leaver.ConnectionId is not null, watcherHost, returnHost);
        returnPeer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
        returnPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        returnHost.Flush();
        Wait(() => leaver.Loaded, watcherHost, returnHost);
        Wait(() => leaver.Spawned, watcherHost, returnHost);
        returnPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        returnHost.Flush();
        Wait(() => leaver.Ready && Saw(watcherEvents, 0x10), watcherHost, returnHost);
        for (var i = 0; i < 20; i++)
        {
            network.Pump(1, 32);
            Drain(watcherHost, watcherEvents);
            Drain(returnHost, returnEvents);
        }
        TestAssert.Equal(1, watcherEvents.Count(item => Saw([item], 0x10)), "one reconnect notice");
        TestAssert.False(returnEvents.Any(item => Saw([item], 0x10)),
            "a restarting client has no HUD for the stock reconnect handler");
        TestAssert.False(watcherEvents.Concat(returnEvents).Any(item => HasPlainOpcode(runtime.Cipher, item, 0x4A)),
            "OnReconnect is raised by the client from 0x10, not by a world event");
    }
}
