using System.IO;

namespace Amberline.Data;

/// <summary>
/// One retail 1.0.0.126 bot loadout: summoner spells, the ordered rune list and
/// the mastery ranks stored for a champion and bot difficulty section.
/// </summary>
public sealed record BotLoadoutData(
    string? SummonerSpell1,
    string? SummonerSpell2,
    IReadOnlyList<int> Runes,
    IReadOnlyDictionary<int, int> Masteries);

/// <summary>
/// Reads <c>Characters/&lt;X&gt;/Scripts/&lt;X&gt;Bot{SummonerSpells,Runes,Masteries}.inibin</c>.
/// Key names, recovered from the shipped files by hash match: every section
/// is <c>Bot{Spell|Rune|Mastery}Data</c> plus a difficulty suffix. The
/// summoner-spell section and suffix list are the client's own
/// (<c>PlayerConnectionInfo</c> builds <c>BotSummonerSpellData</c> + eNEWBIE,
/// eINTERMEDIATE, eADVANCED, eUBER or eTUTORIAL). Keys inside a section are
/// <c>SummonerSpell1/2</c>, <c>Rune1..30</c> and <c>MasteryN</c> (mastery id)
/// with <c>MasteryNLevel</c> (rank; stored as a boolean when 1).
/// </summary>
public static class BotLoadoutReader
{
    public static readonly IReadOnlyList<string> Difficulties =
        ["eNEWBIE", "eINTERMEDIATE", "eADVANCED", "eUBER", "eTUTORIAL"];

    private const int MaxRuneSlots = 30;
    private const int MaxMasteryEntries = 64;

    /// <summary>Returns null when the champion ships no bot loadout files.</summary>
    public static BotLoadoutData? Read(GameDataPaths paths, string champion, int difficulty)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(champion);
        if ((uint)difficulty >= (uint)Difficulties.Count)
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        var stem = $"Characters/{champion}/Scripts/{champion}Bot";
        var spellsPath = paths.Data(stem + "SummonerSpells.inibin");
        if (!File.Exists(spellsPath)) return null;
        var suffix = Difficulties[difficulty];

        var spells = InibinReader.Load(spellsPath);
        var first = Text(spells, $"BotSummonerSpellData{suffix}*SummonerSpell1");
        var second = Text(spells, $"BotSummonerSpellData{suffix}*SummonerSpell2");
        if (first is null || second is null) first = second = null;

        var runes = new List<int>();
        var runesPath = paths.Data(stem + "Runes.inibin");
        if (File.Exists(runesPath))
        {
            var data = InibinReader.Load(runesPath);
            for (var slot = 1; slot <= MaxRuneSlots; slot++)
                if (Number(data, $"BotRuneData{suffix}*Rune{slot}") is { } id && id > 0) runes.Add(id);
        }

        var masteries = new Dictionary<int, int>();
        var masteriesPath = paths.Data(stem + "Masteries.inibin");
        if (File.Exists(masteriesPath))
        {
            var data = InibinReader.Load(masteriesPath);
            for (var index = 1; index <= MaxMasteryEntries; index++)
            {
                if (Number(data, $"BotMasteryData{suffix}*Mastery{index}") is not { } id) break;
                if (Number(data, $"BotMasteryData{suffix}*Mastery{index}Level") is { } rank && rank > 0)
                    masteries[id] = rank;
            }
        }
        return new BotLoadoutData(first, second, runes, masteries);
    }

    private static string? Text(InibinData data, string key) =>
        data.TryGetValue(InibinReader.KeyHash(key), out var value) && value.Kind == InibinValueKind.String
            ? value.AsString() : null;

    // Values are stored as u8/u16, as a bit-packed boolean (rank 1) or as a
    // numeric string, which InibinReader coerces to an integer.
    private static int? Number(InibinData data, string key)
    {
        if (!data.TryGetValue(InibinReader.KeyHash(key), out var value)) return null;
        if (value.Kind == InibinValueKind.Boolean) return value.Boolean ? 1 : 0;
        return value.IsNumeric ? checked((int)value.AsDouble()) : null;
    }
}
