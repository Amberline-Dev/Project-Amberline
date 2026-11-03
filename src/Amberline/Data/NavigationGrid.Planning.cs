using Amberline.Core;

namespace Amberline.Data;

public sealed partial class NavigationGrid
{
    public void ResetPathQueryCounters()
    {
        FindPathCalls = 0;
        FindPathAroundUnitsCalls = 0;
        ReachabilitySearches = AStarSearches = PathSearchExpansions = PlanningWork = 0;
    }

    public IReadOnlyList<Position2> FindPath(Position2 start, Position2 goal)
    {
        FindPathCalls++;
        if (FindPathOverride is { } overridePlanner)
            return overridePlanner(start, goal)
                ?? throw new InvalidOperationException("Navigation path override returned null.");
        var (_, smoothed) = FindPathWithRaw(start, goal, IsPathingWalkable);
        return smoothed;
    }

    // Heroes use the same A* and smoothing implementation, but with the
    // client-sized turret stamp. Keep the public/default entry on the wider
    // NPC policy so existing corpus and override seams remain unchanged.
    internal IReadOnlyList<Position2> FindHeroPath(Position2 start, Position2 goal)
    {
        FindPathCalls++;
        if (FindPathOverride is { } overridePlanner)
            return overridePlanner(start, goal)
                ?? throw new InvalidOperationException("Navigation path override returned null.");
        var wireVertices = new Dictionary<NavCell, Position2>();
        Position2 WireVertex(NavCell cell)
        {
            if (!wireVertices.TryGetValue(cell, out var point))
                wireVertices[cell] = point = HeroCellToWire(cell);
            return point;
        }
        // Every hero chord is tested on the movement view (NotPassable and the 140-unit turret stamps), as the client's chord walk
        // does (its walk tests the NotPassable bit and the bodies only). Batch 223 item 1 retired the 65-unit dilation and
        // the 25-unit margin from chords; the A* still walks the dilated hero view.
        var (_, smoothed) = FindPathWithRaw(start, goal, IsHeroPathingWalkable,
            HasHeroMovementLineOfSight, HasHeroMovementLineOfSight,
            HasHeroMovementLineOfSight, WireVertex);
        return smoothed;
    }

    public (IReadOnlyList<Position2> Raw, IReadOnlyList<Position2> Smoothed) FindPathWithRaw(
        Position2 startPoint, Position2 goalPoint)
        => FindPathWithRaw(startPoint, goalPoint, IsPathingWalkable);

