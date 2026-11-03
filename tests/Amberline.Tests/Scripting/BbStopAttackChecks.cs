using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Night 2 (17 Oct 2026): DestroyMissile (flag 0x10 of a stop-attack, 0x39) removes the missile of the unit's current SpellCaster,
/// which for a spell is the spell's own (looked up by the id the SpellCaster stored from the cast packet), for as
/// long as that SpellCaster lives. So a 0x39 that carries it right after a spell cast, or while a chain orb flies, takes the spell's
/// missile out of the client. The owner's Ryze report of 16 October ("the E projectile disappears after its first hit") had its own
/// cause (the chain list, batch 28), but this stop was a second way to lose the orb: the hero's stop order and the stop in front of
/// every cast carried 0x10 whether or not the server had dropped a swing. Only a swing the server dropped carries it now.
/// </summary>
public static class BbStopAttackChecks
{
    private const byte StopAttackOpcode = 0x39;
    private const byte DestroyClientMissileOpcode = 0x5D;
    private const byte ClientPlayerOrderStop = 10;

    [Check("scripting.n2-a-hero-stop-never-destroys-a-spell-missile")]
    public static void AStopNeverTakesSpellFluxsOrbOrAFlyingAutoOutOfTheClient()
    {
        using var match = new Match("Ryze");
        match.Learn(1);
        // The orb has a second unit to bounce to, so it is in the air for a while.
        match.FarFoe.Position = new Position2(7500, 7000);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        match.Refill();

        // The owner right-clicks an enemy: Ryze's auto launches, then he presses E on the same enemy.
        match.Hero.AttackTarget = match.Foe.NetId;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the auto starts");
        var auto = match.Runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == match.Hero.NetId);
        match.Runtime.Combat.CombatTick(match.Hero.AttackWindup + 0.01, regenerate: false);
        TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.MissileNetId == auto.MissileNetId && hit.WindupRemaining <= 0.0),
            "the auto has launched: its hit stands");
        match.Runtime.Delivery.Flush();
        wire.Packets.Clear();

        TestAssert.True(match.Cast(2, match.Foe), "E cast right after the auto");
        match.Advance(BbM3.Step);
        TestAssert.True(wire.Packets.Any(item => item.Packet.Plaintext.Span[0] == 0xBD), "the client received the cast");

        // The S key while the orb flies.
        match.Advance(GameDataCastTime() + 0.1);
        var stopOrder = ClientGameplayPackets.MoveOrder(match.Hero.NetId, ClientPlayerOrderStop, match.Hero.Position, 0);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, stopOrder.Plaintext.Span, match.Runtime.Dependencies.Gameplay),
            "the stop order is accepted");
        match.Runtime.Delivery.Flush();
        match.Advance(2.0);

        var stops = wire.Packets.Where(item => item.Packet.Plaintext.Span[0] == StopAttackOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == match.Hero.NetId)
            .Select(item => item.Packet.Plaintext.Span[5]).ToArray();
        TestAssert.Equal(2, stops.Length, "the cast stops the auto and the S key stops the hero");
        TestAssert.False(stops.Any(flags => (flags & CombatPackets.CancelWindupFlags) != 0),
            "no stop carries DestroyMissile: the server dropped no swing, and the SpellCaster of the cast owns the orb");
        var order = wire.Packets.Where(item => item.Packet.Plaintext.Span[0] is StopAttackOpcode or 0xBD &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == match.Hero.NetId)
            .Select(item => item.Packet.Plaintext.Span[0]).ToArray();
        TestAssert.Equal([StopAttackOpcode, (byte)0xBD, StopAttackOpcode], order, "the cast's stop comes before the cast, the S key after");
        TestAssert.Equal(CombatPackets.StandingSwingFlags, stops[0],
            "in front of the cast the launched auto is the current SpellCaster: its cast frame is forced if the client has not reached it");
        TestAssert.Equal(CombatPackets.LandedAttackFlags, stops[1],
            "the S key while the orb flies: Spell Flux is the current SpellCaster, so nothing is forced and nothing destroyed");
        TestAssert.False(wire.Packets.Any(item => item.Packet.Plaintext.Span[0] == DestroyClientMissileOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == auto.MissileNetId),
            "the launched auto is not destroyed");
        TestAssert.Equal(0, match.Log.DestroyedMissiles.Count, "nor is the orb");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.n2-a-swing-dropped-after-a-cast-replaced-it-is-not-told")]
    public static void ASwingThatACastReplacedIsNotToldWhenTheServerDropsIt()
    {
        // Kayle's W is an instacast that does not take the hero's order, so the swing stays in its windup on the server; the cast
        // replaces it on the client (Client_DoCastSpell force-stops the attack's SpellCaster). When the owner then clicks away and
        // the server drops the windup, the hero's current SpellCaster is the spell's: a stop with DestroyMissile would remove
        // its missile, and there is no swing left to tell the client about.
        using var match = new Match("Kayle");
        match.Learn(1);
        match.Foe.Position = new Position2(7100, 7000);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        match.Refill();

        match.Hero.AttackTarget = match.Foe.NetId;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the swing starts");
        TestAssert.True(match.Cast(1, match.Ally), "the instant W is cast inside the windup");
        TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == match.Hero.NetId && hit.WindupRemaining > 0.0),
            "the instacast leaves the swing pending on the server");
        match.Runtime.Delivery.Flush();
        wire.Packets.Clear();

        var move = ClientGameplayPackets.MoveOrder(match.Hero.NetId, 2, match.Hero.Position + new Position2(300, 0), 0);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, move.Plaintext.Span, match.Runtime.Dependencies.Gameplay),
            "the move order is accepted");
        match.Runtime.Delivery.Flush();
        TestAssert.False(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == match.Hero.NetId && hit.WindupRemaining > 0.0),
            "the server dropped the windup");
        var stops = wire.Packets.Where(item => item.Packet.Plaintext.Span[0] == StopAttackOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == match.Hero.NetId)
            .Select(item => item.Packet.Plaintext.Span[5]).ToArray();
        TestAssert.Equal(1, stops.Length, "one stop for the move order");
        TestAssert.Equal(CombatPackets.LandedAttackFlags, stops[0],
            "neither DestroyMissile nor ForceSpellCast: the swing is not the hero's current SpellCaster any more, Kayle's W is");
    }

    private static double GameDataCastTime() => Amberline.Data.GameData.Default.Spell("SpellFlux", 5).CastTime;
}
