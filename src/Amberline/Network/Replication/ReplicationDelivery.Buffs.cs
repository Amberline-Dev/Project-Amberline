using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;
using System.Buffers.Binary;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    /// <summary>
    /// The character-data layers the persistent ledger holds for a unit (pushed and not popped), in change-id order: what every observer that sees the
    /// unit is given, and the only layers the repair pass of <see cref="Flush"/> keeps (it pops any other layer a view holds).
    /// </summary>
    internal IReadOnlyList<CharacterDataReplicationMessage> CharacterDataOf(uint actor) =>
        _characterData.Values.Where(model => model.Actor == actor).OrderBy(model => model.Change).ToArray();

    /// <summary>
    /// A 0xBF add for a slot this client already holds under the same buff is a change of a live instance. The stock
    /// client tells the three packets apart (its buff manager's packet handler): 0xBF re-adds the
    /// instance and replays its activation sound and voice; NpcBuffUpdateCount (0x1F) carries the
    /// count (@6), the duration (@7) and the running time (@11) into the instance's resize routine,
    /// which restarts the slot's timers (the start is now minus the running time, the end is now plus the duration), rebuilds the stack to that
    /// count and notifies the HUD; NpcBuffReplace (0x32) does the same timer work (it also writes the torch's start anchor)
    /// with the count the slot already has, and ends in the instance's torch restart, which deactivates every auto-effect particle the instance
    /// holds and re-creates the four slots. Both silent forms therefore refresh the HUD timer the owner sees; what
    /// separates them is the stack count and the torch. A renewal of a buff whose template hands the client a particle
    /// of its own (see <c>BuffPayload.ClientActivationFx</c>) goes out as the 0x1F: it is the only renewal form that
    /// resets that timer without re-lighting the particle. Batch 157 part 3: the aura items re-add their aura
    /// every script period with <c>BUFF_RENEW_EXISTING</c>, and a 0x32 per period made Mana Manipulator's
    /// ZettasManaManipulator_itm.troy disappear and reappear once a second.
    /// The client's handlers are what the two silent
    /// packets exist for, and the 0xBF/0x1F/0x32 choice here matches them (0x32 is read as an in-place renewal). Retail's server
    /// buff manager is not in the client binary. Reconstruction: whether the torch particle the restart creates dies with the span it is created with (the
    /// call passes the copy's end - start) or only when the instance drops it is not recoverable from the client binary;
    /// the 0x1F is the safe renewal under either reading because it never re-creates the particle.
    /// Reconstruction: retail's choice of packet where the buff is stacked or renewed is unrecovered; this server decides per observer at
    /// delivery, from the ledger of what that client holds, because the buff lifecycle always emits an add.
    /// </summary>
    private PlaintextPacket AsHeldBuffUpdate(ReplicationView view, PlaintextPacket packet)
    {
        if (!IsHeldRenewal(view, packet.Plaintext.Span, out var add, out var held)) return packet;
        return held.Count != add.Count || ClientActivationFx(add)
            ? SpellPackets.UpdateBuffCount(add.TargetNetId, add.Slot, add.Count, add.Duration,
                add.CasterNetId, add.RunningTime)
            : SpellPackets.RenewBuff(add.TargetNetId, add.Slot, add.Duration, add.CasterNetId, add.RunningTime);
    }

    /// <summary>
    /// The renewal test of <see cref="AsHeldBuffUpdate"/>, shared with the buff pair rule (<see cref="CoalescesBuffPair"/>): the add carries a
    /// live count and finite times, and the view holds a copy of the same name in its slot that has not run out. A copy that has already run out is
    /// not a live instance to renew: both silent handlers return at once when the slot's stack list is empty, so the add stays an add and re-presents
    /// the buff. The periodic re-adds must not land here (BuffPayload.WireDurationMargin keeps the copy alive across the interval).
    /// </summary>
    private bool IsHeldRenewal(ReplicationView view, ReadOnlySpan<byte> bytes, out BuffAdd add,
        out (uint Hash, byte Count, double EndsAt) held)
    {
        held = default;
        return SpellPackets.TryReadBuffAdd(bytes, out add) &&
            add.Count != 0 && float.IsFinite(add.Duration) && add.Duration >= 0f &&
            float.IsFinite(add.RunningTime) &&
            view.Buffs.TryGetValue((add.TargetNetId, add.Slot), out held) &&
            held.Hash == add.BuffNameHash &&
            SimulationTime() < held.EndsAt;
    }

    /// <summary>
    /// Reconstruction (batch 389): pairs this flush's buff adds and removes of one name on one unit. Two buff packets pair when they are adjacent for their
    /// unit and slot (0xBF, 0x7F, 0x1F and 0x32 are the slot's packets, so any other one of them between keeps the pair apart), one is an add and the other
    /// a remove of the same name hash, and both carry the same observer target and global flag, so they reach a viewer alike. A retirement ends the run:
    /// it clears a unit's buffs from the ledger mid-flush. Retail's flush of such a pair is not in the shipped files; the client draws nothing for a copy
    /// that is created and destroyed in one frame, and a renewal changes only the copy the client already holds.
    /// </summary>
    private void PairBuffDeltas()
    {
        _buffPartners.Clear();
        var open = new Dictionary<(uint Unit, byte Slot), PacketReplicationMessage>();
        foreach (var message in _pending)
        {
            if (message is TemporaryCasterRetirementMessage)
            {
                open.Clear();
                continue;
            }
            if (message is not PacketReplicationMessage packet) continue;
            var bytes = packet.Packet.Plaintext.Span;
            if (bytes.Length < 6 || bytes[0] is not (SpellPackets.BuffAddOpcode or 0x7F or
                    SpellPackets.BuffUpdateCountOpcode or SpellPackets.BuffReplaceOpcode))
                continue;
            var key = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), bytes[5]);
            if (open.Remove(key, out var prior) && IsBuffPairMate(prior, packet))
            {
                _buffPartners[prior] = (packet, true);
                _buffPartners[packet] = (prior, false);
            }
            else
                open[key] = packet;
        }
    }

    // An add and a remove of one name hash that share an observer target and a global flag.
    private static bool IsBuffPairMate(PacketReplicationMessage first, PacketReplicationMessage second)
    {
        if (first.ObserverId != second.ObserverId || first.Global != second.Global) return false;
        var a = first.Packet.Plaintext.Span;
        var b = second.Packet.Plaintext.Span;
        return IsBuffAddOrRemove(a) && IsBuffAddOrRemove(b) && (a[0] == 0x7F) != (b[0] == 0x7F) &&
            BuffNameHashOf(a) == BuffNameHashOf(b);
    }

    private static bool IsBuffAddOrRemove(ReadOnlySpan<byte> bytes) => bytes.Length > 0 && bytes[0] switch
    {
        SpellPackets.BuffAddOpcode => bytes.Length >= 13,
        0x7F => bytes.Length >= 10,
        _ => false,
    };

    // The name hash of a buff add (0xBF, at offset 9) or of a remove (0x7F, at offset 6); the caller has checked the length.
    private static uint BuffNameHashOf(ReadOnlySpan<byte> bytes)
    {
        var offset = bytes[0] == SpellPackets.BuffAddOpcode ? 9 : 6;
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    /// <summary>
    /// Reconstruction (batch 389): whether one half of a paired buff delta stays off this observer's wire. The decision reads what the observer holds: the
    /// ledger of delivered packets (<see cref="TrackBuff"/>). An add of a slot the observer holds nothing in is left off with the remove that follows it:
    /// the client would create and destroy one copy in one frame. A remove of a slot the observer holds nothing in is a no-op, so it is left off with
    /// its add. A remove that follows an add of the name the observer holds is left off when that add renews the copy (<see cref="IsHeldRenewal"/>):
    /// the observer gets one renewal (0x32 or 0x1F), not a remove and an add. The second half of a remove-then-add pair always goes out. Both halves are
    /// judged against the same ledger state for the slot: nothing between them touches it, and both are routed to the observer or neither is.
    /// </summary>
    private bool CoalescesBuffPair(PacketReplicationMessage message, (PacketReplicationMessage Partner, bool First) pair,
        IReadOnlySet<uint> routeVisible, ReplicationObserver observer)
    {
        if (!CanRoute(pair.Partner.Packet, observer.View, routeVisible, pair.Partner.Global)) return false;
        var bytes = message.Packet.Plaintext.Span;
        var holds = observer.View.Buffs.TryGetValue(
            (BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), bytes[5]), out var copy);
        if (bytes[0] == SpellPackets.BuffAddOpcode)
            return pair.First && !holds;
        if (!pair.First) return !holds;
        return !holds || (copy.Hash == BuffNameHashOf(bytes) &&
            IsHeldRenewal(observer.View, pair.Partner.Packet.Plaintext.Span, out _, out _));
    }

    /// <summary>Whether the buff in this add's slot paints an activation particle of its own on the client.</summary>
    private bool ClientActivationFx(BuffAdd add) =>
        World.Get(add.TargetNetId)?.Buffs.FirstOrDefault(buff => buff.Slot == add.Slot) is { } live &&
        live.Payload.ClientActivationFx;

    private static bool IsRefreshRebuilt(PlaintextPacket packet)
    {
        var bytes = packet.Plaintext.Span;
        if (bytes.IsEmpty)
            return false;

        return bytes[0] is
            0x3C or 0x80 or 0x66 or 0xC2 or 0xB5 or 0x54 or 0x3A or
            0xEE or 0xEF or 0x7B or 0x26 or 0x38;
    }

    private void SendCharacterData(ReplicationObserver observer, CharacterDataReplicationMessage model)
    {
        if (model.All)
        {
            // BBPopAllCharacterData sends its 5-byte 0x6A whatever the stack holds; the client clears the unit's whole stack.
            observer.View.CharacterData.RemoveWhere(held => held.Actor == model.Actor);
            Send(observer, model.Packet);
            return;
        }
        var key = (model.Actor, model.Change);
        if (model.SkinName is not null)
        {
            if (observer.View.CharacterData.Add(key)) Send(observer, model.Packet);
            return;
        }
        // Batch 256: every 0x6A runs both of the client's pop handlers (one clears the stack, the other erases the id), so a pop of one
        // held layer leaves the client with none of the unit's layers: the view forgets them all, and the repair pass of Flush pushes back, in id
        // order, the ones the ledger still holds (a buried layer under a popped top, a live layer beside a stale one).
        // Reconstruction: the retail wire of a pop is this 9-byte packet; whether the retail server pushed the remaining layers again is server-side.
        if (!observer.View.CharacterData.Contains(key)) return;
        observer.View.CharacterData.RemoveWhere(held => held.Actor == model.Actor);
        Send(observer, model.Packet);
    }

    private static bool IsCharacterFadePush(PlaintextPacket packet,
        out (uint Actor, short FadeId) key)
    {
        var bytes = packet.Plaintext.Span;
        if (bytes.Length >= 7 && bytes[0] == 0xBA)
        {
            key = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]),
                BinaryPrimitives.ReadInt16LittleEndian(bytes[5..]));
            return true;
        }
        key = default;
        return false;
    }

    private static bool IsCharacterFadePop(PlaintextPacket packet,
        out (uint Actor, short FadeId) key)
    {
        var bytes = packet.Plaintext.Span;
        if (bytes.Length >= 7 && bytes[0] == 0x34)
        {
            key = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]),
                BinaryPrimitives.ReadInt16LittleEndian(bytes[5..]));
            return true;
        }
        key = default;
        return false;
    }

    // Every delivered 0xBF/0x1F/0x32/0x7F passes here, so the ledger is exactly what
    // this client holds: live adds, stack updates, renewals, snapshot adds and fog-routed removals.
    // 0xBF: slot @5, count @7, name hash @9, duration float @17.
    // 0x1F: slot @5, count @6, duration float @7 (it keeps the held buff's name hash).
    // 0x32: slot @5, duration float @10 (it keeps the held buff's name hash and count).
    private void TrackBuff(ReplicationView view, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 21 && bytes[0] == SpellPackets.BuffAddOpcode)
            view.Buffs[(BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), bytes[5])] =
                (BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]), bytes[7],
                    SimulationTime() + BinaryPrimitives.ReadSingleLittleEndian(bytes[17..]));
        else if (bytes.Length >= 11 && bytes[0] == SpellPackets.BuffUpdateCountOpcode)
        {
            var key = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), bytes[5]);
            if (view.Buffs.TryGetValue(key, out var held))
                view.Buffs[key] = (held.Hash, bytes[6],
                    SimulationTime() + BinaryPrimitives.ReadSingleLittleEndian(bytes[7..]));
        }
        else if (bytes.Length >= 18 && bytes[0] == 0x32)
        {
            var key = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), bytes[5]);
            if (view.Buffs.TryGetValue(key, out var held))
                view.Buffs[key] = (held.Hash, held.Count,
                    SimulationTime() + BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]));
        }
        else if (bytes.Length >= 6 && bytes[0] == 0x7F)
            view.Buffs.Remove((BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), bytes[5]));
    }
}
