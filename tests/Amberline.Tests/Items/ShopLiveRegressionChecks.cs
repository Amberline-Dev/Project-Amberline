using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-map1-purchase-rules")]
    public static void Map1RulesRejectShippedUnpurchasableItemsThroughProductionShop()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        hero.Inventory = Inventory.Empty();
        hero.Gold = 50_000.0;
        var itemCooldowns = hero.ItemCooldowns.ToArray();
        var inventory = hero.Inventory;
        var stats = (hero.MatchStatistics.GoldSpent,
            hero.MatchStatistics.ItemsPurchased,
            hero.MatchStatistics.ConsumablesPurchased);

        bool Shop(byte[] packet) => match.Runtime.Session.DispatchShop(
            new SessionInput(1, 1, match.Runtime.Cipher.Encrypt(packet), packet, true));
        byte[] Buy(int itemId)
        {
            var packet = new byte[9]; packet[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), checked((uint)itemId));
            return packet;
        }

        TestAssert.True(Shop([0x60, 0, 0, 0, 0]), "Map1 fountain opens shop");
        match.Clear();
        foreach (var itemId in new[] { 2047, 3178, 3180, 3184 })
        {
            TestAssert.False(Shop(Buy(itemId)), $"Map1 rejects unpurchasable item {itemId}");
            TestAssert.True(ReferenceEquals(inventory, hero.Inventory),
                $"rejected Map1 item {itemId} preserves inventory reference");
            TestAssert.Near(50_000.0, hero.Gold, 0.0,
                $"rejected Map1 item {itemId} preserves gold");
            TestAssert.Equal(stats, (hero.MatchStatistics.GoldSpent,
                hero.MatchStatistics.ItemsPurchased,
                hero.MatchStatistics.ConsumablesPurchased));
            TestAssert.Equal(itemCooldowns.Length, hero.ItemCooldowns.Count,
                $"rejected Map1 item {itemId} preserves cooldown slots");
            for (var slot = 0; slot < itemCooldowns.Length; slot++)
                TestAssert.Near(itemCooldowns[slot], hero.ItemCooldowns[slot], 0.0,
                    $"rejected Map1 item {itemId} preserves cooldown {slot}");
            TestAssert.Equal(0, match.Captures[0].Packets.Count,
                $"rejected Map1 item {itemId} emits no mutation packets");
        }

        TestAssert.True(Shop(Buy(1001)), "Map1 allows an ordinary purchasable item");
        TestAssert.True(Shop(Buy(1042)), "Map1 allows a recipe component");
        TestAssert.True(Shop(Buy(3006)), "Map1 allows a valid recipe purchase");
        TestAssert.Equal(3006, hero.Inventory[0]!.ItemId,
            "valid recipe replaces its components atomically");

        match.Runtime.Combat.Kill(hero);
        match.Runtime.Combat.ScheduleRespawn(hero);
        TestAssert.True(Shop(Buy(1011)), "dead Map1 hero can still purchase a valid item");
    }

    // Batch 183 reproductions (the sweep rows 5, 7, 9 and 8). See docs/maintenance/2026-10-24-batch-183-sweep-gameplay.md.
    [Check("items-live-potion-stack-keeps-running-cooldown")]
    public static void StackedPotionKeepsItsRunningCooldown()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        hero.Inventory = Inventory.Empty();
        hero.Gold = 50_000.0;
        hero.Health = hero.MaxHealth * 0.9;
        bool Shop(byte[] packet) => match.Runtime.Session.DispatchShop(
            new SessionInput(1, 1, match.Runtime.Cipher.Encrypt(packet), packet, true));
        byte[] Buy(int itemId)
        {
            var packet = new byte[9]; packet[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), checked((uint)itemId));
            return packet;
        }
        hero.Inventory = [new InventorySlot(2003, count: 2), null, null, null, null, null];
        TestAssert.True(match.TryCast(0, 4, hero), "one potion drunk");
        TestAssert.Equal(1, hero.Inventory[0]!.Count, "one potion left in the stack");
        var running = hero.ItemCooldowns[0];
        TestAssert.True(running > 0.5, $"the drink starts the potion cooldown ({running})");
        TestAssert.True(Shop([0x60, 0, 0, 0, 0]), "fountain opens shop");
        TestAssert.True(Shop(Buy(2003)), "a potion bought into the same slot stacks");
        TestAssert.Equal(2, hero.Inventory[0]!.Count, "the purchase stacked");
        TestAssert.Near(running, hero.ItemCooldowns[0], 1e-9, "the stacked slot keeps its own running cooldown");
        TestAssert.False(match.TryCast(0, 4, hero), "the stack cannot be drunk again while the cooldown runs");
    }

    [Check("items-live-dead-shopping")]
    public static void DeadShopping()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var hero = match.Hero(0);
            hero.Inventory = Inventory.Empty();
            hero.Gold = 5000;
            var buy = new byte[9]; buy[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 1011); // Giant's Belt
            bool Shop(byte[] packet) => match.Runtime.Session.DispatchShop(
                new SessionInput(1, 1, match.Runtime.Cipher.Encrypt(packet), packet, true));

            TestAssert.False(Shop(buy), "living hero cannot buy in mid lane");
            TestAssert.Near(5000, hero.Gold, 0, "rejection preserves gold");
            match.Runtime.Combat.Kill(hero);
            match.Runtime.Combat.ScheduleRespawn(hero);
            match.Runtime.HeroStats.Refresh(hero);
            var deadline = hero.RespawnAt;
            var maximum = hero.MaxHealth;
            TestAssert.True(Shop([0x60, 0, 0, 0, 0]), "dead hero can open shop");
            TestAssert.True(Shop(buy), "dead hero can buy away from fountain");
            TestAssert.Equal(1011, hero.Inventory[0]!.ItemId);
            TestAssert.True(hero.Gold < 5000 && hero.MaxHealth > maximum);
            TestAssert.False(hero.Alive);
            TestAssert.Near(0, hero.Health, 0, "purchase cannot restore corpse health");
            TestAssert.Near(deadline, hero.RespawnAt, 0, "shopping preserves death timer");
            match.Runtime.Delivery.Flush();
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x72),
                "dead owner receives the purchase packet");

            var afterBuy = hero.Gold;
            var sell = new byte[6]; sell[0] = 0x09; sell[5] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(1), hero.NetId);
            TestAssert.True(Shop(sell), "dead hero can sell");
            TestAssert.True(hero.Inventory[0] is null && hero.Gold > afterBuy);
            TestAssert.Near(0, hero.Health, 0);
            TestAssert.True(Shop(buy));
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(hero));
            TestAssert.Equal(1011, hero.Inventory[0]!.ItemId, "purchase survives respawn");
            TestAssert.Near(hero.MaxHealth, hero.Health, 0);
            hero.Position = new Position2(7000, 7000);
            TestAssert.False(Shop(buy), "respawning restores ordinary shop range restriction");
        }
    }
}
