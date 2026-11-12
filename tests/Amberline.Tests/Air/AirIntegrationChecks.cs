using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using RtmpSharp.Messaging.Messages;

namespace Amberline.Tests.Air;

public static class AirIntegrationChecks
{
    [Check("server.air-bootstrap-loadout-contract")]
    public static void BootstrapAndRpcKeepTypedDataAndRuneColors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-contract-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(directory);
        var account = accounts.RegisterPublic("contract", "testing-password", "Contract");
        var runes = new RuneCatalog();
        var yellow = runes.All.First(r => r.Color == RuneColor.Yellow).Id;
        var blue = runes.All.First(r => r.Color == RuneColor.Blue).Id;
        var loadout = account.Loadout with { YellowRunes = [yellow], BlueRunes = [blue] };
        accounts.SaveLoadout(account.Id, loadout);
        account = accounts.Get(account.Id);
        var bootstrap = AirDtos.LoginData(account);
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(bootstrap);
        using var reader = new AmfReader(new MemoryStream(buffer.ToArray()), new SerializationContext());
        var decoded = (AsObject)reader.ReadAmf3Item();
        TestAssert.Equal("com.riotgames.platform.clientfacade.domain.LoginDataPacket", decoded.TypeName);
        TestAssert.Equal(true, decoded["clientHeartBeatEnabled"]);
        var all = (AsObject)decoded["allSummonerData"];
        var book = (AsObject)all["spellBook"];
        TestAssert.Equal(10, ((ArrayCollection)book["spellBookPages"]).Count); // Batch 706: a full account carries every page.
        var page = (AsObject)((ArrayCollection)book["spellBookPages"])[0];
        var slots = ((ArrayCollection)page["slotEntries"]).Cast<AsObject>().ToDictionary(x => (int)x["runeSlotId"], x => (int)x["runeId"]);
        TestAssert.Equal(yellow, slots[10]);
        TestAssert.Equal(blue, slots[19]);
        TestAssert.Equal(1, AirDtos.Maps().Count);
        TestAssert.Equal(1, ((AsObject)AirDtos.Maps()[0])["minCustomPlayers"]);
        TestAssert.Equal("GAME_CFG_PICK_BLIND", ((AsObject)AirDtos.GameTypes()[0])["name"]);
        TestAssert.Equal("SimulPickStrategy", ((AsObject)AirDtos.GameTypes()[0])["pickMode"]);
        TestAssert.Equal((double)PlatformGames.MainPickServerSeconds,
            ((AsObject)AirDtos.GameTypes()[0])["mainPickTimerDuration"]);
        TestAssert.Equal((double)PlatformGames.PostPickServerSeconds,
            ((AsObject)AirDtos.GameTypes()[0])["postPickTimerDuration"]);
        TestAssert.Equal(1, ((AsObject)decoded["clientSystemStates"])["minNumPlayersForPracticeGame"]);
        TestAssert.Equal(ChampionCatalog.All.Count, AirDtos.Champions().Count,
            "AIR lists the whole catalog, as retail did; the unplayable champions are locked");
        TestAssert.Equal(ChampionCatalog.PlayableNames.Count, AirGameDtos.ChampionNames.Count,
            "the pickable champions are exactly the playable ones (the ten hand-written ones and the script-driven ones)");
        TestAssert.Equal(ChampionCatalog.PlayableNames.Order(StringComparer.Ordinal).ToArray(),
            AirGameDtos.ChampionNames.Values.Order(StringComparer.Ordinal).ToArray(),
            "the pickable champions are the playable names, whoever the roster holds (no count is pinned here)");
        TestAssert.Equal(9, AirGameDtos.SpellId("SummonerRally"));
        TestAssert.Equal(5, AirGameDtos.SpellId("SummonerFortify"));
        TestAssert.True(AirGameDtos.SkinIds(1, skinsOff: false).Contains(1006));
        TestAssert.True(!AirGameDtos.SkinIds(1, skinsOff: false).Contains(6));

