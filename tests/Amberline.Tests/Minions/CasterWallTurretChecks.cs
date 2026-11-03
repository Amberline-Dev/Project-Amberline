using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class CasterWallTurretChecks
{
    private const double FrameDt = 1.0 / 30.0;
    private const double ClosedObservationSeconds = 2.0;
    private const double OpenObservationSeconds = 18.0;

    [Check("minions.caster-wall-turret")]
    public static void IncomingWaveRetriesAndResumesAtBothEnemyTurrets()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
            Run(team, accumulatedPopulation: false);
        Run(Team.Order, accumulatedPopulation: true);
    }

    private static void Run(Team team, bool accumulatedPopulation)
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        world.Clock.SetVirtual(0.0);
        var movement = new MovementSystem(world, grid);
        var packets = new Packets();
        using var ai = new MinionAiSystem(world, grid, movement,
            new PassiveCombat(), packets, new Vision());
        var waves = new WaveScheduler(world, grid, packets, LevelScriptFixtures.Init(world));
        waves.WarmAllRoutes();
        var scenario = accumulatedPopulation ? $"{team} late-population" : team.ToString();
        if (accumulatedPopulation)
            AddAccumulatedPopulation(waves, team);

        var targetTeam = MinionDefinitions.Opposing(team);
        var targetName = team == Team.Order ? "Turret_T2_C_05" : "Turret_T1_C_05";
        var sourceName = team == Team.Order ? "Turret_T1_C_05" : "Turret_T2_C_05";
        var targetPosition = MapDefinitions.TurretPositions[targetName];
        var sourcePosition = MapDefinitions.TurretPositions[sourceName];
        var toward = Normalize(targetPosition - sourcePosition);
        var baseAngle = Math.Atan2(-toward.Z, -toward.X);
        var turret = new Entity(team == Team.Order ? 0x5001A000u : 0x5001A001u,
            EntityKind.Turret, UnitType.Turret, targetTeam, targetPosition)
        {
            Name = targetName,
            Alive = true,
            Created = true,
            Visible = true,
            Protected = false,
            Radius = MapDefinitions.TurretRadius,
            Health = 1_000_000.0,
            MaxHealth = 1_000_000.0,
            AttackDamage = 0.0,
            AttackRange = 1200.0,
        };
        world.Register(turret);

        var caster = MinionDefinitions.BaseStats(MinionType.Caster, team);
        // owner decision 27 Sept 2026: edge-to-edge gameplay radii
        var stopRadius = caster.AttackRange + GameData.Default.Champion(
            MinionDefinitions.CharacterName(MinionType.Caster, team)).GameplayRadius + turret.CombatRadius - 6.0;
        const double wallStep = 0.14;
        var wall = new List<Entity>();
        for (var index = -8; index <= 8; index++)
        {
            var angle = baseAngle + index * wallStep;
            var minion = waves.SpawnFixture(team, MinionType.Caster, index + 8,
                "C", 0.0, 0.0);
            minion.Created = true;
            minion.Visible = true;
            minion.Position = Around(targetPosition, angle, stopRadius);
            minion.Waypoints.Clear();
            minion.Target = null;
            minion.LaneWaypoints.Clear();
            minion.MinionState = MinionAiState.Engage;
            minion.AttackTarget = turret.NetId;
            minion.AttackPoint = new MinionAttackPoint(turret.NetId, angle, stopRadius);
            minion.AnchorPath = Geometry.ClientWaypoints([minion.Position], Bounds(grid)).ToList();
            minion.LastAnchorTime = 0.0;
            wall.Add(minion);
        }

        var incoming = new List<Entity>();
        foreach (var (type, slot, offset) in new[]
        {
            (MinionType.Melee, 0, -0.42),
            (MinionType.Melee, 1, -0.28),
            (MinionType.Melee, 2, 0.28),
            (MinionType.Caster, 3, -0.14),
            (MinionType.Caster, 4, 0.0),
            (MinionType.Caster, 5, 0.14),
            (MinionType.Siege, 6, 0.42),
        })
        {
            var angle = baseAngle + offset;
            var minion = waves.SpawnFixture(team, type, slot, "C", 0.0, 0.0);
            minion.Created = true;
            minion.Visible = true;
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            var roleStop = CombatService.AttackReach(minion, turret) - 6.0;
            minion.Position = Around(targetPosition, angle, stopRadius + 360.0);
            var goal = Around(targetPosition, angle, roleStop);
            minion.Waypoints.Clear();
            minion.Target = null;
            minion.LaneWaypoints = [goal];
            minion.MinionState = MinionAiState.Engage;
            minion.AttackTarget = turret.NetId;
            minion.AttackPoint = new MinionAttackPoint(turret.NetId, angle, roleStop);
            minion.AnchorPath = Geometry.ClientWaypoints([minion.Position, goal], Bounds(grid)).ToList();
            minion.LastAnchorTime = 0.0;
            incoming.Add(minion);
        }
        packets.Orders.Clear();

        var watched = incoming.Where(minion => minion.MinionType == MinionType.Caster)
            .ToArray();
        var initial = incoming.ToDictionary(minion => minion.NetId, minion => minion.Position);
        var accumulator = 0.0;
        var now = 0.0;

        void Step()
        {
            world.Clock.Advance(FrameDt);
            accumulator += FrameDt;
            while (accumulator + 1e-12 >= MovementSystem.FixedTickSeconds)
            {
                var starts = incoming.Where(minion => minion.Alive)
                    .ToDictionary(minion => minion.NetId, minion => minion.Position);
                movement.Tick(MovementSystem.FixedTickSeconds);
                foreach (var minion in incoming.Where(minion => minion.Alive))
                {
                    // Restated in batch 235 to the client's behaviour: the stock client has no rule that keeps a unit
                    // out of a planted body. The collider branch of the client's contact solver steers from the colliders' cluster (one
                    // disc around their mean position), and the three passes chain the governor (each pass may raise the
                    // squared speed to 1.375 x the previous pass's), so a contact frame may step
                    // 52.4 u at 325 u/s and end inside one member of the cluster. The real client does: oracle frame 601
                    // (capture F) steps 52.4 u from 74.67 to 32.87 u of a neighbour, frame 620 enters a stopped body, and
                    // the port reproduces both within 0.0004 u (movement.client-contact-oracle). Here melee 40010012
                    // steps 52.4 u from 84.56 to 38.10 u of wall caster 40010006, one of its three colliders.
                    // What the client bounds is the step: the march budget times sqrt(1.375) per governed pass, at most
                    // four passes (the three of the client's contact solver plus the bend's repeat).
                    var bound = minion.Speed * MovementSystem.FixedTickSeconds * 1.375 * 1.375 + 1e-3;
                    TestAssert.True(Distance(starts[minion.NetId], minion.Position) <= bound,
                        $"{scenario} incoming {minion.NetId:X8} stepped beyond the client's governed bound");
                }
                accumulator -= MovementSystem.FixedTickSeconds;
            }
            packets.Elapsed = now + FrameDt;
            ai.Tick(FrameDt);
            now += FrameDt;
        }

        // Restated in batch 235: the closed-wall wait, its two-query retries and its release (the K3/K4/K5 planner,
        // MinionAiCongestion.AvoidPlantedBodies) were removed from production in batch 225 as a NOT RETAIL stand-in
        // and deleted in batch 271 (minions-planted-approach-preserves-native-stuck-recovery keeps the solver's side). Retail has no server wait behind a planted wall: the server orders each caster to its attack
        // point on the static grid and the client's solver resolves the contact. The scenario keeps the
        // closed wall for the same ten seconds, then opens it.
        var closedUntil = 8.0 + ClosedObservationSeconds;
        while (now < closedUntil - 1e-9)
            Step();
        foreach (var blocker in wall) blocker.Alive = false;
        var openUntil = now + OpenObservationSeconds;
        while (now < openUntil - 1e-9)
            Step();

        TestAssert.True(incoming.All(minion => minion.AttackTarget == turret.NetId &&
                minion.AttackPoint is { TargetNetId: var target } && target == turret.NetId),
            $"{scenario} caster-wall handling changed the attack target or fan intent");
        TestAssert.True(watched.All(minion =>
                Distance(minion.Position, initial[minion.NetId]) > 100.0),
            $"{scenario} released caster component made no material authoritative progress");
        TestAssert.True(now >= 20.0,
            $"{scenario} did not retain at least twenty seconds of collision and AI cadence");
        AssertWireBounds(scenario, movement, packets.Orders);
    }

    private static void AddAccumulatedPopulation(WaveScheduler waves, Team team)
    {
        var composition = new[]
        {
            MinionType.Melee, MinionType.Melee, MinionType.Melee,
            MinionType.Caster, MinionType.Caster, MinionType.Caster,
            MinionType.Siege,
        };
        var ordinal = 0;
        for (var wave = 0; wave < 3; wave++)
        for (var slot = 0; slot < composition.Length; slot++, ordinal++)
        {
            var lane = ordinal % 2 == 0 ? "L" : "R";
            var route = waves.LaneRoute(team, lane);
            var (position, tail) = PositionAlong(route, 400.0 + ordinal * 180.0);
            var minion = waves.SpawnFixture(team, composition[slot], slot, lane,
                0.0, 180.0 + wave * MinionDefinitions.WaveIntervalSeconds);
            minion.Created = true;
            minion.Visible = true;
            minion.Position = position;
            minion.Waypoints = tail;
            minion.Target = null;
            minion.AttackTarget = null;
            minion.MinionState = MinionAiState.March;
            minion.AnchorPath = new[] { position }.Concat(tail).ToList();
            minion.LastAnchorTime = 0.0;
        }
    }

    private static (Position2 Position, List<Position2> Tail) PositionAlong(
        IReadOnlyList<Position2> route, double distance)
    {
        for (var index = 0; index + 1 < route.Count; index++)
        {
            var length = Distance(route[index], route[index + 1]);
            if (length <= 1e-9) continue;
            if (distance <= length)
            {
                var position = new Position2(
                    route[index].X + (route[index + 1].X - route[index].X) * distance / length,
                    route[index].Z + (route[index + 1].Z - route[index].Z) * distance / length);
                return (position, route.Skip(index + 1).ToList());
            }
            distance -= length;
        }
        return (route[^1], []);
    }

    private static void AssertWireBounds(string scenario, MovementSystem movement,
        IReadOnlyList<Order> orders)
    {
        foreach (var group in orders.GroupBy(order => order.NetId))
        {
            var ordered = group.OrderBy(order => order.Elapsed).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (previous.Points.Count != 1 || current.Points.Count <= 1) continue;
                var maximumStep = 325.0 * MovementSystem.FixedTickSeconds *
                    Math.Pow(Math.Sqrt(1.0 + movement.Settings.Collision.SpeedDeadband), 3) + 3.0;
                TestAssert.True(Distance(previous.Points[0], current.Points[0]) <=
                                maximumStep + 1e-9,
                    $"{scenario} {group.Key:X8} resumed from a packet anchor beyond one safe step");
            }
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                TestAssert.False(previous.Points.Count == 1 && current.Points.Count == 1 &&
                                 Distance(previous.Points[0], current.Points[0]) <= 1.0,
                    $"{scenario} {group.Key:X8} repeated an unchanged singleton route");
            }
        }

        TestAssert.False(orders.Any(order => order.Reason is "prediction-resync" or "collision-resync" or "march-reanchor"),
            $"{scenario} published a deleted prediction/park/re-anchor correction");
    }

    private static Position2 Around(Position2 centre, double angle, double radius) =>
        new(centre.X + Math.Cos(angle) * radius,
            centre.Z + Math.Sin(angle) * radius);

    private static Position2 Normalize(Position2 value)
    {
        var length = Distance(value, Position2.Zero);
        return new Position2(value.X / length, value.Z / length);
    }

    private static NavigationBounds Bounds(NavigationGrid grid) =>
        new(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private sealed record Order(double Elapsed, uint NetId, string Reason,
        IReadOnlyList<Position2> Points);

    private sealed class Packets : IMinionPacketSink
    {
        public double Elapsed { get; set; }
        public List<Order> Orders { get; } = [];
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        { position = default; return false; }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints,
            long syncId, string reason) => Orders.Add(new Order(
                Elapsed, minion.NetId, reason, waypoints.ToArray()));
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private sealed class PassiveCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Protected;
        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    private sealed class Vision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }
}
