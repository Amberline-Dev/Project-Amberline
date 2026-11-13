using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// The release zip's folder step in a one-folder install (batch 642): after the folders check out, the page offers to copy them into
/// the install folder and asks whether the originals go, with START-HERE's move code (batch 637). The install is the check's own
/// temporary Home with batch 637's marker beside its app folder; the game and the AIR client are synthetic stand-ins in a separate
/// temporary download folder, pinned by their own SHA-256. Nothing touches the real C:\Amberline, profile or a client folder.
/// </summary>
public static class HostFolderCopyChecks
{
    [Check("server.host-folder-copy-in", TimeoutSeconds = 180)]
    public static void TheFolderStepCopiesTheClientsIntoTheInstall() => CopyInAsync().GetAwaiter().GetResult();

    private static async Task CopyInAsync()
    {
        var folder = OneFolder.Create("league-copy-in", Download);
        var originals = Snapshot(folder.Downloads);
        await folder.RunAsync(async browser =>
        {
            // The folders check out: the offer names what comes in, its size, the drive's free space and the delete question.
            var offer = await browser.PostAsync("/setup", ("game", folder.Game), ("air", folder.Air));
            TestAssert.Equal(HttpStatusCode.OK, offer.Status, offer.Page);
            foreach (var phrase in new[]
                     {
                         $"<h1>Copy your game files into {Encode(folder.Install)}?</h1>", "<p class=step>Step 2 of 4: Your game folders</p>",
                         $"<li>The game: {Encode(folder.Game)}</li>", "<li>The game's language files: ", $"<li>The AIR client: {Encode(folder.Air)}</li>",
                         $"They take 0.0 GB. Drive {Path.GetPathRoot(folder.Install)!.TrimEnd('\\')} has ", "<legend>Delete the original folders you picked?</legend>",
                         "<input type=radio name=delete value=no checked>No, keep them",
                         // Batch 652: the copy's button says what it copies, and at once how big it is and that it takes a while.
                         "<button data-wait=\"Copying the game files (0.0 GB). This takes several minutes.\">Copy the game files</button>",
                         "<button class=secondary formaction=/setup name=keep value=1>Use them where they are</button>",
                     })
                TestAssert.True(offer.Page.Contains(phrase, StringComparison.Ordinal), $"the offer says <{phrase}>: {offer.Page}");
            TestAssert.False(JsonText(folder.Settings).Contains("dataRoot", StringComparison.Ordinal), "nothing is saved before the host answers");

            // The copy, held at its first file: the page follows it through its script and status, under the script policy.
            using var hold = new ManualResetEventSlim();
            var copied = 0;
            folder.AfterCopy = _ => { if (Interlocked.Increment(ref copied) == 2) hold.Wait(TimeSpan.FromSeconds(60)); };
            var started = await browser.PostAsync(HostFolderCopyPath, ("game", folder.Game), ("air", folder.Air), ("delete", "no"));
            TestAssert.Equal(HttpStatusCode.Redirect, started.Status, started.Page);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Volatile.Read(ref copied) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            using (var running = await browser.Client.GetAsync(HostFolderCopyPath))
            {
                var page = await running.Content.ReadAsStringAsync();
                TestAssert.Equal(HostDashboard.Policy, string.Join(";", running.Headers.GetValues("Content-Security-Policy")), "the copy page runs its script file");
                foreach (var phrase in new[] { $"<h1>Copying your game files into {Encode(folder.Install)}</h1>", "<div aria-live=polite><p id=copy-phase class=headline>Copying the game",
                             "<label for=copy-bar>Progress</label><progress id=copy-bar max=100 value=", $"<script src={HostFolderCopyPath}/page.js defer></script>" })
                    TestAssert.True(page.Contains(phrase, StringComparison.Ordinal), $"the running copy says <{phrase}>: {page}");
            }
            var status = await browser.StatusAsync();
            TestAssert.True(status.State == "copying" && status.Phase == "Copying the game" && status.Detail.StartsWith("1 of ", StringComparison.Ordinal),
                $"the status while copying: {status}");
            var again = await browser.PostAsync(HostFolderCopyPath, ("game", folder.Game), ("air", folder.Air), ("delete", "no"));
            TestAssert.Equal(HttpStatusCode.Redirect, again.Status, "a second copy while one runs goes to the running one");
            using (var script = await browser.Client.GetAsync(HostFolderCopyPath + "/page.js"))
            {
                var text = await script.Content.ReadAsStringAsync();
                TestAssert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType, "the script's type");
                TestAssert.True(text.Contains($"fetch('{HostFolderCopyPath}/status.json'", StringComparison.Ordinal) && text.Contains("location.reload()", StringComparison.Ordinal) &&
                    !text.Contains("innerHTML", StringComparison.Ordinal) && !text.Contains("http", StringComparison.Ordinal), "it reads the status and sets text only: " + text);
            }
            hold.Set();
            var done = await browser.EndAsync();
            foreach (var phrase in new[] { $"<h1>Your game files are in {Encode(folder.Install)}</h1>", "<p>The folders you picked are as they were.</p>",
                         "<div class=actions><button>Continue</button></div>" })
                TestAssert.True(done.Contains(phrase, StringComparison.Ordinal), $"the done page says <{phrase}>: {done}");

            // The copies are whole, the originals as they were, nothing half done, and platform.json names the copies.
            var inside = Path.Combine(folder.Install, @"game\projects\lol_game_client\releases\0.0.0.51");
            TestAssert.Equal(Snapshot(folder.Game), Snapshot(inside), "the game copy");
            TestAssert.Equal(Snapshot(folder.Language), Snapshot(Path.Combine(folder.Install, @"game\projects\lol_game_client_en_us")), "its language project");
            TestAssert.Equal(Snapshot(folder.Air), Snapshot(Path.Combine(folder.Install, "air")), "the AIR client copy");
            TestAssert.Equal(originals, Snapshot(folder.Downloads), "a no keeps the originals as they were");
            folder.NothingHalfDone();
            folder.Names(inside, Path.Combine(folder.Install, "air"));
            TestAssert.True(done.Contains($"name=game value=\"{Encode(inside)}\"", StringComparison.Ordinal), "Continue carries the copies: " + done);

            // Continue: the folders inside need no offer, are saved and the step goes on (back to setup without the stand-in server,
            // so GAME_DATA_ROOT never moves to this synthetic game under the other checks).
            File.Delete(folder.Home.Loop.Paths.ServerExecutable);
            var next = await browser.PostAsync("/setup", ("game", inside), ("air", Path.Combine(folder.Install, "air")));
            TestAssert.True(next.Status == HttpStatusCode.OK && next.Page.Contains("<h1>Starting Amberline</h1>", StringComparison.Ordinal), next.Page);
            await HostModeChecks.WaitForStartsAsync(folder.Home.Loop, 2, folder.Run!);
            folder.Names(inside, Path.Combine(folder.Install, "air"));
        });
    }

    [Check("server.host-folder-copy-declined", TimeoutSeconds = 120)]
    public static void DecliningUsesTheFoldersWhereTheyAre() => DeclinedAsync().GetAwaiter().GetResult();

    private static async Task DeclinedAsync()
    {
        var folder = OneFolder.Create("league-copy-declined", Download);
        var originals = Snapshot(folder.Downloads);
        await folder.RunAsync(async browser =>
        {
            var offer = await browser.PostAsync("/setup", ("game", folder.Game), ("air", folder.Air));
            TestAssert.True(offer.Page.Contains("Use them where they are", StringComparison.Ordinal), offer.Page);
            // Back is the folder step itself.
            TestAssert.True(offer.Page.Contains("<div class=actions><a href=/setup>Back</a>", StringComparison.Ordinal), offer.Page);
            File.Delete(folder.Home.Loop.Paths.ServerExecutable);
            var kept = await browser.PostAsync("/setup", ("game", folder.Game), ("air", folder.Air), ("delete", "yes"), ("keep", "1"));
            TestAssert.True(kept.Status == HttpStatusCode.OK && kept.Page.Contains("<h1>Starting Amberline</h1>", StringComparison.Ordinal), kept.Page);
            await HostModeChecks.WaitForStartsAsync(folder.Home.Loop, 2, folder.Run!);
            folder.Names(folder.Game, folder.Air);
            TestAssert.Equal(originals, Snapshot(folder.Downloads), "the folders stay as they were");
            TestAssert.False(Directory.Exists(Path.Combine(folder.Install, "game")) || Directory.Exists(Path.Combine(folder.Install, "air")), "nothing was copied in");
            using var copy = await browser.Client.GetAsync(HostFolderCopyPath);
            TestAssert.Equal(HttpStatusCode.Redirect, copy.StatusCode, "no copy page without a copy");
        });
    }

    /// <summary>
    /// The first friend's layout (batch 635): one folder holds the AIR client at its root, the game under live\projects\..., the
    /// downloaded repository and a file of the friend's own. The pasted parent fills both rows; a yes takes only the clients' own files.
    /// </summary>
    [Check("server.host-folder-copy-friend-layout", TimeoutSeconds = 180)]
    public static void APickedParentGivesUpOnlyTheClientsOwnFiles() => FriendLayoutAsync().GetAwaiter().GetResult();

    private static async Task FriendLayoutAsync()
    {
        var folder = OneFolder.Create("league-copy-friend", downloads =>
        {
            var parent = Path.Combine(downloads, "LoL");
            var clients = Download(parent, parent);
            Write(Path.Combine(parent, "Amberline-main", "START-HERE.cmd"), "@echo off\r\n");
            Write(Path.Combine(parent, "Amberline-main", "tools", "guide.ps1"), "# the guide");
            Write(Path.Combine(parent, "my notes.txt"), "the friend's own file");
            Write(Path.Combine(parent, "live", "other.txt"), "beside the game");
            return clients;
        });
        var parent = folder.Air;
        var repository = Snapshot(Path.Combine(parent, "Amberline-main"));
        var stockAir = string.Join("\n", Snapshot(parent).Split('\n').Where(line => FolderMove.AirEntries.Any(entry =>
            line.StartsWith(entry + "\\", StringComparison.OrdinalIgnoreCase) || line.StartsWith(entry + " ", StringComparison.OrdinalIgnoreCase))));
        var game = Snapshot(folder.Game);
        await folder.RunAsync(async browser =>
        {
            var pasted = await browser.PostAsync("/setup", ("game", ""), ("air", ""), ("paste", parent));
            TestAssert.True(pasted.Page.Contains("Found:</span> " + Encode(folder.Game), StringComparison.Ordinal) &&
                pasted.Page.Contains("Found:</span> " + Encode(parent), StringComparison.Ordinal), "one pasted parent fills both rows: " + pasted.Page);
            var offer = await browser.PostAsync("/setup", ("game", folder.Game), ("air", parent));
            TestAssert.True(offer.Page.Contains($"Other things in {Encode(parent)} stay where they are: " +
                "Amberline-main, live, my notes.txt.", StringComparison.Ordinal), "the offer names what stays: " + offer.Page);

            // The host's yes on one drive: the clients' parts are renamed in, and nothing else of the parent moves.
            var started = await browser.PostAsync(HostFolderCopyPath, ("game", folder.Game), ("air", parent), ("delete", "yes"));
            TestAssert.Equal(HttpStatusCode.Redirect, started.Status, started.Page);
            var done = await browser.EndAsync();
            TestAssert.True(done.Contains($"These stayed in {Encode(parent)}, because they are not part of the AIR client: Amberline-main, live, my notes.txt.",
                StringComparison.Ordinal) && !done.Contains("The folders you picked are as they were.", StringComparison.Ordinal), "the done page names what stayed: " + done);
            TestAssert.Equal(stockAir, Snapshot(Path.Combine(folder.Install, "air")), "the AIR client's own files came in");
            TestAssert.Equal(game, Snapshot(Path.Combine(folder.Install, @"game\projects\lol_game_client\releases\0.0.0.51")), "the game came in");
            TestAssert.Equal(repository, Snapshot(Path.Combine(parent, "Amberline-main")), "the repository is untouched");
            TestAssert.Equal("the friend's own file", File.ReadAllText(Path.Combine(parent, "my notes.txt")), "the friend's own file stays");
            TestAssert.Equal("beside the game", File.ReadAllText(Path.Combine(parent, "live", "other.txt")), "a file beside the game stays");
            TestAssert.False(FolderMove.AirEntries.Any(entry => File.Exists(Path.Combine(parent, entry)) || Directory.Exists(Path.Combine(parent, entry))),
                "the AIR client's own files left the parent");
            TestAssert.False(Directory.Exists(folder.Game) || Directory.Exists(folder.Language), "the game and its language project left");
            folder.NothingHalfDone();
            folder.Names(Path.Combine(folder.Install, @"game\projects\lol_game_client\releases\0.0.0.51"), Path.Combine(folder.Install, "air"));
        });
    }

    [Check("server.host-folder-copy-refusals", TimeoutSeconds = 180)]
    public static void ARefusedOrFailedCopyChangesNothing() => RefusalsAsync().GetAwaiter().GetResult();

    private static async Task RefusalsAsync()
    {
        // The game belongs to an Amberline server installation (a folder with platform-data above it), and a player install records it.
        var record = "";
        var folder = OneFolder.Create("league-copy-refusals", downloads =>
        {
            Directory.CreateDirectory(Path.Combine(downloads, "Server", "platform-data"));
            record = Path.Combine(downloads, "profile", "Amberline", "player", "install.json");
            return Download(Path.Combine(downloads, "Server"), Path.Combine(downloads, "Server", "air"));
        }, records => [record]);
        var server = Path.Combine(folder.Downloads, "Server");
        var originals = Snapshot(server);
        var settings = JsonText(folder.Settings);
        void Unchanged(string what)
        {
            TestAssert.Equal(originals, Snapshot(server), what + ": the originals are as they were");
            // A copy that failed may leave the empty folders above its target, never a file.
            TestAssert.True(Snapshot(Path.Combine(folder.Install, "game")) == "" && !Directory.Exists(Path.Combine(folder.Install, "air")), what + ": nothing came in");
            TestAssert.Equal(settings, JsonText(folder.Settings), what + ": platform.json is as it was");
            folder.NothingHalfDone();
        }
        await folder.RunAsync(async browser =>
        {
            // A yes for a folder that cannot be deleted: refused before anything changes, beside the delete question.
            var refused = await browser.PostAsync(HostFolderCopyPath, ("game", folder.Game), ("air", folder.Air), ("delete", "yes"));
            TestAssert.Equal(HttpStatusCode.BadRequest, refused.Status, refused.Page);
            TestAssert.True(refused.Page.Contains("This game belongs to an Amberline server installation, so it cannot be moved.", StringComparison.Ordinal) &&
                refused.Page.Contains("Nothing was moved, and your folders are as they were.", StringComparison.Ordinal) &&
                refused.Page.Contains("<li><a href=#delete>", StringComparison.Ordinal) && refused.Page.Contains("id=delete-error>", StringComparison.Ordinal) &&
                refused.Page.Contains("<input type=radio name=delete value=yes checked>", StringComparison.Ordinal), "the refusal and its question: " + refused.Page);
            Unchanged("a refused yes");
            using (var none = await browser.Client.GetAsync(HostFolderCopyPath))
                TestAssert.Equal(HttpStatusCode.Redirect, none.StatusCode, "a refusal starts no copy");

            // A copy that fails half way (the disk, a program holding a file): rolled back, and the page says so.
            folder.AfterCopy = _ => throw new IOException("synthetic: the disk is full");
            var failing = await browser.PostAsync(HostFolderCopyPath, ("game", folder.Game), ("air", folder.Air), ("delete", "no"));
            TestAssert.Equal(HttpStatusCode.Redirect, failing.Status, failing.Page);
            var failed = await browser.EndAsync();
            TestAssert.True(failed.Contains("<h1>Your game files were not copied</h1>", StringComparison.Ordinal) &&
                failed.Contains("Amberline could not copy the game and the AIR client: the disk may be full, or a program uses their files. Nothing was moved, and your folders are as they were.",
                    StringComparison.Ordinal) && failed.Contains("<button>Try again</button>", StringComparison.Ordinal), "the failure: " + failed);
            Unchanged("a failed copy");

            // A player install runs from the picked folders: a yes becomes a copy, and the originals stay.
            Write(record, JsonSerializer.Serialize(new { game = folder.Game, air = folder.Air }));
            folder.AfterCopy = _ => { };
            var player = await browser.PostAsync(HostFolderCopyPath, ("game", folder.Game), ("air", folder.Air), ("delete", "yes"));
            TestAssert.Equal(HttpStatusCode.Redirect, player.Status, player.Page);
            var done = await browser.EndAsync();
            TestAssert.True(done.Contains("A player install on this PC runs from the folders you picked, so they stay where they are, and Amberline uses copies.",
                StringComparison.Ordinal), done);
            TestAssert.Equal(originals, Snapshot(server), "the player install's folders stay");
            TestAssert.Equal(Snapshot(folder.Game), Snapshot(Path.Combine(folder.Install, @"game\projects\lol_game_client\releases\0.0.0.51")), "the copy");
        });
    }

    /// <summary>
    /// NOT RETAIL (batch 765): "Use the client zip…" in a one-folder install: the page offers it with the install folder, a closed file window
    /// changes nothing, a zip without the AIR client is refused with Try again for the same zip, and the client zip's game and AIR client are
    /// unpacked beside the install, moved in and named in platform.json; the zip is byte-identical and no staging folder is left. The zips are
    /// small synthetic ones made from the check's own stand-ins (<see cref="ClientZipChecks.Bundle"/>). Round 2: the zip's 2011 launcher goes beside
    /// the install, platform.json names it, and Continue carries it.
    /// </summary>
    [Check("server.host-folder-copy-client-zip", TimeoutSeconds = 180)]
    public static void TheClientZipIsUnpackedIntoTheInstall() => ClientZipAsync().GetAwaiter().GetResult();

    private static async Task ClientZipAsync()
    {
        var picks = new Queue<string?>();
        var launcherSha = "";
        var folder = OneFolder.Create("league-copy-zip", downloads =>
        {
            launcherSha = ClassicLauncherChecks.WriteLauncherFiles(Path.Combine(downloads, "launcher"));
            Write(Path.Combine(downloads, "launcher", "releasemanifest"), "file list");
            return Download(downloads);
        }, chooseZip: _ => picks.Dequeue(), launcherSha256: () => launcherSha);
        var zip = ClientZipChecks.Bundle(Path.Combine(folder.Downloads, "client bundle.zip"), "x/", folder.Game, [folder.Language], folder.Air,
            Path.Combine(folder.Downloads, "launcher"));
        var half = ClientZipChecks.Bundle(Path.Combine(folder.Downloads, "half.zip"), "", folder.Game, [folder.Language], null, null);
        var zipSha = RetailClients.Sha256(zip);
        var originals = Snapshot(Path.Combine(folder.Downloads, "League 2011"));
        await folder.RunAsync(async browser =>
        {
            var page = await browser.Client.GetStringAsync("/setup");
            TestAssert.True(page.Contains("<button id=zip-choose name=choose value=zip aria-describedby=zip-hint>Use the client zip…</button>", StringComparison.Ordinal) &&
                page.Contains($"into {Encode(folder.Install)}; the zip stays as it is.", StringComparison.Ordinal), "the page offers the client zip: " + page);

            picks.Enqueue(null);
            var closed = await browser.PostAsync("/setup", ("game", ""), ("air", ""), ("choose", "zip"));
            TestAssert.True(closed.Status == HttpStatusCode.OK && closed.Page.Contains("<h1>Your game files</h1>", StringComparison.Ordinal), "a closed window: " + closed.Page);

            picks.Enqueue(half);
            var refused = await browser.PostAsync("/setup", ("game", ""), ("air", ""), ("choose", "zip"));
            TestAssert.Equal(HttpStatusCode.Redirect, refused.Status, refused.Page);
            var failed = await browser.EndAsync();
            TestAssert.True(failed.Contains($"This zip has no AIR client in it, so it is not the client zip ({ClientZip.BundleName}). Nothing came into the install folder, " +
                "and the client zip is as it was.", StringComparison.Ordinal) && failed.Contains($"name=zip value=\"{Encode(half)}\"", StringComparison.Ordinal) &&
                failed.Contains("Try again</button>", StringComparison.Ordinal), "the refusal and its Try again: " + failed);

            picks.Enqueue(zip);
            var started = await browser.PostAsync("/setup", ("game", ""), ("air", ""), ("choose", "zip"));
            TestAssert.Equal(HttpStatusCode.Redirect, started.Status, started.Page);
            var done = await browser.EndAsync();
            var inside = Path.Combine(folder.Install, @"game\projects\lol_game_client\releases\0.0.0.51");
            TestAssert.True(done.Contains($"<h1>Your game files are in {Encode(folder.Install)}</h1>", StringComparison.Ordinal) &&
                done.Contains($"name=game value=\"{Encode(inside)}\"", StringComparison.Ordinal), "the done page carries the folders inside: " + done);
            TestAssert.Equal(Snapshot(folder.Game), Snapshot(inside), "the game came in from the zip");
            TestAssert.Equal(Snapshot(folder.Language), Snapshot(Path.Combine(folder.Install, @"game\projects\lol_game_client_en_us")), "its language project");
            TestAssert.Equal(Snapshot(folder.Air), Snapshot(Path.Combine(folder.Install, "air")), "the AIR client came in from the zip");
            folder.Names(inside, Path.Combine(folder.Install, "air"));
            var beside = folder.Install + "-launcher";
            TestAssert.True(File.Exists(Path.Combine(beside, "LoLLauncher.exe")) && done.Contains($"name=launcher value=\"{Encode(beside)}\"", StringComparison.Ordinal) &&
                done.Contains($"The client zip&#39;s 2011 launcher is in {Encode(beside)}, beside the install folder.", StringComparison.Ordinal),
                "the launcher beside the install, and Continue carries it: " + done);
            using (var settings = JsonDocument.Parse(File.ReadAllText(folder.Settings)))
                TestAssert.Equal(beside, settings.RootElement.GetProperty("launcherRoot").GetString(), "platform.json names the launcher beside the install");
            folder.NothingHalfDone();
            TestAssert.False(Directory.Exists(folder.Install + ".client-zip"), "no staging folder is left");
            TestAssert.Equal(zipSha, RetailClients.Sha256(zip), "the zip is byte-identical");
            TestAssert.Equal(originals, Snapshot(Path.Combine(folder.Downloads, "League 2011")), "the folders the zip was made from are untouched");
        });
    }

    private const string HostFolderCopyPath = "/setup/copy";

    /// <summary>
    /// A synthetic download: the game (with a few data files and its locale) at <c>parent\live\projects\lol_game_client\releases\0.0.0.51</c>,
    /// its language project beside it, and the AIR client at <paramref name="air"/>.
    /// </summary>
    internal static (string Game, string Air) Download(string parent, string air)
    {
        var game = HostFolderChecks.Game(Path.Combine(parent, "live", "projects", "lol_game_client", "releases", "0.0.0.51"));
        Write(Path.Combine(game, "DATA", "CFG", "Locale.cfg"), "[General]\r\nLanguageLocaleRegion=en_US\r\n");
        for (var index = 0; index < 6; index++) File.WriteAllBytes(Path.Combine(game, "DATA", $"file{index}.bin"), RandomNumberGenerator.GetBytes(16 * 1024));
        Write(Path.Combine(parent, "live", "projects", "lol_game_client_en_us", "releases", "0.0.0.20", "DATA", "Menu", "fontconfig_en_US.txt"), "stand-in");
        return (game, HostFolderChecks.Air(air));
    }

    private static (string Game, string Air) Download(string downloads) => Download(Path.Combine(downloads, "League 2011"), Path.Combine(downloads, "League 2011", "lol_air_client"));

    /// <summary>
    /// The host's one-folder install in setup mode: the check's Home with batch 637's marker beside its app folder (and without Home's
    /// own AIR stand-in, which would be the copy's target), and the clients in a download folder of their own.
    /// </summary>
    internal sealed class OneFolder
    {
        public required HostModeChecks.Home Home { get; init; }
        public required string Downloads { get; init; }
        public required string Game { get; init; }
        public required string Air { get; init; }
        public string Install => Home.Root;
        public string Language => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Game)!)!)!, "lol_game_client_en_us");
        public string Settings => Home.Loop.Paths.Settings;
        /// <summary>The folder step's copy seam: runs on each copied file.</summary>
        public Action<string> AfterCopy { get; set; } = _ => { };
        public Task<int>? Run { get; private set; }

        /// <param name="chooseZip">Batch 765: the file window's stand-in for "Use the client zip…"; null offers no client zip.</param>
        /// <param name="launcherSha256">Batch 765, round 2: the 2011 launcher's pin, read once the layout is made; null keeps the real one.</param>
        public static OneFolder Create(string prefix, Func<string, (string Game, string Air)> layout, Func<string, IReadOnlyList<string>>? records = null,
            Func<string, string?>? chooseZip = null, Func<string>? launcherSha256 = null)
        {
            var downloads = AirBootstrapChecks.TemporaryDirectory(prefix + "-downloads");
            var (game, air) = layout(downloads);
            OneFolder? made = null;
            var home = HostModeChecks.Home.Create(prefix, root => HostFolderChecks.Pins(game, air) with
                { Records = records?.Invoke(root) ?? [], AfterCopy = path => made!.AfterCopy(path), ChooseZip = chooseZip,
                  LauncherSha256 = launcherSha256?.Invoke() ?? new FolderSetup().LauncherSha256 });
            File.WriteAllText(Path.Combine(home.Root, FolderMove.InstallMarker), "{}");
            HostModeChecks.Delete(Path.Combine(home.Root, "air"));
            return made = new() { Home = home, Downloads = downloads, Game = game, Air = air };
        }

        /// <summary>Setup mode signed in, then <paramref name="body"/>; the loop stops and both folders go afterwards.</summary>
        public async Task RunAsync(Func<Browser, Task> body)
        {
            Run = Task.Run(() => HostMode.RunAsync(Home.Loop));
            try
            {
                using var client = Home.Client();
                await HostModeChecks.WaitForHealthAsync(client, "setup", Run);
                await Home.SignInAsync(client);
                var token = HostModeChecks.Token(await client.GetStringAsync("/setup"));
                await body(new Browser(client, token));
            }
            finally
            {
                await Home.StopAsync(Run);
                HostModeChecks.Delete(Home.Root);
                HostModeChecks.Delete(Downloads);
                // Batch 765: a client zip's staging folder beside the install, should a check have stopped before it was removed, and the
                // 2011 launcher it put beside the install.
                HostModeChecks.Delete(Home.Root + ".client-zip");
                HostModeChecks.Delete(Home.Root + "-launcher");
            }
        }

        /// <summary>No half copy or set-aside original anywhere in the install or the downloads.</summary>
        public void NothingHalfDone()
        {
            var left = new[] { Install, Downloads }.SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                .Where(path => path.EndsWith(".partial", StringComparison.Ordinal) || path.Contains(".amberline-moved", StringComparison.Ordinal)).ToList();
            TestAssert.Equal(0, left.Count, "nothing half done is left: " + string.Join(", ", left));
        }

        /// <summary>platform.json names these two folders.</summary>
        public void Names(string game, string air)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Settings));
            TestAssert.Equal(game, json.RootElement.GetProperty("dataRoot").GetString(), "platform.json's game folder");
            TestAssert.Equal(air, json.RootElement.GetProperty("airRoot").GetString(), "platform.json's AIR client folder");
        }
    }

    internal sealed record CopyStatus(string State, string Phase, string Detail, int Percent);

    /// <summary>The signed-in browser of a check: posts with the page's antiforgery token, follows the copy's status.</summary>
    internal sealed class Browser(HttpClient client, KeyValuePair<string, string> token)
    {
        public HttpClient Client => client;

        public async Task<(HttpStatusCode Status, string Page)> PostAsync(string path, params (string Key, string Value)[] fields)
        {
            using var response = await client.PostAsync(path, new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Key, field.Value)).Prepend(token)));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<CopyStatus> StatusAsync()
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync(HostFolderCopyPath + "/status.json"));
            var root = json.RootElement;
            return new(root.GetProperty("state").GetString()!, root.GetProperty("phase").GetString()!, root.GetProperty("detail").GetString()!, root.GetProperty("percent").GetInt32());
        }

        /// <summary>The copy's page once its status says it ended.</summary>
        public async Task<string> EndAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while ((await StatusAsync()).State == "copying")
            {
                TestAssert.True(DateTime.UtcNow < deadline, "the copy ended within a minute");
                await Task.Delay(50);
            }
            return await client.GetStringAsync(HostFolderCopyPath);
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Snapshot(string folder) => Directory.Exists(folder) ? HostInstallChecks.Snapshot(folder) : "";

    private static string JsonText(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
