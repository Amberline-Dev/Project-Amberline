using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Connections;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's start checks and their cards (batch 631, H9 of the Amberline Host design): a missing part of Amberline, a port
/// another program holds, data from a newer Amberline, the address change, the certificate's end and a VPN. Every check runs in a
/// temporary folder on free loopback ports; the router is H5's fake on loopback and the outside lookup a counter that must stay at
/// zero, so no check asks a real router or service, binds a public interface, elevates or opens a browser. Addresses are from the
/// documentation ranges of RFC 5737.
/// </summary>
public static class HostStartChecks
{
    [Check("server.host-missing-file-named", TimeoutSeconds = 180)]
    public static void AMissingPartOfAmberlineIsNamed() => MissingFileAsync().GetAwaiter().GetResult();

    private static async Task MissingFileAsync()
    {
        // The comparison with BUILD.json, on a small app folder.
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-files");
        try
        {
            var app = Path.Combine(root, "app");
            var files = new Dictionary<string, byte[]>
            {
                ["platform/Amberline.Platform.exe"] = RandomNumberGenerator.GetBytes(300),
                ["server/Amberline.Server.exe"] = RandomNumberGenerator.GetBytes(200),
                ["server/exehost.exe"] = RandomNumberGenerator.GetBytes(100),
            };
            TestAssert.Equal(null, await HostChecks.AppFilesAsync(app, default), "no app folder and no BUILD.json: nothing to compare");
            Release(app, files);
            TestAssert.Equal(null, await HostChecks.AppFilesAsync(app, default), "every file matches its size and SHA-256");
            File.Delete(Path.Combine(app, "server", "exehost.exe"));
            var card = await HostChecks.AppFilesAsync(app, default);
            TestAssert.Equal(("A-FILES-06", "Windows Security may have removed exehost.exe, a part of Amberline.", (string?)null),
                (card?.Code, card?.Message, card?.Action), "the removed file is named");
            TestAssert.True(card!.WhatToDo.Contains("Protection history", StringComparison.Ordinal) &&
                card.WhatToDo.Contains("your settings and users stay", StringComparison.Ordinal), card.WhatToDo);
            // Batch 751 merge-sweep fix: the Hide subject is a digest of the app folder and the problem's kind, never the folder itself.
            TestAssert.Equal(FilesSubject(app, "missing"), card.Subject, "the subject does not reveal the folder");
            File.Delete(Path.Combine(app, "platform", "Amberline.Platform.exe"));
            TestAssert.Equal("Windows Security may have removed Amberline.Platform.exe, a part of Amberline (and 1 more part).",
                (await HostChecks.AppFilesAsync(app, default))?.Message, "the first missing file in BUILD.json's order, and how many more");

            Release(app, files);
            var server = Path.Combine(app, "server", "Amberline.Server.exe");
            var changed = (byte[])files["server/Amberline.Server.exe"].Clone();
            changed[7] ^= 0xFF;
            File.WriteAllBytes(server, changed);
            card = await HostChecks.AppFilesAsync(app, default);
            TestAssert.Equal(("A-FILES-06", "A part of Amberline was changed: Amberline.Server.exe."), (card?.Code, card?.Message), "same size, other bytes");
            TestAssert.Equal(FilesSubject(app, "changed"), card!.Subject, "a changed part is another problem than a missing one");
            File.WriteAllBytes(server, [.. files["server/Amberline.Server.exe"], 0]);
            TestAssert.Equal("A part of Amberline was changed: Amberline.Server.exe.", (await HostChecks.AppFilesAsync(app, default))?.Message, "another size");

            Release(app, files);
            File.WriteAllText(Path.Combine(app, "BUILD.json"), "{\"files\": 3}");
            TestAssert.Equal("A part of Amberline was changed: BUILD.json.", (await HostChecks.AppFilesAsync(app, default))?.Message, "a damaged BUILD.json");
            Release(app, files, extra: "../outside.txt");
            TestAssert.Equal(null, await HostChecks.AppFilesAsync(app, default), "a path outside the app folder is never looked at");
        }
        finally { Delete(root); }

        // The running dashboard: a release whose exehost.exe is gone names it; nothing else of the page changes.
        var saved = SaveVariables();
        var home = Home.Create("league-host-missing");
        var appRoot = Path.GetFullPath(Path.Combine(home.Loop.Paths.App, ".."));
        File.WriteAllText(Path.Combine(appRoot, "BUILD.json"), Manifest(appRoot, ["server/Amberline.Server.exe", "bootstrap/companion.zip"],
            ("server/exehost.exe", 100, new string('A', 64))));
        // An entry an earlier build saved with the folder's full path as subject: it never matches the card and the first pass forgets it.
        var stateFile = HostFriendLight.StateFile(home.Loop.Paths.Data);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, new JsonObject { ["hiddenCards"] = new JsonArray(new JsonObject { ["code"] = "A-FILES-06", ["subject"] = appRoot }) }.ToJsonString());
        home.ChooseFolders();
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await StatusUntilAsync(browser, now => Card(now, "A-FILES-06") is not null);
            var card = Card(status, "A-FILES-06")!.Value;
            TestAssert.Equal("Windows Security may have removed exehost.exe, a part of Amberline.", card.GetProperty("message").GetString());
            TestAssert.Equal(JsonValueKind.Null, card.GetProperty("action").ValueKind, "Restart runs the comparison again; no button of its own");
            TestAssert.False(card.GetRawText().Contains(JsonEncodedText.Encode(home.Root).ToString(), StringComparison.OrdinalIgnoreCase),
                "the card names the file, not the PC's folders");
            TestAssert.Equal(FilesSubject(appRoot, "missing"), card.GetProperty("subject").GetString(), "the Hide subject is the folder's digest");
            await StatusAsync(browser); // a pass after the comparison finished settles the hidden A-FILES-06 entries
            TestAssert.Equal(null, JsonNode.Parse(File.ReadAllText(stateFile))!["hiddenCards"], "the old full-path entry hid nothing and is forgotten");
        }
        finally
        {
            await home.StopAsync(run);
            RestoreVariables(saved);
        }

        // The match server beside the Platform is gone: setup mode says so in plain words instead of a developer's file name.
        File.Delete(home.Loop.Paths.ServerExecutable);
        var next = home.Again();
        run = Task.Run(() => HostMode.RunAsync(next.Loop));
        try
        {
            using var browser = next.Client();
            await WaitForHealthAsync(browser, "setup", run);
            var problem = next.Loop.Problem ?? "";
            TestAssert.True(problem.StartsWith("Windows Security may have removed Amberline.Server.exe, a part of Amberline (in server).", StringComparison.Ordinal) &&
                problem.EndsWith("(A-FILES-06)", StringComparison.Ordinal) && !problem.Contains("host.json", StringComparison.Ordinal), problem);
            await next.SignInAsync(browser);
            TestAssert.True((await browser.GetStringAsync("/setup")).Contains("removed Amberline.Server.exe", StringComparison.Ordinal), "the setup page shows it");
        }
        finally
        {
            await next.StopAsync(run);
            RestoreVariables(saved);
        }
        Delete(home.Root);
    }

    [Check("server.host-port-in-use-plain-words", TimeoutSeconds = 240)]
    public static void APortAnotherProgramHoldsIsNamedInPlainWords() => PortInUseAsync().GetAwaiter().GetResult();

    private static async Task PortInUseAsync()
    {
        TestAssert.True(HostChecks.PortRefused(new IOException("Failed to bind", new AddressInUseException("in use"))), "Kestrel's refusal");
        TestAssert.True(HostChecks.PortRefused(new SocketException((int)SocketError.AddressAlreadyInUse)), "a listener's refusal");
        TestAssert.True(HostChecks.PortRefused(new SocketException((int)SocketError.AccessDenied)), "a port Windows keeps for itself");
        TestAssert.False(HostChecks.PortRefused(new IOException("The disk is full.")), "another IO error");
        TestAssert.False(HostChecks.PortRefused(new SocketException((int)SocketError.ConnectionRefused)), "another socket error");
        TestAssert.Equal("Another program uses a port Amberline needs. Is Amberline already running for another Windows user, or a Play file " +
            "for another server open? (A-NET-01)", HostChecks.PortInUse(null), "without a port");

        // Each port the full start listens on, held by another program; the page's own port is the single-instance check's.
        foreach (var which in new[] { "rtmp", "xmpp", "public" })
        {
            var saved = SaveVariables();
            var home = Home.Create("league-host-port-" + which);
            home.ChooseFolders();
            var port = which switch { "rtmp" => home.RtmpPort, "xmpp" => home.XmppPort, _ => home.RemotePort };
            var other = new TcpListener(IPAddress.Loopback, port);
            other.Start();
            try
            {
                var settings = new PlatformSettings("game", "server", HttpPort: home.Port, RtmpPort: home.RtmpPort, RemotePort: home.RemotePort,
                    BindAddress: "127.0.0.1", XmppPort: home.XmppPort);
                TestAssert.Equal(port, HostChecks.BusyPort(settings), "the taken port is found");
                var exit = await HostMode.RunAsync(home.Loop).WaitAsync(TimeSpan.FromSeconds(90));
                var screen = home.Screen.ToString();
                TestAssert.Equal(2, exit, screen);
                TestAssert.Equal(HostChecks.PortInUse(port) + Environment.NewLine, screen, $"the {which} port, in plain words and nothing else");
                TestAssert.Equal(0, home.Opened.Count, "no page opens");
                TestAssert.Equal(0, home.Loop.Starts, "no mode started listening");
                TestAssert.Equal(null, await HostMode.ProbeAsync(home.Port), "the page's port was given back");
            }
            finally
            {
                other.Stop();
                RestoreVariables(saved);
            }
            TestAssert.Equal(null, HostChecks.BusyPort(new PlatformSettings("game", "server", HttpPort: home.Port, RtmpPort: home.RtmpPort,
                RemotePort: home.RemotePort, BindAddress: "127.0.0.1", XmppPort: home.XmppPort)), "every port free again");
            Delete(home.Root);
        }
    }

    [Check("server.host-newer-data-refused", TimeoutSeconds = 180)]
    public static void DataFromANewerAmberlineIsRefusedAndKept() => NewerDataAsync().GetAwaiter().GetResult();

    private static async Task NewerDataAsync()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-version");
        try
        {
            var file = Path.Combine(root, "platform.json");
            TestAssert.Equal(null, HostChecks.NewerData(file), "no file yet: setup mode's business");
            foreach (var (text, refused) in new[] { ("{}", false), ("{\"dataVersion\": 1}", false), ("{\"dataVersion\": \"2\"}", false), ("{not json", false),
                         ("[2]", false), ("{\"dataVersion\": 2}", true), ("{\"dataVersion\": 1.5}", true), ("{\"dataVersion\": 1e12}", true) })
            {
                File.WriteAllText(file, text);
                TestAssert.Equal(refused, HostChecks.NewerData(file) is not null, text);
            }
        }
        finally { Delete(root); }
        // The format this Amberline writes, and what it reads from a file that predates the field.
        using (var written = JsonDocument.Parse(JsonSerializer.Serialize(new PlatformSettings("game", "server"), HostFiles.Json)))
            TestAssert.Equal(HostChecks.DataVersion, written.RootElement.GetProperty("dataVersion").GetInt32(), "every save names its format");
        TestAssert.Equal(1, JsonSerializer.Deserialize<PlatformSettings>("{}", HostFiles.Json)!.DataVersion, "a file without the field is format 1");

        // A data folder a newer Amberline wrote: refused before any mode starts, and not one byte of it changes.
        var saved = SaveVariables();
        var home = Home.Create("league-host-newer");
        var settings = new JsonObject
        {
            ["httpPort"] = home.Port, ["rtmpPort"] = home.RtmpPort, ["xmppPort"] = home.XmppPort, ["remotePort"] = home.RemotePort,
            ["bindAddress"] = "127.0.0.1", ["dataRoot"] = home.Game, ["airRoot"] = home.Air, ["serverName"] = "Test Server",
            ["dataVersion"] = 2, ["somethingNew"] = new JsonObject { ["kept"] = true },
        };
        File.WriteAllText(home.Loop.Paths.Settings, settings.ToJsonString());
        File.WriteAllText(Path.Combine(home.Loop.Paths.Data, "accounts.json"), "{\"written\": \"by a newer Amberline\"}");
        var before = Snapshot(home.Loop.Paths.Data);
        try
        {
            var exit = await HostMode.RunAsync(home.Loop).WaitAsync(TimeSpan.FromSeconds(60));
            var screen = home.Screen.ToString();
            TestAssert.Equal(2, exit, screen);
            TestAssert.Equal(HostChecks.NewerData(home.Loop.Paths.Settings) + Environment.NewLine, screen, "A-DATA-01, in plain words");
            TestAssert.True(screen.Contains("(A-DATA-01)", StringComparison.Ordinal) && screen.Contains("newer Amberline", StringComparison.Ordinal), screen);
            TestAssert.Equal((0, 0), (home.Opened.Count, home.Loop.Starts), "no page, no listener");
            TestAssert.Equal(null, await HostMode.ProbeAsync(home.Port), "nothing listens");
            TestAssert.Equal(before, Snapshot(home.Loop.Paths.Data), "the data folder is untouched");

            // The same folder in this Amberline's format starts as always.
            settings["dataVersion"] = 1;
            settings.Remove("somethingNew");
            File.WriteAllText(home.Loop.Paths.Settings, settings.ToJsonString());
            File.Delete(Path.Combine(home.Loop.Paths.Data, "accounts.json"));
            var next = home.Again();
            var run = Task.Run(() => HostMode.RunAsync(next.Loop));
            using var browser = next.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await next.StopAsync(run);
        }
        finally { RestoreVariables(saved); }
        Delete(home.Root);
    }

    [Check("server.host-address-change-banner", TimeoutSeconds = 240)]
    public static void TheRoutersNewAddressIsFollowedAndShown() => AddressChangeAsync().GetAwaiter().GetResult();

    private static async Task AddressChangeAsync()
    {
        var saved = SaveVariables();
        await using var router = await HostUpnpChecks.FakeRouter.StartAsync();
        router.ExternalAddress = "203.0.113.7";
        var lookups = 0;
        var home = Home.Create("league-host-address-change");
        home.Loop.Addresses = new HostAddressSources(async cancel =>
        {
            await using var gateway = await UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3), cancel);
            return gateway is null ? null : await gateway.ExternalAddressAsync(cancel);
        }, _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>("198.51.100.99"); });
        home.Loop.AddressEvery = TimeSpan.FromMilliseconds(200);
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
            dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server", publicHost = "203.0.113.7", addressSource = "router",
        }));
        var state = HostFriendLight.StateFile(home.Loop.Paths.Data);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);

            // The router keeps its address: asked at the start and every period, nothing changes.
            await AskedAsync(router, 3);
            var status = await StatusAsync(browser);
            TestAssert.Equal(null, Card(status, "A-NET-09"), "no change, no banner");
            await HostAddressAsync(browser, "203.0.113.7");

            // A new public address: saved, served at once, and the banner with its time.
            router.ExternalAddress = "198.51.100.20";
            status = await StatusUntilAsync(browser, now => Card(now, "A-NET-09") is not null);
            var banner = Card(status, "A-NET-09")!.Value;
            TestAssert.True(Regex.IsMatch(banner.GetProperty("message").GetString()!,
                @"^Your address changed at \d\d:\d\d\. Users need the new Play file to reach you\.$"), banner.GetRawText());
            TestAssert.Equal("address", banner.GetProperty("action").GetString(), "the banner has its button");
            TestAssert.Equal("Send every user the new Play file, then choose Hide.",
                banner.GetProperty("whatToDo").GetString());
            TestAssert.Equal(1, status.GetProperty("attention").EnumerateArray().Count(card => card.GetProperty("code").GetString() == "A-NET-09"), "one banner");
            await HostAddressAsync(browser, "198.51.100.20");
            using (var file = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
                TestAssert.Equal(("198.51.100.20", "router", "Test Server"), (file.RootElement.GetProperty("publicHost").GetString(),
                    file.RootElement.GetProperty("addressSource").GetString(), file.RootElement.GetProperty("serverName").GetString()),
                    "platform.json holds the new address and keeps the rest");
            var kept = File.ReadAllText(state);
            TestAssert.True(kept.Contains("addressChangedAt", StringComparison.Ordinal) && !Regex.IsMatch(kept, @"\d+\.\d+\.\d+\.\d+"),
                "host\\state.json keeps the time, never an address: " + kept);
            var page = await browser.GetStringAsync("/host");
            TestAssert.True(page.Contains($"<form method=post action={HostAttention.AddressSeenPath} data-action=address hidden>", StringComparison.Ordinal),
                "the page carries the banner's button");

            // Answers friends could not use are never followed: a home-network, a shared-provider, an empty answer and a silent router.
            foreach (var answer in new[] { "10.0.0.1", "100.64.1.1", "" })
            {
                router.ExternalAddress = answer;
                await AskedAsync(router, router.Count("GetExternalIPAddress") + 2);
                await HostAddressAsync(browser, "198.51.100.20");
            }
            router.Answer = false;
            var searches = router.Snapshot(router.Searches).Length;
            await Until(() => router.Snapshot(router.Searches).Length >= searches + 2, "the silent router is searched for again");
            await HostAddressAsync(browser, "198.51.100.20");
            router.Answer = true;
            router.ExternalAddress = "198.51.100.20";

            // The banner stays through a Restart; Hide needs the page's token, then it is gone for good.
            home.Loop.Restart();
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(browser, "ready", run);
            TestAssert.True(Card(await StatusAsync(browser), "A-NET-09") is not null, "the banner survives a Restart");
            using (var forged = await browser.PostAsync(HostAttention.AddressSeenPath, new FormUrlEncodedContent([])))
                TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "a form without its token hides nothing");
            TestAssert.True(Card(await StatusAsync(browser), "A-NET-09") is not null, "still shown");
            var token = Token(await browser.GetStringAsync("/host"));
            using (var hide = await browser.PostAsync(HostAttention.AddressSeenPath, new FormUrlEncodedContent([token])))
            {
                TestAssert.Equal(HttpStatusCode.Redirect, hide.StatusCode, await hide.Content.ReadAsStringAsync());
                TestAssert.Equal(HostDashboard.PagePath, hide.Headers.Location?.OriginalString);
            }
            TestAssert.Equal(null, Card(await StatusAsync(browser), "A-NET-09"), "hidden");
            TestAssert.False(File.ReadAllText(state).Contains("addressChangedAt", StringComparison.Ordinal), "and forgotten");

            // The host types an address: the router is not asked again, and its new answer is not followed.
            token = Token(await browser.GetStringAsync("/host/setup/address"));
            using (var typed = await browser.PostAsync("/host/setup/address", new FormUrlEncodedContent(
                       [token, new("choice", "typed"), new("address", "amberline.example.test")])))
                TestAssert.Equal(HttpStatusCode.Redirect, typed.StatusCode, await typed.Content.ReadAsStringAsync());
            router.ExternalAddress = "198.51.100.77";
            var asked = router.Count("GetExternalIPAddress");
            await Task.Delay(1000);
            TestAssert.Equal(asked, router.Count("GetExternalIPAddress"), "a typed address: the router is not asked every period");
            await HostAddressAsync(browser, "amberline.example.test");
            TestAssert.Equal(null, Card(await StatusAsync(browser), "A-NET-09"), "no banner for a typed address");
            TestAssert.Equal(0, lookups, "the outside lookup is never asked");
            TestAssert.False(home.Screen.ToString().Contains("198.51.100", StringComparison.Ordinal), "the window never shows the address");
        }
        finally
        {
            await home.StopAsync(run);
            RestoreVariables(saved);
        }
        Delete(home.Root);
    }

    [Check("server.host-address-banner-play-file", TimeoutSeconds = 180)]
    public static void TheAddressBannerOffersTheNewPlayFile() => BannerPlayFileAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 632 (H9 follow-up): the address-change card (A-NET-09) shows Get the new Play file beside Hide this message, rendered as
    /// the dashboard renders card actions (a form the page shows while a card names its action); the button is H8's download, and the
    /// file it gives holds the router's new address.
    /// </summary>
    private static async Task BannerPlayFileAsync()
    {
        var saved = SaveVariables();
        IPAddress? answer = IPAddress.Parse("203.0.113.7");
        var home = Home.Create("league-host-banner-play-file");
        home.Loop.Addresses = new HostAddressSources(_ => Task.FromResult<IPAddress?>(answer), _ => Task.FromResult<string?>(null));
        home.Loop.AddressEvery = TimeSpan.FromMilliseconds(200);
        Directory.CreateDirectory(home.Loop.Paths.App);
        File.Copy(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", HostInvite.TemplateFile)), Path.Combine(home.Loop.Paths.App, HostInvite.TemplateFile));
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
            dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server", publicHost = "203.0.113.7", addressSource = "router",
        }));
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var page = await browser.GetStringAsync("/host");
            var button = Regex.Match(page, "<form method=get action=([^ >]+) data-action=address hidden><button type=submit class=secondary>Download the Play file</button></form>");
            TestAssert.True(button.Success, "the page carries the banner's Play file button: " + page);
            TestAssert.Equal(HostInvite.PlayFilePath, button.Groups[1].Value, "the button is H8's Play file download");
            TestAssert.True(page.Contains($"<form method=post action={HostAttention.AddressSeenPath} data-action=address hidden>", StringComparison.Ordinal),
                "Hide stays beside it");
            var script = await browser.GetStringAsync("/host/page.js");
            TestAssert.True(script.Contains("form.hidden = !status.attention.some(card => card.action === form.dataset.action);", StringComparison.Ordinal),
                "the page shows a card's buttons while the card names their action");
            TestAssert.Equal(null, Card(await StatusAsync(browser), "A-NET-09"), "no change yet, so the button stays hidden");

            // The router reports a new address: the card names the button's action, and the button's file holds the new address.
            answer = IPAddress.Parse("198.51.100.20");
            var banner = Card(await StatusUntilAsync(browser, now => Card(now, "A-NET-09") is not null), "A-NET-09")!.Value;
            TestAssert.Equal("address", banner.GetProperty("action").GetString(), "the banner names the address buttons");
            using var file = await browser.GetAsync(button.Groups[1].Value + "?");
            var text = await file.Content.ReadAsStringAsync();
            TestAssert.Equal(HttpStatusCode.OK, file.StatusCode, text);
            TestAssert.True(file.Content.Headers.ContentDisposition?.ToString().Contains("filename=Play.cmd", StringComparison.Ordinal) == true, "a download named Play.cmd");
            var token = Regex.Match(text, "\\$HostToken = '([^']+)'").Groups[1].Value;
            TestAssert.Equal("198.51.100.20", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(token).Select(b => (byte)(b ^ 0x5A)).ToArray()),
                "the new Play file holds the new address");
        }
        finally
        {
            await home.StopAsync(run);
            RestoreVariables(saved);
        }
        Delete(home.Root);
    }

    [Check("server.host-start-check-cards", TimeoutSeconds = 240)]
    public static void TheCertificateAndVpnCards() => CertificateAndVpnAsync().GetAwaiter().GetResult();

    private static async Task CertificateAndVpnAsync()
    {
        var now = DateTimeOffset.Now;
        TestAssert.Equal(null, HostChecks.CertificateCard(now.AddYears(20), now), "a new install's certificate");
        TestAssert.Equal(null, HostChecks.CertificateCard(now.AddDays(61), now), "more than 60 days left");
        var soon = HostChecks.CertificateCard(now.AddDays(60), now);
        TestAssert.Equal(("A-CERT-01", $"Your users' Play files stop working on {now.AddDays(60).ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}. Make new ones.", "certificate"),
            (soon?.Code, soon?.Message, soon?.Action), "60 days before");
        TestAssert.Equal("Your users' Play files stopped working: Amberline's certificate ran out.", HostChecks.CertificateCard(now.AddDays(-1), now)?.Message);
        IPAddress Ip(string text) => IPAddress.Parse(text);
        TestAssert.Equal(null, HostChecks.VpnCard(Ip("192.168.1.20"), Ip("192.168.1.20")), "the internet goes out through the router's interface");
        TestAssert.Equal(null, HostChecks.VpnCard(null, Ip("10.8.0.2")), "no router side known");
        TestAssert.Equal(null, HostChecks.VpnCard(Ip("192.168.1.20"), null), "no route to the internet known");
        var vpn = HostChecks.VpnCard(Ip("192.168.1.20"), Ip("10.8.0.2"));
        TestAssert.Equal(("A-NET-08", "A VPN is on, so players may not reach you.", "network"),
            (vpn?.Code, vpn?.Message, vpn?.Action));

        // The running dashboard with a certificate 30 days from its end and the internet behind a VPN; no router answers.
        var saved = SaveVariables();
        var outbound = Ip("10.8.0.2");
        var elevated = 0;
        var home = Home.Create("league-host-cards", network: new HostNetwork(_ => Task.FromResult<UpnpGateway?>(null),
            _ => Task.FromResult<IReadOnlyList<ShownRule>?>(null), (_, _) => { Interlocked.Increment(ref elevated); return Task.FromResult<int?>(null); },
            () => (Ip("192.168.1.1"), Ip("192.168.1.20")), () => outbound));
        home.ChooseFolders();
        var remote = Path.Combine(home.Loop.Paths.Data, "remote");
        var pfx = ExpiringCertificate(remote, TimeSpan.FromDays(30));
        var old = File.ReadAllBytes(pfx);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await StatusUntilAsync(browser, current => Card(current, "A-NET-08") is not null);
            TestAssert.Equal("certificate", Card(status, "A-CERT-01")?.GetProperty("action").GetString(), "the certificate card and its button");
            TestAssert.Equal("network", Card(status, "A-NET-08")?.GetProperty("action").GetString(), "the VPN card and Test again");
            outbound = Ip("192.168.1.20");
            TestAssert.Equal(null, Card(await StatusAsync(browser), "A-NET-08"), "the VPN switched off");
            var page = await browser.GetStringAsync("/host");
            TestAssert.True(page.Contains($"<form method=get action={HostAttention.CertificatePath} data-action=certificate hidden>", StringComparison.Ordinal),
                "the page carries the certificate's button");

            // Make new Play files: asks first, needs the token, keeps the old file and restarts with a new 20-year certificate.
            var confirm = await browser.GetStringAsync(HostAttention.CertificatePath);
            TestAssert.True(confirm.Contains("stops working at once", StringComparison.Ordinal), confirm);
            using (var forged = await browser.PostAsync(HostAttention.CertificatePath, new FormUrlEncodedContent([])))
                TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "a form without its token");
            TestAssert.True(old.AsSpan().SequenceEqual(File.ReadAllBytes(pfx)), "nothing replaced yet");
            using (var renew = await browser.PostAsync(HostAttention.CertificatePath, new FormUrlEncodedContent([Token(confirm)])))
                TestAssert.Equal(HttpStatusCode.OK, renew.StatusCode, await renew.Content.ReadAsStringAsync());
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(browser, "ready", run);
            var replaced = Directory.GetFiles(remote, "host.pfx.replaced-*");
            TestAssert.Equal(1, replaced.Length, "the old certificate is kept");
            TestAssert.True(old.AsSpan().SequenceEqual(File.ReadAllBytes(replaced[0])), "byte for byte");
            using (var fresh = X509CertificateLoader.LoadPkcs12FromFile(pfx, null, X509KeyStorageFlags.EphemeralKeySet))
                TestAssert.True(Math.Abs((fresh.NotAfter.ToUniversalTime() - DateTime.UtcNow.AddYears(HostFiles.CertificateYears)).TotalDays) < 2,
                    $"a new 20-year certificate: {fresh.NotAfter:u}");
            TestAssert.Equal(null, Card(await StatusAsync(browser), "A-CERT-01"), "no card for the new certificate");
            TestAssert.Equal(0, elevated, "nothing elevated");
        }
        finally
        {
            await home.StopAsync(run);
            RestoreVariables(saved);
        }
        Delete(home.Root);
    }

    /// <summary>The app folder as a release has it: the files and a BUILD.json with their sizes and SHA-256 (as tools/build-server.ps1 writes).</summary>
    private static void Release(string app, Dictionary<string, byte[]> files, string? extra = null)
    {
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(app, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        File.WriteAllText(Path.Combine(app, "BUILD.json"), Manifest(app, [.. files.Keys], extra is null ? null : (extra, 1, new string('0', 64))));
    }

    private static string Manifest(string app, string[] present, (string Path, long Bytes, string Sha256)? missing)
    {
        var entries = present.Select(name =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(app, name));
            return new JsonObject { ["path"] = name, ["bytes"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) };
        }).ToList();
        if (missing is { } gone) entries.Insert(0, new JsonObject { ["path"] = gone.Path, ["bytes"] = gone.Bytes, ["sha256"] = gone.Sha256 });
        return new JsonObject { ["version"] = "1.4.0", ["component"] = "amberline-host", ["files"] = new JsonArray([.. entries]) }.ToJsonString();
    }

    private static string ExpiringCertificate(string directory, TimeSpan left)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Amberline host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2) + left, DateTimeOffset.UtcNow + left);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "host.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }

    private static Dictionary<string, string?> SaveVariables() =>
        new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    private static void RestoreVariables(Dictionary<string, string?> saved)
    {
        foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }

    private static string Snapshot(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(folder, file)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var answer = await client.GetAsync("/host/status.json");
        var text = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> StatusUntilAsync(HttpClient client, Func<JsonElement, bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await StatusAsync(client);
            if (ready(status)) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the status never got there: " + status.GetRawText());
            // Loopback is held to the Platform's 180 requests a minute too.
            await Task.Delay(300);
        }
    }

    /// <summary>A-FILES-06's Hide subject: the first 16 hex digits of the SHA-256 of the app folder's full path, and the problem's kind.</summary>
    internal static string FilesSubject(string app, string kind) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(app))))[..16] + ":" + kind;

    private static JsonElement? Card(JsonElement status, string code) =>
        status.GetProperty("attention").EnumerateArray().Where(card => card.GetProperty("code").GetString() == code).Select(card => (JsonElement?)card).FirstOrDefault();

    private static async Task HostAddressAsync(HttpClient client, string host)
    {
        using var manifest = JsonDocument.Parse(await client.GetStringAsync("/bootstrap/client.json"));
        TestAssert.Equal(host, manifest.RootElement.GetProperty("host").GetString(), "the friends' manifest names the saved address");
    }

    private static Task AskedAsync(HostUpnpChecks.FakeRouter router, int count) =>
        Until(() => router.Count("GetExternalIPAddress") >= count, $"the router was asked {count} times");

    private static async Task Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }
}
