using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 912 (19 November 2026, the owner's second 1.0.0.132 hand test): Ahri W's gesture, Graves' True Grit count and Smoke Screen canister,
/// Volibear E's "weird red tornado". docs/maintenance/2026-11-19-batch-912-visuals2.md.
/// </summary>
public static class BbB912Checks
{
    // VolibearE's inibin carries Warwick's HuntersCall hit fields (HaveHitEffect, HitEffectName HuntersCall_eff2.troy), so the default hit pass drew
    // Warwick's red swirl on Volibear and on every unit the roar hit. The retail spotlight shows violet rings and white lightning, no swirl (batch 915).
    [Check("scripting.b912-volibear-majestic-roar-sends-no-warwick-hunters-call-swirl")]
    public static void MajesticRoarSendsNoWarwickSwirl()
    {
        using var match = new Match("Volibear", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        match.Foe.Position = match.Hero.Position + new Position2(200, 0);
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, id: 9002);
        match.Advance(0.2);
        TestAssert.True(match.Cast(2), "Majestic Roar is cast: " + match.CanCast(2).Reason);
        match.Advance(0.5);
        TestAssert.True(Buff(match.Foe, "VolibearE") is not null, "fixture: the roar hits Orianna; buffs " + Names(match.Foe));
        foreach (var (client, wire) in new[] { ("Volibear's", own), ("the enemy's", enemy) })
        {
            var packets = wire.Packets.Select(item => item.Packet).ToArray();
            TestAssert.Equal(0, packets.Count(packet => EffectNamed(packet, "HuntersCall_eff2")),
                $"{client} client gets no HuntersCall_eff2 (Warwick's swirl copied into VolibearE's inibin)");
            TestAssert.True(packets.Any(packet => EffectNamed(packet, "volibear_E_tar")), $"{client} client still gets the roar's own hit effect");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // AhriFoxFire plays Spell2 (Lock, ScaleTime 0.71) and adds UnlockAnimation for 0.25 s. Standing still, the unlock's stop (0x2B) cut the spin at
    // 0.25 s on the client; held, the whole gesture plays (the 2011 spotlight's look). An order inside the gesture sends the held stop at once, once.
    [Check("scripting.b912-ahri-fox-fire-gesture-plays-out-while-she-stands-and-an-order-cuts-it")]
    public static void FoxFireGesturePlaysOutAndAnOrderCutsIt()
    {
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        var own = Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        own.Packets.Clear();
        TestAssert.True(match.Cast(1), "W is cast: " + match.CanCast(1).Reason);
        var cast = match.Now;
        match.Advance(0.4);
        TestAssert.True(own.Packets.Any(item => item.Packet.Plaintext.Span[0] == 0xB9), "fixture: the Spell2 gesture is played");
        var early = Stops(own, match.Hero).Select(at => R(at - cast)).ToArray();
        TestAssert.Equal(0, early.Length, "no stop cuts the gesture while she stands (0x2B at " + string.Join(",", early) + " s)");
        match.Runtime.Spells.CancelOrder(match.Hero);
        match.Advance(Step);
        TestAssert.Equal(1, Stops(own, match.Hero).Length, "an order inside the gesture sends the held stop first");
        match.Runtime.Spells.CancelOrder(match.Hero);
        match.Advance(Step);
        TestAssert.Equal(1, Stops(own, match.Hero).Length, "and only once");

        // A cast inside the gesture: the held stop goes out before the cast's answer (the client refuses a play under the lock).
        match.Hero.CooldownsDisabled = true;
        match.Advance(6.0);
        match.Refill();
        own.Packets.Clear();
        TestAssert.True(match.Cast(1), "W again: " + match.CanCast(1).Reason);
        match.Advance(0.4);
        TestAssert.Equal(0, Stops(own, match.Hero).Length, "fixture: no stop yet");
        TestAssert.True(match.Cast(0, at: new Position2(match.Hero.Position.X + 500.0, match.Hero.Position.Z)), "Q inside the gesture: " +
            match.CanCast(0).Reason);
        match.Advance(Step);
        var qHash = Amberline.Simulation.Spells.SpellSystem.ProtocolHash("AhriOrbofDeception");
        var stop = own.Packets.FindIndex(item => item.Packet.Plaintext.Span[0] == 0x2B);
        var answer = own.Packets.FindIndex(item => item.Packet.Plaintext.Span[0] == 0xBE && item.Packet.Plaintext.Length > 15 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[11..]) == qHash);
        TestAssert.True(stop >= 0 && answer >= 0 && stop < answer, $"the held stop precedes Q's cast answer (stop #{stop}, answer #{answer})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // Left alone, the gesture ends by itself: a later order sends no stop. Walking, the unlock still stops the clip at once (she runs at 0.25 s).
    [Check("scripting.b912-ahri-fox-fire-unlock-stays-for-a-walking-ahri-and-expires-with-the-gesture")]
    public static void FoxFireUnlockWalkingAndExpiry()
    {
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        var own = Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        TestAssert.True(match.Cast(1), "W is cast standing");
        match.Advance(2.0);
        own.Packets.Clear();
        match.Runtime.Spells.CancelOrder(match.Hero);
        match.Advance(Step);
        TestAssert.Equal(0, Stops(own, match.Hero).Length, "an order 2 s after the gesture ended sends no stray stop");
        match.Hero.CooldownsDisabled = true;
        match.Advance(4.0);   // the fox-fires end
        match.Refill();
        match.Runtime.Spells.HeroOrders!.StartMove(match.Hero, new Position2(match.Hero.Position.X + 1500.0, match.Hero.Position.Z));
        match.Advance(0.3);
        own.Packets.Clear();
        TestAssert.True(match.Cast(1), "W is cast walking: " + match.CanCast(1).Reason);
        var cast = match.Now;
        match.Advance(0.4);
        var stops = Stops(own, match.Hero).Select(at => at - cast).ToArray();
        TestAssert.True(stops.Length == 1 && stops[0] < 0.34, "walking, the timed unlock stops the clip at once: 0x2B at " +
            string.Join(",", stops.Select(R)) + " s");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    private static double[] Stops(WireCapture wire, Entity unit) => wire.Packets
        .Where(item => item.Packet.Plaintext.Span[0] == 0x2B && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == unit.NetId)
        .Select(item => item.At).ToArray();

    // The withhold is keyed to VolibearE: Warwick's own Hunter's Call keeps drawing its swirl through the same default hit pass.
    [Check("scripting.b912-warwick-hunters-call-keeps-its-swirl")]
    public static void HuntersCallKeepsItsSwirl()
    {
        using var match = new Match("Warwick", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        var own = Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        TestAssert.True(match.Cast(1), "Hunter's Call is cast: " + match.CanCast(1).Reason);
        match.Advance(0.5);
        TestAssert.True(own.Packets.Any(item => EffectNamed(item.Packet, "HuntersCall_eff2")), "Warwick's client gets HuntersCall_eff2");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
