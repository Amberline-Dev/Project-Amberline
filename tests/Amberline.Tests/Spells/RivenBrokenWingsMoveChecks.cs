using Amberline.Core;
using Amberline.Data;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 168 part 3, owner finding 16 (20 October 2026): "Riven's Q feels disconnected before its ground effect and the end of its
/// animation; near a wall the animation is cancelled and the ground effect is sometimes dropped."
/// <para>
/// Broken Wings is the shipped <c>Spells/RivenTriCleave.luaobj</c> <c>SelfExecute</c>: it adds <c>RivenTriCleaveDamage</c> (whose
/// <c>BuffOnMoveSuccess</c> draws the detonate and hits what the strike marks), then <c>UnlockAnimation</c> (0.75 s, the clip's lock),
/// then casts its own <c>BBMove</c> (<c>FURTHEST_WITHIN_RANGE</c>, speed 900). The spell's own hook started the move, so the move's
/// hooks belong to the buffs the hook added after it; a press whose landing search comes back to the origin (a wall right in front)
/// used to take the "already there" shortcut, which skipped <c>Track</c> altogether: no move hooks, so no detonate and no
/// <c>RivenTriCleaveUnlock</c> (whose <c>BuffOnDeactivate</c> clears <c>UnlockAnimation</c>) — the clip stayed locked for its own
/// 0.75 s and the strike drew nothing. Both are the same root (<see cref="BbScriptMoves"/> track the zero-length move like any other);
/// the check reads them from the production runtime, and reads the clip's own figures off the shipped script.
/// </para>
/// <para>
/// The "disconnected before its ground effect and the end of its animation" half is the 0.25 s cast plus the dash's own flight: the
/// detonate is the <c>BuffOnMoveSuccess</c> that runs at the landing, and the animation is the <c>UnlockAnimation</c> buff's 0.75 s
/// from the same instant, so the check pins both on that one landing rather than at the cast.
/// </para>
/// </summary>
public static class RivenBrokenWingsMoveChecks
{
    /// <summary>One cast on <paramref name="hero"/>; the landing the shipped move asks for is the same search the engine runs.</summary>
    private static void Cast(Match match, Entity hero)
    {
        match.Learn(1);
        match.Refill();
        match.Advance(0.2);
    }

