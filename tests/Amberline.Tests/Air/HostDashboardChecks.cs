using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's dashboard. Every check runs in a temporary folder on free loopback ports: the router is the UPnP checks' fake on
/// loopback, the firewall read-back is a list and the elevation only records its command line, so no check maps a real router port,
/// runs netsh, elevates, opens a browser or writes outside its folder.
/// </summary>
public static class HostDashboardChecks
{
    [Check("server.host-dashboard-status-json", TimeoutSeconds = 180)]
    public static void TheStatusJsonShowsTheNetworkPeopleAndGames() => StatusJsonAsync().GetAwaiter().GetResult();

    private static async Task StatusJsonAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var catalogLoaded = ChampionCatalogLoaded();
        await using var router = await HostUpnpChecks.FakeRouter.StartAsync();
        var firewall = new FakeFirewall();
        var home = Home.Create("league-host-dashboard", network: new HostNetwork(
            cancel => UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3), cancel), firewall.ReadAsync, firewall.RunAsync,
            () => (null, null)));
        home.ChooseFolders();
        var rules = HostFirewall.Rules(Environment.ProcessPath!, home.Loop.Paths.ServerExecutable, home.RemotePort, 5219, 4);
        firewall.Allowed = rules;
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using (var entry = await browser.GetAsync("/open"))
                TestAssert.Equal(HostSteps.Start, entry.Headers.Location?.OriginalString, "the first-run steps first; they end on the dashboard");
            using (var stranger = home.Client())
            using (var refused = await stranger.GetAsync("/host/status.json"))
                TestAssert.Equal(HttpStatusCode.Forbidden, refused.StatusCode, "the status needs the host's session");

            // The start's own test: the firewall rules are missing, the router opened every port and has a public address.
            var status = await WaitForStatusAsync(browser, now => Check(now, "firewall").State != "checking" && Check(now, "router").State != "checking");
            TestAssert.Equal("running", status.GetProperty("state").GetString());
            TestAssert.True(DateTimeOffset.Now - status.GetProperty("since").GetDateTimeOffset() < TimeSpan.FromMinutes(10), "running since this start");
            TestAssert.Equal(new[] { "listening", "firewall", "router", "address", "friend" },
                status.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("id").GetString()).ToArray(), "the network panel's lines");
            TestAssert.Equal(("ok", "Amberline is listening on this PC"), Check(status, "listening"));
            TestAssert.Equal(("bad", "Windows Firewall blocks Amberline."), Check(status, "firewall"));
            TestAssert.Equal(("ok", "Found your router (\"FR-1000\"); it opened the ports for this PC"), Check(status, "router"));
            TestAssert.Equal(("ok", "Your router has a public internet address"), Check(status, "address"));
            TestAssert.Equal("waiting", Check(status, "friend").State);
            TestAssert.Equal(new (string, string?)[] { ("A-NET-02", "firewall") }, Cards(status), "one card, with the firewall button");
            TestAssert.Equal("Address: 198.51.100.20 (from your router)", status.GetProperty("address").GetString());
            TestAssert.Equal("Network: 1 thing needs your attention", status.GetProperty("network").GetString());
            TestAssert.True(Regex.IsMatch(status.GetProperty("headline").GetString()!, @"^Running since \d\d:\d\d\. Friends may not be able to join: see Needs your attention\.$"),
                status.GetProperty("headline").GetString());
            TestAssert.Equal("No friend has connected yet", status.GetProperty("friend").GetString());
            TestAssert.Equal(0, status.GetProperty("online").GetArrayLength(), "nobody signed in");
            TestAssert.Equal((0, 4, 0), (status.GetProperty("gamesRunning").GetInt32(), status.GetProperty("gamesMax").GetInt32(),
                status.GetProperty("games").GetArrayLength()), "no game of 4");
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("guide").ValueKind, "no router guide while the router opened the ports");
            var table = router.Table();
            TestAssert.Equal(new[] { $"TCP {home.RemotePort}", "UDP 5219", "UDP 5220", "UDP 5221", "UDP 5222" },
                table.Keys.Select(key => $"{key.Protocol} {key.Port}").Order(StringComparer.Ordinal).ToArray(), "the sign-in port and one port per game");
            TestAssert.True(table.Values.All(mapping => mapping["NewInternalClient"] == "127.0.0.1" && mapping["NewPortMappingDescription"] == "Amberline"),
                "forwarded to this PC, named Amberline");
            TestAssert.Equal(0, firewall.Commands.Count, "nothing elevated before the host asks");
            using (var answer = await browser.GetAsync("/host/status.json"))
            {
                TestAssert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType);
                TestAssert.Equal("no-store", answer.Headers.CacheControl?.ToString());
                TestAssert.False(answer.Headers.Any(header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)), "no CORS header");
            }

            // The page: one script file of its own origin, allowed on host pages only.
            string page;
            using (var answer = await browser.GetAsync("/host"))
            {
                TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode);
                TestAssert.Equal(HostDashboard.Policy, Header(answer, "Content-Security-Policy"), "script-src 'self' on the dashboard");
                page = await answer.Content.ReadAsStringAsync();
            }
            TestAssert.Equal(1, Regex.Count(page, "<script"), "one script element");
            TestAssert.True(page.Contains("<script src=/host/page.js defer></script>", StringComparison.Ordinal), "the script is a file");
            // Batch 652: the button is Play, and it says at once that the client takes a while.
            TestAssert.True(page.Contains("<form method=post action=/host/play data-wait=\"Starting the game client. This takes up to a minute.\">", StringComparison.Ordinal) &&
                page.Contains("<button>Play</button>", StringComparison.Ordinal), "the dashboard has H12's Play button");
            TestAssert.False(Regex.IsMatch(page, @"\son[a-z]+\s*="), "no inline event handler");
            // Batch 646: Advanced runs page.js too (its Online now table updates in place), under the same policy.
            using (var manage = await browser.GetAsync("/manage"))
                TestAssert.Equal(HostDashboard.Policy, Header(manage, "Content-Security-Policy"), "/manage has the host pages' policy");
            using (var script = await browser.GetAsync("/host/page.js"))
            {
                TestAssert.Equal(HttpStatusCode.OK, script.StatusCode);
                TestAssert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
                var code = await script.Content.ReadAsStringAsync();
                TestAssert.True(code.Contains("'/host/status.json'", StringComparison.Ordinal) && code.Contains("setInterval(refresh, 5000)", StringComparison.Ordinal),
                    "the script refreshes the status");
                TestAssert.False(code.Contains("innerHTML", StringComparison.Ordinal) || code.Contains("eval(", StringComparison.Ordinal), "the script only sets text");
            }
            using (var stranger = home.Client())
            using (var refused = await stranger.GetAsync("/host/page.js"))
                TestAssert.Equal(HttpStatusCode.Forbidden, refused.StatusCode, "the script needs the session too");

            // Allow through Windows Firewall: the one elevated command H4 builds, then the read-back.
            var token = Token(page);
            using (var allow = await browser.PostAsync("/host/firewall", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, allow.StatusCode, "back to the dashboard");
            status = await StatusAsync(browser);
            if (HostFirewall.Refusal(rules) is { } refusal)
            {
                // A folder path the command line cannot carry (for example a user name with accents in the temporary folder).
                TestAssert.Equal(0, firewall.Commands.Count, "a refused path elevates nothing");
                TestAssert.Equal(("bad", refusal.Message), Check(status, "firewall"));
            }
            else
            {
                TestAssert.Equal(1, firewall.Commands.Count, "one Windows prompt");
                TestAssert.Equal(HostFirewall.Command(rules, [], Environment.SystemDirectory), firewall.Commands[0], "the command H4 builds, for both rules");
                TestAssert.Equal(("ok", "Windows Firewall lets friends in"), Check(status, "firewall"));
                TestAssert.Equal("Network: all checks passed", status.GetProperty("network").GetString());
                TestAssert.True(status.GetProperty("headline").GetString()!.EndsWith(". Friends can join.", StringComparison.Ordinal), status.GetProperty("headline").GetString());
                TestAssert.Equal(0, status.GetProperty("attention").GetArrayLength());
            }
            using (var expired = await browser.PostAsync("/host/firewall", Form(new("x", "y"))))
                TestAssert.Equal(HttpStatusCode.BadRequest, expired.StatusCode, "a form without its token does nothing");

            // Test again, now that the router refuses (606) and sits behind the provider's shared address: two cards and the router guide.
            router.Refuse = (action, _) => action == "AddPortMapping" ? 606 : null;
            router.ExternalAddress = "100.64.0.9";
            using (var again = await browser.PostAsync("/host/network", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            status = await StatusAsync(browser);
            TestAssert.Equal(("bad", "Your router does not allow programs to open ports."), Check(status, "router"));
            TestAssert.Equal(("bad", "Your internet provider shares one address among many homes, so friends cannot reach this PC."), Check(status, "address"));
            TestAssert.True(Cards(status).Contains(("A-NET-04", "network")) && Cards(status).Contains(("A-NET-07", null)),
                "the router card with Test again, the shared-address card with no button");
            TestAssert.Equal("Address: 127.0.0.1 (from your settings)", status.GetProperty("address").GetString(), "a shared address is not the friends' address");
            var guide = status.GetProperty("guide").EnumerateArray().Select(step => step.GetString()!).ToArray();
            TestAssert.Equal(5, guide.Length, "the router guide's five steps");
            TestAssert.True(guide[0].Contains("http://127.0.0.1/", StringComparison.Ordinal), guide[0]);
            TestAssert.Equal($"Forward these to this PC, 127.0.0.1: TCP {home.RemotePort} (sign-in) and UDP 5219 to 5222 (games).", guide[2]);
            TestAssert.True(guide[3].Contains("\"FR-1000 port forwarding\"", StringComparison.Ordinal), guide[3]);
            TestAssert.Equal(1, firewall.Commands.Count, "testing again elevates nothing");

            using (var stop = await browser.GetAsync("/host/stop"))
            {
                var question = await stop.Content.ReadAsStringAsync();
                TestAssert.True(question.Contains("<h1>Stop Amberline?</h1>", StringComparison.Ordinal) &&
                    question.Contains("Friends cannot log in until you start Amberline again.", StringComparison.Ordinal), question);
            }
            TestAssert.False(run.IsCompleted, "asking about Stop stops nothing");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(0, router.Table().Count, "Stop removes the router's ports");
        TestAssert.Equal(5, router.Count("DeletePortMapping"));
        foreach (var call in router.Snapshot(router.Calls))
            TestAssert.True(call.StartsWith("127.0.0.1 ", StringComparison.Ordinal), "every router request went to the fake router: " + call);
        TestAssert.Equal(catalogLoaded, ChampionCatalogLoaded(), "the full Platform's start leaves the champion list unloaded");
        Delete(home.Root);
    }

    [Check("server.host-lan-mode-skips-router", TimeoutSeconds = 180)]
    public static void TheHomeNetworkOnlyLeavesTheRouterAlone() => LanModeAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 632: with Only people on my home network (<c>addressSource</c> <c>lan</c>) the dashboard never asks the router (no search,
    /// no mapping, no 5-minute address check), shows no second-router or shared-address card even when the router would report one,
    /// still checks the firewall, and a friend on the home network is a friend reached. Leaving the choice asks the router again;
    /// coming back removes the ports it opened.
    /// </summary>
    private static async Task LanModeAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        await using var router = await HostUpnpChecks.FakeRouter.StartAsync();
        // A router behind the provider's shared address: on the internet choices this is A-NET-07.
        router.ExternalAddress = "100.64.0.9";
        var firewall = new FakeFirewall();
        var home = Home.Create("league-host-lan-mode", network: new HostNetwork(
            cancel => UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3), cancel), firewall.ReadAsync, firewall.RunAsync,
            () => (null, null)));
        var asked = 0;
        home.Loop.Addresses = new HostAddressSources(_ => { Interlocked.Increment(ref asked); return Task.FromResult<IPAddress?>(IPAddress.Parse("203.0.113.7")); },
            _ => throw new InvalidOperationException("the lookup is never asked"), () => IPAddress.Parse("192.168.1.20"));
        home.Loop.AddressEvery = TimeSpan.FromMilliseconds(200);
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
            dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server", publicHost = "192.168.1.20", addressSource = "lan",
        }));
        firewall.Allowed = HostFirewall.Rules(Environment.ProcessPath!, home.Loop.Paths.ServerExecutable, home.RemotePort, 5219, 4);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await WaitForStatusAsync(browser, now => Check(now, "firewall").State != "checking");
            TestAssert.Equal(new[] { "listening", "firewall", "router", "friend" },
                status.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("id").GetString()).ToArray(),
                "no line for the router's internet address");
            TestAssert.Equal(("ok", "Home network only: the router is not needed"), Check(status, "router"));
            TestAssert.Equal(("bad", "Windows Firewall blocks Amberline."), Check(status, "firewall"), "the firewall step still runs");
            TestAssert.Equal(new (string, string?)[] { ("A-NET-02", "firewall") }, Cards(status), "the firewall card only: no router, CGNAT or second-router card");
            TestAssert.Equal("Address: 192.168.1.20 (home network only). Friends must be on the same home network as this PC.",
                status.GetProperty("address").GetString());
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("guide").ValueKind, "no router guide");
            TestAssert.Equal(("waiting", "Waiting for the first friend to start the Play file."), Check(status, "friend"));

            // Several 5-minute periods (200 ms here): the router is never asked for its address, so no A-NET-09 either.
            await Task.Delay(1200);
            TestAssert.Equal(0, asked, "no address check on the home network only");
            TestAssert.Equal(0, router.Snapshot(router.Searches).Length + router.Snapshot(router.Calls).Length, "no router search and no UPnP request");
            TestAssert.False(Cards(await StatusAsync(browser)).Any(card => card.Code == "A-NET-09"), "no address-change banner");

            // A friend on the home network is a friend reached; with the firewall allowed, friends can join.
            home.Loop.Friend.Observe(IPAddress.Parse("192.168.1.31"), "/bootstrap/client.json", DateTimeOffset.UtcNow);
            var page = await browser.GetStringAsync("/host");
            using (var allow = await browser.PostAsync("/host/firewall", Form(Token(page))))
                TestAssert.Equal(HttpStatusCode.Redirect, allow.StatusCode, "back to the dashboard");
            status = await StatusAsync(browser);
            TestAssert.Equal(("ok", "A friend on your home network reached Amberline just now"), Check(status, "friend"));
            if (HostFirewall.Refusal(firewall.Allowed) is null)
            {
                TestAssert.Equal(1, firewall.Commands.Count, "one Windows prompt, as on the internet choices");
                TestAssert.Equal("Network: all checks passed", status.GetProperty("network").GetString());
                TestAssert.True(status.GetProperty("headline").GetString()!.EndsWith(". Friends can join.", StringComparison.Ordinal), status.GetProperty("headline").GetString());
            }

            // Leaving the choice for an internet address asks the router again: it maps the ports and its shared address is A-NET-07.
            using (var typed = await browser.PostAsync("/host/setup/address", Form(Token(page), ("choice", "typed"), ("address", "198.51.100.20"))))
                TestAssert.Equal(HttpStatusCode.Redirect, typed.StatusCode, await typed.Content.ReadAsStringAsync());
            status = await WaitForStatusAsync(browser, now => Check(now, "router").State != "checking" && Check(now, "address").State != "checking");
            TestAssert.True(Check(status, "router").Text.StartsWith("Found your router", StringComparison.Ordinal), Check(status, "router").Text);
            TestAssert.True(Cards(status).Contains(("A-NET-07", null)), "the shared-address card is back on the internet choices");
            TestAssert.Equal(5, router.Table().Count, "the sign-in port and the four game ports");
            TestAssert.Equal(("waiting", "A friend on your home network reached Amberline just now. Waiting for a friend from the internet."), Check(status, "friend"));

            // Back to the home network only: the router's ports are removed, and its address no longer matters.
            using (var lan = await browser.PostAsync("/host/setup/address", Form(Token(page), ("choice", "lan"), ("lan", "192.168.1.20"))))
                TestAssert.Equal(HttpStatusCode.Redirect, lan.StatusCode, await lan.Content.ReadAsStringAsync());
            await HostUpnpChecks.Until(() => router.Table().Count == 0, "the router's ports removed");
            TestAssert.Equal(5, router.Count("DeletePortMapping"));
            status = await StatusAsync(browser);
            TestAssert.Equal(("ok", "Home network only: the router is not needed"), Check(status, "router"));
            TestAssert.False(Cards(status).Any(card => card.Code is "A-NET-06" or "A-NET-07" or "A-NET-03" or "A-NET-04"), "no router card");
            var calls = router.Snapshot(router.Calls).Length;
            using (var again = await browser.PostAsync("/host/network", Form(Token(page))))
                TestAssert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            TestAssert.Equal(calls, router.Snapshot(router.Calls).Length, "Test again on the home network only asks the router nothing");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        foreach (var call in router.Snapshot(router.Calls))
            TestAssert.True(call.StartsWith("127.0.0.1 ", StringComparison.Ordinal), "every router request went to the fake router: " + call);
        Delete(home.Root);
    }

    [Check("server.host-friend-reached-light", TimeoutSeconds = 180)]
    public static void TheFirstFriendTurnsTheLightGreenAndNoAddressIsKept() => FriendLightAsync().GetAwaiter().GetResult();

    private static async Task FriendLightAsync()
    {
        // The light alone: which requests count, and what the state file keeps.
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-friend");
        var data = Path.Combine(root, "data");
        var file = HostFriendLight.StateFile(data);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string Other = """{ "lastAddressChange": "kept" }""";
        File.WriteAllText(file, Other);
        var light = new HostFriendLight(data);
        var t0 = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);
        light.Observe(IPAddress.Loopback, "/bootstrap/client.json", t0);
        light.Observe(IPAddress.IPv6Loopback, AirRemoteTunnel.SignInPath, t0);
        light.Observe(IPAddress.Parse("::ffff:127.0.0.1"), "/bootstrap/client.json", t0);
        light.Observe(IPAddress.Parse("203.0.113.7"), "/", t0);
        light.Observe(IPAddress.Parse("203.0.113.7"), "/store/entry", t0);
        light.Observe(null, "/bootstrap/client.json", t0);
        TestAssert.Equal<DateTimeOffset?>(null, light.FromInternet, "this PC itself, and pages that are not the friends' tools, do not count");
        TestAssert.Equal<DateTimeOffset?>(null, light.FromHomeNetwork);
        TestAssert.Equal(Other, File.ReadAllText(file), "nothing was written for them");

        light.Observe(IPAddress.Parse("192.168.0.23"), "/bootstrap/client.json", t0);
        TestAssert.Equal<DateTimeOffset?>(t0, light.FromHomeNetwork, "a friend on the home network");
        light.Observe(IPAddress.Parse("100.64.0.5"), AirRemoteTunnel.SignInPath, t0.AddSeconds(1));
        light.Observe(IPAddress.Parse("fe80::1"), AirRemoteTunnel.ChatPath, t0.AddSeconds(2));
        TestAssert.Equal<DateTimeOffset?>(null, light.FromInternet, "the home network, a shared provider address and IPv6 link-local are not the internet");
        TestAssert.Equal<DateTimeOffset?>(t0.AddSeconds(2), light.FromHomeNetwork);
        light.Observe(IPAddress.Parse("203.0.113.7"), "/bootstrap/companion.zip", t0.AddSeconds(3));
        TestAssert.Equal<DateTimeOffset?>(t0.AddSeconds(3), light.FromInternet, "a friend from the internet");
        light.Observe(IPAddress.Parse("::ffff:203.0.113.8"), AirRemoteTunnel.Path, t0.AddSeconds(4));
        TestAssert.Equal<DateTimeOffset?>(t0.AddSeconds(4), light.FromInternet, "an IPv4 address in IPv6 form");
        light.Observe(IPAddress.Parse("2001:db8::7"), "/bootstrap/client.json", t0.AddSeconds(5));
        TestAssert.Equal<DateTimeOffset?>(t0.AddSeconds(5), light.FromInternet, "a global IPv6 address");

        var text = File.ReadAllText(file);
        using (var json = JsonDocument.Parse(text))
        {
            TestAssert.Equal("kept", json.RootElement.GetProperty("lastAddressChange").GetString(), "the file's other entries stay");
            // Saved at the first home-network friend (t0) and the first internet friend (t0 + 3 s, with the home time then); not again within a minute.
            TestAssert.Equal(t0.AddSeconds(3), json.RootElement.GetProperty("friendPublicAt").GetDateTimeOffset());
            TestAssert.Equal(t0.AddSeconds(2), json.RootElement.GetProperty("friendLocalAt").GetDateTimeOffset());
            TestAssert.Equal(3, json.RootElement.EnumerateObject().Count(), "a time per kind and nothing else");
        }
        foreach (var address in new[] { "203.0.113", "192.168", "100.64", "2001:db8", "fe80", "ffff" })
            TestAssert.False(text.Contains(address, StringComparison.OrdinalIgnoreCase), "no friend's address is kept: " + address);
        light.Observe(IPAddress.Parse("203.0.113.7"), "/bootstrap/client.json", t0.AddMinutes(2));
        var next = new HostFriendLight(data);
        TestAssert.Equal<DateTimeOffset?>(t0.AddMinutes(2), next.FromInternet, "the next process reads the light back");
        TestAssert.Equal<DateTimeOffset?>(t0.AddSeconds(2), next.FromHomeNetwork);
        File.WriteAllText(file, "{ damaged");
        var damaged = new HostFriendLight(data);
        TestAssert.Equal<DateTimeOffset?>(null, damaged.FromInternet, "a damaged file is an empty light");
        damaged.Observe(IPAddress.Parse("203.0.113.7"), "/bootstrap/client.json", t0);
        using (var json = JsonDocument.Parse(File.ReadAllText(file)))
            TestAssert.Equal(t0, json.RootElement.GetProperty("friendPublicAt").GetDateTimeOffset(), "and is written new");
        Delete(root);

        // The running Platform: the host's own request on the public port is not a friend; a friend's turns the dashboard's light green.
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var catalogLoaded = ChampionCatalogLoaded();
        var home = Home.Create("league-host-friend-page");
        home.ChooseFolders();
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await StatusAsync(browser);
            TestAssert.Equal(("waiting", "Waiting for the first friend to start the Play file."), Check(status, "friend"));
            using (var own = await browser.GetAsync($"https://127.0.0.1:{home.RemotePort}/bootstrap/client.json"))
                TestAssert.Equal(HttpStatusCode.OK, own.StatusCode, "the public port answers the Play file's manifest");
            TestAssert.Equal<DateTimeOffset?>(null, home.Loop.Friend.FromInternet, "a request from this PC is not a friend");
            TestAssert.False(File.Exists(HostFriendLight.StateFile(home.Loop.Paths.Data)), "nothing is saved for it");
            // The check cannot listen on a public interface, so the request a friend's Play file makes is handed to the light directly.
            home.Loop.Friend.Observe(IPAddress.Parse("203.0.113.9"), "/bootstrap/client.json", DateTimeOffset.UtcNow);
            status = await StatusAsync(browser);
            TestAssert.Equal(("ok", "A friend reached Amberline from the internet just now"), Check(status, "friend"));
            TestAssert.Equal("Last friend connected just now", status.GetProperty("friend").GetString());
            TestAssert.False(status.GetRawText().Contains("203.0.113", StringComparison.Ordinal), "the page never shows a friend's address");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.True(new HostFriendLight(home.Loop.Paths.Data).FromInternet is not null, "the next start still knows a friend reached it");
        TestAssert.False(File.ReadAllText(HostFriendLight.StateFile(home.Loop.Paths.Data)).Contains("203.0.113", StringComparison.Ordinal), "no address on disk");
        TestAssert.Equal(catalogLoaded, ChampionCatalogLoaded(), "the full Platform's start leaves the champion list unloaded");
        Delete(home.Root);
    }

    [Check("server.host-stop-confirms-active-match", TimeoutSeconds = 120)]
    public static void StopAndRestartNameTheRunningGamesFirst() => StopConfirmsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The dashboard on a page-only test instance (the gate, the dashboard and the loop, no AIR platform), whose running games are a
    /// list, since a check cannot start a real match.
    /// </summary>
    private static async Task StopConfirmsAsync()
    {
        var home = Home.Create("league-host-stop");
        var games = new List<DashboardGame> { new(4, 2, "Playing") };
        IReadOnlyList<DashboardGame> Running() { lock (games) return [.. games]; }
        var settings = new PlatformSettings(home.Game, home.Loop.Paths.ServerExecutable, HttpPort: home.Port, RemotePort: home.RemotePort, AirRoot: home.Air);
        var run = Task.Run(() => RunPageAsync(home, settings, Running));
        using var client = home.Client();
        await WaitForStartAsync(home.Loop, 1, run);
        await home.SignInAsync(client);

        var page = await PageAsync(client, "/host/stop");
        TestAssert.True(page.Contains("<h1>Stop Amberline?</h1>", StringComparison.Ordinal), page);
        TestAssert.True(page.Contains("1 game is running (4 players). Stopping ends it for everyone.",
            StringComparison.Ordinal), "the warning names the game and its players: " + page);
        // Batch 652: one name for the button whether games run or not, Cancel to leave, and the wait said at once.
        TestAssert.True(page.Contains(">Stop Amberline</button>", StringComparison.Ordinal) && page.Contains(">Cancel</a>", StringComparison.Ordinal) &&
            page.Contains("data-wait=\"Stopping Amberline.\"", StringComparison.Ordinal), page);
        TestAssert.True(page.Contains("name=games value=1", StringComparison.Ordinal), "the answer says how many games it was warned about");
        var token = Token(page);
        using (var bare = await client.PostAsync("/host/stop", Form(new("x", "y"), ("games", "1"))))
        {
            TestAssert.Equal(HttpStatusCode.BadRequest, bare.StatusCode, "a form without its token");
            TestAssert.True((await bare.Content.ReadAsStringAsync()).Contains("The page expired", StringComparison.Ordinal));
        }
        using (var stale = await client.PostAsync("/host/stop", Form(token, ("games", "0"))))
        {
            TestAssert.Equal(HttpStatusCode.Conflict, stale.StatusCode, "a page opened before the game started");
            var again = await stale.Content.ReadAsStringAsync();
            TestAssert.True(again.Contains("A game started since this page was opened.", StringComparison.Ordinal) &&
                again.Contains("1 game is running (4 players).", StringComparison.Ordinal), "the warning comes first: " + again);
        }
        using (var crossSite = new HttpRequestMessage(HttpMethod.Post, "/host/stop") { Content = Form(token, ("games", "1")) })
        {
            crossSite.Headers.TryAddWithoutValidation("Origin", "http://attacker.example");
            TestAssert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode, "another website's form");
        }
        TestAssert.False(run.IsCompleted, "nothing so far stopped Amberline");

        lock (games) games.Add(new(2, 0, "Waiting for players"));
        page = await PageAsync(client, "/host/restart");
        TestAssert.True(page.Contains("<h1>Restart Amberline?</h1>", StringComparison.Ordinal) &&
            page.Contains("2 games are running (6 players). Restarting ends them for everyone.", StringComparison.Ordinal) &&
            page.Contains(">Restart Amberline</button>", StringComparison.Ordinal) &&
            page.Contains("data-wait=\"Restarting Amberline. This takes a few seconds.\"", StringComparison.Ordinal), page);
        using (var restart = await client.PostAsync("/host/restart", Form(Token(page), ("games", "2"))))
        {
            var answer = await restart.Content.ReadAsStringAsync();
            TestAssert.Equal(HttpStatusCode.OK, restart.StatusCode, answer);
            TestAssert.True(answer.Contains("Amberline is restarting", StringComparison.Ordinal) && answer.Contains("http-equiv=refresh", StringComparison.Ordinal),
                "the page comes back by itself: " + answer);
        }
        TestAssert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)), "a confirmed restart asks the loop for the next start");

        lock (games) games.Clear();
        run = Task.Run(() => RunPageAsync(home, settings, Running));
        await WaitForStartAsync(home.Loop, 2, run);
        page = await PageAsync(client, "/host/stop");
        TestAssert.True(page.Contains("Friends cannot log in until you start Amberline again.", StringComparison.Ordinal) &&
            !page.Contains("running (", StringComparison.Ordinal) && page.Contains(">Stop Amberline</button>", StringComparison.Ordinal),
            "no game, no game warning (the restart kept the host signed in): " + page);
        using (var stop = await client.PostAsync("/host/stop", Form(Token(page), ("games", "0"))))
        {
            var answer = await stop.Content.ReadAsStringAsync();
            TestAssert.Equal(HttpStatusCode.OK, stop.StatusCode, answer);
            TestAssert.True(answer.Contains("<h1>Amberline has stopped</h1><p>To start it again, double-click Amberline on your desktop.</p>",
                StringComparison.Ordinal), answer);
        }
        TestAssert.False(await run.WaitAsync(TimeSpan.FromSeconds(30)), "a confirmed stop leaves the loop");
        TestAssert.False(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == home.Port), "Stop releases the page's port");
        Delete(home.Root);
    }

    /// <summary>A page-only start: the gate and the dashboard on the home's page port, run by the home's loop until Stop or Restart.</summary>
    private static async Task<bool> RunPageAsync(Home home, PlatformSettings settings, Func<IReadOnlyList<DashboardGame>> games)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, home.Port));
        builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(home.Loop.Paths.Keys));
        await using var app = builder.Build();
        HostGate.Use(app, home.Port, home.Loop.Sessions, HostDashboard.PagePath);
        await using var dashboard = new HostDashboard(home.Loop, settings, () => [], games);
        dashboard.Map(app);
        return await home.Loop.RunAsync(app, home.Port);
    }

    private static async Task WaitForStartAsync(HostLoop loop, int starts, Task run)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (loop.Starts < starts && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (run.IsFaulted) await run;
        TestAssert.Equal(starts, loop.Starts, "the page started");
    }

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        using var answer = await client.GetAsync(path);
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, path + ": " + page);
        return page;
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var answer = await client.GetAsync("/host/status.json");
        var text = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, text);
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
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the network test never finished: " + status.GetRawText());
            await Task.Delay(100);
        }
    }

    private static (string State, string Text) Check(JsonElement status, string id)
    {
        var check = status.GetProperty("checks").EnumerateArray().Single(line => line.GetProperty("id").GetString() == id);
        return (check.GetProperty("state").GetString()!, check.GetProperty("text").GetString()!);
    }

    private static (string Code, string? Action)[] Cards(JsonElement status) =>
        [.. status.GetProperty("attention").EnumerateArray().Select(card => (card.GetProperty("code").GetString()!, card.GetProperty("action").GetString()))];

    private static string Header(HttpResponseMessage answer, string name) =>
        answer.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    private static FormUrlEncodedContent Form(KeyValuePair<string, string> token, params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)).Prepend(token));

    /// <summary>The host's browser, also on the public port, whose certificate is this temporary host's own.</summary>
    private static HttpClient Browser(Home home) => new(PageDump.Wrap(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })) { BaseAddress = new Uri($"http://127.0.0.1:{home.Port}"), Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The firewall read-back as a list: no Amberline rule until the elevation ran, then both rules as netsh prints them.</summary>
    private sealed class FakeFirewall
    {
        private volatile bool _added;
        public IReadOnlyList<FirewallRule> Allowed { get; set; } = [];
        public List<ElevatedCommand> Commands { get; } = [];

        public Task<IReadOnlyList<ShownRule>?> ReadAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<ShownRule>?>(_added
            ? [.. Allowed.Select(rule => new ShownRule(rule.Name, new Dictionary<string, string>
            {
                ["Enabled"] = "Yes", ["Direction"] = "In", ["Profiles"] = "Domain,Private,Public", ["Action"] = "Allow",
                ["Program"] = rule.Program, ["Protocol"] = rule.Protocol, ["LocalPort"] = rule.LocalPort,
            }))]
            : []);

        public Task<int?> RunAsync(ElevatedCommand command, CancellationToken cancel)
        {
            lock (Commands) Commands.Add(command);
            _added = true;
            return Task.FromResult<int?>(0);
        }
    }
}
