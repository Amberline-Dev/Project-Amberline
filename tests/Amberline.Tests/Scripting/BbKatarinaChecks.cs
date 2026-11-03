using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 266 (26 October 2026, wave 4): Katarina plays from her shipped scripts (<c>CharScriptKatarina</c>, the four spell scripts and their children, the
/// buffs). The numbers are the literals of Katarina.inibin and the bytecode listings of the scripts (BouncingBlades, KillerInstinct, ShadowStep,
/// DeathLotus, DeathLotusMissile, Voracity, GrievousWound, ShadowStepDodge). The scenario <c>S-katarina</c> shows every probe on a headless match. Written 26 October 2026.
/// </summary>
public static class BbKatarinaChecks
{
    [Check("scripting.bb-katarina-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Katarina");
        foreach (var name in new[] { "CharScriptKatarina", "BouncingBlades", "KillerInstinct", "ShadowStep", "DeathLotus", "DeathLotusMissile",
                     "Voracity", "GrievousWound", "ShadowStepDodge", "KillerInstinctBuff" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Katarina");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");

        var hero = new Entity { ChampionName = "Katarina" };
        TestAssert.Equal(["BouncingBlades", "KillerInstinct", "ShadowStep", "DeathLotus"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Katarina's Data*Spell1..4");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "Q, W and E have five ranks and Death Lotus three (the spellbook limits; Katarina ships no Data*MaxLevels)");
        TestAssert.Equal(3, module.RankGate(hero, 0, 2), "Bouncing Blades' second rank is learnt at level 3 (the scenario's L3 gate)");
        TestAssert.Equal(1, module.RankGate(hero, 1, 1), "Killer Instinct's first rank is gated at level one (the scenario's level-one refusal was the spent point)");
        TestAssert.Equal(6, module.RankGate(hero, 3, 1), "Death Lotus is learnt at level 6");

        var info = ChampionCatalog.ByName("Katarina")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, Id: 55, RetailBot: false },
            "playable as a mid for 3150 IP (store-prices-2011.json id 55), not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Katarina"), "listed once");
    }

    /// <summary>
    /// BouncingBlades.luaobj TargetExecute: BBBaseDamage 60 at rank one, plus 0.8 x the bonus attack damage, plus the Killer Instinct rank bonus (8 at Killer
    /// Instinct rank one, read from slot 1), magic with SpellDamageRatio 0.35. Without ability power or bonus attack damage the first hit is 68 raw.
    /// </summary>
    [Check("scripting.bb-katarina-bouncing-blades-hits-for-the-base-and-the-killer-instinct-rank")]
    public static void BladesHitForTheBaseAndTheKillerInstinctRank()
    {
        var match = new Match("Katarina", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.Hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(7400, 7000);
            match.Foe.SpellBlock = 30.0;
            match.Refill();
            match.Advance(0.5);
            var from = new Position3(7000, 0, 7000);
            var aim = new Position3(7400, 0, 7000);
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(0, false, from, aim, match.Foe.NetId), match.Now),
                "Bouncing Blades casts at the foe");
            match.Advance(1.5);
            var hits = HitsOn(match, "Foe");
            TestAssert.Equal(1, hits.Count, "one hit on the foe (the chain stops with no second enemy near)");
            NearF32(68.0, hits[0].Raw, "BBBaseDamage 60 + the Killer Instinct rank bonus 8 (no ability power, no bonus attack damage)");
            TestAssert.Near(Mitigated(hits[0].Raw, 30.0), hits[0].Dealt, 1e-6, "magic damage, mitigated by the foe's 30 magic resist");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// KillerInstinct.luaobj adds its buff (15 s, no damage) to the caster; BouncingBlades.TargetExecute then spends that buff for KillerInstinctBuff2 (4 s)
    /// and its hit marks the target with Internal_50MS and GrievousWound (5 s each): the empowered blade is the one that halves the target's healing.
    /// </summary>
    [Check("scripting.bb-katarina-killer-instinct-empowers-the-next-blades-with-grievous-wound")]
    public static void KillerInstinctEmpowersTheNextBladesWithGrievousWound()
    {
        var match = new Match("Katarina", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.Hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(7400, 7000);
            match.Refill();
            match.Advance(0.5);
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(1, false, new Position3(7000, 0, 7000),
                new Position3(7000, 0, 7000), match.Hero.NetId), match.Now), "Killer Instinct casts on Katarina");
            match.Advance(0.5);
            TestAssert.True(HasBuff(match.Hero, "KillerInstinct"), "the Killer Instinct buff is on Katarina for 15 s");
            TestAssert.False(HasBuff(match.Foe, "GrievousWound"), "no mark before the blade");

            match.Refill();
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(0, false, new Position3(7000, 0, 7000),
                new Position3(7400, 0, 7000), match.Foe.NetId), match.Now), "the empowered Bouncing Blades casts at the foe");
            match.Advance(1.5);
            TestAssert.True(HasBuff(match.Foe, "GrievousWound") && HasBuff(match.Foe, "Internal_50MS"),
                "the empowered blade marks the foe with GrievousWound and Internal_50MS");
            TestAssert.True(HasBuff(match.Hero, "KillerInstinctBuff2") && !HasBuff(match.Hero, "KillerInstinct"),
                "the blade spends Killer Instinct for KillerInstinctBuff2");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// ShadowStep.luaobj TargetExecute: Katarina faces the foe and blinks to the point the gap between the bodies plus 225 units along her facing (the
    /// landing is about 395 from her start when the foe stands 300 away), then deals 80 magic at rank one plus the Killer Instinct rank bonus and orders
    /// her attack on the hero. With Killer Instinct up, the spell adds ShadowStepDodge (3 s) and spends the buff.
    /// </summary>
    [Check("scripting.bb-katarina-shunpo-blinks-past-the-foe-and-spends-killer-instinct-for-a-dodge")]
    public static void ShunpoBlinksPastTheFoeAndSpendsKillerInstinct()
    {
        var match = new Match("Katarina", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.Hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(7300, 7000);
            match.Foe.SpellBlock = 30.0;
            match.Refill();
            match.Advance(0.5);
            var start = match.Hero.Position;
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(2, false, new Position3(7000, 0, 7000),
                new Position3(7300, 0, 7000), match.Foe.NetId), match.Now), "Shunpo casts at the foe");
            match.Advance(0.3);
            var moved = Distance(start, match.Hero.Position);
            TestAssert.True(moved > 380.0 && moved < 410.0, $"Shunpo lands about 395 from her start, past the foe ({moved:0.#})");
            var first = HitsOn(match, "Foe")[0];
            NearF32(88.0, first.Raw, "80 at rank one + the Killer Instinct rank bonus 8 (the basic attack the blink orders comes later)");
            TestAssert.Near(Mitigated(88.0, 30.0), first.Dealt, 1e-6, "magic damage, mitigated by the foe's 30 magic resist");

            match.Refill();
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(1, false, new Position3(7000, 0, 7000),
                new Position3(7000, 0, 7000), match.Hero.NetId), match.Now), "Killer Instinct casts again");
            match.Advance(0.3);
            match.Refill();
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(2, false,
                new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z),
                new Position3(match.Foe.Position.X, 0, match.Foe.Position.Z), match.Foe.NetId), match.Now), "Shunpo casts with Killer Instinct up");
            match.Advance(0.3);
            TestAssert.True(HasBuff(match.Hero, "ShadowStepDodge"), "the dodge is on Katarina for 3 s");
            TestAssert.False(HasBuff(match.Hero, "KillerInstinct"), "Shunpo spends Killer Instinct");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// DeathLotus.luaobj CanCastBuildingBlocks: the cast needs an enemy hero within 550 (a random enemy in the area). Its channel (2.65 s) adds the
    /// DeathLotus buff, which ticks every 0.25 s and casts DeathLotusMissile from the extra slot at the three nearest enemy heroes within 550; each
    /// dagger deals 50 at rank one plus the Killer Instinct rank bonus (8) and the bonus attack damage half-share (none here), magic.
    /// </summary>
    [Check("scripting.bb-katarina-death-lotus-needs-an-enemy-in-range-and-throws-daggers-each-quarter-second")]
    public static void DeathLotusNeedsAFoeAndThrowsDaggers()
    {
        var match = new Match("Katarina", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.Hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(8000, 7000);
            match.Foe.SpellBlock = 30.0;
            match.Refill();
            match.Advance(0.5);
            TestAssert.False(match.Runtime.Spells.CanCast(match.Hero, 3, match.Hero.NetId, new Position3(7000, 0, 7000), match.Now).Allowed,
                "Death Lotus needs an enemy hero within 550 (CanCastBuildingBlocks)");

            match.Foe.Position = new Position2(7400, 7000);
            match.Refill();
            match.Advance(0.5);
            TestAssert.True(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(3, false, new Position3(7000, 0, 7000),
                new Position3(7000, 0, 7000), match.Hero.NetId), match.Now), "Death Lotus casts with the foe 400 away");
            match.Advance(3.0);
            var hits = HitsOn(match, "Foe");
            TestAssert.True(hits.Count >= 9 && hits.Count <= 12, $"the channel's ticks throw about ten daggers at the foe ({hits.Count})");
            NearF32(58.0, hits[0].Raw, "DaggerBase 50 at rank one + the Killer Instinct rank bonus 8");
            TestAssert.Near(Mitigated(58.0, 30.0), hits[0].Dealt, 1e-6, "magic damage, mitigated by the foe's 30 magic resist");
        }
        finally
        {
            match.Dispose();
        }
    }

    /// <summary>
    /// Voracity.luaobj BuffOnKill (a champion kill): Q, W and E cooldowns go to zero, Death Lotus loses fifteen seconds, and the killer gets 25 gold.
    /// </summary>
    [Check("scripting.bb-katarina-voracity-kill-resets-the-basic-spells-and-cuts-death-lotus")]
    public static void VoracityKillResetsTheBasicSpellsAndCutsDeathLotus()
    {
        var match = new Match("Katarina", foeChampion: "Orianna");
        try
        {
            match.Learn(1);
            match.Hero.Position = new Position2(7000, 7000);
            match.Foe.Position = new Position2(7400, 7000);
            match.Refill();
            match.Advance(0.5);
            match.Hero.SpellCooldowns[0] = 8.0;
            match.Hero.SpellCooldowns[1] = 18.0;
            match.Hero.SpellCooldowns[2] = 28.0;
            match.Hero.SpellCooldowns[3] = 60.0;
            var gold = match.Hero.Gold;
            TestAssert.True(match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId), "Katarina kills the foe");
            match.Advance(0.5);
            TestAssert.Equal(0.0, match.Hero.SpellCooldowns[0], "Bouncing Blades is ready");
            TestAssert.Equal(0.0, match.Hero.SpellCooldowns[1], "Killer Instinct is ready");
            TestAssert.Equal(0.0, match.Hero.SpellCooldowns[2], "Shunpo is ready");
            TestAssert.Near(44.5, match.Hero.SpellCooldowns[3], 0.05, "Death Lotus loses 15 s (60 - 15, less the half second)");
            TestAssert.True(match.Hero.Gold - gold >= 25.0, "the kill pays Voracity's 25 gold on top of the kill reward");
        }
        finally
        {
            match.Dispose();
        }
    }

    private static bool HasBuff(Entity unit, string name) => unit.Buffs.Any(buff => buff.Name == name);

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
