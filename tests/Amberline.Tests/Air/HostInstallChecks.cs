using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's self-install and update (batch 627, H11). Releases are stand-in folders with a BUILD.json; the profile, the
/// desktop and the data folder are temporary, and every data folder names a free loopback port, so no check asks the operator's
/// port. The installed Platform is never started (a list stands in) and no browser opens. The update check talks to a fake GitHub.
/// </summary>
public static class HostInstallChecks
{
    [Check("server.host-self-install-copy", TimeoutSeconds = 120)]
    public static void AStartFromTheZipInstallsWithoutTheMarkOfTheWeb() => SelfInstallAsync().GetAwaiter().GetResult();

    private static async Task SelfInstallAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-install");
        var realShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), HostInstall.ShortcutName);
        var realShortcutBefore = File.Exists(realShortcut);
        try
        {
            var release = Release(Path.Combine(root, "Downloads", "Amberline-1.4.0-win-x64"), "1.4.0");
            // Explorer's Extract All marks every file it unpacks from a downloaded zip as coming from the internet.
            foreach (var file in Directory.EnumerateFiles(release, "*", SearchOption.AllDirectories)) MarkFromInternet(file);
            var control = Path.Combine(root, "control.exe");
            File.Copy(Path.Combine(release, "platform", HostInstall.PlatformFile), control);
            TestAssert.True(HasMark(control), "File.Copy carries the Mark of the Web along (so the check can see it)");

            var data = Data(root, "data");
            var before = Snapshot(data);
            var screen = new StringWriter();
            var started = new List<string>();
            var install = Installer(root, release, data, screen, started);
            TestAssert.Equal(Path.Combine(root, "Local", "Amberline", "app"), install.App, "the copy goes to the user's local application data");
            TestAssert.Equal(0, await install.RunAsync(), screen.ToString());

            var files = Directory.EnumerateFiles(release, "*", SearchOption.AllDirectories).ToArray();
            TestAssert.Equal(files.Length, Directory.EnumerateFiles(install.App, "*", SearchOption.AllDirectories).Count(), "the copy has every file");
            foreach (var file in files)
            {
                var copy = Path.Combine(install.App, Path.GetRelativePath(release, file));
                TestAssert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(copy)), $"{copy} is the release's file");
                TestAssert.False(HasMark(copy), $"{copy} carries the Mark of the Web");
            }
            TestAssert.False(Directory.Exists(install.App + ".new") || Directory.Exists(install.Previous), "a first install leaves no other folder");

            var (target, arguments, folder) = ReadShortcut(install.Shortcut);
            TestAssert.Equal(Path.Combine(root, "Desktop", "Amberline.lnk"), install.Shortcut);
            TestAssert.True(string.Equals(install.Installed, target, StringComparison.OrdinalIgnoreCase), $"the icon starts {target}");
            TestAssert.Equal("--home", arguments, "the icon starts the home mode");
            TestAssert.True(string.Equals(Path.Combine(install.App, "platform"), folder, StringComparison.OrdinalIgnoreCase), $"the icon starts in {folder}");
            TestAssert.Equal(install.Installed, started.Single(), "the installed copy starts, not the one in the zip");
            TestAssert.True(screen.ToString().Contains("Amberline 1.4.0 is installed in your user folder, and its icon is on your desktop.", StringComparison.Ordinal),
                screen.ToString());
            // Batch 653: a line before each wait, the copy and the start until the page answers.
            TestAssert.True(screen.ToString().Contains("Copying Amberline 1.4.0 into your user folder. This takes up to a minute.", StringComparison.Ordinal) &&
                screen.ToString().Contains(HostInstall.StartingLine, StringComparison.Ordinal), screen.ToString());

            // The installed copy and a development build (no version in BUILD.json) run in place; only an unpacked release installs.
            var local = Path.Combine(root, "Local");
            var desktop = Path.Combine(root, "Desktop");
            TestAssert.True(HostInstall.ForRelease(HostPaths.Resolve(data, local, Path.Combine(release, "platform") + Path.DirectorySeparatorChar), local, desktop,
                TextWriter.Null) is not null, "a start from the unpacked zip installs");
            TestAssert.Equal(null, HostInstall.ForRelease(HostPaths.Resolve(data, local, Path.Combine(install.App, "platform") + Path.DirectorySeparatorChar), local,
                desktop, TextWriter.Null), "the installed copy runs in place");
            var dev = Path.Combine(root, "build", "app");
            Directory.CreateDirectory(Path.Combine(dev, "platform"));
            TestAssert.Equal(null, HostInstall.ForRelease(HostPaths.Resolve(data, local, Path.Combine(dev, "platform")), local, desktop, TextWriter.Null),
                "a build without BUILD.json runs in place");
            File.WriteAllText(Path.Combine(dev, "BUILD.json"), """{"component":"deployment"}""");
            TestAssert.Equal(null, HostInstall.ForRelease(HostPaths.Resolve(data, local, Path.Combine(dev, "platform")), local, desktop, TextWriter.Null),
                "a server build (no version) runs in place");

            TestAssert.Equal(before, Snapshot(data), "installing writes nothing into the data folder");
            TestAssert.True(File.Exists(Path.Combine(release, "BUILD.json")), "the unpacked zip stays where it is");
            TestAssert.Equal(realShortcutBefore, File.Exists(realShortcut), "the real desktop is untouched");
        }
        finally { HostModeChecks.Delete(root); }
    }

    [Check("server.host-update-swap-keeps-data", TimeoutSeconds = 180)]
    public static void AnUpdateStopsTheOldAmberlineSwapsTheProgramAndKeepsTheData() => UpdateSwapAsync().GetAwaiter().GetResult();

    private static async Task UpdateSwapAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The running Amberline: a home loop in setup mode on free loopback ports (it answers the same stop request as the full one).
        var home = HostModeChecks.Home.Create("league-host-update");
        try { await UpdateSwapAsync(home); }
        finally { HostModeChecks.Delete(home.Root); }
    }

    private static async Task UpdateSwapAsync(HostModeChecks.Home home)
    {
        var root = home.Root;
        var data = home.Loop.Paths.Data;
        // Besides platform.json and page.key, a file of the host's own; the full Platform below writes the real accounts and certificate.
        WriteFile(Path.Combine(data, "kept", "notes.txt"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(256)));
        var screen = new StringWriter();
        var started = new List<string>();
        var v140 = Release(Path.Combine(root, "zip-1.4.0"), "1.4.0");
        var v150 = Release(Path.Combine(root, "zip-1.5.0"), "1.5.0");
        var installer = Installer(root, v140, data, screen, started);
        TestAssert.Equal(0, await installer.RunAsync(), screen.ToString());
        var app = installer.App;

        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        string before;
        try
        {
            using var client = home.Client();
            await HostModeChecks.WaitForHealthAsync(client, "setup", run);

            // Refused, and Amberline keeps running: a stop without the host's session, one without the confirmation form's token,
            // and one signed with another user's key.
            using (var unsigned = await client.PostAsync(HostInstall.StopPath, null))
                TestAssert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode, "a stop request needs the host's session");
            await home.SignInAsync(client);
            using (var unconfirmed = await client.PostAsync(HostInstall.StopPath, new FormUrlEncodedContent(new Dictionary<string, string> { ["games"] = "0" })))
                TestAssert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode, "a stop needs the confirmation form's token");
            // Setup mode's Stop has the dashboard's shape. Showing it makes the page's form-key ring, which then stays as it is.
            var ask = await client.GetStringAsync(HostInstall.StopPath);
            TestAssert.True(ask.Contains("Stop Amberline?", StringComparison.Ordinal) &&
                ask.Contains("<input type=hidden name=games value=0>", StringComparison.Ordinal), ask);
            await HostModeChecks.WaitForHealthAsync(client, "setup", run);
            before = Snapshot(data);
            var otherUser = Data(root, "other-user", home.Port);
            Directory.CreateDirectory(Path.GetDirectoryName(HostSessions.KeyFile(otherUser))!);
            File.WriteAllBytes(HostSessions.KeyFile(otherUser), RandomNumberGenerator.GetBytes(32));
            var foreign = new StringWriter();
            TestAssert.Equal(2, await Installer(root, v150, otherUser, foreign, started).RunAsync(), foreign.ToString());
            TestAssert.True(foreign.ToString().Contains("(A-RUN-02)", StringComparison.Ordinal), foreign.ToString());
            TestAssert.Equal(new Version(1, 4, 0), HostVersion.Read(app).Version, "a refused update keeps the installed version");
            TestAssert.False(Directory.Exists(app + ".new") || Directory.Exists(installer.Previous), "a refused update leaves no other folder");
            await HostModeChecks.WaitForHealthAsync(client, "setup", run);

            // The update: signed in with the host's own page.key, then the confirmed Stop; the folders move once the page is gone.
            TestAssert.Equal(0, await Installer(root, v150, data, screen, started).RunAsync(), screen.ToString());
            TestAssert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(60)), "the old Amberline left its loop as Stop does");
            TestAssert.Equal(null, await HostMode.ProbeAsync(home.Port), "the old Amberline's page is gone");
        }
        finally
        {
            if (!run.IsCompleted) await home.StopAsync(run);
        }
        TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(app).Version, "app holds the new version");
        TestAssert.Equal(new Version(1, 4, 0), HostVersion.Read(installer.Previous).Version, "app.previous holds the one before");
        TestAssert.Equal(Snapshot(v150), Snapshot(app), "app is the new release's copy");
        TestAssert.False(Directory.Exists(app + ".new"), "no half copy is left");
        TestAssert.Equal(before, Snapshot(data), "the data folder is untouched");
        TestAssert.Equal(installer.Installed, started.Last(), "the installed copy starts again");

        // The full Platform: the update goes through the dashboard's own Stop (it warns about running games; none run here) and
        // waits until the Platform has also let go of its account store. One step back only: app.previous is replaced.
        var variables = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var full = home.Again();
        File.WriteAllText(full.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort,
            bindAddress = "127.0.0.1", publicHost = "127.0.0.1", dataRoot = home.Game, airRoot = home.Air,
        }));
        var v160 = Release(Path.Combine(root, "zip-1.6.0"), "1.6.0");
        var fullRun = Task.Run(() => HostMode.RunAsync(full.Loop));
        try
        {
            using var client = full.Client();
            await HostModeChecks.WaitForHealthAsync(client, "ready", fullRun);
            TestAssert.Equal(0, await Installer(root, v160, data, screen, started).RunAsync(), screen.ToString());
            TestAssert.Equal(0, await fullRun.WaitAsync(TimeSpan.FromSeconds(60)), "the full Platform left its loop through the dashboard's Stop");
        }
        finally
        {
            if (!fullRun.IsCompleted) await full.StopAsync(fullRun);
            foreach (var pair in variables) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(new Version(1, 6, 0), HostVersion.Read(app).Version);
        TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(installer.Previous).Version, "app.previous is the version just replaced");

        // A start that fails puts app.previous back and starts it; the data the full Platform wrote stays as it is.
        before = Snapshot(data);
        var v170 = Release(Path.Combine(root, "zip-1.7.0"), "1.7.0");
        var failed = new StringWriter();
        var count = started.Count;
        TestAssert.Equal(0, await Installer(root, v170, data, failed, started, new Queue<bool>([false, true])).RunAsync(), failed.ToString());
        TestAssert.Equal(new Version(1, 6, 0), HostVersion.Read(app).Version, "the version that started before is back");
        TestAssert.Equal(Snapshot(v160), Snapshot(app), "it is the whole earlier copy");
        TestAssert.False(Directory.Exists(installer.Previous) || Directory.Exists(app + ".new"), "the failed version is gone");
        TestAssert.Equal(2, started.Count - count, "the new start, then the earlier one");
        TestAssert.True(failed.ToString().Contains("Amberline 1.7.0 did not start, so the Amberline you had comes back.", StringComparison.Ordinal), failed.ToString());
        TestAssert.Equal(before, Snapshot(data), "the data folder is untouched by the installer");
    }

    [Check("server.host-version-compare", TimeoutSeconds = 120)]
    public static void VersionsCompareAsNumbersAndTheUpdateCheckNeedsConsent() => VersionCompareAsync().GetAwaiter().GetResult();

    private static async Task VersionCompareAsync()
    {
        TestAssert.Equal(new Version(1, 4, 0), HostVersion.Parse("1.4.0"));
        TestAssert.Equal(new Version(1, 5, 0), HostVersion.Parse("v1.5.0"), "a release tag");
        TestAssert.True(HostVersion.Parse("1.10.0") > HostVersion.Parse("1.9.0"), "parts compare as numbers, not as text");
        foreach (var bad in new[] { null, "", "v", "1.4", "1.4.0.0", "1.4.0-beta", " 1.4.0", "1.4.x", "1..4", "-1.4.0" })
            TestAssert.Equal(null, HostVersion.Parse(bad), $"'{bad}' is no release version");

        var root = AirBootstrapChecks.TemporaryDirectory("league-host-version");
        try
        {
            var v150 = Release(Path.Combine(root, "zip-1.5.0"), "1.5.0");
            TestAssert.Equal((new Version(1, 5, 0), (DateTimeOffset?)DateTimeOffset.Parse("2026-11-02T12:00:00Z")), HostVersion.Read(v150), "BUILD.json");
            TestAssert.Equal((null, null), HostVersion.Read(Path.Combine(root, "nowhere")), "no BUILD.json");
            var damaged = Path.Combine(root, "damaged");
            WriteFile(Path.Combine(damaged, "BUILD.json"), "{\"version\":");
            TestAssert.Equal((null, null), HostVersion.Read(damaged), "a damaged BUILD.json");
            WriteFile(Path.Combine(damaged, "BUILD.json"), "[\"1.5.0\"]");
            TestAssert.Equal((null, null), HostVersion.Read(damaged), "a BUILD.json that is no object");

            // The zip's version against the installed one decides what a start from the zip does.
            var data = Data(root, "data");
            var screen = new StringWriter();
            var started = new List<string>();
            var installer = Installer(root, v150, data, screen, started);
            TestAssert.Equal(0, await installer.RunAsync(), screen.ToString());
            var app = installer.App;

            var older = new StringWriter();
            TestAssert.Equal(1, await Installer(root, Release(Path.Combine(root, "zip-1.4.0"), "1.4.0"), data, older, started).RunAsync(), older.ToString());
            TestAssert.True(older.ToString().Contains("You already have a newer Amberline (1.5.0). This zip has 1.4.0, so nothing changed.", StringComparison.Ordinal),
                older.ToString());
            TestAssert.Equal(1, started.Count, "an older zip starts nothing");

            var same = new StringWriter();
            TestAssert.Equal(0, await Installer(root, Release(Path.Combine(root, "zip-1.5.0-again"), "1.5.0"), data, same, started).RunAsync(), same.ToString());
            TestAssert.True(same.ToString().Contains("Amberline 1.5.0 is already installed. Starting it.", StringComparison.Ordinal), same.ToString());
            TestAssert.Equal(2, started.Count, "the same version starts the installed copy");
            TestAssert.Equal(installer.Installed, started[1]);

            var declined = new StringWriter();
            var v1100 = Release(Path.Combine(root, "zip-1.10.0"), "1.10.0");
            TestAssert.Equal(1, await Installer(root, v1100, data, declined, started, confirm: false).RunAsync(), declined.ToString());
            TestAssert.True(declined.ToString().Contains("You have Amberline 1.5.0. This zip has Amberline 1.10.0.", StringComparison.Ordinal) &&
                declined.ToString().Contains("To keep the one you have, close this window.\r\n" + HostInstall.ContinueLine, StringComparison.Ordinal) ||
                declined.ToString().Contains("To keep the one you have, close this window.\n" + HostInstall.ContinueLine, StringComparison.Ordinal), declined.ToString());
            TestAssert.Equal(2, started.Count, "a declined update starts nothing");

            foreach (var unchanged in new[] { older, same, declined }) TestAssert.False(unchanged.ToString().Contains("Copying", StringComparison.Ordinal));
            TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(app).Version, "older, same and declined zips keep the installed copy");
            TestAssert.False(Directory.Exists(installer.Previous) || Directory.Exists(app + ".new"), "and leave no other folder");

            TestAssert.Equal(0, await Installer(root, v1100, data, screen, started).RunAsync(), screen.ToString());
            TestAssert.Equal(new Version(1, 10, 0), HostVersion.Read(app).Version, "1.10.0 is newer than 1.5.0");
            TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(installer.Previous).Version);

            // The update check: only with the host's consent, at most once a day, one GET that names nothing about the host.
            var github = new FakeGitHub
            {
                Body = """{"tag_name":"v1.11.0","html_url":"https://github.com/Amberline-Dev/Project-Amberline/releases/tag/v1.11.0","body":"<b>Faster</b> games"}""",
            };
            using var client = new HttpClient(github);
            var choices = Path.Combine(root, "choices");
            var at = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
            TestAssert.Equal(null, (await HostUpdates.CheckAsync(choices, client, at)).Check, "no choice yet");
            HostUpdates.SaveChoice(choices, false);
            await HostUpdates.CheckAsync(choices, client, at);
            TestAssert.Equal(0, github.Requests.Count, "without the host's yes nothing is asked");

            HostUpdates.SaveChoice(choices, true);
            var found = await HostUpdates.CheckAsync(choices, client, at);
            TestAssert.Equal("GET https://api.github.com/repos/Amberline-Dev/Project-Amberline/releases/latest | Accept: application/vnd.github+json; " +
                "User-Agent: Amberline | no body", github.Requests.Single(), "the one request and everything it sends");
            TestAssert.Equal("1.11.0", found.Latest);
            TestAssert.Equal("https://github.com/Amberline-Dev/Project-Amberline/releases/tag/v1.11.0", found.Page);
            await HostUpdates.CheckAsync(choices, client, at + TimeSpan.FromHours(23));
            TestAssert.Equal(1, github.Requests.Count, "not again within a day");
            await HostUpdates.CheckAsync(choices, client, at + TimeSpan.FromHours(24));
            TestAssert.Equal(2, github.Requests.Count, "again the next day");
            HostUpdates.SaveChoice(choices, false);
            await HostUpdates.CheckAsync(choices, client, at + TimeSpan.FromDays(3));
            TestAssert.Equal(2, github.Requests.Count, "the host's no stops the daily check");
            await HostUpdates.CheckAsync(choices, client, at + TimeSpan.FromDays(3), force: true);
            TestAssert.Equal(3, github.Requests.Count, "Check now asks once");
            TestAssert.Equal(false, HostUpdates.Read(choices).Check, "Check now keeps the choice");

            // A link anywhere else is never offered, and a failed answer keeps what was known.
            github.Body = """{"tag_name":"v1.12.0","html_url":"https://example.test/releases/v1.12.0"}""";
            TestAssert.Equal(HostUpdates.ReleasesPage, (await HostUpdates.CheckAsync(choices, client, at, force: true)).Page);
            github.Status = HttpStatusCode.InternalServerError;
            var kept = await HostUpdates.CheckAsync(choices, client, at + TimeSpan.FromDays(4), force: true);
            TestAssert.Equal("1.12.0", kept.Latest, "a failed check keeps the last answer");
            TestAssert.Equal(at + TimeSpan.FromDays(4), kept.LastCheckUtc, "a failed check counts as the day's check");

            // The page says what the check found, in plain words, with the download page and the steps; it never offers a download.
            var page = HostUpdates.Render("", HostVersion.Read(app), found);
            foreach (var phrase in new[] { "You have Amberline 1.10.0 (built 2 November 2026).", "Amberline 1.11.0 is out", "Open the download page",
                         "href=\"https://github.com/Amberline-Dev/Project-Amberline/releases/tag/v1.11.0\"", "&lt;b&gt;Faster&lt;/b&gt; games",
                         "Double-click Start Amberline. Your settings and friends stay.", HostUpdates.Notice, "Check now" })
                TestAssert.True(page.Contains(phrase, StringComparison.Ordinal), $"the page does not say {phrase}: {page}");
            TestAssert.False(page.Contains("<b>Faster", StringComparison.Ordinal), "the release text is shown as text");
            var newest = HostUpdates.Render("", (new Version(1, 11, 0), null), found);
            TestAssert.True(newest.Contains("This is the newest Amberline", StringComparison.Ordinal) &&
                !newest.Contains("Amberline 1.11.0 is out", StringComparison.Ordinal), newest);
            var dev = HostUpdates.Render("", (null, null), found);
            TestAssert.True(dev.Contains("development build", StringComparison.Ordinal) && !dev.Contains("Amberline 1.11.0 is out", StringComparison.Ordinal), dev);
            TestAssert.True(HostUpdates.SetupQuestion(true).Contains(" checked", StringComparison.Ordinal), "the first run's question is ticked");
            TestAssert.False(HostUpdates.SetupQuestion(false).Contains(" checked", StringComparison.Ordinal), "a no is kept");
        }
        finally { HostModeChecks.Delete(root); }
    }

    [Check("server.host-update-confirmed-skips-only-the-enter", TimeoutSeconds = 120)]
    public static void UpdateNowsStartAsksForNoEnterAndAZipStillDoes() => ConfirmedAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 666 (owner: one click): the start Update now makes (<see cref="HostInstall.ConfirmedOption"/>) skips only the window's Enter.
    /// Each update below answers no to that question if it were asked, so only an update that never asks goes on.
    /// </summary>
    private static async Task ConfirmedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-update-confirmed");
        try
        {
            var data = Data(root, "data");
            var started = new List<string>();
            var screen = new StringWriter();
            var installer = Installer(root, Release(Path.Combine(root, "zip-1.4.0"), "1.4.0"), data, screen, started);
            TestAssert.Equal(0, await installer.RunAsync(), screen.ToString());
            var app = installer.App;

            // The option reaches the installer through the command line, as Start Amberline.cmd passes it on (%*).
            var v150 = Release(Path.Combine(root, "zip-1.5.0"), "1.5.0");
            var local = Path.Combine(root, "Local");
            var paths = HostPaths.Resolve(data, local, Path.Combine(v150, "platform") + Path.DirectorySeparatorChar);
            var parsed = HostMode.ParseInstall([HostInstall.ConfirmedOption])!.Value;
            TestAssert.True(HostInstall.ForRelease(paths, local, Path.Combine(root, "Desktop"), TextWriter.Null, parsed.Folder, parsed.Clients, parsed.Confirmed)
                is { Confirmed: true }, "Update now's start is a confirmed install");
            TestAssert.True(HostInstall.ForRelease(paths, local, Path.Combine(root, "Desktop"), TextWriter.Null) is { Confirmed: false }, "a double-click is not");

            // Confirmed: no question, the window says why, and the update is the same: app.previous, the new start.
            var confirmed = new StringWriter();
            TestAssert.Equal(0, await Installer(root, v150, data, confirmed, started, confirm: false, confirmed: true).RunAsync(), confirmed.ToString());
            TestAssert.True(confirmed.ToString().Contains("You have Amberline 1.4.0. This zip has Amberline 1.5.0.", StringComparison.Ordinal) &&
                confirmed.ToString().Contains(HostInstall.ConfirmedLine, StringComparison.Ordinal) &&
                !confirmed.ToString().Contains(HostInstall.ContinueLine, StringComparison.Ordinal), confirmed.ToString());
            TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(app).Version, "the confirmed update installed 1.5.0");
            TestAssert.Equal(new Version(1, 4, 0), HostVersion.Read(installer.Previous).Version, "and kept 1.4.0 as app.previous");
            TestAssert.Equal(installer.Installed, started.Last());

            // Everything else stays: an older zip changes nothing, and a start that fails brings the version before back.
            var older = new StringWriter();
            TestAssert.Equal(1, await Installer(root, Release(Path.Combine(root, "zip-1.3.0"), "1.3.0"), data, older, started, confirm: false, confirmed: true).RunAsync(),
                older.ToString());
            TestAssert.True(older.ToString().Contains("You already have a newer Amberline (1.5.0).", StringComparison.Ordinal), older.ToString());
            var v160 = Release(Path.Combine(root, "zip-1.6.0"), "1.6.0");
            var failed = new StringWriter();
            TestAssert.Equal(0, await Installer(root, v160, data, failed, started, new Queue<bool>([false, true]), confirm: false, confirmed: true).RunAsync(),
                failed.ToString());
            TestAssert.True(failed.ToString().Contains("Amberline 1.6.0 did not start, so the Amberline you had comes back.", StringComparison.Ordinal), failed.ToString());
            TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(app).Version, "the rollback still runs");

            // A double-click on the zip still asks for Enter (and a no keeps the version installed).
            var asked = new StringWriter();
            TestAssert.Equal(1, await Installer(root, v160, data, asked, started, confirm: false).RunAsync(), asked.ToString());
            TestAssert.True(asked.ToString().Contains(HostInstall.ContinueLine, StringComparison.Ordinal) &&
                !asked.ToString().Contains(HostInstall.ConfirmedLine, StringComparison.Ordinal), asked.ToString());
            TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(app).Version, "a no at the question installs nothing");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>An unpacked release's <c>app</c> folder: stand-in programs, the friends' archive and a BUILD.json naming the version.</summary>
    internal static string Release(string folder, string version)
    {
        var app = Path.Combine(folder, "app");
        WriteFile(Path.Combine(app, "platform", HostInstall.PlatformFile), "platform " + version);
        WriteFile(Path.Combine(app, "platform", "play-bootstrap.cmd"), "template " + version);
        WriteFile(Path.Combine(app, "server", "Amberline.Server.exe"), "server " + version);
        Directory.CreateDirectory(Path.Combine(app, "bootstrap"));
        File.WriteAllBytes(Path.Combine(app, "bootstrap", "companion.zip"), RandomNumberGenerator.GetBytes(128));
        WriteFile(Path.Combine(app, "BUILD.json"), JsonSerializer.Serialize(new { version, component = "amberline-host", builtAtUtc = "2026-11-02T12:00:00Z" }));
        return app;
    }

    /// <summary>A data folder whose platform.json names a free loopback port (or the given one), so the installer never asks 8390.</summary>
    private static string Data(string root, string name, int? port = null)
    {
        var data = Path.Combine(root, name);
        if (port is null)
        {
            var free = new TcpListener(IPAddress.Loopback, 0);
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
        }
        WriteFile(Path.Combine(data, "platform.json"), JsonSerializer.Serialize(new { httpPort = port }));
        return data;
    }

    private static HostInstall Installer(string root, string release, string data, StringWriter screen, List<string> started,
        Queue<bool>? starts = null, bool confirm = true, bool confirmed = false) =>
        new(HostPaths.Resolve(data, Path.Combine(root, "Local"), Path.Combine(release, "platform") + Path.DirectorySeparatorChar),
            Path.Combine(root, "Local"), Path.Combine(root, "Desktop"), screen)
        {
            Confirm = () => confirm,
            Confirmed = confirmed,
            Start = platform => { started.Add(platform); return Task.FromResult(starts is not { Count: > 0 } || starts.Dequeue()); },
        };

    /// <summary>Every file under a folder with its SHA-256, one line each, in order.</summary>
    internal static string Snapshot(string folder) => string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(folder, file) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        .Order(StringComparer.Ordinal));

    private static void MarkFromInternet(string file) => File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

    private static bool HasMark(string file)
    {
        try
        {
            using var stream = new FileStream(file + ":Zone.Identifier", FileMode.Open, FileAccess.Read);
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    internal static (string Target, string Arguments, string Folder) ReadShortcut(string path)
    {
        TestAssert.True(File.Exists(path), $"no shortcut at {path}");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            var link = shell.CreateShortcut(path);
            return ((string)link.TargetPath, (string)link.Arguments, (string)link.WorkingDirectory);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>GitHub's releases API as the check needs it: it records each request and answers with <see cref="Body"/>.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string Body { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = string.Join("; ", request.Headers.Select(header => header.Key + ": " + string.Join(",", header.Value)).Order(StringComparer.Ordinal));
            Requests.Add($"{request.Method} {request.RequestUri} | {headers} | {(request.Content is null ? "no body" : "body")}");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
}
