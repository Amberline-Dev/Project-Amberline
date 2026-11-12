# Amberline

A game server for patch 1.0.0.126 (October 2011) of a MOBA and its Adobe AIR client. Host your own server and play with the
people you invite.

**You need your own copy of the game.** Amberline includes no game or AIR client files. You and each player need the game on
patch 1.0.0.126 (its folder holds `DATA` and `LEVELS`) and its AIR client (its folder holds `assets`), on Windows 10 or 11
(64-bit). A host who may share their own copy can let their players download it from their server
([Game files](HOSTING.md#game-files)).

## Host a server

1. **Download** `Amberline-<version>-win-x64.zip` from the
   [latest release](https://github.com/Amberline-Dev/Project-Amberline/releases/latest) and unzip it.
2. **Double-click `START-HERE.cmd`.** It finds your game and AIR client folders and asks where to install (press Enter for
   `C:\Amberline`). If Windows warns you, choose **More info**, then **Run anyway**.
3. **Follow the page in your browser.** Name your server, check your game folders, make your login, then choose **Allow
   through Windows Firewall**.
4. **Add users.** Choose **Add a user** and send them the Play file and the message it shows.

From then on, start Amberline from its desktop icon, and keep its window open while you play.

## Join a server

1. **Double-click the `Play.cmd`** your host sent you.
2. **Check the two folders** it found (**Change…** picks others), then choose **Install**.
3. **Open the new desktop icon** with your host's server name, and log in with the username and password from your host.

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

Download this repository (**Code**, then **Download ZIP**), unzip it and double-click **`START-HERE.cmd`**. It is the release
zip's guide, with two more steps: it asks where the server runs (this PC or a rented server), finds your game folders, installs
any missing build tool after a yes, builds Amberline (several minutes) and installs it. [HOSTING.md](HOSTING.md#start-herecmd-step-by-step) walks through each step.

### For developers

| Path | What it is |
|---|---|
| `src/Amberline` | the core: readers of the player's game data, the simulation, the BuildingBlocks and Lua script engines, the network protocol |
| `src/Amberline.Server` | the match server, one process per match |
| `src/Amberline.Host` | starts and supervises the match servers |
| `src/Amberline.Platform` | the AIR platform: HTTPS sign-in, RTMP/AMF, XMPP chat, the host's pages and the management page |
| `src/Amberline.Rtmp` | the RTMP/AMF library (vendored rtmp-sharp; see `NOTICE`) |
| `src/Amberline.AirCompanion` | the player tool |
| `src/Amberline.Tools` | a headless bot match and audits of your own game data |
| `native/enet` | ENet (modified) and the bridge the server loads |
| `native/exehost` | the x86 helper that runs unit movement from the player's own game executable |
| `tests/Amberline.Tests` | the checks |
| `START-HERE.cmd`, `tools/guide.ps1` | the setup guide, in the release zip too: where Amberline goes, tools, build and install, step by step |
| `tools/` | `check.ps1` (build and checks), `build-server.ps1` (the deployable server and the release zip), `package-remote.ps1` (its server stage) and `player-file.ps1` with `play-bootstrap.cmd` (the players' `Play.cmd`) |

**Requirements:** the .NET SDK named in `global.json`, PowerShell 7.5 or later and Zig (it builds the two native parts for Windows
and Linux; the build scripts find it as `ZIG`, on `PATH` or in `.tools/zig`). Git is optional: a git checkout records its commit
in `BUILD.json`. You also need your own game folder on patch 1.0.0.126 (`DATA`, `LEVELS`) and AIR client folder.

```powershell
pwsh ./tools/check.ps1                                      # no game files: the data-free checks pass, the others fail
pwsh ./tools/check.ps1 -GameDataRoot <your release folder> -AirClientRoot <your AIR client folder>   # every check
```

`check.ps1` builds into a new folder under `.artifacts/` and runs the checks there; `-Scope` and `-Check` select a subset. To
build a rented server's package by hand, see [A rented server](HOSTING.md#a-rented-server).

### Contributing and licence

Amberline does not take pull requests: open an issue for a bug or a feature, or fork it (see [CONTRIBUTING.md](CONTRIBUTING.md)).
Amberline is under the MIT License (`LICENSE`); third-party licences are in `NOTICE`.

Amberline is an independent, non-commercial project. It contains no files, data or code of the game or its client.
