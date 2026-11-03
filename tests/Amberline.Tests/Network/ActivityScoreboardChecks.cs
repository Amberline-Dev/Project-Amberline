using System.Buffers.Binary;
using System.Text;
using Amberline.Tests;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation;

namespace Amberline.Tests.Network;

/// <summary>Production session coverage for activity, terminal stats and typed owner rows.</summary>
public static class ActivityScoreboardChecks
{
    [Check("session.afk-activity-lifecycle")]
    public static void AfkUsesCombatClockAndAcceptedActivityLatchesHistory()
    {
        var runtime = Start(new[]
        {
            Human(123, 0, "Order", Team.Order, skinId: 1),
            Human(456, 1, "Chaos", Team.Chaos, skinId: 2),
        });
        var owner = runtime.Session.Participants[0];
        var hero = owner.Hero!;
        var start = runtime.Combat.CurrentTime;
        var active = runtime.Session.Participants[1];
        var ownerConnection = owner.ConnectionId!.Value;

        // Playtest #1: owner-only notices at 180 s and 240 s of one idle
        // episode, each once. The other seat stays active throughout.
        TestAssert.False(Poll(179.9).Any(IsAfkNotice), "no notice before three idle minutes");
        var chat = Poll(180.1).Where(IsAfkNotice).ToArray();
        TestAssert.Equal(1, chat.Length, "one chat warning at three idle minutes");
        AssertChatKey(chat[0], ownerConnection, AfkPackets.MoveOrBeKickedKey);
        TestAssert.Equal(1, hero.MatchStatistics.AfkNoticeStage);
        var box = Poll(240.1).Where(IsAfkNotice).ToArray();
        TestAssert.Equal(1, box.Length, "one warning box at four idle minutes");
        TestAssert.Equal(ownerConnection, box[0].ConnectionId, "the warning box goes to the idle owner only");
        TestAssert.Equal("C000000000", Convert.ToHexString(box[0].Packet.Plaintext.Span));
        TestAssert.Equal(LeagueProtocolConstants.CombatChannel, box[0].Packet.Channel);
        TestAssert.True(box[0].Packet.Encrypt && box[0].Packet.Reliability == LeaguePacketReliability.Reliable);
        TestAssert.False(Poll(240.2).Any(IsAfkNotice), "each notice is sent once per idle episode");

        TestAssert.False(Poll(299.9).Any(IsAfkNotice));
        TestAssert.False(hero.MatchStatistics.IsCurrentlyAfk);
        TestAssert.False(hero.MatchStatistics.WasAfk);
        TestAssert.Equal(0, runtime.Session.ConsumeAfkKicks().Count, "no kick before five idle minutes");

        // This command arrives after the five-minute boundary but before the
        // next scheduler poll. The session must evaluate the old idle span,
        // latch history, then clear only the current AFK state.
        runtime.Combat.RestoreClock(start + 300.1);
        var move = ActorPayload(hero);
        TestAssert.True(runtime.Session.DispatchGameplay(owner.ConnectionId!.Value, 1, move, _ => true));
        TestAssert.False(hero.MatchStatistics.IsCurrentlyAfk);
        TestAssert.True(hero.MatchStatistics.WasAfk);
        TestAssert.Equal(0, hero.MatchStatistics.AfkNoticeStage, "accepted activity starts a new idle episode");
        var cancelled = Poll(300.2).Where(IsAfkNotice).ToArray();
        TestAssert.Equal(1, cancelled.Length, "the warned owner sees the cancellation once");
        AssertChatKey(cancelled[0], ownerConnection, AfkPackets.KickCancelledKey);
        TestAssert.Equal(0, runtime.Session.ConsumeAfkKicks().Count, "activity before the poll cancels the kick");

        TestAssert.Equal(1, Poll(480.2).Count(IsAfkNotice), "the new idle episode warns again");
        TestAssert.Equal(1, Poll(600.0).Count(IsAfkNotice), "the new episode's warning box");
        TestAssert.Equal(0, runtime.Session.ConsumeAfkKicks().Count);
        TestAssert.False(Poll(600.2).Any(IsAfkNotice));
        TestAssert.Equal(new[] { ownerConnection }, runtime.Session.ConsumeAfkKicks().ToArray(),
            "300.1 idle seconds request the owner's disconnect");

        // A malformed channel and a forged actor cannot count as activity.
        runtime.Combat.RestoreClock(start + 601.0);
        runtime.Session.RunSchedulers();
        TestAssert.True(hero.MatchStatistics.IsCurrentlyAfk);
        TestAssert.Equal(0, runtime.Session.ConsumeAfkKicks().Count, "the kick is requested once");
        var enemy = runtime.Session.Participants[1].Hero!;
        TestAssert.False(runtime.Session.DispatchGameplay(owner.ConnectionId.Value, 2, move, _ => true));
        var forged = move.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(1), enemy.NetId);
        TestAssert.False(runtime.Session.DispatchGameplay(owner.ConnectionId.Value, 1, forged, _ => true));
        TestAssert.True(hero.MatchStatistics.IsCurrentlyAfk);

