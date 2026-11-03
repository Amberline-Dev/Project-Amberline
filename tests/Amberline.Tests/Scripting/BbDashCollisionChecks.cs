using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 377 (27 October 2026): Body Slam's end area (owner finding 26) and Shadow Dash against units (owner finding 99), through the production
/// runtime. The shipped scripts are the truth: Body Slam deals its damage in <c>BuffOnMoveEnd</c> (<c>BBForEachUnitInTargetArea</c> Range 250 centred on
/// Gragas) after an arrival or a collision stop, and its collision hook only stops the dash. Shadow Dash re-issues no move on contact (the dash stays
/// 575 and starts once) and taunts units within 200 of its end. These checks lock that measured behaviour; they pass on the unchanged code.
/// </summary>
public static class BbDashCollisionChecks
{
    [Check("scripting.bb-gragas-body-slam-end-area-is-250-from-gragas")]
    public static void BodySlamEndAreaIsCentredOnGragas()
    {
        // The foe stands d off the dash line, level with the end point (7600): the dash passes it without touching it.
        foreach (var (d, hit) in new[] { (240.0, true), (300.0, false), (450.0, false) })
        {
            var match = Setup("Gragas", new Position2(7600.0, 7000.0 + d));
            match.Advance(0.8);
            TestAssert.True(match.Cast(2, at: new Position2(7600.0, 7000.0)), "Body Slam is cast toward a point 600 units out");
            match.Advance(2.0);
            var hits = HitsOn(match, "Foe").Count;
            TestAssert.True(hit ? hits == 1 : hits == 0,
                $"a foe {d} units off the end point is {(hit ? "hit" : "not hit")} by the end area (hits {hits}; the area is 250 from Gragas)");
        }
    }

    [Check("scripting.bb-gragas-body-slam-contact-stop-hits-units-within-250-of-the-stop")]
    public static void BodySlamContactStopHitsWhatIsNearTheStop()
    {
        // A foe 380 out on the line stops the dash at contact (Gragas stops near 290). A second foe 150 or 300 off the line, level with the
        // stop point, is inside the end area or outside it. The touched foe is hit by the same end area.
        foreach (var (d, hit) in new[] { (150.0, true), (300.0, false) })
        {
            var match = Setup("Gragas", new Position2(7380.0, 7000.0));
            match.FarFoe.Position = new Position2(7250.0, 7000.0 + d);
            match.Runtime.Movement.SetMoveOrder(match.FarFoe.NetId, match.FarFoe.Position);
            match.Advance(0.8);
            TestAssert.True(match.Cast(2, at: new Position2(7600.0, 7000.0)), "Body Slam is cast toward a point 600 units out");
            match.Advance(2.0);
            var forward = match.Hero.Position.X - 7000.0;
            TestAssert.True(forward is > 250.0 and < 340.0, $"the dash stops at the touched foe (forward {forward:0.##})");
            TestAssert.True(HitsOn(match, "Foe").Count == 1, "the touched foe takes the end area's hit");
            var hits = HitsOn(match, "FarFoe").Count;
            TestAssert.True(hit ? hits == 1 : hits == 0,
                $"a foe {d} units off the line at the stop point is {(hit ? "hit" : "not hit")} (hits {hits}; the area is 250 from Gragas)");
        }
    }

    [Check("scripting.bb-shen-shadow-dash-keeps-575-and-starts-once-when-it-hits-a-unit")]
    public static void ShadowDashKeepsItsLengthOnAHit()
    {
        // Standing champions 150, 300 and 450 out, a minion 300 out, and a champion walking across the line at 150: each is taunted on contact,
        // and the dash runs its 575 as one move (the engine starts no second move on a collision).
        foreach (var offset in new[] { 150.0, 300.0, 450.0 })
        {
            var match = Setup("Shen", new Position2(7000.0 + offset, 7000.0), hero: new Position2(7000.0, 7000.0));
            match.Advance(0.5);
            var (length, starts, tauntAt) = ShadowDash(match, match.Foe);
            TestAssert.Near(575.0, length, 1.0, $"a champion {offset} out: the dash runs 575 (got {length:0.##})");
            TestAssert.True(starts == 1, $"a champion {offset} out: one dash move is started (got {starts})");
            TestAssert.True(!double.IsNaN(tauntAt), $"a champion {offset} out: the champion is taunted on contact");
        }
        {
            var match = Setup("Shen", new Position2(9000.0, 9000.0), hero: new Position2(7000.0, 7000.0));
            var minion = Enemy(match, "Minion", new Position2(7300.0, 7000.0), UnitType.Minion);
            minion.Radius = 48.0;
            match.Runtime.Movement.SetMoveOrder(minion.NetId, minion.Position);
            match.Advance(0.5);
            var (length, starts, tauntAt) = ShadowDash(match, minion);
            TestAssert.Near(575.0, length, 1.0, $"a minion 300 out: the dash runs 575 (got {length:0.##})");
            TestAssert.True(starts == 1 && !double.IsNaN(tauntAt), $"a minion 300 out: one dash move, and the minion is taunted (starts {starts}, taunt {tauntAt:0.00})");
        }
        {
            var match = Setup("Shen", new Position2(7350.0, 7150.0), hero: new Position2(7000.0, 7000.0));
            match.Advance(0.5);
            // The walk starts at the cast, so the champion crosses the dash line during the dash (the timing the batch 377 probe measured).
            match.Runtime.Movement.SetMoveOrder(match.Foe.NetId, new Position2(7350.0, 6850.0));
            var (length, starts, tauntAt) = ShadowDash(match, match.Foe);
            TestAssert.Near(575.0, length, 1.0, $"a champion crossing the line at 150: the dash runs 575 (got {length:0.##})");
            TestAssert.True(starts == 1 && !double.IsNaN(tauntAt), $"a champion crossing the line: one dash move, and it is taunted (starts {starts}, taunt {tauntAt:0.00})");
        }
    }

