using Amberline.Core;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>
/// Draft Mode custom games (batch 543): the GameTypeConfig the stock client offers, the captains' ban phase, the 1-2-2-...-1 pick
/// turns, the PG-0030 refusal of a pick out of turn, the teammate trade and the launch, and the turn deadlines.
/// </summary>
public static class AirDraftChecks
{
    private const string Version = "1.45.11_10_04_15_28";
    private const string NotTurnAlias = "com.riotgames.platform.game.GameNotDraftModeTurnException";

    [Check("server.air-draft-custom-game-bans-turns-trade-start")]
    public static void ADraftCustomGameBansPicksInTurnTradesAndStarts() => DraftAsync().GetAwaiter().GetResult();

    [Check("server.air-draft-turn-deadlines-and-bots")]
    public static void DraftDeadlinesSkipBansLockPicksDropIdlersAndBotsHaveNoTurn() => DeadlinesAsync().GetAwaiter().GetResult();

    private static async Task DraftAsync()
    {
        var root = TemporaryDirectory("league-air-draft");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            games.Push = (_, _) => { };
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            PlatformAccount NewAccount(string name)
            {
                var account = accounts.RegisterPublic(name.ToLowerInvariant(), "testing-password", name);
                var session = sessions.Login(name, account.Username, sessions.IssueTicket(account), Version);
                TestAssert.True(sessions.AuthenticateChannel(name, account.Username, session.Token));
                return account;
            }
            var a = NewAccount("DraftA");
            var b = NewAccount("DraftB");
            var c = NewAccount("DraftC");
            var d = NewAccount("DraftD");

            // The config the create form offers: blind pick first (unchanged), then Draft Mode.
            var configs = AirDtos.GameTypes().Cast<AsObject>().ToArray();
            TestAssert.Equal("GAME_CFG_PICK_BLIND", configs[0]["name"], "blind pick stays the first, default entry");
            var draft = configs.Single(config => Equals(config["id"], 2d));
            TestAssert.Equal("GAME_CFG_DRAFT_STD", draft["name"]);
            TestAssert.Equal("DraftModeSinglePickStrategy", draft["pickMode"]);
            TestAssert.Equal(4, draft["maxAllowableBans"]);
            TestAssert.Equal(true, draft["allowTrades"]);
            TestAssert.Equal(true, draft["exclusivePick"]);
            var login = (AsObject)rpc.Route("DraftA", "clientFacadeService", "getLoginDataPacketForUser", [])!;
            TestAssert.Equal(new object[] { 1d, 2d }, ((ArrayCollection)((AsObject)login["clientSystemStates"])["practiceGameTypeConfigIdList"]).Cast<object>().ToArray(),
                "the practice list offers blind pick and Draft Mode");

            // The stock create call: [name, mode, map, players, gameTypeIndex, password, spectators].
            var created = (AsObject)rpc.Route("DraftA", "gameService", "createPracticeGame",
                ["Draft lobby", "CLASSIC", AirDtos.Maps()[0], 4, AirGameDtos.DraftConfigId, "", "NONE"])!;
            TestAssert.Equal(2, created["gameTypeConfigId"]);
            var id = Convert.ToInt32(created["id"]);
            games.Join(b, id, "");
            games.Join(c, id, "");
            games.Join(d, id, "");
            // Teams fill alternately: Order is A then C, Chaos is B then D; A and B are the captains (first of each team list).
            games.Start(a.Id, id, Revision(games, a.Id, id));
            var game = Game(games, a);
            TestAssert.Equal("PRE_CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal(1, game["pickTurn"]);
            TestAssert.Equal(1, PickMode(game, a), "Order's captain bans first");
            TestAssert.True(new[] { b, c, d }.All(p => PickMode(game, p) == 0));

            var bannable = ((ArrayCollection)rpc.Route("DraftA", "gameService", "getChampionsForBan", [])!).Cast<AsObject>().ToArray();
            TestAssert.Equal(84, bannable.Length, "one ban entry per champion of the patch");
            TestAssert.True(bannable.All(entry => entry.TypeName == "com.riotgames.platform.game.ChampionBanInfoDTO"));
            var annieInfo = bannable.Single(entry => Equals(entry["championId"], 1));
            TestAssert.True((bool)annieInfo["owned"] && (bool)annieInfo["enemyOwned"], "both teams own Annie");

            var outOfTurn = Fault(() => rpc.Route("DraftB", "gameService", "banChampion", [1]));
            TestAssert.Equal("PG-0030", outOfTurn.ErrorCode, "the other captain bans out of turn");
            TestAssert.Equal(NotTurnAlias, outOfTurn.Alias);
            TestAssert.Equal("PG-0030", Fault(() => games.Ban(c.Id, 1)).ErrorCode, "a player who is not a captain cannot ban");
            TestAssert.True(rpc.Route("DraftA", "gameService", "banChampion", [1]) is null);
            TestAssert.True(rpc.Route("DraftA", "gameService", "banChampionCompleted", []) is null, "banChampionCompleted only acknowledges");
            game = Game(games, a);
            TestAssert.Equal(2, game["pickTurn"], "each ban is its own turn");
            TestAssert.Equal(new object[] { 1 }, Banned(game));
            TestAssert.Equal(1, PickMode(game, b), "now Chaos's captain bans");
            TestAssert.Equal("PG-0031", Fault(() => games.Ban(b.Id, 1)).ErrorCode, "a champion is banned once");
            games.Ban(b.Id, 22);
            games.Ban(a.Id, 61);
            // Chaos's captain lets the fourth ban time out: that ban is given up and the picks begin.
            clock.Now = clock.Now.AddSeconds(PlatformGames.DraftBanServerSeconds);
            games.Tick();
            game = Game(games, a);
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal(1, game["pickTurn"]);
            TestAssert.Equal(new object[] { 1, 22, 61 }, Banned(game));

            // Two a side: A alone, then B and D, then C.
            TestAssert.Equal(1, PickTurn(game, a));
            TestAssert.Equal(2, PickTurn(game, b));
            TestAssert.Equal(2, PickTurn(game, d));
            TestAssert.Equal(3, PickTurn(game, c));
            var notYet = Fault(() => rpc.Route("DraftC", "gameService", "selectChampion", [64]));
            TestAssert.Equal("PG-0030", notYet.ErrorCode, "a pick out of turn is refused as no longer your turn");
            TestAssert.Equal(NotTurnAlias, notYet.Alias);
            TestAssert.Equal("PG-0030", Fault(() => games.LockIn(c.Id)).ErrorCode, "and so is a lock-in");
            TestAssert.Equal("PG-0031", Fault(() => games.Pick(a.Id, 22)).ErrorCode, "a banned champion cannot be picked");
            games.Pick(a.Id, 64);
            games.LockIn(a.Id);
            game = Game(games, a);
            TestAssert.Equal(2, game["pickTurn"]);
            TestAssert.Equal(2, PickMode(game, a));
            TestAssert.True(PickMode(game, b) == 1 && PickMode(game, d) == 1 && PickMode(game, c) == 0, "B and D pick together");
            // exclusivePick: the other team's champion is taken too; the answer is the unchanged game.
            TestAssert.False(Picked(games.Pick(b.Id, 64), b, 64), "Lee Sin is A's");
            games.Pick(b.Id, 81);
            games.Pick(d.Id, 76);
            games.LockIn(b.Id);
            TestAssert.Equal(2, Game(games, a)["pickTurn"], "the turn waits for both of its players");
            games.LockIn(d.Id);
            TestAssert.Equal(3, Game(games, a)["pickTurn"]);
            games.Pick(c.Id, 83);
            games.LockIn(c.Id);
            game = Game(games, a);
            TestAssert.Equal("POST_CHAMP_SELECT", game["gameState"], "after the last turn comes the trade phase");
            TestAssert.True(new[] { a, b, c, d }.All(p => PickMode(game, p) == 2));

            // C asked A over chat and A accepted (client to client); C's client then calls tradeChampion with A's summoner id.
            TestAssert.Equal("champion_trading_trade_error",
                Fault(() => rpc.Route("DraftC", "gameService", "tradeChampion", [(int)b.SummonerId])).ErrorCode, "no trade with the other team");
            TestAssert.True(rpc.Route("DraftC", "gameService", "tradeChampion", [(int)a.SummonerId]) is null);
            game = Game(games, a);
            TestAssert.True(Picked(game, a, 83) && Picked(game, c, 64), "the teammates swapped champions");

            clock.Now = clock.Now.AddSeconds(PlatformGames.DraftPostPickServerSeconds);
            games.Tick();
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(4, roster.Length, "the draft game launched with its four players");
            TestAssert.Equal(AirGameDtos.ChampionNames[83], roster.Single(p => p.PlayerId == (int)a.SummonerId).Champion);
            TestAssert.Equal(AirGameDtos.ChampionNames[64], roster.Single(p => p.PlayerId == (int)c.SummonerId).Champion);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task DeadlinesAsync()
    {
        var root = TemporaryDirectory("league-air-draft-deadlines");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var pushed = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushed.Add((account, (AsObject)body));
            var a = accounts.RegisterPublic("deadlinea", "testing-password", "Deadline A");
            var b = accounts.RegisterPublic("deadlineb", "testing-password", "Deadline B");
            var id = Convert.ToInt32(((AsObject)games.Create(a, "Deadlines", "", 1, 2, AirGameDtos.DraftConfigId, "CLASSIC", "NONE"))["id"]);
            games.Join(b, id, "");
            games.Start(a.Id, id, Revision(games, a.Id, id));
            void Expire(int seconds) { clock.Now = clock.Now.AddSeconds(seconds); games.Tick(); }

            // A lets the first ban time out; B bans; both let the last two expire.
            Expire(PlatformGames.DraftBanServerSeconds);
            TestAssert.Equal(2, Game(games, a)["pickTurn"], "a ban not made in time is given up");
            games.Ban(b.Id, 1);
            Expire(PlatformGames.DraftBanServerSeconds);
            Expire(PlatformGames.DraftBanServerSeconds);
            var game = Game(games, a);
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal(new object[] { 1 }, Banned(game));

            // A picks without locking in: the turn's end locks it in. B makes no pick: B is dropped as AFK and A is back in the lobby.
            games.Pick(a.Id, 64);
            Expire(PlatformGames.DraftPickServerSeconds);
            game = Game(games, a);
            TestAssert.Equal(2, game["pickTurn"]);
            TestAssert.Equal(2, PickMode(game, a), "A's pick was locked in for A");
            pushed.Clear();
            Expire(PlatformGames.DraftPickServerSeconds);
            TestAssert.True(games.Current(b.Id) is null, "B made no pick in B's turn");
            TestAssert.Equal("TERMINATED", pushed.Last(p => p.Account == b.Id).Body["gameState"]);
            game = Game(games, a);
            TestAssert.Equal("TEAM_SELECT", game["gameState"]);
            TestAssert.Equal(1, game["pickTurn"]);
            TestAssert.Equal(0, ((ArrayCollection)game["bannedChampionIds"]).Count, "the bans start over with the next selection");
            TestAssert.Equal(0, ((ArrayCollection)game["playerChampionSelections"]).Count);

            // One human and a bot: no human captain on Chaos, so no ban phase. The bot has no turn and its champion is taken for
            // both teams (exclusivePick); blind pick allows that mirror.
            games.AddBot(a.Id, 1, Team.Chaos, 0);
            games.Start(a.Id, id, Revision(games, a.Id, id));
            game = Game(games, a);
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            var bot = ((ArrayCollection)game["teamTwo"]).Cast<AsObject>().Single();
            TestAssert.Equal(0, bot["pickTurn"]);
            TestAssert.Equal(2, bot["pickMode"]);
            TestAssert.Equal(1, PickTurn(game, a));
            TestAssert.False(Picked(games.Pick(a.Id, 1), a, 1), "the Chaos bot holds Annie");
            games.Pick(a.Id, 61);
            games.LockIn(a.Id);
            TestAssert.Equal("POST_CHAMP_SELECT", Game(games, a)["gameState"]);
        }
        finally { DeleteDirectory(root); }
    }

    private static AirFault Fault(Action action)
    {
        try { action(); }
        catch (AirFault fault) { return fault; }
        throw new InvalidOperationException("The call was expected to be refused with a typed platform exception.");
    }

    private static AsObject Game(PlatformGames games, PlatformAccount account) => (AsObject)games.Current(account.Id)!;
    private static long Revision(PlatformGames games, long account, int id) =>
        (long)(double)((AsObject)games.Get(account, id))["optimisticLock"];
    private static object[] Banned(AsObject game) => ((ArrayCollection)game["bannedChampionIds"]).Cast<object>().ToArray();
    private static AsObject Participant(AsObject game, PlatformAccount account) =>
        ((ArrayCollection)game["teamOne"]).Cast<AsObject>().Concat(((ArrayCollection)game["teamTwo"]).Cast<AsObject>())
            .Single(p => p.TypeName == "com.riotgames.platform.game.PlayerParticipant" && Equals(p["accountId"], (double)account.Id));
    private static int PickMode(AsObject game, PlatformAccount account) => (int)Participant(game, account)["pickMode"];
    private static int PickTurn(AsObject game, PlatformAccount account) => (int)Participant(game, account)["pickTurn"];
    private static bool Picked(object game, PlatformAccount account, int champion) =>
        ((ArrayCollection)((AsObject)game)["playerChampionSelections"]).Cast<AsObject>().Any(selection =>
            Equals(selection["summonerInternalName"], "sum" + account.SummonerId) && Equals(selection["championId"], champion));

    // The placeholder executable never starts: only the lobby and the reserved roster matter here.
    private static GameManager NonLaunchingManager(string root)
    {
        var executable = Path.Combine(root, "server-placeholder.exe");
        Directory.CreateDirectory(Path.Combine(root, "game-data"));
        File.WriteAllBytes(executable, []);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        return new GameManager(new HostSettings(Path.Combine(root, "game-data"), executable,
            FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
    }

    private static string TemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
