using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Kill and assist hooks (wave 3, W31b, E06): <c>BuffOnKill</c> and <c>CharOnKillUnit</c> run for the hero credited a kill, <c>BuffOnAssist</c>
/// and <c>CharOnAssistUnit</c> for each hero credited an assist in a champion's death. Every moment is a Reconstruction (the stock client has none
/// of the hook names): the shipped scripts read the holder as <c>Owner</c> and the unit that died as <c>Target</c> (Voracity tests it with
/// <c>CO_IS_TYPE_HERO</c>, Highlander, Silent Killer and Draw a Bead reset cooldowns on a champion, Carnivore, Enrage, Second Sight and Archer's
/// Mark pay for any unit), and they spend or reset what the reward gave, so the hooks run after the kill's gold and experience were paid.
/// </summary>
internal sealed partial class BbHookRouter
{
    /// <summary>
    /// The credit hook of a hero's scripts: its character script's <paramref name="characterHook"/> (when the hero is this champion's) and the
    /// <paramref name="buffHook"/> of every script buff it holds. Owner, Attacker and Caster of the character hook are the hero, Target the dead
    /// unit; a buff hook has the buff's caster for Caster (the owner of <c>CharVars</c> the script reads) and the same Owner, Attacker and Target.
    /// </summary>
    private void RunCreditHook(Entity holder, Entity victim, string buffHook, string characterHook)
    {
        if (_scripts.Character is { } script && script.Event(characterHook) is not null && Owns(holder))
        {
            var scope = NewScope(holder, holder, victim, holder, null, CharInstanceVars(holder));
            Run(script, characterHook, scope, new BbHookFrame(characterHook, script.Name, holder) { Caster = holder });
        }
        foreach (var instance in Buffs.WithHook(holder, buffHook))
        {
            if (!holder.Buffs.Contains(instance.State)) continue; // an earlier hook of this event removed it
            var caster = _host.Spells.World.Get(instance.CasterNetId);
            var scope = NewScope(holder, holder, victim, caster, instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            Run(instance.Script, buffHook, scope, BuffFrame(instance, buffHook, null, inDamage: false));
        }
    }

    public void OnUnitKilled(Entity killer, Entity victim) =>
        Guard("sink", "OnUnitKilled", () => RunCreditHook(killer, victim, "BuffOnKill", "CharOnKillUnit"));

    public void OnUnitAssisted(Entity assistant, Entity victim) =>
        Guard("sink", "OnUnitAssisted", () => RunCreditHook(assistant, victim, "BuffOnAssist", "CharOnAssistUnit"));
}
