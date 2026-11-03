using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 5 (25 October 2026, batch 245): Trundle plays from his shipped scripts. The expected numbers are the rank-one literals of the
/// shipped inibins (the audit fixture's standalone decode) and the tooltips of fontconfig_en_US.txt; the scenario <c>S-Trundle</c> shows the same
/// facts on a headless match. The harness carries no masteries, so no cooldown reduction is assumed. Written 25 October 2026.
/// </summary>
public static class BbTrundleChecks
{
    [Check("scripting.bb-trundle-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Trundle");
        foreach (var name in new[]
                 {
                     "CharScriptTrundle", "TrundleTrollSmash", "TrundleQ", "TrundleQDebuff", "TrundleDesecrate", "TrundleDesecrateBuffs",
                     "TrundleCircle", "TrundleWallPush", "TrundlePain", "TrundlePainBuff", "TrundleDiseaseOverseer", "Crystallize", "Slow",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Trundle");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(23, closure.ScriptNames.Count(), "the closure is 23 scripts (bb-coverage: his four spells, the passive, the pillar's wall unit's scripts and the system buffs)");

        var hero = new Entity { ChampionName = "Trundle" };
        TestAssert.Equal(["TrundleTrollSmash", "trundledesecrate", "TrundleCircle", "TrundlePain"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Trundle.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Trundle")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 4800, RetailBot: true }, "playable as a jungler for 4800 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Trundle"), "listed once");
    }

    /// <summary>Rabid Bite (Q): the cast arms the next attack, the cooldown waits for the bite, and the bite gives the attack damage buff and saps the victim.</summary>
    [Check("scripting.bb-trundle-rabid-bite-arms-the-next-attack-and-saps-its-victim")]
    public static void RabidBiteArmsTheNextAttackAndSapsItsVictim()
    {
        var match = new BbM3.Match("Trundle");
        var victim = match.FarFoe;
        victim.Position = new Position2(match.Hero.Position.X + 100.0, match.Hero.Position.Z);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var attackDamage = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(0, match.Hero), "Q casts on himself");
        match.Advance(0.2);
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleTrollSmash") is not null, "the cast arms the bite (TrundleTrollSmash) on him");
        TestAssert.True(match.Hero.SpellCooldowns[0] < 0.5, "the cooldown waits while the bite is armed (it starts when the bite is spent)");
        var before = victim.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, victim), "his next attack is ordered");
        match.Advance(0.5);
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleTrollSmash") is null, "the bite spends the armed buff");
        TestAssert.True(match.Hero.SpellCooldowns[0] > 2.0 && match.Hero.SpellCooldowns[0] <= 4.0,
            $"the cooldown starts at the bite (4 s at rank one; {match.Hero.SpellCooldowns[0]:0.00} left)");
        TestAssert.True(before - victim.Health > 0.0, "the bite deals physical damage");
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleQ") is not null, "the bite gives him the attack damage buff (TrundleQ)");
        // Effect2 of TrundleTrollSmash is 20 at rank one (the buff's attack damage; the scenario measures 105.66 -> 125.66).
        TestAssert.Near(attackDamage + 20.0, match.Hero.AttackDamage, 0.5, "the bite adds 20 attack damage at rank one");
        TestAssert.True(BbM3.Buff(victim, "TrundleQDebuff") is not null, "the victim is sapped (TrundleQDebuff)");
    }

    /// <summary>Contaminate (W): inside the zone Trundle moves 20 percent faster and attacks 0.2 faster (the rank-one effects), and the zone's buff ends with its eight seconds.</summary>
    [Check("scripting.bb-trundle-contaminate-speeds-him-and-his-attacks-while-he-stands-in-it")]
    public static void ContaminateSpeedsHimAndHisAttacksInsideTheZone()
    {
        var match = new BbM3.Match("Trundle");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var speed = match.Hero.Speed;
        var attackSpeed = match.Hero.AttackSpeedMod;
        TestAssert.True(match.Cast(1, at: new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z)), "W casts on a point 150 units away");
        match.Advance(0.5);
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleDesecrateBuffs") is not null, "he stands in the zone and carries its buff");
        // Effect1 (movement speed) is 20 percent, Effect2 (attack speed) 20 percent of the base 1.0: both at rank one.
        TestAssert.Near(speed * 1.2, match.Hero.Speed, 2.0, "the zone gives 20 percent movement speed at rank one");
        TestAssert.Near(attackSpeed + 0.2, match.Hero.AttackSpeedMod, 0.02, "the zone gives 0.2 attack speed at rank one");
        match.Advance(9.0);
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleDesecrateBuffs") is null, "the zone's buff ends with the zone's eight seconds");
        TestAssert.Near(speed, match.Hero.Speed, 2.0, "his speed returns to the base");
    }

    /// <summary>Pillar of Filth (E): the pillar slows an enemy beside it; the slow is an overlapping buff, one instance per update (BUFF_STACKS_AND_OVERLAPS).</summary>
    [Check("scripting.bb-trundle-pillar-slows-the-enemies-beside-it")]
    public static void PillarSlowsTheEnemiesBesideIt()
    {
        var match = new BbM3.Match("Trundle");
        var pillar = new Position2(match.Hero.Position.X + 300.0, match.Hero.Position.Z);
        var victim = match.FarFoe;
        victim.Position = new Position2(pillar.X + 200.0, pillar.Z);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var speed = victim.Speed;
        TestAssert.True(match.Cast(2, at: pillar), "E casts at the pillar's spot");
        var seen = Within(match, () => BbM3.Buff(victim, "Slow") is not null, 1.0);
        TestAssert.True(!double.IsNaN(seen) && seen < 1.0, "the pillar's aura slows the enemy 200 units away");
        match.Advance(0.5);
        // The Slow instances of the aura overlap (one per update, each with its own timer), so the slow is at least the rank-one 25 percent.
        TestAssert.True(victim.Speed < 0.8 * speed, $"the slow takes more than a fifth of his speed (speed {speed:0.0} to {victim.Speed:0.0})");
    }

