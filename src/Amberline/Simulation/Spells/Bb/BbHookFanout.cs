using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The one <see cref="IBbHookSink"/> a match installs on its gameplay hooks when it has script-driven heroes: it forwards
/// each event to the router of every bound <see cref="BbChampionModule"/> (a router only acts on the heroes of its own
/// champion and on the buff instances it created). Matches without a script-driven hero never create one, so the ten
/// hand-written champions run exactly as before.
/// </summary>
internal sealed class BbHookFanout : IBbHookSink
{
    private readonly List<IBbHookSink> _sinks = new();

    public void Add(IBbHookSink sink) => _sinks.Add(sink);

    public bool OwnsUnit(Entity unit)
    {
        foreach (var sink in _sinks) if (sink.OwnsUnit(unit)) return true;
        return false;
    }

    public void OnBuffTick(Entity unit, BuffState buff, double dt)
    {
        foreach (var sink in _sinks) sink.OnBuffTick(unit, buff, dt);
    }

    public bool AllowBuffAdd(Entity target, Entity adder, string name, ref double duration, double type, double maxStack)
    {
        foreach (var sink in _sinks)
            if (!sink.AllowBuffAdd(target, adder, name, ref duration, type, maxStack)) return false;
        return true;
    }

    public void OnAmmoTimer(Entity unit, BuffState buff)
    {
        foreach (var sink in _sinks) sink.OnAmmoTimer(unit, buff);
    }

