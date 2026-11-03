using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (25 October 2026, batch 216): Miss Fortune plays from her shipped scripts. Expected numbers are the literals of the shipped scripts and
/// spell inibins (decoded by the standalone reader of the audit fixture, not by Amberline's readers); the scenario <c>S-MissFortune</c> shows the same facts
/// on a headless match. Written 25 October 2026.
/// </summary>
public static class BbMissFortuneChecks
{
    [Check("scripting.bb-missfortune-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("MissFortune");
        foreach (var name in new[]
                 {
                     "CharScriptMissFortune", "MissFortuneRicochetShot", "MissFortuneViciousStrikes", "MissFortuneScattershot",
                     "MissFortuneBulletTime", "MissFortuneRShotExtra", "MissFortuneBullets", "MissFortuneBulletsClone", "MissFortuneStrut",
                     "MissFortunePassive", "MissFortuneScatterAoE", "MissFortuneScatterParticle", "MissFortuneStrutDebuff", "MissFortunePassiveStack",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("MissFortune");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(35, closure.ScriptNames.Count(), "the closure is 35 scripts (her ten own spells and helpers, the system buffs and the Fortify and Root tree her passive reaches)");

        var hero = new Entity { ChampionName = "MissFortune" };
        TestAssert.Equal(["MissFortuneRicochetShot", "MissFortuneViciousStrikes", "MissFortuneScattershot", "MissFortuneBulletTime"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from MissFortune.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("MissFortune")!;
        TestAssert.True(info is { Playable: true, Role: "adc", DefaultIp: 6300, RetailBot: true }, "playable as an ADC for 6300 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "MissFortune"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Spells and the passive, from the shipped scripts
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Impure Shots (W, the self buff of <c>MissFortuneViciousStrikes</c>): six seconds on her, the cooldown of 16 from the inibin.</summary>
    [Check("scripting.bb-missfortune-impure-shots-buffs-the-caster-for-six-seconds")]
    public static void ImpureShotsBuffsTheCasterForSixSeconds()
    {
        var match = new BbM3.Match("MissFortune");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(1, match.Hero), "W casts on herself");
        match.Advance(0.5);
        var buff = BbM3.Buff(match.Hero, "MissFortuneViciousStrikes");
        TestAssert.True(buff is not null, "the self buff is on her");
        TestAssert.Equal(1, buff!.Stacks, "one stack of the self buff");
        TestAssert.True(Math.Abs(match.Hero.SpellCooldowns[1] - 15.5) < 0.05, "the cooldown is 16 seconds from the cast (inibin, rank one)");
    }

    /// <summary>Make It Rain (E): the placemarker is announced as a no-render unit that draws its particles (batch 202): NoRender 0x2000, ForceRenderParticles 0x4000, Ghosted 0x400.</summary>
    [Check("scripting.bb-missfortune-make-it-rain-placemarker-draws-only-its-particles")]
    public static void MakeItRainPlacemarkerDrawsOnlyItsParticles()
    {
        var match = new BbM3.Match("MissFortune");
        match.Learn(2);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, match.Foe), "E casts at the enemy's ground");
        match.Advance(1.0);
        var placemarker = match.Runtime.World.Entities.Values.FirstOrDefault(unit => BbM3.Buff(unit, "MissFortuneScatterAoE") is not null);
        TestAssert.True(placemarker is not null, "the placemarker carries the rain");
        var word = CharacterState.ActionWord(placemarker!);
        TestAssert.True((word & 0x2000u) != 0, "NoRender is set (the unit draws no model)");
        TestAssert.True((word & 0x4000u) != 0, "ForceRenderParticles is set (its particles still draw)");
        TestAssert.True((word & 0x400u) != 0, "Ghosted is set");
    }

    /// <summary>
    /// Bullet Time's eight waves (<c>ChannelingUpdateActions</c> counts them; <c>Count &gt;= 8</c> ends the channel) run on the 0.25 s grid from the
    /// channel's start (batch 216 follow-up, a Reconstruction; the grid of <c>BbBuffRuntime.ActionsUpdateInterval</c>). The spell step is 1/30 s, so a
    /// quarter second is 7.5 steps: the seven gaps between the eight waves are 7 or 8 steps, and the eighth wave comes two seconds after the first grid point.
    /// </summary>
    [Check("scripting.bb-missfortune-bullet-time-waves-follow-the-quarter-second-grid")]
    public static void BulletTimeWavesFollowTheQuarterSecondGrid()
    {
        var match = new BbM3.Match("MissFortune");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var cast = match.Cast(3, at: new Position2(match.Hero.Position.X + 60.0, match.Hero.Position.Z));
        TestAssert.True(cast, "Bullet Time casts at the cone point");

        // The step count of each wave, counted from the cast order (one step per call of the runtime, the harness step of 1/30 s).
        var waves = new List<int>();
        var seen = 0;
        for (var step = 1; step <= 180 && waves.Count < 8; step++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            var stacks = Waves(match.Hero);
            while (seen < stacks)
            {
                seen++;
                waves.Add(step);
            }
        }
        TestAssert.Equal(8, waves.Count, "eight waves");
        var gaps = waves.Zip(waves.Skip(1), (earlier, later) => later - earlier).ToArray();
        TestAssert.True(gaps.All(gap => gap is 7 or 8),
            $"each gap is 7 or 8 spell steps (a quarter second on the grid): {string.Join(",", gaps)}");
        TestAssert.True(Math.Abs(gaps.Sum() / 30.0 - 1.75) <= 1.0 / 30.0 + 1e-9, "seven quarter-seconds from the first wave to the eighth");

        match.Advance(0.2);
        TestAssert.Equal(0, Waves(match.Hero), "the eighth wave ends the channel and clears the waves");
        TestAssert.True(BbM3.Buff(match.Hero, "MissFortuneBulletSound") is null, "the channel's sound goes with it");
    }

    private static int Waves(Entity unit) => unit.Buffs.FirstOrDefault(buff => buff.Name == "MissFortuneWaves")?.Stacks ?? 0;
}
