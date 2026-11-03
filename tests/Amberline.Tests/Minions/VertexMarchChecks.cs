using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Production replay of the six source minion-vertex checks.
/// </summary>
public static class VertexMarchChecks
{
    private const double Tick = 1.0 / 30.0;
    private const double Seconds = 25.0;
    private const double EfficiencyWindow = 2.5;
    private const double JamRatio = 0.25;
    private const double JamSeconds = 5.0;
    private const double CapturedMeleeRadius = 35.7437;

    [Check("minions.shipped-mid-markers-routes-and-connectors")]
    public static void ShippedMidMarkersRoutesAndConnectors()
    {
        // Map1 Scene SCB bounds, __NAV_C02..C016 in numeric index order.
        Position2[] shipped =
        [
            new(3276.2103271484375, 3748.5364990234375),
            new(4011.3228759765625, 4309.40771484375),
            new(4554.093994140625, 4869.380859375),
            new(4969.089111328125, 5368.101806640625),
            new(5309.4951171875, 5784.0859375),
            new(5795.802001953125, 6234.40234375),
            new(6307.081787109375, 6702.303955078125),
            new(6934.052490234375, 7260.125),
            new(7672.990478515625, 7801.9921875),
            new(8270.953125, 8392.8837890625),
            new(8743.32958984375, 8884.2998046875),
            new(9218.92431640625, 9322.41650390625),
            new(9727.3740234375, 9872.8837890625),
            new(10234.6884765625, 10420.0),
            new(10917.044921875, 10620.0625),
        ];
        var spine = MapDefinitions.LanePaths["C"];
        TestAssert.Equal(19, spine.Count);
        // Batch 599: the spine is the markers' exact CentralPoints, within 5e-4 of these vertex-bound centres, and the reviewed spawn
        // nudges and detours are offsets from exact scene points, within 0.005 of the old two-decimal values.
        for (var index = 0; index < shipped.Length; index++)
        {
            TestAssert.Near(shipped[index].X, spine[index + 2].X, 1e-3, $"__NAV_C0{index + 2} X");
            TestAssert.Near(shipped[index].Z, spine[index + 2].Z, 1e-3, $"__NAV_C0{index + 2} Z");
        }
        foreach (var (expected, actual) in new[]
        {
            (new Position2(1477.1, 1749.7), spine[0]), (new Position2(2569.6, 3268.9), spine[1]),
            (new Position2(11393.88, 11189.44), spine[^2]), (new Position2(12486.38, 12708.64), spine[^1]),
        })
        {
            TestAssert.Near(expected.X, actual.X, 0.01, "lane C end X");
            TestAssert.Near(expected.Z, actual.Z, 0.01, "lane C end Z");
        }
        TestAssert.Equal(new LaneSpawnApproach(2, 2), MapDefinitions.LaneSpawnApproaches["C"]);

        var grid = NavigationGrid.LoadDefault();
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var routeWorld = new WorldState();
        var scheduler = new WaveScheduler(routeWorld, grid, new NullMinionPackets(),
            LevelScriptFixtures.Init(routeWorld));
        var order = scheduler.LaneRoute(Team.Order, "C").ToArray();
        var chaos = scheduler.LaneRoute(Team.Chaos, "C").ToArray();
        var markers = Geometry.ClientWaypoints(shipped, bounds).ToArray();
        foreach (var route in new[] { order, chaos })
        {
            TestAssert.Equal(route, Geometry.ClientWaypoints(route, bounds).ToArray(),
                "both production routes are client-quantized");
            var indices = (route == order ? markers : markers.Reverse())
                .Select(marker => Array.IndexOf(route, marker)).ToArray();
            TestAssert.True(indices[0] > 0 && indices[^1] == route.Length - 1 &&
                            indices.Zip(indices.Skip(1), (a, b) => a < b).All(increasing => increasing),
                "all fifteen shipped markers occur in team order, ending before the enemy approach");
        }
        var orderJoin = Array.IndexOf(order, markers[0]);
        var chaosJoin = Array.IndexOf(chaos, markers[^1]);
        TestAssert.Equal(order.Skip(orderJoin).ToArray(), chaos.Skip(chaosJoin).Reverse().ToArray(),
            "both teams traverse exactly the same stitched interior in reverse");
        TestAssert.Equal(Geometry.ClientWaypoints([spine[0]], bounds)[0], order[0]);
        TestAssert.Equal(Geometry.ClientWaypoints([spine[^1]], bounds)[0], chaos[0]);

        // The report's straight-segment midpoint is rounded to 0.01 world units.
        static double Length(IReadOnlyList<Position2> path) =>
            path.Zip(path.Skip(1), Distance).Sum();
        var rawMidpoint = Geometry.PositionAlong(spine, Length(spine) / 2.0)!.Value;
        TestAssert.True(Distance(rawMidpoint, new Position2(6972.20, 7288.10)) < 0.01,
            "pre-A* spawn-to-spawn arc midpoint follows shipped geometry, not map-centre symmetry");
        var stitched = order.Concat(chaos.Take(chaosJoin).Reverse()).ToArray();
        var midpoint = Geometry.PositionAlong(stitched, Length(stitched) / 2.0)!.Value;
        // Split at C09: each side's A* detour shifts the half-distance. Allow
        // half the sum of absolute length changes, plus 3 units for quantization.
        var centreIndex = Array.IndexOf(stitched, markers[7]);
        var midpointBudget = 3.0 + (Math.Abs(Length(stitched.Take(centreIndex + 1).ToArray()) -
                Length(spine.Take(10).ToArray())) +
            Math.Abs(Length(stitched.Skip(centreIndex).ToArray()) - Length(spine.Skip(9).ToArray()))) / 2.0;
        TestAssert.True(Distance(midpoint, rawMidpoint) <= midpointBudget,
            $"stitched midpoint {midpoint} exceeds its detour/quantization budget {midpointBudget:R}");

        foreach (var (team, connector) in new[]
                 {
                     (Team.Order, order.Take(orderJoin + 1).ToArray()),
                     (Team.Chaos, chaos.Take(chaosJoin + 1).ToArray()),
                 })
        {
            var world = new WorldState();
            var movement = new MovementSystem(world, grid);
            var cannon = world.Register(new Entity(0x40008180, EntityKind.Minion,
                UnitType.Minion, team, connector[0])
            {
                MinionType = MinionType.Siege, Radius = 55.7437, Speed = 325.0,
                Alive = true, Created = true, Visible = true,
                Waypoints = connector.Skip(1).ToList(),
            });
            TestAssert.Near(55.7437, movement.MovementRadius(cannon), 1e-9,
                "connector traversal uses the full cannon body");
            for (var tick = 0; tick < 300 && cannon.Waypoints.Count > 0; tick++)
            {
                var previous = cannon.Position;
                movement.Tick(MovementSystem.FixedTickSeconds);
                var samples = Math.Max(1, (int)Math.Ceiling(Distance(previous, cannon.Position) / 5.0));
                for (var sample = 0; sample <= samples; sample++)
                {
                    var fraction = (double)sample / samples;
                    var point = new Position2(previous.X + (cannon.Position.X - previous.X) * fraction,
                        previous.Z + (cannon.Position.Z - previous.Z) * fraction);
                    TestAssert.True(grid.IsMovementWalkable(point) &&
                                    grid.PointClearance(point) >= cannon.Radius + 2.5,
                        $"{team} base connector must clear terrain with the full cannon body");
                }
            }
            TestAssert.True(cannon.Waypoints.Count == 0 && Distance(cannon.Position, connector[^1]) < 2.0,
                $"{team} cannon must traverse its base connector within thirty seconds");
        }
    }

