using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 909: a stack added with <c>BUFF_STACKS_AND_CONTINUE</c> raises the count and leaves the timer running; the client's count update (0x1F)
/// carries the time left as its duration and the time the timer has already run as its running time, so the HUD ring keeps its total.
/// </summary>
public static class BuffContinueTimerWireChecks
{
    /// <summary>
    /// A second Health Potion two seconds into the first one's 15 s (<c>RegenerationPotion</c>, the shipped potions' <c>BUFF_STACKS_AND_CONTINUE</c>
    /// on both lines): the 0x1F says count 2, 13 s left and 2 s run. The stock client's 0x1F handler starts the copy at now - running and ends it
    /// at now + duration, and its buff bar's total is end - start.
    /// </summary>
    [Check("buffs.b909-continuing-stack-tells-the-client-its-elapsed-time")]
    public static void ContinuingStackTellsTheClientItsElapsedTime()
    {
        using var match = new Match("Annie");
        var hero = match.Hero;
        var wire = Observe(match, hero.Team, hero);
        var buff = match.Runtime.SpellBuffs.AddContinuing(hero, "RegenerationPotion", BuffType.Heal, 15.0, hero.NetId, 5, new BuffPayload());
        TestAssert.True(buff is not null, "the first potion's buff is added");
        match.Advance(2.0);
        var first = wire.Packets.Count;
        match.Runtime.SpellBuffs.AddContinuing(hero, "RegenerationPotion", BuffType.Heal, 15.0, hero.NetId, 5, new BuffPayload());
        match.Advance(Step);
        TestAssert.Equal(2, buff!.Stacks, "the second potion stacks");
        var update = wire.Packets.Skip(first).Select(item => item.Packet.Plaintext.ToArray()).Single(bytes =>
            bytes.Length >= 15 && bytes[0] == SpellPackets.BuffUpdateCountOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == hero.NetId && bytes[5] == buff.Slot);
        TestAssert.Equal((byte)2, update[6], "the count");
        var duration = BinaryPrimitives.ReadSingleLittleEndian(update.AsSpan(7));
        var running = BinaryPrimitives.ReadSingleLittleEndian(update.AsSpan(11));
        TestAssert.Near(2.0, running, Step + 1e-4, $"the running time is the time since the first potion (duration {R(duration)})");
        TestAssert.Near(15.0 + buff.Payload.WireDurationMargin, duration + running, 1e-3, "duration plus running time is the potion's 15 s");
    }
}
