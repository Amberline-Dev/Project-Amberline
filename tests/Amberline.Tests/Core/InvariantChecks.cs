using Amberline.Core;

namespace Amberline.Tests.Core;

public static class InvariantChecks
{
    [Check("core-invariants")]
    public static void PerClassChecksAndWorldReporting()
    {
        var validHero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            Health = 100.0, MaxHealth = 100.0, Mana = 50.0, MaxMana = 50.0,
            Armor = 10.0, SpellBlock = 30.0, Speed = 340.0, AttackRange = 625.0,
        };
        TestAssert.Equal(0, EntityInvariants.Check(validHero).Count);
        TestAssert.Equal(1, EntityInvariants.Check(null).Count);
        var nonFinite = new Entity(9, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(double.NaN, 0.0))
        {
            Health = 100.0, MaxHealth = 100.0, Mana = 50.0, MaxMana = 50.0,
            Armor = 10.0, SpellBlock = 30.0, Speed = 340.0, AttackRange = 625.0,
        };
        TestAssert.True(EntityInvariants.Check(nonFinite).Any(message => message.Contains("finite")));

        var invalidHero = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            Health = 0.0, MaxHealth = 100.0, Armor = 0.0, SpellBlock = 0.0,
            Speed = 0.0, AttackRange = 0.0, MaxMana = 0.0,
        };
        // hp, armor, spell block, speed and attack range; a hero's empty pool (MaxMana 0) is legitimate since the zero-pool champions (W27b).
        TestAssert.True(EntityInvariants.Check(invalidHero).Count >= 5);
        TestAssert.Equal(0, EntityInvariants.Check(new Entity(8, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
        {
            Health = 100.0, MaxHealth = 100.0, Mana = 0.0, MaxMana = 0.0,
            Armor = 10.0, SpellBlock = 30.0, Speed = 340.0, AttackRange = 175.0,
        }).Count);
        TestAssert.Throws<InvalidOperationException>(() =>
            EntityInvariants.Enforce(invalidHero, strict: true));

        var ward = new Entity(3, EntityKind.Ward, UnitType.Monster, Team.Order, new Position2(0.0, 0.0))
        {
            IsWard = true, Health = 1.0, MaxHealth = 1.0, Speed = 0.0, AttackRange = 0.0,
        };
        TestAssert.Equal(0, EntityInvariants.Check(ward).Count);

        var box = new Entity(4, EntityKind.ShacoBox, UnitType.Monster, Team.Order, new Position2(0.0, 0.0))
        {
            Name = "ShacoBox", Health = 1.0, MaxHealth = 1.0, Speed = 0.0, AttackRange = 100.0,
        };
        TestAssert.Equal(0, EntityInvariants.Check(box).Count);

        var reports = new List<uint>();
        var world = new WorldState(invariantReporter: (entity, _) => reports.Add(entity.NetId));
        world.Register(invalidHero);
        world.Register(invalidHero);
        TestAssert.Equal(new uint[] { 2 }, reports.ToArray());

        var failingReporterWorld = new WorldState(invariantReporter: (_, _) => throw new InvalidOperationException("sink"));
        failingReporterWorld.Register(invalidHero);
    }
}
