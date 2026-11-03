using System.Diagnostics;
using System.Globalization;

// Linked as source into the AIR companion as well (see RadsLayout.cs): keep this file free of references to the rest of the server library.
#if AMBERLINE_RADS_COMPANION
namespace Amberline.AirCompanion.Rads;
#else
namespace Amberline.Rads;
#endif

/// <summary>
/// Builds and maintains the managed RADS layout of one game folder under one RADS root, and decides when to leave it alone.
/// Everything the stock rads.dll requires is in docs/maintenance/2026-10-18-w13-rads-layout.md; this class adds the policy a machine that is
/// not ours needs: the layout is built from the folder's own files, into a staging directory, and becomes visible only when the manifest is
/// moved into place last (a client that starts earlier simply runs unmanaged, which is always correct); a layout that is not provably the
/// current folder's is retired before anything else, because a file that is in the manifest is never read from the folder.
/// </summary>
public static class ManagedLayout
{
    public const string LockName = ".amberline-layout.lock";
    public const string StagePrefix = ".amberline-build-";

    /// <summary>Marginal disk use of the layout relative to the packed bytes (measured 0.52 on the DATA/LEVELS tree) plus a fixed reserve.</summary>
    private const double StoredFraction = 0.6;
    private const long ReserveBytes = 256L << 20;

    public sealed record Release(string Project, string Version);

    public enum Outcome { Unmanaged, Current, Built, Skipped, Failed, Planned }

    /// <summary>The result of <see cref="Ensure"/>; <see cref="ToString"/> is one line and holds no path.</summary>
    public sealed record Result(Outcome Outcome, string Detail, int Files = 0, int Archives = 0, long StoredBytes = 0, double Seconds = 0)
    {
        public override string ToString() => Outcome switch
        {
            Outcome.Built => string.Create(CultureInfo.InvariantCulture, $"managed-layout: built {Files} files in {Archives} archives ({StoredBytes >> 20} MiB) in {Seconds:0} s"),
            Outcome.Current => string.Create(CultureInfo.InvariantCulture, $"managed-layout: current ({Files} files in {Archives} archives)"),
            _ => $"managed-layout: {Outcome.ToString().ToLowerInvariant()} ({Detail})",
        };
    }

    /// <param name="FreeBytes">Free bytes on the root's volume; null reads the volume. A test seam.</param>
    /// <param name="Parallelism">Deflate threads; 0 = all cores.</param>
    /// <param name="DryRun">Decide and report (<see cref="Outcome.Planned"/> for "would build") without writing anything, not even the RADS root or the lock.</param>
    /// <param name="GameRunning">A game is running now: it may have the archives open, so a stale layout is only taken out of use (manifest removed, which protects the next game) and the rebuild waits for the next start.</param>
    public sealed record EnsureOptions(Func<string, long>? FreeBytes = null, int Parallelism = 0, int BatchMegabytes = 128, bool DryRun = false, bool GameRunning = false);

    /// <summary>
    /// Reads the game folder the way rads.dll reads its current directory (RuntimeEnvironment 0x100781F0, name and version 0x10077D20): backslashes become
    /// slashes, the first "projects/" (case-sensitive) starts the project name, the first "releases/" after it starts the version, and the version has to
    /// be a.b.c.d. A folder that does not read like this is never managed by the client; null means so.
    /// </summary>
    public static Release? ParseGameDirectory(string gameDirectory)
    {
        var path = gameDirectory.Replace('\\', '/').TrimEnd('/');
        var at = path.IndexOf("projects/", StringComparison.Ordinal);
        if (at < 0) return null;
        var nameStart = at + "projects/".Length;
        var nameEnd = path.IndexOf('/', nameStart);
        if (nameEnd <= nameStart) return null;
        var release = path.IndexOf("releases/", nameEnd, StringComparison.Ordinal);
        if (release < 0) return null;
        var versionStart = release + "releases/".Length;
        if (versionStart >= path.Length) return null;
        var versionEnd = path.IndexOf('/', versionStart);
        var version = versionEnd < 0 ? path[versionStart..] : path[versionStart..versionEnd];
        try
        {
            // The client re-renders the parsed version as %d.%d.%d.%d, so only the canonical spelling finds the same folder again.
            var number = RadsLayout.ParseVersion(version);
            if (string.Create(CultureInfo.InvariantCulture, $"{number >> 24}.{(number >> 16) & 255}.{(number >> 8) & 255}.{number & 255}") != version) return null;
        }
        catch (ArgumentException) { return null; }
        return new Release(path[nameStart..nameEnd], version);
    }

