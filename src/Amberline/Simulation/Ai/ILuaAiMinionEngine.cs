using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The half of an AI script's natives only a lane minion asks for: <c>Minion.luaobj</c> and the
/// <c>minionLib.luaobj</c> it dofile's name these five and no other engine family does, so they
/// are bound only for a unit whose engine carries this half and a script that names one is never
/// a silent no-op. The other seven a minion names are the shared ownership set
/// (<see cref="ILuaAiOwnedEngine"/>); <c>IsMoving</c> is here rather than there because the pet
/// scripts bind their own through <see cref="ILuaAiPetEngine"/> and a minion is never a pet.
/// </summary>
public interface ILuaAiMinionEngine : ILuaAiEngine
{
    /// <summary>
    /// <c>SetStateAndMoveToForwardNav(state)</c>: take the state and walk the lane's forward
    /// route. Minion.lua names it wherever a target run gave up (<c>FindTargetOrMove</c>'s empty
    /// scan) and from <c>TimerMoveForward</c>, so it is the native that knows where "forward" is.
    /// </summary>
    void SetStateAndMoveToForwardNav(Entity unit, double state);

    /// <summary><c>LastAutoAttackFinished()</c>: no swing is still wound up on anything.</summary>
    bool LastAutoAttackFinished(Entity unit);

    /// <summary>
    /// <c>AddToIgnore(seconds)</c>: drop the current target from selection for a moment. The
    /// scripts name it with <c>0.1</c> from the anti-kite and blocked-path callbacks.
    /// </summary>
    void AddToIgnore(Entity unit, double seconds);

    /// <summary><c>DistanceBetweenMeAndObject(object)</c>: the straight-line distance to a unit.</summary>
    double DistanceBetweenMeAndObject(Entity unit, Entity? other);

    /// <summary><c>IsMoving()</c>: the unit still follows a move order (a planted one does not).</summary>
    bool IsMoving(Entity unit);

    /// <summary>
    /// <c>FindUseableObjectInAcR()</c> (MinionOdin.luaobj proto 0.2 and 0.3: the first priority of the scan, before any target).
    /// Batch 499: the nearest unit in acquisition range whose character's Useable section lets a minion of this team use it (on map 8 an enemy
    /// or neutral capture point guardian: OdinNeutralGuardian's MinionUseSpell OdinMinionSpellAttack, EnemyCanUse 1).
    /// </summary>
    Entity? FindUseableObjectInAcR(Entity unit);

    /// <summary><c>IsObjectUseable(object)</c> (MinionOdin.luaobj protos 0.0, 0.1, 0.2): whether the unit may use the object (batch 499: the same rule).</summary>
    bool IsObjectUseable(Entity unit, Entity? other);

    /// <summary>
    /// <c>UseTarget(object)</c> (MinionOdin.luaobj protos 0.0 to 0.3): the unit goes to use the object. Batch 499: it walks to the use spell's
    /// range; batch 711: there it casts the object's MinionUseSpell once per its cooldown (Minions.MinionAiSystem.NativeUseTarget).
    /// </summary>
    void UseTarget(Entity unit, Entity? other);

    /// <summary>
    /// <c>SetStateAndMoveToBackwardNav(state)</c> (MinionReverse.luaobj only: its TimerMoveForward and its no-target branch of FindTargetOrMove).
    /// Batch 499, Reconstruction: a reversed squad's lane (SquadPushLane Reverse) is installed at birth as its scene nav list walked backward
    /// (Minions.OdinWaves.Route), so the backward nav walks the installed route, as the forward nav does for a forward squad.
    /// </summary>
    void SetStateAndMoveToBackwardNav(Entity unit, double state);
}
