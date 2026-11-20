using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.AirCompanion;
using Amberline.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 856, design 2026-11-17 Part 1): a server that moved to patch 1.0.0.132. The old port's stand-in is this check's own
/// pinned HTTPS listener serving the old manifest with <c>movedTo</c> (the host's real stand-in is batch 856b's); the 1.0.0.132 server is the
/// Platform's bootstrap routes on another free port with another test certificate. Synthetic profile, install and tools; the moved dialog
/// and the program it would start go through the seam, so no window opens and nothing starts.
/// </summary>
public static partial class AirPlayerInstallChecks
{
    /// <summary>
    /// The 1.0.0.132 player folder the checks name: the Play file's rule (<c>Amberline( [0-9]+)? Player</c>), never a line's own (1.0.0.126's is
    /// Amberline Player, 1.0.0.132's Amberline 132 Player), so it is valid on both.
    /// </summary>
    private const string MovedFolder = "Amberline 7 Player";

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    [Check("server.air-player-moved-shortcut", TimeoutSeconds = 300)]
    public static void AMovedServersShortcutSaysSoFirstAndNeverPlays() => MovedShortcutAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The moved dialog comes before every other check of the start: also with the AIR client missing, a client left from an earlier
    /// start running, or a local port held by another program (a friend's 1.0.0.132 tool, say). Not now ends the start; nothing plays.
    /// </summary>
    private static async Task MovedShortcutAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-moved-shortcut");
        var started = new List<Process>();
        try
        {
            using var scene = MovedScene.Make(root);
            var install = scene.Fixture.Install;
            var local = scene.Fixture.Profile.LocalAppData;
            await using var standIn = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(scene.Target()), scene.OldArchive);

