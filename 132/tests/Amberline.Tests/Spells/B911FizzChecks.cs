using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 911 (19 November 2026, owner on 1.0.0.132: "Fizz still autoattacks when using E (Fizz should be completely prevented from attacking after
/// the first E usage)"; "Fizz Q still sometimes makes him run really fast instead of playing the dashing animation").
/// <list type="bullet">
/// <item>E: every swing source (an order held from before E, auto-acquisition on the spear, a click on the spear or in the slam, a bot) swung at
/// the slam's start: <c>FizzJumpDelay</c>'s deactivation writes CanAttack true before it plays the locked slam clip, and the hit landed before
/// the slam did. A locked clip a deactivation plays now holds the swing until its unlock, the <c>UnlockAnimation</c> FizzTrickSlam's end adds
/// (retail's unlock native sets the CanAttack bit, <c>BbNativeHandlers.UnlockAnimation</c>).</item>
/// <item>Q: a windup cast (R) pressed in the dash sent a one-point stop after the dash's 0x68, and an enemy that first saw Fizz mid-dash got no
/// Spell1 clip.</item>
/// </list>
/// </summary>
public static class B911FizzChecks
{
    private static bool Cast(Match match, int slot, Position2 point, uint target = 0)
    {
        var payload = new byte[34];
        payload[0] = 0xA3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), match.Hero.NetId);
        payload[5] = checked((byte)(slot << 1));
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset, 4), (float)point.X);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 8, 4), (float)point.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target);
        return match.Runtime.Session.DispatchGameplay(1, 1, payload, match.Runtime.Dependencies.Gameplay);
    }

    private static bool Order(Match match, byte type, Position2 point, uint target = 0)
    {
        var order = ClientGameplayPackets.MoveOrder(match.Hero.NetId, type, point, target);
        return match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay);
    }

    private static uint About(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 5) return 0u;
        return span[0] == VisionPackets.DashOpcode && span.Length >= 19
            ? BinaryPrimitives.ReadUInt32LittleEndian(span[15..])
            : BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
    }

    private static string Clip(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span[0] != 0xB9) return "";
        var end = span[13..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(span.Slice(13, end < 0 ? span.Length - 13 : end));
    }

    private static string Show(IEnumerable<(double At, PlaintextPacket Packet)> packets, double start) =>
        string.Join(" ", packets.Select(entry => $"{entry.At - start:0.000}:" +
            (entry.Packet.Plaintext.Span[0] == 0xB9 ? "B9:" + Clip(entry.Packet) : entry.Packet.Plaintext.Span[0].ToString("X2"))));

    private sealed record ECase(string Name, bool AutoAcquire = false, bool HeldOrder = false, double ClickAt = -1.0, double RecastAt = -1.0);

    private static readonly ECase[] ECases =
    [
        new("auto-acquisition on, the foe where Fizz lands", AutoAcquire: true),
        new("an attack order held from before E", HeldOrder: true),
        new("an attack click on the spear", ClickAt: 0.55),
        new("an attack click in the slam", ClickAt: 0.75),
        new("an attack order held from before E, recast on the spear", HeldOrder: true, RecastAt: 0.55),
    ];

    /// <summary>
    /// From the E cast to the 0x2B that ends the ability (the slam's unlock, or the second landing's on a recast) neither client gets a 0x0D of
    /// Fizz and no basic attack winds up on the server; the swing comes after that unlock. Anchored on the packet, not on a time.
    /// </summary>
    [Check("spells-live-fizz-playful-trickster-holds-every-swing-until-the-slam-unlocks")]
    public static void PlayfulTricksterHoldsEverySwingUntilTheUnlock()
    {
        foreach (var item in ECases)
        {
            var (match, own, enemy) = B885FizzPlayfulTricksterChecks.Arena();
            using var scope = match;
            var hero = match.Hero;
            hero.HeroAutoAcquire = item.AutoAcquire;
            if (item.HeldOrder)
            {
                var before = own.Packets.Count;
                TestAssert.True(Order(match, 3, match.Foe.Position, match.Foe.NetId), $"{item.Name}: the attack order");
                for (var step = 0; step < 60 && !own.Packets.Skip(before).Any(entry => entry.Packet.Plaintext.Span[0] == 0x0D); step++) match.Advance(Step);
                match.Advance(0.7);
                TestAssert.False(match.Runtime.Combat.IsWindingUpOn(hero, match.Foe), $"{item.Name}: no swing winds up at the cast");
            }
            var start = match.Now;
            var from = new[] { own.Packets.Count, enemy.Packets.Count };
            TestAssert.True(B885FizzPlayfulTricksterChecks.Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), $"{item.Name}: Playful is accepted");
            var clicked = item.ClickAt < 0.0;
            var recast = item.RecastAt < 0.0;
            var windups = new List<double>();
            for (var step = 0; step < 75; step++)
            {
                var t = match.Now - start;
                if (!clicked && t >= item.ClickAt - 1e-6) { clicked = true; Order(match, 3, match.Foe.Position, match.Foe.NetId); }
                if (!recast && t >= item.RecastAt - 1e-6)
                {
                    recast = true;
                    TestAssert.True(Cast(match, 2, new Position2(hero.Position.X + 300.0, hero.Position.Z)), $"{item.Name}: Trickster is accepted on the spear");
                }
                match.Advance(Step);
                if (match.Runtime.Combat.IsWindingUpOn(hero, match.Foe)) windups.Add(match.Now);
            }
            for (var index = 0; index < 2; index++)
            {
                var (who, packets) = index == 0 ? ("Fizz's client", own.Packets) : ("the enemy's client", enemy.Packets);
                var about = packets.Skip(from[index]).Where(entry => About(entry.Packet) == hero.NetId).ToList();
                // The ability's last clip: the locked slam (Spell3c), or the second jump (Spell3d) on a recast. Its end is the next 0x2B.
                var last = about.FindLastIndex(entry => Clip(entry.Packet) is "Spell3c" or "Spell3d");
                var unlock = last < 0 ? -1 : about.FindIndex(last, entry => entry.Packet.Plaintext.Span[0] == 0x2B);
                TestAssert.True(last >= 0 && unlock > last, $"{item.Name}: {who} gets the ability's last clip and its unlock: {Show(about, start)}");
                var early = about.Take(unlock).Where(entry => entry.Packet.Plaintext.Span[0] == 0x0D).ToList();
                TestAssert.Equal(0, early.Count, $"{item.Name}: {who} gets no swing (0x0D) of Fizz before the unlock that ends E: {Show(about, start)}");
                var swing = about.FindIndex(unlock, entry => entry.Packet.Plaintext.Span[0] == 0x0D);
                TestAssert.True(swing > unlock && about[swing].At - about[unlock].At <= 1.0,
                    $"{item.Name}: {who} gets Fizz's swing after the unlock: {Show(about, start)}");
                if (index == 0)
                {
                    var unlockAt = about[unlock].At;
                    var serverEarly = windups.Where(at => at < unlockAt - 1e-6).Select(at => (at - start).ToString("0.000")).ToList();
                    TestAssert.Equal(0, serverEarly.Count, $"{item.Name}: no swing winds up on the server before the unlock: {string.Join(",", serverEarly)}");
                }
            }
            TestAssert.Equal(0, match.Module.ScriptFailures.Count,
                $"{item.Name}: no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
        }
    }

    /// <summary>Fizz at level 9 with every rank, an Orianna 450 east (out of reach), both clients watching.</summary>
    private static (Match Match, WireCapture Own, WireCapture Enemy) QArena(Position2? fizzAt = null, Position2? foeOffset = null)
    {
        var match = new Match("Fizz", foeChampion: "Orianna");
        var hero = match.Hero;
        hero.Level = 9;
        match.Learn(5);
        match.Refill();
        match.Runtime.HeroStats.Refresh(hero, push: false);
        if (fizzAt is { } at) hero.Position = at;
        var offset = foeOffset ?? new Position2(450.0, 0.0);
        match.Ally.Position = new Position2(hero.Position.X - 400.0, hero.Position.Z - 1500.0);
        match.Foe.Position = new Position2(hero.Position.X + offset.X, hero.Position.Z + offset.Z);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(match.Foe.NetId);
        view.Visible.Add(match.Foe.NetId);
        var own = Observe(match, Team.Order, hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.3);
        return (match, own, enemy);
    }

    /// <summary>
    /// R (a windup cast) pressed 0.1 s into Urchin Strike's dash goes at once (batch 663: the dash's clip is unlocked), and nothing in its cast
    /// replaces the dash on either client: no 0xC2 and no 0x39 for Fizz between the 0x68 and the landing's stop.
    /// </summary>
    [Check("spells-live-fizz-r-pressed-in-the-urchin-strike-dash-leaves-the-dash-alone")]
    public static void ChumTheWatersInTheDashLeavesTheDash()
    {
        var (match, own, enemy) = QArena();
        using var scope = match;
        var hero = match.Hero;
        var start = match.Now;
        var from = new[] { own.Packets.Count, enemy.Packets.Count };
        TestAssert.True(Cast(match, 0, match.Foe.Position, match.Foe.NetId), "Urchin Strike is accepted");
        for (var step = 0; step < 10 && hero.ForcedMove is null; step++) match.Advance(Step);
        TestAssert.True(hero.ForcedMove is { Effect: "FizzPiercingStrike" }, "the dash runs");
        match.Advance(0.1);
        TestAssert.True(hero.ForcedMove is { Effect: "FizzPiercingStrike" }, "still in the dash");
        TestAssert.True(Cast(match, 3, new Position2(hero.Position.X + 600.0, hero.Position.Z + 300.0)), "Chum the Waters is accepted in the dash");
        for (var step = 0; step < 30 && hero.ForcedMove is not null; step++) match.Advance(Step);
        TestAssert.True(hero.ForcedMove is null, "Fizz lands");
        for (var index = 0; index < 2; index++)
        {
            var (who, packets) = index == 0 ? ("Fizz's client", own.Packets) : ("the enemy's client", enemy.Packets);
            var about = packets.Skip(from[index]).Where(entry => About(entry.Packet) == hero.NetId).ToList();
            var dash = about.FindIndex(entry => entry.Packet.Plaintext.Span[0] == VisionPackets.DashOpcode);
            var landing = dash < 0 ? -1 : about.FindIndex(dash, entry => entry.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode);
            TestAssert.True(dash >= 0 && landing > dash, $"{who} gets the dash and a stop after it: {Show(about, start)}");
            TestAssert.True(about[landing].At - start >= 0.39, $"{who}: the first stop after the dash is the landing's, not the cast's: {Show(about, start)}");
            var casts = about.Take(landing).Count(entry => entry.Packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode);
            TestAssert.True(casts >= 2, $"{who} gets the R cast inside the dash: {Show(about, start)}");
            TestAssert.Equal(0, about.Skip(dash + 1).Take(landing - dash - 1).Count(entry => entry.Packet.Plaintext.Span[0] == 0x39),
                $"{who} gets no 0x39 for Fizz in the dash: {Show(about, start)}");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    /// <summary>
    /// Urchin Strike out of a brush: the enemy's client first sees Fizz mid-dash. Its reveal carries the dash, and the Spell1 clip the dash plays
    /// follows it, before the landing.
    /// </summary>
    [Check("spells-live-fizz-urchin-strike-first-seen-mid-dash-plays-its-clip")]
    public static void UrchinStrikeSeenFirstMidDashPlaysItsClip()
    {
        // The bot-lane brush (navigation-brush-edge-selection-radius-sight): Fizz inside, Orianna 420 north of him on open floor.
        var (match, own, enemy) = QArena(new Position2(7380.0, 564.8), new Position2(0.0, 420.0));
        using var scope = match;
        var hero = match.Hero;
        var seat = match.Runtime.Delivery.Observers.First(observer => observer.ObserverId == 9002);
        TestAssert.False(seat.View.Visible.Contains(hero.NetId), "the enemy's client does not see Fizz in the brush");
        var start = match.Now;
        var from = enemy.Packets.Count;
        TestAssert.True(Cast(match, 0, match.Foe.Position, match.Foe.NetId), "Urchin Strike is accepted");
        for (var step = 0; step < 30 && !(hero.ForcedMove is not null && seat.View.Visible.Contains(hero.NetId)); step++) match.Advance(Step);
        TestAssert.True(hero.ForcedMove is { Effect: "FizzPiercingStrike" } && seat.View.Visible.Contains(hero.NetId), "the enemy sees Fizz mid-dash");
        for (var step = 0; step < 30 && hero.ForcedMove is not null; step++) match.Advance(Step);
        var about = enemy.Packets.Skip(from).Where(entry => About(entry.Packet) == hero.NetId).ToList();
        var reveal = about.FindIndex(entry => entry.Packet.Plaintext.Span[0] == VisionPackets.EnterVisibilityOpcode);
        var landing = reveal < 0 ? -1 : about.FindIndex(reveal, entry => entry.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode);
        TestAssert.True(reveal >= 0 && landing > reveal, $"the enemy's client gets the reveal, then the landing: {Show(about, start)}");
        var clip = about.FindIndex(reveal, entry => Clip(entry.Packet) == "Spell1");
        TestAssert.True(clip > reveal && clip < landing, $"the enemy's client gets Spell1 after the reveal and before the landing: {Show(about, start)}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
