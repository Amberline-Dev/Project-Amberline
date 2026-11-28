#!/bin/sh
# @NAME@ for Linux (line @LINE@, release @TAG@, version @VERSION@). Run it with sh, as your own user (never as root):
#   sh FILE                  install or repair (no administrator password; the first run downloads about 1 GB)
#   sh FILE --invite LINK    the browser's Join: the player takes the invite link
#   sh FILE --shortcut ID    start a server, as its Windows shortcut does
#   sh FILE --collect-logs   one zip of logs in Downloads, with the server's address replaced by "the server"
#   sh FILE --uninstall      remove the player, its game files and its links (asks first)
# Linux support is a test.
set -eu

LINE="@LINE@"
NAME="@NAME@"
SCHEME="@SCHEME@"
INSTALL="@INSTALL@"
APP_FILE="@APPFILE@"
APP_URL="@APPURL@"
APP_SHA256="@APPSHA256@"

# Pins: the compatibility layer that runs the Windows programs (one place for every version and hash).
UMU_VERSION=1.4.4
UMU_SHA256=eb590691841f7fad3fc3ad8fd5db4ccb87849fe7948e62b28ece7a4ee48cc851
UMU_URL="https://github.com/Open-Wine-Components/umu-launcher/releases/download/$UMU_VERSION/umu-launcher-$UMU_VERSION-zipapp.tar"
PROTON=GE-Proton11-12
PROTON_SHA256=0595159e417c3dbbfb7cc3ae0d3ed15a617cc607aa23ef756ea79029d7af5d70
PROTON_URL="https://github.com/GloriousEggroll/proton-ge-custom/releases/download/$PROTON/$PROTON-x86_64.tar.gz"

DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
KIT="$DATA/amberline-$LINE"
PREFIX="$KIT/prefix"
PROGRAMS="$PREFIX/drive_c/users/steamuser/AppData/Local/Programs/$INSTALL"
SERVERS="$PROGRAMS/servers"
APPS="$DATA/applications"
SCHEME_FILE="$SCHEME.desktop"
KIT_EXEC="$KIT/kit.sh"
case "$KIT_EXEC" in
  *" "*) KIT_EXEC="\"$KIT_EXEC\"" ;;
esac

# The scripts that read the server list and the logs (python3 -I, no new dependency).
PY_SERVERS='
import json, os, re, sys
root = sys.argv[1]
names = sorted(os.listdir(root)) if os.path.isdir(root) else []
for name in names:
    if not re.fullmatch(r"[0-9a-f]{16}", name):
        continue
    try:
        with open(os.path.join(root, name, "server.json"), encoding="utf-8") as handle:
            server = json.load(handle)
    except (OSError, ValueError):
        continue
    server_name = server.get("name") if isinstance(server, dict) else None
    if isinstance(server_name, str) and re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?", server_name):
        print(name, server_name)
'
PY_LOGS='
import glob, json, os, sys, zipfile
kit, out = sys.argv[1], sys.argv[2]
prefix = os.path.join(kit, "prefix", "drive_c")
hosts = set()
for path in glob.glob(os.path.join(prefix, "users", "steamuser", "AppData", "Local", "Programs", "*", "servers", "*", "server.json")):
    try:
        with open(path, encoding="utf-8") as handle:
            host = json.load(handle).get("host")
    except (OSError, ValueError, AttributeError):
        continue
    if isinstance(host, str) and host:
        hosts.add(host.encode("utf-8"))
patterns = [
    os.path.join(kit, "kit.log"),
    os.path.join(kit, "umu.log"),
    os.path.join(prefix, "users", "steamuser", "AppData", "Local", "@PLAYERDIR@", "logs", "*.log"),
    os.path.join(prefix, "users", "steamuser", "AppData", "Local", "Programs", "@INSTALL@", "*", "logs", "*.log"),
    os.path.join(prefix, "Logs", "Game - R3d Logs", "*"),
]
files = sorted({path for pattern in patterns for path in glob.glob(pattern) if os.path.isfile(path)})
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
    for path in files:
        with open(path, "rb") as handle:
            data = handle.read()
        for host in sorted(hosts, key=len, reverse=True):
            data = data.replace(host, b"the server")
        archive.writestr(os.path.relpath(path, kit), data)
