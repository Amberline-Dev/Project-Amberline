using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Batch 248, goal 3: what the client does, confirmed in its code.
/// The client's server-path follower seats the actor on the path's first point with no terrain test, and
/// Minion.lua's forward navigation is SetStateAndMoveToForwardNav(AI_ATTACKMOVESTATE), with no return state.
/// </summary>
public static class B248HeroAnchorChecks
{
    [Check("movement.hero-is-seated-on-its-wire-anchor-whatever-its-cell")]
    public static void HeroIsSeatedOnItsWireAnchor()
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        // Game 879f4fc4: the exact position is in floor cell (195, 197), its two-unit anchor (9421, 9741) in the stamped cell (194, 197) of
        // Turret_T2_C_04.
        var start = new Position2(9422.96, 9741.13);
        var hero = world.Register(new Entity(0x4F00_2483, EntityKind.Hero, UnitType.Hero, Team.Order, start)
        {
            Alive = true, Radius = 35.0, Speed = 340.0,
        });
        var movement = new MovementSystem(world, grid);
        var anchor = movement.ClientAnchor(start);
        TestAssert.True(grid.IsMovementWalkable(start), "the hero stands on floor");
        TestAssert.False(grid.IsMovementWalkable(anchor), $"its anchor {anchor} is in a blocked cell");
        var route = movement.SetMoveOrder(hero.NetId, new Position2(8600.0, 9900.0));
        TestAssert.True(route.Count > 1, "the order plans a route from the anchor's blocked cell");
        TestAssert.Equal(anchor, hero.Position, "the hero is seated on the anchor its client is seated on");
        TestAssert.Equal(anchor, route[0], "the route starts on the anchor");
        for (var tick = 0; tick < 10; tick++) movement.TickHeroes();
        TestAssert.True(Math.Abs(hero.Position.X - anchor.X) + Math.Abs(hero.Position.Z - anchor.Z) > 50.0, "the hero walks out of the cell");
    }

    [Check("minions.forward-navigation-has-no-return-state")]
    public static void ForwardNavigationHasNoReturnState()
    {
        TestAssert.False(Enum.GetNames<MinionAiState>().Contains("Return"),
            "Minion.lua's forward navigation is AI_ATTACKMOVESTATE: the executor has no return state");
        var unit = new Entity(0x4F00_2484, EntityKind.Minion, UnitType.Minion, Team.Order, new Position2(0.0, 0.0));
        foreach (var state in Enum.GetValues<MinionAiState>())
        {
            unit.MinionState = state;
            TestAssert.Equal(state, unit.MinionState, $"{state} round-trips through the state name");
        }
    }
}
