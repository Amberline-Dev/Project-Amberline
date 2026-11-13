using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: removing the player's install again (batch 639; retail players removed the client through its own Windows entry). Two
/// choices: one server (its two shortcuts, the desktop one and the copy in the install folder, and its <c>servers\&lt;id&gt;</c> folder:
/// connection, stage, token cache and logs), or all of
/// Amberline: every server, the player tool, <c>Uninstall.cmd</c>, the per-user record, Play.cmd's download and the "Apps" entry. The AIR
/// and game folders in the install folder stay unless the player asks for them too, and a game folder used in place (a server on this
/// PC reads it) is never deleted. The 2011 launcher's folder goes with the game folders (its files are game files, batch 761), and the classic
/// layout under the RADS root goes in every case. Only the names Amberline wrote are deleted, so a file of the player's own keeps the install folder.
/// </summary>
internal static class PlayerUninstall
{
    /// <summary>The per-user entry in Windows' "Apps" list (HKCU, so no administrator is needed).</summary>
    internal const string AppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Amberline";

    internal sealed record Server(string Id, string Name, string Folder, string? Shortcut, string? FolderShortcut);

    /// <summary>
    /// What a removal kept (the clients, unless they were asked for), what could not be deleted, and the folders left to delete once
    /// this program has exited (the running tool's own; <see cref="DeleteAfterExit"/>).
    /// </summary>
    internal sealed record Removal(IReadOnlyList<string> Kept, IReadOnlyList<string> Left, IReadOnlyList<string> AfterExit);

    /// <summary>The install to remove: the per-user record's, else the one this tool sits in (its parent holds <c>servers</c>).</summary>
    internal static PlayerInstall.Record? Installed(PlayerProfile profile, string tool)
    {
        if (PlayerInstall.ReadRecord(profile) is { } record) return record;
        var install = Path.GetDirectoryName(tool);
        return install is not null && Directory.Exists(Path.Combine(install, "servers"))
            ? new(install, Path.Combine(install, "air"), Path.Combine(install, PlayerInstall.GameRelease)) : null;
    }

