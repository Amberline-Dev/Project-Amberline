using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 5 (batch 294, 26 October 2026): Evelynn plays from her shipped scripts in a production <c>MatchRuntime</c>. The expected numbers are the
/// literals of her shipped scripts and spell inibins (the rank-one figures are read from the inibin dumps and the script tables); the probes run on the
/// <see cref="Match"/> harness (Summoner's Rift, Evelynn against Ezreal 300 away with Ezreal's 30 magic resistance and no ability power unless said).
/// The scenario <c>S-evelynn</c> shows the same mechanics on a headless match. The shipped kit is Determined Killer (the passive), Hate Spike, Shadow Walk
/// (an active toggle), Ravage and Malice and Spite; there is no Dark Frenzy and no backstab bonus in the shipped data.
/// </summary>
public static class BbEvelynnChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static int Stacks(Entity unit, string name) => Buff(unit, name)?.Stacks ?? 0;

    private static double Cooldown(Match match, int slot) => match.Hero.SpellCooldowns[slot];

    // The damage tables of a script's BBApplyDamage blocks, in the order the hook lists them (the Hate Spike's second bolt is the first, the full hit second).
    private static List<double[]> DamageTables(string script, string hook) =>
        Blocks(Script(script), hook).Where(block => block.Function == "BBApplyDamage").Select(block => Table(block, "DamageByLevel")).ToList();

    // The per-rank values of a BBIncStat block of a hook, in the order the hook lists them.
    private static List<double[]> StatTables(string script, string hook) =>
        Blocks(Script(script), hook).Where(block => block.Function == "BBIncStat").Select(block => Table(block, "DeltaByLevel")).ToList();

    private static void Advance(Match match, double seconds) => match.Advance(seconds);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks, shipped numbers
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-evelynn-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Evelynn");
        foreach (var name in new[]
                 {
                     "CharScriptEvelynn", "HateSpike", "HateSpikeMissile", "ShadowWalk", "ShadowWalk_internal", "Ravage", "MaliceandSpite",
                     "SilentKiller", "WasStealthed", "Malice_marker", "EvelynnUnlockAnimation", "Slow",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Evelynn");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(21, closure.ScriptNames.Count(), "the closure is 21 scripts (the character script, four spells and the missiles, the passive and its buffs, the stealth's internal timer, the system buffs)");
        // The passive's data name ("Silent Killer") names no file: the buff is the one the character script adds (SilentKiller), as in Zilean's and Kennen's passives.
        TestAssert.Equal(1, closure.Dangling.Count, "one dangling name");
        TestAssert.True(closure.Dangling[0].Name.StartsWith("Silent Killer", StringComparison.Ordinal), "the dangling name is the passive's data name");
        // Malice_markertwo, ShadowWalkSpeedHolder, EvelynnMaliceandSpiteTickDamage and MaliceAndSpiteCanCast are shipped files that no script of the closure names.
        foreach (var unused in new[] { "Malice_markertwo", "ShadowWalkSpeedHolder", "EvelynnMaliceandSpiteTickDamage", "MaliceAndSpiteCanCast" })
            TestAssert.False(module.ScriptNames.Contains(unused, StringComparer.OrdinalIgnoreCase), $"{unused} is not in the closure");

        var hero = new Entity { ChampionName = "Evelynn" };
        TestAssert.Equal(["HateSpike", "ShadowWalk", "Ravage", "MaliceandSpite"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Evelynn.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Evelynn")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 1350, Id: 28, RetailBot: false }, "playable as a mid laner for 1350 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Evelynn"), "listed once");

        // The figures the probes rest on, as literals of the shipped scripts.
        var hateSpike = DamageTables("HateSpikeMissile", "TargetExecute");
        TestAssert.Equal([12.5, 20.0, 27.5, 35.0, 42.5], hateSpike[0], "the second bolt of Hate Spike is half the first (the HSCounter == 2 branch)");
        TestAssert.Equal([25.0, 40.0, 55.0, 70.0, 85.0], hateSpike[1], "the first bolt of Hate Spike by rank");
        TestAssert.Equal([80.0, 135.0, 190.0, 255.0, 320.0], DamageTables("Ravage", "TargetExecute")[0], "Ravage damage by rank");
        TestAssert.Equal([-10.0, -14.0, -18.0, -22.0, -26.0], ByLevel("Ravage", "TargetExecute", "ArmorMod"), "Ravage cuts armour and magic resistance by rank");
        TestAssert.Equal([10.0, 20.0, 30.0, 40.0, 50.0], ByLevel("ShadowWalk", "TargetExecute", "StealthDuration"), "Shadow Walk stealth by rank");
        TestAssert.Equal([-0.3, -0.35, -0.4, -0.45, -0.5], ByLevel("ShadowWalk", "TargetExecute", "MoveSpeedMod"), "the next hit's slow by rank");
        TestAssert.Equal([12.0, 11.0, 10.0, 9.0, 8.0], ByLevel("ShadowWalk", "OnBuffDeactivate", "BaseCooldown"), "Shadow Walk's cooldown starts when the stealth ends");
        // The character script (Characters/Evelynn/Scripts) is read with CharScript, not Script (the spells' folder).
        var maliceHeal = Blocks(CharScript("Evelynn"), "CharOnLevelUpSpell").First(block => block.Function == "BBSetVarInTable" && block.Text("DestVar") == "MaliceHeal");
        TestAssert.Equal([150.0, 225.0, 300.0], Table(maliceHeal, "SrcValueByLevel"), "Malice and Spite's kill heal by rank");
        TestAssert.Equal([0.2, 0.25, 0.3], StatTables("MaliceandSpite", "BuffOnUpdateStats")[0], "the speed bonus by rank");
        TestAssert.Equal([0.25, 0.5, 0.75], StatTables("MaliceandSpite", "BuffOnUpdateStats")[1], "the attack speed bonus by rank");

        var q = GameData.Default.Spell("HateSpike", 5);
        // The inibin stores 0.8 as a float (0.800000011920929), so the cooldowns are compared to 0.8 within a float's step.
        TestAssert.Equal(5, q.Cooldowns.Count, "Hate Spike has five cooldowns");
        foreach (var cooldown in q.Cooldowns) TestAssert.Near(0.8, cooldown, 1e-6, "Hate Spike recasts after 0.8 s at every rank");
        TestAssert.Equal([12.0, 15.0, 18.0, 21.0, 24.0], q.ManaCosts.ToArray(), "Hate Spike costs by rank");
        var w = GameData.Default.Spell("ShadowWalk", 5);
        TestAssert.Equal([60.0, 60.0, 60.0, 60.0, 60.0], w.ManaCosts.ToArray(), "Shadow Walk costs 60 mana");
        TestAssert.Equal(200.0, w.CastRange, "Shadow Walk range (the spell is cast on herself)");
        var e = GameData.Default.Spell("Ravage", 5);
        TestAssert.Equal([9.0, 9.0, 9.0, 9.0, 9.0], e.Cooldowns.ToArray(), "Ravage cooldown");
        TestAssert.Equal([90.0, 105.0, 120.0, 135.0, 150.0], e.ManaCosts.ToArray(), "Ravage costs by rank");
        TestAssert.Equal(325.0, e.CastRange, "Ravage range");
        var r = GameData.Default.Spell("MaliceandSpite", 3);
        TestAssert.Equal([90.0, 90.0, 90.0], r.Cooldowns.ToArray(), "Malice and Spite cooldown");
        TestAssert.Equal([0.0, 0.0, 0.0], r.ManaCosts.ToArray(), "Malice and Spite costs nothing (its ManaCost keys are stored as False)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Determined Killer, Hate Spike
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-evelynn-determined-killer-halves-only-minion-hits")]
    public static void DeterminedKillerHalvesOnlyMinionHits()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        TestAssert.True(Buff(match.Hero, "SilentKiller") is not null, "the passive is on from the start");
        var armour = match.Hero.Armor;
        var minion = Enemy(match, "EvelynnPassiveMinion", new Position2(match.Hero.Position.X + 120.0, match.Hero.Position.Z));
        var neutral = Enemy(match, "EvelynnPassiveNeutral", new Position2(match.Hero.Position.X - 120.0, match.Hero.Position.Z), UnitType.Minion, Team.Neutral);
        var before = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(minion.NetId, match.Hero.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic);
        var fromMinion = before - match.Hero.Health;
        before = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic);
        var fromChampion = before - match.Hero.Health;
        before = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(neutral.NetId, match.Hero.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic);
        var fromNeutral = before - match.Hero.Health;
        // 100 physical after her armour is the whole hit; the minion's is half of the raw amount, then the same armour.
        TestAssert.Near(Mitigated(100.0, armour), fromChampion, 0.05, "a champion's hit is whole");
        TestAssert.Near(Mitigated(100.0, armour), fromNeutral, 0.05, "a neutral unit's hit is whole (the passive names non-neutral minions)");
        TestAssert.Near(Mitigated(50.0, armour), fromMinion, 0.05, "a non-neutral minion's hit is half");
        // A turret is an AI unit too, and the script's branch leaves it whole (IS_TYPE_TURRET). A turret's own hit is not the raw 100: each hit adds its
        // ramp (the turret's own growth, about 17.7 at this time), so the check compares the step between hits with the passive and the step after it is gone.
        var hits = new double[3];
        for (var i = 0; i < 2; i++)
        {
            before = match.Hero.Health;
            match.Runtime.Combat.ApplyHit(match.ChaosLaneTurret.NetId, match.Hero.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic);
            hits[i] = before - match.Hero.Health;
        }
        match.Hero.Buffs.Remove(Buff(match.Hero, "SilentKiller")!);
        before = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(match.ChaosLaneTurret.NetId, match.Hero.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic);
        hits[2] = before - match.Hero.Health;
        TestAssert.True(hits[0] > 0.0, "the turret hits her");
        TestAssert.Near(hits[1] - hits[0], hits[2] - hits[1], 0.05, "without the passive the turret's next hit is on the same ramp (the passive does not touch it)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-hate-spike-hits-the-closest-and-the-second-bolt-at-half")]
    public static void HateSpikeHitsTheClosestAndTheSecondBoltAtHalf()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        // Hate Spike's CanCast picks a unit at random (BBForEachUnitInTargetAreaRandom): the stream is pinned so the probe is repeatable.
        match.Runtime.World.LuaRandom.HoldRand = 11;
        match.Learn(1);
        match.Refill();
        // The far hero stands 200 away, closer than Foe (300): the first bolt takes him, the second bolt the nearby Foe at half.
        match.FarFoe.Position = new Position2(match.Hero.Position.X + 200.0, match.Hero.Position.Z);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Hero), "Hate Spike is cast on Evelynn (a self cast that finds the closest unit)");
        TestAssert.Near(12.0, mana - match.Hero.Mana, 0.01, "12 mana at rank one, paid at the cast");
        Advance(match, 0.3);
        TestAssert.Near(0.5, Cooldown(match, 0), 0.06, "the cooldown is 0.8 s and runs from the cast");
        Advance(match, 1.0);
        var near = HitsOn(match, "FarFoe");
        var far = HitsOn(match, "Foe");
        TestAssert.Equal(1, near.Count, "the closer hero takes one bolt");
        TestAssert.Equal(1, far.Count, "the farther hero takes the second bolt");
        // 25 magic at rank one with no ability power, after the 30 magic resistance; the second bolt is half of it.
        TestAssert.Near(Mitigated(25.0, 30.0), near[0].Dealt, 0.05, "the first bolt is 25 magic after 30 resistance");
        TestAssert.Near(Mitigated(12.5, 30.0), far[0].Dealt, 0.05, "the second bolt is half of the first");
        TestAssert.Equal(1, Stacks(match.FarFoe, "Malice_marker"), "a hero struck by the first bolt carries Malice_marker");
        TestAssert.Equal(1, Stacks(match.Foe, "Malice_marker"), "a hero struck by the second bolt carries Malice_marker too");
        Advance(match, 8.5);
        TestAssert.Equal(1, Stacks(match.Foe, "Malice_marker"), "the marker lasts ten seconds");
        Advance(match, 1.5);
        TestAssert.Equal(0, Stacks(match.Foe, "Malice_marker"), "the marker is gone after ten seconds");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-hate-spike-needs-a-unit-within-350")]
    public static void HateSpikeNeedsAUnitWithin350()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Runtime.World.LuaRandom.HoldRand = 11;
        match.Learn(1);
        match.Refill();
        // The ally stands beside her and does not count (Hate Spike names enemies, neutral and minion units, and heroes that are not friends).
        match.Foe.Position = new Position2(match.Hero.Position.X + 2000.0, match.Hero.Position.Z);
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        TestAssert.False(match.CanCast(0, match.Hero).Allowed, "refused with no enemy within 350");
        match.Foe.Position = new Position2(match.Hero.Position.X + 300.0, match.Hero.Position.Z);
        TestAssert.True(match.CanCast(0, match.Hero).Allowed, "allowed with an enemy at 300");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shadow Walk
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-evelynn-shadow-walk-walks-then-stealths-for-its-duration")]
    public static void ShadowWalkWalksThenStealthsForItsDuration()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        match.Foe.Position = new Position2(match.Hero.Position.X + 1500.0, match.Hero.Position.Z);
        var mana = match.Hero.Mana;
        var start = match.Now;
        TestAssert.True(match.Cast(1, match.Hero), "Shadow Walk is cast on herself");
        TestAssert.Near(60.0, mana - match.Hero.Mana, 0.01, "60 mana at rank one");
        TestAssert.True(Buff(match.Hero, "ShadowWalk_internal") is not null, "the walk starts");
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is null, "no stealth yet");
        TestAssert.Equal(0.0, Cooldown(match, 1), "the cooldown is zero while the spell is up (its cooldown starts when the stealth ends)");
        Advance(match, 1.0);
        TestAssert.False(match.CanCast(1, match.Hero).Allowed, "the walk seals the slot: a second cast is refused");
        Advance(match, 0.4);
        TestAssert.True(Buff(match.Hero, "ShadowWalk_internal") is not null && Buff(match.Hero, "ShadowWalk") is null,
            "1.4 s after the cast she still walks");
        var stealthAt = double.NaN;
        for (var step = 0; step < 60 && double.IsNaN(stealthAt); step++)
        {
            if (Buff(match.Hero, "ShadowWalk") is not null) stealthAt = match.Now;
            else Advance(match, 0.02);
        }
        TestAssert.False(double.IsNaN(stealthAt), "the stealth begins without a cast or a hit");
        TestAssert.True(Buff(match.Hero, "ShadowWalk_internal") is null, "the walk is over");
        // The script stamps TimeLastHit in TargetExecute, which an instant cast runs at the press: the stealth begins 1.5 s after the press.
        TestAssert.Near(1.5, stealthAt - start, 0.06, "the stealth begins 1.5 s after the cast (TimeLastHit is stamped at the press)");
        // The stealth lasts its rank's duration: ten seconds at rank one.
        Advance(match, 9.5);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is not null, "still stealthed at 9.5 s into it");
        var endedAt = double.NaN;
        for (var step = 0; step < 60 && double.IsNaN(endedAt); step++)
        {
            if (Buff(match.Hero, "ShadowWalk") is null) endedAt = match.Now;
            else Advance(match, 0.02);
        }
        TestAssert.False(double.IsNaN(endedAt), "the stealth ends after its ten seconds");
        TestAssert.Near(10.0, endedAt - stealthAt, 0.06, "the stealth lasts 10 s");
        TestAssert.Near(12.0, Cooldown(match, 1), 0.06, "the cooldown is 12 s at rank one from the end");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-shadow-walk-toggle-ends-it-free-and-starts-the-cooldown")]
    public static void ShadowWalkToggleEndsItFreeAndStartsTheCooldown()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        match.Foe.Position = new Position2(match.Hero.Position.X + 1500.0, match.Hero.Position.Z);
        TestAssert.True(match.Cast(1, match.Hero), "Shadow Walk is cast");
        Advance(match, 2.0);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is not null, "stealthed");
        // While stealthed the spell's own cost is 60 less (BBSetPARCostInc -60 on slot 1), so the toggle that ends it costs nothing.
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, match.Hero), "the second cast toggles the stealth off");
        TestAssert.Equal(mana, match.Hero.Mana, "the toggle costs no mana");
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is null && Buff(match.Hero, "ShadowWalk_internal") is null, "the stealth and its walk are gone");
        TestAssert.Near(12.0, Cooldown(match, 1), 0.01, "the cooldown starts at its base of 12 s");
        Advance(match, 1.0);
        TestAssert.Near(11.0, Cooldown(match, 1), 0.06, "and runs");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-stealth-spell-hit-slows-the-target-and-ends-the-stealth")]
    public static void StealthSpellHitSlowsAndEndsTheStealth()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        TestAssert.True(match.Cast(1, match.Hero), "Shadow Walk is cast");
        Advance(match, 2.0);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is not null, "stealthed before the spell");
        var speedBefore = match.Foe.Speed;
        TestAssert.True(match.Cast(0, match.Hero), "Hate Spike breaks the stealth");
        Advance(match, 0.4);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is null, "a spell that breaks stealth ends it");
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the spell's hit slows the target (WasStealthed, BuffOnSpellHit)");
        // The slow is 30 percent (MoveSpeedMod -0.3); below 220 the movement speed takes the soft floor (110 + half of it, as Zilean's check reads it).
        var slowed = speedBefore * 0.7;
        TestAssert.Near(slowed < 220.0 ? 110.0 + 0.5 * slowed : slowed, match.Foe.Speed, 0.5, "the slow is 30 percent at rank one, then the soft floor");
        Advance(match, 3.2);
        TestAssert.True(Buff(match.Foe, "Slow") is null, "the slow lasts three seconds");
        TestAssert.Near(speedBefore, match.Foe.Speed, 0.5, "and the speed comes back");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-stealth-attack-ends-it-and-slows-the-target")]
    public static void StealthAttackEndsItAndSlowsTheTarget()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        TestAssert.True(match.Cast(1, match.Hero), "Shadow Walk is cast");
        Advance(match, 2.0);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is not null, "stealthed before the attack");
        match.Foe.Position = new Position2(match.Hero.Position.X, match.Hero.Position.Z + 100.0);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack is queued from stealth");
        Advance(match, 0.1);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is null, "the swing ends the stealth (BreakAttackStealth)");
        Advance(match, 1.0);
        TestAssert.True(HitsOn(match, "Foe").Count == 1, "the swing lands");
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the hit slows the target (WasStealthed, BuffOnHitUnit)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-stealth-takes-damage-and-stays-stealthed")]
    public static void StealthTakesDamageAndStaysStealthed()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        match.Foe.Position = new Position2(match.Hero.Position.X + 1500.0, match.Hero.Position.Z);
        TestAssert.True(match.Cast(1, match.Hero), "Shadow Walk is cast");
        Advance(match, 2.0);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is not null, "stealthed");
        // The shipped stealth buff has no BuffOnTakeDamage: a hit does not reveal her (only a swing or her Hate Spike or Ravage does).
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, 60.0, DamageType.Magic, DamageSourceKind.Spell);
        Advance(match, 0.3);
        TestAssert.True(Buff(match.Hero, "ShadowWalk") is not null, "a hit from a champion does not end the stealth");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Ravage, Malice and Spite, the kill heal, death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-evelynn-ravage-hits-and-cuts-armour-and-resistance-for-five-seconds")]
    public static void RavageHitsAndCutsArmourAndResistance()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        var armour = match.Foe.Armor;
        var resistance = match.Foe.SpellBlock;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, match.Foe), "Ravage is cast on the enemy 300 away");
        Advance(match, 0.5);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press; regeneration allowed.
        TestAssert.Near(90.0, mana - match.Hero.Mana, 1.0, "90 mana at rank one");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit");
        // 80 magic at rank one with no ability power, after the 30 magic resistance.
        TestAssert.Near(Mitigated(80.0, 30.0), hits[0].Dealt, 0.05, "80 magic after 30 resistance");
        TestAssert.Near(armour - 10.0, match.Foe.Armor, 0.01, "armour is cut by 10 at rank one");
        TestAssert.Near(resistance - 10.0, match.Foe.SpellBlock, 0.01, "magic resistance is cut by 10 at rank one");
        TestAssert.True(Buff(match.Foe, "Ravage") is not null, "the Ravage buff is on the target");
        TestAssert.Near(9.0 - (0.5 - match.CastFrameOf(2)), Cooldown(match, 2), 0.06, "cooldown 9 s at rank one, from the cast frame");
        Advance(match, 5.0);
        TestAssert.True(Buff(match.Foe, "Ravage") is null, "the cut lasts five seconds");
        TestAssert.Near(armour, match.Foe.Armor, 0.01, "armour comes back");
        TestAssert.Near(resistance, match.Foe.SpellBlock, 0.01, "resistance comes back");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-ravage-scales-with-ability-power")]
    public static void RavageScalesWithAbilityPower()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.GiveAbilityPower();
        match.Refill();
        var ap = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(2, match.Foe), "Ravage is cast");
        Advance(match, 0.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit");
        // 80 + 1.0 x ability power (the shipped SpellDamageRatio), after 30 resistance.
        TestAssert.Near(Mitigated(80.0 + 1.0 * ap, 30.0), hits[0].Dealt, 0.05, "80 plus the full ability power after 30 resistance");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-malice-and-spite-hastens-for-ten-seconds-at-no-cost")]
    public static void MaliceAndSpiteHastensForTenSecondsAtNoCost()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        var speed = match.Hero.Speed;
        var attackSpeed = match.Hero.AttackSpeedMod;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, match.Hero), "Malice and Spite is cast on herself");
        TestAssert.Equal(mana, match.Hero.Mana, "it costs no mana");
        Advance(match, 0.5);
        TestAssert.True(Buff(match.Hero, "MaliceandSpite") is not null, "the speed buff is on her");
        TestAssert.Near(speed * 1.2, match.Hero.Speed, 0.5, "movement speed is up 20 percent at rank one");
        TestAssert.Near(attackSpeed + 0.25, match.Hero.AttackSpeedMod, 0.01, "attack speed is up 25 percent at rank one");
        TestAssert.Near(89.5, Cooldown(match, 3), 0.06, "cooldown 90 s at rank one");
        Advance(match, 9.0);
        TestAssert.True(Buff(match.Hero, "MaliceandSpite") is not null, "still hasted at nine seconds");
        Advance(match, 1.5);
        TestAssert.True(Buff(match.Hero, "MaliceandSpite") is null, "the haste ends after ten seconds");
        TestAssert.Near(speed, match.Hero.Speed, 0.5, "movement speed comes back");
        TestAssert.Near(attackSpeed, match.Hero.AttackSpeedMod, 0.01, "attack speed comes back");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-kill-heals-and-resets-malice-and-spite")]
    public static void KillHealsAndResetsMaliceAndSpite()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        // The kill heal is set when the ultimate is learnt (CharOnLevelUpSpell: 150 at rank one), so the rank is trained the way the game trains it.
        match.Hero.Level = 6;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), "the ultimate is learnt at level six");
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "Malice and Spite is cast (the reset has a cooldown to clear)");
        Advance(match, 0.5);
        match.Hero.Health = match.Hero.MaxHealth - 200.0;
        var before = match.Hero.Health;
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        TestAssert.Near(150.0, match.Hero.Health - before, 0.01, "a kill of a hero heals her 150 at rank one");
        TestAssert.Equal(0.0, Cooldown(match, 3), "and resets Malice and Spite's cooldown");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-assist-on-a-hero-heals-and-resets-malice-and-spite")]
    public static void AssistOnAHeroHealsAndResetsMaliceAndSpite()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Hero.Level = 6;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), "the ultimate is learnt at level six");
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "Malice and Spite is cast");
        Advance(match, 0.5);
        // Evelynn lands a hit on the foe, then her ally kills it: she is credited with the assist (the hit counts for fifteen seconds).
        match.FarFoe.Position = new Position2(12000.0, 12000.0);
        match.Foe.Position = new Position2(match.Hero.Position.X, match.Hero.Position.Z + 100.0);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "Evelynn hits the foe first");
        Advance(match, 1.0);
        match.Hero.Health = match.Hero.MaxHealth - 200.0;
        var before = match.Hero.Health;
        match.Foe.Health = 1.0;
        match.Runtime.Combat.Kill(match.Foe, match.Ally.NetId);
        TestAssert.Near(150.0, match.Hero.Health - before, 0.01, "the assist on a hero heals her 150 at rank one");
        TestAssert.Equal(0.0, Cooldown(match, 3), "and resets the cooldown");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-kill-heals-only-once-malice-and-spite-is-learnt")]
    public static void KillHealsOnlyOnceMaliceAndSpiteIsLearnt()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        // No ultimate rank yet: the script's Level >= 1 gate leaves the heal and the reset out.
        match.Refill();
        match.Hero.Health = match.Hero.MaxHealth - 200.0;
        var before = match.Hero.Health;
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        TestAssert.Near(0.0, match.Hero.Health - before, 0.01, "a kill with the ultimate unlearnt heals nothing");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-kill-of-a-minion-does-not-heal")]
    public static void KillOfAMinionDoesNotHeal()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Hero.Level = 6;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), "the ultimate is learnt at level six");
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "Malice and Spite is cast");
        Advance(match, 0.5);
        var minion = Enemy(match, "EvelynnKilledMinion", new Position2(match.Hero.Position.X + 120.0, match.Hero.Position.Z));
        match.Hero.Health = match.Hero.MaxHealth - 200.0;
        var before = match.Hero.Health;
        var cooldown = Cooldown(match, 3);
        match.Runtime.Combat.Kill(minion, match.Hero.NetId);
        TestAssert.Near(0.0, match.Hero.Health - before, 0.01, "a minion's death does not heal her");
        TestAssert.Near(cooldown, Cooldown(match, 3), 0.01, "and leaves the ultimate's cooldown alone");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.bb-evelynn-death-keeps-determined-killer-and-drops-malice-and-spite")]
    public static void DeathKeepsDeterminedKillerAndDropsMaliceAndSpite()
    {
        using var match = new Match("Evelynn", "Annie", "Ezreal", Team.Order);
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "Malice and Spite is cast");
        Advance(match, 0.5);
        TestAssert.True(Buff(match.Hero, "MaliceandSpite") is not null, "hasted before the death");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        Advance(match, 0.2);
        TestAssert.False(match.Hero.Alive, "dead");
        TestAssert.True(Buff(match.Hero, "SilentKiller") is not null, "Determined Killer persists through death (PersistsThroughDeath)");
        TestAssert.True(Buff(match.Hero, "MaliceandSpite") is null, "the haste does not persist through death");
        for (var step = 0; step < 1200 && !match.Hero.Alive; step++) Advance(match, 0.1);
        TestAssert.True(match.Hero.Alive, "respawns");
        TestAssert.True(Buff(match.Hero, "SilentKiller") is not null, "and the passive is on after the respawn");
        TestAssert.Equal("", Failures(match), "no script failure");
    }
}
