using Amberline.Core;

namespace Amberline.Tests.Core;

public static class WorldChecks
{
    [Check("core-world")]
    public static void OrderedRegistryAndQueries()
    {
        var world = new WorldState();
        var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            Health = 100.0, MaxHealth = 100.0, Armor = 10.0, SpellBlock = 10.0,
            Speed = 300.0, AttackRange = 625.0, MaxMana = 100.0, Mana = 100.0,
        };
        var enemy = new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new Position2(10.0, 0.0))
        {
            Health = 50.0, MaxHealth = 50.0, Speed = 325.0, AttackRange = 100.0,
        };
        var pet = new Entity(3, EntityKind.Pet, UnitType.Monster, Team.Order, new Position2(20.0, 0.0))
        {
            OwnerNetId = hero.NetId, Health = 50.0, MaxHealth = 50.0,
            Speed = 300.0, AttackRange = 100.0,
        };

        world.Register(hero);
        world.Register(enemy);
        world.Register(pet);
        TestAssert.Equal(new uint[] { 1, 2, 3 }, world.Entities.Keys.ToArray());
        TestAssert.True(world.IsAlive(1));
        TestAssert.False(world.IsAlive(99));
        TestAssert.True(WorldState.IsEnemy(hero, enemy));
        TestAssert.False(WorldState.IsEnemy(hero, pet));
        TestAssert.Equal(1, world.Heroes().Count);

        var replacement = new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new Position2(30.0, 0.0));
        world.Register(replacement);
        TestAssert.Equal(new uint[] { 1, 2, 3 }, world.Entities.Keys.ToArray());
        TestAssert.True(ReferenceEquals(replacement, world.Get(2)));

        TestAssert.True(world.Remove(2));
        world.Register(replacement);
        TestAssert.Equal(new uint[] { 1, 3, 2 }, world.Entities.Keys.ToArray());

        var neutral = new Entity(4, EntityKind.Neutral, UnitType.Monster, Team.Neutral, new Position2(100.0, 0.0));
        TestAssert.True(WorldState.IsEnemy(hero, neutral));
        TestAssert.False(WorldState.IsEnemy(neutral, new Entity(5, EntityKind.Monster,
            UnitType.Monster, Team.Neutral, new Position2(0.0, 0.0))));

        world.LocalHeroNetId = hero.NetId;
        TestAssert.True(ReferenceEquals(hero, world.LocalHero()));
        var one = world.IndexUnits();
        TestAssert.Equal(3, world.Near(one, new Position2(0.0, 0.0), 50.0).Count);
    }
}
