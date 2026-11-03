using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Amberline.Data;

namespace Amberline.Simulation;

/// <summary>Mutable typed state attached to one purchased item instance. Batch 149 step 1: the Bloodthirster's and
/// Warmog's growth counters are the copy's own script <c>InstanceVars</c> now (Items/3072 and 3083); batch 149
/// step 5 took Rod of Ages' ageing clock with it — Items/3027.luaobj keeps its own <c>BonusHealth</c>,
/// <c>BonusMana</c> and <c>BonusAbilityPower</c> per copy. Nothing is per-copy hand-written state any more, so this
/// type is the empty shell every <see cref="InventorySlot"/> still carries and clones.</summary>
public sealed class InventoryItemState
{
    public InventoryItemState Clone() => (InventoryItemState)MemberwiseClone();
}

/// <summary>A six-slot item entry. State belongs to the purchased object and survives slot movement.</summary>
public sealed class InventorySlot : IEquatable<InventorySlot>
{
    public InventorySlot(int itemId, int count = 1, int charges = 0, InventoryItemState? state = null)
    {
        ItemId = itemId;
        Count = count;
        Charges = charges;
        State = state ?? new InventoryItemState();
    }

    public int ItemId { get; }
    public int Count { get; }
    public int Charges { get; }
    public InventoryItemState State { get; }

    public InventorySlot Clone() => new(ItemId, Count, Charges, State.Clone());

    // Python's dataclass excludes state from equality. This is useful for
    // comparing inventory structure while retaining instance-local effects.
    public bool Equals(InventorySlot? other) => other is not null &&
        ItemId == other.ItemId && Count == other.Count && Charges == other.Charges;

    public override bool Equals(object? obj) => obj is InventorySlot other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ItemId, Count, Charges);
}

public sealed record RemovedInventoryItem(int Slot, int ItemId, int RemainingCount);

/// <summary>Result of one atomic inventory mutation or query-like swap.</summary>
public sealed class InventoryResult
{
    public InventoryResult(
        bool ok,
        string reason,
        IReadOnlyList<InventorySlot?> slots,
        double gold,
        int slot = -1,
        int count = 0,
        int itemId = 0,
        int sourceSlot = -1,
        int destinationSlot = -1,
        IReadOnlyList<RemovedInventoryItem>? removed = null)
    {
        Ok = ok;
        Reason = reason;
        Slots = slots;
        Gold = gold;
        Slot = slot;
        Count = count;
        ItemId = itemId;
        SourceSlot = sourceSlot;
        DestinationSlot = destinationSlot;
        Removed = removed ?? Array.Empty<RemovedInventoryItem>();
    }

    public bool Ok { get; }
    public string Reason { get; }
    public IReadOnlyList<InventorySlot?> Slots { get; }
    public double Gold { get; }
    public int Slot { get; }
    public int Count { get; }
    public int ItemId { get; }
    public int SourceSlot { get; }
    public int DestinationSlot { get; }
    public IReadOnlyList<RemovedInventoryItem> Removed { get; }
}

/// <summary>Pure six-slot inventory and item-shop operations.</summary>
public static class Inventory
{
    public const int SlotCount = 6;

    public static IReadOnlyList<InventorySlot?> Empty() =>
        new InventorySlot?[SlotCount];

    public static bool HasItem(IReadOnlyList<InventorySlot?> slots, int itemId)
    {
        EnsureSlots(slots);
        for (var index = 0; index < SlotCount; index++)
            if (slots[index]?.ItemId == itemId) return true;
        return false;
    }

