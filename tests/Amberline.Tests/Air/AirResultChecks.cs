using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

public static class AirResultChecks
{
    [Check("server.air-results-whole-display-stats")]
    public static void ResultsAndHistoryProjectFractionalDisplayStatsAtTheAirBoundary()
    {
        var alice = new PlatformAccount(1, 1_000_001, "alice", "Alice", "", "", new MatchPlayer("Alice", "Annie", Team.Order), ProfileIconId: 7);
        var bob = new PlatformAccount(2, 1_000_002, "bob", "Bob", "", "", new MatchPlayer("Bob", "Orianna", Team.Chaos));
        var members = new[] { new PlatformGames.Member(alice, Team.Order), new PlatformGames.Member(bob, Team.Chaos) };
        var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", 90.6,
            [Player(alice, "Annie", "Order", FractionalStats()), Player(bob, "Orianna", "Chaos")], false);
        var lobby = new PlatformGames.Lobby(1, "Result check", "", alice.Id, 2) { Members = [.. members], Result = result };
        var dto = AirGameDtos.Results(lobby, members[0]);
        TestAssert.Equal(91d, dto["gameLength"], "game length is a whole AIR second");
        TestAssert.Equal(7, ((AsObject)((ArrayCollection)dto["teamPlayerParticipantStats"])[0])["profileIconId"]);
        var stats = ((ArrayCollection)((AsObject)((ArrayCollection)dto["teamPlayerParticipantStats"])[0])["statistics"])
            .Cast<AsObject>().ToDictionary(s => (string)s["statTypeName"], s => (double)s["value"]);
        TestAssert.Equal(123d, stats["GOLD_EARNED"]);
        TestAssert.Equal(46d, stats["GOLD_SPENT"]);
        TestAssert.Equal(101d, stats["TOTAL_DAMAGE_DEALT"]);
        TestAssert.Equal(21d, stats["PHYSICAL_DAMAGE_DEALT_PLAYER"]);
        TestAssert.Equal(80d, stats["MAGIC_DAMAGE_DEALT_PLAYER"]);
        TestAssert.Equal(17d, stats["TOTAL_HEAL"]);
        TestAssert.Equal(3d, stats["TOTAL_TIME_SPENT_DEAD"]);
        TestAssert.Equal(91d, stats["TIME_PLAYED"]);

        var receipt = new PlatformResult(1, Guid.NewGuid(), [alice.Id, bob.Id], JsonSerializer.SerializeToElement(result), DateTimeOffset.UtcNow);
        var recent = (AsObject)((ArrayCollection)AirGameDtos.RecentGames(alice, [receipt])["gameStatistics"])[0];
        var historyStats = ((ArrayCollection)recent["statistics"]).Cast<AsObject>().ToArray();
        TestAssert.True(historyStats.All(s => s.TypeName == "com.riotgames.platform.statistics.RawStat"),
            "PlayerGameStats casts every history entry to RawStat; RawStatDTO crashes the profile");
        TestAssert.False(recent.ContainsKey("rawStats"), "the empty stock rawStats setter is not a wire input");
        var history = historyStats.ToDictionary(s => (string)s["statType"], s => (double)s["value"]);
        TestAssert.Equal(stats["TOTAL_DAMAGE_DEALT"], history["TOTAL_DAMAGE_DEALT"], "recent games reuses the same projection");
        TestAssert.Equal(stats["GOLD_EARNED"], history["GOLD_EARNED"], "recent games keeps whole gold values");

        lobby.QueueId = 2;
        TestAssert.Equal("NORMAL", AirGameDtos.Results(lobby, members[0])["queueType"]);
        var normalHistory = (AsObject)((ArrayCollection)AirGameDtos.RecentGames(alice, [receipt with { QueueId = 2 }])["gameStatistics"])[0];
        TestAssert.Equal("MATCHED_GAME", normalHistory["gameType"], "the profile label key is summonerProfile_recentGame_MATCHED_GAME");
        TestAssert.Equal("NORMAL", normalHistory["queueType"]);

        var invalid = result with { DurationSeconds = double.NaN };
        lobby.Result = invalid;
        TestAssert.Throws<InvalidDataException>(() => AirGameDtos.Results(lobby, members[0]),
            "non-finite authoritative AIR result values must fail at the DTO boundary");

