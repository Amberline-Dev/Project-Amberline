using Amberline.Simulation.Combat;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// Source regressions for planted minion seating, occupied routes, and the
/// marching-wave collision solver.  These checks intentionally use the
/// production AI, movement, navigation, and wave-spawn paths.
/// </summary>
public static partial class CrowdRouteChecks
{
    private const double MovementDt = MovementSystem.FixedTickSeconds;
    private const double SourceSimulationSeconds = 60.0;
    private const double JamWindowSeconds = 5.0;
    private const double EscapeWindowSeconds = 15.0;
    private const int SharedStopWaveIndex = 2;
    // The shipped script's zero-based wave index is the source wave number used by the
    // exported caster fixture (the third wave carries the siege unit).
    private const int JamWaveIndex = 2;

    // Batch 271: minions-congestion-validates-authoritative-first-leg is retired with the planted-body planner it tested (its own
    // first-leg validation; no production caller since batch 225).

    [Check("minions-captured-shared-node-progress-after-clear")]
    public static void CapturedSharedNodeProgressesAfterMovingBodyClears()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var start = new Position2(10613.354373033519, 10652.119190349558);
        var sharedNode = new Position2(10687.0, 10709.0);
        var mover = world.Register(new Entity(0x40008110, EntityKind.Minion,
            UnitType.Minion, Team.Order, start)
        {
            MinionType = MinionType.Melee,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = 325.0,
            Radius = MinionDefinitions.BaseStats(MinionType.Melee).Radius,
            Waypoints = [sharedNode],
        });
        var blocker = world.Register(new Entity(0x40008111, EntityKind.Minion,
            UnitType.Minion, Team.Order, sharedNode)
        {
            MinionType = MinionType.Melee,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = 325.0,
            Radius = MinionDefinitions.BaseStats(MinionType.Melee).Radius,
            Waypoints = [new Position2(10807.618151841838, 10774.67491139908)],
        });
        var obstacle = new NavigationObstacle(blocker.Position,
            movement.BodyClearance(mover, blocker));
        TestAssert.False(grid.SegmentClearOfUnits(start, sharedNode, [obstacle], escape: true),
            "the captured first route ends inside the moving body's physical shell");

