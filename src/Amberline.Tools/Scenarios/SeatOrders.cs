using System.Buffers.Binary;
using Amberline.Core;

namespace Amberline.Tools.Scenarios;

/// <summary>The cast request the headless match sends for a bot seat (the scenario director is not part of this tree).</summary>
public static class SeatOrders
{
    /// <summary>
    /// The cast request (0xA2, 34 bytes): <c>[0xA2][caster u32][slot&lt;&lt;1 | summoner][position 3f][end position 3f][target u32]</c>,
    /// the layout <c>SpellSystem.ParseCastRequest</c> reads.
    /// </summary>
    internal static byte[] CastPayload(uint caster, int slot, bool summoner, Position2 position, double positionHeight,
        Position2 end, double endHeight, uint target)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), caster);
        payload[5] = checked((byte)((slot << 1) | (summoner ? 1 : 0)));
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6), (float)position.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(10), (float)positionHeight);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14), (float)position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(18), (float)end.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(22), (float)endHeight);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(26), (float)end.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30), target);
        return payload;
    }
}
