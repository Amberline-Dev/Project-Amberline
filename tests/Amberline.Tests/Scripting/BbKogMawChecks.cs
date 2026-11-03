using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (26 October 2026, batch 326): Kog'Maw plays from his shipped scripts. Expected numbers are the literals of the shipped scripts and
/// spell inibins (decoded by a standalone reader, .artifacts/b326/tools/kogmaw_decode.py); the scenario <c>S-kogmaw</c> shows the same facts on a
/// headless match. Icathian Surprise is his passive: the zombie life it gives runs on the E11 hook of W35 (<c>BuffOnZombie</c>).
/// </summary>
public static class BbKogMawChecks
{
    [Check("scripting.bb-kogmaw-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("KogMaw");
        foreach (var name in new[] { "CharScriptKogMaw", "KogMawCausticSpittle", "KogMawBioArcaneBarrage", "KogMawVoidOoze", "KogMawLivingArtillery",
                     "KogMawIcathianSurprise", "KogMawIcathianSurpriseReady", "KogMawCausticSpittleCharged", "KogMawLivingArtilleryCost" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("KogMaw");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(0, closure.Dangling.Count, "no dangling reference");
        TestAssert.Equal(69, closure.ScriptNames.Count(), "the closure is 69 scripts (audit bb-coverage, batch 326)");

        var hero = new Entity { ChampionName = "KogMaw" };
        TestAssert.Equal(["KogMawCausticSpittle", "KogMawBioArcaneBarrage", "KogMawVoidOoze", "KogMawLivingArtillery"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from KogMaw.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("KogMaw")!;
        TestAssert.True(info is { Playable: true, Role: "adc", DefaultIp: 6300, Id: 96, RetailBot: false }, "playable as an adc for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "KogMaw"), "listed once");
    }

    [Check("scripting.bb-kogmaw-caustic-spittle-damage-by-rank")]
    public static void CausticSpittleDamageByRank()
    {
        // The shipped TargetExecute's BBApplyDamage: magic damage by rank and 0.7 of the ability power, read from the script, not from the engine.
        var hit = Blocks(Script("KogMawCausticSpittle"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal([60.0, 110.0, 160.0, 210.0, 260.0], Table(hit, "DamageByLevel"), "Caustic Spittle damage by rank");
        TestAssert.Equal(0.7, hit.Number("SpellDamageRatio")!.Value, "Caustic Spittle takes 0.7 of the ability power");
    }

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static Match Arena()
    {
        var match = new Match("KogMaw");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Foe.Armor = 0.0;
        match.Refill();
        return match;
    }

    [Check("scripting.bb-kogmaw-icathian-surprise-lives-four-seconds-then-explodes-once")]
    public static void IcathianSurpriseExplodesOnce()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.Advance(1.1); // CharScriptKogMaw's one-second UpdateSelfBuffActions adds KogMawIcathianSurpriseReady to the living hero
        TestAssert.True(Buff(hero, "KogMawIcathianSurpriseReady") is not null, "the Ready buff is on");
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        TestAssert.True(hero.Alive && Buff(hero, "KogMawIcathianSurprise") is not null, "the death starts the zombie life (BuffOnZombie of the Ready buff)");
        match.Advance(3.8);
        TestAssert.True(hero.Alive, "alive for four seconds");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing explodes before the four seconds");
        match.Advance(0.5);
        TestAssert.True(!hero.Alive, "dead after the explosion");
        TestAssert.True(Buff(hero, "KogMawIcathianSurprise") is null, "the zombie buff is gone: " + Names(hero));
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one explosion");
        TestAssert.Near(125.0, hits[0].Dealt, 0.05, "100 + 25 per level, true damage, at level one");
        match.Advance(6.0);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the corpse does not explode again");
        TestAssert.True(Buff(hero, "KogMawIcathianSurprise") is null, "and no second zombie buff: " + Names(hero));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 352: a death into a zombie life keeps the unit's buffs until the life ends. The client's the unit's death routine sets
    /// <c>bZombie</c> instead of <c>bDead</c> and runs no clear; <c>BBForceDead</c>, the life's end, runs <c>RemoveNonPersistantBuffs</c>.
    /// Bio-Arcane Barrage (not persistent, ten seconds) stays through the four-second Icathian Surprise and goes with the explosion.
    /// </summary>
    [Check("scripting.bb-kogmaw-icathian-surprise-keeps-his-buffs-until-the-explosion")]
    public static void IcathianSurpriseKeepsHisBuffs()
    {
        using var match = Arena();
        var hero = match.Hero;
        match.Advance(1.1);
        TestAssert.True(match.Cast(1), "Bio-Arcane Barrage");
        match.Advance(0.3);
        TestAssert.True(Buff(hero, "KogMawBioArcaneBarrage") is not null, "the barrage is on: " + Names(hero));
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        TestAssert.True(hero.Alive && Buff(hero, "KogMawIcathianSurprise") is not null, "the death starts the zombie life");
        TestAssert.True(Buff(hero, "KogMawBioArcaneBarrage") is not null, "the death into the zombie life keeps the barrage: " + Names(hero));
        match.Advance(3.8);
        TestAssert.True(hero.Alive && Buff(hero, "KogMawBioArcaneBarrage") is not null, "kept through the life: " + Names(hero));
        match.Advance(0.5);
        TestAssert.True(!hero.Alive, "dead after the explosion");
        TestAssert.True(Buff(hero, "KogMawBioArcaneBarrage") is null, "BBForceDead's clear takes it: " + Names(hero));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-kogmaw-void-ooze-fires-on-both-branches")]
    public static void VoidOozeFiresOnBothBranches()
    {
        // KogMawVoidOoze's SelfExecute: the end point is the cast point; past 1150 it is moved to 1100 along the facing
        // (BBGetPointByUnitFacingOffset); either way BBSpellCast fires KogMawVoidOozeMissile (extra slot 1) at it.
        using var match = Arena();
        match.Foe.Position = new Position2(7600, 7000);
        // The point as the client sends it (NpcCastSpellReq carries the ground height), the way the scenario S-kogmaw casts.
        bool CastOoze(Position2 point)
        {
            var at = new Position3(point.X, match.Runtime.Navigation.HeightAt(point), point.Z);
            return match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(2, false, at, at, 0), match.Now);
        }
        TestAssert.True(CastOoze(new Position2(7600, 7000)), "near branch: Void Ooze aimed 600 away casts");
        match.Advance(1.5);
        var near = HitsOn(match, "Foe");
        TestAssert.Equal(1, near.Count, "near branch: the missile hits the foe on its line once");
        TestAssert.Near(Mitigated(60.0, 30.0), near[0].Dealt, 0.05, "60 magic at rank one after 30 resistance");
        TestAssert.True(Buff(match.Foe, "KogMawVoidOozeSlow") is not null, "and the trail slows");
        match.Advance(2.0);
        match.Refill();
        TestAssert.True(CastOoze(new Position2(8300, 7000)), "far branch: Void Ooze aimed 1300 away casts");
        match.Advance(1.5);
        TestAssert.Equal(2, HitsOn(match, "Foe").Count, "far branch: the missile (end 1100 out) hits the foe too");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-kogmaw-living-artillery-hits-once-and-costs-forty-more-each-cast")]
    public static void LivingArtilleryDamageAndCost()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, at: match.Foe.Position), "Living Artillery casts on the foe's spot");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(3));
        TestAssert.Near(40.0, mana - match.Hero.Mana, 0.5, "40 mana at the first cast");
        match.Advance(3.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit: the impact (no other damage step)");
        // The impact's champion loop: BaseDamageAmount x 2.5 + BonusDamage (80 x 2.5 = 200 at rank one, no bonus attack damage), magic.
        TestAssert.Near(Mitigated(200.0, 30.0), hits[0].Dealt, 0.05, "200 magic on a champion at rank one after 30 resistance");
        TestAssert.True(Buff(match.Foe, "KogMawLivingArtillerySight") is not null, "the foe is revealed");
        mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, at: match.Foe.Position), "a second cast inside the cost buff's six seconds");
        match.Advance(match.CastFrameOf(3));
        TestAssert.Near(80.0, mana - match.Hero.Mana, 1.0, "costs 40 more (KogMawLivingArtilleryCost), at the cast frame (regeneration allowed)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
