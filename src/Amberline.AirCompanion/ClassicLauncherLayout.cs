using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amberline.AirCompanion.Rads;

namespace Amberline.AirCompanion;

/// <param name="Root">The RADS root (<see cref="GameManagedLayout.ResolveRoot"/>).</param>
/// <param name="NewsPort">The loopback HTTP port that serves <c>/launcher/news</c> (AIR's endpoints use the same one).</param>
internal sealed record ClassicLayoutRequest(string Root, string LauncherFolder, string AirFolder, string GameFolder, int NewsPort);

/// <summary>The launcher's release folder (its required working folder) and the EXE copy in it.</summary>
internal sealed record ClassicLayout(string Deploy, string LauncherExecutable);

/// <summary>
/// NOT RETAIL: retail's update service built this layout under the RADS root and kept it current; it no longer exists, so the classic start
/// builds the same layout from the player's own launcher, AIR and game files (design 2026-11-12 §3 and §6). The generated files (English base
/// translations for the other languages, region overrides pointing the news at
/// our page, the solution manifests, a release manifest when the player has none) exist only here.
/// Add-only: every path created is recorded in <see cref="StampName"/> (relative to the root, one per line, junctions marked "J ") before it is
/// created. Ours means the path itself is in the stamp: only those are refreshed or removed. A needed path that exists and is not ours, or a
/// release folder of one of <see cref="OwnProjects"/> that is not ours (a real old install: the stand-in would report its higher version), refuses
/// the start by name before anything is written. The system cfgs are created once and then left to the launcher, which rewrites them when the
/// player changes region.
/// </summary>
internal static class ClassicLauncherLayout
{
    internal const string StampName = "classic.stamp";
    private const string JunctionMark = "J ";
    private const string LauncherProject = @"projects\lol_launcher", LauncherVersion = "0.0.0.49";
    private const string Solution = @"solutions\lol_game_client_sln\releases\0.0.0.81";

    /// <summary>The junctions to the AIR and game folders, relative to the root: the launcher starts AIR and the game through them.</summary>
    private const string AirDeploy = @"projects\lol_air_client\releases\0.0.0.126\deploy", GameDeploy = Solution + @"\deploy";

    /// <summary>The launcher, AIR and the game as a classic start runs them under <paramref name="root"/>: the launcher's copy in its release
    /// folder, and AIR and the game through the layout's junctions (design 2026-11-12 §3 defines them; the launcher starts both by those paths).</summary>
    internal static (string Launcher, string Air, string Game) Images(string root) => (
        Path.Combine(root, LauncherProject, "releases", LauncherVersion, "deploy", "LoLLauncher.exe"),
        Path.Combine(root, AirDeploy, "LolClient.exe"),
        Path.Combine(root, GameDeploy, "League of Legends.exe"));

    /// <summary>The projects and solution that hold nothing but this layout: a stamped folder at or inside one of them is removed whole. Every
    /// other stamped folder (projects, solutions, system; projects\lol_game_client, the game's managed layout, is never stamped) is shared and
    /// only ever removed when empty. The same list names the release folders an old install would clash with.</summary>
    private static readonly string[] OwnProjects =
    [
        LauncherProject, @"projects\lol_air_client", @"projects\lol_air_client_config_na", @"projects\lol_air_client_config_euw",
        @"projects\lol_air_client_config_eune", @"solutions\lol_game_client_sln",
    ];

    /// <param name="Bytes">A file's content; null with <paramref name="LinkTo"/> null is a folder.</param>
    /// <param name="Once">Created when missing, never compared or rewritten (the system cfgs).</param>
    private sealed record Item(string Path, byte[]? Bytes = null, string? LinkTo = null, bool Once = false);

    /// <summary>The seven region overrides the launcher loads after the base translation (design 2026-11-12 §6), each only the news lines.</summary>
    internal static IReadOnlyDictionary<string, string> RegionOverrides(int newsPort)
    {
        var news = string.Create(CultureInfo.InvariantCulture, $"tr \"url_host_news\" = \"127.0.0.1:{newsPort}\"\r\ntr \"url_path_news\" = \"/launcher/news\"\r\n");
        return new[] { "NA_en_US", "EUW_en_gb", "EUW_de_de", "EUW_es_es", "EUW_fr_fr", "EUNE_en_gb", "EUNE_pl_pl" }.ToDictionary(name => name + ".tra", _ => news);
    }

