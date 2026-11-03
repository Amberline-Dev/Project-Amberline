using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class Wave2PetVisibilityMovementChecks
{
    [Check("movement-b6-pet-fixed-origin-body-corner")]
    public static void PetsWalkTheEnginePlannersRoutePastABodyCorner()
    {
        // Section C / 27 Sept 2026 base-game sweep: a pet ordered past a planted body on its straight line. Restated in batch 254: the
        // emulator's body-aware planner (deleted) re-planned tangent-clear chords its own fixed-origin follower cut at 100 ms; retail plans
        // a pet's order like every unit's (the client's order handler and path builder, PlanClientHeroRoute),
        // whose actor test and smoothing decide the corner, and the contact solver walks it. The pet still reaches the requested point.
        foreach (var kind in new[] { EntityKind.Pet, EntityKind.ShacoClone,
                     EntityKind.YorickRevenant, EntityKind.YorickGhoul })
        {
            var world = new WorldState();
            var grid = NavigationGrid.LoadDefault();
            var movement = new MovementSystem(world, grid);
            var center = movement.ClientAnchor(new Position2(7000, 7000));
            var start = center + new Position2(-100, 0);
            var goal = center + new Position2(100, 0);
            var pet = world.Register(new Entity(1, kind, UnitType.Monster, Team.Order, start)
                { Alive = true, Speed = 350, Radius = 5 });
            var box = world.Register(new Entity(2, EntityKind.ShacoBox, UnitType.Monster, Team.Order, center)
                { Alive = true, Radius = 5 });
            TestAssert.Near(22, movement.BodyClearance(pet, box), 0); // 5 + 5 + native minimum skin 12.
            var engine = movement.PlanClientHeroRoute(pet, goal, static _ => true);
            var route = movement.SetMoveOrder(pet.NetId, goal);
            EnginePlannerRoutes.AssertSame(engine, route, kind + ": the order is the engine planner's route");
            TestAssert.Equal(goal, route[^1], kind + ": retain the requested destination");
            for (var tick = 0; tick < 40 && Distance(pet.Position, goal) > 2; tick++) movement.Tick(.1);
            TestAssert.True(Distance(pet.Position, goal) <= 2, kind + ": the pet walks past the body to its goal");
        }
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));
}
