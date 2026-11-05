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
    // GameTypeConfig.ADVANCED_TUTORIAL_GAMETYPE_ID (GameTypeConfig.as:22).
    private const int AdvancedTutorialConfigId = 11;

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

    /// <summary>
    /// Batch 669: <c>gameService.createTutorialGame(1)</c>, Battle Training (the play menu's battle_training entry,
    /// ChooseGameFlowViewLayoutDefinition.as:158). The stock client opens a guided champion select on it (AdvancedTutorialController) that
    /// offers Garen, Ryze and Ashe, and the bundle names the map: "Battle Training - Summoner's Rift". The game is Summoner's Rift in mode
    /// TUTORIAL (the mode that loads Data\LoadingScreen\TutorialModeSettings.ini, advanced_tutorial_loadscreen.dds), the player and four bots
    /// against five.
    /// </summary>
    [Check("server.air-battle-training-starts-summoners-rift-with-four-bot-allies")]
    public static void BattleTrainingStarts() => BattleTrainingAsync().GetAwaiter().GetResult();

    private static async Task BattleTrainingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"league-air-battle-training-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var account = accounts.RegisterPublic("battletrainee", "testing-password", "Battle Trainee", maxed: false);
            var session = sessions.Login("rpc", account.Username, sessions.IssueTicket(account), Version);
            TestAssert.True(sessions.AuthenticateChannel("rpc", account.Username, session.Token));
            static int Id(string name) => AirGameDtos.ChampionNames.Single(pair => pair.Value == name).Key;
            TestAssert.False(new[] { "Garen", "Ryze", "Ashe" }.Any(name => PlatformEconomy.CanPlay(accounts.Get(account.Id), Id(name), accounts.EconomyRules)),
                "the new account owns none of the tutorial's champions");

            var game = (AsObject)rpc.Route("rpc", "gameService", "createTutorialGame", [1d])!;
            TestAssert.Equal("CHAMP_SELECT", game["gameState"], "the guided champion select (ChampionSelectionController.as:1286)");
            TestAssert.Equal("TUTORIAL_GAME", game["gameType"]);
            TestAssert.Equal("TUTORIAL", game["gameMode"]);
            TestAssert.Equal(GameMaps.Normal, game["mapId"], "Summoner's Rift");
            TestAssert.Equal(AdvancedTutorialConfigId, game["gameTypeConfigId"], "GameTypeConfig.ADVANCED_TUTORIAL_GAMETYPE_ID");
            var config = AirDtos.GameTypes().Cast<AsObject>().Single(c => Equals(c["id"], (double)AdvancedTutorialConfigId));
            TestAssert.True((double)config["mainPickTimerDuration"] > 60 + 3, "longer than the client's 60-second warning");
            var one = ((ArrayCollection)game["teamOne"]).Cast<AsObject>().ToArray();
            var two = ((ArrayCollection)game["teamTwo"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(5, one.Length);
            TestAssert.Equal(5, two.Length);
            TestAssert.Equal((double)account.Id, one[0]["accountId"], "the player leads team one");
            TestAssert.True(one.Skip(1).Concat(two).All(p => p.TypeName == BotAlias), "the other nine seats are bots");
            var bots = ((ArrayCollection)game["playerChampionSelections"]).Cast<AsObject>()
                .Select(s => (string)s["summonerInternalName"]).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            TestAssert.Equal(new[] { "bot_Annie_200", "bot_Malphite_200", "bot_MasterYi_100", "bot_MasterYi_200", "bot_Nasus_200",
                "bot_Shen_100", "bot_Sivir_100", "bot_Sivir_200", "bot_Soraka_100" }, bots, "the player picks; the bots hold their champions");

            // The guided grid lists only the tutorial's three (AdvancedTutorialController.tutorialChampions), owned or not.
            TestAssert.Throws<ArgumentException>(() => rpc.Route("rpc", "gameService", "selectChampion", [(double)Id("Annie")]));
            var picked = (AsObject)rpc.Route("rpc", "gameService", "selectChampion", [(double)Id("Ryze")])!;
            TestAssert.Equal(Id("Ryze"), ((ArrayCollection)picked["playerChampionSelections"]).Cast<AsObject>()
                .Single(s => !((string)s["summonerInternalName"]).StartsWith("bot_", StringComparison.Ordinal))["championId"]);
            rpc.Route("rpc", "gameService", "championSelectCompleted", []);
            TestAssert.Equal("POST_CHAMP_SELECT", ((AsObject)games.Current(account.Id)!)["gameState"]);
            clock.Now += TimeSpan.FromSeconds(PlatformGames.PostPickServerSeconds + 1);
            games.Tick();
            TestAssert.Equal("START_REQUESTED", ((AsObject)games.Current(account.Id)!)["gameState"]);

            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(10, roster.Length);
            TestAssert.True(roster[0] is { IsBot: false, Champion: "Ryze", Team: Team.Order }, "the lent Ryze on Order");
            TestAssert.Equal(4, roster.Count(p => p.IsBot && p.Team == Team.Order));
            TestAssert.Equal(5, roster.Count(p => p.IsBot && p.Team == Team.Chaos));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Batch 686 (community report: leaving a tutorial left the player "assigned to an existing game"): the stock client goes home when its
    /// tutorial game closes, crashed or abandoned, and offers no reconnect (InGameController.onGameCrashedOrAbandoned: a tutorial calls
    /// cancelGameFlow), so the platform ends a tutorial whose player has left. A tutorial still loading (no seat connected yet), still
    /// played, or finished (its result decides) is kept, and so is any other game's disconnected seat (that player may reconnect).
    /// </summary>
    [Check("server.air-tutorial-ends-when-its-player-leaves")]
    public static void TutorialEndsWhenItsPlayerLeaves()
    {
        foreach (var tutorial in new[] { LeaverGame(GameMaps.Tutorial), LeaverGame(GameMaps.Normal, GameMaps.TutorialMode) })
        {
            TestAssert.False(Left(tutorial, LeaverRemote("Waiting for players", connected: false)), "still loading");
            TestAssert.False(Left(tutorial, LeaverRemote("Playing", connected: true)), "being played");
            TestAssert.True(Left(tutorial, LeaverRemote("Playing", connected: false)), "its player has left");
            TestAssert.False(Left(tutorial, LeaverRemote("Finished", connected: false)), "a finished game has its result");
        }
        var left = LeaverGame(GameMaps.Tutorial);
        TestAssert.False(Left(left, LeaverRemote("Waiting for players", connected: true)), "connected while loading");
        TestAssert.True(Left(left, LeaverRemote("Waiting for players", connected: false)), "and gone before the start");

        // Batch 686b: restated to two humans; a custom game with one human now ends too (server.air-solo-custom-game-ends-when-its-player-leaves).
        var custom = LeaverGame(GameMaps.Normal, humans: 2);
        TestAssert.False(Left(custom, LeaverRemote("Playing", connected: true, other: true)));
        TestAssert.False(Left(custom, LeaverRemote("Playing", connected: false, other: true)), "a custom game with others waits for a reconnect");
    }

    /// <summary>
    /// Batch 686b, NOT RETAIL (owner, 4 November 2026: "Custom games should end instantly the moment a player disconnects if only a
    /// single player is present in a game"): a custom game whose only human (bots do not count) was connected and has left ends at once,
    /// released like a tutorial. Two or more humans play on (the leaver may reconnect), and matchmade games are untouched.
    /// </summary>
    [Check("server.air-solo-custom-game-ends-when-its-player-leaves")]
    public static void SoloCustomGameEndsWhenItsPlayerLeaves()
    {
        foreach (var map in new[] { GameMaps.Normal, GameMaps.TwistedTreeline, GameMaps.CrystalScar })
        {
            var solo = LeaverGame(map);
            TestAssert.False(Left(solo, LeaverRemote("Waiting for players", connected: false)), "still loading");
            TestAssert.False(Left(solo, LeaverRemote("Playing", connected: true)), "being played");
            TestAssert.True(Left(solo, LeaverRemote("Playing", connected: false), TimeSpan.FromSeconds(1)), "one human with bots: ends at once");
            TestAssert.False(Left(solo, LeaverRemote("Finished", connected: false)), "a finished game has its result");
        }
        var pair = LeaverGame(GameMaps.Normal, humans: 2);
        TestAssert.False(Left(pair, LeaverRemote("Playing", connected: true, other: true)));
        TestAssert.False(Left(pair, LeaverRemote("Playing", connected: false, other: true)), "two humans, one leaves: stays");
        TestAssert.False(Left(pair, LeaverRemote("Playing", connected: false, other: false)), "and keeps today's rule after both");

        var coop = LeaverGame(GameMaps.Normal);
        coop.QueueId = PlatformGames.Queues.First(queue => queue.Type == "BOT").Id;
        TestAssert.True(coop.IsMatchmade);
        TestAssert.False(Left(coop, LeaverRemote("Playing", connected: true)));
        TestAssert.False(Left(coop, LeaverRemote("Playing", connected: false)), "a matchmade game is untouched");
    }

    /// <summary>
    /// Batch 686b (owner, 4 November 2026): a tutorial whose player never connects (a game client that failed before joining) ends three
    /// minutes after its launch, released like a tutorial its player left; a player who connected and left is released at once, and no
    /// other game is touched however long its seat stays empty.
    /// </summary>
    [Check("server.air-tutorial-ends-when-its-player-never-joins")]
    public static void TutorialEndsWhenItsPlayerNeverJoins()
    {
        var limit = TimeSpan.FromMinutes(3);
        foreach (var state in new[] { "Starting", "Waiting for players" })
            foreach (var tutorial in new[] { LeaverGame(GameMaps.Tutorial), LeaverGame(GameMaps.Normal, GameMaps.TutorialMode) })
            {
                var waiting = LeaverRemote(state, connected: false);
                TestAssert.False(Left(tutorial, waiting, limit - TimeSpan.FromSeconds(1)), $"{state}: never connected at 2:59 stays");
                TestAssert.True(Left(tutorial, waiting, limit), $"{state}: never connected at 3:00 ends");
            }
        var left = LeaverGame(GameMaps.Tutorial);
        TestAssert.False(Left(left, LeaverRemote("Waiting for players", connected: true), TimeSpan.FromSeconds(20)), "connected");
        TestAssert.True(Left(left, LeaverRemote("Waiting for players", connected: false), TimeSpan.FromSeconds(30)), "connected then left ends at once");
        foreach (var other in new[] { LeaverGame(GameMaps.Normal), LeaverGame(GameMaps.TwistedTreeline) })
        {
            TestAssert.False(Left(other, LeaverRemote("Waiting for players", connected: false), limit), "a non-tutorial game is untouched");
            TestAssert.False(Left(other, LeaverRemote("Playing", connected: false), TimeSpan.FromHours(1)), "however long");
        }
    }

    private static readonly DateTimeOffset LeaverLaunch = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlatformAccount Leaver =
        new(7, 70, "tutorialleaver", "Tutorial Leaver", "", "", new MatchPlayer("Tutorial Leaver", "Ashe", Team.Order));

    private static bool Left(PlatformGames.Lobby game, RemoteGame remote, TimeSpan sinceLaunch = default) =>
        PlatformGames.PlayerLeftGame(game, remote, LeaverLaunch + sinceLaunch);

    private static readonly PlatformAccount Partner =
        new(8, 80, "tutorialpartner", "Tutorial Partner", "", "", new MatchPlayer("Tutorial Partner", "Annie", Team.Chaos));

    /// <param name="other">Null: the game has no second human; otherwise the second human's seat and whether it is connected.</param>
    private static RemoteGame LeaverRemote(string state, bool connected, bool? other = null) => new(Guid.NewGuid(), "host", state, 5119,
        [new(1, Leaver.Loadout with { PlayerId = 70 }, true, true, connected, connected),
         .. other is { } partner ? [new RemoteSeat(2, Partner.Loadout with { PlayerId = 80 }, true, false, partner, partner)] : Array.Empty<RemoteSeat>(),
         new(3, PlatformBots.Seat(AirGameDtos.ChampionNames.Single(pair => pair.Value == "Nunu").Key, Team.Order, 0), false, false, false, false)],
        CanStop: true, Error: null);

    private static PlatformGames.Lobby LeaverGame(int map, string? mode = null, int humans = 1) => new(1, "game", "", Leaver.Id, 3)
    {
        MapId = map, Mode = mode, State = "IN_PROGRESS", HostId = Guid.NewGuid(), CredentialsSent = true, Launched = LeaverLaunch,
        Members = humans == 1 ? [new(Leaver, Team.Order)] : [new(Leaver, Team.Order), new(Partner, Team.Chaos)],
    };

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
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

            // Batch 669: Battle Training (1) is a game now (server.air-battle-training-starts-summoners-rift-with-four-bot-allies); a type the
            // client never sends is still refused with its own unsupported-mode text and leaves no game.
            TestAssert.Throws<ArgumentException>(() => rpc.Route("rpc", "gameService", "createTutorialGame", [2d]));
            TestAssert.True(games.Current(account.Id) is null, "a refused tutorial type leaves no game");

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
