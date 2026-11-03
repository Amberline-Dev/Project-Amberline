namespace Amberline.Data;

/// <summary>
/// One champion of patch 1.0.0.126. <see cref="Id"/>, <see cref="Name"/>, <see cref="SkinCount"/>,
/// <see cref="SearchTags"/> and <see cref="AbilityIcons"/> come from the player's AIR client
/// (<see cref="AirChampionData"/>), <see cref="RetailBot"/> from the player's game client fontconfig; <see cref="Role"/>
/// and <see cref="DefaultIp"/> are the server's own.
/// </summary>
/// <param name="Name">The shipped character name: the DATA/Characters folder and the AIR skinName.</param>
/// <param name="SkinCount">Skins in the AIR catalog; the global skin ids are <c>Id * 1000 + 1 .. SkinCount</c>.</param>
/// <param name="AbilityIcons">Passive, Q, W, E, R icon names without the <c>.dds</c> extension.</param>
/// <param name="RetailBot">The client ships <c>game_bot_&lt;Name&gt;</c>: retail 126 had a bot for this champion.</param>
/// <param name="Role">Lane label of the champion roster audit; non-null exactly for <see cref="Playable"/> champions.</param>
/// <param name="DefaultIp">Store IP price for a playable champion; null falls back to the store's unlisted default.</param>
public sealed record ChampionInfo(
    int Id, string Name, int SkinCount,
    IReadOnlyList<string> SearchTags, IReadOnlyList<string> AbilityIcons,
    bool RetailBot, string? Role, int? DefaultIp)
{
    /// <summary>A hand-written champion module is registered, so the champion can be selected and played.</summary>
    public bool Playable => Role is not null;
}

