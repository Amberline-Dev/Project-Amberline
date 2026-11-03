using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    // Admission for synchronous, non-per-caster same-name replacement groups.
    // This observes current slots; it does not reserve them across callbacks.
    public static bool CanAddNamedBuffs(Entity unit, params string[] names) => CanAddNamedBuffs(unit, 0, names);

    /// <summary>
    /// The same admission with <paramref name="freeableSlots"/> more room than the table shows: the slots an add's own
    /// activation frees as it lands (BbBuffRuntime.ActivationFreeable). The scripts never guard the table and a full one
    /// has no recovered retail outcome (the note above), so this is the hand-written ports' own reading of the capacity
    /// case — "with a full table, only the source-cleared statuses or the ready marker can make room" — asked of every
    /// script add instead of the Guardian Angel revive alone.
    /// </summary>
    public static bool CanAddNamedBuffs(Entity unit, int freeableSlots, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(names);
        var free = MaxBuffSlots - unit.Buffs
            .Where(buff => buff.Slot is >= 0 and < MaxBuffSlots)
            .Select(buff => buff.Slot).Distinct().Count() + Math.Max(0, freeableSlots);
        return names.Distinct(StringComparer.Ordinal)
            .Count(name => unit.Buffs.FirstOrDefault(buff => buff.Name == name) is not
                { Slot: >= 0 and < MaxBuffSlots }) <= free;
    }

    /// <summary>
    /// Admission for one new <paramref name="name"/> buff through <see cref="AddBuff"/>: the add of a hostile control to a hero with Boots of
    /// Mobility starts BootsOfMobilityDebuff first (the same condition as in <see cref="AddBuff"/>), so both names need a slot. Without this
    /// the penalty took the last slot and the add itself then evicted the buff with the least time left.
    /// </summary>
    public bool CanAdd(Entity unit, string name, Entity? caster, BuffType type, int freeableSlots = 0) =>
        unit.UnitType == UnitType.Hero && caster is not null && caster.Team != unit.Team && IsMobilityControl(type) &&
        _services.Items.HasItem(unit, 3117)
            ? CanAddNamedBuffs(unit, freeableSlots, name, "BootsOfMobilityDebuff")
            : CanAddNamedBuffs(unit, freeableSlots, name);

    // A non-null caster matches AddBuff/AddStackingBuff with PerCaster=true;
    // null matches ordinary same-name replacement. This also admits mixed
    // mark-plus-shared-visual groups without treating another caster's mark
    // as a reusable slot.
    public static bool CanAddScopedBuffs(Entity unit,
        params (string Name, uint? PerCasterNetId)[] names) => CanAddScopedBuffs(unit, 0, names);

    /// <summary>The same admission with <paramref name="freeableSlots"/> more room than the table shows (see
    /// <see cref="CanAddNamedBuffs(Entity, int, string[])"/>).</summary>
    public static bool CanAddScopedBuffs(Entity unit, int freeableSlots,
        params (string Name, uint? PerCasterNetId)[] names)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(names);
        var free = MaxBuffSlots - unit.Buffs
            .Where(buff => buff.Slot is >= 0 and < MaxBuffSlots)
            .Select(buff => buff.Slot).Distinct().Count() + Math.Max(0, freeableSlots);
        return names.Distinct().Count(name => unit.Buffs.FirstOrDefault(buff =>
            buff.Name == name.Name &&
            (name.PerCasterNetId is null || buff.CasterNetId == name.PerCasterNetId)) is not
                { Slot: >= 0 and < MaxBuffSlots }) <= free;
    }

    /// <summary>
    /// Admission for a group of adds that contains overlapping applications: every application takes a fresh slot, whatever the
    /// table already holds under the name; the named buffs (same-name replacement groups) are counted as CanAddNamedBuffs does.
    /// Slots of buffs still being removed count as used, and a hero with Boots of Mobility and no penalty reserves the slot the
    /// penalty may take before the buff itself is placed.
    /// </summary>
    public bool CanAddOverlapping(Entity unit, int applications = 1, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(names);
        var used = unit.Buffs.SelectMany(buff => new[] { buff.Slot, buff.WireSlot })
            .Where(slot => slot is >= 0 and < MaxBuffSlots).ToHashSet();
        foreach (var (retiringUnit, retiringSlot) in _retiringSlots)
            if (retiringUnit == unit.NetId) used.Add(retiringSlot);
        return CanAddGroup(unit, applications, names, MaxBuffSlots - used.Count);
    }

    /// <summary>
    /// The same admission with the room the caller counts: the shared half of <see cref="CanAddOverlapping"/>, so a boundary that
    /// has no lifecycle of its own (the script engine's per-hook group, <c>ISpellBuffService.CanAddOverlapping</c>) asks the same
    /// question the hand-written ports asked. The room is <see cref="MaxBuffSlots"/> less the live slots.
    /// </summary>
    public static bool CanAddGroup(Entity unit, int applications = 1, params string[] names) =>
        CanAddGroup(unit, applications, names, FreeBuffSlots(unit));

    /// <summary>The slots of <paramref name="unit"/>'s table nothing holds.</summary>
    public static int FreeBuffSlots(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        // Batch 399: the wire slots of overlapping groups count as used with the slots the instances own.
        return MaxBuffSlots - unit.Buffs.SelectMany(buff => new[] { buff.Slot, buff.WireSlot })
            .Where(slot => slot is >= 0 and < MaxBuffSlots).Distinct().Count();
    }

    private static bool CanAddGroup(Entity unit, int applications, string[] names, int room)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(names);
        var missing = names.Distinct(StringComparer.Ordinal)
            .Count(name => unit.Buffs.FirstOrDefault(buff => buff.Name == name) is not
                { Slot: >= 0 and < MaxBuffSlots });
        // The penalty AddBuff starts for a hostile control (BootsOfMobilityDebuff) needs a slot of its own,
        // but only a group that places a fresh instance reaches that code: a pure renewal goes through
        // AddStackingBuff and adds no penalty, so it must not reserve one (the item-slow pair renewing on a
        // full table, items-live-onhit-slow-capacity-and-mobility).
        var boots = (missing > 0 || applications > 0) && unit.UnitType == UnitType.Hero &&
                    Inventory.HasItem(unit.Inventory, 3117) &&
                    !names.Contains("BootsOfMobilityDebuff", StringComparer.Ordinal) &&
                    !unit.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff") ? 1 : 0;
        return applications + missing + boots <= room;
    }

    // The first slot that is neither held by a buff nor still being removed (its removal packet has not been sent). A name that left this unit
    // in this tick takes the slot it left instead, while that slot is free (batch 390).
    private int FirstFreeSlot(Entity unit, string name)
    {
        // Batch 399: a client slot an overlapping group holds on the wire is taken while any instance of the group lives.
        var usedSlots = unit.Buffs.SelectMany(buff => new[] { buff.Slot, buff.WireSlot }).ToHashSet();
        foreach (var (retiringUnit, retiringSlot) in _retiringSlots)
            if (retiringUnit == unit.NetId) usedSlots.Add(retiringSlot);
        // Reconstruction: retail's slot choice is not in the shipped files; the owner chose "re-use the freed slot" (batch 390). A buff of the same
        // name that left this unit in this tick gives its slot to the add while that slot is still free. Sona's Crescendo and Lux's R remove a buff
        // and re-add it in one tick, and the lowest free slot put the re-add in another slot, so one flush held a remove and an add of one copy
        // (BUF-FLUSH). Every other add takes the lowest free slot, as before.
        var left = _leftThisStep.FindLast(entry => entry.Tick == _tick && entry.Unit == unit.NetId && entry.Name == name &&
            !usedSlots.Contains(entry.Slot));
        if (left is not null) return left.Slot;
        return Enumerable.Range(0, MaxBuffSlots).FirstOrDefault(index => !usedSlots.Contains(index), -1);
    }

    /// <summary>
    /// The add-time refusals of <see cref="AddBuff"/> that no script has to agree to: Yorick's delay, the ghoul's Slow, the Shaco box,
    /// the wards, a popped Banshee's Veil and ResistantSkin against a hostile control. An add one of these refuses never takes a slot,
    /// so a caller that admits a group of adds together counts only what this leaves (the hand-written item ports' slowEligible).
    /// The <c>BuffOnAllowAdd</c> of the unit's own script buffs is not asked here: it runs those scripts, and its refusal is learned
    /// when the add itself runs.
    /// </summary>
    /// <param name="hostileInternal">An <see cref="BuffType.Internal"/> row that carries a slow (the shipped Internal_*Slow rows) is
    /// hostile control to ResistantSkin; the caller knows it from the payload it is about to place. The probe that counts a group's
    /// slots before any payload exists leaves it false, which counts such a row as taking a slot and never half-applies a group.</param>
    public bool RefusesAdd(Entity unit, BuffType type, uint casterNetId, int? maxStack = null, bool hostileInternal = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (YorickDelayRejects(unit, type, casterNetId)) return true;
        // The Slow refusal of the ghoul Logic buffs (BuffOnAllowAdd), for the adders that do not run scripts.
        if (type == BuffType.Slow && unit.Kind == EntityKind.YorickGhoul) return true;
        var caster = _world.Get(casterNetId);
        if (unit.Kind == EntityKind.ShacoBox &&
            (maxStack == 76 || (caster is not null && WorldState.IsEnemy(caster, unit) &&
                                IsEpicControl(type)))) return true;
        if (WardRejects(unit, type, casterNetId)) return true;
        if (PoppedShieldRejects(unit, casterNetId)) return true;

        var resistant = unit.Buffs.Any(buff => buff.Name is "ResistantSkin" or "ResistantSkinDragon");
        var hostileControl = maxStack == 76 || IsEpicControl(type) ||
            (type == BuffType.Internal && hostileInternal);
        var hostile = caster is not null && caster.Team != unit.Team;
        // The Worm's idle range (a hostile add from beyond 950 while none of its action timers runs) is
        // ResistantSkin's BuffOnAllowAdd (Spells/ResistantSkin.luaobj 0283-0334), which ScriptBuffRefuses asks for every add.
        return resistant && hostile && hostileControl;
    }

    // SightWard, VisionWard and WriggleLanternWard.luaobj BuffOnAllowAdd:
    // veto these hostile types before either replacement or stack renewal.
    private bool WardRejects(Entity unit, BuffType type, uint casterNetId) =>
        unit.IsWard && _world.Get(casterNetId) is { } caster && caster.Team != unit.Team &&
        (IsEpicControl(type) || type == BuffType.CombatDehancer);

    // BansheesVeil.luaobj BuffOnAllowAdd: once the shield popped (WillRemove) every buff from a hostile adder is refused, whatever its
    // type. The shield leaves at its next BuffOnUpdateStats; HeroItemState.SpellShieldPopped is that interval (TickBuffs re-reads it).
    private bool PoppedShieldRejects(Entity unit, uint casterNetId) =>
        unit.ItemState.SpellShieldPopped && _world.Get(casterNetId) is { } caster && caster.Team != unit.Team;

    // BuffOnAllowAdd of the script buffs on the unit (Black Shield, Banshee's Veil, Spell Shield, Yorick's delay, the wards ...): the retail buff manager asks
    // it for every buff it adds, whoever adds it, so the native controls (BBApplyStun and its siblings), the items and the summoner spells meet the
    // shields the scripts' own adds always met, and a shield of one champion's module reaches the adds of every other module.
    private bool ScriptBuffRefuses(Entity unit, string name, BuffType type, ref double duration, uint casterNetId, int? maxStack) =>
        !_services.Champions.AllowBuffAdd(unit, name, type, ref duration, _world.Get(casterNetId) ?? unit, maxStack);

    // YorickRADelay.luaobj BuffOnAllowAdd, for the adders that do not run scripts (the script engine's own adds ask the hook itself): while the
    // delay lasts, no hostile control or debuff gets on the unit.
    private bool YorickDelayRejects(Entity unit, BuffType type, uint casterNetId) =>
        unit.Buffs.Any(buff => buff.Name == "YorickRADelay") &&
        _world.Get(casterNetId) is { } caster && caster.Team != unit.Team &&
        type is (BuffType.Fear or BuffType.Net or BuffType.Silence or BuffType.Sleep or
            BuffType.Slow or BuffType.Snare or BuffType.Stun or BuffType.Taunt or
            BuffType.Blind or BuffType.Suppression or BuffType.CombatDehancer or BuffType.DamageOverTime);
}
