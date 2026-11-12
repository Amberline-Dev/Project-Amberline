using System.Reflection;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

public static class AirBoostChecks
{
    [Check("server.air-boost-catalog-and-fulfillment-contract")]
    public static void CatalogAndFulfillmentExposeAllStockBoostShapes()
    {
        var root = TemporaryDirectory("league-air-boost-contract");
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var account = accounts.RegisterPublic("boostuser", "testing-password", "Boost User", maxed: false);
            var products = accounts.StoreCatalog.Items.Where(item => item.Kind == "BOOST").ToArray();
            TestAssert.Equal(4, products.Length);
            if (!AirStoreSalesChecks.Without2011Store())
            {
                TestAssert.Equal(290, accounts.StoreCatalog.Get(PlatformStoreCatalog.IpTimedBoostId).RpPrice);
                TestAssert.Equal(150, accounts.StoreCatalog.Get(PlatformStoreCatalog.XpTimedBoostId).RpPrice);
                // The retail 10-win packs of October 2011 (the League wiki's Boost page): IP 670 RP, XP 350 RP.
                TestAssert.Equal(670, accounts.StoreCatalog.Get(PlatformStoreCatalog.IpWinBoostId).RpPrice);
                TestAssert.Equal(350, accounts.StoreCatalog.Get(PlatformStoreCatalog.XpWinBoostId).RpPrice);
            }
            TestAssert.True(products.All(item => item.IpPrice is null && item.RpPrice is not null));

            var notification = Fulfillment(accounts.StoreCatalog.Get(PlatformStoreCatalog.IpTimedBoostId),
                account, PlatformEconomy.For(account));
            TestAssert.Equal("com.riotgames.platform.messaging.StoreFulfillmentNotification", notification.TypeName);
            TestAssert.Equal("BOOST", notification["inventoryType"]);
            var data = (AsObject)notification["data"];
            TestAssert.Equal("com.riotgames.platform.summoner.boost.SummonerActiveBoostsDTO", data.TypeName);
            TestAssert.Equal((double)account.SummonerId, data["summonerId"]);
            TestAssert.True(data.ContainsKey("_ipBoostEndDate"));
            TestAssert.True(data.ContainsKey("_xpBoostPerWinCount"));
            TestAssert.Equal(data["_ipBoostEndDate"], data["ipBoostEndDate"]);
            TestAssert.Equal(data["_xpBoostPerWinCount"], data["xpBoostPerWinCount"]);

            var wire = RoundTrip(notification);
            TestAssert.Equal(notification.TypeName, wire.TypeName);
            TestAssert.Equal("BOOST", wire["inventoryType"]);
            var wireData = (AsObject)wire["data"];
            TestAssert.Equal(data.TypeName, wireData.TypeName);
            TestAssert.Equal(data["ipBoostEndDate"], wireData["ipBoostEndDate"]);
            TestAssert.Equal(data["xpBoostPerWinCount"], wireData["xpBoostPerWinCount"]);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-boost-persistence-and-repeated-purchase")]
    public static void PurchasesExtendTimedStateAndReplayWithoutDoubleDebit()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = TemporaryDirectory("league-air-boost-purchase");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            long accountId;
            long firstEnd;
            Guid replayRequest;
            using (var accounts = new PlatformAccounts(root, root, clock))
            {
                var account = accounts.RegisterPublic("boostbuy", "testing-password", "Boost Buy", maxed: false);
                accountId = account.Id;
                accounts.GrantCurrency(account.Id, 0, 2_000, Guid.NewGuid());
                replayRequest = Guid.NewGuid();
                accounts.Purchase(account.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", replayRequest);
                firstEnd = PlatformEconomy.For(accounts.Get(account.Id)).IpBoostEndDate;
                accounts.Purchase(account.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", Guid.NewGuid());
                accounts.Purchase(account.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid());
                accounts.Purchase(account.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid());
                var state = PlatformEconomy.For(accounts.Get(account.Id));
                TestAssert.Equal(firstEnd + 86_400_000L, state.IpBoostEndDate);
                TestAssert.Equal(80L, state.Rp);   // 2000 - 290 - 290 - 670 - 670
                TestAssert.Equal(20, state.IpBoostPerWinCount);
                TestAssert.Equal(accounts.Get(account.Id).Economy!.Purchases!.Length, 4);
                accounts.Purchase(account.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", replayRequest);
                TestAssert.Equal(80L, PlatformEconomy.For(accounts.Get(account.Id)).Rp,
                    "replaying a request ID must not debit or extend again");
            }

            using var reopened = new PlatformAccounts(root, root, clock);
            var reopenedState = PlatformEconomy.For(reopened.Get(accountId));
            TestAssert.Equal(firstEnd + 86_400_000L, reopenedState.IpBoostEndDate);
            TestAssert.Equal(4, reopened.Get(accountId).Economy!.Purchases!.Length);
            clock.Current = clock.Current.AddDays(2);
            TestAssert.Equal(string.Empty,
                AirDtos.ActiveBoosts(reopened.Get(accountId), clock)["displayIpEndDate"]);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-boost-reward-components-and-consumption")]
    public static void EligibleVictoryAddsIndependentBoostComponentsAndConsumesOneWin()
    {
        var root = TemporaryDirectory("league-air-boost-reward");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var accounts = new PlatformAccounts(root, root, clock);
            var account = accounts.RegisterPublic("boostreward", "testing-password", "Boost Reward", maxed: false);
            accounts.GrantCurrency(account.Id, 0, 2_000, Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.XpTimedBoostId, "RP", Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.XpWinBoostId, "RP", Guid.NewGuid());

            var result = Result(accounts.Get(account.Id), "Order", wasAfk: false);
            var receipt = new PlatformResult(1, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(result, MatchRoster.JsonOptions), QueueId: 2);
            TestAssert.True(accounts.RecordResult(receipt));
            var stored = accounts.FindResult(receipt.HostGameId)!.Rewards![account.Id];
            TestAssert.Equal(21L, stored.BaseIpEarned);
            TestAssert.Equal(21L, stored.TimedIpEarned);
            TestAssert.Equal(150L, stored.WinIpEarned);
            // Batch 549: the account's first matchmade win also claims First Win of the Day, which no boost scales.
            TestAssert.Equal(PlatformEconomy.FirstWinBonusIp, stored.FirstWinIpEarned);
            TestAssert.Equal(stored.BaseIpEarned + stored.FirstWinIpEarned + stored.TimedIpEarned + stored.WinIpEarned, stored.IpEarned);
            TestAssert.Equal(10, stored.BaseXpEarned);
            TestAssert.Equal(10, stored.TimedXpEarned);
            TestAssert.Equal(200, stored.WinXpEarned);
            TestAssert.Equal(stored.BaseXpEarned + stored.TimedXpEarned + stored.WinXpEarned, stored.XpEarned);

            var state = PlatformEconomy.For(accounts.Get(account.Id));
            TestAssert.Equal(9, state.IpBoostPerWinCount);
            TestAssert.Equal(9, state.XpBoostPerWinCount);
            var member = new PlatformGames.Member(accounts.Get(account.Id), Team.Order);
            var lobby = new PlatformGames.Lobby(1, "Boost result", "", account.Id, 1)
            {
                QueueId = 2,
                Members = [member],
                Result = result,
            };
            var eog = AirGameDtos.Results(lobby, member, stored, accounts.EconomyRules);
            TestAssert.Equal(21d, eog["basePoints"]);
            TestAssert.Equal(171d, eog["boostIpEarned"]);
            TestAssert.Equal(210d, eog["boostXpEarned"]);
            TestAssert.False(accounts.RecordResult(receipt with { Result = receipt.Result.Clone() }));
            TestAssert.Equal(9, PlatformEconomy.For(accounts.Get(account.Id)).IpBoostPerWinCount);

            clock.Current = clock.Current.AddDays(2);
            var expired = Result(accounts.Get(account.Id), "Order", wasAfk: false);
            var expiredReceipt = new PlatformResult(2, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(expired, MatchRoster.JsonOptions), QueueId: 2);
            TestAssert.True(accounts.RecordResult(expiredReceipt));
            var expiredReward = accounts.FindResult(expiredReceipt.HostGameId)!.Rewards![account.Id];
            TestAssert.Equal(0L, expiredReward.TimedIpEarned);
            TestAssert.Equal(0, expiredReward.TimedXpEarned);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-boost-loss-afk-cap-and-level30-policy")]
    public static void LossDoesNotConsumeWinBoostsAndAfkOrLevel30GetsNoXpBoost()
    {
        var root = TemporaryDirectory("league-air-boost-policy");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var accounts = new PlatformAccounts(root, root, clock);
            var account = accounts.RegisterPublic("boostpolicy", "testing-password", "Boost Policy", maxed: false);
            accounts.GrantCurrency(account.Id, 0, 2_000, Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.XpWinBoostId, "RP", Guid.NewGuid());
            var loss = Result(accounts.Get(account.Id), "Order", wasAfk: false, winner: "Chaos");
            var lossReceipt = new PlatformResult(1, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(loss, MatchRoster.JsonOptions), QueueId: 2);
            TestAssert.True(accounts.RecordResult(lossReceipt));
            var lossReward = accounts.FindResult(lossReceipt.HostGameId)!.Rewards![account.Id];
            TestAssert.Equal(0L, lossReward.WinIpEarned);
            TestAssert.Equal(0, lossReward.WinXpEarned);
            TestAssert.Equal(10, PlatformEconomy.For(accounts.Get(account.Id)).IpBoostPerWinCount);
            TestAssert.Equal(10, PlatformEconomy.For(accounts.Get(account.Id)).XpBoostPerWinCount);

            var afk = Result(accounts.Get(account.Id), "Order", wasAfk: true);
            var afkReceipt = new PlatformResult(2, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(afk, MatchRoster.JsonOptions), QueueId: 2);
            TestAssert.True(accounts.RecordResult(afkReceipt));
            TestAssert.True(accounts.FindResult(afkReceipt.HostGameId)!.Rewards is null);
            TestAssert.Equal(10, PlatformEconomy.For(accounts.Get(account.Id)).IpBoostPerWinCount);
            TestAssert.Equal(10, PlatformEconomy.For(accounts.Get(account.Id)).XpBoostPerWinCount);

            accounts.EconomyRules.CustomGameRewards = false;
            var custom = Result(accounts.Get(account.Id), "Order", wasAfk: false);
            var customReceipt = new PlatformResult(3, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(custom, MatchRoster.JsonOptions), QueueId: 1);
            TestAssert.True(accounts.RecordResult(customReceipt));
            TestAssert.True(accounts.FindResult(customReceipt.HostGameId)!.Rewards is null);
            TestAssert.Equal(10, PlatformEconomy.For(accounts.Get(account.Id)).IpBoostPerWinCount);

            var tooShort = Result(accounts.Get(account.Id), "Order", wasAfk: false, durationSeconds: 30);
            var tooShortReceipt = new PlatformResult(4, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(tooShort, MatchRoster.JsonOptions), QueueId: 2);
            TestAssert.True(accounts.RecordResult(tooShortReceipt));
            TestAssert.True(accounts.FindResult(tooShortReceipt.HostGameId)!.Rewards is null);
            TestAssert.Equal(10, PlatformEconomy.For(accounts.Get(account.Id)).XpBoostPerWinCount);

            var maxed = accounts.RegisterPublic("boostmax", "testing-password", "Boost Max", maxed: true);
            accounts.GrantCurrency(maxed.Id, 0, 1_000, Guid.NewGuid());
            TestAssert.Throws<InvalidOperationException>(() =>
                accounts.Purchase(maxed.Id, PlatformStoreCatalog.XpTimedBoostId, "RP", Guid.NewGuid()));
            TestAssert.Throws<InvalidOperationException>(() =>
                accounts.Purchase(maxed.Id, PlatformStoreCatalog.XpWinBoostId, "RP", Guid.NewGuid()));
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// Batch 549: First Win of the Day as 1.0.0.126 had it. A matchmade win pays a flat 150 IP no boost changes, then nothing for
    /// 22 hours; a loss, a custom game and a leaver never claim it. The end-of-game DTO and the profile carry it.
    /// </summary>
    [Check("server.air-first-win-of-the-day")]
    public static void FirstWinOfTheDayIsAMatchmadeWinEvery22Hours()
    {
        var root = TemporaryDirectory("league-air-first-win");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var accounts = new PlatformAccounts(root, root, clock);
            var account = accounts.RegisterPublic("firstwin", "testing-password", "First Win", maxed: false);
            var gameId = 0;
            PlatformReward? Play(int queue, string winner = "Order", bool afk = false)
            {
                var receipt = new PlatformResult(++gameId, Guid.NewGuid(), [account.Id], JsonSerializer.SerializeToElement(
                    Result(accounts.Get(account.Id), "Order", afk, winner), MatchRoster.JsonOptions), QueueId: queue);
                TestAssert.True(accounts.RecordResult(receipt));
                return accounts.FindResult(receipt.HostGameId)!.Rewards?.GetValueOrDefault(account.Id);
            }

            TestAssert.Equal(0L, Play(2, winner: "Chaos")!.FirstWinIpEarned, "a loss does not claim it");
            TestAssert.Equal(0L, Play(1)!.FirstWinIpEarned, "a custom game is not matchmade");
            TestAssert.True(Play(2, afk: true) is null, "a leaver earns nothing");
            accounts.GrantCurrency(account.Id, 0, 2_000, Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", Guid.NewGuid());
            var first = Play(2)!;
            TestAssert.Equal(150L, first.FirstWinIpEarned, "the first matchmade win pays 150 IP, boost or not");
            TestAssert.Equal(first.BaseIpEarned + first.FirstWinIpEarned + first.TimedIpEarned + first.WinIpEarned, first.IpEarned);
            TestAssert.Equal(first.BaseIpEarned, first.TimedIpEarned, "the timed boost doubles the base only");

            var member = new PlatformGames.Member(accounts.Get(account.Id), Team.Order);
            var lobby = new PlatformGames.Lobby(9, "Normal Game", "", account.Id, 1)
            { QueueId = 2, Members = [member], Result = Result(member.Account, "Order", false) };
            var eog = AirGameDtos.Results(lobby, member, first, accounts.EconomyRules, now: clock.Current);
            TestAssert.Equal(150d, eog["firstWinBonus"]);
            TestAssert.Equal(22d * 3_600_000, eog["timeUntilNextFirstWinBonus"]);
            TestAssert.Equal(clock.Current.UtcDateTime,
                AirProfileDtos.LifetimeStats(member.Account, accounts.ResultsFor(account.Id))["previousFirstWinOfDay"]);

            clock.Current = clock.Current.AddHours(21);
            TestAssert.Equal(0L, Play(2)!.FirstWinIpEarned, "unavailable for 22 hours after the claim");
            clock.Current = clock.Current.AddHours(1);
            TestAssert.Equal(150L, Play(2)!.FirstWinIpEarned, "available again 22 hours after the claim");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-boost-balance-cap-components")]
    public static void BalanceCapClipsReceiptComponentsWithoutChangingTheirSum()
    {
        var root = TemporaryDirectory("league-air-boost-cap");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var accounts = new PlatformAccounts(root, root, clock);
            var account = accounts.RegisterPublic("boostcap", "testing-password", "Boost Cap", maxed: false);
            accounts.GrantCurrency(account.Id, PlatformEconomy.MaxBalance - 5, 2_000, Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.IpTimedBoostId, "RP", Guid.NewGuid());
            accounts.Purchase(account.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid());
            var result = Result(accounts.Get(account.Id), "Order", wasAfk: false);
            var receipt = new PlatformResult(1, Guid.NewGuid(), [account.Id],
                JsonSerializer.SerializeToElement(result, MatchRoster.JsonOptions), QueueId: 2);
            TestAssert.True(accounts.RecordResult(receipt));
            var reward = accounts.FindResult(receipt.HostGameId)!.Rewards![account.Id];
            TestAssert.Equal(5L, reward.IpEarned);
            TestAssert.Equal(reward.BaseIpEarned + reward.TimedIpEarned + reward.WinIpEarned, reward.IpEarned);
            TestAssert.Equal(9, PlatformEconomy.For(accounts.Get(account.Id)).IpBoostPerWinCount);
        }
        finally { DeleteDirectory(root); }
    }

    private static AsObject Fulfillment(PlatformStoreItem item, PlatformAccount account, PlatformEconomyState state)
    {
        var method = typeof(PlatformStore).GetMethod("Fulfillment", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(PlatformStore), "Fulfillment");
        return (AsObject)(method.Invoke(null, [item, account, state, 1, false]) ?? throw new InvalidOperationException("Fulfillment returned null."));
    }

    private static MatchResult Result(PlatformAccount account, string team, bool wasAfk,
        string winner = "Order", double durationSeconds = 90) => new(8, "Version 1.0.0.126", 1, "CLASSIC", winner, durationSeconds,
        [Player(account, team, wasAfk)], false);

    private static AsObject RoundTrip(AsObject value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(value);
        using var reader = new AmfReader(new MemoryStream(buffer.ToArray()), new SerializationContext());
        return (AsObject)reader.ReadAmf3Item();
    }

    private static PlayerMatchResult Player(PlatformAccount account, string team, bool wasAfk) => new PlayerMatchResult(
        PlayerId: (ulong)account.SummonerId, NetId: (uint)account.Id, Name: account.Summoner,
        Champion: "Annie", Team: team, SkinId: 0, Connected: !wasAfk, Level: 1,
        Kills: 0, Deaths: 0, Assists: 0, KillingSprees: 0, LargestKillingSpree: 0,
        TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0, LaneMinionsKilled: 0,
        OtherMinionsKilled: 0, NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0,
        Statistics: new HeroMatchStatistics().Snapshot(0),
        SummonerSpells: [account.Loadout.SummonerSpell1, account.Loadout.SummonerSpell2], SpellRanks: [0, 0, 0, 0],
        Inventory: []) with { WasAfk = wasAfk };

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
