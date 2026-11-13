using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
/// Settings and Uninstall Amberline (batch 641). Every check runs in temporary folders on free loopback ports, with a temporary
/// desktop, profile and temp folder; the firewall read-back is a list and the elevation only records its command line. No check opens
/// a browser, runs netsh, elevates, reaches a router, or deletes anything outside its own temporary folders.
/// </summary>
public static class HostSettingsChecks
{
    [Check("server.host-settings-change", TimeoutSeconds = 240)]
    public static void SettingsChangeWhatTheFirstRunAskedWithItsOwnPages() => SettingsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 680 (owner, 4 November 2026: "selected person will have cheats in all matches - only toggleable by the host"): Settings'
    /// Cheat commands lists every login, the host's own included, with a box each and one Save. An install whose batch-666 switch was
    /// on shows the host's own login ticked; the first Save writes the list and drops the old switch. A friend's session (its tunnel
    /// token on the friends' port, or a browser on this PC without the host's session) can neither see nor change it.
    /// </summary>
    [Check("server.host-cheat-list-only-the-host", TimeoutSeconds = 180)]
    public static void OnlyTheHostTicksWhoMayUseTheCheatCommands() => CheatListAsync().GetAwaiter().GetResult();

    private static async Task CheatListAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-cheat-list");
        long hostId, friendId;
        using (var accounts = new PlatformAccounts(home.Loop.Paths.Data))
        {
            hostId = accounts.RegisterPublic("cheat_host", "Synthetic-host-Pw-41", "Cheat Host").Id;
            friendId = accounts.RegisterPublic("cheat_friend", "Synthetic-friend-Pw-41", "<b>Friend</b>").Id;
        }
        // A friend's session: the tunnel token its Play file's companion holds.
        var tunnel = new AirRemoteAccess(Path.Combine(home.Loop.Paths.Data, "remote")).Issue(friendId).Token;
        // Batch 666's switch on, no list yet.
        HostInviteChecks.WriteSettings(home, new { serverName = "Cheat League", publicHost = "127.0.0.1", addressSource = HostAddress.Typed, hostAccount = hostId, allowCheats = true });
        var rule = HostSettingsPage.CheatRule(home.Loop.Paths.Settings);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            string Box(long id, bool ticked) => $"<input type=checkbox name=cheat value={id}{(ticked ? " checked" : "")}>";
            var page = await GetAsync(browser, HostSettingsPage.PagePath);
            Has(page, "<h2 id=cheats-title>Cheat commands</h2>", $"action={HostSettingsPage.CheatsPath}",
                Box(hostId, true) + "<span><b>cheat_host</b> · Cheat Host (you)</span></label>",
                Box(friendId, false) + "<span><b>cheat_friend</b> · &lt;b&gt;Friend&lt;/b&gt;</span></label>");
            TestAssert.False(page.Contains("Allow cheat commands in my games", StringComparison.Ordinal), "batch 666's switch is gone: " + page);
            TestAssert.True(rule(hostId) && !rule(friendId), "the migration: the old switch on ticks the host's own login, nobody else's");

            // A friend's session can neither see nor change the list.
            var before = File.ReadAllText(home.Loop.Paths.Settings);
            HttpRequestMessage Friend(HttpMethod method, string url)
            {
                var request = new HttpRequestMessage(method, url)
                    { Content = method == HttpMethod.Get ? null : new FormUrlEncodedContent([new("cheat", friendId.ToString())]) };
                request.Headers.Authorization = new("Bearer", tunnel);
                return request;
            }
            var friends = $"https://127.0.0.1:{home.RemotePort}";
            using (var ladder = await browser.SendAsync(Friend(HttpMethod.Get, friends + "/ladder")))
                TestAssert.Equal(HttpStatusCode.OK, ladder.StatusCode, "the friend's token is a live session on the friends' port");
            foreach (var (method, path) in new[] { (HttpMethod.Get, HostSettingsPage.PagePath), (HttpMethod.Post, HostSettingsPage.CheatsPath) })
            {
                using (var remote = await browser.SendAsync(Friend(method, friends + path)))
                    TestAssert.Equal(HttpStatusCode.NotFound, remote.StatusCode, $"{method} {path} on the friends' port");
                using var stranger = home.Client();
                using (var local = await stranger.SendAsync(Friend(method, path)))
                    TestAssert.Equal(HttpStatusCode.Forbidden, local.StatusCode, $"{method} {path} on this PC without the host's session");
            }
            using (var expired = await browser.PostAsync(HostSettingsPage.CheatsPath, new FormUrlEncodedContent([new("cheat", friendId.ToString())])))
                TestAssert.Equal(HttpStatusCode.BadRequest, expired.StatusCode, "the host's browser without the page's token");
            TestAssert.Equal(before, File.ReadAllText(home.Loop.Paths.Settings), "nothing a friend sent changed platform.json");

            // The host's Save: the friend ticked, the host's own box not; ids that are no login are dropped. The old switch goes.
            await SavedAsync(browser, HostSettingsPage.CheatsPath, "cheats", HostSettingsPage.PagePath,
                ("cheat", friendId.ToString()), ("cheat", "999999"), ("cheat", "-1"), ("cheat", "x"));
            Has(await GetAsync(browser, HostSettingsPage.PagePath + "?saved=cheats"), "<p class=notice role=status>Saved. It counts for games that start from now on.</p>",
                Box(hostId, false) + "<span><b>cheat_host</b>", Box(friendId, true) + "<span><b>cheat_friend</b>");
            TestAssert.Equal(friendId.ToString(), string.Join(",", CheatList(home) ?? []), "platform.json ticks the friend only");
            TestAssert.True(!rule(hostId) && rule(friendId), "games give the cheat commands to the ticked login only");
            TestAssert.False(File.ReadAllText(home.Loop.Paths.Settings).Contains("allowCheats", StringComparison.Ordinal), "the old switch is dropped");
            TestAssert.Equal("Cheat League", Setting(home, "serverName"), "the list keeps the other settings");

