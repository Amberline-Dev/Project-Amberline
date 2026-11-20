#if !AMBERLINE_HOST_FOLDERS
using Amberline.AirCompanion.Rads;
#endif

// Linked as source into the Platform as well (see RetailClients.cs): the AIR layout values; the companion-only part is under #if.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// The companion's values that belong to its release line: patch 1.0.0.132 and its AIR 1.52 client (LolClient 1.52.12_01_17_13_40), master.
/// The files that read them (AirSourceLayout, FolderMove, GameManagedLayout, Program) are meant to be the same text on the 1.0.0.126 line,
/// which keeps its own copy of this file (batch 795; the repository is to be split into 126/, 132/ and host/). A compiled-in value per line,
/// not a runtime switch.
/// </summary>
internal static class CompanionLine
{
    /// <summary>
    /// The stock AIR 1.52 files a source must hold, at their sizes (<see cref="AirSourceLayout"/>; the friend launcher's
    /// <c>Get-AirSourceProblems</c> in tools/air-client-bootstrap.cmd mirrors them). Sizes of the reference <c>D:\132_AIR</c> (batch 791 report,
    /// section 1); 1.45 had <c>LolClient.swf</c> 6,944,733, <c>css\fonts.swf</c> 180,286, <c>mod_lgn.dat</c> 4,598,750, <c>mod_man.dat</c>
    /// 14,783,133, <c>mod_ser.dat</c> 562,446 and none of the three new modules (mod_champ: the champion detail page, mod_rep: the reporting
    /// module, mod_rt: Ranked Teams; AirModulePaths.as:25-36). <c>LolClient.exe</c>, <c>Adobe AIR.dll</c> and <c>application.xml</c> are
    /// byte-identical to 1.45's.
    /// </summary>
    internal static readonly (string Name, long Size)[] AirFiles =
    [
        ("LolClient.exe", 144384),
        ("LolClient.swf", 7009444),
        ("META-INF\\AIR\\application.xml", 5115),
        ("mimetype", 59),
        ("Adobe AIR\\Versions\\1.0\\Adobe AIR.dll", 10887040),
        ("css\\fonts.swf", 180288),
        ("mod_lgn.dat", 4323221),
        ("mod_man.dat", 14798372),
        ("mod_ser.dat", 564621),
        ("mod_champ.dat", 659799),
        ("mod_rep.dat", 600204),
        ("mod_rt.dat", 823935),
    ];

    /// <summary>The asset folders that must hold something; 1.52 adds <c>championDetail</c> and <c>rankedTeams</c>.</summary>
    internal static readonly string[] AirFolders =
    [
        "assets\\championDetail",
        "assets\\data",
        "assets\\help",
        "assets\\htmlTemplates",
        "assets\\images",
        "assets\\locale",
        "assets\\rankedTeams",
        "assets\\sounds",
        "assets\\storeImages",
        "assets\\swfs",
    ];

    /// <summary>The AIR client's own top-level entries this line adds to 1.45's (<see cref="FolderMove.AirEntries"/>): the three 1.52 modules.</summary>
    internal static readonly string[] AddedAirEntries = ["mod_champ.dat", "mod_rep.dat", "mod_rt.dat"];

#if !AMBERLINE_HOST_FOLDERS
    /// <summary>
    /// The friend's loopback endpoints on this line: the ports of this line's Platform block (patch.json "ports": web 8490, RTMP 2199,
    /// chat 5222; tools/install-132.ps1). A friend's companion listens on them, so the store origin the Platform hands out
    /// (<c>getStoreUrl</c>, http://127.0.0.1:&lt;the Platform's HttpPort&gt;) and the friend's AIR copy (<c>lq_uri</c>, <c>remoting_port</c>,
    /// <c>xmpp_muc_server_port</c>) reach it on the friend's PC. Batch 808. The 1.0.0.126 line keeps its own copy of this file (8390, 2099, 5222).
    /// </summary>
    internal const int PlatformHttpPort = 8490;
    internal const int RtmpPort = 2199;
    // Not the line's own 5322: the 1.52 client's main chat connection takes no port from lol.properties (ChatController passes 0 and
    // ChatServiceImpl.connect falls back to 5222, or 5223 with SSL; read from the 1.52 client's services module), so a chat server on 5322 is never
    // reached ("could not connect to PVP.NET chat", owner, 16 November 2026). Only xmpp_muc_server_port is read, and it follows this value.
    internal const int ChatPort = 5222;
    internal const string LocalPlatform = "http://127.0.0.1:8490";

    /// <summary>Names this line's RADS root for the whole process; a stage's <c>radsRoot</c> sets it (<see cref="Program.RunDesktopAsync"/>).</summary>
    internal const string RadsRootVariable = "AMBERLINE_RADS_ROOT";

