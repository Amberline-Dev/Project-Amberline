using System.Text.Json;
using Amberline.Network.Sessions;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Platform.AirWire;

namespace Amberline.Platform;

/// <summary>
/// Result-backed profile DTOs for the patch-126 statistics services. The ranked summaries carry the ratings of this host's ranked
/// games (batch 586, <see cref="PlatformRatings"/>); a ranked queue nobody played stays an empty, unrated (0) summary, which the
/// stock profile does not show (getEloDisplayType: rating 0 is eloNothing).
/// </summary>
public static class AirProfileDtos
{
    private const string LifetimeStatsAlias = AirProtocolNames.PlayerLifetimeStats;
    private const string PlayerStatsAlias = AirProtocolNames.PlayerStatSummaries;
    private const string PlayerStatAlias = AirProtocolNames.PlayerStatSummary;
    private const string SummaryStatsAlias = AirProtocolNames.SummaryAggStats;
    private const string SummaryStatAlias = AirProtocolNames.SummaryAggStat;
    private const string AggregatedStatsAlias = AirProtocolNames.AggregatedStats;
    private const string AggregatedStatsKeyAlias = AirProtocolNames.AggregatedStatsKey;
    private const string AggregatedStatAlias = AirProtocolNames.AggregatedStat;
    private const string ChampionStatInfoAlias = AirProtocolNames.ChampionStatInfo;

    private static readonly string[] SummaryTypes =
        ["Unranked", "RankedSolo5x5", "RankedPremade5x5", "RankedPremade3x3", "RankedSolo3x3", "OdinUnranked", "OdinRankedSolo",
            "odinRankedPremade", "CoopVsAI"];

    /// <summary>
    /// PlayerStatSummaryType.convert: the summary a queue type's games count in. NORMAL and BOT share Unranked, Dominion's queues are
    /// OdinUnranked (batch 579) and each ranked queue type has its own (batch 586); null for a type the client cannot convert.
    /// </summary>
    public static string? SummaryType(string queueType) => queueType switch
    {
        "NORMAL" or "BOT" => "Unranked",
        PlatformGames.DominionQueueType => "OdinUnranked",
        "RANKED_SOLO_5x5" => "RankedSolo5x5",
        "RANKED_PREMADE_5x5" => "RankedPremade5x5",
        "RANKED_PREMADE_3x3" => "RankedPremade3x3",
        "RANKED_SOLO_3x3" => "RankedSolo3x3",
        // The client's own spellings, odinRankedPremade with a lower-case o (PlayerStatSummaryType.DOMINION_RANKED_PREMADE).
        "ODIN_RANKED_SOLO" => "OdinRankedSolo",
        "ODIN_RANKED_PREMADE" => "odinRankedPremade",
        _ => null,
    };

    public static AsObject PlayerStatSummaries(PlatformAccount account, IEnumerable<PlatformResult> receipts)
    {
        ArgumentNullException.ThrowIfNull(account);
        var all = receipts.ToArray();
        var records = NormalRecords(account, all).ToArray();
        var modified = records.Length == 0
            ? DateTime.UnixEpoch
            : records.Max(record => record.Receipt.CompletedAt.UtcDateTime);
        return Object(PlayerStatsAlias,
            ("userId", (double)account.Id),
            ("playerStatSummarySet", List(SummaryTypes.Select(type =>
            {
                var summaryStats = new StatsAccumulator();
                foreach (var record in NormalRecords(account, all, type)) summaryStats.Add(record.Result, record.Player);
                // Only a ranked queue has a rating. Unranked keeps 0: normal matchmaking has no rating here, and a stock-looking 1200
                // would invent one. A ranked queue nobody played is 0 too, which the profile does not show.
                var queueType = PlatformGames.Queues.FirstOrDefault(queue => queue.Ranked && SummaryType(queue.Type) == type)?.Type;
                var ratings = queueType is null ? Array.Empty<PlatformRating>() : PlatformRatings.History(account.Id, all, queueType).ToArray();
                return Object(PlayerStatAlias,
                    ("userId", (double)account.Id),
                    ("playerStatSummaryType", type),
                    ("wins", summaryStats.Wins),
                    ("losses", summaryStats.Losses),
                    ("leaves", summaryStats.Leaves),
                    ("rating", ratings.Length == 0 ? 0 : ratings[^1].After),
                    ("maxRating", ratings.Length == 0 ? 0 : ratings.Max(rating => rating.After)),
                    ("modifyDate", modified),
                    ("aggregatedStats", SummaryStats(summaryStats)));
            }))));
    }

