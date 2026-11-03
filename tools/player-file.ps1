<#
.SYNOPSIS
Writes the one file a player needs, <Root>/Play.cmd, and the Platform's TLS certificate that it pins. Returns the certificate's SHA-256.

.DESCRIPTION
Batch 615. tools/build-server.ps1 runs it on its output; run it alone to write Play.cmd again for an existing deployment folder.
The certificate is <Root>/platform/platform-data/remote/host.pfx, where the deployed Platform loads it. HostFiles.Certificate
(src/Amberline.Host/HostSettings.cs) makes one only when that file is missing, so the deployed server keeps this one, and every
player's Play.cmd keeps working as long as the file stays. -Certificate copies an existing one there; without it the one already in
Root is kept, else a new one is made the way HostFiles.Certificate makes it.
Play.cmd is tools/play-bootstrap.cmd with the host (Base64 of host XOR 0x5A, as PlatformBootstrap.RenderLauncher writes
Play.cmd), the HTTPS port, the certificate's SHA-256 and the server's display name put in.

.PARAMETER Root
The deployment folder (the -Output of tools/build-server.ps1).

.PARAMETER PublicHost
The DNS name or IPv4 address players reach the server on: no scheme, port, path or spaces.

.PARAMETER Port
The Platform's public HTTPS port (platform.json remotePort).

.PARAMETER Certificate
An existing host.pfx to keep (the one a running server has in platform-data/remote). It must load without a password.

.PARAMETER ServerName
The server's display name, the name of the players' desktop shortcut. Default Amberline.

.EXAMPLE
pwsh -NoProfile -File ./tools/player-file.ps1 -Root <deployment folder> -PublicHost <your server's address> -Port 8391
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$PublicHost,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port,
    [string]$Certificate,
    [string]$ServerName = 'Amberline'
)
$ErrorActionPreference = 'Stop'
# PlatformBootstrap.IsHost's rule: the Platform serves the player's download only for such a host.
if ($PublicHost -notmatch '^[A-Za-z0-9.-]{1,253}\z' -or [uri]::CheckHostName($PublicHost) -notin 'Dns', 'IPv4') {
    throw '-PublicHost must be the bare DNS name or IPv4 address players reach (no scheme, port, path or spaces).'
}
# PlayerInstall.ServerName's rule (a desktop file name): keep the two in step.
if ($ServerName -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?\z' -or $ServerName -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z') {
    throw '-ServerName must be 1 to 40 letters, digits, spaces, dots, hyphens or underscores, starting and ending with a letter or digit.'
}

# The pin, read the way the Platform loads the file (HostFiles.Certificate: PKCS#12 without a password).
function Get-Pin([string]$Path) {
    try { $certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($Path, $null) }
    catch { throw "The certificate $Path cannot be read without a password, as the Platform reads it: $($_.Exception.Message)" }
    try {
        if (-not $certificate.HasPrivateKey) { throw "The certificate $Path holds no private key; the Platform serves HTTPS with it." }
        if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw "The certificate $Path has expired; Play.cmd refuses an expired one." }
        $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    } finally { $certificate.Dispose() }
}

$remote = Join-Path $Root 'platform/platform-data/remote'
$pfx = Join-Path $remote 'host.pfx'
if ($Certificate) {
    $source = (Resolve-Path -LiteralPath $Certificate).Path
    [void](Get-Pin $source)
}
New-Item -ItemType Directory -Force -Path $remote | Out-Null
if ($Certificate) {
    if ($source -ne [IO.Path]::GetFullPath($pfx)) { Copy-Item -LiteralPath $source -Destination $pfx -Force }
} elseif (-not (Test-Path -LiteralPath $pfx -PathType Leaf)) {
    # The same key, subject, extensions and lifetime (HostFiles.CertificateYears, 20 years) as HostFiles.Certificate; keep the two
    # in step. Only a missing file is made: an existing host.pfx keeps its pin and its own lifetime.
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Amberline host', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $usages = [Security.Cryptography.OidCollection]::new()
        [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
        $now = [DateTimeOffset]::UtcNow
        $created = $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddYears(20))
        try { [IO.File]::WriteAllBytes($pfx, $created.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx)) }
        finally { $created.Dispose() }
    } finally { $rsa.Dispose() }
}
$pin = Get-Pin $pfx

# CRLF whatever the checkout's line endings: cmd.exe reads the file.
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'play-bootstrap.cmd')).Replace("`r`n", "`n").Replace("`n", "`r`n")
$token = [Convert]::ToBase64String([byte[]]@([Text.Encoding]::ASCII.GetBytes($PublicHost) | ForEach-Object { $_ -bxor 0x5A }))
$text = $template.Replace('__HOST__', $token).Replace('__PORT__', [string]$Port).Replace('__CERT__', $pin).Replace('__NAME__', $ServerName)
[IO.File]::WriteAllText((Join-Path $Root 'Play.cmd'), $text, [Text.Encoding]::ASCII)
$pin
