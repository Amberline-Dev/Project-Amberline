@echo off
rem NOT RETAIL: the player's one file. Retail players installed the client with the publisher's patcher and signed in to its servers;
rem this file downloads the player tool from the operator's server over pinned HTTPS and starts its installer, which puts the
rem player's clients in one folder and makes the desktop shortcut that plays on this server.
rem tools/player-file.ps1 bakes the server into this template when tools/build-server.ps1 builds the server.
setlocal DisableDelayedExpansion
set "PLAY_SELF=%~f0"
rem The 64-bit Windows PowerShell 5.1 only: from a 32-bit cmd, Sysnative is the real System32, never the 32-bit one.
set "PLAY_SHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PLAY_SHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%PLAY_SHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$text=[IO.File]::ReadAllText($env:PLAY_SELF); $marker='# PLAY BOOTSTRAP'; $start=$text.LastIndexOf($marker,[StringComparison]::Ordinal); if($start -lt 0){throw 'Play.cmd is damaged: its PowerShell part is missing.'}; & ([ScriptBlock]::Create($text.Substring($start+$marker.Length)))"
set "PLAY_EXIT=%ERRORLEVEL%"
if not "%PLAY_EXIT%"=="0" (
    echo.
    echo Play did not finish ^(exit code %PLAY_EXIT%^). Press Enter to close.
    pause >nul
)
exit /b %PLAY_EXIT%

# PLAY BOOTSTRAP
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ponytail: obfuscation, not secrecy. The host is Base64(host XOR 0x5A), as in the platform's friend file, so it is not plain text;
# anyone holding this file can decode it.
$HostToken = '__HOST__'
$PortText = '__PORT__'
$Pin = '__CERT__'
# The server's display name: the desktop shortcut's name (tools/build-server.ps1 -ServerName).
$Name = '__NAME__'
$Executable = 'Amberline.AirCompanion.exe'

# The friend launcher's pinned download (tools/air-client-bootstrap.cmd): HTTPS to the baked host and port only, the server
# certificate's SHA-256 must equal the pin, no redirect, no proxy, a size limit. There is no unpinned fallback. Batch 639 added the
# progress line ("Downloading the player tool (12 of 48 MB)..."), written in place while the bytes arrive.
if ($null -eq ('AmberlineBootstrap.PinnedDownload' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AmberlineBootstrap
{
    public static class PinnedDownload
    {
        static string Megabytes(long bytes)
        {
            return ((bytes + 524288) / 1048576).ToString();
        }

        public static byte[] Get(Uri uri, string expectedHost, int expectedPort,
            string expectedCertificate, long maximumBytes, string progress)
        {
            if (!String.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                uri.Port != expectedPort)
                throw new InvalidOperationException("Refusing an unpinned bootstrap URL.");
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException("maximumBytes");

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Proxy = null;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
            {
                if (certificate == null) return false;
                using (var certificate2 = new X509Certificate2(certificate))
                using (var sha = SHA256.Create())
                {
                    var now = DateTime.UtcNow;
                    if (now < certificate2.NotBefore.ToUniversalTime() ||
                        now > certificate2.NotAfter.ToUniversalTime()) return false;
                    var actual = BitConverter.ToString(sha.ComputeHash(certificate2.RawData))
                        .Replace("-", String.Empty).ToUpperInvariant();
                    return String.Equals(actual, expectedCertificate,
                        StringComparison.OrdinalIgnoreCase);
                }
            };

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode != 200)
                    throw new InvalidOperationException("Bootstrap server returned HTTP " +
                        (int)response.StatusCode + ".");
                if (response.ContentLength > maximumBytes)
                    throw new InvalidOperationException("Bootstrap response is too large.");
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    var shown = String.Empty;
                    var size = response.ContentLength > 0 ? " of " + Megabytes(response.ContentLength) : String.Empty;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > maximumBytes)
                            throw new InvalidOperationException("Bootstrap response is too large.");
                        output.Write(buffer, 0, read);
                        if (String.IsNullOrEmpty(progress) || Megabytes(total) == shown) continue;
                        shown = Megabytes(total);
                        Console.Write("\r" + progress + " (" + shown + size + " MB)...");
                    }
                    if (!String.IsNullOrEmpty(progress)) Console.WriteLine();
                    return output.ToArray();
                }
            }
        }
    }
}
'@
}

function ConvertFrom-HostToken([string]$Token) { -join ([Convert]::FromBase64String($Token) | ForEach-Object { [char]($_ -bxor 0x5A) }) }

function Get-PinnedBytes([string]$Server, [int]$Port, [string]$Certificate, [string]$Path, [long]$Limit, [string]$Label = '') {
    $uri = New-Object System.Uri ('https://' + $Server + ':' + $Port + $Path)
    return ,([AmberlineBootstrap.PinnedDownload]::Get($uri, $Server, $Port, $Certificate, $Limit, $Label))
}

