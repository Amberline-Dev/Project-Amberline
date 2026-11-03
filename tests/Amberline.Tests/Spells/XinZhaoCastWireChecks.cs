using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 168 part 2, owner findings 23 and 26 (20 October 2026): one cause, in the cast answer of the Three Talon Strike
/// children. A <c>BBSpellCast</c> a <c>BuffOnPreAttack</c> makes in place of the swing is the hero's own ATTACK, and the
/// client needs the isAutoAttack bit (0x01) to run it as one (the client's cast handler):
/// an extra-slot answer without it and without <c>forceCastingOrChannelling</c> runs
/// the cast-and-serve routine straight from the packet and builds no <c>SpellCaster</c>, so the spell's own
/// <c>AnimationName</c> never plays (finding 26); and every <c>SpellCaster</c> the local player's hero gets for a
/// non-auto-attack cast opens the HUD cast bar, so the same bit is what keeps the bar off
/// (finding 23).
/// </summary>
/// <summary>
/// Finding 25 is the shared <c>FURTHEST_WITHIN_RANGE</c> landing: a request shorter than one client lattice step found no
/// candidate and fell back to the origin, so Xin Zhao's dash was a zero-length move (no <c>0x67</c>, and the buff's dash
/// animation cleared in the same tick) and the hero only got the follow-up attack order.
/// </summary>
public static class XinZhaoCastWireChecks
{
    private static byte Opcode(PlaintextPacket packet) => packet.Plaintext.Span[0];

    /// <summary>Every cast answer of one caster: its spell hash, slot, flags and designer total time, in wire order.</summary>
    private static List<(uint Hash, int Slot, byte Flags, float Total)> CastAnswers(WireCapture wire, uint casterNetId) =>
        wire.Packets.Select(item => item.Packet).Where(packet => Opcode(packet) == SpellPackets.CastSpellAnswerOpcode &&
                packet.Plaintext.Length > 9 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == casterNetId)
            .Select(packet =>
            {
                var info = packet.Plaintext.Span[9..];
                var tail = 48 + 17 * info[47];
                return (BinaryPrimitives.ReadUInt32LittleEndian(info[2..]), (int)info[tail + 21], info[tail + 20],
                    BinaryPrimitives.ReadSingleLittleEndian(info[(tail + 8)..]));
            }).ToList();

    [Check("spells-live-a-three-talon-strike-swing-is-announced-as-the-heroes-attack")]
    public static void ThreeTalonStrikeChildrenCarryTheAutoAttackBit()
    {
        // Findings 23 and 26 together: the three Thrust children are cast from the combo buffs' BuffOnPreAttack hooks in place
        // of the swing the script skipped (BBSkipNextAutoAttack), so each answer must carry isAutoAttack (0x01) and the hero's
        // own attack period as its designer total time. The bar and the animation are the client's; the packet is the server's.
        using var match = new Match("XinZhao");
        var xin = match.Hero;
        var foe = match.Foe;
        xin.Position = new Position2(7000, 7000);
        foe.Position = new Position2(7120, 7000);
        match.Learn(3);
        match.Refill();
        match.Advance(0.2);
        var wire = Observe(match, xin.Team, xin);
        wire.Packets.Clear();
        TestAssert.True(match.Cast(0, at: xin.Position), "Three Talon Strike arms the combo");
        match.Advance(0.3);
        var period = xin.AttackPeriod;
        var seen = 0;
        for (var attack = 0; attack < 3; attack++)
        {
            var mark = wire.Packets.Count;
            match.Runtime.Combat.QueueBasicAttack(xin, foe);
            for (var tick = 0; tick < 90; tick++)
            {
                match.Advance(1.0 / 30.0);
                var replacement = CastAnswers(wire, xin.NetId).Skip(seen).FirstOrDefault(answer => answer.Slot >= 42);
                if (replacement.Hash == 0) continue;
                seen++;
                TestAssert.Equal(0x01, replacement.Flags & 0x01,
                    $"hit {attack + 1}: the swing's replacement is flagged isAutoAttack (no HUD cast bar, and the client builds a SpellCaster that plays the spell's AnimationName)");
                TestAssert.Near(period, replacement.Total, 0.02,
                    $"hit {attack + 1}: the cast lasts the hero's attack period, like the swing it replaces");
                break;
            }
            TestAssert.True(seen == attack + 1, $"hit {attack + 1}: a Thrust child answered the swing ({seen} seen)");
        }
        // The slot spells are not attacks: the combo's own 0xBD keeps the bit clear (its bar is the ordinary 0.25 s cast bar).
        var combo = CastAnswers(wire, xin.NetId).First(answer => answer.Slot == 0);
        TestAssert.Equal(0x00, combo.Flags & 0x01, "the Q press itself is a spell, not an attack");
    }

