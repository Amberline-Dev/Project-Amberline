using Amberline.Core;

namespace Amberline.Tests.Core;

public static class AllocatorChecks
{
    [Check("core-allocator")]
    public static void AllocatorRangesAndMissileWrap()
    {
        var allocator = new NetIdAllocator();
        var heroRange = NetIdAllocator.RangeFor(UnitType.Hero);
        TestAssert.Equal(0x40000001u, allocator.Alloc(UnitType.Hero));
        for (var i = 1u; i < heroRange.Count; i++)
        {
            TestAssert.Equal(heroRange.Base + i, allocator.Alloc(UnitType.Hero));
        }

        TestAssert.Throws<InvalidOperationException>(() => allocator.Alloc(UnitType.Hero));

        var missiles = new NetIdAllocator();
        var missileRange = NetIdAllocator.RangeFor(UnitType.Missile);
        uint last = 0;
        for (var i = 0u; i <= missileRange.Count; i++)
        {
            last = missiles.Alloc(UnitType.Missile);
        }

        TestAssert.Equal(missileRange.Base, last);
        TestAssert.Equal(missileRange.Base + 1, missiles.Alloc(UnitType.Missile));
        TestAssert.Equal(missileRange, NetIdAllocator.RangeFor(UnitType.Missile));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => NetIdAllocator.RangeFor((UnitType)99));
    }
}
