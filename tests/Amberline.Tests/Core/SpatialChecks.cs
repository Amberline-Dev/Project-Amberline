using Amberline.Core;

namespace Amberline.Tests.Core;

public static class SpatialChecks
{
    [Check("core-spatial")]
    public static void SnapshotOrderAndNegativeFlooring()
    {
        var index = new SpatialIndex<string>(new[]
        {
            (0.0, 0.0, "origin"),
            (399.0, 0.0, "same-cell"),
            (-0.1, 0.0, "negative-cell"),
            (-400.0, 0.0, "negative-boundary"),
            (800.0, 0.0, "far-cell"),
        });

        TestAssert.Equal(new[] { "origin", "same-cell" }, index.Query(0.0, 0.0, 0.0));
        TestAssert.Equal(new[] { "origin", "same-cell", "negative-cell", "negative-boundary" },
            index.Query(0.0, 0.0, 1.0));
        TestAssert.Equal(new[] { "origin", "same-cell", "negative-cell", "negative-boundary" },
            index.Query(-200.0, 0.0, 200.0));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => index.Query(0.0, 0.0, -1.0));
    }
}
