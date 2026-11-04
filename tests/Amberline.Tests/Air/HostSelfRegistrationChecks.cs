using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Amberline.Tests.Air.HostInviteChecks;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 656: people register themselves at <c>https://&lt;address&gt;:&lt;friends' port&gt;/register</c> while the host's Settings switch is
/// on. Temporary folders, free loopback ports (the friends' port on loopback, as in the other host checks), made-up logins
/// (<c>host_one</c>, <c>self1</c>, <c>cap_01</c>...) and the reserved name amberline.example.test; no browser, router or client.
/// </summary>
public static class HostSelfRegistrationChecks
{
    private const string TestHost = "amberline.example.test";
    private const string HostPassword = "Synthetic-host-Pw-11";
    private const string Password = "Synthetic-self-Pw-77";
    private const string Switch = "/host/settings/register";
    private const string Warning = "Their browser warns that the connection is not private. They choose Advanced, then Continue.";
    private static readonly string[] SignUpPaths = ["/register", "/register/Play.cmd", "/register/style.css"];
    /// <summary>The sign-up page's stylesheet: the host pages' sheet at an address of the sign-up's own, with the same version.</summary>
    private static readonly string Style = "/register/style.css" + HostPages.StyleHref[HostPages.StylePath.Length..];

    [Check("server.host-self-registration", TimeoutSeconds = 300)]
    public static void PeopleRegisterThemselvesOnTheFriendsPortOnlyWhileTheSwitchIsOn() => SelfRegistrationAsync().GetAwaiter().GetResult();

