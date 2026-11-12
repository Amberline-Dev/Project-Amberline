using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Platform;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's pages as a keyboard and a screen reader meet them (batch 638): every page <c>--home</c> serves, rendered by the
/// real Platform in setup mode and in the full Platform. Temporary folders, free loopback ports, made-up logins (<c>host_pages</c>,
/// <c>user1</c>); no browser, so what a page looks like (contrast, 360 px, 200% zoom) stays the owner's to see.
/// </summary>
public static class HostPagesChecks
{
    private const string Password = "Synthetic-pages-Pw-12";

    /// <summary>The pages the walk renders while server.host-pages-say-users runs it (null for every other check).</summary>
    private static readonly AsyncLocal<List<(string What, string Text)>?> Walked = new();

    [Check("server.host-pages-accessible", TimeoutSeconds = 240)]
    public static void EveryHostPageIsLabelledLandmarkedAndStyledFromItsOwnFile() => PagesAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Layout C and the /register look (batch 748): the Dashboard has the wordmark, the gold frame and the sidebar with the server's
    /// name and Dashboard marked; Updates shows its badge only once the update check knows a newer release; a first-run step has the frame and
    /// no sidebar; the sheet is dark only; no page has an inline style.
    /// </summary>
    [Check("server.host-shell-layout-c", TimeoutSeconds = 180)]
    public static void ShellIsLayoutCInTheRegisterLook() => ShellAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Host pages redesign, task 5: no host page says "friend", since the server serves the general public. It walks the pages of
    /// <see cref="EveryHostPageIsLabelledLandmarkedAndStyledFromItsOwnFile"/> and searches what a person reads on each: the text outside
    /// the tags, the values of alt, title, aria-label, placeholder and data-wait, the value of each button, and the status the Dashboard's
    /// script fills in (its string values; JSON names and check ids are not words on the page).
    /// </summary>
    [Check("server.host-pages-say-users", TimeoutSeconds = 240)]
    public static void NoHostPageSaysFriend() => Task.Run(SayUsersAsync).GetAwaiter().GetResult();

    private static async Task SayUsersAsync()
    {
        // Set inside Task.Run's own flow, so only this check's walk hands its pages over.
        var walked = new List<(string What, string Text)>();
        Walked.Value = walked;
        await PagesAsync();
        var failures = new List<string>();
        foreach (var (what, page) in walked)
        {
            var words = what == "status.json" ? JsonWords(page) : VisibleText(page);
            foreach (Match match in Regex.Matches(words, "friend", RegexOptions.IgnoreCase))
                failures.Add($"{what}: ...{words[Math.Max(0, match.Index - 60)..Math.Min(words.Length, match.Index + 60)]}...");
        }
        TestAssert.True(walked.Count > 0 && failures.Count == 0,
            $"the walk rendered {walked.Count} pages; visible text says friend {failures.Count} times:\n" + string.Join("\n", failures));
    }

    private static async Task ShellAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list: read it the suite's way first (see HostInviteChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-shell");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_shell", Password, "Host Shell").Id;
        HostInviteChecks.WriteSettings(home, new { serverName = "Test Server", publicHost = "amberline.example.test", addressSource = HostAddress.Typed, hostAccount = hostId });
        // The running build's version (BUILD.json beside its app folder), which the Platform reads once when it starts.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(home.Loop.Paths.App)!, "BUILD.json"), "{\"version\":\"1.0.0\"}");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);

            var dashboard = await browser.GetStringAsync(HostDashboard.PagePath);
            TestAssert.True(dashboard.Contains("<header><p class=wordmark>Amberline</p></header>", StringComparison.Ordinal) &&
                dashboard.Contains("<div class=frame><nav class=side aria-label=\"Amberline\"><p class=server><span class=light aria-hidden=true></span>Test Server</p>", StringComparison.Ordinal) &&
                dashboard.Contains("<a href=/host aria-current=page>Dashboard</a>", StringComparison.Ordinal) &&
                dashboard.Contains("<main id=main tabindex=-1 class=page>", StringComparison.Ordinal), "the Dashboard's shell is layout C: " + dashboard);
            TestAssert.True(dashboard.Contains("<a href=/host/updates>Updates</a>", StringComparison.Ordinal), "no newer release, no badge: " + dashboard);

            // The update check knows a newer release (updates.json, read on each page; the file is all it reads, nothing asks GitHub): the badge shows.
            Directory.CreateDirectory(Path.GetDirectoryName(HostUpdates.StateFile(home.Loop.Paths.Data))!);
            File.WriteAllText(HostUpdates.StateFile(home.Loop.Paths.Data), "{\"latest\":\"9.9.9\"}");
            var badged = await browser.GetStringAsync(HostDashboard.PagePath);
            TestAssert.True(badged.Contains("<a href=/host/updates>Updates <span class=badge>1<span class=vh> update available</span></span></a>", StringComparison.Ordinal),
                "a newer release shows the badge on Updates: " + badged);

            // A first-run step (step 4, the address): the frame without a sidebar, and the step's indicator inside it.
            var address = await browser.GetStringAsync("/host/setup/address");
            TestAssert.True(address.Contains("<div class=frame><main id=main tabindex=-1 class=page><p class=step>Step 4 of 4: Your address</p>", StringComparison.Ordinal) &&
                !address.Contains("class=side", StringComparison.Ordinal), "a first-run step has the frame and no sidebar: " + address);
            foreach (var page in new[] { dashboard, badged, address })
                TestAssert.False(page.Contains(" style=", StringComparison.Ordinal), "no inline style: " + page);

            var css = await browser.GetStringAsync(HostPages.StylePath);
            TestAssert.True(css.Contains("--gold:#c9ab6f", StringComparison.Ordinal) && css.Contains(".side", StringComparison.Ordinal) &&
                css.Contains("color-scheme:dark", StringComparison.Ordinal) && !css.Contains("prefers-color-scheme", StringComparison.Ordinal),
                "the host sheet is the register look, dark only");
            // Batch 748: the sign-up sheet keeps its bytes (the hash of batch 706's text), so its versioned address has not changed.
            TestAssert.Equal("/register/style.css?v=927f2fbdd95e", HostPages.PublicStyleHref, "the sign-up sheet's bytes changed");
            // Final fix 4: the version line spans the footer, so it stands centred under the frame (the sheet's paragraphs stop at 65ch).
            TestAssert.True(css.Contains("footer p{max-width:none;margin:0}", StringComparison.Ordinal), "the footer's line is not held to 65ch");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    private static async Task PagesAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list: read it the suite's way first (see HostInviteChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-pages");
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            using var stranger = home.Client();
            await WaitForHealthAsync(browser, "setup", run);

            // A browser without the host's session: the gate's page is bare (no server name, no nav), and the stylesheet is refused.
            var refused = await PageAsync(stranger, HttpMethod.Get, HostSteps.NamePath, HttpStatusCode.Forbidden, HostPages.Policy);
            TestAssert.True(refused.Contains("<p class=wordmark>Amberline</p>", StringComparison.Ordinal) && !refused.Contains("<nav", StringComparison.Ordinal),
                "the refusal page names no server and has no nav: " + refused);
            using (var sheet = await stranger.GetAsync(HostPages.StylePath))
                TestAssert.Equal(HttpStatusCode.Forbidden, sheet.StatusCode, "the stylesheet is a host route");

