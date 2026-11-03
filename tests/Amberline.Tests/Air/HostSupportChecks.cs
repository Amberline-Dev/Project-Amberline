using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Platform;
using Amberline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 640: the dashboard's cards about games and the disk (A-NET-10, A-RUN-01, A-DATA-02) and "Copy a support log", on a page-only
/// test instance (the gate and the dashboard on loopback, no AIR platform): the matches are a list, the logins and the drive's free
/// space are given, since a check cannot start a real match or fill a disk. Every name and address in them is made up.
/// </summary>
public static class HostSupportChecks
{
    private const string Waiting = "Waiting for players";

    [Check("server.host-run-cards", TimeoutSeconds = 120)]
    public static void TheGameAndDiskCardsComeAndGo() => RunCardsAsync().GetAwaiter().GetResult();

    [Check("server.host-support-log-masks", TimeoutSeconds = 120)]
    public static void TheSupportLogHidesEveryPlantedNameAndAddress() => SupportLogAsync().GetAwaiter().GetResult();

    private static async Task RunCardsAsync()
    {
        TunnelMark();
        var home = Home.Create("league-host-run-cards");
        var settings = Settings(home);
        var history = Game("Failed", error: "An old game of the last start.");
        var games = new List<RemoteGame> { history };
        long free = 500_000_000;
        var support = new HostSupport(home.Loop, settings, List(games), Logins.Seat, () => [])
        {
            Every = TimeSpan.FromHours(1), FreeSpace = _ => Interlocked.Read(ref free),
        };
        var run = Task.Run(() => RunPageAsync(home, settings, support));
        using var client = home.Client();
        await WaitForStartAsync(home.Loop, run);
        await home.SignInAsync(client);

        // The watch's first look at the start: the disk card, and no card for a game of the last start.
        var status = await WaitForStatusAsync(client, status => Cards(status).Any(card => card.Code == "A-DATA-02"));
        var disk = Card(status, "A-DATA-02");
        TestAssert.Equal("The drive Amberline keeps its data on (C:) is almost full: 500 MB free.", disk.Message.Replace(Drive(home), "C:"), "the disk card");
        TestAssert.True(disk.WhatToDo.Contains("at least 1.0 GB", StringComparison.Ordinal) && disk.Action is null, "the threshold, no button: " + disk.WhatToDo);
        TestAssert.False(Cards(status).Any(card => card.Code == "A-RUN-01"), "a game of the last start raises no card");
        Interlocked.Exchange(ref free, 50_000_000);
        support.Observe(DateTimeOffset.Now);
        TestAssert.True(Card(await StatusAsync(client), "A-DATA-02").Message.Contains("is full: 50 MB free. Games, settings and the log may not save.", StringComparison.Ordinal),
            "the full disk");
        Interlocked.Exchange(ref free, 5_000_000_000);
        support.Observe(DateTimeOffset.Now);
        TestAssert.False(Cards(await StatusAsync(client)).Any(card => card.Code == "A-DATA-02"), "freed space ends the card");

        // A game waiting for players: a friend through the tunnel who never connects, the host's own seat (direct), a friend who
        // connected and a bot. First seen three minutes ago, so the friend has waited longer than ConnectWait.
        var first = Game(Waiting, [Seat(1, 101), Seat(2, 100), Seat(3, 102, connected: true), Seat(4, 0, bot: true)]);
        Set(games, history, first);
        support.Observe(DateTimeOffset.Now - TimeSpan.FromMinutes(3));
        // A second game first seen now: its friend has not waited long enough yet.
        var second = Game(Waiting, [Seat(1, 103)]);
        Set(games, history, first, second);
        support.Observe(DateTimeOffset.Now);
        var seats = Card(await StatusAsync(client), "A-NET-10");
        TestAssert.Equal("friend1 logged in, but their game could not connect to Amberline.", seats.Message, "only the friend through the tunnel who never connected");
        TestAssert.True(seats.WhatToDo.Contains("game ports (UDP 5219 to 5222)", StringComparison.Ordinal) && seats.WhatToDo.Contains("router", StringComparison.Ordinal) &&
            seats.WhatToDo.Contains("Windows Firewall", StringComparison.Ordinal) && seats.Action == "seat", "what to check: " + seats.WhatToDo);
        var home2 = support.SeatsCard(["friend1", "friend3"], homeOnly: true);
        TestAssert.Equal("friend1 and friend3 logged in, but their games could not connect to Amberline.", home2.Message, "two friends");
        TestAssert.True(home2.WhatToDo.Contains("home network", StringComparison.Ordinal) && !home2.WhatToDo.Contains("router", StringComparison.Ordinal),
            "the home network only needs no router: " + home2.WhatToDo);
        TestAssert.Equal("a, b and 3 more friends logged in, but their games could not connect to Amberline.",
            support.SeatsCard(["a", "b", "c", "d", "e"], homeOnly: false).Message, "many friends");

        // The page: the element the script waits for, the cards' buttons and the support log's link.
        using (var page = await client.GetAsync(HostDashboard.PagePath))
        {
            var html = await page.Content.ReadAsStringAsync();
            var script = await client.GetStringAsync(HostDashboard.PagePath + "/page.js");
            TestAssert.True(html.Contains("<section id=status ", StringComparison.Ordinal) && script.Contains("if (byId('status'))", StringComparison.Ordinal),
                "page.js refreshes only when the page has #status: " + html);
            foreach (var expected in new[] { "data-action=seat", "data-action=run", $"<a href={HostSupport.PagePath}>support log</a>" })
                TestAssert.True(html.Contains(expected, StringComparison.Ordinal), $"the dashboard carries {expected}: {html}");
        }

        // Hide: refused without the page's token or for another card; with both, the card goes.
        var token = Token(await (await client.GetAsync(HostDashboard.PagePath)).Content.ReadAsStringAsync());
        using (var bare = await client.PostAsync(HostSupport.HidePath, Form(new("x", "y"), ("card", "A-NET-10"))))
            TestAssert.Equal(HttpStatusCode.BadRequest, bare.StatusCode, "a form without its token");
        using (var other = await client.PostAsync(HostSupport.HidePath, Form(token, ("card", "A-NET-09"))))
            TestAssert.Equal(HttpStatusCode.BadRequest, other.StatusCode, "another card");
        TestAssert.True(Cards(await StatusAsync(client)).Any(card => card.Code == "A-NET-10"), "nothing hid the card yet");
        using (var hide = await client.PostAsync(HostSupport.HidePath, Form(token, ("card", "A-NET-10"))))
            TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide this message");
        TestAssert.False(Cards(await StatusAsync(client)).Any(card => card.Code == "A-NET-10"), "the card is hidden");

        // The next game of the same friend that does not connect raises it again; once the friend's game connects, it goes.
        var third = Game(Waiting, [Seat(1, 101)]);
        Set(games, history, first, second, third);
        support.Observe(DateTimeOffset.Now - TimeSpan.FromMinutes(3));
        support.Observe(DateTimeOffset.Now);
        TestAssert.Equal("friend1 logged in, but their game could not connect to Amberline.", Card(await StatusAsync(client), "A-NET-10").Message, "a new failure shows again");
        Set(games, history, first, second, third with { State = "Playing", Seats = [Seat(1, 101, connected: true)] });
        support.Observe(DateTimeOffset.Now);
        TestAssert.False(Cards(await StatusAsync(client)).Any(card => card.Code == "A-NET-10"), "the friend's game connected: their game ports work");

        // A friend's game that the host stopped before the wait ran out, and a seat first seen after its game ended: no card.
        var short1 = Game(Waiting, [Seat(1, 103)]);
        var ended = Game("Stopped", [Seat(1, 101)]);
        Set(games, history, short1, ended);
        support.Observe(DateTimeOffset.Now);
        Set(games, history);
        support.Observe(DateTimeOffset.Now + TimeSpan.FromMinutes(5));
        TestAssert.False(support.Cards(homeOnly: false).Any(card => card.Code == "A-NET-10"),
            "a game gone before its wait ran out, or seen only after it ended, raises nothing");

        // A-RUN-01: a match server of this start stopped; then a second one; Hide ends the card until the next one.
        Set(games, history, first with { State = "Failed", Error = "Game process exited unexpectedly. Check its host logs." });
        support.Observe(DateTimeOffset.Now);
        var stopped = Card(await StatusAsync(client), "A-RUN-01");
        TestAssert.True(Regex.IsMatch(stopped.Message, @"^A game stopped unexpectedly at \d\d:\d\d\. The match ended for everyone in it\.$") &&
            stopped.WhatToDo.Contains("send a support log", StringComparison.Ordinal) && stopped.Action == "run", "the stopped game: " + stopped.Message);
        Set(games, history, first with { State = "Failed" }, second with { State = "Failed", Error = "Game startup timed out." });
        support.Observe(DateTimeOffset.Now);
        TestAssert.True(Card(await StatusAsync(client), "A-RUN-01").Message.StartsWith("2 games stopped unexpectedly, the last at ", StringComparison.Ordinal), "two games");
        using (var hide = await client.PostAsync(HostSupport.HidePath, Form(token, ("card", "A-RUN-01"))))
            TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide this message");
        TestAssert.False(Cards(await StatusAsync(client)).Any(card => card.Code == "A-RUN-01"), "the stopped games' card is hidden");
        Set(games, history, first with { State = "Failed" }, second with { State = "Failed" }, Game("Failed", error: "Game process exited before readiness."));
        support.Observe(DateTimeOffset.Now);
        TestAssert.True(Card(await StatusAsync(client), "A-RUN-01").Message.StartsWith("A game stopped unexpectedly at ", StringComparison.Ordinal),
            "a new stop after Hide shows only the new one");

        home.Loop.Stop();
        TestAssert.False(await run.WaitAsync(TimeSpan.FromSeconds(30)), "the page stopped");
        Delete(home.Root);
    }

