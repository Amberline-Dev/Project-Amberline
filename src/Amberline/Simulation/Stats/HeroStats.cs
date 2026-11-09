using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;

namespace Amberline.Simulation.Stats;

/// <summary>Inputs needed to calculate one pure effective hero-stat snapshot.</summary>
public sealed class HeroStatsInput
{
    public HeroStatsInput(
        string champion,
        int level,
        IReadOnlyList<InventorySlot?> inventory,
        IEnumerable<BuffState>? buffs = null,
        HeroItemState? itemState = null,
        RunePage? runes = null,
        MasteryPage? masteries = null,
        IReadOnlyDictionary<string, double>? permanentMods = null,
        string? statCharacter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(champion);
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Count != Amberline.Simulation.Inventory.SlotCount)
            throw new ArgumentException($"inventory must contain exactly {Amberline.Simulation.Inventory.SlotCount} slots", nameof(inventory));
        Champion = champion;
        StatCharacter = string.IsNullOrWhiteSpace(statCharacter) ? champion : statCharacter;
        Level = level;
        Inventory = inventory;
        Buffs = buffs?.ToArray() ?? Array.Empty<BuffState>();
        ItemState = itemState ?? new HeroItemState();
        Runes = runes ?? RunePage.Empty;
        Masteries = masteries ?? MasteryPage.Empty;
        PermanentMods = permanentMods ?? new Dictionary<string, double>();
    }

    public string Champion { get; }
    /// <summary>The character whose record gives the base stats: the champion, or the pushed form (<see cref="Entity.StatCharacterName"/>).</summary>
    public string StatCharacter { get; }
    public int Level { get; }
    public IReadOnlyList<InventorySlot?> Inventory { get; }
    public IReadOnlyList<BuffState> Buffs { get; }
    public HeroItemState ItemState { get; }
    public RunePage Runes { get; }
    public MasteryPage Masteries { get; }
    /// <summary>Script-added permanent modifiers (<c>Entity.PermanentStatMods</c>), merged with the item modifiers.</summary>
    public IReadOnlyDictionary<string, double> PermanentMods { get; }
}

/// <summary>Complete effective values consumed by combat and replication.</summary>
public sealed record HeroStats(
    double MaxHp,
    double MaxMp,
    double AttackDamage,
    double Armor,
    double SpellBlock,
    double HpRegen,
    double MpRegen,
    double AttackSpeedMod,
    double MoveSpeed,
    double AttackRange,
    double AbilityPower,
    double Dodge,
    double CritChance,
    double CritMultiplier,
    double CooldownMod,
    double Hardness,
    bool DodgePiercing,
    double ArmorPenFlat,
    double ArmorPenPercent,
    double MagicPenFlat,
    double MagicPenPercent,
    double LifeSteal,
    double SpellVamp)
{
    // These patch-126 rune/mastery effects are also consumed by the runtime
    // economy and progression hooks after this snapshot is calculated.
    public double GoldPer10Mod { get; init; }
    public double ExperiencePercent { get; init; }
    public double TimeDeadPercent { get; init; }
    public double FlatMovementSpeedMod { get; init; }
    /// <summary>The flat health the sources add before the percent step (<c>GetFlatHPPoolMod</c>), the base health left out.</summary>
    public double FlatHpPoolMod { get; init; }
    public double FlatPhysicalDamageMod { get; init; }
    /// <summary>The client's <c>mFlatCastRangeMod</c> (CharacterIntermediate +0xb8): what every targeter's CanFireSpell adds to the cast range.</summary>
    public double FlatCastRangeMod { get; init; }

    // Energy is independent from mana. These fields stay zero for mana users.
    public double EnergyPool { get; init; }
    public double EnergyRegen { get; init; }
    public MasteryContribution MasteryContribution { get; init; } = MasteryContribution.Empty;
}

