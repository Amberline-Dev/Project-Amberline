using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

// Linked as source into the Platform as well (see RetailClients.cs): the host's setup page and its --client-zip unpack with it.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// NOT RETAIL (batch 765): the client zip as the source of the clients. Retail installed the game and the AIR client with Riot's patcher; Amberline
/// accepts a preserved copy of patch 1.0.0.126 as one zip, the client bundle (<see cref="BundleName"/>), as well as the picked folders. The folders
/// are found in the zip by what they hold, never by their names. Only what an install uses is unpacked, into a staging folder beside the install
/// folder (<see cref="StagingFor"/>), and the install then takes the unpacked folders as it takes picked ones: the same checks, the same moves. The
/// zip itself is only read, never changed or deleted.
/// </summary>
internal static class ClientZip
{
    /// <summary>The client bundle's file name, for the sentences that name it; the zip itself may have any name.</summary>
    internal const string BundleName = "patch-1.0.0.126-client-bundle.zip";

    /// <summary>The game's release folder below the game folder (the folder that holds <c>projects</c>): <see cref="FolderMove.GameRelease"/> below <c>game</c>.</summary>
    private const string Release = "projects/lol_game_client/releases/0.0.0.51/";

    /// <summary>
    /// The file in a staging folder that marks it as this code's: a leftover is deleted only when it holds this file (or nothing). A host's 2011
    /// launcher folder made from the zip carries it too (ClientPlan.PlaceZipLauncher in the Platform), so only such a folder is ever replaced.
    /// </summary>
    internal const string Marker = "amberline-client-zip.txt";

    /// <summary>The unpacked folders: the game's release folder, the AIR client and the 2011 launcher (null when the zip has none, or none was asked for).</summary>
    internal sealed record Unpacked(string Game, string Air, string? Launcher);

    /// <summary>
    /// Where the clients lie in the zip, as entry-name prefixes ("" is the zip's root, anything else ends with '/'): the game folder (the one that holds
    /// <c>projects</c>), the AIR client's folder and the 2011 launcher's (null: none).
    /// </summary>
    internal sealed record Layout(string Game, string Air, string? Launcher);

    /// <summary>
    /// The staging folder of an install folder: beside it, on its drive. Not inside it: MakePlan (and the host's ClientPlan) runs a client folder that
    /// already lies inside the install from where it is, so a staging folder inside would stay the install's game for good. Beside it, the unpacked
    /// folders are outside folders like picked ones, and on the install's drive their moves are renames.
    /// </summary>
    internal static string StagingFor(string install) => FolderMove.Full(install) + ".client-zip";

