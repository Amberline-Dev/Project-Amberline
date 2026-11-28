# Amberline on Linux (test)

Linux support is a test. It is new, and it has been tried on only a few setups. If something goes wrong, tell the person who
invited you. Include the log file from [Sending logs](#sending-logs).

The player runs Windows programs with Wine. The game and its AIR client are the same files as on Windows, so you need your own
copy of them, as described in [README.md](README.md).

## What you need

- An x86-64 Linux desktop, or a Steam Deck in Desktop Mode.
- Python 3.
- About 8 GB of free disk space.
- An internet connection.

Do not run the setup as root. It installs for your user only and asks for no administrator password.

## Steps

1. Open the invite link in your browser on the Linux PC. Choose the Linux download. The file is saved in your Downloads folder.
2. Open a terminal and run the command the invite page shows. It has this form:

   ```
   sh ~/Downloads/Amberline-132-Player-<version>-linux.sh
   ```

   The first run downloads about 1.3 GB and takes several minutes.
3. Go back to the invite page and choose **Join**. Allow the browser to open the player.
4. Continue in the player's window, as on Windows. Setup asks for the game and the AIR client.

If nothing happens after step 3, run the command from step 2 again, then choose **Join**.

## What is downloaded, and from where

The versions are the pinned versions in the script.

- **The player** for the invite's release, from that release's page on GitHub. The script checks its SHA-256 before it runs.
- **umu-launcher**, which starts the Windows programs: [github.com/Open-Wine-Components/umu-launcher](https://github.com/Open-Wine-Components/umu-launcher).
- **GE-Proton**, a build of Wine: [github.com/GloriousEggroll/proton-ge-custom](https://github.com/GloriousEggroll/proton-ge-custom/releases).
- **The Steam Linux Runtime**, a set of Linux libraries. umu-launcher fetches it from Valve's servers on the first run.
- **Microsoft's DirectX June 2010 redistributable**, which the game needs for its text and icons. winetricks fetches it from
  Microsoft on your PC. Amberline does not include it.

The client zip is not changed.

## Where the files go

- The player and its Windows files: `~/.local/share/amberline-132` (the 1.0.0.126 line uses `~/.local/share/amberline-126`).
- Menu entries and the desktop icon for each server, and the link handler that opens the invite.
- The setup file itself stays in your Downloads folder.

## Removing it

Run this command:

```
sh ~/.local/share/amberline-132/kit.sh --uninstall
```

The command asks first and shows how much space it will free. It removes the Windows files, including the game files, the
downloads, the menu entries and the link handler. The client zip you used stays where it is.

## Sending logs

Run this command:

```
sh ~/.local/share/amberline-132/kit.sh --collect-logs
```

It puts one zip file in your Downloads folder. The zip holds the logs of the script, the player and the game. It replaces the
host's address with the word "server". Send that one file to the person who invited you.

## Known limits

- The frame rate and the sound are not yet confirmed on a real Linux desktop.
- The 2011 launcher, the AIR client and the game run under Wine. Some things may behave differently from Windows.
- The invite link may not open the player in every browser yet.
