using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// The home host's folder step (Amberline Host H3). Every folder is synthetic under a temporary root, the executables' pins are their
/// own SHA-256, and the folder window is a list, so no real client folder is read and no window opens.
/// </summary>
public static class HostFolderChecks
{
    [Check("server.host-folder-check-plain-words")]
    public static void FolderProblemsAreNamedInPlainWords()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-words");
        try
        {
            var game = Game(Path.Combine(root, "dl", "projects", "lol_game_client", "releases", "0.0.0.51"));
            var air = Air(Path.Combine(root, "dl", "air"));
            var setup = Pins(game, air);
            var problems = new List<string>();
            string Problem(FolderKind kind, string folder, FolderSetup? with = null)
            {
                var check = HostFolders.Check(kind, folder, with ?? setup);
                TestAssert.False(check.Ok, folder + " must be refused");
                problems.Add(check.Problem!);
                return check.Problem!;
            }
            void Says(string expected, string actual) => TestAssert.True(actual.Contains(expected, StringComparison.Ordinal), actual);

            var good = HostFolders.Check(FolderKind.Game, game, setup);
            TestAssert.True(good.Ok && good.Folder == game, good.Problem);
            TestAssert.True(good.Warnings.SequenceEqual(["Voices and fonts may be missing: lol_game_client_en_us is not next to lol_game_client."]),
                string.Join(" | ", good.Warnings));
            Directory.CreateDirectory(Path.Combine(root, "dl", "projects", "lol_game_client_en_us", "releases", "0.0.0.20", "DATA"));
            TestAssert.Equal(0, HostFolders.Check(FolderKind.Game, game, setup).Warnings.Count, "the language folder beside the game ends the warning");
            TestAssert.True(HostFolders.Check(FolderKind.Air, air, setup).Ok, "the stock AIR layout passes");

            Says("Amberline cannot find that game folder. Check its address, or choose the folder. (A-FILES-01)", Problem(FolderKind.Game, Path.Combine(root, "nowhere")));
            Says("Amberline cannot find that AIR client folder.", Problem(FolderKind.Air, "not a full address"));
            var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
            Says("This folder does not contain the game. Choose the folder you downloaded the 2011 game into. (A-FILES-01)", Problem(FolderKind.Game, empty));
            Says("This folder does not contain the AIR client (it holds an assets folder).", Problem(FolderKind.Air, empty));
            Says("This folder has the game, but not the 2011 version Amberline needs (version 1.0.0.126).",
                Problem(FolderKind.Game, game, setup with { GameSha256 = new string('0', 64) }));
            TestAssert.Equal(game, HostFolders.Check(FolderKind.Game, game, setup with { GameSha256 = new string('0', 64) }).Folder,
                "the refused folder is named on its row");

            Directory.Delete(Path.Combine(game, "LEVELS", "Map8"));
            Says(@"The game folder is incomplete: LEVELS\Map8 is missing. Download it again. (A-FILES-03)", Problem(FolderKind.Game, game));
            Directory.Delete(Path.Combine(game, "DATA", "BuildingBlocks"));
            Says(@"The game folder is incomplete: DATA\BuildingBlocks is missing.", Problem(FolderKind.Game, game));

            File.Delete(Path.Combine(air, "assets", "swfs", "AirGeneratedContent.swf"));
            Says(@"This AIR client is incomplete: 1 file is missing or damaged (for example assets\swfs\AirGeneratedContent.swf). Download it again. (A-FILES-04)",
                Problem(FolderKind.Air, air));
            File.Delete(Path.Combine(air, "LolClient.swf"));
            File.Delete(Path.Combine(air, "mod_lgn.dat"));
            Says("This AIR client is incomplete: 3 files are missing or damaged (for example the AIR client's .swf file).", Problem(FolderKind.Air, air));
            var other = Air(Path.Combine(root, "other-air"));
            Says("This is not the 2011 AIR client Amberline needs (version 1.0.0.126).", Problem(FolderKind.Air, other, setup with { AirSha256 = new string('0', 64) }));

            // A-FILES-05: a saved folder that is gone sends the start back to setup with the old address.
            var gone = Path.Combine(root, "moved-game");
            Says($"The game folder you chose is gone (it was at {gone}). Choose it again. (A-FILES-05)", HostFolders.Gone(gone, air)!);
            Says($"The AIR client folder you chose is gone (it was at {gone}). Choose it again. (A-FILES-05)", HostFolders.Gone(game, gone)!);
            TestAssert.Equal(null, HostFolders.Gone(game, air));
            var paths = HostPaths.Resolve(Path.Combine(root, "data"), Path.Combine(root, "Local"), Path.Combine(root, "app", "platform"));
            Directory.CreateDirectory(paths.Data);
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(new { dataRoot = gone, airRoot = air }));
            try
            {
                Amberline.Platform.Program.LoadSettings(paths.Settings, paths);
                TestAssert.True(false, "a gone game folder must not load");
            }
            catch (ArgumentException ex) { Says("(A-FILES-05)", ex.Message); }
            var (savedGame, _) = HostFolders.Look(setup, gone, null);
            Says("(A-FILES-05)", savedGame.Problem!);
            problems.Add(savedGame.Problem!);

