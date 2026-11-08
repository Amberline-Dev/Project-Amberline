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
/// <item>the first run is the holder's next 0.25 s pass (batch 720, owner decision of 8 November 2026; batch 175 had it one interval
/// after the add);</item>
/// <item>a line the clock has passed is skipped, never repaid (no step-rate burst);</item>
/// <item>a buff whose whole life is shorter than the interval still runs its hook once (the shipped run-once applicators).</item>
/// </list>
/// </summary>
public static class BbBuffActionsScheduleChecks
{
    private const string Probe = "W175ActionsProbe";

    /// <summary>A buff whose only hook counts itself, the way PickACard walks its counter.</summary>
    private static BbScript CountingBuff() => CountingBuff(Probe);

    private static BbScript CountingBuff(string name) =>
        BbW3Rig.Buff(name, new BbEventTable("BuffOnUpdateActions", [BbW3Rig.Count("Runs")]));

    private static BbBuffInstance Give(BbW3Rig rig, double duration, string name = Probe)
    {
        var state = rig.Module.Buffs!.Add(new BbBuffRequest(rig.Match.Hero, rig.Match.Hero, name,
            (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)BbBuffType.BUFF_Internal, 1, duration, 0.0, false, new BbTable()));
        return rig.Module.Buffs.InstanceOf(state ?? throw new InvalidOperationException($"{name} was not added"))
               ?? throw new InvalidOperationException($"{name} has no script instance");
    }

    /// <summary>
    /// Batch 720: the steps of a run of probes on the hero, one probe of its own name per add, each added at a later phase of the
    /// hero's 0.25 s line: <see cref="Passes"/> are the steps the hero's line was due on (<c>SpellSystem.SelfUpdateDue</c>), and each
    /// probe's <see cref="Added"/> and <see cref="First"/> are the step of its add and of its first hook run.
    /// </summary>
    private sealed class PhaseRun(BbW3Rig rig, int probes)
    {
        public List<int> Passes { get; } = new();
        public BbBuffInstance?[] Probes { get; } = new BbBuffInstance?[probes];
        public int[] Added { get; } = new int[probes];
        public int?[] First { get; } = new int?[probes];
        public int[] Gone { get; } = new int[probes];
        public int Step { get; private set; }

        public void Advance()
        {
            rig.Match.Advance(BbM3.Step);
            Step++;
            if (rig.Match.Runtime.Spells.SelfUpdateDue(rig.Match.Hero)) Passes.Add(Step);
            for (var index = 0; index < Probes.Length; index++)
            {
                if (Probes[index] is not { } probe) continue;
                if (First[index] is null && Runs(probe) >= 1.0)
                {
                    TestAssert.Near(1.0, Runs(probe), 1e-9, $"probe {index}: one run in its first run's step");
                    First[index] = Step;
                }
                if (Gone[index] == 0 && !rig.Match.Hero.Buffs.Any(state => ReferenceEquals(state, probe.State))) Gone[index] = Step;
            }
        }

        /// <summary>The first of the hero's passes after <paramref name="step"/>, or at it when <paramref name="inclusive"/>.</summary>
        public int NextPass(int step, bool inclusive) => Passes.First(pass => inclusive ? pass >= step : pass > step);

