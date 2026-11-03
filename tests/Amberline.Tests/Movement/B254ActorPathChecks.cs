using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 254: what the client's server-path follower does to every actor a path reaches (it is called from
/// the client's waypoint handler, with no unit-type test): it stops the actor first (the stop routine zeroes the stored velocity).
/// </summary>
public static class B254ActorPathChecks
{
    [Check("movement.new-path-stops-the-actor")]
    public static void NewPathStopsTheActor()
    {
        var world = new WorldState();
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
        var minion = world.Register(new Entity(100, EntityKind.Minion, UnitType.Minion, Team.Order, new Position2(7000, 7000))
            { Alive = true, Radius = 36, Speed = 325 });
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(7000, 7400))
            { Alive = true, Radius = 40, Speed = 350 });
        var pet = world.Register(new Entity(200, EntityKind.Pet, UnitType.Monster, Team.Order, new Position2(7000, 7800))
            { Alive = true, Radius = 40, Speed = 350 });
        foreach (var unit in new[] { minion, hero, pet })
        {
            unit.Waypoints = [unit.Position, unit.Position + new Position2(400, 0)];
            if (unit.UnitType == UnitType.Hero) movement.TickHeroes(); else movement.TickNonHeroes();
            TestAssert.True(unit.FrameVelocity.X > 1.0, $"{unit.Kind}: the walk stores a velocity");
            var route = movement.SetMoveOrder(unit.NetId, unit.Position + new Position2(0, 300));
            TestAssert.True(route.Count > 1, $"{unit.Kind}: the new order has a route");
            TestAssert.Equal(Position2.Zero, unit.FrameVelocity, $"{unit.Kind}: the new path stops the actor");
        }
    }

    /// <summary>
    /// The client's server-path follower then seats the actor on the path's first point with no unit-type test, and the
    /// server publishes every route from the unit's 2-unit lattice anchor: a minion, a monster and a pet stand where the hero already did.
    /// A stop is a one-point path on that anchor.
    /// </summary>
    [Check("movement.every-actor-is-seated-on-its-path-first-point")]
    public static void EveryActorIsSeatedOnItsPathFirstPoint()
    {
        foreach (var (kind, type) in new[]
                 {
                     (EntityKind.Minion, UnitType.Minion), (EntityKind.Monster, UnitType.Monster),
                     (EntityKind.Pet, UnitType.Monster), (EntityKind.Hero, UnitType.Hero),
                 })
        {
            var world = new WorldState();
            var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
            var unit = world.Register(new Entity(100, kind, type, Team.Order, new Position2(7000.7, 7000.3))
                { Alive = true, Radius = 36, Speed = 325 });
            var anchor = movement.ClientAnchor(unit.Position);
            TestAssert.True(anchor != unit.Position, "fixture: the unit stands off the lattice");
            var route = movement.SetMoveOrder(unit.NetId, new Position2(7400, 7000));
            TestAssert.True(route.Count > 1, $"{kind}: the order has a route");
            TestAssert.Equal(route[0], unit.Position, $"{kind}: seated on the published first point");
            TestAssert.Equal(anchor, unit.Position, $"{kind}: which is its lattice anchor");

            if (unit.UnitType == UnitType.Hero) movement.TickHeroes(); else movement.TickNonHeroes();
            unit.Position += new Position2(0.7, 0.3);
            var stopAnchor = movement.ClientAnchor(unit.Position);
            unit.Waypoints.Clear();
            if (unit.UnitType == UnitType.Hero) movement.TickHeroes(); else movement.TickNonHeroes();
            TestAssert.Equal(stopAnchor, unit.Position, $"{kind}: a stop seats the unit on its one-point path");
        }
    }
}
