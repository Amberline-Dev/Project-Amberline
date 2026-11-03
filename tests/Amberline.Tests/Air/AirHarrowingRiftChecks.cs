using System.Collections;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>
/// The Harrowing rift (Summoner's Rift (Autumn), LEVELS/Map2) in AIR. Batch 439 (owner decision, 28 October 2026: "make the autumn
/// harrowing map always selectible"): the Platform offers it in custom games at all times, next to Summoner's Rift, and the normal
/// queue plays Summoner's Rift at all times. This replaces batch 429's window and its AMBERLINE_HARROWING_RIFT switch, which are gone.
/// The 2011 Harrowing rune window (AirRuneTierChecks) is unchanged. These checks move a test clock across the window (inside it on
/// 25 October, outside it on 1 December) so that neither the offer nor the queue depends on the date. The offer of The Crystal Scar
/// (map 8) and of Twisted Treeline (map 4, batch 557) is checked here as well.
/// </summary>
public static class AirHarrowingRiftChecks
{
    private const string Version = "1.45.11_10_04_15_28";

    private static readonly DateTimeOffset InsideWindow = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OutsideWindow = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    [Check("server.air-harrowing-rift-map-offered-and-created-at-all-times")]
    public static void TheAutumnMapIsOfferedAndCreatableInsideAndOutsideTheHarrowingWindow() =>
        RunOfferAsync().GetAwaiter().GetResult();

    [Check("server.air-normal-queue-plays-summoners-rift-at-all-times")]
    public static void TheNormalQueuePlaysSummonersRiftInsideAndOutsideTheHarrowingWindow() =>
        RunQueueAsync().GetAwaiter().GetResult();

