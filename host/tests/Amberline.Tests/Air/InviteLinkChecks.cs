using System.Buffers.Text;
using System.Text;
using Amberline.Platform;
using Codec = Amberline.AirCompanion.InviteLink;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 917): the invite format v1 (b917-918 invite spec), as the player app (<see cref="Codec"/>) reads and writes it; the
/// Platform compiles the same two files (its internals are not visible here: the host pages' checks compare its links with this codec's). The golden vectors are the spec's example JSON with a fixed pin; the
/// public Join page (batch 918) must decode the same strings.
/// </summary>
public static class InviteLinkChecks
{
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    /// <summary>The spec's example for each line, and its encoded invite (base64url, no padding).</summary>
    private static readonly IReadOnlyDictionary<string, (string Json, string Encoded)> Golden = new Dictionary<string, (string, string)>
    {
        ["126"] = ("""{"v":1,"line":"126","tag":"v0.1.31","host":"203.0.113.10","port":8391,"pin":"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF","name":"My Server"}""",
            "eyJ2IjoxLCJsaW5lIjoiMTI2IiwidGFnIjoidjAuMS4zMSIsImhvc3QiOiIyMDMuMC4xMTMuMTAiLCJwb3J0Ijo4MzkxLCJwaW4iOiIwMTIzNDU2Nzg5QUJDREVGMDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUYwMTIzNDU2Nzg5QUJDREVGIiwibmFtZSI6Ik15IFNlcnZlciJ9"),
        ["132"] = ("""{"v":1,"line":"132","tag":"p132-v0.1.11","host":"203.0.113.10","port":8391,"pin":"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF","name":"My Server"}""",
            "eyJ2IjoxLCJsaW5lIjoiMTMyIiwidGFnIjoicDEzMi12MC4xLjExIiwiaG9zdCI6IjIwMy4wLjExMy4xMCIsInBvcnQiOjgzOTEsInBpbiI6IjAxMjM0NTY3ODlBQkNERUYwMTIzNDU2Nzg5QUJDREVGMDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUYiLCJuYW1lIjoiTXkgU2VydmVyIn0"),
    };

