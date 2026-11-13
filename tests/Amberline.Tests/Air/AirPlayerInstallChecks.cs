using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Host;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL: the player's installer (batch 619). Every check builds synthetic AIR and game folders, a synthetic user profile (its
/// LocalAppData and desktop) and an install folder under one temporary folder: never a real client folder, the real profile or the
/// real desktop. Hosts are reserved .test names or loopback.
/// </summary>
public static class AirPlayerInstallChecks
{
    private const string PinA = "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";
    private const string PinB = "B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2";

    [Check("server.air-player-install-move-plan", TimeoutSeconds = 300)]
    public static void MovesRenameOnOneDriveAndCopyCheckThenDeleteAcrossDrives() => WithRoot("league-air-install-move", root =>
    {
        // One drive: three renames (AIR, game, the game's language project) into the install's layout.
        var same = Fixture.Make(Path.Combine(root, "same"));
        var plan = same.Plan();
        TestAssert.Equal(3, plan.Moves.Count, string.Join(", ", plan.Moves));
        TestAssert.Equal(0L, plan.CopyBytes, "a move on one drive must copy nothing");
        TestAssert.Equal(Path.Combine(same.Install, PlayerInstall.GameRelease), plan.Game);
        var before = same.Snapshot();
        TestAssert.Equal(0, PlayerInstall.Execute(plan).Count);
        same.AssertMoved(plan, before);

        // Another drive (the drive test answers no): copy, check every file, then remove the originals.
        var other = Fixture.Make(Path.Combine(root, "other"));
        plan = other.Plan();
        before = other.Snapshot();
        var copied = 0;
        TestAssert.Equal(0, PlayerInstall.Execute(plan, null, new(SameDrive: (_, _) => false, AfterCopy: _ => copied++)).Count);
        TestAssert.Equal(before.Count, copied, "every file must be copied once");
        other.AssertMoved(plan, before);

        // A failure halfway through the copy: the originals stay as they were and the partial copy is removed.
        var halfway = Fixture.Make(Path.Combine(root, "halfway"));
        plan = halfway.Plan();
        before = halfway.Snapshot();
        var count = 0;
        Fails(() => PlayerInstall.Execute(plan, null, new((_, _) => false, _ => { if (++count == 12) throw new IOException("disk removed"); })), "disk removed");
        halfway.AssertUntouched(plan, before);

        // A copy that differs from its original (one byte) is caught by the check, before any original is touched.
        var corrupt = Fixture.Make(Path.Combine(root, "corrupt"));
        plan = corrupt.Plan();
        before = corrupt.Snapshot();
        var bent = false;
        Fails(() => PlayerInstall.Execute(plan, null, new((_, _) => false, path =>
        {
            if (bent || new FileInfo(path).Length == 0) return;
            bent = true;
            using var stream = File.OpenWrite(path);
            stream.WriteByte(0x5A);
        })), "differs from the original");
        corrupt.AssertUntouched(plan, before);

        // A folder in use when it is moved into place (an open file in the game folder): the AIR folder already moved is moved back,
        // on one drive and across drives alike.
        foreach (var crossDrive in new[] { false, true })
        {
            var busy = Fixture.Make(Path.Combine(root, crossDrive ? "busy-copy" : "busy-rename"));
            plan = busy.Plan();
            before = busy.Snapshot();
            using (new FileStream(busy.GameExecutable, FileMode.Open, FileAccess.Read, FileShare.Read))
                Fails(() => PlayerInstall.Execute(plan, null, new((_, _) => !crossDrive)), "could not be moved");
            busy.AssertUntouched(plan, before);
        }

        // The 2011 launcher folder (batch 761): on one drive it is renamed into <install>\launcher with the other folders; across drives it is
        // copied, checked and then removed, like them. The launcher's refusals and its stage are in server.classic-launcher-install.
        var launcherSource = Path.Combine(root, "launcher-same");
        var launcherSha = ClassicLauncherChecks.WriteLauncherFiles(launcherSource);
        var launched = Fixture.Make(Path.Combine(root, "launcher-one-drive"));
        plan = PlayerInstall.MakePlan(launched.Profile, launched.Air, launched.Game, launched.Install, launched.Pins with { Launcher = launcherSha }, launcherSource);
        TestAssert.Equal(4, plan.Moves.Count, "the AIR client, the game, its language project and the launcher");
        TestAssert.Equal(Path.Combine(launched.Install, "launcher"), plan.Moves.Single(move => FolderMove.Same(move.Source, launcherSource)).Target,
            "the launcher folder's target");
        before = launched.Snapshot();
        TestAssert.Equal(0, PlayerInstall.Execute(plan).Count);
        // The launcher's own names moved; its folder is left for Install to remove once it is empty, as the AIR client's shared folder is (batch 639).
        launched.AssertMoved(plan with { Moves = plan.Moves.Where(move => !FolderMove.Same(move.Source, launcherSource)).ToList() }, before);
        TestAssert.False(File.Exists(Path.Combine(launcherSource, "LoLLauncher.exe")), "the launcher's own files left the picked folder");
        TestAssert.True(File.Exists(Path.Combine(launched.Install, "launcher", "LoLLauncher.exe")), "the launcher is in the install");

        var crossing = Path.Combine(root, "launcher-other-drive");
        ClassicLauncherChecks.WriteLauncherFiles(crossing);
        var across = Fixture.Make(Path.Combine(root, "launcher-across"));
        plan = PlayerInstall.MakePlan(across.Profile, across.Air, across.Game, across.Install, across.Pins with { Launcher = launcherSha }, crossing);
        before = across.Snapshot();
        TestAssert.Equal(0, PlayerInstall.Execute(plan, null, new(SameDrive: (_, _) => false)).Count);
        across.AssertMoved(plan with { Moves = plan.Moves.Where(move => !FolderMove.Same(move.Source, crossing)).ToList() }, before);
        TestAssert.False(File.Exists(Path.Combine(crossing, "LoLLauncher.exe")), "across drives the launcher's own files left the folder once their copies were checked");
        TestAssert.True(File.Exists(Path.Combine(across.Install, "launcher", "LoLLauncher.exe")), "the launcher is in the install");
    });


