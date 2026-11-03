using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Amberline.Tests.Air;

public static class AirBootstrapChecks
{



    private static string? PrepareOutcome(string source, string destination)
    {
        try
        {
            Amberline.AirCompanion.AirOverlay.Prepare(new Amberline.AirCompanion.AirOverlayOptions(source, destination));
            return null;
        }
        catch (IOException error) { return error.Message; }
    }

    private static string Declarations(string source)
    {
        const string marker = "# POWERSHELL BOOTSTRAP";
        var markerIndex = source.LastIndexOf(marker, StringComparison.Ordinal);
        TestAssert.True(markerIndex >= 0, "bootstrap marker is missing");
        var body = source[(markerIndex + marker.Length)..].Replace("\r\n", "\n", StringComparison.Ordinal);
        var mainTry = body.IndexOf("\ntry {\n    if ($BakedHost", StringComparison.Ordinal);
        TestAssert.True(mainTry >= 0, "bootstrap top-level body is missing");
        return body[..mainTry];
    }

    private const string LayoutDriver = """
param(
    [Parameter(Mandatory = $true)][string] $DeclarationsPath,
    [Parameter(Mandatory = $true)][string] $Paths
)
$ErrorActionPreference = 'Stop'
. $DeclarationsPath
foreach ($path in $Paths.Split([char]'|')) {
    $problems = @(Get-AirSourceProblems $path)
    Write-Output ('layout|' + (Split-Path -Leaf $path) + '|' + ($problems -join ';'))
}
Write-Output 'layout-check-ok'
""";


    private static (int ExitCode, string Output) Invoke(string shell, string driver, string declarations,
        string cacheRoot, int port, string fingerprint, string hostToken) =>
        RunPowerShell(shell, ["-File", driver, "-DeclarationsPath", declarations, "-CacheRoot", cacheRoot,
            "-Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Fingerprint", fingerprint,
            "-HostToken", hostToken]);

    internal static (int ExitCode, string Output) RunPowerShell(string shell, string[] scriptArguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(scriptArguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start Windows PowerShell 5.1");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("bootstrap PowerShell regression timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    internal static string? FindWindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "powershell.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var fallback = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=air-bootstrap-check", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static string FindWorkspaceFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var direct = Path.Combine(current.FullName, relativePath);
                if (File.Exists(direct)) return direct;
                var nested = Path.Combine(current.FullName, "emu", relativePath);
                if (File.Exists(nested)) return nested;
            }
        }
        throw new FileNotFoundException(relativePath);
    }

    internal static string TemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
