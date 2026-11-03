using System.Net;
using System.Text.RegularExpressions;
using Amberline.Data;

namespace Amberline.Platform;

public sealed record StoreAbility(string Slot, string Name, string Description, string? Icon);
public sealed record StoreChampion(string Name, string Title, string Lore, string PlayingTips,
    string OpposingTips, ChampionData Stats, IReadOnlyList<StoreAbility> Abilities);

public sealed partial class PlatformStoreCatalog
{
    private readonly string _assetRoot;
    private readonly Dictionary<int, StoreChampion> _championContent = [];
    private readonly Dictionary<int, RuneData> _runeContent = [];
    private readonly Dictionary<string, string> _artFiles = new(StringComparer.Ordinal);

    public StoreChampion? ChampionContent(int id) => _championContent.GetValueOrDefault(id);
    public RuneData? RuneContent(int id) => _runeContent.GetValueOrDefault(id);
    public string? ArtworkFile(string key) => _artFiles.GetValueOrDefault(key);
    public string? Artwork(string key) => _artFiles.ContainsKey(key) ? "/store/art/" + key : null;

    // The AIR client's ChampionGeneratedData carries the patch-era search tags; ChampionCatalog reads them from the player's AIR files.
    public static IReadOnlyList<string> ChampionSearchTags(int championId) =>
        ChampionCatalog.ById(championId)?.SearchTags ?? Array.Empty<string>();

    private void AddArtwork(string key, string relative)
    {
        var file = CaseInsensitivePaths.Resolve(Path.Combine(_assetRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(file)) _artFiles[key] = file;
    }

    // The shipped fontconfig text of every champion (name, skins, title) for the 84-champion store listing.
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    private string DisplayName(string key, string fallback) => _text.GetValueOrDefault(key, fallback);

    private void LoadChampionContent()
    {
        RegisterChampionArtwork();
        var translations = _text;
        var path = GameData.Default.Paths.Data("Menu/fontconfig_en_US.txt");
        if (!File.Exists(path)) return;
        var pattern = new Regex("^\\s*tr\\s+\"(?<key>(?:\\\\.|[^\"])*)\"\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"\\s*$", RegexOptions.CultureInvariant);
        foreach (var line in File.ReadLines(path))
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value;
            if (!key.StartsWith("game_character_", StringComparison.Ordinal) &&
                !key.StartsWith("game_spell_", StringComparison.Ordinal)) continue;
            var value = match.Groups["value"].Value;
            try { value = Regex.Unescape(value); } catch (ArgumentException) { }
            translations[key] = PlainText(value);
        }
        string Text(string key, string fallback = "") => translations.GetValueOrDefault(key, fallback);
        foreach (var (id, name) in AirGameDtos.ChampionNames)
        {
            ChampionData stats;
            try { stats = GameData.Default.Champion(name); }
            catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException) { continue; }
            var abilities = new List<StoreAbility>
            {
                new("Passive", Text("game_character_passiveName_" + name), Text("game_character_passiveDescription_" + name), null),
            };
            for (var slot = 0; slot < stats.SpellNames.Count; slot++)
            {
                var spell = stats.SpellNames[slot];
                abilities.Add(new("QWER"[slot].ToString(), Text("game_spell_displayname_" + spell, spell),
                    Text("game_spell_description_" + spell), null));
            }
            for (var slot = 0; slot < abilities.Count; slot++)
            {
                var key = $"ability-{id}-{slot}";
                // ChampionGeneratedData.createChampion supplies these exact five image names (passive, Q, W, E, R).
                // They are AIR art identities, not native spell script names.
                AddArtwork(key, $"images/championScreens/abilities/{ChampionCatalog.ById(id)!.AbilityIcons[slot]}.png");
                abilities[slot] = abilities[slot] with { Icon = Artwork(key) };
            }
            _championContent[id] = new(Text("game_character_displayname_" + name, name),
                Text("game_character_description_" + name), Text("game_character_lore_" + name),
                Text("game_character_tips_" + name), Text("game_character_opposing_tips_" + name), stats, abilities);
        }
    }

    // Splash and card art of every champion of the patch (the shipped AIR assets carry all 84) for the full listing.
    private void RegisterChampionArtwork()
    {
        foreach (var (id, name) in AirGameDtos.AllChampionNames)
        {
            AddArtwork("champion-" + id, $"images/champions/{name}_Splash_0.jpg");
            AddArtwork("champion-card-" + id, $"storeImages/content/champion_portraits_cards/{id}_Web_0.jpg");
            // Reconstruction: the sale banner's three parallelogram panels show the shipped 307x557 loading portraits (already centred
            // on the character) instead of per-item focal crops of the 1215x717 splash.
            AddArtwork("champion-loading-" + id, $"images/champions/{name}_0.jpg");
            for (var skinId = 1; skinId <= ChampionCatalog.ById(id)!.SkinCount; skinId++)
            {
                AddArtwork("skin-" + (id * 1000 + skinId), $"images/champions/{name}_Splash_{skinId}.jpg");
                AddArtwork("skin-loading-" + (id * 1000 + skinId), $"images/champions/{name}_{skinId}.jpg");
            }
        }
    }

    private string? RegisterRuneArtwork(RuneData rune, IReadOnlyList<RuneData> runes)
    {
        string Relative(RuneData value) => "images/runes/" + Path.ChangeExtension(Path.GetFileName(value.Icon), ".png");
        var key = "rune-" + rune.Id;
        if (!string.IsNullOrWhiteSpace(rune.Icon)) AddArtwork(key, Relative(rune));
        if (Artwork(key) is null)
        {
            // Twelve shipped Elusion records have no image. Reuse an existing
            // icon of the same color/tier; this is only a presentation fallback.
            foreach (var candidate in runes.Where(value => value.Color == rune.Color && value.Tier == rune.Tier && !string.IsNullOrWhiteSpace(value.Icon)))
            {
                AddArtwork(key, Relative(candidate));
                if (Artwork(key) is not null) break;
            }
        }
        return Artwork(key);
    }

    private static string PlainText(string text)
    {
        var lines = Regex.Replace(text, "<br\\s*/?>|<li>|</p>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return WebUtility.HtmlDecode(Regex.Replace(lines, "<[^>]*>", string.Empty)).Trim();
    }
}