    [Check("server.air-player-install-second-server", TimeoutSeconds = 300)]
    public static void ASecondServerReusesTheInstall() => WithRoot("league-air-install-second", root =>
    {
        var fixture = Fixture.Make(root);
        File.WriteAllText(Path.Combine(fixture.Air, "lol.properties"), "host=prod.example.test\r\n");
        var first = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("one.example.test", PinA), "Alpha", fixture.Tool);
        var record = PlayerInstall.ExistingInstall(fixture.Profile, fixture.Pins);
        TestAssert.True(record is not null && record == first.Record, "the per-user record does not name the install");
        TestAssert.Equal(Path.Combine(fixture.Install, "air"), record!.Air);

        // The second server's Play file: the recorded clients are used as they are; only its connection and shortcut are added.
        var plan = PlayerInstall.MakePlan(fixture.Profile, record.Air, record.Game, record.Install, fixture.Pins);
        TestAssert.Equal(0, plan.Moves.Count);
        var second = PlayerInstall.Install(fixture.Profile, plan, Connection("two.example.test", PinB), "Bravo", fixture.Tool);
        TestAssert.Equal(record, second.Record, "the second server changed the install");
        TestAssert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Install, "servers")).Length);
        TestAssert.Equal(new[] { "Alpha.lnk", "Bravo.lnk" }, Directory.GetFiles(fixture.Profile.Desktop).Select(path => Path.GetFileName(path)).Order().ToArray());
        TestAssert.Equal(new[] { "Alpha.lnk", "Bravo.lnk" }, Directory.GetFiles(fixture.Install, "*.lnk").Select(path => Path.GetFileName(path)).Order().ToArray(),
            "the install folder holds no copy of each shortcut");
        foreach (var (result, pin, host) in new[] { (first, PinA, "one.example.test"), (second, PinB, "two.example.test") })
        {
            var link = ShellLink.Read(result.Shortcut);
            TestAssert.Equal("--shortcut " + PlayerInstall.ServerId(pin), link.Arguments);
            // The play mode runs the install's own folders: the moved AIR (no second copy) and the moved game.
            var stage = Amberline.AirCompanion.Program.LoadDesktopStage(result.Server, 8393, Path.Combine(result.Server, "server.json"));
            TestAssert.Equal(record.Air, stage.AirRoot);
            TestAssert.Equal(Path.Combine(record.Game, "League of Legends.exe"), stage.StockExecutable);
            TestAssert.Equal(host, stage.RemoteInvitation!.Host);
            TestAssert.Equal(pin, stage.RemoteInvitation.CertificateSha25664hex);
            TestAssert.Equal(string.Empty, stage.RemoteInvitation.Token64hex, "a token reached the server's connection");
        }

        // The moved AIR folder points at the companion's local endpoints; its stock properties are kept beside them.
        TestAssert.True(File.ReadAllText(Path.Combine(record.Air, "lol.properties")).Contains("host=127.0.0.1", StringComparison.Ordinal), "AIR was not pointed at the local proxy");
        TestAssert.Equal("host=prod.example.test\r\n", File.ReadAllText(Path.Combine(record.Air, "lol.properties.stock")));
        TestAssert.True(RetailClients.AirProblem(record.Air, fixture.Pins.Air) is null, "the configured AIR folder no longer passes its check");

        // Running the first Play file again repairs its shortcut and its copy in the install folder, and keeps everything else.
        File.Delete(first.Shortcut);
        File.Delete(Path.Combine(fixture.Install, "Alpha.lnk"));
        PlayerInstall.Install(fixture.Profile, plan, Connection("one.example.test", PinA), "Alpha", fixture.Tool);
        TestAssert.True(File.Exists(first.Shortcut), "running the Play file again did not repair its shortcut");
        TestAssert.True(File.Exists(Path.Combine(fixture.Install, "Alpha.lnk")), "running the Play file again did not repair the install folder's copy");
        TestAssert.Equal(2, Directory.GetFiles(fixture.Profile.Desktop).Length);
        TestAssert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Install, "servers")).Length);
    });

    [Check("server.air-player-install-shortcut", TimeoutSeconds = 300)]
    public static void ShortcutStartsTheWindowlessToolWithTheGameIcon() => WithRoot("league-air-install-shortcut", root =>
    {
        var fixture = Fixture.Make(root);
        var name = "Check " + Guid.NewGuid().ToString("N")[..8];
        var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("one.example.test", PinA), name, fixture.Tool);
        TestAssert.Equal(Path.Combine(fixture.Profile.Desktop, name + ".lnk"), result.Shortcut);
        TestAssert.False(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk")),
            "the check wrote to the real desktop");
        var tool = Path.Combine(fixture.Install, PlayerInstall.ToolFolder);
        var link = ShellLink.Read(result.Shortcut);
        TestAssert.Equal(Path.Combine(tool, PlayerInstall.WindowlessName), link.Target);
        TestAssert.Equal("--shortcut " + PlayerInstall.ServerId(PinA), link.Arguments);
        TestAssert.Equal(tool, link.WorkingDirectory);
        // The icon is a reference to the moved game executable's own first icon; no icon file exists in the install.
        TestAssert.Equal(Path.Combine(result.Record.Game, "League of Legends.exe"), link.IconPath);
        TestAssert.Equal(0, link.IconIndex);
        // The same shortcut is copied into the install folder under the desktop file's name, and server.json names the copy.
        var folderCopy = Path.Combine(fixture.Install, name + ".lnk");
        TestAssert.True(File.Exists(folderCopy), "the install folder holds no copy of the shortcut");
        TestAssert.Equal(link, ShellLink.Read(folderCopy), "the install folder's copy differs from the desktop shortcut");
        TestAssert.Equal(folderCopy, FolderShortcutOf(result.Server), "server.json does not name the copy");
        TestAssert.Equal(0, Directory.GetFiles(fixture.Install, "*.ico", SearchOption.AllDirectories).Length);

        // The windowless copy differs from the console apphost only in the PE Subsystem field (3 console, 2 Windows GUI).
        var console = File.ReadAllBytes(Path.Combine(tool, "Amberline.AirCompanion.exe"));
        var windowless = File.ReadAllBytes(Path.Combine(tool, PlayerInstall.WindowlessName));
        var at = BitConverter.ToInt32(console, 0x3C) + 4 + 20 + 68;
        TestAssert.Equal((ushort)3, BitConverter.ToUInt16(console, at));
        TestAssert.Equal((ushort)2, BitConverter.ToUInt16(windowless, at));
        windowless[at] = 3;
        TestAssert.True(console.AsSpan().SequenceEqual(windowless), "the windowless copy differs in more than its subsystem");

        // The real apphost of this build: its windowless copy still starts the companion (here with its output redirected).
        var built = Path.Combine(AppContext.BaseDirectory, "..", "..", "Amberline.AirCompanion", "release");
        var copy = Path.Combine(root, "built");
        Fixture.CopyTree(built, copy);
        PlayerInstall.WriteWindowless(copy);
        var real = File.ReadAllBytes(Path.Combine(copy, PlayerInstall.WindowlessName));
        TestAssert.Equal((ushort)2, BitConverter.ToUInt16(real, BitConverter.ToInt32(real, 0x3C) + 92));
        var start = new ProcessStartInfo(Path.Combine(copy, PlayerInstall.WindowlessName)) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        TestAssert.True(process.WaitForExit(60_000), "the windowless companion did not end");
        TestAssert.Equal(0, process.ExitCode, output);
        TestAssert.True(output.Contains("--install --connection", StringComparison.Ordinal), output);
    });

    [Check("server.air-player-install-update-swap", TimeoutSeconds = 300)]
    public static void TheShortcutStagesANewToolAndSwitchesItIn() => UpdateSwapAsync().GetAwaiter().GetResult();

    private static async Task UpdateSwapAsync()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-install-update");
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var port = AirBootstrapChecks.FreePort();
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", pin, port), "Alpha", fixture.Tool);
            var install = fixture.Install;
            var tool = Path.Combine(install, PlayerInstall.ToolFolder);
            TestAssert.Equal("one", Version(tool));
            var served = ServedTool(root, "two");
            File.WriteAllText(tool + ".sha256", served.Sha256);

            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate))
            {
                // The server runs the installed version: nothing is downloaded.
                TestAssert.True(await PlayerInstall.StageUpdateAsync(install, result.Server, CancellationToken.None) is null, "the same version was staged");
                TestAssert.False(Directory.Exists(tool + ".next"), "the same version left a staged folder");
            }
            served = ServedTool(root, "three");
            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate))
            {
                // A tampered archive (another SHA-256 than the manifest's) is refused before anything is written.
                var archive = Path.Combine(served.Folder, "companion.zip");
                var genuine = File.ReadAllBytes(archive);
                File.Copy(Path.Combine(ServedTool(root, "tampered").Folder, "companion.zip"), archive, overwrite: true);
                await FailsAsync(() => PlayerInstall.StageUpdateAsync(install, result.Server, CancellationToken.None), "does not match the SHA-256");
                TestAssert.False(Directory.Exists(tool + ".next"), "a refused archive was unpacked");
                File.WriteAllBytes(archive, genuine);

                var staged = await PlayerInstall.StageUpdateAsync(install, result.Server, CancellationToken.None);
                TestAssert.Equal(tool + ".next", staged);
                TestAssert.Equal("three", Version(staged!));
                TestAssert.True(File.Exists(Path.Combine(staged!, PlayerInstall.WindowlessName)), "the staged tool has no windowless program");
                TestAssert.Equal(served.Sha256, File.ReadAllText(staged + ".sha256"));
                TestAssert.Equal("one", Version(tool), "staging replaced the running tool");
            }

            // The switch, as the staged tool makes it once the old one has exited; while a file of the old tool is open it is refused and
            // the old tool stays complete.
            using (new FileStream(Path.Combine(tool, "Amberline.AirCompanion.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Refused(() => PlayerInstall.InstallTool(tool + ".next", install), "is running");
            TestAssert.Equal("one", Version(tool));
            TestAssert.False(Directory.Exists(tool + ".new"), "a refused switch left its copy");
            PlayerInstall.InstallTool(tool + ".next", install);
            TestAssert.Equal("three", Version(tool));
            TestAssert.Equal(served.Sha256, File.ReadAllText(tool + ".sha256"));
            TestAssert.True(File.Exists(Path.Combine(tool, PlayerInstall.WindowlessName)), "the switched tool has no windowless program");
            TestAssert.Equal(0, Directory.GetDirectories(install, PlayerInstall.ToolFolder + ".old*").Length + Directory.GetDirectories(install, PlayerInstall.ToolFolder + ".new").Length,
                "the switch left the old or the new folder");
            TestAssert.True(File.Exists(result.Shortcut) && ShellLink.Read(result.Shortcut).Target == Path.Combine(tool, PlayerInstall.WindowlessName),
                "the shortcut no longer reaches the tool");
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-install-keys-by-pin", TimeoutSeconds = 300)]
    public static void AServerIsKnownByItsPinNotItsAddress() => WithRoot("league-air-install-pin", root =>
    {
        var fixture = Fixture.Make(root);
        var first = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("old.example.test", PinA), "Alpha", fixture.Tool);
        var record = first.Record;
        var plan = PlayerInstall.MakePlan(fixture.Profile, record.Air, record.Game, record.Install, fixture.Pins);

        // The same server (its pin) sends a Play file with a new address and a new name: one connection, one shortcut.
        var again = PlayerInstall.Install(fixture.Profile, plan, Connection("new.example.test", PinA), "Alpha Two", fixture.Tool);
        TestAssert.Equal(first.Server, again.Server);
        TestAssert.Equal(1, Directory.GetDirectories(Path.Combine(fixture.Install, "servers")).Length);
        TestAssert.Equal("new.example.test", Amberline.AirCompanion.Program.ReadRemoteInvitation(Path.Combine(again.Server, "server.json")).Host);
        TestAssert.Equal(new[] { "Alpha Two.lnk" }, Directory.GetFiles(fixture.Profile.Desktop).Select(path => Path.GetFileName(path)).ToArray());
        TestAssert.Equal(new[] { "Alpha Two.lnk" }, Directory.GetFiles(fixture.Install, "*.lnk").Select(path => Path.GetFileName(path)).ToArray(),
            "the install folder's copy did not follow the rename");

        // Another server under a name the desktop already has (a file that is not ours) gets a numbered shortcut; the file is kept.
        var foreign = Path.Combine(fixture.Profile.Desktop, "Bravo.lnk");
        File.WriteAllText(foreign, "the player's own file");
        var other = PlayerInstall.Install(fixture.Profile, plan, Connection("other.example.test", PinB), "Bravo", fixture.Tool);
        TestAssert.Equal(Path.Combine(fixture.Profile.Desktop, "Bravo 2.lnk"), other.Shortcut);
        TestAssert.Equal("the player's own file", File.ReadAllText(foreign));
    });

    [Check("server.air-player-update-stores-advised-host", TimeoutSeconds = 300)]
    public static void TheServersAdvisedHostIsStored() => AdvisedHostAsync().GetAwaiter().GetResult();

    private static async Task AdvisedHostAsync()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-install-host");
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var port = AirBootstrapChecks.FreePort();
            // The stored connection names "localhost"; the server's manifest names 127.0.0.1 (PlatformBootstrap.Map's publicHost).
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("localhost", pin, port), "Alpha", fixture.Tool);
            var served = ServedTool(root, "one");
            File.WriteAllText(Path.Combine(fixture.Install, PlayerInstall.ToolFolder) + ".sha256", served.Sha256);
            var connection = Path.Combine(result.Server, "server.json");

            // A manifest that names another port is refused, and the stored connection stays as it was.
            await using (var wrong = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate, advertisedPort: port + 1))
                await FailsAsync(() => PlayerInstall.StageUpdateAsync(fixture.Install, result.Server, CancellationToken.None), "does not match this connection");
            TestAssert.Equal("localhost", Amberline.AirCompanion.Program.ReadRemoteInvitation(connection).Host);

            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate))
                TestAssert.True(await PlayerInstall.StageUpdateAsync(fixture.Install, result.Server, CancellationToken.None) is null, "the same version was staged");
            var stored = Amberline.AirCompanion.Program.ReadRemoteInvitation(connection);
            TestAssert.Equal("127.0.0.1", stored.Host, "the host the server advised was not stored");
            TestAssert.Equal(port, stored.Port);
            TestAssert.Equal(pin, stored.CertificateSha25664hex);
            using var json = JsonDocument.Parse(File.ReadAllText(connection));
            TestAssert.Equal("Alpha", json.RootElement.GetProperty("name").GetString(), "rewriting the host lost the server's other fields");
        }
        finally { Cleanup(root); }
    }

    [Check("server.host-lan-play-file", TimeoutSeconds = 300)]
    public static void AHomeNetworkServersPlayFileInstalls() => LanPlayFileAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 632: the Play file of a server for the home network only (<c>addressSource</c> <c>lan</c>) holds its private address,
    /// and the friend's side takes it: Play.cmd's pinned download (which keeps the address the pinned manifest advises), the
    /// connection file it writes and the installer, which keeps the address in the server's connection. The check cannot listen on a
    /// home-network address, so the download reaches the Platform on loopback and the manifest advises 192.168.1.20.
    /// </summary>
    private static async Task LanPlayFileAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string Lan = "192.168.1.20";
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var home = HostModeChecks.Home.Create("league-host-lan-play-file");
        Directory.CreateDirectory(home.Loop.Paths.App);
        File.Copy(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", Amberline.Platform.HostInvite.TemplateFile)),
            Path.Combine(home.Loop.Paths.App, Amberline.Platform.HostInvite.TemplateFile));
        // A real archive for the download to unpack (Home.Create's stand-in is random bytes).
        var served = ServedTool(home.Root, "one");
        File.Copy(Path.Combine(served.Folder, "companion.zip"), Path.Combine(home.Loop.Paths.Bootstrap, "companion.zip"), overwrite: true);
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
            dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server", publicHost = Lan, addressSource = "lan",
        }));
        var run = Task.Run(() => Amberline.Platform.HostMode.RunAsync(home.Loop));
        AirRemoteInvitation connection;
        try
        {
            using var browser = home.Client();
            await HostModeChecks.WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var invite = await browser.GetStringAsync(Amberline.Platform.HostInvite.AddPath);
            TestAssert.True(invite.Contains("Players must be on the same home network as this PC.", StringComparison.Ordinal), "Invite says it: " + invite);
            using var file = await browser.GetAsync(Amberline.Platform.HostInvite.PlayFilePath);
            var play = await file.Content.ReadAsStringAsync();
            TestAssert.Equal(System.Net.HttpStatusCode.OK, file.StatusCode, play);
            var token = System.Text.RegularExpressions.Regex.Match(play, "\\$HostToken = '([^']+)'").Groups[1].Value;
            TestAssert.Equal(Lan, System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(token).Select(b => (byte)(b ^ 0x5A)).ToArray()),
                "the Play file holds the home-network address");
            TestAssert.False(play.Contains(Lan, StringComparison.Ordinal), "never in plain text");
            var pin = System.Text.RegularExpressions.Regex.Match(play, "\\$Pin = '([0-9A-F]{64})'").Groups[1].Value;

            // Play.cmd's own PowerShell part (Windows PowerShell 5.1): the pinned download and the connection file.
            var declarations = Path.Combine(home.Root, "declarations.ps1");
            File.WriteAllText(declarations, AirPlayFileChecks.Declarations(play), new System.Text.UTF8Encoding(false));
            var driver = Path.Combine(home.Root, "lan.ps1");
            File.WriteAllText(driver, """
                param([string]$DeclarationsPath, [int]$Port, [string]$Pin, [string]$Root)
                $ErrorActionPreference = 'Stop'
                . $DeclarationsPath
                $tool = Update-Companion '127.0.0.1' $Port $Pin $Root
                Write-Connection (Join-Path $Root 'server.json') $tool.Host $Port $Pin
                Write-Output ('advised|' + $tool.Host + '|' + (Test-Path -LiteralPath (Join-Path $tool.Folder $Executable)))
                """, new System.Text.UTF8Encoding(false));
            var cache = Path.Combine(home.Root, "player-cache");
            var result = AirBootstrapChecks.RunPowerShell(AirPlayFileChecks.WindowsPowerShell(), ["-File", driver, "-DeclarationsPath", declarations,
                "-Port", home.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Pin", pin, "-Root", cache]);
            TestAssert.Equal(0, result.ExitCode, result.Output);
            TestAssert.True(result.Output.Contains($"advised|{Lan}|True", StringComparison.Ordinal), "the pinned download keeps the advised home-network address: " + result.Output);
            connection = Amberline.AirCompanion.Program.ReadRemoteInvitation(Path.Combine(cache, "server.json"));
            TestAssert.Equal((Lan, home.RemotePort, pin), (connection.Host, connection.Port, connection.CertificateSha25664hex), "the connection file");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        // The installer: the friend's install keeps the home-network address for this server, keyed by its pin.
        try
        {
            var fixture = Fixture.Make(Path.Combine(home.Root, "friend"));
            var installed = PlayerInstall.Install(fixture.Profile, fixture.Plan(), connection, "Test Server", fixture.Tool);
            TestAssert.Equal(Lan, Amberline.AirCompanion.Program.ReadRemoteInvitation(Path.Combine(installed.Server, "server.json")).Host,
                "the installer keeps the home-network address");
            TestAssert.Equal(Path.Combine(fixture.Profile.Desktop, "Test Server.lnk"), installed.Shortcut);
            TestAssert.True(File.Exists(installed.Shortcut), "the shortcut on the (temporary) desktop");
        }
        finally { Cleanup(home.Root); }
    }

    [Check("server.air-player-install-host-data-in-place", TimeoutSeconds = 300)]
    public static void FoldersTheLocalServerReadsStayInPlace() => WithRoot("league-air-install-host-data", root =>
    {
        var fixture = Fixture.Make(root);
        var before = fixture.Snapshot();
        // The home-mode host's settings in the synthetic profile name both folders, in any case; the game by an ancestor of its folder.
        var settings = fixture.Profile.HostSettings;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["DATAROOT"] = Path.GetDirectoryName(fixture.Game)!, ["airroot"] = fixture.Air,
        }));
        var plan = fixture.Plan();
        TestAssert.Equal(0, plan.Moves.Count, "a folder the server reads was planned to move: " + string.Join(", ", plan.Moves));
        TestAssert.Equal(fixture.Air, plan.AirCopyFrom);
        TestAssert.Equal(fixture.Game, plan.Game);
        TestAssert.True(plan.Notes.Count == 2 && plan.Notes.All(note => note.Contains(PlayerInstall.HostUsesFolder, StringComparison.Ordinal)), string.Join(" | ", plan.Notes));

        var result = PlayerInstall.Install(fixture.Profile, plan, Connection("one.example.test", PinA), "Alpha", fixture.Tool);
        TestAssert.Equal(fixture.Game, result.Record.Game, "the install does not refer to the game where it is");
        TestAssert.Equal(Path.Combine(fixture.Install, "air"), result.Record.Air);
        TestAssert.True(File.ReadAllText(Path.Combine(result.Record.Air, "lol.properties")).Contains("host=127.0.0.1", StringComparison.Ordinal), "the AIR copy is not configured");
        TestAssert.Equal(before.Count, fixture.Snapshot().Count, "the server's folders changed");
        foreach (var (path, hash) in before) TestAssert.Equal(hash, Fixture.Hash(path), $"the server's file {path} changed");
        TestAssert.False(File.Exists(Path.Combine(fixture.Air, "lol.properties")), "the server's AIR folder was configured");
        TestAssert.Equal(Path.Combine(fixture.Game, "League of Legends.exe"), ShellLink.Read(result.Shortcut).IconPath);

        // A settings file that cannot be read stops the install: it might name a folder that would otherwise move.
        File.WriteAllText(settings, "{ not json");
        Refused(() => fixture.Plan(), "cannot be read");
    });


    [Check("server.air-player-install-progress-and-cancel", TimeoutSeconds = 300)]
    public static void TheMoveReportsFilesAndPercentAndCancelsBeforeAnyOriginalChanges() => WithRoot("league-air-install-progress", root =>
    {
        // Across drives: one bar for the copy and its check, file counts per part, and Cancel offered only while the originals are untouched.
        var fixture = Fixture.Make(Path.Combine(root, "copy"));
        var plan = fixture.Plan();
        var steps = new Steps();
        TestAssert.Equal(0, PlayerInstall.Execute(plan, steps, new(SameDrive: (_, _) => false)).Count);
        var measured = steps.All.Where(step => step.Percent is not null).ToList();
        TestAssert.True(measured.Count >= 6, string.Join(" | ", steps.All));
        TestAssert.True(measured.Zip(measured.Skip(1)).All(pair => pair.First.Percent <= pair.Second.Percent), "the bar went back: " + string.Join(" | ", measured));
        TestAssert.Equal(100, measured[^1].Percent);
        TestAssert.True(measured.All(step => step.CanCancel), "a measured step offered no cancel");
        var gameFiles = Directory.GetFiles(Path.Combine(fixture.Install, PlayerInstall.GameRelease), "*", SearchOption.AllDirectories).Length;
        foreach (var text in new[] { $"Copying the game client: {gameFiles} of {gameFiles} files (", $"Checking the copied game client: {gameFiles} of {gameFiles} files" })
            TestAssert.True(measured.Any(step => step.Text.StartsWith(text, StringComparison.Ordinal)), $"no step reads '{text}': " + string.Join(" | ", measured));
        var placing = steps.All.Where(step => step.Text.StartsWith("Putting the", StringComparison.Ordinal)).ToList();
        TestAssert.Equal(plan.Moves.Count, placing.Count);
        TestAssert.True(placing.All(step => step.Percent is null && !step.CanCancel), "a rename step offered a cancel or a share");
        TestAssert.True(measured.All(step => steps.All.IndexOf(step) < steps.All.IndexOf(placing[0])), "the bar ran after the renames began");

        // Cancel while copying: the copies are removed and every original stays as it was.
        var canceled = Fixture.Make(Path.Combine(root, "cancel"));
        plan = canceled.Plan();
        var before = canceled.Snapshot();
        using var cancel = new CancellationTokenSource();
        var copied = 0;
        try
        {
            PlayerInstall.Install(canceled.Profile, plan, Connection("one.example.test", PinA), "Alpha", canceled.Tool, null,
                new((_, _) => false, _ => { if (++copied == 5) cancel.Cancel(); }), cancel.Token);
            throw new InvalidOperationException("the canceled install went on");
        }
        catch (OperationCanceledException) { }
        canceled.AssertUntouched(plan, before);
        TestAssert.False(File.Exists(canceled.Profile.RecordPath), "a canceled install wrote its record");
        TestAssert.Equal(0, Directory.GetFiles(canceled.Profile.Desktop).Length);

        // The install log: the plan, the clients in place and the end, with the shortcut.
        var logged = Fixture.Make(Path.Combine(root, "log"));
        var result = PlayerInstall.Install(logged.Profile, logged.Plan(), Connection("one.example.test", PinA), "Alpha", logged.Tool);
        var log = File.ReadAllText(logged.Profile.InstallLog);
        foreach (var line in new[] { $"install: Alpha into {logged.Install}", "install: the clients are in place", $"install: done; shortcut {result.Shortcut}" })
            TestAssert.True(log.Contains(line, StringComparison.Ordinal), $"the log lacks '{line}': {log}");
    });

    [Check("server.air-player-install-window-accessible", TimeoutSeconds = 300)]
    public static void TheWindowsAreKeyboardAndScreenReaderReady() => WithRoot("league-air-install-window", root =>
    {
        var fixture = Fixture.Make(root);
        // The window offers folders it found: here the ones the older play mode's stage remembers.
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Profile.OldStage)!);
        File.WriteAllText(fixture.Profile.OldStage, JsonSerializer.Serialize(new { airSource = fixture.Air, stockExecutable = fixture.GameExecutable }));
        OnSta(() =>
        {
            // Never shown: the checks read the controls as Windows would present them, and no window opens.
            using var window = new PlayerInstallWindow.InstallWindow(fixture.Profile, Connection("one.example.test", PinA), "Alpha", fixture.Tool, fixture.Pins);
            TestAssert.Equal(System.Windows.Forms.HighDpiMode.PerMonitorV2, PlayerInstallWindow.DpiMode);
            TestAssert.Equal(System.Windows.Forms.AutoScaleMode.Dpi, window.AutoScaleMode);
            TestAssert.Equal("Segoe UI", window.Font.FontFamily.Name);
            TestAssert.True(window.Font.SizeInPoints >= 10F, "the font is smaller than 10 pt: " + window.Font.SizeInPoints);
            AssertAccessible(window);
            var setup = Find(window, "Setup");
            // Batch 681: the server's download (hidden until the server offers one) sits under the client folders. Batch 761: the optional
            // 2011 launcher row comes after the game folder, as the third folder.
            TestAssert.Equal(new[] { "Change the AIR client folder", "Change the game client folder", "Change the 2011 launcher folder", "Download the game from Alpha", "Change the install folder", "Install", "Cancel" },
                TabOrder(setup).ToArray());
            TestAssert.Equal("Install", ((System.Windows.Forms.Control)window.AcceptButton!).AccessibleName);
            TestAssert.Equal("Cancel", ((System.Windows.Forms.Control)window.CancelButton!).AccessibleName);
            TestAssert.True(Find(window, "Install").Enabled, "the found clients do not make a plan: " + Find(window, "Notes").Text);
            // An offer: its size beside the button; with clients found, Enter still installs them.
            window.ShowOffer(new([new("air/LolClient.exe", 1, new string('0', 64))], 2_600_000_000));
            TestAssert.Equal("No copy of your own? Alpha can send you the game and the AIR client (2.6 GB).", Find(window, "Download from the server").Text);
            TestAssert.Equal("Install", ((System.Windows.Forms.Control)window.AcceptButton!).AccessibleName);
            AssertAccessible(window);

            // The bar: a share with its percentage and Cancel while copying; moving, no Cancel and the reason during a rename.
            var bar = (System.Windows.Forms.ProgressBar)Find(window, "Setup progress");
            window.ShowStep(new("Copying the game client: 12 of 34 files (0.1 of 0.2 GB)", 37, CanCancel: true));
            TestAssert.Equal((System.Windows.Forms.ProgressBarStyle.Continuous, 37, "37%"), (bar.Style, bar.Value, Find(window, "Percent done").Text));
            TestAssert.True(Find(window, "Cancel").Enabled, "Cancel is off while copying");
            TestAssert.Equal(PlayerInstallWindow.CanCancel, Find(window, "Canceling").Text);
            TestAssert.Equal("Copying the game client: 12 of 34 files (0.1 of 0.2 GB)", Find(window, "Progress").Text);
            window.ShowStep(new("Putting the game client in place…"));
            TestAssert.Equal(System.Windows.Forms.ProgressBarStyle.Marquee, bar.Style);
            TestAssert.False(Find(window, "Cancel").Enabled, "Cancel is on during a rename");
            TestAssert.Equal(PlayerInstall.CannotCancel, Find(window, "Canceling").Text);

            // The finish page: Open (Enter) and Close (Escape), and what stayed where.
            var result = new PlayerInstall.Result(new(fixture.Install, fixture.Air, fixture.Game), root, Path.Combine(fixture.Profile.Desktop, "Alpha.lnk"), [],
                ["These stayed in D:\\Downloads, because they are not part of the AIR client: notes.txt."]);
            window.ShowFinished(result);
            TestAssert.True(window.Finished, "the finished install is not marked");
            TestAssert.Equal("Open Alpha now", ((System.Windows.Forms.Control)window.AcceptButton!).AccessibleName);
            TestAssert.Equal("Close", ((System.Windows.Forms.Control)window.CancelButton!).AccessibleName);
            TestAssert.Equal(new[] { "Open Alpha now", "Close" }, TabOrder(Find(window, "Setup finished")).ToArray());
            var next = Find(window, "What to do next").Text;
            TestAssert.True(next.Contains("\"Alpha\" is on your desktop", StringComparison.Ordinal) && next.Contains("notes.txt", StringComparison.Ordinal), next);
            AssertAccessible(window);
        });

        // An install that stopped: what happened, what to do (the refusal's own sentence), the details and the log.
        var refused = PlayerInstallWindow.InstallProblem(fixture.Profile, "Alpha setup", new InstallRefusedException("The player tool in the install folder is running. Close the AIR client and the game, then try again."));
        TestAssert.Equal(("Setup stopped before it finished.", fixture.Profile.InstallLog), (refused.WhatHappened, refused.Log));
        TestAssert.True(refused.WhatToDo.StartsWith("The player tool", StringComparison.Ordinal) && refused.Report().Contains("Log: " + fixture.Profile.InstallLog, StringComparison.Ordinal),
            refused.Report());

        // The removal window: each server's own Remove button in name order, then Remove Amberline and Close.
        var installed = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("one.example.test", PinA), "Bravo", fixture.Tool);
        PlayerInstall.AddServer(fixture.Profile, installed.Record, Connection("two.example.test", PinB), "Alpha");
        OnSta(() =>
        {
            using var window = new PlayerInstallWindow.UninstallWindow(fixture.Profile, installed.Record, fixture.Tool);
            TestAssert.Equal("Segoe UI", window.Font.FontFamily.Name);
            AssertAccessible(window);
            TestAssert.Equal(new[] { "Remove Alpha", "Remove Bravo", "Also delete the game files", "Remove Amberline", "Close" },
                TabOrder(Find(window, "Remove Amberline")).ToArray());
            TestAssert.Equal("Close", ((System.Windows.Forms.Control)window.CancelButton!).AccessibleName);
            TestAssert.True(window.AcceptButton is null, "Enter must not start a removal");
            var game = Find(window, "Also delete the game files").Text;
            TestAssert.True(game.Contains(installed.Record.Install, StringComparison.Ordinal) && game.EndsWith(" GB)", StringComparison.Ordinal), game);
        });
    });

    /// <summary>
    /// Batch 647: the public build renames the project's old name to Amberline (tools/export/export-public.ps1, section 4), which had
    /// put the player tool's per-user folder inside the host's <c>%LOCALAPPDATA%\Amberline</c>. Computed under the public names, the
    /// player's paths and the host's (pointer, data, the settings the player reads) never overlap, so neither removal reaches the
    /// other's files. Paths only: nothing is created.
    /// </summary>
    [Check("server.air-player-folder-apart-from-host")]
    public static void ThePlayersFolderIsApartFromTheHostsUnderThePublicNames()
    {
        // The export's rename of a path; in the public tree itself the names are public already, so it changes nothing there.
        static string Public(string path) => path.Replace("Amberline", "Amberline", StringComparison.Ordinal);
        const string local = @"C:\Profile\AppData\Local";
        var profile = new PlayerProfile(local, @"C:\Profile\Desktop");
        var host = Public(Path.GetDirectoryName(FolderMove.PointerPath(local))!);
        foreach (var path in new[] { FolderMove.PointerPath(local), Amberline.Platform.HostPaths.Resolve(null, local, @"C:\Elsewhere\app\platform").Data,
                     profile.HostSettings }.Select(Public))
            TestAssert.True(FolderMove.Inside(path, host), $"{path} is not in the host's folder {host}");
        foreach (var path in new[] { profile.PlayerFolder, profile.RecordPath, profile.InstallLog, profile.OldStage }.Select(Public))
            TestAssert.False(FolderMove.Overlaps(path, host), $"the player's {path} overlaps the host's folder {host}");
        TestAssert.True(FolderMove.Inside(PlayerMode.StageRoot(), FolderMove.PlayerFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))),
            "the play mode's stage is outside the player's folder");
    }

    [Check("server.air-player-uninstall-keeps-game", TimeoutSeconds = 300)]
    public static void RemovingAServerOrAmberlineKeepsTheGameUnlessAsked() => WithRoot("league-air-uninstall", root =>
    {
        var fixture = Fixture.Make(Path.Combine(root, "keep"));
        File.WriteAllText(Path.Combine(fixture.Air, "lol.properties"), "host=prod.example.test\r\n");
        var before = fixture.Snapshot();
        var alpha = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("one.example.test", PinA), "Alpha", fixture.Tool);
        var record = alpha.Record;
        var bravo = PlayerInstall.AddServer(fixture.Profile, record, Connection("two.example.test", PinB), "Bravo");
        var install = record.Install;
        TestAssert.Equal(PlayerInstall.Uninstaller, File.ReadAllText(Path.Combine(install, PlayerInstall.UninstallFile)));
        TestAssert.True(PlayerInstall.Uninstaller.Contains("\"%~dp0companion\\Amberline.exe\" --uninstall", StringComparison.Ordinal), PlayerInstall.Uninstaller);
        var entry = PlayerUninstall.AppsEntry(record);
        TestAssert.Equal($"\"{Path.Combine(install, "companion", "Amberline.exe")}\" --uninstall", entry["UninstallString"]);
        TestAssert.Equal(Path.Combine(record.Game, "League of Legends.exe") + ",0", entry["DisplayIcon"]);
        TestAssert.Equal($"{install}|1|1", $"{entry["InstallLocation"]}|{entry["NoModify"]}|{entry["NoRepair"]}");
        TestAssert.False(fixture.Profile.WritesRegistry, "a check's profile may write the registry");
        TestAssert.Equal(record, PlayerUninstall.Installed(fixture.Profile with { LocalAppData = Path.Combine(root, "nobody") }, Path.Combine(install, "companion")) is { } found
            ? found with { Game = record.Game } : null, "the install was not found from its own tool folder");

        // "Remove this server": refused while it runs (its log is open); then its shortcut and folder go, the other server stays.
        var servers = PlayerUninstall.Servers(install);
        TestAssert.Equal(new[] { "Alpha", "Bravo" }, servers.Select(server => server.Name).ToArray());
        Directory.CreateDirectory(Path.Combine(alpha.Server, "logs"));
        using (new FileStream(Path.Combine(alpha.Server, "logs", "shortcut.log"), FileMode.Create, FileAccess.Write, FileShare.Read))
            Refused(() => PlayerUninstall.RemoveServer(fixture.Profile, servers[0]), "Alpha is still running");
        TestAssert.True(File.Exists(Path.Combine(alpha.Server, "server.json")) && File.Exists(alpha.Shortcut), "a refused removal changed the server");
        TestAssert.True(File.Exists(Path.Combine(install, "Alpha.lnk")), "a refused removal deleted Alpha's copy in the install folder");
        PlayerUninstall.RemoveServer(fixture.Profile, servers[0]);
        TestAssert.False(File.Exists(alpha.Shortcut) || Directory.Exists(alpha.Server), "Alpha's shortcut or folder is left");
        TestAssert.True(File.Exists(bravo.Shortcut) && Directory.Exists(bravo.Folder), "removing Alpha touched Bravo");
        TestAssert.False(File.Exists(Path.Combine(install, "Alpha.lnk")), "Alpha's copy in the install folder is left");
        TestAssert.True(File.Exists(Path.Combine(install, "Bravo.lnk")), "removing Alpha removed Bravo's copy");

        // "Remove Amberline from this PC", game files kept: shortcuts, servers, the tool's files, the record and the user's files go; the
        // AIR client (its stock properties back) and the game stay byte for byte; the player's own file keeps the install folder.
        var foreign = Path.Combine(fixture.Profile.Desktop, "Bravo 2.lnk");
        File.WriteAllText(foreign, "the player's own file");
        File.WriteAllText(Path.Combine(install, "my-notes.txt"), "mine");
        var tool = Path.Combine(install, PlayerInstall.ToolFolder);
        var removal = PlayerUninstall.RemoveAll(fixture.Profile, record, deleteGame: false, runningTool: tool);
        TestAssert.Equal(new[] { tool }, removal.AfterExit.ToArray());
        TestAssert.Equal(new[] { Path.Combine(install, "air"), Path.Combine(install, "game") }, removal.Kept.ToArray());
        TestAssert.Equal(0, removal.Left.Count, string.Join(", ", removal.Left));
        TestAssert.Equal(new[] { "Bravo 2.lnk" }, Directory.GetFiles(fixture.Profile.Desktop).Select(path => Path.GetFileName(path)).ToArray());
        TestAssert.Equal(new[] { "air", "companion", "game", "my-notes.txt" },
            Directory.EnumerateFileSystemEntries(install).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());
        TestAssert.False(Directory.Exists(fixture.Profile.PlayerFolder), "the per-user folder is left");
        foreach (var (path, hash) in before)
        {
            var move = new[] { (fixture.Air, record.Air), (fixture.Game, record.Game), (fixture.Language, Path.Combine(install, "game", "projects", "lol_game_client_en_us")) }
                .Single(pair => FolderMove.Inside(path, pair.Item1));
            TestAssert.Equal(hash, Fixture.Hash(Path.Combine(move.Item2, Path.GetRelativePath(move.Item1, path))), $"the kept {path} changed");
        }
        TestAssert.Equal(0, Directory.GetFiles(record.Air, "*.stock").Length, "a stock copy of the AIR properties is left");

        // The running tool's own folder goes once it has exited, also while a file in it is still open for a moment.
        var held = new FileStream(Path.Combine(tool, "Amberline.AirCompanion.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var cleanup = PlayerUninstall.DeleteAfterExit(0, removal.AfterExit, [install]))
        {
            Thread.Sleep(1500);
            TestAssert.True(Directory.Exists(tool), "the folder went while a file in it was open");
            held.Dispose();
            TestAssert.True(cleanup.WaitForExit(60_000), "the cleanup did not end");
        }
        TestAssert.False(Directory.Exists(tool), "the tool's folder is left after the cleanup");
        TestAssert.True(Directory.Exists(install), "the install folder with the player's files was removed");

        // With "also delete the game files": refused while a program runs from the install; then the whole install goes, but a game
        // folder used in place (a server on this PC reads it) stays.
        var all = Fixture.Make(Path.Combine(root, "all"));
        Directory.CreateDirectory(Path.GetDirectoryName(all.Profile.HostSettings)!);
        File.WriteAllText(all.Profile.HostSettings, JsonSerializer.Serialize(new { dataRoot = all.Game }));
        var gameBefore = Directory.EnumerateFiles(all.Game, "*", SearchOption.AllDirectories).ToDictionary(path => path, Fixture.Hash);
        var everything = PlayerInstall.Install(all.Profile, all.Plan(), Connection("one.example.test", PinA), "Alpha", all.Tool);
        TestAssert.Equal(all.Game, everything.Record.Game);
        var probe = Path.Combine(everything.Record.Air, "probe.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), probe);
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/d", "/q", "/k" }) start.ArgumentList.Add(argument);
        using (var running = Process.Start(start)!)
        {
            try { Refused(() => PlayerUninstall.RemoveAll(all.Profile, everything.Record, deleteGame: true), "probe.exe is running from"); }
            finally
            {
                running.StandardInput.Close();
                TestAssert.True(running.WaitForExit(30_000), "the probe did not end when its input closed");
            }
        }
        TestAssert.True(File.Exists(everything.Shortcut), "a refused removal deleted the shortcut");
        File.Delete(probe);
        // Batch 647: an install from before keeps its record and an older Play file's download in the old per-user folder. The record
        // is read from there and moved over, and the removal empties both folders.
        var old = all.Profile.OldPlayerFolder;
        Directory.CreateDirectory(Path.Combine(old, PlayerInstall.ToolFolder));
        File.WriteAllText(Path.Combine(old, "server.json"), "{}");
        File.Move(all.Profile.RecordPath, Path.Combine(old, "install.json"));
        var moved = PlayerUninstall.Installed(all.Profile, Path.Combine(root, "nowhere"));
        TestAssert.Equal(everything.Record, moved, "the record in the old per-user folder is not read");
        TestAssert.True(File.Exists(all.Profile.RecordPath) && !File.Exists(Path.Combine(old, "install.json")), "the old record did not move over");
        removal = PlayerUninstall.RemoveAll(all.Profile, moved!, deleteGame: true);
        TestAssert.Equal(new[] { all.Game }, removal.Kept.ToArray());
        TestAssert.False(Directory.Exists(everything.Record.Install), "the install folder is left");
        TestAssert.Equal(0, Directory.GetFiles(all.Profile.Desktop).Length);
        TestAssert.False(Directory.Exists(all.Profile.PlayerFolder) || Directory.Exists(old), "the per-user folder is left");
        TestAssert.True(File.Exists(all.Profile.HostSettings), "the player's removal deleted the host's settings");
        foreach (var (path, hash) in gameBefore) TestAssert.Equal(hash, Fixture.Hash(path), $"the game used in place lost {path}");
    });

    [Check("server.air-player-install-folder-copy", TimeoutSeconds = 300)]
    public static void TheInstallFolderKeepsACopyOfEachShortcut() => WithRoot("league-air-install-folder-copy", root =>
    {
        // The copy has the desktop file's name in the install folder. A file of the player's own with that name is left alone: the copy is numbered.
        var fixture = Fixture.Make(root);
        var install = fixture.Install;
        var alpha = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("one.example.test", PinA), "Alpha", fixture.Tool);
        var plan = PlayerInstall.MakePlan(fixture.Profile, alpha.Record.Air, alpha.Record.Game, install, fixture.Pins);
        File.WriteAllText(Path.Combine(install, "Bravo.lnk"), "the player's own file");
        var bravo = PlayerInstall.Install(fixture.Profile, plan, Connection("two.example.test", PinB), "Bravo", fixture.Tool);
        TestAssert.Equal("the player's own file", File.ReadAllText(Path.Combine(install, "Bravo.lnk")), "the player's own file changed");
        TestAssert.Equal(Path.Combine(install, "Bravo 2.lnk"), FolderShortcutOf(bravo.Server), "server.json does not name the numbered copy");
        TestAssert.Equal(ShellLink.Read(bravo.Shortcut), ShellLink.Read(Path.Combine(install, "Bravo 2.lnk")), "the numbered copy differs from the desktop shortcut");
        TestAssert.Equal(new[] { "Alpha.lnk", "Bravo 2.lnk", "Bravo.lnk" }, Directory.GetFiles(install, "*.lnk").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray(),
            "the install folder's shortcuts are not the expected ones");

        // "Remove this server" removes Bravo's copy and its desktop shortcut; the player's own file stays.
        PlayerUninstall.RemoveServer(fixture.Profile, PlayerUninstall.Servers(install).Single(server => server.Name == "Bravo"));
        TestAssert.Equal(new[] { "Alpha.lnk", "Bravo.lnk" }, Directory.GetFiles(install, "*.lnk").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray(),
            "removing Bravo did not remove its copy");
        TestAssert.False(File.Exists(bravo.Shortcut), "removing Bravo left its desktop shortcut");
        TestAssert.Equal("the player's own file", File.ReadAllText(Path.Combine(install, "Bravo.lnk")), "removing Bravo deleted the player's own file");

        // An install from before this batch has no folderShortcut and no copy: the next Play file writes both.
        var delta = PlayerInstall.Install(fixture.Profile, plan, Connection("four.example.test", new string('D', 64)), "Delta", fixture.Tool);
        var stored = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(delta.Server, "server.json")))!.AsObject();
        stored.Remove("folderShortcut");
        File.WriteAllText(Path.Combine(delta.Server, "server.json"), stored.ToJsonString());
        File.Delete(Path.Combine(install, "Delta.lnk"));
        PlayerInstall.Install(fixture.Profile, plan, Connection("four.example.test", new string('D', 64)), "Delta", fixture.Tool);
        TestAssert.True(File.Exists(Path.Combine(install, "Delta.lnk")), "the next Play file did not write the copy");
        TestAssert.Equal(Path.Combine(install, "Delta.lnk"), FolderShortcutOf(delta.Server), "the next Play file did not name the copy");

        // A missing Desktop folder: the desktop write fails as it always has, but the copy in the install folder is written first.
        Directory.Delete(fixture.Profile.Desktop, recursive: true);
        var echo = new string('E', 64);
        try { PlayerInstall.Install(fixture.Profile, plan, Connection("five.example.test", echo), "Echo", fixture.Tool); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        TestAssert.True(File.Exists(Path.Combine(install, "Echo.lnk")), "a missing desktop stopped the copy in the install folder");
        TestAssert.Equal(Path.Combine(install, "Echo.lnk"), FolderShortcutOf(Path.Combine(install, "servers", PlayerInstall.ServerId(echo))), "server.json does not name the copy");
    });

    [Check("server.air-player-old-folder-parent", TimeoutSeconds = 300)]
    public static void TheFolderFromBeforeBatch647GoesWithItsParentOnlyWhenEmpty() => WithRoot("league-air-old-parent", root =>
    {
        // Batch 649: the removal empties the folder from before batch 647 (Amberline\player), then its Amberline parent, each only when it
        // is empty: a parent with another entry stays, and so does a folder with an entry the removal does not own (and its parent).
        TestAssert.Equal("", OldFolderLeft(root, "empty", _ => { }), "an empty Amberline\\player and its Amberline parent are removed");
        TestAssert.Equal("Amberline", OldFolderLeft(root, "sibling",
            fixture => File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Profile.OldPlayerFolder)!, "other.txt"), "x")),
            "a parent with another entry stays");
        TestAssert.Equal("player, Amberline", OldFolderLeft(root, "stray",
            fixture => File.WriteAllText(Path.Combine(fixture.Profile.OldPlayerFolder, "stray.txt"), "x")),
            "a folder with an entry the removal does not own stays, and so does its parent");
    });

    /// <summary>
    /// An install whose record and an older Play file's download sit in the folder from before batch 647; the removal runs, then reports
    /// which of the two folders are still there.
    /// </summary>
    private static string OldFolderLeft(string root, string name, Action<Fixture> alsoLeave)
    {
        var fixture = Fixture.Make(Path.Combine(root, name));
        var record = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("one.example.test", PinA), "Alpha", fixture.Tool).Record;
        var old = fixture.Profile.OldPlayerFolder;
        var parent = Path.GetDirectoryName(old)!;
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "server.json"), "{}");
        File.Move(fixture.Profile.RecordPath, Path.Combine(old, "install.json"));
        alsoLeave(fixture);
        var moved = PlayerUninstall.Installed(fixture.Profile, Path.Combine(root, name, "nowhere"));
        TestAssert.Equal(record, moved, "the record in the old per-user folder is read");
        PlayerUninstall.RemoveAll(fixture.Profile, moved!, deleteGame: false);
        return string.Join(", ", new[] { Directory.Exists(old) ? "player" : null, Directory.Exists(parent) ? "Amberline" : null }.OfType<string>());
    }

    [Check("server.air-player-shortcut-start-dialogs", TimeoutSeconds = 300)]
    public static void TheShortcutsStartIsNeverSilent() => ShortcutStartAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 639: the shortcut's start through its seam (no window, no client): the start window's lines, and a dialog with what happened,
    /// what to do and Try again for an unreachable server, a failed pin and a missing client. A server that answers wrongly does not block.
    /// </summary>
    private static async Task ShortcutStartAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-shortcut-start");
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            using var stranger = HostFiles.Certificate(Path.Combine(root, "stranger"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var port = AirBootstrapChecks.FreePort();
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", pin, port), "Alpha", fixture.Tool);
            var id = PlayerInstall.ServerId(pin);
            var served = ServedTool(root, "one");
            File.WriteAllText(Path.Combine(fixture.Install, PlayerInstall.ToolFolder) + ".sha256", served.Sha256);
            var log = Path.Combine(result.Server, "logs", "shortcut.log");

            // Nobody listens: "Can't reach Alpha." twice (Try again, then Close), the address never in the details, no play.
            var ui = new FakeStart(true, false);
            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
            TestAssert.Equal(new[] { PlayerMode.Checking, PlayerMode.Checking }, ui.Lines.ToArray());
            TestAssert.Equal(new[] { "Can't reach Alpha.", "Can't reach Alpha." }, ui.Problems.Select(problem => problem.WhatHappened).ToArray());
            TestAssert.True(ui.Problems.All(problem => problem.Log == log && problem.WhatToDo.Contains("Try again", StringComparison.Ordinal) &&
                !problem.Details.Contains("127.0.0.1", StringComparison.Ordinal)), string.Join(" | ", ui.Problems.Select(problem => problem.Report())));
            TestAssert.Equal(0, ui.Played);

            // Another certificate on the port: the pin fails.
            await using (var wrong = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, stranger))
            {
                ui = new FakeStart(false);
                TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
                TestAssert.Equal("Alpha did not prove it is the server this shortcut was made for.", ui.Problems.Single().WhatHappened);
                TestAssert.True(ui.Problems.Single().WhatToDo.Contains("new Play file", StringComparison.Ordinal), ui.Problems.Single().WhatToDo);
            }

            // The server answers: the update check passes and play starts, with the start window's second line; its window callback
            // closes the start window. A server that answers wrongly (a manifest for another port) does not block play.
            foreach (var advertised in new int?[] { null, port + 1 })
                await using (var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate, advertised))
                {
                    ui = new FakeStart();
                    TestAssert.Equal(0, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
                    TestAssert.Equal(new[] { PlayerMode.Checking, PlayerMode.Starting }, ui.Lines.ToArray());
                    TestAssert.Equal((1, 0), (ui.Played, ui.Problems.Count));
                    TestAssert.Equal((result.Server, Path.Combine(result.Server, "server.json")), ui.PlayedWith);
                    TestAssert.True(ui.Hidden >= 2, "the start window was not closed by the client's window and at the end");
                }

            // The AIR client is gone from the install: the dialog says so and names the Play file; nothing is downloaded or played.
            File.Delete(Path.Combine(result.Record.Air, "LolClient.exe"));
            ui = new FakeStart(false);
            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
            var missing = ui.Problems.Single();
            TestAssert.Equal("The AIR client is missing.", missing.WhatHappened);
            TestAssert.True(missing.WhatToDo.Contains(result.Record.Air, StringComparison.Ordinal) && missing.WhatToDo.Contains("Play file again", StringComparison.Ordinal), missing.WhatToDo);
            TestAssert.Equal(0, ui.Played);
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-shortcut-leftover-client", TimeoutSeconds = 300)]
    public static void ALeftoverClientIsNamedAndClosedOnlyWhenAsked() => LeftoverClientAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 697: a local port the start binds is held. The owner's report: a leftover client from an earlier start kept it, and the
    /// shortcut said "Can't reach &lt;server&gt;" although the server answered. The port holder is this check's own listener; the
    /// leftovers are copies of the 64-bit cmd.exe this check starts inside its temporary install (and one outside it, which must stay).
    /// </summary>
    private static async Task LeftoverClientAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-shortcut-leftover");
        var started = new List<Process>();
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var port = AirBootstrapChecks.FreePort();
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", pin, port), "Alpha", fixture.Tool);
            var id = PlayerInstall.ServerId(pin);
            var served = ServedTool(root, "one");
            File.WriteAllText(Path.Combine(fixture.Install, PlayerInstall.ToolFolder) + ".sha256", served.Sha256);
            await using var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate);
            var held = AirBootstrapChecks.FreePort();
            var holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, held);
            holder.Start();
            try
            {
                // Another program (this check) holds the port: a plain message naming it, never "Can't reach Alpha.".
                var ui = new FakeStart(false) { Binds = held };
                TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
                var busy = ui.Problems.Single();
                TestAssert.Equal($"Port {held} is in use by another program.", busy.WhatHappened);
                TestAssert.True(busy.WhatToDo.Contains("Try again", StringComparison.Ordinal), busy.WhatToDo);
                TestAssert.Equal((0, 0), (ui.Played, ui.Questions.Count));

                // A leftover AIR client of this install (its companion held the port), and a program of the same name elsewhere.
                var leftover = Synthetic(Path.Combine(result.Record.Air, "LolClient.exe"), started);
                var elsewhere = Synthetic(Path.Combine(root, "elsewhere", "LolClient.exe"), started);
                _ = leftover.WaitForExitAsync().ContinueWith(_ => holder.Stop(), TaskScheduler.Default);

                // Cancel: nothing is closed and nothing plays.
                ui = new FakeStart { Binds = held };
                TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
                var (question, canClose) = ui.Questions.Single();
                TestAssert.Equal("A game client from an earlier start is still running in the background.", question.WhatHappened);
                TestAssert.Equal(("Close it and start?", true), (question.WhatToDo, canClose));
                TestAssert.True(question.Details.Contains(Path.Combine(result.Record.Air, "LolClient.exe"), StringComparison.OrdinalIgnoreCase) &&
                    !question.Details.Contains("elsewhere", StringComparison.Ordinal), question.Details);
                TestAssert.Equal((false, false, 0, 0), (leftover.HasExited, elsewhere.HasExited, ui.Played, ui.Problems.Count));

                // Close and start: only the install's leftover ends, the port frees, and play starts.
                ui = new FakeStart { Binds = held, CloseAndStart = true };
                TestAssert.Equal(0, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
                TestAssert.Equal(new[] { PlayerMode.Checking, PlayerMode.Closing, PlayerMode.Starting }, ui.Lines.ToArray());
                TestAssert.True(leftover.WaitForExit(10_000), "the leftover AIR client was not closed");
                TestAssert.False(elsewhere.HasExited, "a program outside the install was closed");
                TestAssert.Equal((1, 1, 0), (ui.Played, ui.Questions.Count, ui.Problems.Count));

                // The game itself runs from the install: a game in progress, Cancel only, and the game keeps running.
                var game = Synthetic(Path.Combine(result.Record.Game, "League of Legends.exe"), started);
                ui = new FakeStart { Binds = held, CloseAndStart = true };
                TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, ui.Seam));
                (question, canClose) = ui.Questions.Single();
                TestAssert.Equal(("A game is in progress on this PC.", false), (question.WhatHappened, canClose));
                TestAssert.Equal((false, false, 0), (game.HasExited, elsewhere.HasExited, ui.Played));
            }
            finally { holder.Stop(); }
        }
        finally
        {
            foreach (var process in started)
            {
                if (!process.HasExited) process.StandardInput.Close();
                process.WaitForExit(30_000);
                process.Dispose();
            }
            Cleanup(root);
        }
    }

    /// <summary>A copy of the 64-bit cmd.exe at <paramref name="path"/>, started: it waits on its input and ends when that closes.</summary>
    private static Process Synthetic(string path, List<Process> started)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path, overwrite: true);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/d", "/q", "/k" }) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        started.Add(process);
        return process;
    }

    /// <summary>The shortcut's seam: the start window's lines, the dialogs it would show and the answers to Try again, and the play step.</summary>
    private sealed class FakeStart(params bool[] answers)
    {
        private readonly Queue<bool> answers = new(answers);

        /// <summary>The seam; batch 697: the leftover question's answer and the ports (only <see cref="Binds"/>, never the real ones).</summary>
        public PlayerMode.ShortcutUi Seam => new(Lines.Add, () => Hidden++, problem =>
        {
            Problems.Add(problem);
            return this.answers.TryDequeue(out var again) && again;
        }, (question, canClose) =>
        {
            Questions.Add((question, canClose));
            return canClose && CloseAndStart;
        }, (server, connection, shown) =>
        {
            // As the real play step: the local listeners bind first (a held port throws the owner's SocketException).
            if (Binds is { } port)
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
            }
            Played++;
            PlayedWith = (server, connection);
            shown();
            return Task.FromResult(0);
        }, Binds is { } bound ? [bound] : []);

        public bool CloseAndStart { get; init; }
        public List<(PlayerProblem Question, bool CanClose)> Questions { get; } = [];
        public int? Binds { get; init; }
        public List<string> Lines { get; } = [];
        public List<PlayerProblem> Problems { get; } = [];
        public int Hidden { get; private set; }
        public int Played { get; private set; }
        public (string, string) PlayedWith { get; private set; }
    }

    /// <summary>A synchronous progress sink: every step, in order (Progress&lt;T&gt; would post them to the thread pool).</summary>
    private sealed class Steps : IProgress<InstallProgress>
    {
        public List<InstallProgress> All { get; } = [];

        public void Report(InstallProgress value) => All.Add(value);
    }

    /// <summary>Runs a window check on a single-threaded apartment, as Windows' controls expect, and passes its failure on.</summary>
    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { check(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
    }

    private static IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control parent) =>
        parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => new[] { child }.Concat(All(child)));

    private static System.Windows.Forms.Control Find(System.Windows.Forms.Control parent, string name) =>
        All(parent).FirstOrDefault(control => control.AccessibleName == name) ?? throw new InvalidOperationException($"no control named '{name}'");

    /// <summary>The buttons and boxes Tab reaches inside <paramref name="parent"/>, in Windows' order (by TabIndex, depth first).</summary>
    private static IEnumerable<string> TabOrder(System.Windows.Forms.Control parent) =>
        parent.Controls.Cast<System.Windows.Forms.Control>().OrderBy(child => child.TabIndex).SelectMany(child =>
            child is System.Windows.Forms.ButtonBase ? [child.AccessibleName!] : TabOrder(child));

    /// <summary>Every control has a name and a description for a screen reader, and only Windows' own colours (high contrast applies).</summary>
    private static void AssertAccessible(System.Windows.Forms.Form window)
    {
        foreach (var control in new[] { window }.Concat(All(window)))
        {
            var what = $"{control.GetType().Name} '{control.AccessibleName}' ({control.Text})";
            TestAssert.False(string.IsNullOrWhiteSpace(control.AccessibleName), "no accessible name: " + what);
            TestAssert.True(control.AccessibleDescription is not null && (control.Text.Length == 0 || control.AccessibleDescription.Length > 0),
                "no accessible description: " + what);
            TestAssert.True(control.ForeColor.IsSystemColor && control.BackColor.IsSystemColor, $"a colour of its own ({control.ForeColor}, {control.BackColor}): " + what);
            if (control is System.Windows.Forms.ButtonBase) TestAssert.True(control.TabStop, "a button Tab skips: " + what);
        }
    }

    /// <summary>Synthetic stock folders as a player downloads them: AIR, and the game release with its language project beside it.</summary>
    internal sealed record Fixture(PlayerProfile Profile, string Air, string Game, string Language, string Install, string Tool, ClientPins Pins)
    {
        public string GameExecutable => Path.Combine(Game, "League of Legends.exe");

        public static Fixture Make(string root)
        {
            var profile = new PlayerProfile(Path.Combine(root, "profile", "Local"), Path.Combine(root, "profile", "Desktop"));
            Directory.CreateDirectory(profile.Desktop);
            var air = Path.Combine(root, "downloads", "lol_air_client");
            AirStockLayoutFixture.Write(air);
            var projects = Path.Combine(root, "downloads", "live", "projects");
            var game = Path.Combine(projects, "lol_game_client", "releases", "0.0.0.51");
            Directory.CreateDirectory(Path.Combine(game, "DATA", "CFG"));
            Directory.CreateDirectory(Path.Combine(game, "LEVELS", "Map1"));
            File.WriteAllBytes(Path.Combine(game, "League of Legends.exe"), RandomNumberGenerator.GetBytes(64 * 1024));
            File.WriteAllText(Path.Combine(game, "DATA", "CFG", "Locale.cfg"), "[General]\r\nLanguageLocaleRegion=en_US\r\n");
            for (var index = 0; index < 20; index++)
                File.WriteAllBytes(Path.Combine(game, "DATA", $"file{index}.bin"), RandomNumberGenerator.GetBytes(16 * 1024));
            var language = Path.Combine(projects, "lol_game_client_en_us");
            Directory.CreateDirectory(Path.Combine(language, "releases", "0.0.0.20", "DATA", "Menu"));
            File.WriteAllText(Path.Combine(language, "releases", "0.0.0.20", "DATA", "Menu", "fontconfig_en_US.txt"), "stand-in");
            var tool = Directory.CreateDirectory(Path.Combine(root, "bootstrap", "companion")).FullName;
            WriteTool(tool, "one");
            File.WriteAllText(tool + ".sha256", new string('1', 64));
            return new(profile, air, game, language, Path.Combine(root, "Install"), tool,
                new(RetailClients.Sha256(Path.Combine(game, "League of Legends.exe")), RetailClients.Sha256(Path.Combine(air, "LolClient.exe"))));
        }

        public PlayerInstall.Plan Plan() => PlayerInstall.MakePlan(Profile, Air, Game, Install, Pins);

        /// <summary>Every file of the three client folders with its SHA-256.</summary>
        public Dictionary<string, string> Snapshot() => new[] { Air, Game, Language }
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)).ToDictionary(path => path, Hash);

        public void AssertMoved(PlayerInstall.Plan plan, Dictionary<string, string> before)
        {
            foreach (var move in plan.Moves) TestAssert.False(Directory.Exists(move.Source), $"the original {move.Label} was not removed");
            foreach (var (path, hash) in before)
            {
                var move = plan.Moves.Single(candidate => FolderMove.Inside(path, candidate.Source));
                var moved = Path.Combine(move.Target, Path.GetRelativePath(move.Source, path));
                TestAssert.Equal(hash, Hash(moved), $"{moved} differs from its original");
            }
            TestAssert.Equal(0, Leftovers(plan).Count, string.Join(", ", Leftovers(plan)));
        }

        public void AssertUntouched(PlayerInstall.Plan plan, Dictionary<string, string> before)
        {
            var now = Snapshot();
            TestAssert.Equal(before.Count, now.Count, "the originals lost or gained files");
            foreach (var (path, hash) in before) TestAssert.Equal(hash, now[path], $"the original {path} changed");
            foreach (var move in plan.Moves) TestAssert.False(Directory.Exists(move.Target), $"{move.Target} exists after a failed move");
            TestAssert.Equal(0, Leftovers(plan).Count, string.Join(", ", Leftovers(plan)));
        }

        /// <summary>The partial copies and set-aside originals a move may leave.</summary>
        private static List<string> Leftovers(PlayerInstall.Plan plan) => plan.Moves
            .SelectMany(move => new[] { move.Target + ".partial", move.Source + ".amberline-moved" }).Where(Directory.Exists).ToList();

        public static string Hash(string path) => RetailClients.Sha256(path);

        public static void CopyTree(string source, string destination)
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>A stand-in player tool: a minimal console PE header (never started) and its version.</summary>
    internal static void WriteTool(string folder, string version)
    {
        var executable = new byte[0x200];
        executable[0] = (byte)'M';
        executable[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(executable, 0x3C);
        BitConverter.GetBytes(0x00004550u).CopyTo(executable, 0x80);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(executable, 0x80 + 24);
        BitConverter.GetBytes((ushort)3).CopyTo(executable, 0x80 + 24 + 68);
        File.WriteAllBytes(Path.Combine(folder, "Amberline.AirCompanion.exe"), executable);
        File.WriteAllText(Path.Combine(folder, "version.txt"), version);
    }

    /// <summary>The install folder's copy of a server's shortcut, as the server's server.json names it (null when it names none).</summary>
    private static string? FolderShortcutOf(string server) =>
        PlayerInstall.Text(PlayerInstall.ReadObject(Path.Combine(server, "server.json")), "folderShortcut");

    private static string Version(string tool) => File.ReadAllText(Path.Combine(tool, "version.txt"));

    /// <summary>A bootstrap folder whose companion.zip holds a stand-in tool of <paramref name="version"/>, and the archive's SHA-256.</summary>
    private static (string Folder, string Sha256) ServedTool(string root, string version)
    {
        var files = Directory.CreateDirectory(Path.Combine(root, "tool-" + version)).FullName;
        WriteTool(files, version);
        var folder = Directory.CreateDirectory(Path.Combine(root, "served-" + version)).FullName;
        var archive = Path.Combine(folder, "companion.zip");
        ZipFile.CreateFromDirectory(files, archive);
        return (folder, RetailClients.Sha256(archive));
    }

    private static AirRemoteInvitation Connection(string host, string pin, int port = 8391) => new(host, port, CertificateSha25664hex: pin);

    private static void Refused(Action action, string text)
    {
        try { action(); }
        catch (InstallRefusedException refused)
        {
            TestAssert.True(refused.Message.Contains(text, StringComparison.Ordinal), $"refused for another reason: {refused.Message}");
            return;
        }
        throw new InvalidOperationException($"not refused (expected: {text})");
    }

    private static void Fails(Action action, string text)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InstallRefusedException)
        {
            TestAssert.True(exception.Message.Contains(text, StringComparison.Ordinal), $"failed for another reason: {exception.Message}");
            return;
        }
        throw new InvalidOperationException($"did not fail (expected: {text})");
    }

    private static async Task FailsAsync(Func<Task> action, string text)
    {
        try { await action(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or HttpRequestException)
        {
            TestAssert.True(exception.Message.Contains(text, StringComparison.Ordinal), $"failed for another reason: {exception.Message}");
            return;
        }
        throw new InvalidOperationException($"did not fail (expected: {text})");
    }

    private static void WithRoot(string prefix, Action<string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory(prefix);
        try { check(root); }
        finally { Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