    private (IReadOnlyList<Position2> Raw, IReadOnlyList<Position2> Smoothed) FindPathWithRaw(
        Position2 startPoint, Position2 goalPoint, Func<NavCell, bool> pathingChecker,
        Func<Position2, Position2, bool>? segmentChecker = null,
        Func<Position2, Position2, bool>? firstSegmentChecker = null,
        Func<Position2, Position2, bool>? terminalSegmentChecker = null,
        Func<NavCell, Position2>? pointMapper = null,
        Func<Position2, Position2, bool>? chordFilter = null)
    {
        EnsureFinite(startPoint, nameof(startPoint));
        EnsureFinite(goalPoint, nameof(goalPoint));
        if (pointMapper is not null)
            goalPoint = ClientPoint(goalPoint);
        var start = WorldToCell(startPoint);
        var goal = WorldToCell(goalPoint);
        if (!IsInBounds(start) || !IsInBounds(goal)) return (Array.Empty<Position2>(), Array.Empty<Position2>());

        // A hero click may land in a movement-walkable cell that is inside
        // the wider hero-planning stamp.  Its final chord still has to pass
        // the planning grid at every cell before the terminal movement cell.
        // Keep this narrow wrapper local to the terminal approach; ordinary
        // LOS and generic NPC callers retain their existing checker.
        Func<Position2, Position2, bool>? terminalRouteChecker =
            segmentChecker is not null && terminalSegmentChecker is not null
                ? (source, target) =>
                {
                    if (!terminalSegmentChecker(source, target)) return false;
                    var sourceCell = WorldToCell(source);
                    var terminalCell = WorldToCell(target);
                    return HasExactLineOfSight(source, target, cell =>
                        cell == terminalCell ||
                        (cell == sourceCell && !pathingChecker(sourceCell))
                            ? IsMovementWalkable(cell)
                            : pathingChecker(cell));
                }
                : terminalSegmentChecker;

        var goalSnapped = false;
        var movementGoal = segmentChecker is not null && IsMovementWalkable(goal);
        var requestedGoalIsPlanningWalkable = pathingChecker(goal);
        var goalCandidates = new List<(NavCell Cell, bool PreserveClick, bool MovementConnector)>();
        var seenGoalCandidates = new HashSet<NavCell>();
        void AddGoalCandidates(IEnumerable<NavCell> candidates, bool preserveClick,
            bool movementConnector)
        {
            foreach (var candidate in candidates)
                if (seenGoalCandidates.Add(candidate))
                    goalCandidates.Add((candidate, preserveClick, movementConnector));
        }

        if (segmentChecker is not null && movementGoal)
        {
            if (requestedGoalIsPlanningWalkable)
                AddGoalCandidates([goal], preserveClick: true, movementConnector: false);
            else if (terminalRouteChecker is not null)
                AddGoalCandidates(NearestCandidates(goal, pathingChecker,
                    point => terminalRouteChecker(pointMapper?.Invoke(point) ?? CellToWorld(point), goalPoint),
                    maxRadius: 50, maximum: 4), preserveClick: true,
                    movementConnector: false);
            if (terminalSegmentChecker is not null)
                AddGoalCandidates(NearestCandidates(goal, pathingChecker,
                    point => HasHeroMovementLineOfSight(
                        pointMapper?.Invoke(point) ?? CellToWorld(point), goalPoint),
                    maxRadius: 50, maximum: 4), preserveClick: true,
                    movementConnector: true);
        }
        // PATH-02: the stock selector owns ordinary blocked clicks. Starts in
        // the planning margin or without a usable planning edge still need the
        // captured escape/bridge policy's destination candidates. Otherwise a
        // singleton reachable component can select the trapped start itself,
        // or a changed endpoint can invalidate its turn-safe fallback.
        var resolveBlockedGoal = segmentChecker is not null && !movementGoal &&
            pathingChecker(start) && Neighbors.Any(offset =>
            {
                var next = new NavCell(start.Column + offset.Dc, start.Row + offset.Dr);
                var point = pointMapper?.Invoke(next) ?? CellToWorld(next);
                return pathingChecker(next) &&
                    (offset.Dc == 0 || offset.Dr == 0 ||
                     !DiagonalCornerBlocked(start, offset.Dc, offset.Dr, pathingChecker)) &&
                    segmentChecker(startPoint, point) &&
                    (pointMapper is null || segmentChecker(ClientPoint(startPoint), point));
            });
        var requestedGoal = goal;
        if (resolveBlockedGoal)
            AddGoalCandidates([goal], preserveClick: false, movementConnector: false);
        else if (segmentChecker is not null && (!movementGoal || goalCandidates.Count == 0))
            AddGoalCandidates(NearestCandidates(goal, pathingChecker,
                _ => true, maxRadius: 50, maximum: 4), preserveClick: false,
                movementConnector: false);

        if (segmentChecker is null && !pathingChecker(goal))
        {
            var goalOnFloor = IsWalkable(goal);
            var approach = Nearest(goal, pathingChecker, maxRadius: 50);
            if (approach is null) return (Array.Empty<Position2>(), Array.Empty<Position2>());
            goal = approach.Value;
            goalSnapped = !goalOnFloor;
        }
        else if (segmentChecker is not null && !pathingChecker(goal) && goalCandidates.Count == 0)
        {
            var approach = Nearest(goal, pathingChecker, maxRadius: 50);
            if (approach is null) return (Array.Empty<Position2>(), Array.Empty<Position2>());
            AddGoalCandidates([approach.Value], preserveClick: false,
                movementConnector: false);
        }

        if (goalCandidates.Count > 0)
        {
            goal = goalCandidates[0].Cell;
            goalSnapped = !goalCandidates[0].PreserveClick;
        }
        else AddGoalCandidates([goal], preserveClick: !goalSnapped, movementConnector: false);

        if (!resolveBlockedGoal && start == goal)
        {
            var same = new[] { startPoint, goalSnapped ? pointMapper?.Invoke(goal) ?? CellToWorld(goal) : goalPoint };
            return (same, same);
        }

        // A direct route is still a pathing-grid decision and retains the
        // 65-unit terrain dilation. The movement-grid checker is reserved for
        // the exact first edge when A* supplies a route out of that shell.
        var directChecker = segmentChecker;
        var directClear = directChecker is null
            ? LineOfSight(start, goal, pathingChecker)
            : !requestedGoalIsPlanningWalkable && !goalSnapped &&
              !goalCandidates[0].MovementConnector && terminalRouteChecker is not null
                ? terminalRouteChecker(startPoint, goalPoint)
                : directChecker(startPoint, goalSnapped ? pointMapper?.Invoke(goal) ?? CellToWorld(goal) : goalPoint);
        if (!resolveBlockedGoal && directClear)
        {
            var direct = new[] { startPoint, goalSnapped ? pointMapper?.Invoke(goal) ?? CellToWorld(goal) : goalPoint };
            // A single chord is still a chord: a hero route that scrapes a wall corner takes the A* route instead.
            if (chordFilter is null || chordFilter(direct[0], direct[1])) return (direct, direct);
        }

        var edgeChecker = segmentChecker is null ? null :
            (Func<NavCell, NavCell, bool>)((from, to) =>
            {
                return (from == start ? firstSegmentChecker ?? segmentChecker : segmentChecker)(
                    from == start ? startPoint : pointMapper?.Invoke(from) ?? CellToWorld(from),
                    pointMapper?.Invoke(to) ?? CellToWorld(to));
            });
        List<NavCell>? cells = null;
        var movementConnector = false;
        bool TryRouteFrom(NavCell routeStart)
        {
            if (resolveBlockedGoal)
            {
                var reachable = ReachableCells(routeStart, pathingChecker, edgeChecker);
                // The walkability test runs first: a wall cell can never be in the
                // component, and asking it would flood the whole component to say so.
                var selected = NearestReachableGoal(requestedGoal, start,
                    candidate => pathingChecker(candidate) && reachable.Contains(candidate));
                if (selected is null) return false;
                cells = AStar(routeStart, selected.Value, pathingChecker, edgeChecker: edgeChecker);
                if (cells is null) return false;
                goal = selected.Value;
                goalSnapped = true;
                movementConnector = false;
                return true;
            }
            foreach (var candidate in goalCandidates)
            {
                var candidatePath = AStar(routeStart, candidate.Cell, pathingChecker,
                    edgeChecker: edgeChecker);
                if (candidatePath is null) continue;
                cells = candidatePath;
                goal = candidate.Cell;
                goalSnapped = !candidate.PreserveClick;
                movementConnector = candidate.MovementConnector;
                return true;
            }
            return false;
        }
        if (!TryRouteFrom(start))
        {
            var escapes = new List<NavCell>();
            if (segmentChecker is not null)
            {
                // An exact or wire start can have no usable cell-centre edge.
                // Connect to a nearby planning vertex on movement floor, then
                // keep that vertex when smoothing the remainder of the route.
                var firstChecker = terminalSegmentChecker ?? firstSegmentChecker ?? segmentChecker;
                var wireStart = ClientPoint(startPoint);
                escapes.AddRange(NearestCandidates(start, pathingChecker,
                    candidate => candidate != start &&
                        firstChecker(startPoint, pointMapper?.Invoke(candidate) ?? CellToWorld(candidate)) &&
                        (pointMapper is null || firstChecker(wireStart,
                            pointMapper.Invoke(candidate))),
                    maxRadius: 50, maximum: 4));
            }
            else if (!pathingChecker(start) && Nearest(start, pathingChecker, maxRadius: 50) is NavCell legacyEscape &&
                     legacyEscape != start)
            {
                escapes.Add(legacyEscape);
            }

            foreach (var escape in escapes)
                if (TryRouteFrom(escape)) break;
        }
        if (cells is null) return (Array.Empty<Position2>(), Array.Empty<Position2>());

        var escapedStart = segmentChecker is not null && cells[0] != start;
        // Smooth the ordinary planning route only as far as the selected
        // terminal vertex. A movement-floor connector must survive smoothing;
        // otherwise a long approach can cut across the terrain planning margin.
        var smoothingGoal = movementConnector
            ? pointMapper?.Invoke(goal) ?? CellToWorld(goal) : goalPoint;
        var smoothedCells = SmoothPath(cells, pathingChecker, segmentChecker,
            escapedStart ? null : firstSegmentChecker,
            movementConnector ? segmentChecker : terminalRouteChecker,
            escapedStart ? null : startPoint, smoothingGoal,
            goalSnapped || movementConnector, pointMapper, chordFilter);
        var smoothed = smoothedCells.Select(cell => pointMapper?.Invoke(cell) ?? CellToWorld(cell)).ToList();
        if (smoothed.Count > 0)
        {
            if (escapedStart)
            {
                var firstChecker = terminalSegmentChecker ?? firstSegmentChecker ?? segmentChecker;
                var escapePoint = smoothed[0];
                if (firstChecker is null || !firstChecker(startPoint, escapePoint) ||
                    (pointMapper is not null && !firstChecker(ClientPoint(startPoint), escapePoint)))
                    return (Array.Empty<Position2>(), Array.Empty<Position2>());
                smoothed.Insert(0, startPoint);
            }
            else smoothed[0] = startPoint;
            if (!goalSnapped)
            {
                if (movementConnector)
                {
                    if (Distance(smoothed[^1], goalPoint) > 1e-9)
                        smoothed.Add(goalPoint);
                }
                else
                {
                    var endpointChecker = terminalRouteChecker ?? segmentChecker;
                    if (segmentChecker is null || smoothed.Count < 2 ||
                        endpointChecker is null || endpointChecker(smoothed[^2], goalPoint))
                        smoothed[^1] = goalPoint;
                    else
                        // Keep the requested click as the endpoint after one
                        // final same-cell step. The centre route edge is legal;
                        // the direct preceding chord is not.
                        smoothed.Add(goalPoint);
                }
            }
        }
        var raw = cells.Select(cell => pointMapper?.Invoke(cell) ?? CellToWorld(cell)).ToArray();
        return (raw, smoothed);
    }

