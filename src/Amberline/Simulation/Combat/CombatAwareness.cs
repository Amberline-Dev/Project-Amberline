using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    // Map1 Constants.var ca_RevealAttackerTimeOut and cfh_* controls.
    // The reveal routine is the one routine behind
    // both a basic attack and a spell whose data sets SpellRevealsChampion: when the cast
    // executes it takes the first resolvable unit of the cast's target list and starts a
    // positional bubble at the caster's position for THAT UNIT'S TEAM only (the neutral
    // slot is the camps); it stays at the cast point instead of following the caster.
    // Basic attacks call this from QueueBasicAttack, spells through SpellSystem.
    public void RevealAttack(Entity attacker, Entity target)
    {
        if (target.Team is (Team.Order or Team.Chaos or Team.Neutral) &&
            WorldState.IsEnemy(attacker, target))
        {
            attacker.AttackRevealRemaining[(int)target.Team] = _gameData.LevelConstants.Values["ca_RevealAttackerTimeOut"];
            // ponytail: one bubble per attacker and team (the latest attack);
            // retail keeps one per attack, so keep a list if chains show gaps.
            attacker.AttackRevealPosition[(int)target.Team] = attacker.Position;
        }
    }

    private void TickCombatAwareness(double dt)
    {
        foreach (var unit in _world.Entities.Values)
        {
            for (var team = 0; team < unit.AttackRevealRemaining.Length; team++)
                unit.AttackRevealRemaining[team] = Math.Max(0.0, unit.AttackRevealRemaining[team] - dt);
            unit.HelpCallRemaining = Math.Max(0.0, unit.HelpCallRemaining - dt);
            unit.LaneHelpCallRemaining = Math.Max(0.0, unit.LaneHelpCallRemaining - dt);
            unit.HelpRemaining = Math.Max(0.0, unit.HelpRemaining - dt);
            unit.HelpStickRemaining = Math.Max(0.0, unit.HelpStickRemaining - dt);
            if (unit.HelpRemaining == 0.0) unit.HelpTarget = null;
        }
    }

    private void CallForHelp(Entity victim, Entity attacker, DamageSourceKind? sourceKind)
    {
        // SetSuppressCallForHelp on the victim and SetCallForHelpSuppresser on the attacker (the Omen of Death delay sets both, with
        // SetIgnoreCallForHelp on the zombie): no one is called.
        if (victim.AnyBuffOrStatus(buff => buff.Payload.SuppressCallForHelp) ||
            attacker.AnyBuffOrStatus(buff => buff.Payload.CallForHelpSuppresser)) return;
        CallLaneHelp(victim, attacker, sourceKind);
        if (victim.UnitType != UnitType.Hero || !victim.Alive ||
            !WorldState.IsEnemy(victim, attacker) ||
            victim.Buffs.Any(buff => buff.Name == "GuardianAngel")) return;
        var owner = ResolveHeroOwner(attacker);
        if (owner is null || !owner.Alive) return;
        var turretHelp = victim.HelpCallRemaining <= 0.0;
        if (turretHelp) victim.HelpCallRemaining = 1.0;
        foreach (var responder in _world.Entities.Values.Where(unit => unit.Alive &&
                     unit.Team == victim.Team && unit.UnitType == UnitType.Turret))
        {
            if (!turretHelp || responder.AnyBuffOrStatus(buff => buff.Payload.IgnoreCallForHelp)) continue;
            if (DistanceSquared(responder.Position, victim.Position) > 800.0 * 800.0 ||
                _hooks.IsActionBlocked(responder) ||
                responder.AnyBuffOrStatus(buff => buff.Type is BuffType.Fear or BuffType.Taunt || buff.Payload.IgnoreCallForHelp) ||
                !IsTargetable(owner, responder)) continue;
            var reach = TurretRules.IsFountainTurret(responder)
                ? AttackReach(responder, owner) : responder.AttackRange + owner.Radius;
            if (DistanceSquared(responder.Position, owner.Position) > reach * reach) continue;
            var current = responder.AttackTarget is uint id ? _world.Get(id) : null;
            if (current is { Alive: true, UnitType: UnitType.Hero } && current != owner &&
                responder.HelpStickRemaining > 0.0) continue;
            responder.HelpTarget = owner.NetId;
            responder.HelpRemaining = 1.0;
            responder.HelpStickRemaining = 1.5;
            // Turret.luaobj OnCallForHelp(self, aggressor) owns the assignment: it takes the
            // aggressor with NetSetState(AI_HARDIDLE_ATTACKING) + SetTarget while its state is
            // HARDIDLE or ATTACKING, and ignores the call in any other state. The stick window
            // above is the engine's and stays.
            AiHost.OnCallForHelp(responder, owner);
        }
    }

    private void CallLaneHelp(Entity victim, Entity attacker, DamageSourceKind? sourceKind)
    {
        // Batch 664: a scripted minion (Minion.lua, Aggro.lua) calls for help as a lane minion does (MinionAiSystem.IsMinionUnit).
        if (!victim.Alive || victim.UnitType != UnitType.Hero && !Minions.MinionAiSystem.IsMinionUnit(victim) ||
            sourceKind == DamageSourceKind.Spell || victim.LaneHelpCallRemaining > 0.0 ||
            !WorldState.IsEnemy(victim, attacker) ||
            victim.Buffs.Any(buff => buff.Name == "GuardianAngel")) return;
        // Preserve owner attribution for champion damage without making pets
        // eligible as a new target class in the lane selector.
        var aggressor = ResolveHeroOwner(attacker) ?? attacker;
        if (!aggressor.Alive || aggressor.UnitType is not (UnitType.Hero or UnitType.Turret) &&
            !Minions.MinionAiSystem.IsMinionUnit(aggressor)) return;
        var priority = Minions.MinionAiSystem.TargetPriority(_world, victim, aggressor, victim);
        if (priority > 4) return;
        victim.LaneHelpCallRemaining = 1.0;
        // Batch 348: every unit whose shipped AI script answers OnCallForHelp is called, not only the lane minions: a BBSpawnMinion unit
        // running Minion.lua (Heimerdinger's turrets: minionLib proto 0.3) or Aggro.lua (Aggro proto 0.3) is an obj_AI_Minion like a lane
        // minion, and the client holds the call as a per-unit virtual of obj_AI_Base (the LeashedCallForHelp stub,
        // in all four AI tables), with Constants.var cfh_* naming melee, ranged and turret responders. Idle.lua's handler is empty (proto 0.3)
        // and the pet scripts have none. The spawned host consumes the call at the unit's next step (TibbersAiSystem.ConsumeHelp).
        // Reconstruction: the native responder test is not in the client; a spawned unit is admitted by the lane minion's rule below.
        foreach (var responder in _world.Entities.Values.Where(unit => unit.Alive && unit.Team == victim.Team &&
                     (unit.UnitType == UnitType.Minion && unit.MinionType is not null || Pets.SpawnedUnitAi.IsLaneLess(unit.AiScript))))
        {
            var buffer = responder.MissileSpeed <= 0.0 ? 420.0 : 170.0;
            var reach = aggressor.UnitType == UnitType.Turret
                ? Math.Max(responder.AcquisitionRange, responder.AttackRange) + aggressor.Radius
                : AttackReach(responder, aggressor) + buffer;
            if (DistanceSquared(responder.Position, victim.Position) > 800.0 * 800.0 ||
                DistanceSquared(responder.Position, aggressor.Position) > reach * reach ||
                _hooks.IsActionBlocked(responder) ||
                responder.AnyBuffOrStatus(buff => buff.Type is BuffType.Fear or BuffType.Taunt || buff.Payload.IgnoreCallForHelp) ||
                !IsTargetable(aggressor, responder) ||
                !Minions.MinionAiSystem.CanAcceptHelp(_world, responder, aggressor, priority,
                    includePending: true)) continue;
            responder.HelpPriority = responder.HelpTarget == aggressor.NetId
                ? Math.Min(responder.HelpPriority, priority) : priority;
            responder.HelpTarget = aggressor.NetId;
            responder.HelpRemaining = 1.0;
        }
    }
}
