using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Platform.Folders;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>Release versions: <c>version</c> in a release's <c>app\BUILD.json</c> (x.y.z, batch 625) and release tags such as <c>v1.5.0</c>.</summary>
public static class HostVersion
{
    /// <summary>x.y.z with an optional leading v; anything else (a pre-release tag, two or four parts) is null.</summary>
    public static Version? Parse(string? text)
    {
        if (text is ['v' or 'V', .. var rest]) text = rest;
        return text is not null && text.Count(c => c == '.') == 2 && text.All(c => char.IsAsciiDigit(c) || c == '.') &&
            Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>The version and build time of a release's <c>app</c> folder; no version for a development build or a damaged file.</summary>
    public static (Version? Version, DateTimeOffset? Built) Read(string appFolder)
    {
        try
        {
            using var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(appFolder, "BUILD.json")));
            var root = build.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            var version = root.TryGetProperty("version", out var text) && text.ValueKind == JsonValueKind.String ? Parse(text.GetString()) : null;
            DateTimeOffset? built = root.TryGetProperty("builtAtUtc", out var at) && at.ValueKind == JsonValueKind.String &&
                at.TryGetDateTimeOffset(out var time) ? time : null;
            return (version, built);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (null, null); }
    }
}

/// <summary>The game and AIR client folders a one-folder install brings in (null: none), and whether their originals go.</summary>
public sealed record HostClients(string? Game, string? Air, bool DeleteOriginals);

/// <summary>
/// The picked game and AIR client brought into a one-folder install, with the player installer's move code (<see cref="FolderMove"/>):
/// the game's release folder and its language project beside it go to <c>game\projects\...</c>, and only the AIR client's own files
/// (<see cref="FolderMove.AirEntries"/>, never anything else of its folder) to <c>air</c>. A picked folder inside the install is used
/// where it is, and one the install has its own copy of stays as it is. With the host's yes the originals go: a rename on one drive;
/// on another drive, or with a no, a copy whose every file is checked first. All or none. Folders a player install on this PC runs
/// from are copied, never deleted. Shared by START-HERE's install (batch 637) and the browser's folder step (batch 642).
/// </summary>
internal sealed record ClientPlan(IReadOnlyList<FolderMove.Move> Moves, IReadOnlyList<string> Notes, string? Game, string? Air, bool Delete, long Bytes)
{
    public const string Unchanged = " Nothing was moved, and your folders are as they were.";

