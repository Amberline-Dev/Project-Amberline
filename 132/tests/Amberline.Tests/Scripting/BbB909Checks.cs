using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 909 (19 November 2026, owner decisions on batch 901's findings): Sejuani's Glacial Prison bursts on the champion it struck, and
/// Graves' True Grit ring keeps its timer when the count rises.
/// </summary>
public static class BbB909Checks
{
    /// <summary>
    /// <c>GravesPassiveCounter</c>'s one-second periodic raises <c>GravesPassiveGrit</c> with <c>BUFF_STACKS_AND_CONTINUE</c>: the count rises and
    /// the 4 s timer the hit started keeps running. The stock client's <c>0x1F</c> handler hands the packet's duration (@7) and
    /// running time (@11) to the instance's resize (end = now + duration), and the HUD draws the ring over
    /// end - start. So the raise one second after a lone hit carries 3 s left and 1 s run: the ring goes on over 4 s instead of restarting.
    /// </summary>
    [Check("scripting.b909-graves-true-grit-raise-keeps-its-four-second-ring")]
    public static void TrueGritRaiseKeepsItsRing()
    {
        using var match = new Match("Graves");
        var hero = match.Hero;
        match.Learn(1);
        var wire = Observe(match, hero.Team, hero);
        hero.AttackTarget = match.Foe.NetId;
        for (var step = 0; step < 90 && Buff(hero, "GravesPassiveGrit") is null; step++) match.Advance(Step);
        var grit = Buff(hero, "GravesPassiveGrit");
        TestAssert.True(grit is not null, "Graves' first hit starts True Grit");
        hero.AttackTarget = null;
        var hitAt = match.Now;
        match.Advance(2.5);
        var margin = grit!.Payload.WireDurationMargin;
        var gritHash = SpellPackets.BuffNameHash("GravesPassiveGrit");
        var slot = -1;
        var held = 0;
        var raises = new List<(double At, int Count, float Duration, float Running)>();
        foreach (var (at, packet) in wire.Packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != hero.NetId) continue;
            if (bytes[0] == SpellPackets.BuffAddOpcode && bytes.Length >= 25 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) == gritHash)
            {
                slot = bytes[5];
                held = bytes[7];
                continue;
            }
            if (bytes[0] != SpellPackets.BuffUpdateCountOpcode || bytes.Length < 15 || bytes[5] != slot) continue;
            if (at > hitAt + 1e-6 && bytes[6] > held)
                raises.Add((at, bytes[6], BinaryPrimitives.ReadSingleLittleEndian(bytes[7..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[11..])));
            held = bytes[6];
        }
        var text = string.Join(" ", raises.Select(raise => $"{R(raise.At - hitAt)}s:x{raise.Count} dur {R(raise.Duration)} run {R(raise.Running)}"));
        TestAssert.True(raises.Count >= 2, "Grit rises twice after the lone hit: " + text);
        for (var i = 0; i < 2; i++)
        {
            var raise = raises[i];
            TestAssert.Near(i + 1.0, raise.At - hitAt, 2 * Step + 1e-6, $"raise {i + 1} comes {i + 1} s after the hit: " + text);
            TestAssert.Near(raise.At - hitAt, raise.Running, 2 * Step + 1e-4, $"raise {i + 1} tells the client the timer has run since the hit: " + text);
            TestAssert.Near(4.0 + margin, raise.Duration + raise.Running, 1e-3, $"raise {i + 1} keeps the ring's 4 s total: " + text);
        }
    }

    /// <summary>
    /// Owner decision (19 November 2026, Sejuani's champion spotlight: the explosion is centred on the target). <c>SejuaniGlacialPrison</c>'s
    /// <c>TargetExecute</c> destroys the line on the champion it struck, and <c>SejuaniGlacialPrisonStart</c>'s <c>BuffOnMissileEnd</c> draws
    /// <c>sejuani_ult_impact_nova.troy</c> and scans its 450 area at <c>MissileEndPosition</c>. That end is the struck champion's centre: the nova's
    /// position is the one the ice block bound to the champion carries, a unit 400 beyond the champion is in the area and a unit beside the line
    /// 500 short of the champion (in reach of the first contact, 150-175 short) is not.
    /// </summary>
    [Check("scripting.b909-sejuani-glacial-prison-bursts-on-the-struck-champion")]
    public static void GlacialPrisonBurstsOnTheStruckChampion()
    {
        using var match = new Match("Sejuani");
        match.Hero.Level = 6;
        match.Learn(1);
        match.Advance(0.3);
        match.Foe.Position = new Position2(7900, 7000);
        Enemy(match, "Beyond", new Position2(8300, 7000));
        Enemy(match, "Short", new Position2(7450, 7220));
        var wire = Observe(match, match.Hero.Team, match.Hero);
        TestAssert.True(match.Cast(3, at: match.Foe.Position), "Glacial Prison is cast at the foe 900 away: " + match.CanCast(3).Reason);
        match.Advance(2.0);
        var log = string.Join(" / ", match.Log.Hits);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the prison strikes the foe; log: " + log);
        var nova = wire.Packets.Single(item => EffectNamed(item.Packet, "sejuani_ult_impact_nova")).Packet.Plaintext.Span;
        var block = wire.Packets.Single(item => EffectNamed(item.Packet, "sejuani_ult_tar_03")).Packet.Plaintext.Span;
        var dx = (BinaryPrimitives.ReadInt16LittleEndian(nova[33..]) - BinaryPrimitives.ReadInt16LittleEndian(block[33..])) * 2.0;
        var dz = (BinaryPrimitives.ReadInt16LittleEndian(nova[39..]) - BinaryPrimitives.ReadInt16LittleEndian(block[39..])) * 2.0;
        var area = $"area hits: Beyond {HitsOn(match, "Beyond").Count}, Short {HitsOn(match, "Short").Count}";
        TestAssert.True(Math.Sqrt(dx * dx + dz * dz) <= 4.0, $"the nova is on the struck champion (off by {R(dx)}, {R(dz)}; {area})");
        TestAssert.Equal(1, HitsOn(match, "Beyond").Count, "the burst reaches the unit 400 beyond the champion; log: " + log);
        TestAssert.Equal(0, HitsOn(match, "Short").Count, "the burst spares the unit 500 short of the champion; log: " + log);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }
}