    /// <summary>
    /// Builds or refreshes the layout. A refusal (one sentence naming the path or the problem) means the start falls back: a space in the root
    /// or a path that is not ours refuses before anything is written; a failure halfway leaves only stamped paths behind. Never throws: Task 3
    /// turns a refusal into the usual start, and a throw would leave the player without a way in.
    /// </summary>
    internal static (ClassicLayout? Layout, string? Refusal) Ensure(ClassicLayoutRequest request, Action<string> log)
    {
        (ClassicLayout?, string?) Refuse(string reason)
        {
            log("classic-layout: refused: " + reason);
            return (null, reason);
        }

        try
        {
            var root = Path.GetFullPath(request.Root);
            // The launcher starts AIR and the game with unquoted command lines (design 2026-11-12 §3).
            if (root.Contains(' ')) return Refuse($"The folder {root} has a space in its path, so the classic launcher cannot start from it.");
            var items = Plan(request);
            var stampPath = Path.Combine(root, LauncherProject, StampName);
            var lines = new HashSet<string>(File.Exists(stampPath) ? File.ReadAllLines(stampPath).Where(line => line.Length > 0) : [], StringComparer.OrdinalIgnoreCase);
            var owned = new HashSet<string>(lines.Select(StampedPath), StringComparer.OrdinalIgnoreCase);
            const string NotOurs = "already exists and was not created by Amberline.";
            foreach (var item in items)
            {
                var full = Path.Combine(root, item.Path);
                if (Taken(full) && (!owned.Contains(item.Path) || (item.LinkTo is not null) != IsLink(full))) return Refuse($"{full} {NotOurs}");
            }
            foreach (var releases in OwnProjects.Select(project => Path.Combine(root, project, "releases")).Where(Directory.Exists))
                if (Directory.EnumerateDirectories(releases).FirstOrDefault(folder => !owned.Contains(Path.GetRelativePath(root, folder))) is { } old)
                    return Refuse($"{old} {NotOurs}");

            var (created, changed) = (0, 0);
            void Record(string line)
            {
                if (!lines.Add(line)) return;   // already stamped by a start that stopped before creating it
                File.AppendAllText(stampPath, line + "\r\n", Encoding.ASCII);
                created++;
            }
            List<string> Missing(string relative)
            {
                var missing = new List<string>();
                for (var path = relative; path.Length > 0 && !Taken(Path.Combine(root, path)); path = Path.GetDirectoryName(path)!) missing.Insert(0, path);
                return missing;
            }
            // Stamped before created: a kill or an error in between leaves a stamped path that is missing (Remove and Ensure skip or make it),
            // never an unstamped path of ours that would refuse every later start.
            void Folder(string relative)
            {
                var missing = Missing(relative);
                missing.ForEach(Record);
                if (missing.Count > 0) Directory.CreateDirectory(Path.Combine(root, relative));
            }

            // The stamp's own folder is the one exception: the stamp cannot be written before its folder exists.
            var stampFolder = Missing(LauncherProject);
            Directory.CreateDirectory(Path.Combine(root, LauncherProject));
            stampFolder.ForEach(Record);
            foreach (var item in items)
            {
                var full = Path.Combine(root, item.Path);
                if (item.Bytes is null && item.LinkTo is null) { Folder(item.Path); continue; }
                Folder(Path.GetDirectoryName(item.Path)!);
                if (!Taken(full))
                {
                    Record(item.LinkTo is null ? item.Path : JunctionMark + item.Path);
                    if (item.LinkTo is { } target) Junction(full, target);
                    else Write(full, item.Bytes!);
                }
                else if (item.LinkTo is { } target)
                {
                    if (SamePath(new FileInfo(full).LinkTarget!, target)) continue;
                    Directory.Delete(full);   // not recursive: removes the link only, never what it points at
                    Junction(full, target);
                    log($"classic-layout: re-pointed {item.Path} to {target}");
                    changed++;
                }
                else if (!item.Once && !File.ReadAllBytes(full).AsSpan().SequenceEqual(item.Bytes!))
                {
                    File.WriteAllBytes(full, item.Bytes!);   // ours (checked above): refreshed in place
                    changed++;
                }
            }
            log($"classic-layout: {created} paths created, {changed} refreshed under {root}");
            var deploy = Path.Combine(root, LauncherProject, "releases", LauncherVersion, "deploy");
            return (new ClassicLayout(deploy, Path.Combine(deploy, "LoLLauncher.exe")), null);
        }
        // Every exception, not a list: besides I/O, a corrupt player manifest throws IndexOutOfRange, Overflow, InvalidData or OutOfMemory
        // (RadsLayout.ReadManifest trusts its counts) and Process.Start a Win32Exception.
        catch (Exception exception)
        {
            return Refuse($"The classic launcher's folders under {request.Root} could not be prepared ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    /// <summary>
    /// Removes the stamped paths: junctions first and as links only; then stamped folders inside <see cref="OwnProjects"/> whole, with what the
    /// launcher wrote inside them (a junction left inside is removed as a link, never followed); then stamped files; then the other stamped folders
    /// (projects, solutions, system), deepest first and only when empty, so the game's managed layout and anything else shared stays. Unstamped
    /// paths (the launcher's root log) stay, and so does a stamped path that has become a link someone else made; a stamp line with a ".." part or
    /// outside the root is skipped and logged. After a failure the stamp is
    /// rewritten with what is still there, so a later Remove retries and Ensure still knows its own paths. Returns the paths it removed.
    /// </summary>
    internal static IReadOnlyList<string> Remove(string root, Action<string> log)
    {
        root = Path.GetFullPath(root);
        var stampPath = Path.Combine(root, LauncherProject, StampName);
        if (!File.Exists(stampPath)) return [];
        var inside = root.TrimEnd('\\', '/') + @"\";
        var entries = new List<(string Line, bool Junction, bool Whole, int Depth, string Path)>();
        foreach (var line in File.ReadAllLines(stampPath).Where(line => line.Length > 0))
        {
            var stamped = StampedPath(line);
            var path = Path.GetFullPath(Path.Combine(root, stamped));
            // Ensure never writes a ".." part: such a line, or one that resolves to the root or outside it, is hand-edited and skipped. The rest is
            // classified on the resolved path, so no spelling of another folder passes for one of OwnProjects (removed whole).
            if (stamped.Split('\\', '/').Contains("..") || !path.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            {
                log($"classic-layout: skipped the stamp line {line}");
                continue;
            }
            var relative = Path.GetRelativePath(root, path);
            var whole = OwnProjects.Any(project => relative.Equals(project, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(project + @"\", StringComparison.OrdinalIgnoreCase));
            entries.Add((line, line.StartsWith(JunctionMark, StringComparison.Ordinal), whole, relative.Count(c => c == '\\'), path));
        }
        var removed = new List<string>();
        var failed = false;
        void Delete(string path, Action delete)
        {
            try { delete(); removed.Add(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"classic-layout: could not remove {path} ({exception.Message})");
                failed = true;
            }
        }

        var paths = entries.Where(entry => !entry.Junction).OrderBy(entry => entry.Depth).ToList();
        foreach (var entry in entries.Where(entry => entry.Junction))
            if (IsLink(entry.Path)) Delete(entry.Path, () => Directory.Delete(entry.Path));   // not recursive: the link only
        foreach (var entry in paths.Where(entry => entry.Whole))   // outermost first; what lay inside is then gone
            if (!IsLink(entry.Path) && Directory.Exists(entry.Path)) Delete(entry.Path, () => Directory.Delete(entry.Path, recursive: true));
        foreach (var entry in paths)
            if (!IsLink(entry.Path) && File.Exists(entry.Path)) Delete(entry.Path, () => File.Delete(entry.Path));
        foreach (var entry in Enumerable.Reverse(paths).Where(entry => !entry.Whole))   // shared folders: deepest first, only when empty
            if (!IsLink(entry.Path) && Directory.Exists(entry.Path) && !Directory.EnumerateFileSystemEntries(entry.Path).Any())
                Delete(entry.Path, () => Directory.Delete(entry.Path));
        if (failed)
        {
            // The stamp may have gone with its folder: write it again, with every stamped path that is still there (its folder too, recreated).
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
                File.WriteAllLines(stampPath, entries.Where(entry => Taken(entry.Path)).Select(entry => entry.Line), Encoding.ASCII);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"classic-layout: could not keep the stamp {stampPath} ({exception.Message})");
            }
        }
        else if (File.Exists(stampPath)) Delete(stampPath, () => File.Delete(stampPath));
        log($"classic-layout: removed {removed.Count} paths under {root}");
        return removed;
    }

    /// <summary>The launcher's project folder while a stamp still lists paths under <paramref name="root"/> (a Remove that failed keeps its stamp); else null.</summary>
    internal static string? Remaining(string root) =>
        File.Exists(Path.Combine(root, LauncherProject, StampName)) ? Path.Combine(root, LauncherProject) : null;

    /// <summary>Every path the layout needs, with its content, in creation order (relative to the root).</summary>
    private static List<Item> Plan(ClassicLayoutRequest request)
    {
        var launcher = Path.GetFullPath(request.LauncherFolder);
        var air = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.AirFolder));
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.GameFolder));
        var release = $@"{LauncherProject}\releases\{LauncherVersion}";
        var deploy = $@"{release}\deploy";

        // No eula_* file anywhere: an absent EULA means no EULA screen (design 2026-11-12 §6).
        var files = ClassicLauncherFiles.Required.Where(file => !file.StartsWith("eula_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file, file => File.ReadAllBytes(Path.Combine(launcher, file)), StringComparer.OrdinalIgnoreCase);
        var version = RadsLayout.ParseVersion(LauncherVersion);
        byte[] manifest;
        IReadOnlyList<RadsLayout.ManifestFile> entries;
        if (ClassicLauncherFiles.ManifestFor(launcher) is { } playerManifest)
        {
            manifest = File.ReadAllBytes(playerManifest);
            entries = RadsLayout.ReadManifest(playerManifest).Files;
        }
        else
        {
            // Mode 0, no managed flag: the launcher then opens each file from its working folder, which holds all of them.
            // Not proven: owner test (design 2026-11-12 §3).
            entries = [.. files.Select(file => new RadsLayout.ManifestFile(file.Key.Replace('\\', '/'), version, MD5.HashData(file.Value), 0, (uint)file.Value.Length, (uint)file.Value.Length))];
            var temp = Path.GetTempFileName();
            try { RadsLayout.WriteManifest(temp, "lol_launcher", version, entries); manifest = File.ReadAllBytes(temp); }
            finally { File.Delete(temp); }
        }

        static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
        // The RADS text reader strips one trailing CR or LF, so the two manifests use LF only (CRLF would leave "\r" in a binary-mode read).
        static byte[] Lines(IEnumerable<string> lines) => Ascii(string.Join("\n", lines) + "\n");
        var gameVersion = ManagedLayout.ParseGameDirectory(game) is { Project: "lol_game_client" } gameRelease ? gameRelease.Version : "0.0.0.51";
        string[] configurations = ["en_US", "en_GB", "de_DE", "es_ES", "fr_FR", "pl_PL"];
        var properties = File.ReadAllBytes(Path.Combine(air, "lol.properties"));

        List<Item> items =
        [
            new(@"system\launcher.cfg", Ascii("airConfigProject = lol_air_client_config_na\r\n"), Once: true),
            new(@"system\locale.cfg", Ascii("locale = en_us\r\n"), Once: true),
            new(@"system\system.cfg", Ascii("Region = NA\r\n"), Once: true),
            new(@"system\user.cfg", Ascii("disableP2P = true\r\n"), Once: true),   // the launcher crashes without it
            new(deploy),   // ours whole: the launcher writes its logs and temp\ in it
            new($@"{release}\releasemanifest", manifest),
            .. files.Select(file => new Item($@"{deploy}\{file.Key}", file.Value)),
            // English launcher text for the other languages (the player's launcher has only en_US and ru_RU).
            .. new[] { "en_gb", "de_de", "es_es", "fr_fr", "pl_pl" }.Select(locale => new Item($@"{deploy}\{locale}.tra", files["en_US.tra"])),
            .. RegionOverrides(request.NewsPort).Select(file => new Item($@"{deploy}\{file.Key}", Ascii(file.Value))),
            .. entries.Where(entry => files.ContainsKey(entry.Path.Replace('/', '\\'))).Select(entry => new Item(
                $@"{LauncherProject}\managedfiles\{VersionText(entry.Version)}\{entry.Path.Replace('/', '\\')}", files[entry.Path.Replace('/', '\\')])),
            new(AirDeploy, LinkTo: air),
            // Every region's config project: the launcher copies its lol.properties over AIR's at each update.
            .. new[] { "na", "euw", "eune" }.Select(region => new Item($@"projects\lol_air_client_config_{region}\releases\0.0.0.1\deploy\lol.properties", properties)),
            new(GameDeploy, LinkTo: game),
            new($@"{Solution}\solutionmanifest", Lines([
                "RADS Solution Manifest", "1.0.0.0", "lol_game_client_sln", "0.0.0.81",
                "1", "lol_game_client", gameVersion, "0", "0",                               // projects: name, version, priority, flags
                configurations.Length.ToString(CultureInfo.InvariantCulture),
                .. configurations.SelectMany(name => new[] { name, "0", "1", "lol_game_client" })])),   // configurations: name, flags, project count, names
            new($@"{Solution}\configurationmanifest", Lines(["RADS Configuration Manifest", "1.0.0.0", "en_US", "1", "lol_game_client"])),
        ];
        return items;
    }

    private static string VersionText(uint version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version >> 24}.{(version >> 16) & 255}.{(version >> 8) & 255}.{version & 255}");

    /// <summary>A stamp line's path, relative to the root (a junction's line carries the "J " mark).</summary>
    private static string StampedPath(string line) => line.StartsWith(JunctionMark, StringComparison.Ordinal) ? line[JunctionMark.Length..] : line;

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>True for a file, a folder or a link (also a dangling one) at the path.</summary>
    private static bool Taken(string path) => Path.Exists(path) || IsLink(path);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static void Write(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);   // CreateNew: never overwrites
        file.Write(bytes);
    }

    /// <summary>A directory junction (no administrator rights needed, unlike a symbolic link). Both paths are quoted, so cmd reads characters like &amp; literally.</summary>
    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("cmd.exe did not start");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"mklink /J {link} failed ({process.ExitCode}): {output.Trim()}");
    }
}
