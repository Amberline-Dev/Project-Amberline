using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Items;

/// <summary>
/// Authoritative item-use runtime for the patch-126 item scripts. Inventory
/// mutation, buff lifecycle, combat, effects, and packets are separate typed
/// dependencies so a match cannot silently accept an unimplemented item.
/// </summary>
public sealed partial class ItemService
{
    // The active cooldowns are not listed here: ItemCooldowns reads them from the shipped item scripts and spell inibins.
    // Which items have an active is not listed here either: an item has one when its own SpellName names a shipped
    // Spells/<name>.luaobj with a cast body (UseActive's own membership test), so no hand-written id list can go stale.

    // The consumables, the one remaining group whose cast runs by hand (batch 149 step 3 keeps the inventory consume):
    // every item whose inibin sets Consumed and that is not a ward (batch 303: the restore potions, the elixirs and
    // Oracle's). Their own SpellName script does what the use does.

    // Reconstruction: item scripts never test buff-table capacity, and retail's full-table outcome is unrecovered; the emulator refuses the use
    // before a cost is spent. The test is the one row the cast cannot do without (the script's own buff name), which is
    // what the hand-written actives checked one by one. A use that adds several rows and gets none is refused here
    // rather than left half-applied; a live Banshee's Veil still consumes Mourning's accepted cast.
    private static readonly IReadOnlyDictionary<int, string> CapacityPreflightBuff =
        new Dictionary<int, string>
        {
            [3123] = "GrievousWound", [3131] = "LightstrikerBuff", [3142] = "SpectralFury",
            [3157] = "ZhonyasRingShield", [3183] = "Haste", [3184] = "OdinEntropicClaymore",
        };

    // The ward actives: Vision Ward, Sight Ward, Wriggle's Lantern. Whether a use spends a charge is the inibin's Consumed
    // (2043 and 2044 do, 3154 does not) and the reach is the ward spell's CastRange (600, batch 303). Everything else about
    // a ward (the character it spawns, its buffs, its lifetime and its sight) is the shipped Spells/<SpellName>.luaobj's
    // own SelfExecute.
    private static readonly HashSet<int> WardItems = [2043, 2044, 3154];

    private readonly WorldState _world;
    private readonly ItemCatalog _catalog;
    private readonly BuffLifecycle _buffs;
    private readonly ISpellCombatService _combat;
    private readonly ISpellEffectSink _effects;
    private readonly IItemRuntimeTransport _transport;
    private readonly NavigationGrid _navigation;
    private readonly GameData _data;
    private readonly ItemCooldowns _cooldowns;
    private double _time;

    public ItemService(
        WorldState world,
        ItemCatalog catalog,
        BuffLifecycle buffs,
        IBuffEffectHooks buffEffects,
        ISpellCombatService combat,
        ISpellEffectSink effects,
        IItemRuntimeTransport transport,
        NavigationGrid navigation,
        GameData? gameData = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        ArgumentNullException.ThrowIfNull(buffEffects);
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _data = gameData ?? GameData.Default;
        _cooldowns = new ItemCooldowns(_data, _catalog);
    }

    public double CurrentTime => _time;

    /// <summary>
    /// Whether the cast of an item's active breaks the caster's stealth, as the shipped cast script says it
    /// (<c>CastingBreaksStealth</c>, or <c>DoesntTriggerSpellCasts</c> when that flag is unset). Arguments are the
    /// casting hero and the item's script name. Wired by the match composition next to
    /// <see cref="CastItemSpell"/>; item checks without a spell system leave it unset and every cast breaks the
    /// stealth, which is what an unset flag means on a cast that does trigger casts.
    /// </summary>
    public Func<Entity, string, bool>? CastingBreaksStealthOf { get; set; }

    /// <summary>
    /// The shipped <c>Spells</c> script's own gate of an item's active (<c>CanCast</c>): RegenerationPotion refuses
    /// above 99 % health and FlaskOfCrystalWater above 99 % mana. Arguments are the casting hero, the item's script
    /// name and its inventory slot. Wired by the match composition next to <see cref="CastItemSpell"/>; item checks
    /// without a spell system leave it unset and no script gate is asked.
    /// </summary>
    public Func<Entity, string, int, bool>? CanCastItemSpell { get; set; }