    private static async Task SupportLogAsync()
    {
        var home = Home.Create("league-host-support-log");
        var settings = Settings(home) with { PublicHost = "planted-home.example.net" };
        Directory.CreateDirectory(home.Loop.Paths.Logs);
        File.WriteAllLines(Path.Combine(home.Loop.Paths.Logs, "amberline-2026-11-02.log"), ["an older day's line"]);
        File.WriteAllLines(Path.Combine(home.Loop.Paths.Logs, "amberline-2026-11-03.log"),
        [
            "friend planted_friend reached the tunnel from 198.51.100.23:50211",
            "a friend at home 192.168.1.57, a shared one 100.64.3.4 and fe80::1c2b:3a4d:5e6f:7a8b%12",
            "an IPv6 friend 2001:db8:85a3::8a2e:370:7334 and a mapped one ::ffff:198.51.100.24",
            @"C:\Users\PlantedUser9\AppData\Local\Amberline on PLANTED-PC-77",
            "Planted Summoner joined; HOST_PLANTED too; write to planted.person@example.com",
            "password=Planted-Secret-Pw-42 and Password: Another-Planted-Pw",
            "open http://127.0.0.1:8390/open?c=PLANTEDLAUNCHCODE123 now",
            "token 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "the address planted-home.example.net:8391 and 203.0.113.77",
            "kept: game 1.0.0.126, release 0.0.0.51, loopback 127.0.0.1 and ::1 at 20:14:33",
        ]);
        var games = new List<RemoteGame>();
        var support = new HostSupport(home.Loop, settings, List(games), summoner => summoner == 104 ? ("planted_friend", true) : null,
            () => [("planted_friend", "Planted Summoner"), ("host_planted", "Host Planted")])
        {
            Every = TimeSpan.FromHours(1), FreeSpace = _ => 50_000_000_000, MachineName = "PLANTED-PC-77", UserName = "PlantedUser9",
        };
        var run = Task.Run(() => RunPageAsync(home, settings, support));
        using var client = home.Client();
        await WaitForStartAsync(home.Loop, run);
        await home.SignInAsync(client);
        // A card naming the friend, and a stopped game whose reason names this PC's folders.
        var waiting = Game(Waiting, [Seat(1, 104)]);
        Set(games, waiting);
        support.Observe(DateTimeOffset.Now - TimeSpan.FromMinutes(3));
        Set(games, waiting, Game("Failed", error: @"[E] could not open C:\Users\PlantedUser9\data\games\x on PLANTED-PC-77"));
        support.Observe(DateTimeOffset.Now);

        using var page = await client.GetAsync(HostSupport.PagePath);
        var html = await page.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, page.StatusCode, Safe(html));
        TestAssert.True(html.Contains("<textarea id=report readonly ", StringComparison.Ordinal) && html.Contains("<button type=button data-copy=report>", StringComparison.Ordinal) &&
            html.Contains($"href={HostSupport.FilePath} download>", StringComparison.Ordinal) && html.Contains("<span id=copy-status role=status>", StringComparison.Ordinal),
            "a read-only box, Copy and the download: " + Safe(html));
        var box = Regex.Match(html, "<textarea[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value;
        TestAssert.False(box.Contains('<'), "the box holds text only (no code span): " + Safe(box));
        var report = WebUtility.HtmlDecode(box);
        using var file = await client.GetAsync(HostSupport.FilePath);
        var download = await file.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, file.StatusCode, "the download");
        TestAssert.Equal("text/plain", file.Content.Headers.ContentType?.MediaType, "a text file");
        TestAssert.True(file.Content.Headers.ContentDisposition is { DispositionType: "attachment", FileName: "amberline-support.txt" or "\"amberline-support.txt\"" },
            "saved as amberline-support.txt: " + file.Content.Headers.ContentDisposition);
        TestAssert.Equal(Regex.Replace(report, "Made: .*\n", ""), Regex.Replace(download, "Made: .*\n", ""), "the download is the box's report");

