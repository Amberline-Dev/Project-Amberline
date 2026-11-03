using Amberline.Network.Protocol;

namespace Amberline.Simulation.Combat;

/// <summary>
/// Ordered packet intent sink used by combat transports. Implementations may
/// route intents through observer visibility, or capture them before delivery
/// for protocol parity checks; lifecycle intents retain their typed builder.
/// </summary>
public interface ICombatPacketQueue
{
    void Enqueue(PlaintextPacket packet, bool global = false);
    void EnqueueLifecycle(uint victimNetId, uint killerNetId,
        Func<uint, PlaintextPacket> buildPacket);

    /// <summary>
    /// Batch 405: a toggle carrier's add (a 0xBF of a buff whose script sets <c>SpellToggleSlot</c>, see <c>BuffPayload.ToggleSlot</c>) names the
    /// 0-based slot it toggles on unit <paramref name="unitNetId"/>. A queue that orders its packets sends it behind that slot's swap of the same
    /// flush; every other queue sends it as a plain packet.
    /// </summary>
    void EnqueueToggleCarrier(PlaintextPacket packet, uint unitNetId, int toggleSlot) => Enqueue(packet);
}