    /// <summary>Runs the source file and all of its controls through MatchRuntime.</summary>
    public static void RunProduction()
    {
        var grid = NavigationGrid.LoadDefault();
        var routeWorld = new WorldState();
        var routeBuilder = new WaveScheduler(routeWorld, grid, new NullMinionPackets(),
            LevelScriptFixtures.Init(routeWorld));
        var route = routeBuilder.LaneRoute(Team.Order, "C");
        // retail sweep B4 open question, 27 Sept 2026: shipped __NAV_C markers
        var expectedVertex = new Position2(6934.052490234375, 7260.125); // __NAV_C09
        var vertex = route
            .Select((point, index) => (Point: point, Index: index,
                Distance: Distance(point, expectedVertex)))
            .OrderBy(item => item.Distance)
            .First();
        TestAssert.True(vertex.Distance < 2.0,
            $"the production mid route lost shipped __NAV_C09: {vertex.Distance:R}");
        TestAssert.True(vertex.Index > 0 && vertex.Index + 1 < route.Count,
            "the source vertex must have incoming and outgoing route legs");

        var previous = route[vertex.Index - 1];
        var dx = vertex.Point.X - previous.X;
        var dz = vertex.Point.Z - previous.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var along = new Position2(dx / length, dz / length);
        var across = new Position2(-along.Z, along.X);

        var file = RunScenario(grid, route, vertex.Index, vertex.Point,
            count: 7, back: 250.0, lateral: 60.0, along, across);
        CheckFile(file);

        var pair = RunScenario(grid, route, vertex.Index, vertex.Point,
            count: 2, back: 200.0, lateral: 40.0, along, across);
        CheckPair(pair);
        CheckVertexPrefix(pair);

        var offVertex = new Position2(vertex.Point.X + along.X * 800.0,
            vertex.Point.Z + along.Z * 800.0);
        var off = RunScenario(grid, route, vertex.Index, vertex.Point,
            count: 2, back: 200.0, lateral: 40.0, along, across,
            firstWaypoint: offVertex);
        CheckControl(off, "off-vertex pair");

        var alone = RunScenario(grid, route, vertex.Index, vertex.Point,
            count: 1, back: 200.0, lateral: 40.0, along, across);
        CheckControl(alone, "single marcher");
    }