print(len(files))
'

# DIAGNOSTICS: KIT_DRY_RUN=1 prints the Wine commands instead of running them; KIT_CACHE=<dir> takes the downloads from there (checks only).
# KIT_DXVK=1 is a tester switch: it runs with Proton's DXVK instead of wined3d (never the default).

say() {
  printf '%s\n' "$*"
  if [ -d "$KIT" ]; then printf '%s\n' "$*" >> "$KIT/kit.log"; fi
}

fail() {
  printf '%s\n' "$*" >&2
  exit 1
}

fetch() { # fetch <url> <file name in dl> <sha256>
  if [ -n "${KIT_CACHE:-}" ] && [ -f "$KIT_CACHE/$2" ]; then
    cp -L "$KIT_CACHE/$2" "$KIT/dl/$2"
  elif ! curl -fsSL -o "$KIT/dl/$2" "$1"; then
    rm -f "$KIT/dl/$2"
    fail "The download did not finish. Check your internet connection, then run this command again."
  fi
  if ! printf '%s  %s\n' "$3" "$KIT/dl/$2" | sha256sum -c --status -; then
    rm -f "$KIT/dl/$2"
    fail "A downloaded file did not pass its check. Nothing was installed. Download the setup file again from the invite page."
  fi
}

prefix_busy() { # this prefix's Wine server answers on its socket; Wine names it after the prefix folder's device and inode, always under /tmp
  [ -d "$PREFIX" ] || return 1
  sock="/tmp/.wine-$(id -u)/server-$(printf '%x' "$(stat -L -c '%d' "$PREFIX")")-$(printf '%x' "$(stat -L -c '%i' "$PREFIX")")/socket"
  [ -S "$sock" ] && python3 -I -c 'import socket, sys; socket.socket(socket.AF_UNIX).connect(sys.argv[1])' "$sock" 2> /dev/null
}

wine_run() { # wine_run <program> [arguments]: a Windows path, never a Unix path (a Unix path blocks the console output of .NET programs)
  verb=waitforexitandrun
  if prefix_busy; then verb=run; fi # a second program while this prefix is in use (a Join during a game) must not wait for the game
  if [ "${KIT_DRY_RUN:-0}" = 1 ]; then
    say "DRY: PROTON_VERB=$verb umu-run $*"
    return 0
  fi
  cd "$HOME"
  wined3d=1
  if [ "${KIT_DXVK:-0}" = 1 ]; then wined3d=0; fi
  WINEPREFIX="$PREFIX" GAMEID=umu-default PROTONPATH="$KIT/$PROTON" PROTON_VERB=$verb PROTON_USE_WINED3D=$wined3d PROTON_USE_XALIA=0 \
    WINEDLLOVERRIDES="winemenubuilder.exe=d;d3dx9_39=n,b" python3 "$KIT/umu/umu-run" "$@" >> "$KIT/umu.log" 2>&1
}

user_dir() { # user_dir <XDG name> <folder name>: xdg-user-dir answers HOME itself when the user has no setting for it
  d=$(xdg-user-dir "$1" 2> /dev/null || true)
  if [ -z "$d" ] || [ "$d" = "$HOME" ]; then d="$HOME/$2"; fi
  printf '%s' "$d"
}

desktop_dir() {
  user_dir DESKTOP Desktop
}

write_entry() { # write_entry <file> <server name> <server id>
  printf '%s\n' '[Desktop Entry]' 'Type=Application' "Name=$2" "Comment=Start $2" "Exec=sh $KIT_EXEC --shortcut $3" \
    'Icon=applications-games' 'Terminal=false' 'Categories=Game;' > "$1"
}

