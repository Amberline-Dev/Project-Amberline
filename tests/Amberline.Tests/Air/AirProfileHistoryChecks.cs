using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

public static class AirProfileHistoryChecks
{
    [Check("server.air-profile-results-projection")]
    public static void ProfileStatsAndTopChampionsUseRecordedNormalResults()
    {
        var account = new PlatformAccount(1, 1_000_001, "profile", "Profile", "", "",
            new MatchPlayer("Profile", "Annie", Team.Order));
        var receipts = new[]
        {
            Receipt(1, account, "Annie", "Order", "Order", 2, 11),
            Receipt(2, account, "Orianna", "Chaos", "Order", 2, 12),
            Receipt(3, account, "Vayne", "Order", "Order", 0, 13)
        };

        var summaries = AirProfileDtos.PlayerStatSummaries(account, receipts);
        var unranked = Summary(summaries, "Unranked");
        TestAssert.Equal(1, (int)unranked["wins"]);
        TestAssert.Equal(1, (int)unranked["losses"]);
        TestAssert.Equal(0, (int)unranked["leaves"]);
        TestAssert.Equal(0, (int)unranked["rating"],
            "unranked rating stays empty without an authoritative rating source");
        TestAssert.Equal(0, (int)unranked["maxRating"],
            "unranked max rating stays empty without an authoritative rating source");
        TestAssert.Equal(0, (int)Summary(summaries, "RankedSolo5x5")["rating"],
            "ranked rating stays empty without ranked receipts");
        TestAssert.Equal(0, ((ArrayCollection)((AsObject)Summary(summaries, "RankedSolo5x5")["aggregatedStats"])["stats"]).Count,
            "ranked aggregate stays empty without ranked receipts");
        TestAssert.Equal(2d, StatValue((AsObject)unranked["aggregatedStats"], "TOTAL_SESSIONS_PLAYED"));

        var top = AirProfileDtos.TopPlayedChampions(account, "CLASSIC", receipts);
        TestAssert.Equal(2, top.Count, "custom receipts are history-only");
        TestAssert.Equal(1d, ((AsObject)top[0])["championId"]);
        TestAssert.Equal(1d, ((AsObject)top[0])["totalGamesPlayed"]);
        TestAssert.Equal(61d, ((AsObject)top[1])["championId"]);
    }

    [Check("server.air-profile-aggregate-contract")]
    public static void AggregatedStatsExposeGlobalAndChampionValues()
    {
        var account = new PlatformAccount(1, 1_000_001, "profile", "Profile", "", "",
            new MatchPlayer("Profile", "Annie", Team.Order));
        var receipt = Receipt(1, account, "Annie", "Order", "Order", 2, 11,
            new HeroMatchStatisticsSnapshot(
                TotalDamageDealt: 100.6, PhysicalDamageDealt: 20.5, MagicDamageDealt: 80.1,
                TotalDamageTaken: 40.4, PhysicalDamageTaken: 10.2, MagicDamageTaken: 30.2,
                LargestCriticalStrike: 12.5, TotalHeal: 3, TotalUnitsHealed: 1,
                GoldSpent: 45.6, ItemsPurchased: 1, ConsumablesPurchased: 0,
                SpellCasts: [], SummonerCasts: [], LargestMultiKill: 2, DoubleKills: 1,
                TripleKills: 0, QuadraKills: 0, PentaKills: 0, UnrealKills: 0,
                GoldEarned: 123.4, FriendlyTurretsLost: 0, FriendlyInhibitorsLost: 0,
                FriendlyNexusesLost: 0, TimePlayed: 90.6, TotalTimeSpentDead: 2.6,
                LongestTimeSpentLiving: 88, TimeSpentDisconnected: 0, LastDisconnectedAt: null));

        var aggregate = AirProfileDtos.AggregatedStats(account, "CLASSIC", [receipt]);
        TestAssert.Equal("com.riotgames.platform.statistics.AggregatedStats", aggregate.TypeName);
        var stats = (ArrayCollection)aggregate["lifetimeStatistics"];
        var global = stats.Cast<AsObject>().Where(s => (double)s["championId"] == 0).ToArray();
        var annie = stats.Cast<AsObject>().Where(s => (double)s["championId"] == 1).ToArray();
        TestAssert.True(global.Length > 0 && annie.Length > 0);
        TestAssert.Equal(1d, StatValue(global, "TOTAL_SESSIONS_PLAYED"));
        TestAssert.Equal(101d, StatValue(annie, "TOTAL_DAMAGE_DEALT"));
        TestAssert.Equal(123d, StatValue(annie, "TOTAL_GOLD_EARNED"));
        foreach (var stat in stats.Cast<AsObject>())
            TestAssert.True(double.IsFinite((double)stat["value"]));

        TestAssert.Equal(0, ((ArrayCollection)AirProfileDtos.AggregatedStats(account, "CLASSIC", [receipt], "ONE")["lifetimeStatistics"]).Count,
            "archived Season One must not receive current testing results");
        TestAssert.Equal(0, ((ArrayCollection)AirProfileDtos.AggregatedStats(account, "ODIN", [receipt])["lifetimeStatistics"]).Count,
            "Dominion must not receive Classic statistics");
        TestAssert.Equal(0, AirProfileDtos.TopPlayedChampions(account, "ODIN", [receipt]).Count,
            "Dominion must not receive Classic top champions");
    }

