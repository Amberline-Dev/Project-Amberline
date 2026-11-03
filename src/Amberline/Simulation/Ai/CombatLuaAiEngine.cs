using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The natives' world half, bound to the services that already own the behaviour. Nothing here
/// decides anything: <c>FindTargetInAcR</c> is the acquisition ladder the hand-written turret AI
/// used, <c>SetTarget</c> is the same target-plus-wire setter, and the auto-attack natives move
/// the flag the combat tick's swing gate already reads.
/// </summary>
internal sealed class CombatLuaAiEngine : ILuaAiEngine
{
    private readonly CombatService _combat;
    private readonly WorldState _world;
    private readonly Dictionary<uint, double> _state = new();
    private readonly Dictionary<uint, uint?> _retained = new();

    public CombatLuaAiEngine(CombatService combat, WorldState world)
    {
        _combat = combat;
        _world = world;
    }

    public double Now => _combat.CurrentTime;

    public string LostTargetHook => "OnTargetLost";

    public Entity? FindTargetInAcquisitionRange(Entity unit) =>
        // The shared ladder: summoned units, then lane minions, then anything else, the nearest
        // of each. A minion script will route through MinionAiSystem.SelectTarget instead when
        // its batch wires it; the native is the same one either way.
        _combat.AcquireTurretTarget(unit);

    public void SetTarget(Entity unit, Entity? target)
    {
        _retained[unit.NetId] = target?.NetId;
        _combat.SetTurretTarget(unit, target);
    }

    public Entity? CurrentTarget(Entity unit)
    {
        // The object the AI took, not the wire id: the combat tick retires a dead target's id and
        // the script still answers for the target it was given (its OnTargetLost carries it).
        var id = HeldTargetId(unit);
        return id is uint netId ? _world.Get(netId) : null;
    }

    public uint? HeldTargetId(Entity unit) =>
        _retained.TryGetValue(unit.NetId, out var held) ? held : unit.AttackTarget;

    public Entity? TauntTarget(Entity unit) => PetControl.TauntTarget(_world, unit);

    public bool TargetStillValid(Entity unit, Entity target) =>
        // The engine's target-lost test. Retail turrets always consider minions, so only the attack test decides.
        _combat.CanAttackIgnoringRange(unit, target);

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && CombatService.InRange(unit, target);

    public void TurnOnAutoAttack(Entity unit, Entity? target) =>
        _combat.SetTurretAutoAttack(unit, target, enabled: true);

    public void TurnOffAutoAttack(Entity unit, double stopReason) =>
        // The stop reason (STOPREASON_*) is the engine's own; nothing in this build sends it. Batch 529 removed batch 528's MOVING
        // exception: a Crystal Scar shrine's laser now follows its swing gate (CombatService.PublishTurretDisplay).
        _combat.SetTurretAutoAttack(unit, null, enabled: false);

    public double AiState(Entity unit) =>
        _state.TryGetValue(unit.NetId, out var state) ? state : 0.0;

    public void SetAiState(Entity unit, double state) => _state[unit.NetId] = state;
}
