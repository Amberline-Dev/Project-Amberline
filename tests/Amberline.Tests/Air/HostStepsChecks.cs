using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Host;
using Amberline.Platform;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's first-run steps (batch 626, H6 of the Amberline Host design): the server's name, the host's own login, the
/// address friends connect to (which <c>/bootstrap/client.json</c> follows without a restart) and the certificate's lifetime. The
/// router and the outside lookup are fakes (<see cref="HostLoop.Addresses"/>), so no check asks a router or the lookup service;
/// everything runs in temporary folders on free loopback ports. Addresses are from the documentation ranges of RFC 5737.
/// </summary>
public static class HostStepsChecks
{
    private const string TestHost = "amberline.example.test";

    [Check("server.host-address-validation")]
    public static void TheAddressStepTakesOnlyAddressesFriendsCanReach()
    {
        foreach (var (text, saved) in new[]
                 {
                     (TestHost, TestHost), ("  Amberline.Example.TEST ", TestHost), ("friday-league.example.org", "friday-league.example.org"),
                     ("x1.example", "x1.example"), ("203.0.113.7", "203.0.113.7"), ("198.51.100.20", "198.51.100.20"),
                 })
        {
            var (host, problem) = HostAddress.Check(text);
            TestAssert.Equal(saved, host, text);
            TestAssert.Equal(null, problem, text);
            // Whatever the step saves is a host the Platform's bootstrap serves and renders (RenderLauncher throws otherwise).
            PlatformBootstrap.RenderLauncher("__HOST__", host!, 8391, new string('A', 64));
        }
        foreach (var (text, says) in new (string?, string)[]
                 {
                     (null, "Type your address."), ("   ", "Type your address."),
                     ("https://" + TestHost, "without http://"), (TestHost + ":8391", "without http://"), (TestHost + "/play", "without http://"),
                     ("amber line.example.test", "without http://"), ("2001:db8::1", "IPv4"),
                     ("localhost", "cannot use this name"), ("amberline", "cannot use this name"), ("-x.example.test", "cannot use this name"),
                     ("x-.example.test", "cannot use this name"), ("a..example.test", "cannot use this name"),
                     ("ämber.example.test", "cannot use this name"), ("under_score.example.test", "cannot use this name"),
                     ("example.123", "cannot use this name"), (new string('a', 64) + ".example.test", "cannot use this name"),
                     (string.Join('.', Enumerable.Repeat("abcdefghi", 26)), "too long"),
                     ("203.0.113", "not a whole address"), ("203.0.113.256", "not a whole address"), ("203.000.113.7", "not a whole address"),
                     ("203.0.113.7.1", "not a whole address"),
                     ("192.168.1.10", "home network"), ("10.0.0.2", "home network"), ("172.16.5.4", "home network"),
                     ("100.64.1.2", "shares one address"),
                     ("127.0.0.1", "cannot reach"), ("0.0.0.0", "cannot reach"), ("169.254.1.1", "cannot reach"), ("224.0.0.1", "cannot reach"),
                 })
        {
            var (host, problem) = HostAddress.Check(text);
            TestAssert.Equal(null, host, text ?? "null");
            TestAssert.True(problem?.Contains(says, StringComparison.Ordinal) == true, $"{text}: {problem}");
        }
    }

