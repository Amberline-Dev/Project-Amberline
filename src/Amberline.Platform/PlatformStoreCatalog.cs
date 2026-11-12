using System.Globalization;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Platform;

/// <summary>
/// One store price.  Patch-126 prices were not recovered for every supported
/// product, so <see cref="HistoricallyVerified"/> is retained with the value
/// instead of presenting an emulator default as historical fact.
/// </summary>
public sealed record PlatformStorePrice(int? Ip, int? Rp, bool HistoricallyVerified = false);

/// <summary>A product exposed by the private patch-126 store surface.</summary>
public sealed record PlatformStoreItem(
    string Id,
    string Kind,
    int ContentId,
    int ChampionId,
    string Name,
    int? IpPrice,
    int? RpPrice,
    string? ImagePath,
    int MinimumLevel = 1,
    bool PriceHistoricallyVerified = false,
    string PriceNote = "Emulator default; patch price not recovered",
    bool Locked = false,
    int SalePercent = 0,
    int? RegularRpPrice = null);

/// <summary>
/// One sale that is live today, as the Store Home and the Home panel show it: <see cref="Percent"/> off the RP price of
/// <see cref="ItemIds"/> until the day <see cref="Expires"/> names. Built from <see cref="PlatformStoreCalendar"/> by
/// <see cref="PlatformStoreCatalog.ActiveSales"/>; it holds no price, so it cannot drift from what a purchase charges.
/// </summary>
public sealed record PlatformStoreSale(string Kind, string Title, int Percent, string Expires, string Body,
    string Button, IReadOnlyList<string> ItemIds);

/// <summary>
/// Prices: the stock client carries none (the AIR domain classes have no price), so they come from <see cref="PlatformStorePrices"/>,
/// the operator's price list or the neutral one: champions (IP and RP), skins (RP), regular runes (IP), boost packs (RP), the extra rune
/// page and the bundles. A price is <see cref="PlatformStorePrice.HistoricallyVerified"/> only when the list names the source of its
/// section (its label becomes the price note); a rune entry with a basis carries that basis as its note and is not marked verified.
/// The minimum levels are the tier gates 10 and 20 (see <see cref="RunePage.MinimumLevel"/>).
/// Read-only store catalog built from the champions of <see cref="ChampionCatalog"/> and the staged AIR presentation assets.
/// Economy and ownership remain account state; this class only resolves product identity, metadata and prices.
/// </summary>
public sealed partial class PlatformStoreCatalog
{
    public const string RunePageBundleId = "bundle:rune-pages-7";
    public const int RunePageBundleSize = 7;
    public const string IpTimedBoostId = "boost:ip-time";
    public const string XpTimedBoostId = "boost:xp-time";
    public const string IpWinBoostId = "boost:ip-wins";
    public const string XpWinBoostId = "boost:xp-wins";

    private readonly Dictionary<string, PlatformStoreItem> _byId;
    private readonly List<PlatformStoreItem> _products;
    private readonly TimeProvider _time;
    private readonly PlatformStoreCalendar _calendar;
    private readonly PlatformStorePrices _prices;
    private readonly string _storeImageRoot;
    private readonly IReadOnlyDictionary<string, PlatformStorePrice> _overrides;
    private readonly int _maximumRunePages;
    private readonly PlatformEconomyRules _rules;
    private readonly Dictionary<int, int> _runeQuantityLimits = [];

