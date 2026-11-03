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
    /// The mode string of both tutorials at the Platform: AIR <c>GameMode.TUTORIAL</c> = "TUTORIAL". The client compares the
    /// SynchVersion mode case-blind with "Tutorial" and then loads <c>Data\LoadingScreen\TutorialModeSettings.ini</c>, Battle Training's
    /// screen; the Basic Tutorial's client is told CLASSIC (<see cref="ClientModeOf"/>, batch 674).
    /// </summary>
    public const string TutorialMode = "TUTORIAL";

    /// <summary>
    /// Batch 669: whether a map is played in a mode. Each map has its own mode (<see cref="ModeOf"/>), and Battle Training is Summoner's Rift
    /// in TUTORIAL: the AIR bundle calls it "Battle Training - Summoner's Rift" (chooseGameType_tutorialGameTypes_advanced), and the client's
    /// TUTORIAL loading screen, <c>Data\LoadingScreen\TutorialModeSettings.ini</c>, is Battle Training's (advanced_tutorial_loadscreen.dds,
    /// game_advanced_tutorial_title; the client).
    /// </summary>
    public static bool IsPlayedIn(int mapId, string mode) => mode == ModeOf(mapId) || (mapId == Normal && mode == TutorialMode);

    /// <summary>
    /// Batch 674: the mode string the game client is sent (SynchVersion; the hero-stats file the client reads, <c>DATA\Globals\&lt;mode&gt;_Stats</c>).
    /// The client loads <c>Data\LoadingScreen\TutorialModeSettings.ini</c> (Battle Training's advanced_tutorial_loadscreen.dds and title) for a
    /// mode equal, case-blind, to "Tutorial", and for any other mode <c>LEVELS\&lt;map&gt;\Mission.ini [LoadScreen] Default</c>, which on Map3 is
    /// "Tutorial": <c>TutorialSettings.ini</c>, basic_tutorial_loadscreen.dds and the TutorialLoadScreen class. So
    /// the Basic Tutorial's own screen shows only in a mode other than TUTORIAL, and the client ships three (CLASSIC, ODIN, TUTORIAL _Stats and
    /// RecItems files; RecItemsTUTORIAL only for Battle Training's ten champions). Reconstruction: the Basic Tutorial's client is told CLASSIC.
    /// The Platform's mode (AIR GameMode.TUTORIAL, the results, recent games and the reward rule) stays TUTORIAL.
    /// </summary>
    public static string ClientModeOf(int mapId, string mode) => mapId == Tutorial && mode == TutorialMode ? ClassicMode : mode;

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
