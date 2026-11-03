using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Items;

public static class ItemChecks
{


    [Check("items.active-cooldowns-come-from-the-shipped-data")]
    public static void ActiveCooldownsComeFromTheShippedData()
    {
        // The item script's BBSetSlotSpellCooldownTimeVer2 overwrites the cast's inibin cooldown; a script that sets none keeps the inibin's.
        var cooldowns = new Amberline.Simulation.Items.ItemCooldowns(GameData.Default, new ItemCatalog());
        var expected = new Dictionary<int, double>
        {
            [3123] = 20.0,   // Executioner's Calling: script 20 over the inibin's Cooldown1..5 of 40
            [3140] = 90.0,   // Quicksilver Sash: script 90, inibin 0
            [3154] = 180.0,  // Wriggle's Lantern: NewCooldown 180 through SrcVar
            [3128] = 60.0, [3144] = 60.0, [3146] = 60.0, [3069] = 60.0, [3142] = 60.0, [3143] = 60.0, [3183] = 60.0, [3184] = 60.0, [3187] = 60.0,
            [3157] = 90.0, [3180] = 90.0, [3131] = 40.0,
            [2043] = 0.0, [2044] = 0.0,                     // wards: no script cooldown, inibin 0
            [2003] = 1.0, [2004] = 1.0, [2037] = 1.0, [2038] = 12.0, [2039] = 12.0, [2042] = 1.0, [2047] = 1.0, // Cooldown1 of the potions
        };
        foreach (var (itemId, seconds) in expected)
            TestAssert.Near(seconds, cooldowns.Of(itemId), 1e-9, $"item {itemId} cooldown");
        TestAssert.Throws<InvalidDataException>(() => cooldowns.Of(1001));
    }

    /// <summary>
    /// Batch 303: the use and shop paths read the item inibin flags instead of the hand-copied id lists, so the flags have
    /// to name exactly the ids those lists held. <c>Consumed</c>: the potions, the elixirs, Oracle's (and 2047, not on
    /// Map1) and the two consumed wards; <c>UsableInStore</c>: the four elixirs the shop drinks on a full inventory and
    /// 2047, which Map1's <c>UnpurchasableItemList</c> keeps out of the shop, so no Map1 purchase changes;
    /// Wriggle's Lantern keeps its charge; every ward spell reaches 600.
    /// </summary>
    [Check("items.consumed-usable-in-store-and-ward-reach-come-from-the-inibin")]
    public static void ConsumedUsableInStoreAndWardReachComeFromTheInibin()
    {
        var catalog = new ItemCatalog();
        var items = Directory.EnumerateFiles(GameDataPaths.Default.Data("Items"), "*.inibin")
            .Select(file => int.TryParse(Path.GetFileNameWithoutExtension(file), out var id) ? catalog.Get(id) : null)
            .OfType<ItemData>().ToArray();
        TestAssert.Equal(string.Join(",", new[] { 2003, 2004, 2037, 2038, 2039, 2042, 2043, 2044, 2047 }),
            string.Join(",", items.Where(item => item.Consumed).Select(item => item.ItemId).Order()), "Consumed items");
        TestAssert.Equal(string.Join(",", new[] { 2037, 2038, 2039, 2042, 2047 }),
            string.Join(",", items.Where(item => item.UsableInStore).Select(item => item.ItemId).Order()), "UsableInStore items");
        TestAssert.False(catalog.Get(3154)!.Consumed, "Wriggle's Lantern keeps its charge");
        foreach (var id in new[] { 2043, 2044, 3154 })
            TestAssert.Near(600.0, GameData.Default.Spell(catalog.Get(id)!.SpellName).CastRangeAt(1), 1e-9, $"item {id} ward reach");
    }

