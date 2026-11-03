using Amberline.Simulation.Combat;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>Ports the bounded planning workload from the original movement scenarios.</summary>
public static class MinionScenarioChecks
{
    [Check("minions-moving-target-cost")]
    public static void MovingTargetKeepsPlanningBounded()
    {
        foreach (var visible in new[] { true, false })
        {
            var navigation = NavigationGrid.LoadDefault();
            navigation.ResetPathQueryCounters();
            var world = new WorldState();
            var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
                new Position2(7500.0, 7500.0))
            {
                Alive = true,
                Created = true,
                Visible = true,
                Radius = 65.0,
                Health = 100000.0,
                MaxHealth = 100000.0,
            };
            world.Register(hero);
            for (var index = 0; index < 30; index++)
            {
                var angle = index * 2.4;
                var radius = 200.0 + index * 12.0;
                var minion = new Entity((uint)(100 + index), EntityKind.Minion,
                    UnitType.Minion, Team.Chaos,
                    new Position2(7500.0 + Math.Cos(angle) * radius,
                        7500.0 + Math.Sin(angle) * radius))
                {
                    MinionType = MinionType.Melee,
                    Alive = true,
                    Created = visible,
                    Visible = visible,
                    Speed = 325.0,
                    Radius = 35.0,
                    AttackRange = 100.0,
                    AttackTarget = hero.NetId,
                    MinionState = MinionAiState.Engage,
                    Health = 1000.0,
                    MaxHealth = 1000.0,
                };
                world.Register(minion);
            }

            var movement = new MovementSystem(world, navigation);
            var packets = new ScenarioPackets();
            var ai = new MinionAiSystem(world, navigation, movement,
                new ScenarioCombat(), packets,
                new WorldMinionVisionService(world, navigation,
                    _ => Array.Empty<VisionProvider>()));
            var dt = 1.0 / 30.0;
            for (var frame = 0; frame < 90; frame++)
            {
                hero.Position = new Position2(
                    7500.0 + 400.0 * Math.Sin(frame / 30.0),
                    7500.0 + 200.0 * Math.Cos(frame / 30.0));
                var detoursBefore = navigation.FindPathAroundUnitsCalls;
                ai.Tick(dt);
                var detours = navigation.FindPathAroundUnitsCalls - detoursBefore;
                TestAssert.True(detours <= 2,
                    $"moving target {visible} exceeded two dynamic planning queries " +
                    $"at frame {frame}: {detours}");
                packets.Clear();
                if (frame % 3 == 0) movement.Tick(0.1);
            }

            TestAssert.True(navigation.FindPathCalls < 400,
                $"moving target {visible} exceeded the source path-query bound: " +
                navigation.FindPathCalls);
        }

        // Batch 271: the deferred attackers' planning slots (the planted-body planner's two-query budget) are retired with the planner.
    }

    private sealed class ScenarioCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive &&
            !target.Buffs.Any(buff => buff.Payload.Untargetable || buff.Payload.Invulnerable);

        public bool IsInAttackRange(Entity attacker, Entity target) =>
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.InRange(attacker, target);

        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }

        private static double Distance(Position2 first, Position2 second) =>
            Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                      (first.Z - second.Z) * (first.Z - second.Z));
    }

    private sealed class ScenarioPackets : IMinionPacketSink
    {
        public void Clear() { }
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
}
