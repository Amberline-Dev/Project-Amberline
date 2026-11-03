using Amberline.Data;

namespace Amberline.Tests;

public static class MapItemRulesChecks
{
    [Check("data.map1-item-rules")]
    public static void Map1RulesMatchShippedIndexedLists()
    {
        var rules = MapItemRules.LoadMap1();

        TestAssert.False(rules.UseExplicitItemInclusion,
            "Map1 leaves explicit item inclusion disabled");
        TestAssert.Equal(21, rules.IncludedItemIds.Count,
            "Map1 indexed inclusion list preserves its unique IDs");
        TestAssert.Equal(17, rules.UnpurchasableItemIds.Count,
            "Map1 indexed unpurchasable list preserves all entries");
        foreach (var itemId in new[] { 2047, 3178, 3180, 3184 })
            TestAssert.False(rules.AllowsPurchase(itemId),
                $"Map1 denies shipped unpurchasable item {itemId}");
        TestAssert.True(rules.AllowsPurchase(3010),
            "Map1 ignores its retained inclusion list while the switch is zero");
        TestAssert.True(rules.AllowsPurchase(3006),
            "Map1 allows an ordinary item absent from the deny list");
    }

    /// <summary>
    /// Batch 550: the Crystal Scar shop is its own shipped files. LEVELS/Map8/Constants.var sets it_UseExplicitItemInclusion 0 and
    /// LEVELS/Map8/Items.inibin holds only an UnpurchasableItemList of 16 ids (no inclusion list), so the shop sells every store record
    /// (InStore key, Purchasable) but those: 110 items, the Dominion ones (3178, 3180, 3181, 3183 to 3187) among them.
    /// </summary>
    [Check("items.map8-shop-is-its-items-inibin")]
    public static void Map8ShopMatchesItsShippedLists()
    {
        var paths = GameDataPaths.Default.ForMap(8);
        var rules = MapItemRules.Load(paths);
        TestAssert.False(rules.UseExplicitItemInclusion, "Map8 leaves explicit item inclusion disabled");
        TestAssert.Equal(0, rules.IncludedItemIds.Count, "Map8 ships no inclusion list");
        TestAssert.Equal("1055,1056,2037,2038,2039,2042,2043,2044,3041,3072,3102,3106,3126,3138,3141,3154",
            string.Join(",", rules.UnpurchasableItemIds.Order()), "Map8 UnpurchasableItemList");
        var catalog = new ItemCatalog(paths);
        var empty = new Amberline.Simulation.InventorySlot?[Amberline.Simulation.Inventory.SlotCount];
        var sold = Directory.EnumerateFiles(paths.Data("Items"), "*.inibin")
            .Select(file => int.TryParse(Path.GetFileNameWithoutExtension(file), out var id) ? id : 0)
            .Where(id => id > 0 && Amberline.Simulation.Inventory.Buy(catalog, empty, 1e9, id, rules).Reason != "unavailable_item")
            .ToHashSet();
        TestAssert.Equal(110, sold.Count, "the map 8 shop sells 110 items");
        foreach (var itemId in new[] { 3178, 3180, 3181, 3183, 3184, 3185, 3186, 3187 })
            TestAssert.True(sold.Contains(itemId), $"map 8 sells Dominion item {itemId}");
        foreach (var itemId in rules.UnpurchasableItemIds)
            TestAssert.False(sold.Contains(itemId), $"map 8 refuses {itemId}");
    }
}
