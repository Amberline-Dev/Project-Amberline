using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 2 (19 October 2026): Zilean plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever existed). Expected
/// numbers are the literals of the shipped scripts and spell inibins; the scenario <c>S-zilean</c> shows the same facts, and more, on a headless match. Written
/// 19 October 2026. Chronoshift is not a zombie life: its script lowers the lethal <c>DamageAmount</c> in <c>BuffOnPreDamage</c> (priority 4) to leave one health
/// point and adds <c>ChronoRevive</c> for three seconds, whose end heals.
/// </summary>
public static class BbZileanChecks
{
    private static readonly double[] BombDamage = [90.0, 145.0, 200.0, 260.0, 320.0];

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Zilean at rank <paramref name="rank"/>, 80 ability power (a Needlessly Large Rod), the enemy 300 away, the ally 150 behind the enemy's side.</summary>
    private static Match Arena(int rank = 5, string foe = "Orianna")
    {
        var match = new Match("Zilean", foeChampion: foe);
        match.Learn(rank);
        match.GiveAbilityPower();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    // The level-one health bar is smaller than the heal of Chronoshift: a level-eighteen hero shows the whole amount.
    private static void Grow(Match match, Entity hero)
    {
        hero.Level = 18;
        match.Runtime.HeroStats.Refresh(hero, push: false);
        TestAssert.True(hero.MaxHealth > 1500.0, "a level-eighteen health bar");
    }

    private static double Blast(Match match, int rank, Entity victim) =>
        Mitigated(BombDamage[rank - 1] + 0.9 * match.Hero.AbilityPower, victim.SpellBlock);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-zilean-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Zilean");
        foreach (var name in new[] { "CharScriptZilean", "TimeBomb", "Rewind", "TimeWarp", "ChronoShift", "HeightenedLearning", "TimeBombCountdown", "TimeWarpSlow", "ChronoRevive" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Zilean");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(66, closure.ScriptNames.Count(), "the closure is 66 scripts: ten of his own, eight system buffs and the 48 the Chronoshift tests name (Yorick's revival tree and what it reaches)");

        var hero = new Entity { ChampionName = "Zilean" };
        TestAssert.Equal(["TimeBomb", "Rewind", "TimeWarp", "ChronoShift"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Zilean.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Zilean")!;
        TestAssert.True(info is { Playable: true, Role: "support", DefaultIp: 1350, Id: 26, RetailBot: false }, "playable as a support for 1350 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Zilean"), "listed once");

        // The tables the figures rest on, a second time as literals (the tooltips of fontconfig_en_US agree).
        TestAssert.Equal(BombDamage, ByLevel("TimeBomb", "TargetExecute", "DamageLevel"), "Time Bomb damage by rank");
        TestAssert.Equal([600.0, 850.0, 1100.0], ByLevel("ChronoShift", "TargetExecute", "BaseHealthBoost"), "Chronoshift heal by rank");
        TestAssert.Equal(0.08, Blocks(Script("HeightenedLearning"), "BuffOnUpdateStats").First(block => block.Function == "BBIncStat").Number("Delta")!.Value, "Heightened Learning: 8 percent");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Heightened Learning
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-zilean-heightened-learning-gives-allied-heroes-eight-percent-experience")]
    public static void AlliedHeroesLearnEightPercentFaster()
    {
        using var match = Arena();
        match.Advance(4.5);
        TestAssert.True(Of(match.Hero, "HeightenedLearning") is not null && Of(match.Ally, "HeightenedLearning") is not null, "Zilean and his ally carry the buff");
        TestAssert.True(Of(match.Foe, "HeightenedLearning") is null && Of(match.FarFoe, "HeightenedLearning") is null, "the enemies do not");
        TestAssert.Near(0.08, match.Ally.LoadoutExperiencePercent, 1e-9, "the ally's experience bonus is 8 percent");
        TestAssert.Near(0.0, match.Foe.LoadoutExperiencePercent, 1e-9, "the enemy's is none");
        double Gain(Entity hero)
        {
            var before = hero.Experience;
            match.Runtime.Combat.AwardHeroProgress(hero, experience: 100.0);
            return hero.Experience - before;
        }
        TestAssert.Near(108.0, Gain(match.Ally), 1e-6, "100 experience is 108 for the ally");
        TestAssert.Near(108.0, Gain(match.Hero), 1e-6, "and for Zilean himself");
        TestAssert.Near(100.0, Gain(match.Foe), 1e-6, "and 100 for the enemy");
        // Renewed every four seconds for five. Retail footage: his passive leaves his allies while he is dead (owner
        // decision 20 Oct 2026, "dead champions' passives stop"). His own script tests Owner IS_NOT_DEAD — that is what
        // the footage shows, so the bonus ends five seconds after the last renewal.
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(5.6);
        TestAssert.True(Of(match.Ally, "HeightenedLearning") is null, "the ally's buff ran out");
        TestAssert.Near(0.0, match.Ally.LoadoutExperiencePercent, 1e-9, "and so did the bonus");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Time Bomb
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-zilean-time-bomb-counts-three-two-one-and-explodes-after-four-seconds")]
    public static void TheBombTicksAndExplodes()
    {
        using var match = Arena();
        var hero = match.Hero;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, match.Foe), "Q casts");
        match.Advance(0.4);
        var bomb = Of(match.Foe, "TimeBomb");
        TestAssert.True(bomb is not null, "the bomb is on the target when the cast ends (a CastType 0 spell has no missile)");
        TestAssert.Near(3.85, bomb!.ExpiresAt, 0.1, "four seconds from the landing");
        TestAssert.Near(130.0, mana - hero.Mana, 1.0, "130 mana at rank 5");
        // Batch 340: the cooldown starts at the cast frame (CastFrame), not at the press.
        TestAssert.Near(10.0, hero.SpellCooldowns[0] + 0.4 - CastFrame("TimeBomb"), 0.1, "cooldown 10 s");
        match.Advance(4.6);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(4, hits.Count, "three countdown ticks and the blast");
        // The countdown reads 3, 2, 1 and is not reduced by magic resistance (TimeBombCountdown sets DamageAmount to TickDamage).
        TestAssert.Equal([3.0, 2.0, 1.0], hits.Take(3).Select(hit => Math.Round(hit.Dealt, 3)).ToArray(), "3, 2, 1");
        TestAssert.Near(1.0, hits[1].At - hits[0].At, 0.07, "a second apart");
        TestAssert.Near(Blast(match, 5, match.Foe), hits[3].Dealt, 0.05, "320 + 0.9 x 80 magic after 30 resistance");
        TestAssert.Near(3.0, hits[3].At - hits[0].At, 0.1, "the blast comes three seconds after the first tick: four after the landing");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "nobody out of the 350 is hurt");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "nor a friend");
        TestAssert.True(Of(match.Foe, "TimeBomb") is null && Of(match.Foe, "TimeBombCountdown") is null, "both buffs are gone");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-zilean-time-bomb-hurts-the-enemies-around-the-carrier-only")]
    public static void TheBlastHurtsEnemiesWithinThreeHundredFiftyOfTheCarrier()
    {
        using var match = Arena(rank: 1);
        match.FarFoe.Position = new Position2(7300, 7340);
        match.Ally.Position = new Position2(7300, 7200);
        TestAssert.True(match.Cast(0, match.Foe), "Q casts");
        match.Advance(5.0);
        var rank = 1;
        TestAssert.Near(Blast(match, rank, match.Foe), HitsOn(match, "Foe").Last().Dealt, 0.05, "the carrier takes the blast");
        TestAssert.Near(Blast(match, rank, match.FarFoe), HitsOn(match, "FarFoe").Single().Dealt, 0.05, "an enemy 340 away takes it too");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "a friend 200 away does not");
        match.Refill();
        match.FarFoe.Position = new Position2(7300, 7400);
        var before = match.FarFoe.Health;
        TestAssert.True(match.Cast(0, match.Foe), "again");
        match.Advance(5.0);
        TestAssert.True(match.FarFoe.Health >= before - 1e-6, "an enemy 400 away is outside the 350");
    }

    [Check("scripting.bb-zilean-time-bomb-on-an-ally-has-no-countdown")]
    public static void AnAlliedCarrierTakesNothingAndHurtsTheEnemiesAround()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7300, 7000);
        match.Ally.Position = new Position2(7150, 7000);
        TestAssert.True(match.Cast(0, match.Ally), "Q at an ally casts");
        match.Advance(5.0);
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "no countdown and no blast on the ally");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the enemy 150 away takes the blast");
        TestAssert.Near(Blast(match, 5, match.Foe), hits[0].Dealt, 0.05, "the blast");
    }

    [Check("scripting.bb-zilean-second-bomb-detonates-the-first-at-once")]
    public static void RewindThenAnotherBombSetsTheFirstOff()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, match.Foe), "first bomb");
        match.Advance(1.3);
        TestAssert.False(match.Cast(0, match.Foe), "Q is on cooldown");
        TestAssert.True(match.Cast(1, match.Hero), "Rewind casts");
        match.Advance(0.4);
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-6, "Time Bomb's cooldown is gone (10 s less 10)");
        var secondAt = match.Now;
        TestAssert.True(match.Cast(0, match.Foe), "second bomb");
        match.Advance(0.4);
        var early = HitsOn(match, "Foe").Where(hit => hit.At >= secondAt).ToArray();
        TestAssert.Equal(1, early.Length, "the first bomb went off with the cast");
        TestAssert.Near(Blast(match, 5, match.Foe), early[0].Dealt, 0.05, "for the rank-five blast");
        TestAssert.Equal(1, match.Foe.Buffs.Count(buff => buff.Name == "TimeBomb"), "one bomb is on the unit: the new one");
        match.Advance(4.6);
        var all = HitsOn(match, "Foe");
        TestAssert.Equal(1, all.Count(hit => hit.Dealt > 100.0 && hit.At > secondAt + 1.0), "and it goes off four seconds after its own landing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-zilean-time-bomb-goes-off-at-once-on-a-dying-carrier")]
    public static void ACarrierThatDiesBlowsUpAtOnce()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(7350, 7000);
        TestAssert.True(match.Cast(0, match.Foe), "Q casts");
        match.Advance(1.5);
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "nothing yet");
        match.Runtime.Combat.Kill(match.Foe, match.Ally.NetId);
        var hits = HitsOn(match, "FarFoe");
        TestAssert.Equal(1, hits.Count, "the unit next to the corpse is hit as the bomb leaves");
        TestAssert.Near(Blast(match, 5, match.FarFoe), hits[0].Dealt, 0.05, "with the full blast");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-zilean-time-bomb-outlives-its-caster")]
    public static void ADeadZileanStillSetsTheBombOff()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, match.Foe), "Q casts");
        match.Advance(1.2);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(3.8);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(4, hits.Count, "the ticks and the blast still happen");
        TestAssert.Near(Blast(match, 5, match.Foe), hits[3].Dealt, 0.05, "the blast");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Rewind, Time Warp
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-zilean-rewind-takes-ten-seconds-off-the-other-three-spells")]
    public static void RewindCutsTheOtherCooldowns()
    {
        using var match = Arena(rank: 2);
        var hero = match.Hero;
        hero.SpellCooldowns[0] = 6.0;
        hero.SpellCooldowns[2] = 14.0;
        hero.SpellCooldowns[3] = 100.0;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(1, hero), "Rewind casts");
        match.Advance(0.3);
        TestAssert.Near(50.0, mana - hero.Mana, 1.0, "50 mana");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-6, "Q: 6 less 10 floors at zero");
        TestAssert.Near(3.7, hero.SpellCooldowns[2], 0.1, "E: 14 less 10");
        TestAssert.Near(89.7, hero.SpellCooldowns[3], 0.1, "R: 100 less 10");
        TestAssert.Near(14.7, hero.SpellCooldowns[1], 0.1, "its own cooldown, 15 s at rank 2");
        match.Advance(15.0);
        TestAssert.True(match.Cast(1, hero), "with everything ready it still casts");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    private static double SoftCap(double speed) =>
        speed > 490.0 ? 475.0 + 0.5 * (speed - 490.0) : speed > 415.0 ? 415.0 + 0.8 * (speed - 415.0) : speed < 220.0 ? 110.0 + 0.5 * speed : speed;

    [Check("scripting.bb-zilean-time-warp-hastes-an-ally-and-slows-an-enemy")]
    public static void TimeWarpIsFiftyFivePercentEitherWay()
    {
        using var match = Arena(rank: 3);
        var hero = match.Hero;
        var ally = match.Ally;
        var foe = match.Foe;
        var allySpeed = ally.Speed;
        var foeSpeed = foe.Speed;
        var selfSpeed = hero.Speed;
        TestAssert.True(match.Cast(2, ally), "E at the ally casts");
        match.Advance(0.4);
        TestAssert.True(Of(ally, "TimeWarp") is not null, "the haste");
        TestAssert.Near(SoftCap(allySpeed * 1.55), ally.Speed, allySpeed * 0.06, "about 55 percent faster (the percent bonuses of the ally add up, then the soft cap)");
        TestAssert.True(ally.Speed > allySpeed * 1.3, "clearly faster");
        TestAssert.Near(4.0, Of(ally, "TimeWarp")!.ExpiresAt, 0.4, "4 seconds at rank 3");
        match.Refill();
        TestAssert.True(match.Cast(2, foe), "E at the enemy casts");
        match.Advance(0.4);
        TestAssert.True(Of(foe, "TimeWarpSlow") is not null, "the slow");
        TestAssert.Near(SoftCap(foeSpeed * 0.45), foe.Speed, 0.5, "55 percent slower (multiplicative), through the soft cap");
        match.Refill();
        TestAssert.True(match.Cast(2, hero), "E at himself casts");
        match.Advance(0.4);
        TestAssert.Near(SoftCap(selfSpeed * 1.55), hero.Speed, 0.5, "Zilean hastes himself the same");
        // Time Warp and Chronoshift are for champions only (their flags carry AffectHeroes, not AffectMinions).
        var minion = Enemy(match, "Minion", new Position2(7100, 7000), team: Team.Order);
        match.Refill();
        TestAssert.False(match.Cast(2, minion), "E at an allied minion is refused");
        TestAssert.False(match.Cast(3, minion), "R at an allied minion is refused");
        match.Advance(4.5);
        TestAssert.Near(allySpeed, ally.Speed, 1e-6, "the ally is back to his speed");
        TestAssert.Near(foeSpeed, foe.Speed, 1e-6, "the enemy too");
        TestAssert.Near(selfSpeed, hero.Speed, 1e-6, "and Zilean");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Chronoshift
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-zilean-chronoshift-leaves-one-health-and-heals-after-three-seconds")]
    public static void TheLethalHitLeavesOneHealthPoint()
    {
        using var match = Arena(rank: 1);
        match.Learn(1);
        var ally = match.Ally;
        Grow(match, ally);
        ally.Health = 500.0;
        ally.SummonerCooldowns[0] = 5.0;
        ally.SummonerCooldowns[1] = 100.0;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, ally), "R casts on an ally");
        match.Advance(0.4);
        TestAssert.Near(200.0, mana - match.Hero.Mana, 1.0, "200 mana");
        TestAssert.Near(180.0, match.Hero.SpellCooldowns[3] + 0.4, 0.1, "cooldown 180 s");
        TestAssert.Near(6.6, Of(ally, "ChronoShift")!.ExpiresAt, 0.1, "the rune lasts seven seconds");
        // Not lethal: the rune stays.
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, ally.NetId, 100.0, DamageType.True, DamageSourceKind.Spell);
        TestAssert.True(Of(ally, "ChronoShift") is not null && Of(ally, "ChronoRevive") is null, "a hit that does not kill leaves the rune alone");
        TestAssert.Near(400.0, ally.Health, 4.0, "the damage landed (less the regeneration of a second)");
        match.Runtime.Buffs.AddBuff(ally, "AcceptanceStun", BuffType.Stun, 10.0, match.Foe.NetId, canMitigateDuration: false);
        match.Runtime.Buffs.AddBuff(ally, "AcceptanceSlow", BuffType.Slow, 10.0, match.Foe.NetId, canMitigateDuration: false);
        // Lethal.
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, ally.NetId, 6000.0, DamageType.Magic, DamageSourceKind.Spell);
        match.Advance(0.1);
        TestAssert.True(ally.Alive, "alive");
        TestAssert.Near(1.0, ally.Health, 1e-6, "with one health point");
        var stasis = Of(ally, "ChronoRevive");
        TestAssert.True(stasis is not null, "in stasis");
        TestAssert.Near(2.9, stasis!.ExpiresAt, 0.1, "for three seconds");
        TestAssert.True(Of(ally, "ChronoShift") is null, "the rune is spent");
        TestAssert.True(Of(ally, "AcceptanceStun") is null && Of(ally, "AcceptanceSlow") is null, "the stasis cleansed the control and the slow");
        TestAssert.Near(3.0, ally.SummonerCooldowns[0] + 0.1, 0.1, "a summoner spell with at most six seconds is set to three");
        TestAssert.Near(100.0, ally.SummonerCooldowns[1] + 0.1, 0.5, "one with more is left");
        // The stasis: invulnerable, and nothing else.
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, ally.NetId, 3000.0, DamageType.True, DamageSourceKind.Spell);
        TestAssert.Near(1.0, ally.Health, 1e-6, "nothing lands");
        match.Advance(3.0);
        TestAssert.True(Of(ally, "ChronoRevive") is null, "the stasis ended");
        var heal = 600.0 + 2.0 * (match.Hero.AbilityPower + 0.1);
        TestAssert.Near(1.0 + heal, ally.Health, 3.0, "healed 600 + 2 x (ability power + 0.1), plus the one it kept");
        TestAssert.True(ally.Alive, "alive");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-zilean-chronoshift-on-himself-and-the-rune-that-is-never-hit")]
    public static void ZileanCanSaveHimselfAndTheRuneEndsUnused()
    {
        using var match = Arena(rank: 1);
        match.Learn(3);
        var hero = match.Hero;
        Grow(match, hero);
        hero.Health = 400.0;
        TestAssert.True(match.Cast(3, hero), "R at himself casts");
        match.Advance(0.3);
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 9000.0, DamageType.Physical, DamageSourceKind.Spell);
        match.Advance(0.1);
        TestAssert.True(hero.Alive && Math.Abs(hero.Health - 1.0) < 1e-6, "he survives with one health point");
        TestAssert.True(match.Cast(0, match.Foe) == false, "and cannot cast in the stasis");
        match.Advance(3.1);
        TestAssert.Near(1.0 + 1100.0 + 2.0 * (hero.AbilityPower + 0.1), hero.Health, 3.0, "rank three heals 1100 + 2 x (ability power + 0.1)");

        // The rune that is never hit ends on its own after seven seconds and heals nobody.
        match.Refill();
        var ally = match.Ally;
        Grow(match, ally);
        ally.Health = 300.0;
        TestAssert.True(match.Cast(3, ally), "cast on the ally");
        match.Advance(7.4);
        TestAssert.True(Of(ally, "ChronoShift") is null && Of(ally, "ChronoRevive") is null, "the rune ended without a trace");
        TestAssert.Near(300.0, ally.Health, 40.0, "and healed nobody (the regeneration of seven seconds only; a heal is 600 or more)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-zilean-chronoshift-rune-persists-through-a-direct-death")]
    public static void TheRuneSurvivesADeathThatSkipsTheDamagePipeline()
    {
        using var match = Arena(rank: 1);
        match.Learn(1);
        var ally = match.Ally;
        TestAssert.True(match.Cast(3, ally), "R casts on an ally");
        match.Advance(0.4);
        TestAssert.True(Of(ally, "ChronoShift") is not null, "the rune");
        // Kill is the server's own death, not a hit: nothing lowers it, so the unit dies (the rune only guards against damage).
        match.Runtime.Combat.Kill(ally, match.Foe.NetId);
        TestAssert.False(ally.Alive, "the unit is dead");
        TestAssert.True(Of(ally, "ChronoShift") is not null, "ChronoShift says PersistsThroughDeath, so the rune is still on the corpse");
        TestAssert.True(Of(ally, "ChronoRevive") is null, "and nothing revived it");
        match.Advance(7.5);
        TestAssert.True(Of(ally, "ChronoShift") is null, "it runs out with its seven seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Two Zileans
    // ------------------------------------------------------------------------------------------------------------

    // Batch 227 (owner request, 25 October 2026): the fountain's swing (the shipped OrderTurretShrineBasicAttack TargetExecute, DAMAGESOURCE_INTERNALRAW
    // on a champion) is not stopped by Chronoshift. The rune's revive is ChronoShift's BuffOnPreDamage, which the internal-raw hit skips, so the
    // lethal hit kills the ally and no ChronoRevive starts (the shipped script's order: BuffOnPreDamage before death, not a death hook).
    [Check("scripting.bb-zilean-fountain-kills-through-chronoshift")]
    public static void TheFountainKillsThroughChronoshift()
    {
        using var match = Arena(rank: 1);
        match.Learn(1);
        var ally = match.Ally;
        Grow(match, ally);
        var fountain = match.ChaosFountain;
        // Zilean stands within her cast range of the ally; both are inside the fountain's reach.
        match.Hero.Position = fountain.Position + new Position2(300, 0);
        ally.Position = fountain.Position + new Position2(400, 0);
        ally.Health = 600.0;
        TestAssert.True(match.Cast(3, ally), "R casts on an ally");
        TestAssert.True(Of(ally, "ChronoShift") is not null, "the rune is on him");
        fountain.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(fountain, ally), "the fountain swings at him");
        match.Advance(2.0);
        TestAssert.False(ally.Alive, "the fountain's lethal hit kills him: the rune does not revive him");
        TestAssert.True(Of(ally, "ChronoRevive") is null, "and no ChronoRevive starts");
    }

    [Check("scripting.bb-zilean-q-sets-off-the-other-teams-bomb-on-the-target")]
    public static void TheOtherZileansBombIsFoundBySkinNameAndDetonated()
    {
        using var match = Arena(foe: "Zilean");
        match.Foe.SpellRanks[0] = 5;
        match.Foe.Mana = match.Foe.MaxMana;
        TestAssert.True(match.Cast(0, match.Ally, caster: match.Foe), "the Chaos Zilean bombs the Order ally");
        match.Advance(1.2);
        var bomb = Of(match.Ally, "TimeBomb");
        TestAssert.Equal(match.Foe.NetId, bomb?.CasterNetId ?? 0u, "its caster is the Chaos Zilean");
        TestAssert.True(match.Cast(0, match.Ally), "the Order Zilean casts at the same unit");
        match.Advance(0.4);
        // BBGetChampionBySkinName finds the Zilean of the other team; his bomb sits on the target, so it goes off with his name on it: the damage hits his enemies (the Order heroes).
        var hit = HitsOn(match, "Ally").Last();
        TestAssert.Near(Mitigated(BombDamage[4] + 0.9 * match.Foe.AbilityPower, match.Ally.SpellBlock), hit.Dealt, 0.05, "the blast of the other bomb, at the casting spell's rank");
        TestAssert.Equal(1, match.Ally.Buffs.Count(buff => buff.Name == "TimeBomb"), "one bomb is on the ally afterwards");
        TestAssert.Equal(match.Hero.NetId, Of(match.Ally, "TimeBomb")!.CasterNetId, "the caster's own");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 583 (the checklist's two-Zilean question): the replacing add takes the other caster's bomb, so it goes off once, at the second cast,
    /// and never again at its own fourth second (TimeBomb.OnBuffDeactivate blasts only for a dead carrier or an expiry). The wiki's
    /// V1.0.0.96 fix "Not detonating an enemy Zilean's bomb when landing on the same unit" is the retail detonation. The Order bomb on its own ally
    /// blasts the Chaos foe beside him 4 s after it lands.
    /// </summary>
    [Check("scripting.bb-zilean-a-replaced-enemy-bomb-goes-off-once-and-never-at-its-own-end")]
    public static void TheReplacedBombNeverBlastsAgain()
    {
        using var match = Arena(foe: "Zilean");
        match.Foe.SpellRanks[0] = 5;
        match.Foe.Mana = match.Foe.MaxMana;
        match.Ally.Position = new Position2(7150, 7000);
        TestAssert.True(match.Cast(0, match.Ally, caster: match.Foe), "the Chaos Zilean bombs the Order ally");
        match.Advance(1.2);
        TestAssert.Equal(match.Foe.NetId, Of(match.Ally, "TimeBomb")?.CasterNetId ?? 0u, "the Chaos bomb sits on the ally");
        TestAssert.True(match.Cast(0, match.Ally), "the Order Zilean casts at the same unit");
        match.Advance(6.0);
        // Countdown ticks are 3, 2 and 1 raw; the blasts are 320 + 0.9 AP raw.
        var blasts = HitsOn(match, "Ally").Where(hit => hit.Raw > 100.0).ToList();
        TestAssert.Equal(1, blasts.Count, "the Chaos bomb goes off once, when the second bomb lands: " + string.Join(",", blasts.Select(hit => R(hit.At))));
        TestAssert.True(blasts[0].At < 2.0, "at the second cast, not 4 s after the first");
        var own = HitsOn(match, "Foe").Where(hit => hit.Raw > 100.0).ToList();
        TestAssert.Equal(1, own.Count, "the Order bomb goes off once on its own clock and hits the Chaos foe 150 from the ally");
        TestAssert.True(own[0].At > 5.0, "four seconds after it landed");
        TestAssert.True(Of(match.Ally, "TimeBomb") is null, "no bomb is left on the ally");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
