<#
.SYNOPSIS
Final verification: a fresh isolated Release build, the matching native ENet bridge,
then the test runner.

.DESCRIPTION
Creates a new .artifacts/check-<stamp>-<id>/ (build/, build.log, native-build.log,
checks.log), builds into it, and runs the checks with GAME_DATA_ROOT and
AMBERLINE_ENET_NATIVE set. No selectors runs the complete suite; -Scope and -Check form
a union, each check runs once. See docs/testing.md.

The checks that need no port, process, file or environment run on -Parallel long-lived
worker processes, longest first; the serial checks (server.*, transport.*, tools.*
and every [Check(..., Serial = true)]) then run alone, each in its own process.
Fixture recording (AMBERLINE_RECORD_FIXTURES=1) always runs serially.

.PARAMETER Scope
Scope names (repeatable), for example movement,minions. See --list-scopes.

.PARAMETER Check
Exact check names or globs (* and ?), for example 'server.air-*'.

.PARAMETER OutputDirectory
A new directory for the artifacts (relative to emu/ or absolute); an existing path is rejected.

.PARAMETER GameDataRoot
The player's own 1.0.0.126 release folder (DATA, LEVELS). Without it the checks run with no game data: the data-free
checks pass and the others fail on the missing files.

.PARAMETER AirClientRoot
The player's own 1.0.0.126 AIR client folder (the one holding LolClient.swf); the champion list is read from it.

.PARAMETER Parallel
Worker processes for the non-serial checks, 1 to 64 (default 4). -Parallel 1 runs every
check in its own child process, one at a time, exactly as before.
#>
[CmdletBinding()]
param(
    [string[]]$Scope = @(),
    [string[]]$Check = @(),
    [string]$OutputDirectory,
    [string]$GameDataRoot,
    [string]$AirClientRoot,
    [ValidateRange(1, 64)]
    [int]$Parallel = 4
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'check-native.ps1')
$repository = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = '.artifacts/check-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
}
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repository $OutputDirectory
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; verification artifacts are immutable.' }
$data = if ($GameDataRoot) { (Resolve-Path -LiteralPath $GameDataRoot).Path } else { $null }
$air = if ($AirClientRoot) { (Resolve-Path -LiteralPath $AirClientRoot).Path } else { $null }
$build = Join-Path $output 'build'
$native = Join-Path $build 'bin/Amberline.Server/release/amberline_enet.dll'
$checks = Join-Path $build 'bin/Amberline.Tests/release/Amberline.Tests.dll'
$selection = @('--parallel', [string]$Parallel)
foreach ($name in $Scope) { $selection += @('--scope', $name) }
$selection += $Check

$previousData = $env:GAME_DATA_ROOT
$previousAir = $env:AIR_CLIENT_ROOT
$previousNative = $env:AMBERLINE_ENET_NATIVE
$previousExeHost = $env:AMBERLINE_EXEHOST
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location -LiteralPath $repository
try {
    $env:GAME_DATA_ROOT = $data
    $env:AIR_CLIENT_ROOT = $air
    Invoke-CheckNative -FilePath 'dotnet' -ArgumentList @(
        'build', 'Amberline.slnx', '-c', 'Release', '--artifacts-path', $build) `
        -LogPath (Join-Path $output 'build.log')

    & (Join-Path $repository 'native/enet/build.ps1') -OutputDirectory (Split-Path -Parent $native) 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'native-build.log')
    $env:AMBERLINE_ENET_NATIVE = $native
    # Batch 608: the x86 helper that runs the client's own movement code beside the server, as amberline_enet.dll is (the server finds it there;
    # the checks through AMBERLINE_EXEHOST).
    $exeHost = Split-Path -Parent $native
    & (Join-Path $repository 'native/exehost/build.ps1') -OutputDirectory $exeHost 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'native-build.log') -Append
    $env:AMBERLINE_EXEHOST = Join-Path $exeHost 'exehost.exe'
    Invoke-CheckNative -FilePath 'dotnet' -ArgumentList (@($checks) + $selection) `
        -LogPath (Join-Path $output 'checks.log')
    Write-Output "Verification artifacts: $output"
} finally {
    $env:GAME_DATA_ROOT = $previousData
    $env:AIR_CLIENT_ROOT = $previousAir
    $env:AMBERLINE_ENET_NATIVE = $previousNative
    $env:AMBERLINE_EXEHOST = $previousExeHost
    Pop-Location
}