    public IReadOnlyList<Position2> FindPathAroundUnits(
        Position2 start, Position2 goal, IEnumerable<NavigationObstacle> sourceObstacles,
        bool preservePhysicalGaps = false, Position2? authoritativeStart = null,
        double turnClearance = 0.0, bool heroPathing = false,
        bool movementFloor = false, bool avoidBlockedCorners = false)
    {
        FindPathAroundUnitsCalls++;
        EnsureFinite(start, nameof(start));
        EnsureFinite(goal, nameof(goal));
        if (authoritativeStart is Position2 exactStart)
            EnsureFinite(exactStart, nameof(authoritativeStart));
        if (!double.IsFinite(turnClearance) || turnClearance < 0.0)
            throw new ArgumentOutOfRangeException(nameof(turnClearance));
        ArgumentNullException.ThrowIfNull(sourceObstacles);
        movementFloor &= heroPathing;
        avoidBlockedCorners &= movementFloor;
        var sourcePadding = preservePhysicalGaps ? 0.0 : 3.0;
        var obstacles = sourceObstacles.Select(obstacle =>
        {
            if (!double.IsFinite(obstacle.Center.X) || !double.IsFinite(obstacle.Center.Z) ||
                !double.IsFinite(obstacle.Radius) || obstacle.Radius < 0.0)
                throw new ArgumentException("Obstacles must have finite non-negative radii", nameof(sourceObstacles));
            return new NavigationObstacle(obstacle.Center, obstacle.Radius + sourcePadding);
        }).ToArray();
        bool PathingWalkable(NavCell cell) =>
            movementFloor && heroPathing
                ? IsMovementWalkable(cell)
                : heroPathing ? IsHeroPathingWalkable(cell) : IsPathingWalkable(cell);
        if (FindPathAroundUnitsOverride is { } overridePlanner)
            return overridePlanner(start, goal, obstacles);
        if (!SegmentClearOfUnits(goal, goal, obstacles)) return Array.Empty<Position2>();

        if (preservePhysicalGaps && !movementFloor)
        {
            var continuous = FindContinuousUnitPath(start, goal, obstacles, authoritativeStart,
                turnClearance, heroPathing);
            if (continuous.Count > 1) return continuous;
        }

        var startCell = WorldToCell(start);
        var goalCell = WorldToCell(goal);
        var bounds = (
            MinColumn: Math.Min(startCell.Column, goalCell.Column) - 10,
            MaxColumn: Math.Max(startCell.Column, goalCell.Column) + 10,
            MinRow: Math.Min(startCell.Row, goalCell.Row) - 10,
            MaxRow: Math.Max(startCell.Row, goalCell.Row) + 10);
        // The normal minion planner uses a whole-cell safety margin because
        // its route is also a client-grid occupancy decision.  Heroes already
        // validate the final polyline against their true circle radii; adding
        // that margin here closes real gaps between bodies and sends them on
        // large detours.  Keep the old conservative default for all existing
        // callers, with a narrow hero-only path to use physical clearance.
        var padding = preservePhysicalGaps ? 0.0 : CellSize / Math.Sqrt(2.0);
        var inflated = obstacles.Select(obstacle =>
            new NavigationObstacle(obstacle.Center, obstacle.Radius + padding)).ToArray();
        var cache = new Dictionary<NavCell, bool>();
        bool LocalWalkable(NavCell cell)
        {
            if (cache.TryGetValue(cell, out var result)) return result;
            result = cell.Column >= bounds.MinColumn && cell.Column <= bounds.MaxColumn &&
                     cell.Row >= bounds.MinRow && cell.Row <= bounds.MaxRow &&
                     PathingWalkable(cell) &&
                     SegmentClearOfUnits(CellToWorld(cell), CellToWorld(cell), inflated);
            cache[cell] = result;
            return result;
        }

        Func<NavCell, NavCell, bool>? edgeChecker = avoidBlockedCorners
            ? (from, to) => from.Column == to.Column || from.Row == to.Row ||
                (PathingWalkable(new NavCell(from.Column, to.Row)) &&
                 PathingWalkable(new NavCell(to.Column, from.Row)))
            : null;

        List<NavCell> Connections(Position2 point, bool escape,
            Position2? alternateStart = null)
        {
            var cell = WorldToCell(point);
            var center = CellToWorld(cell);
            bool AlternateClear(Position2 destination) =>
                alternateStart is not Position2 exact ||
                (FirstSegmentClearOfUnits(point, exact, destination, obstacles) &&
                 (heroPathing ? HasHeroMovementLineOfSight(exact, destination) :
                    HasMovementLineOfSight(exact, destination)));
            if (LocalWalkable(cell) && SegmentClearOfUnits(point, center, obstacles, escape) &&
                AlternateClear(center))
                return [cell];
            var found = new List<(double Distance, NavCell Cell)>();
            for (var dc = -4; dc <= 4; dc++)
            for (var dr = -4; dr <= 4; dr++)
            {
                var candidate = new NavCell(cell.Column + dc, cell.Row + dr);
                if (!LocalWalkable(candidate)) continue;
                var destination = CellToWorld(candidate);
                if (SegmentClearOfUnits(point, destination, obstacles, escape) &&
                    (heroPathing
                        ? HasHeroMovementLineOfSight(point, destination)
                        : LineOfSight(cell, candidate, IsMovementWalkable)) &&
                    AlternateClear(destination))
                    found.Add((Distance(point, destination), candidate));
            }
            return found.OrderBy(item => item.Distance).Take(2).Select(item => item.Cell).ToList();
        }

        var starts = Connections(start, escape: true, alternateStart: authoritativeStart);
        var goals = Connections(goal, escape: false);
        foreach (var entry in starts)
        foreach (var end in goals)
        {
            // Reconstruction: retail's server A* is not recovered; this assumes it weighs every walkable cell the same, so its raw
            // route hugs walls. A hero movement-floor route pays double for planning-margin
            // cells, so it leaves the margin whenever open ground is nearby and only
            // walks the margin where the start or goal sits inside it.
            var cells = AStar(entry, end, LocalWalkable, movementFloor && heroPathing ? 1024 : 512,
                edgeChecker, movementFloor && heroPathing
                    ? cell => IsHeroPathingWalkable(cell) ? 1.0 : HeroMarginCellCost : null);
            if (cells is null) continue;
            var preserveRawVertices = preservePhysicalGaps && movementFloor &&
                avoidBlockedCorners;
            var points = new List<Position2> { start };
            // Cell-centre smoothing is safe for the padded minion planner,
            // but can draw a chord across a real hero-sized gap when the
            // physical mode is enabled. Keep every A* vertex there and let
            // the segment-clear pass below remove only proven-safe corners.
            var routedCells = preservePhysicalGaps ? cells : SmoothPath(cells, LocalWalkable);
            foreach (var cell in routedCells)
            {
                var point = CellToWorld(cell);
                // The start and goal cells are already represented by their
                // exact endpoints. Keep the interior raw A* vertices only in
                // the failure-directed corner pass.
                if (preserveRawVertices &&
                    (WorldToCell(point) == WorldToCell(start) ||
                     WorldToCell(point) == WorldToCell(goal))) continue;
                points.Add(point);
            }
            points.Add(goal);
            var route = new List<Position2> { points[0] };
            var index = 0;
            while (index < points.Count - 1)
            {
                var found = false;
                var chosen = index + 1;
                var firstCandidate = preserveRawVertices ? index + 1 : points.Count - 1;
                for (var candidate = firstCandidate; candidate > index; candidate--)
                {
                    var clear = SegmentClearOfUnits(points[index], points[candidate], obstacles, index == 0);
                    Func<NavCell, bool> checker = index == 0 ? IsMovementWalkable : PathingWalkable;
                    var alternateClear = index != 0 || authoritativeStart is not Position2 exact ||
                        (FirstSegmentClearOfUnits(points[index], exact, points[candidate], obstacles) &&
                         (heroPathing ? HasHeroMovementLineOfSight(exact, points[candidate]) :
                            HasMovementLineOfSight(exact, points[candidate])));
                    // A hero chord is tested on the movement view, as the client's chord walk (batch 223 item 1).
                    if (clear && alternateClear &&
                        (heroPathing
                            ? HasHeroMovementLineOfSight(points[index], points[candidate])
                            : LineOfSight(WorldToCell(points[index]), WorldToCell(points[candidate]), checker)))
                    {
                        chosen = candidate;
                        found = true;
                        break;
                    }
                }
                if (!found) return Array.Empty<Position2>();
                route.Add(points[chosen]);
                index = chosen;
            }
            return route;
        }

        return Array.Empty<Position2>();
    }

