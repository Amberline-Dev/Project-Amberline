using System.Buffers.Binary;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;

namespace Amberline.Tests.Network;

/// <summary>
/// The delta filter of one client's replication store (<see cref="ClientReplicationStore"/>). Its contract is equivalence: a client
/// that applies the filtered stream holds the same values as a client that applies every block, under the client's sync gate.
/// </summary>
public static class ClientReplicationStoreChecks
{
    private const uint Unit = 0x40000001;

    [Check("replication.delta-equals-full-stream")]
    public static void FilteredStreamLeavesTheClientWithTheSameValues()
    {
        var random = new Random(12345);
        long blocks = 0, whole = 0;
        for (var trial = 0; trial < 120; trial++)
        {
            var store = new ClientReplicationStore();
            var full = new NaiveClient();
            var delta = new NaiveClient();
            var syncs = new Dictionary<uint, uint>();
            var values = new Dictionary<(uint, int, int), uint>();
            for (var step = 0; step < 300; step++)
            {
                var unit = (uint)(Unit + random.Next(4));
                if (random.Next(40) == 0)
                {
                    // The client created the unit anew: zero sync ids, zero values.
                    store.Forget(unit);
                    full.Forget(unit);
                    delta.Forget(unit);
                    syncs.Remove(unit);
                }
                syncs.TryGetValue(unit, out var sync);
                // One block in twelve repeats an older sync id: the lower id of a packet that a fresher one superseded.
                var stale = random.Next(12) == 0 && sync > 2;
                var thisSync = stale ? sync - (uint)random.Next(1, 3) : ++sync;
                if (!stale) syncs[unit] = sync;
                var registries = new List<(int Registry, uint Mask, uint[] Values)>();
                for (var registry = 0; registry < 5; registry++)
                {
                    if (random.Next(3) != 0) continue;
                    uint mask = 0;
                    var bits = new List<int>();
                    for (var bit = 0; bit < 12; bit++)
                        if (random.Next(3) == 0) { mask |= 1u << bit; bits.Add(bit); }
                    if (mask == 0) continue;
                    var blockValues = new uint[bits.Count];
                    for (var index = 0; index < bits.Count; index++)
                    {
                        var key = (unit, registry, bits[index]);
                        if (!values.ContainsKey(key) || random.Next(4) == 0) values[key] = (uint)random.Next(5);
                        blockValues[index] = values[key];
                    }
                    registries.Add((registry, mask, blockValues));
                }
                if (registries.Count == 0) continue;
                var packet = Block(thisSync, unit, registries);
                blocks++;
                full.Apply(packet.Plaintext.Span);
                var sent = store.Process(packet, unit); // the unit's owner: CLIENT_ONLY is kept
                if (sent is null) whole++;
                else delta.Apply(sent.Plaintext.Span);
                TestAssert.True(full.HoldsTheValuesOf(delta), $"trial {trial} step {step}: the filtered stream diverged");
            }
        }
        TestAssert.True(whole > blocks / 20, "a stream that repeats values drops whole blocks");
    }