    [Check("spells-live-an-audacious-charge-up-close-still-dashes")]
    public static void AudaciousChargeAtPointBlankSendsItsDash()
    {
        // Finding 25: the client lattice is 2 units, so a request of 1 to 3 units quantises to a point that can be as far as
        // sqrt(2) * 2 from the raw request. The landing search used to allow no outward step and fell back to the raw origin,
        // which made BbScriptMoves.Start a zero-length move: no 0x67 and no dash clip, only the follow-up attack order.
        foreach (var gap in new[] { 1.0, 2.0, 3.0, 5.0 })
        {
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                using var match = new Match("XinZhao", team: team);
                var xin = match.Hero;
                var foe = match.Foe;
                xin.Position = new Position2(7000, 7000);
                foe.Position = new Position2(7000 + gap, 7000);
                match.Learn(3);
                match.Refill();
                match.Advance(0.2);
                var wire = Observe(match, xin.Team, xin);
                wire.Packets.Clear();
                TestAssert.True(match.Cast(2, foe), $"{team} gap {gap}: Audacious Charge accepted");
                match.Advance(0.4);
                // Batch 291: this observer's first sight of Xin is mid-dash, so (batch 276) the dash is her reveal's 0xC2 movement data type 1,
                // read by the 0x67 entry reader; a 0x67 only for a view that already saw her.
                var dashes = wire.Packets.Count(item => ChampionLiveRegressionChecks.AnnouncesDash(item.Packet, xin.NetId));
                TestAssert.Equal(1, dashes, $"{team} gap {gap}: one dash packet (the script's BBMove reached the client)");
                // BBOverrideAnimation Run->Spell1 replaces the WHOLE map (0x6E), so the dash's map is the one that names Spell1 and the
                // buff's OnBuffDeactivate then sends an empty map that restores Run. Two maps with Spell1 are possible on a re-entry
                // (the buff's deactivation followed by the next activation); what matters is that one went out and the map ended empty.
                var states = wire.Packets.Where(item => Opcode(item.Packet) == 0x6E).ToArray();
                TestAssert.True(states.Length >= 2, $"{team} gap {gap}: the run clip is replaced and the run override cleared");
                var animations = states.Where(item => System.Text.Encoding.ASCII.GetString(item.Packet.Plaintext.Span)
                    .Contains("Spell1", StringComparison.Ordinal)).ToArray();
                TestAssert.True(animations.Length >= 1, $"{team} gap {gap}: the Run -> Spell1 dash override went out");
                TestAssert.Equal(0, states[^1].Packet.Plaintext.Span[5], $"{team} gap {gap}: and the map is empty again at the end");
                TestAssert.True(xin.ForcedMove is null, $"{team} gap {gap}: the dash is over");
                TestAssert.True(System.Math.Sqrt(System.Math.Pow(xin.Position.X - foe.Position.X, 2) +
                                                 System.Math.Pow(xin.Position.Z - foe.Position.Z, 2)) <= gap,
                    $"{team} gap {gap}: and ended on the target, not short of it");
            }
        }
    }
}