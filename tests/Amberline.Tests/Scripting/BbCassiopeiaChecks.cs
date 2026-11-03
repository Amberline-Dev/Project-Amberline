using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 317 (25 October 2026, wave 6): Cassiopeia plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written module).
/// Expected numbers are the literals of the shipped scripts (CharScriptCassiopeia, CassiopeiaDeadlyCadence, CassiopeiaNoxiousBlast with its poison
/// and haste, CassiopeiaMiasma with its poison and slow, CassiopeiaTwinFang, CassiopeiaPetrifyingGaze and its spell, CassiopeiaDeathParticle), read from
/// the bytecode listings, and the rank-one and rank-five figures of her inibins. Where the tooltip and the script differ, the script is the expected side and the
/// difference is recorded in the batch 317 record.
/// </summary>
public static class BbCassiopeiaChecks
{
    private static readonly double[] NoxiousPerTick = [25.0, 38.33, 51.66, 65.0, 78.33];
    private static readonly double[] NoxiousHaste = [0.15, 0.175, 0.2, 0.225, 0.25];
    private static readonly double[] MiasmaPerTick = [25.0, 35.0, 45.0, 55.0, 65.0];
    private static readonly double[] MiasmaSlow = [-0.15, -0.2, -0.25, -0.3, -0.35];
    private static readonly double[] TwinFangDamage = [60.0, 95.0, 130.0, 165.0, 200.0];
    private static readonly double[] GazeDamage = [200.0, 325.0, 450.0];