    private static async Task RunOfferAsync()
    {
        var root = TemporaryDirectory("league-air-harrowing-rift");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new MovableClock(InsideWindow);
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var players = Enumerable.Range(1, 5).Select(index =>
                accounts.RegisterPublic("rift" + index, "testing-password", "Rift " + index)).ToArray();
            Login(sessions, "first", players[0]);

            // Inside the window: both maps are offered, a lobby on either is created, and a map the Platform never offers is refused.
            // Batch 443: The Crystal Scar (map 8) joins the offer; it is created only in mode ODIN (DominionOfferChecks below).
            // Batch 557 (TT-7): Twisted Treeline (map 4) joins the offer, so the set is 1, 2, 4 and 8 in OpenMapIds order.
            TestAssert.Equal(new[] { 1, 2, 4, 8 }, MapIds(rpc), "inside the Harrowing window the client is offered Summoner's Rift, the Autumn map, Twisted Treeline and The Crystal Scar");
            TestAssert.Equal(2, MapIdOf(games.Create(players[0], "Autumn In", "", 2, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")),
                "a lobby created on the Autumn map inside the window reports map 2");
            TestAssert.Equal(1, MapIdOf(games.Create(players[1], "Summoners In", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")),
                "Summoner's Rift is creatable inside the window");
            TestAssert.Throws<ArgumentException>(() =>
                games.Create(players[2], "Refused", "", 3, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"),
                "a map the Platform does not offer (the tutorial, map 3) is refused");

            // Outside the window (1 December): the offer and the Autumn map are unchanged.
            clock.Now = OutsideWindow;
            TestAssert.Equal(new[] { 1, 2, 4, 8 }, MapIds(rpc), "outside the Harrowing window the client is still offered the four maps");
            TestAssert.Equal(2, MapIdOf(games.Create(players[3], "Autumn Out", "", 2, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")),
                "a lobby created on the Autumn map outside the window reports map 2");
            TestAssert.Equal(1, MapIdOf(games.Create(players[4], "Summoners Out", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")),
                "Summoner's Rift is creatable outside the window");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Check("server.air-dominion-map-offered-and-created-only-with-odin")]
    public static void TheCrystalScarIsOfferedAndCreatableOnlyInOdinMode() =>
        RunDominionAsync().GetAwaiter().GetResult();

    // Batch 443 (owner decision, 28 October 2026: Dominion for custom games only, no bots at first): The Crystal Scar (LEVELS/Map8) is
    // offered and created only in its own mode, ODIN (AIR GameMode.DOMINION). Summoner's Rift and the Autumn map stay CLASSIC and refuse ODIN.
    private static async Task RunDominionAsync()
    {
        var root = TemporaryDirectory("league-air-dominion-map");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new MovableClock(InsideWindow);
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var players = Enumerable.Range(1, 6).Select(index =>
                accounts.RegisterPublic("dominion" + index, "testing-password", "Odin " + index)).ToArray();
            Login(sessions, "first", players[0]);

            // Batch 557 (TT-7): restated for map 4 (Twisted Treeline) joining the offer; map 8 is still offered.
            TestAssert.Equal(new[] { 1, 2, 4, 8 }, MapIds(rpc), "the client is offered The Crystal Scar in custom games");
            var lobby = games.Create(players[0], "Crystal Scar", "", 8, 2, AirGameDtos.BlindPickConfigId, "ODIN", "NONE");
            TestAssert.Equal(8, MapIdOf(lobby), "a lobby on The Crystal Scar reports map 8");
            TestAssert.Equal("ODIN", GameModeOf(lobby), "its game DTO reports the Dominion mode");
            TestAssert.Equal("CLASSIC", GameModeOf(games.Create(players[1], "Summoners Odin", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")),
                "Summoner's Rift stays CLASSIC");
            TestAssert.Throws<ArgumentException>(() =>
                games.Create(players[2], "Crystal Classic", "", 8, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"),
                "The Crystal Scar is refused in CLASSIC");
            TestAssert.Throws<ArgumentException>(() =>
                games.Create(players[3], "Summoners Odin Refused", "", 1, 2, AirGameDtos.BlindPickConfigId, "ODIN", "NONE"),
                "Summoner's Rift is refused in ODIN");
            TestAssert.Throws<ArgumentException>(() =>
                games.Create(players[4], "Autumn Odin Refused", "", 2, 2, AirGameDtos.BlindPickConfigId, "ODIN", "NONE"),
                "the Harrowing rift is refused in ODIN");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Check("server.air-custom-game-offers-twisted-treeline")]
    public static void TwistedTreelineIsOfferedInCustomGamesWithSixSlotsAtMost() =>
        RunTwistedTreelineAsync().GetAwaiter().GetResult();

    // Batch 557 (TT-7, the overnight plan of 1 November 2026): Twisted Treeline (LEVELS/Map4) is offered in custom games only, and the
    // normal queue keeps Summoner's Rift. The stock client knows map 4 as a six-player map (MapGeneratedData.as:32, createMap(4, ..., 6)).
    private static async Task RunTwistedTreelineAsync()
    {
        var root = TemporaryDirectory("league-air-twisted-treeline");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new MovableClock(InsideWindow);
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var players = Enumerable.Range(1, 5).Select(index =>
                accounts.RegisterPublic("treeline" + index, "testing-password", "Treeline " + index)).ToArray();
            Login(sessions, "first", players[0]);

            TestAssert.Equal(new[] { 1, 2, 4, 8 }, MapIds(rpc), "the client is offered Twisted Treeline in custom games");

            // Six slots at most, bots included: a seventh is refused with PG-0037 (the create view's "maximum of {0} players", {0} = 6)
            // and nothing is created; a six-slot lobby is accepted. Summoner's Rift keeps its ten.
            var seven = Fault(() => games.Create(players[1], "Too Many Treeline", "", 4, 7, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            TestAssert.Equal("PG-0037", seven.ErrorCode, "a seventh slot on Twisted Treeline is refused as too many slots");
            TestAssert.Equal("com.riotgames.platform.game.TooManyPlayersException", seven.Alias, "the refusal is the typed game exception");
            TestAssert.Equal((object)6, seven.SubstitutionArguments[0], "the refusal names the six-player maximum");
            var six = (AsObject)games.Create(players[2], "Six Slot Treeline", "", 4, 6, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
            TestAssert.Equal(4, MapIdOf(six), "a six-slot lobby on Twisted Treeline is accepted");
            var ten = (AsObject)games.Create(players[3], "Ten On Rift", "", 1, 10, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
            TestAssert.Equal(10, Convert.ToInt32(ten["maxNumPlayers"]), "Summoner's Rift still takes ten slots");

            // The lobby search names each lobby's own map (H14: PracticeGameSearchResult.gameMap was always Summoner's Rift).
            var rift = (AsObject)games.Create(players[4], "Rift Search", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
            var treeline = (AsObject)Searched(games, six)["gameMap"];
            TestAssert.Equal(4, Convert.ToInt32(treeline["mapId"]), "the search result names Twisted Treeline for a map 4 lobby");
            TestAssert.Equal("TwistedTreeline", treeline["name"]);
            TestAssert.Equal("The Twisted Treeline", treeline["displayName"]);
            TestAssert.Equal(6, Convert.ToInt32(treeline["totalPlayers"]), "and its six-player size");
            TestAssert.Equal(1, Convert.ToInt32(((AsObject)Searched(games, rift)["gameMap"])["mapId"]),
                "a Summoner's Rift lobby still names Summoner's Rift");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task RunQueueAsync()
    {
        var root = TemporaryDirectory("league-air-queue-rift");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new MovableClock(InsideWindow);
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var current = new Dictionary<string, long>();
            games.AccountForConnection = connection => current.GetValueOrDefault(connection);

            TestAssert.Equal(1, MatchedMapId(accounts, games, current, "inside"),
                "the normal queue plays Summoner's Rift inside the Harrowing window");
            clock.Now = OutsideWindow;
            TestAssert.Equal(1, MatchedMapId(accounts, games, current, "outside"),
                "the normal queue plays Summoner's Rift outside the Harrowing window");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    // Ten solo players queue for the normal queue; the matched game reports the map the queue chose for it.
    private static int MatchedMapId(PlatformAccounts accounts, PlatformGames games, Dictionary<string, long> current, string round)
    {
        var players = Enumerable.Range(0, 10).Select(index =>
            accounts.RegisterPublic(round + index, "testing-password", "Queue " + round + " " + index)).ToArray();
        var connections = players.Select((_, index) => round + "-" + index).ToArray();
        for (var index = 0; index < players.Length; index++)
        {
            current[connections[index]] = players[index].Id;
            _ = games.AttachQueue(players[index], connections[index], [2]);
            if (index < players.Length - 1) games.ConfirmQueueReply(connections[index]);
        }
        games.Tick();
        games.ConfirmQueueReply(connections[^1]);
        games.Tick();
        return Convert.ToInt32(((AsObject)games.Current(players[0].Id)!)["mapId"]);
    }

    private static int MapIdOf(object lobby) => Convert.ToInt32(((AsObject)lobby)["mapId"]);

    // The lobby's row in the browser's search result (PlatformGames.List, PracticeGameSearchResult).
    private static AsObject Searched(PlatformGames games, AsObject lobby) =>
        ((ArrayCollection)games.List()).Cast<AsObject>().Single(row => Convert.ToInt32(row["id"]) == Convert.ToInt32(lobby["id"]));

    private static string GameModeOf(object lobby) => (string)((AsObject)lobby)["gameMode"];

    // The game-map set the stock client reads (gameMapService.getGameMapSet), in the order the Platform lists it.
    private static int[] MapIds(PlatformServices rpc) =>
        ((IEnumerable)rpc.Route("first", "gameMapService", "getGameMapSet", [])!).Cast<AsObject>()
            .Select(map => Convert.ToInt32(map["gameMapId"])).ToArray();

    private static AirFault Fault(Action action)
    {
        try { action(); }
        catch (AirFault fault) { return fault; }
        throw new InvalidOperationException("The call was expected to be refused with a typed platform exception.");
    }

    private static void Login(PlatformSessions sessions, string connection, PlatformAccount account)
    {
        var session = sessions.Login(connection, account.Username, sessions.IssueTicket(account), Version);
        TestAssert.True(sessions.AuthenticateChannel(connection, account.Username, session.Token));
    }

    // The placeholder executable never starts: only the lobby, the queue and the map list matter here.
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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
