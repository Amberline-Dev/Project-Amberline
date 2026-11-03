using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 5 (batch 295, 26 October 2026): Pantheon plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the
/// literals of the shipped scripts and spell inibins (rank one unless stated): Spear Shot throws for 65 (45 mana, four seconds) plus 1.4 of his bonus
/// attack damage, and 1.5 times that against a target at 15 percent of its health or less; Aegis of Zeonia leaps at 50 magic damage and stuns for one
/// second (70 mana, 13 seconds) and shields him; Heartseeker Strike channels 0.75 seconds and strikes three times for 13 physical plus 0.6 of his bonus
/// attack damage, doubled against heroes (45 mana, ten seconds); Grand Skyfall channels 1.5 seconds, floats 1.5 seconds and lands on the point, 400
/// magic damage within 250 and half of it out to 700, with 35 percent slow (150 mana, 150 seconds). Aegis Protection counts his spells and attacks to
/// four, then shields him from the next hit of 40 or more; Certain Death makes his attacks critical against targets at 15 percent of their health or less.
/// The scenario <c>S-Pantheon</c> shows the same facts on a headless match.
/// </summary>
public static class BbPantheonChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>Pantheon at the given level with every spell learnt at rank one, the enemy 150 in front of him, the second enemy far off.</summary>
    private static Match Arena(int level = 1, string foe = "Ezreal")
    {
        var match = new Match("Pantheon", foeChampion: foe);
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Ally.Position = new Position2(6500, 6500);
        foreach (var unit in new[] { match.Foe, match.FarFoe, match.Ally }) unit.SpellBlock = 0.0;
        match.Hero.Level = level;
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Refill();
        return match;
    }

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>Advances in twentieths of a second until the condition holds (at most four seconds).</summary>
    private static bool AdvanceUntil(Match match, Func<bool> condition)
    {
        for (var elapsed = 0.0; elapsed < 4.0 && !condition(); elapsed += 0.05) match.Advance(0.05);
        return condition();
    }

    /// <summary>Spell damage dealt to a named unit (every hit except the basic attacks, which the log records twice).</summary>
    private static List<(double At, double Dealt, double Raw, string Kind)> SpellHits(Match match, string victim) =>
        HitsOn(match, victim).Where(hit => hit.Kind is not ("Basic" or "none")).ToList();

    /// <summary>
    /// Basic attack hits on a named unit. Batch 301: the second entry batch 295 saw for each swing was a real second hit (PantheonBasicAttack's
    /// DAMAGESOURCE_DEFAULT dealt again, uncategorised); it is the swing's own hit now, so each swing is one entry.
    /// </summary>
    private static List<(double At, double Dealt, double Raw, string Kind)> SwingHits(Match match, string victim) =>
        HitsOn(match, victim).Where(hit => hit.Kind == "Basic").ToList();

    /// <summary>Sets a unit's health to a fraction of its maximum (the Certain Death cutoff is fifteen percent).</summary>
    private static void SetHealthFraction(Entity unit, double fraction) => unit.Health = fraction * unit.MaxHealth;

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-pantheon-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Pantheon");
        foreach (var name in new[] { "CharScriptPantheon", "Pantheon_Throw", "Pantheon_LeapBash", "Pantheon_Heartseeker",
                     "Pantheon_HeartseekerChannel", "Pantheon_GrandSkyfall_Jump", "Pantheon_GrandSkyfall_Fall", "Pantheon_GrandSkyfall_FallD",
                     "Pantheon_GrandSkyfall", "Pantheon_Aegis", "Pantheon_AegisShield", "Pantheon_AegisShield2", "Pantheon_Aegis_Counter",
                     "Pantheon_CertainDeath" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Pantheon");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(26, closure.ScriptNames.Count(), "the closure is 26 scripts (audit bb-coverage)");

        var hero = new Entity { ChampionName = "Pantheon" };
        TestAssert.Equal(["Pantheon_Throw", "Pantheon_LeapBash", "Pantheon_Heartseeker", "Pantheon_GrandSkyfall_Jump"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Pantheon.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Pantheon")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, Id: 80, RetailBot: false },
            "playable as a mid laner for 3150 IP, not a retail bot (the generic template)");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Pantheon"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Aegis Protection (counters and the shield) and Certain Death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-pantheon-aegis-counts-spells-to-a-shield-of-his-own")]
    public static void AegisCountsSpellsToAShield()
    {
        using var match = Arena();
        TestAssert.True(Buff(match.Hero, "Pantheon_Aegis") is not null, "he carries the passive (his character script adds it on activation)");
        var counters = new List<int>();
        for (var cast = 1; cast <= 4; cast++)
        {
            match.Refill();
            TestAssert.True(match.Cast(0, match.Foe), $"spear {cast} is accepted");
            match.Advance(1.0);
            counters.Add(match.Hero.Buffs.Count(buff => buff.Name == "Pantheon_Aegis_Counter"));
        }
        // Each cast adds a counter; the fourth gives the shield and clears the counters (BBGetBuffCountFromAll, BBSpellBuffClear).
        TestAssert.Equal("1,2,3,0", string.Join(",", counters), "one counter a spell, cleared at four");
        TestAssert.True(Buff(match.Hero, "Pantheon_AegisShield") is not null, "the fourth spell shields him");
        match.Refill();
        TestAssert.True(match.Cast(0, match.Foe), "a fifth spear");
        match.Advance(1.0);
        TestAssert.Equal(0, match.Hero.Buffs.Count(buff => buff.Name == "Pantheon_Aegis_Counter"), "no counter is added while the shield is up");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-pantheon-aegis-shield-blocks-the-next-hit-of-forty-or-more")]
    public static void AegisShieldBlocksAHit()
    {
        using var match = Arena();
        for (var cast = 0; cast < 4; cast++)
        {
            match.Refill();
            match.Cast(0, match.Foe);
            match.Advance(1.0);
        }
        TestAssert.True(Buff(match.Hero, "Pantheon_AegisShield") is not null, "the shield is up");
        var health = match.Hero.Health;
        // His armour (21) leaves a basic attack of about 44 (above the level-one threshold of 40): the shield takes it whole.
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero), "the foe swings at him");
        match.Advance(1.5);
        TestAssert.True(match.Hero.Health >= health - 0.5, $"the swing is blocked (health {health:0.0} became {match.Hero.Health:0.0})");
        TestAssert.True(Buff(match.Hero, "Pantheon_AegisShield") is null, "the shield is spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// The shield's BuffOnBeingHit zeroes any hit of the threshold or more whatever its source, so a turret shot is blocked like a champion's swing (batch 295
    /// left turret shots unprobed; batch 309). The turret's next shot lands.
    /// </summary>
    [Check("scripting.bb-pantheon-aegis-shield-blocks-a-turret-shot")]
    public static void AegisShieldBlocksATurretShot()
    {
        using var match = Arena();
        var turret = match.ChaosLaneTurret;
        match.Log.Name(turret, "Turret");
        match.Hero.Position = new Position2(turret.Position.X + 300.0, turret.Position.Z);
        TestAssert.True(ScriptBuff(match.Runtime, match.Hero, "Pantheon_AegisShield", 25000.0) is not null, "the shield is up");
        turret.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(turret, match.Hero), "the turret fires at him");
        match.Advance(2.0);
        var shots = HitsOn(match, "Pantheon").Where(hit => hit.Kind == "Basic").ToArray();
        TestAssert.True(shots.Length >= 1 && shots[0].Dealt == 0.0, "the first shot is blocked whole");
        TestAssert.True(Buff(match.Hero, "Pantheon_AegisShield") is null, "the shield is spent");
        TestAssert.True(shots.Length < 2 || shots[1].Dealt > 40.0, "the next shot lands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 349 (batch 295's open 3): a hit below the shield's <c>damageThreshold</c> (40 at level one, the script's <c>OnBuffActivate</c>; 40 + 3 per
    /// level from <c>BuffOnLevelUp</c>) lands whole and leaves the shield up (<c>BuffOnBeingHit</c> tests threshold &lt;= DamageAmount).
    /// A melee lane minion's swing is the small hit.
    /// </summary>
    [Check("scripting.bb-pantheon-aegis-shield-lets-a-hit-below-forty-through-and-stays")]
    public static void AegisShieldLetsASmallHitThrough()
    {
        using var match = Arena();
        TestAssert.True(ScriptBuff(match.Runtime, match.Hero, "Pantheon_AegisShield", 25000.0) is not null, "the shield is up");
        var minion = LaneMinion(match, new Position2(match.Hero.Position.X - 100.0, match.Hero.Position.Z));
        minion.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(minion, match.Hero), "the minion swings at him");
        match.Advance(1.0);
        var hits = HitsOn(match, "Pantheon").Where(hit => hit.Kind == "Basic").ToArray();
        TestAssert.True(hits.Length >= 1 && hits[0].Dealt > 0.0 && hits[0].Dealt < 40.0, $"the small hit lands ({(hits.Length > 0 ? hits[0].Dealt : -1):0.00})");
        TestAssert.True(Buff(match.Hero, "Pantheon_AegisShield") is not null, "and the shield stays for a hit of 40 or more");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-pantheon-certain-death-swing-is-not-critical-above-fifteen-percent")]
    public static void CertainDeathLeavesHealthyTargetsAlone()
    {
        // The cutoff's healthy side. The critical side (a swing at fifteen percent or less) was an engine gap in batch 295 and is closed by
        // batch 301: BbScriptedCritChecks (scripting.bb-scripted-crit-certain-death-swing-is-critical-at-fifteen-percent).
        using var high = Arena();
        SetHealthFraction(high.Foe, 0.16);
        high.Runtime.Combat.QueueBasicAttack(high.Hero, high.Foe);
        high.Advance(1.0);
        var normal = SwingHits(high, "Foe");
        TestAssert.True(normal.Count >= 1, "a swing lands above the cutoff");
        TestAssert.Near(53.6, normal[0].Raw, 0.05, "above fifteen percent the swing is the base attack damage");
        TestAssert.Equal(0, high.Module.ScriptFailures.Count, "no script failed: " + Failures(high));
    }

    [Check("scripting.bb-pantheon-passive-persists-through-death")]
    public static void PassiveSurvivesDeath()
    {
        using var match = Arena();
        match.Advance(0.5); // the character script adds Certain Death on its first update
        TestAssert.True(Buff(match.Hero, "Pantheon_Aegis") is not null && Buff(match.Hero, "Pantheon_CertainDeath") is not null,
            "the passive and Certain Death are on him (E is learnt)");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.True(Buff(match.Hero, "Pantheon_Aegis") is not null, "Aegis Protection persists through death (PersistsThroughDeath)");
        TestAssert.True(Buff(match.Hero, "Pantheon_CertainDeath") is not null, "Certain Death persists through death (PersistsThroughDeath)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Spear Shot (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-pantheon-spear-shot-hits-for-sixty-five-and-costs-forty-five")]
    public static void SpearShotHitsForSixtyFive()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7300, 7000);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Foe), "the spear is accepted");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(45.0, mana - match.Hero.Mana, 0.5, "45 mana at rank one");
        TestAssert.Near(4.0, match.Hero.SpellCooldowns[0], 0.05, "a four second cooldown");
        match.Advance(1.5 - match.CastFrameOf(0));
        var hits = SpellHits(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one spear hit");
        TestAssert.Near(65.0, hits[0].Raw, 0.01, "65 at rank one (plus 1.4 of bonus attack damage, none here)");
        TestAssert.Near(Mitigated(65.0, match.Foe.Armor), hits[0].Dealt, 0.05, "his target's armour mitigates it");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-pantheon-spear-shot-is-one-and-a-half-times-against-a-target-at-fifteen-percent")]
    public static void SpearShotExecutesAtFifteenPercent()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7300, 7000);
        SetHealthFraction(match.Foe, 0.14);
        TestAssert.True(match.Cast(0, match.Foe), "the spear is accepted");
        match.Advance(1.5);
        var hits = SpellHits(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one spear hit");
        TestAssert.Near(65.0 * 1.5, hits[0].Raw, 0.01, "Certain Death multiplies the spear by 1.5 below fifteen percent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Aegis of Zeonia (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-pantheon-aegis-of-zeonia-leaps-stuns-and-shields")]
    public static void AegisOfZeoniaLeapsAndStuns()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(7300, 7000);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, match.Foe), "the leap is accepted");
        TestAssert.Near(70.0, mana - match.Hero.Mana, 0.01, "70 mana at rank one");
        TestAssert.Near(13.0, match.Hero.SpellCooldowns[1], 0.05, "a thirteen second cooldown");
        TestAssert.True(Buff(match.Hero, "Pantheon_AegisShield") is not null, "the leap raises the shield at once");
        TestAssert.True(AdvanceUntil(match, () => Buff(match.Foe, "Stun") is not null), "the leap lands and stuns");
        var hits = SpellHits(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one leap hit");
        TestAssert.Near(50.0, hits[0].Raw, 0.01, "50 magic damage at rank one");
        TestAssert.True(Dist(match.Hero.Position, match.Foe.Position) < 150.0, "he lands beside the foe");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Foe, "Stun") is null, "the stun lasts one second");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Heartseeker Strike (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-pantheon-heartseeker-strikes-three-times-in-its-channel")]
    public static void HeartseekerStrikesThreeTimes()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, at: new Position2(7200, 7000)), "the strike is accepted");
        // The cast takes 0.39 s before the channel starts, and the mana is paid when it starts (the cooldown runs from the cast).
        TestAssert.True(AdvanceUntil(match, () => Buff(match.Hero, "Pantheon_HeartseekerChannel") is not null), "the channel starts");
        TestAssert.Near(45.0, mana - match.Hero.Mana, 1.5, "45 mana at rank one (less a little regeneration)");
        TestAssert.Near(10.0, match.Hero.SpellCooldowns[2] + 0.4, 0.5, "a ten second cooldown from the cast");
        match.Advance(1.5);
        var hits = SpellHits(match, "Foe");
        TestAssert.Equal(3, hits.Count, "three strikes in the channel");
        foreach (var hit in hits)
            TestAssert.Near(13.0 * 2.0, hit.Raw, 0.01, "13 at rank one, doubled against a hero (no bonus attack damage)");
        TestAssert.Near(Mitigated(26.0, match.Foe.Armor) * 3, hits.Sum(hit => hit.Dealt), 0.1, "his target's armour mitigates each strike");
        TestAssert.True(Buff(match.Hero, "Pantheon_HeartseekerChannel") is null, "the channel is over after 0.75 seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Grand Skyfall (R): the channel hands its point to the flight, the flight carries him there, the landing hurts
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-pantheon-grand-skyfall-carries-him-to-the-point-he-cast-at")]
    public static void GrandSkyfallCarriesHimToTheCastPoint()
    {
        using var match = Arena(level: 6);
        var landing = new Position2(7500, 7000);
        TestAssert.True(match.Cast(3, at: landing), "the ultimate is accepted at level six");
        // The cast takes 0.5 s and the channel 1.5 s; the flight starts when the channel succeeds and carries him to the landing point at its first update.
        TestAssert.True(AdvanceUntil(match, () => Buff(match.Hero, "Pantheon_GrandSkyfall_Fall") is not null), "the channel hands him to the flight");
        match.Advance(0.2);
        TestAssert.True(Dist(match.Hero.Position, landing) < 5.0, $"the flight carries him to the cast point (he is {Dist(match.Hero.Position, landing):0.0} away)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-pantheon-grand-skyfall-lands-for-400-within-250-and-half-beyond")]
    public static void GrandSkyfallLandingDamage()
    {
        using var match = Arena(level: 6);
        match.Foe.Position = new Position2(7700, 7000);
        match.FarFoe.Position = new Position2(7900, 7000);
        var before = match.Foe.Speed;
        TestAssert.True(match.Cast(3, at: new Position2(7500, 7000)), "the ultimate is accepted at level six");
        TestAssert.True(AdvanceUntil(match, () => SpellHits(match, "FarFoe").Count > 0), "the landing hits the far foe");
        var near = SpellHits(match, "Foe");
        var far = SpellHits(match, "FarFoe");
        TestAssert.Equal(1, near.Count, "one landing hit on the foe 200 away");
        TestAssert.Near(400.0, near[0].Raw, 0.01, "400 at rank one within 250 of the landing");
        TestAssert.Equal(1, far.Count, "one landing hit on the foe 400 away");
        TestAssert.Near(200.0, far[0].Raw, 0.01, "half of it beyond 250 (the script's distance step)");
        TestAssert.True(Buff(match.Foe, "Slow") is not null && Buff(match.FarFoe, "Slow") is not null, "both are slowed for a second");
        TestAssert.True(match.Foe.Speed < 0.75 * before, $"the slow takes a third of his speed (speed {match.Foe.Speed:0} from {before:0})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-pantheon-grand-skyfall-lifts-a-short-summoner-cooldown-to-two")]
    public static void GrandSkyfallLiftsAShortSummonerCooldown()
    {
        using var match = Arena(level: 6);
        // The channel's success stop sets each summoner slot that is under 2.75 seconds to 2 (BBSetSlotSpellCooldownTimeVer2, SPELLBOOK_SUMMONER).
        match.Hero.SummonerCooldowns[0] = 1.0;
        TestAssert.True(match.Cast(3, at: new Position2(7500, 7000)), "the ultimate is accepted");
        // The success stop removes the channel's buff in the same hook that sets the summoner slot; the flight's own cast comes half a second later.
        TestAssert.True(AdvanceUntil(match, () => Buff(match.Hero, "Pantheon_GrandSkyfall_Jump") is not null), "the cast starts the channel");
        TestAssert.True(AdvanceUntil(match, () => Buff(match.Hero, "Pantheon_GrandSkyfall_Jump") is null), "the channel succeeds");
        // The loop looks every twentieth of a second, so up to a quarter second of the two may have run; the unchanged engine reads 0 here.
        TestAssert.Near(2.0, match.Hero.SummonerCooldowns[0], 0.25, "the short summoner cooldown reads two seconds at the channel's success");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
