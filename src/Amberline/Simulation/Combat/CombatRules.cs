using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Combat;

/// <summary>Pure combat formulae shared by live combat and deterministic checks.</summary>
public static class CombatRules
{
    public static double ResistanceMultiplier(double resistance)
    {
        if (!double.IsFinite(resistance))
            throw new ArgumentOutOfRangeException(nameof(resistance));
        return resistance >= 0.0
            ? 100.0 / (100.0 + resistance)
            : 2.0 - 100.0 / (100.0 - resistance);
    }

    /// <summary>
    /// Client object class for the shipped dr_* table. Every obj_AI_Minion is a
    /// Unit: lane minions, jungle monsters and everything SpawnMinion/SpawnPet
    /// creates (the pet spawn calls the minion creation), which is
    /// this server's UnitType.Minion and UnitType.Monster. Turrets, inhibitors
    /// and the nexus all read the "Building" field.
    /// Reconstruction: the server routine that calls the getters is not shipped, so
    /// pets taking the Unit row (Shaco clone, Tibbers, ghouls) rests on the client
    /// class alone; exempt them here if native play shows otherwise.
    /// </summary>
    public static DamageClass ClassOf(Entity entity) => entity.UnitType switch
    {
        UnitType.Hero => DamageClass.Hero,
        UnitType.Turret or UnitType.Structure => DamageClass.Building,
        _ => DamageClass.Unit,
    };

    /// <summary>
    /// Shipped class-to-class basic-attack ratio (Map1 Constants.var dr_*).
    /// Only the basic-attack path reads it: the native getters are named
    /// GetAttackRatioWhenAttacking*, so spells and script damage are exempt.
    /// </summary>
    public static double AttackRatio(Entity attacker, Entity target, DamageRatios? ratios = null) =>
        (ratios ?? GameData.Default.LevelConstants.DamageRatios).Ratio(ClassOf(attacker), ClassOf(target));

    public static BasicAttackResult BasicAttackDamage(
        Entity attacker,
        Entity target,
        AttackRolls rolls,
        DamageRatios? ratios = null)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        rolls.Validate();
        // Reconstruction: the native routine that multiplies the ratio is not shipped. It is
        // modelled as a plain raw-damage factor applied once, before the script on-hit
        // scalars, turret ramp and mitigation. The scripts are compensations built around it:
        // 0.7 x 1.43 is about 1.0 for the monsters and 0.5 x 2 is 1.0 for the cannon minion
        // against turrets. Those scalars are the character scripts' own now (batch 159 part 2:
        // CharOnHitUnit of CharScriptGolem and its nine siblings, CharOnHitUnit of
        // CharScriptBlue_Minion_MechCannon 0039-0063), so they no longer sit here.
        var ratio = AttackRatio(attacker, target, ratios);

        var missChance = attacker.Buffs
            .Where(buff => buff.Payload.Has(Amberline.Core.Buffs.BuffPayloadField.MissChance))
            .Sum(buff => buff.Payload.MissChance);
        missChance = Math.Clamp(missChance, 0.0, 1.0);
        if (rolls.Miss < missChance) return BasicAttackResult.Miss(false);

        var dodgeChance = Math.Clamp(target.Dodge, 0.0, 1.0);
        if (!attacker.DodgePiercing && rolls.Dodge < dodgeChance)
            return BasicAttackResult.Miss(true);

        // The stock hero ComputeCriticalSuccess only rolls against
        // enemy heroes or AI minions. The base/minion routine is separate.
        var critical = CanCriticallyStrike(attacker, target) && rolls.Crit < Math.Clamp(attacker.CritChance, 0.0, 1.0);
        var damage = attacker.AttackDamage * (critical ? attacker.CritMultiplier : 1.0) * ratio;
        return BasicAttackResult.Hit(damage, critical);
    }

    public static bool CanCriticallyStrike(Entity attacker, Entity target) =>
        attacker.UnitType != UnitType.Hero || target.UnitType is UnitType.Minion or UnitType.Monster ||
        (target.UnitType == UnitType.Hero && WorldState.IsEnemy(attacker, target));

    // A BuildingBlocks script's attack override (Vayne's Tumble bonus, Kayle's Righteous Fury, Nidalee's Takedown) is Entity.ScriptAttackOverride.
    public static bool HasAttackOverride(Entity attacker) => attacker.ScriptAttackOverride is not null;

}
