using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>Batch 571: the Co-op vs. AI (7) and Twisted Treeline 3v3 (8) queues, and a unique name for every queue game.</summary>
public static class AirQueueChecks
{
    internal const string Version = "1.45.11_10_04_15_28";
    internal const string BotAlias = "com.riotgames.platform.game.BotParticipant";

    [Check("server.air-queue-coop-vs-ai-solo-player-gets-a-bot-filled-game")]
    public static void CoopSoloPlayerGetsABotFilledGame() => CoopSoloAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-coop-vs-ai-groups-humans-by-difficulty")]
    public static void CoopGroupsHumansByDifficulty() => CoopGroupsAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-coop-vs-ai-results-use-the-practice-budget")]
    public static void CoopResultsUseThePracticeBudget() => CoopResults();

    [Check("server.air-queue-twisted-treeline-matches-six-three-a-side")]
    public static void TwistedTreelineMatchesSixThreeASide() => TwistedTreelineAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-game-names-are-unique")]
    public static void QueueGameNamesAreUnique() => NamesAsync().GetAwaiter().GetResult();

    private static async Task CoopSoloAsync()
    {
        var root = TemporaryDirectory("league-air-queue-coop-solo");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            games.Push = (_, _) => { };
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var account = accounts.RegisterPublic("coopsolo", "testing-password", "Coop Solo");
            var session = sessions.Login("rpc", account.Username, sessions.IssueTicket(account), Version);
            TestAssert.True(sessions.AuthenticateChannel("rpc", account.Username, session.Token));

            // The stock play menu enables a leaf whose queue is listed; the ribbon appends each queue's name.
            var queues = ((ArrayCollection)rpc.Route("rpc", "matchmakerService", "getAvailableQueues", [])!).Cast<AsObject>().ToArray();
            TestAssert.Equal(new object[] { 2d, 7d, 8d, 14d, 16d, 17d, 4d, 6d, 9d, 11d, 18d, 19d }, queues.Select(q => q["id"]).ToArray(),
                "queues 2, 7, 8, 14, 16 and 17, then the ranked 4, 6, 9 (batch 586), 11, 18 and 19 (batch 590), are listed");
            TestAssert.True(queues.All(q => q["name"] is string { Length: > 0 }), "every queue has a name for 'Searching for Match:'");
            var coop = queues.Single(q => Equals(q["id"], 7d));
            TestAssert.Equal("BOT", coop["type"], "GameQueueManager.getQueues lists BOT queues under Co-op vs. AI");
            TestAssert.Equal("CLASSIC", coop["gameMode"]);
            TestAssert.Equal(5d, coop["numPlayersPerTeam"]);
            TestAssert.Equal(new object[] { 1d }, ((ArrayCollection)coop["supportedMapIds"]).Cast<object>().ToArray());
            var login = (AsObject)rpc.Route("rpc", "clientFacadeService", "getLoginDataPacketForUser", [])!;
            TestAssert.Equal(new object[] { 2d, 7d, 8d, 14d, 16d, 17d, 4d, 6d, 9d, 11d, 18d, 19d },
                ((ArrayCollection)((AsObject)login["clientSystemStates"])["enabledQueueIdsList"]).Cast<object>().ToArray());

            AsObject Attach(string? difficulty) => (AsObject)rpc.Route("rpc", "matchmakerService", "attachToQueue",
                [AirWire.Object("com.riotgames.platform.matchmaking.MatchMakerParams", ("queueIds", AirWire.List([7d])),
                    ("team", null), ("invitationId", null), ("botDifficulty", difficulty))])!;
            var hard = (AsObject)((ArrayCollection)Attach("HARD")["playerJoinFailures"])[0];
            TestAssert.Equal("QUEUE_DISABLED", hard["reasonFailed"], "only the menu's EASY and MEDIUM are offered");
            var joined = Attach("EASY");
            TestAssert.True(joined["playerJoinFailures"] is null);
            TestAssert.Equal(7, ((AsObject)((ArrayCollection)joined["joinedQueues"])[0])["queueId"]);
            TestAssert.Equal(1, ((AsObject)rpc.Route("rpc", "matchmakerService", "getQueueInfo", [7d])!)["queueLength"]);
            TestAssert.Equal(0, ((AsObject)games.QueueInfo())["queueLength"], "the normal queue is a different queue");
            rpc.ResponseSent("rpc", "matchmakerService", "attachToQueue");

            games.Tick();
            TestAssert.True(games.Current(account.Id) is null, "a team of one waits a moment for others of its difficulty");
            clock.Now = clock.Now.AddSeconds(5);
            games.Tick();
            var game = (AsObject)games.Current(account.Id)!;
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal("COOP_VS_AI_GAME", game["gameType"], "the bundle's practiceGame_gameMode_COOP_VS_AI_GAME names it");
            TestAssert.Equal("BOT", game["queueTypeName"]);
            TestAssert.Equal(1, game["mapId"]);
            TestAssert.Equal(10, game["maxNumPlayers"]);
            var one = ((ArrayCollection)game["teamOne"]).Cast<AsObject>().ToArray();
            var two = ((ArrayCollection)game["teamTwo"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(5, one.Length);
            TestAssert.Equal(5, two.Length);
            TestAssert.Equal((double)account.Id, one[0]["accountId"], "the human leads team one");
            var bots = one.Skip(1).Concat(two).ToArray();
            TestAssert.True(bots.All(b => b.TypeName == BotAlias), "every other seat is a bot");
            TestAssert.True(bots.All(b => Equals(b["botSkillLevel"], 0d)), "Beginner (EASY) plays bot level 0");
            TestAssert.True(bots.All(b => Equals(b["pickMode"], 2)), "bots are picked, not picking");
            var botChampions = ((ArrayCollection)game["playerChampionSelections"]).Cast<AsObject>()
                .Where(s => ((string)s["summonerInternalName"]).StartsWith("bot_", StringComparison.Ordinal))
                .Select(s => (int)s["championId"]).ToArray();
            TestAssert.Equal(9, botChampions.Distinct().Count(), "nine different bot champions");
            TestAssert.True(botChampions.All(id => ChampionCatalog.ById(id)!.RetailBot), "only retail 126's bot champions");

            // A teammate bot's champion is taken; the human picks another, and the launch seats the human first and the nine bots after.
            var taken = botChampions[0];
            TestAssert.False(Picked(games.Pick(account.Id, taken), account, taken), "a teammate bot's champion is already taken");
            TestAssert.True(Picked(games.Pick(account.Id, 81), account, 81));
            games.LockIn(account.Id);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(10, roster.Length);
            TestAssert.False(roster[0].IsBot);
            TestAssert.Equal("Ezreal", roster[0].Champion);
            TestAssert.Equal(9, roster.Count(p => p.IsBot));
            TestAssert.Equal(5, roster.Count(p => p.Team == Team.Order));
            var handoff = JsonSerializer.Deserialize<PlatformPendingMatch>(File.ReadAllText(
                Path.Combine(root, "accounts", "match-handoffs", (int)(double)game["id"] + ".json")), MatchRoster.JsonOptions)!;
            TestAssert.Equal(PlatformGames.CoopVsAiQueueId, handoff.QueueId, "the receipt keeps the queue");
            TestAssert.Equal([account.Id], handoff.Accounts);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task CoopGroupsAsync()
    {
        var root = TemporaryDirectory("league-air-queue-coop-groups");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            games.Push = (_, _) => { };
            var connections = new Dictionary<string, long>();
            games.AccountForConnection = connection => connections.GetValueOrDefault(connection);
            PlatformAccount Queue(string name, string difficulty)
            {
                var account = accounts.RegisterPublic(name, "testing-password", name);
                connections[name] = account.Id;
                games.AttachQueue(account, name, [PlatformGames.CoopVsAiQueueId], difficulty);
                games.ConfirmQueueReply(name);
                return account;
            }
            var medium = Enumerable.Range(0, 5).Select(index => Queue("coopmedium" + index, "MEDIUM")).ToArray();
            var easy = Queue("coopeasy", "EASY");

            games.Tick();
            var full = (AsObject)games.Current(medium[0].Id)!;
            TestAssert.Equal(medium.Select(a => (object)(double)a.Id).ToArray(),
                ((ArrayCollection)full["teamOne"]).Cast<AsObject>().Select(p => p["accountId"]).ToArray(), "five humans start at once, in arrival order");
            var enemies = ((ArrayCollection)full["teamTwo"]).Cast<AsObject>().ToArray();
            TestAssert.True(enemies.Length == 5 && enemies.All(b => b.TypeName == BotAlias && Equals(b["botSkillLevel"], 1d)),
                "five Intermediate (MEDIUM, level 1) bots on the other team");
            TestAssert.True(games.Current(easy.Id) is null, "a Beginner player never joins an Intermediate game");
            TestAssert.Equal(1, ((AsObject)games.QueueInfo(PlatformGames.CoopVsAiQueueId))["queueLength"]);

            // A champion-select quit returns the four to the Co-op queue at their difficulty; once the gathering time passes, the four
            // play together with one bot on their team, and the Beginner player gets a game of their own.
            games.Quit(medium[0].Id);
            TestAssert.Equal(5, ((AsObject)games.QueueInfo(PlatformGames.CoopVsAiQueueId))["queueLength"]);
            clock.Now = clock.Now.AddSeconds(5);
            games.Tick();
            var four = (AsObject)games.Current(medium[1].Id)!;
            var team = ((ArrayCollection)four["teamOne"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(4, team.Count(p => p.TypeName != BotAlias));
            TestAssert.True(team.Length == 5 && Equals(team[4]["botSkillLevel"], 1d), "the fifth seat of their team is a bot");
            var alone = (AsObject)games.Current(easy.Id)!;
            TestAssert.False(Equals(alone["id"], four["id"]));
            TestAssert.True(((ArrayCollection)alone["teamTwo"]).Cast<AsObject>().All(b => Equals(b["botSkillLevel"], 0d)));
            TestAssert.Equal(0, ((AsObject)games.QueueInfo(PlatformGames.CoopVsAiQueueId))["queueLength"]);
            TestAssert.Throws<ArgumentException>(() => games.AddBot(medium[1].Id, 1, Team.Order, 0), "queue games take no lobby bots");
        }
        finally { DeleteDirectory(root); }
    }

    private static void CoopResults()
    {
        var root = TemporaryDirectory("league-air-queue-coop-results");
        try
        {
            // The release gate removes custom rewards only: Co-op vs. AI is a queue and keeps paying, from the practice budget.
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"CustomGameRewards\":false,\"PracticeMinutesPerDay\":10}");
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("coopresult", "testing-password", "Coop Result", maxed: false);
            var noon = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            MatchResult Match(int map) => new(8, "Version 1.0.0.126", map, "CLASSIC", "Order", 360, [Player(account)], false);
            PlatformResult Game(long id, int map, int queue, DateTimeOffset at) => new(id, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(Match(map), MatchRoster.JsonOptions), at, queue);
            long Ip() => PlatformEconomy.For(accounts.Get(account.Id)).Ip;

            // A six-minute win pays 18 + 2.312 * 6 = 31.87 IP and spends six of the ten budget minutes; Co-op claims no first win.
            TestAssert.True(accounts.RecordResult(Game(1, 1, PlatformGames.CoopVsAiQueueId, noon)));
            TestAssert.Equal(32L, Ip());
            TestAssert.Equal(360d, PlatformEconomy.Practice(accounts.Get(account.Id), accounts.EconomyRules, noon).SecondsSpent);
            // A Twisted Treeline queue win is a normal game: off the budget, and it claims First Win of the Day.
            TestAssert.True(accounts.RecordResult(Game(2, 4, PlatformGames.TwistedTreelineQueueId, noon.AddMinutes(30))));
            TestAssert.Equal(64L + PlatformEconomy.FirstWinBonusIp, Ip());
            TestAssert.Equal(360d, PlatformEconomy.Practice(accounts.Get(account.Id), accounts.EconomyRules, noon).SecondsSpent);

            // The end-of-game screen shows the quota panel for BOT (EndOfGameStatsView.as:1966), the recent games label it Co-op.
            var member = new PlatformGames.Member(accounts.Get(account.Id), Team.Order);
            var lobby = new PlatformGames.Lobby(1, "Co-op vs. AI Game 1", "", account.Id, 10)
            { QueueId = PlatformGames.CoopVsAiQueueId, Members = [member], Result = Match(1) };
            var eog = AirGameDtos.Results(lobby, member, null, accounts.EconomyRules, null, noon.AddMinutes(30));
            TestAssert.Equal("BOT", eog["queueType"]);
            TestAssert.Equal("COOP_VS_AI_GAME", eog["gameType"]);
            TestAssert.Equal(4d, eog["practiceMinutesLeftToday"], "four of the ten minutes are left");
            lobby.QueueId = PlatformGames.TwistedTreelineQueueId;
            TestAssert.Equal(-1d, AirGameDtos.Results(lobby, member, null, accounts.EconomyRules, null, noon)["practiceMinutesLeftToday"]);
            var recent = ((ArrayCollection)AirGameDtos.RecentGames(accounts.Get(account.Id), accounts.ResultsFor(account.Id))["gameStatistics"])
                .Cast<AsObject>().ToDictionary(row => (double)row["gameId"]);
            TestAssert.Equal("BOT", recent[1]["queueType"], "PlayerGameStats.gameTypeString reads BOT as '(Co-op vs. AI game)'");
            TestAssert.Equal("MATCHED_GAME", recent[1]["gameType"]);
            TestAssert.Equal("NORMAL", recent[2]["queueType"]);
            TestAssert.Equal(4d, recent[2]["gameMapId"]);
            TestAssert.Equal((2, 0, 0), AirProfileDtos.Record(accounts.Get(account.Id), accounts.ResultsFor(account.Id)),
                "both queue games count in Unranked, which the client reads for NORMAL and BOT");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task TwistedTreelineAsync()
    {
        var root = TemporaryDirectory("league-air-queue-treeline");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            games.Push = (_, _) => { };
            var connections = new Dictionary<string, long>();
            games.AccountForConnection = connection => connections.GetValueOrDefault(connection);
            var players = Enumerable.Range(0, 6).Select(index => accounts.RegisterPublic("treeline" + index, "testing-password", "Treeline " + index)).ToArray();
            var rift = accounts.RegisterPublic("riftonly", "testing-password", "Rift Only");
            connections["rift"] = rift.Id;
            games.AttachQueue(rift, "rift", [PlatformGames.NormalQueueId]);
            games.ConfirmQueueReply("rift");
            for (var index = 0; index < players.Length; index++)
            {
                connections["tt" + index] = players[index].Id;
                // The stock flow sends gameModel.difficulty on every attach; a normal queue ignores it.
                games.AttachQueue(players[index], "tt" + index, [PlatformGames.TwistedTreelineQueueId], index == 0 ? "EASY" : null);
                if (index < players.Length - 1) games.ConfirmQueueReply("tt" + index);
            }
            games.Tick();
            TestAssert.True(games.Current(players[0].Id) is null, "five ready players are not two teams of three");
            games.ConfirmQueueReply("tt5");
            games.Tick();

            var game = (AsObject)games.Current(players[0].Id)!;
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal(4, game["mapId"]);
            TestAssert.Equal("NORMAL_GAME", game["gameType"]);
            TestAssert.Equal("NORMAL", game["queueTypeName"]);
            TestAssert.Equal(6, game["maxNumPlayers"]);
            TestAssert.Equal($"Normal Game {(int)(double)game["id"]}", game["name"]);
            TestAssert.Equal(players.Take(3).Select(p => (object)(double)p.Id).ToArray(),
                ((ArrayCollection)game["teamOne"]).Cast<AsObject>().Select(p => p["accountId"]).ToArray(), "first three on team one");
            TestAssert.Equal(players.Skip(3).Select(p => (object)(double)p.Id).ToArray(),
                ((ArrayCollection)game["teamTwo"]).Cast<AsObject>().Select(p => p["accountId"]).ToArray(), "next three on team two");
            TestAssert.Equal(0, ((AsObject)games.QueueInfo(PlatformGames.TwistedTreelineQueueId))["queueLength"]);
            TestAssert.True(games.Current(rift.Id) is null, "a Summoner's Rift player is never taken into a Twisted Treeline game");
            TestAssert.Equal(1, ((AsObject)games.QueueInfo())["queueLength"]);

            // Blind pick on the Treeline, then a six-seat launch on map 4.
            int[] champions = [1, 61, 64];
            for (var index = 0; index < players.Length; index++)
            {
                games.Pick(players[index].Id, champions[index % 3]);
                games.LockIn(players[index].Id);
            }
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();
            var directory = Directory.EnumerateDirectories(Path.Combine(root, "games")).Single();
            var roster = MatchRoster.Read(Path.Combine(directory, "roster.json"));
            TestAssert.Equal(6, roster.Length);
            TestAssert.Equal(3, roster.Count(p => p.Team == Team.Order));
            TestAssert.True(roster.All(p => !p.IsBot));
            var handoff = JsonSerializer.Deserialize<PlatformPendingMatch>(File.ReadAllText(
                Path.Combine(root, "accounts", "match-handoffs", (int)(double)game["id"] + ".json")), MatchRoster.JsonOptions)!;
            TestAssert.Equal(PlatformGames.TwistedTreelineQueueId, handoff.QueueId);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task NamesAsync()
    {
        var root = TemporaryDirectory("league-air-queue-names");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            games.Push = (_, _) => { };
            var connections = new Dictionary<string, long>();
            games.AccountForConnection = connection => connections.GetValueOrDefault(connection);
            PlatformAccount Queue(string name, string difficulty)
            {
                var account = accounts.RegisterPublic(name, "testing-password", name);
                connections[name] = account.Id;
                games.AttachQueue(account, name, [PlatformGames.CoopVsAiQueueId], difficulty);
                games.ConfirmQueueReply(name);
                return account;
            }
            var a = Queue("namesa", "EASY");
            var b = Queue("namesb", "MEDIUM");
            clock.Now = clock.Now.AddSeconds(5);
            games.Tick();
            var gameA = (AsObject)games.Current(a.Id)!;
            var gameB = (AsObject)games.Current(b.Id)!;
            var nameA = (string)gameA["name"];
            var nameB = (string)gameB["name"];
            TestAssert.Equal($"Co-op vs. AI Game {(int)(double)gameA["id"]}", nameA);
            TestAssert.False(nameA == nameB, "two queue games running at once have different names");
            // The client names its champion-select rooms after the game (QueuedGameController: name + "_ta"; ChampionSelectionController:
            // name + "_c1"), so each player enters only their own game's rooms.
            TestAssert.True(games.MayEnterRoom(a.Id, PlatformGames.ChatRoomName("c1", nameA + "_ta")));
            TestAssert.True(games.MayEnterRoom(a.Id, PlatformGames.ChatRoomName("c1", nameA + "_c1")));
            TestAssert.False(games.MayEnterRoom(a.Id, PlatformGames.ChatRoomName("c1", nameB + "_ta")), "never the other queue game's room");
            TestAssert.False(games.MayEnterRoom(b.Id, PlatformGames.ChatRoomName("c1", nameA + "_c1")));
        }
        finally { DeleteDirectory(root); }
    }

    private static bool Picked(object game, PlatformAccount account, int champion) =>
        ((ArrayCollection)((AsObject)game)["playerChampionSelections"]).Cast<AsObject>().Any(selection =>
            Equals(selection["summonerInternalName"], "sum" + account.SummonerId) && Equals(selection["championId"], champion));

    internal static PlayerMatchResult Player(PlatformAccount account) => new(
        PlayerId: (ulong)account.SummonerId, NetId: (uint)account.Id, Name: account.Summoner,
        Champion: "Annie", Team: "Order", SkinId: 0, Connected: true, Level: 1,
        Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
        TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0,
        OtherMinionsKilled: 0, NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0,
        Statistics: new HeroMatchStatistics().Snapshot(0), SummonerSpells: ["SummonerFlash", "SummonerDot"], SpellRanks: [0, 0, 0, 0],
        Inventory: []);

    // The placeholder executable never starts: only the reserved roster and handoff matter here.
    internal static GameManager NonLaunchingManager(string root)
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

    internal static string TemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
