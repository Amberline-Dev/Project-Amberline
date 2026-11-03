using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Amberline.Data;

public enum RuneColor
{
    Red,
    Blue,
    Yellow,
    Black,
}

public enum RuneTier
{
    One = 1,
    Two = 2,
    Three = 3,
}

/// <summary>One shipped patch-126 rune record.</summary>
public sealed record RuneData(
    int Id,
    RuneColor Color,
    RuneTier Tier,
    string DisplayName,
    string Description,
    string Icon,
    IReadOnlyList<ItemModifier> Modifiers)
{
    public double Modifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (var modifier in Modifiers)
            if (modifier.Name == name) return modifier.Value;
        return 0.0;
    }
}

/// <summary>
/// Immutable metadata catalog for the 374 real patch-126 rune records.
/// Rune IDs are data records, not inventory items and are never accepted by
/// the six-slot inventory path.
/// </summary>
public sealed class RuneCatalog
{
    public const int FirstRuneId = 5001;
    public const int LastRuneId = 5374;
    public const int PlaceholderRuneId = 5000;
    // Event runes (8001-8034) and the Razer promotional runes (10001-10002) ship as ordinary DATA/Items rune records with the
    // same color/tier/stat keys. They are resolvable (page validation, stat aggregation) but not part of All/Count: the store,
    // the combiner and the complete-inventory grant list only the 374 regular runes, and no shipped path grants the others.
    public const int FirstEventRuneId = 8001;
    public const int LastEventRuneId = 8034;
    public const int FirstPromotionalRuneId = 10001;
    public const int LastPromotionalRuneId = 10002;

    // These are the shipped unresolved ItemData fields. Their values and
    // ranges are stable in the installed 1.0.0.126 DATA/Items records. Only the name of DescriptionKey is recovered
    // (SDBM hash of "Data*Description"); the stock client's item-array add does not read the color, icon or tier keys, so
    // no client name list can resolve them (A10 audit, 18 October 2026).
    private const uint ColorKey = 973892934;
    private const uint IconKey = 2414666880;
    private const uint TierKey = 3448275386;
    private const uint DescriptionKey = 3747042364;

    private readonly IReadOnlyDictionary<int, RuneData> _runes;
    private readonly IReadOnlyList<RuneData> _all;
    private readonly IReadOnlyDictionary<int, RuneData> _special =
        new ReadOnlyDictionary<int, RuneData>(new Dictionary<int, RuneData>());

    /// <summary>Metadata supplied by the authenticated host; no client data installation is read.</summary>
    public RuneCatalog(IEnumerable<RuneData> definitions)
    {
        Paths = new GameDataPaths(AppContext.BaseDirectory);
        _all = Array.AsReadOnly(definitions.OrderBy(rune => rune.Id).ToArray());
        _runes = new ReadOnlyDictionary<int, RuneData>(_all.ToDictionary(rune => rune.Id));
    }

    public RuneCatalog(GameDataPaths? paths = null)
        : this(new ItemCatalog(paths), paths)
    {
    }

    public RuneCatalog(ItemCatalog itemCatalog, GameDataPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(itemCatalog);
        var root = paths ?? itemCatalog.Paths;
        Paths = root;
        var translations = LoadTranslations(root.Data("Menu/fontconfig_en_US.txt"));
        var runes = new Dictionary<int, RuneData>();
        for (var id = FirstRuneId; id <= LastRuneId; id++)
        {
            var item = itemCatalog.Get(id);
            if (item is null)
                throw new InvalidDataException($"Shipped rune record {id} is missing.");
            runes.Add(id, ReadRune(item, translations));
        }
        var special = new Dictionary<int, RuneData>();
        foreach (var id in Enumerable.Range(FirstEventRuneId, LastEventRuneId - FirstEventRuneId + 1)
                     .Concat(Enumerable.Range(FirstPromotionalRuneId, LastPromotionalRuneId - FirstPromotionalRuneId + 1)))
            if (itemCatalog.Get(id) is { } record)
                special.Add(id, ReadRune(record, translations));
        _special = new ReadOnlyDictionary<int, RuneData>(special);

        if (runes.Count != LastRuneId - FirstRuneId + 1)
            throw new InvalidDataException($"Expected {LastRuneId - FirstRuneId + 1} rune records, got {runes.Count}.");
        _runes = new ReadOnlyDictionary<int, RuneData>(runes);
        _all = Array.AsReadOnly(runes.Values.OrderBy(rune => rune.Id).ToArray());
    }

