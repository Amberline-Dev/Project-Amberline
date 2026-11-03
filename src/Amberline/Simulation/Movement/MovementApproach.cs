using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Movement;

public sealed partial class MovementSystem
{
    /// <summary>
    /// Plans the approach to <paramref name="stop"/> units from the target centre. A hero asks the client planner for the radial
    /// endpoint once and takes its route: the retail client plans straight at the target (<c>IssueOrder</c> plans to the
    /// target's position, with the end-cell snap) and nothing in the shipped scripts or the client picks a firing angle, so the
    /// mirrored-angle search a hero used to run is gone. Reconstruction: every other unit (minions, pets) keeps the radial endpoint
    /// first, then up to fifteen mirrored angles when that route is a detour (retail's choice for these units is native server code, not recovered); a route counts only if it ends within
    /// <paramref name="reach"/> of the target. Planning has no side effects; the caller commits the plan, whose Goal is the endpoint.
    /// Pursuit and unit-targeted spell approaches share it. <paramref name="aimAngle"/> is a reserved firing angle: the endpoint
    /// sits on it and the mirror search runs only when the terrain refuses that seat.
    /// </summary>
    internal MovePlan ApproachWithin(Entity hero, Entity target,
        double stop, double reach, Func<Entity, bool>? visibleTo = null, double? aimAngle = null) =>
        ApproachWithin(hero, target, stop, reach, out _, visibleTo, aimAngle);

    // Keep exhausted terrain connectivity separate from a rejected endpoint,
    // footprint/follower validation or deferred body planning.
    internal MovePlan ApproachWithin(Entity hero, Entity target,
        double stop, double reach, out bool noTerrainRoute, Func<Entity, bool>? visibleTo = null,
        double? aimAngle = null)
    {
        var distance = Math.Sqrt(DistanceSquared(hero.Position, target.Position));
        // Reconstruction: a co-attacker stops on its own reserved firing angle, never on its own
        // bearing. Two minions closing the same structure on neighbouring bearings plan almost the
        // same radial endpoint and walk into each other there (b153, seed 5205: two melee endpoints
        // 57 units apart against a 70-unit pair clearance, the pair then held the interpenetration
        // for the rest of the siege). The endpoint the reserved angle names is the point the fan
        // claim already reserved; the mirrored search is the terrain fallback for a seat the map
        // refuses, not a shorter alternative to it.
        var destination = aimAngle is double seat
            ? new Position2(target.Position.X + Math.Cos(seat) * stop,
                target.Position.Z + Math.Sin(seat) * stop)
            : RadialPoint(hero, target, distance, stop);
        if (hero.UnitType == UnitType.Hero)
        {
            var heroPlan = PlanApproach(hero, destination, visibleTo, out var heroTerrainRoute);
            noTerrainRoute = !heroTerrainRoute;
            return heroPlan;
        }
        MovePlan? selectedPlan = null;
        var baseAngle = aimAngle ?? Math.Atan2(hero.Position.Z - target.Position.Z,
            hero.Position.X - target.Position.X);
        var bestLength = double.PositiveInfinity;
        var radialPlan = PlanApproach(hero, destination, visibleTo, out var radialTerrainRoute);
        noTerrainRoute = !radialTerrainRoute;
        var radialPath = radialPlan.Path;
        if (radialPlan.Status == MoveStatus.Moving &&
            ApproachPathUsable(radialPath, hero, target, reach))
        {
            selectedPlan = radialPlan;
            bestLength = PathLength(radialPath);
            // A reserved seat with a walkable route is the seat. A shorter unreserved firing angle
            // is the duplicate destination the reservation exists to prevent.
            if (aimAngle is not null) return selectedPlan;
        }
        // A straight route to the endpoint is already the geometric minimum. Keep only the small
        // client-coordinate quantization allowance; a full navigation-cell diagonal could hide
        // a materially shorter firing angle.
        var geometricMinimum = Math.Max(0.0,
            Math.Sqrt(DistanceSquared(hero.Position, destination)));
        var radialIsNearMinimum = selectedPlan is not null && bestLength <= geometricMinimum + 6.0;
        var offsets = radialIsNearMinimum
            ? Array.Empty<double>()
            : new[] { 0.3, -0.3, 0.6, -0.6, 0.9, -0.9, 1.2, -1.2,
                1.6, -1.6, 2.0, -2.0, 2.5, -2.5, Math.PI };
        foreach (var offset in offsets)
        {
            var candidate = new Position2(target.Position.X + Math.Cos(baseAngle + offset) * stop,
                target.Position.Z + Math.Sin(baseAngle + offset) * stop);
            var candidatePlan = PlanApproach(hero, candidate, visibleTo, out var candidateTerrainRoute);
            noTerrainRoute &= !candidateTerrainRoute;
            var candidatePath = candidatePlan.Path;
            if (candidatePlan.Status != MoveStatus.Moving || candidatePath.Count <= 1 ||
                !ApproachPathUsable(candidatePath, hero, target, reach)) continue;
            var length = PathLength(candidatePath);
            // Keep the first candidate on an exact tie. The order is mirrored
            // around the target, so a symmetric scene has a stable choice
            // without introducing a global side preference.
            if (selectedPlan is not null && length >= bestLength - 1e-6) continue;
            selectedPlan = candidatePlan;
            bestLength = length;
        }
        return selectedPlan ?? radialPlan;
    }

