using System.Net;
using System.Text.Json;
using Amberline.AirCompanion;
using Forms = System.Windows.Forms;
using Step = Amberline.AirCompanion.PlayerInstallWindow.InstallWindow.Step;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 920, owner design 20 November 2026; the states are the orchestrator's calls): the setup window's one first button per step, what
/// Advanced holds then, and the app's first window. Windows are built and read, never shown: every control of a hidden window reads Visible false, so
/// the window's DIAGNOSTICS seams (<c>Now</c>, <c>First</c>, <c>Says</c>, <c>Offered</c>) say what it shows. DIAGNOSTICS: with
/// AMBERLINE_WINDOW_RENDERS set to a folder, each step is saved there as a PNG for review; otherwise nothing is written.
/// </summary>
public static partial class AirPlayerInstallChecks
{
    private static readonly string[] ClientFolders = ["Change the AIR client folder", "Change the game client folder", "Change the 2011 launcher folder"];

    private static PlayerDownload.Offer SharedGame => new([new("air/LolClient.exe", 1, new string('0', 64))], 2_600_000_000);

    /// <summary>State 1: nothing found, the server shares the game: one line while it is asked, then "Download the game from Alpha (2.6 GB)" first.</summary>
    [Check("server.air-player-setup-download-first", TimeoutSeconds = 300)]
    public static void AServerThatSharesTheGameOffersItsDownloadFirst() => WithRoot("league-air-setup-download", root =>
    {
        var fixture = Fixture.Make(root);
        OnSta(() =>
        {
            using var window = SetupWindow(fixture);
            TestAssert.Equal((Step.Checking, false, "Checking whether Alpha shares the game…"), (window.Now, window.FirstShown, window.Says));
            TestAssert.True(window.AcceptButton is null, "Enter has a button while setup asks the server");
            Snap(window, "1-checking");

            window.ShowOffer(SharedGame);
            TestAssert.Equal((Step.Download, "Download the game from Alpha", "&Download the game from Alpha (2.6 GB)"), (window.Now, window.First.AccessibleName, window.First.Text));
            TestAssert.Equal("Alpha shares the game and the AIR client. Setup downloads and installs them, then adds \"Alpha\" to your desktop.", window.Says);
            TestAssert.True(window.AcceptButton == window.First && window.First.Enabled && window.FirstShown, "Enter is not the download");
            // Advanced, closed at first: the client zip, the three folders and the install folder; the download is the first button, not a row.
            TestAssert.False(window.AdvancedToggle.Checked, "Advanced is open at first");
            AssertOffered(window, ["Use the client zip", .. ClientFolders, "Change the install folder"]);
            AssertAccessible(window, themed: true);
            Snap(window, "1-download");

            // Opening it is a toggle that says so (its checked state), from the keyboard too (Alt+A).
            window.AdvancedToggle.Checked = true;
            TestAssert.Equal(("▼ &Advanced", true), (window.AdvancedToggle.Text, window.AdvancedToggle.TabStop));
            TestAssert.True((window.AdvancedToggle.AccessibilityObject.State & Forms.AccessibleStates.Checked) != 0, "the open Advanced is not announced");
            Snap(window, "1-download-advanced");

            // Batch 922: a server that shares its 2011 launcher too names it, over the first button and in the download's row.
            window.ShowOffer(new([new("air/LolClient.exe", 1, new string('0', 64)), new("launcher/LoLLauncher.exe", 1, new string('0', 64))], 2_600_000_000));
            TestAssert.Equal("Alpha shares the game, the AIR client and the 2011 launcher. Setup downloads and installs them, then adds \"Alpha\" to your desktop.", window.Says);
            TestAssert.Equal("Alpha can also send you the game, the AIR client and the 2011 launcher (2.6 GB).", Find(window, "Download from the server").Text);
        });
    });

