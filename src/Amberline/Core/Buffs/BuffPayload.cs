using Amberline.Core;

namespace Amberline.Core.Buffs;

/// <summary>Resource pool to which a flat regeneration payload applies.</summary>
public enum BuffResourceDomain : byte
{
    Any = 0,
    Mana,
}

/// <summary>
/// Typed status, stat, and effect data carried by one active buff.
///
/// The Python runtime used a mutable dictionary because the original spell
/// scripts mutate timers, shields, and stacks in place. These members keep the
/// same mutability while making every supported field explicit and rejecting
/// non-finite numeric state at the boundary.
/// </summary>
public sealed class BuffPayload
{
    public Amberline.Simulation.Combat.DeathRecapOrigin? DamageOrigin { get; set; }
    public bool PerCaster { get; set; }
    /// <summary>
    /// The add's <c>IsHiddenOnClient</c> (the <c>isHidden</c> byte of its 0xBF, <see cref="BuffState.HiddenOnClientByte"/>): the stock client keeps the
    /// buff, its sound and its particles, and never gives it a slot on the HUD buff bar (the bar skips an instance whose flag byte is set).
    /// Only a script's <c>BBSpellBuffAdd</c> sets it (Brand's <c>BrandPassiveSound</c>, which has no icon to show).
    /// </summary>
    public bool HiddenOnClient { get; set; }
    /// <summary>
    /// Batch 405 (Reconstruction): the 0-based slot the buff's own script toggles on (its <c>SpellToggleSlot</c> global is a slot number from
    /// one; null when the script sets none). The stock client turns that slot's glow on when this add activates, and only if the spell in the
    /// slot is a toggle spell at that moment (the client's spellbook activation handler reads the global). The sink
    /// hands the add to the delivery, which sends it behind the slot's swap of the same flush (<c>ReplicationDelivery</c>).
    /// </summary>
    public int? ToggleSlot { get; set; }
    private HashSet<BuffPayloadField> _presentFields = new();
    private double _abilityPower;
    private double _armor;
    private double _armorMult;
    private double _armorPenFlat;
    private double _armorPenPercent;
    private double _attackDamage;
    private double _attackSpeedMod;
    private double _attackSpeedMult;
    private double _cooldownMod;
    private double _cooldownModBase;
    private double _cooldownModPerLevel;
    private double _critChance;
    private double _critDamage;
    private double _damagePerTick;
    private double _damageShield;
    private double _deceiveCritBonus;
    private double _dodgePiercing;
    private double _healingReceivedMult;
    private double _hpPerTick;
    private double _hpPercentPerTick;
    private double _hpRegen;
    private double _hpRegenMult;
    private double _itemSlowPercent;
    private double _lifeSteal;
    private double _lifeStealMult;
    private double _magicPenFlat;
    private double _magicPenPercent;
    private double _maxHp;
    private double _maxMp;
    private double _maxManaRegenPercent;
    private double _maxEnergyRegenPercent;
    private double _missChance;
    private double _bubbleRadiusFlat;
    private double _bubbleRadiusPercent;
    private double _mpPerTick;
    private double _mpRegen;
    private double _moveSpeedFlat;
    private double _moveSpeedMult;
    private double _moveSpeedPercent;
    private double _period;
    private double _periodElapsed;
    private double _reflect;
    private double _reflectScale;
    private double _reviveHp;
    private double _reviveMp;
    private double _spellBlock;
    private double _spellBlockMult;
    private double _spellVamp;
    private double _spellVampMult;
    private double _spellbladeBonus;
    private double _spellbladeDelay = 1.2;
    private double _crippleAge;
    private double _crippleReduction;
    private double _bushwhackAge;
    private double _bushwhackDamage;
    private double _born;
    private double _hallucinationDamageDealt;
    private double _hallucinationDamageTaken;
    private double _hallucinationExplosion;
    private double _magicShield;
    private double _nextWander;
    private double _nextShot;
    private double _qRatio;
    private double _tooltipValue1;
    private double _tooltipValue2;
    private double _tooltipValue3;
    private double _attackRange;
    private double _dodge;
    private double _flatPhysicalReduction;
    private double _flatMagicReduction;
    private double _percentPhysicalReduction;
    private double _percentMagicReduction;
    private double _respawnTimePercent;