        foreach (var planted in new[] { "planted_friend", "Planted Summoner", "host_planted", "Host Planted", "PlantedUser9", "PLANTED-PC-77", "planted-home",
                     "198.51.100", "192.168.1.57", "100.64.3.4", "fe80::", "2001:db8", "planted.person", "Planted-Secret", "Another-Planted", "PLANTEDLAUNCHCODE",
                     "0123456789abcdef", "203.0.113" })
            TestAssert.False(report.Contains(planted, StringComparison.OrdinalIgnoreCase), $"the report hides {planted}: {Safe(report)}");
        foreach (var kept in new[]
                 {
                     "Amberline support log", "Amberline: (development build)", "Logins: 2. Online now: 0. Games running: 0 of 4.",
                     "(A-NET-10) <login> logged in, but their game could not connect", "(A-RUN-01) A game stopped unexpectedly", @"C:\Users\<Windows user>\",
                     "on <PC name>", "<login> joined; <login> too", "<e-mail>", "password=<hidden> and Password: <hidden>", "/open?c=<secret> now", "token <secret>",
                     "<public address>:8391 and <internet address>", "from <internet address>:50211", "home <home address>, a shared one <home address> and <home address>%12",
                     "friend <internet address> and a mapped one ::ffff:<internet address>", "game 1.0.0.126, release 0.0.0.51, loopback 127.0.0.1 and ::1 at 20:14:33",
                     "The log's last 10 lines (amberline-2026-11-03.log)", "Network checks", "Game folder", "AIR client folder",
                     $"Data folder: 50.0 GB free on {Drive(home)}\n",
                     "Games that stopped unexpectedly since the start",
                 })
            TestAssert.True(report.Contains(kept, StringComparison.Ordinal), $"the report has \"{kept}\": {Safe(report)}");
        TestAssert.False(report.Contains("an older day's line", StringComparison.Ordinal), "only the newest log");

