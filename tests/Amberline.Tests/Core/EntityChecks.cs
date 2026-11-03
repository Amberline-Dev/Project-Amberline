using Amberline.Core;

namespace Amberline.Tests.Core;

public static class EntityChecks
{
    [Check("core-entity")]
    public static void EntityDefaultsAndMappings()
    {
        TestAssert.Equal(0, (int)UnitType.Hero);
        TestAssert.Equal(1, (int)UnitType.Minion);
        TestAssert.Equal(2, (int)UnitType.Turret);
        TestAssert.Equal(3, (int)UnitType.Monster);
        TestAssert.Equal(4, (int)UnitType.Structure);
        TestAssert.Equal(5, (int)UnitType.Missile);
        TestAssert.Equal(100, TeamMapping.ToNative(Team.Order));
        TestAssert.Equal(200, TeamMapping.ToNative(Team.Chaos));
        TestAssert.Equal(300, TeamMapping.ToNative(Team.Neutral));
        TestAssert.Equal(Team.Chaos, TeamMapping.FromNative(200));

        TestAssert.Equal(EntityKind.ShacoBox, EntityKindExtensions.FromSourceType("shaco_box"));
        TestAssert.Equal(EntityKind.YorickRevenant,
            EntityKindExtensions.FromSourceType("yorick_revenant"));
        TestAssert.Equal(EntityKind.Unknown, EntityKindExtensions.FromSourceType("future_unit"));
        TestAssert.Equal("shaco_clone", EntityKind.ShacoClone.ToSourceType());

        var first = new Entity(7, "hero", UnitType.Hero, Team.Order, new Position2(10.0, 20.0));
        var second = new Entity(8, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0.0, 0.0));
        first.Waypoints.Add(new Position2(1.0, 2.0));
        first.SpellRanks[0] = 3;
        TestAssert.Equal(EntityKind.Hero, first.Kind);
        TestAssert.Equal("hero", first.EntityType);
        TestAssert.Equal(new Position2(10.0, 20.0), first.Position);
        TestAssert.Equal(1, first.Waypoints.Count);
        TestAssert.Equal(0, second.Waypoints.Count);
        TestAssert.Equal(0, second.SpellRanks[0]);
        TestAssert.Equal(450.0, first.AcquisitionRange);
        TestAssert.Equal(1, first.Level);
        TestAssert.Equal(1.0, first.AttackSpeedMod);
        TestAssert.Equal(new Position2(0.0, 1.0), first.Facing);
        first.Kind = EntityKind.Minion;
        TestAssert.Equal("minion", first.EntityType);
        TestAssert.Throws<ArgumentException>(() =>
            new Entity(9, "future_unit", UnitType.Minion, Team.Order, new Position2(0.0, 0.0)));
    }
}
