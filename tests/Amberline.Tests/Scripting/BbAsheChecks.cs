using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (25 October 2026, batch 237): Ashe plays from her shipped scripts, a retail bot. Expected numbers are the literals of the shipped
/// scripts and the spell inibins (the audit fixture's standalone decode). The scenario <c>S-Ashe</c> shows the same facts on a headless match, with
/// the passive, the death and the respawn. Written 25 October 2026.
/// </summary>
public static class BbAsheChecks
{
    [Check("scripting.bb-ashe-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Ashe");
        foreach (var name in new[]
                 {
                     "CharScriptAshe", "FrostShot", "FrostArrow", "FrostArrowApplicator", "Volley", "VolleyAttack", "AsheSpiritOfTheHawk",
                     "AsheSpiritOfTheHawkCast", "AsheSpiritOfTheHawkBubble", "EnchantedCrystalArrow", "Chilled", "Focus", "ArchersMark",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Ashe");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(22, closure.ScriptNames.Count(), "the closure is 22 scripts (four spells, the passive, the arrows and the helpers)");

        var hero = new Entity { ChampionName = "Ashe" };
        TestAssert.Equal(["FrostShot", "Volley", "AsheSpiritOfTheHawk", "EnchantedCrystalArrow"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Ashe.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Ashe")!;
        TestAssert.True(info is { Playable: true, Role: "adc", DefaultIp: 450, RetailBot: true }, "playable as an ADC for 450 IP, a retail bot");
    }

    /// <summary>Frost Shot (Q) is a toggle: the first cast adds the FrostShot buff, the next removes it, and switching costs no mana (ManaCost1..5 are False).</summary>
    [Check("scripting.bb-ashe-frost-shot-toggles-on-and-off-with-a-cast")]
    public static void FrostShotIsAToggle()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Hero), "Q casts on Ashe herself");
        TestAssert.True(Buff(match.Hero, "FrostShot") is not null, "the first cast turns Frost Shot on");
        TestAssert.True(match.Cast(0, match.Hero), "a second Q casts again");
        TestAssert.True(Buff(match.Hero, "FrostShot") is null, "the second cast turns it off");
        TestAssert.Near(mana, match.Hero.Mana, 1.0, "switching costs no mana");
    }

    /// <summary>Volley (W) costs 60 mana and a 20-second cooldown at rank one, and its arrows fly along the facing: a foe 300 units ahead on it is hit.</summary>
    [Check("scripting.bb-ashe-volley-costs-sixty-mana-and-hits-the-foe-ahead")]
    public static void VolleyFiresItsConeAlongTheFacing()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        match.Foe.Position = new Position2(match.Hero.Position.X + 300.0 * match.Hero.Facing.X, match.Hero.Position.Z + 300.0 * match.Hero.Facing.Z);
        match.Foe.SpellBlock = 0.0;
        match.Foe.Armor = 0.0;
        var before = match.Foe.Health;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, match.Hero), "W casts");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(1));
        TestAssert.Near(60.0, mana - match.Hero.Mana, 1.0, "Volley costs 60 mana at rank one");
        TestAssert.Near(20.0, match.Hero.SpellCooldowns[1], 0.05, "rank one's 20-second cooldown starts at the cast frame");
        match.Advance(1.5);
        TestAssert.True(before - match.Foe.Health >= 40.0, "an arrow of the cone hits the foe ahead for at least its 40 bonus damage");
    }

    /// <summary>Batch 362 (owner finding 2, Ashe W): the cone's direction is the cast point, not the hero's stale facing. Ashe starts facing north,
    /// the foe stands 300 units east and the cast is aimed at it: she turns to the point and the arrows hit it.</summary>
    [Check("scripting.bb-ashe-volley-cone-turns-to-its-cast-point")]
    public static void VolleyConeFollowsTheCastPoint()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        match.Foe.SpellBlock = 0.0;
        match.Foe.Armor = 0.0;
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(1, null, null, match.Foe.Position), "W casts at the foe, 300 units east");
        match.Advance(match.CastFrameOf(1));
        TestAssert.True(match.Hero.Facing.X > 0.99, $"the hero turns to the cast point at its cast frame (facing {match.Hero.Facing.X:0.00}, {match.Hero.Facing.Z:0.00})");
        match.Advance(1.5);
        TestAssert.True(before - match.Foe.Health >= 40.0, $"the cone hits the foe at the cast point (dealt {before - match.Foe.Health:0.0})");
    }

    /// <summary>Batch 362 (owner finding 2, Ashe W, "the second cast is broken"): a second cast a long way round fires at its own point, not at the
    /// direction of the first. The first cast goes east, the foe is moved west and the second cast is aimed at it.</summary>
    [Check("scripting.bb-ashe-volley-second-cast-fires-at-its-own-point")]
    public static void VolleyCastsAgainFireAtTheirOwnPoints()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        match.Foe.SpellBlock = 0.0;
        match.Foe.Armor = 0.0;
        TestAssert.True(match.Cast(1, null, null, match.Foe.Position), "the first W casts at the foe, 300 units east");
        match.Advance(match.CastFrameOf(1) + 1.5);
        match.Refill();
        match.Foe.Position = new Position2(match.Hero.Position.X - 300.0, match.Hero.Position.Z);
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(1, null, null, match.Foe.Position), "the second W casts at the foe, 300 units west");
        match.Advance(match.CastFrameOf(1));
        TestAssert.True(match.Hero.Facing.X < -0.99, $"the second cast turns the hero west (facing {match.Hero.Facing.X:0.00}, {match.Hero.Facing.Z:0.00})");
        match.Advance(1.5);
        TestAssert.True(before - match.Foe.Health >= 40.0, $"the second cone hits the foe where it was aimed (dealt {before - match.Foe.Health:0.0})");
    }

    /// <summary>Batch 362 (owner finding 2, Ashe W, "double cast"): a second press during the first Volley's windup turns the hero toward its own point,
    /// and the first cone still fires along its own aim at its own cast frame. The first press is east at the foe, the second, half a windup later,
    /// is west at FarFoe (whether the engine accepts that second press is its own gate, not this check's concern).</summary>
    [Check("scripting.bb-ashe-volley-second-press-in-the-windup-keeps-the-first-cone")]
    public static void SecondPressInTheWindupKeepsTheFirstCone()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        match.Foe.SpellBlock = 0.0;
        match.Foe.Armor = 0.0;
        match.FarFoe.Position = new Position2(match.Hero.Position.X - 300.0, match.Hero.Position.Z);
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(1, null, null, match.Foe.Position), "the first W casts at the foe, 300 units east");
        match.Advance(match.CastFrameOf(1) * 0.5);
        _ = match.Cast(1, null, null, match.FarFoe.Position);
        match.Advance(match.CastFrameOf(1) + 1.5);
        TestAssert.True(before - match.Foe.Health >= 40.0, $"the first cone still hits the foe it was aimed at (dealt {before - match.Foe.Health:0.0})");
    }

    /// <summary>Hawkshot (E) lands at its point and leaves Ashe a five-second bubble there (the perception bubble is removed with the buff).</summary>
    [Check("scripting.bb-ashe-hawkshot-leaves-a-five-second-bubble-where-it-lands")]
    public static void HawkshotLeavesABubble()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        var target = new Position2(match.Hero.Position.X + 800.0, match.Hero.Position.Z);
        TestAssert.True(match.Cast(2, null, null, target), "E casts 800 units away");
        // Batch 340: the cast frame pays, not the press.
        match.Advance(match.CastFrameOf(2));
        TestAssert.Near(60.0, match.Hero.SpellCooldowns[2], 0.05, "rank one's 60-second cooldown starts at the cast frame");
        var appeared = Appears(match, match.Hero, "AsheSpiritOfTheHawkBubble", 3.0);
        TestAssert.True(!double.IsNaN(appeared), "the landing gives Ashe the bubble buff");
        var gone = Vanishes(match, match.Hero, "AsheSpiritOfTheHawkBubble", 8.0);
        TestAssert.Near(5.0, gone - appeared, 0.3, "the bubble lasts five seconds");
    }

    /// <summary>Enchanted Crystal Arrow (R) at rank one hits the struck foe for 250 magic, stuns it for one second when it lands on the aim point (the stun is
    /// 0.00125 seconds per unit from there, at least one) and chills it for three.</summary>
    [Check("scripting.bb-ashe-crystal-arrow-stuns-the-struck-foe-and-chills-it")]
    public static void CrystalArrowStunsItsTarget()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(6);
        match.Refill();
        match.Advance(0.5);
        match.Hero.SpellRanks[3] = 1;
        match.Foe.SpellBlock = 0.0;
        var before = match.Foe.Health;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, null, null, match.Foe.Position), "R casts at the foe");
        // Batch 340: the cast frame pays, not the press.
        match.Advance(match.CastFrameOf(3));
        TestAssert.Near(150.0, mana - match.Hero.Mana, 1.0, "rank one costs 150 mana");
        TestAssert.Near(100.0, match.Hero.SpellCooldowns[3], 0.05, "rank one's 100-second cooldown starts at the cast frame");
        var stunned = Appears(match, match.Foe, "EnchantedCrystalArrow", 3.0);
        TestAssert.True(!double.IsNaN(stunned), "the foe is stunned when the arrow lands");
        TestAssert.True(Buff(match.Foe, "Chilled") is not null, "the same landing chills the foe");
        // The committed hit is the proof: the foe's health bar is not read, because the harness's 5000 maximum is reset to Orianna's
        // level-one base by the stat refresh of the stun and the chill (the health falls to that base, not to the hit).
        TestAssert.True(match.Log.Hits.Any(line => line.Contains("Ashe->Foe dealt=250 raw=250", StringComparison.Ordinal)),
            "the arrow commits 250 magic damage at rank one");
        TestAssert.True(before >= 5000.0, "the foe started at the harness's 5000 health");
        var gone = Vanishes(match, match.Foe, "EnchantedCrystalArrow", 3.0);
        TestAssert.Near(1.0, gone - stunned, 0.1, "a landing on the aim point stuns for one second");
    }

    /// <summary>Batch 362 (owner finding 3, Ashe E: "does not cast when Ashe arrives the required spot when it's really far away"): a Hawkshot aimed 5000
    /// units west is out of its 2500 range, so Ashe walks round the ridge in front of the firing point (about eleven seconds) and fires there on
    /// arrival. The walk is progress; the give-up clock does not drop the cast.</summary>
    [Check("scripting.bb-ashe-hawkshot-far-cast-walks-round-the-ridge-and-fires-on-arrival")]
    public static void HawkshotFarCastFiresOnArrival()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, null, null, new Position2(2000.0, 7000.0)), "E is accepted at a point 5000 units west, beyond its 2500 range");
        match.Advance(15.0);
        TestAssert.True(match.Hero.SpellCooldowns[2] > 50.0,
            $"the hawk fires when Ashe arrives: its 60-second cooldown started (cooldown {match.Hero.SpellCooldowns[2]:0.0})");
    }

    /// <summary>Batch 362 guard for the fix above: a walk that does not close in (Ashe rooted at once) still gives up after the window, and the hawk
    /// is never cast.</summary>
    [Check("scripting.bb-ashe-hawkshot-rooted-walk-gives-up-after-the-window")]
    public static void RootedWalkGivesUpAfterTheWindow()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(1);
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(2, null, null, new Position2(2000.0, 7000.0)), "E is accepted at a point 5000 units west");
        TestAssert.True(match.Runtime.Buffs.AddBuff(match.Hero, "Root", BuffType.Snare, 60.0, match.Foe.NetId) is not null, "a root holds Ashe");
        match.Advance(10.0);
        TestAssert.True(match.Hero.PendingCast is null, "the approach that never closes in gives up within its window");
        TestAssert.Equal(0.0, match.Hero.SpellCooldowns[2], "the hawk is not cast");
    }

    /// <summary>Archer's Mark is the aura Hawkshot adds while it is learnt; its kill hook pays the Hawkshot rank in gold (1 to 5 by rank).</summary>
    [Check("scripting.bb-ashe-archers-mark-pays-gold-per-kill-by-hawkshot-rank")]
    public static void ArchersMarkPaysByHawkshotRank()
    {
        var match = new BbM3.Match("Ashe");
        match.Learn(0);
        match.Refill();
        match.Advance(0.5);
        var first = Enemy(match, "Minion", new Position2(match.Hero.Position.X + 50.0, match.Hero.Position.Z));
        var goldBefore = match.Hero.Gold;
        match.Runtime.Combat.Kill(first, match.Hero.NetId);
        var plain = match.Hero.Gold - goldBefore;
        TestAssert.True(Buff(match.Hero, "ArchersMark") is null, "no Archer's Mark without Hawkshot");

        match.Hero.SpellRanks[2] = 3;
        match.Advance(0.5);
        TestAssert.True(Buff(match.Hero, "ArchersMark") is not null, "Hawkshot at rank three adds Archer's Mark");
        var second = Enemy(match, "Minion", new Position2(match.Hero.Position.X + 50.0, match.Hero.Position.Z));
        goldBefore = match.Hero.Gold;
        match.Runtime.Combat.Kill(second, match.Hero.NetId);
        TestAssert.Near(3.0, match.Hero.Gold - goldBefore - plain, 0.01, "the same kill pays three gold more at Hawkshot rank three");
    }

    // The match clock when a buff is first seen (NaN if it does not come within the window).
    private static double Appears(BbM3.Match match, Entity unit, string name, double window)
    {
        var deadline = match.Now + window;
        while (match.Now < deadline && Buff(unit, name) is null) match.Advance(0.05);
        return Buff(unit, name) is null ? double.NaN : match.Now;
    }

    // The match clock when a buff is first seen gone (NaN if it stays past the window).
    private static double Vanishes(BbM3.Match match, Entity unit, string name, double window)
    {
        var deadline = match.Now + window;
        while (match.Now < deadline && Buff(unit, name) is not null) match.Advance(0.05);
        return Buff(unit, name) is null ? match.Now : double.NaN;
    }
}
