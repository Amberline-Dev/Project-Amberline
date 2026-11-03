using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (batch 299): Galio plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever existed). Expected
/// numbers are the literals of the shipped scripts and spell inibins (rank one): Resolute Smite 80 magic (+0.7 ability power) where its line ends (its aim point),
/// in a 230 area, with a 24 percent slow for 2.5 seconds; Bulwark 30 armor and magic resist on an ally for four seconds, with a heal of 25 (+0.3 ability
/// power) that drops by a fifth per hit; Righteous Gust 60 magic (+0.5 ability power) on the line, a haste on Galio and on allies who face away in the draft;
/// Idol of Durand, a two-second channel that taunts within 550, halves the magic damage Galio takes and bursts for 220 (+0.6 ability power), five percent more
/// per hit taken; and Runic Skin, half of the magic resist as ability power. The scenario <c>S-galio</c> shows the same facts on a headless match.
/// Written 26 October 2026.
/// </summary>
public static class BbGalioChecks
{
    private static Match Arena(int rank = 1, string foe = "Orianna")
    {
        var match = new Match("Galio", foeChampion: foe);
        match.Learn(rank);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        // Learn refreshes Galio's stats (level-one health 520) and Healing.Apply refuses a unit at or above its maximum, so the bar is set after it.
        match.Hero.MaxHealth = 5000.0;
        match.Hero.Health = 3000.0;
        match.Refill();
        return match;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-galio-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Galio");
        foreach (var name in new[] { "CharScriptGalio", "GalioResoluteSmite", "GalioBulwark", "GalioBulwarkHeal", "GalioRighteousGust", "GalioRighteousGustMissile",
                     "GalioRighteousGustHaste", "GalioIdolOfDurand", "GalioIdolOfDurandTaunt", "GalioIdolOfDurandMarker", "GalioRunicSkin" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Galio");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(23, closure.ScriptNames.Count(), "the closure is 23 scripts (audit bb-coverage: his four spells, the passive, the bulwark heal, the gust's missile, haste and draft, the idol's taunt and marker, and the system buffs)");

        var hero = new Entity { ChampionName = "Galio" };
        TestAssert.Equal(["GalioResoluteSmite", "GalioBulwark", "GalioRighteousGust", "GalioIdolOfDurand"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Galio.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Resolute Smite is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "Idol of Durand is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Galio")!;
        TestAssert.True(info is { Playable: true, Role: "tank", DefaultIp: 6300, Id: 3, RetailBot: false }, "playable as a tank for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Galio"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Runic Skin
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-galio-runic-skin-converts-half-the-magic-resist-to-ability-power")]
    public static void RunicSkinGivesHalfTheMagicResistAsAbilityPower()
    {
        using var match = Arena();
        match.Advance(0.2);
        TestAssert.True(Buff(match.Hero, "GalioRunicSkin") is not null, "Galio carries Runic Skin from activation");
        TestAssert.Near(15.0, match.Hero.AbilityPower, 1e-6, "30 magic resist gives 15 ability power");
        TestAssert.Near(match.Hero.SpellBlock * 0.5, match.Hero.AbilityPower, 1e-6, "ability power is half the total magic resist");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Resolute Smite (Q): a line missile (CastType 3) that explodes where it ends: its aim point (LineMissileEndsAtTargetPoint, inside the 900 range)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-galio-resolute-smite-damages-and-slows-the-230-area")]
    public static void ResoluteSmiteDamagesAndSlowsTheArea()
    {
        using var match = Arena();
        var foe = match.Foe;
        // The line ends at its aim point (890, inside the 900 range) and explodes there; the foe (900) and the probes stand around that point.
        var end = match.Hero.Position.X + 900.0;
        var aim = new Position2(match.Hero.Position.X + 890.0, match.Hero.Position.Z);
        foe.Position = new Position2(end, match.Hero.Position.Z);
        Enemy(match, "Inside", new Position2(end, match.Hero.Position.Z + 200.0));
        var outside = Enemy(match, "Outside", new Position2(end, match.Hero.Position.Z + 260.0));
        TestAssert.True(match.Cast(0, null, null, aim), "Resolute Smite is accepted toward the foe");
        match.Advance(1.5);

        // 80 magic at rank one plus 0.7 of the 15 ability power (SpellOnMissileEnd, DamageByLevel and SpellDamageRatio).
        var expected = Mitigated(80.0 + 0.7 * 15.0, foe.SpellBlock);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the foe at the end of the line is hit once");
        TestAssert.Near(expected, hits[0].Dealt, 0.6, "80 plus 0.7 ability power, mitigated by the foe's magic resist");
        TestAssert.True(Buff(foe, "GalioResoluteSmite") is not null, "the foe carries the slow");
        TestAssert.Equal(1, HitsOn(match, "Inside").Count, "a unit 200 from the end is inside the 230 area");
        TestAssert.Near(outside.MaxHealth, outside.Health, 1e-6, "a unit 260 from the end is outside it and takes nothing");

        // The slow lasts 2.5 seconds (the buff's Duration).
        match.Advance(3.0);
        TestAssert.True(Buff(foe, "GalioResoluteSmite") is null, "the slow ends after 2.5 seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Bulwark (W): armor and magic resist on an ally, a heal of Galio on each hit that falls by a fifth
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-galio-bulwark-shields-an-ally-and-heals-galio-on-each-hit")]
    public static void BulwarkShieldsAnAllyAndHealsGalio()
    {
        using var match = Arena();
        var ally = match.Ally;
        var armorBefore = ally.Armor;
        var resistBefore = ally.SpellBlock;
        TestAssert.True(match.Cast(1, ally), "Bulwark is accepted on the ally");
        match.Advance(0.5);
        TestAssert.Near(armorBefore + 30.0, ally.Armor, 1e-6, "Bulwark adds 30 armor at rank one");
        TestAssert.Near(resistBefore + 30.0, ally.SpellBlock, 1e-6, "Bulwark adds 30 magic resist at rank one");

        // The heal is 25 plus 0.3 of the 15 ability power, then four fifths of the last one on each further hit (BuffOnPreDamage, MO_MULTIPLY 0.8, MO_MAX 1).
        var heals = new List<double>();
        var health = match.Hero.Health;
        foreach (var _ in Enumerable.Range(0, 3))
        {
            match.Runtime.Combat.ApplyHit(match.Foe.NetId, ally.NetId, 100.0, DamageType.Magic, DamageSourceKind.Spell);
            match.Advance(0.2);
            heals.Add(match.Hero.Health - health);
            health = match.Hero.Health;
        }
        TestAssert.Near(29.5, heals[0], 0.6, "the first hit heals 25 plus 0.3 of 15");
        TestAssert.Near(29.5 * 0.8, heals[1], 0.6, "the second hit heals four fifths of it");
        TestAssert.Near(29.5 * 0.8 * 0.8, heals[2], 0.6, "the third hit heals four fifths again");

        // Four seconds of shield, then the stats go back.
        match.Advance(4.2);
        TestAssert.True(Buff(ally, "GalioBulwark") is null, "the shield ends after four seconds");
        TestAssert.Near(armorBefore, ally.Armor, 1e-6, "the armor goes back");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Righteous Gust (E): a line that damages, a haste on Galio, a haste on allies in the draft, and a vision cube
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-galio-righteous-gust-hits-its-line-and-hastes-the-draft")]
    public static void RighteousGustHitsItsLineAndHastesTheDraft()
    {
        using var match = Arena();
        var foe = match.Foe;
        var ally = match.Ally;
        // BuffOnUpdateActions hastes a unit in the draft that is in front of the gust's attacker and behind its own facing: an ally who runs with the wind.
        ally.Facing = new Position2(1.0, 0.0);
        TestAssert.True(match.Cast(2, null, null, new Position2(match.Hero.Position.X + 1100.0, match.Hero.Position.Z)), "Righteous Gust is accepted toward the east");
        match.Advance(0.3);
        TestAssert.True(Buff(match.Hero, "GalioRighteousGustHaste") is not null, "Galio is hasted by his own cast (a second, SelfExecute)");

        var allyHasted = false;
        var cubes = 0;
        for (var tick = 0; tick < 60; tick++)
        {
            match.Advance(Step);
            if (Buff(ally, "GalioRighteousGustHaste") is not null) allyHasted = true;
            cubes = Math.Max(cubes, match.Runtime.World.Entities.Values.Count(entity => entity.Name == "TestCube" && entity.VisibilitySize == 100.0));
        }
        TestAssert.True(allyHasted, "an ally in front of Galio, facing away, is hasted by the draft");
        TestAssert.True(cubes > 0, "the draft is a vision cube with a 100 sight radius (BBSpawnMinion VisibilitySize)");

        // 60 magic plus 0.5 of the 15 ability power, once, on the foe that the line crosses.
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the line hits the foe once");
        TestAssert.Near(Mitigated(60.0 + 0.5 * 15.0, foe.SpellBlock), hits[0].Dealt, 0.6, "60 plus 0.5 ability power, mitigated");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Idol of Durand (R): a two-second channel, taunts, half the damage taken, a burst for the hits it took
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-galio-idol-of-durand-taunts-and-halves-the-damage-he-takes")]
    public static void IdolOfDurandTauntsAndHalvesTheDamage()
    {
        using var match = Arena(rank: 1);
        var foe = match.Foe;
        TestAssert.True(match.Cast(3), "Idol of Durand is accepted on himself");
        match.Advance(0.6);
        TestAssert.True(Buff(foe, "Taunt") is not null, "an enemy within 550 is taunted by the Idol");

        // Magic damage is halved before the magic resist (BuffOnPreDamage, DAMAGE_TYPE not TRUE, MO_MULTIPLY 0.5).
        match.Runtime.Combat.ApplyHit(foe.NetId, match.Hero.NetId, 200.0, DamageType.Magic, DamageSourceKind.Spell);
        match.Advance(0.1);
        var taken = HitsOn(match, "Galio");
        TestAssert.Equal(1, taken.Count, "Galio takes the hit once");
        TestAssert.Near(Mitigated(100.0, match.Hero.SpellBlock), taken[0].Dealt, 0.6, "half of the magic damage gets through the resist");
    }

    [Check("scripting.bb-galio-idol-of-durand-bursts-for-the-hits-he-took")]
    public static void IdolOfDurandBurstsForTheHitsItTook()
    {
        using var match = Arena(rank: 1);
        var foe = match.Foe;
        var start = match.Now;
        TestAssert.True(match.Cast(3), "Idol of Durand is accepted on himself");
        match.Advance(0.5);
        for (var hit = 0; hit < 3; hit++)
        {
            match.Runtime.Combat.ApplyHit(foe.NetId, match.Hero.NetId, 50.0, DamageType.Magic, DamageSourceKind.Spell);
            match.Advance(0.1);
        }

        // Every damage instance on Galio while the channel runs counts (BuffOnPreDamage, HitCount up to eight): the three hits here and the foe's own
        // attacks and procs once the taunt holds her. The burst is 220 plus 0.6 of the 15 ability power, times 1 + 0.05 x hits (the engine multiplies the whole
        // sum by PercentOfAttack). The channel's success stop adds one magic point of damage to each unit in its area.
        match.Advance(2.5);
        var taken = HitsOn(match, "Galio").Count(hit => hit.At >= start && hit.At < start + 2.0);
        var burst = HitsOn(match, "Foe").Where(hit => hit.Raw > 10.0).ToArray();
        TestAssert.True(taken >= 3, "the three hits on Galio are counted");
        TestAssert.Equal(1, burst.Length, "the burst hits the foe once");
        TestAssert.Near(Mitigated((220.0 + 0.6 * 15.0) * (1.0 + 0.05 * Math.Min(taken, 8)), foe.SpellBlock), burst[0].Dealt, 0.6,
            "220 plus 0.6 ability power, times 1 plus five percent per hit taken");
        TestAssert.True(Buff(match.Hero, "GalioIdolOfDurand") is null, "the channel is over");
    }

    /// <summary>
    /// A stun cuts the channel (batch 299 open question; batch 309): <c>ChannelingCancelStop</c> removes <c>GalioIdolOfDurand</c> as the success stop does,
    /// and the buff's <c>OnBuffDeactivate</c> bursts and frees the taunted foes, so the cut channel still bursts, for the hits taken so far.
    /// </summary>
    [Check("scripting.bb-galio-idol-of-durand-bursts-when-a-stun-cuts-the-channel")]
    public static void IdolOfDurandBurstsWhenCut()
    {
        using var match = Arena(rank: 1);
        var foe = match.Foe;
        TestAssert.True(match.Cast(3), "Idol of Durand is accepted on himself");
        match.Advance(0.5);
        TestAssert.True(match.Module.Channels!.Of(match.Hero) is not null, "the channel runs");
        TestAssert.True(Buff(foe, "Taunt") is not null, "the foe is taunted");
        match.Runtime.SpellBuffs.Add(match.Hero, "Stun", BuffType.Stun, 1.0, foe.NetId, null, canMitigateDuration: false);
        match.Advance(0.2);
        TestAssert.True(match.Module.Channels!.Of(match.Hero) is null, "the stun ended the channel");
        TestAssert.True(Buff(match.Hero, "GalioIdolOfDurand") is null, "the channel's buff is gone");
        var burst = HitsOn(match, "Foe").Where(hit => hit.Raw > 10.0).ToArray();
        TestAssert.Equal(1, burst.Length, "the cut channel bursts once");
        TestAssert.True(burst[0].Raw >= 220.0 + 0.6 * 15.0 - 0.01, "the burst is at least its 220 plus 0.6 ability power");
        TestAssert.True(Buff(foe, "Taunt") is null, "the foe is freed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death and respawn
    // ------------------------------------------------------------------------------------------------------------

    // GalioRighteousGustMissile's OnBuffActivate reads GetSlotSpellLevel of Galio's extra slot 3 (BBGetSlotSpellInfo, SlotType ExtraSlots). Galio holds no extra
    // spell there, so the read must answer an empty slot instead of raising a script exception (a skipped hook).
    [Check("scripting.bb-galio-righteous-gust-runs-without-script-failures")]
    public static void RighteousGustRunsWithoutScriptFailures()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(2, null, null, new Position2(match.Hero.Position.X + 1100.0, match.Hero.Position.Z)), "Righteous Gust is accepted toward the east");
        match.Advance(2.0);
        TestAssert.Equal(string.Empty, string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no hook of the gust is skipped");
    }

    // Batch 331 (marker #225): the slot-level lookup maps an inventory slot to 4.. and an extra slot to 42.. and
    // then answers 0 for any slot outside 0-3, so an extra slot answers 0 whether or not it holds a spell, and so does an inventory slot.
    [Check("scripting.bb-get-slot-spell-level-answers-zero-for-extra-and-inventory-slots")]
    public static void SlotSpellLevelOfExtraAndInventorySlotsIsZero()
    {
        using var match = Arena();
        var host = match.Module.Router!.SpellHost;
        var hero = match.Hero;
        while (hero.ActiveSpellNames.Count <= SpellSystem.ExtraSpellSlotStart + 3) hero.ActiveSpellNames.Add("");
        hero.ActiveSpellNames[SpellSystem.ExtraSpellSlotStart + 3] = "GalioRighteousGustMissile";
        hero.Inventory = [new InventorySlot(3146), null, null, null, null, null];
        TestAssert.Equal((object?)0.0, host.GetSlotSpellInfo(BbSlotSpellInfo.GetSlotSpellLevel, hero, 3.0, 0.0, (double)BbSlotType.ExtraSlots),
            "an extra slot that holds a spell answers 0");
        TestAssert.Equal((object?)0.0, host.GetSlotSpellInfo(BbSlotSpellInfo.GetSlotSpellLevel, hero, 0.0, 0.0, (double)BbSlotType.InventorySlots),
            "an inventory slot answers 0");
        TestAssert.Equal((object?)(double)hero.SpellRanks[2], host.GetSlotSpellInfo(BbSlotSpellInfo.GetSlotSpellLevel, hero, 2.0, 0.0, (double)BbSlotType.SpellSlots),
            "a champion slot answers its rank");
    }

    [Check("scripting.bb-galio-runic-skin-persists-through-death-and-respawn")]
    public static void RunicSkinSurvivesADeath()
    {
        using var match = Arena();
        match.Advance(0.2);
        var before = match.Hero.AbilityPower;
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.False(match.Hero.Alive, "Galio is dead");
        TestAssert.True(Buff(match.Hero, "GalioRunicSkin") is not null, "PersistsThroughDeath keeps the passive through the death");
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        match.Advance(30.0);
        TestAssert.True(match.Hero.Alive, "Galio respawns");
        TestAssert.Near(before, match.Hero.AbilityPower, 1e-6, "the passive gives the same ability power after the respawn");
    }
}