    public PlatformStoreCatalog(PlatformEconomyRules rules, string? airRoot = null, TimeProvider? time = null,
        PlatformStoreCalendar? calendar = null, PlatformStorePrices? prices = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
        _time = time ?? TimeProvider.System;
        _calendar = calendar ?? PlatformStoreCalendar.Default;
        _prices = prices ?? PlatformStorePrices.Default;

        // Native DATA supplies catalog text and rules. Staged AIR files supply
        // optional presentation art; no original assets are rewritten.
        var stagedRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(airRoot) ? AppContext.BaseDirectory : airRoot);
        _storeImageRoot = PlatformStore.StoreImageRoot(stagedRoot);
        _assetRoot = Path.GetDirectoryName(_storeImageRoot)!;
        _maximumRunePages = Math.Max(2, rules.MaximumRunePages);
        _overrides = (rules.Prices ?? new Dictionary<string, PlatformStorePrice>())
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        _products = BuildProducts();
        _byId = _products.ToDictionary(product => product.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every product on offer today with the sales of today applied (see <see cref="Live"/>); recomputed on each call. A windowed
    /// rune (see <see cref="PlatformStoreCalendar.RuneWindows"/>) is listed only inside its window.
    /// </summary>
    public IReadOnlyList<PlatformStoreItem> Items
    {
        get
        {
            var percents = SalePercents();
            var open = _calendar.OpenRuneIdsAt(_time.GetUtcNow());
            return _products.Where(product => Offered(product, open)).Select(product => Live(product, percents)).ToArray();
        }
    }

    /// <summary>
    /// Whether the product can be bought now. Everything but a windowed rune is always on offer; a windowed rune is on offer only
    /// while the calendar says its window is open. The clock is this catalog's own, read on every call, never a request value.
    /// </summary>
    private bool Offered(PlatformStoreItem product, HashSet<int> open) =>
        (!IsWindowProduct(product.ContentId, product.Kind) || open.Contains(product.ContentId)) && !Hidden(product);

    /// <summary>
    /// NOT RETAIL (batch 751): the host's "Offer skins and RP" switch is off (<see cref="PlatformControls.PaidContentOff"/>, read live on every
    /// call); retail always sold skins and took RP. While it is, no skin and nothing sold for RP alone is on offer (the listing,
    /// <see cref="TryGet"/> and the sales leave it out, as a closed limited rune; <see cref="TryGetKnown"/> still names it for a receipt), the
    /// rest carries no RP price (<see cref="Live"/>) and no sale is live, since a sale lowers only the RP price.
    /// </summary>
    public Func<bool> PaidContentOff { get; init; } = () => false;

    private bool Hidden(PlatformStoreItem product) => (product.Kind == "CHAMPION_SKIN" || product.IpPrice is null) && PaidContentOff();

    private bool IsWindowProduct(int contentId, string kind) => kind == "RUNE" && _calendar.WindowRuneIds.Contains(contentId);

    /// <summary>
    /// Whether the rune is one of the limited-edition runes that are sold only inside a calendar window. Unlike the 374 regular
    /// runes, an all-unlocked account does not own it: it has to be bought while its window is open.
    /// </summary>
    public bool IsWindowRune(int runeId) => _calendar.WindowRuneIds.Contains(runeId);

    public PlatformStoreItem Get(string id)
    {
        if (!TryGet(id, out var item))
            throw new ArgumentException($"Unknown store product '{id}'.", nameof(id));
        return item;
    }

    /// <summary>
    /// The product if it is on offer now. A windowed rune outside its window is not found, so a purchase of it is refused as an
    /// unknown product, whatever the request says.
    /// </summary>
    public bool TryGet(string? id, out PlatformStoreItem item)
    {
        item = null!;
        if (string.IsNullOrWhiteSpace(id) || !_byId.TryGetValue(id.Trim(), out var product) || Hidden(product)) return false;
        if (IsWindowProduct(product.ContentId, product.Kind) && !_calendar.OpenRuneIdsAt(_time.GetUtcNow()).Contains(product.ContentId))
            return false;
        item = Live(product, SalePercents());
        return true;
    }

    /// <summary>
    /// The product even when it is off offer (a windowed rune outside its window), for describing something already bought, such
    /// as a receipt of the Purchases page. It must never be used to decide whether something can be bought.
    /// </summary>
    public bool TryGetKnown(string? id, out PlatformStoreItem item)
    {
        item = null!;
        if (string.IsNullOrWhiteSpace(id) || !_byId.TryGetValue(id.Trim(), out var product)) return false;
        item = product;
        return true;
    }

    private Dictionary<string, int> SalePercents() => _calendar.PercentsAt(_time.GetUtcNow());

    /// <summary>
    /// The product as sold right now: while a calendar sale covers it, <see cref="PlatformStoreItem.RpPrice"/> is the sale
    /// price and <see cref="PlatformStoreItem.RegularRpPrice"/> the catalog price. The price is derived here from the clock
    /// and the calendar on every read, and purchases read it through <see cref="Get"/>, so no request can set it.
    /// A host price override (economy.json) wins over a sale. Locked products are priced too: they are shown on sale
    /// and stay unbuyable.
    /// </summary>
    private PlatformStoreItem Live(PlatformStoreItem product, Dictionary<string, int> percents)
    {
        if (PaidContentOff()) return product with { RpPrice = null };
        if (product.RpPrice is not > 0 || _overrides.ContainsKey(product.Id) ||
            !percents.TryGetValue(product.Id, out var percent)) return product;
        return product with
        {
            RpPrice = product.RpPrice.Value * (100 - percent) / 100,
            SalePercent = percent,
            RegularRpPrice = product.RpPrice,
        };
    }

    /// <summary>
    /// The sales live today for the Store Home and the Home panel, from the calendar and this catalog: one per announced
    /// sale, limited to products the catalog lists. Empty outside every window.
    /// </summary>
    public IReadOnlyList<PlatformStoreSale> ActiveSales()
    {
        var sales = new List<PlatformStoreSale>();
        if (PaidContentOff()) return sales;
        foreach (var active in _calendar.ActiveAt(_time.GetUtcNow()))
        {
            var entry = active.Entry;
            var ids = entry.Items.Where(item => _byId.TryGetValue(item.Id, out var product) && !Hidden(product)).Select(item => item.Id).ToArray();
            if (ids.Length == 0) continue;
            var names = ids.Select(id => _byId[id].Name).ToArray();
            var champions = entry.Kind == "CHAMPION";
            var listed = names.Length <= 4 ? JoinNames(names) : JoinNames([.. names.Take(3), $"{names.Length - 3} more"]);
            sales.Add(new PlatformStoreSale(entry.Kind,
                (entry.Event is null ? string.Empty : entry.Event.ToUpperInvariant() + " ") + (champions ? "CHAMPIONS SALE" : "SKINS SALE"),
                entry.Percent, $"Expires {active.Ends.ToString("dddd, MMMM", CultureInfo.InvariantCulture)} {Ordinal(active.Ends.Day)}",
                $"{listed} {(names.Length == 1 ? "is" : "are")} on sale for a limited time!",
                champions ? "Unlock These Champions" : "Unlock These Skins", ids));
        }
        return sales;
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count == 1 ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static string Ordinal(int day) => day + (day is >= 11 and <= 13 ? "th" : (day % 10) switch
    {
        1 => "st", 2 => "nd", 3 => "rd", _ => "th",
    });

    public PlatformStoreItem GetChampion(int championId) => Get($"champion:{championId}");

    public PlatformStoreItem GetSkin(int skinId) => Get($"skin:{skinId}");

    public PlatformStoreItem GetRune(int runeId) => Get($"rune:{runeId}");

    public PlatformStoreItem GetRunePage(int pageId) => Get($"rune_page:{pageId}");

    public static bool IsXpBoost(string? itemId) =>
        string.Equals(itemId, XpTimedBoostId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(itemId, XpWinBoostId, StringComparison.OrdinalIgnoreCase);

    public static bool TryGetBoost(string? itemId, out bool ip, out bool timed)
    {
        ip = timed = false;
        switch (itemId?.Trim().ToLowerInvariant())
        {
            case IpTimedBoostId: ip = timed = true; return true;
            case XpTimedBoostId: timed = true; return true;
            case IpWinBoostId: ip = true; return true;
            case XpWinBoostId: return true;
            default: return false;
        }
    }

    public int GetRuneQuantityLimit(int runeId) => _runeQuantityLimits.GetValueOrDefault(runeId, 9);
    public int MaximumRunePages => _maximumRunePages;

    /// <summary>Map the stock champion purchase path to a catalog product.</summary>
    public bool TryGetStockChampion(int championId, out PlatformStoreItem item) =>
        TryGet($"champion:{championId}", out item);

    /// <summary>Map the stock skin purchase path to a catalog product.</summary>
    public bool TryGetStockSkin(int globalSkinId, out PlatformStoreItem item) =>
        TryGet($"skin:{globalSkinId}", out item);

    private List<PlatformStoreItem> BuildProducts()
    {
        LoadChampionContent();
        var products = new List<PlatformStoreItem>();
        // The store lists all 84 champions of the patch with their skins, as retail did (owner decision 18 October 2026, like the
        // batch-65 champion grid). A champion this server could not play would be Locked, and PlatformAccounts.Purchase refuses it;
        // all 84 play since 26 October 2026 (batch 341), so nothing is locked and every product is buyable, as in retail.
        var championNote = _prices.Label("champions");
        foreach (var (championId, championName) in AirGameDtos.AllChampionNames.OrderBy(pair => pair.Key))
        {
            var locked = !AirGameDtos.ChampionNames.ContainsKey(championId);
            var ip = _prices.ChampionIp(championId) ?? throw new InvalidDataException($"The price list has no price for champion {championId}.");
            products.Add(Price($"champion:{championId}", "CHAMPION", championId, championId,
                ChampionContent(championId)?.Name ?? DisplayName("game_character_displayname_" + championName, championName),
                ip, _prices.ChampionRp(championId),
                ExistingImage($"content/champions/{championId}_Web_0.jpg"), locked: locked, historicallyVerified: championNote is not null,
                note: championNote));

            foreach (var skin in SkinProducts(championId, championName, locked))
                products.Add(skin);
        }

        foreach (var rune in RuneProducts())
            products.Add(rune);

        foreach (var boost in BoostProducts())
            products.Add(boost);

        // Seven additional rune pages (the 25 September 2011 store screenshot), at the price list's bundle price.
        var pageNote = _prices.Label("runePage");
        products.Add(Price(RunePageBundleId, "BUNDLE", RunePageBundleSize, 0,
            "7 Rune Page Bundle", null, BundleRp(RunePageBundleId),
            ExistingImage("content/bundles/runepages_bg.png"), minimumLevel: 1, historicallyVerified: pageNote is not null,
            note: pageNote));

        // The other three bundles of the 25 September 2011 Bundles screenshot (names as shown, art the shipped bundle icons, RP from the
        // price list). NOT RETAIL: their contents (champion and skin packs) are not implemented, so they are listed locked and
        // PlatformAccounts.Purchase refuses them.
        var bundleNote = _prices.Label("bundles");
        foreach (var (id, name, image) in new[]
        {
            ("bundle:gamers-choice-pack", "Gamer's Choice Pack", "d2d_bundle.png"),
            ("bundle:digital-collectors-pack", "Digital Collector's Pack", "collectors_bundle.png"),
            ("bundle:champions-bundle", "Champions Bundle", "champions_bundle.png"),
        })
            products.Add(Price(id, "BUNDLE", 0, 0, name, null, BundleRp(id), ExistingImage("content/bundles/" + image), locked: true,
                historicallyVerified: bundleNote is not null, note: bundleNote));

        // An additional rune page at the price list's IP or RP price. Retail allowed 8 extra pages (10 in all); the host's
        // MaximumRunePages rule (default ten) sets how many are sold. The price stays replaceable through PlatformEconomyRules.Prices.
        var maxPages = _maximumRunePages;
        for (var page = 3; page <= maxPages; page++)
        {
            var id = $"rune_page:{page}";
            products.Add(Price(id, "SPELL_BOOK_PAGE", page, 0, $"Rune Page {page}", _prices.RunePageIp, _prices.RunePageRp,
                ExistingImage("content/rune_pages/runepage_bg.png"), minimumLevel: 1, historicallyVerified: pageNote is not null,
                note: pageNote));
        }

        return products;
    }

    private int BundleRp(string id) => _prices.BundleRp(id) ?? throw new InvalidDataException($"The price list has no price for {id}.");

    // The four boost products follow the host's duration and win-count rules; each costs the RP price of the price list's pack of that
    // amount. NOT RETAIL: an amount the host configures that the list has no pack for is priced per unit.
    private IEnumerable<PlatformStoreItem> BoostProducts()
    {
        yield return Boost(IpTimedBoostId, 1, ip: true, timed: true, _rules.IpBoostDurationDays,
            $"IP Boost ({_rules.IpBoostDurationDays} day)", "content/boosts/ip_boost.png");
        yield return Boost(XpTimedBoostId, 2, ip: false, timed: true, _rules.XpBoostDurationDays,
            $"XP Boost ({_rules.XpBoostDurationDays} day)", "content/boosts/xp_boost.png");
        yield return Boost(IpWinBoostId, 3, ip: true, timed: false, _rules.IpBoostWinCount,
            $"IP Boost ({_rules.IpBoostWinCount} wins)", "content/boosts/ip_win_boost.png");
        yield return Boost(XpWinBoostId, 4, ip: false, timed: false, _rules.XpBoostWinCount,
            $"XP Boost ({_rules.XpBoostWinCount} wins)", "content/boosts/xp_win_boost.png");
    }

    private PlatformStoreItem Boost(string id, int content, bool ip, bool timed, int amount, string name, string image)
    {
        var rp = _prices.BoostRp(ip, timed, amount, out var listedPack) ?? throw new InvalidDataException($"The price list has no price for {id}.");
        var note = listedPack ? _prices.Label("boosts")
            : "Priced by us per unit of the nearest boost pack of the price list, which has no pack of this amount";
        return Price(id, "BOOST", content, 0, name, null, rp, ExistingImage(image), historicallyVerified: listedPack && note is not null,
            note: note);
    }

    private IEnumerable<PlatformStoreItem> SkinProducts(int championId, string championName, bool locked)
    {
        var skinNames = TrySkinNames(championName);
        var skinNote = _prices.Label("skins");
        var files = Directory.Exists(StoreAssetDirectory("content/skins"))
            ? CaseInsensitivePaths.EnumerateFiles(StoreAssetDirectory("content/skins"), $"championsskin_{championId}*.jpg")
            : Enumerable.Empty<string>();

        var ids = files.Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => name["championsskin_".Length..])
            .Where(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(text => int.Parse(text, CultureInfo.InvariantCulture))
            .Where(id => id / 1000 == championId && id % 1000 > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        foreach (var globalId in ids)
        {
            var localId = globalId - championId * 1000;
            var name = skinNames.TryGetValue(localId, out var displayName)
                ? displayName
                : DisplayName($"game_character_skin_displayname_{championName}_{localId}", $"{championName} Skin {localId}");
            // A skin the price list does not sell (in the 2011 list: Collector's Edition, event-only, seasonal or removed) is no product.
            if (_prices.SkinRp(globalId) is not { } skinRp) continue;
            yield return Price($"skin:{globalId}", "CHAMPION_SKIN", globalId, championId, name,
                null, skinRp, ExistingImage($"content/skins/championsskin_{globalId}.jpg"), locked: locked,
                historicallyVerified: skinNote is not null, note: skinNote);
        }
    }

    private IEnumerable<PlatformStoreItem> RuneProducts()
    {
        RuneCatalog? catalog = null;
        try
        {
            catalog = new RuneCatalog(GameData.Default.Paths);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            // Do not offer products whose shipped identity/tier is unavailable.
        }
        if (catalog is null) yield break;

        // NOT RETAIL: owner decision 18 October 2026 (evening): the store lists only the regular runes of October 2011
        // (catalog.All, 5001-5374; the Runes screenshot of 25 September 2011 shows only those) all year. The event runes
        // (8001-8034), the Razer runes (10001-10002), the 5000 placeholder and 5378 (no shipped record) are hidden:
        // they are no products here, so Purchase refuses them as unknown, and page saves refuse them too. The one exception
        // is below: the Harrowing 2011 runes, sold only inside their window (owner decision 18 October 2026, night).
        foreach (var rune in catalog.All)
            yield return RuneProduct(rune, catalog);

        // Owner decision 18 October 2026 (night): the limited-edition runes of an event were sold only during the event. The
        // calendar's rune windows (store/sales.json, runeWindows; the operator's 2011 calendar names the five Harrowing 2011
        // quintessences) name them. They are products here, but Items, TryGet and so Purchase see them only inside their window
        // (Pacific-time days, looped yearly like the sales). They follow the same tier gate as the others and sell for IP only at the
        // price their window lists, which only this server sets (never a request).
        // Appended after the regular runes so that the listing order of the 374 does not change.
        foreach (var id in _calendar.WindowRuneIds.Order())
            if (catalog.Get(id) is { } windowRune && !catalog.IsRegular(id))
                yield return RuneProduct(windowRune, catalog);
    }

    private PlatformStoreItem RuneProduct(RuneData rune, RuneCatalog catalog)
    {
        _runeContent[rune.Id] = rune;
        _runeQuantityLimits[rune.Id] = rune.Color == RuneColor.Black ? 3 : 9;
        var ip = rune.Tier switch
        {
            RuneTier.One => 15,
            RuneTier.Two => 30,
            _ => 80,
        };
        // A windowed rune sells at the IP price its window lists (see RuneProducts), a regular rune at the price list's; a list entry
        // with a basis carries that basis (a Reconstruction: price inferred from its class) as its note.
        var windowed = _calendar.WindowRuneIp(rune.Id);
        var listed = _prices.Rune(rune.Id);
        if (windowed is null && listed is not null)
            return Price($"rune:{rune.Id}", "RUNE", rune.Id, 0, rune.DisplayName, listed.Ip, null,
                RegisterRuneArtwork(rune, catalog.All), minimumLevel: RunePage.MinimumLevel(rune.Tier),
                historicallyVerified: listed.Basis is null,
                note: listed.Basis ?? _prices.Label("runes"));
        // NOT RETAIL: a rune the price list does not price (every rune under the neutral list) sells at its tier's default.
        var windowNote = windowed is null ? null : _calendar.WindowRuneNote(rune.Id);
        return Price($"rune:{rune.Id}", "RUNE", rune.Id, 0, rune.DisplayName, windowed ?? ip, null,
            RegisterRuneArtwork(rune, catalog.All), minimumLevel: RunePage.MinimumLevel(rune.Tier),
            historicallyVerified: windowNote is not null, note: windowNote);
    }

    private PlatformStoreItem Price(string id, string kind, int contentId, int championId, string name,
        int? defaultIp, int? defaultRp, string? imagePath, int minimumLevel = 1,
        bool historicallyVerified = false, bool locked = false, string? note = null)
    {
        var hasOverride = _overrides.TryGetValue(id, out var configured);
        var ip = hasOverride ? configured!.Ip : defaultIp;
        var rp = hasOverride ? configured!.Rp : defaultRp;
        var verified = hasOverride ? configured!.HistoricallyVerified : historicallyVerified;
        var priceNote = hasOverride ? "Configured by the host economy rules" : note ?? "Neutral default: no sourced price list entry";
        // A sale's printed regular prices are never used: the price list alone prices a product (a check compares the two).
        return new PlatformStoreItem(id, kind, contentId, championId, name, ip, rp, imagePath,
            minimumLevel, verified, priceNote, locked);
    }

    /// <summary>
    /// Reconstruction: what patch 1.0.0.126 added to the store on 5 October 2011 (the Xerath release: the champion and the
    /// skins Runeborn Xerath, Battlecast Xerath, Cryocore Brand and Officer Caitlyn). The Store Home lists them under
    /// "What's New" with a New badge. The Sep 25 screenshots fill the same panel with the then-newest items.
    /// </summary>
    public static readonly IReadOnlyList<string> NewItemIds =
        ["champion:101", "skin:101001", "skin:101002", "skin:63003", "skin:51005"];

    /// <summary>
    /// Reconstruction: the "Top Sellers" panel. The server has no sales data; like the 25 September screenshot, where the
    /// list is the newest champion with its skins and the newest skins, it lists the newest items.
    /// </summary>
    public static readonly IReadOnlyList<string> TopSellerIds =
        ["champion:101", "skin:101001", "skin:101002", "skin:63003", "skin:51005", "champion:92"];

    private string? ExistingImage(string relative)
    {
        var path = StoreAssetPath(relative);
        return File.Exists(path) ? relative.Replace('\\', '/') : null;
    }

    private string StoreAssetDirectory(string relative) => StoreAssetPath(relative);

    private string StoreAssetPath(string relative) => CaseInsensitivePaths.Resolve(Path.Combine(_storeImageRoot,
        relative.Replace('/', Path.DirectorySeparatorChar)));

    private static IReadOnlyDictionary<int, string> TrySkinNames(string champion)
    {
        try
        {
            return GameData.Default.Skins(champion).Where(skin => skin.Id > 0)
                .ToDictionary(skin => skin.Id, skin => skin.DisplayName);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            return new Dictionary<int, string>();
        }
    }
}
