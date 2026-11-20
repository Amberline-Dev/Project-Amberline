using System.Buffers.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Linked as source into the Platform as well (see RetailClients.cs): Users, Add a user and the sign-up page make the link the player app reads.
// The values that differ per patch line (the line, its tag prefix and its link scheme) are in InviteLink.Line.cs.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// NOT RETAIL (batch 917, invite format v1, owner design 20 November 2026; retail players installed with the publisher's patcher): the invite
/// link a host sends instead of Play.cmd. The invite is base64url (RFC 4648 section 5, no padding) of the UTF-8 JSON
/// <c>{"v":1,"line":"126","tag":"v0.1.31","host":"203.0.113.10","port":8391,"pin":"&lt;64 hex&gt;","name":"My Server"}</c>, and the link is
/// <see cref="Page"/><c>#&lt;invite&gt;</c>: the part after <c>#</c> never reaches the page's server. The one codec the Platform (which makes
/// the link) and the player app (which reads it) both compile; the public Join page (batch 918) reads the same format. Encoded, not secret:
/// anyone holding the link can read the host's address, as anyone holding Play.cmd could.
/// </summary>
internal static partial class InviteLink
{
    /// <summary>The format version this code writes and the newest it reads.</summary>
    internal const int Format = 1;
    internal const string Page = "https://amberline-dev.github.io/Project-Amberline/join/";
    internal const string Damaged = "This invite link is damaged. Ask the host to copy it again.";
    internal const string Newer = "This invite needs a newer Amberline Player. Download it from the invite page.";

    /// <summary>The refusal of an invite for the other patch line.</summary>
    internal static string OtherLine(string line) => $"This invite is for the 1.0.0.{line} server. Download that line's player from the invite page.";

    /// <summary>One server: the host's own release tag, the address and HTTPS port friends reach, the certificate's SHA-256, the server's name.</summary>
    internal sealed record Invite(string Tag, string Host, int Port, string Pin, string Name);

    // PlatformBootstrap.IsHost's rule; the server name rule of PlayerInstall.IsName and HostSteps.NameProblem (a desktop file name).
    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9.-]{1,253}\z", RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TagPattern = new($@"^{Regex.Escape(TagPrefix)}[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant);
    // Either line's scheme (amberline-player, amberline-132-player), so an invite for the other line still gets its own refusal.
    private static readonly Regex SchemePrefix = new(@"^amberline(?:-[0-9]+)?-player:(?://)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// This line's release tag for the host's <paramref name="version"/> (x.y.z), as its release is tagged. Batch 917 follow-up: no version (a
    /// development build) or one before the line's first player-app release (<see cref="FirstPlayerRelease"/>) names that release.
    /// </summary>
    internal static string Tag(Version? version) => TagPrefix + (version is { } own && own >= Version.Parse(FirstPlayerRelease)
        ? $"{own.Major}.{own.Minor}.{own.Build}" : FirstPlayerRelease);

    /// <summary>The encoded invite of this line; an invite that breaks a rule is refused (<see cref="ArgumentException"/>).</summary>
    internal static string Encode(Invite invite)
    {
        if (!Valid(invite)) throw new ArgumentException("The invite's tag, host, port, pin or name is not valid.", nameof(invite));
        // The spec's key order; compact JSON. Every value is plain ASCII by its rule, so nothing is escaped.
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = Format, line = Line, tag = invite.Tag, host = invite.Host, port = invite.Port, pin = invite.Pin.ToUpperInvariant(), name = invite.Name,
        });
        return Base64Url.EncodeToString(json);
    }

    /// <summary>The link the host sends: the Join page with the invite after <c>#</c>.</summary>
    internal static string Link(Invite invite) => Page + "#" + Encode(invite);

    /// <summary>
    /// The invite in <paramref name="text"/>, or the refusal in plain words. Accepted: the whole link (what follows <c>#</c>), either line's
    /// <c>&lt;scheme&gt;:&lt;invite&gt;</c> or <c>&lt;scheme&gt;://&lt;invite&gt;</c>, with or without a trailing <c>/</c>, or the bare invite; spaces
    /// and quotes around it are trimmed. A newer format, then the other line, are refused by name; anything else that fails is damaged.
    /// </summary>
    internal static (Invite? Invite, string? Refusal) Decode(string? text)
    {
        var encoded = (text ?? "").Trim().Trim('"', '\'').Trim();
        if (encoded.IndexOf('#') is var hash and >= 0) encoded = encoded[(hash + 1)..];
        encoded = SchemePrefix.Replace(encoded, "").TrimEnd('/');
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(encoded));
            root = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException) { return (null, Damaged); }
        if (root.ValueKind != JsonValueKind.Object) return (null, Damaged);
        // The version first: a newer format may name other keys.
        if (Field(root, "v") is not { ValueKind: JsonValueKind.Number } v || !v.TryGetInt32(out var format) || format < 1) return (null, Damaged);
        if (format > Format) return (null, Newer);
        string? Text(string name) => Field(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        if (Text("line") is not ("126" or "132") || Text("tag") is not { } tag || Text("host") is not { } host || Text("pin") is not { } pin ||
            Text("name") is not { } name || Field(root, "port") is not { ValueKind: JsonValueKind.Number } number || !number.TryGetInt32(out var port))
            return (null, Damaged);
        var invite = new Invite(tag, host, port, pin.ToUpperInvariant(), name);
        if (!Valid(invite with { Tag = TagPrefix + "0.0.0" })) return (null, Damaged);
        if (Text("line") is { } line && line != Line) return (null, OtherLine(line));
        return Valid(invite) ? (invite, null) : (null, Damaged);
    }

    /// <summary>Each field by its rule: this line's tag, a bare DNS name or IPv4 address, a port, a 64-hex pin, a server name.</summary>
    internal static bool Valid(Invite invite) => TagPattern.IsMatch(invite.Tag) &&
        HostPattern.IsMatch(invite.Host) && Uri.CheckHostName(invite.Host) is UriHostNameType.Dns or UriHostNameType.IPv4 &&
        invite.Port is >= 1 and <= 65535 && invite.Pin.Length == 64 && invite.Pin.All(char.IsAsciiHexDigit) &&
        NamePattern.IsMatch(invite.Name) && !ReservedName.IsMatch(invite.Name);

    // The key exactly as the spec names it: an object with "V" instead of "v" is damaged.
    private static JsonElement? Field(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value : null;
}
