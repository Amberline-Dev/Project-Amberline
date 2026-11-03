using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation;
using Amberline.Simulation.Runtime;

namespace Amberline.Tests;

/// <summary>
/// Focused checks for the match-owned gameplay hook boundaries. These checks
/// construct the same production graph used by the executable so a missing
/// callback cannot be hidden by a recording-only test double.
/// </summary>
public static class GameplayHooksChecks
{
    [Check("runtime.gameplay-hooks")]
    public static void RuntimeGameplayHooks()
    {
        TargetabilityUsesUntargetableAndRevealProviders();
        PeriodicDamageUsesOneAuthoritativeHit();
        RawDamageOrderPreservesSpellbladeAndOdynBoundaries();
    }

    private static void TargetabilityUsesUntargetableAndRevealProviders()
    {
        var world = new WorldState();
        var reveal = false;
        var hooks = new MatchGameplayHooks(world, GameData.Default,
            (_, _) => reveal);
        var observer = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(0.0, 0.0));
        var target = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(10.0, 0.0));
        world.Register(observer);
        world.Register(target);

        TestAssert.True(hooks.IsTargetable(target, observer), "ordinary enemy is targetable");
        target.Buffs.Add(new BuffState("Untargetable", new BuffPayload { Untargetable = true }));
        TestAssert.False(hooks.IsTargetable(target, observer), "untargetable payload must gate targeting");
        target.Buffs.Clear();
        target.Buffs.Add(new BuffState("Stealth", new BuffPayload { Stealthed = true }));
        TestAssert.False(hooks.IsTargetable(target, observer), "unrevealed enemy stealth must gate targeting");
        reveal = true;
        TestAssert.True(hooks.IsTargetable(target, observer), "reveal provider must expose stealth");
    }

    private static void PeriodicDamageUsesOneAuthoritativeHit()
    {
        var runtime = Production();
        var source = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(0.0, 0.0))
        {
            Alive = true, Created = true,
            Inventory = [new InventorySlot(3116), null, null, null, null, null],
        };
        var target = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(10.0, 0.0))
        {
            Alive = true, Created = true, Health = 100.0, MaxHealth = 100.0,
            Armor = 0.0, SpellBlock = 0.0,
        };
        runtime.World.Register(source);
        runtime.World.Register(target);
        // Batch 147 step 4: Rylai's item callback is Items/3116.luaobj's own ItemOnPreDealDamage now, so its copy
        // binds to the inventory slot first (BbItemModule.SyncInventory). This fixture's session never starts, so
        // the simulation driver stops before SpellSystem.Tick and the check runs that same bind step itself — the
        // same call RawDamageOrderPreservesSpellbladeAndOdynBoundaries makes below.
        runtime.Spells.ItemScripts?.SyncInventory(source);

        // Production registration starts a hero with placeholder values.  The
        // source refreshes its shipped champion stats before a combat event;
        // establish that same baseline so the assertion measures the one hit
        // rather than the health gain from the first Rylai slow refresh.
        runtime.HeroStats.Refresh(target);
        var before = target.Health;
        var expectedDamage = 10.0 * 100.0 / (100.0 + Math.Max(0.0, target.SpellBlock));

        runtime.Dependencies.BuffServices.Combat.ApplyPeriodicDamage(
            new PeriodicDamageRequest(target, source.NetId, target.NetId, 10.0,
                DamageType.Magic, DamageSourceKind.SpellPersist, source.NetId, true));

        TestAssert.Near(before - expectedDamage, target.Health, 1e-9,
            $"periodic damage must use CombatService.ApplyHit exactly once (before={before:R}, after={target.Health:R})");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "ItemSlow"),
            "spell owner/source kind must reach Rylai's item callback");
    }

    // The Yorick passive is script work now (YorickUnholySymbiosis: the outgoing share on hit, the incoming one in BuffOnPreDamage after
    // resistance) and Yorick's module joins a match only when he is in its roster, so this step pins what the hooks own: the Spellblade bonus
    // is inside the raw damage the window's tenth is taken from.
    private static void RawDamageOrderPreservesSpellbladeAndOdynBoundaries()
    {
        var runtime = Production();
        var source = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(0.0, 0.0))
        {
            ChampionName = "Yorick", Alive = true, Created = true,
            Inventory = [new InventorySlot(3078), null, null, null, null, null],
        };
        var target = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(10.0, 0.0))
        {
            ChampionName = "Yorick", Alive = true, Created = true,
            Health = 100.0, MaxHealth = 100.0, Armor = 0.0, SpellBlock = 0.0,
            Inventory = [new InventorySlot(3180), null, null, null, null, null],
        };
        runtime.World.Register(source);
        runtime.World.Register(target);
        // Batch 147: the Spellblade bonus is the Sheen buff's own BuffOnHitUnit, armed by Items/3078.luaobj's
        // ItemOnSpellCast. Its copy binds to the inventory slot on the next spell update (BbItemModule.SyncInventory);
        // this fixture's session never starts, so the simulation driver stops before SpellSystem.Tick and the check
        // runs that same bind step itself.
        runtime.Spells.ItemScripts?.SyncInventory(source);
        // Batch 149 step 4: Odyn's Veil is the shipped Spells/OdynsVeil.luaobj now, and its window is not cast at
        // all — Items/3180.luaobj's UpdateSelfBuffActions re-adds the OdynsVeil buff every nine seconds
        // (ExecuteImmediately), so one poll of the holder opens it. That buff's BuffOnPreDamage takes the tenth off
        // a magic hit and keeps what it prevented in its own CharVars.StoredDamage; the hand-written pair (the
        // reduction in CombatService and Entity.OdynStoredDamage) is gone. Script wins: the window's slot is after
        // resistance, so this fixture pins the Spellblade-before-raw boundary and the script's own share at 0 armor
        // and 0 spell block, where the tenth is the same number either way.
        runtime.Spells.ItemScripts?.Poll(target);
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "OdynsVeil"),
            "the 3180 copy opens its window from its own poll");

        var hooks = (MatchGameplayHooks)runtime.Dependencies.CombatHooks;
        hooks.OnSpellCast(source, 0, 0.0);
        var baseAttack = GameData.Default.StatsAtLevel(
            GameData.Default.Champion("Yorick"), source.Level).AttackDamage;
        var spellbladeBonus = baseAttack * 1.5;
        var raw = 10.0 + spellbladeBonus;

        // Batch 297: Items/3078.luaobj's ItemOnHitUnit also rolls its on-hit slow (CO_RANDOM_CHANCE_LESS_THAN, one draw of the match's Lua
        // stream, which LevelScript seeds from os.time). A proc adds ItemSlow, whose stat update rebuilds this hand-made target from Yorick's
        // own stats (Health 100 -> ~810), so the step only held while the wall-clock draw missed (it failed about 42 minutes in every
        // 2.8 hours). This step pins the damage order, not the slow: the roll misses here.
        LuaRollSeam.Next(runtime.World.LuaRandom, 0.99);
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 10.0,
            DamageType.Magic, DamageSourceKind.Basic), "combined hit must land");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "ItemSlow"), "the pinned roll misses the 3078 slow");
        TestAssert.Near(raw * 0.10, (double)runtime.Spells.ItemScripts!.CharVars(target)["StoredDamage"]!, 1e-9,
            "Odyn stores after the Spellblade bonus");
        TestAssert.Near(100.0 - raw * 0.90, target.Health, 1e-9,
            "Spellblade precedes Odyn and mitigation");
    }

    private static MatchRuntime Production() => MatchRuntime.CreateProduction(
        NavigationGrid.LoadDefault(), GameData.Default, orderChampion: "Annie",
        chaosChampion: "Orianna", solo: true, readySettleSeconds: 0.0,
        random: new FixedRandom(0.5));

    private sealed class FixedRandom(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
