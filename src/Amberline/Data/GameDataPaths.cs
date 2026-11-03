using System;
using System.Collections.Generic;
using System.IO;
using Amberline.Core;

namespace Amberline.Data;

/// <summary>Locates the shipped 1.0.0.126 game data without depending on the Python runtime.</summary>
public sealed class GameDataPaths
{
    public const string ReleaseRelativePath = "game-data";

    public string Root { get; }

    /// <summary>The LEVELS folder the map files are read from: "Map1" by default, "Map2" for the Harrowing rift (<see cref="ForMap"/>).</summary>
    public string MapName { get; }

    /// <summary>The map id of <see cref="MapName"/> ("Map4" is 4), the inverse of <see cref="MapFolder"/>; 0 for a folder not named Map&lt;n&gt;.</summary>
    public int MapId => MapName.StartsWith("Map", StringComparison.Ordinal) && int.TryParse(MapName.AsSpan(3), out var id) ? id : 0;

    public GameDataPaths(string? root = null, string mapName = "Map1")
    {
        Root = CaseInsensitivePaths.Resolve(Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? ResolveRoot() : root));
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        MapName = mapName;
    }

    /// <summary>The same release, reading the map files of <paramref name="mapId"/> (<see cref="GameMaps"/>).</summary>
    public GameDataPaths ForMap(int mapId) => new(Root, MapFolder(mapId));

    /// <summary>The LEVELS folder of a map id: Map1 for Summoner's Rift, Map2 for the Harrowing rift, Map3 for the Basic Tutorial, Map4 for Twisted Treeline, Map8 for The Crystal Scar.</summary>
    public static string MapFolder(int mapId) => mapId switch
    {
        GameMaps.Normal => "Map1",
        GameMaps.Harrowing => "Map2",
        GameMaps.Tutorial => "Map3",
        GameMaps.TwistedTreeline => "Map4",
        GameMaps.CrystalScar => "Map8",
        _ => throw new ArgumentOutOfRangeException(nameof(mapId), $"Map id must be {GameMaps.SupportedText}."),
    };

    public static GameDataPaths Default => new();

    /// <summary>
    /// A path under the release's DATA. Retail's RADS deploy composed the language project (<c>lol_game_client_en_us</c>, whose DATA
    /// holds <c>Menu\fontconfig_en_US.txt</c> and the voice banks) into the game's folder. A download whose client never started has
    /// those files only in that project (the companion deploys them before the client starts, GameLanguagePack), so a path missing
    /// from the release is read from the project's newest release, as the deployed folder would have it (batch 655).
    /// </summary>
    public string Data(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var path = CaseInsensitivePaths.Resolve(Path.Combine(Root, "DATA", relative));
        if (Path.Exists(path) || LanguageRelease() is not { } language) return path;
        var deployed = CaseInsensitivePaths.Resolve(Path.Combine(language, "DATA", relative));
        return Path.Exists(deployed) ? deployed : path;
    }

    private string? _language;

    /// <summary>
    /// The newest release of the en_US language project beside the game, found as the companion finds it (GameLanguagePack's
    /// FindNewestRelease): in an ancestor of the release, or in its <c>projects</c> folder. The server's texts are the en_US ones.
    /// </summary>
    private string? LanguageRelease()
    {
        if (_language is not null) return _language.Length == 0 ? null : _language;
        var found = "";
        for (var directory = new DirectoryInfo(Root); directory is not null && found.Length == 0; directory = directory.Parent)
            foreach (var releases in new[] { Path.Combine(directory.FullName, LanguageProject, "releases"), Path.Combine(directory.FullName, "projects", LanguageProject, "releases") })
                if (Directory.Exists(releases) && Directory.EnumerateDirectories(releases)
                        .Where(release => Directory.Exists(Path.Combine(release, "DATA")))
                        .MaxBy(release => Version.TryParse(Path.GetFileName(release), out var version) ? version : new Version()) is { } newest)
                {
                    found = newest;
                    break;
                }
        return (_language = found).Length == 0 ? null : found;
    }

    private const string LanguageProject = "lol_game_client_en_us";

    /// <summary>A path under the release root (<c>LEVELS/&lt;map&gt;/...</c> and the rest), spelled as the disk spells it.</summary>
    public string Under(params string[] segments) =>
        CaseInsensitivePaths.Resolve(Path.Combine(new[] { Root }.Concat(segments).ToArray()));

    public static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("GAME_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        // Keep the deterministic default in the error path so a missing install
        // is reported as a useful file-not-found error by the individual reader.
        var fallback = Path.Combine(Directory.GetCurrentDirectory(), ReleaseRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFullPath(fallback);
    }

    private static IEnumerable<DirectoryInfo> Starts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var full = Path.GetFullPath(value);
            var directory = new DirectoryInfo(full);
            if (seen.Add(directory.FullName))
                yield return directory;
        }
    }
}
