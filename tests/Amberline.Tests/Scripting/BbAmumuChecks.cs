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
/// Phase 3, wave 3 (19 October 2026): Amumu plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever existed). Expected numbers
/// are the literals of the shipped scripts and spell inibins (rank one unless a check learns more); the scenario <c>S-amumu</c> shows the same facts, and more,
/// on a headless match. Written 19 October 2026.
/// </summary>
public static class BbAmumuChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>Amumu facing a foe 600 units east (Bandage Toss, Curse) with every spell learnt at <paramref name="rank"/>, plain resistances on the foe.</summary>
    private static Match Duel(int rank = 1, double foeDistance = 600.0)
    {
        var match = new Match("Amumu");
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7000 + foeDistance, 7000);
        match.Ally.Position = new Position2(7100, 7100);
        match.Foe.Armor = 0.0;
        match.Foe.SpellBlock = 0.0;
        match.Refill();
        return match;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Amumu");
        TestAssert.Equal(16, module.ScriptNames.Count, "the Amumu closure is 16 scripts (audit bb-coverage)");
        foreach (var name in new[] { "CharScriptAmumu", "BandageToss", "SadMummyBandageToss", "AuraofDespair", "Tantrum", "CurseoftheSadMummy", "CursedTouch", "CursedTouchMarker" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Amumu");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");

        var hero = new Entity { ChampionName = "Amumu" };
        TestAssert.Equal(["BandageToss", "AuraofDespair", "Tantrum", "CurseoftheSadMummy"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Amumu.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks and an ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Amumu")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 1350, Id: 32, RetailBot: false }, "playable as a jungler for 1350 IP, not a retail bot");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Cursed Touch
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-cursed-touch-takes-15-25-or-35-magic-resist-for-four-seconds")]
    public static void CursedTouchFollowsTheLevelTable()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Position = new Position2(7000, 7000);
        foe.Position = new Position2(7080, 7000);
        TestAssert.True(Buff(hero, "CursedTouchMarker") is not null, "the marker is on him from the start");
        foe.SpellBlock = 30.0;
        foreach (var (level, reduction) in new[] { (1, 15.0), (7, 25.0), (13, 35.0) })
        {
            var need = Amberline.Simulation.Combat.ProgressionRules.ExperienceThreshold(level) - hero.Experience;
            if (need > 0.0) match.Runtime.Combat.AwardHeroProgress(hero, experience: need);
            TestAssert.Equal(level, hero.Level, "the level");
            foreach (var old in foe.Buffs.Where(buff => buff.Name == "CursedTouch").ToArray()) match.Runtime.Buffs.RemoveBuff(foe, old.Slot);
            match.Advance(1.1);
            foe.SpellBlock = 30.0;
            hero.AttackCooldown = 0.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "swing");
            match.Advance(0.8);
            var touch = Buff(foe, "CursedTouch") ?? throw new InvalidOperationException($"level {level}: the hit left no Cursed Touch ({Names(foe)})");
            TestAssert.Equal(BuffType.CombatDehancer, touch.Type, "a combat debuff");
            TestAssert.Near(4.0, touch.ExpiresAt, 0.9, "four seconds");
            match.Advance(0.2);
            TestAssert.Near(30.0 - reduction, foe.SpellBlock, 1e-6, $"level {level}: {reduction} off the magic resistance");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Bandage Toss
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-bandage-toss-pulls-him-to-the-unit-stuns-it-and-ends-at-the-landing")]
    public static void BandageTossPullsStunsAndEndsWithItsMove()
    {
        using var match = Duel();
        var hero = match.Hero;
        var foe = match.Foe;
        var health = foe.Health;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, at: foe.Position), "Q on the foe's spot is accepted");
        match.Advance(0.3);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        TestAssert.Near(16.0, hero.SpellCooldowns[0] + 0.3 - match.CastFrameOf(0), 0.1, "16 s at rank one");
        TestAssert.Near(80.0, mana - hero.Mana, 0.5, "80 mana at rank one");
        var sawMove = false;
        var sawBuff = false;
        for (var step = 0; step < 90; step++)
        {
            match.Advance(BbM3.Step);
            if (hero.ForcedMove is { Effect: "SadMummyBandageToss", Completed: false }) sawMove = true;
            sawBuff |= Buff(hero, "SadMummyBandageToss") is not null;
            if (sawMove && hero.ForcedMove is null) break;
        }
        TestAssert.True(sawMove, "he moves as the script says: BBMove at 1350 toward the unit");
        TestAssert.True(sawBuff, "the buff of the spell's own name holds the Spell2 loop");
        TestAssert.Near(80.0, HitsOn(match, "Foe").First().Raw, 1e-6, "80 magic damage at rank one");
        TestAssert.True(Buff(foe, "Stun") is not null, "stunned (BBApplyStun 1 s)");
        // The move is over: BuffOnMoveEnd removed the buff in the same step, and BuffOnMoveSuccess ordered the attack at the unit.
        match.Advance(BbM3.Step);
        TestAssert.True(Buff(hero, "SadMummyBandageToss") is null, "BuffOnMoveEnd removes the buff at the landing, not a second later");
        TestAssert.True(hero.HeroPursuit is { } pursuit && pursuit.TargetNetId == foe.NetId, "BuffOnMoveSuccess orders the attack at the hero he pulled himself to");
        // Restated in batch 659: the script moves him to LandPos, the unit's own position, with IdealDistance the gap between the bodies; that
        // length sizes the speed (BbNativeHandlers.Move) and caps nothing, so he lands on the unit, not a body short of it.
        TestAssert.Near(0.0, Math.Sqrt(Math.Pow(hero.Position.X - foe.Position.X, 2) + Math.Pow(hero.Position.Z - foe.Position.Z, 2)), 12.0, "he lands on the unit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + Failures(match));
    }

    [Check("scripting.bb-amumu-bandage-toss-into-a-spell-shield-pulls-him-and-nothing-else")]
    public static void BandageTossIntoAVeilStillPullsButNeitherHurtsNorStuns()
    {
        using var match = Duel();
        var foe = match.Foe;
        foe.Inventory = [new InventorySlot(3102), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(foe, push: false);
        match.Runtime.Buffs.AddBuff(foe, "BansheesVeil", BuffType.Aura, 30.0, foe.NetId, canMitigateDuration: false);
        TestAssert.True(match.Cast(0, at: foe.Position), "accepted");
        var moved = false;
        for (var step = 0; step < 90; step++)
        {
            match.Advance(BbM3.Step);
            moved |= match.Hero.ForcedMove is not null;
        }
        TestAssert.True(moved, "the script's BBMove runs before the shield matters: he still flies to the unit (retail's script order)");
        // The attack he is ordered to make at the landing (BuffOnMoveSuccess) is a plain swing; the spell's 80 never lands.
        TestAssert.True(HitsOn(match, "Foe").Where(hit => Math.Abs(hit.Raw - 80.0) < 1e-6).All(hit => hit.Dealt == 0.0), "the popped veil zeroes the spell's damage");
        TestAssert.True(Buff(foe, "Stun") is null, "and refuses the stun");
        TestAssert.True(Buff(foe, "BansheesVeilTimer") is not null, "the veil is the one that popped");
    }

    [Check("scripting.bb-amumu-bandage-toss-into-nothing-moves-nobody")]
    public static void BandageTossThatHitsNothingIsJustCooldownAndMana()
    {
        using var match = Duel(foeDistance: 3000.0);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: new Position2(7800, 7000)), "accepted");
        // Batch 293 (batch 279's seat): the cast's windup stop seats Amumu on his lattice anchor at once; he stays there.
        var start = match.Hero.Position;
        for (var step = 0; step < 120; step++)
        {
            match.Advance(BbM3.Step);
            TestAssert.True(match.Hero.ForcedMove is null, "nobody moves");
        }
        TestAssert.Near(80.0, mana - match.Hero.Mana, 8.0, "80 mana (less the regeneration of 4 s)");
        TestAssert.Near(start.X, match.Hero.Position.X, 1e-6, "he stayed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Despair
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-despair-pulses-each-second-costs-mana-and-ends-with-it")]
    public static void DespairIsAToggleThatDrainsMana()
    {
        using var match = Duel(foeDistance: 300.0);
        var hero = match.Hero;
        match.FarFoe.Position = new Position2(7380, 7000);
        hero.Mana = hero.MaxMana;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(1, hero), "W accepted");
        match.Advance(0.3);
        TestAssert.True(Buff(hero, "AuraofDespair") is not null, "the aura is up (BUFF_Aura, 25000 s)");
        // 8 + 1.5 percent (0.003 x rank + 0.012) of the foe's maximum health, which the script caps at 4500: 8 + 67.5 at rank one.
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the first strike is at once (OnBuffActivate)");
        TestAssert.Near(75.5, hits[0].Raw, 1e-4, "75.5 magic damage: 8 + 4500 x 0.015 (the health cap)");
        match.Advance(2.0);
        TestAssert.Equal(3, HitsOn(match, "Foe").Count, "then one strike a second");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "the unit at 380 is outside the 350 ring");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "an ally is not struck");
        TestAssert.Near(8.0 + 8.0 * 2.0, mana - hero.Mana, 8.0, "8 to cast and 8 a second (less regeneration)");
        // Pressing W again ends it.
        TestAssert.True(match.Cast(1, hero), "the second press is accepted");
        match.Advance(0.4);
        TestAssert.True(Buff(hero, "AuraofDespair") is null, "and the aura is gone");
        var before = HitsOn(match, "Foe").Count;
        match.Advance(2.0);
        TestAssert.Equal(before, HitsOn(match, "Foe").Count, "nothing strikes after it");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + Failures(match));
    }

    [Check("scripting.bb-amumu-despair-ends-when-the-mana-is-below-eight")]
    public static void DespairRunsOutOfMana()
    {
        using var match = Duel(foeDistance: 300.0);
        var hero = match.Hero;
        hero.Mana = 17.0;
        TestAssert.True(match.Cast(1, hero), "accepted with 17 mana");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "AuraofDespair") is not null, "up");
        match.Advance(2.3);
        TestAssert.True(Buff(hero, "AuraofDespair") is null, "the second update finds less than 8 and removes it");
        hero.Mana = 3.0;
        hero.SpellCooldowns[1] = 0.0;
        TestAssert.False(match.Cast(1, hero), "with 3 mana the press is refused");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Tantrum
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-tantrum-hits-the-ring-reduces-physical-damage-and-every-hit-cuts-its-cooldown")]
    public static void TantrumActiveAndPassive()
    {
        using var match = Duel(foeDistance: 300.0);
        var hero = match.Hero;
        match.FarFoe.Position = new Position2(7380, 7000);
        match.Advance(0.1);
        TestAssert.True(Buff(hero, "Tantrum") is not null, "the passive buff is added by CharScriptAmumu once E has a rank");
        var mana = hero.Mana;
        TestAssert.True(match.Cast(2, hero), "E accepted");
        match.Advance(0.5);
        // Batch 340: the cast frame pays, CastFrameOf after the press.
        TestAssert.Near(10.0, hero.SpellCooldowns[2] + 0.5 - match.CastFrameOf(2), 0.1, "10 s at rank one");
        TestAssert.Near(50.0, mana - hero.Mana, 0.5, "50 mana");
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the unit at 300 is hit");
        TestAssert.Near(75.0, HitsOn(match, "Foe")[0].Raw, 1e-4, "75 magic damage at rank one");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "the unit at 380 is outside the 350 ring");
        TestAssert.True(Buff(hero, "Tantrum") is not null, "the passive stays on after the cast");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + Failures(match));
    }

    [Check("scripting.bb-amumu-tantrum-takes-two-off-each-physical-hit-and-half-a-second-off-the-cooldown")]
    public static void TantrumPassiveReducesAndCutsTheCooldown()
    {
        using var match = Duel(foeDistance: 90.0);
        var hero = match.Hero;
        var foe = match.Foe;
        hero.Armor = 0.0;
        hero.Health = hero.MaxHealth;
        foe.CritChance = 0.0;
        hero.SpellCooldowns[2] = 6.0;
        foe.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(foe, hero), "the foe swings");
        var cooldown = hero.SpellCooldowns[2];
        match.Advance(1.2);
        // The foe's passive adds a proc hit of its own with the swing; the plain attack is the hit Tantrum reduces.
        var swings = HitsOn(match, "Amumu").Where(hit => hit.Kind == "Basic").ToList();
        TestAssert.Equal(1, swings.Count, "one swing landed: " + string.Join(" | ", match.Log.Hits));
        var swing = swings[0];
        TestAssert.Near(swing.Raw - 2.0, swing.Dealt, 0.05, "two points off the physical hit (Tantrum rank one)");
        TestAssert.Near(cooldown - 0.5 - 1.2, hero.SpellCooldowns[2], 0.1, "half a second off the cooldown on top of the clock");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Curse of the Sad Mummy
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-curse-nets-the-ring-for-two-seconds-and-spares-allies")]
    public static void CurseDamagesAndEntanglesTheRing()
    {
        using var match = Duel(foeDistance: 300.0);
        var hero = match.Hero;
        match.FarFoe.Position = new Position2(7650, 7000);
        hero.Mana = hero.MaxMana;
        var mana = hero.Mana;
        TestAssert.True(match.Cast(3, hero), "R accepted");
        match.Advance(0.5);
        // Batch 340: the cast frame pays, CastFrameOf after the press.
        TestAssert.Near(170.0, hero.SpellCooldowns[3] + 0.5 - match.CastFrameOf(3), 0.1, "170 s at rank one");
        TestAssert.Near(100.0, mana - hero.Mana, 0.5, "100 mana at rank one");
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the unit at 300 is struck once");
        TestAssert.Near(150.0, HitsOn(match, "Foe")[0].Raw, 1e-4, "150 magic damage at rank one");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "the unit at 650 is outside the 600 ring");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count, "an ally is spared");
        var net = Buff(match.Foe, "CurseoftheSadMummy") ?? throw new InvalidOperationException("no net on the foe: " + Names(match.Foe));
        TestAssert.Equal(BuffType.Net, net.Type, "BUFF_Net");
        TestAssert.Near(2.0, net.ExpiresAt, 0.55, "two seconds");
        var state = CharacterState.Of(match.Foe);
        TestAssert.False(state.CanMove, "it cannot move");
        TestAssert.False(state.CanAttack, "it cannot attack");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "CurseoftheSadMummy") is null, "the net is gone after its two seconds");
        TestAssert.True(CharacterState.Of(match.Foe).CanMove && CharacterState.Of(match.Foe).CanAttack, "and both are given back");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-amumu-death-ends-despair-and-keeps-tantrum-and-cursed-touch")]
    public static void DeathKeepsThePassivesAndDropsTheAura()
    {
        using var match = Duel(foeDistance: 300.0);
        var hero = match.Hero;
        TestAssert.True(match.Cast(1, hero), "W");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "AuraofDespair") is not null, "the aura is up");
        TestAssert.True(match.Runtime.Combat.Kill(hero, match.Foe.NetId), "killed");
        match.Advance(0.2);
        TestAssert.True(Buff(hero, "AuraofDespair") is null, "Despair does not persist through death");
        TestAssert.True(Buff(hero, "Tantrum") is not null, "Tantrum (PersistsThroughDeath)");
        TestAssert.True(Buff(hero, "CursedTouchMarker") is not null, "the Cursed Touch marker (PersistsThroughDeath)");
    }
}
