using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 248: every unit's order is planned by the engine planner (the client's path builder, the port of which is
/// MovementSystem.PlanClientHeroRoute). The emulator's NPC planner (the strategic grid with the wider 260-unit turret stamp, its first-leg swap)
/// was a separate policy and is gone.
/// </summary>
public static class B248NpcPlannerChecks
{
    // Long orders across lanes, jungle and the turret margins (the 879f4fc4 caster stood in Turret_T2_C_04's margin).
    private static readonly (Position2 From, Position2 To)[] Orders =
    [
        (new Position2(9422.0, 9741.0), new Position2(7600.0, 7900.0)),
        (new Position2(2000.0, 2000.0), new Position2(6900.0, 7200.0)),
        (new Position2(3500.0, 7400.0), new Position2(7900.0, 4100.0)),
        (new Position2(12000.0, 12100.0), new Position2(9800.0, 4500.0)),
        (new Position2(1600.0, 11300.0), new Position2(4400.0, 13100.0)),
        (new Position2(10500.0, 1400.0), new Position2(13300.0, 4500.0)),
    ];

    [Check("movement.npc-orders-are-planned-by-the-engine-planner")]
    public static void NpcOrdersArePlannedByTheEnginePlanner()
    {
        var grid = NavigationGrid.LoadDefault();
        foreach (var (kind, type) in new[] { (EntityKind.Monster, UnitType.Monster), (EntityKind.Minion, UnitType.Minion) })
        foreach (var (from, to) in Orders)
        {
            var world = new WorldState();
            var unit = world.Register(new Entity(0x4F00_2482, kind, type, Team.Neutral, from)
            {
                Alive = true, Radius = 50.0, Speed = 330.0,
            });
            var movement = new MovementSystem(world, grid);
            var engine = movement.PlanClientHeroRoute(unit, to, static _ => true);
            var route = movement.SetMoveOrder(unit.NetId, to);
            TestAssert.True(engine is { Count: > 1 }, $"the engine planner finds a route {from} -> {to}");
            TestAssert.Equal(engine!.Count, route.Count, $"{kind} {from} -> {to}: the order is the engine planner's route");
            for (var index = 0; index < route.Count; index++)
                TestAssert.True(Math.Abs(route[index].X - engine[index].X) <= 1e-3 && Math.Abs(route[index].Z - engine[index].Z) <= 1e-3,
                    $"{kind} {from} -> {to}: point {index} is {route[index]}, the engine planner's is {engine[index]}");
        }
    }
}
