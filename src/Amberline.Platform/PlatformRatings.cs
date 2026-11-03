using Amberline.Network.Sessions;

namespace Amberline.Platform;

/// <summary>
/// Batch 586: the Elo rating of each ranked queue (Season One: "Players will receive an individual rating for each of these queues",
/// wiki Season_One). A ranked receipt keeps each player's rating before and after the game (<see cref="PlatformResult.Ratings"/>), so the
/// change is applied once, as retail did, and the current rating is the last game's.
/// What the stock client states: a summary starts at 1200 (PlayerStatSummaries.DEFAULT_RATING); a ranked queue the player has not
/// played starts at round((highest ranked rating + 2400) / 3) when another ranked queue has a rating (getInitialRankedRating); the rating
/// is shown once wins + losses reach 10 (ProfileStatsSummaryPresentationModel.MIN_RANKED_GAMES_FOR_RANKING, and the end-of-game
/// showRankedElo).
/// Reconstruction (no shipped file and no Riot statement recovered gives retail's numbers): the standard team Elo, each player's expected
/// score from the two teams' mean ratings, with K 64 for a queue's first 10 games (the client's placement count) and 32 afterwards; a
/// leaver scores a loss.
/// </summary>
public static class PlatformRatings
{
    public const int StartingRating = 1200;
    public const int PlacementGames = 10;
    public const int PlacementK = 64;
    public const int EstablishedK = 32;

    /// <summary>The account's rating after each ranked game of this queue type, in the order they were rated.</summary>
    public static IEnumerable<PlatformRating> History(long account, IEnumerable<PlatformResult> receipts, string queueType) =>
        receipts.Where(receipt => PlatformGames.QueueOf(receipt.QueueId)?.Type == queueType && receipt.Ratings?.ContainsKey(account) == true)
            .Select(receipt => receipt.Ratings![account]);

    public static int Current(long account, IEnumerable<PlatformResult> receipts, string queueType)
    {
        var all = receipts as IReadOnlyCollection<PlatformResult> ?? receipts.ToArray();
        return History(account, all, queueType).LastOrDefault()?.After ?? Initial(account, all, queueType);
    }

    /// <summary>
    /// PlayerStatSummaries.setInitialRating: a summary the client counts as ranked (PlayerStatSummaryType.isRanked, a name starting with
    /// "Ranked") starts at getInitialRankedRating, 1200 or the highest such rating pulled two thirds of the way to 1200; any other starts at
    /// 1200. So the Dominion ranked summaries (OdinRankedSolo, odinRankedPremade) start at 1200 and never seed the classic ones (batch 590).
    /// </summary>
    private static int Initial(long account, IReadOnlyCollection<PlatformResult> receipts, string queueType)
    {
        static bool IsRanked(string type) => AirProfileDtos.SummaryType(type)?.StartsWith("Ranked", StringComparison.Ordinal) == true;
        if (!IsRanked(queueType)) return StartingRating;
        var best = PlatformGames.Queues.Where(queue => queue.Ranked && IsRanked(queue.Type)).Select(queue => queue.Type).Distinct()
            .Select(type => History(account, receipts, type).LastOrDefault()?.After ?? 0).DefaultIfEmpty(0).Max();
        return best == 0 ? StartingRating : (int)Math.Round((best + 2 * StartingRating) / 3.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>The rating change of every human seat of a finished ranked game, from the ratings the earlier receipts left.</summary>
    public static Dictionary<long, PlatformRating> Rate(MatchResult result, IReadOnlyDictionary<ulong, long> accountBySummoner,
        IReadOnlyCollection<PlatformResult> earlier, string queueType)
    {
        var seats = result.Players.Where(player => !player.IsBot).Select(player =>
        {
            var account = accountBySummoner[player.PlayerId];
            return (Player: player, Account: account, Rating: Current(account, earlier, queueType),
                Games: History(account, earlier, queueType).Count());
        }).ToArray();
        if (seats.Select(seat => seat.Player.Team).Distinct().Count() != 2) return [];
        double Mean(string team) => seats.Where(seat => seat.Player.Team == team).Average(seat => seat.Rating);
        return seats.ToDictionary(seat => seat.Account, seat =>
        {
            var expected = 1 / (1 + Math.Pow(10, (Mean(seat.Player.Team == "Order" ? "Chaos" : "Order") - Mean(seat.Player.Team)) / 400));
            var score = !seat.Player.IsLeaver && seat.Player.Team == result.Winner ? 1 : 0;
            var k = seat.Games < PlacementGames ? PlacementK : EstablishedK;
            return new PlatformRating(seat.Rating, seat.Rating + (int)Math.Round(k * (score - expected), MidpointRounding.AwayFromZero));
        });
    }
}
