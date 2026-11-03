using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation;

namespace Amberline.Tests.Network;

public static partial class P1NetworkChecks
{
    [Check("network.neutral-score-fog-routing")]
    public static void NeutralScoreUsesMapReplicationWithoutRevealingTheKill()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = CreateStartedFixture();
            var killer = runtime.Session.Participants.Single(seat => seat.Team == team).Hero!;
            var viewer = runtime.Session.Participants.Single(seat => seat.Team != team);
            var capture = new ScoreCapture();
            var observer = new ReplicationObserver(501, viewer.Team, viewer.Hero!.NetId, capture, view: viewer.View);
            runtime.Delivery.AddObserver(observer);
            runtime.Delivery.Flush();
            TestAssert.True(observer.View.Known.Contains(killer.NetId));
            TestAssert.False(observer.View.Visible.Contains(killer.NetId), "opposite fountain is in fog");
            capture.Packets.Clear();
            var monster = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                EntityKind.Neutral, UnitType.Monster, Team.Neutral, killer.Position)
                { Name = "YoungLizard", Health = 100, MaxHealth = 100 });
            TestAssert.True(runtime.Combat.Kill(monster, killer.NetId));
            runtime.Delivery.Flush();
            var scores = capture.Packets.Where(packet => packet.Plaintext.Length == 23 &&
                packet.Plaintext.Span[0] == 0xCC && packet.Plaintext.Span[10] == 8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]) == 0x200u).ToArray();
            TestAssert.Equal(1, scores.Length, "known enemy score reaches the observer exactly once");
            TestAssert.Equal(killer.NetId, BinaryPrimitives.ReadUInt32LittleEndian(scores[0].Plaintext.Span[11..]));
            TestAssert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(scores[0].Plaintext.Span[19..]));
            var decoded = Amberline.Network.Diagnostics.WirePacketDecoder.Decode(scores[0].Plaintext.Span, 3);
            var fields = (Dictionary<string, object?>)decoded.Fields["fields"]!;
            TestAssert.Equal(1u, Convert.ToUInt32(fields["mNumNeutralMinionsKilled"]));
            TestAssert.False(observer.View.Visible.Contains(killer.NetId));
            TestAssert.False(observer.View.Known.Contains(monster.NetId), "score creates no hidden monster");
        }
    }

    private sealed class ScoreCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    [Check("network.p1-map-ping-wire")]
    public static void MapPingWireRoundTrip()
    {
        var request = PingPackets.MapPingRequest(
            0x4000_0001,
            new Position3(1234.5, 60.0, 6789.25),
            0x4000_0002,
            MapPingCategory.Danger);
        TestAssert.Equal((byte)1, request.Channel);
        TestAssert.Equal(PingPackets.ClientLength, request.Plaintext.Length);
        TestAssert.True(PingPackets.TryReadRequest(request.Plaintext.Span, out var decoded));
        TestAssert.Equal(new MapPingRequest(
            0x4000_0001,
            new Position3(1234.5f, 60.0f, 6789.25f),
            0x4000_0002,
            MapPingCategory.Danger), decoded);

        var response = PingPackets.MapPingNotification(
            decoded.ActorNetId, decoded.Position, decoded.TargetNetId, decoded.Category);
        TestAssert.Equal(PingPackets.ServerLength, response.Plaintext.Length);
        TestAssert.True(PingPackets.TryReadNotification(response.Plaintext.Span, out var notification));
        TestAssert.Equal(decoded.Position, notification.Position);
        TestAssert.Equal(decoded.TargetNetId, notification.TargetNetId);
        TestAssert.Equal(decoded.Category, notification.Category);
        TestAssert.Equal(notification.SenderNetId, notification.SourceNetId);
        TestAssert.True(notification.PlayAudio && notification.ShowChat);

        var padded = response.Plaintext.ToArray().Concat(new byte[6]).ToArray();
        TestAssert.True(PingPackets.TryReadNotification(padded, out _),
            "transport padding must not change the decoded map-ping fields");
        padded[^1] = 1;
        TestAssert.False(PingPackets.TryReadNotification(padded, out _),
            "nonzero bytes after the native struct are malformed");

        var malformedLength = response.Plaintext.ToArray().Concat(new byte[1]).ToArray();
        TestAssert.False(PingPackets.TryReadNotification(malformedLength, out _),
            "only native length or legacy block padding is accepted");

        var badSource = response.Plaintext.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badSource.AsSpan(21, 4),
            notification.SenderNetId + 1);
        TestAssert.False(PingPackets.TryReadNotification(badSource, out _),
            "the S2C source field must agree with the authenticated sender");

        // Captured patch-126 native C2S packet (22 bytes): the five-byte
        // header has a zero net ID and the category uses only its low nibble.
        var nativeRequest = Convert.FromHexString(
            "5a00000000cb32d74580523842f108c6450000000000");
        TestAssert.True(PingPackets.TryReadRequest(nativeRequest, out var nativeDecoded));
        TestAssert.Equal(0u, nativeDecoded.ActorNetId,
            "the native sender leaves the C2S header net ID zero");
        var nativeWithStackBits = nativeRequest.ToArray();
        nativeWithStackBits[^1] = 0xF0;
        TestAssert.True(PingPackets.TryReadRequest(nativeWithStackBits, out _),
            "native stack bits above the category nibble are ignored");
    }

    [Check("network.p1-map-ping-routing")]
    public static void MapPingRoutingAndValidation()
    {
        var runtime = CreateStartedFixture(includeSecondOrderSeat: true);
        var session = runtime.Session;
        var order = session.Participants[0];
        var chaos = session.Participants[1];
        var secondOrder = session.Participants[2];
        var orderHero = order.Hero!;
        var chaosHero = chaos.Hero!;
        var position = new Position3(orderHero.Position.X, 60.0, orderHero.Position.Z);

        var orderPing = PingPackets.MapPingRequest(orderHero.NetId, position, 0,
            MapPingCategory.Offense);
        var orderOutput = session.Receive(11, 1,
            orderPing.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(2, orderOutput.Count, "both attached same-team seats receive the ping");
        var senderOutput = orderOutput.Single(item => item.ConnectionId == 11 &&
            item.Packet.Plaintext.Span[0] == PingPackets.ServerOpcode);
        TestAssert.True(PingPackets.TryReadNotification(senderOutput.Packet.Plaintext.Span,
            out var orderNotification));
        TestAssert.Equal(orderHero.NetId, orderNotification.SenderNetId);
        TestAssert.Equal(orderHero.NetId, orderNotification.SourceNetId);
        TestAssert.True(orderOutput.Any(item => item.ConnectionId == secondOrder.ConnectionId));

        var chaosPing = PingPackets.MapPingRequest(chaosHero.NetId,
            new Position3(chaosHero.Position.X, 60.0, chaosHero.Position.Z), 0,
            MapPingCategory.Danger);
        var chaosOutput = session.Receive(12, 1,
            chaosPing.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(1, chaosOutput.Count, "the chaos sender must receive its team ping");
        TestAssert.Equal(12L, chaosOutput[0].ConnectionId);

        var forged = orderPing.Plaintext.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(1, 4), chaosHero.NetId);
        TestAssert.Equal(0, session.Receive(11, 1,
            runtime.Cipher.Encrypt(forged)).Count,
            "the authenticated seat must supply the ping actor");

        var hiddenTarget = PingPackets.MapPingRequest(orderHero.NetId, position,
            chaosHero.NetId, MapPingCategory.Danger);
        TestAssert.Equal(0, session.Receive(11, 1,
            hiddenTarget.ToWireBytes(runtime.Cipher)).Count,
            "a hidden enemy target must not be disclosed to the team");

        order.View.Visible.Add(chaosHero.NetId);
        var visibleTarget = session.Receive(11, 1,
            hiddenTarget.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(2, visibleTarget.Count,
            "a target already visible to the sender may be relayed to both seats");
        var visibleSender = visibleTarget.Single(item => item.ConnectionId == 11 &&
            item.Packet.Plaintext.Span[0] == PingPackets.ServerOpcode);
        TestAssert.True(PingPackets.TryReadNotification(visibleSender.Packet.Plaintext.Span,
            out var visibleNotification));
        TestAssert.Equal(chaosHero.NetId, visibleNotification.TargetNetId);
        var filteredTeammate = visibleTarget.Single(item => item.ConnectionId == 13 &&
            item.Packet.Plaintext.Span[0] == PingPackets.ServerOpcode);
        TestAssert.True(PingPackets.TryReadNotification(filteredTeammate.Packet.Plaintext.Span,
            out var teammateNotification));
        TestAssert.Equal(0u, teammateNotification.TargetNetId,
            "an unseen target reference is stripped per recipient");
        orderHero.Alive = false;
        var deadPing = session.Receive(11, 1,
            orderPing.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(2, deadPing.Count,
            "a dead player may still send a map ping");
        orderHero.Alive = true;

        secondOrder.Ready = false;
        var loadingOutput = session.Receive(11, 1,
            orderPing.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(new[] { 11L }, loadingOutput.Select(item => item.ConnectionId).ToArray(),
            "a loading teammate does not receive gameplay pings");
        secondOrder.Ready = true;
        TestAssert.True(session.Disconnect(13));
        var disconnectedOutput = session.Receive(11, 1,
            orderPing.ToWireBytes(runtime.Cipher));
        TestAssert.Equal(new[] { 11L }, disconnectedOutput.Select(item => item.ConnectionId).ToArray(),
            "a disconnected teammate does not receive gameplay pings");

        var outside = PingPackets.MapPingRequest(orderHero.NetId,
            new Position3(session.MapBounds.MaxX + 1.0, 60.0, orderHero.Position.Z), 0,
            MapPingCategory.Offense);
        TestAssert.Equal(0, session.Receive(11, 1,
            outside.ToWireBytes(runtime.Cipher)).Count,
            "a ping outside navigation bounds must be rejected");

        var invalidCategory = orderPing.Plaintext.ToArray();
        invalidCategory[^1] = 2;
        TestAssert.Equal(0, session.Receive(11, 1,
            runtime.Cipher.Encrypt(invalidCategory)).Count,
            "patch-126 rejects categories outside offense/danger");
    }

    [Check("network.p1-map-ping-native-routing")]
    public static void CapturedNativePingRoutesThroughProductionSession()
    {
        var runtime = CreateStartedFixture();
        var nativeRequest = Convert.FromHexString(
            "5a00000000cb32d74580523842f108c6450000000000");
        TestAssert.True(PingPackets.TryReadRequest(nativeRequest, out var decoded));
        TestAssert.Equal(0u, decoded.ActorNetId);

        var output = runtime.Session.Receive(11, 1,
            runtime.Cipher.Encrypt(nativeRequest));
        TestAssert.Equal(1, output.Count,
            "a captured zero-header native ping routes one notification to the sender team");
        var notificationPacket = output.Single(item => item.Packet.Plaintext.Span[0] == PingPackets.ServerOpcode);
        TestAssert.True(PingPackets.TryReadNotification(notificationPacket.Packet.Plaintext.Span,
            out var notification));
        TestAssert.Equal(runtime.Session.Participants[0].Hero!.NetId,
            notification.SenderNetId);
        TestAssert.Equal(decoded.Position, notification.Position);
    }

    [Check("network.p1-map-ping-rate")]
    public static void MapPingRateBoundaryAndRefill()
    {
        var runtime = CreateStartedFixture();
        var session = runtime.Session;
        var hero = session.Participants[0].Hero!;
        var packet = PingPackets.MapPingRequest(hero.NetId,
            new Position3(hero.Position.X, 60.0, hero.Position.Z), 0,
            MapPingCategory.Offense);

        for (var i = 0; i < PingPackets.PingBurstCapacity; i++)
            TestAssert.Equal(1, session.Receive(11, 1,
                packet.ToWireBytes(runtime.Cipher)).Count, $"burst {i}");
        TestAssert.Equal(0, session.Receive(11, 1,
            packet.ToWireBytes(runtime.Cipher)).Count,
            "the sixth ping is bounded for one player");

        var chaosHero = session.Participants[1].Hero!;
        var chaosPacket = PingPackets.MapPingRequest(chaosHero.NetId,
            new Position3(chaosHero.Position.X, 60.0, chaosHero.Position.Z), 0,
            MapPingCategory.Danger);
        TestAssert.Equal(1, session.Receive(12, 1,
            chaosPacket.ToWireBytes(runtime.Cipher)).Count,
            "a second player has an independent ping budget");

        runtime.World.Clock.SetVirtual(PingPackets.PingRefillSeconds - 0.001);
        TestAssert.Equal(0, session.Receive(11, 1,
            packet.ToWireBytes(runtime.Cipher)).Count,
            "a token is not available before the refill boundary");
        runtime.World.Clock.SetVirtual(PingPackets.PingRefillSeconds);
        TestAssert.Equal(1, session.Receive(11, 1,
            packet.ToWireBytes(runtime.Cipher)).Count,
            "the emulator policy resets at its three-second boundary");
        for (var i = 1; i < PingPackets.PingBurstCapacity; i++)
            TestAssert.Equal(1, session.Receive(11, 1,
                packet.ToWireBytes(runtime.Cipher)).Count, $"refilled window {i}");
        TestAssert.Equal(0, session.Receive(11, 1,
            packet.ToWireBytes(runtime.Cipher)).Count,
            "the reset budget is still bounded per player");
    }

    [Check("network.native-world-ready-gate")]
    public static void NativeWorldReadyFrameGatesInitialAndReconnectMap()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            readySettleSeconds: 0.0,
            roster: [new(1, 0, "NativeReady", "Janna", Team.Order)]);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Receive(1, 0, session.BuildKeyCheck(1));
        session.Receive(1, 1, [0xC5]);
        var readyFrame = new byte[] { 0xC6, 0, 0, 0, 0 };
        var progress = new byte[30];
        progress[0] = 0x19;
        progress[29] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(progress.AsSpan(17),
            BitConverter.SingleToInt32Bits(100.0f));
        var progressWire = runtime.Cipher.Encrypt(progress.AsSpan(0, 24))
            .Concat(progress.AsSpan(24).ToArray()).ToArray();
        var progressEcho = session.Receive(1, 1, progressWire).Single(packet =>
            packet.Packet.Plaintext.Span[0] == 0x9D).Packet.Plaintext.Span;
        TestAssert.Near(100.0, BitConverter.ToSingle(progressEcho.Slice(17, 4)), 1e-5,
            "100% stock-style encrypted progress is forwarded without declaring native readiness");
        session.Receive(1, 1, [0xA4, 0, 0, 0, 0, 1]);
        session.Receive(1, 1, [0x55]);
        // No 0xC6 yet: the client is still loading its map, and a hero burst that reaches it now faults it. The spawn
        // delay, the loading progress and the markers alone release neither the burst nor the map.
        runtime.World.Clock.SetVirtual(session.Participant(1)!.SpawnAt!.Value);
        var beforeReport = session.RunSchedulers();
        TestAssert.False(session.Participant(1)!.Spawned || session.Participant(1)!.Loaded || session.Started);
        TestAssert.False(beforeReport.Any(packet => packet.Packet.Plaintext.Span[0] is 0x65 or 0x4F or 0xA5 or 0x12 or 0x5F),
            "the spawn delay, loading progress and the loading markers cannot release the burst or the map");

        session.Receive(1, 4, readyFrame); // Wrong channel.
        session.Receive(1, 1, [0xC6, 0, 0, 0]); // Wrong length.
        session.Receive(1, 1, [0xC6, 0, 0, 0, 0, 1]); // Longer C6 UI traffic.
        session.Receive(1, 1, [0xC6, 0, 0, 1, 0]); // Wrong shape.
        TestAssert.False(session.Participant(1)!.Loaded);
        TestAssert.False(session.RunSchedulers().Any(packet =>
            packet.Packet.Plaintext.Span[0] is 0x65 or 0x4F or 0xA5 or 0x12 or 0x5F));
        TestAssert.False(session.Participant(1)!.Spawned, "an invalid 0xC6 releases no burst");

        session.Receive(1, 1, readyFrame);
        var initialSpawn = session.RunSchedulers();
        TestAssert.True(initialSpawn.Any(packet => packet.Packet.Plaintext.Span[0] == 0x4F),
            "the valid frame releases the hero burst");
        TestAssert.True(session.Participant(1)!.Spawned && session.Participant(1)!.Loaded &&
            !session.Participant(1)!.Ready && !session.Started,
            "the seat is made ready on the pass after its burst");
        TestAssert.False(initialSpawn.Any(packet => packet.Packet.Plaintext.Span[0] is 0xA5 or 0x12 or 0x5F),
            "the burst alone does not release the map");

        var initialReady = session.RunSchedulers();
        TestAssert.True(session.Started && session.Participant(1)!.Ready);
        TestAssert.Equal(session.MapEntities.Count(entity => entity.UnitType == UnitType.Turret),
            initialReady.Count(packet => packet.Packet.Plaintext.Span[0] == 0xA5));
        TestAssert.Equal(1, initialReady.Count(packet => packet.Packet.Plaintext.Span[0] == 0x5F));
        runtime.World.Clock.SetVirtual(session.ClockAt!.Value);
        session.RunSchedulers(); // Consume the original match's one-shot clock.
        var worldCount = session.World.Entities.Count;

        TestAssert.True(session.Disconnect(1));
        session.Receive(2, 0, session.BuildKeyCheck(1));
        session.Receive(2, 1, [0xC5]);
        session.Receive(1, 1, readyFrame); // Stale transport generation.
        runtime.World.Clock.SetVirtual(session.Participant(2)!.SpawnAt!.Value);
        session.Receive(2, 1, [0xA4, 0, 0, 0, 0, 1]);
        session.Receive(2, 1, [0x55]);
        session.Receive(2, 1, progressWire);
        TestAssert.False(session.Participant(2)!.Loaded);
        var reconnectWait = session.RunSchedulers();
        TestAssert.False(reconnectWait.Any(packet => packet.ConnectionId == 2 &&
            packet.Packet.Plaintext.Span[0] is 0x65 or 0x4F or 0xA5 or 0x12 or 0x5F or 0xC9),
            "a stale 0xC6, the markers and loading progress release neither the reconnect burst nor the map");
        TestAssert.False(session.Participant(2)!.Spawned);
        TestAssert.Equal(worldCount, session.World.Entities.Count,
            "reconnect loading does not recreate the world");

        session.Receive(2, 1, readyFrame);
        var reconnectSpawn = session.RunSchedulers();
        TestAssert.True(reconnectSpawn.Any(packet => packet.ConnectionId == 2 &&
            packet.Packet.Plaintext.Span[0] == 0x4F), "the valid frame releases the reconnect burst");
        TestAssert.True(session.Participant(2)!.Spawned && session.Participant(2)!.Loaded &&
            !session.Participant(2)!.Ready);
        TestAssert.False(reconnectSpawn.Any(packet => packet.ConnectionId == 2 &&
            packet.Packet.Plaintext.Span[0] is 0x12 or 0x5F or 0xC9),
            "the reconnect burst alone does not release the map or the clock");
        TestAssert.Equal(worldCount, session.World.Entities.Count,
            "the reconnect burst does not recreate the world");

        var reconnectReady = session.RunSchedulers().Where(packet => packet.ConnectionId == 2).ToArray();
        TestAssert.True(session.Participant(2)!.Loaded && session.Participant(2)!.Ready);
        TestAssert.Equal(1, reconnectReady.Count(packet => packet.Packet.Plaintext.Span[0] == 0x5F));
        TestAssert.Equal(1, reconnectReady.Count(packet => packet.Packet.Plaintext.Span[0] == 0xC9));
        session.Receive(2, 1, readyFrame);
        TestAssert.False(session.RunSchedulers().Any(packet => packet.ConnectionId == 2 &&
            packet.Packet.Plaintext.Span[0] is 0xA5 or 0x12 or 0x5F or 0xC9),
            "duplicate native readiness cannot replay the map or clock");
        TestAssert.Equal(worldCount, session.World.Entities.Count);
    }

    [Check("network.p1-reconnect-contract")]
    public static void ReconnectKeepsExistingSeatAndWorld()
    {
        var runtime = CreateStartedFixture();
        var session = runtime.Session;
        var reconnecting = session.Participants[0];
        var attachedPeer = session.Participants[1];
        runtime.Tick(4.25);
        var combatClockBefore = runtime.Combat.CurrentTime;
        var hero = reconnecting.Hero!;
        hero.Position = new Position2(7200.0, 6900.0);
        var inventory = new InventorySlot?[6];
        inventory[0] = new InventorySlot(3026, 1, 0);
        hero.Inventory = inventory;
        hero.SpellCooldowns[0] = 4.5;
        hero.ItemCooldowns[0] = 2.25;
        hero.Alive = false;
        hero.Health = 0.0;
        hero.RespawnAt = 19.0;
        var worldCount = session.World.Entities.Count;
        var heroReference = hero;
        runtime.World.Clock.SetVirtual(42.0);
        var clockBefore = runtime.World.Clock.Monotonic();

        TestAssert.True(session.Disconnect(11));
        TestAssert.True(reconnecting.ConnectionId is null);
        TestAssert.False(reconnecting.Spawned);
        TestAssert.False(reconnecting.Ready);
        TestAssert.True(ReferenceEquals(heroReference, reconnecting.Hero),
            "disconnect preserves the authoritative hero");
        TestAssert.True(session.AuthenticateReconnect(15,
            session.BuildKeyCheck(attachedPeer.PlayerId)) is null,
            "an attached seat cannot be taken over");
        TestAssert.True(session.AuthenticateReconnect(15,
            session.BuildKeyCheck(99)) is null,
            "an unknown player cannot join a started match");

        var output = session.Receive(15, 0, session.BuildKeyCheck(reconnecting.PlayerId));
        TestAssert.False(output.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span.Length > 0 && item.Packet.Plaintext.Span[0] == 0x10),
            "a fresh client must not receive a reconnect HUD notification before loading");
        TestAssert.False(output.Any(item => item.Packet.Plaintext.Span.Length > 0 &&
            item.Packet.Plaintext.Span[0] == 0x4F),
            "reconnect loading does not recreate a hero");
        TestAssert.Equal(15L, reconnecting.ConnectionId);
        TestAssert.False(reconnecting.Spawned || reconnecting.Ready || reconnecting.Loaded,
            "key check gates reconnect until the stock loading markers arrive");
        TestAssert.True(reconnecting.View.Known.Count == 0 && reconnecting.View.Visible.Count == 0,
            "the new observer starts with a fresh per-peer view");
        TestAssert.True(ReferenceEquals(heroReference, reconnecting.Hero));
        TestAssert.Equal(worldCount, session.World.Entities.Count,
            "reconnect does not create a second world or hero");
        TestAssert.Equal(clockBefore, runtime.World.Clock.Monotonic(),
            "reconnect does not reset the match clock");
        var versionOutput = session.Receive(15, 1, new byte[] { 0xC5 });
        TestAssert.True(versionOutput.Any(item => item.Packet.Plaintext.Span[0] == 0x57) &&
            versionOutput.Any(item => item.Packet.Plaintext.Span[0] == 0x98),
            "a started reconnect receives the version/world handshake before spawning");
        TestAssert.True(reconnecting.SpawnAt is not null && !reconnecting.Spawned,
            "the version handshake arms a delayed existing-hero spawn baseline");
        var spawnAt = reconnecting.SpawnAt!.Value;
        var reconnectNotice = new byte[] { 0xA4, 0, 0, 0, 0, 1 };
        session.Receive(15, 1, reconnectNotice);
        session.Receive(15, 1, new byte[] { 0x55 });
        TestAssert.False(reconnecting.Loaded,
            "loading markers cannot declare the restarted native world ready");
        runtime.World.Clock.SetVirtual(spawnAt);
        TestAssert.False(session.RunSchedulers().Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] is 0x65 or 0x4F),
            "the spawn delay alone does not send the burst while the restarted client is still loading its map");
        TestAssert.False(reconnecting.Spawned);
        session.Receive(15, 1, new byte[] { 0xC6, 0, 0, 0, 0 });
        var spawnOutput = session.RunSchedulers();
        TestAssert.True(spawnOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0x65),
            "reconnect starts the staged spawn with StartSpawn");
        TestAssert.True(spawnOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0x4F),
            "reconnect sends roster hero baselines from the existing world");
        var chaosHeroNetId = session.Participants[1].Hero!.NetId;
        TestAssert.True(spawnOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0xC2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == heroReference.NetId),
            "the reconnect's own model is initialized before StartGame constructs its HUD");
        foreach (var member in session.Participants)
            TestAssert.True(spawnOutput.Any(item => item.ConnectionId == 15 &&
                item.Packet.Plaintext.Span[0] == 0x2C &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == member.Hero!.NetId),
                "every roster avatar is initialized before EndSpawn and StartGame, including hidden heroes");
        TestAssert.True(spawnOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0x4F &&
            BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[5..9]) ==
                chaosHeroNetId),
            "reconnect includes the detached seat's roster identity");
        TestAssert.False(spawnOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0xC2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..5]) ==
                chaosHeroNetId),
            "reconnect does not reveal a hidden enemy position in the baseline");
        TestAssert.False(spawnOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] is 0x12 or 0x5F),
            "map readiness waits for the native world/UI-ready frame");
        TestAssert.True(reconnecting.Spawned && !reconnecting.Ready && reconnecting.Loaded,
            "reconnect is spawned but is made ready only on the pass after its burst");
        var readyOutput = session.RunSchedulers();
        foreach (var mapEntity in session.MapEntities)
            TestAssert.True(reconnecting.View.Known.Contains(mapEntity.NetId),
                "reconnect map burst seeds every map ID into Known");
        TestAssert.False(reconnecting.View.Visible.Contains(session.Participants[1].Hero!.NetId),
            "map-known seeding does not reveal the fogged opposing hero");
        TestAssert.True(readyOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0x12) &&
            readyOutput.Any(item => item.ConnectionId == 15 &&
                item.Packet.Plaintext.Span[0] == 0x5F),
            "native world/UI readiness releases EndSpawn and StartGame");
        var gameClock = readyOutput.Single(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0xC9).Packet.Plaintext.Span;
        TestAssert.Near(combatClockBefore,
            BitConverter.ToSingle(gameClock.Slice(5, 4)), 1e-5,
            "reconnect resumes the current combat clock");
        TestAssert.True(readyOutput.Any(item => item.ConnectionId == 15 &&
            item.Packet.Plaintext.Span[0] == 0xC2),
            "reconnect includes the ordinary shop baseline at readiness");
        TestAssert.False(session.AuthenticateReconnect(16,
            session.BuildKeyCheck(reconnecting.PlayerId)) is not null,
            "a reconnected seat cannot be attached twice");
        TestAssert.Equal(4.5, hero.SpellCooldowns[0]);
        TestAssert.Equal(2.25, hero.ItemCooldowns[0]);
        TestAssert.False(hero.Alive,
            "dead seats reconnect without being respawned");
    }

    [Check("network.p1-reconnect-native", Serial = true)]
    public static void NativeReconnectReattachesObserverAndState() => VerifyNativeReconnect(false);

    [Check("network.loading-crash-reconnect", Serial = true)]
    public static void LoadingCrashReleasesSeatBeforeStart()
    {
        foreach (var alreadySpawned in new[] { false, true })
        {
            var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0);
            runtime.World.Clock.SetVirtual(0.0);
            using var network = new MatchRuntimeNetwork(runtime, new LeagueEnetHost(0, 3, 8, bindHost: "127.0.0.1"), ownsHost: true);
            using var oldClient = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            using var otherClient = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            using var replacement = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            var oldPeer = oldClient.Connect("127.0.0.1", network.Port, 8);
            var otherPeer = otherClient.Connect("127.0.0.1", network.Port, 8);
            WaitNative(network, [oldClient, otherClient], () => network.ConnectionIds.Count == 2);
            oldPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
            otherPeer.Send(0, runtime.Session.BuildKeyCheck(2), LeagueEnetPacketFlags.Reliable);
            oldClient.Flush(); otherClient.Flush();
            WaitNative(network, [oldClient, otherClient],
                () => runtime.Session.Participants.All(seat => seat.ConnectionId is not null));
            var seat = runtime.Session.Participants[0];
            var previousId = seat.ConnectionId!.Value;
            if (alreadySpawned)
            {
                runtime.Session.Spawn(previousId);
                runtime.Session.MarkReady(previousId);
            }
            var previousHero = seat.Hero;
            var worldCount = runtime.World.Entities.Count;
            oldClient.Dispose(); // A crash leaves no native Exit/disconnect event.
            var peer = replacement.Connect("127.0.0.1", network.Port, 8);
            WaitNative(network, [otherClient, replacement], () => network.ConnectionIds.Count == 3);
            var badKey = runtime.Session.BuildKeyCheck(1); badKey[^1] ^= 1;
            peer.Send(0, badKey, LeagueEnetPacketFlags.Reliable); replacement.Flush();
            for (var i = 0; i < 10; i++) { network.Pump(1); Drain(replacement, null); }
            TestAssert.Equal(previousId, seat.ConnectionId, "invalid credentials cannot free a loading seat");

            // Invalid channel-zero credentials immediately retire their own
            // generation. A valid retry therefore arrives on a fresh
            // transport, while the old seat remains protected.
            using var validReplacement = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            var validPeer = validReplacement.Connect("127.0.0.1", network.Port, 8);
            WaitNative(network, [otherClient, validReplacement],
                () => network.ConnectionIds.Count == 3);
            validPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
            validReplacement.Flush();
            WaitNative(network, [otherClient, validReplacement],
                () => seat.ConnectionId is long current && current != previousId);
            TestAssert.False(runtime.Session.Started);
            TestAssert.False(seat.Ready || seat.Spawned || seat.Loaded,
                "the new process must cross the complete loading barrier");
            TestAssert.True(ReferenceEquals(previousHero, seat.Hero));
            TestAssert.Equal(worldCount, runtime.World.Entities.Count);
            TestAssert.False(runtime.Session.Disconnect(previousId), "old transport cannot detach its replacement");
            validPeer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
            otherPeer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
            validPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            otherPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            validReplacement.Flush(); otherClient.Flush();
            WaitNative(network, [otherClient, validReplacement],
                () => runtime.Session.Participants.All(player => player.Loaded));
            WaitNative(network, [otherClient, validReplacement],
                () => runtime.Session.Participants.All(player => player.Spawned));
            validPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            otherPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            validReplacement.Flush(); otherClient.Flush();
            WaitNative(network, [otherClient, validReplacement], () => runtime.Session.Started);
            TestAssert.Equal(2, network.ConnectionIds.Count);
            if (alreadySpawned) TestAssert.True(ReferenceEquals(previousHero, seat.Hero));
            TestAssert.True(runtime.Session.Participants.All(player => player.Ready && player.Loaded));
        }
    }

    [Check("session.scheduler-reconnect-gating")]
    public static void SchedulerRespectsLoadingAndFog()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0,
            roster: [new(1, 0, "Loading", "Annie", Team.Order),
                     new(2, 1, "Observer", "Orianna", Team.Chaos),
                     new(3, 2, "Disconnected", "LeeSin", Team.Order)]);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        foreach (var id in new[] { 1L, 2L, 3L })
        {
            session.Authenticate(id, session.BuildKeyCheck((ulong)id));
            session.Spawn(id); session.MarkReady(id);
        }
        session.RunSchedulers();
        TestAssert.True(session.Started);
        session.Disconnect(1);
        var loading = session.AuthenticateReconnect(4, session.BuildKeyCheck(1))!;
        var observer = session.Participant(2)!;
        var disconnected = session.Participant(3)!.Hero!;
        disconnected.Position = new Position2(7000, 7000);
        session.Disconnect(3);
        observer.View.Visible.Remove(disconnected.NetId);
        var hidden = session.RunSchedulers();
        TestAssert.False(hidden.Any(packet => packet.Packet.EntityReferences.Any(reference =>
            reference.NetId == disconnected.NetId)), "return-home routes must not expose an enemy in fog");
        disconnected.Waypoints.Clear(); disconnected.Target = null;
        observer.View.Visible.Add(disconnected.NetId);
        var visible = session.RunSchedulers();
        TestAssert.False(visible.Any(packet => packet.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode),
            "a detached hero gets no automatic route (the stock server leaves it standing)");
        TestAssert.False(visible.Any(packet => packet.ConnectionId == 4), "loading peer cannot receive world routes");

        // Crossing all one-shot scheduler deadlines while one client reloads
        // must neither send it gameplay packets nor suppress the other seat.
        runtime.Tick(1.25);
        var clock = session.RunSchedulers();
        TestAssert.True(clock.Any(packet => packet.ConnectionId == 2 && packet.Packet.Plaintext.Span[0] == 0xC9));
        TestAssert.False(clock.Any(packet => packet.ConnectionId == 4));
        runtime.Tick(1.5);
        TestAssert.False(session.RunSchedulers().Any(packet => packet.ConnectionId == 4));
        runtime.Tick(96.0);
        var scheduled = session.RunSchedulers();
        TestAssert.True(scheduled.Any(packet => packet.ConnectionId == 2));
        TestAssert.False(scheduled.Any(packet => packet.ConnectionId == 4));

        session.Receive(4, 1, new byte[] { 0xC5 });
        session.Receive(4, 1, new byte[] { 0xC6, 0, 0, 0, 0 });
        runtime.World.Clock.SetVirtual(loading.SpawnAt!.Value);
        session.RunSchedulers();
        session.Receive(4, 1, new byte[] { 0xC6, 0, 0, 0, 0 });
        var ready = session.RunSchedulers().Where(packet => packet.ConnectionId == 4).ToArray();
        TestAssert.True(loading.Ready);
        var idle = SpawnPackets.SwitchNexusesToIdleParticles().Plaintext.ToArray();
        TestAssert.True(ready.Any(packet => packet.Packet.Plaintext.Span.SequenceEqual(idle)),
            "late loading restores the current Nexus particle phase after map creation");
    }

    [Check("network.crash-reconnect-production-loop", Serial = true)]
    public static void CrashReconnectWithProductionLoop() => VerifyNativeReconnect(true);

    private static void VerifyNativeReconnect(bool crashed)
    {
        void WaitNative(MatchRuntimeNetwork network, IReadOnlyList<LeagueEnetHost> clients,
            Func<bool> done, List<LeagueEnetEvent>? captured = null) =>
            P1NetworkChecks.WaitNative(network, clients, done, captured, productionLoop: crashed);
        var runeCatalog = new RuneCatalog();
        var masteryCatalog = new MasteryCatalog();
        var orderRunes = RunePage.Create(runeCatalog,
            red: new[] { 5001, 5014 }, summonerLevel: 30);
        var chaosRunes = RunePage.Create(runeCatalog,
            red: new[] { 5002 }, summonerLevel: 30);
        var orderMasteries = MasteryPage.Create(masteryCatalog,
            new Dictionary<int, int>
            {
                [103] = 3, [106] = 3, [136] = 1, [126] = 1, [102] = 4, [107] = 1,
            });
        var chaosMasteries = MasteryPage.Create(masteryCatalog,
            new Dictionary<int, int>
            {
                [133] = 1, [110] = 3, [129] = 1, [109] = 3, [112] = 1,
            });
        var disconnectLog = new DisconnectLog();
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), orderChampion: "Nidalee", readySettleSeconds: 0.0,
            roster:
            [
                new(1, 0, "Player1", "Nidalee", Team.Order,
                    SummonerLevel: 30, Runes: orderRunes, Masteries: orderMasteries),
                new(2, 1, "Player2", "Orianna", Team.Chaos,
                    SummonerLevel: 30, Runes: chaosRunes, Masteries: chaosMasteries),
            ]);
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 3, 8, bindHost: "127.0.0.1"), ownsHost: true, log: disconnectLog);
        using var clientA = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var clientB = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var clientC = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        runtime.World.Clock.SetVirtual(0.0);

        var peerA = clientA.Connect("127.0.0.1", network.Port, 8);
        WaitNative(network, [clientA],
            () => network.ConnectionIds.Count == 1);
        var peerB = clientB.Connect("127.0.0.1", network.Port, 8);
        WaitNative(network, [clientA, clientB],
            () => network.ConnectionIds.Count == 2);
        var serverConnections = network.ConnectionIds.ToArray();
        peerA.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        peerB.Send(0, runtime.Session.BuildKeyCheck(2), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        clientB.Flush();
        WaitNative(network, [clientA, clientB],
            () => runtime.Session.Participants.All(item => item.ConnectionId is not null));

        var orderConnection = runtime.Session.Participant(serverConnections[0]) is { PlayerId: 1 }
            ? serverConnections[0] : serverConnections[1];
        var chaosConnection = orderConnection == serverConnections[0]
            ? serverConnections[1] : serverConnections[0];
        runtime.Session.Spawn(orderConnection);
        runtime.Session.Spawn(chaosConnection);
        runtime.Session.MarkReady(orderConnection);
        runtime.Session.MarkReady(chaosConnection);
        runtime.Session.BuildMapReadyPackets(orderConnection);
        runtime.Session.BuildMapReadyPackets(chaosConnection);
        TestAssert.True(runtime.Session.Start());
        network.Pump(0, 32);

        var orderParticipant = runtime.Session.Participant(orderConnection)!;
        var hero = orderParticipant.Hero!;
        hero.NeutralMinionsKilled = 17;
        var hiddenScoreHero = runtime.Session.Participant(chaosConnection)!.Hero!;
        hiddenScoreHero.NeutralMinionsKilled = 23;
        var inventory = new InventorySlot?[6];
        inventory[0] = new InventorySlot(3026, 1, 0);
        hero.Inventory = inventory;
        hero.SpellCooldowns[0] = 4.5;
        hero.SpellRanks = [3, 2, 1, 1];
        hero.SkillPoints = 4;
        hero.SummonerSpells[0] = "TeleportCancel";
        hero.ScriptCharacterData = new ScriptCharacterLayer(
            runtime.World.Allocate(UnitType.Missile), "Nidalee_Cougar", OverrideSpells: true);
        // The reconnect burst gives a returning client the layers from the delivery's persistent character-data ledger (batch 99), which
        // BBPushCharacterData fills through the production sink; the entity field alone is no longer what the burst reads.
        runtime.Spells.Effects.SendPushCharacterData(hero, hero.ScriptCharacterData.Id, hero.ScriptCharacterData.SkinName,
            hero.ScriptCharacterData.OverrideSpells);
        hero.ActiveSpellNames = ["Takedown", "Pounce", "Swipe", "AspectOfTheCougar"];
        hero.Buffs.Add(new BuffState("AspectOfTheCougar", slot: 1, expiresAt: 100));
        // Reconnect snapshots only serialize shipped client buffs. Keep this
        // state-backed check on a real Nidalee buff rather than a server-only
        // synthetic marker that the native client cannot resolve.
        hero.Buffs.Add(new BuffState("PrimalSurge", slot: 2, expiresAt: 100));
        hero.Alive = false;
        hero.Health = 0.0;
        hero.RespawnAt = runtime.Combat.CurrentTime + 20.0;
        var deadTurret = runtime.Session.MapEntities.First(entity =>
            entity.Kind == EntityKind.Turret &&
            !entity.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase));
        deadTurret.Alive = false;
        deadTurret.Health = 0.0;
        var deadInhibitor = runtime.Session.MapEntities.First(entity =>
            entity.Kind == EntityKind.Inhibitor);
        deadInhibitor.Alive = false;
        deadInhibitor.Health = 0.0;
        deadInhibitor.RespawnAt = runtime.Combat.CurrentTime + 12.0;
        var worldCount = runtime.World.Entities.Count;
        var heroReference = hero;

        // The stock client sends Exit and closes without an ENet disconnect.
        // The server must free this seat before a new client process joins.
        if (crashed)
        {
            clientA.Dispose(); // No Exit or ENet disconnect reaches the server.
            TestAssert.Equal(orderConnection, orderParticipant.ConnectionId);
        }
        else
        {
            peerA.Send(1, new byte[] { 0x94, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            clientA.Flush();
            WaitNative(network, [clientA, clientB], () => orderParticipant.ConnectionId is null);
            TestAssert.Equal(1, network.ConnectionIds.Count);
        }

        var reconnectPeer = clientC.Connect("127.0.0.1", network.Port, 8);
        WaitNative(network, [clientC],
            () => network.ConnectionIds.Count == (crashed ? 3 : 2));
        var validClient = clientC;
        LeagueEnetPeer validReconnectPeer = reconnectPeer;
        if (crashed)
        {
            var invalidKey = runtime.Session.BuildKeyCheck(1); invalidKey[^1] ^= 1;
            reconnectPeer.Send(0, invalidKey, LeagueEnetPacketFlags.Reliable); clientC.Flush();
            for (var i = 0; i < 10; i++) { network.Pump(1); Drain(clientC, null); }
            TestAssert.Equal(orderConnection, orderParticipant.ConnectionId, "bad credentials cannot evict a seat");
            validReconnectPeer = validClient.Connect("127.0.0.1", network.Port, 8);
            WaitNative(network, [clientB, validClient], () => network.ConnectionIds.Count == 3);
        }
        validReconnectPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        validClient.Flush();
        var reconnectEvents = new List<LeagueEnetEvent>();
        WaitNative(network, [clientB, validClient], () =>
        {
            return orderParticipant.ConnectionId is long connectionId &&
                connectionId != orderConnection;
        }, reconnectEvents);

        validReconnectPeer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
        validReconnectPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        validClient.Flush();
        WaitNative(network, [clientB, validClient],
            () => orderParticipant.Loaded, reconnectEvents);
        WaitNative(network, [clientB, validClient],
            () => orderParticipant.Spawned, reconnectEvents);
        validReconnectPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        validClient.Flush();
        WaitNative(network, [clientB, validClient],
            () =>
            {
                // Ready is a server-side loading state. ENet delivery can
                // still lag that transition by a poll, so keep the bounded
                // receive loop alive until every reconnect snapshot family
                // consumed below has reached the restarted client.
                var payloads = reconnectEvents
                    .Select(item => DecodeReconnectPayload(runtime.Cipher, item))
                    .ToArray();
                bool Has(byte opcode) => payloads.Any(payload =>
                    payload.Length > 0 && payload[0] == opcode);
                bool HasEntity(byte opcode, uint netId, int minimumLength = 5) => payloads.Any(payload =>
                    payload.Length >= minimumLength && payload[0] == opcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == netId);
                bool HasScore(uint netId) => payloads.Any(payload =>
                    payload.Length >= 23 && payload[0] == 0xCC &&
                    payload[10] == 8 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(11)) == netId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(15)) == 0x200u);
                var avatarIndex = Array.FindIndex(payloads, payload =>
                    payload.Length >= 534 && payload[0] == 0x2C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId);
                bool HasHeroStatsAfterAvatar() => avatarIndex >= 0 &&
                    payloads.Skip(avatarIndex + 1).Any(payload =>
                        payload.Length >= 15 && payload[0] == 0xCC &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(11, 4)) == hero.NetId);
                bool HasRankSlots() => Enumerable.Range(0, 4).All(slot => payloads.Any(payload =>
                    payload.Length >= 7 && payload[0] == 0x18 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId &&
                    payload[5] == slot));
                bool HasSpellSlots() => Enumerable.Range(0, 4).All(slot => payloads.Any(payload =>
                    payload.Length >= 14 && payload[0] == 0x73 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId &&
                    payload[13] == slot));
                var mapTurrets = runtime.Session.MapEntities
                    .Where(entity => entity.UnitType == UnitType.Turret)
                    .Select(entity => entity.NetId).ToArray();
                bool HasOneMapCreatePerTurret() => mapTurrets.All(netId => payloads.Count(payload =>
                    payload.Length >= 9 && payload[0] == 0xA5 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(5, 4)) == netId) == 1);

                return orderParticipant.Ready &&
                    HasOneMapCreatePerTurret() &&
                    Has(0x4F) && Has(0x56) && Has(0x8A) &&
                    HasScore(hero.NetId) && HasScore(hiddenScoreHero.NetId) &&
                    HasEntity(0x9F, hero.NetId, 74) && HasRankSlots() && HasSpellSlots() &&
                    HasHeroStatsAfterAvatar() && HasEntity(0x2C, hero.NetId, 534) &&
                    HasEntity(0x35, hero.NetId, 7) &&
                    payloads.Any(payload => payload.Length >= 25 && payload[0] == 0xBF &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId &&
                        BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(9, 4)) ==
                            SpellPackets.BuffNameHash("PrimalSurge")) &&
                    HasEntity(0x61, hero.NetId, 16) && HasEntity(0x1E, deadTurret.NetId, 5) &&
                    HasEntity(0x2D, deadInhibitor.NetId, 8);
            }, reconnectEvents);

        var reconnected = runtime.Session.Participants.Single(item => item.PlayerId == 1);
        TestAssert.True(ReferenceEquals(orderParticipant, reconnected),
            "reconnect reuses the fixed roster seat object");
        TestAssert.True(ReferenceEquals(heroReference, reconnected.Hero));
        TestAssert.Equal(worldCount, runtime.World.Entities.Count,
            "native reconnect must not recreate the world");
        TestAssert.False(reconnectEvents.Any(item => item.Channel == LeagueProtocolConstants.VariantChannel &&
            HasPlainOpcode(runtime.Cipher, item, 0x10)),
            "restarted native transport follows loading without the unsafe HUD notification");
        TestAssert.True(reconnectEvents.Any(item => HasPlainOpcode(runtime.Cipher, item, 0x4F)),
            "ordinary replication recreates current visible state for the new observer");
        TestAssert.True(reconnectEvents.Any(item => HasPlainOpcode(runtime.Cipher, item, 0x56)),
            "reconnect state includes current inventory");
        TestAssert.True(reconnectEvents.Any(item => HasPlainOpcode(runtime.Cipher, item, 0x8A)),
            "reconnect state includes current cooldowns");
        var reconnectPayloads = reconnectEvents
            .Select(item => DecodeReconnectPayload(runtime.Cipher, item))
            .ToArray();
        foreach (var (scoredHero, expectedKills) in new[] { (hero, 17), (hiddenScoreHero, 23) })
        {
            var score = reconnectPayloads.Single(payload => payload.Length >= 23 && payload[0] == 0xCC &&
                payload[10] == 8 && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(11)) == scoredHero.NetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(15)) == 0x200u);
            TestAssert.Equal(expectedKills, BinaryPrimitives.ReadInt32LittleEndian(score.AsSpan(19)),
                "reconnect restores each roster hero's score, including an enemy in fog");
            TestAssert.Equal(expectedKills, scoredHero.NeutralMinionsKilled, "snapshot leaves authoritative score unchanged");
        }
        var transformedModel = reconnectPayloads.Single(payload =>
            payload.Length >= 74 && payload[0] == 0x9F &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId);
        TestAssert.Equal(hero.ScriptCharacterData!.Id,
            BinaryPrimitives.ReadUInt32LittleEndian(transformedModel.AsSpan(5, 4)),
            "reconnect model change targets the existing model allocation");
        TestAssert.Equal((byte)1, transformedModel[9],
            "reconnect model change restores the transformed spell book");
        TestAssert.Equal("Nidalee_Cougar",
            Encoding.ASCII.GetString(transformedModel, 10, 63).TrimEnd('\0'),
            "reconnect model change restores the cougar skin");

        var rankPackets = reconnectPayloads.Where(payload =>
            payload.Length == 8 && payload[0] == 0x18 &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId)
            .ToArray();
        TestAssert.Equal(4, rankPackets.Length,
            "reconnect state restores exactly the four champion rank slots");
        foreach (var (slot, expectedRank) in new[] { (0, 3), (1, 2), (2, 1), (3, 1) })
        {
            var rank = rankPackets.Single(payload => payload[5] == slot);
            TestAssert.Equal((byte)expectedRank, rank[6], $"reconnect rank slot {slot}");
            TestAssert.Equal((byte)4, rank[7], $"reconnect skill points slot {slot}");
        }

        foreach (var (slot, expectedName) in new[]
                 { (0, "Takedown"), (1, "Pounce"), (2, "Swipe"), (3, "AspectOfTheCougar") })
        {
            var spellData = reconnectPayloads.Single(payload =>
                payload.Length >= 14 && payload[0] == 0x73 &&
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId &&
                payload[13] == slot);
            TestAssert.Equal(ProtocolEncoding.HashString(expectedName),
                BinaryPrimitives.ReadUInt32LittleEndian(spellData.AsSpan(9, 4)),
                $"reconnect current spell name slot {slot}");
        }

        var avatarIndex = Array.FindIndex(reconnectPayloads, payload =>
            payload.Length >= 534 && payload[0] == 0x2C &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId);
        var heroStatsIndex = Array.FindIndex(reconnectPayloads, payload =>
            payload.Length >= 15 && payload[0] == 0xCC &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(11, 4)) == hero.NetId);
        TestAssert.True(avatarIndex >= 0 && heroStatsIndex > avatarIndex,
            "reconnect loadout metadata precedes the final authoritative hero stats");
        var avatar = reconnectPayloads[avatarIndex];
        TestAssert.Equal(orderParticipant.Runes.WireIds[0],
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(avatar.AsSpan(5, 4))),
            "reconnect preserves the order seat's first rune");
        var encodedMasteries = new Dictionary<uint, byte>();
        for (var slot = 0; slot < 80; slot++)
        {
            var offset = 133 + slot * 5;
            var idHash = BinaryPrimitives.ReadUInt32LittleEndian(avatar.AsSpan(offset, 4));
            var rank = avatar[offset + 4];
            if (idHash == 0)
            {
                TestAssert.Equal((byte)0, rank,
                    "reconnect empty mastery slots have no rank");
                continue;
            }

            TestAssert.True(encodedMasteries.TryAdd(idHash, rank),
                "reconnect mastery entries do not repeat an id");
        }
        TestAssert.Equal(orderParticipant.Masteries.Allocations.Count, encodedMasteries.Count,
            "reconnect preserves the full order seat mastery allocation count");
        foreach (var (id, expectedRank) in orderParticipant.Masteries.Allocations)
        {
            var idHash = ProtocolEncoding.HashString(
                id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            TestAssert.True(encodedMasteries.TryGetValue(idHash, out var actualRank),
                $"reconnect preserves mastery id {id}");
            TestAssert.Equal((byte)expectedRank, actualRank,
                $"reconnect preserves mastery rank {id}");
        }
        var slotNameIndex = Array.FindIndex(reconnectPayloads, payload =>
            payload.Length >= 7 && payload[0] == 0x35 &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId);
        TestAssert.True(avatarIndex >= 0 && slotNameIndex > avatarIndex,
            "original AvatarInfo precedes the live summoner slot override");
        var slotName = reconnectPayloads[slotNameIndex];
        TestAssert.Equal((byte)0x80, slotName[5],
            "live summoner slot update remains a summoner slot");
        var slotNameEnd = Array.IndexOf(slotName, (byte)0, 6);
        TestAssert.True(slotNameEnd > 6, "live summoner slot update is terminated");
        TestAssert.Equal("TeleportCancel",
            Encoding.ASCII.GetString(slotName, 6, slotNameEnd - 6),
            "reconnect restores the active TeleportCancel slot name");

        var primalSurgeBuff = reconnectPayloads.Single(payload =>
            payload.Length >= 25 && payload[0] == 0xBF &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(9, 4)) ==
                SpellPackets.BuffNameHash("PrimalSurge"));
        TestAssert.Equal(hero.NetId,
            BinaryPrimitives.ReadUInt32LittleEndian(primalSurgeBuff.AsSpan(1, 4)),
            "reconnect buff snapshot targets the existing hero");

        var deadHero = reconnectPayloads.Single(payload =>
            payload.Length == 16 && payload[0] == 0x61 &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == hero.NetId);
        TestAssert.Near(hero.RespawnAt - runtime.Combat.CurrentTime,
            BitConverter.ToSingle(deadHero, 11), crashed ? .05 : 1e-5,
            "reconnect preserves the dead hero respawn duration");

        // A destroyed tower is a client-built obj_AI_Turret that 0xA5 binds a net ID to. It is killed with
        // ForceDead, not an NPC death: the native death notifies EVENT_ON_DIE and the client raises the
        // OnTurretDie announcement from it, which a state restore must not do.
        TestAssert.Equal(1, reconnectPayloads.Count(payload =>
            payload.Length == 5 && payload[0] == 0x1E &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == deadTurret.NetId),
            "reconnect kills the destroyed turret exactly once with ForceDead");
        TestAssert.False(reconnectPayloads.Any(payload => payload.Length > 0 && payload[0] is 0xA6 or 0x4A),
            "reconnect replays no NPC death or world event (the client announces them natively)");
        var deadInhibitorPacket = reconnectPayloads.Single(payload =>
            payload.Length == 8 && payload[0] == 0x2D &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4)) == deadInhibitor.NetId);
        // retail sweep B4, 27 Sept 2026: replace the old offset-6 dead high bit.
        var inhibSeconds = BinaryPrimitives.ReadUInt16LittleEndian(deadInhibitorPacket.AsSpan(5, 2)) & 0x7FFF;
        TestAssert.Equal(12, inhibSeconds,
            "reconnect dead inhibitor packet preserves its twelve-second timer");
        TestAssert.Equal(0, deadInhibitorPacket[7] & 1,
            "reconnect restores the native regenerating state");
        var disconnect = disconnectLog.Events.First(e => Equals(e["player_id"], 1UL));
        TestAssert.True(disconnect["reason"] is string reason &&
            reason is "client-exit" or "enet-disconnect" or "replaced-by-reconnect");
        TestAssert.True(disconnect["last_receive_age_seconds"] is double age && age >= 0);
        TestAssert.True(disconnect.ContainsKey("stats_age_seconds") && disconnect.ContainsKey("rtt_ms"),
            "retired native connections retain a last usable ENet sample");
    }

    private sealed class DisconnectLog : ISessionLogSink
    {
        public List<IReadOnlyDictionary<string, object?>> Events { get; } = [];
        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (message == "connection.disconnected" && properties is not null) Events.Add(properties);
        }
    }

    [Check("network.authenticated-channel-zero-retry", Serial = true)]
    public static void AuthenticatedChannelZeroRetriesRemainBoundToCurrentSeat()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 2, 8, bindHost: "127.0.0.1"), ownsHost: true);
        using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        var peer = client.Connect("127.0.0.1", network.Port, 8);
        WaitNative(network, [client], () => network.ConnectionIds.Count == 1);
        var connectionId = network.ConnectionIds.Single();
        var key = runtime.Session.BuildKeyCheck(1);
        peer.Send(0, key, LeagueEnetPacketFlags.Reliable);
        client.Flush();
        WaitNative(network, [client], () => runtime.Session.Participant(connectionId) is not null);
        var seat = runtime.Session.Participant(connectionId)!;
        var seatReference = seat;

        void DrainClientBounded(List<LeagueEnetEvent>? events, DateTime deadline)
        {
            while (true)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("authenticated channel-zero retry client servicing timed out");
                var @event = client.Service(0);
                if (@event.Type == LeagueEnetEventType.None) return;
                events?.Add(@event);
            }
        }

        void QuiesceOwnedEvents()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (true)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("authenticated channel-zero retry quiesce timed out");
                var handled = network.Pump(0, 32, flushDelivery: false);
                DrainClientBounded(null, deadline);
                if (handled == 0) return;
            }
        }

        int PumpSubmitted(int expected, List<LeagueEnetEvent>? events)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var handled = 0;
            while (handled < expected)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("authenticated channel-zero retry input timed out");
                handled += network.Pump(1, expected - handled, flushDelivery: false);
                DrainClientBounded(events, deadline);
            }
            return handled;
        }

        var retryEvents = new List<LeagueEnetEvent>();
        QuiesceOwnedEvents();
        peer.Send(0, key, LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var retryHandled = PumpSubmitted(1, retryEvents);
        TestAssert.Equal(1, retryHandled,
            "the same-seat retry is consumed by exactly one native server event");
        WaitNative(network, [client],
            () => retryEvents.Any(item => item.Type == LeagueEnetEventType.Receive),
            retryEvents);
        TestAssert.True(ReferenceEquals(seatReference,
            runtime.Session.Participant(connectionId)),
            "a valid same-seat retry uses the existing session receive/send path");

        var malformed = (byte[])key.Clone();
        malformed[^1] ^= 1;
        QuiesceOwnedEvents();
        peer.Send(0, malformed, LeagueEnetPacketFlags.Reliable);
        peer.Send(0, runtime.Session.BuildKeyCheck(2), LeagueEnetPacketFlags.Reliable);
        peer.Send(0, key, LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var preStartSentinel = new List<LeagueEnetEvent>();
        var preStartHandled = PumpSubmitted(3, preStartSentinel);
        TestAssert.Equal(3, preStartHandled,
            "malformed, wrong-seat, and valid retries are all consumed before assertions");
        WaitNative(network, [client],
            () => preStartSentinel.Any(item => item.Type == LeagueEnetEventType.Receive),
            preStartSentinel);
        TestAssert.True(ReferenceEquals(seatReference,
            runtime.Session.Participant(connectionId)));
        TestAssert.True(runtime.Session.Participants.Single(item => item.PlayerId == 2).ConnectionId is null,
            "wrong-seat credentials cannot acquire another seat before start");
        TestAssert.Equal(new[] { connectionId }, network.ConnectionIds.ToArray(),
            "malformed and wrong-seat retries keep the authenticated generation");

        var other = runtime.Session.Participants.Single(item => item.PlayerId == 2);
        TestAssert.True(runtime.Session.Authenticate(999,
            runtime.Session.BuildKeyCheck(other.PlayerId)) is not null);
        runtime.Session.Spawn(connectionId);
        runtime.Session.Spawn(999);
        runtime.Session.MarkReady(connectionId);
        runtime.Session.MarkReady(999);
        runtime.Session.BuildMapReadyPackets(connectionId);
        runtime.Session.BuildMapReadyPackets(999);
        TestAssert.True(runtime.Session.Start());
        TestAssert.True(runtime.Session.Disconnect(999),
            "the other seat is detached before its retry is presented");

        var postStartEvents = new List<LeagueEnetEvent>();
        QuiesceOwnedEvents();
        peer.Send(0, runtime.Session.BuildKeyCheck(other.PlayerId),
            LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var postStartHandled = PumpSubmitted(1, postStartEvents);
        TestAssert.Equal(1, postStartHandled,
            "post-start wrong-seat retry is consumed before ownership assertions");
        TestAssert.True(ReferenceEquals(seatReference,
            runtime.Session.Participant(connectionId)));
        TestAssert.True(other.ConnectionId is null,
            "a disconnected other seat cannot be acquired by an authenticated generation");
        TestAssert.Equal(new[] { connectionId }, network.ConnectionIds.ToArray(),
            "wrong-seat post-start retry does not switch or evict the current peer");
    }

    private static void WaitNative(MatchRuntimeNetwork network,
        IReadOnlyList<LeagueEnetHost> clients, Func<bool> done,
        List<LeagueEnetEvent>? capturedLastClient = null, bool productionLoop = false)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done())
        {
            network.Pump(1, 32, flushDelivery: !productionLoop);
            if (productionLoop) network.Tick(1.0 / 30.0);
            for (var index = 0; index < clients.Count; index++)
                Drain(clients[index], index == clients.Count - 1 ? capturedLastClient : null);
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("native reconnect check timed out");
        }
    }

    private static void Drain(LeagueEnetHost host, List<LeagueEnetEvent>? events)
    {
        while (true)
        {
            var @event = host.Service(0);
            if (@event.Type == LeagueEnetEventType.None) return;
            events?.Add(@event);
        }
    }

    /// <summary>
    /// The opcode the client reads: it decrypts every payload of a cipher block or more, so only a shorter one is read raw
    /// (<see cref="DecodeReconnectPayload"/>). Batch 467: the raw first byte of a longer payload is ciphertext, and matching it as an
    /// opcode read a turret's 0xBF BuffAdd (TurretDamageManager, slot 5) whose ciphertext began with 0x4A as a world event.
    /// </summary>
    private static bool HasPlainOpcode(LegacyBlowfishCipher cipher,
        LeagueEnetEvent @event, byte opcode)
    {
        if (@event.Payload.Length == 0) return false;
        try
        {
            var plaintext = DecodeReconnectPayload(cipher, @event);
            return plaintext.Length > 0 && plaintext[0] == opcode;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static byte[] DecodeReconnectPayload(LegacyBlowfishCipher cipher,
        LeagueEnetEvent @event)
    {
        if (@event.Payload.Length < LegacyBlowfish.BlockSize)
            return @event.Payload;
        return cipher.Decrypt(@event.Payload);
    }

    private static MatchRuntime CreateStartedFixture(bool includeSecondOrderSeat = false)
    {
        IReadOnlyList<SessionParticipantDefinition>? roster = includeSecondOrderSeat
            ? [
                new(1, 0, "Player1", "Annie", Team.Order),
                new(2, 1, "Player2", "Orianna", Team.Chaos),
                new(3, 2, "Player3", "Annie", Team.Order),
            ]
            : null;
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        TestAssert.True(session.Authenticate(11, session.BuildKeyCheck(1)) is not null);
        TestAssert.True(session.Authenticate(12, session.BuildKeyCheck(2)) is not null);
        if (includeSecondOrderSeat)
            TestAssert.True(session.Authenticate(13, session.BuildKeyCheck(3)) is not null);
        session.Spawn(11);
        session.Spawn(12);
        if (includeSecondOrderSeat)
            session.Spawn(13);
        session.MarkReady(11);
        session.MarkReady(12);
        if (includeSecondOrderSeat)
            session.MarkReady(13);
        session.BuildMapReadyPackets(11);
        session.BuildMapReadyPackets(12);
        if (includeSecondOrderSeat)
            session.BuildMapReadyPackets(13);
        TestAssert.True(session.Start());
        return runtime;
    }


}
