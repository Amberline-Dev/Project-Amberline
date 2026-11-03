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
            var account = new PlatformAccount(1, 1_000_001, "legacy", "Legacy", "", "",
                new MatchPlayer("Legacy", "Annie", Team.Order, RedRunes: [red]));
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
                var saved = new[]
                {
                    pages[0] with
                    {
                        Name = "  Holey  ",
                        Slots = new Dictionary<int, int> { [2] = red, [19] = blue },
                    },
                    pages[1] with { Name = "Spare", Slots = new Dictionary<int, int>() },
                };
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
            accounts.SaveRunePages(account.Id, account.SummonerId,
            [
                pages[0] with { Slots = new Dictionary<int, int>() },
                pages[1] with
                {
                    Name = "Second",
                    IsCurrent = false,
                    Slots = new Dictionary<int, int> { [10] = yellow },
                },
            ]);
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
                [original[0] with { Slots = new Dictionary<int, int> { [1] = blue } }, original[1]]));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                [original[0] with { PageId = original[1].PageId }, original[1]]));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                [original[0] with { IsCurrent = false }, original[1] with { IsCurrent = false }]));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                [original[0] with { Name = "@@!PaG3!@@" }, original[1]]));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                [original[0] with { Name = "same" }, original[1] with { Name = " SAME " }]));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                [original[0] with { Name = new string('x', 129) }, original[1]]));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(account.Id, account.SummonerId,
                [original[0] with { Name = "bad\u0001name" }, original[1]]));

            TestAssert.Equal(originalBook, Snapshot(accounts.GetRunePages(account.Id)),
                "a failed whole-book save must not alter pages");
            var redRunes = accounts.Get(account.Id).Loadout.RedRunes;
            TestAssert.True(redRunes is null || redRunes.Length == 0);
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
