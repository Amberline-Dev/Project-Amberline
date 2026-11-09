using System.Globalization;

namespace Amberline.Data;

public sealed record ItemModifier(string Name, double Value);

public sealed record UnresolvedItemValue(uint Key, InibinValue Value);

/// <summary>Immutable item metadata recovered from one shipped Items/*.inibin.</summary>
public sealed record ItemData(
    int ItemId,
    string DisplayName,
    string SpellName,
    int MaxStack,
    int Price,
    bool Consumed,
    bool UsableInStore,
    int ItemClass,
    int ItemType,
    bool InStore,
    bool Purchasable,
    bool CanBeSold,
    bool CanBeDropped,
    bool DropsOnDeath,
    bool DisappearsOnDeath,
    bool UseWhenAcquired,
    bool IsRecipe,
    int ComboItem,
    int SpellLevel,
    int SpellCharges,
    double SellBack,
    IReadOnlyList<int> RecipeItems,
    IReadOnlyList<ItemModifier> Modifiers,
    IReadOnlyList<UnresolvedItemValue> Unresolved,
    InibinData Raw)
{
    /// <summary>
    /// Whether the shipped record carries an <c>InStore</c> key at all. The loader reads an absent key as the client default (<see cref="InStore"/>
    /// true), which is true of 471 records no shop ever lists (the rune records, the 42xx placeholders, 10001/10002).
    /// Reconstruction: server-side guard; the retail server's shop validation is unknown, so it fails closed on records the shop never lists
    /// (<c>Inventory.Buy</c> and <c>Inventory.Sell</c> require the shipped flag).
    /// </summary>
    public bool HasShippedStoreFlag => Raw.TryGetValue(InibinReader.KeyHash("Data*InStore"), out _);

    public double Modifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var modifier in Modifiers)
            if (modifier.Name == name) return modifier.Value;
        return 0.0;
    }
}

/// <summary>Root-scoped, lazy item catalog. Missing or malformed item files return null.</summary>
public sealed class ItemCatalog
{
    private static readonly string[] MetaNames =
    {
        "DisplayName", "SpellName", "MaxStack", "Price", "Consumed",
        "UsableInStore", "ItemClass", "ItemType", "InStore",
        "IsPurchasable", "Purchasable", "CanBeSold", "CanBeDropped",
        "DropsOnDeath", "DisappersOnDeath", "UseWhenAcquired", "IsRecipe",
        "ComboItem", "SpellLevel", "SpellCharges", "SellBackModifier",
        "RecipeItem1", "RecipeItem2", "RecipeItem3", "RecipeItem4",
        "RecipeItem5", "RecipeItem6"
    };

    private static readonly string[] ModifierNames =
    {
        "FlatCooldownMod", "PercentCooldownMod",
        "FlatHPPoolMod", "PercentHPPoolMod",
        "FlatHPRegenMod", "PercentHPRegenMod", "PercentHardnessMod",
        "FlatMPPoolMod", "PercentMPPoolMod",
        "FlatMPRegenMod", "PercentMPRegenMod",
        // The r* per-level aliases are used by shipped runes and by a few
        // legacy item records. Keep them separate from the flat MP fields so
        // HeroStatCalculator can apply the level multiplier once.
        "rFlatMPModPerLevel", "rFlatMPRegenModPerLevel",
        "FlatEnergyRegenMod", "FlatEnergyPoolMod",
        "rFlatEnergyRegenModPerLevel", "rFlatEnergyModPerLevel",
        "FlatMovementSpeedMod", "PercentMovementSpeedMod",
        "PercentMultiplicativeMovementSpeedMod",
        "FlatArmorMod", "PercentArmorMod",
        "FlatArmorPenetrationMod", "rFlatArmorPenetrationMod", "PercentArmorPenetrationMod",
        "FlatMagicPenetrationMod", "PercentMagicPenetrationMod",
        "FlatSpellBlockMod", "PercentSpellBlockMod",
        "FlatDodgeMod", "rFlatDodgeMod", "FlatCritChanceMod", "FlatMissChanceMod",
        "FlatCritDamageMod", "PercentCritDamageMod",
        "FlatPhysicalDamageMod", "PercentPhysicalDamageMod",
        "FlatMagicDamageMod", "PercentMagicDamageMod",
        "FlatPhysicalReduction", "PercentPhysicalReduction",
        "FlatMagicReduction", "PercentMagicReduction", "PercentEXPBonus",
        "FlatGoldPer10Mod",
        "FlatAttackRangeMod", "PercentAttackRangeMod",
        "FlatCastRangeMod", "PercentCastRangeMod",
        "PercentAttackSpeedMod", "PercentMultiplicativeAttackSpeedMod",
        "PercentHealingAmountMod", "PercentLifeStealMod",
        "PercentSpellVampMod", "PercentSpellEffectivenessMod",
        "FlatBubbleRadius", "PercentBubbleRadiusMod",
        "rFlatHPModPerLevel", "rFlatArmorModPerLevel",
        "rFlatSpellBlockModPerLevel", "rFlatHPRegenModPerLevel",
        "rFlatPhysicalDamageModPerLevel", "rFlatMagicDamageModPerLevel",
        "rFlatMovementSpeedModPerLevel", "rPercentMovementSpeedModPerLevel",
        "rPercentAttackSpeedModPerLevel", "rFlatCritChanceModPerLevel",
        "rFlatCritDamageModPerLevel", "rFlatDodgeModPerLevel",
        "rFlatArmorPenetrationModPerLevel",
        "rPercentArmorPenetrationModPerLevel",
        "rPercentCooldownMod", "rPercentCooldownModPerLevel", "rFlatTimeDeadMod",
        "rFlatTimeDeadModPerLevel", "rPercentTimeDeadMod",
        "rPercentTimeDeadModPerLevel", "rFlatGoldPer10Mod",
        "rFlatMagicPenetrationMod", "rFlatMagicPenetrationModPerLevel",
        "rPercentMagicPenetrationMod", "rPercentMagicPenetrationModPerLevel"
    };

