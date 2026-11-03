using System.Security.Cryptography;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// The host's one-folder install (batch 637): the release's first start copies the program, the game and the AIR client into the
/// folder the host chose, with the player installer's move code. Every folder is synthetic and temporary: the release is a stand-in
/// with a BUILD.json, the clients are the stock layout's stand-ins with their own pins, and the profile (LocalAppData, desktop) is a
/// temporary one. Nothing touches the real C:\Amberline, profile, desktop or a real client folder; the installed Platform is never
/// started (a list stands in) and no update probes a port the check did not pick.
/// </summary>
public static class HostOneFolderChecks
{
    [Check("server.host-one-folder-install", TimeoutSeconds = 180)]
    public static void TheChosenFolderHoldsTheProgramItsDataTheGameAndTheAirClient() => InstallAsync().GetAwaiter().GetResult();

    private static async Task InstallAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-one-folder");
        var realShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), HostInstall.ShortcutName);
        var realShortcutBefore = File.Exists(realShortcut);
        try
        {
            var downloads = Path.Combine(root, "downloads");
            var clients = Clients.Make(downloads, Path.Combine(downloads, "lol_air_client"));
            var (game, air, language) = (Snapshot(clients.Game), Snapshot(clients.Air), Snapshot(clients.Language));
            var release = HostInstallChecks.Release(Path.Combine(root, "zip-1.4.0"), "1.4.0");
            var folder = Path.Combine(root, "My Amberline");
            var screen = new StringWriter();
            var started = new List<string>();
            var install = Installer(root, release, folder, new(clients.Game, clients.Air, DeleteOriginals: true), screen, started, clients.Pins);
            // The start's order (HostMode): the folder is checked, then the log opens in its data\logs, then the install runs. The
            // install refused that log as "other files" (owner, 3 November 2026).
            TestAssert.Equal(null, install.Check(), "the new folder is accepted");
            HostMode.OpenLog(install.Target.Logs, DateTime.Now).Dispose();
            TestAssert.Equal(0, await install.RunAsync(), screen.ToString());

            // The layout: the program, its data, the game in the RADS-shaped path the server needs, the AIR client, the READ ME.
            TestAssert.Equal(Path.Combine(folder, "app"), install.App);
            // Batch 641: and its uninstaller.
            TestAssert.Equal("READ ME.txt, Uninstall Amberline.cmd, air, amberline-install.json, app, data, game", Entries(folder), "the install folder holds only Amberline");
            TestAssert.Equal(HostUninstall.Command, File.ReadAllText(Path.Combine(folder, HostUninstall.CommandName)), "the uninstaller starts the installed Platform");
            TestAssert.Equal(HostInstallChecks.Snapshot(release), HostInstallChecks.Snapshot(install.App), "app is the release's copy");
            var installedGame = Path.Combine(folder, @"game\projects\lol_game_client\releases\0.0.0.51");
            TestAssert.Equal(game, Snapshot(installedGame), "the game came in whole");
            TestAssert.Equal(language, Snapshot(Path.Combine(folder, @"game\projects\lol_game_client_en_us")), "and its language project beside it");
            TestAssert.Equal(air, Snapshot(Path.Combine(folder, "air")), "the AIR client came in whole");
            // One drive and the host's yes: renamed, and the AIR client's emptied folder is gone too.
            foreach (var original in new[] { clients.Game, clients.Language, clients.Air })
                TestAssert.False(Directory.Exists(original), $"the original {original} is still there");
            using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "platform.json"))))
            {
                TestAssert.True(Same(installedGame, settings.RootElement.GetProperty("dataRoot").GetString()), "platform.json names the game inside");
                TestAssert.True(Same(Path.Combine(folder, "air"), settings.RootElement.GetProperty("airRoot").GetString()), "platform.json names the AIR client inside");
            }
            var readMe = File.ReadAllText(Path.Combine(folder, HostInstall.ReadMeName));
            var pointer = Path.Combine(Local(root), "Amberline", "install.json");
            foreach (var phrase in new[] { "TO REMOVE AMBERLINE", $"Delete this folder: {folder}", $"Delete the small file {pointer}", "Amberline icon on your desktop", "Amberline sign-in" })
                TestAssert.True(readMe.Contains(phrase, StringComparison.Ordinal), $"READ ME.txt does not say <{phrase}>:\n{readMe}");
            TestAssert.True(screen.ToString().Contains($"Amberline 1.4.0 is installed in {folder}, and its icon is on your desktop.", StringComparison.Ordinal), screen.ToString());

            // Outside the folder only the icon and the pointer, which names the folder.
            TestAssert.Equal("install.json", Entries(Path.Combine(Local(root), "Amberline")), "the profile holds only the pointer");
            using (var named = JsonDocument.Parse(File.ReadAllText(pointer)))
                TestAssert.True(Same(folder, named.RootElement.GetProperty("folder").GetString()), "the pointer names the install folder");
            TestAssert.Equal(HostInstall.ShortcutName, Entries(Desktop(root)), "the desktop holds only the icon");
            var (target, arguments, workingFolder) = HostInstallChecks.ReadShortcut(install.Shortcut);
            TestAssert.True(Same(install.Installed, target) && Same(Path.Combine(folder, "app", "platform"), workingFolder) && arguments == "--home",
                $"the icon starts {target} {arguments} in {workingFolder}");
            TestAssert.Equal(install.Installed, started.Single(), "the installed copy starts");

            // The program finds its data folder from its own place; AMBERLINE_PLATFORM_HOME still wins, and a program elsewhere keeps the user folder's.
            var installedPlatform = Path.Combine(folder, "app", "platform") + Path.DirectorySeparatorChar;
            TestAssert.True(Same(Path.Combine(folder, "data"), HostPaths.Resolve(null, Local(root), installedPlatform).Data), "the data folder beside app");
            TestAssert.True(Same(Path.Combine(root, "elsewhere"), HostPaths.Resolve(Path.Combine(root, "elsewhere"), Local(root), installedPlatform).Data), "AMBERLINE_PLATFORM_HOME");
            var unpacked = Path.Combine(release, "platform") + Path.DirectorySeparatorChar;
            TestAssert.True(Same(Path.Combine(Local(root), "Amberline", "data"), HostPaths.Resolve(null, Local(root), unpacked).Data), "an unpacked zip has no install marker");
            TestAssert.Equal(null, HostInstall.ForRelease(HostPaths.Resolve(null, Local(root), installedPlatform), Local(root), Desktop(root), TextWriter.Null),
                "the installed copy runs in place");
            var later = HostInstallChecks.Release(Path.Combine(root, "zip-1.5.0"), "1.5.0");
            var next = HostInstall.ForRelease(HostPaths.Resolve(null, Local(root), Path.Combine(later, "platform")), Local(root), Desktop(root), TextWriter.Null);
            TestAssert.True(next is { AsksFolder: false } && Same(folder, next.Folder), "a later release finds the install through the pointer");
            TestAssert.True(Same(Path.Combine(folder, "data"), next!.Target.Data), "and updates it with the install's own data folder");

            // The player installer keeps a host's folders in place: it reads the install's platform.json through the pointer, and
            // it sees the install as a host installation.
            var hostFolders = PlayerInstall.HostFolders(new PlayerProfile(Local(root), Desktop(root)));
            TestAssert.True(hostFolders.Any(used => Same(used, installedGame)) && hostFolders.Any(used => Same(used, Path.Combine(folder, "air"))),
                "the player installer knows the host's folders: " + string.Join(", ", hostFolders));
            TestAssert.True(Same(folder, FolderMove.HostInstallation(installedGame)), "the game inside belongs to a host installation");
            TestAssert.Equal(realShortcutBefore, File.Exists(realShortcut), "the real desktop is untouched");
        }
        finally { HostModeChecks.Delete(root); }
    }

    [Check("server.host-one-folder-update", TimeoutSeconds = 180)]
    public static void AnUpdateIntoTheSameFolderKeepsTheDataAndTheVersionBefore() => UpdateAsync().GetAwaiter().GetResult();

    private static async Task UpdateAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-one-folder-update");
        try
        {
            var downloads = Path.Combine(root, "downloads");
            var clients = Clients.Make(downloads, Path.Combine(downloads, "lol_air_client"));
            var originals = Snapshot(downloads);
            var folder = Path.Combine(root, "Amberline");
            var screen = new StringWriter();
            var started = new List<string>();
            // The host's no: copies, every file checked, and the originals stay.
            TestAssert.Equal(0, await Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.4.0"), "1.4.0"), folder,
                new(clients.Game, clients.Air, DeleteOriginals: false), screen, started, clients.Pins).RunAsync(), screen.ToString());
            TestAssert.Equal(originals, Snapshot(downloads), "a no keeps the originals as they were");
            TestAssert.Equal(Snapshot(clients.Game), Snapshot(Path.Combine(folder, @"game\projects\lol_game_client\releases\0.0.0.51")), "the game copy");
            // Batch 653: the copy says its size and that it takes minutes.
            TestAssert.True(screen.ToString().Contains($"Copying the game and the AIR client into {folder} (", StringComparison.Ordinal) &&
                screen.ToString().Contains(" GB). This takes several minutes.", StringComparison.Ordinal), screen.ToString());

            // The host's own files in the data folder, on a port of this check's own (an update stops what answers there).
            var data = Path.Combine(folder, "data");
            var settings = Path.Combine(data, "platform.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"httpPort\": 8390", $"\"httpPort\": {FreePort()}", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(data, "notes.txt"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
            var before = Snapshot(data);
            var game = Snapshot(Path.Combine(folder, "game"));

            // A release zip later: the same folder, app.previous, the data untouched.
            var update = new StringWriter();
            var v150 = HostInstallChecks.Release(Path.Combine(root, "zip-1.5.0"), "1.5.0");
            TestAssert.Equal(0, await Installer(root, v150, folder, null, update, started, clients.Pins).RunAsync(), update.ToString());
            TestAssert.True(update.ToString().Contains("You have Amberline 1.4.0. This zip has Amberline 1.5.0.", StringComparison.Ordinal), update.ToString());
            TestAssert.Equal(new Version(1, 5, 0), HostVersion.Read(Path.Combine(folder, "app")).Version, "app holds the new version");
            TestAssert.Equal(new Version(1, 4, 0), HostVersion.Read(Path.Combine(folder, "app.previous")).Version, "app.previous holds the one before");
            TestAssert.Equal(HostInstallChecks.Snapshot(v150), HostInstallChecks.Snapshot(Path.Combine(folder, "app")), "app is the new release's copy");
            TestAssert.Equal(before, Snapshot(data), "the data folder is untouched");

            // START-HERE again, with the folders inside the install: nothing is copied or moved, and platform.json stays as it is.
            var again = new StringWriter();
            var v160 = HostInstallChecks.Release(Path.Combine(root, "zip-1.6.0"), "1.6.0");
            TestAssert.Equal(0, await Installer(root, v160, folder, new(Path.Combine(folder, @"game\projects\lol_game_client\releases\0.0.0.51"),
                Path.Combine(folder, "air"), DeleteOriginals: true), again, started, clients.Pins).RunAsync(), again.ToString());
            TestAssert.False(again.ToString().Contains("the game and the AIR client into", StringComparison.Ordinal), "nothing to bring in:\n" + again);
            // START-HERE with the original folders again: the install keeps its own copies, and the originals stay.
            var outside = new StringWriter();
            var v170 = HostInstallChecks.Release(Path.Combine(root, "zip-1.7.0"), "1.7.0");
            TestAssert.Equal(0, await Installer(root, v170, folder, new(clients.Game, clients.Air, DeleteOriginals: true), outside, started, clients.Pins).RunAsync(),
                outside.ToString());
            TestAssert.True(outside.ToString().Contains("has its own game already, so the game folder you picked stays as it is.", StringComparison.Ordinal) &&
                outside.ToString().Contains("has its own AIR client already", StringComparison.Ordinal), outside.ToString());
            TestAssert.Equal(originals, Snapshot(downloads), "the originals stay");
            TestAssert.Equal(game, Snapshot(Path.Combine(folder, "game")), "the install's game is untouched");
            TestAssert.Equal(before, Snapshot(data), "the data folder is untouched by every update");
            TestAssert.Equal(new Version(1, 6, 0), HostVersion.Read(Path.Combine(folder, "app.previous")).Version, "one step back");
            TestAssert.Equal(4, started.Count, "each run starts the installed copy");
        }
        finally { HostModeChecks.Delete(root); }
    }



    [Check("server.host-one-folder-refusals", TimeoutSeconds = 60)]
    public static void EachRefusalSaysWhyAndChangesNothing() => RefusalsAsync().GetAwaiter().GetResult();

    private static async Task RefusalsAsync()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-one-folder-refusals");
        try
        {
            var rules = new InstallRules([Path.Combine(root, "Synced")], [Path.Combine(root, "Programs")]);
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var air = Directory.CreateDirectory(Path.Combine(root, "air")).FullName;
            HostClients clients = new(game, air, DeleteOriginals: true);
            void Refused(string folder, string text)
            {
                var problem = HostInstall.FolderProblem(folder, clients, rules);
                TestAssert.True(problem?.Contains(text, StringComparison.Ordinal) == true, $"{folder}: expected <{text}>, got <{problem}>");
            }
            Refused(Path.Combine(root, "OneDrive", "Documents", "Amberline"), "OneDrive syncs that folder.");
            Refused(Path.Combine(root, "OneDrive - Example", "Amberline"), "OneDrive syncs that folder.");
            Refused(Path.Combine(root, "Synced", "Amberline"), "OneDrive syncs that folder.");
            Refused(Path.Combine(root, "Programs", "Amberline"), "Program Files needs administrator rights");
            WriteFile(Path.Combine(root, "repo", "START-HERE.cmd"), "@echo off\r\n");
            Refused(Path.Combine(root, "repo", "Amberline"), "inside the Amberline download");
            WriteFile(Path.Combine(root, "unzipped", "Start Amberline.cmd"), "@echo off\r\n");
            Refused(Path.Combine(root, "unzipped", "Amberline"), "the folder you unzipped");
            Refused(Path.GetPathRoot(root)!, "A whole drive cannot be the install folder.");
            Refused(Path.Combine(game, "Amberline"), "inside your game folder");
            Refused(Path.Combine(air, "Amberline"), "inside your AIR client folder");
            WriteFile(Path.Combine(root, "Documents", "letter.txt"), "someone's own file");
            Refused(Path.Combine(root, "Documents"), "That folder holds other files. Choose a new or empty folder.");
            Refused("Amberline", "Type the full path");
            var installed = Path.Combine(root, "installed");
            WriteFile(Path.Combine(installed, FolderMove.InstallMarker), "{}");
            WriteFile(Path.Combine(installed, "data", "platform.json"), "{}");
            TestAssert.Equal(null, HostInstall.FolderProblem(installed, clients, rules), "an Amberline install is updated in place");
            Refused(Path.Combine(installed, "inner"), "inside another Amberline install");
            TestAssert.Equal(null, HostInstall.FolderProblem(Path.Combine(root, "new", "Amberline"), clients, rules), "a new folder");
            TestAssert.Equal(null, HostInstall.FolderProblem(Path.Combine(root, "empty"), clients, rules), "an empty folder");

            // A refused folder changes nothing: no folder, no pointer, no icon.
            var screen = new StringWriter();
            var refusedFolder = Path.Combine(root, "OneDrive", "Amberline");
            var install = Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.4.0"), "1.4.0"), refusedFolder, clients, screen, [], ("", ""), rules: rules);
            TestAssert.Equal(2, await install.RunAsync(), screen.ToString());
            TestAssert.True(screen.ToString().Contains("OneDrive syncs that folder.", StringComparison.Ordinal), screen.ToString());
            TestAssert.False(Directory.Exists(refusedFolder) || Directory.Exists(Local(root)) || Directory.Exists(Desktop(root)), "a refusal wrote something");

            // The release zip on its own asks in the window until a folder is usable; Enter would keep C:\Amberline.
            var asked = new StringWriter();
            var asking = Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.5.0"), "1.5.0"), null, null, asked, [], ("", ""), rules: rules);
            TestAssert.True(asking.AsksFolder, "a PC without an Amberline asks where to install");
            var answers = new Queue<string?>([Path.Combine(root, "Programs", "Amberline"), Path.Combine(root, "chosen")]);
            TestAssert.True(asking.AskFolder(answers.Dequeue), asked.ToString());
            TestAssert.True(Same(Path.Combine(root, "chosen"), asking.Folder), "the second answer is taken");
            TestAssert.True(asked.ToString().Contains(@"Type a folder, or press Enter for C:\Amberline: ", StringComparison.Ordinal) &&
                asked.ToString().Contains("Program Files needs administrator rights", StringComparison.Ordinal), asked.ToString());
            TestAssert.False(Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.6.0"), "1.6.0"), null, null, new StringWriter(), [], ("", ""))
                .AskFolder(() => null), "no answer, no install");

            // START-HERE's request after --home.
            var request = HostMode.ParseInstall(["--install-to", @"D:\Amberline", "--game", @"D:\LoL\game", "--air", @"D:\LoL", "--delete-originals"]);
            TestAssert.True(request is (@"D:\Amberline", { Game: @"D:\LoL\game", Air: @"D:\LoL", DeleteOriginals: true }), $"{request}");
            TestAssert.True(HostMode.ParseInstall([]) is (null, null), "no request");
            TestAssert.Equal(null, HostMode.ParseInstall(["--install-to"]), "a folder is missing");
            TestAssert.Equal(null, HostMode.ParseInstall(["--bogus"]), "an unknown option");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>Synthetic stock clients: the AIR client at <c>air</c>, the game and its language project under <c>parent\live\projects</c>.</summary>
    private sealed record Clients(string Air, string Game, string Language, (string Game, string Air) Pins)
    {
        public static Clients Make(string parent, string air)
        {
            AirStockLayoutFixture.Write(air);
            var projects = Path.Combine(parent, "live", "projects");
            var game = Path.Combine(projects, "lol_game_client", "releases", "0.0.0.51");
            Directory.CreateDirectory(Path.Combine(game, "LEVELS", "Map1"));
            WriteFile(Path.Combine(game, "DATA", "CFG", "Locale.cfg"), "[General]\r\nLanguageLocaleRegion=en_US\r\n");
            File.WriteAllBytes(Path.Combine(game, "League of Legends.exe"), RandomNumberGenerator.GetBytes(64 * 1024));
            for (var index = 0; index < 10; index++) File.WriteAllBytes(Path.Combine(game, "DATA", $"file{index}.bin"), RandomNumberGenerator.GetBytes(16 * 1024));
            var language = Path.Combine(projects, "lol_game_client_en_us");
            WriteFile(Path.Combine(language, "releases", "0.0.0.20", "DATA", "Menu", "fontconfig_en_US.txt"), "stand-in");
            return new(air, game, language, (Sha256(Path.Combine(game, "League of Legends.exe")), Sha256(Path.Combine(air, "LolClient.exe"))));
        }
    }

    private static HostInstall Installer(string root, string release, string? folder, HostClients? clients, StringWriter screen, List<string> started,
        (string Game, string Air) pins, Func<string, string, bool>? sameDrive = null, InstallRules? rules = null) =>
        new(HostPaths.Resolve(null, Local(root), Path.Combine(release, "platform") + Path.DirectorySeparatorChar), Local(root), Desktop(root), screen)
        {
            Folder = folder,
            Clients = clients,
            Confirm = () => true,
            Start = platform => { started.Add(platform); return Task.FromResult(true); },
            Rules = rules ?? new([], []),
            Pins = pins,
            SameDrive = sameDrive,
        };

    private static string Local(string root) => Path.Combine(root, "profile", "Local");
    private static string Desktop(string root) => Path.Combine(root, "profile", "Desktop");

    /// <summary>Every file under a folder with its SHA-256, by relative path ("" when the folder is gone).</summary>
    private static string Snapshot(string folder) => Directory.Exists(folder) ? HostInstallChecks.Snapshot(folder) : "";

    private static string Entries(string folder) => string.Join(", ", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    private static bool Same(string expected, string? actual) =>
        actual is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
