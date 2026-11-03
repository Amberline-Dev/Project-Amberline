using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Items;

public readonly record struct ShopOperationResult(bool Accepted, string Reason)
{
    public static ShopOperationResult Success => new(true, string.Empty);
    public static ShopOperationResult Reject(string reason) => new(false, reason);
}

/// <summary>Packet boundary for authoritative shop mutations.</summary>
public interface IShopTransport
{
    void SendRemoveItem(Entity hero, int slot, int remainingCount);
    void SendBuyItem(Entity hero, int slot, int itemId, int count);
    void SendConsumedPurchase(Entity hero, int itemId);
    void SendSwapItem(Entity hero, int sourceSlot, int destinationSlot);
    void SendItemCooldown(Entity hero, int slot, double seconds);
}

/// <summary>Concrete shop packet adapter. It shares the match queue with combat.</summary>
public sealed class ProtocolShopTransport : IShopTransport
{
    private readonly ICombatPacketQueue _queue;

    public ProtocolShopTransport(ICombatPacketQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public void SendRemoveItem(Entity hero, int slot, int remainingCount) =>
        _queue.Enqueue(ItemsPackets.RemoveItem(hero.NetId, slot, remainingCount));

    public void SendBuyItem(Entity hero, int slot, int itemId, int count) =>
        _queue.Enqueue(ItemsPackets.BuyItem(hero.NetId, slot, checked((uint)itemId), count,
            useOnBought: false));

    public void SendSwapItem(Entity hero, int sourceSlot, int destinationSlot) =>
        _queue.Enqueue(ItemsPackets.SwapItem(hero.NetId, sourceSlot, destinationSlot));

    public void SendConsumedPurchase(Entity hero, int itemId) =>
        _queue.Enqueue(ItemsPackets.BuyItem(hero.NetId, ItemsPackets.TemporaryItemSlot,
            checked((uint)itemId), 1, useOnBought: true));

    public void SendItemCooldown(Entity hero, int slot, double seconds) =>
        _queue.Enqueue(SpellPackets.SetCooldown(hero.NetId, checked((byte)slot),
            Float(seconds)));

    private static float Float(double value) =>
        ProtocolEncoding.Float32(value, nameof(value));
}

/// <summary>
/// Authoritative shop request service. Every mutation is produced by the pure
/// Inventory operation first, then committed and replicated in the source
/// order; rejected operations leave the hero tuple untouched.
/// </summary>
public sealed class ShopService
{
    // The client's shop test compares the 3D distance squared with 1562500 (1250^2), one
    // constant for every map (batch 460). Reconstruction: the server keeps the 2D distance, which every client-legal request also passes.
    public const double ShopRange = 1250.0;

    private readonly WorldState _world;
    private readonly ItemCatalog _catalog;
    private readonly MapItemRules? _mapRules;
    private readonly HeroStatsService _heroStats;
    private readonly IShopTransport _transport;
    private readonly Func<Entity, int, bool>? _consumeElixir;
    // Batch 149 part 2: a shop mutation is an inventory change, and the spell system rebinds the item copies on one
    // (BbItemModule.SyncInventory) before anything publishes. The copies' tooltip rows are script-written and keyed by
    // the spellbook slot the copy sits in, so they have to leave with the copy: without this the sale republished the
    // sold slot's row until the next item poll (items-live-dynamic-tooltip-slots), which the inventory-derived rows of
    // the hand-written stand-ins never did.
    private readonly Action<Entity>? _syncItemScripts;
    private readonly HashSet<uint> _open = new();
    // Retail HeroInventory (buy reply): a useOnBought purchase is placed in the temporary slot, used at once through
    // its item spell, and the client then asks the server to remove it (0x09, slot 7, sell flag clear).
    // The server never holds that item (the elixir is applied when bought), so the request is an acknowledgement: one is expected per
    // temporary purchase issued. Reconstruction: the original server's reply is unrecovered; it is answered like every other
    // removal (0x0B for the slot with count 0) so the client's temporary slot empties.
    private readonly Dictionary<uint, int> _temporaryItems = new();

    public ShopService(WorldState world, ItemCatalog catalog, HeroStatsService heroStats,
        IShopTransport transport, MapItemRules? mapRules = null,
        Func<Entity, int, bool>? consumeElixir = null, Action<Entity>? syncItemScripts = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _mapRules = mapRules;
        _heroStats = heroStats ?? throw new ArgumentNullException(nameof(heroStats));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _consumeElixir = consumeElixir;
        _syncItemScripts = syncItemScripts;
    }