    private readonly Dictionary<int, ItemData?> _cache = new();
    private readonly HashSet<uint> _knownHashes;

    public GameDataPaths Paths { get; }

    public ItemCatalog(GameDataPaths? paths = null)
    {
        Paths = paths ?? GameDataPaths.Default;
        _knownHashes = new HashSet<uint>(MetaNames.Concat(ModifierNames)
            .Select(name => InibinReader.KeyHash("Data*" + name)));
    }

    public ItemData? Get(int itemId)
    {
        if (itemId <= 0) return null;
        if (_cache.TryGetValue(itemId, out var cached)) return cached;
        var loaded = Load(itemId);
        _cache[itemId] = loaded;
        return loaded;
    }

    private ItemData? Load(int itemId)
    {
        var path = Paths.Data($"Items/{itemId}.inibin");
        if (!File.Exists(path)) return null;
        InibinData data;
        try { data = InibinReader.Load(path); }
        catch (InibinException) { return null; }

        var recipes = new List<int>();
        for (var index = 1; index <= 6; index++)
        {
            var recipeId = Integer(Value(data, $"RecipeItem{index}"));
            if (recipeId > 0) recipes.Add(recipeId);
        }

        var modifiers = new List<ItemModifier>();
        foreach (var name in ModifierNames)
        {
            var value = Number(Value(data, name));
            if (value != 0.0) modifiers.Add(new ItemModifier(name, value));
        }

        var unresolved = data.Keys.Where(key => !_knownHashes.Contains(key)).OrderBy(key => key)
            .Select(key => new UnresolvedItemValue(key, data[key])).ToArray();
        var purchasable = Boolean(Value(data, "IsPurchasable") ?? Value(data, "Purchasable"), true);
        // Defaults of an absent key are the stock loader's (the client's item-array add): MaxStack -1, Price 100, CanBeSold, CanBeDropped and InStore 1, SpellLevel 1, everything else 0. 471 of the 647 shipped
        // records (the rune records 5xxx, 8xxx, the 42xx placeholders, 10001/10002) carry none of InStore, Price, MaxStack, CanBeSold and
        // CanBeDropped (MaxStack is also absent on the shop items 3134, 3136 and 3155) and take the defaults; no record carries SpellLevel, so
        // every item spell runs at level 1.
        return new ItemData(
            itemId,
            Text(Value(data, "DisplayName"), string.Empty),
            Text(Value(data, "SpellName"), string.Empty),
            Integer(Value(data, "MaxStack"), -1), Integer(Value(data, "Price"), 100),
            Boolean(Value(data, "Consumed")), Boolean(Value(data, "UsableInStore")),
            Integer(Value(data, "ItemClass")), Integer(Value(data, "ItemType")),
            Boolean(Value(data, "InStore"), true), purchasable,
            Boolean(Value(data, "CanBeSold"), true), Boolean(Value(data, "CanBeDropped"), true),
            Boolean(Value(data, "DropsOnDeath")), Boolean(Value(data, "DisappersOnDeath")),
            Boolean(Value(data, "UseWhenAcquired")), Boolean(Value(data, "IsRecipe")),
            Integer(Value(data, "ComboItem")), Integer(Value(data, "SpellLevel"), 1),
            Integer(Value(data, "SpellCharges")), Number(Value(data, "SellBackModifier"), 0.7),
            Array.AsReadOnly(recipes.ToArray()), Array.AsReadOnly(modifiers.ToArray()),
            Array.AsReadOnly(unresolved), data);
    }

    private static InibinValue? Value(InibinData data, string name) =>
        data.TryGetValue(InibinReader.KeyHash("Data*" + name), out var value) ? value : null;

    private static double Number(InibinValue? value, double fallback = 0.0)
    {
        if (value is null) return fallback;
        double result;
        try
        {
            if (value.Value.Kind == InibinValueKind.Boolean)
                result = value.Value.Boolean ? 1.0 : 0.0;
            else if (value.Value.Kind == InibinValueKind.String &&
                     double.TryParse(value.Value.AsString(), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out var parsed))
                result = parsed;
            else
                result = value.Value.AsDouble();
        }
        catch (InvalidCastException) { return fallback; }
        return double.IsFinite(result) ? result : fallback;
    }

    private static int Integer(InibinValue? value, int fallback = 0)
    {
        var number = Number(value, fallback);
        return double.IsFinite(number) && number >= int.MinValue && number <= int.MaxValue
            ? (int)number : fallback;
    }

    private static bool Boolean(InibinValue? value, bool fallback = false)
    {
        if (value is null) return fallback;
        if (value.Value.Kind == InibinValueKind.String)
        {
            var text = value.Value.AsString().Trim().ToLowerInvariant();
            if (text is "false" or "no" or "off" or "") return false;
            if (text is "true" or "yes" or "on") return true;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                return double.IsFinite(numeric) && numeric != 0.0;
            // Python's bool(float(value)) fallback treats a non-empty,
            // non-numeric string as true.  Keep that coercion for malformed
            // metadata rather than silently turning it into false.
            return true;
        }
        if (value.Value.Kind == InibinValueKind.Boolean) return value.Value.Boolean;
        return Number(value, 0.0) != 0.0;
    }

    private static string Text(InibinValue? value, string fallback)
    {
        if (value is null) return fallback;
        return value.Value.Kind == InibinValueKind.String ? value.Value.AsString() : value.Value.ToString();
    }
}
