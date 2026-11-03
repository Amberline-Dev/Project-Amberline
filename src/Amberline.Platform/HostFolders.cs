using System.Net;
using System.Text.Json;
using Amberline.Host;
using Amberline.Platform.Folders;
using Amberline.Rads;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

public enum FolderKind { Game, Air }

/// <summary>A folder searched to its limits: how many levels below it, and how many folders in all.</summary>
public sealed record SearchPlace(string Folder, int Depth, int Folders);

/// <summary>
/// Where the setup page looks for the two folders and how it asks for one. The defaults look nowhere and open no window, so a check
/// never reads a real folder; <see cref="HostMode.MainAsync"/> passes <see cref="ThisPc"/>.
/// </summary>
public sealed record FolderSetup
{
    /// <summary>
    /// Files that name folders: this PC's one-folder install pointer (<c>folder</c>) and the player install records (<c>install</c>,
    /// <c>game</c>, <c>air</c>; batch 619). Each folder named is checked and searched inside.
    /// </summary>
    public IReadOnlyList<string> Records { get; init; } = [];

    /// <summary>Searched in order when no saved or recorded folder passes.</summary>
    public IReadOnlyList<SearchPlace> Places { get; init; } = [];

    /// <summary>The most time the first look takes for both folders together; the page says so while it looks.</summary>
    public TimeSpan SearchTime { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The folder window: its title in, the chosen folder out, null when cancelled. Null: no window, the address field only.</summary>
    public Func<string, string?>? Choose { get; init; }

    /// <summary>DIAGNOSTICS: test seam. The SHA-256 the two stock executables must have; checks pass their fixtures' own.</summary>
    public string GameSha256 { get; init; } = RetailClients.GameExecutableSha256;

    public string AirSha256 { get; init; } = RetailClients.LolClientSha256;

    /// <summary>DIAGNOSTICS: test seam. Runs on each file the folder step copies into a one-folder install (<see cref="HostFolderCopy"/>).</summary>
    public Action<string>? AfterCopy { get; init; }

    /// <summary>
    /// This PC: this install's own folder (where this program runs from), the user's own folders and the usual game folders
    /// (START-HERE's list), then every fixed drive, each searched <see cref="HostFolders.SearchDepth"/> levels deep, and the Windows
    /// folder window. The one-folder install's pointer and the player install records are <see cref="Records"/>. The first look's time
    /// limit (<see cref="SearchTime"/>) bounds the whole search.
    /// </summary>
    public static FolderSetup ThisPc()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var places = new List<SearchPlace>();
        // This install's own folder: the one-folder install this program runs from (its game and AIR copies sit inside it).
        if (FolderMove.HostInstallation(AppContext.BaseDirectory) is { } own) places.Add(new(own, HostFolders.SearchDepth, HostFolders.SearchFolders));
        places.Add(new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), HostFolders.SearchDepth, HostFolders.SearchFolders));
        // Program Files is never searched as a whole (see HostFolders.Skipped), so its usual game folders are places of their own.
        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            foreach (var name in new[] { "League of Legends", "Riot Games" })
                if (Environment.GetFolderPath(programs) is { Length: > 0 } root && Directory.Exists(Path.Combine(root, name)))
                    places.Add(new(Path.Combine(root, name), HostFolders.SearchDepth, HostFolders.SearchFolders));
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) places.Add(new(drive.RootDirectory.FullName, HostFolders.SearchDepth, HostFolders.SearchFolders)); }
            catch (IOException) { }
        }
        return new()
        {
            Records = [FolderMove.PointerPath(localAppData), .. FolderMove.PlayerRecords(localAppData)],
            Places = places.Where(place => place.Folder.Length > 0).ToList(),
            Choose = OperatingSystem.IsWindows() ? FolderDialog.Choose : null,
        };
    }
}

/// <summary>
/// One folder row of the setup page: the folder looked at (null when none), what is wrong with it in plain words (null when it can
/// be used) and the warnings that do not stop hosting.
/// </summary>
public sealed record FolderCheck(string? Folder, string? Problem, IReadOnlyList<string> Warnings)
{
    public static readonly FolderCheck NotFound = new(null, null, []);
    public bool Ok => Folder is not null && Problem is null;
}

