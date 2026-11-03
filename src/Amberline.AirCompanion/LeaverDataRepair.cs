using System.Diagnostics;
using System.Security.Cryptography;
using RtmpSharp.IO;

namespace Amberline.AirCompanion;

public enum LeaverDataRepairStatus
{
    Repaired,
    AlreadyClean,
    Skipped,
}

public sealed record LeaverDataRepairOutcome(
    LeaverDataRepairStatus Status,
    string Reason,
    string? BackupPath = null,
    string? OriginalSha256 = null,
    string? RepairedSha256 = null);

/// <summary>
/// Removes only the stock AIR duplicate of GlobalPrefs.leaverData.
///
/// The stock client registers GlobalPrefs and UserLocaleProperties, but not
/// LeaverData.  FileStream therefore writes the latter as an anonymous AMF3
/// object on every save.  The typed locale object is the durable copy.
/// </summary>
public static class LeaverDataRepair
{
    public const string GlobalPreferencesRelativePath = "preferences/global/global.properties";
    public const string GlobalPrefsAlias = ">com.riotgames.platform.gameclient.domain.GlobalPrefs";
    public const string UserLocalePropertiesAlias = ">com.riotgames.platform.gameclient.domain.UserLocaleProperties";
    public const int MaxPreferenceBytes = 64 * 1024;
    private const string LeaverDataKey = "leaverData";
    private const string LocaleDataKey = "localeData";

    private static readonly string[] LeaverDataKeys =
    {
        "gameTime", "summonerName", "gameId", "gameInProgress"
    };

    private static readonly string[] LocaleDataKeys =
    {
        "languageSaved", "localId", "summonerName", "timeZone"
    };

