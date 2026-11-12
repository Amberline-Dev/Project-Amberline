namespace Amberline.Core;

public readonly record struct NetIdRange(uint Base, uint Ceiling)
{
    public uint Count => checked(Ceiling - Base);
}

/// <summary>Allocates the era-126 contiguous network-ID ranges per unit class.</summary>
public sealed class NetIdAllocator
{
    private readonly Dictionary<UnitType, uint> _next = new();
    private bool _missileWrapped;

    public NetIdAllocator()
    {
        Reset();
    }

    /// <summary>
    /// Batch 743: a missile-range id a live object still holds, which the wrap must pass over (the replication layer's persistent effects,
    /// <c>ReplicationDelivery</c>). Reconstruction: the missile range (missiles, effects, perception bubbles) wraps after 65536 ids, about 40
    /// minutes into a match (minion and turret attacks take about 1600 a minute); retail's allocator is server code the client does not hold.
    /// </summary>
    public Func<uint, bool>? InUse { get; set; }

    /// <summary>
    /// Batch 743: a missile-range id handed out again after the range wrapped. It names a new object from now on, so whatever still
    /// remembers the old one under it (a client view's effects, a script host's killed effects) forgets it.
    /// </summary>
    public event Action<uint>? Recycled;

    public void Reset()
    {
        _next.Clear();
        _missileWrapped = false;
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
            var allocated = next;
            // At most one lap over the ids in use: with every id held the next one is handed out anyway, as before batch 743.
            for (var skipped = 0u; skipped < range.Count && InUse?.Invoke(allocated) == true; skipped++)
                allocated = Following(allocated);
            var recycled = _missileWrapped;
            _next[unitType] = Following(allocated);
            if (recycled) Recycled?.Invoke(allocated);
            return allocated;

            uint Following(uint id)
            {
                if (id + 1 < range.Ceiling) return id + 1;
                _missileWrapped = true;
                return range.Base;
            }
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
