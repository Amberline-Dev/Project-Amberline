using System.Buffers.Binary;
using System.Numerics;
using Amberline.Network.Protocol;

namespace Amberline.Network.Replication;

/// <summary>
/// What one stock client's replication store holds, kept per connection so a 0xCC block can carry only the fields that changed.
/// <para>
/// The client keeps, per unit and per registry (CLIENT_ONLY, LOCAL1, LOCAL2, MAP, ONVISIBLE), the highest sync id it applied
/// and the value of every field a block wrote; a block is applied only if the stored sync id is lower than the incoming one.
/// The tables are in docs/network/patch126-replication-tables.md. A field whose last sent value equals the new one therefore needs no
/// bytes, which is what the retail server's dirty-bit replication amounts to. This store mirrors that state from the packets
/// that really reach the client, filters the next block against it, and forgets a unit when the client creates it anew (a fresh
/// native object starts with zero sync ids and zero values) and everything on a new connection (<see cref="Clear"/>).
/// </para>
/// </summary>
public sealed class ClientReplicationStore
{
    private const byte ReplicationOpcode = 0xCC;
    private const int Registries = 5;
    private const int HeaderLength = 11; // opcode, u32 0, u32 sync id, u8 entry count, then the first entry's setMaps byte

    private readonly Dictionary<uint, UnitStore> _units = new();
    // Units this client created as a ServerOnly character: their client object has no replication manager.
    private readonly HashSet<uint> _withoutManager = new();
    // Units this client created as a character whose bar is hidden by its health (CombatPackets.HidesBarByHealth): see WithoutHealth.
    private readonly HashSet<uint> _healthHidden = new();
    private readonly Func<string, bool> _serverOnlyCharacter;

    /// <param name="serverOnlyCharacter">Whether a character (the skin a creation packet names) is ServerOnly; the shipped
    /// CharacterData by default (<see cref="Amberline.Data.ChampionData.ServerOnly"/>).</param>
    public ClientReplicationStore(Func<string, bool>? serverOnlyCharacter = null) =>
        _serverOnlyCharacter = serverOnlyCharacter ?? (skin => Amberline.Data.GameData.Default.OptionalChampion(skin)?.ServerOnly == true);

    /// <summary>
    /// 0xCC blocks for a unit whose client object has no replication manager, not sent. The client creates a minion (0x80 SpawnMinion,
    /// 0x66 CreateNeutral) through the minion create call (the skin is the character), which runs SetupReplicationInfo
    /// only when the character is not ServerOnly; every block naming the unit then asserts that the unit has a replication manager
    /// (the replication manager's receive handler). The 8 asserts of the native Dominion runs were the two OdinSpeedShrines' four spawn blocks each.
    /// Reconstruction: the code is shared with the server (the shared client-server code), so retail's server had no replication for these units either.
    /// </summary>
    public long UnmanagedBlocks { get; private set; }

    /// <summary>Whole 0xCC packets that carried nothing new and were not sent.</summary>
    public long DroppedPackets { get; private set; }

    /// <summary>Bytes those packets and the unchanged fields of the packets that were sent would have taken.</summary>
    public long SavedBytes { get; private set; }

    /// <summary>Fields left out of the packets that were sent.</summary>
    public long OmittedFields { get; private set; }

    /// <summary>Registry blocks whose sync id was not above the stream's: the client would drop them, so they are not sent.</summary>
    public long StaleBlocks { get; private set; }

    public int Units => _units.Count;

    /// <summary>A new connection: its client starts with an empty store.</summary>
    public void Clear()
    {
        _units.Clear();
        _withoutManager.Clear();
        _healthHidden.Clear();
    }

    /// <summary>The client created this unit anew (or lost its store): the next block must carry every field.</summary>
    public void Forget(uint netId) => _units.Remove(netId);

    /// <summary>
    /// The client changed this unit's replicated values on its own, so the values it holds are no longer known. The sync ids stay
    /// (the client keeps them), only the "this field is already there" knowledge goes.
    /// </summary>
    public void ForgetValues(uint netId)
    {
        if (!_units.TryGetValue(netId, out var unit)) return;
        Array.Clear(unit.Applied);
    }

