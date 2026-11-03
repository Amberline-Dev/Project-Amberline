using Amberline.Rads;

namespace Amberline.Tests;

public static class RadsLayoutChecks
{
    /// <summary>
    /// The managed-layout packer round-trips: a synthetic tree (deflated, stored, nested, empty and excluded files, one path-hash collision)
    /// packs, the manifest re-reads with the same paths, and rads verify finds every body identical. Hash and manifest constants are the ones read
    /// from the stock rads.dll. The layout record is docs/maintenance/2026-10-18-w13-rads-layout.md.
    /// </summary>
    [Check("tools.rads-pack-verify-round-trip")]
    public static void PackThenVerifyRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-rads-" + Guid.NewGuid().ToString("N"));
        try
        {
            var from = Path.Combine(root, "loose"); var to = Path.Combine(root, "rads");
            void Put(string rel, byte[] bytes) { var full = Path.Combine(from, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes); }
            Put("DATA/Characters/Annie/Annie.inibin", Enumerable.Repeat((byte)7, 4000).ToArray());   // deflates
            Put("DATA/Buffs/Stun.inibin", [1, 2, 3]);                                                  // too small to deflate
            Put("DATA/Empty.bin", []);
            Put("LEVELS/Map1/Scene/room.mat", Enumerable.Repeat((byte)3, 4000).ToArray());              // left loose: the client seeks it in text mode, RADS cannot
            Put("LEVELS/Map1/Scene/room.nvr", Enumerable.Range(0, 5000).Select(i => (byte)(i * 131 + i / 7)).ToArray());
            Put("DATA/CFG/game.cfg", [9]);                                                              // excluded by default
            Put("DATA/Characters/Corki/Corki.dds", [4, 4, 4]);                                         // the two share a path hash (found in the tool run)
            Put("DATA/Characters/Karma/Karma.dds", [5, 5, 5]);
            Put("League of Legends.exe", [0x4D, 0x5A]);                                                // never packed

            TestAssert.Equal(0x0aa075e3u, RadsLayout.PathHash("DATA/Characters/Corki/corki.dds"), "hash rule of rads.dll 0x10055490");
            TestAssert.Equal(RadsLayout.PathHash("DATA/Characters/Corki/Corki.dds"), RadsLayout.PathHash("DATA/Characters/Karma/Karma.dds"));
            TestAssert.Equal(51u, RadsLayout.ParseVersion("0.0.0.51"));

            var report = RadsLayout.Pack(from, to, new RadsLayout.PackOptions(), TextWriter.Null);
            TestAssert.Equal(4, report.Files);
            TestAssert.True(report.Left.Any(x => x.Contains("Corki.dds") && x.Contains("collides")) && report.Left.Any(x => x.Contains("game.cfg")));

            var archive = File.ReadAllBytes(Path.Combine(RadsLayout.ArchiveDirectory(to, "lol_game_client", "0.0.0.51"), "Archive_1.raf"));
            TestAssert.Equal(RadsLayout.RafMagic, BitConverter.ToUInt32(archive, 0));
            TestAssert.Equal(0u, BitConverter.ToUInt32(archive, 8), "the manager index must be 0: the client creates one archive group (rads.dll 0x10057E00)");

            var (project, version, files) = RadsLayout.ReadManifest(RadsLayout.ManifestPath(to, "lol_game_client", "0.0.0.51"));
            TestAssert.Equal("lol_game_client", project);
            TestAssert.Equal(51u, version);
            TestAssert.Equal(
                ["DATA/Buffs/Stun.inibin", "DATA/Characters/Annie/Annie.inibin", "DATA/Empty.bin", "LEVELS/Map1/Scene/room.nvr"],
                files.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            TestAssert.Equal(RadsLayout.InArchive | RadsLayout.Zlib | RadsLayout.Compressed, files.Single(f => f.Path.EndsWith("Annie.inibin", StringComparison.Ordinal)).Mode);
            TestAssert.Equal(RadsLayout.InArchive | RadsLayout.Zlib, files.Single(f => f.Path.EndsWith("Stun.inibin", StringComparison.Ordinal)).Mode);
            TestAssert.True(report.Left.Any(x => x.Contains("room.mat") && x.Contains("text-mode")), "room.mat stays loose: RADS cannot Seek/Tell a text-mode file, deflated or stored");

            TestAssert.Equal(0, RadsLayout.Verify(from, to, "lol_game_client", "0.0.0.51", TextWriter.Null));
            File.WriteAllBytes(Path.Combine(from, "DATA", "Buffs", "Stun.inibin"), [1, 2, 4]);
            TestAssert.Equal(1, RadsLayout.Verify(from, to, "lol_game_client", "0.0.0.51", TextWriter.Null), "a changed loose byte must fail the verify");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The folder rule is rads.dll's own (RuntimeEnvironment 0x100781F0): the first "projects/", case-sensitive, the first "releases/" after the
    /// project, a canonical a.b.c.d version. Anything else is never managed by the client, so Ensure leaves it alone.
    /// </summary>
    [Check("tools.rads-game-folder-rule-follows-rads-dll")]
    public static void GameFolderRuleFollowsRadsDll()
    {
        var expected = new ManagedLayout.Release("lol_game_client", "0.0.0.51");
        TestAssert.Equal(expected, ManagedLayout.ParseGameDirectory(@"D:\Games\LoL\projects\lol_game_client\releases\0.0.0.51"));
        TestAssert.Equal(expected, ManagedLayout.ParseGameDirectory("/x/projects/lol_game_client/releases/0.0.0.51/"));
        // The first "projects/" wins, even inside another name: the client would take "old-x" as the project there, so that is what is built for.
        TestAssert.Equal(new ManagedLayout.Release("old-x", "0.0.0.51"), ManagedLayout.ParseGameDirectory(@"C:\old-projects\old-x\releases\0.0.0.51"));
        TestAssert.True(ManagedLayout.ParseGameDirectory(@"C:\Games\LoL") is null, "no projects/ at all");
        TestAssert.True(ManagedLayout.ParseGameDirectory(@"C:\Projects\lol_game_client\releases\0.0.0.51") is null, "the client's find is case-sensitive");
        TestAssert.True(ManagedLayout.ParseGameDirectory(@"C:\projects\lol_game_client\releases\0.0.0.051") is null, "only the canonical spelling finds the same folder again");
        TestAssert.True(ManagedLayout.ParseGameDirectory(@"C:\projects\lol_game_client\releases\latest") is null, "version must be a.b.c.d");
        TestAssert.True(ManagedLayout.ParseGameDirectory(@"C:\projects\lol_game_client\releases") is null, "no version component");
        TestAssert.True(ManagedLayout.ParseGameDirectory(@"C:\projects\lol_game_client") is null, "no releases/ after the project");
    }

    /// <summary>
    /// Ensure builds once, keeps a layout whose stamp matches, ignores files the client rewrites, rebuilds after a real change or a lost archive,
    /// never touches a layout it did not build, and skips without writing anything when the disk is short or the folder is not RADS-shaped.
    /// </summary>
    [Check("tools.rads-managed-layout-ensure-stamp-and-guards")]
    public static void EnsureStampAndGuards()
    {
        var temp = Path.Combine(Path.GetTempPath(), "amberline-rads-ensure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = Path.Combine(temp, "projects", "lol_game_client", "releases", "0.0.0.51");
            var root = Path.Combine(temp, "rads");
            void Put(string rel, byte[] bytes) { var full = Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes); }
            Put("DATA/Characters/Annie/Annie.inibin", Enumerable.Repeat((byte)7, 4000).ToArray());
            Put("DATA/Buffs/Stun.inibin", [1, 2, 3]);
            Put("LEVELS/Map1/Scene/room.nvr", Enumerable.Range(0, 5000).Select(i => (byte)(i * 131 + i / 7)).ToArray());
            Put("DATA/loadtimings_Map1.txt", [1]);                      // rewritten by every game
            Put("DATA/Menu/HUD/hud1920x1080.ini", [2]);                  // rewritten when the HUD is edited
            Put("League of Legends.exe", [0x4D, 0x5A]);

            var built = ManagedLayout.Ensure(game, root);
            TestAssert.Equal(ManagedLayout.Outcome.Built, built.Outcome, built.ToString());
            TestAssert.Equal(3, built.Files);
            TestAssert.True(!built.ToString().Contains(temp, StringComparison.OrdinalIgnoreCase), "the log line holds no path");
            TestAssert.Equal(0, RadsLayout.Verify(game, root, "lol_game_client", "0.0.0.51", TextWriter.Null));
            TestAssert.True(!Directory.EnumerateFileSystemEntries(root).Any(e => Path.GetFileName(e).StartsWith('.')), "no lock or staging directory is left behind");

            TestAssert.Equal(ManagedLayout.Outcome.Current, ManagedLayout.Ensure(game, root).Outcome, "an unchanged folder keeps its layout");
            Put("DATA/loadtimings_Map1.txt", [1, 2, 3]);
            Put("DATA/Menu/HUD/hud1920x1080.ini", [9, 9]);
            TestAssert.Equal(ManagedLayout.Outcome.Current, ManagedLayout.Ensure(game, root).Outcome, "files the client rewrites are not part of the stamp");

            Put("DATA/Buffs/Stun.inibin", [1, 2, 4]);                    // same size, new content: the write time is in the stamp
            File.SetLastWriteTimeUtc(Path.Combine(game, "DATA", "Buffs", "Stun.inibin"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var rebuilt = ManagedLayout.Ensure(game, root);
            TestAssert.Equal(ManagedLayout.Outcome.Built, rebuilt.Outcome, "a changed packed file is a stale layout");
            TestAssert.Equal(0, RadsLayout.Verify(game, root, "lol_game_client", "0.0.0.51", TextWriter.Null));

            var archive = Directory.EnumerateFiles(RadsLayout.ArchiveDirectory(root, "lol_game_client", "0.0.0.51"), "*.raf.dat").Single();
            File.SetAttributes(archive, FileAttributes.Normal);
            File.Delete(archive);                                          // what rads.dll does to an archive it cannot parse
            TestAssert.Equal(ManagedLayout.Outcome.Built, ManagedLayout.Ensure(game, root).Outcome, "a lost archive is rebuilt");
            TestAssert.Equal(0, RadsLayout.Verify(game, root, "lol_game_client", "0.0.0.51", TextWriter.Null));

            // A game is running (it may have the archives open): a stale layout is taken out of use (manifest and stamp go, archives stay), nothing is rebuilt.
            // The next start, with no game running, clears the leftovers and builds.
            Put("DATA/Buffs/Stun.inibin", [1, 2, 6]);
            File.SetLastWriteTimeUtc(Path.Combine(game, "DATA", "Buffs", "Stun.inibin"), new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
            var whileRunning = ManagedLayout.Ensure(game, root, new ManagedLayout.EnsureOptions(GameRunning: true));
            TestAssert.Equal(ManagedLayout.Outcome.Skipped, whileRunning.Outcome, whileRunning.ToString());
            TestAssert.True(!File.Exists(RadsLayout.ManifestPath(root, "lol_game_client", "0.0.0.51")), "a stale layout does not stay visible while its replacement waits");
            TestAssert.True(Directory.EnumerateFiles(RadsLayout.ArchiveDirectory(root, "lol_game_client", "0.0.0.51")).Any(), "the archives of a possibly running game stay");
            TestAssert.Equal(ManagedLayout.Outcome.Built, ManagedLayout.Ensure(game, root).Outcome, "the next start builds");

            // Not ours: a manifest without our stamp is left exactly as it is.
            var manifest = RadsLayout.ManifestPath(root, "lol_game_client", "0.0.0.51");
            File.Delete(RadsLayout.StampPath(root, "lol_game_client", "0.0.0.51"));
            var before = File.ReadAllBytes(manifest);
            TestAssert.Equal(ManagedLayout.Outcome.Skipped, ManagedLayout.Ensure(game, root).Outcome);
            TestAssert.True(File.ReadAllBytes(manifest).AsSpan().SequenceEqual(before), "a foreign layout is never rewritten");

            // A dry run decides and reports without creating anything, not even the root.
            var planned = Path.Combine(temp, "plan-root");
            TestAssert.Equal(ManagedLayout.Outcome.Planned, ManagedLayout.Ensure(game, planned, new ManagedLayout.EnsureOptions(DryRun: true)).Outcome);
            TestAssert.True(!Directory.Exists(planned), "a dry run writes nothing");

            // Short disk: nothing is created for a release that has no layout yet.
            var other = Path.Combine(temp, "other-root");
            var skipped = ManagedLayout.Ensure(game, other, new ManagedLayout.EnsureOptions(FreeBytes: _ => 1L << 20));
            TestAssert.Equal(ManagedLayout.Outcome.Skipped, skipped.Outcome, skipped.ToString());
            TestAssert.True(!File.Exists(RadsLayout.ManifestPath(other, "lol_game_client", "0.0.0.51")), "no manifest without space");

            // Not RADS-shaped: the client would run unmanaged whatever is in the root.
            var flat = Path.Combine(temp, "flat-game");
            Directory.CreateDirectory(Path.Combine(flat, "DATA"));
            File.WriteAllBytes(Path.Combine(flat, "DATA", "a.bin"), [1]);
            TestAssert.Equal(ManagedLayout.Outcome.Unmanaged, ManagedLayout.Ensure(flat, Path.Combine(temp, "flat-root")).Outcome);
            TestAssert.True(!Directory.Exists(Path.Combine(temp, "flat-root")), "an unmanaged folder creates nothing");
        }
        finally
        {
            if (Directory.Exists(temp)) ManagedLayout.DeleteTree(temp);
        }
    }

    /// <summary>The companion's wrapper: root from the registry value (else the client's default), background start, a path-free line.</summary>
    [Check("server.air-managed-layout-root-and-background-start")]
    public static void CompanionRootAndBackgroundStart()
    {
        TestAssert.Equal(RadsLayout.DefaultRoot, Amberline.AirCompanion.GameManagedLayout.ResolveRoot(() => null));
        TestAssert.Equal(RadsLayout.DefaultRoot, Amberline.AirCompanion.GameManagedLayout.ResolveRoot(() => "  "));
        TestAssert.Equal(Path.Combine(Path.GetTempPath(), "x"), Amberline.AirCompanion.GameManagedLayout.ResolveRoot(() => Path.Combine(Path.GetTempPath(), "x")));
        TestAssert.True(Amberline.AirCompanion.GameManagedLayout.ResolveRoot(() => "rads") is null, "a relative root is resolved from the client's own folder, which the companion cannot build for");

        var temp = Path.Combine(Path.GetTempPath(), "amberline-rads-companion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = Path.Combine(temp, "projects", "lol_game_client", "releases", "0.0.0.51");
            Directory.CreateDirectory(Path.Combine(game, "DATA"));
            File.WriteAllBytes(Path.Combine(game, "DATA", "a.bin"), Enumerable.Repeat((byte)1, 1000).ToArray());
            var written = new List<string>();
            var task = Amberline.AirCompanion.GameManagedLayout.Start(game, written.Add, Path.Combine(temp, "rads"), gameRunning: false);
            var line = task.GetAwaiter().GetResult();
            TestAssert.True(line.StartsWith("managed-layout: built 1 files", StringComparison.Ordinal), line);
            TestAssert.Equal([line], written.ToArray());
            TestAssert.True(!line.Contains(temp, StringComparison.OrdinalIgnoreCase), "the line holds no path");

            // Not RADS-shaped and a volume without 8.3 short names: the client would stop at hero creation ("Avatar spell not found!"), so the log says so.
            var flat = Path.Combine(temp, "flat-game");
            Directory.CreateDirectory(Path.Combine(flat, "DATA"));
            File.WriteAllBytes(Path.Combine(flat, "DATA", "a.bin"), [1]);
            var lines = new List<string>();
            Amberline.AirCompanion.GameManagedLayout.Start(flat, lines.Add, Path.Combine(temp, "rads-flat"), gameRunning: false, shortNames: () => "off").GetAwaiter().GetResult();
            TestAssert.Equal(2, lines.Count);
            TestAssert.True(lines[0].StartsWith("managed-layout: unmanaged", StringComparison.Ordinal) && lines[1].Contains("8.3 short names", StringComparison.Ordinal), string.Join(" | ", lines));
            TestAssert.True(!lines[1].Contains(temp, StringComparison.OrdinalIgnoreCase), "the warning holds no path");
            lines.Clear();
            Amberline.AirCompanion.GameManagedLayout.Start(flat, lines.Add, Path.Combine(temp, "rads-flat"), gameRunning: false, shortNames: () => "on").GetAwaiter().GetResult();
            TestAssert.Equal(1, lines.Count);
            lines.Clear();
            Amberline.AirCompanion.GameManagedLayout.Start(game, lines.Add, Path.Combine(temp, "rads"), gameRunning: false, shortNames: () => "off").GetAwaiter().GetResult();
            TestAssert.Equal(1, lines.Count);   // the layout is active (current): short names do not matter
        }
        finally
        {
            if (Directory.Exists(temp)) ManagedLayout.DeleteTree(temp);
        }
    }
}
