using System.Security.Cryptography;
using Amberline.Data;

namespace Amberline.Platform;

public sealed partial class PlatformAccounts
{
    /// <summary>
    /// Reconstruction: the client fixes the recipes (the combiner list it receives: 2 runes of a tier give one of that tier,
    /// 5 give one of the next) and reads the result as a RuneQuantity list, but which rune the server awarded is not
    /// recoverable; a uniform pick among the shipped runes of the result tier is the owner's approved default.
    /// Consumes unused input runes and awards one result-tier rune atomically.
    /// The caller supplies IDs only; client-side Rune metadata is not trusted.
    /// There is no idempotency key: each successful invocation is one transaction.
    /// </summary>
    public int UseRuneCombiner(long accountId, int combinerId, IReadOnlyList<int> runeIds)
    {
        ArgumentNullException.ThrowIfNull(runeIds);
        var combiner = PlatformRuneCombiner.Get(combinerId);
        if (runeIds.Count != combiner.InputCount)
            throw new ArgumentException($"Rune combiner {combinerId} requires {combiner.InputCount} runes.", nameof(runeIds));

        lock (_gate)
        {
            var account = RequireAccount(accountId);
            var data = account.Economy is null
                ? throw new InvalidOperationException(
                    "Rune combining is unavailable for legacy all-unlocked accounts.")
                : PlatformEconomy.Copy(account.Economy);
            if (data.AllUnlocked)
                throw new InvalidOperationException(
                    "Rune combining is unavailable for all-unlocked accounts.");

            var catalog = RuneCatalogForCombining.Value;
            var owned = data.OwnedRunes is null
                ? new Dictionary<int, int>()
                : new Dictionary<int, int>(data.OwnedRunes);
            var consumed = runeIds.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
            foreach (var (runeId, count) in consumed)
            {
                var rune = catalog.Get(runeId) ?? throw new ArgumentException(
                    $"Unknown rune ID {runeId}.", nameof(runeIds));
                if (rune.Tier != combiner.InputTier)
                    throw new ArgumentException(
                        $"Rune {runeId} is tier {(int)rune.Tier}, but combiner {combinerId} requires tier {(int)combiner.InputTier}.",
                        nameof(runeIds));
                if (count > AvailableCopies(account, runeId, owned))
                    throw new InvalidOperationException(
                        $"The account does not have {count} unused copies of rune {runeId}.");
            }

            foreach (var (runeId, count) in consumed)
            {
                var remaining = owned[runeId] - count;
                if (remaining == 0) owned.Remove(runeId);
                else owned[runeId] = remaining;
            }

            // The result pool is deliberately evaluated after the input burn so
            // a same-tier two-rune recipe can reuse a newly freed quantity slot.
            var eligible = catalog.All
                .Where(rune => rune.Tier == combiner.OutputTier)
                .Where(rune => owned.GetValueOrDefault(rune.Id) < (rune.Color == RuneColor.Black ? 3 : 9))
                .Select(rune => rune.Id)
                .ToArray();
            if (eligible.Length == 0)
                throw new InvalidOperationException(
                    $"No tier {(int)combiner.OutputTier} rune can receive another copy.");

            var awardedRuneId = eligible[RandomNumberGenerator.GetInt32(eligible.Length)];
            owned[awardedRuneId] = owned.GetValueOrDefault(awardedRuneId) + 1;
            var nextData = data with { OwnedRunes = owned };
            var updated = account with { Economy = nextData };
            Save(_state with
            {
                Accounts = _state.Accounts.Select(item => item.Id == accountId ? updated : item).ToArray(),
            });
            return awardedRuneId;
        }
    }

    private static readonly Lazy<RuneCatalog> RuneCatalogForCombining = new(() =>
        new RuneCatalog(GameData.Default.Paths), LazyThreadSafetyMode.PublicationOnly);

    private static int AvailableCopies(PlatformAccount account, int runeId,
        IReadOnlyDictionary<int, int> owned)
    {
        var maximumEquipped = 0;
        foreach (var page in RunePagesFor(account))
        {
            var equipped = page.Slots.Values.Count(id => id == runeId);
            if (equipped > maximumEquipped) maximumEquipped = equipped;
        }
        return Math.Max(0, owned.GetValueOrDefault(runeId) - maximumEquipped);
    }
}
