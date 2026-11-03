using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("stats.live-gold-earned")]
    public static void EarnedGoldIncludesIncomeAndRewardsWithoutCountingResales()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime; var combat = runtime.Combat;
            var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
            var stats = hero.MatchStatistics;
            TestAssert.Equal(475.0, stats.GoldEarned);
            combat.AwardHeroProgress(hero, gold: 1000);
            TestAssert.Equal(1475.0, stats.GoldEarned);
            combat.Kill(hero); combat.ScheduleRespawn(hero);
            var buy = new byte[9]; buy[0] = 0x87;
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 1001);
            bool Shop(byte[] packet) => runtime.Session.DispatchShop(
                new SessionInput(1, 1, runtime.Cipher.Encrypt(packet), packet, true));
            TestAssert.True(Shop(buy));
            var afterPurchase = hero.Gold;
            var sell = new byte[6]; sell[0] = 0x09; sell[5] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(1), hero.NetId);
            TestAssert.True(Shop(sell));
            TestAssert.True(hero.Gold > afterPurchase && stats.GoldSpent > 0);
            TestAssert.Equal(1475.0, stats.GoldEarned, "spending and reselling do not create earned gold");
            TestAssert.True(combat.RespawnImmediately(hero));
            TestAssert.Equal(1475.0, stats.GoldEarned, "respawn does not award starting gold again");

            // Batch 161 (retail footage — the Xerath champion spotlight, October 2011 — over the Constants.var tick
            // comment): ambient income is continuous at 1.3 g/s from the 90-second gate
            // (ai_AmbientGoldAmount / ai_AmbientGoldInterval), so a step onto the gate earns only its own tail and
            // there is no 6.5 payment on the boundary any more.
            combat.CombatTick(90, regenerate: false);
            TestAssert.Near(1475.0 + 1.3 * (combat.CurrentTime - 90.0), stats.GoldEarned, 1e-8);
            hero.Inventory = [new InventorySlot(3096), new InventorySlot(3096), null, null, null, null];
            // Batch 149 step 5: a gold item's income is the window buff its copy opens, not the inventory row (no
            // shipped item record carries FlatGoldPer10Mod). Items/3096.luaobj's OnBuffActivate adds
            // PhilosophersStone and Spells/PhilosophersStone.luaobj's BuffOnUpdateStats is the whole income (one
            // BBIncStat IncFlatGoldPer10Mod of Delta 5); the second copy shares that window (its own OnBuffActivate
            // finds the buff and skips the BBSpellBuffAdd BUFF_REPLACE_EXISTING), which is what "one unique gp10
            // item" is now. The copy binds on the next spell update (BbItemModule.SyncInventory), so the inventory
            // set has to see one update before the window pays.
            runtime.Tick(0.01);
            var earnedBefore = stats.GoldEarned;
            combat.CombatTick(5, regenerate: false);
            TestAssert.Near(earnedBefore + 1.3 * 5.0 + 0.5 * 5.0, stats.GoldEarned, 1e-8,
                "ambient income and one unique gp10 item");
            var ambient = 1.3 * (combat.CurrentTime - 90.0);
            var income = stats.GoldEarned;
            var turret = runtime.World.Entities.Values.First(unit => unit.Kind == EntityKind.Turret && unit.Team == enemy.Team);
            TestAssert.True(combat.Kill(turret, hero.NetId));
            TestAssert.Near(income + 150.0, stats.GoldEarned, 1e-8);
            TestAssert.Near(475.0 + ambient + 150.0, ally.MatchStatistics.GoldEarned, 1e-8, "team turret reward plus starting and ambient gold");

            var pet = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                EntityKind.Pet, UnitType.Monster, team, enemy.Position) { OwnerNetId = hero.NetId, Health = 100, MaxHealth = 100 });
            var neutral = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                EntityKind.Neutral, UnitType.Monster, Team.Neutral, enemy.Position)
                { Health = 100, MaxHealth = 100, GoldReward = 22, GlobalGoldReward = 25 });
            TestAssert.True(combat.Kill(neutral, pet.NetId));
            TestAssert.Near(income + 150.0 + 22.0 + 25.0, stats.GoldEarned, 1e-8, "pet last-hit and global rewards belong to its owner");
            TestAssert.Near(475.0 + ambient + 150.0 + 25.0, ally.MatchStatistics.GoldEarned, 1e-8);
            TestAssert.True(combat.ApplyHit(ally.NetId, enemy.NetId, 1, DamageType.True));
            TestAssert.True(combat.ApplyHit(pet.NetId, enemy.NetId, enemy.Health + 1, DamageType.True));
            TestAssert.Near(income + 150.0 + 22.0 + 25.0 + 400.0, stats.GoldEarned, 1e-8, "first-blood reward");
            TestAssert.Near(475.0 + ambient + 150.0 + 25.0 + 400.0 * .5834, ally.MatchStatistics.GoldEarned, 1e-6, "assist reward");
            combat.AwardHeroProgress(hero, gold: -10);
            TestAssert.Near(income + 150.0 + 22.0 + 25.0 + 400.0, stats.GoldEarned, 1e-8, "a balance correction does not erase lifetime income");
        }
    }

    [Check("stats.live-gold-replicated-on-whole-gold-change")]
    public static void EveryWholeGoldChangeReachesTheOwnersClientInTheTickItMoves()
    {
        // The owner's HUD draws the whole gold (floor(mGold) into GoldCurrent_%d, no smoothing of its own), so a
        // new figure belongs to the client the moment that whole value moves — not on the one-second hero block.
        // The priming step below puts that block on the .9 of every second while the continuous income below crosses
        // its whole values between those seconds, so an update that waits for the block is a failure and this check
        // fails without on-change replication.
        // Batch 161 (retail footage — the Xerath champion spotlight, October 2011): ambient income is continuous at
        // 1.3 g/s from the 90-second gate, so over ten seconds of it the hero gains 13 gold and the HUD rises one
        // gold at a time, never in the 6-7 gold jumps the Constants.var tick cadence produced.
        foreach (var (label, goldItem) in new[] { ("ambient", false), ("gold-per-10", true) })
        {
            using var match = new Match("Annie", Team.Order, ally: true);
            var runtime = match.Runtime; var combat = runtime.Combat;
            var hero = match.Hero(0);
            if (goldItem)
            {
                // Items/3096.luaobj's window buff (PhilosophersStone, IncFlatGoldPer10Mod 5) is the income and pays
                // continuously; the copy binds on the next spell update.
                hero.Inventory = [new InventorySlot(3096), null, null, null, null, null];
                runtime.Tick(0.01);
            }
            var store = new ClientReplicationStore();
            // Everything the client has been given so far (the gp10 arm's item bind), then the priming step's
            // one-second block: its whole hero state — gold 475 and every other field.
            DeliverGold(store, hero.NetId, match.Captures[0].Packets);
            match.Clear();
            combat.CombatTick(89.9 - combat.CurrentTime, regenerate: false);
            runtime.Delivery.Flush();
            DeliverGold(store, hero.NetId, match.Captures[0].Packets);
            match.Clear();
            // Onto the gate itself: the measurement window below is exactly ten seconds of income.
            combat.CombatTick(0.1, regenerate: false);
            runtime.Delivery.Flush();
            DeliverGold(store, hero.NetId, match.Captures[0].Packets);
            match.Clear();

            var incomeFrom = hero.Gold;
            var changes = 0;
            var updates = 0;
            var clientShown = Entity.ShownGold(incomeFrom);
            for (var step = 0; step < 50; step++)
            {
                var shownBefore = Entity.ShownGold(hero.Gold);
                combat.CombatTick(0.2, regenerate: false);
                runtime.Delivery.Flush();
                var (gold, narrow) = DeliverGold(store, hero.NetId, match.Captures[0].Packets);
                match.Clear();
                var shownAfter = Entity.ShownGold(hero.Gold);
                if (gold is not null && Entity.ShownGold(gold.Value) != clientShown)
                {
                    // The HUD figure the client draws moved on the wire. It belongs to this tick and is exactly the
                    // whole gold the hero now has. (The one-second hero block may also carry the fractional gold
                    // between whole values; the HUD draws floor(mGold), so that is not an update.)
                    clientShown = Entity.ShownGold(gold.Value);
                    TestAssert.Equal(shownAfter, clientShown,
                        $"{label}: the client is given the whole gold the hero now has, in the tick it moves");
                    updates++;
                }
                if (shownAfter == shownBefore) continue;
                changes++;
                TestAssert.True(gold is not null,
                    $"{label}: the whole gold {shownAfter} reaches the client in the tick it moves");
                TestAssert.Near(shownAfter, Entity.ShownGold(gold!.Value), 1e-9,
                    $"{label}: the client is given the whole gold the hero now has");
                TestAssert.True(narrow,
                    $"{label}: a gold change is a gold delta, not the whole hero stat block");
                TestAssert.True(shownAfter - shownBefore <= 1.0,
                    $"{label}: income alone never moves the HUD by more than one gold at a time");
            }
            TestAssert.Equal(updates, changes, $"{label}: one gold update per whole-gold change");
            TestAssert.True(changes >= 12,
                $"{label}: ten seconds of income moves the whole gold {changes} times");
            if (!goldItem)
                TestAssert.Near(13.0, hero.Gold - incomeFrom, 0.01,
                    "ten seconds of ambient income after the 90-second gate is 13 gold (6.5 / 5 s prorated)");
        }
    }

    [Check("stats.live-gold-replicated-for-every-source")]
    public static void EverySourceOfGoldReachesTheOwnersClientInTheTickItLands()
    {
        using var match = new Match("Annie", Team.Order, ally: true);
        var runtime = match.Runtime; var combat = runtime.Combat;
        var hero = match.Hero(0); var enemy = match.Hero(1);
        var store = new ClientReplicationStore();
        DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        match.Clear();
        combat.CombatTick(0.1, regenerate: false);
        runtime.Delivery.Flush();
        DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        match.Clear();

        // A kill's bounty: AwardHeroProgress publishes gold and experience in one block.
        var before = Entity.ShownGold(hero.Gold);
        TestAssert.True(combat.ApplyHit(hero.NetId, enemy.NetId, enemy.Health + 1, DamageType.True));
        runtime.Delivery.Flush();
        var (gold, narrow) = DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        match.Clear();
        TestAssert.True(Entity.ShownGold(hero.Gold) != before, "the kill pays a bounty");
        TestAssert.Near(Entity.ShownGold(hero.Gold), Entity.ShownGold(gold!.Value), 1e-9,
            "the bounty reaches the killer in the tick it is earned");
        TestAssert.True(narrow, "a bounty is a gold delta");

        // A turret's team reward: GrantGold income that used to wait for the one-second block.
        var turret = runtime.World.Entities.Values.First(unit => unit.Kind == EntityKind.Turret && unit.Team == enemy.Team);
        before = Entity.ShownGold(hero.Gold);
        TestAssert.True(combat.Kill(turret, hero.NetId));
        runtime.Delivery.Flush();
        (gold, narrow) = DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        match.Clear();
        TestAssert.True(Entity.ShownGold(hero.Gold) != before, "the turret pays its team reward");
        TestAssert.Near(Entity.ShownGold(hero.Gold), Entity.ShownGold(gold!.Value), 1e-9,
            "the team reward reaches its owner in the tick it is granted");
        TestAssert.True(narrow, "a team reward is a gold delta");

        // A sale: ShopService commits the new gold and its hero block carries it.
        combat.AwardHeroProgress(hero, gold: 1000);
        runtime.Delivery.Flush();
        DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        match.Clear();
        TestAssert.True(combat.Kill(hero), "a dead hero can shop");
        combat.ScheduleRespawn(hero);
        var buy = new byte[9]; buy[0] = 0x87;
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 1001);
        bool Shop(byte[] packet) => runtime.Session.DispatchShop(
            new SessionInput(1, 1, runtime.Cipher.Encrypt(packet), packet, true));
        before = Entity.ShownGold(hero.Gold);
        TestAssert.True(Shop(buy));
        runtime.Delivery.Flush();
        (gold, _) = DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        match.Clear();
        TestAssert.True(Entity.ShownGold(hero.Gold) != before, "the purchase spends gold");
        TestAssert.Near(Entity.ShownGold(hero.Gold), Entity.ShownGold(gold!.Value), 1e-9,
            "the purchase reaches the buyer in the tick it is made");

        var sell = new byte[6]; sell[0] = 0x09; sell[5] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(1), hero.NetId);
        before = Entity.ShownGold(hero.Gold);
        TestAssert.True(Shop(sell));
        runtime.Delivery.Flush();
        (gold, _) = DeliverGold(store, hero.NetId, match.Captures[0].Packets);
        TestAssert.True(Entity.ShownGold(hero.Gold) != before, "the sale refunds gold");
        TestAssert.Near(Entity.ShownGold(hero.Gold), Entity.ShownGold(gold!.Value), 1e-9,
            "the sale reaches the seller in the tick it is made");
    }

    /// <summary>
    /// One tick's gold traffic seen twice: what the server built for it (the packet-budget claim, before the store
    /// reduces anything) and what one client's replication store really keeps out of the batch (the value its HUD
    /// draws). Returns the gold that reached that client and whether the change went out as a gold delta rather
    /// than the whole hero stat block.
    /// </summary>
    private static (double? Gold, bool Narrow) DeliverGold(
        ClientReplicationStore store, uint ownerNetId, IEnumerable<PlaintextPacket> packets)
    {
        double? gold = null;
        var narrow = false;
        foreach (var packet in packets)
        {
            if (IsGoldDelta(packet, ownerNetId)) narrow = true;
            if (store.Process(packet, ownerNetId) is not { } sent) continue;
            var bytes = sent.Plaintext.Span;
            if (bytes.Length < 15 || bytes[0] != 0xCC || bytes[9] != 1 || (bytes[10] & 0xE0) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]) != ownerNetId)
                continue;
            var offset = 15;
            float? value = null;
            for (var registry = 0; registry < 5; registry++)
            {
                if ((bytes[10] & (1 << registry)) == 0) continue;
                var mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                offset += 4;
                for (var bit = 0; bit < 32; bit++)
                {
                    if ((mask & (1u << bit)) == 0) continue;
                    if (registry == 0 && bit == 1)
                        value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
                    offset += 4;
                }
            }
            if (value is not null) gold = value;
        }
        return (gold, narrow);
    }

    /// <summary>Gold published on its own — alone, or with experience: CLIENT_ONLY only, gold bit set, delta-sized.</summary>
    private static bool IsGoldDelta(PlaintextPacket packet, uint ownerNetId)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length is >= 23 and <= 27 && bytes[0] == 0xCC && bytes[9] == 1 && bytes[10] == 0x01 &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]) == ownerNetId &&
               (BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]) & 0x02) != 0;
    }

    [Check("stats.live-life-and-connection-time")]
    public static void LifeAndConnectionIntervalsSurviveRespawnReconnectAndMatchEnd()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime; var combat = runtime.Combat; var session = runtime.Session;
            var hero = match.Hero(0); var enemy = match.Hero(1); var stats = hero.MatchStatistics;
            combat.CombatTick(3, regenerate: false);
            TestAssert.True(combat.ApplyHit(enemy.NetId, hero.NetId, hero.Health + 1, DamageType.True));
            TestAssert.False(combat.Kill(hero, enemy.NetId));
            combat.CombatTick(2, regenerate: false);
            var current = stats.Snapshot(combat.CurrentTime);
            TestAssert.Equal((5.0, 2.0, 3.0, 0.0), (current.TimePlayed,
                current.TotalTimeSpentDead, current.LongestTimeSpentLiving, current.TimeSpentDisconnected));
            TestAssert.True(current.LastDisconnectedAt is null);
            TestAssert.True(combat.RespawnImmediately(hero));
            TestAssert.False(combat.RespawnImmediately(hero));
            combat.CombatTick(4, regenerate: false);
            TestAssert.True(session.Disconnect(1));
            TestAssert.False(session.Disconnect(1));
            combat.CombatTick(2, regenerate: false);
            TestAssert.True(session.AuthenticateReconnect(10, session.BuildKeyCheck(999)) is null);
            current = stats.Snapshot(combat.CurrentTime);
            TestAssert.Equal((2.0, 6.0, 2.0), (current.TotalTimeSpentDead,
                current.LongestTimeSpentLiving, current.TimeSpentDisconnected));
            TestAssert.Equal(9.0, current.LastDisconnectedAt!.Value);
            TestAssert.True(session.AuthenticateReconnect(10, session.BuildKeyCheck(1)) is not null);
            TestAssert.True(ReferenceEquals(stats, session.Participants[0].Hero!.MatchStatistics));
            TestAssert.Equal(475.0, stats.GoldEarned);
            combat.CombatTick(3, regenerate: false);
            TestAssert.Equal(2.0, stats.Snapshot(combat.CurrentTime).TimeSpentDisconnected,
                "accepted authentication ends the disconnected interval even while loading");
            TestAssert.True(session.Disconnect(10));
            TestAssert.True(combat.ApplyHit(enemy.NetId, hero.NetId, hero.Health + 1, DamageType.True));
            combat.CombatTick(2, regenerate: false);
            TestAssert.True(combat.Surrender(team));
            var result = session.Result!; var frozen = result.Players[0].Statistics;
            TestAssert.Equal((16.0, 4.0, 9.0, 4.0), (frozen.TimePlayed,
                frozen.TotalTimeSpentDead, frozen.LongestTimeSpentLiving, frozen.TimeSpentDisconnected));
            TestAssert.Equal(14.0, frozen.LastDisconnectedAt!.Value);
            TestAssert.Equal(result.DurationSeconds, frozen.TimePlayed);
            var serialized = JsonSerializer.Serialize(result);
            combat.CombatTick(20, regenerate: false);
            TestAssert.True(session.AuthenticateReconnect(20, session.BuildKeyCheck(1)) is not null);
            TestAssert.True(session.Disconnect(20));
            TestAssert.Equal(serialized, JsonSerializer.Serialize(result), "final records freeze open intervals");
        }
    }

    [Check("stats.live-match-clock-origin")]
    public static void MatchDurationsExcludeTimeBeforeTheMatchStarts()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0, roster: [new(1, 0, "Clock", "Annie", Team.Order)]);
        runtime.World.Clock.SetVirtual(0);
        runtime.Combat.CombatTick(5, regenerate: false);
        TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null);
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1));
        TestAssert.True(runtime.Session.Start());
        TestAssert.False(runtime.Session.Start());
        runtime.Combat.CombatTick(3, regenerate: false);
        TestAssert.True(runtime.Session.Disconnect(1));
        runtime.Combat.CombatTick(2, regenerate: false);
        TestAssert.True(runtime.Combat.Surrender(Team.Order));
        var result = runtime.Session.Result!; var stats = result.Players[0].Statistics;
        TestAssert.Equal((5.0, 5.0, 2.0, 3.0), (result.DurationSeconds, stats.LongestTimeSpentLiving,
            stats.TimeSpentDisconnected, stats.LastDisconnectedAt!.Value));
        TestAssert.Equal(5.0, stats.TimePlayed);
    }

    [Check("combat.friendly-structure-losses")]
    public static void EveryTeammateRecordsStructureLossesOncePerDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var surrender in new[] { false, true })
        {
            using var match = new Match("Annie", team, ally: true);
            var runtime = match.Runtime; var combat = runtime.Combat;
            var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
            TestAssert.True(runtime.Session.Disconnect(3));
            TestAssert.True(combat.ApplyHit(enemy.NetId, ally.NetId, ally.Health + 1, DamageType.True));
            var turret = runtime.World.Entities.Values.First(unit => unit.Kind == EntityKind.Turret && unit.Team == team);
            var inhibitor = runtime.World.Entities.Values.First(unit => unit.Kind == EntityKind.Inhibitor && unit.Team == team);
            TestAssert.True(combat.Kill(turret, enemy.NetId));
            TestAssert.False(combat.Kill(turret, enemy.NetId));
            TestAssert.True(combat.Kill(inhibitor, enemy.NetId));
            TestAssert.False(combat.Kill(inhibitor, enemy.NetId));
            TestAssert.Equal((1, 1), (ally.MatchStatistics.FriendlyTurretsLost, ally.MatchStatistics.FriendlyInhibitorsLost),
                "a dead, disconnected teammate still records the loss");
            // retail sweep B4, 27 Sept 2026: inhibitor recovery is five minutes.
            combat.CombatTick(300, regenerate: false);
            combat.ProcessRespawns();
            TestAssert.True(inhibitor.Alive);
            TestAssert.True(combat.Kill(inhibitor, enemy.NetId));
            var nexus = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == team);
            TestAssert.True(surrender ? combat.Surrender(team) : combat.Kill(nexus, enemy.NetId));
            TestAssert.False(combat.Kill(nexus, enemy.NetId));
            var players = runtime.Session.Result!.Players;
            foreach (var player in new[] { players[0], players[2] })
                TestAssert.Equal((1, 2, 1), (player.Statistics.FriendlyTurretsLost,
                    player.Statistics.FriendlyInhibitorsLost, player.Statistics.FriendlyNexusesLost));
            TestAssert.Equal((0, 0, 0), (players[1].Statistics.FriendlyTurretsLost,
                players[1].Statistics.FriendlyInhibitorsLost, players[1].Statistics.FriendlyNexusesLost));
            TestAssert.Equal(surrender ? 0 : 1, players[1].NexusesKilled);
        }
    }
}
