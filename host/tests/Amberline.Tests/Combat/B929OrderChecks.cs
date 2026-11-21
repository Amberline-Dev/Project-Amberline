using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 929 (players, 21 November 2026, "no orbwalking and randomly causes him to freeze in place", worst on Graves: a freeze after his E and
/// his Q lost about a quarter of the time). The probe's case table (`.artifacts/b929/probe-*.txt`) found the orbwalk itself timed right and
/// two freezes, both in shared engine code: a spell that takes the hero's order, cast while walking, left the hero standing after its cast
/// frame (the walk was not handed back), and a click given during the hero's own dash was dropped, together with a cast queued behind that
/// dash. The client moves only along a route the server sends, so a dropped walk is a hero standing on every screen.
/// </summary>
public static class B929OrderChecks
{
    private static Match Duel(string champion, double range, out WireCapture capture)
    {
        var match = new Match(champion, foeChampion: "Orianna");
        match.Hero.Level = 9;
        match.Learn(5);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Foe.Position = new Position2(match.Hero.Position.X + range, match.Hero.Position.Z);
        match.Ally.Position = new Position2(match.Hero.Position.X - 150.0, match.Hero.Position.Z - 300.0);
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        capture = Observe(match, Team.Order, match.Hero);
        match.Advance(0.5);
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(match.Foe.NetId);
        view.Visible.Add(match.Foe.NetId);
        return match;
    }

    /// <summary>A player's order through the production dispatcher (the 0x75 of 126, 0x76 of 132).</summary>
    private static void Order(Match match, byte order, Position2 at, uint target = 0)
    {
        var packet = ClientGameplayPackets.MoveOrder(match.Hero.NetId, order, at, target);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span, match.Runtime.Dependencies.Gameplay), $"order {order}");
    }

    private static bool Hero(Match match, PlaintextPacket packet, byte opcode) =>
        packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] == opcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == match.Hero.NetId;

    /// <summary>The hero's routes after <paramref name="after"/>: time, point count, last point.</summary>
    private static List<(double At, int Points, Position2 End)> Routes(Match match, WireCapture capture, double after) =>
        capture.Packets.Where(entry => entry.At > after + 1e-9 && Hero(match, entry.Packet, VisionPackets.WaypointListOpcode) &&
                                       (entry.Packet.Plaintext.Span.Length - 9) % 8 == 0)
            .Select(entry =>
            {
                var span = entry.Packet.Plaintext.Span;
                return (entry.At, (span.Length - 9) / 8, new Position2(BinaryPrimitives.ReadSingleLittleEndian(span[^8..]),
                    BinaryPrimitives.ReadSingleLittleEndian(span[^4..])));
            }).ToList();

    private static List<double> Sent(Match match, WireCapture capture, byte opcode, double after) =>
        capture.Packets.Where(entry => entry.At > after + 1e-9 && Hero(match, entry.Packet, opcode)).Select(entry => entry.At).ToList();

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// Freeze 1: a spell that takes the hero's order (a cast time, or an instacast whose script casts a windup child), cast while the hero walks
    /// a move order. The cast stops the hero (its stop path and the client's StopActor at the cast start); the walk comes back once the cast
    /// frame has run, as an attack order does since batch 807 (the engine's order record postpones the current order for the cast:
    /// ShouldPostponeCurrentOrderForSpellcast, TryToExecutePostponedOrders). Before the fix the hero stood until the idle scan attacked.
    /// </summary>
    [Check("attacks.b929-cast-while-walking-walks-on-after-the-cast-frame")]
    public static void CastWhileWalkingWalksOnAfterTheCastFrame()
    {
        // The spell whose cast frame ends the stop: the slot spell, or the windup child of an instacast (Mystic Shot's missile).
        foreach (var (champion, slot, unitTarget, framed) in new[]
                 { ("Nunu", 2, true, "IceBlast"), ("Annie", 0, true, "Disintegrate"), ("Ezreal", 0, false, "EzrealMysticShotMissile") })
        {
            using var match = Duel(champion, 450.0, out var capture);
            var hero = match.Hero;
            var goal = hero.Position + new Position2(0, -900);
            Order(match, 2, goal);
            match.Advance(5 * Step);
            var walkEnd = hero.Waypoints[^1];
            var pressed = match.Now;
            TestAssert.True(match.Cast(slot, unitTarget ? match.Foe : null, at: match.Foe.Position), $"{champion}: the cast is accepted");
            match.Advance(1.0);
            var cast = Sent(match, capture, SpellPackets.CastSpellAnswerOpcode, pressed - Step).FirstOrDefault(double.NaN);
            TestAssert.False(double.IsNaN(cast) || cast > pressed + Step + 1e-6, $"{champion}: the cast answer goes out at once");
            var frame = GameData.Default.Spell(framed, 5).CastDelay;
            TestAssert.True(frame > 0.0, $"{framed} has a cast time");
            var routes = Routes(match, capture, cast - 1e-6);
            var context = $"{champion} slot {slot}, pressed at {pressed:0.000}, cast {cast:0.000}";
            TestAssert.True(routes.Count >= 1 && routes[0].Points == 1, $"{context}: the cast stops the walk ({routes.Count} routes)");
            var resumed = routes.Skip(1).FirstOrDefault(route => route.Points >= 2);
            TestAssert.True(resumed.Points >= 2, $"{context}: the walk is handed back after the cast frame");
            TestAssert.True(resumed.At >= cast + frame - 1e-6 && resumed.At <= cast + 0.6,
                $"{context}: the walk resumes at {resumed.At:0.000}, after the cast frame");
            TestAssert.True(Distance(resumed.End, walkEnd) < 40.0, $"{context}: to the ordered point ({resumed.End} vs {walkEnd})");
            TestAssert.True(Distance(hero.Position, goal) < Distance(routes[0].End, goal) - 150.0,
                $"{context}: and the hero walks it ({hero.Position})");
            // The hand-back is the first order after the cast frame: it stops the cast clip a windup child leaves playing (Mystic Shot).
            TestAssert.False(hero.CastClipActive, $"{context}: the hand-back stopped the cast clip");
        }
    }

    /// <summary>
    /// The hand-back of fix 1 has the gates a click has: a stun inside the cast ends the postponed walk (the dispatcher refuses a click under
    /// a stun), and a route that is a cast approach (the cast ends a walk into range, or replaces a cast that was walking into range) is not
    /// a walk to hand back.
    /// </summary>
    [Check("attacks.b929-a-walk-is-not-handed-back-under-a-stun-or-after-a-cast-approach")]
    public static void AWalkIsNotHandedBackUnderAStunOrAfterACastApproach()
    {
        bool Walks(Match match, WireCapture capture, double from, double to) =>
            Routes(match, capture, from).Any(route => route.At < to && route.Points >= 2);
        // Every row runs and reports, so a red run names each gate that is missing.
        var failures = new List<string>();
        void Row(string name, Action body)
        {
            try { body(); }
            catch (InvalidOperationException error) { failures.Add($"{name}: {error.Message}"); }
        }
        Row("stun", () =>
        {
            using var match = Duel("Nunu", 450.0, out var capture);
            var hero = match.Hero;
            Order(match, 2, hero.Position + new Position2(0, -900));
            match.Advance(5 * Step);
            var pressed = match.Now;
            TestAssert.True(match.Cast(2, match.Foe, at: match.Foe.Position), "Nunu E while walking");
            match.Advance(2 * Step);
            TestAssert.True(match.Runtime.Buffs.AddBuff(hero, "Stun", Amberline.Core.Buffs.BuffType.Stun, 1.0, match.Foe.NetId) is not null, "a stun in the cast");
            match.Advance(1.2);
            TestAssert.False(Walks(match, capture, pressed, pressed + 1.0), "no walk is handed back under the stun");
        });
        Row("approach end", () =>
        {
            using var match = Duel("Nunu", 800.0, out var capture);
            var hero = match.Hero;
            TestAssert.True(match.Cast(2, match.Foe, at: match.Foe.Position) && hero.PendingCast is not null, "Nunu E out of range walks into range");
            match.Advance(2 * Step);
            // The foe walks toward Nunu, so E fires long before the approach route's end.
            match.Runtime.Movement.SetMoveOrder(match.Foe.NetId, hero.Position, _ => true);
            for (var step = 0; step < 90 && hero.PendingCast is not null; step++) match.Advance(Step);
            match.Advance(Step);
            var cast = Sent(match, capture, SpellPackets.CastSpellAnswerOpcode, 0.0).FirstOrDefault(double.NaN);
            TestAssert.False(double.IsNaN(cast), "E fires at the end of its approach");
            match.Advance(1.0);
            TestAssert.False(Walks(match, capture, cast - 1e-6, cast + 1.0), "the rest of the approach is not handed back");
        });
        Row("replaced approach", () =>
        {
            // Annie's Disintegrate out of range walks into range; Summon: Tibbers pressed on the way, in range, replaces it.
            using var match = Duel("Annie", 800.0, out var capture);
            var hero = match.Hero;
            TestAssert.True(match.Cast(0, match.Foe, at: match.Foe.Position) && hero.PendingCast is not null,
                "Disintegrate out of range walks into range");
            match.Advance(3 * Step);
            TestAssert.True(hero.Waypoints.Count > 0 && hero.PendingCast is not null, "Annie walks toward the foe");
            var pressed = match.Now;
            TestAssert.True(match.Cast(3, at: hero.Position + new Position2(0, 200)), "Tibbers replaces the walking Disintegrate");
            match.Advance(Step);
            var cast = Sent(match, capture, SpellPackets.CastSpellAnswerOpcode, pressed - 1e-6).FirstOrDefault(double.NaN);
            TestAssert.False(double.IsNaN(cast), "Tibbers is cast at once");
            match.Advance(1.0);
            TestAssert.False(Walks(match, capture, cast - 1e-6, cast + 1.0), "the replaced approach is not handed back");
        });
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// Freeze 2: a click given during the hero's own dash (a locked dash: Tumble, Quickdraw) was dropped, so the hero stood after the landing (or
    /// walked on with the order the dash had postponed). The click is now held, the latest one wins, and it is applied at the landing.
    /// </summary>
    [Check("attacks.b929-click-during-own-dash-is-applied-at-the-landing")]
    public static void ClickDuringOwnDashIsAppliedAtTheLanding()
    {
        foreach (var before in new[] { "none", "move", "attack" })
        foreach (var during in new[] { "move", "attack" })
        {
            using var match = Duel("Vayne", 450.0, out var capture);
            var hero = match.Hero;
            var foe = match.Foe;
            if (before == "attack")
            {
                Order(match, 3, foe.Position, foe.NetId);
                match.Advance(0.8);
            }
            if (before == "move")
            {
                Order(match, 2, hero.Position + new Position2(-900, 0));
                match.Advance(3 * Step);
            }
            TestAssert.True(match.Cast(0, at: hero.Position + new Position2(0, 300)), $"{before}/{during}: Tumble is accepted");
            TestAssert.True(hero.ForcedMove is { Completed: false }, $"{before}/{during}: Tumble dashes");
            match.Advance(2 * Step);
            var clickAt = match.Now;
            var point = hero.Position + new Position2(400, -400);
            if (during == "move") Order(match, 2, point);
            else Order(match, 3, foe.Position, foe.NetId);
            var context = $"before {before}, a {during} click during the dash";
            TestAssert.True(hero.ForcedMove is not null && Routes(match, capture, clickAt - 1e-6).Count == 0,
                $"{context}: nothing replaces the dash on the client");
            for (var step = 0; step < 60 && hero.ForcedMove is not null; step++) match.Advance(Step);
            TestAssert.True(hero.ForcedMove is null, $"{context}: the dash lands");
            var landed = hero.Position;
            match.Advance(1.0);
            if (during == "move")
            {
                TestAssert.True(Distance(hero.Position, point) < Distance(landed, point) - 150.0,
                    $"{context}: the hero walks to the click after the landing ({landed} -> {hero.Position}, click {point})");
                TestAssert.Equal<uint?>(null, hero.AttackTarget, $"{context}: the click replaced the attack");
            }
            else
            {
                TestAssert.Equal<uint?>(foe.NetId, hero.AttackTarget, $"{context}: the attack click is the order after the landing");
                // Tumble arms its empowered attack, which leaves as a cast (the attack override's cast answer) instead of a 0x0D.
                TestAssert.True(Sent(match, capture, 0x0D, clickAt).Count + Sent(match, capture, SpellPackets.CastSpellAnswerOpcode, clickAt).Count > 0,
                    $"{context}: and the hero swings");
            }
        }
    }

    /// <summary>
    /// The lost cast: a spell pressed during the hero's own locked dash is queued behind it (the stock client refuses a play under the dash's
    /// clip lock) and fires at the landing; a click given after that press dropped it. Retail casts a spell pressed during a dash at once
    /// (batch 663), so the click comes after the cast: the queued spell fires at the landing and the click is applied after its cast frame.
    /// Graves (132): Quickdraw, then Buckshot, then a click, all inside the 0.37 s dash.
    /// </summary>
    [Check("attacks.b929-cast-queued-behind-a-dash-survives-a-later-click")]
    public static void CastQueuedBehindADashSurvivesALaterClick()
    {
        var rows = new List<(string Champion, int Dash, Func<Match, Position2> DashAt, int Slot, bool UnitTarget)>
        {
            // Tumble toward the foe, so Condemn is in range at the landing.
            ("Vayne", 0, m => m.Hero.Position + new Position2(300, 0), 2, true),
        };
        if (GameData.Default.OptionalChampion("Graves") is not null)
            rows.Add(("Graves", 2, m => m.Hero.Position + new Position2(0, 400), 0, false));
        foreach (var (champion, dash, dashAt, slot, unitTarget) in rows)
        {
            using var match = Duel(champion, 450.0, out var capture);
            var hero = match.Hero;
            TestAssert.True(match.Cast(dash, at: dashAt(match)), $"{champion}: the dash is accepted");
            match.Advance(2 * Step);
            TestAssert.True(match.Cast(slot, unitTarget ? match.Foe : null, at: match.Foe.Position) && hero.PendingCast is not null,
                $"{champion}: slot {slot} pressed in the locked dash queues behind it");
            match.Advance(2 * Step);
            var clickAt = match.Now;
            var point = hero.Position + new Position2(-400, 300);
            Order(match, 2, point);
            for (var step = 0; step < 60 && hero.ForcedMove is not null; step++) match.Advance(Step);
            var landed = hero.Position;
            match.Advance(1.2);
            var casts = Sent(match, capture, SpellPackets.CastSpellAnswerOpcode, clickAt);
            TestAssert.True(casts.Count > 0, $"{champion}: the queued slot {slot} still fires after the landing");
            var walk = Routes(match, capture, clickAt).FirstOrDefault(route => route.Points >= 2);
            TestAssert.True(walk.Points >= 2 && walk.At >= casts[0] - 1e-6, $"{champion}: the click's walk comes after the cast ({walk.At:0.000} vs {casts[0]:0.000})");
            TestAssert.True(Distance(hero.Position, point) < Distance(landed, point) - 150.0,
                $"{champion}: and the hero walks to the click ({landed} -> {hero.Position}, click {point})");
        }
    }

    /// <summary>
    /// Orbwalking (NOT REPRODUCED, pinned): a move ordered after the swing's cast frame keeps the hit (no 0x39 between the move and the next
    /// swing, no stop of the walk), and an attack ordered again starts the next swing as soon as the period allows. A ranged and a melee
    /// champion, the move at every third step from the cast frame to the end of the period.
    /// </summary>
    [Check("attacks.b929-orbwalk-move-after-the-hit-keeps-the-hit-and-the-period")]
    public static void OrbwalkMoveAfterTheHitKeepsTheHitAndThePeriod()
    {
        foreach (var (champion, range) in new[] { ("Tristana", 450.0), ("Nunu", 150.0) })
        for (var offset = 0; ; offset += 3)
        {
            using var match = Duel(champion, range, out var capture);
            var hero = match.Hero;
            Order(match, 3, match.Foe.Position, match.Foe.NetId);
            for (var step = 0; step < 90 && Sent(match, capture, 0x0D, 0.0).Count < 2; step++) match.Advance(Step);
            var t0 = Sent(match, capture, 0x0D, 0.0)[^1];
            var (period, windup) = (hero.AttackPeriod, hero.AttackWindup);
            var moveAt = t0 + windup + Step + offset * Step;
            if (moveAt > t0 + period) break;
            while (match.Now < moveAt - 1e-9) match.Advance(Step);
            Order(match, 2, hero.Position + new Position2(0, -250));
            var moved = match.Now;
            match.Advance(4 * Step);
            var attackAt = match.Now;
            Order(match, 3, match.Foe.Position, match.Foe.NetId);
            for (var step = 0; step < 90 && Sent(match, capture, 0x0D, t0).Count == 0; step++) match.Advance(Step);
            var context = $"{champion}: move {offset} steps after the cast frame of the swing of {t0:0.000}";
            var next = Sent(match, capture, 0x0D, t0).FirstOrDefault(double.NaN);
            TestAssert.True(Sent(match, capture, 0x39, t0).All(at => at > next - 1e-9), $"{context}: no stop of the swing that hit");
            var walk = Routes(match, capture, moved - 1e-6);
            TestAssert.True(walk.Count > 0 && walk[0].Points >= 2 && walk[0].At <= moved + Step + 1e-6, $"{context}: the move walks at once");
            TestAssert.True(walk.Where(route => route.At < attackAt - 1e-9).All(route => route.Points >= 2), $"{context}: nothing stops the walk");
            TestAssert.True(next <= Math.Max(t0 + period, attackAt) + 2 * Step + 1e-6,
                $"{context}: the next swing at {next:0.000} waits past the period end {t0 + period:0.000} and the click {attackAt:0.000}");
        }
    }
}
