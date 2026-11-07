using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>
/// Owner decisions of 18 October 2026 (evening): the AIR rune catalog offers only the regular runes a normal player could buy in
/// October 2011 (event, promotional and unknown runes are hidden), and the rune tier gate (tier 1 from level 1, tier 2 from 10,
/// tier 3 from 20) is enforced by the server from the account's own level, on purchase and on every page save.
/// Owner decision of 18 October 2026 (night): the five Harrowing 2011 quintessences (8013-8015, 8017, 8018; Speedy Specter 8016 was
/// 2010 only) are sold only inside their real window, 19 to 31 October by the Pacific calendar, on the same clock and yearly loop
/// as the store sales, at the retail IP prices a 2011 capture printed.
/// </summary>
public static class AirRuneTierChecks
{
    private static readonly Lazy<RuneCatalog> Catalog = new(() => new RuneCatalog());

    // Mid-day UTC on the days around the Harrowing window (19-31 October, Pacific days): safe from the Pacific midnight whichever
    // offset the host's time-zone database gives.
    private static readonly DateTimeOffset OutsideWindow = new(2026, 7, 15, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayBefore = new(2026, 10, 18, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstDay = new(2026, 10, 19, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastDay = new(2026, 10, 31, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayAfter = new(2026, 11, 1, 19, 0, 0, TimeSpan.Zero);
    private static readonly int[] Harrowing = [8013, 8014, 8015, 8017, 8018];

    // The League wiki's Quintessence page of 25 October 2011 (Wayback 20111025142719): Headless Horseman, Piercing Screech and Witches Brew
    // 495; Bountiful Treats and Elusive Werewolf 995. Speedy Specter (8016, 995) was "Released for the Harrowing of 2010 only".
    private static readonly Dictionary<int, int> HarrowingIp = new() { [8013] = 495, [8014] = 495, [8015] = 995, [8017] = 495, [8018] = 995 };

    private static int Rune(RuneTier tier, RuneColor color = RuneColor.Red) =>
        Catalog.Value.All.First(rune => rune.Tier == tier && rune.Color == color).Id;

    [Check("server.air-rune-hidden-promotional-cannot-be-bought-or-equipped")]
    public static void EventPromotionalAndUnknownRunesAreHiddenFromTheStoreInventoryAndPages()
    {
        var root = TemporaryDirectory("league-air-rune-hidden");
        try
        {
            // July: outside the Harrowing window, so the Harrowing runes are hidden like the other event runes.
            using var accounts = new PlatformAccounts(root, root, new MovableClock(OutsideWindow));
            var fresh = accounts.RegisterPublic("hiddenbuyer", "testing-password", "Hidden Buyer", maxed: false);
            accounts.GrantCurrency(fresh.Id, 100_000, 100_000, Guid.NewGuid());
            var catalog = accounts.StoreCatalog;

            // The Razer runes (10001-10002), the 34 event runes (8001-8034), the 5000 placeholder and 5378 are no products.
            TestAssert.Equal(374, catalog.Items.Count(item => item.Kind == "RUNE"));
            TestAssert.True(catalog.Items.Where(item => item.Kind == "RUNE").All(item => item.ContentId is >= 5001 and <= 5374));
            foreach (var hidden in new[] { 8001, 8034, 10001, 10002, 5000, 5378, 8013, 8018 })
            {
                TestAssert.False(catalog.TryGet("rune:" + hidden, out _), $"rune {hidden} is not a store product");
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(fresh.Id, "rune:" + hidden, "IP", Guid.NewGuid()));
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(fresh.Id, "rune:" + hidden, "RP", Guid.NewGuid()));
            }
            TestAssert.True(PlatformEconomy.For(accounts.Get(fresh.Id)).OwnedRunes!.Count == 0, "nothing was granted");
            TestAssert.Equal(100_000L, PlatformEconomy.For(accounts.Get(fresh.Id)).Ip, "nothing was charged");

            // The account's rune inventory (a fully unlocked account owns every regular rune) lists no hidden rune either.
            var maxed = accounts.RegisterPublic("hiddenmaxed", "testing-password", "Hidden Maxed");
            var inventory = AirDtos.RuneInventory(maxed);
            var ids = ((ArrayCollection)inventory["summonerRunes"]).Cast<AsObject>().Select(rune => Convert.ToInt32(rune["runeId"])).ToArray();
            TestAssert.Equal(374, ids.Length);
            TestAssert.True(ids.All(id => id is >= 5001 and <= 5374));

            // Not even an account that owns everything can equip one: the ownership shortcut never reaches hidden records.
            var pages = accounts.GetRunePages(maxed.Id);
            TestAssert.Equal(RuneColor.Red, Catalog.Value.Get(8001)!.Color);
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(maxed.Id, maxed.SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = 8001 } })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(maxed.Id, maxed.SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = 10001 } })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(maxed.Id, maxed.SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [28] = 5378 } })));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveLoadout(maxed.Id,
                accounts.Get(maxed.Id).Loadout with { RedRunes = [8001] }));
            TestAssert.True(accounts.Get(maxed.Id).Loadout.RedRunes is null or { Length: 0 }, "a refused save changes nothing");
            // A regular rune is still fine.
            accounts.SaveRunePages(maxed.Id, maxed.SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = Rune(RuneTier.Three) } }));
        }
        finally { DeleteDirectory(root); }
    }


    [Check("server.air-rune-harrowing-sold-only-inside-its-window")]
    public static void HarrowingRunesAreProductsOnlyInsideTheWindowAndTheServerRefusesThemOutside()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = TemporaryDirectory("league-air-harrowing");
        try
        {
            var clock = new MovableClock(DayBefore);
            // The gate follows the shipped tier of the record: the purchase needs RunePage.MinimumLevel(tier); level 10 or more also opens a black slot.
            var need = RunePage.MinimumLevel(Catalog.Value.Get(8013)!.Tier);
            var buyer = Seed(root, "harrowbuyer", level: Math.Max(need, 10), ip: 5_000);
            var young = Seed(root, "harrowyoung", level: Math.Max(need - 1, 1), ip: 5_000);
            var price8013 = HarrowingIp[8013];
            using var accounts = new PlatformAccounts(root, root, clock);
            var catalog = accounts.StoreCatalog;

            // The day before: not products, not purchasable, nothing charged (the server refuses, whatever the request says).
            TestAssert.Equal(374, catalog.Items.Count(item => item.Kind == "RUNE"));
            foreach (var id in Harrowing)
            {
                TestAssert.False(catalog.TryGet("rune:" + id, out _), $"rune {id} is not a product before the window");
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer, "rune:" + id, "IP", Guid.NewGuid()));
                TestAssert.Throws<ArgumentException>(() => catalog.GetRune(id));
            }
            TestAssert.Equal(5_000L, PlatformEconomy.For(accounts.Get(buyer)).Ip, "nothing was charged");
            TestAssert.Throws<ArgumentException>(() => PlatformStore.RenderProduct(SessionFor(accounts.Get(buyer)), accounts, "rune:8013"),
                "the unlock dialog of a closed rune is an unknown product");

            // First day: exactly the five are on offer, the other event runes (and the 2010-only Speedy Specter) are not.
            clock.Current = FirstDay;
            var runes = catalog.Items.Where(item => item.Kind == "RUNE").ToArray();
            TestAssert.Equal(379, runes.Length);
            TestAssert.Equal(Enumerable.Range(5001, 374).Concat(Harrowing).ToArray(), runes.Select(item => item.ContentId).Order().ToArray());
            var item8013 = catalog.GetRune(8013);
            TestAssert.Equal(need, item8013.MinimumLevel);
            // The server prices them with the retail IP prices of the 2011 capture; nothing in a request sets one.
            foreach (var id in Harrowing)
            {
                var offered = catalog.GetRune(id);
                TestAssert.Equal(HarrowingIp[id], offered.IpPrice!.Value, $"rune {id} sells at its retail IP price");
                TestAssert.True(offered.RpPrice is null, "runes sell for IP only");
                TestAssert.True(offered.PriceHistoricallyVerified);
            }
            TestAssert.Equal(3, catalog.GetRuneQuantityLimit(8013), "a quintessence is limited to three copies");
            foreach (var hidden in new[] { 8001, 8012, 8016, 8019, 8034, 10001, 10002 })
            {
                TestAssert.False(catalog.TryGet("rune:" + hidden, out _), $"rune {hidden} stays hidden inside the window");
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer, "rune:" + hidden, "IP", Guid.NewGuid()));
            }
            var dialog = PlatformStore.RenderProduct(SessionFor(accounts.Get(buyer)), accounts, "rune:8013");
            TestAssert.True(dialog.Contains("name=itemId value=\"rune:8013\"", StringComparison.Ordinal), "the dialog sells it inside the window");
            var list = PlatformStore.RenderHome(SessionFor(accounts.Get(buyer)), accounts, "RUNE");
            TestAssert.True(list.Contains("Quintessence of the Headless Horseman", StringComparison.Ordinal), "the Runes tab lists it inside the window");

            // The tier gate stays: the account one level short is refused, with nothing charged.
            if (need > 1)
            {
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(young, "rune:8013", "IP", Guid.NewGuid()));
                TestAssert.Equal(5_000L, PlatformEconomy.For(accounts.Get(young)).Ip);
            }

            // The last day still sells; a replayed request id is not charged twice.
            clock.Current = LastDay;
            var requestId = Guid.NewGuid();
            var receipt = accounts.Purchase(buyer, "rune:8013", "IP", requestId);
            TestAssert.Equal(1, receipt.Quantity);
            TestAssert.Equal(5_000L - price8013, PlatformEconomy.For(accounts.Get(buyer)).Ip, "charged the retail price of 8013");
            TestAssert.Equal((long)price8013, (long)receipt.Price);
            TestAssert.Equal(receipt.RequestId, accounts.Purchase(buyer, "rune:8013", "IP", requestId).RequestId);
            TestAssert.Equal(5_000L - price8013, PlatformEconomy.For(accounts.Get(buyer)).Ip, "a replay is not charged again");
            TestAssert.Equal(1, PlatformEconomy.For(accounts.Get(buyer)).OwnedRunes![8013]);

            // The day after: closed again for everybody, server-side.
            clock.Current = DayAfter;
            TestAssert.Equal(374, catalog.Items.Count(item => item.Kind == "RUNE"));
            TestAssert.False(catalog.TryGet("rune:8013", out _));
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer, "rune:8013", "IP", Guid.NewGuid()),
                "a second copy cannot be bought after the window");
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer, "rune:8014", "IP", Guid.NewGuid()));
            TestAssert.Equal(1, PlatformEconomy.For(accounts.Get(buyer)).OwnedRunes![8013], "the bought copy stays owned");

            // Reconstruction: the bought copy stays listed, equippable and named on its receipt; only the sale is windowed.
            var inventory = AirDtos.RuneInventory(accounts.Get(buyer));
            var owned = ((ArrayCollection)inventory["summonerRunes"]).Cast<AsObject>().Single(rune => Convert.ToInt32(rune["runeId"]) == 8013);
            TestAssert.Equal(1, Convert.ToInt32(owned["quantity"]));
            var pages = accounts.GetRunePages(buyer);
            accounts.SaveRunePages(buyer, accounts.Get(buyer).SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [28] = 8013 } }));
            TestAssert.Equal(8013, accounts.Get(buyer).Loadout.BlackRunes![0]);
            var history = PlatformStore.RenderPurchases(SessionFor(accounts.Get(buyer)), accounts);
            TestAssert.True(history.Contains("Quintessence of the Headless Horseman", StringComparison.Ordinal));
            TestAssert.False(history.Contains("Catalog item unavailable", StringComparison.Ordinal));
            // A rune the account never bought cannot be equipped, in or out of the window.
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(buyer, accounts.Get(buyer).SummonerId,
                AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [28] = 8014 } })));
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-harrowing-is-not-part-of-the-all-unlocked-grant")]
    public static void AnAllUnlockedAccountMustBuyAHarrowingRuneAndCannotEquipOneItDoesNotHave()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = TemporaryDirectory("league-air-harrowing-maxed");
        try
        {
            var clock = new MovableClock(DayBefore);
            using var accounts = new PlatformAccounts(root, root, clock);
            var maxed = accounts.RegisterPublic("harrowmaxed", "testing-password", "Harrow Maxed");
            accounts.GrantCurrency(maxed.Id, 100_000, 0, Guid.NewGuid());
            var pages = accounts.GetRunePages(maxed.Id);
            PlatformRunePage[] WithHarrowing() => AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [28] = 8013 } });

            // The ownership shortcut of a fully unlocked account covers the 374 regular runes only.
            TestAssert.Equal(374, ((ArrayCollection)AirDtos.RuneInventory(accounts.Get(maxed.Id))["summonerRunes"]).Count);
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(maxed.Id, maxed.SummonerId, WithHarrowing()));
            clock.Current = FirstDay;
            TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(maxed.Id, maxed.SummonerId, WithHarrowing()),
                "an open window does not grant the rune either");
            TestAssert.Throws<ArgumentException>(() => accounts.SaveLoadout(maxed.Id, accounts.Get(maxed.Id).Loadout with { BlackRunes = [8013] }));

            // It sells to this account like to any other (the "already owns it" shortcut covers the regular runes only).
            var access = SessionFor(accounts.Get(maxed.Id));
            var before = PlatformStore.RenderProduct(access, accounts, "rune:8013");
            TestAssert.True(before.Contains("name=currency value=\"IP\"", StringComparison.Ordinal), "the dialog offers the purchase");
            accounts.Purchase(maxed.Id, "rune:8013", "IP", Guid.NewGuid());
            var ids = ((ArrayCollection)AirDtos.RuneInventory(accounts.Get(maxed.Id))["summonerRunes"]).Cast<AsObject>()
                .Select(rune => Convert.ToInt32(rune["runeId"])).ToArray();
            TestAssert.Equal(375, ids.Length);
            TestAssert.False(ids.Contains(8016), "Speedy Specter is not sold in 2011");
            TestAssert.True(ids.Contains(8013));
            accounts.SaveRunePages(maxed.Id, maxed.SummonerId, WithHarrowing());
            TestAssert.Equal(8013, accounts.Get(maxed.Id).Loadout.BlackRunes![0]);
            // Two more copies fit the quintessence limit of three; the fourth is refused.
            accounts.Purchase(maxed.Id, "rune:8013", "IP", Guid.NewGuid(), 2);
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(maxed.Id, "rune:8013", "IP", Guid.NewGuid()));
            // The other event runes are still no part of anything, nor is the 2010-only Speedy Specter.
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(maxed.Id, "rune:8019", "IP", Guid.NewGuid()));
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(maxed.Id, "rune:8016", "IP", Guid.NewGuid()));
        }
        finally { DeleteDirectory(root); }
    }

    private static PlatformSessions.StoreSession SessionFor(PlatformAccount account) =>
        new("opaque", account.Id, "form-token", DateTimeOffset.UtcNow.AddMinutes(10));

    private sealed class MovableClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    [Check("server.air-rune-tier-two-needs-level-ten")]
    public static void TierTwoIsRefusedAtLevelNineAndAllowedAtLevelTenForPurchaseAndPageSave()
    {
        var tierTwo = Rune(RuneTier.Two);
        var root = TemporaryDirectory("league-air-rune-tier-two");
        try
        {
            var id = Seed(root, "tiertwo", level: 9, ip: 5_000);
            using (var accounts = new PlatformAccounts(root, root))
            {
                var item = accounts.StoreCatalog.GetRune(tierTwo);
                TestAssert.Equal(10, item.MinimumLevel);
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(id, "rune:" + tierTwo, "IP", Guid.NewGuid()),
                    "level 9 cannot buy a tier 2 rune");
                TestAssert.Equal(5_000L, PlatformEconomy.For(accounts.Get(id)).Ip, "the refusal charged nothing");
                accounts.Purchase(id, "rune:" + Rune(RuneTier.One), "IP", Guid.NewGuid());
            }

            // The same account at level 10 (the level is the account's own state, nothing a request carries).
            SetLevel(root, id, 10);
            using (var accounts = new PlatformAccounts(root, root))
            {
                accounts.Purchase(id, "rune:" + tierTwo, "IP", Guid.NewGuid());
                var pages = accounts.GetRunePages(id);
                accounts.SaveRunePages(id, accounts.Get(id).SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = tierTwo } }));
                TestAssert.Equal(tierTwo, accounts.Get(id).Loadout.RedRunes![0]);
            }
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-tier-three-needs-level-twenty")]
    public static void TierThreeIsRefusedAtLevelNineteenAndAllowedAtLevelTwenty()
    {
        var tierThree = Rune(RuneTier.Three);
        var root = TemporaryDirectory("league-air-rune-tier-three");
        try
        {
            var id = Seed(root, "tierthree", level: 19, ip: 10_000);
            using (var accounts = new PlatformAccounts(root, root))
            {
                TestAssert.Equal(20, accounts.StoreCatalog.GetRune(tierThree).MinimumLevel);
                TestAssert.Throws<ArgumentException>(() => accounts.Purchase(id, "rune:" + tierThree, "IP", Guid.NewGuid()),
                    "level 19 cannot buy a tier 3 rune");
                // Tier 2 is open at 19, tier 3 is not for a page either.
                var tierTwo = Rune(RuneTier.Two);
                accounts.Purchase(id, "rune:" + tierTwo, "IP", Guid.NewGuid());
                var pages = accounts.GetRunePages(id);
                accounts.SaveRunePages(id, accounts.Get(id).SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = tierTwo } }));
                TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(id, accounts.Get(id).SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = tierThree } })));
            }

            SetLevel(root, id, 20);
            using (var accounts = new PlatformAccounts(root, root))
            {
                accounts.Purchase(id, "rune:" + tierThree, "IP", Guid.NewGuid());
                var pages = accounts.GetRunePages(id);
                accounts.SaveRunePages(id, accounts.Get(id).SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = tierThree } }));
                TestAssert.Equal(tierThree, accounts.Get(id).Loadout.RedRunes![0]);
            }
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-rune-page-save-refuses-over-tier-runes")]
    public static void AnOverTierRuneStaysOwnedButASaveThatEquipsItIsRefusedUntilTheLevelIsReached()
    {
        var tierTwo = Rune(RuneTier.Two);
        var tierOne = Rune(RuneTier.One);
        var root = TemporaryDirectory("league-air-rune-over-tier");
        try
        {
            // An account at level 9 that already owns a tier 2 rune and has it equipped (an account from before the gate).
            var id = Seed(root, "overtier", level: 9, ip: 0, owned: new Dictionary<int, int> { [tierTwo] = 1, [tierOne] = 1 },
                equip: tierTwo);
            using (var accounts = new PlatformAccounts(root, root))
            {
                var account = accounts.Get(id);
                var pages = accounts.GetRunePages(id);
                TestAssert.Equal(1, PlatformEconomy.For(account).OwnedRunes![tierTwo], "the over-tier rune stays owned");
                TestAssert.Equal(tierTwo, pages[0].Slots[1]);

                // A page save that equips it is refused, with nothing changed.
                var before = JsonSerializer.Serialize(accounts.GetRunePages(id), MatchRoster.JsonOptions);
                TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(id, account.SummonerId, pages));
                TestAssert.Throws<ArgumentException>(() => accounts.SaveRunePages(id, account.SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Name = "renamed" }, pages[1] with { Slots = new Dictionary<int, int> { [1] = tierTwo } })));
                TestAssert.Equal(before, JsonSerializer.Serialize(accounts.GetRunePages(id), MatchRoster.JsonOptions));

                // A loadout save that changes the runes to it is refused too; one that leaves the runes alone is not.
                TestAssert.Throws<ArgumentException>(() => accounts.SaveLoadout(id,
                    account.Loadout with { RedRunes = [tierTwo, tierTwo] }));
                accounts.SaveLoadout(id, account.Loadout with { SummonerSpell1 = "SummonerHaste", SummonerSpell2 = "SummonerHeal" });

                // Taking it off the page (and equipping a tier 1 rune) is allowed, and so is switching pages afterwards.
                accounts.SaveRunePages(id, account.SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = tierOne } }));
                TestAssert.Equal(tierOne, accounts.Get(id).Loadout.RedRunes![0]);
                TestAssert.Equal(1, PlatformEconomy.For(accounts.Get(id)).OwnedRunes![tierTwo], "still owned");
            }

            // Switching to a page that holds an over-tier rune is an equip and is refused; the other page is fine.
            var switchRoot = TemporaryDirectory("league-air-rune-over-tier-select");
            try
            {
                var switcher = Seed(switchRoot, "overselect", level: 9, ip: 0, owned: new Dictionary<int, int> { [tierTwo] = 1 },
                    equip: tierTwo, equipOnSecondPage: true);
                using var accounts = new PlatformAccounts(switchRoot, switchRoot);
                TestAssert.Throws<ArgumentException>(() => accounts.SelectRunePage(switcher, accounts.Get(switcher).SummonerId, 2));
                TestAssert.Equal(1L, accounts.GetRunePages(switcher).Single(page => page.IsCurrent).PageId);
            }
            finally { DeleteDirectory(switchRoot); }

            // At level 10 the same equip is accepted.
            SetLevel(root, id, 10);
            using (var accounts = new PlatformAccounts(root, root))
            {
                var pages = accounts.GetRunePages(id);
                accounts.SaveRunePages(id, accounts.Get(id).SummonerId,
                    AirRuneAccountChecks.Book(pages, pages[0] with { Slots = new Dictionary<int, int> { [1] = tierTwo } }));
                TestAssert.Equal(tierTwo, accounts.Get(id).Loadout.RedRunes![0]);
            }
        }
        finally { DeleteDirectory(root); }
    }

    // The account's level and what it holds are state written before the platform opens, like a result or an operator edit.
    private static long Seed(string root, string username, int level, long ip, Dictionary<int, int>? owned = null, int equip = 0,
        bool equipOnSecondPage = false)
    {
        long id;
        using (var accounts = new PlatformAccounts(root, root))
            id = accounts.RegisterPublic(username, "testing-password", username + " name", maxed: false).Id;
        var path = Path.Combine(root, "accounts.json");
        var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
        PlatformAccount Change(PlatformAccount account)
        {
            var pages = account.RunePages ?? [];
            if (equip != 0)
                pages = pages.Select(page => page.PageId == (equipOnSecondPage ? 2 : 1)
                    ? page with { Slots = new Dictionary<int, int> { [1] = equip } } : page).ToArray();
            var loadout = account.Loadout with { SummonerLevel = level };
            if (equip != 0 && !equipOnSecondPage) loadout = loadout with { RedRunes = [equip] };
            return account with
            {
                Loadout = loadout, RunePages = pages,
                Economy = account.Economy! with { Level = level, Ip = ip, OwnedRunes = owned ?? new Dictionary<int, int>() },
            };
        }
        File.WriteAllText(path, JsonSerializer.Serialize(state with
        {
            Accounts = state.Accounts.Select(account => account.Id == id ? Change(account) : account).ToArray(),
        }, MatchRoster.JsonOptions));
        return id;
    }

    private static void SetLevel(string root, long id, int level)
    {
        var path = Path.Combine(root, "accounts.json");
        var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(path), MatchRoster.JsonOptions)!;
        File.WriteAllText(path, JsonSerializer.Serialize(state with
        {
            Accounts = state.Accounts.Select(account => account.Id == id
                ? account with { Loadout = account.Loadout with { SummonerLevel = level }, Economy = account.Economy! with { Level = level } }
                : account).ToArray(),
        }, MatchRoster.JsonOptions));
    }

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
