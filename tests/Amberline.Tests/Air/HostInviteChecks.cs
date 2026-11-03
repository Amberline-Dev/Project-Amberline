using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's Invite a friend and Friends pages (batch 630). Temporary folders, free loopback ports, made-up logins
/// (<c>host_one</c>, <c>friend1</c>, <c>friend2</c>) and the reserved name amberline.example.test; no browser, router or client.
/// </summary>
public static class HostInviteChecks
{
    private const string TestHost = "amberline.example.test";
    private const string HostPassword = "Synthetic-host-Pw-11";
    private static readonly Regex Generated = new(@"^[a-z]+(?:-[a-z]+){3}-[0-9]{2}\z");

    [Check("server.host-invite-creates-login", TimeoutSeconds = 180)]
    public static void InviteMakesTheFriendsLoginAndShowsThePasswordOnce() => InviteAsync().GetAwaiter().GetResult();

    private static async Task InviteAsync()
    {
        // The generator: four common words and two digits, always within the registration rule, and seldom the same twice.
        var made = Enumerable.Range(0, 200).Select(_ => HostInvite.NewPassword()).ToArray();
        TestAssert.True(made.All(password => Generated.IsMatch(password) && password.Length is >= 10 and <= 128), string.Join(" ", made.Take(5)));
        TestAssert.True(made.Distinct().Count() >= 199, "generated passwords repeat");

        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list (RegisterPublic). Read it the suite's way first, never from this
        // check's stand-in AIR folder (the Platform points AIR_CLIENT_ROOT there): its one-byte list would fail every later check.
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-invite");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_one", HostPassword, "Host One").Id;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        const string typed = "Typed-pass-2048";
        string generated;
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var dashboard = await browser.GetStringAsync(HostDashboard.PagePath);
            TestAssert.True(dashboard.Contains("href=/host/invite>Invite a friend", StringComparison.Ordinal) &&
                dashboard.Contains("href=/host/friends>Friends", StringComparison.Ordinal) && dashboard.Contains("href=/manage>Advanced", StringComparison.Ordinal),
                "the dashboard links Invite a friend, Friends and Advanced");
            using (var stranger = home.Client())
            using (var refused = await stranger.GetAsync(HostInvite.InvitePath))
                TestAssert.Equal(HttpStatusCode.Forbidden, refused.StatusCode, "the page needs the host's session");

            using (var form = await browser.GetAsync(HostInvite.InvitePath))
            {
                var page = await form.Content.ReadAsStringAsync();
                TestAssert.True(page.Contains("<h1>Invite a friend</h1>", StringComparison.Ordinal) && page.Contains("name=username", StringComparison.Ordinal) &&
                    page.Contains("name=summoner", StringComparison.Ordinal) && page.Contains("name=mode value=maxed checked", StringComparison.Ordinal) &&
                    page.Contains("name=mode value=fresh>", StringComparison.Ordinal) && page.Contains("Create their login", StringComparison.Ordinal), page);
                TestAssert.Equal(HostDashboard.Policy, string.Join(";", form.Headers.GetValues("Content-Security-Policy")), "the host pages' policy");
            }

            // Refused forms make no login: no token, a bad username, a short password (never shown back).
            await RefusedAsync(browser, "The page expired", null, ("username", "friend1"), ("mode", "maxed"));
            await RefusedAsync(browser, "Choose a username of 3 to 24", "value=\"x!\"", ("username", "x!"), ("mode", "maxed"));
            await RefusedAsync(browser, "Choose a password of at least 10 characters.", "value=\"friend3\"", ("username", "friend3"), ("mode", "maxed"), ("password", "Short-1"));
            await RefusedAsync(browser, "Choose where their login starts", null, ("username", "friend3"), ("mode", "level99"));

