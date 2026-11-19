using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 905 (19 November 2026, owner finding: "Graves passive has a weird delay sometimes where it gets refreshed but the +1 of the number
/// appears a bit later - consistent issue after E / with high attack speed"). True Grit's count rises on <c>GravesPassiveCounter</c>'s own
/// one-second clock (its <c>BuffOnUpdateActions</c>: <c>BBExecutePeriodically</c> 1 s, <c>ExecuteImmediately</c> false, a
/// <c>BUFF_STACKS_AND_CONTINUE</c> add of <c>GravesPassiveGrit</c>), while every hit Graves deals or takes only renews the counter and resets
/// Grit's timer to 4 s (<c>CharScriptGraves</c> <c>CharOnPreDealDamage</c>/<c>CharOnPreDamage</c>: <c>BUFF_RENEW_EXISTING</c> and
/// <c>BBSpellBuffRenew</c>). Below one attack a second the refreshes come more often than the +1, so most refreshes are followed by the +1
/// a moment later: the shipped scripts' timing, not a server delay.
/// </summary>
public static class BbB905GravesGritChecks
{
    /// <summary>
    /// At an attack period under a second (Quickdraw rank 5 and two Recurve Bows) the count still rises exactly once a second from the first
    /// hit: a renewal keeps the counter's clock (batch 896; a renewal that reset <c>LastTimeExecuted</c> would never let the count rise while
    /// the hits come faster than a second). The hits between two rises refresh Grit on the wire with the count it already has.
    /// </summary>
    [Check("scripting.p132-graves-true-grit-count-rises-once-a-second-and-hits-only-refresh-it")]
    public static void TrueGritCountRisesOnItsOwnClock()
    {
        using var match = new Match("Graves");
        var hero = match.Hero;
        match.Learn(5);
        match.Refill();
        hero.Inventory = [new InventorySlot(1043), new InventorySlot(1043), null, null, null, null];
        match.Runtime.HeroStats.Refresh(hero, push: false);
        var wire = Observe(match, hero.Team, hero);
        TestAssert.True(match.Cast(2, at: new Position2(7000, 7300)), "Quickdraw is cast");
        match.Advance(0.6);
        hero.AttackTarget = match.Foe.NetId;
        var rises = new List<(double At, int Count)>();
        var last = 0;
        for (var step = 0; step < 30 * 6; step++)
        {
            match.Advance(Step);
            var stacks = Buff(hero, "GravesPassiveGrit")?.Stacks ?? 0;
            if (stacks > last) rises.Add((match.Now, stacks));
            last = stacks;
        }
        TestAssert.True(hero.AttackPeriod < 1.0, $"Graves attacks faster than once a second ({hero.AttackPeriod:0.000} s)");
        var hits = HitsOn(match, "Foe");
        TestAssert.True(rises.Count >= 5 && rises[0].Count == 2, "the first hit starts Grit at two stacks, then it rises; rises: " +
            string.Join(" ", rises.Select(rise => $"{R(rise.At)}:{rise.Count}")));
        TestAssert.Near(hits[0].At, rises[0].At, 1e-6, "Grit starts on the first hit");
        for (var i = 1; i < rises.Count; i++)
        {
            TestAssert.Near(1.0, rises[i].At - rises[i - 1].At, Step + 1e-6, $"rise {i} comes a second after the one before");
            TestAssert.Equal(rises[i - 1].Count + 1, rises[i].Count, $"rise {i} adds one stack");
        }
        TestAssert.True(hits.Count > rises.Count, $"more hits ({hits.Count}) than rises ({rises.Count}): the hits only refresh");
        // On Graves' own wire every Grit packet of a hit step that is not a rise step carries the count Grit already had.
        var gritHash = SpellPackets.BuffNameHash("GravesPassiveGrit");
        var slot = -1;
        var held = 0;
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
            if (bytes[0] != SpellPackets.BuffUpdateCountOpcode || bytes[5] != slot) continue;
            var count = bytes[6];
            var riseStep = rises.Any(rise => Math.Abs(rise.At - at) < 1e-6);
            if (!riseStep) TestAssert.Equal(held, (int)count, $"the refresh at {R(at)} keeps the count");
            TestAssert.True(count >= held, $"the count on the wire never falls ({held} -> {count} at {R(at)})");
            held = count;
        }
        TestAssert.True(slot >= 0, "Grit reached Graves' client");
        TestAssert.Equal(rises[^1].Count, held, "the client ends on the server's count");
    }
}
