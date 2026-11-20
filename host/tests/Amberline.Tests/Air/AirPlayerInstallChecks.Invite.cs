using System.Net;
using System.Security.Cryptography;
using Amberline.AirCompanion;
using Amberline.Host;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 917, owner design 20 November 2026): the player app's invite (<see cref="PlayerInvite"/>), which replaced Play.cmd. The
/// link scheme's registration goes through a fake key (<see cref="FakeKey"/>), never the registry; the question, the refusals and the
/// program the invite would start go through the seam, so no window opens and nothing starts. Synthetic profile, install and tools.
/// </summary>
public static partial class AirPlayerInstallChecks
{
    /// <summary>
    /// The downloaded app's first start copies only its own one file into the player folder (never what lay beside it), names the copy after
    /// the line's player folder, and registers the scheme to that copy; a second start from the download, or a start of the copy, writes nothing
    /// new, and a copy that is in use stays.
    /// </summary>
    [Check("server.air-player-invite-first-launch")]
    public static void TheDownloadedAppCopiesOnlyItselfAndRegistersTheScheme() => WithRoot("league-air-invite-first", root =>
    {
        TestAssert.False(PlayerInvite.Bundled, "the checks' own tool has its assembly beside it, as an install's has");
        TestAssert.Equal(InviteLink.Line == "126" ? "Amberline Player.exe" : "Amberline 132 Player.exe", PlayerInvite.CopyName, "the copy's name");
        TestAssert.Equal("\"C:\\A B\\x.exe\" --invite \"%1\"", PlayerInvite.Command(@"C:\A B\x.exe"), "the scheme's open command");
        var key = new FakeKey();
        var profile = new PlayerProfile(Path.Combine(root, "Local"), Path.Combine(root, "Desktop"), Links: key.Seam);
        var downloads = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
        var app = Path.Combine(downloads, "Amberline-Player-1.4.0.exe");
        File.WriteAllText(app, "the app");
        foreach (var beside in new[] { "Play.cmd", "capabilities.json", "Amberline.AirCompanion.dll", "Other.exe" }) File.WriteAllText(Path.Combine(downloads, beside), beside);

        var copy = PlayerInvite.Adopt(profile, app);
        TestAssert.Equal(Path.Combine(profile.PlayerFolder, PlayerInvite.CopyName), copy);
        TestAssert.Equal(new[] { copy }, Directory.GetFiles(profile.PlayerFolder), "only the one file is copied");
        TestAssert.Equal(new[] { Path.Combine(profile.PlayerFolder, "logs") }, Directory.GetDirectories(profile.PlayerFolder), "and its log");
        TestAssert.Equal("the app", File.ReadAllText(copy));
        TestAssert.Equal((PlayerInvite.Command(copy), 1), (key.Command, key.Writes), "the scheme opens the copy");

        // Started again the same way, or the copy itself: the same copy, the scheme as it is.
        TestAssert.Equal(copy, PlayerInvite.Adopt(profile, app));
        TestAssert.Equal(copy, PlayerInvite.Adopt(profile, copy));
        TestAssert.Equal((PlayerInvite.Command(copy), 1), (key.Command, key.Writes), "an unchanged scheme is not written again");
        // A newer download replaces the copy; one that is running (its window open) stays as it is.
        var newer = Path.Combine(downloads, "Amberline-Player-1.4.1.exe");
        File.WriteAllText(newer, "the newer app");
        PlayerInvite.Adopt(profile, newer);
        TestAssert.Equal("the newer app", File.ReadAllText(copy));
        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            TestAssert.Equal(copy, PlayerInvite.Adopt(profile, app));
        TestAssert.Equal("the newer app", File.ReadAllText(copy));
        // A scheme that names another program is pointed at the copy (the first start always takes it).
        key.Command = PlayerInvite.Command(newer);
        PlayerInvite.Adopt(profile, app);
        TestAssert.Equal(PlayerInvite.Command(copy), key.Command);

        // A profile without a key (every other check's) registers nothing.
        PlayerInvite.Adopt(profile with { Links = null, LocalAppData = Path.Combine(root, "Other") }, app);
        TestAssert.Equal(PlayerInvite.Command(copy), key.Command);
    });

