using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The half of an AI script's natives that any unit with an owner or a leash point asks for:
/// <c>Leashed.luaobj</c> for a jungle monster's camp, <c>Pet.luaobj</c>,
/// <c>UncontrollablePet.luaobj</c> and <c>YorickPHPet.luaobj</c> for a pet's master. The same
/// seven natives, the same semantics, two engines that already own the behaviour
/// (<see cref="Neutral.NeutralLuaAiEngine"/> and <see cref="Pets.PetLuaAiEngine"/>), so one binding in
/// <see cref="LuaAiInstance"/> serves both and a script that names one is never a silent no-op.
/// </summary>
public interface ILuaAiOwnedEngine : ILuaAiEngine
{
    /// <summary>
    /// <c>SetStateAndCloseToTarget(state, target)</c>: take the target and close on it. Writing the
    /// state first would run an attack-state re-path on the target being replaced, so the state,
    /// the target and the path go out in one step.
    /// </summary>
    void SetStateAndCloseToTarget(Entity unit, double state, Entity? target);

    /// <summary><c>SetStateAndMove(state, point)</c>: walk to a point (the fear wander).</summary>
    void SetStateAndMove(Entity unit, double state, Position2 point);

    /// <summary><c>GetFearLeashPoint()</c>: the point a fear wander is centred on.</summary>
    Position2 GetFearLeashPoint(Entity unit);

    /// <summary><c>MakeWanderPoint(point, distance)</c>: a random point within <paramref name="distance"/>.</summary>
    Position2 MakeWanderPoint(Entity unit, Position2 origin, double distance);

    /// <summary>
    /// <c>GetOwner(object)</c>: the unit's owner (a pet's master), or none. Takes the net id so a
    /// unit that already left the world still answers from what the engine remembered —
    /// <c>OnTargetLost</c> reads the owner of a dead pet that way. The pet scripts call it with no
    /// argument, which is the calling unit's own owner.
    /// </summary>
    Entity? GetOwner(uint netId);

    /// <summary><c>GetGoldRedirectTarget(object)</c>: the hero a kill through this unit credits.</summary>
    Entity? GetGoldRedirectTarget(uint netId);

    /// <summary><c>TargetInCancelAttackRange()</c>: inside the range a swing is kept, not restarted.</summary>
    bool TargetInCancelAttackRange(Entity unit, Entity? target);
}
