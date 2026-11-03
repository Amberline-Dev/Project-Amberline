using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amberline.Platform;

/// <summary>
/// Public, pinned companion distribution. Never includes AIR/game files or account state.
/// NOT RETAIL: retail players installed the client from Riot's patcher and connected to Riot's hosts; this serves
/// the friend launcher and companion archive so a friend's stock AIR can reach this host.
/// </summary>
public static class PlatformBootstrap
{
    /// <summary>
    /// The manifest and the archive need only the public host and the archive (the player's Play.cmd, tools/player-file.ps1, reads
    /// both); Play.cmd also needs its template, which a build without the friend launcher leaves out.
    /// </summary>
    public static void Map(WebApplication app, string directory, string launcherTemplate, string? publicHost, int port,
        X509Certificate2 certificate) => Map(app, directory, launcherTemplate, () => publicHost, port, certificate);

    /// <summary>
    /// <paramref name="publicHost"/> and <paramref name="serverName"/> are read on each request, so the address the home host's
    /// page saves applies without a restart (<see cref="HostSteps"/>). The manifest names the server only when it has a name.
    /// </summary>
    public static void Map(WebApplication app, string directory, string launcherTemplate, Func<string?> publicHost, int port,
        X509Certificate2 certificate, Func<string?>? serverName = null)
    {
        var archive = Path.Combine(directory, "companion.zip");
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var checksum = File.Exists(archive) ? Hash(archive) : null;
        var template = File.Exists(launcherTemplate);
        string? Served() => checksum is not null && publicHost() is { } host && IsHost(host) ? host : null;
        app.MapGet("/bootstrap/client.json", () => Served() is { } host
            ? Results.Json(new { protocol = 1, host, port, certificateSha256 = fingerprint,
                companionZip = "/bootstrap/companion.zip", companionSha256 = checksum,
                companionVersion = checksum, serverName = serverName?.Invoke() }, ManifestJson) : Results.NotFound());
        app.MapGet("/bootstrap/companion.zip", () => Served() is not null
            ? Results.File(archive, "application/zip", "companion.zip", enableRangeProcessing: true) : Results.NotFound());
        app.MapGet("/bootstrap/Play.cmd", () => template && Served() is { } host
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(RenderLauncher(File.ReadAllText(launcherTemplate), host, port, fingerprint)),
                "application/octet-stream", "Play.cmd") : Results.NotFound());
    }

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <param name="serverName">The Play file's server name (<c>__NAME__</c>, <see cref="HostAddress.NameProblem"/>'s rule), put in
    /// last as <c>tools/player-file.ps1</c> does; null leaves the template's <c>__NAME__</c> as it is.</param>
    public static string RenderLauncher(string template, string host, int port, string certificateSha256, string? serverName = null)
    {
        if (!IsHost(host) || port is < 1 or > 65535 || certificateSha256.Length != 64 ||
            certificateSha256.Any(c => !char.IsAsciiHexDigit(c)) || serverName is not null && HostAddress.NameProblem(serverName) is not null)
            throw new ArgumentException("Invalid public launcher endpoint.");
        // ponytail: obfuscation, not secrecy. Base64(host XOR 0x5A) keeps the public host out of the
        // friend CMD's plain text (ConvertFrom-HostToken decodes it); real hiding needs a relay or DNS name.
        var hostToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(host).Select(b => (byte)(b ^ 0x5A)).ToArray());
        var text = template.Replace("__HOST__", hostToken, StringComparison.Ordinal)
            .Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__CERT__", certificateSha256, StringComparison.Ordinal);
        return serverName is null ? text : text.Replace("__NAME__", serverName, StringComparison.Ordinal);
    }

    private static bool IsHost(string? host) => !string.IsNullOrEmpty(host) && host.Length <= 253 &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