    [Check("items.shop-stays-fail-closed-for-records-without-an-instore-flag")]
    public static void ShopStaysFailClosedForKeylessRecords()
    {
        // The loader reads an absent InStore/CanBeSold/Price as the client defaults (true/true/100); the server still refuses to sell or buy
        // a record the shop never lists (rune records, 42xx placeholders, 10001/10002).
        var catalog = new ItemCatalog();
        foreach (var id in new[] { 5001, 4200, 8001, 10001 })
        {
            var item = catalog.Get(id) ?? throw new InvalidOperationException($"no item {id}");
            TestAssert.True(item.InStore && item.CanBeSold && !item.HasShippedStoreFlag, $"item {id} takes the client defaults");
            TestAssert.Equal(100, item.Price);
            TestAssert.Equal(-1, item.MaxStack);
            TestAssert.Equal(1, item.SpellLevel);
            TestAssert.Equal("unavailable_item", Inventory.Buy(catalog, Inventory.Empty(), 10_000.0, id).Reason);
            var held = Inventory.Empty().ToArray();
            held[0] = new InventorySlot(id);
            TestAssert.Equal("unsellable_item", Inventory.Sell(catalog, held, 0.0, 0).Reason);
        }
        TestAssert.True(Inventory.Buy(catalog, Inventory.Empty(), 10_000.0, 1001).Ok);
    }


    [Check("items.inventory.atomicity")]
    public static void InventoryAtomicityAndState()
    {
        var catalog = new ItemCatalog();
        var full = Enumerable.Range(0, Inventory.SlotCount)
            .Select(_ => (InventorySlot?)new InventorySlot(1036)).ToArray();
        var failed = Inventory.Buy(catalog, full, 20_000.0, 1001);
        TestAssert.False(failed.Ok, "a full inventory must reject a purchase");
        TestAssert.Equal("inventory_full", failed.Reason);
        TestAssert.True(ReferenceEquals(full, failed.Slots), "rejected purchase changed the input list");

        // Batch 149 step 5: Rod of Ages' ageing clock is Items/3027.luaobj's own InstanceVars.BonusHealth /
        // BonusMana / BonusAbilityPower, so the per-instance state is an empty shell — this check only needs one
        // to prove it travels with the purchased object.
        var state = new InventoryItemState();
        var stack = new InventorySlot(2003, 2, 7, state);
        var slots = Inventory.Empty().ToArray();
        slots[0] = stack;
        var bought = Inventory.Buy(catalog, slots, 1_000.0, 2003);
        TestAssert.True(bought.Ok);
        TestAssert.Equal(3, bought.Slots[0]!.Count);
        TestAssert.True(ReferenceEquals(state, bought.Slots[0]!.State),
            "adding to a surviving stack replaced its per-instance state");
        var swapped = Inventory.Swap(bought.Slots, bought.Gold, 0, 1);
        TestAssert.True(ReferenceEquals(state, swapped.Slots[1]!.State),
            "slot swap replaced the purchased instance state");
        var consumed = Inventory.Consume(catalog, swapped.Slots, swapped.Gold, 1);
        TestAssert.True(ReferenceEquals(state, consumed.Slots[1]!.State),
            "consuming from a surviving stack replaced its per-instance state");

        var malformed = CreateMalformedCatalog(out var malformedRoot);
        try
        {
            var malformedSlots = Inventory.Empty();
            var cyclic = Inventory.Buy(malformed, malformedSlots, 100.0, 900001);
            TestAssert.False(cyclic.Ok);
            TestAssert.Equal("malformed_recipe", cyclic.Reason);
            TestAssert.Equal(100.0, cyclic.Gold);
            TestAssert.True(ReferenceEquals(malformedSlots, cyclic.Slots),
                "cyclic recipe rejection was not atomic");
            var missing = Inventory.Buy(malformed, malformedSlots, 100.0, 900003);
            TestAssert.False(missing.Ok);
            TestAssert.Equal("malformed_recipe", missing.Reason);
        }
        finally
        {
            Directory.Delete(malformedRoot, recursive: true);
        }
    }

