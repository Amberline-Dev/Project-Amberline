<#
.SYNOPSIS
Makes or keeps the Platform's TLS certificate, which the players' invite link pins, and returns its SHA-256.

.DESCRIPTION
Batch 615; batch 917 removed Play.cmd from it (the invite link replaced Play.cmd: tools/build-server.ps1 writes it to INVITE-LINK.txt).
tools/build-server.ps1 runs it on its output. The certificate is <Root>/platform/platform-data/remote/host.pfx, where the deployed Platform
loads it. HostFiles.Certificate (src/Amberline.Host/HostSettings.cs) makes one only when that file is missing, so the deployed server keeps
this one, and every player's invite link and shortcut keep working as long as the file stays. -Certificate copies an existing one there;
without it the one already in Root is kept, else a new one is made the way HostFiles.Certificate makes it.

.PARAMETER Root
The deployment folder (the -Output of tools/build-server.ps1).

.PARAMETER Certificate
An existing host.pfx to keep (the one a running server has in platform-data/remote). It must load without a password.

.EXAMPLE
pwsh -NoProfile -File ./host/tools/player-file.ps1 -Root <deployment folder>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Certificate
)
$ErrorActionPreference = 'Stop'

# The pin, read the way the Platform loads the file (HostFiles.Certificate: PKCS#12 without a password).
function Get-Pin([string]$Path) {
    try { $certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile($Path, $null) }
    catch { throw "The certificate $Path cannot be read without a password, as the Platform reads it: $($_.Exception.Message)" }
    try {
        if (-not $certificate.HasPrivateKey) { throw "The certificate $Path holds no private key; the Platform serves HTTPS with it." }
        if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw "The certificate $Path has expired; the player app refuses an expired one." }
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
Get-Pin $pfx
