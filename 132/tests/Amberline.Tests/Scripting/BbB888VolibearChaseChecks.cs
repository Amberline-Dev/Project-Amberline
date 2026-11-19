using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 888 (owner decision, 18 November 2026: "check at start, cut on blink"), the case batch 884 left open: Volibear's Rolling Thunder
/// swing on a champion at the edge of his reach that walks straight away as the swing starts. The swing started in reach lands, so the
/// fling happens. docs/maintenance/2026-11-18-batch-888-swing-rule.md.
/// </summary>
public static class BbB888VolibearChaseChecks
{
    [Check("scripting.b888-volibear-rolling-thunder-flings-a-champion-walking-away-from-the-reach-edge")]
    public static void RollingThunderFlingsAWalkingChampion()
    {
        using var match = new Match("Volibear", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(0), "Q is cast");
        match.Foe.Position = match.Hero.Position + new Position2(150, 0);
        match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
        var swing = B888SwingRuleChecks.Watch(match, match.Hero, match.Foe, B888SwingRuleChecks.WalkAway(match, match.Foe));
        TestAssert.True(swing.CrossedInWindup, "fixture: Orianna crosses reach + 100 inside the windup: " + swing.Story);
        TestAssert.True(swing.Landed is not null && swing.Cut is null, "the Rolling Thunder swing lands: " + swing.Story);
        TestAssert.True(Buff(match.Foe, "VolibearQExtra") is not null, "Orianna is flung (VolibearQExtra): " + swing.Story + "; foe buffs " +
            Names(match.Foe));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
