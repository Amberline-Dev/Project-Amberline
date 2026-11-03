namespace Amberline.Core;

/// <summary>
/// The maps a match can be played on, and the mode string each one is played in (<see cref="ModeOf"/>): the client reads both from
/// the SynchVersion packet, and the Platform and the Host pick them per game.
/// </summary>
public static class GameMaps
{
    /// <summary>Summoner's Rift, LEVELS/Map1: the shipped map every match ran on.</summary>
    public const int Normal = 1;

    /// <summary>
    /// NOT RETAIL: Summoner's Rift (Autumn), LEVELS/Map2 (fontconfig game_map_displayname_2), the Harrowing rift. The Platform offers it
    /// in custom games at all times (PlatformStoreCalendar.OpenMapIds, owner decision 28 October 2026); the normal queue keeps
    /// Summoner's Rift. Reconstruction: retail showed it during its Harrowing event.
    /// </summary>
    public const int Harrowing = 2;

    /// <summary>
    /// The Basic Tutorial, LEVELS/Map3: one centre lane, its own <c>Scripts/TutorialScript.luaobj</c> on top of the LevelScript, played in
    /// <see cref="TutorialMode"/>. Batch 563 (phase 1): the server and the headless tool accept it. Batch 580: the Platform starts it for
    /// <c>createTutorialGame(0)</c> (PlatformGames.CreateTutorial); it is not a custom-game map.
    /// </summary>
    public const int Tutorial = 3;

    /// <summary>
    /// Twisted Treeline, LEVELS/Map4 (shipped in live/projects/lol_game_client/releases/0.0.0.51; its LevelScript has no ODIN strings, so
    /// CLASSIC). Accepted at the server's gates; its structures are read from its shipped scene (<see cref="MapLayout.FromScene"/>, batch 545),
    /// its lanes and waves are not yet (TT-3). AIR offers it in custom games (batch 557) and the 3v3 normal queue 8 (batch 571).
    /// </summary>
    public const int TwistedTreeline = 4;

    /// <summary>
    /// Dominion, The Crystal Scar, LEVELS/Map8 (AIR <c>GameMap.CRYSTAL_SCAR_ID</c>). The stock AIR client creates it with
    /// <c>GameMode.DOMINION</c> (batch 441 research, section 5.1). Custom games only: the queue keeps Summoner's Rift (batch 443).
    /// </summary>
    public const int CrystalScar = 8;

    /// <summary>The mode string of the classic game (Summoner's Rift and the Harrowing rift).</summary>
    public const string ClassicMode = "CLASSIC";

    /// <summary>
    /// The mode string of The Crystal Scar: <c>GameMode.DOMINION</c> = "ODIN" (AIR GameMode.as:10). Reconstruction: how the client uses the
    /// string is not traced yet (batch 441 section 4.2; batch 442 answers it). Keep this one value as the place that changes.
    /// </summary>
    public const string DominionMode = "ODIN";

    /// <summary>
    /// The mode string of the Basic Tutorial: AIR <c>GameMode.TUTORIAL</c> = "TUTORIAL". The client compares the
    /// SynchVersion mode case-blind with "Tutorial" and then loads <c>Data\LoadingScreen\TutorialModeSettings.ini</c>.
    /// </summary>
    public const string TutorialMode = "TUTORIAL";

    /// <summary>The ids <see cref="IsSupported"/> accepts, for the refusal messages of every <c>--map-id</c> gate.</summary>
    public const string SupportedText =
        "1 (Summoner's Rift), 2 (the Harrowing rift), 3 (the Basic Tutorial), 4 (Twisted Treeline) or 8 (The Crystal Scar)";

    /// <summary>Whether a map id names a map the server can load (the Platform offers a subset in custom games).</summary>
    public static bool IsSupported(int mapId) => mapId is Normal or Harrowing or Tutorial or TwistedTreeline or CrystalScar;

    /// <summary>The mode string a map is played in: "ODIN" on The Crystal Scar, "TUTORIAL" on the Basic Tutorial, "CLASSIC" on the other maps.</summary>
    public static string ModeOf(int mapId) => mapId switch
    {
        CrystalScar => DominionMode,
        Tutorial => TutorialMode,
        _ => ClassicMode,
    };

    /// <summary>
    /// Reconstruction: the lane minions of the Harrowing rift wear MeshSkin1 (Blue_Minion_Harrowing.skn, Red_Minion_*_Harrowing.skn; the
    /// Animations.list [skin1] runs). The client formats "MeshSkin%d" with the skin id, and the shipped Map2 names no
    /// skin itself, so the server's minion SkinId carries it.
    /// </summary>
    public const int HarrowingMinionSkinId = 1;
}