            var ui = new FakeStart { LocalAppData = local };
            TestAssert.Equal(1, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
            var question = ui.Moves.Single();
            TestAssert.Equal(("Alpha", "Alpha now runs patch 1.0.0.132 (January 2012).", false), (question.Title, question.WhatHappened, question.Installed));
            TestAssert.Equal("Set it up now? Use the same username and password as before. Your 1.0.0.126 setup is kept.", question.WhatToDo);
            TestAssert.Equal((0, 0, 0, 0), (ui.Played, ui.Started.Count, ui.Problems.Count, ui.Questions.Count));
            TestAssert.Equal(new[] { PlayerMode.Checking }, ui.Lines.ToArray());
            TestAssert.False(Directory.Exists(Path.Combine(install, PlayerInstall.ToolFolder + ".next")), "a moved server's tool was staged");

            // A local port another program holds: the moved dialog, never "Port n is in use".
            var held = AirBootstrapChecks.FreePort();
            var holder = new TcpListener(IPAddress.Loopback, held);
            holder.Start();
            try
            {
                ui = new FakeStart { LocalAppData = local, Binds = held };
                TestAssert.Equal(1, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
                TestAssert.Equal((1, 0, 0), (ui.Moves.Count, ui.Problems.Count, ui.Played));
            }
            finally { holder.Stop(); }

            // The AIR client is gone: the moved dialog, never "The AIR client is missing".
            var air = Path.Combine(scene.Result.Record.Air, "LolClient.exe");
            File.Delete(air);
            ui = new FakeStart { LocalAppData = local };
            TestAssert.Equal(1, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
            TestAssert.Equal((1, 0, 0), (ui.Moves.Count, ui.Problems.Count, ui.Played));

            // A client from an earlier start still runs from the install: the moved dialog, no leftover question, and it keeps running.
            var leftover = Synthetic(air, started);
            ui = new FakeStart { LocalAppData = local, CloseAndStart = true };
            TestAssert.Equal(1, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
            TestAssert.Equal((1, 0, 0, 0), (ui.Moves.Count, ui.Questions.Count, ui.Problems.Count, ui.Played));
            TestAssert.False(leftover.HasExited, "the moved dialog closed a client");

            // A movedTo that is malformed (not an object, or fields of another type) or names a refused player folder: no dialog, no Set up and
            // no Start it, the plain "could not start" problem (Try again reads the manifest again, Close ends), and never play.
            await standIn.StopAsync();
            foreach (var (malformed, why) in new[] { ("\"x\"", "no valid port"), ("{\"port\":\"8491\",\"name\":5}", "no valid port"),
                (Json(new { port = scene.NewPort, certificateSha256 = scene.NewPin, name = "Beta", playerFolder = OwnPlayerFolder }), "no valid player folder") })
                await using (var odd = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(null).Replace("\"movedTo\":null", "\"movedTo\":" + malformed, StringComparison.Ordinal), scene.OldArchive))
                {
                    ui = new FakeStart(true, false) { LocalAppData = local, SetUp = true };
                    TestAssert.Equal(2, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
                    TestAssert.Equal((0, 0, 0), (ui.Moves.Count, ui.Played, ui.Started.Count));
                    TestAssert.Equal(new[] { "Alpha could not start.", "Alpha could not start." }, ui.Problems.Select(problem => problem.WhatHappened).ToArray());
                    TestAssert.True(ui.Problems[0].Details.Contains(why, StringComparison.Ordinal), ui.Problems[0].Details);
                }
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

    [Check("server.air-player-moved-finishing-run", TimeoutSeconds = 300)]
    public static void TheRunThatFinishesAnUpdateReadsMovedTo() => MovedFinishingRunAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The run that finishes an update (<c>checkUpdate: false</c>) reads the manifest only for <c>movedTo</c>: a moved server gets the dialog; a
    /// server that cannot be reached, or one that publishes another tool, is played as before, and nothing is staged.
    /// </summary>
    private static async Task MovedFinishingRunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-moved-finishing");
        try
        {
            using var scene = MovedScene.Make(root);
            var install = scene.Fixture.Install;
            var next = Path.Combine(install, PlayerInstall.ToolFolder + ".next");
            await using (var standIn = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(scene.Target()), scene.OldArchive))
            {
                var ui = new FakeStart { LocalAppData = scene.Fixture.Profile.LocalAppData };
                TestAssert.Equal(1, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: false, ui.Seam));
                TestAssert.Equal((1, 0), (ui.Moves.Count, ui.Played));
            }

            // Nobody listens: no dialog at all, play as before.
            var quiet = new FakeStart();
            TestAssert.Equal(0, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: false, quiet.Seam));
            TestAssert.Equal((1, 0, 0), (quiet.Played, quiet.Problems.Count, quiet.Moves.Count));
            TestAssert.Equal(new[] { PlayerMode.Checking, PlayerMode.Starting }, quiet.Lines.ToArray());

            // A server that publishes another tool: never a second update on this run.
            var newer = ServedTool(root, "newer");
            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(newer.Folder, scene.OldPort, scene.Old))
            {
                quiet = new FakeStart();
                TestAssert.Equal(0, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: false, quiet.Seam));
                TestAssert.Equal((1, 0), (quiet.Played, quiet.Problems.Count));
                TestAssert.False(Directory.Exists(next), "the finishing run staged another update");
            }
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-moved-install-window", TimeoutSeconds = 300)]
    public static void TheInstallWindowSaysSoBeforeInstalling() => MovedInstallWindowAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The <c>--install</c> window's first step (an old Play file run again): the moved dialog instead of the install, 0 once Set up started
    /// the 1.0.0.132 setup (Play.cmd reports anything else as a failure). Without <c>movedTo</c>, or when the manifest cannot be read, the
    /// install window opens as before.
    /// </summary>
    private static async Task MovedInstallWindowAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-moved-install");
        try
        {
            using var scene = MovedScene.Make(root);
            var local = scene.Fixture.Profile.LocalAppData;
            var connection = Connection("localhost", scene.OldPin, scene.OldPort);
            var log = scene.Fixture.Profile.InstallLog;

            var ui = new FakeStart { LocalAppData = local };
            TestAssert.True(await ServerMoved.BeforeInstallAsync(connection, "Alpha", log, ui.Moved) is null, "an unreachable server stopped the install");
            await using (var plain = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(null), scene.OldArchive))
                TestAssert.True(await ServerMoved.BeforeInstallAsync(connection, "Alpha", log, ui.Moved) is null, "a server that did not move stopped the install");
            TestAssert.Equal((0, 0), (ui.Moves.Count, ui.Problems.Count));