    public double AbilityPower { get => _abilityPower; set => _abilityPower = Mark(value, nameof(AbilityPower), BuffPayloadField.AbilityPower); }
    public double Armor { get => _armor; set => _armor = Mark(value, nameof(Armor), BuffPayloadField.Armor); }
    public double ArmorMult { get => _armorMult; set => _armorMult = Mark(value, nameof(ArmorMult), BuffPayloadField.ArmorMult); }
    public double ArmorPenFlat { get => _armorPenFlat; set => _armorPenFlat = Mark(value, nameof(ArmorPenFlat), BuffPayloadField.ArmorPenFlat); }
    public double ArmorPenPercent { get => _armorPenPercent; set => _armorPenPercent = Mark(value, nameof(ArmorPenPercent), BuffPayloadField.ArmorPenPercent); }
    public double AttackDamage { get => _attackDamage; set => _attackDamage = Mark(value, nameof(AttackDamage), BuffPayloadField.AttackDamage); }
    public double AttackSpeedMod { get => _attackSpeedMod; set => _attackSpeedMod = Mark(value, nameof(AttackSpeedMod), BuffPayloadField.AttackSpeedMod); }
    public double AttackSpeedMult { get => _attackSpeedMult; set => _attackSpeedMult = Mark(value, nameof(AttackSpeedMult), BuffPayloadField.AttackSpeedMult); }
    public double CooldownMod { get => _cooldownMod; set => _cooldownMod = Mark(value, nameof(CooldownMod), BuffPayloadField.CooldownMod); }
    public double CooldownModBase { get => _cooldownModBase; set => _cooldownModBase = Mark(value, nameof(CooldownModBase), BuffPayloadField.CooldownModBase); }
    public double CooldownModPerLevel { get => _cooldownModPerLevel; set => _cooldownModPerLevel = Mark(value, nameof(CooldownModPerLevel), BuffPayloadField.CooldownModPerLevel); }
    public double CritChance { get => _critChance; set => _critChance = Mark(value, nameof(CritChance), BuffPayloadField.CritChance); }
    public double CritDamage { get => _critDamage; set => _critDamage = Mark(value, nameof(CritDamage), BuffPayloadField.CritDamage); }
    public double DamagePerTick { get => _damagePerTick; set => _damagePerTick = Mark(value, nameof(DamagePerTick), BuffPayloadField.DamagePerTick); }
    public double DamageShield { get => _damageShield; set => _damageShield = Mark(value, nameof(DamageShield), BuffPayloadField.DamageShield); }
    public double DeceiveCritBonus { get => _deceiveCritBonus; set => _deceiveCritBonus = Mark(value, nameof(DeceiveCritBonus), BuffPayloadField.DeceiveCritBonus); }
    public double DodgePiercing { get => _dodgePiercing; set => _dodgePiercing = Mark(value, nameof(DodgePiercing), BuffPayloadField.DodgePiercing); }
    public double HealingReceivedMult { get => _healingReceivedMult; set => _healingReceivedMult = Mark(value, nameof(HealingReceivedMult), BuffPayloadField.HealingReceivedMult); }
    public double HpPerTick { get => _hpPerTick; set => _hpPerTick = Mark(value, nameof(HpPerTick), BuffPayloadField.HpPerTick); }
    public double HpPercentPerTick { get => _hpPercentPerTick; set => _hpPercentPerTick = Mark(value, nameof(HpPercentPerTick), BuffPayloadField.HpPercentPerTick); }
    public double HpRegen { get => _hpRegen; set => _hpRegen = Mark(value, nameof(HpRegen), BuffPayloadField.HpRegen); }
    public double HpRegenMult { get => _hpRegenMult; set => _hpRegenMult = Mark(value, nameof(HpRegenMult), BuffPayloadField.HpRegenMult); }
    public double ItemSlowPercent { get => _itemSlowPercent; set => _itemSlowPercent = Mark(value, nameof(ItemSlowPercent), BuffPayloadField.ItemSlowPercent); }
    public double LifeSteal { get => _lifeSteal; set => _lifeSteal = Mark(value, nameof(LifeSteal), BuffPayloadField.LifeSteal); }
    public double LifeStealMult { get => _lifeStealMult; set => _lifeStealMult = Mark(value, nameof(LifeStealMult), BuffPayloadField.LifeStealMult); }
    public double MagicPenFlat { get => _magicPenFlat; set => _magicPenFlat = Mark(value, nameof(MagicPenFlat), BuffPayloadField.MagicPenFlat); }
    public double MagicPenPercent { get => _magicPenPercent; set => _magicPenPercent = Mark(value, nameof(MagicPenPercent), BuffPayloadField.MagicPenPercent); }
    public double MaxHp { get => _maxHp; set => _maxHp = Mark(value, nameof(MaxHp), BuffPayloadField.MaxHp); }
    /// <summary>
    /// The part of <see cref="MaxHp"/> that does not raise the holder's current health when it is added (<c>BBIncMaxHealth</c> with
    /// <c>IncCurrentHealth</c> false adds to the client's <c>m_NonHealingFlatHPPoolMod</c>, the true form to <c>mFlatHPPoolMod</c>: Vladimir's Blood Gorged, Jax's
    /// equipment mastery). Not a presence-tracked column; <see cref="MaxHp"/> already holds it.
    /// </summary>
    public double NonHealingMaxHp { get; set; }
    /// <summary>IncPercentEXPBonus from a script buff (Zilean's Heightened Learning, +0.08): added to the hero's experience bonus.</summary>
    public double ExperiencePercent { get; set; }
    /// <summary>
    /// IncPercentRespawnTimeMod from a script buff (<c>Spells/PositiveChampionDelta.luaobj</c>'s 0.1 x T row): the hero's respawn
    /// timer is multiplied by 1 - this, the same field the rune and mastery rows feed (<c>HeroStats.TimeDeadPercent</c>).
    /// Batch 159 part 3: the team-size catch-up runs from that script now, so the ramp arrives here instead of through
    /// <c>TeamSizeCatchUp</c>'s hand-written reader.
    /// </summary>
    public double RespawnTimePercent
    {
        get => _respawnTimePercent;
        set => _respawnTimePercent = Mark(value, nameof(RespawnTimePercent), BuffPayloadField.RespawnTimePercent);
    }
    /// <summary>
    /// IncFlatGoldPer10Mod from a script buff (the four gold items' window buffs — AvariceBlade, PhilosophersStone,
    /// KagesLuckyPick, HeartOfGold — +5 apiece): added to the hero's gold-per-ten, which the combat tick pays.
    /// Batch 149 step 5: the hand-written distinct-item count that stood in for it is gone.
    /// </summary>
    public double GoldPer10 { get; set; }
    /// <summary>IncAcquisitionRangeMod from a script buff (Sona's Power Chord, -900): added to the range an idle hero auto-acquires targets in.</summary>
    public double AcquisitionRange { get; set; }
    /// <summary>BBIncScaleSkinCoef from a script buff's stats hook (Olaf's Ragnarok +0.1, Cho'Gath's Feast, Nasus' Fury of the Sands): added to the unit's model scale coefficient (1 + the sum of the buffs).</summary>
    public double ScaleSkinCoef { get; set; }
    /// <summary>BBIncPercentPARRegenMod from a script buff (Veigar's Equilibrium): the unit's resource regeneration is multiplied by 1 + this.</summary>
    public double MpRegenMult { get; set; }
    /// <summary>
    /// BBIncPercentPARPoolMod from a script buff's stats hook: the unit's resource pool is multiplied by 1 + this, the pool twin of
    /// <see cref="MpRegenMult"/>. Its flat half is <see cref="MaxMp"/>; no shipped script calls the percent one yet.
    /// </summary>
    public double MaxMpMult { get; set; }
    public double MaxMp { get => _maxMp; set => _maxMp = Mark(value, nameof(MaxMp), BuffPayloadField.MaxMp); }
    public double MaxManaRegenPercent { get => _maxManaRegenPercent; set => _maxManaRegenPercent = Mark(value, nameof(MaxManaRegenPercent), BuffPayloadField.MaxManaRegenPercent); }
    public double MaxEnergyRegenPercent { get => _maxEnergyRegenPercent; set => _maxEnergyRegenPercent = Mark(value, nameof(MaxEnergyRegenPercent), BuffPayloadField.MaxEnergyRegenPercent); }
    public double MissChance { get => _missChance; set => _missChance = Mark(value, nameof(MissChance), BuffPayloadField.MissChance); }
    public double MpPerTick { get => _mpPerTick; set => _mpPerTick = Mark(value, nameof(MpPerTick), BuffPayloadField.MpPerTick); }
    public double MpRegen { get => _mpRegen; set => _mpRegen = Mark(value, nameof(MpRegen), BuffPayloadField.MpRegen); }
    public double MoveSpeedFlat { get => _moveSpeedFlat; set => _moveSpeedFlat = Mark(value, nameof(MoveSpeedFlat), BuffPayloadField.MoveSpeedFlat); }
    public double MoveSpeedMult { get => _moveSpeedMult; set => _moveSpeedMult = Mark(value, nameof(MoveSpeedMult), BuffPayloadField.MoveSpeedMult); }
    public double MoveSpeedPercent { get => _moveSpeedPercent; set => _moveSpeedPercent = Mark(value, nameof(MoveSpeedPercent), BuffPayloadField.MoveSpeedPercent); }
    public double Period { get => _period; set => _period = Mark(value, nameof(Period), BuffPayloadField.Period); }
    /// <summary>
    /// The periodic this payload describes is scheduled by the holder's own script (<c>BBExecutePeriodically</c> in its
    /// <c>BuffOnUpdateActions</c>), so <c>BuffLifecycle</c>'s ticker must not run it as well. Batch 142 part 2: binding
    /// the summoner scripts left SummonerDot with two schedulers and the pulse landed twice (28 true damage where
    /// SummonerDot.luaobj's own <c>BBApplyDamage</c> asks for 14). The engine's ticker stays the applier for the
    /// payloads host code fills without a running script (<c>ItemPassiveService.AddMourning</c>). Not a
    /// presence-tracked column: nothing of it reaches the client or a corpus.
    /// </summary>
    public bool ScriptSchedulesPeriodic { get; set; }
    public double PeriodElapsed { get => _periodElapsed; set => _periodElapsed = Mark(value, nameof(PeriodElapsed), BuffPayloadField.PeriodElapsed); }
    public double Reflect { get => _reflect; set => _reflect = Mark(value, nameof(Reflect), BuffPayloadField.Reflect); }
    /// <summary>Molten Shield SpellDamageRatio: the reflect adds this fraction of the owner's AP at the moment of the hit.</summary>
    public double ReflectScale { get => _reflectScale; set => _reflectScale = Mark(value, nameof(ReflectScale), BuffPayloadField.ReflectScale); }
    public double ReviveHp { get => _reviveHp; set => _reviveHp = Mark(value, nameof(ReviveHp), BuffPayloadField.ReviveHp); }
    public double ReviveMp { get => _reviveMp; set => _reviveMp = Mark(value, nameof(ReviveMp), BuffPayloadField.ReviveMp); }
    public double SpellBlock { get => _spellBlock; set => _spellBlock = Mark(value, nameof(SpellBlock), BuffPayloadField.SpellBlock); }
    public double SpellBlockMult { get => _spellBlockMult; set => _spellBlockMult = Mark(value, nameof(SpellBlockMult), BuffPayloadField.SpellBlockMult); }
    public double SpellVamp { get => _spellVamp; set => _spellVamp = Mark(value, nameof(SpellVamp), BuffPayloadField.SpellVamp); }
    public double SpellVampMult { get => _spellVampMult; set => _spellVampMult = Mark(value, nameof(SpellVampMult), BuffPayloadField.SpellVampMult); }
    public double SpellbladeBonus { get => _spellbladeBonus; set => _spellbladeBonus = Mark(value, nameof(SpellbladeBonus), BuffPayloadField.SpellbladeBonus); }
    public double SpellbladeDelay { get => _spellbladeDelay; set => _spellbladeDelay = Mark(value, nameof(SpellbladeDelay), BuffPayloadField.SpellbladeDelay); }
    public double CrippleAge { get => _crippleAge; set => _crippleAge = Mark(value, nameof(CrippleAge), BuffPayloadField.CrippleAge); }
    public double CrippleReduction { get => _crippleReduction; set => _crippleReduction = Mark(value, nameof(CrippleReduction), BuffPayloadField.CrippleReduction); }
    public double BushwhackAge { get => _bushwhackAge; set => _bushwhackAge = Mark(value, nameof(BushwhackAge), BuffPayloadField.BushwhackAge); }
    public double BushwhackDamage { get => _bushwhackDamage; set => _bushwhackDamage = Mark(value, nameof(BushwhackDamage), BuffPayloadField.BushwhackDamage); }
    public double HallucinationDamageDealt { get => _hallucinationDamageDealt; set => _hallucinationDamageDealt = Mark(value, nameof(HallucinationDamageDealt), BuffPayloadField.HallucinationDamageDealt); }
    public double HallucinationDamageTaken { get => _hallucinationDamageTaken; set => _hallucinationDamageTaken = Mark(value, nameof(HallucinationDamageTaken), BuffPayloadField.HallucinationDamageTaken); }
    public double HallucinationExplosion { get => _hallucinationExplosion; set => _hallucinationExplosion = Mark(value, nameof(HallucinationExplosion), BuffPayloadField.HallucinationExplosion); }
    public double MagicShield { get => _magicShield; set => _magicShield = Mark(value, nameof(MagicShield), BuffPayloadField.MagicShield); }
    public double NextWander { get => _nextWander; set => _nextWander = Mark(value, nameof(NextWander), BuffPayloadField.NextWander); }
    public double NextShot { get => _nextShot; set => _nextShot = Mark(value, nameof(NextShot), BuffPayloadField.NextShot); }
    public double QRatio { get => _qRatio; set => _qRatio = Mark(value, nameof(QRatio), BuffPayloadField.QRatio); }
    /// <summary>Shipped BB tooltip variable Index 1, serialized as the first float.</summary>
    public double TooltipValue1 { get => _tooltipValue1; set => _tooltipValue1 = Mark(value, nameof(TooltipValue1), BuffPayloadField.TooltipValue1); }
    /// <summary>Shipped BB tooltip variable Index 2, serialized as the second float.</summary>
    public double TooltipValue2 { get => _tooltipValue2; set => _tooltipValue2 = Mark(value, nameof(TooltipValue2), BuffPayloadField.TooltipValue2); }
    /// <summary>Shipped BB tooltip variable Index 3, serialized as the third float.</summary>
    public double TooltipValue3 { get => _tooltipValue3; set => _tooltipValue3 = Mark(value, nameof(TooltipValue3), BuffPayloadField.TooltipValue3); }

