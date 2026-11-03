using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

/// <summary>Batch 254: a route compared with the engine planner's (<c>MovementSystem.PlanClientHeroRoute</c>, the client's path builder).</summary>
internal static class EnginePlannerRoutes
{
    /// <summary>The committed route (its first point the unit's wire anchor) has the engine planner's points.</summary>
    public static void AssertSame(IReadOnlyList<Position2>? engine, IReadOnlyList<Position2> route, string label)
    {
        TestAssert.True(engine is { Count: > 1 }, $"{label}: the engine planner finds a route");
        TestAssert.Equal(engine!.Count, route.Count, $"{label}: the route has the engine planner's {engine.Count} points");
        for (var index = 1; index < engine.Count; index++)
            TestAssert.True(Math.Abs(route[index].X - engine[index].X) <= 1e-3 && Math.Abs(route[index].Z - engine[index].Z) <= 1e-3,
                $"{label}: point {index} is {route[index]}, the engine planner's is {engine[index]}");
    }
}

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 254: a pet's walk is an order the engine plans like every unit's (the client's order handler ->
    /// the client's path builder, <c>MovementSystem.PlanClientHeroRoute</c>), not the emulator's body-aware detour planner.
    /// Pet.lua's hard move (PetLuaAiEngine.Move) and a spawned unit's walk (TibbersAiSystem.Move) both commit the engine planner's route,
    /// here past a planted ally body on the straight line.
    /// </summary>
    [Check("movement.pet-orders-are-planned-by-the-engine-planner")]
    public static void PetOrdersArePlannedByTheEnginePlanner()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            owner.Position = new Position2(7000, 6600);
            match.Hero(1).Position = new Position2(12000, 12000);
            var clone = runtime.Clones.Spawn(owner, owner, new Position2(7300, 7000), CloneKind.ShacoClone,
                owner.Health, "Hallucinate", 18.0, owner.ChampionName, showMinimapIcon: true);
            runtime.SpellBuffs.Add(clone, "HallucinateFull", BuffType.Internal, 18.0, owner.NetId, new BuffPayload());
            Body(runtime, team, new Position2(7600, 7000));
            var goal = new Position2(7900, 7000);
            var engine = runtime.Movement.PlanClientHeroRoute(clone, goal, static _ => true);
            TestAssert.True(runtime.Pets.Command(owner, 6, goal, actorNetId: clone.NetId), "the hard move is accepted");
            EnginePlannerRoutes.AssertSame(engine, clone.Waypoints, $"{team} clone hard move");

            var spawned = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Monster,
                UnitType.Monster, team, new Position2(7300, 7600))
            {
                Alive = true, Created = true, Visible = true, Speed = 330, Radius = 40, Health = 500, MaxHealth = 500,
            });
            Body(runtime, team, new Position2(7600, 7600));
            var spawnedGoal = new Position2(7900, 7600);
            var spawnedEngine = runtime.Movement.PlanClientHeroRoute(spawned, spawnedGoal, static _ => true);
            runtime.Tibbers.Move(spawned, spawnedGoal);
            EnginePlannerRoutes.AssertSame(spawnedEngine, spawned.Waypoints, $"{team} spawned unit walk");
        }

        static void Body(Amberline.Network.Sessions.MatchRuntime runtime, Team team, Position2 at) =>
            runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, team, at)
            {
                Alive = true, Created = true, Visible = true, Speed = 325.0, Radius = 35.0, Health = 1000.0, MaxHealth = 1000.0,
            });
    }
}
