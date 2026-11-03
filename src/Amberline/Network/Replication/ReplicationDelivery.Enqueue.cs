using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    public bool IsEffectPending(uint netId) => _pending.OfType<EffectCreateReplicationMessage>()
        .Any(message => message.EffectNetId == netId);

    public bool IsAnimationPending(uint netId) => _pending.OfType<PacketReplicationMessage>()
        .Any(message => message.ObserverId is null && message.Packet.Plaintext.Length > 0 &&
            message.Packet.Plaintext.Span[0] == 0xB8 &&
            message.Packet.EntityReferences.Any(reference => reference.NetId == netId));

    public void Enqueue(PlaintextPacket packet, bool global = false)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Enqueue(new PacketReplicationMessage(packet, global));
    }

    /// <summary>Batch 405: a toggle carrier's add, sent behind the last rename of its slot in the same flush (see MoveToggleCarriersBehindTheirSlotRenames).</summary>
    public void EnqueueToggleCarrier(PlaintextPacket packet, uint unitNetId, int toggleSlot)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Enqueue(new PacketReplicationMessage(packet, ToggleCarrier: new ToggleCarrierMark(unitNetId, toggleSlot)));
    }

    /// <summary>
    /// Batch 471: queues a packet for every observer of one team, in sight or not (a team's Dominion quest tracker, 0x90, and its quest
    /// announcement). An observer registered later does not get it.
    /// </summary>
    public void EnqueueForTeam(Team team, PlaintextPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        foreach (var observer in _observers.Where(observer => observer.Team == team))
            Enqueue(new PacketReplicationMessage(packet, Global: true, ObserverId: observer.ObserverId));
    }

    /// <summary>Queues a packet for one ready observer without widening its visibility.</summary>
    public void EnqueueForObserver(ulong observerId, PlaintextPacket packet)
    {
        if (observerId == 0) throw new ArgumentOutOfRangeException(nameof(observerId));
        ArgumentNullException.ThrowIfNull(packet);
        Enqueue(new PacketReplicationMessage(packet, Global: false, ObserverId: observerId));
    }

    public void EnqueueLifecycle(
        uint victimNetId,
        uint killerNetId,
        Func<uint, PlaintextPacket> buildPacket)
        => Enqueue(new LifecycleReplicationMessage(victimNetId, killerNetId, buildPacket));

    public void EnqueueCast(SpellCastReplicationMessage message) => Enqueue(message);

    public void EnqueueMissile(MissileReplicationMessage message) => Enqueue(message);

    public void EnqueueEffectCreate(EffectCreateReplicationMessage message) => Enqueue(message);

    public void EnqueueEffectDestroy(EffectDestroyReplicationMessage message) => Enqueue(message);

    public void EnqueueChainMissileSync(ChainMissileSyncReplicationMessage message) => Enqueue(message);

    public void EnqueueLineMissileHitList(LineMissileHitListReplicationMessage message) => Enqueue(message);

    public void EnqueueCharacterData(CharacterDataReplicationMessage message) => Enqueue(message);

    public void EnqueueTemporaryCasterRetirement(TemporaryCasterRetirementMessage message) => Enqueue(message);

    /// <summary>
    /// Turns the script heals recorded by <c>Healing.Apply</c> into UnitApplyHeal packets, after
    /// everything queued earlier this tick. Routing is the ordinary target rule: only observers that
    /// currently see the healed unit receive it (retail sends it through the unit's local view).
    /// </summary>
    private void QueueHealAnnouncements()
    {
        foreach (var entity in World.Entities.Values)
        {
            var heals = entity.PendingHealAnnouncements;
            if (heals.Count == 0) continue;
            foreach (var heal in heals)
                _pending.Add(new PacketReplicationMessage(
                    CombatPackets.UnitApplyHeal(entity.NetId, heal.MaxHealth, heal.Amount)));
            heals.Clear();
        }
    }
}
