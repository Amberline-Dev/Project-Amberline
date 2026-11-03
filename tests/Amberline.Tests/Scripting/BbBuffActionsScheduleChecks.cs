using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 175, owner finding (24 October 2026): "W: the card changes very rapidly on first use, then behaves normally.
/// Probably a timer running constantly in the background."
/// <para>
/// The card cycle is the shipped <c>Spells/PickACard.luaobj</c> <c>BuffOnUpdateActions</c>, so the owner's "first use" is the
/// engine's <c>ActionsUpdateInterval</c> schedule, not the spell: the batch 168 grid kept overdue time
/// (<c>ActionsNextAt += ActionsUpdateInterval</c>), and a line that had fallen behind was repaid one simulation step at a time
/// until the debt was gone. These checks drive the production runtime with a probe buff that counts its own hook runs, so the
/// cadence is measured rather than argued:
/// </para>
/// <list type="bullet">
/// <item>the first line comes one interval after the add (PickACard's even seed needs a full interval before its first turn);</item>
/// <item>a line the clock has passed is skipped, never repaid (no step-rate burst);</item>
/// <item>a buff whose whole life is shorter than the interval still runs its hook once (the shipped run-once applicators).</item>
/// </list>
/// </summary>
public static class BbBuffActionsScheduleChecks
{
    private const string Probe = "W175ActionsProbe";

    /// <summary>A buff whose only hook counts itself, the way PickACard walks its counter.</summary>
    private static BbScript CountingBuff() =>
        BbW3Rig.Buff(Probe, new BbEventTable("BuffOnUpdateActions", [BbW3Rig.Count("Runs")]));

    private static BbBuffInstance Give(BbW3Rig rig, double duration)
    {
        var state = rig.Module.Buffs!.Add(new BbBuffRequest(rig.Match.Hero, rig.Match.Hero, Probe,
            (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)BbBuffType.BUFF_Internal, 1, duration, 0.0, false, new BbTable()));
        return rig.Module.Buffs.InstanceOf(state ?? throw new InvalidOperationException($"{Probe} was not added"))
               ?? throw new InvalidOperationException($"{Probe} has no script instance");
    }

    private static double Runs(BbBuffInstance instance) =>
        instance.InstanceVars["Runs"] is double value ? value : 0.0;

    /// <summary>Advances in simulation steps, returning the match time of every hook run the probe counted.</summary>
    private static List<double> RunTimes(BbM3.Match match, BbBuffInstance instance, double seconds)
    {
        var times = new List<double>();
        var seen = Runs(instance);
        for (var step = 0; step < (int)Math.Round(seconds * 30); step++)
        {
            match.Advance(BbM3.Step);
            var now = Runs(instance);
            if (now == seen) continue;
            // One line per run: a burst would show here as several runs inside one step, which the schedule never does.
            TestAssert.True(now - seen <= 1.0, $"one hook run per update at most ({seen} -> {now} in one step)");
            seen = now;
            times.Add(match.Now);
        }
        return times;
    }

    [Check("scripting.bb-buff-actions-the-first-line-is-one-interval-after-the-add")]
    public static void FirstLineIsOneIntervalAfterTheAdd()
    {
        var buff = CountingBuff();
        using var rig = new BbW3Rig(null, buff);
        var added = rig.Match.Now;
        var instance = Give(rig, 25000.0);
        TestAssert.Near(0.0, Runs(instance), 1e-9, "the add itself does not run the update hook");
        TestAssert.Near(added + 0.25, instance.ActionsNextAt, 1e-9,
            "the first line is the add's clock plus the quarter second, not the add");
        var runs = RunTimes(rig.Match, instance, 1.1);
        TestAssert.True(runs.Count >= 4, $"the hook runs while the buff lives ({runs.Count} runs in 1.1 s)");
        // 30 Hz steps: the line at add + 0.25 is met by the step at +0.267.
        TestAssert.Near(0.25, runs[0] - added, 0.04,
            $"the first run is the first line after the add: {string.Join(", ", runs.Select(t => $"{t - added:0.000}"))}");
        for (var index = 1; index < runs.Count; index++)
            TestAssert.Near(0.25, runs[index] - runs[index - 1], 0.04,
                $"a run every quarter second: {string.Join(", ", runs.Select(t => $"{t - added:0.000}"))}");
        rig.NoFailures();
    }

    [Check("scripting.bb-buff-actions-a-line-the-clock-passed-is-skipped-not-repaid")]
    public static void ALateLineIsSkippedNotRepaid()
    {
        // The owner's shape: a schedule that fell behind the clock. PickACard's press after a long stall is the case the check
        // states; the buff here is the same bare-hook schedule and the line is pushed two seconds into the past, the debt the old
        // "overdue kept" addition repaid one 30 Hz step at a time (a card every 0.033 s until the line caught the clock).
        var buff = CountingBuff();
        using var rig = new BbW3Rig(null, buff);
        var instance = Give(rig, 25000.0);
        rig.Match.Advance(1.0);
        var before = Runs(instance);
        TestAssert.True(before >= 3, $"the schedule ran before the setback ({before} runs in 1 s)");
        instance.ActionsNextAt -= 2.0;
        var runs = RunTimes(rig.Match, instance, 1.1);
        // The owed line is one run, taken at the first update that meets the clock; the next line is a whole interval later, so
        // 1.1 s holds five runs. Repaying the backlog gave one run per update: 33.
        TestAssert.True(runs.Count <= 6,
            $"the debt is skipped, not repaid at the step rate ({runs.Count} runs in 1.1 s): " +
            string.Join(", ", runs.Select(t => t.ToString("0.000"))));
        TestAssert.True(runs.Count >= 4, $"the schedule goes on after the debt ({runs.Count} runs in 1.1 s)");
        for (var index = 1; index < runs.Count; index++)
            TestAssert.Near(0.25, runs[index] - runs[index - 1], 0.04,
                $"every run after the owed one is a whole interval apart: {string.Join(", ", runs.Select(t => t.ToString("0.000")))}");
        rig.NoFailures();
    }

    [Check("scripting.bb-buff-actions-a-buff-shorter-than-the-interval-runs-its-hook-once")]
    public static void AShortBuffRunsOnce()
    {
        // The shipped "do it once and remove myself" applicators live shorter than the quarter second (FrostArrowApplicator and
        // ToxicShotApplicator 0.1 s, Renekton's unlock animations 0.01 s), so the grid alone would never run their hook and their
        // whole effect is that run. They take it at their first update and never a second time.
        var buff = CountingBuff();
        using var rig = new BbW3Rig(null, buff);
        var added = rig.Match.Now;
        var instance = Give(rig, 0.1);
        var runs = RunTimes(rig.Match, instance, 1.0);
        TestAssert.Equal(1, runs.Count,
            $"a buff that ends before its first line runs its hook exactly once: {string.Join(", ", runs.Select(t => $"{t - added:0.000}"))}");
        TestAssert.Near(0.033, runs[0] - added, 0.02, "and at its first update, not its last");
        TestAssert.False(rig.Match.Hero.Buffs.Any(state => ReferenceEquals(state, instance.State)),
            "the buff ended on its own duration");
        rig.NoFailures();
    }
}
