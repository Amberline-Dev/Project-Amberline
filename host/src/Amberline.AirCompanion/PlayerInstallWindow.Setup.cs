using System.Windows.Forms;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL (batch 920, owner design 20 November 2026; the states and texts are the orchestrator's calls): the setup window shows one first
/// button for the step it is in, and every other way under Advanced (closed at first): the server's download when it shares the game, the
/// client zip when it does not, Install when a copy was found or the install is kept. Retail players had Riot's patcher instead.
/// </summary>
internal static partial class PlayerInstallWindow
{
    /// <summary>
    /// What the server's <c>/files</c> answers: its checked list (<see cref="PlayerDownload.OfferAsync"/>), "still preparing" (null, true) or none
    /// (null, false: a 404, the server shares no game). A host that has just turned sharing on holds <c>/files</c> until it has checked every file
    /// (<c>HostFileShare.ServeAsync</c>, minutes), so the pinned client's 15 s run out first (a TaskCanceledException around a TimeoutException); the
    /// server is preparing, not gone, when its manifest (<c>/bootstrap/client.json</c>) still answers. Anything else throws, as the list's read does.
    /// </summary>
    internal static async Task<(PlayerDownload.Offer? Offer, bool Preparing)> AskShareAsync(HttpClient client, Uri origin, ClientPins pins)
    {
        try { return (await PlayerDownload.OfferAsync(client, origin, pins, CancellationToken.None), false); }
        catch (TaskCanceledException timeout) when (timeout.InnerException is TimeoutException)
        {
            await PlayerInstall.GetAsync(client, new Uri(origin, "/bootstrap/client.json"), 1 << 20, CancellationToken.None);
            return (null, true);
        }
    }

    private const string Folded = "► &Advanced", Unfolded = "▼ &Advanced";

    /// <summary>The client zip button's description, wherever the button is.</summary>
    private static string ZipDescription => $"Choose the client zip ({ClientZip.BundleName}). Setup unpacks it and installs the game.";

    internal sealed partial class InstallWindow
    {
        /// <summary>The step setup is in, which names the first button.</summary>
        internal enum Step { Checking, Download, Continue, Failed, Preparing, Zip, Install }

        private readonly Label intro;
        private readonly Button primary;
        private readonly CheckBox advancedToggle;
        private readonly TableLayoutPanel advanced;
        private bool checking, preparing, resumable, zipRow, downloadRow, primaryShown;
        private string? failure;

        /// <summary>A copy (found, chosen or kept) installs; else the server's offer downloads, or the client zip when the server shares nothing.</summary>
        private Step Current() =>
            installKept || air is not null && game is not null ? Step.Install
            : offer is not null ? (failure is not null ? Step.Failed : resumable ? Step.Continue : Step.Download)
            : checking ? Step.Checking : preparing ? Step.Preparing : Step.Zip;

        /// <summary>The first button and the line over it for the step, Enter on it, and the two rows Advanced holds only when they are not first.</summary>
        private void Settle()
        {
            var step = Current();
            var size = offer is null ? "" : FolderMove.Gigabytes(offer.Bytes);
            const string installName = "Install", zipName = "Use the client zip";
            // The folders themselves are under Advanced: long paths are no help to a first look.
            var installLine = installKept ? $"Your game is set up in {install}. Setup adds \"{name}\" to your desktop, with a copy in that folder."
                : (airFound && gameFound ? "Setup found the game and the AIR client on this PC." : "Setup uses the game and AIR client folders you chose.") +
                    $" Install puts them in one folder and adds \"{name}\" to your desktop.";
            (intro.Text, primary.Text, primary.AccessibleName, primary.AccessibleDescription) = step switch
            {
                // A plan setup refuses: the bold note says why; Try again checks again and installs once nothing is in the way (a disabled flat
                // button's text is drawn darker than its navy, so it could not be read).
                Step.Install when plan is null => (installLine, "&" + TryAgainButton, TryAgainButton, "Checks the folders again and installs if nothing is in the way."),
                Step.Install => (installLine, "&" + installName, installName,
                    installKept ? $"Adds {name} to your desktop." : $"Moves the clients into the install folder and adds {name} to your desktop."),
                Step.Download => ($"{name} shares {What(offer!)}. Setup downloads and installs them, then adds \"{name}\" to your desktop.",
                    $"&Download the game from {name} ({size} GB)", $"Download the game from {name}",
                    $"Downloads {What(offer!)} from {name} ({size} GB) into the install folder and installs them."),
                Step.Continue => ($"The download from {name} stopped. You can continue it from where it stopped. Setup then installs the game.",
                    $"&Continue the download from {name}", $"Continue the download from {name}", "Continues the download where it stopped, then installs the game."),
                Step.Failed => (failure + " Choose Try again to continue where it stopped.", "&" + TryAgainButton, TryAgainButton, "Continues the download where it stopped."),
                Step.Preparing => ($"{name} is still preparing its game files. Try again in a few minutes.", "&" + TryAgainButton, TryAgainButton,
                    $"Asks {name} again whether its game files are ready."),
                Step.Zip => ($"Setup needs the client zip ({ClientZip.BundleName}). This one file contains the game, the AIR client and the 2011 launcher. " +
                    "Your host can tell you where to get it.", "Use the client &zip…", zipName, ZipDescription),
                _ => ($"Checking whether {name} shares the game…", primary.Text, primary.AccessibleName, primary.AccessibleDescription),
            };
            intro.AccessibleDescription = intro.Text;
            // No first button while the server is asked.
            var shown = step != Step.Checking;
            var appears = shown && !primaryShown;
            primary.Visible = primaryShown = shown;
            primary.Enabled = !busy;
            // NOT RETAIL (batch 765): an install that is set up already has its clients; the zip is for a new one.
            zipRow = !installKept && step != Step.Zip;
            downloadRow = offer is not null && step == Step.Install;
            zipText.Visible = zipButton.Visible = zipRow;
            downloadText.Visible = downloadButton.Visible = downloadRow;
            AcceptButton = shown ? primary : null;
            // A first button that appears (the server answered, a stop) takes the focus from Advanced or Cancel.
            if (appears) primary.Select();
        }

        private Task PrimaryAsync() => Current() switch
        {
            Step.Install => InstallAsync(),
            Step.Zip => ZipAsync(),
            Step.Preparing => AskServerAsync(),
            Step.Checking => Task.CompletedTask,
            _ => DownloadAsync(),
        };

        /// <summary>
        /// Batch 681: a player without a copy of their own asks the server whether it shares the game. NOT RETAIL (classic start, batch 761): an
        /// install without its 2011 launcher asks for the launcher alone, which the server offers when it shares one. Batch 920: meanwhile the window
        /// says it is asking; a server still preparing its files gets Try again (<see cref="AskShareAsync"/>).
        /// </summary>
        private async Task AskServerAsync()
        {
            if (installKept ? launcher is not null : (air is not null && game is not null)) return;
            if (!installKept)
            {
                (checking, preparing) = (true, false);
                Settle();
            }
            PlayerDownload.Offer? offered = null;
            var waiting = false;
            try
            {
                using var client = PlayerDownload.Client(connection, out var origin);
                (offered, waiting) = await AskShareAsync(client, origin, pins);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { PlayerInstall.Log(profile, "download: the server offers no game files: " + exception.Message); }
            if (IsDisposed) return;
            if (waiting) PlayerInstall.Log(profile, "download: the server is still preparing its game files");
            if (offered is null) ShowNoOffer(waiting);
            else if (!installKept) ShowOffer(offered);
            else if (PlayerDownload.Subset(offered, path => path.StartsWith("launcher/", StringComparison.Ordinal)) is { Files.Count: > 0 } launcherOnly)
                ShowLauncherOffer(launcherOnly);
        }

        /// <summary>The server shares no game (<paramref name="waiting"/> false: the client zip is first) or is still preparing it (Try again is first).</summary>
        internal void ShowNoOffer(bool waiting)
        {
            (checking, preparing) = (false, waiting);
            if (!busy) UpdateText();
        }

        /// <summary>
        /// Back from a download that stopped: canceled (<paramref name="failed"/> null: what came stays, and the first button continues it) or failed (its
        /// one sentence over Try again). A canceled launcher download of a kept install says so under its Install.
        /// </summary>
        internal void ShowStopped(string? failed)
        {
            (failure, resumable) = (failed, true);
            Stopped();
            if (failed is null && Current() == Step.Install)
                status.Text = status.AccessibleDescription = "The download stopped. Choose it again under Advanced to continue where it stopped.";
        }

        /// <summary>NOT RETAIL (classic start, batch 761): a list that has the 2011 launcher says so.</summary>
        private static string What(PlayerDownload.Offer offered) => offered.Files.Any(entry => entry.Path.StartsWith("launcher/", StringComparison.Ordinal))
            ? "the game, the AIR client and the 2011 launcher" : "the game and the AIR client";

        /// <summary>DIAGNOSTICS (batch 920): what the checks read, since every control of a window that is never shown reads Visible false.</summary>
        internal Step Now => Current();

        /// <summary>DIAGNOSTICS (batch 920): the first button, whether it shows, and the line over it.</summary>
        internal Button First => primary;
        internal bool FirstShown => primaryShown;
        internal string Says => intro.Text;

        /// <summary>DIAGNOSTICS (batch 920): Advanced's toggle, and the buttons Advanced holds in this step, in order.</summary>
        internal CheckBox AdvancedToggle => advancedToggle;
        internal IEnumerable<string> Offered => new (Control Button, bool Shown)[]
            {
                (zipButton, zipRow), (airButton, true), (gameButton, true), (launcherButton, true), (downloadButton, downloadRow), (installButton, true),
            }.Where(row => row.Shown).Select(row => row.Button.AccessibleName!);
    }
}
