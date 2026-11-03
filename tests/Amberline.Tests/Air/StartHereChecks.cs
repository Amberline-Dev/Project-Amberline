using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;

namespace Amberline.Tests.Air;

/// <summary>
/// DIAGNOSTICS: the START-HERE guide (batch 633): <c>START-HERE.cmd</c> and <c>tools/guide.ps1</c> run with <c>-DryRun</c> and injected
/// answers. The home path runs in Windows PowerShell 5.1 (the shell the .cmd starts), the rented path in PowerShell 7, so both
/// parsers read the guide. Stand-ins for winget, pwsh, dotnet and git come first on the child's PATH and note every call: a dry
/// run must call none of them, so nothing is installed or built. The guide's answers and logs go to a temporary folder.
/// Batch 635: the game search walks only the temporary trees given with <c>-SearchRoots</c>, never this PC's drives. Every run
/// gets a made-up account and PC name, which the guide's log must never hold. Batch 637: every run gets a temporary
/// <c>LOCALAPPDATA</c>, so the guide never reads this PC's install pointer. Batch 645: the window's lines wrap at 78 columns, so the
/// checks compare texts with every run of spaces and line ends as one space.
/// </summary>
public static class StartHereChecks
{
    private const string FakeAccount = "FakeAccount7";
    private const string FakePc = "FAKEPC7";

    [Check("server.start-here-dry-run", TimeoutSeconds = 180)]
    public static void DryRunPrintsThePlanAndRunsNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = File.ReadAllBytes(AirBootstrapChecks.FindWorkspaceFile("START-HERE.cmd"));
        TestAssert.True(start.All(b => b is < 0x80 and not 0), "START-HERE.cmd must be plain ASCII");
        var startText = Encoding.ASCII.GetString(start);
        TestAssert.True(startText.EndsWith("\r\n", StringComparison.Ordinal) && !startText.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n')
            && !startText.Replace("\r\n", "", StringComparison.Ordinal).Contains('\r'), "START-HERE.cmd must have CRLF line ends");
        // Batch 645: the last line says what the key does. Batch 653: in the word list's words.
        foreach (var part in new[] { "title Amberline setup", @"Sysnative\WindowsPowerShell\v1.0\powershell.exe", @"tools\guide.ps1", "pause >nul",
            "START-HERE.cmd in that folder", "Press Enter to close." })
            TestAssert.True(startText.Contains(part, StringComparison.Ordinal), $"START-HERE.cmd lacks {part}");

