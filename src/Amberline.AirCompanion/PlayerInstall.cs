using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Amberline.AirCompanion.FolderMove;

namespace Amberline.AirCompanion;

/// <summary>
/// A failure in the player's words: what happened, what to do, the technical details the Copy button copies, and the log
/// that holds the whole story.
/// </summary>
internal sealed record PlayerProblem(string Title, string WhatHappened, string WhatToDo, string Details, string? Log)
{
    /// <summary>The text the Copy button puts on the clipboard, for a message to the host.</summary>
    public string Report() => $"{WhatHappened}\r\n{WhatToDo}\r\n\r\n{Details}" + (Log is null ? "" : $"\r\n\r\nLog: {Log}");

    /// <summary>The type and message of an exception and of each inner one, without the stack: the log keeps the stack.</summary>
    public static string Describe(Exception exception)
    {
        var lines = new List<string>();
        for (var inner = exception; inner is not null; inner = inner.InnerException) lines.Add($"{inner.GetType().Name}: {inner.Message}");
        return string.Join("\r\n", lines);
    }
}

/// <summary>
/// The Windows user's folders the installer reads and writes, and whether it may write the per-user "Apps" entry in the registry.
/// Checks pass temporary folders and no registry, never the real profile.
/// </summary>
internal sealed record PlayerProfile(string LocalAppData, string Desktop, bool WritesRegistry = false)
{
    public static PlayerProfile Current() => new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), WritesRegistry: true);

    /// <summary>The per-user folder of the player tool: Play.cmd's download, the record and the install log (<see cref="FolderMove.PlayerFolder"/>).</summary>
    public string PlayerFolder => FolderMove.PlayerFolder(LocalAppData);

    /// <summary>The per-user folder before batch 647 (<see cref="FolderMove.OldPlayerFolder"/>): read as a fallback, never written.</summary>
    public string OldPlayerFolder => FolderMove.OldPlayerFolder(LocalAppData);

    /// <summary>The parent of that folder (<c>Amberline</c>): the removal takes it too, but only when it is empty.</summary>
    public string OldPlayerParent => Path.GetDirectoryName(OldPlayerFolder)!;

    /// <summary>The per-user record of the install: its folder and the AIR and game folders it runs.</summary>
    public string RecordPath => Path.Combine(PlayerFolder, "install.json");

    /// <summary>The installer's and the uninstaller's log, which their error dialogs name.</summary>
    public string InstallLog => Path.Combine(PlayerFolder, "logs", "install.log");

    /// <summary>The stage of the older play mode (batch 612), whose remembered folders are offered when there is no install yet.</summary>
    public string OldStage => Path.Combine(PlayerFolder, "stage", "stage.json");

    /// <summary>The settings of an Amberline server on this PC (its home-mode data folder), when it has one.</summary>
    public string HostSettings => Path.Combine(LocalAppData, "Amberline", "data", "platform.json");
}

/// <summary>The SHA-256 the two client executables must have; checks pass their fixtures' own.</summary>
internal sealed record ClientPins(string Game = RetailClients.GameExecutableSha256, string Air = RetailClients.LolClientSha256);

