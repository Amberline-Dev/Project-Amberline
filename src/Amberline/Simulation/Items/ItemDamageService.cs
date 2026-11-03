using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Items;

/// <summary>Item damage hooks executed at the combat pipeline boundaries.</summary>
public sealed class ItemDamageService
{
    private readonly WorldState _world;
    private readonly BuffLifecycle _buffs;
    private readonly ItemPassiveService _passives;
    private readonly ISpellEffectSink _effects;

    public ItemDamageService(WorldState world, BuffLifecycle buffs, IBuffEffectHooks buffEffects,
        ItemPassiveService passives, ISpellCombatService combat, ISpellEffectSink effects)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        ArgumentNullException.ThrowIfNull(buffEffects);
        _passives = passives ?? throw new ArgumentNullException(nameof(passives));
        ArgumentNullException.ThrowIfNull(combat);
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    /// <summary>
    /// The hand-written pop of a BansheesVeil that no script owns. The item script raises the veil as a script instance, and that
    /// instance pops itself (BuffOnBeingSpellHit, then its BuffOnUpdateStats at the 0.25 s poll), so this gate answers only a veil
    /// without one: it takes the buff away at once and adds the forty-five second timer. NOT RETAIL: no retail veil lacks its script.
    /// </summary>
    public bool ConsumeSpellShield(Entity? attacker, Entity target, DamageSourceKind? sourceKind,
        bool force = false)
    {
        if (!force && sourceKind is not (DamageSourceKind.Spell or DamageSourceKind.SpellAoe or DamageSourceKind.SpellPersist)) return false;
        if (attacker is null || attacker.Team == target.Team) return false;
        var shield = target.Buffs.FirstOrDefault(buff => buff.Name == "BansheesVeil");
        if (shield is null) return false;
        target.ItemState.SpellShieldPopped = true;
        _buffs.RemoveBuff(target, shield.Slot);
        _effects.CreateEffect("SpellEffect_proc.troy", target.Position,
            targetNetId: target.NetId, bindNetId: target.NetId);
        _buffs.AddBuff(target, "BansheesVeilTimer", BuffType.Internal, 45.0,
            target.NetId, canMitigateDuration: false);
        return true;
    }

    public double PreprocessItemDamage(Entity? attacker, Entity target, double damage,
        DamageType damageType, DamageSourceKind? sourceKind)
    {
        if (_world.Get(target.NetId) is null)
            throw new InvalidOperationException($"item damage target 0x{target.NetId:X8} is not in this world");
        if (!double.IsFinite(damage) || damage < 0.0) throw new ArgumentOutOfRangeException(nameof(damage));

        // Batch 147 step 4: the Mobility boots penalty is Items/3117.luaobj's own ItemOnPreDamage /
        // ItemOnPreDealDamage and Hexdrinker's shield is Items/3155.luaobj's ItemOnPreDamage (the trigger) plus
        // Spells/Hexdrinker.luaobj's BuffOnPreDamage (the absorb). Batch 149 step 5 took the last body here too:
        // Guardian Angel's revival is Spells/WillRevive.luaobj's own BuffOnPreDamage (the damage becomes
        // Health - 1) and Spells/GuardianAngel.luaobj's OnBuffActivate (the Death animation, the status set, the
        // cleared-by-type list, the summoner-cooldown floor) and OnBuffDeactivate (the 750 / 375 heal and the
        // HasBeenRevived marker), which Items/3026.luaobj's UpdateSelfBuffActions keeps alive.
        return damage;
    }

    public void OnUnitDamaged(Entity? attacker, Entity target, double damage, double rawDamage,
        DamageSourceKind? sourceKind, bool reflected, bool sourceIsPeriodic)
    {
        // Recall's cancel on damage is Spells/Recall.luaobj's own BuffOnTakeDamage (a non-periodic hit sets WillRemove, and its
        // BuffOnUpdateActions stops the channel); batch 179 removed the hand-written cancel that stood here.
        // Batch 149 step 5: Thornmail's reflection is Spells/Thornmail.luaobj's own BuffOnBeingHit — the window
        // Items/3075.luaobj keeps alive — which applies DamageAmount * 0.3 back at the attacker as
        // DAMAGESOURCE_REACTIVE with its own Thornmail_tar.troy.
    }

    public void OnBasicAttackHit(Entity attacker, Entity target, double rawDamage,
        DamageType damageType) => _passives.OnBasicAttackHit(attacker, target, rawDamage, damageType);

}