    /// <summary>
    /// The moves into <paramref name="folder"/>; a broken safety rule is an <see cref="InstallRefusedException"/> that says why.
    /// Nothing is changed either way. <paramref name="playerRecords"/> are the player installs' records (batch 619).
    /// </summary>
    public static ClientPlan Make(string folder, HostClients clients, IEnumerable<string> playerRecords, (string Game, string Air) pins,
        Func<string, string, bool>? sameDrive)
    {
        var moves = new List<FolderMove.Move>();
        var notes = new List<string>();
        string? game = null, air = null;
        if (clients.Game is { } pickedGame)
        {
            var target = Path.Combine(folder, FolderMove.GameRelease);
            if (FolderMove.Inside(pickedGame, folder)) game = FolderMove.Full(pickedGame);
            else if (Directory.Exists(target))
            {
                game = target;
                notes.Add($"{folder} has its own game already, so the game folder you picked stays as it is.");
            }
            else
            {
                if (RetailClients.GameProblem(pickedGame, pins.Game) is { } wrong) throw new InstallRefusedException($"Amberline cannot bring in that game folder: {wrong}.");
                var source = FolderMove.Full(pickedGame);
                moves.Add(new("game", source, target));
                if (FolderMove.LanguageProject(source) is { } language)
                {
                    var languageTarget = Path.Combine(folder, "game", "projects", Path.GetFileName(language));
                    FolderMove.NotThere(languageTarget, "game's language files");
                    moves.Add(new("game's language files", language, languageTarget));
                }
                game = target;
            }
        }
        if (clients.Air is { } pickedAir)
        {
            var target = Path.Combine(folder, "air");
            if (FolderMove.Inside(pickedAir, folder)) air = FolderMove.Full(pickedAir);
            else if (Directory.Exists(target))
            {
                air = target;
                notes.Add($"{folder} has its own AIR client already, so the AIR client folder you picked stays as it is.");
            }
            else
            {
                if (RetailClients.AirProblem(pickedAir, pins.Air) is { } wrong) throw new InstallRefusedException($"Amberline cannot bring in that AIR client folder: {wrong}.");
                moves.Add(new("AIR client", FolderMove.Full(pickedAir), target, FolderMove.AirEntries));
                air = target;
            }
        }

        var delete = clients.DeleteOriginals;
        var used = playerRecords.Select(HostFolders.ReadRecord).SelectMany(record => new[] { record.Game, record.Air }).OfType<string>().ToList();
        if (delete && moves.Any(move => used.Any(folderUsed => FolderMove.Overlaps(folderUsed, move.Source))))
        {
            delete = false;
            notes.Add("A player install on this PC runs from the folders you picked, so they stay where they are, and Amberline uses copies.");
        }
        if (delete)
        {
            var running = FolderMove.RunningImages();
            foreach (var move in moves) FolderMove.CheckMovable(move, folder, running);
        }
        var bytes = moves.Where(move => !delete || !(sameDrive ?? FolderMove.SameRoot)(move.Source, move.Target)).Sum(FolderMove.CopyBytes);
        var drive = Path.GetPathRoot(folder)!;
        if (bytes > new DriveInfo(drive).AvailableFreeSpace)
            throw new InstallRefusedException($"Drive {drive.TrimEnd('\\')} has not enough free space: the game and the AIR client need {FolderMove.Gigabytes(bytes)} GB.");
        return new(moves, notes, game, air, delete, bytes);
    }

    /// <summary>
    /// After the moves: the lines for the host (originals that could not be removed, what stayed in an AIR folder that holds other
    /// things, the notes), the AIR client's emptied folder removed, and <paramref name="settings"/> naming the folders inside.
    /// </summary>
    public IReadOnlyList<string> Finish(IReadOnlyList<string> leftovers, string settings)
    {
        var lines = leftovers.Select(leftover => $"Amberline could not delete {leftover}. You can delete it yourself.").ToList();
        // An AIR client folder that held other things (the first friend's: the game and the Amberline download too) keeps them.
        foreach (var move in Moves.Where(move => Delete && move.Entries is not null))
            if (FolderMove.Others(move) is { Count: > 0 } others)
                lines.Add($"These stayed in {move.Source}, because they are not part of the {move.Label}: {string.Join(", ", others)}.");
            else
                try { Directory.Delete(move.Source); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Install, empty folder: {ex.Message}"); }
        lines.AddRange(Notes);
        HostMode.Change(settings, saved => saved with { DataRoot = Game ?? saved.DataRoot, AirRoot = Air ?? saved.AirRoot, ServerExecutable = null! });
        return lines;
    }
}

/// <summary>
/// The folders an install folder must stay out of: OneDrive's (they sync every file) and Program Files (it needs administrator
/// rights, and Amberline needs none). DIAGNOSTICS: checks pass synthetic ones.
/// </summary>
public sealed record InstallRules(IReadOnlyList<string> OneDrive, IReadOnlyList<string> ProgramFiles)
{
    public static InstallRules ThisPc() => new(
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable).OfType<string>().Where(Path.IsPathFullyQualified).ToList(),
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") }.OfType<string>().Where(Path.IsPathFullyQualified).ToList());
}

/// <summary>
/// NOT RETAIL: the home host's self-install and update in place (retail had no server program on players' PCs). A start from an
/// unpacked release copies its <c>app</c> folder into the install folder and puts an Amberline icon on the desktop, so the zip can
/// be deleted and the firewall rules name stable paths. Batch 637: the install folder is one the host chose (<see cref="Folder"/>,
/// <c>C:\Amberline</c> unless they choose another), holding <c>app</c>, <c>app.previous</c>, <c>data</c>, the game and the AIR
/// client, plus a READ ME; outside it only the icon and a pointer (<c>%LOCALAPPDATA%\Amberline\install.json</c>) that lets a later
/// release find it. Installs from before batch 637 (<c>%LOCALAPPDATA%\Amberline\app</c>, no folder) are updated where they are.
/// A newer release asks the host first, then stops the running Amberline with its own confirmed Stop (signed in with the host's
/// key), keeps the installed folder as <c>app.previous</c> (one step back) and starts the new one; a start that fails puts
/// <c>app.previous</c> back. The installer writes the data folder only to name the game and AIR client it brought in.
/// </summary>
public sealed class HostInstall(HostPaths paths, string localAppData, string desktop, TextWriter screen)
{
    public const string ShortcutName = "Amberline.lnk";
    /// <summary>The install folder offered first (owner, 2 November 2026): it needs no administrator rights and is outside OneDrive.</summary>
    public const string DefaultFolder = @"C:\Amberline";
    public const string ReadMeName = "READ ME.txt";
    // 120 tries of half a second: an old Amberline that just stopped may take a while to end its matches and release its folder.
    private const int Tries = 120;
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(500);

