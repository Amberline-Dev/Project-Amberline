using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Hook dispatch added in Phase 3 (W22): the unit events that scripts of the ten hand-written champions listen to and the router did not
/// run. Every moment is a Reconstruction: the stock client has none of the hook names (the server dispatches them), so each rule
/// follows from what the shipped scripts do with the variables they read, as the hook table in docs/scripting/building-blocks-engine.md
/// explains.
/// </summary>
internal sealed partial class BbHookRouter
{
    /// <summary>
    /// <c>CharOnPreAttack</c> and <c>BuffOnPreAttack</c>: a hero is about to resolve a basic attack on <paramref name="target"/>. Owner and
    /// Attacker are the attacking unit, Target the unit attacked. The hook runs before the hit's damage is computed, which is where
    /// Shaco's <c>Backstab</c> needs it (it adds a short <c>FromBehind</c> buff that its own <c>BuffOnHitUnit</c> then reads) and where
    /// <c>Deceive</c> arms its crit bonus. Evidence: those scripts; the combat service resolves the attack start in
    /// <c>CombatService.BasicAttackDamage</c> through <c>ICombatHooks.OnBasicAttackStart</c> before the hit hooks run.
    /// </summary>
    public void RunPreAttack(Entity attacker, Entity target)
    {
        if (_scripts.Character is { } script && script.Event("CharOnPreAttack") is not null && Owns(attacker))
        {
            var scope = NewScope(attacker, attacker, target, attacker, null, CharInstanceVars(attacker));
            Run(script, "CharOnPreAttack", scope, new BbHookFrame("CharOnPreAttack", script.Name, attacker) { Caster = attacker });
        }
        foreach (var instance in Buffs.WithHook(attacker, "BuffOnPreAttack"))
        {
            var scope = NewScope(attacker, attacker, target, attacker, instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            Run(instance.Script, "BuffOnPreAttack", scope, BuffFrame(instance, "BuffOnPreAttack", DamageSourceKind.Basic, inDamage: false));
        }
    }

    /// <summary>
    /// <c>CharOnLevelUp</c> and <c>BuffOnLevelUp</c>: the hero gained a champion level. Owner is the hero (the holder of the buff), Attacker the
    /// buff's caster (Yorick's <c>TechmaturgicalIcon</c> writes the tooltip of the caster's spell from it).
    /// </summary>
    public void RunLevelUp(Entity hero)
    {
        if (_scripts.Character is { } script && script.Event("CharOnLevelUp") is not null && Owns(hero))
        {
            var scope = NewScope(hero, hero, hero, hero, hero.Level, CharInstanceVars(hero));
            Run(script, "CharOnLevelUp", scope, new BbHookFrame("CharOnLevelUp", script.Name, hero) { Caster = hero });
        }
        foreach (var instance in Buffs.WithHook(hero, "BuffOnLevelUp"))
            RunBuffHook(instance, "BuffOnLevelUp", _host.Spells.World.Get(instance.CasterNetId));
    }

    /// <summary>
    /// <c>BuffOnLevelUpSpell</c> of the hero's script buffs: the hero spent a skill point; <c>Slot</c> is the spell slot that gained the rank
    /// (Nidalee's <c>AspectOfTheCougar</c> re-reads the ultimate's rank, Yorick's death-defying buff the cost of the slot).
    /// </summary>
    private void RunBuffLevelUpSpell(Entity hero, int slot)
    {
        var rank = slot is >= 0 and < 4 && hero.SpellRanks.Count > slot ? hero.SpellRanks[slot] : 0;
        foreach (var instance in Buffs.WithHook(hero, "BuffOnLevelUpSpell"))
            RunBuffHook(instance, "BuffOnLevelUpSpell", _host.Spells.World.Get(instance.CasterNetId),
                prepare: scope => { scope.Vars["Slot"] = (double)slot; if (rank >= 1) scope.Level = rank; });
    }

    /// <summary>
    /// <c>CharOnResurrect</c> and <c>BuffOnResurrect</c>: a hero that died came back at its fountain (not the first spawn, which is
    /// <c>CharOnActivate</c>). Vayne re-seals her spell slots, Yorick resets his ghoul counter, Orianna gives herself the ball again and
    /// destroys the missile of the old life. Owner is the hero. The engine's own release of a cast seal is the last step (see below).
    /// <para>
    /// <c>Target</c> is the hero too, as in <c>CharOnActivate</c> and <c>CharOnLevelUpSpell</c>. Xin Zhao's
    /// <c>CharOnResurrect</c> gives Tireless Warrior back with <c>TargetVar="Target"</c> (<c>CharScriptXinZhao.luaobj</c> instr 0084, in the block
    /// stored at 0147; the other 22 shipped scripts of the hook name <c>Owner</c>). With no Target the passive would be gone for the rest of the
    /// game after his first death, so retail binds Target to the hero.
    /// </para>
    /// </summary>
    public void RunResurrect(Entity hero)
    {
        if (_scripts.Character is { } script && script.Event("CharOnResurrect") is not null && Owns(hero))
        {
            var scope = NewScope(hero, hero, hero, hero, null, CharInstanceVars(hero));
            Run(script, "CharOnResurrect", scope, new BbHookFrame("CharOnResurrect", script.Name, hero) { Caster = hero });
        }
        foreach (var instance in Buffs.WithHook(hero, "BuffOnResurrect"))
            RunBuffHook(instance, "BuffOnResurrect", _host.Spells.World.Get(instance.CasterNetId));
        // Batch 306 (owner decision, 26 October 2026: "Fortify follows the scripts"): batch 203's respawn renew of APBonusDamageToTowers is
        // gone. FortifyCheck is not PersistsThroughDeath and its only shipped add is APBonusDamageToTowers' OnBuffActivate, which no script runs
        // again after a death, so Fortify's passive ends at the first death, as the scripts say.
        // Reconstruction: a cast seal that the scripts above did not reopen (a champion whose CharOnResurrect is silent) is released here,
        // at respawn. Retail's rule for a dead hero's sealed slots is not recovered; death itself no longer opens them (SpellSlotSeals.ReleaseCastSeals).
        if (hero.SpellSeals.ReleaseCastSeals()) _host.Spells.SendChampionStats(hero);
    }

    public void OnHeroRespawn(Entity hero) => Guard("sink", "OnHeroRespawn", () => RunResurrect(hero));
    public void OnHeroDisconnect(Entity hero) => Guard("sink", "OnHeroDisconnect", () => RunDisconnect(hero));
    public void OnHeroReconnect(Entity hero) => Guard("sink", "OnHeroReconnect", () => RunReconnect(hero));

    /// <summary>
    /// <c>CharOnDisconnect</c> and <c>BuffOnDisconnect</c>: a hero's player dropped out of a live match (a lost connection, an AFK kick, a
    /// loading timeout). Owner is the hero. The shipped CharOnDisconnect of the champions casts inventory slot 6, the disconnect trigger,
    /// and nothing else. The DisconnectTimer is not added here: APBonusDamageToTowers' shipped BuffOnDisconnect adds it (BUFF_RENEW_EXISTING,
    /// Owner, 25000 s), and every hero carries that buff (batch 203). Adding it here too would run DisconnectTimer's OnBuffActivate twice.
    /// </summary>
    public void RunDisconnect(Entity hero)
    {
        if (_scripts.Character is { } script && script.Event("CharOnDisconnect") is not null && Owns(hero))
        {
            var scope = NewScope(hero, hero, hero, hero, null, CharInstanceVars(hero));
            Run(script, "CharOnDisconnect", scope, new BbHookFrame("CharOnDisconnect", script.Name, hero) { Caster = hero });
        }
        foreach (var instance in Buffs.WithHook(hero, "BuffOnDisconnect"))
            RunBuffHook(instance, "BuffOnDisconnect", _host.Spells.World.Get(instance.CasterNetId));
    }

    /// <summary>
    /// <c>CharOnReconnect</c> and <c>BuffOnReconnect</c>: the seat came back and is ready. DisconnectTimer's shipped BuffOnReconnect removes
    /// the timer again, which undoes the disconnect.
    /// </summary>
    public void RunReconnect(Entity hero)
    {
        if (_scripts.Character is { } script && script.Event("CharOnReconnect") is not null && Owns(hero))
        {
            var scope = NewScope(hero, hero, hero, hero, null, CharInstanceVars(hero));
            Run(script, "CharOnReconnect", scope, new BbHookFrame("CharOnReconnect", script.Name, hero) { Caster = hero });
        }
        foreach (var instance in Buffs.WithHook(hero, "BuffOnReconnect"))
            RunBuffHook(instance, "BuffOnReconnect", _host.Spells.World.Get(instance.CasterNetId));
    }
}
