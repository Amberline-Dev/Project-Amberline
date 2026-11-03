using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 168, owner findings 10 and 18 (20 October 2026): "Amumu's E cooldown jumps 9 -> 8 -> 9 on the HUD after use",
/// the same on Ryze's Q, "every ability whose cooldown is shortened by any means". Root cause: the engine sent the cast's
/// cooldown twice, once as <c>0x8A</c> at the press and again in the cast answer (<c>0xBD</c>), and the client applies the
/// cast answer's copy at the cast frame (the shared cost routine and its cooldown adjustment,
/// the slot's remaining time is raised to <c>castInfo.cooldown</c> when it is shorter). With a
/// reduced cooldown the extra <c>0x8A</c> arrived 0.25 s late and raised a record that had already run down across a
/// whole-second boundary (<c>ceil(8.1 - 0.2)</c> = 8 -> 9). The client needs the cast answer's copy only, so the engine
/// sends no <c>0x8A</c> for its own cost write; retail's server writes that packet only from a script's
/// <c>BBSetSlotSpellCooldownTime</c>. These checks pin both
/// halves: no eager packet, and the cast answer carrying the value the client starts the slot from.
/// </summary>
public static class CastCooldownPacketChecks
{
    private static byte Opcode(PlaintextPacket packet) => packet.Plaintext.Span[0];

    /// <summary>The cast answer's own cooldown and the slot it names, for a champion slot of <paramref name="casterNetId"/>.</summary>
    private static (double Cooldown, int Slot)? CastAnswer(PlaintextPacket packet, uint casterNetId)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 9 + 48 || Opcode(packet) != SpellPackets.CastSpellAnswerOpcode) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) != casterNetId) return null;
        var info = span[9..];
        var tail = 48 + 17 * info[47];
        if (info.Length < tail + 22) return null;
        return (BinaryPrimitives.ReadSingleLittleEndian(info[(tail + 12)..]), info[tail + 21]);
    }

    private static double[] CooldownPackets(IEnumerable<PlaintextPacket> packets, Entity hero, int slot) =>
        packets.Where(packet => Opcode(packet) == 0x8A && packet.Plaintext.Length >= 10 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
                (packet.Plaintext.Span[5] & 0x7F) == slot)
            .Select(packet => (double)BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..])).ToArray();

    [Check("spells-live-a-champion-cast-starts-its-hud-cooldown-from-the-cast-answer-only")]
    public static void AChampionCastSendsNoSeparateCooldownPacket()
    {
        // Amumu's Tantrum (9 s at rank 2) and Ryze's Overload (3.5 s), with and without the cooldown reduction that made
        // the owner's finding visible: -10 % turns them into 8.1 s and 3.15 s, which cross a whole second inside the
        // 0.25 s windup, so an extra packet then raised the HUD's readout by a second.
        foreach (var champion in new[] { "Amumu", "Ryze" })
        foreach (var reduction in new[] { 0.0, -0.10 })
        {
            using var match = new Match(champion);
            var hero = match.Hero;
            var foe = match.Foe;
            match.Learn(2);
            hero.Position = new Position2(7000, 7000);
            foe.Position = new Position2(7150, 7000);
            match.Refill();
            // Tantrum is a self area cast, Overload a unit-target missile.
            var slot = champion == "Amumu" ? 2 : 0;
            var spell = champion == "Amumu" ? "Tantrum" : "Overload";
            var expected = GameData.Default.Spell(spell, 5).Cooldowns[hero.SpellRanks[slot] - 1] * (1.0 + reduction);
            match.Advance(0.2);
            hero.CooldownMod = reduction;
            var wire = Observe(match, hero.Team, hero);
            wire.Packets.Clear();
            TestAssert.True(champion == "Amumu" ? match.Cast(slot, at: foe.Position) : match.Cast(slot, foe),
                $"{champion}: cast accepted");
            // Batch 340: both are windups, and the cost write is the cast frame's (DoCast_ClientAndServer -> ApplyCostsOfSpell), as the
            // client's own: the press starts nothing, the cast frame starts the reduced cooldown (was: written in the press's step).
            TestAssert.Near(0.0, hero.SpellCooldowns[slot], 1e-9, $"{champion} {reduction}: the press starts no cooldown");
            var peak = 0.0;
            for (var elapsed = 0.0; elapsed < 0.5 - 1e-9; elapsed += Step)
            {
                match.Advance(Step);
                peak = Math.Max(peak, hero.SpellCooldowns[slot]);
            }
            TestAssert.Near(expected, peak, Step + 1e-6, $"{champion} {reduction}: the cast frame starts the reduced cooldown");
            var packets = wire.Packets.Select(item => item.Packet).ToArray();
            TestAssert.Equal(0, CooldownPackets(packets, hero, slot).Length,
                $"{champion} {reduction}: the engine's own cost write sends no 0x8A (it used to repeat the cast answer 0.25 s later)");
            var answers = packets.Select(packet => CastAnswer(packet, hero.NetId))
                .Where(answer => answer is { Slot: var named } && named == slot).ToArray();
            TestAssert.Equal(1, answers.Length, $"{champion} {reduction}: one cast answer names the slot");
            TestAssert.Near(expected, answers[0]!.Value.Cooldown, 1e-6,
                $"{champion} {reduction}: the cast answer carries the reduced cooldown, which the client applies at its cast frame");
        }
    }

    [Check("spells-live-a-script-set-cooldown-still-sends-its-own-packet")]
    public static void AScriptWrittenCooldownStillReachesTheClient()
    {
        // The other half of the split: what retail's server does send as 0x8A is a script's
        // BBSetSlotSpellCooldownTime(Ver2). Blitzcrank's Power Fist (Spells/PowerFist.luaobj) writes E's cooldown from
        // its hooks (0 while the fist is armed); Amumu's Tantrum writes none, which the check above pins.
        using var match = new Match("Blitzcrank");
        var hero = match.Hero;
        match.Learn(2);
        hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Refill();
        match.Advance(0.2);
        var wire = Observe(match, hero.Team, hero);
        wire.Packets.Clear();
        TestAssert.True(match.Cast(2, hero), "Power Fist armed");
        match.Advance(0.3);
        var armed = CooldownPackets(wire.Packets.Select(item => item.Packet), hero, 2);
        TestAssert.True(armed.Length >= 1, "the script's own cooldown write sends a 0x8A");
        TestAssert.Near(0.0, armed[^1], 1e-9, "the arm clears the slot (the script's Src)");
        TestAssert.Near(0.0, hero.SpellCooldowns[2], 1e-9, "and the server holds the same");
    }
}