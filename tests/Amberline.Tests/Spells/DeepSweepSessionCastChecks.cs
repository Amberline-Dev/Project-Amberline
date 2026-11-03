using Amberline.Core;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-session-ground-cast-hidden-incidental-target")]
    public static void SessionGroundCastIgnoresHiddenIncidentalTarget()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Nidalee", "LeeSin", "Annie" })
        {
            using var match = new Match(champion, team);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.Position = new Position2(4000, 6900);
            enemy.Position = new Position2(4300, 6900);
            match.Runtime.Delivery.Flush();
            TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                "real participant view cannot see the separate brush");
            hero.Inventory = [new InventorySlot(3144), null, null, null, null, null];
            TestAssert.False(match.TryCast(0, 4, enemy), "unit-target item keeps session sight gate");
            TestAssert.Near(0, hero.ItemCooldowns[0], 0, "rejected item spends no cooldown");
            var before = hero.Mana;
            var accepted = match.TryCast(0, 0, enemy);
            TestAssert.Equal(champion != "Annie", accepted,
                "spear/Sonic Wave use ground aim, Disintegrate requires a visible unit");
            if (champion == "Annie") TestAssert.Near(before, hero.Mana, 0);
            else
            {
                // Batch 340: a windup spends its resource at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not at the press.
                match.Runtime.Tick(0.5);
                TestAssert.True(hero.Mana < before, "accepted ground cast commits its resource");
            }
        }
    }
}