/// <summary>
/// Calculates effective hero stats. The calculation deliberately
/// owns the ordering of level growth, item modifiers, active payloads, script
/// growth, conversion passives, and resource-specific cleanup in one place.
/// Reconstruction: the server's stat producer is not in the 126 client, which only holds the
/// CharacterIntermediate accumulators (flat, percent, per-level and multiplicative fields). The
/// (base + flat) * (1 + percent) composition and the per-stat order below are the maintained
/// reading of those accumulators and the shipped item/buff scripts, with the retail clamps taken from
/// Map1 Constants.var (cooldown -0.4) and UnitStats.inibin (movement).
/// </summary>
public static class HeroStatCalculator
{
    // LEVELS/Map1/Constants.var overrides the engine's -0.95 fallback.
    public const double MinimumCooldownMod = -0.4;
    // Retail data: the shipped DATA/Globals/UnitStats.inibin (a server-side file, the client never reads it)
    // holds exactly these numbers: 220, 415 and 490 (the stage starts), the factors 0.5, 0.8 and 0.5, and 0.35 for
    // the stacked slows (MovementSpeedCalculator). The stages apply after all flat, percentage, multiplicative
    // and champion movement modifiers have been composed; the 126 client has no copy of the producer.
    public const double MovementSoftCapLowerStart = 220.0;
    public const double MovementSoftCapStart = 415.0;
    public const double MovementSoftCapEnd = 490.0;
    public const double MovementSoftCapMiddleScale = 0.80;
    public const double MovementSoftCapHighScale = 0.50;

    public static double ApplyMovementSoftCaps(double movementSpeed)
    {
        if (!double.IsFinite(movementSpeed))
            throw new ArgumentOutOfRangeException(nameof(movementSpeed));
        var speed = Math.Max(0.0, movementSpeed);
        if (speed < MovementSoftCapLowerStart) return 110.0 + speed * 0.5;
        if (speed <= MovementSoftCapStart) return speed;
        if (speed <= MovementSoftCapEnd)
            return speed * MovementSoftCapMiddleScale + 83.0;
        return speed * MovementSoftCapHighScale + 230.0;
    }

    public static HeroStats Calculate(GameData gameData, ItemCatalog itemCatalog, HeroStatsInput input)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(input);

