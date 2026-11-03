using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat.live-nimbleness-capacity-dodge-renewal")]
    public static void NimblenessCapacityDodgeAndRenewal()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var used in new[] { 64, 63 })
        {
            using var match = new Match("Annie", team);
            var target = match.Hero(0);
            var attacker = match.Hero(1);
            target.Masteries = DefensivePage(new MasteryCatalog());
            match.Runtime.HeroStats.Refresh(target);
            target.Dodge = 1.0;
            FillChampionCapacity(match.Runtime.Buffs, target, used);
            var before = target.Health;
            var roll = new AttackRolls(Crit: 1.0, Dodge: 0.0, Miss: 1.0);
            TestAssert.True(match.Runtime.Combat.BasicAttackDamage(attacker, target, roll).Dodged);
            TestAssert.Near(before, target.Health, 1e-8, "dodged attack still avoids damage");
            TestAssert.Equal(used == 63,
                target.Buffs.Any(buff => buff.Name == "Nimbleness"),
                "a saturated dodge cannot allocate a new mastery buff");
            if (used == 64) continue;

            TestAssert.Equal(64, target.Buffs.Count);
            TestAssert.True(match.Runtime.Combat.BasicAttackDamage(attacker, target, roll).Dodged);
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Nimbleness"),
                "the existing mastery buff renews in its occupied slot");
            TestAssert.Equal(64, target.Buffs.Count);
        }
    }
}
