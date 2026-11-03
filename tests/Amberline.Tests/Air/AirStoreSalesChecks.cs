using System.Net;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Amberline.Tests.Air;

/// <summary>
/// The store sale calendar (Riot's real September-December 2011 sales replayed by month and day) and the RP purchase path
/// that charges its prices. The clock is injected; a sale price is only ever derived from it.
/// </summary>
public static class AirStoreSalesChecks
{
    // 2026-10-08 is inside the 7-10 October window (champions Vayne, Janna, Shen; Acolyte Lee Sin and four more skins).
    private static readonly DateTimeOffset InSale = new(2026, 10, 8, 19, 0, 0, TimeSpan.Zero);

    // Batch 597: the 2011 calendar and price list are operator content (content/store); these checks need them. Without them the
    // neutral store applies: no sale and no rune window, ever.
    internal static bool Without2011Store()
    {
        if (PlatformContent.Has("store/sales.json") && PlatformContent.Has("store/prices.json")) return false;
        TestAssert.Equal(0, PlatformStoreCalendar.Default.Entries.Count + PlatformStoreCalendar.Default.RuneWindows.Count, "the neutral calendar");
        var root = TemporaryDirectory("league-air-store-neutral");
        try
        {
            using var accounts = new PlatformAccounts(root, root, new FixedTimeProvider(InSale));
            TestAssert.False(accounts.StoreCatalog.ActiveSales().Any() || accounts.StoreCatalog.Items.Any(item => item.SalePercent > 0), "no sale");
        }
        finally { DeleteDirectory(root); }
        return true;
    }