    /// <summary>
    /// A hero's pursuit route: the route the stock client plans for an AttackTo, which is a path to the target's position
    /// (<c>IssueOrder</c> hands the target's actor position to the client's path builder for a target over 1000
    /// units away). The planner's end-cell snap (<c>SetToNearestGetToAbleCell</c>) then moves the end off the target's own cell to the
    /// nearest cell its body leaves free, next to the target, so the route ends within reach in all but a crowded scene; a radial
    /// stop point at the reach edge (the earlier plan) sat 6 units inside reach, and any unit standing on it moved the snapped end
    /// past reach (22 % of the plans of a headless match). The hero stops where combat plants it, as it always did. Reconstruction:
    /// the retail server's own chase (<c>SetStateAndCloseToTarget</c>) is native; the client's AttackTo plan is the nearest evidence.
    /// </summary>
    internal MovePlan PlanChase(Entity hero, Entity target, Func<Entity, bool>? visibleTo) =>
        PlanApproach(hero, target.Position, visibleTo, out _);

    // Every unit's approach is the engine's plan of an order to the point (PlanOrder).
    private MovePlan PlanApproach(Entity unit, Position2 destination,
        Func<Entity, bool>? visibleTo, out bool terrainRoute)
    {
        var plan = PlanOrder(unit, destination, visibleTo);
        terrainRoute = plan.Status != MoveStatus.Unreachable;
        return plan;
    }

    // Reconstruction: retail server route trimming is unrecovered. A vertex of the installed route inside attack reach ends the
    // pursuit there: the prefix of a route the client planner made is itself a valid route, so it needs no new validation.
    internal IReadOnlyList<Position2>? ShortenHeroApproach(Entity hero, Entity target, double reach)
    {
        if (!_heroOrders.TryGetValue(hero.NetId, out var order) ||
            !ReferenceEquals(hero.Waypoints, order.Route)) return null;
        var stop = Math.Max(0.0, reach - 6.0);
        for (var index = 0; index + 1 < hero.Waypoints.Count; index++)
        {
            var endpoint = hero.Waypoints[index];
            if (DistanceSquared(endpoint, target.Position) > stop * stop) continue;
            var path = ClientWaypoints(new[] { hero.Position }.Concat(hero.Waypoints.Take(index + 1)));
            hero.Waypoints.RemoveRange(index + 1, hero.Waypoints.Count - index - 1);
            _heroOrders[hero.NetId] = order with { Goal = endpoint };
            hero.LastAttackStopPoint = endpoint;
            // The shortened route is a new path: the install stops the actor and seats it on the
            // path's first point, the hero's lattice anchor.
            hero.FrameVelocity = Position2.Zero;
            hero.Position = path[0];
            return path;
        }
        return null;
    }

    internal bool ApproachPathUsable(IReadOnlyList<Position2> path,
        Entity unit, Entity target, double reach) =>
        path.Count > 1 && DistanceSquared(path[^1], target.Position) <= reach * reach &&
        (!IsStructureTarget(target) || RouteClearOfObstacle(path,
            new NavigationObstacle(target.Position, BodyClearance(unit, target))));

    private static Position2 RadialPoint(Entity unit, Entity target, double distance, double stop) =>
        distance <= 1e-9 ? target.Position + new Position2(stop, 0.0) :
        new(target.Position.X + (unit.Position.X - target.Position.X) / distance * stop,
            target.Position.Z + (unit.Position.Z - target.Position.Z) / distance * stop);

    // A turret is a body the route must clear; the Nexus and the inhibitors have none (their footprint is stamped cells the route already avoids).
    private static bool IsStructureTarget(Entity target) => target.UnitType == UnitType.Turret;

    private static double PathLength(IReadOnlyList<Position2> path)
    {
        var length = 0.0;
        for (var index = 1; index < path.Count; index++)
            length += Math.Sqrt(DistanceSquared(path[index - 1], path[index]));
        return length;
    }
}
