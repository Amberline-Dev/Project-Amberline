using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Linked as source into the Platform as well (see RetailClients.cs): the host's one-folder install moves the clients with it.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>A refusal of the installer: one plain sentence for the player. Nothing was changed.</summary>
internal sealed class InstallRefusedException(string message) : Exception(message);

/// <summary>
/// One step of a move for a window: what is happening, the share done (null while a step has no measure, such as a rename), and
/// whether the user may still cancel (only while copying and checking, which leave the originals untouched).
/// </summary>
internal sealed record InstallProgress(string Text, int? Percent = null, bool CanCancel = false);

/// <summary>
/// NOT RETAIL: moving the stock clients into an install folder. Retail's patcher installed them. Written for the player's installer
/// (batch 619) and shared with the host's one-folder install (batch 637). On one drive a move is a rename; on another drive, or when
/// the originals are kept, each folder is copied next to its target and every file is compared with its original (size and
/// SHA-256) before anything else changes. A move takes a whole folder, or only the listed entries of a folder that holds other
/// things as well, so nothing that is not the client's is ever moved or removed.
/// </summary>
internal static class FolderMove
{
    /// <summary>Where the game goes: a RADS-shaped path, so the managed layout (GameManagedLayout) is built on any drive.</summary>
    internal const string GameRelease = @"game\projects\lol_game_client\releases\0.0.0.51";

    /// <summary>The file at the root of a host's one-folder install; the installed program finds its data folder beside it.</summary>
    internal const string InstallMarker = "amberline-install.json";

    /// <summary>
    /// The top-level files and folders of the stock 1.0.0.126 AIR client (the reference lol_air_client, its configured properties
    /// and AirOverlay's <c>.stock</c> copies of them). An AIR folder that also holds other things gives up only these.
    /// </summary>
    internal static readonly string[] AirEntries =
    [
        "Adobe AIR", "META-INF", "assets", "css", "logs", "LolClient.exe", "LolClient.swf", "mimetype", "mod_lgn.dat", "mod_man.dat",
        "mod_ser.dat", "version.txt", "lol.properties", "locale.properties", "lol.properties.stock", "locale.properties.stock",
    ];

    /// <summary>A folder to move: the whole <paramref name="Source"/>, or only its <paramref name="Entries"/> (names directly inside it).</summary>
    internal sealed record Move(string Label, string Source, string Target, IReadOnlyList<string>? Entries = null);

    /// <summary>DIAGNOSTICS: test seams of the move. SameDrive replaces the drive test; AfterCopy runs on each copied file.</summary>
    internal sealed record MoveOptions(Func<string, string, bool>? SameDrive = null, Action<string>? AfterCopy = null);

    /// <summary>The per-user pointer to a host's one-folder install: <c>%LOCALAPPDATA%\Amberline\install.json</c> naming its folder.</summary>
    internal static string PointerPath(string localAppData) => Path.Combine(localAppData, "Amberline", "install.json");

    /// <summary>
    /// The player tool's per-user folder (batch 647): Play.cmd's download, the install record, the logs, the play mode's stage. A name
    /// of its own, so it never lies inside the host's <c>%LOCALAPPDATA%\Amberline</c> (pointer, data), also after the public rename.
    /// </summary>
    internal static string PlayerFolder(string localAppData) => Path.Combine(localAppData, "Amberline Player");

    /// <summary>
    /// The player tool's folder before batch 647. The public build's rename put it inside the host's folder, so it moved; it is read as
    /// a fallback (an older Play file still downloads into it) and emptied by the player's removal, which then removes its parent too once
    /// that is empty.
    /// </summary>
    internal static string OldPlayerFolder(string localAppData) => Path.Combine(localAppData, "Amberline", "player");

    /// <summary>The player install records a host on this PC reads: the current one, then the one from before batch 647.</summary>
    internal static string[] PlayerRecords(string localAppData) =>
        [Path.Combine(PlayerFolder(localAppData), "install.json"), Path.Combine(OldPlayerFolder(localAppData), "install.json")];