        var level = Math.Max(1, input.Level);
        var mastery = MasteryEffects.Calculate(input.Masteries, Math.Min(18, level));
        // Reconstruction: stats at level L are base + perLevel * L (GameData.StatsAtLevel). The shipped DATA/Globals/HealthGain.ini
        // ([Coefficients] MCoefficient 0.7, NCoefficient 1.1) is loaded by the stock client's Experience unit into two globals it never
        // reads (defaults 0 and 1), so the server shapes health growth with them; the formula is not recoverable, health growth stays linear.
        var baseStats = gameData.StatsAtLevel(gameData.Champion(input.StatCharacter), input.Level);
        var itemModifiers = Inventory.AggregateModifiers(itemCatalog, input.Inventory);
        var modifiers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in itemModifiers)
            modifiers[pair.Key] = pair.Value;
        foreach (var pair in input.Runes.Modifiers)
            modifiers[pair.Key] = modifiers.TryGetValue(pair.Key, out var existing)
                ? existing + pair.Value
                : pair.Value;
        foreach (var pair in mastery.Modifiers)
            modifiers[pair.Key] = modifiers.TryGetValue(pair.Key, out var existing)
                ? existing + pair.Value
                : pair.Value;

        // IncPermanent*Mod of a BuildingBlocks script: the character's permanent flat/percent modifiers are the same
        // accumulators the items feed, so they join the item totals before the percentage step. Items/3124.luaobj's
        // OnBuffActivate / OnDeactivate pair (PercentAttackSpeedMod ±0.04, FlatMagicDamageMod ±7 per copy) reaches
        // this as a PermanentMod now (batch 147); the hand-written per-copy loop that stood in for it is gone, or
        // the purchase bonus would be counted twice.
        foreach (var pair in input.PermanentMods)
            modifiers[pair.Key] = modifiers.TryGetValue(pair.Key, out var existing)
                ? existing + pair.Value
                : pair.Value;

        // Script wins (batch 147 step 4): the whole effect of the Boots of Mobility penalty is
        // Spells/BootsOfMobilityDebuff.luaobj's own BuffOnUpdateStats, one BBIncStat IncFlatMovementSpeedMod of
        // -60 on the holder, which reaches the calculator as the buff's MoveSpeedFlat below. The hand-written
        // rewrite of FlatMovementSpeedMod that stood in for it (60 + the fastest other item) is gone: it counted
        // the same penalty twice once the buff is script-bound and never matched the shipped -60 with more than
        // one movement-speed item equipped.

        var dynamicCooldown = input.Buffs.Sum(buff =>
            buff.Payload.CooldownModBase + buff.Payload.CooldownModPerLevel * level);

        double Active(Func<BuffPayload, double> selector) => input.Buffs.Sum(buff => selector(buff.Payload));
        double ItemTotal(string name) => Modifier(modifiers, name) + Modifier(modifiers, "r" + name + "PerLevel") * level;
        double Scaled(double baseValue, string flatName, string percentName, Func<BuffPayload, double>? active = null) =>
            (baseValue + ItemTotal(flatName) + (active is null ? 0.0 : Active(active))) *
            (1.0 + ItemTotal(percentName));

        var maxHp = (baseStats.MaxHp + Modifier(modifiers, "FlatHPPoolMod")
                     + Modifier(modifiers, "rFlatHPModPerLevel") * level + Active(payload => payload.MaxHp)) *
                    (1.0 + Modifier(modifiers, "PercentHPPoolMod"));
        // Shipped rune data names the per-level mana field rFlatMPModPerLevel (no
        // shipped record carries a rFlatMPPoolModPerLevel), so ItemTotal cannot
        // build the key from the pool name. Batch 149 step 5: the Tear family's growth is
        // Spells/TearOfTheGoddessTrack.luaobj's own BuffOnUpdateStats BBIncFlatPARPoolMod of
        // CharVars.TearBonusMana, so it joins Active(payload => payload.MaxMp) like every other script pool row.
        var maxMp = (baseStats.MaxMp + Modifier(modifiers, "FlatMPPoolMod") +
                     Modifier(modifiers, "rFlatMPModPerLevel") * level +
                     Active(payload => payload.MaxMp)) *
                    (1.0 + ItemTotal("PercentMPPoolMod"));
        // The pool is one pair for every kind (mMP / mMaxMP); the kind is the character record's PARType, and the item, rune and script
        // modifiers are keyed by the kind (Flat{MP,Energy,Souls,Shield,Other}PoolMod and ...RegenMod), so a mana item
        // does nothing to a fury, heat or shield pool and the other way round.
        var pool = gameData.Champion(input.StatCharacter).PrimaryResource;
        var poolKey = pool.StatKeyName();
        var energyPool = baseStats.MaxMp + Modifier(modifiers, "FlatEnergyPoolMod") +
                         Modifier(modifiers, "rFlatEnergyModPerLevel") * level + Active(payload => payload.MaxMp);
        // Souls, Shield and Other: the same composition as energy with their own keys, and the percent step the client's accumulators have.
        var otherPool = (baseStats.MaxMp + Modifier(modifiers, "Flat" + poolKey + "PoolMod") +
                         Modifier(modifiers, "rFlat" + poolKey + "ModPerLevel") * level + Active(payload => payload.MaxMp)) *
                        (1.0 + Modifier(modifiers, "Percent" + poolKey + "PoolMod"));
        var usesMana = pool == PrimaryResourceType.Mana;
        var mpRegen = Scaled(baseStats.MpRegen, "FlatMPRegenMod", "PercentMPRegenMod",
                         payload => payload.MpRegen);
        var energyRegen = baseStats.MpRegen + Modifier(modifiers, "FlatEnergyRegenMod") +
                          Modifier(modifiers, "rFlatEnergyRegenModPerLevel") * level +
                          input.Buffs.Sum(buff => buff.Payload.ResourceDomain == BuffResourceDomain.Mana
                              ? 0.0 : buff.Payload.MpRegen);
        var otherRegen = (baseStats.MpRegen + Modifier(modifiers, "Flat" + poolKey + "RegenMod") +
                          Modifier(modifiers, "rFlat" + poolKey + "RegenModPerLevel") * level +
                          input.Buffs.Sum(buff => buff.Payload.ResourceDomain == BuffResourceDomain.Mana
                              ? 0.0 : buff.Payload.MpRegen)) *
                         (1.0 + Modifier(modifiers, "Percent" + poolKey + "RegenMod"));
        var armor = Scaled(baseStats.Armor, "FlatArmorMod", "PercentArmorMod") + Active(payload => payload.Armor);
        var spellBlock = Scaled(baseStats.SpellBlock, "FlatSpellBlockMod", "PercentSpellBlockMod") +
                         Active(payload => payload.SpellBlock);
        var attackDamage = Scaled(baseStats.AttackDamage, "FlatPhysicalDamageMod", "PercentPhysicalDamageMod") +
                           Active(payload => payload.AttackDamage);
        // Ardor and ZhonyasRing are live script buffs since batch 144 (Spells/Ardor.luaobj and Spells/ZhonyasRing.luaobj),
        // so the flat AP pool is just the sum: each buff's BuffOnUpdateActions recomputes its own delta from the stat this
        // line builds and BuffOnUpdateStats puts it back through IncFlatMagicDamageMod. The steady state of that feedback
        // loop is the scripts' own sequential multiplication over the complete flat pool (1.30 for ZhonyasRing's 0.3 share,
        // 1.04 for Ardor's PercentMod), which is what the calculator used to hard-code here.
        var abilityPower = (ItemTotal("FlatMagicDamageMod") +
                            Active(payload => payload.AbilityPower)) *
                           (1.0 + ItemTotal("PercentMagicDamageMod"));
        var attackRange = Scaled(baseStats.AttackRange, "FlatAttackRangeMod", "PercentAttackRangeMod",
            payload => payload.AttackRange);

        armor *= Math.Max(0.0, 1.0 + Active(payload => payload.ArmorMult));
        spellBlock *= Math.Max(0.0, 1.0 + Active(payload => payload.SpellBlockMult));
        var moveSpeed = MovementSpeedCalculator.Calculate(
            baseStats.MoveSpeed,
            ItemTotal("FlatMovementSpeedMod"),
            ItemTotal("PercentMovementSpeedMod"),
            Modifier(modifiers, "PercentMultiplicativeMovementSpeedMod"),
            input.Buffs.Select(buff => buff.Payload),
            applyHeroSoftCaps: false);

        var attackSpeedPercent = ItemTotal("PercentAttackSpeedMod") +
                                 Active(payload => payload.AttackSpeedMod);
        // Ardor.luaobj reads GetPercentAttackSpeedMod, subtracts its prior
        // Ardor contribution, and adds PercentMod times that remaining
        // percentage through IncPercentAttackSpeedMod. The native character
        // has separate mAttackSpeedMod and percentage-accumulator fields.
        // The maintained server convention stores level growth in the base
        // value and adds item/buff percentage points to that value. Ardor
        // scales only this explicit bonus pool; the native getter's exact
        // default remains an open cross-check. Since batch 144 Ardor runs as
        // the live buff it is and lands in Active(...) above, so the calculator
        // no longer multiplies the pool by its own reading of PercentMod.
        var attackSpeed = baseStats.AttackSpeedMod + attackSpeedPercent;
        // IncPercentMultiplicativeAttackSpeedMod is called once per active
        // payload. Native multiplies each clamped factor; summing the
        // payload values makes two distinct slows behave as one larger slow.
        var attackSpeedMultiplier = 1.0;
        foreach (var buff in input.Buffs)
            attackSpeedMultiplier *= Math.Max(0.0, 1.0 + buff.Payload.AttackSpeedMult);
        // The floor is gcd_PercentAttackSpeedModMinimum (-0.95, a multiplier of 0.05) from Map1 Constants.var: the one value every
        // unit class reads (MapConstants.AttackSpeedMultiplierFloor).
        attackSpeed = Math.Max(gameData.LevelConstants.AttackSpeedMultiplierFloor, attackSpeed *
            (1.0 + Modifier(modifiers, "PercentMultiplicativeAttackSpeedMod")) *
            attackSpeedMultiplier);

        // The script helper IncFlatDodgeMod (Evasion 114, Counter Strike, Fleet of Foot, Udyr's monkey agility) stores (1 - delta) x current
        // with delta clamped to [0, 1], so script sources compose as 1 - product(1 - delta), not a sum.
        // Batch 551: Evasion is one of those script sources. Talents/114.luaobj's UpdateSelfBuffStats is BBIncStat IncFlatDodgeMod with
        // 0.005 x TalentLevel, the same helper the buffs call, so its factor joins the buffs' product rather than the item sum it was added to.
        // Reconstruction: how that factor joins the item FlatDodgeMod and the rune m_FlatDodgeMod plus per-level fields is not in the client:
        // mDodge is replicated and GetDodge reads it, so the client composes nothing. Those are summed with it here.
        var masteryDodge = Modifier(mastery.Modifiers, "FlatDodgeMod");
        var scriptDodge = 1.0 - input.Buffs.Aggregate(1.0 - Math.Clamp(masteryDodge, 0.0, 1.0),
            (remaining, buff) => remaining * (1.0 - Math.Clamp(buff.Payload.Dodge, 0.0, 1.0)));
        var dodge = Math.Max(0.0, ItemTotal("FlatDodgeMod") - masteryDodge + Modifier(modifiers, "rFlatDodgeMod") + scriptDodge);
        // Crit chance is a probability: the replicated HUD value is capped at
        // 100% like the combat roll (six Infinity Edges must not show 150%).
        var critChance = Math.Clamp(ItemTotal("FlatCritChanceMod") + Active(payload => payload.CritChance), 0.0, 1.0);
        var critMultiplier = Math.Max(1.0,
            (2.0 + ItemTotal("FlatCritDamageMod") + Active(payload => payload.CritDamage)) *
            (1.0 + ItemTotal("PercentCritDamageMod")));
        var cooldownMod = ItemTotal("PercentCooldownMod") + Modifier(modifiers, "rPercentCooldownMod") +
                           Active(payload => payload.CooldownMod) + dynamicCooldown;
        var hardness = Math.Max(0.0, ItemTotal("PercentHardnessMod"));
        var dodgePiercing = Active(payload => payload.DodgePiercing) > 0.0;
        var armorPenFlat = ItemTotal("FlatArmorPenetrationMod") +
                           Modifier(modifiers, "rFlatArmorPenetrationMod") +
                           Active(payload => payload.ArmorPenFlat);
        // Last Whisper's IncPercentArmorPenetrationMod and Sorcerer's Shoes' IncFlatMagicPenetrationMod reach the
        // calculator as active payloads of their buffs (batch 144), the same fields the item and rune totals feed.
        // Batch 340: the script helper's armour penetration setter clamps its value to [0, 1]
        // and stores (1 - p) * current in the remaining-armor field, exactly as the magic one
        // below: two sources compose as remaining armor (Last Whisper's 40 and a mastery's 10 leave 54,
        // not 50). Every shipped percent armor source is such a script call (LastWhisper, the mastery items 4109, 4202, 4233,
        // 4241, the Odin buffs); no item, rune or mastery inibin carries a non-zero PercentArmorPenetrationMod.
        var armorPenRemaining = 1.0 - Math.Clamp(ItemTotal("PercentArmorPenetrationMod"), 0.0, 1.0);
        foreach (var buff in input.Buffs)
            armorPenRemaining *= 1.0 - Math.Clamp(buff.Payload.ArmorPenPercent, 0.0, 1.0);
        var armorPenPercent = 1.0 - armorPenRemaining;
        var magicPenFlat = ItemTotal("FlatMagicPenetrationMod") + Modifier(modifiers, "rFlatMagicPenetrationMod") +
                           Active(payload => payload.MagicPenFlat);
        // IncPercentMagicPenetrationMod updates the native remaining-resistance
        // component multiplicatively: each call stores (1 - p) * current.
        // Keep item, rune and mastery contributions separate so aggregation
        // does not turn Void Staff 40% plus Archaic Knowledge 15% into 55%.
        var magicPenRemaining = 1.0;
        void AddMagicPenetration(double value)
        {
            magicPenRemaining *= 1.0 - Math.Clamp(value, 0.0, 1.0);
        }

        AddMagicPenetration(Modifier(itemModifiers, "PercentMagicPenetrationMod"));
        AddMagicPenetration(Modifier(itemModifiers, "rPercentMagicPenetrationMod"));
        AddMagicPenetration(Modifier(input.Runes.Modifiers, "PercentMagicPenetrationMod"));
        AddMagicPenetration(Modifier(input.Runes.Modifiers, "rPercentMagicPenetrationMod"));
        AddMagicPenetration(Modifier(mastery.Modifiers, "PercentMagicPenetrationMod"));
        AddMagicPenetration(Modifier(mastery.Modifiers, "rPercentMagicPenetrationMod"));
        foreach (var buff in input.Buffs)
            AddMagicPenetration(buff.Payload.MagicPenPercent);
        var magicPenPercent = 1.0 - magicPenRemaining;
        var lifeSteal = Math.Max(0.0, (Modifier(modifiers, "PercentLifeStealMod") + Active(payload => payload.LifeSteal)) *
            (1.0 + Active(payload => payload.LifeStealMult)));
        var spellVamp = Math.Max(0.0, (Modifier(modifiers, "PercentSpellVampMod") + Active(payload => payload.SpellVamp)) *
            (1.0 + Active(payload => payload.SpellVampMult)));

        // Script-owned growth is applied through ItemScriptStats, like every other script stat row. Batch 149
        // step 1: the Bloodthirster's (3072) and Warmog's (3083) growth counters are the copy's own script
        // InstanceVars; batch 149 step 5 took Rod of Ages' ageing clock with it — Items/3027.luaobj's
        // UpdateSelfBuffStats reads its own InstanceVars.BonusHealth / BonusMana / BonusAbilityPower and writes
        // IncFlatHPPoolMod / IncFlatPARPoolMod / IncFlatMagicDamageMod rows that arrive as the ItemScript payload,
        // before the percent step the script's own flat rows sit behind (the hand-written stacks were added after
        // it, script wins).

        // Strength of Spirit consumes the final mana pool, including Rod of
        // Ages and other script-owned mana stacks. Reconstruction: its script reads GetMaxPAR(PAR_MANA); an
        // energy hero reads no mana (Crest of the Ancient Golem queries PAR_MANA and PAR_ENERGY in turn, so the
        // read is typed), hence zero here. Add it once before the
        // ordinary HP-regen percentage; a per-buff selector would multiply it
        // once for every active buff. Warmog's regen (Items/3083.luaobj UpdateSelfBuffStats IncStat
        // IncFlatHPRegenMod) is one of those flat rows and joins the sum with the other script payloads.
        var strengthOfSpiritRegen = usesMana
            ? Math.Max(0.0, maxMp) * mastery.Combat.StrengthOfSpiritHealthRegenPerMaxMana : 0.0;
        var hpRegen = (baseStats.HpRegen + ItemTotal("FlatHPRegenMod") +
                       strengthOfSpiritRegen + Active(payload => payload.HpRegen)) *
                      (1.0 + ItemTotal("PercentHPRegenMod")) *
                      Math.Max(0.0, 1.0 + Active(payload => payload.HpRegenMult));

        // Batch 149 step 1: the snowball trio's per-stack stats and its two twenty-stack caps are the script buffs
        // (MejaisStats / LeviathanStats / MuramasaStats and MejaisCap / LeviathanCap / MuramasaCap) and arrive the
        // same way every other script buff's do. Batch 149 step 5: Archangel's (3003) and Manamune's (3004)
        // conversions go with them — Items/3003.luaobj's UpdateSelfBuffStats is
        // InstanceVars.MaxMana * 0.03 into IncFlatMagicDamageMod and Spells/ManamuneAttackConversion.luaobj's is
        // InstanceVars.OwnerMana * 0.02 into IncFlatPhysicalDamageMod.
        // Batch 144: Atma's Impaler (3005) and Zhonya's Ring (3089) run from their shipped scripts now, so the two
        // hand-written conversions are gone. Atma's Impaler is Spells/AtmasImpaler.luaobj (OwnerHealth * 0.02 flat
        // physical damage), Zhonya's Ring is Spells/ZhonyasRing.luaobj (0.3 of the flat AP pool beside its own), and
        // Ardor's own PercentMod arrives as the live buff talent 111 asks for.
        cooldownMod = Math.Max(MinimumCooldownMod, cooldownMod);
        moveSpeed = ApplyMovementSoftCaps(moveSpeed);
        var usesEnergy = pool == PrimaryResourceType.Energy;
        if (usesEnergy)
        {
            // Energy uses the explicit energy aliases plus shared/energy
            // payloads. Mana-only aura payloads are excluded here.
            maxMp = energyPool;
            energyRegen += energyPool * Active(payload => payload.MaxEnergyRegenPercent);
            mpRegen = energyRegen;
        }
        else if (!usesMana)
        {
            // Souls, Shield and Other: the pool of the kind alone (no tear, Rod of Ages or mana-percent item reaches it) and its own regeneration.
            maxMp = otherPool;
            mpRegen = otherRegen;
        }
        else
            mpRegen += maxMp * Active(payload => payload.MaxManaRegenPercent);
        // The mana init and set-max clamp the maximum to 0 or more (the client's init and the setter beside it): a character whose BaseMP is
        // negative (Vladimir -10000, Dr. Mundo -100000, the health-cost heroes) has an empty pool, which the client then shows as 0 / 0. The
        // 0xCC replication writes the raw float, and a negative current pool would refuse every cast (OnCanUseSpell compares mMP with the cost).
        // BBIncPercentPARPoolMod (the pool half of the same IncPARHelper as Veigar's regen percent): the pool of the
        // kind scales by 1 + the script's percent, whose flat twin (BBIncFlatPARPoolMod, Rod of Ages' and Tear of the
        // Goddess' row) is already part of the pool above.
        maxMp *= Math.Max(0.0, 1.0 + Active(payload => payload.MaxMpMult));
        maxMp = Math.Max(0.0, maxMp);
        // BBIncPercentPARRegenMod (Veigar's Equilibrium): the resource regeneration scales by 1 + the script's percent.
        mpRegen *= Math.Max(0.0, 1.0 + Active(payload => payload.MpRegenMult));

        // No shipped item record carries FlatGoldPer10Mod (only runes carry rFlatGoldPer10Mod), so a gold item's
        // income is its window buff's own BBIncStat IncFlatGoldPer10Mod (Items/3093, 3096, 3098, 3132; batch 149
        // step 5) and arrives with the other script rows below.
        var goldPer10 = Modifier(modifiers, "FlatGoldPer10Mod") +
                        Modifier(modifiers, "rFlatGoldPer10Mod") +
                        mastery.Economy.GoldPer10Bonus + Active(payload => payload.GoldPer10);
        var experiencePercent = Modifier(modifiers, "PercentEXPBonus") +
                                mastery.Economy.ExperiencePercentBonus + Active(payload => payload.ExperiencePercent);
        var timeDeadPercent = Modifier(modifiers, "rPercentTimeDeadMod") +
                              Modifier(modifiers, "rPercentTimeDeadModPerLevel") * level +
                              mastery.Economy.RespawnTimePercent +
                              // Batch 159 part 3: the team-size catch-up's 0.1 x T respawn ramp is
                              // Spells/PositiveChampionDelta.luaobj's own IncPercentRespawnTimeMod row, so it arrives here with
                              // the other buff payloads. The hand-written TeamSizeCatchUp reader that stood in for it is gone.
                              Active(payload => payload.RespawnTimePercent);

        return new HeroStats(maxHp, maxMp, attackDamage, armor, spellBlock, hpRegen, mpRegen,
            attackSpeed, moveSpeed, attackRange, abilityPower, dodge, critChance, critMultiplier,
            cooldownMod, hardness, dodgePiercing, armorPenFlat, armorPenPercent, magicPenFlat,
            magicPenPercent, lifeSteal, spellVamp)
        {
            GoldPer10Mod = goldPer10,
            FlatMovementSpeedMod = ItemTotal("FlatMovementSpeedMod") + Active(payload => payload.MoveSpeedFlat),
            // TT-01: script GetFlatPhysicalDamageMod excludes the percentage
            // contribution, but includes the flat script/item conversions above.
            FlatPhysicalDamageMod = attackDamage - baseStats.AttackDamage -
                (baseStats.AttackDamage + ItemTotal("FlatPhysicalDamageMod")) * ItemTotal("PercentPhysicalDamageMod"),
            ExperiencePercent = experiencePercent,
            TimeDeadPercent = timeDeadPercent,
            // Items, runes, masteries and permanent modifiers; no shipped record or script names it (batch 331 scan), so it is 0 in shipped play.
            FlatCastRangeMod = ItemTotal("FlatCastRangeMod"),
            // The client's mFlatHPPoolMod: the flat health every source adds before the percent step (items, runes, masteries, buffs, the permanent
            // modifiers and every script-owned growth row), without the level growth of the base. The script
            // buffs' own flat pool rows (LeviathanStats' IncFlatHPPoolMod and Rod of Ages' own among them) arrive
            // through the buff payload sum above. The non-healing pool is not part of it: BBIncMaxHealth with IncCurrentHealth false adds to
            // m_NonHealingFlatHPPoolMod and the script helper's flat health pool getter returns
            // mFlatHPPoolMod alone, so Vladimir's Blood Gorged never turns its own health back into ability power.
            FlatHpPoolMod = Modifier(modifiers, "FlatHPPoolMod") + Modifier(modifiers, "rFlatHPModPerLevel") * level +
                            Active(payload => payload.MaxHp - payload.NonHealingMaxHp),
            EnergyPool = usesEnergy ? energyPool : 0.0,
            EnergyRegen = usesEnergy ? energyRegen : 0.0,
            MasteryContribution = mastery
        };
    }

    private static double Modifier(IReadOnlyDictionary<string, double> modifiers, string name) =>
        modifiers.TryGetValue(name, out var value) ? value : 0.0;
}
