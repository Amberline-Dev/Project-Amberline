using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Linked as source into the Platform as well (see RetailClients.cs): its setup page warns when the language folder is not found.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>What <see cref="GameLanguagePack.Ensure"/> did, as one log line without paths.</summary>
public sealed record LanguagePackResult(string Locale, string Status, int Linked = 0, int Copied = 0, int Present = 0, int Kept = 0, int Failed = 0)
{
    public override string ToString() => Status == "deployed"
        ? string.Create(CultureInfo.InvariantCulture,
            $"language-pack {Locale}: linked={Linked} copied={Copied} present={Present} kept={Kept} failed={Failed}")
        : $"language-pack {Locale}: {Status}";
}

/// <summary>
/// Deploys the game's language pack the way the retail RADS solution did. Retail ships it as a sibling project
/// (<c>projects\lol_game_client_&lt;locale&gt;\releases\&lt;newest&gt;\DATA</c>: <c>Menu\fontconfig_&lt;locale&gt;.txt</c>,
/// <c>Sounds\FMOD\LoL_Audio_*.fev</c> and the <c>VOBank_*.fsb</c> voice bank) and its deploy step composed the
/// project trees into one folder. The companion starts the base release directly, so without this step the
/// client has no voice-over (its audio bank load fails, every announcer and champion line is silent) and
/// loads no UI text unless the fontconfig was copied by hand.
/// Only files that are missing are created, as hard links when the volume allows (no disk cost) and as a
/// verified copy otherwise. A file that already exists and differs is kept as it is. The stock executable and
/// every existing file are never written.
/// </summary>
public static class GameLanguagePack
{
    public const string DefaultLocale = "en_US";

    /// <summary>Only files above this size are hard-linked (the voice bank is 160 MiB); smaller ones are copied.</summary>
    private const long LinkMinimumBytes = 4L * 1024 * 1024;

    /// <summary>Creates <paramref name="linkPath"/> as a hard link of <paramref name="existingPath"/>; false when the volume cannot.</summary>
    public delegate bool LinkFile(string linkPath, string existingPath);