    public double OnPreDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var sink in _sinks) amount = sink.OnPreDamage(target, source, amount, type, kind);
        return amount;
    }

    public double OnPreMitigationDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var sink in _sinks) amount = sink.OnPreMitigationDamage(target, source, amount, type, kind);
        return amount;
    }

    public void OnTakeDamage(Entity target, Entity? source, double amount, DamageSourceKind? kind, bool periodic)
    {
        foreach (var sink in _sinks) sink.OnTakeDamage(target, source, amount, kind, periodic);
    }

    public void OnUnitDeath(Entity unit, uint killerNetId)
    {
        foreach (var sink in _sinks) sink.OnUnitDeath(unit, killerNetId);
    }

    public void OnUnitDeathReleased(Entity unit)
    {
        foreach (var sink in _sinks) sink.OnUnitDeathReleased(unit);
    }

    public bool OnDeathBecomesZombie(Entity unit, uint killerNetId)
    {
        var becomes = false;
        foreach (var sink in _sinks) becomes |= sink.OnDeathBecomesZombie(unit, killerNetId);
        return becomes;
    }

    public void OnZombie(Entity unit)
    {
        foreach (var sink in _sinks) sink.OnZombie(unit);
    }

    public void OnForceDead(Entity unit)
    {
        foreach (var sink in _sinks) sink.OnForceDead(unit);
    }

    public double OnHeal(Entity unit, double amount)
    {
        foreach (var sink in _sinks) amount = sink.OnHeal(unit, amount);
        return amount;
    }

    public void OnLevelChanged(Entity hero)
    {
        foreach (var sink in _sinks) sink.OnLevelChanged(hero);
    }

    public void OnHeroRespawn(Entity hero)
    {
        foreach (var sink in _sinks) sink.OnHeroRespawn(hero);
    }

    public void OnHeroDisconnect(Entity hero)
    {
        foreach (var sink in _sinks) sink.OnHeroDisconnect(hero);
    }

    public void OnHeroReconnect(Entity hero)
    {
        foreach (var sink in _sinks) sink.OnHeroReconnect(hero);
    }

    public void OnSpellCast(Entity hero, int slot, string spellName)
    {
        foreach (var sink in _sinks) sink.OnSpellCast(hero, slot, spellName);
    }

    public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target)
    {
        foreach (var sink in _sinks) sink.OnNamedSpellCast(hero, slot, spellName, target);
    }

    public void OnBasicAttackStart(Entity attacker, Entity target)
    {
        foreach (var sink in _sinks) sink.OnBasicAttackStart(attacker, target);
    }

    public bool SendAttackAsCast(Entity attacker, Entity target, uint missileNetId)
    {
        foreach (var sink in _sinks) if (sink.SendAttackAsCast(attacker, target, missileNetId)) return true;
        return false;
    }

    public bool OnUnitAttackHit(Entity unit, Entity target)
    {
        foreach (var sink in _sinks) if (sink.OnUnitAttackHit(unit, target)) return true;
        return false;
    }

    public double OnAttackOverrideDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome)
    {
        foreach (var sink in _sinks) damage = sink.OnAttackOverrideDamage(attacker, target, damage, attackOverride, outcome);
        return damage;
    }

    public bool AttackOverrideIgnoresCrit(Entity attacker, Entity target)
    {
        foreach (var sink in _sinks) if (sink.AttackOverrideIgnoresCrit(attacker, target)) return true;
        return false;
    }

    public bool TakeAttackOverrideIgnoresIncreaseMods()
    {
        var any = false;
        foreach (var sink in _sinks) any |= sink.TakeAttackOverrideIgnoresIncreaseMods();
        return any;
    }

    public BasicAttackOutcome? TakeScriptedHitResult()
    {
        BasicAttackOutcome? result = null;
        foreach (var sink in _sinks) result = sink.TakeScriptedHitResult() ?? result;
        return result;
    }

    public void OnBasicAttackSent(Entity attacker, Entity target, uint missileNetId)
    {
        foreach (var sink in _sinks) sink.OnBasicAttackSent(attacker, target, missileNetId);
    }

    public void OnBasicAttackHit(Entity attacker, Entity target, double rawDamage, bool attackOverride, BasicAttackOutcome outcome)
    {
        foreach (var sink in _sinks) sink.OnBasicAttackHit(attacker, target, rawDamage, attackOverride, outcome);
    }

    public double ModifyBasicAttackDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome)
    {
        foreach (var sink in _sinks) damage = sink.ModifyBasicAttackDamage(attacker, target, damage, attackOverride, outcome);
        return damage;
    }

    public void OnBasicAttackOnCorpse(Entity attacker, Entity corpse, bool attackOverride)
    {
        foreach (var sink in _sinks) sink.OnBasicAttackOnCorpse(attacker, corpse, attackOverride);
    }

    public double OnPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var sink in _sinks) amount = sink.OnPreDealDamage(source, target, amount, type, kind);
        return amount;
    }

    public double OnBuffPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var sink in _sinks) amount = sink.OnBuffPreDealDamage(source, target, amount, type, kind);
        return amount;
    }

    public bool OnBeingSpellHit(Entity? source, Entity target, DamageSourceKind? kind)
    {
        var absorbed = false;
        foreach (var sink in _sinks) absorbed |= sink.OnBeingSpellHit(source, target, kind);
        return absorbed;
    }

    public bool OnBreakSpellShields(Entity target)
    {
        var popped = false;
        foreach (var sink in _sinks) popped |= sink.OnBreakSpellShields(target);
        return popped;
    }

    public bool ShieldPopped(Entity target)
    {
        foreach (var sink in _sinks) if (sink.ShieldPopped(target)) return true;
        return false;
    }

    public bool IsScripted(Entity target, BuffState buff)
    {
        foreach (var sink in _sinks) if (sink.IsScripted(target, buff)) return true;
        return false;
    }

    public void OnCancelChannel(Entity hero, string reason)
    {
        foreach (var sink in _sinks) sink.OnCancelChannel(hero, reason);
    }

    public bool IsSpellToggledOn(Entity hero, int slot)
    {
        foreach (var sink in _sinks) if (sink.IsSpellToggledOn(hero, slot)) return true;
        return false;
    }

    public void OnUnitKilled(Entity killer, Entity victim)
    {
        foreach (var sink in _sinks) sink.OnUnitKilled(killer, victim);
    }

    public void OnUnitAssisted(Entity assistant, Entity victim)
    {
        foreach (var sink in _sinks) sink.OnUnitAssisted(assistant, victim);
    }

    public double OnBeingHit(Entity target, Entity attacker, double amount, DamageType type, BasicAttackOutcome outcome)
    {
        foreach (var sink in _sinks) amount = sink.OnBeingHit(target, attacker, amount, type, outcome);
        return amount;
    }

    public double OnDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var sink in _sinks) amount = sink.OnDealDamage(source, target, amount, type, kind);
        return amount;
    }

    public void OnBasicAttackAvoided(Entity attacker, Entity target, BasicAttackOutcome outcome)
    {
        foreach (var sink in _sinks) sink.OnBasicAttackAvoided(attacker, target, outcome);
    }

    public void OnBasicAttackLaunched(Entity attacker, Entity target, uint missileNetId)
    {
        foreach (var sink in _sinks) sink.OnBasicAttackLaunched(attacker, target, missileNetId);
    }
}