    /// <summary>The install folder the pointer names, when that folder still holds the install's marker; else null.</summary>
    internal static string? ReadPointer(string localAppData)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(PointerPath(localAppData)));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("folder", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) &&
                File.Exists(Path.Combine(folder, InstallMarker)) ? Full(folder) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    internal static void WritePointer(string localAppData, string folder)
    {
        var path = PointerPath(localAppData);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { folder = Full(folder) }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The moves, all or none. First every folder that is copied (another drive, or <paramref name="deleteOriginals"/> false) is copied
    /// next to its target and each file is compared with its original. Then every folder is put in place: on one drive with
    /// <paramref name="deleteOriginals"/> each part is renamed; a copied part's original is first renamed aside on its own drive, which
    /// fails while a program has a file in it open. Any failure renames everything back and removes the copies. Only when everything
    /// is in place are the originals set aside removed. Returns the leftovers that could not be removed. Batch 639: the progress counts
    /// files and bytes of the copy and of its check (one bar for both), and <paramref name="cancel"/> is honoured until the first
    /// original is touched; the renames that follow are quick and run to the end or roll back.
    /// </summary>
    internal static IReadOnlyList<string> Execute(IReadOnlyList<Move> moves, bool deleteOriginals = true, IProgress<InstallProgress>? progress = null,
        MoveOptions? options = null, CancellationToken cancel = default)
    {
        var sameDrive = options?.SameDrive ?? SameRoot;
        var copies = new Dictionary<Move, string>();
        var undo = new Stack<Action>();
        var asides = new List<string>();
        var copied = moves.Where(move => !deleteOriginals || !sameDrive(move.Source, move.Target)).ToList();
        var meter = new Meter(progress, 2 * copied.Sum(CopyBytes));
        try
        {
            foreach (var move in copied)
            {
                var partial = move.Target + ".partial";
                TryDeleteTree(partial);
                copies[move] = partial;
                var items = Items(move, partial);
                var files = items.SelectMany(item => Files(item.Item, move.Label)).ToList();
                var count = new Count(files.Count, files.Sum(file => file.Length));
                foreach (var (item, target) in items) CopyItem(item, target, move.Label, count, meter, options?.AfterCopy, cancel);
                count = new Count(files.Count, count.Bytes);
                foreach (var (item, target) in items) Verify(item, target, move.Label, count, meter, cancel);
            }
            cancel.ThrowIfCancellationRequested();
            foreach (var move in moves)
            {
                progress?.Report(new($"Putting the {move.Label} in place…"));
                Directory.CreateDirectory(Path.GetDirectoryName(move.Target)!);
                if (!copies.TryGetValue(move, out var partial))
                {
                    if (move.Entries is not null)
                    {
                        Directory.CreateDirectory(move.Target);
                        undo.Push(() => Directory.Delete(move.Target));
                    }
                    foreach (var (item, target) in Items(move, move.Target))
                    {
                        Rename(item, target, move.Label);
                        undo.Push(() => MoveItem(target, item));
                    }
                    continue;
                }
                if (deleteOriginals)
                    foreach (var (item, _) in Items(move, partial))
                    {
                        var aside = Unused(item + ".amberline-moved");
                        Rename(item, aside, move.Label);
                        undo.Push(() => MoveItem(aside, item));
                        asides.Add(aside);
                    }
                Directory.Move(partial, move.Target);
                undo.Push(() => Directory.Move(move.Target, partial));
            }
        }
        catch
        {
            while (undo.Count > 0)
            {
                try { undo.Pop()(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            foreach (var partial in copies.Values) TryDeleteTree(partial);
            throw;
        }
        var leftovers = new List<string>();
        foreach (var aside in asides)
        {
            progress?.Report(new("Removing the originals, which are now in the install folder…"));
            if (!TryDeleteItem(aside)) leftovers.Add(aside);
        }
        return leftovers;
    }

    /// <summary>The same moves for a console (the host's install): a line per step, and the copy's share in steps of 10%.</summary>
    internal static IReadOnlyList<string> Execute(IReadOnlyList<Move> moves, bool deleteOriginals, IProgress<string>? progress, MoveOptions? options = null) =>
        Execute(moves, deleteOriginals, progress is null ? null : new Lines(progress), options);

    private sealed class Lines(IProgress<string> lines) : IProgress<InstallProgress>
    {
        private int next;

        public void Report(InstallProgress value)
        {
            if (value.Percent is { } percent)
            {
                if (percent < next) return;
                next = percent / 10 * 10 + 10;
            }
            lines.Report(value.Text);
        }
    }

    /// <summary>The bar of the copies: bytes done of all bytes to copy and check, reported at most ten times a second and at each part's end.</summary>
    private sealed class Meter(IProgress<InstallProgress>? progress, long total)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long done;
        private TimeSpan reported = TimeSpan.FromDays(-1);

        public void Add(long bytes, string text, bool final)
        {
            done += bytes;
            if (progress is null || (!final && clock.Elapsed - reported < TimeSpan.FromMilliseconds(100))) return;
            reported = clock.Elapsed;
            progress.Report(new(text, total <= 0 ? 100 : (int)Math.Min(100, done * 100 / total), CanCancel: true));
        }
    }

    /// <summary>The bytes a move copies when it is not a rename.</summary>
    internal static long CopyBytes(Move move) => Items(move, move.Target).Sum(item => TreeBytes(item.Item, move.Label));

    /// <summary>What a move takes: the folder itself, or each listed entry that exists, with its place under <paramref name="root"/>.</summary>
    private static List<(string Item, string Target)> Items(Move move, string root) => move.Entries is null
        ? [(move.Source, root)]
        : move.Entries.Select(entry => (Item: Path.Combine(move.Source, entry), Target: Path.Combine(root, entry)))
            .Where(pair => File.Exists(pair.Item) || Directory.Exists(pair.Item)).ToList();

    /// <summary>The names left in a move's source that are not the client's (an AIR folder that also held other things).</summary>
    internal static IReadOnlyList<string> Others(Move move) => move.Entries is null || !Directory.Exists(move.Source) ? []
        : Directory.EnumerateFileSystemEntries(move.Source).Select(path => Path.GetFileName(path))
            .Where(name => !move.Entries.Contains(name, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The safety rules of a folder the installer would move. Each broken rule is a refusal with its reason.</summary>
    internal static void CheckMovable(string source, string install, string label, IReadOnlyList<string> running)
    {
        if (IsDriveRoot(source)) throw new InstallRefusedException($"A whole drive cannot be moved. Choose the {label}'s own folder.");
        if (HostInstallation(source) is not null)
            throw new InstallRefusedException($"This {label} belongs to an Amberline server installation, so it cannot be moved. Choose your own copy of the {label}.");
        if (Inside(install, source))
            throw new InstallRefusedException($"The install folder is inside the {label} folder. Choose an install folder somewhere else.");
        if (running.FirstOrDefault(image => Inside(image, source)) is { } program)
            throw new InstallRefusedException($"{Path.GetFileName(program)} is running from the {label} folder. Close it, then try again.");
    }

    /// <summary>The safety rules of every part a move takes.</summary>
    internal static void CheckMovable(Move move, string install, IReadOnlyList<string> running)
    {
        foreach (var (item, _) in Items(move, move.Target)) CheckMovable(item, install, move.Label, running);
    }

    /// <summary>
    /// The Amberline host installation that holds <paramref name="folder"/>: an ancestor (or the folder) with platform-data, with
    /// server\Amberline.Server.exe, or with the marker of a one-folder install.
    /// </summary>
    internal static string? HostInstallation(string folder)
    {
        for (var directory = new DirectoryInfo(Full(folder)); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "platform-data")) || File.Exists(Path.Combine(directory.FullName, "server", "Amberline.Server.exe")) ||
                File.Exists(Path.Combine(directory.FullName, InstallMarker)))
                return directory.FullName;
        return null;
    }

    /// <summary>The executables of the running processes this user can inspect (a protected process is skipped), but <paramref name="except"/>'s.</summary>
    internal static IReadOnlyList<string> RunningImages(int except = 0)
    {
        var images = new List<string>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try { if (process.Id != except && process.MainModule?.FileName is { } path) images.Add(path); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        return images;
    }

    /// <summary>The game's language project (voice-over and UI text) when it sits beside the game as in the stock layout.</summary>
    internal static string? LanguageProject(string game)
    {
        var releases = Directory.GetParent(game);
        var project = releases?.Parent;
        var projects = project?.Parent;
        if (releases is null || project is null || projects is null || !releases.Name.Equals("releases", StringComparison.OrdinalIgnoreCase) ||
            !project.Name.Equals("lol_game_client", StringComparison.OrdinalIgnoreCase)) return null;
        var locale = GameLanguagePack.ResolveLocale(game);
        if (!Regex.IsMatch(locale, "^[A-Za-z]{2}_[A-Za-z]{2}$", RegexOptions.CultureInvariant)) return null;
        var language = Path.Combine(projects.FullName, "lol_game_client_" + locale.ToLowerInvariant());
        return Directory.Exists(language) ? language : null;
    }

    internal static void NotThere(string target, string label)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new InstallRefusedException($"The install folder already has a folder for the {label} ({target}). Choose another install folder.");
    }

    private static void Rename(string source, string target, string label)
    {
        try { MoveItem(source, target); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallRefusedException($"The {label} folder could not be moved: a program may have a file in it open. Close it, then try again. ({exception.Message})");
        }
    }

    private static void MoveItem(string source, string target)
    {
        if (File.Exists(source)) File.Move(source, target);
        else Directory.Move(source, target);
    }

    /// <summary>The files of a folder, recursively, or the file itself. A link inside is refused: a copy would follow it out of the folder.</summary>
    private static IEnumerable<FileInfo> Files(string root, string label)
    {
        IEnumerable<FileSystemInfo> entries = File.Exists(root) ? [new FileInfo(root)]
            : new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false });
        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InstallRefusedException($"The {label} folder holds a link ({entry.FullName}), which cannot be copied safely.");
            if (entry is FileInfo file) yield return file;
        }
    }

    /// <summary>The files and bytes of one move's copy (or of its check), and how many are done.</summary>
    private sealed class Count(int files, long bytes)
    {
        public int Files { get; } = files;
        public long Bytes { get; } = bytes;
        public int DoneFiles;
        public long DoneBytes;
    }

    private static string Number(int files) => files.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static void CopyItem(string source, string destination, string label, Count count, Meter? meter, Action<string>? afterCopy,
        CancellationToken cancel = default)
    {
        var single = File.Exists(source);
        if (!single)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        else Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        foreach (var file in Files(source, label))
        {
            cancel.ThrowIfCancellationRequested();
            var target = single ? destination : Path.Combine(destination, Path.GetRelativePath(source, file.FullName));
            File.Copy(file.FullName, target);
            afterCopy?.Invoke(target);
            count.DoneFiles++;
            count.DoneBytes += file.Length;
            meter?.Add(file.Length, $"Copying the {label}: {Number(count.DoneFiles)} of {Number(count.Files)} files ({Gigabytes(count.DoneBytes)} of {Gigabytes(count.Bytes)} GB)",
                count.DoneFiles == count.Files);
        }
    }

    private static void Verify(string source, string copy, string label, Count count, Meter meter, CancellationToken cancel)
    {
        var single = File.Exists(source);
        var originals = Files(source, label).ToList();
        if (!single && Files(copy, label).Count() != originals.Count) throw new IOException($"The copy of the {label} has another number of files than the original.");
        foreach (var original in originals)
        {
            cancel.ThrowIfCancellationRequested();
            var relative = single ? Path.GetFileName(source) : Path.GetRelativePath(source, original.FullName);
            var copied = new FileInfo(single ? copy : Path.Combine(copy, relative));
            if (!copied.Exists || copied.Length != original.Length || !Hash(original.FullName).AsSpan().SequenceEqual(Hash(copied.FullName)))
                throw new IOException($"The copy of the {label} differs from the original at {relative}.");
            count.DoneFiles++;
            meter.Add(original.Length, $"Checking the copied {label}: {Number(count.DoneFiles)} of {Number(count.Files)} files", count.DoneFiles == count.Files);
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    internal static long TreeBytes(string root, string label) => Files(root, label).Sum(file => file.Length);

    /// <summary>A folder copied file by file (no progress, no seam).</summary>
    internal static void CopyTree(string source, string destination, string label) => CopyItem(source, destination, label, new Count(0, 0), null, null);

    internal static string Unused(string path)
    {
        var candidate = path;
        for (var number = 2; Directory.Exists(candidate) || File.Exists(candidate); number++) candidate = $"{path}-{number}";
        return candidate;
    }

    internal static bool TryDeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return true;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TryDeleteItem(string path)
    {
        if (!File.Exists(path)) return TryDeleteTree(path);
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string Gigabytes(long bytes) => (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));

    internal static bool IsDriveRoot(string folder) => string.Equals(Path.GetPathRoot(Full(folder)), Full(folder), StringComparison.OrdinalIgnoreCase);

    internal static bool SameRoot(string first, string second) => string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase);

    internal static bool Same(string first, string second) => string.Equals(Full(first), Full(second), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    internal static bool Inside(string path, string folder)
    {
        var child = Full(path);
        var parent = Full(folder);
        return string.Equals(child, parent, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(parent.EndsWith('\\') ? parent : parent + '\\', StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Overlaps(string first, string second) => Inside(first, second) || Inside(second, first);
}