    /// <summary>
    /// Makes the layout under <paramref name="root"/> match <paramref name="gameDirectory"/>: nothing to do when the stamp matches, a rebuild when it does not,
    /// and no change at all for a game folder the client could not use or a layout another tool owns. Never throws for environmental problems
    /// (no space, no permission, files in use); those come back as <see cref="Outcome.Skipped"/> or <see cref="Outcome.Failed"/> and leave the client unmanaged.
    /// </summary>
    public static Result Ensure(string gameDirectory, string root, EnsureOptions? options = null, TextWriter? log = null, CancellationToken cancel = default)
    {
        options ??= new EnsureOptions();
        log ??= TextWriter.Null;
        var clock = Stopwatch.StartNew();
        gameDirectory = Path.GetFullPath(gameDirectory);
        root = Path.GetFullPath(root);
        var release = ParseGameDirectory(gameDirectory);
        if (release is null)
            return new(Outcome.Unmanaged, "the game folder is not under projects/<name>/releases/<version>, so the client runs on its own files");
        if (!Directory.Exists(Path.Combine(gameDirectory, "DATA"))) return new(Outcome.Unmanaged, "the game folder has no DATA");

        FileStream? gate = null;
        string? stage = null;
        try
        {
            if (!options.DryRun)
            {
                Directory.CreateDirectory(root);
                try { gate = new FileStream(Path.Combine(root, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { return new(Outcome.Skipped, "another build is running"); }
                foreach (var old in Directory.EnumerateDirectories(root, StagePrefix + "*")) DeleteTree(old);   // killed builds; we hold the lock
            }

            var pack = new RadsLayout.PackOptions(release.Project, release.Version, Parallelism: options.Parallelism, BatchMegabytes: options.BatchMegabytes);
            var current = RadsLayout.ComputeStamp(gameDirectory, pack);
            if (current.Files == 0) return new(Outcome.Unmanaged, "nothing to pack");
            var manifest = RadsLayout.ManifestPath(root, release.Project, release.Version);
            var archiveDirectory = RadsLayout.ArchiveDirectory(root, release.Project, release.Version);

            var stale = false;
            if (File.Exists(manifest))
            {
                switch (RadsLayout.CheckStamp(root, release.Project, release.Version, current))
                {
                    case RadsLayout.StampState.Missing:
                        return new(Outcome.Skipped, "a layout for this release exists that this tool did not build; left alone");
                    case RadsLayout.StampState.Current when RadsLayout.CheckStructure(root, release.Project, release.Version).Count == 0:
                        return new(Outcome.Current, "", current.Files, Directory.EnumerateFiles(archiveDirectory, "*.raf").Count());
                }
                stale = true;
            }
            else if (Directory.Exists(archiveDirectory) && Directory.EnumerateFileSystemEntries(archiveDirectory).Any(e => !IsOurArchive(e)))
                return new(Outcome.Skipped, "the archive folder for this release holds files this tool did not write; left alone");

            if (options.GameRunning && !options.DryRun)
            {
                if (stale) TakeOutOfUse(root, release);
                return new(Outcome.Skipped, stale ? "a game is running; the stale layout was taken out of use, the rebuild waits for the next start" : "a game is running; the build waits for the next start");
            }

            // A layout that is not provably the current folder's goes first, manifest first, before anything can fail: without a manifest the client reads
            // the game folder, which is always right; a stale manifest is never right. (A half-swapped build has archives but no manifest and is cleared here too.)
            if (!options.DryRun && !Retire(root, release, out var why)) return new(Outcome.Failed, why);

            var need = (long)(current.Bytes * StoredFraction) + ReserveBytes;
            var free = options.FreeBytes?.Invoke(root) ?? new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
            if (free < need) return new(Outcome.Skipped, $"{need >> 20} MiB needed on the RADS volume, {free >> 20} MiB free");
            if (options.DryRun)
                return new(Outcome.Planned, $"would {(stale ? "rebuild" : "build")} {current.Files} files ({current.Bytes >> 20} MiB raw); {need >> 20} MiB needed, {free >> 20} MiB free", current.Files, StoredBytes: current.Bytes);

            stage = Path.Combine(root, StagePrefix + Guid.NewGuid().ToString("N"));
            var report = RadsLayout.Pack(gameDirectory, stage, pack, log, cancel);
            var problems = RadsLayout.CheckStructure(stage, release.Project, release.Version);
            if (problems.Count > 0) return new(Outcome.Failed, "the new layout failed its own check: " + problems[0]);

            // Swap in: archives, then the stamp and the report, then the manifest last (the point at which the client can see the layout).
            Directory.CreateDirectory(Path.GetDirectoryName(archiveDirectory)!);
            Directory.Move(RadsLayout.ArchiveDirectory(stage, release.Project, release.Version), archiveDirectory);
            Directory.CreateDirectory(RadsLayout.ReleaseDirectory(root, release.Project, release.Version));
            File.Move(RadsLayout.StampPath(stage, release.Project, release.Version), RadsLayout.StampPath(root, release.Project, release.Version), overwrite: true);
            File.Move(Path.Combine(stage, "pack-report.txt"), Path.Combine(RadsLayout.ReleaseDirectory(root, release.Project, release.Version), "managed.pack-report.txt"), overwrite: true);
            File.Move(RadsLayout.ManifestPath(stage, release.Project, release.Version), manifest);
            return new(Outcome.Built, "", report.Files, report.Archives, report.StoredBytes, clock.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) { return new(Outcome.Skipped, "cancelled"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new(Outcome.Failed, exception.GetType().Name + " while building");
        }
        finally
        {
            if (stage is not null) { try { DeleteTree(stage); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            gate?.Dispose();
        }
    }

    private static bool IsOurArchive(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("Archive_", StringComparison.Ordinal) && (name.EndsWith(".raf", StringComparison.Ordinal) || name.EndsWith(".raf.dat", StringComparison.Ordinal));
    }

    /// <summary>Deletes only the manifest and the stamp: a client that starts afterwards runs on the game folder, and a running one keeps its archives.</summary>
    private static void TakeOutOfUse(string root, Release release)
    {
        try
        {
            foreach (var file in new[] { RadsLayout.ManifestPath(root, release.Project, release.Version), RadsLayout.StampPath(root, release.Project, release.Version) })
                if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }   // nothing more to do; the next start retries
    }

    /// <summary>Removes the manifest, then the stamp, then the archives of one release. False (with a reason) when a running client holds an archive open.</summary>
    private static bool Retire(string root, Release release, out string why)
    {
        why = "";
        try
        {
            foreach (var file in new[] { RadsLayout.ManifestPath(root, release.Project, release.Version), RadsLayout.StampPath(root, release.Project, release.Version) })
                if (File.Exists(file)) File.Delete(file);   // File.Delete throws when the directory is missing, not only the file
            var archives = RadsLayout.ArchiveDirectory(root, release.Project, release.Version);
            if (!Directory.Exists(archives)) return true;
            foreach (var file in Directory.EnumerateFiles(archives).Where(IsOurArchive))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            Directory.Delete(archives);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            why = "the old layout could not be removed (a running game may be using it); the next start tries again";
            return false;
        }
    }

    /// <summary>Deletes a directory tree including read-only files (the archives are read-only on purpose).</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
