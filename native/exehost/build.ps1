[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'build'),
    # Batch 634: linux-x64 builds the Linux helper (exehost, static i386) with build.sh's flags, so no sh is needed.
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $Runtime = 'win-x64'
)

# Builds exehost.exe (Windows x86), the process that runs the client's own movement code from the player's League of Legends.exe
# (see exehost.c). Batch 608: production uses it; tools/check.ps1 and tools/package-remote.ps1 build it beside the server.
# Batch 634: built with Zig (zig cc), as build.sh builds the Linux helper; keep the flags of the two scripts in step.
# Windows PowerShell 5.1 runs this file too.
$ErrorActionPreference = 'Stop'

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

# build.sh's flags, which say why: the MSVC inline assembly and __declspec(naked) of exehost.c; -mstackrealign and no sibling
# calls, since the client calls in with a 4-byte aligned stack; -fno-strict-aliasing, as clang-cl built the Windows helper before.
$arguments = @('cc', '-O2', '-fms-extensions', '-fasm-blocks', '-fdeclspec', '-mstackrealign', '-fno-optimize-sibling-calls',
    '-fno-strict-aliasing', '-fno-sanitize=all', '-Wall', '-s')
if ($Runtime -eq 'win-x64') {
    # A 32-bit console program with clang-cl's layout: image base 0x400000 (the client image is then always relocated, as before),
    # dynamic base, NX, a 1 MiB stack; no structured exception handling of its own (exehost.c uses a vectored handler); the
    # entry point wmain (-municode); BCryptHash from bcrypt.dll; the C runtime is the system's UCRT. No PDB (-s); lld writes a
    # content hash in place of the time stamp, so the build is byte-reproducible (clang-cl's /Brepro).
    $name = 'exehost.exe'
    $arguments += @('-target', 'x86-windows-gnu', '-municode', '-Wl,--stack,1048576')
    $libraries = @('-lbcrypt')
} else {
    # Image base 0x8048000, so the client image's own range (0x400000 + 0x2990000) stays free and it maps unrelocated.
    $name = 'exehost'
    $arguments += @('-target', 'x86-linux-musl', '-static', '-fno-pie', '-no-pie', '-Wl,--image-base=0x8048000')
    $libraries = @()
}

$outputDirectoryResolved = [System.IO.Path]::GetFullPath($OutputDirectory)
# Zig may write by-products beside the output: build into a folder of its own, keep only the program.
$temporary = Join-Path $outputDirectoryResolved ".exehost.$PID"
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
try {
    $arguments += @('-o', (Join-Path $temporary $name), (Join-Path $PSScriptRoot 'exehost.c')) + $libraries
    & $zig @arguments
    if ($LASTEXITCODE -ne 0) { throw "zig cc failed with exit code $LASTEXITCODE" }
    Move-Item -Force -LiteralPath (Join-Path $temporary $name) -Destination (Join-Path $outputDirectoryResolved $name)
} finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue -LiteralPath $temporary
}
Write-Output "Built $(Join-Path $outputDirectoryResolved $name)"