    private static RuneData ReadRune(ItemData item, IReadOnlyDictionary<string, string> translations)
    {
        var id = item.ItemId;
        if (!TryText(item, ColorKey, out var colorText) ||
            !Enum.TryParse<RuneColor>(colorText, ignoreCase: true, out var color) ||
            !TryTier(item, out var tier))
            throw new InvalidDataException($"Shipped rune record {id} has invalid color/tier metadata.");

        var displayKey = $"game_item_displayname_{id}";
        var descriptionKey = $"game_item_description_{id}";
        if (!translations.TryGetValue(displayKey, out var displayName) ||
            !translations.TryGetValue(descriptionKey, out var fallbackDescription))
            throw new InvalidDataException($"Shipped rune record {id} is missing localization.");
        var description = TryText(item, DescriptionKey, out var rawDescription)
            ? Translate(translations, rawDescription)
            : fallbackDescription;
        var icon = TryText(item, IconKey, out var rawIcon) ? rawIcon : string.Empty;
        return new RuneData(id, color, tier, displayName, description, icon,
            Array.AsReadOnly(item.Modifiers.ToArray()));
    }

    public GameDataPaths Paths { get; }
    /// <summary>The 374 regular runes: what the store sells, the combiner merges and the complete inventory grants.</summary>
    public IReadOnlyList<RuneData> All => _all;
    public int Count => _runes.Count;
    /// <summary>Event and promotional runes that ship as rune records but are not granted by any shipped path.</summary>
    public IReadOnlyCollection<RuneData> Special => _special.Values.OrderBy(rune => rune.Id).ToArray();
    public bool Contains(int id) => _runes.ContainsKey(id) || _special.ContainsKey(id);
    /// <summary>
    /// A regular rune, one of the <see cref="All"/> records a normal player could buy in October 2011 (5001-5374). The event
    /// (8001-8034) and promotional (10001-10002) records, the 5000 placeholder and any id the shipped data lacks (5378) are
    /// not: the AIR rune catalog (RuneGeneratedData) lists all of them for the client's own hydration, but the store and
    /// the account's inventory only ever offer the regular ones.
    /// </summary>
    public bool IsRegular(int id) => _runes.ContainsKey(id);
    public RuneData? Get(int id) =>
        _runes.TryGetValue(id, out var rune) ? rune : _special.TryGetValue(id, out var special) ? special : null;

    /// <summary>Aggregates rune modifiers without treating rune IDs as items.</summary>
    public IReadOnlyDictionary<string, double> AggregateModifiers(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var rune = Get(id) ?? throw new ArgumentException($"Unknown rune ID {id}.", nameof(ids));
            foreach (var modifier in rune.Modifiers)
                result[modifier.Name] = result.TryGetValue(modifier.Name, out var value)
                    ? value + modifier.Value : modifier.Value;
        }
        return new ReadOnlyDictionary<string, double>(result);
    }

    private static bool TryText(ItemData item, uint key, out string value)
    {
        foreach (var unresolved in item.Unresolved)
        {
            if (unresolved.Key != key || unresolved.Value.Kind != InibinValueKind.String) continue;
            value = unresolved.Value.AsString();
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryTier(ItemData item, out RuneTier tier)
    {
        foreach (var unresolved in item.Unresolved)
        {
            if (unresolved.Key != TierKey ||
                (!unresolved.Value.IsNumeric && unresolved.Value.Kind != InibinValueKind.Boolean)) continue;
            var value = unresolved.Value.Kind == InibinValueKind.Boolean
                ? (unresolved.Value.Boolean ? 1 : 0)
                : (int)unresolved.Value.AsDouble();
            if (Enum.IsDefined(typeof(RuneTier), value))
            {
                tier = (RuneTier)value;
                return true;
            }
        }
        tier = default;
        return false;
    }

    private static IReadOnlyDictionary<string, string> LoadTranslations(string path)
    {
        if (!File.Exists(path)) return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var match = Translation.Match(line);
            if (!match.Success) continue;
            var key = SafeUnescape(match.Groups["key"].Value);
            var value = SafeUnescape(match.Groups["value"].Value);
            result[key] = value;
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static string Translate(IReadOnlyDictionary<string, string> translations, string key) =>
        translations.TryGetValue(key, out var value) ? value : key;

    private static string SafeUnescape(string value)
    {
        try { return Regex.Unescape(value); }
        catch (ArgumentException) { return value; }
    }

    private static readonly Regex Translation = new(
        "^\\s*tr\\s+\"(?<key>(?:\\\\.|[^\"])*)\"\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