    /// <summary>Flat attack range a script buff adds (<c>IncFlatAttackRangeMod</c>), in game units.</summary>
    public double AttackRange { get => _attackRange; set => _attackRange = Mark(value, nameof(AttackRange), BuffPayloadField.AttackRange); }
    /// <summary>Flat dodge chance a script buff adds (<c>IncFlatDodgeMod</c>), as a fraction.</summary>
    public double Dodge { get => _dodge; set => _dodge = Mark(value, nameof(Dodge), BuffPayloadField.Dodge); }
    /// <summary>Flat physical damage a script buff takes off each hit its holder takes (<c>IncFlatPhysicalReduction</c>; negative adds damage).</summary>
    public double FlatPhysicalReduction { get => _flatPhysicalReduction; set => _flatPhysicalReduction = Mark(value, nameof(FlatPhysicalReduction), BuffPayloadField.FlatPhysicalReduction); }
    /// <summary>Flat magic damage a script buff takes off each hit its holder takes (<c>IncFlatMagicReduction</c>; negative adds damage).</summary>
    public double FlatMagicReduction { get => _flatMagicReduction; set => _flatMagicReduction = Mark(value, nameof(FlatMagicReduction), BuffPayloadField.FlatMagicReduction); }
    /// <summary>Fraction of each physical hit a script buff spares its holder (<c>IncPercentPhysicalReduction</c>; negative adds damage).</summary>
    public double PercentPhysicalReduction { get => _percentPhysicalReduction; set => _percentPhysicalReduction = Mark(value, nameof(PercentPhysicalReduction), BuffPayloadField.PercentPhysicalReduction); }
    /// <summary>Units a script buff adds to the holder's perception radius (<c>IncFlatBubbleRadiusMod</c>, <c>mFlatBubbleRadiusMod</c>).</summary>
    public double BubbleRadiusFlat { get => _bubbleRadiusFlat; set => _bubbleRadiusFlat = Mark(value, nameof(BubbleRadiusFlat), BuffPayloadField.BubbleRadiusFlat); }
    /// <summary>Fraction of the holder's own perception radius a script buff adds (<c>IncPercentBubbleRadiusMod</c>, <c>mPercentBubbleRadiusMod</c>; -1 leaves the unit blind).</summary>
    public double BubbleRadiusPercent { get => _bubbleRadiusPercent; set => _bubbleRadiusPercent = Mark(value, nameof(BubbleRadiusPercent), BuffPayloadField.BubbleRadiusPercent); }
    /// <summary>Fraction of each magic hit a script buff spares its holder (<c>IncPercentMagicReduction</c>; negative adds damage).</summary>
    public double PercentMagicReduction { get => _percentMagicReduction; set => _percentMagicReduction = Mark(value, nameof(PercentMagicReduction), BuffPayloadField.PercentMagicReduction); }

