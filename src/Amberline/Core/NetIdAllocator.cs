namespace Amberline.Core;

public readonly record struct NetIdRange(uint Base, uint Ceiling)
{
    public uint Count => checked(Ceiling - Base);
}

/// <summary>Allocates the era-126 contiguous network-ID ranges per unit class.</summary>
public sealed class NetIdAllocator
{
    private readonly Dictionary<UnitType, uint> _next = new();

    public NetIdAllocator()
    {
        Reset();
    }

    public void Reset()
    {
        _next.Clear();
        foreach (var unitType in Enum.GetValues<UnitType>())
        {
            _next[unitType] = RangeFor(unitType).Base;
        }
    }

    public uint Alloc(UnitType unitType)
    {
        var range = RangeFor(unitType);
        var next = _next[unitType];

        if (unitType == UnitType.Missile)
        {
            var offset = next - range.Base;
            var allocated = range.Base + offset % range.Count;
            _next[unitType] = range.Base + (offset + 1) % range.Count;
            return allocated;
        }

        if (next >= range.Ceiling)
        {
            throw new InvalidOperationException(
                $"The {unitType} network-ID range is exhausted at 0x{range.Ceiling:X8}.");
        }

        _next[unitType] = checked(next + 1);
        return next;
    }

    public uint Next(UnitType unitType)
    {
        _ = RangeFor(unitType);
        return _next[unitType];
    }

    public static NetIdRange RangeFor(UnitType unitType) => unitType switch
    {
        UnitType.Hero => new(0x40000001, 0x40000040),
        UnitType.Turret => new(0x40000040, 0x40010000),
        UnitType.Minion => new(0x40010000, 0x40020000),
        UnitType.Missile => new(0x40020000, 0x40030000),
        UnitType.Monster => new(0x40030000, 0x40040000),
        UnitType.Structure => new(0x40040000, 0x40050000),
        _ => throw new ArgumentOutOfRangeException(nameof(unitType)),
    };
}
