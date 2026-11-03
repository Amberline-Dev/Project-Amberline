using Amberline.Core;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The jungle half of an AI script's natives: everything <c>Leashed.luaobj</c> asks for beyond
/// <see cref="ILuaAiOwnedEngine"/> and the shared targeting/combat surface of
/// <see cref="ILuaAiEngine"/>. The leash point, the roam state, the leash counter and the
/// valid-target list are engine-owned state the script only names; the movement and order requests
/// are the same path and stop mechanics the hand-written monster machine drove, so a scripted camp
/// walks, resets and regenerates through the services the match already knows
/// (<see cref="Neutral.NeutralLuaAiEngine"/> binds these).
/// <c>GetState</c>/<c>SetState</c> read and write <see cref="ILuaAiEngine.AiState"/>, the same
/// store an external <see cref="Entity.NeutralState"/> write is adopted into; the natives that
/// carry a state id also hand it here so the world half can project the observable state string.
/// </summary>
public interface ILuaAiLeashedEngine : ILuaAiOwnedEngine
{
    /// <summary><c>SetStateAndMoveToLeashedPos(state)</c>: the retreat walk home.</summary>
    void SetStateAndMoveToLeashedPos(Entity unit, double state);

    /// <summary><c>SetMyLeashedPos()</c>: remember the leash point (the camp's home, at spawn).</summary>
    void SetMyLeashedPos(Entity unit);

    /// <summary><c>GetMyLeashedPos()</c>.</summary>
    Position2 GetMyLeashedPos(Entity unit);

    /// <summary><c>GetDistToLeashedPos()</c>: the unit's own distance to its leash point.</summary>
    double GetDistToLeashedPos(Entity unit);

    /// <summary><c>GetDistToRetreat()</c>: how far the retreating unit still is from home.</summary>
    double GetDistToRetreat(Entity unit);

    /// <summary><c>IsMovementStopped()</c>: the unit is no longer following a route.</summary>
    bool IsMovementStopped(Entity unit);

    /// <summary><c>GetRoamState()</c>: <c>HOSTILE</c> (1) while engaged, <c>INACTIVE</c> (0) otherwise.</summary>
    double GetRoamState(Entity unit);

    /// <summary><c>SetRoamState(roam)</c>.</summary>
    void SetRoamState(Entity unit, double roam);

    /// <summary><c>GetOriginalState()</c>: the roam state the unit was spawned with.</summary>
    double GetOriginalState(Entity unit);

    /// <summary><c>GetLeashCounter()</c>: leash replacements since the last arrival home.</summary>
    double GetLeashCounter(Entity unit);

    /// <summary><c>SetLeashCounter(counter)</c>.</summary>
    void SetLeashCounter(Entity unit, double counter);

    /// <summary><c>AddValidTarget(target)</c>: the list <c>FindNewTarget</c> chooses from.</summary>
    void AddValidTarget(Entity unit, Entity? target);

    /// <summary><c>ClearValidTargets()</c>: <c>Retreat()</c> empties the list.</summary>
    void ClearValidTargets(Entity unit);

    /// <summary><c>FindValidTargetNearPosition(point, radius)</c>: the listed candidate nearest the point.</summary>
    Entity? FindValidTargetNearPosition(Entity unit, Position2 position, double radius);

    /// <summary><c>GetHP()</c> / <c>GetMaxHP()</c> / <c>SetHP(hp)</c>.</summary>
    double GetHP(Entity unit);
    double GetMaxHP(Entity unit);
    void SetHP(Entity unit, double hp);

    /// <summary><c>DistanceBetweenObjectAndPoint(object, point)</c>.</summary>
    double DistanceBetweenObjectAndPoint(Entity? unit, Position2 point);

    /// <summary><c>Error(message)</c>: the script's own report, logged like a script failure.</summary>
    void Error(Entity unit, string message);
}
