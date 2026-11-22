using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The hops of a chain attack after its primary hit (Sivir's Ricochet, <c>RicochetAttack</c>, CastType 2): the attack's own missile
/// has landed on the first unit (the engine's basic attack dealt that hit and ran the override script for it), and the orb now flies
/// from unit to unit at the attack spell's missile speed. Each arrival runs the override spell's <c>TargetExecute</c> with the
/// number of the hit (<c>GetCastSpellTargetsHitPlusOne</c>). Like the client's chain missile the orb flies to every
/// unit of its list whose object still exists, living or not, and counts the arrival as a hop; it hits only what can be hit, so a
/// unit that died on the way takes nothing and the orb bounces off its corpse to the next one (the retail rule of
/// <c>SpellSystem.DestroyTargetedProjectiles</c>; the same as the spell chain, <see cref="BbSpellProjectile"/>). A unit that is gone
/// entirely is skipped.
/// </summary>
internal sealed class BbChainAttack : ISharedSpellProjectile
{
    private readonly BbHookRouter _router;
    private readonly BbScript _script;
    private readonly ScriptAttackOverride _attack;
    // The planned hops: ReplanHop replans from a planned unit that is dead when the orb would start toward it (batch 177 part 4). Batch 938:
    // the swing plan's own array, which its BbChainSync copies send too; 0 = no hop.
    private readonly uint[] _hops;
    private readonly double _speed;
    private readonly uint _missileNetId;
    private int _hop = 1;
    private int _aimedHop = -1;
    private bool _corpseBounced;
    private Position2 _position;

    public BbChainAttack(BbHookRouter router, BbScript script, Entity attacker, ScriptAttackOverride attack, uint[] hops,
        Position2 start, double speed, uint missileNetId = 0)
    {
        _router = router;
        _script = script;
        Attacker = attacker;
        _attack = attack;
        _hops = hops;
        _position = start;
        _speed = speed;
        _missileNetId = missileNetId;
    }

    public Entity Attacker { get; }

    public SharedProjectileSnapshot Snapshot => new(_attack.SpellName, Attacker.NetId, _hop < _hops.Length ? _hops[_hop] : 0u, 0,
        0.0, true, _position, _speed, Rank: _attack.Level, Slot: _attack.Slot);

    public bool Tick(SpellSystem system, double dt)
    {
        var budget = dt * _speed;
        while (_hop < _hops.Length)
        {
            // The orb starts toward its planned unit once (_aimedHop): a unit that is dead by then (or a slot a shorter replan emptied) is replanned.
            if (_aimedHop != _hop)
            {
                _aimedHop = _hop;
                if ((_hops[_hop] == 0 || system.World.Get(_hops[_hop]) is { Alive: false }) && !ReplanHop(system)) return Cut(system);
            }
            var unit = system.World.Get(_hops[_hop]);
            if (unit is null)
            {
                _hop++;
                continue;
            }
            var dx = unit.Position.X - _position.X;
            var dz = unit.Position.Z - _position.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance > budget)
            {
                if (distance > 1e-9)
                    _position = new Position2(_position.X + dx / distance * budget, _position.Z + dz / distance * budget);
                return true;
            }
            budget -= distance;
            _position = unit.Position;
            var number = _hop++;
            if (unit.Alive && !unit.Protected && system.Combat.IsTargetable(unit, Attacker))
                _router.RunChainHop(Attacker, _attack, _script, unit, number);
            else if (!unit.Alive)
            {
                // A unit that died while the orb flew to it: the orb bounces off the corpse once, then the chain must find living units.
                if (_corpseBounced && _hop < _hops.Length) return Cut(system);
                _corpseBounced = true;
            }
        }
        return false;
    }

    /// <summary>
    /// Batch 177 part 4 (the owner's 24 October 2026 finding 19, "E keeps bouncing off a dead target", checked on Sivir's Ricochet too): a
    /// planned hop that is dead when the orb would start toward it is replanned with the rest of the list from the orb
    /// (<see cref="BbChainParameters.Extend"/>, batch 938: the whole rest, so the new unit is not also a later hop). The client follows the list it
    /// was sent, so the new list is sent again. False when no living unit qualifies, which ends the orb.
    /// </summary>
    private bool ReplanHop(SpellSystem system)
    {
        var data = system.Data.Spell(_attack.SpellName, 5);
        var passed = _hops.Take(_hop).Select(id => system.World.Get(id)).OfType<Entity>().ToList();
        var kept = passed.Count;
        var plan = BbChainParameters.Read(_script)?.Extend(system, Attacker, passed, _position, data,
            BbSpellTargeting.Names(BbSpellTargeting.Flags(data)), kept + _hops.Length - _hop);
        if (plan is null || plan.Count == kept) return false;
        for (var i = _hop; i < _hops.Length; i++) _hops[i] = i - _hop + kept < plan.Count ? plan[i - _hop + kept].NetId : 0u;
        if (_missileNetId != 0) system.Effects.SendChainMissileSync(Attacker, _missileNetId, _hops, attack: true);
        return true;
    }

    /// <summary>
    /// Batch 938: the orb ends while the client's list still names hops. The client's chain missile follows its list, so it is destroyed
    /// rather than left to fly on to units that take nothing.
    /// </summary>
    private bool Cut(SpellSystem system)
    {
        _hop = _hops.Length;
        if (_missileNetId != 0) system.Effects.SendDestroyMissile(_missileNetId);
        return false;
    }
}