    /// <summary>
    /// State 2: nothing found, no download: "Use the client zip…" first with one plain line about the zip (no address); a server still preparing
    /// its files gets that sentence and Try again. What tells them apart is the server's answer (<see cref="PlayerInstallWindow.AskShareAsync"/>).
    /// </summary>
    [Check("server.air-player-setup-zip-first", TimeoutSeconds = 300)]
    public static void AServerThatSharesNothingOffersTheClientZipFirst() => WithRoot("league-air-setup-zip", root =>
    {
        var fixture = Fixture.Make(root);
        OnSta(() =>
        {
            using var window = SetupWindow(fixture);
            window.ShowNoOffer(false);
            TestAssert.Equal((Step.Zip, "Use the client zip", "Use the client &zip…"), (window.Now, window.First.AccessibleName, window.First.Text));
            TestAssert.True(window.Says.Contains(ClientZip.BundleName, StringComparison.Ordinal) && !window.Says.Contains("://", StringComparison.Ordinal) &&
                !window.Says.Contains("www.", StringComparison.Ordinal), "the zip's line: " + window.Says);
            TestAssert.True(window.AcceptButton == window.First, "Enter is not the client zip");
            AssertOffered(window, [.. ClientFolders, "Change the install folder"]);
            AssertAccessible(window, themed: true);
            Snap(window, "2-zip");

            window.ShowNoOffer(true);
            TestAssert.Equal((Step.Preparing, PlayerInstallWindow.TryAgainButton), (window.Now, window.First.AccessibleName));
            TestAssert.Equal("Alpha is still preparing its game files. Try again in a few minutes.", window.Says);
            AssertOffered(window, ["Use the client zip", .. ClientFolders, "Change the install folder"]);
            Snap(window, "2-preparing");
        });

        // The server's answers: /files held while the manifest answers is "preparing"; a 404 shares nothing; a server that answers neither throws (the
        // window then offers the client zip and logs why).
        (PlayerDownload.Offer? Offer, bool Preparing) Ask(HttpStatusCode? files, bool manifest)
        {
            using var client = new HttpClient(new HeldShare(files, manifest)) { Timeout = TimeSpan.FromSeconds(1) };
            return PlayerInstallWindow.AskShareAsync(client, new Uri("https://share.example.test:8391/"), fixture.Pins).GetAwaiter().GetResult();
        }
        TestAssert.Equal(((PlayerDownload.Offer?)null, true), Ask(null, manifest: true), "a held /files on a server that answers");
        TestAssert.Equal(((PlayerDownload.Offer?)null, false), Ask(HttpStatusCode.NotFound, manifest: true), "a 404");
        TestAssert.Throws<TaskCanceledException>(() => Ask(null, manifest: false), "a server that answers nothing counted as preparing");
    });

    /// <summary>State 3: a copy found on this PC: Install first, with one line naming the two folders; Advanced as in state 1, plus the offer when there is one.</summary>
    [Check("server.air-player-setup-found-install", TimeoutSeconds = 300)]
    public static void ACopyOnThisPcInstallsFirst() => WithRoot("league-air-setup-found", root =>
    {
        var fixture = Fixture.Make(root);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Profile.OldStage)!);
        File.WriteAllText(fixture.Profile.OldStage, JsonSerializer.Serialize(new { airSource = fixture.Air, stockExecutable = fixture.GameExecutable }));
        OnSta(() =>
        {
            using var window = SetupWindow(fixture);
            TestAssert.Equal((Step.Install, "Install", true, true), (window.Now, window.First.AccessibleName, window.First.Enabled, window.FirstShown));
            TestAssert.Equal("Setup found the game and the AIR client on this PC. Install puts them in one folder and adds \"Alpha\" to your desktop.", window.Says);
            TestAssert.True(Find(window, "Game client folder").Text.EndsWith(fixture.Game, StringComparison.Ordinal), "Advanced names the found game folder");
            AssertOffered(window, ["Use the client zip", .. ClientFolders, "Change the install folder"]);
            window.ShowOffer(SharedGame);
            TestAssert.Equal((Step.Install, "Install"), (window.Now, ((Forms.Control)window.AcceptButton!).AccessibleName));
            AssertOffered(window, ["Use the client zip", .. ClientFolders, "Download the game from Alpha", "Change the install folder"]);
            Snap(window, "3-found");
        });

