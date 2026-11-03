using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

// The companion links this file (and ManagedLayout.cs) as source instead of referencing the server library: a project reference would
// put the whole server (Amberline.dll, BouncyCastle) into the friends' companion package. Its copy lives in its own namespace so the
// test assembly, which sees both, never meets one type name twice.
#if AMBERLINE_RADS_COMPANION
namespace Amberline.AirCompanion.Rads;
#else
namespace Amberline.Rads;
#endif

/// <summary>
/// The stock 1.0.0.126 client's "managed" file layout: a RADS release manifest plus RAF archives (rads.dll). Every format rule below
/// was read from the stock rads.dll, not from the web; the addresses are in docs/maintenance/2026-10-18-w13-rads-layout.md.
/// Layout under a RADS root (the client resolves the root from HKLM\Software\Riot Games\RADS\LocalRootFolder, default c:\rads):
///   projects/&lt;project&gt;/releases/&lt;version&gt;/releasemanifest
///   projects/&lt;project&gt;/filearchives/&lt;version&gt;/Archive_N.raf + Archive_N.raf.dat
/// The client's working directory must contain projects/&lt;project&gt;/releases/&lt;version&gt; for it to pick managed mode at all.
/// </summary>
public static class RadsLayout
{
    public const uint RafMagic = 0x18BE0EF0, RafVersion = 1;                    // the archive file reader
    public const uint ManifestMagic = 0x4D534C52, ManifestVersion = 0x10001;     // "RLSM", the release manifest loader
    public const uint InArchive = 2, Zlib = 4, Compressed = 0x10;                // entry mode bits, the file opener, the managed-compressed file type

    /// <summary>Bump when the packer's output (format, default exclusions, compression rule) changes: every stamped layout is then rebuilt.</summary>
    public const int PackerVersion = 2;

    /// <summary>The root rads.dll uses when the registry value HKLM\Software\Riot Games\RADS\LocalRootFolder is absent (0x100877E0).</summary>
    public const string DefaultRoot = @"c:\rads";

    /// <summary>rads.dll 0x10055490: lower-cased ASCII, h = (h &lt;&lt; 4) + c, the top nibble folded back in.</summary>
    public static uint PathHash(string path)
    {
        uint h = 0;
        foreach (var ch in path)
        {
            h = (h << 4) + (uint)char.ToLowerInvariant(ch);
            var top = h & 0xF0000000;
            if (top != 0) h = h ^ (top >> 24) ^ top;
        }
        return h;
    }