            // friend1: the summoner name left empty, the password made by Amberline.
            var ready = await InviteFormAsync(browser, ("username", "Friend1"), ("summoner", ""), ("mode", "maxed"), ("password", ""));
            TestAssert.True(ready.Contains("<h1>friend1 is ready</h1>", StringComparison.Ordinal) &&
                ready.Contains("<a href=/host/play-file download>Download the Play file for Test Server</a>", StringComparison.Ordinal) &&
                ready.Contains("<button type=button data-copy=message>Copy</button>", StringComparison.Ordinal) &&
                ready.Contains("<script src=/host/page.js defer></script>", StringComparison.Ordinal) &&
                ready.Contains("The password is shown only now.", StringComparison.Ordinal) && !ready.Contains("onclick", StringComparison.Ordinal), ready);
            var message = Message(ready);
            TestAssert.True(message.StartsWith("Join my game server \"Test Server\"!\n", StringComparison.Ordinal) &&
                message.Contains("puts \"Test Server\" on your desktop. Then log in with:\n  username: friend1\n  password: ", StringComparison.Ordinal), message);
            generated = Regex.Match(message, @"password: (\S+)\z").Groups[1].Value;
            TestAssert.True(Generated.IsMatch(generated), "the message holds a generated password: " + message);
            using (var login = JsonDocument.Parse(await LoginAsync(browser, "friend1", generated, HttpStatusCode.OK)))
                TestAssert.Equal("Friend1", login.RootElement.GetProperty("summoner").GetString(), "an empty summoner name is the username as typed");
            await LoginAsync(browser, "friend1", "Wrong-password-1", HttpStatusCode.Forbidden);
            await RefusedAsync(browser, "already taken", null, ("username", "friend1"), ("mode", "maxed"));

            // friend2: a password the host typed, starting at level 1.
            var typedReady = await InviteFormAsync(browser, ("username", "friend2"), ("summoner", "Friend Two"), ("mode", "fresh"), ("password", typed));
            TestAssert.True(Message(typedReady).EndsWith("username: friend2\n  password: " + typed, StringComparison.Ordinal), typedReady);
            await LoginAsync(browser, "friend2", typed, HttpStatusCode.OK);
            TestAssert.False((await browser.GetStringAsync(HostInvite.InvitePath)).Contains(typed, StringComparison.Ordinal), "a new form is empty");

            var friends = await browser.GetStringAsync(HostInvite.FriendsPath);
            TestAssert.True(friends.Contains("<h1>Friends (2)</h1>", StringComparison.Ordinal) && friends.Contains("<b>friend1</b>", StringComparison.Ordinal) &&
                friends.Contains("<b>friend2</b>", StringComparison.Ordinal) && !friends.Contains("host_one", StringComparison.Ordinal) &&
                friends.Contains("no game yet", StringComparison.Ordinal) && !friends.Contains(generated, StringComparison.Ordinal), friends);

