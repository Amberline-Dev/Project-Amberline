using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The power-resource natives of the wave 2 engine bundle (W27b, 19 October 2026; the new-champion wave plan, items E05 to E24): the regeneration and
/// pool modifiers of a stats hook, and the cost multipliers of the champion's spell slots. The rest of the bundle is in
/// <c>BbNativeHandlers.Cosmetics.cs</c> (client-only presentation) and <c>BbNativeHandlers.UnitState.cs</c> (unit and spell state);
/// <c>Wave2Bound</c> and <c>ExecuteWave2</c>, which dispatch all of them, are in this file.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    private static readonly HashSet<string> Wave2Bound = new(StringComparer.Ordinal)
    {
        "BBIncFlatPARRegenMod", "BBIncPercentPARRegenMod", "BBIncFlatPARPoolMod", "BBIncPercentPARPoolMod",
        "BBSkipNextAutoAttack",
        "BBSetPARMultiplicativeCostInc", "BBGetPARMultiplicativeCostInc",
        "BBSetCameraPosition", "BBPauseAnimation",
        "BBFadeInColorFadeEffect", "BBFadeOutColorFadeEffect", "BBSetVoiceOverride", "BBResetVoiceOverride",
        "BBSetPARColorOverride", "BBClearPARColorOverride",
        "BBGetCastSpellDragEndPos", "BBTestUnitAttributeFlag", "BBIncSpellLevel",
    };

    private bool ExecuteWave2(BbCall call)
    {
        switch (call.Function)
        {
            case "BBIncFlatPARRegenMod": IncParRegen(call, percent: false); return true;
            case "BBIncPercentPARRegenMod": IncParRegen(call, percent: true); return true;
            case "BBIncFlatPARPoolMod": IncParPool(call, percent: false); return true;
            case "BBIncPercentPARPoolMod": IncParPool(call, percent: true); return true;
            case "BBSkipNextAutoAttack": SkipNextAutoAttack(call); return true;
            case "BBSetPARMultiplicativeCostInc": SetParMultiplicativeCostInc(call); return true;
            case "BBGetPARMultiplicativeCostInc": GetParMultiplicativeCostInc(call); return true;
            case "BBSetCameraPosition": SetCameraPosition(call); return true;
            case "BBPauseAnimation": PauseAnimation(call); return true;
            case "BBFadeInColorFadeEffect": ColorFade(call, fadeIn: true); return true;
            case "BBFadeOutColorFadeEffect": ColorFade(call, fadeIn: false); return true;
            case "BBSetVoiceOverride": SetVoiceOverride(call, reset: false); return true;
            case "BBResetVoiceOverride": SetVoiceOverride(call, reset: true); return true;
            case "BBSetPARColorOverride": SetParColorOverride(call); return true;
            case "BBClearPARColorOverride": ClearParColorOverride(call); return true;
            case "BBGetCastSpellDragEndPos": GetCastSpellDragEndPos(call); return true;
            case "BBTestUnitAttributeFlag": TestUnitAttributeFlag(call); return true;
            case "BBIncSpellLevel": IncSpellLevel(call); return true;
            default: return false;
        }
    }

    /// <summary>
    /// <c>BBSetPARMultiplicativeCostInc</c> and <c>BBGetPARMultiplicativeCostInc</c>
    /// (the same slot parser as <c>BBSetPARCostInc</c> (<c>SpellSlotOwner</c>, <c>SlotType</c>, <c>SpellSlot</c>, and for the
    /// setter <c>Cost</c>, for both an optional <c>PARType</c>, default mana). The setter stores <c>Cost</c> in the spell instance's multiplicative cost field
    /// (the second float of the slot record, 0 on a fresh slot) when the owner's resource is <c>PARType</c>, a set and not an add; the getter pushes that
    /// float into <c>IncCostVar</c> (0 when the owner's resource is another one). A slot out of range or an owner of another resource only alerts.
    /// Cassiopeia's Deadly Cadence lowers all four slots by ten percent per cast (it reads the value, adds -0.1 and sets it back), Xerath's Arcane
    /// Barrage sets -1 on the ultimate (a free recast). Only champion spell slots are bound, like <c>BBSetPARCostInc</c>.
    /// </summary>
    private void SetParMultiplicativeCostInc(BbCall call)
    {
        if (Unit(call, "SpellSlotOwnerVar") is not { } owner) return;
        var slot = ChampionCostSlot(call, "BBSetPARMultiplicativeCostInc", out var parType);
        if (slot < 0 || _host.PowerType(owner) != parType) return;
        var multiplier = Sum(call, "Cost");
        if (multiplier == 0.0) owner.SpellParCostMultipliers.Remove(slot);
        else owner.SpellParCostMultipliers[slot] = multiplier;
        if (owner.UnitType == UnitType.Hero) Spells.SendChampionStats(owner);
    }

    private void GetParMultiplicativeCostInc(BbCall call)
    {
        if (Unit(call, "SpellSlotOwnerVar") is not { } owner) return;
        var slot = ChampionCostSlot(call, "BBGetPARMultiplicativeCostInc", out var parType);
        if (slot < 0) return;
        StoreOut(call, "IncCost", _host.PowerType(owner) == parType && owner.SpellParCostMultipliers.TryGetValue(slot, out var multiplier) ? multiplier : 0.0);
    }

    /// <summary>The champion spellbook slot (0-3) a cost native names, or -1 when the client alerts "slot was out of range"; <paramref name="parType"/> is its <c>PARType</c>.</summary>
    private int ChampionCostSlot(BbCall call, string function, out BbParType parType)
    {
        var type = (BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0);
        var slot = (int)Sum(call, "SpellSlot");
        parType = (BbParType)(int)(call.Value("PARType") as double? ?? 0.0);
        if (type is BbSlotType.InventorySlots or BbSlotType.ExtraSlots)
            throw BbSpellHost.Unbound($"{function} on an {type} slot", "W27b");
        return (uint)slot < SpellSystem.ChampionSlotCount ? slot : -1;
    }

    /// <summary>
    /// <c>BBIncFlatPARRegenMod</c> and <c>BBIncPercentPARRegenMod</c>, both <c>IncPARHelper</c>: <c>Target</c>, <c>Delta</c> and <c>PARType</c>
    /// (optional, default 0, mana). When the unit's resource type is
    /// <c>PARType</c> the helper adds <c>Delta</c> to the unit's flat or percent regeneration modifier of that pool (it logs and does nothing for a
    /// unit of another resource). Singed's Insanity Potion (flat, per five seconds) and Veigar's Equilibrium (percent, from the mana he lacks) call
    /// them from <c>BuffOnUpdateStats</c>, so the stats sample collects them into the buff payload like the <c>BBIncStat</c> accessors; a call from
    /// anywhere else is refused.
    /// </summary>
    private void IncParRegen(BbCall call, bool percent)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        var parType = call.Value("PARType") is double named ? named : (double)BbParType.PAR_MANA;
        if (_host.PowerType(target) != (BbParType)(int)parType) return;
        if (!_host.HasFrame || _host.Frame.Stats is not { } collector || !ReferenceEquals(target, _host.Frame.Owner))
            throw BbSpellHost.Unbound($"{call.Function} outside its holder's BuffOnUpdateStats", "W27b");
        collector.AddParRegen(Sum(call, "Delta"), percent);
    }

    /// <summary>
    /// <c>BBIncFlatPARPoolMod</c> and <c>BBIncPercentPARPoolMod</c>: the pool half of the same
    /// <c>IncPARHelper</c> as the regeneration accessors above, with the same parameters and the same rules — the delta joins the unit's flat or
    /// percent pool modifier of that kind when the unit's resource is <c>PARType</c>, and a unit of another resource is left alone. Rod of Ages'
    /// ageing (Items/3027.luaobj's <c>UpdateSelfBuffStats</c> of its own <c>InstanceVars.BonusMana</c>) and Spells/TearOfTheGoddessTrack.luaobj's
    /// <c>BuffOnUpdateStats</c> are the only shipped callers, so the sample collects the delta into the buff payload like the <c>BBIncStat</c>
    /// accessors; a call from anywhere else is refused. Batch 149 part 2: both names were owner-mapped but unbound, so a hook that used one raised
    /// and its whole sample was dropped — Rod of Ages then wrote no stat and no tooltip row at all.
    /// </summary>
    private void IncParPool(BbCall call, bool percent)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        var parType = call.Value("PARType") is double named ? named : (double)BbParType.PAR_MANA;
        if (_host.PowerType(target) != (BbParType)(int)parType) return;
        if (!_host.HasFrame || _host.Frame.Stats is not { } collector || !ReferenceEquals(target, _host.Frame.Owner))
            throw BbSpellHost.Unbound($"{call.Function} outside its holder's stats hook", "W27b");
        collector.AddParPool(Sum(call, "Delta"), percent);
    }
}