    public bool IsOpen(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        return _open.Contains(hero.NetId);
    }

    public bool InShopRange(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        // Batch 460: read per call, so the shop is the match map's (a static field kept the first map a process used).
        var anchor = MapDefinitions.ShopPosition(hero.Team == Team.Chaos ? Team.Chaos : Team.Order);
        var dx = hero.Position.X - anchor.X;
        var dz = hero.Position.Z - anchor.Z;
        return dx * dx + dz * dz <= ShopRange * ShopRange;
    }

    // The stock check returns true for a dead player before checking shop range.
    public bool CanShop(Entity hero) => hero.UnitType == UnitType.Hero &&
        _world.Get(hero.NetId) is not null && (!hero.Alive || InShopRange(hero));

    public void UpdateAccess(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (!CanShop(hero)) _open.Remove(hero.NetId);
    }

    public ShopOperationResult Open(Entity hero, ShopOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (request.Header != 0) { _open.Remove(hero.NetId); return Reject("invalid_header"); }
        if (!CanShop(hero)) { _open.Remove(hero.NetId); return Reject("shop_unavailable"); }
        _open.Add(hero.NetId);
        return ShopOperationResult.Success;
    }

    public ShopOperationResult Buy(Entity hero, BuyItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (request.SenderNetId != hero.NetId) return Reject("wrong_sender");
        if (!CanShop(hero)) { _open.Remove(hero.NetId); return Reject("shop_unavailable"); }

        var result = Inventory.Buy(_catalog, hero.Inventory, hero.Gold,
            checked((int)request.ItemId), _mapRules);
        if (!result.Ok && result.Reason == "inventory_full" &&
            _catalog.Get(checked((int)request.ItemId))?.UsableInStore == true && _consumeElixir is not null)
        {
            var purchase = Inventory.Buy(_catalog, new InventorySlot?[Inventory.SlotCount],
                hero.Gold, (int)request.ItemId, _mapRules);
            if (!purchase.Ok) return Reject(purchase.Reason);
            if (!_consumeElixir(hero, purchase.ItemId)) return Reject("elixir_unavailable");
            hero.MatchStatistics.RecordPurchase(hero.Gold - purchase.Gold, true);
            hero.Gold = purchase.Gold;
            _temporaryItems[hero.NetId] = _temporaryItems.GetValueOrDefault(hero.NetId) + 1;
            _transport.SendConsumedPurchase(hero, purchase.ItemId);
            _heroStats.Refresh(hero);
            _heroStats.Push(hero);
            return ShopOperationResult.Success;
        }
        if (!result.Ok) return Reject(result.Reason);
        hero.MatchStatistics.RecordPurchase(hero.Gold - result.Gold,
            _catalog.Get(result.ItemId)!.Consumed);
        return Place(hero, result);
    }

    /// <summary>
    /// Batch 580: a free item for a level script (<c>BBCreateItem</c>, the Basic Tutorial's <c>CreateItem(player, 1029)</c> and
    /// <c>(player, 1031)</c> in EventWelcome). Reconstruction: the item takes the place a purchase would (a recipe takes its components),
    /// the hero's gold is unchanged, no shop range, map item list or purchase statistics apply (Map3's shop lists neither item:
    /// LEVELS/Map3 Constants.var <c>it_UseExplicitItemInclusion = 1</c>), and the client learns it from the purchase's 0x72.
    /// </summary>
    public ShopOperationResult Grant(Entity hero, int itemId)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (_catalog.Get(itemId) is null) return Reject("unknown_item");
        var cost = Inventory.CurrentCost(_catalog, itemId, hero.Inventory);
        var result = Inventory.Buy(_catalog, hero.Inventory, hero.Gold + cost, itemId, mapRules: null);
        return result.Ok ? Place(hero, result) : Reject(result.Reason);
    }

