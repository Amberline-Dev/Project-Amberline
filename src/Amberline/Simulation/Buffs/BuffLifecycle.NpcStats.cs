using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    /// <summary>
    /// The pool part of a payload on a unit that is not a hero. A hero's goes through <c>HeroStatsService.Refresh</c> (a full
    /// recalculation of <c>stats.MaxHp</c>); an NPC's maximum health is its entity field, and this is where a payload moves it:
    /// <c>IncFlatHPPoolMod</c> (payload.MaxHp) and the non-healing pool of a script's <c>BBIncMaxHealth</c> (payload.NonHealingMaxHp),
    /// whose direct write for an NPC never reaches a payload. The hero's own health rule stands (HeroStatsService): the healing part
    /// of a growth raises current health while the unit lives, a shrink only clamps it into the pool. The reverse of a removal is the
    /// same call with the signs flipped.
    /// </summary>
    private static void ApplyNpcMaxHealth(Entity unit, double maxHp, double nonHealingMaxHp)
    {
        if (maxHp == 0.0 && nonHealingMaxHp == 0.0) return;
        unit.MaxHealth = Math.Max(1.0, unit.MaxHealth + maxHp + nonHealingMaxHp);
        unit.Health = unit.Alive
            ? Math.Min(unit.MaxHealth, unit.Health + Math.Max(0.0, maxHp))
            : Math.Min(unit.MaxHealth, unit.Health);
    }

    public static void IncreaseNpcBaseResistances(Entity unit, double armor, double spellBlock)
    {
        var baseline = unit.ResistanceBaseline ?? (unit.Armor, unit.SpellBlock);
        unit.ResistanceBaseline = (baseline.Armor + armor, baseline.SpellBlock + spellBlock);
        RecomputeNpcResistances(unit);
    }

    /// <summary>
    /// Adds a permanent attack-speed amount to a non-hero unit's baseline and rebuilds its timing from it (batch 159
    /// step 2: a script's <c>BBIncPermanentStat IncPermanentPercentAttackSpeedMod</c> on a non-hero; since batch 760
    /// <c>BbSpellHost.PermanentOnPet</c> passes the lane turret's <c>PersonalTurretAssistBonus</c> one as shown only). It is the same additive family a buff's own
    /// <c>IncPercentAttackSpeedMod</c> joins in <see cref="RecomputeNpcAttackTiming"/> — a percent amount on top of the
    /// unit's base attacks-per-second — kept apart here because a permanent outlives the buff that added it and is not
    /// rolled back when a buff expires. With <paramref name="shownOnly"/> (batch 760: the lane turret's assist bonus) the
    /// amount joins only <see cref="AttackTimingBaseline.ShownAttackSpeed"/>: the replicated multiplier and the wind-up,
    /// not the firing period.
    /// </summary>
    public static void IncreaseNpcBaseAttackSpeed(Entity unit, double attackSpeed, bool shownOnly = false)
    {
        EnsureNpcAttackTimingBaseline(unit);
        if (unit.AttackTimingBaseline is not { } baseline) return;
        unit.AttackTimingBaseline = shownOnly
            ? baseline with { ShownAttackSpeed = baseline.ShownAttackSpeed + attackSpeed }
            : baseline with { BaseAttackSpeed = baseline.BaseAttackSpeed + attackSpeed };
        RecomputeNpcAttackTiming(unit);
    }

    private static void RecomputeNpcResistances(Entity unit,
        BuffPayload? addedPayload = null, BuffPayload? removedPayload = null)
    {
        var baseline = unit.ResistanceBaseline ??= (unit.Armor, unit.SpellBlock);
        var armor = baseline.Armor;
        var spellBlock = baseline.SpellBlock;
        var armorPercent = 0.0;
        var spellBlockPercent = 0.0;
        foreach (var buff in unit.Buffs)
        {
            if (ReferenceEquals(buff.Payload, removedPayload)) continue;
            Add(buff.Payload);
        }
        if (addedPayload is not null && !unit.Buffs.Any(buff => ReferenceEquals(buff.Payload, addedPayload)))
            Add(addedPayload);
        // Match HeroStats: active flat components precede summed percentages.
        unit.Armor = armor * Math.Max(0.0, 1.0 + armorPercent);
        unit.SpellBlock = spellBlock * Math.Max(0.0, 1.0 + spellBlockPercent);

        void Add(BuffPayload payload)
        {
            armor += payload.Armor;
            spellBlock += payload.SpellBlock;
            armorPercent += payload.ArmorMult;
            spellBlockPercent += payload.SpellBlockMult;
        }
    }

    /// <summary>
    /// A non-hero's armour and magic penetration: its seed (<see cref="Entity.PenetrationBaseline"/>), its character script's own sample
    /// (<see cref="Entity.CharacterScriptStats"/>) and every buff it wears, composed as the hero calculator composes them (HeroStats): flat
    /// amounts add, and a percent amount, clamped to [0, 1], multiplies the remaining fraction, as the script helper's
    /// armour and magic penetration percent setters
    /// store (1 - p) x current. Their target is whatever obj_AI_Base the unit manager finds, so a turret,
    /// minion, monster or pet takes a buff's penetration as a hero does. Batch 467: before this only a character script's sample wrote
    /// these fields on a non-hero, and OdinGuardianBuff's 65% (BuffOnUpdateStats block 5) never reached the guardian's hits.
    /// </summary>
    public static void RecomputeNpcPenetration(Entity unit, BuffPayload? addedPayload = null, BuffPayload? removedPayload = null)
    {
        var (armorFlat, armorPercent, magicFlat, magicPercent) = unit.PenetrationBaseline ??=
            (unit.ArmorPenetrationFlat, unit.ArmorPenetrationPercent, unit.MagicPenetrationFlat, unit.MagicPenetrationPercent);
        if (unit.CharacterScriptStats is { } own) Add(own);
        foreach (var buff in unit.Buffs)
            if (!ReferenceEquals(buff.Payload, removedPayload)) Add(buff.Payload);
        if (addedPayload is not null && !unit.Buffs.Any(buff => ReferenceEquals(buff.Payload, addedPayload)))
            Add(addedPayload);
        unit.ArmorPenetrationFlat = armorFlat;
        unit.ArmorPenetrationPercent = armorPercent;
        unit.MagicPenetrationFlat = magicFlat;
        unit.MagicPenetrationPercent = magicPercent;

        void Add(BuffPayload payload)
        {
            armorFlat += payload.ArmorPenFlat;
            armorPercent = ComposePercent(armorPercent, payload.ArmorPenPercent);
            magicFlat += payload.MagicPenFlat;
            magicPercent = ComposePercent(magicPercent, payload.MagicPenPercent);
        }
    }

    private static double AttackSpeedFactor(BuffPayload payload)
    {
        return Math.Max(AttackSpeedFloor, 1.0 + payload.AttackSpeedMult);
    }

    private static bool ChangesAttackSpeed(BuffPayload payload) =>
        payload.Has(BuffPayloadField.AttackSpeedMod) ||
        payload.Has(BuffPayloadField.AttackSpeedMult);

    private static void EnsureNpcAttackTimingBaseline(Entity unit)
    {
        if (unit.AttackTimingBaseline is not null) return;
        var period = double.IsFinite(unit.AttackPeriod) ? Math.Max(0.0, unit.AttackPeriod) : 0.0;
        var windup = double.IsFinite(unit.AttackWindup) ? unit.AttackWindup : 0.0;
        var fraction = period > 0.0 ? Math.Max(0.0, windup / period) : 0.0;
        var attackSpeed = double.IsFinite(unit.AttackSpeedMod) && unit.AttackSpeedMod > 0.0
            ? unit.AttackSpeedMod : 1.0;
        unit.AttackTimingBaseline = new AttackTimingBaseline(attackSpeed, period * attackSpeed, fraction);
    }

    private static void RecomputeNpcAttackTiming(Entity unit,
        BuffPayload? addedPayload = null, BuffPayload? removedPayload = null)
    {
        if (unit.AttackTimingBaseline is not { } baseline) return;

        var additive = 0.0;
        var multiplicative = 1.0;
        foreach (var buff in unit.Buffs)
        {
            if (removedPayload is not null && ReferenceEquals(buff.Payload, removedPayload)) continue;
            additive += buff.Payload.AttackSpeedMod;
            multiplicative *= Math.Max(0.0, 1.0 + buff.Payload.AttackSpeedMult);
        }
        if (addedPayload is not null && !unit.Buffs.Any(buff => ReferenceEquals(buff.Payload, addedPayload)))
        {
            additive += addedPayload.AttackSpeedMod;
            multiplicative *= Math.Max(0.0, 1.0 + addedPayload.AttackSpeedMult);
        }

        var baseAttackSpeed = double.IsFinite(baseline.BaseAttackSpeed) && baseline.BaseAttackSpeed > 0.0
            ? baseline.BaseAttackSpeed : 1.0;
        var effectiveAttackSpeed = Math.Max(AttackSpeedFloor, (baseAttackSpeed + additive) * multiplicative);
        // Reconstruction (batch 760, owner request of 12 November 2026: "Make sure the turrets display the correct 1.083 value in the HUD but
        // they keep shooting at the current pace"): the shown amount (a lane turret's assist bonus, 0 for every other unit) joins the
        // replicated multiplier as any permanent amount would, but not the firing period. The 2011 footage shows lane turrets firing at the
        // multiplier 1; what slowed the retail server is not known
        var shownAttackSpeed = Math.Max(AttackSpeedFloor,
            (baseAttackSpeed + baseline.ShownAttackSpeed + additive) * multiplicative);
        unit.AttackSpeedMod = shownAttackSpeed;
        if (baseline.BasePeriod <= 0.0 || !double.IsFinite(baseline.BasePeriod)) return;

        var castFraction = Math.Max(0.0, double.IsFinite(baseline.CastFraction) ? baseline.CastFraction : 0.0);
        unit.AttackPeriod = Math.Clamp(baseline.BasePeriod / effectiveAttackSpeed, 0.4, 5.0);
        // The stock 0x0D handler draws the cast frame after its own cast delay, the delay helper's period at the replicated
        // mAttackSpeedMod times the cast fraction, so the hit waits that long: the period at the shown multiplier.
        unit.AttackWindup = Math.Clamp(baseline.BasePeriod / shownAttackSpeed, 0.4, 5.0) * castFraction;
    }
}
