using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Items;

/// <summary>Concrete packet adapter used by a match composition.</summary>
public sealed class ProtocolItemRuntimeTransport : IItemRuntimeTransport
{
    private readonly ICombatPacketQueue _queue;
    private readonly Func<Entity, bool> _wireReady;

    public ProtocolItemRuntimeTransport(
        ICombatPacketQueue queue,
        Func<Entity, bool> wireReady,
        NavigationGrid navigation,
        ReplicationSyncCounter syncIds)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _wireReady = wireReady ?? throw new ArgumentNullException(nameof(wireReady));
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(syncIds);
    }

    public bool IsWireReady(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _wireReady(unit);
    }

    public void SendUseItem(Entity hero, int inventorySlot, int remainingCount, int charges)
    {
        ArgumentNullException.ThrowIfNull(hero);
        _queue.Enqueue(ItemsPackets.UseItem(hero.NetId, inventorySlot,
            remainingCount, charges));
    }

}
