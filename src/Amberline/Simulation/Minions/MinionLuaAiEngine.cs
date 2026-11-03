using Amberline.Core;
using Amberline.Simulation.Ai;

namespace Amberline.Simulation.Minions;

/// <summary>
/// The world side of a lane minion's shipped AI: <c>Minion.luaobj</c> and the
/// <c>minionLib.luaobj</c> it dofile's decide what a minion does (its states, its timers, its
/// target choice), and this engine answers their natives from the services that already own
/// targeting, combat and movement (<see cref="MinionAiSystem"/>).
/// It is deliberately thinner than <see cref="Neutral.NeutralLuaAiEngine"/>: a minion's route
/// follower, contact solver, waypoint wire and client-path rules are not the script's, and they
/// stay where they are. Everything here is a projection onto that existing half — an AI_* id onto
/// <see cref="Entity.MinionState"/>, a path request onto <c>MinionAiSystem</c>'s lane planner —
/// so the script runs the decision and the engine still decides how the unit moves.
/// </summary>
public sealed class MinionLuaAiEngine : ILuaAiMinionEngine, ILuaAiOwnedEngine
{
    // The AI_* ids Minion.luaobj and minionLib.luaobj write (AiGlobals). Nothing else appears.
    private const double StateHalted = 0;       // AI_HALTED
    private const double StateIdle = 1;         // AI_IDLE
    private const double StateAttackMove = 3;   // AI_ATTACKMOVESTATE
    private const double StateAttacking = 4;    // AI_ATTACKMOVE_ATTACKING
    private const double StateTaunted = 5;      // AI_TAUNTED
    private const double StateFeared = 6;       // AI_FEARED

    private readonly MinionAiSystem _ai;

    public MinionLuaAiEngine(MinionAiSystem ai) =>
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));

    public double Now => _ai.ElapsedSeconds;

    public string LostTargetHook => "OnTargetLost";

    // ---- ILuaAiEngine: the shared natives ----

    public Entity? FindTargetInAcquisitionRange(Entity unit) => _ai.NativeFindTarget(unit);

    public void SetTarget(Entity unit, Entity? target) => _ai.NativeSetTarget(unit, target);

    public Entity? CurrentTarget(Entity unit) => _ai.NativeCurrentTarget(unit);

    public uint? HeldTargetId(Entity unit) => _ai.NativeHeldTargetId(unit);

    public Entity? TauntTarget(Entity unit) => _ai.NativeTauntTarget(unit);

    public bool TargetStillValid(Entity unit, Entity target) => _ai.NativeTargetStillValid(unit, target);

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && _ai.NativeTargetInAttackRange(unit, target);

    public void TurnOnAutoAttack(Entity unit, Entity? target) => _ai.NativeTurnOnAutoAttack(unit, target);

    public void TurnOffAutoAttack(Entity unit, double stopReason) => _ai.NativeTurnOffAutoAttack(unit, stopReason);

    public double AiState(Entity unit) => StateId(unit.MinionState);

    public void SetAiState(Entity unit, double state) => unit.MinionState = StateOf(state);

    // ---- ILuaAiOwnedEngine: the ownership half ----

    public void SetStateAndCloseToTarget(Entity unit, double state, Entity? target) =>
        _ai.NativeSetStateAndCloseToTarget(unit, StateOf(state), target);

    public void SetStateAndMove(Entity unit, double state, Position2 point) =>
        _ai.NativeSetStateAndMove(unit, StateOf(state), point);

    public Position2 GetFearLeashPoint(Entity unit) => _ai.NativeGetFearLeashPoint(unit);

    public Position2 MakeWanderPoint(Entity unit, Position2 origin, double distance) =>
        _ai.NativeMakeWanderPoint(unit, origin, distance);

    /// <summary>A lane minion has no owner; the scripts call this and read nil.</summary>
    public Entity? GetOwner(uint netId) => null;

    /// <summary>A lane minion redirects no gold; the scripts call this and read nil.</summary>
    public Entity? GetGoldRedirectTarget(uint netId) => null;

    public bool TargetInCancelAttackRange(Entity unit, Entity? target) =>
        target is not null && _ai.NativeTargetInCancelAttackRange(unit, target);

    // ---- ILuaAiMinionEngine: the natives only a lane minion names ----

    public void SetStateAndMoveToForwardNav(Entity unit, double state) =>
        _ai.NativeSetStateAndMoveToForwardNav(unit, StateOf(state));

    public bool LastAutoAttackFinished(Entity unit) => _ai.NativeLastAutoAttackFinished(unit);

    public void AddToIgnore(Entity unit, double seconds) => _ai.NativeAddToIgnore(unit, seconds);

    public double DistanceBetweenMeAndObject(Entity unit, Entity? other) =>
        _ai.NativeDistanceBetweenMeAndObject(unit, other);

    public bool IsMoving(Entity unit) => _ai.NativeIsMoving(unit);

    // ---- batch 451: the four natives the Odin scripts name (see ILuaAiMinionEngine) ----

    /// <summary>Batch 499: the nearest unit in acquisition range the minion may use (<see cref="MinionAiSystem.NativeFindUseableObjectInAcR"/>).</summary>
    public Entity? FindUseableObjectInAcR(Entity unit) => _ai.NativeFindUseableObjectInAcR(unit);

    /// <summary>Batch 499: whether the minion may use the unit (its character's Useable section).</summary>
    public bool IsObjectUseable(Entity unit, Entity? other) => _ai.NativeIsObjectUseable(unit, other);

    /// <summary>Batch 499: the minion goes to use the unit (<see cref="MinionAiSystem.NativeUseTarget"/>).</summary>
    public void UseTarget(Entity unit, Entity? other) => _ai.NativeUseTarget(unit, other);

    /// <summary>Reconstruction: walks the installed route like the forward nav; a reversed squad's route is its lane walked backward (<see cref="OdinWaves.Route"/>).</summary>
    public void SetStateAndMoveToBackwardNav(Entity unit, double state) =>
        _ai.NativeSetStateAndMoveToForwardNav(unit, StateOf(state));

    // ---- the AI_* <-> MinionAiState projection ----

    internal static double StateId(MinionAiState state) => state switch
    {
        MinionAiState.Halted => StateHalted,
        MinionAiState.Idle => StateIdle,
        MinionAiState.March => StateAttackMove,
        MinionAiState.Engage => StateAttacking,
        MinionAiState.Taunted => StateTaunted,
        MinionAiState.Feared => StateFeared,
        _ => StateHalted,
    };

    /// <summary>
    /// AI_HARDIDLE (2) is an idle like AI_IDLE. A minion script writes none of the remaining ids (its
    /// forward navigation is SetStateAndMoveToForwardNav(AI_ATTACKMOVESTATE), Minion.lua; batch 248 deleted
    /// the emulator's AI_RETREAT return transient); an unknown one reads as idle rather than raising inside a native call.
    /// </summary>
    internal static MinionAiState StateOf(double state) => state switch
    {
        StateHalted => MinionAiState.Halted,
        StateIdle or 2 => MinionAiState.Idle,
        StateAttackMove => MinionAiState.March,
        StateAttacking => MinionAiState.Engage,
        StateTaunted => MinionAiState.Taunted,
        StateFeared => MinionAiState.Feared,
        _ => MinionAiState.Idle,
    };
}
