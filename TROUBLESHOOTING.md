# Troubleshooting

Every problem Amberline shows says what happened and what to do. Most also have a short code in brackets, such as
`(A-NET-02)`. Find the code below for more help. Words in **bold** are what you see on the screen.

- [Windows warnings](#windows-warnings)
- [Error codes](#error-codes): [network](#network-a-net), [files](#files-a-files), [data](#data-a-data),
  [certificate](#certificate-a-cert), [running](#running-a-run)
- [Router problems](#router-problems): UPnP, a second router, a shared provider address, a VPN
- [Messages without a code](#messages-without-a-code)
- [For players](#for-players)
- [Support log](#support-log)
- [Still stuck?](#still-stuck)

## Windows warnings

- **"Windows protected your PC"** (SmartScreen): Amberline is new and not signed, so Windows is careful. Download Amberline only
  from this repository. For a release zip, check that its SHA-256 matches the one on the Releases page: open PowerShell in the
  zip's folder and type `Get-FileHash .\Amberline-<version>-win-x64.zip`. Then choose **More info**, and **Run anyway**.
- **Smart App Control** (Windows 11): when it is on, it blocks unsigned programs, Amberline included. Amberline cannot change
  that setting, and it never asks you to turn protections off.
- **User Account Control** (Windows asks for your permission):
  - while `START-HERE.cmd` installs a tool with `winget`: choose **Yes**;
  - when you choose **Allow through Windows Firewall** on the dashboard: Windows asks once, for a Microsoft program named
    **Windows Command Processor**. Choose **Yes**. If you choose No, the dashboard says **Windows did not allow it. Friends cannot
    join yet.** ([A-NET-02](#a-net-02)).
- **Windows Security removed a file**: see [A-FILES-06](#a-files-06).

## Error codes

### Network (A-NET)

#### A-NET-01

**Another program uses a port Amberline needs (8390). Is Amberline already running for another Windows user, or a Play file for
another server open?** (The number is the port.)

Amberline cannot start, or cannot update, because another program listens on one of its ports. The usual causes:

- Amberline is running for another Windows user on this PC. Sign in as that user and close its window.
- You opened another host's server with its Play file, and its game client is still open. Close the game client and the game.
- Another program uses the port. Close it, then start Amberline again.

#### A-NET-02

Windows Firewall does not let your friends in yet. The dashboard says which case it is:

- **Windows Firewall blocks Amberline.** Choose **Allow through Windows Firewall**. Windows asks once, for a Microsoft program
  named Windows Command Processor: choose **Yes**.
- **Windows did not allow it, so friends cannot join yet.** You chose No in that Windows question. Choose **Allow through
  Windows Firewall** again, and press **Yes**.
- **Windows lets friends in on some network types only.** Choose **Allow through Windows Firewall** again: friends need every
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
  rules by hand in Windows Defender Firewall, **Advanced settings**: one for the Amberline Platform program (TCP 8391) and one
  for the Amberline server program (UDP 5219-5222, or the range Settings shows under **Games**), on Domain, Private and Public.

#### A-NET-03

- **Your router did not answer. It may have UPnP switched off.** Switch on UPnP in your router ("Allow programs to open ports")
  and press **Test again**, or [open the ports by hand](HOSTING.md#your-router).
- **Your router answered but has no internet address right now.** Check that the router is connected to the internet, then
  press **Test again**.

#### A-NET-04

Your router refused to open a port:

- **Your router does not allow programs to open ports.** Switch on UPnP for this PC, or open the ports by hand.
- **Your router has no room for more open ports.** Remove old port forwardings on the router's page, or open the ports by hand.
- **Your router keeps port 8391 for something else, such as its own port forwarding.** Open the ports by hand on the router's page.
- **Your router refused to open port 8391 (its error ...).** The router gave another reason, with its own number. Open the ports
  by hand on the router's page.

[Opening the ports by hand](HOSTING.md#your-router) is explained in the hosting guide.

#### A-NET-05

**Another computer in your home already uses port 5219.** Remove that port forwarding on the router's page, or stop the program
on the other computer, then press **Test again**.

#### A-NET-06

**Your router is behind another router (often your provider's box).** This is called double NAT. Put the provider's box in bridge
mode, or open the same ports on both routers, or set your router as the box's "exposed host". See
[Router problems](#router-problems).

#### A-NET-07

**Your internet provider shares one address among many homes, so friends cannot reach this PC.** This is called CGNAT. Ask your
provider for a public IPv4 address (some do it on request), or [host on a rented server](HOSTING.md#a-rented-server).

#### A-NET-08

**A VPN is on, so friends may not reach you.** Friends reach you through your normal connection. Switch the VPN off while you
host, then press **Test again**.

#### A-NET-09

**Your address changed at 07:12. Friends need the new Play file to reach you.** Your internet address changed, often because
the router restarted. Amberline already uses the new one. Choose **Download the Play file** and send it to every friend; it
holds the new address. Then choose **Hide**. See
[When your address changes](HOSTING.md#when-your-address-changes).

#### A-NET-10

**friend1 logged in, but their game could not connect to Amberline.** Games use the game ports (UDP 5219 to 5222, or the range
Settings shows under **Games**), not the sign-in port, so the login working says nothing about the game.

- Check that your router sends the game ports to this PC (UPnP, or the port forwarding of the router guide), and that Windows
  Firewall lets Amberline in (the **Network** list on the dashboard).
- A VPN, or a school or work network on your friend's side, can also block games.
- With **Only people on my home network**: check Windows Firewall, and that your friend's PC is on your home network.

Then ask your friend to start the game again, and choose **Hide**.

### Files (A-FILES)

These show on the setup page's folder step, and in the support log.

#### A-FILES-01

- **Amberline cannot find that game folder. Check its address, or choose the folder.** The folder does not exist (any more).
- **This folder does not contain the game. Choose the folder you downloaded the 2011 game into.** Choose the folder that
  holds `DATA` and `LEVELS`, or a folder above it.
- **This folder contains neither the game nor its AIR client.** Choose the folder you downloaded them into.

#### A-FILES-02

**This folder has the game, but not the 2011 version Amberline needs (version 1.0.0.126).** Amberline only works with
patch 1.0.0.126. Choose the folder from that download; it contains `projects\lol_game_client\releases\0.0.0.51`.

#### A-FILES-03

**The game folder is incomplete: LEVELS is missing. Download it again.** (The name is the first part that is missing.) The game
needs `DATA`, `DATA\Scripts`, `DATA\BuildingBlocks`, `LEVELS` and the maps in `LEVELS`.

#### A-FILES-04

- **This folder does not contain the AIR client (it holds an assets folder).** Choose the folder you downloaded it into.
- **This AIR client is incomplete: 3 files are missing or damaged (for example ...). Download it again.**
- **This is not the 2011 AIR client Amberline needs (version 1.0.0.126).** Choose the AIR client from the same download as the
  game.

#### A-FILES-05

**The game folder you chose is gone (it was at D:\Games\...). Choose it again.** The same for the AIR client folder. The folder
was moved, renamed or deleted since Amberline last started. Choose it again on the folder step.

#### A-FILES-06

- **Windows Security may have removed Amberline.Server.exe, a part of Amberline.** Restore it in Windows Security
  (**Protection history**), or download Amberline again; your settings and friends stay. Then press **Restart Amberline**.
- **A part of Amberline was changed.** Download Amberline again and start it. Your settings and friends stay.

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

**Your friends' Play files stop working on 1 March 2028. Make new ones.** Or **Your friends' Play files stopped working:
Amberline's certificate ran out.** Each Play file checks your server's certificate. This message shows 60 days before the
certificate runs out. New installs get a certificate that lasts 20 years; installs from early versions have one that lasts two.

Choose **Make new Play files**. Amberline makes a new certificate and restarts (not while a game runs). Then send every friend
the new Play file: their old ones stop working at once.

### Running (A-RUN)

#### A-RUN-01

**A game stopped unexpectedly at 20:14. The match ended for everyone in it.** Everyone is back in their client and can start a
new game. If it happens again, choose **Open the support log** and send it with an issue report. Then choose **Hide**.

#### A-RUN-02

- **This Amberline belongs to another Windows user on this PC.** Only the Windows user who started it can open its page.
- **This PC runs an Amberline that belongs to another Windows user, so this one cannot update it.** Sign in to Windows as that
  user and close Amberline there, then try again.

## Router problems

Friends outside your home reach your PC through your router. Amberline asks your router to let them in, with a router feature
called UPnP. These can stop that:

- **UPnP is off** ([A-NET-03](#a-net-03), [A-NET-04](#a-net-04)): switch it on in your router's settings (often called "Allow
  programs to open ports"), or open the ports by hand. The dashboard shows the steps, and so does
  [the hosting guide](HOSTING.md#your-router). Open TCP 8391 (sign-in) and UDP 5219 to 5222 (games) to this PC.
- **A second router** ([A-NET-06](#a-net-06), double NAT): your router sits behind your provider's box. Put the box in bridge
  mode, open the same ports on both, or make your router the box's "exposed host".
- **A shared provider address** ([A-NET-07](#a-net-07), CGNAT): your provider gives one internet address to many homes, so nobody
  outside can reach you. Ask your provider for a public IPv4 address, or use a rented server.
- **A VPN** ([A-NET-08](#a-net-08)): switch it off while you host. On a friend's side, a VPN or a school or work network can stop
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
  **Update now** on its page, or unzip the newest release zip and double-click its `START-HERE.cmd`.
- **Nothing was changed. This download installs Amberline on the PC you start it on.** You typed `2` (a rented server) in the
  release zip's guide. Unzip the zip on a Windows server and type `1` there, or build a Linux server's package with the code
  download's `START-HERE.cmd`.
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
  **Amberline** icon and it opens the page for you.
- **This link has expired.** Each link works once, for 2 minutes. Start Amberline again from its desktop icon.
- **Amberline refused this request.** It came from another website. Use the page that Amberline opened for you.
- **The page expired. Please choose again.** Open the page again and repeat what you did.
- **Your copy cannot be shared.** (Settings, **Game files**) The reason follows: the game's or the AIR client's executable is not
  the 1.0.0.126 one, a folder holds a link, or a file cannot be read. Choose the right folders (**Change the folders**) or fix that,
  then choose **Save** under **Game files** again. Until then nothing is shared.
- **Amberline is checking every file once before it shares them.** (Settings, **Game files**) Wait a few minutes, then open
  Settings again.
- **Windows did not start your game client.** (**Play**) Try again, or restart Amberline. **Your game client is already
  open**: look for it on the taskbar.

### Update now (the Updates page)

Each of these ends with **so nothing was installed**. The Amberline you have keeps running, and the download is deleted.

- **There is no newer Amberline to install.** Your version is the newest, or newer than GitHub's latest release.
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

## For players

### The sign-up page

The host's link `https://<address>:<port>/register` (see [Sign-ups](HOSTING.md#sign-ups)):

- **Your connection is not private** (Firefox: **Warning: Potential Security Risk Ahead**): the server has its own certificate,
  which the browser does not know. Choose **Advanced**, then the button that goes on (Chrome: **Proceed to ... (unsafe)**, Edge:
  **Continue to ... (unsafe)**, Firefox: **Accept the Risk and Continue**).
- The browser warns about `Play.cmd` (for example **not commonly downloaded**): it is a command file from a new site. Choose
  **Keep** (Edge: **...**, **Keep**, **Keep anyway**). When you double-click it, Windows may ask whether to run it: choose **Run**
  (or **More info**, **Run anyway**).
- The page cannot be found: the host has turned sign-ups off. Ask the host.
- **Too many sign-ups. Try again in an hour.** Too many logins were made in the last hour. Try again later.
- **The page expired. Please try again.** Open the link again and fill in the form again.
- **The Play file is not ready. Ask the host for it.** The server has no address or Play file yet: ask the host to send it.

### The Play file

When `Play.cmd` cannot finish, its window says **Play could not finish:** and why:

- **Can't reach the server.** Check that you are online and that the host's server is on, then double-click
  `Play.cmd` again.
- **The server's identity does not match this Play file.** or **This Play file does not match the server any more.** Ask the
  host for a new Play file.
- **The downloaded player tool does not match the SHA-256 the server published, so nothing was changed.** Double-click
  `Play.cmd` again; if it happens again, tell the host.
- **The server sent no valid address**, **no valid SHA-256 for the player tool**, or **The player tool from the server has no
  ...**: ask the host to update the server, or for a new Play file.
- **Windows has no app data folder for your user.** Sign in to Windows with your own account, then try again.

### The setup window

- **This is not the 1.0.0.126 AIR client.** or **This is not the 1.0.0.126 game client.** Choose the folder from the 2011
  download: the one with `assets` (the AIR client), or the one with `DATA` and `LEVELS` (the game).
- **Setup stopped before it finished.** The window says why, for example: the install folder is inside the game folder, a
  program runs from that folder (close it), the drive has not enough free space, or the folder belongs to an Amberline server
  on this PC. Fix that, then choose **Install** again.
- **Windows did not let Amberline change a folder.** Choose an install folder in your own user folder (the one setup suggests),
  then choose **Install** again.
- **Setup stopped: a file could not be copied or moved.** Check that the drive has free space and is still connected, then
  choose **Install** again.

Your folders are always where they were, or complete in the install folder. **Copy** copies what happened, to send to
your host.

When your host shares their game ([Game files](HOSTING.md#game-files)) and setup finds no copy of your own, it offers **Download
the game from** the server's name:

- **No download button.** Your host has not turned on **Share the game files with players**, setup found your own copy, or the
  server was still checking its files when setup opened. Close setup and double-click the Play file again a few minutes later.
- **The download stopped.** You chose **Cancel**, or the connection dropped (**The download from the server stopped.**). Choose
  **Download the game from** ... again: it continues where it stopped.
- **A downloaded file did not pass its check.** A file from the server differs from the server's own list, for example because the
  host changed their game folder. Nothing was installed. Ask your host to choose **Save** under **Game files** again, then download
  again.

### The desktop icon

- **Can't reach** the server: check that you are online. If you are, the server may be off: ask the host to start it, then
  choose **Try again**.
- The server **did not prove it is the server this shortcut was made for**: the host may have set the server up again. Ask the
  host for a new Play file and double-click it.
- **The AIR client is missing.** or **The game client is missing.** You moved or deleted it. Double-click the server's Play
  file again: it finds your game and repairs the setup.
- **This shortcut's server is not set up any more.** Double-click your server's Play file again: it sets the server up and
  makes a new icon.
- The server **could not start**: choose **Try again**. If it keeps happening, choose **Copy** and send the text to the host.
- **A game client from an earlier start is still running in the background.** Sometimes the game client does not close fully
  and keeps running without a window. Choose **Close and start**: Amberline closes only the programs that run from its own
  install folder, then starts. **Cancel** leaves them running. **Show details** lists them.
- **A game is in progress on this PC.** A game started from this install is still running. Amberline never closes it: finish
  the game, then double-click the icon again.
- **Port 2099 is in use by another program.** (or 5222, 8390, 8393): Amberline needs these ports on your PC, and a program
  that is not part of this install holds one, for example an Amberline server or another game client. Close it, then choose
  **Try again**. If you can't tell which program it is, restart the PC.

The details never show the server's address.

### Logging in

Use the username and password from your host's message, or the ones you chose on the sign-up page. After too many wrong passwords, sign-in is blocked for 15 minutes.
If you lost your password, ask your host: **Friends**, **New password** makes a new one.

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
- every login's username and summoner name (`<login>`);
- every internet or home-network address (`<internet address>`, `<home address>`) and e-mail address (`<e-mail>`);
- anything after the word "password", codes in web links, and long keys and tokens (`<hidden>`, `<secret>`).

No password is in it: Amberline only keeps a scrambled form of each password, never the password itself.

`START-HERE.cmd` keeps its own logs in `.artifacts\start-here` inside the download. They write your Windows account's name and
your PC's name as `<hidden>`.

## Still stuck?

Open an issue on this repository (see [CONTRIBUTING.md](CONTRIBUTING.md)). Say what you did, what you expected and what
happened, and paste the support log. Never attach game or AIR client files.
