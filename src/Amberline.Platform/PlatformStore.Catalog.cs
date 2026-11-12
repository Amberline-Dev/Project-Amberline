using System.Globalization;
using System.Net;
using Amberline.Data;

namespace Amberline.Platform;

public static partial class PlatformStore
{
    public static string RenderHome(PlatformSessions.StoreSession access, PlatformAccounts accounts,
        string? kind = null, int page = 1, string? search = null, bool notOwned = false, string? sort = null,
        string? runeType = null, int? runeTier = null, string? currency = null, string? tag = null,
        bool focusSearch = false, bool onSale = false, bool championOwned = false, string? dialogItemId = null, string? overlay = null,
        string? focusCard = null)
    {
        var account = accounts.Get(access.AccountId);
        var economy = PlatformEconomy.For(account);
        var filter = NormalizeKind(kind);
        var searchTerm = NormalizeSearch(search);
        var sortKey = NormalizeSort(sort);
        var runeTypeKey = NormalizeRuneType(runeType);
        var runeTierKey = NormalizeRuneTier(runeTier);
        var currencyKey = NormalizeCurrency(currency);
        var championTagKey = NormalizeChampionTag(tag);
        _ = page; // Retained for the stock callback; v1.0.0.122 replaced store pagination with scrolling.

        var catalog = accounts.StoreCatalog;
        // State the cards carry into the unlock dialog so that closing it returns to the same filtered view.
        var state = StoreParams(filter, searchTerm, notOwned, sortKey, runeTypeKey, runeTierKey, currencyKey, championTagKey,
            onSale, championOwned);
        var dialogItem = dialogItemId is null ? null : catalog.Get(dialogItemId);
        // The dialog comes first in the document so that its art is requested before the hundreds of card images behind it
        // (the real client showed an empty dialog while a rune list was still loading); z-index keeps it on top.
        // focusCard: the card the grid scrolls to when the page itself is the response of a purchase (it has no #card address).
        string Page(string content) => dialogItem is null
            ? Layout("Store", overlay + content, focusCard is null ? string.Empty : $" data-card=\"{Encode(focusCard)}\"")
            : Layout("Store", RenderUnlockDialog(access, catalog, account, economy, dialogItem, state) + content,
                $" data-card=\"{CardId(dialogItem.Id)}\"");

        if (filter is null && searchTerm.Length == 0 && !notOwned && sortKey == "name" && !onSale && !championOwned &&
            runeTypeKey is null && runeTierKey is null && currencyKey is null && championTagKey is null)
            return Page(RenderHomeDashboard(accounts, account, economy, state));

        IEnumerable<PlatformStoreItem> all = catalog.Items
            .Where(item => filter is null || item.Kind.Equals(filter, StringComparison.Ordinal));
        if (championTagKey is not null && (filter is "CHAMPION" or "CHAMPION_SKIN"))
            all = all.Where(item => PlatformStoreCatalog.ChampionSearchTags(item.ChampionId)
                .Contains(championTagKey, StringComparer.OrdinalIgnoreCase));
        if (searchTerm.Length > 0)
            all = all.Where(item => SearchText(item, catalog).Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
        if (filter == "RUNE" && runeTypeKey is not null)
            all = all.Where(item => catalog.RuneContent(item.ContentId)?.Color.ToString()
                .Equals(runeTypeKey, StringComparison.OrdinalIgnoreCase) == true);
        if (filter == "RUNE" && runeTierKey is not null)
            all = all.Where(item => catalog.RuneContent(item.ContentId)?.Tier == runeTierKey);
        if (filter == "RUNE" && currencyKey is not null)
            all = all.Where(item => currencyKey == "IP" ? item.IpPrice is not null : item.RpPrice is not null);
        if (onSale) all = all.Where(item => item.SalePercent > 0);
        if (championOwned && filter == "CHAMPION_SKIN")
            all = all.Where(item => IsOwned(catalog.GetChampion(item.ChampionId), account, economy, catalog));
        if (notOwned)
            all = all.Where(item => !IsOwnedForFilter(item, account, economy, catalog));
        all = SortItems(all, sortKey);
        var products = all.Select(item => RenderProductRow(item, account, economy, catalog, state: state)).ToArray();
        var empty = products.Length == 0 ? "<p class=empty>No products match these filters.</p>" : string.Empty;
        var filters = RenderFilters(filter, searchTerm, notOwned, sortKey, runeTypeKey, runeTierKey, currencyKey,
            championTagKey, focusSearch, onSale, championOwned, catalog.PaidContentOff());
        return Page($$"""
            <section class="store-frame store-catalog store-{{(filter ?? "all").ToLowerInvariant()}}">
              {{RenderStoreHeader(catalog, filter)}}
              <div class=store-panel>
                {{filters}}
                <section class=catalog-panel><div class=catalog-scroll id=catalog-scroll><div class="products {{(filter == "CHAMPION_SKIN" ? "skin-products" : filter == "RUNE" ? "rune-products" : "champion-products")}}">{{string.Join("", products)}}</div>{{empty}}</div></section>
              </div>
            </section>
            """);
    }

    /// <param name="catalog">
    /// NOT RETAIL (batch 751): while the host's "Offer skins and RP" switch is off (<see cref="PlatformStoreCatalog.PaidContentOff"/>) the header
    /// has no Purchase RP link and no tab whose kind the catalog does not list (Skins, and the kinds sold for RP alone); retail showed them all.
    /// Null (a page that has no catalog at hand) shows them all.
    /// </param>
    private static string RenderStoreHeader(PlatformStoreCatalog? catalog, string? filter, bool purchases = false, bool codes = false, bool rp = false)
    {
        var listed = catalog?.PaidContentOff() == true ? catalog.Items.Select(item => item.Kind).ToHashSet() : null;
        var tabs = new[]
        {
            (Key: (string?)null, Label: "Home"),
            (Key: (string?)"CHAMPION", Label: "Champions"),
            (Key: (string?)"CHAMPION_SKIN", Label: "Skins"),
            (Key: (string?)"RUNE", Label: "Runes"),
            (Key: (string?)"BOOST", Label: "Boosts"),
            (Key: (string?)"BUNDLE", Label: "Bundles"),
            (Key: (string?)"SPELL_BOOK_PAGE", Label: "Other"),
        };
        var supported = string.Join("", tabs.Where(tab => listed is null || tab.Key is null || listed.Contains(tab.Key)).Select(tab =>
            $"<a class=\"store-tab{(!purchases && !codes && !rp && (string.Equals(tab.Key, filter, StringComparison.Ordinal) || tab.Key is null && filter is null) ? " active" : string.Empty)}\" href=\"{StoreQuery(tab.Key, string.Empty, false, "name")}\">{tab.Label}</a>"));
        var purchasesClass = purchases ? " active" : string.Empty;
        var codesClass = codes ? " active" : string.Empty;
        var rpLink = listed is not null ? string.Empty : $"<a class=\"store-rp-link{(rp ? " active" : string.Empty)}\" href=\"/store/rp\">Purchase RP</a>";
        return $"<aside class=store-nav>{rpLink}<nav class=store-tabs>{supported}<a class=\"store-tab{codesClass}\" href=\"/store/codes\">Codes</a><a class=\"store-tab{purchasesClass}\" href=\"/store/purchases\">Purchases</a></nav></aside>";
    }

    private static string RenderFilters(string? filter, string search, bool notOwned, string sort,
        string? runeType = null, RuneTier? runeTier = null, string? currency = null,
        string? championTag = null, bool focusSearch = false, bool onSale = false, bool championOwned = false, bool rpOff = false)
    {
        var kind = filter is null ? string.Empty : WebUtility.HtmlEncode(filter);
        var encodedSearch = WebUtility.HtmlEncode(search);
        const string submit = " onclick=\"this.form.submit()\"";
        string Check(string name, string label, bool on) =>
            $"<label class=check-label><input type=checkbox name={name} value=1{(on ? " checked" : string.Empty)}{submit}> {label}</label>";
        string Radio(string name, string value, string label, bool on) =>
            $"<label class=check-label><input type=radio name={name} value={value}{(on ? " checked" : string.Empty)}{submit}> {label}</label>";
        var showAll = $"<a class=check-label href=\"{StoreQuery(filter, string.Empty, false, "name")}\"><input type=checkbox{(notOwned || onSale || championOwned || championTag is not null || runeType is not null || runeTier is not null || currency is not null || search.Length > 0 ? string.Empty : " checked")} onclick=\"return false\"> Show All</a>";

        var options = new System.Text.StringBuilder();
        // NOT RETAIL (batch 751): rpOff, the host's "Offer skins and RP" switch is off: no sale (a sale lowers the RP price), no RP currency and
        // no RP price sort; retail always offered them.
        if (filter is "CHAMPION" or "CHAMPION_SKIN" && !rpOff) options.Append(Check("sale", "On Sale", onSale));
        if (filter == "CHAMPION_SKIN") options.Append(Check("championOwned", "Champion Owned", championOwned));
        if (filter is null or "CHAMPION" or "CHAMPION_SKIN" or "RUNE" or "BOOST" or "BUNDLE" or "SPELL_BOOK_PAGE")
            options.Append(Check("notOwned", "Not Owned", notOwned));
        if (filter == "CHAMPION")
        {
            foreach (var (value, label) in new[] { ("assassin", "Assassin"), ("fighter", "Fighter"), ("tank", "Tank"), ("mage", "Mage"), ("support", "Support") })
                options.Append(Radio("tag", value, label, championTag == value));
        }
        if (filter == "RUNE")
        {
            options.Append($"<span class=filter-label>Tier</span><select id=rune-tier name=runeTier class=sort-select onchange=\"this.form.submit()\"><option value=\"\">All tiers</option>" +
                $"<option value=1{(runeTier == RuneTier.One ? " selected" : string.Empty)}>Tier 1</option><option value=2{(runeTier == RuneTier.Two ? " selected" : string.Empty)}>Tier 2</option><option value=3{(runeTier == RuneTier.Three ? " selected" : string.Empty)}>Tier 3</option></select>");
            options.Append("<span class=filter-label>Types</span>");
            options.Append($"<label class=check-label><input type=radio name=runeType value=\"\"{(runeType is null ? " checked" : string.Empty)}{submit}> All types</label>");
            foreach (var (value, label) in new[] { ("Red", "Mark"), ("Blue", "Glyph"), ("Yellow", "Seal"), ("Black", "Quintessence") })
                options.Append($"<label class=check-label><input type=radio name=runeType value={value}{(runeType == value ? " checked" : string.Empty)}{submit}> {label}</label>");
            options.Append($"<span class=filter-label>Available for</span><select id=rune-currency name=currency class=sort-select onchange=\"this.form.submit()\"><option value=\"\">Any currency</option>" +
                $"<option value=IP{(currency == "IP" ? " selected" : string.Empty)}>IP</option>" +
                (rpOff ? string.Empty : $"<option value=RP{(currency == "RP" ? " selected" : string.Empty)}>RP</option>") + "</select>");
        }
        options.Append(showAll);

        var sortRows = new System.Text.StringBuilder();
        // The screenshots offer RP and IP price sorts for champions, RP for skins and bundles, IP for runes.
        if (filter is null or "CHAMPION" or "CHAMPION_SKIN" or "BUNDLE" or "BOOST" or "SPELL_BOOK_PAGE" && !rpOff)
            sortRows.Append($"<label class=check-label><input type=radio name=sort value=rp{(sort == "rp" ? " checked" : string.Empty)}{submit}> Price (RP)</label>");
        if (filter is null or "CHAMPION" or "RUNE")
            sortRows.Append($"<label class=check-label><input type=radio name=sort value=ip{(sort == "ip" ? " checked" : string.Empty)}{submit}> Price (IP)</label>");
        sortRows.Append($"<label class=check-label><input type=radio name=sort value=name{(sort == "name" ? " checked" : string.Empty)}{submit}> Alphabetical</label>");
        var clear = search.Length > 0
            ? $"<a class=clear-search href=\"{StoreQuery(filter, string.Empty, notOwned, sort, runeType, runeTier, currency, championTag)}\">Clear search</a>" : string.Empty;
        return $$"""
            <aside class=filter-panel>
              <form method=get action="/store/home">
                <input type=hidden name=kind value="{{kind}}">
                <section class=filter-block><button class=search-submit type=submit title="Search catalog"><span>Search</span></button>
                  <input id=store-search class=search-box type=text name=search aria-label="Search" value="{{encodedSearch}}" data-server-search="{{encodedSearch}}" data-focus="{{(focusSearch ? "1" : "0")}}" maxlength=64 autocomplete=off onkeyup="storeSearchChanged(this)">
                  <input type=hidden id=store-focus-search name=focusSearch value="">{{clear}}
                </section>
                <section class=filter-block><button class=filter-button type=submit title="Apply filters"><span>Filters</span></button>
                  {{options}}
                </section>
                <section class=filter-block><button class=sort-button type=submit title="Apply sort"><span>Sort By</span></button>
                  {{sortRows}}
                </section>
              </form>
            </aside>
            """;
    }

    // Why an item cannot be unlocked right now, or null when it can.
    private static string? Unavailability(PlatformStoreItem item, PlatformAccount account, PlatformEconomyState economy,
        PlatformStoreCatalog catalog)
    {
        // NOT RETAIL: "Unavailable" is the red label of the three bundles whose contents this server does not implement (PlatformStoreItem.Locked,
        // set in PlatformStoreCatalog.BuildProducts, refused by Purchase). Since 26 October 2026 (batch 341) every champion and skin plays, so these
        // are the only locked products. The other case is the XP boost at level 30 (IsUnavailable), which the client's own rules also refuse.
        if (item.Locked || IsUnavailable(item, economy)) return "Unavailable";
        if (IsOwned(item, account, economy, catalog)) return item.Kind == "RUNE" ? "Max quantity" : "Owned";
        if (IsBundleUnavailable(item, account, economy, catalog)) return "Requires room for 7 pages";
        if (item.Kind == "CHAMPION_SKIN" && !IsOwned(catalog.GetChampion(item.ChampionId), account, economy, catalog))
            return "Requires champion";
        return economy.Level < item.MinimumLevel ? $"Level {item.MinimumLevel}" : null;
    }

    // The Unlock button (94x22 art) or, for an item that cannot be unlocked, the red reason over a ghost button. It opens the unlock
    // dialog over the same view (/store/item/{id}?{state}); the item card stays behind it.
    private static string UnlockAction(PlatformStoreItem item, string? reason, string state, string cssClass = "card-action")
    {
        return reason is null
            ? $"<a class=\"{cssClass}\" href=\"{ItemHref(item, state)}\">Unlock</a>"
            : $"<span class=\"{cssClass} disabled{(reason is "Owned" or "Max quantity" ? " owned" : string.Empty)}\" aria-disabled=true><span>{WebUtility.HtmlEncode(reason)}</span></span>";
    }

    // The card's two hover halves. Upper: the client's own champion window (StoreController.openInventoryBrowser through the sandbox
    // bridge; ids "champions_<id>" and "championsskin_<globalSkinId>", the paths StoreController builds for the store). Lower: the unlock dialog.
    // Reconstruction: a skin card opens its champion's window at that skin (retail's skin behaviour is not recorded).
    // Owner decision 18 October 2026 (night): every card keeps its Unlock half, also one that cannot be bought right now (locked,
    // owned, champion missing, level too low, ...): the dialog it opens (RenderUnlockDialog) then states the reason, like the 2010
    // "Sorry" dialog, and nothing can be submitted from it. Before, such a card had no Unlock half and its refusal dialog was
    // reachable only from the Home or a direct link. The purchase itself is decided by POST /store/purchase alone.
    private static string HoverLinks(PlatformStoreItem item, string state)
    {
        var info = item.Kind switch
        {
            "CHAMPION" => $"<a class=card-info href=\"#\" onclick=\"return storeInfo('champions_{item.ContentId.ToString(CultureInfo.InvariantCulture)}','')\"><span class=mag></span></a>",
            "CHAMPION_SKIN" => $"<a class=card-info href=\"#\" onclick=\"return storeInfo('champions_{item.ChampionId.ToString(CultureInfo.InvariantCulture)}','championsskin_{item.ContentId.ToString(CultureInfo.InvariantCulture)}')\"><span class=mag></span></a>",
            _ => string.Empty,
        };
        return info + $"<a class=card-buy href=\"{ItemHref(item, state)}\">Unlock</a>";
    }

    // Anchor of an item's card (the grid scrolls back to it when the dialog closes).
    private static string CardId(string itemId) => "c-" + new string(itemId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray());

    private static string ItemHref(PlatformStoreItem item, string state) =>
        $"/store/item/{WebUtility.UrlEncode(item.Id)}?{WebUtility.HtmlEncode(state)}#{CardId(item.Id)}";

    // New and sale badges use the shipped stock art (New.png, sale_sunburst.png, hoticon2.png).
    private static string Badges(PlatformStoreItem item)
    {
        var badges = string.Empty;
        if (item.SalePercent > 0) badges += $"<span class=\"card-badge sale\">{item.SalePercent}%<br>OFF</span>";
        if (PlatformStoreCatalog.NewItemIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)) badges += "<span class=\"card-badge fresh\">NEW</span>";
        else if (PlatformStoreCatalog.TopSellerIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)) badges += "<span class=\"card-badge hot\"></span>";
        return badges;
    }

    private static string RenderProductRow(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog, bool lastColumn = false, string state = "", bool dialog = false)
    {
        if (item.Kind == "RUNE") return RenderRuneProductRow(item, account, economy, catalog, lastColumn, state, dialog);
        var imagePath = CardImage(item, catalog);
        var image = imagePath is { Length: > 0 }
            ? $"<img class=card-art src=\"{Encode(imagePath)}\" alt=\"\">"
            : "<span class=missing-art></span>";
        var cardClass = (item.Kind == "CHAMPION_SKIN" ? "product skin-card" : item.Kind == "CHAMPION" ? "product champion-card" : "product icon-card") +
            (lastColumn ? " last-column" : "");
        var reason = Unavailability(item, account, economy, catalog);
        if (dialog)
            return $"<article class=\"{cardClass}\"><div class=card-body>{image}<span class=card-title>{Encode(item.Name)}</span>{Badges(item)}<span class=card-footer><span class=price>{FormatPrice(item, economy)}</span></span></div></article>";
        var action = UnlockAction(item, reason, state);
        var searchText = Encode(SearchText(item, catalog));
        return $"<article id=\"{CardId(item.Id)}\" class=\"{cardClass}\" data-search=\"{searchText}\"><div class=card-body>{image}<span class=card-title title=\"{WebUtility.HtmlEncode(item.Name)}\">{WebUtility.HtmlEncode(item.Name)}</span>{Badges(item)}{HoverLinks(item, state)}<span class=card-footer>{action}<span class=price>{FormatPrice(item, economy)}</span></span></div></article>";
    }

    private static string RenderRuneProductRow(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog, bool lastColumn, string state, bool dialog)
    {
        var rune = catalog.RuneContent(item.ContentId);
        var imagePath = item.ImagePath is { Length: > 0 } path ? path : catalog.Artwork("rune-" + item.ContentId);
        var image = imagePath is { Length: > 0 }
            ? $"<img class=rune-art src=\"{Encode(imagePath)}\" alt=\"\">"
            : "<span class=missing-art></span>";
        var description = rune is null ? "Rune" : rune.Description;
        var reason = Unavailability(item, account, economy, catalog);
        var action = dialog ? string.Empty : UnlockAction(item, reason, state);
        var cardClass = "product rune-card rune-" + (rune?.Color.ToString().ToLowerInvariant() ?? "unknown") +
            (lastColumn ? " last-column" : string.Empty);
        return $"<article id=\"{CardId(item.Id)}\" class=\"{cardClass}\" data-search=\"{Encode(SearchText(item, catalog))}\"><div class=card-body><span class=rune-header><span class=card-title title=\"{Encode(item.Name)}\">{Encode(item.Name)}</span><span class=rune-effect>{Encode(description)}</span></span><span class=rune-image>{image}</span>{(dialog ? string.Empty : HoverLinks(item, state))}<span class=card-footer>{action}<span class=price>{FormatPrice(item, economy)}</span></span></div></article>";
    }

    // The shipped store portraits frame champions and skins for the 156x210 cards.
    private static string? CardImage(PlatformStoreItem item, PlatformStoreCatalog catalog) =>
        item.Kind == "CHAMPION" ? catalog.Artwork("champion-card-" + item.ContentId) ?? FeaturedImage(item, catalog) :
        item.Kind == "CHAMPION_SKIN" && item.ImagePath is { Length: > 0 } path
            ? AssetUrl(path) : FeaturedImage(item, catalog);

    private static string? FeaturedImage(PlatformStoreItem item, PlatformStoreCatalog catalog) =>
        item.Kind == "CHAMPION" ? catalog.Artwork("champion-" + item.ContentId) ??
            (item.ImagePath is { Length: > 0 } championPath ? AssetUrl(championPath) : null) :
        item.Kind == "CHAMPION_SKIN" ? catalog.Artwork("skin-" + item.ContentId) ??
            (item.ImagePath is { Length: > 0 } skinPath ? AssetUrl(skinPath) : null) :
        item.ImagePath is { Length: > 0 } path ? AssetUrl(path) : null;
}
