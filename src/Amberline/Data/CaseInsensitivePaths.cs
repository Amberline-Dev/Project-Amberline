using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Amberline.Data;

/// <summary>
/// The one lookup of the shipped game files and the AIR client files. The data was named for Windows, where file names ignore case, and
/// the code builds names from data (a champion folder, a CharScript name) that are not always spelled as the disk spells them:
/// <c>Characters/FiddleSticks/Scripts/CharScriptFiddleSticks.luaobj</c> is <c>CharScriptFiddlesticks.luaobj</c> on disk. A Linux file system
/// does not ignore case, so <see cref="Resolve"/> returns the path spelled as the disk spells it, and both systems open the same files.
/// </summary>
public static class CaseInsensitivePaths
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    // ponytail: listings are cached for the life of the process. The data root and the AIR client are read-only installs, so a cached
    // folder does not change under a running server. Ceiling: a file added to a folder after its first lookup is seen after a restart;
    // move to a change-notified cache if the Platform ever writes into a folder it reads.
    private static readonly ConcurrentDictionary<string, Listing?> Listings = new(StringComparer.Ordinal);

    /// <summary>
    /// DIAGNOSTICS: a test seam that sees every <see cref="Resolve"/> call as (the path asked for, the path returned). Production leaves it
    /// null; the proof check <c>data.case-insensitive-loads-match-disk</c> reads it.
    /// </summary>
    public static Action<string, string>? Observer { get; set; }

    /// <summary>
    /// The path spelled as the disk spells it. Each segment is matched exactly first, then ignoring case, against the cached listing of its
    /// folder, so a folder holding two names that differ only in case resolves each to itself. A path that matches nothing comes back
    /// unchanged, so the caller's own not-found error names what it asked for.
    /// </summary>
    public static string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var segment in full.Substring(root.Length).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Child(current, segment);
            if (name is null) return Observed(path, path);
            current = Path.Combine(current, name);
        }
        return Observed(path, current);
    }

    /// <summary>
    /// The files of a folder whose names match <paramref name="pattern"/> (<c>*</c> and <c>?</c>), ignoring case as the Windows listing the
    /// shipped code was written against does (<c>*.dds</c> finds <c>AkaliLoadScreen_1.DDS</c>). The folder is found as <see cref="Resolve"/>
    /// finds it, and each file comes back spelled as on disk.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string folder, string pattern) =>
        Directory.EnumerateFiles(Resolve(folder))
            .Where(file => FileSystemName.MatchesWin32Expression(pattern, Path.GetFileName(file.AsSpan()), ignoreCase: true));

    private static string Observed(string asked, string resolved)
    {
        Observer?.Invoke(asked, resolved);
        return resolved;
    }

    private static string? Child(string folder, string segment)
    {
        var listing = Listings.GetOrAdd(folder, Read);
        if (listing is null) return Exists(Path.Combine(folder, segment)) ? segment : null;
        if (listing.Exact.Contains(segment)) return segment;
        return listing.Folded.GetValueOrDefault(segment);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static Listing? Read(string folder)
    {
        string[] names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)).OfType<string>()
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        // Names are sorted, so when two differ only in case the first (ordinal) spelling is the one a case-insensitive match returns.
        var folded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) folded.TryAdd(name, name);
        return new Listing(new HashSet<string>(names, StringComparer.Ordinal), folded);
    }

    private sealed record Listing(HashSet<string> Exact, Dictionary<string, string> Folded);
}