    private ShopOperationResult Place(Entity hero, InventoryResult result)
    {
        EnsureCooldowns(hero);
        CommitInventory(hero, result);
        _syncItemScripts?.Invoke(hero);
        foreach (var removed in result.Removed)
            if (removed.RemainingCount == 0) hero.ItemCooldowns[removed.Slot] = 0.0;
        // Reconstruction: the shipped item scripts set a cooldown on every inventory slot that holds the cast item at the
        // moment of use (the six-slot name loops) and nothing else, so a copy bought later would start ready. A new copy
        // inherits the longest remaining cooldown of an equipped copy here (kept from the 7 September sweep, which
        // closed the sell-and-rebuy reset); the original server's behaviour is unrecovered.
        // A copy that stacks into a slot already holding the item keeps that slot's own running cooldown: the cooldown
        // belongs to the used slot's spell instance (ItemService.Use), and a stack cannot be drunk twice in one instant.
        // Inventory.Buy counts 1 only for a copy that opens an empty slot, so a larger count means it stacked.
        var stacked = result.Count > 1;
        var inherited = hero.Inventory
            .Select((slot, index) => (slot, index))
            .Where(pair => pair.index != result.Slot && pair.slot?.ItemId == result.ItemId)
            .Select(pair => hero.ItemCooldowns[pair.index])
            .DefaultIfEmpty(0.0).Max();
        var cooldown = stacked ? hero.ItemCooldowns[result.Slot] : inherited;
        hero.ItemCooldowns[result.Slot] = cooldown;
        _heroStats.Refresh(hero);
        foreach (var removed in result.Removed)
            _transport.SendRemoveItem(hero, removed.Slot, removed.RemainingCount);
        _transport.SendBuyItem(hero, result.Slot, result.ItemId, result.Count);
        if (cooldown > 0.0) _transport.SendItemCooldown(hero, result.Slot + 4, cooldown);
        _heroStats.Push(hero);
        return ShopOperationResult.Success;
    }

    public ShopOperationResult Sell(Entity hero, RemoveItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (request.SenderNetId != hero.NetId) return Reject("wrong_sender");
        if (request.Slot == ItemsPackets.TemporaryItemSlot) return RemoveTemporary(hero, request);
        if (!request.Sell) return Reject("drop_unsupported");
        if (!CanShop(hero)) { _open.Remove(hero.NetId); return Reject("shop_unavailable"); }
        var result = Inventory.Sell(_catalog, hero.Inventory, hero.Gold, request.Slot);
        if (!result.Ok) return Reject(result.Reason);
        EnsureCooldowns(hero);
        CommitInventory(hero, result);
        _syncItemScripts?.Invoke(hero);
        if (result.Count == 0) hero.ItemCooldowns[result.Slot] = 0.0;
        _heroStats.Refresh(hero);
        _transport.SendRemoveItem(hero, result.Slot, result.Count);
        _heroStats.Push(hero);
        return ShopOperationResult.Success;
    }

    // The client's removal of the temporary slot after it used a useOnBought purchase (see _temporaryItems). Not a shop action: it
    // needs no shop range, and a sell of the slot is refused (the item was consumed, never owned).
    private ShopOperationResult RemoveTemporary(Entity hero, RemoveItemRequest request)
    {
        if (request.Sell) return Reject("temporary_item_unsellable");
        if (!_temporaryItems.TryGetValue(hero.NetId, out var pending) || pending <= 0) return Reject("no_temporary_item");
        if (pending == 1) _temporaryItems.Remove(hero.NetId);
        else _temporaryItems[hero.NetId] = pending - 1;
        _transport.SendRemoveItem(hero, ItemsPackets.TemporaryItemSlot, 0);
        return ShopOperationResult.Success;
    }

    public ShopOperationResult Swap(Entity hero, SwapItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (request.SenderNetId != hero.NetId) return Reject("wrong_sender");
        var result = Inventory.Swap(hero.Inventory, hero.Gold,
            request.SourceSlot, request.DestinationSlot);
        if (!result.Ok) return Reject(result.Reason);
        EnsureCooldowns(hero);
        CommitInventory(hero, result);
        _syncItemScripts?.Invoke(hero);
        (hero.ItemCooldowns[result.SourceSlot], hero.ItemCooldowns[result.DestinationSlot]) =
            (hero.ItemCooldowns[result.DestinationSlot], hero.ItemCooldowns[result.SourceSlot]);
        _transport.SendSwapItem(hero, result.SourceSlot, result.DestinationSlot);
        _heroStats.PushItemTooltips(hero);
        return ShopOperationResult.Success;
    }

    private static void CommitInventory(Entity hero, InventoryResult result)
    {
        hero.Inventory = result.Slots;
        hero.Gold = result.Gold;
    }

    private static void EnsureCooldowns(Entity hero)
    {
        while (hero.ItemCooldowns.Count < Inventory.SlotCount) hero.ItemCooldowns.Add(0.0);
    }

    private static ShopOperationResult Reject(string reason) => ShopOperationResult.Reject(reason);
}
