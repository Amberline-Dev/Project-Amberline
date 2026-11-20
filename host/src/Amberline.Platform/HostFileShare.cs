using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Platform.Folders;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the host shares their own game and AIR client with players who have none (batch 681; the owner, 4 November 2026: "Where
/// the person hosting the server can decide if they want to also host the game and air client files for the players"; anyone with the
/// player app may download, off by default). Retail players got both from Riot's patcher. While Settings' switch is on
/// (<see cref="PlatformSettings.ShareFiles"/>), the friends' port (HTTPS with the certificate the invite link pins) serves two things more:
/// <see cref="Prefix"/>, the manifest (each file's path in the player's install, size and SHA-256), and <c>/files/&lt;path&gt;</c>, one
/// file of that manifest with ranges, so a download resumes. Nothing else of the host reaches that port (<see cref="Serves"/>).
/// <list type="bullet">
/// <item>The manifest is exactly the game's release folder, its language project beside it (as <see cref="FolderMove.LanguageProject"/>
/// takes it) and the AIR client's own entries (<see cref="FolderMove.AirEntries"/>), less what the clients write as they run (their logs,
/// crash dumps and the game's last command line, which names a match's server and key: <see cref="Private"/>). NOT RETAIL (batch 761, task 8):
/// while the host's 2011 launcher folder holds the accepted files, that release's files and its file list are listed too, under <c>launcher/</c>,
/// unless a link stands on the way to them (<see cref="LauncherLink"/>), which leaves the launcher out and Settings says why.</item>
/// <item>A request names a manifest entry, looked up as it is: never a path on disk, so no traversal reaches anything else.</item>
/// <item>The two stock executables in it must have the pinned SHA-256s (<see cref="RetailClients"/>), the folders must hold no link and
/// must not hold Amberline's own data folder; otherwise Settings says why and nothing is served.</item>
/// <item>Each address gets <see cref="PerAddress"/> transfers at a time and all players together <see cref="InAll"/>
/// (<see cref="Program.RequestPartition"/>, <see cref="Program.FilesPartition"/>), so the tunnels of a running game keep their share
/// of the port.</item>
/// <item>NOT RETAIL (batch 922; the owner, 20 November 2026: "make restarts reuse the last file check"): a build that shares keeps what it found in
/// <see cref="HostPaths.ShareRecord"/>: each file's manifest path, path on disk, size, last write time and SHA-256, and the folders, pins and protocol it
/// was made for. A start with the switch on runs every check again and lists the files again, but takes a file's SHA-256 from that record while its path,
/// size and last write time are unchanged; turning the switch on or Save hashes every file and replaces the record (see <see cref="Build"/>).</item>
/// </list>
/// </summary>
public sealed class HostFileShare(HostLoop loop, string game, string air, int remotePort, bool on, string? launcher)
{
    public const string Prefix = "/files";
    public const int PerAddress = 4, InAll = 16;
    /// <summary>The owner's notice beside the switch, in one sentence.</summary>
    public const string Notice = "You share your own copy of the game. Make sure you are allowed to share it. Amberline never includes these files.";
    /// <summary>Where each folder goes in the player's install (<see cref="FolderMove.GameRelease"/>, <c>air</c>).</summary>
    public const string GameRoot = "game/projects/lol_game_client/releases/0.0.0.51", AirRoot = "air";
    /// <summary>NOT RETAIL (batch 761, task 8): where the host's 2011 launcher goes in the player's install (its own <c>launcher</c> folder beside <c>air</c> and <c>game</c>).</summary>
    internal const string LauncherRoot = "launcher";

    public sealed record Entry(string Path, long Size, string Sha256);

    /// <summary>DIAGNOSTICS (batch 922): each file a build lists, by its path on disk, and whether the build read it to hash it; the checks count them.</summary>
    public static event Action<string, bool>? Checked;

    /// <summary>
    /// What a build found: the files by their manifest path, the manifest's bytes and the folders' sizes, or why nothing is shared. A
    /// <c>LauncherNote</c> names why the 2011 launcher is left out while the rest is shared (NOT RETAIL, batch 761, task 8).
    /// </summary>
    internal sealed record Shared(IReadOnlyDictionary<string, (string Source, Entry Entry, string? Id)> Files, byte[] Manifest, long GameBytes, long AirBytes,
        long LauncherBytes, string? Problem, string? LauncherNote = null, bool Open = false);

    /// <summary>NOT RETAIL (batch 922): how far a build got, for Settings while it runs: whether it reuses the last check, and whether it has had to hash a file.</summary>
    internal sealed class Progress(bool reusing)
    {
        public volatile bool Reusing = reusing, Hashing;
    }

    private sealed record Building(Task<Shared> Result, Progress Progress);

    private volatile bool _on = on;
    // A start reuses the last check (Build); Set, which Settings' switch and Save call, checks every file.
    private volatile Building? _built = on ? Start(loop, game, air, launcher, full: false) : null;

    public bool On => _on;

    /// <summary>A request the friends' port serves from the share: on that port, over HTTPS, a GET of <see cref="Prefix"/> or below, while the switch is on.</summary>
    public bool Serves(HttpContext context) => _on && context.Connection.LocalPort == remotePort && context.Request.IsHttps &&
        HttpMethods.IsGet(context.Request.Method) && context.Request.Path.StartsWithSegments(Prefix);

    /// <summary>Settings' switch: saved in platform.json and served at once; turning it on (or Save while it is on) checks every file again.</summary>
    internal void Set(bool value)
    {
        HostMode.Change(loop.Paths.Settings, saved => saved with { ShareFiles = value });
        if (value) _built = Start(loop, game, air, launcher, full: true);
        _on = value;
    }

    public void Map(WebApplication app)
    {
        app.MapGet(Prefix, (HttpContext context) => ServeAsync(context));
        app.MapGet(Prefix + "/{**path}", (HttpContext context) => ServeAsync(context));
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (!Serves(context) || _built is not { } built) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        var shared = await built.Result.WaitAsync(context.RequestAborted);
        var path = context.Request.Path.Value!;
        // The rest of the address after /files/ is compared with the manifest's paths as it is; a file changed since the manifest is not served.
        var entry = path.Length > Prefix.Length ? path[(Prefix.Length + 1)..] : null;
        if (shared.Problem is not null) context.Response.StatusCode = StatusCodes.Status404NotFound;
        else if (entry is null) await Results.Bytes(shared.Manifest, "application/json").ExecuteAsync(context);
        else if (shared.Files.TryGetValue(entry, out var file) && Open(file.Source, file.Entry, file.Id) is { } stream)
            await Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
        else context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    /// <summary>Settings' warning while files outside a locked install are shared and other Windows accounts may change them (the orchestrator, 17 November 2026).</summary>
    public const string OpenWarning = "Other Windows accounts on this PC can change these files.";

    /// <summary>
    /// NOT RETAIL (batch 857 review, HIGH 2, narrowed by the orchestrator on 17 November 2026): a listed file, served from a handle opened so nobody
    /// may write it while it is sent, that leads to its listed path itself (<see cref="InstallLock.FinalPath"/>) and has the list's size. A file of
    /// the locked install or of the 2011 launcher folder beside it (it has an <paramref name="id"/>) must also be this user's alone
    /// (<see cref="InstallLock.Private"/>), have one name and be the same file the list was built from (<see cref="InstallLock.Identity"/>); any
    /// other file (a game or AIR folder used where it is) is served as it is: the player's side hashes every file against the list before it is put in
    /// place (PlayerDownload.Place; the orchestrator, 17 November 2026: no whole-file hash per request here).
    /// Else null (404; the log says which file).
    /// </summary>
    private static FileStream? Open(string path, Entry entry, string? id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            if (stream.Length == entry.Size && string.Equals(InstallLock.FinalPath(stream.SafeFileHandle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
                (id is null || InstallLock.Identity(stream.SafeFileHandle) == id && InstallLock.Links(stream.SafeFileHandle) == 1 && InstallLock.Private(stream.GetAccessControl())))
                return stream;
            Console.Error.WriteLine($"File share: {path} is not served: it changed, it is reached through a link, or other Windows accounts can change it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        stream.Dispose();
        return null;
    }

    /// <summary>
    /// Settings' lines under the switch: what is shared and its size, that it checks the files, or why it cannot be shared. Batch 922: a start that reuses the
    /// last check says minutes only once it has to hash a file; a sharing host without a 2011 launcher folder is told how to share the launcher too.
    /// </summary>
    internal string Status()
    {
        if (!_on || _built is not { } built) return "";
        if (!built.Result.IsCompleted)
            return !built.Progress.Reusing ? "<p>Amberline is checking the game files before sharing them. This takes a few minutes. Open this page again to see when it is done.</p>"
                : built.Progress.Hashing ? "<p>Amberline is checking the files that changed since its last check. This can take a few minutes. Open this page again to see when it is done.</p>"
                : "<p>Amberline is comparing the files with its last check. This takes less than a minute. Open this page again to see when it is done.</p>";
        var shared = built.Result.Result;
        if (shared.Problem is { } problem)
            return $"<p class=problem role=alert>Your copy cannot be shared: {WebUtility.HtmlEncode(problem)} Nothing is shared.</p>";
        return $"<p>Shared: {SharedParts(shared)}. Players who have no copy of their own download these files during setup, using your internet upload.</p>" +
            (shared.LauncherNote is { } note ? $"<p class=warn>The 2011 launcher is not shared. {WebUtility.HtmlEncode(note)}</p>" : "") +
            (launcher is null ? "<p>No 2011 launcher folder is chosen, so the 2011 launcher is not shared. To share it too, choose Change the folders under Your game folders, then its folder under 2011 launcher.</p>" : "") +
            (shared.Open ? $"<p class=warn>{OpenWarning}</p>" : "");
    }

    /// <summary>What Settings names as shared, with each part's size; the 2011 launcher is named only when its files are listed.</summary>
    private static string SharedParts(Shared shared) => shared.LauncherBytes > 0
        ? $"the game folder ({HostUninstall.Size(shared.GameBytes)}), the AIR client folder ({HostUninstall.Size(shared.AirBytes)}) and the 2011 launcher ({HostUninstall.Size(shared.LauncherBytes)})"
        : $"the game folder ({HostUninstall.Size(shared.GameBytes)}) and the AIR client folder ({HostUninstall.Size(shared.AirBytes)})";

    // Batch 857 (the orchestrator, 17 November 2026): only the files of the locked install and of the 2011 launcher folder beside it must be private.
    private static Building Start(HostLoop loop, string game, string air, string? launcher, bool full)
    {
        var progress = new Progress(reusing: !full);
        return new(Task.Run(() => Build(game, air, loop.Paths.Data, (loop.Folders.GameSha256, loop.Folders.AirSha256, loop.Folders.LauncherSha256),
            HostMode.UsableLauncher(launcher, loop.Paths),
            HostPaths.InstallFolder(loop.Paths.App) is { } install ? [install, ClientPlan.LauncherBeside(install)] : [], loop.Paths.ShareRecord, full, progress)), progress);
    }

    /// <summary>
    /// The client's own run-time files, which hold the host's data, never the game's: its logs (<c>*.log</c>, the release folder's
    /// <c>*_r3dlog.txt</c>, <c>*_netlog.txt</c> and <c>Localization_Errors.txt</c>), crash dumps and <c>lastCmd.log</c>.
    /// NOT RETAIL: the 2011 launcher's language file (<c>DATA/CFG/defaults/locale.cfg</c>) is left out too: it holds the host's own language,
    /// and a player's usual start puts English back in their copy (design 2026-11-12 §6).
    /// </summary>
    internal static bool Private(string relative, bool release) =>
        relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
        release && !relative.Contains('/') && relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
        release && string.Equals(relative.Replace('\\', '/'), "DATA/CFG/defaults/locale.cfg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NOT RETAIL (batch 761, task 8, review round 1): the first link (a junction or a symbolic link) on the way from the 2011 launcher's folder to its
    /// files: the folder itself, a folder between it and one of the release's files (a junction in img would serve img\ad0.png from wherever it points),
    /// or the folder of the file list beside it. Null when the way holds none. A link that is a file itself is refused where the files are walked.
    /// </summary>
    internal static string? LauncherLink(string launcher, string? fileList)
    {
        static string? Linked(string folder) =>
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ? $"{folder} is a link, which is never shared." : null;
        try
        {
            if (Linked(launcher) is { } own) return own;
            foreach (var file in ClassicLauncherFiles.Required)
            {
                var folder = launcher;
                foreach (var part in file.Split('\\')[..^1])
                {
                    folder = Path.Combine(folder, part);
                    if (Linked(folder) is { } inside) return inside;
                }
            }
            return fileList is null ? null : Linked(Path.GetDirectoryName(fileList)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"a folder of the 2011 launcher cannot be read ({exception.Message}).";
        }
    }

    /// <summary>
    /// The manifest of the two folders, or why they cannot be shared. Reads every file once to hash it, unless <paramref name="full"/> is false and
    /// <paramref name="record"/> holds the last check made for these folders, pins and protocol.
    /// <para>NOT RETAIL (batch 922): a file's SHA-256 is then taken from the record while its manifest path, its path on disk, its size and its last write
    /// time (both read from the open file) are what the record says; every other check runs as on a full check, and the three executables are checked
    /// against their pins on their bytes whatever the record says. The reuse trusts size and last write time: a file rewritten with the same size and the
    /// same last write time keeps its old SHA-256 until Settings' Save (a full check). The player's side hashes every downloaded file against the list, so
    /// such a file fails there and is never installed; TROUBLESHOOTING's answer is Save. A full check removes the record when it starts, so a Save that is
    /// refused or cut short leaves none; a reusing start keeps it (a start cut short leaves it for the next), and every build that shares replaces it.</para>
    /// </summary>
    internal static Shared Build(string game, string air, string data, (string Game, string Air, string Launcher) pins, string? launcher,
        IReadOnlyList<string> locked, string record, bool full = true, Progress? progress = null)
    {
        var last = full ? null : Last(record, game, air, launcher, pins);
        if (progress is not null) progress.Reusing = last is not null;
        if (full) Forget(record);
        static Shared Refused(string problem) => new(new Dictionary<string, (string, Entry, string?)>(), [], 0, 0, 0, problem);
        if (RetailClients.GameProblem(game, pins.Game) is { } gameProblem) return Refused($"the game folder: {gameProblem}.");
        if (RetailClients.AirProblem(air, pins.Air) is { } airProblem) return Refused($"the AIR client folder: {airProblem}.");
        var roots = new List<(string Source, string Path, bool Release)> { (game, GameRoot, true) };
        if (FolderMove.LanguageProject(game) is { } language) roots.Add((language, "game/projects/" + Path.GetFileName(language), false));
        // The AIR client's own entries only; its logs hold the host's own logins.
        foreach (var entry in FolderMove.AirEntries.Where(entry => entry != "logs"))
            if (File.Exists(Path.Combine(air, entry)) || Directory.Exists(Path.Combine(air, entry))) roots.Add((Path.Combine(air, entry), AirRoot + "/" + entry, false));
        // NOT RETAIL (batch 761, task 8): the host's 2011 launcher, when its folder holds the accepted files (ClassicLauncherFiles): each file of its release
        // and its file list (beside it or in the folder), under launcher/. Nothing else of the folder is listed. A launcher that fails its check, or has a link
        // on the way to its files (LauncherLink), is left out while the game and the AIR client are still shared; Settings says why (LauncherNote).
        var fileList = launcher is null ? null : ClassicLauncherFiles.ManifestFor(launcher);
        var launcherSkipped = launcher is null ? null : (ClassicLauncherFiles.Problem(launcher, pins.Launcher) ?? LauncherLink(launcher, fileList));
        var launcherFolder = launcher is not null && launcherSkipped is null ? launcher : null;
        if (launcherFolder is not null)
        {
            roots.AddRange(ClassicLauncherFiles.Required.Select(file => (Path.Combine(launcherFolder, file), LauncherRoot + "/" + file.Replace('\\', '/'), false)));
            if (fileList is not null) roots.Add((fileList, LauncherRoot + "/releasemanifest", false));
        }
        if (roots.FirstOrDefault(root => FolderMove.Overlaps(root.Source, data)) is { Source: not null } holds)
            return Refused($"{holds.Source} holds Amberline's own data folder.");
        if (launcherFolder is not null && FolderMove.Overlaps(launcherFolder, data))
            return Refused($"{launcherFolder} holds Amberline's own data folder.");
        var files = new Dictionary<string, (string Source, Entry Entry, string? Id)>(StringComparer.Ordinal);
        var remembered = new List<Remembered>();
        var open = false;
        long gameBytes = 0, airBytes = 0, launcherBytes = 0;
        try
        {
            foreach (var (source, prefix, release) in roots)
            {
                IEnumerable<FileSystemInfo> items = File.Exists(source) ? [new FileInfo(source)]
                    : new DirectoryInfo(source).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false });
                foreach (var item in items)
                {
                    // A link could lead out of the folder, to anything on the host's PC.
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return Refused($"{item.FullName} is a link, which is never shared.");
                    if (item is not FileInfo file) continue;
                    var relative = File.Exists(source) ? "" : Path.GetRelativePath(source, file.FullName).Replace('\\', '/');
                    if (Private(relative, release)) continue;
                    var path = relative.Length == 0 ? prefix : prefix + "/" + relative;
                    using (var stream = file.OpenRead())
                    {
                        if (!OperatingSystem.IsWindows()) return Refused("only Windows shares files.");
                        // NOT RETAIL (batch 857 review, HIGH 2; narrowed by the orchestrator, 17 November 2026): a file of the locked install or of the
                        // 2011 launcher folder beside it is shared only while it is this user's alone, has one name and is reached by its own path; it is
                        // then served by its identity. A game or AIR folder used where it is stays shared, served while its bytes match the list, and
                        // Settings warns while other Windows accounts can change it.
                        string? id = null;
                        if (locked.Any(folder => FolderMove.Inside(file.FullName, folder)))
                        {
                            if (!InstallLock.Private(stream.GetAccessControl()))
                                return Refused($"other Windows accounts on this PC can change {file.FullName}, a file of your Amberline install.");
                            if (InstallLock.Links(stream.SafeFileHandle) != 1 ||
                                !string.Equals(InstallLock.FinalPath(stream.SafeFileHandle), file.FullName, StringComparison.OrdinalIgnoreCase))
                                return Refused($"{file.FullName} is reached through a link or has a second name (a hard link), which is never shared.");
                            id = InstallLock.Identity(stream.SafeFileHandle);
                        }
                        else
                        {
                            // Batch 857 review (F): served only by its own path, so Settings says why a folder reached another way is not shared.
                            if (!string.Equals(InstallLock.FinalPath(stream.SafeFileHandle), file.FullName, StringComparison.OrdinalIgnoreCase))
                                return Refused($"{file.FullName} is reached another way (a junction, a subst or mapped drive, or a short 8.3 name), so Amberline does not share it. Choose the folder by its own full path.");
                            open |= !InstallLock.Private(stream.GetAccessControl());
                        }
                        // NOT RETAIL (batch 922): the last check's SHA-256 while the record's path, size and last write time still hold (see Build).
                        var size = stream.Length;
                        var written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                        var sha = last?.GetValueOrDefault(path) is { } kept && kept.Size == size && kept.Written == written &&
                            string.Equals(kept.Source, file.FullName, StringComparison.OrdinalIgnoreCase) ? kept.Sha256 : null;
                        if (sha is null && progress is not null) progress.Hashing = true;
                        Checked?.Invoke(file.FullName, sha is null);
                        sha ??= Convert.ToHexString(SHA256.HashData(stream));
                        files[path] = (file.FullName, new(path, size, sha), id);
                        remembered.Add(new(path, file.FullName, size, written, sha));
                        if (prefix.StartsWith(LauncherRoot + "/", StringComparison.Ordinal)) launcherBytes += size;
                        else if (prefix.StartsWith(AirRoot + "/", StringComparison.Ordinal)) airBytes += size;
                        else gameBytes += size;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Refused($"a file cannot be read ({exception.Message})."); }
        // The pins again, on the bytes just read: a file replaced since the first check is not shared either.
        // The launcher's pin is written in lower case (ClassicLauncherFiles), the hashes here in upper case: compared without case, as its check does.
        var launcherChanged = launcherFolder is not null &&
            !string.Equals(files.GetValueOrDefault(LauncherRoot + "/LoLLauncher.exe").Entry?.Sha256, pins.Launcher, StringComparison.OrdinalIgnoreCase);
        if (files.GetValueOrDefault(GameRoot + "/" + HostFolders.GameExecutable).Entry?.Sha256 != pins.Game ||
            files.GetValueOrDefault(AirRoot + "/LolClient.exe").Entry?.Sha256 != pins.Air || launcherChanged)
            return Refused("an executable changed while Amberline checked it.");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { protocol = Protocol, bytes = gameBytes + airBytes + launcherBytes,
            files = files.Values.Select(file => file.Entry).OrderBy(entry => entry.Path, StringComparer.Ordinal) }, Json);
        Remember(record, new(Protocol, game, air, launcher, pins.Game, pins.Air, pins.Launcher, [.. remembered]));
        return new(files, manifest, gameBytes, airBytes, launcherBytes, null, launcherSkipped, open);
    }

    private const int Protocol = 1;

    /// <summary>NOT RETAIL (batch 922): the last check that shared (<see cref="HostPaths.ShareRecord"/>): what it was made for and each file it listed.</summary>
    private sealed record Record(int Protocol, string? Game, string? Air, string? Launcher, string? GamePin, string? AirPin, string? LauncherPin, Remembered[]? Files);

    private sealed record Remembered(string? Path, string? Source, long Size, DateTime Written, string? Sha256);

    /// <summary>The last check's files by their manifest path, when its record was made for these folders, pins and protocol; else null (a full check).</summary>
    private static Dictionary<string, Remembered>? Last(string record, string game, string air, string? launcher, (string Game, string Air, string Launcher) pins)
    {
        static bool Same(string? saved, string? now) => string.Equals(saved, now, StringComparison.OrdinalIgnoreCase);
        Record? last;
        try { last = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(record), Json); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
        if (last is not { Protocol: Protocol, Files: { } saved } || !Same(last.Game, game) || !Same(last.Air, air) || !Same(last.Launcher, launcher) ||
            (last.GamePin, last.AirPin, last.LauncherPin) != pins)
            return null;
        var kept = new Dictionary<string, Remembered>(StringComparer.Ordinal);
        foreach (var file in saved)
            if (file is not { Path: { } path, Source: not null, Sha256: { Length: 64 } sha } || !sha.All(char.IsAsciiHexDigitUpper) || !kept.TryAdd(path, file))
                return null;
        return kept;
    }

    private static void Remember(string record, Record found)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllBytes(record + ".tmp", JsonSerializer.SerializeToUtf8Bytes(found, Json));
            File.Move(record + ".tmp", record, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"File share: the record of this check could not be saved, so the next start checks every file again ({exception.Message}).");
        }
    }

    private static void Forget(string record)
    {
        try { if (File.Exists(record)) File.Delete(record); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"File share: the record of the last check could not be removed ({exception.Message}).");
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
