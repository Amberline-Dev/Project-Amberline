namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the Platform's own block on the 1.0.0.132 line (batch 810): the ports of <c>132/patch.json</c> "ports" (HTTP 8490, public
/// HTTPS 8491, RTMP 2199, chat 5222, game UDP 5319 to 5322) and the patch name the host's pages print. The defaults of
/// <see cref="PlatformSettings"/> and the host code read these. The 1.0.0.126 line keeps its own copy at this path (8390, 8391, 2099,
/// 5222, 5219). The friends' companion has its own block (<c>132/src/Amberline.AirCompanion/CompanionLine.cs</c>), which the Platform
/// does not compile.
/// </summary>
public static class PlatformLine
{
    public const int HttpPort = 8490;
    public const int RemotePort = 8491;
    public const int RtmpPort = 2199;
    public const int XmppPort = 5222;   // the 1.52 client's main chat connection always uses 5222 (see ChatPort in CompanionLine.cs)
    public const int FirstGamePort = 5319;

    /// <summary>The name the host's pages give the friends' AIR client and game (126 says "patch-126").</summary>
    public const string PatchName = "patch-132";

    /// <summary>
    /// Batch 816: what the host's update check (<see cref="HostUpdates"/>) asks GitHub, and the tag prefix of this line's releases. 132
    /// releases are tagged p132-v0.1.x and are never the project's latest release (that is the 126 line's, owner, 16 November 2026), so
    /// the check lists the releases (one page of 100, newest first) and takes the highest p132-v tag that is not a draft.
    /// </summary>
    public const string ReleaseQuery = "https://api.github.com/repos/Amberline-Dev/Project-Amberline/releases?per_page=100";
    public const string ReleaseTagPrefix = "p132-v";

    /// <summary>
    /// NOT RETAIL (batch 879): the start of the host page's cookie names (<see cref="HostSessions.CookieName"/>, <see cref="HostSessions.FormsCookie"/>).
    /// Cookies ignore ports, so with one name on both lines a browser using both lines' pages on 127.0.0.1 kept one line's session and form
    /// token only (batch 875). The 1.0.0.126 line has its own (Amberline126); the old shared Amberline.Host and Amberline.Forms are read by
    /// neither and never deleted, so an older Amberline still running keeps its own.
    /// </summary>
    public const string CookiePrefix = "Amberline132";

    /// <summary>
    /// NOT RETAIL (batch 830, owner 16 November 2026): the 1.0.0.126 line's home install, whose logins this line's setup and Users offer to copy
    /// (<see cref="HostLoginCopy"/>): its <c>FolderMove.HostName</c> and <c>InstallMarker</c> (<c>126/patch.json</c> "home"). The 1.0.0.126
    /// line has no such value; its Program.cs offers no copy. NOT RETAIL (batch 856): its friends' port, 8391 (<c>126/patch.json</c> "ports"
    /// "remote"), where this line's stand-in answers that line's old shortcuts while the host guides them here (<see cref="HostOldShortcuts"/>).
    /// NOT RETAIL (batch 857, owner 17 November 2026: "126 locks itself"): 0.1.26, the first 1.0.0.126 release whose start locks its install;
    /// until it is locked, the login copy and the stand-in refuse it and name that release.
    /// </summary>
    public static readonly EarlierHome CopyLoginsFrom = new("Amberline", "amberline-install.json", "1.0.0.126", RemotePort: 8391, LocksFrom: "0.1.26");

    /// <summary>
    /// NOT RETAIL (batch 923): whether the host's own game (Play on this PC) needs 8.3 short names in <paramref name="gameFolder"/>: always on this line,
    /// whose game runs loose from any folder (the companion's <c>CompanionLine.PrepareLayout</c>), and its loose spell index matches the <c>.inibin</c>
    /// files only through their short names (<c>GameFolderReport.ShortNames</c>). The 1.0.0.126 line needs them only outside the managed layout's shape.
    /// </summary>
    public static bool NeedsShortNames(string gameFolder) => true;

    /// <summary>NOT RETAIL (batch 923): what the host does about a game folder whose files have no short names (the setup's note and the dashboard's card).</summary>
    public const string ShortNamesAdvice = "Copy the game folder to drive C:, then choose the copy under Settings, Your game folders.";
}
