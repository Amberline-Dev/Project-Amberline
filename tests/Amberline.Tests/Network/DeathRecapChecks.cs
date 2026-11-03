using System.Buffers.Binary;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

public static class DeathRecapChecks
{
    [Check("death-recap.packet-layout")]
    public static void PacketLayoutUsesPatch126Event11Record()
    {
        var origin = DeathRecapOrigin.BasicAttack(0, DeathRecapSourceType.Hero);
        var record = new DeathRecapRecord(7.5, 2, 1, 42.0, 0.0, 0.0, origin);
        // playtest B4 #16, 27 Sept 2026: shipped Game.cfg 15 s
        var snapshot = new DeathRecapSnapshot(1, 2, 1, 15.0,
            DeathRecapSourceType.Hero, new[] { record });
        var packet = CombatPackets.DeathRecap(snapshot, snapshot.Records);
        var bytes = packet.Plaintext.Span;

        TestAssert.Equal(0x19 + 0x2F, bytes.Length);
        TestAssert.Equal((byte)0x24, bytes[0]);
        TestAssert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
        TestAssert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]));
        TestAssert.Equal(15.0f, BitConverter.ToSingle(bytes[9..]));
        TestAssert.Equal(0, bytes[0x0D] & 0xF0);
        TestAssert.Equal(47, BinaryPrimitives.ReadInt32LittleEndian(bytes[0x11..]));
        TestAssert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes[0x15..]));

        const int row = 0x19;
        TestAssert.Equal((byte)11, bytes[row + 6]);
        TestAssert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(row + 7)..]));
        TestAssert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(row + 11)..]));
        TestAssert.Near(42.0, BitConverter.ToSingle(bytes[(row + 15)..]), 1e-6);
        TestAssert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(row + 43)..]));
    }

    [Check("death-recap.history-window-cap")]
    public static void HistoryWindowAndCapAreBounded()
    {
        var history = new DeathRecapHistory();
        var origin = DeathRecapOrigin.BasicAttack(1, DeathRecapSourceType.Hero);
        for (var index = 0; index < 129; index++)
            history.Add(new DeathRecapRecord(100.0, 2, 1, 1.0, 0.0, 0.0, origin), 100.0);

        var capped = history.Snapshot(100.0);
        TestAssert.Equal(128, capped.Count);
        var window = new DeathRecapHistory();
        window.Add(new DeathRecapRecord(100.0, 2, 1, 1.0, 0.0, 0.0, origin), 100.0);
        window.Add(new DeathRecapRecord(111.0, 2, 1, 1.0, 0.0, 0.0, origin), 111.0);
        var windowed = window.Snapshot(111.0);
        // playtest B4 #16, 27 Sept 2026: shipped Game.cfg 15 s
        TestAssert.Equal(2, windowed.Count);
        TestAssert.Equal(100.0, windowed[0].Timestamp);
        TestAssert.Equal(2, window.Snapshot(115.0).Count, "the minimum includes its exact boundary");
        TestAssert.Equal(1, window.Snapshot(115.001).Count, "fixed-window policy trims only older rows");
        TestAssert.Equal(111.0, window.Snapshot(115.001)[0].Timestamp);
    }

    [Check("death-recap.packet-rejects-unsafe-records")]
    public static void PacketRejectsUnsafeRecords()
    {
        var origin = DeathRecapOrigin.BasicAttack(0, DeathRecapSourceType.Hero);
        // playtest B4 #16, 27 Sept 2026: shipped Game.cfg 15 s
        var snapshot = new DeathRecapSnapshot(1, 2, 1, 15.0,
            DeathRecapSourceType.Hero, Array.Empty<DeathRecapRecord>());
        AssertThrows(() => CombatPackets.DeathRecap(snapshot,
            new[] { new DeathRecapRecord(1.0, 99, 1, 1.0, 0.0, 0.0, origin) }));
        AssertThrows(() => CombatPackets.DeathRecap(snapshot,
            new[] { new DeathRecapRecord(1.0, 2, 0, 1.0, 0.0, 0.0, origin) }));
        AssertThrows(() => CombatPackets.DeathRecap(snapshot,
            new[] { new DeathRecapRecord(1.0, 2, 1, double.NaN, 0.0, 0.0, origin) }));
        AssertThrows(() => CombatPackets.DeathRecap(snapshot,
            new[] { new DeathRecapRecord(1.0, 2, 1, 0.0, 0.0, 0.0, origin) }));
        var unsafeOrigin = origin with { ParentScriptNameHash = 0x1234 };
        AssertThrows(() => CombatPackets.DeathRecap(snapshot,
            new[] { new DeathRecapRecord(1.0, 2, 1, 1.0, 0.0, 0.0, unsafeOrigin) }));
    }

    private static void AssertThrows(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected death recap validation to throw.");
        }
        catch (ArgumentException)
        {
        }
    }
}