        // The privacy pass on its own: one pass, so a login named like a mark stays a mark; a name inside a longer word stays.
        TestAssert.Equal("<login> met <login>; logins stay; <PC name>", HostSupport.Mask("login met Login; logins stay; Box", [("login", "<login>"), ("Box", "<PC name>")]),
            "whole words, case-insensitive, one pass");
        TestAssert.Equal("127.0.0.1 and 0.0.0.0 and :: stay", HostSupport.Mask("127.0.0.1 and 0.0.0.0 and :: stay", [("127.0.0.1", "<public address>")]),
            "loopback and the unspecified address stay, also as the public address");
        // Batch 648, seen in a browser: a LAN party's address said "<public address> (home network only)".
        TestAssert.True(HostSupport.AddressMark("192.168.1.20") == "<home address>" && HostSupport.AddressMark("203.0.113.77") == "<public address>" &&
            HostSupport.AddressMark("planted-home.example.net") == "<public address>", "the address friends use is marked by its kind");

        home.Loop.Stop();
        TestAssert.False(await run.WaitAsync(TimeSpan.FromSeconds(30)), "the page stopped");
        Delete(home.Root);
    }

    /// <summary>The sessions' tunnel mark: a login through the friends' tunnel is one; a direct one, a closed connection and no login are not.</summary>
    private static void TunnelMark()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-tunnel-mark");
        using (var accounts = new PlatformAccounts(root))
        {
            var direct = accounts.RegisterPublic("mark_direct", "correct-horse-1", "Mark Direct");
            var friend = accounts.RegisterPublic("mark_friend", "correct-horse-1", "Mark Friend");
            var sessions = new PlatformSessions(accounts);
            const string version = "1.45.11_10_04_15_28";
            sessions.Login("direct", direct.Username, sessions.IssueTicket(direct), version);
            sessions.Login("tunnel", friend.Username, sessions.IssueTicket(friend), version);
            sessions.ObserveOrigin("direct", throughTunnel: false);
            sessions.ObserveOrigin("tunnel", throughTunnel: true);
            TestAssert.True(sessions.ThroughTunnel(friend.Id), "a login through the tunnel is a friend's");
            TestAssert.False(sessions.ThroughTunnel(direct.Id), "a direct login is not");
            TestAssert.False(sessions.ThroughTunnel(987654321), "no login is not");
            sessions.Suspend("tunnel");
            TestAssert.False(sessions.ThroughTunnel(friend.Id), "a closed connection forgets its mark");
        }
        Delete(root);
    }

    /// <summary>The logins behind the seats: 101, 103 and 104 are friends through the tunnel, 100 the host, 102 a friend who connects.</summary>
    private static class Logins
    {
        public static (string Username, bool ThroughTunnel)? Seat(long summoner) => summoner switch
        {
            100 => ("host_own", false),
            >= 101 and <= 104 => ($"friend{summoner - 100}", true),
            _ => null,
        };
    }

    private static PlatformSettings Settings(Home home) =>
        new(home.Game, home.Loop.Paths.ServerExecutable, HttpPort: home.Port, RemotePort: home.RemotePort, AirRoot: home.Air);

    private static Func<IReadOnlyList<RemoteGame>> List(List<RemoteGame> games) => () => { lock (games) return [.. games]; };

    private static void Set(List<RemoteGame> games, params RemoteGame[] now)
    {
        lock (games) { games.Clear(); games.AddRange(now); }
    }

    private static RemoteGame Game(string state, RemoteSeat[]? seats = null, string? error = null) =>
        new(Guid.NewGuid(), "host_own", state, 5219, seats ?? [], CanStop: false, error);

    private static RemoteSeat Seat(int id, int summoner, bool connected = false, bool bot = false) =>
        new(id, new MatchPlayer($"Seat {id}", "Annie", Team.Order, IsBot: bot, PlayerId: bot ? null : summoner), Claimed: !bot, Yours: false,
            connected, connected);

    private static DashboardCard[] Cards(JsonElement status) =>
        [.. status.GetProperty("attention").EnumerateArray().Select(card => new DashboardCard(card.GetProperty("code").GetString()!,
            card.GetProperty("message").GetString()!, card.GetProperty("whatToDo").GetString()!, card.GetProperty("action").GetString()))];

    private static DashboardCard Card(JsonElement status, string code) =>
        Cards(status).SingleOrDefault(card => card.Code == code) ?? throw new InvalidOperationException($"no {code} card: {Safe(status.GetRawText())}");

    /// <summary>The temporary data folder's drive without its backslash, as the card names it ("C:").</summary>
    private static string Drive(Home home) => Path.GetPathRoot(home.Loop.Paths.Data)!.TrimEnd('\\');

    /// <summary>A failure message without this PC's own names (temporary folders sit in the user's profile).</summary>
    private static string Safe(string text) => HostSupport.Mask(text, [(Environment.UserName, "<you>"), (Environment.MachineName, "<this PC>")]);

    private static async Task<bool> RunPageAsync(Home home, PlatformSettings settings, HostSupport support)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, home.Port));
        builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(home.Loop.Paths.Keys));
        await using var app = builder.Build();
        HostGate.Use(app, home.Port, home.Loop.Sessions, HostDashboard.PagePath);
        HostPages.Use(app, home.Loop, () => "Planted Server", nav: true);
        await using var dashboard = new HostDashboard(home.Loop, settings, () => [], () => []) { Support = support };
        dashboard.Map(app);
        return await home.Loop.RunAsync(app, home.Port);
    }

    private static async Task WaitForStartAsync(HostLoop loop, Task run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (loop.Starts < 1 && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (run.IsFaulted) await run;
        TestAssert.Equal(1, loop.Starts, "the page started");
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var answer = await client.GetAsync("/host/status.json");
        var text = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, Safe(text));
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Func<JsonElement, bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await StatusAsync(client);
            if (ready(status)) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the watch never looked: " + Safe(status.GetRawText()));
            await Task.Delay(100);
        }
    }

    private static FormUrlEncodedContent Form(KeyValuePair<string, string> token, params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)).Prepend(token));
}
