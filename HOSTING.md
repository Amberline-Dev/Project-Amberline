# Hosting an Amberline server

This guide walks you through every step: installing Amberline, the setup page in your browser, the dashboard, inviting friends
and keeping Amberline up to date. Words in **bold** are what you see on the screen. If something goes wrong, the error has a
short code such as `(A-NET-02)`: look it up in [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

- [What you need](#what-you-need)
- [Two ways to install](#two-ways-to-install)
- [START-HERE.cmd, step by step](#start-herecmd-step-by-step)
- [The release zip](#the-release-zip)
- [The Amberline folder](#the-amberline-folder)
- [The Amberline window](#the-amberline-window)
- [The setup page](#the-setup-page)
- [The dashboard](#the-dashboard)
- [Invite a friend](#invite-a-friend)
- [Friends](#friends)
- [Advanced](#advanced)
- [Settings and Uninstall](#settings-and-uninstall)
- [Updates](#updates)
- [Play on this PC](#play-on-this-pc)
- [Only your home network (LAN party)](#only-your-home-network-lan-party)
- [When your address changes](#when-your-address-changes)
- [Removing Amberline](#removing-amberline)
- [A rented server](#a-rented-server)

## What you need

- A PC with Windows 10 or Windows 11, 64-bit.
- Your own copy of the game, patch 1.0.0.126 (October 2011). Its folder holds `DATA` and `LEVELS`. In a full
  download it is several folders deep; Amberline looks for it.
- The AIR client that goes with it. Its folder holds `assets`.
- Enough free disk space for Amberline's own copy of the game and the AIR client, and a few hundred MB for the program.
  `START-HERE.cmd` tells you how much before it copies anything.
- For friends outside your home: a router that lets them in. Amberline asks your router for this itself (with UPnP, a router
  feature). If your router does not allow it, the dashboard shows you how to do it by hand.

Each friend needs their own copy of the same game and AIR client, unless you share yours with them ([Game files](#game-files)).

## Two ways to install

Both start with the same guide: double-click **`START-HERE.cmd`**. It asks the same questions in the same order.

- **The release zip** (`Amberline-<version>-win-x64.zip`, on this repository's Releases page when one is published): nothing is
  built. Amberline brings everything it needs, so you install nothing else. The guide skips its steps 4 and 5.
- **The code download** (**Code**, then **Download ZIP**): the guide builds Amberline on your PC. It finds the tools a build
  needs and installs a missing one after you say yes. Use this when there is no release zip, or to try the newest code.

Both install the same Amberline in the same way. A release always counts as newer than a build made with `START-HERE.cmd`, so it
replaces that build when you start it.

## START-HERE.cmd, step by step

Unzip the release zip or the code download (**Code**, then **Download ZIP**) and double-click `START-HERE.cmd`. In the release
zip, a double-click on `Start Amberline.cmd` opens it too. A window named **Amberline setup** asks one question at a time: type each answer, then press Enter. A yes or no always says **Type Y for yes or N for no.**
To stop, close the window.

If you ran it before, it shows **Last time you chose:** and asks **Use these answers again?** Even after `N`, the folders you
chose last time are searched first.

### Step 1 of 6: Where will your server run?

Type `1` for **On this PC** (see also [LAN party](#only-your-home-network-lan-party)) or `2` for **On a rented server** (see
[A rented server](#a-rented-server)). The release zip installs Amberline only on the PC it runs on, so there `2` changes
nothing: the guide says to unzip the zip on a Windows server and type `1` there, or to build a Linux server's package from the
code download.

### Step 2 of 6: Your game and AIR client folders

The guide searches this PC for both, for at most 60 seconds; press `S` to skip. It looks in the usual places first (the folders
you chose last time, the folder around the download, Desktop, Downloads, Documents and the usual game folders), then on every
drive.

Copies that can't be used come first (**Can't be used, <why>: <folder>**), then **The search found these. Choose one:** with the
usable ones, **Choose another folder in a window** and **Paste a path**. Answer **Type a number**. With nothing usable it says
**None found. Choose it yourself.**: press Enter for a folder window, or paste a path. A folder with the game somewhere inside
also works (**Looking inside that folder**). It asks for the game folder first, then the AIR client folder.

### Step 3 of 6: Where Amberline goes

- **Where should Amberline be installed?**: type a full path, or press Enter for `C:\Amberline`. With a **Problem:** line it
  refuses a whole drive, Program Files, a OneDrive folder, a folder inside the download or inside your game or AIR client folder,
  and a folder that holds other files.
- If Amberline is installed already, the guide skips steps 1 and 2 (**Steps 1 and 2 of 6: not needed.**), names your version
  and this download's and asks **Update this Amberline now?** Updating also repairs it; settings, logins, friends and the
  install's game and AIR client stay. `N` changes nothing. `R` builds a package for a rented server instead (step 1 answered
  **On a rented server**; the release zip says that this package is built from the code download). To install elsewhere,
  uninstall first (`Uninstall Amberline.cmd` in its folder). If yours is a release (or newer), the guide asks **Build a package
  for a rented server instead?** (the release zip ends at once with **Nothing was changed**): update it with the newest release
  zip. A release zip of the version you have starts it (**Amberline ... is already installed. Starting it.**).
- It lists what it copies, their size and the drive's free space. From an AIR client folder only the AIR client's own files go.
- **Delete the originals?**: with `Y` your original game and AIR client folders go once the copies are checked (on the same
  drive they are moved). Only the clients' own files are deleted. With `N` you keep two copies.

### Step 4 of 6: Build tools

The release zip is built already: the guide says **Steps 4 and 5 of 6: not needed.** and goes on with step 6.

From the code download, the guide looks for the .NET SDK, PowerShell 7 and Zig. For a missing one it says **Missing: <tool> (...)** and asks **Install
<tool> now? This accepts its license.**

- `Y` installs it with `winget`. If Windows asks, choose **Yes**.
- `N` shows its download page. Install it, then **Press Enter to continue.**
- Still not found after its installer asked for a restart: restart Windows, then double-click `START-HERE.cmd` again.

### Step 5 of 6: Building Amberline

This takes a few minutes, longer the first time. A line names each part (**Part 1 of 7** to **Part 7 of 7**), **Still building:
N minutes so far** comes once a minute, and it ends with **Done: the build is finished**. The details go to the log.

### Step 6 of 6: Installing and starting Amberline

A new window named **Amberline** copies the program, the game and the AIR client into your install folder (it names the size; the
copy takes several minutes), deletes the originals if you said yes, puts an **Amberline** icon on your desktop and opens its page
in your browser (**Starting Amberline and opening its page in your browser.**). After an update it first says **Press Enter to
continue.**; the update ends any game in progress. Then follow [the setup page](#the-setup-page).

The guide ends with a **Summary**: the install folder, what happens now, the setup page, how to update and its log (in
`.artifacts\start-here` in the download; your PC's and Windows account's names are written as `<hidden>`). The window then says
**Press Enter to close.** To update later: choose **Update now** on Amberline's page (see [Updates](#updates)), or unzip
the newest release and double-click its `START-HERE.cmd`. With the code: download the newest code and double-click
`START-HERE.cmd` again.

## The release zip

1. Download `Amberline-<version>-win-x64.zip` from this repository's Releases page. Check that its SHA-256 matches the one
   listed there. To see it, open PowerShell in the zip's folder and type `Get-FileHash .\Amberline-<version>-win-x64.zip`.
2. Unzip the whole zip into a folder. Keep the folders inside it together.
3. Double-click `START-HERE.cmd` (`Start Amberline.cmd` opens it too). If Windows warns you, see
   [Windows warnings](TROUBLESHOOTING.md#windows-warnings).
4. Answer its questions, as in [START-HERE.cmd, step by step](#start-herecmd-step-by-step). It skips steps 4 and 5.
5. A window named **Amberline** copies the program, the game and the AIR client into the install folder, puts an
   **Amberline** icon on your desktop and opens its setup page in your web browser.

You can delete the zip and the unzipped folder afterwards. The zip's `READ ME FIRST.txt` says the same.

## The Amberline folder

Everything Amberline keeps is in the install folder (`C:\Amberline` unless you chose another):

| In the folder | What it is |
|---|---|
| `app` | the Amberline program |
| `app.previous` | the version you had before the last update; it comes back by itself if an update does not start |
| `data` | your settings, your friends' logins, the server's certificate and the logs |
| `game` | your copy of the game, when Amberline copied it in |
| `air` | your copy of the AIR client, when Amberline copied it in |
| `READ ME.txt` | what the folder is, and how to remove Amberline |
| `amberline-install.json` | a small marker: Amberline finds its data folder through it |

Outside the folder there is only the desktop icon and the small file `%LOCALAPPDATA%\Amberline\install.json`, which tells a
later update where the folder is. Keep the folder where it is: the icon and later updates look for it there.

An Amberline installed by an earlier version, in your user folder (`%LOCALAPPDATA%\Amberline`), stays there and is updated there.

## The Amberline window

While Amberline runs, its window says **Amberline is running - close this window to stop.**

- You can minimise the window. **Closing it stops Amberline and ends any game in progress.** Keep it open while your friends play.
- Start Amberline from the **Amberline** icon on your desktop. If it is running already, the icon just opens its page again.
- If your browser did not open, start Amberline again from the icon, or copy the link the window shows into your browser within
  2 minutes.

For your safety the page opens only through Amberline itself, and only for the Windows user who started it. Its address works
only on this PC: nobody else can open it.

## The setup page

The first time, the page takes you through four steps. The top of the page says which step you are on. Each step ends in
**Continue**; the folder step and the address step also have **Back**. The login step has none (the name and the folders are
saved by then: change them later in [Settings](#settings-and-uninstall)).

A button that takes a while (for example **Play**, or the copy of your game files) says at once what it is doing, such as
**Starting…**, and a line under it says how long it takes. Just wait: nothing is broken.

### Step 1 of 4: Name your server

**Welcome to Amberline.** Type a **Server name**. Your friends see it on the desktop icon that starts their game. Use 1 to 40
letters, digits, spaces, dots, `-` or `_`, starting and ending with a letter or digit.

### Step 2 of 4: Your game folders

The page shows two rows, **Game folder** and **AIR client folder**.

- **Found:** shows the folder Amberline found; **Not found yet** shows none. Each row has **Choose folder…**: a Windows folder
  window opens, and it may open behind your browser (the button says so while it waits).
- You can also paste a folder's address into the field and choose **Check**.
- Choose **Continue** when both rows are right.

Amberline checks each folder: the game must be patch 1.0.0.126 and complete, the AIR client the matching 2011 one. A folder that
is wrong says why, with its code. A note under the game folder (for example missing voices and fonts) only matters for
[Play on this PC](#play-on-this-pc); hosting works regardless.

If the folders are outside your install folder, the page asks **Copy your game files into C:\Amberline?** It shows what comes in,
its size and the free space, and asks **Delete the original folders you picked?** (**No** is chosen first).

- **Copy the game files** copies them. The page says at once how big the copy is and that it takes several minutes; a progress
  bar follows it. At the end the page says **Your game files are in C:\Amberline**. If the copy fails, it says **Nothing was
  moved, and your folders are as they were.** and offers **Try again**. **Yes, delete the originals** deletes the folders you
  picked once the copy is complete.
- **Use them where they are** keeps your folders where they are.

### Step 3 of 4: Your own login

You play from this PC too, so you need a login of your own.

- **Username**: 3 to 24 letters (a to z), digits or `_`. You type it when you log in.
- **Summoner name**: what other players see.
- **Password**: at least 10 characters. Amberline never shows it again.
- **Start at**: **Level 30, everything unlocked** or **Level 1, earn everything**.

Choose **Continue**. If more than one thing needs changing, the page lists every problem at once, each beside its field.

### Step 4 of 4: Your address

Your friends' computers find yours by its address. **How do your friends find this PC?**

- **The address your router reports**: shown when your router tells Amberline its internet address. This is the easiest
  choice, and Amberline notices when the address changes.
- **A name for my address** (or **I type my address or a name for it**): type your internet address (four numbers with dots)
  or a name for it (a dynamic DNS name, which keeps pointing at your address when it changes; most home addresses change now
  and then, often when the router restarts). A home-network address, an address your provider shares, or an IPv6 address is
  refused with the reason.
- **Find my address on the internet**: Amberline asks one outside service (`api.ipify.org`) once. That service sees your
  address. Amberline never asks it otherwise.
- **Only people on my home network (LAN party)**: see [below](#only-your-home-network-lan-party).

The tick box **Tell me about new versions (Amberline asks GitHub once a day; GitHub sees your address).** is ticked. Untick it
if you do not want that; you can change it later on the [Updates](#updates) page.

Choose **Continue**. Amberline asks your router for its address first, which takes up to 20 seconds; the button says so. Then
the dashboard opens.

## The dashboard

The menu at the top has **Dashboard**, **Invite a friend**, **Friends**, **Settings**, **Advanced** and **Updates**. The bottom
of each page shows Amberline's version.

**Status** shows what Amberline is doing, for example **Running since 18:02. Friends can join.**, your address, and when a friend
last connected. Until the first status comes in, the page says **Checking…**. **Stop Amberline** and **Restart Amberline** ask
first, and say how many games are running: stopping or restarting ends them for everyone; **Cancel** keeps Amberline running.
After a restart the page comes back by itself; after a stop, start Amberline again from its desktop icon.

**Play** opens your own game client (see [below](#play-on-this-pc)).

**Online now** lists who is logged in, and **Games** the games that are running.

**Network** lists five checks, each with its light:

1. **Amberline is listening on this PC**.
2. **Windows Firewall lets friends in**.
3. **Found your router; it opened the ports for this PC** (the sign-in and game ports).
4. **Your router has a public internet address** (no second router, no shared provider address).
5. **A friend reached Amberline from the internet**. Until then: **Waiting for the first friend to start the Play file.**

A line that is not green says what is wrong, and the same problem is listed under **Needs your attention**. Friends can join when
Windows Firewall lets them in, and either your router opened the ports and has a public address, or a friend from the internet
already reached Amberline (for example because you opened the ports by hand).

**Needs your attention** lists every problem, with its code and what to do. A button appears beside a problem only while it
needs one:

- **Allow through Windows Firewall**: Windows asks once, for a Microsoft program named **Windows Command Processor**. Press
  **Yes**; the button says **Waiting…** until you answer. Amberline then adds two rules, **Amberline sign-in** and
  **Amberline games**.
- **Test again**: runs the network checks again (up to a minute), for example after you changed something on your router.
- **Download the Play file**, **Make new Play files**, **Hide** and **Open the support log**: see the codes in
  [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

### Your router

While Amberline runs, it asks your router (with UPnP) to send the sign-in port, TCP 8391, and the game ports, UDP 5219 to 5222,
to this PC (one game port per game: with more [games at the same time](#settings-and-uninstall), more ports, for example UDP
5219 to 5226 for eight). When Amberline stops, it removes them again. If your router does not do this, the dashboard shows **Open the ports
on your router by hand**:

1. Open your router's page. Its address and password are often on a sticker on the router.
2. Find "Port forwarding" (or "Virtual server" or "NAT"; some routers say "Port sharing").
3. Forward TCP 8391 (sign-in) and UDP 5219 to 5222 (games) to this PC.
4. Give this PC a fixed address on the router ("DHCP reservation" or "Always use this address"), so the forwarding keeps
   pointing at it.
5. Press **Test again**. Or switch on UPnP in the router ("Allow programs to open ports"), then press **Test again**.

The dashboard fills in your router's address, this PC's address, your router's model and the game ports where it knows them.

## Invite a friend

1. Choose **Invite a friend**.
2. Fill in **Their username** (3 to 24 letters a to z, digits or `_`), and if you like a **Summoner name** (empty means the
   username). Choose **Start them at** level 30 or level 1.
3. Leave **Password** empty and Amberline makes one (four common words and two digits), or type one of at least 10 characters.
4. Choose **Create their login**. If more than one thing needs changing, the page lists every problem at once, each beside its
   field. When it works, the page says **friend1 is ready** and shows two things to send:
   - **Download the Play file for** your server: the file `Play.cmd`. It is the same file for every friend. It sets up their game
     and puts your server's name on their desktop.
   - A message with their username and password. **Copy** copies it. Send it privately.

The password is shown only now. If your friend loses it, use [Friends](#friends), **New password**.

Until the address step is done, Invite says **Your address first** and links back to the setup page.

While [Sign-ups](#sign-ups) is on, Invite also shows the sign-up link, with **Copy**: a friend can make their own login there instead.

## Friends

**Friends** lists every login you made, whether they are online now, and the date of their last game.

- **New password**: for a friend who lost theirs. The old password stops working at once, and the friend is logged out. The new
  one is shown once, in a message to send.
- **Remove access**: the friend can no longer log in. Their row then says **Access removed** and the date.
- **Give access again** (on a friend whose access you removed): a new password, shown once in a message to send, as **New
  password** does. **Back** returns to the list.

No button deletes the friend's games.

## Advanced

**Advanced** is the server's full management page. Its friend parts point to **Invite a friend** and **Friends**. Its **Online
now** table updates by itself every 5 seconds without reloading the page, so you can type in its forms at any time; **Pause
updates** stops it. Its other parts:

- **Send a notice to players**: a line at the top of every logged-in game client until you clear it (send it empty) or
  Amberline restarts.
- **Give RP**: RP for a login, to buy champions and skins in the store. Nobody pays.
- **Make a friend log in again**: the next time their PC connects, they log in again with their username and password.
- **Set a new password**: for any login, yours too. The player is logged out and logs in again with the new password.
- **Set a new password**: choose **Save**; the page then says **Saved.**
- **Make a reward code**: a code that gives IP and RP once, typed in the store's **Redeem a code** page. The next page shows it
  once, with **Copy**. **Reward codes** lists them; **Cancel code** stops one that is not used yet.
- **Leavers** and **Player reports**: who left matchmade games or dodged, with **Clear**, and the reports players sent.

When a form has a problem, the page comes back with **There is a problem** at the top, a link to the field, and what you typed
(never a password).

## Settings and Uninstall

**Settings** shows what the first run asked, each part with a link to change it. Each form there ends in **Save**, and **Cancel**
leaves it without saving; after **Save** Settings says **Saved.**

- **Server name**: **Change the server name** has the same rule as the first step. After **Save** the page offers **Download the
  Play file**. Your friends' desktop icon shows the name their Play file holds, so send them the new one.
- **Your address**: **Change your address** has the same choices as the address step (for example to switch the LAN party on
  or off). After **Save**, send your friends the new Play file: the one they have still looks for this PC at the old address.
- **Your game folders**: **Change the folders** works like the folder step. Amberline restarts to use new folders, so it refuses
  while a game is running. In an installed Amberline it offers to copy folders from elsewhere into the install folder. The page is
  headed **Change the folders**; after **Save** Amberline restarts and comes back here, where **Saved.** says so.
- **Games**: **Games at the same time**, 1 to 32 (4 at first). Each game uses about one CPU core, some memory and its own game
  port; the page shows the ports, for example UDP 5219 to 5222. After **Save** it counts for the next game that starts: a running
  game goes on, and with a lower number a new game waits until fewer run. The router gets the new ports at once. Windows Firewall
  asks you first: with more games the dashboard says **Windows Firewall's rule must be updated for the new game ports.** Choose
  **Allow through Windows Firewall** there and press **Yes**. Fewer games need nothing. Play files stay as they are.
- **Updates**: whether the daily check is on; **Change the update check** opens the [Updates](#updates) page.
- **Play file**: **Download the Play file**, and **Make new Play files**, which makes a new certificate and restarts: every
  friend's Play file stops working, so send them all the new one. It asks first; **Cancel** comes back to Settings.
- **Sign-ups**: see below.
- **Game files**: see below.
- **Cheat commands**: every login on your server, yours included (marked **(you)**), with a box each. A ticked login can use the
  testing commands in every game it plays here, from the next game that starts; nobody is ticked at first, and bots never can.
  Tick or untick the boxes and choose **Save**. Only you can change the list, on this page; your friends cannot. If you had
  turned on the old **Allow cheat commands in my games**, your own login starts ticked.
- **Uninstall Amberline**: see below.

### Sign-ups

**Let people register themselves** is off at first. Tick it and choose **Save**: anyone with the link can then make their own
login, and Settings shows the link, `https://<your address>:<the friends' port>/register`, with **Copy**. Send it to whoever you
like; Invite shows it too.

- The page asks for a username, a summoner name (empty means the username), a password of at least 10 characters and the start
  level. It lists every problem at once. When it works, it offers **Download the Play file**, the same file Invite gives.
- Their browser warns that the connection is not private: Amberline's own certificate is not one the browser knows. They choose
  **Advanced**, then the button that goes on (Chrome: **Proceed to ... (unsafe)**, Edge: **Continue to ... (unsafe)**, Firefox:
  **Accept the Risk and Continue**). Another PC or browser warns again; it is the same certificate the Play file checks.
- Each address can make 12 logins an hour, and the page makes 30 an hour in all. After that it says **Too many sign-ups. Try again
  in an hour.**
- New logins appear in [Friends](#friends), where **Remove access** stops one. Untick the box and **Save** to close the page at
  once; nothing else of Amberline is ever on that port.

### Game files

**Share the game files with players** is off at first. Beside it Settings says: you share your own copy of the game, so make sure
you may share it; Amberline itself never ships these files. Tick it and choose **Save**: a player whose PC has no game or AIR client
of their own can then download yours with the Play file. Anyone with your Play file can download it.

- Amberline first checks every file once, which takes a few minutes; Settings says so until it is done, then shows what it shares:
  the game folder and the AIR client folder, each with its size. The download uses your internet upload.
- It shares exactly the game folder you chose (with its language files beside it) and the AIR client's own files. It never shares
  what the game and the AIR client write as they run (their logs, crash files and the last match's command line), anything else in
  those folders, or anything of Amberline's own (logins, settings, certificate).
- A copy whose game or AIR client executable is not the 1.0.0.126 one, or a folder that holds a link, is not shared: Settings says
  **Your copy cannot be shared** and why. Fix the folder, then choose **Save** again.
- Each player downloads at most four files at a time, and all players together sixteen, so a game that is running keeps its share of
  your connection. A player whose download stops continues where it stopped.
- Untick the box and **Save** to stop sharing at once.

On the player's side, the setup window then offers **Download the game from** your server's name, with the size. It checks every
file against the list your server sends and both executables against the 1.0.0.126 ones, and puts the game in the same folder a
player's own copy would go. A player who has their own copy uses it as before.

### Uninstall Amberline

**Uninstall Amberline** in Settings names Amberline's folder and its size, and what goes: the program, the desktop icon, the
small file that tells a later Amberline where its folder is, the two Windows Firewall rules (if you let Amberline through,
Windows asks once, for **Windows Command Processor**: press **Yes**), and the ports Amberline opened on your router. Two tick
boxes, both off: **Also delete the game and AIR client copies** and **Also delete your logins and settings** (your friends'
logins and Play files then stop working for good). What you keep stays in the folder, and its `READ ME.txt` says how to use it
again (install Amberline into the same folder). Type `uninstall` and choose **Uninstall Amberline**; **Cancel** leaves the page.
While a game runs, the page says so and offers no button.

Without the browser: double-click `Uninstall Amberline.cmd` in the install folder. It asks the same questions in its window
(close Amberline first). An Amberline that runs from an unpacked zip or a build folder is not installed: its page says there is
nothing to uninstall; close its window and delete that folder.

## Updates

The **Updates** page shows your version and what the last check found. The tick box turns the daily check on or off (choose
**Save**; the page then says **Saved.**); **Check now** asks GitHub once, which takes up to 20 seconds. Amberline never downloads
or installs anything by itself.

When a new version is out, the page shows **Amberline 1.2.3 is out** (with the new number), its notes and **Update now**:

1. Choose **Update now**. If a game is running, the page first says **Updating ends the game in progress.**; choose **Update
   now** again to go on, or **Cancel**.
2. The page shows the download, for example **Downloading Amberline 1.2.3 (220 MB). This takes a few minutes.**, with a
   progress bar. Keep the page and the Amberline window open.
3. Amberline takes the zip and its `.sha256` file only from this repository's own release of that version. It checks the zip's
   size and SHA-256, and that it holds that version. Then it opens a new Amberline window (look on the taskbar). That window
   works as if you had double-clicked the new zip's `Start Amberline.cmd`, without asking you again: it names both versions and
   says **You chose Update now on Amberline's page, so the update goes on.** It stops the running Amberline, installs the new
   one and starts it, and its page opens in your browser. Your settings and friends stay. If the new version does not start,
   the version you had comes back by itself.
4. If any check fails, the page says why in one sentence and **The Amberline you have keeps running.** Nothing was installed,
   and the download is deleted.

The download goes into the `data\updates` folder of your install. The next update replaces it.

You can still update by hand. Under **Or update by hand**, the page has **Open the download page** and the steps:

1. Download the new zip from this repository's Releases page and unzip it.
2. Double-click its `START-HERE.cmd` (`Start Amberline.cmd` opens it too) and answer `Y` to **Update this Amberline now?**.
   The new window names your version and the zip's, says that updating ends any game in progress and that closing the window
   keeps the one you have, then **Press Enter to continue.** Your settings and friends stay.
3. If the new version does not start, the version you had comes back by itself.

A zip with an older version than yours changes nothing. If you installed with `START-HERE.cmd`, you can also download the
newest code and double-click `START-HERE.cmd` again; it names your version and this download's and offers to update. If yours
is a release, a build made there changes nothing. Your friends' player tool updates itself the next time they start the game.

## Play on this PC

**Play** opens the game client on this PC, connected to your own server. The button says **Starting…** at once; the client's
window opens by itself, in up to a minute. Log in with your own login. The first time takes a few minutes, because Amberline
makes its own copy of your AIR client (your AIR client folder is only read).

## Only your home network (LAN party)

If all your friends are in your home (on the same router or Wi-Fi), choose **Only people on my home network (LAN party)** at
the address step.

- Amberline fills in this PC's address on your home network (for example `192.168.1.20`); you can type it yourself.
- Amberline then leaves your router alone. Only Windows Firewall needs to let your friends in.
- Friends outside your home cannot join. The dashboard, Invite and the message you send say so.
- The network checks leave the router out, and a friend from your home network turns the friend light green.
- A router problem the address step finds (a second router, a shared provider address) shows there as a tip, not a problem: it
  matters only for friends outside your home.
- To switch it on or off later: **Settings**, **Change your address**. Then send your friends the new Play file.

## When your address changes

Most home internet addresses change now and then, often when the router restarts.

- If you chose **Use the address your router reports**, Amberline asks the router again every 5 minutes. When the address
  changes, Amberline uses the new one at once and the dashboard says **Your address changed** (code A-NET-09). Choose **Get the
  new Play file** and send it to every friend: their old icons cannot reach you until they run it. Then choose **Hide this
  message**.
- A name for your address (a dynamic DNS name) avoids this: the name follows your address.
- To type a new address or name yourself: **Settings**, **Change your address**.

## Removing Amberline

The easy way: **Settings**, **Uninstall Amberline**, or `Uninstall Amberline.cmd` in the install folder (see
[Uninstall Amberline](#uninstall-amberline)). By hand:

1. Close the Amberline window.
2. Delete the install folder (`C:\Amberline` unless you chose another). This also deletes Amberline's copy of the game and the
   AIR client in it.
3. Delete the **Amberline** icon on your desktop.
4. Delete the small file `%LOCALAPPDATA%\Amberline\install.json`.
5. If you let Amberline through Windows Firewall, delete its two rules, **Amberline sign-in** and **Amberline games**, in
   Windows Defender Firewall, **Advanced settings**, **Inbound Rules**.

The `READ ME.txt` in the install folder lists the same steps, with your folder's exact path.

## A rented server

A rented server is a computer in a data centre that is always on and has its own internet address. Your friends then do not
depend on your PC or your router.

### With START-HERE.cmd

Run the code download's `START-HERE.cmd` on your own PC and type `2` at step 1 (the release zip builds no server package).
It asks:

- **Type the address**: a name or a number address with dots, as your hosting company shows it, without `https://` and without
  a port.
- **Type a server name, or press Enter for Amberline**: your friends see it on their desktop icon.
- **Which system does your server run?** `1` for Windows, `2` for Linux.

At step 2 choose the game and AIR client folders on your PC: the build checks them. At step 3 choose where the server package
goes (press Enter for `C:\Amberline\server-package`). Nothing is copied or deleted on your PC. After the build, step 6 says
**Do these in order:**

1. Copy the whole server package folder to your server (with Remote Desktop for Windows, or with WinSCP or `scp` for Linux).
2. Copy your own game folder and AIR client folder to the server as well.
3. On the server, open `platform\platform-data\platform.json` (on Linux `platform/platform-data/platform.json`) in a text editor
   and replace every value in `<...>`: the game folder, the full path of the server program (`server\Amberline.Server.exe` on
   Windows, `server/Amberline.Server` on Linux) and the AIR client folder, as they are on the server. On Windows, write each `\`
   in them as `\\`. If your address is a name, also put the server's number address (four numbers with dots) in `gameHost`.
4. In your hosting company's firewall, open TCP port 8391 and UDP ports 5219 to 5222.
5. Start the platform and keep it running. On Windows: open the `platform` folder and start `Amberline.Platform.exe`. On Linux,
   in the copied folder:

   ```sh
   chmod +x server/Amberline.Server server/exehost platform/Amberline.Platform
   cd platform
   ./Amberline.Platform
   ```

6. Make a login for each friend on the management page (see below).
7. Give each friend their login and the Play file, the package's `Play.cmd`. It is the only file they need.

File Explorer opens the package folder, and the guide ends with a **Summary** that names it. Building into the same package
folder again keeps its certificate, so the `Play.cmd` files you gave out keep working.

### By hand

Install the [developer requirements](README.md#requirements) on a PC, then build the package with your server's address:

```powershell
pwsh -NoProfile -File ./tools/build-server.ps1 -GameRoot <game release folder> -AirRoot <AIR client folder> -Runtime win-x64 -PublicHost <your server's address>
```

- `-Runtime linux-x64` builds for a Linux server.
- `-RemotePort` (default 8391) and `-FirstGamePort` (default 5219) change the ports; `-ServerName` (default `Amberline`) is the
  name of the players' desktop icon.
- The output folder holds `server/`, `platform/` (with the settings, the server's certificate and the player tool), `Play.cmd`
  and `README-DEPLOY.txt`, which explains every file.
- Keep `platform/platform-data/remote/host.pfx` private: it is the server's TLS key, and every `Play.cmd` checks it. A rebuild
  into the same output folder keeps it; for a build into another folder, pass `-Certificate <that host.pfx>`.
- The build never copies your game or AIR client into the package; its last step checks that.

### The management page

The management page `/manage` listens only on the server itself (`127.0.0.1:8390`). Never open its port. Reach it through an
SSH tunnel from your own PC:

```sh
ssh -L 8390:127.0.0.1:8390 <user>@<your server's address>
```

then open `http://127.0.0.1:8390/manage` in a browser on your PC and create each friend's login there. Keep TCP 8390, 2099 and
5222 closed to the internet: only TCP 8391 and UDP 5219 to 5222 are for your friends.

### Updating a rented server

Stop the platform and copy the new package over the old one, except `platform/platform-data/platform.json`. Players get the new
player tool the next time they start the game.
