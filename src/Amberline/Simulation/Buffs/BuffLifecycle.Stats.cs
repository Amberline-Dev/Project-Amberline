using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    /// <summary>
    /// Rebuilds movement from the active payload set. NPCs retain their first
    /// unmodified speed as a baseline; heroes use the loadout-aware calculator.
    /// </summary>
    public void RefreshMovementSpeed(Entity unit, bool push = true)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitType == UnitType.Hero)
        {
            _heroStats.Refresh(unit, push);
            return;
        }
        if (!IsMovementUnit(unit)) return;
        var previous = unit.Speed;
        RecomputeMovementSpeed(unit);
        if (push && previous != unit.Speed && _services.Effects.IsWireReady(unit))
            _services.Effects.SendMovementSpeed(unit);
    }

    public static bool BuffNeedsFullRefresh(BuffPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Has(BuffPayloadField.MaxHp) || payload.Has(BuffPayloadField.AttackDamage) ||
               payload.ExperiencePercent != 0.0 || payload.MpRegenMult != 0.0 || payload.MaxMpMult != 0.0 ||
               payload.Has(BuffPayloadField.Armor) || payload.Has(BuffPayloadField.SpellBlock) ||
               payload.Has(BuffPayloadField.AttackSpeedMod) || payload.Has(BuffPayloadField.CritChance) ||
               payload.Has(BuffPayloadField.CritDamage) || payload.Has(BuffPayloadField.AbilityPower) ||
               payload.Has(BuffPayloadField.CooldownMod) || payload.Has(BuffPayloadField.MoveSpeedFlat) ||
               payload.Has(BuffPayloadField.MoveSpeedPercent) || payload.Has(BuffPayloadField.MoveSpeedMult) ||
               payload.Has(BuffPayloadField.AttackSpeedMult) || payload.Has(BuffPayloadField.ArmorPenFlat) ||
               payload.Has(BuffPayloadField.DodgePiercing) || payload.Has(BuffPayloadField.ArmorMult) ||
               payload.Has(BuffPayloadField.SpellBlockMult) || payload.Has(BuffPayloadField.HpRegen) ||
               payload.Has(BuffPayloadField.MpRegen) || payload.Has(BuffPayloadField.LifeSteal) ||
               payload.Has(BuffPayloadField.SpellVamp) || payload.Has(BuffPayloadField.HpRegenMult) ||
               payload.Has(BuffPayloadField.LifeStealMult) || payload.Has(BuffPayloadField.SpellVampMult) ||
               payload.Has(BuffPayloadField.CooldownModBase) ||
               payload.Has(BuffPayloadField.CooldownModPerLevel) ||
               payload.Has(BuffPayloadField.MaxManaRegenPercent) ||
               payload.Has(BuffPayloadField.MaxEnergyRegenPercent) ||
               payload.Has(BuffPayloadField.AttackRange) || payload.Has(BuffPayloadField.Dodge) ||
               payload.Has(BuffPayloadField.ArmorPenPercent) || payload.Has(BuffPayloadField.MagicPenFlat) ||
               payload.Has(BuffPayloadField.MagicPenPercent) || payload.Has(BuffPayloadField.MaxMp) ||
               payload.Has(BuffPayloadField.RespawnTimePercent) || payload.GoldPer10 != 0.0;
    }

    public static void ApplyBuffStats(Entity unit, BuffPayload payload)
    {
        if (unit.UnitType == UnitType.Hero)
        {
            unit.Armor += payload.Armor;
            unit.SpellBlock += payload.SpellBlock;
        }
        else
        {
            RecomputeNpcResistances(unit, addedPayload: payload);
            RecomputeNpcPenetration(unit, addedPayload: payload);
            unit.AttackDamage += payload.AttackDamage;
            // Batch 694: a buff's IncPercentCooldownMod reaches a unit that is no hero (heroes take it in HeroStatCalculator); its slot casts read it.
            unit.CooldownMod += payload.CooldownMod;
            unit.CritMultiplier += payload.CritDamage;
            ApplyNpcMaxHealth(unit, payload.MaxHp, payload.NonHealingMaxHp);
        }
        if (unit.UnitType == UnitType.Hero)
        {
            var speedFactor = Math.Max(0.0, 1.0 + payload.MoveSpeedPercent) *
                              Math.Max(0.0, 1.0 + payload.MoveSpeedMult);
            unit.Speed *= speedFactor;
        }
        if (unit.UnitType != UnitType.Hero)
        {
            if (ChangesAttackSpeed(payload))
            {
                EnsureNpcAttackTimingBaseline(unit);
                RecomputeNpcAttackTiming(unit, addedPayload: payload);
            }
            return;
        }
        var attackSpeedFactor = AttackSpeedFactor(payload);
        unit.AttackSpeedMod *= attackSpeedFactor;
        if (attackSpeedFactor > 0.0)
        {
            unit.AttackPeriod /= attackSpeedFactor;
            unit.AttackWindup /= attackSpeedFactor;
        }
    }

    public static void ReverseBuffStats(Entity unit, BuffPayload payload)
    {
        if (unit.UnitType != UnitType.Hero)
        {
            RecomputeNpcResistances(unit, removedPayload: payload);
            RecomputeNpcPenetration(unit, removedPayload: payload);
            unit.AttackDamage -= payload.AttackDamage;
            unit.CooldownMod -= payload.CooldownMod;
            unit.CritMultiplier -= payload.CritDamage;
            ApplyNpcMaxHealth(unit, -payload.MaxHp, -payload.NonHealingMaxHp);
        }
        if (unit.UnitType == UnitType.Hero)
        {
            unit.Armor -= payload.Armor;
            unit.SpellBlock -= payload.SpellBlock;
            var speedFactor = Math.Max(0.0, 1.0 + payload.MoveSpeedPercent) *
                              Math.Max(0.0, 1.0 + payload.MoveSpeedMult);
            if (speedFactor > 0.0) unit.Speed /= speedFactor;
        }
        if (unit.UnitType != UnitType.Hero)
        {
            if (ChangesAttackSpeed(payload))
                RecomputeNpcAttackTiming(unit, removedPayload: payload);
            return;
        }
        var attackSpeedFactor = AttackSpeedFactor(payload);
        if (attackSpeedFactor > 0.0)
        {
            unit.AttackSpeedMod /= attackSpeedFactor;
            unit.AttackPeriod *= attackSpeedFactor;
            unit.AttackWindup *= attackSpeedFactor;
        }
    }

    // One more remaining-fraction factor. A lone source keeps its exact value: 1 - (1 - p) is not p in binary floating point, and a
    // turret's 0.2 must stay the 0.2 its sample wrote before batch 467.
    private static double ComposePercent(double percent, double added)
    {
        added = Math.Clamp(added, 0.0, 1.0);
        return added == 0.0 ? percent : percent == 0.0 ? added : 1.0 - (1.0 - percent) * (1.0 - added);
    }

    /// <summary>
    /// The part of a unit's penetration its buffs do not give it (items, runes, masteries, the script samples): the flat amounts less the
    /// buffs', each buff's percent factor divided back out of the remaining fraction. Batch 467: a clone copies this part only
    /// (SharedCloneService). Its own copies of the owner's items put their buffs on it again (BbItemModule.IsPetHolder: Items/3035's
    /// OnActivate adds LastWhisper to the clone), which now count on a non-hero, so the owner's would count twice.
    /// </summary>
    public static (double ArmorFlat, double ArmorPercent, double MagicFlat, double MagicPercent) PenetrationOutsideBuffs(Entity unit)
    {
        var (armorFlat, armorPercent, magicFlat, magicPercent) =
            (unit.ArmorPenetrationFlat, unit.ArmorPenetrationPercent, unit.MagicPenetrationFlat, unit.MagicPenetrationPercent);
        foreach (var buff in unit.Buffs)
        {
            armorFlat -= buff.Payload.ArmorPenFlat;
            magicFlat -= buff.Payload.MagicPenFlat;
            armorPercent = Without(armorPercent, buff.Payload.ArmorPenPercent);
            magicPercent = Without(magicPercent, buff.Payload.MagicPenPercent);
        }
        return (armorFlat, armorPercent, magicFlat, magicPercent);

        // A full (1.0) factor cannot be divided back out; no shipped buff gives 100%.
        static double Without(double percent, double removed)
        {
            removed = Math.Clamp(removed, 0.0, 1.0);
            return removed is 0.0 or 1.0 ? percent : 1.0 - (1.0 - percent) / (1.0 - removed);
        }
    }

    private static bool IsMovementUnit(Entity unit) =>
        unit.UnitType is UnitType.Minion or UnitType.Monster;

    private static bool ChangesMovementSpeed(BuffPayload payload) =>
        payload.MoveSpeedFlat != 0.0 || payload.MoveSpeedPercent != 0.0 ||
        payload.MoveSpeedMult != 0.0;

    private static void RecomputeMovementSpeed(Entity unit)
    {
        if (!IsMovementUnit(unit)) return;
        var baseline = unit.MovementSpeedBaseline ??= (double.IsFinite(unit.Speed)
            ? Math.Max(0.0, unit.Speed) : 0.0);
        unit.Speed = MovementSpeedCalculator.Calculate(
            baseline, 0.0, 0.0, 0.0,
            unit.Buffs.Select(buff => buff.Payload),
            applyHeroSoftCaps: false);
    }

    private static BuffPayload ScalePayload(BuffPayload source, int stacks)
    {
        var result = source.Clone();
        CopyScaled(source, result, BuffPayloadField.Armor, source.Armor, value => result.Armor = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ArmorMult, source.ArmorMult, value => result.ArmorMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ArmorPenFlat, source.ArmorPenFlat, value => result.ArmorPenFlat = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ArmorPenPercent, source.ArmorPenPercent, value => result.ArmorPenPercent = value, stacks);
        CopyScaled(source, result, BuffPayloadField.AttackDamage, source.AttackDamage, value => result.AttackDamage = value, stacks);
        CopyScaled(source, result, BuffPayloadField.AttackSpeedMod, source.AttackSpeedMod, value => result.AttackSpeedMod = value, stacks);
        CopyScaled(source, result, BuffPayloadField.AttackSpeedMult, source.AttackSpeedMult, value => result.AttackSpeedMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.AbilityPower, source.AbilityPower, value => result.AbilityPower = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CritChance, source.CritChance, value => result.CritChance = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CritDamage, source.CritDamage, value => result.CritDamage = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CooldownMod, source.CooldownMod, value => result.CooldownMod = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CooldownModBase, source.CooldownModBase, value => result.CooldownModBase = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CooldownModPerLevel, source.CooldownModPerLevel, value => result.CooldownModPerLevel = value, stacks);
        CopyScaled(source, result, BuffPayloadField.DamagePerTick, source.DamagePerTick, value => result.DamagePerTick = value, stacks);
        CopyScaled(source, result, BuffPayloadField.DamageShield, source.DamageShield, value => result.DamageShield = value, stacks);
        CopyScaled(source, result, BuffPayloadField.DeceiveCritBonus, source.DeceiveCritBonus, value => result.DeceiveCritBonus = value, stacks);
        CopyScaled(source, result, BuffPayloadField.DodgePiercing, source.DodgePiercing, value => result.DodgePiercing = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HealingReceivedMult, source.HealingReceivedMult, value => result.HealingReceivedMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HpPerTick, source.HpPerTick, value => result.HpPerTick = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HpPercentPerTick, source.HpPercentPerTick, value => result.HpPercentPerTick = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HpRegen, source.HpRegen, value => result.HpRegen = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HpRegenMult, source.HpRegenMult, value => result.HpRegenMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ItemSlowPercent, source.ItemSlowPercent, value => result.ItemSlowPercent = value, stacks);
        CopyScaled(source, result, BuffPayloadField.LifeSteal, source.LifeSteal, value => result.LifeSteal = value, stacks);
        CopyScaled(source, result, BuffPayloadField.LifeStealMult, source.LifeStealMult, value => result.LifeStealMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MagicPenFlat, source.MagicPenFlat, value => result.MagicPenFlat = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MagicPenPercent, source.MagicPenPercent, value => result.MagicPenPercent = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MaxHp, source.MaxHp, value => result.MaxHp = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MaxMp, source.MaxMp, value => result.MaxMp = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MaxManaRegenPercent, source.MaxManaRegenPercent, value => result.MaxManaRegenPercent = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MaxEnergyRegenPercent, source.MaxEnergyRegenPercent, value => result.MaxEnergyRegenPercent = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MissChance, source.MissChance, value => result.MissChance = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MpPerTick, source.MpPerTick, value => result.MpPerTick = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MpRegen, source.MpRegen, value => result.MpRegen = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MoveSpeedFlat, source.MoveSpeedFlat, value => result.MoveSpeedFlat = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MoveSpeedMult, source.MoveSpeedMult, value => result.MoveSpeedMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MoveSpeedPercent, source.MoveSpeedPercent, value => result.MoveSpeedPercent = value, stacks);
        CopyScaled(source, result, BuffPayloadField.Period, source.Period, value => result.Period = value, stacks);
        CopyScaled(source, result, BuffPayloadField.PeriodElapsed, source.PeriodElapsed, value => result.PeriodElapsed = value, stacks);
        CopyScaled(source, result, BuffPayloadField.Reflect, source.Reflect, value => result.Reflect = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ReflectScale, source.ReflectScale, value => result.ReflectScale = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ReviveHp, source.ReviveHp, value => result.ReviveHp = value, stacks);
        CopyScaled(source, result, BuffPayloadField.ReviveMp, source.ReviveMp, value => result.ReviveMp = value, stacks);
        CopyScaled(source, result, BuffPayloadField.SpellBlock, source.SpellBlock, value => result.SpellBlock = value, stacks);
        CopyScaled(source, result, BuffPayloadField.SpellBlockMult, source.SpellBlockMult, value => result.SpellBlockMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.SpellVamp, source.SpellVamp, value => result.SpellVamp = value, stacks);
        CopyScaled(source, result, BuffPayloadField.SpellVampMult, source.SpellVampMult, value => result.SpellVampMult = value, stacks);
        CopyScaled(source, result, BuffPayloadField.SpellbladeBonus, source.SpellbladeBonus, value => result.SpellbladeBonus = value, stacks);
        CopyScaled(source, result, BuffPayloadField.SpellbladeDelay, source.SpellbladeDelay, value => result.SpellbladeDelay = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CrippleAge, source.CrippleAge, value => result.CrippleAge = value, stacks);
        CopyScaled(source, result, BuffPayloadField.CrippleReduction, source.CrippleReduction, value => result.CrippleReduction = value, stacks);
        CopyScaled(source, result, BuffPayloadField.BushwhackAge, source.BushwhackAge, value => result.BushwhackAge = value, stacks);
        CopyScaled(source, result, BuffPayloadField.BushwhackDamage, source.BushwhackDamage, value => result.BushwhackDamage = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HallucinationDamageDealt, source.HallucinationDamageDealt, value => result.HallucinationDamageDealt = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HallucinationDamageTaken, source.HallucinationDamageTaken, value => result.HallucinationDamageTaken = value, stacks);
        CopyScaled(source, result, BuffPayloadField.HallucinationExplosion, source.HallucinationExplosion, value => result.HallucinationExplosion = value, stacks);
        CopyScaled(source, result, BuffPayloadField.MagicShield, source.MagicShield, value => result.MagicShield = value, stacks);
        CopyScaled(source, result, BuffPayloadField.NextWander, source.NextWander, value => result.NextWander = value, stacks);
        CopyScaled(source, result, BuffPayloadField.NextShot, source.NextShot, value => result.NextShot = value, stacks);
        CopyScaled(source, result, BuffPayloadField.QRatio, source.QRatio, value => result.QRatio = value, stacks);
        CopyScaled(source, result, BuffPayloadField.TooltipValue1, source.TooltipValue1, value => result.TooltipValue1 = value, stacks);
        CopyScaled(source, result, BuffPayloadField.TooltipValue2, source.TooltipValue2, value => result.TooltipValue2 = value, stacks);
        CopyScaled(source, result, BuffPayloadField.TooltipValue3, source.TooltipValue3, value => result.TooltipValue3 = value, stacks);
        CopyScaled(source, result, BuffPayloadField.AttackRange, source.AttackRange, value => result.AttackRange = value, stacks);
        CopyScaled(source, result, BuffPayloadField.Dodge, source.Dodge, value => result.Dodge = value, stacks);
        CopyScaled(source, result, BuffPayloadField.FlatPhysicalReduction, source.FlatPhysicalReduction, value => result.FlatPhysicalReduction = value, stacks);
        CopyScaled(source, result, BuffPayloadField.FlatMagicReduction, source.FlatMagicReduction, value => result.FlatMagicReduction = value, stacks);
        CopyScaled(source, result, BuffPayloadField.PercentPhysicalReduction, source.PercentPhysicalReduction, value => result.PercentPhysicalReduction = value, stacks);
        CopyScaled(source, result, BuffPayloadField.PercentMagicReduction, source.PercentMagicReduction, value => result.PercentMagicReduction = value, stacks);
        CopyScaled(source, result, BuffPayloadField.BubbleRadiusFlat, source.BubbleRadiusFlat, value => result.BubbleRadiusFlat = value, stacks);
        CopyScaled(source, result, BuffPayloadField.BubbleRadiusPercent, source.BubbleRadiusPercent, value => result.BubbleRadiusPercent = value, stacks);
        result.NonHealingMaxHp = source.NonHealingMaxHp * stacks;
        result.ExperiencePercent = source.ExperiencePercent * stacks;
        result.AcquisitionRange = source.AcquisitionRange * stacks;
        result.ScaleSkinCoef = source.ScaleSkinCoef * stacks;
        result.MpRegenMult = source.MpRegenMult * stacks;
        result.MaxMpMult = source.MaxMpMult * stacks;
        result.Stacks = stacks;
        return result;
    }

    private static void CopyScaled(BuffPayload source, BuffPayload destination,
        BuffPayloadField field, double value, Action<double> assign, int stacks)
    {
        if (!source.Has(field)) return;
        assign(value * stacks);
        destination.MarkPresent(field);
    }
}
