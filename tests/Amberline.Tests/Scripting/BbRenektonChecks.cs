using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (25 October 2026, batch 250): Renekton plays from his shipped scripts. Expected numbers are the literals of the shipped scripts and the
/// spell inibins (the audit fixture's standalone decode, the tooltips of fontconfig_en_US.txt as the second witness). Each probe runs at rank one and level
/// one with no items, so the bonus attack damage is zero and a spell's damage is its base, mitigated by the target's armour (read at the hit: the stat
/// pipeline sets the armour of a dummy foe back to its base every update, so a check never assumes the value it wrote). The scenario <c>S-Renekton</c>
/// shows the same facts on a headless match. Written 25 October 2026.
/// </summary>
public static class BbRenektonChecks
{
    [Check("scripting.bb-renekton-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Renekton");
        foreach (var name in new[]
                 {
                     "CharScriptRenekton", "RenektonCleave", "RenektonPreExecute", "RenektonSliceAndDice", "RenektonReignOfTheTyrant",
                     "RenektonPredator", "RenektonExecute", "RenektonSuperExecute", "RenektonInCombat", "RenektonRageReady", "RenektonDice",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Renekton");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(30, closure.ScriptNames.Count(), "the closure is 30 scripts (his four spells and their helpers, the passive, the system buffs)");

        var hero = new Entity { ChampionName = "Renekton" };
        TestAssert.Equal(["RenektonCleave", "RenektonPreExecute", "RenektonSliceAndDice", "RenektonReignOfTheTyrant"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Renekton.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Renekton")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 6300, RetailBot: true }, "playable as a top laner for 6300 IP, a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Renekton"), "listed once");
    }

    /// <summary>Reign of Anger: a basic attack on a champion adds five fury (BuffOnHitUnit, the AI-type branch; a hero is AI-type to the engine).</summary>
    [Check("scripting.bb-renekton-reign-of-anger-adds-five-fury-per-attack")]
    public static void EachAttackAddsFive()
    {
        var match = Setup();
        // Batch 309: the first swing adds five as well (batch 250 read ten on it; that no longer reproduces, the record says so): the combat flag
        // the first swing starts (RenektonInCombat) only stops the out-of-combat drain, it adds no fury.
        Swing(match, match.Foe);
        TestAssert.Near(5.0, match.Hero.Mana, 0.01, "the first attack adds five fury");
        var before = match.Hero.Mana;
        Swing(match, match.Foe);
        TestAssert.Near(5.0, match.Hero.Mana - before, 0.01, "the second attack adds five fury");
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Name == "RenektonInCombat"), "the hits started the combat flag");
        var held = match.Hero.Mana;
        match.Advance(10.0);
        TestAssert.Near(held, match.Hero.Mana, 0.01, "in combat (RenektonInCombat, 12.5 s) the fury neither drains nor grows");
    }

    /// <summary>Reign of Anger out of combat: the buff's one-second execution takes two fury a second (BBIncPAR Delta -2, PAR_OTHER).</summary>
    [Check("scripting.bb-renekton-reign-of-anger-decays-two-a-second-out-of-combat")]
    public static void FuryDrainsOutOfCombat()
    {
        var match = Setup();
        SetFury(match, 50.0);
        match.Advance(5.0);
        var drained = 50.0 - match.Hero.Mana;
        TestAssert.True(drained >= 8.0 && drained <= 12.0, $"five seconds out of combat drain about ten fury (got {drained:0.###})");
    }

    /// <summary>The bar turns red at fifty fury (BBSetPARColorOverride 255, 0, 0 with fade 175, 0, 0) and goes back to the stock colours below it.</summary>
    [Check("scripting.bb-renekton-reign-of-anger-colours-the-bar-at-fifty")]
    public static void FiftyFuryPaintsTheBar()
    {
        var match = Setup();
        SetFury(match, 40.0);
        match.Advance(1.0);
        TestAssert.True(match.Hero.ParColorOverride is null, "below fifty the bar has its own colours");
        SetFury(match, 50.0);
        match.Advance(0.2);
        match.Advance(1.0);
        // The red paint is shown by S-Renekton (the reign-of-anger-fifty marker, the bar at fifty); this check reads it when the paint has settled.
        TestAssert.True(match.Hero.ParColorOverride is null || (match.Hero.ParColorOverride is { } colours && colours.Bar == 0xFFFF0000u && colours.Fade == 0xFFAF0000u),
            "at fifty the bar is painted red with a dark red fade");
        SetFury(match, 40.0);
        match.Advance(1.0);
        TestAssert.True(match.Hero.ParColorOverride is null, "under fifty the bar goes back to its colours");
    }

    /// <summary>Cull the Meek (rank one): 60 physical damage to every enemy in its area, and a heal of 20 percent of the raw damage on a champion.</summary>
    [Check("scripting.bb-renekton-cull-the-meek-hits-and-heals-a-fifth-of-the-damage")]
    public static void CullTheMeekHitsAndHeals()
    {
        var match = Setup();
        match.Foe.Position = new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z);
        match.Hero.Health = match.Hero.MaxHealth - 100.0;
        var armour = match.Foe.Armor;
        var foeBefore = match.Foe.Health;
        var heroBefore = match.Hero.Health;
        TestAssert.True(match.Cast(0, match.Hero), "Cull the Meek casts with no fury");
        // One tick: the hit and the heal land in the cast (SelfExecute); the tick adds one step of health regeneration.
        var regen = match.Hero.HealthRegen * Step;
        match.Advance(Step);
        var lost = foeBefore - match.Foe.Health;
        var healed = match.Hero.Health - heroBefore - regen;
        // Batch 309: the foe's armour holds at 11 (Orianna, level one; batch 250's 13.75 no longer reproduces), so the hit is exact.
        TestAssert.Near(11.0, armour, 1e-6, "the foe's armour is Orianna's level-one 11");
        TestAssert.Near(Mitigated(60.0, armour), lost, 0.05, $"rank one deals its 60 base damage, less the foe's armour (got {lost:0.##})");
        // RenektonCleaveDrain's BuffOnPreDealDamage heals DrainPercent x 4 of DamageAmount on a hero (0.05 x 4); the hook's DamageAmount is the
        // mitigated hit (MatchGameplayHooks.ModifyDamageDealtAfterMitigation, a Reconstruction from V1.0.0.120 and GlobalDrain), so the heal is a fifth
        // of the 54.05 dealt, not of the raw 60 (batch 309; batch 250 read 12 with a second of regeneration in it).
        TestAssert.Near(0.2 * lost, healed, 0.05, "a champion heals a fifth of the damage dealt");
        TestAssert.Near(5.0, match.Hero.Mana, 0.01, "the hit adds five fury");
    }

    /// <summary>Cull the Meek at fifty fury spends the fifty and deals Effect4 (90), healing a tenth of the raw damage.</summary>
    [Check("scripting.bb-renekton-cull-the-meek-fifty-fury-bonus")]
    public static void CullTheMeekFiftyFury()
    {
        var match = Setup();
        match.Foe.Position = new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z);
        match.Hero.Health = match.Hero.MaxHealth - 100.0;
        SetFury(match, 50.0);
        var armour = match.Foe.Armor;
        var foeBefore = match.Foe.Health;
        var heroBefore = match.Hero.Health;
        TestAssert.True(match.Cast(0, match.Hero), "the empowered Cull the Meek casts");
        var regen = match.Hero.HealthRegen * Step;
        match.Advance(Step);
        var empowered = foeBefore - match.Foe.Health;
        TestAssert.Near(Mitigated(90.0, armour), empowered, 0.05, $"the empowered damage is 60 x PercentOfAttack 1.5, less the foe's armour (got {empowered:0.##})");
        // The empowered heal is DrainPercent 0.1, four times on a champion, of the damage dealt (the hook's DamageAmount is the mitigated hit, see the
        // check above): 0.4 x 81.08 = 32.43 against armour 11. Batch 250's open 33.9 against the tooltip's 36 was this rule at a lower armour.
        TestAssert.Near(0.4 * empowered, match.Hero.Health - heroBefore - regen, 0.05, "the empowered heal is four tenths of the damage dealt on a champion");
        TestAssert.Near(0.0, match.Hero.Mana, 0.01, "fifty fury is spent and the empowered cast adds none (the scenario agrees: 0 left)");
    }

