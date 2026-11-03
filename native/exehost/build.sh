#!/bin/sh
# Builds exehost for Linux (production movement helper since batch 608; Linux port batch 609): a static i386 ELF (musl) with Zig, on Linux or cross from Windows (Git Bash).
# Usage: build.sh [output directory]   (default: build/ beside this script)
# build.ps1 -Runtime linux-x64 runs the same zig cc command without sh (batch 634): keep the two in step.
# Zig: $ZIG, else zig on PATH, else the repository's .tools/zig (see docs/maintenance/2026-11-01-batch-609-linux-native.md).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/build}

zig=${ZIG:-}
if [ -z "$zig" ] && command -v zig >/dev/null 2>&1; then zig=zig; fi
dir=$here
while [ -z "$zig" ] && [ "$dir" != / ] && [ -n "$dir" ]; do
    for candidate in "$dir/.tools/zig/zig" "$dir/.tools/zig/zig.exe"; do
        if [ -z "$zig" ] && [ -x "$candidate" ]; then
            zig=$candidate
            : "${ZIG_GLOBAL_CACHE_DIR:=$dir/.tools/zig-cache}"
            : "${ZIG_LOCAL_CACHE_DIR:=$dir/.tools/zig-cache}"
            export ZIG_GLOBAL_CACHE_DIR ZIG_LOCAL_CACHE_DIR
        fi
    done
    dir=$(dirname "$dir")
done
[ -n "$zig" ] || { echo "build.sh: Zig is required (set ZIG, put zig on PATH, or extract it into .tools/zig)" >&2; exit 1; }

mkdir -p "$out"
# - linked at 0x8048000, so the client image's own range (0x400000 + 0x2990000) stays free and it maps unrelocated;
# - the MSVC inline assembly and __declspec(naked) of exehost.c: -fms-extensions -fasm-blocks -fdeclspec;
# - -mstackrealign and no sibling calls: the client calls in with a 4-byte aligned stack (exehost_linux.h);
# - -fno-strict-aliasing as clang-cl built the Windows helper until batch 634 (the grid and actor fields are written through casts).
"$zig" cc -target x86-linux-musl -O2 -static -fno-pie -no-pie -fms-extensions -fasm-blocks -fdeclspec \
    -mstackrealign -fno-optimize-sibling-calls -fno-strict-aliasing -fno-sanitize=all -Wall -s \
    -Wl,--image-base=0x8048000 -o "$out/exehost.$$" "$here/exehost.c"
mv -f "$out/exehost.$$" "$out/exehost"
echo "Built $out/exehost"