    /// <summary>
    /// Unpacks into <paramref name="staging"/> what an install uses: the game's release folder, its one language project (the install's own rule,
    /// <see cref="FolderMove.LanguageFolder"/>, read from the unpacked game), the AIR client's own entries (<see cref="FolderMove.AirEntries"/>) without
    /// its <c>logs</c> folder, and with <paramref name="launcher"/> the 2011 launcher's own files (<see cref="ClassicLauncherFiles.Required"/> and its
    /// <c>releasemanifest</c>). The staging folder is laid out as the install is (<c>game\projects\...</c>, <c>air</c>, <c>launcher</c>). A leftover of an
    /// interrupted run is removed and redone first. Every refusal is an <see cref="InstallRefusedException"/> with one plain sentence; after a refusal,
    /// a failure or a cancel nothing of the staging folder is left. The caller removes it after the install (<see cref="Remove"/>).
    /// </summary>
    internal static Unpacked Unpack(string zip, string staging, bool launcher, IProgress<InstallProgress>? progress = null, CancellationToken cancel = default)
    {
        staging = FolderMove.Full(staging);
        using var archive = Open(zip);
        var layout = Locate(archive);
        var entries = archive.Entries.Select(entry => (Entry: entry, Name: Name(entry))).ToList();
        List<(ZipArchiveEntry Entry, string Target)> Under(string prefix, string target, Func<string, bool>? take = null) => entries
            .Where(item => item.Name.Length > prefix.Length && item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (take?.Invoke(item.Name[prefix.Length..]) ?? true))
            .Select(item => (item.Entry, target + item.Name[prefix.Length..])).ToList();
        var game = Under(layout.Game + Release, "game/" + Release);
        // Only the AIR client's own entries, as a picked AIR folder that holds other things gives up only those (batch 639): an AIR client at the zip's
        // root would otherwise take the whole zip with it. Its logs stay behind.
        var air = Under(layout.Air, "air/", rest => FolderMove.AirEntries.Any(own => !own.Equals("logs", StringComparison.OrdinalIgnoreCase) &&
            (rest.Equals(own, StringComparison.OrdinalIgnoreCase) || rest.StartsWith(own + "/", StringComparison.OrdinalIgnoreCase))));
        var launcherFiles = launcher && layout.Launcher is { } found ? Under(found, "launcher/", rest =>
            rest.Equals("releasemanifest", StringComparison.OrdinalIgnoreCase) ||
            ClassicLauncherFiles.Required.Any(file => rest.Equals(file.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))) : [];
        // The language project is known only once the game's own locale is unpacked, so the space it needs is the largest project beside the game.
        var largestLanguage = entries.Where(item => item.Name.StartsWith(layout.Game + "projects/", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Project: item.Name[(layout.Game.Length + "projects/".Length)..].Split('/')[0], item.Entry.Length))
            .Where(item => !item.Project.Equals("lol_game_client", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Project, StringComparer.OrdinalIgnoreCase).Select(project => project.Sum(item => item.Length)).DefaultIfEmpty(0).Max();
        Prepare(staging);
        try
        {
            var meter = new Meter(progress) { Total = Bytes(game) + Bytes(air) + Bytes(launcherFiles) + largestLanguage };
            // The space the unpacked files take. The install that follows renames them on this drive, so it needs none of its own (MakePlan and
            // ClientPlan still check theirs).
            var root = Path.GetPathRoot(staging)!;
            long free;
            try { free = new DriveInfo(root).AvailableFreeSpace; }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
            { throw new InstallRefusedException($"Drive {root.TrimEnd('\\')} is not available. Choose a folder on another drive."); }
            if (meter.Total > free)
                throw new InstallRefusedException($"Drive {root.TrimEnd('\\')} has {FolderMove.Gigabytes(free)} GB free; unpacking the client zip needs " +
                    $"{FolderMove.Gigabytes(meter.Total)} GB. Free some space, or choose a folder on another drive.");
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[1 << 20];
            void Extract(List<(ZipArchiveEntry Entry, string Target)> part)
            {
                foreach (var (entry, target) in part) ExtractEntry(entry, Target(staging, target), written, buffer, meter, cancel);
            }
            Extract(game);
            var stagedGame = Path.Combine(staging, FolderMove.GameRelease);
            var language = FolderMove.LanguageFolder(stagedGame) is { } name ? Under(layout.Game + "projects/" + name + "/", "game/projects/" + name + "/") : [];
            meter.Total += Bytes(language) - largestLanguage;
            Extract(language);
            Extract(air);
            Extract(launcherFiles);
            meter.Add(0, final: true);
            return new(stagedGame, Path.Combine(staging, "air"), launcherFiles.Count > 0 ? Path.Combine(staging, "launcher") : null);
        }
        // A damaged entry, or one .NET cannot read (an unknown compression method, encryption).
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            Remove(staging);
            throw new InstallRefusedException($"The client zip is damaged ({exception.Message}). Use an undamaged copy of it.");
        }
        catch
        {
            Remove(staging);
            throw;
        }
    }

    /// <summary>
    /// The game folder, the AIR client and the optional 2011 launcher in <paramref name="archive"/>, found by what they hold, in any case and under any
    /// top folder: the folder whose <c>projects\lol_game_client\releases\0.0.0.51</c> holds the game executable, the one that holds <c>LolClient.exe</c>
    /// and the one that holds <c>LoLLauncher.exe</c>. Every entry is checked first (<see cref="CheckSafe"/>). No game or AIR client, or any of the three
    /// more than once, is a refusal; no launcher is fine.
    /// </summary>
    internal static Layout Locate(ZipArchive archive)
    {
        foreach (var entry in archive.Entries) CheckSafe(entry);
        var names = archive.Entries.Select(Name).ToList();
        var game = One(Holders(names, Release + "League of Legends.exe"), "game client", required: true)!;
        var air = One(Holders(names, "LolClient.exe"), "AIR client", required: true)!;
        return new(game, air, One(Holders(names, "LoLLauncher.exe"), "2011 launcher", required: false));
    }

    /// <summary>Removes a staging folder of this code's (one with its marker, or an empty one); a folder of that name that is not ours is left alone. False when it stays.</summary>
    internal static bool Remove(string staging) => !Directory.Exists(staging) || (Ours(staging) && FolderMove.TryDeleteTree(staging));

    private static ZipArchive Open(string zip)
    {
        if (!File.Exists(zip)) throw new InstallRefusedException($"There is no client zip at {zip}. Choose the client zip ({BundleName}).");
        try { return ZipFile.OpenRead(zip); }
        catch (InvalidDataException) { throw new InstallRefusedException($"{Path.GetFileName(zip)} is not a zip file, or it is damaged. Choose the client zip ({BundleName})."); }
    }