/// <summary>
/// NOT RETAIL: the player's installer (batch 619). Retail players installed the client with Riot's patcher. Here the player brings the
/// stock 1.0.0.126 AIR and game folders; they are moved into one install folder (a rename on the same drive; on another drive a copy,
/// a check of every file's size and SHA-256, and only then the removal of the originals), the AIR folder is pointed at the companion's
/// local endpoints, the companion is copied in, and each server gets its connection, a desktop shortcut and a copy of it in the install
/// folder. A per-user record says where the install is, so a second server's Play file only adds its own connection and shortcut.
/// </summary>
internal static class PlayerInstall
{
    internal const string GameRelease = FolderMove.GameRelease;
    internal const string ToolFolder = "companion";
    internal const string WindowlessName = "Amberline.exe";
    internal const string UninstallFile = "Uninstall.cmd";
    internal const string DefaultServerName = "Amberline";
    internal const string CannotCancel = "This step can't be canceled, or your game files could end up half moved. It takes a moment.";
    internal const string HostUsesFolder = "Your Amberline server uses this folder, so it stays where it is.";
    private const string ToolExecutable = "Amberline.AirCompanion.exe";
    private const string GameExecutable = "League of Legends.exe";
    private const string LocalPlatform = "http://127.0.0.1:8390";
    private const int MaestroPort = 8393;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // The desktop file name rule; tools/player-file.ps1 and tools/build-server.ps1 check the same.
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant);
    internal static readonly Regex ReservedName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal sealed record Record(string Install, string Air, string Game);

    /// <summary>
    /// What an install will do. <paramref name="Air"/> and <paramref name="Game"/> are the folders the install runs afterwards;
    /// <paramref name="AirCopyFrom"/> is an AIR folder a server on this PC uses, copied instead of moved; <paramref name="CopyBytes"/> is
    /// what crosses drives.
    /// </summary>
    internal sealed record Plan(string Install, string Air, string Game, IReadOnlyList<Move> Moves, string? AirCopyFrom, long CopyBytes,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// A finished install. <paramref name="Leftovers"/> are originals that could not be removed; <paramref name="Stayed"/> says what was
    /// left in a picked folder because it is not part of a client (an AIR folder that held other things too).
    /// </summary>
    internal sealed record Result(Record Record, string Server, string Shortcut, IReadOnlyList<string> Leftovers, IReadOnlyList<string> Stayed);

    internal static string ServerName(string value) => NamePattern.IsMatch(value) && !ReservedName.IsMatch(value) ? value
        : throw new ArgumentException("The server name must be 1 to 40 letters, digits, spaces, dots, hyphens or underscores, starting and ending with a letter or digit.");

    /// <summary>A server is known by its certificate pin, never by its address: a new address for the same server replaces its connection.</summary>
    internal static string ServerId(string certificateSha256) => certificateSha256[..16].ToLowerInvariant();

    /// <summary>
    /// The install record. Without one, the record in the folder from before batch 647 is read and moved over (written here, then
    /// deleted there); a move that fails still answers the old record, so the install works either way.
    /// </summary>
    internal static Record? ReadRecord(PlayerProfile profile)
    {
        var old = Path.Combine(profile.OldPlayerFolder, "install.json");
        if (File.Exists(profile.RecordPath) || ReadRecord(old) is not { } moved) return ReadRecord(profile.RecordPath);
        try
        {
            PlayerMode.WriteJson(profile.RecordPath, moved);
            File.Delete(old);
            Log(profile, $"moved the install record from {old}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return moved;
    }

    private static Record? ReadRecord(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json) is { Install: not null, Air: not null, Game: not null } record
                ? record : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The recorded install when both of its clients still pass their checks; a second server's Play file reuses it as it is.</summary>
    internal static Record? ExistingInstall(PlayerProfile profile, ClientPins pins) => ReadRecord(profile) is { } record &&
        RetailClients.AirProblem(record.Air, pins.Air) is null && RetailClients.GameProblem(record.Game, pins.Game) is null ? record : null;

    /// <summary>
    /// The AIR and game folders offered first ("We found your ... here"): the install's, then the ones the older play mode remembers, then
    /// the ones this PC's Amberline server reads. Only a folder that passes its check is offered.
    /// </summary>
    internal static (string? Air, string? Game) Found(PlayerProfile profile, ClientPins pins)
    {
        var record = ReadRecord(profile);
        var stage = ReadObject(profile.OldStage) ?? ReadObject(Path.Combine(profile.OldPlayerFolder, "stage", "stage.json"));
        var oldGame = Text(stage, "stockExecutable") is { } executable ? Path.GetDirectoryName(executable) : null;
        IReadOnlyList<string> host;
        try { host = HostFolders(profile); }
        catch (InstallRefusedException) { host = []; }
        var air = new[] { record?.Air, Text(stage, "airSource") }.Concat(host)
            .FirstOrDefault(folder => folder is not null && Directory.Exists(folder) && RetailClients.AirProblem(folder, pins.Air) is null);
        var game = new[] { record?.Game, oldGame }.Concat(host)
            .FirstOrDefault(folder => folder is not null && Directory.Exists(folder) && RetailClients.GameProblem(folder, pins.Game) is null);
        return (air, game);
    }

    /// <summary>
    /// The folders an Amberline server on this PC reads (its platform.json dataRoot and airRoot, in any case): the one in the user's
    /// folder, and the one of the one-folder install the pointer names (batch 637). They are used in place and never moved. A settings
    /// file that cannot be read stops the install: it might name a folder the installer would otherwise move.
    /// </summary>
    internal static IReadOnlyList<string> HostFolders(PlayerProfile profile)
    {
        var folders = new List<string>();
        var files = new[] { profile.HostSettings, ReadPointer(profile.LocalAppData) is { } install ? Path.Combine(install, "data", "platform.json") : null };
        foreach (var path in files.OfType<string>().Where(File.Exists))
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
                var folder = Path.GetDirectoryName(path)!;
                folders.AddRange(document.RootElement.EnumerateObject()
                    .Where(property => property.Name.Equals("dataRoot", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("airRoot", StringComparison.OrdinalIgnoreCase))
                    .Where(property => property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    .Select(property => Path.GetFullPath(property.Value.GetString()!, folder)));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new InstallRefusedException($"The settings of the Amberline server on this PC ({path}) cannot be read, so it is not known which folders it uses. Fix or remove that file, then try again.");
            }
        return folders;
    }

    /// <summary>
    /// Checks everything before anything changes and says what the install will do. Every refusal is an <see cref="InstallRefusedException"/>.
    /// </summary>
    internal static Plan MakePlan(PlayerProfile profile, string airFolder, string gameFolder, string installFolder, ClientPins pins)
    {
        var install = Full(installFolder);
        var air = Full(airFolder);
        var game = Full(gameFolder);
        if (RetailClients.AirProblem(air, pins.Air) is { } airProblem)
            throw new InstallRefusedException($"This is not the 1.0.0.126 AIR client: its executable is missing or different ({airProblem}).");
        if (RetailClients.GameProblem(game, pins.Game) is { } gameProblem)
            throw new InstallRefusedException($"This is not the 1.0.0.126 game client: the game executable is missing or different ({gameProblem}).");
        // Batch 639: an AIR folder that also holds other things (the first outside tester's held the game and the downloaded repository
        // too) gives up only the AIR client's own entries, as the host's one-folder install does (batch 637); the rest stays where it is.
        var shared = new Move("AIR client", air, Path.Combine(install, "air"), AirEntries);
        List<string> airParts = Others(shared).Count > 0 ? [.. AirEntries.Select(entry => Path.Combine(air, entry))] : [air];
        if (Inside(air, game) || airParts.Any(part => Inside(game, part)))
            throw new InstallRefusedException("The AIR client and the game client must be two separate folders, neither inside the other.");
        // Before the "already in the install" test below: an install folder that is (or is inside) a client folder is never valid.
        foreach (var (folders, label) in new (List<string> Folders, string Label)[] { (airParts, "AIR client"), ([game], "game client") })
            if (folders.Any(folder => Inside(install, folder)))
                throw new InstallRefusedException($"The install folder is inside the {label} folder. Choose an install folder somewhere else.");
        if (IsDriveRoot(install)) throw new InstallRefusedException("Choose a folder for the install, not a whole drive.");
        if (HostInstallation(install) is not null)
            throw new InstallRefusedException("The install folder is inside an Amberline server installation. Choose a folder of your own.");
        var host = HostFolders(profile);
        var running = RunningImages();
        var moves = new List<Move>();
        var notes = new List<string>();
        string? airCopyFrom = null;
        var airRun = Path.Combine(install, "air");
        if (Inside(air, install)) airRun = air;
        else if (host.Any(folder => Overlaps(folder, air)))
        {
            notes.Add("AIR client: " + HostUsesFolder + " A copy is made in the install folder.");
            NotThere(airRun, "AIR client");
            airCopyFrom = air;
        }
        else
        {
            var move = airParts.Count > 1 ? shared : new Move("AIR client", air, airRun);
            CheckMovable(move, install, running);
            NotThere(airRun, "AIR client");
            moves.Add(move);
            if (Others(move) is { Count: > 0 } others)
                notes.Add($"AIR client: {air} holds other things too. Only the AIR client's own files move; these stay: {string.Join(", ", others)}.");
        }

        var gameRun = Path.Combine(install, GameRelease);
        if (Inside(game, install)) gameRun = game;
        else if (host.Any(folder => Overlaps(folder, game)))
        {
            notes.Add("Game client: " + HostUsesFolder);
            gameRun = game;
        }
        else
        {
            CheckMovable(game, install, "game client", running);
            NotThere(gameRun, "game client");
            moves.Add(new("game client", game, gameRun));
            // The voice-over and UI text project beside the game (GameLanguagePack reads it from any ancestor of the game folder).
            if (LanguageProject(game) is { } language && !host.Any(folder => Overlaps(folder, language)))
            {
                var target = Path.Combine(install, "game", "projects", Path.GetFileName(language));
                CheckMovable(language, install, "game's language files", running);
                NotThere(target, "game's language files");
                moves.Add(new("game's language files", language, target));
            }
        }

        var copyBytes = moves.Where(move => !SameRoot(move.Source, move.Target)).Sum(CopyBytes) +
            (airCopyFrom is null ? 0 : TreeBytes(airCopyFrom, "AIR client"));
        var root = Path.GetPathRoot(install)!;
        long free;
        try { free = new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        { throw new InstallRefusedException($"Drive {root.TrimEnd('\\')} is not available. Choose another install folder."); }
        if (copyBytes > free)
            throw new InstallRefusedException($"Drive {root.TrimEnd('\\')} has {Gigabytes(free)} GB free; the copy needs {Gigabytes(copyBytes)} GB. Choose another install folder or free some space.");
        return new(install, airRun, gameRun, moves, airCopyFrom, copyBytes, notes);
    }

    /// <summary>The moves of <paramref name="plan"/>, all or none (<see cref="FolderMove.Execute"/>); the originals go once every copy is in place.</summary>
    internal static IReadOnlyList<string> Execute(Plan plan, IProgress<InstallProgress>? progress = null, MoveOptions? options = null,
        CancellationToken cancel = default) =>
        FolderMove.Execute(plan.Moves, deleteOriginals: true, progress, options, cancel);

    /// <summary>
    /// The whole install after <see cref="MakePlan"/>: the moves, the record, the AIR folder pointed at the local endpoints, the player
    /// tool, then this server's connection and shortcut. The record is written right after the moves, so a later step that fails (a
    /// running tool, say) is repaired by running the Play file again. Batch 639: <paramref name="cancel"/> works until the first
    /// original is touched; an AIR folder that held other things keeps them (and is removed only when it is left empty); the install
    /// folder gets <c>Uninstall.cmd</c> and the real profile (<see cref="PlayerProfile.WritesRegistry"/>) the per-user entry in
    /// Windows' "Apps" list; each step goes to the install log.
    /// </summary>
    internal static Result Install(PlayerProfile profile, Plan plan, AirRemoteInvitation connection, string serverName, string toolSource,
        IProgress<InstallProgress>? progress = null, MoveOptions? options = null, CancellationToken cancel = default)
    {
        var name = ServerName(serverName);
        Log(profile, $"install: {name} into {plan.Install}; {plan.Moves.Count} folder(s) to move, {Gigabytes(plan.CopyBytes)} GB to copy");
        var leftovers = Execute(plan, progress, options, cancel);
        var stayed = new List<string>();
        foreach (var move in plan.Moves.Where(move => move.Entries is not null))
            if (Others(move) is { Count: > 0 } others) stayed.Add($"These stayed in {move.Source}, because they are not part of the {move.Label}: {string.Join(", ", others)}.");
            else
                try { Directory.Delete(move.Source); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { leftovers = [.. leftovers, move.Source]; }
        var record = new Record(plan.Install, plan.Air, plan.Game);
        PlayerMode.WriteJson(profile.RecordPath, record);
        Log(profile, "install: the clients are in place" + (stayed.Count == 0 ? "" : "; " + string.Join(" ", stayed)));
        if (plan.AirCopyFrom is { } source && !Directory.Exists(plan.Air))
        {
            // A server on this PC reads that folder: it is only read here, into a copy that becomes the install's AIR folder.
            progress?.Report(new("Copying the AIR client your server uses…"));
            var partial = plan.Air + ".partial";
            TryDeleteTree(partial);
            AirOverlay.Prepare(new AirOverlayOptions(source, partial, LocalPlatform, MaestroPort));
            Directory.Move(partial, plan.Air);
        }
        else
        {
            foreach (var file in new[] { "lol.properties", "locale.properties" })
            {
                var path = Path.Combine(plan.Air, file);
                if (File.Exists(path) && !File.Exists(path + ".stock")) File.Copy(path, path + ".stock");
            }
            AirOverlay.Configure(plan.Air, AirOverlay.ParsePlatformUrl(LocalPlatform), MaestroPort);
        }
        progress?.Report(new("Installing the player tool…"));
        InstallTool(toolSource, plan.Install);
        var (server, shortcut) = AddServer(profile, record, connection, name);
        File.WriteAllText(Path.Combine(plan.Install, UninstallFile), Uninstaller, System.Text.Encoding.ASCII);
        if (profile.WritesRegistry) PlayerUninstall.WriteAppsEntry(record);
        Log(profile, $"install: done; shortcut {shortcut}" + (leftovers.Count == 0 ? "" : "; left: " + string.Join(", ", leftovers)));
        return new(record, server, shortcut, leftovers, stayed);
    }

    /// <summary>The install folder's way out: the windowless tool's removal window (it asks before it deletes anything).</summary>
    internal const string Uninstaller = "@echo off\r\nrem Removes Amberline, or one of its servers, from this PC. The window asks before it deletes anything.\r\n" +
        "start \"\" \"%~dp0" + ToolFolder + "\\" + WindowlessName + "\" --uninstall\r\n";

    /// <summary>One timestamped line in the install log; a log that cannot be written never stops the install.</summary>
    internal static void Log(PlayerProfile profile, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile.InstallLog)!);
            File.AppendAllText(profile.InstallLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\r\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The server's folder (<c>servers\&lt;id&gt;</c>: its connection, its stage for the play mode, its token cache), its desktop shortcut,
    /// named after the server and holding the game executable's own icon, and a copy of that shortcut in the install folder, so the game
    /// still starts when the desktop icon is gone. A server already known by its pin keeps its folder and its single shortcut pair: the
    /// connection and the shortcuts' names follow the new Play file.
    /// </summary>
    internal static (string Folder, string Shortcut) AddServer(PlayerProfile profile, Record record, AirRemoteInvitation connection, string name)
    {
        var id = ServerId(connection.CertificateSha25664hex);
        var folder = Path.Combine(record.Install, "servers", id);
        var stored = ReadObject(Path.Combine(folder, "server.json"));
        var previous = Text(stored, "shortcut");
        var previousCopy = Text(stored, "folderShortcut");
        var shortcut = ShortcutPath(profile, record.Install, id, name, previous);
        var copy = FolderShortcutPath(record.Install, id, shortcut);
        PlayerMode.WriteJson(Path.Combine(folder, "server.json"), new
        {
            host = connection.Host, port = connection.Port, certificateSha256 = connection.CertificateSha25664hex, name, shortcut,
            folderShortcut = copy,
        });
        PlayerMode.WriteJson(Path.Combine(folder, "stage.json"), new
        {
            remote = true, airRoot = record.Air, stockExecutable = Path.Combine(record.Game, GameExecutable), maestroPort = MaestroPort,
            clientFiles = "installed",
        });
        var tool = Path.Combine(record.Install, ToolFolder);
        var link = new ShellLink.Link(Path.Combine(tool, WindowlessName), "--shortcut " + id, tool,
            Path.Combine(record.Game, GameExecutable), 0, $"Play the game (patch 1.0.0.126) on {name}");
        // The copy is written first: a desktop that cannot take its shortcut (a missing folder, say) must not stop the copy.
        ShellLink.Write(copy, link);
        ShellLink.Write(shortcut, link);
        if (previous is not null && !Same(previous, shortcut) && IsOurShortcut(previous, id)) File.Delete(previous);
        if (previousCopy is not null && !Same(previousCopy, copy) && IsOurShortcut(previousCopy, id)) File.Delete(previousCopy);
        return (folder, shortcut);
    }

    /// <summary>The shortcut's path: the server's name on the desktop, numbered when another file or another server already has it.</summary>
    private static string ShortcutPath(PlayerProfile profile, string install, string id, string name, string? previous)
    {
        var servers = Path.Combine(install, "servers");
        var taken = Directory.Exists(servers)
            ? Directory.EnumerateDirectories(servers).Where(folder => !Path.GetFileName(folder).Equals(id, StringComparison.OrdinalIgnoreCase))
                .Select(folder => Text(ReadObject(Path.Combine(folder, "server.json")), "shortcut")).OfType<string>().ToList()
            : [];
        for (var number = 1; number < 100; number++)
        {
            var path = Path.Combine(profile.Desktop, (number == 1 ? name : $"{name} {number}") + ".lnk");
            if (taken.Any(other => Same(other, path))) continue;
            if (!File.Exists(path) || (previous is not null && Same(path, previous)) || IsOurShortcut(path, id)) return path;
        }
        throw new InstallRefusedException($"The desktop already has too many shortcuts named {name}.");
    }

    /// <summary>
    /// The install folder's copy of <paramref name="shortcut"/>, under the same file name there. Only a file of the player's own with
    /// that name moves the copy to a numbered name; the file itself is never touched.
    /// </summary>
    private static string FolderShortcutPath(string install, string id, string shortcut)
    {
        var stem = Path.GetFileNameWithoutExtension(shortcut);
        for (var number = 1; number < 100; number++)
        {
            var path = Path.Combine(install, (number == 1 ? stem : $"{stem} {number}") + ".lnk");
            if (!File.Exists(path) || IsOurShortcut(path, id)) return path;
        }
        throw new InstallRefusedException($"The install folder already has too many shortcuts named {stem}.");
    }

    internal static bool IsOurShortcut(string path, string id)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var link = ShellLink.Read(path);
            return link.Arguments == "--shortcut " + id && Path.GetFileName(link.Target).Equals(WindowlessName, StringComparison.OrdinalIgnoreCase);
        }
        // A file that is not a shell link fails to load (a COM error, mapped by its HRESULT).
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    /// <summary>
    /// Copies the player tool from <paramref name="source"/> (Play.cmd's download, or a staged update) into the install's companion folder.
    /// The running program cannot replace itself, so the copy is made beside it and switched in by two renames; a tool still running from the
    /// install folder makes the switch fail and leaves the old one in place. The version mark (<c>&lt;folder&gt;.sha256</c>) follows.
    /// </summary>
    internal static void InstallTool(string source, string install)
    {
        source = Full(source);
        var target = Path.Combine(install, ToolFolder);
        if (!Same(source, target))
        {
            if (!File.Exists(Path.Combine(source, ToolExecutable))) throw new IOException($"The player tool has no {ToolExecutable}.");
            var fresh = target + ".new";
            TryDeleteTree(fresh);
            CopyTree(source, fresh, "player tool");
            WriteWindowless(fresh);
            var old = Unused(target + ".old");
            if (Directory.Exists(target))
            {
                try { Directory.Move(target, old); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryDeleteTree(fresh);
                    throw new InstallRefusedException("The player tool in the install folder is running. Close the AIR client and the game, then try again.");
                }
            }
            try { Directory.Move(fresh, target); }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(target)) Directory.Move(old, target);
                TryDeleteTree(fresh);
                throw;
            }
            TryDeleteTree(old);
        }
        else if (!File.Exists(Path.Combine(target, WindowlessName))) WriteWindowless(target);
        var mark = source + ".sha256";
        if (File.Exists(mark) && !Same(mark, target + ".sha256")) File.Copy(mark, target + ".sha256", overwrite: true);
    }

    /// <summary>
    /// NOT RETAIL: the desktop shortcut's windowless program. The .NET apphost is the same file for a console and a windowed program: the
    /// SDK makes a WinExe by setting the PE header's Subsystem field to 2 (Windows GUI) instead of 3 (console). This writes Amberline.exe
    /// beside the companion's own apphost with only that field changed, so the shortcut starts the companion without a console window,
    /// while the friend launcher keeps running the console original (it waits for its exit code).
    /// </summary>
    internal static void WriteWindowless(string toolFolder)
    {
        var bytes = File.ReadAllBytes(Path.Combine(toolFolder, ToolExecutable));
        var header = bytes.Length >= 0x40 ? BitConverter.ToInt32(bytes, 0x3C) : -1;
        var subsystem = header + 4 + 20 + 68;
        if (header <= 0 || subsystem + 2 > bytes.Length || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z' ||
            BitConverter.ToUInt32(bytes, header) != 0x00004550 || BitConverter.ToUInt16(bytes, header + 24) is not (0x10B or 0x20B) ||
            BitConverter.ToUInt16(bytes, subsystem) is not (2 or 3))
            throw new InvalidDataException($"The player tool's {ToolExecutable} is not a Windows program.");
        bytes[subsystem] = 2;
        bytes[subsystem + 1] = 0;
        File.WriteAllBytes(Path.Combine(toolFolder, WindowlessName), bytes);
    }

    /// <summary>The server's manifest (/bootstrap/client.json), as tools/play-bootstrap.cmd reads it.</summary>
    private sealed record Manifest(int Protocol, string? Host, int Port, string? CertificateSha256, string? CompanionSha256);

    /// <summary>
    /// The shortcut's quick check, before play: the manifest over the pinned HTTPS port of the server's connection. A newer player tool
    /// is downloaded (pinned, SHA-256 checked before anything is written) and unpacked into <c>companion.next</c>, whose folder is
    /// returned; null when the install runs the server's version. A manifest that names another host is the server's own advice (the pin
    /// proves it is the same server), so the connection keeps the new host; another port or pin is refused.
    /// </summary>
    internal static async Task<string?> StageUpdateAsync(string install, string serverFolder, CancellationToken cancel)
    {
        var path = Path.Combine(serverFolder, "server.json");
        var connection = Program.ReadRemoteInvitation(path);
        using var client = AirRemoteProxy.CreatePinnedClient(Convert.FromHexString(connection.CertificateSha25664hex));
        client.Timeout = TimeSpan.FromSeconds(10);
        var origin = new UriBuilder(Uri.UriSchemeHttps, connection.Host, connection.Port).Uri;
        var manifest = JsonSerializer.Deserialize<Manifest>(await GetAsync(client, new Uri(origin, "/bootstrap/client.json"), 1 << 20, cancel), Json)
            ?? throw new InvalidDataException("The server published an empty manifest.");
        if (manifest.Protocol != 1 || manifest.Port != connection.Port ||
            !string.Equals(manifest.CertificateSha256, connection.CertificateSha25664hex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The server's manifest does not match this connection.");
        if (manifest.Host is { } advised && !string.Equals(advised, connection.Host, StringComparison.OrdinalIgnoreCase))
        {
            (connection with { Host = advised }).Validate();
            var stored = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            stored["host"] = advised;
            PlayerMode.WriteJson(path, stored);
            Console.WriteLine("update: the server advised a new address; the connection keeps it");
        }
        var sha = manifest.CompanionSha256?.ToUpperInvariant();
        if (sha is not { Length: 64 } || !sha.All(char.IsAsciiHexDigit)) throw new InvalidDataException("The server published no valid SHA-256 for the player tool.");
        var next = Path.Combine(install, ToolFolder + ".next");
        if (Mark(Path.Combine(install, ToolFolder)) == sha)
        {
            if (Directory.Exists(next) && TryDeleteTree(next)) TryDelete(next + ".sha256");
            return null;
        }
        if (Mark(next) == sha && File.Exists(Path.Combine(next, WindowlessName))) return next;
        var archive = await GetAsync(client, new Uri(origin, "/bootstrap/companion.zip"), 512 << 20, cancel);
        if (Convert.ToHexString(SHA256.HashData(archive)) != sha)
            throw new InvalidDataException("The downloaded player tool does not match the SHA-256 the server published; nothing was changed.");
        TryDelete(next + ".sha256");
        if (!TryDeleteTree(next)) throw new IOException("An earlier update of the player tool is still in use.");
        using (var zip = new MemoryStream(archive)) ZipFile.ExtractToDirectory(zip, next);
        if (!File.Exists(Path.Combine(next, ToolExecutable)))
        {
            TryDeleteTree(next);
            throw new InvalidDataException($"The player tool from the server has no {ToolExecutable}.");
        }
        WriteWindowless(next);
        File.WriteAllText(next + ".sha256", sha);
        return next;
    }

    internal static async Task<byte[]> GetAsync(HttpClient client, Uri uri, long limit, CancellationToken cancel)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"The server answered HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The server's response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("The server's response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? Mark(string folder)
    {
        try { return File.ReadAllText(folder + ".sha256").Trim().ToUpperInvariant(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal static JsonElement? ReadObject(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static string? Text(JsonElement? element, string name) =>
        element is { } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString() : null;
}
