using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 9 (26 October 2026, batch 342): Malzahar plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are
/// the literals of the shipped scripts and spell inibins (the dump of the closure, <c>AlZaharCalloftheVoid</c> and the rest); the scenario
/// <c>S-malzahar</c> shows the same facts on a headless match. Call of the Void's two portals stand at the ends of a 750 line through the target
/// point, perpendicular to the aim, and fire at 0.4 seconds. The voidlings run <c>UncontrollablePet.lua</c> (AI, Reconstruction on the acquisition).
/// </summary>
public static class BbMalzaharChecks
{
    private static readonly double[] CallDamage = [40.0, 67.5, 95.0, 122.5, 150.0];
    private static readonly double[] CallSilence = [1.4, 1.8, 2.2, 2.6, 3.0];
    private static readonly double[] ZonePercent = [0.04, 0.05, 0.06, 0.07, 0.08];
    private static readonly double[] CurseDamage = [10.0, 17.5, 25.0, 32.5, 40.0];
    private static readonly double[] CurseRefund = [10.0, 14.0, 18.0, 22.0, 26.0];
    private static readonly double[] GraspDamage = [50.0, 80.0, 110.0];

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static double Gap(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Malzahar at rank one everywhere (rank three of the ultimate is the same script), 80 ability power, the Chaos Orianna 300 east of him.</summary>
    private static Match Arena()
    {
        var match = new Match("Malzahar");
        match.Learn(1);
        match.GiveAbilityPower();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 30.0;
        match.FarFoe.Position = new Position2(12000, 12000);
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    // A portal is the script's 'TestCubeRender' unit (the Name the spawn gives its entity is the skin's); a voidling is the unit that carries the
    // AlZaharVoidling buff its summon adds.
    private static List<Entity> Portals(Match match) =>
        match.Runtime.World.Entities.Values.Where(entity => entity.Name == "TestCubeRender" && entity.Alive).ToList();

    private static List<Entity> Voidlings(Match match) =>
        match.Runtime.World.Entities.Values.Where(entity => entity.Alive && Buff(entity, "AlZaharVoidling") is not null).ToList();

    /// <summary>Casts a slot the way its script takes it: Malefic Visions is a unit target, the others go to a point.</summary>
    private static bool Cast(Match match, int slot) =>
        slot == 2 ? match.Cast(slot, match.Foe) : match.Cast(slot, at: match.Foe.Position);

    /// <summary>Four casts (Null Zone, Call of the Void, Malefic Visions, Null Zone) with the cooldowns refilled: the fourth one summons the voidling.</summary>
    private static Entity? Summon(Match match)
    {
        foreach (var slot in new[] { 1, 0, 2, 1 })
        {
            match.Refill();
            TestAssert.True(Cast(match, slot), $"slot {slot} is accepted");
            match.Advance(0.5);
        }
        return Voidlings(match).SingleOrDefault();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malzahar-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Malzahar");
        foreach (var name in new[]
                 {
                     "CharScriptMalzahar", "AlZaharCalloftheVoid", "AlZaharCalloftheVoidMissile", "AlZaharNullZone", "AlZaharMaleficVisions",
                     "AlZaharNetherGrasp", "AlZaharSummonVoidling", "AlZaharVoidlingCount", "AlZaharVoidling", "AlZaharVoidlingDetonation",
                     "AlZaharVoidlingPhase2", "AlZaharVoidlingPhase3", "Suppression",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Malzahar");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(28, closure.ScriptNames.Count(), "the closure is 28 scripts: his character script, four spells, the passive, the voidling's buffs and the shared system buffs");

        var hero = new Entity { ChampionName = "Malzahar" };
        TestAssert.Equal(new[] { "AlZaharCalloftheVoid", "AlZaharNullZone", "AlZaharMaleficVisions", "AlZaharNetherGrasp" },
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Malzahar.inibin");
        TestAssert.Equal(new[] { 5, 5, 5, 3 }, Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three spells of five ranks, the ultimate of three");
        TestAssert.Equal(new[] { 1, 3, 5, 7, 9 }, Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(),
            "Call of the Void is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal(new[] { 6, 11, 16 }, Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(),
            "Nether Grasp is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Malzahar")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 6300, RetailBot: false }, "playable as a mid for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Malzahar"), "listed once");

        // The tables the figures rest on, a second time as literals read from the scripts.
        TestAssert.Equal(CallDamage, ByLevel("AlZaharCalloftheVoidMissile", "TargetExecute", "BaseDamage"), "Call of the Void damage by rank");
        TestAssert.Equal(CallSilence, ByLevel("AlZaharCalloftheVoidMissile", "TargetExecute", "SilenceDuration"), "Call of the Void silence by rank");
        TestAssert.Equal(ZonePercent, ByLevel("AlZaharNullZone", "SelfExecute", "HealthPercent"), "Null Zone percent of maximum health by rank");
        TestAssert.Equal(CurseDamage, ByLevel("AlZaharMaleficVisions", "BuffOnUpdateActions", "DamagePerTick"), "Malefic Visions tick by rank");
        TestAssert.Equal(CurseRefund, ByLevel("AlZaharMaleficVisions", "BuffOnDeath", "ManaRestore"), "Malefic Visions mana refund by rank");
        TestAssert.Equal(GraspDamage, ByLevel("AlZaharNetherGrasp", "ChannelingStart", "DamageToDeal"), "Nether Grasp hit by rank");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Call of the Void (Q)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malzahar-call-of-the-void-fires-two-portals-from-a-line-through-the-target")]
    public static void CallOfTheVoidFiresFromTwoPortalsOnALine()
    {
        using var match = Arena();
        var ap = match.Hero.AbilityPower;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Call of the Void is accepted");
        match.Advance(0.3); // the cast takes a quarter of a second; the portals stand from then until 0.4 seconds later
        var portals = Portals(match);
        TestAssert.Equal(2, portals.Count, "two portals stand");
        var first = portals[0].Position;
        var last = portals[1].Position;
        // The line is 750 long with fifteen points 50 apart; its first point is one step in (BBForEachPointOnLine reads the client's point loop:
        // the step is added before the first test), so the two portals stand 700 apart, 25 off the target's centre.
        TestAssert.Near(700.0, Gap(first, last), 1.0, "the portals are the first and last of fifteen points 50 apart");
        TestAssert.Near(match.Foe.Position.X, first.X, 5.0, "the line is perpendicular to the aim: both portals stand on the target's column");
        TestAssert.Near(match.Foe.Position.X, last.X, 5.0, "the second portal stands on the same column");
        TestAssert.True(Math.Abs((first.Z + last.Z) / 2.0 - 7000.0) <= 50.0, "the line is centred on the target point, within one step");
        TestAssert.Near(mana - 80.0, match.Hero.Mana, 2.0, "rank one costs 80 mana");

        match.Advance(1.0); // the portals fire at 0.4 seconds: each missile flies to the target point and crosses it
        TestAssert.Equal(0, Portals(match).Count, "the portals are gone once they have fired");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(2, hits.Count, "one missile from each portal crosses the target point");
        var expected = Mitigated(CallDamage[0] + 0.4 * ap, match.Foe.SpellBlock);
        foreach (var hit in hits)
            TestAssert.Near(expected, hit.Dealt, 0.05, "each missile deals 40 plus 0.4 ability power, before resistance");
        TestAssert.True(Buff(match.Foe, "Silence") is not null, "a hero hit by a missile is silenced");
        match.Advance(3.5);
        TestAssert.True(Buff(match.Foe, "Silence") is null, "the silence of rank one is over within 1.4 seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Null Zone (W)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malzahar-null-zone-takes-a-share-of-maximum-health-each-second")]
    public static void NullZoneTakesAShareOfMaximumHealthEachSecond()
    {
        using var match = Arena();
        var ap = match.Hero.AbilityPower;
        var minion = Enemy(match, "Minion", new Position2(7300, 7050));
        minion.MaxHealth = 5000.0;
        minion.Health = 5000.0;
        var maxHealth = match.Foe.MaxHealth;
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "Null Zone is accepted");
        match.Advance(5.5);

        // The zone ticks at once and then each second for its five seconds (the script: BuffOnUpdateActions, ExecuteImmediately).
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(5, hits.Count, "five ticks: one as the zone lands and one each second");
        var expected = Mitigated(maxHealth * (ZonePercent[0] + 0.0001 * ap), match.Foe.SpellBlock);
        foreach (var hit in hits)
            TestAssert.Near(expected, hit.Dealt, 0.05, "a hero loses 4 percent of her maximum health plus 0.01 percent per point of ability power, before resistance");

        // A minion or a monster takes at most 120 of a tick (MO_MIN before the damage is applied).
        var minionHits = HitsOn(match, "Minion");
        TestAssert.Equal(5, minionHits.Count, "a minion in the zone is ticked five times too");
        foreach (var hit in minionHits)
            TestAssert.Near(Mitigated(120.0, minion.SpellBlock), hit.Dealt, 0.05, "a minion's tick is capped at 120 before resistance");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Malefic Visions (E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malzahar-malefic-visions-ticks-every-0-45-seconds-for-four-seconds")]
    public static void MaleficVisionsTicksEvery045SecondsForFourSeconds()
    {
        using var match = Arena();
        var ap = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(2, match.Foe), "Malefic Visions is accepted");
        match.Advance(4.5);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(8, hits.Count, "eight ticks: one every 0.45 seconds for four seconds");
        var expected = Mitigated(CurseDamage[0] + 0.1 * ap, match.Foe.SpellBlock);
        foreach (var hit in hits)
            TestAssert.Near(expected, hit.Dealt, 0.05, "each tick is 10 plus 0.1 ability power, before resistance");
        TestAssert.True(Buff(match.Foe, "AlZaharMaleficVisions") is null, "the curse is over with its four seconds");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-malzahar-malefic-visions-jumps-to-a-nearby-enemy-and-refunds-mana-when-its-carrier-dies")]
    public static void MaleficVisionsJumpsAndRefundsWhenItsCarrierDies()
    {
        using var match = Arena();
        match.FarFoe.Position = new Position2(7550, 7150); // 290 from the carrier: the nearest enemy that is not marked
        var ap = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(2, match.Foe), "Malefic Visions is accepted");
        match.Advance(1.0);
        var before = match.Hero.Mana;
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        TestAssert.Near(CurseRefund[0], match.Hero.Mana - before, 0.05, "the carrier's death refunds ten mana at rank one");
        TestAssert.True(Buff(match.FarFoe, "AlZaharMaleficVisions") is not null, "the curse jumps to the enemy nearest the carrier");
        TestAssert.True(Buff(match.FarFoe, "AlZaharRecentVis") is not null, "and that enemy is marked as visited");
        match.Advance(4.5);
        var hits = HitsOn(match, "FarFoe");
        TestAssert.Equal(8, hits.Count, "the jumped curse ticks for four seconds");
        var expected = Mitigated(CurseDamage[0] + 0.1 * ap, match.FarFoe.SpellBlock);
        foreach (var hit in hits)
            TestAssert.Near(expected, hit.Dealt, 0.05, "the jumped curse ticks at the same rank figures");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Nether Grasp (R)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malzahar-nether-grasp-hits-five-times-and-suppresses-for-the-channel")]
    public static void NetherGraspHitsFiveTimesWhileSuppressing()
    {
        using var match = Arena();
        var ap = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(3, match.Foe), "Nether Grasp is accepted");
        match.Advance(0.6); // the channel begins 0.25 seconds after the press
        TestAssert.True(Buff(match.Foe, "Suppression") is not null, "the target is suppressed while it is held");
        TestAssert.True(Buff(match.Foe, "AlZaharNetherGrasp") is not null, "and carries the channel's curse");
        match.Advance(2.4);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(5, hits.Count, "five hits: one when the channel lands and four every half second");
        var expected = Mitigated(GraspDamage[0] + 0.26 * ap, match.Foe.SpellBlock);
        foreach (var hit in hits)
            TestAssert.Near(expected, hit.Dealt, 0.05, "each hit is 50 plus 0.26 ability power, before resistance");
        TestAssert.True(Buff(match.Foe, "Suppression") is null && Buff(match.Foe, "AlZaharNetherGrasp") is null,
            "both end with the channel");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-malzahar-nether-grasp-ends-when-its-target-leaves-1500")]
    public static void NetherGraspEndsWhenItsTargetIsOutOfReach()
    {
        using var match = Arena();
        TestAssert.True(match.Cast(3, match.Foe), "Nether Grasp is accepted");
        match.Advance(0.6);
        match.Foe.Position = new Position2(8700, 7000); // 1700 from Malzahar
        match.Advance(0.5);
        TestAssert.True(Buff(match.Foe, "AlZaharNetherGrasp") is null && Buff(match.Foe, "Suppression") is null,
            "the channel and its suppression end out of reach");
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "only the hit that landed when the channel began");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Summon Voidling (passive), the voidling
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malzahar-every-fourth-spell-summons-a-voidling")]
    public static void EveryFourthSpellSummonsAVoidling()
    {
        using var match = Arena();
        foreach (var slot in new[] { 1, 0, 2 })
        {
            match.Refill();
            TestAssert.True(Cast(match, slot), $"slot {slot} is accepted");
            match.Advance(0.5);
        }
        TestAssert.Equal(0, Voidlings(match).Count, "three spells summon nothing");
        TestAssert.True(Buff(match.Hero, "AlZaharSummonVoidling") is not null, "the third spell readies the next one");
        match.Refill();
        TestAssert.True(Cast(match, 1), "the fourth spell is accepted");
        match.Advance(0.5);
        var voidlings = Voidlings(match);
        TestAssert.Equal(1, voidlings.Count, "the fourth spell summons one voidling");
        TestAssert.True(voidlings[0].Team == match.Hero.Team, "on his team");
        TestAssert.True(Buff(match.Hero, "AlZaharSummonVoidling") is null, "the ready state is spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-malzahar-voidling-grows-at-seven-and-fourteen-seconds-and-expires-at-twenty-one")]
    public static void VoidlingGrowsAndExpires()
    {
        using var match = Arena();
        var voidling = Summon(match);
        TestAssert.True(voidling is not null, "a voidling exists");
        // Level one: the summon adds 50 health and 5 attack damage (BonusHealth = 50 x level, BonusDamage = 5 x level) to the shipped voidling's
        // 200 health and 20 attack damage (MalzaharVoidling.inibin, level one).
        TestAssert.Near(250.0, voidling!.MaxHealth, 0.05, "the voidling starts with its shipped 200 health and the summon's 50 bonus");
        TestAssert.Near(25.0, voidling.AttackDamage, 0.05, "and its shipped 20 attack damage plus the summon's 5 bonus");
        // The clock starts when the summon returns, half a second after the cast. Phase two is due 6.5 seconds after the cast (the script's timer
        // counts half-second steps from the first one), phase three at 13.5, the voidling expires at 21: the checks sit 0.4 seconds clear of each.
        match.Advance(5.5);
        TestAssert.True(Buff(voidling, "AlZaharVoidlingPhase2") is null, "phase two is not yet there 5.5 seconds in");
        match.Advance(1.5);
        TestAssert.True(Buff(voidling, "AlZaharVoidlingPhase2") is not null, "phase two is there 7 seconds in");
        TestAssert.Near(37.5, voidling.AttackDamage, 0.05, "half again its attack damage: 25 plus half of the 25 it had");
        match.Advance(5.5);
        TestAssert.True(Buff(voidling, "AlZaharVoidlingPhase3") is null, "phase three is not yet there 12.5 seconds in");
        match.Advance(1.5);
        TestAssert.True(Buff(voidling, "AlZaharVoidlingPhase3") is not null, "phase three is there 14 seconds in");
        match.Advance(5.5);
        TestAssert.True(voidling.Alive, "the voidling is alive 19.5 seconds in");
        match.Advance(2.0);
        TestAssert.True(!voidling.Alive, "the voidling dies when its 21 seconds are over (21.5 seconds in)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-malzahar-voidlings-die-with-their-owner")]
    public static void VoidlingsDieWithTheirOwner()
    {
        using var match = Arena();
        var voidling = Summon(match);
        TestAssert.True(voidling is not null, "a voidling exists");
        match.Advance(1.0);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(0.5);
        TestAssert.True(!voidling!.Alive, "the death of Malzahar detonates his voidlings");
        TestAssert.Equal(0, Voidlings(match).Count, "none is left");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
