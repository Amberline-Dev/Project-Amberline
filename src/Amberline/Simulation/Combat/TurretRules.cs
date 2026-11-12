using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Combat;

public readonly record struct TurretStaticStats(
    double MaxHealth,
    double Damage,
    double Armor,
    double SpellBlock,
    double HealthRegen,
    double AttackRange,
    double MissileSpeed,
    double Windup,
    double AttackPeriod)
{
    /// <summary>CharacterData GlobalExpGivenOnDeath: outer 30, inner 50, inhibitor 70, nexus 120.</summary>
    public double GlobalExperience { get; init; }
    public double AcquisitionRange { get; init; } = AttackRange;
    /// <summary>
    /// Cast fraction of the attack period (0.3 + AttackDelayCastOffsetPercent, floored at 0). Null derives it from
    /// <see cref="Windup"/> / <see cref="AttackPeriod"/>.
    /// </summary>
    public double? CastFraction { get; init; }
}

/// <summary>Static, innate and timed Map1 turret rules from the shipped character scripts.</summary>
public static class TurretRules
{
    // Spells/TurretBonus.luaobj BuffOnUpdateStats, one line per constant (the growth a turret's own buff stacks).
    public const double BonusDamage = 6.0;
    public const double BonusArmor = 1.5;
    public const double BonusSpellBlock = 1.5;
    public const double BonusInterval = 60.0;
    public const double BackdoorResistance = 150.0;
    // OrderTurretShrineBasicAttack / ChaosTurretShrineBasicAttack TargetExecute (identical in the scripts): BBApplyDamage Damage 1000 TRUE_DAMAGE,
    // as DAMAGESOURCE_INTERNALRAW on a champion and DAMAGESOURCE_RAW otherwise. PercentOfAttack 1 adds nothing to a true-damage call (batch 227).
    public const double ShrineDamage = 1000.0;
    // Global attack delay clamp (gcd_AttackMinDelay / gcd_AttackMaxDelay in Map1/Constants.var).
    private const double MinAttackDelay = 0.4;
    private const double MaxAttackDelay = 5.0;

    public static bool IsLaneTurret(Entity unit) => unit.UnitType == UnitType.Turret &&
        MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier != TurretTier.Fountain;

    public static bool IsFountainTurret(Entity unit) => unit.UnitType == UnitType.Turret &&
        MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier == TurretTier.Fountain;

    /// <summary>
    /// A turret that fires a shrine spell's TargetExecute (<see cref="ShrineDamage"/> true damage) and sees past the turret sight test: Summoner's
    /// Rift's two fountain shrines, and every turret CreateLevelProps makes on The Crystal Scar (its four base shrines, towerIds 1 and 2, which the
    /// tier table reads as Inhibitor and Inner). Reconstruction (batch 528): the Odin shrines ship no attack spell of their own and attack with
    /// their team's shrine spell (batch 443). The owner's video of a later patch shows them dealing 1000 true damage, as on Summoner's Rift, and
    /// the wiki's patch history from V1.0.0.126 on records no change to them. The owner also asked for the Summoner's Rift fountain's sight
    /// exemption on them. The fountain-only rules stay on <see cref="IsFountainTurret"/>, for example the perception-bubble reach cap: the Odin
    /// shrines keep their shipped 1600 attack range and 2250 acquisition range. Batch 665: it is the wire's shrine too (VisibilityRules.IsShrine): untargetable
    /// to both teams (the shipped CharScriptOdinOrderTurretShrine / CharScriptOdinChaosTurretShrine set SetTargetable false) and no 0xB5, so no health bar.
    /// </summary>
    public static bool IsShrineTurret(Entity unit) => unit.UnitType == UnitType.Turret &&
        MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && (tier == TurretTier.Fountain || MapDefinitions.OnCrystalScar);

    /// <summary>One of The Crystal Scar's four base shrines: a <see cref="IsShrineTurret"/> unit on map 8 (attack 1600, acquisition 2250).</summary>
    public static bool IsCrystalScarShrine(Entity unit) => MapDefinitions.OnCrystalScar && IsShrineTurret(unit);