sync_entries() { # one entry per server (in the menu and on the desktop); the entries of removed servers go
  desk=$(desktop_dir)
  for old in "$APPS"/amberline-"$LINE"-*.desktop "$desk"/amberline-"$LINE"-*.desktop; do
    [ -f "$old" ] || continue
    if [ "$(basename "$old")" != "$SCHEME_FILE" ]; then rm -f "$old"; fi
  done
  mkdir -p "$APPS"
  python3 -I -c "$PY_SERVERS" "$SERVERS" | while read -r id name; do
    write_entry "$APPS/amberline-$LINE-$id.desktop" "$name" "$id"
    if [ -d "$desk" ]; then
      write_entry "$desk/amberline-$LINE-$id.desktop" "$name" "$id"
      chmod +x "$desk/amberline-$LINE-$id.desktop"
      gio set "$desk/amberline-$LINE-$id.desktop" metadata::trusted true 2> /dev/null || true
    fi
  done
  update-desktop-database "$APPS" 2> /dev/null || true
}

write_handler() { # the invite link's scheme (the browser's Join) starts the player with the link
  printf '%s\n' '[Desktop Entry]' 'Type=Application' "Name=$NAME" 'NoDisplay=true' "MimeType=x-scheme-handler/$SCHEME;" \
    "Exec=sh $KIT_EXEC --invite %u" > "$APPS/$SCHEME_FILE"
  update-desktop-database "$APPS" 2> /dev/null || true
  xdg-mime default "$SCHEME_FILE" "x-scheme-handler/$SCHEME" 2> /dev/null || true
}

link_downloads() { # the prefix's Downloads is the user's own, so the player's file dialog finds a downloaded client zip
  down=$(user_dir DOWNLOAD Downloads)
  users="$PREFIX/drive_c/users/steamuser"
  if [ -d "$users" ] && [ -d "$down" ] && [ ! -L "$users/Downloads" ]; then
    rm -rf "$users/Downloads"
    ln -s "$down" "$users/Downloads"
  fi
}

need_app() {
  if [ "${KIT_DRY_RUN:-0}" != 1 ] && [ ! -f "$PREFIX/drive_c/amberline/$APP_FILE" ]; then
    fail "$NAME is not installed for this user. Run this file without options first."
  fi
}