    /// <summary>Agony (R): the cast drains the target's health and 15 percent of its armor and magic resist; Trundle gains the armor it drains, and the drain of the stats ends with the buff.</summary>
    [Check("scripting.bb-trundle-agony-drains-health-armor-and-magic-resist")]
    public static void AgonyDrainsHealthArmorAndMagicResist()
    {
        var match = new BbM3.Match("Trundle");
        var victim = match.FarFoe;
        // Batch 252: the harness writes 5000 health straight into the hero (BbM3), and the first stat refresh (TrundlePain's IncFlatArmorMod in
        // BuffOnUpdateStats) re-bases MaxHealth to the champion's own value (Ezreal 430 at level 1) and clamps the health: that clamp was the
        // "4583 drained of 5000" of batch 245, and the 600 this check used to write was clamped the same way. The victim keeps his own pool here.
        match.Runtime.HeroStats.Refresh(victim);
        victim.Health = victim.MaxHealth;
        victim.Position = new Position2(match.Hero.Position.X + 300.0, match.Hero.Position.Z);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var armor = victim.Armor;
        var magicResist = victim.SpellBlock;
        var health = victim.Health;
        var maxHealth = victim.MaxHealth;
        var selfArmor = match.Hero.Armor;
        TestAssert.True(match.Cast(3, victim), "R casts on the enemy 300 units away");
        match.Advance(0.5);
        TestAssert.Near(maxHealth, victim.MaxHealth, 1e-9, "the drain does not touch his maximum health");
        // Effect2 is 15 percent of the armor (rank one), taken from the target and given to Trundle.
        var drained = armor - victim.Armor;
        TestAssert.Near(0.15 * armor, drained, 0.05 * armor, "15 percent of the armor is drained at rank one");
        TestAssert.Near(drained, match.Hero.Armor - selfArmor, 0.05, "Trundle gains the armor the target loses");
        // TrundlePain.OnBuffActivate: ModdedDamage = DamageDealt (100 at rank one) + 0.6 AP, magic, before the 15 percent of magic resist is
        // taken; then BuffOnUpdateActions' BBExecutePeriodically (ExecuteImmediately) deals DamageSecond = ModdedDamage / 6 through the lowered
        // resist. Plain resistance arithmetic, the regeneration of the half second inside the tolerance.
        var modded = 100.0 + 0.6 * match.Hero.AbilityPower;
        var expectedFirst = modded * 100.0 / (100.0 + magicResist) + modded / 6.0 * 100.0 / (100.0 + 0.85 * magicResist);
        var firstDrain = health - victim.Health;
        TestAssert.Near(expectedFirst, firstDrain, 2.0, $"the first drain is the script's 100 plus one sixth through the resist ({firstDrain:0.00})");
        match.Advance(6.5);
        TestAssert.True(health - victim.Health > 1.3 * firstDrain,
            $"the drain goes on for six seconds (first {firstDrain:0.00}, total {health - victim.Health:0.00}, health {victim.Health:0.00}, buffs {Names(victim)})");
        TestAssert.Near(armor, victim.Armor, 0.05, "the stolen armor returns when the drain ends");
    }

    /// <summary>Decompose (passive): when a nearby unit dies, Trundle heals for a percentage of its maximum health (2 percent at the first level, 6 at 18).</summary>
    [Check("scripting.bb-trundle-decompose-heals-on-a-nearby-death")]
    public static void DecomposeHealsOnANearbyDeath()
    {
        var match = new BbM3.Match("Trundle");
        var victim = match.FarFoe;
        victim.Position = new Position2(match.Hero.Position.X + 300.0, match.Hero.Position.Z);
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        match.Hero.Health = Math.Max(1.0, match.Hero.MaxHealth - 400.0);
        var before = match.Hero.Health;
        var victimMax = victim.MaxHealth;
        match.Runtime.Combat.Kill(victim, match.Ally.NetId);
        match.Advance(0.5);
        var percent = (match.Hero.Health - before) / victimMax;
        TestAssert.True(percent >= 0.02 - 1e-3 && percent <= 0.06 + 1e-3,
            $"he heals 2 to 6 percent of the dead unit's maximum health ({percent * 100.0:0.00} percent)");
        TestAssert.True(match.Hero.Health > before, "the heal lands on him");
    }

    /// <summary>The passive stays through a death and the respawn; the death takes his buffs that the scripts remove, and the passive is not one of them.</summary>
    [Check("scripting.bb-trundle-passive-survives-death-and-respawn")]
    public static void PassiveSurvivesDeathAndRespawn()
    {
        var match = new BbM3.Match("Trundle");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleDiseaseOverseer") is not null, "the passive is on him before the death");
        match.Runtime.Combat.Kill(match.Hero, match.FarFoe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        var respawned = Within(match, () => match.Hero.Alive, 120.0);
        TestAssert.True(!double.IsNaN(respawned), "he respawns");
        TestAssert.True(BbM3.Buff(match.Hero, "TrundleDiseaseOverseer") is not null, "the passive is on him after the respawn");
    }

    /// <summary>Advances the match until the condition holds; returns the seconds it took, or NaN when it does not hold within the window.</summary>
    private static double Within(BbM3.Match match, Func<bool> condition, double window)
    {
        for (var elapsed = 0.0; elapsed <= window; elapsed += BbM3.Step)
        {
            if (condition()) return elapsed;
            match.Advance(BbM3.Step);
        }
        return double.NaN;
    }
}
