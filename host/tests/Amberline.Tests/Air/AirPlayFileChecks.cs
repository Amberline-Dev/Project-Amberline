using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL: the server package's certificate and the friends' download (batch 615). tools/build-server.ps1 requires the server's public
/// address and makes the Platform's certificate (tools/player-file.ps1); the player app downloads the companion from the Platform's
/// /bootstrap routes over the pinned HTTPS port. Batch 917: Play.cmd is gone (the invite link replaced it, <see cref="InviteLinkChecks"/>), so
/// these checks no longer make or run it. Tests use the reserved name amberline.example.test.
/// </summary>
public static class AirPlayFileChecks
{
    private const string TestHost = "amberline.example.test";

    /// <summary>
    /// player-file.ps1 makes the certificate the deployed Platform loads (HostFiles.Certificate keeps an existing host.pfx) and returns its
    /// pin; a rebuild keeps it, a kept certificate is copied as it is, one the Platform cannot load is refused before anything is written.
    /// Batch 917: it writes no Play.cmd and takes no host, port or server name.
    /// </summary>
    [Check("server.air-play-file-keeps-certificate")]
    public static void RebuildsKeepTheCertificateAndItsPin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-play-file-pin");
        try
        {
            var first = Path.Combine(root, "first");
            var pin = PlayerFile(first);
            var pfx = Path.Combine(first, "platform", "platform-data", "remote", "host.pfx");
            var bytes = File.ReadAllBytes(pfx);
            TestAssert.Equal(new[] { pfx }, Directory.GetFiles(first, "*", SearchOption.AllDirectories), "player-file.ps1 writes the certificate only");
            TestAssert.Equal(pin, PlayerFile(first), "running it again for the same folder changed the pin");
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(pfx)), "running it again replaced the certificate");

            // The pin is the certificate the deployed Platform loads (HostFiles.Certificate keeps an existing host.pfx).
            var remote = Path.GetDirectoryName(pfx)!;
            using (var loaded = HostFiles.Certificate(remote))
                TestAssert.Equal(pin, loaded.GetCertHashString(HashAlgorithmName.SHA256), "the pin must be the certificate the Platform serves");
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(pfx)), "the Platform replaced the build's certificate");

            // Made as HostFiles.Certificate makes its own: key, subject, extensions and lifetime.
            using (var built = X509CertificateLoader.LoadPkcs12FromFile(pfx, null))
            using (var platform = HostFiles.Certificate(Path.Combine(root, "reference")))
            {
                TestAssert.Equal(platform.Subject, built.Subject);
                TestAssert.Equal(platform.GetRSAPublicKey()!.KeySize, built.GetRSAPublicKey()!.KeySize);
                TestAssert.Equal(platform.SignatureAlgorithm.Value, built.SignatureAlgorithm.Value);
                TestAssert.Equal(Describe(platform), Describe(built), "the certificate extensions differ from HostFiles.Certificate's");
                TestAssert.True(Math.Abs(((platform.NotAfter - platform.NotBefore) - (built.NotAfter - built.NotBefore)).TotalDays) < 2,
                    "the certificate lifetime differs from HostFiles.Certificate's");
            }

            // build-server.ps1 passes the existing -Output's host.pfx (or the operator's -Certificate) to the new build.
            var second = Path.Combine(root, "second");
            TestAssert.Equal(pin, PlayerFile(second, pfx), "a build from the kept certificate changed the pin");
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(second, "platform", "platform-data", "remote", "host.pfx"))),
                "the kept certificate was not copied as it is");

            // A certificate the Platform cannot load (it reads host.pfx without a password) is refused before anything is written.
            var locked = Path.Combine(root, "locked.pfx");
            using (var certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.Exportable))
                File.WriteAllBytes(locked, certificate.Export(X509ContentType.Pfx, "secret"));
            var third = Path.Combine(root, "third");
            var refused = RunPlayerFile(third, locked);
            TestAssert.True(refused.ExitCode != 0 && refused.Output.Contains("without a password", StringComparison.Ordinal), refused.Output);
            TestAssert.False(Directory.Exists(third), "a refused certificate still wrote the folder");
            // Batch 917: the script takes no host any more (it went with Play.cmd).
            var oldCall = AirBootstrapChecks.RunPowerShell("pwsh", ["-File", AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "player-file.ps1")),
                "-Root", Path.Combine(root, "old"), "-PublicHost", TestHost]);
            TestAssert.True(oldCall.ExitCode != 0 && oldCall.Output.Contains("PublicHost", StringComparison.Ordinal), oldCall.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Check("server.air-build-server-requires-public-host")]
    public static void BuildServerRefusesAMissingOrMalformedPublicHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "build-server.ps1"));
        var source = File.ReadAllText(script);
        // The defaults are the Platform's own (PlatformSettings).
        var defaults = new PlatformSettings("game", "server");
        // Batch 810: build-server.ps1 takes the line's remote and first game ports from its patch.json when a parameter is 0 (the default).
        var ports = System.Text.Json.JsonDocument.Parse(File.ReadAllText(WorkspaceFiles.Find("patch.json"))).RootElement.GetProperty("ports");
        TestAssert.Equal(defaults.RemotePort, ports.GetProperty("remote").GetInt32(), "the line's remote port (patch.json)");
        TestAssert.Equal(defaults.FirstGamePort, ports.GetProperty("gameFirst").GetInt32(), "the line's first game port (patch.json)");
        TestAssert.True(source.Contains("$RemotePort = [int]$facts.ports.remote", StringComparison.Ordinal)
            && source.Contains("$FirstGamePort = [int]$facts.ports.gameFirst", StringComparison.Ordinal), "build-server.ps1 takes both ports from patch.json");

        var root = AirBootstrapChecks.TemporaryDirectory("league-air-build-server-host");
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var air = Directory.CreateDirectory(Path.Combine(root, "air")).FullName;
            var output = Path.Combine(root, "out");
            string[] inputs = ["-File", script, "-Patch", WorkspaceFiles.Patch, "-GameRoot", game, "-AirRoot", air, "-Output", output];

            var missing = AirBootstrapChecks.RunPowerShell("pwsh", inputs);
            TestAssert.True(missing.ExitCode != 0 && missing.Output.Contains("PublicHost", StringComparison.Ordinal), missing.Output);
            foreach (var malformed in new[] { $"https://{TestHost}", $"{TestHost}/play", "amberline example.test", $"{TestHost}:8391", "" })
            {
                var refused = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", malformed]);
                TestAssert.True(refused.ExitCode != 0 && refused.Output.Contains("PublicHost", StringComparison.Ordinal),
                    $"-PublicHost {malformed} was not refused: {refused.Output}");
            }
            var loopback = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", TestHost, "-RemotePort", ports.GetProperty("web").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            TestAssert.True(loopback.ExitCode != 0 && loopback.Output.Contains("-RemotePort", StringComparison.Ordinal), loopback.Output);
            // Batch 619: the display name becomes a desktop file name, so a name Windows cannot use is refused before anything is built.
            foreach (var badName in new[] { "bad/name", " Amberline", "CON", "" })
            {
                var refused = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", TestHost, "-ServerName", badName]);
                TestAssert.True(refused.ExitCode != 0 && refused.Output.Contains("-ServerName", StringComparison.Ordinal),
                    $"-ServerName '{badName}' was not refused: {refused.Output}");
            }
            // A good host passes on to the input checks, which refuse the empty game folder before anything is built.
            var accepted = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", TestHost]);
            TestAssert.True(accepted.ExitCode != 0 && accepted.Output.Contains("game executable", StringComparison.Ordinal), accepted.Output);
            TestAssert.False(Directory.Exists(output), "a refused build created its output folder");
            // Batch 625: -Layout Home needs no address. Without one it passes the same refusals and reaches the input checks.
            var home = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-Layout", "Home", "-Version", "1.4.0"]);
            TestAssert.True(home.ExitCode != 0 && home.Output.Contains("game executable", StringComparison.Ordinal) &&
                !home.Output.Contains("PublicHost", StringComparison.Ordinal), home.Output);
            TestAssert.False(Directory.Exists(output), "a refused release created its output folder");
            // Batch 917: -Version names the invite link's release on the Server layout too, as x.y.z; a malformed one is refused first.
            var version = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", TestHost, "-Version", "1.4"]);
            TestAssert.True(version.ExitCode != 0 && version.Output.Contains("-Version", StringComparison.Ordinal), version.Output);
            TestAssert.False(Directory.Exists(output), "a refused build created its output folder");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Runs tools/player-file.ps1 and returns the pin it prints.</summary>
    private static string PlayerFile(string root, string? certificate = null)
    {
        var result = RunPlayerFile(root, certificate);
        TestAssert.Equal(0, result.ExitCode, result.Output);
        var pin = result.Output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length == 64) ?? string.Empty;
        TestAssert.True(Regex.IsMatch(pin, "^[0-9A-F]{64}$"), result.Output);
        return pin;
    }

    private static (int ExitCode, string Output) RunPlayerFile(string root, string? certificate)
    {
        string[] arguments = ["-File", AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "player-file.ps1")), "-Root", root];
        return AirBootstrapChecks.RunPowerShell("pwsh", certificate is null ? arguments : [.. arguments, "-Certificate", certificate]);
    }

    internal static string WindowsPowerShell() =>
        AirBootstrapChecks.FindWindowsPowerShell() ?? throw new InvalidOperationException("Windows PowerShell 5.1 is required for these checks");

    /// <summary>The real /bootstrap routes on loopback HTTPS. <paramref name="advertisedPort"/> is the port client.json names (default: the listening one).</summary>
    internal static async Task<WebApplication> StartPlatformAsync(string bootstrap, int port, X509Certificate2 certificate, int? advertisedPort = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        PlatformBootstrap.Map(app, bootstrap, "127.0.0.1", advertisedPort ?? port, certificate);
        await app.StartAsync();
        return app;
    }

    private static string Describe(X509Certificate2 certificate) => string.Join(";", certificate.Extensions
        .Select(extension => extension.Oid!.Value + (extension.Critical ? "!" : "") + ":" + Convert.ToHexString(extension.RawData))
        .Where(text => !text.StartsWith("2.5.29.14", StringComparison.Ordinal)));
}
