using System.Security.Cryptography;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Host;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 923; both lines since batch 926): the 1.0.0.132 game runs loose, and its spell index then needs 8.3 short names on its files
///, which Windows gives by default on the system drive only. The profile's two seams answer per folder:
/// <see cref="PlayerProfile.SystemDrive"/> (is it on the system drive) and <see cref="PlayerProfile.ShortNames"/> (do its files have short names), so no
/// real drive without them is needed. Every check runs on both lines: new installs go on the system drive on both (<see cref="CompanionLine.SystemDriveInstall"/>),
/// and only 1.0.0.132 repairs a start's game (<see cref="CompanionLine.ShortNameStartRepair"/>): the repair checks ask nothing on 1.0.0.126.
/// </summary>
public static partial class AirPlayerInstallChecks
{
    [Check("server.air-player-install-system-drive", TimeoutSeconds = 300)]
    public static void NewInstallsGoOnTheSystemDriveWithShortNames() => WithRoot("league-air-install-system-drive", root =>
    {
        TestAssert.True(CompanionLine.SystemDriveInstall, "the new installs go on the system drive on both lines");

        // The real probe (no seam): a folder not made yet is asked through its nearest parent; it answers, and leaves nothing behind.
        var probed = GameFolderReport.NewShortNames(Path.Combine(root, "not", "made"));
        TestAssert.True(probed is "on" or "off", "the probe answered " + probed);
        TestAssert.Equal(0, Directory.GetDirectories(root, "amberline-short-name-probe-*").Length, "the probe's folder is left");
        TestAssert.False(Directory.Exists(Path.Combine(root, "not")), "the probe made the folder it asked about");

        // A copy found on another drive: copied into the install on the system drive, every file checked, the originals then removed (the cross-drive
        // move as before); never renamed in, on either line (batch 926).
        var elsewhere = Fixture.Make(Path.Combine(root, "elsewhere"));
        var downloads = Path.GetDirectoryName(elsewhere.Air)!;
        var profile = elsewhere.Profile with { SystemDrive = folder => !FolderMove.Inside(folder, downloads) };
        var plan = PlayerInstall.MakePlan(profile, elsewhere.Air, elsewhere.Game, elsewhere.Install, elsewhere.Pins);
        var game = plan.Moves.Single(move => move.Label == "game client");
        TestAssert.Equal((true, Path.Combine(elsewhere.Install, PlayerInstall.GameRelease)), (game.Copy, plan.Game), "a game found on another drive");
        TestAssert.True(plan.CopyBytes >= FolderMove.CopyBytes(game) && plan.CopyBytes > 0, "the plan counts the game's copy");
        var before = elsewhere.Snapshot();
        var copied = new List<string>();
        TestAssert.Equal(0, PlayerInstall.Execute(plan, null, new(AfterCopy: copied.Add)).Count);
        TestAssert.Equal(Directory.EnumerateFiles(plan.Game, "*", SearchOption.AllDirectories).Count(),
            copied.Count(path => FolderMove.Inside(path, plan.Game + ".partial")), "the game's files were copied, not renamed");
        elsewhere.AssertMoved(plan, before);

        // A found copy on the system drive whose files have no short names: copied too (a rename keeps files without them). Both lines (batch 926).
        var bare = Fixture.Make(Path.Combine(root, "bare"));
        profile = bare.Profile with { ShortNames = folder => FolderMove.Same(folder, bare.Game) ? "off" : "on" };
        plan = PlayerInstall.MakePlan(profile, bare.Air, bare.Game, bare.Install, bare.Pins);
        TestAssert.True(plan.Moves.Single(move => move.Label == "game client").Copy, "a game without short names on the system drive");
        before = bare.Snapshot();
        TestAssert.Equal(0, PlayerInstall.Execute(plan).Count);
        bare.AssertMoved(plan, before);

        // A found copy on the system drive with short names (or no answer): today's plan, renames only.
        var fine = Fixture.Make(Path.Combine(root, "fine"));
        foreach (var answer in new[] { "on", "unknown" })
        {
            plan = PlayerInstall.MakePlan(fine.Profile with { ShortNames = _ => answer }, fine.Air, fine.Game, fine.Install, fine.Pins);
            TestAssert.Equal((0L, false), (plan.CopyBytes, plan.Moves.Any(move => move.Copy)), $"a game that can start where it is ({answer})");
        }

        // The install folder on another drive: refused with the owner's one line (setup's plan, the client zip, the download, Change…); a drive
        // whose new files get no short names: one message saying what to do. Both lines (batch 926).
        var drive = fine.Profile.SystemDriveName;
        var otherDrive = $"The game must be installed on drive {drive}, where Windows can run it.";
        var away = fine.Profile with { SystemDrive = folder => !FolderMove.Inside(folder, fine.Install) };
        var none = fine.Profile with { ShortNames = folder => FolderMove.Inside(folder, fine.Install) ? "off" : "on" };
        var offer = new PlayerDownload.Offer([new("game/League of Legends.exe", 1, new string('0', 64)), new("air/LolClient.exe", 1, new string('0', 64))], 2);
        Refused(() => PlayerInstall.MakePlan(away, fine.Air, fine.Game, fine.Install, fine.Pins), otherDrive);
        // Batch 926 review: only the loose 1.0.0.132 game needs short names; 1.0.0.126's install is never refused for them.
        void NotForShortNames(Action act)
        {
            try { act(); }
            catch (InstallRefusedException refused) { TestAssert.False(refused.Message == PlayerInstall.NoShortNamesAnywhere(none), "1.0.0.126 refused for short names"); }
        }
        if (CompanionLine.ShortNameStartRepair)
        {
            Refused(() => PlayerInstall.MakePlan(none, fine.Air, fine.Game, fine.Install, fine.Pins), PlayerInstall.NoShortNamesAnywhere(none));
            TestAssert.True(PlayerInstall.NoShortNamesAnywhere(none).Contains("fsutil 8dot3name set 0", StringComparison.Ordinal), "the message says what to do");
        }
        else NotForShortNames(() => PlayerInstall.MakePlan(none, fine.Air, fine.Game, fine.Install, fine.Pins));
        Refused(() => PlayerInstall.InstallFromZip(away, Path.Combine(root, "no-such.zip"), fine.Install, fine.Pins, null, Connection("one.example.test", PinA),
            "Alpha", fine.Tool), otherDrive);
        TestAssert.False(Directory.Exists(ClientZip.StagingFor(fine.Install)), "the zip was unpacked before the refusal");
        Refused(() => PlayerDownload.Plan(fine.Install, offer, null, away, null, fine.Pins), otherDrive);
        if (CompanionLine.ShortNameStartRepair) Refused(() => PlayerDownload.Plan(fine.Install, offer, null, none, null, fine.Pins), PlayerInstall.NoShortNamesAnywhere(none));
        else NotForShortNames(() => PlayerDownload.Plan(fine.Install, offer, null, none, null, fine.Pins));
        TestAssert.Equal(otherDrive, PlayerInstall.InstallFolderProblem(away, fine.Install));
        TestAssert.Equal(null, PlayerInstall.InstallFolderProblem(fine.Profile, fine.Install), "a folder on the system drive");

        // A server's game on this PC on another drive: copied in with its language files, the server's folders untouched. Both lines (batch 926); 1.0.0.126 used it in place before.
        var hosted = Fixture.Make(Path.Combine(root, "hosted"));
        var settings = hosted.Profile.HostSettings;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, JsonSerializer.Serialize(new Dictionary<string, string> { ["dataRoot"] = Path.GetDirectoryName(hosted.Game)! }));
        profile = hosted.Profile with { SystemDrive = folder => !FolderMove.Inside(folder, Path.GetDirectoryName(hosted.Air)!) };
        plan = PlayerInstall.MakePlan(profile, hosted.Air, hosted.Game, hosted.Install, hosted.Pins);
        TestAssert.Equal(new[] { "game client", "game's language files" }, plan.Moves.Where(move => move.KeepSource).Select(move => move.Label).ToArray(),
            "the server's game and its language files are copied");
        TestAssert.Equal(Path.Combine(hosted.Install, PlayerInstall.GameRelease), plan.Game);
        before = hosted.Snapshot();
        TestAssert.Equal(0, PlayerInstall.Execute(plan).Count);
        foreach (var (path, hash) in before.Where(pair => !FolderMove.Inside(pair.Key, hosted.Air)))
            TestAssert.Equal(hash, Fixture.Hash(path), $"the server's {path} changed");
        TestAssert.True(File.Exists(Path.Combine(plan.Game, "League of Legends.exe")) &&
            File.Exists(Path.Combine(hosted.Install, "game", "projects", "lol_game_client_en_us", "releases", "0.0.0.20", "DATA", "Menu", "fontconfig_en_US.txt")),
            "the copy and its language files are in the install");
    });

    [Check("server.air-player-shortcut-short-names-repair", TimeoutSeconds = 300)]
    public static void TheStartMovesAGameWithoutShortNamesOnceAndNeverLoops() => ShortNamesRepairAsync().GetAwaiter().GetResult();

    /// <summary>
    /// An install whose game files have no short names, on another drive (the friend's F:\vidya\Amberline 132\Amberline 132), started from its desktop
    /// shortcut's finishing run (the released tool's first start after its update): Cancel plays nothing; Move copies the game into the player's own folder
    /// on the system drive, the record, the server's stage and its shortcuts name it, and the start plays. A game that still has none after the move, or
    /// on the system drive, gets one message and never the move again; the removal with the game files takes the moved game. 1.0.0.126: nothing is asked.
    /// </summary>
    private static async Task ShortNamesRepairAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-short-names-repair");
        try
        {
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var pin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", pin, AirBootstrapChecks.FreePort()), "Alpha", fixture.Tool);
            var id = PlayerInstall.ServerId(pin);
            var install = result.Record.Install;
            var oldGame = result.Record.Game;
            var moved = Path.Combine(fixture.Profile.PlayerFolder, PlayerInstall.GameRelease);
            // The install is on another drive and its files have none; the system drive gives them (the player's folder).
            var friend = fixture.Profile with
            {
                SystemDrive = folder => !FolderMove.Inside(folder, install),
                ShortNames = folder => FolderMove.Inside(folder, install) ? "off" : "on",
            };

            // Cancel: the question names the drive, the size and the free space; nothing moves and nothing plays.
            var ui = new FakeStart();
            var exit = await PlayerMode.StartAsync(install, id, checkUpdate: false, ui.Seam, friend);
            if (!CompanionLine.ShortNameStartRepair)
            {
                TestAssert.Equal((0, 0, 1), (exit, ui.GameQuestions.Count, ui.Played), "1.0.0.126 asks nothing and plays");
                return;
            }
            TestAssert.Equal(2, exit);
            var (question, canMove) = ui.GameQuestions.Single();
            TestAssert.Equal((PlayerMode.CantStartHere, true), (question.WhatHappened, canMove));
            TestAssert.True(question.WhatToDo.StartsWith($"Move it to drive {friend.SystemDriveName}? The game needs ", StringComparison.Ordinal) &&
                question.WhatToDo.Contains(" GB free.", StringComparison.Ordinal), question.WhatToDo);
            TestAssert.Equal((0, oldGame), (ui.Played, PlayerInstall.ReadRecord(friend)!.Game), "Cancel moved or played");
            TestAssert.True(File.Exists(Path.Combine(oldGame, "League of Legends.exe")), "Cancel touched the game");

            // Move: the game and its language files are in the player's folder, checked; the originals are gone; the record, the stage and both
            // shortcuts name the new game; then the start plays.
            var before = Directory.EnumerateFiles(oldGame, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(oldGame, path), Fixture.Hash);
            ui = new FakeStart { MoveGame = true };
            TestAssert.Equal(0, await PlayerMode.StartAsync(install, id, checkUpdate: false, ui.Seam, friend));
            TestAssert.Equal((1, 1), (ui.GameQuestions.Count, ui.Played));
            TestAssert.True(ui.Lines.Contains(PlayerMode.Moving(friend.SystemDriveName)), string.Join(" | ", ui.Lines));
            TestAssert.Equal(moved, PlayerInstall.ReadRecord(friend)!.Game, "the record names the moved game");
            foreach (var (relative, hash) in before) TestAssert.Equal(hash, Fixture.Hash(Path.Combine(moved, relative)), $"the moved {relative}");
            TestAssert.False(Directory.Exists(oldGame), "the old copy is left");
            TestAssert.True(Directory.Exists(Path.Combine(fixture.Profile.PlayerFolder, "game", "projects", "lol_game_client_en_us")), "the language files did not move");
            TestAssert.Equal(Path.Combine(moved, "League of Legends.exe"),
                PlayerInstall.Text(PlayerInstall.ReadObject(Path.Combine(result.Server, "stage.json")), "stockExecutable"), "the stage names the moved game");
            TestAssert.Equal(Path.Combine(moved, "League of Legends.exe"), ShellLink.Read(result.Shortcut).IconPath, "the desktop shortcut's icon");
            TestAssert.True(PlayerInstall.IsOurShortcut(result.Shortcut, id), "the desktop shortcut");

            // The next start: the moved game has short names, so nothing is asked. A second server keeps the install and the moved game where they are.
            ui = new FakeStart();
            TestAssert.Equal(0, await PlayerMode.StartAsync(install, id, checkUpdate: false, ui.Seam, friend));
            TestAssert.Equal((0, 1), (ui.GameQuestions.Count, ui.Played));
            var kept = PlayerInstall.ExistingInstall(friend, fixture.Pins)!;
            var second = PlayerInstall.MakePlan(friend, kept.Air, kept.Game, kept.Install, fixture.Pins);
            TestAssert.Equal((0, moved), (second.Moves.Count, second.Game), "a second server moved the game again");

            // Never a loop: files on the system drive without short names (the drive gives them, or gives none) get one message, never the move.
            foreach (var home in new[] { "on", "off" })
            {
                ui = new FakeStart { MoveGame = true };
                var bare = friend with { ShortNames = folder => FolderMove.Same(folder, PlayerInstall.GameHome(friend)) ? home : "off" };
                TestAssert.Equal(2, await PlayerMode.StartAsync(install, id, checkUpdate: false, ui.Seam, bare));
                var (message, offered) = ui.GameQuestions.Single();
                TestAssert.Equal((PlayerMode.CantStart, false, 0), (message.WhatHappened, offered, ui.Played));
                TestAssert.True(message.WhatToDo.Contains("Uninstall.cmd", StringComparison.Ordinal) &&
                    message.WhatToDo.Contains("fsutil", StringComparison.Ordinal) == (home == "off"), message.WhatToDo);
                TestAssert.Equal(moved, PlayerInstall.ReadRecord(friend)!.Game, "the message moved the game");
            }

            // Removing Amberline with its game files takes the moved game too; kept, it is listed as kept.
            var record = PlayerInstall.ReadRecord(friend)!;
            TestAssert.True(PlayerUninstall.GameFolders(record).Contains(PlayerInstall.GameHome(friend)), "the moved game is the install's own");
            var removal = PlayerUninstall.RemoveAll(friend, record, deleteGame: true, runningTool: Path.Combine(install, PlayerInstall.ToolFolder));
            TestAssert.Equal(0, removal.Left.Count, string.Join(", ", removal.Left));
            TestAssert.False(Directory.Exists(PlayerInstall.GameHome(friend)), "the moved game is left");
        }
        finally { Cleanup(root); }
    }

    [Check("server.air-player-shortcut-short-names-still-none", TimeoutSeconds = 300)]
    public static void AMoveThatLeavesNoShortNamesSaysSoOnce() => StillNoneAsync().GetAwaiter().GetResult();

    /// <summary>
    /// The system drive's probe answers that it gives short names, but the moved files still have none: one message after the move, and the next start
    /// offers no move again (the game is on the system drive now).
    /// </summary>
    private static async Task StillNoneAsync()
    {
        if (!OperatingSystem.IsWindows() || !CompanionLine.ShortNameStartRepair) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-short-names-none");
        try
        {
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var pin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("127.0.0.1", pin, AirBootstrapChecks.FreePort()), "Alpha", fixture.Tool);
            var id = PlayerInstall.ServerId(pin);
            var install = result.Record.Install;
            var home = PlayerInstall.GameHome(fixture.Profile);
            var friend = fixture.Profile with
            {
                SystemDrive = folder => !FolderMove.Inside(folder, install),
                ShortNames = folder => FolderMove.Same(folder, home) ? "on" : "off",
            };
            var ui = new FakeStart { MoveGame = true };
            TestAssert.Equal(2, await PlayerMode.StartAsync(install, id, checkUpdate: false, ui.Seam, friend));
            TestAssert.Equal(new[] { (PlayerMode.CantStartHere, true), (PlayerMode.CantStart, false) },
                ui.GameQuestions.Select(asked => (asked.Problem.WhatHappened, asked.CanMove)).ToArray());
            TestAssert.True(ui.GameQuestions[1].Problem.WhatToDo.Contains("fsutil 8dot3name set 0", StringComparison.Ordinal), ui.GameQuestions[1].Problem.WhatToDo);
            TestAssert.Equal(0, ui.Played);
            ui = new FakeStart { MoveGame = true };
            TestAssert.Equal(2, await PlayerMode.StartAsync(install, id, checkUpdate: false, ui.Seam, friend));
            TestAssert.Equal((PlayerMode.CantStart, false), (ui.GameQuestions.Single().Problem.WhatHappened, ui.GameQuestions.Single().CanMove));
        }
        finally { Cleanup(root); }
    }
}