            await home.SignInAsync(browser);
            await StylesheetAsync(browser, HostPages.Policy);

            // Setup mode: the name step and the folder step, each with an error, and setup mode's Stop.
            Step(await PageAsync(browser, HttpMethod.Get, HostSteps.NamePath, HttpStatusCode.OK, HostPages.Policy), 1);
            Errors(Step(await FormAsync(browser, HostSteps.NamePath, HttpStatusCode.BadRequest, HostPages.Policy, ("name", "Friday/League")), 1), "name");
            using (var named = await PostAsync(browser, HostSteps.NamePath, ("name", "Friday League")))
                TestAssert.Equal(HttpStatusCode.Redirect, named.StatusCode, "the name is saved");
            var folders = await FolderPageAsync(browser);
            Step(folders, 2);
            TestAssert.True(folders.Contains("<p class=server>Friday League</p>", StringComparison.Ordinal), "the header names the server: " + folders);
            TestAssert.True(folders.Contains("<a href=/host/setup/name>Back</a><button>Continue</button>", StringComparison.Ordinal),
                "Back and Continue side by side, at the end of the step: " + folders);
            Errors(Step(await FormAsync(browser, "/setup", HttpStatusCode.BadRequest, HostPages.Policy,
                ("game", ""), ("air", ""), ("paste", Path.Combine(home.Root, "nowhere"))), 2), "paste");
            // Batch 643, seen in a browser: the start's problem (no folders saved yet) went on standing once both rows were found.
            var found = Step(await FormAsync(browser, "/setup", HttpStatusCode.OK, HostPages.Policy, ("game", home.Game), ("air", home.Air), ("paste", home.Game)), 2);
            TestAssert.True(Regex.Matches(found, "<span class=ok>").Count == 2 && !found.Contains("class=error-summary", StringComparison.Ordinal) &&
                found.Contains("<title>Your game files · Amberline</title>", StringComparison.Ordinal), "both folders found: no problem stands: " + found);
            await PageAsync(browser, HttpMethod.Get, HostInstall.StopPath, HttpStatusCode.OK, HostPages.Policy);

            // The folders start the full Platform (in this process: the check's loop has no next process).
            await FormAsync(browser, "/setup", HttpStatusCode.OK, HostPages.Policy, ("game", home.Game), ("air", home.Air));
            await WaitForStartsAsync(home.Loop, 2, run);
            await WaitForHealthAsync(browser, "ready", run);
            await StylesheetAsync(browser, HostDashboard.Policy);

