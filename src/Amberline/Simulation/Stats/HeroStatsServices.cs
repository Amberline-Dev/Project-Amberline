using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Stats;

/// <summary>Hooks that keep champion timing, spell costs, and buff removal outside the pure stat service.</summary>
public interface IHeroStatsHooks
{
    void UpdateAttackTiming(Entity hero, double attackSpeedMod);
    IReadOnlyList<double> ManaCosts(Entity hero);
    bool IsPendingActionBlocked(Entity hero);
    /// <summary>A script buff has toggled the champion slot on (<c>SpellToggleSlot</c>): the next cast of it costs nothing.</summary>
    bool IsSpellToggledOn(Entity hero, int spellSlot);
    /// <summary>Runs stale-buff cleanup with an explicit wire context.</summary>
    void RemoveBuff(Entity hero, int slot, bool emitWire);

    /// <summary>Ordinary removal retains the historical wire-emitting behavior.</summary>
    void RemoveBuff(Entity hero, int slot) => RemoveBuff(hero, slot, emitWire: true);
}

/// <summary>Transport boundary for the typed hero-stat replication snapshot.</summary>
public interface IHeroStatsTransport
{
    void Push(HeroStatsReplicationSnapshot snapshot);
    void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries);
}

public readonly record struct ItemTooltipEntry(int Slot, double First, double Second, double Third);

public sealed record HeroStatsReplicationSnapshot(
    uint HeroNetId,
    HeroStats Effective,
    double BaseAttackDamage,
    double BonusAttackDamage,
    int ActionState,
    int IsTargetable,
    uint CanCastBits1,
    uint CanCastBits2,
    double CurrentHealth,
    double CurrentMana,
    int Level,
    double Gold,
    double Experience,
    IReadOnlyList<double> ManaCosts);

/// <summary>
/// Root-scoped facade for refresh, transition bookkeeping, and replication.
/// It requires both champion hooks and a transport; no transition silently
/// drops an item reset or wire snapshot.
/// </summary>
public sealed class HeroStatsService
{
    private readonly GameData _gameData;
    private readonly ItemCatalog _itemCatalog;
    private readonly IHeroStatsHooks _hooks;
    private readonly IHeroStatsTransport _transport;

    /// <summary>The spell system's CanCast gate per slot (<see cref="SpellSystem.ScriptCastGateOpen"/>), set once the spells exist; null is open.</summary>
    public Func<Entity, int, bool>? CastGateOpen { get; set; }

    /// <summary>Whether a slot's spell ships <c>SpellData*CanCastWhileDisabled</c> (<see cref="SpellSystem.CastsWhileDisabled"/>); null is no exemption.</summary>
    public Func<Entity, int, bool>? CastsWhileDisabled { get; set; }

    public HeroStatsService(
        GameData gameData,
        ItemCatalog itemCatalog,
        IHeroStatsHooks hooks,
        IHeroStatsTransport transport)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _itemCatalog = itemCatalog ?? throw new ArgumentNullException(nameof(itemCatalog));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>The kind of the hero's one resource pool: its stat character's <c>PARType</c> (the champion, or the pushed form).</summary>
    internal PrimaryResourceType PrimaryResourceOf(Entity hero) => _gameData.Champion(hero.StatCharacterName).PrimaryResource;

    internal bool UsesMana(Entity hero) => PrimaryResourceOf(hero) == PrimaryResourceType.Mana;