        var nexus = runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
        TestAssert.True(runtime.Combat.Kill(nexus));
        TestAssert.False(runtime.Session.DispatchGameplay(owner.ConnectionId.Value, 1, move, _ => true),
            "post-game input is not accepted as activity");
        TestAssert.True(hero.MatchStatistics.IsCurrentlyAfk);

        var prestart = Start(new[]
        {
            Human(123, 0, "Order", Team.Order),
            Human(456, 1, "Chaos", Team.Chaos),
        }, begin: false);
        var prestartHero = prestart.Session.Participants[0].Hero!;
        TestAssert.False(prestart.Session.DispatchGameplay(123, 1,
            ActorPayload(prestartHero), _ => true));
        TestAssert.False(prestartHero.MatchStatistics.WasAfk,
            "pre-start input cannot mutate match activity state");

        IReadOnlyList<SessionOutboundPacket> Poll(double elapsed)
        {
            runtime.Combat.RestoreClock(start + elapsed);
            TestAssert.True(runtime.Session.DispatchGameplay(active.ConnectionId!.Value, 1,
                ActorPayload(active.Hero!), _ => true), "the other seat stays active");
            return runtime.Session.RunSchedulers();
        }
    }

    [Check("network.afk-kick-disconnect-data", Serial = true)]
    public static void AfkKickDisconnectsTheNativePeerWithInactivityData()
    {
        var runtime = Start(new[]
        {
            Human(123, 0, "Idle", Team.Order),
            Human(456, 1, "Away", Team.Chaos),
        });
        var session = runtime.Session;
        foreach (var seat in session.Participants)
            TestAssert.True(session.Disconnect(seat.ConnectionId!.Value));
        using var network = new MatchRuntimeNetwork(runtime, new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1"), ownsHost: true);
        using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        var packets = new List<byte[]>();
        uint? disconnectData = null;
        var idle = session.Participants[0];
        var peer = client.Connect("127.0.0.1", network.Port, 8);
        Wait(() => network.ConnectionIds.Count == 1);
        peer.Send(0, session.BuildKeyCheck(idle.PlayerId), LeagueEnetPacketFlags.Reliable);
        client.Flush(); Wait(() => idle.ConnectionId is not null);
        peer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
        peer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        client.Flush(); Wait(() => idle.Loaded);
        Wait(() => idle.Spawned);
        peer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        client.Flush(); Wait(() => idle.Ready);

        // Reauthentication is activity; the episode starts there.
        var hero = idle.Hero!;
        var activeAt = hero.MatchStatistics.LastActivityAt;
        runtime.Combat.RestoreClock(activeAt + 299.9);
        Wait(() => packets.Any(packet => packet[0] == AfkPackets.OpenWarningMessageOpcode));
        TestAssert.True(disconnectData is null && idle.ConnectionId is not null,
            "no kick before five idle minutes");

        runtime.Combat.RestoreClock(activeAt + 300.1);
        Wait(() => disconnectData is not null);
        TestAssert.Equal(AfkPackets.KickDisconnectData, disconnectData!.Value,
            "the stock client shows its prolonged-inactivity exit for data 6");
        TestAssert.True(idle.ConnectionId is null, "the kicked seat is detached like any disconnect");
        TestAssert.True(hero.MatchStatistics.WasAfk, "the kick keeps the AFK history");
        TestAssert.Equal(1, packets.Count(packet => packet[0] == AfkPackets.LocalizedChatTextOpcode));
        TestAssert.Equal(1, packets.Count(packet => packet[0] == AfkPackets.OpenWarningMessageOpcode));

        void Wait(Func<bool> done)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!done())
            {
                network.Pump(1, 32);
                while (true)
                {
                    var received = client.Service(0);
                    if (received.Type == LeagueEnetEventType.None) break;
                    if (received.Type == LeagueEnetEventType.Disconnect) disconnectData = received.Data;
                    if (received.Type == LeagueEnetEventType.Receive && received.Channel == 3)
                        packets.Add(received.Payload.Length >= 8 ? runtime.Cipher.Decrypt(received.Payload) : received.Payload);
                }
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("AFK kick loopback timeout");
            }
        }
    }

    private static bool IsAfkNotice(SessionOutboundPacket output) =>
        output.Packet.Plaintext.Span[0] is AfkPackets.LocalizedChatTextOpcode or AfkPackets.OpenWarningMessageOpcode;

    private static void AssertChatKey(SessionOutboundPacket output, long connectionId, string key)
    {
        TestAssert.Equal(connectionId, output.ConnectionId, "the chat notice goes to the idle owner only");
        var plaintext = output.Packet.Plaintext.Span;
        TestAssert.Equal(133, plaintext.Length);
        TestAssert.Equal(AfkPackets.LocalizedChatTextOpcode, plaintext[0]);
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(plaintext[1..5]));
        TestAssert.Equal(key, Encoding.ASCII.GetString(plaintext[5..]).TrimEnd('\0'));
        TestAssert.True(plaintext[(5 + key.Length)..].IndexOfAnyExcept((byte)0) < 0, "NUL padded key");
        TestAssert.Equal(LeagueProtocolConstants.CombatChannel, output.Packet.Channel);
        TestAssert.True(output.Packet.Encrypt && output.Packet.Reliability == LeaguePacketReliability.Reliable);
    }

    [Check("session.afk-pause-disconnect-and-bot-policy")]
    public static void AfkFreezesDuringPauseSkipsDetachedSeatsAndExcludesBots()
    {
        var runtime = Start(new[]
        {
            Human(123, 0, "Order", Team.Order),
            Human(456, 1, "Chaos", Team.Chaos),
        });
        var owner = runtime.Session.Participants[0];
        var hero = owner.Hero!;
        var start = runtime.Combat.CurrentTime;
        runtime.Combat.RestoreClock(start + 299.0);
        TestAssert.Equal(2, SendPause(runtime, owner.ConnectionId!.Value, pause: true).Count);
        var pausedAt = runtime.Combat.CurrentTime;
        runtime.Tick(600.0);
        runtime.Session.RunSchedulers();
        TestAssert.Equal(pausedAt, runtime.Combat.CurrentTime);
        TestAssert.False(hero.MatchStatistics.IsCurrentlyAfk,
            "wall time during pause cannot advance the combat-clock AFK policy");
        TestAssert.Equal(2, SendPause(runtime, owner.ConnectionId.Value, pause: false).Count);

        runtime.Tick(5.01);
        runtime.Session.RunSchedulers();
        TestAssert.False(runtime.Session.Paused);
        runtime.Tick(300.0);
        runtime.Session.RunSchedulers();
        TestAssert.True(hero.MatchStatistics.IsCurrentlyAfk,
            "the same 300 seconds count after the match resumes");

        var detachedRuntime = Start(new[]
        {
            Human(123, 0, "Order", Team.Order),
            Human(456, 1, "Chaos", Team.Chaos),
        });
        var detached = detachedRuntime.Session.Participants[0];
        TestAssert.True(detachedRuntime.Session.Disconnect(detached.ConnectionId!.Value));
        detachedRuntime.Combat.RestoreClock(detachedRuntime.Combat.CurrentTime + 600.0);
        detachedRuntime.Session.RunSchedulers();
        TestAssert.False(detached.Hero!.MatchStatistics.IsCurrentlyAfk,
            "a detached seat is excluded from connected-player AFK latching");
        TestAssert.True(detachedRuntime.Session.AuthenticateReconnect(900,
            detachedRuntime.Session.BuildKeyCheck(detached.PlayerId)) is not null);
        TestAssert.False(detached.Hero.MatchStatistics.IsCurrentlyAfk);
        TestAssert.False(detached.Hero.MatchStatistics.WasAfk);

        var boundaryRuntime = Start(new[]
        {
            Human(123, 0, "Boundary", Team.Order),
            Human(456, 1, "Chaos", Team.Chaos),
        });
        var boundary = boundaryRuntime.Session.Participants[0];
        boundaryRuntime.Combat.RestoreClock(boundaryRuntime.Combat.CurrentTime + 300.1);
        TestAssert.True(boundaryRuntime.Session.Disconnect(boundary.ConnectionId!.Value));
        TestAssert.True(boundary.Hero!.MatchStatistics.WasAfk,
            "disconnect just after the AFK threshold latches history before detachment");
        boundaryRuntime.Combat.RestoreClock(boundaryRuntime.Combat.CurrentTime + 600.0);
        boundaryRuntime.Session.RunSchedulers();
        TestAssert.True(boundary.Hero.MatchStatistics.WasAfk,
            "detached time cannot erase a latched AFK history");

        var mixed = Start(new[]
        {
            Human(123, 0, "Human", Team.Order),
            new SessionParticipantDefinition(456, 1, "Bot", "Annie", Team.Order, IsBot: true),
            Human(789, 2, "Enemy", Team.Chaos),
        });
        mixed.Combat.RestoreClock(mixed.Combat.CurrentTime + 300.0);
        mixed.Session.RunSchedulers();
        TestAssert.True(mixed.Session.Participants.Single(seat => seat.PlayerId == 123)
            .Hero!.MatchStatistics.IsCurrentlyAfk);
        TestAssert.False(mixed.Session.Participants.Single(seat => seat.IsBot)
            .Hero!.MatchStatistics.IsCurrentlyAfk, "server-owned bots are not AFK players");
        var botPause = PausePackets.Pause(uint.MaxValue, 1).Plaintext.Span;
        TestAssert.Equal(0, mixed.Session.Receive(456, 1,
            NativeRequestWire.Encrypt(mixed.Cipher, botPause)).Count,
            "a server-owned bot has no pause authority or transport identity");
        TestAssert.False(mixed.Session.Paused);
    }

    [Check("protocol.classic-hero-stats-owner-privacy")]
    public static void OwnerRowsCarryTypedPoliciesWhileForeignRowsStayPublic()
    {
        var runtime = Start(new[]
        {
            Human(123, 0, "Owner", Team.Order, skinId: 1),
            Human(456, 1, "Foreign", Team.Chaos, skinId: 2),
        });
        foreach (var participant in runtime.Session.Participants)
            participant.Loaded = true;
        var owner = runtime.Session.Participants[0];
        var hero = owner.Hero!;
        hero.MatchStatistics.RecordSuperMonsterKill();
        hero.MatchStatistics.RecordDisconnect(runtime.Combat.CurrentTime);
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 300.0);
        runtime.Session.RunSchedulers();

        var request = new byte[5];
        request[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        var rows = runtime.Session.BuildClassicHeroStatsRefreshBurst(owner.ConnectionId!.Value, request)
            .Where(packet => packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode)
            .ToArray();
        TestAssert.Equal(2, rows.Length);
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var own = Amberline.Tests.ClassicHeroStatsChecks.Decode(rows.Single(row =>
            BinaryPrimitives.ReadUInt32LittleEndian(row.Plaintext.Span[1..5]) == hero.NetId)
            .Plaintext.Span, schema);
        var foreign = Amberline.Tests.ClassicHeroStatsChecks.Decode(rows.Single(row =>
            BinaryPrimitives.ReadUInt32LittleEndian(row.Plaintext.Span[1..5]) != hero.NetId)
            .Plaintext.Span, schema);
        TestAssert.Equal(123L, (long)own["ID"]);
        TestAssert.Equal(1, (int)own["SKIN"]);
        TestAssert.Equal(1, (int)own["SUPER_MONSTER_KILLED"]);
        TestAssert.Equal(1, (int)own["WAS_AFK"]);
        TestAssert.Near(300.0, (double)own["TIME_OF_FROM_LAST_DISCONNECT"], 1e-6);
        TestAssert.Equal(string.Empty, (string)own["WIN"]);
        TestAssert.Equal(0L, (long)foreign["ID"]);
        TestAssert.Equal(0, (int)foreign["SKIN"]);
        TestAssert.Equal(0, (int)foreign["SUPER_MONSTER_KILLED"]);
        TestAssert.Equal(0, (int)foreign["WAS_AFK"]);
        TestAssert.Equal(0.0, (double)foreign["TIME_OF_FROM_LAST_DISCONNECT"]);
        TestAssert.Equal(string.Empty, (string)foreign["WIN"]);
    }

    [Check("session.afk-terminal-result-and-reconnect")]
    public static void TerminalResultLatchesAfkAndOwnerTerminalRowsPreserveWinner()
    {
        var runtime = Start(new[]
        {
            Human(123, 0, "Winner", Team.Order),
            Human(456, 1, "Loser", Team.Chaos),
        });
        var owner = runtime.Session.Participants[0];
        var captures = runtime.Session.Participants.ToDictionary(participant => participant.PlayerId,
            _ => new Capture());
        foreach (var participant in runtime.Session.Participants)
            runtime.Delivery.AddObserver(new ReplicationObserver(participant.PlayerId,
                participant.Team, participant.Hero!.NetId, captures[participant.PlayerId], ready: true,
                view: participant.View));
        var lastActivity = runtime.Combat.CurrentTime + 299.0;
        runtime.Combat.RestoreClock(lastActivity);
        var loser = runtime.Session.Participants[1];
        TestAssert.True(runtime.Session.DispatchGameplay(loser.ConnectionId!.Value, 1,
            ActorPayload(loser.Hero!), _ => true));
        runtime.Combat.RestoreClock(lastActivity + 1.0);
        runtime.Session.RunSchedulers();
        var nexus = runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
        TestAssert.True(runtime.Combat.Kill(nexus));
        var result = runtime.Session.Result ?? throw new InvalidOperationException("missing match result");
        TestAssert.True(result.Players.Single(player => player.PlayerId == 123).WasAfk);
        TestAssert.False(result.Players.Single(player => player.PlayerId == 456).WasAfk);

        runtime.Delivery.Flush();
        var terminal = captures.Values.SelectMany(capture => capture.Packets)
            .Where(packet => packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode)
            .ToArray();
        TestAssert.Equal(2, terminal.Length, "one terminal owner row per connected human");
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var ownerTerminalTime = 0.0;
        foreach (var packet in terminal)
        {
            var heroNetId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]);
            var participant = runtime.Session.Participants.Single(seat => seat.Hero!.NetId == heroNetId);
            var values = Amberline.Tests.ClassicHeroStatsChecks.Decode(packet.Plaintext.Span, schema);
            TestAssert.Equal(participant.Team == Team.Order ? "Win" : "Loss", (string)values["WIN"]);
            TestAssert.Equal(participant.PlayerId == 123 ? 1 : 0, (int)values["WAS_AFK"]);
            if (participant.PlayerId == 123)
                ownerTerminalTime = (double)values["TIME_PLAYED"];
            TestAssert.True(captures[participant.PlayerId].Packets.Contains(packet));
            TestAssert.Equal(1, captures[participant.PlayerId].Packets.Count(candidate =>
                candidate.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(candidate.Plaintext.Span[1..5]) == heroNetId),
                "foreign observers do not receive the private terminal row");
        }

        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 60.0);
        var oldConnection = owner.ConnectionId!.Value;
        TestAssert.True(runtime.Session.Disconnect(oldConnection),
            "terminal reconnect starts from a detached transport");
        var reconnected = runtime.Session.AuthenticateReconnect(900,
            runtime.Session.BuildKeyCheck(owner.PlayerId));
        TestAssert.True(reconnected is not null,
            "terminal reconnect authenticates the known roster seat");
        var handshake = runtime.Session.Receive(900, 1, [0xC5]);
        TestAssert.True(handshake.Any(output => output.Packet.Plaintext.Span[0] == 0x57),
            "terminal reconnect receives the staged version handshake");
        runtime.Session.Receive(900, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = reconnected!.SpawnAt ??
            throw new InvalidOperationException("terminal reconnect did not arm spawn");
        // The reconnect scheduler is driven by the world clock. Keep the
        // combat clock at its post-result value so terminal TIME_PLAYED stays
        // anchored while the staged spawn boundary advances.
        runtime.World.Clock.SetVirtual(spawnAt);
        var baseline = runtime.Session.RunSchedulers();
        TestAssert.False(baseline.Any(output => output.Packet.Plaintext.Span[0] == 0x4B),
            "terminal reconnect defers Classic rows until the ready stage");
        runtime.Session.Receive(900, 1, [0xA4, 0, 0, 0, 0, 1]);
        runtime.Session.Receive(900, 1, [0xC6, 0, 0, 0, 0]);
        runtime.Session.RunSchedulers();
        TestAssert.True(reconnected.Spawned && reconnected.Loaded && reconnected.Ready,
            "terminal reconnect reaches the ready boundary");
        var reconnect = runtime.Session.BuildReconnectMatchState(900);
        var reconnectValues = Amberline.Tests.ClassicHeroStatsChecks.Decode(
            reconnect.Single(packet => packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode)
                .Plaintext.Span, schema);
        TestAssert.Equal("Win", (string)reconnectValues["WIN"]);
        TestAssert.Equal(1, (int)reconnectValues["WAS_AFK"]);
        TestAssert.Near(ownerTerminalTime, (double)reconnectValues["TIME_PLAYED"], 1e-6,
            "terminal reconnect rows stay anchored to the captured result clock");

        AssertDisconnectedTerminalSnapshotFreezes();
    }

    private static void AssertDisconnectedTerminalSnapshotFreezes()
    {
        var runtime = Start(new[]
        {
            Human(123, 0, "Winner", Team.Order),
            Human(456, 1, "Loser", Team.Chaos),
        });
        var owner = runtime.Session.Participants[0];
        var foreign = runtime.Session.Participants[1];
        var ownerNetId = owner.Hero!.NetId;
        var foreignCapture = new Capture();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            foreign.PlayerId, foreign.Team, foreign.Hero!.NetId, foreignCapture,
            ready: true, view: foreign.View));

        var start = runtime.Combat.CurrentTime;
        runtime.Combat.RestoreClock(start + 240.0);
        TestAssert.True(runtime.Session.Disconnect(owner.ConnectionId!.Value),
            "owner detaches before result capture");
        runtime.Combat.RestoreClock(start + 300.0);
        var nexus = runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.Nexus && unit.Team == Team.Chaos);
        TestAssert.True(runtime.Combat.Kill(nexus));
        TestAssert.True(runtime.Session.Result is not null);
        runtime.Delivery.Flush();
        TestAssert.False(foreignCapture.Packets.Any(packet =>
            packet.Plaintext.Length >= 5 &&
            packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == ownerNetId),
            "a detached owner's terminal row is not delivered to a foreign observer");

        runtime.Combat.RestoreClock(start + 360.0);
        var reconnected = runtime.Session.AuthenticateReconnect(900,
            runtime.Session.BuildKeyCheck(owner.PlayerId));
        TestAssert.True(reconnected is not null);
        TestAssert.True(runtime.Session.Receive(900, 1, [0xC5]).Any(output =>
            output.Packet.Plaintext.Span[0] == 0x57));
        runtime.Session.Receive(900, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = reconnected!.SpawnAt ??
            throw new InvalidOperationException("detached terminal reconnect did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        runtime.Session.RunSchedulers();
        runtime.Session.Receive(900, 1, [0xA4, 0, 0, 0, 0, 1]);
        runtime.Session.Receive(900, 1, [0xC6, 0, 0, 0, 0]);
        runtime.Session.RunSchedulers();

        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var first = runtime.Session.BuildReconnectMatchState(900)
            .Single(packet => packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode);
        var firstValues = Amberline.Tests.ClassicHeroStatsChecks.Decode(
            first.Plaintext.Span, schema);
        TestAssert.Near(60.0, (double)firstValues["TIME_OF_FROM_LAST_DISCONNECT"], 1e-6,
            "detached terminal row freezes disconnect duration at result capture");
        TestAssert.Near(60.0, (double)firstValues["TIME_SPENT_DISCONNECTED"], 1e-6,
            "detached terminal row freezes cumulative disconnect time at result capture");
        TestAssert.Equal("Win", (string)firstValues["WIN"]);

        runtime.Combat.RestoreClock(start + 480.0);
        runtime.World.Clock.SetVirtual(spawnAt + 120.0);
        var later = runtime.Session.BuildReconnectMatchState(900)
            .Single(packet => packet.Plaintext.Span[0] == ClassicHeroStatsPackets.Opcode);
        TestAssert.True(first.Plaintext.Span.SequenceEqual(later.Plaintext.Span),
            "later reconnect clocks cannot mutate the captured owner row");
    }

    private static SessionParticipantDefinition Human(ulong playerId, uint clientId,
        string name, Team team, int skinId = 0) =>
        new(playerId, clientId, name, "Annie", team, SkinId: skinId);

    private static MatchRuntime Start(IReadOnlyList<SessionParticipantDefinition> roster,
        bool begin = true)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        foreach (var participant in runtime.Session.Participants.Where(seat => !seat.IsBot))
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck(participant.PlayerId)) is not null);
            runtime.Session.Spawn(connection);
            runtime.Session.MarkReady(connection);
            participant.Loaded = true;
            participant.Hero!.HeroAutoAcquire = false;
        }
        if (begin)
        {
            TestAssert.True(runtime.Session.RunSchedulers().Any(output =>
                output.Packet.Plaintext.Span[0] == 0x65) || runtime.Session.Started,
                "production session reaches the map-ready/start boundary");
            TestAssert.True(runtime.Session.Started);
        }
        return runtime;
    }

    private static byte[] ActorPayload(Entity hero)
    {
        var payload = new byte[5];
        payload[0] = 0x3E;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), hero.NetId);
        return payload;
    }

    private static IReadOnlyList<SessionOutboundPacket> SendPause(
        MatchRuntime runtime, long connection, bool pause)
    {
        var packet = pause ? PausePackets.Pause(uint.MaxValue, 1) :
            PausePackets.Resume(uint.MaxValue, false);
        return runtime.Session.Receive(connection, 1,
            NativeRequestWire.Encrypt(runtime.Cipher, packet.Plaintext.Span));
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
