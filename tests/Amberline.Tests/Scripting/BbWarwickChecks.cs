using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (25 October 2026, batch 239): Warwick plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever
/// existed). Expected numbers are the literals of the shipped scripts and spell inibins; the scenario <c>S-warwick</c> shows the same facts on a headless
/// match. Infinite Duress is the global <c>Suppression</c> (E03) and a channel (E08): the channel ticks on the script's own half second and ends with the
/// suppression. The drain of Hungering Strike heals Warwick for 80 percent of the damage that leaves him after mitigation (<c>GlobalDrain</c>).
/// </summary>
public static class BbWarwickChecks
{
    // Rank-five and rank-one figures of the shipped scripts and spell inibins (HungeringStrike, HuntersCall, BloodScent, InfiniteDuress).
    private const double HungerMinimumRankFive = 275.0;
    private const double HungerMaxHealthRankFive = 0.2;
    private const double HungerDrainShare = 0.8;
    private const double DuressTickCount = 5.0;

    private static Match Arena(int rank = 5, string foe = "Ezreal")
    {
        var match = new Match("Warwick", foeChampion: foe);
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    private static double Mitigated(double raw, double resistance) => raw * 100.0 / (100.0 + resistance);

    // The shipped scripts name the aura both "Bloodscent_internal" (CharOnLevelUpSpell) and "BloodScent_internal" (the toggle): the lookup ignores case.
    private static bool Has(Entity unit, string name) => unit.Buffs.Any(buff => string.Equals(buff.Name, name, StringComparison.OrdinalIgnoreCase));

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-warwick-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Warwick");
        foreach (var name in new[] { "CharScriptWarwick", "HungeringStrike", "HuntersCall", "BloodScent", "InfiniteDuress", "InfiniteDuressChannel",
                     "Suppression", "GlobalDrain", "BloodScent_internal", "BloodScent_target", "EternalThirstIcon", "InfiniteDuressSound" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Warwick");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(21, closure.ScriptNames.Count(), "the closure is 21 scripts: his own, the spells, the global Suppression and GlobalDrain, and the bot fortify and disconnect helpers");
        // The passive's inibin name has a space ("Eternal Thirst"); the character script adds the buff EternalThirstIcon itself (the same as Zilean's passive).
        TestAssert.Equal(["Eternal Thirst"], closure.Dangling.Select(dangling => dangling.Name).ToArray(), "one dangling name: the passive's spaced inibin name");

        var hero = new Entity { ChampionName = "Warwick" };
        TestAssert.Equal(["HungeringStrike", "HuntersCall", "BloodScent", "InfiniteDuress"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Warwick.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Warwick")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 1350, Id: 19, RetailBot: true }, "playable as a jungler for 1350 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Warwick"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hungering Strike: the magic hit and the drain
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-warwick-hungering-strike-hits-the-greater-of-the-minimum-and-a-health-share")]
    public static void HungerHitIsTheGreaterOfTheMinimumAndAHealthShare()
    {
        using var match = Arena(rank: 5);
        var foe = match.Foe;
        foe.Health = foe.MaxHealth;
        var raw = Math.Max(HungerMinimumRankFive, HungerMaxHealthRankFive * foe.MaxHealth);
        var before = foe.Health;
        TestAssert.True(match.Cast(0, foe), "Hungering Strike casts on the enemy");
        match.Advance(1.0);
        var lost = before - foe.Health;
        TestAssert.Near(Mitigated(raw, foe.SpellBlock), lost, 2.0, "the hit is the greater of 275 and 20 percent of the target's maximum health, after magic resistance");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-warwick-hungering-strike-drains-eighty-percent-of-the-hit")]
    public static void HungerHealsEightyPercentOfTheMitigatedHit()
    {
        using var match = Arena(rank: 5);
        var foe = match.Foe;
        foe.Health = foe.MaxHealth;
        var raw = Math.Max(HungerMinimumRankFive, HungerMaxHealthRankFive * foe.MaxHealth);
        var mitigated = Mitigated(raw, foe.SpellBlock);
        // Level eighteen: a level-one health bar would cap the heal before the drain can show (Zilean's Grow does the same).
        match.Hero.Level = 18;
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Hero.Health = match.Hero.MaxHealth * 0.5;
        var before = match.Hero.Health;
        TestAssert.True(match.Cast(0, foe), "Hungering Strike casts on the enemy");
        match.Advance(0.5);
        // GlobalDrain (Attacker, 0.01 s) takes DrainPercent of the damage that leaves Warwick after mitigation, and heals him by it (regeneration is small).
        // The tolerance is the regeneration of the half second after the cast (a few points at level eighteen).
        TestAssert.Near(HungerDrainShare * mitigated, match.Hero.Health - before, 6.0, "the drain heals 80 percent of the mitigated hit");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hunter's Call: self and allies within 1200
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-warwick-hunters-call-buffs-himself-and-the-ally-beside-him")]
    public static void HuntersCallBuffsSelfAndNearbyAllies()
    {
        using var match = Arena(rank: 5);
        match.Ally.Position = new Position2(7600, 7000);
        match.Refill();
        TestAssert.True(match.Cast(1, match.Hero), "Hunter's Call casts on Warwick");
        match.Advance(0.5);
        var self = Buff(match.Hero, "HuntersCall");
        var ally = Buff(match.Ally, "HuntersCall");
        TestAssert.True(self is not null && ally is not null, "Warwick and the ally 600 away carry Hunter's Call");
        TestAssert.True(Buff(match.Foe, "HuntersCall") is null && Buff(match.FarFoe, "HuntersCall") is null, "no enemy carries it");
        TestAssert.True(self!.ExpiresAt - match.Now > 9.0 && self.ExpiresAt - match.Now <= 10.0, "ten seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Blood Scent: the toggle, the mark, the haste, the cooldown after switching off
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-warwick-blood-scent-marks-a-low-health-enemy-and-hastens-him")]
    public static void BloodScentMarksLowHealthEnemies()
    {
        using var match = Arena(rank: 5);
        var foe = match.Foe;
        foe.Health = foe.MaxHealth * 0.4;
        if (!Has(match.Hero, "BloodScent_internal")) TestAssert.True(match.Cast(2, match.Hero), "Blood Scent toggles on");
        match.Advance(2.5);
        TestAssert.True(Buff(foe, "BloodScent_target") is not null, "an enemy at or below half health carries the mark");
        TestAssert.True(Buff(match.Hero, "BloodScent") is not null, "Warwick carries the haste while the mark is on a target in range");
    }

    [Check("scripting.bb-warwick-blood-scent-cooldown-is-four-seconds-after-switching-off")]
    public static void BloodScentCooldownAfterSwitchingOff()
    {
        using var match = Arena(rank: 5);
        var hero = match.Hero;
        match.Refill();
        // Learning E starts the aura (CharOnLevelUpSpell adds Bloodscent_internal at the first rank); a cast from the aura's on state toggles it off.
        if (!Has(hero, "BloodScent_internal")) TestAssert.True(match.Cast(2, hero), "Blood Scent toggles on");
        match.Advance(1.0);
        TestAssert.True(Has(hero, "BloodScent_internal"),
            $"the aura is on before the switch (buffs: {string.Join(",", hero.Buffs.Select(buff => buff.Name))}; cooldown {hero.SpellCooldowns[2]:0.00})");
        TestAssert.True(match.Cast(2, hero), "Blood Scent toggles off");
        match.Advance(0.1);
        TestAssert.True(!Has(hero, "BloodScent_internal"), "the aura is off");
        TestAssert.True(hero.SpellCooldowns[2] > 3.5 && hero.SpellCooldowns[2] <= 4.0, $"the switch sets Blood Scent's cooldown to 4 s (got {hero.SpellCooldowns[2]:0.00})");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Infinite Duress: the suppression, the teleport beside the target, the channel
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-warwick-infinite-duress-teleports-beside-the-target-and-suppresses-it")]
    public static void InfiniteDuressTeleportsAndSuppresses()
    {
        using var match = Arena(rank: 3);
        var foe = match.Foe;
        var start = match.Hero.Position;
        // Batch 269 goal 4: the landing is a random point of the 150 ring around the target (BBGetRandomPointInAreaUnit, Radius = InnerRadius
        // = 150) drawn from the match's unseeded random, and the harness's ally stands on that ring (7150, 7000). About one cast in five drew a
        // point inside the ally's body; the teleport's correction backed Warwick off along his ray to about 240 from the target (8 of 40 runs
        // in one worker, 1 of 12 in fresh processes: not state left by another check). The ally stands off the ring, so every angle lands at 150.
        match.Ally.Position = new Position2(6600, 7000);
        TestAssert.True(match.Cast(3, foe), "Infinite Duress casts on the enemy");
        match.Advance(0.3);
        var distance = Dist(match.Hero.Position, foe.Position);
        TestAssert.Near(150.0, distance, 12.0, "Warwick lands 150 units from the target (InnerRadius 150)");
        TestAssert.True(Dist(start, match.Hero.Position) > 100.0, "the teleport moved him");
        TestAssert.True(Buff(foe, "Suppression") is not null, "the target is suppressed (the global Suppression, E03)");
        TestAssert.True(Buff(match.Hero, "InfiniteDuress") is not null, "Warwick carries his lifesteal buff from the immobilised branch");
    }

    [Check("scripting.bb-warwick-infinite-duress-channel-ticks-five-times-and-ends-with-the-suppression")]
    public static void InfiniteDuressChannelTicksAndEnds()
    {
        using var match = Arena(rank: 3);
        var foe = match.Foe;
        foe.Health = foe.MaxHealth;
        TestAssert.True(match.Cast(3, foe), "Infinite Duress casts on the enemy");
        var ticks = 0;
        var last = foe.Health;
        for (var t = 0.0; t < 3.0; t += 0.25)
        {
            match.Advance(0.25);
            if (foe.Health < last - 0.01) ticks++;
            last = foe.Health;
        }
        TestAssert.Equal((int)DuressTickCount, ticks, "five hits across the channel");
        TestAssert.True(Buff(foe, "Suppression") is null && Buff(foe, "InfiniteDuressChannel") is null,
            "the suppression and the channel are gone when it ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Eternal Thirst: the passive keeps through death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-warwick-eternal-thirst-is-kept-through-death")]
    public static void EternalThirstIconPersistsThroughDeath()
    {
        using var match = Arena(rank: 5);
        var hero = match.Hero;
        TestAssert.True(Buff(hero, "EternalThirstIcon") is not null, "the passive icon is on him from the start");
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(hero);
        match.Advance(0.5);
        TestAssert.True(!hero.Alive, "dead");
        TestAssert.True(Buff(hero, "EternalThirstIcon") is not null, "the passive icon is kept while dead (PersistsThroughDeath)");
        match.Advance(90.0);
        TestAssert.True(hero.Alive, "respawned");
        TestAssert.True(Buff(hero, "EternalThirstIcon") is not null, "the passive icon is still on him after the respawn");
    }
}
