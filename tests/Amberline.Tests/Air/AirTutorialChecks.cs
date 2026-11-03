using System.Text.Json;
using Amberline.Core;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>Batch 580: <c>gameService.createTutorialGame</c>, the Basic Tutorial on map 3.</summary>
public static class AirTutorialChecks
{
    private const string Version = "1.45.11_10_04_15_28";
    private const string BotAlias = "com.riotgames.platform.game.BotParticipant";

    [Check("server.air-tutorial-basic-launches-ashe-with-nunu-and-master-yi")]
    public static void BasicTutorialLaunches() => BasicAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 592: a won tutorial sets the summoner's tutorialFlag (the first-login offer's test, ViewMediatorImpl.as:151/429) and pays no IP
    /// or XP, its results carrying the GAME_TYPE penalty the client renames to TUTORIAL (EndOfGameStatsController.fixPenalties); a lost one
    /// leaves the flag.
    /// </summary>
    [Check("server.air-tutorial-win-sets-the-tutorial-flag-and-pays-nothing")]
    public static void TutorialWinSetsTheFlagAndPaysNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"league-air-tutorial-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            PlayerMatchResult Player(ulong id, MatchPlayer seat, bool bot) => new(
                PlayerId: id, NetId: (uint)id, Name: seat.Name, Champion: seat.Champion, Team: seat.Team.ToString(), SkinId: 0,
                Connected: true, Level: 2, Kills: 1, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
                TurretsKilled: 1, InhibitorsKilled: 1, NexusesKilled: 1, LaneMinionsKilled: 0, OtherMinionsKilled: 20,
                NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 2300, Statistics: new HeroMatchStatistics().Snapshot(364),
                SummonerSpells: [seat.SummonerSpell1, seat.SummonerSpell2], SpellRanks: [0, 1, 0, 0], Inventory: [], IsBot: bot);
            PlatformResult Tutorial(PlatformAccount account, string winner)
            {
                var ashe = account.Loadout with { Champion = "Ashe", Team = Team.Order, PlayerId = (int)account.SummonerId };
                var result = new MatchResult(8, "Version 1.0.0.126", GameMaps.Tutorial, GameMaps.TutorialMode, winner, 364.3,
                    [Player((ulong)account.SummonerId, ashe, false), Player(900001, PlatformBots.Seat(20, Team.Order, 0), true),
                     Player(900002, PlatformBots.Seat(11, Team.Chaos, 0), true)], false);
                return new PlatformResult(accounts.AllocateGameId(), Guid.NewGuid(), [account.Id], JsonSerializer.SerializeToElement(result));
            }

            var winner = accounts.RegisterPublic("tutorialwinner", "testing-password", "Tutorial Winner", maxed: false);
            TestAssert.False(winner.TutorialFlag, "a new summoner is offered the tutorial");
            var ip = PlatformEconomy.For(winner).Ip;
            TestAssert.True(accounts.RecordResult(Tutorial(winner, "Order")));
            var after = accounts.Get(winner.Id);
            TestAssert.True(after.TutorialFlag, "the won tutorial sets the flag");
            TestAssert.Equal(true, AirDtos.Summoner(after)["tutorialFlag"], "and the summoner the client reads at login says so");
            var receipt = accounts.ResultsFor(winner.Id).Single();
            TestAssert.True(receipt.Rewards is null, "the tutorial pays no IP or XP");
            TestAssert.Equal(ip, PlatformEconomy.For(after).Ip, "the balance is unchanged");

            var member = new PlatformGames.Member(after, Team.Order);
            var lobby = new PlatformGames.Lobby((int)receipt.GameId, "Tutorial", "", winner.Id, 3)
            {
                MapId = GameMaps.Tutorial, Members = [member], Result = receipt.Result.Deserialize<MatchResult>(),
            };
            var stats = AirGameDtos.Results(lobby, member);
            TestAssert.Equal("TUTORIAL_GAME", stats["gameType"]);
            var penalty = ((ArrayCollection)stats["pointsPenalties"]).Cast<AsObject>().Single();
            TestAssert.Equal("GAME_TYPE", penalty["type"], "the penalty the client renames to TUTORIAL");
            TestAssert.Equal(0d, stats["ipEarned"] is double earned ? earned : Convert.ToDouble(stats["ipEarned"]), "no IP earned");

            var loser = accounts.RegisterPublic("tutorialloser", "testing-password", "Tutorial Loser", maxed: false);
            TestAssert.True(accounts.RecordResult(Tutorial(loser, "Chaos")));
            TestAssert.False(accounts.Get(loser.Id).TutorialFlag, "a lost tutorial keeps the offer");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task BasicAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"league-air-tutorial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            var pushed = new List<AsObject>();
            games.Push = (_, message) => { if (message is AsObject dto) pushed.Add(dto); };
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var account = accounts.RegisterPublic("tutorialplayer", "testing-password", "Tutorial Player");
            var session = sessions.Login("rpc", account.Username, sessions.IssueTicket(account), Version);
            TestAssert.True(sessions.AuthenticateChannel("rpc", account.Username, session.Token));

            // Battle Training (1) has no shipped map or script of its own: still the client's own unsupported-mode text.
            TestAssert.Throws<ArgumentException>(() => rpc.Route("rpc", "gameService", "createTutorialGame", [1d]));
            TestAssert.True(games.Current(account.Id) is null, "a refused Battle Training leaves no game");

            // The basic tutorial comes back launching: the client accepts a tutorial GameDTO only in champion select or START_REQUESTED.
            var game = (AsObject)rpc.Route("rpc", "gameService", "createTutorialGame", [0d])!;
            TestAssert.Equal("START_REQUESTED", game["gameState"]);
            TestAssert.Equal("TUTORIAL_GAME", game["gameType"], "GameType.TUTORIAL_GAME");
            TestAssert.Equal("TUTORIAL", game["gameMode"], "GameMode.TUTORIAL");
            TestAssert.Equal(GameMaps.Tutorial, game["mapId"]);
            TestAssert.Equal("NONE", game["queueTypeName"], "no queue");
            TestAssert.True(pushed.Count > 0 && Equals(pushed[^1]["optimisticLock"], game["optimisticLock"]),
                "the push and the answer carry the same revision, so either may arrive first");
            var one = ((ArrayCollection)game["teamOne"]).Cast<AsObject>().ToArray();
            var two = ((ArrayCollection)game["teamTwo"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(2, one.Length);
            TestAssert.Equal((double)account.Id, one[0]["accountId"], "the player leads team one");
            TestAssert.Equal(BotAlias, one[1].TypeName);
            TestAssert.Equal(BotAlias, two.Single().TypeName);
            var selections = ((ArrayCollection)game["playerChampionSelections"]).Cast<AsObject>().ToArray();
            var mine = selections.Single(s => !((string)s["summonerInternalName"]).StartsWith("bot_", StringComparison.Ordinal));
            TestAssert.Equal(22, mine["championId"], "Ashe");
            TestAssert.Equal((double)AirGameDtos.SpellId("SummonerHeal"), mine["spell1Id"], "Heal on D for the Heal lesson");
            TestAssert.Equal(new[] { "bot_MasterYi_200", "bot_Nunu_100" },
                selections.Select(s => (string)s["summonerInternalName"]).Where(name => name.StartsWith("bot_", StringComparison.Ordinal))
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray());

            // The match the TutorialScript expects: its player first on Order, Nunu on Order and Master Yi on Chaos, found by name.
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(3, roster.Length);
            TestAssert.False(roster[0].IsBot);
            TestAssert.Equal("Ashe", roster[0].Champion);
            TestAssert.Equal(Team.Order, roster[0].Team);
            TestAssert.Equal("SummonerHeal", roster[0].SummonerSpell1);
            TestAssert.True(roster[1] is { IsBot: true, Champion: "Nunu", Team: Team.Order }, "Nunu bot on Order");
            TestAssert.True(roster[2] is { IsBot: true, Champion: "MasterYi", Team: Team.Chaos }, "Master Yi bot on Chaos");
            var handoff = JsonSerializer.Deserialize<PlatformPendingMatch>(File.ReadAllText(
                Path.Combine(root, "accounts", "match-handoffs", (int)(double)game["id"] + ".json")), MatchRoster.JsonOptions)!;
            TestAssert.Equal(0, handoff.QueueId, "no queue");
            TestAssert.Equal([account.Id], handoff.Accounts);

            // One game at a time: the player is in it now.
            TestAssert.Throws<ArgumentException>(() => rpc.Route("rpc", "gameService", "createTutorialGame", [0d]));
            TestAssert.Equal("TUTORIAL_GAME", ((AsObject)games.Current(account.Id)!)["gameType"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // The placeholder executable never starts: only the reserved roster and handoff matter here (AirQueueChecks' manager).
    private static GameManager NonLaunchingManager(string root)
    {
        var executable = Path.Combine(root, "server-placeholder.exe");
        Directory.CreateDirectory(Path.Combine(root, "game-data"));
        File.WriteAllBytes(executable, []);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        return new GameManager(new HostSettings(Path.Combine(root, "game-data"), executable,
            FirstGamePort: port, MaxGames: 2, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
    }
}