    /// <summary>
    /// The RADS root of this line's own files (<see cref="GameManagedLayout.ResolveRoot"/>, batch 776; the managed game layout, which this line
    /// keeps off): <see cref="RadsRootVariable"/> (<paramref name="configured"/> is the test seam), else <see cref="RadsLayout.Line132Root"/>.
    /// NOT RETAIL: rads.dll reads <c>HKLM\Software\Riot Games\RADS\LocalRootFolder</c> (32-bit view), else c:\rads, which the 1.0.0.126 line's
    /// managed layout uses, so this one reads neither. Null for a value that is not a full path, or one at, inside or above c:\rads: then
    /// nothing is built. The classic start is the exception (batch 814): the 2011 launcher reads only that registry root, so it uses
    /// <see cref="RadsRegistry.Root"/> and takes the launcher layout over from the other line there.
    /// </summary>
    internal static string? RadsRoot(Func<string?>? configured = null)
    {
        var value = (configured ?? (() => Environment.GetEnvironmentVariable(RadsRootVariable)))();
        if (string.IsNullOrWhiteSpace(value)) return RadsLayout.Line132Root;
        return Path.IsPathFullyQualified(value) && !RadsLayout.TouchesLine126Root(value) ? value : null;
    }

    /// <summary>
    /// The start's managed-layout step (<see cref="Program.PrepareGameFolder"/>). Batch 795, 1.0.0.132: none. rads.dll, in 126 and 132 alike,
    /// reads a layout only under <c>HKLM\...\RADS\LocalRootFolder</c> or c:\rads, both the 1.0.0.126 line's, so a layout under this line's root
    /// (C:\rads132) is never read and the game runs loose from its own folder ("The 1.0.0.132 install"). A loose client
    /// needs 8.3 short names, so their absence is the one warning. (The 126 line builds the layout here: <see cref="GameManagedLayout.Start"/>.)
    /// </summary>
    internal static void PrepareLayout(string gameDirectory, Action<string> write)
    {
        write(ManagedLayoutOff);
        if (GameFolderReport.ShortNames(gameDirectory) == "off") write(NoShortNamesWarning);
    }

    /// <summary>
    /// NOT RETAIL (batch 889): the folder the game starts in (<see cref="StockClientLauncher.PlanStart"/>), spelt so that rads.dll reads no layout.
    /// Retail ran this game managed from its own RADS root. rads.dll (1.0.0.132, RuntimeEnvironment 0x100782a0) takes its mode from the current
    /// directory with a case-sensitive find: "projects/" loads <c>&lt;root&gt;\projects\&lt;name&gt;\releases\&lt;version&gt;\releasemanifest</c>
    /// under the registry root or c:\rads, "solutions/" the solution manifest. A player install keeps this game in
    /// <c>game\projects\lol_game_client\releases\0.0.0.51</c> (<see cref="FolderMove.GameRelease"/>), the release the 1.0.0.126 line's managed
    /// layout fills in c:\rads, so the 1.0.0.132 game read 126's archives (white HUD bars, img:// names, no colourblind option, a Map 6 crash).
    /// Those two words are given in capitals: the same folder to Windows, which keeps the spelling a start gives (nothing in the game or rads.dll
    /// sets the directory again), so rads.dll finds neither and the game runs loose from its own files, as this line intends (batch 795).
    /// </summary>
    internal static string GameWorkingDirectory(string folder) =>
        folder.Replace(@"projects\", @"PROJECTS\", StringComparison.Ordinal).Replace(@"solutions\", @"SOLUTIONS\", StringComparison.Ordinal);

    internal const string ManagedLayoutOff = "managed-layout: off (1.0.0.132: the game runs loose from its own folder; rads.dll reads no layout under this line's RADS root)";

    /// <summary>
    /// NOT RETAIL (batch 923; owner, 20 November 2026: "enforce the user to install the game on the C drive"; both lines since batch 926): a new player
    /// install is made on the Windows system drive, and its game files must have 8.3 short names. Retail's patcher put the game wherever the player
    /// chose, and ran it managed. This line's game runs loose (<see cref="PrepareLayout"/>), and its spell index then comes from
    /// <c>FindFirstFileA("Data\Spells\*.ini")</c>, which matches the <c>.inibin</c> files only through their short names (1.0.0.132 the client
    /// to the client); Windows gives them by default only on the system drive. So setup refuses other drives and copies a game found elsewhere
    /// (or whose files have none) onto it (<see cref="PlayerInstall.MakePlan"/>). The 1.0.0.126 line makes its new installs the same way but runs its game
    /// from the managed layout on any drive.
    /// </summary>
    internal static readonly bool SystemDriveInstall = true;

    /// <summary>
    /// NOT RETAIL (batch 926): on this line the shortcut's start offers to move an install's game that has no 8.3 short names
    /// (<see cref="PlayerMode.StartAsync"/>). The 1.0.0.126 line keeps it off: its managed layout runs its game from any drive.
    /// </summary>
    internal static readonly bool ShortNameStartRepair = true;

    internal const string NoShortNamesWarning = "game.folder: WARNING this volume has no 8.3 short names: the loose 1.0.0.132 client builds no spell index without them and stops at hero creation (\"Avatar spell not found!\"). Keep the game on a volume with short names (C: has them).";
#endif
}
