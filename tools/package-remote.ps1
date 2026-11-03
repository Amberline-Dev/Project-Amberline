[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [Parameter(Mandatory)][string]$GameDataRoot,
    [string]$BuildIdentity = ('patch126-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$DocumentationDirectory,
    [string]$ExistingHostDirectory,
    [switch]$ServerOnly,
    # Batch 611: the server's runtime; its natives are built for it (batch 634: Zig, native/*/build.ps1 -Runtime).
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($ServerOnly -and ($ExistingHostDirectory -or $DocumentationDirectory)) {
    throw '-ServerOnly does not prepare host data or guest documentation; omit -ExistingHostDirectory and -DocumentationDirectory.'
}
if (-not $ServerOnly -and $Runtime -ne 'win-x64') {
    throw 'The detached host and guest packages are Windows-only; use -ServerOnly for linux-x64.'
}
if (-not $OutputDirectory) {
    $packageName = if ($ServerOnly) { "server-update-$stamp" } else { "detached-$stamp" }
    $OutputDirectory = Join-Path $repository (Join-Path 'artifacts' $packageName)
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new output folder; existing packages are never overwritten.' }
$runtimeRoot = Join-Path $destination 'runtime'
# Batch 634: a folder without .git, or a PC without git, builds too; its source commit is unknown (as in build-server.ps1).
$gitCheckout = (Test-Path -LiteralPath (Join-Path $repository '.git')) -and [bool](Get-Command git -CommandType Application -ErrorAction SilentlyContinue)
$commit = if ($gitCheckout) { (& git -C $repository rev-parse HEAD) } else { 'unknown (no git checkout)' }
$localChanges = if ($gitCheckout) { [bool](& git -C $repository status --porcelain | Select-Object -First 1) } else { $null }
$data = (Resolve-Path -LiteralPath $GameDataRoot).Path
$build = Join-Path $repository (Join-Path '.artifacts' ('remote-package-' + [guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
$projects = if ($ServerOnly) { @('Amberline.Server') } else { @('Amberline.Server', 'Amberline.Host', 'Amberline.Launcher') }
foreach ($project in $projects) {
    $target = switch ($project) {
        'Amberline.Server' { Join-Path $runtimeRoot 'server' }
        'Amberline.Host' { $runtimeRoot }
        'Amberline.Launcher' { Join-Path $build 'guest' }
    }
    $remoteFlag = if ($project -eq 'Amberline.Launcher') { '-p:RemoteLauncher=true' } else { '-p:RemoteLauncher=false' }
    # Batch 611: the server is a self-contained folder. Single-file bundling plus the ReadyToRun cross-module flags (batch 573) crashed
    # the published server at its first JSON use (System.Buffers.ProbabilisticMapState); the same publish as a folder passes the preflight.
    $bundle = if ($project -eq 'Amberline.Server') { @() } else { @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
    & dotnet publish (Join-Path $repository "src/$project/$project.csproj") -c Release -r $Runtime --self-contained true --artifacts-path (Join-Path $build $project) -o $target -p:PublishTrimmed=false @bundle "-p:InformationalVersion=$BuildIdentity" $remoteFlag
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
}
$serverDirectory = Join-Path $runtimeRoot 'server'
$serverExecutable = if ($Runtime -eq 'win-x64') { 'Amberline.Server.exe' } else { 'Amberline.Server' }
# Natives beside the server: the server finds amberline_enet and exehost in its own folder (LeagueEnetTransport.cs, ExeHostProcess.cs).
# Batch 634: Zig builds them for either runtime (native/*/build.ps1 -Runtime), so neither LLVM, the C++ Build Tools nor sh is needed.
# Batch 635: -ServerOnly is build-server.ps1's server stage, so this prints its stage line 3 of 7 (tools/guide.ps1 shows it as a bar).
if ($ServerOnly) { Write-Host 'AMBERLINE-STAGE 3/7 Building the native parts' }
& (Join-Path $repository 'native/enet/build.ps1') -Runtime $Runtime -OutputDirectory $serverDirectory
# Batch 608: the helper that runs the client's own movement code from the player's EXE (without it the server uses its fallback mover).
& (Join-Path $repository 'native/exehost/build.ps1') -Runtime $Runtime -OutputDirectory $serverDirectory
if ($ServerOnly) {
    $manifest = [ordered]@{
        component = 'server'
        runtime = $Runtime
        buildIdentity = $BuildIdentity
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        sourceCommit = $commit
        sourceHasLocalChanges = $localChanges
        files = @(Get-ChildItem -LiteralPath $serverDirectory -File | Sort-Object Name |
            ForEach-Object { [ordered]@{ path = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $serverDirectory 'BUILD.json') -Encoding utf8
    # The preflight runs the server on this host, so only a server for this host's runtime can take it.
    if ($Runtime -eq 'win-x64' -and $IsWindows) {
        $previousData = $env:GAME_DATA_ROOT
        $previousNative = $env:AMBERLINE_ENET_NATIVE
        try {
            $env:GAME_DATA_ROOT = $data
            $env:AMBERLINE_ENET_NATIVE = Join-Path $serverDirectory 'amberline_enet.dll'
            & (Join-Path $serverDirectory $serverExecutable) --solo --ephemeral --validate-only --data-root $data --log-dir (Join-Path $build 'validation')
            if ($LASTEXITCODE -ne 0) { throw 'Published server preflight failed.' }
        } finally {
            $env:GAME_DATA_ROOT = $previousData
            $env:AMBERLINE_ENET_NATIVE = $previousNative
        }
    } else {
        Write-Host "Preflight not run: the $Runtime server runs on that system only (run server --validate-only there)."
    }
    # Batch 611: the publish intermediates (obj, the preflight log folder) are regenerable; the staged server is in $destination.
    Remove-Item -LiteralPath $build -Recurse -Force
    Write-Host "Staged server: $serverDirectory"
    return
}
$guest = Join-Path $runtimeRoot 'guest-template'
New-Item -ItemType Directory -Path $guest | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'guest/Amberline.Launcher.exe') -Destination (Join-Path $guest 'Launcher.exe')
$manifest = [ordered]@{
    buildIdentity = $BuildIdentity
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceCommit = $commit
    sourceHasLocalChanges = $localChanges
    files = @('Amberline.Host.exe', 'server/Amberline.Server.exe', 'server/amberline_enet.dll', 'guest-template/Launcher.exe') |
        ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeRoot $_) -Algorithm SHA256).Hash } }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runtimeRoot 'BUILD.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $runtimeRoot 'BUILD.json') -Destination $guest
if ($DocumentationDirectory) {
    foreach ($document in @('NATIVE-CHECKLIST.md', 'DIAGNOSTICS.md', 'CHANGELOG.md')) {
        $source = Join-Path $DocumentationDirectory $document
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeRoot $document)
            Copy-Item -LiteralPath $source -Destination (Join-Path $guest $document)
        }
    }
}
$previousData = $env:GAME_DATA_ROOT
$previousNative = $env:AMBERLINE_ENET_NATIVE
$previousHost = $env:AMBERLINE_HOST_HOME
try {
    $env:GAME_DATA_ROOT = $data
    $env:AMBERLINE_ENET_NATIVE = Join-Path $runtimeRoot 'server/amberline_enet.dll'
    $env:AMBERLINE_HOST_HOME = Join-Path $runtimeRoot 'host-data'
    & (Join-Path $runtimeRoot 'server/Amberline.Server.exe') --solo --ephemeral --validate-only --data-root $data --log-dir (Join-Path $build 'validation')
    if ($LASTEXITCODE -ne 0) { throw 'Published server preflight failed.' }
    if ($ExistingHostDirectory) {
        $existing = (Resolve-Path -LiteralPath $ExistingHostDirectory).Path
        foreach ($required in @('host.json', 'guests.json', 'invitations')) {
            if (-not (Test-Path -LiteralPath (Join-Path $existing $required))) { throw "Missing retained host data: $required" }
        }
        Copy-Item -LiteralPath $existing -Destination $env:AMBERLINE_HOST_HOME -Recurse
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        foreach ($export in Get-ChildItem -LiteralPath (Join-Path $env:AMBERLINE_HOST_HOME 'invitations') -Directory) {
            if (-not (Test-Path -LiteralPath (Join-Path $export.FullName 'invitation.json'))) { continue }
            foreach ($file in Get-ChildItem -LiteralPath $guest -File) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $export.FullName $file.Name) -Force
            }
            $archive = $export.FullName + '.zip'
            if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
            [IO.Compression.ZipFile]::CreateFromDirectory($export.FullName, $archive)
        }
    } else {
        & (Join-Path $runtimeRoot 'Amberline.Host.exe') --init $data
        if ($LASTEXITCODE -ne 0) { throw 'Host initialization failed.' }
    }
} finally {
    $env:GAME_DATA_ROOT = $previousData
    $env:AMBERLINE_ENET_NATIVE = $previousNative
    $env:AMBERLINE_HOST_HOME = $previousHost
}
Write-Host "Prepared host: $destination"
Write-Host 'The administrator launcher is in runtime\host-data\invitations. Guest packages are retired; friends use Play.cmd.'
Write-Host 'Allow TCP 5120 and UDP 5119-5122 through the host firewall/router. No firewall settings were changed.'
