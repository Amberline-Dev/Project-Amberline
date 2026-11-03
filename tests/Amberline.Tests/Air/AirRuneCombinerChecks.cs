using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Air;

public static class AirRuneCombinerChecks
{
    private static readonly Lazy<RuneCatalog> Catalog = new(() => new RuneCatalog());

    [Check("server.air-rune-combiner-catalog")]
    public static void ExposesTheFiveShippedRecipes()
    {
        var recipes = PlatformRuneCombiner.All;
        TestAssert.Equal(5, recipes.Count);
        TestAssert.Equal((2, RuneTier.One, RuneTier.One),
            (recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierOneId).InputCount,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierOneId).InputTier,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierOneId).OutputTier));
        TestAssert.Equal((2, RuneTier.Two, RuneTier.Two),
            (recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierTwoId).InputCount,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierTwoId).InputTier,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierTwoId).OutputTier));
        TestAssert.Equal((2, RuneTier.Three, RuneTier.Three),
            (recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierThreeId).InputCount,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierThreeId).InputTier,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.TwoTierThreeId).OutputTier));
        TestAssert.Equal((5, RuneTier.One, RuneTier.Two),
            (recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.FiveTierOneId).InputCount,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.FiveTierOneId).InputTier,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.FiveTierOneId).OutputTier));
        TestAssert.Equal((5, RuneTier.Two, RuneTier.Three),
            (recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.FiveTierTwoId).InputCount,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.FiveTierTwoId).InputTier,
                recipes.Single(recipe => recipe.Id == PlatformRuneCombiner.FiveTierTwoId).OutputTier));
        TestAssert.Equal(PlatformRuneCombiner.AirAlias,
            (string)PlatformRuneCombiner.ToAir(recipes[0]).TypeName!);
        TestAssert.Throws<ArgumentException>(() => PlatformRuneCombiner.Get(9999));
    }

    [Check("server.air-rune-combiner-count-conservation-reopen")]
    public static void ConsumesInputsAwardsOneAndPersistsAcrossReopen()
    {
        var root = TemporaryDirectory("league-air-rune-combiner-count");
        try
        {
            var inputs = Runes(RuneTier.One).Take(5).Select(rune => rune.Id).ToArray();
            Seed(root, inputs.ToDictionary(id => id, _ => 1));
            int awarded;
            string pages;
            using (var accounts = new PlatformAccounts(root))
            {
                var before = TotalOwned(accounts.Get(1));
                pages = Snapshot(accounts.GetRunePages(1));
                awarded = accounts.UseRuneCombiner(1, PlatformRuneCombiner.FiveTierOneId, inputs);
                var afterAccount = accounts.Get(1);
                TestAssert.Equal(before - 4, TotalOwned(afterAccount));
                TestAssert.Equal(RuneTier.Two, Catalog.Value.Get(awarded)!.Tier);
                TestAssert.Equal(1, afterAccount.Economy!.OwnedRunes![awarded]);
                TestAssert.Equal(pages, Snapshot(accounts.GetRunePages(1)),
                    "combining must not alter saved rune pages or the active loadout");
            }

            using var reopened = new PlatformAccounts(root);
            var persisted = reopened.Get(1).Economy!.OwnedRunes!;
            TestAssert.Equal(1, persisted[awarded]);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-combiner-boundary-recipes")]
    public static void AcceptsTwoRuneTierThreeAndFiveRuneTierOneAndTwoRecipes()
    {
        foreach (var recipe in PlatformRuneCombiner.All)
        {
            var root = TemporaryDirectory("league-air-rune-combiner-boundary");
            try
            {
                var inputs = Runes(recipe.InputTier).Take(recipe.InputCount).Select(rune => rune.Id).ToArray();
                Seed(root, inputs.ToDictionary(id => id, _ => 1));
                using var accounts = new PlatformAccounts(root);
                var awarded = accounts.UseRuneCombiner(1, recipe.Id, inputs);
                TestAssert.Equal(recipe.OutputTier, Catalog.Value.Get(awarded)!.Tier, recipe.Name);
            }
            finally { DeleteDirectory(root); }
        }
    }

    [Check("server.air-rune-combiner-invalid-atomic")]
    public static void RejectsWrongCountTierAndUnknownIdsWithoutSaving()
    {
        var root = TemporaryDirectory("league-air-rune-combiner-invalid");
        try
        {
            var tierOne = Runes(RuneTier.One).Take(2).Select(rune => rune.Id).ToArray();
            var tierTwo = Runes(RuneTier.Two).First().Id;
            Seed(root, new Dictionary<int, int> { [tierOne[0]] = 1, [tierOne[1]] = 1, [tierTwo] = 1 });
            var beforeBytes = File.ReadAllBytes(Path.Combine(root, "accounts.json"));
            using var accounts = new PlatformAccounts(root);
            var before = SnapshotEconomy(accounts.Get(1));
            TestAssert.Throws<ArgumentException>(() => accounts.UseRuneCombiner(
                1, PlatformRuneCombiner.TwoTierOneId, [tierOne[0]]));
            TestAssert.Throws<ArgumentException>(() => accounts.UseRuneCombiner(
                1, PlatformRuneCombiner.TwoTierOneId, [tierOne[0], tierTwo]));
            TestAssert.Throws<ArgumentException>(() => accounts.UseRuneCombiner(
                1, PlatformRuneCombiner.TwoTierOneId, [tierOne[0], 1234]));
            TestAssert.Equal(before, SnapshotEconomy(accounts.Get(1)));
            TestAssert.Equal(beforeBytes, File.ReadAllBytes(Path.Combine(root, "accounts.json")));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-combiner-max-equipped-per-page")]
    public static void UsesTheMaximumEquippedCopiesAcrossPages()
    {
        var root = TemporaryDirectory("league-air-rune-combiner-pages");
        try
        {
            var rune = Runes(RuneTier.One).First().Id;
            var pages = new[]
            {
                new PlatformRunePage(1, "Default", true, new Dictionary<int, int> { [1] = rune }, DateTime.UnixEpoch),
                new PlatformRunePage(2, "Page 2", false, new Dictionary<int, int> { [1] = rune }, DateTime.UnixEpoch),
            };
            Seed(root, new Dictionary<int, int> { [rune] = 3 }, pages);
            using var accounts = new PlatformAccounts(root);
            accounts.UseRuneCombiner(1, PlatformRuneCombiner.TwoTierOneId, [rune, rune]);
            TestAssert.Equal(2, TotalOwned(accounts.Get(1)));
            TestAssert.Equal(2, accounts.GetRunePages(1).Sum(page => page.Slots.Values.Count(id => id == rune)));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-combiner-no-output-atomic")]
    public static void RefusesWhenEveryOutputRuneIsAtCapacity()
    {
        var root = TemporaryDirectory("league-air-rune-combiner-capacity");
        try
        {
            var inputs = Runes(RuneTier.Two).Take(5).Select(rune => rune.Id).ToArray();
            var owned = inputs.ToDictionary(id => id, _ => 1);
            foreach (var rune in Runes(RuneTier.Three)) owned[rune.Id] = Capacity(rune);
            Seed(root, owned);
            var beforeBytes = File.ReadAllBytes(Path.Combine(root, "accounts.json"));
            using var accounts = new PlatformAccounts(root);
            var before = SnapshotEconomy(accounts.Get(1));
            TestAssert.Throws<InvalidOperationException>(() => accounts.UseRuneCombiner(
                1, PlatformRuneCombiner.FiveTierTwoId, inputs));
            TestAssert.Equal(before, SnapshotEconomy(accounts.Get(1)));
            TestAssert.Equal(beforeBytes, File.ReadAllBytes(Path.Combine(root, "accounts.json")));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-combiner-maxed-preservation")]
    public static void RefusesLegacyAndAllUnlockedAccountsWithoutChangingThem()
    {
        foreach (var legacy in new[] { true, false })
        {
            var root = TemporaryDirectory("league-air-rune-combiner-maxed");
            try
            {
                var inputs = Runes(RuneTier.One).Take(2).Select(rune => rune.Id).ToArray();
                Seed(root, inputs.ToDictionary(id => id, _ => 1), legacy: legacy, allUnlocked: !legacy);
                var beforeBytes = File.ReadAllBytes(Path.Combine(root, "accounts.json"));
                using var accounts = new PlatformAccounts(root);
                TestAssert.Throws<InvalidOperationException>(() => accounts.UseRuneCombiner(
                    1, PlatformRuneCombiner.TwoTierOneId, inputs));
                TestAssert.Equal(legacy, accounts.Get(1).Economy is null);
                TestAssert.Equal(beforeBytes, File.ReadAllBytes(Path.Combine(root, "accounts.json")));
            }
            finally { DeleteDirectory(root); }
        }
    }

    private static IReadOnlyList<RuneData> Runes(RuneTier tier) =>
        Catalog.Value.All.Where(rune => rune.Tier == tier).ToArray();

    private static int Capacity(RuneData rune) => rune.Color == RuneColor.Black ? 3 : 9;

    private static int TotalOwned(PlatformAccount account) =>
        account.Economy?.OwnedRunes?.Values.Sum() ?? 0;

    private static string SnapshotEconomy(PlatformAccount account) =>
        JsonSerializer.Serialize(account.Economy, MatchRoster.JsonOptions);

    private static string Snapshot(IEnumerable<PlatformRunePage> pages) =>
        JsonSerializer.Serialize(pages.Select(page => new
        {
            page.PageId,
            page.Name,
            page.IsCurrent,
            Slots = page.Slots.OrderBy(pair => pair.Key),
            page.CreatedAt,
        }), MatchRoster.JsonOptions);

    private static void Seed(string root, IReadOnlyDictionary<int, int> owned,
        PlatformRunePage[]? pages = null, bool legacy = false, bool allUnlocked = false)
    {
        Directory.CreateDirectory(root);
        var account = new PlatformAccount(1, 1_000_001, "combiner", "Combiner", "", "",
            new MatchPlayer("Combiner", "Annie", Team.Order),
            RunePages: pages ??
            [
                new PlatformRunePage(1, "Default", true, new Dictionary<int, int>(), DateTime.UnixEpoch),
                new PlatformRunePage(2, "Page 2", false, new Dictionary<int, int>(), DateTime.UnixEpoch),
            ],
            Economy: legacy ? null : new PlatformEconomyData(allUnlocked, 0, 0, 0, 1,
                [], [], owned.ToDictionary(pair => pair.Key, pair => pair.Value), [], []));
        var state = new PlatformAccounts.State(2, [account], [], []);
        File.WriteAllText(Path.Combine(root, "accounts.json"),
            JsonSerializer.Serialize(state, MatchRoster.JsonOptions));
    }

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
