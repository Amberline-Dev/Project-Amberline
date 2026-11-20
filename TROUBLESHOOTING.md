# Troubleshooting

Every problem Amberline shows says what happened and what to do. Most problems also have a short code in brackets, such as
`(A-NET-02)`. Find the code below for more help. Words in **bold** are what you see on the screen.

- [Windows warnings](#windows-warnings)
- [Error codes](#error-codes): [network](#network-a-net), [files](#files-a-files), [data](#data-a-data),
  [certificate](#certificate-a-cert), [running](#running-a-run)
- [Hiding a message](#hiding-a-message)
- [Router problems](#router-problems): UPnP, a second router, a shared provider address, a VPN
- [Messages without a code](#messages-without-a-code)
- [For players](#for-players)
- [Support log](#support-log)
- [Getting help](#getting-help)

## Windows warnings

- **"Windows protected your PC"** (SmartScreen): Amberline is new and not signed, so Windows warns you. Download Amberline only
  from this repository. For a release zip, check that its SHA-256 matches the one on the Releases page. To see it, open PowerShell
  in the zip's folder and type `Get-FileHash .\Amberline-<version>-win-x64.zip`. Then choose **More info**, and **Run anyway**.
- **Smart App Control** (Windows 11): when it is on, it blocks unsigned programs, including Amberline. Amberline cannot change
  that setting, and it never asks you to turn protections off.
- **User Account Control** (Windows asks for your permission):
  - When `START-HERE.cmd` installs a tool with `winget`, choose **Yes**.
  - When you choose **Allow through Windows Firewall** on the dashboard, Windows asks once, for a Microsoft program named
    **Windows Command Processor**. Choose **Yes**. If you choose No, the dashboard says **Windows did not allow it, so players
    cannot join yet.** ([A-NET-02](#a-net-02)).
- **Windows Security removed a file**: see [A-FILES-06](#a-files-06).

## Error codes

### Network (A-NET)

#### A-NET-01

**Another program uses a port Amberline needs (8390). Amberline may already be running for another Windows user, or the player
app may be open for another server.** The number is the port.

Amberline cannot start or update, because another program uses one of its ports. The usual causes:

- Amberline is running for another Windows user on this PC. Sign in as that user and close its window.
- You opened another host's server with its invite link, and its game client is still open. Close the game client and the game.
- Another program uses the port. Close it, then start Amberline again.

#### A-NET-02

Windows Firewall does not let your players in yet. The dashboard shows which case it is:

- **Windows Firewall blocks Amberline.** Choose **Allow through Windows Firewall**. Windows asks once, for a Microsoft program
  named Windows Command Processor. Choose **Yes**.
- **Windows did not allow it, so players cannot join yet.** You chose No when Windows asked. Choose **Allow through Windows
  Firewall** again, and choose **Yes**.
- **Windows lets players in on some network types only.** Choose **Allow through Windows Firewall** again. Players need every
  network type, including public ones.
- **Windows Firewall has Amberline's rule for another folder.** You moved or updated Amberline. Choose **Allow through Windows
  Firewall** again to point the rule at this folder.
- **Windows Firewall's rule must be updated for the new game ports.** You chose more **Games at the same time** in Settings, and
  the rule covers fewer game ports. Choose **Allow through Windows Firewall** and choose **Yes**.
- If **Windows Firewall blocks Amberline.** appears because of a block rule, remove that rule in Windows Defender Firewall
  (**Advanced settings**), then try again.
- **Amberline could not read the Windows Firewall rules on this PC.** Check that Windows Firewall is running, then try again.
- **Amberline cannot ask Windows Firewall to allow it from this folder. Its path has a character that Windows cannot pass
  safely.** Move Amberline to a folder whose path has only plain letters, digits, spaces, dots and dashes. Or add two inbound
  rules by hand in Windows Defender Firewall, **Advanced settings**. Add one for the Amberline Platform program (TCP 8391) and
  one for the Amberline server program (UDP 5219-5222, or the range Settings shows under **Games**), on Domain, Private and
  Public.

#### A-NET-03

- **Your router did not answer. It may have UPnP switched off.** Switch on UPnP in your router ("Allow programs to open ports")
  and choose **Test again**, or [open the ports by hand](HOSTING.md#your-router).
- **Your router answered but has no internet address right now.** Check that the router is connected to the internet, then
  choose **Test again**.

#### A-NET-04

Your router refused to open a port:

- **Your router does not allow programs to open ports.** Switch on UPnP for this PC, or open the ports by hand.
- **Your router has no room for more open ports.** Remove old port forwardings on the router's page, or open the ports by hand.
- **Your router keeps port 8391 for something else, such as its own port forwarding.** Open the ports by hand on the router's
  page.
- **Your router refused to open port 8391 (its error ...).** The router gave another reason, with its own number. Open the ports
  by hand on the router's page.

The hosting guide explains how to [open the ports by hand](HOSTING.md#your-router).

#### A-NET-05

**Another computer in your home already uses port 5219.** Remove that port forwarding on the router's page, or stop the program
on the other computer. Then choose **Test again**.

#### A-NET-06

**Your router is behind another router (often your provider's box).** This is called double NAT. Put the provider's box in bridge
mode, or open the same ports on both routers, or set your router as the box's "exposed host". See
[Router problems](#router-problems).

#### A-NET-07

**Your internet provider shares one address among many homes, so players cannot reach this PC.** This is called CGNAT. Ask your
provider for a public IPv4 address. Some providers give one on request. Or [host on a rented server](HOSTING.md#a-rented-server).

#### A-NET-08

**A VPN is on, so players may not reach you.** Players reach you through your normal connection. Switch the VPN off while you
host, then choose **Test again**.

#### A-NET-09

**Your address changed at 07:12. Users need the new invite link to reach you.** Your internet address changed, often because the
router restarted. Amberline already uses the new address. Choose **Copy the new invite link**, which opens Users, and send the
link to every user. Then choose **Hide**. See [When your address changes](HOSTING.md#when-your-address-changes).

#### A-NET-10

**user1 logged in, but their game could not connect to Amberline.** Games use the game ports (UDP 5219 to 5222, or the range
Settings shows under **Games**), not the sign-in port. A working login does not mean that the game ports are open.

- Check that your router forwards the game ports to this PC, with UPnP or with port forwarding as in the router guide. Also check
  that Windows Firewall lets Amberline in (the **Network** list on the dashboard).
- A VPN, or a school or work network on the player's side, can also block games.
- With **Only people on my home network**, check Windows Firewall, and check that the player's PC is on your home network.

Then ask the player to start the game again, and choose **Hide**.

### Files (A-FILES)

These codes appear on the setup page's folder step, and in the support log.

#### A-FILES-01

- **Amberline cannot find that game folder. Check its address, or choose the folder.** The folder does not exist, or no longer
  exists.
- **This folder does not contain the game. Choose the folder you downloaded the 2011 game into.** Choose the folder that contains
  `DATA` and `LEVELS`, or a folder above it.
- **This folder contains neither the game nor its AIR client.** Choose the folder you downloaded them into.

#### A-FILES-02

**This folder has another version of the game. Amberline needs version 1.0.0.126 from 2011.** Amberline works only with patch
1.0.0.126. Choose the folder from that download. It contains `projects\lol_game_client\releases\0.0.0.51`.

#### A-FILES-03

**The game folder is incomplete: LEVELS is missing. Download it again.** The name is the first missing part. The game needs
`DATA`, `DATA\Scripts`, `DATA\BuildingBlocks`, `LEVELS` and the maps in `LEVELS`.

#### A-FILES-04

- **This folder does not contain the AIR client, which has an assets folder.** Choose the folder you downloaded it into.
- **This AIR client is incomplete. 3 files are missing or damaged, for example ... Download it again.**
- **This is another version of the AIR client. Amberline needs the one for version 1.0.0.126 from 2011.** Choose the AIR client
  from the same download as the game.

#### A-FILES-05

**The game folder you chose is no longer at D:\Games\Game 2011. Choose it again.** The same message exists for the AIR client folder.
The folder was moved, renamed or deleted after Amberline last started. Choose it again on the folder step.

#### A-FILES-06

- **Windows Security may have removed Amberline.Server.exe, a part of Amberline.** Restore it in Windows Security, under
  **Protection history**, then choose **Restart Amberline**. Or download Amberline again. Your settings and users stay.
- **A part of Amberline was changed.** Download Amberline again and start it. Your settings and users stay.

At every start, Amberline compares its program files with the list it was built with.

#### A-FILES-07

**Windows gave this game folder's files no short names, so your own game stops while loading from it.** This appears as a card on
the dashboard, and as a note under the game folder on the setup page. Windows creates short names for files by default only on
drive C:. The game needs them when it is not in a folder like `projects\lol_game_client\releases\0.0.0.51`. Keep the game in
such a folder, or move it to drive C:, then choose it under **Settings**, **Your game folders**. Only **Play** on this PC is
affected. Your players and their games are not. **Hide** hides
the card.

### Data (A-DATA)

#### A-DATA-01

**Your settings come from a newer version of Amberline. Start that version, or download the newest version and start it. Your
settings were not changed.** You started an older Amberline than the one that last saved your settings.

#### A-DATA-02

**The drive Amberline keeps its data on (C:) is almost full: 600 MB free.** Or **is full**. Free at least 1 GB on that drive,
for example by emptying the Recycle Bin. Otherwise games and updates may fail to save. The message disappears by itself.

### Certificate (A-CERT)

#### A-CERT-01

**Your users' invite link and shortcuts stop working on 1 March 2028. Make a new link.** Or **Your users' invite link and
shortcuts no longer work because Amberline's certificate has expired.** Each invite link and each user's desktop icon checks
your server's certificate. This message appears 60 days before the certificate expires. New installs get a certificate that
lasts 20 years. Installs from early versions have one that lasts two years.

Choose **Make a new invite link**. Amberline makes a new certificate and restarts. This is not possible while a game runs. Then
send every user the new invite link. Their old links stop working immediately.

### Running (A-RUN)

#### A-RUN-01

**A game stopped unexpectedly at 20:14. The match ended for everyone in it.** Everyone is back in their client and can start a
new game. If it happens again, choose **Open the support log** and send it with an issue report. Then choose **Hide**.

#### A-RUN-02

- **This Amberline belongs to another Windows user on this PC.** Only the Windows user who started it can open its page.
- **This PC runs an Amberline that belongs to another Windows user, so this one cannot update it.** Sign in to Windows as that
  user and close Amberline there, then try again.

## Hiding a message

Each message under **Needs your attention** has a **Hide** button. Choose it to hide the message. A rented server has no home
router, so A-NET-02 and A-NET-03 can appear all the time there. Hide removes them from the dashboard.

A hidden message stays hidden after Amberline restarts, until its problem changes. If the problem goes away and comes back, or a
different problem appears, the message appears again. A message about a game that was already running when Amberline started
does not come back for that game.

While something is hidden, the dashboard shows how many messages are hidden, for example **2 messages hidden.**, with **Show
them** under the list. Choose it to show every hidden message again. Hidden messages do not cause the warning at the top of the
dashboard. The **Network** line says **2 hidden** instead.

## Router problems

Players outside your home reach your PC through your router. Amberline asks your router to let them in, using a router feature
called UPnP. These problems can stop that:

- **UPnP is off** ([A-NET-03](#a-net-03), [A-NET-04](#a-net-04)). Switch it on in your router's settings, where it is often
  called "Allow programs to open ports". Or open the ports by hand. The dashboard shows the steps, and so does
  [the hosting guide](HOSTING.md#your-router). Forward TCP 8391 (sign-in) and UDP 5219 to 5222 (games) to this PC.
- **A second router** ([A-NET-06](#a-net-06), double NAT). Your router is behind your provider's box. Put the box in bridge
  mode, open the same ports on both, or make your router the box's "exposed host".
- **A shared provider address** ([A-NET-07](#a-net-07), CGNAT). Your provider gives one internet address to many homes, so
  nobody outside can reach you. Ask your provider for a public IPv4 address, or use a rented server.
- **A VPN** ([A-NET-08](#a-net-08)). Switch it off while you host. On a player's side, a VPN or a school or work network can
  stop their game from connecting ([A-NET-10](#a-net-10)).
- **Everyone is at home.** You do not need the router at all. Choose
  [Only people on my home network (LAN party)](HOSTING.md#only-your-home-network-lan-party).

## Messages without a code

### START-HERE.cmd

- **Some Amberline files are missing.** Unzip the whole download into a folder first, then double-click `START-HERE.cmd` in
  that folder.
- **Something went wrong.** The window says what happened, what to do and where the full log is. If the build stopped because
  of your game or AIR client folder, double-click `START-HERE.cmd` again. Answer `N` when it asks to use your answers again, and
  choose that folder.
- **Problem: ... is still not found.** If the tool's installer asked you to restart Windows, restart, then double-click
  `START-HERE.cmd` again.
- **The search stopped at its time limit of 60 seconds.** or **You skipped the search.** Choose what it found, or choose the
  folder yourself with **Choose another folder in a window** or **Paste a path**. If it found nothing, press Enter for the
  folder window or paste the path. Next time, the folders you chose are searched first.
- **Nothing was changed. Your Amberline ... is not older than this download's build**: your Amberline is a release, or the same
  version as this release zip or newer. A release always counts as newer than a build made here. Update it with **Update now** on
  its page, or unzip the newest release zip and double-click its `START-HERE.cmd`. The guide looks only at this line's Amberline
  (the **Amberline** icon, `C:\Amberline` by default).
- **Nothing was changed. This download installs Amberline on the PC you start it on.** You typed `2` (a rented server) in the
  release zip's guide. Unzip the zip on a Windows server and type `1` there, or build a Linux server's package with the code
  download's `126\START-HERE.cmd`.
- **Nothing was changed.** after step 3: you answered `N` to **Update this Amberline now?** To install Amberline in another
  folder instead, uninstall it first (`Uninstall Amberline.cmd` in its folder), then double-click `START-HERE.cmd` again.

### Start Amberline.cmd (the release zip)

- **Some Amberline files are missing.** Unzip the whole zip again, keep its folders together, then double-click
  `START-HERE.cmd`.
- **You already have a newer Amberline** ... **so nothing changed.** Start Amberline from its icon on your desktop.
- **The running Amberline did not stop.** Close its window, then double-click `Start Amberline.cmd` again.
- **Amberline could not copy or replace its files.** Free some disk space and close every Amberline window, then double-click
  `Start Amberline.cmd` again.
- **... did not start, so the Amberline you had comes back.** The update did not start, and your previous version runs again.
- **Amberline could not put its icon on your desktop.** The window says where the program is, so you can start it from there.

### Amberline's page

- **Open Amberline from its desktop icon.** For your safety, the page opens only through Amberline. Double-click the
  **Amberline** icon, and it opens the page for you.
- **This link has expired.** Each link works once, for 2 minutes. Start Amberline again from its desktop icon.
- **Amberline refused this request.** It came from another website. Use the page that Amberline opened for you.
- **The page expired. Choose again.** or **The page expired. Try again.** Open the page again and repeat what you did.
- **Your copy cannot be shared.** (Settings, **Game files**) The reason follows. The game's or the AIR client's program is not
  the 1.0.0.126 one, a folder contains a link, or a file cannot be read. Choose the right folders (**Change the folders**) or fix
  the problem, then choose **Save** under **Game files** again. Until then, nothing is shared.
- **Amberline is checking the game files before sharing them.** (Settings, **Game files**) Wait a few minutes, then open
  Settings again.
- **Windows did not start your game client.** (**Play**) Try again, or restart Amberline. If it says **Your game client is
  already open**, look for it on the taskbar.

### Update now (the Updates page)

Each of these says **so nothing was installed**. The Amberline you have keeps running, and the download is deleted.

- **There is no newer Amberline to install.** Your version is the newest release, or newer.
- **GitHub has no download for this version that Amberline can verify.** The release has no zip and `.sha256` file where
  Amberline expects them. Use **Open the download page** and update by hand.
- **Amberline could not download the update from GitHub.** Check the internet connection, then choose **Update now** again.
- **The download was cut off or has the wrong size.** Choose **Update now** again.
- **The download does not match its SHA-256.** Choose **Update now** again. If the message appears again, do not install that
  release by hand either, and report it.
- **The download contains a different Amberline version than the release.** or **The download is not a complete Amberline
  release.** The release itself is broken. Report it, and keep the version you have.
- **Amberline could not save or unpack the download.** The disk may be full. Free some disk space, then try again.
- **Windows did not start the new Amberline's installer.** Try again, or update by hand from the download page.
- **The window of the last update is still open. Let it finish or close it, then try again.** It is the window that an earlier
  **Update now** opened (look on the taskbar).

## For players

### The sign-up page

Your host may send you a sign-up link, `https://<address>:<port>/register` (see [Sign-ups](HOSTING.md#sign-ups)). What you may see:

- **Your connection is not private** (Firefox: **Warning: Potential Security Risk Ahead**). The server uses its own certificate,
  which your browser does not know. Choose **Advanced**, then the button that continues (Chrome: **Proceed to ... (unsafe)**,
  Edge: **Continue to ... (unsafe)**, Firefox: **Accept the Risk and Continue**).
- **Open the invite** opens the server's Join page. See [The invite link](#the-invite-link).
- The page cannot be found. The host has turned sign-ups off. Ask the host.
- **Too many sign-ups. Try again in an hour.** Too many logins were made in the last hour. Try again later.
- **The page expired. Try again.** Open the link again and fill in the form again.
- **The invite link appears here once Amberline knows your address. Ask the host for the invite link.** The server does not know
  its address yet, so it has no invite link. Ask the host to send it.

### The invite link

Your host sends you one link. It opens the server's Join page.

1. Choose **Download Amberline Player** and open the file. It is the same file for everyone, from the project's release page.
   Windows may show **Windows protected your PC**. Choose **More info**, then **Run anyway**. You only need to do this once. On
   Windows 11 with **Smart App Control** turned on, Windows blocks the app, and Amberline cannot change that.
2. The app moves itself to its own folder and shows **Go back to your browser and click Join on the invite page.** Choose **Join**
   on the page. Your browser may ask to open Amberline Player. Allow it. If **Join** does not open anything, paste the link
   into the box under **If Join does not open anything, paste the invite link here.** and choose **Add**.
3. The app asks **Add <server name> (<address>)?** Choose **Yes**. Nothing is added or downloaded before you do. The app then
   downloads the server's player tool (about 64 MB, only the first time) and opens the setup window.

If you already have the app, **Join** goes straight to step 3. The app may show one of these messages instead:

- **This invite link is damaged. Ask the host to copy it again.** Part of the link is missing or changed, for example because a
  chat app cut it off. Ask for the link again and copy all of it.
- **This invite needs a newer Amberline Player. Download it from the invite page.** Your app is older than the link.
- **This invite is for the 1.0.0.<line> server. Download that line's player from the invite page.** Each patch line has its own
  app.
- **Cannot reach** the server. Check that you are online and that the host's server is running, then choose **Try again**.
- **This server does not match your invite.** Ask the host for a new invite link and open it.
- **The downloaded player tool does not match the SHA-256 the server published. Nothing was changed.** Choose **Try again**. If
  it happens again, tell the host.
- **The server sent no valid address**, **The server published no valid SHA-256 for the player tool** or **The player tool from
  the server has no ...**. Ask the host to update the server, or ask for a new invite link.

If you installed Amberline with a Play file before, your desktop icon keeps working. After its next start, the invite page's
**Join** also reaches that install, so the link of a second server only adds that server.

### The setup window

Setup shows one main button, based on what it found. The other options are under **Advanced**: the client zip, your own AIR and
game folders, the 2011 launcher and the install folder.

- **Download the game from** the server's name, with the size. Your host shares the game, and setup found no copy on your PC.
  This button downloads and installs it.
- **Use the client zip…**. The server does not share the game. Choose the client zip (`patch-1.0.0.126-client-bundle.zip`).
  Your host can tell you where to get it.
- **Install**. Setup found your own copy on this PC, or this PC is already set up (for a second server, or a repair).
- **... is still preparing its game files. Try again in a few minutes.** Your host has just turned on sharing, and the server is
  still checking its files. Choose **Try again** a few minutes later.

Messages in the setup window:

- **This is not the 1.0.0.126 AIR client.** or **This is not the 1.0.0.126 game client.** Choose the folder from the 2011
  download: the one with `assets` for the AIR client, or the one with `DATA` and `LEVELS` for the game.
- **Setup stopped before it finished.** The window says why. For example, the install folder is inside the game folder, a
  program is running from that folder, the drive does not have enough free space, or the folder belongs to an Amberline server
  on this PC. Fix the problem, then choose **Install** again. While setup still sees the problem, the button says **Try again**.
- **Windows did not allow Amberline to change a folder.** Choose an install folder inside your user folder, such as the one setup
  suggests. Then choose **Install** again.
- **A file could not be copied or moved.** Check that the drive has enough free space and is still connected. Then choose
  **Install** again.
- **The game must be installed on drive C:, where Windows can run it.** Setup installs the game on the drive Windows runs from.
  This is C: on most PCs, and the message names yours. **Change…** under **Advanced** only accepts a folder on that drive. If
  setup finds a copy of the game on another drive, it copies the game to drive C:, checks every file and then removes the
  original.
- **This zip has no game client in it** (or no AIR client, or more than one), **is not a zip file**, **contains an unsafe
  entry** or **is damaged**. Choose the client zip itself (`patch-1.0.0.126-client-bundle.zip`), or an undamaged copy of it.
- **A folder named ... is in the way.** Setup unpacks the client zip into a folder next to the install folder. Move or rename
  the folder the message names, then choose **Use the client zip…** again.

Your folders are always either where they were or complete in the install folder. The client zip is only read. **Copy** copies
the message so that you can send it to your host.

If your host shares the game ([Game files](HOSTING.md#game-files)) and setup finds no copy on your PC, setup offers **Download
the game from** the server's name:

- **No download button.** Your host has not turned on **Share the game files with players**. Or setup found your own copy. In
  that case **Install** comes first, and the download is under **Advanced**.
- **The download stopped.** If you chose **Cancel**, choose **Continue the download from** .... If the connection dropped
  (**The download from the server stopped.**), choose **Try again**. In both cases the download continues where it stopped.
- **A downloaded file did not pass its check.** A file from the server is different from the server's own list, for example
  because the host changed their game folder. Nothing was installed. Ask your host to choose **Save** under **Game files** again,
  then download again.
- **... already contains other files. Choose an empty or new folder.** right after **Checking the downloaded game…**, in the
  folder setup suggests. An older version of Amberline stopped there. After your host updates Amberline, open the invite link
  again and choose **Download the game from** ... again. Setup uses the game that is already in that folder and does not
  download it again. For a folder you chose yourself, the message means that the folder contains your own files. Choose an
  empty or new folder.

### The desktop icon

- **The desktop icon is missing.** A copy of the icon is in your install folder (the folder that contains `companion`, `air` and
  `game`), with the same name. Double-click it to play. To put the icon back on the desktop, open your server's invite link
  again.
- **Cannot reach** the server. Check that you are online. If you are, the server may be off. Ask the host to start it, then
  choose **Try again**.
- **This server does not match your invite.** The host may have set up the server again. Ask the host for a new invite link
  and open it.
- **The AIR client is missing.** or **The game client is missing.** It was moved or deleted. Open the server's invite link
  again. Setup finds your game and repairs the setup.
- **This shortcut's server is no longer set up.** Open your server's invite link again. It sets up the server and makes a new
  icon.
- The server **could not start**. Choose **Try again**. If it keeps happening, choose **Copy** and send the text to the host.
- **A game client from an earlier start is still running in the background.** Sometimes the game client does not close fully
  and keeps running without a window. Choose **Close and start**. Amberline closes only the programs that run from its own
  install folder, then starts. **Cancel** leaves them running. **Show details** lists them.
- **A game is in progress on this PC.** A game started from this install is still running. Amberline never closes it. Finish
  the game, then double-click the icon again.
- **Port 2099 is in use by another program.** (or 5222, 8390, 8393). Amberline needs these ports on your PC, and a program
  outside this install is using one, for example an Amberline server or another game client. Close that program, then choose
  **Try again**. If you do not know which program it is, restart the PC.

The details never show the server's address.

### The 2011 launcher

If you have the original 2011 launcher's folder, the desktop icon can open that launcher instead of Amberline's own window. The
launcher shows the server's news and status, and **Play** starts the game client and the game, as in 2011. Without the folder,
the game starts the usual way.

- **Where the folder goes.** During setup, choose it under **Advanced**, in the **2011 launcher (optional)** row. Or open the
  invite link again on an install that does not have it yet. Setup then asks for the folder, or offers to download it if your
  host shares it. Setup moves the launcher's own files into a `launcher` folder in your install folder, next to `companion`,
  `air` and `game`. If the folder is inside an old installation (its path contains a `projects` folder), setup copies the files
  and leaves the folder unchanged. You can also copy the launcher's files into that `launcher` folder yourself.
- **Only version 1.0.0.251 works.** Setup and the start both check the launcher's program. A folder with another version, or
  with a missing file, is not accepted, and the window says why.
- **The language follows the launcher.** The AIR client shows the language you pick in the launcher. The launcher and the game
  stay in English, and the usual start always uses English.
- **Every region leads to your server.** North America, EU West and EU Nordic & East all connect to the same server.
- **The launcher closes right away.** It needs the same Windows components as the game, and a PC that runs the game has them.
  Amberline then starts the game the usual way and shows the reason once, in the small window that starts the game:
  **The classic launcher did not open: <reason>. Starting the usual way.** The reasons are:
  - **The classic launcher folder is missing <a file>.** or **The classic launcher is not version 1.0.0.251.** Choose the
    complete folder of the 2011 launcher, version 1.0.0.251.
  - **The classic launcher's program file could not be read.** Close the program that may be using it, then start again.
  - **Port 8393, 8394 or 8395 is in use by another program.** Close that program, then start again.
  - **Another classic launcher or update service is already running on this PC.** Close the launcher's window, then start
    again.
  - **<a path> already exists and was not created by Amberline.** An old installation of the game, or part of one, is in the
    folder the launcher uses on this PC (normally `C:\rads`). This message then appears at every start. Remove that old
    installation's folders, or choose **Remove the 2011 launcher** (below) to use the usual start from then on.
  - **The folder <a path> has a space in its path, so the classic launcher cannot start from it.** The classic start is not
    possible on this PC. Amberline starts the usual way each time.
  - **It closed before it was ready.** or **It was not ready within 60 seconds.** The launcher did not open the game client.
    Start again. If it keeps happening, remove the 2011 launcher (below).
  - For any other reason, send the message to your host.
- **Last time the classic launcher did not open the game client.** After such a start, the next start asks what to do. Choose
  **Open the classic launcher again** or **Start the usual way this time**. The question appears again until the game client has
  opened through the launcher.
- **Remove the 2011 launcher.** Open Amberline's uninstaller (its entry in Windows' list of installed apps, or `Uninstall.cmd`
  in the install folder) and choose **Remove the 2011 launcher**. This removes its files and the files Amberline
  made for it on this PC. From then on, the game starts the usual way. The option only appears while the launcher's folder is in
  the install folder.
- **Windows asks for administrator rights when the launcher starts.** Entries that an old installation of the game left in
  Windows' list of installed apps can make the launcher's safety check ask for them. Remove the old entries (Settings, then
  Apps), or ask your host for help.

### Logging in

Use the username and password from your host's message, or the ones you chose on the sign-up page. After too many wrong
passwords, sign-in is blocked for 15 minutes. If you lost your password, ask your host to make a new one
(**Users**, **New password**).

### Queues

- **The client says a queue is disabled** (its own message), but other queues work. The host has turned off too many champions
  for that queue's bots, so the queue cannot start a game. Choose another queue, or ask the host to turn more champions on.
  The host can select more champions in Settings, under **Champions**, and choose **Save**. The queue works again right away.

## Support log

On the dashboard, choose **support log** under the cards, or **Open the support log** on a stopped game's card. It opens a short
report that helps someone find what went wrong. **Copy** copies it, and **Download** saves it as a .txt file. Read it before you
send it.

It contains:

- Amberline's version, your Windows version and the .NET version
- the status, the network checks and the problems listed on the dashboard
- the folder checks (ok, or the problem, never the folder's path) and the free disk space
- the games that stopped unexpectedly since the start, and why
- the last 100 lines of Amberline's log
- how many logins there are, how many users are online and how many games are running

It replaces these with marks:

- your Windows user name (`<Windows user>`), your PC's name (`<PC name>`), your server's name (`<server name>`) and your public
  address (`<public address>`, or `<home address>` for a home-network address)
- every login's username and summoner name, including logins you deleted (`<login>`)
- every internet or home-network address (`<internet address>`, `<home address>`) and e-mail address (`<e-mail>`)
- anything after the word "password", codes in web links, and long keys and tokens (`<hidden>`, `<secret>`)

The log contains no passwords. Amberline stores only a scrambled form of each password, never the password itself.

`START-HERE.cmd` keeps its own logs in `.artifacts\start-here` inside the download. They write your Windows account's name and
your PC's name as `<hidden>`.

## Getting help

Open an issue on this repository (see [CONTRIBUTING.md](CONTRIBUTING.md)). Describe what you did, what you expected and what
happened, and paste the support log. Never attach game or AIR client files.