    private static async Task SelfRegistrationAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list: read it the suite's way first (see HostInviteChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-self-registration");
        Directory.CreateDirectory(home.Loop.Paths.App);
        File.WriteAllText(Path.Combine(home.Loop.Paths.App, HostInvite.TemplateFile),
            File.ReadAllText(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", HostInvite.TemplateFile))));
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_one", HostPassword, "Host One").Id;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        var link = $"https://{TestHost}:{home.RemotePort}/register";
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using var person = Person(home, IPAddress.Loopback);

            // Off, as on every existing install: no sign-up on the friends' port, the switch off in Settings, no link on Invite.
            await NotFoundAsync(person, "with the switch off");
            var settings = await browser.GetStringAsync(HostSettingsPage.PagePath);
            TestAssert.True(settings.Contains("Let people register themselves", StringComparison.Ordinal) &&
                !settings.Contains("name=on value=on checked", StringComparison.Ordinal) && !settings.Contains(link, StringComparison.Ordinal), settings);
            TestAssert.False((await browser.GetStringAsync(HostInvite.InvitePath)).Contains(link, StringComparison.Ordinal), "Invite has no link while it is off");

            // On: saved in platform.json; Settings and Invite show the link, its Copy and the line on the browser's warning.
            await PostAsync(browser, Switch, Token(settings), HttpStatusCode.Redirect, ("on", "on"));
            settings = await browser.GetStringAsync(HostSettingsPage.PagePath + "?saved=register");
            TestAssert.True(settings.Contains("<p class=notice role=status>Saved.", StringComparison.Ordinal) &&
                settings.Contains("name=on value=on checked", StringComparison.Ordinal) && settings.Contains($">{link}</pre>", StringComparison.Ordinal) &&
                settings.Contains("data-copy=register-link>Copy</button>", StringComparison.Ordinal) && settings.Contains(Warning, StringComparison.Ordinal), settings);
            var invite = await browser.GetStringAsync(HostInvite.InvitePath);
            TestAssert.True(invite.Contains($">{link}</pre>", StringComparison.Ordinal) && invite.Contains(Warning, StringComparison.Ordinal), invite);
            using (var file = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
                TestAssert.True(file.RootElement.GetProperty("selfRegistration").GetBoolean(), "the switch is saved in platform.json");

            // The page: the server's name, the four fields, the host pages' look and policy; no host menu, no /host link, no script.
            string form;
            using (var answer = await person.GetAsync("/register"))
            {
                form = await answer.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, form);
                var policy = string.Join(";", answer.Headers.GetValues("Content-Security-Policy"));
                TestAssert.Equal(HostPages.Policy, policy, "the host pages' policy: script-src 'self', nothing inline");
                Public(form, "GET /register", policy);
            }
            TestAssert.True(form.Contains("<h1>Create your login for Test Server</h1>", StringComparison.Ordinal) &&
                form.Contains("name=username", StringComparison.Ordinal) && form.Contains("name=summoner", StringComparison.Ordinal) &&
                form.Contains("name=password type=password", StringComparison.Ordinal) && form.Contains("name=mode value=maxed checked", StringComparison.Ordinal) &&
                form.Contains("name=mode value=fresh>", StringComparison.Ordinal) && form.Contains("<form method=post action=/register novalidate>", StringComparison.Ordinal), form);
            using (var sheet = await person.GetAsync(Style))
            {
                TestAssert.Equal(HttpStatusCode.OK, sheet.StatusCode, "the sign-up page's stylesheet");
                TestAssert.Equal("text/css", sheet.Content.Headers.ContentType?.MediaType);
                TestAssert.Equal(await browser.GetStringAsync(HostPages.StyleHref), await sheet.Content.ReadAsStringAsync(), "the host pages' own stylesheet");
            }

            // Every problem at once (batch 650's list), each beside its field; a name already taken; an expired page. Never the password back.
            var refused = await PostAsync(person, "/register", Token(form), HttpStatusCode.BadRequest,
                ("username", "x!"), ("summoner", ""), ("password", "Short-1"), ("mode", "level99"));
            TestAssert.True(refused.Contains("<a href=#username>Choose a username of 3 to 24", StringComparison.Ordinal) &&
                refused.Contains("<a href=#password>Choose a password of at least 10 characters.</a>", StringComparison.Ordinal) &&
                refused.Contains("<a href=#mode>Choose where your login starts.</a>", StringComparison.Ordinal) &&
                refused.Contains("id=username-error", StringComparison.Ordinal) && refused.Contains("id=password-error", StringComparison.Ordinal) &&
                refused.Contains("id=mode-error", StringComparison.Ordinal) && refused.Contains("value=\"x!\"", StringComparison.Ordinal), refused);
            TestAssert.False(refused.Contains("Short-1", StringComparison.Ordinal), "a refused form never shows the password back");
            Public(refused, "POST /register (problems)", HostPages.Policy);
            refused = await PostAsync(person, "/register", Token(refused), HttpStatusCode.BadRequest,
                ("username", "host_one"), ("summoner", "Host Two"), ("password", Password), ("mode", "maxed"));
            TestAssert.True(refused.Contains("<a href=#username>This username or summoner name is already taken.", StringComparison.Ordinal) &&
                !refused.Contains(Password, StringComparison.Ordinal), refused);
            refused = await PostAsync(person, "/register", null, HttpStatusCode.BadRequest, ("username", "self1"), ("password", Password), ("mode", "maxed"));
            TestAssert.True(refused.Contains("The page expired", StringComparison.Ordinal) && !refused.Contains(Password, StringComparison.Ordinal), refused);

            // A sign-up: the login is ready and logs in; the page offers the Play file and never shows the password.
            var ready = await PostAsync(person, "/register", Token(form), HttpStatusCode.OK,
                ("username", "Self1"), ("summoner", ""), ("password", Password), ("mode", "fresh"));
            TestAssert.True(ready.Contains("<h1>Your login is ready</h1>", StringComparison.Ordinal) && ready.Contains("<b>self1</b>", StringComparison.Ordinal) &&
                ready.Contains("<a class=button href=/register/Play.cmd download>Download the Play file</a>", StringComparison.Ordinal) &&
                !ready.Contains(Password, StringComparison.Ordinal), ready);
            Public(ready, "POST /register (ready)", HostPages.Policy);
            using (var login = JsonDocument.Parse(await LoginAsync(browser, "self1", Password, HttpStatusCode.OK)))
                TestAssert.Equal("Self1", login.RootElement.GetProperty("summoner").GetString(), "an empty summoner name is the username as typed");
            TestAssert.True((await browser.GetStringAsync(HostInvite.FriendsPath)).Contains("<b>self1</b>", StringComparison.Ordinal), "the new login is in Friends");

            // The Play file is Invite's own for this server.
            byte[] play;
            using (var download = await person.GetAsync("/register/Play.cmd"))
            {
                TestAssert.Equal(HttpStatusCode.OK, download.StatusCode, await download.Content.ReadAsStringAsync());
                TestAssert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
                TestAssert.True(download.Content.Headers.ContentDisposition?.ToString().Contains("filename=Play.cmd", StringComparison.Ordinal) == true, "named Play.cmd");
                play = await download.Content.ReadAsByteArrayAsync();
            }
            using (var invited = await browser.GetAsync(HostInvite.PlayFilePath))
            {
                var invites = await invited.Content.ReadAsByteArrayAsync();
                TestAssert.True(play.AsSpan().SequenceEqual(invites), "the same file Invite makes");
            }

            // With the switch on, nothing else of the host is on the friends' port: every route but the public allow-list and the three
            // sign-up paths is 404 there, the host's session cookie notwithstanding (as server.host-routes-never-public walks them off).
            var routes = home.Loop.Routes().OfType<RouteEndpoint>().Select(endpoint => (
                Path: Regex.Replace(endpoint.RoutePattern.RawText ?? "", @"\{[^}]*\}", "x"),
                Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])).ToArray();
            foreach (var expected in new[] { "/open", "/setup", "/manage", "/host", "/host/style.css", "/host/page.js", "/host/play-file", "/host/invite", Switch })
                TestAssert.True(routes.Any(route => route.Path == expected) || expected == "/setup", expected + " is one of the routes walked");
            foreach (var (path, methods) in routes.Append(("/setup", ["GET", "POST"])).Append(("/register/x", ["GET"])))
            {
                if (AirRemoteTunnel.IsPublicPath(path) || SignUpPaths.Contains(path)) continue;
                foreach (var method in methods)
                {
                    using var remote = await HostSessionChecks.PacedAsync(browser, () => HostSessionChecks.Request(method, $"https://127.0.0.1:{home.RemotePort}{path}"));
                    TestAssert.Equal(HttpStatusCode.NotFound, remote.StatusCode, $"{method} {path} on the friends' port with sign-ups on");
                }
            }

            // The local /register is still the operator's page, for the signed-in host only.
            var local = await browser.GetStringAsync("/register");
            TestAssert.True(local.Contains("<h1>Create your account</h1>", StringComparison.Ordinal) && !local.Contains("Create your login for", StringComparison.Ordinal), local);

            // Off again: the friends' port forgets the sign-up at once, without a restart.
            await PostAsync(browser, Switch, Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect);
            await NotFoundAsync(person, "with the switch off again");
            TestAssert.False((await browser.GetStringAsync(HostInvite.InvitePath)).Contains(link, StringComparison.Ordinal), "Invite drops the link");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        // Never logged: the password nowhere in plain text, the username not in the window or a log file.
        NeverStored(home.Loop.Paths.Data, home.Screen.ToString(), Password);
        TestAssert.False(home.Screen.ToString().Contains("self1", StringComparison.OrdinalIgnoreCase), "the window never names the new login");
        foreach (var log in Directory.EnumerateFiles(home.Root, "*.log", SearchOption.AllDirectories))
            TestAssert.False(File.ReadAllText(log).Contains("self1", StringComparison.OrdinalIgnoreCase), "no log names the new login: " + log);
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
            TestAssert.True(store.FindName("Self1")!.Economy is not null, "the chosen start: level 1");
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "signing up leaves the champion list as it was");
        Delete(home.Root);
    }

    /// <summary>
    /// The caps: 12 sign-ups an hour from one address (the <c>registration</c> limit) and 30 new logins an hour in all, so a flood
    /// from many addresses cannot fill the store. Four addresses of this PC's loopback stand in for four people. A refused sign-up is
    /// the plain page "Too many sign-ups", never a stack trace; the host's own Invite is not held to them.
    /// </summary>
    [Check("server.host-self-registration-caps", TimeoutSeconds = 300)]
    public static void SignUpsStopAtTwelveAnAddressAndThirtyAnHourInAll() => CapsAsync().GetAwaiter().GetResult();

    private static async Task CapsAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-self-registration-caps");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_one", HostPassword, "Host One").Id;
        // Saved on in platform.json: the start reads it.
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId, selfRegistration = true });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var attempt = 0;
            async Task<(HttpStatusCode Status, string Page, string Policy)> SignUpAsync(HttpClient person)
            {
                var token = Token(await person.GetStringAsync("/register"));
                attempt++;
                using var answer = await person.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
                    { [token.Key] = token.Value, ["username"] = $"cap_{attempt:D2}", ["password"] = Password, ["mode"] = "maxed" }));
                return (answer.StatusCode, await answer.Content.ReadAsStringAsync(),
                    answer.Headers.TryGetValues("Content-Security-Policy", out var policy) ? string.Join(";", policy) : "");
            }
            static void Made((HttpStatusCode Status, string Page, string Policy) answer, string why) =>
                TestAssert.True(answer.Status == HttpStatusCode.OK && answer.Page.Contains("<h1>Your login is ready</h1>", StringComparison.Ordinal), why + ": " + answer.Page);
            static void TooMany((HttpStatusCode Status, string Page, string Policy) answer, string why)
            {
                TestAssert.Equal(HttpStatusCode.TooManyRequests, answer.Status, why + ": " + answer.Page);
                TestAssert.True(answer.Page.StartsWith("<!doctype html>", StringComparison.Ordinal) && answer.Page.Contains("<h1>Too many sign-ups</h1>", StringComparison.Ordinal) &&
                    answer.Page.Contains("Try again in an hour.", StringComparison.Ordinal) && !answer.Page.Contains("Exception", StringComparison.Ordinal) &&
                    !answer.Page.Contains(" at Amberline", StringComparison.Ordinal) && !answer.Page.Contains(Password, StringComparison.Ordinal), why + ", in plain words: " + answer.Page);
                TestAssert.Equal(HostPages.Policy, answer.Policy, why + ": the host pages' policy");
                Public(answer.Page, why, answer.Policy);
            }

            using (var first = Person(home, IPAddress.Parse("127.0.0.2")))
            {
                for (var i = 1; i <= 12; i++) Made(await SignUpAsync(first), $"sign-up {i} of 12 from one address");
                TooMany(await SignUpAsync(first), "the 13th sign-up from one address in an hour");
            }
            using (var second = Person(home, IPAddress.Parse("127.0.0.3")))
                for (var i = 1; i <= 12; i++) Made(await SignUpAsync(second), $"sign-up {i} of 12 from a second address");
            using (var third = Person(home, IPAddress.Parse("127.0.0.4")))
            {
                for (var i = 1; i <= 6; i++) Made(await SignUpAsync(third), $"sign-up {24 + i} of 30 in the hour");
                TooMany(await SignUpAsync(third), "the 31st new login in an hour, from an address with 6 of its 12 left");
            }
            using (var fourth = Person(home, IPAddress.Parse("127.0.0.5")))
                TooMany(await SignUpAsync(fourth), "a new address once the hour's 30 are made");

            // The host's own Invite is not held to the friends' port's caps.
            await PostAsync(browser, HostInvite.InvitePath, Token(await browser.GetStringAsync(HostInvite.InvitePath)), HttpStatusCode.OK,
                ("username", "friend1"), ("mode", "maxed"));
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
        {
            var logins = store.ListAccounts();
            TestAssert.Equal(30, logins.Count(account => account.Username.StartsWith("cap_", StringComparison.Ordinal)), "30 sign-ups, no more");
            TestAssert.Equal(32, logins.Length, "the host, 30 sign-ups and one invited friend");
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "signing up leaves the champion list as it was");
        Delete(home.Root);
    }

    /// <summary>Every sign-up path answers 404 on the friends' port, for GET and POST.</summary>
    private static async Task NotFoundAsync(HttpClient person, string when)
    {
        foreach (var path in SignUpPaths)
        foreach (var method in new[] { "GET", "POST" })
        {
            using var answer = await person.SendAsync(HostSessionChecks.Request(method, path));
            TestAssert.Equal(HttpStatusCode.NotFound, answer.StatusCode, $"{method} {path} on the friends' port {when}");
        }
    }

    /// <summary>A sign-up page: the host pages' standard (batch 652) at its own stylesheet, and nothing of the host's: no menu, no /host link, no script.</summary>
    private static void Public(string page, string what, string policy)
    {
        HostPagesChecks.Accessible(page, what, policy, Style);
        TestAssert.False(page.Contains("<nav", StringComparison.Ordinal) || page.Contains("/host", StringComparison.Ordinal) || page.Contains("<script", StringComparison.Ordinal),
            $"{what}: no host menu, no link to a host page and no script: {page}");
    }

    /// <summary>
    /// A stranger's browser on the friends' port from <paramref name="from"/> (every 127.x address is this PC's loopback), which has
    /// clicked through the certificate warning: it takes Amberline's own certificate, the one the Play file pins.
    /// </summary>
    internal static HttpClient Person(Home home, IPAddress from)
    {
        var pin = Pin(home);
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
                { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == pin },
            ConnectCallback = async (context, cancel) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(from, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancel);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        }) { BaseAddress = new Uri($"https://127.0.0.1:{home.RemotePort}"), Timeout = TimeSpan.FromSeconds(30) };
    }
}
