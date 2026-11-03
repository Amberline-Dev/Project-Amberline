using Amberline.Core;
using Amberline.Network;

namespace Amberline.Tests;

public static class FoundationChecks
{
    [Check("clock")]
    public static void Clock()
    {
        var real = new GameClock();
        TestAssert.True(real.Now() > 1_000_000_000.0, "real mode must return Unix wall-clock seconds");
        TestAssert.False(real.IsVirtual);

        var clock = new GameClock();
        clock.SetVirtual(100.0);
        TestAssert.True(clock.IsVirtual);
        TestAssert.Equal(100.0, clock.Now());
        TestAssert.Equal(100.0, clock.Monotonic());
        TestAssert.Equal(100_000_000_000L, clock.MonotonicNanoseconds());

        clock.Advance(2.5);
        TestAssert.Equal(102.5, clock.Now());
        TestAssert.Equal(102_500L, clock.SyncId());

        var ids = new long[5];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = clock.SyncId();
        }

        for (var i = 1; i < ids.Length; i++)
        {
            TestAssert.True(ids[i] > ids[i - 1], "sync IDs must strictly increase in one millisecond");
        }

        clock.Advance(0.0005);
        TestAssert.True(clock.SyncId() > ids[^1], "sub-millisecond advances must not move sync IDs backwards");

        clock.SetVirtual(5.0);
        TestAssert.Equal(5_000L, clock.SyncId());

        var other = new GameClock();
        other.SetVirtual(5.0);
        TestAssert.Equal(5_000L, other.SyncId());
        TestAssert.Equal(5_001L, clock.SyncId());

        var wall = 11.5;
        var mono = 22.5;
        var injected = new GameClock(() => wall, () => mono);
        TestAssert.Equal(wall, injected.Now());
        TestAssert.Equal(mono, injected.Monotonic());
        injected.SetVirtual(7.0);
        injected.Advance(0.5);
        TestAssert.Equal(7.5, injected.Now());
        injected.UseReal();
        TestAssert.Equal(wall, injected.Now());
        TestAssert.Equal(mono, injected.Monotonic());
        TestAssert.Throws<InvalidOperationException>(() => injected.Advance(1.0));
    }

    [Check("packet-buffer")]
    public static void PacketBuffer()
    {
        var writer = new Amberline.Network.PacketBuffer(64);
        writer.WriteByte(0x7f);
        writer.WriteSByte(-2);
        writer.WriteBoolean(true);
        writer.WriteInt16(unchecked((short)0x1234));
        writer.WriteUInt16(0xabcd);
        writer.WriteInt32(unchecked((int)0x12345678));
        writer.WriteUInt32(0x89abcdef);
        writer.WriteInt64(unchecked((long)0x0123456789abcdef));
        writer.WriteUInt64(0xfedcba9876543210);
        writer.WriteSingle(1.23456789);
        writer.WriteDouble(Math.PI);
        writer.WriteBytes(new byte[] { 0xaa, 0xbb });

        var bytes = writer.ToArray();
        TestAssert.Equal((byte)0x7f, bytes[0]);
        TestAssert.Equal((byte)0x34, bytes[3]);
        TestAssert.Equal((byte)0x12, bytes[4]);
        TestAssert.Equal((byte)0xef, bytes[11]);
        TestAssert.Equal((byte)0xcd, bytes[12]);

        var reader = new Amberline.Network.PacketBuffer(bytes);
        TestAssert.Equal((byte)0x7f, reader.ReadByte());
        TestAssert.Equal((sbyte)-2, reader.ReadSByte());
        TestAssert.True(reader.ReadBoolean());
        TestAssert.Equal(unchecked((short)0x1234), reader.ReadInt16());
        TestAssert.Equal((ushort)0xabcd, reader.ReadUInt16());
        TestAssert.Equal(unchecked((int)0x12345678), reader.ReadInt32());
        TestAssert.Equal(0x89abcdefu, reader.ReadUInt32());
        TestAssert.Equal(unchecked((long)0x0123456789abcdef), reader.ReadInt64());
        TestAssert.Equal(0xfedcba9876543210ul, reader.ReadUInt64());
        TestAssert.Near(1.23456789, reader.ReadSingle(), 0.000001);
        TestAssert.Near(Math.PI, reader.ReadDouble(), 0.0);
        TestAssert.Equal(new byte[] { 0xaa, 0xbb }, reader.ReadBytes(2));
        TestAssert.Equal(0, reader.Remaining);
        TestAssert.Throws<InvalidOperationException>(() => reader.ReadByte());

        var fixedWriter = new Amberline.Network.PacketBuffer(1);
        TestAssert.Throws<InvalidOperationException>(() => fixedWriter.WriteUInt16(1));
        TestAssert.Throws<InvalidOperationException>(() => reader.WriteByte(1));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => reader.ReadBytes(-1));
        TestAssert.Throws<OverflowException>(() => fixedWriter.WriteSingle(1e100));
    }
}
