using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 952 (community report, 28 November 2026: "Ennemies spells in fog of war replay when you go in it"). The stock client's buff add
/// (126 0xBF, 132 0xC0) plays the buff's OnBuffActivate event and voice for every add it takes; the count
/// update (0x1F) and the renewal (0x32) change a copy it holds and play nothing. An enemy who saw Annie's first Pyromania stack holds that copy
/// through the fog, so the stack she gains in fog comes with the reveal as a count update, as it would in sight. Molten Shield, gained in fog, is
/// a buff the client does not hold: only an add creates it, so the reveal still adds it (the client has no silent create).
/// </summary>
public static class B952FogBuffReplayChecks
{
    [Check("spells.b952-a-stack-gained-in-fog-reaches-the-reveal-as-a-count-update")]
    public static void StackGainedInFogReachesTheRevealAsACountUpdate()
    {
        using var match = new Match("Annie");
        match.Learn(3);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Ally.Position = new Position2(7000, 6850);
        match.Foe.Position = new Position2(7350, 7000);
        var ally = Observe(match, Team.Order, match.Ally, 9001);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        var view = match.Runtime.Delivery.Observers.First(observer => ReferenceEquals(observer.Transport, enemy)).View;
        var pyromania = SpellPackets.BuffNameHash(GameData.Default.ClientBuffName("Pyromania"));
        var shield = SpellPackets.BuffNameHash(GameData.Default.ClientBuffName("MoltenShield"));
        match.Advance(0.5);
        TestAssert.True(view.Visible.Contains(match.Hero.NetId), "the enemy sees Annie first");
        TestAssert.True(match.Cast(1, at: new Position2(6600, 7000)), "Incinerate is cast in sight");
        match.Advance(1.0);
        TestAssert.Equal(1, Buff(match.Hero, "Pyromania")?.Stacks ?? 0, "Incinerate gives the first Pyromania stack");
        var slot = checked((byte)Buff(match.Hero, "Pyromania")!.Slot);
        TestAssert.True(view.Buffs.TryGetValue((match.Hero.NetId, slot), out var held) && held.Hash == pyromania && held.Count == 1,
            "the enemy holds Pyromania x1");

        match.Foe.Position = new Position2(7000, 10500);
        match.Advance(0.5);
        TestAssert.False(view.Visible.Contains(match.Hero.NetId), "the enemy lost her");
        match.Refill();
        var fog = match.Now;
        TestAssert.True(match.Cast(2), "Molten Shield is cast in fog");
        match.Advance(0.5);
        TestAssert.Equal(2, Buff(match.Hero, "Pyromania")?.Stacks ?? 0, "Molten Shield gives the second stack");
        TestAssert.True(Buff(match.Hero, "MoltenShield") is not null, "the shield runs at the reveal");
        var reveal = match.Now;
        match.Foe.Position = new Position2(7350, 7000);
        match.Advance(0.5);
        TestAssert.True(view.Visible.Contains(match.Hero.NetId), "the enemy sees her again");

        bool OnSlot(PlaintextPacket packet, byte opcode) => packet.Plaintext.Length >= 7 && packet.Plaintext.Span[0] == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == match.Hero.NetId;
        bool Add(PlaintextPacket packet, uint hash) => OnSlot(packet, SpellPackets.BuffAddOpcode) && packet.Plaintext.Length >= 13 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == hash;
        bool CountTo(PlaintextPacket packet, byte count) => OnSlot(packet, SpellPackets.BuffUpdateCountOpcode) &&
            packet.Plaintext.Span[5] == slot && packet.Plaintext.Span[6] == count;
        int Between(BbM3.WireCapture capture, double from, double to, Func<PlaintextPacket, bool> test) =>
            capture.Packets.Count(entry => entry.At >= from && entry.At < to && test(entry.Packet));

        TestAssert.Equal(0, Between(enemy, fog, reveal, packet => OnSlot(packet, SpellPackets.BuffAddOpcode) ||
            OnSlot(packet, SpellPackets.BuffUpdateCountOpcode)), "no buff packet of hers reached the enemy in fog");
        TestAssert.Equal(0, Between(enemy, reveal, double.MaxValue, packet => Add(packet, pyromania)),
            "the held Pyromania is not added again at the reveal (an add replays its activation)");
        TestAssert.Equal(1, Between(enemy, reveal, double.MaxValue, packet => CountTo(packet, 2)),
            "the stack gained in fog comes with the reveal as one count update to x2");
        TestAssert.Equal(1, Between(enemy, reveal, double.MaxValue, packet => Add(packet, shield)),
            "Molten Shield, which the enemy does not hold, is still added at the reveal");
        TestAssert.Equal(1, Between(ally, 0, double.MaxValue, packet => Add(packet, pyromania)),
            "the ally who saw both casts got Pyromania added once");
        TestAssert.Equal(1, Between(ally, fog, double.MaxValue, packet => CountTo(packet, 2)), "and the second stack as a count update");
    }
}
