using System;
using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-champion-spell-hook-capacity")]
    public static void SpellItemProcsDoNotAbortAnAcceptedChampionCast()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (spellbladeItem, alias) in new[]
                 {
                     (3057, "Sheen"), (3078, "Sheen"), (3100, "LichBane"),
                 })
        foreach (var phase in new[] { "missing-full", "one-free", "existing-full" })
        {
            using var match = new Match("Ezreal", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3124), new InventorySlot(spellbladeItem),
                null, null, null, null];
            match.Runtime.HeroStats.Refresh(hero);
            // Batch 147: the Rageblade stack and the spellblade arm are the items' own ItemOnSpellCast hooks now,
            // so both copies have to reach their inventory slots first (BbItemModule.SyncInventory, one spell update).
            match.Runtime.Tick(0.01);
            if (phase == "existing-full")
            {
                TestAssert.True(match.Runtime.Buffs.AddBuff(hero, "Rageblade",
                    BuffType.CombatEnhancer, 5.0, hero.NetId) is not null);
                TestAssert.True(match.Runtime.Buffs.AddBuff(hero, alias,
                    BuffType.CombatEnhancer, 10.0, hero.NetId) is not null);
            }
            FillItemBuffSlots(match.Runtime, hero, phase == "one-free" ? 63 : 64);
            var beforeMana = hero.Mana;
            match.Clear();
            TestAssert.True(match.TryCast(0, 0, enemy),
                $"{team}/{spellbladeItem}/{phase}: missing item proc never aborts Ezreal Q");
            TestAssert.True(hero.Mana < beforeMana && hero.SpellCooldowns[0] > 0.0,
                "accepted champion cast still commits normal resource and cooldown");
            TestAssert.Equal(64, hero.Buffs.Count);
            if (phase == "missing-full")
            {
                TestAssert.False(hero.Buffs.Any(buff => buff.Name is "Rageblade" or "Sheen" or "LichBane"));
                // Batch 357 (restates batch 181): Rageblade's add carries its binding name; GuinsoosRageblade has no script.
                TestAssert.False(match.Captures[0].Packets.Any(packet =>
                    IsBuffAdd(packet, hero, alias)),
                    "saturated missing proc emits no item-buff wire");
            }
            else if (phase == "one-free")
            {
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Rageblade"));
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == alias),
                    "Rageblade's earlier hook uses the one free slot");
            }
            else
            {
                TestAssert.Equal(2, hero.Buffs.Single(buff => buff.Name == "Rageblade").Stacks);
                TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == alias));
                TestAssert.True(match.Captures[0].Packets.Any(packet =>
                    IsBuffAdd(packet, hero, alias)),
                    "same-name item procs renew at full capacity");
            }
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var existing in new[] { false, true })
        {
            using var match = new Match("Annie", team);
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3124), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(attacker);
            // Batch 147: the Rageblade stack is Items/3124.luaobj's own ItemOnHitUnit now (its copy binds on the
            // next spell update, BbItemModule.SyncInventory).
            match.Runtime.Tick(0.01);
            if (existing)
                TestAssert.True(match.Runtime.Buffs.AddBuff(attacker, "Rageblade",
                    BuffType.CombatEnhancer, 5.0, attacker.NetId) is not null);
            FillItemBuffSlots(match.Runtime, attacker, 64);
            var before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                50.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(target.Health < before,
                "full-table Rageblade hook never aborts an authoritative basic hit");
            TestAssert.Equal(existing, attacker.Buffs.Any(buff => buff.Name == "Rageblade"));
            TestAssert.Equal(64, attacker.Buffs.Count);
            if (existing)
                TestAssert.Equal(2, attacker.Buffs.Single(buff => buff.Name == "Rageblade").Stacks);
        }
    }

    [Check("items-live-spell-damage-lightbringer-capacity")]
    public static void SpellDamageLightbringerNeedsARecipientSlot()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var source = match.Hero(0);
            var target = match.Hero(1);
            source.Inventory = [new InventorySlot(3187), null, null, null, null, null];
            // Batch 147 step 4: Items/3187.luaobj runs the mark from its own ItemOnDealDamage now, so the copy has to
            // be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update latency the 3022
            // cases below already account for).
            match.Runtime.Tick(0.01);
            match.Runtime.HeroStats.Refresh(source);
            FillItemBuffSlots(match.Runtime, target, 64);
            match.Runtime.Delivery.Flush();
            match.Clear();
            var before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, target.NetId,
                50.0, DamageType.Magic, DamageSourceKind.Spell));
            match.Runtime.Delivery.Flush();
            TestAssert.True(target.Health < before,
                "full-table optional spell proc does not abort committed damage");
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "OdinLightbringer"));
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                IsBuffAdd(packet, target, "OdinLightbringer")));

            var filler = target.Buffs.First(buff =>
                buff.Name.StartsWith("ItemCapacityPassive_", StringComparison.Ordinal));
            TestAssert.True(match.Runtime.Buffs.RemoveBuff(target, filler.Slot));
            before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, target.NetId,
                50.0, DamageType.Magic, DamageSourceKind.Spell));
            TestAssert.True(target.Health < before);
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "OdinLightbringer"));
            TestAssert.Equal(64, target.Buffs.Count);

            before = target.Health;
            TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, target.NetId,
                50.0, DamageType.Magic, DamageSourceKind.Spell));
            TestAssert.True(target.Health < before);
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "OdinLightbringer"));
            TestAssert.Equal(64, target.Buffs.Count);
        }
    }

    [Check("replication.item-vision-is-the-script-bubble")]
    public static void LightbringerSightIsItsBuffScriptBubbleOnly()
    {
        // Batch 270: OdinLightbringer.luaobj's OnBuffActivateBuildingBlocks adds the mark's sight itself (BBAddUnitPerceptionBubble,
        // radius 400) and OnBuffDeactivateBuildingBlocks removes it (BBRemovePerceptionBubble). The retired once-a-second "item
        // vision refresh" re-sent the same disc as a 1.5 s 0x7B with a 0x6FFExxxx id; retail has no such projection.
        using var match = new Match("Annie", Team.Order);
        var source = match.Hero(0);
        var target = match.Hero(1);
        source.Inventory = [new InventorySlot(3187), null, null, null, null, null];
        match.Runtime.Tick(0.01);
        match.Runtime.HeroStats.Refresh(source);
        match.Runtime.Delivery.Flush();
        match.Clear();
        TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, target.NetId,
            50.0, DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "OdinLightbringer"), "the hit marks the target");
        for (var step = 0; step < 30; step++)
        {
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();
        }
        TestAssert.True(match.Runtime.World.TimedVisionBubbles.Any(bubble => bubble.AnchorNetId == target.NetId),
            "the buff script's own unit bubble follows the marked target");
        TestAssert.False(match.Captures[0].Packets.Any(packet => packet.Plaintext.Span[0] == 0x7B &&
                (BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[33..]) & 0xFFFF0000u) == 0x6FFE0000u),
            "no server-made item vision bubble reaches the marking team");
    }

    [Check("items-live-aura-capacity-retries-on-next-sync")]
    public static void PassiveAuraWaitsForRecipientSlotWithoutStoppingTick()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var enemy = match.Hero(1);
            source.Inventory = [new InventorySlot(3001), null, null, null, null, null];
            runtime.HeroStats.Refresh(source);
            FillItemBuffSlots(runtime, enemy, 64);
            TestAssert.False(BuffLifecycle.CanAddNamedBuffs(enemy, "AbyssalScepterAura"));
            runtime.Delivery.Flush();
            match.Clear();

            // Batch 145 step 2: Items/3001.luaobj re-adds both auras from BBExecutePeriodically (0.9 s,
            // ExecuteImmediately false), so "the next sync" this check's name means is the script's own next fire —
            // one period after the copy binds, then every period again.
            runtime.Tick(1.0);
            runtime.Delivery.Flush();
            TestAssert.True(source.Buffs.Any(buff => buff.Name == "AbyssalScepterAuraSelf"),
                $"{team}: normal self aura still applies");
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "AbyssalScepterAura"),
                $"{team}: saturated enemy has no aura");
            // Batch 357 (restates batch 181): the aura's add carries its binding AbyssalScepterAura; Abyssal Scepter has no script.
            TestAssert.False(match.Captures[0].Packets.Any(packet =>
                IsBuffAdd(packet, enemy, "AbyssalScepterAura")),
                $"{team}: rejected aura has no buff-add wire");

            TestAssert.True(runtime.Buffs.RemoveBuff(enemy, enemy.Buffs.Last().Slot));
            TestAssert.True(BuffLifecycle.CanAddNamedBuffs(enemy, "AbyssalScepterAura"));
            match.Clear();
            runtime.Tick(1.0);
            runtime.Delivery.Flush();
            var aura = enemy.Buffs.Single(buff => buff.Name == "AbyssalScepterAura");
            TestAssert.Equal(source.NetId, aura.CasterNetId);
            TestAssert.Equal(64, enemy.Buffs.Count);
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
                IsBuffAdd(packet, enemy, "AbyssalScepterAura")),
                $"{team}: one aura add after capacity returns");
            TestAssert.True(BuffLifecycle.CanAddNamedBuffs(enemy,
                "AbyssalScepterAura", "AbyssalScepterAura"),
                $"{team}: duplicate same-name renewal needs no new slot");
            runtime.Tick(1.0);
            TestAssert.Equal(1, enemy.Buffs.Count(buff => buff.Name == "AbyssalScepterAura"));
        }
    }

    [Check("items-live-onhit-slow-capacity-and-mobility")]
    public static void OnHitItemSlowCommitsItsMarkerOrLeavesHitIntact()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (used, boots, expectedSlow) in new[]
                 {
                     (64, false, false), (63, false, false), (62, false, true),
                     (64, true, false), (62, true, false), (61, true, true),
                 })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3022), null, null, null, null, null];
            if (boots) target.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            // Batch 147: Items/3022.luaobj binds its copy to the inventory slot on the next spell update
            // (BbItemModule.SyncInventory), the way a real buy does, so give the runtime that update first.
            runtime.Tick(0.01);
            runtime.HeroStats.Refresh(attacker);
            runtime.HeroStats.Refresh(target);
            FillItemBuffSlots(runtime, target, used);
            runtime.Delivery.Flush();
            match.Clear();
            var before = target.Health;
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 50.0,
                DamageType.Physical, DamageSourceKind.Basic),
                $"{team}/{used}/{boots}: basic hit finishes");
            runtime.Delivery.Flush();
            TestAssert.True(target.Health < before,
                $"{team}/{used}/{boots}: saturated item proc does not cancel hit damage");
            TestAssert.Equal(expectedSlow, target.Buffs.Any(buff => buff.Name == "ItemSlow"));
            TestAssert.Equal(expectedSlow, target.Buffs.Any(buff => buff.Name == "Internal_30Slow"));
            TestAssert.Equal(boots && used < 64,
                target.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"),
                $"{team}/{used}/{boots}: Mobility Boots owns its independent combat debuff slot");
            if (!expectedSlow)
            {
                // Batch 357 (restates batch 181): ItemSlow's add carries its binding name; Frozen Mallet Slow has no script.
                TestAssert.False(match.Captures[0].Packets.Any(packet =>
                    IsBuffAdd(packet, target, "ItemSlow") ||
                    IsBuffAdd(packet, target, "Internal_30Slow")),
                    $"{team}/{used}/{boots}: denied pair emits neither marker");
                continue;
            }

            TestAssert.Equal(64, target.Buffs.Count);
            TestAssert.True(BuffLifecycle.CanAddNamedBuffs(target,
                "Internal_30Slow", "ItemSlow", "Internal_30Slow"));
            match.Clear();
            before = target.Health;
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 50.0,
                DamageType.Physical, DamageSourceKind.Basic));
            runtime.Delivery.Flush();
            TestAssert.True(target.Health < before);
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "Internal_30Slow"));
            TestAssert.Equal(1, target.Buffs.Count(buff => buff.Name == "ItemSlow"));
            TestAssert.Equal(64, target.Buffs.Count);
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3022), null, null, null, null, null];
            match.Runtime.Tick(0.01); // Items/3022.luaobj binds its copy on the next spell update (batch 147)
            match.Runtime.HeroStats.Refresh(attacker);
            TestAssert.True(match.Runtime.SpellBuffs.Add(target, "ResistantSkin",
                BuffType.Aura, 10.0, target.NetId) is not null);
            FillItemBuffSlots(match.Runtime, target, 63);
            var speed = target.Speed;
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId,
                50.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "ItemSlow"),
                $"{team}: slow immunity rejects the visible ItemSlow marker");
            TestAssert.Near(0.0, target.Buffs.Single(buff => buff.Name == "Internal_30Slow")
                .Payload.MoveSpeedMult, 1e-9,
                $"{team}: self-bound internal row has no active slow without ItemSlow");
            TestAssert.Near(speed, target.Speed, 1e-9,
                $"{team}: retained internal row does not slow a resistant recipient");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Inventory = [new InventorySlot(3022), null, null, null, null, null];
            runtime.Tick(0.01); // Items/3022.luaobj binds its copy on the next spell update (batch 147)
            runtime.HeroStats.Refresh(attacker);
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 50.0,
                DamageType.Physical, DamageSourceKind.Basic));
            var internalSlow = target.Buffs.Single(buff => buff.Name == "Internal_30Slow");
            var marker = target.Buffs.Single(buff => buff.Name == "ItemSlow");
            target.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            runtime.HeroStats.Refresh(target);
            FillItemBuffSlots(runtime, target, 64);
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"));
            internalSlow.ExpiresAt = marker.ExpiresAt = .25;
            runtime.Delivery.Flush();
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 50.0,
                DamageType.Physical, DamageSourceKind.Basic));
            runtime.Delivery.Flush();
            TestAssert.Near(2.0, internalSlow.ExpiresAt, 1e-9,
                $"{team}: existing internal slow renews at 64 slots");
            TestAssert.Near(2.0, marker.ExpiresAt, 1e-9,
                $"{team}: existing ItemSlow marker renews without allocating Boots");
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"));
            TestAssert.Equal(64, target.Buffs.Count);
        }
    }

    // Batch 147: Items/3082.luaobj ItemOnBeingHit adds Slow and then Cripple as two independent BBSpellBuffAdd calls
    // (BUFF_STACKS_AND_OVERLAPS, MaxStack 100, 3 s each) and never looks at the buff table. The pair rule below is the
    // emulator's, not the script's: NOT RETAIL, the 64-slot table is a client limit the scripts never guard, so one proc
    // takes every slot it needs or none of them (BuffLifecycle's admission helpers, BbBuffRuntime.CommitProcAdds).
    [Check("items-live-retaliatory-slow-pair-capacity")]
    public static void RetaliatoryArmorProcNeedsBothSlotsAfterItsRoll()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (used, boots, expectedPair) in new[]
                 {
                     (64, false, false), (63, false, false), (62, false, true),
                     (64, true, false), (62, true, false), (61, true, true),
                 })
        {
            var random = new CountingProcRandom();
            using var match = new Match("Annie", team, random: random);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var defender = match.Hero(1);
            defender.Inventory = [new InventorySlot(3082), null, null, null, null, null];
            if (boots) attacker.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            runtime.HeroStats.Refresh(defender);
            runtime.HeroStats.Refresh(attacker);
            // Batch 147: 3082's retaliation is Items/3082.luaobj ItemOnBeingHit now. A copy binds to its inventory slot on the
            // next spell update (BbItemModule.SyncInventory), the way a real buy does, so give the runtime that update first.
            runtime.Tick(0.01);
            FillItemBuffSlots(runtime, attacker, used);
            runtime.Delivery.Flush();
            match.Clear();
            var rollsBefore = random.Draws;
            // Batch 222: the armor proc's roll is the BuildingBlocks roll on the match's Lua stream (the match RNG is not drawn).
            var luaBefore = runtime.World.LuaRandom.HoldRand;
            var defenderHealth = defender.Health;
            // Batch 222: the hit's rolls are BuildingBlocks rolls on the match's Lua stream, not the match RNG this counting source fed: the
            // stream's next three draws pass, as every roll returned 0.0 before.
            LuaRollSeam.Passing(match.Runtime.World.LuaRandom, 3);
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, defender.NetId, 50.0,
                DamageType.Physical, DamageSourceKind.Basic));
            runtime.Delivery.Flush();
            TestAssert.True(defender.Health < defenderHealth,
                $"{team}/{used}/{boots}: retaliatory admission does not cancel hit damage");
            // Batch 222: the roll is a draw of the match's Lua stream (BbSpellHost.Random), so the stream must have moved.
            TestAssert.True(runtime.World.LuaRandom.HoldRand != luaBefore,
                $"{team}/{used}/{boots}: armor proc still consumes its random roll");
            TestAssert.Equal(expectedPair, attacker.Buffs.Any(buff => buff.Name == "Slow"));
            TestAssert.Equal(expectedPair, attacker.Buffs.Any(buff => buff.Name == "Cripple"));
            if (!expectedPair)
            {
                TestAssert.False(match.Captures[0].Packets.Any(packet =>
                    IsBuffAdd(packet, attacker, "Slow") || IsBuffAdd(packet, attacker, "Cripple")),
                    $"{team}/{used}/{boots}: denied pair has no partial wire");
                continue;
            }

            var packets = match.Captures[0].Packets;
            var slowIndex = packets.FindIndex(packet => IsBuffAdd(packet, attacker, "Slow"));
            var crippleIndex = packets.FindIndex(packet => IsBuffAdd(packet, attacker, "Cripple"));
            TestAssert.True(slowIndex >= 0 && crippleIndex > slowIndex,
                $"{team}/{used}/{boots}: successful retaliation retains Slow-before-Cripple wire order");
            TestAssert.Equal(64, attacker.Buffs.Count);
            match.Clear();
            defenderHealth = defender.Health;
            // Batch 222: the hit's rolls are BuildingBlocks rolls on the match's Lua stream, not the match RNG this counting source fed: the
            // stream's next three draws pass, as every roll returned 0.0 before.
            LuaRollSeam.Passing(match.Runtime.World.LuaRandom, 3);
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, defender.NetId, 50.0,
                DamageType.Physical, DamageSourceKind.Basic));
            runtime.Delivery.Flush();
            TestAssert.True(defender.Health < defenderHealth);
            TestAssert.Equal(1, attacker.Buffs.Count(buff => buff.Name == "Slow"));
            TestAssert.Equal(1, attacker.Buffs.Count(buff => buff.Name == "Cripple"));
            TestAssert.Equal(64, attacker.Buffs.Count);
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, random: new CountingProcRandom());
            var attacker = match.Hero(0);
            var defender = match.Hero(1);
            defender.Inventory = [new InventorySlot(3082), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(defender);
            // Batch 147: see above — the copy of Items/3082.luaobj binds on the next spell update.
            match.Runtime.Tick(0.01);
            TestAssert.True(match.Runtime.SpellBuffs.Add(attacker, "ResistantSkin",
                BuffType.Aura, 10.0, attacker.NetId) is not null);
            FillItemBuffSlots(match.Runtime, attacker, 63);
            // Batch 222: the hit's rolls are BuildingBlocks rolls on the match's Lua stream, not the match RNG this counting source fed: the
            // stream's next three draws pass, as every roll returned 0.0 before.
            LuaRollSeam.Passing(match.Runtime.World.LuaRandom, 3);
            TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, defender.NetId,
                50.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.False(attacker.Buffs.Any(buff => buff.Name == "Slow"),
                $"{team}: resistant attacker still rejects hostile Slow");
            TestAssert.True(attacker.Buffs.Any(buff => buff.Name == "Cripple"),
                $"{team}: immune Slow leaves one eligible retaliation slot for Cripple");
        }
    }

    private static bool IsBuffAdd(PlaintextPacket packet, Entity target, string name) =>
        packet.Plaintext.Length >= 13 && Opcode(packet) == SpellPackets.BuffAddOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
        SpellPackets.BuffNameHash(name);

    private sealed class CountingProcRandom : IRandomSource
    {
        public int Draws { get; private set; }
        public double NextDouble() { Draws++; return 0.0; }
    }
}
