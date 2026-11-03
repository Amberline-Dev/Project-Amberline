using Amberline.Core;
using Amberline.Data;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 659 (community reports of 3 November 2026): Riven's "Q movement not calculated correctly ... she dashes way too fast especially if
/// its the close target branch", Riven's "E > R > W cancel not possible?" and Lee Sin's "W on target close to wall fails".
/// <para>
/// The first and the third are one rule of <c>BBMove</c>: <c>IdealDistance</c> is the length the call's <c>Speed</c> is given for, so a
/// move of another length scales its speed by requested / ideal and always lasts ideal / speed (Reconstruction from the shipped call
/// sites: Broken Wings asks for at most 240 with 275 and 375, Fling's landing is 420 behind Singed with 420; a cap would make those numbers
/// dead). Lee Sin's Safeguard passes the gap between the bodies as its
/// <c>IdealDistance</c> and the target's centre as its point, so the cap stopped him a body short of the ward, inside the wall the ward
/// stood behind.
/// </para>
/// </summary>
public static class RivenLeeDashChecks
{
    private const double Step = 1.0 / 30.0;

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>Steps the match until the hero's dash has started and ended: the dash's speed and how long it ran.</summary>
    private static (double Speed, double Seconds) WatchDash(Match match, Entity hero, double within = 1.5)
    {
        double? started = null;
        var speed = double.NaN;
        for (var elapsed = 0.0; elapsed < within; elapsed += Step)
        {
            match.Advance(Step);
            if (started is null && hero.ForcedMove is { } move)
            {
                started = match.Now - Step;
                speed = move.Speed;
            }
            else if (started is not null && hero.ForcedMove is null) return (speed, match.Now - Step - started.Value);
        }
        throw new InvalidOperationException($"no dash that started and ended within {within} s (started {started})");
    }

