using System.Security.Cryptography;

// Linked as source into the Platform as well (the host's optional launcher folder); BCL types only, no other companion type.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// NOT RETAIL: retail's update service installed and verified the 2011 launcher itself; the classic start instead accepts the player's own copy
/// of launcher 1.0.0.251 (release 0.0.0.49, the one live with patch 1.0.0.126), checked by its EXE's fingerprint and the release's file list,
/// in the flat folder shape of that release.
/// </summary>
internal static class ClassicLauncherFiles
{
    /// <summary>SHA-256 of the 1.0.0.251 <c>LoLLauncher.exe</c>.</summary>
    internal const string LauncherSha256 = "47ef899335cad6e91d862919276101bf7ded54f673cc7bcb6b9381d4ba32c264";

    /// <summary>The release's 22 files, relative to the launcher folder.</summary>
    internal static IReadOnlyList<string> Required { get; } =
    [
        "LoLLauncher.exe", "LoLLauncher.exe.version", "launcher.maestro.dll", "LoLLauncher.swf", "RADS.swf", "slideshow.swf", "Riot_SafetyCheck.exe",
        "en_US.tra", "ru_RU.tra", "RU_en_US.tra", "RU_ru_RU.tra", "RU_TEST_en_US.tra", "RU_TEST_ru_RU.tra", "eula_RU_ru_ru.txt", "eula_RU_TEST_ru_ru.txt",
        @"xml\slideshow.xml", @"img\ad0.png", @"img\ad1.png", @"img\ad2.png", @"img\ad3.png", @"img\ad4.png", @"img\ad5.png",
    ];

    /// <summary>The release files whose names name the game or its publisher, by their role: the sentence below is player-facing text (owner, 3 Nov).</summary>
    private static readonly Dictionary<string, string> Neutral = new()
    {
        ["LoLLauncher.exe"] = "the launcher's program file",
        ["LoLLauncher.exe.version"] = "the launcher's version file",
        ["LoLLauncher.swf"] = "the launcher's main screen file",
        ["Riot_SafetyCheck.exe"] = "the launcher's safety-check program",
    };

    /// <summary>
    /// Why <paramref name="folder"/> is not the accepted launcher (a missing file, then the EXE's fingerprint), or null when it is. One short
    /// sentence that never names the game or its publisher; never throws, since the classic start falls back on any problem.
    /// </summary>
    /// <param name="sha256">DIAGNOSTICS: test seam: the accepted fingerprint; production always checks <see cref="LauncherSha256"/> (checks pass their fixtures' own).</param>
    internal static string? Problem(string folder, string sha256 = LauncherSha256)
    {
        if (Required.FirstOrDefault(file => !File.Exists(Path.Combine(folder, file))) is { } missing)
            return $"The classic launcher folder is missing {Neutral.GetValueOrDefault(missing, missing)}.";
        try
        {
            using var exe = File.OpenRead(Path.Combine(folder, "LoLLauncher.exe"));
            return Convert.ToHexString(SHA256.HashData(exe)).Equals(sha256, StringComparison.OrdinalIgnoreCase) ? null : "The classic launcher is not version 1.0.0.251.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "The classic launcher's program file could not be read.";   // locked or unreadable
        }
    }

    /// <summary>The release's <c>releasemanifest</c> in <paramref name="folder"/> or its parent (the download's shape), else null.</summary>
    internal static string? ManifestFor(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return new[] { folder, Path.GetDirectoryName(folder) }.OfType<string>().Select(d => Path.Combine(d, "releasemanifest")).FirstOrDefault(File.Exists);
    }
}
