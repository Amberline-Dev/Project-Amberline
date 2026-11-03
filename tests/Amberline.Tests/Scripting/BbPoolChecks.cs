using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 8 (W35b, E20 and E14): the resource pool has a kind (the character record's <c>PARType</c>: Mana, Energy, Souls, Shield,
/// Other) and the stat pipeline, the typed script accessors, the respawn and the fountain follow it; a negative <c>BaseMP</c> (Vladimir's -10000, Dr. Mundo's
/// -100000) is an empty pool; the cast hooks run for the extra-slot child a hero's script casts (Dr. Mundo pays his health there); the timer of an ammo buff
/// (Corki, Akali, Karma) recharges a charge through <c>BuffOnUpdateAmmo</c>. The characters are the shipped data and scripts; the match is the production
/// Ryze match of <see cref="BbM3.Match"/>, and a pool kind other than mana is put on its hero the way a script does, with a pushed character layer.
/// Written and run by the worker that made them (the coordinator allowed the checks of the touched area on 19 October 2026).
/// </summary>
public static class BbPoolChecks
{
    private static HeroStats Stats(string champion, params (string Key, double Value)[] permanent)
    {
        var data = GameData.Default;
        return HeroStatCalculator.Calculate(data, new ItemCatalog(data.Paths),
            new HeroStatsInput(champion, 1, new InventorySlot?[6], permanentMods: permanent.ToDictionary(pair => pair.Key, pair => pair.Value)));
    }

    /// <summary>Puts the pool of <paramref name="character"/> on the match's hero (a pushed layer that carries its spells, as Nidalee's cougar form does).</summary>
    private static void Become(BbM3.Match match, string character)
    {
        match.Hero.ScriptCharacterData = new ScriptCharacterLayer(900001, character, true);
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
    }

    private static BbChampionModule Attach(BbM3.Match match, string champion)
    {
        var dispatcher = (ChampionSpellDispatcher)match.Runtime.Dependencies.ChampionDispatcher;
        var module = new BbChampionModule(champion);
        var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(dispatcher, ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).Append(module).ToArray());
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        ((BbHookFanout)hooks.ScriptHooks!).Add(module.Bind(match.Runtime.Spells));
        return module;
    }

    private static BbBlock Get(string dest, string function, string parType) =>
        BbTest.Block("BBGetPAROrHealth", ["DestVar", dest, "DestVarTable", "InstanceVars", "OwnerVar", "Owner", "Function", BbTest.Ref(function), "PARType", BbTest.Ref(parType)]);

    private static BbBlock Inc(double delta, string parType) =>
        BbTest.Block("BBIncPAR", ["TargetVar", "Owner", "Delta", delta, "PARType", BbTest.Ref(parType)]);

    [Check("scripting.bb-pools-the-stat-pipeline-composes-the-pool-of-the-characters-kind-with-the-keys-of-that-kind")]
    public static void StatPipelineByKind()
    {
        TestAssert.Equal(PrimaryResourceType.Other, GameData.Default.Champion("Tryndamere").PrimaryResource, "Tryndamere's pool is Other");
        TestAssert.Equal(PrimaryResourceType.Shield, GameData.Default.Champion("Mordekaiser").PrimaryResource, "Mordekaiser's is Shield");
        TestAssert.Equal(PrimaryResourceType.Energy, GameData.Default.Champion("Akali").PrimaryResource, "Akali's is Energy");
        TestAssert.Equal(PrimaryResourceType.Mana, GameData.Default.Champion("Annie").PrimaryResource, "Annie's (the shipped \"Mana\") is the client's default, 0");
        TestAssert.Equal(100.0, Stats("Tryndamere").MaxMp, "an Other pool of BaseMP 100");
        TestAssert.Equal(130.0, Stats("Tryndamere", ("FlatMPPoolMod", 500.0), ("FlatOtherPoolMod", 30.0)).MaxMp, "only the Other key counts for an Other pool");
        TestAssert.Equal(150.0, Stats("Mordekaiser", ("FlatShieldPoolMod", 30.0), ("FlatOtherPoolMod", 99.0)).MaxMp, "only the Shield key counts for a Shield pool");
        TestAssert.Equal(250.0, Stats("Akali", ("FlatEnergyPoolMod", 50.0), ("FlatMPPoolMod", 99.0)).MaxMp, "only the Energy key counts for an Energy pool");
        TestAssert.Near(Stats("Annie").MaxMp, Stats("Annie", ("FlatEnergyPoolMod", 50.0), ("FlatOtherPoolMod", 50.0)).MaxMp, 1e-9, "a mana pool ignores the other kinds' keys");
        TestAssert.Equal(2.0, Stats("Rumble", ("FlatOtherRegenMod", 2.0), ("FlatMPRegenMod", 7.0)).MpRegen, "the regeneration of an Other pool follows its own key");
        var data = GameData.Default;
        var catalog = new ItemCatalog(data.Paths);
        var withManaItem = HeroStatCalculator.Calculate(data, catalog,
            new HeroStatsInput("Rumble", 1, new InventorySlot?[] { new InventorySlot(1027), null, null, null, null, null }));
        TestAssert.Equal(100.0, withManaItem.MaxMp, "Sapphire Crystal adds no mana to a heat pool");
    }

    [Check("scripting.bb-pools-a-negative-base-is-an-empty-pool-and-never-a-negative-one")]
    public static void NegativeBaseIsEmpty()
    {
        TestAssert.True(GameData.Default.StatsAtLevel(GameData.Default.Champion("Vladimir"), 1).MaxMp < 0.0, "Vladimir ships BaseMP -10000");
        foreach (var champion in new[] { "Vladimir", "DrMundo", "Garen", "Katarina", "Riven" })
            TestAssert.Equal(0.0, Stats(champion).MaxMp, champion + " has an empty pool");
        TestAssert.Equal(0.0, Stats("Vladimir", ("FlatMPPoolMod", 250.0)).MaxMp, "mana added to a -10000 pool leaves it empty (the setter clamps at 0)");
        using var match = new BbM3.Match("Ryze");
        Become(match, "Vladimir");
        TestAssert.Equal(0.0, match.Hero.MaxMana, "the hero's maximum is 0, not negative");
        TestAssert.Equal(0.0, match.Hero.Mana, "and so is the pool");
        TestAssert.Equal(0, EntityInvariants.Check(match.Hero).Count, "the entity invariant holds for an empty pool");
    }

    [Check("scripting.bb-pools-the-typed-accessors-answer-only-for-the-units-own-kind-and-health-ignores-the-type")]
    public static void TypedAccessors()
    {
        var probe = BbW3Rig.Buff("W8PoolProbe", new BbEventTable("OnBuffActivate",
        [
            Get("OtherNow", "GetPAR", "PAR_OTHER"), Get("ManaNow", "GetPAR", "PAR_MANA"), Get("OtherMax", "GetMaxPAR", "PAR_OTHER"),
            Get("ManaMax", "GetMaxPAR", "PAR_MANA"), Get("Health", "GetHealth", "PAR_MANA"),
            Inc(30.0, "PAR_OTHER"), Inc(30.0, "PAR_MANA"), Inc(10.0, "PAR_ENERGY"),
            Get("OtherAfter", "GetPAR", "PAR_OTHER"), Get("ManaAfter", "GetPAR", "PAR_MANA"), Get("OtherPercent", "GetPARPercent", "PAR_OTHER"),
            Inc(500.0, "PAR_OTHER"), Get("OtherCapped", "GetPAR", "PAR_OTHER"), Inc(-500.0, "PAR_OTHER"), Get("OtherFloor", "GetPAR", "PAR_OTHER"),
        ]));
        using (var rig = new BbW3Rig(null, probe))
        {
            Become(rig.Match, "Rumble");
            var hero = rig.Match.Hero;
            hero.Mana = 0.0;
            var health = hero.Health;
            var vars = rig.Give(hero, probe).InstanceVars;
            rig.NoFailures();
            TestAssert.Equal(100.0, hero.MaxMana, "a heat pool of 100");
            TestAssert.Equal(0.0, (double)vars["OtherNow"]!, "empty heat pool");
            TestAssert.Equal(0.0, (double)vars["ManaNow"]!, "a mana read finds nothing");
            TestAssert.Equal(100.0, (double)vars["OtherMax"]!, "the heat maximum");
            TestAssert.Equal(0.0, (double)vars["ManaMax"]!, "no mana maximum");
            TestAssert.Equal(health, (double)vars["Health"]!, "GetHealth ignores the type asked");
            TestAssert.Equal(30.0, (double)vars["OtherAfter"]!, "IncPAR of the unit's own kind fills it; the mana and energy increments did nothing");
            TestAssert.Equal(0.0, (double)vars["ManaAfter"]!, "still no mana");
            TestAssert.Near(0.3, (double)vars["OtherPercent"]!, 1e-9, "the percent of the heat pool");
            TestAssert.Equal(100.0, (double)vars["OtherCapped"]!, "the pool stops at its maximum");
            TestAssert.Equal(0.0, (double)vars["OtherFloor"]!, "and at zero");
        }
        using (var rig = new BbW3Rig(null, probe))
        {
            var hero = rig.Match.Hero;
            hero.Mana = 50.0;
            var vars = rig.Give(hero, probe).InstanceVars;
            rig.NoFailures();
            TestAssert.Equal(0.0, (double)vars["OtherNow"]!, "a mana hero has no Other pool to read");
            TestAssert.Equal(50.0, (double)vars["ManaNow"]!, "its mana reads");
            TestAssert.Equal(0.0, (double)vars["OtherAfter"]!, "an Other increment does nothing to a mana hero");
            TestAssert.Equal(80.0, hero.Mana, "the mana increment added its 30 to 50");
        }
    }

    [Check("scripting.bb-pools-the-permanent-pool-modifier-feeds-the-pool-of-its-kind")]
    public static void PermanentPoolMod()
    {
        var probe = BbW3Rig.Buff("W8PoolModProbe", new BbEventTable("OnBuffActivate",
        [
            BbTest.Block("BBIncPermanentFlatPARPoolMod", ["TargetVar", "Owner", "Delta", 30.0, "PARType", BbTest.Ref("PAR_OTHER")]),
            BbTest.Block("BBIncPermanentFlatPARPoolMod", ["TargetVar", "Owner", "Delta", 100.0, "PARType", BbTest.Ref("PAR_MANA")]),
        ]));
        var probeShield = BbW3Rig.Buff("W8ShieldModProbe", new BbEventTable("OnBuffActivate",
        [
            BbTest.Block("BBIncPermanentFlatPARPoolMod", ["TargetVar", "Owner", "Delta", 30.0, "PARType", BbTest.Ref("PAR_SHIELD")]),
        ]));
        using var rig = new BbW3Rig(null, probe, probeShield);
        Become(rig.Match, "Mordekaiser");
        var hero = rig.Match.Hero;
        TestAssert.Equal(120.0, hero.MaxMana, "a shield pool of 120");
        rig.Give(hero, probe);
        TestAssert.Equal(120.0, hero.MaxMana, "a Mana and an Other modifier do nothing to a shield pool");
        TestAssert.True(hero.PermanentStatMods.Count == 0, "and are not even kept");
        rig.Give(hero, probeShield);
        TestAssert.Equal(150.0, hero.MaxMana, "the shield modifier raises the shield pool");
        TestAssert.Equal(150.0, hero.Mana, "and the current value with it, as a mana pool's rise does (the script takes the 30 back with IncPAR)");
        rig.NoFailures();
    }

    [Check("scripting.bb-pools-a-life-starts-with-an-empty-shield-or-other-pool-and-a-full-mana-pool-and-the-fountain-fills-only-the-latter")]
    public static void RespawnAndFountain()
    {
        foreach (var (character, refills) in new (string? Character, bool Refills)[] { (null, true), ("Rumble", false), ("Mordekaiser", false), ("Akali", true) })
        {
            using var match = new BbM3.Match("Ryze");
            if (character is not null) Become(match, character);
            var hero = match.Hero;
            hero.Mana = hero.MaxMana;
            match.Advance(5.0);
            match.Runtime.Combat.Kill(hero, match.Foe.NetId);
            match.Runtime.Combat.ScheduleRespawn(hero);
            TestAssert.True(!hero.Alive, (character ?? "Ryze") + " died");
            var diedAt = match.Now;
            var wait = hero.RespawnAt - diedAt;
            for (var seconds = 0; seconds < 400 && !hero.Alive; seconds++) match.Advance(1.0);
            TestAssert.True(hero.Alive, $"{character ?? "Ryze"} respawned (timer {wait:0.0} s from {diedAt:0.0}, now {match.Now:0.0}, RespawnAt {hero.RespawnAt:0.0})");
            var expected = refills ? hero.MaxMana : 0.0;
            TestAssert.Near(expected, hero.Mana, 1e-9, (character ?? "Ryze") + ": the pool at the start of a life");
            // The respawn put the hero in the fountain: a pool that can regenerate fills, a fury or shield pool does not.
            hero.Mana = hero.MaxMana * 0.1;
            var low = hero.Mana;
            match.Advance(4.0);
            if (refills) TestAssert.True(hero.Mana > low + 1.0, (character ?? "Ryze") + ": the fountain fills the pool");
            else TestAssert.Near(low, hero.Mana, 1e-6, (character ?? "Ryze") + ": the fountain leaves a fury or shield pool alone");
        }
    }

    [Check("scripting.bb-pools-the-cast-hooks-run-for-the-extra-slot-child-a-script-casts-so-dr-mundo-pays-his-health-there")]
    public static void ChildCastHooksPayMundosHealth()
    {
        using var match = new BbM3.Match("Ryze");
        var mundo = Attach(match, "DrMundo");
        var hero = match.Hero;
        hero.ChampionName = "DrMundo";
        hero.SpellRanks[0] = 1;
        hero.MaxHealth = hero.Health = 5000.0;
        var router = mundo.Router!;
        var child = BbM3.Script("InfectedCleaverMissile");
        TestAssert.True(child.Global("DoesntTriggerSpellCasts") is BbValue.Bool { Value: true }, "the child is flagged DoesntTriggerSpellCasts, which does not stop the hook");
        // The slot spell's own cast pays nothing: the character script tests the child's name.
        router.RunSpellCast(hero, 0, "InfectedCleaverMissileCast", BbM3.Script("InfectedCleaverMissileCast"));
        TestAssert.Equal(5000.0, hero.Health, "the slot cast is free");
        router.RunChildSpellCast(hero, SpellSystem.ExtraSpellSlotStart, "InfectedCleaverMissile", child, 1, null);
        TestAssert.Equal(4950.0, hero.Health, "the child's cast costs the 50 health of rank 1");
        hero.SpellRanks[0] = 5;
        router.RunChildSpellCast(hero, SpellSystem.ExtraSpellSlotStart, "InfectedCleaverMissile", child, 5, null);
        TestAssert.Equal(4860.0, hero.Health, "and 90 at rank 5");
        hero.Health = 30.0;
        router.RunChildSpellCast(hero, SpellSystem.ExtraSpellSlotStart, "InfectedCleaverMissile", child, 5, null);
        TestAssert.Equal(1.0, hero.Health, "a health cost never kills (the pool stops at 1)");
        TestAssert.Equal(0, mundo.ScriptFailures.Count, "no script failure: " + string.Join("; ", mundo.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));
    }

    [Check("scripting.bb-pools-get-flat-hp-pool-mod-reads-the-flat-health-the-sources-add-and-not-the-base")]
    public static void FlatHealthPoolMod()
    {
        var probe = BbW3Rig.Buff("W8FlatHealthProbe", new BbEventTable("OnBuffActivate",
        [
            BbTest.Block("BBGetStat", ["Stat", BbTest.Ref("GetFlatHPPoolMod"), "TargetVar", "Owner", "DestVar", "Flat", "DestVarTable", "InstanceVars"]),
        ]));
        using var rig = new BbW3Rig(null, probe);
        var hero = rig.Match.Hero;
        hero.Level = 5;
        rig.Match.Runtime.HeroStats.Refresh(hero, push: false);
        TestAssert.Equal(0.0, (double)rig.Give(hero, probe).InstanceVars["Flat"]!, "no source of health: the base and its level growth are not part of it");
        hero.Inventory = [new InventorySlot(1011), null, null, null, null, null];
        rig.Match.Runtime.HeroStats.Refresh(hero, push: false);
        var belt = new ItemCatalog(GameData.Default.Paths).Get(1011)!.Modifier("FlatHPPoolMod");
        TestAssert.True(belt > 0.0, "Giant's Belt carries a flat health modifier");
        TestAssert.Equal(belt, (double)rig.Give(hero, probe).InstanceVars["Flat"]!, "Giant's Belt adds its health: " + belt);
        rig.NoFailures();
    }

    [Check("scripting.bb-pools-a-character-script-may-set-a-buff-tooltip-variable-and-nothing-shows-it")]
    public static void CharacterScriptBuffTooltipVariable()
    {
        var character = BbW3Rig.Character("W8TooltipCharacter", new BbEventTable("UpdateSelfBuffStats",
        [
            BbTest.Block("BBSetBuffToolTipVar", ["Value", 25.0, "Index", 1]),
        ]));
        using var rig = new BbW3Rig(character);
        rig.Match.Advance(0.3);
        rig.NoFailures();
    }
}
