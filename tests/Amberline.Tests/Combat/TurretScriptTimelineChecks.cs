using Amberline.Core;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// The swing and targeting-beam timeline DATA/Scripts/Turret.luaobj produces through the real
/// services — CombatService's AI host, the acquisition ladder and the swing gate — pinned frame by
/// frame. This is the behaviour the two turret cases of combat-tick-corpus.json (turret-sticky and
/// turret-toggle-minions) used to pin for the hand-written machine; batch 141 left those cases
/// stale and they are listed as moved here in the batch record, because the machine armed the
/// swing on the scan that met a target it already had while TimerFindEnemies only arms from the
/// trailing gate of a scan that entered ATTACKING or TAUNTED. Nothing here is an oracle
/// comparison: every line is the shipped script's own rule, read from
/// the Turret script's bytecode listing, proto 0.7.
///
/// One TimerFindEnemies scan every 0.15 s, three 0.05 s combat steps apart (the timer arms one
/// interval after the step the host first saw the unit in, so the scans land at 0.15, 0.30, ...):
///  - t=0.15 the HARDIDLE scan: FindTargetInAcR takes the lane minion, NetSetState sets
///    AI_HARDIDLE_ATTACKING and SetTarget publishes the target and the beam. The trailing gate
///    keys on the entry state, still HARDIDLE, so there is no TurnOnAutoAttack — a turret never
///    swings on the tick that gave it its target.
///  - t=0.30 the ATTACKING scan: the target is in attack range, so TurnOnAutoAttack(GetTarget())
///    arms the swing and the shot leaves in the same combat step.
///  - t=0.50 the minion walks out of the 778-unit reach; t=0.60's scan stops the swing
///    (TurnOffAutoAttack MOVING) and retires the beam but never calls SetTarget, so the id
///    survives (classified difference 2 in combat.turret-ai-differential). t=0.75's empty search
///    calls TurnOffAutoAttack(TARGET_LOST) again and resends nothing.
///  - t=0.80 the minion returns; t=0.90's HARDIDLE scan re-takes it and republishes the beam,
///    still without swinging, and t=1.05's scan arms the swing again.
/// </summary>
public static class TurretScriptTimelineChecks
{
    private const double Step = 0.05;
    private const int Steps = 22;

    [Check("combat.turret-script-timeline")]
    public static void TheScriptOwnsTheSwingAndBeamTimeline()
    {
        var run = new Run();
        for (var index = 0; index < Steps; index++)
        {
            // Not on a scan: the two drops land between the timer's deadlines.
            if (index == 9) run.Minion.Position = new(7900, 7000);
            if (index == 15) run.Minion.Position = new(7100, 7000);
            run.Tick();
        }
        var trace = run.Trace;

        // ---- t=0.15: the acquisition tick sets the target and the beam and does not swing ----

        TestAssert.Equal((uint?)2, trace[2].Target, "the first scan takes the lane minion");
        TestAssert.Equal("", trace[2].Swings,
            "the acquisition tick sets the target and the beam and starts no swing: TimerFindEnemies' " +
            "trailing gate keys on the entry state, which the search path leaves as HARDIDLE");
        TestAssert.Equal(1, trace[2].Beams, "SetTarget is what publishes the client's targeting beam");

        // ---- t=0.30: the next scan arms the swing, which starts in the same combat step ----

        TestAssert.Equal("2", trace[5].Swings,
            "the scan after the acquisition arms the swing (TurnOnAutoAttack) and the shot leaves in the same step");
        TestAssert.Equal(1, trace[5].Beams,
            "TurnOnAutoAttack reaffirms a display the client already has, so it publishes nothing new");
        TestAssert.Equal("", trace[8].Swings,
            "the shot is gone by the next scan (a 0.05 s windup, then a 100-unit missile at 1000 units/s)");

        // ---- t=0.60: the range drop stops the swing and retires the beam; t=0.75 resends nothing ----

        TestAssert.Equal((uint?)2, trace[11].Target,
            "out of attack range is not a target loss: the drop keeps the id (classified difference 2)");
        TestAssert.Equal("", trace[11].Swings, "the drop stops the swing with TurnOffAutoAttack(MOVING)");
        TestAssert.Equal(2, trace[11].Beams, "TurnOffAutoAttack is what retires the targeting beam");
        TestAssert.Equal((uint?)2, trace[14].Target,
            "the empty search keeps the id too: Turret.luaobj never calls SetTarget(nil)");
        TestAssert.Equal(2, trace[14].Beams, "an idle scan's TurnOffAutoAttack(TARGET_LOST) resends nothing");

        // ---- t=0.90: the return re-acquires and republishes without swinging; t=1.05 swings ----

        TestAssert.Equal((uint?)2, trace[17].Target, "the next HARDIDLE search takes the returned minion");
        TestAssert.Equal("", trace[17].Swings,
            "the re-acquisition tick still does not swing: the search path's trailing gate sees HARDIDLE");
        TestAssert.Equal(3, trace[17].Beams, "SetTarget republishes the beam the range drop retired");
        TestAssert.Equal("2", trace[20].Swings, "the scan after the re-acquisition arms the swing again");
        TestAssert.Equal(3, trace[20].Beams, "TurnOnAutoAttack's second call on the same target is silent");

        for (var index = 2; index < Steps; index++)
            TestAssert.Equal((uint?)2, trace[index].Target,
                $"t={trace[index].Time:0.00}: the script holds the target id from the first scan to the last");

        // The client is told exactly what the script's calls say, once each: take, retire, retake.
        TestAssert.Equal(new[] { (1u, (uint?)2), (1u, (uint?)null), (1u, (uint?)2) },
            run.Transport.TurretTargets.ToArray(),
            "the targeting display follows SetTarget/TurnOnAutoAttack and TurnOffAutoAttack, one packet per change");
    }

    // ---- the harness -------------------------------------------------------------------------------

    /// <summary>One turret and one lane minion over the real services, stepped at 0.05 s.</summary>
    private sealed class Run
    {
        public Run()
        {
            Transport = new CombatChecks.TestTransport();
            Combat = new CombatService(World, new CombatChecks.TestHooks(), Transport, new FixedRandomSource(0.5));
            Turret = World.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, Team.Order, new(7000, 7000))
            {
                Health = 1e6, MaxHealth = 1e6, AttackRange = 775, AttackDamage = 1, AttackPeriod = 0.5,
                AttackWindup = 0.05, MissileSpeed = 1000, Radius = 0,
            });
            Minion = World.Register(new Entity(2, EntityKind.Minion, UnitType.Minion, Team.Chaos, new(7100, 7000))
            { Health = 1e6, MaxHealth = 1e6, Radius = 0 });
        }

        public WorldState World { get; } = new();
        public CombatService Combat { get; }
        public CombatChecks.TestTransport Transport { get; }
        public Entity Turret { get; }
        public Entity Minion { get; }
        public List<Frame> Trace { get; } = new();

        public void Tick()
        {
            Combat.CombatTick(Step, regenerate: false);
            var time = Trace.Count * Step + Step;
            Trace.Add(new Frame(time, Turret.AttackTarget, Swings(), Transport.TurretTargets.Count));
        }

        /// <summary>The turret's in-flight shots, as the targets they are flying at.</summary>
        private string Swings() => string.Join(",", Combat.PendingHits
            .Where(hit => hit.SourceNetId == Turret.NetId)
            .Select(hit => hit.TargetNetId)
            .OrderBy(netId => netId));
    }

    private readonly record struct Frame(double Time, uint? Target, string Swings, int Beams);

    private sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
