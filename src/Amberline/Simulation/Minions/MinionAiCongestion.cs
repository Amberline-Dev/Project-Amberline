using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Minions;

public sealed partial class MinionAiSystem
{
    // Reconstruction: the emulator's wire step for the native SetStateAndCloseToTarget / MoveToForwardNav path request (unrecovered).
    // Retail is assumed to publish a path only when its AI changes it (unrecovered); this drops a repeat of the previous destination (within
    // 4 units) while the unit still follows a route, and when the planner finds no route it falls back to a straight
    // line that the client's own terrain test accepts or rejects ("-fallback", 0.00 per minion-minute in 8 headless
    // matches of 14 minutes).
    private bool IssueMove(Entity minion, Position2 destination, string reason,
        bool allowFallback = true, IReadOnlyList<Position2>? prevalidatedPath = null)
    {
        if (!double.IsFinite(destination.X) || !double.IsFinite(destination.Z)) return false;
        if (_lastStop.TryGetValue(minion.NetId, out var previous) &&
            DistanceSquared(previous.Point, destination) <= 4.0 * 4.0 &&
            minion.Waypoints.Count > 0) return true;
        IReadOnlyList<Position2> route;
        if (prevalidatedPath is not null)
        {
            if (prevalidatedPath.Count == 0) return false;
            route = _movement.InstallPath(minion, prevalidatedPath);
        }
        else
        {
            route = _movement.CommitMoveOrder(minion, EnginePlan(minion, destination));
        }
        if (route.Count == 0)
        {
            if (allowFallback && _navigation.HasMovementLineOfSight(minion.Position, destination))
            {
                EmitWaypoints(minion, _movement.InstallPath(minion, [minion.Position, destination]), reason + "-fallback");
                return true;
            }
            minion.Target = null;
            minion.Waypoints.Clear();
            // The stop's one-point path is installed on receipt: stopped and seated on the anchor at once.
            if (allowFallback) EmitWaypoints(minion, _movement.InstallStop(minion), reason + "-no-route");
            return false;
        }
        _lastStop[minion.NetId] = (destination, AimTarget(minion, destination));
        EmitWaypoints(minion, route, reason);
        return true;
    }

    /// <summary>
    /// The route of a minion order, planned the way the engine plans every unit's order: the base unit's order step
    /// calls the client's path builder, which runs the navigation mesh's A*
    /// (the grid planner) with the stuck-actor test and then
    /// the path smoother (<see cref="MovementSystem.PlanClientHeroRoute"/> is the port). A unit that stands still -- a
    /// planted attacker, a unit stuck for more than 50 ms, one at its path end -- closes the cells its body covers, so a minion closing
    /// on its target is routed around the planted wave in front of it instead of into it, where the solver used to hold it against the
    /// planted body until the 45-frame give-up let it walk through (batch 225: 138 to 165 stacked episodes over four seeds). This is the
    /// shared engine code: the client plans its own hero with it, and it stubs only the server's own entry points
    /// (<c>ServerPathfinding</c> and <c>RefreshPath</c>). The server knows every
    /// unit, so every unit counts. Unreachable (an empty path) when the engine plans nothing, as the order then degrades.
    /// </summary>
    private MovementSystem.MovePlan EnginePlan(Entity minion, Position2 destination)
    {
        var path = EngineRoute(minion, destination);
        return path is not null
            ? new MovementSystem.MovePlan(destination, path, MovementSystem.MoveStatus.Moving)
            : new MovementSystem.MovePlan(destination, Array.Empty<Position2>(), MovementSystem.MoveStatus.Unreachable);
    }

    /// <summary>
    /// The repath the solver's stuck site asks for (<see cref="OnFixedStepCompleted"/>): the order's goal is planned again from where
    /// the minion stands with the engine planner (<see cref="EngineRoute"/>), whose actor test now closes the cells of the bodies that
    /// hold it (a unit stuck for 50 ms counts as standing still). The minionLib collision callbacks answer false outside AI_TAUNTED and
    /// AI_FEARED (proto 0.10 and 0.11), which is the hook's 0, so a closing minion repaths. The goal is the order's own point (the chase
    /// seat or the overlap escape, <c>_lastStop</c>), or the one point a route has left; a plan that ends short of an occupied goal is
    /// walked as planned while the goal stays the order's (<c>m_GotoPosition</c> against <c>m_Path</c>).
    /// Reconstruction: <c>RefreshPath</c> and <c>ServerPathfinding</c> are server code the client stubs, so the goal of a lane march
    /// (the forward-navigation native's) and their own pacing (<c>m_TimeBetweenPathUpdates</c>,
    /// <c>m_RepathOnDuplicate</c>) are not recovered: a march with interior waypoints is left to the client's own bend,
    /// and a plan that changes nothing is not published again.
    /// </summary>
    private void RepathStuck(Entity minion)
    {
        if (minion.Waypoints.Count == 0 && minion.Target is null) return;
        // The anchor a route starts with is the point the unit stood on (CommitMoveOrder): the follower pops it at once.
        var current = new[] { minion.Position }
            .Concat((minion.Waypoints.Count > 0 ? minion.Waypoints : [minion.Target!.Value])
                .SkipWhile(point => DistanceSquared(point, minion.Position) <= 1.0)).ToArray();
        if (current.Length < 2) return;
        Position2? goal = minion.MinionState is MinionAiState.Engage or MinionAiState.Taunted &&
                          _lastStop.TryGetValue(minion.NetId, out var order) ? order.Point
            : current.Length == 2 ? current[1] : null;
        if (goal is not Position2 point) return;
        // A plan that finds nothing changes nothing: the next stuck frame asks again. Reconstruction: what the server's repath does
        // with an empty plan is not recovered; stopping there left a chasing minion standing on the blocked cell it had been held on
        // (batch 236, seed 1, 1326 s).
        if (EngineRoute(minion, point) is not { } path) return;
        var route = new[] { minion.Position }.Concat(path.Skip(1)).ToArray();
        var bounds = new NavigationBounds(_navigation.MinX, _navigation.MaxX, _navigation.MinZ, _navigation.MaxZ);
        var before = Geometry.ClientWaypoints(current, bounds);
        var after = Geometry.ClientWaypoints(route, bounds);
        if (before.Count == after.Count && before.Zip(after).All(pair => DistanceSquared(pair.First, pair.Second) <= 1.0)) return;
        EmitWaypoints(minion, _movement.InstallPath(minion, route), "stuck-repath");
    }