    /// <summary>The servers of an install, by name.</summary>
    internal static IReadOnlyList<Server> Servers(string install)
    {
        var servers = Path.Combine(install, "servers");
        if (!Directory.Exists(servers)) return [];
        return Directory.EnumerateDirectories(servers).Where(folder => Regex.IsMatch(Path.GetFileName(folder), "^[0-9a-f]{16}$"))
            .Select(folder =>
            {
                var json = PlayerInstall.ReadObject(Path.Combine(folder, "server.json"));
                var id = Path.GetFileName(folder);
                return new Server(id, PlayerInstall.Text(json, "name") ?? id, folder, PlayerInstall.Text(json, "shortcut"),
                    PlayerInstall.Text(json, "folderShortcut"));
            })
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// "Remove this server": the folder is first renamed aside, which fails while its shortcut is running (its log is open), so nothing is
    /// half deleted; then its two shortcuts (each only while it is still Amberline's link for this server) and the folder go.
    /// </summary>
    internal static void RemoveServer(PlayerProfile profile, Server server)
    {
        var aside = server.Folder + ".removing";
        if (Directory.Exists(server.Folder))
        {
            try { Directory.Move(server.Folder, aside); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InstallRefusedException($"{server.Name} is still running. Close the game client and the game, then try again.");
            }
        }
        if (server.Shortcut is { } shortcut && PlayerInstall.IsOurShortcut(shortcut, server.Id)) File.Delete(shortcut);
        if (server.FolderShortcut is { } copy && PlayerInstall.IsOurShortcut(copy, server.Id)) File.Delete(copy);
        FolderMove.TryDeleteTree(aside);
        PlayerInstall.Log(profile, $"uninstall: removed the server {server.Name}");
    }

    /// <summary>The bytes of every file under <paramref name="folder"/> (0 when it is missing); links are not followed.</summary>
    internal static long Size(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(file => file.Length);
    }

    /// <summary>
    /// The AIR, game and 2011 launcher folders inside the install folder: what "also delete the game files" adds. NOT RETAIL (classic start): the
    /// launcher's files are game files too, since the installer moved the player's own folder in (<see cref="PlayerInstall.LauncherPlan"/>), so they stay
    /// unless the game files are asked for.
    /// </summary>
    internal static IReadOnlyList<string> GameFolders(PlayerInstall.Record record) =>
        new[] { Path.Combine(record.Install, "air"), Path.Combine(record.Install, "game"), Path.Combine(record.Install, PlayerInstall.LauncherFolder) }
            .Where(Directory.Exists).ToList();

    /// <summary>
    /// "Remove Amberline from this PC". Refused while a program runs from the install folder (the League client, the game or another
    /// shortcut), before anything changes. <paramref name="runningTool"/> is this program's folder: it cannot delete itself, so it is
    /// returned in <see cref="Removal.AfterExit"/> with the folders that hold it. NOT RETAIL (classic start): <paramref name="radsRoot"/> is the
    /// RADS root the classic start uses (<see cref="GameManagedLayout.ResolveRoot"/>), or null for none; its stamped layout goes first, always. The AIR,
    /// game and 2011 launcher folders are game files: they stay unless the game files are asked for (<see cref="GameFolders"/>).
    /// </summary>
    internal static Removal RemoveAll(PlayerProfile profile, PlayerInstall.Record record, bool deleteGame, string? runningTool = null, string? radsRoot = null)
    {
        var install = record.Install;
        if (FolderMove.RunningImages(Environment.ProcessId).FirstOrDefault(image => FolderMove.Inside(image, install)) is { } program)
            throw new InstallRefusedException($"{Path.GetFileName(program)} is running from {install}. Close the game client and the game, then try again.");
        // NOT RETAIL (classic start): with the game files asked for, the 2011 launcher's folder is removed too, so a link in it is refused before anything
        // changes (see RemoveLauncher). Kept, the folder is never walked.
        var launcher = Path.Combine(install, PlayerInstall.LauncherFolder);
        if (deleteGame && Directory.Exists(launcher) && FirstLink(launcher) is { } linked) throw new InstallRefusedException(LinkRefusal(linked));
        foreach (var server in Servers(install)) RemoveServer(profile, server);

        var kept = new List<string>();
        var paths = new List<string>();
        foreach (var name in new[] { PlayerInstall.ToolFolder, PlayerInstall.ToolFolder + ".next", PlayerInstall.ToolFolder + ".new", "servers" })
            paths.AddRange([Path.Combine(install, name), Path.Combine(install, name + ".sha256")]);
        if (Directory.Exists(install)) paths.AddRange(Directory.GetDirectories(install, PlayerInstall.ToolFolder + ".old*"));
        paths.Add(Path.Combine(install, PlayerInstall.UninstallFile));
        if (deleteGame) paths.AddRange(GameFolders(record));
        else
        {
            // The AIR client stays as it came: the installer kept its stock properties beside the ones that point at the player tool.
            foreach (var file in new[] { "lol.properties", "locale.properties" })
            {
                var path = Path.Combine(record.Air, file);
                if (File.Exists(path + ".stock")) File.Move(path + ".stock", path, overwrite: true);
            }
            kept.AddRange(GameFolders(record));
        }
        if (!FolderMove.Inside(record.Game, install)) kept.Add(record.Game);
        // The per-user files, in the player's folder and in the one from before batch 647: Play.cmd's download and connection, the
        // record, the logs; and the play mode's font cache. Only these names: nothing of the host's (its pointer, its data) is touched.
        foreach (var player in new[] { profile.PlayerFolder, profile.OldPlayerFolder })
            paths.AddRange([Path.Combine(player, PlayerInstall.ToolFolder), Path.Combine(player, PlayerInstall.ToolFolder + ".sha256"),
                Path.Combine(player, "server.json"), Path.Combine(player, "install.json"), Path.Combine(player, "logs")]);
        paths.Add(Path.Combine(profile.LocalAppData, "AmberlineAir", "fonts"));

        var left = new List<string>();
        var afterExit = new List<string>();
        // NOT RETAIL (classic start): the layout goes before the install folder, since its junctions point into it (Remove takes links as links only).
        if (radsRoot is not null)
        {
            try { ClassicLauncherLayout.Remove(radsRoot, line => PlayerInstall.Log(profile, line)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                PlayerInstall.Log(profile, $"uninstall: the 2011 launcher's layout: {exception.Message}");
            }
            if (ClassicLauncherLayout.Remaining(radsRoot) is { } rest) left.Add(rest);
        }
        foreach (var path in paths)
        {
            if (runningTool is not null && FolderMove.Inside(runningTool, path)) afterExit.Add(path);
            else if (File.Exists(path)) { try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { left.Add(path); } }
            else if (!FolderMove.TryDeleteTree(path)) left.Add(path);
        }
        // Batch 649: the folder from before batch 647, then its Amberline parent: each goes only when it is empty (RemoveIfEmpty never recurses).
        foreach (var folder in new[] { install, profile.PlayerFolder, profile.OldPlayerFolder, profile.OldPlayerParent, Path.Combine(profile.LocalAppData, "AmberlineAir") })
            RemoveIfEmpty(folder);
        if (profile.WritesRegistry) DeleteAppsEntry(install);
        return new(kept, left, afterExit);
    }

    /// <summary>
    /// NOT RETAIL (classic start, design §2): "Remove the 2011 launcher", for a player who wants the usual start for good. The classic layout under
    /// <paramref name="root"/> goes first (what its stamp lists; junctions as links), then <c>&lt;install&gt;\launcher</c>, the launcher's own files that
    /// the installer moved in. A link in that folder is refused before anything changes: a link is never followed. A file still in use keeps the layout,
    /// its stamp and the folder, and is refused with a sentence, so the next choice finishes the job. With nothing left, a second call removes nothing.
    /// <paramref name="log"/> receives the layout's own lines.
    /// </summary>
    internal static IReadOnlyList<string> RemoveLauncher(PlayerInstall.Record record, string? root, Action<string>? log = null)
    {
        var folder = Path.Combine(record.Install, PlayerInstall.LauncherFolder);
        if (Directory.Exists(folder) && FirstLink(folder) is { } linked) throw new InstallRefusedException(LinkRefusal(linked));
        var removed = new List<string>();
        var stuck = false;
        try
        {
            if (root is not null)
            {
                removed.AddRange(ClassicLauncherLayout.Remove(root, log ?? (_ => { })));
                stuck = ClassicLauncherLayout.Remaining(root) is not null;
            }
            if (!stuck && Directory.Exists(folder))
            {
                stuck = !FolderMove.TryDeleteTree(folder);
                if (!stuck) removed.Add(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { stuck = true; }
        if (stuck) throw new InstallRefusedException(InUse);
        return removed;
    }

    /// <summary>The refusal for a file that cannot go yet (a program has it open); the layout and the folder stay, so the next choice finishes.</summary>
    private const string InUse = "The 2011 launcher is still in use. Close it, then choose Remove the 2011 launcher again.";

    /// <summary>The refusal for a link in the 2011 launcher's folder: the folder is removed without following links, so the player deletes it.</summary>
    private static string LinkRefusal(string link) =>
        $"{link} is a link. The 2011 launcher's files are removed without following links: delete the link yourself, then try again.";

    /// <summary>The first link (a junction or a symbolic link) at or inside <paramref name="folder"/>, or null. A folder is searched only when it is not a link.</summary>
    private static string? FirstLink(string folder)
    {
        if ((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0) return folder;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return entry.FullName;
            if (entry is DirectoryInfo && FirstLink(entry.FullName) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>
    /// Deletes <paramref name="folders"/> once process <paramref name="processId"/> has exited (0: at once), then each of
    /// <paramref name="emptyParents"/> that is empty. A hidden 64-bit Windows PowerShell does it, retrying for 30 seconds while a file
    /// is still in use; the paths travel inside its encoded command, so no quoting rule of cmd applies to them.
    /// </summary>
    internal static Process DeleteAfterExit(int processId, IReadOnlyList<string> folders, IReadOnlyList<string> emptyParents)
    {
        static string List(IEnumerable<string> paths) => "@(" + string.Join(",", paths.Select(path => "'" + path.Replace("'", "''") + "'")) + ")";
        var script = new StringBuilder("$ErrorActionPreference = 'SilentlyContinue'\n");
        if (processId != 0) script.Append($"Wait-Process -Id {processId} -Timeout 120\n");
        script.Append($"$folders = {List(folders)}\n")
            .Append("foreach ($try in 1..60) {\n")
            .Append("  foreach ($folder in $folders) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force } }\n")
            .Append("  if (-not ($folders | Where-Object { Test-Path -LiteralPath $_ })) { break }\n")
            .Append("  Start-Sleep -Milliseconds 500\n}\n")
            .Append($"foreach ($parent in {List(emptyParents)}) {{ if ((Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force)) {{ Remove-Item -LiteralPath $parent -Force }} }}\n");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString())) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Windows PowerShell did not start.");
    }

    /// <summary>The values of the "Apps" entry: the windowless tool's removal window, the game's icon, and no Modify or Repair.</summary>
    internal static IReadOnlyDictionary<string, object> AppsEntry(PlayerInstall.Record record) => new Dictionary<string, object>
    {
        ["DisplayName"] = PlayerInstall.DefaultServerName,
        ["Publisher"] = PlayerInstall.DefaultServerName,
        ["DisplayIcon"] = Path.Combine(record.Game, "League of Legends.exe") + ",0",
        ["InstallLocation"] = record.Install,
        ["UninstallString"] = $"\"{Path.Combine(record.Install, PlayerInstall.ToolFolder, PlayerInstall.WindowlessName)}\" --uninstall",
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
    };

    /// <summary>Writes the "Apps" entry; a registry that refuses it never fails the install (Uninstall.cmd still works).</summary>
    internal static void WriteAppsEntry(PlayerInstall.Record record)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppsKey, writable: true);
            foreach (var (name, value) in AppsEntry(record))
                key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    /// <summary>Deletes the "Apps" entry when it names this install (another install's entry is left alone).</summary>
    private static void DeleteAppsEntry(string install)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(AppsKey))
                if (key?.GetValue("InstallLocation") is not string location || !FolderMove.Inside(location, install) || !FolderMove.Inside(install, location))
                    return;
            Registry.CurrentUser.DeleteSubKeyTree(AppsKey, throwOnMissingSubKey: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    private static void RemoveIfEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
