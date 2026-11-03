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
/// Phase 3, wave 7 (25 October 2026, batch 328): Karthus plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). Expected numbers are the rank-one literals of Karthus.inibin and his four spell inibins (cost, cooldown, the effects) and the
/// shipped tooltips of fontconfig_en_US (delay, duration, channel); the scenario <c>S-karthus</c> shows the same facts on a headless match.
/// Written 25 October 2026. Ability power is zero in every arena, so each damage is its base amount after the foe's resistance.
/// </summary>
public static class BbKarthusChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Karthus at rank one, the enemy 300 away, every foe with 30 magic resistance and no armour.</summary>
    private static Match Arena(string foe = "Orianna")
    {
        var match = new Match("Karthus", foeChampion: foe);
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-karthus-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Karthus");
        foreach (var name in new[] { "CharScriptKarthus", "LayWaste", "WallOfPain", "Defile", "FallenOne", "DeathDefied", "DeathDefiedBuff", "WallofPainTarget" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Karthus");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(68, closure.ScriptNames.Count(), "the closure is 68 scripts (his own, the Death Defied and Requiem trees, the system buffs, the Yorick revival tree the Death Defied tests name)");

        var hero = new Entity { ChampionName = "Karthus" };
        TestAssert.Equal(["LayWaste", "WallOfPain", "Defile", "FallenOne"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Karthus.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Karthus")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, Id: 30, RetailBot: false }, "playable as a mid for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Karthus"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Lay Waste
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-karthus-lay-waste-lands-half-a-second-after-its-cast")]
    public static void LayWasteLandsAfterItsDelay()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts on the foe's spot");
        match.Advance(0.6);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(20.0, mana - match.Hero.Mana, 1.0, "20 mana at rank one");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands before the half second (the cast takes a quarter of one)");
        match.Advance(0.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one blast");
        TestAssert.Near(Mitigated(80.0, 30.0), hits[0].Dealt, 0.05, "alone in the area: double damage, 80 magic after 30 resistance");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-karthus-lay-waste-deals-single-damage-when-two-units-are-hit")]
    public static void LayWasteHitsEachForSingleDamage()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(7340, 7000); // inside the blast beside the foe
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts");
        match.Advance(1.2);
        TestAssert.Near(Mitigated(40.0, 30.0), HitsOn(match, "Foe")[0].Dealt, 0.05, "two units: 40 magic after 30 resistance on the foe");
        TestAssert.Near(Mitigated(40.0, 30.0), HitsOn(match, "FarFoe")[0].Dealt, 0.05, "and on the other");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "nor on a friend");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Wall of Pain
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-karthus-wall-of-pain-strips-armour-and-resistance-and-slows-a-unit-in-it")]
    public static void WallOfPainDebuffsTheUnitInIt()
    {
        using var match = Arena();
        var armour = match.Foe.Armor;
        var resistance = match.Foe.SpellBlock;
        var speed = match.Foe.Speed;
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "W casts across the foe");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "WallofPainExtra") is not null, "the foe carries the wall's armour and resistance debuff");
        TestAssert.Near(-15.0, match.Foe.Armor - armour, 1e-6, "armour down by 15 at rank one");
        TestAssert.Near(-15.0, match.Foe.SpellBlock - resistance, 1e-6, "magic resistance down by 15");
        TestAssert.True(Buff(match.Foe, "WallofPainTarget") is not null, "and the slow");
        TestAssert.True(match.Foe.Speed < speed, "the foe moves slower inside the wall");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Defile
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-karthus-defile-drains-mana-and-hurts-nearby-enemies-each-second")]
    public static void DefileDrainsManaAndHurtsEachSecond()
    {
        using var match = Arena();
        var hero = match.Hero;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(2), "E toggles on");
        match.Advance(0.3);
        // The press pays the rank's ManaCost (the generic cast cost: 30 at rank one). The script then drains the same amount each second from the
        // first full second on (BBExecutePeriodically: TimeBetweenExecutions 1, ExecuteImmediately false), so two drains land by 2.2 seconds.
        TestAssert.Near(30.0, mana - hero.Mana, 1.0, "the press pays 30 mana at rank one");
        match.Advance(1.9);
        TestAssert.True(Buff(hero, "Defile") is not null, "the toggle is on");
        TestAssert.Near(90.0, mana - hero.Mana, 3.5, "30 at the press and 30 a second after it, the regeneration aside");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(3, hits.Count, "a hit at the press and one each second after it (at 0, 1 and 2 seconds)");
        TestAssert.Near(Mitigated(30.0, 30.0), hits[0].Dealt, 0.05, "30 magic after 30 resistance (no ability power)");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "nobody beyond the 550 of the blast");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-karthus-defile-restores-mana-on-a-kill-while-toggled-off")]
    public static void DefileRestoresManaOnAKillWhenOff()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.Cast(2);
        match.Advance(0.6);
        match.Cast(2); // toggled off again: the passive form
        match.Advance(0.6);
        TestAssert.True(Buff(hero, "Defile") is null, "the toggle is off");
        hero.Mana = 100.0;
        match.Runtime.Combat.Kill(match.Foe, hero.NetId);
        match.Advance(0.2);
        TestAssert.Near(120.0, hero.Mana, 1.0, "a kill restores 20 mana at rank one");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Requiem
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-karthus-requiem-hits-every-enemy-champion-after-a-three-second-channel")]
    public static void RequiemHitsEveryEnemyChampion()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(12000, 12000); // out of any range: Requiem reaches every champion
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3), "R casts");
        match.Advance(3.0);
        // Batch 340: the channel's cast frame pays, not the press (2.75 s of regeneration since).
        TestAssert.Near(150.0, mana - match.Hero.Mana, 5.0, "150 mana at rank one");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count + HitsOn(match, "FarFoe").Count, "nothing lands during the channel");
        match.Advance(1.2);
        TestAssert.Equal(1, HitsOn(match, "FarFoe").Count, "the far enemy takes one blast");
        TestAssert.Near(Mitigated(250.0, 30.0), HitsOn(match, "FarFoe")[0].Dealt, 0.05, "250 magic after 30 resistance");
        TestAssert.Near(Mitigated(250.0, 30.0), HitsOn(match, "Foe")[0].Dealt, 0.05, "and the near one takes the same");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "a friend takes nothing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death Defied
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-karthus-death-defied-casts-for-free-for-seven-seconds-after-his-death")]
    public static void DeathDefiedCastsAfterDeath()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        TestAssert.True(Buff(hero, "DeathDefiedBuff") is not null, "the spirit form is on");
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Lay Waste casts while dead");
        match.Advance(1.2);
        TestAssert.Near(mana, hero.Mana, 1.0, "the spirit form casts for free");
        TestAssert.Near(Mitigated(80.0, 30.0), HitsOn(match, "Foe")[0].Dealt, 0.05, "and the blast lands");
        match.Advance(6.0);
        TestAssert.True(Buff(hero, "DeathDefiedBuff") is null, "the spirit form ends after seven seconds");
        TestAssert.True(!hero.Alive, "and Karthus is dead");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 350: Requiem pressed in the spirit form finishes its channel. Wiki, Karthus patch history, V1.0.0.126: "Bug Fix: Requiem channel
    /// sometimes not finishing if cast while active" (Death Defied); the spirit form lasts seven seconds, the channel three.
    /// </summary>
    [Check("scripting.bb-karthus-requiem-cast-in-death-defied-finishes-its-channel")]
    public static void RequiemCastInDeathDefiedFinishes()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        TestAssert.True(Buff(hero, "DeathDefiedBuff") is not null, "the spirit form is on");
        TestAssert.True(match.Cast(3), "R casts in the spirit form");
        match.Advance(4.2);
        TestAssert.Equal(1, HitsOn(match, "FarFoe").Count, "the far enemy takes the blast at the channel's end");
        TestAssert.Near(Mitigated(250.0, 30.0), HitsOn(match, "Foe")[0].Dealt, 0.05, "250 magic after 30 resistance");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Wall of Pain's posts stand on their placemarkers, not on Karthus. <c>SelfExecute</c> adds <c>WallOfPainBeam</c> to the last placemarker with
    /// <c>AttackerVar='Other1'</c> (the first placemarker), so inside the beam <c>Attacker</c> is that placemarker: the post bound to it stands there, and
    /// the beam joins the two posts. Finding 36: the engine read the placemarker applier as its owner (Karthus), so the second post stood on him.
    /// </summary>
    [Check("scripting.bb-karthus-wall-of-pain-posts-stand-on-their-placemarkers")]
    public static void PostsStandOnTheirPlacemarkers()
    {
        using var match = new Match("Karthus");
        match.Learn(1);
        match.Hero.Position = new Position2(7000.0, 7000.0);
        match.Foe.Position = new Position2(9000.0, 9000.0);
        match.Advance(0.5);
        match.Log.EffectCalls.Clear();
        TestAssert.True(match.Cast(1, at: new Position2(7500.0, 7000.0)), "Wall of Pain is cast at the middle of the wall");
        match.Advance(2.0);
        var posts = match.Log.EffectCalls.Where(call => call.Name == "wallofpain_new_post_green.troy").ToArray();
        TestAssert.Equal(2, posts.Length, "two posts are created");
        var beam = match.Log.EffectCalls.Single(call => call.Name == "wallofpain__new_beam.troy");
        TestAssert.True(posts.All(post => post.Bind != match.Hero.NetId), "no post is bound to Karthus");
        TestAssert.True(beam.Bind != match.Hero.NetId && beam.Target != match.Hero.NetId, "the beam joins two placemarkers, not Karthus");
        // Batches 397-398 (Reconstruction): an effect bound to a placemarker is sent unbound at the placemarker's point, and the beam keeps its far
        // placemarker's point as target (batch 431); so each post stands at the point of the beam's end it belongs to.
        TestAssert.Equal(0u, posts[0].Bind | posts[1].Bind | beam.Bind, "the posts and the beam are sent unbound (batch 397)");
        TestAssert.Equal(0u, beam.Target, "the beam ends on its far placemarker's point, not its net id (batch 431, Reconstruction; batch 398 kept the net id)");
        TestAssert.Equal(beam.Source, posts[0].Source, "the first post stands on the beam's near placemarker");
        TestAssert.Equal(beam.TargetPoint, posts[1].Source, "the second post stands on the beam's far placemarker");
    }
}
