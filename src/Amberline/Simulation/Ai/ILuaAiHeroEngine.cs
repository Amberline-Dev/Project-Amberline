using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The engine half Hero.luaobj's natives ask for: one champion's own order, target and walk state.
/// The same world half the shared <see cref="ILuaAiEngine"/> and the ownership half
/// <see cref="ILuaAiOwnedEngine"/> already name, plus the eleven natives only the hero script calls
/// (Hero.txt's constant table: <c>AssignTargetPosInPos</c>, <c>CanSeeMe</c>, <c>ClearTargetPosInPos</c>,
/// <c>DistanceBetweenObjectAndTargetPosSq</c>, <c>GetLostTargetIfVisible</c>,
/// <c>GetPercentAttackSpeedMod</c>, <c>GetTargetOrFindTargetInAcR</c>, <c>IsMovementStopped</c>,
/// <c>IsTargetLost</c>, <c>LastAutoAttackFinished</c>, <c>Say</c>, <c>SpellBuffRemoveType</c>).
/// <see cref="Movement.HeroLuaAiEngine"/> binds them to the order and movement plumbing
/// (<see cref="Movement.HeroAttackOrderSystem"/>, <see cref="Combat.CombatService"/>); nothing here
/// decides anything, the chunk's callbacks and its 0.2 s timers are the whole rule set.
/// </summary>
public interface ILuaAiHeroEngine : ILuaAiOwnedEngine
{
    /// <summary>
    /// <c>SetStateAndMoveInPos(state)</c>: the state, then the walk to the order's in-position point
    /// (the click <see cref="AssignTargetPosInPos"/> publishes). Hero.txt's OnOrder names it only for
    /// <c>ORDER_MOVETO</c> and <c>ORDER_ATTACKMOVE</c>.
    /// </summary>
    void SetStateAndMoveInPos(Entity unit, double state);

    /// <summary>
    /// <c>AssignTargetPosInPos()</c>: publishes the order's in-position point as the walk goal the
    /// arrival test (<see cref="DistanceBetweenObjectAndTargetPosSq"/>) reads. It takes no argument,
    /// so the point is the one the caller staged before <c>OnOrder</c> — the contract
    /// <see cref="LuaAiService.Order"/> states.
    /// </summary>
    void AssignTargetPosInPos(Entity unit);

    /// <summary><c>ClearTargetPosInPos()</c>: drops that goal (OnOrder's taunt branch, OnInit, OnStopMove).</summary>
    void ClearTargetPosInPos(Entity unit);

    /// <summary>
    /// <c>DistanceBetweenObjectAndTargetPosSq(object)</c>: the squared distance from the object to
    /// the published goal (TimerDistanceScan's 100-unit arrival test).
    /// </summary>
    double DistanceBetweenObjectAndTargetPosSq(Entity unit, Entity? other);

    /// <summary>
    /// <c>GetTargetOrFindTargetInAcR()</c>: the target the AI holds, or the one
    /// <see cref="ILuaAiEngine.FindTargetInAcquisitionRange"/> would take (TimerDistanceScan).
    /// </summary>
    Entity? GetTargetOrFindTargetInAcquisitionRange(Entity unit);

    /// <summary>
    /// <c>GetLostTargetIfVisible()</c>: the target <c>OnTargetLost</c> handed back to the walk to
    /// its last known location, once it can be seen again (TimerDistanceScan, proto 0.13 pc 0089).
    /// </summary>
    Entity? GetLostTargetIfVisible(Entity unit);

    /// <summary>
    /// <c>CanSeeMe(object)</c>: the object's side can see this unit — the gate Hero.txt puts in
    /// front of every soft engage (TimerCheckAttack proto 0.12 pc 0051-0059, TimerDistanceScan
    /// proto 0.13 pc 0019-0023 and pc 0041-0045). The direction is the object's sight of the
    /// hero, not the hero's sight of the object: a stealthed hero acquires nothing.
    /// </summary>
    bool CanSeeMe(Entity unit, Entity? other);

    /// <summary>
    /// <c>IsTargetLost()</c>: the held target is gone or can no longer be fought (death,
    /// untargetability, a side change, or out of sight) — the test TimerCheckAttack re-acquires on.
    /// </summary>
    bool IsTargetLost(Entity unit);

    /// <summary>
    /// The reason the engine is about to report the loss with — <c>LOST_VISIBILITY</c> when the
    /// target is still a legal one and only sight was lost (Hero.txt OnTargetLost proto 0.3
    /// pc 0009-0011 switches on it), 0 for a target that died, became untargetable or changed side.
    /// </summary>
    double TargetLossReason(Entity unit, Entity? target);

    /// <summary><c>IsMovementStopped()</c>: the hero has no live route left.</summary>
    bool IsMovementStopped(Entity unit);

    /// <summary><c>IsMoving()</c>: the hero is walking.</summary>
    bool IsMoving(Entity unit);

    /// <summary>
    /// <c>LastAutoAttackFinished()</c>: no swing of this hero is still in its windup (TimerCheckAttack
    /// waits for one before it re-acquires).
    /// </summary>
    bool LastAutoAttackFinished(Entity unit);

    /// <summary>
    /// <c>GetPercentAttackSpeedMod()</c>: the fractional attack-speed bonus the champion carries
    /// (<c>CalculateAttackTimer</c> computes <c>1.6 / (mod + 1.0)</c> from it).
    /// </summary>
    double GetPercentAttackSpeedMod(Entity unit);

    /// <summary>
    /// <c>SpellBuffRemoveType(object, type)</c>: every buff of that script type on the object goes
    /// (TimerCheckAttack's charm branch drops <c>BUFF_Taunt</c> from the hero itself).
    /// </summary>
    void SpellBuffRemoveType(Entity unit, Entity? target, double scriptType);

    /// <summary><c>Say(text)</c>: the hero's own line (OnAICommand).</summary>
    void Say(Entity unit, string text);
}