install() {
  command -v python3 > /dev/null 2>&1 || fail "Python 3 is not installed. Install it from your software center, then run this command again."
  avail=$(df -Pk "$HOME" | awk 'NR == 2 { print $4 }')
  if [ "${avail:-0}" -lt 8388608 ]; then
    fail "There is not enough free space. $NAME needs about 8 GB free in your home folder."
  fi
  say "Linux support is a test. If something goes wrong, run this file with --collect-logs and send the zip."
  say "Installing $NAME for this user in $KIT."
  mkdir -p "$KIT/dl" "$APPS"
  if [ ! -f "$0" ]; then fail "Run this file with sh from its own location."; fi
  if [ "$0" != "$KIT/kit.sh" ]; then cp "$0" "$KIT/kit.sh"; fi
  if [ ! -x "$KIT/umu/umu-run" ]; then
    say "Downloading the files that run Windows programs (about 1 GB). This takes several minutes."
    fetch "$UMU_URL" umu.tar "$UMU_SHA256"
    tar -xf "$KIT/dl/umu.tar" -C "$KIT"
    rm -f "$KIT/dl/umu.tar"
  fi
  if [ ! -d "$KIT/$PROTON" ]; then
    fetch "$PROTON_URL" proton.tar.gz "$PROTON_SHA256"
    tar -xzf "$KIT/dl/proton.tar.gz" -C "$KIT"
    if [ -d "$KIT/$PROTON-x86_64" ]; then mv "$KIT/$PROTON-x86_64" "$KIT/$PROTON"; fi
    rm -f "$KIT/dl/proton.tar.gz"
  fi
  fetch "$APP_URL" "$APP_FILE" "$APP_SHA256"
  wine_run 'C:\windows\system32\cmd.exe' /c ver > /dev/null
  mkdir -p "$PREFIX/drive_c/amberline"
  cp "$KIT/dl/$APP_FILE" "$PREFIX/drive_c/amberline/$APP_FILE"
  rm -f "$KIT/dl/$APP_FILE"
  link_downloads
  # A new prefix already holds Wine's own d3dx9_39.dll (it leaves the game's HUD white): only the maker's file counts as installed.
  dll="$PREFIX/drive_c/windows/syswow64/d3dx9_39.dll"
  if [ ! -f "$dll" ] || grep -q 'Wine builtin DLL' "$dll"; then
    say "Adding a runtime file the game needs. This is downloaded from its maker."
    wine_run winetricks -q d3dx9_39 > /dev/null || true
    if [ ! -f "$dll" ] || grep -q 'Wine builtin DLL' "$dll"; then
      fail "The download did not finish. Check your internet connection, then run this command again."
    fi
  fi
  # The player's windows ask for Segoe UI, which the compatibility layer does not ship; Arial is shipped and fits.
  wine_run 'C:\windows\system32\reg.exe' add 'HKCU\Software\Wine\Fonts\Replacements' /v 'Segoe UI' /d Arial /f > /dev/null
  write_handler
  sync_entries
  if [ "${KIT_DRY_RUN:-0}" != 1 ]; then
    # The player's first window (its paste box is the fallback when the browser cannot open the link); it runs beside this script
    # and stays open when the terminal is closed (an ignored hang-up stays ignored in the programs it starts).
    (trap '' HUP; wine_run "C:\\amberline\\$APP_FILE") &
  fi
  say "Done. Go back to your browser and choose Join on the invite page."
}

uninstall() {
  size=$(du -sh "$KIT" 2> /dev/null | cut -f1 || true)
  printf 'Remove %s and its game files from this PC? This deletes %s and cannot be undone. Type yes to remove it: ' "$NAME" "${size:-nothing}"
  answer=""
  read -r answer || true
  if [ "$answer" != yes ]; then
    printf '%s\n' "Nothing was removed."
    exit 0
  fi
  desk=$(desktop_dir)
  rm -f "$APPS/$SCHEME_FILE" "$APPS"/amberline-"$LINE"-*.desktop "$desk"/amberline-"$LINE"-*.desktop
  update-desktop-database "$APPS" 2> /dev/null || true
  rm -rf "$KIT"
  printf '%s\n' "$NAME was removed."
}

collect_logs() {
  down=$(user_dir DOWNLOAD Downloads)
  mkdir -p "$down"
  out="$down/amberline-$LINE-logs-$(date +%Y%m%d-%H%M%S).zip"
  count=$(python3 -I -c "$PY_LOGS" "$KIT" "$out")
  say "Wrote $out ($count log files). Send this one file to the person who asked for it."
}

if [ "$(id -u)" -eq 0 ]; then
  fail "Run this as your own user, not as root."
fi

case "${1:-}" in
  "") install ;;
  --invite)
    if [ -z "${2:-}" ]; then fail "Give the invite link after --invite."; fi
    need_app
    wine_run "C:\\amberline\\$APP_FILE" --invite "$2"
    sync_entries
    ;;
  --shortcut)
    id="${2:-}"
    if ! printf '%s' "$id" | grep -Eq '^[0-9a-f]{16}$'; then fail "Give the server's id after --shortcut."; fi
    need_app
    wine_run "C:\\users\\steamuser\\AppData\\Local\\Programs\\$INSTALL\\companion\\Amberline.exe" --shortcut "$id"
    sync_entries
    ;;
  --collect-logs) collect_logs ;;
  --uninstall) uninstall ;;
  *) fail "Unknown option: $1. Options: --invite LINK, --shortcut ID, --collect-logs, --uninstall." ;;
esac
