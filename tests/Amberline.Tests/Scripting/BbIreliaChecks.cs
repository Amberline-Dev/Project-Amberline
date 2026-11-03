using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 3 (26 October 2026, batch 272): Irelia plays from her shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the
/// literals of the shipped scripts and spell inibins; the scenario <c>S-Irelia</c> shows the same facts on a headless match. Ionian Fervor's
/// reduction is the character script's own <c>BuffOnAllowAdd</c>, which the engine did not ask before batch 272 (the Ionian Fervor check was red then).
/// </summary>
public static class BbIreliaChecks
{
    private static int Stacks(Entity unit, string name) => Buff(unit, name)?.Stacks ?? 0;

    [Check("scripting.bb-irelia-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Irelia");
        foreach (var name in new[] { "CharScriptIrelia", "IreliaGatotsu", "IreliaHitenStyle", "IreliaEquilibriumStrike", "IreliaTranscendentBlades", "IreliaTranscendentBladesSpell", "IreliaIonianDuelist" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Irelia");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(24, closure.ScriptNames.Count(), "the closure is 24 scripts");

        var hero = new Entity { ChampionName = "Irelia" };
        TestAssert.Equal(["IreliaGatotsu", "IreliaHitenStyle", "IreliaEquilibriumStrike", "IreliaTranscendentBlades"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Irelia.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Bladesurge is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "Transcendent Blades is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Irelia")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 6300, RetailBot: false }, "playable as a top laner for 6300 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Irelia"), "listed once");
    }

    // Ionian Fervor: CharScriptIrelia's BuffOnAllowAdd multiplies the duration of an enemy's disable by CharVars.CCReduction, which her periodic
    // UpdateSelfBuffStats sets from the enemy champions she can see within 1400 (1 - 0.1, 1 - 0.25 and 1 - 0.4 for one, two and three or more).
    [Check("scripting.bb-irelia-ionian-fervor-cuts-an-enemy-disable-by-the-enemy-count")]
    public static void IonianFervorShortensEnemyDisables()
    {
        using var match = new Match("Irelia", foeChampion: "Ezreal");
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(hero.Position.X + 4000.0, hero.Position.Z);
        match.Advance(1.5);
        match.Runtime.Buffs.AddBuff(hero, "ProbeStun", BuffType.Stun, 2.0, foe.NetId, canMitigateDuration: true);
        TestAssert.Near(2.0, Buff(hero, "ProbeStun")!.ExpiresAt, 1e-6, "no enemy champion within 1400: the stun keeps its two seconds");
        match.Advance(3.0);

        foe.Position = new Position2(hero.Position.X + 900.0, hero.Position.Z);
        match.Advance(1.5);
        match.Runtime.Buffs.AddBuff(hero, "ProbeStun", BuffType.Stun, 2.0, foe.NetId, canMitigateDuration: true);
        TestAssert.Near(1.8, Buff(hero, "ProbeStun")!.ExpiresAt, 1e-6, "one enemy champion near: 0.9 of the two seconds");
        TestAssert.Equal(1, Stacks(hero, "IreliaIonianDuelist"), "one aura stack for one enemy");
    }

    [Check("scripting.bb-irelia-bladesurge-kill-resets-the-cooldown-and-refunds-mana")]
    public static void BladesurgeKillResetsItsCooldown()
    {
        using var match = new Match("Irelia", foeChampion: "Ezreal");
        match.Learn(1);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(hero.Position.X + 400.0, hero.Position.Z);
        foe.Health = 5.0;
        match.Refill();
        var mana = hero.Mana;
        TestAssert.True(match.Cast(0, foe), "Bladesurge is accepted on a unit 400 away");
        match.Advance(1.5);
        TestAssert.False(foe.Alive, "the dash ends the foe");
        TestAssert.Equal(0.0, hero.SpellCooldowns[0], "a kill gives the cooldown back");
        TestAssert.Near(mana - 60.0 + 35.0, hero.Mana, 3.0, "60 mana spent, 35 back");
    }

    [Check("scripting.bb-irelia-equilibrium-strike-stuns-when-her-health-is-lower-and-slows-otherwise")]
    public static void EquilibriumStrikeStunsOrSlows()
    {
        using var match = new Match("Irelia", foeChampion: "Ezreal");
        match.Learn(1);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(hero.Position.X + 300.0, hero.Position.Z);
        foe.SpellBlock = 30.0;

        // Her half against the foe's full: the foe's percentage is the higher, so the strike stuns for one second.
        hero.Health = hero.MaxHealth * 0.5;
        foe.Health = foe.MaxHealth;
        match.Refill();
        var before = foe.Health;
        TestAssert.True(match.Cast(2, foe), "Equilibrium Strike is accepted");
        match.Advance(0.25);
        TestAssert.True(Buff(foe, "Stun") is not null, "the stun comes when her percentage is the lower");
        TestAssert.Near(Mitigated(80.0, 30.0), before - foe.Health, 2.0, "80 magic damage at rank one, less the foe's resistance");

        // Her full against the foe's half: the foe's percentage is the lower, so the strike slows instead.
        match.Advance(8.0);
        hero.Health = hero.MaxHealth;
        foe.Health = foe.MaxHealth * 0.5;
        match.Refill();
        TestAssert.True(match.Cast(2, foe), "Equilibrium Strike is accepted again");
        match.Advance(0.25);
        TestAssert.True(Buff(foe, "Slow") is not null && Buff(foe, "Stun") is null, "the slow comes when her percentage is the higher");
    }

    [Check("scripting.bb-irelia-transcendent-blades-four-charges-then-one-blade-a-press-and-a-cooldown-when-spent")]
    public static void TranscendentBladesSpendsItsCharges()
    {
        using var match = new Match("Irelia", foeChampion: "Ezreal");
        match.Learn(1);
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(hero.Position.X + 600.0, hero.Position.Z);
        match.Refill();
        var mana = hero.Mana;
        TestAssert.True(match.Cast(3), "the first press is accepted");
        match.Advance(0.5);
        TestAssert.Equal(4, Stacks(hero, "IreliaTranscendentBladesSpell"), "four charges from the first press, and no blade is thrown");
        TestAssert.Near(mana - 100.0, hero.Mana, 3.0, "100 mana at rank one");

        var aim = new Position2(hero.Position.X + 600.0, hero.Position.Z);
        for (var press = 0; press < 4; press++)
        {
            TestAssert.True(match.Cast(3, at: aim), $"blade {press + 1} is accepted");
            match.Advance(1.2);
        }
        TestAssert.True(Buff(hero, "IreliaTranscendentBlades") is null, "the spent charges end the buff");
        TestAssert.True(hero.SpellCooldowns[3] > 55.0 && hero.SpellCooldowns[3] <= 60.0, "the cooldown is 60 seconds at rank one, less the time since the last blade");
    }
}
