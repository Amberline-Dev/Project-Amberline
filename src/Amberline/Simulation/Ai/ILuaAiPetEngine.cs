using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The pet half of an AI script's natives: everything <c>Pet.luaobj</c>, <c>UncontrollablePet.luaobj</c>
/// and <c>YorickPHPet.luaobj</c> ask for beyond <see cref="ILuaAiOwnedEngine"/> and the shared
/// targeting/combat surface. The "in position" point is engine-owned state the script only names:
/// <c>SetLastPosPointWithObj</c> fills it from an object and the order dispatch fills it from the
/// click, and <c>SetStateAndMoveInPos</c> / <c>DistanceBetweenObjectAndInPosSq</c> read it back
/// (<see cref="Pets.PetLuaAiEngine"/> binds these to the services the hand-written pet machine drove).
/// </summary>
public interface ILuaAiPetEngine : ILuaAiOwnedEngine
{
    /// <summary>
    /// <c>SetStateAndMoveInPos(state)</c>: take the state and walk to the in-position point — the
    /// hard move's own destination (<c>ORDER_PETHARDMOVE</c>), or the remembered point
    /// <c>SetLastPosPointWithObj</c> took when the owner is out of sight.
    /// </summary>
    void SetStateAndMoveInPos(Entity unit, double state);

    /// <summary><c>SetLastPosPointWithObj(object)</c>: the in-position point becomes that object's position.</summary>
    void SetLastPosPointWithObj(Entity unit, Entity? source);

    /// <summary>
    /// The in-position point becomes <paramref name="point"/>: the order dispatch fills it from the
    /// click before it runs <c>OnOrder</c>, which is what <c>ORDER_MOVETO</c>'s near/far rule
    /// (<c>FAR_MOVMEMENT_DISTANCE &lt; DistanceBetweenObjectAndInPosSq(me)</c>) and
    /// <c>ORDER_PETHARDMOVE</c>'s walk read.
    /// </summary>
    void SetLastPosPoint(Entity unit, Position2 point);

    /// <summary><c>DistanceBetweenObjectAndInPosSq(object)</c>: the squared distance from the object to the in-position point.</summary>
    double DistanceBetweenObjectAndInPosSq(Entity? unit);

    /// <summary><c>DistanceBetweenObjects(a, b)</c>.</summary>
    double DistanceBetweenObjects(Entity? first, Entity? second);

    /// <summary><c>GetPetReturnRadius(object)</c>: the distance at which a pet returns to its owner (<c>ai_DefaultPetReturnRadius</c>, 200).</summary>
    double GetPetReturnRadius(Entity? unit);

    /// <summary><c>GetCanMove(object)</c>: the unit may walk (not stunned, snared or displaced).</summary>
    bool GetCanMove(Entity unit);

    /// <summary><c>IsMoving()</c>: the calling unit is still following a route.</summary>
    bool IsMoving(Entity unit);

    /// <summary>
    /// <c>IsNetworkLocal()</c>: the AI instance that decides is the authoritative one.
    /// Reconstruction: every AI unit on this server is authoritative, so this is always true; retail
    /// split a pet's AI between the owner's client and the server and the flag told the two apart.
    /// </summary>
    bool IsNetworkLocal(Entity unit);

    /// <summary><c>OwnerIsMoving()</c>: the owner is walking or has a target of its own.</summary>
    bool OwnerIsMoving(Entity unit);

    /// <summary>
    /// <c>SetActorPositionFromObject(object, from)</c>: teleport the first object onto the second
    /// (the leash blink). The unit keeps its life; only its position and its route change.
    /// </summary>
    void SetActorPositionFromObject(Entity unit, Entity? from);

    /// <summary>
    /// <c>FindTargetInAcRUsingGoldRedirectTarget()</c>: the acquisition scan of
    /// <c>UncontrollablePet.luaobj</c> and <c>YorickPHPet.luaobj</c>, run around the hero the pet
    /// fights for rather than around the pet, so it stays with its master.
    /// </summary>
    Entity? FindTargetInAcquisitionRangeOfGoldRedirectTarget(Entity unit);

    /// <summary><c>Die(object, damagesource)</c>: the unit's own death (<c>DAMAGESOURCE_INTERNALRAW</c>).</summary>
    void Die(Entity unit, double damageSource);

    /// <summary><c>Say(message)</c>: the script's own debug line.</summary>
    void Say(Entity unit, string message);
}
