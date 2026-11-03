using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>Runs the authoritative spell-shield gate for scripted effects.</summary>
    public bool ConsumeSpellShield(Entity source, Entity target,
        DamageSourceKind? sourceKind = DamageSourceKind.Spell) =>
        ConsumeSpellShield(source, target, sourceKind, force: false);

    /// <summary>
    /// Explicit shield consumption for scripted effects.  The source-kind
    /// remains part of the combat hook so Banshee checks and attribution keep
    /// the same boundary as ordinary spell damage.
    /// </summary>
    public bool ConsumeSpellShield(Entity source, Entity target,
        DamageSourceKind? sourceKind, bool force)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // A spell hit reaches a unit that can be hit at all: ApplyHit refuses the others (dead, protected, invulnerable, untargetable)
        // before it looks at a shield, so a hit that no damage could follow does not spend one either. A forced break is the script's
        // explicit BBBreakSpellShields and runs whatever follows.
        if (!force && !(target.Alive && target.Health > 0.0 && !target.Protected && !IsInvulnerable(target) &&
                        !_hooks.IsGodMode(target) && _hooks.IsTargetable(target, null))) return false;
        return _hooks.ConsumeSpellShield(source, target, sourceKind, force);
    }

    public bool ConsumeSpellShield(Entity source, Entity target, bool force) =>
        ConsumeSpellShield(source, target, DamageSourceKind.Spell, force);

    public static double ResistanceMultiplier(double resistance) => CombatRules.ResistanceMultiplier(resistance);

    // Reconstruction: the server's armor/magic-resist routine is not in the 126 client (the base unit's damage-reduction stage and
    // the other damage stages are empty stubs there). The multiplier 100/(100+R) (2-100/(100-R) below zero) and the
    // penetration order rest on the period statements in patch126-partial-rules-evidence.md section 4.
    private static double ApplyMitigation(double amount, Entity target, Entity? source, DamageType type)
    {
        if (type == DamageType.True) return amount;
        var resistance = type == DamageType.Physical ? target.Armor : target.SpellBlock;
        if (source is not null)
        {
            if (type == DamageType.Physical)
            {
                // Riot's April 2011 rule: flat before percent; only reduction can make armor negative.
                if (resistance > 0.0)
                    resistance = Math.Max(0.0, resistance - source.ArmorPenetrationFlat) *
                        (1.0 - source.ArmorPenetrationPercent);
            }
            else if (resistance > 0.0)
            {
                // Preserve naturally negative spell block. Penetration only
                // lowers a positive resistance and cannot manufacture a
                // negative value from a valid positive stat. Patch 1.0.0.126
                // predates the Season 3 pre-season change, so magic
                // penetration is flat first, then percent, exactly like
                // armor above (surrenderat20 April 2011 and the mobafire
                // theory-crafting thread 8307 quoting the 2011 wiki;
                // patch126-partial-rules-evidence.md section 4).
                var percent = Math.Clamp(source.MagicPenetrationPercent, 0.0, 1.0);
                var flat = Math.Max(0.0, source.MagicPenetrationFlat);
                resistance = Math.Max(0.0, resistance - flat) * (1.0 - percent);
            }
        }
        return amount * CombatRules.ResistanceMultiplier(resistance);
    }

    /// <summary>
    /// The damage reduction stats of the holder's script buffs (<c>IncFlat/PercentPhysical/MagicReduction</c>: Amumu's Tantrum takes
    /// 2 per rank off each physical hit, the jungle Worm's WrathDebuff puts 15 per stack on each magic hit, Ferocious Howl, Garen's
    /// Command and Hemoplague move a percentage). Evidence: the client holds the four <c>CharacterIntermediate</c> accumulators
    /// (<c>mFlatPhysicalReduction</c>, <c>mPercentPhysicalReduction</c>, <c>mFlatMagicReduction</c>, <c>mPercentMagicReduction</c>,
    /// ) and the Lua accessors that fill them. The client holds no <c>ReduceDamage</c> name at all (the stage
    /// a stage of that name appears only in another build, which is not evidence for this client), so placing
    /// the stats in the stage of <see cref="ApplyMitigation"/> rests on the accumulators' names. Reconstruction: the server body
    /// is not recovered, so the order is a guess: after armor or spell block and the turret delta, the percentage first, then the flat
    /// amount, floored at zero; true damage is not reduced; a negative value adds damage (WrathDebuff's tooltip reads "increased
    /// Magical Damage taken"). The tooltips read per hit ("takes @Effect1Amount@ reduced damage from physical attacks").
    /// </summary>
    private static double ApplyReduction(Entity target, double amount, DamageType type)
    {
        if (type == DamageType.True) return amount;
        double flat = 0.0, percent = 0.0;
        foreach (var buff in target.Buffs)
        {
            var payload = buff.Payload;
            if (type == DamageType.Physical)
            {
                flat += payload.FlatPhysicalReduction;
                percent += payload.PercentPhysicalReduction;
            }
            else
            {
                flat += payload.FlatMagicReduction;
                percent += payload.PercentMagicReduction;
            }
        }
        if (percent != 0.0) amount *= Math.Max(0.0, 1.0 - percent);
        return flat == 0.0 ? amount : Math.Max(0.0, amount - flat);
    }

    private double ConsumeShields(Entity target, double amount)
    {
        foreach (var buff in target.Buffs.ToArray())
        {
            if (amount <= 0.0 || buff.Payload.DamageShield <= 0.0) continue;
            var absorbed = Math.Min(amount, buff.Payload.DamageShield);
            buff.Payload.DamageShield -= absorbed;
            var remove = buff.Payload.DamageShield <= 0.0;
            _hooks.OnDamageShieldChanged(target, buff, -absorbed, remove);
            if (remove) _hooks.RemoveBuff(target, buff.Slot);
            amount -= absorbed;
        }
        return amount;
    }
}