    [Check("spells-live-broken-wings-into-a-wall-still-detonates-and-unlocks-its-clip")]
    public static void BrokenWingsIntoAWallStillDrawsAndUnlocks()
    {
        using var match = new Match("Riven");
        var hero = match.Hero;
        var foe = match.Foe;
        var grid = match.Runtime.Navigation;
        // The owner's wall case is the lock-on one: a foe standing a body's width away with terrain right behind it. The script
        // then asks for a 10-unit dash (gap - 25, floored at the 25 of the facing offset, less MoveBackBy 15), and the landing
        // search has nowhere to go: the wall is inside the request, so every candidate is unwalkable and it falls back to the
        // origin. That is the zero-length move whose hooks the pre-fix Track skipped.
        var found = (Position2?)null;
        var direction = default(Position2);
        foreach (var dir in new[] { new Position2(1, 0), new Position2(-1, 0), new Position2(0, 1), new Position2(0, -1) })
        {
            if (found is not null) break;
            for (var column = 20; column < grid.CountX - 20 && found is null; column++)
            for (var row = 20; row < grid.CountY - 20 && found is null; row++)
            {
                var anchor = match.Runtime.Movement.ClientAnchor(grid.CellToWorld(new NavCell(column, row)));
                if (!grid.IsMovementWalkable(anchor)) continue;
                // The lock-on's own dash: 25 along the facing less the 15 of MoveBackBy. The search returns the origin when the
                // terrain in front is closer than that, which is the owner's "near a wall" press.
                var lockOnDash = new Position2(anchor.X + dir.X * 10, anchor.Z + dir.Z * 10);
                var landing = match.Runtime.Movement.FurthestWalkableLanding(anchor, lockOnDash, hero);
                if (Distance(landing, anchor) > 0.5) continue;
                if (!grid.IsMovementWalkable(new Position2(anchor.X - dir.X * 60, anchor.Z - dir.Z * 60))) continue;
                found = anchor;
                direction = dir;
            }
        }
        TestAssert.True(found is not null,
            "Map1 has a walkable point whose lock-on dash (25 - 15 = 10 units) has nowhere to land");
        var spot = found!.Value;
        hero.Position = spot;
        hero.Facing = direction;
        foe.Position = new Position2(spot.X + direction.X * 40, spot.Z + direction.Z * 40);
        Cast(match, hero);
        TestAssert.True(match.Cast(0, foe), "the lock-on press is accepted");
        match.Advance(0.6);
        var blocked = new Position2(spot.X + direction.X * 10, spot.Z + direction.Z * 10);
        TestAssert.True(Distance(match.Runtime.Movement.FurthestWalkableLanding(spot, blocked, hero), spot) <= 0.5,
            "the terrain in front is still closer than the dash after the cast");
        // The strike's own buff and the clip's lock are the script's; the detonate is its BuffOnMoveSuccess and the unlock its
        // RivenTriCleaveUnlock (the 0.1 s buff whose deactivation clears UnlockAnimation).
        var effects = match.Log.Effects;
        TestAssert.True(effects.Any(line => line.Contains("detonate.troy", StringComparison.Ordinal)),
            "the strike's ground effect is drawn even though the dash could not leave the spot: " + string.Join(" | ", effects));
        TestAssert.True(match.Log.Animations.Any(line => line.Contains("play", StringComparison.Ordinal) && line.Contains("Spell1a", StringComparison.Ordinal)),
            "the strike's clip plays: " + string.Join(" | ", match.Log.Animations));
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "UnlockAnimation"),
            "the clip's lock ended (RivenTriCleaveUnlock's deactivation cleared UnlockAnimation)");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "RivenTriCleaveDamage"),
            "the strike buff ended with the move");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    private static double Distance(Position2 a, Position2 b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    [Check("spells-live-broken-wings-lands-its-detonate-and-clip-on-the-dashs-own-figures")]
    public static void BrokenWingsDetonateAndClipAreTheScriptsFigures()
    {
        // The script's own numbers, read shipped: the detonate pair and the clip's 0.75 s lock.
        var script = Script("RivenTriCleaveDamage");
        var success = Blocks(script, "BuffOnMoveSuccess").ToArray();
        var detonateNames = success.Where(block => block.Function == "BBSpellEffectCreate")
            .Select(block => block.Text("EffectName")!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        TestAssert.Equal(
            "exile_Q_01_detonate.troy,exile_Q_02_detonate.troy,exile_Q_02_detonate_ult.troy,exile_Q_03_detonate.troy,exile_Q_03_detonate_ult.troy",
            string.Join(",", detonateNames),
            "the strike's ground effect is chosen by the strike counter (the ult's variant beside each)");
        var cleave = Script("RivenTriCleave");
        var unlock = Blocks(cleave, "SelfExecute")
            .First(block => block.Function == "BBSpellBuffAdd" && block.Text("BuffName") == "UnlockAnimation");
        TestAssert.Near(0.75, unlock.Number("Duration") ?? double.NaN, 1e-12, "the clip is locked for 0.75 s");
        using var match = new Match("Riven");
        var hero = match.Hero;
        hero.Position = new Position2(7000, 7000);
        hero.Facing = new Position2(1, 0);
        Cast(match, hero);
        TestAssert.True(match.Cast(0, at: new Position2(7600, 7000)), "an open-floor press is accepted");
        match.Advance(0.2);
        var on = match.Log.Timeline.FindIndex(line => line.Contains("add Riven UnlockAnimation", StringComparison.Ordinal));
        TestAssert.True(on >= 0, "the clip's lock is added at the cast: " + string.Join(" | ", match.Log.Timeline));
        var at = double.Parse(match.Log.Timeline[on].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        TestAssert.Near(0.2, at, 0.05, "the lock starts at the cast's own frame (the 0.25 s SelfExecute)");
        // The clip runs through the dash: the hero holds UnlockAnimation while it flies, and the landing's own
        // RivenTriCleaveUnlock (0.1 s) is what clears it, so the strike's move hooks are what end the animation.
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "UnlockAnimation"), "the clip is locked while the dash flies");
        // Batch 365 (wire grace): the release is read from the model as the clock runs. The clip's remove is held: it leaves the model at once
        // and its packet goes out one step later (BuffLifecycle.EndTick), and the match log records only the removes that go out at once.
        var releasedAt = double.NaN;
        void AdvanceWatching(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 1.0 / 30.0)
            {
                match.Advance(1.0 / 30.0);
                if (double.IsNaN(releasedAt) && !hero.Buffs.Any(buff => buff.Name == "UnlockAnimation")) releasedAt = match.Now;
            }
        }
        AdvanceWatching(0.4);
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("exile_Q_01_detonate.troy", StringComparison.Ordinal)),
            "the dash's landing ran the strike's BuffOnMoveSuccess: " + string.Join(" | ", match.Log.Effects));
        var drawn = match.Log.Effects.Last(line => line.Contains("detonate.troy", StringComparison.Ordinal));
        var drawnAt = double.Parse(drawn.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        TestAssert.True(drawnAt > at + 0.1,
            $"the detonate is drawn when the dash lands, not at the cast: {drawn}");
        AdvanceWatching(0.3);
        TestAssert.False(double.IsNaN(releasedAt), "and the clip is released with the landing's unlock");
        // The shipping unlock: the landing adds RivenTriCleaveUnlock (0.1 s) and its deactivation clears UnlockAnimation, so the
        // clip's release sits one 0.1 s after the detonate — a cast that never landed never adds either.
        TestAssert.Near(drawnAt + 0.1, releasedAt, 0.1,
            $"the unlock follows the detonate by its own 0.1 s (RivenTriCleaveUnlock), which only a landing runs (detonate {drawnAt:0.00}, release {releasedAt:0.00})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }
}