            // Warnings from the stock client's folder hazards (MAX_PATH, the system code page), the same list the companion logs.
            var deep = Game(Path.Combine(root, new string('g', Math.Max(1, 200 - root.Length))));
            TestAssert.True(HostFolders.GameWarnings(deep).Contains(@"This folder's path is long; the game may not start from it. Move it closer to C:\."),
                string.Join(" | ", HostFolders.GameWarnings(deep)));
            var foreign = Game(Path.Combine(root, "\u4e2d\u6587 \u0e44\u0e17\u0e22"));
            var expectsCodePage = Amberline.AirCompanion.GameFolderReport.Hazards(foreign, Amberline.AirCompanion.GameFolderReport.SystemAnsi())
                .Contains("not-ansi-representable");
            TestAssert.Equal(expectsCodePage, HostFolders.GameWarnings(foreign).Any(warning => warning.StartsWith("This folder's path has letters the 2011 game cannot read", StringComparison.Ordinal)),
                "the code-page warning follows the companion's own hazard");

            foreach (var problem in problems)
                foreach (var jargon in new[] { "SHA", "exehost", "bytes", "-GameRoot", "-AirRoot", ".c " })
                    TestAssert.False(problem.Contains(jargon, StringComparison.Ordinal), $"no developer words ({jargon}): {problem}");
        }
        finally { HostModeChecks.Delete(root); }
    }

    [Check("server.host-folder-search-bounded")]
    public static void TheFolderSearchStaysWithinItsLimits()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-search");
        try
        {
            string[] marker = [HostFolders.GameExecutable];
            var deep = Path.Combine(root, "deep");
            var deepGame = Game(Path.Combine(deep, "a", "b", "c", "d", "e", "f", "g"));
            TestAssert.Equal(0, HostFolders.Find(deep, marker, 6, 1000).Count, "seven levels down is past a depth of six");
            TestAssert.True(HostFolders.Find(deep, marker, 7, 1000).SequenceEqual([deepGame]), "within the depth it is found");

            var wide = Path.Combine(root, "wide");
            for (var index = 0; index < 40; index++) Directory.CreateDirectory(Path.Combine(wide, $"a{index:00}"));
            var wideGame = Game(Path.Combine(wide, "z", "game"));
            TestAssert.Equal(0, HostFolders.Find(wide, marker, 6, 30).Count, "the folder budget ends the walk before the 43rd folder");
            TestAssert.True(HostFolders.Find(wide, marker, 6, 43).SequenceEqual([wideGame]), "breadth first: root, 40 siblings, z, then the game");

            var nested = Path.Combine(root, "nested");
            var outer = Game(Path.Combine(nested, "game"));
            Air(Path.Combine(outer, "DATA", "air"));
            TestAssert.True(HostFolders.Find(nested, [HostFolders.GameExecutable, "LolClient.exe"], 6, 1000).SequenceEqual([outer]),
                "it never goes below a folder it found");

            var hidden = Path.Combine(root, "hidden");
            Game(Path.Combine(hidden, "secret", "game"));
            File.SetAttributes(Path.Combine(hidden, "secret"), FileAttributes.Directory | FileAttributes.Hidden);
            Game(Path.Combine(hidden, "Windows", "game"));
            Game(Path.Combine(hidden, "Program Files", "game"));
            TestAssert.Equal(0, HostFolders.Find(hidden, marker, 6, 1000).Count, "hidden folders and the system's own folders are not searched");

            // The automatic look: a player install record first, then the places, skipping a wrong game found earlier.
            var install = Path.Combine(root, "install");
            var recordGame = Game(Path.Combine(install, "game", "projects", "lol_game_client", "releases", "0.0.0.51"));
            var recordAir = Air(Path.Combine(install, "air"));
            var record = Path.Combine(root, "Local", "Amberline", "player", "install.json");
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllText(record, JsonSerializer.Serialize(new { install, air = recordAir, game = recordGame }));
            var pins = Pins(recordGame, recordAir);
            var (fromRecord, airFromRecord) = HostFolders.Look(pins with { Records = [record] }, null, null);
            TestAssert.True(fromRecord.Folder == recordGame && airFromRecord.Folder == recordAir, "the player install's folders are offered");

            var downloads = Path.Combine(root, "profile", "Downloads");
            var wrong = Path.Combine(downloads, "Riot", "League of Legends.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(wrong)!);
            File.WriteAllBytes(wrong, RandomNumberGenerator.GetBytes(256));
            var placeGame = Game(Path.Combine(downloads, "LoL", "live", "projects", "lol_game_client", "releases", "0.0.0.51"));
            File.Copy(Path.Combine(recordGame, HostFolders.GameExecutable), Path.Combine(placeGame, HostFolders.GameExecutable), overwrite: true);
            var placeAir = Air(Path.Combine(downloads, "LoL", "air"));
            var (searchedGame, searchedAir) = HostFolders.Look(pins with { Places = [new(Path.Combine(root, "profile"), 7, 1000)] }, null, null);
            TestAssert.True(searchedGame.Folder == placeGame && searchedAir.Folder == placeAir, $"{searchedGame} {searchedAir}");
            var (none, _) = HostFolders.Look(pins with { Places = [new(Path.Combine(root, "profile"), 7, 5)] }, null, null);
            TestAssert.Equal(FolderCheck.NotFound, none, "a place's budget bounds the automatic look as well");

            // A place reaches the game's release folder at least eight levels down (the user's folder and every drive), with a folder budget.
            foreach (var place in FolderSetup.ThisPc().Places)
                TestAssert.True(place.Depth >= 8 && place.Folders > 0, $"{place.Depth} levels, {place.Folders} folders");
            // The one-folder install's pointer, then the player's record (batch 647: its own per-user folder), then the one in the folder from before.
            var records = FolderSetup.ThisPc().Records;
            TestAssert.True(records.Count == 3 && records[0].EndsWith(@"\Amberline\install.json", StringComparison.OrdinalIgnoreCase) &&
                records[1].EndsWith(@"\Amberline Player\install.json", StringComparison.OrdinalIgnoreCase) &&
                records[2].EndsWith(@"\Amberline\player\install.json", StringComparison.OrdinalIgnoreCase), string.Join(", ", records));
            TestAssert.Equal(0, new FolderSetup().Places.Count + new FolderSetup().Records.Count, "by default nothing is searched");
            TestAssert.Equal(null, new FolderSetup().Choose, "by default no folder window opens");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// The owner's VM test (3 November 2026): the AIR client two levels down and the game seven levels down, under a user's folder and
    /// under a drive. The places are ThisPc's own (their depth and folder budget), moved under a temporary root, so no real folder is read.
    /// </summary>
    [Check("server.host-folder-search-reaches-deep-game")]
    public static void TheFirstLookReachesAGameSevenLevelsDown()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-deep");
        try
        {
            var places = FolderSetup.ThisPc().Places;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var searched = new (string Name, SearchPlace? Place)[]
            {
                ("user-folder", places.FirstOrDefault(place => place.Folder == profile)),
                ("drive", places.FirstOrDefault(place => Path.GetPathRoot(place.Folder) == place.Folder)),
            };
            foreach (var (name, place) in searched)
            {
                TestAssert.True(place is not null, $"the first look searches a {name}");
                var placeRoot = Path.Combine(root, name);
                var game = Game(Path.Combine(placeRoot, "a", "b", "c", "d", "e", "f", "release"));
                var air = Air(Path.Combine(placeRoot, "x", "air"));
                var (foundGame, foundAir) = HostFolders.Look(Pins(game, air) with { Places = [new(placeRoot, place!.Depth, place.Folders)] }, null, null);
                TestAssert.True(foundGame.Folder == game && foundAir.Folder == air,
                    $"{name}: the game is {foundGame.Folder ?? "not found"}, the AIR client is {foundAir.Folder ?? "not found"}");
            }
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// A download holds the AIR client shallow and the game down its own branch, past a folder budget that the broad search runs out of
    /// (ten sibling folders come before the game's branch). The first look finds the AIR client and then searches above it, where the
    /// game is, within the same budget.
    /// </summary>
    [Check("server.host-folder-search-finds-game-beside-air")]
    public static void TheGameBesideAnAirClientIsFoundWithinTheSameBudget()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-beside");
        try
        {
            var download = Path.Combine(root, "download");
            var air = Air(Path.Combine(download, "D", "E", "air"));
            var game = Game(Path.Combine(download, "D", "E", "live", "p", "q", "r", "release"));
            for (var index = 0; index < 10; index++) Directory.CreateDirectory(Path.Combine(download, "D", "Z", $"zz{index:00}"));
            var (foundGame, foundAir) = HostFolders.Look(Pins(game, air) with { Places = [new(download, 8, 16)] }, null, null);
            TestAssert.True(foundGame.Folder == game && foundAir.Folder == air,
                $"the game is {foundGame.Folder ?? "not found"}, the AIR client is {foundAir.Folder ?? "not found"}");
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>
    /// The folders a record names are searched, not only checked as they are: the one-folder install's pointer names its folder as
    /// <c>folder</c>, and a player install record names it as <c>install</c>, with no game or AIR key of its own.
    /// </summary>
    [Check("server.host-folder-search-reads-install-records")]
    public static void TheFoldersAnInstallRecordNamesAreSearched()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-records");
        try
        {
            var install = Path.Combine(root, "install");
            var game = Game(Path.Combine(install, "game", "projects", "lol_game_client", "releases", "0.0.0.51"));
            var air = Air(Path.Combine(install, "air"));
            var pins = Pins(game, air);
            var pointer = Path.Combine(root, "pointer.json");
            File.WriteAllText(pointer, JsonSerializer.Serialize(new { folder = install }));
            var record = Path.Combine(root, "record.json");
            File.WriteAllText(record, JsonSerializer.Serialize(new { install }));
            foreach (var file in new[] { pointer, record })
            {
                var (foundGame, foundAir) = HostFolders.Look(pins with { Records = [file] }, null, null);
                TestAssert.True(foundGame.Folder == game && foundAir.Folder == air,
                    $"{Path.GetFileName(file)}: the game is {foundGame.Folder ?? "not found"}, the AIR client is {foundAir.Folder ?? "not found"}");
            }
        }
        finally { HostModeChecks.Delete(root); }
    }

    /// <summary>The first look's time limit (20 seconds for both folders together, as the page says): with no time left it checks nothing.</summary>
    [Check("server.host-folder-search-time-bound")]
    public static void TheFirstLookStopsAtItsTimeLimit()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-time");
        try
        {
            var game = Game(Path.Combine(root, "a", "release"));
            var air = Air(Path.Combine(root, "air"));
            var setup = Pins(game, air) with { Places = [new(root, HostFolders.SearchDepth, HostFolders.SearchFolders)] };
            var (none, noAir) = HostFolders.Look(setup with { SearchTime = TimeSpan.Zero }, null, null);
            TestAssert.True(none.Folder is null && noAir.Folder is null, "a look with no time left checks nothing");
            TestAssert.True(new FolderSetup().SearchTime == TimeSpan.FromSeconds(20), "the default limit is 20 seconds");
            var (found, foundAir) = HostFolders.Look(setup, null, null);
            TestAssert.True(found.Folder == game && foundAir.Folder == air, "with the default limit both folders are found");
        }
        finally { HostModeChecks.Delete(root); }
    }

    [Check("server.host-folder-parent-resolves-release", TimeoutSeconds = 120)]
    public static void AParentFolderResolvesToTheReleaseFolder() => ParentResolvesAsync().GetAwaiter().GetResult();

    private static async Task ParentResolvesAsync()
    {
        string parent = "", release = "", air = "", empty = "";
        var titles = new List<string>();
        var picks = new Queue<string>();
        var home = HostModeChecks.Home.Create("league-host-parent", root =>
        {
            parent = Path.Combine(root, "downloads", "League 2011");
            release = Game(Path.Combine(parent, "live", "projects", "lol_game_client", "releases", "0.0.0.51"));
            air = Air(Path.Combine(parent, "lol_air_client"));
            empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
            return Pins(release, air) with { Choose = title => { lock (titles) titles.Add(title); return picks.Dequeue(); } };
        });
        string Encoded(string text) => WebUtility.HtmlEncode(text);

        // The functions first: a download's root, the folder above it and a player install's root all resolve.
        var setup = home.Loop.Folders;
        TestAssert.Equal(release, HostFolders.Check(FolderKind.Game, parent, setup).Folder, "the download's root resolves to its release folder");
        TestAssert.Equal(release, HostFolders.Check(FolderKind.Game, Path.GetDirectoryName(parent)! + "\\", setup).Folder, "and so does the folder above it");
        TestAssert.Equal(air, HostFolders.Check(FolderKind.Air, parent, setup).Folder, "the AIR client beside it as well");
        var install = Path.Combine(home.Root, "Install");
        var installGame = Game(Path.Combine(install, "game", "projects", "lol_game_client", "releases", "0.0.0.51"));
        File.Copy(Path.Combine(release, HostFolders.GameExecutable), Path.Combine(installGame, HostFolders.GameExecutable), overwrite: true);
        TestAssert.Equal(installGame, HostFolders.Check(FolderKind.Game, install, setup).Folder, "a player install (batch 619) resolves too");
        var mixed = Path.Combine(home.Root, "mixed");
        var modern = Path.Combine(mixed, "Modern", HostFolders.GameExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(modern)!);
        File.WriteAllBytes(modern, RandomNumberGenerator.GetBytes(256));
        TestAssert.Equal(Path.GetDirectoryName(modern), HostFolders.Check(FolderKind.Game, mixed, setup).Folder, "only a wrong game: it is named");
        var right = Game(Path.Combine(mixed, "old", "projects", "lol_game_client", "releases", "0.0.0.51"));
        File.Copy(Path.Combine(release, HostFolders.GameExecutable), Path.Combine(right, HostFolders.GameExecutable), overwrite: true);
        TestAssert.Equal(right, HostFolders.Check(FolderKind.Game, mixed, setup).Folder, "the first folder that passes wins over a shallower wrong one");

        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var client = home.Client();
            await HostModeChecks.WaitForHealthAsync(client, "setup", run);
            // H2's gate: the setup POSTs open the folder window on the desktop, so like every host route they need the session.
            using (var stranger = home.Client())
            {
                var unsigned = await stranger.PostAsync("/setup", new FormUrlEncodedContent([KeyValuePair.Create("choose", "game")]));
                TestAssert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode, "a browser without the session cannot open the folder window");
                TestAssert.Equal(0, titles.Count, "no folder window for it");
            }
            await home.SignInAsync(client);
            var page = await client.GetStringAsync("/setup");
            TestAssert.Equal(2, page.Split("Not found yet").Length - 1, page);
            // Batch 652: the folder window's note is what the Choose button says while it waits.
            TestAssert.True(page.Contains("Choose folder…", StringComparison.Ordinal) &&
                page.Contains("data-wait=\"Opening the folder window. It may open behind this browser window.\"", StringComparison.Ordinal), page);
            var token = HostModeChecks.Token(page);
            async Task<(HttpStatusCode Status, string Page)> Post(params (string Key, string Value)[] fields)
            {
                using var response = await client.PostAsync("/setup", new FormUrlEncodedContent(
                    fields.Select(field => KeyValuePair.Create(field.Key, field.Value)).Prepend(KeyValuePair.Create(token.Key, token.Value))));
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            var pasted = await Post(("game", ""), ("air", ""), ("paste", "\"" + parent + "\""));
            TestAssert.Equal(HttpStatusCode.OK, pasted.Status, pasted.Page);
            TestAssert.True(pasted.Page.Contains("Found:</span> " + Encoded(release), StringComparison.Ordinal) &&
                pasted.Page.Contains("Found:</span> " + Encoded(air), StringComparison.Ordinal), "one pasted parent fills both rows: " + pasted.Page);
            var nothing = await Post(("game", ""), ("air", ""), ("paste", empty));
            TestAssert.Equal(HttpStatusCode.BadRequest, nothing.Status);
            TestAssert.True(nothing.Page.Contains("This folder contains neither the game nor its AIR client.", StringComparison.Ordinal), nothing.Page);

            picks.Enqueue(parent);
            var chosen = await Post(("game", ""), ("air", ""), ("choose", "game"));
            TestAssert.True(titles.SequenceEqual(["Choose the game folder"]), string.Join(",", titles));
            TestAssert.True(chosen.Page.Contains("Found:</span> " + Encoded(release), StringComparison.Ordinal), "the window's parent folder resolves: " + chosen.Page);
            picks.Enqueue(empty);
            var wrongAir = await Post(("game", release), ("air", ""), ("choose", "air"));
            TestAssert.True(wrongAir.Page.Contains("This folder does not contain the AIR client", StringComparison.Ordinal) &&
                wrongAir.Page.Contains("Found:</span> " + Encoded(release), StringComparison.Ordinal), "the problem shows under its row: " + wrongAir.Page);

            var missing = await Post(("game", release), ("air", ""));
            TestAssert.Equal(HttpStatusCode.BadRequest, missing.Status);
            TestAssert.True(missing.Page.Contains("Choose the AIR client folder first.", StringComparison.Ordinal), missing.Page);

            // Without the stand-in server the saved folders send the loop back to setup instead of the full Platform, so
            // GAME_DATA_ROOT never moves to this synthetic game under the other checks.
            File.Delete(home.Loop.Paths.ServerExecutable);
            var used = await Post(("game", parent), ("air", parent));
            TestAssert.Equal(HttpStatusCode.OK, used.Status, used.Page);
            await HostModeChecks.WaitForStartsAsync(home.Loop, 2, run);
            await HostModeChecks.WaitForHealthAsync(client, "setup", run);
            using (var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
            {
                TestAssert.Equal(release, json.RootElement.GetProperty("dataRoot").GetString(), "the parent is saved as its release folder");
                TestAssert.Equal(air, json.RootElement.GetProperty("airRoot").GetString());
            }
            var again = await client.GetStringAsync("/setup");
            TestAssert.True(again.Contains("Found:</span> " + Encoded(release), StringComparison.Ordinal), "the saved folders are offered: " + again);

            Directory.Move(air, air + "-moved");
            home.Loop.Restart();
            await HostModeChecks.WaitForStartsAsync(home.Loop, 3, run);
            var gone = await client.GetStringAsync("/setup");
            // Batch 638: the code is small and secondary (its own span), and the error summary at the top names the problem too.
            TestAssert.True(gone.Contains(Encoded($"The AIR client folder you chose is gone (it was at {air}). Choose it again. ") +
                "<span class=code>(A-FILES-05)</span>", StringComparison.Ordinal), gone);
            TestAssert.Equal(2, gone.Split("(A-FILES-05)").Length - 1, "the message shows twice: in the error summary and under its row");
        }
        finally { await home.StopAsync(run); }
        HostModeChecks.Delete(home.Root);
    }

    /// <summary>A synthetic 1.0.0.126 game release: League of Legends.exe (random bytes; its pin is its own SHA-256), DATA with Scripts
    /// and BuildingBlocks, LEVELS with the five maps.</summary>
    internal static string Game(string folder)
    {
        foreach (var name in new[] { @"DATA\Scripts", @"DATA\BuildingBlocks", @"LEVELS\Map1", @"LEVELS\Map2", @"LEVELS\Map3", @"LEVELS\Map4", @"LEVELS\Map8" })
            Directory.CreateDirectory(Path.Combine(folder, name));
        File.WriteAllBytes(Path.Combine(folder, HostFolders.GameExecutable), RandomNumberGenerator.GetBytes(4096));
        return Path.GetFullPath(folder);
    }

    /// <summary>The stock AIR layout (<see cref="AirStockLayoutFixture"/>, sized files) and the champion list file.</summary>
    internal static string Air(string folder)
    {
        AirStockLayoutFixture.Write(folder);
        File.WriteAllBytes(Path.Combine(folder, "assets", "swfs", "AirGeneratedContent.swf"), [0]);
        return Path.GetFullPath(folder);
    }

    /// <summary>A folder setup whose pins are these two folders' own executables (the game's when it has one).</summary>
    internal static FolderSetup Pins(string game, string air)
    {
        var executable = Path.Combine(game, HostFolders.GameExecutable);
        return new()
        {
            GameSha256 = File.Exists(executable) ? Sha256(executable) : new FolderSetup().GameSha256,
            AirSha256 = Sha256(Path.Combine(air, "LolClient.exe")),
        };
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