    // The ScriptedStaticModifiers table this used to merge in (the BBIncStat/BBIncPermanentStat deltas recovered from
    // the shipped scripts, one entry per item id) is gone: every row moved to the item's own shipped script as its group
    // was converted — batch 144 the purchase-side group, batch 145 step 2 Frozen Heart's cooldown, batch 149 step 2 the
    // active-window group, batch 149 step 4 the last one (3187's -0.10, which its HextechSweeper window's own
    // IncPermanentPercentCooldownMod already applied and which counted twice while both were live). This aggregation is
    // the inibin rows only; items.live-purchase-script-stats pins the scripted numbers where they land now.
    public static IReadOnlyDictionary<string, double> AggregateModifiers(
        ItemCatalog catalog, IReadOnlyList<InventorySlot?> slots)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsureSlots(slots);
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var slot in slots)
        {
            if (slot is null) continue;
            var item = catalog.Get(slot.ItemId);
            if (item is null) continue;
            foreach (var modifier in item.Modifiers)
            {
                // Unique stats count once (batch 311). Boots: every boots description in the shipped
                // DATA/Menu/fontconfig_en_US.txt reads "UNIQUE Passive: Enhanced Movement N (does not stack with other
                // Boots)" and the Movement Speed stat tooltip (game_DynamicMenuText_[Attributes]_MovementSpeed) says
                // "Only one pair of boots at a time will increase your movement speed"; the seven boots are the only
                // shop items with a flat FlatMovementSpeedMod (other speed items carry PercentMovementSpeedMod, which
                // stacks). Tenacity: 3111/3170/3172/3173 carry PercentHardnessMod 0.35, "Does not stack with other
                // Tenacity items". Neither the item inibins, the client's item data table (472 bytes) nor
                // the client code hold a unique/group field or the rule itself: it is server-only.
                // Reconstruction: which pair counts is not in the shipped files; the largest is taken, as the ranked
                // "Enhanced Movement 1/2/3/5" names suggest. Mobility's in-combat -60 is its own script buff
                // (BootsOfMobilityDebuff), applied after this pick.
                if (modifier.Name is "FlatMovementSpeedMod" or "PercentHardnessMod")
                {
                    totals[modifier.Name] = totals.TryGetValue(modifier.Name, out var current)
                        ? Math.Max(current, modifier.Value) : modifier.Value;
                }
                else
                {
                    totals[modifier.Name] = totals.TryGetValue(modifier.Name, out var current)
                        ? current + modifier.Value * slot.Count : modifier.Value * slot.Count;
                }
            }
        }

        return new ReadOnlyDictionary<string, double>(totals);
    }

    public static double TotalCost(ItemCatalog catalog, int itemId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return RecursiveCost(catalog, itemId, null, null, true, new HashSet<int>());
    }

    public static double CurrentCost(ItemCatalog catalog, int itemId, IReadOnlyList<InventorySlot?> slots)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsureSlots(slots);
        return Quote(catalog, itemId, slots).Cost;
    }

    public static InventoryResult Buy(
        ItemCatalog catalog, IReadOnlyList<InventorySlot?> slots, double gold, int itemId,
        MapItemRules? mapRules = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsureSlots(slots);
        var item = catalog.Get(itemId);
        if (item is null) return Rejected("unknown_item", slots, gold);
        // Reconstruction: server-side guard; the retail server's shop validation is unknown, so it fails closed on records the shop never lists
        // (the loader's InStore default is true for a record without the key, ItemData.HasShippedStoreFlag).
        if (!item.InStore || !item.HasShippedStoreFlag || !item.Purchasable ||
            mapRules is not null && !mapRules.AllowsPurchase(itemId))
            return Rejected("unavailable_item", slots, gold);

        QuoteResult quote;
        try { quote = Quote(catalog, itemId, slots); }
        catch (InvalidDataException) { return Rejected("malformed_recipe", slots, gold); }
        if (gold < quote.Cost) return Rejected("insufficient_gold", slots, gold);

        var updated = new InventorySlot?[SlotCount];
        for (var index = 0; index < SlotCount; index++) updated[index] = slots[index];
        var removed = new List<RemovedInventoryItem>();
        foreach (var index in quote.MatchedSlots)
        {
            var component = updated[index];
            if (component is null) return Rejected("malformed_recipe", slots, gold);
            var remaining = component.Count - 1;
            updated[index] = remaining <= 0
                ? null
                : new InventorySlot(component.ItemId, remaining, component.Charges, component.State);
            removed.Add(new RemovedInventoryItem(index, component.ItemId, Math.Max(0, remaining)));
        }

        var stackLimit = Math.Max(1, item.MaxStack);
        var destination = -1;
        if (stackLimit > 1)
        {
            for (var index = 0; index < SlotCount; index++)
            {
                var candidate = updated[index];
                if (candidate is not null && candidate.ItemId == itemId && candidate.Count < stackLimit)
                {
                    destination = index;
                    break;
                }
            }
        }
        if (destination < 0)
        {
            for (var index = 0; index < SlotCount; index++)
                if (updated[index] is null) { destination = index; break; }
        }
        if (destination < 0) return Rejected("inventory_full", slots, gold);

        var previous = updated[destination];
        var count = previous is null ? 1 : previous.Count + 1;
        var charges = previous is null ? item.SpellCharges : previous.Charges;
        updated[destination] = previous is null
            ? new InventorySlot(itemId, count, charges)
            : new InventorySlot(itemId, count, charges, previous.State);
        removed.Sort((left, right) => left.Slot.CompareTo(right.Slot));
        return new InventoryResult(true, string.Empty, Array.AsReadOnly(updated), gold - quote.Cost,
            destination, count, itemId, removed: new ReadOnlyCollection<RemovedInventoryItem>(removed));
    }

    public static InventoryResult Sell(
        ItemCatalog catalog, IReadOnlyList<InventorySlot?> slots, double gold, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsureSlots(slots);
        if (!ValidSlot(slotIndex)) return Rejected("invalid_slot", slots, gold);
        var slot = slots[slotIndex];
        if (slot is null) return Rejected("empty_slot", slots, gold);
        var item = catalog.Get(slot.ItemId);
        // Reconstruction: server-side guard; the retail server's shop validation is unknown, so it fails closed on records the shop never lists
        // (an item that cannot be bought cannot be sold; CanBeSold still applies).
        if (item is null || !item.HasShippedStoreFlag || !item.CanBeSold) return Rejected("unsellable_item", slots, gold);

        double resaleBase;
        try { resaleBase = TotalCost(catalog, item.ItemId); }
        catch (InvalidDataException) { return Rejected("malformed_recipe", slots, gold); }
        var updated = new InventorySlot?[SlotCount];
        for (var index = 0; index < SlotCount; index++) updated[index] = slots[index];
        var count = slot.Count - 1;
        updated[slotIndex] = count <= 0
            ? null
            : new InventorySlot(slot.ItemId, count, slot.Charges, slot.State);
        return new InventoryResult(true, string.Empty, Array.AsReadOnly(updated),
            gold + resaleBase * item.SellBack, slotIndex, Math.Max(0, count), slot.ItemId);
    }

    public static InventoryResult Swap(
        IReadOnlyList<InventorySlot?> slots, double gold, int sourceSlot, int destinationSlot)
    {
        EnsureSlots(slots);
        if (!ValidSlot(sourceSlot) || !ValidSlot(destinationSlot))
            return Rejected("invalid_slot", slots, gold);
        var updated = new InventorySlot?[SlotCount];
        for (var index = 0; index < SlotCount; index++) updated[index] = slots[index];
        (updated[sourceSlot], updated[destinationSlot]) = (updated[destinationSlot], updated[sourceSlot]);
        var slot = updated[destinationSlot];
        return new InventoryResult(true, string.Empty, Array.AsReadOnly(updated), gold,
            destinationSlot, slot?.Count ?? 0, slot?.ItemId ?? 0, sourceSlot, destinationSlot);
    }

    public static InventoryResult Consume(
        ItemCatalog catalog, IReadOnlyList<InventorySlot?> slots, double gold, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsureSlots(slots);
        if (!ValidSlot(slotIndex)) return Rejected("invalid_slot", slots, gold);
        var slot = slots[slotIndex];
        if (slot is null) return Rejected("empty_slot", slots, gold);
        var item = catalog.Get(slot.ItemId);
        if (item is null || !item.Consumed) return Rejected("not_consumable", slots, gold);

        var updated = new InventorySlot?[SlotCount];
        for (var index = 0; index < SlotCount; index++) updated[index] = slots[index];
        var count = slot.Count - 1;
        updated[slotIndex] = count <= 0
            ? null
            : new InventorySlot(slot.ItemId, count, slot.Charges, slot.State);
        return new InventoryResult(true, string.Empty, Array.AsReadOnly(updated), gold,
            slotIndex, Math.Max(0, count), slot.ItemId);
    }

    private static InventoryResult Rejected(string reason, IReadOnlyList<InventorySlot?> slots, double gold) =>
        new(false, reason, slots, gold);

    private static bool ValidSlot(int slot) => slot >= 0 && slot < SlotCount;

    private static void EnsureSlots(IReadOnlyList<InventorySlot?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != SlotCount)
            throw new ArgumentException($"inventory must contain exactly {SlotCount} slots", nameof(slots));
    }

    private static QuoteResult Quote(ItemCatalog catalog, int itemId, IReadOnlyList<InventorySlot?> slots)
    {
        var available = new int[SlotCount];
        for (var index = 0; index < SlotCount; index++) available[index] = slots[index]?.ItemId ?? 0;
        var matched = new List<int>();
        var cost = RecursiveCost(catalog, itemId, available, matched, true, new HashSet<int>());
        return new QuoteResult(cost, matched);
    }

    private static double RecursiveCost(
        ItemCatalog catalog,
        int itemId,
        int[]? available,
        List<int>? matched,
        bool top,
        HashSet<int> visiting)
    {
        var item = catalog.Get(itemId) ?? throw new InvalidDataException(
            $"recipe references missing item {itemId}");
        if (!top && available is not null && matched is not null)
        {
            for (var index = 0; index < available.Length; index++)
            {
                if (available[index] != itemId) continue;
                available[index] = 0;
                matched.Add(index);
                return 0.0;
            }
        }

        if (!visiting.Add(itemId))
            throw new InvalidDataException($"cyclic item recipe at {itemId}");
        try
        {
            var cost = (double)item.Price;
            if (item.IsRecipe)
                foreach (var componentId in item.RecipeItems)
                    cost += RecursiveCost(catalog, componentId, available, matched, false, visiting);
            return cost;
        }
        finally { visiting.Remove(itemId); }
    }

    private sealed record QuoteResult(double Cost, IReadOnlyList<int> MatchedSlots);
}