            // A refused player folder (this line's own): the plain problem, no dialog, nothing started.
            await using (var own = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(scene.Target(folder: OwnPlayerFolder)), scene.OldArchive))
            {
                var refused = new FakeStart { LocalAppData = local, SetUp = true };
                TestAssert.Equal(1, await ServerMoved.BeforeInstallAsync(connection, "Alpha", log, refused.Moved));
                TestAssert.Equal(("Alpha could not start.", 0, 0), (refused.Problems.Single().WhatHappened, refused.Moves.Count, refused.Started.Count));
            }

            await using var standIn = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(scene.Target()), scene.OldArchive);
            TestAssert.Equal(1, await ServerMoved.BeforeInstallAsync(connection, "Alpha", log, ui.Moved));
            TestAssert.Equal(("Alpha now runs patch 1.0.0.132 (January 2012).", 0), (ui.Moves.Single().WhatHappened, ui.Started.Count));
            TestAssert.True(ui.Logged.Contains("moved: not now"), string.Join(" | ", ui.Logged));

            await using var platform = await AirPlayFileChecks.StartPlatformAsync(scene.NewFolder, scene.NewPort, scene.New);
            ui = new FakeStart { LocalAppData = local, SetUp = true };
            TestAssert.Equal(0, await ServerMoved.BeforeInstallAsync(connection, "Alpha", log, ui.Moved));
            TestAssert.Equal(Path.Combine(local, MovedFolder, PlayerInstall.ToolFolder, "Amberline.AirCompanion.exe"), ui.Started.Single().File);
            TestAssert.Equal("--install", ui.Started.Single().Arguments[0]);
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-moved-set-up", TimeoutSeconds = 300)]
    public static void SetUpDoesWhatThe132PlayFileDoes() => MovedSetUpAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Set up as the 1.0.0.132 Play file: the manifest and the tool from the host the shortcut reached, over the 1.0.0.132 pin; a wrong pin, a
    /// wrong SHA-256, no valid advised address or an invalid <c>movedTo</c> are refused before anything is written; on success the Play
    /// file's layout and the <c>--install</c> call, whose connection holds the advised host. Start it when this PC has that install (same
    /// port and pin, whatever its host).
    /// </summary>
    private static async Task MovedSetUpAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-moved-set-up");
        try
        {
            using var scene = MovedScene.Make(root);
            using var stranger = HostFiles.Certificate(Path.Combine(root, "stranger"));
            var local = scene.Fixture.Profile.LocalAppData;
            var folder = Path.Combine(local, MovedFolder);
            var companion = Path.Combine(folder, PlayerInstall.ToolFolder);
            var log = scene.Log;

            // The 1.0.0.132 server is off: "Can't reach", Try again, then Close; nothing is written.
            var ui = new FakeStart(true, false) { LocalAppData = local, SetUp = true };
            TestAssert.Equal(1, await ServerMoved.RunAsync("Alpha", "localhost", scene.Target(), log, ui.Moved));
            TestAssert.Equal(new[] { "Cannot reach Alpha.", "Cannot reach Alpha." }, ui.Problems.Select(problem => problem.WhatHappened).ToArray());
            TestAssert.True(ui.Problems.All(problem => !problem.Details.Contains("localhost", StringComparison.OrdinalIgnoreCase)), ui.Problems[0].Report());
            TestAssert.Equal((0, false), (ui.Started.Count, Directory.Exists(folder)));
            TestAssert.Equal(new[] { ServerMoved.SettingUp, ServerMoved.SettingUp }, ui.Lines.ToArray());

            // Another certificate on the 1.0.0.132 port: the pin fails.
            await using (var wrong = await AirPlayFileChecks.StartPlatformAsync(scene.NewFolder, scene.NewPort, stranger))
            {
                ui = new FakeStart { LocalAppData = local, SetUp = true };
                TestAssert.Equal(1, await ServerMoved.RunAsync("Alpha", "localhost", scene.Target(), log, ui.Moved));
                TestAssert.Equal("This server does not match your invite.", ui.Problems.Single().WhatHappened);
                TestAssert.Equal((0, false), (ui.Started.Count, Directory.Exists(folder)));
            }

            // A manifest that advises no valid address.
            await using (var bad = await StartStandInAsync(scene.NewPort, scene.New, Json(new { protocol = 1, host = "not a host", port = scene.NewPort,
                certificateSha256 = scene.NewPin, companionSha256 = scene.NewSha }), File.ReadAllBytes(Path.Combine(scene.NewFolder, "companion.zip"))))
            {
                ui = new FakeStart { LocalAppData = local, SetUp = true };
                TestAssert.Equal(1, await ServerMoved.RunAsync("Alpha", "localhost", scene.Target(), log, ui.Moved));
                TestAssert.Equal("Patch 1.0.0.132 could not be set up for Alpha.", ui.Problems.Single().WhatHappened);
                TestAssert.True(ui.Problems.Single().Details.Contains("no valid address", StringComparison.Ordinal), ui.Problems.Single().Details);
                TestAssert.Equal((0, false), (ui.Started.Count, Directory.Exists(folder)));
            }

            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(scene.NewFolder, scene.NewPort, scene.New))
            {
                // An archive other than the one the manifest's SHA-256 names: refused before anything is written.
                var archive = Path.Combine(scene.NewFolder, "companion.zip");
                var genuine = File.ReadAllBytes(archive);
                File.Copy(Path.Combine(ServedTool(root, "tampered").Folder, "companion.zip"), archive, overwrite: true);
                ui = new FakeStart { LocalAppData = local, SetUp = true };
                TestAssert.Equal(1, await ServerMoved.RunAsync("Alpha", "localhost", scene.Target(), log, ui.Moved));
                TestAssert.Equal("Patch 1.0.0.132 could not be set up for Alpha.", ui.Problems.Single().WhatHappened);
                TestAssert.True(ui.Problems.Single().Details.Contains("does not match the SHA-256", StringComparison.Ordinal), ui.Problems.Single().Details);
                TestAssert.False(Directory.Exists(companion) || Directory.Exists(companion + ".next") || File.Exists(Path.Combine(folder, "server.json")),
                    "a refused archive was written");
                TestAssert.Equal(0, ui.Started.Count);
                File.WriteAllBytes(archive, genuine);

                // The genuine one: the Play file's layout, a connection with the host the manifest advises (127.0.0.1; the shortcut reached
                // localhost), and the 1.0.0.132 tool's --install with it.
                ui = new FakeStart { LocalAppData = local, SetUp = true };
                TestAssert.Equal(0, await ServerMoved.RunAsync("Alpha", "localhost", scene.Target(), log, ui.Moved));
                var connection = Path.Combine(folder, "server.json");
                var (file, arguments) = ui.Started.Single();
                TestAssert.Equal(Path.Combine(companion, "Amberline.AirCompanion.exe"), file);
                TestAssert.Equal(new[] { "--install", "--connection", connection, "--server-name", "Beta" }, arguments);
                var written = Amberline.AirCompanion.Program.ReadRemoteInvitation(connection);
                TestAssert.Equal(("127.0.0.1", scene.NewPort, scene.NewPin), (written.Host, written.Port, written.CertificateSha25664hex));
                TestAssert.Equal(scene.NewSha, File.ReadAllText(companion + ".sha256"));
                TestAssert.Equal("v132", Version(companion));
                TestAssert.Equal(0, ui.Problems.Count);
            }

            // A movedTo that is not valid is refused before any of it is used, and before the dialog: a player folder that is a path, and each
            // other field. The player folder follows the Play file's rule and is never this line's own, the host's or Amberline.
            ui = new FakeStart { LocalAppData = local, SetUp = true };
            await FailsAsync(() => ServerMoved.RunAsync("Alpha", "localhost", scene.Target(folder: @"..\Escaped"), log, ui.Moved), "no valid player folder");
            TestAssert.Equal((0, 0, 0), (ui.Moves.Count, ui.Problems.Count, ui.Started.Count));
            TestAssert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(local)!, "Escaped")), "a player folder outside LocalAppData was written");
            var otherLine = OwnPlayerFolder == "Amberline Player" ? "Amberline 132 Player" : "Amberline Player";
            foreach (var refused in new[] { OwnPlayerFolder, OwnPlayerFolder.ToUpperInvariant(), "Amberline", "Amberline", "amberline", "Moved Test Player",
                @"Amberline ..\Player", "Amberline Player\\..", "Amberline 1x Player", " Amberline 7 Player", "Amberline 7 Player\n", "Amberline  Player" })
                TestAssert.Equal("no valid player folder", ServerMoved.Problem(scene.Target(folder: refused)), refused);
            foreach (var accepted in new[] { otherLine, "amberline 7 player", MovedFolder })
                TestAssert.True(ServerMoved.Problem(scene.Target(folder: accepted)) is null, $"{accepted} was refused");
            if (OwnPlayerFolder == "Amberline Player")
                TestAssert.True(ServerMoved.Problem(scene.Target(folder: "Amberline 132 Player")) is null, "the 1.0.0.126 tool refused Amberline 132 Player");
            foreach (var (invalid, why) in new (ServerMoved.Target, string)[]
            {
                (scene.Target(port: 0), "no valid port"), (scene.Target(pin: "ABC"), "no valid certificate pin"),
                (scene.Target() with { Name = "Beta/Gamma" }, "no valid server name"), (scene.Target(folder: "CON"), "no valid player folder"),
                (scene.Target(folder: @"C:\Moved"), "no valid player folder"), (scene.Target() with { PlayerFolder = null }, "no valid player folder"),
            })
                TestAssert.Equal(why, ServerMoved.Problem(invalid));
            TestAssert.True(ServerMoved.Problem(scene.Target()) is null, "a valid movedTo was refused");

            // Start it: this PC's 1.0.0.132 install of Beta. Another port is not it; another host still is.
            var install132 = Path.Combine(root, "Install132");
            var id132 = PlayerInstall.ServerId(scene.NewPin);
            var tool132 = Path.Combine(install132, PlayerInstall.ToolFolder, PlayerInstall.WindowlessName);
            PlayerMode.WriteJson(Path.Combine(folder, "install.json"), new { install = install132, air = Path.Combine(install132, "air"), game = Path.Combine(install132, "game") });
            Directory.CreateDirectory(Path.GetDirectoryName(tool132)!);
            File.WriteAllText(tool132, "stand-in");
            var server132 = Path.Combine(install132, "servers", id132, "server.json");
            PlayerMode.WriteJson(server132, new { host = "elsewhere.example.test", port = scene.NewPort + 1, certificateSha256 = scene.NewPin, name = "Beta" });
            TestAssert.True(ServerMoved.Installed(local, scene.Target()) is null, "an install for another port counted");
            PlayerMode.WriteJson(server132, new { host = "elsewhere.example.test", port = scene.NewPort, certificateSha256 = scene.NewPin, name = "Beta" });
            await using (var standIn = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(scene.Target()), scene.OldArchive))
            {
                ui = new FakeStart { LocalAppData = local, SetUp = true };
                TestAssert.Equal(0, await PlayerMode.StartAsync(scene.Fixture.Install, scene.Id, checkUpdate: true, ui.Seam));
                var question = ui.Moves.Single();
                TestAssert.True(question.Installed, "the 1.0.0.132 install was not found");
                TestAssert.True(question.WhatToDo.StartsWith("It is already set up on this PC. Start it?", StringComparison.Ordinal), question.WhatToDo);
                TestAssert.Equal(tool132, ui.Started.Single().File);
                TestAssert.Equal(new[] { "--shortcut", id132 }, ui.Started.Single().Arguments);
                TestAssert.Equal((0, 0), (ui.Played, ui.Problems.Count));
            }
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-moved-stalled-body", TimeoutSeconds = 120)]
    public static void AServerThatStallsAfterItsHeadersIsLeft() => StalledBodyAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 856a, security review: a server that sends its headers and the start of a body, then nothing. A read with a budget ends after
    /// it (the player tool's 10 minutes; 1 s here); Set up's manifest read ends after its 10 s and says "Can't reach", so the "Setting up"
    /// window never waits for ever, and nothing is written.
    /// </summary>
    private static async Task StalledBodyAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-moved-stalled");
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var port = AirBootstrapChecks.FreePort();
            await using var stalled = await StartStalledAsync(port, certificate);

            var clock = Stopwatch.StartNew();
            using (var client = AirRemoteProxy.CreatePinnedClient(Convert.FromHexString(pin)))
            {
                try
                {
                    await PlayerInstall.GetAsync(client, new Uri($"https://127.0.0.1:{port}/bootstrap/companion.zip"), 512 << 20, CancellationToken.None,
                        TimeSpan.FromSeconds(1));
                    throw new InvalidOperationException("a stalled body was read to its end");
                }
                catch (TaskCanceledException timeout) when (timeout.InnerException is TimeoutException) { }
            }
            TestAssert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the 1 s budget ended after {clock.Elapsed}");

            var local = Path.Combine(root, "profile", "Local");
            var ui = new FakeStart { LocalAppData = local, SetUp = true };
            clock.Restart();
            TestAssert.Equal(1, await ServerMoved.RunAsync("Alpha", "127.0.0.1", new ServerMoved.Target(port, pin, "Beta", MovedFolder),
                Path.Combine(root, "shortcut.log"), ui.Moved));
            TestAssert.True(clock.Elapsed >= TimeSpan.FromSeconds(9) && clock.Elapsed < TimeSpan.FromSeconds(30), $"Set up's manifest read ended after {clock.Elapsed}");
            TestAssert.Equal("Cannot reach Alpha.", ui.Problems.Single().WhatHappened);
            TestAssert.True(ui.Problems.Single().Details.Contains("did not finish answering within 10 seconds", StringComparison.Ordinal), ui.Problems.Single().Details);
            TestAssert.Equal((0, false), (ui.Started.Count, Directory.Exists(Path.Combine(local, MovedFolder))));
        }
        finally { Cleanup(root); }
    }

    /// <summary>A pinned HTTPS listener whose every bootstrap path sends its headers and one byte of a 1 MB body, then nothing.</summary>
    private static async Task<WebApplication> StartStalledAsync(int port, X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/bootstrap/{file}", (RequestDelegate)(async context =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = 1 << 20;
            await context.Response.Body.WriteAsync("{"u8.ToArray());
            await context.Response.Body.FlushAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(60), context.RequestAborted); }
            catch (OperationCanceledException) { }
        }));
        await app.StartAsync();
        return app;
    }

    [Check("server.air-player-moved-none-unchanged", TimeoutSeconds = 300)]
    public static void WithoutMovedToTheStartIsUnchanged() => MovedNoneAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Without <c>movedTo</c> (absent, or null) the start is today's: the update check passes and play starts. A manifest that cannot be read
    /// fails where the update check sits, so an unreachable server with the AIR client gone still shows the missing-client dialog.
    /// </summary>
    private static async Task MovedNoneAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-moved-none");
        try
        {
            using var scene = MovedScene.Make(root);
            var install = scene.Fixture.Install;
            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(scene.OldFolder, scene.OldPort, scene.Old))
            {
                var ui = new FakeStart();
                TestAssert.Equal(0, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
                TestAssert.Equal(new[] { PlayerMode.Checking, PlayerMode.Starting }, ui.Lines.ToArray());
                TestAssert.Equal((1, 0, 0), (ui.Played, ui.Problems.Count, ui.Moves.Count));
            }
            await using (var standIn = await StartStandInAsync(scene.OldPort, scene.Old, scene.Manifest(null), scene.OldArchive))
            {
                var ui = new FakeStart();
                TestAssert.Equal(0, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, ui.Seam));
                TestAssert.Equal((1, 0, 0), (ui.Played, ui.Problems.Count, ui.Moves.Count));
            }

            File.Delete(Path.Combine(scene.Result.Record.Air, "LolClient.exe"));
            var missing = new FakeStart(false);
            TestAssert.Equal(2, await PlayerMode.StartAsync(install, scene.Id, checkUpdate: true, missing.Seam));
            TestAssert.Equal("The AIR client is missing.", missing.Problems.Single().WhatHappened);
            TestAssert.Equal((0, 0), (missing.Played, missing.Moves.Count));
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-moved-released-reader")]
    public static void TheReleasedManifestReaderIgnoresMovedTo()
    {
        // The wire shape of interface 1 (batch 856): the stand-in's manifest, with the fields a Platform's manifest also carries.
        const string pin = "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";
        const string sha = "C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3";
        const string next = "B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2";
        var json = "{\"protocol\":1,\"host\":\"amberline.example.test\",\"port\":8391,\"certificateSha256\":\"" + pin + "\"," +
            "\"companionZip\":\"/bootstrap/companion.zip\",\"companionSha256\":\"" + sha + "\",\"companionVersion\":\"" + sha + "\",\"serverName\":\"Alpha\"," +
            "\"movedTo\":{\"port\":8491,\"certificateSha256\":\"" + next + "\",\"name\":\"Beta\",\"playerFolder\":\"Amberline 132 Player\"}}";
        var released = JsonSerializer.Deserialize<ReleasedManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        TestAssert.Equal(new ReleasedManifest(1, "amberline.example.test", 8391, pin, sha), released);
        var current = JsonSerializer.Deserialize<PlayerInstall.Manifest>(json, WireJson)!;
        TestAssert.Equal(new ServerMoved.Target(8491, next, "Beta", "Amberline 132 Player"), current.MovedTo);
        TestAssert.True(JsonSerializer.Deserialize<PlayerInstall.Manifest>(json.Replace("\"movedTo\":{", "\"other\":{", StringComparison.Ordinal), WireJson)!.MovedTo is null,
            "a manifest without movedTo read one");
        // A malformed movedTo: the released reader still reads the manifest; the current one reads a movedTo that Problem refuses.
        foreach (var malformed in new[] { "\"x\"", "[1]", "{\"port\":\"8491\",\"certificateSha256\":5,\"name\":true}" })
        {
            var odd = json[..json.IndexOf("\"movedTo\":", StringComparison.Ordinal)] + "\"movedTo\":" + malformed + "}";
            TestAssert.Equal(released, JsonSerializer.Deserialize<ReleasedManifest>(odd, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            TestAssert.Equal("no valid port", ServerMoved.Problem(JsonSerializer.Deserialize<PlayerInstall.Manifest>(odd, WireJson)!.MovedTo!));
        }
    }

    /// <summary>
    /// FROZEN: the manifest record every released player tool reads (PlayerInstall.cs from a00305923, 2 November 2026, to 1.0.0.126 release
    /// 0.1.24) with its options, <c>JsonSerializerDefaults.Web</c>. Never change it with the current tool: it stands for tools already on
    /// friends' PCs.
    /// </summary>
    private sealed record ReleasedManifest(int Protocol, string? Host, int Port, string? CertificateSha256, string? CompanionSha256);

    [Check("server.air-player-tool-capabilities")]
    public static void ThePlayerToolsPackageSaysItKnowsMovedTo()
    {
        // Interface 2 (batch 856): capabilities.json, {"movedTo":1}, at the root of the tool's publish output and so of companion.zip.
        var marker = WorkspaceFiles.Find(Path.Combine("src", "Amberline.AirCompanion", "capabilities.json"));
        using (var document = JsonDocument.Parse(File.ReadAllText(marker)))
        {
            var properties = document.RootElement.EnumerateObject().ToArray();
            TestAssert.Equal(1, properties.Length, File.ReadAllText(marker));
            TestAssert.Equal(("movedTo", 1), (properties[0].Name, properties[0].Value.GetInt32()));
        }
        // The line's project publishes it at the root (no folder in its TargetPath).
        var project = File.ReadAllText(WorkspaceFiles.Find(Path.Combine("src", "Amberline.AirCompanion", "Amberline.AirCompanion.csproj")));
        TestAssert.True(Regex.IsMatch(project, @"<None Include=""\$\(HostRoot\)src\\Amberline\.AirCompanion\\capabilities\.json"" TargetPath=""capabilities\.json""[^>]*CopyToPublishDirectory=""PreserveNewest"""),
            "the companion project does not publish capabilities.json at its root");
        // tools/build-server.ps1 zips that publish folder's contents, so the publish root is the archive's root.
        var build = File.ReadAllText(WorkspaceFiles.Find(Path.Combine("tools", "build-server.ps1")));
        TestAssert.True(Regex.IsMatch(build, @"dotnet publish \(Join-Path \$repository 'src/Amberline\.AirCompanion/Amberline\.AirCompanion\.csproj'\)[^\r\n]*`\r?\n[^\r\n]*-o \$companion "),
            "build-server.ps1 no longer publishes the companion into $companion");
        TestAssert.True(build.Contains("[IO.Compression.ZipFile]::CreateFromDirectory($companion, $companionZip, [IO.Compression.CompressionLevel]::Optimal, $false)", StringComparison.Ordinal),
            "build-server.ps1 no longer zips the publish folder's contents into companion.zip");
        // The build output this check runs beside has it too, next to the tool.
        TestAssert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "capabilities.json")), "the build output has no capabilities.json");
    }

    /// <summary>One moved server: Alpha's 1.0.0.126 install (reached as localhost) on the old port, and Beta, its 1.0.0.132 side.</summary>
    private sealed record MovedScene(Fixture Fixture, PlayerInstall.Result Result, string Id, X509Certificate2 Old, int OldPort, string OldFolder,
        string OldSha, X509Certificate2 New, int NewPort, string NewFolder, string NewSha, string Log) : IDisposable
    {
        public string OldPin => Old.GetCertHashString(HashAlgorithmName.SHA256);
        public string NewPin => New.GetCertHashString(HashAlgorithmName.SHA256);
        public byte[] OldArchive => File.ReadAllBytes(Path.Combine(OldFolder, "companion.zip"));

        public ServerMoved.Target Target(string? pin = null, int? port = null, string folder = MovedFolder) => new(port ?? NewPort, pin ?? NewPin, "Beta", folder);

        /// <summary>The old port's manifest as the stand-in sends it (interface 1): this server's own fields, and <c>movedTo</c>.</summary>
        public string Manifest(ServerMoved.Target? moved) => Json(new { protocol = 1, host = "localhost", port = OldPort, certificateSha256 = OldPin,
            companionZip = "/bootstrap/companion.zip", companionSha256 = OldSha, movedTo = moved is null ? null : (object)new { port = moved.Port,
            certificateSha256 = moved.CertificateSha256, name = moved.Name, playerFolder = moved.PlayerFolder } });

        public static MovedScene Make(string root)
        {
            var old = HostFiles.Certificate(Path.Combine(root, "remote126"));
            var next = HostFiles.Certificate(Path.Combine(root, "remote132"));
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var oldPort = AirBootstrapChecks.FreePort();
            var newPort = AirBootstrapChecks.FreePort();
            while (newPort == oldPort) newPort = AirBootstrapChecks.FreePort();
            var pin = old.GetCertHashString(HashAlgorithmName.SHA256);
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("localhost", pin, oldPort), "Alpha", fixture.Tool);
            var served = ServedTool(root, "one");
            File.WriteAllText(Path.Combine(fixture.Install, PlayerInstall.ToolFolder) + ".sha256", served.Sha256);
            var served132 = ServedTool(root, "v132");
            return new(fixture, result, PlayerInstall.ServerId(pin), old, oldPort, served.Folder, served.Sha256, next, newPort, served132.Folder,
                served132.Sha256, Path.Combine(result.Server, "logs", "shortcut.log"));
        }

        public void Dispose()
        {
            Old.Dispose();
            New.Dispose();
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, WireJson);

    /// <summary>This line's own player folder name (Amberline Player on 1.0.0.126, Amberline 132 Player on 1.0.0.132), which movedTo never names.</summary>
    private static string OwnPlayerFolder => Path.GetFileName(FolderMove.PlayerFolder(""));

    /// <summary>A pinned HTTPS listener on loopback with the two bootstrap paths: <paramref name="manifest"/> as it is, and the archive.</summary>
    private static async Task<WebApplication> StartStandInAsync(int port, X509Certificate2 certificate, string manifest, byte[] archive)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/bootstrap/client.json", () => Results.Text(manifest, "application/json"));
        app.MapGet("/bootstrap/companion.zip", () => Results.File(archive, "application/zip", "companion.zip"));
        await app.StartAsync();
        return app;
    }
}