    /// <summary>A match with Cassiopeia trained to level six, every slot at <paramref name="rank"/>, the foes 250 away with 30 magic resist.</summary>
    private static Match Arena(int rank = 1, string foe = "Ezreal")
    {
        var match = new Match("Cassiopeia", foeChampion: foe);
        var experience = ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        TestAssert.Equal(6, match.Hero.Level, "Cassiopeia reaches level six (the experience of the seat helper)");
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = 0;
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"Cassiopeia's slot {slot} is trained at level six");
        }
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        match.Advance(0.5);
        // The harness sets 5000 health; the first stat refresh of a unit clamps it to its real maximum (a foe at level six has about 430).
        // Settle that here, so no damage is read from the clamp.
        foreach (var unit in new[] { match.Hero, match.Ally, match.Foe, match.FarFoe })
        {
            Refresh(match, unit);
            unit.Health = unit.MaxHealth;
        }
        return match;
    }

    /// <summary>Two stat refreshes: an update hook of a buff adds its stat during the previous refresh (the Veigar and Zilean checks read the same way).</summary>
    private static void Refresh(Match match, Entity unit)
    {
        match.Runtime.HeroStats.Refresh(unit, push: false);
        match.Runtime.HeroStats.Refresh(unit, push: false);
    }

    private static BuffState? Find(Entity unit, string name) => unit.Buffs.FirstOrDefault(buff => buff.Name == name);

    /// <summary>The speed a slow leaves: under 220 the stat keeps half of the distance below it (110 + speed / 2; the Zilean check's floor).</summary>
    private static double SoftFloor(double speed) => speed < 220.0 ? 110.0 + 0.5 * speed : speed;

    /// <summary>
    /// A cone cast. <see cref="Match.Cast"/> gives the request one point for both ends, which a cone reads as no direction; a real request has the
    /// caster's own point at its start and the aim at its end.
    /// </summary>
    private static bool CastCone(Match match, Position2 aim) =>
        match.Runtime.Spells.HandleCastRequest(match.Hero,
            new SpellCastRequest(3, false, new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z), new Position3(aim.X, 0, aim.Z), 0),
            match.Now);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks, passive
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Cassiopeia");
        foreach (var name in new[] { "CharScriptCassiopeia", "CassiopeiaNoxiousBlast", "CassiopeiaNoxiousBlastPoison", "CassiopeiaNoxiousBlastHaste",
                     "CassiopeiaMiasma", "CassiopeiaMiasmaPoison", "CassiopeiaTwinFang", "CassiopeiaPetrifyingGaze", "CassiopeiaPetrifyingGazeSpell",
                     "CassiopeiaDeadlyCadence", "CassiopeiaDeathParticle" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Cassiopeia");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(22, closure.ScriptNames.Count(), "the closure is 22 scripts (audit bb-coverage: 287 blocks, 46 functions, one dangling passive name)");

        var hero = new Entity { ChampionName = "Cassiopeia" };
        TestAssert.Equal(["CassiopeiaNoxiousBlast", "CassiopeiaMiasma", "CassiopeiaTwinFang", "CassiopeiaPetrifyingGaze"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Cassiopeia.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "the ultimate is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Cassiopeia")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 6300, Id: 69, RetailBot: false },
            "playable as a mid laner for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Cassiopeia"), "listed once");

        // The tables the figures rest on, a second time as literals.
        var poison = Blocks(Script("CassiopeiaNoxiousBlast"), "SelfExecute").First(item => item.Text("DestVar") == "PoisonPerTick");
        TestAssert.Equal(NoxiousPerTick, Numbers(poison.Param("SrcValueByLevel")!), "Noxious Blast's poison per tick by rank (SelfExecute)");
        var haste = Blocks(Script("CassiopeiaNoxiousBlast"), "OnBuffDeactivate").First(item => item.Text("DestVar") == "MoveSpeedMod");
        TestAssert.Equal(NoxiousHaste, Numbers(haste.Param("SrcValueByLevel")!), "Noxious Blast's haste by rank (OnBuffDeactivate, Cassiopeia's MoveSpeedMod)");
        // Batch 352: the poison's add carries the wiki's three seconds (BbScriptOverrides; the shipped literal is 3.25).
        var poisonAdd = Blocks(Script("CassiopeiaNoxiousBlast"), "OnBuffDeactivate").First(item => item.Text("BuffName") == "CassiopeiaNoxiousBlastPoison");
        TestAssert.Equal(3.0, poisonAdd.Number("Duration")!.Value, "Noxious Blast's poison lasts three seconds (the shipped 3.25 overridden)");
        var dart = Blocks(Script("CassiopeiaMiasma"), "SpellOnMissileEnd").First(item => item.Text("DestVar") == "DamagePerTick");
        TestAssert.Equal(MiasmaPerTick, Numbers(dart.Param("SrcValueByLevel")!), "Miasma's damage per second by rank (SpellOnMissileEnd)");
        var slow = Blocks(Script("CassiopeiaMiasma"), "SpellOnMissileEnd").First(item => item.Text("DestVar") == "MoveSpeedMod");
        TestAssert.Equal(MiasmaSlow, Numbers(slow.Param("SrcValueByLevel")!), "Miasma's slow by rank (SpellOnMissileEnd)");
        var bolt = Blocks(Script("CassiopeiaTwinFang"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal(TwinFangDamage, Numbers(bolt.Param("DamageByLevel")!), "Twin Fang damage by rank (TargetExecute)");
        var gaze = Blocks(Script("CassiopeiaPetrifyingGazeSpell"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal(GazeDamage, Numbers(gaze.Param("DamageByLevel")!), "Petrifying Gaze damage by rank (TargetExecute)");
    }

    [Check("scripting.bb-cassiopeia-passive-is-deadly-cadence-and-no-other-buff-is-added")]
    public static void PassiveIsDeadlyCadenceOnly()
    {
        using var match = Arena(1);
        // The inibin's passive name is Anivia's Rebirth Marker (the closure's one dangling name): nothing adds it to Cassiopeia.
        TestAssert.True(Find(match.Hero, "Rebirth Marker") is null, "no Rebirth Marker: that passive script is Anivia's");
        TestAssert.True(Find(match.Hero, "CassiopeiaDeadlyCadence") is null, "Deadly Cadence waits for a spell cast (CharOnSpellCast)");
        TestAssert.True(Find(match.Hero, "ChampionChampionDelta") is not null, "the shared delta buff is on from activation (CharOnActivate)");
        TestAssert.True(Find(match.Hero, "CassiopeiaDeathParticle") is null, "the death particle is not on a living Cassiopeia");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Deadly Cadence
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-deadly-cadence-cuts-later-spells-by-ten-percent-a-stack")]
    public static void DeadlyCadenceCutsTheCostOfLaterSpells()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var foe = match.Foe;
        // Each cast adds a stack of Deadly Cadence (five at most, five seconds). The first cast pays full cost; each later cast pays 10 percent less per stack.
        var expected = new[] { 35.0, 70.0 * 0.9, 50.0 * 0.8 };
        var costs = new List<double>();
        for (var index = 0; index < expected.Length; index++)
        {
            var slot = index;
            hero.SpellCooldowns[slot] = 0.0;
            hero.Mana = hero.MaxMana;
            var before = hero.Mana;
            var at = foe.Position;
            // Twin Fang is a unit-target bolt (the foe); the two others are cast at the point.
            TestAssert.True(slot == 2 ? match.Cast(slot, foe) : match.Cast(slot, at: at), $"slot {slot} is cast");
            // Batch 340: a windup pays at its cast frame (was 0.1 s after the press).
            match.Advance(Math.Max(0.1, match.CastFrameOf(slot)));
            costs.Add(before - hero.Mana);
            TestAssert.Near(expected[index], costs[index], 1.0, $"cast {index + 1} costs {expected[index]} (the stacks before it cut the base cost)");
            match.Advance(0.5);
        }
        TestAssert.True(Find(hero, "CassiopeiaDeadlyCadence") is { Stacks: 3 }, "three stacks after three casts");
        // The stacks run out together after five seconds from the last renewal (their duration), and the cost goes back to full.
        match.Advance(6.0);
        TestAssert.True(Find(hero, "CassiopeiaDeadlyCadence") is null, "the buff has ended");
        hero.SpellCooldowns[0] = 0.0;
        hero.Mana = hero.MaxMana;
        var full = hero.Mana;
        TestAssert.True(match.Cast(0, at: foe.Position), "Noxious Blast is cast again");
        match.Advance(Math.Max(0.1, match.CastFrameOf(0)));
        TestAssert.Near(35.0, full - hero.Mana, 1.0, "back to full cost once the stacks have ended");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Noxious Blast
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-noxious-blast-poisons-after-its-delay-and-hastes-her-on-a-champion")]
    public static void NoxiousBlastPoisonsAfterItsDelayAndHastesHer()
    {
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = Arena(rank);
            var hero = match.Hero;
            var foe = match.Foe;
            var speed = hero.Speed;
            TestAssert.True(match.Cast(0, at: foe.Position), $"rank {rank}: Noxious Blast is cast at the enemy's feet");
            // The area is a delayed one: nothing on the foe at the cast, the poison a quarter second later.
            TestAssert.True(Find(foe, "CassiopeiaNoxiousBlastPoison") is null, $"rank {rank}: no poison before the blast");
            // Every damage step of the next five seconds is kept: the first tick comes with the poison, at the blast.
            var ticks = new List<double>();
            for (var step = 0; step < 150; step++)
            {
                var before = foe.Health;
                match.Advance(Step);
                if (before - foe.Health > 0.5) ticks.Add(before - foe.Health);
                if (step == 30) // one second: the cast takes a quarter second and the blast's buff another quarter
                {
                    TestAssert.True(Find(foe, "CassiopeiaNoxiousBlastPoison") is not null, $"rank {rank}: the foe is poisoned when the blast lands");
                    TestAssert.True(Find(hero, "CassiopeiaNoxiousBlastHaste") is not null, $"rank {rank}: a champion hit gives Cassiopeia the haste buff");
                    Refresh(match, hero);
                    TestAssert.Near(1.0 + NoxiousHaste[rank - 1], hero.Speed / speed, 0.02,
                        $"rank {rank}: {NoxiousHaste[rank - 1] * 100}% movement speed (the blast's MoveSpeedMod by rank)");
                }
            }
            // The poison ticks at once and then each second (ExecuteImmediately). Batch 352 (owner, 26 October 2026, "wiki value per spell"):
            // the script's 3.25 seconds gave a fourth tick at three seconds; the tooltip's three seconds, the per-tick table (a third of
            // 75/115/155/195/235) and the wiki's 90% ratio (three ticks of 0.3) are three, and BbScriptOverrides loads 3.0 into the add.
            TestAssert.Equal(3, ticks.Count, $"rank {rank}: three poison ticks (the tooltip's three seconds, ticking at once and each second)");
            foreach (var tick in ticks)
                TestAssert.Near(Mitigated(NoxiousPerTick[rank - 1], 30.0), tick, 0.5,
                    $"rank {rank}: each tick is {NoxiousPerTick[rank - 1]} magic against 30 magic resist");
            TestAssert.True(Find(foe, "CassiopeiaNoxiousBlastPoison") is null, $"rank {rank}: the poison has ended");
        }
    }

    [Check("scripting.bb-cassiopeia-noxious-blast-haste-is-for-a-champion-only")]
    public static void NoxiousBlastHasteNeedsAChampion()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var minion = BbM3.Enemy(match, "Minion", new Position2(7300, 7000));
        // The foe would stand inside the blast's 200 units (Arena places it at 7250): it moves out of the area first.
        match.Foe.Position = new Position2(9000, 9000);
        var speed = hero.Speed;
        TestAssert.True(match.Cast(0, at: minion.Position), "Noxious Blast is cast at the minion");
        match.Advance(0.5);
        TestAssert.True(Find(minion, "CassiopeiaNoxiousBlastPoison") is not null, "the minion is poisoned");
        TestAssert.True(Find(hero, "CassiopeiaNoxiousBlastHaste") is null, "no haste for a minion (the script's IsType hero test)");
        Refresh(match, hero);
        TestAssert.Near(1.0, hero.Speed / speed, 0.01, "Cassiopeia's speed is unchanged");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Miasma
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-miasma-cloud-poisons-and-slows-the-units-inside-it")]
    public static void MiasmaPoisonsAndSlowsInside()
    {
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = Arena(rank);
            var hero = match.Hero;
            var foe = match.Foe;
            // The cloud lands on the cast point, 185 units wide at first and growing; the foe stands on the point.
            var point = foe.Position;
            var speed = foe.Speed;
            // The poison ticks each second while the foe stays in the cloud (ExecuteImmediately): every tick is the cloud's damage per second.
            var ticks = new List<double>();
            TestAssert.True(match.Cast(1, at: point), $"rank {rank}: Miasma is cast on the foe");
            for (var step = 0; step < 90; step++)
            {
                var before = foe.Health;
                match.Advance(Step);
                if (before - foe.Health > 0.5) ticks.Add(before - foe.Health);
            }
            TestAssert.True(Find(foe, "CassiopeiaMiasmaPoison") is not null, $"rank {rank}: the foe in the cloud is poisoned");
            TestAssert.True(Find(foe, "Slow") is not null, $"rank {rank}: the foe in the cloud is slowed");
            Refresh(match, foe);
            TestAssert.Near(SoftFloor(speed * (1.0 + MiasmaSlow[rank - 1])), foe.Speed, 1.0,
                $"rank {rank}: the slow by rank ({MiasmaSlow[rank - 1]}), then the soft floor under 220");
            TestAssert.True(ticks.Count >= 2, $"rank {rank}: the poison ticks each second while the foe stays in the cloud");
            foreach (var tick in ticks)
                TestAssert.Near(Mitigated(MiasmaPerTick[rank - 1], 30.0), tick, 0.5, $"rank {rank}: each tick is {MiasmaPerTick[rank - 1]} magic against 30 magic resist");
            // The cloud lasts seven seconds: after it the poison runs out within its own two seconds and no new one comes.
            match.Advance(8.0);
            TestAssert.True(Find(foe, "CassiopeiaMiasmaPoison") is null, $"rank {rank}: no poison once the cloud is gone");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Twin Fang
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-twin-fang-refunds-to-half-a-second-on-a-poisoned-target")]
    public static void TwinFangRefundsOnAPoisonedTarget()
    {
        // An unpoisoned target: the full five-second cooldown, the damage of the rank.
        using (var match = Arena(1))
        {
            var foe = match.Foe;
            var hp = foe.Health;
            TestAssert.True(match.Cast(2, foe), "Twin Fang is cast at an unpoisoned enemy");
            match.Advance(1.0);
            // The cooldown starts with the hit, a quarter second after the cast: one second on, about four are left.
            TestAssert.Near(4.0, match.Hero.SpellCooldowns[2], 0.2, "an unpoisoned target keeps the five-second cooldown");
            var dealt = hp - foe.Health + foe.HealthRegen;
            TestAssert.Near(Mitigated(TwinFangDamage[0], 30.0), dealt, 1.5, "60 magic against 30 magic resist (no ability power)");
        }
        // A poisoned target (Noxious Blast's poison): the cooldown falls to the refund (the inibin's Effect3Amount, 0.5 seconds); the damage is the same.
        using (var match = Arena(1))
        {
            var foe = match.Foe;
            TestAssert.True(match.Cast(0, at: foe.Position), "Noxious Blast poisons the foe");
            match.Advance(0.5);
            TestAssert.True(Find(foe, "CassiopeiaNoxiousBlastPoison") is not null, "the foe is poisoned");
            match.Hero.SpellCooldowns[2] = 0.0;
            TestAssert.True(match.Cast(2, foe), "Twin Fang is cast at the poisoned enemy");
            match.Advance(1.0);
            // The refund (0.5 seconds from the hit, the script's SetSlotSpellCooldownTimeVer2) is over within the second: Twin Fang is ready again.
            // The script's damage does not depend on the poison (no branch around BBApplyDamage), so the bolt is not measured here.
            TestAssert.True(match.Hero.SpellCooldowns[2] <= 0.01, "a poisoned target leaves a cooldown of 0.5 seconds");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Petrifying Gaze
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-petrifying-gaze-stuns-the-enemies-facing-her-and-slows-the-rest")]
    public static void PetrifyingGazeStunsFacingAndSlowsTheRest()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var facing = match.Foe;
        var away = match.FarFoe;
        // Both stand in front of her, close to the aim; the first faces her, the second faces away.
        facing.Position = new Position2(7250, 7000);
        facing.Facing = new Position2(-1.0, 0.0);
        away.Position = new Position2(7350, 7060);
        away.Facing = new Position2(1.0, 0.0);
        var awaySpeed = away.Speed;
        var hpFacing = facing.Health;
        var hpAway = away.Health;
        hero.Facing = new Position2(1.0, 0.0);
        TestAssert.True(CastCone(match, facing.Position), "Petrifying Gaze is cast towards the front");
        // The cast takes half a second and its hit (the stun and the damage) lands with it.
        match.Advance(1.0);
        TestAssert.True(Find(facing, "CassiopeiaPetrifyingGaze") is not null, "the enemy facing her carries the stun buff");
        TestAssert.True(facing.AnyBuffOrStatus(buff => buff.Payload.ActionDisabled), "and cannot act (SetStunned)");
        TestAssert.True(Find(away, "CassiopeiaPetrifyingGaze") is null, "the enemy facing away is not stunned");
        TestAssert.True(Find(away, "Slow") is not null, "the enemy facing away is slowed");
        Refresh(match, away);
        TestAssert.Near(SoftFloor(awaySpeed * 0.4), away.Speed, 1.0, "slowed by 60 percent (the spell's SpellBuffAdd of MoveSpeedMod -0.6), then the soft floor");
        TestAssert.Near(Mitigated(GazeDamage[0], 30.0), hpFacing - facing.Health + facing.HealthRegen, 1.5, "both take the rank's 200 magic damage (the spell's ApplyDamage is outside the if)");
        TestAssert.Near(Mitigated(GazeDamage[0], 30.0), hpAway - away.Health + away.HealthRegen, 1.5, "the enemy facing away takes the same damage");
        match.Advance(2.5);
        TestAssert.True(Find(facing, "CassiopeiaPetrifyingGaze") is null, "the stun is over after its two seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-cassiopeia-death-particle-stays-on-the-corpse-and-goes-with-the-respawn")]
    public static void DeathParticleFollowsTheDeath()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        // A direct Kill does not schedule the respawn (the lethal-hit caller does); the check plays that caller, as the scenario does.
        match.Runtime.Combat.ScheduleRespawn(hero);
        // A corpse's self-buff poll runs about once a second (BbHookRouter.RunUpdateSelfBuffActions), so the particle comes within two.
        match.Advance(2.0);
        TestAssert.True(!hero.Alive, "Cassiopeia is dead");
        TestAssert.True(Find(hero, "CassiopeiaDeathParticle") is not null, "the death particle is added while dead (CharScriptCassiopeia, every quarter second)");
        match.Advance(5.0);
        TestAssert.True(Find(hero, "CassiopeiaDeathParticle") is not null, "the particle stays through the stone form (renewed while dead)");
        for (var elapsed = 0.0; !hero.Alive && elapsed < 300.0; elapsed += 0.5) match.Advance(0.5);
        TestAssert.True(hero.Alive, "Cassiopeia respawns");
        TestAssert.True(Find(hero, "CassiopeiaDeathParticle") is null, "the respawn removes the death particle (CharOnResurrect)");
    }
}
