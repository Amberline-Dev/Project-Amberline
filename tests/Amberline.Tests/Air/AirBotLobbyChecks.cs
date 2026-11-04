using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using Amberline.Simulation.Bots;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>Bots in AIR custom games: lobby RPCs, DTOs, launch, results and the bot loadout files.</summary>
public static class AirBotLobbyChecks
{
    private const string Version = "1.45.11_10_04_15_28";
    private const string BotAlias = "com.riotgames.platform.game.BotParticipant";
    // Annie, Orianna, Lee Sin, Nidalee, Yorick, Shaco, Ezreal, Vayne, Janna, Blitzcrank.
    private static readonly int[] Roster = [1, 61, 64, 76, 83, 35, 81, 67, 40, 53];

    [Check("server.air-bot-lobby-rpc-round-trip-and-dto-shape")]
    public static void SelectAndRemoveBotChampionRoundTripThroughTheStockLobbyDto() =>
        RpcAsync().GetAwaiter().GetResult();

    private static async Task RpcAsync()
    {
        var root = TemporaryDirectory("league-air-bot-rpc");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            var pushed = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushed.Add((account, (AsObject)body));
            var owner = accounts.RegisterPublic("botowner", "testing-password", "Bot Owner");
            var friend = accounts.RegisterPublic("botfriend", "testing-password", "Bot Friend");
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            Login(sessions, "owner", owner);
            Login(sessions, "friend", friend);
            var lobby = (AsObject)games.Create(owner, "Bots", "", 1, 10, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
            var id = Convert.ToInt32(lobby["id"]);
            games.Join(friend, id, "");
            var revision = (double)((AsObject)games.Get(owner.Id, id))["optimisticLock"];
            pushed.Clear();

            // The stock lobby sends the champion id and the BotParticipant it built for that champion.
            TestAssert.True(rpc.Route("owner", "gameService", "selectBotChampion", [1d, ClientBot(1, "100")]) is null);
            var afterAdd = LastGame(pushed, friend.Id);
            TestAssert.True((double)afterAdd["optimisticLock"] > revision, "a bot change is a new lobby revision");
            TestAssert.True(pushed.Any(p => p.Account == owner.Id) && pushed.Any(p => p.Account == friend.Id),
                "every lobby member receives the updated GameDTO");
            var teamOne = ((ArrayCollection)afterAdd["teamOne"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(2, teamOne.Length, "the owner and one bot");
            var bot = teamOne.Single(p => p.TypeName == BotAlias);
            TestAssert.Equal("Annie bot", bot["summonerName"]);
            TestAssert.Equal("bot_Annie_100", bot["summonerInternalName"]);
            TestAssert.Equal("100", bot["teamId"]);
            TestAssert.Equal(0d, bot["botSkillLevel"]);
            TestAssert.Equal("Basic", bot["botSkillLevelName"], "the client's own difficulty text is echoed back");
            TestAssert.True(bot["champion"] is null, "the stock client fills the champion from its registry");
            TestAssert.Equal(0, bot["pickMode"]);
            var selection = ((ArrayCollection)afterAdd["playerChampionSelections"]).Cast<AsObject>()
                .Single(s => (string)s["summonerInternalName"] == "bot_Annie_100");
            TestAssert.Equal(1, selection["championId"]);
            // AnnieBotSummonerSpells.inibin (eNEWBIE): SummonerDot then SummonerMana.
            TestAssert.Equal(14d, selection["spell1Id"]);
            TestAssert.Equal(13d, selection["spell2Id"]);

            games.Join(accounts.RegisterPublic("botlate", "testing-password", "Bot Late"), id, "");
            TestAssert.True(rpc.Route("owner", "gameService", "selectBotChampion", [61d, ClientBot(61, "200", "Beginner")]) is null);
            var afterSecond = LastGame(pushed, owner.Id);
            var chaosBot = ((ArrayCollection)afterSecond["teamTwo"]).Cast<AsObject>().Single(p => p.TypeName == BotAlias);
            TestAssert.Equal("bot_Orianna_200", chaosBot["summonerInternalName"]);
            TestAssert.Equal("Beginner", chaosBot["botSkillLevelName"], "a localized difficulty name survives the round trip");
            var orianna = ((ArrayCollection)afterSecond["playerChampionSelections"]).Cast<AsObject>()
                .Single(s => (string)s["summonerInternalName"] == "bot_Orianna_200");
            TestAssert.Equal(4d, orianna["spell1Id"], "a champion without bot files gets the platform default spells");
            TestAssert.Equal(14d, orianna["spell2Id"]);
            var search = ((ArrayCollection)games.List()).Cast<AsObject>().Single(g => Convert.ToInt32(g["id"]) == id);
            TestAssert.Equal(2, search["team1Count"], "bots count toward the lobby team sizes");
            TestAssert.Equal(3, search["team2Count"]);

            TestAssert.Throws<ArgumentException>(() => rpc.Route("friend", "gameService", "selectBotChampion",
                [64d, ClientBot(64, "100")]), "only the lobby owner adds bots");
            TestAssert.Throws<ArgumentException>(() => rpc.Route("owner", "gameService", "selectBotChampion",
                [64d, ClientBot(64, "300")]), "the team is 100 or 200");
            TestAssert.Throws<ArgumentException>(() => rpc.Route("owner", "gameService", "selectBotChampion",
                [61d, ClientBot(64, "100")]), "the champion id and the BotParticipant champion must agree");

            // The stock client returns the BotParticipant it received from the server.
            TestAssert.True(rpc.Route("owner", "gameService", "removeBotChampion", [1d, bot]) is null);
            TestAssert.True(rpc.Route("owner", "gameService", "removeBotChampion", [61d, chaosBot]) is null);
            var afterRemove = LastGame(pushed, friend.Id);
            TestAssert.Equal(0, ((ArrayCollection)afterRemove["teamOne"]).Cast<AsObject>().Count(p => p.TypeName == BotAlias));
            TestAssert.Equal(0, ((ArrayCollection)afterRemove["teamTwo"]).Cast<AsObject>().Count(p => p.TypeName == BotAlias));
            TestAssert.Equal(0, ((ArrayCollection)afterRemove["playerChampionSelections"]).Count);
            TestAssert.Throws<ArgumentException>(() => rpc.Route("owner", "gameService", "removeBotChampion", [1d, bot]),
                "a bot that is gone cannot be removed twice");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-bot-lobby-validation")]
    public static void BotSeatsFollowTeamCapacityChampionAndOwnerRules() => ValidationAsync().GetAwaiter().GetResult();

    private static async Task ValidationAsync()
    {
        var root = TemporaryDirectory("league-air-bot-validation");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            games.Push = (_, _) => { };
            PlatformAccount NewAccount(string name) => accounts.RegisterPublic(name.Replace(' ', '_').ToLowerInvariant(), "testing-password", name);

            // A full lobby of ten: one human and nine bots (four beside the owner, five opposing).
            var host = NewAccount("Bot Host");
            var full = Id(games.Create(host, "Nine bots", "", 1, 10, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            foreach (var champion in Roster.Take(4)) games.AddBot(host.Id, champion, Team.Order, 0);
            TestAssert.Throws<ArgumentException>(() => games.AddBot(host.Id, Roster[4], Team.Order, 0),
                "the owner's team holds five: the owner and four bots");
            foreach (var champion in Roster.Take(5)) games.AddBot(host.Id, champion, Team.Chaos, 0);
            TestAssert.Throws<ArgumentException>(() => games.AddBot(host.Id, Roster[5], Team.Chaos, 0),
                "no team and no lobby holds more than five and ten");
            var fullGame = (AsObject)games.Get(host.Id, full);
            TestAssert.Equal(5, ((ArrayCollection)fullGame["teamOne"]).Count);
            TestAssert.Equal(5, ((ArrayCollection)fullGame["teamTwo"]).Count);
            TestAssert.Throws<ArgumentException>(() => games.Join(NewAccount("Bot Late"), full, ""), "bots occupy lobby slots");
            TestAssert.False(games.SwitchTeam(host.Id, full), "bots fill the other team: the Boolean answer is false, not a fault");

            // Champion, difficulty, owner and state rules.
            var owner = NewAccount("Rule Owner");
            var friend = NewAccount("Rule Friend");
            var lobby = Id(games.Create(owner, "Rules", "", 1, 10, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            games.Join(friend, lobby, "");
            games.AddBot(owner.Id, 1, Team.Chaos, 0);
            TestAssert.Throws<ArgumentException>(() => games.AddBot(owner.Id, 1, Team.Chaos, 0),
                "a team cannot hold the same champion twice");
            games.AddBot(owner.Id, 1, Team.Order, 0);
            TestAssert.Throws<ArgumentException>(() => games.AddBot(owner.Id, 999, Team.Chaos, 0), "unknown champion");
            TestAssert.Throws<ArgumentException>(() => games.AddBot(owner.Id, 0, Team.Chaos, 0), "no champion");
            TestAssert.Throws<ArgumentException>(() => games.AddBot(owner.Id, 61, Team.Chaos, 4), "unsupported difficulty");
            TestAssert.Throws<ArgumentException>(() => games.AddBot(owner.Id, 61, Team.Chaos, -1), "unsupported difficulty");
            TestAssert.Throws<ArgumentException>(() => games.AddBot(friend.Id, 61, Team.Chaos, 0), "only the owner adds bots");
            TestAssert.Throws<ArgumentException>(() => games.RemoveBot(friend.Id, 1, Team.Chaos), "only the owner removes bots");
            TestAssert.Throws<ArgumentException>(() => games.RemoveBot(owner.Id, 53, Team.Chaos), "no such bot");
            games.AddBot(owner.Id, 64, Team.Order, 0);
            games.AddBot(owner.Id, 76, Team.Order, 0);

            // Champion select: bots are seated, not picking. Humans cannot take a champion a teammate bot holds.
            games.Start(owner.Id, lobby, (long)(double)((AsObject)games.Get(owner.Id, lobby))["optimisticLock"]);
            var selecting = (AsObject)games.Get(owner.Id, lobby);
            TestAssert.Equal("CHAMP_SELECT", selecting["gameState"]);
            var bots = ((ArrayCollection)selecting["teamOne"]).Cast<AsObject>().Concat(((ArrayCollection)selecting["teamTwo"]).Cast<AsObject>())
                .Where(p => p.TypeName == BotAlias).ToArray();
            TestAssert.Equal(4, bots.Length);
            TestAssert.True(bots.All(b => Equals(2, b["pickMode"])), "bots keep their champion and have finished picking");
            TestAssert.Throws<ArgumentException>(() => games.AddBot(owner.Id, 61, Team.Chaos, 0), "no bots after team select");
            TestAssert.Throws<ArgumentException>(() => games.RemoveBot(owner.Id, 1, Team.Chaos), "no bot changes after team select");
            // A champion a teammate holds is not refused: the unchanged game comes back and the client reports it taken.
            TestAssert.False(Picked(games.Pick(owner.Id, 1), owner, 1), "the owner's team already has an Annie bot");
            TestAssert.False(Picked(games.Pick(owner.Id, 64), owner, 64), "the owner's team already has a Lee Sin bot");
            TestAssert.False(Picked(games.Pick(friend.Id, 1), friend, 1), "the friend's team already has an Annie bot");
            games.Pick(friend.Id, 61); // Orianna: no Chaos bot holds it, and opposing mirror picks stay allowed
            games.Pick(owner.Id, 81);
            games.LockIn(owner.Id);
            TestAssert.Equal("CHAMP_SELECT", ((AsObject)games.Get(owner.Id, lobby))["gameState"], "bots do not lock in");
            games.LockIn(friend.Id);
            TestAssert.Equal("POST_CHAMP_SELECT", ((AsObject)games.Get(owner.Id, lobby))["gameState"],
                "the humans alone complete champion selection");

            // A four-seat lobby holds two per team.
            var small = NewAccount("Small Owner");
            var smallId = Id(games.Create(small, "Small", "", 1, 4, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            games.AddBot(small.Id, 1, Team.Order, 0);
            games.AddBot(small.Id, 1, Team.Chaos, 0);
            games.AddBot(small.Id, 61, Team.Chaos, 0);
            TestAssert.Throws<ArgumentException>(() => games.AddBot(small.Id, 64, Team.Order, 0), "the lobby is full");
            TestAssert.Equal(4, Occupants((AsObject)games.Get(small.Id, smallId)));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-bot-launch-reserves-human-seats-and-appends-bots")]
    public static void LaunchBuildsOneHumanAndNineBotSeats() => LaunchAsync().GetAwaiter().GetResult();

    private static async Task LaunchAsync()
    {
        var root = TemporaryDirectory("league-air-bot-launch");
        try
        {
            using var accounts = new PlatformAccounts(root);
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            games.Push = (_, _) => { };
            var owner = accounts.RegisterPublic("launchowner", "testing-password", "Launch Owner");
            var lobby = Id(games.Create(owner, "Nine bots", "", 1, 10, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            // Annie and the other champions appear on both teams to exercise unique roster names.
            foreach (var champion in Roster.Take(4)) games.AddBot(owner.Id, champion, Team.Order, 0);
            foreach (var champion in Roster.Take(5)) games.AddBot(owner.Id, champion, Team.Chaos, 0);
            games.Start(owner.Id, lobby, (long)(double)((AsObject)games.Get(owner.Id, lobby))["optimisticLock"]);
            games.Pick(owner.Id, 81);
            games.LockIn(owner.Id);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();

            // The manager cannot start its placeholder executable; the reserved roster is still written.
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(10, roster.Length);
            TestAssert.Equal(1, roster.Count(p => !p.IsBot));
            TestAssert.Equal(9, roster.Count(p => p.IsBot));
            TestAssert.Equal(5, roster.Count(p => p.Team == Team.Order));
            TestAssert.Equal(5, roster.Count(p => p.Team == Team.Chaos));
            TestAssert.False(roster[0].IsBot, "the human seat stays first so seat number = member index + 1");
            TestAssert.Equal(owner.Summoner, roster[0].Name);
            TestAssert.Equal("Ezreal", roster[0].Champion);
            TestAssert.Equal(owner.SummonerId, (long)roster[0].PlayerId!.Value);
            TestAssert.True(roster.Skip(1).All(p => p.IsBot && p.PlayerId is null && p.SkinId == 0), "bot seats: skin 0, numbered by the server");
            // Owner, 26 October 2026: cheats for everyone. This human seat has no direct session on this PC (as a friend's tunnel seat),
            // and it still gets the cheat commands; bot seats never do.
            TestAssert.True(roster[0].CanCheat, "every human seat may cheat");
            TestAssert.False(roster.Skip(1).Any(p => p.CanCheat), "no bot seat may cheat");
            TestAssert.Equal(10, roster.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "roster names are unique");
            TestAssert.True(roster.Any(p => p.Name == "Annie Bot") && roster.Any(p => p.Name == "Annie Bot 2"),
                "the same champion on both teams keeps its in-game bot name and gets a numeric suffix");
            TestAssert.True(roster.Where(p => p.IsBot).All(p => p.Name.EndsWith(" Bot") || p.Name.Contains(" Bot ")),
                "every bot seat is named in the retail \"<champion> Bot\" format, not the lobby's \"<skin> bot\"");
            TestAssert.True(roster.Where(p => p.IsBot && p.Champion == "Annie").All(p =>
                p.SummonerSpell1 == "SummonerDot" && p.SummonerSpell2 == "SummonerMana"), "Annie bots use AnnieBotSummonerSpells.inibin");
            TestAssert.True(roster.Where(p => p.IsBot && p.Champion == "Orianna").All(p =>
                p.SummonerSpell1 == "SummonerFlash" && p.SummonerSpell2 == "SummonerDot" &&
                p.RedRunes is null && p.Masteries is null), "a champion without bot files gets default spells and empty pages");
            TestAssert.Equal(10, MatchRoster.ToParticipants(roster).Length);
            TestAssert.Equal(9, MatchRoster.ToParticipants(roster).Count(p => p.IsBot));

            var guest = PlatformGames.Guest(owner);
            var seats = manager.List(guest).Single().Seats;
            TestAssert.Equal(10, seats.Length);
            TestAssert.True(seats[0].Claimed && seats[0].Yours, "the human seat is reserved for the account");
            TestAssert.True(seats.Skip(1).All(s => s.Player.IsBot && !s.Claimed && !s.Yours), "bot seats hold no reservation");

            var handoff = JsonSerializer.Deserialize<PlatformPendingMatch>(
                File.ReadAllText(Path.Combine(root, "match-handoffs", lobby + ".json")), MatchRoster.JsonOptions)!;
            TestAssert.Equal([owner.Id], handoff.Accounts, "receipts list the human account only");
            TestAssert.Equal(10, handoff.Players.Length);
            TestAssert.True(handoff.Players.Skip(1).All(p => p.IsBot));

            // CreateReserved keeps its invariants with bot seats.
            var human = roster[0];
            var bot = roster[1];
            var other = new HostGuest("air:other", "Other", "");
            TestAssert.Throws<ArgumentException>(() => manager.CreateReserved(guest, new(Guid.NewGuid(), [human, bot]), [guest, other]),
                "a bot seat takes no participant");
            TestAssert.Throws<ArgumentException>(() => manager.CreateReserved(guest, new(Guid.NewGuid(), [human, bot]), []),
                "a human seat needs a participant");
            TestAssert.Throws<ArgumentException>(() => manager.CreateReserved(guest,
                new(Guid.NewGuid(), [human, human with { Name = "Second", PlayerId = 1_000_099 }, bot]), [guest, guest]),
                "human participants must be distinct");
            TestAssert.Throws<ArgumentException>(() => manager.CreateReserved(guest, new(Guid.NewGuid(), [bot]), []),
                "a roster needs a human");
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// Batch 680 (owner, 4 November 2026: "selected person will have cheats in all matches - only toggleable by the host"; it restates
    /// batch 666's "Disable cheats" switch): a home host's games give the cheat commands to the logins Settings' list ticks, the host's
    /// own as any other, and to nobody else. Before the list's first Save, batch 666's switch on ticks the host's own login (the
    /// migration); once the list is saved, the old switch is ignored. The operator's start keeps every human seat; bots never cheat.
    /// </summary>
    [Check("server.host-cheats-off-by-default")]
    public static void HomeGamesGiveCheatsOnlyToTheLoginsSettingsTicks() => HomeCheatsAsync().GetAwaiter().GetResult();

    private static async Task HomeCheatsAsync()
    {
        TestAssert.Equal((true, true), await CheatSeatsAsync(null), "the operator's start: every human seat");
        TestAssert.Equal((false, false), await CheatSeatsAsync((host, _) => new { hostAccount = host }), "a platform.json from before the switch: nobody");
        TestAssert.Equal((false, false), await CheatSeatsAsync((host, _) => new { hostAccount = host, allowCheats = false }), "the old switch off: nobody");
        TestAssert.Equal((true, false), await CheatSeatsAsync((host, _) => new { hostAccount = host, allowCheats = true }),
            "the old switch on, no list yet: the migration ticks the host's own login");
        TestAssert.Equal((false, false), await CheatSeatsAsync((_, _) => new { allowCheats = true }), "the old switch on without a host login: nobody");
        TestAssert.Equal((false, true), await CheatSeatsAsync((host, friend) => new { hostAccount = host, allowCheats = true, cheatAccounts = new[] { friend } }),
            "a saved list: its friend only, the old switch ignored");
        TestAssert.Equal((true, true), await CheatSeatsAsync((host, friend) => new { hostAccount = host, cheatAccounts = new[] { friend, host } }),
            "both ticked: both");
        TestAssert.Equal((false, false), await CheatSeatsAsync((host, _) => new { hostAccount = host, allowCheats = true, cheatAccounts = Array.Empty<long>() }),
            "a saved empty list: nobody, the old switch ignored");
        TestAssert.Equal((false, false), await CheatSeatsAsync((_, _) => "{ damaged"), "a platform.json that cannot be read: nobody");
    }

    /// <summary>
    /// The cheat rights of the owner's (the host's) and a friend's seat in one launched custom game; <paramref name="settings"/> null is the
    /// operator's start, else home mode with the platform.json it makes from the owner's and the friend's account ids.
    /// </summary>
    private static async Task<(bool Host, bool Friend)> CheatSeatsAsync(Func<long, long, object>? settings)
    {
        var root = TemporaryDirectory("league-host-cheats");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock) { Push = (_, _) => { } };
            var owner = accounts.RegisterPublic("cheathost", "testing-password", "Cheat Host");
            var friend = accounts.RegisterPublic("cheatfriend", "testing-password", "Cheat Friend");
            if (settings is not null)
            {
                var file = Path.Combine(root, "platform.json");
                File.WriteAllText(file, settings(owner.Id, friend.Id) is string text ? text : JsonSerializer.Serialize(settings(owner.Id, friend.Id)));
                games.MayCheat = HostSettingsPage.CheatRule(file);
            }
            var lobby = Id(games.Create(owner, "Cheats", "", 1, 10, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            games.Join(friend, lobby, "");
            games.Start(owner.Id, lobby, (long)(double)((AsObject)games.Get(owner.Id, lobby))["optimisticLock"]);
            games.Pick(owner.Id, 81);
            games.Pick(friend.Id, 61);
            games.LockIn(owner.Id);
            games.LockIn(friend.Id);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            return (roster.Single(p => p.PlayerId == owner.SummonerId).CanCheat, roster.Single(p => p.PlayerId == friend.SummonerId).CanCheat);
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// Batch 463: an ODIN custom game on The Crystal Scar (map 8) takes bot seats as a CLASSIC one does: the stock add-bot RPC, the lobby
    /// DTO and the launch, whose reserved game runs on map 8 with the bots appended to the human seat.
    /// </summary>
    [Check("server.air-bot-lobby-accepts-bots-on-map-8")]
    public static void DominionLobbyAcceptsBotSeats() => DominionAsync().GetAwaiter().GetResult();

    private static async Task DominionAsync()
    {
        var root = TemporaryDirectory("league-air-bot-dominion");
        try
        {
            using var accounts = new PlatformAccounts(root);
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var pushed = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushed.Add((account, (AsObject)body));
            var owner = accounts.RegisterPublic("odinowner", "testing-password", "Odin Owner");
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            Login(sessions, "owner", owner);
            var lobby = Id(games.Create(owner, "Dominion bots", "", GameMaps.CrystalScar, 10, AirGameDtos.BlindPickConfigId,
                GameMaps.ModeOf(GameMaps.CrystalScar), "NONE"));
            TestAssert.True(rpc.Route("owner", "gameService", "selectBotChampion", [1d, ClientBot(1, "100")]) is null,
                "the stock add-bot call is accepted in an ODIN lobby");
            foreach (var champion in Roster.Skip(1).Take(3)) games.AddBot(owner.Id, champion, Team.Order, 0);
            foreach (var champion in Roster.Take(5)) games.AddBot(owner.Id, champion, Team.Chaos, 0);
            var dto = LastGame(pushed, owner.Id);
            TestAssert.Equal("ODIN", dto["gameMode"], "the lobby stays a Dominion game");
            TestAssert.Equal(GameMaps.CrystalScar, Convert.ToInt32(dto["mapId"]), "on map 8");
            TestAssert.Equal(5, ((ArrayCollection)dto["teamOne"]).Count, "the owner and four bots");
            TestAssert.Equal(5, ((ArrayCollection)dto["teamTwo"]).Cast<AsObject>().Count(p => p.TypeName == BotAlias), "five opposing bots");

            games.Start(owner.Id, lobby, (long)(double)((AsObject)games.Get(owner.Id, lobby))["optimisticLock"]);
            games.Pick(owner.Id, 81);
            games.LockIn(owner.Id);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();

            var directory = Directory.EnumerateDirectories(Path.Combine(root, "games")).Single();
            var roster = MatchRoster.Read(Path.Combine(directory, "roster.json"));
            TestAssert.Equal(10, roster.Length);
            TestAssert.Equal(9, roster.Count(p => p.IsBot), "nine bot seats reach the Dominion game");
            var offered = AirGameDtos.SpellNamesFor(GameMaps.CrystalScar).Values.ToHashSet();
            TestAssert.True(roster.Where(p => p.IsBot).All(p => offered.Contains(p.SummonerSpell1) && offered.Contains(p.SummonerSpell2)),
                "every bot's summoner spells are offered on map 8");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-bot-results-credit-only-humans")]
    public static void ResultsWithBotSeatsRecoverAndCreditOnlyHumans() => ResultsAsync().GetAwaiter().GetResult();

    private static async Task ResultsAsync()
    {
        var root = TemporaryDirectory("league-air-bot-results");
        try
        {
            var games = Path.Combine(root, "games");
            var clock = new ManualClock();
            using var accounts = new PlatformAccounts(root, timeProvider: clock);
            var alice = accounts.RegisterPublic("botresult", "testing-password", "Bot Result", maxed: false);
            var human = alice.Loadout with { Champion = "Ezreal", Team = Team.Order, PlayerId = (int)alice.SummonerId };
            var annie = PlatformBots.Seat(1, Team.Chaos, 0) with { Name = "Annie bot" };
            var orianna = PlatformBots.Seat(61, Team.Order, 0) with { Name = "Orianna bot" };
            var pending = new PlatformPendingMatch(accounts.AllocateGameId(), Guid.NewGuid(), 0, [alice.Id], [human, annie, orianna]);
            accounts.PrepareMatch(pending);
            TestAssert.Throws<ArgumentException>(() => accounts.PrepareMatch(pending with
            {
                GameId = accounts.AllocateGameId(), HostGameId = Guid.NewGuid(), Players = [human, annie with { IsBot = false }, orianna]
            }), "a handoff seat after the accounts must be a bot");

            var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Chaos", 600,
                [Player((ulong)alice.SummonerId, human, false), Player(2, annie, true), Player(3, orianna, true)], false);
            var folder = Path.Combine(games, pending.HostGameId.ToString("N"), "logs", "session");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "match-result.json"),
                JsonSerializer.Serialize(new { session_id = pending.HostGameId.ToString(), result }));
            await using var manager = new GameManager(new HostSettings(root, Environment.ProcessPath!, BindAddress: "127.0.0.1"), games);
            TestAssert.Equal(1, accounts.RecoverResults(manager, TextWriter.Null), "recovery accepts a result with bot seats");
            var receipt = accounts.ResultsFor(alice.Id).Single();
            TestAssert.Equal([alice.Id], receipt.Rewards!.Keys.ToArray(), "only the human account is rewarded");
            TestAssert.Equal(3, receipt.Result.Deserialize<MatchResult>()!.Players.Count);
            TestAssert.True(PlatformEconomy.For(accounts.Get(alice.Id)).Ip > 0);

            TestAssert.Throws<InvalidDataException>(() => accounts.RecordResult(new PlatformResult(accounts.AllocateGameId(),
                Guid.NewGuid(), [alice.Id], JsonSerializer.SerializeToElement(result with
                { Players = [result.Players[0], result.Players[1] with { IsBot = false }, result.Players[2]] }))),
                "a bot seat cannot stand in for a second human");
            TestAssert.Throws<InvalidDataException>(() => accounts.RecordResult(new PlatformResult(accounts.AllocateGameId(),
                Guid.NewGuid(), [alice.Id], JsonSerializer.SerializeToElement(result with
                { Players = [result.Players[0] with { IsBot = true }, result.Players[1], result.Players[2]] }))),
                "a human seat cannot be reported as a bot");

            var member = new PlatformGames.Member(accounts.Get(alice.Id), Team.Order);
            var lobby = new PlatformGames.Lobby(pending.GameId, "Bots", "", alice.Id, 10)
            {
                Members = [member],
                Bots = [new(1, Team.Chaos, 0, annie), new(61, Team.Order, 0, orianna)],
                Result = receipt.Result.Deserialize<MatchResult>()
            };
            var dto = AirGameDtos.Results(lobby, member, receipt.Rewards![alice.Id]);
            var mine = ((ArrayCollection)dto["teamPlayerParticipantStats"]).Cast<AsObject>().ToArray();
            var others = ((ArrayCollection)dto["otherTeamPlayerParticipantStats"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(2, mine.Length);
            TestAssert.Equal(1, others.Length);
            TestAssert.Equal(true, others[0]["botPlayer"]);
            TestAssert.Equal("Annie", others[0]["summonerName"], "the stock view resolves a bot's name from its champion");
            TestAssert.Equal(false, mine.Single(p => (double)p["userId"] == alice.SummonerId)["botPlayer"]);
            TestAssert.Equal(alice.Summoner, mine.Single(p => (double)p["userId"] == alice.SummonerId)["summonerName"]);
            var recent = (AsObject)((ArrayCollection)AirGameDtos.RecentGames(accounts.Get(alice.Id), accounts.ResultsFor(alice.Id))["gameStatistics"])[0];
            TestAssert.Equal(0, ((ArrayCollection)recent["fellowPlayers"]).Count, "bots are not fellow players");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-bot-enabled-champions-match-server-bot-roster")]
    public static void BotEnabledChampionsAreExactlyTheServerBotRoster()
    {
        var champions = ((ArrayCollection)AirDtos.Champions()).Cast<AsObject>().ToArray();
        TestAssert.Equal(ChampionCatalog.All.Count, champions.Length, "AIR lists all 84 champions, as retail did");
        TestAssert.Equal(AirGameDtos.ChampionNames.Count, champions.Count(c => (bool)c["owned"]),
            "only the champions the server plays are owned by an unlocked account");
        var enabled = champions.Where(c => (bool)c["botEnabled"]).Select(c => (string)c["skinName"]).Order().ToArray();
        TestAssert.Equal(BotController.SupportedChampions.Order().ToArray(), enabled,
            "the lobby picker offers exactly the champions the server can run as bots");
        TestAssert.Equal(ServerOptions.SupportedChampions.Order().ToArray(), enabled);
        foreach (var (id, name) in AirGameDtos.ChampionNames)
            TestAssert.Equal(BotController.SupportedChampions.Contains(name), PlatformBots.IsBotEnabled(id));
        TestAssert.False(PlatformBots.IsBotEnabled(999));
        TestAssert.Equal("Annie bot", PlatformBots.DisplayName(1), "the lobby keeps the stock text");
        TestAssert.Equal("Annie Bot", PlatformBots.MatchName(1), "Annie ships game_bot_Annie");
        TestAssert.Equal("Lee Sin Bot", PlatformBots.MatchName(64), "a champion without a key uses its display name");
        TestAssert.Equal("Blitzcrank Bot", PlatformBots.MatchName(53));
        TestAssert.Equal("bot_LeeSin_200", PlatformBots.InternalName(64, Team.Chaos));
    }

    [Check("server.air-bot-loadout-reads-shipped-inibin")]
    public static void BotLoadoutsComeFromTheShippedBotInibinFiles()
    {
        var paths = GameData.Default.Paths;
        var basic = BotLoadoutReader.Read(paths, "Annie", 0)!;
        TestAssert.Equal("SummonerDot", basic.SummonerSpell1);
        TestAssert.Equal("SummonerMana", basic.SummonerSpell2);
        TestAssert.Equal(0, basic.Runes.Count, "the Basic (eNEWBIE) section ships no runes");
        TestAssert.Equal(0, basic.Masteries.Count, "the Basic (eNEWBIE) section ships no masteries");
        var intermediate = BotLoadoutReader.Read(paths, "Annie", 1)!;
        TestAssert.Equal(22, intermediate.Runes.Count);
        TestAssert.Equal(7, intermediate.Masteries.Count);
        TestAssert.Equal(15, intermediate.Masteries.Values.Sum());
        foreach (var difficulty in new[] { 2, 3 })
        {
            var advanced = BotLoadoutReader.Read(paths, "Annie", difficulty)!;
            TestAssert.Equal(30, advanced.Runes.Count);
            TestAssert.Equal(13, advanced.Masteries.Count);
            TestAssert.Equal(30, advanced.Masteries.Values.Sum());
            TestAssert.True(advanced.Masteries.ContainsKey(120), "Mastery13 is stored as a numeric string");
        }
        var ashe = BotLoadoutReader.Read(paths, "Ashe", 0)!;
        TestAssert.Equal("SummonerDot", ashe.SummonerSpell1);
        TestAssert.Equal("SummonerExhaust", ashe.SummonerSpell2);
        TestAssert.True(BotLoadoutReader.Read(paths, "Orianna", 0) is null, "Orianna ships no bot files");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => BotLoadoutReader.Read(paths, "Annie", 5));

        var seat = PlatformBots.Seat(1, Team.Order, 0);
        TestAssert.True(seat.IsBot && seat.SkinId == 0 && seat.RedRunes is null && seat.Masteries is null);
        TestAssert.Equal("SummonerDot", seat.SummonerSpell1);
        var advancedSeat = PlatformBots.Seat(1, Team.Chaos, 2);
        TestAssert.Equal(9, advancedSeat.RedRunes!.Length);
        TestAssert.Equal(9, advancedSeat.YellowRunes!.Length);
        TestAssert.Equal(9, advancedSeat.BlueRunes!.Length);
        TestAssert.Equal(3, advancedSeat.BlackRunes!.Length);
        TestAssert.Equal(30, advancedSeat.Masteries!.Values.Sum());
        var defaults = PlatformBots.Seat(61, Team.Order, 0);
        TestAssert.Equal(SummonerLoadout.DefaultFirst, defaults.SummonerSpell1);
        TestAssert.Equal(SummonerLoadout.DefaultSecond, defaults.SummonerSpell2);
    }

    /// <summary>
    /// Batch 165: the add-bot list offers every champion the game server can run as a bot (the
    /// playable roster) and nothing else. Retail 126 offered only the 18 game_bot_ champions;
    /// the roster-wide list is the owner decision of 22 Oct 2026 (PlatformBots.IsBotEnabled).
    /// </summary>
    [Check("server.air-bot-list-offers-every-playable-champion")]
    public static void TheAddBotListOffersEveryChampionTheServerRunsAsABot()
    {
        var listed = ((ArrayCollection)AirDtos.Champions()).Cast<AsObject>()
            .Where(champion => (bool)champion["botEnabled"]).Select(champion => (string)champion["skinName"]).Order().ToArray();
        TestAssert.Equal(ChampionCatalog.PlayableNames.Order().ToArray(), listed,
            "the add-bot list holds every champion the game server runs as a bot, and no other");
        foreach (var champion in ChampionCatalog.Playable)
        {
            TestAssert.True(PlatformBots.IsBotEnabled(champion.Id), $"{champion.Name} is offered");
            var seat = PlatformBots.Seat(champion.Id, Team.Chaos, 0);
            TestAssert.True(seat.IsBot && seat.Champion == champion.Name && seat.SkinId == 0,
                $"{champion.Name} takes a bot seat from the list");
        }
        foreach (var champion in ChampionCatalog.All.Where(champion => !champion.Playable))
            TestAssert.False(PlatformBots.IsBotEnabled(champion.Id), $"{champion.Name} is not offered: the server cannot run it");
        TestAssert.False(PlatformBots.IsBotEnabled(999));
    }

    /// <summary>
    /// A champion the client ships no game_bot_ name for (every roster champion but the six retail
    /// bots) shows a real name in the lobby and in game. The client's CreateHero handler (0x4F, byte
    /// 16) renames an isBot hero to "game_bot_" + the skin and shows that raw key when fontconfig has
    /// no entry, so such a hero is created without the flag and named by the server.
    /// </summary>
    [Check("server.air-bot-name-of-a-non-retail-champion")]
    public static void ANonRetailBotShowsARealNameInLobbyAndInGame()
    {
        var data = GameData.Default;
        // The oracle is the fontconfig the client itself localizes from.
        var labels = Regex.Matches(File.ReadAllText(data.Paths.Data("Menu/fontconfig_en_US.txt")),
                "(?m)^tr\\s+\"(game_(?:bot|character_displayname)_[^\"]+)\"\\s*=\\s*\"([^\"]*)\"")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        foreach (var champion in ChampionCatalog.Playable)
        {
            var named = labels.ContainsKey("game_bot_" + champion.Name);
            TestAssert.Equal(champion.RetailBot, named, $"{champion.Name} has a client bot name exactly when it is a retail bot");
            TestAssert.Equal(named, data.ClientNamesBot(champion.Name));
            TestAssert.Equal(champion.Name + " bot", PlatformBots.DisplayName(champion.Id), "the lobby keeps the stock text");
            var name = PlatformBots.MatchName(champion.Id);
            TestAssert.Equal(data.BotName(champion.Name), name);
            if (named)
                TestAssert.Equal(labels["game_bot_" + champion.Name], name, $"{champion.Name} keeps the client's own text");
            else
                TestAssert.Equal(labels.GetValueOrDefault("game_character_displayname_" + champion.Name, champion.Name) + " Bot", name,
                    $"{champion.Name} is named in the retail format, never the raw key");
            TestAssert.False(name.Contains("game_", StringComparison.Ordinal), $"{champion.Name} shows no localization key");
            TestAssert.Equal(name, PlatformBots.Seat(champion.Id, Team.Order, 0).Name, "the match seat carries the same name");
        }
        // Orianna and Lee Sin: a one-word and a two-word display name.
        TestAssert.False(data.ClientNamesBot("Orianna"), "the raw key would show if the client renamed her");
        TestAssert.Equal("Orianna bot", PlatformBots.DisplayName(61));
        TestAssert.Equal("Orianna Bot", PlatformBots.MatchName(61));
        TestAssert.Equal("Lee Sin Bot", PlatformBots.MatchName(64));
        TestAssert.Equal("Twisted Fate Bot", PlatformBots.MatchName(4));
        TestAssert.Equal("Xin Zhao Bot", PlatformBots.MatchName(5));
        TestAssert.Equal("Annie Bot", PlatformBots.MatchName(1), "a retail bot keeps game_bot_Annie");
    }

    /// <summary>
    /// A champion with no shipped &lt;X&gt;Bot*.inibin gets batch 160's reconstructed loadout, never an
    /// empty one (the 29 September 2026 bot cards showed empty summoner spells): the platform default
    /// spell pair and the rune and mastery pages the retail Basic (eNEWBIE) arm ships. The shipped
    /// files win where they exist.
    /// </summary>
    [Check("server.air-bot-loadout-of-a-non-retail-champion-is-reconstructed")]
    public static void ANonRetailBotGetsTheReconstructedLoadoutNeverAnEmptyOne()
    {
        foreach (var champion in ChampionCatalog.Playable)
        {
            // The files exist for the 17 retail bots that ship them; Alistar, the eighteenth, has none.
            var data = BotLoadoutReader.Read(GameData.Default.Paths, champion.Name, 0);
            TestAssert.Equal(champion.RetailBot && champion.Name != "Alistar", data is not null,
                $"{champion.Name} ships bot loadout files exactly when it is a retail bot with a loadout");
            for (var skill = 0; skill <= PlatformBots.MaxSkillLevel; skill++)
            {
                var seat = PlatformBots.Seat(champion.Id, Team.Chaos, skill);
                var spells = SummonerLoadout.ValidatePair(seat.SummonerSpell1, seat.SummonerSpell2);
                TestAssert.Equal(seat.SummonerSpell1, spells.First);
                TestAssert.Equal(seat.SummonerSpell2, spells.Second);
                TestAssert.True(seat.IsBot && seat.SkinId == 0 && seat.SummonerLevel == 30);
                if (data is null)
                {
                    TestAssert.Equal(SummonerLoadout.DefaultFirst, seat.SummonerSpell1,
                        $"{champion.Name} difficulty {skill} gets the reconstructed pair");
                    TestAssert.Equal(SummonerLoadout.DefaultSecond, seat.SummonerSpell2);
                    TestAssert.True(seat.RedRunes is null && seat.BlueRunes is null && seat.YellowRunes is null &&
                        seat.BlackRunes is null && seat.Masteries is null,
                        "the retail Basic arm ships no rune or mastery pages");
                    continue;
                }
                var shipped = BotLoadoutReader.Read(GameData.Default.Paths, champion.Name, skill)!;
                // A difficulty section without spells is reconstructed the same way a champion without files is.
                var expected = shipped.SummonerSpell1 is { } first && shipped.SummonerSpell2 is { } second
                    ? SummonerLoadout.ValidatePair(first, second)
                    : (First: SummonerLoadout.DefaultFirst, Second: SummonerLoadout.DefaultSecond);
                TestAssert.Equal(expected.First, seat.SummonerSpell1, $"{champion.Name} difficulty {skill} keeps the shipped pair");
                TestAssert.Equal(expected.Second, seat.SummonerSpell2);
            }
        }

        // The reconstructed pair reaches the lobby's champion selection and the match seat.
        var lobby = new PlatformGames.Lobby(1, "Reconstructed", "", 1, 10)
        {
            Bots = [new PlatformGames.BotSeat(61, Team.Chaos, 0, PlatformBots.Seat(61, Team.Chaos, 0)),
                new PlatformGames.BotSeat(4, Team.Order, 3, PlatformBots.Seat(4, Team.Order, 3))]
        };
        var selections = ((ArrayCollection)AirGameDtos.Game(lobby)["playerChampionSelections"]).Cast<AsObject>()
            .ToDictionary(selection => (string)selection["summonerInternalName"]);
        TestAssert.Equal(4d, selections["bot_Orianna_200"]["spell1Id"], "the lobby shows the reconstructed pair");
        TestAssert.Equal(14d, selections["bot_Orianna_200"]["spell2Id"]);
        TestAssert.Equal(4d, selections["bot_TwistedFate_100"]["spell1Id"], "every difficulty is reconstructed the same way");
        TestAssert.Equal(14d, selections["bot_TwistedFate_100"]["spell2Id"]);
        var participants = MatchRoster.ToParticipants([new MatchPlayer("Human", "Ezreal", Team.Order),
            PlatformBots.Seat(61, Team.Order, 1)]);
        TestAssert.Equal("SummonerFlash", participants[1].SummonerSpell1, "the match seat carries the pair");
        TestAssert.Equal("SummonerDot", participants[1].SummonerSpell2);
        TestAssert.True(participants[1].IsBot && participants[1].SummonerLevel == 30);
    }

    // What the stock TeamSelectionController sends: BotParticipant with the picked champion and
    // teamId "100" (blue/order) or "200" (purple/chaos).
    private static AsObject ClientBot(int champion, string teamId, string difficulty = "Basic")
    {
        var name = AirGameDtos.ChampionNames[champion];
        var dto = ((ArrayCollection)AirDtos.Champions()).Cast<AsObject>().Single(c => Convert.ToInt32(c["championId"]) == champion);
        return AirWire.Object(BotAlias, ("summonerName", name + " bot"), ("summonerInternalName", $"bot_{name}_{teamId}"),
            ("teamId", teamId), ("botSkillLevel", 0d), ("botSkillLevelName", difficulty), ("champion", dto));
    }

    private static AsObject LastGame(List<(long Account, AsObject Body)> pushed, long account) =>
        pushed.Last(p => p.Account == account && p.Body.TypeName == "com.riotgames.platform.game.GameDTO").Body;

    private static int Id(object game) => Convert.ToInt32(((AsObject)game)["id"]);

    private static int Occupants(AsObject game) =>
        ((ArrayCollection)game["teamOne"]).Count + ((ArrayCollection)game["teamTwo"]).Count;

    private static PlayerMatchResult Player(ulong id, MatchPlayer seat, bool bot) => new(
        PlayerId: id, NetId: (uint)id, Name: seat.Name, Champion: seat.Champion, Team: seat.Team.ToString(), SkinId: seat.SkinId,
        Connected: true, Level: 1, Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
        TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0, OtherMinionsKilled: 0,
        NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0, Statistics: new HeroMatchStatistics().Snapshot(600),
        SummonerSpells: [seat.SummonerSpell1, seat.SummonerSpell2], SpellRanks: [0, 0, 0, 0], Inventory: [], IsBot: bot);

    private static void Login(PlatformSessions sessions, string connection, PlatformAccount account)
    {
        var session = sessions.Login(connection, account.Username, sessions.IssueTicket(account), Version);
        TestAssert.True(sessions.AuthenticateChannel(connection, account.Username, session.Token));
    }

    // The placeholder executable never starts: only the reserved roster and handoff matter here.
    private static bool Picked(object game, PlatformAccount account, int champion) =>
        ((ArrayCollection)((AsObject)game)["playerChampionSelections"]).Cast<AsObject>().Any(selection =>
            Equals(selection["summonerInternalName"], "sum" + account.SummonerId) && Equals(selection["championId"], champion));

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
