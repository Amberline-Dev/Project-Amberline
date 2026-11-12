using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using RtmpSharp.Net;
using static Amberline.Tests.Air.HostInviteChecks;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The host's activity feed (<see cref="HostActivity"/>, task 2 of the host pages redesign): the feed's own rules, then each line where it
/// is written. Made-up logins, temporary folders and free loopback ports. The home run takes a real login over the running Platform's own
/// RTMP port; the games run this repo's own match server and stop before the check ends. No browser and no stock client run.
/// </summary>
public static class HostActivityChecks
{
    private const string Password = "Synthetic-feed-Pw-42";
    private const string HostPassword = "Synthetic-host-Pw-11";
    private const string TestHost = "amberline.example.test";
    private const string ClientVersion = "1.45.11_10_04_15_28";

    [Check("server.host-activity-caps-and-orders")]
    public static void FeedKeepsTheNewestEntriesFirstAndDropsTheOldest()
    {
        var at = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        var now = at;
        var feed = new HostActivity(3, () => now);
        for (var i = 1; i <= 5; i++)
        {
            now = at.AddMinutes(i);
            feed.Add($"event {i}");
        }
        TestAssert.Equal("event 5 | event 4 | event 3", Lines(feed), "the newest entry comes first and the two oldest are dropped");
        TestAssert.Equal(at.AddMinutes(5), feed.Recent()[0].At, "an entry keeps the clock's time");
        TestAssert.Equal(2, feed.Recent(2).Count, "max limits the answer");
        TestAssert.Equal(0, new HostActivity().Recent().Count, "a new feed is empty");
    }

    [Check("server.host-activity-escapes-text")]
    public static void TextIsKeptAsGivenForTheRenderersToEscape()
    {
        var feed = new HostActivity();
        feed.Add("<b>x</b> logged in");
        TestAssert.Equal("<b>x</b> logged in", feed.Recent()[0].Text, "the feed keeps the raw text; the pages escape what they show");
    }

