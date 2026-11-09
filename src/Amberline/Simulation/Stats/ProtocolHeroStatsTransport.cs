using System.Linq;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Stats;

/// <summary>
/// Protocol adapter for the authoritative hero-stat snapshot.  The transport
/// receives the already-calculated snapshot, so it cannot silently recalculate
/// or omit level, action-state, mana-cost, or equipment tooltip fields.
/// </summary>
public sealed class ProtocolHeroStatsTransport : IHeroStatsTransport
{
    private readonly ICombatPacketQueue _queue;
    private readonly ReplicationSyncCounter _syncIds;

    public ProtocolHeroStatsTransport(ICombatPacketQueue queue, ReplicationSyncCounter syncIds)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _syncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
    }

    public void Push(HeroStatsReplicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _queue.Enqueue(CombatPackets.HeroStats(snapshot, _syncIds.Next(snapshot.HeroNetId)));
    }

    public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;
        _queue.Enqueue(SpellPackets.SpellTooltipVariables(
            entries.Select(entry => new SpellTooltipEntry(
                checked((byte)entry.Slot),
                Float(entry.First), Float(entry.Second), Float(entry.Third))).ToArray(),
            heroNetId));
    }

    private static float Float(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (float)value;
    }
}