        var invalidStat = result with
        {
            Players =
            [
                Player(alice, "Annie", "Order", FractionalStats() with { TotalDamageDealt = double.PositiveInfinity }),
                Player(bob, "Orianna", "Chaos")
            ]
        };
        lobby.Result = invalidStat;
        TestAssert.Throws<InvalidDataException>(() => AirGameDtos.Results(lobby, members[0]),
            "non-finite raw statistics must fail with the field-level AIR validation");
    }

    [Check("server.air-results-victory-and-numeric-contract")]
    public static void ResultMarkersMatchTheStockPresenceCheckAndNumbersStayFinite()
    {
        var alice = new PlatformAccount(1, 1_000_001, "alice", "Alice", "", "", new MatchPlayer("Alice", "Annie", Team.Order));
        var bob = new PlatformAccount(2, 1_000_002, "bob", "Bob", "", "", new MatchPlayer("Bob", "Orianna", Team.Chaos));
        var members = new[] { new PlatformGames.Member(alice, Team.Order), new PlatformGames.Member(bob, Team.Chaos) };
        foreach (var winner in new[] { "Order", "Chaos" })
        {
            var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", winner, 90,
                [Player(alice, "Annie", "Order"), Player(bob, "Orianna", "Chaos")], false);
            var lobby = new PlatformGames.Lobby(1, "Result check", "", alice.Id, 2) { Members = [.. members], Result = result };
            foreach (var member in members)
            {
                var dto = AirGameDtos.Results(lobby, member);
                var own = ((ArrayCollection)dto["teamPlayerParticipantStats"]).Cast<AsObject>().Single();
                var raw = ((ArrayCollection)own["statistics"]).Cast<AsObject>().Select(s => (string)s["statTypeName"]).ToArray();
                // EndOfGameStatsController tests findStat(WIN) != null, even if value were zero.
                TestAssert.Equal(member.Team.ToString() == winner, raw.Contains("WIN"));
                TestAssert.Equal(member.Team.ToString() != winner, raw.Contains("LOSE"));
                foreach (var field in new[] { "firstWinBonus", "practiceMsecsUntilReset", "basePoints", "boostXpEarned",
                    "boostIpEarned", "completionBonusPoints", "timeUntilNextFirstWinBonus", "queueBonusEarned" })
                    TestAssert.True(dto[field] is double number && double.IsFinite(number));
                TestAssert.Equal(0d, own["wins"]);
                TestAssert.Equal(0d, own["losses"]);
                var receipt = new PlatformResult(1, Guid.NewGuid(), [alice.Id, bob.Id], JsonSerializer.SerializeToElement(result), DateTimeOffset.UtcNow);
                var recent = (AsObject)((ArrayCollection)AirGameDtos.RecentGames(member.Account, [receipt])["gameStatistics"])[0];
                var history = ((ArrayCollection)recent["statistics"]).Cast<AsObject>().Select(s => (string)s["statType"]).ToArray();
                TestAssert.Equal(raw, history, "history and results must use the same authoritative win marker");
            }
        }
    }

    private static PlayerMatchResult Player(PlatformAccount account, string champion, string team, HeroMatchStatisticsSnapshot? statistics = null) => new(
        PlayerId: (ulong)account.SummonerId, NetId: (uint)account.Id, Name: account.Summoner, Champion: champion,
        Team: team, SkinId: 0, Connected: true, Level: 1, Kills: 0, Deaths: 0, Assists: 0,
        KillingSprees: 0, LargestKillingSpree: 0, TurretsKilled: 0, InhibitorsKilled: 0, NexusesKilled: 0,
        LaneMinionsKilled: 0, OtherMinionsKilled: 0, NeutralMinionsKilled: 0, GoldOnHand: 0, Experience: 0,
        Statistics: statistics ?? new HeroMatchStatistics().Snapshot(0), SummonerSpells: ["SummonerFlash", "SummonerDot"],
        SpellRanks: [0, 0, 0, 0], Inventory: []);

    private static HeroMatchStatisticsSnapshot FractionalStats() => new(
        TotalDamageDealt: 100.6, PhysicalDamageDealt: 20.5, MagicDamageDealt: 79.5,
        TotalDamageTaken: 40.5, PhysicalDamageTaken: 10.5, MagicDamageTaken: 30.5,
        LargestCriticalStrike: 12.5, TotalHeal: 17, TotalUnitsHealed: 1,
        GoldSpent: 45.6, ItemsPurchased: 1, ConsumablesPurchased: 0,
        SpellCasts: [], SummonerCasts: [], LargestMultiKill: 0, DoubleKills: 0,
        TripleKills: 0, QuadraKills: 0, PentaKills: 0, UnrealKills: 0,
        GoldEarned: 123.4, FriendlyTurretsLost: 0, FriendlyInhibitorsLost: 0,
        FriendlyNexusesLost: 0, TimePlayed: 90.6, TotalTimeSpentDead: 2.6,
        LongestTimeSpentLiving: 88.0, TimeSpentDisconnected: 0, LastDisconnectedAt: null);
}
