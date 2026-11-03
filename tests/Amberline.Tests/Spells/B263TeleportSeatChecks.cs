using Amberline.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 263: a teleport that publishes a one-point path seats the unit on that path's point at once. The path's install,
    /// the client's path install (any unit) stops the actor (it zeroes the stored velocity) and
    /// sets its position to the path's first point, the two-unit lattice anchor; batch 254 left the pet there only at
    /// its next order or frame stop, and a respawned hero at its next order.
    /// </summary>
    [Check("movement.pet-leash-blink-seats-at-once")]
    public static void PetLeashBlinkSeatsAtOnce()
    {
        // Pet.lua TimerScanDistance's leash blink: SetActorPositionFromObject onto the owner, then the one-point path.
        using (var match = new Match("Annie", Team.Order))
        {
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            match.Hero(1).Position = new Position2(12000, 12000);
            match.CastGround(0, owner.Position, 3);
            runtime.Tick(.5);
            var pet = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
            owner.Position = new Position2(7500.7, 10000.3);
            var anchor = runtime.Movement.ClientAnchor(owner.Position);
            TestAssert.True(anchor != owner.Position, "fixture: the owner stands off the lattice");
            pet.FrameVelocity = new Position2(20, 5);
            runtime.Pets.Engine.SetActorPositionFromObject(pet, owner);
            TestAssert.Equal(anchor, pet.Position, "the blink's one-point path seats the pet on its anchor at once");
            TestAssert.Equal(Position2.Zero, pet.FrameVelocity, "the path's install stops the pet");
            TestAssert.Equal(0, pet.Waypoints.Count, "the pet keeps no route");
        }
    }

    /// <summary>
    /// A respawn is the teleport to the spawn point and its one-point path: the hero stands on the spawn point's lattice anchor at once.
    /// The spawn point stays the exact <c>__Spawn_T1.SCB</c> centre (owner decision, 18 October 2026); the client's install seats the hero
    /// on its anchor, as every next order already did (batch 254).
    /// </summary>
    [Check("movement.respawn-seats-at-once")]
    public static void RespawnSeatsAtOnce()
    {
        using (var match = new Match("Annie", Team.Order))
        {
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.Alive = false;
            hero.RespawnAt = 1.0;
            hero.FrameVelocity = new Position2(20, 5);
            TestAssert.True(runtime.Combat.RespawnImmediately(hero), "the hero respawns");
            var spawn = new Position2(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);
            var anchor = runtime.Movement.ClientAnchor(spawn);
            TestAssert.True(anchor != spawn, "fixture: the spawn point lies off the lattice");
            TestAssert.Equal(anchor, hero.Position, "the respawn's one-point path seats the hero on its anchor at once");
            TestAssert.Equal(Position2.Zero, hero.FrameVelocity, "the path's install stops the hero");
            TestAssert.Equal(0, hero.Waypoints.Count, "the hero keeps no route");
        }
    }
}
