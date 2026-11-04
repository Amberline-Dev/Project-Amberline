using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amberline.Data;
using Amberline.Server;

namespace Amberline.Host;

public sealed record HostSettings(string DataRoot, string ServerExecutable, int ControlPort = 5120,
    int FirstGamePort = 5119, int MaxGames = 4, int FinishedGraceSeconds = 30,
    int WaitingTimeoutMinutes = 30, int MaxGameHours = 6, string BindAddress = "0.0.0.0")
{
    /// <summary>The most games a host runs at once (Settings' Games at the same time, batch 684: 1 to 32).</summary>
    public const int MostGames = 32;

    /// <summary>
    /// <paramref name="games"/> match servers fit from <paramref name="firstGamePort"/>: 1 to <see cref="MostGames"/> of them, every port
    /// within 1 to 65535. The one rule of the start, Settings and <see cref="GameManager.MaxGames"/>.
    /// </summary>
    public static bool GamePortsFit(int firstGamePort, int games) =>
        firstGamePort is >= 1 and <= 65535 && games is >= 1 and <= MostGames && firstGamePort + games - 1 <= 65535;

    public void Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Host bind address must be an IPv4 address.");
        if (ControlPort is < 1 or > 65535 || !GamePortsFit(FirstGamePort, MaxGames) || FinishedGraceSeconds is < 1 or > 300 ||
            WaitingTimeoutMinutes is < 1 or > 1440 || MaxGameHours is < 1 or > 48)
            throw new ArgumentException("Invalid host ports, capacity or timeouts.");
        if (!Directory.Exists(CaseInsensitivePaths.Resolve(DataRoot)) || !File.Exists(ServerExecutable))
            throw new ArgumentException("Configure the existing game data folder and prepared server executable in host.json.");
    }
}

public sealed record HostGuest(string Id, string Name, string TokenHash, bool Admin = false);

public static class HostFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    /// <summary>
    /// Replaces <paramref name="path"/> whole, through a temporary file of this write's own, so two writers never share one. Windows
    /// refuses the replace while another reader holds the file open (the address watch, a page, a virus scanner), so it is tried again
    /// for up to 2 seconds (batch 655: the address step answered 500 under load).
    /// </summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            for (var attempt = 1; ; attempt++)
                try
                {
                    File.Move(temporary, path, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40) { Thread.Sleep(50); }
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>The lifetime of a new certificate (owner, 2 November 2026): a pinned self-signed certificate gains nothing from expiring.</summary>
    public const int CertificateYears = 20;

    /// <summary>
    /// <c>host.pfx</c> in <paramref name="directory"/>, made when missing. An existing file is loaded and never replaced, so its pin
    /// and its own lifetime stay (an install made with the earlier two years keeps them).
    /// </summary>
    public static X509Certificate2 Certificate(string directory)
    {
        var path = Path.Combine(directory, "host.pfx");
        if (!File.Exists(path))
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=Amberline host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(CertificateYears));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    // Read on each request so an operator can revoke an invitation while the manager is running.
    public static HostGuest? Authenticate(string directory, string token)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) return null;
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        foreach (var guest in ReadGuests(directory))
            if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(guest.TokenHash))) return guest;
        return null;
    }

    public static HostGuest[] ReadGuests(string directory) =>
        File.Exists(Path.Combine(directory, "guests.json"))
            ? JsonSerializer.Deserialize<HostGuest[]>(File.ReadAllText(Path.Combine(directory, "guests.json")), Json) ?? [] : [];

    public static GuestInvitation Invite(string directory, string name, int port, bool admin = false)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var guest = new HostGuest(Guid.NewGuid().ToString("N"), name, Hash(token), admin);
        Write(Path.Combine(directory, "guests.json"), ReadGuests(directory).Append(guest).ToArray());
        using var certificate = Certificate(directory);
        return new GuestInvitation(token, certificate.GetCertHashString(HashAlgorithmName.SHA256), port);
    }
}