    public static string PlatformFile => OperatingSystem.IsWindows() ? "Amberline.Platform.exe" : "Amberline.Platform";

    /// <summary>The <c>app</c> folder this program runs from.</summary>
    public string Release { get; } = Path.GetFullPath(Path.Combine(paths.App, ".."));

    /// <summary>The one-folder install this release goes into; null for an install from before batch 637, in the user's folder.</summary>
    public string? Folder { get; set; }

    /// <summary>The game and AIR client to bring into <see cref="Folder"/> (START-HERE passes them); null brings none.</summary>
    public HostClients? Clients { get; init; }

    public string App => Folder is null ? Path.GetFullPath(Path.Combine(localAppData, "Amberline", "app")) : Path.Combine(Folder, "app");
    public string Previous => App + ".previous";
    private string Staging => App + ".new";
    public string Shortcut { get; } = Path.Combine(desktop, ShortcutName);
    public string Installed => Path.Combine(App, "platform", PlatformFile);

    /// <summary>The installed Amberline's folders: its data folder is the install's own, or the release's (<see cref="HostPaths.Resolve"/>).</summary>
    public HostPaths Target => Folder is null ? paths with { App = Path.Combine(App, "platform") } : new(Path.Combine(Folder, "data"), Path.Combine(App, "platform"));

    /// <summary>A start from the zip on a PC without an Amberline: the console asks where to install (<see cref="AskFolder"/>).</summary>
    public bool AsksFolder => Folder is null && !Directory.Exists(App);

    // DIAGNOSTICS: the steps and facts a check replaces, so it needs no console, real start, real client or second drive.
    /// <summary>The host's yes to an update: Enter in the window.</summary>
    public Func<bool> Confirm { get; init; } = () => !Console.IsInputRedirected && Console.ReadLine() is not null;
    /// <summary>Starts an installed Platform; true once its page answers, false when it ended first. Null: in this window.</summary>
    public Func<string, Task<bool>>? Start { get; init; }
    public InstallRules Rules { get; init; } = InstallRules.ThisPc();
    /// <summary>The SHA-256 of the stock League of Legends.exe and LolClient.exe.</summary>
    public (string Game, string Air) Pins { get; init; } = (RetailClients.GameExecutableSha256, RetailClients.LolClientSha256);
    /// <summary>Replaces the drive test of the client moves (a check forces the copy path on one drive).</summary>
    public Func<string, string, bool>? SameDrive { get; init; }

