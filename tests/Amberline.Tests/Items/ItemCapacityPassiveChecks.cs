using System;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-banshee-capacity-retry")]
    public static void BansheeRetriesWithoutLosingItsInitialDuration()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            FillItemBuffSlots(runtime, hero, 64);
            hero.Inventory = [new InventorySlot(3102), null, null, null, null, null];
            runtime.HeroStats.Refresh(hero);
            runtime.Delivery.Flush();
            match.Clear();

            // Batch 149 step 5: Items/3102.luaobj owns this now. OnActivate adds BansheesVeil for 10 s only when the hero
            // has neither BansheesVeil nor BansheesVeilTimer; the full table refuses that add, and nothing re-arms its
            // ten seconds — there is no BansheeInitialPending in the script. The retry is the copy's own
            // UpdateSelfBuffActions, a 2 s BBExecutePeriodically (ExecuteImmediately false) whose add is 3 s and which
            // runs whenever BansheesVeilTimer is absent. The hand-written pet queue (BansheePetInitialized) is gone too:
            // OnActivate's other branch only mirrors the shield onto a non-hero that owns the copy (BBGetPetOwner), and a
            // pet holds no inventory slot here.
            runtime.Tick(0.01);
            runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"));
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                Opcode(packet) == SpellPackets.BuffAddOpcode &&
                ReadUInt32(packet, 9) == SpellPackets.BuffNameHash("BansheesVeil")),
                $"{team}: rejected allocation emits no shield buff");

            TestAssert.True(runtime.Buffs.RemoveBuff(hero, hero.Buffs.Last().Slot));
            runtime.Tick(2.5);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"),
                $"{team}: the 2 s periodic retries once a slot is free");
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "BansheesVeil"));
            // The veil is continuous while the copy is held, not a one-shot: the periodic adds again whenever
            // BansheesVeilTimer is absent and Spells/BansheesVeilTimer.luaobj's own OnBuffDeactivate re-adds for ten
            // seconds. What this pins is the capacity half of it — the retry is one shield and stays one.
            runtime.Tick(5.0);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"),
                $"{team}: the veil is renewed rather than left to its three seconds");
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "BansheesVeil"),
                $"{team}: the renewals stack no second shield");
        }
    }

    [Check("items-live-hexdrinker-capacity-proc")]
    public static void HexdrinkerCapacityKeepsDamageAndTriggerStateCoherent()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (usedSlots, rawDamage) in new[]
                 {
                     (64, 200.0), (63, 200.0), (63, 600.0),
                     (64, 600.0), (62, 200.0),
                 })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3155), null, null, null, null, null];
            // Batch 147 step 4: Items/3155.luaobj runs the trigger from its own ItemOnPreDamage now, so the copy has
            // to be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update latency the 3022
            // cases already account for).
            runtime.Tick(0.01);
            runtime.HeroStats.Refresh(hero);
            hero.MaxHealth = rawDamage < 300.0 ? 2000.0 : 3000.0;
            hero.Health = rawDamage < 300.0 ? 700.0 : 800.0;
            FillItemBuffSlots(runtime, hero, usedSlots);
            runtime.Delivery.Flush();
            match.Clear();
            var before = hero.Health;
            var mitigated = rawDamage * CombatRules.ResistanceMultiplier(hero.SpellBlock);
            var needsShield = mitigated < 300.0;
            // Script wins (batch 147 step 4): Items/3155.luaobj adds Hexdrinker and HexdrinkerTimer on both paths of
            // its trigger (a hit past the budget leaves the shield with a zero one), so one proc needs two slots
            // whatever the hit. The old one-slot rule for such a hit was the hand-written add shape.
            var triggers = usedSlots + 2 <= 64;

            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, rawDamage,
                DamageType.Magic, DamageSourceKind.Basic),
                $"{team}/{usedSlots}/{rawDamage}: magic hit lands");
            runtime.Delivery.Flush();
            var expectedDamage = triggers ? Math.Max(0.0, mitigated - 300.0) : mitigated;
            TestAssert.Near(before - expectedDamage, hero.Health, 1e-7,
                $"{team}/{usedSlots}/{rawDamage}: only an allocated proc prevents damage");
            TestAssert.Near(expectedDamage, hero.MatchStatistics.TotalDamageTaken, 1e-7,
                $"{team}/{usedSlots}/{rawDamage}: damage accounting matches health");
            TestAssert.Equal(triggers, hero.Buffs.Any(buff => buff.Name == "HexdrinkerTimer"),
                $"{team}/{usedSlots}/{rawDamage}: cooldown marker matches the proc");
            TestAssert.Equal(triggers,
                hero.Buffs.Any(buff => buff.Name == "Hexdrinker"),
                $"{team}/{usedSlots}/{rawDamage}: the trigger adds its shield on both paths");
            TestAssert.Equal(triggers ? 3 : 0, match.Captures[0].Packets.Count(packet =>
                    Opcode(packet) == FxPackets.CreateGroupOpcode &&
                    new[]
                    {
                        SpellPackets.BuffNameHash("hexTech_dmg_shield_birth"),
                        SpellPackets.BuffNameHash("hexTech_dmg_shield_onHit_01"),
                        SpellPackets.BuffNameHash("hexTech_dmg_shield_onHit_02"),
                    }.Contains(ReadUInt32(packet, 6))),
                $"{team}/{usedSlots}/{rawDamage}: trigger FX match committed buffs");
            if (triggers)
            {
                TestAssert.Near(60.0, hero.Buffs.Single(buff => buff.Name == "HexdrinkerTimer").ExpiresAt,
                    1e-9, $"{team}/{usedSlots}/{rawDamage}: cooldown lifetime");
                // Script wins (batch 147 step 4): a hit past the budget leaves the added shield with a zero one
                // (Items/3155.luaobj stores ShieldHealth 0 on that path), not without the shield. The budget is the
                // script's own InstanceVars.ShieldHealth, which Spells/Hexdrinker.luaobj absorbs against; the engine
                // has no payload shield of its own for it.
                TestAssert.Near(needsShield ? 300.0 - mitigated : 0.0,
                    (double)runtime.Spells.ItemScripts!.Buffs!.Instances
                        .Single(item => item.Name == "Hexdrinker").InstanceVars["ShieldHealth"]!,
                    1e-7, $"{team}/{usedSlots}/{rawDamage}: live shield budget");
            }
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3155), null, null, null, null, null];
            // Batch 147 step 4: Items/3155.luaobj runs the trigger from its own ItemOnPreDamage now, so the copy has
            // to be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update latency the 3022
            // cases already account for).
            runtime.Tick(0.01);
            runtime.HeroStats.Refresh(hero);
            FillItemBuffSlots(runtime, hero, 64);
            hero.MaxHealth = 2000.0;
            hero.Health = 1.0;
            var resolved = 200.0 * CombatRules.ResistanceMultiplier(hero.SpellBlock);
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 200.0,
                DamageType.Magic, DamageSourceKind.Basic));
            TestAssert.False(hero.Alive, $"{team}: full table does not fake a survival proc");
            var recap = runtime.Combat.GetLastDeathRecap(hero.NetId)
                ?? throw new InvalidOperationException("lethal capacity hit produced no recap");
            TestAssert.Near(resolved, recap.Records.Sum(row => row.TotalDamage), 1e-7,
                $"{team}: lethal unshielded damage remains in recap");
        }
    }

    private static void FillItemBuffSlots(MatchRuntime runtime, Entity unit, int count)
    {
        for (var index = unit.Buffs.Count; index < count; index++)
            TestAssert.True(runtime.SpellBuffs.Add(unit, $"ItemCapacityPassive_{index}",
                BuffType.Internal, 60.0, unit.NetId) is not null,
                $"{unit.Name}: fill buff slot {index}");
        TestAssert.Equal(count, unit.Buffs.Count);
    }
}