    /// <summary>
    /// Every login the feed names as "logged in" ends with exactly one "logged out": an explicit logout, the host's end of a parked login, a
    /// drop before the channel authenticates, a parked login's resume window (a fake clock), a second login, and an open login that stops
    /// sending heartbeats. A parked login writes nothing while its window is open.
    /// </summary>
    [Check("server.host-activity-sessions")]
    public static void EveryLoginEndsOnceAndAParkedLoginWaitsForItsWindow()
    {
        var root = Directory.CreateTempSubdirectory("league-host-activity-sessions").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            var account = accounts.RegisterPublic("feedlogin", Password, "Feed Login");

            // An explicit logout ends the login once; a second logout finds nothing. The host's end of a parked login is one more logout.
            var (feed, _, sessions) = NewSessions(accounts);
            sessions.Login("feed-connection-1", account.Username, sessions.IssueTicket(account), ClientVersion);
            sessions.Logout("feed-connection-1");
            sessions.Logout("feed-connection-1");
            TestAssert.Equal("feedlogin logged out | feedlogin logged in", Lines(feed), "a logout ends the login once; a second logout adds nothing");
            var parked = sessions.Login("feed-connection-2", account.Username, sessions.IssueTicket(account), ClientVersion);
            sessions.AuthenticateChannel("feed-connection-2", parked.Username, parked.Token);
            sessions.Suspend("feed-connection-2");
            TestAssert.Equal("feedlogin logged in | feedlogin logged out | feedlogin logged in", Lines(feed), "a dropped login is parked, not ended");
            sessions.EndAccount(account.Id);
            TestAssert.Equal("feedlogin logged out | feedlogin logged in | feedlogin logged out | feedlogin logged in", Lines(feed),
                "the host's end of a parked login is one logout");
            sessions.EndAccount(account.Id);
            TestAssert.Equal(4, feed.Recent().Count, "an account with no login has nothing to end");

            // A drop before the channel authenticates is not parked: the login ends at the drop.
            var (dropped, _, dropSessions) = NewSessions(accounts);
            dropSessions.Login("drop-connection", account.Username, dropSessions.IssueTicket(account), ClientVersion);
            dropSessions.Suspend("drop-connection");
            TestAssert.Equal("feedlogin logged out | feedlogin logged in", Lines(dropped), "a drop before channel authentication ends the login");
            TestAssert.Equal(0, dropSessions.Online().Count, "and nothing is parked");

            // A parked login waits out its resume window (60 minutes) with nothing written; the window's end is its logout.
            var (window, windowTime, windowSessions) = NewSessions(accounts);
            var waiting = windowSessions.Login("window-connection", account.Username, windowSessions.IssueTicket(account), ClientVersion);
            windowSessions.AuthenticateChannel("window-connection", waiting.Username, waiting.Token);
            windowSessions.Suspend("window-connection");
            windowTime.Now = windowTime.Now.AddMinutes(59);
            TestAssert.Equal(1, windowSessions.Online().Count, "a parked login is listed inside its resume window");
            TestAssert.Equal("feedlogin logged in", Lines(window), "and writes nothing while it waits");
            windowTime.Now = windowTime.Now.AddMinutes(2);
            TestAssert.Equal(0, windowSessions.Online().Count, "past the window it is discarded");
            TestAssert.Equal("feedlogin logged out | feedlogin logged in", Lines(window), "the window's end is its logout");

            // A second login ends the first; a further login ends a parked one the same way.
            var (super, _, superSessions) = NewSessions(accounts);
            superSessions.Login("super-connection-1", account.Username, superSessions.IssueTicket(account), ClientVersion);
            var second = superSessions.Login("super-connection-2", account.Username, superSessions.IssueTicket(account), ClientVersion);
            TestAssert.Equal("feedlogin logged in | feedlogin logged out | feedlogin logged in", Lines(super), "a second login ends the first");
            superSessions.AuthenticateChannel("super-connection-2", second.Username, second.Token);
            superSessions.Suspend("super-connection-2");
            superSessions.Login("super-connection-3", account.Username, superSessions.IssueTicket(account), ClientVersion);
            TestAssert.Equal("feedlogin logged in | feedlogin logged out | feedlogin logged in | feedlogin logged out | feedlogin logged in",
                Lines(super), "a further login ends a parked one");

            // An open login that sends no heartbeat for 30 minutes expires, and its expiry is its logout.
            var (open, openTime, openSessions) = NewSessions(accounts);
            openSessions.Login("open-connection", account.Username, openSessions.IssueTicket(account), ClientVersion);
            openTime.Now = openTime.Now.AddMinutes(31);
            TestAssert.True(openSessions.AccountForConnection("open-connection") is null, "an open login with no heartbeat expires");
            TestAssert.Equal("feedlogin logged out | feedlogin logged in", Lines(open), "its expiry is its logout");
        }
        finally { Remove(root); }
    }

    [Check("server.host-activity-records-events", TimeoutSeconds = 300)]
    public static void SignUpsLoginsAndGamesReachTheFeed() => RecordsAsync().GetAwaiter().GetResult();

    private static async Task RecordsAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Home.Create("league-host-activity");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_one", HostPassword, "Host One").Id;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId, selfRegistration = true });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using var person = HostSelfRegistrationChecks.Person(home, IPAddress.Loopback);
            await PostAsync(person, "/register", Token(await person.GetStringAsync("/register")), HttpStatusCode.OK,
                ("username", "Feed1"), ("summoner", ""), ("password", Password), ("mode", "maxed"));
            TestAssert.Equal("feed1 signed up", Lines(home.Loop.Activity), "a sign-up on the friends' port is the one line");
            await RemotingLoginAsync(home, browser);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        var games = await GamesAsync();
        foreach (var entry in home.Loop.Activity.Recent().Concat(games.Recent()))
        {
            TestAssert.False(Regex.IsMatch(entry.Text, @"\d{1,3}(\.\d{1,3}){3}"), "no address reaches the feed: " + entry.Text);
            TestAssert.False(entry.Text.Contains(Password, StringComparison.Ordinal), "no password reaches the feed");
        }
        TestAssert.Equal("5v5 map | 5v5 map (autumn) | Tutorial map | 3v3 map | Capture map | Map 9",
            string.Join(" | ", new[] { 1, 2, 3, 4, 8, 9 }.Select(HostActivity.MapName)), "the neutral map names");
        Delete(home.Root);
    }

    /// <summary>
    /// The stock client's login over the running Platform's own RTMP port: the login queue's ticket, the remoting login, the channel login and
    /// the logout. The feed names each one before its reply, so its lines are checked right after each call.
    /// </summary>
    private static async Task RemotingLoginAsync(Home home, HttpClient browser)
    {
        var ticket = JsonDocument.Parse(await LoginAsync(browser, "feed1", Password, HttpStatusCode.OK)).RootElement.GetProperty("token").GetString()!;
        var rtmp = new RtmpClient(new Uri($"rtmp://127.0.0.1:{home.RtmpPort}"), new SerializationContext(), ObjectEncoding.Amf3);
        try
        {
            await rtmp.ConnectAsync();
            var login = (AsObject)await rtmp.InvokeAsync<object>("my-rtmps", "loginService", "login",
                AirWire.Object("com.riotgames.platform.login.AuthenticationCredentials",
                    ("username", "feed1"), ("authToken", ticket), ("clientVersion", ClientVersion)));
            TestAssert.Equal("feed1 logged in | feed1 signed up", Lines(home.Loop.Activity), "the login is named before its reply");
            TestAssert.True(await rtmp.LoginAsync("feed1", (string)login["token"]), "the channel login succeeds");
            await rtmp.InvokeAsync<object>("my-rtmps", "loginService", "logout", (string)login["token"]);
            TestAssert.Equal("feed1 logged out | feed1 logged in | feed1 signed up", Lines(home.Loop.Activity), "the logout is named before its reply");
        }
        finally { rtmp.Close(); }
    }

    /// <summary>
    /// Three games on one match server: a 3v3 lobby with two seats and a one-seat 5v5 game, both seen playing (the seam stands in for the
    /// server's report); one 5v5 game whose final result is imported; and one 5v5 game stopped before it reported. Returns the feed they wrote.
    /// </summary>
    private static async Task<HostActivity> GamesAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-host-activity-games").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Close();
            await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(),
                FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var feed = new HostActivity();
            var clock = new Clock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock, feed) { Push = (_, _) => { } };

            // Started: a match reports Playing only once its clients have loaded, so the seam stands in for that report.
            var trees = accounts.RegisterPublic("feedtrees", Password, "Feed Trees");
            var second = accounts.RegisterPublic("feedsecond", Password, "Feed Second");
            var lobby = Convert.ToInt32(((AsObject)games.Create(trees, "Trees", "", 4, 6, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"))["id"]);
            games.Join(second, lobby, "");
            games.StartPlayingForTest(lobby);
            var winner = accounts.RegisterPublic("feedwin", Password, "Feed Win");
            var won = Solo(games, clock, winner, "Win");
            games.StartPlayingForTest(won);
            TestAssert.Equal("Game started · 5v5 map · 1 player | Game started · 3v3 map · 2 players", Lines(feed),
                "a game with one seat says player");

            // Ended: the match writes its final result, and the next pass imports it and names the side that won.
            var match = manager.List(PlatformGames.Guest(winner)).Single();
            var seat = MatchRoster.Read(Path.Combine(root, "games", match.Id.ToString("N"), "roster.json")).Single();
            var logs = Path.Combine(root, "games", match.Id.ToString("N"), "logs", "session");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "match-result.json"),
                JsonSerializer.Serialize(new { session_id = match.Id.ToString(), result = Completed(seat, winner) }));
            games.Tick();
            await manager.StopAsync(PlatformGames.Guest(winner), match.Id);
            games.Tick();
            TestAssert.Equal("Game ended · 5v5 map · Blue won | Game started · 5v5 map · 1 player | Game started · 3v3 map · 2 players", Lines(feed),
                "a finished game names the side that won");

            // Ended early: a match stopped before any result is a game that ended early.
            var lost = accounts.RegisterPublic("feedlost", Password, "Feed Lost");
            Solo(games, clock, lost, "Lost");
            var stopped = manager.List(PlatformGames.Guest(lost)).Single(game => game.Id != match.Id);
            await manager.StopAsync(PlatformGames.Guest(lost), stopped.Id);
            games.Tick();
            TestAssert.Equal("Game ended early · 5v5 map | Game ended · 5v5 map · Blue won | Game started · 5v5 map · 1 player | Game started · 3v3 map · 2 players",
                Lines(feed), "a game stopped before its result ends early");
            return feed;
        }
        finally { Remove(root); }
    }

    /// <summary>A one-player 5v5 custom game from selection to launch: its countdown ends and the match is handed to the match server.</summary>
    internal static int Solo(PlatformGames games, Clock clock, PlatformAccount account, string name)
    {
        var created = (AsObject)games.Create(account, name, "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
        var id = Convert.ToInt32(created["id"]);
        games.Start(account.Id, id, Convert.ToInt64(created["optimisticLock"]));
        games.Pick(account.Id, 1);
        games.LockIn(account.Id);
        clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
        games.Tick();
        return id;
    }

    /// <summary>The final result of one seat that won (the shape AirIntegrationChecks writes for the same server).</summary>
    internal static MatchResult Completed(MatchPlayer seat, PlatformAccount account) => new(
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

    /// <summary>A fresh feed, the clock the check moves, and the sessions that write to that feed.</summary>
    private static (HostActivity Feed, Clock Time, PlatformSessions Sessions) NewSessions(PlatformAccounts accounts)
    {
        var feed = new HostActivity();
        var time = new Clock();
        return (feed, time, new PlatformSessions(accounts, time, feed));
    }

    internal static string ServerExecutable() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "Amberline.Server", "release", "Amberline.Server.exe"));

    /// <summary>The feed's lines, newest first, joined for one comparison.</summary>
    private static string Lines(HostActivity feed) => string.Join(" | ", feed.Recent().Select(entry => entry.Text));

    private static void Remove(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