    [Check("server.air-profile-history-fellows-rewards")]
    public static void RecentGamesAndResultsExposeRecordedRosterAndReward()
    {
        var account = new PlatformAccount(1, 1_000_001, "profile", "Profile", "", "",
            new MatchPlayer("Profile", "Annie", Team.Order, SummonerLevel: 13), ProfileIconId: 7,
            Economy: new PlatformEconomyData(false, 987, 0, 321, 13, [], [], new Dictionary<int, int>(), [], []));
        var friend = new PlatformAccount(2, 1_000_002, "friend", "Friend", "", "",
            new MatchPlayer("Friend", "Orianna", Team.Chaos, SummonerLevel: 18), ProfileIconId: 8);
        var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", 90,
            [
                ResultPlayer(account, "Annie", "Order", 7),
                ResultPlayer(friend, "Orianna", "Chaos", 8)
            ], false);
        var reward = new PlatformReward(37, 120, 12, 13, 987, 9_321, 1_400);
        var receipt = new PlatformResult(9, Guid.NewGuid(), [account.Id, friend.Id],
            JsonSerializer.SerializeToElement(result), DateTimeOffset.UtcNow, AirGameDtos.NormalQueueId,
            new Dictionary<long, PlatformReward> { [account.Id] = reward });
        var members = new[]
        {
            new PlatformGames.Member(account, Team.Order),
            new PlatformGames.Member(friend, Team.Chaos)
        };
        var lobby = new PlatformGames.Lobby(9, "Result", "", account.Id, 2)
        {
            QueueId = AirGameDtos.NormalQueueId, Members = [.. members], Result = result
        };

        var end = AirGameDtos.Results(lobby, members[0], reward);
        var own = ((ArrayCollection)end["teamPlayerParticipantStats"]).Cast<AsObject>().Single();
        TestAssert.Equal(13d, own["level"], "result tooltip uses account summoner level");
        var raw = ((ArrayCollection)own["statistics"]).Cast<AsObject>()
            .ToDictionary(stat => (string)stat["statTypeName"], stat => (double)stat["value"]);
        TestAssert.Equal(7d, raw["LEVEL"], "raw result LEVEL remains champion level");
        TestAssert.Equal(1055d, raw["ITEM0"], "result history keeps the recorded item slots");
        TestAssert.Equal(4d, own["spell1Id"], "result keeps the recorded first summoner spell");
        TestAssert.Equal(14d, own["spell2Id"], "result keeps the recorded second summoner spell");
        TestAssert.Equal(37d, end["ipEarned"]);
        TestAssert.Equal(37d, end["basePoints"], "receipts predating boost components retain their base IP");
        TestAssert.Equal(120d, end["experienceEarned"]);
        TestAssert.Equal(321d, end["experienceTotal"], "EOG progress is current-level XP, not lifetime XP");
        TestAssert.Equal(1_079d, end["expPointsToNextLevel"], "EOG reports XP remaining in the current level");
        TestAssert.Equal(987d, end["ipTotal"], "EOG uses the live account IP balance");
        var balance = AirDtos.AccountBalanceNotification(account);
        TestAssert.Equal("com.riotgames.platform.messaging.StoreAccountBalanceNotification", balance.TypeName);
        TestAssert.Equal(end["ipTotal"], balance["ip"], "the header notification uses the same persisted balance as EOG");
        TestAssert.Equal(0d, balance["rp"]);
        TestAssert.Equal(1d, end["talentPointsGained"], "one level grants one talent point");
        TestAssert.Equal(0, ((ArrayCollection)end["newSpells"]).Count,
            "the stock controller rebuilds newSpells from its spell catalog after refresh");
        TestAssert.Equal(true, end["leveledUp"]);

