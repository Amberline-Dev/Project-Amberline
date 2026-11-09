using System.Collections.ObjectModel;
using Amberline.Data;

namespace Amberline.Core;

/// <summary>
/// One validated patch-126 mastery allocation for a player. The rules are the stock AIR client's own
/// (TalentGroupController.activateTalentItems and increaseTalentRank): a rank never exceeds the talent's
/// maxRank; a row opens once the ranks in the earlier rows of its group reach the row's pointsToActivate;
/// the summoner level reaches the talent's minLevel; a talent with a prerequisite needs that prerequisite at
/// its maxRank; the total never exceeds the account's talent points (one per summoner level, 30 at most).
/// </summary>
public sealed class MasteryPage
{
    public const int MaximumPoints = 30;
    private readonly IReadOnlyDictionary<int, int> _allocations;

    private MasteryPage(SortedDictionary<int, int> allocations)
    {
        _allocations = new ReadOnlyDictionary<int, int>(
            allocations);
        TotalPoints = _allocations.Values.Sum();
    }

    public static MasteryPage Empty { get; } = new(
        new SortedDictionary<int, int>());

    public IReadOnlyDictionary<int, int> Allocations => _allocations;
    public int TotalPoints { get; }

    public int Rank(int masteryId) =>
        _allocations.TryGetValue(masteryId, out var rank) ? rank : 0;

    public static MasteryPage Create(
        MasteryCatalog catalog,
        IReadOnlyDictionary<int, int>? allocations = null,
        int summonerLevel = 30)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (summonerLevel is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(summonerLevel), "Summoner level must be 1..30.");

        var normalized = new SortedDictionary<int, int>();
        foreach (var allocation in allocations ??
                     (IReadOnlyDictionary<int, int>)new Dictionary<int, int>())
        {
            if (!catalog.TryGet(allocation.Key, out var definition))
                throw new ArgumentException($"Unknown mastery id {allocation.Key}.", nameof(allocations));
            if (allocation.Value < 0)
                throw new ArgumentException(
                    $"{definition.DisplayName} in {definition.GroupName} has a negative rank.",
                    nameof(allocations));
            if (allocation.Value > definition.MaxRank)
                throw new ArgumentException(
                    $"{definition.DisplayName} in {definition.GroupName} rank {allocation.Value} exceeds max rank {definition.MaxRank}.",
                    nameof(allocations));
            if (allocation.Value > 0 && summonerLevel < definition.MinLevel)
                throw new ArgumentException(
                    $"{definition.DisplayName} in {definition.GroupName} requires summoner level {definition.MinLevel}.",
                    nameof(allocations));
            if (allocation.Value > 0) normalized.Add(allocation.Key, allocation.Value);
        }

        var totalPoints = normalized.Values.Sum();
        if (totalPoints > MaximumPoints)
            throw new ArgumentException($"Mastery page spends {totalPoints} points; maximum is {MaximumPoints}.", nameof(allocations));
        if (totalPoints > summonerLevel)
            throw new ArgumentException(
                $"Mastery page spends {totalPoints} points; summoner level {summonerLevel} permits {summonerLevel}.",
                nameof(allocations));

        foreach (var (id, _) in normalized)
        {
            var definition = catalog.Get(id);
            var previousRowPoints = normalized
                .Where(pair => catalog.Get(pair.Key).GroupId == definition.GroupId &&
                               catalog.Get(pair.Key).Row < definition.Row)
                .Sum(pair => pair.Value);
            if (previousRowPoints < definition.PointsToActivate)
                throw new ArgumentException(
                    $"{definition.DisplayName} requires {definition.PointsToActivate} earlier points in {definition.GroupName}.",
                    nameof(allocations));

            if (definition.PrerequisiteId is int prerequisite)
            {
                var prerequisiteDefinition = catalog.Get(prerequisite);
                if (RankOf(normalized, prerequisite) < prerequisiteDefinition.MaxRank)
                    throw new ArgumentException(
                        $"{definition.DisplayName} requires {prerequisiteDefinition.DisplayName} at rank {prerequisiteDefinition.MaxRank}.",
                        nameof(allocations));
            }
        }

        return normalized.Count == 0 ? Empty : new MasteryPage(normalized);
    }

    public static bool TryCreate(
        MasteryCatalog catalog,
        IReadOnlyDictionary<int, int>? allocations,
        out MasteryPage page,
        out string? error,
        int summonerLevel = 30)
    {
        try
        {
            page = Create(catalog, allocations, summonerLevel);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            page = Empty;
            error = ex.Message;
            return false;
        }
    }

    private static int RankOf(IReadOnlyDictionary<int, int> allocations, int id) =>
        allocations.TryGetValue(id, out var rank) ? rank : 0;
}