            // Advanced: the operator's friend launcher and registration are gone; Invite a friend and Friends replace them. Batch 648: in
            // the host pages' words (the nav replaces the Back link, as on Updates). Batch 652: the nav's own links, no paragraph repeating them.
            var advanced = await browser.GetStringAsync("/manage");
            TestAssert.True(advanced.Contains("<h1>Advanced</h1>", StringComparison.Ordinal) && !advanced.Contains("Back to the dashboard", StringComparison.Ordinal) &&
                advanced.Contains("<a href=/host/invite>Invite a friend</a>", StringComparison.Ordinal) && advanced.Contains("<a href=/host/friends>Friends</a>", StringComparison.Ordinal) &&
                !advanced.Contains("Play.cmd", StringComparison.Ordinal) && !advanced.Contains("href=\"/register\"", StringComparison.Ordinal) &&
                advanced.Contains("<h2>Give RP</h2>", StringComparison.Ordinal) && advanced.Contains("<h2>Make a reward code</h2>", StringComparison.Ordinal) &&
                advanced.Contains("Player reports", StringComparison.Ordinal) && advanced.Contains("<h2 id=leavers-heading>Leavers</h2>", StringComparison.Ordinal) &&
                advanced.Contains("<h2>Send a notice to players</h2>", StringComparison.Ordinal) && advanced.Contains("<h2>Make a friend log in again</h2>", StringComparison.Ordinal), advanced);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
        {
            TestAssert.Equal(3, store.ListAccounts().Length, "the host's login and two friends");
            TestAssert.True(store.Authenticate("host_one", HostPassword) is not null, "the host's own login is unchanged");
            TestAssert.True(store.FindName("Friend1")!.Economy is null && store.FindName("Friend Two")!.Economy is not null,
                "friend1 starts at level 30, friend2 at level 1");
        }
        NeverStored(home.Loop.Paths.Data, home.Screen.ToString(), generated, typed);
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "inviting leaves the champion list as it was");
        Delete(home.Root);
    }

    /// <summary>
    /// Batch 650: Invite lists every problem at once, as the first-run login step does (batch 649). A bad username, a short typed
    /// password and a bad start choice are all shown, each beside its field and linked from the summary; a name already taken is listed
    /// with them; an empty password is not a problem. The typed password is never shown back, and no login is made.
    /// </summary>
    [Check("server.host-invite-lists-every-problem", TimeoutSeconds = 120)]
    public static void InviteListsEveryProblemAtOnce() => InviteProblemsAsync().GetAwaiter().GetResult();

    private static async Task InviteProblemsAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Home.Create("league-host-invite-problems");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_one", HostPassword, "Host One").Id;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        const string typed = "Short-1";
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);

            // A bad username, a typed password of 7 characters and a start choice that does not exist: all three at once.
            var page = await PostAsync(browser, HostInvite.InvitePath, Token(await browser.GetStringAsync(HostInvite.InvitePath)), HttpStatusCode.BadRequest,
                ("username", "x!"), ("summoner", ""), ("mode", "level99"), ("password", typed));
            TestAssert.True(page.Contains("<a href=#username>Choose a username of 3 to 24", StringComparison.Ordinal) &&
                page.Contains("<a href=#password>Choose a password of at least 10 characters.</a>", StringComparison.Ordinal) &&
                page.Contains("<a href=#mode>Choose where their login starts.</a>", StringComparison.Ordinal) &&
                page.Contains("id=username-error", StringComparison.Ordinal) && page.Contains("id=password-error", StringComparison.Ordinal) &&
                page.Contains("id=mode-error", StringComparison.Ordinal), page);
            TestAssert.False(page.Contains(typed, StringComparison.Ordinal), "a refused form never shows the password back");

            // A name already taken (the host's own login) is listed with the short password, and the start choice is fine.
            page = await PostAsync(browser, HostInvite.InvitePath, Token(await browser.GetStringAsync(HostInvite.InvitePath)), HttpStatusCode.BadRequest,
                ("username", "host_one"), ("summoner", "Host Two"), ("mode", "maxed"), ("password", typed));
            TestAssert.True(page.Contains("<a href=#username>This username or summoner name is already taken.", StringComparison.Ordinal) &&
                page.Contains("<a href=#password>Choose a password of at least 10 characters.</a>", StringComparison.Ordinal) &&
                !page.Contains("id=mode-error", StringComparison.Ordinal), page);

            // An empty password is not a problem (Amberline makes one): only the bad username is listed.
            page = await PostAsync(browser, HostInvite.InvitePath, Token(await browser.GetStringAsync(HostInvite.InvitePath)), HttpStatusCode.BadRequest,
                ("username", "x!"), ("summoner", ""), ("mode", "maxed"), ("password", ""));
            TestAssert.True(page.Contains("<a href=#username>Choose a username of 3 to 24", StringComparison.Ordinal) &&
                !page.Contains("Choose a password", StringComparison.Ordinal) && !page.Contains("id=password-error", StringComparison.Ordinal), page);

            TestAssert.True((await browser.GetStringAsync(HostInvite.FriendsPath)).Contains("<h1>Friends (0)</h1>", StringComparison.Ordinal),
                "refused forms make no login");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    [Check("server.host-play-file-renders-connection", TimeoutSeconds = 180)]
    public static void ThePlayFileCarriesTheServersNameAddressPortAndPin() => PlayFileAsync().GetAwaiter().GetResult();

    private static async Task PlayFileAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Home.Create("league-host-play-file");
        // The template beside the Platform, as tools/build-server.ps1 -Layout Home puts it, here with a checkout's LF line ends.
        var source = AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", HostInvite.TemplateFile));
        var template = Path.Combine(home.Loop.Paths.App, HostInvite.TemplateFile);
        Directory.CreateDirectory(home.Loop.Paths.App);
        File.WriteAllText(template, File.ReadAllText(source).ReplaceLineEndings("\n"));
        WriteSettings(home, new { serverName = "Test Server" });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using (var stranger = home.Client())
            using (var refused = await stranger.GetAsync(HostInvite.PlayFilePath))
                TestAssert.Equal(HttpStatusCode.Forbidden, refused.StatusCode, "the Play file needs the host's session");
            foreach (var path in new[] { HostInvite.PlayFilePath, HostInvite.InvitePath })
            {
                using var early = await browser.GetAsync(path);
                var page = await early.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.Conflict, early.StatusCode, path + " before the address: " + page);
                TestAssert.True(page.Contains("Your address first", StringComparison.Ordinal) && page.Contains("href=/host/setup>", StringComparison.Ordinal), page);
            }

            // The address step saves the address; the Play file follows at once, without a restart.
            var step = await browser.GetStringAsync("/host/setup/address");
            await PostAsync(browser, "/host/setup/address", Token(step), HttpStatusCode.Redirect, ("choice", "typed"), ("address", TestHost));
            var pin = Pin(home);
            var (text, disposition) = await PlayFileAsync(browser);
            TestAssert.True(disposition.Contains("filename=Play.cmd", StringComparison.Ordinal), disposition);
            TestAssert.Equal(PlatformBootstrap.RenderLauncher(File.ReadAllText(source).ReplaceLineEndings("\r\n"), TestHost, home.RemotePort, pin, "Test Server"),
                text, "the template rendered with the server's name, address, public port and pin");
            TestAssert.False(Regex.IsMatch(text, "(?<!\r)\n"), "CRLF line ends throughout: cmd.exe reads the file");
            foreach (var placeholder in new[] { "__HOST__", "__PORT__", "__CERT__", "__NAME__" })
                TestAssert.False(text.Contains(placeholder, StringComparison.Ordinal), placeholder + " is filled in");
            TestAssert.False(text.Contains(TestHost, StringComparison.OrdinalIgnoreCase), "the address is never in plain text");
            var token = Regex.Match(text, "\\$HostToken = '([^']+)'").Groups[1].Value;
            TestAssert.Equal(TestHost, Encoding.ASCII.GetString(Convert.FromBase64String(token).Select(b => (byte)(b ^ 0x5A)).ToArray()), "the host token decodes");
            TestAssert.True(text.Contains($"$PortText = '{home.RemotePort}'", StringComparison.Ordinal) && text.Contains($"$Pin = '{pin}'", StringComparison.Ordinal) &&
                text.Contains("$Name = 'Test Server'", StringComparison.Ordinal), "port, pin and name");
            if (OperatingSystem.IsWindows())
            {
                // tools/player-file.ps1 (batch 615 and 619) writes the same file for the same server and certificate.
                var built = Path.Combine(home.Root, "player-file");
                var result = AirBootstrapChecks.RunPowerShell("pwsh", ["-File", AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "player-file.ps1")),
                    "-Root", built, "-PublicHost", TestHost, "-Port", home.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-Certificate", Path.Combine(home.Loop.Paths.Data, "remote", "host.pfx"), "-ServerName", "Test Server"]);
                TestAssert.Equal(0, result.ExitCode, result.Output);
                TestAssert.Equal(File.ReadAllText(Path.Combine(built, "Play.cmd")), text, "the page's Play file equals tools/player-file.ps1's");
            }

            // A friend's file and the server's manifest agree: the manifest names the same server, address and pin.
            using (var friend = new HttpClient(new HttpClientHandler
                   {
                       UseProxy = false,
                       ServerCertificateCustomValidationCallback = (_, served, _, _) => served?.GetCertHashString(HashAlgorithmName.SHA256) == pin,
                   }) { Timeout = TimeSpan.FromSeconds(10) })
            using (var manifest = JsonDocument.Parse(await friend.GetStringAsync($"https://127.0.0.1:{home.RemotePort}/bootstrap/client.json")))
            {
                TestAssert.Equal(TestHost, manifest.RootElement.GetProperty("host").GetString());
                TestAssert.Equal("Test Server", manifest.RootElement.GetProperty("serverName").GetString());
                TestAssert.Equal(pin, manifest.RootElement.GetProperty("certificateSha256").GetString());
            }

            // A new server name reaches the next download, also without a restart.
            var name = await browser.GetStringAsync(HostSteps.NamePath);
            await PostAsync(browser, HostSteps.NamePath, Token(name), HttpStatusCode.Redirect, ("name", "Other Server"));
            TestAssert.True((await PlayFileAsync(browser)).Text.Contains("$Name = 'Other Server'", StringComparison.Ordinal), "the renamed server");

            // Amberline's own template removed (for example by an antivirus): said in plain words.
            File.Delete(template);
            using var missing = await browser.GetAsync(HostInvite.PlayFilePath);
            TestAssert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            TestAssert.True((await missing.Content.ReadAsStringAsync()).Contains("The Play file is missing", StringComparison.Ordinal), "the missing template");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    [Check("server.host-remove-access-ends-sessions", TimeoutSeconds = 120)]
    public static void RemoveAccessAndNewPasswordEndTheFriendsSessionsButNeverTheHosts() => RemoveAccessAsync().GetAwaiter().GetResult();

    private static async Task RemoveAccessAsync()
    {
        const string oldPassword = "Synthetic-old-Pw-41";
        const string version = "1.45.11_10_04_15_28";
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-remove-access");
        var log = new AirReportChecks.CapturedLog();
        var paths = HostPaths.Resolve(Path.Combine(root, "data"), Path.Combine(root, "Local"), Path.Combine(root, "app", "platform"));
        string newPassword;
        try
        {
            using var accounts = new PlatformAccounts(paths.Data);
            var host = accounts.RegisterPublic("host_one", HostPassword, "Host One");
            var friend = accounts.RegisterPublic("friend1", oldPassword, "Friend One");
            var other = accounts.RegisterPublic("friend2", oldPassword, "Friend Two");
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(new { hostAccount = host.Id }));
            var sessions = new PlatformSessions(accounts);
            var access = new AirRemoteAccess(Path.Combine(paths.Data, "remote"));
            var loop = new HostLoop(paths, _ => { }, TextWriter.Null);
            var port = AirManagementChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(log);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));
            await using var app = builder.Build();
            HostGate.Use(app, port, loop.Sessions, HostDashboard.PagePath);
            HostInvite.Map(app, loop, () => TestHost, () => "Test Server", accounts, sessions, access, 8391, new string('A', 64));
            app.MapPost(Amberline.Platform.Program.LoginQueuePath, (Delegate)((HttpContext context) =>
                Amberline.Platform.Program.AuthenticateAsync(context, accounts, sessions, access)));
            await app.StartAsync();
            using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            using (var open = await browser.GetAsync(loop.PageUrl(port))) TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "the launch code signs in");

            // Each login holds a tunnel token, a live session, an unused login ticket and a session parked for a reconnect.
            // A login's ways in: a tunnel token, a session (live, or parked for a reconnect) and an unused login ticket.
            (string Tunnel, string Session, string Ticket) Signed(PlatformAccount account, bool park = false)
            {
                var tunnel = access.Issue(account.Id).Token;
                var session = sessions.Login("rtmp-" + account.Id, account.Username, sessions.IssueTicket(account), version);
                TestAssert.True(sessions.AuthenticateChannel("rtmp-" + account.Id, account.Username, session.Token));
                if (park) sessions.Suspend("rtmp-" + account.Id);
                return (tunnel, session.Token, sessions.IssueTicket(account));
            }
            void StillIn(PlatformAccount account, (string Tunnel, string Session, string Ticket) held, string who)
            {
                TestAssert.Equal(account.Id, access.AccountFor(held.Tunnel), who + "'s tunnel token stays");
                TestAssert.Equal(account.Id, sessions.AccountForConnection("rtmp-" + account.Id), who + "'s session stays");
            }
            var hostHeld = Signed(host);
            var friendHeld = Signed(friend);
            var otherHeld = Signed(other);

            var page = await browser.GetStringAsync(HostInvite.FriendsPath);
            TestAssert.True(page.Contains("<h1>Friends (2)</h1>", StringComparison.Ordinal) && !page.Contains("host_one", StringComparison.Ordinal) &&
                page.Contains($"name=accountId value={friend.Id}>", StringComparison.Ordinal) && !page.Contains($"name=accountId value={host.Id}>", StringComparison.Ordinal) &&
                Regex.Matches(page, "online now").Count == 2, "both friends online, the host not listed: " + page);
            var token = Token(page);

            // Refused: no token, the host's own login (Remove access and New password), an account that does not exist, another site.
            await PostAsync(browser, HostInvite.FriendsPath + "/remove", null, HttpStatusCode.BadRequest, ("accountId", friend.Id.ToString()));
            foreach (var action in new[] { "/remove", "/password" })
            {
                var refused = await PostAsync(browser, HostInvite.FriendsPath + action, token, HttpStatusCode.BadRequest, ("accountId", host.Id.ToString()));
                TestAssert.True(refused.Contains("Choose one of your friends.", StringComparison.Ordinal), refused);
            }
            await PostAsync(browser, HostInvite.FriendsPath + "/remove", token, HttpStatusCode.BadRequest, ("accountId", "999999"));
            using (var crossSite = new HttpRequestMessage(HttpMethod.Post, HostInvite.FriendsPath + "/remove")
                   { Content = new FormUrlEncodedContent(new Dictionary<string, string> { [token.Key] = token.Value, ["accountId"] = friend.Id.ToString() }) })
            {
                crossSite.Headers.TryAddWithoutValidation("Origin", "http://attacker.example");
                TestAssert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(crossSite)).StatusCode, "another site's form");
            }
            await LoginAsync(browser, "friend1", oldPassword, HttpStatusCode.OK);
            StillIn(friend, friendHeld, "friend1");
            StillIn(host, hostHeld, "the host");

            // Remove access: the password no longer works, every way in ends; the account and the other logins stay.
            var removed = await PostAsync(browser, HostInvite.FriendsPath + "/remove", token, HttpStatusCode.OK, ("accountId", friend.Id.ToString()));
            TestAssert.True(removed.Contains("friend1 can no longer log in, and was logged out.", StringComparison.Ordinal) &&
                removed.Contains("<b>friend1</b>", StringComparison.Ordinal), "the account stays listed: " + removed);
            await LoginAsync(browser, "friend1", oldPassword, HttpStatusCode.Forbidden);
            TestAssert.True(accounts.Authenticate("friend1", oldPassword) is null, "the tunnel's sign-in refuses the old password too");
            TestAssert.True(access.AccountFor(friendHeld.Tunnel) is null, "friend1's tunnel token is revoked");
            TestAssert.True(sessions.AccountForConnection("rtmp-" + friend.Id) is null, "friend1's live session ended");
            TestAssert.Throws<ArgumentException>(() => sessions.Login("rtmp-late", friend.Username, friendHeld.Ticket, version), "friend1's unused ticket");
            TestAssert.True(accounts.Find(friend.Id) is not null, "no account is deleted");
            StillIn(other, otherHeld, "friend2");
            StillIn(host, hostHeld, "the host");
            TestAssert.True(accounts.Authenticate("host_one", HostPassword) is not null && accounts.Authenticate("friend2", oldPassword) is not null,
                "the host's and friend2's passwords stay");

            // Batch 646: the store keeps the removal (only for friend1, and no password in it), so Friends says "Access removed" and
            // offers only Give access again; friend2's row keeps both buttons.
            var store = File.ReadAllText(Path.Combine(paths.Data, "accounts.json"));
            TestAssert.True(Regex.Count(store, "\"AccessRemovedAt\"") == 1 && !store.Contains(oldPassword, StringComparison.Ordinal), "the removal is on record: " + store);
            string Row(string page, string username) => Regex.Match(page, $"<tr><td><b>{username}</b>.*?</tr>", RegexOptions.Singleline).Value;
            var listed = await browser.GetStringAsync(HostInvite.FriendsPath);
            TestAssert.True(Row(listed, "friend1").Contains("<b>Access removed</b> on ", StringComparison.Ordinal) &&
                Row(listed, "friend1").Contains("Give access again<span class=vh> for friend1</span>", StringComparison.Ordinal) &&
                !Row(listed, "friend1").Contains("New password", StringComparison.Ordinal) && !Row(listed, "friend1").Contains("Remove access", StringComparison.Ordinal) &&
                Row(listed, "friend2").Contains("Remove access<span class=vh> for friend2</span>", StringComparison.Ordinal) &&
                !Row(listed, "friend2").Contains("Access removed", StringComparison.Ordinal) &&
                listed.Contains("<p class=tip>New password and Remove access log the friend out at once.", StringComparison.Ordinal), "friend1's access removed, friend2's not: " + listed);

            // Give access again (New password's route) lets friend1 in again, shown once; it ends friend1's ways in again (a parked
            // session too), nobody else's.
            var fresh = Signed(friend, park: true);
            var changed = await PostAsync(browser, HostInvite.FriendsPath + "/password", token, HttpStatusCode.OK, ("accountId", friend.Id.ToString()));
            var message = Message(changed);
            newPassword = Regex.Match(message, @"password: (\S+)\n").Groups[1].Value;
            TestAssert.True(Generated.IsMatch(newPassword) && message.StartsWith("Your access to \"Test Server\" is back:\n  username: friend1\n", StringComparison.Ordinal) &&
                changed.Contains("<h1>friend1 can log in again</h1>", StringComparison.Ordinal) && changed.Contains("data-copy=message", StringComparison.Ordinal), changed);
            TestAssert.False(File.ReadAllText(Path.Combine(paths.Data, "accounts.json")).Contains("\"AccessRemovedAt\"", StringComparison.Ordinal), "a new password ends the removal");
            TestAssert.True(Row(await browser.GetStringAsync(HostInvite.FriendsPath), "friend1").Contains("Remove access", StringComparison.Ordinal), "friend1 is active again");
            await LoginAsync(browser, "friend1", newPassword, HttpStatusCode.OK);
            TestAssert.True(access.AccountFor(fresh.Tunnel) is null, "New password revokes friend1's tunnel token");
            TestAssert.False(sessions.Resume("rtmp-back", friend.Username, fresh.Session), "friend1's parked session cannot resume");
            TestAssert.Throws<ArgumentException>(() => sessions.Login("rtmp-late", friend.Username, fresh.Ticket, version), "friend1's unused ticket");
            StillIn(other, otherHeld, "friend2");
            StillIn(host, hostHeld, "the host");
            TestAssert.False((await browser.GetStringAsync(HostInvite.FriendsPath)).Contains(newPassword, StringComparison.Ordinal), "shown only once");
            // New password for a friend whose access stays keeps its own words.
            var renewed = Message(await PostAsync(browser, HostInvite.FriendsPath + "/password", token, HttpStatusCode.OK, ("accountId", friend.Id.ToString())));
            var renewedPassword = Regex.Match(renewed, @"password: (\S+)\n").Groups[1].Value;
            TestAssert.True(Generated.IsMatch(renewedPassword) && renewed.StartsWith("Your new password for \"Test Server\":\n  username: friend1\n", StringComparison.Ordinal), renewed);
            await LoginAsync(browser, "friend1", renewedPassword, HttpStatusCode.OK);
            TestAssert.True(log.Text.Contains("/host/friends/password", StringComparison.Ordinal), "the web host log saw the requests");
            TestAssert.False(log.Text.Contains(newPassword, StringComparison.Ordinal) || log.Text.Contains(renewedPassword, StringComparison.Ordinal),
                "the web host log never holds a password");
            await app.StopAsync();
        }
        catch { Delete(root); throw; }
        NeverStored(paths.Data, "", newPassword);
        Delete(root);
    }

    internal static void WriteSettings(Home home, object extra)
    {
        var settings = JsonSerializer.SerializeToNode(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort,
            bindAddress = "127.0.0.1", dataRoot = home.Game, airRoot = home.Air,
        })!.AsObject();
        foreach (var (key, value) in JsonSerializer.SerializeToNode(extra)!.AsObject()) settings[key] = value?.DeepClone();
        File.WriteAllText(home.Loop.Paths.Settings, settings.ToJsonString());
    }

    internal static string Pin(Home home)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(home.Loop.Paths.Data, "remote", "host.pfx"), null);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private static async Task<(string Text, string Disposition)> PlayFileAsync(HttpClient browser)
    {
        using var file = await browser.GetAsync(HostInvite.PlayFilePath);
        TestAssert.Equal(HttpStatusCode.OK, file.StatusCode, await file.Content.ReadAsStringAsync());
        TestAssert.Equal("application/octet-stream", file.Content.Headers.ContentType?.MediaType);
        var bytes = await file.Content.ReadAsByteArrayAsync();
        TestAssert.True(bytes.All(b => b < 0x80), "an ASCII file");
        return (Encoding.ASCII.GetString(bytes), file.Content.Headers.ContentDisposition?.ToString() ?? "");
    }

    /// <summary>The copyable message's text, as <c>page.js</c> copies it.</summary>
    private static string Message(string page)
    {
        var match = Regex.Match(page, "<pre id=message[^>]*>(.*?)</pre>", RegexOptions.Singleline);
        TestAssert.True(match.Success, "the page has the message: " + page);
        return WebUtility.HtmlDecode(match.Groups[1].Value).ReplaceLineEndings("\n");
    }

    private static async Task<string> InviteFormAsync(HttpClient browser, params (string Name, string Value)[] fields) =>
        await PostAsync(browser, HostInvite.InvitePath, Token(await browser.GetStringAsync(HostInvite.InvitePath)), HttpStatusCode.OK, fields);

    private static async Task RefusedAsync(HttpClient browser, string says, string? keeps, params (string Name, string Value)[] fields)
    {
        var count = Regex.Match(await browser.GetStringAsync(HostInvite.FriendsPath), @"<h1>Friends \((\d+)\)</h1>").Groups[1].Value;
        var token = says == "The page expired" ? (KeyValuePair<string, string>?)null : Token(await browser.GetStringAsync(HostInvite.InvitePath));
        var page = await PostAsync(browser, HostInvite.InvitePath, token, HttpStatusCode.BadRequest, fields);
        TestAssert.True(page.Contains(says, StringComparison.Ordinal) && (keeps is null || page.Contains(keeps, StringComparison.Ordinal)), page);
        foreach (var (name, value) in fields)
            if (name == "password") TestAssert.False(page.Contains(value, StringComparison.Ordinal), "a refused form never shows the password back");
        TestAssert.True((await browser.GetStringAsync(HostInvite.FriendsPath)).Contains($"<h1>Friends ({count})</h1>", StringComparison.Ordinal), "a refused form makes no login");
    }

    internal static async Task<string> PostAsync(HttpClient client, string path, KeyValuePair<string, string>? token, HttpStatusCode expected,
        params (string Name, string Value)[] fields)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        if (token is { } field) values[field.Key] = field.Value;
        using var answer = await client.PostAsync(path, new FormUrlEncodedContent(values));
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(expected, answer.StatusCode, $"POST {path}: {page}");
        return page;
    }

    internal static async Task<string> LoginAsync(HttpClient client, string username, string password, HttpStatusCode expected)
    {
        using var answer = await client.PostAsync(Amberline.Platform.Program.LoginQueuePath, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["payload"] = $"user={username},password={password}" }));
        TestAssert.Equal(expected, answer.StatusCode, $"the login queue for {username}");
        return await answer.Content.ReadAsStringAsync();
    }

    /// <summary>No file under the folder (the account store included) and no window text holds a password in plain text.</summary>
    internal static void NeverStored(string root, string screen, params string[] secrets)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Select(path => (path, File.ReadAllText(path))).ToArray();
        TestAssert.True(files.Any(file => file.path.EndsWith("accounts.json", StringComparison.Ordinal)), "the account store was written");
        foreach (var secret in secrets)
        {
            TestAssert.True(secret.Length >= 10, "a password to look for");
            TestAssert.False(screen.Contains(secret, StringComparison.Ordinal), "the window never shows a password");
            foreach (var (path, text) in files)
                TestAssert.False(text.Contains(secret, StringComparison.Ordinal), $"a password is never stored in plain text ({Path.GetFileName(path)})");
        }
    }
}