    public HeroStats Refresh(Entity hero, bool push = false)
    {
        EnsureHero(hero);
        // Nothing of a sale is cleaned up here: every item's leftovers are its shipped script's own (batches 147, 149,
        // 183; batch 303 took the last row, Guinsoo's Rageblade stacks, which Items/3124's OnDeactivate leaves to
        // lapse on their own 5 s).
        hero.SetEquippedItemIds(hero.Inventory.Where(slot => slot is not null).Select(slot => slot!.ItemId));
        var previousMaxHealth = hero.MaxHealth;
        var previousMaxMana = hero.MaxMana;
        var stats = Calculate(hero);
        ApplyLoadoutContribution(hero, stats);

        // BBIncMaxHealth with IncCurrentHealth false adds to the pool without healing: that part of the change is not a gain of current health.
        var nonHealing = hero.Buffs.Sum(buff => buff.Payload.NonHealingMaxHp) + (hero.CharacterScriptStats?.NonHealingMaxHp ?? 0.0) +
                         (hero.ItemScriptStats?.NonHealingMaxHp ?? 0.0);
        var healthGain = stats.MaxHp - previousMaxHealth - (nonHealing - hero.NonHealingMaxHp);
        hero.NonHealingMaxHp = nonHealing;
        var manaGain = stats.MaxMp - previousMaxMana;
        hero.MaxHealth = stats.MaxHp;
        hero.MaxMana = stats.MaxMp;
        hero.Health = hero.Alive ? Math.Min(hero.MaxHealth, hero.Health + Math.Max(0.0, healthGain)) : 0.0;
        hero.Mana = Math.Min(hero.MaxMana, hero.Mana + Math.Max(0.0, manaGain));
        hero.AttackDamage = stats.AttackDamage;
        hero.Armor = stats.Armor;
        hero.SpellBlock = stats.SpellBlock;
        hero.Speed = stats.MoveSpeed;
        hero.FlatMovementSpeedMod = stats.FlatMovementSpeedMod;
        hero.FlatHpPoolMod = stats.FlatHpPoolMod;
        hero.FlatCastRangeMod = stats.FlatCastRangeMod;
        hero.AttackRange = stats.AttackRange;
        hero.HealthRegen = stats.HpRegen;
        hero.ManaRegen = stats.MpRegen;
        hero.AttackSpeedMod = stats.AttackSpeedMod;
        hero.AbilityPower = stats.AbilityPower;
        hero.Dodge = stats.Dodge;
        hero.CritChance = stats.CritChance;
        hero.CritMultiplier = stats.CritMultiplier;
        hero.CooldownMod = stats.CooldownMod;
        hero.Hardness = stats.Hardness;
        // DodgePiercing is the status bit BBSetDodgePiercing writes (batch 241): one bit only a script call changes, not a stat to rebuild.
        hero.ArmorPenetrationFlat = stats.ArmorPenFlat;
        hero.ArmorPenetrationPercent = stats.ArmorPenPercent;
        hero.MagicPenetrationFlat = stats.MagicPenFlat;
        hero.MagicPenetrationPercent = stats.MagicPenPercent;
        hero.LifeSteal = stats.LifeSteal;
        hero.SpellVamp = stats.SpellVamp;
        _hooks.UpdateAttackTiming(hero, stats.AttackSpeedMod);
        if (push) Push(hero);
        return stats;
    }

    /// <summary>Calculates loadout-aware stats without mutating combat fields.</summary>
    public HeroStats Calculate(Entity hero)
    {
        EnsureHero(hero);
        return HeroStatCalculator.Calculate(_gameData, _itemCatalog,
            new HeroStatsInput(hero.ChampionName, hero.Level, hero.Inventory, ScriptedPayloads(hero),
                hero.ItemState, hero.Runes, hero.Masteries, hero.PermanentStatMods, hero.StatCharacterName));
    }

    /// <summary>
    /// The hero's buffs plus the two script polls that hold no wire buff of their own: the character script's own stat
    /// deltas (<c>UpdateSelfBuffStats</c>, <c>Entity.CharacterScriptStats</c>) and the item scripts' (<c>Entity.ItemScriptStats</c>,
    /// one summed sample across the inventory copies). Each counts like one more buff payload.
    /// </summary>
    private static IEnumerable<BuffState> ScriptedPayloads(Entity hero)
    {
        IEnumerable<BuffState> payloads = hero.Buffs;
        if (hero.CharacterScriptStats is { } own) payloads = payloads.Append(new BuffState("CharacterScript", own));
        if (hero.ItemScriptStats is { } items) payloads = payloads.Append(new BuffState("ItemScript", items));
        return payloads;
    }