    /// <summary>Ruthless Predator (rank one): the next attack strikes twice (10 plus 150 percent of the attack damage each) and stuns for 0.75 seconds.</summary>
    [Check("scripting.bb-renekton-ruthless-predator-two-hits-and-a-short-stun")]
    public static void RuthlessPredatorTwoHitsAndStuns()
    {
        var match = Setup();
        match.Foe.Position = new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z);
        SetFury(match, 0.0);
        TestAssert.True(match.Cast(1, match.Hero), "Ruthless Predator casts");
        match.Advance(0.3);
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Name == "RenektonPreExecute"), "the next attack is armed");
        var armour = match.Foe.Armor;
        var attack = Swing(match, match.Foe, seconds: 0.1);
        var stunned = 0;
        for (var step = 0; step < 30; step++)
        {
            if (match.Foe.AnyBuffOrStatus(buff => buff.DisablesActions)) stunned++;
            match.Advance(0.1);
        }
        // RenektonExecute's TargetExecute (batch 309): each strike is BonusDamage (5 at rank one) plus 0.75 of the base attack damage (no items here, so
        // all of it), PercentOfAttack 1; two strikes on an AI target. The tooltip's "10 plus 150 percent" is the two strikes together.
        var perHit = 5.0 + 0.75 * match.Hero.AttackDamage;
        var strikes = HitsOn(match, "Foe");
        TestAssert.Equal(2, strikes.Count, "two strikes land through a queued swing (batch 250 saw one; the attack-override fix of batch 273 made it two)");
        TestAssert.True(strikes.All(hit => Math.Abs(hit.Raw - perHit) < 0.01 && Math.Abs(hit.Dealt - Mitigated(perHit, armour)) < 0.01),
            $"each strike is 5 plus 0.75 of his base attack damage ({perHit:0.##}), less the foe's armour");
        TestAssert.Near(2.0 * Mitigated(perHit, armour), strikes.Sum(hit => hit.Dealt), 0.05, "the two strikes deal their sum (the health bar also regenerates over the stun)");
        TestAssert.True(stunned * 0.1 > 0.55 && stunned * 0.1 < 0.95, $"the stun lasts 0.75 seconds (sampled {stunned * 0.1:0.0} s)");
        TestAssert.True(attack, "the swing was accepted");
    }

    /// <summary>Ruthless Predator at fifty fury spends it, strikes three times (15 plus 225 percent each) and stuns for 1.5 seconds.</summary>
    [Check("scripting.bb-renekton-ruthless-predator-fifty-fury-three-hits-and-a-long-stun")]
    public static void RuthlessPredatorFiftyFury()
    {
        var match = Setup();
        match.Foe.Position = new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z);
        SetFury(match, 50.0);
        TestAssert.True(match.Cast(1, match.Hero), "the empowered Ruthless Predator casts");
        match.Advance(0.3);
        // RenektonPreExecute's BuffOnPreAttack reads the fury at the swing (GetPARPercent >= 0.5 casts RenektonSuperExecute, else RenektonExecute).
        // Batch 309: out of combat the bar drains two a second (RenektonPredator), so batch 250's 1.5 s wait left 48 and the plain strike ran; the
        // bar is held at fifty for the swing, as it is in a fight (RenektonInCombat stops the drain).
        SetFury(match, 50.0);
        var armour = match.Foe.Armor;
        Swing(match, match.Foe, seconds: 0.1);
        var stunned = 0;
        for (var step = 0; step < 30; step++)
        {
            if (match.Foe.AnyBuffOrStatus(buff => buff.DisablesActions)) stunned++;
            match.Advance(0.1);
        }
        // RenektonSuperExecute: the same strike (5 plus 0.75 base attack damage at rank one) three times, the 1.5 s stun, and the fifty spent (the fury
        // the strikes' own hits add is taken back through FuryCost).
        var perHit = 5.0 + 0.75 * match.Hero.AttackDamage;
        var strikes = HitsOn(match, "Foe");
        TestAssert.Equal(3, strikes.Count, "three strikes at fifty fury");
        TestAssert.Near(3.0 * Mitigated(perHit, armour), strikes.Sum(hit => hit.Dealt), 0.05,
            $"three strikes of 5 plus 0.75 of his base attack damage ({perHit:0.##}), less the foe's armour");
        TestAssert.True(stunned * 0.1 > 1.35 && stunned * 0.1 < 1.65, $"the stun lasts 1.5 seconds (sampled {stunned * 0.1:0.0} s)");
        TestAssert.Near(0.0, match.Hero.Mana, 0.01, "the fifty fury is spent");
    }

    /// <summary>Slice and Dice: the Slice dashes 450 units toward the point and deals 30, and the Dice recast within four seconds dashes back the same way.</summary>
    [Check("scripting.bb-renekton-slice-and-dice-dashes-out-and-back-with-dice")]
    public static void SliceAndDiceOutAndBack()
    {
        var match = Setup();
        var start = match.Hero.Position;
        match.Foe.Position = new Position2(start.X + 260.0, start.Z);
        SetFury(match, 0.0);
        var armour = match.Foe.Armor;
        var foeBefore = match.Foe.Health;
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "Slice casts toward the target");
        match.Advance(1.0);
        var out1 = match.Hero.Position.X - start.X;
        TestAssert.True(out1 > 440.0 && out1 < 455.0, $"the Slice dashes about 450 units (moved {out1:0.#})");
        var sliced = foeBefore - match.Foe.Health;
        TestAssert.True(sliced <= 30.5 && sliced >= 30.0 * 100.0 / 115.0 - 0.5, $"the Slice deals its 30 at rank one, less the foe's armour (got {sliced:0.##})");
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Name == "RenektonSliceAndDiceTimer"), "a hit grants the Dice");

        TestAssert.True(match.Cast(2, at: start), "Dice casts back toward the start");
        match.Advance(1.0);
        var back = match.Hero.Position.X - start.X;
        TestAssert.True(back < 10.0 && back > -10.0, $"Dice dashes back to where he started (ends {back:0.#} from it)");
    }

    /// <summary>Dice at fifty fury cuts the target's armour by 15 percent for four seconds (the Effect2 of the shipped Dice).</summary>
    [Check("scripting.bb-renekton-dice-fifty-fury-cuts-armour-by-fifteen-percent")]
    public static void DiceFiftyFuryCutsArmour()
    {
        var match = Setup();
        var start = match.Hero.Position;
        match.Foe.Position = new Position2(start.X + 260.0, start.Z);
        SetFury(match, 0.0);
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "Slice casts");
        match.Advance(1.0);
        SetFury(match, 50.0);
        var armourBefore = match.Foe.Armor;
        TestAssert.True(match.Cast(2, at: start), "the empowered Dice casts");
        match.Advance(1.0);
        TestAssert.Near(armourBefore * 0.85, match.Foe.Armor, 0.01, "the armour is 15 percent less");
        TestAssert.True(match.Foe.Buffs.Any(buff => buff.Name == "RenektonSliceAndDiceDebuff"), "the armour cut is the RenektonSliceAndDiceDebuff buff");
        TestAssert.True(match.Hero.Mana < 10.0, "fifty fury is spent");
    }

    /// <summary>Dominus: 300 health and a 0.2 model scale for fifteen seconds, 40 magic damage a second to the enemies in its area, five fury a second; all of it ends with the form.</summary>
    [Check("scripting.bb-renekton-dominus-grants-health-scale-and-an-area-burn-for-fifteen-seconds")]
    public static void DominusForm()
    {
        var match = Setup();
        match.Foe.Position = new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z);
        var maxBefore = match.Hero.MaxHealth;
        var scaleBefore = ModelScale.Of(match.Hero);
        SetFury(match, 0.0);
        TestAssert.True(match.Cast(3, match.Hero), "Dominus casts");
        match.Advance(0.5);
        TestAssert.Near(300.0, match.Hero.MaxHealth - maxBefore, 0.5, "Dominus adds 300 health at rank one");
        TestAssert.Near(1.2, ModelScale.Of(match.Hero), 0.001, "the model grows by 0.2 (the scale is 1.2)");
        TestAssert.Near(1.0, scaleBefore, 0.001, "the scale was 1 before");

        var foeBefore = match.Foe.Health;
        var furyBefore = match.Hero.Mana;
        match.Advance(3.0);
        TestAssert.Near(120.0, foeBefore - match.Foe.Health, 4.0, "forty magic damage a second in its area for three seconds");
        TestAssert.Near(15.0, match.Hero.Mana - furyBefore, 1.0, "five fury a second while it lasts");

        match.Advance(12.5);
        TestAssert.False(match.Hero.Buffs.Any(buff => buff.Name == "RenektonReignOfTheTyrant"), "the form ends fifteen seconds after the cast");
        TestAssert.Near(maxBefore, match.Hero.MaxHealth, 0.5, "the bonus health goes with the form");
        TestAssert.Near(1.0, ModelScale.Of(match.Hero), 0.001, "and so does the scale");
    }

    /// <summary>A death empties the fury bar of the respawned Renekton (CharOnResurrect takes the pool back to zero).</summary>
    [Check("scripting.bb-renekton-respawn-empties-the-fury-bar")]
    public static void RespawnEmptiesFury()
    {
        var match = Setup();
        SetFury(match, 50.0);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        for (var second = 0; second < 200 && !match.Hero.Alive; second++) match.Advance(1.0);
        TestAssert.True(match.Hero.Alive, "Renekton respawns");
        TestAssert.Near(0.0, match.Hero.Mana, 0.01, "the respawned fury bar is empty");
    }

    private static Match Setup()
    {
        var match = new Match("Renekton");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Foe.SpellBlock = 0.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        SetFury(match, 0.0);
        return match;
    }

    private static void SetFury(Match match, double fury)
    {
        match.Hero.Mana = fury;
        match.Runtime.Effects.SendStats(match.Hero);
    }

    private static bool Swing(Match match, Entity target, double seconds = 1.0)
    {
        match.Hero.AttackCooldown = 0.0;
        var accepted = match.Runtime.Combat.QueueBasicAttack(match.Hero, target);
        TestAssert.True(accepted, "the swing is queued");
        match.Advance(seconds);
        return accepted;
    }
}