    /// <summary>
    /// What <c>playerStatsService.retrievePlayerStatsByAccountId</c> answers: SummonerProfileController casts the result
    /// to PlayerLifetimeStats and reads <c>playerStatSummaries</c> from it, so the bare summaries object it received
    /// before was dropped by the cast and the profile summary stayed empty.
    /// </summary>
    public static AsObject LifetimeStats(PlatformAccount account, IEnumerable<PlatformResult> receipts) =>
        Object(LifetimeStatsAlias,
            ("userId", (double)account.Id),
            ("playerStatSummaries", PlayerStatSummaries(account, receipts)),
            ("previousFirstWinOfDay", account.Economy is { FirstWinAt: > 0 } economy
                ? DateTimeOffset.FromUnixTimeMilliseconds(economy.FirstWinAt).UtcDateTime : null),
            ("gameStatistics", List([])));

    public static AsObject AggregatedStats(PlatformAccount account, string gameMode,
        IEnumerable<PlatformResult> receipts, string season = "CURRENT")
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(gameMode);
        if (season is not ("CURRENT" or "ONE")) throw new ArgumentException("Unknown statistics season.", nameof(season));
        // The stock profile can request the archived Season One and Dominion.
        // This host has current Classic receipts only; never relabel them. Ranked games are Classic too (batch 586): the profile page
        // that shows these is titled "Ranked Stats" (summonerProfile_page_stats).
        var records = gameMode == "CLASSIC" && season == "CURRENT" ? NormalRecords(account, receipts, ClassicSummaries).ToArray() : [];
        var global = new StatsAccumulator();
        var champions = new Dictionary<int, StatsAccumulator>();
        foreach (var record in records)
        {
            global.Add(record.Result, record.Player);
            var championId = AirGameDtos.ChampionNames.FirstOrDefault(pair => pair.Value == record.Player.Champion).Key;
            if (championId == 0) continue;
            (champions.TryGetValue(championId, out var champion) ? champion : champions[championId] = new StatsAccumulator())
                .Add(record.Result, record.Player);
        }

