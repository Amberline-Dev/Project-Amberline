
namespace Amberline.Platform;

public static partial class PlatformStore
{
    /// <param name="Kind">NEW (green burst), SALE (blue seal), or none.</param>
    /// <param name="ArtKeys">Catalog artwork keys of the splash arts shown as diagonal strips.</param>
    private sealed record PromoSlide(string Kind, string Title, string Subtitle, string Body, string Button, string Href,
        int Percent, IReadOnlyList<string> ArtKeys);

    /// <summary>
    /// The store home, laid out like the 25 September 2011 screenshot of the same page: a 780x350 promo with page buttons,
    /// "What's New" cards under it, "Featured Item" and "Top Sellers" on the right.
    /// Reconstruction: which items it shows. The screenshot's sale was the 26 September skin sale and its new items were
    /// Riven's; this page shows what was current when 1.0.0.126 shipped (the Xerath release) and the sales that the 2011
    /// calendar has live today (see <see cref="PlatformStoreCatalog.ActiveSales"/>; none outside a window). Top Sellers has
    /// no sales data behind it.
    /// </summary>
    private static string RenderHomeDashboard(PlatformAccounts accounts, PlatformAccount account, PlatformEconomyState economy,
        string state)
    {
        var catalog = accounts.StoreCatalog;
        var slides = new List<PromoSlide>();
        if (catalog.TryGet("champion:101", out var xerath))
            slides.Add(new("NEW", xerath.Name, "The Magus Ascendant",
                "A long-range mage who bombards his enemies from afar. New in this patch.",
                "Unlock " + xerath.Name, ItemHref(xerath, state), 0, ["champion-loading-101", "skin-loading-101001", "skin-loading-101002"]));
        foreach (var sale in catalog.ActiveSales())
        {
            var keys = sale.ItemIds.Take(3).Select(id => (sale.Kind == "CHAMPION" ? "champion-loading-" : "skin-loading-") +
                id[(id.IndexOf(':') + 1)..]).ToArray();
            slides.Add(new("SALE", sale.Title, sale.Expires, sale.Body, sale.Button,
                StoreQuery(sale.Kind, string.Empty, false, "name") + "&amp;sale=1", sale.Percent, keys));
        }
        slides.Add(new("NEW", "New Skins", "Now in the store",
            "Runeborn Xerath, Battlecast Xerath, Cryocore Brand and Officer Caitlyn skins are here!",
            "Unlock These Skins", StoreQuery("CHAMPION_SKIN", string.Empty, false, "name"), 0,
            ["skin-loading-101001", "skin-loading-101002", "skin-loading-63003"]));

        var promoMarkup = new System.Text.StringBuilder();
        var pager = new System.Text.StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            var slide = slides[i];
            var strips = new System.Text.StringBuilder();
            for (var s = 0; s < Math.Min(3, slide.ArtKeys.Count); s++)
                if (catalog.Artwork(slide.ArtKeys[s]) is { } art)
                    strips.Append($"<span class=\"bn-panel bn{s + 1}\"><img src=\"{Encode(art)}\" alt=\"\"></span>");
            var badge = slide.Kind == "SALE"
                ? $"<span class=promo-seal title=\"{slide.Percent}% off\"><b>{slide.Percent}%</b><i>OFF</i></span>"
                : "<span class=promo-new>NEW</span>";
            promoMarkup.Append($"<div class=\"promo-slide{(i == 0 ? " on" : string.Empty)}\" id=promo{i}><div class=promo-art>{strips}</div>{badge}" +
                $"<h1 class=promo-title>{Encode(slide.Title)}</h1><span class=promo-expires>{Encode(slide.Subtitle)}</span>" +
                $"<p class=promo-text>{Encode(slide.Body)}</p><a class=promo-button href=\"{slide.Href}\">{Encode(slide.Button)}</a></div>");
            pager.Append($"<a href=\"#\" id=promopage{i} class=\"{(i == 0 ? "on" : string.Empty)}\" onclick=\"return storePromo({i},{slides.Count})\">{i + 1}</a>");
        }

        var newItems = PlatformStoreCatalog.NewItemIds.Select(id => catalog.TryGet(id, out var item) ? item : null)
            .Where(item => item is not null).Select(item => item!).ToArray();
        var cards = string.Join("", newItems.Select(item => RenderFeaturedCard(item, account, economy, catalog, state)));
        var featured = newItems.FirstOrDefault();
        var featuredMarkup = featured is null ? "<p class=empty>No products are available.</p>" : RenderFeaturedItem(featured, account, economy, catalog, state);
        var sellers = string.Join("", PlatformStoreCatalog.TopSellerIds.Select(id => catalog.TryGet(id, out var item) ? item : null)
            .Where(item => item is not null).Select(item => RenderTopSeller(item!, account, economy, catalog, state)));
        return $$"""
            <section class="store-frame store-home">
              {{RenderStoreHeader(null)}}
              <div class=store-panel>
                <div class=home-dashboard>
                  <section class=home-hero>{{promoMarkup}}<div class=promo-pager>{{pager}}</div></section>
                  <section class=home-whats-new><h2>What's New</h2><div class=home-cards>{{cards}}</div></section>
                  <aside class=home-sidebar>
                    <section class=sidebar-panel><h2>Featured Item</h2>{{featuredMarkup}}</section>
                    <section class=sidebar-panel><h2>Top Sellers</h2><div class=sidebar-products>{{sellers}}</div></section>
                  </aside>
                </div>
              </div>
            </section>
            """;
    }

    // "What's New" card: the catalog card art with its title, the New badge and the Unlock button (the same hover halves as the grid).
    private static string RenderFeaturedCard(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog, string state)
    {
        var image = CardImage(item, catalog);
        var art = image is null ? "<span class=missing-art></span>" : $"<img class=card-art src=\"{Encode(image)}\" alt=\"\">";
        var reason = Unavailability(item, account, economy, catalog);
        var action = UnlockAction(item, reason, state);
        return $"<article id=\"{CardId(item.Id)}\" class=featured-card><div class=card-body>{art}<span class=card-title>{Encode(item.Name)}</span>{Badges(item)}{HoverLinks(item, state)}<span class=card-footer>{action}<span class=price>{FormatPrice(item, economy)}</span></span></div></article>";
    }

    private static string RenderFeaturedItem(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog, string state)
    {
        var image = CardImage(item, catalog);
        var art = image is null ? string.Empty : $"<img src=\"{Encode(image)}\" alt=\"\">";
        var action = UnlockAction(item, Unavailability(item, account, economy, catalog), state);
        return $"<div class=sidebar-feature><a class=sidebar-feature-art href=\"{ItemHref(item, state)}\">{art}</a><span class=sidebar-feature-copy><strong>{Encode(item.Name)}</strong>{action}<span class=price>{FormatPrice(item, economy)}</span></span></div>";
    }

    private static string RenderTopSeller(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog, string state)
    {
        var image = CardImage(item, catalog);
        var art = image is null ? string.Empty : $"<img src=\"{Encode(image)}\" alt=\"\">";
        var reason = Unavailability(item, account, economy, catalog);
        var why = reason is null || reason is "Owned" or "Max quantity" ? string.Empty : $"<span class=why>{Encode(reason)}</span>";
        return $"<div class=sidebar-product><a class=sidebar-art href=\"{ItemHref(item, state)}\">{art}</a><span class=sidebar-copy><strong>{Encode(item.Name)}</strong>{why}<span class=price>{FormatPrice(item, economy)}</span></span><span class=hot></span></div>";
    }
}
