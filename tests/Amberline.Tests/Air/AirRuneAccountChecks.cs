using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Air;

public static class AirRuneAccountChecks
{
    private static readonly Lazy<RuneCatalog> Catalog = new(() => new RuneCatalog());

    [Check("server.air-rune-pages-legacy-migration")]
    public static void LegacyPagesAreVirtualUntilTheFirstPageMutation()
    {
        var root = TemporaryDirectory("league-air-rune-legacy");
        try
        {
            var red = Catalog.Value.All.First(rune => rune.Color == RuneColor.Red).Id;
            // Batch 706: a full account completes its rune book on its first read (FullAccountsCarryEveryRunePageUpToTheHostCap and
            // AnExistingFullAccountGainsItsEmptyPagesOnTheFirstReadAndKeepsItsOwn), so this legacy check uses an economy account,
            // whose book stays virtual until its first page mutation.
            var account = new PlatformAccount(1, 1_000_001, "legacy", "Legacy", "", "",
                new MatchPlayer("Legacy", "Annie", Team.Order, RedRunes: [red]),
                Economy: new PlatformEconomyData(false, 0, 0, 0, 1, [], [], new Dictionary<int, int> { [red] = 1 }, [], []));
            Directory.CreateDirectory(root);
            var state = new PlatformAccounts.State(2, [account], [], []);
            File.WriteAllText(Path.Combine(root, "accounts.json"),
                JsonSerializer.Serialize(state, MatchRoster.JsonOptions));
            var before = File.ReadAllBytes(Path.Combine(root, "accounts.json"));

            using (var accounts = new PlatformAccounts(root))
            {
                var pages = accounts.GetRunePages(account.Id);
                TestAssert.Equal(2, pages.Length);
                TestAssert.Equal(red, pages[0].Slots[1]);
                TestAssert.False(pages[0].Slots.ContainsKey(2));
                TestAssert.True(pages[0].IsCurrent);
                TestAssert.False(pages[1].IsCurrent);
                TestAssert.True(accounts.Get(account.Id).RunePages is null,
                    "reading a legacy account must not materialize pages");
            }

            TestAssert.Equal(before, File.ReadAllBytes(Path.Combine(root, "accounts.json")),
                "read-only legacy migration must preserve the original bytes");

            using (var accounts = new PlatformAccounts(root))
            {
                accounts.SelectRunePage(account.Id, account.SummonerId, 2);
                TestAssert.True(accounts.Get(account.Id).RunePages is not null,
                    "page selection is the first persistent page mutation");
            }
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-pages-holes-reopen")]
    public static void RunePageHolesAndServerCreatedAtSurviveSaveAndReopen()
    {
        var root = TemporaryDirectory("league-air-rune-holes");
        var createdAt = default(DateTime);
        try
        {
            using (var accounts = new PlatformAccounts(root))
            {
                var account = Register(accounts, "holes", "Holes");
                var red = Catalog.Value.All.First(rune => rune.Color == RuneColor.Red).Id;
                var blue = Catalog.Value.All.First(rune => rune.Color == RuneColor.Blue).Id;
                var pages = accounts.GetRunePages(account.Id);
                createdAt = pages[0].CreatedAt;
                var saved = Book(pages, pages[0] with
                {
                    Name = "  Holey  ",
                    Slots = new Dictionary<int, int> { [2] = red, [19] = blue },
                }, pages[1] with { Name = "Spare", Slots = new Dictionary<int, int>() });
                accounts.SaveRunePages(account.Id, account.SummonerId, saved);
                var current = accounts.GetRunePages(account.Id)[0];
                TestAssert.Equal("Holey", current.Name);
                TestAssert.Equal(createdAt, current.CreatedAt);
                TestAssert.False(current.Slots.ContainsKey(1));
                TestAssert.Equal(red, current.Slots[2]);
                TestAssert.Equal(blue, current.Slots[19]);
                TestAssert.Equal(red, accounts.Get(account.Id).Loadout.RedRunes![0]);
                TestAssert.Equal(blue, accounts.Get(account.Id).Loadout.BlueRunes![0]);

                accounts.SaveLoadout(account.Id,
                    accounts.Get(account.Id).Loadout with { SummonerSpell1 = "SummonerHeal" });
                current = accounts.GetRunePages(account.Id)[0];
                TestAssert.False(current.Slots.ContainsKey(1),
                    "non-rune loadout changes must preserve page holes");
                TestAssert.Equal(red, current.Slots[2]);
            }

            using (var reopened = new PlatformAccounts(root))
            {
                var page = reopened.GetRunePages(1)[0];
                TestAssert.Equal("Holey", page.Name);
                TestAssert.Equal(createdAt, page.CreatedAt);
                TestAssert.False(page.Slots.ContainsKey(1));
                TestAssert.Equal(2, page.Slots.Count);
            }
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-pages-selection-loadout")]
    public static void SelectingAStoredPageUpdatesTheActiveLoadoutAtomically()
    {
        var root = TemporaryDirectory("league-air-rune-select");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = Register(accounts, "select", "Select");
            var yellow = Catalog.Value.All.First(rune => rune.Color == RuneColor.Yellow).Id;
            var pages = accounts.GetRunePages(account.Id);
            accounts.SaveRunePages(account.Id, account.SummonerId, Book(pages, pages[0] with { Slots = new Dictionary<int, int>() },
                pages[1] with
                {
                    Name = "Second",
                    IsCurrent = false,
                    Slots = new Dictionary<int, int> { [10] = yellow },
                }));
            accounts.SelectRunePage(account.Id, account.SummonerId, 2);
            var selected = accounts.GetRunePages(account.Id);
            TestAssert.False(selected[0].IsCurrent);
            TestAssert.True(selected[1].IsCurrent);
            TestAssert.Equal(yellow, accounts.Get(account.Id).Loadout.YellowRunes![0]);
            TestAssert.True(accounts.Get(account.Id).Loadout.RedRunes is { Length: 0 });
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-pages-validation-and-atomicity")]
    public static void RuneBookRejectsInvalidIdentityPagesAndNamesWithoutPartialSave()
    {
        var root = TemporaryDirectory("league-air-rune-validation");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = Register(accounts, "validation", "Validation");
            var blue = Catalog.Value.All.First(rune => rune.Color == RuneColor.Blue).Id;
            var original = accounts.GetRunePages(account.Id);
            var originalBook = Snapshot(original);

            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(
                account.Id, account.SummonerId + 1, original));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { Slots = new Dictionary<int, int> { [1] = blue } })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { PageId = original[1].PageId })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { IsCurrent = false }, original[1] with { IsCurrent = false })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { Name = "@@!PaG3!@@" })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { Name = "same" }, original[1] with { Name = " SAME " })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { Name = new string('x', 129) })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                Book(original, original[0] with { Name = "bad\u0001name" })));

            TestAssert.Equal(originalBook, Snapshot(accounts.GetRunePages(account.Id)),
                "a failed whole-book save must not alter pages");
            var redRunes = accounts.Get(account.Id).Loadout.RedRunes;
            TestAssert.True(redRunes is null || redRunes.Length == 0);
        }
        finally { DeleteDirectory(root); }
    }

    /// Batch 706: a full account's book has every page up to the cap, so a check that rewrites pages 1 and 2 keeps the rest.
    internal static PlatformRunePage[] Book(PlatformRunePage[] pages, PlatformRunePage first, PlatformRunePage? second = null) =>
        [first, second ?? pages[1], .. pages.Skip(2)];

    // Batch 706 (owner, 7 November 2026): a full account (level 30, unlocked) gets every rune page up to the host's cap on creation;
    // an economy account still starts with its two pages and buys the rest from the store.
    [Check("server.air-full-account-gets-every-rune-page")]
    public static void FullAccountsCarryEveryRunePageUpToTheHostCap()
    {
        foreach (var cap in new[] { 10, 6 })
        {
            var root = TemporaryDirectory("league-air-rune-full-book");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "economy.json"), $"{{\"MaximumRunePages\":{cap}}}");
                using var accounts = new PlatformAccounts(root);
                var full = accounts.RegisterPublic("fullbook", "testing-password", "Full Book");
                var book = accounts.GetRunePages(full.Id);
                TestAssert.Equal(cap, book.Length, $"a full account gets all {cap} rune pages under a host cap of {cap}");
                TestAssert.True(book.Select(page => page.PageId).SequenceEqual(Enumerable.Range(1, cap).Select(id => (long)id)),
                    "the pages are ids 1 to the cap");
                TestAssert.True(book[0] is { IsCurrent: true, Name: "Default" }, "page 1 stays current and named Default");
                TestAssert.True(book.Skip(1).All(page => !page.IsCurrent && page.Slots.Count == 0 && page.Name == $"Page {page.PageId}"),
                    "the other pages are empty, not current and named Page N");
                var economy = accounts.RegisterPublic("ecobook", "testing-password", "Eco Book", maxed: false);
                TestAssert.Equal(2, accounts.GetRunePages(economy.Id).Length, "an economy account keeps its two pages");
            }
            finally { DeleteDirectory(root); }
        }
    }

    [Check("server.air-full-account-tops-up-its-rune-book-on-first-read")]
    public static void AnExistingFullAccountGainsItsEmptyPagesOnTheFirstReadAndKeepsItsOwn()
    {
        // Batch 706: a full account stored with fewer pages gets the missing empty ones (ids not in use) the first time its pages are
        // read or saved, and the store keeps them. Opening the store alone writes nothing, and its own pages stay as they are.
        var root = TemporaryDirectory("league-air-rune-full-topup");
        try
        {
            var red = Catalog.Value.All.First(rune => rune.Color == RuneColor.Red).Id;
            Directory.CreateDirectory(root);
            long accountId;
            using (var accounts = new PlatformAccounts(root))
                accountId = accounts.RegisterPublic("topup", "testing-password", "Top Up").Id;
            var path = Path.Combine(root, "accounts.json");
            var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
            // A store written before this rule: a full account with two stored pages, page 1 filled and current, page 4 an empty spare.
            var stored = new[]
            {
                new PlatformRunePage(1, "Mine", true, new Dictionary<int, int> { [1] = red }, DateTime.UtcNow),
                new PlatformRunePage(4, "Spare", false, new Dictionary<int, int>(), DateTime.UtcNow),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state with
            {
                Accounts = state.Accounts.Select(account => account.Id == accountId ? account with { RunePages = stored } : account).ToArray(),
            }, MatchRoster.JsonOptions));
            var before = File.ReadAllBytes(path);

            using (var accounts = new PlatformAccounts(root))
            {
                TestAssert.True(before.SequenceEqual(File.ReadAllBytes(path)), "opening the store writes nothing");
                var pages = accounts.GetRunePages(accountId);
                TestAssert.Equal(10, pages.Length, "the first read completes the full book");
                TestAssert.True(pages.Select(page => page.PageId).SequenceEqual(Enumerable.Range(1, 10).Select(id => (long)id)),
                    "the added pages take the ids not in use");
                TestAssert.True(pages[0] is { IsCurrent: true, Name: "Mine" } && pages[0].Slots[1] == red,
                    "the filled current page keeps its name and runes");
                TestAssert.True(pages[3] is { Name: "Spare", IsCurrent: false }, "the existing spare page keeps its name");
                TestAssert.True(pages.Where(page => page.PageId is not (1 or 4))
                    .All(page => !page.IsCurrent && page.Slots.Count == 0 && page.Name == $"Page {page.PageId}"),
                    "the added pages are empty, not current and named Page N");
            }

            using (var reopened = new PlatformAccounts(root))
                TestAssert.Equal(10, reopened.Get(accountId).RunePages!.Length, "the added pages are stored");
        }
        finally { DeleteDirectory(root); }
    }

    private static PlatformAccount Register(PlatformAccounts accounts, string username, string summoner) =>
        accounts.RegisterPublic(username, "secret-password", summoner);

    private static string Snapshot(IEnumerable<PlatformRunePage> pages) =>
        JsonSerializer.Serialize(pages.Select(page => new
        {
            page.PageId,
            page.Name,
            page.IsCurrent,
            Slots = page.Slots.OrderBy(pair => pair.Key),
            page.CreatedAt,
        }), MatchRoster.JsonOptions);

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
