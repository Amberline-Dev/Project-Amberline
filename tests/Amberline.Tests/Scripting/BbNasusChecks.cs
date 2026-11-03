using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, new champions, wave 7 (25 October 2026, batch 247): Nasus plays from his shipped scripts. Expected numbers are the rank-one literals of
/// the shipped scripts and the spell inibins (the audit fixture's standalone decode), with the shipped tooltips of fontconfig_en_US.txt as the second
/// witness. The scenario <c>S-Nasus</c> shows the same facts on a headless match. The harness carries no masteries and no armor or magic resistance
/// unless a check sets them, so the raw figures are the script's. Written 25 October 2026.
/// </summary>
public static class BbNasusChecks
{
    [Check("scripting.bb-nasus-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Nasus");
        foreach (var name in new[]
                 {
                     "CharScriptNasus", "SoulEater", "SiphoningStrikeNew", "SiphoningStrike", "SiphoningStrikeDamageBonus", "Wither",
                     "SpiritFire", "SpiritFireAoE", "SpiritFireArmorReduction", "GodofDeath",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Nasus");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");

        var hero = new Entity { ChampionName = "Nasus" };
        TestAssert.Equal(["SiphoningStrikeNew", "Wither", "SpiritFire", "GodofDeath"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Nasus.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Nasus")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, RetailBot: true }, "playable as a top laner for 1350 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Nasus"), "listed once");
    }

    /// <summary>Siphoning Strike (Q, self): 20 mana at rank one; the next basic attack is one hit of its total attack damage plus 30 (the shipped
    /// BuffOnHitUnit: DamageAmount - TotalAD + TotalAD x ADMultiplier 1.0 + BaseDamage 30 + DamageBonus), and the hit spends the buff.</summary>
    [Check("scripting.bb-nasus-siphoning-strike-empowers-the-next-basic-attack")]
    public static void SiphoningStrikeEmpowersTheNextBasicAttack()
    {
        var match = new Match("Nasus");
        var victim = match.FarFoe;
        victim.Position = new Position2(7100.0, 7000.0);
        match.Hero.Position = new Position2(7000.0, 7000.0);
        victim.Armor = 0.0;
        victim.SpellBlock = 0.0;
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var attackDamage = match.Hero.AttackDamage;

        var plainBefore = victim.Health;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, victim), "a plain basic attack is queued");
        match.Advance(1.5);
        var plain = plainBefore - victim.Health;

        victim.Health = victim.MaxHealth;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Hero), "Siphoning Strike casts on himself");
        TestAssert.Near(20.0, mana - match.Hero.Mana, 1.0, "rank one costs 20 mana (the regeneration is under one point)");
        TestAssert.True(BbM3.Buff(match.Hero, "SiphoningStrikeNew") is not null, "the empowered buff is on him");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 0.01, "the cooldown does not run while the buff waits for its hit");
        match.Advance(0.3);

        var before = victim.Health;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, victim), "the empowered attack is queued");
        match.Advance(1.5);
        var empowered = before - victim.Health;
        TestAssert.True(BbM3.Buff(match.Hero, "SiphoningStrikeNew") is null, "the hit spends the buff");
        var cooldown = match.Hero.SpellCooldowns[0];
        TestAssert.True(cooldown > 6.4 && cooldown <= 8.0, $"the 8-second cooldown starts with the hit ({cooldown:0.00} left 1.5 s after it)");
        TestAssert.Near(attackDamage + 30.0, empowered, 1.5,
            $"the empowered swing is one hit of the total attack damage plus 30 ({empowered:0.00} against {plain:0.00} plain; {Hits(match)})");
    }

    /// <summary>Siphoning Strike's kill: the foe carries the 1-second <c>SiphoningStrike</c> mark of the hit, and its death gives Nasus a permanent
    /// <c>SiphoningStrikeDamageBonus</c> stack (the buff's <c>BuffOnDeath</c> of the shipped <c>SiphoningStrike</c> script).</summary>
    [Check("scripting.bb-nasus-siphoning-strike-kill-grants-a-permanent-stack")]
    public static void SiphoningStrikeKillGrantsAPermanentStack()
    {
        var match = new Match("Nasus");
        var victim = match.FarFoe;
        victim.Position = new Position2(7100.0, 7000.0);
        match.Hero.Position = new Position2(7000.0, 7000.0);
        victim.Armor = 0.0;
        victim.SpellBlock = 0.0;
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(0, match.Hero), "Siphoning Strike casts on himself");
        match.Advance(0.3);
        victim.Health = 1.0;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, victim), "the empowered attack is queued");
        match.Advance(1.5);
        TestAssert.False(victim.Alive, "the empowered hit kills the foe");
        var stack = BbM3.Buff(match.Hero, "SiphoningStrikeDamageBonus");
        TestAssert.True(stack is not null, "the kill gives Nasus the damage bonus stack");
        TestAssert.True(stack!.ExpiresAt > match.Now + 1000.0, "the stack is permanent");
    }

    /// <summary>Wither (W): a slow that starts at 35 percent and grows to 47 percent over its five seconds (the tooltip "increasingly slowed over
    /// time"); the rank-one figures are the spell's Effect2Amount, Effect1Amount and Effect3Amount (35, 47, 5).</summary>
    [Check("scripting.bb-nasus-wither-slows-and-ages-its-target")]
    public static void WitherSlowsItsTargetAndAgesIt()
    {
        var match = new Match("Nasus");
        var victim = match.FarFoe;
        victim.Position = new Position2(7100.0, 7000.0);
        match.Hero.Position = new Position2(7000.0, 7000.0);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var baseSpeed = victim.Speed;
        TestAssert.True(match.Cast(1, victim), "Wither casts on the foe");
        match.Advance(0.5);
        var early = victim.Speed / baseSpeed;
        TestAssert.True(BbM3.Buff(victim, "Wither") is not null, "the foe carries Wither");
        // Batch 252: the early figure is pinned below against the script's -0.35 through the movement floor (it is not 0.65 of the base speed).
        var trace = new List<string> { early.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) };
        var late = early;
        for (var i = 0; i < 10; i++)
        {
            match.Advance(0.5);
            trace.Add((victim.Speed / baseSpeed).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            if (i == 7) late = victim.Speed / baseSpeed; // four and a half seconds after the cast
        }
        TestAssert.True(late < early - 0.04, $"the slow grows over the duration (every half second: {string.Join(" ", trace)})");
        // Batch 252 (open question 2 of batch 247): Wither's arithmetic. OnBuffActivate sets TimeBetweenTicks = 5 / 5 = 1 s; BuffOnUpdateStats'
        // BBExecutePeriodically (not immediate) adds BonusSpeedMod (-0.03 at rank one) to SpeedMod (-0.35) each second, so the multiplicative
        // movement modifier is -0.35 until 1 s and -0.47 from 4 s (the tooltip's 35 rising to 47 percent). The ratio of the speeds is not the slow:
        // UnitStats.inibin's floor halves a speed below 220 (HeroStatCalculator.ApplyMovementSoftCaps).
        double Expected(double mod) => Amberline.Simulation.Stats.HeroStatCalculator.ApplyMovementSoftCaps(baseSpeed * (1.0 + mod)) / baseSpeed;
        TestAssert.Near(Expected(-0.35), early, 0.002, $"half a second in, the slow is the script's 35 percent (every half second: {string.Join(" ", trace)})");
        TestAssert.Near(Expected(-0.47), late, 0.002, $"four and a half seconds in, the slow is 35 + 4 x 3 = 47 percent (every half second: {string.Join(" ", trace)})");
        TestAssert.True(Vanishes(match, victim, "Wither", 2.0) > 0.0, "Wither ends after its five seconds");
    }

    /// <summary>Spirit Fire (E, a point): 55 magic at rank one on each foe in the area at once, and the armor debuff of 20 for five seconds.</summary>
    [Check("scripting.bb-nasus-spirit-fire-hits-its-area-and-reduces-armor-for-five-seconds")]
    public static void SpiritFireHitsItsAreaAndReducesArmor()
    {
        var match = new Match("Nasus");
        var victim = match.FarFoe;
        victim.Position = new Position2(7100.0, 7000.0);
        match.Hero.Position = new Position2(7000.0, 7000.0);
        victim.SpellBlock = 0.0;
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var armor = victim.Armor;
        TestAssert.True(match.Cast(2, at: victim.Position), "Spirit Fire casts at the point");
        match.Advance(0.6);
        TestAssert.True(BbM3.HitsOn(match, "FarFoe").Any(hit => Math.Abs(hit.Dealt - 55.0) < 2.0 && hit.Kind == "SpellAoe"),
            $"the initial hit is 55 magic at rank one ({Hits(match)})");
        TestAssert.Near(armor - 20.0, victim.Armor, 0.5, $"the armor debuff takes 20 armor ({armor:0.00} to {victim.Armor:0.00})");
        match.Advance(3.0);
        TestAssert.Near(armor - 20.0, victim.Armor, 0.5, "the debuff holds for its five seconds");
        var untilBack = 0.0;
        while (victim.Armor < armor - 0.5 && untilBack < 12.0) { match.Advance(0.1); untilBack += 0.1; }
        TestAssert.True(untilBack > 1.5 && untilBack < 8.0, $"the armor comes back when the debuff ends, after the zone and its tail ({untilBack:0.0} s more)");
        TestAssert.Near(armor, victim.Armor, 0.5, "the armor is back at its own value");
    }

    /// <summary>Fury of the Sands (R, self): 150 mana, a 120-second cooldown at rank one, 300 bonus health for fifteen seconds, the model scale
    /// (1 + 0.3, the E16 scale), and 3 percent of a nearby foe's health each second (the tooltip's drain).</summary>
    [Check("scripting.bb-nasus-fury-of-the-sands-adds-health-scale-and-a-burn-for-fifteen-seconds")]
    public static void FuryOfTheSandsAddsHealthScaleAndABurn()
    {
        var match = new Match("Nasus");
        var victim = match.FarFoe;
        victim.Position = new Position2(7150.0, 7000.0);
        match.Hero.Position = new Position2(7000.0, 7000.0);
        victim.SpellBlock = 0.0;
        match.Learn(1);
        match.Hero.Level = 6;
        match.Refill();
        match.Advance(0.5);
        var maxBefore = match.Hero.MaxHealth;
        TestAssert.True(match.Cast(3, match.Hero), "Fury of the Sands casts on himself");
        match.Advance(0.3);
        TestAssert.Near(300.0, match.Hero.MaxHealth - maxBefore, 0.5, "the bonus health is 300 at rank one");
        TestAssert.Near(1.3, ModelScale.Of(match.Hero), 0.01, "the model scale grows by 0.3");
        var grant = BbM3.Buff(match.Hero, "GodofDeath");
        TestAssert.True(grant is not null && grant.ExpiresAt > 14.0 && grant.ExpiresAt <= 15.0, $"the form lasts fifteen seconds ({grant?.ExpiresAt:0.00} left)");

        var health = victim.Health;
        match.Advance(1.0);
        var burn = health - victim.Health;
        TestAssert.Near(0.03 * victim.MaxHealth, burn, 3.0, $"each second takes 3 percent of the foe's maximum health ({burn:0.00} of {victim.MaxHealth:0.00})");
    }

    // The victim's hit records of the harness log, for a failure message.
    private static string Hits(BbM3.Match match) => string.Join(" | ", BbM3.HitsOn(match, "FarFoe")
        .Select(hit => $"{hit.At:0.00}:dealt {hit.Dealt:0.00} raw {hit.Raw:0.00} {hit.Kind}"));

    // The match clock when a buff is first seen gone (NaN if it stays past the window).
    private static double Vanishes(BbM3.Match match, Entity unit, string name, double window)
    {
        var deadline = match.Now + window;
        while (match.Now < deadline && BbM3.Buff(unit, name) is not null) match.Advance(0.05);
        return BbM3.Buff(unit, name) is null ? match.Now : double.NaN;
    }

    /// <summary>Soul Eater (passive): bonus lifesteal by level, 14 percent below 6, 17 from 6 and 20 from 11 (the script's level tiers; the tooltip
    /// "bonus Lifesteal").</summary>
    [Check("scripting.bb-nasus-soul-eater-lifesteal-follows-the-level")]
    public static void SoulEaterLifestealFollowsTheLevel()
    {
        var match = new Match("Nasus");
        match.Advance(1.5);
        TestAssert.Near(0.14, match.Hero.LifeSteal, 1e-6, "level 1 has 14 percent lifesteal");
        match.Hero.Level = 6;
        match.Advance(1.5);
        TestAssert.Near(0.17, match.Hero.LifeSteal, 1e-6, "level 6 has 17 percent lifesteal");
        match.Hero.Level = 11;
        match.Advance(1.5);
        TestAssert.Near(0.20, match.Hero.LifeSteal, 1e-6, "level 11 has 20 percent lifesteal");
    }

    /// <summary>Death: the Siphoning Strike stack and Soul Eater persist through the death, Fury of the Sands does not (its buff does not
    /// persist through a death in its script).</summary>
    [Check("scripting.bb-nasus-stacks-and-soul-eater-persist-through-death")]
    public static void StacksAndSoulEaterPersistThroughDeath()
    {
        var match = new Match("Nasus");
        match.Learn(1);
        match.Hero.Level = 6;
        match.Refill();
        match.Advance(0.5);
        match.Hero.Level = 6;
        TestAssert.True(match.Cast(3, match.Hero), "Fury of the Sands casts on himself");
        match.Advance(0.3);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        for (var i = 0; i < 1200 && !match.Hero.Alive; i++) match.Advance(0.1);
        TestAssert.True(match.Hero.Alive, "Nasus respawns");
        match.Advance(1.5);
        TestAssert.True(BbM3.Buff(match.Hero, "GodofDeath") is null, "Fury of the Sands ends with the death");
        TestAssert.True(BbM3.Buff(match.Hero, "SoulEater") is not null, "Soul Eater persists through the death");
        TestAssert.Near(0.17, match.Hero.LifeSteal, 1e-6, "Soul Eater still gives its level's lifesteal after the respawn");
    }
}