        // A plan setup refuses (here: a host's settings on this PC that cannot be read): the bold note says why, and Try again (checks again, then
        // installs) takes Install's place, since a disabled flat button could not be read.
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Profile.HostSettings)!);
        File.WriteAllText(fixture.Profile.HostSettings, "{ not json");
        OnSta(() =>
        {
            using var window = SetupWindow(fixture);
            var note = Find(window, "Notes");
            TestAssert.Equal((Step.Install, true, PlayerInstallWindow.TryAgainButton), (window.Now, window.FirstShown, window.First.AccessibleName), "a refused plan: " + note.Text);
            TestAssert.True(window.AcceptButton == window.First && window.First.Enabled && note.Text.Length > 0 && note.Font.Bold, "no bold refusal: " + note.Text);
            AssertOffered(window, ["Use the client zip", .. ClientFolders, "Change the install folder"]);
            Snap(window, "3-refused");
        });
    });

    /// <summary>State 4: a kept install (a second server, a repair): the one Install, the line naming the install; no client zip under Advanced.</summary>
    [Check("server.air-player-setup-kept-install", TimeoutSeconds = 300)]
    public static void AKeptInstallKeepsItsOneInstall() => WithRoot("league-air-setup-kept", root =>
    {
        var fixture = Fixture.Make(root);
        var installed = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("two.example.test", PinB), "Bravo", fixture.Tool);
        OnSta(() =>
        {
            using var window = SetupWindow(fixture);
            TestAssert.Equal((Step.Install, "Install", true), (window.Now, window.First.AccessibleName, window.FirstShown));
            TestAssert.Equal($"Your game is set up in {installed.Record.Install}. Setup adds \"Alpha\" to your desktop, with a copy in that folder.", window.Says);
            TestAssert.True(window.First.Enabled, "the kept install makes no plan: " + Find(window, "Notes").Text);
            AssertOffered(window, [.. ClientFolders, "Change the install folder"]);
            TestAssert.False(Find(window, "Change the install folder").Enabled, "a kept install's folder can be changed");
            window.ShowLauncherOffer(new([new("launcher/LoLLauncher.exe", 1, new string('0', 64))], 40_000_000));
            TestAssert.Equal(Step.Install, window.Now);
            AssertOffered(window, [.. ClientFolders, "Download the 2011 launcher from Alpha", "Change the install folder"]);
            AssertAccessible(window, themed: true);
            Snap(window, "4-kept");
        });
    });

    /// <summary>
    /// State 5: the download runs (the bar, Cancel, the first button off), is canceled (the first button continues it) or fails (its one sentence over
    /// Try again); the failure dialog's text names Try again too.
    /// </summary>
    [Check("server.air-player-setup-download-stopped", TimeoutSeconds = 300)]
    public static void AStoppedDownloadContinuesOrTriesAgain() => WithRoot("league-air-setup-stopped", root =>
    {
        var fixture = Fixture.Make(root);
        OnSta(() =>
        {
            using var window = SetupWindow(fixture);
            window.ShowOffer(SharedGame);
            window.SetBusy(true);
            window.ShowStep(new("Downloading the game from Alpha: 1.2 of 2.6 GB", 46, CanCancel: true));
            TestAssert.Equal((false, false, true, false), (window.FirstShown, window.First.Enabled, Find(window, "Cancel").Enabled, window.AdvancedToggle.Enabled));
            TestAssert.Equal("46%", Find(window, "Percent done").Text);
            Snap(window, "5-running");

            window.ShowStopped(null);
            TestAssert.Equal((Step.Continue, "Continue the download from Alpha", true), (window.Now, window.First.AccessibleName, window.First.Enabled));
            TestAssert.Equal("The download from Alpha stopped. You can continue it from where it stopped. Setup then installs the game.", window.Says);
            TestAssert.True(window.AcceptButton == window.First && window.AdvancedToggle.Enabled, "the stopped window is not back");
            AssertOffered(window, ["Use the client zip", .. ClientFolders, "Change the install folder"]);
            Snap(window, "5-canceled");

            window.ShowStopped("The download from the server stopped.");
            TestAssert.Equal((Step.Failed, PlayerInstallWindow.TryAgainButton), (window.Now, window.First.AccessibleName));
            TestAssert.Equal("The download from the server stopped. Choose Try again to continue where it stopped.", window.Says);
            AssertAccessible(window, themed: true);
            Snap(window, "5-failed");
        });
        var dropped = PlayerInstallWindow.InstallProblem(fixture.Profile, "Alpha setup", new HttpRequestException("The connection dropped."));
        TestAssert.Equal("The download from the server stopped.", dropped.WhatHappened);
        TestAssert.True(dropped.WhatToDo.Contains("choose Try again", StringComparison.Ordinal), dropped.WhatToDo);
    });

    /// <summary>
    /// The app's first window: the Join step is the headline (the largest text), the browser's question next, the paste box below it as the fallback;
    /// no sentence twice; Add still refuses a damaged link under the box and the window stays.
    /// </summary>
    [Check("server.air-player-welcome-join-first")]
    public static void TheAppsFirstWindowLeadsWithJoin() => OnSta(() =>
    {
        if (!OperatingSystem.IsWindows()) return;
        using var window = new PlayerInstallWindow.WelcomeWindow();
        TestAssert.Equal("Go back to your browser and click Join on the invite page.", Find(window, "Join").Text);
        TestAssert.Equal($"Your browser may ask to open {PlayerInvite.Product}. Allow it.", Find(window, "Allow").Text);
        TestAssert.Equal("If Join does not open anything, paste the invite link here.", Find(window, "Paste instead").Text);
        var texts = All(window).OfType<Forms.Label>().Select(label => label.Text).Where(text => text.Length > 0).ToList();
        TestAssert.Equal("Go back to your browser and click Join on the invite page.", texts[0], "the headline is not first");
        TestAssert.Equal(texts.Count, texts.Distinct().Count(), "a sentence twice: " + string.Join(" | ", texts));
        TestAssert.True(Find(window, "Join").Font.SizeInPoints > Find(window, "Paste instead").Font.SizeInPoints, "the fallback is not smaller than the headline");
        TestAssert.Equal(new[] { "Add", "Close" }, TabOrder(window).ToArray());
        TestAssert.Equal(("Add", "Close"), (((Forms.Control)window.AcceptButton!).AccessibleName, ((Forms.Control)window.CancelButton!).AccessibleName));
        AssertAccessible(window, themed: true);
        Snap(window, "welcome");

        // A hidden window's button ignores PerformClick, so the check raises its Click as a press would.
        ((Forms.TextBox)Find(window, "Invite link")).Text = "not an invite";
        typeof(Forms.Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(Find(window, "Add"), [EventArgs.Empty]);
        TestAssert.Equal((InviteLink.Decode("not an invite").Refusal, (string?)null), (Find(window, "Problem").Text, window.Invite));
        Snap(window, "welcome-refused");
    });

    private static PlayerInstallWindow.InstallWindow SetupWindow(Fixture fixture) =>
        new(fixture.Profile, Connection("one.example.test", PinA), "Alpha", fixture.Tool, fixture.Pins);

    private static void AssertOffered(PlayerInstallWindow.InstallWindow window, string[] expected)
    {
        var offered = window.Offered.ToArray();
        TestAssert.Equal(expected, offered, "Advanced holds " + string.Join(" | ", offered));
    }

    /// <summary>WCAG's contrast ratio of two colours (relative luminance, sRGB).</summary>
    private static double Contrast(System.Drawing.Color one, System.Drawing.Color two)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(System.Drawing.Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (a, b) = (Luminance(one), Luminance(two));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>DIAGNOSTICS (batch 920): with AMBERLINE_WINDOW_RENDERS set to a folder, a PNG of the window (never shown), named after the line and the step.</summary>
    private static void Snap(Forms.Form window, string step)
    {
        if (Environment.GetEnvironmentVariable("AMBERLINE_WINDOW_RENDERS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        PlayerInstallWindow.Render(window, Path.Combine(folder, $"{InviteLink.Line}-{step}.png"));
    }

    /// <summary>A server in memory: <c>/files</c> answers <paramref name="files"/> or is held until the client gives up; the manifest answers or is held.</summary>
    private sealed class HeldShare(HttpStatusCode? files, bool manifest) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var answer = request.RequestUri!.AbsolutePath == "/files" ? files : manifest ? HttpStatusCode.OK : null;
            if (answer is not { } status)
            {
                await Task.Delay(Timeout.Infinite, cancel);
                throw new InvalidOperationException("unreachable");
            }
            return new HttpResponseMessage(status) { Content = new ByteArrayContent("{}"u8.ToArray()) };
        }
    }
}