    [Check("spells-live-broken-wings-dash-lasts-ideal-distance-over-speed-near-or-far")]
    public static void BrokenWingsDashTime()
    {
        var cleave = Script("RivenTriCleave");
        var moves = Blocks(cleave, "SelfExecute").Where(block => block.Function == "BBMove").ToArray();
        TestAssert.Equal(3, moves.Length, "one BBMove per strike");
        TestAssert.Equal("825,275,15|825,275,15|900,375,15",
            string.Join("|", moves.Select(move => $"{move.Number("Speed")},{move.Number("IdealDistance")},{move.Number("MoveBackBy")}")),
            "the shipped speed, ideal distance and step-back of the three strikes");
        using var match = new Match("Riven");
        var hero = match.Hero;
        var foe = match.Foe;
        match.Learn(5);
        match.Advance(0.2);
        foe.Position = new Position2(9000, 9000);
        hero.Position = new Position2(7000, 7000);
        hero.Facing = new Position2(1, 0);

        // Far: no unit at the cursor, so the strike asks for 240 along the facing less the 15 it steps back.
        TestAssert.True(match.Cast(0, at: new Position2(7600, 7000)), "the open-floor strike is accepted");
        var (speed, seconds) = WatchDash(match, hero);
        TestAssert.Near(825.0 * 225.0 / 275.0, speed, 0.5, "225 of the ideal 275: the speed is 825 x 225 / 275");
        TestAssert.Near(275.0 / 825.0, seconds, 2 * Step, "and the dash lasts its ideal 275 / 825 s");

        // Near: the lock-on branch at a standing foe asks for the gap less 25 (at least 25), less the 15 it steps back.
        match.Advance(0.6);
        hero.SpellCooldowns[0] = 0;
        hero.Facing = new Position2(1, 0);
        foe.Position = new Position2(hero.Position.X + 200, hero.Position.Z);
        var gap = 200.0 - hero.CombatRadius - foe.CombatRadius;
        var requested = Math.Max(gap - 25.0, 25.0) - 15.0;
        TestAssert.True(match.Cast(0, foe, at: foe.Position), "the lock-on strike is accepted");
        (speed, seconds) = WatchDash(match, hero);
        TestAssert.Near(825.0 * requested / 275.0, speed, 0.5, $"a {requested:0.0}-unit strike at a close foe slows to 825 x {requested:0.0} / 275");
        TestAssert.Near(275.0 / 825.0, seconds, 2 * Step, "and still lasts 275 / 825 s instead of a blink");

        // The third strike: 375 at 900 (the higher hop), the same 225 asked.
        match.Advance(0.6);
        hero.SpellCooldowns[0] = 0;
        hero.Facing = new Position2(1, 0);
        foe.Position = new Position2(9000, 9000);
        TestAssert.True(match.Cast(0, at: new Position2(hero.Position.X + 600, hero.Position.Z)), "the third strike is accepted");
        (speed, seconds) = WatchDash(match, hero);
        TestAssert.Near(900.0 * 225.0 / 375.0, speed, 0.5, "the third strike: 900 x 225 / 375");
        TestAssert.Near(375.0 / 900.0, seconds, 2 * Step, "the third strike lasts 375 / 900 s");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    [Check("spells-live-riven-valor-then-blade-then-ki-burst-all-take-effect-in-order")]
    public static void ValorBladeKiBurst()
    {
        using var match = new Match("Riven");
        var hero = match.Hero;
        var foe = match.Foe;
        match.Learn(3);
        match.Advance(0.2);
        hero.Position = new Position2(7000, 7000);
        hero.Facing = new Position2(1, 0);
        // The foe stands 330 past Valor's 250: inside Ki Burst's 360 with Blade of the Exile up, outside its plain 300.
        foe.Position = new Position2(7000 + 250 + 330, 7000);
        // Batch 663 (owner, 3 November 2026, "Cast at once"): Valor's clip is unlocked, so the presses are cast at once instead of queueing
        // to the landing; they go through the client's path, where Ki Burst pressed inside the blade's uncancellable windup is held to its
        // cast frame (the dispatcher's windup hold, owner decision of 18 October 2026), which is what gives it the blade's 360.
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 2, new Position2(7800, 7000)), "Valor is accepted");
        for (var step = 0; step < 15 && hero.ForcedMove is null; step++) match.Advance(Step);
        TestAssert.True(hero.ForcedMove is not null, "Valor's dash is running");
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 3, hero.Position), "Blade of the Exile is accepted during the dash");
        TestAssert.True(hero.PendingCast is null, "and is not queued behind it");
        match.Advance(Step);
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 1, hero.Position), "Ki Burst is accepted during the dash");
        match.Advance(1.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "RivenFengShuiEngine"), "Blade of the Exile took effect");
        TestAssert.Equal("RivenIzunaBlade", match.Module.SpellName(hero, 3), "the R slot now holds Wind Slash");
        TestAssert.True(hero.SpellCooldowns[1] > 0.0, "Ki Burst was cast");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("exile_W_cast_ult_01.troy", StringComparison.Ordinal)),
            "Ki Burst came after Blade of the Exile (its ult branch): " + string.Join(" | ", match.Log.Effects));
        TestAssert.True(foe.Buffs.Any(buff => buff.Name == "RivenMartyrDebuff"), "and its 360 reached the foe 330 away");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    [Check("spells-live-safeguard-reaches-an-ally-just-past-a-wall")]
    public static void SafeguardPastAWall()
    {
        using var match = new Match("LeeSin");
        var hero = match.Hero;
        var ally = match.Ally;
        var grid = match.Runtime.Navigation;
        var movement = match.Runtime.Movement;
        match.Learn(5);
        match.Advance(0.2);
        // A wall right behind the ally as Lee sees it: the ally's centre is open floor, the strip from 40 to 140 units back toward Lee is
        // wall, and Lee stands on open floor 260 back. The touching point a body short of the ally lies inside that strip.
        Position2? found = null;
        var direction = default(Position2);
        foreach (var dir in new[] { new Position2(1, 0), new Position2(-1, 0), new Position2(0, 1), new Position2(0, -1) })
        {
            if (found is not null) break;
            for (var column = 20; column < grid.CountX - 20 && found is null; column += 2)
            for (var row = 20; row < grid.CountY - 20 && found is null; row += 2)
            {
                var spot = movement.ClientAnchor(grid.CellToWorld(new NavCell(column, row)));
                Position2 Back(double by) => new(spot.X - dir.X * by, spot.Z - dir.Z * by);
                if (grid.PointClearance(spot) < movement.MovementRadius(hero) + 6.0) continue;
                if (Enumerable.Range(4, 11).Any(k => grid.IsMovementWalkable(Back(k * 10.0)))) continue;
                var lee = Back(260.0);
                if (grid.PointClearance(lee) < movement.MovementRadius(hero) + 6.0) continue;
                found = spot;
                direction = dir;
            }
        }
        TestAssert.True(found is not null, "Map1 has an open spot with a 100-unit wall 40 units behind it");
        var target = found!.Value;
        ally.Position = target;
        hero.Position = new Position2(target.X - direction.X * 260.0, target.Z - direction.Z * 260.0);
        hero.Facing = direction;
        TestAssert.True(match.Cast(1, ally), "Safeguard on the ally is accepted");
        match.Advance(1.0);
        TestAssert.True(Distance(hero.Position, target) <= 40.0,
            $"Lee Sin lands on the ally past the wall ({Distance(hero.Position, target):0.0} units from it)");
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"), "the ally is shielded");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"), "and so is Lee Sin");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    [Check("spells-live-safeguard-reaches-an-ally-hugging-a-wall")]
    public static void SafeguardToAnAllyHuggingAWall()
    {
        using var match = new Match("LeeSin");
        var hero = match.Hero;
        var ally = match.Ally;
        var grid = match.Runtime.Navigation;
        var movement = match.Runtime.Movement;
        match.Learn(5);
        match.Advance(0.2);
        var body = movement.MovementRadius(hero);
        // The ally stands 15 to 40 units from a wall; Lee Sin stands on open floor 300 away, and the whole way between them is floor (no wall
        // to cross), but its last 60 units are closer to the wall than his body.
        Position2? found = null;
        var direction = default(Position2);
        var dirs = Enumerable.Range(0, 16).Select(i => new Position2(Math.Cos(i * Math.PI / 8), Math.Sin(i * Math.PI / 8))).ToArray();
        for (var column = 20; column < grid.CountX - 20 && found is null; column += 3)
        for (var row = 20; row < grid.CountY - 20 && found is null; row += 3)
        {
            var spot = movement.ClientAnchor(grid.CellToWorld(new NavCell(column, row)));
            var clearance = grid.PointClearance(spot);
            if (!grid.IsMovementWalkable(spot) || clearance < 15.0 || clearance > 40.0) continue;
            foreach (var dir in dirs)
            {
                Position2 Back(double by) => new(spot.X - dir.X * by, spot.Z - dir.Z * by);
                if (grid.PointClearance(Back(300.0)) < body + 6.0) continue;
                if (Enumerable.Range(0, 31).Any(k => !grid.IsMovementWalkable(Back(k * 10.0)))) continue;
                if (Enumerable.Range(0, 7).Any(k => grid.PointClearance(Back(k * 10.0)) >= body + 4.0)) continue;
                found = spot;
                direction = dir;
                break;
            }
        }
        TestAssert.True(found is not null, "Map1 has an ally spot 15 to 40 units from a wall with open floor 300 back");
        var target = found!.Value;
        ally.Position = target;
        hero.Position = new Position2(target.X - direction.X * 300.0, target.Z - direction.Z * 300.0);
        hero.Facing = direction;
        TestAssert.True(match.Cast(1, ally), "Safeguard on the ally is accepted");
        match.Advance(1.0);
        // He lands on the ally, or at most one body clearance (his radius + 4) short of it where his body clears the wall.
        TestAssert.True(Distance(hero.Position, target) <= body + 4.0 + 10.0,
            $"Lee Sin reaches the ally beside the wall ({Distance(hero.Position, target):0.0} units from it)");
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"), "the ally is shielded");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }
}