# What happened and what to do, in the player's words. A failed connection names its likely cause: no answer from the server, or a
# certificate other than the one this file pins. Anything else keeps its own message.
function Get-PlainMessage([Exception]$Exception) {
    for ($inner = $Exception; $null -ne $inner; $inner = $inner.InnerException) {
        if ($inner -isnot [Net.WebException]) { continue }
        $status = [string]$inner.Status
        if ($status -in 'TrustFailure', 'SecureChannelFailure') {
            return "The server's identity does not match this Play file. Ask the host for a new Play file."
        }
        if ($status -in 'ConnectFailure', 'NameResolutionFailure', 'Timeout', 'ConnectionClosed', 'ReceiveFailure', 'SendFailure', 'KeepAliveFailure') {
            return "Can't reach the server. Check that you are online and that the host's server is on, then double-click Play.cmd again."
        }
    }
    return $Exception.Message
}

function Get-Sha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '') } finally { $sha.Dispose() }
}

# The companion folder under Root (Folder) and the server's address (Host). The folder is downloaded and unpacked again only when
# the SHA-256 the server publishes differs from the copy's; the archive must match that SHA-256 before anything is written. A
# manifest naming another host is the server's own advice (the pin proves it is the same server): Host is that host. Another
# port or pin is refused.
function Update-Companion([string]$Server, [int]$Port, [string]$Certificate, [string]$Root) {
    $manifest = [Text.Encoding]::UTF8.GetString((Get-PinnedBytes $Server $Port $Certificate '/bootstrap/client.json' 1MB)) | ConvertFrom-Json
    if ($manifest.protocol -ne 1 -or $manifest.port -ne $Port -or $manifest.certificateSha256 -ne $Certificate) {
        throw 'This Play file does not match the server any more. Ask the host for a new Play file.'
    }
    $advised = [string]$manifest.host
    if ($advised -notmatch '^[A-Za-z0-9.-]{1,253}$' -or [Uri]::CheckHostName($advised) -notin 'Dns', 'IPv4') {
        throw 'The server sent no valid address. Ask the host for a new Play file.'
    }
    $sha = ([string]$manifest.companionSha256).ToUpperInvariant()
    if ($sha -notmatch '^[0-9A-F]{64}$') { throw 'The server sent no valid SHA-256 for the player tool. Ask the host to update the server.' }
    $companion = Join-Path $Root 'companion'
    $mark = Join-Path $Root 'companion.sha256'
    if ((Test-Path -LiteralPath (Join-Path $companion $Executable) -PathType Leaf) -and (Test-Path -LiteralPath $mark -PathType Leaf) -and
        ([IO.File]::ReadAllText($mark).Trim() -eq $sha)) { return [pscustomobject]@{ Folder = $companion; Host = $advised } }
    $zip = Get-PinnedBytes $Server $Port $Certificate '/bootstrap/companion.zip' 512MB 'Downloading the player tool'
    if ((Get-Sha256 $zip) -ne $sha) {
        throw 'The downloaded player tool does not match the SHA-256 the server published. Nothing was changed; double-click Play.cmd again.'
    }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $next = $companion + '.next'
    if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Recurse -Force }
    $download = Join-Path $Root 'companion.zip.download'
    [IO.File]::WriteAllBytes($download, $zip)
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($download, $next)
    } finally { Remove-Item -LiteralPath $download -Force }
    if (-not (Test-Path -LiteralPath (Join-Path $next $Executable) -PathType Leaf)) {
        Remove-Item -LiteralPath $next -Recurse -Force
        throw "The player tool from the server has no $Executable. Ask the host to update the server."
    }
    if (Test-Path -LiteralPath $companion) { Remove-Item -LiteralPath $companion -Recurse -Force }
    Move-Item -LiteralPath $next -Destination $companion
    [IO.File]::WriteAllText($mark, $sha)
    return [pscustomobject]@{ Folder = $companion; Host = $advised }
}

# The connection file the companion's installer reads (--connection): host, HTTPS port and pin, no token.
function Write-Connection([string]$Path, [string]$Server, [int]$Port, [string]$Certificate) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json @{ host = $Server; port = $Port; certificateSha256 = $Certificate }))
}

$server = $null
try {
    if ($HostToken.StartsWith('__')) { throw 'This Play file was not made for a server. Ask the host for theirs.' }
    $server = ConvertFrom-HostToken $HostToken
    $port = [int]$PortText
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($local)) { throw 'Windows has no app data folder for your user. Sign in to Windows with your own account, then try again.' }
    # The per-user folder of the player tool, where PlayerInstall keeps its record (install.json): FolderMove.PlayerFolder, a name
    # apart from the host's Amberline folder.
    $root = Join-Path $local 'Amberline Player'
    Write-Host "Connecting to $Name. This takes a few seconds."
    $tool = Update-Companion $server $port $Pin $root
    $connection = Join-Path $root 'server.json'
    Write-Connection $connection $tool.Host $port $Pin
    Write-Host 'Opening the setup window. This takes a few seconds. Finish the setup there.'
    & (Join-Path $tool.Folder $Executable) --install --connection $connection --server-name $Name
    exit $LASTEXITCODE
}
catch {
    $message = Get-PlainMessage $_.Exception
    if ($server) { $message = $message.Replace($server, 'the server') }
    Write-Host ''
    Write-Host ('Play could not finish: ' + $message) -ForegroundColor Yellow
    exit 1
}
