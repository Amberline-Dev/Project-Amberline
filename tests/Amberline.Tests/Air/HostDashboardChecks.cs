using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RtmpSharp.IO;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's dashboard. Every check runs in a temporary folder on free loopback ports: the router is the UPnP checks' fake on
/// loopback, the firewall read-back is a list and the elevation only records its command line, so no check maps a real router port,
/// runs netsh, elevates, opens a browser or writes outside its folder.
/// </summary>
public static class HostDashboardChecks
{
    private const string Password = "Synthetic-dashboard-Pw-7";

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
            // Final fix 5: Copy address (and the sign-up link) give the saved address, the one Users, Settings, Add a user and the Play file
            // give, not the router's public one.
            TestAssert.Equal("127.0.0.1", status.GetProperty("publicHost").GetString(), "Copy address copies the saved address");
            TestAssert.Equal("Network: 1 thing needs your attention", status.GetProperty("network").GetString());
            TestAssert.True(Regex.IsMatch(status.GetProperty("headline").GetString()!, @"^Running since \d\d:\d\d\. Players may not be able to join: see Needs your attention\.$"),
                status.GetProperty("headline").GetString());
            TestAssert.Equal("No player has connected yet", status.GetProperty("friend").GetString());
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
                TestAssert.Equal(("ok", "Windows Firewall lets players in"), Check(status, "firewall"));
                TestAssert.Equal("Network: all checks passed", status.GetProperty("network").GetString());
                TestAssert.True(status.GetProperty("headline").GetString()!.EndsWith(". Players can join.", StringComparison.Ordinal), status.GetProperty("headline").GetString());
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
            TestAssert.Equal(("bad", "Your internet provider shares one address among many homes, so players cannot reach this PC."), Check(status, "address"));
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
                    question.Contains("Users cannot log in until you start Amberline again.", StringComparison.Ordinal), question);
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

    [Check("server.host-dashboard-hide-card", TimeoutSeconds = 180)]
    public static void AnAttentionCardHidesAndComesBackOnlyWhenItsProblemChanges() => HideCardAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 751: every Needs-your-attention card has Hide. The router answers with no internet address (A-NET-03) and Windows cannot read
    /// the firewall rules (A-NET-02, Unreadable). A hidden card stays hidden through a new dashboard on the same folder, is forgotten by a
    /// pass that does not raise it, and comes back for a different problem; the network line counts it; Show them clears the set.
    /// </summary>
    private static async Task HideCardAsync()
    {
        await using var router = await HostUpnpChecks.FakeRouter.StartAsync();
        var firewall = new HideFirewall();
        var home = Home.Create("league-host-hide-card", network: new HostNetwork(
            cancel => UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3), cancel), firewall.ReadAsync,
            (_, _) => Task.FromResult<int?>(null), () => (null, null)));
        var stateFile = HostFriendLight.StateFile(home.Loop.Paths.Data);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, """{"keptKey": "kept"}""");
        router.ExternalAddress = "";
        var settings = new PlatformSettings(home.Game, home.Loop.Paths.ServerExecutable, HttpPort: home.Port, RemotePort: home.RemotePort, AirRoot: home.Air);
        var run = Task.Run(() => RunDashboardAsync(home, settings));
        try
        {
            using var client = home.Client();
            await WaitForStartAsync(home.Loop, 1, run);
            await home.SignInAsync(client);

            // The first pass: the firewall cannot be read, and the router has no internet address (A-NET-03 on the address line).
            var status = await WaitForStatusAsync(client, now => Check(now, "firewall").State != "checking" && Check(now, "address").State != "checking");
            var page = await PageAsync(client, HostDashboard.PagePath);
            var token = Token(page);

            // Batch 751 final review: the router that answers without an internet address (A-NET-03 on the address line) and the router that
            // does not answer (A-NET-03 on the router line) are two problems. Hiding the first never hides the second, nor its warning.
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-03"), ("subject", CardSubject(status, "A-NET-03")!))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the no-address card");
            router.Answer = false;
            await using (var silent = new HostDashboard(home.Loop, settings, () => [], () => []))
            {
                await silent.Network.TestAsync(CancellationToken.None);
                var shown = silent.Status(DateTimeOffset.Now);
                TestAssert.Equal("Your router did not answer. It may have UPnP switched off.", shown.Attention.SingleOrDefault(card => card.Code == "A-NET-03")?.Message,
                    "the router that does not answer shows although the no-address card was hidden");
                TestAssert.Equal("Network: 1 thing needs your attention", shown.Network, "and it counts as a problem");
            }
            router.Answer = true;
            using (var show = await client.PostAsync(HostDashboard.PagePath + "/show-hidden", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, show.StatusCode, "Show them");

            TestAssert.Equal("Unreadable", CardSubject(status, "A-NET-02"), "the firewall card names its state");
            TestAssert.Equal("address", CardSubject(status, "A-NET-03"), "the router card names its line");
            TestAssert.True(page.Contains("<template id=hide-template>", StringComparison.Ordinal) && page.Contains($"action={HostDashboard.PagePath}/hide", StringComparison.Ordinal) &&
                page.Contains($"action={HostDashboard.PagePath}/show-hidden", StringComparison.Ordinal), "every card has Hide, and Show them is on the page");

            // The route refuses a form without its token, a card that keeps its own Hide (A-NET-09), and a card that is not named.
            using (var bare = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(new("x", "y"), ("code", "A-NET-03"), ("subject", "A-NET-03"))))
                TestAssert.Equal(HttpStatusCode.BadRequest, bare.StatusCode, "a form without its token");
            using (var own = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-09"), ("subject", "x"))))
                TestAssert.Equal(HttpStatusCode.BadRequest, own.StatusCode, "A-NET-09 keeps its own Hide");

            // Hide the router card: it goes, the firewall card stays, the network line says 1 hidden, and the headline keeps its warning.
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-03"), ("subject", "address"))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the router card");
            status = await StatusAsync(client);
            TestAssert.False(HasCard(status, "A-NET-03"), "the hidden card is gone");
            TestAssert.True(HasCard(status, "A-NET-02"), "the firewall card stays");
            TestAssert.Equal("Network: 1 hidden", status.GetProperty("network").GetString(), "the network line counts the hidden card");
            TestAssert.True(status.GetProperty("headline").GetString()!.EndsWith(" Players may not be able to join: see Needs your attention.", StringComparison.Ordinal),
                "the firewall card still warns: " + status.GetProperty("headline").GetString());
            TestAssert.Equal("1 message hidden.", status.GetProperty("hidden").GetString(), "the count below the list");
            TestAssert.Equal(new[] { "A-NET-03:address" }, HiddenEntries(stateFile), "the hidden identity is saved by code and subject");
            TestAssert.Equal("kept", JsonNode.Parse(File.ReadAllText(stateFile))!["keptKey"]!.GetValue<string>(), "the file's other keys stay");

            // A new dashboard on the same folder: the router card stays hidden, the firewall card shows.
            await using (var reloaded = new HostDashboard(home.Loop, settings, () => [], () => []))
            {
                await reloaded.Network.TestAsync(CancellationToken.None);
                var again = reloaded.Status(DateTimeOffset.Now);
                TestAssert.False(again.Attention.Any(card => card.Code == "A-NET-03"), "the hidden card survives a restart");
                TestAssert.True(again.Attention.Any(card => card.Code == "A-NET-02"), "the firewall card shows after a restart");
            }

            // A pass where the router answers with its address: the card is not raised, so its hidden entry goes.
            router.ExternalAddress = "198.51.100.20";
            using (var answer = await client.PostAsync(HostDashboard.PagePath + "/network", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, "Test again");
            status = await StatusAsync(client);
            TestAssert.False(HasCard(status, "A-NET-03"), "no router card while the address is public");
            TestAssert.Equal(Array.Empty<string>(), HiddenEntries(stateFile), "a pass that does not raise the card forgets its hidden entry");

            // The same problem again, in a later pass: it shows.
            router.ExternalAddress = "";
            using (var answer = await client.PostAsync(HostDashboard.PagePath + "/network", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, "Test again");
            status = await StatusAsync(client);
            TestAssert.Equal("address", CardSubject(status, "A-NET-03"), "the same problem coming back shows again");
            TestAssert.Equal("Network: 1 thing needs your attention", status.GetProperty("network").GetString(), "the problem count is back");

            // Hide both cards: no warning, the network line says 2 hidden; Show them brings both back.
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-03"), ("subject", "address"))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the router card again");
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-02"), ("subject", "Unreadable"))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the firewall card");
            status = await StatusAsync(client);
            TestAssert.True(Regex.IsMatch(status.GetProperty("headline").GetString()!, @"^Running since \d\d:\d\d\.$"), "no warning when every problem is hidden: " + status.GetProperty("headline").GetString());
            TestAssert.Equal("Network: 2 hidden", status.GetProperty("network").GetString(), "the network line says 2 hidden");
            TestAssert.Equal("2 messages hidden.", status.GetProperty("hidden").GetString(), "two messages hidden");
            using (var show = await client.PostAsync(HostDashboard.PagePath + "/show-hidden", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, show.StatusCode, "Show them");
            status = await StatusAsync(client);
            TestAssert.True(HasCard(status, "A-NET-02") && HasCard(status, "A-NET-03"), "Show them brings both cards back");
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("hidden").ValueKind, "nothing hidden");
            TestAssert.Equal(Array.Empty<string>(), HiddenEntries(stateFile), "Show them clears the saved set");

            // The firewall card is hidden as Unreadable; a pass that reads Blocked shows the Blocked card, and the Unreadable entry goes.
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-02"), ("subject", "Unreadable"))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the unreadable firewall");
            firewall.Shown = [new ShownRule("Amberline block", new Dictionary<string, string>
            {
                ["Enabled"] = "Yes", ["Direction"] = "In", ["Action"] = "Block", ["Program"] = Environment.ProcessPath!,
            })];
            using (var answer = await client.PostAsync(HostDashboard.PagePath + "/network", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, "Test again");
            status = await StatusAsync(client);
            TestAssert.Equal("Blocked", CardSubject(status, "A-NET-02"), "the blocked firewall shows although Unreadable was hidden");
            TestAssert.Equal(Array.Empty<string>(), HiddenEntries(stateFile), "the Unreadable entry is forgotten");

            // A-FILES-06's subject is a digest of the app folder and the problem's kind (batch 751 merge-sweep fix: never the folder, which the
            // status JSON would show); a subject past 1024 characters is refused.
            var filesSubject = HostStartChecks.FilesSubject(Path.Combine(home.Loop.Paths.App, ".."), "missing");
            using (var files = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-FILES-06"), ("subject", filesSubject))))
                TestAssert.Equal(HttpStatusCode.Redirect, files.StatusCode, "the app folder's card can be hidden");
            using (var tooLong = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-FILES-06"), ("subject", new string('p', 1025)))))
                TestAssert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode, "a subject past the limit is refused");
            TestAssert.Equal(new[] { "A-FILES-06:" + filesSubject }, HiddenEntries(stateFile), "the subject is saved");
            // The folder has no problem now, so the next pass forgets its entry (A-FILES-06 ran and raised nothing).
            status = await StatusAsync(client);
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("hidden").ValueKind, "a folder with no problem is forgotten by the next pass");
            using (var show = await client.PostAsync(HostDashboard.PagePath + "/show-hidden", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, show.StatusCode, "Show them clears it");

            // The owner's rented server: the router does not answer at all (no router to find), so the address line is unknown without a card.
            // Hiding the firewall card and the router card leaves no warning and says 2 hidden.
            firewall.Shown = null;
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-02"), ("subject", "Unreadable"))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the firewall card (router not found)");
            using (var hide = await client.PostAsync(HostDashboard.PagePath + "/hide", Form(token, ("code", "A-NET-03"), ("subject", "router"))))
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, "Hide the router card (router not found)");
            // A dashboard that finds no router (it has none yet: discovery is silent), so its address line is unknown with no card.
            router.Answer = false;
            await using (var silent = new HostDashboard(home.Loop, settings, () => [], () => []))
            {
                await silent.Network.TestAsync(CancellationToken.None);
                var quiet = silent.Status(DateTimeOffset.Now);
                TestAssert.Equal(0, quiet.Attention.Length, "both cards are hidden; the address line has no card");
                TestAssert.Equal("Network: 2 hidden", quiet.Network, "the network line says 2 hidden");
                TestAssert.True(Regex.IsMatch(quiet.Headline, @"^Running since \d\d:\d\d\.$"), "no warning while every card is hidden (router not found): " + quiet.Headline);
            }
            router.Answer = true;
            using (var show = await client.PostAsync(HostDashboard.PagePath + "/show-hidden", Form(token)))
                TestAssert.Equal(HttpStatusCode.Redirect, show.StatusCode, "Show them again");

            // Batch 751 final review: hiding "Play files stop working on <date>" never hides "stopped working", a different problem (the
            // subject names the certificate and its state); the warning's entry is forgotten once the certificate has run out.
            using (var key = ECDsa.Create())
            using (var certificate = new CertificateRequest("CN=hide-check", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)))
            {
                var ends = new DateTimeOffset(certificate.NotAfter);
                var unstarted = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }).Build();
                await using (unstarted)
                await using (var later = new HostDashboard(home.Loop, settings, () => [], () => []))
                {
                    later.Map(unstarted, certificate: certificate);
                    var soon = later.Status(ends - TimeSpan.FromDays(10)).Attention.Single(card => card.Code == "A-CERT-01");
                    later.Hidden.Add(soon.Code, soon.Subject);
                    TestAssert.False(later.Status(ends - TimeSpan.FromDays(9)).Attention.Any(card => card.Code == "A-CERT-01"), "the warning is hidden");
                    TestAssert.Equal("Your users' Play files stopped working: Amberline's certificate ran out.",
                        later.Status(ends + TimeSpan.FromDays(1)).Attention.SingleOrDefault(card => card.Code == "A-CERT-01")?.Message,
                        "the certificate that ran out shows although the warning was hidden");
                    TestAssert.Equal(Array.Empty<string>(), HiddenEntries(stateFile), "the warning's entry is forgotten");
                }
            }

            home.Loop.Stop();
            TestAssert.False(await run.WaitAsync(TimeSpan.FromSeconds(30)), "the page stopped");
        }
        finally
        {
            if (!run.IsCompleted) home.Loop.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Delete(home.Root);
    }

    /// <summary>The firewall read-back for the hide check: unreadable (null) until a list is set.</summary>
    private sealed class HideFirewall
    {
        public IReadOnlyList<ShownRule>? Shown { get; set; }

        public Task<IReadOnlyList<ShownRule>?> ReadAsync(CancellationToken cancel) => Task.FromResult(Shown);
    }

    private static async Task<bool> RunDashboardAsync(Home home, PlatformSettings settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, home.Port));
        builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(home.Loop.Paths.Keys));
        await using var app = builder.Build();
        HostGate.Use(app, home.Port, home.Loop.Sessions, HostDashboard.PagePath);
        HostPages.Use(app, home.Loop, () => "Test Server", nav: true);
        await using var dashboard = new HostDashboard(home.Loop, settings, () => [], () => []);
        dashboard.Map(app);
        return await home.Loop.RunAsync(app, home.Port);
    }

    /// <summary>The card's subject for a code, or null when no card has it.</summary>
    private static string? CardSubject(JsonElement status, string code) =>
        status.GetProperty("attention").EnumerateArray().Where(card => card.GetProperty("code").GetString() == code)
            .Select(card => card.GetProperty("subject").GetString()).FirstOrDefault();

    private static bool HasCard(JsonElement status, string code) =>
        status.GetProperty("attention").EnumerateArray().Any(card => card.GetProperty("code").GetString() == code);

    /// <summary>The saved hidden identities, each as "code:subject"; none when the key or the file is missing.</summary>
    private static string[] HiddenEntries(string stateFile)
    {
        var state = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        return state["hiddenCards"] is JsonArray entries
            ? [.. entries.Select(entry => $"{entry!["code"]!.GetValue<string>()}:{entry["subject"]!.GetValue<string>()}")]
            : [];
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
            TestAssert.Equal("Address: 192.168.1.20 (home network only). Players must be on the same home network as this PC.",
                status.GetProperty("address").GetString());
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("guide").ValueKind, "no router guide");
            TestAssert.Equal(("waiting", "Waiting for the first player to start the Play file."), Check(status, "friend"));

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
            TestAssert.Equal(("ok", "A player on your home network reached Amberline just now"), Check(status, "friend"));
            if (HostFirewall.Refusal(firewall.Allowed) is null)
            {
                TestAssert.Equal(1, firewall.Commands.Count, "one Windows prompt, as on the internet choices");
                TestAssert.Equal("Network: all checks passed", status.GetProperty("network").GetString());
                TestAssert.True(status.GetProperty("headline").GetString()!.EndsWith(". Players can join.", StringComparison.Ordinal), status.GetProperty("headline").GetString());
            }

            // Leaving the choice for an internet address asks the router again: it maps the ports and its shared address is A-NET-07.
            using (var typed = await browser.PostAsync("/host/setup/address", Form(Token(page), ("choice", "typed"), ("address", "198.51.100.20"))))
                TestAssert.Equal(HttpStatusCode.Redirect, typed.StatusCode, await typed.Content.ReadAsStringAsync());
            status = await WaitForStatusAsync(browser, now => Check(now, "router").State != "checking" && Check(now, "address").State != "checking");
            TestAssert.True(Check(status, "router").Text.StartsWith("Found your router", StringComparison.Ordinal), Check(status, "router").Text);
            TestAssert.True(Cards(status).Contains(("A-NET-07", null)), "the shared-address card is back on the internet choices");
            TestAssert.Equal(5, router.Table().Count, "the sign-in port and the four game ports");
            TestAssert.Equal(("waiting", "A player on your home network reached Amberline just now. Waiting for a player from the internet."), Check(status, "friend"));

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
            TestAssert.Equal(("waiting", "Waiting for the first player to start the Play file."), Check(status, "friend"));
            using (var own = await browser.GetAsync($"https://127.0.0.1:{home.RemotePort}/bootstrap/client.json"))
                TestAssert.Equal(HttpStatusCode.OK, own.StatusCode, "the public port answers the Play file's manifest");
            TestAssert.Equal<DateTimeOffset?>(null, home.Loop.Friend.FromInternet, "a request from this PC is not a friend");
            TestAssert.False(File.Exists(HostFriendLight.StateFile(home.Loop.Paths.Data)), "nothing is saved for it");
            // The check cannot listen on a public interface, so the request a friend's Play file makes is handed to the light directly.
            home.Loop.Friend.Observe(IPAddress.Parse("203.0.113.9"), "/bootstrap/client.json", DateTimeOffset.UtcNow);
            status = await StatusAsync(browser);
            TestAssert.Equal(("ok", "A player reached Amberline from the internet just now"), Check(status, "friend"));
            TestAssert.Equal("Last player connected just now", status.GetProperty("friend").GetString());
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
        TestAssert.True(page.Contains("Users cannot log in until you start Amberline again.", StringComparison.Ordinal) &&
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

    [Check("server.host-games-at-once", TimeoutSeconds = 180)]
    public static void SettingsChoosesHowManyGamesRunAtOnce() => GamesAtOnceAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 684 (owner, 4 November 2026: a Settings field "Games at the same time", 1 to 32, default 4): a saved number reaches the
    /// dashboard's "Games N of M", the support log and the router's ports at once, without a restart. Save never asks Windows: a rule
    /// with fewer game ports gets the card that it must be updated, whose button writes the new range; a rule wider than the ports still
    /// lets every game in. A number outside 1 to 32 is refused beside its field and saves nothing.
    /// </summary>
    private static async Task GamesAtOnceAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        await using var router = await HostUpnpChecks.FakeRouter.StartAsync();
        // The two rules exist already, for the four games of a new install.
        var firewall = new FakeFirewall { Added = true };
        var home = Home.Create("league-host-games-at-once", network: new HostNetwork(
            cancel => UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3), cancel), firewall.ReadAsync, firewall.RunAsync,
            () => (null, null)));
        IReadOnlyList<FirewallRule> Rules(int games) =>
            HostFirewall.Rules(Environment.ProcessPath!, home.Loop.Paths.ServerExecutable, home.RemotePort, 5219, games);
        firewall.Allowed = Rules(4);
        home.ChooseFolders();
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await WaitForStatusAsync(browser, now => Check(now, "firewall").State != "checking" && Check(now, "router").State != "checking");
            TestAssert.Equal(("ok", "Windows Firewall lets players in"), Check(status, "firewall"));
            TestAssert.Equal(4, status.GetProperty("gamesMax").GetInt32(), "four games by default");
            TestAssert.Equal(5, router.Table().Count, "the sign-in port and four game ports");

            var page = await PageAsync(browser, "/host/settings");
            foreach (var part in new[] { "<h2 id=games-title>Games</h2>", "<form method=post action=/host/settings/games novalidate>",
                         "<label for=games>Games at the same time", "1 to 32. Each game uses about one CPU core, some memory and its own game port: now UDP 5219 to 5222.",
                         "type=number min=1 max=32 inputmode=numeric value=\"4\"" })
                TestAssert.True(page.Contains(part, StringComparison.Ordinal), part + " in " + page);

            // Six: saved, counted at once, the router's two new ports opened; the firewall rule must be updated, and Save asked nothing.
            using (var six = await browser.PostAsync("/host/settings/games", Form(Token(page), ("games", "6"))))
            {
                TestAssert.Equal(HttpStatusCode.Redirect, six.StatusCode, await six.Content.ReadAsStringAsync());
                TestAssert.Equal("/host/settings?saved=games", six.Headers.Location?.OriginalString);
            }
            TestAssert.Equal(6, MaxGames(home), "platform.json keeps the number");
            page = await PageAsync(browser, "/host/settings?saved=games");
            TestAssert.True(page.Contains("<p class=notice role=status>Saved. It counts for games that start from now on; a running game goes on. " +
                "If the dashboard asks, choose Allow through Windows Firewall.</p>", StringComparison.Ordinal) &&
                page.Contains("now UDP 5219 to 5224.", StringComparison.Ordinal) && page.Contains("value=\"6\"", StringComparison.Ordinal), page);
            await HostUpnpChecks.Until(() => router.Table().Count == 7, "the router forwards six game ports");
            TestAssert.Equal(new[] { $"TCP {home.RemotePort}", "UDP 5219", "UDP 5220", "UDP 5221", "UDP 5222", "UDP 5223", "UDP 5224" },
                router.Table().Keys.Select(key => $"{key.Protocol} {key.Port}").Order(StringComparer.Ordinal).ToArray(), "the new range");
            status = await WaitForStatusAsync(browser, now => Check(now, "firewall").State == "bad");
            TestAssert.Equal(("bad", "Windows Firewall's rule must be updated for the new game ports."), Check(status, "firewall"));
            TestAssert.Equal(new (string, string?)[] { ("A-NET-02", "firewall") }, Cards(status), "the firewall card, with its button");
            TestAssert.Equal(6, status.GetProperty("gamesMax").GetInt32(), "Games 0 of 6");
            TestAssert.Equal(0, firewall.Commands.Count, "Save never asks Windows");
            TestAssert.True((await PageAsync(browser, HostSupport.FilePath)).Contains("Games running: 0 of 6.", StringComparison.Ordinal),
                "the support log counts the new number");

            // The card's button: one prompt, which writes the six-game range over both existing rules.
            firewall.Next = Rules(6);
            using (var allow = await browser.PostAsync("/host/firewall", Form(Token(page))))
                TestAssert.Equal(HttpStatusCode.Redirect, allow.StatusCode);
            if (HostFirewall.Refusal(Rules(6)) is null)
            {
                TestAssert.Equal(1, firewall.Commands.Count, "one Windows prompt");
                TestAssert.Equal(HostFirewall.Command(Rules(6), [HostFirewall.SignInRule, HostFirewall.GamesRule], Environment.SystemDirectory),
                    firewall.Commands[0], "the new range, over the old rules");
                TestAssert.Equal(("ok", "Windows Firewall lets players in"), Check(await StatusAsync(browser), "firewall"));
            }

            // Two: the router's ports above it go at once; the wider firewall rule still lets both games in, so nothing is asked.
            using (var two = await browser.PostAsync("/host/settings/games", Form(Token(page), ("games", "2"))))
                TestAssert.Equal(HttpStatusCode.Redirect, two.StatusCode, await two.Content.ReadAsStringAsync());
            await HostUpnpChecks.Until(() => router.Table().Count == 3, "the router forwards two game ports");
            TestAssert.Equal(4, router.Count("DeletePortMapping"), "the four ports above two are deleted");
            status = await WaitForStatusAsync(browser, now => now.GetProperty("gamesMax").GetInt32() == 2 && Check(now, "firewall").State != "checking");
            if (HostFirewall.Refusal(Rules(6)) is null)
            {
                TestAssert.Equal(("ok", "Windows Firewall lets players in"), Check(status, "firewall"));
                TestAssert.Equal(1, firewall.Commands.Count, "fewer games ask Windows nothing");
            }

            // Refused: outside 1 to 32, not a number, empty; each beside its field, the typed value kept, nothing saved.
            foreach (var typed in new[] { "0", "33", "x", "", "-1", "4.5" })
            {
                using var refused = await browser.PostAsync("/host/settings/games", Form(Token(page), ("games", typed)));
                var answer = await refused.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.BadRequest, refused.StatusCode, answer);
                foreach (var part in new[] { "<li><a href=#games>Choose a number from 1 to 32.</a></li>",
                             "<p class=field-error id=games-error><span class=vh>Error: </span>Choose a number from 1 to 32.</p>",
                             $"value=\"{WebUtility.HtmlEncode(typed)}\" aria-describedby=\"games-hint games-error\" aria-invalid=true>" })
                    TestAssert.True(answer.Contains(part, StringComparison.Ordinal), $"{typed}: {part} in {answer}");
            }
            using (var expired = await browser.PostAsync("/host/settings/games", Form(new("x", "y"), ("games", "8"))))
                TestAssert.Equal(HttpStatusCode.BadRequest, expired.StatusCode, "a form without its token saves nothing");
            TestAssert.Equal(2, MaxGames(home), "a refused number saves nothing");
            TestAssert.Equal(2, (await StatusAsync(browser)).GetProperty("gamesMax").GetInt32());
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(0, router.Table().Count, "Stop removes the router's ports");
        foreach (var call in router.Snapshot(router.Calls))
            TestAssert.True(call.StartsWith("127.0.0.1 ", StringComparison.Ordinal), "every router request went to the fake router: " + call);
        Delete(home.Root);
    }

    [Check("server.host-dashboard-layout", TimeoutSeconds = 180)]
    public static void TheDashboardLeadsWithAttentionTilesActionsGamesAndActivity() => LayoutAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The Dashboard's parts in the owner's order: Needs your attention first (hidden while it is empty), the three tiles, the actions, the
    /// game cards and the activity feed, the server details last. Each list has its template for the script; nothing is inline.
    /// </summary>
    private static async Task LayoutAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Home.Create("league-host-dashboard-layout");
        home.ChooseFolders();
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var page = await PageAsync(browser, HostDashboard.PagePath);
            TestAssert.True(Regex.IsMatch(page, @"<main[^>]*>\s*<h1>Dashboard</h1>\s*<section id=attention-section"), "Needs your attention is the first part of main");
            string[] order =
            [
                "<section id=attention-section class=attention aria-labelledby=attention-title hidden>", "<div class=tile id=tile-online>",
                "<div class=tile id=tile-games>", "<div class=tile id=tile-signups>", "<button type=button data-copy=address-value id=copy-address hidden>",
                $"<a class=\"button secondary\" href={HostInvite.AddPath}>Add a user</a>", "<ul id=cards class=game-cards></ul>",
                "<template id=card-template>", "<ol id=feed class=feed></ol>", "<template id=feed-template>", "<section id=status class=status",
                "<h2 id=online-title>Online now</h2>", "<h2>Network</h2>",
            ];
            var at = order.Select(part => page.IndexOf(part, StringComparison.Ordinal)).ToArray();
            TestAssert.True(at.All(index => index >= 0) && at.Zip(at.Skip(1)).All(pair => pair.First < pair.Second),
                "the parts come in the design's order: " + string.Join(" | ", order.Zip(at, (part, index) => $"{index} {part}")));
            foreach (var number in new[] { "<b id=tile-online-count>", "<b id=tile-games-count>", "<b id=tile-signups-state>" })
                TestAssert.True(page.Contains(number, StringComparison.Ordinal), "a tile's number: " + number);
            TestAssert.True(page.Contains("<button type=button data-copy=signup-link id=copy-signup hidden>Copy sign-up link</button>", StringComparison.Ordinal),
                "Copy sign-up link waits for the script to show it, while sign-ups are on");
            // Without the script each part says Checking…, never an empty state nobody has checked; the script fills in the empty lines.
            TestAssert.True(page.Contains("<p id=cards-empty class=empty>Checking…</p>", StringComparison.Ordinal) &&
                page.Contains("<p id=feed-empty class=empty>Checking…</p>", StringComparison.Ordinal) &&
                page.Contains("<p id=lobbies class=empty hidden></p>", StringComparison.Ordinal),
                "the parts say Checking… until the script fills them; the waiting-lobbies line stays hidden until a lobby waits");
            // The script's notice sits by the games, outside the folded server details.
            var notice = page.IndexOf("<noscript><p class=problem>This page needs JavaScript", StringComparison.Ordinal);
            TestAssert.True(notice > page.IndexOf("id=copy-address", StringComparison.Ordinal) && notice < page.IndexOf("<h2 id=cards-title>", StringComparison.Ordinal) &&
                notice < page.IndexOf("<details id=server-details", StringComparison.Ordinal), "the no-script notice sits by the games, before Server details");
            TestAssert.True(page.Contains("<details id=server-details open><summary>Server details</summary>", StringComparison.Ordinal) &&
                page.Contains("<table id=online-table hidden>", StringComparison.Ordinal), "the status, the online table and the network checks fold under Server details");
            TestAssert.False(Regex.IsMatch(page, @"\sstyle\s*="), "no inline style on the Dashboard");
            TestAssert.Equal(1, Regex.Count(page, "<script"), "one script element");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    [Check("server.host-dashboard-empty-states", TimeoutSeconds = 180)]
    public static void AnEmptyDashboardSaysSoInEachPart() => EmptyStatesAsync().GetAwaiter().GetResult();

    /// <summary>With no game and no activity yet, each part says so: no card, no lobby line, an empty feed, nobody online and sign-ups off.</summary>
    private static async Task EmptyStatesAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = Home.Create("league-host-dashboard-empty");
        home.ChooseFolders();
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await StatusAsync(browser);
            TestAssert.Equal(0, status.GetProperty("cards").GetArrayLength(), "no game card while no game runs");
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("lobbies").ValueKind, "no waiting-lobbies line");
            TestAssert.Equal(0, status.GetProperty("activity").GetArrayLength(), "an empty feed: nothing has happened since the start");
            TestAssert.Equal(0, status.GetProperty("onlineCount").GetInt32(), "nobody online");
            TestAssert.False(status.GetProperty("signUpsOpen").GetBoolean(), "sign-ups are off");
            TestAssert.Equal(JsonValueKind.Null, status.GetProperty("signUpLink").ValueKind, "no sign-up link while sign-ups are off");
            var page = await PageAsync(browser, HostDashboard.PagePath);
            // Without the script each part says Checking…, never an empty state nobody has checked; the script sets the empty lines from the status above.
            TestAssert.True(page.Contains("<p id=cards-empty class=empty>Checking…</p>", StringComparison.Ordinal) &&
                page.Contains("<p id=feed-empty class=empty>Checking…</p>", StringComparison.Ordinal), "the static default is Checking…, not an empty state");
            var script = await browser.GetStringAsync(HostPages.ScriptPath);
            TestAssert.True(script.Contains("text('cards-empty', 'No games running.')", StringComparison.Ordinal) &&
                script.Contains("text('feed-empty', 'Nothing yet.')", StringComparison.Ordinal), "the script sets the empty lines once the status has no game and no activity");
            TestAssert.True(page.Contains("id=attention-section class=attention aria-labelledby=attention-title hidden>", StringComparison.Ordinal),
                "no attention, so the section is hidden");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Delete(home.Root);
    }

    [Check("server.host-dashboard-status-cards", TimeoutSeconds = 180)]
    public static void GameCardsAndTheLobbyLineComeFromTheLobbies() => StatusCardsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The game cards and the waiting-lobbies line come from the platform's lobbies (a match in progress with its seats and champions, a lobby
    /// still in team selection), the sign-up link and the address come with the status, and the feed's text arrives as JSON text that the
    /// page sets as text and never as markup. The lobbies are made with the platform's own calls; no match server runs.
    /// </summary>
    private static async Task StatusCardsAsync()
    {
        var home = Home.Create("league-host-dashboard-cards");
        using var accounts = new PlatformAccounts(Path.Combine(home.Root, "accounts"));
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, home.Loop.Paths.ServerExecutable,
            FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(home.Root, "games"));
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddSeconds(-90) };
        var games = new PlatformGames(accounts, manager, "127.0.0.1", clock) { Push = (_, _) => { } };
        var trees = accounts.RegisterPublic("cardtrees", Password, "Card Trees");
        var second = accounts.RegisterPublic("cardsecond", Password, "Card Second");
        var created = (AsObject)games.Create(trees, "Trees", "", 4, 6, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
        var lobby = Convert.ToInt32(created["id"]);
        var joined = (AsObject)games.Join(second, lobby, "");
        games.Start(trees.Id, lobby, Convert.ToInt64(joined["optimisticLock"]));
        games.Pick(trees.Id, 1);
        games.Pick(second.Id, 2);
        games.StartPlayingForTest(lobby);
        var waiter = accounts.RegisterPublic("cardwaiter", Password, "Card Waiter");
        games.Create(waiter, "Waiting", "", 4, 6, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");

        var settings = new PlatformSettings(home.Game, home.Loop.Paths.ServerExecutable, HttpPort: home.Port, RemotePort: home.RemotePort,
            AirRoot: home.Air, PublicHost: "amberline.example.test");
        using var signUp = new HostSelfRegistration(home.Loop, accounts, home.RemotePort, "0123456789ab", on: true);
        var run = Task.Run(() => RunPageAsync(home, settings, () => [], games.HostBoard, signUp: signUp));
        try
        {
            using var client = home.Client();
            await WaitForStartAsync(home.Loop, 1, run);
            await home.SignInAsync(client);
            home.Loop.Activity.Add("<b>x</b> logged in");
            var status = await StatusAsync(client);
            var card = status.GetProperty("cards").EnumerateArray().Single();
            TestAssert.Equal("3v3 map", card.GetProperty("map").GetString(), "the map's neutral name");
            TestAssert.Equal("Classic", card.GetProperty("mode").GetString(), "the mode's name");
            TestAssert.Equal(clock.Now, card.GetProperty("since").GetDateTimeOffset(), "since the game started playing (the lobby's StateSince)");
            TestAssert.True(card.GetProperty("elapsedSeconds").GetInt32() is >= 90 and < 600,
                "the whole seconds the status counted since the start (the page shows mm:ss): " + card.GetRawText());
            TestAssert.False(card.GetProperty("canEnd").GetBoolean(), "a seeded game has no match server to end");
            TestAssert.Equal($"cardtrees · {AirGameDtos.ChampionNames[1]}", Seats(card, "blue").Single(), "the blue seat: username and champion");
            TestAssert.Equal($"cardsecond · {AirGameDtos.ChampionNames[2]}", Seats(card, "red").Single(), "the red seat: username and champion");
            TestAssert.Equal("Lobbies: 1 waiting (3v3 map, 1/6)", status.GetProperty("lobbies").GetString(), "the waiting lobby, as one line");
            TestAssert.Equal("<b>x</b> logged in", status.GetProperty("activity")[0].GetProperty("text").GetString(),
                "the feed's text arrives as JSON text; the page sets it as text, never as markup");
            TestAssert.Equal(home.Loop.Activity.Recent()[0].At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                status.GetProperty("activity")[0].GetProperty("time").GetString(), "the feed line's clock time, as the server formats it");
            TestAssert.True(status.GetProperty("signUpsOpen").GetBoolean(), "sign-ups are on");
            TestAssert.Equal($"https://amberline.example.test:{home.RemotePort}/register", status.GetProperty("signUpLink").GetString(), "the sign-up link on the friends' port");
            TestAssert.Equal("amberline.example.test", status.GetProperty("publicHost").GetString(), "the address Copy address copies");
            var page = await PageAsync(client, HostDashboard.PagePath);
            TestAssert.True(page.Contains("<template id=card-template>", StringComparison.Ordinal) && page.Contains("<template id=feed-template>", StringComparison.Ordinal),
                "the page holds the templates the script fills");
            TestAssert.False(page.Contains("cardtrees", StringComparison.Ordinal) || page.Contains("Card Trees", StringComparison.Ordinal) ||
                page.Contains("<b>x</b>", StringComparison.Ordinal) || page.Contains("Lobbies: 1", StringComparison.Ordinal),
                "the page's own markup holds none of the games' names or the feed's text");
        }
        finally { await EndPageAsync(home, run); }
        Delete(home.Root);
    }

    [Check("server.host-end-game", TimeoutSeconds = 180)]
    public static void EndGameAsksOnceAndAnswersAGameThatAlreadyEnded() => EndGameAsync().GetAwaiter().GetResult();

    /// <summary>
    /// End game on a page-only dashboard: a board with two running games and a recording stop. The confirm page names a running game, any
    /// other id gets "This game has already ended.", an unsigned POST is refused, a signed POST stops the game once and goes back to the
    /// dashboard (303), and a stop that finds its game gone (RemoteFault 404) answers with the same page, not a failure.
    /// </summary>
    private static async Task EndGameAsync()
    {
        var home = Home.Create("league-host-end-game");
        var running = Guid.NewGuid();
        var vanished = Guid.NewGuid();
        var cards = new List<DashboardGameCard>
        {
            new(running, "3v3 map", "Classic", new DateTimeOffset(2026, 11, 11, 14, 32, 0, TimeSpan.Zero), [new("cardtrees", "Annie")],
                [new("cardsecond", "Olaf")], true),
            new(vanished, "5v5 map", "Classic", new DateTimeOffset(2026, 11, 11, 14, 40, 0, TimeSpan.Zero), [new("cardthird", "Ashe")],
                [new("cardfourth", "Garen")], true),
        };
        var ended = new List<Guid>();
        DashboardBoard Board() { lock (cards) return new([.. cards], null); }
        Task Stop(Guid id)
        {
            lock (cards) cards.RemoveAll(card => card.Id == id);
            lock (ended) ended.Add(id);
            if (id == vanished) throw new RemoteFault(404, "Game not found on this host.");
            return Task.CompletedTask;
        }
        var settings = new PlatformSettings(home.Game, home.Loop.Paths.ServerExecutable, HttpPort: home.Port, RemotePort: home.RemotePort, AirRoot: home.Air);
        var run = Task.Run(() => RunPageAsync(home, settings, () => [], Board, Stop));
        try
        {
            using var client = home.Client();
            await WaitForStartAsync(home.Loop, 1, run);
            await home.SignInAsync(client);
            var end = $"{HostDashboard.PagePath}/games/end";

            using (var unknown = await client.GetAsync($"{end}?id={Guid.NewGuid():D}"))
            {
                var answer = await unknown.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.NotFound, unknown.StatusCode, answer);
                TestAssert.True(answer.Contains("This game has already ended.", StringComparison.Ordinal), answer);
            }
            string confirm;
            using (var question = await client.GetAsync($"{end}?id={running:D}"))
            {
                confirm = await question.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.OK, question.StatusCode, confirm);
            }
            TestAssert.True(confirm.Contains("<h1>End this game?</h1>", StringComparison.Ordinal) &&
                confirm.Contains("The 3v3 map game (Classic, 2 players) ends for everyone in it.", StringComparison.Ordinal) &&
                confirm.Contains($"<form method=post action={end} data-wait=", StringComparison.Ordinal) &&
                confirm.Contains($"name=id value={running:D}", StringComparison.Ordinal), "the confirm page: " + confirm);
            var token = Token(confirm);

            using (var unsigned = await client.PostAsync(end, new FormUrlEncodedContent([new("id", running.ToString("D"))])))
            {
                TestAssert.Equal(HttpStatusCode.BadRequest, unsigned.StatusCode, "a form without its token");
                TestAssert.True((await unsigned.Content.ReadAsStringAsync()).Contains("The page expired", StringComparison.Ordinal), "the expired page");
            }
            TestAssert.Equal(0, ended.Count, "nothing ended without a signed answer");

            using (var answer = await client.PostAsync(end, Form(token, ("id", running.ToString("D")))))
            {
                TestAssert.Equal(HttpStatusCode.SeeOther, answer.StatusCode, await answer.Content.ReadAsStringAsync());
                TestAssert.Equal(HostDashboard.PagePath, answer.Headers.Location?.OriginalString, "back to the dashboard");
            }
            TestAssert.Equal(1, ended.Count, "the stop was asked once");
            TestAssert.Equal(running, ended[0], "for the running game");

            using (var again = await client.PostAsync(end, Form(token, ("id", running.ToString("D")))))
            {
                var answer = await again.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.NotFound, again.StatusCode, answer);
                TestAssert.True(answer.Contains("This game has already ended.", StringComparison.Ordinal), answer);
            }
            TestAssert.Equal(1, ended.Count, "an answer for a game that ended stops nothing");

            using (var gone = await client.PostAsync(end, Form(token, ("id", vanished.ToString("D")))))
            {
                var answer = await gone.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.NotFound, gone.StatusCode, answer);
                TestAssert.True(answer.Contains("This game has already ended.", StringComparison.Ordinal), "the match server's 404 is the same page: " + answer);
            }
            TestAssert.Equal(2, ended.Count, "the stop reached the game that was already gone");
        }
        finally { await EndPageAsync(home, run); }
        Delete(home.Root);
    }

    [Check("server.host-mode-labels", TimeoutSeconds = 180)]
    public static void AnOdinGameIsCaptureOnItsCardAndItsConfirmPage() => ModeLabelsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The Crystal Scar's mode (ODIN) reads Capture on the host page, never Dominion: a Crystal Scar lobby seeded through the platform's own calls
    /// reads Capture in status.json, and a running Crystal Scar game (its card made by PlatformGames.CardOf from its lobby, with the match server's
    /// record that it runs) reads Capture in status.json and on its End game confirm page. No match server runs.
    /// </summary>
    private static async Task ModeLabelsAsync()
    {
        var home = Home.Create("league-host-mode-labels");
        using var accounts = new PlatformAccounts(Path.Combine(home.Root, "accounts"));
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, home.Loop.Paths.ServerExecutable,
            FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(home.Root, "games"));
        var clock = new Clock { Now = DateTimeOffset.UtcNow.AddMinutes(-3) };
        var games = new PlatformGames(accounts, manager, "127.0.0.1", clock) { Push = (_, _) => { } };
        var owner = accounts.RegisterPublic("crystalowner", Password, "Crystal Owner");
        var second = accounts.RegisterPublic("crystalsecond", Password, "Crystal Second");
        var created = (AsObject)games.Create(owner, "Crystal", "", GameMaps.CrystalScar, 10, AirGameDtos.BlindPickConfigId, GameMaps.DominionMode, "NONE");
        var lobby = Convert.ToInt32(created["id"]);
        var joined = (AsObject)games.Join(second, lobby, "");
        games.Start(owner.Id, lobby, Convert.ToInt64(joined["optimisticLock"]));
        games.Pick(owner.Id, 1);
        games.Pick(second.Id, 2);
        games.StartPlayingForTest(lobby);

        // A Crystal Scar game that runs: its lobby's card, with the match server's record of the game (a stand-in; no process runs here).
        var running = Guid.NewGuid();
        var live = PlatformGames.CardOf(new PlatformGames.Lobby(lobby + 1000, "Crystal Live", "", owner.Id, 10)
        {
            MapId = GameMaps.CrystalScar, State = "IN_PROGRESS", HostId = running, StateSince = clock.Now,
            Members = [new PlatformGames.Member(owner, Team.Order) { Champion = 1 }, new PlatformGames.Member(second, Team.Chaos) { Champion = 2 }],
        }, new RemoteGame(running, "crystalowner", "Playing", port, [], CanStop: true, Error: null));

        var settings = new PlatformSettings(home.Game, home.Loop.Paths.ServerExecutable, HttpPort: home.Port, RemotePort: home.RemotePort, AirRoot: home.Air);
        var run = Task.Run(() => RunPageAsync(home, settings, () => [], () =>
        {
            var board = games.HostBoard();
            return new DashboardBoard([.. board.Cards, live], board.Lobbies);
        }));
        try
        {
            using var client = home.Client();
            await WaitForStartAsync(home.Loop, 1, run);
            await home.SignInAsync(client);
            var status = await StatusAsync(client);
            var cards = status.GetProperty("cards").EnumerateArray().ToArray();
            var seeded = cards.Single(card => card.GetProperty("id").GetString() == Guid.Empty.ToString());
            TestAssert.Equal("Capture map", seeded.GetProperty("map").GetString(), "the Crystal Scar's neutral map name");
            TestAssert.Equal("Capture", seeded.GetProperty("mode").GetString(), "the Crystal Scar's mode ODIN reads Capture");
            var endable = cards.Single(card => card.GetProperty("id").GetString() == running.ToString());
            TestAssert.Equal("Capture", endable.GetProperty("mode").GetString(), "a running Crystal Scar game reads Capture too");
            TestAssert.True(endable.GetProperty("canEnd").GetBoolean(), "the running game can be ended");
            TestAssert.False(status.GetRawText().Contains("Dominion", StringComparison.Ordinal), "no card says Dominion: " + status.GetRawText());

            using var question = await client.GetAsync($"{HostDashboard.PagePath}/games/end?id={running:D}");
            var confirm = await question.Content.ReadAsStringAsync();
            TestAssert.Equal(HttpStatusCode.OK, question.StatusCode, confirm);
            TestAssert.True(confirm.Contains("The Capture map game (Capture, 2 players) ends for everyone in it.", StringComparison.Ordinal) &&
                !confirm.Contains("Dominion", StringComparison.Ordinal), "the End game confirm page reads Capture: " + confirm);
        }
        finally { await EndPageAsync(home, run); }
        Delete(home.Root);
    }

    /// <summary>The seats of one team of a game card, as "username · champion" lines.</summary>
    private static string[] Seats(JsonElement card, string team) =>
        [.. card.GetProperty(team).EnumerateArray().Select(seat => $"{seat.GetProperty("player").GetString()} · {seat.GetProperty("champion").GetString()}")];

    /// <summary>A page-only start's end: the loop stops as Stop does, and the run is left once it has finished.</summary>
    private static async Task EndPageAsync(Home home, Task run)
    {
        home.Loop.Stop();
        try { await run.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static int MaxGames(Home home)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings));
        return settings.RootElement.GetProperty("maxGames").GetInt32();
    }

    /// <summary>
    /// A page-only start: the gate and the dashboard on the home's page port, run by the home's loop until Stop or Restart. The game cards,
    /// the end-game stop and the sign-up switch are the dashboard's own seams (<paramref name="board"/> and <paramref name="endGame"/> default
    /// to none).
    /// </summary>
    private static async Task<bool> RunPageAsync(Home home, PlatformSettings settings, Func<IReadOnlyList<DashboardGame>> games,
        Func<DashboardBoard>? board = null, Func<Guid, Task>? endGame = null, HostSelfRegistration? signUp = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, home.Port));
        builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(home.Loop.Paths.Keys));
        await using var app = builder.Build();
        HostGate.Use(app, home.Port, home.Loop.Sessions, HostDashboard.PagePath);
        await using var dashboard = new HostDashboard(home.Loop, settings, () => [], games, signUp: signUp, board: board, endGame: endGame);
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

    /// <summary>
    /// The firewall read-back as a list: no Amberline rule until the elevation ran (or <see cref="Added"/>), then <see cref="Allowed"/> as
    /// netsh prints them; an elevation replaces them with <see cref="Next"/> when it is set.
    /// </summary>
    private sealed class FakeFirewall
    {
        private volatile bool _added;
        public IReadOnlyList<FirewallRule> Allowed { get; set; } = [];
        public IReadOnlyList<FirewallRule>? Next { get; set; }
        public bool Added { init => _added = value; }
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
            if (Next is { } next) Allowed = next;
            _added = true;
            return Task.FromResult<int?>(0);
        }
    }
}
