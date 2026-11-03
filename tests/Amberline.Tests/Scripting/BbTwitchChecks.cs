using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 3 (batch 283, 26 October 2026): Twitch plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever
/// existed). Expected numbers are the literals of the shipped scripts and spell inibins, and the shipped tooltips where they agree; the scenario
/// <c>S-twitch</c> shows the same facts on a headless match. Batch 575: Spray and Pray's attack is a line missile (<c>SprayAndPrayAttack</c> CastType 3),
/// so its bolts pierce (batch 283 had read the script's primary-target damage alone).
/// </summary>
public static class BbTwitchChecks
{
    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static int Stacks(Entity unit) => Of(unit, "DeadlyVenom")?.Stacks ?? 0;

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    // The hero movement soft cap (HeroStats.ApplyMovementSoftCaps): below 220 a speed s reads 110 + s / 2.
    private static double Soft(double speed) => speed < 220.0 ? 110.0 + speed * 0.5 : speed;

    /// <summary>Twitch at rank one (level one: Deadly Venom's amount is 2.5 a stack), Ezreal 300 away, Sivir 400 away from him.</summary>
    private static Match Arena(int rank = 1)
    {
        var match = new Match("Twitch", foeChampion: "Ezreal");
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.FarFoe.Position = new Position2(7400, 7150);
        match.Foe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    [Check("scripting.bb-twitch-module-binds-the-shipped-closure")]
    public static void ModuleRunsItsClosureWithoutAFailure()
    {
        using var match = Arena();
        match.Advance(1.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        TestAssert.True(Of(match.Hero, "DeadlyVenom_marker") is not null, "the passive's marker is on him from his activation");
    }

    [Check("scripting.bb-twitch-deadly-venom-stacks-per-hit-and-ticks-true-damage")]
    public static void EachHitStacksAndEachSecondHurtsByTheStacks()
    {
        using var match = Arena();
        var foe = match.Foe;
        for (var hit = 1; hit <= 3; hit++)
        {
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, foe), "swing " + hit);
            match.Advance(1.5);
            TestAssert.Equal(hit, Stacks(foe), "one stack per landed attack");
        }
        // Each stack is 2.5 true damage a second at level one (CharVars.DamageAmount, the 1 to 5 band; 5.0 from level six, measured by S-twitch);
        // the victim's regeneration is taken out.
        var before = foe.Health;
        var regen = foe.HealthRegen;
        match.Advance(1.0);
        TestAssert.Near(7.5, before - foe.Health + regen, 0.6, "three stacks, 2.5 each, a second");
        match.Advance(6.0);
        TestAssert.Equal(0, Stacks(foe), "the stacks lapse 6.1 seconds after the last hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twitch-ambush-stealth-attack-speed-and-cooldown")]
    public static void AmbushStealthsAfterTheFadeAndGivesAttackSpeedWhenItEnds()
    {
        using var match = Arena();
        var hero = match.Hero;
        var baseSpeed = hero.AttackSpeedMod;
        TestAssert.True(match.Cast(0, hero), "Q casts");
        var fade = 0.0;
        while (Of(hero, "HideInShadows") is null && fade < 3.0)
        {
            match.Advance(0.1);
            fade += 0.1;
        }
        TestAssert.Near(1.75, fade, 0.3, "stealthed 1.75 seconds after the press (the 1.5 second fade after the 0.25 second cast)");
        var lasted = 0.0;
        while (Of(hero, "HideInShadows") is not null && lasted < 13.0)
        {
            match.Advance(0.1);
            lasted += 0.1;
        }
        TestAssert.Near(10.0, lasted, 0.25, "ten seconds of stealth, counted from the moment it began");
        TestAssert.Near(baseSpeed + 0.3, hero.AttackSpeedMod, 0.01, "30 percent attack speed when it ends (AttackSpeedMod 0.3 at rank one)");
        TestAssert.True(Of(hero, "HideInShadowsBuff") is not null, "the attack speed buff");
        TestAssert.Near(11.0, hero.SpellCooldowns[0], 0.3, "the inibin's 11 seconds start when the stealth ends");
        match.Advance(10.5);
        TestAssert.Near(baseSpeed, hero.AttackSpeedMod, 0.01, "the bonus lasts ten seconds (the buff's duration is the lesser of twice the stealth and ten)");
        hero.SpellCooldowns[0] = 0.0;
        TestAssert.True(match.Cast(0, hero), "Q casts again");
        match.Advance(2.0);
        TestAssert.True(Of(hero, "HideInShadows") is not null, "stealthed again");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "an attack from the stealth");
        match.Advance(0.6);
        TestAssert.True(Of(hero, "HideInShadows") is null, "an attack launch ends the stealth (BuffOnLaunchAttack)");
        TestAssert.True(Of(hero, "HideInShadowsBuff") is not null, "the attack speed buff follows the break");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twitch-venom-cask-slows-the-enemy-in-the-area-for-two-seconds")]
    public static void VenomCaskSlowsTheEnemyForItsDuration()
    {
        using var match = Arena();
        var foe = match.Foe;
        foe.Position = new Position2(7400, 7000);
        var baseSpeed = foe.Speed;
        TestAssert.True(match.Cast(1, match.Hero), "W casts");
        match.Advance(0.5);
        // Thirty percent off the move speed (no stacks), then the hero soft cap below 220.
        TestAssert.Near(Soft(baseSpeed * 0.7), foe.Speed, 1.0, "30 percent of the move speed at rank one");
        TestAssert.True(Of(foe, "DebilitatingPoison") is not null, "the slow is on the enemy");
        match.Advance(2.0);
        TestAssert.Near(baseSpeed, foe.Speed, 1.0, "the slow ends after two seconds at rank one");
        TestAssert.True(Of(foe, "DebilitatingPoison") is null, "the buff is gone");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twitch-expunge-hurts-only-the-poisoned-and-spends-the-stacks")]
    public static void ExpungeHurtsThePoisonedAndSpendsTheStacks()
    {
        using var match = Arena();
        var foe = match.Foe;
        for (var hit = 1; hit <= 3; hit++)
        {
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, foe), "swing " + hit);
            match.Advance(1.5);
        }
        TestAssert.Equal(3, Stacks(foe), "three stacks to spend");
        foe.Health = foe.MaxHealth;
        var foeHits = HitsOn(match, "Foe").Count;
        var farHits = HitsOn(match, "FarFoe").Count;
        TestAssert.True(match.Cast(2, match.Hero), "E casts");
        match.Advance(1.0);
        var blasts = HitsOn(match, "Foe").Skip(foeHits).ToList();
        TestAssert.Equal(1, blasts.Count, "one blast on the poisoned enemy");
        // BaseDamage 30 magic at rank one plus ExplosionDamage 20 for each stack (90 before resistance), after 30 magic resistance.
        TestAssert.Near(Mitigated(30.0 + 3 * 20.0, 30.0), blasts[0].Dealt, 0.5, "90 magic before resistance, 69.2 after 30 resistance");
        TestAssert.Equal(farHits, HitsOn(match, "FarFoe").Count, "a unit without the poison takes nothing");
        TestAssert.Equal(0, Stacks(foe), "the stacks are spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twitch-spray-and-pray-range-damage-and-five-attacks")]
    public static void SprayAndPrayAddsRangeAndDamageForFiveAttacks()
    {
        using var match = Arena();
        var hero = match.Hero;
        var range = hero.AttackRange;
        var damage = hero.AttackDamage;
        TestAssert.True(match.Cast(3, hero), "R casts");
        match.Advance(0.5);
        TestAssert.Near(375.0, hero.AttackRange - range, 0.01, "375 range (IncFlatAttackRangeMod)");
        TestAssert.Near(15.0, hero.AttackDamage - damage, 0.01, "15 bonus attack damage at rank one");
        TestAssert.Equal(5, Of(hero, "TwitchSprayAndPray")?.Stacks ?? 0, "five attacks at rank one");
        TestAssert.Near(105.0, hero.SpellCooldowns[3] + 0.5, 0.2, "the inibin's 105 seconds (the script's AutoCooldownByLevel of 90 is not read)");
        for (var attack = 1; attack <= 5; attack++)
        {
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "attack " + attack);
            match.Advance(1.5);
            TestAssert.Equal(5 - attack, Of(hero, "TwitchSprayAndPray")?.Stacks ?? 0, "one attack spent");
        }
        TestAssert.Near(0.0, hero.AttackRange - range, 0.01, "the range ends with the attacks");
        TestAssert.Near(0.0, hero.AttackDamage - damage, 0.01, "so does the bonus damage");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 575 (owner question 3): <c>SprayAndPrayAttack.inibin</c> is CastType 3 (a line missile), LineWidth 60, CastRange 1100, and its script
    /// sets <c>NotSingleTargetSpell</c>; the wiki's history gives "100% to all targets hit" since V0.9.22.15. A bolt fired at the foe goes on and
    /// hits the champion behind it with the same swing (its Deadly Venom stack included); an ally on the line is not hit, and a plain swing
    /// (TwitchBasicAttack, CastType 1) stops at its target. Red before the batch: the champion behind took nothing.
    /// </summary>
    [Check("scripting.bb-twitch-spray-and-pray-bolts-pierce-the-units-on-their-line")]
    public static void SprayAndPrayBoltsPierceTheUnitsOnTheirLine()
    {
        using var match = Arena();
        var hero = match.Hero;
        var behind = match.FarFoe;
        behind.Position = new Position2(7650, 7000);   // on the line, 350 past the foe and 650 from Twitch (inside the 1100)
        match.Ally.Position = new Position2(7150, 7000); // an ally on the line, between Twitch and the foe
        static int Basic(Match match, string victim) => HitsOn(match, victim).Count(hit => hit.Kind == "Basic");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "a plain swing");
        match.Advance(1.5);
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "a plain swing stops at the unit it was fired at");
        TestAssert.True(match.Cast(3, hero), "R casts");
        match.Advance(0.5);
        var foeBefore = Basic(match, "Foe");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "a Spray and Pray swing");
        match.Advance(1.5);
        TestAssert.Equal(foeBefore + 1, Basic(match, "Foe"), "the attacked unit takes its swing once");
        var pierced = HitsOn(match, "FarFoe").Where(hit => hit.Kind == "Basic").ToList();
        TestAssert.Equal(1, pierced.Count, "the bolt goes on and hits the champion behind once");
        TestAssert.Near(HitsOn(match, "Foe").Last(hit => hit.Kind == "Basic").Raw, pierced[0].Raw, 1e-6, "with the same swing");
        TestAssert.Equal(1, Stacks(behind), "and its on-hit: one Deadly Venom stack");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "an ally on the line is not hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-twitch-death-keeps-the-deadly-venom-marker")]
    public static void TheMarkerSurvivesADeathAndTheRespawn()
    {
        using var match = Arena();
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        TestAssert.True(!match.Hero.Alive, "he is dead");
        TestAssert.True(Of(match.Hero, "DeadlyVenom_marker") is not null, "PersistsThroughDeath: the marker stays on him");
        var waited = 0.0;
        while (!match.Hero.Alive && waited < 240.0)
        {
            match.Advance(1.0);
            waited += 1.0;
        }
        TestAssert.True(match.Hero.Alive, "he respawns");
        TestAssert.True(Of(match.Hero, "DeadlyVenom_marker") is not null, "the marker is on him after the respawn");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
