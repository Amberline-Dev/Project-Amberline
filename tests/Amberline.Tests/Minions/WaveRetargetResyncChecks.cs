using Amberline.Simulation.Combat;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Consolidated production-wave regression for the three 120-second target
/// scenarios and the wire-silence rules of the minion correction packets.  One
/// real match run keeps the expensive wave setup shared while each assertion
/// retains the contract it protects.
/// </summary>
public static partial class WaveRetargetResyncChecks
{
    [Check("minions.wave-retarget-resync", Serial = true)]
    public static void ProductionWaveContracts()
    {
        var previousTrace = Environment.GetEnvironmentVariable("LOG_FIDELITY_TRACE");
        Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", "1");
        var trace = new TraceLog();
        try
        {
            var runtime = MatchRuntime.CreateProduction(
                NavigationGrid.LoadDefault(), GameData.Default,
                orderChampion: "Annie", chaosChampion: "Annie", solo: true,
                originalRosterCounts: new SessionRosterCounts(1, 0),
                readySettleSeconds: 0.0, minionFidelity: true, sessionLog: trace);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            TestAssert.True(runtime.Session.Authenticate(1,
                    runtime.Session.BuildKeyCheck(1)) is not null,
                "production wave observer authentication");
            _ = runtime.Session.Spawn(1);
            TestAssert.True(runtime.Session.MarkReady(1), "production wave observer ready");
            TestAssert.True(runtime.Session.Start(), "production wave session start");

            var participant = runtime.Session.Participants.Single();
            var hero = participant.Hero ?? throw new InvalidOperationException(
                "production wave observer has no hero");
            hero.Protected = true;
            hero.Position = new Position2(7601.74, 6449.17);
            hero.Waypoints.Clear();
            hero.Target = null;
            hero.AttackTarget = null;
            runtime.Delivery.AddObserver(new ReplicationObserver(
                1, Team.Order, hero.NetId, new CaptureTransport(),
                ready: true, view: participant.View));
            runtime.Delivery.Flush();
            runtime.Waves!.AutomaticSpawningEnabled = false;
            var explicitIds = new HashSet<uint>();
            const double stagger = MinionDefinitions.MinionStaggerSeconds;
            for (var wave = 2; wave <= 4; wave++)
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                var composition = LevelScriptFixtures.Wave(runtime.World, team, "C", wave);
                for (var slot = 0; slot < composition.Count; slot++)
                {
                    var now = runtime.Combat.CurrentTime;
                    var minion = runtime.Waves.SpawnFixture(team, composition[slot], slot,
                        "C", now, now);
                    explicitIds.Add(minion.NetId);
                }
                runtime.Tick(stagger);
            }
            trace.Clear();

            var waiting = new Dictionary<uint, WaitingWindow>();
            var completedWaits = new List<double>();
            var freshCasterAcquisitions = new List<double>();
            var realStops = new List<uint>();
            var realStopNoOps = new List<uint>();
            var invalidEjections = 0;
            var targetLossEdges = 0;
            var selectableTargetLossEdges = 0;
            var returnedTargetLosses = new HashSet<uint>();
            const double dt = 1.0 / 30.0;

            runtime.Minions!.ReturnDecisionObserver = (minion, reason) =>
            {
                if (reason == "target-lost")
                {
                    targetLossEdges++;
                    returnedTargetLosses.Add(minion.NetId);
                    // owner decision 27 Sept 2026: edge-to-edge gameplay radii
                    if (minion.AttackTarget is uint targetId &&
                        runtime.World.Get(targetId) is { Alive: true } target &&
                        Distance(minion.Position, target.Position) <=
                            CombatService.AttackReach(minion, target) + 1e-6)
                        invalidEjections++;
                }
            };
            runtime.Minions.AcquireDecisionObserver = (minion, target, retarget) =>
            {
                if (!retarget && minion.MinionState == MinionAiState.March &&
                    minion.MinionType == MinionType.Caster &&
                    minion.AttackTarget is null)
                    freshCasterAcquisitions.Add(Distance(minion.Position, target.Position));
            };
            runtime.Minions.ClearMovementObserver = (minion, hadWaypoints, hadTarget, moved) =>
            {
                realStops.Add(minion.NetId);
                if (!hadWaypoints && !hadTarget && moved is double distance && distance <= 1.0)
                    realStopNoOps.Add(minion.NetId);
            };

            // The source workload injects explicit waves 2/3/4 for both
            // teams, then runs a complete 120-second 30 Hz fight.  The
            // before/after edge is retained so waits begin only after a real
            // target loss, rather than counting the initial march.
            for (var tick = 0; tick < 120 * 30; tick++)
            {
                var before = runtime.World.Entities.Values
                    .Where(entity => explicitIds.Contains(entity.NetId) && entity.Alive)
                    .ToDictionary(entity => entity.NetId, entity => new PreviousMinion(
                        entity.MinionState, entity.AttackTarget,
                        entity.Position, entity.AttackRange, entity.Radius));

                runtime.Tick(dt);
                var now = runtime.Combat.CurrentTime;
                foreach (var minion in runtime.World.Entities.Values.Where(entity =>
                             explicitIds.Contains(entity.NetId) && entity.Alive))
                {
                    before.TryGetValue(minion.NetId, out var old);
                    if (returnedTargetLosses.Contains(minion.NetId) &&
                        minion.MinionState == MinionAiState.March &&
                        minion.AttackTarget is null &&
                        runtime.Minions.SelectTarget(minion) is not null)
                    {
                        returnedTargetLosses.Remove(minion.NetId);
                        selectableTargetLossEdges++;
                        waiting[minion.NetId] = new WaitingWindow(now, 0.0);
                    }

                    if (waiting.TryGetValue(minion.NetId, out var window))
                    {
                        if (old is not null)
                            window = window with
                            {
                                Distance = window.Distance + Distance(old.Position, minion.Position)
                            };
                        if (minion.MinionState == MinionAiState.March &&
                            minion.AttackTarget is null &&
                            runtime.Minions.SelectTarget(minion) is not null)
                            waiting[minion.NetId] = window;
                        else
                        {
                            if (window.Distance > 0.0)
                                completedWaits.Add(now - window.StartedAt);
                            waiting.Remove(minion.NetId);
                        }
                    }
                }
            }

            foreach (var window in waiting.Values)
                if (window.Distance > 0.0) completedWaits.Add(runtime.Combat.CurrentTime - window.StartedAt);

            TestAssert.Equal(38, explicitIds.Count,
                "the source wave workload must inject waves 2/3/4 for both teams");
            TestAssert.True(trace.Orders.Count > 0,
                "production wave run emitted no fidelity movement orders");
            TestAssert.True(completedWaits.Count > 0,
                $"production wave run never observed a post-loss target wait window " +
                $"(losses={targetLossEdges}, selectable={selectableTargetLossEdges})");
            TestAssert.True(freshCasterAcquisitions.Count >= 6,
                $"production wave run exercised too few fresh caster acquisitions ({freshCasterAcquisitions.Count})");
            var casterDistances = freshCasterAcquisitions.OrderBy(distance => distance).ToArray();
            var casterMedian = casterDistances[casterDistances.Length / 2];
            TestAssert.True(casterMedian > MinionDefinitions.MinionAggroRadius + 50.0,
                $"fresh casters committed too close to their target: median={casterMedian:R}");
            var maxWait = completedWaits.DefaultIfEmpty(0.0).Max();
            // Acquisition is a time bound. Movement runs at 100 ms and its
            // collision solve can change distance travelled within that time.
            var waitBound = MinionDefinitions.TargetScanPeriodSeconds + dt;
            TestAssert.True(maxWait <= waitBound + 1e-8,
                $"post-loss target wait exceeded the scan interval: {maxWait:R}s > {waitBound:R}s");
            TestAssert.Equal(0, invalidEjections,
                "a live target inside the minion's plant band must not eject immediately");
            TestAssert.Equal(0, realStopNoOps.Count,
                "the real wave must not emit an information-free planted stop");
            TestAssert.True(realStops.Count > 0,
                "the real wave never emitted a ClearMovement stop");
            // Owner decision, 16 Oct 2026: the prediction resync and the collision park/resume are gone.
            // Every minion C1 is a hard snap, so a fight, however crowded, publishes neither.
            TestAssert.False(trace.Orders.Any(order => order.Reason is "prediction-resync" or "collision-resync"),
                "the real wave published a deleted prediction-resync or collision park/resume");
            TestAssert.False(trace.Orders.Any(order => order.Reason == "march-reanchor"),
                "the real wave published the deleted 30 s march re-anchor");
            TestAssert.True(MaxCorrectionRate(trace.Orders) <= 10,
                "the real wave exceeded ten corrections in a half-open one-second window");

            var duplicateSingleRoutes = trace.Orders
                .GroupBy(order => order.NetId)
                .SelectMany(group => group.OrderBy(order => order.MonotonicNanoseconds)
                    .Zip(group.OrderBy(order => order.MonotonicNanoseconds).Skip(1)))
                .Count(pair => pair.First.Waypoints.Count == 1 &&
                    pair.Second.Waypoints.Count == 1 &&
                    Distance(pair.First.Waypoints[0], pair.Second.Waypoints[0]) <= 1.0);
            TestAssert.Equal(0, duplicateSingleRoutes,
                "a retarget must not resend an information-free stop waypoint");

            RunSourceResyncCases();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_FIDELITY_TRACE", previousTrace);
        }
    }

    private static void RunSourceResyncCases()
    {
        RunRetargetStopNoOpCase();
        RunSeparationDebtStaysSilentCase();
    }

    [Check("minions.march-has-no-periodic-reanchor")]
    public static void AMarchingMinionIsNeverReanchoredByTime()
    {
        // K8 was a non-retail 30 s re-anchor of a marching unit (freeze insurance). Real-client telemetry on the
        // batch-32 build (T3, 2000 s, 508 K8 packets) found 0 client-only stops it ended and every K8 packet was a
        // hard snap (median 20 u, up to 67 u); without it (T4) no client stood still for 2 s while the server moved
        // the unit. Retail publishes a path only when an AI decision changes it. Deleted 17 Oct 2026.
        var fixture = NewResyncFixture(0x40018001, MinionAiState.March);
        var start = fixture.Entity.Position;
        var goal = new Position2(start.X + 900.0, start.Z);
        fixture.Entity.Waypoints = [goal];
        fixture.Entity.AnchorPath = [start, goal];
        fixture.Entity.LastAnchorTime = 0.0;
        // Far from its anchor and long past the old 6 s and 30 s cadences, as the old rule needed.
        fixture.Entity.Position = new Position2(start.X + 300.0, start.Z);
        for (var second = 1; second <= 125; second++) fixture.Ai.Tick(1.0);
        TestAssert.Equal(0, fixture.Packets.Orders.Count,
            "a marching unit gets no route from the AI just because its last anchor is old");
        TestAssert.Equal(goal, fixture.Entity.AnchorPath[^1], "the client's route is untouched");
    }

    // Whatever the collision solver did to a march (separation from a collision-free walk, the size the
    // deleted 119-unit prediction resync keyed on), the wire says nothing.
    private static void RunSeparationDebtStaysSilentCase()
    {
        var fixture = NewResyncFixture(0x40018052, MinionAiState.March);
        var start = fixture.Entity.Position;
        var goal = new Position2(start.X + 900.0, start.Z);
        fixture.Entity.Waypoints = [goal];
        fixture.Entity.AnchorPath = [start, goal];
        fixture.Entity.LastAnchorTime = 0.0;
        fixture.Entity.RepathCount = 1;
        foreach (var offset in new[] { 10.0, 60.0, 130.0, 400.0 })
        {
            fixture.Entity.Position = new Position2(start.X + offset, start.Z);
            fixture.Entity.SeparationAccumulation = new Position2(offset, 0.0);
            fixture.Ai.Tick(0.2);
        }
        TestAssert.Equal(0, fixture.Packets.Orders.Count,
            "separation debt of any size must not replace the client's collision path");
        TestAssert.Equal(new Position2(400.0, 0.0), fixture.Entity.SeparationAccumulation,
            "separation history is only retired by a published anchor");
    }

    private static void RunRetargetStopNoOpCase()
    {
        var fixture = NewResyncFixture(0x40018020, MinionAiState.Engage);
        var dead = new Entity(0x40018021, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7100.0, 7000.0))
        {
            MinionType = MinionType.Melee,
            Alive = false,
            Radius = MinionDefinitions.BaseStats(MinionType.Melee).Radius,
        };
        var replacement = new Entity(0x40018022, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(7100.0, 7000.0))
        {
            MinionType = MinionType.Melee,
            Alive = true,
            Created = true,
            Visible = true,
            Radius = MinionDefinitions.BaseStats(MinionType.Melee).Radius,
        };
        fixture.World.Register(dead);
        fixture.World.Register(replacement);
        fixture.Entity.AttackTarget = dead.NetId;
        fixture.Entity.Target = null;
        fixture.Entity.Waypoints.Clear();

        // The retarget edge is planted before the replacement is selected:
        // there is no movement order to cancel and no position change since
        // the prior anchor.  Production Acquire must therefore avoid an
        // information-free one-point movement packet.
        fixture.Ai.Tick(1.0 / 30.0);
        TestAssert.Equal(0, fixture.Packets.Orders.Count,
            "a planted retarget with no prior order must not emit a stop no-op");
    }

    private static ResyncFixture NewResyncFixture(uint netId, MinionAiState state)
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var packets = new ResyncPackets();
        var entity = new Entity(netId, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(7000.0, 7000.0))
        {
            MinionType = MinionType.Melee,
            MinionState = state,
            Alive = true,
            Created = true,
            Visible = true,
            Speed = MinionDefinitions.BaseStats(MinionType.Melee).MoveSpeed,
            Radius = MinionDefinitions.BaseStats(MinionType.Melee).Radius,
            AttackRange = MinionDefinitions.BaseStats(MinionType.Melee).AttackRange,
            AcquisitionRange = MinionDefinitions.BaseStats(MinionType.Melee).AcquisitionRange,
        };
        world.Register(entity);
        var combat = new ResyncCombat();
        return new ResyncFixture(world, movement,
            new MinionAiSystem(world, grid, movement, combat, packets,
                new ResyncVision()), entity, packets, combat);
    }

    private static int MaxCorrectionRate(IEnumerable<ObservedOrder> orders)
    {
        var worst = 0;
        foreach (var group in orders.Where(order => IsCorrection(order.Reason))
                     .GroupBy(order => order.NetId))
        {
            var times = group.Select(order => order.MonotonicNanoseconds)
                .OrderBy(time => time).ToArray();
            for (var index = 0; index < times.Length; index++)
            {
                var count = times.Skip(index).Count(time =>
                    time - times[index] < 1_000_000_000L - 1L);
                worst = Math.Max(worst, count);
            }
        }
        return worst;
    }

    private static bool IsCorrection(string reason) =>
        reason is "prediction-resync" or "collision-resync";

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private sealed record PreviousMinion(
        MinionAiState State, uint? Target, Position2 Position,
        double AttackRange, double Radius);

    private sealed record WaitingWindow(double StartedAt, double Distance);

    private sealed record ResyncFixture(
        WorldState World,
        MovementSystem Movement,
        MinionAiSystem Ai,
        Entity Entity,
        ResyncPackets Packets,
        ResyncCombat Combat);

    private sealed record ResyncOrder(
        double Elapsed,
        uint NetId,
        IReadOnlyList<Position2> Points,
        string Reason = "");

    private sealed class CaptureTransport : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) { }
    }

    private sealed class TraceLog : ISessionLogSink
    {
        public List<ObservedOrder> Orders { get; } = [];

        public void Clear() => Orders.Clear();

        public void Write(string category, string level, string message,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (message != "fidelity_batch" || properties is null ||
                !properties.TryGetValue("orders", out var raw) ||
                raw is not JsonElement orders || orders.ValueKind != JsonValueKind.Array)
                return;
            foreach (var order in orders.EnumerateArray())
            {
                if (order.TryGetProperty("kind", out var kind) && kind.GetString() == "stop-attack") continue;
                if (!order.TryGetProperty("netid", out var netId) ||
                    !order.TryGetProperty("mono_ns", out var monoNs) ||
                    !order.TryGetProperty("waypoints", out var waypoints) ||
                    waypoints.ValueKind != JsonValueKind.Array)
                    continue;
                var reason = order.TryGetProperty("reason", out var rawReason) &&
                             rawReason.ValueKind == JsonValueKind.String
                    ? rawReason.GetString() ?? string.Empty : string.Empty;
                var points = new List<Position2>();
                foreach (var point in waypoints.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
                        continue;
                    points.Add(new Position2(point[0].GetDouble(), point[1].GetDouble()));
                }
                Orders.Add(new ObservedOrder(netId.GetUInt32(), monoNs.GetInt64(), reason, points));
            }
        }
    }

    private sealed record ObservedOrder(uint NetId, long MonotonicNanoseconds,
        string Reason, IReadOnlyList<Position2> Waypoints);

    private sealed class ResyncPackets : IMinionPacketSink
    {
        public double Elapsed { get; set; }
        public List<ResyncOrder> Orders { get; } = [];

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
            long syncId, string reason) => Orders.Add(new(
                Elapsed, minion.NetId, waypoints.ToArray(), reason));
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private sealed class ResyncCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Protected;
        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);
        public bool AutoAttackFinished { get; set; } = true;
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => AutoAttackFinished;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class ResyncVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }
}