    public HeroStatsReplicationSnapshot Snapshot(Entity hero)
    {
        EnsureHero(hero);
        var champion = _gameData.Champion(hero.StatCharacterName);
        var effective = new HeroStats(
            hero.MaxHealth, hero.MaxMana, hero.AttackDamage, hero.Armor,
            hero.SpellBlock, hero.HealthRegen, hero.ManaRegen, hero.AttackSpeedMod,
            hero.Speed, hero.AttackRange, hero.AbilityPower, hero.Dodge,
            hero.CritChance, hero.CritMultiplier, hero.CooldownMod, hero.Hardness,
            hero.DodgePiercing, hero.ArmorPenetrationFlat,
            hero.ArmorPenetrationPercent, hero.MagicPenetrationFlat,
            hero.MagicPenetrationPercent, hero.LifeSteal, hero.SpellVamp)
        {
            FlatMovementSpeedMod = hero.FlatMovementSpeedMod
        };
        var baseStats = _gameData.StatsAtLevel(champion, hero.Level);
        var pendingActionBlocked = _hooks.IsPendingActionBlocked(hero);
        var buffHardControl = hero.AnyBuffOrStatus(IsHardControlled);
        var silence = hero.Buffs.Any(buff => buff.Type == BuffType.Silence || buff.Payload.Silence);
        // The whole ActionState word the client keeps (CharacterState.ActionWord), not only its three permission bits: a taunted,
        // feared, asleep, suppressed or ghosted hero is told so, and the client's order gates read them. The pending-cast and
        // forced-move holds below are this server's own and clear the permission bits on top of it.
        var actionState = CharacterState.ActionWord(hero);
        if (pendingActionBlocked) actionState &= ~7u;
        if (hero.ForcedMove is not null && hero.ForcedMove.Effect != "Fear") actionState &= ~5u;

        uint castBits1 = uint.MaxValue;
        uint castBits2 = uint.MaxValue;
        // The stock hero's main spellbook, registered as mCanCastBits1. A slot whose spell ships CanCastWhileDisabled
        // keeps its bit under every control: the base unit's spell-use check (shared client and server code) skips the cannot-cast
        // states for it and itself refuses the suppressed caster of a spell without CannotBeSuppressed, from the ActionState word
        // (batch 314). Any other control clears the bit, as before. NOT RETAIL (owner, 26 October 2026, "Block in stasis"): the stasis
        // clears every slot's bit, exempt or not, because SpellOwnerGate.InStasis refuses every spell there (retail keeps the exempt bit).
        var stasis = SpellOwnerGate.InStasis(hero);
        for (var slot = 0; slot < 4; slot++)
        {
            var exempt = CastsWhileDisabled is { } exemptSpell && exemptSpell(hero, slot);
            if (stasis || !exempt && (buffHardControl || silence)) castBits1 &= ~(1u << slot);
        }
        // BBSealSpellSlot: a sealed slot's bit is clear in its spellbook's word (mCanCastBits1 is the main book, mCanCastBits2 the second,
        // summoner one; the second book has its own castBit1). The hand-written special cases above are the same
        // seals for the champions that are not run from their scripts yet (Vayne's W and Tumble are seals of her script since phase 3).
        castBits1 &= ~hero.SpellSeals.ClearedCastBits(summonerBook: false);
        castBits2 &= ~hero.SpellSeals.ClearedCastBits(summonerBook: true);
        // The spell's own CanCast gate (Starcall: some enemy unit within 610) clears its slot's bit while it refuses, so the client greys the
        // slot while nothing is in reach (finding 32). The stat block goes out once a second (CombatService), so the bit follows the gate.
        if (CastGateOpen is { } castGateOpen)
            for (var slot = 0; slot < 4; slot++)
                if (!castGateOpen(hero, slot)) castBits1 &= ~(1u << slot);

        var targetable = !hero.Alive || IsInvulnerable(hero) ||
                         hero.AnyBuffOrStatus(buff => buff.Payload.Untargetable) ? 0 : 1;

        var suppliedCosts = _hooks.ManaCosts(hero)
            ?? throw new InvalidOperationException("mana-cost hook returned null");
        if (suppliedCosts.Count != 4 || suppliedCosts.Any(cost => !double.IsFinite(cost)))
            throw new InvalidDataException("mana-cost hook must return exactly four finite costs");
        var costs = suppliedCosts.ToArray();
        // A slot a script buff toggled on (SpellToggleSlot: Janna's Howling Gale charge, Singed's Poison Trail) costs nothing: the shared
        // The spellbook's can-use check turns the owner's result 5 (PAR below the cost, the hero's check) into 0 when the
        // slot's toggle state is 2 (eToggleOn), and the shared spell cost adjustment (run by ApplyCostsOfSpell
        // from DoCast_ClientAndServer) spends no PAR for such a slot (batch 331), so the cast path charges nothing either (BbChampionModule).
        for (var slot = 0; slot < costs.Length; slot++)
            if (_hooks.IsSpellToggledOn(hero, slot)) costs[slot] = 0.0;
        // BBSetPARCostInc: the slot's mana cost increment is added to the slot's cost (the shipped scripts lower it to free, or raise it per cast).
        foreach (var (slot, increment) in hero.SpellParCostIncrements)
            if ((uint)slot < (uint)costs.Length) costs[slot] = Math.Max(0.0, costs[slot] + increment);
        foreach (var (slot, multiplier) in hero.SpellParCostMultipliers)
            if ((uint)slot < (uint)costs.Length) costs[slot] = Math.Max(0.0, costs[slot]) * Math.Max(0.0, 1.0 + multiplier);
        // Reconstruction (27 October 2026, owner finding 78): a windup's cost is spent at its cast frame, so the stat block keeps the per-slot
        // costs it was priced with until then (Entity.HeldManaCosts), whatever the cast's hooks changed at the press.
        if (hero.HeldManaCosts is { } held)
            for (var slot = 0; slot < costs.Length && slot < held.Count; slot++) costs[slot] = held[slot];
        if (hero.UnlimitedResource) Array.Fill(costs, 0.0);

        return new HeroStatsReplicationSnapshot(
            hero.NetId, effective, baseStats.AttackDamage,
            hero.AttackDamage - baseStats.AttackDamage, unchecked((int)actionState), targetable,
            castBits1, castBits2, hero.Health, hero.Mana, hero.Level, hero.Gold,
            hero.Experience, costs);
    }