    public bool AbilityDisabled { get; set; }
    /// <summary>
    /// The buff is an <see cref="BuffType.Invulnerability"/> buff that does not stop its holder acting (a script-driven
    /// protection such as Kayle's Intervention); without it that type is the Zhonya's stasis.
    /// </summary>
    public bool ActionsAllowed { get; set; }
    /// <summary>Source lifecycle marker used by pet and trap buffs.</summary>
    public bool Active { get; set; }
    public bool ActionDisabled { get; set; }
    /// <summary>
    /// Source <c>born</c> timestamp for timed pet/trap state.  This is a
    /// clock value, rather than a flag; preserving the value matters when a
    /// match clock is not zero at entity creation.
    /// </summary>
    public double Born { get => _born; set => _born = Finite(value, nameof(Born)); }
    public bool CanAttack { get; set; }
    /// <summary>SetDisarmed(true) or SetCanAttack(false) from a script buff: the unit's CanAttack status bit is clear.</summary>
    public bool Disarmed { get; set; }
    /// <summary>SetPacified(true) from a script buff: the unit's CanAttack and CanCast status bits are clear.</summary>
    public bool Pacified { get; set; }
    /// <summary>SetSuppressCallForHelp(true) from a script buff: damage the unit takes calls nobody to its help (YorickRADelay).</summary>
    public bool SuppressCallForHelp { get; set; }
    /// <summary>SetCallForHelpSuppresser(true) from a script buff: damage the unit deals calls nobody to its victim's help (YorickRADelay).</summary>
    public bool CallForHelpSuppresser { get; set; }
    /// <summary>SetIgnoreCallForHelp(true) from a script buff: the unit does not answer a call for help (YorickRADelay).</summary>
    public bool IgnoreCallForHelp { get; set; }
    /// <summary>
    /// SetSuppressed(true) from a script buff (the global <c>Suppression</c> script and Skarner's Impale set it with SetStunned): the unit's Suppressed
    /// status bit (CharacterState bit 7) is set. The bit alone clears no action; SetStunned does that.
    /// </summary>
    public bool Suppressed { get; set; }
    public bool Ghosted { get; set; }
    /// <summary>SetGhostProof(true) from a script (Anivia's and Jarvan's walls, Trundle's pillar): ghosted units cannot walk through the holder.</summary>
    public bool GhostProof { get; set; }
    public bool Invulnerable { get; set; }
    public bool MagicImmune { get; set; }
    /// <summary>Shipped dispel metadata; expiry and explicit script removal still apply.</summary>
    public bool NonDispellable { get; set; }
    /// <summary>
    /// The buff's own shipped script draws its activation effect: its <c>OnBuffActivate</c> makes a
    /// <c>BBSpellEffectCreate</c> and its <c>OnBuffDeactivate</c> takes it back, so the particle's lifetime is the
    /// script's. The server-owned persistent visual (BuffEffectService's <c>PersistentEffects</c> row) is a leftover
    /// from the hand-written buffs and must not sit beside it for the life of the buff.
    /// </summary>
    public bool ScriptOwnsActivationFx { get; set; }
    /// <summary>
    /// The buff's own shipped template gives the stock client an activation particle of its own: a non-empty
    /// <c>AutoBuffActivateEffect</c> (or <c>AutoBuffActivateEffect2</c>..<c>4</c>, the four slots
    /// the client's torch restart walks). The client paints it for the life of the add and re-presents it on
    /// NpcBuffReplace (0x32), whose handler ends in the client's torch restart: that
    /// function deactivates every auto effect the instance holds and re-creates the four slots, so a renewal sent as
    /// 0x32 kills and re-lights the particle. A renewal of a live buff must not re-run the buff's activation
    /// presentation (M2: a <c>BUFF_RENEW_EXISTING</c> renewal does not re-run <c>OnBuffActivate</c>), so it goes out
    /// as NpcBuffUpdateCount (0x1F) instead — its handler passes the count (@6), the duration (@7)
    /// and the running time (@11) to the client's instance resize, which restarts the slot's
    /// timers and rebuilds the stack the same way and leaves live particles alone (the torch is restarted only where
    /// a particle slot is empty), so the HUD timer the renewal resets still reaches the owner. Batch 157 part 3:
    /// Mana Manipulator's aura "disappears, appears, disappears, appears" because
    /// <c>Spells/ManaManipulatorAuraSelf.luaobj</c> carries <c>AutoBuffActivateEffect='ZettasManaManipulator_itm.troy'</c>
    /// and its item re-adds the buff every second with <c>BUFF_RENEW_EXISTING</c>; Stark's Fervor's aura buffs carry
    /// <c>''</c> and draw their troy in their own <c>OnBuffActivate</c>, which no renewal touches. Set by
    /// <c>BbBuffRuntime.Add</c>; read by the held-buff rewrite (ReplicationDelivery) that picks the renewal packet.
    /// </summary>
    public bool ClientActivationFx { get; set; }
    /// <summary>
    /// Extra seconds the wire copy of this buff is given over its script's duration. A buff the script re-adds every
    /// period with <c>BUFF_RENEW_EXISTING</c> must never drop out of the client between two re-adds: the client's copy
    /// runs out at the add's duration, and a renewal for a slot whose stack list is already empty is a no-op in both
    /// silent handlers (the client's buff packet handler), so a lapsed copy is never coming back. The shipped
    /// scripts leave almost no room (3152's WillOfTheAncients and 3105's AegisoftheLegionAuraFriend are
    /// duration 1.0 / period 1.0; Mana Manipulator's is 1.1 / 1.0), which is only enough for a re-add that reaches the
    /// client the instant the copy runs out. Set for the adds of the periodic re-add hooks (the two self-buff polls and
    /// an ammo buff's BuffOnUpdateAmmo); 0.25 s of margin is what the emulator's own delivery needs and is under the
    /// half-duration the poll's renewal throttle allows.
    /// Reconstruction: how much transport retail's server budgets for is not in the client or the shipped data; the
    /// margin is not part of the script's duration (the server's own timer still ends where the script says).
    /// </summary>
    public double WireDurationMargin { get; set; }
    /// <summary>
    /// The add is server-side bookkeeping only: its script's logic needs the buff (<c>BBIfHasBuff</c> names it and its
    /// duration is the marker's) but no packet may carry it to the stock client. Its script's buff template is Riot's
    /// copy-paste of Morgana's Q (<c>BuffName='Dark Binding'</c>, <c>BuffTextureName='FallenAngel_DarkBinding.dds'</c>,
    /// <c>AutoBuffActivateEffect='DarkBinding_tar.troy'</c>), so a client that resolves the 0xBF's name hash to that
    /// script paints DarkBinding_tar.troy on the holder for the life of the marker: the client's torch restart
    /// plays the buff particle of whichever script the global-buff lookup found, with no
    /// gate on the unit's kind or on the add's type or hidden flag. Batch 157 part 2: placing a ward drew Morgana's Q
    /// on the ward, because <c>Spells/SightWard.luaobj</c> (and VisionWard's and WriggleLanternWard's) adds
    /// <c>ItemPlacementMissile</c> as the placement marker and that missile script carries the copied template. The
    /// template is not this script's own visual: <c>DarkBindingMissile</c>'s add names itself and the snare it lands is
    /// exactly this troy, while a placement's own visual is the <c>BBSpellEffectCreate</c> of its
    /// <c>TargetExecute</c> (<c>ItemPlacement_tar.troy</c>). Set by <c>BbBuffRuntime.Add</c>; the buff wire honours it
    /// beside <c>GameData.HasClientBuff</c> (which keeps the name unknown to the client's hash map out of the packets).
    /// Reconstruction: retail's ward placement shows no Morgana's Q, so retail's server cannot have sent this add; the
    /// packet retail uses for a marker the client must not draw is not recoverable from the client binary, and the
    /// add itself stays on the server, applied and authoritative.
    /// </summary>
    public bool ServerOnlyMarker { get; set; }
    /// <summary>Source physical-only immunity used by Fortify.</summary>
    public bool PhysicalImmune { get; set; }
    // Dead columns since the Blitzcrank conversion (19 October 2026): his buffs are script buffs now; the recorded corpora still carry
    // ManaBarrier, ManaBarrierCooldown, ManaBarrierIcon, PowerFist, PowerFistSlow, RocketGrab, StaticFieldAura and PowerFistRank.
    public bool ManaBarrier { get; set; }
    public bool ManaBarrierCooldown { get; set; }
    public bool ManaBarrierIcon { get; set; }
    public bool MovementDisabled { get; set; }
    public bool NoRender { get; set; }
    /// <summary>SetForceRenderParticles(true) from a script buff: bit 14 of the unit's CharacterState word (particles bound to it draw although the unit itself is hidden).</summary>
    public bool ForceRenderParticles { get; set; }
    public bool PowerFist { get; set; }
    public bool PowerFistSlow { get; set; }
    public bool RocketGrab { get; set; }
    public bool Silence { get; set; }
    public bool SourceIsPeriodic { get; set; }
    public bool StaticFieldAura { get; set; }
    public bool Stealthed { get; set; }
    public bool Silenced { get; set; }
    public bool Targetable { get; set; }
    public bool Untargetable { get; set; }