    /// <summary>
    /// The range a turret takes for an absent Data*AttackRange (the acquisition range defaults to the same value). Reconstruction:
    /// retail's CharacterData default for a missing key is not in the shipped files. 750 is the lane default the hand-written
    /// table used to hold, and the shrine's 2250 is its shipped value (batch 204, the official notes of 2 September 2009). Every
    /// Map1 turret character carries both keys, so the default is never read with the shipped inibins
    /// (combat.turret-range-keys-shipped).
    /// </summary>
    private static double AbsentRangeDefault(TurretTier tier) => tier == TurretTier.Fountain ? 2250.0 : 750.0;

    public static TurretStaticStats StatsFor(TurretTier tier, Team team, GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        // The character is CreateLevelProps' own: each (tier, team) pair the script creates carries
        // exactly one character name.
        var character = MapDefinitions.TurretCharacter(tier, team);
        var champion = gameData.Champion(character);
        // Every field below is read from the shipped character data; the shrine's ranges are read the way the lane turrets read theirs.
        double Number(string field, double defaultValue) =>
            champion.Raw.TryGetValue(InibinReader.KeyHash("Data*" + field), out var value) &&
            (value.IsNumeric || value.Kind == InibinValueKind.Boolean)
                ? value.AsClientNumber() : defaultValue;
        // The client delay helpers clamp the period to 0.4..5 s. The shrine's AttackDelayOffsetPercent is stored as the 16-bit value
        // 65535, which the stock client sign-extends to -1 (the client's raw integer read): raw period 0, clamped to 0.4 s; cast
        // fraction 0.3 + 0.1 = 0.4, so windup 0.16 s. All ten turret inibins carry both offsets; an absent key reads the CharacterData
        // loader's default 0.
        var basePeriod = Math.Clamp(1.6 * (1 + Number("AttackDelayOffsetPercent", 0.0)),
            MinAttackDelay, MaxAttackDelay);
        var castFraction = Math.Max(0, 0.3 + Number("AttackDelayCastOffsetPercent", 0.0));
        // Reconstruction: The Crystal Scar's shrines (OdinOrderTurretShrine, OdinChaosTurretShrine) ship no <character>BasicAttack spell. Their
        // character data is the Summoner's Rift shrine's (batch 443), so they attack with the shrine spell of their team. Summoner's Rift's turrets
        // all ship their own spell and never take the fallback.
        var attackSpell = gameData.Spell(gameData.HasSpell(character + "BasicAttack") ? character + "BasicAttack"
            : team == Team.Order ? "OrderTurretShrineBasicAttack" : "ChaosTurretShrineBasicAttack");
        return new TurretStaticStats(
            MaxHealth: champion.BaseHp, Damage: champion.BaseDamage, Armor: champion.Armor, SpellBlock: champion.SpellBlock,
            HealthRegen: champion.HpRegen,
            // The shipped attack range (Data*AttackRange) and acquisition range (Data*AcquisitionRange, which drives
            // FindTargetInAcR), for every tier including the fountain (batch 204).
            AttackRange: Number("AttackRange", AbsentRangeDefault(tier)),
            // The client flies a missile only for CastType 1, 2 and 3 (BbChampionModule.CastType; case 0 runs the effect at the cast frame), the
            // rule a hero's attack reads (HeroAttackTiming). Batch 527: both shrine spells are CastType 0, so their stored MissileSpeed 500 is
            // unused data; read as a flight it landed the map 8 shrine's hit about 2 s after its shot, 430 units from where the laser held the
            // hero. The lane turrets' spells are CastType 1 and keep their flight.
            MissileSpeed: Amberline.Simulation.Spells.Bb.BbChampionModule.CastType(attackSpell) is 1 or 2 or 3 ? attackSpell.MissileSpeed : 0.0,
            Windup: basePeriod * castFraction, AttackPeriod: basePeriod)
        {
            GlobalExperience = champion.GlobalExpGivenOnDeath,
            AcquisitionRange = Number("AcquisitionRange", AbsentRangeDefault(tier)),
            CastFraction = castFraction,
        };
    }

