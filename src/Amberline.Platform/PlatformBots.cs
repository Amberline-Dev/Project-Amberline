using Amberline.Core;
using Amberline.Data;
using Amberline.Server;
using Amberline.Simulation.Bots;

namespace Amberline.Platform;

/// <summary>Custom-lobby bot rules shared by the lobby, its DTOs and the match launch.</summary>
public static class PlatformBots
{
    /// <summary>BotParticipant.SKILL_LEVEL_BASIC..HARD in the stock client.</summary>
    public const int MaxSkillLevel = 3;
    private static readonly Lazy<RuneCatalog> Runes = new(() => new RuneCatalog(GameData.Default.Paths), LazyThreadSafetyMode.PublicationOnly);

    // NOT RETAIL: owner decision 22 Oct 2026. Retail 126's add-bot list offers only the 18
    // champions whose game_bot_ name the client ships (fontconfig; ChampionCatalog.RetailBot:
    // the 17 with Bot*.inibin loadouts plus Alistar). Batch 160 made every roster champion a
    // bot the game server runs from its shipped Bot/Task scripts, so the list here offers every
    // champion the server can run as a bot (the playable roster of ChampionCatalog, one source
    // of truth) and a non-retail bot champion carries this marker at its list's code site.
    public static bool IsBotEnabled(int champion) =>
        AirGameDtos.ChampionNames.TryGetValue(champion, out var name) && BotController.SupportedChampions.Contains(name);

    /// <summary>
    /// BotParticipant.updateInternalName: summonerName = champion.skinName + " bot". This is the stock
    /// lobby's own text; the match seat is named by <see cref="MatchName"/>.
    /// </summary>
    public static string DisplayName(int champion) => AirGameDtos.ChampionNames[champion] + " bot";

    /// <summary>
    /// The seat's roster name, sent in RequestRename, CreateHero and the results: the in-game bot name
    /// ("Annie Bot"), see <see cref="GameData.BotName"/>.
    /// </summary>
    public static string MatchName(int champion) => GameData.Default.BotName(AirGameDtos.ChampionNames[champion]);

    /// <summary>BotParticipant.getInternalName: "bot" + "_" + skinName + "_" + teamId.</summary>
    public static string InternalName(int champion, Team team) =>
        "bot_" + AirGameDtos.ChampionNames[champion] + "_" + TeamId(team);

    /// <summary>Team.TEAM_ID_BLUE / TEAM_ID_PURPLE as the String the client stores in BotParticipant.teamId.</summary>
    public static string TeamId(Team team) => team == Team.Order ? "100" : "200";

    // The client localizes these names itself (BotParticipant.as:47-56 takes them from the bot_difficulty_* resources);
    // the wire copy only mirrors them.
    public static string SkillName(int level) => level switch
    { 0 => "Basic", 1 => "Easy", 2 => "Medium", _ => "Hard" };

    /// <summary>
    /// The match seat for one lobby bot. Spells, runes and masteries come from the shipped
    /// <c>&lt;X&gt;Bot{SummonerSpells,Runes,Masteries}.inibin</c> when the champion has them (17 of the
    /// retail bots; every one but Alistar has them). NOT RETAIL: owner decision 22 Oct 2026 — a
    /// non-retail bot champion has no retail loadout to read, so its seat gets the reconstructed
    /// loadout, never an empty one: the platform default summoner-spell pair (Flash and Ignite)
    /// with the rune and mastery pages the retail Basic (eNEWBIE) arm ships for every champion
    /// (none). Retail's higher difficulty arms carry the shipped pages when the files exist.
    /// </summary>
    public static MatchPlayer Seat(int champion, Team team, int skillLevel)
    {
        var name = AirGameDtos.ChampionNames[champion];
        var data = BotLoadoutReader.Read(GameData.Default.Paths, name, skillLevel);
        var spells = data?.SummonerSpell1 is { } first && data.SummonerSpell2 is { } second
            ? SummonerLoadout.ValidatePair(first, second)
            : (First: SummonerLoadout.DefaultFirst, Second: SummonerLoadout.DefaultSecond);
        // Every listed rune, grouped by its catalog colour and not capped at the lobby slot counts: the retail
        // bot lists reach the game as they are (RunePage.CreateBot).
        int[]? OfColor(RuneColor color)
        {
            if (data is null || data.Runes.Count == 0) return null;
            var ids = data.Runes.Where(id => Runes.Value.Get(id)?.Color == color).ToArray();
            return ids.Length == 0 ? null : ids;
        }
        var player = new MatchPlayer(MatchName(champion), name, team, spells.First, spells.Second,
            RedRunes: OfColor(RuneColor.Red), BlueRunes: OfColor(RuneColor.Blue),
            YellowRunes: OfColor(RuneColor.Yellow), BlackRunes: OfColor(RuneColor.Black),
            Masteries: data is null || data.Masteries.Count == 0 ? null : new Dictionary<int, int>(data.Masteries),
            IsBot: true);
        _ = MatchRoster.ValidateLoadout(player);
        return player;
    }
}
