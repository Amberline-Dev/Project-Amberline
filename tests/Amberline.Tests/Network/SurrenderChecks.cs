using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Server;

namespace Amberline.Tests.Network;

public static class SurrenderChecks
{
    private const double StartOffset = 23;

    [Check("protocol.surrender-stock-layouts")]
    public static void StockLayoutsAndShortRequests()
    {
        var request = Request(true);
        request[1] = 0xFE;
        request[5] = 0xFF;
        TestAssert.True(SurrenderPackets.TryReadVote(request, out var yes) && yes,
            "header is not the voter and high flag bits are native padding");
        request[5] = 0xFE;
        TestAssert.True(SurrenderPackets.TryReadVote(request, out yes) && !yes);
        foreach (var length in new[] { 0, 1, 5, 7, 8, 16 })
            TestAssert.False(SurrenderPackets.TryReadVote(new byte[length], out _));
        request[0] = 0xAD;
        TestAssert.False(SurrenderPackets.TryReadVote(request, out _));

        var packet = SurrenderPackets.Vote(Team.Chaos, 0x11223344, true, true, 2, 1, 5, 45.5);
        TestAssert.Equal("D2000000000344332211020105C800000000003642", Convert.ToHexString(packet.Plaintext.Span));
        TestAssert.Equal(LeagueProtocolConstants.CombatChannel, packet.Channel);
        TestAssert.True(packet.Encrypt);
        var status = SurrenderPackets.Status(Team.Order, SurrenderStatus.Agreed, 4, 1);
        TestAssert.Equal("AD0000000004000000040164000000", Convert.ToHexString(status.Plaintext.Span));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SurrenderPackets.Vote(Team.Order, 1, true, true, 4, 2, 5, 60));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SurrenderPackets.Vote(Team.Order, 1, true, true, 1, 0, 5, double.NaN));
    }

    [Check("session.surrender-vote-lifecycle")]
    public static void AuthenticationTimingVotesAndTimeouts()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = Create(5, start: false);
            var session = runtime.Session;
            var seats = TeamSeats(runtime, team);
            var first = seats[0].ConnectionId!.Value;
            TestAssert.Equal(0, session.Receive(first, 1, Request(true)).Count, "loading cannot start votes");
            TestAssert.True(session.Start());
            TestAssert.Equal(0, session.Receive(900, 1, Request(true)).Count, "unknown connection");
            TestAssert.Equal(0, session.Receive(first, 2, Request(true)).Count, "wrong channel");
            TestAssert.Equal(0, Vote(runtime, first, false).Count, "no vote cannot open a ballot");
            At(runtime, 1199.75);
            AssertStatus(Vote(runtime, first, true).Single().Packet, SurrenderStatus.TooEarly, team, 0, 0);
            // Wall time is independent: only the simulated match clock opens voting.
            runtime.World.Clock.SetVirtual(999999);
            AssertStatus(Vote(runtime, first, true).Single().Packet, SurrenderStatus.TooEarly, team, 0, 0);
            At(runtime, 1200);
            seats[0].Ready = false;
            TestAssert.Equal(0, Vote(runtime, first, true).Count);
            seats[0].Ready = true;
            var forgedHeader = Request(true);
            BinaryPrimitives.WriteUInt32LittleEndian(forgedHeader.AsSpan(1), seats[1].Hero!.NetId);
            var started = session.Receive(first, 1, forgedHeader);
            AssertTeamRecipients(started, seats);
            foreach (var output in started)
                AssertVote(output.Packet, team, seats[0].Hero!.NetId, 3, 1, 0, 5, 60);
            var duplicate = Vote(runtime, first, false);
            TestAssert.Equal(first, duplicate.Single().ConnectionId);
            AssertStatus(duplicate.Single().Packet, SurrenderStatus.AlreadyVoted, team, 1, 0);

            Vote(runtime, seats[1].ConnectionId!.Value, false);
            var secondNo = Vote(runtime, seats[2].ConnectionId!.Value, false);
            TestAssert.True(secondNo.All(output => output.Packet.Plaintext.Span[0] == 0xD2),
                "no modern early failure when the remaining votes cannot pass");
            At(runtime, 1259.75);
            TestAssert.False(session.RunSchedulers().Any(output => output.Packet.Plaintext.Span[0] == 0xAD));
            At(runtime, 1260);
            var failed = session.RunSchedulers().Where(output => output.Packet.Plaintext.Span[0] == 0xAD).ToArray();
            AssertTeamRecipients(failed, seats);
            foreach (var output in failed) AssertStatus(output.Packet, SurrenderStatus.Failed, team, 1, 4);
            TestAssert.False(session.RunSchedulers().Any(output => output.Packet.Plaintext.Span[0] == 0xAD), "timeout emits once");
            At(runtime, 1439.75);
            AssertStatus(Vote(runtime, first, true).Single().Packet, SurrenderStatus.Cooldown, team, 0, 0);
            At(runtime, 1440);
            TestAssert.True(Vote(runtime, first, true).All(output => output.Packet.Plaintext.Span[0] == 0xD2));
            // A full failed ballot resolves immediately, and its cooldown starts then.
            foreach (var seat in seats.Skip(1)) Vote(runtime, seat.ConnectionId!.Value, false);
            At(runtime, 1619.75);
            AssertStatus(Vote(runtime, first, true).Single().Packet, SurrenderStatus.Cooldown, team, 0, 0);
            At(runtime, 1620);
            TestAssert.Equal(5, Vote(runtime, first, true).Count);
        }
    }

    [Check("session.surrender-rosters-and-completion")]
    public static void EverySupportedTeamSizeAndWinnerUsesTheCeremony()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var size in Enumerable.Range(1, 5))
        {
            var runtime = Create(size);
            At(runtime, 1200);
            var captures = runtime.Session.Participants.Select(seat =>
            {
                var capture = new Capture();
                runtime.Delivery.AddObserver(new ReplicationObserver(seat.PlayerId, seat.Team,
                    seat.Hero!.NetId, capture, ready: true, view: seat.View));
                return capture;
            }).ToArray();
            runtime.Delivery.Flush();
            foreach (var capture in captures) capture.Packets.Clear();
            var seats = TeamSeats(runtime, team);
            var required = (size * 7 + 9) / 10;
            var stats = runtime.Session.Participants.Select(seat => (seat.Hero!.Gold, seat.Hero.Experience)).ToArray();
            var outputs = Array.Empty<SessionOutboundPacket>();
            for (var index = 0; index < required; index++)
            {
                TestAssert.False(runtime.Combat.GameOver);
                outputs = Vote(runtime, seats[index].ConnectionId!.Value, true).ToArray();
            }
            TestAssert.True(runtime.Combat.GameOver && runtime.Combat.Surrendered);
            TestAssert.Equal(size * 2, outputs.Count(output => output.Packet.Plaintext.Span[0] == 0xAD),
                "successful surrender is announced to both teams");
            foreach (var output in outputs.Where(output => output.Packet.Plaintext.Span[0] == 0xAD))
                AssertStatus(output.Packet, SurrenderStatus.Agreed, team, required, 0);
            TestAssert.Equal(stats, runtime.Session.Participants.Select(seat => (seat.Hero!.Gold, seat.Hero.Experience)).ToArray());
            TestAssert.True(runtime.Session.Result!.Players.All(player =>
                player.NexusesKilled == 0 && player.Kills == 0 && player.Assists == 0));
            TestAssert.True(runtime.Session.Result.Surrendered);
            TestAssert.Near(1200, runtime.Session.Result.DurationSeconds, 1e-7);
            var winner = team == Team.Order ? Team.Chaos : Team.Order;
            TestAssert.Equal(winner.ToString(), runtime.Session.Result.Winner);
            var losingNexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == team);
            var winningNexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == winner);
            TestAssert.False(losingNexus.Alive);
            TestAssert.True(winningNexus.Alive);
            TestAssert.False(runtime.Combat.Surrender(winner));
            TestAssert.False(runtime.Combat.Kill(winningNexus, seats[0].Hero!.NetId), "winner cannot reverse");
            TestAssert.Equal(0, Vote(runtime, seats[0].ConnectionId!.Value, true).Count);
            runtime.Delivery.Flush();
            runtime.Combat.CombatTick(8.5, regenerate: false);
            runtime.Delivery.Flush();
            foreach (var capture in captures)
            {
                var finish = capture.Packets.Single(packet => packet.Plaintext.Span[0] == 0xCE);
                TestAssert.Equal((byte)(winner == Team.Order ? 3 : 2), finish.Plaintext.Span[5]);
                TestAssert.Equal(1, capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0xCD));
                var fade = capture.Packets.Single(packet => packet.Plaintext.Span[0] == 0xD4);
                TestAssert.Equal(team == Team.Order ? (byte)100 : (byte)200, fade.Plaintext.Span[5]);
            }
            runtime.Combat.CombatTick(20, regenerate: false);
            runtime.Delivery.Flush();
            TestAssert.True(captures.All(capture => capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0xCE) == 1));
        }
    }

    [Check("session.surrender-reconnect-and-independent-teams")]
    public static void ReconnectRestoresBallotWithoutChangingEligibility()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var runtime = Create(5);
            var session = runtime.Session;
            var seats = TeamSeats(runtime, team);
            var enemies = TeamSeats(runtime, team == Team.Order ? Team.Chaos : Team.Order);
            At(runtime, 1200);
            Vote(runtime, seats[0].ConnectionId!.Value, true);
            Vote(runtime, seats[1].ConnectionId!.Value, false);
            At(runtime, 1210);
            var independent = Vote(runtime, enemies[0].ConnectionId!.Value, true);
            AssertTeamRecipients(independent, enemies);
            TestAssert.True(session.Disconnect(seats[0].ConnectionId!.Value));
            TestAssert.True(session.AuthenticateReconnect(101, session.BuildKeyCheck(seats[0].PlayerId)) is not null);
            TestAssert.Equal(0, Vote(runtime, 101, true).Count, "fresh transport must finish loading");
            TestAssert.Equal(0, session.BuildReconnectMatchState(101).Count);
            var ready = FinishReconnect(runtime, seats[0]);
            TestAssert.False(ready.Any(output => output.Packet.Plaintext.Span[0] == 0xD2), "vote replay waits for visibility flush");
            var restored = session.BuildReconnectMatchState(101).ToArray();
            var restoredClassic = restored.Where(packet =>
                packet.Plaintext.Span[0] == 0x4B).ToArray();
            var expectedHeroIds = session.Participants
                .Select(participant => participant.Hero!.NetId)
                .ToHashSet();
            TestAssert.Equal(expectedHeroIds.Count + 2, restored.Length,
                "reconnect restores only the full known roster and two ballot rows");
            TestAssert.Equal(expectedHeroIds.Count, restoredClassic.Length,
                "reconnect restores one Classic row for every known hero");
            foreach (var heroId in expectedHeroIds)
                TestAssert.Equal(1, restoredClassic.Count(packet =>
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == heroId),
                    "reconnect restores each known hero exactly once");
            var restoredOwner = restoredClassic.Single(packet =>
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == seats[0].Hero!.NetId);
            TestAssert.Equal((byte)0x4B, restoredOwner.Plaintext.Span[0],
                "reconnect restores the requesting owner's full Classic snapshot");
            var restoredVotes = restored.Where(packet => packet.Plaintext.Span[0] == 0xD2).ToArray();
            TestAssert.Equal(2, restoredVotes.Length,
                "reconnect restores the ballot after all Classic rows");
            var firstVoteIndex = Array.FindIndex(restored,
                packet => packet.Plaintext.Span[0] == 0xD2);
            var lastClassicIndex = Array.FindLastIndex(restored,
                packet => packet.Plaintext.Span[0] == 0x4B);
            TestAssert.True(lastClassicIndex >= 0 && lastClassicIndex < firstVoteIndex,
                "reconnect restores every Classic row before the ballot");
            AssertVote(restoredVotes[0], team, seats[0].Hero!.NetId, 3, 1, 1, 5, 50);
            AssertVote(restoredVotes[1], team, seats[1].Hero!.NetId, 0, 1, 1, 5, 50);
            AssertStatus(Vote(runtime, 101, false).Single().Packet, SurrenderStatus.AlreadyVoted, team, 1, 1);
            TestAssert.True(session.Disconnect(seats[4].ConnectionId!.Value));
            Vote(runtime, seats[2].ConnectionId!.Value, true);
            Vote(runtime, seats[3].ConnectionId!.Value, true);
            TestAssert.False(runtime.Combat.GameOver, "three yes votes do not shrink a fixed five-seat ballot after disconnect");
            At(runtime, 1260);
            var failed = session.RunSchedulers().Where(output => output.Packet.Plaintext.Span[0] == 0xAD).ToArray();
            TestAssert.Equal(4, failed.Length);
            foreach (var output in failed) AssertStatus(output.Packet, SurrenderStatus.Failed, team, 3, 2);
            At(runtime, 1270);
            var enemyFailed = session.RunSchedulers().Where(output => output.Packet.Plaintext.Span[0] == 0xAD).ToArray();
            AssertTeamRecipients(enemyFailed, enemies);
            foreach (var output in enemyFailed) AssertStatus(output.Packet, SurrenderStatus.Failed, enemies[0].Team, 1, 4);
        }
    }

    [Check("server.surrender-result-and-finished-reconnect")]
    public static void SavedCauseAndReconnectAfterEitherMatchEnding()
    {
        foreach (var surrendered in new[] { false, true })
        foreach (var loser in new[] { Team.Order, Team.Chaos })
        {
            var root = Path.Combine(Path.GetTempPath(), "amberline-surrender-" + Guid.NewGuid().ToString("N"));
            using var log = ServerLogger.Open(root, new StringWriter());
            var runtime = Create(1, log: log);
            var seat = TeamSeats(runtime, loser)[0];
            At(runtime, 1200);
            if (surrendered) Vote(runtime, seat.ConnectionId!.Value, true);
            else TestAssert.True(runtime.Combat.Kill(runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.Nexus && unit.Team == loser)));
            var path = Path.Combine(Directory.GetDirectories(root).Single(), "match-result.json");
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            TestAssert.Equal(surrendered, saved.RootElement.GetProperty("result").GetProperty("Surrendered").GetBoolean());
            var result = runtime.Session.Result;
            runtime.Combat.CombatTick(10, regenerate: false);
            runtime.Session.Disconnect(seat.ConnectionId!.Value);
            var loading = runtime.Session.Receive(101, 0, runtime.Session.BuildKeyCheck(seat.PlayerId));
            TestAssert.False(loading.Any(output => output.Packet.Plaintext.Span[0] == 0xCE),
                "a restarted process must load before receiving terminal HUD packets");
            var ready = FinishReconnect(runtime, seat);
            TestAssert.False(ready.Any(output => output.Packet.Plaintext.Span[0] == 0xCE));
            var finishPackets = runtime.Session.BuildReconnectMatchState(101).ToArray();
            var finish = finishPackets.Single(packet => packet.Plaintext.Span[0] == 0xCE);
            var terminal = finishPackets.Single(packet => packet.Plaintext.Span[0] == 0x4B);
            TestAssert.True(finishPackets.ToList().IndexOf(terminal) < finishPackets.ToList().IndexOf(finish),
                "owner terminal Classic row precedes the EndGame ceremony on reconnect");
            TestAssert.Equal((byte)((loser == Team.Chaos ? 1 : 0) | (surrendered ? 2 : 0)), finish.Plaintext.Span[5]);
            var terminalValues = ClassicHeroStatsChecks.Decode(terminal.Plaintext.Span,
                ClassicHeroStatsSchema.Load(GameDataPaths.Default));
            // This reconnect is for the surrendered/destroyed owner's seat.  The
            // owner is the losing side in both loser branches; WIN is therefore
            // always the terminal owner's Loss value.
            TestAssert.Equal("Loss", (string)terminalValues["WIN"]);
            TestAssert.Equal(1, (int)terminalValues["WAS_AFK"],
                "idle terminal evaluation latches the connected owner's AFK history");
            TestAssert.True(ReferenceEquals(result, runtime.Session.Result));
            TestAssert.False(runtime.Session.RunSchedulers().Any(output => output.Packet.Plaintext.Span[0] == 0xCE));
        }
    }

    [Check("network.surrender-late-reconnect-packets", Serial = true)]
    public static void NativeReconnectOrdersVoteAndFinishAfterWorldAndSpellSnapshots()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ending in new[] { "active", "surrender", "nexus" })
        {
            var runtime = Create(5);
            At(runtime, 1200);
            var seats = TeamSeats(runtime, team);
            Vote(runtime, seats[0].ConnectionId!.Value, true);
            if (ending == "surrender")
                foreach (var seat in seats.Skip(1).Take(3)) Vote(runtime, seat.ConnectionId!.Value, true);
            else if (ending == "nexus")
                runtime.Combat.Kill(runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == team));
            if (ending != "active") runtime.Combat.CombatTick(9, regenerate: false);
            foreach (var seat in runtime.Session.Participants) runtime.Session.Disconnect(seat.ConnectionId!.Value);
            runtime.Delivery.Flush();
            using var network = new MatchRuntimeNetwork(runtime, new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1"), ownsHost: true);
            using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
            var packets = new List<byte[]>();
            var peer = client.Connect("127.0.0.1", network.Port, 8);
            Wait(() => network.ConnectionIds.Count == 1);
            peer.Send(0, runtime.Session.BuildKeyCheck(seats[0].PlayerId), LeagueEnetPacketFlags.Reliable);
            client.Flush();
            Wait(() => seats[0].ConnectionId is not null);
            TestAssert.False(packets.Any(packet => packet[0] is 0xD2 or 0xCE), "no early HUD packets during authentication");
            peer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
            peer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            client.Flush();
            Wait(() => seats[0].Loaded);
            Wait(() => seats[0].Spawned);
            TestAssert.False(packets.Any(packet => packet[0] is 0xD2 or 0xCE), "the vote/result UI waits for the pass after the burst");
            peer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
            client.Flush();
            var opcode = ending == "active" ? (byte)0xD2 : (byte)0xCE;
            Wait(() => packets.Any(packet => packet[0] == opcode));
            var late = packets.FindIndex(packet => packet[0] == opcode);
            var hero = seats[0].Hero!;
            var reveal = packets.FindIndex(packet => packet.Length >= 5 && packet[0] == 0xC2 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == hero.NetId);
            var rank = packets.FindIndex(packet => packet.Length >= 5 && packet[0] == 0x18 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) == hero.NetId);
            TestAssert.True(reveal >= 0 && rank > reveal && late > rank,
                "native transport restores the hero and spell book before vote/result UI");
            if (ending == "active")
            {
                TestAssert.Equal((byte)3, packets[late][5]);
                TestAssert.Equal(team == Team.Order ? 100 : 200, BinaryPrimitives.ReadInt32LittleEndian(packets[late].AsSpan(13)));
                TestAssert.Equal(60f, BinaryPrimitives.ReadSingleLittleEndian(packets[late].AsSpan(17)));
            }
            else TestAssert.Equal((byte)((team == Team.Chaos ? 1 : 0) | (ending == "surrender" ? 2 : 0)), packets[late][5]);
            TestAssert.Equal(1, packets.Count(packet => packet[0] == opcode));

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
                        if (received.Type != LeagueEnetEventType.Receive || received.Channel != 3) continue;
                        packets.Add(received.Payload.Length >= 8 ? runtime.Cipher.Decrypt(received.Payload) : received.Payload);
                    }
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("late reconnect packet timeout: " + ending);
                }
            }
        }
    }

    private static MatchRuntime Create(int teamSize, bool start = true, ISessionLogSink? log = null)
    {
        var roster = Enumerable.Range(0, teamSize * 2).Select(index => new SessionParticipantDefinition(
            (ulong)index + 1, (uint)index, $"Vote {index + 1}", "Annie", index % 2 == 0 ? Team.Order : Team.Chaos)).ToArray();
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), roster: roster,
            readySettleSeconds: 0, sessionLog: log);
        runtime.World.Clock.SetVirtual(500);
        foreach (var seat in runtime.Session.Participants)
        {
            var connection = (long)seat.PlayerId;
            TestAssert.True(runtime.Session.Authenticate(connection, runtime.Session.BuildKeyCheck(seat.PlayerId)) is not null);
            runtime.Session.Spawn(connection);
            runtime.Session.MarkReady(connection);
            seat.Hero!.HeroAutoAcquire = false;
        }
        runtime.Combat.CombatTick(StartOffset, regenerate: false);
        if (start) TestAssert.True(runtime.Session.Start());
        return runtime;
    }

    private static IReadOnlyList<SessionOutboundPacket> FinishReconnect(MatchRuntime runtime, SessionParticipant seat)
    {
        runtime.Session.Receive(seat.ConnectionId!.Value, 1, new byte[] { 0xC5, 0, 0, 0, 0 });
        seat.Loaded = true;
        runtime.World.Clock.SetVirtual(runtime.World.Clock.Monotonic() + 3);
        // The burst goes out on the first pass and the seat is made ready on the next one.
        var burst = runtime.Session.RunSchedulers();
        var ready = runtime.Session.RunSchedulers();
        return [.. burst, .. ready];
    }

    private static SessionParticipant[] TeamSeats(MatchRuntime runtime, Team team) =>
        runtime.Session.Participants.Where(seat => seat.Team == team).ToArray();
    private static byte[] Request(bool yes) => [0xAC, 0, 0, 0, 0, (byte)(yes ? 1 : 0)];
    private static IReadOnlyList<SessionOutboundPacket> Vote(MatchRuntime runtime, long connection, bool yes) =>
        runtime.Session.Receive(connection, 1, Request(yes));
    private static void At(MatchRuntime runtime, double elapsed) =>
        runtime.Combat.CombatTick(StartOffset + elapsed - runtime.Combat.CurrentTime, regenerate: false);
    private static void AssertTeamRecipients(IEnumerable<SessionOutboundPacket> packets, SessionParticipant[] seats) =>
        TestAssert.Equal(seats.Select(seat => seat.ConnectionId!.Value).Order().ToArray(),
            packets.Select(packet => packet.ConnectionId).Order().ToArray());
    private static void AssertStatus(PlaintextPacket packet, SurrenderStatus status, Team team, int yes, int no)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(15, bytes.Length);
        TestAssert.Equal((byte)0xAD, bytes[0]);
        TestAssert.Equal((int)status, BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]));
        TestAssert.Equal((byte)yes, bytes[9]);
        TestAssert.Equal((byte)no, bytes[10]);
        TestAssert.Equal(team == Team.Order ? 100 : 200, BinaryPrimitives.ReadInt32LittleEndian(bytes[11..]));
    }
    private static void AssertVote(PlaintextPacket packet, Team team, uint voter, byte flags,
        int yes, int no, int members, float remaining)
    {
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(21, bytes.Length);
        TestAssert.Equal((byte)0xD2, bytes[0]);
        TestAssert.Equal(flags, bytes[5]);
        TestAssert.Equal(voter, BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]));
        TestAssert.Equal((byte)yes, bytes[10]);
        TestAssert.Equal((byte)no, bytes[11]);
        TestAssert.Equal((byte)members, bytes[12]);
        TestAssert.Equal(team == Team.Order ? 100 : 200, BinaryPrimitives.ReadInt32LittleEndian(bytes[13..]));
        TestAssert.Equal(remaining, BinaryPrimitives.ReadSingleLittleEndian(bytes[17..]));
    }
    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