    /// <summary>One field of a unit the client wrote itself: the registry (0..4) and the bits in it.</summary>
    private readonly record struct Field(int Registry, uint Bits);

    // Fields the client's own packet handlers write, so the value it holds may differ from the last one sent:
    // a cast packet (0xBD) deducts its mana cost from the caster's mMP (the spell helper's cost routine), and
    // EnterLocalVisibility (0xB5) writes mMaxHP and mHP. mMP is MAP bit 1 of a hero and LOCAL1 bit 1 of a
    // minion; mHP and mMaxHP are MAP bits 0 and 2 of a hero, LOCAL1 bits 0 and 2 of a minion and LOCAL1 bits 8 and 9 of a turret.
    private static readonly Field[] CastWrites = [new(3, 0b10), new(1, 0b10)];
    private static readonly Field[] RevealWrites = [new(3, 0b101), new(1, 0b1_0000_0101)];

    /// <summary>
    /// Notes everything one packet changes in the client's store and returns the packet to send: the packet itself, a 0xCC block
    /// reduced to its changed fields, or null when a 0xCC carries nothing the client lacks. <paramref name="ownerNetId"/> is the
    /// hero this client plays: the CLIENT_ONLY registry (experience, gold, spell permission bits and mana costs, the player's own
    /// HUD data) is for its owner only.
    /// </summary>
    public PlaintextPacket? Process(PlaintextPacket packet, uint ownerNetId = 0)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var bytes = packet.Plaintext.Span;
        if (bytes.Length == 0) return packet;
        if (bytes[0] == ReplicationOpcode)
        {
            if (bytes.Length >= HeaderLength + 4 && bytes[9] == 1 &&
                _withoutManager.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..])))
            {
                UnmanagedBlocks++;
                return null;
            }
            if (_healthHidden.Count > 0)
            {
                packet = WithoutHealth(packet);
                bytes = packet.Plaintext.Span;
            }
            // The store assumes the client applies every block, in order. An unreliable block may be lost: pass it through and
            // forget the unit, so its next reliable block carries every field.
            if (packet.Reliability != LeaguePacketReliability.Reliable)
            {
                ForgetFrame(bytes);
                return packet;
            }
            return Filter(packet, bytes, ownerNetId);
        }
        if (bytes[0] == 0xBD && bytes.Length >= 5) ForgetFields(BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), CastWrites);
        else if (bytes[0] is 0xB5 or 0xA6 or 0x61 or 0x4D && bytes.Length >= 5)
        {
            var netId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
            ForgetFields(netId, RevealWrites);
            // EnterLocalVisibility: opcode, net id, mMaxHP, mHP (VisionPackets.EnterLocalVisibility).
            if (bytes[0] == 0xB5 && bytes.Length >= 13 && _healthHidden.Contains(netId)) return Zeroed(packet, [9]);
        }
        else if (PacketCatalog.Default.TryGet(bytes[0], packet.Channel, out var spec) && spec.Creations.Count > 0)
        {
            Span<uint> ids = stackalloc uint[8];
            var count = spec.ReadCreations(bytes, ids);
            for (var index = 0; index < count; index++)
            {
                Forget(ids[index]);
                _withoutManager.Remove(ids[index]);
                _healthHidden.Remove(ids[index]);
            }
            var skin = MinionSkinOffset(bytes[0]);
            if (count == 1 && skin > 0 && bytes.Length >= skin + 64)
            {
                var character = CharacterName(bytes.Slice(skin, 64));
                if (_serverOnlyCharacter(character)) _withoutManager.Add(ids[0]);
                if (CombatPackets.HidesBarByHealth(character)) _healthHidden.Add(ids[0]);
            }
        }
        return packet;
    }

    // Where a minion creation names the character the minion create call loads (its EAX argument): 0x80 at offset 0x67 (case 0x80),
    // 0x66 at offset 0x6e (case 0x66); 0 for any other packet.
    private static int MinionSkinOffset(byte opcode) => opcode switch { 0x80 => 0x67, 0x66 => 0x6E, _ => 0 };

    private static string CharacterName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    // A minion's LOCAL1 bits that carry its health: mHP (0) and mHPRegenRate (17), CombatPackets.MinionStats.
    private static readonly int[] HealthBits = [0, 17];

    /// <summary>
    /// Batch 516: the one rule for a unit whose bar is hidden by its health (<see cref="CombatPackets.HidesBarByHealth(string?)"/>, the capture
    /// point guardian; batch 503: the stock client's minion bar draws nothing below 0.01 health). Every 0xCC this client is sent for it carries
    /// mHP 0 and mHPRegenRate 0, whichever builder wrote the block, and its 0xB5 carries mHP 0 (above). The pool (mMP, mMaxMP) and mMaxHP go
    /// out as they are. Before, each builder wrote 0 itself and one did not: the reveal's MinionCurrentStats (ProductionMatchComposition
    /// BuildStats) sent the real 50000 right after every guardian's creation. Batch 512 stopped the full blocks that wrote 0 again 0.8 s later.
    /// </summary>
    private PlaintextPacket WithoutHealth(PlaintextPacket packet)
    {
        var bytes = packet.Plaintext.Span;
        // A one-unit block (the server builds no other) that writes LOCAL1, registry 1.
        if (bytes.Length < HeaderLength + 4 || bytes[9] != 1 || (bytes[10] & 0xE0) != 0 || (bytes[10] & 0x02) == 0 ||
            !_healthHidden.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]))) return packet;
        var at = 15;
        if ((bytes[10] & 0x01) != 0)
        {
            if (at + 4 > bytes.Length) return packet;
            at += 4 + 4 * BitOperations.PopCount(BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]));
        }
        if (at + 4 > bytes.Length) return packet;
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        Span<int> offsets = stackalloc int[HealthBits.Length];
        var count = 0;
        foreach (var bit in HealthBits)
            if ((mask & (1u << bit)) != 0) offsets[count++] = at + 4 + 4 * BitOperations.PopCount(mask & ((1u << bit) - 1));
        return Zeroed(packet, offsets[..count]);
    }

    /// <summary>The packet with the 4-byte values at <paramref name="offsets"/> set to 0; the packet itself when they already are.</summary>
    private static PlaintextPacket Zeroed(PlaintextPacket packet, ReadOnlySpan<int> offsets)
    {
        var bytes = packet.Plaintext.Span;
        byte[]? output = null;
        foreach (var offset in offsets)
        {
            if (offset + 4 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) == 0) continue;
            (output ??= bytes.ToArray()).AsSpan(offset, 4).Clear();
        }
        return output is null ? packet : ProtocolEncoding.Packet(packet.Channel, output, packet.Encrypt, packet.Reliability, [.. packet.EntityReferences]);
    }

    private void ForgetFields(uint netId, Field[] fields)
    {
        if (!_units.TryGetValue(netId, out var unit)) return;
        foreach (var field in fields) unit.Applied[field.Registry] &= ~field.Bits;
    }

    private PlaintextPacket? Filter(PlaintextPacket packet, ReadOnlySpan<byte> bytes, uint ownerNetId)
    {
        if (bytes.Length < HeaderLength + 4 || (bytes[10] & 0xE0) != 0 || bytes[9] != 1)
        {
            // A frame with several units (the server builds none) or one the parser does not know: the client applies it, and
            // this store cannot say what it changed, so every unit in it becomes unknown.
            ForgetFrame(bytes);
            return packet;
        }

        var sync = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
        var setMaps = bytes[10];
        var netId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]);
        Span<int> maskAt = stackalloc int[Registries];
        Span<uint> mask = stackalloc uint[Registries];
        var offset = 15;
        for (var registry = 0; registry < Registries; registry++)
        {
            if ((setMaps & (1 << registry)) == 0) continue;
            if (offset + 4 > bytes.Length)
            {
                _units.Remove(netId);
                return packet;
            }
            maskAt[registry] = offset;
            mask[registry] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            offset += 4 + 4 * BitOperations.PopCount(mask[registry]);
        }
        if (offset != bytes.Length)
        {
            _units.Remove(netId);
            return packet;
        }

        if (!_units.TryGetValue(netId, out var unit)) _units[netId] = unit = new UnitStore();
        Span<uint> keep = stackalloc uint[Registries];
        var changed = false;
        var anyKept = false;
        for (var registry = 0; registry < Registries; registry++)
        {
            if (mask[registry] == 0) continue;
            if (registry == 0 && netId != ownerNetId)
            {
                // Experience, gold, permission bits and mana costs are the owner's HUD data; another client is not sent them.
                changed = true;
                continue;
            }
            // A block whose sync id is not above the highest one the stream carried is a no-op for the client (it applies a
            // block only if the stored id is lower). It is not sent: the client's own stored id may be lower than the stream's,
            // because blocks that carried nothing new were not sent, so it could apply an older value the stream had superseded.
            if (sync <= unit.Sync[registry])
            {
                StaleBlocks++;
                changed = true;
                continue;
            }
            var values = bytes[(maskAt[registry] + 4)..];
            var slot = 0;
            for (var bit = 0; bit < 32; bit++)
            {
                if ((mask[registry] & (1u << bit)) == 0) continue;
                var value = BinaryPrimitives.ReadUInt32LittleEndian(values[(4 * slot++)..]);
                if ((unit.Applied[registry] & (1u << bit)) == 0 || unit.Values(registry)[bit] != value)
                {
                    keep[registry] |= 1u << bit;
                    unit.Values(registry)[bit] = value;
                }
            }
            unit.Applied[registry] |= mask[registry];
            unit.Sync[registry] = sync;
            if (keep[registry] != mask[registry]) changed = true;
            if (keep[registry] != 0) anyKept = true;
        }

        if (!anyKept)
        {
            DroppedPackets++;
            SavedBytes += bytes.Length;
            return null;
        }

        if (!changed) return packet;

        var length = 15;
        var kept = 0;
        byte newSetMaps = 0;
        for (var registry = 0; registry < Registries; registry++)
        {
            if (keep[registry] == 0) continue;
            newSetMaps |= (byte)(1 << registry);
            length += 4 + 4 * BitOperations.PopCount(keep[registry]);
            kept += BitOperations.PopCount(keep[registry]);
        }
        var output = new byte[length];
        bytes[..HeaderLength].CopyTo(output);
        output[10] = newSetMaps;
        bytes.Slice(11, 4).CopyTo(output.AsSpan(11));
        var at = 15;
        for (var registry = 0; registry < Registries; registry++)
        {
            if (keep[registry] == 0) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), keep[registry]);
            at += 4;
            var values = bytes[(maskAt[registry] + 4)..];
            var slot = 0;
            for (var bit = 0; bit < 32; bit++)
            {
                if ((mask[registry] & (1u << bit)) == 0) continue;
                var value = BinaryPrimitives.ReadUInt32LittleEndian(values[(4 * slot++)..]);
                if ((keep[registry] & (1u << bit)) == 0) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
                at += 4;
            }
        }
        OmittedFields += BitOperations.PopCount(mask[0]) + BitOperations.PopCount(mask[1]) + BitOperations.PopCount(mask[2]) +
            BitOperations.PopCount(mask[3]) + BitOperations.PopCount(mask[4]) - kept;
        SavedBytes += bytes.Length - length;
        return ProtocolEncoding.Packet(packet.Channel, output, packet.Encrypt, packet.Reliability, [.. packet.EntityReferences]);
    }

    /// <summary>Forgets every unit a frame names; a frame this store cannot parse names none it can find.</summary>
    private void ForgetFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10) return;
        var count = bytes[9];
        var offset = 10;
        for (var entry = 0; entry < count; entry++)
        {
            if (offset + 5 > bytes.Length) return;
            var setMaps = bytes[offset];
            _units.Remove(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 1)..]));
            offset += 5;
            if ((setMaps & 0xE0) != 0) return;
            for (var registry = 0; registry < Registries; registry++)
            {
                if ((setMaps & (1 << registry)) == 0) continue;
                if (offset + 4 > bytes.Length) return;
                offset += 4 + 4 * BitOperations.PopCount(BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
            }
        }
    }

    private sealed class UnitStore
    {
        public readonly uint[] Sync = new uint[Registries];
        public readonly uint[] Applied = new uint[Registries];
        private readonly uint[]?[] _values = new uint[]?[Registries];

        /// <summary>The values of one registry; a unit uses two or three of the five, so the arrays are made on first use.</summary>
        public uint[] Values(int registry) => _values[registry] ??= new uint[32];
    }
}