    [Check("server.host-lan-address-accepted", TimeoutSeconds = 180)]
    public static void TheHomeNetworkChoiceTakesOnlyThisNetworksAddresses() => LanAddressAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 632 (owner, 2 November 2026: "Yes, as a 'home network only' choice"): the address step's home-network choice offers this
    /// PC's own home-network address and takes a typed private IPv4 address; loopback, link-local and the provider's shared range are
    /// refused in plain words; the address is saved as <c>publicHost</c> with <c>addressSource</c> <c>lan</c> and served at once.
    /// </summary>
    private static async Task LanAddressAsync()
    {
        foreach (var (text, saved) in new[]
                 {
                     ("192.168.1.20", "192.168.1.20"), (" 192.168.1.20 ", "192.168.1.20"), ("10.0.0.2", "10.0.0.2"), ("172.16.5.4", "172.16.5.4"),
                     ("172.31.255.254", "172.31.255.254"), ("192.168.255.1", "192.168.255.1"),
                 })
        {
            var (host, problem) = HostAddress.CheckLan(text);
            TestAssert.Equal(saved, host, text);
            TestAssert.Equal(null, problem, text);
            PlatformBootstrap.RenderLauncher("__HOST__", host!, 8391, new string('A', 64));
        }
        foreach (var (text, says) in new (string?, string)[]
                 {
                     (null, "Type this PC's address"), ("  ", "Type this PC's address"),
                     ("127.0.0.1", "\"this PC itself\""), ("127.10.0.1", "\"this PC itself\""),
                     ("169.254.10.20", "169.254 by itself"), ("100.64.1.2", "belongs to your internet provider"), ("100.127.255.1", "belongs to your internet provider"),
                     ("203.0.113.7", "internet address"), ("172.32.0.1", "internet address"), ("11.0.0.1", "internet address"),
                     ("0.0.0.0", "cannot reach"), ("224.0.0.1", "cannot reach"),
                     ("192.168.1", "not a home-network address"), ("192.168.001.020", "not a home-network address"), ("192.168.1.256", "not a home-network address"),
                     ("fe80::1", "not a home-network address"), (TestHost, "not a home-network address"), ("192.168.1.20:8391", "not a home-network address"),
                 })
        {
            var (host, problem) = HostAddress.CheckLan(text);
            TestAssert.Equal(null, host, text ?? "null");
            TestAssert.True(problem?.Contains(says, StringComparison.Ordinal) == true, $"{text}: {problem}");
        }
        // The internet choices still refuse a home-network address, and now point at the home-network choice.
        TestAssert.True(HostAddress.Check("192.168.1.20").Problem?.Contains("Only people on my home network", StringComparison.Ordinal) == true,
            "the typed choice names the home-network choice");

        var variables = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Home.Create("league-host-lan-address");
        IPAddress? own = IPAddress.Parse("192.168.1.20");
        var routerAsked = 0;
        home.Loop.Addresses = new HostAddressSources(_ => { Interlocked.Increment(ref routerAsked); return Task.FromResult<IPAddress?>(IPAddress.Parse("203.0.113.7")); },
            _ => throw new InvalidOperationException("the lookup is never asked"), () => own);
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort,
            bindAddress = "127.0.0.1", dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server",
        }));
        string pin;
        using (var certificate = HostFiles.Certificate(Path.Combine(home.Loop.Paths.Data, "remote"))) pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var page = await browser.GetStringAsync("/host/setup/address");
            // Batch 652: the field holds the address Amberline found (its hint no longer repeats it) and says who can join.
            TestAssert.True(page.Contains("<input type=radio name=choice value=lan>Only people on my home network (LAN party)</label>", StringComparison.Ordinal) &&
                page.Contains("name=lan maxlength=15", StringComparison.Ordinal) && page.Contains("placeholder=\"192.168.1.20\" value=\"192.168.1.20\"", StringComparison.Ordinal) &&
                page.Contains("Only friends on the same router or Wi-Fi can join.", StringComparison.Ordinal), "the choice offers this PC's own address: " + page);
            TestAssert.True(page.Contains("value=router checked", StringComparison.Ordinal), "the router's address stays the preselected choice");

            // Refused: nothing saved, the typed text shown back.
            var before = File.ReadAllText(home.Loop.Paths.Settings);
            foreach (var (text, says) in new[] { ("127.0.0.1", "this PC itself"), ("169.254.3.4", "169.254"), ("100.64.0.9", "internet provider"), ("203.0.113.9", "internet address") })
            {
                using var refused = await PostAsync(browser, "/host/setup/address", ("choice", "lan"), ("lan", text));
                var answer = await refused.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.BadRequest, refused.StatusCode, answer);
                TestAssert.True(answer.Contains(says, StringComparison.Ordinal) && answer.Contains($"value=\"{text}\"", StringComparison.Ordinal) &&
                    answer.Contains("value=lan checked", StringComparison.Ordinal), $"{text}: " + answer);
            }
            TestAssert.Equal(before, File.ReadAllText(home.Loop.Paths.Settings), "a refused home-network address saves nothing");

            // A typed private address (not the one Amberline found) is saved as lan and served at once.
            await SavedAsync(browser, "/host/setup/address", ("choice", "lan"), ("lan", " 10.0.0.2 "));
            await ExpectAddressAsync(browser, home, "10.0.0.2", HostAddress.Lan, pin, 1);
            await SavedAsync(browser, "/host/setup/address", ("choice", "lan"), ("lan", "192.168.1.20"));
            await ExpectAddressAsync(browser, home, "192.168.1.20", HostAddress.Lan, pin, 1);
            page = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(page.Contains("Saved now: <b>192.168.1.20</b> (home network only).", StringComparison.Ordinal) &&
                page.Contains("value=lan checked", StringComparison.Ordinal) && !page.Contains("value=router checked", StringComparison.Ordinal),
                "the step shows the saved home-network choice: " + page);
            using (var next = await browser.GetAsync(HostSteps.Start))
                TestAssert.Equal("/host/setup/login", next.Headers.Location?.OriginalString, "the address is done");

            // No home-network address on this PC (none found, or only an internet one): the field is empty, the choice still there.
            own = IPAddress.Parse("203.0.113.50");
            await SavedAsync(browser, "/host/setup/address", ("choice", "typed"), ("address", TestHost));
            page = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(page.Contains("value=lan>", StringComparison.Ordinal) &&
                page.Contains("placeholder=\"192.168.1.20\" value=\"\"", StringComparison.Ordinal), "nothing offered: " + page);
            TestAssert.True(routerAsked > 0, "the page still asks the router for the internet choices");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in variables) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    [Check("server.host-certificate-lifetime")]
    public static void NewCertificatesLastTwentyYearsAndAnExistingOneIsKept()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-certificate");
        try
        {
            // A new install: HostFiles.Certificate (the Platform's start, --home or not) makes a 20-year certificate.
            using (var made = HostFiles.Certificate(Path.Combine(root, "new")))
                TestAssert.True(TwentyYears(made), "a new certificate lasts 20 years: " + Lifetime(made));

            // An existing install keeps its file byte for byte, with its pin and its own two years.
            var old = TwoYearCertificate(Path.Combine(root, "old"));
            var bytes = File.ReadAllBytes(old);
            using (var kept = HostFiles.Certificate(Path.GetDirectoryName(old)!))
                TestAssert.True(kept.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddYears(3), "the kept certificate keeps its lifetime: " + Lifetime(kept));
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(old)), "HostFiles.Certificate replaced an existing host.pfx");
            if (!OperatingSystem.IsWindows()) return;

            // tools/player-file.ps1, which tools/build-server.ps1 runs: a new folder gets 20 years; a folder that holds one, or a
            // -Certificate, keeps it as it is.
            var fresh = Path.Combine(root, "fresh");
            PlayerFile(fresh, null);
            using (var made = X509CertificateLoader.LoadPkcs12FromFile(Pfx(fresh), null))
                TestAssert.True(TwentyYears(made), "player-file.ps1 makes a 20-year certificate: " + Lifetime(made));
            var rebuilt = Path.Combine(root, "rebuilt");
            Directory.CreateDirectory(Path.GetDirectoryName(Pfx(rebuilt))!);
            File.Copy(old, Pfx(rebuilt));
            PlayerFile(rebuilt, null);
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Pfx(rebuilt))), "player-file.ps1 replaced the folder's host.pfx");
            var copied = Path.Combine(root, "copied");
            PlayerFile(copied, old);
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Pfx(copied))), "player-file.ps1 changed the -Certificate it kept");
        }
        finally { Delete(root); }
    }

    [Check("server.host-bootstrap-host-follows-settings", TimeoutSeconds = 180)]
    public static void TheBootstrapServesTheSavedAddressWithoutARestart() => FollowsAsync().GetAwaiter().GetResult();

    private static async Task FollowsAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var catalogLoaded = ChampionCatalogLoaded();
        var home = Home.Create("league-host-address");
        IPAddress? router = IPAddress.Parse("203.0.113.7");
        string? answer = "198.51.100.20";
        var lookups = 0;
        home.Loop.Addresses = new HostAddressSources(_ => Task.FromResult<IPAddress?>(router),
            _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>(answer); });
        // Both folders chosen and the server named, so the loop starts the full Platform at once; no address yet.
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort,
            bindAddress = "127.0.0.1", dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server",
        }));
        // An install from before H6: its two-year certificate stays, with its pin.
        var pfx = TwoYearCertificate(Path.Combine(home.Loop.Paths.Data, "remote"));
        var certificate = File.ReadAllBytes(pfx);
        string pin;
        using (var loaded = X509CertificateLoader.LoadPkcs12(certificate, null)) pin = loaded.GetCertHashString(HashAlgorithmName.SHA256);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using (var none = await browser.GetAsync("/bootstrap/client.json"))
                TestAssert.Equal(HttpStatusCode.NotFound, none.StatusCode, "no address yet, so nothing for friends");

            var page = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(page.Contains("The address your router reports: 203.0.113.7", StringComparison.Ordinal), page);
            TestAssert.Equal(0, lookups, "the page never asks the outside service by itself");

            var before = File.ReadAllText(home.Loop.Paths.Settings);
            await RefusedAsync(browser, "/host/setup/address", "home network", ("choice", "typed"), ("address", "192.168.1.10"));
            TestAssert.Equal(before, File.ReadAllText(home.Loop.Paths.Settings), "a refused address saves nothing");

            await SavedAsync(browser, "/host/setup/address", ("choice", "typed"), ("address", " Amberline.Example.Test "));
            await ExpectAddressAsync(browser, home, TestHost, HostAddress.Typed, pin, 1);
            // Friends read the manifest on the public port, pinned to the kept certificate.
            using (var friend = new HttpClient(new HttpClientHandler
                   {
                       UseProxy = false,
                       ServerCertificateCustomValidationCallback = (_, served, _, _) => served?.GetCertHashString(HashAlgorithmName.SHA256) == pin,
                   }) { Timeout = TimeSpan.FromSeconds(10) })
            using (var manifest = JsonDocument.Parse(await friend.GetStringAsync($"https://127.0.0.1:{home.RemotePort}/bootstrap/client.json")))
                TestAssert.Equal(TestHost, manifest.RootElement.GetProperty("host").GetString(), "the public port serves the new address");
            page = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(page.Contains($"Saved now: <b>{TestHost}</b> (typed by you)", StringComparison.Ordinal) &&
                page.Contains($"value=\"{TestHost}\"", StringComparison.Ordinal), "the step shows what was saved: " + page);

            await SavedAsync(browser, "/host/setup/address", ("choice", "router"));
            await ExpectAddressAsync(browser, home, "203.0.113.7", HostAddress.Router, pin, 1);

            // A router behind the provider's shared address: its card, no router choice, and the choice refused.
            router = IPAddress.Parse("100.64.0.9");
            page = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(page.Contains("(A-NET-07)", StringComparison.Ordinal) &&
                !page.Contains("The address your router reports", StringComparison.Ordinal), page);
            await RefusedAsync(browser, "/host/setup/address", "shares one address", ("choice", "router"));
            // Batch 646: with the home-network choice the router's card is a tip, not a problem (the router's address does not matter
            // there), and the address field's own hint stays the field's (batch 652: the example, not the note that addresses change).
            TestAssert.True(page.Contains("<p class=problem>", StringComparison.Ordinal) &&
                page.Contains("<p class=hint id=address-hint>For example 203.0.113.7 or yourname.duckdns.org.", StringComparison.Ordinal) &&
                !page.Contains("<p class=tip>For example", StringComparison.Ordinal), "the internet choices: the card is a problem: " + page);
            await SavedAsync(browser, "/host/setup/address", ("choice", "lan"), ("lan", "192.168.1.20"));
            page = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(page.Contains("<p class=tip>Only for friends outside your home network: ", StringComparison.Ordinal) &&
                page.Contains("(A-NET-07)", StringComparison.Ordinal) && !page.Contains("<p class=problem>", StringComparison.Ordinal), "home network only: the card is a tip: " + page);
            router = IPAddress.Parse("203.0.113.7");
            await SavedAsync(browser, "/host/setup/address", ("choice", "router"));
            router = null;
            TestAssert.True((await browser.GetStringAsync("/host/setup/address")).Contains("value=typed checked>I type my address or a name for it", StringComparison.Ordinal),
                "without a router the step asks the host to type the address");
            await RefusedAsync(browser, "/host/setup/address", "Your router did not answer", ("choice", "router"));
            await ExpectAddressAsync(browser, home, "203.0.113.7", HostAddress.Router, pin, 1);
            TestAssert.Equal(0, lookups, "no choice but the lookup asks the outside service");

            // The lookup: once per press, and only an answer that is a public IPv4 address.
            await SavedAsync(browser, "/host/setup/address", ("choice", "lookup"));
            TestAssert.Equal(1, lookups, "one press, one request");
            await ExpectAddressAsync(browser, home, "198.51.100.20", HostAddress.Lookup, pin, 1);
            foreach (var (bad, says) in new (string?, string)[]
                     { ("<html>busy</html>", "cannot use"), (TestHost, "cannot use"), ("10.1.2.3", "home network"), (null, "could not ask") })
            {
                answer = bad;
                await RefusedAsync(browser, "/host/setup/address", says, ("choice", "lookup"));
            }
            TestAssert.Equal(5, lookups, "each press asks once");
            await ExpectAddressAsync(browser, home, "198.51.100.20", HostAddress.Lookup, pin, 1);

            // The saved address is the next start's too.
            home.Loop.Restart();
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(browser, "ready", run);
            await ExpectAddressAsync(browser, home, "198.51.100.20", HostAddress.Lookup, pin, 2);
            using (var next = await browser.GetAsync(HostSteps.Start))
                TestAssert.Equal("/host/setup/login", next.Headers.Location?.OriginalString, "the address saved, the host's login is still to do");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.True(certificate.AsSpan().SequenceEqual(File.ReadAllBytes(pfx)), "the --home start replaced an existing host.pfx");
        TestAssert.Equal(catalogLoaded, ChampionCatalogLoaded(), "the address step leaves the champion list unloaded");
        Delete(home.Root);
    }

    [Check("server.host-first-run-steps", TimeoutSeconds = 180)]
    public static void TheFirstRunNamesTheServerThenMakesTheHostsLogin() => FirstRunAsync().GetAwaiter().GetResult();

    private static async Task FirstRunAsync()
    {
        foreach (var name in new[] { "Friday League", "A", "League.2011-eu_1" })
            TestAssert.Equal(null, HostAddress.NameProblem(name), name);
        foreach (var name in new[] { "", " Friday", "Friday ", new string('x', 41), "CON", "con.txt", "Friday/League", "Friday'League", "Über" })
            TestAssert.True(HostAddress.NameProblem(name) is not null, name + " is refused (a desktop file name)");

        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // The login step's account code reads the champion list. Load it from where every check reads it before the full Platform
        // points AIR_CLIENT_ROOT at this check's empty AIR folder (server.* checks run one at a time).
        TestAssert.True(AirGameDtos.ChampionNames.Count > 0, "the champion list loads");
        var home = Home.Create("league-host-steps");
        var settings = home.Loop.Paths.Settings;
        File.WriteAllText(settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
        }));
        const string password = "Amber line test 626";
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "setup", run);
            await home.SignInAsync(browser);
            await ExpectNextAsync(browser, "/host/setup/name");
            var page = await browser.GetStringAsync("/host/setup/name");
            TestAssert.True(page.Contains("Welcome to Amberline", StringComparison.Ordinal) && page.Contains("Name your server", StringComparison.Ordinal), page);
            var before = File.ReadAllText(settings);
            using (var forged = await browser.PostAsync("/host/setup/name", new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Friday League" })))
                TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "the form needs its antiforgery token");
            await RefusedAsync(browser, "/host/setup/name", "A server name has 1 to 40", ("name", "Friday/League"));
            TestAssert.Equal(before, File.ReadAllText(settings), "a refused name saves nothing");
            await SavedAsync(browser, "/host/setup/name", ("name", " Friday League "));
            TestAssert.Equal("Friday League", Setting(settings, "serverName"));
            await ExpectNextAsync(browser, "/setup");
            TestAssert.True((await browser.GetStringAsync("/host/setup/name")).Contains("value=\"Friday League\"", StringComparison.Ordinal),
                "the step shows what was saved");

            // The folder step (HostFolders, setup mode's own form) links Back to the name, keeps every first-run answer (here also an
            // address and a login id saved earlier) and starts the full Platform.
            TestAssert.True((await browser.GetStringAsync("/setup")).Contains("<a href=/host/setup/name>Back</a>", StringComparison.Ordinal),
                "the folder step links Back to the name step");
            var seeded = JsonNode.Parse(File.ReadAllText(settings))!;
            seeded["publicHost"] = TestHost;
            seeded["addressSource"] = HostAddress.Typed;
            seeded["hostAccount"] = 999_999;
            File.WriteAllText(settings, seeded.ToJsonString());
            using (var folders = await PostAsync(browser, "/setup", ("game", home.Game), ("air", home.Air)))
                TestAssert.Equal(HttpStatusCode.OK, folders.StatusCode, await folders.Content.ReadAsStringAsync());
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(browser, "ready", run);
            TestAssert.Equal("Friday League", Setting(settings, "serverName"), "the folder step keeps the name");
            TestAssert.Equal(TestHost, Setting(settings, "publicHost"), "the folder step keeps the address");
            TestAssert.Equal(HostAddress.Typed, Setting(settings, "addressSource"), "the folder step keeps where the address came from");
            using (var json = JsonDocument.Parse(File.ReadAllText(settings)))
                TestAssert.Equal(999_999, json.RootElement.GetProperty("hostAccount").GetInt32(), "the folder step keeps the login id");
            TestAssert.Equal(home.Game, Setting(settings, "dataRoot"), "the folder step saved the game folder");
            var airSaved = Setting(settings, "airRoot");
            TestAssert.True(!string.IsNullOrEmpty(airSaved), "the folder step saved the AIR folder");
            // Back to no address, so the walk goes on to the address step; the id names no account, so the login step is next.
            var unseeded = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
            unseeded.Remove("publicHost");
            unseeded.Remove("addressSource");
            File.WriteAllText(settings, unseeded.ToJsonString());
            await ExpectNextAsync(browser, "/host/setup/login");
            page = await browser.GetStringAsync("/host/setup/login");
            // Batch 646: no Back (the folder step before it belonged to setup mode, which has ended); the page says where to change them.
            TestAssert.True(page.Contains("Your own login", StringComparison.Ordinal) && page.Contains("<button type=submit>Continue</button>", StringComparison.Ordinal) &&
                !page.Contains(">Back</a>", StringComparison.Ordinal) && page.Contains("Settings changes them later.", StringComparison.Ordinal), page);

            using (var weak = await PostAsync(browser, "/host/setup/login", ("username", "host_test"), ("summoner", "Host Test"), ("password", "Zq9#short"), ("mode", "fresh")))
            {
                TestAssert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
                var text = await weak.Content.ReadAsStringAsync();
                TestAssert.True(text.Contains("Choose a password of at least 10 characters.", StringComparison.Ordinal) && text.Contains("value=\"host_test\"", StringComparison.Ordinal) &&
                    text.Contains("value=\"Host Test\"", StringComparison.Ordinal) && text.Contains("value=fresh checked", StringComparison.Ordinal), text);
                TestAssert.False(text.Contains("Zq9#short", StringComparison.Ordinal), "a refused form never shows the password back");
            }
            // Batch 649: every problem at once. Each field says its own problem, the summary links to it, and the password is never shown back.
            using (var several = await PostAsync(browser, "/host/setup/login", ("username", "a b"), ("summoner", ""), ("password", "Zq9#short"), ("mode", "fresh")))
            {
                TestAssert.Equal(HttpStatusCode.BadRequest, several.StatusCode);
                var text = await several.Content.ReadAsStringAsync();
                foreach (var (field, problem) in new[]
                         {
                             ("username", "Choose a username of 3 to 24 letters (a to z), digits or _, without spaces."),
                             ("summoner", "Choose a summoner name of 1 to 27 letters (a to z), digits, spaces or common symbols."),
                             ("password", "Choose a password of at least 10 characters."),
                         })
                    TestAssert.True(text.Contains($"<li><a href=#{field}>{problem}</a></li>", StringComparison.Ordinal) &&
                        text.Contains($"id={field}-error><span class=vh>Error: </span>{problem}</p>", StringComparison.Ordinal), $"{field}: {text}");
                TestAssert.False(text.Contains("Zq9#short", StringComparison.Ordinal), "a refused form never shows the password back");
            }
            await SavedAsync(browser, "/host/setup/login", ("username", "host_test"), ("summoner", "Host Test"), ("password", password), ("mode", "fresh"));
            using (var json = JsonDocument.Parse(File.ReadAllText(settings)))
            {
                TestAssert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("hostAccount").ValueKind, "platform.json names the host's login by id");
                TestAssert.True(json.RootElement.GetProperty("hostAccount").GetInt64() != 999_999, "the new login's id replaced the old one");
            }
            TestAssert.Equal(home.Game, Setting(settings, "dataRoot"), "the login step keeps the folders");
            TestAssert.Equal(airSaved, Setting(settings, "airRoot"), "the login step keeps the folders");
            using (var login = await LoginAsync(browser, "host_test", password))
                TestAssert.Equal(HttpStatusCode.OK, login.StatusCode, "the League client's sign-in takes the new login");
            using (var wrong = await LoginAsync(browser, "host_test", password + "!"))
                TestAssert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            // A form left open in another tab (the step now shows no form; any page's token is this browser's).
            var token = Token(await browser.GetStringAsync("/host/setup/name"));
            using (var stale = await browser.PostAsync("/host/setup/login", new FormUrlEncodedContent(new Dictionary<string, string>
                   {
                       [token.Key] = token.Value, ["username"] = "host_two", ["summoner"] = "Host Two", ["password"] = password, ["mode"] = "maxed",
                   })))
                TestAssert.Equal(HostSteps.Start, stale.Headers.Location?.OriginalString, "the done step sends the host on");
            using (var second = await LoginAsync(browser, "host_two", password))
                TestAssert.Equal(HttpStatusCode.Forbidden, second.StatusCode, "a second form makes no second login");
            page = await browser.GetStringAsync("/host/setup/login");
            TestAssert.True(page.Contains("Your login <b>host_test</b> is ready.", StringComparison.Ordinal) && !page.Contains(password, StringComparison.Ordinal) &&
                !page.Contains(">Back</a>", StringComparison.Ordinal), page);
            await ExpectNextAsync(browser, "/host/setup/address");
            // Every step done (the address put back): the steps end on the dashboard (batch 628, H7).
            var done = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
            done["publicHost"] = "203.0.113.7";
            File.WriteAllText(settings, done.ToJsonString());
            await ExpectNextAsync(browser, HostDashboard.PagePath);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        // The password is only hashed (PlatformAccounts): nothing in the data folder or the window holds it.
        var secret = Encoding.UTF8.GetBytes(password);
        foreach (var file in Directory.GetFiles(home.Loop.Paths.Data, "*", SearchOption.AllDirectories))
            TestAssert.True(File.ReadAllBytes(file).AsSpan().IndexOf(secret) < 0, "the password is in " + Path.GetFileName(file));
        TestAssert.False(home.Screen.ToString().Contains(password, StringComparison.Ordinal), "the window shows the password");
        Delete(home.Root);
    }

    private static async Task ExpectAddressAsync(HttpClient client, Home home, string host, string source, string pin, int starts)
    {
        using (var manifest = JsonDocument.Parse(await client.GetStringAsync("/bootstrap/client.json")))
        {
            TestAssert.Equal(host, manifest.RootElement.GetProperty("host").GetString(), "client.json follows the saved address");
            TestAssert.Equal(pin, manifest.RootElement.GetProperty("certificateSha256").GetString(), "the kept certificate's pin");
            TestAssert.Equal("Test Server", manifest.RootElement.GetProperty("serverName").GetString(), "client.json names the server");
        }
        TestAssert.Equal(host, Setting(home.Loop.Paths.Settings, "publicHost"));
        TestAssert.Equal(source, Setting(home.Loop.Paths.Settings, "addressSource"));
        TestAssert.Equal(home.Game, Setting(home.Loop.Paths.Settings, "dataRoot"), "the address step keeps the folders");
        TestAssert.Equal(starts, home.Loop.Starts, "a new address needs no restart");
    }

    private static async Task ExpectNextAsync(HttpClient client, string next)
    {
        using var answer = await client.GetAsync(HostSteps.Start);
        TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        TestAssert.Equal(next, answer.Headers.Location?.OriginalString, "the next first-run step");
    }

    /// <summary>Posts the step's form with a fresh antiforgery token from its page, as the browser would.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, params (string Name, string Value)[] fields)
    {
        var token = Token(await client.GetStringAsync(path));
        var form = new Dictionary<string, string> { [token.Key] = token.Value };
        foreach (var (name, value) in fields) form[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static async Task SavedAsync(HttpClient client, string path, params (string Name, string Value)[] fields)
    {
        using var answer = await PostAsync(client, path, fields);
        TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, await answer.Content.ReadAsStringAsync());
        TestAssert.Equal(HostSteps.Start, answer.Headers.Location?.OriginalString, "a saved step goes on to the next one");
    }

    private static async Task RefusedAsync(HttpClient client, string path, string says, params (string Name, string Value)[] fields)
    {
        using var answer = await PostAsync(client, path, fields);
        var text = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.BadRequest, answer.StatusCode, text);
        TestAssert.True(text.Contains(says, StringComparison.Ordinal), $"{path} says \"{says}\": {text}");
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string user, string password) =>
        client.PostAsync(Amberline.Platform.Program.LoginQueuePath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["payload"] = $"user={user},password={password}" }));

    private static string? Setting(string settingsFile, string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(settingsFile));
        return json.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static string Pfx(string root) => Path.Combine(root, "platform", "platform-data", "remote", "host.pfx");

    private static void PlayerFile(string root, string? certificate)
    {
        string[] arguments = ["-File", AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "player-file.ps1")), "-Root", root,
            "-PublicHost", TestHost, "-Port", "8391"];
        var result = AirBootstrapChecks.RunPowerShell("pwsh", certificate is null ? arguments : [.. arguments, "-Certificate", certificate]);
        TestAssert.Equal(0, result.ExitCode, result.Output);
    }

    private static bool TwentyYears(X509Certificate2 certificate) =>
        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
        Math.Abs((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow.AddYears(20)).TotalDays) < 2;

    private static string Lifetime(X509Certificate2 certificate) => $"{certificate.NotBefore:u} to {certificate.NotAfter:u}";

    /// <summary>A host.pfx as installs made before batch 626 have it: the Platform's recipe, two years.</summary>
    private static string TwoYearCertificate(string directory)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Amberline host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "host.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }
}
