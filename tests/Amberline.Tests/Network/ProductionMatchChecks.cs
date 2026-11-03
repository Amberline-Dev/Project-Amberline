using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Server;
using Amberline.Simulation;
using System.Buffers.Binary;
using System.Text.Json;

namespace Amberline.Tests;

public static class ProductionMatchChecks
{
    [Check("server.production-compose")]
    public static void EphemeralTwoSeatLoadingAndInput()
    {
        var logRoot = Path.Combine(Path.GetTempPath(),
            "amberline-native-log-" + Guid.NewGuid().ToString("N"));
        using var logger = ServerLogger.Open(logRoot, new StringWriter());
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, "Annie", "Orianna", sessionLog: logger);
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 2, 8,
                maximumPacketSize: LeagueEnetHost.ProductionMaximumPacketSize,
                maximumWaitingData: LeagueEnetHost.ProductionMaximumWaitingData, bindHost: "127.0.0.1"),
            ownsHost: true, log: logger);
        using var clientA = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var clientB = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        TestAssert.True(network.Port != 0 && network.Port != 5119);
        // Keep the world clock on a distinct nonzero origin.  Combat owns its
        // own simulation clock, so the later HeroStats snapshot can prove it
        // is using combat time rather than the loading/world clock.
        runtime.World.Clock.SetVirtual(100.0);

        // Loading packets use channel 6; the client handshake must negotiate
        // the full channel range before the first key-check response.
        var peerA = clientA.Connect("127.0.0.1", network.Port, 8);
        var clientAConnected = false;
        var clientBConnected = false;
        var connectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!clientAConnected || network.ConnectionIds.Count != 1)
        {
            network.Pump(1, 32, flushDelivery: false);
            clientAConnected |= clientA.Service(1).Type == LeagueEnetEventType.Connect;
            if (DateTime.UtcNow >= connectDeadline)
                throw new InvalidOperationException("native production peers did not connect");
        }
        var peerB = clientB.Connect("127.0.0.1", network.Port, 8);
        connectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!clientBConnected || network.ConnectionIds.Count != 2)
        {
            network.Pump(1, 32, flushDelivery: false);
            clientBConnected |= clientB.Service(1).Type == LeagueEnetEventType.Connect;
            if (DateTime.UtcNow >= connectDeadline)
                throw new InvalidOperationException("native production peers did not connect");
        }

        var serverConnectionA = network.ConnectionIds[0];
        var serverConnectionB = network.ConnectionIds[1];
        var eventsA = new List<LeagueEnetEvent>();
        var eventsB = new List<LeagueEnetEvent>();

        peerA.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        peerB.Send(0, runtime.Session.BuildKeyCheck(2), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        clientB.Flush();
        PumpUntil(network, clientA, clientB, () =>
            network.Runtime.Session.Participant(serverConnectionA)?.ConnectionId is not null &&
            network.Runtime.Session.Participant(serverConnectionB)?.ConnectionId is not null,
            peerA, peerB, eventsA, eventsB);

        // Follow the loading boundary over native ENet.  Channel 6 requests
        // the roster burst, C5 schedules the source's delayed spawn, and the
        // Native world/UI-ready C6 completes each client's loading state.
        peerA.Send(6, new byte[] { 1 }, LeagueEnetPacketFlags.Reliable);
        peerB.Send(6, new byte[] { 1 }, LeagueEnetPacketFlags.Reliable);
        peerA.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
        peerB.Send(1, new byte[] { 0xC7 }, LeagueEnetPacketFlags.Reliable);
        // The hero burst waits for the client's own world-ready frame, which follows its map load.
        peerA.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        peerB.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        clientB.Flush();
        PumpUntil(network, clientA, clientB, () =>
            runtime.Session.Participant(serverConnectionA) is { Loaded: true } &&
            runtime.Session.Participant(serverConnectionB) is { Loaded: true },
            peerA, peerB, eventsA, eventsB);
        TestAssert.True(eventsA.Any(item => item.Type == LeagueEnetEventType.Receive && item.Channel == 6),
            "seat 1 must receive native loading packets (" + string.Join(',', eventsA.Select(item => item.Type + ":" + item.Channel)) + ")");
        TestAssert.True(eventsB.Any(item => item.Type == LeagueEnetEventType.Receive && item.Channel == 6),
            "seat 2 must receive native loading packets (" + string.Join(',', eventsB.Select(item => item.Type + ":" + item.Channel)) + ")");

        runtime.World.Clock.SetVirtual(103.1);
        PumpUntil(network, clientA, clientB, () =>
            runtime.Session.Participant(serverConnectionA)?.Spawned == true &&
            runtime.Session.Participant(serverConnectionB)?.Spawned == true,
            peerA, peerB, eventsA, eventsB);
        peerA.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        peerB.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        clientB.Flush();
        // The seats become ready on the pass after their burst; do it before the clock moves on.
        PumpUntil(network, clientA, clientB, () =>
            runtime.Session.Participant(serverConnectionA) is { Loaded: true, Ready: true } &&
            runtime.Session.Participant(serverConnectionB) is { Loaded: true, Ready: true },
            peerA, peerB, eventsA, eventsB);

        runtime.World.Clock.SetVirtual(105.2);
        PumpUntil(network, clientA, clientB, () => runtime.Session.Started,
            peerA, peerB, eventsA, eventsB);
        TestAssert.True(eventsA.Any(item => item.Type == LeagueEnetEventType.Receive && item.Channel == 3),
            "seat 1 must receive native version/map output");

        // Advance only the started simulation clock before the first private
        // collection.  The world clock is already on a distinct nonzero
        // origin, while the combat clock remains match-relative.
        runtime.World.Clock.SetVirtual(105.2);
        network.Tick(2.0);
        Drain(clientA, eventsA);
        Drain(clientB, eventsB);
        TestAssert.True(runtime.Combat.CurrentTime > 0.0,
            "the production combat clock must be nonzero before the snapshot assertion");
        TestAssert.True(runtime.World.Clock.Now() > runtime.Combat.CurrentTime + 100.0,
            "the world and combat clocks must retain distinct origins");

        // A real owner request first queues the ordinary effective 0xCC
        // refresh and then receives its full Classic 0x4B plus one narrow
        // public projection for the other known row. The second seat may
        // receive its normal visible-hero 0xCC, but never receives this
        // request's private collection.
        eventsA.Clear();
        eventsB.Clear();
        // The client's replication store sends a 0xCC block only if a field differs from its last copy, and nothing has
        // changed since the last hero stats block. One new value (gold, which the owner receives) lets the refresh through.
        runtime.Session.Participant(serverConnectionA)!.Hero!.Gold += 1.0;
        var ownerStatsRequest = new byte[5];
        ownerStatsRequest[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(ownerStatsRequest.AsSpan(1),
            runtime.Session.Participant(serverConnectionA)!.Hero!.NetId);
        peerA.Send(1, ownerStatsRequest, LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        var expectedClassicRows = runtime.Session.Participants.Count(participant => participant.Hero is not null);
        PumpUntil(network, clientA, clientB,
            () => eventsA.Count(item => WireOpcode(item, runtime.Cipher) == 0x4B) >= expectedClassicRows,
            peerA, peerB, eventsA, eventsB);
        var ownerHero = runtime.Session.Participant(serverConnectionA)!.Hero!;
        var otherHero = runtime.Session.Participant(serverConnectionB)!.Hero!;
        var initialClassicEvent = eventsA.Last(item =>
            WireOpcode(item, runtime.Cipher) == 0x4B &&
            ClassicHeroNetId(item, runtime.Cipher) == ownerHero.NetId);
        var initialTimes = DecodeClassicTimes(initialClassicEvent, runtime.Cipher);
        TestAssert.Near(runtime.Combat.CurrentTime, initialTimes.TimePlayed, 1e-6,
            "TIME_PLAYED must use the match-relative combat clock");
        TestAssert.Equal(0.0, initialTimes.TimeSpentDisconnected,
            "a connected owner starts with zero disconnected time");
        var ownerOpcodes = eventsA
            .Where(item => item.Type == LeagueEnetEventType.Receive && item.Channel == 3)
            .Select(item => WireOpcode(item, runtime.Cipher))
            .Where(opcode => opcode is not null)
            .Select(opcode => opcode!.Value)
            .ToArray();
        var otherOpcodes = eventsB
            .Where(item => item.Type == LeagueEnetEventType.Receive && item.Channel == 3)
            .Select(item => WireOpcode(item, runtime.Cipher))
            .Where(opcode => opcode is not null)
            .Select(opcode => opcode!.Value)
            .ToArray();
        var ownerClassicEvents = eventsA.Where(item => WireOpcode(item, runtime.Cipher) == 0x4B).ToArray();
        TestAssert.Equal(2, ownerClassicEvents.Length,
            "owner receives its full row and one public foreign row for 0x59");
        TestAssert.Equal(1, ownerClassicEvents.Count(item =>
            ClassicHeroNetId(item, runtime.Cipher) == ownerHero.NetId),
            "owner receives one full absolute Classic snapshot for 0x59");
        TestAssert.Equal(1, ownerClassicEvents.Count(item =>
            ClassicHeroNetId(item, runtime.Cipher) == otherHero.NetId),
            "owner receives one public foreign Classic projection for 0x59");
        AssertPublicClassic(ownerClassicEvents.Single(item =>
            ClassicHeroNetId(item, runtime.Cipher) == otherHero.NetId), runtime.Cipher,
            otherHero, "0x59 foreign projection");
        TestAssert.Equal(0, otherOpcodes.Count(opcode => opcode == 0x4B),
            "other seat receives no snapshot from the first seat's request");
        var effectiveIndex = Array.IndexOf(ownerOpcodes, (byte)0xCC);
        var classicIndex = Array.IndexOf(ownerOpcodes, (byte)0x4B);
        TestAssert.True(effectiveIndex >= 0 && effectiveIndex < classicIndex,
            "effective 0xCC precedes the owner and public absolute 0x4B rows");

        // Send a second-seat request as an ordered sentinel.  Drain a few
        // additional pump turns after its own snapshot so a late, wrongly
        // routed absolute collection cannot evade the negative assertion.
        eventsA.Clear();
        eventsB.Clear();
        var otherStatsRequest = new byte[5];
        otherStatsRequest[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(otherStatsRequest.AsSpan(1),
            runtime.Session.Participant(serverConnectionB)!.Hero!.NetId);
        peerB.Send(1, otherStatsRequest, LeagueEnetPacketFlags.Reliable);
        clientB.Flush();
        PumpUntil(network, clientA, clientB,
            () => eventsB.Count(item => WireOpcode(item, runtime.Cipher) == 0x4B) >= expectedClassicRows,
            peerA, peerB, eventsA, eventsB);
        for (var turn = 0; turn < 3; turn++)
        {
            network.Pump(1, 32, flushDelivery: false);
            Drain(clientA, eventsA);
            Drain(clientB, eventsB);
        }
        TestAssert.Equal(0, eventsA.Count(item => WireOpcode(item, runtime.Cipher) == 0x4B),
            "the second-seat sentinel must not produce an absolute snapshot on seat 1");
        var secondRequestClassic = eventsB.Where(item => WireOpcode(item, runtime.Cipher) == 0x4B).ToArray();
        TestAssert.Equal(2, secondRequestClassic.Length,
            "the second-seat sentinel must produce its owner and public foreign rows");
        TestAssert.Equal(1, secondRequestClassic.Count(item =>
            ClassicHeroNetId(item, runtime.Cipher) == otherHero.NetId),
            "the second-seat sentinel includes one owner snapshot");
        TestAssert.Equal(1, secondRequestClassic.Count(item =>
            ClassicHeroNetId(item, runtime.Cipher) == ownerHero.NetId),
            "the second-seat sentinel includes one public foreign projection");

        // The read-only request remains valid during a native pause. Its
        // snapshot uses the frozen combat clock, then the test resumes before
        // exercising ordinary gameplay below.
        eventsA.Clear();
        eventsB.Clear();
        var combatAtPause = runtime.Combat.CurrentTime;
        var worldAtPause = runtime.World.Clock.Now();
        peerA.Send(1, NativeRequestWire.Encrypt(runtime.Cipher,
                PausePackets.Pause(0, 1800).Plaintext.Span),
            LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB, () => runtime.Session.Paused,
            peerA, peerB, eventsA, eventsB);
        eventsA.Clear();
        eventsB.Clear();
        runtime.World.Clock.SetVirtual(worldAtPause + 25.0);
        network.Pump(1, 32, flushDelivery: false);
        Drain(clientA, eventsA);
        Drain(clientB, eventsB);
        TestAssert.Equal(combatAtPause, runtime.Combat.CurrentTime,
            "advancing the paused world clock must not advance combat time");
        TestAssert.True(runtime.World.Clock.Now() > worldAtPause,
            "the paused world clock must continue to advance for session timers");
        peerA.Send(1, ownerStatsRequest, LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB,
            () => eventsA.Count(item => WireOpcode(item, runtime.Cipher) == 0x4B) >= expectedClassicRows,
            peerA, peerB, eventsA, eventsB);
        var pausedClassicEvent = eventsA.Last(item =>
            WireOpcode(item, runtime.Cipher) == 0x4B &&
            ClassicHeroNetId(item, runtime.Cipher) == ownerHero.NetId);
        var pausedTimes = DecodeClassicTimes(pausedClassicEvent, runtime.Cipher);
        TestAssert.Near(initialTimes.TimePlayed, pausedTimes.TimePlayed, 1e-6,
            "paused refresh must retain simulation-relative TIME_PLAYED");
        TestAssert.Equal(initialTimes.TimeSpentDisconnected, pausedTimes.TimeSpentDisconnected,
            "paused refresh must retain TIME_SPENT_DISCONNECTED");
        TestAssert.Equal(2, eventsA.Count(item => WireOpcode(item, runtime.Cipher) == 0x4B),
            "paused owner can still refresh its full and public Classic rows");
        TestAssert.Equal(0, eventsB.Count(item => WireOpcode(item, runtime.Cipher) == 0x4B),
            "paused owner refresh remains seat-local");
        peerA.Send(1, NativeRequestWire.Encrypt(runtime.Cipher,
                PausePackets.Resume(uint.MaxValue, delayed: false).Plaintext.Span),
            LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        network.Pump(1, 32, flushDelivery: false);
        Drain(clientA, eventsA);
        Drain(clientB, eventsB);
        runtime.World.Clock.SetVirtual(runtime.World.Clock.Now() + 5.1);
        PumpUntil(network, clientA, clientB, () => !runtime.Session.Paused,
            peerA, peerB, eventsA, eventsB);

        var hero = runtime.Session.Participant(serverConnectionA)!.Hero!;
        var validPacket = ClientGameplayPackets.MoveOrder(hero.NetId, 2,
            new Position2(hero.Position.X + 100.0, hero.Position.Z + 100.0));
        var validWire = validPacket.ToWireBytes(runtime.Cipher);
        peerA.Send(1, validWire, LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB, () => hero.Waypoints.Count > 0,
            peerA, peerB, eventsA, eventsB);
        var acceptedDestination = hero.Waypoints[^1];

        var invalidPacket = ClientGameplayPackets.MoveOrder(
            runtime.Session.Participant(serverConnectionB)!.Hero!.NetId, 2,
            new Position2(hero.Position.X + 200.0, hero.Position.Z + 200.0));
        peerA.Send(1, invalidPacket.ToWireBytes(runtime.Cipher), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        var victim = runtime.Session.Participant(serverConnectionB)!.Hero!;
        var victimGold = victim.Gold;
        // A same-channel accepted sentinel drains the ordered reliable input
        // stream after the rejected actor packet, so the assertion observes
        // processing rather than a pre-arrival snapshot.
        var sentinelDestination = new Position2(hero.Position.X + 120.0,
            hero.Position.Z + 120.0);
        var sentinel = ClientGameplayPackets.MoveOrder(hero.NetId, 2,
            sentinelDestination);
        peerA.Send(1, sentinel.ToWireBytes(runtime.Cipher), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB,
            () => hero.Waypoints.Count > 0 && hero.Waypoints[^1] != acceptedDestination,
            peerA, peerB, eventsA, eventsB);
        var wireDestination = Geometry.ClientWaypoints([sentinelDestination], new NavigationBounds(
            runtime.Navigation.MinX, runtime.Navigation.MaxX, runtime.Navigation.MinZ, runtime.Navigation.MaxZ))[0];
        TestAssert.Equal(wireDestination, hero.Waypoints[^1],
            "wrong-seat actor input must be rejected by the session boundary");
        TestAssert.Equal(victimGold, victim.Gold,
            "wrong-seat actor input must not mutate the victim");

        var beforeCheatGold = hero.Gold;
        var cheat = new byte[16];
        cheat[0] = 0xB6;
        cheat[5] = 1; // ADD_GOLD
        BinaryPrimitives.WriteUInt32LittleEndian(cheat.AsSpan(8, 4), hero.NetId);
        BinaryPrimitives.WriteInt32LittleEndian(cheat.AsSpan(12, 4),
            BitConverter.SingleToInt32Bits(25.0f));
        peerA.Send(1, runtime.Cipher.Encrypt(cheat), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB, () => hero.Gold >= beforeCheatGold + 25.0,
            peerA, peerB, eventsA, eventsB);

        var invalidCheat = (byte[])cheat.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidCheat.AsSpan(8, 4),
            victim.NetId);
        peerA.Send(1, runtime.Cipher.Encrypt(invalidCheat), LeagueEnetPacketFlags.Reliable);
        var validSentinelCheat = (byte[])cheat.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(validSentinelCheat.AsSpan(12, 4),
            BitConverter.SingleToInt32Bits(1.0f));
        peerA.Send(1, runtime.Cipher.Encrypt(validSentinelCheat), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB, () => hero.Gold >= beforeCheatGold + 26.0,
            peerA, peerB, eventsA, eventsB);
        TestAssert.Equal(victimGold, victim.Gold,
            "cheat target ownership must be enforced at the session boundary");

        var beforeChatGold = hero.Gold;
        var chatCommand = ChatPackets.Envelope(0, false,
            System.Text.Encoding.ASCII.GetBytes("/gold 3"));
        peerA.Send(5, chatCommand.ToWireBytes(runtime.Cipher),
            LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        PumpUntil(network, clientA, clientB, () => hero.Gold >= beforeChatGold + 3.0,
            peerA, peerB, eventsA, eventsB);

        // Exercise the real post-start simulation path through the same
        // native network shell.  This is intentionally a fixed source tick,
        // rather than a direct Session/Spawn call, so the check covers the
        // movement accumulator, combat clock, delivery queue, and ENet flush.
        var combatBeforeTick = runtime.Combat.CurrentTime;
        var worldBeforeTick = runtime.World.Clock.Now();
        var receiveCountBeforeTick = eventsA.Count(item => item.Type == LeagueEnetEventType.Receive);
        var positionBeforeTick = hero.Position;
        hero.SpellCooldowns[0] = 0.2;
        hero.ItemCooldowns[0] = 0.2;
        network.Tick(0.1);
        Drain(clientA, eventsA);
        Drain(clientB, eventsB);
        TestAssert.True(runtime.Combat.CurrentTime > combatBeforeTick,
            "started production network must advance the authoritative combat clock");
        TestAssert.True(runtime.World.Clock.Now() > worldBeforeTick,
            "started production network must advance the virtual world clock");
        TestAssert.True(eventsA.Count(item => item.Type == LeagueEnetEventType.Receive) > receiveCountBeforeTick,
            "a real simulation tick must produce new native output (not reuse loading packets)");
        TestAssert.True(hero.Position != positionBeforeTick,
            "the native simulation tick must advance an accepted hero route");
        TestAssert.True(hero.SpellCooldowns[0] < 0.2 && hero.ItemCooldowns[0] < 0.2,
            "the oversized 0.1-second tick must advance spell and item cooldowns");
        TestAssert.Near(0.1, runtime.Combat.CurrentTime - combatBeforeTick, 1e-9,
            "the 0.1-second input must be split into fixed 1/30 simulation steps without time loss");

        // Verify the same native run produced the source-compatible records
        // needed by replay and telemetry, rather than only lifecycle logs.
        logger.Flush();
        // ServerLogger owns the append handle with exclusive sharing on
        // Windows; close it before reading the completed native capture.
        logger.Dispose();
        var sessionDirectory = Directory.GetDirectories(logRoot).Single();
        var records = File.ReadAllLines(Path.Combine(sessionDirectory, "session.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            var receive = records.FirstOrDefault(record =>
                record.RootElement.GetProperty("cat").GetString() == "PKT" &&
                record.RootElement.GetProperty("msg").GetString() == "<-");
            var send = records.FirstOrDefault(record =>
                record.RootElement.GetProperty("cat").GetString() == "PKT" &&
                record.RootElement.GetProperty("msg").GetString() == "->");
            TestAssert.True(receive is not null,
                "native input must be recorded as bounded PKT detail");
            TestAssert.True(send is not null,
                "native output must be recorded as a PKT send event");
            var inbound = receive!.RootElement;
            TestAssert.True(inbound.TryGetProperty("op", out _) &&
                inbound.TryGetProperty("ch", out _) &&
                inbound.TryGetProperty("n", out _) &&
                inbound.TryGetProperty("raw", out _) &&
                inbound.TryGetProperty("sampled", out _) &&
                inbound.TryGetProperty("truncated", out _) &&
                inbound.TryGetProperty("redacted", out _) &&
                inbound.TryGetProperty("partial_replay", out _),
                "PKT input records must preserve bounded metadata and output fields");
            var raw = inbound.GetProperty("raw").GetString() ?? string.Empty;
            TestAssert.True(raw.Length <= 64 && raw.Length % 2 == 0,
                "PKT input records must cap raw previews at 32 bytes");
            TestAssert.False(inbound.TryGetProperty("hex", out _),
                "retired NET recv records must not restore full encrypted payloads");
            TestAssert.Equal(false, inbound.GetProperty("redacted").GetBoolean(),
                "ordinary authenticated input is not credential material");
            TestAssert.True(inbound.GetProperty("partial_replay").GetBoolean(),
                "packet detail records must identify partial replay evidence");
            TestAssert.True(send!.RootElement.TryGetProperty("op", out _) &&
                send.RootElement.TryGetProperty("ch", out _) &&
                send.RootElement.TryGetProperty("rel", out _) &&
                send.RootElement.TryGetProperty("raw", out _),
                "PKT send records must preserve decoded opcode/channel/reliability/raw");
        }
        finally
        {
            foreach (var record in records) record.Dispose();
            logger.Dispose();
            try { Directory.Delete(logRoot, recursive: true); } catch { }
        }
    }

    [Check("server.production-classic-public-reconnect")]
    public static void PublicClassicReconnectUsesAuthoritativeForeignCounters()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Owner", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Foreign", "Orianna", Team.Chaos),
            new SessionParticipantDefinition(3, 2, "Omitted", "LeeSin", Team.Order),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        IReadOnlyList<PlaintextPacket>? initialOwnerSpawn = null;
        for (var index = 0; index < roster.Length; index++)
        {
            var connectionId = 101L + index;
            TestAssert.True(session.Authenticate(connectionId,
                session.BuildKeyCheck(roster[index].PlayerId)) is not null);
            var spawn = session.Spawn(connectionId);
            TestAssert.True(spawn.Count > 0);
            if (index == 0)
                initialOwnerSpawn = spawn;
            TestAssert.True(session.MarkReady(connectionId));
        }
        session.RunSchedulers();
        TestAssert.True(session.Started, "the mixed production roster starts before reconnect coverage");

        var owner = session.Participant(101)!;
        var foreign = session.Participant(102)!;
        var omitted = session.Participant(103)!;
        var foreignHero = foreign.Hero!;
        var omittedHero = omitted.Hero!;
        TestAssert.False(owner.View.Visible.Contains(foreignHero.NetId),
            "the initial foreign row is hidden at the owner spawn boundary");
        var initialForeignRows = initialOwnerSpawn!
            .Where(packet => packet.Plaintext.Length > 0 &&
                packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) ==
                    foreignHero.NetId)
            .ToArray();
        TestAssert.Equal(1, initialForeignRows.Length,
            "initial spawn includes one hidden foreign public Classic row");
        AssertPublicClassic(initialForeignRows[0], foreignHero,
            "initial hidden foreign public projection");
        TestAssert.True(owner.View.Known.Contains(foreignHero.NetId));
        TestAssert.True(owner.View.Known.Contains(omittedHero.NetId));

        // Change the foreign authoritative row only after the recipient is
        // detached. Seed private values as well, so a leaked full snapshot
        // cannot pass merely because the fixture happened to be all zeroes.
        TestAssert.True(session.Disconnect(101));
        foreignHero.ChampionKills = 4;
        foreignHero.ChampionDeaths = 2;
        foreignHero.ChampionAssists = 7;
        foreignHero.LaneMinionsKilled = 23;
        foreignHero.OtherMinionsKilled = 11;
        foreignHero.NeutralMinionsKilled = 5;
        foreignHero.Gold = 9999.0;
        foreignHero.Inventory = [new InventorySlot(3026, 1, 0), new InventorySlot(2003, 3, 0),
            null, null, null, null];
        foreignHero.MatchStatistics.RecordGoldEarned(1234.0);
        foreignHero.MatchStatistics.RecordDamageDealt(321.0, DamageType.Physical, critical: false);
        foreignHero.MatchStatistics.RecordCast(0);
        foreignHero.Position = new Position2(13000.0, 13000.0);

        var reconnecting = session.AuthenticateReconnect(201,
            session.BuildKeyCheck(owner.PlayerId));
        TestAssert.True(reconnecting is not null);
        var version = session.Receive(201, 1, [0xC5]);
        TestAssert.True(version.Any(output => output.Packet.Plaintext.Span[0] == 0x57),
            "reconnect receives the staged version handshake");
        session.Receive(201, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = reconnecting!.SpawnAt ??
            throw new InvalidOperationException("reconnect handshake did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        var baseline = session.RunSchedulers();
        TestAssert.False(baseline.Any(output => output.Packet.Plaintext.Span[0] == 0x4B),
            "reconnect baseline defers Classic rows until readiness");
        session.Receive(201, 1, [0xA4, 0, 0, 0, 0, 1]);
        session.Receive(201, 1, [0xC6, 0, 0, 0, 0]);
        session.RunSchedulers();
        TestAssert.True(reconnecting.Spawned && reconnecting.Loaded && reconnecting.Ready,
            "reconnect reaches the ready boundary before public stats restoration");
        TestAssert.True(reconnecting.View.Known.Contains(foreignHero.NetId));
        TestAssert.False(reconnecting.View.Visible.Contains(foreignHero.NetId),
            "the foreign row is restored while its position remains hidden");

        // The third roster hero is known during spawn, then explicitly removed
        // to prove the reconnect burst never bypasses recipient knowledge.
        reconnecting.View.Known.Remove(omittedHero.NetId);
        var restored = session.BuildReconnectMatchState(201)
            .Where(packet => packet.Plaintext.Length > 0 && packet.Plaintext.Span[0] == 0x4B)
            .ToArray();
        TestAssert.Equal(2, restored.Length,
            "reconnect restores owner and exactly one known foreign row");
        TestAssert.Equal(1, restored.Count(packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == owner.Hero!.NetId));
        var foreignPacket = restored.Single(packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == foreignHero.NetId);
        AssertPublicClassic(foreignPacket, foreignHero,
            "reconnect foreign public projection retains counters and native defaults");
        TestAssert.False(restored.Any(packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == omittedHero.NetId),
            "a removed known row is omitted from the reconnect burst");

        var refreshPayload = new byte[5];
        refreshPayload[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(refreshPayload.AsSpan(1), owner.Hero!.NetId);
        var refreshed = session.BuildClassicHeroStatsRefreshBurst(201, refreshPayload)
            .Where(packet => packet.Plaintext.Length > 0 &&
                packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode)
            .ToArray();
        TestAssert.Equal(2, refreshed.Length,
            "local 0x59 restores the owner and exactly one remaining known foreign row");
        TestAssert.True(refreshed.Any(packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == owner.Hero!.NetId),
            "local 0x59 retains the requesting owner row after Known removal");
        var refreshedForeign = refreshed.Single(packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == foreignHero.NetId);
        AssertPublicClassic(refreshedForeign, foreignHero,
            "local 0x59 foreign public projection after Known removal");
        TestAssert.False(refreshed.Any(packet =>
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == omittedHero.NetId),
            "local 0x59 omits the removed known row");
    }

    private static void PumpUntil(MatchRuntimeNetwork network, LeagueEnetHost first,
        LeagueEnetHost second, Func<bool> done, LeagueEnetPeer? firstPeer = null,
        LeagueEnetPeer? secondPeer = null, List<LeagueEnetEvent>? firstEvents = null,
        List<LeagueEnetEvent>? secondEvents = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done())
        {
            network.Pump(1, 32, flushDelivery: false);
            Drain(first, firstEvents);
            Drain(second, secondEvents);
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("ephemeral production match did not reach expected state");
        }
    }

    private static void Drain(LeagueEnetHost host, List<LeagueEnetEvent>? events)
    {
        while (true)
        {
            var @event = host.Service(0);
            if (@event.Type == LeagueEnetEventType.None)
                return;
            events?.Add(@event);
        }
    }

    private static byte? WireOpcode(LeagueEnetEvent @event, LegacyBlowfishCipher cipher)
    {
        if (@event.Type != LeagueEnetEventType.Receive || @event.Payload.Length == 0)
            return null;
        var plaintext = @event.Payload.Length >= LegacyBlowfishCipher.BlockSize &&
                        @event.Channel is 1 or 3 or 5
            ? cipher.Decrypt(@event.Payload)
            : @event.Payload;
        return plaintext.Length == 0 ? null : plaintext[0];
    }

    private static uint ClassicHeroNetId(LeagueEnetEvent @event,
        LegacyBlowfishCipher cipher)
    {
        if (WireOpcode(@event, cipher) != ClassicHeroStatsPackets.Opcode)
            throw new InvalidOperationException("event is not a Classic HeroStats packet");
        var plaintext = cipher.Decrypt(@event.Payload);
        return BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(1, 4));
    }

    private static void AssertPublicClassic(LeagueEnetEvent @event,
        LegacyBlowfishCipher cipher, Entity hero, string name)
    {
        if (WireOpcode(@event, cipher) != ClassicHeroStatsPackets.Opcode)
            throw new InvalidOperationException("event is not a Classic HeroStats packet");
        AssertPublicClassic(DecodeClassicEvent(@event, cipher), hero, name);
    }

    private static void AssertPublicClassic(PlaintextPacket packet,
        Entity hero, string name)
    {
        if (packet.Plaintext.Length == 0 || packet.Plaintext.Span[0] != ClassicHeroStatsPackets.Opcode)
            throw new InvalidOperationException("packet is not a Classic HeroStats packet");
        AssertPublicClassic(packet.Plaintext.Span, hero, name);
    }

    private static void AssertPublicClassic(ReadOnlySpan<byte> bytes,
        Entity hero, string name)
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var values = ClassicHeroStatsChecks.Decode(bytes, schema);
        AssertPublicClassic(values, hero, name);
    }

    private static void AssertPublicClassic(IReadOnlyDictionary<string, object> values,
        Entity hero, string name)
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        TestAssert.Equal(hero.ChampionKills, (int)values["CHAMPIONS_KILLED"], name + " kills");
        TestAssert.Equal(hero.ChampionDeaths, (int)values["NUM_DEATHS"], name + " deaths");
        TestAssert.Equal(hero.ChampionAssists, (int)values["ASSISTS"], name + " assists");
        TestAssert.Equal(hero.LaneMinionsKilled + hero.OtherMinionsKilled,
            (int)values["MINIONS_KILLED"], name + " lane/other CS");
        TestAssert.Equal(0, (int)values["NEUTRAL_MINIONS_KILLED"], name + " neutral collection default");
        foreach (var definition in schema.Definitions)
        {
            if (definition.Name is "CHAMPIONS_KILLED" or "NUM_DEATHS" or "ASSISTS" or "MINIONS_KILLED")
                continue;
            switch (definition.Kind)
            {
                case ClassicHeroStatKind.Int:
                    TestAssert.Equal(0, (int)values[definition.Name], name + " " + definition.Name);
                    break;
                case ClassicHeroStatKind.Float:
                    TestAssert.Equal(0.0, (double)values[definition.Name], name + " " + definition.Name);
                    break;
                case ClassicHeroStatKind.String:
                    TestAssert.Equal(string.Empty, (string)values[definition.Name], name + " " + definition.Name);
                    break;
                case ClassicHeroStatKind.LongLong:
                    TestAssert.Equal(0L, (long)values[definition.Name], name + " " + definition.Name);
                    break;
            }
        }
    }

    private static Dictionary<string, object> DecodeClassicEvent(
        LeagueEnetEvent @event, LegacyBlowfishCipher cipher)
    {
        if (@event.Type != LeagueEnetEventType.Receive || @event.Channel != 3)
            throw new InvalidOperationException("Classic HeroStats event must be a channel 3 receive");
        var bytes = cipher.Decrypt(@event.Payload);
        if (bytes.Length < 9 || bytes[0] != ClassicHeroStatsPackets.Opcode)
            throw new InvalidDataException("Classic HeroStats event has an invalid header");
        var declaredCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
        var declaredTotal = checked(5L + declaredCount);
        if (declaredTotal > bytes.Length)
            throw new InvalidDataException("Classic HeroStats event is shorter than its declared payload");
        var trailing = bytes.Length - (int)declaredTotal;
        if (trailing > LegacyBlowfishCipher.BlockSize - 1)
            throw new InvalidDataException("Classic HeroStats event has excessive cipher padding");
        for (var index = (int)declaredTotal; index < bytes.Length; index++)
            if (bytes[index] != 0)
                throw new InvalidDataException("Classic HeroStats event has nonzero cipher padding");
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        return ClassicHeroStatsChecks.Decode(bytes.AsSpan(0, (int)declaredTotal), schema);
    }

    private static (double TimePlayed, double TimeSpentDisconnected) DecodeClassicTimes(
        LeagueEnetEvent @event, LegacyBlowfishCipher cipher)
    {
        if (@event.Type != LeagueEnetEventType.Receive || @event.Channel != 3)
            throw new InvalidOperationException("Classic HeroStats event must be a channel 3 receive");
        var values = DecodeClassicEvent(@event, cipher);
        var timePlayed = (double)values["TIME_PLAYED"];
        var timeSpentDisconnected = (double)values["TIME_SPENT_DISCONNECTED"];
        TestAssert.True(double.IsFinite(timePlayed) && double.IsFinite(timeSpentDisconnected),
            "Classic HeroStats snapshot must contain both clock fields");
        return (timePlayed, timeSpentDisconnected);
    }
}
