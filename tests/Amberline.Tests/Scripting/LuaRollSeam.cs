using Amberline.Scripting.Lua;

namespace Amberline.Tests;

/// <summary>
/// Batch 222: the BuildingBlocks roll (<c>IBbGameHost.Random</c>, the script's <c>CO_RANDOM_CHANCE_LESS_THAN</c>) is drawn from the match's Lua
/// stream (<c>WorldState.LuaRandom</c>), the way retail's BuildingBlocks random runs in Lua. A check that used to feed rolls through the match
/// RNG (<c>FixedRandomSource</c>, <c>CountingProcRandom</c>) sets the stream instead: <see cref="Next"/> makes the next roll return the value
/// the check always fed; <see cref="Passing"/> makes the next rolls of one hit pass.
/// </summary>
internal static class LuaRollSeam
{
    /// <summary>Sets <paramref name="crt"/> so that its next step yields the draw <paramref name="draw"/> (to 15 bits) in [0, 1).</summary>
    public static void Next(CrtState crt, double draw)
    {
        var fifteenBits = (uint)Math.Round(draw * 32767.0);
        var next = fifteenBits << 16;
        crt.HoldRand = unchecked((next - 2531011u) * Inverse(214013u));
    }

    /// <summary>
    /// Sets <paramref name="crt"/> so that its next <paramref name="count"/> draws all pass: the first is exactly 0 and the others are below 0.05.
    /// Every roll of a hit returned 0.0 when the match RNG fed it, so a hit whose rolls all pass is the same case.
    /// </summary>
    public static void Passing(CrtState crt, int count)
    {
        for (uint low = 0; low < 65536; low++)
        {
            // The first draw is 0 when the state after its step has zero in bits 16..30 (bits 0..15 are free).
            var after = low;
            var state = after;
            var pass = true;
            for (var i = 1; i < count && pass; i++)
            {
                state = unchecked(state * 214013u + 2531011u);
                pass = ((state >> 16) & 0x7fff) < 1638; // 0.05 of 32767
            }
            if (!pass) continue;
            crt.HoldRand = unchecked((after - 2531011u) * Inverse(214013u));
            return;
        }
        throw new InvalidOperationException($"no Lua stream state passes {count} draws");
    }

    /// <summary>The inverse of an odd number modulo 2^32 (Newton's iteration; each step doubles the correct bits).</summary>
    private static uint Inverse(uint odd)
    {
        var x = odd;
        for (var i = 0; i < 5; i++) x = unchecked(x * (2u - odd * x));
        return x;
    }
}
