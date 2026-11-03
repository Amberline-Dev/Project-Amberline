namespace Amberline.Core.Buffs;

/// <summary>Source names whose presence has behavior even when their value is zero.</summary>
public enum BuffPayloadField : byte
{
    Armor = 0,
    ArmorMult,
    ArmorPenFlat,
    ArmorPenPercent,
    AttackDamage,
    AttackSpeedMod,
    AttackSpeedMult,
    AbilityPower,
    CritChance,
    CritDamage,
    CooldownMod,
    CooldownModBase,
    CooldownModPerLevel,
    DodgePiercing,
    HpRegen,
    HpRegenMult,
    LifeSteal,
    LifeStealMult,
    MagicPenFlat,
    MagicPenPercent,
    MaxHp,
    MaxMp,
    MaxManaRegenPercent,
    MaxEnergyRegenPercent,
    MpRegen,
    MoveSpeedFlat,
    MoveSpeedMult,
    MoveSpeedPercent,
    SpellBlock,
    SpellBlockMult,
    SpellVamp,
    SpellVampMult,
    DamageShield,
    DeceiveCritBonus,
    HealingReceivedMult,
    MissChance,
    Reflect,
    ReflectScale,
    ReviveHp,
    ReviveMp,
    ItemSlowPercent,
    SpellbladeBonus,
    SpellbladeDelay,
    BushwhackAge,
    BushwhackDamage,
    HallucinationDamageDealt,
    HallucinationDamageTaken,
    HallucinationExplosion,
    MagicShield,
    NextWander,
    NextShot,
    QRatio,
    MaxTicks,
    Period,
    CrippleAge,
    CrippleReduction,
    PeriodElapsed,
    TicksDone,
    DamagePerTick,
    HpPerTick,
    HpPercentPerTick,
    MpPerTick,
    /// <summary>Shipped client buff tooltip variable Index 1 (wire float 1).</summary>
    TooltipValue1,
    /// <summary>Shipped client buff tooltip variable Index 2 (wire float 2).</summary>
    TooltipValue2,
    /// <summary>Shipped client buff tooltip variable Index 3 (wire float 3).</summary>
    TooltipValue3,
    /// <summary>BuildingBlocks <c>IncFlatAttackRangeMod</c> from a script buff's stats hook (Kayle's Righteous Fury).</summary>
    AttackRange,
    /// <summary>BuildingBlocks <c>IncFlatDodgeMod</c> from a script buff's stats hook (Sivir's Fleet of Foot).</summary>
    Dodge,
    /// <summary><c>IncFlatPhysicalReduction</c>: flat damage taken off each physical hit (Amumu's Tantrum).</summary>
    FlatPhysicalReduction,
    /// <summary><c>IncFlatMagicReduction</c>: flat damage taken off each magic hit (the jungle Worm's WrathDebuff, negative).</summary>
    FlatMagicReduction,
    /// <summary><c>IncPercentPhysicalReduction</c>: fraction of each physical hit that is not taken (Ferocious Howl, Command).</summary>
    PercentPhysicalReduction,
    /// <summary><c>IncPercentMagicReduction</c>: fraction of each magic hit that is not taken.</summary>
    PercentMagicReduction,
    /// <summary><c>IncFlatBubbleRadiusMod</c>: units added to the unit's perception radius (the client's <c>(percent + 1) * base + flat</c>).</summary>
    BubbleRadiusFlat,
    /// <summary><c>IncPercentBubbleRadiusMod</c>: the unit's own perception radius scaled by one plus this (Bushwhack's dormant trap: -1).</summary>
    BubbleRadiusPercent,
    /// <summary><c>IncPercentRespawnTimeMod</c> from a script buff's stats hook (PositiveChampionDelta's 0.1 x T ramp).</summary>
    RespawnTimePercent,
}
