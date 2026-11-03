using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Items;

/// <summary>
/// Focused production-graph checks for BUFF_RENEW_EXISTING item passives.
/// These deliberately inspect authoritative buff identity and packet churn;
/// the parent item corpus covers the ordinary first-application formulas.
/// </summary>
public static class ItemRenewalLiveChecks
{
    [Check("items-live-item-slow-binding-producers")]
    public static void ItemSlowBindingCoversMap1Producers()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            RunBindingProducer(team, 3116, DamageSourceKind.Spell, "Internal_35Slow",
                random: null);
            RunBindingProducer(team, 3022, DamageSourceKind.Basic, "Internal_30Slow",
                random: null);
            RunBindingProducer(team, 3044, DamageSourceKind.Basic, "Internal_30Slow",
                random: new AnnieChecks.FixedRandomSource(0.1));
            RunBindingProducer(team, 3078, DamageSourceKind.Basic, "Internal_35Slow",
                random: new AnnieChecks.FixedRandomSource(0.1));
        }
    }

    private static void RunBindingProducer(Team attackingTeam, int itemId,
        DamageSourceKind sourceKind, string internalName, IRandomSource? random)
    {
        // Script wins (batch 147): every shipped Items/<id>.luaobj item-slow producer (3022, 3044, 3078 with
        // the spell-cast group and 3116 with the damage group) renews ItemSlow with BUFF_RENEW_EXISTING and
        // AttackerVar=Attacker, so a second attacker's hit rebinds the marker to that incoming attacker
        // (BuffLifecycle.AddStackingBuff writes existing.CasterNetId); the Internal_*Slow rows are the ones
        // bound to the target (AttackerVar=Target). 3116 used to be hand-written and renewed the marker in
        // place, keeping its original owner; its shipped script binds the marker to the attacker like the rest.
        var markerFollowsAttacker = itemId is 3022 or 3044 or 3078 or 3116;
        var defendingTeam = attackingTeam == Team.Order ? Team.Chaos : Team.Order;
        var fixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "BindingOwner", "Annie", attackingTeam),
            new SessionParticipantDefinition(2, 1, "BindingSecondOwner", "Annie", attackingTeam),
            new SessionParticipantDefinition(3, 2, "BindingTarget", "Orianna", defendingTeam),
        ], random);
        var runtime = fixture.Runtime;
        foreach (var hero in runtime.World.Heroes()) hero.HeroAutoAcquire = false;
        var source = fixture.Hero(0);
        var secondSource = fixture.Hero(1);
        var target = fixture.Hero(2);
        source.Position = secondSource.Position = target.Position = new Position2(7000, 7000);
        source.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
        secondSource.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
        // Batch 147: the script producers (3022, 3044, 3078) bind their Items/<id>.luaobj copy to the inventory
        // slot on the next spell update (BbItemModule.SyncInventory), the way a real buy does.
        runtime.Tick(0.01);
        target.MaxHealth = target.Health = 100000.0;
        target.Armor = target.SpellBlock = 0.0;
        runtime.HeroStats.Refresh(source, push: false);
        runtime.HeroStats.Refresh(secondSource, push: false);
        runtime.HeroStats.Refresh(target, push: false);
        var capture = AttachCapture(runtime, source);
        capture.Clear();

        // Batch 222: each hit's roll is the BuildingBlocks roll on the match's Lua stream, not the match RNG this source feeds:
        // the next Lua draw is the 0.1 the source always gave.
        if (random is not null) LuaRollSeam.Next(runtime.World.LuaRandom, 0.1);
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            itemId == 3116 ? DamageType.Magic : DamageType.Physical, sourceKind),
            $"{attackingTeam} {itemId} first producer hit lands");
        runtime.Delivery.Flush();
        var first = target.Buffs.Single(buff => buff.Name == internalName);
        var slot = first.Slot;
        var percent = BuffLifecycle.ItemSlowRows.Single(row => row.Name == internalName).Percent;
        TestAssert.Equal(target.NetId, first.CasterNetId,
            $"{attackingTeam} {itemId} first internal row binds target");
        TestAssert.True(TryBuffAdd(capture.Packets, target.NetId,
                SpellPackets.BuffNameHash(internalName), out var firstWireSlot,
                out var firstWireCaster),
            $"{attackingTeam} {itemId} first internal row has a decoded 0xBF add");
        TestAssert.Equal((byte)slot, firstWireSlot,
            $"{attackingTeam} {itemId} first internal row preserves its slot");
        TestAssert.Equal(target.NetId, firstWireCaster,
            $"{attackingTeam} {itemId} first internal row publishes target caster");
        var marker = target.Buffs.Single(buff => buff.Name == "ItemSlow");
        TestAssert.Equal(source.NetId, marker.CasterNetId,
            $"{attackingTeam} {itemId} marker remains source-bound");
        TestAssert.True(TryBuffAdd(capture.Packets, target.NetId,
                SpellPackets.BuffNameHash("ItemSlow"), out var firstMarkerSlot, // Batch 357 (restates batch 181): the binding; Frozen Mallet Slow has no script
                out var firstMarkerCaster),
            $"{attackingTeam} {itemId} first marker has a decoded 0xBF add");
        TestAssert.Equal((byte)marker.Slot, firstMarkerSlot,
            $"{attackingTeam} {itemId} first marker preserves its slot");
        TestAssert.Equal(source.NetId, firstMarkerCaster,
            $"{attackingTeam} {itemId} first marker publishes its owner");
        // Script wins (batch 147): the magnitude lands on the ItemSlow marker (Spells/ItemSlow.luaobj's
        // cascade) and the self-bound row stays empty. This is also where the archived on-hit corpus rows
        // of the family now live, so the numbers stay covered here.
        TestAssert.Near(-percent, marker.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} {itemId} marker carries the shipped slow magnitude");
        TestAssert.Near(0.0, first.Payload.MoveSpeedMult, 1e-9,
            $"{attackingTeam} {itemId} internal row carries no penalty");

        runtime.Buffs.TickBuffs(target, 0.5);
        capture.Clear();
        // Batch 222: each hit's roll is the BuildingBlocks roll on the match's Lua stream, not the match RNG this source feeds:
        // the next Lua draw is the 0.1 the source always gave.
        if (random is not null) LuaRollSeam.Next(runtime.World.LuaRandom, 0.1);
        TestAssert.True(runtime.Combat.ApplyHit(secondSource.NetId, target.NetId, 1.0,
            itemId == 3116 ? DamageType.Magic : DamageType.Physical, sourceKind),
            $"{attackingTeam} {itemId} second-attacker renewal lands");
        runtime.Delivery.Flush();
        var renewed = target.Buffs.Single(buff => buff.Name == internalName);
        TestAssert.True(ReferenceEquals(first, renewed),
            $"{attackingTeam} {itemId} renewal keeps the internal instance");
        TestAssert.Equal(slot, renewed.Slot,
            $"{attackingTeam} {itemId} renewal keeps the internal slot");
        TestAssert.Equal(target.NetId, renewed.CasterNetId,
            $"{attackingTeam} {itemId} renewal keeps target caster binding");
        TestAssert.True(TryBuffRenewal(capture.Packets, target.NetId, (byte)slot, out var renewedWireSlot,
                out var renewedWireCaster),
            $"{attackingTeam} {itemId} renewal has a decoded renewal packet");
        TestAssert.Equal((byte)slot, renewedWireSlot,
            $"{attackingTeam} {itemId} renewal preserves its wire slot");
        TestAssert.Equal(target.NetId, renewedWireCaster,
            $"{attackingTeam} {itemId} renewal publishes target caster");
        var expectedMarkerOwner = markerFollowsAttacker ? secondSource.NetId : source.NetId;
        TestAssert.Equal(expectedMarkerOwner,
            target.Buffs.Single(buff => buff.Name == "ItemSlow").CasterNetId,
            markerFollowsAttacker
                ? $"{attackingTeam} {itemId} renewed marker follows the incoming attacker (script AttackerVar=Owner)"
                : $"{attackingTeam} {itemId} renewed marker preserves its original owner");
        TestAssert.True(TryBuffRenewal(capture.Packets, target.NetId, (byte)marker.Slot, out var renewedMarkerSlot,
                out var renewedMarkerCaster),
            $"{attackingTeam} {itemId} renewed marker has a decoded renewal packet");
        TestAssert.Equal((byte)marker.Slot, renewedMarkerSlot,
            $"{attackingTeam} {itemId} renewed marker preserves its slot");
        TestAssert.Equal(expectedMarkerOwner, renewedMarkerCaster,
            markerFollowsAttacker
                ? $"{attackingTeam} {itemId} renewed marker publishes the incoming attacker on wire"
                : $"{attackingTeam} {itemId} renewed marker preserves its original owner on wire");
    }

    [Check("items-live-item-slow-renewal")]
    public static void ItemSlowRenewsIdentityAndReconcilesOverlap()
    {
        var fixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "SlowOwner", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "SlowSecondOwner", "Orianna", Team.Order),
            new SessionParticipantDefinition(3, 2, "SlowTarget", "Orianna", Team.Chaos),
        ]);
        var runtime = fixture.Runtime;
        foreach (var hero in runtime.World.Heroes()) hero.HeroAutoAcquire = false;
        var source = fixture.Hero(0);
        var secondSource = fixture.Hero(1);
        var target = fixture.Hero(2);
        source.Position = secondSource.Position = target.Position = new Position2(7000, 7000);
        source.Inventory = [new InventorySlot(3116), null, null, null, null, null];
        // Batch 147 step 4: Items/3116.luaobj runs its slow from its own damage hooks now, so the copy has to
        // be bound to the slot first (BbItemModule.SyncInventory, the same latency the 3022 cases below use).
        runtime.Tick(0.01);
        target.MaxHealth = target.Health = 100000.0;
        target.SpellBlock = 0.0;
        runtime.HeroStats.Refresh(source, push: false);
        runtime.HeroStats.Refresh(target, push: false);
        var capture = AttachCapture(runtime, source);
        capture.Clear();
        var baseSpeed = target.Speed;

        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.Spell), "first spell slow lands");
        runtime.Delivery.Flush();
        var strong = target.Buffs.Single(buff => buff.Name == "Internal_35Slow");
        var strongSlot = strong.Slot;
        // Script wins (batch 147): Spells/ItemSlow.luaobj puts the movement modifier on the ItemSlow
        // marker it selects with, and the Internal_*Slow rows stay empty. The old check asserted the
        // penalty on the strongest row.
        var slowMarker = target.Buffs.Single(buff => buff.Name == "ItemSlow");
        TestAssert.Near(-0.35, slowMarker.Payload.MoveSpeedMult, 1e-9,
            "first Rylai spell slow applies its movement modifier to the marker");
        TestAssert.Near(0.0, strong.Payload.MoveSpeedMult, 1e-9,
            "first Rylai row stays empty");
        var speedWithStrong = target.Speed;

        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.Spell), "repeated spell slow lands");
        runtime.Delivery.Flush();
        var renewed = target.Buffs.Single(buff => buff.Name == "Internal_35Slow");
        TestAssert.True(ReferenceEquals(strong, renewed),
            "repeated Rylai slow keeps the authoritative buff object");
        TestAssert.Equal(strongSlot, renewed.Slot,
            "repeated Rylai slow keeps its native slot");
        TestAssert.Near(-0.35, slowMarker.Payload.MoveSpeedMult, 1e-9,
            "renewal preserves the selected slow on the marker");
        TestAssert.Near(1.5, renewed.ExpiresAt, 1e-9,
            "repeated Rylai slow renews its full duration");
        TestAssert.False(capture.Packets.Any(IsBuffRemove),
            "repeated Rylai slow emits no remove/re-add churn");

        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.SpellAoe), "weaker spell-area slow lands");
        runtime.Delivery.Flush();
        var weak = target.Buffs.Single(buff => buff.Name == "Internal_15Slow");
        TestAssert.Near(-0.35, slowMarker.Payload.MoveSpeedMult, 1e-9,
            "stronger overlapping slow remains selected on the marker");
        TestAssert.Near(0.0, weak.Payload.MoveSpeedMult, 1e-9,
            "weaker overlapping row contributes no movement modifier");
        TestAssert.Near(speedWithStrong, target.Speed, 1e-9,
            "weaker overlap does not change the effective movement speed");

        renewed.ExpiresAt = 0.1;
        runtime.Buffs.TickBuffs(target, 0.2);
        TestAssert.False(target.Buffs.Contains(renewed), "stronger slow expires");
        TestAssert.Near(-0.15, slowMarker.Payload.MoveSpeedMult, 1e-9,
            "weaker slow becomes the marker's selection when the stronger one expires");
        TestAssert.True(target.Speed > speedWithStrong,
            "effective speed rises after the stronger slow expires");

        weak.ExpiresAt = 0.1;
        runtime.Buffs.TickBuffs(target, 0.2);
        TestAssert.Near(baseSpeed, target.Speed, 1e-9,
            "all internal slows are removed without a residual speed penalty");

        source.Inventory = [new InventorySlot(3022), null, null, null, null, null];
        secondSource.Inventory = [new InventorySlot(3022), null, null, null, null, null];
        // Batch 147: Items/3022.luaobj binds its copy on the next spell update (BbItemModule.SyncInventory).
        runtime.Tick(0.01);
        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic), "first item on-hit slow lands");
        runtime.Delivery.Flush();
        var onHit = target.Buffs.Single(buff => buff.Name == "Internal_30Slow");
        var onHitSlot = onHit.Slot;
        TestAssert.Equal(target.NetId, onHit.CasterNetId,
            "first item on-hit internal row binds its target");
        TestAssert.True(TryBuffAdd(capture.Packets, target.NetId,
                SpellPackets.BuffNameHash("Internal_30Slow"), out var firstWireSlot,
                out var firstWireCaster),
            "first item on-hit internal row has a decoded 0xBF add");
        TestAssert.Equal((byte)onHitSlot, firstWireSlot,
            "first item on-hit internal row keeps its authoritative slot on wire");
        TestAssert.Equal(target.NetId, firstWireCaster,
            "first item on-hit internal row publishes target caster on wire");
        TestAssert.Near(2.0, onHit.ExpiresAt, 1e-9,
            "first item on-hit slow uses its full duration");
        runtime.Buffs.TickBuffs(target, 0.5);
        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(secondSource.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic), "second item on-hit slow lands");
        runtime.Delivery.Flush();
        var renewedOnHit = target.Buffs.Single(buff => buff.Name == "Internal_30Slow");
        TestAssert.True(ReferenceEquals(onHit, renewedOnHit),
            "same-item on-hit slow keeps its authoritative instance");
        TestAssert.Equal(onHitSlot, renewedOnHit.Slot,
            "same-item on-hit slow keeps its native slot");
        TestAssert.Equal(target.NetId, renewedOnHit.CasterNetId,
            "same-item on-hit renewal keeps target caster binding");
        TestAssert.True(TryBuffRenewal(capture.Packets, target.NetId, (byte)onHitSlot, out var renewedWireSlot,
                out var renewedWireCaster),
            "renewed item on-hit internal row has a decoded 0x32 renewal");
        TestAssert.Equal((byte)onHitSlot, renewedWireSlot,
            "renewed item on-hit internal row keeps its authoritative slot on wire");
        TestAssert.Equal(target.NetId, renewedWireCaster,
            "renewed item on-hit internal row publishes target caster on wire");
        TestAssert.Near(2.0, renewedOnHit.ExpiresAt, 1e-9,
            "same-item on-hit renewal restores the full duration");
        TestAssert.False(capture.Packets.Any(IsBuffRemove),
            "same-item on-hit renewal emits no remove/re-add churn");

        source.Inventory = [new InventorySlot(3116), null, null, null, null, null];
        var dragonSpawnAt = NeutralDefinitions.InitialSpawnSeconds +
            NeutralDefinitions.Camps[6].InitialDelay;
        runtime.Combat.RestoreClock(dragonSpawnAt);
        runtime.Tick(0.01);
        var boss = runtime.World.Entities.Values.Single(entity =>
            entity.Name == "Dragon" && entity.Alive);
        source.Position = boss.Position;
        var dragonSpeed = boss.Speed;
        var dragonHealth = boss.Health;
        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(source.NetId, boss.NetId, 1.0,
            DamageType.Magic, DamageSourceKind.Spell),
            "real scheduled Dragon accepts damage while rejecting hostile slow control");
        TestAssert.True(boss.Health < dragonHealth,
            "real scheduled Dragon damage remains observable");
        TestAssert.False(boss.Buffs.Any(buff => buff.Name == "ItemSlow"),
            "real scheduled Dragon rejects the hostile ItemSlow marker");
        var dragonRows = boss.Buffs.Where(buff =>
            BuffLifecycle.IsItemSlowRow(buff.Name)).ToArray();
        TestAssert.Equal(1, dragonRows.Length,
            "real scheduled Dragon retains one self-bound internal slow row");
        TestAssert.Equal(boss.NetId, dragonRows[0].CasterNetId,
            "real scheduled Dragon internal row binds to itself");
        TestAssert.Near(0.0, dragonRows[0].Payload.MoveSpeedMult, 1e-9,
            "real scheduled Dragon internal row has no movement contribution");
        TestAssert.Near(dragonSpeed, boss.Speed, 1e-9,
            "real scheduled Dragon speed remains unchanged");
    }

    [Check("items-live-mourning-renewal")]
    public static void MourningRenewsPayloadAndPeriodicClockInPlace()
    {
        var fixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "BleedOne", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "BleedTwo", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "BleedTarget", "LeeSin", Team.Chaos),
        ]);
        var runtime = fixture.Runtime;
        var first = fixture.Hero(0);
        var second = fixture.Hero(1);
        var target = fixture.Hero(2);
        first.Position = second.Position = target.Position = new Position2(7000, 7000);
        first.Inventory = [new InventorySlot(3123), null, null, null, null, null];
        second.Inventory = [new InventorySlot(3123), null, null, null, null, null];
        first.AttackDamage = 42.0;
        second.AttackDamage = 77.0;
        target.MaxHealth = target.Health = 100000.0;
        target.SpellBlock = 0.0;
        runtime.HeroStats.Refresh(target, push: false);
        target.Armor = 0.0;
        target.SpellBlock = 0.0;
        // Batch 147: Mourning is Items/3123.luaobj ItemOnHitUnit's own add now. The copy binds to its
        // inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real buy does, so
        // both buyers get that one update before the first hit.
        runtime.Tick(0.01);
        var capture = AttachCapture(runtime, first);
        capture.Clear();

        var beforeHit = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(first.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic), "first Mourning hit lands");
        TestAssert.Near(1.0, beforeHit - target.Health, 1e-9,
            "Mourning does not pulse synchronously with the item hit");
        runtime.Delivery.Flush();
        var mourning = target.Buffs.Single(buff => buff.Name == "Mourning");
        var slot = mourning.Slot;
        TestAssert.Near(4.0, mourning.Payload.DamagePerTick, 1e-9,
            "first Mourning payload is Mourning.luaobj Damage=4, whatever the attacker damage");

        capture.Clear();
        var beforePreRenewal = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(first.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic),
            "Mourning renews before its first update");
        TestAssert.Near(1.0, beforePreRenewal - target.Health, 1e-9,
            "pre-update Mourning renewal does not pulse with the hit");
        runtime.Delivery.Flush();
        TestAssert.True(ReferenceEquals(mourning, target.Buffs.Single(buff => buff.Name == "Mourning")),
            "pre-update Mourning renewal keeps the buff instance");
        TestAssert.Equal(first.NetId, mourning.CasterNetId,
            "pre-update Mourning renewal keeps its caster");
        TestAssert.False(capture.Packets.Any(IsBuffRemove),
            "pre-update Mourning renewal emits no remove/re-add churn");

        // Batch 147: the periodic clock is the script's own now. Mourning.luaobj schedules its pulses with
        // BBExecutePeriodically (proto 0.70: LastTimeExecuted is seeded on the first buff update, the child
        // fires at once, and at most one delayed child runs per update with the overdue time kept), so
        // BuffLifecycle's PeriodElapsed bookkeeping is payload metadata and is not advanced here any more.
        // The clock BBExecutePeriodically reads is GetTime(), which a match advances in SpellSystem.Tick before
        // it ticks the unit's buffs (ProductionMatchComposition.Tick); a bare BuffLifecycle.TickBuffs leaves it
        // where it was. The cadence below therefore runs the same phase pair a source step does: one pulse on
        // the first update, one per second after it, and one per update when a coarse step leaves them backlogged.
        void Step(double dt)
        {
            runtime.Spells.Tick(dt);
            runtime.Buffs.TickBuffs(target, dt);
        }

        var beforeImmediate = target.Health;
        Step(0.01);
        TestAssert.Near(beforeImmediate - 4.0, target.Health, 1e-9,
            "Mourning pulses once on its first update");

        var beforeHalf = target.Health;
        Step(0.4);
        TestAssert.Near(beforeHalf, target.Health, 1e-9,
            "Mourning does not pulse again inside its one-second period");
        capture.Clear();
        TestAssert.True(runtime.Combat.ApplyHit(second.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic), "second Mourning hit lands");
        runtime.Delivery.Flush();
        var renewed = target.Buffs.Single(buff => buff.Name == "Mourning");
        TestAssert.True(ReferenceEquals(mourning, renewed),
            "repeated Mourning keeps the authoritative buff object");
        TestAssert.Equal(slot, renewed.Slot, "repeated Mourning keeps its native slot");
        // The renewal keeps the script's LastTimeExecuted with the instance, so period progress survives it;
        // the 0.6 s tick below is what reads it (exactly one pulse).
        TestAssert.Near(4.0, renewed.Payload.DamagePerTick, 1e-9,
            "Mourning renewal keeps the flat 4 payload of the incoming attacker");
        TestAssert.Equal(second.NetId, renewed.CasterNetId,
            "Mourning renewal records the incoming attacker");
        TestAssert.Near(8.0, renewed.ExpiresAt, 1e-9,
            "Mourning renewal restores the full duration");
        TestAssert.True(TryBuffRenewal(capture.Packets, target.NetId, (byte)slot, out var renewedWireSlot,
                out var renewedWireCaster),
            "Mourning renewal reaches the client as a decoded renewal packet");
        TestAssert.Equal((byte)slot, renewedWireSlot, "Mourning renewal preserves its wire slot");
        TestAssert.Equal(second.NetId, renewedWireCaster,
            "Mourning renewal publishes the incoming attacker on the wire");
        TestAssert.False(capture.Packets.Any(IsBuffRemove),
            "Mourning renewal emits no remove/re-add churn");

        var beforeDot = target.Health;
        Step(0.6);
        TestAssert.Near(beforeDot - 4.0, target.Health, 1e-9,
            "Mourning continues its DOT from the preserved periodic clock");

        var beforeCoarse = target.Health;
        Step(2.4);
        TestAssert.Near(beforeCoarse - 4.0, target.Health, 1e-9,
            "Mourning emits at most one delayed pulse in a coarse update");
        capture.Clear();
        var beforeBacklogRenewal = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(second.NetId, target.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic),
            "Mourning renews while a delayed pulse is backlogged");
        TestAssert.Near(1.0, beforeBacklogRenewal - target.Health, 1e-9,
            "backlogged Mourning renewal does not pulse with the hit");
        runtime.Delivery.Flush();
        renewed = target.Buffs.Single(buff => buff.Name == "Mourning");
        TestAssert.True(ReferenceEquals(mourning, renewed),
            "backlogged Mourning renewal keeps the buff instance");
        TestAssert.False(capture.Packets.Any(IsBuffRemove),
            "backlogged Mourning renewal emits no remove/re-add churn");
        beforeCoarse = target.Health;
        runtime.Buffs.TickBuffs(target, 0.1);
        TestAssert.Near(beforeCoarse - 4.0, target.Health, 1e-9,
            "Mourning drains one retained backlog pulse per update");

        renewed.ExpiresAt = 0.1;
        capture.Clear();
        var beforeExpiry = target.Health;
        runtime.Buffs.TickBuffs(target, 0.2);
        // Reconstruction (wire grace): the expiry's remove is held through its own step and goes out at the end of the next one.
        runtime.Buffs.EndTick();
        runtime.Buffs.EndTick();
        runtime.Delivery.Flush();
        TestAssert.Near(beforeExpiry, target.Health, 1e-9,
            "Mourning does not emit an unready pulse before expiry");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Mourning"),
            "Mourning has one final expiry");
        TestAssert.Equal(1, capture.Packets.Count(IsBuffRemove),
            "Mourning expiry sends one final buff removal");
        var afterRemoval = target.Health;
        runtime.Buffs.TickBuffs(target, 1.0);
        TestAssert.Near(afterRemoval, target.Health, 1e-9,
            "Mourning does not damage after its removal");

        var reverseFixture = StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "ReverseBleedSource", "Annie", Team.Chaos),
            new SessionParticipantDefinition(2, 1, "ReverseBleedTarget", "LeeSin", Team.Order),
        ]);
        var reverseRuntime = reverseFixture.Runtime;
        var reverseSource = reverseFixture.Hero(0);
        var reverseTarget = reverseFixture.Hero(1);
        reverseSource.Position = reverseTarget.Position = new Position2(7000, 7000);
        reverseSource.Inventory = [new InventorySlot(3123), null, null, null, null, null];
        reverseSource.AttackDamage = 31.0;
        reverseTarget.MaxHealth = reverseTarget.Health = 100000.0;
        reverseTarget.Armor = reverseTarget.SpellBlock = 0.0;
        reverseRuntime.Tick(0.01);
        TestAssert.True(reverseRuntime.Combat.ApplyHit(reverseSource.NetId, reverseTarget.NetId, 1.0,
            DamageType.Physical, DamageSourceKind.Basic),
            "reverse-team Mourning hit lands");
        var reverseBeforePulse = reverseTarget.Health;
        reverseRuntime.Buffs.TickBuffs(reverseTarget, 0.01);
        TestAssert.Near(reverseBeforePulse - 4.0, reverseTarget.Health, 1e-9,
            "reverse-team Mourning pulses on its first update");
    }

    private static ProductionFixture StartProduction(
        IReadOnlyList<SessionParticipantDefinition> roster, IRandomSource? random = null)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, random: random, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        var connections = new long[roster.Count];
        for (var index = 0; index < roster.Count; index++)
        {
            var connection = 100L + index;
            connections[index] = connection;
            TestAssert.True(runtime.Session.Authenticate(connection,
                runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"production seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection),
                $"production seat {index} reaches ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "production item renewal fixture starts");
        return new ProductionFixture(runtime, connections);
    }

    private static CaptureTransport AttachCapture(MatchRuntime runtime, Entity observerHero)
    {
        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(1, observerHero.Team,
            observerHero.NetId, capture));
        runtime.Delivery.Flush();
        return capture;
    }

    private static bool IsBuffRemove(PlaintextPacket packet) =>
        packet.Plaintext.Length > 0 && packet.Plaintext.Span[0] == 0x7F;

    // A renewal of a buff the client holds is a silent timer restart of that one instance, never a re-add. Batch 157
    // part 3 split the two packets the wire has for it by what the buff's own shipped template hands the client:
    // NpcBuffReplace (0x32, slot @5, caster @14) ends in the client's torch restart, which deactivates and
    // re-creates the four AutoBuffActivateEffect particles of the instance — so a renewal of a buff with such a torch
    // (ItemSlow's GLOBAL_FREEZE.TROY, Mourning's bleeding_GLB2_tar.troy) is NpcBuffUpdateCount (0x1F, slot @5,
    // caster @15), whose handler resizes the same timers and restarts the torch only where a particle slot is empty. A
    // buff with no torch has nothing to re-present and keeps the 0x32 it always had. Both are one renewal of one
    // instance; which one the aura family takes is pinned by items-live-aura-item-buff-wire-does-not-re-present-the-client-torch.
    private static bool TryBuffRenewal(IReadOnlyList<PlaintextPacket> packets,
        uint targetNetId, byte expectedSlot, out byte slot, out uint casterNetId)
    {
        slot = 0;
        casterNetId = 0;
        foreach (var packet in packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 18 || bytes[5] != expectedSlot ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != targetNetId)
                continue;
            if (bytes[0] == 0x32)
            {
                slot = bytes[5];
                casterNetId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
                return true;
            }
            if (bytes[0] == 0x1F && bytes.Length >= 19)
            {
                slot = bytes[5];
                casterNetId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]);
                return true;
            }
        }
        return false;
    }

    private static bool TryBuffAdd(IReadOnlyList<PlaintextPacket> packets,
        uint targetNetId, uint buffHash, out byte slot, out uint casterNetId)
    {
        slot = 0;
        casterNetId = 0;
        foreach (var packet in packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 25 || bytes[0] != SpellPackets.BuffAddOpcode ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != targetNetId ||
                bytes[5] >= SpellPackets.MaxBuffSlots ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) != buffHash)
                continue;
            slot = bytes[5];
            casterNetId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return true;
        }
        return false;
    }

    private sealed record ProductionFixture(MatchRuntime Runtime, long[] Connections)
    {
        public Entity Hero(int index) => Runtime.Session.Participants[index].Hero!
            ?? throw new InvalidOperationException($"hero {index} missing");
    }

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
        public void Clear() => Packets.Clear();
    }
}