    [Check("server.air-store-sale-calendar-maps-month-and-day")]
    public static void TodayMapsOntoTheYearlyLoopAndNothingIsOnSaleOutsideAWindow()
    {
        if (Without2011Store()) return;
        var calendar = PlatformStoreCalendar.Default;
        string[] ChampionIds(DateTimeOffset now) => calendar.ActiveAt(now)
            .Where(sale => sale.Entry.Kind == "CHAMPION").SelectMany(sale => sale.Entry.Items.Select(item => item.Id)).ToArray();

        // Inside the 7-10 October window, in 2026 and in any other year; the dates are moved into the current year.
        var window = calendar.ActiveAt(InSale).Single(sale => sale.Entry.Kind == "CHAMPION");
        TestAssert.Equal(new DateOnly(2026, 10, 7), window.Starts);
        TestAssert.Equal(new DateOnly(2026, 10, 10), window.Ends);
        TestAssert.Equal("champion:40", window.Entry.Items[1].Id);
        TestAssert.Equal(3, ChampionIds(InSale).Length);
        TestAssert.Equal(new DateOnly(2031, 10, 10), calendar.ActiveAt(new DateTimeOffset(2031, 10, 9, 12, 0, 0, TimeSpan.Zero))
            .Single(sale => sale.Entry.Kind == "CHAMPION").Ends);

        // Both ends are inclusive, and the day is the Pacific-time day: 06:30 UTC on 11 October is still the evening of the 10th.
        TestAssert.Equal(3, ChampionIds(new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.Zero)).Length);
        TestAssert.Equal(3, ChampionIds(new DateTimeOffset(2026, 10, 11, 6, 30, 0, TimeSpan.Zero)).Length);
        TestAssert.Equal(0, ChampionIds(new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.Zero)).Length);
        TestAssert.Equal(0, ChampionIds(new DateTimeOffset(2026, 10, 6, 19, 0, 0, TimeSpan.Zero)).Length);

        // Outside every window nothing is on sale.
        foreach (var quiet in new[] { new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                     new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero) })
        {
            TestAssert.Equal(0, calendar.ActiveAt(quiet).Count, quiet.ToString("u"));
            TestAssert.Equal(0, calendar.PercentsAt(quiet).Count);
        }

        // Windows can overlap (Harrowing sale and the 28-31 October sale): both are live, each with its own items.
        TestAssert.True(calendar.ActiveAt(new DateTimeOffset(2026, 10, 29, 19, 0, 0, TimeSpan.Zero)).Count(sale => sale.Entry.Kind == "CHAMPION") == 2);
        // Black Friday: 75% on the skins and 20% on the champions at the same time.
        var friday = calendar.PercentsAt(new DateTimeOffset(2026, 11, 26, 19, 0, 0, TimeSpan.Zero));
        TestAssert.Equal(75, friday["skin:20001"]);
        TestAssert.Equal(20, friday["champion:20"]);
    }

    [Check("server.air-store-sale-price-follows-the-clock")]
    public static void CatalogPricesAreDerivedFromTheCalendarAndTheClock()
    {
        if (Without2011Store()) return;
        var root = TemporaryDirectory("league-air-store-sale-price");
        try
        {
            AddSkinArt(root, 64002, 64001, 68001, 81001);
            var clock = new FixedTimeProvider(InSale);
            using var accounts = new PlatformAccounts(root, root, clock);
            var catalog = accounts.StoreCatalog;

            var janna = catalog.GetChampion(40);
            TestAssert.Equal(50, janna.SalePercent);
            TestAssert.Equal<int?>(585, janna.RegularRpPrice);
            TestAssert.Equal<int?>(292, janna.RpPrice);
            TestAssert.Equal<int?>(487, catalog.GetChampion(67).RpPrice);
            TestAssert.Equal<int?>(975, catalog.GetChampion(67).RegularRpPrice);
            TestAssert.Equal<int?>(487, catalog.GetSkin(64002).RpPrice);
            TestAssert.Equal(0, catalog.GetSkin(64001).SalePercent);
            TestAssert.Equal(janna.IpPrice, catalog.GetChampion(40).IpPrice, "a sale never touches the IP price");
            TestAssert.Equal(3, catalog.Items.Count(item => item.Kind == "CHAMPION" && item.SalePercent > 0));
            // The posted regular price is the catalog's price (Lee Sin 975 RP: the 2 September sale post), not the 520 default.
            TestAssert.Equal<int?>(975, catalog.GetChampion(64).RpPrice);

            var sales = catalog.ActiveSales();
            TestAssert.Equal(2, sales.Count);
            var championSale = sales.Single(sale => sale.Kind == "CHAMPION");
            TestAssert.Equal("Expires Saturday, October 10th", championSale.Expires);
            TestAssert.True(championSale.Body.Contains("Janna", StringComparison.Ordinal));

            // The same catalog, later: the price follows the clock with nothing rebuilt.
            clock.Current = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
            TestAssert.Equal<int?>(585, catalog.GetChampion(40).RpPrice);
            TestAssert.Equal(0, catalog.GetChampion(40).SalePercent);
            TestAssert.Equal<int?>(null, catalog.GetChampion(40).RegularRpPrice);
            clock.Current = new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero);
            TestAssert.Equal(0, catalog.ActiveSales().Count);
            TestAssert.Equal(0, catalog.Items.Count(item => item.SalePercent > 0));

            // Black Friday rounds down like Riot's posts: a 75% skin and a 20% champion.
            clock.Current = new DateTimeOffset(2026, 11, 26, 19, 0, 0, TimeSpan.Zero);
            var ezreal = catalog.GetChampion(81);
            TestAssert.Equal(20, ezreal.SalePercent);
            TestAssert.Equal(ezreal.RegularRpPrice!.Value * 80 / 100, ezreal.RpPrice!.Value);
            TestAssert.Equal(75, catalog.GetSkin(81001).SalePercent);
            TestAssert.Equal(catalog.GetSkin(81001).RegularRpPrice!.Value * 25 / 100, catalog.GetSkin(81001).RpPrice!.Value);
            TestAssert.Equal("BLACK FRIDAY SKINS SALE", catalog.ActiveSales().Single(sale => sale.Kind == "CHAMPION_SKIN").Title);
        }
        finally { DeleteDirectory(root); }

        // A host price override wins over the sale.
        var overrideRoot = TemporaryDirectory("league-air-store-sale-override");
        try
        {
            var rules = new PlatformEconomyRules
            {
                Prices = new Dictionary<string, PlatformStorePrice>(StringComparer.OrdinalIgnoreCase) { ["champion:40"] = new(null, 700, true) },
            };
            var catalog = new PlatformStoreCatalog(rules, overrideRoot, new FixedTimeProvider(InSale));
            TestAssert.Equal<int?>(700, catalog.GetChampion(40).RpPrice);
            TestAssert.Equal(0, catalog.GetChampion(40).SalePercent);
            TestAssert.Equal(50, catalog.GetChampion(67).SalePercent, "other products stay on sale");
        }
        finally { DeleteDirectory(overrideRoot); }
    }

    [Check("server.air-store-rp-purchase-charges-the-sale-price")]
    public static void RpPurchasesOfChampionsAndSkinsChargeTheServerSidePriceAndNeverDoubleSpend()
    {
        if (Without2011Store()) return;
        var root = TemporaryDirectory("league-air-store-rp-purchase");
        try
        {
            AddSkinArt(root, 64002);
            var clock = new FixedTimeProvider(InSale);
            using var accounts = new PlatformAccounts(root, root, clock);
            var buyer = accounts.RegisterPublic("salebuyer", "testing-password", "Sale Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 0, 2_000, Guid.NewGuid());
            long Rp() => PlatformEconomy.For(accounts.Get(buyer.Id)).Rp;

            // A champion with RP, at the sale price (Janna 585 RP regular, 292 on sale).
            var janna = accounts.Purchase(buyer.Id, "champion:40", "RP", Guid.NewGuid());
            TestAssert.Equal(292L, janna.Price);
            TestAssert.Equal("RP", janna.Currency);
            TestAssert.Equal(2_000L - 292, Rp());
            TestAssert.True(PlatformEconomy.For(accounts.Get(buyer.Id)).OwnedChampions!.Contains(40));

            // A skin on sale needs its champion first (ownership check unchanged), then charges the sale price.
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(buyer.Id, "skin:64002", "RP", Guid.NewGuid()));
            accounts.Purchase(buyer.Id, "champion:64", "RP", Guid.NewGuid());
            TestAssert.Equal(2_000L - 292 - 975, Rp());
            TestAssert.Equal(487L, accounts.Purchase(buyer.Id, "skin:64002", "RP", Guid.NewGuid()).Price);

            // The same product after the window is full price; an owned champion is still refused.
            clock.Current = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
            accounts.GrantCurrency(buyer.Id, 0, 5_000, Guid.NewGuid());
            var before = Rp();
            TestAssert.Equal(975L, accounts.Purchase(buyer.Id, "champion:67", "RP", Guid.NewGuid()).Price);
            TestAssert.Equal(before - 975, Rp());
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(buyer.Id, "champion:40", "RP", Guid.NewGuid()));

            // A replayed request is not charged again (Vayne, 487 RP on sale).
            clock.Current = InSale;
            var replayBuyer = accounts.RegisterPublic("replaybuyer", "testing-password", "Replay Buyer", maxed: false);
            accounts.GrantCurrency(replayBuyer.Id, 0, 2_000, Guid.NewGuid());
            var replay = Guid.NewGuid();
            var first = accounts.Purchase(replayBuyer.Id, "champion:67", "RP", replay);
            TestAssert.Equal(487L, first.Price);
            TestAssert.Equal(2_000L - 487, PlatformEconomy.For(accounts.Get(replayBuyer.Id)).Rp);
            TestAssert.Equal(first, accounts.Purchase(replayBuyer.Id, "champion:67", "RP", replay));
            TestAssert.Equal(2_000L - 487, PlatformEconomy.For(accounts.Get(replayBuyer.Id)).Rp);

            // The page's displayed price is compared with the live one, never trusted: a stale price (sale ended) is refused
            // and a matching one goes through.
            var stale = accounts.RegisterPublic("stalebuyer", "testing-password", "Stale Buyer", maxed: false);
            accounts.GrantCurrency(stale.Id, 0, 5_000, Guid.NewGuid());
            clock.Current = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(stale.Id, "champion:67", "RP", Guid.NewGuid(), 1, 487));
            TestAssert.Equal(5_000L, PlatformEconomy.For(accounts.Get(stale.Id)).Rp);
            TestAssert.Equal(975L, accounts.Purchase(stale.Id, "champion:67", "RP", Guid.NewGuid(), 1, 975).Price);
        }
        finally { DeleteDirectory(root); }

        // Two requests at once for the last 292 RP: exactly one wins, the balance never goes negative.
        var raceRoot = TemporaryDirectory("league-air-store-rp-race");
        try
        {
            using var accounts = new PlatformAccounts(raceRoot, raceRoot, new FixedTimeProvider(InSale));
            var buyer = accounts.RegisterPublic("racebuyer", "testing-password", "Race Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 0, 292, Guid.NewGuid());
            var attempts = new[] { "champion:40", "champion:40", "champion:67", "champion:98" }.Select(id => Task.Run(() =>
            {
                try { accounts.Purchase(buyer.Id, id, "RP", Guid.NewGuid()); return true; }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return false; }
            })).ToArray();
            var results = Task.WhenAll(attempts).GetAwaiter().GetResult();
            TestAssert.Equal(1, results.Count(won => won), "only one purchase can spend the 292 RP");
            TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(buyer.Id)).Rp);
            TestAssert.Equal(1, PlatformEconomy.For(accounts.Get(buyer.Id)).OwnedChampions!.Distinct().Count());
        }
        finally { DeleteDirectory(raceRoot); }
    }

    // Until 26 October 2026 this was server.air-store-locked-item-refused-on-sale: a champion the server could not play was shown on sale but
    // refused. All 84 champions are playable since batch 341, so no champion or skin is locked; the sale is bought at its price instead.
    [Check("server.air-store-sale-items-are-bought-at-the-sale-price-and-no-champion-or-skin-is-locked")]
    public static void SaleChampionsAndSkinsAreBoughtAtTheSalePriceAndNoChampionOrSkinIsLocked()
    {
        if (Without2011Store()) return;
        var root = TemporaryDirectory("league-air-store-sale-purchase");
        try
        {
            AddSkinArt(root, 7001);
            // 12 November is inside the LeBlanc champion sale (11-14 November 2011); 16 November is inside the Wicked LeBlanc skin sale (15-18
            // November).
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 12, 19, 0, 0, TimeSpan.Zero));
            using var accounts = new PlatformAccounts(root, root, clock);
            var buyer = accounts.RegisterPublic("salebuyer", "testing-password", "Sale Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 0, 10_000, Guid.NewGuid());

            // No champion or skin is locked once all 84 champions play. The three bundles whose contents are not implemented stay locked, and a
            // locked bundle is refused on purchase and charges nothing (the refusal half of the old check, now on a bundle).
            TestAssert.Equal(0, accounts.StoreCatalog.Items.Count(product => product.Locked && (product.Kind == "CHAMPION" || product.Kind == "CHAMPION_SKIN")),
                "no champion or skin is locked once all 84 champions play");
            TestAssert.Equal(3, accounts.StoreCatalog.Items.Count(product => product.Locked && product.Kind == "BUNDLE"), "the three bundles stay locked");
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer.Id, "bundle:champions-bundle", "RP", Guid.NewGuid()));
            TestAssert.Equal(10_000L, PlatformEconomy.For(accounts.Get(buyer.Id)).Rp, "a refused bundle charges nothing");

            var leblanc = accounts.StoreCatalog.GetChampion(7);
            TestAssert.Equal(50, leblanc.SalePercent);
            TestAssert.Equal<int?>(487, leblanc.RpPrice);
            accounts.Purchase(buyer.Id, "champion:7", "RP", Guid.NewGuid());

            clock.Current = new DateTimeOffset(2026, 11, 16, 19, 0, 0, TimeSpan.Zero);
            var skin = accounts.StoreCatalog.GetSkin(7001);
            TestAssert.Equal(50, skin.SalePercent);
            var skinPrice = skin.RpPrice ?? throw new InvalidOperationException("the skin has an RP price");
            accounts.Purchase(buyer.Id, "skin:7001", "RP", Guid.NewGuid());

            // Both were charged at the sale price and granted.
            var economy = PlatformEconomy.For(accounts.Get(buyer.Id));
            TestAssert.Equal(10_000L - 487 - skinPrice, economy.Rp);
            TestAssert.True(economy.OwnedChampions is not null && economy.OwnedChampions.Contains(7), "LeBlanc was granted");
            TestAssert.Equal(2, economy.Purchases?.Length ?? 0);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-store-insufficient-rp-refused-on-sale")]
    public static void TheSalePriceIsTheOnlyPriceAndTooLittleRpIsRefused()
    {
        if (Without2011Store()) return;
        var root = TemporaryDirectory("league-air-store-rp-short");
        try
        {
            using var accounts = new PlatformAccounts(root, root, new FixedTimeProvider(InSale));
            var buyer = accounts.RegisterPublic("shortbuyer", "testing-password", "Short Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 0, 291, Guid.NewGuid());
            // 291 RP is one short of Janna's sale price (292) and far short of her regular 585.
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(buyer.Id, "champion:40", "RP", Guid.NewGuid()));
            TestAssert.Equal(291L, PlatformEconomy.For(accounts.Get(buyer.Id)).Rp);
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(buyer.Id, "champion:40", "IP", Guid.NewGuid()),
                "IP is not RP: the account has none");
            TestAssert.True(PlatformEconomy.For(accounts.Get(buyer.Id)).OwnedChampions is null or { Length: 0 });
            accounts.GrantCurrency(buyer.Id, 0, 1, Guid.NewGuid());
            TestAssert.Equal(292L, accounts.Purchase(buyer.Id, "champion:40", "RP", Guid.NewGuid()).Price);
            TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(buyer.Id)).Rp);
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-store-http-rp-purchase-ignores-client-price")]
    public static void HttpPurchaseChargesTheServerPriceKeepsEveryGateAndPageShowsTheSale() =>
        HttpPurchaseAsync().GetAwaiter().GetResult();

    private static async Task HttpPurchaseAsync()
    {
        if (Without2011Store()) return;
        var root = TemporaryDirectory("league-air-store-http-sale");
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root, root, new FixedTimeProvider(InSale));
            var account = accounts.RegisterPublic("httpsale", "testing-password", "Http Sale", maxed: false);
            accounts.GrantCurrency(account.Id, 0, 700, Guid.NewGuid());
            var sessions = new PlatformSessions(accounts);
            var session = sessions.Login("sale-buyer", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
            TestAssert.True(sessions.AuthenticateChannel("sale-buyer", account.Username, session.Token));
            var access = sessions.RedeemStoreTicket(sessions.IssueStoreTicket("sale-buyer"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
            });
            PlatformStore.Map(app, accounts, sessions, root);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("Cookie", PlatformStore.StoreCookie + "=" + access.Token);

                // The unlock dialog offers the RP purchase at the sale price (its price column and the form's expected price), with the
                // regular price struck through on the item card.
                var page = await client.GetStringAsync("/store/item/champion%3A40");
                TestAssert.True(page.Contains("name=expectedPrice value=\"292\"", StringComparison.Ordinal));
                TestAssert.True(page.Contains("class=dlg-price>292<", StringComparison.Ordinal));
                TestAssert.True(page.Contains("<s>585</s>", StringComparison.Ordinal));
                var home = await client.GetStringAsync("/store/home");
                TestAssert.True(home.Contains("CHAMPIONS SALE", StringComparison.Ordinal));
                TestAssert.True(home.Contains("Expires Saturday, October 10th", StringComparison.Ordinal));

                async Task<HttpStatusCode> Buy(string itemId, IEnumerable<KeyValuePair<string, string>>? extra = null,
                    string? formToken = null, Guid? request = null)
                {
                    var fields = new Dictionary<string, string>
                    {
                        ["itemId"] = itemId, ["currency"] = "RP", ["quantity"] = "1",
                        ["requestId"] = (request ?? Guid.NewGuid()).ToString(), ["formToken"] = formToken ?? access.FormToken,
                    };
                    foreach (var pair in extra ?? []) fields[pair.Key] = pair.Value;
                    using var form = new FormUrlEncodedContent(fields);
                    using var response = await client.PostAsync("/store/purchase", form);
                    return response.StatusCode;
                }
                long Rp() => PlatformEconomy.For(accounts.Get(account.Id)).Rp;

                // Every existing gate still refuses first: wrong form token, locked item, unknown item.
                TestAssert.Equal(HttpStatusCode.BadRequest, await Buy("champion:40", formToken: "wrong-form-token"));
                TestAssert.Equal(HttpStatusCode.BadRequest, await Buy("champion:68"));
                TestAssert.Equal(HttpStatusCode.BadRequest, await Buy("champion:999999"));
                TestAssert.Equal(700L, Rp());

                // Client-supplied price fields are ignored; a stale displayed price is refused.
                TestAssert.Equal(HttpStatusCode.BadRequest, await Buy("champion:40", [new("expectedPrice", "585")]));
                TestAssert.Equal(HttpStatusCode.BadRequest, await Buy("champion:40", [new("expectedPrice", "garbage")]));
                TestAssert.Equal(700L, Rp());
                var request = Guid.NewGuid();
                TestAssert.Equal(HttpStatusCode.OK, await Buy("champion:40",
                    [new("price", "1"), new("rpPrice", "1"), new("amount", "1"), new("salePrice", "1"), new("expectedPrice", "292")], request: request));
                TestAssert.Equal(700L - 292, Rp(), "the server charged its own sale price, not 1");
                TestAssert.Equal(HttpStatusCode.OK, await Buy("champion:40", request: request), "a replay is idempotent");
                TestAssert.Equal(700L - 292, Rp());

                // Two purchases at once for the remaining 408 RP: Vayne (487 on sale) does not fit and Shen is locked, so neither is charged.
                var raced = await Task.WhenAll(Buy("champion:67"), Buy("champion:98"));
                TestAssert.True(raced.All(status => status == HttpStatusCode.BadRequest), "insufficient RP is refused");
                TestAssert.Equal(700L - 292, Rp());
            }
            finally { await app.StopAsync(); }
        }
        finally { DeleteDirectory(root); }
    }

    private static void AddSkinArt(string root, params int[] skinIds)
    {
        var skins = Path.Combine(root, "assets", "storeImages", "content", "skins");
        Directory.CreateDirectory(skins);
        foreach (var id in skinIds) File.WriteAllBytes(Path.Combine(skins, $"championsskin_{id}.jpg"), [1]);
    }

    private static string TemporaryDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
