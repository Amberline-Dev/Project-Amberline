using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: the player's windows: the installer (<c>--install</c>, started by Play.cmd), the removal window (<c>--uninstall</c>), the
/// shortcut's start window and the error dialogs. Windows' own controls, folder picker and task dialogs, in plain words; every decision
/// is PlayerInstall's or PlayerUninstall's, which the checks drive without a window. Batch 639: a tab order, Enter and Escape, an
/// accessible name and description on every control, Segoe UI at 10 pt (or the system's larger text), per-monitor DPI awareness, and
/// Windows' own colours only, so a high-contrast theme applies.
/// </summary>
internal static class PlayerInstallWindow
{
    /// <summary>Sharp at 150% and 200%, and again after the window moves to a monitor with another scale.</summary>
    internal const HighDpiMode DpiMode = HighDpiMode.PerMonitorV2;
    internal const string CanCancel = "You can still cancel. Your folders stay as they are.";
    private static readonly Lock Gate = new();
    private static bool prepared;

    public static int Run(string[] args)
    {
        var file = Program.Option(args, "--connection") ?? throw new ArgumentException("--install needs --connection <server.json>.");
        // Host, port and pin only: a token in the file is never kept (the player's login supplies the bearer).
        var connection = Program.ReadRemoteInvitation(Path.GetFullPath(file)) with { Token64hex = "", Bearer = null, RegistrationCode = null };
        var name = PlayerInstall.ServerName(Program.Option(args, "--server-name") ?? PlayerInstall.DefaultServerName);
        var tool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var finished = false;
        OnWindowsThread(() =>
        {
            using var window = new InstallWindow(PlayerProfile.Current(), connection, name, tool);
            Application.Run(window);
            finished = window.Finished;
        });
        if (finished) return 0;
        Console.WriteLine("Setup was canceled. To set up later, double-click the Play file again.");
        return 1;
    }

    /// <summary>The removal window (<c>--uninstall</c>, from Uninstall.cmd in the install folder or from Windows' "Apps" list).</summary>
    public static int RunUninstall()
    {
        var profile = PlayerProfile.Current();
        var tool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (PlayerUninstall.Installed(profile, tool) is not { } record)
        {
            ShowProblem(new(PlayerInstall.DefaultServerName, "Amberline is not installed for this Windows user.",
                "Nothing was changed. To remove it for another Windows user, sign in as that user.",
                $"No install record at {profile.RecordPath}", null));
            return 1;
        }
        OnWindowsThread(() =>
        {
            using var window = new UninstallWindow(profile, record, tool);
            Application.Run(window);
        });
        return 0;
    }

    /// <summary>A failure with Copy and Close, for a program with no window of its own (the shortcut).</summary>
    internal static void ShowProblem(PlayerProblem problem) => OnWindowsThread(() => Problem(null, problem, null));

    /// <summary>A failure with Try again, Copy and Close. True when the player chose Try again.</summary>
    internal static bool AskTryAgain(PlayerProblem problem)
    {
        var again = false;
        OnWindowsThread(() => again = Problem(null, problem, TryAgainButton));
        return again;
    }

    /// <summary>
    /// Batch 697: a client left from an earlier start, with Close and start (the default), Copy and Cancel; a running game gets Copy and
    /// Cancel only. True when the player chose Close and start.
    /// </summary>
    internal static bool AskCloseLeftover(PlayerProblem problem, bool canClose)
    {
        var close = false;
        OnWindowsThread(() => close = Problem(null, problem, canClose ? CloseAndStart : null, TaskDialogButton.Cancel));
        return close;
    }

    /// <summary>
    /// NOT RETAIL (the classic start, design 2026-11-12 §2): the last classic start ended without the game client opening, so this start asks
    /// once, before anything opens. True for <see cref="ClassicAgain"/> (the default); <see cref="UsualWay"/>, Escape or closing the dialog
    /// start the usual way, for this start only.
    /// </summary>
    internal static bool AskClassic(string title)
    {
        var again = false;
        OnWindowsThread(() =>
        {
            var classic = new TaskDialogButton(ClassicAgain);
            var page = new TaskDialogPage
            {
                Caption = title,
                Heading = "Last time the classic launcher did not open the game client.",
                Text = "Open the classic launcher again, or start the usual way this time?",
                Icon = TaskDialogIcon.Information,
                AllowCancel = true,
                SizeToContent = true,
            };
            page.Buttons.Add(classic);
            page.Buttons.Add(new TaskDialogButton(UsualWay));
            page.DefaultButton = classic;
            again = TaskDialog.ShowDialog(page) == classic;
        });
        return again;
    }

    internal const string TryAgainButton = "Try again";
    internal const string CloseAndStart = "Close and start";
    internal const string ClassicAgain = "Open the classic launcher again";
    internal const string UsualWay = "Start the usual way this time";

