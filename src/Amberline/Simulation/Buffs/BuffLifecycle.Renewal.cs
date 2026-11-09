using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    /// <summary>
    /// Removes the buff in <paramref name="slot"/>. <paramref name="emitWire"/> suppresses every wire side effect of the
    /// removal; <paramref name="emitRemovalPacket"/> suppresses only the RemoveBuff packet, for the replacing add that
    /// supersedes it (see <see cref="AddBuff"/>): its own add is delivered as NpcBuffReplace instead.
    /// </summary>
    public bool RemoveBuff(Entity unit, int slot, bool emitWire = true, bool emitRemovalPacket = true) =>
        RemoveBuffCore(unit, slot, emitWire, emitMovementDelta: true, emitRemovalPacket);

    /// <summary>
    /// A removal inside a hook run whose removal packet waits until that run ends. The shipped CharScriptIrelia clears IreliaIonianDuelist and
    /// adds it back in the same run every second (BBSpellBuffClear, then BBSpellBuffAdd): with the packet sent at once, the client got a remove and
    /// an add of one buff in one flush (sweep BUF-FLUSH). Here the add of that name in the run takes the held slot, and the delivery turns that add
    /// into the renewal the client holds (<c>ReplicationDelivery.AsHeldBuffUpdate</c>). A clear with no re-add sends its packet when the run ends
    /// (<see cref="FlushRenewalRemovals"/>). The deactivation runs now, as for any removal.
    /// Reconstruction: how the original server sends a clear that the same run re-adds is not in the client; the renewal forms (0x32, 0x1F)
    /// are the ones it has for a live copy, so the add goes out as one of those.
    /// </summary>
    public bool RemoveBuffForRenewal(Entity unit, int slot, object frame)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(frame);
        return HoldRemoval(unit, slot, frame);
    }

    // The removal runs now (the deactivation, the stats, the effects); only its packet is held, with the slot reserved.
    private bool HoldRemoval(Entity unit, int slot, object? frame)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var buff = unit.Buffs.FirstOrDefault(candidate => candidate.Slot == slot);
        if (buff is null) return false;
        var reserved = _retiringSlots.Add((unit.NetId, slot));
        RemoveBuffNow(unit, buff, slot, emitWire: true, emitMovementDelta: true, emitRemovalPacket: false);
        _renewalRemovals.Add(new DeferredRemoval(unit, buff, slot, frame, _tick, reserved));
        return true;
    }

    /// <summary>Sends the removal packets that <see cref="RemoveBuffForRenewal"/> held for one hook run and that no add of the name took.</summary>
    public void FlushRenewalRemovals(object frame)
    {
        if (_renewalRemovals.Count == 0) return;
        foreach (var removal in _renewalRemovals.Where(held => ReferenceEquals(held.Frame, frame)).ToArray()) SendHeld(removal);
    }

    private void SendHeld(DeferredRemoval removal)
    {
        _renewalRemovals.Remove(removal);
        ReleaseRenewalSlot(removal);
        var sent = _services.Effects.SendBuffRemove(removal.Unit, removal.Buff);
        if (sent && removal.Unit.UnitType == UnitType.Hero) _heroStats.Push(removal.Unit);
    }

    // The add of a buff that the same run cleared takes the slot the clear held (RemoveBuffForRenewal).
    private bool TakeRenewalSlot(Entity unit, string name, out int slot)
    {
        var index = _renewalRemovals.FindIndex(held => ReferenceEquals(held.Unit, unit) && held.Buff.Name == name);
        if (index < 0)
        {
            slot = -1;
            return false;
        }
        var removal = _renewalRemovals[index];
        _renewalRemovals.RemoveAt(index);
        ReleaseRenewalSlot(removal);
        slot = removal.Slot;
        return true;
    }

    private void ReleaseRenewalSlot(DeferredRemoval removal)
    {
        if (removal.Reserved) _retiringSlots.Remove((removal.Unit.NetId, removal.Slot));
    }

    private bool RemoveBuffCore(Entity unit, int slot, bool emitWire, bool emitMovementDelta, bool emitRemovalPacket = true)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var buff = unit.Buffs.FirstOrDefault(candidate => candidate.Slot == slot);
        if (buff is null) return false;
        var reserved = _retiringSlots.Add((unit.NetId, slot));
        try { return RemoveBuffNow(unit, buff, slot, emitWire, emitMovementDelta, emitRemovalPacket); }
        finally { if (reserved) _retiringSlots.Remove((unit.NetId, slot)); }
    }
}