    /// <summary>
    /// The engine's route for <paramref name="minion"/> to <paramref name="goal"/>, or null when it plans nothing (<see cref="EnginePlan"/>).
    /// DIAGNOSTICS: a check that stands a scenario planner in for navigation (<see cref="NavigationGrid.FindPathOverride"/>) has it stand
    /// in for this planner too.
    /// </summary>
    private IReadOnlyList<Position2>? EngineRoute(Entity minion, Position2 goal)
    {
        var path = _navigation.FindPathOverride is not null
            ? _navigation.FindPath(minion.Position, goal)
            : _movement.PlanClientHeroRoute(minion, goal, static _ => true);
        return path is { Count: > 1 } ? path : null;
    }

    // The unit an order was taken against, as it stood when the order went out. EngageMove's
    // re-issue deadband measures the target's travel from here, so a deeper chase seat does not
    // also re-plan more often than the centre seat it replaced.
    private Position2 AimTarget(Entity minion, Position2 destination) =>
        minion.AttackTarget is uint held && _world.Get(held) is { } target
            ? target.Position : destination;

    private bool IssueOverlapMove(Entity minion, Position2 destination, string reason)
    {
        var obstacles = LocalBodyObstacles(minion, [minion.Position, destination]);
        var localPath = LocalOverlapPath(minion, destination, obstacles);
        if (localPath is not null)
        {
            EmitOverlapStopAttack(minion, destination);
            return IssueMove(minion, destination, reason, prevalidatedPath: localPath);
        }
        if (_navigation.IsPathingWalkable(destination))
        {
            EmitOverlapStopAttack(minion, destination);
            return IssueMove(minion, destination, reason);
        }
        return false;
    }

    private void EmitOverlapStopAttack(Entity minion, Position2 destination)
    {
        var sameLiveOrder = _lastStop.TryGetValue(minion.NetId, out var previous) &&
            DistanceSquared(previous.Point, destination) <= 4.0 * 4.0 &&
            minion.Waypoints.Count > 0;
        if (!sameLiveOrder) StopAttack(minion, "overlap-declip");
    }

    private void EmitWaypoints(Entity minion, IReadOnlyList<Position2> route, string reason)
    {
        var stored = route.ToArray();
        if (_packets.IsReferenceReady(minion))
            _packets.SendWaypoints(minion, stored, _world.Clock.SyncId(), reason);
        Anchor(minion, stored);
    }

    private void ClearMovement(Entity minion, string reason)
    {
        var hadOrder = minion.Waypoints.Count > 0 || minion.Target is not null;
        var hadWaypoints = minion.Waypoints.Count > 0;
        var hadTarget = minion.Target is not null;
        var clearObserver = ClearMovementObserver;
        double? movedSinceAnchor = clearObserver is null || minion.AnchorPath.Count == 0
            ? null
            : Distance(minion.Position, minion.AnchorPath[0]);
        minion.Waypoints.Clear();
        minion.Target = null;
        if (!hadOrder) return;
        _lastStop.Remove(minion.NetId);
        if (_packets.IsReferenceReady(minion))
        {
            try { clearObserver?.Invoke(minion, hadWaypoints, hadTarget, movedSinceAnchor); }
            catch { /* diagnostics must never change movement */ }
            // The stop is a one-point path on the minion's point, installed on receipt: seated on its anchor at once.
            _movement.InstallStop(minion);
            _packets.SendStop(minion, _world.Clock.SyncId(), reason);
        }
        Anchor(minion, [minion.Position]);
    }

    private static void Prune<T>(Dictionary<uint, T> cache, HashSet<uint> active)
    {
        foreach (var netId in cache.Keys.Where(netId => !active.Contains(netId)).ToArray())
            cache.Remove(netId);
    }
}
