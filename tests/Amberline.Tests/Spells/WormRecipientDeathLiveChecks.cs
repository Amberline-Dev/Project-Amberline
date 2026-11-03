using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("neutral.live-worm-recipient-death-revive-buff-cleanup")]
    public static void WormRecipientDeathAndReviveRetireExactTemporaryBuffs()
    {
        var names = new[] { "WormAttack", "WrathDebuff", "SweepingBlow",
            "Propel", "Nimbleness" };
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var victim = match.Hero(0);
            var enemy = match.Hero(1);
            victim.Masteries = DefensivePage(new MasteryCatalog());
            match.Runtime.HeroStats.Refresh(victim);
            victim.Dodge = 1.0;
            TestAssert.True(match.Runtime.Combat.BasicAttackDamage(enemy, victim,
                new AttackRolls(Crit: 1.0, Dodge: 0.0, Miss: 1.0)).Dodged);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "Nimbleness"),
                "a real defensive-mastery dodge installed the temporary buff");

            var worm = match.Runtime.World.Register(new Entity(
                match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
                UnitType.Monster, Team.Neutral, victim.Position + new Position2(200, 0))
            { Name = "Worm", Health = 1000, MaxHealth = 1000 });
            // Batch 174 part 2: the Baron's WormAttack is its scripted cast. The victim hits the bound Baron, the Baron's CharOnPreDamage
            // casts WormAttack back, and its TargetExecute installs the debuff when the missile lands.
            match.Runtime.Spells.UnitScripts!.BindUnit(worm, "Worm");
            TestAssert.True(match.Runtime.Combat.ApplyHit(victim.NetId, worm.NetId,
                1.0, DamageType.Physical, DamageSourceKind.Basic));
            for (var step = 0; step < 40 && !victim.Buffs.Any(buff => buff.Name == "WormAttack"); step++)
            {
                match.Runtime.World.Clock.Advance(.1);
                match.Runtime.Spells.Tick(.1);
                foreach (var unit in match.Runtime.World.Entities.Values.ToArray()) match.Runtime.Buffs.TickBuffs(unit, .1);
                match.Runtime.Combat.CombatTick(.1, regenerate: false);
            }
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "WormAttack"),
                "the Baron's scripted WormAttack landed and installed its damage debuff");
            TestAssert.True(match.Runtime.SpellBuffs.AddStacking(victim, "WrathDebuff",
                BuffType.CombatDehancer, 3.0, worm.NetId, 10,
                new BuffPayload { SpellBlock = -15.0 }) is not null);
            TestAssert.True(match.Runtime.SpellBuffs.Add(victim, "SweepingBlow",
                BuffType.Stun, .5, worm.NetId) is not null);
            TestAssert.True(match.Runtime.SpellBuffs.Add(victim, "Propel",
                BuffType.Stun, 1.2, worm.NetId) is not null);
            TestAssert.True(match.Runtime.SpellBuffs.Add(victim, "WormUnrelatedInternal",
                BuffType.Internal, 10.0, victim.NetId) is not null);
            foreach (var name in names)
                TestAssert.True(victim.Buffs.Any(buff => buff.Name == name),
                    $"{name} was live before the death boundary");

            TestAssert.True(match.Runtime.Combat.Kill(worm, enemy.NetId));
            foreach (var name in names)
                TestAssert.True(victim.Buffs.Any(buff => buff.Name == name),
                    $"{name} follows the living recipient, not the caster's death");

            TestAssert.True(match.Runtime.Combat.Kill(victim, enemy.NetId));
            match.Runtime.Delivery.Flush();
            foreach (var name in names)
                TestAssert.False(victim.Buffs.Any(buff => buff.Name == name),
                    $"{name} cannot persist on the corpse");
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "WormUnrelatedInternal"),
                "the exact-name sweep leaves an unrelated inert internal buff intact");
            match.Runtime.Combat.ScheduleRespawn(victim);
            victim.SummonerSpells[0] = "SummonerRevive";
            TestAssert.True(match.TryCast(0, 0, victim, summoner: true),
                "real dead-only Revive restores the hero");
            TestAssert.True(victim.Alive);
            foreach (var name in names)
                TestAssert.False(victim.Buffs.Any(buff => buff.Name == name),
                    $"{name} cannot reappear on the revived life");
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "WormUnrelatedInternal"));
        }
    }
}
