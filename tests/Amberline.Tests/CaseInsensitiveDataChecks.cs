using System.Collections.Concurrent;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Encounters;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// The shipped data and the AIR client were named for Windows, where file names ignore case. The same loads must work on a case-sensitive
/// file system (the Linux VPS), so these checks hold every lookup to the disk's spelling: the resolver finds each name a load asks for, and
/// returns it spelled as the disk spells it.
/// </summary>
public static class CaseInsensitiveDataChecks
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private enum Outcome { Spelled, Missing, Misspelled, Unlistable }

    private sealed record Loaded(IReadOnlyList<string> Champions, string Release, string Air);

    [Check("data.case-insensitive-resolver")]
    public static void Resolver()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "Characters", "FiddleSticks", "Scripts");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "CharScriptFiddlesticks.luaobj");
            File.WriteAllText(file, "-- temporary");

            // The exact spelling is the fast path; another case of the same names comes back spelled as the disk spells them.
            TestAssert.Equal(file, CaseInsensitivePaths.Resolve(file));
            var asked = Path.Combine(root, "characters", "fiddlesticks", "SCRIPTS", "charscriptFIDDLESTICKS.LUAOBJ");
            TestAssert.Equal(file, CaseInsensitivePaths.Resolve(asked));

            // A name that is not there comes back unchanged, so the caller's own not-found error names what it asked for.
            var missing = Path.Combine(root, "Characters", "FiddleSticks", "Scripts", "CharScriptMissing.luaobj");
            TestAssert.Equal(missing, CaseInsensitivePaths.Resolve(missing));
            var absentFolder = Path.Combine(root, "Spells", "Disintegrate.inibin");
            TestAssert.Equal(absentFolder, CaseInsensitivePaths.Resolve(absentFolder));

            // Pattern listings match ignoring case, and each file comes back spelled as on disk.
            var listed = CaseInsensitivePaths.EnumerateFiles(Path.Combine(root, "CHARACTERS", "fiddlesticks", "scripts"), "charscript*.LUAOBJ").ToArray();
            TestAssert.Equal(1, listed.Length, "one CharScript in the folder");
            TestAssert.Equal(file, listed[0]);

            // A release root is looked up the same way, and the paths under it resolve from its own spelling.
            var paths = new GameDataPaths(root);
            TestAssert.Equal(file, paths.Under("characters", "fiddlesticks", "scripts", "charscriptFIDDLESTICKS.LUAOBJ"));

            // The observer sees each lookup as (asked, resolved). Other threads of this process may look up paths too, so only this name is judged.
            var seen = new ConcurrentQueue<(string Asked, string Resolved)>();
            CaseInsensitivePaths.Observer = (lookup, resolved) => seen.Enqueue((lookup, resolved));
            try
            {
                _ = CaseInsensitivePaths.Resolve(asked);
            }
            finally
            {
                CaseInsensitivePaths.Observer = null;
            }
            var ours = seen.Where(pair => pair.Asked == asked).ToArray();
            TestAssert.Equal(1, ours.Length, "one lookup of this name observed");
            TestAssert.Equal(file, ours[0].Resolved);

            // On a case-sensitive file system two names that differ only in case are both kept: each resolves to itself, and a
            // case-insensitive ask takes the first in ordinal order. On Windows the second file replaces the first, so this part is skipped.
            var dual = Path.Combine(root, "Dual");
            Directory.CreateDirectory(dual);
            File.WriteAllText(Path.Combine(dual, "Name.inibin"), "a");
            File.WriteAllText(Path.Combine(dual, "name.inibin"), "b");
            if (Directory.EnumerateFileSystemEntries(dual).Count() == 2)
            {
                TestAssert.Equal(Path.Combine(dual, "name.inibin"), CaseInsensitivePaths.Resolve(Path.Combine(dual, "name.inibin")));
                TestAssert.Equal(Path.Combine(dual, "Name.inibin"), CaseInsensitivePaths.Resolve(Path.Combine(dual, "NAME.inibin")));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("data.case-insensitive-loads-match-disk", Serial = true, TimeoutSeconds = 300)]
    public static void LoadsMatchDisk()
    {
        var requests = new ConcurrentDictionary<(string Asked, string Resolved), byte>();
        CaseInsensitivePaths.Observer = (asked, resolved) => requests.TryAdd((asked, resolved), 0);
        Loaded loaded;
        try
        {
            loaded = LoadShippedData();
        }
        finally
        {
            CaseInsensitivePaths.Observer = null;
        }
        Judge(requests.Keys.ToArray(), loaded);
    }

    /// <summary>Everything the server reads from the shipped data and the AIR client, for every map it plays.</summary>
    private static Loaded LoadShippedData()
    {
        var paths = GameDataPaths.Default;
        var data = new GameData(paths);
        var resolver = new BbScriptResolver(paths);
        var champions = resolver.DiscoverChampions();
        TestAssert.Equal(84, champions.Count, "the roster is the 84 champions of patch 126");
        BbClosure? first = null;
        foreach (var champion in champions)
        {
            _ = data.Champion(champion);
            _ = data.HasCharacterData(champion);
            var closure = resolver.Resolve(champion);
            first ??= closure;
            foreach (var name in closure.ScriptNames.Concat(closure.DataOnly))
            {
                if (data.HasSpell(name)) _ = data.Spell(name);
                _ = data.HasBuffData(name);
                _ = data.HasClientBuff(name);
                _ = data.SpellHasUncancellableWindup(name);
                _ = data.SpellChannelDuration(name);
                _ = data.SpellPersistsThroughDeath(name);
            }
        }
        foreach (var particle in Directory.EnumerateFiles(paths.Data("Particles"), "*.troy"))
            _ = data.HasClientParticle(Path.GetFileName(particle));

        // Spawned characters (every folder: the champions, the minions, monsters and helper units) and every shipped spell and item script.
        var shipped = new BbScriptSet(first ?? throw new InvalidOperationException("no champion closure"), paths);
        foreach (var folder in Directory.EnumerateDirectories(paths.Data("Characters")))
            _ = shipped.SpawnedCharacter(Path.GetFileName(folder));
        foreach (var file in Directory.EnumerateFiles(paths.Data("Spells"), "*.luaobj"))
            _ = shipped.FindOrShipped(Path.GetFileNameWithoutExtension(file));
        foreach (var file in Directory.EnumerateFiles(paths.Data("Items"), "*.luaobj"))
            _ = BbScriptLoader.Load(paths, "Items/" + Path.GetFileName(file));

        // The shared AI scripts, then each map: its layout, constants, item and progression rules, navigation grid, encounters, and scripts.
        var catalog = new AiScriptCatalog(paths);
        foreach (var file in Directory.EnumerateFiles(paths.Data("Scripts"), "*.luaobj"))
            _ = catalog.Load(Path.GetFileNameWithoutExtension(file));
        foreach (var mapId in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.Tutorial, GameMaps.TwistedTreeline, GameMaps.CrystalScar })
        {
            var map = paths.ForMap(mapId);
            _ = MapLayout.For(map);
            _ = MapConstants.Load(map, map.MapName);
            _ = MapItemRules.Load(map);
            _ = MapProgressionRules.Load(map);
            _ = NavigationGrid.Load(map);
            _ = EncounterCatalog.Load(map);
            _ = new GameData(map).RecallSpellName;
            _ = new AiScriptCatalog(map).Load("LevelScript");
            // The match-time snapshot runs the map's LevelScript: its neutral camps, experience and turrets.
            _ = MapScriptData.NeutralCampsFor(map);
            _ = MapScriptData.NeutralExperienceFor(map);
            _ = MapScriptData.TurretsFor(map);
            foreach (var file in Directory.EnumerateFiles(map.Under("LEVELS", map.MapName, "Scripts"), "*.luaobj"))
                _ = LuaObjectReader.Load(file);
        }
        var crystalScar = paths.ForMap(GameMaps.CrystalScar);
        _ = BehaviorTreeLoader.LoadMap(crystalScar);
        var locations = new NamedLocations(crystalScar);
        foreach (var line in File.ReadLines(crystalScar.Under("LEVELS", crystalScar.MapName, "Locations.dat")))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && !columns[0].StartsWith(';')) _ = locations.Locate(columns[0]);
        }

        // The AIR client: the champion catalog's rows, then the Platform's store images and its operator content folder.
        TestAssert.True(ChampionCatalog.All.Count > 0, "the champion catalog reads the AIR client");
        var swf = AirChampionData.ResolveSwf();
        _ = AirChampionData.Read(swf);
        var air = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(swf))) ?? string.Empty;
        _ = new PlatformStoreCatalog(new PlatformEconomyRules(), air).Items.Count;
        _ = PlatformStore.StoreImageRoot(air);
        _ = PlatformContent.Has("store/prices.json");
        _ = PlatformContent.Home;
        return new Loaded(champions, paths.Root, air);
    }

    /// <summary>Every lookup the loads made: a found file must be spelled on disk, a missing one must have no case-only twin, and the cases that differ are counted by class.</summary>
    private static void Judge((string Asked, string Resolved)[] requests, Loaded loaded)
    {
        var disk = new DiskSpelling();
        var failures = new List<string>();
        var moved = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var existing = 0;
        foreach (var (asked, resolved) in requests.OrderBy(pair => pair.Asked, StringComparer.Ordinal).ThenBy(pair => pair.Resolved, StringComparer.Ordinal))
        {
            var full = Path.GetFullPath(asked);
            if (Exists(resolved))
            {
                existing++;
                var (kind, onDisk) = disk.Check(resolved);
                if (kind != Outcome.Spelled)
                    failures.Add($"{Shown(resolved, loaded)}: returned, but the disk spells {kind} ({onDisk})");
                else if (!string.Equals(full, resolved, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{Shown(asked, loaded)}: resolved to a different file, {Shown(resolved, loaded)}");
                if (!string.Equals(full, resolved, StringComparison.Ordinal))
                    Add(moved, Classify(loaded, resolved), $"{Shown(asked, loaded)} -> {Shown(resolved, loaded)}");
                continue;
            }
            var missed = disk.Check(full);
            if (missed.Kind == Outcome.Misspelled)
                failures.Add($"{Shown(asked, loaded)}: not found, though the disk holds {missed.OnDisk} and the lookup did not resolve it");
        }

        // Every champion's record was looked up through the resolver, so the roster is covered by the disk spelling check.
        foreach (var champion in loaded.Champions)
        {
            var record = Path.Combine("Characters", champion, champion + ".inibin");
            if (!requests.Any(pair => pair.Asked.EndsWith(record, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"champion {champion}: its record was never looked up through the resolver");
        }

        var differing = moved.Values.Sum(lines => lines.Count);
        Console.WriteLine($"case-insensitive lookups: {requests.Length} distinct, {existing} found, {differing} asked for another case than the disk holds");
        foreach (var (cls, lines) in moved)
        {
            Console.WriteLine($"{cls}: {lines.Count}");
            foreach (var line in lines) Console.WriteLine($"  {line}");
        }
        TestAssert.True(differing > 0, "the shipped names differ in case from the code's spelling (batch 609)");
        TestAssert.True(moved.Values.Any(lines => lines.Any(line => line.Contains("CharScriptFiddlesticks.luaobj", StringComparison.Ordinal))),
            "FiddleSticks's CharScript is spelled Fiddlesticks on disk (batch 609)");
        if (failures.Count > 0)
            throw new InvalidOperationException($"{failures.Count} lookups did not match the disk's spelling:\n" + string.Join('\n', failures.Take(40)));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Add(SortedDictionary<string, List<string>> moved, string cls, string line)
    {
        if (!moved.TryGetValue(cls, out var lines)) moved[cls] = lines = [];
        lines.Add(line);
    }

    /// <summary>The path relative to the release root or the AIR client, with forward slashes; never the absolute folder of the install.</summary>
    private static string Shown(string path, Loaded loaded)
    {
        if (path.StartsWith(loaded.Release, StringComparison.OrdinalIgnoreCase))
            return "<release>/" + Relative(path[loaded.Release.Length..]);
        if (loaded.Air.Length > 0 && path.StartsWith(loaded.Air, StringComparison.OrdinalIgnoreCase))
            return "<air>/" + Relative(path[loaded.Air.Length..]);
        return "<other>/" + Path.GetFileName(path);
    }

    private static string Relative(string rest) => string.Join('/', rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The class of a shipped path, for the mismatch counts: what kind of file the code asked for.</summary>
    private static string Classify(Loaded loaded, string path)
    {
        if (loaded.Air.Length > 0 && path.StartsWith(loaded.Air, StringComparison.OrdinalIgnoreCase)) return "AIR client files";
        if (!path.StartsWith(loaded.Release, StringComparison.OrdinalIgnoreCase)) return "other files";
        var parts = path[loaded.Release.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("LEVELS", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2) return "LEVELS map folder";
            if (parts.Length == 3) return "LEVELS map file";
            return parts[2] switch
            {
                _ when parts[2].Equals("Scene", StringComparison.OrdinalIgnoreCase) => "LEVELS scene (SCB, CFG)",
                _ when parts[2].Equals("Scripts", StringComparison.OrdinalIgnoreCase) => "LEVELS scripts (luaobj, xml)",
                _ when parts[2].Equals("Encounters", StringComparison.OrdinalIgnoreCase) => "LEVELS encounters (json)",
                _ => "LEVELS other folder",
            };
        }
        if (parts.Length >= 2 && parts[0].Equals("DATA", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("Characters", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 3) return "character shared file";
                if (parts.Length == 4 && parts[3].Equals(parts[2] + ".inibin", StringComparison.OrdinalIgnoreCase))
                    return "champion record (<champion>/<champion>.inibin)";
                if (parts.Length > 4 && parts[3].Equals("Scripts", StringComparison.OrdinalIgnoreCase))
                    return parts[4].StartsWith("CharScript", StringComparison.OrdinalIgnoreCase)
                        ? "character CharScript (Scripts/CharScript*.luaobj)" : "character script (other)";
                return "character asset file";
            }
            return parts[1] switch
            {
                _ when parts[1].Equals("Spells", StringComparison.OrdinalIgnoreCase) => "spell file (inibin, luaobj)",
                _ when parts[1].Equals("Items", StringComparison.OrdinalIgnoreCase) => "item file (inibin, luaobj)",
                _ when parts[1].Equals("Buffs", StringComparison.OrdinalIgnoreCase) => "buff file",
                _ when parts[1].Equals("Particles", StringComparison.OrdinalIgnoreCase) => "particle file",
                _ when parts[1].Equals("Scripts", StringComparison.OrdinalIgnoreCase) => "shared AI script (Scripts/*.luaobj)",
                _ => "DATA " + parts[1] + " file",
            };
        }
        return "release other";
    }

    /// <summary>
    /// The disk's own spelling, read directly from the folder listings and independent of the resolver: every segment of a path must be
    /// listed exactly, or the first segment that is not listed tells how the path differs from the disk.
    /// </summary>
    private sealed class DiskSpelling
    {
        private readonly Dictionary<string, HashSet<string>?> _folders = new(StringComparer.Ordinal);

        public (Outcome Kind, string OnDisk) Check(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var folder = root;
            foreach (var segment in full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var child = Path.Combine(folder, segment);
                var names = Names(folder);
                if (names is null) return (Exists(child) ? Outcome.Unlistable : Outcome.Missing, segment);
                if (names.Contains(segment))
                {
                    folder = child;
                    continue;
                }
                var other = names.FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase));
                return other is null ? (Outcome.Missing, segment) : (Outcome.Misspelled, other);
            }
            return (Outcome.Spelled, full);
        }

        private HashSet<string>? Names(string folder)
        {
            if (_folders.TryGetValue(folder, out var names)) return names;
            try
            {
                names = Directory.EnumerateFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)).OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                names = null;
            }
            _folders[folder] = names;
            return names;
        }
    }
}