    /// <summary>An entry's name with '/' between its parts (a zip made on Windows may use '\').</summary>
    private static string Name(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    /// <summary>
    /// Refuses an entry that could land outside the staging folder (an absolute name, a drive letter or an alternate stream, an empty, <c>.</c> or
    /// <c>..</c> part, a NUL) and one that is a link (a Unix symbolic link, or an entry with Windows' reparse-point attribute).
    /// </summary>
    private static void CheckSafe(ZipArchiveEntry entry)
    {
        var name = Name(entry);
        var link = ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
        if (link || name.Length == 0 || name.StartsWith('/') || name.Contains('\0') || name.Contains(':') ||
            name.TrimEnd('/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InstallRefusedException($"The zip holds an unsafe entry ({entry.FullName}), so nothing is unpacked from it. Choose the client zip ({BundleName}).");
    }

    /// <summary>The prefixes of the folders that hold <paramref name="path"/> (a file's path below the folder), each once in any case.</summary>
    private static List<string> Holders(IEnumerable<string> names, string path) => names
        .Where(name => name.Equals(path, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase))
        .Select(name => name[..^path.Length]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? One(List<string> found, string what, bool required) => found.Count switch
    {
        1 => found[0],
        0 when !required => null,
        0 => throw new InstallRefusedException($"This zip has no {what} in it, so it is not the client zip ({BundleName})."),
        _ => throw new InstallRefusedException($"This zip has more than one {what} in it, so it is not known which to use. Choose the client zip ({BundleName})."),
    };

    private static long Bytes(List<(ZipArchiveEntry Entry, string Target)> part) => part.Sum(item => item.Entry.Length);

    /// <summary>The full path of a staged entry; it must stay under <paramref name="staging"/>.</summary>
    private static string Target(string staging, string target)
    {
        var full = Path.GetFullPath(Path.Combine(staging, target.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
        return FolderMove.Inside(full, staging) && !FolderMove.Same(full, staging) ? full
            : throw new InstallRefusedException($"The zip holds an unsafe entry ({target}), so nothing is unpacked from it. Choose the client zip ({BundleName}).");
    }

    /// <summary>A fresh staging folder with its marker. A leftover of ours is removed first; another folder of that name, or a link, stops the unpack.</summary>
    private static void Prepare(string staging)
    {
        if (Directory.Exists(staging))
        {
            if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0 || !Ours(staging))
                throw new InstallRefusedException($"A folder named {staging} is in the way: Amberline unpacks the client zip there. Move or rename that folder, then try again.");
            if (!FolderMove.TryDeleteTree(staging))
                throw new InstallRefusedException($"The folder {staging}, left from an earlier unpack, could not be removed: a program may have a file in it open. Close it, then try again.");
        }
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, Marker), "Amberline unpacks the client zip here while it installs, and removes this folder afterwards.\r\n");
    }

    private static bool Ours(string staging) => File.Exists(Path.Combine(staging, Marker)) || !Directory.EnumerateFileSystemEntries(staging).Any();

    /// <summary>
    /// One entry into its place, never over another one. .NET 10's <see cref="ZipArchiveEntry.Open"/> does not check an entry's CRC-32 (tried on
    /// 10.0.14 with one byte of a stored entry flipped: it read without an error), so it is computed here while the file is written; a file longer
    /// than its entry says stops at once.
    /// </summary>
    private static void ExtractEntry(ZipArchiveEntry entry, string target, HashSet<string> written, byte[] buffer, Meter meter, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (Name(entry).EndsWith('/'))
        {
            Directory.CreateDirectory(target);
            return;
        }
        if (!written.Add(target))
            throw new InstallRefusedException($"The zip holds {entry.FullName} twice, so it is not known which to use. Choose the client zip ({BundleName}).");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var crc = uint.MaxValue;
        long length = 0;
        using (var input = entry.Open())
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            for (int read; (read = input.Read(buffer)) > 0;)
            {
                cancel.ThrowIfCancellationRequested();
                length += read;
                if (length > entry.Length) throw new InvalidDataException($"{entry.FullName} is longer than the zip says");
                crc = Crc32(crc, buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
                meter.Add(read);
            }
        if (length != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException($"{entry.FullName} does not unpack correctly: its CRC-32 or size differs");
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(index =>
    {
        var value = (uint)index;
        for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
        return value;
    })];

    /// <summary>The zip's CRC-32 (IEEE 802.3, reflected), one byte at a time.</summary>
    private static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        return crc;
    }

    /// <summary>The unpack's bar: megabytes done of <see cref="Total"/>, reported at most ten times a second and at the end; Cancel works throughout.</summary>
    private sealed class Meter(IProgress<InstallProgress>? progress)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private TimeSpan shown = TimeSpan.FromDays(-1);
        private long done;
        public long Total;

        public void Add(long bytes, bool final = false)
        {
            done += bytes;
            if (progress is null || (!final && clock.Elapsed - shown < TimeSpan.FromMilliseconds(100))) return;
            shown = clock.Elapsed;
            progress.Report(new(string.Create(CultureInfo.InvariantCulture, $"Unpacking the client zip: {done / 1_000_000:N0} of {Total / 1_000_000:N0} MB"),
                Total <= 0 ? 100 : (int)Math.Min(100, done * 100 / Total), CanCancel: true));
        }
    }
}