            // Both ticked, then none: a saved empty list ticks nobody (the old switch never comes back).
            await SavedAsync(browser, HostSettingsPage.CheatsPath, "cheats", HostSettingsPage.PagePath, ("cheat", hostId.ToString()), ("cheat", friendId.ToString()));
            TestAssert.True(rule(hostId) && rule(friendId), "both ticked: both");
            await SavedAsync(browser, HostSettingsPage.CheatsPath, "cheats", HostSettingsPage.PagePath);
            TestAssert.Equal("", string.Join(",", CheatList(home) ?? [-1]), "an empty list is saved");
            TestAssert.True(!rule(hostId) && !rule(friendId), "none ticked: nobody");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    /// <summary>
    /// Batch 751 (owner, 12 November 2026): Settings' "Skins and RP" switch. Off is saved in platform.json and applied to the live
    /// <see cref="PlatformControls"/> at once; a file without the field reads as on.
    /// </summary>
    [Check("server.host-settings-paid-content-switch", TimeoutSeconds = 180)]
    public static void SkinsAndRpSwitchSavesAndAppliesAtOnce() => PaidContentAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 751 (owner, 12 November 2026): Settings' "Champions" section lists every playable champion, ticked = available. A Save stores
    /// the playable ids that are not ticked (an id that is not playable is ignored) and applies them to the live controls at once.
    /// </summary>
    [Check("server.host-settings-champion-list", TimeoutSeconds = 180)]
    public static void ChampionListSavesDisabledChampionsAndAppliesAtOnce() => ChampionListAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 751's checks share one Settings page: the page-only Settings with a store the check holds, and a browser signed in as the host.
    /// <paramref name="body"/> runs once the page is up. The environment is restored and the champion list checked after it.
    /// </summary>
    internal static async Task OnSettingsPageAsync(string prefix, Func<Home, HttpClient, PlatformAccounts, Task> body, Func<DateOnly>? today = null)
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A full Platform reads the champion list; read it the suite's way first (see HostPagesChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create(prefix);
        using var accounts = new PlatformAccounts(home.Loop.Paths.Data);
        var run = Task.Run(async () => await RunSettingsAsync(home, () => 0, accounts, today) ? 1 : 0);
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            await body(home, browser, accounts);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    private static Task PaidContentAsync() => OnSettingsPageAsync("league-host-paid-content", async (home, browser, accounts) =>
    {
        Has(await GetAsync(browser, HostSettingsPage.PagePath), "<h2 id=paid-title>Skins and RP</h2>", $"action={HostSettingsPage.PaidContentPath}",
            "<input type=checkbox name=on value=on checked>Offer skins and RP",
            "Off: everyone plays the default looks, and the store shows no skins and no RP. Nothing anyone owns is removed.");
        TestAssert.False(accounts.Controls.PaidContentOff, "a settings file without the field offers skins and RP");

        await SavedAsync(browser, HostSettingsPage.PaidContentPath, "paid", HostSettingsPage.PagePath);
        TestAssert.True(accounts.Controls.PaidContentOff, "unticked: the live controls turn skins and RP off at once");
        TestAssert.True(Flag(home, "paidContentOff"), "platform.json holds the switch off");
        Has(await GetAsync(browser, HostSettingsPage.PagePath + "?saved=paid"), "<p class=notice role=status>Saved.</p>",
            "<input type=checkbox name=on value=on>Offer skins and RP");

        await SavedAsync(browser, HostSettingsPage.PaidContentPath, "paid", HostSettingsPage.PagePath, ("on", "on"));
        TestAssert.False(accounts.Controls.PaidContentOff, "ticked again: skins and RP are offered");
        TestAssert.False(Flag(home, "paidContentOff"), "platform.json holds the switch on");
    });

    private static Task ChampionListAsync() => OnSettingsPageAsync("league-host-champion-list", async (home, browser, accounts) =>
    {
        var playable = AirGameDtos.ChampionNames.Keys.ToArray();
        var page = await GetAsync(browser, HostSettingsPage.PagePath);
        Has(page, "<h2 id=champions-title>Champions</h2>", $"action={HostSettingsPage.ChampionsPath}", "Tutorials keep their own champions.");
        TestAssert.Equal(playable.Length, page.Split("name=champion value=").Length - 1, "every playable champion has a box");
        foreach (var id in playable) Has(page, $"name=champion value={id} checked>");
        TestAssert.Equal(3, page.Split(" (Battle Training keeps it)</label>").Length - 1, "Battle Training's three are marked as kept there");
        TestAssert.True(playable.All(accounts.Controls.IsEnabled), "nothing is disabled before the first Save");

        // Every playable champion but the first is ticked; the POST also names an id that is not playable, which is ignored.
        var disabled = playable[0];
        var ticked = playable.Skip(1).Select(id => ("champion", id.ToString())).Append(("champion", "999999")).ToArray();
        await SavedAsync(browser, HostSettingsPage.ChampionsPath, "champions", HostSettingsPage.PagePath, ticked);
        TestAssert.Equal(disabled.ToString(), string.Join(",", DisabledList(home) ?? []), "platform.json lists the unticked champion only");
        TestAssert.False(accounts.Controls.IsEnabled(disabled), "the live controls refuse the unticked champion at once");
        TestAssert.True(playable.Skip(1).All(accounts.Controls.IsEnabled), "every ticked champion stays enabled");
        Has(await GetAsync(browser, HostSettingsPage.PagePath + "?saved=champions"), "<p class=notice role=status>Saved.</p>",
            $"name=champion value={disabled}>", $"name=champion value={playable[1]} checked");
    });

    /// <summary>
    /// Batch 751: a platform.json without the two fields starts with skins and RP offered and every champion enabled; one that holds them
    /// starts with them as saved. Loaded through <see cref="Program.LoadSettings"/> and <see cref="PlatformControls.From"/>, which is what
    /// the real start does (RunAsync sets the store's controls from them).
    /// </summary>
    [Check("server.host-settings-controls-from-file", TimeoutSeconds = 120)]
    public static void ControlsStartFromTheSavedFile() => ControlsFromFileAsync().GetAwaiter().GetResult();