        var guide = AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "guide.ps1"));
        var windowsPowerShell = AirBootstrapChecks.FindWindowsPowerShell() ?? throw new InvalidOperationException("Windows PowerShell 5.1 not found");
        var pwsh = FindOnPath("pwsh.exe") ?? throw new InvalidOperationException("pwsh not found");
        var root = AirBootstrapChecks.TemporaryDirectory("league-start-here");
        try
        {
            var standIns = StandIns(root);
            var called = Path.Combine(standIns, "called.txt");
            // The made-up names in a folder name: the screen shows them, the log masks them.
            var game = Path.Combine(root, $"game of {FakeAccount} on {FakePc}");
            var air = Path.Combine(root, "my air client");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(air);
            var state = Path.Combine(root, "state");
            var nothing = Path.Combine(root, "empty search");
            Directory.CreateDirectory(nothing);
            string[] search = ["-SearchRoots", nothing];

            // This PC, PowerShell 5.1: Zig missing, offered and "installed" on a yes. Batch 634: the guide needs the .NET SDK,
            // PowerShell 7 and Zig only (Zig builds the Windows natives too); Git, LLVM and the C++ Build Tools are neither looked for
            // nor offered.
            // Batch 637: step 3 asks where Amberline goes and whether the originals go; the release's first start gets both.
            var installFolder = Path.Combine(root, "Amberline home");
            var home = Run(windowsPowerShell, guide, state, standIns, root, "home",
                new { where = "1", gameRoot = game, airRoot = air, installFolder, deleteOriginals = "Y", install = "Y", missing = new[] { "zig" } }, search);
            TestAssert.Equal(0, home.ExitCode, home.Output);
            // Batch 645: the questions on their own line, then a prompt that says what to type; the summary at the end. Batch 653: the
            // word list's yes or no, shorter words, and a line before every wait (the search, the search inside a picked folder).
            AssertHas(home.Output, "home plan", "DRY RUN", "Step 1 of 6", "Step 3 of 6: Where Amberline goes", "Step 4 of 6: Build tools", "Step 6 of 6",
                "SDK: found.", "PowerShell 7: found.", "Missing: Zig (it builds two small native parts).",
                "Install Zig now? This accepts its license. Type Y for yes or N for no. Y", "would install it with: winget install --id zig.zig --exact",
                "--accept-package-agreements --accept-source-agreements", "Done: all the tools are here.", "-Layout 'Home'", $"-GameRoot '{game}'", $"-AirRoot '{air}'",
                "Start Amberline.cmd", "Searching this PC for them. This takes at most 60 seconds. Press S to skip.", "Looked at 1 folder.",
                "Game folder (patch 1.0.0.126, with DATA and LEVELS): None found. Choose it yourself.",
                "Press Enter to choose it in a window, or paste its path: ", "Looking inside that folder (at most 60 seconds).",
                $"Your game folder: {game}", $"Your AIR client folder: {air}",
                $"Where should Amberline be installed? It keeps everything in that folder. Type a folder, or press Enter for C:\\Amberline: {installFolder}",
                $"Install folder: {installFolder}", $"Amberline copies these into the install folder: {game} {air}", " has ", " GB free.",
                "Delete the originals? Type Y for yes or N for no. Y",
                $"--install-to \"{installFolder}\" --game \"{game}\" --air \"{air}\" --delete-originals",
                $"Summary Install folder: {installFolder} Now: a new window named Amberline copies the program, the game and the AIR client into that folder.",
                "Once the copies are checked, it deletes the originals. Then its page opens in your web browser.",
                "On that page: set up your server, then choose Invite a friend for each friend.", "From now on: start Amberline from its desktop icon.",
                "double-click START-HERE.cmd again. Log:", "DRY RUN finished");
            AssertPlain(home.Output, root);
            foreach (var gone in new[] { "Git:", "needs Git,", "LLVM", "Build Tools" })
                TestAssert.False(home.Output.Contains(gone, StringComparison.Ordinal), $"the guide still mentions {gone}:\n{home.Output}");
            TestAssert.True(Regex.IsMatch(home.Output, @"-Version '0\.0\.\d+'"), "home plan lacks the source-build version 0.0.N:\n" + home.Output);
            TestAssert.False(home.Output.Contains("-PublicHost", StringComparison.Ordinal), "a home build takes no -PublicHost:\n" + home.Output);
            TestAssert.False(home.Output.Contains("winget install --id Microsoft.DotNet", StringComparison.Ordinal), "a found tool is not offered:\n" + home.Output);
            using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "answers.json"))))
            {
                TestAssert.Equal("home", saved.RootElement.GetProperty("where").GetString());
                TestAssert.Equal(game, saved.RootElement.GetProperty("gameRoot").GetString());
                TestAssert.Equal(installFolder, saved.RootElement.GetProperty("installFolder").GetString());
            }

            // A second run offers the answers it kept and asks nothing else.
            var again = Run(windowsPowerShell, guide, state, standIns, root, "again", new { useSaved = "Y", missing = Array.Empty<string>() }, search);
            TestAssert.Equal(0, again.ExitCode, again.Output);
            AssertHas(again.Output, "kept answers", "Last time you chose: Where: this PC", $"Game folder: {game}",
                "Use these answers again? Type Y for yes or N for no. Y", "-Layout 'Home'",
                "Steps 1 and 2 of 6: your answers from last time.", $"Step 3 of 6: Where Amberline goes The install folder from last time: {installFolder}",
                $"--install-to \"{installFolder}\"", "--delete-originals", "Summary");
            TestAssert.False(again.Output.Contains("Type 1 or 2", StringComparison.Ordinal) || again.Output.Contains("Where should Amberline be installed", StringComparison.Ordinal),
                "kept answers are not asked again:\n" + again.Output);

            // A rented Linux server, PowerShell 7: new answers; Zig missing and refused, so only its download page is shown. Step 3 asks
            // where the package goes, and nothing is copied or deleted on this PC.
            var package = Path.Combine(root, "server package");
            var rented = Run(pwsh, guide, state, standIns, root, "rented", new
            {
                useSaved = "N", where = "2", host = "amberline.example.test", serverName = "Friends Server", system = "2",
                gameRoot = game, airRoot = air, packageFolder = package, install = "N", missing = new[] { "zig" },
            }, search);
            TestAssert.Equal(0, rented.ExitCode, rented.Output);
            // Batch 645: the copy steps as a numbered checklist, then the summary.
            AssertHas(rented.Output, "rented plan", "Zig", "Install Zig now? This accepts its license. Type Y for yes or N for no. N",
                "Install Zig from its download page, then come back here: https://ziglang.org/download/", "-Layout 'Server'",
                "-Runtime 'linux-x64'", "-PublicHost 'amberline.example.test'", "-ServerName 'Friends Server'", "chmod +x",
                "Type the address: amberline.example.test", "Type a server name, or press Enter for Amberline: Friends Server",
                "Which system does your server run? 1. Windows 2. Linux Type 1 or 2: 2",
                "Step 3 of 6: Where the server package goes", @"Where should the server package go? Type a folder, or press Enter for C:\Amberline\server-package",
                "Nothing is copied or deleted on this PC.", $"-Output '{package}'",
                $"Step 6 of 6: Putting it on your server Do these in order: 1. Copy this folder to your server: {package}",
                "2. Copy your game and AIR client folders to the server too.", "3. On the server, edit", "4. In your hosting company's firewall", "5. On the server",
                "6. Make each friend a login", $"7. Give each friend their login and this Play file: {Path.Combine(package, "Play.cmd")}",
                "four numbers with dots", $"Summary Server package folder: {package} Built. File Explorer shows the folder now. Next: the checklist above.",
                "The same folder keeps your friends' Play files working. Log:");
            AssertPlain(rented.Output, root);
            TestAssert.False(rented.Output.Contains("Delete the originals", StringComparison.Ordinal), "a rented server deletes nothing:\n" + rented.Output);
            TestAssert.False(rented.Output.Contains("would install it with", StringComparison.Ordinal), "a no installs nothing:\n" + rented.Output);
            TestAssert.False(rented.Output.Contains("-Version", StringComparison.Ordinal), "a server build takes no -Version:\n" + rented.Output);

            // An address build-server.ps1 would refuse: the guide stops in plain words (what, what to do, the log), no stack trace.
            var refused = Run(windowsPowerShell, guide, state, standIns, root, "refused", new
            {
                useSaved = "N", where = "2", host = "https://amberline.example.test:8391", missing = Array.Empty<string>(),
            }, search);
            TestAssert.Equal(1, refused.ExitCode, refused.Output);
            foreach (var part in new[] { "What happened:", "What to do:", "The full log is here:" })
                TestAssert.True(refused.Output.Contains(part, StringComparison.Ordinal), $"the refusal lacks <{part}>:\n{refused.Output}");
            TestAssert.False(refused.Output.Contains("CategoryInfo", StringComparison.Ordinal) || refused.Output.Contains("At line:", StringComparison.Ordinal),
                "the refusal shows a stack trace:\n" + refused.Output);

            // Batch 637: an install the pointer names is offered for an update, in the same folder (the folders inside it are kept).
            var existing = Path.Combine(root, "existing install");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "amberline-install.json"), "{}");
            var pointerProfile = Path.Combine(root, "profile-with-install");
            Directory.CreateDirectory(Path.Combine(pointerProfile, "Amberline"));
            File.WriteAllText(Path.Combine(pointerProfile, "Amberline", "install.json"), JsonSerializer.Serialize(new { folder = existing }));
            var update = Run(windowsPowerShell, guide, state, standIns, root, "update", new
            {
                useSaved = "N", where = "1", gameRoot = game, airRoot = air, updateInstall = "Y", deleteOriginals = "N", missing = Array.Empty<string>(),
            }, search, localAppData: pointerProfile);
            TestAssert.Equal(0, update.ExitCode, update.Output);
            // Batch 645: the installed version (none here) and this download's, the update that also repairs, no second install.
            AssertHas(update.Output, "the update", $"Your Amberline without a version number is in: {existing}",
                "This download builds 0.0.", "Updating also repairs it; your settings, logins and friends stay.", "uninstall this one first",
                "Update this Amberline now? Type Y for yes or N for no. Y",
                $"--install-to \"{existing}\" --game \"{game}\" --air \"{air}\" in a new window",
                $"Summary Install folder: {existing} Now: a new window named Amberline asks you to press Enter, then updates to 0.0.");
            TestAssert.False(Flat(update.Output).Contains("Where should Amberline be installed", StringComparison.Ordinal), "an update asks for no other folder:\n" + update.Output);
            TestAssert.False(update.Output.Contains("--delete-originals", StringComparison.Ordinal), "a no keeps the originals:\n" + update.Output);

            // An install folder inside the Amberline download is refused in plain words.
            var repository = Path.GetDirectoryName(Path.GetDirectoryName(guide))!;
            var inside = Run(windowsPowerShell, guide, state, standIns, root, "inside", new
            {
                useSaved = "N", where = "1", gameRoot = game, airRoot = air, installFolder = Path.Combine(repository, "Amberline"), missing = Array.Empty<string>(),
            }, search);
            TestAssert.Equal(1, inside.ExitCode, inside.Output);
            AssertHas(inside.Output, "the refusal", "Problem: That folder is inside the Amberline download (the folder with START-HERE.cmd)", "What happened:");

            TestAssert.False(File.Exists(called), "a dry run called a tool: " + (File.Exists(called) ? File.ReadAllText(called) : ""));
            var logs = Directory.GetFiles(state, "guide-*.log");
            TestAssert.True(logs.Length == 6, "every run writes its log");
            // Batch 635, the masking guard: the screen showed the made-up names (in the game folder's name), no log holds them.
            TestAssert.True(home.Output.Contains(FakeAccount, StringComparison.Ordinal), "the made-up names reach the guide:\n" + home.Output);
            AssertMasked(logs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Batch 635: before it asks, the guide searches for <c>League of Legends.exe</c> (checked against the SHA-256 pin of exehost.c)
    /// and the AIR client (<c>LolClient.swf</c> at the stock size, and <c>LolClient.exe</c>). A synthetic tree holds a correct game in
    /// the RADS layout, a game of another patch, a correct and an old AIR client, and three correct copies the search must leave
    /// out: in a hidden folder, behind a junction, and deeper than its depth bound. The guide itself is a copy inside the AIR client
    /// folder, as the first friend had it. A second run with a folder limit of 3 shows the count bound.
    /// </summary>
    [Check("server.start-here-search", TimeoutSeconds = 180)]
    public static void SearchFindsVerifiesAndRanksTheCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        var windowsPowerShell = AirBootstrapChecks.FindWindowsPowerShell() ?? throw new InvalidOperationException("Windows PowerShell 5.1 not found");
        var pwsh = FindOnPath("pwsh.exe") ?? throw new InvalidOperationException("pwsh not found");
        var root = AirBootstrapChecks.TemporaryDirectory("league-start-here-search");
        try
        {
            var standIns = StandIns(root);
            var tree = Path.Combine(root, "pc");
            var lol = Path.Combine(tree, "Games", "LoL");
            var release = Path.Combine(lol, "live", "projects", "lol_game_client", "releases", "0.0.0.51");
            CopyExe(release);
            Directory.CreateDirectory(Path.Combine(release, "DATA"));
            using (var swf = File.Create(Path.Combine(lol, "LolClient.swf"))) swf.SetLength(6944733);
            File.WriteAllText(Path.Combine(lol, "LolClient.exe"), "client");
            var old = Path.Combine(tree, "Old");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "League of Legends.exe"), "another patch");
            var oldAir = Path.Combine(tree, "OldAir");
            Directory.CreateDirectory(oldAir);
            File.WriteAllText(Path.Combine(oldAir, "LolClient.swf"), "another version");
            File.WriteAllText(Path.Combine(oldAir, "LolClient.exe"), "client");
            // Correct copies the search leaves out: hidden, behind a junction, ten folders deep (the bound is 8).
            var hidden = Path.Combine(tree, ".hidden-copy");
            CopyExe(hidden);
            new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;
            var outside = Path.Combine(root, "outside");
            CopyExe(outside);
            var link = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", Path.Combine(tree, "linked-folder"), outside }) link.ArgumentList.Add(argument);
            using var junction = Process.Start(link)!;
            junction.StandardOutput.ReadToEnd();
            junction.WaitForExit();
            TestAssert.Equal(0, junction.ExitCode, "mklink /J");
            CopyExe(Path.Combine(tree, "too-deep", "2", "3", "4", "5", "6", "7", "8", "9", "10"));

            // The guide, with the files it reads, inside the AIR client folder.
            var inner = Path.Combine(lol, "Amberline");
            foreach (var file in new[] { Path.Combine("tools", "guide.ps1"), "global.json", Path.Combine("native", "exehost", "exehost.c") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(inner, file))!);
                File.Copy(AirBootstrapChecks.FindWorkspaceFile(file), Path.Combine(inner, file));
            }
            var guide = Path.Combine(inner, "tools", "guide.ps1");

            // The game: the found one is listed first; "Paste a path" with the parent folder resolves to the release inside it.
            var found = Run(windowsPowerShell, guide, Path.Combine(root, "state"), standIns, root, "found",
                new { where = "1", gameChoice = "3", gameRoot = Path.Combine(tree, "Games"), airChoice = "1", installFolder = Path.Combine(root, "Amberline"),
                    deleteOriginals = "N", missing = Array.Empty<string>() },
                ["-SearchRoots", tree]);
            TestAssert.Equal(0, found.ExitCode, found.Output);
            var foundLog = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "state"), "guide-*.log").Single(), Encoding.UTF8);
            AssertHas(found.Output, "the search", $"Searching {tree}", "Looked at ", $"1. {release} (patch 1.0.0.126, correct)", $"Can't be used, another patch: {old}",
                "2. Choose another folder in a window", "3. Paste a path", "Type a number: 3", "Looking inside that folder (at most 60 seconds).",
                $"Found it inside that folder: {release}",
                $"Your game folder: {release}", $"1. {lol} (patch 1.0.0.126, correct)", $"Can't be used, another version: {oldAir}", $"-GameRoot '{release}'",
                $"-AirRoot '{lol}'", "Amberline's own folder is inside your AIR client folder. That is fine");
            // Batch 645: what can't be used comes first, so the numbered choices stand right above their question.
            TestAssert.True(found.Output.IndexOf("Can't be used, another patch", StringComparison.Ordinal) < found.Output.IndexOf("The search found these. Choose one:", StringComparison.Ordinal),
                "the copies that can't be used are named before the choices:\n" + found.Output);
            foreach (var skipped in new[] { ".hidden-copy", "linked-folder", "too-deep", "outside" })
                TestAssert.False(found.Output.Contains(skipped, StringComparison.Ordinal), $"the search went into {skipped}:\n{found.Output}");

            // Bounded: 3 folders looked at, so neither correct copy is reached; the guide asks for the folders as before.
            var bounded = Run(pwsh, guide, Path.Combine(root, "state-bounded"), standIns, root, "bounded",
                new { where = "1", gameRoot = release, airRoot = lol, installFolder = Path.Combine(root, "Amberline"), deleteOriginals = "N",
                    missing = Array.Empty<string>() }, ["-SearchRoots", tree, "-SearchFolderLimit", "3"]);
            TestAssert.Equal(0, bounded.ExitCode, bounded.Output);
            TestAssert.True(bounded.Output.Contains("Looked at 3 folders.", StringComparison.Ordinal), "the folder bound:\n" + bounded.Output);
            TestAssert.False(bounded.Output.Contains("correct)", StringComparison.Ordinal), "the bounded search reached a correct copy:\n" + bounded.Output);
            TestAssert.True(bounded.Output.Contains($"-GameRoot '{release}'", StringComparison.Ordinal), bounded.Output);
            TestAssert.False(File.Exists(Path.Combine(standIns, "called.txt")), "a dry run called a tool");
        }
        finally
        {
            // The junction alone first: a recursive delete reports a junction as a failed volume mount point.
            var junction = Path.Combine(root, "pc", "linked-folder");
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Batch 635: a run that is not a dry run, up to the build. Every tool is a stand-in, and the stand-in pwsh "builds": it prints
    /// two of build-server.ps1's stage lines, the .NET variables it was started with, a line with the account and PC name, and a
    /// refusal. The window shows a progress bar per stage and the refusal; the details (the variables, the named line) go to the log
    /// only, with the names masked. The child runs with -OutputFormat Text and every stream as text, so no CLIXML comes back.
    /// </summary>
    [Check("server.start-here-build-progress", TimeoutSeconds = 120)]
    public static void BuildStagesBecomeProgressBars()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guide = AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "guide.ps1"));
        var windowsPowerShell = AirBootstrapChecks.FindWindowsPowerShell() ?? throw new InvalidOperationException("Windows PowerShell 5.1 not found");
        var root = AirBootstrapChecks.TemporaryDirectory("league-start-here-build");
        try
        {
            var standIns = StandIns(root);
            File.WriteAllText(Path.Combine(standIns, "pwsh.cmd"), string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0called.txt\" echo pwsh %*",
                "if /i \"%~3\"==\"-Command\" (echo 7.6.0& exit /b 0)",
                "echo AMBERLINE-STAGE 1/7 Checking your game and AIR client folders",
                "echo AMBERLINE-STAGE 2/7 Building the server",
                "echo nologo=%DOTNET_NOLOGO% telemetry=%DOTNET_CLI_TELEMETRY_OPTOUT% language=%DOTNET_CLI_UI_LANGUAGE%",
                "echo a detail line from %USERNAME% on %COMPUTERNAME%",
                "echo GUIDE-ERROR: the stand-in build stopped here",
                "exit /b 1", ""), Encoding.ASCII);
            foreach (var name in new[] { "dotnet", "zig" }) File.WriteAllText(Path.Combine(standIns, name + ".cmd"), "@exit /b 0\r\n", Encoding.ASCII);
            var game = Path.Combine(root, "game");
            var air = Path.Combine(root, "air");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(air);
            var state = Path.Combine(root, "state");

            var build = Run(windowsPowerShell, guide, state, standIns, root, "build",
                new { where = "1", gameRoot = game, airRoot = air, installFolder = Path.Combine(root, "Amberline"), deleteOriginals = "N" },
                ["-SearchRoots", game], dryRun: false);
            TestAssert.Equal(1, build.ExitCode, build.Output);
            // Batch 645: plain "Part n of 7" lines in place of text bars, which a screen reader spells out sign by sign.
            AssertHas(build.Output, "the build window", "This takes a few minutes, longer the first time.",
                "Part 1 of 7: Checking your game and AIR client folders", "Part 2 of 7: Building the server",
                "What happened: The build stopped with this message: the stand-in build stopped here", "The full log is here:");
            foreach (var detail in new[] { "AMBERLINE-STAGE", "nologo=", "a detail line", "[#", "[-", "%  " })
                TestAssert.False(build.Output.Contains(detail, StringComparison.Ordinal), $"the window shows a detail <{detail}>:\n{build.Output}");

            var logs = Directory.GetFiles(state, "guide-*.log");
            TestAssert.Equal(1, logs.Length, "the run's log");
            var log = File.ReadAllText(logs[0], Encoding.UTF8);
            foreach (var part in new[] { "AMBERLINE-STAGE 2/7 Building the server", "nologo=1 telemetry=1 language=en", "a detail line from <hidden> on <hidden>" })
                TestAssert.True(log.Contains(part, StringComparison.Ordinal), $"the log lacks <{part}>:\n{log}");
            AssertMasked(logs);

            var calls = File.ReadAllLines(Path.Combine(standIns, "called.txt"));
            var buildCall = calls.Single(line => line.Contains("-EncodedCommand", StringComparison.Ordinal));
            TestAssert.True(buildCall.Contains("-OutputFormat Text", StringComparison.Ordinal), "pwsh runs with -OutputFormat Text: " + buildCall);
            var command = Encoding.Unicode.GetString(Convert.FromBase64String(buildCall[(buildCall.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16)..].Trim()));
            TestAssert.True(command.Contains("*>&1", StringComparison.Ordinal), "every stream comes back as text: " + command);
            TestAssert.False(calls.Any(line => line.StartsWith("winget", StringComparison.Ordinal)), "nothing was installed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Batch 645: the search's time limit and memory, and the ends of a run on a PC with an install, on a synthetic tree (the shipped
    /// EXE copied, read only). A time limit of 0 seconds stops the search before its first folder, says so, and the guide asks for
    /// the folders. A run that declines the kept answers searches the folders chosen last time first, so they are listed though the
    /// roots given are empty. An install with its own copies of the game and the AIR client is updated with them (nothing copied,
    /// no delete question) and ends with the summary; a no to the update ends with "Nothing was changed"; an installed release
    /// (0.1.0) ends right after step 1, because a build made here would not replace it.
    /// </summary>
    [Check("server.start-here-summary-and-search-limit", TimeoutSeconds = 180)]
    public static void SummaryAndTimeLimitedSearch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guide = AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "guide.ps1"));
        var windowsPowerShell = AirBootstrapChecks.FindWindowsPowerShell() ?? throw new InvalidOperationException("Windows PowerShell 5.1 not found");
        var root = AirBootstrapChecks.TemporaryDirectory("league-start-here-summary");
        try
        {
            var standIns = StandIns(root);
            var tree = Path.Combine(root, "pc");
            var release = Path.Combine(tree, "Riot Games", "League of Legends", "RADS", "projects", "lol_game_client", "releases", "0.0.0.51", "deploy");
            CopyExe(release);
            var air = Path.Combine(tree, "AIR");
            Directory.CreateDirectory(air);
            using (var swf = File.Create(Path.Combine(air, "LolClient.swf"))) swf.SetLength(6944733);
            File.WriteAllText(Path.Combine(air, "LolClient.exe"), "client");
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var state = Path.Combine(root, "state");
            var none = Array.Empty<string>();

            var limited = Run(windowsPowerShell, guide, state, standIns, root, "limited",
                new { where = "1", gameRoot = release, airRoot = air, installFolder = Path.Combine(root, "Amberline"), deleteOriginals = "N", missing = none },
                ["-SearchRoots", tree, "-SearchSeconds", "0"]);
            TestAssert.Equal(0, limited.ExitCode, limited.Output);
            AssertHas(limited.Output, "the time limit", "This takes at most 0 seconds. Press S to skip.", "The search stopped at its time limit of 0 seconds.",
                "Looked at 0 folders.", "None found. Choose it yourself.", $"Your game folder: {release}");
            TestAssert.False(limited.Output.Contains("correct)", StringComparison.Ordinal), "the stopped search listed a find:\n" + limited.Output);

            var remembered = Run(windowsPowerShell, guide, state, standIns, root, "remembered",
                new { useSaved = "N", where = "1", gameChoice = "1", airChoice = "1", installFolder = Path.Combine(root, "Amberline"), deleteOriginals = "N", missing = none },
                ["-SearchRoots", empty]);
            TestAssert.Equal(0, remembered.ExitCode, remembered.Output);
            AssertHas(remembered.Output, "the remembered folders", $"Searching {release} Searching {air} Searching {empty}", $"1. {release} (patch 1.0.0.126, correct)",
                $"1. {air} (patch 1.0.0.126, correct)", $"-GameRoot '{release}'", $"-AirRoot '{air}'");

            // An install the pointer names, with its own game and AIR client.
            var existing = Path.Combine(root, "existing");
            var ownGame = Path.Combine(existing, "game", "projects", "lol_game_client", "releases", "0.0.0.51");
            var ownAir = Path.Combine(existing, "air");
            foreach (var folder in new[] { ownGame, ownAir, Path.Combine(existing, "app") }) Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(existing, "amberline-install.json"), "{}");
            var build = Path.Combine(existing, "app", "BUILD.json");
            File.WriteAllText(build, """{"version": "0.0.1"}""");
            var profile = Path.Combine(root, "profile-with-install");
            Directory.CreateDirectory(Path.Combine(profile, "Amberline"));
            File.WriteAllText(Path.Combine(profile, "Amberline", "install.json"), JsonSerializer.Serialize(new { folder = existing }));

            var own = Run(windowsPowerShell, guide, Path.Combine(root, "state-own"), standIns, root, "own",
                new { where = "1", gameRoot = release, airRoot = air, updateInstall = "Y", missing = none }, ["-SearchRoots", empty], localAppData: profile);
            TestAssert.Equal(0, own.ExitCode, own.Output);
            AssertHas(own.Output, "the update with the install's own copies", $"Your Amberline 0.0.1 is in: {existing}", "This download builds 0.0.",
                "Using the install folder's own copy of the game.", "Using the install folder's own copy of the AIR client.",
                $"--install-to \"{existing}\" --game \"{ownGame}\" --air \"{ownAir}\" in a new window",
                $"Summary Install folder: {existing} Now: a new window named Amberline asks you to press Enter, then updates to 0.0.",
                "This ends any game in progress.", "Then: its page opens in your web browser.",
                "From now on: start Amberline from its desktop icon.", "START-HERE.cmd again. Log:");
            foreach (var gone in new[] { "Amberline copies these", "Delete the originals", "--delete-originals" })
                TestAssert.False(Flat(own.Output).Contains(gone, StringComparison.Ordinal), $"the update with own copies shows <{gone}>:\n{own.Output}");
            AssertPlain(own.Output, root);

            var no = Run(windowsPowerShell, guide, Path.Combine(root, "state-no"), standIns, root, "no",
                new { where = "1", gameRoot = release, airRoot = air, updateInstall = "N", missing = none }, ["-SearchRoots", empty], localAppData: profile);
            TestAssert.Equal(0, no.ExitCode, no.Output);
            AssertHas(no.Output, "the no", "Update this Amberline now? Type Y for yes or N for no. N",
                "Summary Nothing was changed. To open Amberline: double-click its desktop icon. Log:");
            TestAssert.False(no.Output.Contains("Step 4 of 6", StringComparison.Ordinal), "a no stops before the tools:\n" + no.Output);

            File.WriteAllText(build, """{"version": "0.1.0"}""");
            var newer = Run(windowsPowerShell, guide, Path.Combine(root, "state-newer"), standIns, root, "newer",
                new { where = "1", missing = none }, ["-SearchRoots", empty], localAppData: profile);
            TestAssert.Equal(0, newer.ExitCode, newer.Output);
            AssertHas(newer.Output, "the installed release", "Type 1 or 2: 1",
                $"Summary Nothing was changed. Your Amberline 0.1.0, in {existing}, is not older than this download's build 0.0.",
                "To update it, use the newest Amberline release zip.", "DRY RUN finished");
            TestAssert.False(newer.Output.Contains("Step 2 of 6", StringComparison.Ordinal), "an installed release ends after step 1:\n" + newer.Output);
            TestAssert.False(File.Exists(Path.Combine(standIns, "called.txt")), "a dry run called a tool");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Batch 653, the owner's word list (3 November 2026): every yes or no in START-HERE, the Start Amberline window, Uninstall
    /// Amberline.cmd and the Play file asks "Type Y for yes or N for no.", every pause says "Press Enter to continue." and every
    /// window's end "Press Enter to close.". The sources are read as text, so no other phrasing of the three is left anywhere in them.
    /// Allowed besides: a typed answer's default ("or press Enter for C:\Amberline"), the folder prompt's window, the typed word that
    /// confirms an uninstall, and two sentences that only describe Enter (the intro, and the update's new window in the summary).
    /// </summary>
    [Check("server.start-here-word-list")]
    public static void ConsoleAnswersUseTheWordList()
    {
        string[] sentences = ["Press Enter to continue.", "Press Enter to close.", "Type Y for yes or N for no.", "press Enter to uninstall Amberline now."];
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "START-HERE.cmd", Path.Combine("tools", "guide.ps1"), Path.Combine("tools", "play-bootstrap.cmd"),
                     Path.Combine("src", "Amberline.Platform", "HostMode.cs"), Path.Combine("src", "Amberline.Platform", "HostInstall.cs"),
                     Path.Combine("src", "Amberline.Platform", "HostUninstall.cs") })
        {
            var text = File.ReadAllText(AirBootstrapChecks.FindWorkspaceFile(name));
            foreach (Match match in Regex.Matches(text, @"(?i)\b(press (any key|enter)|type y\b|\(y/n\))[^.""'\r\n]*\.?"))
            {
                var phrase = match.Value;
                var before = text[Math.Max(0, match.Index - 24)..match.Index];
                var allowed = sentences.Contains(phrase, StringComparer.Ordinal) || phrase.StartsWith("press Enter for ", StringComparison.Ordinal) ||
                    phrase.StartsWith("Press Enter to choose it in a window", StringComparison.Ordinal) ||
                    before.EndsWith("Type each answer, then ", StringComparison.Ordinal) || before.EndsWith("asks you to ", StringComparison.Ordinal);
                TestAssert.True(allowed, $"{name} says <{phrase}>, which is not one of the word list's console answers");
                found.Add(phrase);
            }
        }
        foreach (var sentence in sentences) TestAssert.True(found.Contains(sentence), $"no window says <{sentence}>");
        // The guide asks every yes or no through Read-YesNo, whose prompt is the word list's, and reads a pause only with it.
        var guide = File.ReadAllText(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "guide.ps1")));
        TestAssert.True(guide.Contains("$yesNo = 'Type Y for yes or N for no.'", StringComparison.Ordinal) &&
            guide.Contains("$answer = Read-Valid $Key $yesNo", StringComparison.Ordinal) && guide.Contains("Read-Line $goOn", StringComparison.Ordinal),
            "the guide's yes or no and pause prompts");
        TestAssert.False(Regex.IsMatch(guide, @"Read-Host\s+['""]"), "the guide reads a line with a prompt of its own");
        // The release's Start Amberline.cmd ends a failed start with the same close line.
        var release = File.ReadAllText(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "build-server.ps1")));
        TestAssert.True(release.Contains("echo Press Enter to close.\r\npause >nul", StringComparison.Ordinal) ||
            release.Contains("echo Press Enter to close.\npause >nul", StringComparison.Ordinal), "Start Amberline.cmd's close line");
    }

    /// <summary>Batch 645: the console's lines wrap, so a line may break anywhere between words; compare with every run of spaces as one.</summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    private static void AssertHas(string output, string what, params string[] parts)
    {
        var flat = Flat(output);
        foreach (var part in parts)
            TestAssert.True(flat.Contains(Flat(part), StringComparison.Ordinal), $"{what} lacks <{part}>:\n{output}");
    }

    /// <summary>
    /// Batch 645, for a screen reader: no line is longer than 80 columns but one that holds a path, a web address or a dry run's plan,
    /// and there are no text bars.
    /// </summary>
    private static void AssertPlain(string output, string root)
    {
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "guide.ps1"))))!;
        foreach (var line in output.Split('\n').Select(line => line.TrimEnd('\r')))
            TestAssert.True(line.Length <= 80 || line.Contains(root, StringComparison.OrdinalIgnoreCase) || line.Contains(repository, StringComparison.OrdinalIgnoreCase) ||
                line.Contains("://", StringComparison.Ordinal) || line.StartsWith("DRY RUN:", StringComparison.Ordinal), $"a line is longer than 80 columns:\n{line}");
        foreach (var art in new[] { "[#", "[-", "%  " })
            TestAssert.False(output.Contains(art, StringComparison.Ordinal), $"the window shows a text bar <{art}>:\n{output}");
    }

    private static string StandIns(string root)
    {
        var standIns = Path.Combine(root, "stand-ins");
        Directory.CreateDirectory(standIns);
        foreach (var name in new[] { "winget", "pwsh", "dotnet", "git" })
            File.WriteAllText(Path.Combine(standIns, name + ".cmd"), $"@echo {name} %*>>\"%~dp0called.txt\"\r\n", Encoding.ASCII);
        return standIns;
    }

    private static void CopyExe(string folder)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "League of Legends.exe");
        File.Copy(Path.Combine(GameDataPaths.Default.Root, "League of Legends.exe"), target);
        File.SetAttributes(target, FileAttributes.Normal);
    }

    private static void AssertMasked(string[] logs)
    {
        var masked = false;
        foreach (var log in logs)
        {
            var text = File.ReadAllText(log, Encoding.UTF8);
            TestAssert.False(text.Contains(FakeAccount, StringComparison.OrdinalIgnoreCase) || text.Contains(FakePc, StringComparison.OrdinalIgnoreCase),
                $"the log {Path.GetFileName(log)} holds the account or PC name:\n{text}");
            masked |= text.Contains("<hidden>", StringComparison.Ordinal);
        }
        TestAssert.True(masked, "no log line was masked");
    }

    private static (int ExitCode, string Output) Run(string shell, string guide, string state, string standIns, string root, string name, object answers,
        string[] extra, bool dryRun = true, string? localAppData = null)
    {
        localAppData ??= Path.Combine(root, "profile-local");
        Directory.CreateDirectory(localAppData);
        var answersFile = Path.Combine(root, name + ".json");
        File.WriteAllText(answersFile, JsonSerializer.Serialize(answers));
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", guide }
            .Concat(dryRun ? new[] { "-DryRun" } : Array.Empty<string>()).Concat(new[] { "-Answers", answersFile, "-StateFolder", state }).Concat(extra))
            start.ArgumentList.Add(argument);
        // The child's PATH only: the stand-ins first. Made-up account and PC names, which the log must mask.
        start.Environment["PATH"] = standIns + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["USERNAME"] = FakeAccount;
        start.Environment["COMPUTERNAME"] = FakePc;
        start.Environment["LOCALAPPDATA"] = localAppData;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start " + shell);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("the START-HERE guide dry run timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, file)).FirstOrDefault(File.Exists);
}
