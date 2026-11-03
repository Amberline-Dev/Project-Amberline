using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// The windup gate (<c>SpellWindup</c>, 18 October 2026) on the production dispatcher, read off the wire: a move click inside the
    /// uncancellable windup of a cast sends the client no route (it would walk the hero in the cast pose), a second click replaces it,
    /// an accepted cast replaces it, and the click acts 0.1 s after the cast frame. Recall (batch 47) is the same rule.
    /// </summary>
    [Check("spells-windup-gate-holds-a-click-and-applies-it-after-the-cast-frame")]
    public static void AClickInsideAnUncancellableWindupReachesTheClientAfterIt()
    {
        foreach (var kind in new[] { "annie-q", "annie-r", "orianna-r", "ezreal-r", "recall" })
        {
            var champion = kind.Split('-')[0] switch { "annie" => "Annie", "orianna" => "Orianna", "ezreal" => "Ezreal", _ => "Annie" };
            using var match = new Match(champion, Team.Order);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            match.Clear();
            switch (kind)
            {
                case "annie-q": match.Cast(0, 0, enemy); break;
                case "annie-r": match.CastGround(0, enemy.Position, slot: 3); break;
                case "orianna-r": match.Cast(0, 3, hero); break;
                case "ezreal-r": match.CastDirected(0, 3, hero.Position, enemy.Position); break;
                default: match.Cast(0, SpellSystem.RecallSlot, hero); break;
            }
            var windup = hero.CastWindupRemaining;
            TestAssert.True(windup > 0.2, $"{kind}: the cast packet starts the client's uncancellable window ({windup:0.000} s)");

            match.Clear();
            match.Move(0, hero.Position + new Position2(0, 300));
            TestAssert.True(hero.Waypoints.Count == 0 && hero.Target is null, $"{kind}: the click inside the windup installs no route");
            TestAssert.False(HeroWire(match, hero), $"{kind}: and tells the client nothing (no route, no stop)");

            // A newer click replaces the held one.
            match.Move(0, hero.Position + new Position2(0, -300));
            match.Runtime.Tick(windup + 0.05);
            // (The module's own cast-completion packets, Ezreal's stop among them, may reach the client here: only the route is judged.)
            TestAssert.True(hero.Waypoints.Count == 0 && hero.Target is null, $"{kind}: still held until 0.1 s after the cast frame");
            match.Runtime.Tick(0.1);
            TestAssert.True(HeroWire(match, hero), $"{kind}: the held click reaches the client after the cast frame");
            TestAssert.True(hero.Waypoints.Count > 0 || hero.Target is not null, $"{kind}: and installs its route");
            var end = hero.Waypoints.Count > 0 ? hero.Waypoints[^1] : hero.Target!.Value;
            TestAssert.True(end.Z < hero.Position.Z - 100.0, $"{kind}: the later click won ({end})");
        }

        // An accepted cast replaces a held click: nothing is applied after the windups.
        using (var match = new Match("Annie", Team.Order))
        {
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            match.Cast(0, 0, enemy);
            var origin = hero.Position;
            match.Move(0, origin + new Position2(0, 300));
            match.CastDirected(0, 1, hero.Position, enemy.Position);
            match.Runtime.Tick(1.0);
            TestAssert.Equal(origin, hero.Position, "an accepted cast replaces the click held through the windup");
            TestAssert.True(hero.Waypoints.Count == 0 && hero.Target is null, "and no route was applied after it");
        }

        // An instacast has no SpellCaster on the client, so its click acts at once.
        using (var match = new Match("Orianna", Team.Order))
        {
            var hero = match.Hero(0);
            match.Cast(0, 1, hero);
            TestAssert.True(hero.CastWindupRemaining <= 0.0, "Orianna W is an instacast: no windup");
            match.Move(0, hero.Position + new Position2(0, 300));
            TestAssert.True(hero.Waypoints.Count > 0 || hero.Target is not null, "its click is routed at once");
        }

        static bool HeroWire(Match match, Entity hero) => match.Captures.Any(capture => capture.Packets.Any(packet =>
            Opcode(packet) is 0xC1 or 0x39 && packet.Plaintext.Length >= 5 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId));
    }

    /// <summary>
    /// Ezreal's Q and W parents are instacasts (<c>SpellData*Flags</c> 347140, bit 4 set) whose scripts cast the normal children
    /// <c>EzrealMysticShotMissile</c> and <c>EzrealEssenceFluxMissile</c> (flags 115712 and 76800, no instacast bit), and the child's cast
    /// packet is what makes the client's SpellCaster. A press inside R's one second windup is therefore held like any non-instant spell
    /// and cast 0.1 s after R goes out (owner decision, 18 October 2026, evening); the module's pending state used to refuse it.
    /// </summary>
    [Check("spells-windup-gate-holds-ezreal-q-and-w-pressed-inside-r-and-casts-them-after-it")]
    public static void EzrealQAndWPressedInsideTheRWindupAreCastAfterIt()
    {
        foreach (var slot in new[] { 0, 1 })
        {
            using var match = new Match("Ezreal", Team.Order);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            match.CastDirected(0, 3, hero.Position, enemy.Position);
            TestAssert.True(hero.CastWindupRemaining > 0.9, $"slot {slot}: R's cast packet starts a one second window");
            match.Runtime.Tick(0.1);
            match.CastDirected(0, slot, hero.Position, enemy.Position);
            TestAssert.Equal(0.0, hero.SpellCooldowns[slot], $"slot {slot}: the press inside R's windup is held, not cast");
            match.Runtime.Tick(0.8);
            TestAssert.Equal(0.0, hero.SpellCooldowns[slot], $"slot {slot}: still held until R goes out");
            match.Runtime.Tick(0.4);
            TestAssert.True(hero.SpellCooldowns[slot] > 0.0, $"slot {slot}: cast right after R went out");
        }
    }
}
