using System.Text;

namespace Amberline.AirCompanion;

public sealed record AirOverlayOptions(string SourceRoot, string DestinationRoot,
    string PlatformUrl = "http://127.0.0.1:8390", int MaestroPort = 8393);

public static class AirOverlay
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void Prepare(AirOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = Path.GetFullPath(options.SourceRoot);
        var destination = Path.GetFullPath(options.DestinationRoot);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"AIR source was not found: {source}");
        // Refuse an incomplete or truncated stock folder before any disposable copy exists (the friend launcher checks the same list).
        var incomplete = AirSourceLayout.Problems(source);
        if (incomplete.Count > 0)
            throw new IOException($"AIR source is incomplete (missing or damaged: {string.Join(", ", incomplete).Replace("LolClient.swf", "the AIR client's .swf file").Replace("LolClient.exe", "the AIR client's executable")}). " +
                "Choose a complete copy of the stock AIR client; no disposable copy was made.");
        if (PathsOverlap(source, destination)) throw new ArgumentException("The AIR destination must be outside the source tree.");
        if (options.MaestroPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "Maestro port must be between 1 and 65535.");

        var platform = ParsePlatformUrl(options.PlatformUrl);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException($"AIR destination is not empty: {destination}");
        Directory.CreateDirectory(destination);
        CopyTree(source, destination);
        Configure(destination, platform, options.MaestroPort);
    }

    /// <summary>
    /// Points an AIR folder the companion owns at the companion's local endpoints: the disposable copy above, or the player's AIR
    /// folder that the installer moved into the install folder (PlayerInstall). Only lol.properties and locale.properties change.
    /// </summary>
    public static void Configure(string airRoot, Uri platform, int maestroPort)
    {
        var destination = Path.GetFullPath(airRoot);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "127.0.0.1",
            ["host_randomize"] = "false",
            ["lq_uri"] = new Uri(platform, "login-queue/rest/queue").AbsoluteUri,
            ["lq_uri_randomize"] = "false",
            ["channel_protocol"] = "rtmp",
            ["remoting_port"] = "2099",
            ["startChat"] = "true",
            // Legacy chat stays local. Remote companions carry these bytes in
            // the pinned HTTPS bridge rather than exposing plaintext XMPP.
            ["xmpp_server_url"] = "127.0.0.1",
            ["xmpp_server_url_randomize"] = "false",
            ["xmpp_use_ssl"] = "false",
            ["xmpp_accept_self_signed_cert"] = "false",
            ["xmpp_muc_server_url"] = "127.0.0.1",
            ["xmpp_muc_server_port"] = "5222",
            ["xmpp_muc_conference_name"] = "conference",
            ["startMaestro"] = "true",
            ["maestro_port"] = maestroPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // These keys are all present on RiotApplication and otherwise fall back to
            // compiled public URLs. Keep optional requests inside the configured platform.
            ["getClientIpURL"] = new Uri(platform, "ip").AbsoluteUri,
            ["rssFeedJoJURL"] = new Uri(platform, "rss/joj.xml").AbsoluteUri,
            ["rssFeedStoreURL"] = new Uri(platform, "rss/store.xml").AbsoluteUri,
            ["rssStatusURLs"] = new Uri(platform, "status/").AbsoluteUri,
            ["storyPageURL"] = new Uri(platform, "story").AbsoluteUri,
            ["lobbyLandingURL"] = new Uri(platform, "landing").AbsoluteUri,
            // The profile's Ladder Rankings button (SummonerSummaryView) opens riotApplication.ladderURL when set (batch 549).
            // The forgot-password and create-account links have no such property: they are resource-bundle URLs
            // (recoverPasswordURL with only the region as a subdomain, createAccountURL) and stay Riot's.
            ["ladderURL"] = new Uri(platform, "ladder").AbsoluteUri,
        };
        // RiotApplication loads lol.properties first and locale.properties second. Apply
        // the same source-backed endpoint set to both files so the later file cannot restore
        // a public endpoint or a different Maestro port.
        ApplyProperties(Path.Combine(destination, "lol.properties"), properties);
        ApplyProperties(Path.Combine(destination, "locale.properties"), properties);
    }

    /// <summary>
    /// NOT RETAIL: retail's game used the language its launcher wrote into <c>locale=</c>; the usual start has no language choice, so it sets
    /// <paramref name="locale"/> (en_US) back. Only that line changes: it is added when missing, and every other line stays as it is.
    /// </summary>
    public static void SetLocale(string airRoot, string locale = "en_US") =>
        ApplyProperties(Path.Combine(Path.GetFullPath(airRoot), "locale.properties"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["locale"] = locale });

    /// <summary>
    /// Migrates only the old generated story endpoint in an owned disposable
    /// copy. Stock source files, lobby routing, and unrelated operator values
    /// are left untouched.
    /// </summary>
    public static int MigrateStoryPageUrl(string airRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(airRoot);
        var root = Path.GetFullPath(airRoot);
        var changed = 0;
        foreach (var name in new[] { "lol.properties", "locale.properties" })
            if (MigrateStoryPageUrlFile(Path.Combine(root, name))) changed++;
        return changed;
    }

    public static Uri ParsePlatformUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) || !uri.IsLoopback ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Platform URL must be a loopback HTTP origin without a path, query, credentials or fragment.", nameof(value));
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void ApplyProperties(string path, IReadOnlyDictionary<string, string> values)
    {
        var exists = File.Exists(path);
        var bytes = exists ? File.ReadAllBytes(path) : Array.Empty<byte>();
        var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = hasBom ? Utf8.GetString(bytes, 3, bytes.Length - 3) : Utf8.GetString(bytes);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var hadTrailingNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (hadTrailingNewline) lines.RemoveAt(lines.Count - 1);
        if (!exists && lines.Count == 1 && lines[0].Length == 0) lines.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            var separator = lines[index].IndexOf('=');
            if (separator <= 0) continue;
            var key = lines[index][..separator].Trim();
            if (values.TryGetValue(key, out var value))
            {
                lines[index] = $"{key}={value}";
                seen.Add(key);
            }
        }
        foreach (var (key, value) in values)
            if (!seen.Contains(key)) lines.Add($"{key}={value}");
        var output = string.Join("\r\n", lines) + "\r\n";
        var result = Utf8.GetBytes(output);
        if (hasBom) result = [0xEF, 0xBB, 0xBF, .. result];
        File.WriteAllBytes(path, result);
    }

    private static bool MigrateStoryPageUrlFile(string path)
    {
        if (!File.Exists(path)) return false;
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = hasBom ? Utf8.GetString(bytes, 3, bytes.Length - 3) : Utf8.GetString(bytes);
        var lines = text.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var ending = line.EndsWith('\r') ? "\r" : string.Empty;
            var content = ending.Length == 0 ? line : line[..^1];
            var separator = content.IndexOf('=');
            if (separator <= 0 || !string.Equals(content[..separator].Trim(), "storyPageURL", StringComparison.Ordinal))
                continue;
            var value = content[(separator + 1)..];
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !uri.IsLoopback ||
                uri.AbsolutePath != "/landing" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                continue;
            var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            lines[index] = content[..(separator + 1)] + origin + "/story" + ending;
            changed = true;
        }
        if (!changed) return false;
        var result = Utf8.GetBytes(string.Join('\n', lines));
        if (hasBom) result = [0xEF, 0xBB, 0xBF, .. result];

        var backup = path + ".story-landing-" + Guid.NewGuid().ToString("N") + ".bak";
        var temporary = path + ".story-landing-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, options: FileOptions.WriteThrough))
            {
                output.Write(result, 0, result.Length);
                output.Flush(flushToDisk: true);
            }
            // Recheck after writing the replacement, immediately before commit.
            if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path))) return false;
            File.Replace(temporary, path, backup, ignoreMetadataErrors: false);
            temporary = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception) { }
            }
        }
        return true;
    }

    private static void CopyTree(string source, string destination, bool root = true)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            // New client packages must not carry the operator's historical logs.
            // Originals stay in the source tree; AIR creates fresh logs on launch.
            if (root && Path.GetFileName(entry).Equals("logs", StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"AIR source contains a reparse point: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyTree(entry, target, root: false);
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        var a = first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }
}