    /// <summary>Windows' dialogs need a single-threaded apartment, which the companion's Main does not have.</summary>
    internal static void OnWindowsThread(Action action)
    {
        var thread = new Thread(() =>
        {
            Prepare();
            action();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    /// <summary>Visual styles (the task dialogs need them) and the DPI mode, once per process and before its first window.</summary>
    private static void Prepare()
    {
        lock (Gate)
        {
            if (prepared) return;
            prepared = true;
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(DpiMode);
        }
    }

    /// <summary>Segoe UI at 10 pt, or larger when Windows' "Make text bigger" has made the system's message font (9 pt) larger.</summary>
    internal static Font UiFont(FontStyle style = FontStyle.Regular, float larger = 0) =>
        new("Segoe UI", Math.Max(10F, (SystemFonts.MessageBoxFont?.SizeInPoints ?? 9F) + 1F) + larger, style);

    /// <summary>
    /// A failure as a Windows task dialog: what happened (the heading), what to do, the details behind "Show details", the log's path, a
    /// Copy button that keeps the dialog open (the text then says "Copied."), and Try again when it can help. True when the player chose
    /// Try again. Batch 653: the word list's Copy, not "Copy details". Batch 697: the button that goes on is <paramref name="action"/> (none
    /// for a failure without one), and <paramref name="dismiss"/> (Close by default) ends it.
    /// </summary>
    private static bool Problem(IWin32Window? owner, PlayerProblem problem, string? action, TaskDialogButton? dismiss = null)
    {
        var copy = new TaskDialogButton(CopyButton) { AllowCloseDialog = false };
        TaskDialogPage? shown = null;
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(problem.Report());
                if (shown is not null) shown.Text = problem.WhatToDo + "\r\n\r\n" + Copied;
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        var again = new TaskDialogButton(action ?? TryAgainButton);
        var close = dismiss ?? TaskDialogButton.Close;
        var page = shown = new TaskDialogPage
        {
            Caption = problem.Title,
            Heading = problem.WhatHappened,
            Text = problem.WhatToDo,
            Icon = TaskDialogIcon.Warning,
            Expander = new TaskDialogExpander(problem.Details) { CollapsedButtonText = "Show details", ExpandedButtonText = "Hide details" },
            Footnote = problem.Log is null ? null : new TaskDialogFootnote("The log is " + problem.Log),
            AllowCancel = true,
            SizeToContent = true,
        };
        if (action is not null) page.Buttons.Add(again);
        page.Buttons.Add(copy);
        page.Buttons.Add(close);
        page.DefaultButton = action is not null ? again : close;
        return (owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page)) == again;
    }

    /// <summary>The word list's copy button and its status (batch 653).</summary>
    internal const string CopyButton = "Copy";
    internal const string Copied = "Copied.";

    /// <summary>A question before something is deleted: <paramref name="yes"/> (its verb and object) or Cancel, with Cancel the default.</summary>
    private static bool Confirm(IWin32Window owner, string caption, string heading, string text, string yes)
    {
        var accept = new TaskDialogButton(yes);
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage { Caption = caption, Heading = heading, Text = text, Icon = TaskDialogIcon.Warning, AllowCancel = true };
        page.Buttons.Add(accept);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;
        return TaskDialog.ShowDialog(owner, page) == accept;
    }

    private static void Inform(IWin32Window owner, string caption, string heading, string text) =>
        TaskDialog.ShowDialog(owner, new TaskDialogPage { Caption = caption, Heading = heading, Text = text, Icon = TaskDialogIcon.Information });

    /// <summary>An install that stopped, in the player's words; the log has the whole exception.</summary>
    internal static PlayerProblem InstallProblem(PlayerProfile profile, string title, Exception exception)
    {
        const string where = "Your folders are where they were, or complete in the install folder. ";
        var details = PlayerProblem.Describe(exception);
        return exception switch
        {
            InstallRefusedException refused => new(title, "Setup stopped before it finished.", refused.Message, details, profile.InstallLog),
            // Batch 681: the download from the server.
            InvalidDataException => new(title, "A downloaded file did not pass its check.", exception.Message, details, profile.InstallLog),
            HttpRequestException => new(title, "The download from the server stopped.",
                "Check your internet connection, then choose Download again: it continues where it stopped. If it stops again, choose Copy and send the text to the host.",
                details, profile.InstallLog),
            UnauthorizedAccessException => new(title, "Windows did not let Amberline change a folder.",
                where + "Choose an install folder in your user folder (as Setup suggests), then choose Install again.", details, profile.InstallLog),
            IOException => new(title, "Setup stopped: a file could not be copied or moved.",
                where + "Check the drive's free space and connection, then choose Install again.", details, profile.InstallLog),
            _ => new(title, "Setup stopped before it finished.",
                where + "Choose Install to try again. If it stops again, choose Copy and send the text to the host.", details, profile.InstallLog),
        };
    }

    private static T Named<T>(T control, string name, string description) where T : Control
    {
        control.AccessibleName = name;
        control.AccessibleDescription = description;
        return control;
    }

    /// <summary>A label that wraps at <paramref name="width"/>; a path's "&amp;" is shown as it is, never as a keyboard shortcut.</summary>
    private static Label Line(string name, string description, int width) =>
        Named(new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(width, 0), Margin = new Padding(0, 6, 12, 6) }, name, description);

    private static Button Action(string text, string name, string description) =>
        Named(new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 0), Padding = new Padding(8, 2, 8, 2) },
            name, description);

    private static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        // Right to left: the first button is the rightmost; Tab still goes from the left one to the right one.
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        Named(panel, "Buttons", "The window's buttons.");
        for (var index = 0; index < buttons.Length; index++)
        {
            buttons[index].TabIndex = buttons.Length - 1 - index;
            panel.Controls.Add(buttons[index]);
        }
        return panel;
    }

    /// <summary>Adds the rows in reading order, each control's tab stop in the same order.</summary>
    private static TableLayoutPanel Rows(params (Control Text, Control? Button)[] rows)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12), Location = Point.Empty };
        var tab = 0;
        for (var row = 0; row < rows.Length; row++)
        {
            var (text, button) = rows[row];
            text.TabIndex = tab++;
            table.Controls.Add(text, 0, row);
            if (button is null) table.SetColumnSpan(text, 2);
            else
            {
                button.TabIndex = tab++;
                button.Anchor = AnchorStyles.Right;
                table.Controls.Add(button, 1, row);
            }
        }
        return table;
    }

    internal sealed class InstallWindow : Form
    {
        private readonly PlayerProfile profile;
        private readonly AirRemoteInvitation connection;
        private readonly string name;
        private readonly string tool;
        private readonly ClientPins pins;
        private readonly Font bold;
        private readonly Label airText, gameText, launcherText, downloadText, installText, spaceText, status, progressText, percent, cancelNote, doneHeading, doneText;
        private readonly Button airButton, gameButton, launcherButton, downloadButton, installButton, start, cancel, open, close;
        private PlayerDownload.Offer? offer;
        private bool downloading;
        private readonly ProgressBar progress;
        private readonly FlowLayoutPanel bar;
        private readonly TableLayoutPanel setup, done;
        private bool installKept;
        private string? air, game, launcher;
        private bool airFound, gameFound;
        private string install;
        private PlayerInstall.Plan? plan;
        private PlayerInstall.Result? result;
        private CancellationTokenSource? canceling;
        private bool busy, canCancel;

        public bool Finished { get; private set; }

        internal InstallWindow(PlayerProfile profile, AirRemoteInvitation connection, string name, string tool, ClientPins? pins = null)
        {
            this.profile = profile;
            this.connection = connection;
            this.name = name;
            this.tool = tool;
            this.pins = pins ?? new ClientPins();
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont();
            bold = UiFont(FontStyle.Bold);
            Text = $"{name} setup";
            AccessibleName = Text;
            AccessibleDescription = $"Sets up the game on patch 1.0.0.126 to play on {name}.";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // Line lengths follow the text size, so larger system text wraps the same way instead of squeezing into a column.
            var width = (int)(560 * Font.SizeInPoints / 10F);

            var existing = PlayerInstall.ExistingInstall(profile, this.pins);
            (air, game) = existing is null ? PlayerInstall.Found(profile, this.pins) : (existing.Air, existing.Game);
            (airFound, gameFound) = (air is not null, game is not null);
            installKept = existing is not null;
            install = existing?.Install ?? DefaultInstall(profile, game);
            // NOT RETAIL (classic start, design §1): an install that already holds its 2011 launcher keeps it; a new install has none until chosen.
            launcher = existing is null ? null : InstalledLauncher(existing.Install, this.pins);

            var heading = Line("Welcome", $"Welcome to {name}.", width);
            heading.Font = UiFont(FontStyle.Bold, 2);
            heading.Text = $"Welcome to {name}";
            var intro = Line("What setup does", "", width);
            intro.Text = existing is null
                ? $"Setup puts your 1.0.0.126 AIR client and game client in one folder, adds \"{name}\" to your desktop, with a copy in that folder."
                : $"Your game is set up in {existing.Install}. Setup adds \"{name}\" to your desktop, with a copy in that folder.";
            intro.AccessibleDescription = intro.Text;
            airText = Line("AIR client folder", "", width);
            gameText = Line("Game client folder", "", width);
            launcherText = Line("2011 launcher folder (optional)", "", width);
            downloadText = Line("Download from the server", "", width);
            installText = Line("Install folder", "", width);
            spaceText = Line("Free space", "", width);
            status = Line("Notes", "What setup found, or why it cannot start yet.", width);
            progressText = Line("Progress", "What setup is doing now.", width);
            cancelNote = Line("Canceling", "Whether setup can still be canceled, and why not.", width);
            percent = Named(new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(8, 3, 0, 0) }, "Percent done", "How much of the copy is done.");
            progress = Named(new ProgressBar { Style = ProgressBarStyle.Marquee, Minimum = 0, Maximum = 100, Width = width - 64, TabStop = false },
                "Setup progress", "How far setup is.");
            bar = Named(new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Visible = false },
                "Progress bar", "The progress bar and its percentage.");
            bar.Controls.Add(progress);
            bar.Controls.Add(percent);
            airButton = Action("Change…", "Change the AIR client folder", "Choose the folder that holds the assets folder.");
            gameButton = Action("Change…", "Change the game client folder", "Choose the folder that holds DATA and LEVELS.");
            // NOT RETAIL (classic start, design §1): the optional third folder, the 2011 launcher; the install works without it.
            launcherButton = Action("Change…", "Change the 2011 launcher folder", "Choose the folder of the 2011 launcher (optional): its program and its files.");
            downloadButton = Action($"&Download the game from {name}", $"Download the game from {name}",
                $"Downloads the game and the AIR client from {name} into the install folder and adds {name} to your desktop.");
            installButton = Action("Change…", "Change the install folder", "Choose where setup puts the game.");
            start = Action("&Install", "Install", $"Moves the clients into the install folder and adds {name} to your desktop.");
            cancel = Action("Cancel", "Cancel", "Closes setup, or stops the copy while that is still possible.");
            open = Action($"&Open {name} now", $"Open {name} now", $"Starts {name} from its new desktop shortcut.");
            close = Action("Close", "Close", "Closes setup.");
            foreach (var control in new Control[] { progressText, cancelNote, downloadText, downloadButton }) control.Visible = false;

            setup = Rows((heading, null), (intro, null), (airText, airButton), (gameText, gameButton), (launcherText, launcherButton), (downloadText, downloadButton),
                (installText, installButton), (spaceText, null),
                (status, null), (bar, null), (progressText, null), (cancelNote, null), (Buttons(cancel, start), null));
            Named(setup, "Setup", AccessibleDescription);
            doneHeading = Line("Done", "", width);
            doneHeading.Font = heading.Font;
            doneText = Line("What to do next", "", width);
            done = Rows((doneHeading, null), (doneText, null), (Buttons(close, open), null));
            Named(done, "Setup finished", $"{name} is ready.");
            done.Visible = false;
            setup.TabIndex = 0;
            done.TabIndex = 1;
            Controls.Add(setup);
            Controls.Add(done);
            AcceptButton = start;
            CancelButton = cancel;

            airButton.Click += (_, _) => Pick("Choose your AIR client folder (with its assets folder)", air, folder =>
                RetailClients.AirProblem(folder, this.pins.Air) is { } problem
                    ? Wrong("This is not the 1.0.0.126 AIR client.", "Choose the 2011 AIR client's folder (with its assets folder).", problem) : null,
                folder => (air, airFound) = (folder, false));
            gameButton.Click += (_, _) => Pick("Choose your game client folder (with DATA and LEVELS)", game, folder =>
                RetailClients.GameProblem(folder, this.pins.Game) is { } problem
                    ? Wrong("This is not the 1.0.0.126 game client.", "Choose the 2011 game's folder (with DATA and LEVELS).", problem) : null,
                folder => (game, gameFound) = (folder, false));
            // NOT RETAIL (classic start, design §1): the launcher folder is checked by the launcher check; its own sentence is the refusal.
            launcherButton.Click += (_, _) => Pick("Choose the folder of your 2011 launcher (its program and its files)", launcher, folder =>
                ClassicLauncherFiles.Problem(folder, this.pins.Launcher) is { } problem
                    ? Wrong("This is not the 2011 launcher.", "Choose the folder that holds the 2011 launcher's program and its files.", problem) : null,
                folder => launcher = folder);
            installButton.Enabled = !installKept;
            installButton.Click += (_, _) => Pick("Choose where to install (a new or empty folder is best)", install, _ => null,
                folder => install = InstallFolder(folder), allowNew: true);
            start.Click += async (_, _) => await InstallAsync();
            downloadButton.Click += async (_, _) => await DownloadAsync();
            cancel.Click += (_, _) => CancelClicked();
            open.Click += (_, _) => Open();
            close.Click += (_, _) => Close();
            UpdateText();
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>
        /// Batch 681: a player without a copy of their own asks the server whether it shares the game. NOT RETAIL (classic start, batch 761): an
        /// install without its 2011 launcher asks for the launcher alone, which the server offers when it shares one.
        /// </summary>
        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (installKept ? launcher is not null : (air is not null && game is not null)) return;
            try
            {
                using var client = PlayerDownload.Client(connection, out var origin);
                if (await PlayerDownload.OfferAsync(client, origin, pins, CancellationToken.None) is { } offered && !IsDisposed)
                {
                    if (!installKept) ShowOffer(offered);
                    else if (PlayerDownload.Subset(offered, path => path.StartsWith("launcher/", StringComparison.Ordinal)) is { Files.Count: > 0 } launcherOnly)
                        ShowLauncherOffer(launcherOnly);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { PlayerInstall.Log(profile, "download: the server offers no game files: " + exception.Message); }
        }

        /// <summary>The server shares the game: its size, and the button that downloads it into the install folder.</summary>
        internal void ShowOffer(PlayerDownload.Offer offered)
        {
            offer = offered;
            // NOT RETAIL (classic start, batch 761): a list that has the 2011 launcher says so.
            var what = offered.Files.Any(entry => entry.Path.StartsWith("launcher/", StringComparison.Ordinal))
                ? "the game, the AIR client and the 2011 launcher" : "the game and the AIR client";
            downloadText.Text = $"No copy of your own? {name} can send you {what} ({FolderMove.Gigabytes(offered.Bytes)} GB).";
            downloadText.AccessibleDescription = downloadText.Text;
            downloadText.Visible = downloadButton.Visible = true;
            UpdateText();
        }

        /// <summary>
        /// NOT RETAIL (classic start, batch 761): an install set up before its 2011 launcher, on a server that shares the launcher: the download button
        /// fetches the launcher alone into the install's <c>launcher</c> folder, and the text and the button say so.
        /// </summary>
        internal void ShowLauncherOffer(PlayerDownload.Offer launcherOnly)
        {
            offer = launcherOnly;
            downloadText.Text = $"Add the 2011 launcher: {name} can send you its copy ({launcherOnly.Bytes / 1e6:0} MB). The game stays as it is.";
            downloadText.AccessibleDescription = downloadText.Text;
            downloadButton.Text = $"&Download the 2011 launcher from {name}";
            downloadButton.AccessibleName = $"Download the 2011 launcher from {name}";
            downloadButton.AccessibleDescription = $"Downloads the 2011 launcher from {name} into the install folder.";
            downloadText.Visible = downloadButton.Visible = true;
            UpdateText();
        }

        /// <summary>
        /// The download, then the found copy's install (<see cref="PlayerInstall.Install"/>). Cancel keeps what came, and Download again
        /// continues from there.
        /// </summary>
        private async Task DownloadAsync()
        {
            if (offer is not { } listed) return;
            // NOT RETAIL (classic start, review round 1): an install set up before its launcher keeps the AIR and game folders its record names, read again
            // here; the download then adds the launcher only.
            var kept = installKept ? PlayerInstall.ExistingInstall(profile, pins) : null;
            if (installKept && kept is null)
            {
                status.Font = bold;
                status.Text = status.AccessibleDescription = "The game and the AIR client are not in place any more, so the 2011 launcher is not added. " +
                    "Double-click the Play file again to repair the setup.";
                return;
            }
            // NOT RETAIL (classic start, batch 761): a launcher folder the player picked is the install's own; the list's launcher files are not fetched then.
            var offered = launcher is null ? listed : PlayerDownload.Subset(listed, path => !path.StartsWith("launcher/", StringComparison.Ordinal));
            PlayerInstall.Plan chosen;
            try { chosen = PlayerDownload.Plan(install, offered, kept, profile, launcher, pins); }
            catch (InstallRefusedException refused)
            {
                status.Font = bold;
                status.Text = status.AccessibleDescription = refused.Message;
                return;
            }
            using var source = new CancellationTokenSource();
            canceling = source;
            downloading = true;
            SetBusy(true);
            ShowStep(new($"Downloading the game from {name}…", 0, CanCancel: true));
            var report = new Progress<InstallProgress>(ShowStep);
            PlayerInstall.Result finished;
            try
            {
                PlayerInstall.Log(profile, $"download: {FolderMove.Gigabytes(offered.Bytes)} GB from {name} into {chosen.Install}");
                finished = await Task.Run(async () =>
                {
                    using var client = PlayerDownload.Client(connection, out var origin);
                    await PlayerDownload.DownloadAsync(client, origin, offered, chosen, pins, name, report, source.Token);
                    return PlayerInstall.Install(profile, chosen, connection, name, tool, report);
                });
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                PlayerInstall.Log(profile, "download: canceled; what came stays for the next try");
                Stopped();
                status.Text = status.AccessibleDescription = "The download stopped. Choose Download again to continue where it stopped.";
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                PlayerInstall.Log(profile, "download: stopped: " + exception);
                Stopped();
                Problem(this, InstallProblem(profile, Text, exception), null);
                return;
            }
            finally { downloading = false; }
            canceling = null;
            SetBusy(false);
            ShowFinished(finished);
        }

        /// <summary>A move must not stop halfway: while it runs, closing the window is a cancel (when that is still possible) or nothing.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                CancelClicked();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) bold.Dispose();
            base.Dispose(disposing);
        }

        private PlayerProblem Wrong(string heading, string todo, string details) => new(Text, heading, todo, details, null);

        private void UpdateText()
        {
            airText.Text = air is null ? "AIR client: not found. Choose its folder (with its assets folder)."
                : (airFound ? "AIR client (found):\r\n" : "AIR client:\r\n") + air;
            gameText.Text = game is null ? "Game client: not found. Choose its folder (with DATA and LEVELS)."
                : (gameFound ? "Game client (found):\r\n" : "Game client:\r\n") + game;
            installText.Text = "Install folder:\r\n" + install;
            // NOT RETAIL (classic start, design §1, batch 761): the optional 2011 launcher; a new install starts without one, an install without its own asks.
            var launcherLabel = launcher is null
                ? (installKept
                    ? $"Add the 2011 launcher (optional): this install does not have it yet. Choose its folder to add it, or download it from {name} when that server shares it."
                    : "2011 launcher (optional): not chosen. Choose its folder to start the game with the 2011 launcher, or leave it out to start the usual way.")
                : (FolderMove.Same(launcher, Path.Combine(install, PlayerInstall.LauncherFolder)) ? "2011 launcher (in your install):\r\n" : "2011 launcher (optional):\r\n") + launcher;
            launcherText.Text = launcherLabel;
            foreach (var label in new[] { airText, gameText, launcherText, installText }) label.AccessibleDescription = label.Text;
            plan = null;
            spaceText.Text = "";
            status.Font = Font;
            status.Text = "";
            if (air is not null && game is not null)
            {
                try
                {
                    plan = PlayerInstall.MakePlan(profile, air, game, install, pins, launcher);
                    var root = Path.GetPathRoot(install)!;
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    spaceText.Text = $"Free space on {root.TrimEnd('\\')}: {free / 1e9:0.0} GB. " + (plan.CopyBytes > 0
                        ? $"Setup copies {plan.CopyBytes / 1e9:0.0} GB first, which takes several minutes, and removes the originals only once every file is checked."
                        : plan.Moves.Count > 0 ? "Moving your folders on this drive takes a moment." : "Your clients are already in place.");
                    status.Text = string.Join("\r\n", plan.Notes);
                }
                catch (InstallRefusedException refused)
                {
                    // No colour of its own: bold text stays readable in every theme, high contrast included.
                    status.Font = bold;
                    status.Text = refused.Message;
                }
            }
            spaceText.AccessibleDescription = spaceText.Text;
            status.AccessibleDescription = status.Text;
            start.Enabled = plan is not null;
            // Enter installs a found copy; without one, it downloads the server's (batch 681).
            AcceptButton = plan is null && offer is not null ? downloadButton : start;
        }

        private void Pick(string description, string? current, Func<string, PlayerProblem?> problem, Action<string> accept, bool allowNew = false)
        {
            using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = allowNew };
            if (current is not null && Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
            if (problem(dialog.SelectedPath) is { } wrong)
            {
                Problem(this, wrong, null);
                return;
            }
            accept(dialog.SelectedPath);
            UpdateText();
        }

        /// <summary>One step of the install: the bar (a share, or moving while a step has none), its text, and whether Cancel works now.</summary>
        internal void ShowStep(InstallProgress step)
        {
            if (canceling?.IsCancellationRequested == true) return;
            canCancel = step.CanCancel;
            progressText.Text = step.Text;
            if (step.Percent is { } share)
            {
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = Math.Clamp(share, 0, 100);
                percent.Text = $"{progress.Value}%";
            }
            else
            {
                progress.Style = ProgressBarStyle.Marquee;
                percent.Text = "";
            }
            progress.AccessibleDescription = step.Text;
            percent.AccessibleDescription = percent.Text;
            progressText.AccessibleDescription = step.Text;
            cancel.Enabled = canCancel;
            cancelNote.Text = canCancel ? CanCancel : PlayerInstall.CannotCancel;
            cancelNote.AccessibleDescription = cancelNote.Text;
        }

        private void SetBusy(bool on)
        {
            busy = on;
            foreach (var control in new Control[] { airButton, gameButton, launcherButton, downloadButton, start }) control.Enabled = !on;
            installButton.Enabled = !on && !installKept;
            cancel.Enabled = true;
            foreach (var control in new Control[] { bar, progressText, cancelNote }) control.Visible = on;
        }

        private void CancelClicked()
        {
            if (!busy)
            {
                Close();
                return;
            }
            if (!canCancel || canceling is not { } source) return;
            source.Cancel();
            cancel.Enabled = false;
            progressText.Text = downloading ? "Canceling the download. What came stays, so Download again continues from there."
                : "Canceling: removing the copy. Your folders stay as they are.";
        }

        private async Task InstallAsync()
        {
            UpdateText();
            if (plan is not { } chosen) return;
            using var source = new CancellationTokenSource();
            canceling = source;
            SetBusy(true);
            ShowStep(new("Starting. This takes a few seconds."));
            var report = new Progress<InstallProgress>(ShowStep);
            PlayerInstall.Result finished;
            try
            {
                finished = await Task.Run(() => PlayerInstall.Install(profile, chosen, connection, name, tool, report, cancel: source.Token));
            }
            catch (OperationCanceledException)
            {
                PlayerInstall.Log(profile, "install: canceled; the originals are untouched");
                Stopped();
                status.Text = "Setup was canceled. Your folders are where they were.";
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                PlayerInstall.Log(profile, "install: stopped: " + exception);
                Stopped();
                Problem(this, InstallProblem(profile, Text, exception), null);
                return;
            }
            canceling = null;
            SetBusy(false);
            ShowFinished(finished);
        }

        /// <summary>Back to the first page; when the clients were already moved before the stop, the page shows them in the install.</summary>
        private void Stopped()
        {
            canceling = null;
            SetBusy(false);
            if (PlayerInstall.ExistingInstall(profile, pins) is { } now)
            {
                (air, game, install, airFound, gameFound) = (now.Air, now.Game, now.Install, false, false);
                launcher = InstalledLauncher(now.Install, pins);
                installKept = true;
                installButton.Enabled = false;
            }
            UpdateText();
        }

        /// <summary>The finish page: "Open &lt;server&gt; now" (Enter) and "Close" (Escape).</summary>
        internal void ShowFinished(PlayerInstall.Result finished)
        {
            result = finished;
            Finished = true;
            doneHeading.Text = $"{name} is ready";
            var notes = finished.Stayed.ToList();
            if (finished.Leftovers.Count > 0) notes.Add("Delete these old folders yourself: " + string.Join(", ", finished.Leftovers));
            doneText.Text = $"\"{name}\" is on your desktop, and a copy of its shortcut is in {finished.Record.Install}, " +
                "in case the desktop icon goes missing. Open it and log in with the login the host sent you." +
                string.Concat(notes.Select(note => "\r\n\r\n" + note));
            doneHeading.AccessibleDescription = doneHeading.Text;
            doneText.AccessibleDescription = doneText.Text;
            setup.Visible = false;
            done.Visible = true;
            AcceptButton = open;
            CancelButton = close;
            open.Select();
        }

        /// <summary>Opens the new shortcut as a double-click on the desktop does.</summary>
        private void Open()
        {
            try { using (Process.Start(new ProcessStartInfo(result!.Shortcut) { UseShellExecute = true })) { } }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                Problem(this, new(Text, $"{name} could not be opened.", $"Open \"{name}\" on your desktop instead.",
                    PlayerProblem.Describe(exception), profile.InstallLog), null);
            }
            Close();
        }

        /// <summary>NOT RETAIL (classic start, design §1): the install's own 2011 launcher folder when it passes the launcher check; else null.</summary>
        private static string? InstalledLauncher(string install, ClientPins pins)
        {
            var folder = Path.Combine(install, PlayerInstall.LauncherFolder);
            return Directory.Exists(folder) && ClassicLauncherFiles.Problem(folder, pins.Launcher) is null ? folder : null;
        }

        /// <summary>The default install folder: on the game client's drive when that is not the system drive (a rename there), else the user's programs folder.</summary>
        private static string DefaultInstall(PlayerProfile profile, string? game)
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory);
            return game is not null && Path.GetPathRoot(game) is { Length: > 0 } root && !string.Equals(root, system, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, PlayerInstall.DefaultServerName)
                : Path.Combine(profile.LocalAppData, "Programs", PlayerInstall.DefaultServerName);
        }

        /// <summary>A picked folder that already holds other files gets its own Amberline folder inside it.</summary>
        private static string InstallFolder(string picked)
        {
            var folder = FolderMove.Full(picked);
            var empty = !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
            var ours = Directory.Exists(Path.Combine(folder, PlayerInstall.ToolFolder)) || Directory.Exists(Path.Combine(folder, "servers"));
            return empty || ours ? folder : Path.Combine(folder, PlayerInstall.DefaultServerName);
        }
    }

    /// <summary>
    /// "Remove this server" for each server, and "Remove Amberline from this PC" with "Also delete the game files"; both ask first, and
    /// the second names the folder and the size it frees.
    /// </summary>
    internal sealed class UninstallWindow : Form
    {
        private readonly PlayerProfile profile;
        private readonly PlayerInstall.Record record;
        private readonly string tool;
        private readonly int width;
        private readonly TableLayoutPanel servers;
        private readonly CheckBox alsoGame;
        private readonly Button removeAll, close;
        // NOT RETAIL (classic start): the 2011 launcher's row, present only while the install has the launcher's folder.
        private readonly Label? launcherLine;
        private readonly Button? removeLauncher;
        private readonly long gameBytes, toolBytes;
        private const string RemoveAllText = "&Remove Amberline…";
        private const string LauncherChoice = "Remove the 2011 launcher";
        private const string LauncherText = "Removes the 2011 launcher's files. Amberline then starts the game the usual way.";

        internal UninstallWindow(PlayerProfile profile, PlayerInstall.Record record, string tool)
        {
            this.profile = profile;
            this.record = record;
            this.tool = tool;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont();
            Text = "Remove Amberline";
            AccessibleName = Text;
            AccessibleDescription = "Removes one Amberline server, or Amberline itself, from this PC.";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            width = (int)(560 * Font.SizeInPoints / 10F);
            // NOT RETAIL (classic start): the 2011 launcher's folder is one of the game files (PlayerUninstall.GameFolders), so its size is in gameBytes.
            var launcher = Path.Combine(record.Install, PlayerInstall.LauncherFolder);
            var withLauncher = Directory.Exists(launcher);
            gameBytes = PlayerUninstall.GameFolders(record).Sum(PlayerUninstall.Size);
            toolBytes = new[] { Path.Combine(record.Install, PlayerInstall.ToolFolder), Path.Combine(record.Install, "servers"), profile.PlayerFolder, profile.OldPlayerFolder }
                .Sum(PlayerUninstall.Size);

            var heading = Line("Remove Amberline", "", width);
            heading.Font = UiFont(FontStyle.Bold, 2);
            heading.Text = "Remove a server, or Amberline itself";
            heading.AccessibleDescription = heading.Text;
            var serversHeading = Line("Servers", "The servers set up on this PC.", width);
            serversHeading.Font = UiFont(FontStyle.Bold);
            serversHeading.Text = "Servers on this PC";
            servers = Named(new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty },
                "Server list", "Each server with its Remove button. Removing one deletes its shortcut and connection; the game stays.");
            var rows = new List<(Control Text, Control? Button)> { (heading, null), (serversHeading, null), (servers, null) };
            if (withLauncher)
            {
                // NOT RETAIL (classic start, design §2): the choice for a player who wants the usual start for good. It asks first.
                launcherLine = Line("2011 launcher", LauncherText, width - 160);
                launcherLine.Text = LauncherText;
                removeLauncher = Action("Remove the 2011 &launcher…", LauncherChoice, "Asks first, then removes the 2011 launcher's files and its classic start.");
                removeLauncher.Click += (_, _) => RemoveLauncher();
                rows.Add((launcherLine, removeLauncher));
            }
            var allHeading = Line("Remove Amberline from this PC", "Removes every server, the player tool and its settings.", width);
            allHeading.Font = serversHeading.Font;
            allHeading.Text = "Remove Amberline from this PC";
            var allText = Line("What it removes", "", width);
            allText.Text = $"Removes every Amberline shortcut, the player tool and its settings ({record.Install} and your user folder).";
            allText.AccessibleDescription = allText.Text;
            // NOT RETAIL (classic start): the 2011 launcher's folder is named among the game files when the install has it.
            var clients = withLauncher ? "the AIR client, the game client and the 2011 launcher" : "the AIR client and the game client";
            alsoGame = Named(new CheckBox { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(width, 0), Enabled = gameBytes > 0, Margin = new Padding(0, 6, 0, 6) },
                "Also delete the game files", $"Deletes {clients} in the install folder too.");
            alsoGame.Text = $"Also delete the game files ({clients} in {record.Install}, {FolderMove.Gigabytes(gameBytes)} GB)";
            removeAll = Action(RemoveAllText, "Remove Amberline", "Asks first, then removes Amberline from this PC.");
            close = Action("Close", "Close", "Closes this window.");
            rows.Add((allHeading, null));
            rows.Add((allText, null));
            rows.Add((alsoGame, null));
            rows.Add((Buttons(close, removeAll), null));
            var page = Rows(rows.ToArray());
            Named(page, Text, AccessibleDescription);
            Controls.Add(page);
            CancelButton = close;
            removeAll.Click += async (_, _) => await RemoveAllAsync();
            close.Click += (_, _) => Close();
            FillServers();
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>The server rows: its name and its own Remove button, in name order.</summary>
        internal void FillServers()
        {
            servers.SuspendLayout();
            foreach (var control in servers.Controls.Cast<Control>().ToList()) control.Dispose();
            servers.Controls.Clear();
            var list = PlayerUninstall.Servers(record.Install);
            var tab = 0;
            for (var row = 0; row < list.Count; row++)
            {
                var server = list[row];
                var label = Line(server.Name, $"The server {server.Name}.", width - 160);
                label.Text = server.Name;
                label.TabIndex = tab++;
                var remove = Action($"Remove {server.Name}…", $"Remove {server.Name}", $"Asks first, then deletes the shortcut and connection of {server.Name}.");
                remove.TabIndex = tab++;
                remove.Click += (_, _) => RemoveServer(server);
                servers.Controls.Add(label, 0, row);
                servers.Controls.Add(remove, 1, row);
            }
            if (list.Count == 0)
            {
                var none = Line("No servers", "No server is set up on this PC.", width);
                none.Text = "No server is set up on this PC.";
                servers.Controls.Add(none, 0, 0);
            }
            servers.ResumeLayout();
        }

        private void RemoveServer(PlayerUninstall.Server server)
        {
            if (!Confirm(this, Text, $"Remove {server.Name}?",
                "Its shortcuts and connection are deleted. The game and your other servers stay.", $"Remove {server.Name}")) return;
            try { PlayerUninstall.RemoveServer(profile, server); }
            catch (InstallRefusedException refused)
            {
                Problem(this, new(Text, $"{server.Name} was not removed.", refused.Message, PlayerProblem.Describe(refused), profile.InstallLog), null);
            }
            FillServers();
        }

        /// <summary>
        /// NOT RETAIL (classic start, design §2): "Remove the 2011 launcher", after a question. Its files and the classic layout go, and the next start
        /// is the usual start. A refusal (a link, a file still in use) is shown with what to do, and the row stays so the choice can be made again.
        /// </summary>
        private void RemoveLauncher()
        {
            if (launcherLine is null || removeLauncher is null) return;
            if (!Confirm(this, Text, "Remove the 2011 launcher?",
                "Deletes the 2011 launcher's files in " + Path.Combine(record.Install, PlayerInstall.LauncherFolder) +
                " and the files the classic start built for it on this PC. The next start is the usual start.", "Remove the 2011 launcher")) return;
            try
            {
                PlayerUninstall.RemoveLauncher(record, GameManagedLayout.ResolveRoot(), line => PlayerInstall.Log(profile, line));
                PlayerInstall.Log(profile, "uninstall: removed the 2011 launcher");
                launcherLine.Text = "The 2011 launcher is removed. Amberline starts the game the usual way.";
                launcherLine.AccessibleDescription = launcherLine.Text;
                removeLauncher.Enabled = false;
            }
            catch (InstallRefusedException refused)
            {
                Problem(this, new(Text, "The 2011 launcher was not removed.", refused.Message, PlayerProblem.Describe(refused), profile.InstallLog), null);
            }
        }

        private async Task RemoveAllAsync()
        {
            var withGame = alsoGame.Checked;
            var bytes = FolderMove.Gigabytes(toolBytes + (withGame ? gameBytes : 0));
            var text = withGame
                ? $"This deletes {record.Install} and everything Amberline put in it, your game files too: {bytes} GB."
                : $"This deletes the player tool, its settings and every Amberline shortcut ({bytes} GB). Your game files stay in {record.Install}.";
            if (!Confirm(this, Text, "Remove Amberline from this PC?", text, "Remove Amberline")) return;
            // This program's own folder may be one it deletes: it must not keep it as its working folder.
            Environment.CurrentDirectory = Path.GetTempPath();
            var controls = new List<Control> { removeAll, close, alsoGame, servers };
            if (removeLauncher is not null) controls.Add(removeLauncher);
            foreach (var control in controls) control.Enabled = false;
            // Batch 653: removing several GB takes a moment; the button says what it is doing meanwhile.
            removeAll.Text = "Removing…";
            UseWaitCursor = true;
            PlayerUninstall.Removal removal;
            try { removal = await Task.Run(() => PlayerUninstall.RemoveAll(profile, record, withGame, tool, GameManagedLayout.ResolveRoot())); }
            catch (Exception exception) when (exception is InstallRefusedException or IOException or UnauthorizedAccessException)
            {
                UseWaitCursor = false;
                removeAll.Text = RemoveAllText;
                foreach (var control in controls) control.Enabled = true;
                alsoGame.Enabled = gameBytes > 0;
                FillServers();
                Problem(this, new(Text, "Amberline was not removed.", exception is InstallRefusedException ? exception.Message
                    : "Close the game client and the game, then choose Remove Amberline again.", PlayerProblem.Describe(exception), profile.InstallLog), null);
                return;
            }
            UseWaitCursor = false;
            if (removal.AfterExit.Count > 0)
                PlayerUninstall.DeleteAfterExit(Environment.ProcessId, removal.AfterExit, [record.Install, profile.PlayerFolder, profile.OldPlayerFolder, profile.OldPlayerParent]);
            var notes = new List<string>();
            if (removal.Kept.Count > 0) notes.Add("Your game files stay in: " + string.Join(", ", removal.Kept));
            if (removal.Left.Count > 0) notes.Add("Delete these yourself: " + string.Join(", ", removal.Left));
            Inform(this, Text, "Amberline is removed from this PC.", notes.Count == 0 ? "Nothing of it is left." : string.Join("\r\n\r\n", notes));
            Close();
        }
    }

    /// <summary>
    /// The shortcut's quiet start window: one line ("Checking for updates…", "Starting the game client…") over a moving bar, on its own
    /// thread so the start goes on, closed once the League client's window appears. A window that cannot be shown is skipped.
    /// </summary>
    internal sealed class StatusWindow(string title) : IDisposable
    {
        private Form? form;
        private Label? line;
        private Thread? thread;

        public void Show(string text)
        {
            if (Volatile.Read(ref form) is { } shown)
            {
                try { shown.BeginInvoke(() => line!.Text = line.AccessibleDescription = text); }
                catch (InvalidOperationException) { }
                return;
            }
            var ready = new ManualResetEventSlim();
            var started = new Thread(() =>
            {
                try
                {
                    Prepare();
                    using var window = new Form
                    {
                        Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, ShowInTaskbar = true,
                        StartPosition = FormStartPosition.CenterScreen, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    };
                    window.SuspendLayout();
                    window.AutoScaleDimensions = new SizeF(96F, 96F);
                    window.AutoScaleMode = AutoScaleMode.Dpi;
                    window.Font = UiFont();
                    Named(window, title, $"{title} is starting.");
                    line = Named(new Label { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 8) }, "Status", text);
                    var moving = Named(new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 320 }, "Working", "Amberline is working.");
                    var panel = Named(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(16) },
                        "Status", text);
                    panel.Controls.Add(line);
                    panel.Controls.Add(moving);
                    window.Controls.Add(panel);
                    window.ResumeLayout(false);
                    window.Shown += (_, _) =>
                    {
                        Volatile.Write(ref form, window);
                        ready.Set();
                    };
                    Application.Run(window);
                    Interlocked.CompareExchange(ref form, null, window);
                }
                // A start window is a courtesy: without a desktop to show it on, the start goes on without it.
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                    System.Runtime.InteropServices.ExternalException) { }
                finally { ready.Set(); }
            }) { IsBackground = true };
            started.SetApartmentState(ApartmentState.STA);
            thread = started;
            started.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        public void Hide()
        {
            if (Interlocked.Exchange(ref form, null) is { } shown)
            {
                try { shown.BeginInvoke(shown.Close); }
                catch (InvalidOperationException) { }
            }
            if (Interlocked.Exchange(ref thread, null) is { } running && running != Thread.CurrentThread) running.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Hide();
    }
}
