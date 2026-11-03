using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 271: a fear wander is a two-point path, and the client installs it like every path
    /// (the client's path install, any unit): the actor is stopped (it zeroes the stored velocity)
    /// and seated on the path's first point, its two-unit lattice anchor. Batch 263 left the wanders unseated and moving
    /// (open question 2). The forced move walks the segment, so the unit keeps no route after it.
    /// </summary>
    [Check("movement.fear-wander-is-installed-like-a-path")]
    public static void FearWanderIsInstalledLikeAPath()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        match.CastGround(0, owner.Position, 3);
        runtime.Tick(.5);
        var failures = new List<string>();

        // Hero.luaobj's TimerFeared (HeroAttackOrderSystem.WanderFeared).
        var hero = match.Hero(1);
        Wander("hero", hero, new Position2(7600.7, 7700.3), () => runtime.Spells.HeroOrders!.WanderFeared(hero, new Position2(7900, 8000)));

        // Pet.lua's SetStateAndMove(AI_FEARED, point) (Tibbers).
        var pet = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
        Wander("pet", pet, new Position2(7500.7, 10000.3),
            () => runtime.Pets.Engine.SetStateAndMove(pet, PetLuaAiEngine.StateFeared, new Position2(7800, 10000)));

        // minionLib's SetStateAndMove(AI_FEARED, point).
        var stats = MinionDefinitions.BaseStats(MinionType.Melee);
        var minion = runtime.World.Register(new Entity(0x40027101, EntityKind.Minion, UnitType.Minion, Team.Chaos,
            new Position2(9000.7, 7000.3))
        {
            MinionType = MinionType.Melee, Alive = true, Created = true, Visible = true, Speed = stats.MoveSpeed, Radius = stats.Radius,
        });
        Wander("minion", minion, minion.Position,
            () => runtime.Minions!.NativeSetStateAndMove(minion, MinionAiState.Feared, new Position2(9300, 7000)));

        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Wander(string name, Entity unit, Position2 start, Action act)
        {
            unit.Position = start;
            var anchor = runtime.Movement.ClientAnchor(start);
            TestAssert.True(anchor != start, $"fixture: the {name} stands off the lattice");
            unit.FrameVelocity = new Position2(20, 5);
            act();
            if (unit.ForcedMove is not { Effect: "Fear" } move)
            {
                failures.Add($"{name}: no Fear wander started");
                return;
            }
            if (unit.Position != anchor) failures.Add($"{name}: stands on {unit.Position}, not on its path's first point {anchor}");
            if (unit.FrameVelocity != Position2.Zero) failures.Add($"{name}: the path's install did not stop it ({unit.FrameVelocity})");
            if (unit.Waypoints.Count != 0) failures.Add($"{name}: keeps a route after the wander ({unit.Waypoints.Count} points)");
            if (move.Destination == anchor) failures.Add($"{name}: the wander does not walk");
        }
    }
}