    [Check("items.inventory.modifiers")]
    public static void StaticModifiersAndUniqueStats()
    {
        var catalog = new ItemCatalog();
        var slots = Inventory.Empty().ToArray();
        slots[0] = new InventorySlot(1001);
        slots[1] = new InventorySlot(3047);
        slots[2] = new InventorySlot(3111);
        slots[3] = new InventorySlot(1062);
        slots[4] = new InventorySlot(1063);
        slots[5] = new InventorySlot(3031);
        var totals = Inventory.AggregateModifiers(catalog, slots);
        TestAssert.Near(70.0, totals["FlatMovementSpeedMod"], 1e-9);
        TestAssert.Near(0.3499999940395355, totals["PercentHardnessMod"], 1e-9);
        // Batch 144: this aggregation owns the inibin rows above and nothing else. The purchase group's own numbers
        // left it for the items' shipped scripts — 1062/1063's shared DoranT2Health (+200 flat HP) and 3031's
        // InfinityEdge (+0.5 crit damage) land on the hero through BbItemModule.Poll and its buffs, and
        // items.live-purchase-script-stats pins them there.
        TestAssert.Near(0.0, Amount(totals, "FlatHPPoolMod"), 1e-9,
            "Doran's scripted max health is not an inventory modifier");
        TestAssert.Near(0.0, Amount(totals, "FlatCritDamageMod"), 1e-9,
            "Infinity Edge's scripted crit damage is not an inventory modifier");
        // No hand-written row is left (batch 149 step 4 took the last one, 3187's -0.10 cooldown): every scripted
        // number is its own shipped script's now and items.live-purchase-script-stats pins them where they land.
        // 3187's is the HextechSweeper window's own IncPermanentPercentCooldownMod; keeping the row beside it counted
        // twice, which is what this zero and the live check together stand for.
        var sweeper = Inventory.Empty().ToArray();
        sweeper[0] = new InventorySlot(3187);
        TestAssert.Near(0.0, Amount(Inventory.AggregateModifiers(catalog, sweeper), "PercentCooldownMod"), 1e-9,
            "Hextech Sweeper's cooldown is its own window's, not an inventory modifier");
        var converted = Inventory.Empty().ToArray();
        converted[0] = new InventorySlot(3065);
        TestAssert.Near(0.0, Amount(Inventory.AggregateModifiers(catalog, converted), "PercentCooldownMod"), 1e-9,
            "Spirit Visage's cooldown is its own window's, not an inventory modifier");

        var duplicateShoes = Inventory.Empty().ToArray();
        duplicateShoes[0] = new InventorySlot(3020);
        duplicateShoes[1] = new InventorySlot(3020);
        TestAssert.Near(0.0, Amount(Inventory.AggregateModifiers(catalog, duplicateShoes), "FlatMagicPenetrationMod"),
            1e-9, "Sorcerer's Shoes' pen is 20 per copy of UpdateSelfBuffStats, not an inventory modifier");
    }

    /// <summary>One aggregated modifier, where a row the lookup does not have reads as zero.</summary>
    // Batch 311: "Only one pair of boots at a time will increase your movement speed" (shipped fontconfig, the Movement
    // Speed stat tooltip; every boots description: "does not stack with other Boots"). The pair that counts is the
    // largest (Reconstruction, see Inventory.AggregateModifiers), not the first slot; a non-boots speed item still adds.
    [Check("items.inventory.boots-unique-speed")]
    public static void BootsSpeedCountsOnce()
    {
        var gameData = new GameData();
        var catalog = new ItemCatalog(gameData.Paths);
        InventorySlot?[] Slots(params int[] ids)
        {
            var slots = Inventory.Empty().ToArray();
            for (var index = 0; index < ids.Length; index++) slots[index] = new InventorySlot(ids[index]);
            return slots;
        }
        double MoveSpeed(InventorySlot?[] slots, params BuffState[] buffs) =>
            HeroStatCalculator.Calculate(gameData, catalog, new HeroStatsInput("Annie", 1, slots, buffs)).MoveSpeed;

        TestAssert.Near(90.0, Inventory.AggregateModifiers(catalog, Slots(1001, 3009))["FlatMovementSpeedMod"], 1e-9,
            "Boots of Speed first, Swiftness second: the larger pair counts, not the first");
        TestAssert.Near(90.0, Inventory.AggregateModifiers(catalog, Slots(3009, 1001))["FlatMovementSpeedMod"], 1e-9);
        var baseSpeed = gameData.StatsAtLevel(gameData.Champion("Annie"), 1).MoveSpeed;
        TestAssert.Near(baseSpeed + 90.0, MoveSpeed(Slots(1001, 3009)), 1e-9, "two pairs give one pair's speed");
        TestAssert.Near(MoveSpeed(Slots(3009)), MoveSpeed(Slots(3009, 3006, 1001)), 1e-9);

        // Zeal's +8% is not boots speed: it multiplies the one pair's flat bonus as it would on its own.
        var zeal = catalog.Get(3086)!.Modifier("PercentMovementSpeedMod");
        TestAssert.Near(0.07999999821186066, zeal, 1e-12);
        TestAssert.Near((baseSpeed + 50.0) * (1.0 + zeal), MoveSpeed(Slots(1001, 3086)), 1e-9,
            "one pair plus a non-boots speed item still add");

        // Mobility (130) beside Swiftness (90): Mobility is the pair that counts, and its in-combat
        // BootsOfMobilityDebuff -60 lands on the hero after that pick, so Swiftness's 90 does not come back.
        TestAssert.Near(baseSpeed + 70.0, MoveSpeed(Slots(3117, 3009),
            new BuffState("BootsOfMobilityDebuff", new BuffPayload { MoveSpeedFlat = -60.0 })), 1e-9);
    }

