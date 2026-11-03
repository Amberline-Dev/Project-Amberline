#!/usr/bin/env bash
# DIAGNOSTICS: batch 609. Smoke test of the Linux exehost for a VPS or CI: build it, start it on the player's League of Legends.exe
# (1.0.0.126; the helper checks its SHA-256) and Map1's grid, then one ping and one A* search across the middle of the map.
# Usage: smoke-linux.sh <League of Legends.exe> <LEVELS/Map1/AIPath.aimesh_ngrid> [helper]
#   helper: an already built exehost to test instead of building one (any build speaks the same protocol, exehost.exe included).
# Needs bash, dd, od, and a kernel that runs 32-bit programs (IA32 emulation). The full differential check is
# movement.b596-exe-astar-matches-port (tests/Amberline.Tests/Movement/ExeHostAStarChecks.cs) with AMBERLINE_EXEHOST set to the helper.
set -euo pipefail
[ $# -ge 2 ] || { echo "usage: $0 <League of Legends.exe> <AIPath.aimesh_ngrid> [helper]" >&2; exit 2; }
here=$(cd "$(dirname "$0")" && pwd)
exe=$1 grid=$2 host=${3:-}
if [ -z "$host" ]; then
    sh "$here/build.sh" "$here/build"
    host=$here/build/exehost
fi
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

coproc HOST { exec "$host" "$exe" "$grid" 2>"$work/stderr"; }
to=${HOST[1]} from=${HOST[0]} pid=$HOST_PID
die() { echo "smoke: $*" >&2; sed 's/^/smoke: helper: /' "$work/stderr" >&2; exit 1; }
# Exactly $1 bytes of the reply stream into the file $2. (Not in a pipeline: bash closes a coprocess's descriptors in subshells.)
take() { dd bs=1 count="$1" of="$2" 2>/dev/null <&"$from"; }
reply() { # one reply into $work/reply
    take 4 "$work/length"
    length=$(od -An -tu4 "$work/length" | tr -d ' ')
    [ -n "$length" ] || die "the helper stopped"
    take "$length" "$work/reply"
}

# Ready: image base, relocated, grid width and height.
reply
read -r base relocated width height < <(od -An -tu4 "$work/reply")
printf 'ready: image at 0x%x, relocated %s, grid %sx%s\n' "$base" "$relocated" "$width" "$height"
[ "$width" = 293 ] && [ "$height" = 294 ] || die "expected Map1's 293x294 grid"

# Ping (request 1): the payload comes back.
printf '\x01\x00\x00\x00\x08\x00\x00\x00smoke-ok' >&"$to"
reply
[ "$(cat "$work/reply")" = smoke-ok ] || die "ping came back wrong"

# A* (request 3, the AStarRequest of exehost.c): cell (100, 100) at (4696, 4914.8) to cell (150, 150) at (7196, 7414.8), hint (100, 100),
# speed 325, radius 35, cap 800, no path flag; ctx 0, 0, 1 (gave up: no unit test), as the differential check sends them.
printf '\x03\x00\x00\x00\x34\x00\x00\x00' >&"$to"
printf '\x64\x00\x64\x00\x96\x00\x96\x00\x64\x00\x64\x00\x00\xc0\x92\x45\x00\x00\x00\x00\x66\x96\x99\x45\x00\xe0\xe0\x45\x00\x00\x00\x00' >&"$to"
printf '\x66\xb6\xe7\x45\x00\x80\xa2\x43\x00\x00\x0c\x42\x20\x03\x00\x00\x00\x00\x00\x01' >&"$to"
reply
read -r ret partial < <(od -An -tu1 -N2 "$work/reply")
read -r count < <(od -An -tu2 -j2 -N2 "$work/reply")
read -r ns < <(od -An -tu4 -j4 -N4 "$work/reply")
echo "A*: return $ret, partial $partial, $count points, $((ns / 1000)) us in the routine"
[ "$ret" = 1 ] && [ "$count" -ge 2 ] || die "the search found no path"
# The Windows helper (exehost.exe, batch 609) answers this search with 60 points; the Linux one must give the same bits.
sum=$(tail -c +9 "$work/reply" | sha256sum | cut -c1-64)
[ "$count" = 60 ] && [ "$sum" = 6f2e4eb0ba590a49584b7b9c2af2400185c2a8fff8a76823682f07357f33dff2 ] ||
    die "the points differ from the Windows helper's ($count points, sha256 $sum)"

exec {to}>&-
wait "$pid" || die "the helper exited with $?"
echo "smoke: passed"