    public int AmountToSubtract { get; set; }
    public int ActionState { get; set; }
    public int BushwhackTicks { get; set; }
    public int DeceiveRank { get; set; }
    public int FlurryHits { get; set; }
    public int MaxTicks { get => _maxTicks; set { _maxTicks = value; _presentFields.Add(BuffPayloadField.MaxTicks); } }
    public int? PowerFistRank { get; set; }
    public int Rank { get; set; }
    public int Stacks { get; set; } = 1;
    public int TicksDone
    {
        get => _ticksDone;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(TicksDone));
            _ticksDone = value;
            _presentFields.Add(BuffPayloadField.TicksDone);
        }
    }

    public uint ExtraFxNetId { get; set; }
    public uint FxNetId { get; set; }
    /// <summary>Owner net ID carried by the source key <c>silver_caster</c>.</summary>
    public uint SilverCasterNetId { get; set; }
    public uint SpellOwnerNetId { get; set; }
    public uint UnitNetId { get; set; }
    /// <summary>Exact displacement instance associated with this buff.</summary>
    public ForcedMoveState? ForcedMove { get; set; }

    public Position2? CasterPosition { get; set; }
    public Position2? DeceivePosition { get; set; }
    public Position2? FearOrigin { get; set; }
    public Position2? Position { get; set; }
    public IReadOnlyList<uint> VisionBubbles { get; set; } = Array.Empty<uint>();

    public DamageType DamageType { get; set; } = global::Amberline.Core.DamageType.Magic;
    public BuffResourceDomain ResourceDomain { get; set; } = BuffResourceDomain.Any;
    public string? ItemAuraKey { get; set; }
    public DamageSourceKind? SourceKind { get; set; }
    public string? ForcedMoveEffect { get; set; }

    public bool Has(BuffPayloadField field) => _presentFields.Contains(field);

    /// <summary>Shallow-copy the typed payload while isolating presence state.</summary>
    public BuffPayload Clone()
    {
        var clone = (BuffPayload)MemberwiseClone();
        clone._presentFields = new HashSet<BuffPayloadField>(_presentFields);
        return clone;
    }

    /// <summary>Mark an explicitly decoded source key whose value is zero.</summary>
    public void MarkPresent(BuffPayloadField field) => _presentFields.Add(field);

    private double Mark(double value, string name, BuffPayloadField field)
    {
        var finite = Finite(value, name);
        _presentFields.Add(field);
        return finite;
    }

    private static double Finite(double value, string name) => double.IsFinite(value)
        ? value
        : throw new ArgumentOutOfRangeException(name, value, "buff payload values must be finite");

    private int _maxTicks;
    private int _ticksDone;
}