        var shortEnd = AirGameDtos.Results(lobby, members[0]);
        TestAssert.Equal(0d, shortEnd["ipEarned"]);
        TestAssert.Equal(0d, shortEnd["experienceEarned"]);
        TestAssert.Equal(987d, shortEnd["ipTotal"], "a result without a reward keeps the current IP balance");
        TestAssert.Equal(321d, shortEnd["experienceTotal"], "a result without a reward keeps current-level XP");

        var recent = (AsObject)((ArrayCollection)AirGameDtos.RecentGames(account, [receipt])["gameStatistics"])[0];
        var fellows = ((ArrayCollection)recent["fellowPlayers"]).Cast<AsObject>().ToArray();
        TestAssert.Equal(1, fellows.Length);
        TestAssert.Equal(61d, fellows[0]["championId"]);
        TestAssert.Equal((double)friend.SummonerId, fellows[0]["summonerId"]);
        TestAssert.Equal(200d, fellows[0]["teamId"]);
        TestAssert.Equal(37d, recent["ipEarned"]);
        TestAssert.Equal(120d, recent["experienceEarned"]);
        TestAssert.Equal(true, recent["leveledUp"]);
    }

    private static AsObject Summary(AsObject summaries, string type) =>
        ((ArrayCollection)summaries["playerStatSummarySet"]).Cast<AsObject>()
            .Single(summary => (string)summary["playerStatSummaryType"] == type);

    private static double StatValue(AsObject summaryStats, string name) =>
        StatValue(((ArrayCollection)summaryStats["stats"]).Cast<AsObject>().ToArray(), name);

    private static double StatValue(IEnumerable<AsObject> stats, string name) =>
        (double)stats.Single(stat => (string)stat["statType"] == name)["value"];

    private static PlatformResult Receipt(long gameId, PlatformAccount account, string champion,
        string team, string winner, int queueId, int minutes, HeroMatchStatisticsSnapshot? statistics = null)
    {
        var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", winner, 90,
            [new PlayerMatchResult((ulong)account.SummonerId, (uint)account.Id, account.Summoner,
                champion, team, 0, true, 30, 2, 1, 3, 0, 2, 1, 0, 0, 12, 4, 1, 500, 100,
                statistics ?? new HeroMatchStatistics().Snapshot(0),
                ["SummonerFlash", "SummonerDot"], [0, 0, 0, 0], [], false, false)], false);
        return new PlatformResult(gameId, Guid.NewGuid(), [account.Id],
            JsonSerializer.SerializeToElement(result), DateTimeOffset.UtcNow.AddMinutes(minutes), queueId);
    }

    private static PlayerMatchResult ResultPlayer(PlatformAccount account, string champion, string team, int level) =>
        new((ulong)account.SummonerId, (uint)account.Id, account.Summoner, champion, team, 0, true, level,
            2, 1, 3, 0, 2, 1, 0, 0, 12, 4, 1, 500, 100,
            new HeroMatchStatistics().Snapshot(0), ["SummonerFlash", "SummonerDot"], [0, 0, 0, 0],
            [new MatchResultItem(1055, 1, 0)], false, false);
}
