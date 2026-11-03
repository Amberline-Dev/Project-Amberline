using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Shaco runs from his shipped scripts (phase 3, W33, 19 October 2026). The scenario check <c>tools.scenario.shaco-plays-from-his-shipped-scripts</c>
/// pins the spells' numbers; these pin the engine rules the conversion needed (the hook variable <c>LifeTime</c>, the crit damage stat, the
/// <c>/cooldowns</c> cheat for script cooldowns, the cast's target in <c>BuffOnSpellCast</c>, the allied-only effect, the fade ledger).
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] ShacoSystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    [Check("scripting.bb-shaco-module-binds-the-shipped-closure")]
    public static void ShacoModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Shaco");
        var hero = new Entity { ChampionName = "Shaco" };
        TestAssert.Equal(["Deceive", "JackInTheBox", "TwoShivPoison", "HallucinateFull"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Shaco.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Shaco");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(28, closure.ScriptNames.Count(), "the Shaco closure is 28 scripts (character, spells, buffs, the box's spell, the shared system buffs)");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => ShacoSystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 28 are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !ShacoSystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
            {
                if (hook == BbScript.PreLoadHook || hook == "CharOnDisconnect") continue;
                if (BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
                if (BbNativeHandlers.SpawnRefusal(block) is { } refusal) unbound.Add($"{script.Name}:{hook}:{refusal}");
            }
        TestAssert.Equal(0, unbound.Count, "every native a Shaco hook runs is bound and every spawn is accepted: " + string.Join(", ", unbound));
        TestAssert.Equal("Hallucinate", GameData.Default.Champion("Shaco").Raw.TryGetValue(InibinReader.KeyHash("Data*ExtraSpell1"), out var extra) &&
                                       extra.Kind == InibinValueKind.String ? extra.AsString() : null, "ExtraSpell1 is the unused Hallucinate leftover");
    }

    [Check("scripting.bb-shaco-box-arms-at-lifetime-two")]
    public static void ShacoBoxStealthsWhenItsBuffHasLivedTwoSeconds()
    {
        // JackInTheBox.luaobj BuffOnUpdateActions: "LifeTime >= 2", a local no block sets: the hook scope supplies it (before 19 October 2026 the
        // hook raised 'attempt to compare number with nil' on every update).
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        enemy.Position = new Position2(11000, 11000);
        var box = ShacoScriptSupport.SpawnBox(runtime, hero, hero.Position + new Position2(0, 400));
        Advance(match, 1.9);
        TestAssert.False(box.Buffs.Any(buff => buff.Name == "Stealth"), "asleep and visible before two seconds");
        Advance(match, 0.3);
        TestAssert.True(box.Buffs.Any(buff => buff.Name == "Stealth"), "stealthed after two seconds");
        var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Shaco")!;
        TestAssert.Equal(0, module.ScriptFailures.Count, "no hook raised");
    }

    [Check("scripting.bb-shaco-deceive-crit-bonus-is-a-crit-damage-stat")]
    public static void ShacoDeceiveCritBonusLowersTheCritMultiplierUntilTheHit()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Shaco")!;
        var plain = hero.CritMultiplier;
        TestAssert.Near(2.0, plain, 1e-9, "the base crit multiplier");
        module.Buffs!.Add(new BbBuffRequest(hero, hero, "DeceiveCritBonus", (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)BbBuffType.BUFF_Internal,
            1, 10.0, 0.0, false, new BbTable { ["CritDmgBonus"] = -0.2 }));
        Advance(match, 0.1);
        TestAssert.Near(1.8, hero.CritMultiplier, 1e-9, "IncFlatCritDamageMod -0.2 (rank 3)");
        // BuffOnHitUnit marks the hit and the next stats update ends the buff. That update is the 0.25 s stats
        // grid (batch 150 part 2a), not the every-update poll this wait was written against: the buff lives until
        // its next sample, so the wait has to cross one.
        module.Router!.RunHitUnit(hero, enemy, 100.0);
        Advance(match, 0.3);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "DeceiveCritBonus"), "one empowered hit");
        TestAssert.Near(plain, hero.CritMultiplier, 1e-9, "the multiplier is back");
    }

    [Check("scripting.bb-shaco-deceive-cooldown-follows-the-cooldowns-cheat")]
    public static void ShacoDeceiveCooldownIsZeroWithCooldownsOff()
    {
        foreach (var cheat in new[] { false, true })
        {
            using var match = new Match("Shaco", Team.Order);
            var hero = match.Hero(0);
            hero.CooldownsDisabled = cheat;
            match.CastGround(0, hero.Position + new Position2(300, 0));
            Advance(match, 3.8);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Deceive"), "the stealth is over");
            if (cheat) TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "no cooldown with the cheat on");
            else TestAssert.Near(11.0 - 0.2, hero.SpellCooldowns[0], 0.3, "the 11 s cooldown starts when the stealth ends");
        }
    }

    [Check("scripting.bb-shaco-backstab-marks-the-cast-from-behind")]
    public static void ShacoBackstabJudgesTheTwoShivCastWhereTheTargetStoodAtTheCast()
    {
        foreach (var behind in new[] { true, false })
        {
            using var match = new Match("Shaco", Team.Order);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.Position = new Position2(7000, 7000);
            enemy.Position = new Position2(7200, 7000);
            hero.Facing = new Position2(1, 0);
            enemy.Facing = behind ? new Position2(1, 0) : new Position2(-1, 0);
            match.Cast(0, 2, enemy);
            TestAssert.Equal(behind, hero.Buffs.Any(buff => buff.Name == "CastFromBehind"),
                behind ? "BuffOnSpellCast saw the target in front and facing away" : "a target facing Shaco is not a backstab");
        }
    }

    [Check("scripting.bb-shaco-clone-mark-is-shown-to-shacos-team-only")]
    public static void ShacoCloneMarkIsAnAlliedOnlyEffect()
    {
        // HallucinateFull.OnBuffActivate creates Jester_Copy with SpecificTeamOnly = the caster's team and UseSpecificUnit true (SpecificUnitOnlyVar
        // "Nothing"): the effect is for that team's clients, so the enemy cannot tell which one is the clone.
        using var match = new Match("Shaco", Team.Order);
        var hero = match.Hero(0);
        match.Cast(0, 3, hero);
        Advance(match, 0.7);
        match.Runtime.Delivery.Flush();
        TestAssert.True(match.Runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == hero.NetId), "the clone exists");
        TestAssert.True(HasEffect(match.Captures[0], "Jester_Copy"), "Shaco's team is shown the mark");
        TestAssert.False(HasEffect(match.Captures[1], "Jester_Copy"), "the enemy is not");
    }

    [Check("scripting.bb-shaco-fade-ledger-follows-the-pushes")]
    public static void ShacoFadeLedgerKeepsOnlyWhatALateObserverNeeds()
    {
        using var match = new Match("Shaco", Team.Order);
        var hero = match.Hero(0);
        match.CastGround(0, hero.Position + new Position2(300, 0));
        Advance(match, 0.3);
        TestAssert.True(hero.ScriptFades.Any(layer => layer.Opacity < 1.0f), "Deceive's translucent layer is on the ledger while the stealth runs");
        Advance(match, 3.6);
        TestAssert.True(hero.ScriptFades.Count > 0 && hero.ScriptFades.All(layer => layer.Opacity >= 1.0f),
            "the push of full opacity at the end of the stealth supersedes it: nothing translucent is left to replay");
    }
}
