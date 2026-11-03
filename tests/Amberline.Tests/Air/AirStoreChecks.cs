using System.Text.Json;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;

namespace Amberline.Tests.Air;

public static class AirStoreChecks
{
    [Check("server.air-store-catalog-contract")]
    public static void CatalogUsesConfiguredPricesAndStableStockPaths()
    {
        var root = TemporaryDirectory("league-air-store-catalog");
        try
        {
            var rules = new PlatformEconomyRules
            {
                MaximumRunePages = 4,
                Prices = new Dictionary<string, PlatformStorePrice>(StringComparer.OrdinalIgnoreCase)
                {
                    ["champion:1"] = new(777, null, true),
                },
            };
            var catalog = new PlatformStoreCatalog(rules, root);
            TestAssert.Equal("champion:1", PlatformStore.StockProductId("Champion", "champions_1"));
            TestAssert.Equal("skin:53001", PlatformStore.StockProductId("Skin", "championsskin_53001.jpg"));
            TestAssert.Equal<int?>(777, catalog.GetChampion(1).IpPrice);
            TestAssert.True(catalog.GetChampion(1).PriceHistoricallyVerified);
            TestAssert.Equal("SPELL_BOOK_PAGE", catalog.GetRunePage(3).Kind);
            var bundle = catalog.Get(PlatformStoreCatalog.RunePageBundleId);
            TestAssert.Equal("BUNDLE", bundle.Kind);
            TestAssert.Equal("7 Rune Page Bundle", bundle.Name);
            TestAssert.Throws<ArgumentException>(() => catalog.Get("champion:999999"));
            if (AirStoreSalesChecks.Without2011Store()) return;
            TestAssert.Equal<int?>(2600, bundle.RpPrice);
            TestAssert.True(bundle.PriceHistoricallyVerified);
        }
        finally { DeleteDirectory(root); }
    }