    private static readonly Regex LocaleName = new("^[a-z]{2}_[A-Z]{2}$", RegexOptions.CultureInvariant);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string linkPath, string existingPath, nint securityAttributes);

    private static bool NativeLink(string linkPath, string existingPath) =>
        OperatingSystem.IsWindows() && CreateHardLink(linkPath, existingPath, 0);

    /// <summary>
    /// The language the client will load: <c>LanguageLocaleRegion</c> from <c>DATA\CFG\Locale.cfg</c>, then
    /// <c>game.cfg</c>, else en_US (the stock client never receives a locale
    /// from AIR or on its command line, so AIR's own locale is not consulted).
    /// Reconstruction: the key name is confirmed for game.cfg (it sits in the client's option table and in the shipped
    /// defaults); Locale.cfg is read by the same wrapper, and no shipped Locale.cfg exists to confirm its key.
    /// </summary>
    public static string ResolveLocale(string gameDirectory)
    {
        foreach (var name in new[] { "Locale.cfg", "game.cfg" })
        {
            var path = Path.Combine(gameDirectory, "DATA", "CFG", name);
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    var separator = line.IndexOf('=');
                    if (separator > 0 && line[..separator].Trim().Equals("LanguageLocaleRegion", StringComparison.OrdinalIgnoreCase) &&
                        line[(separator + 1)..].Trim() is { Length: > 0 } value)
                        return value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return DefaultLocale;
    }

    /// <summary>
    /// NOT RETAIL: retail's game used the language its launcher wrote into <c>DATA\CFG\defaults\locale.cfg</c>; the usual start has no language
    /// choice, so it puts <see cref="DefaultLocale"/> back. Written as the launcher writes it: a <c>[General]</c> section with that one line.
    /// Only where the file exists: true when it was written, and a missing file is never created.
    /// </summary>
    public static bool ResetLocaleCfg(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "DATA", "CFG", "defaults", "locale.cfg");
        if (!File.Exists(path)) return false;
        File.WriteAllText(path, $"[General]\r\nLanguageLocaleRegion={DefaultLocale}\r\n");
        return true;
    }

    public static LanguagePackResult Ensure(string gameDirectory, string locale, LinkFile? link = null)
    {
        link ??= NativeLink;
        if (!LocaleName.IsMatch(locale)) return new(locale, "skipped (unsupported locale name)");
        var release = FindNewestRelease(gameDirectory, locale);
        if (release is null)
            return new(locale, $"skipped (project lol_game_client_{locale.ToLowerInvariant()} not found beside the game; " +
                "voice-over and UI text need that folder next to lol_game_client)");

        int linked = 0, copied = 0, present = 0, kept = 0, failed = 0;
        var source = Path.Combine(release, "DATA");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(gameDirectory, "DATA", relative);
            try
            {
                switch (Place(file, destination, link))
                {
                    case Placement.Linked: linked++; break;
                    case Placement.Copied: copied++; break;
                    case Placement.Present: present++; break;
                    case Placement.Kept: kept++; break;
                }
                // The client probes DATA\Sounds\FMOD\<locale>\ first and falls back to DATA\Sounds\FMOD\,
                // which is where the project tree puts the files. The localized
                // folder is mirrored only where a hard link costs nothing, as the working host layout has it.
                if (string.Equals(Path.GetDirectoryName(relative), Path.Combine("Sounds", "FMOD"), StringComparison.OrdinalIgnoreCase))
                {
                    var mirror = Path.Combine(gameDirectory, "DATA", "Sounds", "FMOD", locale, Path.GetFileName(relative));
                    if (File.Exists(mirror)) present++;
                    else if (TryLink(destination, mirror, link)) linked++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        return new(locale, "deployed", linked, copied, present, kept, failed);
    }

    private enum Placement { Linked, Copied, Present, Kept }

    private static Placement Place(string source, string destination, LinkFile link)
    {
        if (File.Exists(destination)) return SameFile(source, destination) ? Placement.Present : Placement.Kept;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // NOT RETAIL: retail's deploy hard-links every file. Small files (the fontconfig text, which this project
        // edits for its tooltip line) are copied so an in-place edit of one copy can never change the sibling project.
        if (new FileInfo(source).Length > LinkMinimumBytes && TryLink(source, destination, link)) return Placement.Linked;

        // Small file, different volume or a file system without hard links: a verified copy, moved into place whole.
        var temporary = destination + ".langpack-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            if (!HashOf(source).SequenceEqual(HashOf(temporary))) throw new IOException("The copied language file does not match its source.");
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Placement.Copied;
    }

    private static bool TryLink(string source, string destination, LinkFile link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!link(destination, source)) return false;
        // A link that did not come out as the same bytes is removed and reported as unavailable.
        if (new FileInfo(destination).Length == new FileInfo(source).Length) return true;
        File.Delete(destination);
        return false;
    }

    /// <summary>Same length and either the same write time (a hard link or a preserved copy) or the same bytes.</summary>
    private static bool SameFile(string first, string second)
    {
        var a = new FileInfo(first);
        var b = new FileInfo(second);
        if (a.Length != b.Length) return false;
        return a.LastWriteTimeUtc == b.LastWriteTimeUtc || HashOf(first).SequenceEqual(HashOf(second));
    }

    private static byte[] HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    /// <summary>The newest release of the locale project beside the game, found from any ancestor of the game folder.</summary>
    internal static string? FindNewestRelease(string gameDirectory, string locale)
    {
        var project = "lol_game_client_" + locale.ToLowerInvariant();
        var directory = new DirectoryInfo(Path.GetFullPath(gameDirectory));
        for (var depth = 0; directory is not null && depth < 8; directory = directory.Parent, depth++)
        {
            foreach (var root in new[] { directory.FullName, Path.Combine(directory.FullName, "projects") })
            {
                var releases = Path.Combine(root, project, "releases");
                if (!Directory.Exists(releases)) continue;
                var newest = Directory.EnumerateDirectories(releases)
                    .Where(path => Directory.Exists(Path.Combine(path, "DATA")))
                    .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                    .Where(entry => entry.Version is not null)
                    .OrderByDescending(entry => entry.Version)
                    .FirstOrDefault();
                if (newest.Path is not null) return newest.Path;
            }
        }
        return null;
    }
}
