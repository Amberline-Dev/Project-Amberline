using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 916 (owner, 19 November 2026: Volibear Q "really rarely fails to flip the enemy VISUALLY - they just teleport"): Rolling Thunder's bounce
/// point lies on the line through Volibear, so a target standing on his lattice row or column was flung along an axis, which the stock client does
/// not draw (the target rose and fell on its start, then the landing stop seated it beyond Volibear: batch 912's six client flings). The fling is
/// now sent one lattice step off the axis (<c>MovementSystem.OffClientAxis</c>). docs/maintenance/2026-11-19-batch-916-knockup-arc.md.
/// </summary>
public static class BbB916VolibearChecks
{
    [Check("scripting.b916-volibear-rolling-thunder-flings-a-target-on-his-row-or-column-along-a-path-the-client-draws")]
    public static void RollingThunderOnARowOrColumnIsDrawn()
    {
        foreach (var (name, offset) in new[] { ("row", new Position2(150.0, 0.0)), ("column", new Position2(0.0, -150.0)) })
        {
            using var match = new Match("Volibear", foeChampion: "Orianna");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            match.Learn(1);
            match.Refill();
            var movement = match.Runtime.Movement;
            match.Hero.Position = movement.ClientAnchor(match.Hero.Position);
            match.Foe.Position = movement.ClientAnchor(match.Hero.Position + offset);
            var start = match.Foe.Position;
            var wire = Observe(match, Team.Order, match.Hero);
            TestAssert.True(match.Cast(0), $"{name}: Q is cast");
            match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
            for (var step = 0; step < 90 && Buff(match.Foe, "VolibearQExtra") is null; step++) match.Advance(Step);
            TestAssert.True(Buff(match.Foe, "VolibearQExtra") is not null, $"{name}: the swing flings the target (VolibearQExtra)");
            match.Advance(Step);
            var dash = BbB916Checks.DashOf(wire, match.Foe) ?? throw new InvalidOperationException($"{name}: no fling packet");
            BbB916Checks.AssertDrawn(name, dash, match.Foe.ForcedMove!.Speed);
            match.Advance(0.8);
            TestAssert.True(match.Foe.ForcedMove is null, $"{name}: the fling landed");
            var stop = BbB916Checks.LastStopOf(wire, match.Foe) ?? throw new InvalidOperationException($"{name}: no landing stop");
            TestAssert.True(Math.Abs(stop[0] - dash.End[0]) < 0.01f && Math.Abs(stop[1] - dash.End[1]) < 0.01f,
                $"{name}: the landing stop {BbB916Checks.Show(stop)} is the fling's end {BbB916Checks.Show(dash.End)}");
            var dx = match.Foe.Position.X - start.X;
            var dz = match.Foe.Position.Z - start.Z;
            TestAssert.Near(400.0, Math.Sqrt(dx * dx + dz * dz), 4.0, $"{name}: the target is still thrown 400 units over Volibear");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{name}: no script failed");
        }
    }
}
