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
/// NOT RETAIL: the player's one file (batch 615). tools/build-server.ps1 requires the server's public address, makes the Platform's
/// certificate and writes Play.cmd (tools/player-file.ps1 from tools/play-bootstrap.cmd); Play.cmd downloads the companion from the
/// Platform's /bootstrap routes over the pinned HTTPS port. Tests use the reserved name amberline.example.test.
/// </summary>
public static class AirPlayFileChecks
{
    private const string TestHost = "amberline.example.test";

    [Check("server.air-play-file-bakes-connection")]
    public static void PlayFileCarriesHostPortAndPinOnlyInBakedForm()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-play-file");
        try
        {
            var pin = PlayerFile(root, TestHost, 8391);
            var play = File.ReadAllText(Path.Combine(root, "Play.cmd"));
            TestAssert.False(play.Contains(TestHost, StringComparison.OrdinalIgnoreCase), "Play.cmd must not hold its host in plain text");
            TestAssert.True(play.Length < 16 * 1024, $"Play.cmd must stay a few KB; it is {play.Length} bytes");
            TestAssert.True(play.Contains(@"\Sysnative\WindowsPowerShell\v1.0\powershell.exe", StringComparison.Ordinal) &&
                !play.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase), "Play.cmd must run the 64-bit Windows PowerShell only");
            TestAssert.True(play.Contains("pause >nul", StringComparison.Ordinal), "Play.cmd must pause after an error");