    // PATH-02: stock SetToNearestGetToAbleCell. BuildNavigationPath
    // passes the start cell as its second parameter, the mutable click, and zero as
    // the last argument (so max(0, clickDistanceSquared - int(.75 * arg^2))
    // is simply clickDistanceSquared). Both scores must be <= their best;
    // equal ties replace the prior cell. This is not a lexicographic sort.
    private NavCell? NearestReachableGoal(NavCell requested, NavCell start,
        Func<NavCell, bool> reachable)
    {
        var bestStart = long.MaxValue;
        var bestClick = long.MaxValue;
        NavCell? best = null;
        var candidate = new NavCell(requested.Column + 1, requested.Row);
        NavCell[] directions = [new(0, 1), new(-1, 0), new(0, -1), new(1, 0)];
        var direction = 0;
        var legLength = 1;
        var legProgress = 0;
        var grow = true;
        var foundLegs = 0;
        for (var iteration = 0; iteration < 4000; iteration++)
        {
            if (candidate.Column > 1 && candidate.Row > 1 &&
                candidate.Column < CountX - 2 && candidate.Row < CountY - 2 &&
                reachable(candidate))
            {
                var dx = (long)candidate.Column - start.Column;
                var dz = (long)candidate.Row - start.Row;
                var startScore = dx * dx + dz * dz;
                dx = (long)candidate.Column - requested.Column;
                dz = (long)candidate.Row - requested.Row;
                var clickScore = dx * dx + dz * dz;
                if (startScore <= bestStart && clickScore <= bestClick)
                {
                    best = candidate;
                    bestStart = startScore;
                    bestClick = clickScore;
                }
            }
            candidate = new NavCell(candidate.Column + directions[direction].Column,
                candidate.Row + directions[direction].Row);
            if (++legProgress != legLength) continue;
            if (best is not null && ++foundLegs > 3) return best;
            direction = (direction + 1) % directions.Length;
            legProgress = 0;
            if (grow) legLength++;
            grow = !grow;
        }
        return null;
    }

