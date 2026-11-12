using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Who may use the home host's page: the launch code and its session (another Windows user), the origin gate (another website in
/// the host's browser), DNS rebinding and the public port. Each check runs a home loop in a temporary folder on free loopback
/// ports; the "browser" is an HTTP client, so no browser starts and nothing is written outside the temporary folder.
/// </summary>
public static class HostSessionChecks
{
    private const string FromIcon = "Open Amberline from its desktop icon";
    private const string Expired = "This link has expired";

    [Check("server.host-launch-code-session", TimeoutSeconds = 120)]
    public static void TheLaunchCodeSignsTheHostInOnceAndAnotherUsersCodeFails() => LaunchCodeAsync().GetAwaiter().GetResult();

    private static async Task LaunchCodeAsync()
    {
        var home = Home.Create("league-host-session");
        var keyFile = HostSessions.KeyFile(home.Loop.Paths.Data);
        TestAssert.Equal(Path.Combine(home.Loop.Paths.Data, "host", "page.key"), keyFile);
        var key = File.ReadAllBytes(keyFile);
        TestAssert.Equal(32, key.Length, "the launch-code key is 32 random bytes in the data folder");
        string session;
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var stranger = home.Client();
            await WaitForHealthAsync(stranger, "setup", run);
            using (var page = await stranger.GetAsync("/setup"))
            {
                TestAssert.Equal(HttpStatusCode.Forbidden, page.StatusCode, "no session, no setup page");
                var text = await page.Content.ReadAsStringAsync();
                TestAssert.True(text.Contains(FromIcon, StringComparison.Ordinal) && !text.Contains("<label for=paste>", StringComparison.Ordinal), text);
            }
            using (var post = await stranger.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
                       { ["game"] = home.Game, ["air"] = home.Air })))
                TestAssert.Equal(HttpStatusCode.Forbidden, post.StatusCode, "no session, no form");
            await ExpectRefusedAsync(stranger, "/open", FromIcon);
            await ExpectRefusedAsync(stranger, "/open?c=12345.abc", FromIcon);

            using (var first = home.Client()) await home.SignInAsync(first);
            var opened = home.Opened[0];
            TestAssert.True(Regex.IsMatch(opened, "^" + Regex.Escape(home.OpenPrefix) + @"\d+\.[A-Za-z0-9_-]{43}$"), opened);
            TestAssert.True(Regex.IsMatch(home.Screen.ToString(), Regex.Escape(home.OpenPrefix) + @"\d+\.[A-Za-z0-9_-]{43}"),
                "the window shows a link with a code of its own, for a browser that did not open");
            // SignInAsync used the opened code; this browser gets one of its own.
            using var browser = home.Client();
            using (var open = await browser.GetAsync("/open?c=" + home.Loop.Sessions.Code(DateTimeOffset.UtcNow)))
            {
                TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode);
                // Since H6 the landing is the first-run steps (HostSteps.Start).
                TestAssert.Equal(HostSteps.Start, open.Headers.Location?.OriginalString, "the code leaves the address bar");
                var set = open.Headers.TryGetValues("Set-Cookie", out var values)
                    ? values.SingleOrDefault(value => value.StartsWith(HostSessions.CookieName + "=", StringComparison.Ordinal)) ?? "" : "";
                session = set.Split(';')[0][Math.Min(set.Length, HostSessions.CookieName.Length + 1)..];
                TestAssert.Equal(43, session.Length, "the session is 32 random bytes: " + set);
                var attributes = set.ToLowerInvariant();
                TestAssert.True(attributes.Contains("httponly", StringComparison.Ordinal) &&
                    attributes.Contains("samesite=strict", StringComparison.Ordinal) && attributes.Contains("path=/", StringComparison.Ordinal), set);
                TestAssert.False(attributes.Contains("expires", StringComparison.Ordinal) || attributes.Contains("max-age", StringComparison.Ordinal),
                    "the session cookie has no expiry date: " + set);
            }
            TestAssert.True((await browser.GetStringAsync("/setup")).Contains("<label for=paste>", StringComparison.Ordinal),
                "the signed-in browser sees the setup page");
            using (var again = await browser.GetAsync("/open"))
                TestAssert.Equal(HostSteps.Start, again.Headers.Location?.OriginalString, "a signed-in browser needs no new code");

            // Once only, within 2 minutes, and only from this user's key.
            await ExpectRefusedAsync(stranger, opened, Expired);
            var now = DateTimeOffset.UtcNow;
            await ExpectRefusedAsync(stranger, "/open?c=" + home.Loop.Sessions.Code(now - HostSessions.CodeLifetime - TimeSpan.FromSeconds(1)), Expired);
            await ExpectRefusedAsync(stranger, "/open?c=" + home.Loop.Sessions.Code(now + TimeSpan.FromMinutes(1)), Expired);
            // Another Windows user's Amberline signs with the key in that user's own profile.
            var otherUser = new HostSessions(HostSessions.KeyFile(Path.Combine(home.Root, "other-user")));
            await ExpectRefusedAsync(stranger, "/open?c=" + otherUser.Code(now), "(A-RUN-02)");
            var forged = home.Loop.Sessions.Code(now);
            await ExpectRefusedAsync(stranger, "/open?c=" + forged[..^2] + (forged[^2] == 'A' ? "B" : "A") + forged[^1], "(A-RUN-02)");
            using (var guessed = new HttpRequestMessage(HttpMethod.Get, "/setup"))
            {
                guessed.Headers.Add("Cookie", $"{HostSessions.CookieName}={new string('A', 43)}");
                TestAssert.Equal(HttpStatusCode.Forbidden, (await stranger.SendAsync(guessed)).StatusCode, "a made-up session is refused");
            }
            using (var still = await stranger.GetAsync("/setup"))
                TestAssert.Equal(HttpStatusCode.Forbidden, still.StatusCode, "a refused code signs nobody in");

            home.Loop.Restart();
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(stranger, "setup", run);
            using (var after = await browser.GetAsync("/setup"))
                TestAssert.Equal(HttpStatusCode.OK, after.StatusCode, "a Restart keeps the host signed in");
            TestAssert.Equal(1, home.Opened.Count, "a Restart opens no second page");
        }
        finally { await home.StopAsync(run); }

        // The next process keeps the key file but none of the sessions: the old cookie is refused, its own code signs in.
        var next = home.Again();
        TestAssert.True(File.ReadAllBytes(keyFile).AsSpan().SequenceEqual(key), "the key is made once and kept");
        var rerun = Task.Run(() => HostMode.RunAsync(next.Loop));
        try
        {
            using var client = next.Client();
            await WaitForHealthAsync(client, "setup", rerun);
            using (var old = new HttpRequestMessage(HttpMethod.Get, "/setup"))
            {
                old.Headers.Add("Cookie", $"{HostSessions.CookieName}={session}");
                TestAssert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(old)).StatusCode, "a new process forgets the old sessions");
            }
            await next.SignInAsync(client);
            using var setup = await client.GetAsync("/setup");
            TestAssert.Equal(HttpStatusCode.OK, setup.StatusCode, "the new process's own code signs in");
        }
        finally { await next.StopAsync(rerun); }
        Delete(home.Root);
    }

    [Check("server.host-reopen-lands-on-allowed-page", TimeoutSeconds = 120)]
    public static void TheRestartLandsOnlyOnTheHostsOwnAllowedPage() => ReopenLandingAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The reopen after a restart names its landing with <c>to</c> (batch 649): a key of the host's own list lands on its page; anything
    /// else (no key, an unknown one, a path, an absolute address, <c>//host</c>) lands on the first-run steps. Each code opens the page
    /// once, so every request brings a code of its own, made a second apart.
    /// </summary>
    private static async Task ReopenLandingAsync()
    {
        var home = Home.Create("league-host-landing");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using (var probe = home.Client()) await WaitForHealthAsync(probe, "setup", run);
            var made = DateTimeOffset.UtcNow;
            var cases = new (string? To, string Landing)[]
            {
                ("settings", HostSettingsPage.PagePath + "?saved=folders"),
                (null, HostSteps.Start),
                ("", HostSteps.Start),
                ("bogus", HostSteps.Start),
                (HostSettingsPage.PagePath, HostSteps.Start),
                ("https://example.test/", HostSteps.Start),
                ("//example.test", HostSteps.Start),
            };
            for (var i = 0; i < cases.Length; i++)
            {
                var (to, landing) = cases[i];
                var label = to is null ? "(none)" : "\"" + to + "\"";
                using var browser = home.Client();
                using var answer = await browser.GetAsync("/open?c=" + home.Loop.Sessions.Code(made - TimeSpan.FromSeconds(i + 1)) + (to is null ? "" : "&to=" + to));
                TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, "to=" + label);
                TestAssert.Equal(landing, answer.Headers.Location?.OriginalString, "to=" + label + " lands on");
            }
        }
        finally { await home.StopAsync(run); }
        Delete(home.Root);
    }

    [Check("server.host-rebinding-and-origin-refused", TimeoutSeconds = 120)]
    public static void ARebindingOrCrossSitePageCannotUseTheHostPage() => RebindingAndOriginAsync().GetAwaiter().GetResult();

    private static async Task RebindingAndOriginAsync()
    {
        var home = Home.Create("league-host-origin");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "setup", run);
            await home.SignInAsync(browser);
            var token = Token(await browser.GetStringAsync("/setup"));
            var own = $"http://127.0.0.1:{home.Port}";
            // A form posted by another site: the session and even a valid form token are present here, the origin alone refuses it.
            foreach (var (origin, site, why) in new (string?, string?, string)[]
                     {
                         ("http://attacker.example", null, "another website"),
                         ("null", null, "an opaque origin (a sandboxed frame or a local file)"),
                         ($"http://127.0.0.1:{home.RemotePort}", null, "a page of another program on this PC"),
                         ($"http://localhost:{home.Port}", null, "the page under another host name"),
                         (null, "cross-site", "Sec-Fetch-Site cross-site"),
                         (null, "same-site", "Sec-Fetch-Site same-site (another port on this PC)"),
                         (own, "cross-site", "its own Origin with a cross-site fetch"),
                     })
            {
                using var refused = await browser.SendAsync(SetupPost(token, home, origin, site));
                TestAssert.Equal(HttpStatusCode.Forbidden, refused.StatusCode, why);
                TestAssert.True((await refused.Content.ReadAsStringAsync()).Contains("It came from another website", StringComparison.Ordinal), why);
            }
            // The page itself, the browser's own navigation and a program without browser headers pass the gate and reach the form.
            foreach (var (origin, site) in new (string?, string?)[] { (own, "same-origin"), (null, "none"), (null, null) })
            {
                using var passed = await browser.SendAsync(SetupPost(token, home, origin, site));
                TestAssert.True(passed.StatusCode == HttpStatusCode.BadRequest &&
                    (await passed.Content.ReadAsStringAsync()).Contains("Amberline cannot find that game folder.", StringComparison.Ordinal),
                    $"Origin {origin}, Sec-Fetch-Site {site}: {passed.StatusCode}");
            }
            using (var preflight = new HttpRequestMessage(HttpMethod.Options, "/setup"))
            {
                preflight.Headers.Add("Origin", "http://attacker.example");
                preflight.Headers.Add("Access-Control-Request-Method", "POST");
                using var answer = await browser.SendAsync(preflight);
                TestAssert.Equal(HttpStatusCode.Forbidden, answer.StatusCode, "a cross-site preflight is refused");
                TestAssert.False(answer.Headers.Concat(answer.Content.Headers).Any(header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)),
                    "no CORS header is ever sent");
            }

            // DNS rebinding: a name the attacker controls points at 127.0.0.1, so the browser sends that name as Host. Even with the
            // host's session (a real browser would not send it to that name) every host route is 404, and a code is not used up.
            var code = home.Loop.Sessions.Code(DateTimeOffset.UtcNow);
            foreach (var name in new[] { "attacker.example", $"attacker.example:{home.Port}", "127.0.0.1.attacker.example", "localhost.attacker.example" })
            foreach (var path in new[] { "/setup", "/open?c=" + code, "/open", "/manage", "/host/page" })
            {
                using var rebound = new HttpRequestMessage(HttpMethod.Get, path);
                rebound.Headers.Host = name;
                using var answer = await browser.SendAsync(rebound);
                TestAssert.Equal(HttpStatusCode.NotFound, answer.StatusCode, name + path);
                TestAssert.False(answer.Headers.Contains("Set-Cookie"), "a rebound page gets no cookie");
            }
            using (var reboundPost = SetupPost(token, home, null, null))
            {
                reboundPost.Headers.Host = "attacker.example";
                TestAssert.Equal(HttpStatusCode.NotFound, (await browser.SendAsync(reboundPost)).StatusCode, "a rebound form post");
            }
            using (var stranger = home.Client())
            using (var open = await stranger.GetAsync("/open?c=" + code))
                TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "the rebound requests did not use up the code");
        }
        finally { await home.StopAsync(run); }
        Delete(home.Root);
    }

    [Check("server.host-routes-never-public", TimeoutSeconds = 300)]
    public static void EveryHostRouteStaysOffThePublicPortAndBehindTheGate() => RoutesNeverPublicAsync().GetAwaiter().GetResult();

    private static async Task RoutesNeverPublicAsync()
    {
        foreach (var path in new[] { "/open", "/setup", "/manage", "/manage/grant", "/register", "/host", "/host/play-file" })
        {
            TestAssert.True(HostGate.IsHostPath(path), path + " is a host route");
            TestAssert.False(AirRemoteTunnel.IsPublicPath(path), path + " is never on the public allow-list");
        }
        TestAssert.False(HostGate.IsHostPath("/"), "/ is the AIR client's public entry, not a host page");

        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var catalogLoaded = ChampionCatalogLoaded();
        var home = Home.Create("league-host-routes");
        // Both folders chosen, so the loop starts the full Platform at once (its public HTTPS port on loopback in this check).
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort,
            bindAddress = "127.0.0.1", publicHost = "127.0.0.1", dataRoot = home.Game, airRoot = home.Air,
        }));
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            using var stranger = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var routes = home.Loop.Routes().OfType<RouteEndpoint>().Select(endpoint => (
                Path: Regex.Replace(endpoint.RoutePattern.RawText ?? "", @"\{[^}]*\}", "x"),
                Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])).ToArray();
            var hostRoutes = routes.Where(route => HostGate.IsHostPath(route.Path)).ToArray();
            foreach (var expected in new[] { "/open", "/manage", "/manage/online.json", "/manage/grant", "/manage/password", "/manage/broadcast.json", "/register",
                         "/host", "/host/status.json", "/host/page.js", "/host/stop", "/host/restart", "/host/firewall", "/host/network",
                         "/host/invite", "/host/play-file", "/host/friends", "/host/users", "/host/users/add", "/host/users/password", "/host/users/disable",
                         "/host/users/enable", "/host/users/delete",
                         HostSettingsPage.PagePath, HostSettingsPage.NamePath, HostSettingsPage.AddressPath, HostSettingsPage.FoldersPath, HostSettingsPage.UninstallPath })
                TestAssert.True(hostRoutes.Any(route => route.Path == expected), expected + " is one of the routes walked");

            // The public port: every route that is not on the allow-list is 404, the host's session cookie notwithstanding.
            // Allowed routes are not requested (the store would read the empty AIR folder of this check).
            foreach (var (path, methods) in routes)
            {
                if (AirRemoteTunnel.IsPublicPath(path)) { TestAssert.False(HostGate.IsHostPath(path), path + " is public and a host route"); continue; }
                foreach (var method in methods)
                {
                    using var remote = await PacedAsync(browser, () => Request(method, $"https://127.0.0.1:{home.RemotePort}{path}"));
                    TestAssert.Equal(HttpStatusCode.NotFound, remote.StatusCode, $"{method} {path} on the public port");
                }
            }
            // Loopback: every host route refuses a rebound name, a browser without the session and (for a form) another site.
            foreach (var (path, methods) in hostRoutes)
            foreach (var method in methods)
            {
                using (var rebound = await PacedAsync(browser, () =>
                       {
                           var request = Request(method, path);
                           request.Headers.Host = "attacker.example";
                           return request;
                       }))
                    TestAssert.Equal(HttpStatusCode.NotFound, rebound.StatusCode, $"{method} {path} under a rebound name");
                using (var unsigned = await PacedAsync(stranger, () => Request(method, path)))
                {
                    TestAssert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode, $"{method} {path} without the session");
                    TestAssert.True((await unsigned.Content.ReadAsStringAsync()).Contains(FromIcon, StringComparison.Ordinal), $"{method} {path}");
                }
                if (method == "GET") continue;
                using var crossSite = await PacedAsync(browser, () =>
                {
                    var request = Request(method, path);
                    request.Headers.TryAddWithoutValidation("Origin", "http://attacker.example");
                    return request;
                });
                TestAssert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode, $"{method} {path} from another site");
            }
            using (var manage = await browser.GetAsync("/manage"))
                TestAssert.Equal(HttpStatusCode.OK, manage.StatusCode, "the gate lets the signed-in host through");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(catalogLoaded, ChampionCatalogLoaded(), "the full Platform's start leaves the champion list unloaded");
        Delete(home.Root);
    }

    private static async Task ExpectRefusedAsync(HttpClient client, string url, string text)
    {
        using var answer = await client.GetAsync(url);
        TestAssert.Equal(HttpStatusCode.Forbidden, answer.StatusCode, url);
        TestAssert.True((await answer.Content.ReadAsStringAsync()).Contains(text, StringComparison.Ordinal), url + " says: " + text);
        TestAssert.False(answer.Headers.Contains("Set-Cookie"), url + " sets no cookie");
    }

    private static HttpRequestMessage SetupPost(KeyValuePair<string, string> token, Home home, string? origin, string? site)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/setup")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
                { [token.Key] = token.Value, ["game"] = Path.Combine(home.Root, "nowhere"), ["air"] = home.Air }),
        };
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (site is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        return request;
    }

    internal static HttpRequestMessage Request(string method, string url) =>
        new(new HttpMethod(method), url) { Content = method == "GET" ? null : new FormUrlEncodedContent([]) };

    /// <summary>
    /// One request of the walk. The Platform holds each address to 180 requests a minute (<c>Program.RequestPartition</c>), and the walk
    /// sends more than that from loopback once there are enough host routes (batch 641's Settings tipped it): a 429 waits for the next
    /// minute's window, asking again every 5 seconds.
    /// </summary>
    internal static async Task<HttpResponseMessage> PacedAsync(HttpClient client, Func<HttpRequestMessage> request)
    {
        for (var tries = 1; ; tries++)
        {
            using var message = request();
            var answer = await client.SendAsync(message);
            if (answer.StatusCode != HttpStatusCode.TooManyRequests || tries == 15) return answer;
            answer.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The host's browser, also on the public port, whose certificate is this temporary host's own.</summary>
    internal static HttpClient Browser(Home home) => new(PageDump.Wrap(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })) { BaseAddress = new Uri($"http://127.0.0.1:{home.Port}"), Timeout = TimeSpan.FromSeconds(10) };
}
