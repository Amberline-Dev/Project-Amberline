# Troubleshooting

Every problem Amberline shows says what happened and what to do. Most also have a short code in brackets, such as
`(A-NET-02)`. Find the code below for more help. Words in **bold** are what you see on the screen.

- [Windows warnings](#windows-warnings)
- [Error codes](#error-codes): [network](#network-a-net), [files](#files-a-files), [data](#data-a-data),
  [certificate](#certificate-a-cert), [running](#running-a-run)
- [Hiding a message](#hiding-a-message)
- [Router problems](#router-problems): UPnP, a second router, a shared provider address, a VPN
- [Messages without a code](#messages-without-a-code)
- [For players](#for-players)
- [Support log](#support-log)
- [Still stuck?](#still-stuck)

## Windows warnings

- **"Windows protected your PC"** (SmartScreen): Amberline is new and not signed, so Windows is careful. Download Amberline only
  from this repository. For a release zip, check that its SHA-256 matches the one on the Releases page: open PowerShell in the
  zip's folder and type `Get-FileHash .\Amberline-132-<version>-win-x64.zip`. Then choose **More info**, and **Run anyway**.
- **Smart App Control** (Windows 11): when it is on, it blocks unsigned programs, Amberline included. Amberline cannot change
  that setting, and it never asks you to turn protections off.
- **User Account Control** (Windows asks for your permission):
  - while `START-HERE.cmd` installs a tool with `winget`: choose **Yes**;
  - when you choose **Allow through Windows Firewall** on the dashboard: Windows asks once, for a Microsoft program named
    **Windows Command Processor**. Choose **Yes**. If you choose No, the dashboard says **Windows did not allow it. Players cannot
    join yet.** ([A-NET-02](#a-net-02)).
- **Windows Security removed a file**: see [A-FILES-06](#a-files-06).

## Error codes

### Network (A-NET)

#### A-NET-01

**Another program uses a port Amberline needs (8490). Is Amberline already running for another Windows user, or the player app
for another server open?** (The number is the port.)

Amberline cannot start, or cannot update, because another program listens on one of its ports. The usual causes:

- Amberline is running for another Windows user on this PC. Sign in as that user and close its window.
- A 1.0.0.126 Amberline runs on this PC (port 5222). Both lines use that port for chat, so only one of them runs at a time:
  close the other one's window, then start this one.
- You opened another host's server with its invite link, and its game client is still open. Close the game client and the game.
- Another program uses the port. Close it, then start Amberline again.

#### A-NET-02

Windows Firewall does not let your players in yet. The dashboard says which case it is:

- **Windows Firewall blocks Amberline.** Choose **Allow through Windows Firewall**. Windows asks once, for a Microsoft program
  named Windows Command Processor: choose **Yes**.
- **Windows did not allow it, so players cannot join yet.** You chose No in that Windows question. Choose **Allow through
  Windows Firewall** again, and press **Yes**.
- **Windows lets players in on some network types only.** Choose **Allow through Windows Firewall** again: players need every
  network type, public ones too.
- **Windows Firewall has Amberline's rule for another folder.** You moved or updated Amberline. Choose **Allow through Windows
  Firewall** again to point the rule at this folder.
- **Windows Firewall's rule must be updated for the new game ports.** You chose more **Games at the same time** in Settings, and
  the rule covers fewer game ports. Choose **Allow through Windows Firewall** and press **Yes**.
- **Windows Firewall blocks Amberline** (a block rule): remove the block rule in Windows Defender Firewall (**Advanced
  settings**), then try again.
- **Amberline could not read the Windows Firewall rules on this PC.** Check that Windows Firewall is running, then try again.
- **Amberline cannot ask Windows Firewall to allow it from this folder**: the folder's path has a character Windows cannot pass
  safely. Move Amberline to a folder whose path has only plain letters, digits, spaces, dots and dashes. Or add two inbound
  rules by hand in Windows Defender Firewall, **Advanced settings**: one for the Amberline Platform program (TCP 8491) and one
  for the Amberline server program (UDP 5319-5322, or the range Settings shows under **Games**), on Domain, Private and Public.

#### A-NET-03

- **Your router did not answer. It may have UPnP switched off.** Switch on UPnP in your router ("Allow programs to open ports")
  and press **Test again**, or [open the ports by hand](HOSTING.md#your-router).
- **Your router answered but has no internet address right now.** Check that the router is connected to the internet, then
  press **Test again**.

#### A-NET-04

Your router refused to open a port:

- **Your router does not allow programs to open ports.** Switch on UPnP for this PC, or open the ports by hand.
- **Your router has no room for more open ports.** Remove old port forwardings on the router's page, or open the ports by hand.
- **Your router keeps port 8491 for something else, such as its own port forwarding.** Open the ports by hand on the router's page.
- **Your router refused to open port 8491 (its error ...).** The router gave another reason, with its own number. Open the ports
  by hand on the router's page.

[Opening the ports by hand](HOSTING.md#your-router) is explained in the hosting guide.

#### A-NET-05

**Another computer in your home already uses port 5319.** Remove that port forwarding on the router's page, or stop the program
on the other computer, then press **Test again**.

#### A-NET-06

**Your router is behind another router (often your provider's box).** This is called double NAT. Put the provider's box in bridge
mode, or open the same ports on both routers, or set your router as the box's "exposed host". See
[Router problems](#router-problems).

#### A-NET-07

**Your internet provider shares one address among many homes, so players cannot reach this PC.** This is called CGNAT. Ask your
provider for a public IPv4 address (some do it on request), or [host on a rented server](HOSTING.md#a-rented-server).

#### A-NET-08

**A VPN is on, so players may not reach you.** Players reach you through your normal connection. Switch the VPN off while you
host, then press **Test again**.

#### A-NET-09

**Your address changed at 07:12. Users need the new invite link to reach you.** Your internet address changed, often because
the router restarted. Amberline already uses the new one. Choose **Copy the new invite link** (it opens Users) and send the link
to every user; it holds the new address. Then choose **Hide**. See
[When your address changes](HOSTING.md#when-your-address-changes).

#### A-NET-10

**user1 logged in, but their game could not connect to Amberline.** Games use the game ports (UDP 5319 to 5322, or the range
Settings shows under **Games**), not the sign-in port, so the login working says nothing about the game.

- Check that your router sends the game ports to this PC (UPnP, or the port forwarding of the router guide), and that Windows
  Firewall lets Amberline in (the **Network** list on the dashboard).
- A VPN, or a school or work network on the player's side, can also block games.
- With **Only people on my home network**: check Windows Firewall, and that the player's PC is on your home network.

Then ask the player to start the game again, and choose **Hide**.

### Files (A-FILES)

These show on the setup page's folder step, and in the support log.

#### A-FILES-01

- **Amberline cannot find that game folder. Check its address, or choose the folder.** The folder does not exist (any more).
- **This folder does not contain the game. Choose the folder you downloaded the 2012 game into.** Choose the folder that
  holds the game's program, `DATA` and `LEVELS`, or a folder above it.
- **This folder contains neither the game nor its AIR client.** Choose the folder you downloaded them into.

#### A-FILES-02

**This folder has the game, but not the 2012 version Amberline needs (version 1.0.0.132).** Amberline only works with
patch 1.0.0.132. Choose the folder from that download: it holds the game's program, `DATA` and `LEVELS`.

#### A-FILES-03

**The game folder is incomplete: LEVELS is missing. Download it again.** (The name is the first part that is missing.) The game
needs `DATA`, `DATA\Scripts`, `DATA\BuildingBlocks`, `LEVELS` and the maps in `LEVELS`.

#### A-FILES-04

- **This folder does not contain the AIR client (it holds an assets folder).** Choose the folder you downloaded it into.
- **This AIR client is incomplete: 3 files are missing or damaged (for example ...). Download it again.**
- **This is not the 2012 AIR client Amberline needs (version 1.0.0.132).** Choose the AIR client from the same download as the
  game.

#### A-FILES-05

**The game folder you chose is gone (it was at D:\Games\...). Choose it again.** The same for the AIR client folder. The folder
was moved, renamed or deleted since Amberline last started. Choose it again on the folder step.

#### A-FILES-06

- **Windows Security may have removed Amberline.Server.exe, a part of Amberline.** Restore it in Windows Security
  (**Protection history**), or download Amberline again; your settings and users stay. Then press **Restart Amberline**.
- **A part of Amberline was changed.** Download Amberline again and start it. Your settings and users stay.

Amberline compares its program files with the list it was built with at every start.

### Data (A-DATA)

#### A-DATA-01

**Your settings come from a newer Amberline. Start that version, or update this one: download the newest Amberline and start
it. Amberline did not change your settings.** You started an older Amberline than the one that last saved your settings.

#### A-DATA-02

**The drive Amberline keeps its data on (C:) is almost full: 600 MB free.** Or **is full**. Free at least 1 GB on that drive,
for example by emptying the Recycle Bin, or games and updates may fail to save. The message goes by itself.

### Certificate (A-CERT)

#### A-CERT-01

**Your users' invite link and shortcuts stop working on 1 March 2028. Make a new link.** Or **Your users' invite link and
shortcuts stopped working: Amberline's certificate ran out.** Each invite link and each user's desktop icon checks your server's
certificate. This message shows 60 days before the
certificate runs out. New installs get a certificate that lasts 20 years.

Choose **Make a new invite link**. Amberline makes a new certificate and restarts (not while a game runs). Then send every user
the new invite link: their old ones stop working at once.

### Running (A-RUN)

#### A-RUN-01

**A game stopped unexpectedly at 20:14. The match ended for everyone in it.** Everyone is back in their client and can start a
new game. If it happens again, choose **Open the support log** and send it with an issue report. Then choose **Hide**.

#### A-RUN-02

- **This Amberline belongs to another Windows user on this PC.** Only the Windows user who started it can open its page.
- **This PC runs an Amberline that belongs to another Windows user, so this one cannot update it.** Sign in to Windows as that
  user and close Amberline there, then try again.

## Hiding a message

Each message under **Needs your attention** has a **Hide** button. Choose it to set a message aside. A rented server has no home router, so
A-NET-02 and A-NET-03 can show all the time there; Hide clears them from the dashboard. A hidden message stays hidden after Amberline
restarts, until its problem changes: if the problem goes away and comes back, or a different problem appears, the message shows
again. A message about a game that was already running when Amberline started does not come back for that game.

While something is hidden, the dashboard says how many are hidden, for example **2 messages hidden.**, with **Show them** under the list; choose it
to show every hidden message again. Hidden messages do not make the warning at the top of the dashboard appear; the **Network** line
says **2 hidden** instead.

## Router problems

Players outside your home reach your PC through your router. Amberline asks your router to let them in, with a router feature
called UPnP. These can stop that:

- **UPnP is off** ([A-NET-03](#a-net-03), [A-NET-04](#a-net-04)): switch it on in your router's settings (often called "Allow
  programs to open ports"), or open the ports by hand. The dashboard shows the steps, and so does
  [the hosting guide](HOSTING.md#your-router). Open TCP 8491 (sign-in) and UDP 5319 to 5322 (games) to this PC.
- **A second router** ([A-NET-06](#a-net-06), double NAT): your router sits behind your provider's box. Put the box in bridge
  mode, open the same ports on both, or make your router the box's "exposed host".
- **A shared provider address** ([A-NET-07](#a-net-07), CGNAT): your provider gives one internet address to many homes, so nobody
  outside can reach you. Ask your provider for a public IPv4 address, or use a rented server.
- **A VPN** ([A-NET-08](#a-net-08)): switch it off while you host. On a player's side, a VPN or a school or work network can stop
  their game from connecting ([A-NET-10](#a-net-10)).
- **Everyone is at home**: you do not need the router at all. Choose
  [Only people on my home network (LAN party)](HOSTING.md#only-your-home-network-lan-party).

## Messages without a code

### START-HERE.cmd

- **The Amberline files are not all here.** Unzip the whole download into a folder first, then double-click `START-HERE.cmd`
  in that folder.
- **Something went wrong.** The window says what happened and what to do, and where the full log is. If the build stopped
  because of your game or AIR client folder, double-click `START-HERE.cmd` again, answer `N` when it asks to use your answers
  again, and choose that folder.
- **Problem: ... is still not found.** If the tool's installer asked you to restart Windows, restart, then double-click
  `START-HERE.cmd` again.
- **The search stopped at its time limit of 60 seconds.** (or **You skipped the search.**): pick what it found, or choose the
  folder yourself: **Choose another folder in a window** or **Paste a path** (when it found nothing, press Enter for the folder
  window or paste the path). Next time, the folders you chose are searched first.
- **Nothing was changed. Your Amberline ... is not older than this download's build**: your Amberline is a release (a
  release always counts as newer than a build made here), or the same version as this release zip or newer. Update it with
  **Update now** on its page, or unzip the newest release zip and double-click its `START-HERE.cmd`. The guide looks only at this
  line's Amberline (the **Amberline 132** icon, `C:\Amberline 132` by default): a 1.0.0.126 Amberline on this PC is another
  program, and the guide never counts, updates or replaces it.
- **Nothing was changed. This download installs Amberline on the PC you start it on.** You typed `2` (a rented server) in the
  release zip's guide. Unzip the zip on a Windows server and type `1` there, or build a Linux server's package with the code
  download's `132\START-HERE.cmd`.
- **Nothing was changed.** (after step 3) You answered `N` to **Update this Amberline now?**. To install Amberline
  in another folder instead, uninstall it first (`Uninstall Amberline.cmd` in its folder), then double-click `START-HERE.cmd`
  again.

### Start Amberline.cmd (the release zip)

- **Amberline's program files are not all here.** Unzip the whole zip again, keep its folders together, then double-click
  `START-HERE.cmd`.
- **You already have a newer Amberline.** Nothing changed. Start Amberline from its icon on your desktop.
- **The running Amberline did not stop.** Close its window, then double-click `Start Amberline.cmd` again.
- **Amberline could not copy or replace its files.** Close every Amberline
  window, then double-click `Start Amberline.cmd` again.
- **... did not start, so the Amberline you had comes back.** The update did not start; your previous version runs again.
- **Amberline could not put its icon on your desktop.** The window says where the program is, so you can start it from there.

### Amberline's page

- **Open Amberline from its desktop icon.** For your safety, the page opens only through Amberline. Double-click the
  **Amberline 132** icon and it opens the page for you.
- **This link has expired.** Each link works once, for 2 minutes. Start Amberline again from its desktop icon.
- **Amberline refused this request.** It came from another website. Use the page that Amberline opened for you.
- **The page expired. Please choose again.** Open the page again and repeat what you did.
- **Your copy cannot be shared.** (Settings, **Game files**) The reason follows: the game's or the AIR client's executable is not
  the 1.0.0.132 one, a folder holds a link, or a file cannot be read. Choose the right folders (**Change the folders**) or fix that,
  then choose **Save** under **Game files** again. Until then nothing is shared.
- **Amberline is checking every file once before it shares them.** (Settings, **Game files**) Wait a few minutes, then open
  Settings again.
- **Windows did not start your game client.** (**Play**) Try again, or restart Amberline. **Your game client is already
  open**: look for it on the taskbar.

### Update now (the Updates page)

Each of these ends with **so nothing was installed**. The Amberline you have keeps running, and the download is deleted.

- **There is no newer Amberline to install.** Your version is the newest `p132-v` release, or newer than it.
- **GitHub names no download for this version that Amberline can check.** The release has no zip and `.sha256` file at its own
  address on this repository. Use **Open the download page** and update by hand.
- **Amberline could not download the update from GitHub.** Check the internet connection, then choose **Update now** again.
- **The download was cut off or is not the size GitHub names.** Choose **Update now** again.
- **The download does not match its SHA-256.** Choose **Update now** again. If it keeps saying so, do not install that release
  by hand either, and report it.
- **The download holds another Amberline version than the release names.** or **The download is not a whole Amberline
  release.** The release itself is broken: report it, and keep the version you have.
- **Amberline could not save or unpack the download (the disk may be full).** Free some disk space, then try again.
- **Windows did not start the new Amberline's installer.** Try again, or update by hand from the download page.
- **The window of the last update is still open: let it finish or close it, then try again.** It is the window an earlier
  **Update now** opened (look on the taskbar).

### Old 1.0.0.126 shortcuts on the Network list

While Amberline guides old 1.0.0.126 shortcuts, these lines show on the dashboard's **Network** list (see
[Moving from the 1.0.0.126 line](HOSTING.md#moving-from-the-100126-line)). They wait; they are not in **Needs your attention**.
Amberline checks them again every 5 minutes, and when you choose **Test again**.

- **Your Amberline for 1.0.0.126 was not found**. Guiding counts only the 1.0.0.126 Amberline that your Windows account
  installed, in `C:\Amberline` or the folder you chose. An install whose folders or files are links does not count. If you
  uninstalled it, choose **Stop guiding old shortcuts** on **Users**.
- **Update your Amberline for 1.0.0.126 to 0.1.26 or later and start it once**. Other people who use this PC can still change its
  files: it is not locked to your Windows account yet. Its version 0.1.26 or later locks its folder when it starts. On its page,
  choose **Updates**, then **Update now**. When the update has started it, close its window, then choose **Test again** here.
- **Update your Amberline for 1.0.0.126 to 0.1.26 or later**. Old shortcuts can be offered the move only by a 1.0.0.126 Amberline
  of version 0.1.26 or later. On its page, choose **Updates**, then **Update now**. When the update has started it, close its
  window, then choose **Test again** here.
- **Close your Amberline for 1.0.0.126**. Only one of the two lines runs at a time. Close the 1.0.0.126 window. Guiding starts
  within 5 minutes, or when you choose **Test again**.
- **Port 8391 is in use by another program**. (The number is the port.) Another program on this PC is using TCP port 8391. Close
  it, then choose **Test again**.
- **Allow it through Windows Firewall**. Guiding needs its own rule, **Amberline 132 old sign-in**. Choose **Allow through
  Windows Firewall** on **Users**, then **Yes** when Windows asks ([A-NET-02](#a-net-02)). The dashboard's **Windows Firewall lets
  players in** check does not change.

## For players

### The sign-up page

The host's link `https://<address>:<port>/register` (see [Sign-ups](HOSTING.md#sign-ups)):

- **Your connection is not private** (Firefox: **Warning: Potential Security Risk Ahead**): the server has its own certificate,
  which the browser does not know. Choose **Advanced**, then the button that goes on (Chrome: **Proceed to ... (unsafe)**, Edge:
  **Continue to ... (unsafe)**, Firefox: **Accept the Risk and Continue**).
- **Open the invite** opens the server's Join page: see [The invite link](#the-invite-link).
- The page cannot be found: the host has turned sign-ups off. Ask the host.
- **Too many sign-ups. Try again in an hour.** Too many logins were made in the last hour. Try again later.
- **The page expired. Please try again.** Open the link again and fill in the form again.
- **The invite link appears here once Amberline knows your address. Ask the host for the invite link.** The server has no
  address yet, so it has no invite link. Ask the host to send it.

### The invite link

Your host sends one link. It opens the server's Join page:

1. **Download Amberline Player** there (one file from the project's release, the same for everyone) and open it. Windows may say
   **Windows protected your PC**: choose **More info**, then **Run anyway** (once). On Windows 11 with **Smart App Control** on,
   Windows blocks the app completely; Amberline cannot change that.
2. The app puts itself in its own folder and says **Now click Join on your invite page, or paste the invite link here.** Choose
   **Join** on the page (the browser asks once to open Amberline Player: allow it), or paste the link into the app's box and choose
   **Add**.
3. The app asks **Add <server name> (<address>)?** Choose **Yes**. Nothing is added or downloaded before that. Then it
   downloads the server's player tool (about 64 MB, once) and opens the setup window.

If you already have the app, the link's **Join** goes straight to step 3. What the app may say instead:

- **This invite link is damaged. Ask the host to copy it again.** Part of the link is missing or changed (for example cut off by a
  chat app). Ask for it again and copy all of it.
- **This invite needs a newer Amberline Player. Download it from the invite page.** Your app is older than the link.
- **This invite is for the 1.0.0.<line> server. Download that line's player from the invite page.** The two patch lines have
  separate apps.
- **Can't reach** the server: check that you are online and that the host's server is on, then choose **Try again**.
- The server **did not prove it is the server this shortcut was made for**: ask the host for a new invite link and open it.
- **The downloaded player tool does not match the SHA-256 the server published; nothing was changed.** Choose **Try again**; if it
  happens again, tell the host.
- **The server sent no valid address**, **no valid SHA-256 for the player tool**, or **The player tool from the server has no
  ...**: ask the host to update the server, or for a new invite link.

Installed with a Play file before? Your desktop icon keeps working as it is. Its next start lets the invite page's **Join** reach your
install, so a second server's link only adds that server.

### The setup window

- **This is not the 1.0.0.132 AIR client.** or **This is not the 1.0.0.132 game client.** Choose the folder from the 2012
  download: the one with `assets` (the AIR client), or the one with `DATA` and `LEVELS` (the game).
- **Setup stopped before it finished.** The window says why, for example: the install folder is inside the game folder, a
  program runs from that folder (close it), the drive has not enough free space, or the folder belongs to an Amberline server
  on this PC. Fix that, then choose **Install** again.
- **Windows did not let Amberline change a folder.** Choose an install folder in your own user folder (the one setup suggests),
  then choose **Install** again.
- **Setup stopped: a file could not be copied or moved.** Check that the drive has free space and is still connected, then
  choose **Install** again.

- **This zip has no game client in it** (or no AIR client, or more than one), **is not a zip file**, **holds an unsafe entry** or
  **is damaged**: choose the client zip itself (`patch-1.0.0.132-client-bundle.zip`), or an undamaged copy of it.
- **A folder named ... is in the way.** Setup unpacks the client zip into a folder beside the install folder. Move or rename the
  folder it names, then choose **Use the client zip…** again.

Your folders are always where they were, or complete in the install folder. The client zip is only read. **Copy** copies what
happened, to send to your host.

A 1.0.0.132 server's invite link sets up this line's own Amberline (its app is **Amberline 132 Player**): its settings in `%LOCALAPPDATA%\Amberline 132 Player`, its
install folder (`Amberline 132` by default) and its entry **Amberline 132** in Windows' list of installed apps. If you also play
on a 1.0.0.126 server, that setup (**Amberline Player**, **Amberline**) stays apart, with its own game files: setup refuses its
install folder (**The install folder already has a folder for the AIR client**), and removing one setup leaves the other. A server
whose name is already on your desktop gets a numbered icon (for example **Amberline 2**).

When your host shares their game ([Game files](HOSTING.md#game-files)) and setup finds no copy of your own, it offers **Download
the game from** the server's name:

- **No download button.** Your host has not turned on **Share the game files with players**, setup found your own copy, or the
  server was still checking its files when setup opened. Close setup and open the invite link again a few minutes later.
- **The download stopped.** You chose **Cancel**, or the connection dropped (**The download from the server stopped.**). Choose
  **Download the game from** ... again: it continues where it stopped.
- **A downloaded file did not pass its check.** A file from the server differs from the server's own list, for example because the
  host changed their game folder. Nothing was installed. Ask your host to choose **Save** under **Game files** again, then download
  again.

### The desktop icon

- **The desktop icon is gone.** Its copy is in your install folder (the folder that holds `companion`, `air` and `game`), under the
  same name: double-click it to play. Open your server's invite link again to put the icon back on the desktop.
- **Can't reach** the server: check that you are online. If you are, the server may be off: ask the host to start it, then
  choose **Try again**.
- The server **did not prove it is the server this shortcut was made for**: the host may have set the server up again. Ask the
  host for a new invite link and open it.
- **The AIR client is missing.** or **The game client is missing.** You moved or deleted it. Open the server's invite
  link again: it finds your game and repairs the setup.
- **This shortcut's server is not set up any more.** Open your server's invite link again: it sets the server up and
  makes a new icon.
- The server **could not start**: choose **Try again**. If it keeps happening, choose **Copy** and send the text to the host.
- **A game client from an earlier start is still running in the background.** Sometimes the game client does not close fully
  and keeps running without a window. Choose **Close and start**: Amberline closes only the programs that run from its own
  install folder, then starts. **Cancel** leaves them running. **Show details** lists them.
- **A game is in progress on this PC.** A game started from this install is still running. Amberline never closes it: finish
  the game, then double-click the icon again.
- **Port 2199 is in use by another program.** (or 5222, 8490, 8393): Amberline needs these ports on your PC, and a program
  that is not part of this install holds one, for example an Amberline server or another game client (a 1.0.0.126 server's too:
  only one of the two plays at a time). Close it, then choose **Try again**. If you can't tell which program it is, restart the PC.

The details never show the server's address.

### The 2011 launcher

If you have the original 2011 launcher's folder, the desktop icon can open that launcher instead of Amberline's own window. It shows
the server's news and status, and **Play** starts the game client and the game, as in 2011. Without the folder, the game starts the
usual way, as before.

- **Where the folder goes.** Choose it in the optional **2011 launcher folder** row during setup. Or open the invite link again on an install
  that does not have it: setup then asks for the folder, or offers to download it when your host shares it. Setup moves the launcher's
  own files into a `launcher` folder inside your install folder, beside `companion`, `air` and `game`. If the folder lies inside an old
  installation's folders (a path with a `projects` folder in it), setup copies the files and leaves the folder as it was. You can also copy the launcher's files into that
  `launcher` folder yourself.
- **Only version 1.0.1.19 works.** Setup and the start both check the launcher's program. A folder with another version, or with a
  file missing, is refused, and the window says why.
- **The language.** The launcher and the game stay in English, and the usual start always uses English.
- **Every region leads to your server.** North America, EU West and EU Nordic & East all connect to the same server.
- **The launcher closes at once.** It needs the same Windows parts as the game, and a PC that runs the game has them. Amberline then
  starts the game the usual way and says why, once, in the small window that starts the game:
  **The classic launcher didn't open: <reason>. Starting the usual way.** The reasons:
  - **The classic launcher folder is missing <a file>.** or **The classic launcher is not version 1.0.1.19.** Choose the complete
    folder of the 2011 launcher, version 1.0.1.19.
  - **The classic launcher's program file could not be read.** Close the program that may be using it, then start again.
  - **Port 8393, 8394 or 8395 is in use by another program.** Close that program, then start again.
  - **Another classic launcher or update service is already running on this PC.** Close the launcher's window, then start again.
  - **<a file> is running from <a folder>; close it first.** A launcher, AIR client or game of the other patch line (1.0.0.126) or
    of this one still runs from the launcher's folder on this PC. Close it, then start again.
  - **The other patch line's 2011 launcher files under <a folder> could not all be removed (a program still uses them).** The
    other patch line is 1.0.0.126. Close the program that uses them, then start again.
  - **<a path> already exists and was not created by Amberline.** An old install of the game, or what is left of one, is in the
    folder the launcher uses on this PC (normally `C:\rads`), so this comes back at every start. Remove that old install's folders,
    or choose **Remove the 2011 launcher** (below) to start the usual way from then on.
  - **The folder <a path> has a space in its path, so the classic launcher cannot start from it.** The classic start is not possible
    on this PC; Amberline starts the usual way each time.
  - **It closed before it was ready.** or **It was not ready within 60 seconds.** The launcher did not open the game client. Start
    again. If it keeps happening, remove the 2011 launcher (below).
  - Any other reason: send the message to your host.
- **Last time the classic launcher did not open the game client.** After such a start, the next start asks: choose **Open the classic
  launcher again**, or **Start the usual way this time**. The question comes back until the game client has opened through the
  launcher.
- **Remove the 2011 launcher.** In Amberline's uninstaller (its entry **Amberline 132** in Windows' list of installed apps, or `Uninstall.cmd` in the
  install folder), choose **Remove the 2011 launcher**. Its files go, with the files Amberline built for it on this PC, and the usual
  start is used from then on. The choice shows only while the launcher's folder is in the install folder.
- **Windows asks for administrator rights when the launcher starts.** Entries left in Windows' list of installed apps by an old install
  of the game can make the launcher's safety check ask for them. Remove the old entries (Settings, then Apps), or ask your host for help.

### Logging in

Use the username and password from your host's message, or the ones you chose on the sign-up page. After too many wrong passwords, sign-in is blocked for 15 minutes.
If you lost your password, ask your host: **Users**, **New password** makes a new one.

### Queues

- **The client says a queue is disabled** (its own queue-disabled message) although other queues work: the host has turned off too
  many champions for that queue's bots, so it cannot start a game. Choose another queue, or ask the host to turn more champions on.
  Host: in Settings, **Champions**, tick more champions and choose **Save**; the queue works again at once.

## Support log

On the dashboard, choose **support log** (under the cards; **Open the support log** on a stopped game's card). It opens a short
report that helps someone find what went wrong. **Copy** copies it; **Download** saves it as a .txt file. Read it before you send
it.

It holds:

- Amberline's version, your Windows version and the .NET version;
- the status, the network checks and the problems listed on the dashboard;
- the folder checks (ok, or the problem; never the folder's path) and the free disk space;
- the games that stopped unexpectedly since the start, and why;
- the last 100 lines of Amberline's log;
- how many logins there are, how many are online and how many games run.

It hides, with marks:

- your Windows user name (`<Windows user>`), your PC's name (`<PC name>`), your server's name (`<server name>`) and your public
  address (`<public address>`, or `<home address>` for a home-network address);
- every login's username and summoner name, also of a login you deleted (`<login>`);
- every internet or home-network address (`<internet address>`, `<home address>`) and e-mail address (`<e-mail>`);
- anything after the word "password", codes in web links, and long keys and tokens (`<hidden>`, `<secret>`).

No password is in it: Amberline only keeps a scrambled form of each password, never the password itself.

`START-HERE.cmd` keeps its own logs in `.artifacts\start-here` inside the download. They write your Windows account's name and
your PC's name as `<hidden>`.

## Still stuck?

Open an issue on this repository (see [CONTRIBUTING.md](../CONTRIBUTING.md)). Say what you did, what you expected and what
happened, and paste the support log. Never attach game or AIR client files.