/// <summary>
/// NOT RETAIL: the host's folder step (Amberline Host H3). Retail ran its servers on Riot's own copies; here the host points
/// Amberline at the game and AIR folders they downloaded. The checks are tools/build-server.ps1's input checks in plain words
/// (the game: League of Legends.exe with the 1.0.0.126 SHA-256, DATA, its Scripts and BuildingBlocks, LEVELS and the five maps;
/// the AIR client: the stock layout of <see cref="AirSourceLayout"/>, LolClient.exe's SHA-256 and the champion list's
/// AirGeneratedContent.swf), plus the stock client's folder hazards as warnings. A chosen folder that is not itself the game is
/// searched inside, so a download's root resolves to its release folder.
/// </summary>
public static class HostFolders
{
    public const string GameExecutable = "League of Legends.exe";

    /// <summary>A chosen folder is searched this deep: an install's game\projects\lol_game_client\releases\0.0.0.51 is five levels.</summary>
    public const int InsideDepth = 6;

    public const int InsideFolders = 5000;

    /// <summary>
    /// The first look's search of a place: eight levels deep (a download's release folder is seven below the user's folder, and START-HERE
    /// searches eight) and at most this many folders; the time limit of <see cref="FolderSetup.SearchTime"/> usually ends it first.
    /// </summary>
    public const int SearchDepth = 8;

    public const int SearchFolders = 60000;

    /// <summary>The first pass of the look: the top of every place, where both clients usually sit in a download.</summary>
    private const int ShallowDepth = 4;

    /// <summary>The folders searched above an AIR client found first: the game is usually within a few hundred of it, so the pass stays short.</summary>
    private const int BesideFolders = 5000;

    private static readonly string[] GameFolders =
        [@"DATA", @"DATA\Scripts", @"DATA\BuildingBlocks", @"LEVELS", @"LEVELS\Map1", @"LEVELS\Map2", @"LEVELS\Map3", @"LEVELS\Map4", @"LEVELS\Map8"];

    private static readonly string[] AirMarkers = ["LolClient.exe", "LolClient.swf"];

    private const string ChampionList = @"assets\swfs\AirGeneratedContent.swf";