    [Check("server.air-player-invite-asks-first", TimeoutSeconds = 300)]
    public static void ALinkAddsNothingBeforeYes() => InviteAsksFirstAsync().GetAwaiter().GetResult();

    /// <summary>
    /// A link launch: a damaged invite or one for the other line is refused in plain words; a valid one asks "Add &lt;name&gt; (&lt;host&gt;)?" and
    /// No writes nothing at all (not even the log) and downloads nothing, while the server is up. Yes does what Play.cmd did: the pinned
    /// manifest and the tool (a wrong pin is refused, nothing written), the player folder's companion and server.json with the host the
    /// manifest advises, and the tool's <c>--install</c>; the same tool is kept, a new one downloaded. The Platform no longer serves
    /// <c>/bootstrap/Play.cmd</c>.
    /// </summary>
    private static async Task InviteAsksFirstAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-invite-asks");
        try
        {
            using var certificate = HostFiles.Certificate(Path.Combine(root, "remote"));
            using var stranger = HostFiles.Certificate(Path.Combine(root, "stranger"));
            var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var port = AirBootstrapChecks.FreePort();
            var key = new FakeKey();
            var profile = new PlayerProfile(Path.Combine(root, "Local"), Path.Combine(root, "Desktop"), Links: key.Seam);
            var invite = new InviteLink.Invite(InviteLink.Tag(new Version(1, 4, 0)), "localhost", port, pin, "Alpha");
            var link = InviteLink.Link(invite);
            var companion = Path.Combine(profile.PlayerFolder, PlayerInstall.ToolFolder);
            var served = ServedTool(root, "one");

            // Refused: nothing is asked, nothing written.
            var ui = new FakeInvite(profile, yes: true);
            TestAssert.Equal(1, await PlayerInvite.RunAsync("https://amberline-dev.github.io/Project-Amberline/join/#bm90IGFuIGludml0ZQ", ui.Seam));
            var other = InviteLink.Line == "126" ? "132" : "126";
            var otherInvite = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(
                $$"""{"v":1,"line":"{{other}}","tag":"v1.4.0","host":"localhost","port":{{port}},"pin":"{{pin}}","name":"Alpha"}"""));
            TestAssert.Equal(1, await PlayerInvite.RunAsync(otherInvite, ui.Seam));
            TestAssert.Equal(new[] { InviteLink.Damaged, InviteLink.OtherLine(other) }, ui.Refusals.ToArray());
            TestAssert.Equal((0, 0, 0), (ui.Asked.Count, ui.Lines.Count, ui.Started.Count));
            TestAssert.False(Directory.Exists(profile.LocalAppData), "a refused invite wrote the profile");

            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(served.Folder, port, certificate))
            {
                using (var http = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, offered, _, _) => offered?.GetCertHashString(HashAlgorithmName.SHA256) == pin,
                }))
                {
                    TestAssert.Equal(HttpStatusCode.OK, (await http.GetAsync($"https://127.0.0.1:{port}/bootstrap/client.json")).StatusCode, "the manifest");
                    TestAssert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"https://127.0.0.1:{port}/bootstrap/Play.cmd")).StatusCode,
                        "batch 917: no Play.cmd is served");
                }

                // No: nothing at all, though the server is up.
                ui = new FakeInvite(profile, yes: false);
                TestAssert.Equal(1, await PlayerInvite.RunAsync(link, ui.Seam));
                TestAssert.Equal(invite, ui.Asked.Single());
                TestAssert.Equal((0, 0, 0), (ui.Lines.Count, ui.Started.Count, ui.Problems.Count));
                TestAssert.False(Directory.Exists(profile.LocalAppData), "No wrote the profile");
                TestAssert.Equal(((string?)null, 0), (key.Command, key.Writes));

                // Yes, each accepted form: the tool's --install with the connection the manifest advises (127.0.0.1; the link names localhost).
                foreach (var form in new[] { link, $"{InviteLink.Scheme}:{InviteLink.Encode(invite)}" })
                {
                    ui = new FakeInvite(profile, yes: true);
                    TestAssert.Equal(0, await PlayerInvite.RunAsync(form, ui.Seam));
                    var connection = Path.Combine(profile.PlayerFolder, "server.json");
                    TestAssert.Equal($"{Path.Combine(companion, "Amberline.AirCompanion.exe")}|--install|--connection|{connection}|--server-name|Alpha",
                        string.Join("|", ui.Started.Select(start => start.File).Concat(ui.Started.Single().Arguments)), "the server's tool sets up the server");
                    var written = Amberline.AirCompanion.Program.ReadRemoteInvitation(connection);
                    TestAssert.Equal(("127.0.0.1", port, pin), (written.Host, written.Port, written.CertificateSha25664hex));
                    TestAssert.Equal(served.Sha256, File.ReadAllText(companion + ".sha256"));
                    TestAssert.Equal("one", Version(companion));
                    TestAssert.Equal(new[] { PlayerInvite.Connecting("Alpha") }, ui.Lines.ToArray());
                    File.WriteAllText(Path.Combine(companion, "kept.txt"), "kept");
                }
                TestAssert.True(File.Exists(Path.Combine(companion, "kept.txt")), "an unchanged tool was downloaded again");
                TestAssert.Equal(((string?)null, 0), (key.Command, key.Writes), "the invite registers no scheme (the first start and the shortcut do)");
            }

            // A newer tool on the server is downloaded; another certificate on the port is refused, and nothing is written.
            var newer = ServedTool(root, "two");
            await using (var platform = await AirPlayFileChecks.StartPlatformAsync(newer.Folder, port, certificate))
            {
                ui = new FakeInvite(profile, yes: true);
                TestAssert.Equal(0, await PlayerInvite.RunAsync(link, ui.Seam));
                TestAssert.Equal("two", Version(companion));
                TestAssert.False(File.Exists(Path.Combine(companion, "kept.txt")), "the old tool's folder was kept");
            }
            File.Delete(Path.Combine(profile.PlayerFolder, "server.json"));
            await using (var wrong = await AirPlayFileChecks.StartPlatformAsync(ServedTool(root, "three").Folder, port, stranger))
            {
                ui = new FakeInvite(profile, yes: true, false);
                TestAssert.Equal(1, await PlayerInvite.RunAsync(link, ui.Seam));
                var problem = ui.Problems.Single();
                TestAssert.Equal("Alpha did not prove it is the server this shortcut was made for.", problem.WhatHappened);
                TestAssert.Equal("Ask the host for a new invite link and open it.", problem.WhatToDo);
                TestAssert.False(problem.Details.Contains("localhost", StringComparison.OrdinalIgnoreCase), problem.Report());
                TestAssert.Equal(("two", 0, false), (Version(companion), ui.Started.Count, File.Exists(Path.Combine(profile.PlayerFolder, "server.json"))));
            }
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-invite-shortcut-registers", TimeoutSeconds = 300)]
    public static void AnInstallsStartRegistersTheScheme() => ShortcutRegistersAsync().GetAwaiter().GetResult();

    /// <summary>
    /// An install made with Play.cmd: its shortcut's start registers the scheme to the install's windowless tool (also when the server is not
    /// reached); a scheme whose program is there (the app's own copy) stays; one whose program is gone is replaced. A start without a profile
    /// (every other check's) registers nothing.
    /// </summary>
    private static async Task ShortcutRegistersAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-invite-shortcut");
        try
        {
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var port = AirBootstrapChecks.FreePort();
            PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", PinA, port), "Alpha", fixture.Tool);
            var id = PlayerInstall.ServerId(PinA);
            var tool = Path.Combine(fixture.Install, PlayerInstall.ToolFolder, PlayerInstall.WindowlessName);
            TestAssert.True(File.Exists(tool), "the install's windowless tool");
            var key = new FakeKey();
            var profile = fixture.Profile with { Links = key.Seam };

            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, new FakeStart(false).Seam));
            TestAssert.Equal(((string?)null, 0), (key.Command, key.Writes), "a start without a profile registered the scheme");
            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, new FakeStart(false).Seam, profile));
            TestAssert.Equal((PlayerInvite.Command(tool), 1), (key.Command, key.Writes), "the start registered the scheme to the install's tool");
            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, new FakeStart(false).Seam, profile));
            TestAssert.Equal(1, key.Writes, "an unchanged scheme is not written again");

            var copy = PlayerInvite.Adopt(profile, WriteApp(root));
            TestAssert.Equal(PlayerInvite.Command(copy), key.Command);
            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, new FakeStart(false).Seam, profile));
            TestAssert.Equal(PlayerInvite.Command(copy), key.Command, "the app's own copy keeps the scheme");
            File.Delete(copy);
            TestAssert.Equal(2, await PlayerMode.StartAsync(fixture.Install, id, checkUpdate: true, new FakeStart(false).Seam, profile));
            TestAssert.Equal(PlayerInvite.Command(tool), key.Command, "a scheme whose program is gone is replaced");
        }
        finally { Cleanup(root); }
    }

    /// <summary>Remove Amberline takes the scheme and the app's own copy too; removing one server keeps both.</summary>
    [Check("server.air-player-uninstall-removes-link", TimeoutSeconds = 300)]
    public static void RemovingAmberlineRemovesTheScheme() => WithRoot("league-air-invite-uninstall", root =>
    {
        var fixture = Fixture.Make(Path.Combine(root, "player"));
        var key = new FakeKey();
        var profile = fixture.Profile with { Links = key.Seam };
        var alpha = PlayerInstall.Install(profile, fixture.Plan(), Connection("one.example.test", PinA), "Alpha", fixture.Tool);
        PlayerInstall.AddServer(profile, alpha.Record, Connection("two.example.test", PinB), "Bravo");
        var copy = PlayerInvite.Adopt(profile, WriteApp(root));
        PlayerUninstall.RemoveServer(profile, PlayerUninstall.Servers(alpha.Record.Install)[0]);
        TestAssert.Equal((PlayerInvite.Command(copy), 0, true), (key.Command, key.Deletes, File.Exists(copy)), "removing one server keeps the link");
        PlayerUninstall.RemoveAll(profile, alpha.Record, deleteGame: false);
        TestAssert.Equal(((string?)null, 1), (key.Command, key.Deletes), "Remove Amberline removes the scheme");
        TestAssert.False(File.Exists(copy) || Directory.Exists(profile.PlayerFolder), "the app's copy (and its emptied folder) is left");
    });

    /// <summary>A downloaded app stand-in in its own folder.</summary>
    private static string WriteApp(string root)
    {
        var app = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName, "Amberline-Player-1.4.0.exe");
        File.WriteAllText(app, "the app");
        return app;
    }

    /// <summary>The link scheme's registration in memory (<see cref="PlayerInvite.SchemeKey"/>): the command, and how often it was written and removed.</summary>
    private sealed class FakeKey
    {
        public string? Command { get; set; }
        public int Writes { get; private set; }
        public int Deletes { get; private set; }

        public PlayerInvite.SchemeKey Seam => new(() => Command, value =>
        {
            Command = value;
            Writes++;
        }, () =>
        {
            Command = null;
            Deletes++;
        });
    }

    /// <summary>The invite's seam: the question's answer, then Try again's answers in order; what it asked, refused, showed and would start.</summary>
    private sealed class FakeInvite(PlayerProfile profile, bool yes, params bool[] again)
    {
        private readonly Queue<bool> again = new(again);

        public PlayerInvite.Ui Seam => new(profile, Lines.Add, invite =>
        {
            Asked.Add(invite);
            return yes;
        }, Refusals.Add, problem =>
        {
            Problems.Add(problem);
            return again.TryDequeue(out var answer) && answer;
        }, (file, arguments) => Started.Add((file, [.. arguments])));

        public List<InviteLink.Invite> Asked { get; } = [];
        public List<string> Refusals { get; } = [];
        public List<string> Lines { get; } = [];
        public List<PlayerProblem> Problems { get; } = [];
        public List<(string File, string[] Arguments)> Started { get; } = [];
    }
}
