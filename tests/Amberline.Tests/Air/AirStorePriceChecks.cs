using System.Globalization;
using Amberline.Data;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// The store's retail prices of October 2011 (owner rule "as close to retail as possible", 19 October 2026): champions (IP and RP), skins
/// (RP; an unlisted skin was not for sale), regular runes (IP), boost packs, the extra rune page and the bundles, from the operator's
/// <c>content/store/prices.json</c> (batch 597; its private source is <c>assets/air-store-2011/prices.json</c>).
/// </summary>
public static class AirStorePriceChecks
{


    // Batch 187: an amount retail never sold is priced per unit of the nearest pack. That price is our own extrapolation, and the served note says so.
    [Check("server.air-store-unsold-boost-amount-note-says-our-price")]
    public static void AnUnsoldBoostAmountIsLabelledAsOurPrice()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-boost-note-" + Guid.NewGuid().ToString("N"));
        var catalog = new PlatformStoreCatalog(new PlatformEconomyRules { IpBoostWinCount = 6 }, root);
        TestAssert.True(catalog.TryGet(PlatformStoreCatalog.IpWinBoostId, out var boost), "the configured 6-win IP boost is listed");
        TestAssert.False(boost.PriceHistoricallyVerified, "retail sold no 6-win pack");
        TestAssert.True(boost.PriceNote.StartsWith("Priced by us", StringComparison.Ordinal), $"the note says the price is ours: {boost.PriceNote}");
    }

    [Check("server.air-store-catalog-prices-follow-the-retail-list-and-the-sales-use-them")]
    public static void CatalogPricesAreTheRetailOnesAndSalesDiscountThem()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = Path.Combine(Path.GetTempPath(), "league-air-prices-" + Guid.NewGuid().ToString("N"));
        var skins = Path.Combine(root, "assets", "storeImages", "content", "skins");
        Directory.CreateDirectory(skins);
        try
        {
            // Art for Annie's six skins, Lee Sin's two and Xerath's first (the store lists a skin only where its art exists).
            foreach (var id in new[] { 1001, 1002, 1003, 1004, 1005, 1006, 64001, 64002, 101001 })
                File.WriteAllBytes(Path.Combine(skins, $"championsskin_{id}.jpg"), [1]);
            var july = new FixedClock(new DateTimeOffset(2026, 7, 15, 19, 0, 0, TimeSpan.Zero));
            var catalog = new PlatformStoreCatalog(new PlatformEconomyRules(), root, july);

            void Champion(int id, int ip, int rp)
            {
                var item = catalog.GetChampion(id);
                TestAssert.Equal<int?>(ip, item.IpPrice, $"champion {id} IP");
                TestAssert.Equal<int?>(rp, item.RpPrice, $"champion {id} RP");
                TestAssert.True(item.PriceHistoricallyVerified, $"champion {id} price is sourced");
            }
            Champion(1, 450, 260);        // Annie
            Champion(40, 1350, 585);      // Janna
            Champion(61, 4800, 975);      // Orianna (the roster said 6300)
            Champion(53, 3150, 975);      // Blitzcrank (the roster said 6300)
            Champion(67, 6300, 975);      // Vayne
            Champion(101, 6300, 975);     // Xerath
            Champion(30, 3150, 975);      // Karthus, reduced from 6300 on 14 October 2011

            TestAssert.Equal<int?>(520, catalog.GetSkin(1002).RpPrice);   // Red Riding Annie
            TestAssert.Equal<int?>(975, catalog.GetSkin(1005).RpPrice);   // Frostfire Annie
            TestAssert.Equal<int?>(1820, catalog.GetSkin(1003).RpPrice);  // Annie in Wonderland, a legendary skin
            TestAssert.Equal<int?>(975, catalog.GetSkin(64002).RpPrice);  // Acolyte Lee Sin
            // Goth Annie was a Collector's Edition skin: no product, not purchasable, whatever the request says.
            TestAssert.False(catalog.TryGet("skin:1001", out _));
            TestAssert.Throws<ArgumentException>(() => catalog.GetSkin(1001));

            // Runes: printed prices, and a labelled inference for a rune the page does not print.
            var strength = catalog.GetRune(5001);       // Lesser Mark of Strength
            TestAssert.Equal<int?>(15, strength.IpPrice);
            TestAssert.True(strength.PriceHistoricallyVerified);
            TestAssert.Equal<int?>(30, catalog.GetRune(5003).IpPrice);    // Lesser Mark of Alacrity
            TestAssert.Equal<int?>(2050, catalog.GetRune(5373).IpPrice);  // Greater Quintessence of Meditation
            var haste = catalog.GetRune(5004);                          // Lesser Mark of Haste: not on the page, priced like Alacrity
            TestAssert.Equal<int?>(30, haste.IpPrice);
            TestAssert.False(haste.PriceHistoricallyVerified);
            TestAssert.True(haste.PriceNote.StartsWith("Reconstruction:", StringComparison.Ordinal));
            TestAssert.True(catalog.Items.Where(item => item.Kind == "RUNE").All(item => item.RpPrice is null), "runes sell for IP only");

            // Rune pages: 6300 IP or 590 RP. Boosts and bundles as printed.
            var page = catalog.GetRunePage(3);
            TestAssert.Equal<int?>(6300, page.IpPrice);
            TestAssert.Equal<int?>(590, page.RpPrice);
            TestAssert.Equal<int?>(290, catalog.Get(PlatformStoreCatalog.IpTimedBoostId).RpPrice);
            TestAssert.Equal<int?>(150, catalog.Get(PlatformStoreCatalog.XpTimedBoostId).RpPrice);
            TestAssert.Equal<int?>(670, catalog.Get(PlatformStoreCatalog.IpWinBoostId).RpPrice);
            TestAssert.Equal<int?>(350, catalog.Get(PlatformStoreCatalog.XpWinBoostId).RpPrice);
            TestAssert.Equal<int?>(2600, catalog.Get(PlatformStoreCatalog.RunePageBundleId).RpPrice);
            TestAssert.Equal<int?>(3410, catalog.Get("bundle:champions-bundle").RpPrice);

            // A host that configures another boost length is priced per unit (Reconstruction), 4 wins at the retail pack price.
            var four = new PlatformStoreCatalog(new PlatformEconomyRules { IpBoostWinCount = 4, XpBoostDurationDays = 3 }, root, july);
            TestAssert.Equal<int?>(290, four.Get(PlatformStoreCatalog.IpWinBoostId).RpPrice);
            TestAssert.Equal<int?>(260, four.Get(PlatformStoreCatalog.XpTimedBoostId).RpPrice);

            // The sales discount the retail base: 8 October 2026 is inside the 7-10 October window (Janna 585 -> 292, Acolyte Lee Sin 975 -> 487);
            // the discount is derived from the clock on every read, and the base is the same price list.
            var october = new FixedClock(new DateTimeOffset(2026, 10, 8, 19, 0, 0, TimeSpan.Zero));
            var onSale = new PlatformStoreCatalog(new PlatformEconomyRules(), root, october);
            TestAssert.Equal<int?>(292, onSale.GetChampion(40).RpPrice);
            TestAssert.Equal<int?>(585, onSale.GetChampion(40).RegularRpPrice);
            TestAssert.Equal<int?>(487, onSale.GetSkin(64002).RpPrice);
            TestAssert.Equal<int?>(975, onSale.GetSkin(64002).RegularRpPrice);
            TestAssert.Equal<int?>(1350, onSale.GetChampion(40).IpPrice, "a sale never touches the IP price");
            // A host price override still wins over the list.
            var rules = new PlatformEconomyRules { Prices = new Dictionary<string, PlatformStorePrice> { ["champion:1"] = new(1, 2) } };
            var overridden = new PlatformStoreCatalog(rules, root, july).GetChampion(1);
            TestAssert.Equal<int?>(1, overridden.IpPrice);
            TestAssert.Equal<int?>(2, overridden.RpPrice);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-purchase-charges-the-retail-price")]
    public static void PurchasesChargeTheServersRetailPriceInBothCurrencies()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = Path.Combine(Path.GetTempPath(), "league-air-price-buy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root, root, new FixedClock(new DateTimeOffset(2026, 7, 15, 19, 0, 0, TimeSpan.Zero)));
            var buyer = accounts.RegisterPublic("pricebuyer", "testing-password", "Price Buyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 9_000, 3_000, Guid.NewGuid());
            // Orianna: 4800 IP (the old roster price was 6300) and Annie in RP: 260; the form's expected price can only refuse.
            TestAssert.Equal(4_800L, accounts.Purchase(buyer.Id, "champion:61", "IP", Guid.NewGuid()).Price);
            TestAssert.Equal(260L, accounts.Purchase(buyer.Id, "champion:1", "RP", Guid.NewGuid()).Price);
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(buyer.Id, "champion:40", "RP", Guid.NewGuid(), 1, 100));
            // An additional rune page costs 590 RP (or 6300 IP): the RP price is new.
            TestAssert.Equal(590L, accounts.Purchase(buyer.Id, "rune_page:3", "RP", Guid.NewGuid()).Price);
            // A boost pack at its retail RP price, and a rune at its printed IP price.
            TestAssert.Equal(670L, accounts.Purchase(buyer.Id, PlatformStoreCatalog.IpWinBoostId, "RP", Guid.NewGuid()).Price);
            TestAssert.Equal(15L, accounts.Purchase(buyer.Id, "rune:5001", "IP", Guid.NewGuid()).Price);
            var economy = PlatformEconomy.For(accounts.Get(buyer.Id));
            TestAssert.Equal(9_000L - 4_800 - 15, economy.Ip);
            TestAssert.Equal(3_000L - 260 - 590 - 670, economy.Rp);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