    private static void CheckFile(VertexRun run)
    {
        foreach (var id in run.Ids)
        {
            var row = Measure(run, id);
            TestAssert.True(row.Frames.Count > 0 && row.Popped,
                $"vertex file marcher {id:X8} never consumed its first leg");
            if (!row.Jammed) continue;

            // A file may meet a later shared stop point after it has cleared
            // the vertex.  That is a different source scenario.  A low
            // efficiency run at the vertex, or a march-only run in its last
            // two seconds, is the old vertex lock and must fail.
            TestAssert.True(row.ToVertex >= 200.0,
                $"vertex file marcher {id:X8} jammed {row.ToVertex:R}u from the vertex");
            TestAssert.True(row.Segment.Any(frame => frame.State != MinionAiState.March),
                $"vertex file marcher {id:X8} remained in MARCH for its jam segment");
        }
    }

    private static void CheckPair(VertexRun run)
    {
        foreach (var id in run.Ids)
        {
            var row = Measure(run, id);
            var nearest = row.Frames
                .OrderBy(frame => Distance(frame.Position, run.Vertex))
                .First();
            TestAssert.True(row.Popped,
                $"vertex pair marcher {id:X8} never consumed its first leg");
            TestAssert.True(row.Closest <= 15.0,
                $"vertex pair marcher {id:X8} stayed {row.Closest:R}u from the vertex; " +
                $"popped={row.Popped} jammed={row.Jammed}; " +
                DescribeFrame(nearest, run.Vertex));
            TestAssert.False(row.Jammed,
                $"vertex pair marcher {id:X8} has a {row.WorstRun:R}s vertex jam; " +
                $"popped={row.Popped} " +
                DescribeFrame(nearest, run.Vertex));
        }
    }

