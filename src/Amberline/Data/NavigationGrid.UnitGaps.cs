using Amberline.Core;

namespace Amberline.Data;

public sealed partial class NavigationGrid
{
    // The terrain grid remains the coarse planner. This bounded visibility graph
    // supplies sub-cell passages between mobile bodies; every edge is checked
    // against ALL supplied obstacles after conversion to native waypoint units.
    private IReadOnlyList<Position2> FindContinuousUnitPath(Position2 start, Position2 goal,
        IReadOnlyList<NavigationObstacle> obstacles, Position2? authoritativeStart = null,
        double turnClearance = 0.0, bool heroPathing = false)
    {
        var bounds = new NavigationBounds(MinX, MaxX, MinZ, MaxZ);
        Position2 Wire(Position2 point) => Geometry.ClientWaypoints([point], bounds)[0];
        start = Wire(start);
        goal = Wire(goal);
        // Hero packets may use the actor's exact sub-cell position as an
        // alternate first origin. Their validator permits the client movement
        // grid on that first edge, then requires the selected hero/NPC pathing
        // grid for every tail edge. Minion congestion keeps its existing
        // movement-grid graph and default 260u pathing policy.
        var heroTerrainContract = authoritativeStart is not null || heroPathing;
        // The unit test is a few multiplications per body; the terrain ray walks every cell it crosses. Both
        // are pure, so asking the cheap one first returns the same answer and skips most rays in a crowd.
        bool Clear(Position2 from, Position2 to, bool first = false,
            bool escape = false)
        {
            PlanningWork++;
            return SegmentClearOfUnits(from, to, obstacles, escape) &&
                (heroPathing
                    ? (first || to == goal ? HasHeroMovementLineOfSight(from, to) :
                        HasHeroPathingLineOfSight(from, to))
                    : (first || !heroTerrainContract
                        ? HasMovementLineOfSight(from, to)
                        : HasPathingLineOfSight(from, to)));
        }
        bool FirstEdgeClear(Position2 to) => Clear(start, to, first: true, escape: true) &&
            (authoritativeStart is not Position2 exact ||
            ((heroPathing ? HasHeroMovementLineOfSight(exact, to) :
                HasMovementLineOfSight(exact, to)) &&
             FirstSegmentClearOfUnits(start, exact, to, obstacles)));
        if (FirstEdgeClear(goal)) return [start, goal];
        if (!IsMovementWalkable(goal) || !SegmentClearOfUnits(goal, goal, obstacles)) return [];

        const int maximumPoints = 192;
        const int sides = 12;
        var points = new List<Position2> { start, goal };
        var seen = new HashSet<Position2>(points);
        var turnObstacles = turnClearance <= 0.0 ? obstacles : obstacles.Select(obstacle =>
            obstacle with { Radius = obstacle.Radius + turnClearance }).ToArray();
        bool Add(Position2 point)
        {
            if (points.Count >= maximumPoints) return true;
            point = Wire(point);
            if (!IsMovementWalkable(point) || !SegmentClearOfUnits(point, point, turnObstacles)) return false;
            if (seen.Add(point)) points.Add(point);
            return true;
        }
        var nearby = obstacles.OrderBy(obstacle =>
            Distance(start, obstacle.Center) + Distance(obstacle.Center, goal) - 2 * obstacle.Radius)
            .ThenBy(obstacle => obstacle.Center.X).ThenBy(obstacle => obstacle.Center.Z)
            .Take(12).ToArray();

        // Gap centres come before peripheral samples so a dense crowd cannot
        // exhaust the vertex budget before its narrow passages are represented.
        for (var left = 0; left < nearby.Length; left++)
        for (var right = left + 1; right < nearby.Length; right++)
        {
            var a = nearby[left];
            var b = nearby[right];
            var distance = Distance(a.Center, b.Center);
            if (distance <= a.Radius + b.Radius || distance > a.Radius + b.Radius + CellSize * 2) continue;
            var along = (distance + a.Radius - b.Radius) / (2 * distance);
            var middle = new Position2(a.Center.X + (b.Center.X - a.Center.X) * along,
                a.Center.Z + (b.Center.Z - a.Center.Z) * along);
            if (turnClearance <= 0.0)
            {
                // Quantization may put the midpoint just outside a diagonal
                // slit. Adjacent samples are fallbacks only; a valid or
                // already-seen midpoint remains the preferred centre.
                if (!Add(middle))
                    foreach (var offset in new[] { new Position2(2, 0), new Position2(-2, 0), new Position2(0, 2), new Position2(0, -2) })
                        Add(middle + offset);
                continue;
            }

            // A waypoint inside the pinch becomes a corner that the stock
            // fixed-origin follower can cut. Put two possible turns far enough
            // along the open axis that a full movement-step ball around either
            // turn remains outside both physical discs. Their connecting edge
            // still crosses the exact, uninflated gap.
            var fromA = Distance(a.Center, middle);
            var fromB = Distance(b.Center, middle);
            var aTurn = a.Radius + turnClearance;
            var bTurn = b.Radius + turnClearance;
            var gateDistance = Math.Max(
                Math.Sqrt(Math.Max(0.0, aTurn * aTurn - fromA * fromA)),
                Math.Sqrt(Math.Max(0.0, bTurn * bTurn - fromB * fromB))) + 3.0;
            var tangent = new Position2(-(b.Center.Z - a.Center.Z) / distance,
                                         (b.Center.X - a.Center.X) / distance);
            var gate = new Position2(tangent.X * gateDistance, tangent.Z * gateDistance);
            Add(middle + gate);
            Add(middle - gate);
        }
        foreach (var obstacle in nearby)
        {
            var radius = (obstacle.Radius + turnClearance) / Math.Cos(Math.PI / sides) + 3.0;
            void At(double angle) => Add(new Position2(obstacle.Center.X + radius * Math.Cos(angle),
                obstacle.Center.Z + radius * Math.Sin(angle)));
            foreach (var endpoint in new[] { start, goal })
            {
                var distance = Distance(endpoint, obstacle.Center);
                var bearing = Math.Atan2(endpoint.Z - obstacle.Center.Z, endpoint.X - obstacle.Center.X);
                if (distance > radius)
                {
                    var tangent = Math.Acos(radius / distance);
                    At(bearing + tangent);
                    At(bearing - tangent);
                }
            }
            for (var side = 0; side < sides; side++) At(side * Math.PI * 2 / sides);
        }

        var costs = Enumerable.Repeat(double.PositiveInfinity, points.Count).ToArray();
        var parent = Enumerable.Repeat(-1, points.Count).ToArray();
        var closed = new bool[points.Count];
        var queue = new PriorityQueue<int, (double Estimate, int Vertex)>();
        costs[0] = 0;
        queue.Enqueue(0, (Distance(start, goal), 0));
        while (queue.TryDequeue(out var current, out _))
        {
            if (closed[current]) continue;
            if (current == 1)
            {
                var route = new List<Position2>();
                for (var vertex = 1; vertex >= 0; vertex = parent[vertex]) route.Add(points[vertex]);
                route.Reverse();
                return route;
            }
            closed[current] = true;
            for (var next = 1; next < points.Count; next++)
            {
                if (closed[next] || next == current) continue;
                var cost = costs[current] + Distance(points[current], points[next]);
                if (cost >= costs[next] - 1e-6 ||
                    (current == 0 ? !FirstEdgeClear(points[next]) : !Clear(points[current], points[next])))
                    continue;
                costs[next] = cost;
                parent[next] = current;
                queue.Enqueue(next, (cost + Distance(points[next], goal), next));
            }
        }
        return [];
    }
}