        // Keep the source's exact active goal while the body clears it. The
        // planner disposition remains open; this check records the physical
        // acceptance edge and proves the authoritative route can progress
        // once the occupied node is genuinely free.
        blocker.Position = new Position2(10807.618151841838, 10774.67491139908);
        for (var tick = 0; tick < 4; tick++)
            movement.Tick(MovementSystem.FixedTickSeconds);
        TestAssert.True(Distance(mover.Position, start) > 1.0,
            "the captured minion must resume authoritative progress after the blocker clears");
        TestAssert.True(Distance(mover.Position, sharedNode) < 1.0,
            "release must reach the retained shared-node goal");
    }

    [Check("minions-shared-stop-point")]
    public static void SharedStopPointRemainsSeparatedAndLatched()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var packets = new RecordingPackets();
        var target = new Entity(0x40008000, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7300.0, 7000.0))
        {
            Alive = true,
            Created = true,
            Visible = true,
            Radius = 65.0,
            Health = 5000.0,
            MaxHealth = 5000.0,
        };
        world.Register(target);

        // This is the recorded staggered-bearing source case: three caster
        // attackers approach the same target from bearings pi and pi +/- .5.
        var attackers = new List<Entity>();
        foreach (var (netId, bearing) in new[]
        {
            (0x40008001u, Math.PI),
            (0x40008002u, Math.PI + 0.5),
            (0x40008003u, Math.PI - 0.5),
        })
        {
            var entity = new Entity(netId, EntityKind.Minion, UnitType.Minion,
                Team.Order, new Position2(
                    target.Position.X + Math.Cos(bearing) * 900.0,
                    target.Position.Z + Math.Sin(bearing) * 900.0))
            {
                MinionType = MinionType.Caster,
                Alive = true,
                Created = true,
                Visible = true,
                Speed = 325.0,
                Radius = MinionDefinitions.BaseStats(MinionType.Caster).Radius,
                AttackRange = 550.0,
                AcquisitionRange = 1000.0,
                Health = 280.0,
                MaxHealth = 280.0,
                MinionState = MinionAiState.March,
            };
            world.Register(entity);
            attackers.Add(entity);
        }

        var ai = new MinionAiSystem(world, grid, movement, new PassiveCombat(),
            packets, new WorldMinionVisionService(world, grid,
                _ => Array.Empty<VisionProvider>()));

        // Let the production engage path choose each absolute fan bearing. minionLib 0.0's
        // OnInit arms TimerFindEnemies on DELAY_FIND_ENEMIES, and Minion.lua 0.2 is a marching
        // minion's only acquisition pass (its AI_ATTACKMOVESTATE arm, 0009-0036): the scan sits on
        // that 0.25 s grid, not on the single frame the old machine's lazy TargetScanReady allowed.
        // Minion.lua 0.4's arm for this state only re-issues the march.
        AwaitScriptScan(ai, attackers);
        var claims = attackers.Select(entity => entity.AttackPoint).ToArray();
        TestAssert.True(claims.All(point => point is not null),
            "every staggered caster must receive a production attack claim");
        var stopPoints = claims.Select(point =>
        {
            var claim = point!.Value;
            return new Position2(target.Position.X + Math.Cos(claim.Angle) * claim.Radius,
                target.Position.Z + Math.Sin(claim.Angle) * claim.Radius);
        }).ToArray();
        var closest = stopPoints.SelectMany((left, index) =>
                stopPoints.Skip(index + 1).Select(right => Distance(left, right)))
            .Min();
        TestAssert.True(closest > 2.0 * MinionDefinitions.BaseStats(MinionType.Caster).Radius,
            $"staggered caster stop points overlap: closest={closest:R}");

        var initialAngles = attackers.ToDictionary(entity => entity.NetId,
            entity => entity.AttackPoint!.Value.Angle);
        for (var frame = 0; frame < 40; frame++)
        {
            ai.Tick(0.03);
            foreach (var entity in attackers)
                TestAssert.Near(initialAngles[entity.NetId], entity.AttackPoint!.Value.Angle,
                    1e-12, "a settled attack claim changed while its target was unchanged");
        }

        // The source also guards the front/back formation.  A caster's firing
        // ring must remain materially farther from the target than a melee's.
        var melee = AddAttacker(world, 0x40008010, MinionType.Melee,
            new Position2(6400.0, 7000.0));
        var caster = AddAttacker(world, 0x40008011, MinionType.Caster,
            new Position2(6400.0, 7000.0));
        // The two rows are born mid-fight, so their own TimerFindEnemies grid starts here and
        // their claim lands on its first DELAY_FIND_ENEMIES boundary just like the staggered trio.
        AwaitScriptScan(ai, melee, caster);
        TestAssert.True(melee.AttackPoint is not null && caster.AttackPoint is not null,
            "formation guard requires both melee and caster claims");
        TestAssert.True(caster.AttackPoint!.Value.Radius >
                        melee.AttackPoint!.Value.Radius + 2.0 * caster.Radius,
            $"melee/caster rows collapsed: melee={melee.AttackPoint!.Value.Radius:R}, " +
            $"caster={caster.AttackPoint!.Value.Radius:R}");

        var fight = RunSharedFight();
        // Sample-size guard for the overlap statistics below, not a retail value. The shipped ai_Target cost spreads
        // the two waves over the enemy minions, so fewer attackers share a target: this fight plants 38 co-attacker
        // pairs on the build with the client contact solver (more than 40 when every minion took the nearest enemy).
        TestAssert.True(fight.Closest.Count > 30,
            $"full two-wave source fight produced only {fight.Closest.Count} planted pairs");
        var gaps = fight.Closest.Values.Select(value => value.Gap).OrderBy(value => value).ToArray();
        var overlapRate = fight.Closest.Values.Count(value => value.Gap < value.RadiusSum - 0.01) /
                          (double)fight.Closest.Count;
        TestAssert.True(gaps[0] > 25.0,
            $"full fight's deepest planted overlap was only {gaps[0]:R}u");
        TestAssert.True(overlapRate <= 0.05,
            $"full fight planted overlap rate {overlapRate:P1} exceeded source bound");
        TestAssert.True(gaps[gaps.Length / 2] > 100.0,
            $"full fight median planted gap collapsed to {gaps[gaps.Length / 2]:R}u");
        TestAssert.False(fight.Churn,
            "a full fight changed a latched fan claim while its target stayed unchanged: " +
            string.Join("; ", fight.ClaimChanges.Select(change =>
                $"{change.NetId:X8}/{change.Target:X8} {change.OldAngle:R}->{change.NewAngle:R} " +
                $"justified={change.Justified}, physicalOverlap={change.PhysicalOverlap}, " +
                $"successful={change.Successful}")));
        TestAssert.True(fight.Claims.Count > 0,
            "the production fight emitted no fan claims to measure");
        foreach (var claim in fight.Claims)
            TestAssert.True(claim.Arc <= Math.Max(claim.Contenders, 1) * claim.Seat * 1.25 + 1e-6,
                $"fan claim {claim.NetId:X8} gave up {claim.Arc:R}u, " +
                $"more than {Math.Max(claim.Contenders, 1) * 1.25:R} seats " +
                $"({claim.Seat:R}u) with {claim.Contenders} contenders " +
                $"angle={claim.Angle:R} home={claim.Home:R} origin={claim.Origin}");
        var meleeMedian = Median(fight.Plants.Where(item => item.Type == MinionType.Melee)
            .Select(item => item.Distance));
        var casterMedian = Median(fight.Plants.Where(item => item.Type == MinionType.Caster)
            .Select(item => item.Distance));
        TestAssert.True(casterMedian > meleeMedian + 2.0 * MinionDefinitions.BaseStats(MinionType.Caster).Radius,
            $"full fight formation rows collapsed: melee={meleeMedian:R}, caster={casterMedian:R}");
    }

    [Check("minions-caster-jam")]
    public static void CasterWaveHasNoBackwardCollisionCandidates()
    {
        var result = RunMarchingWave();
        var siegeNetId = result.Tracks.Single(pair =>
            pair.Value[0].Role == MinionType.Siege).Key;
        var steered = result.Steering[siegeNetId];
        var measured = 0;
        var backward = 0;
        foreach (var sample in steered)
        {
            var marchLength = Length(sample.March);
            var appliedLength = Length(sample.Applied);
            if (marchLength <= 1e-6 || appliedLength <= 1e-6) continue;
            measured++;
            if (Dot(sample.March, sample.Applied) < 0.0) backward++;
        }
        TestAssert.True(measured > (int)(SourceSimulationSeconds / MovementDt * 0.4),
            $"the siege wave produced too few measured movement ticks: {measured}");
        TestAssert.Equal(0, backward,
            $"collision solver produced {backward} backward march candidates");

        // Preserve the source's sliding detector for any transient squeeze:
        // a window that travels 800u in five seconds must not finish within a
        // 150u box, and every march-only window must escape over 15 seconds.
        foreach (var (netId, track) in result.Tracks)
        {
            var windows = FindWindows(track, JamWindowSeconds, 800.0, 150.0);
            foreach (var window in windows)
            {
                var inside = result.Steering[netId]
                    .Skip(window.Start)
                    .Take(window.End - window.Start + 1)
                    .Where(sample => Length(sample.March) > 1e-6 &&
                                     Length(sample.Applied) > 1e-6)
                    .ToArray();
                TestAssert.True(inside.All(sample => Dot(sample.March, sample.Applied) >= 0.0),
                    $"{netId:X8} jam window contains backward steering");
                var escape = WorstEfficiencyWindow(track, EscapeWindowSeconds, marchOnly: true);
                TestAssert.True(escape is not null && escape.Value.Net / escape.Value.Path > 0.5,
                    $"{netId:X8} did not escape its five-second jam window");
            }
        }
        var siegeEscape = WorstEfficiencyWindow(result.Tracks[siegeNetId], EscapeWindowSeconds,
            marchOnly: true);
        TestAssert.True(siegeEscape is not null &&
                        siegeEscape.Value.Net / siegeEscape.Value.Path > 0.5,
            "the siege marcher must have a measured 15-second escape window");

        // Source controls rerun the identical production wave with each
        // suspected mechanism disabled.  A solver-disabled wave must not
        // satisfy the jam detector, while bypassing the movement-cell gate
        // must add no drop or backward candidate and must not create a new
        // jam.  These use the internal MovementSystem seam above; the wave,
        // AI, clock, and tick path remain production-owned.
        var noCollision = RunMarchingWave(collisionSolverEnabled: false);
        var noCollisionJams = noCollision.Tracks.Values
            .SelectMany(track => FindWindows(track, JamWindowSeconds, 800.0, 150.0))
            .ToArray();
        TestAssert.Equal(0, noCollisionJams.Length,
            "disabling the collision solver must remove the caster-jam detector signature");

        var noWalkability = RunMarchingWave(walkabilityGateEnabled: false);
        var normalJamCount = result.Tracks.Values.Sum(track =>
            FindWindows(track, JamWindowSeconds, 800.0, 150.0).Count);
        var noWalkJamCount = noWalkability.Tracks.Values.Sum(track =>
            FindWindows(track, JamWindowSeconds, 800.0, 150.0).Count);
        TestAssert.True(noWalkability.DroppedFrames == 0,
            $"walkability bypass dropped {noWalkability.DroppedFrames} frames");
        TestAssert.True(noWalkJamCount <= normalJamCount,
            $"walkability bypass added jam windows: normal={normalJamCount}, " +
            $"bypassed={noWalkJamCount}");
        TestAssert.True(noWalkability.Steering.Values.SelectMany(samples => samples)
            .All(sample => Length(sample.March) <= 1e-6 || Length(sample.Applied) <= 1e-6 ||
                           Dot(sample.March, sample.Applied) >= 0.0),
            "walkability bypass emitted a backward march candidate");
    }

    private static (WorldState World, NavigationGrid Grid, MovementSystem Movement,
        MinionAiSystem Ai, Entity Mover, List<Entity> Blockers, RecordingPackets Packets)
        BuildRoute((double X, double Z) start, (double X, double Z) goal,
            int blockerCount, MinionAiState state, MovementSettings? settings = null,
            double spacing = 75.0, MinionType moverType = MinionType.Siege)
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid, settings);
        var packets = new RecordingPackets();
        var mover = new Entity(0x4000A000, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(start.X, start.Z))
        {
            MinionType = moverType,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = 325.0,
            Radius = MinionDefinitions.BaseStats(moverType).Radius,
            AttackRange = 300.0,
            Waypoints = [new Position2(goal.X, goal.Z)],
            MinionState = state,
            Health = 700.0,
            MaxHealth = 700.0,
            AnchorPath = [new Position2(start.X, start.Z), new Position2(goal.X, goal.Z)],
        };
        world.Register(mover);
        var blockers = new List<Entity>();
        for (var index = 0; index < blockerCount; index++)
        {
            var blocker = new Entity((uint)(0x4000A001 + index), EntityKind.Minion,
                UnitType.Minion, Team.Order,
                new Position2(7101.0, 7001.0 + spacing * (index - (blockerCount - 1) / 2.0)))
            {
                MinionType = MinionType.Caster,
                Alive = true,
                Created = true,
                Visible = true,
                Speed = 325.0,
                Radius = MinionDefinitions.BaseStats(MinionType.Caster).Radius,
                Health = 280.0,
                MaxHealth = 280.0,
                MinionState = MinionAiState.Engage,
            };
            world.Register(blocker);
            blockers.Add(blocker);
        }
        var ai = new MinionAiSystem(world, grid, movement, new PassiveCombat(), packets,
            new WorldMinionVisionService(world, grid, _ => Array.Empty<VisionProvider>()));
        return (world, grid, movement, ai, mover, blockers, packets);
    }

    private static SharedFightResult RunSharedFight()
    {
        var grid = NavigationGrid.LoadDefault();
        var runtime = ProductionRuntime(grid);
        var world = runtime.World;
        var scheduler = runtime.Waves ?? throw new InvalidOperationException(
            "production runtime has no wave scheduler");
        var composition = LevelScriptFixtures.Wave(world, Team.Order, "C", SharedStopWaveIndex);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        for (var slot = 0; slot < composition.Count; slot++)
        {
            var now = runtime.Combat.CurrentTime;
            scheduler.SpawnFixture(team, composition[slot], slot, "C", now, now);
            runtime.Tick(MinionDefinitions.MinionStaggerSeconds);
        }
        var closest = new Dictionary<(uint Left, uint Right, uint Target),
            (double Gap, double RadiusSum)>();
        var claims = new Dictionary<uint, (uint Target, double Angle)>();
        var churn = false;
        var claimChanges = new List<ClaimChange>();
        var claimObservations = new List<ClaimObservation>();
        var plants = new List<(MinionType Type, double Distance)>();
        var minions = runtime.Minions ?? throw new InvalidOperationException(
            "production crowd runtime has no minion AI");
        minions.FanClaimObserver = observation =>
        {
            if (claims.TryGetValue(observation.MinionNetId, out var old) &&
                old.Target == observation.TargetNetId &&
                Math.Abs(old.Angle - observation.Angle) > 1e-12)
            {
                var justified = observation.Reclaim &&
                    observation.PhysicalOverlapAtEntry && observation.Successful;
                claimChanges.Add(new ClaimChange(observation.MinionNetId,
                    observation.TargetNetId, old.Angle, observation.Angle,
                    justified, observation.PhysicalOverlapAtEntry,
                    observation.Successful));
                if (!justified) churn = true;
            }
            if (observation.Fresh)
            {
                claimObservations.Add(new ClaimObservation(
                    observation.MinionNetId, observation.Radius *
                    Math.Abs(WrapAngle(observation.Angle - observation.HomeAngle)),
                    observation.Seat, observation.RivalCount, observation.Angle,
                    observation.HomeAngle, observation.Origin));
            }
            claims[observation.MinionNetId] =
                (observation.TargetNetId, observation.Angle);
        };
        for (var frame = 0; frame < (int)(SourceSimulationSeconds / (1.0 / 30.0)); frame++)
        {
            runtime.Tick(1.0 / 30.0);
            var planted = world.Entities.Values.Where(entity => entity.UnitType == UnitType.Minion &&
                entity.Alive && entity.AttackTarget is not null && entity.Waypoints.Count == 0 &&
                entity.Target is null).ToArray();
            foreach (var entity in planted)
            {
                var target = world.Get(entity.AttackTarget!.Value);
                if (target is null) continue;
                plants.Add((entity.MinionType!.Value, Distance(entity.Position, target.Position)));
            }
            foreach (var left in planted)
            foreach (var right in planted)
            {
                if (left.NetId >= right.NetId || left.Team != right.Team ||
                    left.AttackTarget != right.AttackTarget) continue;
                var gap = Distance(left.Position, right.Position);
                var key = (left.NetId, right.NetId, left.AttackTarget!.Value);
                // This classifier measures physical planting overlap, so use
                // the same pair-aware movement body shell as MovementSystem;
                // Entity.Radius remains gameplay hitbox/attack metadata.
                var radiusSum = runtime.Movement.PairMovementRadius(left, right) +
                                runtime.Movement.PairMovementRadius(right, left);
                if (!closest.TryGetValue(key, out var previous) || gap < previous.Gap)
                    closest[key] = (gap, radiusSum);
            }
        }
        return new SharedFightResult(closest, churn, plants, claimObservations, claimChanges);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        TestAssert.True(ordered.Length > 0, "source formation produced no planted role samples");
        return ordered[ordered.Length / 2];
    }

    // The mover's order to its route's goal, planned by the engine planner as every minion order is (EnginePlan -> CommitMoveOrder).
    private static void PlanOrder(MovementSystem movement, Entity mover) =>
        TestAssert.True(movement.SetMoveOrder(mover.NetId, mover.Waypoints[^1]).Count > 1, "the engine plans the occupied route");

    private static void RunFullRouteTick(MovementSystem movement, MinionAiSystem ai,
        ref double elapsed)
    {
        ai.Tick(MovementDt);
        movement.Tick(MovementDt);
        elapsed += MovementDt;
    }

    /// <summary>
    /// Steps until every claimant holds a production fan claim. The claim is the engine's side of
    /// Minion.lua 0.3's <c>SetStateAndCloseToTarget(AI_ATTACKMOVE_ATTACKING, target)</c>
    /// (0020-0026), which only runs from Minion.lua 0.2's scan; waiting for that scan instead of
    /// assuming the old machine's same-frame acquisition is the point.
    /// </summary>
    private static void AwaitScriptScan(MinionAiSystem ai, IReadOnlyList<Entity> claimants) =>
        AwaitScriptScan(ai, claimants.ToArray());

    private static void AwaitScriptScan(MinionAiSystem ai, params Entity[] claimants)
    {
        for (var tick = 0; tick < 30 && claimants.Any(entity => entity.AttackPoint is null); tick++)
            ai.Tick(0.03);
    }

    private static Entity AddAttacker(WorldState world, uint netId, MinionType type,
        Position2 position)
    {
        var stats = MinionDefinitions.BaseStats(type);
        var entity = new Entity(netId, EntityKind.Minion, UnitType.Minion,
            Team.Order, position)
        {
            MinionType = type,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = stats.MoveSpeed,
            Radius = stats.Radius,
            AttackRange = stats.AttackRange,
            AcquisitionRange = 1000.0,
            Health = stats.MaxHealth,
            MaxHealth = stats.MaxHealth,
            MinionState = MinionAiState.March,
        };
        world.Register(entity);
        return entity;
    }

    private static (Dictionary<uint, List<TrackSample>> Tracks,
        Dictionary<uint, List<SteeringSample>> Steering, int DroppedFrames) RunMarchingWave(
        bool collisionSolverEnabled = true, bool walkabilityGateEnabled = true)
    {
        var tracks = new Dictionary<uint, List<TrackSample>>();
        var steering = new Dictionary<uint, List<SteeringSample>>();
        var grid = NavigationGrid.LoadDefault();
        var sink = new MovementRecorder(steering, tracks);
        var runtime = ProductionRuntime(grid, sink);
        sink.Attach(runtime.World, () => runtime.Combat.CurrentTime);
        runtime.Movement.CollisionSolverEnabled = collisionSolverEnabled;
        runtime.Movement.MovementCellWalkabilityEnabled = walkabilityGateEnabled;
        var scheduler = runtime.Waves ?? throw new InvalidOperationException(
            "production runtime has no wave scheduler");
        var composition = LevelScriptFixtures.Wave(runtime.World, Team.Order, "C", JamWaveIndex);
        for (var slot = 0; slot < composition.Count; slot++)
        {
            var now = runtime.Combat.CurrentTime;
            scheduler.SpawnFixture(Team.Order, composition[slot], slot, "C", now, now);
            runtime.Tick(MinionDefinitions.MinionStaggerSeconds);
        }
        for (var frame = 0; frame < (int)(SourceSimulationSeconds / (1.0 / 30.0)); frame++)
            runtime.Tick(1.0 / 30.0);
        return (tracks, steering, sink.DroppedFrames);
    }

    private static MatchRuntime ProductionRuntime(NavigationGrid grid,
        IMovementDiagnosticSink? movementDiagnostics = null)
    {
        var runtime = MatchRuntime.CreateProduction(grid, GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0, minionFidelity: true,
            movementDiagnostics: movementDiagnostics);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null,
            "production crowd runtime authentication");
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1),
            "production crowd runtime ready");
        TestAssert.True(runtime.Session.Start(),
            "production crowd runtime start");
        var participant = runtime.Session.Participants.Single();
        var hero = participant.Hero ?? throw new InvalidOperationException(
            "production crowd runtime has no hero");
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

    private static List<(int Start, int End)> FindWindows(IReadOnlyList<TrackSample> track,
        double duration, double minimumPath, double maximumNet)
    {
        var windows = new List<(int Start, int End)>();
        for (var start = 0; start < track.Count; start++)
        {
            var path = 0.0;
            var end = start;
            while (end + 1 < track.Count && track[end + 1].Time - track[start].Time <= duration)
            {
                path += Distance(track[end].Position, track[end + 1].Position);
                end++;
            }
            if (track[end].Time - track[start].Time < duration * 0.95) continue;
            var net = Distance(track[start].Position, track[end].Position);
            if (path >= minimumPath && net <= maximumNet &&
                track.Skip(start).Take(end - start + 1).All(sample =>
                    sample.State == MinionAiState.March && sample.AttackTarget is null))
                windows.Add((start, end));
        }
        return windows;
    }

    private static (double Path, double Net)? WorstEfficiencyWindow(IReadOnlyList<TrackSample> track,
        double duration, bool marchOnly = false)
    {
        (double Path, double Net)? worst = null;
        for (var start = 0; start < track.Count; start++)
        {
            if (marchOnly && (track[start].State != MinionAiState.March ||
                              track[start].AttackTarget is not null)) continue;
            var path = 0.0;
            var end = start;
            while (end + 1 < track.Count && track[end + 1].Time - track[start].Time <= duration)
            {
                if (marchOnly && (track[end + 1].State != MinionAiState.March ||
                                  track[end + 1].AttackTarget is not null))
                    break;
                path += Distance(track[end].Position, track[end + 1].Position);
                end++;
            }
            if (track[end].Time - track[start].Time < duration * 0.95 || path <= 1e-9) continue;
            var net = Distance(track[start].Position, track[end].Position);
            if (worst is null || net / path < worst.Value.Net / worst.Value.Path)
                worst = (path, net);
        }
        return worst;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private static double Length(Position2 value) => Distance(value, Position2.Zero);
    private static double Dot(Position2 first, Position2 second) =>
        first.X * second.X + first.Z * second.Z;

    private static double WrapAngle(double value)
    {
        while (value > Math.PI) value -= 2.0 * Math.PI;
        while (value < -Math.PI) value += 2.0 * Math.PI;
        return value;
    }

    private sealed record TrackSample(double Time, Position2 Position,
        MinionAiState State, uint? AttackTarget, MinionType Role);

    private sealed record SharedFightResult(
        IReadOnlyDictionary<(uint Left, uint Right, uint Target), (double Gap, double RadiusSum)> Closest,
        bool Churn,
        IReadOnlyList<(MinionType Type, double Distance)> Plants,
        IReadOnlyList<ClaimObservation> Claims,
        IReadOnlyList<ClaimChange> ClaimChanges);

    private sealed record ClaimObservation(uint NetId, double Arc, double Seat,
        int Contenders, double Angle, double Home, Position2 Origin);

    private sealed record ClaimChange(uint NetId, uint Target, double OldAngle,
        double NewAngle, bool Justified, bool PhysicalOverlap, bool Successful);

    private sealed record SteeringSample(Position2 March, Position2 Applied);

    private sealed class MovementRecorder(
        Dictionary<uint, List<SteeringSample>> steering,
        Dictionary<uint, List<TrackSample>> tracks) : IMovementDiagnosticSink
    {
        private WorldState? _world;
        private Func<double>? _now;
        public int DroppedFrames { get; private set; }

        public void Attach(WorldState world, Func<double> now)
        {
            _world = world;
            _now = now;
        }

        public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot.NetId == 0) continue;
                var start = snapshot.StartPosition ?? Position2.Zero;
                var march = snapshot.MarchPosition ?? start;
                var candidate = snapshot.CollisionCandidatePosition ?? march;
                var committed = snapshot.CommittedPosition ?? candidate;
                // The source control counts a solver-level whole-frame
                // revert, not a later route-progress rejection.  Movement
                // marks both blocked-cell and halted collision branches in
                // this snapshot, before ProgressCommit can reject a path.
                if (snapshot.Halted)
                    DroppedFrames++;
                steering.TryAdd(snapshot.NetId, new List<SteeringSample>());
                steering[snapshot.NetId].Add(new SteeringSample(march - start,
                    candidate - start));
                if (_world?.Get(snapshot.NetId) is { } entity &&
                    entity.UnitType == UnitType.Minion && entity.Alive &&
                    entity.MinionType is MinionType role)
                {
                    tracks.TryAdd(entity.NetId, new List<TrackSample>());
                    tracks[entity.NetId].Add(new TrackSample(_now?.Invoke() ?? 0.0,
                        committed, entity.MinionState, entity.AttackTarget, role));
                }
            }
        }
    }

    private sealed class PassiveCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive &&
            !target.Protected && !target.Buffs.Any(buff =>
                buff.Payload.Untargetable || buff.Payload.Invulnerable);

        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);

        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class RecordingPackets : IMinionPacketSink
    {
        public List<IReadOnlyList<Position2>> WaypointOrders { get; } = new();
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
            long syncId, string reason) => WaypointOrders.Add(waypoints.ToArray());
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private sealed class NoRetainTransport : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) { }
    }
}