    private static void CheckControl(VertexRun run, string label)
    {
        foreach (var id in run.Ids)
        {
            var row = Measure(run, id);
            TestAssert.True(row.Popped,
                $"{label} {id:X8} did not continue down the route");
            TestAssert.False(row.Jammed,
                $"{label} {id:X8} incorrectly matched the vertex jam detector");
        }
    }

    private static void CheckVertexPrefix(VertexRun run)
    {
        var initialWaypoints = run.InitialWaypointCount;
        var prefix = run.Ids.SelectMany(id => ThroughFirstPop(
                run.Frames[id], initialWaypoints))
            .ToArray();
        TestAssert.True(prefix.Length > 0, "vertex prefix has no movement samples");
        // The stock collider kernel does take its sidestep branch in contact: 278 runs in the 4,859 real
        // client contact frames (night 2 M-RE), 277 of them at most 9 consecutive updates. Only a sustained
        // sidestep, the old vertex lock, fails.
        foreach (var id in run.Ids)
        {
            var longest = 0;
            var current = 0;
            foreach (var frame in ThroughFirstPop(run.Frames[id], initialWaypoints))
            {
                current = frame.Branches.Any(branch => branch.StartsWith("sidestep", StringComparison.Ordinal))
                    ? current + 1 : 0;
                longest = Math.Max(longest, current);
            }
            TestAssert.True(longest <= 9,
                $"the vertex prefix of {id:X8} used the sidestep branch for {longest} consecutive frames");
        }
        TestAssert.False(prefix.Any(frame => frame.Branches.Contains("noprog")),
            "the vertex prefix rejected a frame for lack of progress");
        TestAssert.False(prefix.Any(frame => frame.Halted || frame.Branches.Contains("halt")),
            "the vertex prefix hit a terrain halt");
        TestAssert.True(prefix.Any(frame => frame.ColliderCount > 0),
            "the vertex prefix never observed a real production collider");

        var avoidedFrames = prefix.Count(frame => frame.AvoidCount > 0 ||
            frame.Branches.Any(branch => branch.StartsWith("avoid", StringComparison.Ordinal)));
        // Avoidance is the commonest branch in real contact: 3,510 of the 4,859 real client contact frames
        // (72%) are avoidance-only (night 2 M-RE). Half the prefix still separates it from a vertex lock.
        TestAssert.True(avoidedFrames <= prefix.Length * 0.5,
            $"vertex avoidance occupied {avoidedFrames}/{prefix.Length} frames");

        var popTime = run.Ids.SelectMany(id => run.Frames[id])
            .Where(frame => frame.WaypointCount < initialWaypoints)
            .Select(frame => frame.Time)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min();
        TestAssert.True(popTime < 5.0,
            $"the shared vertex was consumed too late: {popTime:R}s");
    }

    private static IEnumerable<VertexFrame> ThroughFirstPop(
        IReadOnlyList<VertexFrame> frames, int initialWaypoints)
    {
        foreach (var frame in frames)
        {
            yield return frame;
            if (frame.WaypointCount < initialWaypoints) yield break;
        }
    }

