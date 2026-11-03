#!/bin/sh
# Builds libamberline_enet.so (x86_64 Linux, glibc 2.28 or newer) with Zig: on Linux, or cross from Windows (Git Bash).
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
# The HAS_* switches are what ENet's own configure finds on Linux; HAS_POLL keeps enet_socket_wait off select() and its FD_SETSIZE limit.
"$zig" cc -target x86_64-linux-gnu.2.28 -shared -fPIC -O2 -fno-sanitize=all -I"$here/include" \
    -DAMBERLINE_ENET_BRIDGE_BUILD -DENET_BUILDING_LIB \
    -DHAS_POLL -DHAS_FCNTL -DHAS_INET_PTON -DHAS_INET_NTOP -DHAS_MSGHDR_FLAGS -DHAS_SOCKLEN_T \
    -DHAS_GETADDRINFO -DHAS_GETNAMEINFO -DHAS_GETHOSTBYNAME_R -DHAS_GETHOSTBYADDR_R \
    -Wl,-soname,libamberline_enet.so -s -o "$out/libamberline_enet.so.$$" \
    "$here/callbacks.c" "$here/compress.c" "$here/host.c" "$here/list.c" "$here/packet.c" "$here/peer.c" \
    "$here/protocol.c" "$here/unix.c" "$here/amberline_enet_bridge.c"
mv -f "$out/libamberline_enet.so.$$" "$out/libamberline_enet.so"
echo "Built $out/libamberline_enet.so"