    private static string Other => Codec.Line == "126" ? "132" : "126";

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    [Check("server.invite-link-codec")]
    public static void TheInviteRoundTripsAndRefusesInPlainWords()
    {
        // The line's values: its tag prefix is the release tags' (PlatformLine, patch.json), its scheme the spec's.
        TestAssert.Equal(PlatformLine.ReleaseTagPrefix, Codec.TagPrefix, "the invite's tag prefix is the line's release tags'");
        TestAssert.Equal(Codec.Line == "126" ? "amberline-player" : "amberline-132-player", Codec.Scheme, "the line's link scheme");
        var (ownJson, ownEncoded) = Golden[Codec.Line];

        // The golden vector, both ways.
        var invite = new Codec.Invite(Codec.Line == "126" ? "v0.1.31" : "p132-v0.1.11", "203.0.113.10", 8391, Pin, "My Server");
        TestAssert.Equal(ownEncoded, Codec.Encode(invite), "the spec's example encodes to the golden invite");
        TestAssert.Equal(ownEncoded, Encode(ownJson), "the golden invite is the spec's JSON in base64url");
        TestAssert.Equal(Codec.Page + "#" + ownEncoded, Codec.Link(invite), "the link is the Join page with the invite after #");
        TestAssert.Equal("https://amberline-dev.github.io/Project-Amberline/join/#" + ownEncoded, Codec.Link(invite), "the spec's page");
        TestAssert.Equal(invite, Codec.Decode(ownEncoded).Invite, "the golden invite decodes");
        // Batch 917 follow-up: no release version (a development build), a guide build (0.0.N) or a release before the line's first player-app
        // release names that release; it and later releases keep their own. patch.json carries the same release for build-server.ps1.
        var first = Codec.TagPrefix + Codec.FirstPlayerRelease;
        TestAssert.Equal(Codec.Line == "126" ? "v0.1.31" : "p132-v0.1.11", first, "the line's first player-app release");
        TestAssert.Equal(Codec.FirstPlayerRelease, System.Text.Json.JsonDocument.Parse(File.ReadAllText(WorkspaceFiles.Find("patch.json")))
            .RootElement.GetProperty("firstPlayerRelease").GetString(), "patch.json's firstPlayerRelease");
        foreach (var (version, what) in new (Version?, string)[] { (null, "a development build"), (new(0, 0, 461520), "a guide build"),
            (new(0, 1, Codec.Line == "126" ? 30 : 10), "the release before") })
            TestAssert.Equal(first, Codec.Tag(version), what);
        TestAssert.Equal(first, Codec.Tag(Version.Parse(Codec.FirstPlayerRelease)), "the first player-app release itself");
        TestAssert.Equal(Codec.TagPrefix + "1.4.0", Codec.Tag(new Version(1, 4, 0)), "a later release keeps its own tag");

        // A lower-case pin is written upper-case; a DNS name and the port's ends round-trip.
        foreach (var other in new[] { invite with { Pin = Pin.ToLowerInvariant() }, invite with { Host = "play.example.test", Port = 1 },
            invite with { Port = 65535, Name = "A" }, invite with { Name = "Saturday League 2.0_x-y" } })
            TestAssert.Equal(other with { Pin = other.Pin.ToUpperInvariant() }, Codec.Decode(Codec.Encode(other)).Invite, $"round trip of {other}");

        // Every accepted form: the whole link, either scheme form, a trailing slash, spaces and quotes around it.
        foreach (var form in new[]
        {
            Codec.Link(invite), $"  {Codec.Link(invite)}  ", $"\"{Codec.Link(invite)}\"", $"'{ownEncoded}'", ownEncoded, ownEncoded + "/",
            $"{Codec.Scheme}:{ownEncoded}", $"{Codec.Scheme}://{ownEncoded}", $"{Codec.Scheme}://{ownEncoded}/", $"\"{Codec.Scheme}:{ownEncoded}\"",
            $"{Codec.Scheme.ToUpperInvariant()}:{ownEncoded}",
        })
            TestAssert.Equal(invite, Codec.Decode(form).Invite, $"the form {form}");

        // The refusals, in plain words.
        TestAssert.Equal(Codec.Damaged, "This invite link is damaged. Ask the host to copy it again.");
        TestAssert.Equal(Codec.Newer, "This invite needs a newer Amberline Player. Download it from the invite page.");
        var (otherJson, otherEncoded) = Golden[Other];
        var otherLine = $"This invite is for the 1.0.0.{Other} server. Download that line's player from the invite page.";
        var otherScheme = Other == "126" ? "amberline-player" : "amberline-132-player";
        foreach (var form in new[] { otherEncoded, $"{otherScheme}:{otherEncoded}", "https://amberline-dev.github.io/Project-Amberline/join/#" + otherEncoded })
            TestAssert.Equal(otherLine, Codec.Decode(form).Refusal, $"the other line's invite ({form})");
        TestAssert.Equal(Codec.Newer, Codec.Decode(Encode("""{"v":2,"line":"x"}""")).Refusal, "a newer format, whatever its other keys");
        TestAssert.Equal(Codec.Newer, Codec.Decode(Encode(ownJson.Replace("\"v\":1", "\"v\":7", StringComparison.Ordinal))).Refusal, "v 7");
        string Swap(string from, string to) => Encode(ownJson.Replace(from, to, StringComparison.Ordinal));
        foreach (var (damaged, why) in new (string?, string)[]
        {
            (null, "nothing"), ("", "empty"), ("   ", "spaces"), ("not an invite!", "not base64url"), (Encode("not json"), "not JSON"),
            (Encode("[1,2]"), "not an object"), (Encode("{}"), "no keys"), (ownEncoded[..^4], "cut short"), ("https://amberline-dev.github.io/Project-Amberline/join/#", "no invite"),
            (Swap("\"v\":1", "\"v\":0"), "v 0"), (Swap("\"v\":1", "\"v\":\"1\""), "v as text"), (Swap("\"v\":1,", ""), "no v"),
            (Swap("\"line\":\"" + Codec.Line + "\"", "\"line\":\"127\""), "another patch"), (Swap(",\"name\":\"My Server\"", ""), "no name"),
            (Swap("\"port\":8391", "\"port\":0"), "port 0"), (Swap("\"port\":8391", "\"port\":65536"), "port 65536"), (Swap("\"port\":8391", "\"port\":\"8391\""), "port as text"),
            (Swap(Pin, Pin[..63]), "a short pin"), (Swap(Pin, Pin[..63] + "G"), "a pin that is not hex"),
            (Swap("203.0.113.10", "https://203.0.113.10"), "a host with a scheme"), (Swap("203.0.113.10", "203.0.113.10:8391"), "a host with a port"),
            (Swap("203.0.113.10", "my host"), "a host with a space"), (Swap("My Server", "My/Server"), "a name with a slash"),
            (Swap("My Server", "CON"), "a reserved name"), (Swap("My Server", " My Server"), "a name starting with a space"),
            (Swap("\"tag\":\"" + Codec.TagPrefix, "\"tag\":\"x" + Codec.TagPrefix), "a tag of another form"),
            (Swap("\"tag\":\"" + invite.Tag, "\"tag\":\"" + invite.Tag + "-beta"), "a pre-release tag"),
            (Swap("\"tag\":\"" + invite.Tag, "\"tag\":\"" + (Codec.Line == "126" ? "p132-v0.1.11" : "v0.1.31")), "the other line's tag"),
            (Swap("\"v\":1", "\"V\":1"), "V instead of v"),
        })
        {
            var decoded = Codec.Decode(damaged);
            TestAssert.True(decoded.Invite is null && decoded.Refusal == Codec.Damaged, $"{why} is damaged, not: {decoded}");
        }

        // The writer refuses what the reader would.
        foreach (var bad in new[] { invite with { Host = "my host" }, invite with { Port = 0 }, invite with { Pin = "AB" }, invite with { Name = "a/b" },
            invite with { Tag = "0.1.31" } })
        {
            try
            {
                Codec.Encode(bad);
                throw new InvalidOperationException($"{bad} was encoded");
            }
            catch (ArgumentException) { }
        }
    }
}