    private static VertexRun RunScenario(NavigationGrid grid,
        IReadOnlyList<Position2> route, int vertexIndex, Position2 vertex,
        int count, double back, double lateral, Position2 along, Position2 across,
        Position2? firstWaypoint = null)
    {
        var trace = new VertexTraceSink();
        var runtime = CreateRuntime(grid, trace);
        var routeTail = route.Skip(vertexIndex).ToArray();
        var goal = firstWaypoint is Position2 requested
            ? new[] { requested }.Concat(routeTail.Skip(1)).ToArray()
            : routeTail;
        var ids = new List<uint>(count);
        var entities = new Dictionary<uint, Entity>(count);
        var initialWaypoints = goal.Length;
        for (var index = 0; index < count; index++)
        {
            var side = count == 1 ? 1.0 : index == 0 ? 1.0 : -1.0;
            var distanceBack = count == 7 ? back + index * 60.0 : back;
            var offset = count == 7 ? ((index % 3) - 1) * lateral : lateral * side;
            var position = new Position2(
                vertex.X - along.X * distanceBack + across.X * offset,
                vertex.Z - along.Z * distanceBack + across.Z * offset);
            var entity = runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee,
                index, "C", 0.0, 0.0);
            // This is a replay of the captured melee collision corpus. Keep
            // its original 35.7437u movement disc: production lane-minion
            // pairs now use the unscaled radius (playtest B2 AE5, 26 Sept
            // 2026), so no compensation for the 0.75 NPC-pair shell remains.
            entity.Radius = CapturedMeleeRadius;
            entity.Position = position;
            entity.Waypoints = goal.ToList();
            entity.AnchorPath = new[] { position }.Concat(goal).ToList();
            entity.LastAnchorTime = 0.0;
            entity.AttackTarget = null;
            entity.Target = null;
            entity.MinionState = MinionAiState.March;
            ids.Add(entity.NetId);
            entities.Add(entity.NetId, entity);
        }

        var frames = ids.ToDictionary(id => id, _ => new List<VertexFrame>());
        for (var frame = 0; frame < (int)(Seconds / Tick); frame++)
        {
            runtime.Tick(Tick);
            trace.Record(frames, entities, runtime.Combat.CurrentTime);
        }

