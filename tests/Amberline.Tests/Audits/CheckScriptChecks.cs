using System.Diagnostics;
using System.Text;

namespace Amberline.Tests;

public static class CheckScriptChecks
{
    [Check("tools.check-script-native-helper")]
    public static void NativeCheckHelperUsesTheProductionPathOnWindowsPowerShell51()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = FindWindowsPowerShell();
        TestAssert.True(shell is not null, "Windows PowerShell 5.1 is required for the check helper regression");
        var helper = FindWorkspaceFile(Path.Combine("tools", "check-native.ps1"));
        var root = Path.Combine(Path.GetTempPath(), "amberline-check-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = Path.Combine(root, "driver.ps1");
        File.WriteAllText(driver, Driver, new UTF8Encoding(false));
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = shell!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(driver);
            start.ArgumentList.Add(helper);
            start.ArgumentList.Add(root);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("failed to start Windows PowerShell 5.1");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("check native helper regression timed out");
            }
            TestAssert.Equal(0, process.ExitCode, stdout.Result + stderr.Result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static readonly string Driver = @"
param(
    [Parameter(Mandatory = $true)][string] $HelperPath,
    [Parameter(Mandatory = $true)][string] $Root
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'the regression must run under Windows PowerShell 5.1'
}
. $HelperPath

$successLog = Join-Path $Root 'success.log'
$ErrorActionPreference = 'Stop'
Invoke-CheckNative -FilePath $env:ComSpec -ArgumentList @('/d', '/s', '/c',
    'echo stdout & echo stderr 1>&2 & echo after') -LogPath $successLog
if ($ErrorActionPreference -ne 'Stop') { throw 'success did not restore ErrorActionPreference' }
$success = Get-Content -LiteralPath $successLog -Raw
if ($success.IndexOf('stdout', [StringComparison]::Ordinal) -lt 0 -or
    $success.IndexOf('stderr', [StringComparison]::Ordinal) -lt 0 -or
    $success.IndexOf('after', [StringComparison]::Ordinal) -lt 0) {
    throw 'native stdout/stderr/after markers were not retained'
}

$ErrorActionPreference = 'SilentlyContinue'
$nonZero = $false
$failureMessage = $null
$nonZeroLog = Join-Path $Root 'nonzero.log'
try {
    Invoke-CheckNative -FilePath $env:ComSpec -ArgumentList @('/d', '/s', '/c',
        'echo stdout & echo stderr 1>&2 & echo after & exit /b 7') `
        -LogPath $nonZeroLog
}
catch {
    $nonZero = $true
    $failureMessage = $_.Exception.Message
    if ($ErrorActionPreference -ne 'SilentlyContinue') { throw 'nonzero did not restore ErrorActionPreference' }
}
if (-not $nonZero) { throw 'nonzero native exit was accepted' }
$nonZeroOutput = Get-Content -LiteralPath $nonZeroLog -Raw
if ($nonZeroOutput.IndexOf('stdout', [StringComparison]::Ordinal) -lt 0 -or
    $nonZeroOutput.IndexOf('stderr', [StringComparison]::Ordinal) -lt 0 -or
    $nonZeroOutput.IndexOf('after', [StringComparison]::Ordinal) -lt 0) {
    throw 'nonzero native stdout/stderr/after markers were not retained'
}
if ($failureMessage -notmatch 'exit code 7(?:;|$)') {
    throw ('native exit did not report 7: ' + $failureMessage)
}

$ErrorActionPreference = 'Continue'
$logFailure = Join-Path $Root 'log-directory'
New-Item -ItemType Directory -Path $logFailure | Out-Null
$writeFailed = $false
try {
    Invoke-CheckNative -FilePath $env:ComSpec -ArgumentList @('/d', '/c', 'echo logging-failure') `
        -LogPath $logFailure
}
catch {
    $writeFailed = $true
    if ($ErrorActionPreference -ne 'Continue') { throw 'log failure did not restore ErrorActionPreference' }
}
if (-not $writeFailed) { throw 'log-write failure was accepted' }
";

    private static string? FindWindowsPowerShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "powershell.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var fallback = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static string FindWorkspaceFile(string relativePath)
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
}