            // The full Platform's steps: the login (form, error, done) and the address (form, error).
            Step(await PageAsync(browser, HttpMethod.Get, "/host/setup/login", HttpStatusCode.OK, HostDashboard.Policy), 3);
            var login = new[] { ("username", "host_pages"), ("summoner", "Host Pages"), ("mode", "maxed") };
            Errors(Step(await FormAsync(browser, "/host/setup/login", HttpStatusCode.BadRequest, HostDashboard.Policy, [.. login, ("password", "short")]), 3),
                "password");
            using (var created = await PostAsync(browser, "/host/setup/login", [.. login, ("password", Password)]))
                TestAssert.Equal(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
            Step(await PageAsync(browser, HttpMethod.Get, "/host/setup/login", HttpStatusCode.OK, HostDashboard.Policy), 3);
            Step(await PageAsync(browser, HttpMethod.Get, "/host/setup/address", HttpStatusCode.OK, HostDashboard.Policy), 4);
            Errors(Step(await FormAsync(browser, "/host/setup/address", HttpStatusCode.BadRequest, HostDashboard.Policy,
                ("choice", HostAddress.Typed), ("address", "not an address")), 4), "address");

            // The dashboard and every page its nav and buttons reach.
            var dashboard = Nav(await PageAsync(browser, HttpMethod.Get, HostDashboard.PagePath, HttpStatusCode.OK, HostDashboard.Policy), "Dashboard");
            TestAssert.True(Regex.IsMatch(dashboard, "<div aria-live=polite><p class=headline>") && dashboard.Contains("<th scope=col>Username</th>", StringComparison.Ordinal),
                "the status is a live region and the online table has header cells: " + dashboard);
            // The script fills the status card from /host/status.json, so its words are on the page too (server.host-pages-say-users reads them).
            var dashboardStatus = await browser.GetStringAsync(HostDashboard.PagePath + "/status.json");
            Walked.Value?.Add(("status.json", dashboardStatus));
            // Batch 748: Add a user (Invite a friend) is part of Users.
            Nav(await PageAsync(browser, HttpMethod.Get, HostInvite.AddPath, HttpStatusCode.OK, HostDashboard.Policy), "Users");
            var badName = await FormAsync(browser, HostInvite.AddPath, HttpStatusCode.BadRequest, HostDashboard.Policy, ("username", "x!"), ("mode", "maxed"));
            Errors(badName, "username");
            // Batch 748: the problem stands beside its field, above the input, where the host's sheet shows it (see StylesheetAsync).
            TestAssert.True(badName.Contains("<p class=field-error id=username-error>", StringComparison.Ordinal) &&
                badName.IndexOf("<p class=field-error id=username-error>", StringComparison.Ordinal) < badName.IndexOf("<input id=username ", StringComparison.Ordinal),
                "the username's problem stands beside its field: " + badName);
            var ready = await FormAsync(browser, HostInvite.AddPath, HttpStatusCode.OK, HostDashboard.Policy, ("username", "user1"), ("mode", "maxed"));
            TestAssert.True(ready.Contains("<button type=button data-copy=message>Copy</button> <span id=copy-status role=status></span>", StringComparison.Ordinal),
                "Copy says Copied in a live region: " + ready);
            // Batch 643: when the browser cannot copy, the script selects the text, so Ctrl+C copies it.
            var script = await browser.GetStringAsync(HostDashboard.PagePath + "/page.js");
            TestAssert.True(script.Contains("if (source.select) source.select(); else getSelection().selectAllChildren(source);", StringComparison.Ordinal),
                "Copy's fallback selects the text");
            // Batch 652: the wait feedback. The pressed button shows the sentence's first word, a live region made at load says the sentence,
            // and the buttons are disabled only after the browser has read the form (a disabled button would leave out its name and value).
            TestAssert.True(script.Contains("const sentence = event.submitter?.dataset.wait ?? form.dataset.wait;", StringComparison.Ordinal) &&
                script.Contains("control.textContent = sentence.split(' ')[0] + '…';", StringComparison.Ordinal) &&
                script.Contains("region.setAttribute('role', 'status');", StringComparison.Ordinal) &&
                script.Contains("waiting(form, event.submitter, sentence);", StringComparison.Ordinal) && script.Contains("button.disabled = true;", StringComparison.Ordinal) &&
                script.Contains("document.querySelectorAll('[data-wait]')", StringComparison.Ordinal), "page.js says what a slow action is doing: " + script);
            // Batch 646: Advanced's table, only while it is live; the count line uses the page's own words.
            TestAssert.True(script.Contains("fetch('/manage/online.json', { cache: 'no-store' })", StringComparison.Ordinal) &&
                script.Contains("if (byId('online-table')?.hasAttribute('data-live')) setInterval(online, 5000);", StringComparison.Ordinal) &&
                script.Contains("'Nobody is logged in.' : rows.length === 1 ? '1 person is logged in.' : `${rows.length} people are logged in.`", StringComparison.Ordinal),
                "page.js updates Advanced's table: " + script);
            // Batch 748: Users (Friends): New password, Disable (Remove access) and Enable (Give access again), each its own route, and Delete
            // behind its confirm page.
            const string Users = HostInvite.UsersPath;
            var friends = Nav(await PageAsync(browser, HttpMethod.Get, Users, HttpStatusCode.OK, HostDashboard.Policy), "Users");
            var friend = Regex.Match(friends, "name=accountId value=(\\d+)>").Groups[1].Value;
            TestAssert.True(friends.Contains("New password<span class=vh> for user1</span>", StringComparison.Ordinal), "each button names its user: " + friends);
            await FormAsync(browser, Users + "/password", HttpStatusCode.OK, HostDashboard.Policy, Users, ("accountId", friend));
            // Batch 646: a disabled user's row says so, and its buttons are named for the user; the short words of the status never break.
            var removed = await FormAsync(browser, Users + "/disable", HttpStatusCode.OK, HostDashboard.Policy, Users, ("accountId", friend));
            TestAssert.True(removed.Contains("<span class=mark>Disabled</span></td><td class=words>Never seen here</td>", StringComparison.Ordinal) &&
                removed.Contains("Enable<span class=vh> user1</span>", StringComparison.Ordinal), "the disabled user's row: " + removed);
            await FormAsync(browser, Users + "/enable", HttpStatusCode.OK, HostDashboard.Policy, Users, ("accountId", friend));
            await FormAsync(browser, Users + "/disable", HttpStatusCode.OK, HostDashboard.Policy, Users, ("accountId", friend));
            await FormAsync(browser, Users + "/disable", HttpStatusCode.NotFound, HostDashboard.Policy, Users, ("accountId", "0"));
            var hostLogin = Regex.Match(File.ReadAllText(home.Loop.Paths.Settings), "\"hostAccount\":\\s*(\\d+)").Groups[1].Value;
            await PageAsync(browser, HttpMethod.Get, $"{Users}/delete?id={hostLogin}", HttpStatusCode.BadRequest, HostDashboard.Policy);
            Nav(await PageAsync(browser, HttpMethod.Get, $"{Users}/delete?id={friend}", HttpStatusCode.OK, HostDashboard.Policy), "Users");
            TestAssert.True(Nav(await FormAsync(browser, Users + "/delete", HttpStatusCode.OK, HostDashboard.Policy, $"{Users}/delete?id={friend}", ("accountId", friend)), "Users")
                .Contains("<p class=empty>No users yet. Add one or turn on sign-ups.</p>", StringComparison.Ordinal), "user1 is deleted");
            await PageAsync(browser, HttpMethod.Get, "/host/play", HttpStatusCode.OK, HostDashboard.Policy);
            await PageAsync(browser, HttpMethod.Post, "/host/play", HttpStatusCode.BadRequest, HostDashboard.Policy);
            Nav(await PageAsync(browser, HttpMethod.Get, "/host/updates", HttpStatusCode.OK, HostDashboard.Policy), "Updates");
            // Batch 643: Save answers with what was saved (it gave no sign before).
            using (var save = await PostAsync(browser, "/host/updates", ("do", "save")))
                TestAssert.Equal("/host/updates?saved=1", save.Headers.Location?.OriginalString, "Save comes back to the page with its notice");
            TestAssert.True(Nav(await PageAsync(browser, HttpMethod.Get, "/host/updates?saved=1", HttpStatusCode.OK, HostDashboard.Policy), "Updates")
                .Contains("<p class=notice role=status>Saved.</p>", StringComparison.Ordinal), "the Updates notice");
            // Settings (batch 641): the first run's forms without a step number, the folders, and the uninstall (not installed, then installed).
            Nav(await PageAsync(browser, HttpMethod.Get, HostSettingsPage.PagePath, HttpStatusCode.OK, HostDashboard.Policy), "Settings");
            Nav(await PageAsync(browser, HttpMethod.Get, HostSettingsPage.NamePath, HttpStatusCode.OK, HostDashboard.Policy), "Settings");
            Errors(Nav(await FormAsync(browser, HostSettingsPage.NamePath, HttpStatusCode.BadRequest, HostDashboard.Policy, ("name", "Friday/League")), "Settings"), "name");
            Nav(await PageAsync(browser, HttpMethod.Get, HostSettingsPage.AddressPath, HttpStatusCode.OK, HostDashboard.Policy), "Settings");
            Errors(Nav(await FormAsync(browser, HostSettingsPage.AddressPath, HttpStatusCode.BadRequest, HostDashboard.Policy,
                ("choice", HostAddress.Typed), ("address", "not an address")), "Settings"), "address");
            Nav(await FolderPageAsync(browser, HostSettingsPage.FoldersPath, HostDashboard.Policy), "Settings");
            Nav(await PageAsync(browser, HttpMethod.Get, HostSettingsPage.UninstallPath, HttpStatusCode.OK, HostDashboard.Policy), "Settings");
            File.WriteAllText(Path.Combine(home.Root, "amberline-install.json"), "{}");
            Nav(await PageAsync(browser, HttpMethod.Get, HostSettingsPage.UninstallPath, HttpStatusCode.OK, HostDashboard.Policy), "Settings");
            Errors(Nav(await FormAsync(browser, HostSettingsPage.UninstallPath, HttpStatusCode.BadRequest, HostDashboard.Policy, ("confirm", "")), "Settings"), "confirm");
            await PageAsync(browser, HttpMethod.Get, HostSupport.PagePath, HttpStatusCode.OK, HostDashboard.Policy);
            await PageAsync(browser, HttpMethod.Get, "/host/stop", HttpStatusCode.OK, HostDashboard.Policy);
            await PageAsync(browser, HttpMethod.Get, "/host/restart", HttpStatusCode.OK, HostDashboard.Policy);
            await PageAsync(browser, HttpMethod.Get, HostAttention.CertificatePath, HttpStatusCode.OK, HostDashboard.Policy);
            await PageAsync(browser, HttpMethod.Post, HostDashboard.PagePath + "/network", HttpStatusCode.BadRequest, HostDashboard.Policy);
            // Batch 646: Advanced never reloads itself; page.js updates its Online now table from its JSON, under the host pages' policy.
            var advanced = Nav(await PageAsync(browser, HttpMethod.Get, "/manage", HttpStatusCode.OK, HostDashboard.Policy), "Advanced");
            TestAssert.True(!advanced.Contains("http-equiv=refresh", StringComparison.Ordinal) && advanced.Contains("<table id=online-table data-live", StringComparison.Ordinal) &&
                advanced.Contains("<p id=online-count aria-live=polite>", StringComparison.Ordinal) && advanced.Contains("<script src=/host/page.js defer></script>", StringComparison.Ordinal),
                "Advanced updates its table in place: " + advanced);
            Nav(await PageAsync(browser, HttpMethod.Get, "/manage?refresh=off", HttpStatusCode.OK, HostDashboard.Policy), "Advanced");
            using (var online = await browser.GetAsync("/manage/online.json"))
                TestAssert.True(online.StatusCode == HttpStatusCode.OK && online.Content.Headers.ContentType?.MediaType == "application/json" &&
                    (await online.Content.ReadAsStringAsync()).StartsWith('['), "Advanced's table as JSON for the signed-in host");
            await AdvancedAsync(browser, advanced);
            // Last, because it stops Amberline: the page after an uninstall (this loop's places are nowhere, so nothing outside is touched).
            await FormAsync(browser, HostSettingsPage.UninstallPath, HttpStatusCode.OK, HostDashboard.Policy, ("confirm", HostUninstall.ConfirmWord));
            TestAssert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)) == 0, "the uninstall stops Amberline");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
        await CopyPagesAsync();
        await SettingsCopyPagesAsync();
    }

    /// <summary>
    /// Batch 641: the same copy pages from Settings' folder page (the offer and the done page), with the nav and no step number, on a
    /// page-only Settings (<see cref="HostSettingsChecks.RunSettingsAsync"/>) in the check's own install.
    /// </summary>
    private static async Task SettingsCopyPagesAsync()
    {
        const string Folders = HostSettingsPage.FoldersPath, Copy = HostSettingsPage.FoldersPath + "/copy";
        var folder = HostFolderCopyChecks.OneFolder.Create("league-host-pages-settings-copy", downloads =>
            HostFolderCopyChecks.Download(Path.Combine(downloads, "League 2011"), Path.Combine(downloads, "League 2011", "lol_air_client")));
        var run = Task.Run(() => HostSettingsChecks.RunSettingsAsync(folder.Home, () => 0));
        try
        {
            using var browser = folder.Home.Client();
            async Task<int> Watched() => await run ? 1 : 0;
            await WaitForHealthAsync(browser, "ready", Watched());
            await folder.Home.SignInAsync(browser);
            (string, string)[] rows = [("game", folder.Game), ("air", folder.Air)];
            Nav(await FormAsync(browser, Folders, HttpStatusCode.OK, HostDashboard.Policy, rows), "Settings");
            using (var started = await PostAsync(browser, Copy, Folders, [.. rows, ("delete", "no")]))
                TestAssert.Equal(HttpStatusCode.Redirect, started.StatusCode, "the copy starts");
            await HostSettingsChecks.CopiedAsync(browser, Copy);
            Nav(await PageAsync(browser, HttpMethod.Get, Copy, HttpStatusCode.OK, HostDashboard.Policy), "Settings");
        }
        finally
        {
            folder.Home.Loop.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            Delete(folder.Home.Root);
            Delete(folder.Downloads);
        }
    }

    /// <summary>
    /// Batch 642: the folder step's copy into a one-folder install (the offer, a refused yes, an expired form, a failed copy, the copy
    /// running and done), in the check's own install with synthetic clients (<see cref="HostFolderCopyChecks.OneFolder"/>).
    /// </summary>
    private static async Task CopyPagesAsync()
    {
        var folder = HostFolderCopyChecks.OneFolder.Create("league-host-pages-copy", downloads =>
        {
            // Below a folder with platform-data (an Amberline server installation), so a yes is refused beside its question.
            Directory.CreateDirectory(Path.Combine(downloads, "Server", "platform-data"));
            return HostFolderCopyChecks.Download(Path.Combine(downloads, "Server"), Path.Combine(downloads, "Server", "air"));
        });
        await folder.RunAsync(async signedIn =>
        {
            var browser = signedIn.Client;
            (string, string)[] rows = [("game", folder.Game), ("air", folder.Air)];
            Step(await FormAsync(browser, "/setup", HttpStatusCode.OK, HostPages.Policy, rows), 2);
            Errors(Step(await FormAsync(browser, CopyPath, HttpStatusCode.BadRequest, HostDashboard.Policy, "/setup", [.. rows, ("delete", "yes")]), 2), "delete");
            await PageAsync(browser, HttpMethod.Post, CopyPath, HttpStatusCode.BadRequest, HostDashboard.Policy);

            folder.AfterCopy = _ => throw new IOException("synthetic: the disk is full");
            using (var failing = await PostAsync(browser, CopyPath, "/setup", [.. rows, ("delete", "no")]))
                TestAssert.Equal(HttpStatusCode.Redirect, failing.StatusCode, "the copy starts");
            await signedIn.EndAsync();
            Step(await PageAsync(browser, HttpMethod.Get, CopyPath, HttpStatusCode.OK, HostDashboard.Policy), 2);

            using var hold = new ManualResetEventSlim();
            var copied = 0;
            folder.AfterCopy = _ => { if (Interlocked.Increment(ref copied) == 2) hold.Wait(TimeSpan.FromSeconds(60)); };
            using (var copying = await PostAsync(browser, CopyPath, "/setup", [.. rows, ("delete", "no")]))
                TestAssert.Equal(HttpStatusCode.Redirect, copying.StatusCode, "the copy starts");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Volatile.Read(ref copied) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            var running = await PageAsync(browser, HttpMethod.Get, CopyPath, HttpStatusCode.OK, HostDashboard.Policy);
            TestAssert.True(running.Contains("<p class=step>Step 2 of 4: Your game folders</p>", StringComparison.Ordinal) &&
                running.Contains("<div aria-live=polite><p id=copy-phase", StringComparison.Ordinal), "the running copy: its step and its live phase: " + running);
            hold.Set();
            await signedIn.EndAsync();
            Step(await PageAsync(browser, HttpMethod.Get, CopyPath, HttpStatusCode.OK, HostDashboard.Policy), 2);
        });
    }

    /// <summary>
    /// Batch 648: Advanced in the host pages' words, and each refused form answered with Advanced itself (the error summary, the message
    /// beside its field, what was typed but never a password), where the operator's page answers with JSON.
    /// </summary>
    private static async Task AdvancedAsync(HttpClient browser, string advanced)
    {
        const string Manage = "/manage", Policy = HostDashboard.Policy;
        var words = Regex.Replace(advanced, "<[^>]+>", " ");
        foreach (var word in new[] { "Account", "account", "Game testing", "testing RP", "Issue", "issuance", "Revoke", "token", "companion", "ticker",
                     "Redeemer", "Severity", "platform", "Back to the dashboard" })
            TestAssert.False(words.Contains(word, StringComparison.Ordinal), $"Advanced says \"{word}\": {words}");
        var login = Regex.Match(advanced, "<option value=\"(\\d+)\"").Groups[1].Value;
        string Advanced(string page) => Nav(page, "Advanced");
        Errors(Advanced(await FormAsync(browser, "/manage/grant", HttpStatusCode.BadRequest, Policy, Manage,
            ("accountId", login), ("rp", "0"), ("requestId", Guid.NewGuid().ToString()))), "grant-rp");
        var mismatch = Advanced(await FormAsync(browser, "/manage/password", HttpStatusCode.BadRequest, Policy, Manage,
            ("accountId", login), ("password", "Synthetic-one-Pw-12"), ("confirm", "Synthetic-two-Pw-12")));
        Errors(mismatch, "password-confirm");
        TestAssert.False(mismatch.Contains("Synthetic-one", StringComparison.Ordinal) || mismatch.Contains("Synthetic-two", StringComparison.Ordinal),
            "a refused password is never shown back");
        Errors(Advanced(await FormAsync(browser, "/manage/password", HttpStatusCode.BadRequest, Policy, Manage,
            ("accountId", login), ("password", "short"), ("confirm", "short"))), "password-new");
        Errors(Advanced(await FormAsync(browser, "/manage/reward-code/create", HttpStatusCode.BadRequest, Policy, Manage,
            ("ip", "0"), ("rp", "0"), ("requestId", Guid.NewGuid().ToString()))), "code-ip");
        var typed = new string('x', 201);
        var notice = Advanced(await FormAsync(browser, "/manage/broadcast", HttpStatusCode.BadRequest, Policy, Manage, ("message", typed), ("severity", "INFO")));
        Errors(notice, "notice-message");
        TestAssert.True(notice.Contains($"value=\"{typed}\"", StringComparison.Ordinal) && notice.Contains("<option value=INFO selected>Information</option>", StringComparison.Ordinal),
            "what was typed comes back: " + notice);
        TestAssert.True(Advanced(await PageAsync(browser, HttpMethod.Post, "/manage/grant", HttpStatusCode.BadRequest, Policy))
            .Contains("<li>The page expired. Please try again.</li>", StringComparison.Ordinal), "an expired form says so in the summary");
        using (var given = await PostAsync(browser, "/manage/grant", Manage, ("accountId", login), ("rp", "100"), ("requestId", Guid.NewGuid().ToString())))
            TestAssert.Equal("/manage?done=grant", given.Headers.Location?.OriginalString, "Give RP comes back with its notice");
        TestAssert.True(Advanced(await PageAsync(browser, HttpMethod.Get, "/manage?done=grant", HttpStatusCode.OK, Policy))
            .Contains("<p class=notice role=status>The RP is given.</p>", StringComparison.Ordinal), "the done notice");
        var code = await FormAsync(browser, "/manage/reward-code/create", HttpStatusCode.OK, Policy, Manage, ("ip", "0"), ("rp", "100"), ("requestId", Guid.NewGuid().ToString()));
        TestAssert.True(code.Contains("<button type=button data-copy=reward-code>Copy</button> <span id=copy-status role=status></span>", StringComparison.Ordinal) &&
            code.Contains("<script src=/host/page.js defer></script>", StringComparison.Ordinal), "the code has its Copy button: " + code);
        TestAssert.True(Advanced(await PageAsync(browser, HttpMethod.Get, Manage, HttpStatusCode.OK, Policy))
            .Contains("<div class=wide role=region tabindex=0 aria-labelledby=codes-heading><table><thead><tr><th scope=col>Status</th>", StringComparison.Ordinal) &&
            advanced.Contains("<div class=wide role=region tabindex=0 aria-labelledby=leavers-heading><table>", StringComparison.Ordinal),
            "the reward codes' and leavers' header cells, each table in its own scrolling region");
    }

    private const string CopyPath = "/setup/copy";

    /// <summary>The stylesheet: its own type, a host route's policy, dark only and a visible focus.</summary>
    private static async Task StylesheetAsync(HttpClient browser, string policy)
    {
        using var sheet = await browser.GetAsync(HostPages.StyleHref);
        TestAssert.Equal(HttpStatusCode.OK, sheet.StatusCode, "the stylesheet");
        // Batch 643: the browser keeps it, so the page shown as Amberline restarts is styled although the listener has just closed.
        TestAssert.True(sheet.Headers.CacheControl is { Private: true, MaxAge.TotalDays: >= 1 } && Regex.IsMatch(HostPages.StyleHref, @"\?v=[0-9a-f]{12}\z"),
            $"the stylesheet is cached under a versioned address: {sheet.Headers.CacheControl} {HostPages.StyleHref}");
        TestAssert.Equal("text/css", sheet.Content.Headers.ContentType?.MediaType, "the stylesheet's type");
        TestAssert.Equal(policy, string.Join(";", sheet.Headers.GetValues("Content-Security-Policy")), "the stylesheet's policy");
        TestAssert.Equal("nosniff", string.Join(";", sheet.Headers.GetValues("X-Content-Type-Options")), "nosniff");
        var css = await sheet.Content.ReadAsStringAsync();
        TestAssert.True(css.Contains("color-scheme:dark", StringComparison.Ordinal) && !css.Contains("prefers-color-scheme", StringComparison.Ordinal) &&
            css.Contains(":focus-visible{outline:3px", StringComparison.Ordinal) && !css.Contains("@import", StringComparison.Ordinal) &&
            !css.Contains("url(", StringComparison.Ordinal), "dark only, a visible focus, nothing fetched: " + css);
        // Batch 748: a field's problem is visible beside its field (the sign-up sheet's clipped rule is not in this sheet), the sheen stops for
        // reduced motion, and forced colours keep the borders and the sidebar's current page as an outline (a colour alone would vanish).
        TestAssert.True(css.Contains(".field-error{margin:0 0 6px;color:var(--bad);font-weight:600}", StringComparison.Ordinal) &&
            !css.Contains(".field-error{position:absolute", StringComparison.Ordinal), "the host's field problem is visible: " + css);
        TestAssert.True(css.Contains("@media (prefers-reduced-motion:reduce){.wordmark{animation:none}}", StringComparison.Ordinal), "the sheen stops for reduced motion");
        TestAssert.True(css.Contains("@media (forced-colors:active){.frame,.side,.tile,.game-card,.folder,.status,.mark,.badge,.warn,.actions a:not(.button){border-color:CanvasText}",
            StringComparison.Ordinal) && css.Contains(".side a[aria-current=page]{outline:2px solid;outline-offset:-2px}", StringComparison.Ordinal) &&
            css.Contains(".side a[aria-current=page]:focus-visible{outline:3px solid;outline-offset:2px}", StringComparison.Ordinal),
            "forced colours keep the borders, the current page's outline and the focus ring over it");
        // Batch 748: a focused field or select keeps the host's 3 px ring: the sign-up's shared field rule hides the outline (outline:none, 0,3,1).
        TestAssert.True(css.Contains("input:not([type=radio]):not([type=checkbox]):focus-visible,select:focus-visible{outline:3px solid var(--focus);outline-offset:2px}",
            StringComparison.Ordinal), "a focused field keeps the 3 px focus ring");
        // Batch 748: Back and Cancel in an action row keep the 44 px secondary button, and the host's borders are the 3:1 tokens (see HostRules).
        TestAssert.True(css.Contains(".actions a:not(.button){display:inline-flex;align-items:center;justify-content:center;min-height:44px;", StringComparison.Ordinal) &&
            css.Contains(":root{--line:#8593a8;--gold-dim:#806a3a;", StringComparison.Ordinal), "the action links are 44 px buttons; the host's borders reach 3:1");
        // Batch 643, seen in a browser: a field with a problem gets the red border (its rule outranks the fields' own), the nav is not
        // held to the text's 65ch, and a table's headers and buttons never break inside a word on a narrow screen.
        // Batch 646: Friends' Last seen words never break (td.words), and notices and cards fill the column like the other boxes.
        // Batch 648: a long folder path (the copy offer, the uninstall page) wraps instead of scrolling the page sideways at 400 px, and
        // Advanced's wide tables scroll in their own region (hidden with an empty Online now table).
        foreach (var rule in new[] { "input[aria-invalid=true]:not([type=radio]):not([type=checkbox]){border-color:var(--bad)", ".side ul{display:flex;flex-wrap:wrap;gap:4px;max-width:none;",
                     "th{font-weight:600;overflow-wrap:normal}", "td button{min-height:36px;padding:4px 12px;overflow-wrap:normal}", "td.words{overflow-wrap:normal}",
                     ".notice,.problem,.error-summary{position:relative;max-width:none;", ".warn{position:relative;max-width:none;", ".cards{max-width:none}",
                     ".page{min-width:0;padding:20px 16px 24px;overflow-wrap:anywhere}",
                     ".wide{overflow-x:auto;margin:0 0 16px}", ".wide:has(>table[hidden]){display:none}",
                     "@media (max-width:23rem){.wide th,.wide td{padding-right:8px}.wide td button{padding-left:8px;padding-right:8px}}" })
            TestAssert.True(css.Contains(rule, StringComparison.Ordinal), "the stylesheet has " + rule);
    }

    /// <summary>The folder page once the first look is done (a slow disk shows a page that comes back by itself first).</summary>
    private static async Task<string> FolderPageAsync(HttpClient browser, string path = "/setup", string policy = HostPages.Policy)
    {
        for (var tries = 0; tries < 40; tries++)
        {
            var page = await PageAsync(browser, HttpMethod.Get, path, HttpStatusCode.OK, policy);
            if (page.Contains("<label for=paste>", StringComparison.Ordinal)) return page;
            await Task.Delay(250);
        }
        throw new TimeoutException("the folder step never showed its form");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, params (string Name, string Value)[] fields) =>
        await PostAsync(client, path, path, fields);

    /// <summary>Posts a form with a fresh antiforgery token from <paramref name="tokenPage"/>, as the browser would.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string tokenPage, params (string Name, string Value)[] fields)
    {
        var token = Token(await client.GetStringAsync(tokenPage));
        var form = new Dictionary<string, string> { [token.Key] = token.Value };
        foreach (var (name, value) in fields) form[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static Task<string> FormAsync(HttpClient client, string path, HttpStatusCode status, string policy, params (string Name, string Value)[] fields) =>
        FormAsync(client, path, status, policy, path, fields);

    private static async Task<string> FormAsync(HttpClient client, string path, HttpStatusCode status, string policy, string tokenPage,
        params (string Name, string Value)[] fields)
    {
        using var answer = await PostAsync(client, path, tokenPage, fields);
        return await CheckedAsync(answer, $"POST {path}", status, policy);
    }

    private static async Task<string> PageAsync(HttpClient client, HttpMethod method, string path, HttpStatusCode status, string policy)
    {
        using var request = new HttpRequestMessage(method, path) { Content = method == HttpMethod.Get ? null : new FormUrlEncodedContent([]) };
        using var answer = await client.SendAsync(request);
        return await CheckedAsync(answer, $"{method} {path}", status, policy);
    }

    /// <summary>One page against the batch's bar (WCAG 2.2 AA where markup can show it).</summary>
    private static async Task<string> CheckedAsync(HttpResponseMessage answer, string what, HttpStatusCode status, string policy)
    {
        var page = await answer.Content.ReadAsStringAsync();
        Walked.Value?.Add((what, page));
        TestAssert.Equal(status, answer.StatusCode, $"{what}: {page}");
        TestAssert.Equal("text/html", answer.Content.Headers.ContentType?.MediaType, what);
        var csp = string.Join(";", answer.Headers.GetValues("Content-Security-Policy"));
        TestAssert.Equal(policy, csp, what + "'s policy");
        Accessible(page, what, csp);
        return page;
    }

    /// <param name="style">The stylesheet's address when it is not the host pages' own (batch 656: the friends' port's sign-up page).</param>
    internal static void Accessible(string page, string what, string policy, string? style = null)
    {
        void Is(bool condition, string rule) => TestAssert.True(condition, $"{what}: {rule}\n{page}");
        var tags = Regex.Matches(page, "<([a-zA-Z][a-zA-Z0-9]*)(\\s[^>]*)?>").Select(tag => (Name: tag.Groups[1].Value.ToLowerInvariant(), Attributes: tag.Groups[2].Value, tag.Index)).ToArray();
        string? Attribute(string attributes, string name) =>
            Regex.Match(attributes, $"\\s{name}=(?:\"([^\"]*)\"|([^\\s>\"]+))") is { Success: true } found ? found.Groups[1].Success ? found.Groups[1].Value : found.Groups[2].Value
            : Regex.IsMatch(attributes, $"\\s{name}(\\s|$)") ? "" : null;
        int Count(string name) => tags.Count(tag => tag.Name == name);

        // The document: its language, a title per page, one h1 and the headings in order.
        Is(page.StartsWith("<!doctype html><html lang=en>", StringComparison.Ordinal), "lang=en");
        var title = Regex.Match(page, "<title>([^<]+)</title>");
        var heading = Regex.Match(page, "<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline);
        Is(Count("title") == 1 && Count("h1") == 1, "one title and one h1");
        Is(title.Success && title.Groups[1].Value.Contains(Regex.Replace(heading.Groups[1].Value, "<[^>]+>", ""), StringComparison.Ordinal) &&
            title.Groups[1].Value.EndsWith(" · Amberline", StringComparison.Ordinal), "the title names the page");
        var levels = tags.Where(tag => tag.Name is ['h', >= '1' and <= '6']).Select(tag => tag.Name[1] - '0').ToArray();
        Is(levels[0] == 1 && levels.Zip(levels.Skip(1)).All(pair => pair.Second <= pair.First + 1), "headings in order: " + string.Join(",", levels));

        // Landmarks and the skip link, first on the page.
        // Owner, 7 November 2026: the sign-up pages (their own stylesheet) have the Amberline wordmark above the panel and no footer.
        Is(style is null
            ? Count("header") == 1 && Count("main") == 1 && Count("footer") == 1 && Count("nav") <= 1
            : Count("header") == 1 && Count("main") == 1 && Count("footer") == 0 && Count("nav") == 0, "header, main, footer once each (sign-up: no footer)");
        Is(tags.First(tag => tag.Name == "a").Attributes == " class=skip href=#main" && page.Contains("<main id=main tabindex=-1", StringComparison.Ordinal),
            "a skip link to the main content comes first");
        Is(page.Contains($"<link rel=stylesheet href=\"{style ?? HostPages.StyleHref}\">", StringComparison.Ordinal), "the stylesheet file, at its versioned address");

        // Batch 643: a form the server checks lets the server's error summary speak, not the browser's bubble in its own language.
        // Batch 648: Advanced's forms too (their answers to a bad field are now a page; the operator's page is not a host page).
        foreach (Match form in Regex.Matches(page, "<form([^>]*)>(.*?)</form>", RegexOptions.Singleline))
            if (Regex.IsMatch(form.Groups[2].Value, "<(input|select)[^>]*\\s(required|minlength=)") && Attribute(form.Groups[1].Value, "action") is { } action)
                Is(Attribute(form.Groups[1].Value, "novalidate") is not null, $"the form to {action} has novalidate");

        // Nothing inline: no style element or attribute, no script body, no handler; a script only where the policy runs one.
        Is(Count("style") == 0 && tags.All(tag => Attribute(tag.Attributes, "style") is null), "no inline style");
        Is(tags.All(tag => !Regex.IsMatch(tag.Attributes, "\\son[a-z]+\\s*=")), "no inline event handler");
        Is(Regex.Matches(page, "<script([^>]*)>(.*?)</script>", RegexOptions.Singleline).All(script =>
            Attribute(script.Groups[1].Value, "src") is { Length: > 0 } && script.Groups[2].Length == 0), "scripts are files");
        Is(Count("script") == 0 || policy.Contains("script-src 'self'", StringComparison.Ordinal), "a script only under a policy that runs it");
        Is(!policy.Contains("unsafe-inline", StringComparison.Ordinal) && policy.Contains("style-src 'self'", StringComparison.Ordinal), "style-src 'self'");

        // Every id once; every reference names one of them.
        var ids = tags.Select(tag => Attribute(tag.Attributes, "id")).OfType<string>().ToArray();
        Is(ids.Length == ids.Distinct(StringComparer.Ordinal).Count(), "ids are unique: " + string.Join(",", ids));
        foreach (var tag in tags)
        {
            foreach (var reference in new[] { "aria-describedby", "aria-labelledby", "for" })
                foreach (var id in (Attribute(tag.Attributes, reference) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    Is(ids.Contains(id), $"{reference}={id} names an element");
            if (tag.Name == "a" && Attribute(tag.Attributes, "href") is ['#', .. var target]) Is(ids.Contains(target), $"#{target} is on the page");
        }

        // Every field labelled (by its label's for, by a wrapping label, or by name), every button and link named.
        foreach (var field in tags.Where(tag => tag.Name is "input" or "select" or "textarea" && Attribute(tag.Attributes, "type") != "hidden"))
        {
            var id = Attribute(field.Attributes, "id");
            var wrapped = page.LastIndexOf("<label", field.Index, StringComparison.Ordinal) > page.LastIndexOf("</label>", field.Index, StringComparison.Ordinal);
            var forId = id is not null && tags.Any(tag => tag.Name == "label" && Attribute(tag.Attributes, "for") == id);
            Is(wrapped || forId || Attribute(field.Attributes, "aria-label") is { Length: > 0 }, $"the field{field.Attributes} has a label");
        }
        foreach (Match element in Regex.Matches(page, "<(button|a)(\\s[^>]*)?>(.*?)</\\1>", RegexOptions.Singleline))
        {
            Is(Regex.Replace(element.Groups[3].Value, "<[^>]+>", "").Trim().Length > 0 || Attribute(element.Groups[2].Value, "aria-label") is { Length: > 0 },
                $"<{element.Groups[1].Value}{element.Groups[2].Value}> has a name");
            if (element.Groups[1].Value == "a") Is(Attribute(element.Groups[2].Value, "href") is { Length: > 0 }, "a link has an address");
        }
        Is(Regex.Matches(page, "<table.*?</table>", RegexOptions.Singleline).All(table => table.Value.Contains("<th", StringComparison.Ordinal)), "tables have header cells");

        // Batch 652, the owner's standard: every button, link-button and action-row link says a word of the list (or a named exception),
        // and every slow action says at once what it is doing (data-wait), which only works where page.js runs.
        foreach (Match element in Regex.Matches(page, "<(button)(\\s[^>]*)?>(.*?)</button>|<(a)(\\s[^>]*class=(?:\"[^\"]*button[^\"]*\"|button)[^>]*)>(.*?)</a>", RegexOptions.Singleline))
        {
            var label = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(element.Groups[3].Value + element.Groups[6].Value, "<span class=vh>.*?</span>", ""), "<[^>]+>", "")).Trim();
            Is(Words.Contains(label) || Exceptions.ContainsKey(label), $"the button \"{label}\" uses the word list (or is a named exception)");
        }
        foreach (Match row in Regex.Matches(page, "<div class=actions>(.*?)</div>", RegexOptions.Singleline))
            foreach (Match link in Regex.Matches(row.Groups[1].Value, "<a (?![^>]*class=)[^>]*>(.*?)</a>", RegexOptions.Singleline))
                Is(link.Groups[1].Value is "Back" or "Cancel", $"the action row's link \"{link.Groups[1].Value}\" is Back or Cancel");
        foreach (Match form in Regex.Matches(page, "<form([^>]*)>(.*?)</form>", RegexOptions.Singleline))
            if (Attribute(form.Groups[1].Value, "method") == "post" && Attribute(form.Groups[1].Value, "action") is { } action && Slow.Any(slow => Regex.IsMatch(action, slow)))
                Is(Attribute(form.Groups[1].Value, "data-wait") is { Length: > 0 } || Regex.IsMatch(form.Groups[2].Value, "<button[^>]*\\sdata-wait=\"[^\"]+\""),
                    $"the slow form to {action} says what it is doing (data-wait)");
        Is(!page.Contains("data-wait=", StringComparison.Ordinal) || page.Contains($"<script src={HostPages.ScriptPath} defer></script>", StringComparison.Ordinal),
            "a page with data-wait loads page.js");
        // Batch 648: Advanced's Leavers and reward-code tables had a header cell without a name.
        Is(!Regex.IsMatch(page, "<th(\\s[^>]*)?>\\s*</th>"), "every header cell names its column");
    }

    /// <summary>
    /// Batch 652, the owner's word list (shared with batch 653's windows): what a button on a host page may say. Continue goes on in the
    /// first-run steps, Back goes back a step, Save saves and stays, Cancel leaves without saving, Copy copies text, Play starts the
    /// League client, and an action that cannot be undone names its verb and object.
    /// </summary>
    private static readonly HashSet<string> Words = ["Continue", "Back", "Save", "Cancel", "Copy", "Play", "Uninstall Amberline", "Remove access"];

    /// <summary>The named exceptions: buttons that do one thing of their own, each with where it is and why its words fit.</summary>
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        ["Choose folder…"] = "folder step: opens the Windows folder window (the ellipsis says a window follows)",
        ["Check"] = "folder step: looks inside a pasted folder; nothing is saved",
        ["Copy the game files"] = "folder step's offer: copies the game and the AIR client into the install folder (not text: verb and object)",
        ["Use them where they are"] = "folder step's offer: the other answer to the offer's question",
        ["Try again"] = "a copy that failed: back to the offer",
        ["Create their login"] = "Add a user: makes the user's login (verb and object)",
        ["Create your login"] = "the friends' port's sign-up page (batch 656): makes the person's own login (verb and object)",
        ["Add another user"] = "a user's ready page: Add a user again (batch 748; it was Invite another friend)",
        ["Give your address"] = "Add a user before the address step: goes to it",
        ["New password"] = "Users: a new password for that user",
        ["Disable"] = "Users (batch 748; it was Remove access): a password nobody knows, the user logged out; Enable undoes it",
        ["Enable"] = "Users (batch 748; it was Give access again): a new password for a disabled user, shown once",
        ["Delete"] = "Users (batch 748): opens the confirm page that deletes the user; nothing is deleted yet",
        ["Delete user"] = "the Delete confirm page (batch 748): deletes the login for good (verb and object)",
        ["Get the Play file"] = "Users (batch 748): downloads the server's Play file to send (verb and object)",
        ["Download the Play file"] = "the address-change card: H8's download; the sign-up page's done page (batch 656)",
        ["Hide"] = "a card's own button: hides that card",
        ["Show them"] = "the dashboard (batch 751): shows every hidden attention card again",
        ["Make new Play files"] = "the certificate card and its page: cannot be undone (verb and object)",
        ["Open the support log"] = "the stopped-game card: opens the support log page",
        ["Download"] = "the support log: saves it as a file",
        ["Allow through Windows Firewall"] = "the firewall card: Windows' own words for it",
        ["Test again"] = "the network cards: runs the network test again",
        ["Stop Amberline"] = "the dashboard and the Stop page: ends games (verb and object)",
        ["Restart Amberline"] = "the dashboard and the Restart page: ends games (verb and object)",
        ["Copy sign-up link"] = "the dashboard: copies the sign-up link while sign-ups are on (verb and object)",
        ["Copy the My account link"] = "Users (batch 751): copies the address of My account, where users change their password or delete their account",
        ["Change password"] = "My account (batch 751): sets the user's own new password (verb and object)",
        ["Delete my account"] = "My account (batch 751): deletes the user's own login for good (verb and object)",
        ["Copy address"] = "the dashboard: copies the address friends type (verb and object)",
        ["Add a user"] = "the dashboard: opens the form that makes a user's login (verb and object)",
        ["End game"] = "the dashboard's game cards and their confirm page: ends a running game (verb and object)",
        ["Check now"] = "Updates: asks GitHub once",
        ["Update now"] = "Updates and its confirm (batch 666): downloads the newer release, checks it and starts its installer",
        ["Send to players"] = "Advanced: sends the notice",
        ["Give RP"] = "Advanced: gives RP to a login",
        ["Make them log in again"] = "Advanced: ends a friend's sign-in token",
        ["Make a reward code"] = "Advanced: makes a code shown once",
        ["Cancel code"] = "Advanced: cancels a reward code (verb and object; not the Cancel of a form)",
        ["Clear"] = "Advanced's leavers: forgives a player's leaves",
        ["Reviewed"] = "Advanced's reports: closes a report as reviewed",
        ["Dismiss"] = "Advanced's reports: closes a report as dismissed",
    };

    /// <summary>The forms whose answer can take more than about 2 seconds (batch 652): each says at once what it is doing.</summary>
    private static readonly string[] Slow =
    [
        "^/host/play$", "^/host/firewall$", "^/host/network$", "^/host/stop$", "^/host/restart$", "^/host/certificate", "^/host/updates$", "^/host/updates/install$",
        "^/host/settings/uninstall$", "^/host/users/password$", "^/host/users/enable$", "^/manage/reward-code/create$", "^/host/setup/address$", "^/host/settings/address$",
        "^/setup$", "^/setup/copy$", "^/host/settings/folders$", "^/host/settings/folders/copy$", "^/host/games/end$",
    ];

    /// <summary>A first-run step: its indicator, Back and Continue at its end, and no nav.</summary>
    private static string Step(string page, int step)
    {
        TestAssert.True(page.Contains($"<p class=step>Step {step} of {HostPages.Steps.Length}: {HostPages.Steps[step - 1]}</p>", StringComparison.Ordinal) &&
            !page.Contains("<nav", StringComparison.Ordinal), $"step {step}'s indicator and no nav: {page}");
        // Batch 642: the copy pages of the folder step go Back to the folder step itself (/setup).
        TestAssert.True(page.Contains("http-equiv=refresh", StringComparison.Ordinal) || Regex.IsMatch(page, "<div class=actions>(<a href=/(host/)?setup[^>]*>Back</a>)?<(button|a class=button)"),
            $"step {step}'s Back and Continue: {page}");
        return page;
    }

    /// <summary>A refused form: the summary first, focused, linking the field, whose own message is tied to it.</summary>
    private static void Errors(string page, string field)
    {
        TestAssert.True(page.Contains("<div class=error-summary role=alert tabindex=-1 autofocus aria-labelledby=error-title>", StringComparison.Ordinal) &&
            page.Contains($"<li><a href=#{field}>", StringComparison.Ordinal) && page.Contains($"<title>Error: ", StringComparison.Ordinal), "the error summary: " + page);
        TestAssert.True(page.Contains($"id={field}-error>", StringComparison.Ordinal) && Regex.IsMatch(page, $"aria-describedby=\"?[^\">]*{field}-error"),
            $"the message beside {field}: {page}");
        TestAssert.True(page.IndexOf("class=error-summary", StringComparison.Ordinal) < page.IndexOf("<form", StringComparison.Ordinal), "the summary comes before the form");
    }

    /// <summary>
    /// A page's visible text: its text outside the tags (comments, scripts and styles left out), then the values a person reads, which are the
    /// values of alt, title, aria-label, placeholder and data-wait, and each button's value.
    /// </summary>
    private static string VisibleText(string page)
    {
        var markup = Regex.Replace(page, "<!--.*?-->|<(script|style)\\b.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var values = Regex.Matches(markup, "\\s(?:alt|title|aria-label|placeholder|data-wait)=(?:\"([^\"]*)\"|([^\\s>\"]+))", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        var buttons = Regex.Matches(markup, "<(button|input)(\\s[^>]*)?>", RegexOptions.IgnoreCase)
            .Where(tag => tag.Groups[1].Value.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(tag.Groups[2].Value, "\\stype=(?:submit|button|reset)\\b", RegexOptions.IgnoreCase))
            .Select(tag => Regex.Match(tag.Groups[2].Value, "\\svalue=(?:\"([^\"]*)\"|([^\\s>\"]+))") is { Success: true } value
                ? (value.Groups[1].Success ? value.Groups[1].Value : value.Groups[2].Value) : "");
        var text = Regex.Replace(markup, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(string.Join(" ", text, string.Join(" ", values), string.Join(" ", buttons)));
    }

    /// <summary>The status JSON's words: every string value except a check's id (an identifier); the property names are not values.</summary>
    private static string JsonWords(string json)
    {
        var words = new List<string>();
        void Walk(JsonElement element, string name)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String when name != "id": words.Add(element.GetString()!); break;
                case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) Walk(item, ""); break;
                case JsonValueKind.Object: foreach (var property in element.EnumerateObject()) Walk(property.Value, property.Name); break;
            }
        }
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, "");
        return string.Join(" ", words);
    }

    /// <summary>A full-Platform page: the nav names it as the current page, once.</summary>
    private static string Nav(string page, string current)
    {
        TestAssert.True(page.Contains("<nav class=side aria-label=\"Amberline\">", StringComparison.Ordinal) && Regex.Count(page, "aria-current=page") == 1 &&
            Regex.IsMatch(page, $"aria-current=page>{current}</a>"), $"the nav marks {current}: {page}");
        return page;
    }
}