        return new VertexRun(ids, frames, vertex, initialWaypoints);
    }

    private static VertexRow Measure(VertexRun run, uint id)
    {
        var frames = run.Frames[id];
        TestAssert.Equal((int)(Seconds / Tick), frames.Count,
            $"vertex run {id:X8} retained exactly 750 post-tick frames");
        TestAssert.True(frames.Count >= 2, $"vertex run {id:X8} has too few movement samples");
        var efficiency = Efficiency(frames);
        var worst = WorstRun(efficiency);
        var segment = worst is { } span
            ? frames.Where(frame => frame.Time >= Math.Max(span.Start, span.End - 2.0) &&
                                    frame.Time <= span.End).ToArray()
            : frames.ToArray();
        var toVertex = segment.Min(frame => Distance(frame.Position, run.Vertex));
        return new VertexRow(frames, segment,
            frames[^1].WaypointCount < run.InitialWaypointCount,
            frames.Min(frame => Distance(frame.Position, run.Vertex)),
            worst?.Duration ?? 0.0,
            worst is { Duration: >= JamSeconds },
            toVertex);
    }

    private static IReadOnlyList<(double Time, double Ratio)> Efficiency(
        IReadOnlyList<VertexFrame> frames)
    {
        var result = new List<(double, double)>();
        var end = 0;
        for (var start = 0; start < frames.Count; start++)
        {
            end = Math.Max(end, start);
            while (end + 1 < frames.Count &&
                   frames[end + 1].Time - frames[start].Time <= EfficiencyWindow)
                end++;
            if (frames[end].Time - frames[start].Time < EfficiencyWindow * 0.9) break;
            var path = 0.0;
            for (var index = start; index < end; index++)
                path += Distance(frames[index].Position, frames[index + 1].Position);
            var net = Distance(frames[start].Position, frames[end].Position);
            result.Add((frames[start].Time, path > 1e-9 ? net / path : 1.0));
        }
        return result;
    }

    private static (double Start, double End, double Duration)? WorstRun(
        IReadOnlyList<(double Time, double Ratio)> efficiency)
    {
        double? start = null;
        (double Start, double End, double Duration) best = default;
        foreach (var (time, ratio) in efficiency)
        {
            if (ratio < JamRatio)
            {
                start ??= time;
                var duration = time - start.Value;
                if (duration > best.Duration)
                    best = (start.Value, time, duration);
            }
            else
            {
                start = null;
            }
        }
        return best.Duration > 0.0 ? best : null;
    }

    private static string DescribeFrame(VertexFrame frame, Position2 vertex)
    {
        var headOffset = frame.WaypointHead is Position2 head
            ? Distance(head, vertex)
            : double.NaN;
        var branches = frame.Branches.Count == 0
            ? "-"
            : string.Join(',', frame.Branches);
        return $"nearest t={frame.Time:R}s pos=({frame.Position.X:R},{frame.Position.Z:R}) " +
            $"state={frame.State} wp={frame.WaypointCount} " +
            $"head={Format(frame.WaypointHead)} tail={Format(frame.WaypointTail)} " +
            $"headVertexGap={headOffset:R} branches=[{branches}] " +
            $"colliders={frame.ColliderCount} avoid={frame.AvoidCount} halted={frame.Halted}";
    }

    private static string Format(Position2? point) => point is Position2 value
        ? $"({value.X:R},{value.Z:R})"
        : "-";

    private static MatchRuntime CreateRuntime(NavigationGrid grid,
        VertexTraceSink? trace)
    {
        var runtime = MatchRuntime.CreateProduction(grid, GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, minionFidelity: true,
            movementDiagnostics: trace);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
                runtime.Session.BuildKeyCheck(1)) is not null,
            "vertex production observer authentication");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1),
            "vertex production observer ready");
        TestAssert.True(runtime.Session.Start(), "vertex production session start");
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException(
            "vertex production observer has no hero");
        hero.Protected = true;
        hero.Position = new Position2(7601.74, 6449.17);
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        runtime.Delivery.AddObserver(new ReplicationObserver(
            1, Team.Order, hero.NetId, new NoRetainTransport(), ready: true,
            view: participant.View));
        runtime.Delivery.Flush();
        return runtime;
    }

    private static double Distance(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }


    private sealed record VertexRun(
        IReadOnlyList<uint> Ids,
        IReadOnlyDictionary<uint, List<VertexFrame>> Frames,
        Position2 Vertex,
        int InitialWaypointCount);

    private sealed record VertexFrame(double Time, Position2 Position,
        int WaypointCount, MinionAiState State, IReadOnlyList<string> Branches,
        int ColliderCount, int AvoidCount, bool Halted,
        Position2? WaypointHead, Position2? WaypointTail);

    private sealed record VertexRow(IReadOnlyList<VertexFrame> Frames,
        IReadOnlyList<VertexFrame> Segment, bool Popped, double Closest,
        double WorstRun, bool Jammed, double ToVertex);

    private sealed class VertexTraceSink : IMovementDiagnosticSink
    {
        private readonly Dictionary<uint, MovementDiagnosticSnapshot> _latest = new();

        public void OnMovementTick(double _,
            IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
        {
            _latest.Clear();
            foreach (var snapshot in snapshots)
                _latest[snapshot.NetId] = snapshot;
        }

        public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
            OnMovementTick(MovementSystem.FixedTickSeconds, snapshots);

        public void Record(Dictionary<uint, List<VertexFrame>> frames,
            IReadOnlyDictionary<uint, Entity> entities, double time)
        {
            foreach (var (id, entity) in entities)
            {
                _latest.TryGetValue(id, out var snapshot);
                var position = entity.Position;
                var branches = snapshot?.Branches ?? Array.Empty<string>();
                Position2? waypointHead = entity.Waypoints.Count > 0
                    ? entity.Waypoints[0] : null;
                Position2? waypointTail = entity.Waypoints.Count > 0
                    ? entity.Waypoints[^1] : null;
                frames[entity.NetId].Add(new VertexFrame(time, position,
                    entity.Waypoints.Count, entity.MinionState,
                    branches.ToArray(), snapshot?.ColliderIds.Count ?? 0,
                    snapshot?.AvoidCount ?? 0, snapshot?.Halted ?? false,
                    waypointHead, waypointTail));
            }
        }
    }

    private sealed class NullMinionPackets : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints,
            long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private sealed class NoRetainTransport : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) { }
    }
}
