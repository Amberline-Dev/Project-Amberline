using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 7 (25 October 2026, batch 333): Lux plays from her shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). Expected numbers are the rank-one literals of Lux.inibin and her four spell inibins (cost, cooldown, the effects), the shipped
/// tooltips of fontconfig_en_US (the two-second binding, the half effect on the second unit, the zone's slow, the passive's six seconds and its
/// bonus) and the shipped scripts' constants; the scenario <c>S-lux</c> shows the same facts on a headless match. Written 25 October 2026. Ability
/// power is zero in every arena, so each damage is its base amount after the foe's resistance (30 magic resistance).
/// </summary>
public static class BbLuxChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Lux at rank one, the enemy 300 away (the ally between them), every foe with 30 magic resistance and no armour.</summary>
    private static Match Arena()
    {
        var match = new Match("Lux");
        match.Learn(1);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-lux-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Lux");
        foreach (var name in new[] { "CharScriptLux", "LuxLightBinding", "LuxPrismaticWave", "LuxLightStrikeKugel", "LuxMaliceCannon", "LuxIlluminationPassive",
                     "LuxIlluminatingFraulein", "LuxLightBindingMis", "LuxPrismaticWaveShield", "LuxPrismaticWaveShieldSelf", "LuxMaliceCannonBeam", "LuxMaliceCannonBall" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Lux");
        TestAssert.Equal(33, closure.ScriptNames.Count(), "the closure is 33 scripts (her own, the four spells and their missiles, dummies and cubes, the passive and its mark, the system buffs)");

        var hero = new Entity { ChampionName = "Lux" };
        TestAssert.Equal(["LuxLightBinding", "LuxPrismaticWave", "LuxLightStrikeKugel", "LuxMaliceCannon"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Lux.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Lux")!;
        TestAssert.True(info is { Playable: true, Role: "support", DefaultIp: 3150, Id: 99, RetailBot: false }, "playable as a support for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Lux"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Light Binding (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-lux-light-binding-costs-60-mana-and-cools-for-15")]
    public static void LightBindingCostsAndCools()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts on the foe's spot");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(60.0, mana - match.Hero.Mana, 1.0, "60 mana at rank one");
        TestAssert.Near(15.0, match.Hero.SpellCooldowns[0], 0.5, "a 15 second cooldown at rank one (the inibin, not the script's AutoCooldownByLevel)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-lux-light-binding-hits-the-first-unit-for-60-and-the-second-for-half")]
    public static void LightBindingHitsTwoUnitsOnTheLine()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(7340, 7000); // behind the foe on the line of the ball
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts");
        match.Advance(1.5);
        var first = HitsOn(match, "Foe");
        var second = HitsOn(match, "FarFoe");
        TestAssert.Equal(1, first.Count, "one hit on the first unit");
        TestAssert.Near(Mitigated(60.0, 30.0), first[0].Dealt, 0.05, "60 magic after 30 resistance on the first unit");
        TestAssert.Equal(1, second.Count, "the second unit on the line is struck");
        TestAssert.Near(Mitigated(30.0, 30.0), second[0].Dealt, 0.05, "half of the effect on the second unit (the tooltip's 50 percent)");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "the ball does not hurt an ally it passes");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-lux-light-binding-roots-the-first-unit-for-two-seconds")]
    public static void LightBindingRootsForTwoSeconds()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts");
        match.Advance(1.2); // the ball lands about half a second after the cast
        TestAssert.False(CharacterState.Of(match.Foe).CanMove, "bound on landing (SetCanMove off)");
        match.Advance(1.0); // 2.2 seconds
        TestAssert.False(CharacterState.Of(match.Foe).CanMove, "still bound two seconds after the landing");
        match.Advance(0.8); // 3.0 seconds
        TestAssert.True(CharacterState.Of(match.Foe).CanMove, "free again after the two seconds (SetCanMove on at the buff's end)");
    }

    [Check("scripting.bb-lux-illumination-mark-lasts-six-seconds")]
    public static void IlluminationMarkLastsSixSeconds()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts and charges the foe");
        match.Advance(0.8);
        TestAssert.True(Buff(match.Foe, "LuxIlluminatingFraulein") is not null, "the foe carries the charge");
        match.Advance(4.7);
        TestAssert.True(Buff(match.Foe, "LuxIlluminatingFraulein") is not null, "still charged at about five and a half seconds");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "LuxIlluminatingFraulein") is null, "the charge is gone after its six seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Prismatic Barrier (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-lux-prismatic-barrier-costs-60-and-shields-herself-and-the-ally-on-its-path")]
    public static void PrismaticBarrierShieldsHerselfAndTheAllyOnTheWand()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "W casts toward the foe");
        // Batch 340: the cast frame pays, not the press.
        match.Advance(match.CastFrameOf(1));
        TestAssert.Near(60.0, mana - match.Hero.Mana, 1.0, "60 mana at rank one");
        TestAssert.Near(14.0, match.Hero.SpellCooldowns[1], 0.5, "a 14 second cooldown at rank one");
        match.Advance(1.5); // the wand flies out past the ally and comes back to Lux
        TestAssert.True(Buff(match.Hero, "LuxPrismaticWaveShieldSelf") is not null, "Lux carries her own shield");
        TestAssert.True(Buff(match.Ally, "LuxPrismaticWaveShield") is not null, "the ally the wand touches carries the shield");
        TestAssert.True(Buff(match.Foe, "LuxPrismaticWaveShield") is null, "the enemy the wand flies to does not");
        // The wand's buffs are added with Duration 3.0 (LuxPrismaticWave.luaobj and LuxPrismaticWaveMissile.luaobj); the probe of batch 333 saw both gone at 5.5 s.
        match.Advance(4.0);
        TestAssert.True(Buff(match.Hero, "LuxPrismaticWaveShieldSelf") is null && Buff(match.Ally, "LuxPrismaticWaveShield") is null,
            "both shields are gone after their three seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Lucent Singularity (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-lux-lucent-singularity-costs-70-mana-and-slows-by-twenty-percent")]
    public static void LucentSingularitySlowsItsArea()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        var speed = match.Foe.Speed;
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "E casts on the foe's spot");
        // Batch 340: the cast frame pays, not the press.
        match.Advance(match.CastFrameOf(2));
        TestAssert.Near(70.0, mana - match.Hero.Mana, 1.0, "70 mana at rank one");
        match.Advance(1.0);
        // The zone's creation frees the slot for the detonation (the script's BBSetSlotSpellCooldownTimeVer2 with the cooldown of
        // LSCooldown, which is the inibin's Effect3 of 12, is written at the detonation and at the zone's end): no cooldown while it is up.
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[2], 1e-6, "the slot shows no cooldown while the zone is up");
        TestAssert.Near(0.8, match.Foe.Speed / speed, 0.01, "the zone slows the enemy in it by 20 percent at rank one");
    }

    [Check("scripting.bb-lux-lucent-singularity-detonation-starts-a-twelve-second-cooldown")]
    public static void LucentSingularityDetonationStartsItsCooldown()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "E casts");
        match.Advance(1.0);
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "the second press is accepted");
        TestAssert.Near(12.0, match.Hero.SpellCooldowns[2], 0.5, "the detonation starts a 12 second cooldown (LSCooldown, the inibin's Effect3)");
    }

    [Check("scripting.bb-lux-lucent-singularity-detonates-for-sixty-magic-on-a-second-press")]
    public static void LucentSingularityDetonatesOnTheSecondPress()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "E casts");
        match.Advance(1.0);
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "the second press is accepted");
        match.Advance(0.8);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one detonation on the enemy in the zone");
        TestAssert.Near(Mitigated(60.0, 30.0), hits[0].Dealt, 0.05, "60 magic after 30 resistance at rank one");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Final Spark (R)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-lux-final-spark-hits-every-enemy-in-its-line-for-300-magic")]
    public static void FinalSparkHitsEveryEnemyOnTheLine()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(9000, 7000); // on the line through the foe
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, at: match.Foe.Position), "R casts");
        TestAssert.Near(100.0, mana - match.Hero.Mana, 1.0, "100 mana at rank one");
        TestAssert.Near(80.0, match.Hero.SpellCooldowns[3], 0.5, "an 80 second cooldown at rank one (the inibin)");
        match.Advance(3.0);
        var foe = HitsOn(match, "Foe");
        var far = HitsOn(match, "FarFoe");
        TestAssert.True(foe.Count >= 1, "the beam hits the enemy in front of Lux");
        TestAssert.Near(Mitigated(300.0, 30.0), foe[0].Dealt, 0.05, "300 magic after 30 resistance at rank one");
        TestAssert.True(far.Count >= 1, "the beam also hits the enemy further along the line");
        TestAssert.Near(Mitigated(300.0, 30.0), far[0].Dealt, 0.05, "the same damage to every enemy in the beam");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Illumination (passive)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-lux-illumination-ignites-for-seventy-at-level-six")]
    public static void IlluminationScalesWithHerLevel()
    {
        using var match = Arena();
        // The level comes from experience, the way a kill or the scenario's Level() gives it, so the level-up hooks run.
        match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience);
        match.Refill();
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts and charges the foe");
        match.Advance(0.8);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe, new AttackRolls(1.0, 1.0, 1.0)), "the swing was accepted");
        match.Advance(2.5);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Dealt - Mitigated(70.0, 30.0)) < 0.05),
            "the ignition deals 10 plus 10 times the level (70 at level six) magic after 30 resistance");
    }

    [Check("scripting.bb-lux-illumination-ignites-the-charge-for-twenty-at-level-one")]
    public static void IlluminationIgnitesOnTheNextAttack()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Q casts and charges the foe");
        match.Advance(0.8);
        TestAssert.True(Buff(match.Foe, "LuxIlluminatingFraulein") is not null, "the foe is charged");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe, new AttackRolls(1.0, 1.0, 1.0)), "the swing was accepted");
        match.Advance(2.0);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Dealt - Mitigated(20.0, 30.0)) < 0.05),
            "the ignition deals 10 plus 10 times the level (20 at level one) magic after 30 resistance");
        TestAssert.True(Buff(match.Foe, "LuxIlluminatingFraulein") is null, "the ignition uses up the charge");
    }
}
