using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 254 restates the body-aware pet planner's route checks (movement.body-aware-pet-preserves-clear-terrain-route,
/// movement.body-aware-pet-avoids-planted-minion-body) to the engine planner every unit's order runs on
/// (the client's order handler and path builder, MovementSystem.PlanClientHeroRoute).
/// </summary>
public static partial class PetEngineRouteChecks
{
    [Check("movement.pet-route-ignores-a-distant-owner-body")]
    public static void PetRouteIgnoresADistantOwnerBody()
    {
        // Tibbers follow-straight frame 58: the owner is a distant mobile body. The order is the engine planner's route, and the owner
        // standing well off it does not change it.
        var world = new WorldState();
        var owner = world.Register(new Entity(1073741825, EntityKind.Hero,
            UnitType.Hero, Team.Order, new Position2(8480, 7000))
        {
            Radius = 54.54439926147461, Speed = 310, Alive = true,
        });
        var pet = world.Register(new Entity(1073938432, EntityKind.Pet,
            UnitType.Monster, Team.Order, new Position2(7945, 7115))
        {
            Name = "AnnieTibbers", OwnerNetId = owner.NetId,
            Radius = 24.654800415039062, Speed = 350, Alive = true,
        });
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
        var destination = new Position2(8445, 7165);
        var engine = movement.PlanClientHeroRoute(pet, destination, static _ => true);
        var route = movement.SetMoveOrder(pet.NetId, destination).ToArray();
        EnginePlannerRoutes.AssertSame(engine, route, "the pet's order");
        owner.Alive = false;
        pet.Waypoints.Clear();
        var alone = movement.SetMoveOrder(pet.NetId, destination).ToArray();
        TestAssert.Equal(alone, route, "a distant owner body does not change the route");
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));
}