    public static (double Start, double Duration) ScalingWindow(TurretTier tier) => tier switch
    {
        TurretTier.Outer => (0.0, 480.1),
        TurretTier.Nexus => (0.0, 2280.1),
        TurretTier.Inner => (420.0, 660.1),
        TurretTier.Inhibitor => (960.0, 1200.1),
        _ => (0.0, 0.0),
    };

    /// <summary>
    /// How many <see cref="BonusInterval"/> lines of Spells/TurretBonus.luaobj's growth a turret of that window has
    /// seen at <paramref name="now"/>. The hand-written form of the script's own mutation clock, kept as a data
    /// reader for the corpora that pin its shape; a live turret accumulates its own permanents and does not read this.
    /// </summary>
    public static int BonusStacks(double start, double duration, double now)
    {
        if (!double.IsFinite(start) || !double.IsFinite(duration) || !double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (duration <= 0.0 || now <= start) return 0;
        var elapsed = Math.Min(now - start, duration);
        return Math.Max(0, (int)Math.Floor((elapsed + 1e-9) / BonusInterval));
    }

    public static int BonusStacks(TurretTier tier, double now)
    {
        var (start, duration) = ScalingWindow(tier);
        return BonusStacks(start, duration, now);
    }

    /// <summary>
    /// Spells/TurretBonusHealth.luaobj's one-shot: the add's NextBuffVars.BonusHealth per enemy champion (250 for the
    /// eight lane characters, 125 for the two nexus ones, 0 for the shrine), capped at five. The hand-written form of
    /// the same rule, for a unit no character script claims.
    /// </summary>
    public static double HealthBonus(TurretTier tier, int enemyHeroCount) => tier switch
    {
        TurretTier.Fountain => 0.0,
        TurretTier.Nexus => 125.0 * Math.Min(5, Math.Max(0, enemyHeroCount)),
        _ => 250.0 * Math.Min(5, Math.Max(0, enemyHeroCount)),
    };

    /// <summary>
    /// Seeds one turret's own stats. With <paramref name="scriptOwned"/> the shipped character script is the unit's
    /// (batch 159 step 2: <c>CharScript&lt;turret&gt;.luaobj</c> runs at the character's creation and adds its own
    /// buffs), so every amount the script states is left to the script and only the raw inibin values are written
    /// here — the same starting point the script's permanents run on. Without it the hand-written model applies the
    /// same amounts itself (a check that builds a turret without a match).
    /// </summary>
    public static void Initialize(Entity turret, int enemyHeroCount, TurretStaticStats selected, bool scriptOwned = false)
    {
        ArgumentNullException.ThrowIfNull(turret);
        var tier = MapDefinitions.TurretTiers.TryGetValue(turret.Name, out var value) ? value : TurretTier.Outer;
        // TurretAssistManager's +150 and PersonalTurretAssistBonus's +25, which both activate once. The scripts add
        // them at activation (Spells/TurretAssistManager.luaobj OnBuffActivate and Spells/PersonalTurretAssistBonus.
        // luaobj OnBuffActivate); this base records what the unit is after them, which is what the timed growth and
        // the backdoor rule stack on.
        var resistanceBonus = tier == TurretTier.Fountain ? 0 : BackdoorResistance + 25;
        // TurretBonusHealth's one-shot (Spells/TurretBonusHealth.luaobj, run at the add of every turret character's
        // TurretBonusHealth): the add's own NextBuffVars.BonusHealth per enemy champion, capped at five. The script
        // applies it; the hand-written form is this function.
        var maxHealth = scriptOwned ? selected.MaxHealth : selected.MaxHealth + HealthBonus(tier, enemyHeroCount);
        turret.TurretBaseDamage = selected.Damage;
        turret.TurretBaseArmor = selected.Armor + resistanceBonus;
        turret.TurretBaseSpellBlock = selected.SpellBlock + resistanceBonus;
        var (start, duration) = ScalingWindow(tier);
        turret.TurretScalingStart = start;
        turret.TurretScalingDuration = duration;
        turret.TurretScalingStacks = 0;
        turret.TurretLastRegenSend = -1.0;
        turret.MaxHealth = maxHealth;
        turret.Health = maxHealth;
        turret.AttackDamage = selected.Damage;
        // The raw inibin defenses while the script owns the unit: TurretAssistManager's IncPermanentFlat*Mod 150 and
        // PersonalTurretAssistBonus's 25 raise them from here at activation.
        turret.Armor = scriptOwned ? selected.Armor : turret.TurretBaseArmor;
        turret.SpellBlock = scriptOwned ? selected.SpellBlock : turret.TurretBaseSpellBlock;
        turret.HealthRegen = selected.HealthRegen;
        turret.AttackRange = selected.AttackRange;
        turret.AcquisitionRange = selected.AcquisitionRange;
        turret.MissileSpeed = selected.MissileSpeed;
        // Every turret starts at the multiplier 1. Reconstruction (batch 759, "Match the footage"): PersonalTurretAssistBonus's
        // IncPermanentPercentAttackSpeedMod 0.3 does not apply (BbSpellHost.PermanentOnPet), so the hand-written form adds none either
        turret.AttackSpeedMod = 1.0;
        var castFraction = selected.CastFraction ??
            (selected.AttackPeriod > 0.0 ? selected.Windup / selected.AttackPeriod : 0.0);
        turret.AttackPeriod = Math.Clamp(selected.AttackPeriod / turret.AttackSpeedMod, MinAttackDelay, MaxAttackDelay);
        turret.AttackWindup = selected.Windup / turret.AttackSpeedMod;
        turret.AttackTimingBaseline = new AttackTimingBaseline(
            turret.AttackSpeedMod, selected.AttackPeriod, castFraction);
        // All eight lane-turret scripts grant these; shrine spells are separate. A script-owned unit gets them from
        // its own CharScript: IncPercentArmorPenetrationMod 0.2 of the UpdateSelfBuffStats sample and the
        // BBSetDodgePiercing(true) of CharOnActivate.
        if (!scriptOwned)
        {
            turret.ArmorPenetrationPercent = tier == TurretTier.Fountain ? 0.0 : 0.2;
            turret.DodgePiercing = tier != TurretTier.Fountain;
        }
        turret.GlobalExperienceReward = selected.GlobalExperience;
    }

    public static void Initialize(Entity turret, int enemyHeroCount, GameData gameData, bool scriptOwned = false)
    {
        ArgumentNullException.ThrowIfNull(turret);
        ArgumentNullException.ThrowIfNull(gameData);
        var tier = MapDefinitions.TurretTiers.TryGetValue(turret.Name, out var value) ? value : TurretTier.Outer;
        Initialize(turret, enemyHeroCount, StatsFor(tier, turret.Team, gameData), scriptOwned);
    }

    public static bool Tick(Entity turret, double now, double dt, bool applyRegen = true)
    {
        ArgumentNullException.ThrowIfNull(turret);
        if (!turret.Alive || turret.UnitType != UnitType.Turret) return false;
        // Batch 159 part 1: a turret grows through its own TurretBonus buff and nothing else — the three
        // BBIncPermanentStat of Spells/TurretBonus.luaobj's BuffOnUpdateStats under the add's TickRate 60, on the
        // mutation clock batch 159 step 1 gave a bare permanent. The hand-written stack counter this tick used to
        // apply is deleted with the rule it stood for; BonusStacks and ScalingWindow stay as the shape of that script
        // for the corpora that pin it (session-spawn-corpus's turretScaling rows, combat.turret.rules).
        var changed = false;
        if (dt < 0.0 || !double.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt));
        if (applyRegen && turret.HealthRegen > 0.0 && turret.Health < turret.MaxHealth)
        {
            var oldHealth = turret.Health;
            turret.Health = Math.Min(turret.MaxHealth, turret.Health + turret.HealthRegen * dt);
            changed |= turret.Health > oldHealth;
        }

        return changed;
    }
}