        public string Describe(int index) =>
            $"probe {index} added at step {Added[index]}, first run {First[index]?.ToString() ?? "none"}, passes {string.Join(" ", Passes)}";
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
            // Batch 720: and every run is one of the holder's 0.25 s passes.
            TestAssert.True(match.Runtime.Spells.SelfUpdateDue(match.Hero), $"the run at {match.Now:0.000} is on the hero's pass");
            seen = now;
            times.Add(match.Now);
        }
        return times;
    }

    /// <summary>
    /// Batch 720 (owner decision, 8 November 2026; restates batch 175's <c>scripting.bb-buff-actions-the-first-line-is-one-interval-after-the-add</c>,
    /// which pinned the first run one interval after the add): a <c>TickRate 0</c> buff's first <c>BuffOnUpdateActions</c> runs on its holder's
    /// next 0.25 s pass, 0 to 0.25 s after the add, then on every pass while it lives. Reconstruction (batch 718: no source gives the phase).
    /// The add's own step: the hero's line is stepped in <c>SpellSystem.Tick</c> and the buffs' hooks run in <c>TickBuffs</c> after it, so an
    /// add inside a pass step before the buff update (here a <c>SpellSystem.RunAt</c> action, the end of the spell tick) runs in that step,
    /// and an add between two steps comes after that step's buff update and meets the next pass. Nine adds of each kind cover every phase
    /// of the line (7.5 steps at 30 Hz). The old schedule ran every first hook eight steps (0.267 s) after its add.
    /// </summary>
    [Check("scripting.bb-buff-actions-the-first-run-is-the-holders-next-pass")]
    public static void FirstRunIsTheHoldersNextPass()
    {
        const int Phases = 9;
        var names = Enumerable.Range(0, 2 * Phases).Select(index => $"W720ActionsProbe{index}").ToArray();
        using var rig = new BbW3Rig(null, names.Select(CountingBuff).ToArray());
        var spells = rig.Match.Runtime.Spells;
        var run = new PhaseRun(rig, names.Length);
        for (var step = 0; step < 30; step++) run.Advance();
        // Between two steps, one add per step.
        for (var index = 0; index < Phases; index++)
        {
            run.Added[index] = run.Step;
            run.Probes[index] = Give(rig, 25000.0, names[index]);
            TestAssert.Near(0.0, Runs(run.Probes[index]!), 1e-9, "the add itself does not run the update hook");
            run.Advance();
        }
        // Inside a step, after the hero's line was stepped and before the buff update: one add per step.
        for (var index = Phases; index < names.Length; index++)
        {
            var slot = index;
            spells.RunAt(spells.CurrentTime + BbM3.Step / 2.0, () => run.Probes[slot] = Give(rig, 25000.0, names[slot]));
            run.Advance();
            run.Added[index] = run.Step;
            TestAssert.True(run.Probes[index] is not null, "the timed add ran inside the step");
        }
        for (var step = 0; step < 12; step++) run.Advance();

        var delays = new int[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            var inStep = index >= Phases;
            TestAssert.Equal(run.NextPass(run.Added[index], inclusive: inStep), run.First[index] ?? -1,
                $"the first run is the hero's next pass ({(inStep ? "an add inside a step meets its own step's pass" : "an add between steps meets the next one")}): {run.Describe(index)}");
            delays[index] = run.First[index]!.Value - run.Added[index];
            TestAssert.True(delays[index] * BbM3.Step <= 0.25 + BbM3.Step + 1e-9, $"never more than one line after the add: {run.Describe(index)}");
            // Then every pass while it lives, once each: the hook ran on each of the hero's passes from its first run on.
            TestAssert.Near(run.Passes.Count(pass => pass >= run.First[index]!.Value), Runs(run.Probes[index]!), 1e-9,
                $"one run on every pass after the first: {run.Describe(index)}");
        }
        TestAssert.True(delays.Take(Phases).Distinct().Count() >= 7,
            $"the adds between steps met every phase of the line (first run 1 to 8 steps after the add): {string.Join(" ", delays.Take(Phases))}");
        TestAssert.True(delays.Skip(Phases).Contains(0),
            $"an add inside a pass step ran in that step: {string.Join(" ", delays.Skip(Phases))}");
        rig.NoFailures();
    }

    [Check("scripting.bb-buff-actions-a-line-the-clock-passed-is-skipped-not-repaid")]
    public static void ALateLineIsSkippedNotRepaid()
    {
        // The owner's shape: a schedule that fell behind the clock. PickACard's press after a long stall is the case the check
        // states; the buff here is the same bare-hook schedule and the line is pushed two seconds into the past, the debt the old
        // "overdue kept" addition repaid one 30 Hz step at a time (a card every 0.033 s until the line caught the clock).
        // Batch 720: the line is the holder's own 0.25 s pass now (SpellSystem.StepSelfUpdate skips a passed line the same way), and the
        // buff's ActionsNextAt is only the not-before stamp of its next run; a stamp two seconds in the past adds no run off the passes.
        var buff = CountingBuff();
        using var rig = new BbW3Rig(null, buff);
        var instance = Give(rig, 25000.0);
        rig.Match.Advance(1.0);
        var before = Runs(instance);
        TestAssert.True(before >= 3, $"the schedule ran before the setback ({before} runs in 1 s)");
        instance.ActionsNextAt -= 2.0;
        var runs = RunTimes(rig.Match, instance, 1.1);
        // The owed line is one run, taken at the hero's next pass (batch 720; batch 175: at the first update that met the clock); the next
        // line is a whole interval later, so 1.1 s holds five runs. Repaying the backlog gave one run per update: 33.
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
        // Batch 720: the first line is the holder's next pass now. A 0.1 s buff whose life holds that pass runs on it; one that ends
        // before it takes its single run at its first update, as before, and that run stands for the line it will not see: the next
        // run may only come a whole line later, so the step a buff outlives its duration by (0.1 - 3 / 30 leaves a float residue, the
        // expiry tests ExpiresAt <= 0) never meets a pass for a second run. Nine adds, one per step, meet both cases.
        const int Phases = 9;
        var names = Enumerable.Range(0, Phases).Select(index => $"W720ShortProbe{index}").ToArray();
        using var rig = new BbW3Rig(null, names.Select(CountingBuff).ToArray());
        var run = new PhaseRun(rig, Phases);
        for (var step = 0; step < 30; step++) run.Advance();
        for (var index = 0; index < Phases; index++)
        {
            run.Added[index] = run.Step;
            run.Probes[index] = Give(rig, 0.1, names[index]);
            run.Advance();
        }
        for (var step = 0; step < 30; step++) run.Advance();
        var onPass = 0;
        for (var index = 0; index < Phases; index++)
        {
            // The last step of its 0.1 s (three 30 Hz steps after the add between two steps).
            var lastStep = run.Added[index] + (int)Math.Round(0.1 / BbM3.Step);
            TestAssert.True(run.Gone[index] >= lastStep && run.Gone[index] <= lastStep + 1,
                $"the buff ended on its own duration (gone after step {run.Gone[index]}): {run.Describe(index)}");
            TestAssert.Near(1.0, Runs(run.Probes[index]!), 1e-9,
                $"a buff that ends before its first line runs its hook exactly once: {run.Describe(index)}");
            var pass = run.NextPass(run.Added[index], inclusive: false);
            if (pass <= lastStep) onPass++;
            TestAssert.Equal(pass <= lastStep ? pass : run.Added[index] + 1, run.First[index] ?? -1,
                $"on the hero's pass when its life holds one, else at its first update, not its last: {run.Describe(index)}");
        }
        TestAssert.True(onPass > 0 && onPass < Phases, $"the adds met both cases ({onPass} of {Phases} ran on a pass)");
        rig.NoFailures();
    }
}
