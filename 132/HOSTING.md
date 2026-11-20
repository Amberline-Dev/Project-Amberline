# Hosting an Amberline server

This guide is for patch 1.0.0.132 (January 2012). The 1.0.0.126 line has its own guide, [HOSTING.md](../HOSTING.md).

It covers every step: installing Amberline, the setup page in your browser, the dashboard, adding users and keeping Amberline up
to date. Words in **bold** are what you see on the screen. Each error has a short code such as `(A-NET-02)`. Look it up in
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).

- [What you need](#what-you-need)
- [Two ways to install](#two-ways-to-install)
- [START-HERE.cmd, step by step](#start-herecmd-step-by-step)
- [The release zip](#the-release-zip)
- [The Amberline folder](#the-amberline-folder)
- [The Amberline window](#the-amberline-window)
- [The setup page](#the-setup-page)
- [The dashboard](#the-dashboard)
- [Add a user](#add-a-user)
- [Users](#users)
- [Advanced](#advanced)
- [Settings and Uninstall](#settings-and-uninstall)
- [Updates](#updates)
- [Play on this PC](#play-on-this-pc)
- [Only your home network (LAN party)](#only-your-home-network-lan-party)
- [When your address changes](#when-your-address-changes)
- [Removing Amberline](#removing-amberline)
- [Moving from the 1.0.0.126 line](#moving-from-the-100126-line)
- [A rented server](#a-rented-server)

## What you need

- A PC with Windows 10 or Windows 11, 64-bit.
- Your own copy of the game, patch 1.0.0.132 (January 2012). Its folder contains the game's program, `DATA` and `LEVELS`. In a
  download it may be several folders deep. Amberline looks for it.
- The matching AIR client (version 1.52, 2012). Its folder contains `assets`.
- Enough free disk space for Amberline's copy of the game and the AIR client, plus a few hundred MB for the program.
  `START-HERE.cmd` shows how much before it copies anything.
- For players outside your home, a router that lets them in. Amberline asks your router to open the ports (with UPnP, a router
  feature). If your router does not allow this, the dashboard shows how to open them by hand.

Each player needs their own copy of the same game and AIR client, unless you share yours ([Game files](#game-files)).

## Two ways to install

Both start the same way: double-click **`START-HERE.cmd`**. It asks the same questions in the same order.

- The release zip (`Amberline-132-<version>-win-x64.zip`, in the release tagged `p132-v<version>` on this repository's Releases
  page). The latest release on that page belongs to the 1.0.0.126 line. Nothing is built. The zip contains everything Amberline
  needs, so you install nothing else. The guide skips its steps 4 and 5.
- The code download (**Code**, then **Download ZIP**). The guide builds Amberline on your PC. It finds the tools a build needs
  and installs a missing one after you agree. Use this when there is no release zip, or to try the newest code.

Both install Amberline the same way. A release always counts as newer than a build made with `START-HERE.cmd`, so it replaces that
build when you start it.

## START-HERE.cmd, step by step

Unzip the release zip or the code download and double-click `START-HERE.cmd`. In the code download, use the one in the `132`
folder. In the release zip, `Start Amberline.cmd` opens it too. A window named **Amberline setup** asks one question at a time.
Type each answer, then press Enter. A yes or no question always says **Type Y for yes or N for no.** To stop, close the window.

If you ran it before, it shows **Last time you chose:** and asks **Use these answers again?** If you answer `N`, the folders you
chose last time are still searched first.

### Step 1 of 6: Where will your server run?

Type `1` for **On this PC** (see also [LAN party](#only-your-home-network-lan-party)) or `2` for **On a rented server** (see
[A rented server](#a-rented-server)). The release zip installs Amberline only on the PC it runs on, so `2` changes nothing there.
The guide then tells you to unzip the zip on a Windows server and type `1` there, or to build a Linux server's package from the
code download.

### Step 2 of 6: Your game and AIR client folders

The guide searches this PC for both, for at most 60 seconds. Press `S` to skip. It looks in the usual places first: the folders
you chose last time, the folder around the download, Desktop, Downloads, Documents and the usual game folders. Then it searches
every drive.

It lists copies that cannot be used first (**Cannot be used, <why>: <folder>**). Then it shows **The search found these. Choose
one:** with the usable ones, **Choose another folder in a window** and **Paste a path**. Answer **Type a number**. If nothing
usable is found, it says **None found. Choose it yourself.** Press Enter for a folder window, or paste a path. A folder that
contains the game somewhere inside also works (**Looking inside that folder**). The guide asks for the game folder first, then
the AIR client folder.

### Step 3 of 6: Where Amberline goes

- **Where should Amberline be installed?** Type a full path, or press Enter for `C:\Amberline 132`. The guide refuses some
  folders, with a **Problem:** line: a whole drive, Program Files, a OneDrive folder, a folder inside the download or inside your
  game or AIR client folder, and a folder that already contains other files.
- If this line's Amberline is already installed, the guide skips steps 1 and 2 (**Steps 1 and 2 of 6: not needed.**). It shows
  your version and this download's, and asks **Update this Amberline now?** Updating also repairs it. Your settings, logins and
  users stay, and so do the game and the AIR client in the install. `N` changes nothing. `R` builds a package for a rented server
  instead, as if you had chosen **On a rented server** at step 1. The release zip cannot build that package and says so.
- To install in another folder, uninstall first (`Uninstall Amberline.cmd` in the install folder).
- If your version is not older than this download, the guide changes nothing (**Nothing was changed.**). From the code download
  it then asks **Build a package for a rented server instead?** The release zip ends there. To update, use the newest release zip.
  A release zip of the version you already have starts it (**Amberline ... is already installed. Starting it.**).
- An Amberline of the 1.0.0.126 line is a separate program. The guide never updates or replaces it. Each line has its own install
  folder (`C:\Amberline 132` here, `C:\Amberline` for 1.0.0.126), its own desktop icon (**Amberline 132** and **Amberline**) and
  its own Windows Firewall rules, so both can be installed on one PC. The guide refuses the other line's folder as your install
  folder. Both lines use port 5222 for chat, so only one of them can run at a time. Close the other line's Amberline window
  before you start this one ([A-NET-01](TROUBLESHOOTING.md#a-net-01)).
- The guide lists what it copies, the size and the drive's free space. From an AIR client folder, only the AIR client's own
  files are copied.
- **Delete the originals?** With `Y`, your original game and AIR client folders are deleted after the copies are checked. On the
  same drive they are moved instead. Only the clients' own files are deleted. With `N`, you keep two copies.

### Step 4 of 6: Build tools

The release zip is already built. The guide says **Steps 4 and 5 of 6: not needed.** and continues with step 6.

From the code download, the guide looks for the .NET SDK, PowerShell 7 and Zig. For a missing tool it says **Missing: <tool>
(...)** and asks **Install <tool> now? This accepts its license.**

- `Y` installs it with `winget`. If Windows asks, choose **Yes**.
- `N` shows its download page. Install it, then **Press Enter to continue.**
- If the tool is still not found after its installer asked for a restart, restart Windows, then double-click `START-HERE.cmd`
  again.

### Step 5 of 6: Building Amberline

This takes a few minutes, longer the first time. A line names each part (**Part 1 of 7** to **Part 7 of 7**). **Still building:
N minutes so far** appears once a minute. The step ends with **Done: the build is finished**. The details go to the log.

### Step 6 of 6: Installing and starting Amberline

A new window named **Amberline** copies the program, the game and the AIR client into your install folder. It shows the size, and
the copy takes several minutes. It deletes the originals if you answered yes, puts an **Amberline 132** icon on your desktop and
opens its page in your browser (**Starting Amberline and opening its page in your browser.**). During an update it first says
**Press Enter to continue.** The update ends any game in progress. Then continue with [the setup page](#the-setup-page).

The guide ends with a **Summary**: the install folder, what happens next, the setup page, how to update, and where its log is
(`.artifacts\start-here` in the download). The log writes your PC's and Windows account's names as `<hidden>`. The window then
says **Press Enter to close.**

To update later, choose **Update now** on Amberline's page (see [Updates](#updates)), or unzip the newest release and double-click
its `START-HERE.cmd`. With the code download, download the newest code and double-click `132\START-HERE.cmd` again.

## The release zip

1. Download `Amberline-132-<version>-win-x64.zip` from the release tagged `p132-v<version>` on this repository's Releases page.
   This is not the latest release, and not the 1.0.0.126 line's `Amberline-<version>-win-x64.zip`. Check that its SHA-256 matches
   the one listed there. To see it, open PowerShell in the zip's folder and type
   `Get-FileHash .\Amberline-132-<version>-win-x64.zip`.
2. Unzip the whole zip into a folder. Keep the folders inside it together.
3. Double-click `START-HERE.cmd`. `Start Amberline.cmd` opens it too. If Windows warns you, see
   [Windows warnings](TROUBLESHOOTING.md#windows-warnings).
4. Answer its questions, as in [START-HERE.cmd, step by step](#start-herecmd-step-by-step). It skips steps 4 and 5.
5. A window named **Amberline** copies the program, the game and the AIR client into the install folder, puts an
   **Amberline 132** icon on your desktop and opens the setup page in your web browser.

You can delete the zip and the unzipped folder afterwards. The zip's `READ ME FIRST.txt` says the same.

## The Amberline folder

Amberline keeps everything in the install folder (`C:\Amberline 132` unless you chose another):

| In the folder | What it is |
|---|---|
| `app` | the Amberline program |
| `app.previous` | the version you had before the last update. It comes back by itself if an update does not start. |
| `data` | your settings, your users' logins, the server's certificate and the logs |
| `game` | your copy of the game, when Amberline copied it in |
| `air` | your copy of the AIR client, when Amberline copied it in |
| `READ ME.txt` | what the folder is, and how to remove Amberline |
| `amberline-132-install.json` | a small marker that Amberline uses to find its data folder |

Outside the folder there is only the desktop icon and the small file `%LOCALAPPDATA%\Amberline 132\install.json`. It tells a later
update where the folder is. Keep the folder where it is, because the icon and later updates look for it there. A 1.0.0.126
Amberline on the same PC has its own folder, icon and `%LOCALAPPDATA%\Amberline\install.json`.

## The Amberline window

While Amberline runs, its window says **Amberline is running. Closing this window stops it and any game in progress.**

- You can minimize the window. Keep it open while your players play.
- Start Amberline from the **Amberline 132** icon on your desktop. If it is already running, the icon opens its page again.
- If your browser did not open, start Amberline again from the icon, or copy the link the window shows into your browser
  within 2 minutes.

For your safety, the page opens only through Amberline, and only for the Windows user who started it. Its address works only
on this PC. Nobody else can open it.

## The setup page

The first time, the page takes you through four steps. The top of the page shows the current step. Each step ends with
**Continue**. The folder step and the address step also have **Back**. The login step has no **Back**, because the name and the
folders are already saved. You can change them later in [Settings](#settings-and-uninstall).

A button that takes a while, for example **Play** or the copy of your game files, shows what it is doing, such as **Starting…**.
A line under it says how long it takes. Wait until it is done.

### Step 1 of 4: Name your server

**Welcome to Amberline 132** appears. Type a **Server name**. Your users see it on the desktop icon that starts their game. Use 1
to 40 letters, digits, spaces, dots, `-` or `_`. Start and end with a letter or digit.

### Step 2 of 4: Your game folders

The page shows two rows, **Game folder** and **AIR client folder**. An optional third row, **2011 launcher (optional)**, is for the
original 2011 launcher's folder. See [The 2011 launcher](#the-2011-launcher).

- **Found:** shows the folder Amberline found. **Not found yet** means it found none. Each row has **Choose folder…**, which opens
  a Windows folder window. The window may open behind your browser. The button says so while it waits.
- You can also paste a folder's address into the field and choose **Check**.
- **Use the client zip…** appears in an install folder such as `C:\Amberline 132`. Use it to select the client zip
  (`patch-1.0.0.132-client-bundle.zip`) instead of the two folders. Amberline installs the game and the AIR client from it into
  the install folder, with a progress bar. The zip is not changed. If you do not have a working 2011 launcher yet, the zip's
  launcher goes into a folder next to the install folder (for example `C:\Amberline 132-launcher`), and the launcher row uses it.
  START-HERE also accepts the zip. At the game folder question, paste the zip's path or drag the zip into its window.
- Choose **Continue** when both rows are correct.

Amberline checks each folder. The game must be patch 1.0.0.132 and complete. The AIR client must be the matching 2012 version
(1.52). A wrong folder shows the reason and its code. A note under the game folder, for example about missing voices and fonts,
matters only for [Play on this PC](#play-on-this-pc). Hosting works either way.

If the folders are outside your install folder, the page asks **Copy your game files into C:\Amberline 132?** It shows what is
copied, its size and the free space. It also asks **Delete the original folders you picked?** **No** is selected by default.

- **Copy the game files** starts the copy. The page shows its size and that it takes several minutes, then a progress bar. When it
  is done, the page says **Your game files are in C:\Amberline 132**. If the copy fails, the page says **Nothing was moved, and your
  folders are as they were.** and offers **Try again**. With **Yes, delete the originals**, the folders you picked are deleted after
  the copy is complete.
- **Use them where they are** keeps your folders where they are.

### Step 3 of 4: Your own login

You play from this PC too, so you need a login of your own.

If this PC also has an Amberline for 1.0.0.126, the step first offers **Copy the logins from your Amberline for 1.0.0.126**, with
the number of logins found. It adds **yours among them** when your own login there is one of them. **Copy the logins** copies them.
Then, under **Your login from 1.0.0.126**, type that login's password and choose **Use this login** to make it your own login here.
See [Moving from the 1.0.0.126 line](#moving-from-the-100126-line). Otherwise, make a new login:

- **Username**: 3 to 24 letters (a to z), digits or `_`. You type it when you log in.
- **Summoner name**: what other players see.
- **Password**: at least 10 characters. Amberline never shows it again.
- **Start at**: **Level 30, everything unlocked** or **Level 1, earn everything**.

Choose **Continue**. If something needs to be changed, the page lists every problem, each next to its field.

### Step 4 of 4: Your address

Your players' computers find yours by its address. The page asks **How do your players find this PC?**

- **The address your router reports** appears when your router tells Amberline its internet address. This is the simplest choice,
  and Amberline notices when the address changes.
- **A name for my address** (or **I type my address or a name for it**): type your internet address (four numbers with dots) or a
  name for it. A name from a dynamic DNS service keeps pointing at your address when it changes. Most home addresses change from
  time to time, often when the router restarts. A home-network address, an address your provider shares, or an IPv6 address is
  refused with the reason.
- **Find my address on the internet**: Amberline asks one outside service (`api.ipify.org`) once. That service sees your address.
  Amberline does not contact it at any other time.
- **Only people on my home network (LAN party)**: see [below](#only-your-home-network-lan-party).

The check box **Tell me about new versions (Amberline asks GitHub once a day, so GitHub sees your address).** is selected. Clear it
if you do not want this. You can change it later on the [Updates](#updates) page.

Choose **Continue**. Amberline first asks your router for its address, which takes up to 20 seconds. The button says so. Then the
dashboard opens.

## The dashboard

The menu on the left has **Dashboard**, **Users**, **Settings**, **Advanced** and **Updates**. **Updates** shows a badge when a new
version is out. On a narrow screen, the menu is a row of tabs above the page. The bottom of each page shows Amberline's version.

**Needs your attention** comes first. It lists every problem, with its code and what to do, and is hidden when there is none.
Three tiles show how many users are **Online**, how many **Games running**, and whether **Sign-ups** are open. **Play** opens your
own game client (see [below](#play-on-this-pc)). **Add a user** starts a new login. **Copy sign-up link** appears while sign-ups are
on, and **Copy address** appears once your address is known.

**Games** has a card for each running game. It shows the map and mode, how long the game has run, and each team's players with
their champions. **End game** on a card asks first, then stops that game for everyone in it. Lobbies that are still waiting are
listed in one line under the cards.

**Activity** lists the newest events first: logins and logouts, sign-ups, games that start and end, and the users you add, disable,
enable or delete. It is kept while Amberline runs and starts empty after a restart. It never shows chat, passwords or addresses.

**Server details** is a section below them, open by default. It contains **Status**, **Online now**, **Network** and the router
guide.

Each card under **Needs your attention** has **Hide**, which hides it until its problem changes. See
[Hiding a message](TROUBLESHOOTING.md#hiding-a-message).

**Status** shows what Amberline is doing, for example **Running since 18:02. Players can join.** It also shows your address and
when a player last connected. Until the first status arrives, the page says **Checking…**. **Stop Amberline** and **Restart
Amberline** ask first and show how many games are running. Stopping or restarting ends those games for everyone. **Cancel** keeps
Amberline running. After a restart, the page comes back by itself. After a stop, start Amberline again from its desktop icon.

**Online now** lists who is logged in.

**Network** lists five checks, each with a light. A sixth appears while Amberline guides old 1.0.0.126 shortcuts (see
[Ports, firewall and the guiding line](#ports-firewall-and-the-guiding-line)).

1. **Amberline is listening on this PC**.
2. **Windows Firewall lets players in**.
3. **Your router opened the ports for this PC** (the sign-in and game ports).
4. **Your router has a public internet address** (no second router and no address shared by your provider).
5. **A player reached Amberline from the internet**. Until then, it says **Waiting for the first player to open the invite link.**

An installed Amberline adds one more line below them: **Only your Windows account can change this install**. Amberline locks its
install folder so that other Windows accounts on this PC cannot change its files. The folder's **Security** tab (in its
**Properties**) then lists only you, SYSTEM and Administrators. If the lock fails, the line says why. Examples are a folder that
another Windows account made, or a drive without Windows permissions (FAT32 or exFAT). This line never stops the server and does
not affect players. The first start after an update checks the whole folder, which takes up to a minute.

A line that is not green says what is wrong, and the same problem appears under **Needs your attention**. Players can join when
Windows Firewall lets them in, and either your router opened the ports and has a public address, or a player from the internet
has already reached Amberline. The second case happens, for example, when you opened the ports by hand.

A button appears next to a problem only while that problem needs it:

- **Allow through Windows Firewall**: Windows asks once, for a Microsoft program named **Windows Command Processor**. Choose
  **Yes**. The button says **Waiting…** until you answer. Amberline then adds two rules, **Amberline 132 sign-in** and
  **Amberline 132 games**. While it guides old 1.0.0.126 shortcuts, it adds a third, **Amberline 132 old sign-in**. A 1.0.0.126
  Amberline keeps its own rules.
- **Test again** runs the network checks again, which takes up to a minute. Use it after you change something on your router.
- **Copy the new invite link**, **Make a new invite link**, **Hide** and **Open the support log**: see the codes in
  [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

### Your router

While Amberline runs, it asks your router (with UPnP) to forward the sign-in port, TCP 8491, and the game ports, UDP 5319 to 5322,
to this PC. Each game uses one game port. With more [games at the same time](#settings-and-uninstall), more ports are used, for
example UDP 5319 to 5326 for eight. When Amberline stops, it removes these forwardings. If your router does not allow this, the
dashboard shows **Open the ports on your router by hand**:

1. Open your router's page. Its address and password are often on a sticker on the router.
2. Find "Port forwarding" (or "Virtual server" or "NAT". Some routers call it "Port sharing").
3. Forward TCP 8491 (sign-in) and UDP 5319 to 5322 (games) to this PC.
4. Give this PC a fixed address on the router ("DHCP reservation" or "Always use this address"), so the forwarding keeps
   pointing at it.
5. Choose **Test again**. Or switch on UPnP in the router ("Allow programs to open ports"), then choose **Test again**.

Where the dashboard knows them, it fills in your router's address, this PC's address, your router's model and the game ports.

## Add a user

1. Choose **Add a user** on the dashboard or on the **Users** page.
2. Fill in **Their username** (3 to 24 letters a to z, digits or `_`). You can also type a **Summoner name**. If it is empty, the
   username is used. Choose **Start them at** level 30 or level 1.
3. Leave **Password** empty to let Amberline make one (four common words and two digits), or type one of at least 10 characters.
4. Choose **Create their login**. If something needs to be changed, the page lists every problem, each next to its field. When
   the login is made, the page says **user1 is ready** and shows two things to send:
   - Your server's **invite link**. **Copy invite link** copies it. Paste it in Discord, WhatsApp or an email. The link is the same
     for every user. It opens their Join page, where they download this line's player app (**Amberline 132 Player**) once. The app
     then sets up their game and puts your server's name on their desktop. Their setup belongs to this line. Its settings are in
     `%LOCALAPPDATA%\Amberline 132 Player`, and Windows lists it as **Amberline 132** under installed apps. A user who also plays on
     a 1.0.0.126 server keeps that setup (**Amberline Player**, **Amberline**) separate, in its own install folder.
   - A message with their username and password. **Copy** copies it. Send it privately.

The invite link contains your address in encoded form. Anyone with the link can read it, as with any address you send. The link
also names your Amberline's release, so the Join page offers the player app of that release. A development build, or a build
older than the line's first release with the player app, names that first release instead. Any of these works, because the app
then downloads the player tool from your Amberline.

The password is shown only once. If the user loses it, use **New password** on [Users](#users).

Until the address step is done, Add a user says **Your address first** and links back to the setup page.

While [Sign-ups](#sign-ups) is on, Add a user also shows the sign-up link, with **Copy**. People can make their own login there
instead.

## Users

**Users** lists every login, made by you or through sign-up, with its status:

- **In game**, with how long the match has been running.
- **In lobby**: waiting in a queue, a lobby or champion select.
- **Online**.
- **Last seen**: when the user last logged in or out, for example "less than an hour ago" or "3 days ago".
- **Never seen here**.

A user who made their own login on the sign-up page is marked **Signed up**. A user whose access you turned off is marked
**Disabled**. The title shows the number of users. Your own login is not in the list. It is the one you play with, and you can
set its password under [Advanced](#advanced).

Above the list are **Add a user**, **Copy invite link** (once your address is known) and **Copy sign-up link** (while sign-ups are
on). Below them, the **Invite link** box shows the link, or why there is none yet. Type in **Search users** to filter the list.
**Copy the My account link** copies the address where users can change their own password or delete their login (see
[My account](#my-account)).

While your Amberline for 1.0.0.126 has a login that is not here yet, **Copy the logins** appears above the list (see
[Moving from the 1.0.0.126 line](#moving-from-the-100126-line)). While your Amberline for 1.0.0.126 is on this PC, a section
named **Old 1.0.0.126 shortcuts** also contains **Move my 1.0.0.126 players here** (see
[Move my 1.0.0.126 players here](#move-my-100126-players-here)).

- **New password**: for a user who lost theirs. The old password stops working immediately, and the user is logged out. The new
  one is shown once, in a message to send.
- **Disable** (for an active user): the user can no longer log in and is logged out immediately. Their games stay. The row then
  says **Disabled**.
- **Enable** (for a disabled user): makes a new password, shown once in a message to send. The user can log in again.
- **Delete**: removes the login for good. A page asks **Delete {user}?** and says that this cannot be undone. The username and the
  summoner name become free for others, and past games and results keep the old names. Delete is refused while the user is in a
  queue, a lobby or a running game. The page says why. Wait until the game ends, then try again.

## Advanced

**Advanced** is the server's full management page. Logins are managed on [Users](#users). Its **Online now** table updates every
5 seconds without reloading the page, so you can use its forms at any time. **Pause updates** stops the updates. Its other parts:

- **Send a notice to players**: a line at the top of every logged-in game client, until you clear it or Amberline restarts. To
  clear it, send an empty notice.
- **Give RP**: RP for a login, to buy champions and skins in the store. Nobody pays.
- **Make a user log in again**: the next time their PC connects, they log in again with their username and password.
- **Set a new password**: for any login, including yours. The player is logged out and logs in again with the new password.
  Choose **Save**. The page then says **Saved.**
- **Make a reward code**: a code that gives IP and RP once. Players type it on the store's **Redeem a code** page. The next page
  shows the code once, with **Copy**. **Reward codes** lists your codes. **Cancel code** stops one that is not used yet.
- **Leavers** and **Player reports**: who left matchmade games or dodged, with **Clear**, and the reports players sent.

If a form has a problem, the page shows **There is a problem** at the top, with a link to the field. It keeps what you typed,
except passwords.

## Settings and Uninstall

**Settings** shows what the first run asked, each part with a link to change it. Each form there ends with **Save**. **Cancel**
leaves it without saving. After **Save**, Settings says **Saved.**

- **Server name**: **Change the server name** uses the same rule as the first step. After **Save**, the page offers **Copy the
  invite link**. Your users' desktop icons show the name from their invite link, so send them the new one.
- **Your address**: **Change your address** has the same choices as the address step, for example to switch the LAN party on or
  off. After **Save**, send your users the new invite link. Their old link still uses the old address.
- **Your game folders**: **Change the folders** works like the folder step. Amberline restarts to use new folders, so this is
  refused while a game is running. In an installed Amberline, it offers to copy folders from elsewhere into the install folder.
  After **Save**, Amberline restarts and comes back to this page, which says **Saved.**
- **Games**: **Games at the same time**, 1 to 32 (4 by default). Each game uses about one CPU core, some memory and its own game
  port. The page shows the ports, for example UDP 5319 to 5322. After **Save**, the number applies to the next game that starts.
  Running games are not affected. With a lower number, a new game waits until fewer games run. The router gets the new ports
  immediately. Windows Firewall needs your approval first. With more games, the dashboard says **Windows Firewall's rule must be
  updated for the new game ports.** Choose **Allow through Windows Firewall** there and choose **Yes**. Fewer games need nothing.
  Invite links do not change.
- **Updates** shows whether the daily check is on. **Change the update check** opens the [Updates](#updates) page.
- **Invite link**: **Copy the invite link** opens Users. **Make a new invite link** makes a new certificate and restarts
  Amberline. Every user's old link and desktop icon then stop working, so send everyone the new link. It asks first. **Cancel**
  returns to Settings.
- **Skins and RP**: **Offer skins and RP** is selected by default. Clear it and choose **Save** to turn skins and RP off. Everyone
  then plays the default looks, the store shows no skins and no RP, and nothing can be bought or given with RP. Nothing anyone owns
  is removed. Select the box again to bring everything back.
- **Champions**: a check box for every champion. Clear one and choose **Save** to turn it off. Players cannot pick it, and bots do
  not take it. A champion already picked in a running champion select or game stays. Users see a turned-off champion greyed out
  after they sign in again. **Tutorials keep their own champions**: the tutorial's three fixed champions stay available there even
  when they are turned off. The list marks them. If too few champions are on for a queue's bots, players are told that the queue
  cannot start (see [Queues](TROUBLESHOOTING.md#queues)).
- **Launcher news**: up to five headlines that the 2011 launcher shows when it opens. See [Launcher news](#launcher-news).
- **Sign-ups**: see below.
- **Game files**: see below.
- **Cheat commands**: every login on your server, including yours (marked **(you)**), each with a check box. A selected login can
  use the testing commands in every game it plays here, starting with the next game. Nobody is selected by default, and bots never
  can use them. Select or clear the boxes and choose **Save**. Only you can change this list, on this page.
- **Uninstall Amberline**: see below.

### Sign-ups

**Let people register themselves** is off by default. Select it and choose **Save**. Anyone with the link can then make their own
login. Settings shows the link, `https://<your address>:<the public port>/register`, with **Copy**. Send it to anyone you like. Add
a user shows it too.

- The page asks for a username, a summoner name (if empty, the username is used), a password of at least 10 characters and the
  start level. It lists every problem. When the login is made, it offers **Open the invite** and shows the invite link, the same
  link Add a user gives. It offers them under **Already have a login?** too.
- Their browser warns that the connection is not private, because the browser does not know Amberline's own certificate. They
  choose **Advanced**, then the button to continue (Chrome: **Proceed to ... (unsafe)**, Edge: **Continue to ... (unsafe)**,
  Firefox: **Accept the Risk and Continue**). Another PC or browser shows the warning again. It is the same certificate that the
  invite link checks.
- Each address can make 12 sign-up attempts an hour. Every submit counts, refused ones too. The page accepts 30 new logins an
  hour in total. After that it says **Too many sign-ups. Try again in an hour.**
- New logins appear in [Users](#users), where **Disable** stops one. To close the page, clear the box and choose **Save**. Nothing
  else of Amberline is on that port.

### My account

Users can change their own password and delete their own login at `https://<your address>:<the public port>/account`, the same
address the sign-up page uses. The page is available whether or not sign-ups are open.

- **Change password**: the user types their username, their current password and a new password twice (at least 10 characters).
  They are signed out of the game client and sign in again with the new password. A running game is not affected.
- **Delete my account**: the user types their username and password and selects **I understand this deletes my login and cannot
  be undone**. Their login is deleted for good. Their past games stay, under their name. This is refused while they are in a lobby
  or a game. They can try again after it ends.
- A wrong username or password gets the same message, **That name and password do not match.**
- Your own login can change its password on that page, but it cannot be deleted there.

### The 2011 launcher

The launcher folder has two uses. It switches **Play on this PC** to the original 2011 launcher. The launcher opens first, as in
2011, and shows your news and your server's status. Its **Play** starts the game client and the game. With **Share the game files
with players** on, your players can also download the launcher with the game.

A player's own start follows that player's install, not your folder. It uses the launcher when the player picked one during setup,
added it by opening the invite link again, or downloaded it from you. Otherwise the player's game starts the usual way. Without a
launcher folder, Play on this PC also starts the game the usual way.

- The folder contains the launcher's program and its files, with their `img` and `xml` folders. Amberline accepts only
  version 1.0.1.19 and checks the program's fingerprint. Any other version, or a folder with a missing file, is refused, and the
  row says why. Amberline uses the folder where it is. Nothing is moved or copied. **Stop using this folder** removes it from the
  settings.
- One folder for both patch lines. The launcher keeps its files under `c:\rads` on this PC, and the 1.0.0.126 line's launcher
  uses that folder too. Starting this line's classic start takes the folder over from the other line, and the other line's start
  takes it back. A start is refused while the other line's launcher, AIR client or game runs from it. The reasons are in
  [The 2011 launcher](TROUBLESHOOTING.md#the-2011-launcher).
- The launcher closes with the game. When AIR and the game have closed, the launcher closes too. If it is still listed in Task
  Manager after that, end it there.
- Your players can download the launcher with the game. With **Share the game files with players** on and the folder chosen,
  the launcher's files are shared with the game (see [Game files](#game-files)). A player's setup offers them when the install
  does not have the launcher yet.
- Regions and languages: every region in the launcher leads to your server. The launcher and the game stay in English.
- **Uninstall Amberline** leaves the files Amberline made for the launcher under `c:\rads`. A new install rebuilds what it needs.

### Launcher news

When the 2011 launcher opens, it shows up to five headlines, each with an optional link, next to your server's status. The status
says online while your server answers. Settings' **Launcher news** box holds the headlines:

- Type a headline and, if you like, its link in each row, then choose **Save**. The counter under the box shows the bytes left.
  The launcher shows at most 440 bytes of news.
- Use only plain letters, digits and punctuation, without quotation marks or backslashes. A row with another character, or a link
  with no title, is refused, and the page names that headline. More than five headlines, or more than the counter allows, is
  also refused.
- Until you save one, the launcher shows a welcome headline with your server's name.
- A new or changed headline gets the date you save it. An unchanged one keeps its date.

### Game files

**Share the game files with players** is off by default. Next to it, Settings says: **You share your own copy of the game. Make sure
you are allowed to share it. Amberline never includes these files.** Select it and choose **Save**. A player whose PC has no game
or AIR client can then download yours during setup. Anyone with your invite link can download it.

- Amberline first checks the game files, which takes a few minutes. Settings says so until the check is done. Then it shows what
  it shares: the game folder and the AIR client folder, each with its size. The download uses your internet upload.
- After a restart or an update, Amberline checks only the files that changed, so sharing is ready again in less than a minute.
  **Save** under **Game files** checks every file again.
- While no 2011 launcher folder is chosen, Settings says that the launcher is not shared and where to choose its folder. With the
  folder chosen, its files are shared too.
- Amberline shares exactly the game folder you chose (with its language files next to it, if any), the AIR client's own files and,
  when you chose a 2011 launcher folder, the launcher's own files (see [The 2011 launcher](#the-2011-launcher)). It never shares
  what the game and the AIR client write while they run, such as logs, crash files and the last match's command line. It never
  shares anything else in those folders, or anything of Amberline's own, such as logins, settings or the certificate.
- A copy whose game or AIR client program is not the 1.0.0.132 one, or a folder that contains a link, is not shared. Settings says
  **Your copy cannot be shared** and why. Fix the folder, then choose **Save** again.
- Each player downloads at most four files at a time, and all players together at most sixteen, so a running game keeps its share
  of your connection. A download that stops continues where it stopped.
- To stop sharing, clear the box and choose **Save**. Sharing stops immediately.

On the player's side, the setup window then offers **Download the game from** your server's name, with the size, as its first
button. The other ways are under **Advanced**. One click downloads and installs the game. While your server is still checking the
files after you turn sharing on, the setup window says that it is still preparing its game files and offers **Try again**. The
player's setup checks every file against the list your server sends, and both programs against the 1.0.0.132 ones. It puts the
game on drive C:, in the same folder as a player's own copy. A player who has their own copy can use it instead. If that copy is
on another drive, setup copies it to drive C: first.

### Uninstall Amberline

**Uninstall Amberline** in Settings shows Amberline's folder, its size and what is removed: the program, the desktop icon, the note
of where its folder is, the two Windows Firewall rules and the ports Amberline opened on your router. If you let Amberline through
Windows Firewall, Windows asks once, for **Windows Command Processor**. Choose **Yes**.

Two check boxes, both cleared by default, add more: **Also delete the game and AIR client copies** and **Also delete your logins and
settings**. If you delete the logins and settings, your users' logins and invite links stop working for good. What you keep stays in
the folder, and its `READ ME.txt` says how to use it again: install Amberline into the same folder. Type `uninstall` and choose
**Uninstall Amberline**. **Cancel** leaves the page. While a game is running, the page says so and shows no button.

You can also uninstall without the browser. Close Amberline, then double-click `Uninstall Amberline.cmd` in the install folder. It
asks the same questions in its window. An Amberline that runs from an unzipped folder or a build folder is not installed. Its page
says there is nothing to uninstall. Close its window and delete that folder.

## Updates

The **Updates** page shows your version and the result of the last check. The check box turns the daily check on or off. Choose
**Save**, and the page says **Saved.** **Check now** asks GitHub once, which takes up to 20 seconds. Amberline never downloads or
installs anything by itself. This line looks only at its own releases (tagged `p132-v`), never at the 1.0.0.126 line's.

When a new version is out, the page shows **Amberline 132 1.2.3 is out** (with the new number), its notes and **Update now**:

1. Choose **Update now**. If a game is running, the page first says **Updating ends the game in progress.** Choose **Update now**
   again to continue, or **Cancel**.
2. The page shows the download, for example **Downloading Amberline 1.2.3 (220 MB). This takes a few minutes.**, with a progress
   bar. Keep the page and the Amberline window open.
3. Amberline downloads the new version from its releases page and verifies it. A new Amberline window then opens (look on the
   taskbar). That window shows both versions and says **You chose Update now on Amberline's page, so the update continues.** It
   stops the running Amberline, installs the new one and starts it, and the page opens in your browser. Your settings and users
   stay. If the new version does not start, the version you had comes back by itself.
4. If a check fails, the page gives the reason in one sentence and says **The Amberline you have keeps running.** Nothing is
   installed, and the download is deleted.

The download is saved in the `data\updates` folder of your install. The next update replaces it.

You can also update by hand. Under **Or update by hand**, the page has **Open the download page** and these steps:

1. Download the new zip (`Amberline-132-<version>-win-x64.zip`) from the release tagged `p132-v<version>` on this repository's
   Releases page and unzip it.
2. Double-click its `START-HERE.cmd` (`Start Amberline.cmd` opens it too) and answer `Y` to **Update this Amberline now?** The new
   window shows your version and the zip's. It says that updating ends any game in progress and that closing the window keeps the
   version you have. Then it says **Press Enter to continue.** Your settings and users stay.
3. If the new version does not start, the version you had comes back by itself.

A zip with an older version than yours changes nothing. If you installed with the code download's `START-HERE.cmd`, you can also
download the newest code and double-click `START-HERE.cmd` again. It shows your version and this download's and offers to update.
If yours is a release, a build from the code changes nothing. Your players' tool updates itself the next time they start the game.

## Play on this PC

**Play** opens the game client on this PC, connected to your own server. The button says **Starting…** and the client's window
opens by itself within a minute. Log in with your own login. The first time takes a few minutes, because Amberline makes its own
copy of your AIR client. Your AIR client folder is only read. With a 2011 launcher folder chosen (see
[The 2011 launcher](#the-2011-launcher)), Play opens that launcher first, as in 2011.

Your own game needs short names for its files, which Windows creates by default only on drive C:. A game folder on another drive
stops while loading, and **Needs your attention** says so ([A-FILES-07](TROUBLESHOOTING.md#a-files-07)). Copy the game folder to
drive C: and choose the copy under **Settings**, **Your game folders**. Hosting and your players are not affected. Their setup
always puts the game on their own drive C:.

## Only your home network (LAN party)

If all your players are in your home, on the same router or Wi-Fi, choose **Only people on my home network (LAN party)** at the
address step.

- Amberline fills in this PC's address on your home network (for example `192.168.1.20`). You can also type it yourself.
- Amberline then does not change your router. Only Windows Firewall needs to let your players in.
- Players outside your home cannot join. The dashboard, Add a user and the message you send say so.
- The network checks leave out the router, and a player from your home network counts as having reached Amberline.
- A router problem found at the address step, such as a second router or an address your provider shares, appears as a tip, not
  as a problem. It matters only for players outside your home.
- To switch it on or off later, go to **Settings**, **Change your address**. Then send your users the new invite link.

## When your address changes

Most home internet addresses change from time to time, often when the router restarts.

- If you chose **The address your router reports**, Amberline asks the router again every 5 minutes. When the address changes,
  Amberline uses the new one immediately, and the dashboard says **Your address changed** (code A-NET-09). Choose **Copy the new
  invite link**, which opens Users, and send the link to every user. Their old icons cannot reach you until they open it. Then
  choose **Hide**.
- A name for your address (a dynamic DNS name) avoids this, because the name follows your address.
- To type a new address or name yourself, go to **Settings**, **Change your address**.

## Removing Amberline

The simplest way is **Settings**, **Uninstall Amberline**, or `Uninstall Amberline.cmd` in the install folder (see
[Uninstall Amberline](#uninstall-amberline)). To remove it by hand:

1. Close the Amberline window.
2. Delete the install folder (`C:\Amberline 132` unless you chose another). This also deletes Amberline's copy of the game and the
   AIR client in it.
3. Delete the **Amberline 132** icon on your desktop.
4. Delete the small file `%LOCALAPPDATA%\Amberline 132\install.json`.
5. If you let Amberline through Windows Firewall, delete its rules in Windows Defender Firewall, **Advanced settings**,
   **Inbound Rules**: **Amberline 132 sign-in**, **Amberline 132 games** and, if it guided old 1.0.0.126 shortcuts,
   **Amberline 132 old sign-in**.

A 1.0.0.126 Amberline on the same PC has its own folder, icon, file and rules without "132". Leave them.

The `READ ME.txt` in the install folder lists the same steps, with your folder's exact path.

## Moving from the 1.0.0.126 line

This Amberline is a separate program from an Amberline for 1.0.0.126. Installing it starts fresh. The 1.0.0.126 one, with its
folder, icon, logins and everything else, stays as it is and keeps working.

- **Copy the logins**: at the setup page's [step 3](#step-3-of-4-your-own-login), and on [Users](#users) while a login there is
  not here yet, Amberline offers **Copy the logins from your Amberline for 1.0.0.126: N logins found**. It finds the Amberline that
  your Windows account installed (in `C:\Amberline` or the folder you chose). It only reads its files and never changes them or
  their permissions. That Amberline counts only once it is locked to your Windows account, so that other people who use this PC
  cannot change its files. Update it to 0.1.26 or later and start it once to lock its folder. Until then, and for an install whose
  folders or files are links to another place, the page says what to do and copies nothing.
- What is copied: each login's username, summoner name and password, so your users keep logging in with the password they
  have. Also whether it starts at level 30 with everything unlocked or at level 1, and the **Signed up** and **Disabled** marks.
  Every copied login is a user. Your own login there becomes your own login here once you type its password at the setup page's
  step 3.
- What is not copied: progress. What each user owns (champions, skins, IP, RP, runes and rune pages), masteries, match history
  and ranked stay with the 1.0.0.126 server. Here each copied login starts as a new login. Logins you deleted there, and the
  server's name, address and settings, are not copied either.
- A login whose username or summoner name is already used here is not copied, and the page names it. A login Amberline could not
  read is counted and left out.
- While the 1.0.0.126 Amberline is running, the page says so. Close its window, then open the page again.
- Ports: this line has its own ports, sign-in TCP 8491 and games UDP 5319 to 5322 (1.0.0.126: TCP 8391 and UDP 5219 to
  5222), and its own Windows Firewall rules. If you forwarded the 1.0.0.126 ports on your router by hand, forward these too
  ([Your router](#your-router)). Both use chat port 5222, so only one Amberline can run at a time.
- New users need this server's invite link. The 1.0.0.126 server's link, or its old Play file, is for the 1.0.0.126 server and
  never plays this one. Send new users this server's invite link (**Copy invite link** on [Users](#users)). While guiding is on, a
  user who still has the 1.0.0.126 setup is offered this server instead. See [What users see](#what-users-see).

### Move my 1.0.0.126 players here

Amberline can guide your users' old 1.0.0.126 desktop icons to this server. This is called guiding. Each icon then offers to set
up this server, and the user signs in with the same username and password. While your Amberline for 1.0.0.126 is on this PC,
[Users](#users) has a section named **Old 1.0.0.126 shortcuts**. Its button, **Move my 1.0.0.126 players here**, copies the logins
that are not here yet, then turns guiding on.

1. Keep your 1.0.0.126 Amberline installed, updated to 0.1.26 or later, and closed. After updating, start it once and close it
   again. That start locks its folder to your Windows account, which guiding needs. After that it never needs to run. Leave it in
   its folder (`C:\Amberline` unless you chose another).
2. Keep this Amberline running. Guiding works only while it runs. Once guiding is on, it stays on when you restart Amberline,
   until you choose **Stop guiding old shortcuts**.
3. Choose **Move my 1.0.0.126 players here**. If the copy is refused, guiding stays off and the page says why. A copy is refused,
   for example, while your 1.0.0.126 Amberline is running, or when other people on this PC can change its files, or when they are
   links. If there is nothing to copy, guiding still turns on.

Choosing the button again later copies the logins made on 1.0.0.126 since then. **Stop guiding old shortcuts** (on **Users**, while
guiding is on) turns guiding off immediately. Old shortcuts can then no longer reach this server. Guiding works only on the PC you
host from. A rented server cannot do it, because both Amberlines must be on one PC.

### What users see

- Their old desktop icon, or an old 1.0.0.126 Play file, may first update its player tool. Then it shows the server's name and
  **now runs patch 1.0.0.132 (January 2012).** It asks **Set it up now?** and says **Use the same username and password as
  before. Your 1.0.0.126 setup is kept.**
- **Set up** opens the 1.0.0.132 setup window. They give it the 1.0.0.132 client zip, or use the copy you share (see
  [Game files](#game-files)). Then they sign in with the same username and password.
- If their PC already has the 1.0.0.132 setup, the dialog asks **It is already set up on this PC. Start it?** and offers **Start it**
  instead of **Set up**.
- **Not now** starts nothing.

### Ports, firewall and the guiding line

- Router: while guiding is on, Amberline also asks your router (with UPnP) to forward TCP 8391, the 1.0.0.126 sign-in port, to
  this PC. If you forwarded the 1.0.0.126 ports by hand, that forwarding already covers TCP 8391.
- Windows Firewall: **Allow through Windows Firewall** (on **Users** when the guiding line asks for it, or on the dashboard)
  also adds **Amberline 132 old sign-in**, a rule for TCP 8391. Windows asks once, as before. Without that rule, only the guiding
  line says so. **Windows Firewall lets players in** does not change.
- The guiding line: while guiding is on, the dashboard's **Network** list has a sixth line. It shows **Old 1.0.0.126 shortcuts
  are guided here** with a green light, or the reason it is not working yet. **Users** shows the same line. The reasons, and what
  to do about each, are in [TROUBLESHOOTING.md](TROUBLESHOOTING.md#old-100126-shortcuts-on-the-network-list). The line is checked
  again every 5 minutes and when you choose **Test again**.

## A rented server

A rented server is a computer in a data center that is always on and has its own internet address. Your players then do not
depend on your PC or your router.

### With START-HERE.cmd

Run the code download's `132\START-HERE.cmd` on your own PC and type `2` at step 1. The release zip builds no server package. The
guide asks:

- **Type the address**: a name or a number address with dots, as your hosting company shows it, without `https://` and without
  a port.
- **Type a server name, or press Enter for Amberline**: your users see it on their desktop icon.
- **Which system does your server run?** `1` for Windows, `2` for Linux.

At step 2, choose the game and AIR client folders on your PC. The build checks them. At step 3, choose where the server package
goes (press Enter for `C:\Amberline 132\server-package`). Nothing is copied or deleted on your PC. After the build, step 6 says
**Do these in order:**

1. Copy the whole server package folder to your server (with Remote Desktop for Windows, or with WinSCP or `scp` for Linux).
2. Copy your own game folder and AIR client folder to the server as well.
3. On the server, open `platform\platform-data\platform.json` (on Linux `platform/platform-data/platform.json`) in a text editor.
   Replace every value in `<...>` with the paths on the server: the game folder, the full path of the server program
   (`server\Amberline.Server.exe` on Windows, `server/Amberline.Server` on Linux) and the AIR client folder. On Windows, write
   each `\` in them as `\\`. If your address is a name, also put the server's number address (four numbers with dots) in
   `gameHost`.
4. In your hosting company's firewall, open TCP port 8491 and UDP ports 5319 to 5322.
5. Start the platform and keep it running. On Windows, open the `platform` folder and start `Amberline.Platform.exe`. On Linux,
   run these in the copied folder:

   ```sh
   chmod +x server/Amberline.Server server/exehost platform/Amberline.Platform
   cd platform
   ./Amberline.Platform
   ```

6. Make a login for each user on the management page (see below).
7. Give each user their login and the invite link from the package's `INVITE-LINK.txt`.

File Explorer opens the package folder, and the guide ends with a **Summary** that names it. If you build into the same package
folder again, its certificate is kept, so the invite link you sent keeps working.

### By hand

Install the [developer requirements](../README.md#for-developers) on a PC, then build the package with your server's address:

```powershell
pwsh -NoProfile -File ./host/tools/build-server.ps1 -Patch 132 -GameRoot <game release folder> -AirRoot <AIR client folder> -Runtime win-x64 -PublicHost <your server's address>
```

- `-Runtime linux-x64` builds for a Linux server.
- `-RemotePort` (default 8491) and `-FirstGamePort` (default 5319) change the ports. `-ServerName` (default `Amberline`) is the
  name of the users' desktop icon.
- `-Version <x.y.z>` names the release you built from. The invite link includes it, so the Join page offers that release's
  player app. Without it, or with a version older than the line's first release with the player app, the link names that first
  release.
- The output folder contains `server/`, `platform/` (with the settings, the server's certificate and the player tool),
  `INVITE-LINK.txt` (the link each user needs) and `README-DEPLOY.txt`, which explains every file.
- Keep `platform/platform-data/remote/host.pfx` private. It is the server's TLS key, and every invite link checks it. A rebuild
  into the same output folder keeps it. For a build into another folder, pass `-Certificate <that host.pfx>`.
- The build never copies your game or AIR client into the package. Its last step checks this.

### The management page

The management page `/manage` is available only on the server itself (`127.0.0.1:8490`). Never open its port. Reach it through
an SSH tunnel from your own PC:

```sh
ssh -L 8490:127.0.0.1:8490 <user>@<your server's address>
```

Then open `http://127.0.0.1:8490/manage` in a browser on your PC and create each user's login there. Keep TCP 8490, 2199 and
5222 closed to the internet. Only TCP 8491 and UDP 5319 to 5322 are for your players.

### Updating a rented server

Stop the platform and copy the new package over the old one, except `platform/platform-data/platform.json`. Players get the new
player tool the next time they start the game.
