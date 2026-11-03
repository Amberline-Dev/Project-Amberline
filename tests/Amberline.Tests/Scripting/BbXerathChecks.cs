using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 7 (26 October 2026, batch 334): Xerath plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module
/// ever existed). Expected numbers are the rank-one literals of Xerath.inibin and his four spell inibins (cost, cooldown, the effects), the shipped
/// tooltips of fontconfig_en_US (the Locus duration and penetration, the Mage Chains stun, the three casts of Arcane Barrage, Ascended Form's 15 per
/// cent), and the scripts' own delays. The scenario <c>S-xerath</c> shows the same facts on a headless match.
/// Written 26 October 2026. Ability power is zero in every arena unless a check gives it the Needlessly Large Rod (+80), so each damage is its base
/// amount after the foe's 30 magic resistance.
/// </summary>
public static class BbXerathChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>A match with Xerath at rank one, the enemy 300 away on his row, every foe with 30 magic resistance and no ability power.</summary>
    private static Match Arena(string foe = "Orianna")
    {
        var match = new Match("Xerath", foeChampion: foe);
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    /// <summary>The far end of the Arcanopulse line from the caster's side; the foe stands on the same row, so the beam passes through him.</summary>
    private static readonly Position2 Row = new(8200, 7000);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Xerath");
        foreach (var name in new[] { "CharScriptXerath", "XerathArcanopulse", "XerathLocusOfPower", "XerathMageChains", "XerathArcaneBarrageWrapper",
                     "XerathAscended", "XerathMageChainsRoot", "XerathEnergize" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Xerath");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(46, closure.ScriptNames.Count(), "the closure is 46 scripts (his own, the four spells and their extended and wrapper forms, the Mage Chains root, the system buffs)");

        var hero = new Entity { ChampionName = "Xerath" };
        TestAssert.Equal(["XerathArcanopulse", "XerathLocusOfPower", "XerathMageChains", "XerathArcaneBarrageWrapper"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Xerath.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Xerath")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 6300, Id: 101, RetailBot: false }, "playable as a mid for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Xerath"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Arcanopulse (Q): a delayed line through every unit on it
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-arcanopulse-hits-the-line-after-its-delay")]
    public static void ArcanopulseHitsTheLineAfterItsDelay()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: Row), "Q casts down the row through the foe");
        TestAssert.Near(65.0, mana - match.Hero.Mana, 1.0, "65 mana at rank one");
        TestAssert.Near(7.0, match.Hero.SpellCooldowns[0], 0.2, "7 seconds of cooldown");
        match.Advance(0.25);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands in the first quarter second (the beam appears after its delay)");
        match.Advance(0.75);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the line hits the foe once within a second");
        TestAssert.Near(Mitigated(75.0, 30.0), hits[0].Dealt, 0.05, "75 magic after 30 resistance");
        match.Advance(3.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "and the line does not hit him again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 336: the Arcanopulse test cubes (BBSpawnMinion TestCubeRender10Vision, Placemarker False; the record copies the melee minion's
    /// ExpGivenOnDeath 77) expire one and two seconds after the cast and pay nobody. Retail's death experience is the unit's own ScriptExpGiven
    /// within its ExpGiveRadius (obj_AI_Base, zero from the constructor), which only obj_Barracks fills from the level script's spawn info; it
    /// gave the foe two levels within half a second of each cast.
    /// </summary>
    [Check("scripting.bb-xerath-arcanopulse-cubes-pay-no-experience-when-they-expire")]
    public static void ArcanopulseCubesPayNoExperience()
    {
        using var match = Arena();
        var experience = match.Foe.Experience;
        TestAssert.True(match.Cast(0, at: Row), "Q casts down the row through the foe");
        match.Advance(0.25);
        var cubes = match.Runtime.World.Entities.Values.Where(unit => unit.Name == "TestCubeRender10Vision").ToArray();
        TestAssert.True(cubes.Length > 0, "the cast spawns its test cubes");
        TestAssert.True(cubes.All(cube => !cube.Placemarker), "they are not placemarkers");
        match.Advance(3.0);
        TestAssert.True(cubes.All(cube => !cube.Alive), "the cubes expire");
        TestAssert.Near(experience, match.Foe.Experience, 1e-9, "their deaths pay the foe beside them no experience");
        TestAssert.Equal(1, match.Foe.Level, "the foe is still level one");
    }

    [Check("scripting.bb-xerath-arcanopulse-adds-sixty-percent-of-ability-power")]
    public static void ArcanopulseScalesWithAbilityPower()
    {
        using var match = Arena();
        match.GiveAbilityPower(); // the Needlessly Large Rod: 80 ability power
        TestAssert.True(match.Cast(0, at: Row), "Q casts");
        match.Advance(1.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the line hits the foe");
        TestAssert.Near(Mitigated(75.0 + 0.6 * 80.0, 30.0), hits[0].Dealt, 0.05, "75 plus 0.6 of 80 ability power, after 30 resistance");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Locus of Power (W): a toggle that roots him, adds magic penetration and lasts eight seconds
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-locus-of-power-roots-adds-penetration-and-ends-after-eight-seconds")]
    public static void LocusOfPowerRootsAndAddsPenetration()
    {
        using var match = Arena();
        var hero = match.Hero;
        var mana = hero.Mana;
        var penetration = hero.MagicPenetrationPercent;
        TestAssert.True(match.Cast(1, hero), "W casts on himself");
        TestAssert.Near(0.0, mana - hero.Mana, 1.0, "Locus of Power costs no mana (the regeneration aside)");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "XerathLocusOfPower") is not null, "the toggle is on; buffs: " + Names(hero));
        TestAssert.Near(0.10, hero.MagicPenetrationPercent - penetration, 0.001, "10 per cent magic penetration at rank one");
        TestAssert.False(CharacterState.Of(hero).CanMove, "he is immobilised while the toggle is on");
        TestAssert.True(CharacterState.Of(hero).CanCast, "and can still cast");
        match.Advance(7.0);
        TestAssert.True(Buff(hero, "XerathLocusOfPower") is not null, "still on after seven and a half seconds");
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "XerathLocusOfPower") is null, "off after its eight seconds");
        TestAssert.Near(0.0, hero.MagicPenetrationPercent - penetration, 0.001, "and the penetration is gone");
        TestAssert.True(CharacterState.Of(hero).CanMove, "he can move again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Mage Chains (E): a bolt that marks; his next damaging spell on the mark stuns for 1.5 seconds
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-mage-chains-marks-and-the-next-damaging-spell-stuns")]
    public static void MageChainsMarksThenStuns()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, match.Foe), "E casts on the foe");
        // Batch 346 (batch 340's rule): the cast frame pays and starts the cooldown (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        match.Advance(match.CastFrameOf(2));
        TestAssert.Near(70.0, mana - match.Hero.Mana, 1.0, "70 mana at rank one (the cast frame takes it)");
        TestAssert.Near(12.0, match.Hero.SpellCooldowns[2], 0.2, "12 seconds of cooldown");
        match.Advance(1.0 - match.CastFrameOf(2));
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the bolt hits the foe once");
        TestAssert.Near(Mitigated(70.0, 30.0), hits[0].Dealt, 0.05, "70 magic after 30 resistance");
        TestAssert.True(Buff(match.Foe, "XerathMageChains") is not null, "the foe carries the mark; buffs: " + Names(match.Foe));
        TestAssert.True(Buff(match.Foe, "XerathMageChainsRoot") is null, "the mark alone does not stun");

        TestAssert.True(match.Cast(0, at: Row), "Q casts on the marked foe");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "XerathMageChainsRoot") is not null, "his next damaging spell stuns the foe; buffs: " + Names(match.Foe));
        TestAssert.True(Buff(match.Foe, "XerathMageChains") is null, "and spends the mark");
        TestAssert.False(CharacterState.Of(match.Foe).CanMove, "the stun holds him in place");
        match.Advance(1.6);
        TestAssert.True(Buff(match.Foe, "XerathMageChainsRoot") is null, "the stun is over after its 1.5 seconds");
        TestAssert.True(CharacterState.Of(match.Foe).CanMove, "and he moves again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Arcane Barrage (R): three blasts within twelve seconds, then the cooldown
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-arcane-barrage-goes-off-three-times-then-cools-down")]
    public static void ArcaneBarrageThreeCastsThenCooldown()
    {
        using var match = Arena();
        var at = match.Foe.Position;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, at: at), "the first blast casts");
        TestAssert.Near(150.0, mana - match.Hero.Mana, 1.0, "150 mana at rank one");
        match.Advance(1.5);
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[3], 0.01, "no cooldown after the first blast (two charges remain)");
        mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, at: at), "the second blast casts");
        TestAssert.Near(0.0, mana - match.Hero.Mana, 1.0, "the second blast costs no mana");
        match.Advance(1.5);
        TestAssert.True(match.Cast(3, at: at), "the third blast casts");
        match.Advance(1.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(3, hits.Count, "each blast hits the foe once");
        foreach (var hit in hits)
            TestAssert.Near(Mitigated(125.0, 30.0), hit.Dealt, 0.05, "125 magic after 30 resistance per blast");
        TestAssert.Near(78.5, match.Hero.SpellCooldowns[3], 0.2, "the cooldown of 80 started at the third blast (78.5 left 1.5 seconds later)");
        TestAssert.False(match.Cast(3, at: at), "a fourth blast is refused on cooldown");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Ascended Form (passive) and death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-ascended-form-converts-fifteen-percent-of-ability-power-to-armour")]
    public static void AscendedFormConvertsAbilityPowerToArmour()
    {
        using var match = Arena();
        var armour = match.Hero.Armor;
        TestAssert.True(Buff(match.Hero, "XerathAscended") is not null, "the passive is on from the spawn");
        match.GiveAbilityPower(); // 80 ability power
        match.Advance(0.5);
        TestAssert.Near(12.0, match.Hero.Armor - armour, 0.01, "15 per cent of 80 ability power as armour");
    }

    [Check("scripting.bb-xerath-ascended-form-survives-a-death-and-respawn")]
    public static void AscendedFormSurvivesADeathAndRespawn()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        TestAssert.True(!hero.Alive, "Xerath is dead");
        TestAssert.True(Buff(hero, "XerathAscended") is not null, "the passive persists through the death");
        match.Runtime.Combat.ScheduleRespawn(hero);
        for (var step = 0; step < 240 && !hero.Alive; step++)
            match.Advance(0.5);
        TestAssert.True(hero.Alive, "he respawns");
        TestAssert.True(Buff(hero, "XerathAscended") is not null, "and the passive is on again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 583 (the checklist's question 3, the width at the beam's edge): XerathArcanopulseDamage hits BBForEachUnitInTargetRectangle around a
    /// point 500 ahead of Xerath, HalfWidth 95 and HalfLength 500; the inibins' LineWidth is not the hit area. A unit counts while
    /// its body reaches the box: the client's box-intersection test compares each axis with the unit's radius,
    /// as BbSpellTargeting.InsideBox does. So the edge is 95 plus the target's radius from the line.
    /// </summary>
    [Check("scripting.bb-xerath-arcanopulse-edge-is-ninety-five-plus-the-targets-radius")]
    public static void ArcanopulseEdgeIsNinetyFivePlusTheRadius()
    {
        foreach (var (margin, hit) in new[] { (-5.0, true), (5.0, false) })
        {
            using var match = Arena();
            var across = 95.0 + match.Foe.CombatRadius + margin;
            match.Foe.Position = new Position2(7500, 7000 + across);
            TestAssert.True(match.Cast(0, at: Row), "Q casts down the row");
            match.Advance(1.0);
            TestAssert.Equal(hit ? 1 : 0, HitsOn(match, "Foe").Count,
                $"a foe {across:0.0} from the line (radius {match.Foe.CombatRadius:0.0}) is {(hit ? "hit" : "missed")}");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
        }
    }

    /// <summary>
    /// Batch 583 (question 5): XerathMageChains adds its mark (BUFF_CombatDehancer) for Duration 4.0 with CanMitigateDuration false,
    /// Effect3 4 at every rank; the wiki lists no change to it up to V1.0.0.126 (Xerath's release patch).
    /// </summary>
    [Check("scripting.bb-xerath-mage-chains-mark-lasts-four-seconds")]
    public static void MageChainsMarkLastsFourSeconds()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(2, match.Foe), "E casts on the foe");
        var landed = 0.0;
        for (var elapsed = 0.0; elapsed < 2.0 && landed == 0.0; elapsed += Step)
        {
            match.Advance(Step);
            if (Buff(match.Foe, "XerathMageChains") is not null) landed = match.Now;
        }
        TestAssert.True(landed > 0.0, "the bolt lands and marks the foe");
        match.Advance(3.85);
        TestAssert.True(Buff(match.Foe, "XerathMageChains") is not null, "the mark is still on 3.85 s after it landed");
        match.Advance(0.3);
        TestAssert.True(Buff(match.Foe, "XerathMageChains") is null, "and gone at 4.15 s");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Harness sanity: the foe in the arena gains only its regeneration when nothing hits it
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-xerath-arena-foe-regenerates-without-a-hit")]
    public static void IdleFoeGainsOnlyItsRegeneration()
    {
        using var match = Arena();
        var before = match.Foe.Health;
        match.Advance(10.0);
        TestAssert.True(match.Foe.Health - before < 25.0, $"an idle foe gains its regeneration only (gained {match.Foe.Health - before:0.00})");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "and nothing hit him");
    }
}
