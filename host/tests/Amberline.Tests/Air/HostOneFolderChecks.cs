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
            // Batch 857: then the folder is made and locked (HostMode calls LockNew before it opens the log).
            TestAssert.Equal(null, install.LockNew(), "the new folder is made and locked");
            HostMode.OpenLog(install.Target.Logs, DateTime.Now).Dispose();
            TestAssert.Equal(0, await install.RunAsync(), screen.ToString());

            // The layout: the program, its data, the game in the RADS-shaped path the server needs, the AIR client, the READ ME.
            TestAssert.Equal(Path.Combine(folder, "app"), install.App);
            // Batch 641: and its uninstaller.
            TestAssert.Equal($"READ ME.txt, Uninstall Amberline.cmd, air, {FolderMove.InstallMarker}, app, data, game", Entries(folder), "the install folder holds only Amberline");
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
            var pointer = Path.Combine(Local(root), FolderMove.HostName, "install.json");
            foreach (var phrase in new[] { "TO REMOVE AMBERLINE", $"Delete this folder: {folder}", $"Delete the small file {pointer}", "Amberline icon on your desktop", HostFirewall.SignInRule })
                TestAssert.True(readMe.Contains(phrase, StringComparison.Ordinal), $"READ ME.txt does not say <{phrase}>:\n{readMe}");
            TestAssert.True(screen.ToString().Contains($"Amberline 1.4.0 is installed in {folder}, and its icon is on your desktop.", StringComparison.Ordinal), screen.ToString());

            // Outside the folder only the icon and the pointer, which names the folder.
            TestAssert.Equal("install.json", Entries(Path.Combine(Local(root), FolderMove.HostName)), "the profile holds only the pointer");
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
            TestAssert.True(Same(Path.Combine(Local(root), FolderMove.HostName, "data"), HostPaths.Resolve(null, Local(root), unpacked).Data), "an unpacked zip has no install marker");
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
            Refused(Path.Combine(root, "Documents"), "That folder already contains other files. Choose an empty or new folder.");
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
            TestAssert.True(asked.ToString().Contains($"Type a folder, or press Enter for {HostInstall.DefaultFolder}: ", StringComparison.Ordinal) &&
                asked.ToString().Contains("Program Files needs administrator rights", StringComparison.Ordinal), asked.ToString());
            TestAssert.False(Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.6.0"), "1.6.0"), null, null, new StringWriter(), [], ("", ""))
                .AskFolder(() => null), "no answer, no install");

            // START-HERE's request after --home.
            var request = HostMode.ParseInstall(["--install-to", @"D:\Amberline", "--game", @"E:\Games\game", "--air", @"E:\Games", "--delete-originals"]);
            TestAssert.True(request is (@"D:\Amberline", { Game: @"E:\Games\game", Air: @"E:\Games", DeleteOriginals: true }, false), $"{request}");
            TestAssert.True(HostMode.ParseInstall([]) is (null, null, false), "no request");
            // Batch 666: only Update now passes the page's confirmation.
            TestAssert.True(HostMode.ParseInstall([HostInstall.ConfirmedOption]) is (null, null, true), "Update now's start");
            TestAssert.Equal(null, HostMode.ParseInstall(["--install-to"]), "a folder is missing");
            TestAssert.Equal(null, HostMode.ParseInstall(["--bogus"]), "an unknown option");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// NOT RETAIL (batch 765): <c>--client-zip</c>, for a host on a command line, instead of <c>--game</c> and <c>--air</c> (never with them): the
    /// game and the AIR client are unpacked beside the install folder and moved in, platform.json names them, no staging folder is left and the zip
    /// is byte-identical. The zip is a small synthetic one (<see cref="ClientZipChecks.Bundle"/>).
    /// </summary>
    [Check("server.host-one-folder-client-zip", TimeoutSeconds = 180)]
    public static void TheClientZipComesInThroughItsOption() => ClientZipAsync().GetAwaiter().GetResult();

    private static async Task ClientZipAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-one-folder-zip");
        try
        {
            var request = HostMode.ParseInstall(["--install-to", @"D:\Amberline", "--client-zip", @"E:\Downloads\bundle.zip"]);
            TestAssert.True(request is (@"D:\Amberline", { Game: null, Air: null, Zip: @"E:\Downloads\bundle.zip" }, false), $"{request}");
            TestAssert.Equal(null, HostMode.ParseInstall(["--client-zip", "bundle.zip", "--game", @"E:\game"]), "with --game");
            TestAssert.Equal(null, HostMode.ParseInstall(["--air", @"E:\air", "--client-zip", "bundle.zip"]), "with --air");
            TestAssert.Equal(null, HostMode.ParseInstall(["--client-zip"]), "the zip is missing");

            var clients = Clients.Make(Path.Combine(root, "downloads"), Path.Combine(root, "downloads", "lol_air_client"));
            var zip = ClientZipChecks.Bundle(Path.Combine(root, "client bundle.zip"), "any top folder/", clients.Game, [clients.Language], clients.Air, null);
            var zipSha = Sha256(zip);
            var folder = Path.Combine(root, "My Amberline");
            var screen = new StringWriter();
            var install = Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.4.0"), "1.4.0"), folder, new(null, null, DeleteOriginals: false, Zip: zip),
                screen, [], clients.Pins);
            TestAssert.Equal(null, install.Check(), "the new folder is accepted");
            TestAssert.Equal(0, await install.RunAsync(), screen.ToString());

            var installedGame = Path.Combine(folder, @"game\projects\lol_game_client\releases\0.0.0.51");
            TestAssert.Equal($"READ ME.txt, Uninstall Amberline.cmd, air, {FolderMove.InstallMarker}, app, data, game", Entries(folder), "the install folder holds only Amberline");
            TestAssert.Equal(Snapshot(clients.Game), Snapshot(installedGame), "the game came in from the zip");
            TestAssert.Equal(Snapshot(clients.Language), Snapshot(Path.Combine(folder, @"game\projects\lol_game_client_en_us")), "and its language project");
            TestAssert.Equal(Snapshot(clients.Air), Snapshot(Path.Combine(folder, "air")), "the AIR client came in from the zip");
            using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "platform.json"))))
            {
                TestAssert.True(Same(installedGame, settings.RootElement.GetProperty("dataRoot").GetString()), "platform.json names the game inside");
                TestAssert.True(Same(Path.Combine(folder, "air"), settings.RootElement.GetProperty("airRoot").GetString()), "platform.json names the AIR client inside");
            }
            TestAssert.False(Directory.Exists(folder + ".client-zip"), "no staging folder is left");
            TestAssert.Equal(zipSha, Sha256(zip), "the zip is byte-identical");
            var lines = screen.ToString();
            foreach (var phrase in new[] { $"Unpacking the game and the AIR client from the client zip into {folder}.", "Unpacking the client zip: ", $"Moving the game and the AIR client into {folder}." })
                TestAssert.True(lines.Contains(phrase, StringComparison.Ordinal), $"the window says <{phrase}>: {lines}");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// NOT RETAIL (batch 765, round 2; owner, 13 November 2026: "Beside the install"): with <c>--client-zip</c>, the zip's 2011 launcher goes into
    /// <c>&lt;install&gt;-launcher</c>, outside the installation, and platform.json's launcherRoot names it once it passes the launcher row's check.
    /// A host's own working launcher stays, a folder of that name Amberline did not make is never touched, one it made from an earlier zip is
    /// replaced, and a launcher that fails its check never fails the install. The zip with the two folders has a sentence of its own.
    /// </summary>
    [Check("server.host-one-folder-client-zip-launcher", TimeoutSeconds = 240)]
    public static void TheClientZipsLauncherGoesBesideTheInstall() => ZipLauncherAsync().GetAwaiter().GetResult();

    private static async Task ZipLauncherAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-one-folder-zip-launcher");
        try
        {
            TestAssert.Equal("Give either the client zip (--client-zip) or the game and AIR client folders (--game and --air), not both.",
                HostMode.OptionsProblem(["--install-to", @"D:\Amberline", "--client-zip", "bundle.zip", "--air", @"E:\air"]), "the zip with a folder");
            TestAssert.Equal("Amberline does not know these options. Start it from its desktop icon or Start Amberline.cmd.", HostMode.OptionsProblem(["--bogus"]),
                "an unknown option");

            var clients = Clients.Make(Path.Combine(root, "downloads"), Path.Combine(root, "downloads", "lol_air_client"));
            var launcher = Path.Combine(root, "downloads", "launcher");
            var launcherSha = ClassicLauncherChecks.WriteLauncherFiles(launcher);
            WriteFile(Path.Combine(launcher, "releasemanifest"), "file list");
            var zip = ClientZipChecks.Bundle(Path.Combine(root, "client bundle.zip"), "", clients.Game, [clients.Language], clients.Air, launcher);
            var zipSha = Sha256(zip);
            var release = HostInstallChecks.Release(Path.Combine(root, "zip-1.4.0"), "1.4.0");
            async Task<(string Folder, string Beside, string Screen)> InstallAsync(string name, string sha, Action<string>? before = null)
            {
                var folder = Path.Combine(root, name);
                before?.Invoke(folder);
                var screen = new StringWriter();
                TestAssert.Equal(0, await Installer(root, release, folder, new(null, null, false, zip), screen, [], clients.Pins, launcherSha256: sha).RunAsync(),
                    screen.ToString());
                TestAssert.Equal(Snapshot(clients.Game), Snapshot(Path.Combine(folder, @"game\projects\lol_game_client\releases\0.0.0.51")), name + ": the game came in");
                TestAssert.False(Directory.Exists(folder + ".client-zip") || Directory.Exists(Path.Combine(folder, "launcher")), name + ": no staging folder, no launcher inside");
                return (folder, folder + "-launcher", screen.ToString());
            }
            void HasLauncher(string folder, string what)
            {
                foreach (var file in ClassicLauncherFiles.Required.Append("releasemanifest").Append("amberline-client-zip.txt"))
                    TestAssert.True(File.Exists(Path.Combine(folder, file)), $"{what}: {file} beside the install");
                // Batch 857: the folder beside the install is locked as the install is (protected, and every file private).
                TestAssert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected && InstallLockChecks.FirstOpen(folder) is null,
                    $"{what}: the launcher beside the install is locked ({InstallLockChecks.FirstOpen(folder)})");
            }

            // A new install: the launcher is beside it, whole, and platform.json names it.
            var (fresh, beside, screen) = await InstallAsync("Fresh", launcherSha);
            HasLauncher(beside, "a new install");
            TestAssert.True(Same(beside, LauncherRoot(fresh)), "platform.json names the launcher beside the install: " + LauncherRoot(fresh));
            TestAssert.True(screen.Contains($"The client zip's 2011 launcher is in {beside}, beside the install folder.", StringComparison.Ordinal), screen);

            // The host's own working launcher stays, and nothing is put beside the install.
            var (own, ownBeside, _) = await InstallAsync("Own", launcherSha, folder =>
            {
                WriteFile(Path.Combine(folder, FolderMove.InstallMarker), "{}");
                WriteFile(Path.Combine(folder, "data", "platform.json"), JsonSerializer.Serialize(new { launcherRoot = launcher }));
            });
            TestAssert.True(Same(launcher, LauncherRoot(own)) && !Directory.Exists(ownBeside), "the host's own launcher stays: " + LauncherRoot(own));

            // A folder of that name Amberline did not make is never touched; the game and the AIR client still come in.
            var (foreign, foreignBeside, foreignScreen) = await InstallAsync("Foreign", launcherSha, folder => WriteFile(Path.Combine(folder + "-launcher", "mine.txt"), "the host's own"));
            TestAssert.Equal("mine.txt", Entries(foreignBeside), "the folder in the way is untouched");
            TestAssert.Equal(null, LauncherRoot(foreign), "no launcher is named");
            TestAssert.True(foreignScreen.Contains($"{foreignBeside} already contains other files, so Amberline did not put the client zip's 2011 launcher there. " +
                "Your 2011 launcher setting is not changed.", StringComparison.Ordinal), foreignScreen);

            // A launcher that fails its check never fails the install, and leaves nothing beside it.
            var (failing, failingBeside, failingScreen) = await InstallAsync("Failing", new string('0', 64));
            TestAssert.True(LauncherRoot(failing) is null && !Directory.Exists(failingBeside), "a failing launcher is not kept");
            TestAssert.True(failingScreen.Contains("The client zip's 2011 launcher did not pass its check. Your 2011 launcher setting is not changed.", StringComparison.Ordinal),
                failingScreen);

            // One Amberline made from an earlier zip (its marker) is replaced whole.
            var (again, againBeside, _) = await InstallAsync("Again", launcherSha, folder =>
            {
                WriteFile(Path.Combine(folder + "-launcher", "amberline-client-zip.txt"), "made earlier");
                WriteFile(Path.Combine(folder + "-launcher", "old.txt"), "from an earlier zip");
            });
            HasLauncher(againBeside, "a replaced launcher");
            TestAssert.True(!File.Exists(Path.Combine(againBeside, "old.txt")) && Same(againBeside, LauncherRoot(again)), "the earlier launcher folder was replaced");
            TestAssert.Equal(zipSha, Sha256(zip), "the zip is byte-identical");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// Batch 857 (design steps 1-3, 6): the host's install folder is locked to the installing account. A folder of another account (a stand-in SID),
    /// beside the install too, or a link is refused; a new folder is made and locked before its first write; the game and the AIR client keep the
    /// drive's permissions when they are moved in, and take the install's; the start makes a full pass and records it; an update's swap drops the
    /// record; Status says either.
    /// </summary>
    [Check("server.host-one-folder-locked", TimeoutSeconds = 180)]
    public static void TheInstallFolderIsLockedToTheInstallingAccount() => LockedAsync().GetAwaiter().GetResult();

    private static async Task LockedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-one-folder-lock");
        try
        {
            var drive = InstallLockChecks.Hazard(Path.Combine(root, "drive"));
            var downloads = Path.Combine(drive, "downloads");
            var clients = Clients.Make(downloads, Path.Combine(downloads, "lol_air_client"));
            var folder = Path.Combine(drive, "Amberline");
            const string Other = " Choose another folder, for example C:\\Amberline.";
            var someone = new InstallRules([], [], InstallLockChecks.Someone);
            Directory.CreateDirectory(folder);
            TestAssert.Equal($"{folder} belongs to another Windows account." + Other, HostInstall.FolderProblem(folder, null, someone), "another account's folder");
            Directory.Delete(folder);
            foreach (var beside in new[] { folder + ".client-zip", folder + "-launcher" })
            {
                Directory.CreateDirectory(beside);
                TestAssert.Equal($"{beside} belongs to another Windows account." + Other, HostInstall.FolderProblem(folder, null, someone), "another account's folder beside it");
                Directory.Delete(beside);
            }
            HostLoginCopyChecks.Junction(folder, downloads);
            TestAssert.Equal($"{folder} is a link to another place." + Other, HostInstall.FolderProblem(folder, null, new([], [])), "a junction");
            Directory.Delete(folder);

            // The start's order (HostMode): the folder is checked, made and locked, and only then the log opens in its data\logs.
            var screen = new StringWriter();
            var started = new List<string>();
            var install = Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.4.0"), "1.4.0"), folder,
                new(clients.Game, clients.Air, DeleteOriginals: true), screen, started, clients.Pins);
            TestAssert.Equal(null, install.Check(), "the new folder is accepted");
            TestAssert.Equal(null, install.LockNew(), "made and locked");
            TestAssert.True(Directory.Exists(folder) && !Directory.Exists(Path.Combine(folder, "data")), "nothing written in it yet");
            TestAssert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected && CopyPlaces.Private(folder), "locked before its first write");
            HostMode.OpenLog(install.Target.Logs, DateTime.Now).Dispose();
            TestAssert.Equal(0, await install.RunAsync(), screen.ToString());
            TestAssert.Equal(null, InstallLockChecks.FirstOpen(folder), "the game and the AIR client moved in took the install's lock");

            // The installed Platform's start: a full pass, recorded; an update's swap drops the record, so the next start walks it all again.
            var paths = install.Target;
            var loop = new HostLoop(paths, _ => { }, TextWriter.Null) { LockCheck = () => InstallLock.Check(folder, paths.LockRecord, Local(root), root) };
            loop.CheckLock();
            TestAssert.Equal<(bool, string)?>((true, InstallLock.LockedLine), loop.LockLine, "the start: locked");
            TestAssert.True(File.Exists(paths.LockRecord), "the full pass is recorded");
            // An update stops what answers on the install's port: this check's own free one, never the line's default.
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.Settings))!.AsObject();
            saved["httpPort"] = FreePort();
            File.WriteAllText(paths.Settings, saved.ToJsonString());
            var update = new StringWriter();
            TestAssert.Equal(0, await Installer(root, HostInstallChecks.Release(Path.Combine(root, "zip-1.5.0"), "1.5.0"), folder, null, update, started, clients.Pins)
                .RunAsync(), update.ToString());
            TestAssert.True(!File.Exists(paths.LockRecord) && InstallLockChecks.FirstOpen(folder) is null, "the swap: still private, and the record went");

            // Review HIGH 2 and its follow-up: the start also checks the 2011 launcher folder beside the install. Without the client zip's mark it is
            // reported and never used; with it, it is locked. Review LOW 9: a link beside an install refuses a new install, never an update.
            var launcherBeside = folder + "-launcher";
            Directory.CreateDirectory(launcherBeside);
            File.WriteAllText(Path.Combine(launcherBeside, "LoLLauncher.exe"), "not Amberline's");
            TestAssert.Equal($"{launcherBeside} was not put there by Amberline, so Amberline does not use it", HostMode.LockInstall(folder, paths, Local(root), root),
                "a launcher folder without the mark");
            TestAssert.Equal(null, HostMode.UsableLauncher(launcherBeside, paths), "and it is never shared or started");
            File.WriteAllText(Path.Combine(launcherBeside, Amberline.AirCompanion.ClientZip.Marker), "Amberline put the 2011 launcher here");
            TestAssert.Equal(null, HostMode.LockInstall(folder, paths, Local(root), root), "the marked launcher folder: locked");
            TestAssert.True(new DirectoryInfo(launcherBeside).GetAccessControl().AreAccessRulesProtected && InstallLockChecks.FirstOpen(launcherBeside) is null, "the launcher folder is private");
            FolderMove.TryDeleteTree(launcherBeside);
            HostLoginCopyChecks.Junction(launcherBeside, drive);
            TestAssert.Equal(null, HostInstall.FolderProblem(folder, null, new([], [])), "an update never stops on the folder launcherBeside it");
            var newFolder = Path.Combine(drive, "New Amberline");
            HostLoginCopyChecks.Junction(newFolder + "-launcher", drive);
            TestAssert.Equal($"{newFolder}-launcher is a link to another place." + Other, HostInstall.FolderProblem(newFolder, null, new([], [])),
                "a new install refuses a link launcherBeside it");
            Directory.Delete(newFolder + "-launcher");
            Directory.Delete(launcherBeside);

            // Status: one line, ok or waiting, never bad (a folder that cannot be locked has nothing to do with players joining); none by default.
            var settings = new PlatformSettings(clients.Game, paths.ServerExecutable);
            async Task<string> LineAsync(HostLoop shown)
            {
                await using var dashboard = new HostDashboard(shown, settings, () => [], () => []);
                return string.Join(" | ", dashboard.Status(DateTimeOffset.Now).Checks.Where(check => check.Id == "install").Select(check => $"{check.State}: {check.Text}"));
            }
            TestAssert.Equal($"ok: {InstallLock.LockedLine}", await LineAsync(loop), "locked");
            loop.LockCheck = () => $"{folder} belongs to another Windows account";
            loop.CheckLock();
            TestAssert.Equal($"waiting: Other Windows accounts on this PC may be able to change this install: {folder} belongs to another Windows account",
                await LineAsync(loop), "not locked: the reason");
            TestAssert.Equal("", await LineAsync(new HostLoop(paths, _ => { }, TextWriter.Null)), "a loop without the real check shows no line");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>platform.json's launcherRoot in an install folder, or null.</summary>
    private static string? LauncherRoot(string folder)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "platform.json")));
        return settings.RootElement.TryGetProperty("launcherRoot", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
        (string Game, string Air) pins, Func<string, string, bool>? sameDrive = null, InstallRules? rules = null, string? launcherSha256 = null) =>
        new(HostPaths.Resolve(null, Local(root), Path.Combine(release, "platform") + Path.DirectorySeparatorChar), Local(root), Desktop(root), screen)
        {
            Folder = folder,
            Clients = clients,
            Confirm = () => true,
            Start = platform => { started.Add(platform); return Task.FromResult(true); },
            Rules = rules ?? new([], []),
            Pins = pins,
            SameDrive = sameDrive,
            LauncherSha256 = launcherSha256 ?? ClassicLauncherFiles.LauncherSha256,
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