    private static async Task ControlsFromFileAsync()
    {
        // Read the champion list the suite's way first (see HostPagesChecks).
        var champions = ChampionCatalog.All.Count;
        var playable = AirGameDtos.ChampionNames.Keys.ToArray();
        var home = Home.Create("league-host-controls-file");
        try
        {
            HostInviteChecks.WriteSettings(home, new { serverExecutable = home.Loop.Paths.ServerExecutable });
            var (settings, _) = Amberline.Platform.Program.LoadSettings(home.Loop.Paths.Settings);
            var controls = PlatformControls.From(settings);
            TestAssert.False(controls.PaidContentOff, "no field: skins and RP are offered");
            TestAssert.True(playable.All(controls.IsEnabled), "no field: every champion is enabled");

            // Saved the way the Settings page saves them: the switch off, and one champion disabled.
            var disabled = playable[0];
            HostInviteChecks.WriteSettings(home, new { serverExecutable = home.Loop.Paths.ServerExecutable, paidContentOff = true, disabledChampions = new[] { disabled } });
            (settings, _) = Amberline.Platform.Program.LoadSettings(home.Loop.Paths.Settings);
            controls = PlatformControls.From(settings);
            TestAssert.True(controls.PaidContentOff, "the saved switch: skins and RP off");
            TestAssert.False(controls.IsEnabled(disabled), "the saved list: the champion is disabled");
            TestAssert.True(playable.Skip(1).All(controls.IsEnabled), "the saved list: the other champions stay enabled");
        }
        finally { Delete(home.Root); }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
    }

    /// <summary>
    /// Batch 751 fix (owner's rule: Settings always renders): while the AIR client's champion data cannot be read, Settings still answers
    /// 200 with a plain line in place of the list, and a Champions save is refused and leaves the stored list as it was.
    /// </summary>
    [Check("server.host-settings-champions-unreadable", TimeoutSeconds = 180)]
    public static void ChampionsUnreadableKeepsSettingsAndTheSavedList() => ChampionsUnreadableAsync().GetAwaiter().GetResult();

    private static Task ChampionsUnreadableAsync() => OnSettingsPageAsync("league-host-champions-unreadable", async (home, browser, accounts) =>
    {
        var playable = AirGameDtos.ChampionNames.Keys.ToArray();
        var disabled = playable[0];
        await SavedAsync(browser, HostSettingsPage.ChampionsPath, "champions", HostSettingsPage.PagePath,
            playable.Skip(1).Select(id => ("champion", id.ToString())).ToArray());
        TestAssert.Equal(disabled.ToString(), string.Join(",", DisabledList(home) ?? []), "the list is saved while the data reads");

        var real = HostSettingsPage.ChampionRoster;
        HostSettingsPage.ChampionRoster = () => throw new FileNotFoundException("The AIR client's files are not here.");
        try
        {
            var page = await GetAsync(browser, HostSettingsPage.PagePath);
            Has(page, "<h2 id=paid-title>Skins and RP</h2>", "<h2 id=champions-title>Champions</h2>",
                "The champion list appears once Amberline can read the AIR client's files.");
            TestAssert.False(page.Contains("name=champion value=", StringComparison.Ordinal), "no list while the data cannot be read");

            using var refused = await PostAsync(browser, HostSettingsPage.ChampionsPath, HostSettingsPage.PagePath, ("champion", playable[1].ToString()));
            TestAssert.Equal(HttpStatusCode.BadRequest, refused.StatusCode, await refused.Content.ReadAsStringAsync());
            TestAssert.Equal(disabled.ToString(), string.Join(",", DisabledList(home) ?? []), "a refused save leaves the stored list unchanged");
            TestAssert.False(accounts.Controls.IsEnabled(disabled), "the live controls keep the disabled champion");
            TestAssert.True(accounts.Controls.IsEnabled(playable[1]), "and the others stay enabled");
        }
        finally { HostSettingsPage.ChampionRoster = real; }
    });

    [Check("server.host-uninstall", TimeoutSeconds = 300)]
    public static void UninstallRemovesOnlyAmberlinesOwnPartsOnceItHasEnded() => UninstallAsync().GetAwaiter().GetResult();

    private static async Task SettingsAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A full Platform reads the champion list; read it the suite's way first (see HostPagesChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-settings");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "setup", run);
            await home.SignInAsync(browser);
            using (var named = await PostAsync(browser, HostSteps.NamePath, HostSteps.NamePath, ("name", "Friday League")))
                TestAssert.Equal(HttpStatusCode.Redirect, named.StatusCode, "the first run's name");
            using (var chosen = await PostAsync(browser, "/setup", await FolderPageAsync(browser, "/setup"), ("game", home.Game), ("air", home.Air)))
                TestAssert.Equal(HttpStatusCode.OK, chosen.StatusCode, "the first run's folders");
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(browser, "ready", run);

            // Settings, in the nav: every first-run answer, each with its own change link, the Play file and the uninstall.
            var page = await GetAsync(browser, HostSettingsPage.PagePath);
            Has(page, "Settings", "aria-current=page>Settings</a>", "<b>Friday League</b>", $"<a href={HostSettingsPage.NamePath}>Change the server name</a>",
                // Batch 652: the two links whose pages load slowly say so at once (data-wait).
                $"<a href={HostSettingsPage.AddressPath} data-wait=", ">Change your address</a>", $"Game folder: {home.Game}", $"AIR client folder: {home.Air}",
                $"<a href={HostSettingsPage.FoldersPath} data-wait=", ">Change the folders</a>", "<a href=/host/updates>Change the update check</a>",
                $"<a href={HostInvite.PlayFilePath} download>Download the Play file</a>", $"<a href=\"{HostAttention.CertificatePath}?from=settings\">Make new Play files</a>",
                $"<a href={HostSettingsPage.UninstallPath}>Uninstall Amberline</a>");
            // Batch 646: from Settings, Cancel (batch 652's word) goes back to Settings (from the dashboard's card, to the dashboard).
            Has(await GetAsync(browser, HostAttention.CertificatePath + "?from=settings"), $"<a class=\"button secondary\" href={HostSettingsPage.PagePath}>Cancel</a>",
                $"action=\"{HostAttention.CertificatePath}?from=settings\"");
            Has(await GetAsync(browser, HostAttention.CertificatePath), $"<a class=\"button secondary\" href={HostDashboard.PagePath}>Cancel</a>");