    private void ApplyLoadoutContribution(Entity hero, HeroStats stats)
    {
        hero.LoadoutMasteries = stats.MasteryContribution;
        // CombatTick already has the shipped, distinct item GP10 path. Keep
        // page/rune gold here while removing catalog item GP10 so a purchased
        // item is not paid twice through the aggregate stat snapshot. Batch 149 step 5: the gold items' income is
        // their window buffs' own BBIncStat IncFlatGoldPer10Mod rows (no shipped item record carries
        // FlatGoldPer10Mod — only runes carry rFlatGoldPer10Mod — so those rows are the whole income) and CombatTick
        // pays those from the live buff payloads too, so they leave this figure the same way the catalog rows do.
        var itemModifiers = Inventory.AggregateModifiers(_itemCatalog, hero.Inventory);
        var itemGoldPer10 = itemModifiers.TryGetValue("FlatGoldPer10Mod", out var flatGold)
            ? flatGold : 0.0;
        if (itemModifiers.TryGetValue("rFlatGoldPer10Mod", out var growthGold))
            itemGoldPer10 += growthGold;
        var scriptGoldPer10 = hero.Buffs.Sum(buff => buff.Payload.GoldPer10);
        hero.LoadoutGoldPer10 = stats.GoldPer10Mod - itemGoldPer10 - scriptGoldPer10;
        hero.LoadoutExperiencePercent = stats.ExperiencePercent;
        hero.LoadoutRespawnTimePercent = stats.TimeDeadPercent;
    }

    public HeroStatsReplicationSnapshot Push(Entity hero)
    {
        EnsureHero(hero);
        var snapshot = Snapshot(hero);
        PushItemTooltips(hero);
        // The block carries the hero's gold, so the client holds it: a later change of the whole value is the only
        // thing that still needs a delta (CombatService.ReplicateGold).
        hero.MarkReplicatedGold();
        _transport.Push(snapshot);
        return snapshot;
    }

    /// <summary>Republish spellbook tooltip variables, including slot-only item changes.</summary>
    public void PushItemTooltips(Entity hero)
    {
        EnsureHero(hero);
        var entries = BuildItemTooltipEntries(hero);
        if (entries.Count > 0) _transport.PushItemTooltips(hero.NetId, entries);
    }

    /// <summary>Builds script-owned champion and item values for live/reconnect delivery.</summary>
    public IReadOnlyList<ItemTooltipEntry> BuildItemTooltipEntries(Entity hero)
    {
        EnsureHero(hero);
        // One row per spellbook slot, every one of them a script's own BBSetSpellToolTipVar write
        // (Entity.ScriptSpellTooltips). Batch 149 step 5 took the last hand-written stand-ins with the scripts that
        // write the same rows — the tear trio (Items/3003, 3004, 3070) and Rod of Ages (Items/3027) — so nothing
        // claims a slot a script has not written.
        var entries = new SortedDictionary<int, ItemTooltipEntry>();
        foreach (var (slot, values) in hero.ScriptSpellTooltips)
            entries[slot] = new ItemTooltipEntry(slot, values[0], values[1], values[2]);
        return entries.Values.ToArray();
    }

    /// <summary>
    /// The one-second tooltip calculation of the hand-written champions is gone with them (Nidalee's and Yorick's, 19 October 2026): every
    /// champion's tooltip numbers are its character script's own <c>BBSetSpellToolTipVar</c> writes (<c>Entity.ScriptSpellTooltips</c>), published
    /// when a value changes. The entry point stays for the production tick and its checks.
    /// </summary>
    public void TickSpellTooltips(Entity hero, double dt)
    {
        EnsureHero(hero);
        if (!double.IsFinite(dt) || dt < 0.0) throw new ArgumentOutOfRangeException(nameof(dt));
    }

    private static bool IsHardControlled(BuffState buff) =>
        buff.DisablesActions;

    // The replicated targetable bit is cleared by a stasis. A script's own invulnerability (Kayle's Intervention) leaves the unit
    // targetable: the shipped script sets no SetTargetable, only SetInvulnerable.
    private static bool IsInvulnerable(Entity unit) => unit.AnyBuffOrStatus(buff =>
        (buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable) && !buff.Payload.ActionsAllowed);

    private static void EnsureHero(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (hero.UnitType != UnitType.Hero)
            throw new ArgumentException("hero stats require a hero entity", nameof(hero));
    }
}
