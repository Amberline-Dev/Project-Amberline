# Amberline

A game server for patch 1.0.0.126 (October 2011) of a MOBA and its Adobe AIR client. Host your own server and play with the
people you invite.

**You need your own copy of the game.** Amberline includes no game or AIR client files. You and each player need the game on
patch 1.0.0.126 (its folder holds `DATA` and `LEVELS`) and its AIR client (its folder holds `assets`), or the client zip
(`patch-1.0.0.126-client-bundle.zip`) that holds both, on Windows 10 or 11 (64-bit). A host who may share their own copy can let their players download it from their server
([Game files](HOSTING.md#game-files)).

## Downloads

| Patch | Newest release | Hosting guide |
|---|---|---|
| 1.0.0.126 (October 2011) | [Amberline, latest release](https://github.com/Amberline-Dev/Project-Amberline/releases/latest) | [HOSTING.md](HOSTING.md) |
| 1.0.0.132 (January 2012) | [Amberline 132 0.1.10](https://github.com/Amberline-Dev/Project-Amberline/releases/tag/p132-v0.1.10) | [132/HOSTING.md](132/HOSTING.md) |

The **Releases** box on the right shows only the 1.0.0.126 line's newest release; [all releases](https://github.com/Amberline-Dev/Project-Amberline/releases)
lists both lines.

## Host a server

1. **Download** `Amberline-<version>-win-x64.zip` from the
   [latest release](https://github.com/Amberline-Dev/Project-Amberline/releases/latest) and unzip it.
2. **Double-click `START-HERE.cmd`.** It finds your game and AIR client folders (or takes the client zip: paste its path or drag it
   into the window when it asks for the game folder) and asks where to install (press Enter for
   `C:\Amberline`). If Windows warns you, choose **More info**, then **Run anyway**.
3. **Follow the page in your browser.** Name your server, check your game folders, make your login, then choose **Allow
   through Windows Firewall**.
4. **Add users.** Choose **Add a user** and send them the Play file and the message it shows.

From then on, start Amberline from its desktop icon, and keep its window open while you play.

**Hosting patch 1.0.0.132 (January 2012)?** Read [132/HOSTING.md](132/HOSTING.md) and [132/TROUBLESHOOTING.md](132/TROUBLESHOOTING.md) instead: that line's releases are tagged `p132-v` and are never the latest release, which is patch 1.0.0.126's.

## Join a server

1. **Double-click the `Play.cmd`** your host sent you.
2. **Choose Use the client zip…** and pick the client zip (`patch-1.0.0.126-client-bundle.zip`): setup unpacks it and installs.
   Or **check the two folders** it found (**Change…** picks others), then choose **Install**.
3. **Open the new desktop icon** with your host's server name, and log in with the username and password from your host.

**Optional: the original 2011 launcher.** If you have that launcher's folder, choose it in the optional **2011 launcher folder** row
during setup, or run `Play.cmd` again on an install that does not have it yet. Then starting the game opens the launcher, as in 2011.
Without it, the game starts as it always has. See [The 2011 launcher](TROUBLESHOOTING.md#the-2011-launcher).

## Help

- [HOSTING.md](HOSTING.md): every page and button, updates, users who sign up themselves, a LAN party and a rented server.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md): every error message, Windows warnings, router problems and help for players.
- Found a bug? [Open an issue](CONTRIBUTING.md).

---

## More details

### What Amberline is

An open-source server for the 1.0.0.126 build (October 2011) of the game and its Adobe AIR client: the match server, the AIR
platform (sign-in, chat, lobbies, matchmaking, store and profiles), a setup page in your browser and a small player tool for your
players. Your PC can be the server, or a server you rent. Amberline reads your own copy of the game on your PC, and shares it with
your players only if you turn that on in Settings.

### Build it yourself instead

Download this repository (**Code**, then **Download ZIP**), unzip it, open its `126` folder and double-click **`START-HERE.cmd`**. It is the release
zip's guide, with two more steps: it asks where the server runs (this PC or a rented server), finds your game folders, installs
any missing build tool after a yes, builds Amberline (several minutes) and installs it. [HOSTING.md](HOSTING.md#start-herecmd-step-by-step) walks through each step.

### For developers

The repository holds two patch lines side by side:

| Folder | What it is |
|---|---|
| `126/` | patch 1.0.0.126 (October 2011), the line the releases are built from |
| `132/` | patch 1.0.0.132 (January 2012), released under `p132-v` tags (never the latest release); see [132/HOSTING.md](132/HOSTING.md) |
| `host/` | every file both lines share, with the same text in both |

A line's folder holds its own projects (`<line>/Amberline.slnx`), the files that differ between the lines and its natives; each
project also compiles the same folder under `host/` (`HostRoot` in the root `Directory.Build.props`). A file is in `host/` or in a
line folder, never in both. Inside each of them:

| Path | What it is |
|---|---|
| `src/Amberline` | the core: readers of the player's game data, the simulation, the BuildingBlocks and Lua script engines, the network protocol |
| `src/Amberline.Server` | the match server, one process per match |
| `src/Amberline.Host` | starts and supervises the match servers |
| `src/Amberline.Platform` | the AIR platform: HTTPS sign-in, RTMP/AMF, XMPP chat, the host's pages and the management page |
| `src/Amberline.Rtmp` | the RTMP/AMF library (vendored rtmp-sharp; see `NOTICE`) |
| `src/Amberline.AirCompanion` | the player tool |
| `src/Amberline.Tools` | a headless bot match and audits of your own game data |
| `native/enet` | ENet (modified) and the bridge the server loads (in each line folder) |
| `native/exehost` | the x86 helper that runs unit movement from the player's own game executable (in each line folder) |
| `tests/Amberline.Tests` | the checks |
| `START-HERE.cmd`, `patch.json` | the line's setup guide start and its facts (in each line folder); the guide itself is `host/tools/guide.ps1` (the release zip has it in `tools/`) |
| `tools/` | `check.ps1` (build and checks, in each line folder); in `host/tools`: `build-server.ps1` (the deployable server and the release zip, `-Patch 126` or `-Patch 132`), `package-remote.ps1` (its server stage) and `player-file.ps1` with `play-bootstrap.cmd` (the players' `Play.cmd`) |

**Requirements:** the .NET SDK named in `global.json`, PowerShell 7.5 or later and Zig (it builds the two native parts for Windows
and Linux; the build scripts find it as `ZIG`, on `PATH` or in `.tools/zig`). Git is optional: a git checkout records its commit
in `BUILD.json`. You also need your own game folder on patch 1.0.0.126 (`DATA`, `LEVELS`) and AIR client folder.

```powershell
pwsh ./126/tools/check.ps1                                  # no game files: the data-free checks pass, the others fail
pwsh ./126/tools/check.ps1 -GameDataRoot <your release folder> -AirClientRoot <your AIR client folder>   # every check
```

`check.ps1` builds its line into a new folder under the line's `.artifacts/` and runs the checks there; `-Scope` and `-Check`
select a subset. The 132 line is checked the same way with `./132/tools/check.ps1` and its own 1.0.0.132 folders. To
build a rented server's package by hand, see [A rented server](HOSTING.md#a-rented-server).

### Contributing and licence

Amberline does not take pull requests: open an issue for a bug or a feature, or fork it (see [CONTRIBUTING.md](CONTRIBUTING.md)).
Amberline is under the MIT License (`LICENSE`); third-party licences are in `NOTICE`.

Amberline is an independent, non-commercial project. It contains no files, data or code of the game or its client.