            // The name: the first run's field and rule, no step number, Cancel back to Settings (batch 652); a saved name says what friends need.
            var name = await GetAsync(browser, HostSettingsPage.NamePath);
            Has(name, "<h1>Change the server name</h1>", $"action={HostSettingsPage.NamePath}", "<a href=/host/settings>Cancel</a><button type=submit>Save</button>",
                "value=\"Friday League\"");
            TestAssert.False(name.Contains("class=step", StringComparison.Ordinal), "Settings' pages have no step number: " + name);
            var refused = await TextAsync(await PostAsync(browser, HostSettingsPage.NamePath, HostSettingsPage.NamePath, ("name", "Friday/League")), HttpStatusCode.BadRequest);
            Has(refused, HostAddress.NameProblem("Friday/League")!, $"action={HostSettingsPage.NamePath}");
            await SavedAsync(browser, HostSettingsPage.NamePath, "name", ("name", "Saturday League"));
            var renamed = await GetAsync(browser, HostSettingsPage.PagePath + "?saved=name");
            Has(renamed, "<p class=server><span class=light aria-hidden=true></span>Saturday League</p>", "<p class=notice role=status>Saved. Send your users the new Play file",
                $"<a href={HostInvite.PlayFilePath} download>Download the Play file</a></p>");
            TestAssert.Equal("Saturday League", Setting(home, "serverName"), "platform.json holds the new name");

            // The address: the first run's choices and checks, the home network only too, without the update question.
            var updates = HostUpdates.StateFile(home.Loop.Paths.Data);
            var updatesBefore = File.Exists(updates) ? File.ReadAllText(updates) : null;
            var address = await GetAsync(browser, HostSettingsPage.AddressPath);
            Has(address, "<h1>Change your address</h1>","How do your players find this PC?", "value=lan", $"action={HostSettingsPage.AddressPath}",
                "<a href=/host/settings>Cancel</a><button type=submit>Save</button>");
            TestAssert.False(address.Contains("name=updates", StringComparison.Ordinal) || address.Contains("class=step", StringComparison.Ordinal),
                "no update question and no step number in Settings: " + address);
            var wrong = await TextAsync(await PostAsync(browser, HostSettingsPage.AddressPath, HostSettingsPage.AddressPath,
                ("choice", HostAddress.Typed), ("address", "192.168.1.20")), HttpStatusCode.BadRequest);
            Has(wrong, WebUtility.HtmlEncode(HostAddress.Check("192.168.1.20").Problem!));
            await SavedAsync(browser, HostSettingsPage.AddressPath, "address", ("choice", HostAddress.Lan), ("lan", "192.168.1.20"));
            var moved = await GetAsync(browser, HostSettingsPage.PagePath + "?saved=address");
            Has(moved, "<p class=notice role=status>Saved. Send your users the new Play file: their old one looks at the old address.",
                "<b>192.168.1.20</b> (home network only)");
            TestAssert.Equal("192.168.1.20", Setting(home, "publicHost"), "platform.json holds the new address");
            TestAssert.Equal(HostAddress.Lan, Setting(home, "addressSource"), "and where it came from");
            TestAssert.Equal(updatesBefore, File.Exists(updates) ? File.ReadAllText(updates) : null, "Settings' address leaves the update choice alone");

            // Batch 680: Cheat commands lists every login (batch 666's one switch is gone); before the first login it says so. The list,
            // its Save and its migration: server.host-cheat-list-only-the-host.
            Has(page, "<h2 id=cheats-title>Cheat commands</h2>", "Nobody has a login yet.");
            TestAssert.False(page.Contains("Allow cheat commands in my games", StringComparison.Ordinal) || page.Contains($"action={HostSettingsPage.CheatsPath}", StringComparison.Ordinal),
                "no switch and no empty form: " + page);
            TestAssert.True(CheatList(home) is null, "platform.json ticks nobody");

