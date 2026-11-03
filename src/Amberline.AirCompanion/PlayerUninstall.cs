using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: removing the player's install again (batch 639; retail players removed the client through its own Windows entry). Two
/// choices: one server (its desktop shortcut and its <c>servers\&lt;id&gt;</c> folder: connection, stage, token cache and logs), or all of
/// Amberline: every server, the player tool, <c>Uninstall.cmd</c>, the per-user record, Play.cmd's download and the "Apps" entry. The AIR
/// and game folders in the install folder stay unless the player asks for them too, and a game folder used in place (a server on this
/// PC reads it) is never deleted. Only the names Amberline wrote are deleted, so a file of the player's own keeps the install folder.
/// </summary>
internal static class PlayerUninstall
{
    /// <summary>The per-user entry in Windows' "Apps" list (HKCU, so no administrator is needed).</summary>
    internal const string AppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Amberline";

    internal sealed record Server(string Id, string Name, string Folder, string? Shortcut);

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
                return new Server(id, PlayerInstall.Text(json, "name") ?? id, folder, PlayerInstall.Text(json, "shortcut"));
            })
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// "Remove this server": the folder is first renamed aside, which fails while its shortcut is running (its log is open), so nothing is
    /// half deleted; then the shortcut (only while it is still Amberline's link for this server) and the folder go.
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

    /// <summary>The AIR and game folders inside the install folder: what "also delete the game files" adds.</summary>
    internal static IReadOnlyList<string> GameFolders(PlayerInstall.Record record) =>
        new[] { Path.Combine(record.Install, "air"), Path.Combine(record.Install, "game") }.Where(Directory.Exists).ToList();

    /// <summary>
    /// "Remove Amberline from this PC". Refused while a program runs from the install folder (the League client, the game or another
    /// shortcut), before anything changes. <paramref name="runningTool"/> is this program's folder: it cannot delete itself, so it is
    /// returned in <see cref="Removal.AfterExit"/> with the folders that hold it.
    /// </summary>
    internal static Removal RemoveAll(PlayerProfile profile, PlayerInstall.Record record, bool deleteGame, string? runningTool = null)
    {
        var install = record.Install;
        if (FolderMove.RunningImages(Environment.ProcessId).FirstOrDefault(image => FolderMove.Inside(image, install)) is { } program)
            throw new InstallRefusedException($"{Path.GetFileName(program)} is running from {install}. Close the game client and the game, then try again.");
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