    /// <summary>
    /// The installer when this program runs from an unpacked release (its <c>app\BUILD.json</c> names a version) other than an
    /// installed one; null for an installed copy and for a development build, which run in place. Its folder: the one asked for
    /// (START-HERE's <c>--install-to</c>), else the one the pointer names, else none (the user's folder, or a question).
    /// </summary>
    public static HostInstall? ForRelease(HostPaths paths, string localAppData, string desktop, TextWriter screen, string? folder = null,
        HostClients? clients = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var install = new HostInstall(paths, localAppData, desktop, screen) { Clients = clients };
        if (HostVersion.Read(install.Release).Version is null || HostPaths.InstallFolder(paths.App) is not null ||
            string.Equals(install.Release, install.App, StringComparison.OrdinalIgnoreCase)) return null;
        install.Folder = folder ?? FolderMove.ReadPointer(localAppData);
        return install;
    }

    /// <summary>
    /// The release zip on its own, on a PC without an Amberline: asks in the window where to install it (Enter keeps
    /// <c>C:\Amberline</c>) until the answer is a folder Amberline can use. False when the window has no more input.
    /// </summary>
    public bool AskFolder(Func<string?> read)
    {
        while (true)
        {
            screen.WriteLine("Where should Amberline be installed? It keeps everything in that folder.");
            screen.Write($"Type a folder, or press Enter for {DefaultFolder}: ");
            if (read() is not { } answer) return false;
            var folder = string.IsNullOrWhiteSpace(answer) ? DefaultFolder : answer.Trim().Trim('"');
            if (FolderProblem(folder, Clients, Rules) is { } problem) { screen.WriteLine(problem); continue; }
            Folder = FolderMove.Full(folder);
            return true;
        }
    }

    /// <summary>Why <see cref="Folder"/> cannot be the install folder, in one plain sentence; null when it can (it is then a full path).</summary>
    public string? Check()
    {
        if (Folder is null) return null;
        if (FolderProblem(Folder, Clients, Rules) is { } problem) return problem;
        _checked = Folder = FolderMove.Full(Folder);
        return null;
    }

    // The folder Check passed. The start (HostMode) checks it, then opens its log in the folder's data\logs before RunAsync runs, so
    // RunAsync checking it again found that log and refused the host's new folder as "already holds other files" (owner, 3 November 2026).
    private string? _checked;

