using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-hexdrinker-presentation")]
    public static void HexdrinkerEmitsTriggerAndCanonicalShieldLifecycle()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.Inventory = [new InventorySlot(3155), null, null, null, null, null];
            // Batch 147 step 4: Items/3155.luaobj runs the trigger from its own ItemOnPreDamage now, so the copy has
            // to be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update latency the 3022
            // cases already account for).
            match.Runtime.Tick(0.01);
            match.Runtime.HeroStats.Refresh(hero);
            hero.MaxHealth = 5000.0;
            hero.Health = 1200.0;
            match.Clear();

            TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                200.0, DamageType.Magic), $"{team}: magic hit lands");
            match.Runtime.Delivery.Flush();
            var effects = match.Captures[0].Packets
                .Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode)
                .ToArray();
            var birth = FindEffect(effects, "hexTech_dmg_shield_birth.troy");
            var onHit1 = FindEffect(effects, "hexTech_dmg_shield_onHit_01.troy");
            var onHit2 = FindEffect(effects, "hexTech_dmg_shield_onHit_02.troy");
            foreach (var packet in new[] { birth, onHit1, onHit2 })
            {
                TestAssert.Equal(hero.NetId, FxTarget(packet), $"{team}: effect target");
                TestAssert.Equal(hero.NetId, FxBinding(packet), $"{team}: effect owner binding");
            }
            TestAssert.False(effects.Any(packet => ReadUInt32(packet, 6) ==
                    SpellPackets.BuffNameHash("hexTech_dmg_shield_duration")),
                $"{team}: AutoBuffActivateEffect remains client-owned without duplicate server FX");
            TestAssert.True(match.Captures[0].Packets.Any(packet =>
                    Opcode(packet) == SpellPackets.BuffAddOpcode &&
                    ReadUInt32(packet, 9) == SpellPackets.BuffNameHash("Hexdrinker")), // Batch 357 (restates batch 181): the binding
                $"{team}: canonical shield buff is added");

            match.Clear();
            TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                25.0, DamageType.Magic), $"{team}: subsequent magic hit lands");
            match.Runtime.Delivery.Flush();
            var absorbEffects = match.Captures[0].Packets
                .Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode)
                .ToArray();
            TestAssert.Equal(2, absorbEffects.Length,
                $"{team}: shield hit emits only the recurring impact pair");
            TestAssert.False(absorbEffects.Any(packet => ReadUInt32(packet, 6) ==
                    SpellPackets.BuffNameHash("hexTech_dmg_shield_birth")),
                $"{team}: shield hit does not replay trigger birth");

            var shield = hero.Buffs.Single(buff => buff.Name == "Hexdrinker");
            // Script wins (batch 147 step 4): the budget of Spells/Hexdrinker.luaobj's shield is its own
            // InstanceVars.ShieldHealth (the NextBuffVars hand-off of Items/3155.luaobj's trigger), not an engine
            // payload shield. The script absorbs in BuffOnPreDamage and only keeps the displayed value in step with
            // BBIncreaseShield / BBReduceShield, so the hand-written Payload.MagicShield is empty here.
            var budget = (double)match.Runtime.Spells.ItemScripts!.Buffs!.Instances
                .Single(item => item.Name == "Hexdrinker").InstanceVars["ShieldHealth"]!;
            TestAssert.True(budget > 0.0 && budget < 300.0,
                $"{team}: partial magic shield stores the remaining 300-point budget");
            TestAssert.Near(4.0, shield.ExpiresAt, 1e-8,
                $"{team}: shield has the shipped four-second lifetime");
            var timer = hero.Buffs.Single(buff => buff.Name == "HexdrinkerTimer");
            TestAssert.Near(60.0, timer.ExpiresAt, 1e-8,
                $"{team}: trigger has the shipped sixty-second cooldown marker");

            match.Clear();
            match.Runtime.Tick(4.1);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Hexdrinker"),
                $"{team}: shield expires");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "HexdrinkerTimer"),
                $"{team}: cooldown marker outlives the shield");
            TestAssert.True(match.Captures[0].Packets.Any(packet =>
                    Opcode(packet) == 0x7F &&
                    ReadUInt32(packet, 6) == SpellPackets.BuffNameHash("Hexdrinker")), // Batch 357 (restates batch 181): the binding
                $"{team}: canonical shield buff is removed after four seconds");
        }
    }

    [Check("items-live-dynamic-tooltip-slots")]
    public static void DynamicItemTooltipsUseShippedPerSlotUnits()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        hero.Inventory = [new InventorySlot(3027), new InventorySlot(3072),
            new InventorySlot(3083), null, null, null];
        // Batch 149 step 1: the Bloodthirster's and Warmog's growth is the copy's own InstanceVars now
        // (Items/3072.luaobj's PhysicalDamageBonus / PercentLifeSteal, Items/3083.luaobj's ExtraHP / ExtraRegen),
        // and their UpdateSelfBuffStats writes both the stat delta and the two BBSetSpellToolTipVar rows from it.
        // Batch 149 step 5: Rod of Ages' ageing is the same shape — Items/3027.luaobj's own
        // InstanceVars.BonusHealth / BonusMana / BonusAbilityPower, whose UpdateSelfBuffStats writes the three
        // tooltip rows (Index 1 = BonusHealth, Index 2 = BonusMana, Index 3 = BonusAbilityPower) from them. The
        // copies bind to their slots on the next item poll (BbItemModule.SyncInventory), and only then is there
        // an InstanceVars table to write; the sample that reads it runs on the poll after that. Batch 246: the copies' bare
        // stat hooks sample on the hero's 0.25 s self-buff line (BbSelfUpdate), so the next sample is a quarter second on.
        match.Runtime.Spells.Tick(.01);
        var rodOfAges = match.Runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(0)!;
        rodOfAges.InstanceVars["BonusHealth"] = 54.0;
        rodOfAges.InstanceVars["BonusMana"] = 60.0;
        rodOfAges.InstanceVars["BonusAbilityPower"] = 6.0;
        var bloodthirster = match.Runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(1)!;
        bloodthirster.InstanceVars["PhysicalDamageBonus"] = 7.0;
        bloodthirster.InstanceVars["PercentLifeSteal"] = 0.0075;
        var warmog = match.Runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(2)!;
        warmog.InstanceVars["ExtraHP"] = 90.0;
        warmog.InstanceVars["ExtraRegen"] = 0.60;
        match.Runtime.Spells.Tick(.25);
        match.Clear();

        match.Runtime.HeroStats.Refresh(hero, push: true);
        match.Runtime.Delivery.Flush();
        var entries = TooltipEntries(match.Captures[0].Packets);
        TestAssert.Equal(3, entries.GetArrayLength(), "one tooltip entry per equipped dynamic item");
        AssertTooltip(entries, 64, 54.0, 60.0, 6.0, "Rod of Ages");
        AssertTooltip(entries, 65, 7.0, 0.75, 0.0, "Bloodthirster percentage display");
        AssertTooltip(entries, 66, 90.0, 3.0, 0.0, "Warmog per-five-second display");

        // Shop swap and sale use the same producer through their real session
        // handlers while the hero is alive at the Order fountain. This keeps
        // the item state used by the tooltip assertions intact.
        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        // The shop-open request carries only its zero header. The session
        // connection already identifies the hero; a hero net ID here is
        // parsed as the native header and is rejected before range checking.
        var open = new byte[] { 0x60, 0, 0, 0, 0 };
        TestAssert.True(match.Runtime.Session.DispatchShop(new SessionInput(
            1, 1, match.Runtime.Cipher.Encrypt(open), open, true)),
            "living hero opens shop at the fountain for slot transition checks");
        match.Clear();
        var swap = new byte[7]; swap[0] = 0x23;
        BinaryPrimitives.WriteUInt32LittleEndian(swap.AsSpan(1), hero.NetId);
        swap[5] = 0; swap[6] = 1;
        TestAssert.True(match.Runtime.Session.DispatchShop(new SessionInput(
                1, 1, match.Runtime.Cipher.Encrypt(swap), swap, true)),
            "slot swap accepted");
        // Batch 149 step 1: the copy keeps its InstanceVars across the move and rewrites its tooltip row at the slot
        // it now holds on the next item poll — the shop's own publication carries the layout change at once and the
        // script's re-write of its two rows follows it in the same capture window (batch 246: the next poll is the hero's next
        // 0.25 s self-buff line).
        match.Runtime.Spells.Tick(.25);
        match.Runtime.Delivery.Flush();
        var swappedEntries = TooltipEntries(match.Captures[0].Packets);
        AssertTooltip(swappedEntries, 64, 7.0, 0.75, 0.0,
            "swapped Bloodthirster slot");
        AssertTooltip(swappedEntries, 65, 54.0, 60.0, 6.0,
            "swapped Rod of Ages slot");

        match.Clear();
        var sell = new byte[6]; sell[0] = 0x09;
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(1), hero.NetId);
        sell[5] = 0x80 | 1;
        TestAssert.True(match.Runtime.Session.DispatchShop(new SessionInput(
                1, 1, match.Runtime.Cipher.Encrypt(sell), sell, true)),
            "slot sale accepted");
        match.Runtime.Delivery.Flush();
        var soldEntries = TooltipEntries(match.Captures[0].Packets);
        TestAssert.Equal(2, soldEntries.GetArrayLength(),
            "sale republishes only remaining dynamic item slots");
        TestAssert.False(soldEntries.EnumerateArray().Any(value =>
                value.GetProperty("selector").GetInt32() == 65),
            "sold Rod of Ages slot has no tooltip record");
    }

    [Check("items-live-reconnect-dynamic-tooltips", Serial = true)]
    public static void ReconnectRestoresDynamicItemTooltips()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            readySettleSeconds: 0.0,
            roster:
            [
                new(1, 0, "Order", "Annie", Team.Order),
                new(2, 1, "Chaos", "Orianna", Team.Chaos),
            ]);
        using var network = new MatchRuntimeNetwork(runtime,
            new LeagueEnetHost(0, 3, 8, bindHost: "127.0.0.1"), ownsHost: true);
        using var clientA = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var clientB = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        using var clientC = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        runtime.World.Clock.SetVirtual(0.0);

        var peerA = clientA.Connect("127.0.0.1", network.Port, 8);
        PumpNative(network, [clientA], () => network.ConnectionIds.Count == 1);
        var peerB = clientB.Connect("127.0.0.1", network.Port, 8);
        PumpNative(network, [clientA, clientB], () => network.ConnectionIds.Count == 2);
        var serverConnections = network.ConnectionIds.ToArray();
        peerA.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        peerB.Send(0, runtime.Session.BuildKeyCheck(2), LeagueEnetPacketFlags.Reliable);
        clientA.Flush();
        clientB.Flush();
        PumpNative(network, [clientA, clientB], () =>
            runtime.Session.Participants.All(item => item.ConnectionId is not null));

        var orderConnection = runtime.Session.Participant(serverConnections[0]) is { PlayerId: 1 }
            ? serverConnections[0] : serverConnections[1];
        var chaosConnection = orderConnection == serverConnections[0]
            ? serverConnections[1] : serverConnections[0];
        var orderPeer = orderConnection == serverConnections[0] ? peerA : peerB;
        var orderClient = orderConnection == serverConnections[0] ? clientA : clientB;
        var chaosClient = chaosConnection == serverConnections[0] ? clientA : clientB;
        runtime.Session.Spawn(orderConnection);
        runtime.Session.Spawn(chaosConnection);
        runtime.Session.MarkReady(orderConnection);
        runtime.Session.MarkReady(chaosConnection);
        runtime.Session.BuildMapReadyPackets(orderConnection);
        runtime.Session.BuildMapReadyPackets(chaosConnection);
        TestAssert.True(runtime.Session.Start(), "production reconnect fixture starts");
        network.Pump(0, 32);
        DrainNative(orderClient, null);
        DrainNative(chaosClient, null);

        var participant = runtime.Session.Participant(orderConnection)
            ?? throw new InvalidOperationException("order participant missing");
        var hero = participant.Hero!;
        hero.Inventory = [new InventorySlot(3027), null, null, null, null, null];
        // Batch 149 step 5: Rod of Ages' tooltip rows are Items/3027.luaobj's own BBSetSpellToolTipVar writes from
        // its InstanceVars.BonusHealth / BonusMana / BonusAbilityPower (UpdateSelfBuffStats), so the copy has to
        // bind and then be sampled before there is a row to republish on the reconnect.
        runtime.Spells.Tick(.01);
        var rodOfAges = runtime.Spells.ItemScripts!.LoadoutOf(hero)!.Item(0)!;
        rodOfAges.InstanceVars["BonusHealth"] = 72.0;
        rodOfAges.InstanceVars["BonusMana"] = 80.0;
        rodOfAges.InstanceVars["BonusAbilityPower"] = 8.0;
        // Batch 246: one quarter-second step always crosses the hero's next self-buff line (BbSelfUpdate), where the bare stat hook samples.
        runtime.Spells.Tick(.25);
        runtime.HeroStats.Refresh(hero);

        orderPeer.Send(1, new byte[] { 0x94, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        orderClient.Flush();
        PumpNative(network, [clientA, clientB], () => participant.ConnectionId is null);

        var reconnectPeer = clientC.Connect("127.0.0.1", network.Port, 8);
        PumpNative(network, [clientA, clientB, clientC],
            () => network.ConnectionIds.Count == 2);
        reconnectPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        clientC.Flush();
        var baseline = new List<LeagueEnetEvent>();
        PumpNative(network, [clientA, clientB, clientC],
            () => participant.ConnectionId is long connectionId && connectionId != orderConnection,
            null, null, baseline);

        reconnectPeer.Send(1, new byte[] { 0xC5 }, LeagueEnetPacketFlags.Reliable);
        reconnectPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        clientC.Flush();
        PumpNative(network, [clientA, clientB, clientC],
            () => participant.Loaded, null, null, baseline);
        PumpNative(network, [clientA, clientB, clientC],
            () => participant.Spawned, null, null, baseline);
        TestAssert.False(baseline.Any(item => NativeOpcode(item, runtime.Cipher) == 0x83),
            "reconnect baseline does not publish item tooltips before inventory restoration");

        var ready = new List<LeagueEnetEvent>();
        reconnectPeer.Send(1, new byte[] { 0xC6, 0, 0, 0, 0 }, LeagueEnetPacketFlags.Reliable);
        clientC.Flush();
        PumpNative(network, [clientA, clientB, clientC],
            () => participant.Ready && ready.Any(item =>
                NativeOpcode(item, runtime.Cipher) == 0x83 &&
                TooltipContains(item, runtime.Cipher, hero.NetId, 64, 72.0, 80.0, 8.0)),
            null, null, ready);

        var setItemIndex = ready.FindIndex(item =>
            NativeOpcode(item, runtime.Cipher) == 0x56 &&
            NativeU32(item, runtime.Cipher, 1) == hero.NetId &&
            NativeU32(item, runtime.Cipher, 6) == 3027);
        var tooltipIndex = ready.FindIndex(item =>
            NativeOpcode(item, runtime.Cipher) == 0x83 &&
            TooltipContains(item, runtime.Cipher, hero.NetId, 64, 72.0, 80.0, 8.0));
        TestAssert.True(setItemIndex >= 0, "ready reconnect restores the dynamic item slot");
        TestAssert.True(tooltipIndex > setItemIndex,
            "ready reconnect publishes item tooltip after SetItem creates the item spell record");
    }

    private static void PumpNative(MatchRuntimeNetwork network,
        IReadOnlyList<LeagueEnetHost> clients, Func<bool> done,
        params List<LeagueEnetEvent>?[] captures)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done())
        {
            network.Pump(1, 32);
            for (var index = 0; index < clients.Count; index++)
                DrainNative(clients[index], index < captures.Length ? captures[index] : null);
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("native item tooltip fixture timed out");
        }
    }

    private static void DrainNative(LeagueEnetHost host, List<LeagueEnetEvent>? capture)
    {
        while (true)
        {
            var @event = host.Service(0);
            if (@event.Type == LeagueEnetEventType.None) return;
            capture?.Add(@event);
        }
    }

    private static byte[] NativePlaintext(LeagueEnetEvent @event, LegacyBlowfishCipher cipher)
    {
        if (@event.Type != LeagueEnetEventType.Receive || @event.Payload.Length == 0)
            return Array.Empty<byte>();
        if (@event.Payload.Length >= LegacyBlowfishCipher.BlockSize &&
            @event.Channel is 1 or 3 or 5)
            return cipher.Decrypt(@event.Payload);
        return @event.Payload;
    }

    private static byte NativeOpcode(LeagueEnetEvent @event, LegacyBlowfishCipher cipher)
    {
        var payload = NativePlaintext(@event, cipher);
        return payload.Length == 0 ? (byte)0 : payload[0];
    }

    private static uint NativeU32(LeagueEnetEvent @event, LegacyBlowfishCipher cipher, int offset)
    {
        var payload = NativePlaintext(@event, cipher);
        return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
    }

    private static bool TooltipContains(LeagueEnetEvent @event,
        LegacyBlowfishCipher cipher, uint heroNetId, byte selector,
        double first, double second, double third)
    {
        var payload = NativePlaintext(@event, cipher);
        if (payload.Length < 24 || payload[0] != 0x83) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5, 2));
        for (var index = 0; index < count; index++)
        {
            var offset = 7 + index * 17;
            if (offset + 17 > payload.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4)) != heroNetId ||
                payload[offset + 4] != selector)
                continue;
            return Math.Abs(BitConverter.ToSingle(payload, offset + 5) - first) < 1e-5 &&
                Math.Abs(BitConverter.ToSingle(payload, offset + 9) - second) < 1e-5 &&
                Math.Abs(BitConverter.ToSingle(payload, offset + 13) - third) < 1e-5;
        }
        return false;
    }

    private static JsonElement TooltipEntries(IEnumerable<PlaintextPacket> packets)
    {
        // One row set per publication, and a slot swap publishes twice: the shop's own layout change at once and the
        // converted copy's re-write of its rows on the next item poll. The last set is the settled one.
        var packet = packets.Where(candidate => Opcode(candidate) == 0x83).Last();
        var decoded = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
        TestAssert.Equal("ToolTipVars", decoded.Name, "tooltip packet decoder name");
        return JsonSerializer.SerializeToElement(decoded.Fields["entries"]);
    }

    private static PlaintextPacket FindEffect(
        IReadOnlyList<PlaintextPacket> packets, string effectName)
    {
        var hash = SpellPackets.BuffNameHash(effectName[..^5]);
        return packets.Single(candidate => ReadUInt32(candidate, 6) == hash);
    }

    private static void AssertTooltip(JsonElement entries, int selector,
        double first, double second, double third, string label)
    {
        var entry = entries.EnumerateArray().Single(value =>
            value.GetProperty("selector").GetInt32() == selector);
        TestAssert.Equal("spellbook", entry.GetProperty("selector_kind").GetString(),
            label + " selector kind");
        TestAssert.Equal(selector - 60, entry.GetProperty("slot").GetInt32(),
            label + " champion spellbook slot");
        TestAssert.Near(first, entry.GetProperty("f1").GetDouble(), 1e-5,
            label + " f1");
        TestAssert.Near(second, entry.GetProperty("f2").GetDouble(), 1e-5,
            label + " f2");
        TestAssert.Near(third, entry.GetProperty("f3").GetDouble(), 1e-5,
            label + " f3");
    }

    private static uint FxTarget(PlaintextPacket packet) => ReadUInt32(packet, 21);

    private static uint FxBinding(PlaintextPacket packet) => ReadUInt32(packet, 29);

    private static uint ReadUInt32(PlaintextPacket packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[offset..(offset + 4)]);
}
