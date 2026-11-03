using System;

namespace Amberline.Simulation;

/// <summary>
/// Persistent hero-owned state the item scripts do not keep themselves. Batch 149 step 5 moved the last of
/// the hand-written counters into the shipped scripts' own tables — the Tear family's growth and reservoirs
/// are <c>CharVars.TearBonusMana</c> and <c>InstanceVars.CooldownResevoir</c>, Rod of Ages' ageing is its
/// copy's <c>InstanceVars.BonusHealth</c> / <c>BonusMana</c> / <c>BonusAbilityPower</c>, and Sword of the
/// Divine's, Ionic Spark's, Force of Nature's and Banshee's Veil's counters are their window buffs' — so
/// what is left here is the one flag no script can answer for.
/// </summary>
public sealed class HeroItemState
{
    /// <summary>
    /// A hostile spell popped a spell shield of this unit (Banshee's Veil or a script's Spell Shield) and the shield has not left yet.
    /// SpellShield.luaobj and BansheesVeil.luaobj keep the shield on the unit with WillRemove set until their BuffOnUpdateStats swaps it
    /// out at the next 0.25 s poll; in between BuffOnPreDamage sets DamageAmount to 0 and BuffOnAllowAdd refuses any buff from a hostile
    /// adder. The damage pipeline and <c>BuffLifecycle</c> read this flag (the zero, the refusal and the absent 0x68 packet), and
    /// <c>BuffLifecycle.TickBuffs</c> re-reads it each tick from the script instances, so it lasts exactly as long as WillRemove does.
    /// Set by <c>MatchGameplayHooks.ConsumeSpellShield</c>.
    /// </summary>
    public bool SpellShieldPopped { get; set; }

    /// <summary>Clone construction snapshots counters without sharing mutable state.</summary>
    public HeroItemState Clone() => (HeroItemState)MemberwiseClone();
}