    private static double Amount(IReadOnlyDictionary<string, double> totals, string key) =>
        totals.TryGetValue(key, out var value) ? value : 0.0;

    [Check("items.inventory.recipes")]
    public static void RecipeAndSellingRegressions()
    {
        var catalog = new ItemCatalog();
        var empty = Inventory.Empty();
        var exact = Inventory.Buy(catalog, empty, 920.0, 3006);
        TestAssert.True(exact.Ok);
        TestAssert.Near(0.0, exact.Gold, 1e-9);
        TestAssert.Equal(3006, exact.Slots[0]!.ItemId);

        var oneShort = Inventory.Buy(catalog, empty, 919.0, 3006);
        TestAssert.False(oneShort.Ok);
        TestAssert.Equal("insufficient_gold", oneShort.Reason);
        TestAssert.True(ReferenceEquals(empty, oneShort.Slots));

        var components = Inventory.Buy(catalog, empty, 920.0, 1001);
        components = Inventory.Buy(catalog, components.Slots, components.Gold, 1042);
        var completed = Inventory.Buy(catalog, components.Slots, components.Gold + 150.0, 3006);
        TestAssert.True(completed.Ok);
        TestAssert.Equal(2, completed.Removed.Count);
        TestAssert.Equal(0, completed.Removed[0].Slot);
        TestAssert.Equal(1, completed.Removed[1].Slot);

        var oneSword = Inventory.Buy(catalog, empty, 1_000.0, 1036);
        TestAssert.Near(922.0, Inventory.CurrentCost(catalog, 3134, oneSword.Slots), 1e-9);
        var twoSwords = Inventory.Buy(catalog, oneSword.Slots, oneSword.Gold, 1036);
        TestAssert.Near(507.0, Inventory.CurrentCost(catalog, 3134, twoSwords.Slots), 1e-9);
        var brutalizer = Inventory.Buy(catalog, twoSwords.Slots, 507.0, 3134);
        TestAssert.True(brutalizer.Ok);
        TestAssert.Equal(2, brutalizer.Removed.Count);

        var phage = Inventory.Buy(catalog, empty, 10_000.0, 3044);
        var trinityPriceWithoutPhage = Inventory.TotalCost(catalog, 3078) - Inventory.TotalCost(catalog, 3044);
        TestAssert.Near(trinityPriceWithoutPhage,
            Inventory.CurrentCost(catalog, 3078, phage.Slots), 1e-9);
        var trinity = Inventory.Buy(catalog, phage.Slots, trinityPriceWithoutPhage, 3078);
        TestAssert.True(trinity.Ok);
        TestAssert.Equal(3044, trinity.Removed[0].ItemId);

        var full = Inventory.Empty();
        var wallet = 10_000.0;
        foreach (var id in new[] { 1001, 1042, 1036, 1036, 1036, 1036 })
        {
            var purchased = Inventory.Buy(catalog, full, wallet, id);
            TestAssert.True(purchased.Ok);
            full = purchased.Slots;
            wallet = purchased.Gold;
        }
        var capacityFreed = Inventory.Buy(catalog, full, 150.0, 3006);
        TestAssert.True(capacityFreed.Ok);
        TestAssert.Equal(3006, capacityFreed.Slots[0]!.ItemId);
        TestAssert.True(capacityFreed.Slots[1] is null);

        var soldGreaves = Inventory.Sell(catalog,
            new InventorySlot?[] { new InventorySlot(3006), null, null, null, null, null }, 0.0, 0);
        TestAssert.True(soldGreaves.Ok);
        TestAssert.Near(644.0, soldGreaves.Gold, 1e-9);

        var potionSlots = Inventory.Empty();
        var potionGold = 1_000.0;
        for (var count = 0; count < 10; count++)
        {
            var purchased = Inventory.Buy(catalog, potionSlots, potionGold, 2003);
            TestAssert.True(purchased.Ok);
            potionSlots = purchased.Slots;
            potionGold = purchased.Gold;
        }
        TestAssert.Equal(9, potionSlots[0]!.Count);
        TestAssert.Equal(1, potionSlots[1]!.Count);
        var soldPotion = Inventory.Sell(catalog, potionSlots, potionGold, 0);
        TestAssert.True(soldPotion.Ok);
        TestAssert.Equal(8, soldPotion.Slots[0]!.Count);
        TestAssert.Near(potionGold + 35.0 * 0.7, soldPotion.Gold, 1e-9);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static bool Present(ItemData item, string key) => item.Raw.TryGetValue(InibinReader.KeyHash("Data*" + key), out _);

    private static void CompareItem(ItemData actual, JsonElement expected)
    {
        TestAssert.Equal(expected.GetProperty("item_id").GetInt32(), actual.ItemId);
        TestAssert.Equal(expected.GetProperty("display_name").GetString(), actual.DisplayName);
        TestAssert.Equal(expected.GetProperty("spell_name").GetString(), actual.SpellName);
        // The archived corpus was recorded with false/0 for an absent key; the stock loader's defaults are MaxStack -1, Price 100,
        // InStore/CanBeSold/CanBeDropped 1 and SpellLevel 1 (the client's item array add), so an absent key is checked against those.
        TestAssert.Equal(Present(actual, "MaxStack") ? expected.GetProperty("max_stack").GetInt32() : -1, actual.MaxStack);
        TestAssert.Equal(Present(actual, "Price") ? expected.GetProperty("price").GetInt32() : 100, actual.Price);
        TestAssert.Equal(expected.GetProperty("consumed").GetBoolean(), actual.Consumed);
        TestAssert.Equal(expected.GetProperty("usable_in_store").GetBoolean(), actual.UsableInStore);
        TestAssert.Equal(expected.GetProperty("item_class").GetInt32(), actual.ItemClass);
        TestAssert.Equal(expected.GetProperty("item_type").GetInt32(), actual.ItemType);
        TestAssert.Equal(!Present(actual, "InStore") || expected.GetProperty("in_store").GetBoolean(), actual.InStore);
        TestAssert.Equal(expected.GetProperty("purchasable").GetBoolean(), actual.Purchasable);
        TestAssert.Equal(!Present(actual, "CanBeSold") || expected.GetProperty("can_be_sold").GetBoolean(), actual.CanBeSold);
        TestAssert.Equal(!Present(actual, "CanBeDropped") || expected.GetProperty("can_be_dropped").GetBoolean(), actual.CanBeDropped);
        TestAssert.Equal(expected.GetProperty("drops_on_death").GetBoolean(), actual.DropsOnDeath);
        TestAssert.Equal(expected.GetProperty("disappears_on_death").GetBoolean(), actual.DisappearsOnDeath);
        TestAssert.Equal(expected.GetProperty("use_when_acquired").GetBoolean(), actual.UseWhenAcquired);
        TestAssert.Equal(expected.GetProperty("is_recipe").GetBoolean(), actual.IsRecipe);
        TestAssert.Equal(expected.GetProperty("combo_item").GetInt32(), actual.ComboItem);
        TestAssert.Equal(Present(actual, "SpellLevel") ? expected.GetProperty("spell_level").GetInt32() : 1, actual.SpellLevel);
        TestAssert.Equal(expected.GetProperty("spell_charges").GetInt32(), actual.SpellCharges);
        TestAssert.Near(expected.GetProperty("sell_back").GetDouble(), actual.SellBack, 1e-9);

        var recipes = expected.GetProperty("recipe_items").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        TestAssert.Equal(recipes.Length, actual.RecipeItems.Count);
        for (var index = 0; index < recipes.Length; index++)
            TestAssert.Equal(recipes[index], actual.RecipeItems[index]);

        // The archived corpus predates promotion of these shipped stat aliases
        // from raw fields into ItemData.Modifiers. Migrate only this explicit
        // stat set; zero-valued records still carry the raw key and must be
        // checked below, while unrelated metadata stays unresolved.
        var promoted = expected.GetProperty("unresolved").EnumerateArray()
            .Where(entry => PromotedModifierNames.TryGetValue(entry[0].GetUInt32(), out _))
            .ToArray();
        var migratedModifiers = expected.GetProperty("modifiers").EnumerateArray()
            .Select(entry => (Name: entry[0].GetString()!, Value: entry[1].GetDouble()))
            .Concat(promoted
                .Where(entry => entry[1].ValueKind == JsonValueKind.Number &&
                                Math.Abs(entry[1].GetDouble()) > 0.0)
                .Select(entry => (Name: PromotedModifierNames[entry[0].GetUInt32()],
                    Value: entry[1].GetDouble())))
            .ToArray();
        var expectedModifiers = migratedModifiers.ToDictionary(entry => entry.Name, entry => entry.Value,
            StringComparer.Ordinal);
        TestAssert.Equal(expectedModifiers.Count, actual.Modifiers.Count, $"item {actual.ItemId} modifier count");
        foreach (var modifier in actual.Modifiers)
        {
            TestAssert.True(expectedModifiers.TryGetValue(modifier.Name, out var expectedValue),
                $"item {actual.ItemId} unexpected modifier {modifier.Name}");
            // Batch 64: the corpus holds the decimal b/10 of a byte/10 value, the client reads (float)b * 0.1f; they differ by at most
            // two float32 roundings (under 2e-7 relative, the same bound as data.gamedata.corpus).
            TestAssert.Near(expectedValue, modifier.Value, Math.Max(1e-7, Math.Abs(expectedValue) * 2e-7),
                $"item {actual.ItemId} modifier {modifier.Name}");
        }

        var unresolved = expected.GetProperty("unresolved").EnumerateArray().ToArray();
        // Some raw fields were intentionally promoted from Unresolved into
        // typed modifier entries. Keep the archived raw-value comparison for
        // those fields while comparing only the remaining metadata here.
        var retainedUnresolved = unresolved
            .Where(entry => !PromotedModifierNames.ContainsKey(entry[0].GetUInt32()))
            .ToArray();
        TestAssert.Equal(retainedUnresolved.Length, actual.Unresolved.Count,
            $"item {actual.ItemId} unresolved count");
        for (var index = 0; index < retainedUnresolved.Length; index++)
        {
            TestAssert.Equal(retainedUnresolved[index][0].GetUInt32(), actual.Unresolved[index].Key);
            CompareScalar(retainedUnresolved[index][1], actual.Unresolved[index].Value,
                $"item {actual.ItemId} unresolved {actual.Unresolved[index].Key}");
        }
        foreach (var entry in promoted)
        {
            var key = entry[0].GetUInt32();
            TestAssert.True(actual.Raw.TryGetValue(key, out var raw),
                $"item {actual.ItemId} promoted modifier raw key {key}");
            CompareScalar(entry[1], raw, $"item {actual.ItemId} promoted modifier {key}");
        }
    }

    private static readonly IReadOnlyDictionary<uint, string> PromotedModifierNames =
        new Dictionary<uint, string>
        {
            [250160022] = "rFlatDodgeMod",
            [331225348] = "rPercentCooldownMod",
            [1666342795] = "rFlatArmorPenetrationMod",
            [2481029467] = "rFlatMPModPerLevel",
            [3022420538] = "rFlatMPRegenModPerLevel",
            [4217354982] = "FlatEnergyRegenMod",
            [2827316447] = "rFlatEnergyRegenModPerLevel",
            [2138837669] = "FlatEnergyPoolMod",
            [1919127670] = "rFlatEnergyModPerLevel",
        };

    private static void CompareScalar(JsonElement expected, InibinValue actual, string context)
    {
        if (expected.ValueKind == JsonValueKind.String)
        {
            TestAssert.Equal(expected.GetString(), actual.Kind == InibinValueKind.String
                ? actual.AsString() : null, context);
            return;
        }
        if (expected.ValueKind == JsonValueKind.True || expected.ValueKind == JsonValueKind.False)
        {
            TestAssert.Equal(expected.GetBoolean(), actual.Kind == InibinValueKind.Boolean && actual.Boolean,
                context);
            TestAssert.Equal(InibinValueKind.Boolean, actual.Kind, context);
            return;
        }
        TestAssert.True(expected.ValueKind == JsonValueKind.Number, context);
        var expectedNumber = expected.GetDouble();
        var actualNumber = actual.Kind == InibinValueKind.Boolean
            ? (actual.Boolean ? 1.0 : 0.0) : actual.AsDouble();
        TestAssert.Near(expectedNumber, actualNumber, 1e-7, context);
    }

    private static void CompareResult(InventoryResult actual, JsonElement expected)
    {
        TestAssert.Equal(expected.GetProperty("ok").GetBoolean(), actual.Ok);
        TestAssert.Equal(expected.GetProperty("reason").GetString(), actual.Reason);
        TestAssert.Near(expected.GetProperty("gold").GetDouble(), actual.Gold, 1e-7);
        TestAssert.Equal(expected.GetProperty("slot").GetInt32(), actual.Slot);
        TestAssert.Equal(expected.GetProperty("count").GetInt32(), actual.Count);
        TestAssert.Equal(expected.GetProperty("item_id").GetInt32(), actual.ItemId);
        TestAssert.Equal(expected.GetProperty("source_slot").GetInt32(), actual.SourceSlot);
        TestAssert.Equal(expected.GetProperty("destination_slot").GetInt32(), actual.DestinationSlot);

        var expectedSlots = expected.GetProperty("slots").EnumerateArray().ToArray();
        TestAssert.Equal(Inventory.SlotCount, expectedSlots.Length);
        for (var index = 0; index < expectedSlots.Length; index++)
        {
            var expectedSlot = expectedSlots[index];
            var actualSlot = actual.Slots[index];
            if (expectedSlot.ValueKind == JsonValueKind.Null)
            {
                TestAssert.True(actualSlot is null, $"slot {index} should be empty");
                continue;
            }
            TestAssert.True(actualSlot is not null, $"slot {index} should contain an item");
            TestAssert.Equal(expectedSlot.GetProperty("item_id").GetInt32(), actualSlot!.ItemId);
            TestAssert.Equal(expectedSlot.GetProperty("count").GetInt32(), actualSlot.Count);
            TestAssert.Equal(expectedSlot.GetProperty("charges").GetInt32(), actualSlot.Charges);
        }

        var expectedRemoved = expected.GetProperty("removed").EnumerateArray().ToArray();
        TestAssert.Equal(expectedRemoved.Length, actual.Removed.Count);
        for (var index = 0; index < expectedRemoved.Length; index++)
        {
            var expectedEntry = expectedRemoved[index];
            var actualEntry = actual.Removed[index];
            TestAssert.Equal(expectedEntry[0].GetInt32(), actualEntry.Slot);
            TestAssert.Equal(expectedEntry[1].GetInt32(), actualEntry.ItemId);
            TestAssert.Equal(expectedEntry[2].GetInt32(), actualEntry.RemainingCount);
        }
    }

    private static ItemCatalog CreateMalformedCatalog(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "amberline-item-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "DATA", "Items"));
        WriteItem(root, 900001, 900002);
        WriteItem(root, 900002, 900001);
        WriteItem(root, 900003, 999998);
        return new ItemCatalog(new GameDataPaths(root));
    }

    private static void WriteItem(string root, int itemId, int recipeId)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DisplayName"] = $"synthetic_{itemId}",
            ["Price"] = "10",
            ["InStore"] = "1",
            ["IsPurchasable"] = "1",
            ["CanBeSold"] = "1",
            ["IsRecipe"] = "1",
            ["RecipeItem1"] = recipeId.ToString()
        };
        var path = Path.Combine(root, "DATA", "Items", itemId + ".inibin");
        File.WriteAllBytes(path, EncodeStrings(values));
    }

    private static byte[] EncodeStrings(IReadOnlyDictionary<string, string> values)
    {
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.Latin1, leaveOpen: true);
        var entries = values.Select(pair =>
            (Key: InibinReader.KeyHash("Data*" + pair.Key), Text: pair.Value)).ToArray();
        var blob = new MemoryStream();
        var offsets = new ushort[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            offsets[index] = checked((ushort)blob.Position);
            var bytes = Encoding.Latin1.GetBytes(entries[index].Text);
            blob.Write(bytes);
            blob.WriteByte(0);
        }
        var blobBytes = blob.ToArray();
        writer.Write((byte)2);
        writer.Write((ushort)blobBytes.Length);
        writer.Write((ushort)(1 << 12));
        writer.Write((ushort)entries.Length);
        foreach (var entry in entries) writer.Write(entry.Key);
        foreach (var offset in offsets) writer.Write(offset);
        writer.Write(blobBytes);
        return body.ToArray();
    }
}
