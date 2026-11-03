using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Replication;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, new champions (batch 313, wave 6, 25 October 2026): Nocturne plays from his shipped scripts. The expected numbers are the literals of the
/// shipped scripts and of the rank-one spell inibins, and the scenario <c>S-nocturne</c> shows the same facts on a headless match. He is not a retail bot.
/// Batch 349 settled the batch 313 record's open findings: the dash's extra term is the swing of its AI_ATTACKTO order, the Horror's last hit is one
/// more tick, and the Duskbringer reaches a foe 300 away.
/// </summary>
public static class BbNocturneChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static bool Has(Entity unit, string name) => Buff(unit, name) is not null;

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Nocturne at rank one (level one), Ezreal 100 in front of him (inside his attack range of 125) with no armour,
    /// Sivir 224 away, inside his 360 area.</summary>
    private static Match Arena()
    {
        var match = new Match("Nocturne", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.FarFoe.Position = new Position2(7200, 7100);
        match.Foe.Armor = 0.0;
        match.FarFoe.Armor = 0.0;
        match.Refill();
        return match;
    }

    /// <summary>Advances until the passive is back (checked every tenth of a second), up to <paramref name="limit"/> seconds.</summary>
    private static void UntilPassive(Match match, double limit)
    {
        var waited = 0.0;
        while (!Has(match.Hero, "NocturneUmbraBlades") && waited < limit)
        {
            match.Advance(0.1);
            waited += 0.1;
        }
    }

    [Check("scripting.bb-nocturne-module-binds-the-shipped-closure")]
    public static void ModuleRunsItsClosureWithoutAFailure()
    {
        using var match = Arena();
        match.Advance(11.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        TestAssert.True(Has(match.Hero, "NocturneUmbraBlades"), "the passive is ready ten seconds after the start (LastHit starts at zero)");
    }

    [Check("scripting.bb-nocturne-umbra-blades-proc-hits-the-area-and-heals-per-hit")]
    public static void ProcHitsEverythingWithinTheAreaAndHealsPerUnitHit()
    {
        using var match = Arena();
        match.Advance(11.0);
        var hero = match.Hero;
        var foe = match.Foe;
        var far = match.FarFoe;
        var attack = hero.AttackDamage;
        var foeBefore = foe.Health;
        var farBefore = far.Health;
        // The heal is capped at his maximum health, so he starts 100 short of it.
        hero.Health = hero.MaxHealth - 100.0;
        var heroBefore = hero.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "the attack that procs");
        match.Advance(1.5);
        // 120 percent of his attack damage, physically, on the target and on the other enemy within 360; 15 health for each unit it hits (level one).
        TestAssert.Near(1.2 * attack, foeBefore - foe.Health + foe.HealthRegen * 1.5, 2.0, "the proc's hit on the target");
        TestAssert.Near(1.2 * attack, farBefore - far.Health + far.HealthRegen * 1.5, 2.0, "the proc's hit on the enemy within 360");
        TestAssert.Near(30.0, hero.Health - heroBefore, 3.0, "a heal of 15 for each of the two units hit");
        TestAssert.False(Has(hero, "NocturneUmbraBlades"), "the proc uses the passive up (its attack's marker removes it)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nocturne-attacks-shorten-the-umbra-blades-timer")]
    public static void AttacksWhileThePassiveIsDownBringItBackEarlier()
    {
        using var match = Arena();
        match.Advance(11.0);
        var hero = match.Hero;
        var foe = match.Foe;
        // A proc and then nothing: the passive is back ten seconds after the proc's hit.
        var procAt = match.Now;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "the first proc");
        match.Advance(1.5);
        UntilPassive(match, 14.0);
        var plain = match.Now - procAt;
        // A second proc, then three attacks while the passive is down: each landed attack moves its timer one second, so it is back about seven seconds on.
        procAt = match.Now;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "the second proc");
        match.Advance(1.5);
        for (var hit = 0; hit < 3; hit++)
        {
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "attack " + hit);
            match.Advance(1.5);
        }
        UntilPassive(match, 14.0);
        var shortened = match.Now - procAt;
        TestAssert.Near(10.0, plain, 0.6, "back ten seconds after a proc with no attacks");
        TestAssert.True(shortened <= plain - 2.0, $"three attacks while it is down bring it back earlier ({shortened:0.0} against {plain:0.0} seconds)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nocturne-duskbringer-hits-for-its-script-damage-and-marks-a-hero")]
    public static void DuskbringerHitsForItsScriptDamageAndMarksAHero()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        var foe = match.Foe;
        var foeBefore = foe.Health;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, at: foe.Position), "Q casts along the line past the foe");
        match.Advance(0.4);
        TestAssert.Near(60.0, mana - hero.Mana, 2.0, "60 mana at rank one (the inibin)");
        TestAssert.Near(10.0, hero.SpellCooldowns[0], 0.5, "10 seconds at rank one (the inibin)");
        TestAssert.True(Has(hero, "NocturneDuskbringerHaste"), "the caster gets the haste and the bonus damage with the cast (SelfExecute)");
        match.Advance(1.1);
        // The script deals its DamageByLevel physical plus 0.75 of his bonus attack damage; the haste the cast gives him (15 at rank one) is up
        // at the hit. Batch 335 (owner, 26 October 2026, "Wiki value per spell"): the script's table says 48.75 at rank one, the inibin, the
        // tooltip and the wiki (V1.0.0.118) 60; BbScriptOverrides loads 60/105/150/195/240 into the script's table.
        TestAssert.Near(60.0 + 0.75 * 15.0, foeBefore - foe.Health + foe.HealthRegen * 1.5, 1.5, "60 at rank one plus 0.75 of the haste's 15 bonus attack damage");
        TestAssert.True(Has(foe, "NocturneDuskbringerTrail"), "a hero it hits takes the Dusk Trail");
        match.Advance(5.0);
        TestAssert.False(Has(foe, "NocturneDuskbringerTrail"), "the Dusk Trail lasts five seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nocturne-shroud-shield-breaks-on-a-spell-and-boosts-attack-speed")]
    public static void ShieldBreaksOnASpellAndTheBoostFollows()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.SpellRanks = [1, 1, 1, 1];
        foe.Mana = foe.MaxMana;
        var speed = hero.AttackSpeedMod;
        TestAssert.True(match.Cast(1, hero), "W shields him");
        match.Advance(0.3);
        TestAssert.True(Has(hero, "NocturneShroudofDarkness"), "the shield is up for two seconds");
        TestAssert.True(match.Cast(0, hero, caster: foe), "Ezreal's Q hits him");
        match.Advance(1.5);
        TestAssert.False(Has(hero, "NocturneShroudofDarkness"), "a spell that hits him breaks the shield");
        TestAssert.True(Has(hero, "NocturneShroudofDarknessBuff"), "the attack speed boost follows the break");
        TestAssert.True(hero.AttackSpeedMod - speed > 0.15, $"the boost adds 20 percent at rank one ({speed:0.000} to {hero.AttackSpeedMod:0.000})");
        match.Advance(5.0);
        TestAssert.False(Has(hero, "NocturneShroudofDarknessBuff"), "the boost lasts five seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nocturne-unspeakable-horror-ticks-every-three-quarters-of-a-second")]
    public static void TetherHurtsEveryThreeQuartersOfASecondAndEndsByTwoSeconds()
    {
        using var match = Arena();
        match.Advance(1.0);
        var foe = match.Foe;
        var before = foe.Health;
        TestAssert.True(match.Cast(2, foe), "E casts on Ezreal");
        var samples = new List<string>();
        var last = foe.Health;
        for (var step = 1; step <= 6; step++)
        {
            match.Advance(0.25);
            if (Math.Abs(foe.Health - last) > 0.5) samples.Add($"{step * 0.25:0.00}s={foe.Health - last:0.00}");
            last = foe.Health;
        }
        // 12.5 magic at rank one with every 0.75 seconds of the tether, less the foe's magic resistance (100 / (100 + MR)): three ticks by 1.5 seconds.
        TestAssert.True(Has(foe, "NocturneUnspeakableHorror"), "the tether is on the foe for up to three seconds");
        TestAssert.Near(3 * 12.5 * 100.0 / (100.0 + foe.SpellBlock), before - foe.Health + foe.HealthRegen * 1.5, 2.5,
            "three ticks of 12.5 magic in the first 1.5 seconds: " + string.Join(" ", samples));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 349 (the record's open 4, "about 4550 at the fear at level one"): the whole tether. <c>NocturneUnspeakableHorror</c> hits 12.5 at rank one
    /// at once and every 0.75 s (0, 0.75, 1.5), fears at 2 s (<c>BBExecutePeriodically</c> 2, not immediately) and removes itself, and its
    /// <c>OnBuffDeactivate</c> hits once more because <c>Feared</c> is true: four hits, 50 at rank one, the tooltip's "over two seconds".
    /// </summary>
    [Check("scripting.bb-nocturne-unspeakable-horror-fears-at-two-seconds-with-a-fourth-hit")]
    public static void TetherFearsAtTwoSecondsWithAFourthHit()
    {
        using var match = Arena();
        match.Advance(1.0);
        var foe = match.Foe;
        var cast = match.Now;
        TestAssert.True(match.Cast(2, foe), "E casts on Ezreal");
        match.Advance(3.0);
        TestAssert.False(Has(foe, "NocturneUnspeakableHorror"), "the tether has ended by three seconds");
        // Read from the hit log, not from his health: the harness gives its heroes MaxHealth 5000 (BbM3.Match), and the fear's status change
        // refreshes his stats, which clamps that to his real maximum: the 4550 "hit" of the batch 313 record (harness artifact).
        var hits = HitsOn(match, "Foe").Where(hit => hit.At > cast).ToList();
        var timeline = string.Join(" ", hits.Select(hit => $"{hit.At - cast:0.00}s raw {hit.Raw:0.00} {hit.Kind}"));
        TestAssert.Equal(4, hits.Count, "three ticks and the fear's last hit: " + timeline);
        TestAssert.True(hits.All(hit => Math.Abs(hit.Raw - 12.5) < 1e-6), "each 12.5 magic at rank one: " + timeline);
        TestAssert.Near(0.75, hits[1].At - hits[0].At, 0.05, "the ticks are 0.75 s apart: " + timeline);
        TestAssert.Near(2.0, hits[3].At - hits[0].At, 0.05, "the fear's hit comes two seconds after the first: " + timeline);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>Batch 349 (the record's open 5, "missiles at 300"): the Duskbringer reaches a foe 300 away in the match harness.</summary>
    [Check("scripting.bb-nocturne-duskbringer-hits-a-foe-three-hundred-away")]
    public static void DuskbringerHitsAFoeThreeHundredAway()
    {
        using var match = Arena();
        var foe = match.Foe;
        foe.Position = new Position2(7300, 7000);
        match.FarFoe.Position = new Position2(9000, 9000);
        match.Advance(1.0);
        var before = foe.Health;
        TestAssert.True(match.Cast(0, at: foe.Position), "Q casts at the foe 300 away");
        match.Advance(1.5);
        TestAssert.True(before - foe.Health > 50.0, $"the blade hits the foe 300 away (lost {before - foe.Health:0.00})");
        TestAssert.True(Has(foe, "NocturneDuskbringerTrail"), "and leaves the Dusk Trail on him");
    }

    [Check("scripting.bb-nocturne-paranoia-cuts-sight-then-dashes-to-the-foe")]
    public static void WindowCutsTheSightAndTheSecondPressDashes()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        var foe = match.Foe;
        TestAssert.True(match.Cast(3, hero), "R opens the window");
        match.Advance(0.3);
        TestAssert.True(Has(hero, "NocturneParanoia"), "the window is open for four seconds");
        TestAssert.Near(-300.0, VisibilityRules.BubbleRadiusMods(foe).Flat, 1.0, "the enemy champion's sight radius is 300 less");
        var from = hero.Position;
        var foeBefore = foe.Health;
        TestAssert.True(match.Cast(3, foe), "the second press dashes to the foe");
        match.Advance(1.0);
        // The dash hits for 150 physical at rank one (the script's BaseDamage) and ends the window. Batch 349 (the record's open 3, "a stat term of about
        // 47"): NocturneParanoiaDash's arrival ends with BBIssueOrder AI_ATTACKTO on the target, so his first swing lands inside this second as well:
        // 150 plus one plain attack (57.1 at level one, no armour). Script = retail; GetFlatPhysicalDamageMod is 0 here.
        var dealt = foeBefore - foe.Health + foe.HealthRegen;
        TestAssert.Near(150.0 + hero.AttackDamage, dealt, 2.0, $"the dash's 150 and the swing its attack order starts (measured {dealt:0.00}, attack damage {hero.AttackDamage:0.00})");
        TestAssert.Equal(foe.NetId, hero.AttackTarget ?? 0u, "the dash leaves him attacking the foe (AI_ATTACKTO)");
        TestAssert.True(Distance(from, hero.Position) > 20.0, "he moved to the foe");
        TestAssert.False(Has(hero, "NocturneParanoia"), "the window ends with the dash");
        TestAssert.Near(160.0, hero.SpellCooldowns[3], 2.0, "the cooldown is 160 seconds once the window ends (the script's NewCd at rank one)");
        match.Advance(3.0);
        TestAssert.Near(0.0, VisibilityRules.BubbleRadiusMods(foe).Flat, 1.0, "the sight comes back four seconds after the first press");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-nocturne-paranoia-window-closes-after-four-seconds")]
    public static void AnUnusedWindowEndsAfterFourSecondsWithItsCooldown()
    {
        using var match = Arena();
        match.Advance(1.0);
        var hero = match.Hero;
        var foe = match.Foe;
        TestAssert.True(match.Cast(3, hero), "R opens the window");
        match.Advance(4.2);
        TestAssert.False(Has(hero, "NocturneParanoia"), "without a dash the window closes after four seconds");
        TestAssert.Near(0.0, VisibilityRules.BubbleRadiusMods(foe).Flat, 1.0, "the sight comes back");
        TestAssert.Near(160.0, hero.SpellCooldowns[3], 2.0, "the cooldown starts at the window's end: 160 seconds (the inibin)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
