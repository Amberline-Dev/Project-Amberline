using Amberline.Core;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-self-ground-input-ignores-hidden-incidental-id")]
    public static void SelfAndGroundInputIgnoreHiddenMouseoverButTargetedInputDoesNot()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            foreach (var spell in new[]
                     { "SummonerFlash", "SummonerHaste", "SummonerBoost", "SummonerClairvoyance" })
            {
                using var match = new Match("Annie", team);
                var hero = match.Hero(0);
                var enemy = match.Hero(1);
                hero.Position = new Position2(4300, 6900);
                enemy.Position = hero.Position;
                hero.SummonerSpells = [spell, "SummonerDot"];
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                    "the enemy is registered to the client before it hides");
                enemy.StealthHidden = true;
                match.Runtime.Delivery.Flush();
                TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
                    team + "/" + spell + " uses an enemy currently hidden from its client");
                var point = spell == "SummonerFlash" ? new Position2(4000, 6900) : hero.Position;
                var before = hero.Position;
                var visionCount = match.Runtime.World.TimedVisionBubbles.Count;
                TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 0,
                    enemy.NetId, point)),
                    team + "/" + spell + " ignores the irrelevant mouse-over NetId");
                TestAssert.True(hero.SummonerCooldowns[0] > 0,
                    team + "/" + spell + " committed its summoner cooldown");
                if (spell == "SummonerFlash")
                    TestAssert.True(hero.Position != before, team + " actually flashed");
                else if (spell == "SummonerHaste")
                    TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerHaste"),
                        team + " gained Ghost");
                else if (spell == "SummonerBoost")
                    TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerBoost"),
                        team + " gained Cleanse's follow-up");
                else
                    TestAssert.Equal(visionCount + 1,
                        match.Runtime.World.TimedVisionBubbles.Count);
            }

            using (var match = new Match("Annie", team))
            {
                var hero = match.Hero(0);
                var enemy = match.Hero(1);
                hero.Position = new Position2(4300, 6900);
                enemy.Position = hero.Position;
                hero.Inventory = [new InventorySlot(2044), new InventorySlot(3157),
                    null, null, null, null];
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId));
                enemy.StealthHidden = true;
                match.Runtime.Delivery.Flush();
                TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId));
                var wardsBefore = match.Runtime.World.Entities.Values.Count(unit => unit.IsWard);
                TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 4,
                    enemy.NetId, hero.Position)), team + " places a ward despite stale mouse-over");
                TestAssert.True(hero.Inventory[0] is null,
                    team + " consumed the ward item");
                TestAssert.Equal(wardsBefore + 1,
                    match.Runtime.World.Entities.Values.Count(unit => unit.IsWard));
                TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 5,
                    enemy.NetId, hero.Position)), team + " activates self Zhonya despite stale mouse-over");
                TestAssert.True(hero.ItemCooldowns[1] > 0,
                    team + " committed Zhonya's cooldown");
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield"));
            }

            using (var match = new Match("Annie", team))
            {
                var hero = match.Hero(0);
                var enemy = match.Hero(1);
                hero.Position = new Position2(4300, 6900);
                enemy.Position = hero.Position;
                hero.SummonerSpells = ["SummonerDot", "SummonerExhaust"];
                hero.Inventory = [new InventorySlot(3144), null, null, null, null, null];
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId));
                enemy.StealthHidden = true;
                match.Runtime.Delivery.Flush();
                TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId));
                TestAssert.False(InputCast(match, 1, InputOrder(hero.NetId, 3, enemy)),
                    "hidden enemy cannot receive an attack order");
                TestAssert.False(InputCast(match, 1, InputCastPacket(hero.NetId, 0,
                    enemy.NetId, enemy.Position)), "Ignite retains its unit-sight gate");
                TestAssert.False(InputCast(match, 1, InputCastPacket(hero.NetId, 1,
                    enemy.NetId, enemy.Position)), "Exhaust retains its unit-sight gate");
                TestAssert.False(InputCast(match, 1, InputCastPacket(hero.NetId, 4,
                    enemy.NetId, enemy.Position)), "targeted Cutlass retains its unit-sight gate");
                TestAssert.Near(0, hero.SummonerCooldowns[0], 0);
                TestAssert.Near(0, hero.SummonerCooldowns[1], 0);
                TestAssert.Near(0, hero.ItemCooldowns[0], 0);
                enemy.StealthHidden = false;
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId));
                TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 0,
                    enemy.NetId, enemy.Position)), "a revealed enemy is still valid for Ignite");
            }

            using (var match = new Match("Annie", team))
            {
                var hero = match.Hero(0);
                hero.Position = new Position2(4300, 6900);
                hero.SummonerSpells = ["SummonerSmite", "SummonerDot"];
                var monster = match.Runtime.World.Register(new Entity(
                    match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Monster,
                    UnitType.Monster, Team.Neutral, hero.Position)
                {
                    Name = "Golem", Created = true, Health = 1000, MaxHealth = 1000,
                });
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(monster.NetId));
                monster.StealthHidden = true;
                match.Runtime.Delivery.Flush();
                TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(monster.NetId));
                TestAssert.False(InputCast(match, 1, InputCastPacket(hero.NetId, 0,
                    monster.NetId, monster.Position)), "Smite retains its unit-sight gate");
                TestAssert.Near(0, hero.SummonerCooldowns[0], 0);
                monster.StealthHidden = false;
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(monster.NetId));
                TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 0,
                    monster.NetId, monster.Position)), "revealed monster remains smiteable");
            }
        }
    }
}
