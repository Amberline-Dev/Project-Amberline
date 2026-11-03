using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

// Janna runs from her shipped scripts (19 October 2026): Monsoon is a script channel (BbScriptChannels) whose first pulse comes when the 0.25 s
// windup ends, Howling Gale's charge is the buff HowlingGale and its wave a line missile cast by a placemarker TestCube, Zephyr's passive is the
// buff SowTheWind. The checks of these Janna files observe buffs, health, positions and packets, and the script engine's own channel; the
// hand-written JannaState they used to read is gone.
public static partial class ChampionLiveRegressionChecks
{
    /// <summary>The script channel the hero runs now (Janna's Monsoon), or null.</summary>
    private static BbChannel? JannaChannel(Match match, Entity hero) =>
        ((BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Janna")!).Channels?.Of(hero);

    /// <summary>Whether Howling Gale's charge buff is on the hero.</summary>
    private static bool JannaCharging(Entity hero) => hero.Buffs.Any(buff => buff.Name == "HowlingGale");

    /// <summary>The live TestCube placemarkers the Howling Gale releases left in the world (each lives four seconds).</summary>
    private static List<Entity> JannaCubes(Match match) =>
        match.Runtime.World.Entities.Values.Where(unit => unit.Placemarker && unit.Alive).ToList();

    /// <summary>The client missile id of the Howling Gale wave in flight (a line missile cast by the TestCube), or 0.</summary>
    private static uint JannaWaveMissile(Match match)
    {
        var cubes = JannaCubes(match).Select(cube => cube.NetId).ToHashSet();
        return match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
            .FirstOrDefault(projectile => projectile.MissileNetId != 0 && cubes.Contains(projectile.CasterNetId)).MissileNetId;
    }

    /// <summary>
    /// Steps the match until <paramref name="victim"/> runs a forced move started by the script spell <paramref name="effect"/> (the label of a
    /// scripted displacement is the running script's name: Move for Howling Gale, MoveAway for Monsoon), at most <paramref name="limit"/> seconds.
    /// </summary>
    private static ForcedMoveState? AwaitForcedMove(Match match, Entity victim, string effect, double limit = 1.0)
    {
        for (var elapsed = 0.0; elapsed < limit && victim.ForcedMove?.Effect != effect; elapsed += 0.01)
            match.Runtime.Tick(0.01);
        return victim.ForcedMove?.Effect == effect ? victim.ForcedMove : null;
    }

    [Check("spells-janna-b5-monsoon-buff-duration-and-cleanup")]
    public static void JannaMonsoonPublishesTenSecondsAndRemovesAtChannelEnd()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var match = new Match("Janna", Team.Order);
            var hero = match.Hero(0);
            match.Cast(0, 3, hero);
            Advance(match, .3);
            var buff = hero.Buffs.Single(b => b.Name == "ReapTheWhirlwind");
            TestAssert.True(JannaChannel(match, hero) is not null, "the windup ended: Monsoon is channelling");
            TestAssert.Near(10, buff.ExpiresAt, .1, "JAN-04: scripted buff lasts ten seconds (ExpiresAt is the time it has left)");
            match.Runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                // Batch 357: the add and the remove carry the binding name (ReapTheWhirlwind), not the BuffName "Reap The Whirlwind".
                var packet = capture.Packets.Single(p => Opcode(p) == 0xBF &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("ReapTheWhirlwind"));
                // Batch 365 (wire grace): a renewal-type add (BUFF_RENEW/REPLACE/STACKS_AND_RENEWS) carries one step of margin on its wire duration.
                TestAssert.Near((float)(10 + Amberline.Simulation.Spells.Bb.BbBuffRuntime.WireGraceStep), BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[17..]), 0);
            }
            Advance(match, cancel ? .5 : 3.5);
            TestAssert.True(hero.Buffs.Contains(buff), "the channel is still running before its four seconds are up");
            match.Runtime.Delivery.Flush(); match.Clear();
            if (cancel) match.Move(0, hero.Position + new Position2(100, 0));
            else Advance(match, 1.0);
            match.Runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Contains(buff), "completion and movement cancellation remove the ten-second buff");
            TestAssert.True(JannaChannel(match, hero) is null, "the channel is over");
            foreach (var capture in match.Captures)
                TestAssert.Equal(1, capture.Packets.Count(p => Opcode(p) == 0x7F &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("ReapTheWhirlwind")));
        }
    }

    [Check("spells-live-janna-monsoon-snapshot-and-team-effects")]
    public static void MonsoonSnapshotsHealingAndUsesTeamEffects()
    {
        // ReapTheWhirlwind.ChannelingStart: TickAmount = {35,55,75} + 0.175 AP, taken once at the start; OnBuffActivate heals every allied unit
        // below full health within 700 and BuffOnUpdateStats does it again every 0.5 s with the stored amount.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var rank in new[] { 1, 3 })
        {
            using var match = new Match("Janna", team, ally: true);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var ally = match.Hero(2);
            var minion = runtime.Waves!.SpawnFixture(team, MinionType.Melee, 0, "C", 0, 0);
            minion.Position = hero.Position + new Position2(-100, 100);
            minion.Waypoints.Clear();
            runtime.Tick(.01);
            hero.SpellRanks[3] = rank;
            hero.Inventory = [new InventorySlot(1058), null, null, null, null, null];
            runtime.HeroStats.Refresh(hero, push: false);
            TestAssert.True(hero.AbilityPower > 50, "Needlessly Large Rod raises ability power");
            var amount = (rank == 1 ? 35.0 : 75.0) + 0.175 * hero.AbilityPower;
            ally.Health = 100;
            minion.Health = 100;
            match.Clear();
            match.Cast(0, 3, hero);
            Advance(match, .3);
            runtime.Delivery.Flush();
            TestAssert.Near(100 + amount, ally.Health, 1.0, "the initial pulse heals the stored amount");
            AssertMinionHeal();

            // The stored amount does not follow ability power afterwards.
            hero.Inventory = [null, null, null, null, null, null];
            runtime.HeroStats.Refresh(hero, push: false);
            TestAssert.Near(0, hero.AbilityPower, 1e-9);
            TestAssert.Near(amount, NextPulse(), 1.0, $"{team}/rank {rank}: losing ability power cannot change channel pulses");

            // The target's healing reduction is evaluated for every pulse.
            runtime.Buffs.AddBuff(ally, "GrievousWound", BuffType.CombatDehancer, 5,
                match.Hero(1).NetId, new BuffPayload { HealingReceivedMult = -.5 });
            TestAssert.Near(amount / 2, NextPulse(), 1.0, "target healing reduction is evaluated for each pulse");

            // After four seconds the channel and its buff are gone.
            Advance(match, 4.0);
            TestAssert.True(JannaChannel(match, hero) is null, "the channel ended");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ReapTheWhirlwind"), "the buff left with the channel");

            double NextPulse()
            {
                // A pulse is a jump of tens of health in one step (every 0.5 s); regeneration adds a few hundredths per step and, summed over the
                // half second between pulses, would cross any small threshold before the pulse arrives.
                ally.Health = 100;
                for (var step = 0; step < 40; step++)
                {
                    var before = ally.Health;
                    runtime.Tick(1.0 / 30.0);
                    if (ally.Health - before > 2.0) return ally.Health - before;
                }
                return 0.0;
            }

            void AssertMinionHeal()
            {
                TestAssert.Near(100 + amount, minion.Health, 1.0);
                TestAssert.True(match.Captures[0].Packets.Any(p => Opcode(p) == 0xCC &&
                    p.Plaintext.Length >= 23 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[11..]) == minion.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[15..]) == 1 &&
                    Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(p.Plaintext.Span[19..]) - minion.Health) < 1.0),
                    "Monsoon publishes the minion's healed HP");
            }
        }
    }

    [Check("spells-live-janna-monsoon-knockback-assists")]
    public static void MonsoonKnockbackAwardsAnAssistWithoutHealingOrDamage()
    {
        // MoveAway.OnBuffActivate applies the assist marker for the buff's attacker (BBApplyAssistMarker, 10 s in the script, 15 s centrally).
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var casterDies in new[] { false, true })
        {
            using var match = new Match("Janna", team, ally: true);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var ally = match.Hero(2);
            var health = enemy.Health;
            match.Cast(0, 3, hero);
            Advance(match, .3);
            TestAssert.True(enemy.Buffs.Any(b => b.Name == "MoveAway"), $"{team}: Monsoon knocks the enemy away");
            TestAssert.True(enemy.Health <= health + 1.0, "Monsoon causes no damage");
            if (casterDies)
                runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, hero.Health + 1,
                    DamageType.True, DamageSourceKind.Spell);
            var gold = hero.Gold;
            runtime.Combat.ApplyHit(ally.NetId, enemy.NetId, enemy.Health + 1,
                DamageType.True, DamageSourceKind.Spell);
            TestAssert.Equal(1, ally.ChampionKills);
            TestAssert.Equal(1, hero.ChampionAssists, $"{team}/dead={casterDies}: knockback assist");
            TestAssert.True(hero.Gold > gold, "the assist also pays its gold reward");
        }
    }
}