    public static string GlobalPreferencesPath(string airRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(airRoot);
        return Path.Combine(Path.GetFullPath(airRoot), GlobalPreferencesRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Takes an exclusive lease for one staged AIR copy.  The caller must hold
    /// the returned stream until the AIR process exits.
    /// </summary>
    public static FileStream AcquireAirCopyLease(string airRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(airRoot);
        var root = Path.GetFullPath(airRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The AIR copy directory was not found: {root}");

        var path = Path.Combine(root, ".air-companion-leaverdata.lock");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, options: FileOptions.WriteThrough);
    }

    /// <summary>
    /// Lists the running processes whose actual executable path is the staged copy.  This covers
    /// both ADL-launched and direct LolClient-launched staged copies without
    /// relying on the command line or a friendly process name.  Empty when none runs.
    /// </summary>
    public static IReadOnlyCollection<int> RunningSameAirCopyProcessIds(ProcessStartInfo airStart)
    {
        ArgumentNullException.ThrowIfNull(airStart);
        if (string.IsNullOrWhiteSpace(airStart.FileName)) return [];

        string target;
        try
        {
            target = Path.IsPathRooted(airStart.FileName)
                ? Path.GetFullPath(airStart.FileName)
                : Path.GetFullPath(Path.Combine(
                    string.IsNullOrWhiteSpace(airStart.WorkingDirectory)
                        ? Environment.CurrentDirectory
                        : airStart.WorkingDirectory,
                    airStart.FileName));
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The AIR executable path could not be inspected; refusing to edit preferences.");
        }

        var processName = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrEmpty(processName)) return [];

        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch (Exception)
        {
            throw new InvalidOperationException("Running AIR processes could not be inspected; refusing to edit preferences.");
        }

        var ids = new List<int>();
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var actual = process.MainModule?.FileName
                        ?? throw new InvalidOperationException("The running executable path is unavailable.");
                    if (string.Equals(
                            Path.GetFullPath(actual), target, StringComparison.OrdinalIgnoreCase))
                        ids.Add(process.Id);
                }
                catch (Exception)
                {
                    throw new InvalidOperationException(
                        "A running AIR process could not be inspected; refusing to edit preferences.");
                }
            }
        }

        return ids;
    }

    public static LeaverDataRepairOutcome RepairGlobalPreferences(string airRoot)
    {
        var path = GlobalPreferencesPath(airRoot);
        if (!File.Exists(path)) return Skipped("missing");

        try
        {
            if (new FileInfo(path).Length > MaxPreferenceBytes)
                return Skipped("size-limit");

            var source = File.ReadAllBytes(path);
            if (source.Length > MaxPreferenceBytes)
                return Skipped("size-limit");

            if (!TryBuildRepair(source, out var repaired, out var reason))
                return Skipped(reason);

            if (source.AsSpan().SequenceEqual(repaired))
                return new LeaverDataRepairOutcome(
                    LeaverDataRepairStatus.AlreadyClean,
                    "already-clean",
                    OriginalSha256: Sha256(source),
                    RepairedSha256: Sha256(repaired));

            var originalHash = Sha256(source);
            var repairedHash = Sha256(repaired);
            var backup = path + ".leaverdata-" + Guid.NewGuid().ToString("N") + ".bak";
            var temporary = path + ".leaverdata-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 4096, options: FileOptions.WriteThrough))
                {
                    output.Write(repaired, 0, repaired.Length);
                    output.Flush(flushToDisk: true);
                }

                // Do not replace a preference file that changed after the
                // initial read (for example, a stock client save or a manual
                // operator edit).
                byte[] current;
                try { current = File.ReadAllBytes(path); }
                catch (Exception) { return Skipped("changed-during-repair"); }
                if (!source.AsSpan().SequenceEqual(current))
                    return Skipped("changed-during-repair");

                // File.Replace is deliberately the only commit operation.  A
                // copy/delete fallback could lose the original preference file.
                File.Replace(temporary, path, backup, ignoreMetadataErrors: false);
                temporary = string.Empty;
                return new LeaverDataRepairOutcome(
                    LeaverDataRepairStatus.Repaired, "duplicate-removed", backup, originalHash, repairedHash);
            }
            catch (Exception)
            {
                return Skipped("atomic-replace-failed");
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception)
        {
            // Malformed AMF and inaccessible files must leave the stock file
            // untouched and must not prevent the AIR client from launching.
            return Skipped("unreadable");
        }
    }

    /// <summary>
    /// Testable, side-effect-free part of the sanitizer.  It accepts exactly
    /// the stock duplicate shape and returns a complete AMF3 replacement.
    /// </summary>
    public static bool TryBuildRepair(byte[] source, out byte[] repaired, out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        repaired = Array.Empty<byte>();
        reason = "unknown-format";
        if (source.Length > MaxPreferenceBytes)
        {
            reason = "size-limit";
            return false;
        }

        if (!TryReadRoot(source, out var root, out reason)) return false;
        if (!string.Equals(root.TypeName, GlobalPrefsAlias, StringComparison.Ordinal))
        {
            reason = "root-alias";
            return false;
        }

        if (!root.TryGetValue(LocaleDataKey, out var localeValue) || localeValue is not AsObject locale)
        {
            reason = "duplicate-fields-missing";
            return false;
        }

        if (!string.Equals(locale.TypeName, UserLocalePropertiesAlias, StringComparison.Ordinal) ||
            !HasExactKeys(locale, LocaleDataKeys))
        {
            reason = "locale-schema";
            return false;
        }
        if (!TryValidateRoundtrippableFields(root, locale, out reason))
            return false;

        if (!root.TryGetValue(LeaverDataKey, out var leaverValue))
        {
            repaired = source.ToArray();
            reason = "already-clean";
            return true;
        }
        if (leaverValue is not AsObject leaver)
        {
            reason = "leaver-schema";
            return false;
        }

        if (!string.IsNullOrEmpty(leaver.TypeName) || !HasExactKeys(leaver, LeaverDataKeys))
        {
            reason = "leaver-schema";
            return false;
        }
        if (!ValuesMatch(leaver, locale))
        {
            reason = "duplicate-mismatch";
            return false;
        }

        if (!root.Remove(LeaverDataKey))
        {
            reason = "duplicate-fields-missing";
            return false;
        }

        try
        {
            using var stream = new MemoryStream();
            using (var writer = new AmfWriter(stream, new SerializationContext()))
                writer.WriteAmf3Item(root);
            repaired = stream.ToArray();
            if (repaired.Length > MaxPreferenceBytes)
            {
                repaired = Array.Empty<byte>();
                reason = "size-limit";
                return false;
            }

            if (!TryReadRoot(source, out var originalForCompare, out var originalReason))
            {
                repaired = Array.Empty<byte>();
                reason = originalReason;
                return false;
            }
            if (!TryReadRoot(repaired, out var repairedRoot, out var repairedReason))
            {
                repaired = Array.Empty<byte>();
                reason = repairedReason;
                return false;
            }
            if (!EquivalentWithoutLeaver(originalForCompare, repairedRoot))
            {
                repaired = Array.Empty<byte>();
                reason = "roundtrip-mismatch";
                return false;
            }

            reason = "duplicate-removed";
            return true;
        }
        catch (Exception)
        {
            repaired = Array.Empty<byte>();
            reason = "serialization-failed";
            return false;
        }
    }

    private static bool TryReadRoot(byte[] bytes, out AsObject root, out string reason)
    {
        root = null!;
        reason = "unknown-format";
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new AmfReader(stream, new SerializationContext());
            if (reader.ReadAmf3Item() is not AsObject parsed)
            {
                reason = "root-type";
                return false;
            }
            if (reader.DataAvailable)
            {
                reason = "trailing-data";
                return false;
            }
            root = parsed;
            return true;
        }
        catch (Exception)
        {
            reason = "malformed-amf";
            return false;
        }
    }

    private static bool HasExactKeys(AsObject value, IReadOnlyList<string> expected)
    {
        if (value.Count != expected.Count) return false;
        foreach (var key in expected)
            if (!value.ContainsKey(key)) return false;
        return true;
    }

    private static bool TryValidateRoundtrippableFields(AsObject root, AsObject locale, out string reason)
    {
        // Keep unrelated root fields only when RtmpSharp can round-trip them
        // without changing their meaning.  The stock file contains scalar
        // fields plus the typed locale object.  Unknown arrays/objects/dates
        // are an intentional no-op rather than a lossy rewrite.
        foreach (var entry in root)
        {
            if (entry.Key == LeaverDataKey || entry.Key == LocaleDataKey) continue;
            if (!IsPrimitive(entry.Value))
            {
                reason = "unsupported-root-field";
                return false;
            }
        }
        foreach (var entry in locale)
        {
            if (!IsPrimitive(entry.Value))
            {
                reason = "unsupported-locale-field";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private static bool ValuesMatch(AsObject leaver, AsObject locale)
    {
        return leaver.TryGetValue("gameInProgress", out var gameInProgress) &&
               locale.TryGetValue("languageSaved", out var languageSaved) &&
               StrictBoolEqual(gameInProgress, languageSaved) &&
               leaver.TryGetValue("summonerName", out var leaverName) &&
               locale.TryGetValue("summonerName", out var localeName) &&
               leaverName is string leaverString && localeName is string localeString &&
               string.Equals(leaverString, localeString, StringComparison.Ordinal) &&
               leaver.TryGetValue("gameTime", out var gameTime) &&
               locale.TryGetValue("timeZone", out var timeZone) &&
               StrictNumberEqual(gameTime, timeZone) &&
               leaver.TryGetValue("gameId", out var gameId) &&
               locale.TryGetValue("localId", out var localId) &&
               StrictNumberEqual(gameId, localId);
    }

    private static bool StrictBoolEqual(object? left, object? right) =>
        left is bool leftBool && right is bool rightBool && leftBool == rightBool;

    private static bool StrictNumberEqual(object? left, object? right)
    {
        return TryNumber(left, out var leftNumber) && TryNumber(right, out var rightNumber) &&
               leftNumber == rightNumber;
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case int item:
                number = item;
                return true;
            case double item when double.IsFinite(item):
                number = item;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static bool IsPrimitive(object? value) =>
        value is null || value is bool || value is string || TryNumber(value, out _);

    private static bool EquivalentWithoutLeaver(AsObject original, AsObject repaired)
    {
        if (!string.Equals(original.TypeName, repaired.TypeName, StringComparison.Ordinal)) return false;
        var expectedKeys = original.Keys.Where(key => key != LeaverDataKey).ToHashSet(StringComparer.Ordinal);
        if (repaired.Keys.Any(key => key == LeaverDataKey) || repaired.Count != expectedKeys.Count) return false;
        foreach (var key in expectedKeys)
        {
            if (!repaired.TryGetValue(key, out var repairedValue) || !original.TryGetValue(key, out var originalValue))
                return false;
            if (key == LocaleDataKey)
            {
                if (originalValue is not AsObject originalLocale || repairedValue is not AsObject repairedLocale ||
                    !EquivalentLocale(originalLocale, repairedLocale)) return false;
            }
            else if (!PrimitiveEqual(originalValue, repairedValue)) return false;
        }
        return true;
    }

    private static bool EquivalentLocale(AsObject original, AsObject repaired)
    {
        if (!string.Equals(original.TypeName, repaired.TypeName, StringComparison.Ordinal) ||
            original.Count != repaired.Count) return false;
        foreach (var key in original.Keys)
        {
            if (!repaired.TryGetValue(key, out var repairedValue) || !original.TryGetValue(key, out var originalValue) ||
                !PrimitiveEqual(originalValue, repairedValue)) return false;
        }
        return true;
    }

    private static bool PrimitiveEqual(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is string leftString && right is string rightString)
            return string.Equals(leftString, rightString, StringComparison.Ordinal);
        if (left is bool leftBool && right is bool rightBool)
            return leftBool == rightBool;
        return TryNumber(left, out var leftNumber) && TryNumber(right, out var rightNumber) &&
               leftNumber == rightNumber;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static LeaverDataRepairOutcome Skipped(string reason) =>
        new(LeaverDataRepairStatus.Skipped, reason);
}
