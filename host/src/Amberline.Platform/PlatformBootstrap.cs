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
    /// The manifest and the archive need only the public host and the archive (the player app's invite setup and the installed tool's
    /// update check read both). Batch 917: <c>/bootstrap/Play.cmd</c> is gone; friends get the invite link (<see cref="Folders.InviteLink"/>).
    /// </summary>
    public static void Map(WebApplication app, string directory, string? publicHost, int port, X509Certificate2 certificate) =>
        Map(app, directory, () => publicHost, port, certificate);

    /// <summary>
    /// <paramref name="publicHost"/> and <paramref name="serverName"/> are read on each request, so the address the home host's
    /// page saves applies without a restart (<see cref="HostSteps"/>). The manifest names the server only when it has a name.
    /// </summary>
    public static void Map(WebApplication app, string directory, Func<string?> publicHost, int port,
        X509Certificate2 certificate, Func<string?>? serverName = null)
    {
        var archive = Path.Combine(directory, "companion.zip");
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var checksum = File.Exists(archive) ? Hash(archive) : null;
        string? Served() => checksum is not null && publicHost() is { } host && IsHost(host) ? host : null;
        app.MapGet("/bootstrap/client.json", () => Served() is { } host
            ? Manifest(host, port, fingerprint, checksum!, serverName?.Invoke()) : Results.NotFound());
        app.MapGet("/bootstrap/companion.zip", () => Served() is not null
            ? Results.File(archive, "application/zip", "companion.zip", enableRangeProcessing: true) : Results.NotFound());
    }

    /// <summary>
    /// <c>/bootstrap/client.json</c>: protocol 1, the address, port and pin the player's tool checks, and the archive's SHA-256. NOT RETAIL
    /// (batch 856): <paramref name="movedTo"/>, only from the stand-in on the old 1.0.0.126 port (<see cref="HostOldShortcuts"/>); without
    /// it the manifest is as before, byte for byte (a null member is left out).
    /// </summary>
    public static IResult Manifest(string host, int port, string certificateSha256, string companionSha256, string? serverName,
        MovedTo? movedTo = null) =>
        Results.Json(new { protocol = 1, host, port, certificateSha256, companionZip = "/bootstrap/companion.zip", companionSha256,
            companionVersion = companionSha256, serverName, movedTo }, ManifestJson);

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Batch 829: the name of the player tool's per-user folder (<see cref="Folders.FolderMove.PlayerFolder"/>, "Amberline Player" on 1.0.0.126,
    /// "Amberline 132 Player" on 1.0.0.132), which the moved notice names (<see cref="MovedTo"/>), so each line's player app fills its own.
    /// </summary>
    public static readonly string PlayerFolderName = Path.GetFileName(Folders.FolderMove.PlayerFolder(""));

    internal static bool IsHost(string? host) => !string.IsNullOrEmpty(host) && host.Length <= 253 &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// NOT RETAIL (batch 856, interface 1): the manifest's <c>movedTo</c>, where an old 1.0.0.126 shortcut's server now runs: its remote port,
/// its pin (the format of the manifest's own <c>certificateSha256</c>), its name and the player folder its player app fills.
/// </summary>
public sealed record MovedTo(int Port, string CertificateSha256, string Name, string PlayerFolder);
