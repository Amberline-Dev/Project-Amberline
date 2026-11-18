using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>Batch 885: the 0xB9 PlayAnimation flag word of 1.0.0.132 (Fizz's Playful / Trickster showed no body animation on the 132 client).</summary>
public static class B885PlayAnimationFlagChecks
{
    /// <summary>
    /// The 1.0.0.132 client hands the 0xB9 flag word to its AnimationComponent: 8 queues the
    /// clip behind the one playing and 4 queues it unless that one loops; with neither the clip plays at once
    ///, 0x10 marking it looping, 0x20 locking it and 0x80
    /// cutting the blend. 0x40 skips a clip already playing. The 126 word (8 replace, 2 loop, 0x40 lock, 0x200 no blend) queued every
    /// scripted clip behind Fizz's looping idle or run.
    /// </summary>
    [Check("protocol.p132-play-animation-plays-at-once-with-the-132-component-bits")]
    public static void PlayAnimationUsesThe132ComponentBits()
    {
        static uint Flags(PlaintextPacket packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]);
        TestAssert.Equal(0u, Flags(SpellPackets.PlayAnimation(1, "Spell3a")), "a plain play: at once, blended, unlocked, not looped");
        TestAssert.Equal(0x20u, Flags(SpellPackets.PlayAnimation(1, "Spell3c", lockAnimation: true)), "Lock is the component's lock bit 0x20");
        TestAssert.Equal(0x10u, Flags(SpellPackets.PlayAnimation(1, "Channel", loop: true)), "Loop is the component's loop bit 0x10");
        TestAssert.Equal(0x80u, Flags(SpellPackets.PlayAnimation(1, "Spell3c", blend: false)), "Blend false is the component's no-blend bit 0x80");
        TestAssert.Equal(0xB0u, Flags(SpellPackets.PlayAnimation(1, "Spell1b", loop: true, lockAnimation: true, blend: false)), "all three");

        var (match, own, _) = B885FizzPlayfulTricksterChecks.Arena();
        using var scope = match;
        var hero = match.Hero;
        var from = own.Packets.Count;
        TestAssert.True(B885FizzPlayfulTricksterChecks.Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), "Playful is accepted");
        match.Advance(1.0);
        var plays = own.Packets.Skip(from).Select(entry => entry.Packet.Plaintext.ToArray())
            .Where(bytes => bytes.Length > 13 && bytes[0] == 0xB9 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == hero.NetId)
            .Select(bytes => (Name: Encoding.ASCII.GetString(bytes, 13, Array.IndexOf(bytes, (byte)0, 13) - 13), Flags: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5))))
            .ToList();
        TestAssert.True(plays.Any(play => play.Name == "Spell3a") && plays.Any(play => play.Name == "Spell3c"),
            "the hop (Spell3a) and the slam (Spell3c) are sent: " + string.Join(",", plays.Select(play => $"{play.Name}:0x{play.Flags:X}")));
        TestAssert.True(plays.All(play => (play.Flags & 0xCu) == 0),
            "no E clip asks the client to queue it behind the clip playing: " + string.Join(",", plays.Select(play => $"{play.Name}:0x{play.Flags:X}")));
    }
}
