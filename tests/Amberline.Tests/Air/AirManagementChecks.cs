using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.AirCompanion;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RtmpSharp.IO;
using RtmpSharp.Net;

namespace Amberline.Tests.Air;

public static class AirManagementChecks
{
    [Check("server.air-management-retired-invitation-routes")]
    public static void HostPageRetiresTheInvitationRoutes() => RetiredInvitationRoutesAsync().GetAwaiter().GetResult();

    [Check("server.air-management-reward-code-csrf-boundary")]
    public static void HostRewardCodesAreLoopbackCsrfProtectedAndRevocable() => RewardCodeManagementAsync().GetAwaiter().GetResult();

    [Check("server.air-management-online-now")]
    public static void OnlineNowShowsSessionsWithTheirRealAddressesAndEncodesNames() => OnlineNowAsync().GetAwaiter().GetResult();

    [Check("server.air-management-broadcast")]
    public static void PlayerNoticeIsLoopbackOnlyFormOrJsonAndNeverForged() => BroadcastAsync().GetAwaiter().GetResult();

    [Check("server.host-advanced-partial-refresh")]
    public static void HomeAdvancedUpdatesItsTableInPlaceAndTheOperatorPageStays() => AdvancedRefreshAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 646: the home host's Advanced (<c>/manage</c> with Invite a friend) never reloads itself, so nothing typed in its forms is
    /// lost: <c>page.js</c> refills the Online now table from <c>/manage/online.json</c> while it is live and says the count in a live
    /// line. The operator's page (no <c>--home</c>) keeps its 5-second reload, its words and no script, and has no such route.
    /// </summary>
    private static async Task AdvancedRefreshAsync()
    {
        const string Json = "/manage/online.json";
        var root = Path.Combine(Path.GetTempPath(), "league-air-advanced-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(root);
            const string hostile = "<b>Chuck</b>&\"'";
            PlatformManagement.OnlineRow[] rows = [new("alpha", hostile, "this PC", "Home, logged in", "logged in 10:00:00 UTC (5 s)", "not connected")];
            foreach (var home in new[] { false, true })
            {
                var port = FreePort();
                var web = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                web.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
                web.Services.AddAntiforgery();
                await using var app = web.Build();
                PlatformManagement.Map(app, accounts, port, FreePort(), online: () => rows, invite: home ? HostInvite.InvitePath : null);
                await app.StartAsync();
                try
                {
                    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                    var live = await client.GetStringAsync("/manage");
                    var paused = await client.GetStringAsync("/manage?refresh=off");
                    using var table = await client.GetAsync(Json);
                    // Batch 648: a refused form. The operator's exception goes on to Program's error middleware (its JSON answer; this bare
                    // app has none); the home host's Advanced answers with itself, its error summary linking the field.
                    var token = Regex.Match(live, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                    using var refusal = await client.PostAsync("/manage/grant", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        [token.Groups[1].Value] = WebUtility.HtmlDecode(token.Groups[2].Value), ["accountId"] = "1", ["rp"] = "0", ["requestId"] = Guid.NewGuid().ToString(),
                    }));
                    var answered = await refusal.Content.ReadAsStringAsync();
                    TestAssert.True(home ? refusal.StatusCode == HttpStatusCode.BadRequest && answered.Contains("<h2 id=error-title>There is a problem</h2>", StringComparison.Ordinal) &&
                            answered.Contains("<li><a href=#grant-rp>Choose an RP amount from 1 to 1,000,000.</a></li>", StringComparison.Ordinal)
                        : refusal.StatusCode != HttpStatusCode.BadRequest && !answered.Contains("There is a problem", StringComparison.Ordinal),
                        $"a refused form ({(home ? "home" : "operator")}): {refusal.StatusCode} {answered}");
                    if (!home)
                    {
                        TestAssert.True(live.Contains("<h2>Grant testing RP</h2>", StringComparison.Ordinal) &&
                            live.Contains("<label>Account<select name=accountId required>", StringComparison.Ordinal), "the operator's words stay: " + live);
                        TestAssert.True(live.Contains("<meta http-equiv=refresh content=5>", StringComparison.Ordinal) && live.Contains("<h2>Online now (1)</h2>", StringComparison.Ordinal) &&
                            live.Contains("<a href=\"/manage?refresh=off\">Pause</a> it to use the forms below.", StringComparison.Ordinal) &&
                            !live.Contains("<script", StringComparison.Ordinal), "the operator's page reloads itself as before: " + live);
                        TestAssert.True(!paused.Contains("http-equiv=refresh", StringComparison.Ordinal) && paused.Contains("Refresh is paused.", StringComparison.Ordinal), paused);
                        TestAssert.Equal(HttpStatusCode.NotFound, table.StatusCode, "the operator's page has no table route");
                        continue;
                    }
                    TestAssert.True(!live.Contains("http-equiv=refresh", StringComparison.Ordinal) && live.Contains("<script src=/host/page.js defer></script>", StringComparison.Ordinal) &&
                        live.Contains("<p id=online-count aria-live=polite>1 person is logged in.</p>", StringComparison.Ordinal) &&
                        live.Contains("<table id=online-table data-live><thead><tr><th scope=col>Username</th>", StringComparison.Ordinal) &&
                        live.Contains("<tbody id=online-rows><tr><td>alpha</td><td>&lt;b&gt;Chuck&lt;/b&gt;", StringComparison.Ordinal) &&
                        live.Contains("<a href=\"/manage?refresh=off\">Pause updates</a>", StringComparison.Ordinal), "home: no full reload, page.js updates the live table: " + live);
                    TestAssert.True(!paused.Contains("http-equiv=refresh", StringComparison.Ordinal) && !paused.Contains("data-live", StringComparison.Ordinal) &&
                        paused.Contains("<a href=\"/manage\">Resume updates</a>", StringComparison.Ordinal), "paused: the table stays as it is: " + paused);
                    TestAssert.Equal(HttpStatusCode.OK, table.StatusCode, "the table's route");
                    TestAssert.True(table.Headers.CacheControl?.NoStore == true && table.Content.Headers.ContentType?.MediaType == "application/json", "never cached, JSON");
                    using (var answer = JsonDocument.Parse(await table.Content.ReadAsStringAsync()))
                    {
                        var row = answer.RootElement.EnumerateArray().Single();
                        TestAssert.True(row.EnumerateObject().Select(field => field.Name).SequenceEqual(["username", "summoner", "address", "doing", "since", "chat"]) &&
                            row.GetProperty("summoner").GetString() == hostile, "the six cells, as text the script sets with textContent: " + row);
                    }
                    rows = [];
                    var empty = await client.GetStringAsync("/manage");
                    TestAssert.True(empty.Contains("<p id=online-count aria-live=polite>Nobody is logged in.</p>", StringComparison.Ordinal) &&
                        empty.Contains("<table id=online-table data-live hidden>", StringComparison.Ordinal), "nobody online: the line says so, the table hides: " + empty);
                    using var remote = new HttpRequestMessage(HttpMethod.Get, Json);
                    remote.Headers.Host = "public.example";
                    using var refused = await client.SendAsync(remote);
                    TestAssert.Equal(HttpStatusCode.NotFound, refused.StatusCode, "the table's route is loopback only");
                }
                finally { await app.StopAsync(); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Batch 549: the "Notify players" form needs the antiforgery token; the script endpoint needs JSON; both are loopback-only.
    private static async Task BroadcastAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-management-broadcast-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(root);
            var calls = new List<(string? Text, string Severity)>();
            var port = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery();
            await using var app = builder.Build();
            PlatformManagement.Map(app, accounts, port, FreePort(), broadcast: (text, severity) => { calls.Add((text, severity)); return 3; });
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                var page = await client.GetStringAsync("/manage");
                TestAssert.True(page.Contains("Notify players", StringComparison.Ordinal) && page.Contains("No notice is showing", StringComparison.Ordinal));
                var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                var token = new KeyValuePair<string, string>(field.Groups[1].Value, WebUtility.HtmlDecode(field.Groups[2].Value));

                using var forged = await client.PostAsync("/manage/broadcast", new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = "forged" }));
                TestAssert.False(forged.IsSuccessStatusCode || forged.StatusCode == HttpStatusCode.Redirect, "the form needs its antiforgery token");
                using var sent = await client.PostAsync("/manage/broadcast", new FormUrlEncodedContent(
                    new Dictionary<string, string> { [token.Key] = token.Value, ["message"] = " Restart <now> ", ["severity"] = "INFO" }));
                TestAssert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
                TestAssert.True(calls.SequenceEqual([("Restart <now>", "INFO")]), "the trimmed text and the chosen severity are sent");
                page = await client.GetStringAsync("/manage");
                TestAssert.True(page.Contains("reached 3 clients): Restart &lt;now&gt;", StringComparison.Ordinal), "the page shows the current notice, encoded");

                StringContent Json(string body, string type = "application/json") => new(body, System.Text.Encoding.UTF8, type);
                using var plain = await client.PostAsync("/manage/broadcast.json", Json("{\"message\":\"x\"}", "text/plain"));
                TestAssert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode, "a cross-site form or text post is not JSON");
                using var remote = new HttpRequestMessage(HttpMethod.Post, "/manage/broadcast.json") { Content = Json("{\"message\":\"x\"}") };
                remote.Headers.Host = "public.example";
                TestAssert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(remote)).StatusCode, "a rebound host name is refused");
                using var script = await client.PostAsync("/manage/broadcast.json", Json("{\"message\":\"Update in 2 minutes\"}"));
                TestAssert.Equal(HttpStatusCode.OK, script.StatusCode);
                TestAssert.Equal(3, JsonDocument.Parse(await script.Content.ReadAsStringAsync()).RootElement.GetProperty("reached").GetInt32());
                using var tooLong = await client.PostAsync("/manage/broadcast.json", Json($"{{\"message\":\"{new string('a', 201)}\"}}"));
                TestAssert.False(tooLong.IsSuccessStatusCode, "a notice is one ticker line");
                using var badLevel = await client.PostAsync("/manage/broadcast.json", Json("{\"message\":\"x\",\"severity\":\"LOUD\"}"));
                TestAssert.False(badLevel.IsSuccessStatusCode);
                using var cleared = await client.PostAsync("/manage/broadcast.json", Json("{\"message\":\"\"}"));
                TestAssert.Equal(HttpStatusCode.OK, cleared.StatusCode);
                TestAssert.True(calls.SequenceEqual([("Restart <now>", "INFO"), ("Update in 2 minutes", "WARN"), (null, "WARN")]),
                    "only the accepted posts reach the clients; an empty message clears");
                TestAssert.True((await client.GetStringAsync("/manage")).Contains("No notice is showing", StringComparison.Ordinal));
            }
            finally { await app.StopAsync(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Fake data only: Alpha..Echo, addresses from the documentation range 203.0.113.0/24.
    private static async Task OnlineNowAsync()
    {
        var friend = IPAddress.Parse("203.0.113.77");
        var stop = TimeSpan.FromSeconds(5);

        // 1. The tunnel names the friend behind its loopback source port while the connection lasts, and forgets it after.
        var peers = new AirTunnelPeers();
        using (var backend = new TcpListener(IPAddress.Loopback, 0))
        {
            backend.Start();
            var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
            var tunnelPort = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, tunnelPort));
            await using var tunnel = builder.Build();
            // The remote listener is HTTPS and sees the friend's own address; this listener is plain loopback, so both are set here.
            tunnel.Use((context, next) => { context.Request.Scheme = "https"; context.Connection.RemoteIpAddress = friend; return next(context); });
            var access = new AirRemoteAccess(Path.Combine(Path.GetTempPath(), "league-air-online-" + Guid.NewGuid().ToString("N")));
            AirRemoteTunnel.Map(tunnel, access, backendPort, FreePort(), peers);
            await tunnel.StartAsync();
            try
            {
                var accepted = backend.AcceptTcpClientAsync();
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + access.Issue(1).Token);
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{tunnelPort}{AirRemoteTunnel.Path}"), CancellationToken.None);
                using var inbound = await accepted.WaitAsync(stop);
                var source = (IPEndPoint)inbound.Client.RemoteEndPoint!;
                TestAssert.True(friend.Equals(peers.Resolve(backendPort, source)),
                    "the loopback listener must see the friend's address behind the tunnel's source port");
                TestAssert.True(IPAddress.Loopback.Equals(peers.Resolve(backendPort + 1, source)),
                    "another port is another connection: the loopback address stays");
                TestAssert.False(peers.IsDirectLocal(backendPort, source), "a tunnel connection is never a direct seat");
                TestAssert.True(peers.IsDirectLocal(backendPort + 1, source), "a loopback connection on another port is direct");
                socket.Abort();
                var deadline = DateTime.UtcNow + stop;
                while (!IPAddress.Loopback.Equals(peers.Resolve(backendPort, source)) && DateTime.UtcNow < deadline) await Task.Delay(20);
                TestAssert.True(IPAddress.Loopback.Equals(peers.Resolve(backendPort, source)), "a closed tunnel connection is forgotten");
                TestAssert.True(peers.IsDirectLocal(backendPort, source), "after a tunnel closes its source is no longer a tunnel");
            }
            finally { await tunnel.StopAsync(); }
        }

        // 2. The RTMP host reports the address of a new connection, from the first message on it.
        var observed = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rtmp = new AirRtmpHost(new IPEndPoint(IPAddress.Loopback, 0), (_, _, _, _) => "ok", (_, _, _) => false,
            _ => null, _ => { }, peerObserved: (_, peer) => observed.TrySetResult(peer));
        await rtmp.StartAsync();
        var rtmpClient = new RtmpClient(new Uri($"rtmp://127.0.0.1:{rtmp.BoundEndPoint.Port}"), new SerializationContext(), ObjectEncoding.Amf3);
        try
        {
            await rtmpClient.ConnectAsync();
            await rtmpClient.InvokeAsync<object>("endpoint", "svc", "op", "x");
            TestAssert.True(IPAddress.IsLoopback((await observed.Task.WaitAsync(stop)).Address), "the RTMP host must report the peer address");
        }
        finally { rtmpClient.Close(); await rtmp.DisposeAsync(); }

        // 3. The table: sessions, the lobby and store they are in, hostile names encoded, loopback-only.
        var root = Path.Combine(Path.GetTempPath(), "league-air-online-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(root);
            PlatformAccount Register(string username, string summoner) => accounts.RegisterPublic(username, "secret-password", summoner);
            var alpha = Register("alpha", "Alpha");
            var bravo = Register("bravo", "Bravo");
            var charlie = Register("charlie", "<b>Chuck</b>&\"'");
            var delta = Register("delta", "Delta");
            var echo = Register("echo", "Echo");
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var sessions = new PlatformSessions(accounts, clock);
            void LogIn(PlatformAccount account, IPAddress? peer)
            {
                if (peer is not null) sessions.ObservePeer("c-" + account.Username, peer);
                var session = sessions.Login("c-" + account.Username, account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
                TestAssert.True(sessions.AuthenticateChannel("c-" + account.Username, account.Username, session.Token));
            }
            LogIn(alpha, IPAddress.Parse("203.0.113.10"));
            LogIn(bravo, IPAddress.Loopback);
            LogIn(charlie, IPAddress.Parse("203.0.113.12"));
            LogIn(delta, IPAddress.Parse("203.0.113.13"));
            LogIn(echo, null);
            sessions.Suspend("c-delta");
            sessions.RedeemStoreTicket(sessions.IssueStoreTicket("c-charlie"));
            Directory.CreateDirectory(Path.Combine(root, "game-data"));
            var executable = Path.Combine(root, "server-placeholder.exe");
            File.WriteAllBytes(executable, []);
            await using var manager = new GameManager(new HostSettings(Path.Combine(root, "game-data"), executable,
                FirstGamePort: FreePort(), MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            const string hostileLobby = "<img src=x onerror=alert(1)>";
            var lobby = (AsObject)games.Create(alpha, hostileLobby, "", 1, 4, 1, "CLASSIC", "NONE");
            games.Join(bravo, Convert.ToInt32(lobby["id"]), "");
            await using var chat = new AirXmppHost(accounts, sessions, Path.Combine(root, "social"), 0);
            clock.Advance(TimeSpan.FromSeconds(20));

            var rows = PlatformManagement.Online(accounts, sessions, games, chat, clock).ToDictionary(row => row.Username);
            TestAssert.Equal(5, rows.Count);
            TestAssert.Equal("203.0.113.10", rows["alpha"].Address);
            TestAssert.True(rows["alpha"].Doing.StartsWith("In lobby of custom game \"" + hostileLobby + "\"", StringComparison.Ordinal) &&
                rows["alpha"].Doing.EndsWith(", host", StringComparison.Ordinal), rows["alpha"].Doing);
            TestAssert.Equal("this PC", rows["bravo"].Address);
            TestAssert.True(rows["bravo"].Doing.StartsWith("In lobby of", StringComparison.Ordinal) && !rows["bravo"].Doing.EndsWith(", host", StringComparison.Ordinal), rows["bravo"].Doing);
            TestAssert.Equal("In the store, last page 20 s ago", rows["charlie"].Doing);
            TestAssert.True(rows["delta"].Doing.StartsWith("Reconnecting", StringComparison.Ordinal), rows["delta"].Doing);
            TestAssert.Equal("203.0.113.13", rows["delta"].Address);
            TestAssert.Equal("unknown", rows["echo"].Address);
            TestAssert.Equal("Home, logged in", rows["echo"].Doing);
            TestAssert.True(rows.Values.All(row => row.Chat == "not connected" && row.Since.Contains(" UTC (", StringComparison.Ordinal)));

            var port = FreePort();
            var web = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            web.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            web.Services.AddAntiforgery();
            await using var app = web.Build();
            PlatformManagement.Map(app, accounts, port, FreePort(), null, null,
                () => PlatformManagement.Online(accounts, sessions, games, chat, clock));
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                var page = await client.GetStringAsync("/manage");
                TestAssert.True(page.Contains("Online now (5)", StringComparison.Ordinal) && page.Contains("203.0.113.10", StringComparison.Ordinal) &&
                    page.Contains("this PC", StringComparison.Ordinal), "the page must list every session with its address");
                TestAssert.True(page.Contains("&lt;img src=x onerror=alert(1)&gt;", StringComparison.Ordinal) &&
                    page.Contains("&lt;b&gt;Chuck&lt;/b&gt;&amp;&quot;&#39;", StringComparison.Ordinal), "player-typed names must be HTML-encoded");
                TestAssert.False(page.Contains("<img src=x", StringComparison.Ordinal) || page.Contains("<b>Chuck", StringComparison.Ordinal),
                    "a hostile lobby or summoner name must never reach the page as markup");
                TestAssert.True(page.Contains("http-equiv=refresh content=5", StringComparison.Ordinal), "the table refreshes itself");
                TestAssert.False((await client.GetStringAsync("/manage?refresh=off")).Contains("http-equiv=refresh", StringComparison.Ordinal),
                    "the refresh can be paused so the forms stay usable");
                using var remote = new HttpRequestMessage(HttpMethod.Get, "/manage");
                remote.Headers.Host = "public.example";
                using var remoteResponse = await client.SendAsync(remote);
                TestAssert.Equal(HttpStatusCode.NotFound, remoteResponse.StatusCode, "the table is operator-only: a remote host name is refused");
            }
            finally { await app.StopAsync(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private static async Task RewardCodeManagementAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-management-reward-code-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote");
        PlatformAccounts? store = null;
        try
        {
            store = new PlatformAccounts(root);
            var accounts = store!;
            accounts.RegisterPublic("rewardhost", "testing-password", "Reward Host", maxed: false);
            var port = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery();
            await using var app = builder.Build();
            PlatformManagement.Map(app, accounts, port, FreePort());
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = true,
                    CookieContainer = new CookieContainer(),
                    UseProxy = false,
                };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                var page = await client.GetStringAsync("/manage");
                // Playtest B3 #11, 26 Sept 2026: with public registration off, the host creates friend
                // accounts on local /register and no public link is offered.
                TestAssert.True(page.Contains("Public registration is off", StringComparison.Ordinal) &&
                    page.Contains("href=\"/register\"", StringComparison.Ordinal) &&
                    !page.Contains("https://", StringComparison.OrdinalIgnoreCase),
                    "an unconfigured public registration URL must leave only local registration on /manage");
                var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                TestAssert.True(field.Success, "management page must expose an antiforgery form field");
                var fieldName = field.Groups[1].Value;
                var fieldValue = WebUtility.HtmlDecode(field.Groups[2].Value);
                var requestId = Guid.NewGuid().ToString("D");

                using var remotePage = new HttpRequestMessage(HttpMethod.Get, "/manage");
                remotePage.Headers.Host = "public.example";
                using var remotePageResponse = await client.SendAsync(remotePage);
                TestAssert.Equal(HttpStatusCode.NotFound, remotePageResponse.StatusCode,
                    "remote-host requests cannot list operator reward codes");

                using var forged = await client.PostAsync("/manage/reward-code/create", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["ip"] = "321", ["rp"] = "654", ["requestId"] = requestId }));
                TestAssert.False(forged.IsSuccessStatusCode, "reward-code issuance requires antiforgery");
                TestAssert.Equal(0, accounts.ListRewardCodes().Length);

                using var remoteRequest = new HttpRequestMessage(HttpMethod.Post, "/manage/reward-code/create")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        [fieldName] = fieldValue, ["ip"] = "321", ["rp"] = "654", ["requestId"] = Guid.NewGuid().ToString("D"),
                    }),
                };
                remoteRequest.Headers.Host = "public.example";
                using var remoteResponse = await client.SendAsync(remoteRequest);
                TestAssert.Equal(HttpStatusCode.NotFound, remoteResponse.StatusCode,
                    "remote-host requests cannot issue operator reward codes");

                using var issuedResponse = await client.PostAsync("/manage/reward-code/create", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        [fieldName] = fieldValue, ["ip"] = "321", ["rp"] = "654", ["requestId"] = requestId,
                    }));
                TestAssert.Equal(HttpStatusCode.OK, issuedResponse.StatusCode);
                TestAssert.True(issuedResponse.Headers.CacheControl?.NoStore == true,
                    "the raw-code issuance response must not be cached");
                var issuedHtml = await issuedResponse.Content.ReadAsStringAsync();
                var rawCodeMatch = Regex.Match(issuedHtml, "readonly value=\"([^\"]+)\"");
                TestAssert.True(rawCodeMatch.Success, "issuance response must show the raw code for copying");
                var rawCode = WebUtility.HtmlDecode(rawCodeMatch.Groups[1].Value);
                TestAssert.Equal(1, accounts.ListRewardCodes().Length);
                TestAssert.False(issuedHtml.Contains(accounts.ListRewardCodes()[0].CodeHash, StringComparison.OrdinalIgnoreCase),
                    "issuance response must not expose the stored hash");

                page = await client.GetStringAsync("/manage");
                TestAssert.False(page.Contains(rawCode, StringComparison.Ordinal),
                    "the raw code is not repeated in the management list");
                TestAssert.True(page.Contains("Unused", StringComparison.Ordinal));
                using var remoteRevoke = new HttpRequestMessage(HttpMethod.Post, "/manage/reward-code/revoke")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        [fieldName] = fieldValue, ["id"] = accounts.ListRewardCodes()[0].Id.ToString("D"),
                    }),
                };
                remoteRevoke.Headers.Host = "public.example";
                using var remoteRevokeResponse = await client.SendAsync(remoteRevoke);
                TestAssert.Equal(HttpStatusCode.NotFound, remoteRevokeResponse.StatusCode,
                    "remote-host requests cannot revoke operator reward codes");
                TestAssert.False(accounts.ListRewardCodes()[0].Revoked);
                var revokeField = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                TestAssert.True(revokeField.Success);
                using var revoked = await client.PostAsync("/manage/reward-code/revoke", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        [revokeField.Groups[1].Value] = WebUtility.HtmlDecode(revokeField.Groups[2].Value),
                        ["id"] = accounts.ListRewardCodes()[0].Id.ToString("D"),
                    }));
                TestAssert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
                TestAssert.True(accounts.ListRewardCodes()[0].Revoked);
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            store?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RetiredInvitationRoutesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-management-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(root);
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        const string publicRegistrationUrl = "https://127.0.0.1/register";
        PlatformManagement.Map(app, accounts, port, 8391, null, publicRegistrationUrl);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var html = await client.GetStringAsync("/manage");
            TestAssert.True(html.Contains(publicRegistrationUrl, StringComparison.Ordinal),
                "the management page must publish the configured trusted registration URL");
            var field = Regex.Match(html, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
            TestAssert.True(field.Success);
            var form = new Dictionary<string, string> { [field.Groups[1].Value] = WebUtility.HtmlDecode(field.Groups[2].Value),
                ["name"] = "Invited friend", ["host"] = "127.0.0.1" };
            // Invitations and guest revocation are retired (25 October 2026): these routes are not served, even with a valid form token.
            foreach (var route in new[] { "/manage/invite", "/manage/register", "/manage/revoke" })
            {
                using var retired = await client.PostAsync(route, new FormUrlEncodedContent(form));
                TestAssert.Equal(HttpStatusCode.NotFound, retired.StatusCode, route + " must not be served");
            }
            TestAssert.False(html.Contains("/manage/invite", StringComparison.Ordinal), "the management page links no invitation");
        }
        finally { await app.StopAsync(); }
    }

    [Check("server.air-management-local-boundary")]
    public static void ManagementRequiresTheLocalListenerAndLoopbackHost()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 8390;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("127.0.0.1", 8390);
        TestAssert.True(PlatformManagement.IsLocal(context, 8390));
        context.Connection.LocalPort = 8391;
        TestAssert.False(PlatformManagement.IsLocal(context, 8390), "a remote tunnel must never grant operator access");
        context.Connection.LocalPort = 8390;
        context.Request.Host = new HostString("attacker.example", 8390);
        TestAssert.False(PlatformManagement.IsLocal(context, 8390), "reject DNS-rebound operator requests");
        context.Request.Host = new HostString("localhost", 8390);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
        TestAssert.False(PlatformManagement.IsLocal(context, 8390));
    }


    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RegistrationForwardingIsLimitedToTheConfiguredLoopbackProxyAsync()
    {
        var settings = new PlatformSettings("data", "server", PublicHost: "public.example",
            PublicRegistrationUrl: "https://public.example/register");
        settings.ValidatePublicRegistrationUrl();
        TestAssert.Throws<ArgumentException>(() => new PlatformSettings("data", "server",
            PublicHost: "public.example", PublicRegistrationUrl: "https://other.example/register")
            .ValidatePublicRegistrationUrl());
        TestAssert.Throws<ArgumentException>(() => new PlatformSettings("data", "server",
            PublicHost: "public.example", PublicRegistrationUrl: "https://public.example/register?x=1")
            .ValidatePublicRegistrationUrl());
        // Playtest B3 #11: a cleared platform.json value turns public registration off instead of failing startup.
        var cleared = JsonSerializer.Deserialize<PlatformSettings>(
            """{"dataRoot":"data","serverExecutable":"server","publicHost":"public.example","publicRegistrationUrl":""}""",
            HostFiles.Json)!;
        cleared.ValidatePublicRegistrationUrl();
        TestAssert.True(cleared.PublicRegistrationUrl is null, "an empty PublicRegistrationUrl must mean public registration is off");

        var publicUrl = new Uri(settings.PublicRegistrationUrl!);
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 8390;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString(publicUrl.Host, publicUrl.Port);
        context.Request.Path = "/register";
        TestAssert.True(Amberline.Platform.Program.IsRegistrationProxyRequest(context, 8390, publicUrl));

        context.Connection.LocalPort = 8391;
        TestAssert.False(Amberline.Platform.Program.IsRegistrationProxyRequest(context, 8390, publicUrl),
            "the pinned public listener must not consume forwarded headers");
        context.Connection.LocalPort = 8390;
        context.Request.Path = "/manage";
        TestAssert.False(Amberline.Platform.Program.IsRegistrationProxyRequest(context, 8390, publicUrl),
            "management paths must never enter the registration proxy realm");
        context.Request.Path = "/register";
        context.Request.Host = new HostString("127.0.0.1", 8390);
        TestAssert.False(Amberline.Platform.Program.IsRegistrationProxyRequest(context, 8390, publicUrl),
            "the local operator host must not consume public forwarding headers");
        context.Request.Host = new HostString(publicUrl.Host, publicUrl.Port);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        TestAssert.False(Amberline.Platform.Program.IsRegistrationProxyRequest(context, 8390, publicUrl),
            "a non-loopback peer cannot claim to be the trusted proxy");

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        settings = settings with { HttpPort = port };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        await using var app = builder.Build();
        Amberline.Platform.Program.UseRegistrationProxy(app, settings);
        app.MapGet("/register", (HttpContext request) => Results.Json(new
        {
            scheme = request.Request.Scheme,
            remote = request.Connection.RemoteIpAddress?.ToString(),
        }));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var proxied = new HttpRequestMessage(HttpMethod.Get, "/register");
            proxied.Headers.Host = publicUrl.Host;
            proxied.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.10");
            proxied.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(proxied);
            response.EnsureSuccessStatusCode();
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            TestAssert.Equal("https", body.GetProperty("scheme").GetString());
            TestAssert.Equal("198.51.100.10", body.GetProperty("remote").GetString());

            using var forgedLocal = new HttpRequestMessage(HttpMethod.Get, "/register");
            forgedLocal.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.99");
            forgedLocal.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            using var localResponse = await client.SendAsync(forgedLocal);
            localResponse.EnsureSuccessStatusCode();
            var localBody = JsonSerializer.Deserialize<JsonElement>(await localResponse.Content.ReadAsStringAsync());
            TestAssert.Equal("http", localBody.GetProperty("scheme").GetString());
            TestAssert.Equal("127.0.0.1", localBody.GetProperty("remote").GetString());

            using var chain = new HttpRequestMessage(HttpMethod.Get, "/register");
            chain.Headers.Host = publicUrl.Host;
            chain.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99, 198.51.100.20");
            chain.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http, https");
            using var chainResponse = await client.SendAsync(chain);
            chainResponse.EnsureSuccessStatusCode();
            var chainBody = JsonSerializer.Deserialize<JsonElement>(await chainResponse.Content.ReadAsStringAsync());
            TestAssert.Equal("https", chainBody.GetProperty("scheme").GetString());
            TestAssert.Equal("198.51.100.20", chainBody.GetProperty("remote").GetString());
        }
        finally { await app.StopAsync(); }
    }
}
