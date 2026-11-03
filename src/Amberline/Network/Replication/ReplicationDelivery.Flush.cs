using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;
using System.Buffers.Binary;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    // Reconstruction (batch 323): a cast's own hooks (CharOnSpellCast, BuffOnSpellCast, SpellSystem.BeginChampionCast) run at its start,
    // before the module queues the 0xBD, and a hook may rename the slot being cast: CharScriptNocturne's CharOnSpellCast swaps NocturneParanoia
    // for NocturneParanoia2 (BBSetSpell), then restores the slot's cooldown (BBSetSlotSpellCooldownTimeVer2). The stock client starts the slot's
    // cooldown from a cast only while the slot still holds the cast's spell (AdjustVarsAfterSpellCast, batch 134), and its 0x35
    // handler (the change-spell routine, then SetSpellData) resets the slot's cooldown, so the rename and every slot packet of
    // that unit and slot queued after it (0x35, 0x1A, 0x8A, 0x37) follow the cast, in their own order. When the retail server raised these hooks
    // against its cast packet is server-only; the client fixes only this order.
    private void AnnounceCastsBeforeTheirSlotRenames()
    {
        for (var index = 0; index < _pending.Count; index++)
        {
            if (_pending[index] is not SpellCastReplicationMessage { CasterNetId: var caster, Info: var info }) continue;
            // The earliest rename of the cast's slot to another spell since the caster's previous cast in this flush.
            var rename = -1;
            for (var earlier = index - 1; earlier >= 0; earlier--)
            {
                if (_pending[earlier] is SpellCastReplicationMessage previous && previous.CasterNetId == caster) break;
                if (_pending[earlier] is PacketReplicationMessage packet &&
                    SpellPackets.TryReadChangeSlotSpellName(packet.Packet.Plaintext.Span, out var update) &&
                    update.NetId == caster && update.Slot == info.Slot && !update.IsSummoner &&
                    ProtocolEncoding.HashString(update.SpellName) != info.SpellHash)
                    rename = earlier;
            }
            if (rename < 0) continue;
            var moved = new List<ReplicationMessage>();
            for (var at = index - 1; at >= rename; at--)
            {
                if (!IsSlotPacket(_pending[at], caster, info.Slot)) continue;
                moved.Insert(0, _pending[at]);
                _pending.RemoveAt(at);
                index--;
            }
            _pending.InsertRange(index + 1, moved);
            index += moved.Count;
        }
    }

    // Batch 405 (Reconstruction): the stock client reads a toggle slot's spell when a buff's add activates (the spellbook's activation handler,
    // the script's SpellToggleSlot): the slot's glow comes on only when the spell in that slot has IsToggleSpell
    // set at that moment, and a later swap of the slot is not read again. Xerath W's add (XerathLocusOfPower, SpellToggleSlot 2) was queued before
    // the swap to XerathLocusOfPowerToggle that its own activation makes, and that swap follows the cast (AnnounceCastsBeforeTheirSlotRenames), so
    // the client read the base spell and the glow never lit. The carrier's add moves behind the last rename of its slot that this flush queued
    // after it. Retail's server order of these packets is not in the shipped files: the client's reads fix only that the swap comes first.
    private void MoveToggleCarriersBehindTheirSlotRenames()
    {
        for (var index = 0; index < _pending.Count; index++)
        {
            if (_pending[index] is not PacketReplicationMessage { ToggleCarrier: { } carrier } add) continue;
            var last = -1;
            for (var later = index + 1; later < _pending.Count; later++)
                if (_pending[later] is PacketReplicationMessage packet &&
                    SpellPackets.TryReadChangeSlotSpellName(packet.Packet.Plaintext.Span, out var update) &&
                    update.NetId == carrier.UnitNetId && update.Slot == carrier.Slot && !update.IsSummoner)
                    last = later;
            if (last < 0) continue;
            // After the removal the rename sits at last - 1, so the insert lands directly behind it.
            _pending.RemoveAt(index);
            _pending.Insert(last, add);
            index--;
        }
    }

    // The slot packets share one layout: opcode, the unit's net ID, the slot byte (bit 0x80 is the summoner book).
    private static bool IsSlotPacket(ReplicationMessage message, uint unit, byte slot)
    {
        if (message is not PacketReplicationMessage packet) return false;
        var bytes = packet.Packet.Plaintext.Span;
        return bytes.Length >= 6 && bytes[0] is 0x35 or 0x1A or 0x8A or 0x37 &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == unit && bytes[5] == slot;
    }

    private void SendAll(ReplicationObserver observer, IReadOnlyList<PlaintextPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        foreach (var packet in packets)
            Send(observer, packet);
    }

    // First-sight and reveal stats are built from the unit's state at flush time with fresh
    // sync ids, so they supersede every 0xCC queued for that unit earlier in the same tick.
    // Reconstruction: dropping the superseded copy is a bandwidth saving. Whether the retail server
    // sent such copies is unknown; the client discards them, so only the packet count differs.
    private void SendFreshReplication(ReplicationObserver observer, IReadOnlyList<PlaintextPacket> packets)
    {
        SendAll(observer, packets);
        Span<uint> masks = stackalloc uint[5];
        foreach (var packet in packets)
        {
            if (!CombatPackets.TryReadReplicationBlock(packet.Plaintext.Span, out var sync, out var unit, masks))
                continue;
            if (!_freshReplication.TryGetValue((observer, unit), out var ledger))
                _freshReplication[(observer, unit)] = ledger = (new uint[5], new uint[5]);
            for (var map = 0; map < 5; map++)
                if (masks[map] != 0 && sync > ledger.Sync[map])
                {
                    ledger.Sync[map] = sync;
                    ledger.Applied[map] |= masks[map];
                }
        }
    }

    // The same test as PacketStreamChecker's "stale duplicate": every channel the packet sets is
    // already stored under a higher sync id and the packet carries no field the client lacks.
    // Equal or zero sync ids and unseen fields are still sent, so the checker keeps reporting them.
    private bool IsSupersededByFreshReplication(PlaintextPacket packet, ReplicationObserver observer)
    {
        if (_freshReplication.Count == 0) return false;
        Span<uint> masks = stackalloc uint[5];
        if (!CombatPackets.TryReadReplicationBlock(packet.Plaintext.Span, out var sync, out var unit, masks) ||
            sync == 0 || !_freshReplication.TryGetValue((observer, unit), out var ledger))
            return false;
        var any = false;
        for (var map = 0; map < 5; map++)
        {
            if (masks[map] == 0) continue;
            any = true;
            if (sync >= ledger.Sync[map] || (masks[map] & ~ledger.Applied[map]) != 0) return false;
        }
        return any;
    }

    private void Send(ReplicationObserver observer, PlaintextPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        // Refresh snapshots and queued gameplay effects share the native fade
        // stack. Deduplicate at their common send boundary, after routing.
        if (IsCharacterFadePush(packet, out var key) && observer.View.CharacterFades.Contains(key)) return;
        if (IsRepeatedSpellTooltips(observer, packet.Plaintext.Span)) return;
        packet = observer.View.ForClient(packet);
        observer.Transport.Send(packet);
        if (IsCharacterFadePush(packet, out key)) observer.View.CharacterFades.Add(key);
        else if (IsCharacterFadePop(packet, out key)) observer.View.CharacterFades.Remove(key);
        else TrackBuff(observer.View, packet.Plaintext.Span);
    }

    // Batch 554: every hero stat push (each buff add or remove on the hero, a level-up, the one-second block: HeroStatsService.Push,
    // ProtocolCombatTransport.SendHeroStats) carries the hero's whole spellbook tooltip table, so one Riven Q sent the same 75-byte 0x83 seven
    // times in one flush (S-riven seed 1, tick 1044). The client's 0x83 handler (the case 0x83 of its packet handler) stores a
    // spellbook entry's floats in the slot's SpellDataInst (the float fields and the flag byte). SetSpellData (which
    // the slot rename 0x35 reaches through the change-spell routine, and a character push through the spellbook's init) and the record reset
    // routine leave those fields alone. So a spellbook 0x83 byte-identical to the last one this observer got for the unit in this flush
    // changes nothing on the client and is not sent. Another spellbook 0x83 for the unit, an item packet of the hero (an item slot's record) or a
    // character push or pop (0x9F, 0x6A: the table keeps following a rebuilt book, as RefreshSpellbookStats orders it) in between starts over.
    // Buff selectors (below 60, the BuffManager's) are never dropped. Reconstruction: retail's 0x83 cadence is not in the shipped files; the
    // client ends the flush with the same values.
    private bool IsRepeatedSpellTooltips(ReplicationObserver observer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 5 && bytes[0] is 0x0B or 0x44 or 0x56 or 0x72 or 0xA7 or 0x9F or 0x6A)
        {
            // ItemsPackets (remove, swap, set, buy, use) and the character data push and pops name the unit at byte 1.
            _spellTooltipsSent.Remove((observer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])));
            return false;
        }
        if (bytes.Length < 24 || bytes[0] != 0x83 || bytes.Length != 7 + 17 * BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..])) return false;
        var unit = BinaryPrimitives.ReadUInt32LittleEndian(bytes[7..]);
        var table = true;
        for (var entry = 7; entry < bytes.Length; entry += 17)
            table &= BinaryPrimitives.ReadUInt32LittleEndian(bytes[entry..]) == unit && bytes[entry + 4] >= SpellPackets.MaxBuffTooltipSlots;
        if (!table)
        {
            for (var entry = 7; entry < bytes.Length; entry += 17)
                if (bytes[entry + 4] >= SpellPackets.MaxBuffTooltipSlots)
                    _spellTooltipsSent.Remove((observer, BinaryPrimitives.ReadUInt32LittleEndian(bytes[entry..])));
            return false;
        }
        if (_spellTooltipsSent.TryGetValue((observer, unit), out var sent) && bytes.SequenceEqual(sent)) return true;
        _spellTooltipsSent[(observer, unit)] = bytes.ToArray();
        return false;
    }
}
