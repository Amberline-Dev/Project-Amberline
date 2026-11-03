using Amberline.Core;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static class PathToNetworkDataChecks
{
    // Batch 315: the client's path-to-network-data writer (shared path code the client links for its own order send) writes
    // every point after the first as a signed byte with its flag bit only when |delta| < 0x80 on the 2-unit grid, else as a short,
    // so a delta of -128 is a short. The 0x67 entry's path (read by the client's dash reader and its path-from-network-data reader)
    // is written the same way.
    [Check("protocol.path-to-network-data-writes-a-delta-byte-only-below-128")]
    public static void DeltaByteOnlyBelow128()
    {
        var bounds = new NavigationBounds(0, 14000, 0, 14000); // grid origin 7000, 7000
        const uint unit = 0x40000001;
        // Grid points (0, 0), (-128, 127), (-1, 0): deltas x -128 (short), z +127, x +127, z -127 (bytes).
        var route = VisionPackets.RouteMovementData(unit, [new(7000, 7000), new(6744, 7254), new(6998, 7000)], bounds, 5)!;
        // [type][sync u32][header u32][net id u32] then the path.
        TestAssert.Equal("0E" + "00000000" + "80FF" + "7F" + "7F" + "81", Convert.ToHexString(route.AsSpan(13)),
            "flags 0x0E (x1 cleared), both first coordinates and x1 as shorts, the other three as bytes");

        var dash = VisionPackets.Dash(unit, new Position2(7000, 7000), new Position2(7100, 6800), 1200f, bounds);
        var path = dash.Plaintext.Span[(dash.Plaintext.Length - 7)..].ToArray();
        TestAssert.Equal("03" + "00000000" + "32" + "9C", Convert.ToHexString(path), "the dash entry's two points: deltas 50 and -100 as bytes");
        var decoded = (object?[])WirePacketDecoder.Decode(dash.Plaintext.Span, dash.Channel).Fields["waypoints"]!;
        TestAssert.Equal(2, decoded.Length);
        // The decoder places points around the Map1 grid origin, so compare the hop, not the points.
        var (start, end) = ((float[])decoded[0]!, (float[])decoded[1]!);
        TestAssert.Near(100, end[0] - start[0], 1e-3, "the decoded hop x");
        TestAssert.Near(-200, end[1] - start[1], 1e-3, "the decoded hop z");
    }

    // Batch 316: the client's position compressor packs a coordinate from its float32, subtracts the integer origin and divides by
    // the step 2 on the x87 stack, stores the quotient as float32 and truncates it. Two boundary values land one grid step from a
    // double truncation: a double that rounds up to the next float32, and a small float32 whose quotient rounds to a whole number.
    // The 0x8C FX positions use the same grid (the client's FX position packer).
    [Check("protocol.get-compressed-pos-rounds-in-float32")]
    public static void CompressedPosRoundsInFloat32()
    {
        var bounds = new NavigationBounds(0, 13982, 0, 14446); // grid origin 6991, 7223 (Map1's)
        // 7000.99999999 is the float32 7001: (7001 - 6991) / 2 = 5 (a double truncates 4.999999995 to 4).
        TestAssert.Equal(((short)5, (short)0), VisionPackets.Compress(new Position2(7000.99999999, 7223), bounds));
        // The float32 1 + 2^-23: (x - 6991) / 2 = -3494.99999994, float32 -3495 (a double truncates to -3494).
        TestAssert.Equal(((short)-3495, (short)0), VisionPackets.Compress(new Position2(1.0000001192092896, 7223), bounds));
        var fx = FxPackets.CompressPosition(new Position2(7000.99999999, 1.0000001192092896), bounds, (_, _) => 0.0);
        TestAssert.Equal((short)5, fx.X, "the FX x");
        TestAssert.Equal((short)-3611, fx.Z, "the FX z: (1.0000001 - 7223) / 2 = -3610.99999994, float32 -3611");
    }
}
