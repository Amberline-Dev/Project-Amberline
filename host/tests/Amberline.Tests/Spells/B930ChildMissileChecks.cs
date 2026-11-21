using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 930 (players, 21 November 2026, Graves's Buckshot: "no projectile, just hits everyone in the cone immediately", "damage is calculated half a
/// second after", "randomly doesn't work about 25% of the time"). Two shared causes, both seen on Ashe's Volley too (the same shape on both lines):
/// a line child cast with <c>FireWithoutCasting</c> reached every client but its caster's as a cast answer, which builds a SpellCaster with the child's
/// own cast time (0.5 s for VolleyAttack and GravesClusterShotAttack) whenever the parent's SpellCaster has already ended there; and a cone flagged
/// <c>LockConeToPlayer</c> walked its caster first when the request's end lay beyond the cone's reach from where the server placed him.
/// </summary>
public static class B930ChildMissileChecks
{
    private static IEnumerable<(string Champion, int Slot, string Child)> Shooters()
    {
        yield return ("Ashe", 1, "VolleyAttack");
        // Graves is a 1.0.0.132 champion (patch 1.0.0.127); the 126 data has no Graves.
        if (GameData.Default.OptionalChampion("Graves") is not null) yield return ("Graves", 0, "GravesClusterShotAttack");
    }

    private static Match Duel(string champion, out WireCapture own, out WireCapture enemy)
    {
        var match = new Match(champion);
        match.Learn(1);
        match.Refill();
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Foe.Position = new Position2(match.Hero.Position.X + 500.0, match.Hero.Position.Z);
        match.Ally.Position = new Position2(match.Hero.Position.X - 300.0, match.Hero.Position.Z - 300.0);
        own = Observe(match, Team.Order, match.Hero);
        enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.5);
        return match;
    }

    /// <summary>
    /// The child's missiles reach every client as their creation 0x41 alone, never as a cast answer: the stock client casts an extra-slot answer at
    /// once only while its spellbook holds a SpellCaster (the client: the at-once branch sits under
    /// the spellbook+0x2c test); once the parent's caster has passed its end time it has stopped and released that slot
    ///, and the answer then builds a new SpellCaster whose
    /// cast frame, and so the missiles, come the child's own cast time later, after the server's hits. The server sends the children at the parent's cast
    /// frame, 0.217 to 0.25 s after the parent's answer, against the parent's 0.25 s, so which way a client went was a matter of milliseconds.
    /// </summary>
    [Check("spells.b930-a-fire-without-casting-line-child-reaches-every-client-as-its-missile")]
    public static void FireWithoutCastingLineChildReachesEveryClientAsItsMissile()
    {
        foreach (var (champion, slot, child) in Shooters())
        {
            using var match = Duel(champion, out var own, out var enemy);
            var hash = SpellPackets.BuffNameHash(child);
            var pressed = match.Now;
            TestAssert.True(match.Cast(slot, at: match.Foe.Position), $"{champion}: the cast is accepted");
            match.Advance(1.2);
            List<(double At, uint Missile, byte Opcode)> Child(WireCapture capture) => capture.Packets
                .Where(entry => entry.At > pressed && MissileOf(entry.Packet) is { } m && m.Spell == hash)
                .Select(entry => (entry.At, MissileOf(entry.Packet)!.Value.Missile, entry.Packet.Plaintext.Span[0])).ToList();
            var mine = Child(own);
            var theirs = Child(enemy);
            TestAssert.Equal(0, mine.Count(entry => entry.Opcode != 0x41), $"{champion}: no cast answer of {child} reaches the caster's client");
            TestAssert.Equal(0, theirs.Count(entry => entry.Opcode != 0x41), $"{champion}: no cast answer of {child} reaches the enemy's client");
            var missiles = mine.Select(entry => entry.Missile).Distinct().OrderBy(id => id).ToArray();
            TestAssert.True(missiles.Length >= 3, $"{champion}: the caster's client gets the {child} missiles ({missiles.Length})");
            TestAssert.Equal(string.Join(",", missiles), string.Join(",", theirs.Select(entry => entry.Missile).Distinct().OrderBy(id => id)),
                $"{champion}: the enemy's client gets the same {child} missiles");
            var first = mine.Min(entry => entry.At);
            TestAssert.Near(first, theirs.Min(entry => entry.At), 1e-9, $"{champion}: both clients get them in the same step");
            var hits = HitsOn(match, "Foe");
            TestAssert.True(hits.Count >= 1 && hits.Min(hit => hit.At) > first + 1e-9,
                $"{champion}: the foe is hit after the missiles were created ({string.Join(",", hits.Select(hit => hit.At.ToString("0.000")))} vs {first:0.000})");
        }
    }

    /// <summary>
    /// A cone flagged <c>LockConeToPlayer</c> fires where its caster stands at any aim distance (the client
    /// puts the end at the reach when the flag is set, so its start is the caster); the cast answer goes out in the next step and the caster does not walk.
    /// Annie's Incinerate (no flag) still walks for an aim beyond its 521.3 (batch 324), the control.
    /// </summary>
    [Check("spells.b930-a-cone-locked-to-its-caster-fires-at-any-aim-distance")]
    public static void ConeLockedToItsCasterFiresAtAnyAimDistance()
    {
        foreach (var (champion, slot, _) in Shooters().Append(("Annie", 1, "")))
        foreach (var distance in new[] { 900.0, 1500.0 })
        {
            using var match = Duel(champion, out var own, out _);
            var start = match.Hero.Position;
            var pressed = match.Now;
            // the request's end straight north, away from the foe: a kite
            var accepted = match.Cast(slot, at: start + new Position2(0.0, distance));
            match.Advance(2 * Step);
            var answered = own.Packets.Any(entry => entry.At > pressed && entry.Packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode &&
                                                    BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == match.Hero.NetId);
            var moved = Math.Sqrt(Math.Pow(match.Hero.Position.X - start.X, 2) + Math.Pow(match.Hero.Position.Z - start.Z, 2));
            var context = $"{champion} slot {slot}, end {distance:0} away";
            if (champion == "Annie")
            {
                TestAssert.False(answered, $"{context}: Incinerate (no LockConeToPlayer) walks toward its start first");
                continue;
            }
            TestAssert.True(accepted && answered, $"{context}: the cast answer goes out in the next step");
            // the cast's own stop snaps the hero onto the client grid (about a unit); a walk toward the start is 70 units or more
            TestAssert.True(moved < 5.0, $"{context}: the caster does not walk ({moved:0.0})");
        }
    }
}
