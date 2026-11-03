namespace Amberline.Simulation.Combat;

/// <summary>
/// Match-owned monotonic replication IDs. The native protocol keeps one
/// counter per unit across HP-only, hero, minion, turret, and building 0xCC
/// updates; packet builders remain stateless and receive the selected ID.
/// </summary>
public sealed class ReplicationSyncCounter
{
    private readonly Dictionary<uint, uint> _values = new();

    public uint Next(uint unitNetId)
    {
        if (unitNetId == 0) throw new ArgumentOutOfRangeException(nameof(unitNetId));
        var next = checked(_values.GetValueOrDefault(unitNetId) + 1u);
        _values[unitNetId] = next;
        return next;
    }

    /// <summary>Aligns a replayed observer refresh without emitting a packet.</summary>
    public void EnsureAtLeast(uint unitNetId, uint value)
    {
        if (unitNetId == 0) throw new ArgumentOutOfRangeException(nameof(unitNetId));
        _values[unitNetId] = Math.Max(_values.GetValueOrDefault(unitNetId), value);
    }

    /// <summary>
    /// Starts a fresh replay/loading phase before any packets are emitted.
    /// Production match composition calls this only while constructing a new
    /// match; it is also useful to seed an isolated packet corpus.
    /// </summary>
    public void Reset() => _values.Clear();
}
