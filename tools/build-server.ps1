<#
.SYNOPSIS
Builds the deployable server: server/ and platform/ for one runtime, the natives beside the server, the configuration with the
server's public address, the Platform's TLS certificate, the players' companion archive, Play.cmd (the one file a player needs),
README-DEPLOY.txt and BUILD.json. Checks that no file of the game or AIR folders went into the output.
With -Layout Home it builds the Amberline release zip for a host's own PC instead (batch 625, see the .DESCRIPTION).

.DESCRIPTION
Batch 611. The operator supplies the stock 1.0.0.126 game folder (-GameRoot: the game executable, DATA, LEVELS) and the
1.0.0.126 AIR client folder (-AirRoot: its .swf file, assets). Neither folder is copied. The build reads them to check them, and
the final scan fails if an output file, or an entry of the companion archive, has the SHA-256 of a file in either folder or a game
file extension. Batch 635: when the repository, the output or the work folder lies inside either folder, its files are left out of
that folder's side of the comparison (they are Amberline's, not the game's). Each stage prints an AMBERLINE-STAGE n/7 line.
The server comes from tools/package-remote.ps1 -ServerOnly (self-contained, ReadyToRun, natives beside it); the Platform is
published here. The natives are built for the runtime with Zig (batch 634: native/*/build.ps1 -Runtime, for win-x64 and
linux-x64 alike). The server's --validate-only preflight runs only for win-x64 on Windows. Nothing is started.
Batch 615: the operator also names the address players reach (-PublicHost). The build makes the Platform's TLS certificate
(platform/platform-data/remote/host.pfx, where the Platform loads it), publishes the companion for Windows x64 into
platform/platform-data/bootstrap/companion.zip (served at /bootstrap/companion.zip) and writes Play.cmd with the host, the port
and the certificate's SHA-256 baked in (tools/player-file.ps1). A rebuild into an existing -Output keeps its host.pfx, so every
Play.cmd handed out keeps working; the old folder is moved to <Output>.previous-<stamp>, never deleted.
Batch 625, -Layout Home: the release zip a host unpacks and starts. Start Amberline.cmd runs app\platform\<Platform> --home with
its own arguments (START-HERE passes the install folder, the game, the AIR client and the delete answer, batch 637), which installs
or updates the copy in the install folder (C:\Amberline unless the host chooses another; installs before batch 637 are in
%LOCALAPPDATA%\Amberline\app) and starts that one (batch 627); the
server, the friends' companion.zip and the host's own companion (app\companion, batch 629) sit beside the Platform's folder, where
the Platform derives them (HostPaths, batch 620; HostPlay). No
address, certificate, Play.cmd or platform.json is made: the host's setup page writes those on the host's PC. A git checkout must
have no local changes (batch 634: a folder without git builds, its source commit unknown) and the release folder must be new. The zip, its .sha256 and a release-notes stub are written to the release
folder. The script never publishes: it prints the gh command for the owner to run.
Batch 682 (one guide): the zip also carries the repository's START-HERE.cmd and tools/guide.ps1, unchanged, and BUILD.json names
the game executable's SHA-256 (gameExecutableSha256) for the guide's search. Start Amberline.cmd started without options opens
that guide; with options (the guide's install request, Update now's --update-confirmed) it runs the Platform as before.

.PARAMETER PublicHost
Required for -Layout Server, refused for -Layout Home. The DNS name or IPv4 address players reach this server on: no scheme, port,
path or spaces. Baked into Play.cmd and platform.json (publicHost; an IPv4 address is also the advertised gameHost).

.PARAMETER RemotePort
The Platform's public HTTPS port (platform.json remotePort). Default 8391, PlatformSettings' default.

.PARAMETER FirstGamePort
The first UDP game port (platform.json firstGamePort); the Platform uses it and the next three (maxGames 4). Default 5219,
PlatformSettings' default.

.PARAMETER Certificate
An existing Platform certificate (a server's platform-data/remote/host.pfx) to keep, so the players' files stay valid. It must load
without a password. Default: the host.pfx of an existing -Output, else a new one.

.PARAMETER ServerName
The server's display name, baked into Play.cmd: the players' installer names the desktop shortcut after it. 1 to 40 letters,
digits, spaces, dots, hyphens or underscores, starting and ending with a letter or digit. Default Amberline.

.PARAMETER GameRoot
The 1.0.0.126 game release folder (releases/0.0.0.51). Its game executable must have the SHA-256 in native/exehost/exehost.c.

.PARAMETER AirRoot
The 1.0.0.126 AIR client folder (the one holding its .swf file).

.PARAMETER Layout
Server (the default): the deployment folder described above. Home (batch 625): the release zip. Home needs -Version, takes no
-PublicHost and is win-x64 only.

.PARAMETER Version
Home only: the release version as x.y.z (for example 1.4.0). It names the zip and, by default, the release folder.

.PARAMETER Runtime
win-x64 or linux-x64. Default: win-x64 on Windows, linux-x64 elsewhere.

.PARAMETER Output
The output folder, relative to the repository or absolute. Default: .artifacts/build-server-<runtime>-<stamp> (Home: .artifacts/
release-<version>). An existing Server folder must be an earlier output of this script (it holds BUILD.json): it is rebuilt, keeping
its certificate. A Home release folder must not exist yet.

.EXAMPLE
pwsh -NoProfile -File ./tools/build-server.ps1 -GameRoot /srv/game/releases/0.0.0.51 -AirRoot /srv/game/air -Runtime linux-x64 -PublicHost <your server's address>

.EXAMPLE
pwsh -NoProfile -File ./tools/build-server.ps1 -Layout Home -Version 1.4.0 -GameRoot <game release folder> -AirRoot <AIR client folder>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$AirRoot,
    [ValidateSet('Server', 'Home')][string]$Layout = 'Server',
    [ValidateSet('win-x64', 'linux-x64')][string]$Runtime = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }),
    [string]$Output,
    [string]$PublicHost,
    [ValidateRange(1, 65535)][int]$RemotePort = 8391,
    [ValidateRange(1, 65532)][int]$FirstGamePort = 5219,
    [string]$Certificate,
    [string]$ServerName = 'Amberline',
    [string]$Version,
    # DIAGNOSTICS: a check seam for -Layout Home. The server, platform and companion.zip parts are copied from this folder instead of
    # being published, so the checks reach the layout, the refusals and the scans without a full build. Never used for a release.
    [string]$PartsFrom
)
$ErrorActionPreference = 'Stop'
if ($Layout -eq 'Server' -and -not $PublicHost) { throw '-PublicHost is required for -Layout Server: the DNS name or IPv4 address players reach this server on.' }
if ($Layout -eq 'Home') {
    if ($PublicHost) { throw '-PublicHost is for -Layout Server only: a home release has no address, the host sets it in the setup page.' }
    if (-not $Version -or $Version -cnotmatch '^\d+\.\d+\.\d+\z') { throw '-Version is required for -Layout Home, as x.y.z (for example 1.4.0).' }
    if ($Runtime -ne 'win-x64') { throw '-Runtime must be win-x64 for -Layout Home: the release is Windows x64 only.' }
} elseif ($Version) { throw '-Version is for -Layout Home only.' }
if ($PartsFrom -and $Layout -ne 'Home') { throw '-PartsFrom is a check seam for -Layout Home only.' }
# PlatformBootstrap.IsHost's rule (tools/player-file.ps1 checks it again): the Platform serves the players' download only for such a host.
if ($PublicHost -and ($PublicHost -notmatch '^[A-Za-z0-9.-]{1,253}\z' -or [uri]::CheckHostName($PublicHost) -notin 'Dns', 'IPv4')) {
    throw '-PublicHost must be the bare DNS name or IPv4 address players reach (no scheme, port, path or spaces).'
}
# PlayerInstall.ServerName's rule (a desktop file name; tools/player-file.ps1 checks it again).
if ($ServerName -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?\z' -or $ServerName -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z') {
    throw '-ServerName must be 1 to 40 letters, digits, spaces, dots, hyphens or underscores, starting and ending with a letter or digit.'
}
if ($RemotePort -in 8390, 2099, 5222) { throw '-RemotePort must differ from the loopback ports 8390 (management), 2099 (RTMP) and 5222 (XMPP).' }
$repository = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$identity = "patch126-$Runtime-$stamp"
if (-not $Output) { $Output = Join-Path '.artifacts' $(if ($Layout -eq 'Home') { "release-$Version" } else { "build-server-$Runtime-$stamp" }) }
$destination = if ([IO.Path]::IsPathRooted($Output)) { [IO.Path]::GetFullPath($Output) } else { [IO.Path]::GetFullPath((Join-Path $repository $Output)) }
$destination = $destination.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
# A rebuild goes into <Output>.next-<stamp> and replaces the old folder only when it is complete; the old one is moved aside.
$previous = $null
if ($Layout -eq 'Home') {
    if (Test-Path -LiteralPath $destination) { throw "The release folder $destination exists. Choose another -Output." }
} elseif (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'BUILD.json') -PathType Leaf)) {
        throw 'The -Output folder exists and is not an output of this script (no BUILD.json). Choose a new folder.'
    }
    $previous = $destination
    $kept = Join-Path $previous 'platform/platform-data/remote/host.pfx'
    if (-not $Certificate -and (Test-Path -LiteralPath $kept -PathType Leaf)) { $Certificate = $kept }
    $destination = "$previous.next-$stamp"
}
if ($Certificate) { $Certificate = (Resolve-Path -LiteralPath $Certificate -ErrorAction Stop).Path }
if ($PartsFrom) { $PartsFrom = (Resolve-Path -LiteralPath $PartsFrom -ErrorAction Stop).Path }

function Find-Child([string]$Directory, [string]$Name) {
    # Case-insensitive, as the Windows lookups the stock files were written for; Linux file systems differ.
    Get-ChildItem -LiteralPath $Directory -Force | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
}
function Find-Under([string]$Root, [string[]]$Path) {
    $current = $Root
    foreach ($segment in $Path) {
        $child = Find-Child $current $segment
        if (-not $child) { return $null }
        $current = $child.FullName
    }
    Get-Item -LiteralPath $current
}
function Get-ExpectedExeSha256 {
    $source = Get-Content -LiteralPath (Join-Path $repository 'native/exehost/exehost.c') -Raw
    $table = [regex]::Match($source, 'ExeSha256\[32\]\s*=\s*\{([^}]*)\}')
    $bytes = @([regex]::Matches($table.Groups[1].Value, '0x([0-9a-fA-F]{2})') | ForEach-Object { $_.Groups[1].Value })
    if (-not $table.Success -or $bytes.Count -ne 32) { throw 'native/exehost/exehost.c does not hold its 32-byte ExeSha256 table.' }
    (-join $bytes).ToUpperInvariant()
}
# Batch 635: one machine-readable line per stage; tools/guide.ps1 turns them into its progress bar (package-remote.ps1 prints 3/7).
function Write-Stage([int]$Number, [string]$Text) { Write-Host "AMBERLINE-STAGE $Number/7 $Text" }

Write-Stage 1 'Checking your game and AIR client folders'
# Inputs: read to check, never copied.
$game = (Resolve-Path -LiteralPath $GameRoot -ErrorAction Stop).Path
$air = (Resolve-Path -LiteralPath $AirRoot -ErrorAction Stop).Path
$exe = Find-Child $game 'League of Legends.exe'
if (-not $exe -or $exe.PSIsContainer) { throw '-GameRoot has no game executable: give the 1.0.0.126 game release folder (releases/0.0.0.51).' }
foreach ($folder in @(@('DATA'), @('DATA', 'Scripts'), @('DATA', 'BuildingBlocks'), @('LEVELS'), @('LEVELS', 'Map1'), @('LEVELS', 'Map2'),
        @('LEVELS', 'Map3'), @('LEVELS', 'Map4'), @('LEVELS', 'Map8'))) {
    if (-not (Find-Under $game $folder)) { throw "-GameRoot has no $($folder -join '/') folder (the server reads it)." }
}
if ((Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash -ne (Get-ExpectedExeSha256)) {
    throw 'The game executable is not the 1.0.0.126 executable the server needs (SHA-256 differs from native/exehost/exehost.c).'
}
$lolClient = Find-Child $air 'LolClient.swf'
if (-not $lolClient -or $lolClient.PSIsContainer) { throw '-AirRoot has no .swf file: give the 1.0.0.126 AIR client folder.' }
if ($lolClient.Length -ne 6944733) {
    throw "-AirRoot's .swf file is $($lolClient.Length) bytes; the 1.0.0.126 client's is 6944733 (the stock size AirSourceLayout.cs checks)."
}
if (-not (Find-Under $air @('assets', 'swfs', 'AirGeneratedContent.swf'))) {
    throw '-AirRoot has no assets/swfs/AirGeneratedContent.swf: the champion catalog (ChampionCatalog) is read from it.'
}
foreach ($art in @(@('assets', 'images'), @('assets', 'storeImages'))) {
    if (-not (Find-Under $air $art)) { Write-Warning "-AirRoot has no $($art -join '/') folder: the Platform serves without that art." }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (global.json pins 10.0.302) is required on PATH.' }
# Batch 634: a folder without .git (GitHub's "Download ZIP"), or a PC without git, builds too; its source commit is unknown.
# The .git test comes first, so git never reports the commit of a repository around the folder.
$gitCheckout = (Test-Path -LiteralPath (Join-Path $repository '.git')) -and [bool](Get-Command git -CommandType Application -ErrorAction SilentlyContinue)
$commit = if ($gitCheckout) { (& git -C $repository rev-parse HEAD) } else { 'unknown (no git checkout)' }
$localChanges = if ($gitCheckout) { [bool](& git -C $repository status --porcelain | Select-Object -First 1) } else { $null }
# Home: a clean git checkout, so the release names one source commit (BUILD.json's sourceHasLocalChanges is then false).
if ($Layout -eq 'Home' -and $localChanges) {
    throw 'The checkout has local changes: commit them first, so the release names one source commit.'
}

# The deployable tree: the output folder (Server), or the app\ folder of a stage folder that becomes the zip (Home).
$work = Join-Path (Split-Path -Parent $destination) ("build-server-work-$stamp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$stage = Join-Path $work 'stage'
$app = if ($Layout -eq 'Home') { Join-Path $stage 'app' } else { $destination }
New-Item -ItemType Directory -Path $destination, $work | Out-Null
New-Item -ItemType Directory -Force -Path $app | Out-Null
# The certificate and Play.cmd come first: a -Certificate that cannot be used fails before the long build. Server only: a home
# release has neither (its host's Platform makes the certificate at its first start).
$pin = $null
if ($Layout -eq 'Server') {
    $playerFile = @{ Root = $destination; PublicHost = $PublicHost; Port = $RemotePort; ServerName = $ServerName }
    if ($Certificate) { $playerFile.Certificate = $Certificate }
    $pin = & (Join-Path $PSScriptRoot 'player-file.ps1') @playerFile
}

# Server: the package-remote server stage (publish, natives, BUILD.json, and the preflight where this system can run it).
Write-Stage 2 'Building the server'
if ($PartsFrom) {
    # DIAGNOSTICS: the check seam (-PartsFrom), the server comes from the folder.
    Copy-Item -LiteralPath (Join-Path $PartsFrom 'server') -Destination $app -Recurse
} else {
    $previousAir = $env:AIR_CLIENT_ROOT
    try {
        $env:AIR_CLIENT_ROOT = $air
        & (Join-Path $repository 'tools/package-remote.ps1') -ServerOnly -Runtime $Runtime -OutputDirectory (Join-Path $work 'remote') `
            -GameDataRoot $game -BuildIdentity $identity
    } finally {
        $env:AIR_CLIENT_ROOT = $previousAir
    }
    Move-Item -LiteralPath (Join-Path $work 'remote/runtime/server') -Destination (Join-Path $app 'server')
}

# Platform: the same runtime, self-contained, a folder (its platform-data is created at first start).
Write-Stage 4 'Building the Platform'
if ($PartsFrom) {
    # DIAGNOSTICS: the check seam (-PartsFrom), the Platform comes from the folder.
    Copy-Item -LiteralPath (Join-Path $PartsFrom 'platform') -Destination $app -Recurse
} else {
    & dotnet publish (Join-Path $repository 'src/Amberline.Platform/Amberline.Platform.csproj') -c Release -r $Runtime --self-contained true `
        --artifacts-path (Join-Path $work 'Amberline.Platform') -o (Join-Path $app 'platform') "-p:InformationalVersion=$identity"
    if ($LASTEXITCODE -ne 0) { throw 'Amberline.Platform publish failed.' }
}
if ($Layout -eq 'Home') {
    # The Play-file template (tools/play-bootstrap.cmd) beside the Platform: the host's Platform renders each friend's Play file from it.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'play-bootstrap.cmd') -Destination (Join-Path $app 'platform')
}

$serverExecutable = if ($Runtime -eq 'win-x64') { 'Amberline.Server.exe' } else { 'Amberline.Server' }
$platformExecutable = if ($Runtime -eq 'win-x64') { 'Amberline.Platform.exe' } else { 'Amberline.Platform' }
$platformData = Join-Path $destination 'platform/platform-data'
if ($Layout -eq 'Server') {
    New-Item -ItemType Directory -Force -Path $platformData | Out-Null
    # The address and ports the operator gave; placeholders for the server's own folders (nothing of the build machine's paths).
    $template = @'
{
  "dataRoot": "<GAME FOLDER: the 1.0.0.126 release folder on this server (the game executable, DATA, LEVELS)>",
  "serverExecutable": "<ABSOLUTE PATH: the server executable in server/ (Amberline.Server or Amberline.Server.exe)>",
  "gameHost": "{GAMEHOST}",
  "httpPort": 8390,
  "rtmpPort": 2099,
  "firstGamePort": {FIRSTGAMEPORT},
  "maxGames": 4,
  "remotePort": {REMOTEPORT},
  "publicHost": "{PUBLICHOST}",
  "airRoot": "<AIR FOLDER: the 1.0.0.126 AIR client folder on this server (its .swf file, assets)>",
  "publicRegistrationUrl": null,
  "bindAddress": "0.0.0.0",
  "xmppPort": 5222
}
'@
    $gameHost = if ([uri]::CheckHostName($PublicHost) -eq 'IPv4') { $PublicHost } else { '<PUBLIC IPv4: the address players reach this server on>' }
    $template = $template.Replace('{GAMEHOST}', $gameHost).Replace('{PUBLICHOST}', $PublicHost)
    $template = $template.Replace('{FIRSTGAMEPORT}', [string]$FirstGamePort).Replace('{REMOTEPORT}', [string]$RemotePort)
    Set-Content -LiteralPath (Join-Path $platformData 'platform.json') -Value $template -Encoding utf8
}

# The players' companion: Windows x64 whatever the server's runtime, without the client-files installer (ClientFilesInstaller.cs).
# The Platform serves it at /bootstrap/companion.zip from platform-data/bootstrap (PlatformBootstrap.Map), with its SHA-256; a home
# release keeps it in app\bootstrap, where the Platform derives its bootstrap folder (HostPaths.Bootstrap).
Write-Stage 5 'Building the player tool'
$companionZip = if ($Layout -eq 'Home') { Join-Path $app 'bootstrap/companion.zip' } else { Join-Path $platformData 'bootstrap/companion.zip' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $companionZip) | Out-Null
if ($PartsFrom) {
    # DIAGNOSTICS: the check seam (-PartsFrom), the archive comes from the folder.
    Copy-Item -LiteralPath (Join-Path $PartsFrom 'companion.zip') -Destination $companionZip
} else {
    $companion = Join-Path $work 'companion'
    & dotnet publish (Join-Path $repository 'src/Amberline.AirCompanion/Amberline.AirCompanion.csproj') -c Release -r win-x64 --self-contained true `
        --artifacts-path (Join-Path $work 'Amberline.AirCompanion') -o $companion -p:AmberlineArchiveInstaller=false "-p:InformationalVersion=$identity"
    if ($LASTEXITCODE -ne 0) { throw 'Amberline.AirCompanion publish failed.' }
    if (Test-Path -LiteralPath (Join-Path $companion 'SharpCompress.dll')) { throw 'The companion was published with the client-files installer.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($companion, $companionZip, [IO.Compression.CompressionLevel]::Optimal, $false)
}
# The host's own play (Play on this PC, batch 629): the same companion unpacked in app\companion, where the Platform starts it.
if ($Layout -eq 'Home') { [IO.Compression.ZipFile]::ExtractToDirectory($companionZip, (Join-Path $app 'companion')) }

Write-Stage 6 'Packing'
$requirements = if ($Runtime -eq 'win-x64') { 'Windows x64 (Windows Server 2019 or later, or Windows 10/11).' } else {
    'Linux x86-64 with glibc 2.28 or newer, libstdc++6, OpenSSL 3 (libssl3, TLS) and ICU (libicu, .NET globalization). The exehost helper is a 32-bit program: the kernel must run 32-bit programs (IA32 emulation).' }
$start = if ($Runtime -eq 'win-x64') { "cd platform`r`n  .\$platformExecutable" } else {
    "chmod +x server/Amberline.Server server/exehost platform/$platformExecutable`n  cd platform`n  ./$platformExecutable" }
$readme = @'
Amberline server {IDENTITY} ({RUNTIME}), built from source commit {COMMIT}.

WHAT IS HERE
  server/     Amberline.Server, the match server, and its natives beside it: amberline_enet (the ENet transport) and
              exehost (runs the client's movement code from your own game executable).
  platform/   Amberline.Platform: accounts, store, lobby and the AIR front. It starts the match servers itself.
  platform/platform-data/platform.json   the configuration. publicHost and the ports are the ones this package was built
              for; replace every <...> value before the first start.
  platform/platform-data/remote/host.pfx   the Platform's TLS certificate and its private key. Keep it private. Every
              Play.cmd pins its SHA-256 ({PIN}): replace this file and every player needs a new Play.cmd.
  platform/platform-data/bootstrap/companion.zip   the player tool (Windows x64), served to Play.cmd.
  Play.cmd    the one file each player needs (see PLAYERS).
  BUILD.json  the build identity and the SHA-256 of every file.
  No game or AIR file is in this package. Copy your own 1.0.0.126 game folder (the game executable, DATA, LEVELS)
  and your own 1.0.0.126 AIR client folder (its .swf file, assets) to this machine; dataRoot and airRoot name them.

REQUIREMENTS
  {REQUIREMENTS}

START (edit platform-data/platform.json first)
  {START}
  The Platform passes GAME_DATA_ROOT and AIR_CLIENT_ROOT to the match servers it starts. A server run by hand needs both set
  (AIR_CLIENT_ROOT to the AIR folder). Check the server alone (it starts no match):
    server/{SERVER} --solo --ephemeral --validate-only --data-root <game folder>

PORTS TO OPEN
  TCP {REMOTEPORT}          public HTTPS: sign-in, store and the player tool download.
  UDP {GAMEPORTS}     game traffic, one port per match (firstGamePort to firstGamePort + maxGames - 1).
  Loopback only, do not open: TCP 8390 (management), 2099 (RTMP) and 5222 (XMPP).

ADMINISTRATION
  The management page /manage listens on 127.0.0.1:8390 only. From your own PC, tunnel it over SSH:
    ssh -L 8390:127.0.0.1:8390 <user>@<server>
  and open http://127.0.0.1:8390/manage in a browser on your PC. Create each player's login there.

PLAYERS
  Send each player Play.cmd (the only file they need) and, privately, the login you created for them. Each player needs
  Windows 10 or 11 and their own 1.0.0.126 game and AIR client folders. Play.cmd downloads the player tool from this server
  (the server's certificate must match the pin, the archive its SHA-256) and opens a setup window: it finds or asks for the
  two folders, moves them into one install folder the player picks (on another drive: copied, every file checked, then the
  originals removed), and puts a shortcut named "{SERVERNAME}" on the desktop. The shortcut asks this server for a newer
  player tool, updates it when there is one, and starts the AIR client, without a console window. A player who already
  installed for another server keeps that install: your Play.cmd only adds your server and its shortcut. Running Play.cmd
  again repairs the shortcut.

REBUILDING AND UPDATING
  A rebuild into this folder keeps platform-data/remote/host.pfx, so every Play.cmd keeps working; the old folder is moved
  to <folder>.previous-<stamp>. A build into another folder keeps it with -Certificate <this host.pfx>.
  To update a running server, stop the Platform and copy the new package over it, except platform-data/platform.json.
  The Platform reads companion.zip's SHA-256 when it starts, so players get the new tool at their next start.
'@
if ($Layout -eq 'Server') {
    $tokens = @{ IDENTITY = $identity; RUNTIME = $Runtime; COMMIT = $commit; REQUIREMENTS = $requirements; START = $start; SERVER = $serverExecutable;
        PIN = $pin; REMOTEPORT = $RemotePort; GAMEPORTS = "$FirstGamePort-$($FirstGamePort + 3)"; SERVERNAME = $ServerName }
    foreach ($token in $tokens.Keys) { $readme = $readme.Replace("{$token}", [string]$tokens[$token]) }
    Set-Content -LiteralPath (Join-Path $destination 'README-DEPLOY.txt') -Value $readme -Encoding utf8
}

# The manifest: what was built from which commit, and the SHA-256 of every file of the deployment (Home: of app\).
$files = @(Get-ChildItem -LiteralPath $app -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($app, $_.FullName).Replace('\', '/'); bytes = $_.Length;
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
$manifest = [ordered]@{
    component = if ($Layout -eq 'Home') { 'amberline-host' } else { 'deployment' }
    runtime = $Runtime
    buildIdentity = $identity
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceCommit = $commit
    sourceHasLocalChanges = $localChanges
    files = $files
}
if ($Layout -eq 'Home') {
    $manifest.Insert(0, 'version', $Version)
    # Batch 682: the pin the zip's guide checks a found game against (the zip has no exehost.c).
    $manifest.Insert(1, 'gameExecutableSha256', (Get-ExpectedExeSha256))
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $app 'BUILD.json') -Encoding utf8

# The natives and both executables must be where the deployment expects them.
$natives = if ($Runtime -eq 'win-x64') { @('amberline_enet.dll', 'exehost.exe') } else { @('libamberline_enet.so', 'exehost') }
$players = if ($Layout -eq 'Home') { @('platform/play-bootstrap.cmd', 'bootstrap/companion.zip', 'companion/Amberline.AirCompanion.exe') } else { @('Play.cmd', 'platform/platform-data/remote/host.pfx', 'platform/platform-data/bootstrap/companion.zip') }
foreach ($required in @("server/$serverExecutable") + ($natives | ForEach-Object { "server/$_" }) + @("platform/$platformExecutable") + $players) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) { throw "The output has no $required." }
}

function Write-HomeFile([string]$Name, [string]$Text) {
    # Plain ASCII with CRLF line ends: Windows' command interpreter and Notepad both read it as written.
    $crlf = $Text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $stage $Name), $crlf, [Text.Encoding]::ASCII)
}
if ($Layout -eq 'Home') {
    # The files at the zip's root. Start Amberline.cmd runs the Platform with --home from its own folder (the Platform's derived
    # server and bootstrap folders are beside that folder). Batch 682: without options it opens the guide (START-HERE.cmd), unless
    # the guide started it (AMBERLINE_GUIDE), so a double-click on it asks the guide's questions too.
    $homeStart = @'
@echo off
rem Installs Amberline in the folder you choose (C:\Amberline unless you choose another), or updates it, then starts it.
rem START-HERE passes the install folder, the game and the AIR client as arguments; without any, this opens START-HERE.
rem The installed Amberline goes on running in this window after the program here ends.
title Amberline
if not exist "%~dp0app\platform\{PLATFORM}" goto missing
if "%~1"=="" if not defined AMBERLINE_GUIDE if exist "%~dp0START-HERE.cmd" goto guide
cd /d "%~dp0app\platform"
"{PLATFORM}" --home %*
exit /b %errorlevel%
:guide
call "%~dp0START-HERE.cmd"
exit /b %errorlevel%
:missing
echo Some Amberline files are missing. Unzip the whole zip again, then double-click START-HERE.cmd.
echo Press Enter to close.
pause >nul
exit /b 1
'@
    $homeReadMe = @'
AMBERLINE {VERSION}: READ ME FIRST
==================================

Amberline hosts the game on patch 1.0.0.126 (October 2011) on your PC for your friends.
This zip holds no game or AIR client file. You need your own game folder (with
DATA and LEVELS) and AIR client folder (with its assets folder).
You need Windows 10 or 11, 64-bit, and nothing else.

STARTING
1. Unzip the whole zip into a folder.
2. Double-click START-HERE.cmd. If Windows warns you, see WINDOWS WARNINGS below.
3. Answer its questions: where the server runs, your game and AIR client folders (it
   searches for them), and the install folder (press Enter for C:\Amberline). Amberline
   keeps everything there and puts an Amberline icon on your desktop. You can then
   delete the zip.
4. Follow the setup page that opens in your browser.
5. Keep the Amberline window open while friends play.

UPDATING
Choose Update now on Amberline's page. Or unzip the new zip, double-click its
START-HERE.cmd and answer Y when it asks to update.
Your settings and friends stay. If the new version does not start, the old one comes back.
Amberline asks GitHub once a day whether a new version is out (untick it on the setup page
to stop that). It never installs anything by itself.

WINDOWS WARNINGS
- "Windows protected your PC": Amberline is not signed. Download the zip only from the
  official Amberline releases page, and check its SHA-256 against the one listed there:
  open PowerShell in its folder and type
  Get-FileHash .\Amberline-{VERSION}-win-x64.zip
  Then choose More info, and Run anyway.
- Windows 11's Smart App Control blocks unsigned programs, Amberline too. Amberline never
  asks you to turn protections off.

WHERE AMBERLINE KEEPS ITS DATA
In the install folder: app, app.previous (the version before the last update), data, game
and air. Its READ ME.txt says how to remove Amberline. Outside it: only the desktop icon
and %LOCALAPPDATA%\Amberline\install.json. An Amberline installed before this version stays
in %LOCALAPPDATA%\Amberline and is updated there.
'@
    Write-HomeFile 'Start Amberline.cmd' $homeStart.Replace('{PLATFORM}', $platformExecutable)
    Write-HomeFile 'READ ME FIRST.txt' $homeReadMe.Replace('{VERSION}', $Version)
    # Batch 682: the one guide, as the repository has it.
    Copy-Item -LiteralPath (Join-Path $repository 'START-HERE.cmd') -Destination $stage
    New-Item -ItemType Directory -Path (Join-Path $stage 'tools') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'tools/guide.ps1') -Destination (Join-Path $stage 'tools')
}

# The final scan: no Riot file type, and no file byte-identical to a file of the game or AIR folder.
Write-Stage 7 'The final check: nothing of the game or AIR client goes in'
$riotExtensions = '.inibin', '.luaobj', '.raf', '.dds', '.swf', '.sco', '.scb', '.skn', '.skl', '.anm', '.fev', '.fsb', '.troybin', '.wpk', '.bnk'
$tree = if ($Layout -eq 'Home') { $stage } else { $destination }
$outputFiles = @(Get-ChildItem -LiteralPath $tree -Recurse -File)
$riot = @($outputFiles | Where-Object { $riotExtensions -contains $_.Extension.ToLowerInvariant() })
if ($riot.Count -gt 0) { throw "The output holds $($riot.Count) file(s) of a game file type, first: $([IO.Path]::GetRelativePath($tree, $riot[0].FullName))." }
# Batch 635: Amberline's own folders (the repository, the output, the work folder) may lie inside the game or AIR folder, as the
# first friend had them. Their files are not the game's or the client's, so they are left out of the root they lie in. A root
# that lies inside the repository is still compared in full.
$separator = [IO.Path]::DirectorySeparatorChar
$caseRule = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$own = @($repository, $destination, $previous, $work | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd($separator) + $separator })
$bySize = @{}
foreach ($root in @($game, $air)) {
    $inside = @($own | Where-Object { $_.StartsWith($root.TrimEnd($separator) + $separator, $caseRule) })
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Length -gt 0 }) {
        $ours = $false
        foreach ($folder in $inside) { if ($file.FullName.StartsWith($folder, $caseRule)) { $ours = $true; break } }
        if ($ours) { continue }
        if (-not $bySize.ContainsKey($file.Length)) { $bySize[$file.Length] = [Collections.Generic.List[string]]::new() }
        $bySize[$file.Length].Add($file.FullName)
    }
}
$digests = @{}
function Get-Digest([string]$Path) {
    if (-not $digests.ContainsKey($Path)) { $digests[$Path] = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    $digests[$Path]
}
foreach ($file in $outputFiles | Where-Object { $_.Length -gt 0 -and $bySize.ContainsKey($_.Length) }) {
    $digest = Get-Digest $file.FullName
    foreach ($candidate in $bySize[$file.Length]) {
        if ((Get-Digest $candidate) -eq $digest) {
            throw "The output file $([IO.Path]::GetRelativePath($tree, $file.FullName)) is a copy of a file of -GameRoot or -AirRoot. Nothing from them may ship."
        }
    }
}
# The archive's entries too: every player receives the companion archive, and the release zip is every file of the release.
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Test-ArchiveEntries([string]$Path, [string]$Label) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            if ($riotExtensions -contains [IO.Path]::GetExtension($entry.Name).ToLowerInvariant()) {
                throw "The $Label holds $($entry.FullName), a game file type."
            }
            if ($entry.Length -eq 0 -or -not $bySize.ContainsKey($entry.Length)) { continue }
            $stream = $entry.Open()
            try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            foreach ($candidate in $bySize[$entry.Length]) {
                if ((Get-Digest $candidate) -eq $digest) {
                    throw "The $Label's $($entry.FullName) is a copy of a file of -GameRoot or -AirRoot. Nothing from them may ship."
                }
            }
        }
    } finally { $archive.Dispose() }
}
Test-ArchiveEntries $companionZip 'companion archive'

if ($Layout -eq 'Home') {
    # The release zip is built in the work folder and scanned like the output; it moves into the release folder only when it passes.
    $zipName = "Amberline-$Version-win-x64.zip"
    $zip = Join-Path $work $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Test-ArchiveEntries $zip 'release zip'
    Move-Item -LiteralPath $zip -Destination $destination
    $zipPath = Join-Path $destination $zipName
    $sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$sha  $zipName`n", [Text.Encoding]::ASCII)
    # The notes stub for the release page: the owner writes the changes; the warnings and the hash are already in it.
    $notesPath = Join-Path $destination "Amberline-$Version-release-notes.md"
    $notes = @'
# Amberline {VERSION}

{SOURCE}

- Zip: {ZIP}
- SHA-256: {HASH}

## What changed

(Describe the changes in this version before you publish.)

## Windows warnings

Windows will warn you because Amberline is new and not signed. Check that you downloaded it from this releases page and that its SHA-256 matches the one above, then choose More info, then Run anyway. On Windows 11 with Smart App Control on, Windows blocks Amberline completely; Amberline cannot change that.
'@
    $source = if ($gitCheckout) { "Built from source commit $commit, with no local changes." } else { 'Built from a folder without git: the source commit is unknown.' }
    Set-Content -LiteralPath $notesPath -Value $notes.Replace('{VERSION}', $Version).Replace('{SOURCE}', $source).Replace('{ZIP}', $zipName).Replace('{HASH}', $sha) -Encoding utf8
}
Remove-Item -LiteralPath $work -Recurse -Force
$size = [math]::Round((Get-ChildItem -LiteralPath $destination -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
if ($previous) {
    $aside = "$previous.previous-$stamp"
    Move-Item -LiteralPath $previous -Destination $aside
    Move-Item -LiteralPath $destination -Destination $previous
    $destination = $previous
    Write-Output "The previous build is now $aside; delete it when it is no longer needed."
}
if ($Layout -eq 'Home') {
    Write-Output "Built the Amberline release: $zipPath ($size MB; the .sha256 and the release-notes stub are beside it)."
    Write-Output 'The script does not publish. When you have checked the zip and the notes, publish it yourself with:'
    Write-Output "  gh release create v$Version `"$zipPath`" `"$zipPath.sha256`" --notes-file `"$notesPath`""
} else {
    Write-Output "Built $Runtime server package: $([IO.Path]::GetRelativePath($repository, $destination)) ($($outputFiles.Count) files, $size MB, identity $identity)"
    Write-Output "Hand each player $(Join-Path $destination 'Play.cmd'); it pins the certificate SHA-256 $pin."
}