    [Check("scripting.bb-shen-shadow-dash-end-taunts-within-200-of-its-end")]
    public static void ShadowDashEndAreaTauntsWhatIsNearItsEnd()
    {
        // The end area (BuffOnMoveEnd, Range 200 around Shen) taunts a foe 150 past the 575 end and not one 250 past it.
        foreach (var (beyond, taunted) in new[] { (150.0, true), (250.0, false) })
        {
            var match = Setup("Shen", new Position2(7000.0 + 575.0 + beyond, 7000.0), hero: new Position2(7000.0, 7000.0));
            match.Advance(0.5);
            var (length, _, tauntAt) = ShadowDash(match, match.Foe);
            TestAssert.Near(575.0, length, 1.0, $"the dash runs 575 toward a point 700 out (got {length:0.##})");
            TestAssert.True(double.IsNaN(tauntAt) != taunted,
                $"a foe {beyond} past the end is {(taunted ? "taunted" : "not taunted")} by the end area (taunt at {tauntAt:0.00})");
        }
    }

    [Check("scripting.bb-shen-shadow-dash-unit-cast-ends-on-the-unit")]
    public static void ShadowDashCastOnAUnitEndsOnIt()
    {
        // A cast on a champion 300 out: the cast point is the champion's, so the dash ends there (299), not at the 575 cap.
        var match = Setup("Shen", new Position2(7300.0, 7000.0), hero: new Position2(7000.0, 7000.0));
        match.Advance(0.5);
        var start = match.Hero.Position;
        TestAssert.True(match.Cast(2, match.Foe), "Shadow Dash is cast on the champion 300 out");
        match.Advance(2.0);
        var length = Distance(start, match.Hero.Position);
        TestAssert.True(length is > 290.0 and < 305.0, $"the dash ends on the unit's point, 300 out (got {length:0.##})");
    }

    // A match with Shen (or Gragas) at the hero spot and the foe at foe; the ally and the far foe are moved off the lanes, and the foe stands still.
    private static Match Setup(string champion, Position2 foe, Position2? hero = null)
    {
        var match = new Match(champion);
        match.Learn(1);
        match.Hero.Position = hero ?? new Position2(7000.0, 7000.0);
        match.Ally.Position = new Position2(6000.0, 9000.0);
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        match.Foe.Position = foe;
        match.Foe.SpellBlock = 0.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        match.Runtime.Movement.SetMoveOrder(match.Foe.NetId, foe);
        return match;
    }

    // Casts Shadow Dash 700 out (the script caps it at 575) and samples each spell step: the length, the number of moves the engine started
    // (a new ForcedMoveState object each), and when the unit first shows a taunt (NaN if never).
    private static (double Length, int Starts, double TauntAt) ShadowDash(Match match, Entity unit)
    {
        var start = match.Hero.Position;
        var castAt = match.Now;
        match.Cast(2, at: new Position2(start.X + 700.0, start.Z));
        object? previous = null;
        var starts = 0;
        var tauntAt = double.NaN;
        for (var step = 0; step < 90; step++)
        {
            match.Advance(Step);
            var move = match.Hero.ForcedMove;
            if (move is not null && !ReferenceEquals(move, previous))
            {
                starts++;
                previous = move;
            }
            if (double.IsNaN(tauntAt) && Buff(unit, "Taunt") is not null) tauntAt = match.Now - castAt;
        }
        return (Distance(start, match.Hero.Position), starts, tauntAt);
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
