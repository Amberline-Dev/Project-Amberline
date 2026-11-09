using System.Globalization;

namespace Amberline.Data;

/// <summary>
/// Map-scoped item-store rules recovered from the shipped level data.
/// ItemCatalog remains the raw global item catalog; this layer is applied only
/// at an authoritative map purchase boundary.
/// </summary>
public sealed class MapItemRules
{
    private const string ExplicitInclusionSetting = "it_UseExplicitItemInclusion";
    private const int MaxListEntries = 1024;

    private MapItemRules(bool useExplicitItemInclusion,
        IReadOnlySet<int> includedItemIds, IReadOnlySet<int> unpurchasableItemIds)
    {
        UseExplicitItemInclusion = useExplicitItemInclusion;
        IncludedItemIds = includedItemIds;
        UnpurchasableItemIds = unpurchasableItemIds;
    }

    public bool UseExplicitItemInclusion { get; }
    public IReadOnlySet<int> IncludedItemIds { get; }
    public IReadOnlySet<int> UnpurchasableItemIds { get; }

    public bool AllowsPurchase(int itemId) =>
        (!UseExplicitItemInclusion || IncludedItemIds.Contains(itemId)) &&
        !UnpurchasableItemIds.Contains(itemId);

    /// <summary>Loads the patch-126 Summoner's Rift (Map1) item rules.</summary>
    public static MapItemRules LoadMap1(GameDataPaths? paths = null) =>
        Load(paths ?? GameDataPaths.Default, "Map1");

    /// <summary>The item rules of the map the paths name (<see cref="GameDataPaths.MapName"/>).</summary>
    public static MapItemRules Load(GameDataPaths? paths = null)
    {
        var map = paths ?? GameDataPaths.Default;
        return Load(map, map.MapName);
    }

    private static MapItemRules Load(GameDataPaths paths, string mapName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var constantsPath = paths.Under("LEVELS", mapName, "Constants.var");
        var itemRulesPath = paths.Under("LEVELS", mapName, "Items.inibin");
        if (!File.Exists(constantsPath))
            throw new FileNotFoundException("missing map constants", constantsPath);
        if (!File.Exists(itemRulesPath))
            throw new FileNotFoundException("missing map item rules", itemRulesPath);

        var useExplicit = ReadExplicitSetting(constantsPath);
        var data = InibinReader.Load(itemRulesPath);
        return new MapItemRules(
            useExplicit,
            ReadIds(data, "ItemInclusionList"),
            ReadIds(data, "UnpurchasableItemList"));
    }

    private static bool ReadExplicitSetting(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var comment = line.IndexOf(';');
            var valueLine = comment < 0 ? line : line[..comment];
            var separator = valueLine.IndexOf('=');
            if (separator < 0 || !string.Equals(valueLine[..separator].Trim(),
                    ExplicitInclusionSetting, StringComparison.Ordinal))
                continue;

            var value = valueLine[(separator + 1)..].Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var integer))
                return integer != 0;
            if (bool.TryParse(value, out var boolean))
                return boolean;
            throw new InvalidDataException(
                $"invalid {ExplicitInclusionSetting} value '{value}': {path}");
        }

        throw new InvalidDataException(
            $"map constants do not define {ExplicitInclusionSetting}: {path}");
    }

    private static IReadOnlySet<int> ReadIds(InibinData data, string section)
    {
        var ids = new HashSet<int>();
        for (var index = 0; index < MaxListEntries; index++)
        {
            var key = InibinReader.KeyHash($"{section}*Item{index}");
            // The matched client reads Item0, Item1, ... and terminates on
            // the first missing/zero value. Keep that indexed-list contract;
            // scanning past a gap would accept records the client ignores.
            if (!data.TryGetValue(key, out var value) || IsZero(value)) break;
            if (!TryReadId(value, out var itemId))
                throw new InvalidDataException($"invalid {section} item entry {index}");
            ids.Add(itemId);
        }
        return ids;
    }

    private static bool IsZero(InibinValue value)
    {
        if (value.Kind == InibinValueKind.String)
            return value.AsString().Trim() == "0";
        if (value.Kind == InibinValueKind.Boolean)
            return !value.Boolean;
        return value.IsNumeric && value.AsDouble() == 0.0;
    }

    private static bool TryReadId(InibinValue value, out int itemId)
    {
        itemId = 0;
        if (value.Kind == InibinValueKind.String)
            return int.TryParse(value.AsString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out itemId) && itemId > 0;
        if (!value.IsNumeric) return false;
        var number = value.AsDouble();
        if (!double.IsFinite(number) || number < 1.0 || number > int.MaxValue ||
            number != Math.Truncate(number)) return false;
        itemId = (int)number;
        return true;
    }
}