    private List<NavCell> NearestCandidates(NavCell origin, Func<NavCell, bool> checker,
        Func<NavCell, bool> candidateFilter, int maxRadius, int maximum)
    {
        var candidates = new List<NavCell>(maximum);
        for (var radius = 0; radius <= maxRadius && candidates.Count < maximum; radius++)
        {
            for (var dc = -radius; dc <= radius && candidates.Count < maximum; dc++)
            for (var dr = -radius; dr <= radius && candidates.Count < maximum; dr++)
            {
                if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius) continue;
                var candidate = new NavCell(origin.Column + dc, origin.Row + dr);
                if (checker(candidate) && candidateFilter(candidate))
                    candidates.Add(candidate);
            }
        }
        return candidates;
    }

    private List<NavCell> SmoothPath(IReadOnlyList<NavCell> raw, Func<NavCell, bool> checker,
        Func<Position2, Position2, bool>? segmentChecker = null,
        Func<Position2, Position2, bool>? firstSegmentChecker = null,
        Func<Position2, Position2, bool>? terminalSegmentChecker = null,
        Position2? exactStart = null, Position2? exactGoal = null, bool goalSnapped = true,
        Func<NavCell, Position2>? pointMapper = null,
        Func<Position2, Position2, bool>? chordFilter = null)
    {
        if (raw.Count == 0) return [];
        var anchor = raw[0];
        Position2 CellPoint(NavCell cell) => pointMapper?.Invoke(cell) ?? CellToWorld(cell);
        var anchorPoint = exactStart ?? CellPoint(anchor);
        var smoothed = new List<NavCell> { anchor };
        var index = 0;
        while (anchor != raw[^1])
        {
            var best = segmentChecker is null ? index + 1 : -1;
            for (var candidate = raw.Count - 1; candidate > index; candidate--)
            {
                var requestedPoint = candidate == raw.Count - 1 && !goalSnapped &&
                    exactGoal is Position2 requestedGoal
                    ? requestedGoal : CellPoint(raw[candidate]);
                var candidatePoint = requestedPoint;
                var edgeChecker = candidate == raw.Count - 1 && !goalSnapped
                    ? terminalSegmentChecker ?? (index == 0
                        ? firstSegmentChecker ?? segmentChecker : segmentChecker)
                    : index == 0 ? firstSegmentChecker ?? segmentChecker : segmentChecker;
                // The next raw cell is the A* edge itself and is never filtered.
                bool KeepsClearance(Position2 end) => candidate == index + 1 ||
                    chordFilter is null || chordFilter(anchorPoint, end);
                if (edgeChecker is null
                        ? LineOfSight(anchor, raw[candidate], checker)
                        : edgeChecker(anchorPoint, candidatePoint) && KeepsClearance(candidatePoint))
                {
                    best = candidate;
                    break;
                }
                // The requested point may be in a walkable cell while its
                // sub-cell chord from the preceding anchor clips a blocked
                // cell. Keep the legal cell-centre endpoint in that case.
                if (edgeChecker is not null && candidate == raw.Count - 1 &&
                    !goalSnapped && requestedPoint != CellPoint(raw[candidate]) &&
                    edgeChecker(anchorPoint, CellPoint(raw[candidate])) &&
                    KeepsClearance(CellPoint(raw[candidate])))
                {
                    best = candidate;
                    break;
                }
            }
            if (best < 0) return [];
            anchor = raw[best];
            anchorPoint = best == raw.Count - 1 && !goalSnapped &&
                exactGoal is Position2 requested
                ? requested : CellPoint(anchor);
            smoothed.Add(anchor);
            index = best;
        }
        return smoothed;
    }

    private static bool DiagonalCornerBlocked(
        NavCell cell, int dc, int dr, Func<NavCell, bool> checker) =>
        !checker(new NavCell(cell.Column + dc, cell.Row)) &&
        !checker(new NavCell(cell.Column, cell.Row + dr));

    private ReachableComponent ReachableCells(NavCell start, Func<NavCell, bool> checker,
        Func<NavCell, NavCell, bool>? edgeChecker)
    {
        ReachabilitySearches++;
        return new ReachableComponent(this, start, checker, edgeChecker);
    }

    /// <summary>
    /// The cells reachable from a start over the same directed edges as A*. The exact start may lie
    /// outside the planning margin, just as in A*. The breadth-first flood is lazy: <see cref="Contains"/>
    /// expands only until it finds the asked cell, and answers "no" only after the whole component has been
    /// expanded, so every answer equals a complete flood's. Batch 20: a blocked click asked about a few
    /// cells beside the start but flooded the whole map component first (up to 73 ms per query, sixteen
    /// times per hero approach and per minion engage), which stalled the single simulation thread.
    /// </summary>
    private sealed class ReachableComponent
    {
        private readonly NavigationGrid _grid;
        private readonly Func<NavCell, bool> _checker;
        private readonly Func<NavCell, NavCell, bool>? _edgeChecker;
        private readonly HashSet<NavCell> _reached;
        private readonly Queue<NavCell> _pending = new();

        public ReachableComponent(NavigationGrid grid, NavCell start, Func<NavCell, bool> checker,
            Func<NavCell, NavCell, bool>? edgeChecker)
        {
            _grid = grid;
            _checker = checker;
            _edgeChecker = edgeChecker;
            _reached = [start];
            _pending.Enqueue(start);
        }

        public bool Contains(NavCell cell)
        {
            if (_reached.Contains(cell)) return true;
            while (_pending.TryDequeue(out var current))
            {
                _grid.PathSearchExpansions++;
                _grid.PlanningWork += GridSearchWork;
                foreach (var (dc, dr, _) in Neighbors)
                {
                    var next = new NavCell(current.Column + dc, current.Row + dr);
                    if (_reached.Contains(next) || !_checker(next)) continue;
                    if (dc != 0 && dr != 0 && DiagonalCornerBlocked(current, dc, dr, _checker)) continue;
                    if (_edgeChecker is not null && !_edgeChecker(current, next)) continue;
                    _reached.Add(next);
                    _pending.Enqueue(next);
                }
                if (_reached.Contains(cell)) return true;
            }
            return false;
        }
    }

    private List<NavCell>? AStar(NavCell start, NavCell goal, Func<NavCell, bool> checker,
        int? maxNodes = null, Func<NavCell, NavCell, bool>? edgeChecker = null,
        Func<NavCell, double>? cellCost = null)
    {
        AStarSearches++;
        static double Heuristic(NavCell a, NavCell b)
        {
            var dc = a.Column - b.Column;
            var dr = a.Row - b.Row;
            return Math.Sqrt(dc * dc + dr * dr);
        }

        if (start == goal) return [start];
        if (!checker(goal)) return null;
        // Every route must enter its goal over one of these same search
        // edges. Reject an isolated terminal without exhausting the map;
        // the exact start may itself lie outside the planning margin.
        if (start != goal)
        {
            var hasIncomingEdge = false;
            foreach (var (dc, dr, _) in Neighbors)
            {
                var previous = new NavCell(goal.Column - dc, goal.Row - dr);
                if (previous != start && !checker(previous)) continue;
                if (dc != 0 && dr != 0 &&
                    DiagonalCornerBlocked(previous, dc, dr, checker)) continue;
                if (edgeChecker is null || edgeChecker(previous, goal))
                {
                    hasIncomingEdge = true;
                    break;
                }
            }
            if (!hasIncomingEdge) return null;
        }

        var open = new PriorityQueue<NavCell, (double F, int Column, int Row)>();
        var cameFrom = new Dictionary<NavCell, NavCell>();
        var scores = new Dictionary<NavCell, double> { [start] = 0.0 };
        var closed = new HashSet<NavCell>();
        open.Enqueue(start, (Heuristic(start, goal), start.Column, start.Row));
        while (open.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current)) continue;
            PathSearchExpansions++;
            PlanningWork += GridSearchWork;
            if (maxNodes is not null && closed.Count > maxNodes.Value) return null;
            if (current == goal)
            {
                var path = new List<NavCell> { goal };
                while (path[^1] != start) path.Add(cameFrom[path[^1]]);
                path.Reverse();
                return path;
            }

            foreach (var (dc, dr, cost) in Neighbors)
            {
                var next = new NavCell(current.Column + dc, current.Row + dr);
                if (closed.Contains(next) || !checker(next)) continue;
                if (dc != 0 && dr != 0 && DiagonalCornerBlocked(current, dc, dr, checker)) continue;
                if (edgeChecker is not null && !edgeChecker(current, next)) continue;
                var tentative = scores[current] + cost * (cellCost?.Invoke(next) ?? 1.0);
                if (!scores.TryGetValue(next, out var old) || tentative < old)
                {
                    scores[next] = tentative;
                    cameFrom[next] = current;
                    open.Enqueue(next, (tentative + Heuristic(next, goal), next.Column, next.Row));
                }
            }
        }
        return null;
    }
}