            // The folders: the first run's rows and checks; a saved choice restarts Amberline (in this process: the check's loop has no next one).
            var folders = await FolderPageAsync(browser, HostSettingsPage.FoldersPath);
            Has(folders, "Found:</span> " + WebUtility.HtmlEncode(home.Game), $"action={HostSettingsPage.FoldersPath}",
                "<h1>Change the folders</h1>", "<a href=/host/settings>Cancel</a><button>Save</button>", "Saving restarts Amberline");
            TestAssert.False(folders.Contains("class=step", StringComparison.Ordinal), "no step number: " + folders);
            var nowhere = await TextAsync(await PostAsync(browser, HostSettingsPage.FoldersPath, folders,
                ("game", home.Game), ("air", home.Air), ("paste", Path.Combine(home.Root, "nowhere"))), HttpStatusCode.BadRequest);
            Has(nowhere, "Amberline cannot find that folder.");
            var restarting = await TextAsync(await PostAsync(browser, HostSettingsPage.FoldersPath, folders, ("game", home.Game), ("air", home.Air)), HttpStatusCode.OK);
            Has(restarting, "<h1>Starting Amberline</h1>", "http-equiv=refresh", "&to=settings");
            await WaitForStartsAsync(home.Loop, 3, run);
            await WaitForHealthAsync(browser, "ready", run);
            // Batch 649: the restart's reopen lands on Settings, which says the folders are saved (the browser is signed in: no new code).
            var reopen = System.Text.RegularExpressions.Regex.Match(restarting, "url=([^\"]+)").Groups[1].Value;
            using (var landed = await browser.GetAsync(reopen))
                TestAssert.Equal(HostSettingsPage.PagePath + "?saved=folders", landed.Headers.Location?.OriginalString, "the folders' restart lands on Settings");
            Has(await GetAsync(browser, HostSettingsPage.PagePath + "?saved=folders"), "<p class=notice role=status>Saved. Amberline restarted to use the new folders.");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    private static async Task UninstallAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var outside = AirBootstrapChecks.TemporaryDirectory("league-host-uninstall-outside");
        try
        {
            await FromSettingsAsync(outside);
            await FromTheWindowAsync(outside);
            Plans(outside);
        }
        finally { Delete(outside); }
    }

    /// <summary>
    /// Settings' uninstall on a page-only instance whose games are a list: refused while a game runs, the typed confirmation, then the
    /// firewall's delete-only command, the icon, the pointer, the stop, and the cleanup run on the temporary install. The data is kept.
    /// </summary>
    private static async Task FromSettingsAsync(string outside)
    {
        var rules = new List<ShownRule> { Rule(HostFirewall.SignInRule), Rule(HostFirewall.GamesRule), Rule("A rule of another program") };
        var commands = new List<ElevatedCommand>();
        var network = new HostNetwork(_ => Task.FromResult<UpnpGateway?>(null),
            _ => { lock (rules) return Task.FromResult<IReadOnlyList<ShownRule>?>([.. rules]); },
            (command, _) =>
            {
                lock (rules)
                {
                    commands.Add(command);
                    rules.RemoveAll(rule => rule.Name.StartsWith("Amberline", StringComparison.Ordinal));
                }
                return Task.FromResult<int?>(0);
            },
            () => (null, null));
        var home = Home.Create("league-host-uninstall", network: network);
        var places = new UninstallPlaces(Path.Combine(outside, "Desktop"), Path.Combine(outside, "Local"), Path.Combine(outside, "Temp"));
        home.Loop.Uninstall = places;
        home.ChooseFolders();
        // Home's folder as a one-folder install: the AIR copy inside it (Home's), the game outside it (the shipped release), a read-only
        // file in the program, the version before, and a file of the host's own, which is not Amberline's.
        var root = home.Root;
        File.WriteAllText(Path.Combine(root, "amberline-install.json"), "{}");
        File.WriteAllText(Path.Combine(root, HostInstall.ReadMeName), "AMBERLINE");
        File.WriteAllText(Path.Combine(root, HostUninstall.CommandName), HostUninstall.Command);
        Directory.CreateDirectory(Path.Combine(root, "app.previous"));
        File.WriteAllText(Path.Combine(root, "app.previous", "old.txt"), "the version before");
        var platform = Path.Combine(root, "app", "platform", HostInstall.PlatformFile);
        Directory.CreateDirectory(Path.GetDirectoryName(platform)!);
        File.WriteAllText(platform, "stand-in Platform");
        var readOnly = Path.Combine(root, "app", "platform", "read-only.txt");
        File.WriteAllText(readOnly, "read-only");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(root, "my notes.txt"), "the host's own file");
        var icon = Path.Combine(places.Desktop!, HostInstall.ShortcutName);
        Shortcut(icon, platform);
        var pointer = Path.Combine(places.LocalAppData!, "Amberline", "install.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllText(pointer, JsonSerializer.Serialize(new { folder = root }));
        var gameExe = Path.Combine(home.Game, HostFolders.GameExecutable);
        var gameBefore = File.Exists(gameExe);

        var games = new List<DashboardGame> { new(2, 0, "Playing") };
        int Running() { lock (games) return games.Count; }
        var run = Task.Run(() => RunSettingsAsync(home, Running));
        try
        {
            using var browser = home.Client();
            async Task<int> Watched() => await run ? 1 : 0;
            await WaitForHealthAsync(browser, "ready", Watched());
            await home.SignInAsync(browser);

            // While this Amberline runs, the window's uninstaller sends the host to its page and changes nothing.
            var refusedScreen = new StringWriter();
            var (refusedExit, refusedCleanup) = await HostUninstall.ConsoleAsync(home.Loop.Paths, places, network, refusedScreen,
                () => throw new InvalidOperationException("no question is asked while Amberline runs"));
            TestAssert.True(refusedExit == 1 && refusedCleanup is null && refusedScreen.ToString().Contains("Amberline is running.", StringComparison.Ordinal),
                refusedScreen.ToString());

            // A running game: the uninstall and a folder change are refused.
            var busy = await GetAsync(browser, HostSettingsPage.UninstallPath);
            Has(busy, "A game is running. Uninstall Amberline when it has ended");
            TestAssert.False(busy.Contains("name=confirm", StringComparison.Ordinal), "no form while a game runs: " + busy);
            var folders = await FolderPageAsync(browser, HostSettingsPage.FoldersPath);
            Has(await TextAsync(await PostAsync(browser, HostSettingsPage.FoldersPath, folders, ("game", home.Game), ("air", home.Air)), HttpStatusCode.Conflict),
                "A game is running. Change the folders when it has ended");
            TestAssert.Equal(1, home.Loop.Starts, "a refused folder change does not restart");

            lock (games) games.Clear();
            var plan = HostUninstall.Plan(home.Loop.Paths, new PlatformSettings(home.Game, null!, AirRoot: home.Air), places.LocalAppData)!;
            TestAssert.True(plan.OneFolder && plan.Folder == root, $"the install folder: {plan.Folder}");
            TestAssert.Equal("my notes.txt", string.Join(", ", plan.Others), "only the host's own file is not Amberline's");
            var form = await GetAsync(browser, HostSettingsPage.UninstallPath);
            Has(form, $"Amberline's folder: <b>{WebUtility.HtmlEncode(root)}</b> ({HostUninstall.Size(plan.Bytes)}).",
                $"Also delete the game and AIR client copies ({HostUninstall.Size(plan.Size(UninstallKind.Clients))})",
                $"Also delete your logins and settings ({HostUninstall.Size(plan.Size(UninstallKind.Data))})",
                WebUtility.HtmlEncode($"The game folder you chose stays where it is: {home.Game}"), "Type uninstall to confirm", "Windows asks once");

            var unconfirmed = await TextAsync(await PostAsync(browser, HostSettingsPage.UninstallPath, form, ("clients", "on"), ("confirm", "yes")), HttpStatusCode.BadRequest);
            Has(unconfirmed, "Type uninstall in the box to confirm.", "id=confirm-error", "name=clients value=on checked");
            TestAssert.True(commands.Count == 0 && File.Exists(icon) && File.Exists(pointer), "an unconfirmed uninstall changes nothing");

            var done = await TextAsync(await PostAsync(browser, HostSettingsPage.UninstallPath, form, ("clients", "on"), ("confirm", " Uninstall ")), HttpStatusCode.OK);
            Has(done, "<h1>Amberline is uninstalled</h1>", "Amberline&#39;s rules in Windows Firewall are removed.", "The Amberline icon on your desktop is removed.",
                WebUtility.HtmlEncode($"Your logins, your settings, the server's certificate and the logs: {Path.Combine(root, "data")}"),
                WebUtility.HtmlEncode($"Other things in {root}, which are not Amberline's: my notes.txt"));
            TestAssert.False(await run.WaitAsync(TimeSpan.FromSeconds(30)), "the uninstall stops Amberline");
        }
        finally
        {
            if (!run.IsCompleted)
            {
                home.Loop.Stop();
                await run.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        // One prompt: the delete-only command for the two rules that existed, through System32's cmd and netsh.
        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        TestAssert.Equal(1, commands.Count, "Windows asks once");
        TestAssert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), commands[0].FileName);
        TestAssert.Equal($"/d /c \"\"{netsh}\" advfirewall firewall delete rule name=\"Amberline sign-in\" && \"{netsh}\" advfirewall firewall delete rule name=\"Amberline games\"\"",
            commands[0].Arguments, "the elevated command deletes the two rules only");
        TestAssert.True(HostFirewall.RemoveCommand([Rule("A rule of another program")], Environment.SystemDirectory) is null, "no rule of Amberline's: nothing to ask");
        TestAssert.False(File.Exists(icon) || File.Exists(pointer) || Directory.Exists(Path.GetDirectoryName(pointer)), "the icon, the pointer and its folder are gone");
        var note = File.ReadAllText(Path.Combine(root, HostInstall.ReadMeName));
        foreach (var phrase in new[] { "AMBERLINE (UNINSTALLED)", "- data", $"Delete this folder when you no longer need them: {root}", "install Amberline into this same folder" })
            TestAssert.True(note.Contains(phrase, StringComparison.Ordinal), $"the kept note says <{phrase}>:\n{note}");

        await CleanUpAsync(home.Loop.Cleanup, root, places.Temp!);
        TestAssert.Equal("READ ME.txt, amberline-install.json, data, my notes.txt", Entries(root), "what was kept stays, the rest is gone");
        TestAssert.Equal(gameBefore, File.Exists(gameExe), "the game folder outside the install is untouched");
        Delete(root);
    }

    /// <summary>
    /// Uninstall Amberline.cmd's questions: a no changes nothing; then everything goes, the icon of another Amberline stays, and the
    /// folder (a name with a non-ASCII letter and a percent sign) is gone after the cleanup.
    /// </summary>
    private static async Task FromTheWindowAsync(string outside)
    {
        var tree = Path.Combine(outside, "window");
        var folder = Path.Combine(tree, "Amberline ü 100%");
        var places = new UninstallPlaces(Path.Combine(tree, "Desktop"), Path.Combine(tree, "Local"), Path.Combine(tree, "Temp"));
        var platform = Path.Combine(folder, "app", "platform", HostInstall.PlatformFile);
        foreach (var file in new[] { platform, Path.Combine(folder, "game", "projects", "lol_game_client", "releases", "0.0.0.51", HostFolders.GameExecutable),
                     Path.Combine(folder, "air", "LolClient.exe"), Path.Combine(folder, "data", "accounts.json") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "stand-in");
        }
        File.WriteAllText(Path.Combine(folder, "amberline-install.json"), "{}");
        File.WriteAllText(Path.Combine(folder, HostUninstall.CommandName), HostUninstall.Command);
        // A free port, so the window's "is Amberline running" question never reaches a real one.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        File.WriteAllText(Path.Combine(folder, "data", "platform.json"), JsonSerializer.Serialize(new { httpPort = port }));
        var another = Path.Combine(tree, "Another Amberline", "app", "platform", HostInstall.PlatformFile);
        Directory.CreateDirectory(Path.GetDirectoryName(another)!);
        File.WriteAllText(another, "stand-in");
        var icon = Path.Combine(places.Desktop!, HostInstall.ShortcutName);
        Shortcut(icon, another);
        var pointer = Path.Combine(places.LocalAppData!, "Amberline", "install.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllText(pointer, JsonSerializer.Serialize(new { folder }));
        var paths = HostPaths.Resolve(null, places.LocalAppData!, Path.Combine(folder, "app", "platform"));
        TestAssert.Equal(Path.Combine(folder, "data"), paths.Data, "the install's data folder");
        var network = new HostNetwork(_ => Task.FromResult<UpnpGateway?>(null), _ => Task.FromResult<IReadOnlyList<ShownRule>?>([Rule("A rule of another program")]),
            (_, _) => throw new InvalidOperationException("nothing to elevate"), () => (null, null));

        var answers = new Queue<string>(["", "", "no"]);
        var kept = new StringWriter();
        var (keptExit, keptCleanup) = await HostUninstall.ConsoleAsync(paths, places, network, kept, () => answers.TryDequeue(out var answer) ? answer : null);
        TestAssert.True(keptExit == 1 && keptCleanup is null && kept.ToString().Contains("Nothing was uninstalled.", StringComparison.Ordinal), kept.ToString());
        TestAssert.True(File.Exists(pointer) && File.Exists(platform), "a no changes nothing");

        answers = new(["y", "Yes", "UNINSTALL"]);
        var screen = new StringWriter();
        var (exit, cleanup) = await HostUninstall.ConsoleAsync(paths, places, network, screen, () => answers.TryDequeue(out var answer) ? answer : null);
        var said = screen.ToString();
        TestAssert.Equal(0, exit, said);
        foreach (var phrase in new[] { $"Amberline's folder: {folder} (", "Also delete the game and AIR client copies in it", "Also delete your logins and settings",
                     "Type uninstall and press Enter", HostUninstall.YesNoLine, "Uninstalling. This takes a few seconds.", "Amberline is uninstalled.",
                     "Windows Firewall has no rule of Amberline's to remove.", $"starts another Amberline ({another}), so it stays.",
                     $"Amberline's folder {folder} is deleted, with everything in it, as soon as Amberline has closed." })
            TestAssert.True(said.Contains(phrase, StringComparison.Ordinal), $"the window says <{phrase}>:\n{said}");
        TestAssert.True(File.Exists(icon) && !File.Exists(pointer), "another Amberline's icon stays; this install's pointer goes");

        await CleanUpAsync(cleanup, folder, places.Temp!);
        TestAssert.False(Directory.Exists(folder), "the whole install folder is gone");
        TestAssert.True(File.Exists(another), "the other Amberline is untouched");
    }

    /// <summary>What is not installed has nothing to uninstall; an install from before batch 637 is the Amberline folder of the profile.</summary>
    private static void Plans(string outside)
    {
        var settings = new PlatformSettings(null!, null!);
        var development = Path.Combine(outside, "build", "platform");
        Directory.CreateDirectory(development);
        TestAssert.True(HostUninstall.Plan(HostPaths.Resolve(null, Path.Combine(outside, "Local"), development), settings, Path.Combine(outside, "Local")) is null,
            "a development build is not installed");
        var local = Path.Combine(outside, "Old");
        var old = Path.Combine(local, "Amberline", "app", "platform");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(Path.Combine(local, "Amberline", "data"));
        var paths = HostPaths.Resolve(null, local, old);
        var plan = HostUninstall.Plan(paths, settings, local)!;
        TestAssert.True(!plan.OneFolder && plan.Folder == Path.Combine(local, "Amberline") && plan.Others.Count == 0, $"the old install folder: {plan.Folder}");
        TestAssert.Equal("app, data", string.Join(", ", plan.Parts.Select(part => Path.GetFileName(part.Path))), "its program and its data");
        TestAssert.True(HostUninstall.Plan(paths, settings, null) is null, "without the profile folder it is not recognised");
        TestAssert.True(HostUninstall.Command.Contains("\"%~dp0app\\platform\\" + HostInstall.PlatformFile + "\" --uninstall & exit /b\r\n", StringComparison.Ordinal),
            HostUninstall.Command);
        TestAssert.Equal("2.4 GB", HostUninstall.Size(2_400_000_000), "sizes as people read them");
        TestAssert.Equal("12 MB", HostUninstall.Size(12_345_678));
    }

    /// <summary>
    /// Runs the cleanup the uninstall handed over, after checking that every path it names lies in <paramref name="install"/> or in
    /// <paramref name="temp"/>; it must end by itself and delete itself.
    /// </summary>
    private static async Task CleanUpAsync(ProcessStartInfo? cleanup, string install, string temp)
    {
        TestAssert.True(cleanup is not null, "the uninstall hands the folder to a cleanup");
        var script = Directory.GetFiles(temp, "amberline-uninstall-*.cmd").Single();
        TestAssert.True(cleanup!.Arguments.Contains(script, StringComparison.Ordinal) && cleanup.FileName == Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"{cleanup.FileName} {cleanup.Arguments}");
        var text = File.ReadAllText(script);
        foreach (Match quoted in Regex.Matches(text, "\"([A-Za-z]:\\\\[^\"]*)\""))
        {
            var path = quoted.Groups[1].Value.Replace("%%", "%", StringComparison.Ordinal);
            TestAssert.True(path.StartsWith(install, StringComparison.OrdinalIgnoreCase), $"the cleanup names only the install's parts: {path}\n{text}");
        }
        using var process = Process.Start(cleanup)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(150));
        TestAssert.False(File.Exists(script), "the cleanup deletes itself");
    }

    [Check("server.host-settings-folder-copy", TimeoutSeconds = 180)]
    public static void SettingsFolderPageOffersTheCopyInBeforeItRestarts() => SettingsCopyAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 642's copy-in from Settings' folder page in a one-folder install (synthetic clients in a download folder, a page-only
    /// Settings, so no full Platform reads the synthetic game): the offer with the nav and no step number, the copy under Settings'
    /// own address, and the done page, whose Continue saves the copies and restarts.
    /// </summary>
    private static async Task SettingsCopyAsync()
    {
        const string Folders = HostSettingsPage.FoldersPath, Copy = HostSettingsPage.FoldersPath + "/copy";
        var folder = HostFolderCopyChecks.OneFolder.Create("league-settings-copy", downloads =>
            HostFolderCopyChecks.Download(Path.Combine(downloads, "League 2011"), Path.Combine(downloads, "League 2011", "lol_air_client")));
        var run = Task.Run(() => RunSettingsAsync(folder.Home, () => 0));
        try
        {
            using var browser = folder.Home.Client();
            async Task<int> Watched() => await run ? 1 : 0;
            await WaitForHealthAsync(browser, "ready", Watched());
            await folder.Home.SignInAsync(browser);
            var offer = await TextAsync(await PostAsync(browser, Folders, await FolderPageAsync(browser, Folders), ("game", folder.Game), ("air", folder.Air)),
                HttpStatusCode.OK);
            // Batch 649: reached from Settings, the copy pages say the folder page's words; the question moves into the text.
            Has(offer, "<h1>Change the folders</h1>", $"<p>Copy your game files into {WebUtility.HtmlEncode(folder.Install)}?</p>", "aria-current=page>Settings</a>", $"<form method=post action={Copy}>",
                $"<a href={Folders}>Back</a>", $"formaction={Folders} name=keep value=1>Use them where they are</button>");
            TestAssert.False(offer.Contains("class=step", StringComparison.Ordinal) || File.ReadAllText(folder.Settings).Contains("dataRoot", StringComparison.Ordinal),
                "no step number, and nothing saved before the host answers: " + offer);
            using (var started = await PostAsync(browser, Copy, offer, ("game", folder.Game), ("air", folder.Air), ("delete", "no")))
                TestAssert.True(started.StatusCode == HttpStatusCode.Redirect && started.Headers.Location?.OriginalString == Copy, "the copy starts on Settings' own page");
            using (var script = await browser.GetAsync(Copy + "/page.js"))
                Has(await script.Content.ReadAsStringAsync(), $"fetch('{Copy}/status.json'");
            var done = await CopiedAsync(browser, Copy);
            var (game, air) = (Path.Combine(folder.Install, @"game\projects\lol_game_client\releases\0.0.0.51"), Path.Combine(folder.Install, "air"));
            Has(done, "<h1>Change the folders</h1>", "<p class=notice>Done: the game files are in", "aria-current=page>Settings</a>", $"<form method=post action={Folders} data-wait=",
                $"name=game value=\"{WebUtility.HtmlEncode(game)}\"");
            Has(await TextAsync(await PostAsync(browser, Folders, done, ("game", game), ("air", air)), HttpStatusCode.OK), "<h1>Starting Amberline</h1>");
            TestAssert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)), "Continue saves the copies and restarts");
            folder.Names(game, air);
            folder.NothingHalfDone();
        }
        finally
        {
            if (!run.IsCompleted) folder.Home.Loop.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            Delete(folder.Home.Root);
            Delete(folder.Downloads);
        }
    }

    /// <summary>The copy's page at <paramref name="copy"/> once its status says it ended (at most a minute).</summary>
    internal static async Task<string> CopiedAsync(HttpClient browser, string copy)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            using var status = JsonDocument.Parse(await browser.GetStringAsync(copy + "/status.json"));
            if (status.RootElement.GetProperty("state").GetString() != "copying") return await browser.GetStringAsync(copy);
            TestAssert.True(DateTime.UtcNow < deadline, "the copy ended within a minute");
            await Task.Delay(50);
        }
    }

    /// <summary>A page-only Settings on the home's page port (the gate, the shell, the steps without logins, Settings), until Stop or Restart.</summary>
    internal static async Task<bool> RunSettingsAsync(Home home, Func<int> games, PlatformAccounts? accounts = null, Func<DateOnly>? today = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, home.Port));
        builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(home.Loop.Paths.Keys));
        await using var app = builder.Build();
        // The full Platform's policy for its /host pages (HostDashboard.Map).
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(HostDashboard.PagePath)) context.Response.Headers.ContentSecurityPolicy = HostDashboard.Policy;
            return next(context);
        });
        app.MapGet("/health", () => Results.Json(new { status = "ready", service = "Amberline.AirPlatform", protocol = 1, mode = "home" }));
        HostGate.Use(app, home.Port, home.Loop.Sessions, HostSettingsPage.PagePath);
        var steps = HostSteps.Map(app, home.Loop, null);
        HostPages.Use(app, home.Loop, () => steps.ServerName, nav: true);
        // Batch 656: the sign-up's switch, saved off; this page-only Settings serves no friends' port and makes no login (no account store).
        using var signUp = new HostSelfRegistration(home.Loop, null!, home.RemotePort, new string('A', 64), on: false);
        // The day the news is dated and read as: the local day, unless a check injects another (Program.RunAsync reads the local day).
        var clock = today ?? (() => LauncherNews.LocalDay());
        HostSettingsPage.Map(app, home.Loop, steps, games, signUp, accounts, today: clock);
        // NOT RETAIL (batch 761): the launcher's news beside Settings, as Program.RunAsync maps it on the full Platform (LauncherNews).
        if (accounts is not null) LauncherNews.Map(app, () => accounts.Controls.LauncherNews, () => steps.ServerName, clock);
        return await home.Loop.RunAsync(app, home.Port);
    }

    private static ShownRule Rule(string name) => new(name, new Dictionary<string, string>());

    private static void Shortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            var link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static string Entries(string folder) => string.Join(", ", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    /// <summary>platform.json's <c>cheatAccounts</c> (batch 680), or null while it has none.</summary>
    private static long[]? CheatList(Home home)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings));
        return json.RootElement.TryGetProperty("cheatAccounts", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(id => id.GetInt64()).ToArray() : null;
    }

    /// <summary>platform.json's <c>disabledChampions</c> (batch 751), or null while it has none.</summary>
    private static int[]? DisabledList(Home home)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings));
        return json.RootElement.TryGetProperty("disabledChampions", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(id => id.GetInt32()).ToArray() : null;
    }

    /// <summary>A switch platform.json holds on; the field is written only when it is on (batch 751), so a missing one is off.</summary>
    private static bool Flag(Home home, string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings));
        return json.RootElement.TryGetProperty(name, out var value) && value.GetBoolean();
    }

    private static string? Setting(Home home, string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings));
        return json.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static void Has(string page, params string[] phrases)
    {
        foreach (var phrase in phrases) TestAssert.True(page.Contains(phrase, StringComparison.Ordinal), $"the page does not say <{phrase}>:\n{page}");
    }

    /// <summary>A save that sends the browser back to Settings with its note.</summary>
    private static Task SavedAsync(HttpClient browser, string path, string saved, params (string Name, string Value)[] fields) =>
        SavedAsync(browser, path, saved, path, fields);

    /// <summary>The same, for a form on <paramref name="tokenPage"/> (batch 666: Settings' own cheat switch has no page of its own).</summary>
    private static async Task SavedAsync(HttpClient browser, string path, string saved, string tokenPage, params (string Name, string Value)[] fields)
    {
        using var answer = await PostAsync(browser, path, tokenPage, fields);
        TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, await answer.Content.ReadAsStringAsync());
        TestAssert.Equal($"{HostSettingsPage.PagePath}?saved={saved}", answer.Headers.Location?.OriginalString, "back to Settings");
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        using var answer = await client.GetAsync(path);
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, $"GET {path}: {page}");
        return page;
    }

    private static async Task<string> TextAsync(HttpResponseMessage answer, HttpStatusCode status)
    {
        using (answer)
        {
            var page = await answer.Content.ReadAsStringAsync();
            TestAssert.Equal(status, answer.StatusCode, page);
            return page;
        }
    }

    /// <summary>Posts a form with the antiforgery token of <paramref name="tokenPage"/> (a path to fetch, or a page already fetched).</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string tokenPage, params (string Name, string Value)[] fields)
    {
        var token = Token(tokenPage.StartsWith('/') ? await client.GetStringAsync(tokenPage) : tokenPage);
        // A list, not a dictionary: batch 680's Cheat commands sends one field per ticked box.
        return await client.PostAsync(path, new FormUrlEncodedContent([token, .. fields.Select(field => KeyValuePair.Create(field.Name, field.Value))]));
    }

    /// <summary>A folder page once its first look is done (a slow disk shows a page that comes back by itself first).</summary>
    private static async Task<string> FolderPageAsync(HttpClient browser, string path)
    {
        for (var tries = 0; tries < 40; tries++)
        {
            var page = await GetAsync(browser, path);
            if (page.Contains("<label for=paste>", StringComparison.Ordinal)) return page;
            await Task.Delay(250);
        }
        throw new TimeoutException($"{path} never showed its form");
    }
}