        var settings = new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), BindAddress: "127.0.0.1");
        var manager = new GameManager(settings, Path.Combine(directory, "games"));
        try
        {
            var sessions = new PlatformSessions(accounts);
            var session = sessions.Login("client", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
            TestAssert.True(sessions.AuthenticateChannel("client", account.Username, session.Token));
            var clock = new SoloClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var rpc = new PlatformServices(accounts, sessions, games);
            var canonicalStore = (string)rpc.Route("client", "loginService", "getStoreUrl", [])!;
            var queryStart = canonicalStore.IndexOf('?');
            // Stock UrlVo keeps the entire pre-query base and StoreController
            // concatenates a leading /store/... path; it does not resolve a URI.
            var stockDeepLink = new Uri(canonicalStore[..queryStart] +
                "/store/items/view_item/Champion/champions_1.html" + canonicalStore[queryStart..]);
            TestAssert.Equal("/store/items/view_item/Champion/champions_1.html", stockDeepLink.AbsolutePath,
                "the canonical store base must not prepend an entry-page path");
            var friend = (AsObject)rpc.Route("client", "statisticsService", "getSummonerSummaryByInternalName", [AirGameDtos.InternalName(account)])!;
            TestAssert.Equal("com.riotgames.platform.summoner.SummonerSummary", friend.TypeName);
            TestAssert.Equal((double)account.Id, friend["accountId"]);
            var profile = (AsObject)rpc.Route("client", "summonerService", "getAllPublicSummonerDataByAccount", [(double)account.Id])!;
            TestAssert.Equal("com.riotgames.platform.summoner.BasePublicSummonerDTO", ((AsObject)profile["summoner"]).TypeName);
            TestAssert.Equal(0, ((ArrayCollection)rpc.Route("client", "playerStatsService", "retrieveTopPlayedChampions", [(double)account.Id, "CLASSIC"])!).Count);
            TestAssert.Throws<ArgumentException>(() => rpc.Route("client", "playerStatsService", "getAggregatedStats",
                [(double)account.Id, "CLASSIC", "not-a-stock-season"]));
            var preferences = AirDtos.Summoner(account);
            preferences.Remove("acctId"); // stock sealed Summoner does not send this field
            preferences["tutorialFlag"] = true;
            preferences["helpFlag"] = true;
            preferences["name"] = "Client cannot rename an account";
            var saved = (AsObject)rpc.Route("client", "summonerService", "saveSummoner", [preferences])!;
            TestAssert.Equal(true, saved["tutorialFlag"]);
            TestAssert.Equal(true, saved["helpFlag"]);
            TestAssert.Equal(account.Summoner, saved["name"]);
            preferences["sumId"] = (double)account.SummonerId + 1;
            TestAssert.Throws<ArgumentException>(() => rpc.Route("client", "summonerService", "saveSummoner", [preferences]));
            preferences["sumId"] = (double)account.SummonerId;
            preferences["tutorialFlag"] = "true";
            TestAssert.Throws<ArgumentException>(() => rpc.Route("client", "summonerService", "saveSummoner", [preferences]));
            rpc.Route("client", "summonerService", "updateProfileIconId", [17]);
            TestAssert.Equal(17, AirDtos.Summoner(accounts.Get(account.Id))["profileIconId"]);
            TestAssert.True(accounts.Get(account.Id).TutorialFlag);
            var queues = (ArrayCollection)rpc.Route("client", "matchmakerService", "getAvailableQueues", [])!;
            // Batch 571: the normal 5v5 queue, Co-op vs. AI (7) and Twisted Treeline 3v3 (8); batch 579: normal draft (14), Dominion (16, 17);
            // batch 586: ranked solo/duo (4) and the ranked team queues (6, 9); batch 590: ranked solo 3v3 (11), Dominion ranked (18, 19).
            TestAssert.Equal(12, queues.Count);
            var queue = (AsObject)queues[0];
            TestAssert.Equal("com.riotgames.platform.matchmaking.GameQueueConfig", queue.TypeName);
            TestAssert.Equal(2d, queue["id"]); // stock PvP / Classic / SR / Normal leaf
            TestAssert.Equal("NORMAL", queue["type"]);
            TestAssert.True((int)queue["minLevel"] <= account.Loadout.SummonerLevel);
            TestAssert.Equal(5, queue["maximumParticipantListSize"], "batch 579: a party fills at most one side");
            TestAssert.Equal(true, decoded["matchMakingEnabled"]);
            TestAssert.Equal(true, rpc.Route("client", "matchmakerService", "isMatchmakingEnabled", []));
            TestAssert.Throws<ArgumentException>(() => rpc.Route("client", "matchmakerService", "attachToQueue", []));
            var created = (AsObject)rpc.Route("client", "gameService", "createPracticeGame",
                ["Stock argument order", "CLASSIC", AirDtos.Maps()[0], 2, 1, "password", "ALL"])!;
            TestAssert.Equal("TEAM_SELECT", created["gameState"]);
            TestAssert.Equal(true, created["passwordSet"]);
            TestAssert.Equal(false, created["spectatorsAllowed"]);
            TestAssert.Equal(0, ((ArrayCollection)created["observers"]).Count);
            TestAssert.Equal(17, ((AsObject)created["ownerSummary"])["profileIconId"]);
            var listed = (AsObject)((ArrayCollection)rpc.Route("client", "gameService", "listAllPracticeGames", [])!)[0];
            TestAssert.Equal("NONE", listed["allowSpectators"]);
            var selected = (AsObject)rpc.Route("client", "spellBookService", "selectDefaultSpellBookPage", [page])!;
            TestAssert.Equal("com.riotgames.platform.summoner.spellbook.SpellBookPage", selected.TypeName);
            TestAssert.Equal(1d, selected["pageId"]);
            var alternate = (AsObject)((ArrayCollection)book["spellBookPages"])[1];
            var black = runes.All.First(r => r.Color == RuneColor.Black).Id;
            alternate["name"] = "Alternate";
            alternate["slotEntries"] = AirWire.List([AirWire.Object(
                "com.riotgames.platform.summoner.spellbook.SlotEntry", ("runeSlotId", 29), ("runeId", black))]);
            rpc.Route("client", "spellBookService", "saveSpellBook", [book]);
            TestAssert.Equal(yellow, accounts.Get(account.Id).Loadout.YellowRunes![0]);
            TestAssert.Equal(blue, accounts.Get(account.Id).Loadout.BlueRunes![0]);
            selected = (AsObject)rpc.Route("client", "spellBookService", "selectDefaultSpellBookPage", [alternate])!;
            TestAssert.Equal(2d, selected["pageId"]);
            TestAssert.Equal(true, selected["isCurrent"]);
            TestAssert.Equal(29, ((AsObject)((ArrayCollection)selected["slotEntries"])[0])["runeSlotId"],
                "rune page projection preserves an unused slot before the selected rune");
            TestAssert.Equal(black, accounts.Get(account.Id).Loadout.BlackRunes![0]);
            rpc.Route("client", "spellBookService", "selectDefaultSpellBookPage", [page]);
            var startReply = (AsObject)rpc.Route("client", "gameService", "startChampionSelection", [created["id"], created["optimisticLock"]])!;
            TestAssert.Equal("com.riotgames.platform.game.StartChampSelectDTO", startReply.TypeName);
            TestAssert.Equal(0, ((ArrayCollection)startReply["invalidPlayers"]).Count);
            var started = (AsObject)games.Current(account.Id)!;
            TestAssert.Equal("CHAMP_SELECT", started["gameState"], "one human can enter champion selection without bots");
            TestAssert.Equal(true, rpc.Route("client", "gameService", "selectSpells", [7, 12]));
            TestAssert.Equal("SummonerHeal", accounts.Get(account.Id).Loadout.SummonerSpell1);
            TestAssert.Equal(12, new PlatformGames.Member(accounts.Get(account.Id), Team.Order).Spell2);
            games.Pick(account.Id, 1);
            games.LockIn(account.Id);
            TestAssert.Equal("POST_CHAMP_SELECT", ((AsObject)games.Current(account.Id)!)["gameState"]);
            TestAssert.True(rpc.Route("client", "spellBookService", "selectDefaultSpellBookPage", [alternate]) is AsObject,
                "rune selection remains editable during the post-pick countdown");
            rpc.Route("client", "spellBookService", "saveSpellBook", [book]);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            // Item 33: the stock client keeps its rune/mastery Save live at "starting game" and both calls carry no error
            // handler, so retail answered them; a refusal is the generic PvP.net error window. The page is account data and
            // the match roster is the launch snapshot, so these now succeed past the deadline.
            TestAssert.True(rpc.Route("client", "spellBookService", "selectDefaultSpellBookPage", [alternate]) is AsObject,
                "a page selected after the countdown is answered, not refused");
            TestAssert.Equal((double)account.SummonerId, ((AsObject)rpc.Route("client", "spellBookService", "saveSpellBook", [book])!)["summonerId"],
                "a rune book saved after the countdown answers the book instead of a fault");
        }
        finally { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    [Check("server.air-rune-combiner-rpc-and-roster-lock")]
    public static void RuneCombinerUsesStockIdsAndHonorsFrozenRosters() =>
        RuneCombinerRpcAsync().GetAwaiter().GetResult();

    private static async Task RuneCombinerRpcAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-combiner-rpc-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(directory);
        var account = accounts.RegisterPublic("combine", "testing-password", "Combine", maxed: false);
        var rune = new RuneCatalog().All.First(r => r.Tier == RuneTier.One && r.Color == RuneColor.Red);
        var item = accounts.StoreCatalog.GetRune(rune.Id);
        accounts.GrantCurrency(account.Id, item.IpPrice!.Value * 4L, 0, Guid.NewGuid());
        for (var copy = 0; copy < 4; copy++) accounts.Purchase(account.Id, item.Id, "IP", Guid.NewGuid());
        var sessions = new PlatformSessions(accounts);
        var session = sessions.Login("combiner", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
        TestAssert.True(sessions.AuthenticateChannel("combiner", account.Username, session.Token));
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), BindAddress: "127.0.0.1"),
            Path.Combine(directory, "games"));
        var clock = new SoloClock();
        var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
        var rpc = new PlatformServices(accounts, sessions, games);
        var recipes = (ArrayCollection)rpc.Route("combiner", "inventoryService", "getAllRuneCombiners", [])!;
        var prior = File.ReadAllText(Path.Combine(directory, "accounts.json"));
        TestAssert.Throws<ArgumentException>(() => rpc.Route("combiner", "inventoryService", "useGrabBag", [1]));
        // Batch 580: the Basic Tutorial (0) is a game now (server.air-tutorial-*); batch 669: so is Battle Training (1,
        // server.air-battle-training-*). A type the client never sends (2) is still refused.
        TestAssert.Throws<ArgumentException>(() => rpc.Route("combiner", "gameService", "createTutorialGame", [2]));
        TestAssert.Equal(prior, File.ReadAllText(Path.Combine(directory, "accounts.json")), "deferred actions cannot grant runes or complete tutorial flags");
        TestAssert.False(accounts.Get(account.Id).TutorialFlag, "retain the tutorial offer as requested");
        TestAssert.Equal(5, recipes.Count);
        // Item 38: the combiner lists its recipes for every account, the fully unlocked one included; retail answered the
        // list unconditionally (the client builds its combine view and target count from it). The refusal on a combine is
        // a typed fault the client's own handler can read, not the "no recipes" empty view it used to get.
        var maxed = accounts.RegisterPublic("combinemaxed", "testing-password", "Combine Maxed");
        var maxedSession = sessions.Login("combiner-maxed", maxed.Username, sessions.IssueTicket(maxed), "1.45.11_10_04_15_28");
        TestAssert.True(sessions.AuthenticateChannel("combiner-maxed", maxed.Username, maxedSession.Token));
        TestAssert.Equal(5, ((ArrayCollection)rpc.Route("combiner-maxed", "inventoryService", "getAllRuneCombiners", [])!).Count,
            "a fully unlocked account is offered the recipes retail offered it");
        TestAssert.Throws<AirFault>(() => rpc.Route("combiner-maxed", "inventoryService", "useRuneCombiner",
            [recipes.Cast<AsObject>().Single(row => (int)row["inputTier"] == 1 && (int)row["inputCount"] == 2)["id"],
                AirWire.List([AirWire.Object("", ("runeId", rune.Id)), AirWire.Object("", ("runeId", rune.Id))])]),
            "an all-unlocked account still cannot burn its guaranteed runes, and is told so in the shape its handler reads");
        var recipe = recipes.Cast<AsObject>().Single(row => (int)row["inputTier"] == 1 && (int)row["inputCount"] == 2);
        TestAssert.Equal(PlatformRuneCombiner.AirAlias, recipe.TypeName);
        // Only itemId is authoritative; other fields originate in the AIR catalog.
        var inputs = AirWire.List(Enumerable.Range(0, 2).Select(_ => AirWire.Object("com.riotgames.platform.catalog.runes.Rune",
            ("itemId", rune.Id), ("tier", 3))));
        var awards = rpc.Route("combiner", "inventoryService", "useRuneCombiner", [recipe["id"], inputs]);
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(awards);
        using var reader = new AmfReader(new MemoryStream(buffer.ToArray()), new SerializationContext());
        var wireAwards = (ArrayCollection)reader.ReadAmf3Item();
        TestAssert.Equal(1, wireAwards.Count);
        var award = (AsObject)wireAwards[0];
        TestAssert.Equal("com.riotgames.platform.catalog.runes.RuneQuantity", award.TypeName);
        TestAssert.Equal(1, award["quantity"]);
        TestAssert.Equal(RuneTier.One, new RuneCatalog().Get((int)award["runeId"])!.Tier);
        TestAssert.Equal(3, accounts.Get(account.Id).Economy!.OwnedRunes!.Values.Sum());
        TestAssert.Throws<ArgumentException>(() => rpc.Route("combiner", "inventoryService", "useRuneCombiner",
            [recipe["id"], AirWire.List([AirWire.Object("", ("runeId", rune.Id)), AirWire.Object("", ("runeId", rune.Id))])]));
        // Item 38: the client's combine handler is registered only for UnexpectedServiceException and reads errorCode and
        // exception off the rootCause; InventoryServiceImpl.useRuneCombiner. A plain refusal left that handler with nulls.
        try
        {
            rpc.Route("combiner", "inventoryService", "useRuneCombiner",
                [recipe["id"], AirWire.List([AirWire.Object("", ("runeId", rune.Id))])]);
            TestAssert.True(false, "a wrong input count must be refused");
        }
        catch (AirFault fault)
        {
            TestAssert.Equal(AirFault.UnexpectedService, fault.Alias);
            var rootCause = fault.RootCause();
            TestAssert.Equal(AirFault.UnexpectedService, rootCause["rootCauseClassname"],
                "the client resolves the handler from getClassByAlias(rootCauseClassname)");
            TestAssert.True((string)rootCause["message"]! is { Length: > 0 }, "ServerError.exception is never null");
        }
        var lobby = (AsObject)games.Create(accounts.Get(account.Id), "Combiner lock", "", 1, 2, 1, "CLASSIC", "NONE");
        games.Start(account.Id, PlatformServices.Integer(lobby["id"]), PlatformServices.Integer(lobby["optimisticLock"]));
        games.Pick(account.Id, 1);
        games.LockIn(account.Id);
        clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
        TestAssert.Throws<ArgumentException>(() => rpc.Route("combiner", "inventoryService", "useRuneCombiner",
            [recipe["id"], inputs]));
        TestAssert.Equal(3, accounts.Get(account.Id).Economy!.OwnedRunes!.Values.Sum());
    }

    // Leaving the Profile tab before the summoner data arrives resets profileAcctId (NaN, then 0)
    // while the stats reads are still being sent; those reads must not fault (17 Oct 2026 log).
    [Check("server.air-profile-stats-tolerate-unknown-account")]
    public static void ProfileStatsReadsAnswerEmptyForUnknownAccounts() => ProfileStatsUnknownAsync().GetAwaiter().GetResult();

    private static async Task ProfileStatsUnknownAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-air-stats-unknown-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(directory);
        var account = accounts.RegisterPublic("statsread", "testing-password", "StatsRead", maxed: false);
        var sessions = new PlatformSessions(accounts);
        var session = sessions.Login("stats", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
        TestAssert.True(sessions.AuthenticateChannel("stats", account.Username, session.Token));
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), BindAddress: "127.0.0.1"),
            Path.Combine(directory, "games"));
        var rpc = new PlatformServices(accounts, sessions, new PlatformGames(accounts, manager, "127.0.0.1", new SoloClock()));
        foreach (var unknown in new object[] { 0d, double.NaN, 987654d })
        {
            var lifetime = (AsObject)rpc.Route("stats", "playerStatsService", "retrievePlayerStatsByAccountId", [unknown])!;
            TestAssert.Equal("com.riotgames.platform.statistics.PlayerLifetimeStats", lifetime.TypeName);
            TestAssert.Equal("com.riotgames.platform.statistics.PlayerStatSummaries",
                ((AsObject)lifetime["playerStatSummaries"]).TypeName);
            TestAssert.Equal(0, ((ArrayCollection)rpc.Route("stats", "playerStatsService", "retrieveTopPlayedChampions", [unknown, "CLASSIC"])!).Count);
            TestAssert.Equal(0, ((ArrayCollection)((AsObject)rpc.Route("stats", "playerStatsService", "getRecentGames", [unknown])!)["gameStatistics"]).Count);
            TestAssert.Equal("com.riotgames.platform.statistics.AggregatedStats",
                ((AsObject)rpc.Route("stats", "playerStatsService", "getAggregatedStats", [unknown, "CLASSIC", "CURRENT"])!).TypeName);
        }
        TestAssert.Throws<ArgumentException>(() => rpc.Route("stats", "playerStatsService", "getAggregatedStats",
            [0d, "CLASSIC", "not-a-stock-season"]));
        var own = (AsObject)rpc.Route("stats", "playerStatsService", "retrievePlayerStatsByAccountId", [(double)account.Id])!;
        TestAssert.Equal((double)account.Id, own["userId"]);
    }

    [Check("server.air-rune-inventory-wire")]
    public static void CompleteRuneInventoryDecodesAndUsesTheStockRuneCatalog()
    {
        var account = new PlatformAccount(42, 1_000_042, "runes", "Runes", "", "", new MatchPlayer("Runes", "Annie", Team.Order));
        var catalog = new RuneCatalog().All.ToDictionary(r => r.Id);
        var envelope = new AcknowledgeMessage { Body = AirDtos.RuneInventory(account), CorrelationId = "rune-inventory" };
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext()))
        {
            writer.WriteAmf3Item(envelope);
            writer.WriteAmf3Item("end-of-runes");
        }
        using var input = new MemoryStream(buffer.ToArray());
        using var reader = new AmfReader(input, new SerializationContext());
        var reply = (AcknowledgeMessage)reader.ReadAmf3Item();
        TestAssert.Equal("rune-inventory", reply.CorrelationId);
        var inventory = (AsObject)reply.Body;
        TestAssert.Equal("com.riotgames.platform.summoner.runes.SummonerRuneInventory", inventory.TypeName);
        var runes = ((ArrayCollection)inventory["summonerRunes"]).Cast<AsObject>().ToArray();
        TestAssert.Equal(374, runes.Length);
        TestAssert.Equal(Enumerable.Range(5001, 374).ToArray(), runes.Select(r => (int)r["runeId"]).Order().ToArray());
        foreach (var rune in runes)
        {
            TestAssert.Equal("com.riotgames.platform.summoner.runes.SummonerRune", rune.TypeName);
            TestAssert.Equal((double)account.SummonerId, rune["summonerId"]);
            // Batch 698: the stock catalog gives every effect categoryId 1 (Health), so a null rune made the rune book's
            // "Runes Type" filter list everything for Health and nothing for the other choices. The typed Rune carries its
            // imagePath, so Inventory.hydrateRuneData keeps it and RuneBookController.filterByRuneCategory reads our category.
            var typed = (AsObject)rune["rune"]!;
            TestAssert.Equal("com.riotgames.platform.catalog.runes.Rune", typed.TypeName);
            TestAssert.Equal((int)rune["runeId"], typed["itemId"]);
            TestAssert.True(typed["imagePath"] is string, "a null imagePath makes AIR replace the rune with its own catalog copy");
            var effects = ((ArrayCollection)typed["itemEffects"]).Cast<AsObject>().ToArray();
            TestAssert.True(effects.Length == 1 && effects[0].TypeName == "com.riotgames.platform.catalog.ItemEffect");
            TestAssert.True((int)effects[0]["effectCategory"] is >= 1 and <= 6, $"rune {rune["runeId"]} has a rune book category");
            var quantity = catalog[(int)rune["runeId"]].Color == RuneColor.Black ? 3 : 9;
            TestAssert.Equal(quantity, rune["quantity"]);
            TestAssert.Equal(quantity, rune["currentAvailableQuantity"]);
            TestAssert.True(rune["purchaseDate"] is null, "an unlocked account bought nothing, so no rune glows as newly purchased");
        }
        TestAssert.Equal("end-of-runes", reader.ReadAmf3Item());
        TestAssert.Equal(input.Length, input.Position, "all nested objects must consume exactly their wire bodies");

        // The stock filter: All (0) lists everything, any other choice lists the runes whose first effect has that category.
        AsObject Effect(int id) => ((ArrayCollection)((AsObject)runes.Single(r => (int)r["runeId"] == id)["rune"]!)["itemEffects"])
            .Cast<AsObject>().First();
        int Listed(int category) => runes.Count(r => category == 0 || (int)Effect((int)r["runeId"])["effectCategory"] == category);
        TestAssert.Equal(374, Listed(0));
        for (var category = 1; category <= 6; category++)
            TestAssert.True(Listed(category) is > 0 and < 374, $"category {category} lists only its own runes ({Listed(category)})");
        TestAssert.Equal(374, Enumerable.Range(1, 6).Sum(Listed), "every rune is in exactly one category");
        // Stock catalog values (AirGeneratedContent RuneGeneratedData): stat name, primary rune type, image.
        foreach (var (id, name, category, type, image) in new[]
                 {
                     (5001, "FlatPhysicalDamageMod", 3, 1, "r_1_1.png"), (5255, "FlatHPPoolMod", 1, 3, "r_3_3.png"),
                     (5299, "FlatMPPoolMod", 2, 5, "b_1_3.png"), (5297, "FlatMagicDamageMod", 4, 5, "b_3_3.png"),
                     (5303, "rFlatMagicPenetrationMod", 4, 1, "b_1_3.png"), (5264, "rFlatDodgeModPerLevel", 5, 3, ""),
                     (5266, "rPercentCooldownModPerLevel", 6, 5, "r_2_3.png"), (5369, "FlatEnergyRegenMod", 6, 3, "y_1_3.png"),
                 })
        {
            var effect = Effect(id);
            TestAssert.Equal(name, effect["name"]);
            TestAssert.Equal(category, effect["effectCategory"], $"rune {id} category");
            TestAssert.Equal(type, effect["effectType"], $"rune {id} primary rune type");
            TestAssert.Equal(image, ((AsObject)runes.Single(r => (int)r["runeId"] == id)["rune"]!)["imagePath"]);
        }
        // The stock renderer prints "+ " before the formatted value; cooldown reductions keep the catalog's negative sign.
        TestAssert.Near(-0.00016, Convert.ToDouble(Effect(5266)["value"]), 1e-7);
    }

    [Check("server.air-reserved-native-player-identity")]
    public static void HostReadinessAndReconnectPreserveSummonerIdentity() => ReservedAsync().GetAwaiter().GetResult();

    private static async Task ReservedAsync()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        var directory = Path.Combine(Path.GetTempPath(), "league-air-reserved-" + Guid.NewGuid().ToString("N"));
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), directory);
        var alice = new HostGuest("air:1", "Alice", "");
        var bob = new HostGuest("air:2", "Bob", "");
        var players = new[] { new MatchPlayer("Alice", "Annie", Team.Order, PlayerId: 1_000_001), new MatchPlayer("Bob", "Annie", Team.Chaos, PlayerId: 1_000_002) };
        var participants = MatchRoster.ToParticipants(players);
        TestAssert.Equal(1_000_001UL, participants[0].PlayerId);
        TestAssert.Equal(0U, participants[0].ClientId);
        TestAssert.Equal(1UL, MatchRoster.ToParticipants([players[0] with { PlayerId = null }])[0].PlayerId);
        TestAssert.Throws<IOException>(() => manager.CreateReserved(alice, new(Guid.NewGuid(), players), [alice, bob],
            _ => throw new IOException("Simulated handoff persistence failure")));
        TestAssert.Equal(0, manager.List(alice).Length, "failed persistence must not create a running match");
        var failedRecord = Directory.EnumerateFiles(directory, "game.json", SearchOption.AllDirectories).Single();
        using (var saved = JsonDocument.Parse(File.ReadAllText(failedRecord)))
            TestAssert.Equal("Failed", saved.RootElement.GetProperty("state").GetString(),
                "a prelaunch handoff failure must not be recorded as an interrupted running match");
        TestAssert.True(File.Exists(Path.Combine(Path.GetDirectoryName(failedRecord)!, "roster.json")),
            "a prelaunch failure preserves the roster for diagnosis");
        var prepared = false;
        var game = manager.CreateReserved(alice, new(Guid.NewGuid(), players), [alice, bob], _ =>
        {
            TestAssert.Equal(0, manager.List(alice).Length, "durable handoff precedes publishing or starting the match");
            prepared = true;
        });
        TestAssert.True(prepared);
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (manager.List(alice).Single(g => g.Id == game.Id).State == "Starting" && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        TestAssert.Equal("Waiting for players", manager.List(alice).Single(g => g.Id == game.Id).State);
        TestAssert.Throws<RemoteFault>(() => manager.Join(bob, game.Id, 1, local: true));
        TestAssert.Throws<RemoteFault>(() => manager.ReadResult(new("stranger", "Stranger", ""), game.Id));
        TestAssert.Equal(1_000_001, manager.Join(alice, game.Id, 1, local: true).PlayerId);
        TestAssert.Equal(1_000_002, manager.Join(bob, game.Id, 2, local: true).PlayerId);
        TestAssert.Equal(1_000_002, manager.Join(bob, game.Id, 2, local: true).PlayerId);
        TestAssert.True(manager.ReadResult(alice, game.Id) is null, "an unfinished server has no authoritative result");
        var resultDirectory = Path.Combine(directory, game.Id.ToString("N"), "logs", "session");
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, "match-result.json");
        File.WriteAllText(resultPath + ".tmp", "{");
        TestAssert.True(manager.ReadResult(alice, game.Id) is null, "a partial atomic write is not a result");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { session_id = Guid.NewGuid(), result = new { } }));
        TestAssert.True(manager.ReadResult(alice, game.Id) is null, "another session's result is not accepted");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { session_id = game.Id.ToString() }));
        TestAssert.Throws<InvalidDataException>(() => manager.ReadResult(alice, game.Id),
            "a matching result without its payload is invalid");
        File.Delete(resultPath);
    }

    private static string ServerExecutable() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "Amberline.Server", "release", "Amberline.Server.exe"));

    [Check("server.air-solo-handoff-reconnect-and-retry")]
    public static void SoloSelectionHandsOffToReservedServerAndRecoversAfterStop() => SoloRetryAsync().GetAwaiter().GetResult();

    private static async Task SoloRetryAsync()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        var directory = Path.Combine(Path.GetTempPath(), "league-air-solo-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(directory, "accounts"));
        var account = accounts.RegisterPublic("solo", "testing-password", "Solo");
        var clock = new SoloClock();
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(),
            FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(directory, "games"));
        var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
        var pushed = new List<AsObject>();
        var presenceChanged = new List<long>();
        games.Push = (_, body) => pushed.Add((AsObject)body);
        games.PresenceChanged = id => presenceChanged.Add(id);
        var guest = PlatformGames.Guest(account);
        for (var round = 0; round < 3; round++)
        {
            TestAssert.True(games.ActivePresenceChampion(account.Id) is null,
                "a new lobby must not retain the previous match champion");
            // Only champion-selection time is accelerated. The shared match host
            // intentionally enforces its real three-second creation rate limit.
            if (round > 0) await Task.Delay(TimeSpan.FromSeconds(3));
            var lobby = (AsObject)games.Create(accounts.Get(account.Id), "Solo retry", "", 1, 2, 1, "CLASSIC", "NONE");
            games.Start(account.Id, Convert.ToInt32(lobby["id"]), Convert.ToInt64(lobby["optimisticLock"]));
            games.Pick(account.Id, 1);
            games.LockIn(account.Id);
            TestAssert.True(games.ActivePresenceChampion(account.Id) is null,
                "a selected champion is not active during post-pick countdown");
            TestAssert.True(games.Reconnect(account.Id) is null, "no credentials before match readiness");
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            var presenceBeforeLaunch = presenceChanged.Count;
            games.Tick();
            TestAssert.Equal("Annie", games.ActivePresenceChampion(account.Id),
                "launch presence must resolve the selected roster champion");
            TestAssert.True(presenceChanged.Skip(presenceBeforeLaunch).Contains(account.Id),
                "launch must refresh the account's XMPP presence");
            var match = manager.List(guest).Single(g => g.State is not ("Stopped" or "Failed"));
            if (round == 0)
            {
                await manager.StopAsync(guest, match.Id);
                var presenceBeforeFailure = presenceChanged.Count;
                games.Tick();
                TestAssert.True(games.ActivePresenceChampion(account.Id) is null,
                    "failed launch must clear the active champion");
                TestAssert.True(presenceChanged.Skip(presenceBeforeFailure).Contains(account.Id),
                    "failed launch must refresh XMPP presence");
                TestAssert.Equal("FAILED_TO_START", pushed.Last()["gameState"],
                    "failure before credentials must use AIR's launch-error transition");
                TestAssert.Equal("FAILED_TO_START", ((AsObject)games.Get(account.Id, Convert.ToInt32(lobby["id"]))) ["gameState"],
                    "a recovery poll must return the same failure state as the push");
                TestAssert.True(games.Current(account.Id) is null);
                TestAssert.True(games.Reconnect(account.Id) is null);
                continue;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (games.Reconnect(account.Id) is null)
            {
                await Task.Delay(100, deadline.Token);
                games.Tick();
            }
            var reconnect = (AsObject)games.Reconnect(account.Id)!;
            var credentials = (AsObject)reconnect["playerCredentials"];
            TestAssert.Equal((double)account.SummonerId, credentials["playerId"]);
            var original = pushed.Last(p => p.TypeName.EndsWith("PlayerCredentialsDto", StringComparison.Ordinal));
            TestAssert.Equal(original["encryptionKey"], credentials["encryptionKey"]);
            TestAssert.Throws<ArgumentException>(() => games.SaveLoadout(account.Id, a => a.Loadout));
            if (round == 2)
            {
                var roster = MatchRoster.Read(Path.Combine(directory, "games", match.Id.ToString("N"), "roster.json"));
                TestAssert.Equal("Annie", roster.Single().Champion,
                    "the presence champion must match the reserved match roster");
                var result = CompletedResult(roster.Single(), account);
                var folder = Path.Combine(directory, "games", match.Id.ToString("N"), "logs", "session");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "match-result.json");
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { session_id = match.Id.ToString(), result }));
                File.Move(path + ".tmp", path);
                TestAssert.Throws<RemoteFault>(() => manager.ReadCompletedResult(new("stranger", "Stranger", ""), match.Id));
                TestAssert.True(manager.ReadCompletedResult(guest, match.Id) is not null,
                    "the durable result remains readable before the next finished-status pulse");
                var presenceBeforeResult = presenceChanged.Count;
                games.Tick();
                TestAssert.True(games.ActivePresenceChampion(account.Id) is null,
                    "an imported result must clear the active champion before server stop");
                TestAssert.True(presenceChanged.Skip(presenceBeforeResult).Contains(account.Id),
                    "result import must refresh XMPP presence");
                TestAssert.Equal(1, accounts.ResultsFor(account.Id).Length);
                TestAssert.Equal(result.Winner, accounts.ResultsFor(account.Id).Single().Result.Deserialize<MatchResult>()!.Winner);
                games.Tick();
                TestAssert.Equal(1, accounts.ResultsFor(account.Id).Length, "repeated polling must not import twice");
                await manager.StopAsync(guest, match.Id);
                games.Tick();
                TestAssert.Equal(1, accounts.ResultsFor(account.Id).Length,
                    "a late stopped state must not discard an imported result");
                continue;
            }
            await manager.StopAsync(guest, match.Id);
            var presenceBeforeTermination = presenceChanged.Count;
            games.Tick();
            TestAssert.True(games.ActivePresenceChampion(account.Id) is null,
                "terminated match must clear the active champion");
            TestAssert.True(presenceChanged.Skip(presenceBeforeTermination).Contains(account.Id),
                "match termination must refresh XMPP presence");
            TestAssert.Equal("TERMINATED_IN_ERROR", pushed.Last()["gameState"],
                "a stopped match without results must not open AIR's completed-game screen");
            TestAssert.Equal("TERMINATED_IN_ERROR", ((AsObject)games.Get(account.Id, Convert.ToInt32(lobby["id"]))) ["gameState"]);
            TestAssert.True(games.Current(account.Id) is null, "a stopped match releases the participant for another game");
            TestAssert.True(games.Reconnect(account.Id) is null);
        }
    }

    private static MatchResult CompletedResult(MatchPlayer seat, PlatformAccount account) => new(
        8, "Version 1.0.0.126", 1, "CLASSIC", seat.Team.ToString(), 600,
        [new PlayerMatchResult(
            PlayerId: (ulong)seat.PlayerId!.Value, NetId: (uint)account.Id, Name: seat.Name,
            Champion: seat.Champion, Team: seat.Team.ToString(), SkinId: seat.SkinId, Connected: true,
            Level: 1, Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
            TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0,
            OtherMinionsKilled: 0, NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0,
            Statistics: new HeroMatchStatistics().Snapshot(600),
            SummonerSpells: [seat.SummonerSpell1, seat.SummonerSpell2], SpellRanks: [0, 0, 0, 0],
            Inventory: [])], false);

    private sealed class SoloClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