    /// <summary>
    /// The active slot cast of an item, run from its shipped <c>DATA/Spells</c> script (batch 149 steps 3 and 4): the
    /// potions', elixirs' and Oracle's add to the drinking hero (<c>TargetExecute</c>), the wards' placement
    /// (<c>SelfExecute</c>) and the whole active group's own cast — Zhonya's Hourglass, Deathfire Grasp, Hextech
    /// Gunblade, Bilgewater Cutlass, Executioner's Calling, Randuin's Omen, Shurelya's Crest, Quicksilver Sash,
    /// Youmuu's Ghostblade, Lightstriker, Prilisa's Blessing, Odyn's Veil, Entropic Claymore and Hextech Sweeper.
    /// Arguments are the casting hero, the item's script name, its inventory slot, the cast point, the cast end and
    /// the aimed-at unit (the hero itself for a self cast), then the item id the use belongs to: the recap identity of
    /// the damage that cast and its chains deal. The slot cannot give it (a shop elixir is applied without one) and
    /// an active used from slot 0..3 would otherwise be recapped as a champion spell. Wired like
    /// <see cref="CanCastItemSpell"/>; what stays here is what no script decides: the inventory consume, the used
    /// slot's cooldown and the packets.
    /// </summary>
    public Action<Entity, string, int, Position3, Position3, Entity?, int>? CastItemSpell { get; set; }

    /// <summary>
    /// Whether the item-slot cast of an inventory slot names a unit with the click rather than a point. The answer is
    /// the shipped spell's own targeting: a unit-target cast (TTYPE_Target / TTYPE_TargetOrLocation) asks for one and
    /// every other shape is placed or drunk and does not. The wards and the consumables still cast by hand never ask
    /// for a unit either; an item with no script active keeps the conservative input gate.
    /// </summary>
    internal static bool InputTargetsUnit(Entity hero, int slot, GameData data, ItemCatalog catalog)
    {
        var index = slot - 4;
        if ((uint)index >= Inventory.SlotCount || index >= hero.Inventory.Count ||
            hero.Inventory[index] is not { } item)
            return true;
        if (WardItems.Contains(item.ItemId) || catalog.Get(item.ItemId)?.Consumed == true)
            return false;
        if (ActiveTargeting(data, catalog, item.ItemId) is not { } targeting) return true;
        return targeting is BbTargetType.TTYPE_Target or BbTargetType.TTYPE_TargetOrLocation;
    }

    /// <summary>
    /// The targeting shape of an item's script active, or null when it has none (no <c>SpellName</c>, no shipped
    /// script, or a script without a cast body). Same question <see cref="UseActive"/> asks of its own spell.
    /// </summary>
    private static BbTargetType? ActiveTargeting(GameData data, ItemCatalog catalog, int itemId)
    {
        var spellName = catalog.Get(itemId)?.SpellName;
        if (string.IsNullOrEmpty(spellName) || !HasCastHook(data, spellName)) return null;
        try { return BbChampionModule.Targeting(data.Spell(spellName)); }
        catch (InibinException) { return BbTargetType.TTYPE_Self; }
    }

    public ItemUseResult Use(Entity hero, ItemUseRequest request)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (request.IsSummoner) return ItemUseResult.Reject("summoner_slot");
        if (request.Slot < 4 || request.Slot >= 4 + Inventory.SlotCount)
            return ItemUseResult.Reject("invalid_slot");
        if (!Finite(request.Position) || !Finite(request.EndPosition))
            return ItemUseResult.Reject("invalid_position");
        if (!hero.Alive) return ItemUseResult.Reject("dead");

        var inventorySlot = request.Slot - 4;
        EnsureCooldowns(hero);
        var slot = hero.Inventory[inventorySlot];
        if (slot is null) return ItemUseResult.Reject("empty_slot");
        // BBSealSpellSlot with SlotType InventorySlots (DeathDefiedBuff seals the six item slots): the slot's cast bit is clear (SpellSlotSeals).
        if (hero.SpellSeals.IsSealed(false, request.Slot))
            return ItemUseResult.Reject("slot_sealed");
        if (!CanUseItemSpell(hero, slot.ItemId))
            return ItemUseResult.Reject("action_disabled");