            // The same scheme as Play.cmd: rendering the template with the Platform's own generator gives the same file. Batch 619
            // added the server's display name (__NAME__, -ServerName, default Amberline), which only player-file.ps1 fills.
            var template = File.ReadAllText(AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "play-bootstrap.cmd")))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
                .Replace("__NAME__", "Amberline", StringComparison.Ordinal);
            TestAssert.Equal(PlatformBootstrap.RenderLauncher(template, TestHost, 8391, pin), play,
                "player-file.ps1 must render Play.cmd exactly as PlatformBootstrap.RenderLauncher renders a launcher");
            TestAssert.True(play.Contains("--install --connection $connection --server-name $Name", StringComparison.Ordinal),
                "Play.cmd must start the companion's installer for its server");

            // Play.cmd's own decoder (Windows PowerShell 5.1) gives back the host, the port and the pin.
            var declarations = Path.Combine(root, "declarations.ps1");
            File.WriteAllText(declarations, Declarations(play), new UTF8Encoding(false));
            var driver = Path.Combine(root, "decode.ps1");
            File.WriteAllText(driver, "param([string]$DeclarationsPath)\n$ErrorActionPreference = 'Stop'\n. $DeclarationsPath\n" +
                "Write-Output ('decoded|' + (ConvertFrom-HostToken $HostToken) + '|' + $PortText + '|' + $Pin)\n", new UTF8Encoding(false));
            var decoded = AirBootstrapChecks.RunPowerShell(WindowsPowerShell(), ["-File", driver, "-DeclarationsPath", declarations]);
            TestAssert.Equal(0, decoded.ExitCode, decoded.Output);
            TestAssert.True(decoded.Output.Contains($"decoded|{TestHost}|8391|{pin}", StringComparison.Ordinal), decoded.Output);

            // The pin is the certificate the deployed Platform loads (HostFiles.Certificate keeps an existing host.pfx).
            var remote = Path.Combine(root, "platform", "platform-data", "remote");
            var before = File.ReadAllBytes(Path.Combine(remote, "host.pfx"));
            using (var loaded = HostFiles.Certificate(remote))
                TestAssert.Equal(pin, loaded.GetCertHashString(HashAlgorithmName.SHA256), "Play.cmd must pin the certificate the Platform serves");
            TestAssert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(remote, "host.pfx"))), "the Platform replaced the build's certificate");

            // Made as HostFiles.Certificate makes its own: key, subject, extensions and lifetime.
            var reference = Path.Combine(root, "reference");
            using var built = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(remote, "host.pfx"), null);
            using var platform = HostFiles.Certificate(reference);
            TestAssert.Equal(platform.Subject, built.Subject);
            TestAssert.Equal(platform.GetRSAPublicKey()!.KeySize, built.GetRSAPublicKey()!.KeySize);
            TestAssert.Equal(platform.SignatureAlgorithm.Value, built.SignatureAlgorithm.Value);
            TestAssert.Equal(Describe(platform), Describe(built), "the certificate extensions differ from HostFiles.Certificate's");
            TestAssert.True(Math.Abs(((platform.NotAfter - platform.NotBefore) - (built.NotAfter - built.NotBefore)).TotalDays) < 2,
                "the certificate lifetime differs from HostFiles.Certificate's");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Check("server.air-play-file-keeps-certificate")]
    public static void RebuildsKeepTheCertificateAndItsPin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-play-file-pin");
        try
        {
            var first = Path.Combine(root, "first");
            var pin = PlayerFile(first, TestHost, 8391);
            var pfx = Path.Combine(first, "platform", "platform-data", "remote", "host.pfx");
            var bytes = File.ReadAllBytes(pfx);
            TestAssert.Equal(pin, PlayerFile(first, TestHost, 8391), "writing Play.cmd again for the same folder changed the pin");
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(pfx)), "writing Play.cmd again replaced the certificate");

            // build-server.ps1 passes the existing -Output's host.pfx (or the operator's -Certificate) to the new build.
            var second = Path.Combine(root, "second");
            TestAssert.Equal(pin, PlayerFile(second, TestHost, 8391, pfx), "a build from the kept certificate changed the pin");
            TestAssert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(second, "platform", "platform-data", "remote", "host.pfx"))),
                "the kept certificate was not copied as it is");

            // A certificate the Platform cannot load (it reads host.pfx without a password) is refused before anything is written.
            var locked = Path.Combine(root, "locked.pfx");
            using (var certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.Exportable))
                File.WriteAllBytes(locked, certificate.Export(X509ContentType.Pfx, "secret"));
            var third = Path.Combine(root, "third");
            var refused = RunPlayerFile(third, TestHost, 8391, locked);
            TestAssert.True(refused.ExitCode != 0 && refused.Output.Contains("without a password", StringComparison.Ordinal), refused.Output);
            TestAssert.False(File.Exists(Path.Combine(third, "Play.cmd")), "a refused certificate still wrote Play.cmd");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Check("server.air-play-file-pinned-download", TimeoutSeconds = 300)]
    public static void PlayFileDownloadsOnlyThePinnedMatchingCompanion() => PinnedDownloadAsync().GetAwaiter().GetResult();

    private static async Task PinnedDownloadAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-air-play-download");
        try
        {
            // A Play.cmd for a loopback Platform, with the certificate the build makes, as the deployed Platform loads it.
            var bakedPort = AirBootstrapChecks.FreePort();
            var deploy = Path.Combine(root, "deploy");
            var pin = PlayerFile(deploy, "127.0.0.1", bakedPort);
            using var certificate = HostFiles.Certificate(Path.Combine(deploy, "platform", "platform-data", "remote"));
            var play = Path.Combine(deploy, "Play.cmd");
            var declarations = Path.Combine(root, "declarations.ps1");
            File.WriteAllText(declarations, Declarations(File.ReadAllText(play)), new UTF8Encoding(false));
            var driver = Path.Combine(root, "driver.ps1");
            File.WriteAllText(driver, Driver, new UTF8Encoding(false));
            var cache = Path.Combine(root, "player");
            var shell = WindowsPowerShell();

            // The real routes, without the friend launcher's template (a public build has none): the companion is still served.
            var firstPort = AirBootstrapChecks.FreePort();
            await using (var first = await StartPlatformAsync(Companion(root, "one"), firstPort, certificate))
            {
                using var http = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, served, _, _) => served?.GetCertHashString(HashAlgorithmName.SHA256) == pin,
                });
                TestAssert.Equal(HttpStatusCode.OK, (await http.GetAsync($"https://127.0.0.1:{firstPort}/bootstrap/client.json")).StatusCode,
                    "client.json must be served without the friend launcher's template");
                TestAssert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"https://127.0.0.1:{firstPort}/bootstrap/Play.cmd")).StatusCode,
                    "Play.cmd needs its template");
                var result = Drive(shell, driver, declarations, firstPort, pin, cache, "first");
                TestAssert.Equal(0, result.ExitCode, result.Output);
                TestAssert.True(result.Output.Contains("first-ok", StringComparison.Ordinal), result.Output);
            }
            var connection = Amberline.AirCompanion.Program.ReadRemoteInvitation(Path.Combine(cache, "server.json"));
            TestAssert.Equal("127.0.0.1", connection.Host);
            TestAssert.Equal(firstPort, connection.Port);
            TestAssert.Equal(pin, connection.CertificateSha25664hex);

            // A new companion on the server (another SHA-256) is downloaded again; a tampered archive is refused.
            var secondDirectory = Companion(root, "two");
            await using (var second = await StartPlatformAsync(secondDirectory, bakedPort, certificate))
            {
                var result = Drive(shell, driver, declarations, bakedPort, pin, cache, "update");
                TestAssert.Equal(0, result.ExitCode, result.Output);
                TestAssert.True(result.Output.Contains("update-ok", StringComparison.Ordinal), result.Output);
                // Batch 639: the download shows its progress in megabytes, in place ("\r"), up to the whole archive (2 MB here).
                TestAssert.True(Regex.IsMatch(result.Output, @"\rDownloading the player tool \(1 (of 2 )?MB\)\.\.\.") &&
                    Regex.IsMatch(result.Output, @"\rDownloading the player tool \(2 (of 2 )?MB\)\.\.\."), "the download's progress line: " + result.Output);

                File.Copy(Path.Combine(Companion(root, "three"), "companion.zip"), Path.Combine(secondDirectory, "companion.zip"), overwrite: true);
                result = Drive(shell, driver, declarations, bakedPort, pin, cache, "tampered");
                TestAssert.Equal(0, result.ExitCode, result.Output);
                TestAssert.True(result.Output.Contains("tampered-ok", StringComparison.Ordinal), result.Output);

                // The whole file through cmd.exe: the refusal reaches the player as a message, a non-zero exit and the pause; the host is
                // never printed. Nothing is written before the archive's SHA-256 matches, so the player folder stays untouched.
                var whole = RunCmd(play);
                TestAssert.Equal(1, whole.ExitCode, whole.Output);
                TestAssert.True(whole.Output.Contains("does not match the SHA-256", StringComparison.Ordinal) &&
                    whole.Output.Contains("Play did not finish (exit code 1). Press Enter to close.", StringComparison.Ordinal) &&
                    whole.Output.Contains("This takes a few seconds.", StringComparison.Ordinal), whole.Output);
                TestAssert.False(whole.Output.Contains("127.0.0.1", StringComparison.Ordinal), "Play.cmd printed its host");
            }
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
        TestAssert.Equal(defaults.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Regex.Match(source, @"\[int\]\$RemotePort = (\d+)").Groups[1].Value, "-RemotePort's default");
        TestAssert.Equal(defaults.FirstGamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Regex.Match(source, @"\[int\]\$FirstGamePort = (\d+)").Groups[1].Value, "-FirstGamePort's default");

        var root = AirBootstrapChecks.TemporaryDirectory("league-air-build-server-host");
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var air = Directory.CreateDirectory(Path.Combine(root, "air")).FullName;
            var output = Path.Combine(root, "out");
            string[] inputs = ["-File", script, "-GameRoot", game, "-AirRoot", air, "-Output", output];

            var missing = AirBootstrapChecks.RunPowerShell("pwsh", inputs);
            TestAssert.True(missing.ExitCode != 0 && missing.Output.Contains("PublicHost", StringComparison.Ordinal), missing.Output);
            foreach (var malformed in new[] { $"https://{TestHost}", $"{TestHost}/play", "amberline example.test", $"{TestHost}:8391", "" })
            {
                var refused = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", malformed]);
                TestAssert.True(refused.ExitCode != 0 && refused.Output.Contains("PublicHost", StringComparison.Ordinal),
                    $"-PublicHost {malformed} was not refused: {refused.Output}");
            }
            var loopback = AirBootstrapChecks.RunPowerShell("pwsh", [.. inputs, "-PublicHost", TestHost, "-RemotePort", "8390"]);
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

            var playerFile = RunPlayerFile(Path.Combine(root, "player"), $"https://{TestHost}", 8391, null);
            TestAssert.True(playerFile.ExitCode != 0 && playerFile.Output.Contains("PublicHost", StringComparison.Ordinal), playerFile.Output);
            TestAssert.False(Directory.Exists(Path.Combine(root, "player")), "player-file.ps1 wrote into a folder for a refused host");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Runs tools/player-file.ps1 and returns the pin it prints.</summary>
    private static string PlayerFile(string root, string host, int port, string? certificate = null)
    {
        var result = RunPlayerFile(root, host, port, certificate);
        TestAssert.Equal(0, result.ExitCode, result.Output);
        var pin = result.Output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length == 64) ?? string.Empty;
        TestAssert.True(Regex.IsMatch(pin, "^[0-9A-F]{64}$"), result.Output);
        return pin;
    }

    private static (int ExitCode, string Output) RunPlayerFile(string root, string host, int port, string? certificate)
    {
        string[] arguments = ["-File", AirBootstrapChecks.FindWorkspaceFile(Path.Combine("tools", "player-file.ps1")), "-Root", root,
            "-PublicHost", host, "-Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        return AirBootstrapChecks.RunPowerShell("pwsh", certificate is null ? arguments : [.. arguments, "-Certificate", certificate]);
    }

    /// <summary>Play.cmd's PowerShell part up to its main block: the variables and functions a driver can call.</summary>
    internal static string Declarations(string play)
    {
        const string marker = "# PLAY BOOTSTRAP";
        var body = play[(play.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Replace("\r\n", "\n", StringComparison.Ordinal);
        var main = body.IndexOf("\n$server = $null\ntry {", StringComparison.Ordinal);
        TestAssert.True(main >= 0, "Play.cmd's main block is missing");
        return body[..main];
    }

    internal static string WindowsPowerShell() =>
        AirBootstrapChecks.FindWindowsPowerShell() ?? throw new InvalidOperationException("Windows PowerShell 5.1 is required for the Play.cmd checks");

    /// <summary>
    /// A bootstrap folder whose companion.zip holds a stand-in executable, a version file and 2,000,000 random bytes (about 2 MB
    /// packed, so the download's progress line counts megabytes).
    /// </summary>
    private static string Companion(string root, string version)
    {
        var files = Directory.CreateDirectory(Path.Combine(root, "companion-" + version)).FullName;
        File.WriteAllText(Path.Combine(files, "Amberline.AirCompanion.exe"), "stand-in, never started");
        File.WriteAllText(Path.Combine(files, "version.txt"), version);
        File.WriteAllBytes(Path.Combine(files, "padding.bin"), RandomNumberGenerator.GetBytes(2_000_000));
        var bootstrap = Directory.CreateDirectory(Path.Combine(root, "bootstrap-" + version)).FullName;
        ZipFile.CreateFromDirectory(files, Path.Combine(bootstrap, "companion.zip"));
        return bootstrap;
    }

    /// <summary>The real /bootstrap routes on loopback HTTPS. <paramref name="advertisedPort"/> is the port client.json names (default: the listening one).</summary>
    internal static async Task<WebApplication> StartPlatformAsync(string bootstrap, int port, X509Certificate2 certificate, int? advertisedPort = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        PlatformBootstrap.Map(app, bootstrap, Path.Combine(bootstrap, "no-launcher-template.cmd"), "127.0.0.1", advertisedPort ?? port, certificate);
        await app.StartAsync();
        return app;
    }

    private static (int ExitCode, string Output) Drive(string shell, string driver, string declarations, int port, string pin, string cache, string phase) =>
        AirBootstrapChecks.RunPowerShell(shell, ["-File", driver, "-DeclarationsPath", declarations, "-Port",
            port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Pin", pin, "-Root", cache, "-Phase", phase,
            "-Closed", AirBootstrapChecks.FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    /// <summary>Runs Play.cmd through the 64-bit cmd.exe with no input, so its pause returns at once.</summary>
    private static (int ExitCode, string Output) RunCmd(string play)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", play }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Play.cmd did not finish");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Describe(X509Certificate2 certificate) => string.Join(";", certificate.Extensions
        .Select(extension => extension.Oid!.Value + (extension.Critical ? "!" : "") + ":" + Convert.ToHexString(extension.RawData))
        .Where(text => !text.StartsWith("2.5.29.14", StringComparison.Ordinal)));

    private const string Driver = """
param(
    [Parameter(Mandatory = $true)][string] $DeclarationsPath,
    [Parameter(Mandatory = $true)][int] $Port,
    [Parameter(Mandatory = $true)][string] $Pin,
    [Parameter(Mandatory = $true)][string] $Root,
    [Parameter(Mandatory = $true)][string] $Phase,
    [int] $Closed = 0
)
$ErrorActionPreference = 'Stop'
. $DeclarationsPath
# Windows messages are localized: only this file's own refusals are matched by their text.
function Assert-Refused([scriptblock]$Action, [string]$Text, [string]$What) {
    $message = $null
    try { [void](& $Action) } catch { $message = $_.Exception.Message }
    if ($null -eq $message) { throw "$What was accepted" }
    if ($Text -and $message.IndexOf($Text, [StringComparison]::Ordinal) -lt 0) { throw "$What was refused for another reason: $message" }
}
function Version([string]$Folder) { [IO.File]::ReadAllText((Join-Path $Folder 'version.txt')) }
switch ($Phase) {
    'first' {
        $companion = (Update-Companion '127.0.0.1' $Port $Pin $Root).Folder
        if ((Version $companion) -ne 'one') { throw 'the first download was not unpacked' }
        [IO.File]::WriteAllText((Join-Path $companion 'kept.txt'), 'kept')
        $again = (Update-Companion '127.0.0.1' $Port $Pin $Root).Folder
        if (-not (Test-Path -LiteralPath (Join-Path $again 'kept.txt'))) { throw 'an unchanged SHA-256 downloaded the companion again' }
        $other = Join-Path $Root 'other'
        Assert-Refused { Update-Companion '127.0.0.1' $Port ('0' * 64) $other } $null 'a wrong certificate pin'
        if (Test-Path -LiteralPath $other) { throw 'a wrong pin wrote the player folder' }
        # Batch 639: the player reads what happened and what to do, not Windows' words for a failed TLS or TCP connection.
        $message = $null
        try { [void](Update-Companion '127.0.0.1' $Port ('0' * 64) $other) } catch { $message = Get-PlainMessage $_.Exception }
        if ($message -notlike "*identity does not match this Play file*") { throw "a wrong pin reads: $message" }
        $message = $null
        try { [void](Get-PinnedBytes '127.0.0.1' $Closed $Pin '/bootstrap/client.json' 1MB) } catch { $message = Get-PlainMessage $_.Exception }
        if ($message -notlike "Can't reach the server*") { throw "an unreachable server reads: $message" }
        # Batch 619 (host design rule 2, replaces the refusal of batch 615): the pin proves it is the same server, so a manifest that
        # names another host is that server's own advice and becomes the connection's host.
        $advised = Update-Companion 'localhost' $Port $Pin $other
        if ($advised.Host -ne '127.0.0.1') { throw "the host the server advised was not kept: $($advised.Host)" }
        Remove-Item -LiteralPath $other -Recurse -Force
        Assert-Refused { [void](Get-PinnedBytes '127.0.0.1' $Port $Pin '/bootstrap/client.json' 1) } $null 'an oversized response'
        Write-Connection (Join-Path $Root 'server.json') '127.0.0.1' $Port $Pin
        Write-Output 'first-ok'
    }
    'update' {
        $companion = (Update-Companion '127.0.0.1' $Port $Pin $Root).Folder
        if ((Version $companion) -ne 'two') { throw 'a new SHA-256 on the server did not download the new companion' }
        if (Test-Path -LiteralPath (Join-Path $companion 'kept.txt')) { throw 'the old companion folder was kept' }
        Write-Output 'update-ok'
    }
    'tampered' {
        $fresh = Join-Path $Root 'fresh'
        Assert-Refused { Update-Companion '127.0.0.1' $Port $Pin $fresh } 'does not match the SHA-256' 'an archive that differs from its SHA-256'
        if (Test-Path -LiteralPath $fresh) { throw 'a refused archive wrote the player folder' }
        Write-Output 'tampered-ok'
    }
}
""";
}
