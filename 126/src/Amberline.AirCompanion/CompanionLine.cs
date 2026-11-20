namespace Amberline.AirCompanion;

/// <summary>
/// The companion's friend ports on the 1.0.0.126 line (batch 808): the ports of this line's Platform block, HTTP 8390, RTMP 2099 and
/// chat 5222, so a friend's companion listens where the host's store origin and the friend's AIR copy point. The 1.0.0.132 line has its own
/// copy (132/src/Amberline.AirCompanion/CompanionLine.cs, the 132 block). The host/ code that reads these members compiles against both.
/// </summary>
internal static class CompanionLine
{
    internal const int PlatformHttpPort = 8390;
    internal const int RtmpPort = 2099;
    internal const int ChatPort = 5222;
    internal const string LocalPlatform = "http://127.0.0.1:8390";

    /// <summary>
    /// The folder the game starts in (<see cref="StockClientLauncher.PlanStart"/>), as given: on this line rads.dll is meant to read it, so a game in
    /// <c>projects\lol_game_client\releases\0.0.0.51</c> loads the managed layout <see cref="GameManagedLayout"/> builds for it. The 1.0.0.132 line
    /// hides that path from its rads.dll (batch 889).
    /// </summary>
    internal static string GameWorkingDirectory(string folder) => folder;

    /// <summary>
    /// NOT RETAIL (batch 923, on this line since batch 926; owner, 20 November 2026): a new player install goes on the Windows system drive, its game
    /// copied in and checked (<see cref="PlayerInstall.OnSystemDrive"/>). Retail's patcher put the game wherever the player chose. This line's game is
    /// served by <see cref="GameManagedLayout"/> on any drive, so the rule is not needed for it: it is the owner's one rule for both lines. The install
    /// keeps the game in <c>game\projects\lol_game_client\releases\0.0.0.51</c>, the shape that layout reads. The 1.0.0.132 line has the same value.
    /// </summary>
    internal static readonly bool SystemDriveInstall = true;

    /// <summary>
    /// NOT RETAIL (batch 926): off on this line. The shortcut's start does not offer to move an install's game that has no 8.3 short names
    /// (<see cref="PlayerMode.StartAsync"/>): this line's managed layout runs its game from any drive, so a working install elsewhere is left alone.
    /// The 1.0.0.132 line runs its game loose and turns it on.
    /// </summary>
    internal static readonly bool ShortNameStartRepair = false;
}
