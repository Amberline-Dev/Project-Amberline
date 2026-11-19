using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 892 (both lines): a death ends the unit's script moves while their buffs are still on it, and only their failure hook runs, as
/// cleanup that touches no other unit (BbScriptMoves.EndForDeath; owner decision, 18 November 2026). docs/maintenance/2026-11-18-batch-892-audit-fizz-volibear-shyvana.md (132).
/// </summary>
public static class BbB892DeathMoveChecks
{
    // Owner decision (18 November 2026): a champion who dies mid-dash deals no landing damage. Gragas's Body Slam lands in its BuffOnMoveEnd
    // (damage and slow within 250), Riven's Broken Wings in RivenTriCleaveDamage's BuffOnMoveFailure (damage, knock-back and debuff around her).
    // Each is killed 0.1 s into the dash beside a minion: the minion takes nothing, the dash buff is gone, no script fails.
    [Check("scripting.b892-a-champion-killed-mid-dash-deals-no-landing-damage")]
    public static void ChampionKilledMidDashDealsNoLandingDamage()
    {
        var failures = new List<string>();
        Dash("Gragas", 2, "GragasBodySlam");
        Dash("Riven", 0, "RivenTriCleaveDamage");
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Dash(string champion, int slot, string buff)
        {
            using var match = new Match(champion, foeChampion: "Orianna");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            match.Foe.Position = match.Hero.Position + new Position2(0, -2000);
            match.Ally.Position = match.Hero.Position + new Position2(0, -1000);
            var minion = LaneMinion(match, match.Hero.Position + new Position2(0, 150));
            match.Runtime.Minions!.HaltAI(minion);
            minion.Waypoints.Clear();
            match.Advance(0.2);
            match.Learn(1);
            match.Refill();
            if (!match.Cast(slot, at: match.Hero.Position + new Position2(600, 0)))
            {
                failures.Add($"{champion}: the dash was not cast");
                return;
            }
            for (var step = 0; step < 30 && match.Hero.ForcedMove is null; step++) match.Advance(Step);
            if (match.Hero.ForcedMove is null || Buff(match.Hero, buff) is null)
            {
                failures.Add($"{champion}: fixture, not in the dash ({Names(match.Hero)})");
                return;
            }
            match.Advance(0.1);
            var hits = match.Log.Hits.Count;
            match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
            match.Advance(0.5);
            var landed = match.Log.Hits.Skip(hits).Where(line => line.Contains("->Minion ", StringComparison.Ordinal)).ToList();
            if (landed.Count != 0) failures.Add($"{champion}: the dead champion's dash still hit: {string.Join(" ; ", landed)}");
            if (Buff(match.Hero, buff) is not null) failures.Add($"{champion}: {buff} outlived the death");
            if (match.Module.ScriptFailures.Count != 0) failures.Add($"{champion}: {match.Module.ScriptFailures.Count} script failures");
        }
    }
}
