using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Spells;

/// <summary>
/// The stock owner callback of the can-use check (the owner's can-use callback), for one
/// spell of one unit. A spell without <c>SpellData*CanCastWhileDisabled</c> is disabled while the caster is suppressed, cannot cast
/// (stunned, silenced, feared, asleep, in Zhonya's stasis), is feared, asleep or taunted (unless the spell carries
/// SPELL_AffectUseable, 0x8000000); <c>CantCastWhileRooted</c> spells need a unit that can move; a suppressed caster is disabled
/// unless <c>CannotBeSuppressed</c>. A root or net alone (CanMove only) never blocks an ordinary spell. The CharacterState bits come
/// from the shipped control scripts: Stun and Suppression SetStunned, Silence SetSilenced, Fear SetFeared plus CanCast off, Taunt
/// SetTaunted, Sleep SetSleep, Root SetRooted, Net SetNetted (<see cref="CharacterState"/> holds the rule, shared with the attack
/// gate and the pets). Shared by item uses and champion casts (death is a separate gate: <c>IsDisabledWhileDead</c>,
/// <c>CanOnlyCastWhileDead</c>).
/// </summary>
internal static class SpellOwnerGate
{
    /// <summary>SPELL_AffectUseable (SpellData*Flags bit 0x8000000): a taunted caster may still use it.</summary>
    private const long AffectUseable = 0x8000000;

    /// <summary>
    /// Whether <paramref name="spell"/> (null: no spell record, every flag takes the client default false) may be used by
    /// <paramref name="hero"/> in its present control state. <paramref name="forcedMoveClearsCanMove"/> adds the forced-move
    /// CanMove-off state (a non-Fear dash replicates CanMove off, playtest B3 #10) to the root test; champion casts leave it out
    /// because a cast requested during the hero's own dash is queued until the landing (owner decision B3 #10).
    /// </summary>
    public static bool CanUse(Entity hero, SpellData? spell, bool forcedMoveClearsCanMove = false)
    {
        if (InStasis(hero)) return false;
        // Stasis (ZhonyasRingShield SetStunned), Guardian Angel's revival and ActionDisabled payloads are inside the rule.
        var state = CharacterState.Of(hero);
        bool suppressed = state.Suppressed, cannotCast = !state.CanCast, cannotMove = !state.CanMove;
        bool feared = state.Feared, asleep = state.Asleep, taunted = state.Taunted;
        if (forcedMoveClearsCanMove && hero.ForcedMove is { Completed: false, Effect: not "Fear" }) cannotMove = true;
        if (spell is null || !spell.RawFlag("SpellData*CanCastWhileDisabled"))
        {
            var affectUseable = spell is not null &&
                spell.Raw.TryGetValue(InibinReader.KeyHash("SpellData*Flags"), out var flags) &&
                flags.IsNumeric && ((long)flags.AsDouble() & AffectUseable) != 0;
            if (suppressed || cannotCast || feared || asleep || taunted && !affectUseable) return false;
        }
        if (cannotMove && spell is not null && spell.RawFlag("SpellData*CantCastWhileRooted")) return false;
        if (suppressed && (spell is null || !spell.RawFlag("SpellData*CannotBeSuppressed"))) return false;
        return true;
    }

    /// <summary>
    /// NOT RETAIL: owner decision, 26 October 2026 ("Block in stasis"): no spell and no item active is usable in Zhonya's stasis (the
    /// shipped buff <c>ZhonyasRingShield</c>). Retail's shared OnCanUseSpell lets a CanCastWhileDisabled spell through the stasis's
    /// SetStunned (Remove Scurvy, Undying Rage, Ferocious Howl, Quicksilver Sash), because Zhonya's script seals only the summoner slots.
    /// </summary>
    public static bool InStasis(Entity hero) => hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield");
}
