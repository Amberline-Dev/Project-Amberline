using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Server;


namespace Amberline.Tests.Network;

public static class LauncherChecks
{


    [Check("server.launcher-roster")]
    public static void RosterValidationLoadingAndStartBarrier()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amberline-roster-{Guid.NewGuid():N}.json");
        var champions = ServerOptions.SupportedChampions.ToArray();
        try
        {
            foreach (var count in new[] { 1, 2, 3, 10 })
            {
                var players = Enumerable.Range(0, count).Select(index => new MatchPlayer(
                    $"Nick {index + 1}", champions[index], index % 2 == 0 ? Team.Order : Team.Chaos,
                    "SummonerHeal", "SummonerTeleport", GameData.Default.Skins(champions[index])[^1].Id)).ToArray();
                // Solo also supports a selected Chaos seat.
                if (count == 1) players[0] = players[0] with { Team = Team.Chaos };
                File.WriteAllText(path, JsonSerializer.Serialize(players, MatchRoster.JsonOptions));
                var options = ServerOptions.Parse(["--roster", path, "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
                TestAssert.Equal(count, options.Players!.Count);
                using var logger = ServerLogger.Open(Path.Combine(Path.GetTempPath(), "amberline-roster-logs", Guid.NewGuid().ToString("N")), new StringWriter());
                using var network = new BuiltinServerMatchFactory().Create(options, logger);
                var session = network.Runtime.Session;
                session.World.Clock.SetVirtual(0);
                session.CreateWorld();
                TestAssert.Equal(players.Count(player => player.Team == Team.Order), session.OriginalRosterCounts.Order);
                TestAssert.Equal(players.Count(player => player.Team == Team.Chaos), session.OriginalRosterCounts.Chaos);
                TestAssert.True(session.Authenticate(99, session.BuildKeyCheck((ulong)count + 1)) is null);
                for (var index = 0; index < count; index++)
                {
                    var participant = session.Authenticate(index + 1, session.BuildKeyCheck((ulong)index + 1));
                    TestAssert.True(participant is not null);
                    TestAssert.True(session.Authenticate(100 + index, session.BuildKeyCheck((ulong)index + 1)) is null);
                    var spawn = session.Spawn(index + 1).Where(packet => packet.Plaintext.Span[0] == 0x4F).ToArray();
                    TestAssert.Equal(count, spawn.Length);
                    for (var seat = 0; seat < count; seat++)
                    {
                        var payload = spawn[seat].Plaintext.Span;
                        TestAssert.Equal((uint)seat, BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(9, 4)));
                        TestAssert.Equal(players[seat].SkinId, BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(19, 4)));
                        TestAssert.Equal(players[seat].Name, Encoding.ASCII.GetString(payload.Slice(23, 40)).TrimEnd('\0'));
                        TestAssert.Equal(players[seat].Champion, Encoding.ASCII.GetString(payload.Slice(63, 40)).TrimEnd('\0'));
                    }
                    session.MarkReady(index + 1);
                    if (index < count - 1) TestAssert.False(session.CanStart);
                }
                var loading = LoadingPackets.SynchVersionS2C(players: session.Roster).Plaintext.Span;
                for (var seat = 0; seat < count; seat++)
                {
                    TestAssert.Equal(players[seat].Name, Encoding.ASCII.GetString(loading.Slice(33 + seat * 87, 28)).TrimEnd('\0'));
                    TestAssert.Equal(players[seat].Champion, Encoding.ASCII.GetString(loading.Slice(61 + seat * 87, 28)).TrimEnd('\0'));
                    TestAssert.Equal(ProtocolEncoding.HashString(players[seat].SummonerSpell1), BinaryPrimitives.ReadUInt32LittleEndian(loading.Slice(20 + seat * 87, 4)));
                    TestAssert.Equal(ProtocolEncoding.HashString(players[seat].SummonerSpell2), BinaryPrimitives.ReadUInt32LittleEndian(loading.Slice(24 + seat * 87, 4)));
                }
                var reskins = LoadingPackets.LoadingBurst(1, players: session.Roster)
                    .Where(packet => packet.Plaintext.Span[0] == 0x65).ToArray();
                TestAssert.Equal(count, reskins.Length);
                for (var seat = 0; seat < count; seat++)
                    TestAssert.Equal(players[seat].SkinId,
                        BinaryPrimitives.ReadInt32LittleEndian(reskins[seat].Plaintext.Span[16..20]));
                session.World.Clock.Advance(3);
                TestAssert.True(session.CanStart);
                TestAssert.True(session.Start());
                TestAssert.True(session.ControlledHero(1, session.Participants[0].Hero!.NetId) is not null);
                if (count > 1) TestAssert.True(session.ControlledHero(1, session.Participants[1].Hero!.NetId) is null);
                TestAssert.True(session.Disconnect(count));
                TestAssert.True(session.AuthenticateReconnect(99, session.BuildKeyCheck((ulong)count)) is not null);
                var reconnect = session.Spawn(99).Where(packet => packet.Plaintext.Span[0] == 0x4F).ToArray();
                TestAssert.Equal(count, reconnect.Length);
                for (var seat = 0; seat < count; seat++)
                    TestAssert.Equal(players[seat].SkinId,
                        BinaryPrimitives.ReadInt32LittleEndian(reconnect[seat].Plaintext.Span[19..23]));
            }
            var player = new MatchPlayer("Alice", "annie", Team.Order);
            TestAssert.Equal("Annie", MatchRoster.Validate([player])[0].Champion);
            foreach (var team in new[] { Team.Order, Team.Chaos })
                TestAssert.Throws<ArgumentException>(() => MatchRoster.ToParticipants([
                    player with { Team = team },
                    player with { Name = "Bob", Champion = "ANNIE", Team = team }]));
            TestAssert.Equal(2, MatchRoster.ToParticipants([
                player, player with { Name = "Bob", Team = Team.Chaos }]).Length,
                "opposing teams may select the same champion");
            foreach (var invalid in new[] { player with { Name = " " }, player with { Name = "Łukasz" },
                         player with { Name = new string('a', 28) }, player with { Champion = "unknown" },
                         player with { Team = (Team)999 }, player with { Name = "bad\nname" },
                         player with { SkinId = -1 }, player with { SkinId = 1001 },
                         player with { SkinId = 7 } })
                TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate([invalid]));
            TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate([]));
            TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate([player, player with { Name = "alice" }]));
            TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate(Enumerable.Range(0, 6).Select(i => player with { Name = $"Name{i}" }).ToArray()));
            File.WriteAllText(path, "[{\"Name\":\"A\",\"Champion\":\"Annie\",\"Team\":\"Other\"}]");
            TestAssert.Throws<ArgumentException>(() => ServerOptions.Parse(["--roster", path]));
            File.WriteAllText(path, "null");
            TestAssert.Throws<ArgumentException>(() => MatchRoster.Read(path));
            File.WriteAllText(path, "[{\"Name\":\"A\",\"Champion\":\"Annie\",\"Team\":\"Order\"}]");
            var legacy = MatchRoster.Read(path)[0];
            TestAssert.Equal(0, legacy.SkinId, "old roster files retain the default skin");
            TestAssert.False(legacy.IsBot, "old roster files default IsBot to false");
        }
        finally { File.Delete(path); }
    }

    [Check("server.launcher-process-control")]
    public static void ReadinessStopAndOwnerExit()
    {
        foreach (var stopCommand in new[] { true, false })
        {
            var logDirectory = Path.Combine(Path.GetTempPath(), "amberline-control", Guid.NewGuid().ToString("N"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { typeof(ServerOptions).Assembly.Location, "--solo", "--ephemeral", "--bind", "127.0.0.1", "--launcher-control",
                         "--log-dir", logDirectory }) start.ArgumentList.Add(arg);
            start.Environment["DEV_VISIBILITY_BASELINE"] = "0";
            start.Environment["MINION_FIDELITY_MODE"] = "0";
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = Task.Run(async () =>
            {
                var lines = new List<string>();
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lines.Add(line);
                    if (line == "launcher.ready") ready.TrySetResult();
                }
                ready.TrySetException(new InvalidOperationException(string.Join('\n', lines)));
                return lines;
            });
            try
            {
                ready.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                if (stopCommand) { process.StandardInput.WriteLine("stop"); process.StandardInput.Flush(); }
                else process.StandardInput.Close();
                TestAssert.True(process.WaitForExit(10_000), "owned server must exit after Stop or launcher pipe EOF");
                TestAssert.Equal(0, process.ExitCode, stderr.GetAwaiter().GetResult());
                output.GetAwaiter().GetResult();
                var sessionLog = Directory.GetFiles(logDirectory, "session.jsonl", SearchOption.AllDirectories).Single();
                TestAssert.True(File.ReadAllLines(sessionLog).Any(line => line.Contains("server.stop", StringComparison.Ordinal)), "graceful stop must flush the final log record");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            }
        }
    }

    [Check("server.bot-seat-foundation")]
    public static void BotSeatFoundation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amberline-bot-roster-{Guid.NewGuid():N}.json");
        var players = new[]
        {
            new MatchPlayer("BotSeat", "Annie", Team.Order, IsBot: true),
            new MatchPlayer("HumanSeat", "Orianna", Team.Chaos),
        };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(players, MatchRoster.JsonOptions));
            var options = ServerOptions.Parse(["--roster", path, "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            TestAssert.True(options.Players![0].IsBot, "saved bot flag must survive roster parsing");
            TestAssert.False(options.Players[1].IsBot, "an explicit human seat remains non-bot after roster parsing");
            TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate(players.Select(player => player with
                { IsBot = true }).ToArray()), "all-bot production rosters must be rejected");

            using var logger = ServerLogger.Open(Path.Combine(Path.GetTempPath(), "amberline-bot-logs", Guid.NewGuid().ToString("N")), new StringWriter());
            using var network = new BuiltinServerMatchFactory().Create(options, logger);
            var session = network.Runtime.Session;
            session.World.Clock.SetVirtual(0);
            session.CreateWorld();
            TestAssert.Equal(1, session.OriginalRosterCounts.Order, "bot remains in full Order roster count");
            TestAssert.Equal(1, session.OriginalRosterCounts.Chaos, "human remains in full Chaos roster count");
            TestAssert.True(session.Participants[0].IsBot && session.Participants[0].Spawned &&
                session.Participants[0].Loaded && session.Participants[0].Ready,
                "bot is server-ready without a peer");
            TestAssert.True(session.Authenticate(11, session.BuildKeyCheck(2)) is not null,
                "human seat after a bot seat can authenticate");
            TestAssert.True(session.Authenticate(12, session.BuildKeyCheck(1)) is null,
                "bot seat has no authentication path");

            var spawn = session.Spawn(11);
            var heroes = spawn.Where(packet => packet.Plaintext.Span[0] == 0x4F).ToArray();
            TestAssert.Equal(2, heroes.Length);
            TestAssert.Equal((byte)0, spawn.Single(packet => packet.Plaintext.Span[0] == 0x65).Plaintext.Span[5],
                "Order bot is already counted in the loading roster");
            TestAssert.Equal((byte)0, spawn.Single(packet => packet.Plaintext.Span[0] == 0x65).Plaintext.Span[6],
                "no additional Chaos seats");
            TestAssert.Equal((byte)1, heroes[0].Plaintext.Span[16], "bot CreateHero flag");
            TestAssert.Equal((byte)0, heroes[1].Plaintext.Span[16], "human CreateHero flag");

            TestAssert.True(session.MarkReady(11));
            session.World.Clock.Advance(3.0);
            TestAssert.True(session.CanStart, "server-ready bot does not block the start barrier");
            session.BuildMapReadyPackets(11);
            TestAssert.True(session.Start());
            session.Participants[0].Hero!.Position = new Position2(5000.0, 5000.0);
            TestAssert.True(session.Disconnect(11));
            TestAssert.True(session.AuthenticateReconnect(13, session.BuildKeyCheck(2)) is not null,
                "human reconnect still reattaches after a bot seat");
            TestAssert.True(session.AuthenticateReconnect(14, session.BuildKeyCheck(1)) is null,
                "bot reconnect is rejected without a fake peer");
        }
        finally { File.Delete(path); }
    }

    [Check("server.bot-seat-mixed-roster-barrier")]
    public static void MixedRosterWaitsForHumansOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amberline-bot-mixed-{Guid.NewGuid():N}.json");
        var players = new[]
        {
            new MatchPlayer("Bot1", "Annie", Team.Order, IsBot: true),
            new MatchPlayer("Human1", "Orianna", Team.Chaos),
            new MatchPlayer("Bot2", "LeeSin", Team.Order, IsBot: true),
            new MatchPlayer("Human2", "Nidalee", Team.Chaos),
            new MatchPlayer("Bot3", "Shaco", Team.Order, IsBot: true),
            new MatchPlayer("Bot4", "Ezreal", Team.Chaos, IsBot: true),
            new MatchPlayer("Bot5", "Vayne", Team.Order, IsBot: true),
            new MatchPlayer("Bot6", "Janna", Team.Chaos, IsBot: true),
            new MatchPlayer("Human3", "Blitzcrank", Team.Order),
            new MatchPlayer("Bot7", "Yorick", Team.Chaos, IsBot: true),
        };
        TestAssert.Equal(new[] { 1, 3, 8 }, players.Select((player, index) => (player, index))
            .Where(item => !item.player.IsBot).Select(item => item.index).ToArray(),
            "humans occupy nonconsecutive seats beyond index zero");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(players, MatchRoster.JsonOptions));
            var options = ServerOptions.Parse(["--roster", path, "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            using var logger = ServerLogger.Open(Path.Combine(Path.GetTempPath(), "amberline-bot-mixed-logs", Guid.NewGuid().ToString("N")), new StringWriter());
            using var network = new BuiltinServerMatchFactory().Create(options, logger);
            var session = network.Runtime.Session;
            session.World.Clock.SetVirtual(0);

            TestAssert.True(session.Authenticate(20, session.BuildKeyCheck(2)) is not null);
            var firstSpawn = session.Spawn(20);
            session.MarkReady(20);
            TestAssert.False(session.CanStart, "the unjoined human seat keeps the barrier closed");
            var startSpawn = firstSpawn.Single(packet => packet.Plaintext.Span[0] == 0x65).Plaintext.Span;
            TestAssert.Equal((byte)0, startSpawn[5], "Order bots are already counted in the loading roster");
            TestAssert.Equal((byte)0, startSpawn[6], "Chaos bots are already counted in the loading roster");
            var heroes = firstSpawn.Where(packet => packet.Plaintext.Span[0] == 0x4F).ToArray();
            TestAssert.Equal(10, heroes.Length);
            TestAssert.True(heroes.Select((packet, index) => (packet, player: players[index])).Where(item => item.player.IsBot)
                .All(item => item.packet.Plaintext.Span[16] == (item.player.Champion == "Annie" ? 1 : 0)),
                "only Annie among these bots has a game_bot_ name in the stock client; the others are named by the server");
            TestAssert.True(heroes.All(packet => packet.Plaintext.Span[17] == 0), "default bot rank is zero");

            TestAssert.True(session.Authenticate(21, session.BuildKeyCheck(4)) is not null);
            session.Spawn(21);
            session.MarkReady(21);
            TestAssert.False(session.CanStart, "the final human seat still keeps the barrier closed");
            TestAssert.True(session.Authenticate(22, session.BuildKeyCheck(9)) is not null);
            session.Spawn(22);
            session.MarkReady(22);
            session.World.Clock.Advance(3.0);
            TestAssert.True(session.CanStart, "the last human opens the barrier after bots are ready");
        }
        finally { File.Delete(path); }
    }

    [Check("server.bot-seat-surrender-policy")]
    public static void OneHumanFourBotTeamCanSurrender()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amberline-bot-surrender-{Guid.NewGuid():N}.json");
        var players = new[]
        {
            new MatchPlayer("Bot1", "Annie", Team.Order, IsBot: true),
            new MatchPlayer("Bot2", "Orianna", Team.Order, IsBot: true),
            new MatchPlayer("Human", "LeeSin", Team.Order),
            new MatchPlayer("Bot3", "Nidalee", Team.Order, IsBot: true),
            new MatchPlayer("Bot4", "Shaco", Team.Order, IsBot: true),
            new MatchPlayer("Enemy", "Ezreal", Team.Chaos),
        };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(players, MatchRoster.JsonOptions));
            var options = ServerOptions.Parse(["--roster", path, "--ephemeral", "--bind", "127.0.0.1", "--channel-limit", "8"]);
            using var logger = ServerLogger.Open(Path.Combine(Path.GetTempPath(), "amberline-bot-surrender-logs", Guid.NewGuid().ToString("N")), new StringWriter());
            using var network = new BuiltinServerMatchFactory().Create(options, logger);
            var session = network.Runtime.Session;
            session.World.Clock.SetVirtual(0);
            TestAssert.True(session.Authenticate(30, session.BuildKeyCheck(3)) is not null);
            TestAssert.True(session.Authenticate(31, session.BuildKeyCheck(6)) is not null);
            session.Spawn(30);
            session.Spawn(31);
            session.MarkReady(30);
            session.MarkReady(31);
            session.World.Clock.Advance(3.0);
            TestAssert.True(session.CanStart);
            session.BuildMapReadyPackets(30);
            session.BuildMapReadyPackets(31);
            TestAssert.True(session.Start());
            // Production session time is the CombatService clock, deliberately
            // independent from wall/virtual World.Clock time.
            var matchOrigin = network.Runtime.Combat.CurrentTime;
            network.Runtime.Combat.RestoreClock(matchOrigin + 1200.0);

            var vote = session.Receive(30, 1, [SurrenderPackets.ClientOpcode, 0, 0, 0, 0, 1]);
            TestAssert.Equal(5, session.OriginalRosterCounts.Order,
                "all five Order seats remain in original roster accounting");
            TestAssert.Equal(1, session.OriginalRosterCounts.Chaos);
            var votePacket = vote.Single(output => output.Packet.Plaintext.Span[0] == 0xD2).Packet.Plaintext.Span;
            TestAssert.Equal((byte)1, votePacket[10], "one human yes vote");
            TestAssert.Equal((byte)0, votePacket[11], "no human no votes");
            TestAssert.Equal((byte)1, votePacket[12], "bots are excluded from surrender denominator");
            TestAssert.Equal(2, vote.Count(output => output.Packet.Plaintext.Span[0] == 0xAD),
                "agreed status reaches both human transports only");
            TestAssert.True(session.Result is { Surrendered: true });
            TestAssert.True(session.Result!.Players.Single(player => player.Name == "Bot1").IsBot,
                "final result identifies server-owned bot seats");
            TestAssert.False(session.Result.Players.Single(player => player.Name == "Enemy").IsBot);
        }
        finally { File.Delete(path); }
    }
}