        var lifetime = AggregatedStatsFor(global, 0).Cast<AsObject>().ToList();
        foreach (var pair in champions.OrderBy(pair => pair.Key))
            lifetime.AddRange(AggregatedStatsFor(pair.Value, pair.Key, AirGameDtos.ChampionNames[pair.Key]).Cast<AsObject>());
        return Object(AggregatedStatsAlias,
            ("key", Object(AggregatedStatsKeyAlias,
                ("userId", (double)account.Id), ("gameMode", gameMode))),
            ("lifetimeStatistics", List(lifetime)));
    }

    public static ArrayCollection TopPlayedChampions(PlatformAccount account, string gameMode,
        IEnumerable<PlatformResult> receipts)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(gameMode);
        var champions = new Dictionary<int, StatsAccumulator>();
        foreach (var record in gameMode == "CLASSIC" ? NormalRecords(account, receipts, ClassicSummaries) : [])
        {
            var championId = AirGameDtos.ChampionNames.FirstOrDefault(pair => pair.Value == record.Player.Champion).Key;
            if (championId == 0) continue;
            (champions.TryGetValue(championId, out var champion) ? champion : champions[championId] = new StatsAccumulator())
                .Add(record.Result, record.Player);
        }

        return List(champions.OrderByDescending(pair => pair.Value.Games)
            .ThenByDescending(pair => pair.Value.Wins)
            .ThenBy(pair => pair.Key)
            .Select(pair => Object(ChampionStatInfoAlias,
                ("championId", (double)pair.Key),
                ("totalGamesPlayed", (double)pair.Value.Games),
                ("stats", AggregatedStatsFor(pair.Value, pair.Key, AirGameDtos.ChampionNames[pair.Key])))));
    }

    private static readonly string[] ClassicSummaries = ["Unranked", "RankedSolo5x5", "RankedPremade5x5", "RankedPremade3x3", "RankedSolo3x3"];

    /// <summary>
    /// The wins, losses and leaves of a queue type's summary (Unranked without one) that the profile shows; the end-of-game screen prints
    /// the record of the game's own queue type, so a ranked game shows its ranked record and the client's showRankedElo counts it.
    /// </summary>
    public static (int Wins, int Losses, int Leaves) Record(PlatformAccount account, IEnumerable<PlatformResult> receipts, string? queueType = null)
    {
        var stats = new StatsAccumulator();
        foreach (var record in NormalRecords(account, receipts, (queueType is null ? null : SummaryType(queueType)) ?? "Unranked"))
            stats.Add(record.Result, record.Player);
        return (stats.Wins, stats.Losses, stats.Leaves);
    }

    private static IEnumerable<(PlatformResult Receipt, MatchResult Result, PlayerMatchResult Player)> NormalRecords(
        PlatformAccount account, IEnumerable<PlatformResult> receipts, params string[] summaries)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (summaries.Length == 0) summaries = ["Unranked"];
        foreach (var receipt in receipts)
        {
            // Every matchmade queue counts in its type's summary (PlayerStatSummaryType.convert, used by
            // PlayerStatSummaries.getPlayerStatSummary): NORMAL and BOT alike in Unranked, so Co-op and Twisted Treeline count; the Dominion
            // queues (ODIN_UNRANKED) and each ranked queue in their own. Custom games count in none.
            if (PlatformGames.QueueOf(receipt.QueueId) is not { } queue || !summaries.Contains(SummaryType(queue.Type)))
                continue;
            var result = receipt.Result.Deserialize<MatchResult>()
                ?? throw new InvalidDataException($"Result receipt {receipt.GameId} is empty.");
            var player = result.Players.SingleOrDefault(player => player.PlayerId == (ulong)account.SummonerId);
            if (player is null)
                throw new InvalidDataException($"Result receipt {receipt.GameId} has no player {account.SummonerId}.");
            yield return (receipt, result, player);
        }
    }

    private static AsObject SummaryStats(StatsAccumulator stats) =>
        Object(SummaryStatsAlias,
            ("stats", List(stats.Values.Select(pair => Object(SummaryStatAlias,
                ("statType", pair.Key), ("value", AirWhole(pair.Value, pair.Key)))))));

    private static ArrayCollection AggregatedStatsFor(StatsAccumulator stats, int championId, string? championName = null) =>
        List(stats.Values.Select(pair => AggregatedStat(pair.Key, pair.Value, championId, championName, stats.Games)));

    private static AsObject AggregatedStat(string statType, double value, int championId,
        string? championName, int count) =>
        Object(AggregatedStatAlias,
            ("id", 0d), ("statType", statType), ("value", AirWhole(value, statType)),
            ("count", (double)count), ("championId", (double)championId),
            ("championDisplayName", championName ?? string.Empty),
            ("displayName", statType), ("description", statType));

    private static double AirWhole(double value, string field)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException($"AIR profile statistic '{field}' must be finite.");
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private sealed class StatsAccumulator
    {
        private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _maxima = new(StringComparer.Ordinal);

        public int Games { get; private set; }
        public int Wins { get; private set; }
        public int Losses { get; private set; }
        public int Leaves { get; private set; }

        public IEnumerable<KeyValuePair<string, double>> Values =>
            _values.Concat(_maxima).OrderBy(pair => pair.Key);

        public void Add(MatchResult result, PlayerMatchResult player)
        {
            Games++;
            if (player.IsLeaver) Leaves++;
            else if (player.Team.Equals(result.Winner, StringComparison.Ordinal)) Wins++;
            else Losses++;

            var s = player.Statistics;
            Total("TOTAL_SESSIONS_PLAYED", 1);
            Total("TOTAL_SESSIONS_WON", player.IsLeaver ? 0 : player.Team.Equals(result.Winner, StringComparison.Ordinal) ? 1 : 0);
            Total("TOTAL_SESSIONS_LOST", player.IsLeaver ? 0 : player.Team.Equals(result.Winner, StringComparison.Ordinal) ? 0 : 1);
            Total("TOTAL_LEAVES", player.IsLeaver ? 1 : 0);
            Total("TOTAL_CHAMPION_KILLS", player.Kills);
            Total("TOTAL_DEATHS_PER_SESSION", player.Deaths);
            Total("TOTAL_ASSISTS", player.Assists);
            Total("TOTAL_TURRETS_KILLED", player.TurretsKilled);
            Total("TOTAL_MINION_KILLS", player.LaneMinionsKilled + player.OtherMinionsKilled);
            Total("TOTAL_NEUTRAL_MINIONS_KILLED", player.NeutralMinionsKilled);
            Total("TOTAL_DOUBLE_KILLS", s.DoubleKills);
            Total("TOTAL_TRIPLE_KILLS", s.TripleKills);
            Total("TOTAL_QUADRA_KILLS", s.QuadraKills);
            Total("TOTAL_PENTA_KILLS", s.PentaKills);
            Total("TOTAL_DAMAGE_DEALT", s.TotalDamageDealt);
            Total("TOTAL_PHYSICAL_DAMAGE_DEALT", s.PhysicalDamageDealt);
            Total("TOTAL_MAGIC_DAMAGE_DEALT", s.MagicDamageDealt);
            Total("TOTAL_DAMAGE_TAKEN", s.TotalDamageTaken);
            Total("TOTAL_HEAL", s.TotalHeal);
            Total("TOTAL_GOLD_EARNED", s.GoldEarned);
            Total("TOTAL_TIME_SPENT_DEAD", s.TotalTimeSpentDead);
            Max("MAX_CHAMPIONS_KILLED", player.Kills);
            Max("MAX_NUM_DEATHS", player.Deaths);
            Max("MAX_LARGEST_KILLING_SPREE", player.LargestKillingSpree);
            Max("MAX_LARGEST_CRITICAL_STRIKE", s.LargestCriticalStrike);
            Max("MAX_TIME_PLAYED", result.DurationSeconds);
        }

        private void Total(string key, double value) => _values[key] = _values.GetValueOrDefault(key) + value;
        private void Max(string key, double value) => _maxima[key] = Math.Max(_maxima.GetValueOrDefault(key), value);
    }
}