    /// <summary>"0.0.0.51" as the client renders and parses release versions (%d.%d.%d.%d of a big-endian u32).</summary>
    public static uint ParseVersion(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(p => !byte.TryParse(p, out _))) throw new ArgumentException($"version '{text}' is not a.b.c.d");
        return parts.Aggregate(0u, (acc, p) => (acc << 8) | byte.Parse(p));
    }

    public static string ManifestPath(string root, string project, string version) => Path.Combine(root, "projects", project, "releases", version, "releasemanifest");
    public static string ArchiveDirectory(string root, string project, string version) => Path.Combine(root, "projects", project, "filearchives", version);

    /// <param name="Parallelism">Worker threads for deflate; 0 = all cores (the companion passes a quarter of them so a game that loads meanwhile is not starved).</param>
    /// <param name="BatchMegabytes">Raw bytes held in memory per deflate batch (the companion keeps this small).</param>
    public sealed record PackOptions(string Project = "lol_game_client", string Version = "0.0.0.51",
        string[]? Dirs = null, string[]? Exclude = null, int ArchiveMegabytes = 256, bool Store = false,
        int Parallelism = 0, int BatchMegabytes = 128)
    {
        public string[] PackedDirs => Dirs ?? ["DATA", "LEVELS"];
        // NOT RETAIL: files the client, the companion or the owner rewrite stay loose, so a stale archived copy can never shadow them
        // (a file that is in the manifest is never read from the folder). Retail deployed settings and locale files as plain (mode 0)
        // files too. Rewritten by the client or the player in the owner's live tree: DATA/CFG/*, DATA/loadtimings_*.txt (every game),
        // DATA/Menu/HUD/* (hud<width>x<height>.ini and chat.ini, saved when the HUD is edited). fontconfig_* is deployed by the language pack.
        // IMEConfig.xml is the one file the client opens by an absolute name (current folder + "data/menu/IMEConfig.xml"): the file opener
        // finds no manifest entry for an absolute name and fopens it, so it has to be on disk (found with the Frida hook of the W13
        // throwaway runs; the only absolute RADS open of a game).
        public string[] Excluded => Exclude ?? ["DATA/CFG/", "DATA/loadtimings_", "DATA/Menu/HUD/", "DATA/Menu/fontconfig_", "DATA/Menu/IMEConfig.xml"];

        // NOT RETAIL: these files stay loose. The client opens each of them in text mode and then asks for the file position and seeks (the loader of a
        // map's scene materials, via the file wrapper). FileManagedCompressed and FileManagedUncompressed both refuse Seek and Tell in
        // text mode ("Only supported when file is opened for binary access", logged twice per game; found with the W15 Frida hook on 0x1007E5C0:
        // LEVELS/Map1/Scene/room.mat is the only text-mode file of a game that seeks, and storing it uncompressed did not help); FileUnmanaged is a plain fopen
        // and supports both, as in a loose game. The other 130 text-mode files of a game (Animations.list, *.sco, Env.ini ...) are only read forward.
        public string[] LooseSuffixes => ["/Scene/room.mat"];
    }

    public sealed record ManifestFile(string Path, uint Version, byte[] Md5, uint Mode, uint RawSize, uint StoredSize);

    public sealed record PackReport(int Files, long RawBytes, long StoredBytes, int Archives, IReadOnlyList<string> Left);

    private sealed record Prepared(string Path, byte[] Md5, uint Mode, uint RawSize, byte[] Stored);

    // ---------------------------------------------------------------- pack

    public sealed record SourceFile(string Path, long Size, long WriteTicks);

    /// <summary>Everything under the packed directories that is a candidate for the manifest, sorted by path; excluded and non-ASCII names go to <paramref name="left"/>.</summary>
    private static List<SourceFile> EnumerateSource(string from, PackOptions options, List<string> left, TextWriter log)
    {
        var files = new List<SourceFile>();
        foreach (var dir in options.PackedDirs)
        {
            var full = Path.Combine(from, dir);
            if (!Directory.Exists(full)) { log.WriteLine($"note: '{dir}' is not in the source, skipped"); continue; }
            foreach (var info in new DirectoryInfo(full).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(from, info.FullName).Replace('\\', '/');
                if (options.Excluded.Any(e => e.StartsWith('.') ? rel.EndsWith(e, StringComparison.OrdinalIgnoreCase) : rel.StartsWith(e, StringComparison.OrdinalIgnoreCase))) left.Add($"{rel}\texcluded");
                else if (options.LooseSuffixes.Any(x => rel.EndsWith(x, StringComparison.OrdinalIgnoreCase))) left.Add($"{rel}\ttext-mode file the client seeks (RADS cannot)");
                else if (rel.Any(c => c is < ' ' or > '~')) left.Add($"{rel}\tnon-ASCII name (the client hashes bytes)");
                else files.Add(new SourceFile(rel, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return files;
    }

    public static PackReport Pack(string from, string to, PackOptions options, TextWriter log, CancellationToken cancel = default)
    {
        from = Path.GetFullPath(from); to = Path.GetFullPath(to);
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"loose release root '{from}' does not exist");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) throw new IOException($"target '{to}' exists and is not empty; pack writes only into a new directory");
        var version = ParseVersion(options.Version);
        var left = new List<string>();
        var source = EnumerateSource(from, options, left, log);
        var stamp = StampOf(source, options);
        var files = source.Select(f => f.Path).ToList();
        // rads.dll finds an archived file by its path hash alone (the archive search does not compare the path), so a shared hash would
        // serve the wrong file. NOT RETAIL: colliding files stay loose and out of the manifest (the client then opens them from the folder).
        foreach (var group in files.GroupBy(PathHash).Where(g => g.Count() > 1).ToList())
            foreach (var rel in group) { files.Remove(rel); left.Add($"{rel}\tpath hash {group.Key:x8} collides"); }

        var archiveDir = ArchiveDirectory(to, options.Project, options.Version);
        Directory.CreateDirectory(archiveDir);
        var entries = new List<ManifestFile>(files.Count);
        var limit = (long)options.ArchiveMegabytes * 1024 * 1024;
        var batchLimit = (long)Math.Max(1, options.BatchMegabytes) << 20;
        var parallel = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = options.Parallelism > 0 ? options.Parallelism : -1 };
        RafWriter? raf = null; var archives = 0; long rawTotal = 0, storedTotal = 0;
        for (var i = 0; i < files.Count;)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = new List<string>(); long bytes = 0;
            while (i < files.Count && batch.Count < 64 && (batch.Count == 0 || bytes < batchLimit)) { batch.Add(files[i]); bytes += new FileInfo(Path.Combine(from, files[i])).Length; i++; }
            var prepared = new Prepared[batch.Count];
            Parallel.For(0, batch.Count, parallel, j => prepared[j] = Prepare(from, batch[j], options.Store));
            foreach (var p in prepared)
            {
                if (raf is null || (raf.DataLength > 0 && raf.DataLength + p.Stored.Length > limit))
                {
                    raf?.Finish();
                    raf = new RafWriter(Path.Combine(archiveDir, $"Archive_{++archives}.raf"));
                }
                raf.Add(p.Path, p.Stored);
                entries.Add(new ManifestFile(p.Path, version, p.Md5, p.Mode, p.RawSize, (uint)p.Stored.Length));
                rawTotal += p.RawSize; storedTotal += p.Stored.Length;
            }
            if (entries.Count % 2000 < batch.Count) log.WriteLine($"  {entries.Count}/{files.Count} files");
        }
        raf?.Finish();
        WriteManifest(ManifestPath(to, options.Project, options.Version), options.Project, version, entries);
        // The files the client must still find in its own folder (everything not in the manifest under the packed directories).
        File.WriteAllLines(Path.Combine(to, "pack-report.txt"), left);
        WriteStamp(to, stamp);
        // NOT RETAIL: read-only archives. rads.dll deletes an archive it cannot parse (the archive loader, DeleteFileA);
        // a read-only file survives that, so a bad pack can never be eaten by the client.
        foreach (var file in Directory.EnumerateFiles(archiveDir)) File.SetAttributes(file, FileAttributes.ReadOnly);
        return new PackReport(entries.Count, rawTotal, storedTotal, archives, left);
    }

    private static Prepared Prepare(string from, string rel, bool store)
    {
        var raw = File.ReadAllBytes(Path.Combine(from, rel));
        var md5 = MD5.HashData(raw);
        if (!store && raw.Length >= 128)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            // Reconstruction: keep the deflated copy only when it saves 10 percent or more; retail deflated per its own build rules, which are unknown.
            if (ms.Length <= raw.Length * 0.9) return new Prepared(rel, md5, InArchive | Zlib | Compressed, (uint)raw.Length, ms.ToArray());
        }
        return new Prepared(rel, md5, InArchive | Zlib, (uint)raw.Length, raw);
    }

    /// <summary>One Archive_N.raf (index) + Archive_N.raf.dat (concatenated file bodies); the layout follows the client's archive writer.</summary>
    private sealed class RafWriter(string rafPath)
    {
        private readonly FileStream _dat = new(rafPath + ".dat", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        private readonly List<(string Path, uint Hash, uint Offset, uint Size)> _entries = [];
        public long DataLength => _dat.Length;

        public void Add(string path, byte[] stored)
        {
            if (_dat.Length + stored.Length > uint.MaxValue) throw new IOException("archive data would pass 4 GiB");
            _entries.Add((path, PathHash(path), (uint)_dat.Length, (uint)stored.Length));
            _dat.Write(stored);
        }

        public void Finish()
        {
            _dat.Dispose();
            var n = _entries.Count;
            var pathList = new MemoryStream();
            using (var pw = new BinaryWriter(pathList, Encoding.ASCII, leaveOpen: true))
            {
                var stringsAt = 8 + 8 * n;
                pw.Write((uint)0); pw.Write((uint)n);                           // size (patched below), count
                foreach (var e in _entries) { pw.Write((uint)stringsAt); pw.Write((uint)(e.Path.Length + 1)); stringsAt += e.Path.Length + 1; }
                foreach (var e in _entries) { pw.Write(Encoding.ASCII.GetBytes(e.Path)); pw.Write((byte)0); }
                pw.Flush(); pathList.Position = 0; pw.Write((uint)pathList.Length);
            }
            using var w = new BinaryWriter(File.Create(rafPath));
            // The third header word is the client's "manager index": the archive reader keeps it and the archive loader uses it to
            // pick the archive group, of which the manager creates exactly one. Anything but 0 trips the CRT invalid-parameter
            // handler (process exit 0xC000000D, seen on the first managed launch with the archive number here).
            w.Write(RafMagic); w.Write(RafVersion); w.Write(0u);
            w.Write(20u); w.Write((uint)(20 + 4 + 16 * n));                    // file list offset, path list offset
            w.Write((uint)n);
            foreach (var (e, pathIndex) in _entries.Select((e, i) => (e, i)).OrderBy(x => x.e.Hash).ThenBy(x => x.i))   // the client binary-searches by hash
            { w.Write(e.Hash); w.Write(e.Offset); w.Write(e.Size); w.Write((uint)pathIndex); }
            w.Write(pathList.ToArray());
        }
    }

    // --------------------------------------------------------------- stamp

    /// <summary>
    /// What a managed layout was built from. A file that is in the manifest is never read from the game folder, so a layout whose source changed
    /// afterwards would silently shadow the change: the stamp (stored beside the manifest) lets the owner of the layout see that and rebuild.
    /// </summary>
    /// <param name="Options">Packed directories, exclusions and the store flag: a different policy is a different layout.</param>
    /// <param name="Hash">SHA-256 over every candidate file's relative path, size and last write time (UTC ticks).</param>
    public sealed record SourceStamp(int Packer, string Project, string Version, string Options, int Files, long Bytes, string Hash)
    {
        public IReadOnlyList<string> Lines() =>
        [
            "amberline-managed-layout",
            $"packer={Packer}", $"project={Project}", $"version={Version}", $"options={Options}",
            $"files={Files}", $"bytes={Bytes}", $"source={Hash}",
        ];
    }

    public enum StampState { Missing, Stale, Current }

    private static SourceStamp StampOf(IReadOnlyList<SourceFile> files, PackOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        foreach (var f in files) { hash.AppendData(Encoding.UTF8.GetBytes($"{f.Path}\t{f.Size}\t{f.WriteTicks}\n")); bytes += f.Size; }
        var policy = $"dirs={string.Join(',', options.PackedDirs)};exclude={string.Join(',', options.Excluded)};store={(options.Store ? 1 : 0)};loose={string.Join(',', options.LooseSuffixes)}";
        return new SourceStamp(PackerVersion, options.Project, options.Version, policy, files.Count, bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    /// <summary>The stamp of the source as it is now: one directory walk, no file is read.</summary>
    public static SourceStamp ComputeStamp(string from, PackOptions options) =>
        StampOf(EnumerateSource(Path.GetFullPath(from), options, [], TextWriter.Null), options);

    public static string ReleaseDirectory(string root, string project, string version) => Path.Combine(root, "projects", project, "releases", version);
    public static string StampPath(string root, string project, string version) => Path.Combine(ReleaseDirectory(root, project, version), "managed.stamp");

    private static string ArchiveInventory(string archiveDir) =>
        !Directory.Exists(archiveDir) ? "" : string.Join(';', Directory.EnumerateFiles(archiveDir).Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => $"{f.Name}:{f.Length}"));

    private static void WriteStamp(string root, SourceStamp stamp) =>
        File.WriteAllLines(StampPath(root, stamp.Project, stamp.Version),
            stamp.Lines().Append("archives=" + ArchiveInventory(ArchiveDirectory(root, stamp.Project, stamp.Version))));

    /// <summary>Compares the stamp beside the manifest with <paramref name="current"/> and with the archives as they are on disk now (the client deletes archives it cannot parse).</summary>
    public static StampState CheckStamp(string root, string project, string version, SourceStamp current)
    {
        var path = StampPath(root, project, version);
        if (!File.Exists(path)) return StampState.Missing;
        var lines = File.ReadAllLines(path);
        var expected = current.Lines().Append("archives=" + ArchiveInventory(ArchiveDirectory(root, project, version)));
        return lines.SequenceEqual(expected) ? StampState.Current : StampState.Stale;
    }

    /// <summary>
    /// A cheap sanity check of a finished layout, reading only the manifest and the archive indexes: they parse with the client's rules, the hashes are
    /// sorted, every archive body is long enough, and every manifest path is found by hash. Empty means good. (<see cref="Verify"/> also compares every byte.)
    /// </summary>
    public static IReadOnlyList<string> CheckStructure(string root, string project, string version)
    {
        var problems = new List<string>();
        try
        {
            var (name, manifestVersion, files) = ReadManifest(ManifestPath(root, project, version));
            if (name != project) problems.Add($"manifest project is '{name}'");
            if (manifestVersion != ParseVersion(version)) problems.Add($"manifest version {manifestVersion:x8} is not {version}");
            var hashes = new HashSet<uint>();
            var archives = Directory.EnumerateFiles(ArchiveDirectory(root, project, version), "*.raf").Select(ReadRaf).ToList();
            if (archives.Count == 0) problems.Add("no archive index");
            foreach (var a in archives)
            {
                var dat = new FileInfo(a.Name + ".dat");
                if (!dat.Exists) { problems.Add("an archive body is missing"); continue; }
                for (var i = 0; i < a.Entries.Length; i++)
                {
                    var e = a.Entries[i];
                    if (i > 0 && e.Hash < a.Entries[i - 1].Hash) { problems.Add("an archive index is not sorted by hash"); break; }
                    if (e.Offset + (long)e.Size > dat.Length) { problems.Add("an archive body is shorter than its index says"); break; }
                    hashes.Add(e.Hash);
                }
            }
            if (files.Any(f => !hashes.Contains(PathHash(f.Path)))) problems.Add("a manifest file is in no archive");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            problems.Add(exception.GetType().Name + ": " + exception.Message);
        }
        return problems;
    }

    // ------------------------------------------------------------ manifest

    private sealed class DirNode(string name)
    {
        public readonly string Name = name;
        public readonly SortedDictionary<string, DirNode> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, ManifestFile> Files = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tables in the order the manifest writer uses: children of a directory are contiguous and after it.</summary>
    public static void WriteManifest(string path, string project, uint version, IReadOnlyList<ManifestFile> files)
    {
        var root = new DirNode("");
        foreach (var f in files)
        {
            var parts = f.Path.Split('/'); var d = root;
            foreach (var part in parts[..^1]) { if (!d.Dirs.TryGetValue(part, out var next)) d.Dirs[part] = next = new DirNode(part); d = next; }
            d.Files[parts[^1]] = f;
        }
        var strings = new List<string> { "" }; var stringIndex = new Dictionary<string, uint> { [""] = 0 };
        uint S(string s) { if (!stringIndex.TryGetValue(s, out var i)) { stringIndex[s] = i = (uint)strings.Count; strings.Add(s); } return i; }
        var order = new List<DirNode> { root }; var dirRows = new List<uint[]>(); var fileRows = new List<(uint Name, ManifestFile File)>();
        for (var i = 0; i < order.Count; i++)
        {
            var d = order[i]; var subStart = order.Count;
            order.AddRange(d.Dirs.Values);
            dirRows.Add([S(d.Name), (uint)subStart, (uint)d.Dirs.Count, (uint)fileRows.Count, (uint)d.Files.Count]);
            foreach (var (name, f) in d.Files) fileRows.Add((S(name), f));
        }
        var projectIndex = S(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(ManifestMagic); w.Write(ManifestVersion); w.Write(projectIndex); w.Write(version);
        w.Write((uint)dirRows.Count); foreach (var row in dirRows) foreach (var v in row) w.Write(v);
        w.Write((uint)fileRows.Count);
        foreach (var (name, f) in fileRows)
        {
            w.Write(name); w.Write(f.Version); w.Write(f.Md5);
            // After the 16-byte MD5: mode, raw size (the zlib expected size, rads.dll reads it at entry+0x34), stored size. The last two words
            // are never read by the game; 0 is a Reconstruction (the retail meaning is unknown).
            w.Write(f.Mode); w.Write(f.RawSize); w.Write(f.StoredSize); w.Write(0u); w.Write(0u);
        }
        var blob = strings.SelectMany(s => Encoding.ASCII.GetBytes(s).Append((byte)0)).ToArray();
        w.Write((uint)strings.Count); w.Write((uint)blob.Length); w.Write(blob);
    }

    /// <summary>Reads a manifest the way the manifest loader does (same checks), returning the project and every file with its full path.</summary>
    public static (string Project, uint Version, List<ManifestFile> Files) ReadManifest(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadUInt32() != ManifestMagic) throw new InvalidDataException("releasemanifest: no RLSM magic");
        if (r.ReadUInt32() != ManifestVersion) throw new InvalidDataException("releasemanifest: version is not 0x10001");
        var projectIndex = r.ReadUInt32(); var version = r.ReadUInt32();
        var dirs = new uint[r.ReadUInt32()][];
        for (var i = 0; i < dirs.Length; i++) dirs[i] = Enumerable.Range(0, 5).Select(_ => r.ReadUInt32()).ToArray();
        var rows = new (uint Name, uint Version, byte[] Md5, uint Mode, uint Raw, uint Stored)[r.ReadUInt32()];
        for (var i = 0; i < rows.Length; i++)
        {
            var name = r.ReadUInt32(); var v = r.ReadUInt32(); var md5 = r.ReadBytes(16); var mode = r.ReadUInt32(); var raw = r.ReadUInt32(); var stored = r.ReadUInt32(); r.ReadUInt64();
            rows[i] = (name, v, md5, mode, raw, stored);
        }
        var count = r.ReadUInt32(); var blob = r.ReadBytes((int)r.ReadUInt32());
        var strings = Encoding.ASCII.GetString(blob).Split('\0').Take((int)count).ToArray();
        if (strings.Length != count || projectIndex >= count) throw new InvalidDataException("releasemanifest: string table or project name invalid");
        string Str(uint i) => i < strings.Length ? strings[i] : throw new InvalidDataException($"releasemanifest: string {i} out of range");

        var paths = new string[dirs.Length]; var files = new List<ManifestFile>();
        var stack = new Stack<(uint Parent, uint Index)>(); stack.Push((uint.MaxValue, 0));
        while (stack.Count > 0)
        {
            var (parent, index) = stack.Pop();
            if (!(parent == uint.MaxValue || parent < index)) throw new InvalidDataException("releasemanifest: parent directory ordering failure");
            var d = dirs[index]; var parentPath = parent == uint.MaxValue ? "" : paths[parent];
            var dirPath = (parentPath.Length > 0 ? parentPath + "/" : "") + (d[0] != uint.MaxValue ? Str(d[0]) : "");
            paths[index] = dirPath;
            for (var k = 0u; k < d[4]; k++)
            {
                var row = rows[d[3] + k];
                files.Add(new ManifestFile((dirPath.Length > 0 ? dirPath + "/" : "") + Str(row.Name), row.Version, row.Md5, row.Mode, row.Raw, row.Stored));
            }
            for (var k = 0u; k < d[2]; k++) stack.Push((index, d[1] + k));
        }
        return (Str(projectIndex), version, files);
    }

    // -------------------------------------------------------------- verify

    private sealed record RafIndex(string Name, byte[] Index, (uint Hash, uint Offset, uint Size, uint PathIndex)[] Entries, string[] Paths);

    private static RafIndex ReadRaf(string path)
    {
        var b = File.ReadAllBytes(path);
        uint U(int at) => BitConverter.ToUInt32(b, at);
        if (U(0) != RafMagic || U(4) != RafVersion) throw new InvalidDataException($"{path}: not a version 1 RAF");
        int fileList = (int)U(12), pathList = (int)U(16);
        var entries = new (uint, uint, uint, uint)[U(fileList)];
        for (var i = 0; i < entries.Length; i++) { var at = fileList + 4 + 16 * i; entries[i] = (U(at), U(at + 4), U(at + 8), U(at + 12)); }
        var paths = new string[U(pathList + 4)];
        for (var i = 0; i < paths.Length; i++) { var at = pathList + 8 + 8 * i; var start = pathList + (int)U(at); paths[i] = Encoding.ASCII.GetString(b, start, (int)U(at + 4) - 1); }
        return new RafIndex(path, b, entries, paths);
    }

    /// <summary>Re-reads manifest and archives the way rads.dll does (hash-only lookup, first archive wins) and compares every file byte for byte with the loose tree.</summary>
    public static int Verify(string from, string root, string project, string version, TextWriter log)
    {
        from = Path.GetFullPath(from);
        var (name, manifestVersion, files) = ReadManifest(ManifestPath(root, project, version));
        var failures = new List<string>();
        void Fail(string message) { if (failures.Count < 40) failures.Add(message); else if (failures.Count == 40) failures.Add("... more"); }
        if (name != project) Fail($"manifest project is '{name}', expected '{project}'");
        if (manifestVersion != ParseVersion(version)) Fail($"manifest version {manifestVersion:x8} does not match {version}");
        var archiveDir = ArchiveDirectory(root, project, version);
        var archives = Directory.EnumerateFiles(archiveDir, "*.raf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(ReadRaf).ToList();
        foreach (var a in archives)
        {
            for (var i = 1; i < a.Entries.Length; i++) if (a.Entries[i].Hash < a.Entries[i - 1].Hash) { Fail($"{a.Name}: file list is not sorted by hash"); break; }
            foreach (var e in a.Entries) if (PathHash(a.Paths[e.PathIndex]) != e.Hash) Fail($"{a.Name}: hash of '{a.Paths[e.PathIndex]}' is wrong");
            if (!File.Exists(a.Name + ".dat")) Fail($"{a.Name}.dat is missing");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0, done = 0, good = 0;
        foreach (var f in files)
        {
            var before = failures.Count; done++;
            if (done % 4000 == 0) log.WriteLine($"  {done}/{files.Count} files");
            if (!seen.Add(f.Path)) { Fail($"duplicate manifest entry {f.Path}"); continue; }
            var hash = PathHash(f.Path);
            RafIndex? found = null; var at = -1;
            foreach (var a in archives)                                        // the manager's find: archives in order, lower_bound on the hash
            {
                var (lo, hi) = (0, a.Entries.Length);
                while (lo < hi) { var mid = (lo + hi) / 2; if (a.Entries[mid].Hash < hash) lo = mid + 1; else hi = mid; }
                if (lo < a.Entries.Length && a.Entries[lo].Hash == hash) { found = a; at = lo; break; }
            }
            if (found is null) { Fail($"{f.Path}: not found in any archive"); continue; }
            var entry = found.Entries[at];
            if (!string.Equals(found.Paths[entry.PathIndex], f.Path, StringComparison.OrdinalIgnoreCase)) { Fail($"{f.Path}: hash lookup lands on '{found.Paths[entry.PathIndex]}'"); continue; }
            if (entry.Size != f.StoredSize) Fail($"{f.Path}: manifest stored size {f.StoredSize} != archive {entry.Size}");
            byte[] stored = new byte[entry.Size];
            using (var dat = File.OpenRead(found.Name + ".dat")) { dat.Seek(entry.Offset, SeekOrigin.Begin); dat.ReadExactly(stored); }
            byte[] raw;
            if ((f.Mode & Compressed) != 0)
            {
                using var z = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
                using var ms = new MemoryStream(); z.CopyTo(ms); raw = ms.ToArray();
            }
            else raw = stored;
            if (raw.Length != f.RawSize) { Fail($"{f.Path}: inflated {raw.Length} bytes, manifest says {f.RawSize}"); continue; }
            if (!MD5.HashData(raw).AsSpan().SequenceEqual(f.Md5)) Fail($"{f.Path}: MD5 differs from the manifest");
            var loose = Path.Combine(from, f.Path);
            if (!File.Exists(loose)) Fail($"{f.Path}: not in the loose source");
            else if (!File.ReadAllBytes(loose).AsSpan().SequenceEqual(raw)) Fail($"{f.Path}: bytes differ from the loose source");
            bytes += raw.Length;
            if (failures.Count == before) good++;
        }
        log.WriteLine($"verified {good} of {files.Count} manifest files across {archives.Count} archive(s), {bytes:N0} bytes compared");
        foreach (var failure in failures) log.WriteLine("FAIL " + failure);
        return failures.Count == 0 ? 0 : 1;
    }
}
