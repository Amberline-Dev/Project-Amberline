using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 168 part 3, owner finding 20 (20 October 2026): "Twisted Fate's W (Pick A Card) cards cycle WAY too fast."
/// The cycle is the shipped <c>Spells/PickACard.luaobj</c> <c>BuffOnUpdateActions</c> itself: it adds 1 to
/// <c>InstanceVars.Counter</c> on every call and changes the slot (and the particle) only when that counter reaches 2
/// (red), 4 (gold) and 6 (blue, reset to 0), so one card is two calls of the hook and a full shuffle of three is six.
/// The engine ran a <c>TickRate 0</c> buff's update hook on every simulation step (1/30 s), so a card turned every 66 ms
/// and a full shuffle took 0.2 s (batch 130 open item 2 recorded exactly that). The root is the cadence, not the script:
/// the native keeps <c>mLastTickTimeUpdateActions</c> beside <c>mLastTickTimeUpdateStats</c> around one
/// <c>mTickTimeDelay</c>, and the scripts are written to the same quarter second the stats hook already runs on
/// (<c>BbBuffRuntime.StatsUpdateInterval</c>, batch 150's script evidence: Oriana's <c>TicksLeft = 8</c> over two seconds).
/// <c>BbBuffRuntime.ActionsUpdateInterval</c> now gives the actions hook that grid too, so a card is 0.5 s and a round 1.5 s.
/// </summary>
public static class TwistedFateCardCycleChecks
{
    /// <summary>Card lock spells in the order the script installs them: Counter 0/2/4 -> blue/red/gold.</summary>
    private static string Card(Entity hero) => hero.ActiveSpellNames[1] is { Length: > 0 } name ? name : "";

    [Check("spells-live-pick-a-card-turns-one-card-per-half-second")]
    public static void PickACardCyclesOnTheShippedQuarterSecond()
    {
        // The script's own arithmetic (PickACard.TargetExecute seeds NextBuffVars.Counter, BuffOnUpdateActions walks it by
        // one and changes the card on 2, 4 and 6): one card is two update calls. On the 0.25 s buff update that is 0.5 s a
        // card and 1.5 s for the three colours, which is what a player can lock by hand; the old per-step cadence gave 66 ms.
        using var match = new Match("TwistedFate");
        var hero = match.Hero;
        hero.Position = new Position2(7000, 7000);
        match.Learn(1);
        match.Refill();
        match.Advance(0.2);
        // Batch 175: the card the press deals is the script's seeded one, and the seed is EVEN (BlueCardLock 0, RedCardLock 2,
        // GoldCardLock 4) while BuffOnUpdateActions changes the slot on 2, 4 and 6. The first card therefore owns the buff from the
        // press to the first hook run one interval later, and only then comes the first turn. Measured from the press, like the
        // owner's "first use": a schedule that ran at the add (or repaid a backlog a step at a time) showed the seeded card for a
        // single interval and then turned at step rate, which is the "very rapid" first use (finding 22, 24 October 2026).
        TestAssert.True(match.Cast(1, hero), "Pick a Card casts on himself");
        var seed = Card(hero);
        TestAssert.True(seed is "BlueCardLock" or "RedCardLock" or "GoldCardLock", $"the press deals one of the three cards: {seed}");
        var turns = new List<(double At, string Slot)>();
        var last = seed;
        var start = match.Now;
        for (var step = 0; step < 30 * 3; step++)
        {
            match.Advance(1.0 / 30.0);
            if (Card(hero) == last) continue;
            last = Card(hero);
            turns.Add((match.Now - start, last));
        }
        // The first card is a whole card: the seeded one is shown for 0.5 s, not the 0.25 s a first run at the add leaves it.
        TestAssert.Near(0.5, turns[0].At, 0.08,
            $"the first card (the script's seeded one) is shown for a whole card: {string.Join(", ", turns.Select(t => $"{t.At:0.00}s {t.Slot}"))}");
        TestAssert.True(turns.Count >= 5, $"the deck turns on its own while the buff runs ({turns.Count} turns in 3 s)");
        var gaps = turns.Select((turn, index) => turn.At - (index == 0 ? 0.0 : turns[index - 1].At)).Skip(1).ToArray();
        foreach (var gap in gaps)
            TestAssert.Near(0.5, gap, 0.08,
                $"a card every 0.5 s (the script's two update calls on the shipped 0.25 s buff tick): {string.Join(", ", turns.Select(t => $"{t.At:0.00}s {t.Slot}"))}");
        // Three colours in the script's order: the seed may be any of the three, the sequence is blue -> red -> gold ->
        // blue (Counter 0 -> 2 -> 4 -> 6/0), never a repeat.
        var seen = turns.Select(turn => turn.Slot).ToArray();
        for (var index = 1; index < seen.Length; index++)
            TestAssert.True(seen[index] != seen[index - 1], $"the same card is not dealt twice in a row: {string.Join(", ", seen)}");
        TestAssert.True(seen.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
            $"all three cards turn over: {string.Join(", ", seen)}");
        // One full shuffle of three is the script's six calls: 1.5 s, not the 0.2 s the per-step cadence gave.
        var round = turns.Skip(1).Take(3).ToArray();
        TestAssert.Near(1.5, round[^1].At - round[0].At + 0.5, 0.1,
            $"a round of three takes 1.5 s (three cards of 0.5 s), turns at {string.Join(", ", turns.Select(t => $"{t.At:0.00}"))}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    }

    [Check("scripting.bb-pick-a-card-cycle-is-the-scripts-own-counter-on-the-buff-tick")]
    public static void CardCycleIsTheScriptsCounterOnTheBuffTick()
    {
        // The timing above is the script's, not a constant of the engine: PickACard.BuffOnUpdateActions walks Counter by 1
        // and names its card on 2, 4 and 6. Pinned on the shipped table so a re-extract that changed the arithmetic would
        // fail here rather than only on the live check.
        var script = Script("PickACard");
        var update = script.Event("BuffOnUpdateActions");
        TestAssert.True(update is not null, "PickACard has a BuffOnUpdateActions");
        var flat = Blocks(script, "BuffOnUpdateActions").ToArray();
        var increment = flat.First(block => block.Function == "BBMath" && block.Text("DestVar") == "Counter");
        TestAssert.Equal("InstanceVars", increment.Text("DestVarTable"), "the counter the hook walks lives in InstanceVars");
        TestAssert.Near(1.0, increment.Number("Src2Value") ?? double.NaN, 1e-12, "by one a call");
        var thresholds = flat.Where(block => block.Function is "BBIf" or "BBElseIf" && block.Text("Src1Var") == "Counter")
            .Select(block => block.Number("Value2")).ToArray();
        TestAssert.Equal([2.0, 4.0, 6.0], thresholds, "a card changes on 2, 4 and 6: red, gold, blue");
        TestAssert.True(flat.All(block => block.Function != "BBExecutePeriodically"),
            "the script schedules no periodic of its own: its timing is the buff update (mLastTickTimeUpdateActions)");
    }
}