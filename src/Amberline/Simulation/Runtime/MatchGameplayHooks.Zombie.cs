using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Runtime;

/// <summary>
/// The engine side of the zombie life and of the hits of Yorick's pets (Phase 3, 19 October 2026): what the stock server does around the
/// shipped scripts. A script buff decides that a death becomes a zombie life (<c>BuffOnDeath</c> sets <c>BecomeZombie</c>) and runs the
/// life itself (<c>BuffOnZombie</c>, <c>YorickRADelay</c>, <c>YorickRAZombie</c>); the unit-level transition and the end of the life
/// (<c>BBForceDead</c>) are here. The hand-written <c>YorickSpellService</c> that did all of it is gone.
/// </summary>
public sealed partial class MatchGameplayHooks
{
    // DIAGNOSTICS, the lifecycle monitor only: how long a zombie life may last at most (the one-second delay and the ten-second zombie buff of the
    // shipped scripts) before it counts as stuck. The scripts own the real end.
    private const double ZombieNominalSeconds = 11.0;

    public bool DeathBecomesZombie(Entity unit, uint killerNetId) =>
        Sinks?.OnDeathBecomesZombie(unit, killerNetId) == true;

    /// <summary>
    /// The unit that died into a zombie life (<c>BuffOnDeath</c> set <c>BecomeZombie</c>): it keeps the original, counted death and its respawn
    /// deadline, and stands up again for the script to take over (<c>BuffOnZombie</c>). Reconstruction: the stock server's zombie transition is
    /// not in the client; the unit is alive with one health point (YorickRAZombie heals it to full when its delay ends), its orders, route,
    /// windups and pending casts are gone, it stops recalling, and <c>BuffOnZombie</c> runs once it is alive (the hand-written port swept the
    /// buffs while the unit was still dead; the scripts sweep by type, which does not depend on it). The unit keeps every buff it died with
    /// (batch 352: the death runs no clear, <see cref="EndZombieLife"/> does), so <c>YorickReviveAllySelf</c>'s sweep finds them.
    /// </summary>
    public void BeginZombie(Entity unit, uint killerNetId)
    {
        unit.YorickRevival = new YorickRevivalState(Combat.CurrentTime + ZombieNominalSeconds);
        // Revival cancels only positive-delay attacks owned by the unit; already-launched projectiles remain in the match queue.
        Combat.CancelPendingWindups(unit.NetId);
        unit.Alive = true;
        unit.Health = 1.0;
        unit.AttackTarget = null;
        unit.Target = null;
        unit.Waypoints.Clear();
        unit.PendingCast = null;
        unit.ForcedMove = null;
        Spells.CancelRecall(unit);
        Sinks?.OnZombie(unit);
        if (Effects.IsReferenceReady(unit))
        {
            // The stand-up's one-point path is installed on receipt: seated on its anchor at once.
            Effects.SendWaypoints(unit, Spells.Movement.InstallStop(unit), _world.Clock.SyncId());
            Effects.SendStats(unit);
        }
    }

    /// <summary>
    /// A kill of a unit in its zombie life (the damage that reaches it in the delay is only <c>DAMAGESOURCE_INTERNALRAW</c>; in the zombie buff the
    /// script ends the life itself): the life ends, and the buffs that run it go with it, the delay first because its end would start the zombie
    /// buff, then the zombie buff, then the marker. Reconstruction: no shipped script kills a unit in its delay, so retail's rule for that case is not recovered; this is the hand-written port's
    /// rule (a kill finishes the temporary life and starts no second one).
    /// </summary>
    public bool TryFinishExistingRevival(Entity unit, uint killerNetId)
    {
        if (!EndZombieLife(unit)) return false;
        foreach (var prefix in new[] { "YorickRADelay", "YorickRAZombie", "YorickReviveAllySelf" })
            foreach (var buff in unit.Buffs.Where(buff => buff.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                if (unit.Buffs.Contains(buff)) Buffs.RemoveBuff(unit, buff.Slot);
        return true;
    }

    /// <summary>
    /// <c>BBForceDead</c> of a unit in a zombie life, and a kill of one: the life ends the way the lifetime reaching its end does. The unit is dead
    /// again (<c>bDead</c> set, <c>bZombie</c> cleared) without another death: no rewards, no statistics, no second respawn timer. The server's
    /// <c>RemoveNonPersistantBuffs</c> runs, the unit's unfinished work and the effects it received end as a death retires them, its pets die
    /// with it, and <c>NpcForceDead</c> and the stats go out. False when the unit is in no zombie life.
    /// </summary>
    public bool EndZombieLife(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.YorickRevival is null) return false;
        unit.YorickRevival = null;
        Combat.CancelPendingWindups(unit.NetId);
        unit.Alive = false;
        unit.Health = 0.0;
        // This second death bypasses CombatService.Kill: retire the life's unfinished source work and recipient effects without repeating
        // kills, rewards or statistics.
        Spells.DestroyTargetedProjectiles(unit.NetId);
        RetireChampionDeathWork(unit);
        RetireNonPersistentRecipientBuffs(unit);
        Sinks?.OnForceDead(unit);
        unit.AttackTarget = null;
        unit.Target = null;
        unit.Waypoints.Clear();
        unit.ForcedMove = null;
        unit.PendingCast = null;
        // The owner-death cascade runs here too: the ghouls Yorick's ally summoned during the life die with it, retail behaviour per the
        // owner research of 18 October 2026, and so would an ally's Tibbers.
        Combat.DespawnPetsOf(unit.NetId);
        if (Effects.IsReferenceReady(unit))
        {
            Effects.SendForceDead(unit);
            Effects.SendStats(unit);
        }
        return true;
    }

    public double ScriptHealingScale(Entity unit) => Sinks is null ? 1.0 : Sinks.OnHeal(unit, 1.0);

    /// <summary>
    /// A swing of Yorick's ghoul or revenant: <c>BuffOnHitUnit</c> of the script buffs the unit holds (<c>YorickSpectralLogic</c> ...,
    /// <c>YorickRAPetBuff2</c>), which deal the hit as Yorick and take the swing away. The engine runs the hit hooks of script heroes only
    /// (<see cref="ModifyBasicAttackDamage(Entity, Entity, double, bool?, BasicAttackOutcome)"/>), so a pet's swing is handed to them here.
    /// False when the target is gone or no script drives the match.
    /// </summary>
    private bool ScriptPetHit(Entity pet, Entity target, double amount)
    {
        if (Sinks is null || !target.Alive) return false;
        Sinks.ModifyBasicAttackDamage(pet, target, amount, false, BasicAttackOutcome.Normal);
        return true;
    }
}
