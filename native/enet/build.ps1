[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86')]
    [string] $Architecture = 'x64',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'build'),
    # Batch 634: linux-x64 builds libamberline_enet.so (x86-64, glibc 2.28 or newer) with build.sh's flags, so no sh is needed.
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $Runtime = 'win-x64'
)

# Builds amberline_enet.dll, the ENet bridge the server's transport loads (LeagueEnetTransport.cs). Batch 634: built with Zig
# (zig cc), as build.sh builds the Linux bridge; keep the flags of the two scripts in step. Windows PowerShell 5.1 runs this file
# too (the Launcher).
$ErrorActionPreference = 'Stop'
if ($Runtime -eq 'linux-x64' -and $Architecture -ne 'x64') { throw '-Architecture x86 is for Windows only.' }

# Zig as build.sh finds it: $env:ZIG, else zig on PATH, else the nearest .tools/zig above this folder (its cache in .tools/zig-cache).
$zig = $env:ZIG
if (-not $zig) { $zig = (Get-Command zig -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
for ($dir = $PSScriptRoot; -not $zig -and $dir; $dir = Split-Path -Parent $dir) {
    foreach ($candidate in (Join-Path $dir '.tools/zig/zig.exe'), (Join-Path $dir '.tools/zig/zig')) {
        if (-not $zig -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $zig = $candidate
            if (-not $env:ZIG_GLOBAL_CACHE_DIR) { $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $dir '.tools/zig-cache' }
            if (-not $env:ZIG_LOCAL_CACHE_DIR) { $env:ZIG_LOCAL_CACHE_DIR = Join-Path $dir '.tools/zig-cache' }
        }
    }
}
if (-not $zig) { throw 'Zig is required: set ZIG, put zig on PATH, or extract it into .tools/zig.' }

$sourceDirectory = $PSScriptRoot
# unix.c and win32.c each compile to nothing on the other system.
$sources = @('callbacks.c', 'compress.c', 'host.c', 'list.c', 'packet.c', 'peer.c', 'protocol.c', 'unix.c', 'win32.c',
    'amberline_enet_bridge.c') | ForEach-Object { Join-Path $sourceDirectory $_ }
$arguments = @('cc', '-shared', '-O2', '-fno-sanitize=all', ('-I' + (Join-Path $sourceDirectory 'include')),
    '-DAMBERLINE_ENET_BRIDGE_BUILD', '-DENET_BUILDING_LIB', '-s')
if ($Runtime -eq 'win-x64') {
    # ENET_DLL: ENet's and the bridge's functions are __declspec(dllexport), as clang-cl built them. The C runtime is the system's
    # UCRT; lld writes a content hash in place of the time stamp, so the build is byte-reproducible.
    $name = 'amberline_enet.dll'
    $target = if ($Architecture -eq 'x86') { 'x86-windows-gnu' } else { 'x86_64-windows-gnu' }
    $arguments += @('-target', $target, '-DENET_DLL')
    $libraries = @('-lws2_32', '-lwinmm')
} else {
    # The HAS_* switches are what ENet's own configure finds on Linux; HAS_POLL keeps enet_socket_wait off select() and its
    # FD_SETSIZE limit.
    $name = 'libamberline_enet.so'
    $arguments += @('-target', 'x86_64-linux-gnu.2.28', '-fPIC', '-DHAS_POLL', '-DHAS_FCNTL', '-DHAS_INET_PTON', '-DHAS_INET_NTOP',
        '-DHAS_MSGHDR_FLAGS', '-DHAS_SOCKLEN_T', '-DHAS_GETADDRINFO', '-DHAS_GETNAMEINFO', '-DHAS_GETHOSTBYNAME_R',
        '-DHAS_GETHOSTBYADDR_R', '-Wl,-soname,libamberline_enet.so')
    $libraries = @()
}

$outputDirectoryResolved = [System.IO.Path]::GetFullPath($OutputDirectory)
# Zig writes an import library beside a DLL: build into a folder of its own, keep only the library.
$temporary = Join-Path $outputDirectoryResolved ".amberline_enet.$PID"
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
try {
    $arguments += @('-o', (Join-Path $temporary $name)) + $sources + $libraries
    & $zig @arguments
    if ($LASTEXITCODE -ne 0) { throw "zig cc failed with exit code $LASTEXITCODE" }
    Move-Item -Force -LiteralPath (Join-Path $temporary $name) -Destination (Join-Path $outputDirectoryResolved $name)
} finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue -LiteralPath $temporary
}
Write-Output "Built $(Join-Path $outputDirectoryResolved $name)"