/// <summary>
/// The one list of every champion of patch 1.0.0.126 (84), and the single place that decides which of them
/// the server plays. The AIR champion list, the roster validation, the bot roster, the store, the economy
/// and the tools all read this class; none keeps a champion list of its own.
///
/// Nothing of the client is kept in the source: on first use the rows are read from the player's own files. Id, name, skin
/// count, search tags and ability icons come from the AIR client's <c>ChampionGeneratedData</c> in
/// <c>assets/swfs/AirGeneratedContent.swf</c> (<see cref="AirChampionData"/>, located by <see cref="AirChampionData.ResolveSwf"/>);
/// the retail bot flag is the game client's <c>game_bot_&lt;Name&gt;</c> fontconfig text (<see cref="GameData.ClientNamesBot"/>).
/// <see cref="PlayableNames"/> needs neither. The check <c>spells-champion-catalog-runtime-load-hash</c> pins the loaded rows by hash.
///
/// Curated: <see cref="PlayableRoster"/>. A champion becomes selectable by
/// adding it to <see cref="PlayableRoster"/> after its module is registered in
/// <c>ProductionMatchComposition</c>; the invariant check <c>spells-champion-catalog-playable-equals-modules</c>
/// keeps the two in step. See docs/champions/champion-roster.md, "Adding a champion".
/// </summary>
public static class ChampionCatalog
{
    /// <summary>
    /// The champions the server plays today: one registered module each (ten hand-written, plus Soraka from her scripts). The order is the development
    /// roster order (default seats, mini-launcher seats, AIR dictionary order); the lane label and the
    /// default IP price belong to the same row.
    /// The IP prices are the October 2011 retail prices (the League wiki's champion list of 30 October 2011, Wayback 20111030161753),
    /// the same figures as the store's price list (<c>data/store-prices-2011.json</c> in the Platform; a check compares the two).
    /// </summary>
    private static readonly (string Name, string Role, int DefaultIp)[] PlayableRoster =
    [
        ("Annie", "mid", 450), ("Orianna", "mid", 4800), ("LeeSin", "jungle", 6300),
        ("Nidalee", "top", 3150), ("Yorick", "top", 6300), ("Shaco", "jungle", 3150),
        ("Ezreal", "adc", 6300), ("Vayne", "adc", 6300), ("Janna", "support", 1350),
        ("Blitzcrank", "support", 3150),
        // BuildingBlocks engine (M2, 16 October 2026): the first champion that runs from her shipped scripts.
        ("Soraka", "support", 450),
        // BuildingBlocks engine (M3, 16 October 2026): champions that run from their shipped scripts. The prices are the 2011 IP
        // prices in force when patch V1.0.0.126 shipped (between V1.0.0.125 on 14 Sep and V1.0.0.127 on 19 Oct 2011), from the
        // wiki's Price reduction history: Ryze 1350 -> 450 on
        // 12 Apr 2011 (V1.0.0.115), Taric 3150 -> 1350 on 5 Aug 2011. Kayle and Sivir have no recorded 2011 change; their 450 is
        // inferred from that page's silence and their later 450 tier, as docs/champions/champion-roster.md records.
        ("Taric", "support", 1350),
        ("Kayle", "top", 450),
        ("Ryze", "mid", 450),
        ("Sivir", "adc", 450),
        // Phase 3, new champions from their shipped scripts (19 October 2026): wave 1. The 3150 IP is in store-prices-2011.json.
        ("Kassadin", "mid", 3150),
        // Phase 3, new champions (19 October 2026, wave 1): from their shipped scripts. The IP price is the 2011 figure of the Platform's store-prices-2011.json.
        ("Udyr", "jungle", 3150),
        // Phase 3, new champions (19 October 2026, W33c): wave 2. The 1350 IP is in store-prices-2011.json.
        ("Morgana", "support", 1350),
        // Phase 3, new champions (19 October 2026, wave 2): from his shipped scripts. The IP price is the 2011 figure of the Platform's store-prices-2011.json.
        ("XinZhao", "jungle", 3150),
        // Phase 3, new champions (19 October 2026, wave 3): from their shipped scripts. 1350 IP is the 2011 figure of store-prices-2011.json.
        ("Amumu", "jungle", 1350),
        // Phase 3, new champions from their shipped scripts (19 October 2026): wave 3, one of the 13 retail bots. The 1350 IP is in store-prices-2011.json.
        ("Alistar", "support", 1350),
        // Phase 3, new champions (19 October 2026, wave 2): Brand from his shipped scripts. The 6300 IP is the 2011 figure of store-prices-2011.json.
        ("Brand", "mid", 6300),
        // Phase 3, new champions (19 October 2026, wave 5, W24b): Twisted Fate from his shipped scripts. The IP price is the 2011 figure of the Platform's store-prices-2011.json.
        ("TwistedFate", "mid", 1350),
        // Phase 3, new champions (19 October 2026, wave 8, W35c): from his shipped scripts. The 1350 IP is in store-prices-2011.json.
        ("Tryndamere", "top", 1350),
        // Wave 2: Riven (6300 IP, store-prices-2011.json). A top-lane fighter; her pool is a zero-size shield.
        ("Riven", "top", 6300),
        // Phase 3, new champions (19 October 2026, wave 2, W30b): Zilean runs from his shipped scripts. The 1350 IP is in store-prices-2011.json.
        ("Zilean", "support", 1350),
        // Phase 3, new champions (25 October 2026, batch 214): Garen runs from his shipped scripts. The 1350 IP is in store-prices-2011.json (id 86).
        ("Garen", "top", 1350),
        // Phase 3, new champions (25 October 2026, batch 215, wave 4): Master Yi runs from his shipped scripts. The 450 IP is in store-prices-2011.json;
        // the lane label is the 2011 role (a judgement: no shipped file carries it, see the batch 215 record).
        ("MasterYi", "jungle", 450),
        // Phase 3, new champions (25 October 2026, wave 4, batch 216): Miss Fortune runs from her shipped scripts. The 6300 IP is in store-prices-2011.json (id 21).
        ("MissFortune", "adc", 6300),
        // Phase 3, new champions (25 October 2026, batch 224, wave 4): Shen runs from his shipped scripts. The 3150 IP is in store-prices-2011.json (id 98);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say support, tank, melee).
        ("Shen", "top", 3150),
        // Phase 3, new champions (25 October 2026, batch 237, wave 6): Ashe runs from her shipped scripts, a retail bot. The 450 IP is in store-prices-2011.json (id 22);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say ranged, recommended, carry).
        ("Ashe", "adc", 450),
        // Phase 3, new champions (25 October 2026, batch 238): Cho'Gath runs from his shipped scripts (a retail bot). The 1350 IP is in store-prices-2011.json (id 31); the lane label is the 2011 role (a judgement: no shipped file carries it).
        ("Chogath", "top", 1350),
        // Phase 3, new champions (25 October 2026, batch 239, wave 4): Warwick runs from his shipped scripts. The 1350 IP is in store-prices-2011.json (id 19);
        // the lane label follows the AIR tag "jungler" (no shipped file carries a lane, see the batch 239 record).
        ("Warwick", "jungle", 1350),
        // Phase 3, new champions (25 October 2026, batch 240, wave 4): Nunu runs from his shipped scripts. The 450 IP is in store-prices-2011.json (id 20);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say jungler, recommended, fighter, melee).
        ("Nunu", "jungle", 450),
        // Phase 3, new champions (25 October 2026, wave 5, batch 243): Malphite runs from his shipped scripts. The 1350 IP is in store-prices-2011.json (id 54);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say fighter, melee).
        ("Malphite", "top", 1350),
        // Phase 3, new champions (25 October 2026, batch 245, wave 5): Trundle runs from his shipped scripts. The 4800 IP is in store-prices-2011.json (id 48);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say fighter, melee).
        ("Trundle", "jungle", 4800),
        // Phase 3, new champions (25 October 2026, wave 7, batch 247): Nasus runs from his shipped scripts. The 1350 IP is in store-prices-2011.json (id 75);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say fighter, pusher, melee).
        ("Nasus", "top", 1350),
        // Phase 3, new champions (25 October 2026, batch 249, wave 6): Maokai runs from his shipped scripts. The 6300 IP is store-prices-2011.json
        // id 57; the lane label is a judgement (no shipped file carries the role; the AIR tags say pusher, tank, melee).
        ("Maokai", "top", 6300),
        // Phase 3, new champions (25 October 2026, batch 250, wave 8): Renekton runs from his shipped scripts. The 6300 IP is in store-prices-2011.json (id 58);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say fighter, melee).
        ("Renekton", "top", 6300),
        // Phase 3, new champions (26 October 2026, batch 251, wave 8): Akali runs from her shipped scripts. The 3150 IP is store-prices-2011.json
        // id 84; the lane label is a judgement (no shipped file carries the role; the AIR tags say assassin, stealth, melee).
        ("Akali", "mid", 3150),
        // Phase 3, new champions (25 October 2026, batch 255, wave 8): Corki runs from his shipped scripts. The 3150 IP is in store-prices-2011.json (id 42);
        // the lane label is the 2011 role (a judgement: no shipped file carries it, the AIR tags say ranged, carry).
        ("Corki", "mid", 3150),
        // Phase 3, new champions (25 October 2026, batch 257, wave 8): Dr. Mundo runs from his shipped scripts. The 1350 IP is in store-prices-2011.json (id 36);
        // the lane label is a judgement like Renekton's (no shipped file carries it; the AIR tags say fighter, melee).
        ("DrMundo", "top", 1350),
        // Phase 3, new champions (26 October 2026, batch 260, wave 8): Rumble runs from his shipped scripts. The 4800 IP is in store-prices-2011.json (id 68);
        // the lane label is a judgement (no shipped file carries the role; the AIR tags say pusher, mage, melee).
        ("Rumble", "top", 4800),
        // Phase 3, new champions (26 October 2026, batch 258, waves 7 and 8): Karma runs from her shipped scripts. The 3150 IP is store-prices-2011.json
        // id 43; the lane label is a judgement (no shipped file carries the role; the AIR tags say support, ranged).
        ("Karma", "support", 3150),
        // Phase 3, new champions (25 October 2026, wave 8, batch 261): Vladimir runs from his shipped scripts (the health-cost pool, E09 Tides
        // of Blood heal hook, E25 Blood Gorged; not a retail bot). The 6300 IP is store-prices-2011.json id 8; the lane label is a judgement
        // (no shipped file carries the role; the AIR tags say ranged, mage, fighter).
        ("Vladimir", "mid", 6300),
        // Phase 3, new champions (25 October 2026, batch 262, wave 4): FiddleSticks runs from his shipped scripts. The 1350 IP is in store-prices-2011.json
        // (id 9); the lane label follows the AIR tag "jungler" (a judgement: no shipped file carries a lane).
        ("FiddleSticks", "jungle", 1350),
        // Phase 3, new champions (26 October 2026, batch 266, wave 4): Katarina runs from her shipped scripts. The 3150 IP is store-prices-2011.json
        // id 55; the lane label is a judgement (no shipped file carries the role; the AIR tags say assassin, recommended, melee).
        ("Katarina", "mid", 3150),
        // Phase 3, new champions (26 October 2026, batch 267, wave 4): Swain runs from his shipped scripts. The 6300 IP is store-prices-2011.json id 50;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say ranged, mage).
        ("Swain", "mid", 6300),
        // Phase 3, new champions (26 October 2026, batch 265, wave 4): Gragas runs from his shipped scripts. The 3150 IP is store-prices-2011.json id 79;
        // the lane label is a judgement (no shipped file carries the role; the AIR tags say fighter, melee).
        ("Gragas", "jungle", 3150),
        // Phase 3, new champions (26 October 2026, batch 268, wave 4): Tristana runs from her shipped scripts. The 1350 IP is store-prices-2011.json
        // id 18; the lane label is a judgement (no shipped file carries the role; the AIR tags say ranged, recommended, carry).
        ("Tristana", "adc", 1350),
        // Phase 3, new champions (26 October 2026, batch 272, wave 3): Irelia runs from her shipped scripts. The 6300 IP is store-prices-2011.json id 39;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say assassin, melee).
        ("Irelia", "top", 6300),
        // Phase 3, new champions (26 October 2026, batch 273, wave 3): Jax runs from his shipped scripts. The 1350 IP is store-prices-2011.json id 24;
        // the lane label is a judgement (no shipped file carries the role; the AIR tags say fighter, melee).
        ("Jax", "top", 1350),
        // Phase 3, new champions (26 October 2026, batch 275, wave 3): Poppy runs from her shipped scripts. The 450 IP is store-prices-2011.json id 78;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say assassin, fighter, melee).
        ("Poppy", "top", 450),
        // Phase 3, new champions (26 October 2026, batch 278, wave 3): Rammus runs from his shipped scripts. The 3150 IP is store-prices-2011.json id 33;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say recommended, tank, melee).
        ("Rammus", "jungle", 3150),
        // Phase 3, new champions (26 October 2026, batch 282, wave 3): Sona runs from her shipped scripts. The 3150 IP is store-prices-2011.json id 37;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say support, ranged, recommended).
        ("Sona", "support", 3150),
        // Phase 3, new champions (26 October 2026, batch 280, wave 3): Sion runs from his shipped scripts. The 1350 IP is store-prices-2011.json id 14;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say fighter, melee).
        ("Sion", "top", 1350),
        // Phase 3, new champions (26 October 2026, batch 283, wave 3): Twitch runs from his shipped scripts. The 3150 IP is store-prices-2011.json id 29;
        // the lane label is a judgement like Ezreal's and Vayne's (no shipped file carries a lane; the AIR tags say ranged, stealth, carry).
        ("Twitch", "adc", 3150),
        // Phase 3, new champions (26 October 2026, batch 285, wave 2): Urgot runs from his shipped scripts. The 3150 IP is store-prices-2011.json id 6;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say ranged, fighter).
        ("Urgot", "top", 3150),
        // Phase 3, new champions (26 October 2026, batch 290, wave 2): Kennen runs from his shipped scripts. The 6300 IP is store-prices-2011.json
        // id 85; the lane label is a judgement (no shipped file carries a lane). He is not a retail bot, so the generic bot template applies.
        ("Kennen", "top", 6300),
        // Phase 3, new champions (26 October 2026, batch 289, wave 2): Singed runs from his shipped scripts. The 1350 IP is store-prices-2011.json id 27;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say fighter, melee).
        ("Singed", "top", 1350),
        // Phase 3, new champions (26 October 2026, batch 294, wave 5): Evelynn runs from her shipped scripts. The 1350 IP is store-prices-2011.json
        // id 28; the lane label is a judgement (no shipped file carries a lane). She is not a retail bot, so the generic bot template applies.
        ("Evelynn", "mid", 1350),
        // Phase 3, new champions (26 October 2026, batch 295, wave 5): Pantheon runs from his shipped scripts. The 3150 IP is
        // store-prices-2011.json id 80; the lane label is a judgement (no shipped file carries a lane; the AIR tags say assassin, melee).
        // He is not a retail bot, so the generic bot template applies.
        ("Pantheon", "mid", 3150),
        // the lane label is a judgement (the AIR tags say support, tank, melee); the IP price is store-prices-2011.json's 6300.
        ("Galio", "tank", 6300),
        // Phase 3, new champions (25 October 2026, batch 300, wave 6): Teemo runs from his shipped scripts. The 1350 IP is store-prices-2011.json id 17;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say support, ranged, stealth). He is not a retail bot.
        ("Teemo", "top", 1350),
        // Phase 3, new champions (26 October 2026, batch 304, wave 6): Gangplank runs from his shipped scripts. The 3150 IP is
        // store-prices-2011.json id 41; the lane label is a judgement (the AIR tags say carry, melee). He is not a retail bot.
        ("Gangplank", "top", 3150),
        // Phase 3, new champions (25 October 2026, batch 308, wave 6): Veigar runs from his shipped scripts. The 1350 IP is store-prices-2011.json id 45;
        // the lane label is a judgement (no shipped file carries a lane; the AIR tags say ranged, mage). He is not a retail bot.
        ("Veigar", "mid", 1350),
        // Phase 3, new champions (26 October 2026, batch 312, wave 6): Caitlyn runs from her shipped scripts. The 6300 IP is
        // store-prices-2011.json id 51; the lane label is a judgement (ADC). She is not a retail bot.
        ("Caitlyn", "adc", 6300),
        // Phase 3, new champions (25 October 2026, batch 313, wave 6): Nocturne runs from his shipped scripts. The 6300 IP is store-prices-2011.json id 56;
        // the lane label is a judgement (no shipped file carries a lane). He is not a retail bot (no NocturneBot files).
        ("Nocturne", "jungle", 6300),
        // Phase 3, new champions (25 October 2026, batch 317, wave 6): Cassiopeia runs from her shipped scripts. The 6300 IP is
        // store-prices-2011.json id 69; the lane label is a judgement (no shipped file carries a lane; the AIR tags say ranged, mage). She is not a retail bot.
        ("Cassiopeia", "mid", 6300),
        // Phase 3, new champions (25 October 2026, batch 321, wave 6): Talon runs from his shipped scripts. The 6300 IP is
        // store-prices-2011.json id 91; the lane label is a judgement (mid). He is not a retail bot (no Talon bot files).
        ("Talon", "mid", 6300),
        // Phase 3, new champions (25 October 2026, batch 318, wave 6): Skarner runs from his shipped scripts. The 6300 IP is
        // store-prices-2011.json id 72; the lane label is a judgement (the AIR tags say jungler, fighter, melee). He is not a retail bot.
        ("Skarner", "jungle", 6300),
        // Phase 3, new champions (26 October 2026, batch 319, wave 6): Leona runs from her shipped scripts. The 6300 IP is
        // store-prices-2011.json id 89; the lane label is a judgement (the AIR tags say tank, melee). She is not a retail bot.
        ("Leona", "support", 6300),
        // Phase 3, new champions (25 October 2026, batch 328, wave 7): Karthus runs from his shipped scripts. The 3150 IP is store-prices-2011.json
        // id 30 (the 30 October 2011 capture; 6300 before the 14 October reduction); the lane label is a judgement (mid). He is not a retail bot (no Karthus bot files).
        ("Karthus", "mid", 3150),
        // Phase 3, new champions (25 October 2026, batch 327, wave 7): Olaf runs from his shipped scripts. The 3150 IP is store-prices-2011.json
        // id 2; the lane label is a judgement (no shipped file carries a lane; the AIR tags say fighter, melee). He is not a retail bot (no OlafBot files).
        ("Olaf", "top", 3150),
        // Phase 3, new champions (26 October 2026, batch 330, wave 7): Anivia runs from her shipped scripts. The 3150 IP is store-prices-2011.json
        // id 34; the lane label is a judgement (the AIR tags say ranged, mage). She is not a retail bot.
        ("Anivia", "mid", 3150),
        // Phase 3, new champions (26 October 2026, batch 326, wave 6): Kog'Maw runs from his shipped scripts. The 6300 IP is
        // store-prices-2011.json id 96; the lane label is a judgement (the AIR tags say ranged, carry). He is not a retail bot (no KogMawBot files).
        ("KogMaw", "adc", 6300),
        // Phase 3, new champions (25 October 2026, batch 333, wave 7): Lux runs from her shipped scripts. The 3150 IP is store-prices-2011.json id 99;
        // the lane label follows her AIR tags (support, ranged, mage) as Zilean's does. She is not a retail bot (no game_bot_Lux in the fontconfig).
        ("Lux", "support", 3150),
        // Phase 3, new champions (26 October 2026, batch 334, wave 7): Xerath runs from his shipped scripts. The 6300 IP is store-prices-2011.json
        // id 101; the lane label is a judgement (no shipped file carries a lane; the AIR tags say ranged, mage). He is not a retail bot (no XerathBot files).
        ("Xerath", "mid", 6300),
        // Phase 3, new champions (26 October 2026, batch 332, wave 7): Jarvan IV runs from his shipped scripts. The 4800 IP is store-prices-2011.json
        // id 59; the lane label is a judgement (jungle). He is not a retail bot (no JarvanIV bot files).
        ("JarvanIV", "jungle", 4800),
        // Phase 3, new champions (26 October 2026, batch 337, wave 9): LeBlanc runs from her shipped scripts. The 3150 IP is store-prices-2011.json
        // id 7; the lane label is a judgement (mid, mage). She is not a retail bot.
        ("Leblanc", "mid", 3150),
        // Phase 3, new champions (25 October 2026, batch 338, wave 9): Wukong (MonkeyKing) runs from his shipped scripts. The 6300 IP is in
        // store-prices-2011.json (id 62); the lane label is a judgement (no shipped file carries it, the AIR tags say stealth, fighter, melee).
        // He is not a retail bot (no MonkeyKingBot files), so the generic bot template applies.
        ("MonkeyKing", "top", 6300),
        // the lane label is a judgement (mid); not a retail bot (no Malzahar bot files).
        ("Malzahar", "mid", 6300),
        // Phase 3, new champions (26 October 2026, batch 339, wave 9): Heimerdinger runs from his shipped scripts. The 3150 IP is store-prices-2011.json
        // id 74; the lane label is a judgement (the AIR tags say pusher, ranged, mage). He is not a retail bot (no HeimerdingerBot files).
        ("Heimerdinger", "mid", 3150),
        // Phase 3, new champions (26 October 2026, batch 341, wave 9): Mordekaiser runs from his shipped scripts. The 3150 IP is
        // store-prices-2011.json id 82; the lane label is a judgement (the 2011 top laner; no shipped file carries a lane). He is not a
        // retail bot (no MordekaiserBot files).
        ("Mordekaiser", "top", 3150),
    ];