    [Check("replication.delta-sends-changed-fields-only")]
    public static void OnlyChangedFieldsAreSent()
    {
        var store = new ClientReplicationStore();
        var first = store.Process(Block(1, Unit, [(3, 0b111u, [10u, 20u, 30u])]));
        TestAssert.Equal(15 + 4 + 12, first!.Plaintext.Length, "the first block of a unit is sent whole");

        var second = store.Process(Block(2, Unit, [(3, 0b111u, [10u, 21u, 30u])]));
        var bytes = second!.Plaintext.Span;
        TestAssert.Equal(15 + 4 + 4, bytes.Length, "one changed field");
        TestAssert.Equal(0b010u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]));
        TestAssert.Equal(21u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[19..]));
        TestAssert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]), "the sync id stays the block's");

        TestAssert.True(store.Process(Block(3, Unit, [(3, 0b111u, [10u, 21u, 30u])])) is null, "nothing new, nothing sent");
        TestAssert.True(store.DroppedPackets == 1 && store.SavedBytes > 0);
    }

    [Check("replication.delta-skips-blocks-the-client-would-drop")]
    public static void StaleSyncIdsAreNotSent()
    {
        var store = new ClientReplicationStore();
        TestAssert.True(store.Process(Block(5, Unit, [(3, 0b1u, [100u])])) is not null);
        // The id the stream already passed: the client would ignore the block, and the filtered stream may leave the client's
        // own stored id lower, so a stale value must not reach it.
        TestAssert.True(store.Process(Block(5, Unit, [(3, 0b1u, [90u])])) is null);
        TestAssert.True(store.Process(Block(4, Unit, [(3, 0b1u, [80u])])) is null);
        TestAssert.True(store.Process(Block(6, Unit, [(3, 0b1u, [100u])])) is null, "back to the held value: nothing to send");
        var next = store.Process(Block(7, Unit, [(3, 0b1u, [70u])]));
        TestAssert.Equal(70u, BinaryPrimitives.ReadUInt32LittleEndian(next!.Plaintext.Span[19..]));
        TestAssert.Equal(2L, store.StaleBlocks);
    }

    [Check("replication.delta-resends-a-unit-the-client-creates-anew")]
    public static void CreationAndReconnectEmptyTheStore()
    {
        var store = new ClientReplicationStore();
        TestAssert.True(store.Process(Block(1, Unit, [(3, 0b1u, [5u])])) is not null);
        TestAssert.True(store.Process(Block(2, Unit, [(3, 0b1u, [5u])])) is null);

        // A creation packet (or a new connection) gives the client a fresh native object: zero sync ids, zero values.
        store.Forget(Unit);
        var again = store.Process(Block(1, Unit, [(3, 0b1u, [5u])]));
        TestAssert.True(again is not null, "after Forget the same value and a low sync id are sent again");

        store.Clear();
        TestAssert.True(store.Process(Block(3, Unit, [(3, 0b1u, [5u])])) is not null, "a new connection starts empty");
        TestAssert.Equal(1, store.Units);
    }

    [Check("replication.delta-forgets-what-the-client-writes-itself")]
    public static void ClientWritesAndUnreliableBlocksForceAResend()
    {
        var store = new ClientReplicationStore();
        TestAssert.True(store.Process(Block(1, Unit, [(3, 0b1010u, [100u, 50u])])) is not null);
        TestAssert.True(store.Process(Block(2, Unit, [(3, 0b1010u, [100u, 50u])])) is null, "unchanged");

        // A cast packet makes the client deduct the cost from the caster's mana (MAP bit 1 of a hero).
        var cast = ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel,
            [0xBD, 0x01, 0x00, 0x00, 0x40, 0, 0, 0, 0], true, LeaguePacketReliability.Reliable);
        TestAssert.True(ReferenceEquals(cast, store.Process(cast)), "a cast passes through unchanged");
        var after = store.Process(Block(3, Unit, [(3, 0b1010u, [100u, 50u])]));
        TestAssert.Equal(0b0010u, BinaryPrimitives.ReadUInt32LittleEndian(after!.Plaintext.Span[15..]),
            "only the mana the client changed is sent again");

        // An unreliable block may be lost: it passes through and the unit is forgotten.
        var lossy = ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel,
            Block(4, Unit, [(3, 0b1010u, [100u, 50u])]).Plaintext.ToArray(), true, LeaguePacketReliability.Unreliable);
        TestAssert.True(ReferenceEquals(lossy, store.Process(lossy)));
        TestAssert.True(store.Process(Block(5, Unit, [(3, 0b1010u, [100u, 50u])])) is not null, "everything again after a possible loss");
    }

    // CLIENT_ONLY (experience, gold, permission bits, mana costs) is the owning player's HUD data.
    [Check("replication.delta-keeps-the-client-only-registry-for-its-owner")]
    public static void ClientOnlyRegistryGoesToTheOwnerOnly()
    {
        var owner = new ClientReplicationStore();
        var other = new ClientReplicationStore();
        var packet = Block(1, Unit, [(0, 0b11u, [7u, 8u]), (3, 0b1u, [100u])]);
        var mine = owner.Process(packet, Unit)!.Plaintext.Span;
        TestAssert.Equal(0b1001, (int)mine[10], "the owner gets CLIENT_ONLY and MAP");
        var theirs = other.Process(packet, Unit + 1)!.Plaintext.Span;
        TestAssert.Equal(0b1000, (int)theirs[10], "another client gets MAP only");
        TestAssert.True(other.Process(Block(2, Unit, [(0, 0b11u, [9u, 9u])]), Unit + 1) is null, "and no gold-only block at all");
    }

    /// <summary>
    /// Batch 464: the client makes a minion's replication manager only when its character is not ServerOnly (the minion's creation
    /// handler), and the CharacterData loader reads ServerOnly with the character's NeverRender as its default
    /// (the character loader's default). Every 0xCC naming such a unit asserts on the client's replication manager accessor: the 8 asserts
    /// of the native Dominion runs were the two OdinSpeedShrines' spawn blocks.
    /// </summary>
    [Check("replication.server-only-minion-gets-no-replication-blocks")]
    public static void ServerOnlyMinionGetsNoReplicationBlocks()
    {
        var store = new ClientReplicationStore();
        uint next = 0x40030011;
        uint Spawn(string character, uint? netId = null)
        {
            var id = netId ?? next++;
            store.Process(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(id, 0x40, new(6000, 6000), 100, character, character), 0.0));
            return id;
        }
        bool Sent(uint unit, uint sync) => store.Process(Block(sync, unit, [(1, 0b1u, [100u]), (3, 0b100u, [4u])])) is not null;

        // NeverRender 1 and no ServerOnly key: ServerOnly by the default.
        foreach (var character in new[] { "OdinSpeedShrine", "TestCube", "SpellBook1", "XerathArcaneBarrageLauncher" })
        {
            var unit = Spawn(character);
            TestAssert.False(Sent(unit, 2) || Sent(unit, 3), $"{character}: no block reaches the client");
        }
        // ServerOnly 0 set against NeverRender 1, and an ordinary rendered unit: replicated.
        foreach (var character in new[] { "TestCubeRender", "OdinTestCubeRender", "OdinOpeningBarrier", "OdinShieldRelic" })
            TestAssert.True(Sent(Spawn(character), 2), $"{character}: its block is sent");
        TestAssert.Equal(8L, store.UnmanagedBlocks, "the eight blocks of the four ServerOnly units");

        // The same net id created anew as a replicated character, or a new connection, has a replication manager again.
        TestAssert.True(Sent(Spawn("OdinShieldRelic", 0x40030011), 4), "a unit created anew from a replicated character");
        var shrine = Spawn("OdinSpeedShrine");
        store.Clear();
        TestAssert.True(Sent(shrine, 5), "a new connection: the store no longer holds the creation");
    }

    private static PlaintextPacket Block(uint sync, uint unit, IReadOnlyList<(int Registry, uint Mask, uint[] Values)> registries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0xCC);
        writer.Write(0u);
        writer.Write(sync);
        writer.Write((byte)1);
        byte setMaps = 0;
        foreach (var block in registries) setMaps |= (byte)(1 << block.Registry);
        writer.Write(setMaps);
        writer.Write(unit);
        foreach (var block in registries.OrderBy(block => block.Registry))
        {
            writer.Write(block.Mask);
            foreach (var value in block.Values) writer.Write(value);
        }
        writer.Flush();
        return ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, stream.ToArray(), true,
            LeaguePacketReliability.Reliable);
    }

    /// <summary>A client that applies every 0xCC block it receives under the sync gate.</summary>
    private sealed class NaiveClient
    {
        private readonly Dictionary<(uint Unit, int Registry), (uint Sync, Dictionary<int, uint> Values)> _store = new();

        public void Forget(uint unit)
        {
            foreach (var key in _store.Keys.Where(key => key.Unit == unit).ToArray()) _store.Remove(key);
        }

        public void Apply(ReadOnlySpan<byte> bytes)
        {
            var sync = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
            var setMaps = bytes[10];
            var unit = BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]);
            var offset = 15;
            for (var registry = 0; registry < 5; registry++)
            {
                if ((setMaps & (1 << registry)) == 0) continue;
                var mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                offset += 4;
                _store.TryGetValue((unit, registry), out var held);
                held.Values ??= new();
                var applies = sync > held.Sync;
                for (var bit = 0; bit < 32; bit++)
                {
                    if ((mask & (1u << bit)) == 0) continue;
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                    offset += 4;
                    if (applies) held.Values[bit] = value;
                }
                if (applies) held.Sync = sync;
                _store[(unit, registry)] = held;
            }
        }

        /// <summary>Same field values; the filtered client may hold a lower sync id, never a higher one.</summary>
        public bool HoldsTheValuesOf(NaiveClient other)
        {
            foreach (var key in _store.Keys.Union(other._store.Keys))
            {
                _store.TryGetValue(key, out var mine);
                other._store.TryGetValue(key, out var theirs);
                var mineValues = mine.Values ?? new();
                var theirValues = theirs.Values ?? new();
                foreach (var bit in mineValues.Keys.Union(theirValues.Keys))
                    if (mineValues.GetValueOrDefault(bit) != theirValues.GetValueOrDefault(bit)) return false;
                if (theirs.Sync > mine.Sync) return false;
            }
            return true;
        }
    }
}
