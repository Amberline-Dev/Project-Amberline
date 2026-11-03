using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Linked as source into the Platform as well (see RetailClients.cs).
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// One log line about where the picked game lives. The stock client reads its own folder through ANSI Win32
/// calls into fixed 260-character buffers (<c>GetCurrentDirectoryA</c>, <c>GetModuleFileNameA</c> and the
/// <c>r3dlog</c> name appended to the folder), so the folder is the one
/// thing a friend's machine changes that the host never sees. A friend whose client dies during load can then
/// paste this line instead of describing the setup. No path or user name is ever written.
/// </summary>
internal static class GameFolderReport
{
    /// <summary>Longest path under the shipped 0.0.0.51 tree, relative to the executable folder (measured).</summary>
    internal const int DeepestShippedAsset = 94;

    private const int MaxPath = 260;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder? shortPath, uint length);

    /// <summary>
    /// Whether the game's volume gives long file names a short 8.3 alias: <c>on</c>, <c>off</c> or <c>unknown</c>. The stock client needs them when it
    /// runs on its loose files: it builds its spell-name index with <c>FindFirstFile("Data\Spells\*.ini")</c>, which matches the <c>.inibin</c> files only
    /// through their short names (<c>ANNIEB~1.INI</c>); a volume without them gives an empty index, the loading screen finds no summoner-spell icon and the
    /// client stops at hero creation with "Avatar spell not found!". Windows 8 and later create short names on the system volume only by default
    /// (<c>fsutil behavior query disable8dot3</c> = 2, "per volume"). The managed RADS layout reads the index from its archive instead and does not need them.
    /// Evidence: docs/maintenance/2026-10-18-w15-rads-delivery.md.
    /// </summary>
    internal static string ShortNames(string gameDirectory)
    {
        if (!OperatingSystem.IsWindows()) return "unknown";
        try
        {
            // A file whose long name has no 8.3 form of its own (name over eight characters, extension over three), so a short alias always differs from it.
            var sample = Directory.EnumerateFiles(Path.Combine(gameDirectory, "DATA", "Spells"), "*.inibin")
                .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file).Length > 8);
            if (sample is null) return "unknown";
            var alias = new StringBuilder(1024);
            var length = GetShortPathName(sample, alias, (uint)alias.Capacity);
            if (length == 0 || length >= alias.Capacity) return "unknown";
            return string.Equals(Path.GetFileName(alias.ToString()), Path.GetFileName(sample), StringComparison.OrdinalIgnoreCase) ? "off" : "on";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return "unknown"; }
    }

    /// <summary>Problems visible from the folder string alone. Empty means none.</summary>
    internal static IReadOnlyList<string> Hazards(string gameDirectory, Encoding ansi)
    {
        var hazards = new List<string>();
        // Full path of the deepest asset must fit MAX_PATH (259 characters plus the terminator).
        if (gameDirectory.TrimEnd('\\').Length + 1 + DeepestShippedAsset >= MaxPath) hazards.Add("path-too-long");
        if (gameDirectory.StartsWith(@"\\", StringComparison.Ordinal)) hazards.Add("network-share-path");
        try { ansi.GetBytes(gameDirectory); }
        catch (EncoderFallbackException) { hazards.Add("not-ansi-representable"); }
        return hazards;
    }

    /// <summary>The system ANSI code page (what the stock client's ...A calls use), failing on unmappable characters.</summary>
    internal static Encoding SystemAnsi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetACP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception exception) when (exception is ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        { return Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
    }

    internal static string Describe(string gameDirectory)
    {
        var drive = "unknown";
        try
        {
            var info = new DriveInfo(Path.GetPathRoot(gameDirectory)!);
            drive = info.DriveType + "/" + info.DriveFormat;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { /* UNC roots have no DriveInfo; the hazard list names them. */ }
        var hazards = Hazards(gameDirectory, SystemAnsi());
        return string.Create(CultureInfo.InvariantCulture,
            $"game.folder drive={drive} pathChars={gameDirectory.Length} hazards={(hazards.Count == 0 ? "none" : string.Join(',', hazards))} shortnames={ShortNames(gameDirectory)}");
    }
}
