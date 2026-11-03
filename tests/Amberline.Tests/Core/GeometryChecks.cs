using Amberline.Core;

namespace Amberline.Tests.Core;

public static class GeometryChecks
{
    [Check("core-geometry")]
    public static void NativeQuantizationAndGeometry()
    {
        var bounds = new NavigationBounds(0.0, 14_000.0, 0.0, 14_400.0);
        var quantized = Geometry.ClientWaypoints(new[]
        {
            new Position2(6_999.9, 7_201.1),
            new Position2(6_997.0, 7_205.0),
        }, bounds);
        TestAssert.Equal(new Position2(7_000.0, 7_200.0), quantized[0]);
        TestAssert.Equal(new Position2(6_998.0, 7_204.0), quantized[1]);
        TestAssert.Equal(0, Geometry.ClientWaypoints(Array.Empty<Position2>(), bounds).Count);
        TestAssert.Throws<ArgumentOutOfRangeException>(() => Geometry.ClientWaypoints(
            new[] { new Position2(double.NaN, 0.0) }, bounds));

        var path = new[]
        {
            new Position2(0.0, 0.0),
            new Position2(0.0, 0.0),
            new Position2(10.0, 0.0),
        };
        var walked = Geometry.PositionAlong(path, 5.0);
        TestAssert.Equal(new Position2(5.0, 0.0), walked!.Value);
        TestAssert.Equal(new Position2(10.0, 0.0), Geometry.PositionAlong(path, 50.0)!.Value);
        TestAssert.True(Geometry.PositionAlong(Array.Empty<Position2>(), 1.0) is null);

        var contact = Geometry.SweptContact(new Position2(-10.0, 0.0), new Position2(10.0, 0.0),
            new Position2(0.0, 0.0), 1.0);
        TestAssert.Near(0.45, contact!.Value, 1e-12);
        TestAssert.Equal(0.0, Geometry.SweptContact(new Position2(0.0, 0.0), new Position2(1.0, 0.0),
            new Position2(0.0, 0.0), 1.0)!.Value);

        var entity = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(2.0, 2.0));
        entity.Facing = new Position2(0.0, 1.0);
        TestAssert.Equal(new Position2(0.0, 1.0), Geometry.FaceTowards(entity, entity.Position));
        TestAssert.Equal(new Position2(1.0, 0.0), Geometry.FaceTowards(entity, new Position2(3.0, 2.0)));
    }
}