    private sealed record Loaded(ChampionInfo[] All, ChampionInfo[] Playable,
        Dictionary<int, ChampionInfo> ById, Dictionary<string, ChampionInfo> ByName);

    // PublicationOnly: a failed load (the AIR client's file refused for a moment) is not kept; the next use tries again (batch 657).
    private static readonly Lazy<Loaded> _loaded = new(Load, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>All 84 champions, ordered by id (loaded from the player's AIR client on first use).</summary>
    public static IReadOnlyList<ChampionInfo> All => _loaded.Value.All;

    /// <summary>The playable champions in development roster order.</summary>
    public static IReadOnlyList<ChampionInfo> Playable => _loaded.Value.Playable;

    /// <summary>
    /// The names of <see cref="Playable"/>, in the same order. They are the curated roster itself, so the game server, which reads
    /// only these, never needs the AIR client.
    /// </summary>
    public static IReadOnlyList<string> PlayableNames { get; } = PlayableRoster.Select(seat => seat.Name).ToArray();

    public static ChampionInfo? ById(int id) => _loaded.Value.ById.GetValueOrDefault(id);

    /// <summary>Case-insensitive; returns null for a name that is not a champion of the patch.</summary>
    public static ChampionInfo? ByName(string? name) => name is null ? null : _loaded.Value.ByName.GetValueOrDefault(name);

    private static Loaded Load()
    {
        var seats = PlayableRoster.ToDictionary(seat => seat.Name, StringComparer.Ordinal);
        var data = GameData.Default;
        var all = AirChampionData.Read(AirChampionData.ResolveSwf()).OrderBy(row => row.Id).Select(row =>
        {
            var playable = seats.TryGetValue(row.Name, out var seat);
            return new ChampionInfo(row.Id, row.Name, row.SkinCount, row.Tags, row.Icons,
                data.ClientNamesBot(row.Name), playable ? seat.Role : null, playable ? seat.DefaultIp : null);
        }).ToArray();
        var byName = all.ToDictionary(champion => champion.Name, StringComparer.OrdinalIgnoreCase);
        // A misspelt curated name must fail loudly, not silently drop a champion.
        var unknown = seats.Keys.Where(name => !byName.ContainsKey(name)).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException("Curated champion names missing from the catalog: " + string.Join(", ", unknown));
        return new Loaded(all, PlayableRoster.Select(seat => byName[seat.Name]).ToArray(), all.ToDictionary(champion => champion.Id), byName);
    }
}
