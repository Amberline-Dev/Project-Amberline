using System;
using System.Collections.Generic;
using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Simulation.Buffs;

/// <summary>
/// Cross-system dependencies required by the buff lifecycle. The lifecycle
/// does not manufacture no-op services: a match orchestrator must supply each
/// boundary so control, combat, effects, and item hooks cannot disappear
/// silently during migration.
/// </summary>
public interface IBuffServices
{
    ICombatBuffHooks Combat { get; }
    ICastBuffHooks Casts { get; }
    IChampionBuffHooks Champions { get; }
    IBuffEffectHooks Effects { get; }
    IItemBuffHooks Items { get; }
}

public interface ICombatBuffHooks
{
    bool IsPendingActionBlocked(Entity unit);
    double HealingMultiplier(Entity unit);
    void InterruptBasicAttack(Entity unit);
    // Legacy hooks without a cancellation result cannot justify a client stop.
    void InterruptBasicAttack(Entity unit, out bool cancelledWindup)
    {
        InterruptBasicAttack(unit);
        cancelledWindup = false;
    }
    void ApplyPeriodicDamage(PeriodicDamageRequest request);
    void Kill(Entity unit, uint killerNetId = 0);
}

public readonly record struct PeriodicDamageRequest(
    Entity Target,
    uint SourceNetId,
    uint TargetNetId,
    double Amount,
    DamageType DamageType,
    DamageSourceKind? SourceKind,
    uint SpellOwnerNetId,
    bool SourceIsPeriodic,
    Amberline.Simulation.Combat.DeathRecapOrigin? RecapOrigin = null);

public interface ICastBuffHooks
{
    void CancelPendingCast(Entity hero);
}

public interface IChampionBuffHooks
{
    void OnBuffTick(Entity unit, BuffState buff, double dt);
    void OnBuffRemoved(Entity unit, BuffState buff);

    /// <summary>BuffOnAllowAdd of the script buffs on <paramref name="unit"/>: false refuses the add, and <paramref name="duration"/> comes back as the
    /// hooks left it (Judgment halves a slow). Asked by every add, a script's or not.</summary>
    bool AllowBuffAdd(Entity unit, string name, BuffType type, ref double duration, Entity adder, int? maxStack) => true;

    /// <summary>Whether a spell shield of <paramref name="unit"/> has popped and not left yet (its script's WillRemove; see HeroItemState.SpellShieldPopped).</summary>
    bool SpellShieldPopped(Entity unit) => false;

    /// <summary>The timer of an <see cref="BuffType.AmmoStack"/> buff ran out: its script's <c>BuffOnUpdateAmmo</c> adds the next charge (E14).</summary>
    void OnAmmoTimer(Entity unit, BuffState buff) { }
}

public interface IBuffEffectHooks
{
    bool IsWireReady(Entity unit);
    bool SendBuffAdd(Entity unit, BuffState buff, int count);
    bool SendBuffRemove(Entity unit, BuffState buff);
    void SendDamageShieldDelta(Entity unit, double amount);
    void SendAttackSpeed(Entity unit);
    void SendMovementSpeed(Entity unit);
    void EnsureBuffFx(Entity unit, BuffState buff);
    void KillSpellEffect(uint effectNetId);
    void RemovePerceptionBubble(Entity unit, uint bubbleId);
    void SendWaypoints(Entity unit);
    void PauseAnimation(Entity unit, bool paused);
    void SendInstantStopAttack(Entity unit);
    void CompleteGuardianRevival(Entity unit) { }
}

public interface IItemBuffHooks
{
    bool HasItem(Entity unit, int itemId);
    void SyncItemSlow(Entity target);
}
