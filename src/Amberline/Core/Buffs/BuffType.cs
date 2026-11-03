namespace Amberline.Core.Buffs;

/// <summary>
/// Patch-126 buff type byte values: the numbers the stock client registers for the scripts' <c>BUFF_*</c> constants
/// and compares in its buff bar (which draws the negative border for 3, 4, 6-10, 0x0B, 0x11, 0x12, 0x14-0x18). The packet
/// byte of 0xBF is this value unchanged. Until the A4 audit (18 October 2026) the table carried another build's numbering,
/// one higher from Stun up (from the Python server's packets_spell.py), which drew Stun as Invisibility, Invisibility as
/// Silence and so on.
/// </summary>
public enum BuffType : byte
{
    Internal = 0,
    Aura = 1,
    CombatEnhancer = 2,
    CombatDehancer = 3,
    Stun = 4,
    Invisibility = 5,
    Silence = 6,
    Taunt = 7,
    Polymorph = 8,
    Slow = 9,
    Snare = 10,
    /// <summary>The scripts' <c>BUFF_Damage</c>.</summary>
    DamageOverTime = 11,
    Heal = 12,
    Haste = 13,
    SpellImmunity = 14,
    PhysicalImmunity = 15,
    Invulnerability = 16,
    Sleep = 17,
    NearSight = 18,
    Frenzy = 19,
    Fear = 20,
    Net = 21,
    Poison = 22,
    Suppression = 23,
    Blind = 24,
    AmmoStack = 25,
}
