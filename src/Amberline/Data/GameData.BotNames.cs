namespace Amberline.Data;

public sealed partial class GameData
{
    /// <summary>
    /// Whether the stock client names an isBot hero itself. Its CreateHero handler (0x4F, byte 16)
    /// localizes "game_bot_" + the skin name and shows the raw key when fontconfig has no such entry,
    /// so only the retail bot champions (fontconfig ships 18 of them) can carry the flag.
    /// </summary>
    public bool ClientNamesBot(string champion) => Labels.ContainsKey("game_bot_" + champion);

    /// <summary>
    /// The in-game name of a bot: the client's own "game_bot_" text where it ships one, otherwise the
    /// same retail format "&lt;champion display name&gt; Bot". NOT RETAIL for those champions: retail had
    /// no bots for them; the client shows this server-sent name because such a hero is created
    /// without the bot flag.
    /// </summary>
    public string BotName(string champion) =>
        Labels.TryGetValue("game_bot_" + champion, out var name) ? name
        : Labels.GetValueOrDefault("game_character_displayname_" + champion, champion) + " Bot";
}