    // Never searched: the system's own folders (Windows, Microsoft, $Recycle.Bin and the like), other users' profiles (the user's own is
    // a place of its own), package caches, build output and version control. START-HERE's list (tools/guide.ps1) plus .git.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "Program Files", "Program Files (x86)", "ProgramData", "Users", "AppData", "Common Files", "Reference Assemblies",
          "Internet Explorer", "System Volume Information", "node_modules", ".artifacts", ".git" };

    private static bool Skip(string name) => Skipped.Contains(name) || name.StartsWith('$') ||
        name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static readonly EnumerationOptions Children = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        // Hidden and system folders, and junctions or links, which can loop back to their own parents.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private static string Label(FolderKind kind) => kind == FolderKind.Game ? "game folder" : "AIR client folder";

    /// <summary>
    /// Checks a folder the host chose (window, pasted address or the saved setting): the folder itself, else the first folder inside it
    /// (breadth first, to <see cref="InsideDepth"/>) that passes; when none passes, the first one found and its problem.
    /// </summary>
    public static FolderCheck Check(FolderKind kind, string? chosen, FolderSetup setup)
    {
        var folder = Clean(chosen);
        if (folder is null || !Directory.Exists(folder))
            return new(null, $"Amberline cannot find that {Label(kind)}. Check its address, or choose the folder. (A-FILES-01)", []);
        var found = Find(folder, kind == FolderKind.Game ? [GameExecutable] : AirMarkers, InsideDepth, InsideFolders);
        if (found.Count == 0)
            return new(null, kind == FolderKind.Game
                ? "This folder does not contain the game. Choose the folder you downloaded the 2011 game into. (A-FILES-01)"
                : "This folder does not contain the AIR client (it holds an assets folder). Choose the folder you downloaded it into. (A-FILES-04)", []);
        FolderCheck? first = null;
        foreach (var candidate in found)
        {
            var check = Inspect(kind, candidate, setup);
            if (check.Ok) return check;
            first ??= check;
        }
        return first!;
    }

    /// <summary>The A-FILES-05 sentence when a saved folder is gone since the last start, else null.</summary>
    public static string? Gone(string game, string air) =>
        !Directory.Exists(game) ? $"The game folder you chose is gone (it was at {game}). Choose it again. (A-FILES-05)"
        : !Directory.Exists(air) ? $"The AIR client folder you chose is gone (it was at {air}). Choose it again. (A-FILES-05)"
        : null;

    /// <summary>
    /// The folders the page offers first: the saved ones, then the folders the records name (each checked as it is and searched inside),
    /// then the search places. The search runs in three passes within <see cref="FolderSetup.SearchTime"/> for both folders together:
    /// the top of every place first, then, once an AIR client is found and the game is not, the folders above that client (a download
    /// often holds both), then every place to its full depth. A saved folder that is gone or fails keeps its problem when nothing else
    /// is found.
    /// </summary>
    public static (FolderCheck Game, FolderCheck Air) Look(FolderSetup setup, string? savedGame, string? savedAir)
    {
        var until = DateTime.UtcNow + setup.SearchTime;
        var game = Saved(FolderKind.Game, savedGame, setup);
        var air = Saved(FolderKind.Air, savedAir, setup);
        var places = new List<SearchPlace>();
        foreach (var record in setup.Records)
        {
            var (install, recordGame, recordAir) = ReadRecord(record);
            foreach (var folder in new[] { install, recordGame, recordAir })
                if (folder is not null && Directory.Exists(folder)) places.Add(new(folder, SearchDepth, SearchFolders));
        }
        places.AddRange(setup.Places);
        places = places.DistinctBy(place => place.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        bool Done() => (game.Ok && air.Ok) || DateTime.UtcNow >= until;
        void Scan(string root, int depth, int folders)
        {
            foreach (var folder in Find(root, [GameExecutable, .. AirMarkers], depth, folders, until))
            {
                var kind = File.Exists(Path.Combine(folder, GameExecutable)) ? FolderKind.Game : FolderKind.Air;
                if (kind == FolderKind.Game && !game.Ok && Inspect(kind, folder, setup) is { Ok: true } g) game = g;
                if (kind == FolderKind.Air && !air.Ok && Inspect(kind, folder, setup) is { Ok: true } a) air = a;
            }
        }
        var besideSearched = false;
        foreach (var place in places)
        {
            if (Done()) break;
            Scan(place.Folder, Math.Min(ShallowDepth, place.Depth), place.Folders);
            // A download often holds the game beside the AIR client, so once one is found the folders above it are searched before the rest.
            if (air.Ok && !game.Ok && !besideSearched)
            {
                besideSearched = true;
                foreach (var above in AboveAir(air.Folder!))
                {
                    if (Done()) break;
                    Scan(above, SearchDepth, Math.Min(place.Folders, BesideFolders));
                }
            }
        }
        foreach (var place in places)
        {
            if (Done()) break;
            Scan(place.Folder, place.Depth, place.Folders);
        }
        return (game, air);
    }

    /// <summary>The folders up to three levels above an AIR client, nearest first. Never a drive's root.</summary>
    private static IEnumerable<string> AboveAir(string air)
    {
        var folder = air;
        for (var level = 0; level < 3 && Path.GetDirectoryName(folder) is { } parent && Path.GetDirectoryName(parent) is not null; level++)
        {
            folder = parent;
            yield return folder;
        }
    }

    /// <summary>
    /// Folders at or under <paramref name="root"/>, breadth first, that hold one of <paramref name="markers"/>. It never goes below a
    /// match, deeper than <paramref name="depth"/> levels, into more than <paramref name="folders"/> folders in all, past
    /// <paramref name="until"/> (when given), or into a hidden or system folder, a junction or a link, or a folder of <see cref="Skip"/>.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, IReadOnlyList<string> markers, int depth, int folders, DateTime? until = null)
    {
        var found = new List<string>();
        var queue = new Queue<(string Folder, int Level)>();
        queue.Enqueue((Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), 0));
        for (var visited = 0; queue.Count > 0 && visited < folders && (until is null || DateTime.UtcNow < until); visited++)
        {
            var (folder, level) = queue.Dequeue();
            if (markers.Any(marker => File.Exists(Path.Combine(folder, marker)))) { found.Add(folder); continue; }
            if (level == depth) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(folder, "*", Children))
                    if (!Skip(Path.GetFileName(child))) queue.Enqueue((child, level + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static FolderCheck Saved(FolderKind kind, string? saved, FolderSetup setup)
    {
        var folder = Clean(saved);
        if (folder is null) return FolderCheck.NotFound;
        if (!Directory.Exists(folder))
            return new(null, $"The {Label(kind)} you chose is gone (it was at {folder}). Choose it again. (A-FILES-05)", []);
        return Inspect(kind, folder, setup);
    }

    /// <summary>The checks of one folder that holds the game's or the AIR client's executable.</summary>
    private static FolderCheck Inspect(FolderKind kind, string folder, FolderSetup setup)
    {
        try
        {
            return kind == FolderKind.Game ? InspectGame(folder, setup.GameSha256) : InspectAir(folder, setup.AirSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(folder, $"Amberline cannot read the files in this folder ({ex.Message}). Close programs that use them, then try again.", []);
        }
    }

    private static FolderCheck InspectGame(string folder, string sha256)
    {
        var executable = Path.Combine(folder, GameExecutable);
        if (!File.Exists(executable))
            return new(folder, "This folder does not contain the game. Choose the folder you downloaded the 2011 game into. (A-FILES-01)", []);
        if (RetailClients.Sha256(executable) != sha256)
            return new(folder, "This folder has the game, but not the 2011 version Amberline needs (version 1.0.0.126). Choose the " +
                @"folder from the download for version 1.0.0.126 (it contains projects\lol_game_client\releases\0.0.0.51). (A-FILES-02)", []);
        if (GameFolders.FirstOrDefault(name => !Directory.Exists(Path.Combine(folder, name))) is { } missing)
            return new(folder, $"The game folder is incomplete: {missing} is missing. Download it again. (A-FILES-03)", []);
        return new(folder, null, GameWarnings(folder));
    }

    private static FolderCheck InspectAir(string folder, string sha256)
    {
        var problems = AirSourceLayout.Problems(folder).ToList();
        if (!problems.Contains(@"assets\swfs\") && !File.Exists(Path.Combine(folder, ChampionList))) problems.Add(ChampionList);
        if (problems.Count > 0)
            return new(folder, $"This AIR client is incomplete: {problems.Count} {(problems.Count == 1 ? "file is" : "files are")} missing or " +
                $"damaged (for example {problems[0].Replace("LolClient.swf", "the AIR client's .swf file").Replace("LolClient.exe", "the AIR client's executable")}). Download it again. (A-FILES-04)", []);
        if (RetailClients.Sha256(Path.Combine(folder, "LolClient.exe")) != sha256)
            return new(folder, "This is not the 2011 AIR client Amberline needs (version 1.0.0.126). Choose the AIR client from the same " +
                "download as the game. (A-FILES-04)", []);
        return new(folder, null, []);
    }

    /// <summary>What may stop the host's own game (Play on this PC) from this folder; hosting works regardless.</summary>
    public static IReadOnlyList<string> GameWarnings(string folder)
    {
        var warnings = new List<string>();
        var locale = GameLanguagePack.ResolveLocale(folder);
        if (GameLanguagePack.FindNewestRelease(folder, locale) is null)
            warnings.Add($"Voices and fonts may be missing: lol_game_client_{locale.ToLowerInvariant()} is not next to lol_game_client.");
        foreach (var hazard in GameFolderReport.Hazards(folder, GameFolderReport.SystemAnsi()))
            warnings.Add(hazard switch
            {
                "path-too-long" => @"This folder's path is long; the game may not start from it. Move it closer to C:\.",
                "network-share-path" => "This folder is on another computer (a network share); the game may not start from it. Copy it to this PC.",
                _ => "This folder's path has letters the 2011 game cannot read; the game may not start from it. Move it to a folder whose " +
                    "name has only plain letters and digits.",
            });
        // The client builds its spell list through short file names unless it runs from the managed layout, which needs this folder shape.
        if (ManagedLayout.ParseGameDirectory(folder) is null && GameFolderReport.ShortNames(folder) == "off")
            warnings.Add(@"This drive keeps no short file names, so the game stops while loading from this folder. Keep the game in a " +
                @"folder like projects\lol_game_client\releases\0.0.0.51, or move it to drive C:.");
        return warnings;
    }

    /// <summary>The folders a record names: its install folder (<c>install</c>, or the pointer's <c>folder</c>), its game and its AIR client.</summary>
    internal static (string? Install, string? Game, string? Air) ReadRecord(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            string? Text(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? Clean(value.GetString()) : null;
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (Text("install") ?? Text("folder"), Text("game"), Text("air")) : (null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (null, null, null); }
    }

    private static string? Clean(string? folder)
    {
        folder = folder?.Trim().Trim('"');
        return string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder) ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    /// <summary>
    /// The setup page's folder step on <c>/setup</c>: GET shows the rows; POST with <c>choose</c> opens the folder window for one row,
    /// with <c>paste</c> checks a pasted address for both, and otherwise ("Use these folders") checks both folders, saves them and
    /// restarts into the full Platform.
    /// </summary>
    internal static void MapSetup(WebApplication app, HostLoop loop) => Map(app, loop, new("/setup", 2, HostSteps.NamePath, null));

    /// <summary>
    /// Settings' folder page (batch 641, <see cref="HostSettingsPage"/>): the same rows, checks and save as the first run's step. The
    /// game data is read once per process, so saving restarts Amberline into a fresh one (batch 636); never while a game runs.
    /// </summary>
    internal static void MapSettings(WebApplication app, HostLoop loop, Func<int> games) =>
        Map(app, loop, new(HostSettingsPage.FoldersPath, 0, HostSettingsPage.PagePath, games));

    /// <summary>Where the folder step is shown: its address, its step number (0: none), its Back, and the running games (null: none can run).</summary>
    internal sealed record FolderPlace(string Path, int Step, string Back, Func<int>? Games);

    /// <summary>A folder page's heading (and its copy pages'): Setup keeps its own words; reached from Settings, the words of its link (batch 649).</summary>
    internal static string Heading(FolderPlace place, string setupWords) => place.Games is null ? setupWords : "Change the folders";

    private static void Map(WebApplication app, HostLoop loop, FolderPlace place)
    {
        var setup = loop.Folders;
        var saved = HostMode.ReadOrDefault(loop.Paths.Settings);
        // Setup mode looks at once; Settings looks at the saved folders when the page is first opened.
        Task<(FolderCheck Game, FolderCheck Air)>? first = null;
        Task<(FolderCheck Game, FolderCheck Air)> Looked() => first ??= Task.Run(() => Look(setup, saved.DataRoot, saved.AirRoot));
        if (place.Games is null) Looked();
        // Batch 642: a one-folder install offers to copy the picked folders in before they are saved (Settings' folder page too).
        var copy = HostFolderCopy.For(loop, place);
        copy?.Map(app);
        app.MapGet(place.Path, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var look = Looked();
            // A first look over a slow disk: a note that comes back by itself, instead of a page that seems stuck.
            if (await Task.WhenAny(look, Task.Delay(TimeSpan.FromSeconds(3))) != look)
                return HostMode.Page($"<h1>{Heading(place, "Your game files")}</h1><p>Looking for your game folders. This takes up to " +
                    $"{(int)setup.SearchTime.TotalSeconds} seconds.</p>", refresh: place.Path, step: place.Step);
            var (game, air) = await look;
            return Form(Program.RegistrationToken(context, antiforgery), loop, place, game, air, null, looked: setup.Places.Count > 0);
        });
        app.MapPost(place.Path, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var token = Program.RegistrationToken(context, antiforgery);
            Microsoft.AspNetCore.Http.IFormCollection form;
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (AntiforgeryValidationException)
            {
                var (g, a) = await Looked();
                return Form(token, loop, place, g, a, "The page expired. Please try again.", false, StatusCodes.Status400BadRequest);
            }
            // The rows come back in hidden fields; an empty one is a row not chosen yet.
            FolderCheck Row(FolderKind kind, string value) => string.IsNullOrWhiteSpace(value) ? FolderCheck.NotFound : Check(kind, value, setup);
            var game = Row(FolderKind.Game, form["game"].ToString());
            var air = Row(FolderKind.Air, form["air"].ToString());
            if (form.ContainsKey("choose"))
            {
                var kind = form["choose"] == "air" ? FolderKind.Air : FolderKind.Game;
                if (setup.Choose is not { } choose)
                    return Form(token, loop, place, game, air, "Paste the folder's address instead.", false, StatusCodes.Status400BadRequest);
                string? picked;
                try { picked = choose(kind == FolderKind.Game ? "Choose the game folder" : "Choose the AIR client folder"); }
                catch (InvalidOperationException ex) { return Form(token, loop, place, game, air, ex.Message, false); }
                if (picked is not null && kind == FolderKind.Game) game = Check(kind, picked, setup);
                else if (picked is not null) air = Check(kind, picked, setup);
                return Form(token, loop, place, game, air, null, false);
            }
            if (form.ContainsKey("paste"))
            {
                // One address for both rows: a row changes when the address holds (or contains) that kind of folder.
                var pastedGame = Check(FolderKind.Game, form["paste"], setup);
                var pastedAir = Check(FolderKind.Air, form["paste"], setup);
                if (pastedGame.Folder is not null) game = pastedGame;
                if (pastedAir.Folder is not null) air = pastedAir;
                var note = pastedGame.Folder is not null || pastedAir.Folder is not null ? null
                    : Clean(form["paste"]) is { } pasted && Directory.Exists(pasted)
                        ? "This folder contains neither the game nor its AIR client. Choose the folder you downloaded them into. (A-FILES-01)"
                        : "Amberline cannot find that folder. Check its address. (A-FILES-01)";
                return Form(token, loop, place, game, air, note, false, note is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
            }
            if (game == FolderCheck.NotFound) game = game with { Problem = "Choose the game folder first." };
            if (air == FolderCheck.NotFound) air = air with { Problem = "Choose the AIR client folder first." };
            if (!game.Ok || !air.Ok) return Form(token, loop, place, game, air, null, false, StatusCodes.Status400BadRequest);
            if (place.Games?.Invoke() > 0) return Busy(token, loop, place, game, air);
            // "Use them where they are" (keep) saves the picked folders as they are.
            if (copy is not null && !form.ContainsKey("keep") && copy.Offer(token, game, air) is { } offer) return offer;
            HostMode.Change(loop.Paths.Settings, saved => saved with { DataRoot = game.Folder!, AirRoot = air.Folder, ServerExecutable = null! });
            context.Response.OnCompleted(() => { loop.Restart(); return Task.CompletedTask; });
            // Batch 649: Settings' folders come back to Settings with a notice; setup's come back to the first-run steps, as before.
            return HostMode.Page("<h1>Starting Amberline</h1><p>This page comes back by itself in a few seconds.</p>",
                refresh: place.Games is null ? loop.ReopenPath : loop.ReopenPathTo("settings"), nav: false);
        });
    }

    /// <summary>Settings' refusal while a game runs: the folders' save restarts Amberline, and so may their copy's end.</summary>
    internal static IResult Busy(string token, HostLoop loop, FolderPlace place, FolderCheck game, FolderCheck air) =>
        Form(token, loop, place, game, air, null, false, StatusCodes.Status409Conflict,
            "A game is running. Change the folders when it has ended.");

    /// <param name="problem">A problem with no field of its own (a running game), first in the summary.</param>
    internal static IResult Form(string token, HostLoop loop, FolderPlace place, FolderCheck game, FolderCheck air, string? note, bool looked,
        int status = StatusCodes.Status200OK, string? problem = null)
    {
        string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");
        var choose = loop.Folders.Choose is not null;
        var hidden = $"{token}<input type=hidden name=game value=\"{Encode(game.Folder)}\"><input type=hidden name=air value=\"{Encode(air.Folder)}\">";
        // A row's problem is fixed with its Choose button, or with the pasted address when this PC has no folder window.
        string Target(string name) => choose ? name + "-choose" : "paste";
        string Row(string name, string title, FolderCheck check)
        {
            var state = check.Ok ? $"<span class=ok><span aria-hidden=true>&#10003;</span> Found:</span> {Encode(check.Folder)}"
                : check.Folder is not null ? $"<span class=no><span aria-hidden=true>&#10007;</span> Not usable:</span> {Encode(check.Folder)}"
                : $"<span class=no><span aria-hidden=true>&#10007;</span> Not found{(check.Problem is null ? " yet" : "")}</span>";
            var described = $"{name}-title{(check.Problem is null ? "" : $" {name}-error")}";
            // Batch 652: one name for both states, and the wait says where the folder window opens.
            var button = choose
                ? $"<form method=post action={place.Path}{HostPages.Wait("Opening the folder window. It may open behind this browser window.")}>{hidden}" +
                  $"<button id={name}-choose class=secondary name=choose value={name} aria-describedby=\"{described}\">Choose folder…</button></form>"
                : "";
            var below = check.Problem is not null ? HostPages.FieldError(name, check.Problem)
                : string.Concat(check.Warnings.Select(warning => $"<p class=warn>{Encode(warning)}</p>"));
            return $"<section class=folder aria-labelledby={name}-title><h2 id={name}-title>{title}</h2><p>{state}</p>{below}{button}</section>";
        }
        // The start's problem stands, but one about the saved folders (none yet, or gone) goes once both rows are usable (batch 643).
        var aboutFolders = loop.Problem is HostPaths.NoFolders || loop.Problem?.EndsWith("(A-FILES-05)", StringComparison.Ordinal) == true;
        var top = problem ?? (loop.Problem is { } start && !(aboutFolders && game.Ok && air.Ok) && start != game.Problem && start != air.Problem ? start : null);
        return HostMode.Page($$"""
            <h1>{{Heading(place, "Your game files")}}</h1>
            <p>{{(looked ? "Amberline looked for them on this PC." : "Choose the 2011 game and its AIR client.")}}</p>
            {{HostPages.Errors((top, null), (game.Problem, Target("game")), (air.Problem, Target("air")), (note, "paste"))}}
            {{Row("game", "Game folder", game)}}
            {{Row("air", "AIR client folder", air)}}
            <form method=post action={{place.Path}}{{HostPages.Wait("Looking in that folder. This takes a few seconds.")}}>{{hidden}}
            {{HostPages.Field("paste", "Or paste a folder's address", "Any folder that holds the game or the AIR client.",
                note, "maxlength=260 placeholder=\"for example C:\\Games\\2011 game\"", required: null)}}
            <p><button class=secondary>Check</button></p></form>
            {{(place.Games is null ? "" : "<p class=tip>Saving restarts Amberline for a few seconds.</p>")}}
            <form method=post action={{place.Path}}{{HostPages.Wait("Checking the folders. This takes a few seconds.")}}>{{hidden}}<div class=actions><a href={{place.Back}}>{{(place.Games is null ? "Back" : "Cancel")}}</a><button>{{(place.Games is null ? "Continue" : "Save")}}</button></div></form>
            """, status, step: place.Step);
    }
}