        if (WardItems.Contains(slot.ItemId))
            return UseWard(hero, request, inventorySlot, slot);
        if (_catalog.Get(slot.ItemId)?.Consumed == true)
            return UseConsumable(hero, request, inventorySlot, slot);
        return UseActive(hero, request, inventorySlot, slot);
    }

    public void Tick(double dt, bool advanceCooldowns = true)
    {
        if (!double.IsFinite(dt) || dt < 0.0) throw new ArgumentOutOfRangeException(nameof(dt));
        _time += dt;
        if (advanceCooldowns)
        {
            foreach (var hero in _world.Entities.Values.Where(entity => entity.UnitType == UnitType.Hero))
            {
                EnsureCooldowns(hero);
                for (var index = 0; index < hero.ItemCooldowns.Count; index++)
                    hero.ItemCooldowns[index] = Math.Max(0.0, hero.ItemCooldowns[index] - dt);
            }
        }
    }

    /// <summary>
    /// The active of an item whose shipped <c>Spells/&lt;SpellName&gt;.luaobj</c> has a cast body — the whole
    /// item-active group of batch 149 step 4: Zhonya's Hourglass, Deathfire Grasp, Hextech Gunblade, Bilgewater
    /// Cutlass, Executioner's Calling, Randuin's Omen, Shurelya's Crest, Quicksilver Sash, Youmuu's Ghostblade,
    /// Lightstriker, Prilisa's Blessing, Odyn's Veil, Entropic Claymore and Hextech Sweeper. What the use does is
    /// that script's own: the add, the damage, the dispel, the cooldown it writes over every slot that holds the
    /// item, the missiles it casts and the vision bubble it drops. What stays here is what no script decides — the
    /// gates above, the target and range the spell inibin names, the not-retail capacity preflight, the used slot's
    /// own cooldown and the packets.
    /// <para>
    /// The membership test is the script itself (see <see cref="HasCastHook"/>), so no hand-written id list says
    /// which items have an active. A <c>SpellName</c> with no cast body is not an active and the use is refused
    /// (<c>unsupported_item</c>), which is what the old tables answered for every id outside them.
    /// </para>
    /// </summary>
    private ItemUseResult UseActive(Entity hero, ItemUseRequest request, int inventorySlot, InventorySlot slot)
    {
        if (hero.ItemCooldowns[inventorySlot] > 0.0) return ItemUseResult.Reject("cooldown");
        if (ScriptName(slot.ItemId) is not { } spellName || !HasCastHook(_data, spellName))
            return ItemUseResult.Reject("unsupported_item");
        var spell = SpellOf(spellName);
        var targeting = spell is null ? BbTargetType.TTYPE_Self : BbChampionModule.Targeting(spell);
        var range = spell is null ? 0.0 : spell.CastRangeAt(1);
        Entity? target = null;
        Position2 aim;
        if (targeting is BbTargetType.TTYPE_Target or BbTargetType.TTYPE_TargetOrLocation)
        {
            target = _world.Get(request.TargetNetId);
            // The target gate is the shipped spell's own flags (AffectEnemies | AffectHeroes on all four targeted
            // actives, without AffectMinions, AffectTurrets or AffectBuildings), the same one a champion unit-target
            // cast gets. Validated before any cost is spent.
            if (target is null || spell is null ||
                BbSpellTargeting.RejectTarget(hero, target, BbSpellTargeting.Names(BbSpellTargeting.Flags(spell))) is not null ||
                target.Protected || !_combat.IsTargetable(target, hero))
                return ItemUseResult.Reject("target");
            aim = target.Position;
        }
        else
        {
            // A self or self-area cast is centred on its owner and needs no aim; every other shape is placed at the
            // point the player named (Hextech Sweeper reads it back as its own TargetPos).
            aim = targeting is BbTargetType.TTYPE_Self or BbTargetType.TTYPE_SelfAOE
                ? hero.Position
                : new Position2(request.Position.X, request.Position.Z);
        }
        if (target is null && targeting is not (BbTargetType.TTYPE_Self or BbTargetType.TTYPE_SelfAOE) &&
            range > 0.0 && DistanceSquared(aim, hero.Position) > range * range)
            return ItemUseResult.Reject("range");
        else if (target is not null && range > 0.0 &&
                 DistanceSquared(target.Position, hero.Position) > range * range)
            return ItemUseResult.Reject("range");

        // Reconstruction: see CapacityPreflightBuff. Mourning's is the one its unshielded effect needs: a live Banshee's
        // Veil is left to consume the accepted cast instead of being refused here.
        if (CapacityPreflightBuff.TryGetValue(slot.ItemId, out var capacityName) &&
            !(slot.ItemId == 3123 && target is not null && target.Buffs.Any(buff => buff.Name == "BansheesVeil")) &&
            !BuffLifecycle.CanAddNamedBuffs(target is not null && slot.ItemId == 3123 ? target : hero, capacityName))
            return ItemUseResult.Reject("buff_slots_full");

        var cooldown = hero.CooldownsDisabled ? 0.0 : _cooldowns.Of(slot.ItemId);
        SetCooldown(hero, inventorySlot, cooldown);
        var at = new Position3(aim.X, _navigation.HeightAt(aim), aim.Z);
        var launch = new Position3(hero.Position.X, _navigation.HeightAt(hero.Position), hero.Position.Z);
        SendCast(hero, request, spellName, cooldown, at, launch,
            target is null ? null : new[] { new SpellCastTarget(target.NetId, at, 0) });
        _effects.SendCooldown(hero, request.Slot, cooldown);

        // The cast itself. The script's SelfExecute / TargetExecute own what the use does and write the cooldown of
        // every slot that holds the item over the one just set, the way retail's own cost write is followed by the
        // script's own (BBSetSlotSpellCooldownTimeVer2).
        CastItemSpell?.Invoke(hero, spellName, inventorySlot, at, at, target ?? hero, slot.ItemId);

        SendUse(hero, inventorySlot, slot.Count, slot.Charges);
        return ItemUseResult.Success;
    }

    /// <summary>
    /// Whether the shipped <c>Spells/&lt;name&gt;.luaobj</c> an item's <c>SpellName</c> names has a cast body
    /// (<c>SelfExecuteBuildingBlocks</c> or <c>TargetExecuteBuildingBlocks</c>): the membership test of the active
    /// group, and the same question <c>BbItemModule.CastItemSpell</c> asks before it runs one. Cached by script name
    /// the way <c>ItemPassiveService</c> caches its own shipped-script flags.
    /// </summary>
    private static bool HasCastHook(GameData data, string spellName)
    {
        if (CastHooks.TryGetValue(spellName, out var found)) return found;
        var script = BbScriptLoader.Load(data.Paths, $"Spells/{spellName}.luaobj").Script;
        return CastHooks[spellName] = script is not null &&
            (script.Event("SelfExecute") is not null || script.Event("TargetExecute") is not null);
    }

    private static readonly Dictionary<string, bool> CastHooks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped spell record of an item's active, or null when its inibin names no spell.</summary>
    private SpellData? SpellOf(string spellName)
    {
        try { return _data.Spell(spellName); }
        catch (InibinException) { return null; }
    }

    private ItemUseResult UseWard(Entity hero, ItemUseRequest request, int inventorySlot,
        InventorySlot slot)
    {
        var consumed = _catalog.Get(slot.ItemId)!.Consumed;
        if (hero.ItemCooldowns[inventorySlot] > 0.0) return ItemUseResult.Reject("cooldown");
        if (ScriptName(slot.ItemId) is not { } spellName || SpellOf(spellName) is not { } spell)
            return ItemUseResult.Reject("unsupported_item");
        var target = new Position2(request.Position.X, request.Position.Z);
        var reach = spell.CastRangeAt(1);
        if (DistanceSquared(target, hero.Position) > reach * reach)
            return ItemUseResult.Reject("range");

        var cooldown = hero.CooldownsDisabled ? 0.0 : _cooldowns.Of(slot.ItemId);
        InventoryResult? consumedResult = null;
        if (consumed)
        {
            consumedResult = Inventory.Consume(_catalog, hero.Inventory, hero.Gold, inventorySlot);
            if (!consumedResult.Ok) return ItemUseResult.Reject(consumedResult.Reason);
            hero.Inventory = consumedResult.Slots;
        }
        else SetCooldown(hero, inventorySlot, cooldown);

        // Spells/SightWard, VisionWard and WriggleLantern SelfExecute: BBSpawnMinion of the ward character
        // (IsWard, VisibilitySize 0, gold redirected to its owner), its three buffs (SharedWardBuff, the ward's own
        // 180 s invisibility row and the 2 s ItemPlacementMissile placement marker) and the placement missile of
        // extra slot 7. The ward is the script's own; nothing here builds one any more.
        // The ward cast packet intentionally carries a zero target height; only the launch point is sampled from the
        // navigation grid.
        var castPosition = new Position3(target.X, 0.0, target.Z);
        var launch = new Position3(hero.Position.X, _navigation.HeightAt(hero.Position), hero.Position.Z);
        CastItemSpell?.Invoke(hero, spellName, inventorySlot, castPosition, castPosition, hero, slot.ItemId);

        SendCast(hero, request, spellName, cooldown, castPosition, launch);
        _effects.SendCooldown(hero, request.Slot, cooldown);
        SendUse(hero, inventorySlot, consumedResult?.Count ?? slot.Count,
            hero.Inventory[inventorySlot]?.Charges ?? 0);
        return ItemUseResult.Success;
    }

    private ItemUseResult UseConsumable(Entity hero, ItemUseRequest request, int inventorySlot,
        InventorySlot slot)
    {
        var id = slot.ItemId;
        if (hero.ItemCooldowns[inventorySlot] > 0.0) return ItemUseResult.Reject("cooldown");
        var spellName = ScriptName(id);
        if (spellName is null) return ItemUseResult.Reject("unsupported_item");

        // The script's own CanCast (RegenerationPotion above 99 % health, FlaskOfCrystalWater above 99 % mana). The
        // reject string is the engine's public vocabulary: no shipped file names it, and the client's answer is
        // what the player sees.
        if (CanCastItemSpell is not null && !CanCastItemSpell(hero, spellName, inventorySlot))
            return ItemUseResult.Reject(CanCastReason(id));

        // Reconstruction: item scripts never test buff-table capacity, and retail's full-table outcome is unrecovered; the emulator refuses the
        // use before a charge is spent. The test is the primary buff the cast adds (the script's own name); a
        // potion's second row (Potion_Internal) then declines silently on a full table, which is what a full table
        // means. A second drink needs no new slot (BUFF_STACKS_AND_CONTINUE plus the renewing add) and passes.
        if (!BuffLifecycle.CanAddNamedBuffs(hero, spellName))
            return ItemUseResult.Reject("buff_slots_full");

        var result = Inventory.Consume(_catalog, hero.Inventory, hero.Gold, inventorySlot);
        if (!result.Ok) return ItemUseResult.Reject(result.Reason);
        hero.Inventory = result.Slots;

        // Spells/RegenerationPotion and FlaskOfCrystalWater add the 20 s restore row (10 HP or 5 mana a second,
        // MaxStack 5, stacked and continued); the five elixirs and Oracle's add their own named row. All of it is the
        // cast script's TargetExecute / SelfExecute now.
        var at = new Position3(hero.Position.X, _navigation.HeightAt(hero.Position), hero.Position.Z);
        CastItemSpell?.Invoke(hero, spellName, inventorySlot, at, at, hero, id);

        // The consumable's spell inibin carries the use cooldown (1 s on the potions
        // and Oracle's items, 12 s on Elixir of Brilliance/Elusiveness), so a stack
        // cannot be drunk twice in the same instant. The cooldown belongs to the used
        // slot's spell instance only: unlike Zhonya's or Mourning's script, the potion
        // scripts never loop over the other slots holding the same item. A used-up
        // stack leaves no slot to cool down.
        if (hero.Inventory[inventorySlot] is not null)
        {
            var cooldown = hero.CooldownsDisabled ? 0.0 : _cooldowns.Of(id);
            SetCooldown(hero, inventorySlot, cooldown);
            if (_effects.IsReferenceReady(hero)) _effects.SendCooldown(hero, request.Slot, cooldown);
        }
        SendUse(hero, inventorySlot, result.Count,
            hero.Inventory[inventorySlot]?.Charges ?? 0);
        return ItemUseResult.Success;
    }

    /// <summary>The shipped <c>Spells</c> script of an item's active, as the item catalog names it.</summary>
    private string? ScriptName(int itemId) =>
        _catalog.Get(itemId)?.SpellName is { Length: > 0 } name ? name : null;

    /// <summary>
    /// The client's answer to a use the script's <c>CanCast</c> refused. Only the two restore potions have such a
    /// gate and only those two have an answer string of their own.
    /// </summary>
    private static string CanCastReason(int id) => id == 2004 ? "full_mana" : "full_health";

    /// <summary>
    /// The shop's elixir application: an item whose inibin sets <c>UsableInStore</c> (2037/2038/2039/2042 on Map1) is applied
    /// on a purchase into a full inventory and never reaches it. A purchase is not a cast, so no <c>CanCast</c> is asked; the
    /// add is the same <c>TargetExecute</c> the use runs.
    /// </summary>
    public bool ApplyPurchasedElixir(Entity hero, int id)
    {
        if (_catalog.Get(id)?.UsableInStore != true) return false;
        if (ScriptName(id) is not { } spellName) return false;
        // Reconstruction: the same capacity test as the use path (retail's full-table outcome is unrecovered), so the shop answers "elixir_unavailable" instead of
        // spending the gold on a row that cannot be added.
        if (!BuffLifecycle.CanAddNamedBuffs(hero, spellName)) return false;
        var at = new Position3(hero.Position.X, _navigation.HeightAt(hero.Position), hero.Position.Z);
        CastItemSpell?.Invoke(hero, spellName, 0, at, at, hero, id);
        return true;
    }

    private void SendCast(Entity hero, ItemUseRequest request, string spellName,
        double cooldown, Position3 target, Position3 launch,
        IReadOnlyList<SpellCastTarget>? targets = null)
    {
        // Both stealth buffs honor the cast script's own CastingBreaksStealth flag (the rule their shipped
        // BuffOnSpellCast applies to a champion cast, Deceive.luaobj / VayneTumbleFade.luaobj); a cast that says
        // nothing and does not set DoesntTriggerSpellCasts breaks the stealth. An item's own active raises no
        // BuffOnSpellCast (batch 164), so this reader is the item path's runner of that rule.
        if (CastingBreaksStealthOf?.Invoke(hero, spellName) ?? true)
            foreach (var fade in hero.Buffs.Where(buff => buff.Name is "VayneTumbleFade" or "Deceive").ToArray())
                _buffs.RemoveBuff(hero, fade.Slot);
        if (!_effects.IsReferenceReady(hero)) return;
        _effects.SendCast(hero, new SpellCastInfo(
            SpellPackets.BuffNameHash(spellName), hero.NetId, 1, hero.NetId, 0,
            target, target, targets ?? Array.Empty<SpellCastTarget>(), Float(cooldown), 0.0f,
            request.Slot, launch));
    }

    /// <summary>
    /// The used slot's own cooldown, the write <c>ApplyCostsOfSpell</c> makes before a cast. The script of an active
    /// then overwrites every slot that holds the item (<c>BBSetSlotSpellCooldownTimeVer2</c>), the way retail's own
    /// write is followed by the script's; a ward's or a potion's script writes none and the used slot is all there is.
    /// </summary>
    private void SetCooldown(Entity hero, int inventorySlot, double cooldown)
    {
        EnsureCooldowns(hero);
        hero.ItemCooldowns[inventorySlot] = cooldown;
    }

    private void SendUse(Entity hero, int inventorySlot, int remainingCount, int charges)
    {
        if (_transport.IsWireReady(hero))
            _transport.SendUseItem(hero, inventorySlot, remainingCount, charges);
    }

    /// <summary>
    /// The stock owner callback that checks an item's spell, shared with champion casts
    /// (<see cref="SpellOwnerGate"/>). A root or net alone (CanMove only) never blocks an item, and Quicksilver Sash
    /// (CanCastWhileDisabled and CannotBeSuppressed in its inibin) works through every control.
    /// </summary>
    private bool CanUseItemSpell(Entity hero, int itemId)
    {
        SpellData? spell = null;
        if (_catalog.Get(itemId)?.SpellName is { Length: > 0 } spellName)
        {
            try { spell = _data.Spell(spellName); }
            catch (InibinException) { /* no spell record: every flag takes the client default (false) */ }
        }
        return SpellOwnerGate.CanUse(hero, spell);
    }

    private static void EnsureCooldowns(Entity hero)
    {
        while (hero.ItemCooldowns.Count < Inventory.SlotCount) hero.ItemCooldowns.Add(0.0);
    }

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    private static bool Finite(Position3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static float Float(double value) =>
        !double.IsFinite(value) || value < float.MinValue || value > float.MaxValue
            ? throw new ArgumentOutOfRangeException(nameof(value)) : (float)value;
}
