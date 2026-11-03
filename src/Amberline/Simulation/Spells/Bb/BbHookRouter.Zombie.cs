using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Zombie lives and healing (Phase 3, Yorick, 19 October 2026). The stock client holds <c>bZombie</c> / <c>bBecomeZombie</c> on every unit
/// (<c>BBGetIsZombie</c>, <c>BBForceDead</c>) and the death packet's become-zombie byte, but none of the hook names: a script sets
/// <c>BecomeZombie</c> in its <c>BuffOnDeath</c> and acts in <c>BuffOnZombie</c> (<c>YorickReviveAllySelf</c>, <c>DeathDefied</c>,
/// <c>KogMawIcathianSurpriseReady</c>), and the heals a unit receives pass through <c>BuffOnHeal</c>. Every moment here is a Reconstruction
/// from those scripts; the unit-level transition (alive again, one health, work cancelled) is <c>MatchGameplayHooks.BeginZombie</c>.
/// </summary>
internal sealed partial class BbHookRouter
{
    public bool OnDeathBecomesZombie(Entity unit, uint killerNetId)
    {
        var becomes = false;
        Guard("sink", "OnDeathBecomesZombie", () => becomes = RunBecomeZombie(unit, killerNetId));
        return becomes;
    }

    public void OnZombie(Entity unit) => Guard("sink", "OnZombie", () => RunZombie(unit));

    public void OnForceDead(Entity unit) => Guard("sink", "OnForceDead", () => Buffs.RemoveNonPersistent(unit));

    public double OnHeal(Entity unit, double amount)
    {
        var result = amount;
        Guard("sink", "OnHeal", () => result = RunHeal(unit, amount));
        return result;
    }

    /// <summary>
    /// <c>BuffOnDeath</c> of the unit's buffs that also have <c>BuffOnZombie</c>, read for the <c>BecomeZombie</c> variable. The hooks run here, ahead
    /// of the death hook of the other buffs, because the death packet needs the answer; <see cref="BbBuffInstance.DeathHookRan"/> keeps the
    /// death hook from running them a second time.
    /// </summary>
    private bool RunBecomeZombie(Entity unit, uint killerNetId)
    {
        var killer = _host.Spells.World.Get(killerNetId);
        var becomes = false;
        foreach (var instance in Buffs.WithHook(unit, "BuffOnZombie"))
        {
            if (instance.Script.Event("BuffOnDeath") is null || instance.DeathHookRan) continue;
            BbScope? seen = null;
            RunBuffHook(instance, "BuffOnDeath", killer, prepare: scope => seen = scope);
            instance.DeathHookRan = true;
            becomes |= seen?.Vars["BecomeZombie"] is true;
        }
        return becomes;
    }

    /// <summary>
    /// <c>BuffOnZombie</c> of every script buff the unit holds once its zombie life has started, in the order of its buff list (batch 352). The
    /// death kept the unit's buffs (<c>CombatService.Kill</c>), so the hook reaches a buff that did not choose the life: Caitlyn's mark, which has
    /// no <c>BuffOnDeath</c>, cancels her channel from it. Reconstruction: the server's dispatch is not in the client; the death hook goes to every
    /// buff, and the mark's hook has no other way to run.
    /// </summary>
    private void RunZombie(Entity unit)
    {
        foreach (var instance in Buffs.WithHook(unit, "BuffOnZombie"))
        {
            if (!unit.Buffs.Contains(instance.State)) continue;
            RunBuffHook(instance, "BuffOnZombie", _host.Spells.World.Get(instance.CasterNetId));
        }
    }

    private double RunHeal(Entity unit, double amount)
    {
        foreach (var instance in Buffs.WithHook(unit, "BuffOnHeal"))
        {
            var caster = _host.Spells.World.Get(instance.CasterNetId);
            var scope = NewScope(instance.Owner, caster, instance.Owner, caster, instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["Health"] = amount;
            Run(instance.Script, "BuffOnHeal", scope, BuffFrame(instance, "BuffOnHeal", null, inDamage: false));
            if (scope.ReturnValue is double effective && double.IsFinite(effective)) amount = Math.Max(0.0, effective);
        }
        return amount;
    }
}