    [Check("server.air-store-lists-all-champions-refuses-locked-and-applies-sale")]
    public static void StoreListsEveryChampionRefusesTheUnplayableAndAppliesTheSale()
    {
        var root = TemporaryDirectory("league-air-store-locked");
        try
        {
            // 8 October is inside the 7-10 October 2011 sale window the calendar replays every year.
            using var accounts = new PlatformAccounts(root, root, new FixedTimeProvider(new DateTimeOffset(2026, 10, 8, 19, 0, 0, TimeSpan.Zero)));
            var catalog = accounts.StoreCatalog;
            TestAssert.Equal(ChampionCatalog.All.Count, catalog.Items.Count(item => item.Kind == "CHAMPION"));
            // All 84 champions play since batch 341, so every one is listed unlocked (Xerath included).
            TestAssert.Equal(ChampionCatalog.All.Count, catalog.Items.Count(item => item.Kind == "CHAMPION" && !item.Locked), "all 84 are listed unlocked");
            var janna = catalog.GetChampion(40);
            TestAssert.False(janna.Locked);
            var buyer = accounts.RegisterPublic("lockedbuyer", "secret-password", "Locked Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 0, 5_000, Guid.NewGuid());
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer.Id, "bundle:champions-bundle", "RP", Guid.NewGuid()));
            if (AirStoreSalesChecks.Without2011Store()) return;
            TestAssert.Equal(50, janna.SalePercent);
            TestAssert.Equal<int?>(585, janna.RegularRpPrice);
            TestAssert.Equal<int?>(292, janna.RpPrice);
            TestAssert.Equal(292L, accounts.Purchase(buyer.Id, "champion:40", "RP", Guid.NewGuid()).Price);
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    [Check("server.air-store-search-sort-filter-pagination")]
    public static void StorePresentationPreservesSearchFiltersAndScrollsAllResults()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = TemporaryDirectory("league-air-store-presentation");
        try
        {
            var skinRoot = Path.Combine(root, "assets", "storeImages", "content", "skins");
            Directory.CreateDirectory(skinRoot);
            // Art for the six Annie skins; the store lists the ones that were for sale in October 2011 (not Goth Annie, a Collector's Edition skin).
            for (var skin = 1001; skin <= 1006; skin++)
                File.WriteAllBytes(Path.Combine(skinRoot, $"championsskin_{skin}.jpg"), [1]);

            using var accounts = new PlatformAccounts(root, root);
            var account = accounts.RegisterPublic("storepresentation", "secret-password", "Store Presentation", maxed: false);
            var access = new PlatformSessions.StoreSession("opaque-session", account.Id, "form-token",
                DateTimeOffset.UtcNow.AddMinutes(5));

            var sortedChampions = PlatformStore.RenderHome(access, accounts, "CHAMPION", 1, null, false, "ip");
            var annieIndex = sortedChampions.IndexOf(">Annie<", StringComparison.Ordinal);
            var jannaIndex = sortedChampions.IndexOf(">Janna<", StringComparison.Ordinal);
            TestAssert.True(annieIndex >= 0 && jannaIndex > annieIndex);

            var searchedChampion = PlatformStore.RenderHome(access, accounts, "CHAMPION", 1, "Annie", false, "name");
            TestAssert.True(searchedChampion.Contains(">Annie<", StringComparison.Ordinal));
            TestAssert.False(searchedChampion.Contains(">Janna<", StringComparison.Ordinal));

            var assassinChampions = PlatformStore.RenderHome(access, accounts, "CHAMPION", 1, null, false, "name",
                null, null, null, "assassin");
            TestAssert.True(assassinChampions.Contains(">Lee Sin<", StringComparison.Ordinal), "stock assassin tag");
            TestAssert.False(assassinChampions.Contains(">Nidalee<", StringComparison.Ordinal), "Nidalee is not an assassin in stock data");
            var supportChampions = PlatformStore.RenderHome(access, accounts, "CHAMPION", 1, null, false, "name",
                null, null, null, "support");
            TestAssert.True(supportChampions.Contains(">Nidalee<", StringComparison.Ordinal), "stock support tag");
            TestAssert.False(supportChampions.Contains(">Lee Sin<", StringComparison.Ordinal), "Lee Sin is not a support in stock data");

            accounts.GrantCurrency(account.Id, 1000, 0, Guid.NewGuid());
            accounts.Purchase(account.Id, "champion:1", "IP", Guid.NewGuid());
            var notOwnedPurchased = PlatformStore.RenderHome(access, accounts, "CHAMPION", 1, "Annie", true, "name");
            TestAssert.False(notOwnedPurchased.Contains(">Annie<", StringComparison.Ordinal));

            var pageTwo = PlatformStore.RenderHome(access, accounts, "CHAMPION_SKIN", 2, "Annie", true, "rp");
            TestAssert.True(pageTwo.Contains("name=sort value=rp checked", StringComparison.Ordinal));
            TestAssert.False(pageTwo.Contains("class=pagination", StringComparison.Ordinal));
            TestAssert.True(pageTwo.Contains("class=catalog-scroll", StringComparison.Ordinal));
            TestAssert.True(pageTwo.Contains("class=\"products skin-products\"", StringComparison.Ordinal));
            TestAssert.True(pageTwo.Contains("championsskin_1006.jpg", StringComparison.Ordinal));
            TestAssert.False(pageTwo.Contains("championsskin_1001.jpg", StringComparison.Ordinal), "Goth Annie was never sold in the store");

            var escaped = PlatformStore.RenderHome(access, accounts, "CHAMPION", 1, "<script>", false, "name");
            TestAssert.True(escaped.Contains("value=\"&lt;script&gt;\"", StringComparison.Ordinal));
            TestAssert.False(escaped.Contains("value=\"<script>\"", StringComparison.Ordinal));

            var allChampions = System.Net.WebUtility.UrlDecode(PlatformStore.RenderHome(access, accounts, "CHAMPION"));
            foreach (var item in accounts.StoreCatalog.Items.Where(item => item.Kind == "CHAMPION"))
                TestAssert.True(allChampions.Contains(item.Id, StringComparison.Ordinal), $"Missing reachable store item {item.Id}.");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-store-rune-search-and-private-history")]
    public static void RuneSearchUsesShippedEffectsAndHistoryUsesOnlyAccountReceipts()
    {
        var root = TemporaryDirectory("league-air-store-history");
        long buyerId;
        try
        {
            using (var accounts = new PlatformAccounts(root, root))
            {
                var buyer = accounts.RegisterPublic("storebuyer", "secret-password", "<Buyer>", maxed: false);
                var other = accounts.RegisterPublic("storeother", "secret-password", "Other Buyer", maxed: false);
                buyerId = buyer.Id;
                var access = new PlatformSessions.StoreSession("opaque-buyer", buyer.Id, "form-token",
                    DateTimeOffset.UtcNow.AddMinutes(5));
                var otherAccess = new PlatformSessions.StoreSession("opaque-other", other.Id, "other-form-token",
                    DateTimeOffset.UtcNow.AddMinutes(5));
                var runeItem = accounts.StoreCatalog.Items.First(item => item.Kind == "RUNE");
                var rune = accounts.StoreCatalog.RuneContent(runeItem.ContentId)!;
                var effect = rune.Modifiers.Select(modifier => modifier.Name).FirstOrDefault() ??
                    rune.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries).First();
                var filtered = PlatformStore.RenderHome(access, accounts, "RUNE", 1, effect, false, "name",
                    rune.Color.ToString(), (int)rune.Tier, "IP");
                TestAssert.True(filtered.Contains(runeItem.Name, StringComparison.Ordinal), "rune effect search");
                TestAssert.True(filtered.Contains($"name=runeType value={rune.Color} checked", StringComparison.Ordinal),
                    "the exclusive rune type control reflects the active filter");
                TestAssert.True(filtered.Contains($"name=runeTier", StringComparison.Ordinal), "rune tier filter");
                var unavailableCurrency = PlatformStore.RenderHome(access, accounts, "RUNE", 1, null, false, "name",
                    null, null, "RP");
                TestAssert.True(unavailableCurrency.Contains("No products match these filters", StringComparison.Ordinal),
                    "rune currency availability filter");

                accounts.GrantCurrency(buyer.Id, 1_000, 0, Guid.NewGuid());
                accounts.Purchase(buyer.Id, "champion:1", "IP", Guid.NewGuid());
                var history = PlatformStore.RenderPurchases(access, accounts);
                TestAssert.True(history.Contains("Annie", StringComparison.Ordinal));
                TestAssert.True(history.Contains("Date (UTC)", StringComparison.Ordinal));
                var privateHistory = PlatformStore.RenderPurchases(otherAccess, accounts);
                TestAssert.False(privateHistory.Contains("Annie", StringComparison.Ordinal), "purchase history is account scoped");
            }

            var statePath = Path.Combine(root, "accounts.json");
            var state = JsonSerializer.Deserialize<PlatformAccounts.State>(File.ReadAllText(statePath), MatchRoster.JsonOptions)!;
            var savedBuyer = state.Accounts.Single(account => account.Id == buyerId);
            var unknown = new PlatformPurchase(Guid.NewGuid(), "legacy:<evil>&id", "IP", 17,
                DateTimeOffset.UtcNow.AddMinutes(1));
            var updatedBuyer = savedBuyer with { Economy = savedBuyer.Economy! with
            {
                Purchases = [.. (savedBuyer.Economy.Purchases ?? []), unknown],
            }};
            File.WriteAllText(statePath, JsonSerializer.Serialize(state with
            {
                Accounts = state.Accounts.Select(account => account.Id == buyerId ? updatedBuyer : account).ToArray(),
            }, MatchRoster.JsonOptions));

            using var reopened = new PlatformAccounts(root, root);
            var reopenedAccess = new PlatformSessions.StoreSession("opaque-buyer", buyerId, "form-token",
                DateTimeOffset.UtcNow.AddMinutes(5));
            var withFallback = PlatformStore.RenderPurchases(reopenedAccess, reopened);
            TestAssert.True(withFallback.Contains("Catalog item unavailable (legacy:&lt;evil&gt;&amp;id)", StringComparison.Ordinal),
                "unknown catalog IDs are escaped and labeled");
            TestAssert.True(withFallback.IndexOf("legacy:&lt;evil&gt;", StringComparison.Ordinal) <
                withFallback.IndexOf("Annie", StringComparison.Ordinal), "history is newest first");
        }
        finally { DeleteDirectory(root); }
    }
}