    /// <summary>
    /// The install folder's rules: a full path; not a drive root, Program Files or a OneDrive folder; not inside the Amberline download
    /// or an unzipped release (a folder with START-HERE.cmd or Start Amberline.cmd), inside another install, or inside the game or
    /// AIR client folder; and new, empty or an Amberline install already (so deleting it never deletes anything else).
    /// </summary>
    public static string? FolderProblem(string folder, HostClients? clients, InstallRules rules)
    {
        const string other = " Choose another folder, for example C:\\Amberline.";
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(folder.Trim().Trim('"'))) return "Type the full path, for example C:\\Amberline.";
            full = FolderMove.Full(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Windows cannot use that folder path." + other; }
        if (FolderMove.IsDriveRoot(full)) return "A whole drive cannot be the install folder." + other;
        if (rules.ProgramFiles.Any(programs => FolderMove.Inside(full, programs)))
            return "Program Files needs administrator rights." + other;
        if (rules.OneDrive.Any(synced => FolderMove.Inside(full, synced)) ||
            full.Split('\\').Any(part => part.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || part.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase)))
            return "OneDrive syncs that folder." + other;
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "START-HERE.cmd")) || File.Exists(Path.Combine(directory.FullName, "Start Amberline.cmd")))
                return $"That folder is inside the Amberline download or the folder you unzipped ({directory.FullName})." + other;
            if (directory.FullName != full && File.Exists(Path.Combine(directory.FullName, FolderMove.InstallMarker)))
                return $"That folder is inside another Amberline install ({directory.FullName})." + other;
        }
        foreach (var (source, label) in new[] { (clients?.Game, "game"), (clients?.Air, "AIR client") })
            if (source is not null && FolderMove.Inside(full, source)) return $"That folder is inside your {label} folder." + other;
        if (Directory.Exists(full) && !File.Exists(Path.Combine(full, FolderMove.InstallMarker)) && Directory.EnumerateFileSystemEntries(full).Any())
            return "That folder holds other files. Choose a new or empty folder.";
        return null;
    }

    /// <summary>Installs, updates or starts the installed Amberline; the exit code of the window (0 started, 1 nothing done, 2 failed).</summary>
    public async Task<int> RunAsync()
    {
        var offered = HostVersion.Read(Release).Version ?? throw new InvalidDataException("This release names no version.");
        if (Folder != _checked && Check() is { } refused)
        {
            screen.WriteLine(refused);
            return 2;
        }
        var had = Directory.Exists(App);
        var installed = had ? HostVersion.Read(App).Version : null;
        try
        {
            if (Folder is not null) PrepareFolder();
            if (installed is not null && installed >= offered)
            {
                CreateShortcut();
                if (installed > offered)
                {
                    screen.WriteLine($"You already have a newer Amberline ({installed}). This zip has {offered}, so nothing changed. " +
                        "Start Amberline from its desktop icon.");
                    return 1;
                }
                screen.WriteLine($"Amberline {offered} is already installed. Starting it.");
                return await StartInstalledAsync() ? 0 : 2;
            }
            if (had)
            {
                screen.WriteLine(installed is null ? $"This zip has Amberline {offered}. The Amberline on this PC has no version number."
                    : $"You have Amberline {installed}. This zip has Amberline {offered}.");
                screen.WriteLine("Updating ends any game in progress. Your settings and friends stay. To keep the one you have, close this window.");
                screen.WriteLine(ContinueLine);
                if (!Confirm()) return 1;
            }
            var where = Folder ?? "your user folder";
            screen.WriteLine($"Copying Amberline {offered} into {where}. This takes up to a minute.");
            await DeleteTreeAsync(Staging);
            CopyTree(Release, Staging);
            // The game and the AIR client come in after the running Amberline (which may read them) has stopped. A first install has
            // no Amberline of its own to stop (whatever answers on the port then is another one, which the new start reports).
            if (((had ? await StopRunningAsync() : null) ?? BringClients()) is { } problem)
            {
                await DeleteTreeAsync(Staging);
                screen.WriteLine(problem);
                return 2;
            }
            if (had)
            {
                await DeleteTreeAsync(Previous);
                await MoveAsync(App, Previous);
            }
            await MoveAsync(Staging, App);
            CreateShortcut();
            screen.WriteLine($"Amberline {offered} is installed in {where}, and its icon is on your desktop. You can delete the zip and the unzipped folder.");
            if (await StartInstalledAsync()) return 0;
            if (!had) { screen.WriteLine("Amberline did not start. Its window says why."); return 2; }
            screen.WriteLine($"Amberline {offered} did not start, so the Amberline you had comes back.");
            await DeleteTreeAsync(App);
            await MoveAsync(Previous, App);
            return await StartInstalledAsync() ? 0 : 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Install: {ex}");
            try
            {
                if (had && !Directory.Exists(App) && Directory.Exists(Previous)) Directory.Move(Previous, App);
                if (Directory.Exists(Staging)) Directory.Delete(Staging, recursive: true);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Install: {again.Message}"); }
            screen.WriteLine("Amberline could not copy or replace its files. Free some disk space and close every Amberline window, " +
                "then double-click Start Amberline again.");
            return 2;
        }
    }

    /// <summary>
    /// Copies a folder by reading and writing each file, so the download's Mark of the Web (the <c>Zone.Identifier</c> stream that
    /// <c>File.Copy</c> would copy along) stays behind and later starts from the icon raise no download warning.
    /// </summary>
    public static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(output);
        }
    }

    /// <summary>
    /// The install folder's own files, made first, so a later run finds even a half-finished install: the marker by which the installed
    /// program finds its data folder beside it, the READ ME (what the folder is, how to remove Amberline) and the pointer.
    /// </summary>
    private void PrepareFolder()
    {
        var folder = Folder!;
        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, FolderMove.InstallMarker);
        if (!File.Exists(marker))
            File.WriteAllText(marker, "{ \"about\": \"This folder is an Amberline install; Amberline finds its data folder through this file.\" }\r\n");
        File.WriteAllText(Path.Combine(folder, ReadMeName), ReadMe(folder, FolderMove.PointerPath(localAppData)));
        File.WriteAllText(Path.Combine(folder, HostUninstall.CommandName), HostUninstall.Command);
        FolderMove.WritePointer(localAppData, folder);
    }

    /// <summary>The install folder's READ ME: what is in it, and the four things to delete to remove Amberline.</summary>
    public static string ReadMe(string folder, string pointer) => $"""
        AMBERLINE
        =========

        This folder is Amberline on this PC:
        - app            the program
        - app.previous   the version before the last update
        - data           settings, logins, the certificate and the logs
        - game           your copy of the game (when Amberline copied it in)
        - air            your copy of the AIR client (when Amberline copied it in)
        - {HostUninstall.CommandName}   removes Amberline (it asks first)

        Start Amberline from the Amberline icon on your desktop. Keep this folder where it is.

        TO REMOVE AMBERLINE
        On Amberline's page choose Settings, then Uninstall Amberline. Or, with Amberline closed, double-click
        {HostUninstall.CommandName} here. Both ask first.

        By hand:
        1. Close the Amberline window.
        2. Delete this folder: {folder}
        3. Delete the Amberline icon on your desktop.
        4. Delete the small file {pointer}.
        5. If you let Amberline through Windows Firewall, delete its rules "{HostFirewall.SignInRule}" and "{HostFirewall.GamesRule}"
           (Windows Defender Firewall, Advanced settings, Inbound Rules).

        """.ReplaceLineEndings("\r\n");

    /// <summary>Writes each progress line to the window as it comes (Progress&lt;T&gt; would post them out of order).</summary>
    private sealed class Lines(TextWriter screen) : IProgress<string>
    {
        public void Report(string value) => screen.WriteLine(value);
    }

    /// <summary>
    /// START-HERE's game and AIR client into the install (<see cref="ClientPlan"/>), then <c>platform.json</c> names the folders inside.
    /// Null when done (or nothing to do); else why nothing changed.
    /// </summary>
    private string? BringClients()
    {
        if (Folder is not { } folder || Clients is not { } clients || (clients.Game is null && clients.Air is null)) return null;
        try
        {
            var plan = ClientPlan.Make(folder, clients, FolderMove.PlayerRecords(localAppData), Pins, SameDrive);
            IReadOnlyList<string> leftovers = [];
            if (plan.Moves.Count > 0)
            {
                // Batch 653: a copy says its size and that it takes minutes (its lines follow at every 10%); a rename is instant.
                screen.WriteLine(plan.Bytes > 0
                    ? $"Copying the game and the AIR client into {folder} ({FolderMove.Gigabytes(plan.Bytes)} GB). This takes several minutes."
                    : $"Moving the game and the AIR client into {folder}.");
                leftovers = FolderMove.Execute(plan.Moves, plan.Delete, new Lines(screen), new(SameDrive));
            }
            foreach (var line in plan.Finish(leftovers, Target.Settings)) screen.WriteLine(line);
            return null;
        }
        catch (InstallRefusedException refused) { return refused.Message + ClientPlan.Unchanged; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Install, game and AIR client: {ex}");
            return "Amberline could not copy the game and the AIR client: the disk may be full, or a program uses their files." + ClientPlan.Unchanged;
        }
    }

    /// <summary>The installed Platform: <see cref="Start"/>, or in this window once its page answers.</summary>
    private Task<bool> StartInstalledAsync()
    {
        screen.WriteLine(StartingLine);
        return (Start ?? (platform => StartInWindowAsync(platform, HostMode.ReadPort(Target.Settings))))(Installed);
    }

    /// <summary>Batch 653: the word list's pause, and the line while the installed Amberline starts until its page answers.</summary>
    public const string ContinueLine = "Press Enter to continue.";
    public const string StartingLine = "Starting Amberline and opening its page in your browser. This takes a few seconds.";

    /// <summary>
    /// Stops the Amberline answering on this data folder's port, or says why it cannot. It uses the page's own Stop: a launch code
    /// signed with this user's <c>page.key</c> gives a session at <c>/open</c>; <c>GET /host/stop</c> is the confirmation form (the
    /// dashboard's, or <see cref="MapSetupStop"/> in setup mode), and posting it back with its token and the number of games it
    /// warned about stops the loop (the window has asked the host already). Then it waits until the old Amberline has let go.
    /// Null when nothing runs any more.
    /// </summary>
    private async Task<string?> StopRunningAsync()
    {
        var port = HostMode.ReadPort(Target.Settings);
        switch (await HostMode.ProbeAsync(port))
        {
            case null: return null;
            case false: return $"Another program uses a port Amberline needs ({port}), so Amberline cannot update. (A-NET-01)";
        }
        const string foreign = "This PC runs an Amberline that belongs to another Windows user, so this one cannot update it. (A-RUN-02)";
        const string closeIt = "The running Amberline did not stop. Close its window, then double-click Start Amberline again.";
        var keyFile = HostSessions.KeyFile(Target.Data);
        // A missing or damaged key means the running Amberline is not this user's; reading it then would write a new one.
        if (!File.Exists(keyFile) || new FileInfo(keyFile).Length != 32) return foreign;
        screen.WriteLine("Stopping the running Amberline. This takes up to a minute.");
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using (var open = await client.GetAsync($"/open?c={new HostSessions(keyFile).Code(DateTimeOffset.UtcNow)}"))
                if (open.StatusCode != HttpStatusCode.Redirect) return open.StatusCode == HttpStatusCode.Forbidden ? foreign : closeIt;
            string page;
            // An Amberline from before this version has no Stop page (404): the host closes its window.
            using (var ask = await client.GetAsync(StopPath))
            {
                if (!ask.IsSuccessStatusCode) return closeIt;
                page = await ask.Content.ReadAsStringAsync();
            }
            var token = TokenField.Match(page);
            var games = GamesField.Match(page);
            if (!token.Success || !games.Success) return closeIt;
            // A game that started since the form was made gets 409: the host then decides on the dashboard.
            using (var stop = await client.PostAsync(StopPath, new FormUrlEncodedContent(new Dictionary<string, string>
                   { [token.Groups[1].Value] = WebUtility.HtmlDecode(token.Groups[2].Value), ["games"] = games.Groups[1].Value })))
                if (!stop.IsSuccessStatusCode) return closeIt;
            await WaitForStopAsync(port);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Install, stop request: {ex.Message}");
            return closeIt;
        }
    }

    public const string StopPath = HostDashboard.PagePath + "/stop";
    private static readonly Regex TokenField = new("<input type=hidden name=\"([^\"]+)\" value=\"([^\"]+)\">", RegexOptions.CultureInvariant);
    private static readonly Regex GamesField = new(@"<input type=hidden name=games value=(\d+)>", RegexOptions.CultureInvariant);

    /// <summary>
    /// Setup mode's Stop: the dashboard's two routes (<see cref="HostDashboard"/>, full mode only) for the mode without games.
    /// <c>GET /host/stop</c> asks; its form posts back with the antiforgery token, and the loop stops after the answer. Both are host
    /// routes (<see cref="HostGate"/>), so only the host's session reaches them, and that comes only from the host's <c>page.key</c>.
    /// </summary>
    public static void MapSetupStop(WebApplication app, HostLoop loop)
    {
        app.MapGet(StopPath, (HttpContext context, IAntiforgery antiforgery) => HostMode.Page(
            "<h1>Stop Amberline?</h1><p>Friends cannot log in until you start Amberline again.</p>" +
            $"<form method=post action={StopPath}{HostPages.Wait("Stopping Amberline.")}>{Program.RegistrationToken(context, antiforgery)}<input type=hidden name=games value=0>" +
            $"<div class=buttons><button type=submit>Stop Amberline</button> <a class=\"button secondary\" href={HostSteps.Start}>Cancel</a></div></form>"));
        app.MapPost(StopPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return HostMode.Page($"<h1>The page expired</h1><p><a href={StopPath}>Choose again.</a></p>", StatusCodes.Status400BadRequest);
            context.Response.OnCompleted(() => { loop.Stop(); return Task.CompletedTask; });
            return HostMode.Page("<h1>Amberline has stopped</h1><p>To start it again, double-click Amberline on your desktop.</p>");
        });
    }

    /// <summary>Waits, at most a minute, until the old page's port is closed and its account store (<c>accounts.lock</c>) is free.</summary>
    private async Task WaitForStopAsync(int port)
    {
        for (var tries = 1; await HostMode.ProbeAsync(port) is not null || StoreHeld(); tries++)
        {
            if (tries >= Tries) throw new TimeoutException("The running Amberline did not end.");
            await Task.Delay(Retry);
        }
    }

    /// <summary>Whether a Platform still holds the account store's lease (opened without sharing); opening it changes nothing.</summary>
    private bool StoreHeld()
    {
        try
        {
            using var lease = new FileStream(Path.Combine(Target.Data, "accounts.lock"), FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
    }

    /// <summary>The installed Platform in this same window (the zip's window becomes Amberline's window).</summary>
    private static async Task<bool> StartInWindowAsync(string platform, int port)
    {
        Process process;
        try { process = Process.Start(new ProcessStartInfo(platform, "--home") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(platform) })!; }
        catch (Win32Exception ex) { Console.Error.WriteLine($"Install, start: {ex.Message}"); return false; }
        using (process)
        {
            // ponytail: no time limit; a start that neither answers nor ends keeps this window waiting until the host closes it.
            while (true)
            {
                if (await HostMode.ProbeAsync(port) == true) return true;
                if (process.HasExited) return false;
                await Task.Delay(Retry);
            }
        }
    }

    /// <summary>The desktop's Amberline icon: the installed Platform with <c>--home</c>, in its own folder (WScript.Shell through COM).</summary>
    private void CreateShortcut()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Directory.CreateDirectory(desktop);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            try
            {
                var link = shell.CreateShortcut(Shortcut);
                link.TargetPath = Installed;
                link.Arguments = "--home";
                link.WorkingDirectory = Path.GetDirectoryName(Installed);
                link.Description = "Starts Amberline";
                link.Save();
                Marshal.FinalReleaseComObject(link);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Install, desktop icon: {ex.Message}");
            screen.WriteLine($"Amberline could not put its icon on your desktop. Start it from {Installed}.");
        }
    }

    /// <summary>Moves a folder, waiting while a program that just ended still holds its files.</summary>
    private static async Task MoveAsync(string from, string to)
    {
        for (var tries = 1; ; tries++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && tries < Tries) { await Task.Delay(Retry); }
        }
    }

    private static async Task DeleteTreeAsync(string path)
    {
        for (var tries = 1; Directory.Exists(path); tries++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && tries < Tries) { await Task.Delay(Retry); }
        }
    }
}
