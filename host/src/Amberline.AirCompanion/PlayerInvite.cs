using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL (batch 917, owner design 20 November 2026; retail players installed with the publisher's patcher): the player app's invite. It
/// replaced Play.cmd, which Microsoft Defender quarantined. Three ways in:
/// <list type="bullet">
/// <item><c>--invite &lt;text&gt;</c> (<see cref="Run"/>): a Join click on the invite page (this line's link scheme) or the welcome window's
/// Add. The invite (<see cref="InviteLink"/>) is decoded and the player asked "Add &lt;name&gt; (&lt;host&gt;)?": nothing is added or downloaded
/// before Yes. Then what Play.cmd did (<see cref="SetUpAsync"/>): the server's player tool over its pinned port into the player folder, its
/// connection beside it, and that tool's <c>--install</c>, whose window installs, or only adds the server to an install that is there.</item>
/// <item>No arguments, as the one downloaded file (<see cref="Bundled"/>, <see cref="Welcome"/>): it copies only itself into the player folder
/// (<see cref="Adopt"/>), registers the link scheme to that copy, and asks for the invite in a small window.</item>
/// <item>The installed shortcut's start registers the scheme to the install's windowless tool when none that works is registered
/// (<see cref="Register"/>: installs made with Play.cmd), and the removal of the install takes it away (<see cref="Unregister"/>).</item>
/// </list>
/// </summary>
internal static class PlayerInvite
{
    /// <summary>This line's player folder's name, the app's name in its windows: <c>Amberline Player</c> (1.0.0.132: <c>Amberline 132 Player</c>).</summary>
    internal static readonly string Product = Path.GetFileName(FolderMove.PlayerFolder(""));

    /// <summary>The app's own copy in the player folder, which the link scheme starts.</summary>
    internal static readonly string CopyName = Product + ".exe";

    internal const string Refused = "This invite link can't be used.";

    /// <summary>
    /// The one downloaded file (a single-file publish, tools/build-server.ps1): its assembly is inside it, not beside it. The installed tool
    /// (the companion folder of an install, or a development build) has <c>Amberline.AirCompanion.dll</c> beside its program.
    /// </summary>
    internal static bool Bundled => !File.Exists(Path.Combine(AppContext.BaseDirectory, "Amberline.AirCompanion.dll"));

    /// <summary>
    /// DIAGNOSTICS: the per-user registration of the link scheme as delegates (a test seam): the command it opens, or null; write it; remove
    /// the scheme. <see cref="Windows"/> is <c>HKCU\Software\Classes\&lt;scheme&gt;</c>; checks pass their own, so no check writes the registry.
    /// </summary>
    internal sealed record SchemeKey(Func<string?> Read, Action<string> Write, Action Delete)
    {
        private const string Classes = @"Software\Classes\" + InviteLink.Scheme;

        /// <summary>The spec's registration: default value <c>URL:Amberline Player</c>, an empty <c>URL Protocol</c>, and the open command.</summary>
        internal static SchemeKey Windows { get; } = new(
            () =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(Classes + @"\shell\open\command");
                return key?.GetValue(null) as string;
            },
            command =>
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Classes, writable: true))
                {
                    key.SetValue(null, "URL:Amberline Player");
                    key.SetValue("URL Protocol", "");
                }
                using var open = Registry.CurrentUser.CreateSubKey(Classes + @"\shell\open\command", writable: true);
                open.SetValue(null, command);
            },
            () => Registry.CurrentUser.DeleteSubKeyTree(Classes, throwOnMissingSubKey: false));
    }

    /// <summary>The scheme's open command for <paramref name="exe"/>: Windows puts the whole link in <c>%1</c>.</summary>
    internal static string Command(string exe) => $"\"{exe}\" --invite \"%1\"";

    /// <summary>
    /// Points the link scheme at <paramref name="exe"/>. With <paramref name="keepWorking"/> (the shortcut's start), a scheme whose program
    /// is still there stays as it is: the app's own copy, registered by its first start, keeps it. A profile without a key (every check
    /// that passes none) writes nothing; a registry that refuses never stops the start.
    /// </summary>
    internal static void Register(PlayerProfile profile, string exe, bool keepWorking)
    {
        if (profile.Links is not { } key) return;
        try
        {
            var have = key.Read();
            if (keepWorking && have is not null && RegisteredProgram(have) is { } registered && File.Exists(registered)) return;
            if (have == Command(exe)) return;
            key.Write(Command(exe));
            PlayerInstall.Log(profile, $"link: the invite link now opens {exe}");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { PlayerInstall.Log(profile, $"link: not registered ({exception.Message})"); }
    }

    /// <summary>The removal of the install: the link scheme goes too (nothing is left to open it).</summary>
    internal static void Unregister(PlayerProfile profile)
    {
        try { profile.Links?.Delete(); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { PlayerInstall.Log(profile, $"link: not removed ({exception.Message})"); }
    }

    /// <summary>The quoted program at the start of a registered command, or null.</summary>
    private static string? RegisteredProgram(string command) => Regex.Match(command, "^\"([^\"]+)\"") is { Success: true } found ? found.Groups[1].Value : null;

    /// <summary>
    /// The first start of the downloaded file: only this one file is copied into the player folder (<see cref="CopyName"/>), and the link scheme
    /// opens that copy. A start of the copy itself copies nothing; a copy that is running (its window is open) stays as it is. Returns the copy.
    /// </summary>
    internal static string Adopt(PlayerProfile profile, string self)
    {
        var copy = Path.Combine(profile.PlayerFolder, CopyName);
        if (!FolderMove.Same(self, copy))
        {
            Directory.CreateDirectory(profile.PlayerFolder);
            try { File.Copy(self, copy, overwrite: true); }
            catch (IOException) when (File.Exists(copy)) { PlayerInstall.Log(profile, "link: the app's copy is in use; it stays"); }
        }
        Register(profile, copy, keepWorking: false);
        return copy;
    }

    /// <summary>
    /// DIAGNOSTICS: the invite's windows and its process start as delegates (a test seam), so a check drives the question, the refusals, the
    /// setup and its failures without a window, a real profile or a started program. <see cref="Ask"/> is true for Yes.
    /// </summary>
    internal sealed record Ui(PlayerProfile Profile, Action<string> Status, Func<InviteLink.Invite, bool> Ask, Action<string> Refuse,
        Func<PlayerProblem, bool> TryAgain, Action<string, IReadOnlyList<string>> Start)
    {
        public static Ui Windows(PlayerInstallWindow.StatusWindow status, PlayerProfile profile) => new(profile, status.Show,
            invite =>
            {
                status.Hide();
                return PlayerInstallWindow.AskAdd(invite);
            },
            refusal => PlayerInstallWindow.ShowProblem(new(Product, Refused, refusal, refusal, null)),
            problem =>
            {
                status.Hide();
                return PlayerInstallWindow.AskTryAgain(problem);
            }, ServerMoved.Launch);
    }

    internal static string Connecting(string name) =>
        $"Connecting to {name}. The first time, this downloads its player tool (about 64 MB) and takes a few minutes.";

    /// <summary><c>--invite &lt;text&gt;</c>: only that one argument is read. Every failure is a dialog: the program has no console.</summary>
    internal static int Run(string? text)
    {
        try
        {
            using var status = new PlayerInstallWindow.StatusWindow(Product);
            return RunAsync(text, Ui.Windows(status, PlayerProfile.Current())).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PlayerInstallWindow.ShowProblem(new(Product, $"{Product} stopped.", "Choose Copy and send the text to the host.", PlayerProblem.Describe(exception), null));
            return 2;
        }
    }

    /// <summary>
    /// The invite in <paramref name="text"/>: refused in plain words, or asked about; nothing is written before Yes (not even the log). After
    /// Yes, <see cref="SetUpAsync"/> and the server's tool's <c>--install</c>; a failure asks Try again, with the address kept out. 0 once
    /// the setup window started, else 1.
    /// </summary>
    internal static async Task<int> RunAsync(string? text, Ui ui)
    {
        var (invite, refusal) = InviteLink.Decode(text);
        if (invite is null)
        {
            ui.Refuse(refusal!);
            return 1;
        }
        if (!ui.Ask(invite)) return 1;
        var log = ui.Profile.InstallLog;
        PlayerInstall.Log(ui.Profile, $"invite: adding {invite.Name}");
        while (true)
        {
            try
            {
                ui.Status(Connecting(invite.Name));
                var (file, arguments) = await SetUpAsync(ui.Profile.PlayerFolder, invite.Host, invite.Port, invite.Pin, invite.Name, CancellationToken.None);
                ui.Start(file, arguments);
                PlayerInstall.Log(ui.Profile, "invite: started the setup");
                return 0;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                PlayerInstall.Log(ui.Profile, "invite: " + exception.ToString().Replace(invite.Host, "the server", StringComparison.OrdinalIgnoreCase));
                if (!ui.TryAgain(PlayerMode.StartProblem(exception, invite.Name, invite.Host, log))) return 1;
            }
        }
    }

    /// <summary>
    /// What Play.cmd did (its Update-Companion and the <c>--install</c> call), from <paramref name="host"/> over the pinned
    /// <paramref name="port"/>: the manifest, then the player tool, which must match the published SHA-256 before anything is written, unpacked
    /// into <c>&lt;root&gt;\companion</c> (kept when it is that version already), and the connection file <c>&lt;root&gt;\server.json</c> with the
    /// host the manifest advises (checked as Play.cmd checked it). Returns the tool's program and its <c>--install</c> arguments.
    /// </summary>
    internal static async Task<(string File, string[] Arguments)> SetUpAsync(string root, string host, int port, string pin, string name,
        CancellationToken cancel)
    {
        var manifest = await PlayerInstall.ReadManifestAsync(host, port, pin, cancel);
        if (manifest.Host is not { } advised || !Regex.IsMatch(advised, @"^[A-Za-z0-9.-]{1,253}\z") ||
            Uri.CheckHostName(advised) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            throw new InvalidDataException("The server sent no valid address. Ask the host for a new invite link.");
        var sha = PlayerInstall.ToolSha256(manifest);
        var companion = Path.Combine(root, PlayerInstall.ToolFolder);
        var executable = Path.Combine(companion, "Amberline.AirCompanion.exe");
        if (!File.Exists(executable) || PlayerInstall.Mark(companion) != sha)
        {
            var next = companion + ".next";
            await PlayerInstall.DownloadToolAsync(host, port, pin, sha, next, cancel);
            if (!FolderMove.TryDeleteTree(companion))
            {
                FolderMove.TryDeleteTree(next);
                throw new IOException("The player tool on this PC is in use. Close it, then choose Try again.");
            }
            Directory.Move(next, companion);
            File.WriteAllText(companion + ".sha256", sha);
        }
        var connection = Path.Combine(root, "server.json");
        PlayerMode.WriteJson(connection, new { host = advised, port, certificateSha256 = pin });
        return (executable, ["--install", "--connection", connection, "--server-name", name]);
    }

    /// <summary>
    /// The downloaded file started without arguments: its copy in the player folder and the link scheme (<see cref="Adopt"/>), then the welcome
    /// window. Its Add goes on as <c>--invite</c> does; closing it ends the program. A copy that could not be made is a dialog.
    /// </summary>
    internal static int Welcome()
    {
        try
        {
            var profile = PlayerProfile.Current();
            try { Adopt(profile, Environment.ProcessPath ?? throw new IOException("Windows did not name this program's file.")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                PlayerInstallWindow.ShowProblem(new(Product, $"{Product} could not put itself in its folder.",
                    $"Check that {profile.PlayerFolder} can be written, then open {Product} again.", PlayerProblem.Describe(exception), profile.InstallLog));
                return 2;
            }
            return PlayerInstallWindow.AskInvite() is { } text ? Run(text) : 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PlayerInstallWindow.ShowProblem(new(Product, $"{Product} stopped.", "Choose Copy and send the text to the host.", PlayerProblem.Describe(exception), null));
            return 2;
        }
    }
}
