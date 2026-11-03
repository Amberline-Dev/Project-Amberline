using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 9 (25 October 2026, batch 338): Wukong (MonkeyKing) plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are
/// the literals of the shipped scripts and spell inibins (decoded with the standalone inibin reader of the batch, not with a Amberline reader); the scenario
/// <c>S-monkeyking</c> shows the same facts on a headless match. Stone Skin is his passive, Crushing Blow (Q), Decoy (W), Nimbus Strike (E) and Cyclone (R).
/// The Cyclone's first hit knocks the enemy up and then a five-second marker (per team) stops further knock-ups; its damage is a half-second tick.
/// </summary>
public static class BbMonkeyKingChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>The Wukong match: no attack damage bonus, the foe 300 away with 30 magic resistance and no armour, the far foe out of range.</summary>
    private static Match Arena(int rank = 1)
    {
        var match = new Match("MonkeyKing", foeChampion: "Orianna");
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Foe.Armor = 0.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Refill();
        return match;
    }

    private static double Armour(Match match)
    {
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        return match.Hero.Armor;
    }

    private static Entity? Clone(Match match, string buff) =>
        match.Runtime.World.Entities.Values.FirstOrDefault(entity => entity.Alive && entity != match.Hero && entity.Buffs.Any(item => item.Name == buff));

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks, the figures of the scripts
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("MonkeyKing");
        foreach (var name in new[] { "CharScriptMonkeyKing", "MonkeyKingPassive", "MonkeyKingDoubleAttack", "MonkeyKingDecoy", "MonkeyKingNimbus",
                     "MonkeyKingSpinToWin", "MonkeyKingDecoyClone", "MonkeyKingNimbusKickClone", "MonkeyKingSpinKnockup", "MonkeyKingSpinMarkerOrder" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("MonkeyKing");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(36, closure.ScriptNames.Count(), "the closure is 36 scripts: his own, the system buffs and the clones");

        var hero = new Entity { ChampionName = "MonkeyKing" };
        TestAssert.Equal(["MonkeyKingDoubleAttack", "MonkeyKingDecoy", "MonkeyKingNimbus", "MonkeyKingSpinToWin"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from MonkeyKing.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("MonkeyKing")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 6300, Id: 62, RetailBot: false }, "playable as a top laner for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "MonkeyKing"), "listed once");

        // The tables the figures rest on, read back from the shipped scripts (the second witness is the spell inibins, decoded in the audit entry).
        TestAssert.Equal([30.0, 60.0, 90.0, 120.0, 150.0], ByLevel("MonkeyKingDoubleAttack", "BuffOnHitUnit", "BonusDamage"), "Crushing Blow: bonus damage by rank");
        TestAssert.Equal([9.0, 8.0, 7.0, 6.0, 5.0], ByLevel("MonkeyKingDoubleAttack", "SelfExecute", "SpellCooldown"), "Crushing Blow: cooldown by rank");
        TestAssert.Equal([70.0, 115.0, 160.0, 205.0, 250.0], ByLevel("MonkeyKingDecoySwipe", "TargetExecute", "BaseDamage"), "Decoy's explosion by rank");
        TestAssert.Equal([60.0, 105.0, 150.0, 195.0, 240.0], ByLevel("MonkeyKingNimbus", "TargetExecute", "DamageVar"), "Nimbus Strike by rank");
        TestAssert.Equal([0.2, 0.25, 0.3, 0.35, 0.4], ByLevel("MonkeyKingNimbus", "TargetExecute", "AttackSpeedVar"), "Nimbus Strike: attack speed by rank");
        TestAssert.Equal([20.0, 110.0, 200.0, 0.0, 0.0], ByLevel("MonkeyKingSpinToWin", "SelfExecute", "BaseDamage"), "Cyclone: damage per second by rank");
        TestAssert.Equal([120.0, 105.0, 90.0, 90.0, 90.0], ByLevel("MonkeyKingSpinToWin", "SelfExecute", "SpellCooldown"), "Cyclone: cooldown by rank");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Stone Skin
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-stone-skin-counts-visible-enemy-heroes")]
    public static void StoneSkinGivesArmourAndResistanceForEachNearbyEnemy()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(9000, 7000);
        match.Advance(1.2);
        var baseline = Armour(match);
        var baseResistance = match.Hero.SpellBlock;
        TestAssert.True(Buff(match.Hero, "MonkeyKingPassive") is not null, "Stone Skin is on from the start");
        TestAssert.True(Failures(match).Length == 0, "no script failure");

        match.Foe.Position = new Position2(7300, 7000); // one enemy hero inside 1400 (the foe's spot in the other checks is 7150)
        match.Advance(1.2);
        TestAssert.Near(baseline + 4.0, Armour(match), 0.05, "level 1: four armour for one enemy");
        TestAssert.Near(baseResistance + 4.0, match.Hero.SpellBlock, 0.05, "and four magic resistance");

        match.FarFoe.Position = new Position2(7600, 7000); // two enemy heroes, both in sight (a foe 1000 away is out of his sight)
        match.Advance(1.2);
        TestAssert.Near(baseline + 8.0, Armour(match), 0.05, "level 1: eight armour for two enemies");

        // The level changes the base armour too, so the higher tiers are read as the change when one enemy leaves (the base stays put).
        match.Hero.Level = 7;
        match.Advance(1.2);
        var twoAtSeven = Armour(match);
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Advance(1.2);
        TestAssert.Near(6.0, twoAtSeven - Armour(match), 0.05, "level 7: six per enemy (one enemy leaves: minus six)");

        match.Hero.Level = 13;
        match.Advance(1.2);
        var oneAtThirteen = Armour(match);
        match.FarFoe.Position = new Position2(7600, 7000);
        match.Advance(1.2);
        TestAssert.Near(8.0, Armour(match) - oneAtThirteen, 0.05, "level 13: eight per enemy (one enemy comes back: plus eight)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Crushing Blow (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-crushing-blow-adds-bonus-damage-to-the-next-attack")]
    public static void CrushingBlowAddsItsBonusToTheNextAttackOnly()
    {
        using var match = Arena();
        match.Foe.Armor = 40.0;
        match.Advance(0.3);
        var plain = match.Hero.AttackDamage;
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        var armourBefore = match.Foe.Armor;
        TestAssert.True(match.Cast(0), "Q casts on himself");
        match.Advance(0.3);
        TestAssert.True(Buff(match.Hero, "MonkeyKingDoubleAttack") is not null, "the Crushing Blow buff is on him for six seconds");
        TestAssert.True(match.Hero.SpellCooldowns[0] < 0.05, "the Q slot shows no cooldown while the buff runs (SelfExecute zeroes it)");
        match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
        match.Advance(1.5);
        // The swing is one proc hit (the attack's damage plus the bonus) and a basic record of zero: the shipped script multiplies the attack's
        // DamageAmount by 0 once it has used it, so only the proc hit deals damage.
        var all = HitsOn(match, "Foe");
        var hits = all.Where(hit => hit.Raw > 0.0).ToList();
        TestAssert.True(hits.Count >= 1, "the next attack lands");
        TestAssert.Equal(1, hits.Count, "one swing, one damaging hit: " + string.Join(" ", all.Select(hit => $"[{hit.At:0.000} raw {hit.Raw:0.00} dealt {hit.Dealt:0.00} {hit.Kind}]")));
        TestAssert.Near(plain + 30.0, hits[0].Raw, 0.5, "rank one: the attack plus 30 bonus physical damage");
        TestAssert.True(Buff(match.Hero, "MonkeyKingDoubleAttack") is null, "the first hit spends the buff");
        // The shipped BuffOnHitUnit tests CO_IS_TYPE_AI, which holds for a hero too (obj_AI_Base is the parent of heroes, minions and turrets): a
        // champion takes the thirty percent armour debuff for three seconds, the turret branch alone does not (no armour debuff on a turret).
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        TestAssert.True(Buff(match.Foe, "MonkeyKingDoubleAttackDebuff") is not null, "the foe carries the armour debuff for three seconds");
        TestAssert.Near(armourBefore * 0.7, match.Foe.Armor, 1e-6, "thirty percent less armour (the debuff is a percent mod)");
        match.Advance(3.2);
        match.Runtime.HeroStats.Refresh(match.Foe, push: false);
        TestAssert.True(Buff(match.Foe, "MonkeyKingDoubleAttackDebuff") is null, "the debuff ends after three seconds");
        TestAssert.Near(armourBefore, match.Foe.Armor, 1e-6, "and the armour comes back");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    [Check("scripting.bb-monkeyking-crushing-blow-cooldown-starts-when-the-buff-ends")]
    public static void CrushingBlowCooldownStartsWhenTheBuffEnds()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0), "Q casts");
        match.Advance(6.2);
        TestAssert.True(Buff(match.Hero, "MonkeyKingDoubleAttack") is null, "the buff ends after six seconds without a hit");
        TestAssert.Near(9.0, match.Hero.SpellCooldowns[0] + 0.2, 0.1, "rank one: nine seconds, starting when the buff ends");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Decoy (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-decoy-clone-explodes-after-one-and-a-half-seconds")]
    public static void DecoyCloneExplodesAfterItsStealthAndDies()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(0.2);
        TestAssert.True(Buff(match.Hero, "MonkeyKingDecoyStealth") is not null, "he is stealthed while the decoy lives");
        var clone = Clone(match, "MonkeyKingDecoyClone");
        TestAssert.True(clone is not null, "a clone stands where he stood");
        match.Advance(1.0);
        TestAssert.True(clone!.Alive, "the clone is still there before its one and a half seconds run out");
        match.Advance(0.6);
        var swipe = HitsOn(match, "Foe");
        TestAssert.True(swipe.Count >= 1, "the explosion hurts the foe near the clone");
        TestAssert.Near(Mitigated(70.0, 30.0), swipe[0].Dealt, 0.05, "rank one: 70 magic damage through 30 resistance (no ability power)");
        // The clone's death is the self hit of Spells/MonkeyKingKillCloneW's OnBuffDeactivate (9999 true damage): it dies when that 1.25 s buff ends.
        TestAssert.True(Buff(clone!, "MonkeyKingKillCloneW") is not null && clone!.Alive, "the explosion leaves the clone for its 1.25 s death buff");
        match.Advance(1.4);
        TestAssert.True(!clone!.Alive, "the clone dies when its death buff ends (its self hit is in OnBuffDeactivate)");
        TestAssert.True(Buff(match.Hero, "MonkeyKingDecoyStealth") is null, "the stealth ends with its one and a half seconds");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    [Check("scripting.bb-monkeyking-decoy-stealth-breaks-on-an-attack")]
    public static void DecoyStealthBreaksWhenHeAttacks()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(1), "W casts");
        match.Advance(0.2);
        TestAssert.True(Buff(match.Hero, "MonkeyKingDecoyStealth") is not null, "stealthed");
        match.Runtime.Spells.HeroOrders!.StartAttack(match.Hero, match.Foe);
        match.Advance(0.6);
        TestAssert.True(Buff(match.Hero, "MonkeyKingDecoyStealth") is null, "his attack ends the stealth (BuffOnLaunchAttack)");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Nimbus Strike (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-nimbus-strike-dashes-hits-once-and-gives-attack-speed")]
    public static void NimbusStrikeDashesToTheTargetAndHitsIt()
    {
        using var match = Arena();
        var before = match.Hero.AttackSpeedMod;
        TestAssert.True(match.Cast(2, match.Foe), "E casts on the foe");
        match.Advance(0.9);
        var hits = HitsOn(match, "Foe");
        TestAssert.True(hits.Count >= 1, "the dash ends in a hit");
        TestAssert.Near(60.0, hits[0].Dealt, 0.05, "rank one: 60 physical damage (the foe has no armour; no bonus attack damage, so no 0.8 share)");
        TestAssert.True(Buff(match.Hero, "MonkeyKingNimbusAS") is not null, "the attack speed buff follows");
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        TestAssert.True(match.Hero.AttackSpeedMod > before * 1.19, "rank one: twenty percent attack speed");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    [Check("scripting.bb-monkeyking-nimbus-strike-clone-strikes-a-nearby-enemy-with-true-damage")]
    public static void NimbusStrikeCloneStrikesANearbyEnemyWithTrueDamage()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(7450, 7000); // 150 from the foe, inside the 320 of the search
        TestAssert.True(match.Cast(2, match.Foe), "E casts on the foe");
        match.Advance(0.9);
        var strikes = HitsOn(match, "FarFoe");
        TestAssert.True(strikes.Count >= 1, "a clone flies to the enemy near the target and strikes it");
        TestAssert.Near(60.0, strikes[0].Dealt, 0.05, "rank one: 60 true damage (not reduced by the 30 resistance)");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Cyclone (R)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-cyclone-ticks-every-half-second-and-knocks-up-once")]
    public static void CycloneTicksEveryHalfSecondAndKnocksUpOnce()
    {
        using var match = Arena(rank: 1);
        var attack = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(3), "R casts");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Hero, "MonkeyKingSpinToWin") is not null, "the spin runs");
        TestAssert.True(Buff(match.Foe, "MonkeyKingSpinKnockup") is not null, "the first hit knocks the foe up");
        TestAssert.True(Buff(match.Foe, "MonkeyKingSpinMarkerOrder") is not null, "and marks it for five seconds");
        match.Advance(4.1);
        var ticks = HitsOn(match, "Foe");
        TestAssert.True(ticks.Count >= 8 && ticks.Count <= 9, $"eight ticks in four seconds (saw {ticks.Count})");
        TestAssert.Near(0.5 * (20.0 + 1.2 * attack), ticks[0].Raw, 0.5, "each tick: half of (20 plus 1.2 times the attack damage)");
        TestAssert.True(Buff(match.Hero, "MonkeyKingSpinToWin") is null, "the spin ends after four seconds");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    [Check("scripting.bb-monkeyking-cyclone-marker-stops-knock-ups-for-five-seconds")]
    public static void CycloneMarkerStopsRepeatKnockUps()
    {
        using var match = Arena(rank: 1);
        TestAssert.True(match.Cast(3), "R casts");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Foe, "MonkeyKingSpinMarkerOrder") is not null, "the marker runs");
        TestAssert.True(Buff(match.Foe, "MonkeyKingSpinKnockup") is null, "the one-second knock-up has ended");
        // The spin ticks on for another 2.8 s inside the marker's five seconds: no second knock-up.
        match.Advance(2.8);
        TestAssert.True(Buff(match.Foe, "MonkeyKingSpinKnockup") is null, "the marker stops a second knock-up while it runs");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive through death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-monkeyking-stone-skin-persists-through-death")]
    public static void StoneSkinPersistsThroughDeath()
    {
        using var match = Arena();
        match.Advance(1.2);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.True(!match.Hero.Alive, "he is dead");
        TestAssert.True(Buff(match.Hero, "MonkeyKingPassive") is not null, "Stone Skin persists through death (PersistsThroughDeath)");
        TestAssert.True(Failures(match).Length == 0, "no script failure");
    }
}
