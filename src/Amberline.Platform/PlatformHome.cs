using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Amberline.Platform;

/// <summary>One News row or feed item. <see cref="Thumb"/> is the row's same-origin picture (the stock store backdrop when absent).</summary>
public sealed record PlatformHomeNotice(string Title, string Description,
    DateTimeOffset? PublishedAt = null, DateTimeOffset? ExpiresAt = null, string? Link = null, string? Thumb = null);

public sealed record PlatformHomeModel(string ClientIdentity, IReadOnlyList<string> ChampionNames,
    IReadOnlyList<PlatformHomeNotice> Notices, bool CustomGamesEnabled, int OpenPracticeGames)
{
    public static PlatformHomeModel Testing(int openPracticeGames = 0) => new(
        "1.45.11_10_04_15_28", AirGameDtos.ChampionNames.Values.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        [], true, Math.Max(0, openPracticeGames));
}

/// <summary>
/// The PvP.net landing page (<c>lobbyLandingURL</c>), the Journal page (<c>/story</c>), the RSS feeds and the server-status payload
/// shown inside the stock client's HTML frames. The client protocol does not depend on their content: the stock client only loads the
/// URLs and injects <c>airHostNavigate</c> (LobbyLanding.as): "store" opens the store at a product (the unlock dialog), "inventory"
/// opens the client's own champion window.
/// NOT RETAIL: what the pages say is operator content (<see cref="PlatformContent"/>, owner decision of 1 November 2026). Retail's
/// pages were Riot's website, which never shipped in the client; the operator's <c>home/</c> folder supplies the landing template
/// (<c>landing.html</c>), the Journal (<c>story.html</c>), the News rows and feed items (<c>home.json</c>) and the page images, and
/// without it the Platform serves the neutral built-in pages below. The template's tokens: <c>{{friz-face-css}}</c>,
/// <c>{{friz-stack}}</c>, <c>{{news}}</c>, <c>{{featured[:item]}}</c>, <c>{{on-sale}}</c>, <c>{{buttons:item}}</c>.
/// </summary>
public static partial class PlatformHome
{
    private sealed record HomeFeed(string? Title, PlatformHomeNotice[]? Items);

    /// <summary>The operator's <c>home/home.json</c>: the News rows, the feeds by name and the feeds' description.</summary>
    private sealed record HomeContent(PlatformHomeNotice[]? News, string? FeedDescription, Dictionary<string, HomeFeed>? Feeds);

    private static HomeContent Content() => PlatformContent.Read("home/home.json") is { } json
        ? JsonSerializer.Deserialize<HomeContent>(json, JsonSerializerOptions.Web) ?? throw new InvalidDataException("home/home.json is empty.")
        : new(null, null, null);

    private static readonly PlatformHomeNotice[] NeutralNews =
        [new("Welcome", "Create a custom game, join a queue against other summoners or bots, or visit the store.")];

    private static readonly Regex Token = new(@"\{\{([a-z-]+)(?::([^}]*))?\}\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Names of the operator's page images (served by MapImages); never a path.
    private static readonly Regex BundledName = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\.(png|jpg|gif)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ContentType(string file) =>
        file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" :
        file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image/jpeg";

    /// <summary>
    /// Self-hosted page art: <c>/img/home/{key}</c>. Allow-list: the images of the operator's <c>home/</c> folder (a plain file name
    /// with a png/jpg/gif extension, no separators, looked up only in that folder; <c>archive-</c> prefix accepted), the champion
    /// portraits and splashes of the shipped AIR images, and two stock backgrounds. Anything else is 404.
    /// </summary>
    public static void MapImages(WebApplication app, string airRoot)
    {
        var bundled = PlatformContent.Home;
        // The store frame maps /img/ to the client's own assets (parentFrame.html sandboxRoot), so its pages reach the operator's art
        // on this separate path; same allow-list, same folder.
        app.MapGet("/store/img/home/{key}", (string key) =>
        {
            var name = key.StartsWith("archive-", StringComparison.Ordinal) ? key["archive-".Length..] : key;
            return BundledName.IsMatch(name) && PlatformStore.IsSafeAssetPath(name, bundled, out var recovered)
                ? Results.File(recovered, ContentType(name)) : Results.NotFound();
        });
        app.MapGet("/img/home/{key}", (string key) =>
        {
            var name = key.StartsWith("archive-", StringComparison.Ordinal) ? key["archive-".Length..] : key;
            if (BundledName.IsMatch(name) && PlatformStore.IsSafeAssetPath(name, bundled, out var recovered))
                return Results.File(recovered, ContentType(name));
            string? relative = key switch
            {
                "background" => "backgrounds/pvp_landingBackground.jpg",
                "parchment" => "backgrounds/parchment.png",
                _ => null
            };
            var parts = key.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                AirGameDtos.AllChampionNames.TryGetValue(id, out var champion))
                relative = parts[0] switch
                {
                    "portrait" => $"champions/{champion}_Square_0.png",
                    "feature" => $"champions/{champion}_Splash_0.jpg",
                    _ => null
                };
            if (relative is null || !PlatformStore.IsSafeAssetPath(relative, Path.Combine(airRoot, "assets", "images"), out var file))
                return Results.NotFound();
            return Results.File(file, ContentType(file));
        });
    }

    /// <summary>
    /// The Home: the operator's <c>home/landing.html</c> with its tokens filled, or the neutral page. The News rows are the model's
    /// notices, else the operator's, else one welcome row; the panels come from the store catalog and the sales live today.
    /// </summary>
    public static string RenderLanding(PlatformHomeModel? model = null, PlatformStoreCatalog? catalog = null)
    {
        model ??= PlatformHomeModel.Testing();
        var notices = model.Notices.Count > 0 ? model.Notices : Content().News ?? NeutralNews;
        return Token.Replace(PlatformContent.Read("home/landing.html") ?? NeutralLanding, token => token.Groups[1].Value switch
        {
            "friz-face-css" => PlatformStore.FrizFaceCss,
            "friz-stack" => PlatformStore.FrizStack,
            "news" => string.Join("", notices.Take(12).Select(RenderNotice)),
            "featured" => RenderFeatured(catalog, token.Groups[2].Success ? token.Groups[2].Value : PlatformStoreCatalog.NewItemIds[0]),
            "on-sale" => RenderOnSale(catalog),
            "buttons" => RenderButtons(catalog, token.Groups[2].Value),
            _ => token.Value,
        });
    }

    /// <summary>The Journal: the operator's static <c>home/story.html</c>, or the neutral page.</summary>
    public static string RenderStory() => PlatformContent.Read("home/story.html") ?? NeutralStory;

    // The store path StoreController builds for a product (LobbyLanding "store" action) and the champion window ids it parses.
    private static string StorePath(PlatformStoreItem item) => item.Kind == "CHAMPION"
        ? $"/store/items/view_item/Champion/champions_{item.ContentId.ToString(CultureInfo.InvariantCulture)}"
        : $"/store/items/view_item/Skin/championsskin_{item.ContentId.ToString(CultureInfo.InvariantCulture)}";

    private static string InfoCall(PlatformStoreItem item) => item.Kind == "CHAMPION"
        ? $"return info('champions_{item.ContentId.ToString(CultureInfo.InvariantCulture)}','')"
        : $"return info('champions_{item.ChampionId.ToString(CultureInfo.InvariantCulture)}','championsskin_{item.ContentId.ToString(CultureInfo.InvariantCulture)}')";

    private static string ItemIcon(PlatformStoreCatalog catalog, PlatformStoreItem item)
    {
        var key = item.Kind == "CHAMPION" ? "champion-card-" + item.ContentId : "skin-" + item.ContentId;
        var art = catalog.Artwork(key);
        return art is null ? "<span class=ico></span>"
            : $"<span class=\"ico{(item.Kind == "CHAMPION" ? string.Empty : " wide")}\" style=\"background-image:url('{WebUtility.HtmlEncode(art)}')\"></span>";
    }

    private static string RpLine(PlatformStoreItem item)
    {
        if (item.RpPrice is not int rp) return string.Empty;
        var price = rp.ToString("N0", CultureInfo.InvariantCulture);
        var old = item.SalePercent > 0 && item.RegularRpPrice is int regular
            ? $"<s>{regular.ToString("N0", CultureInfo.InvariantCulture)}</s> " : string.Empty;
        var off = item.SalePercent > 0 ? $" <em>({item.SalePercent}% OFF)</em>" : string.Empty;
        return $"<img src=\"/store/img/layout/rp_logo2.png\" alt=\"\">{old}{price}{off}";
    }

    // FEATURED ITEM: a store product (by default the newest, PlatformStoreCatalog.NewItemIds), priced by the catalog; its Unlock button
    // opens the store's unlock dialog for it.
    private static string RenderFeatured(PlatformStoreCatalog? catalog, string id)
    {
        if (catalog is null || !catalog.TryGet(id, out var item)) return string.Empty;
        var subtitle = catalog.ChampionContent(item.ChampionId)?.Title ?? string.Empty;
        return $"<div id=feat class=pnl><h2>Featured Item</h2>{ItemIcon(catalog, item)}<span class=inm>{WebUtility.HtmlEncode(item.Name)}</span>" +
            $"<span class=isub>{WebUtility.HtmlEncode(subtitle)}</span><span class=ipr>{RpLine(item)}</span>" +
            $"<a class=btn style=\"left:98px\" href=\"#\" onclick=\"return store('{StorePath(item)}')\">Unlock</a></div>";
    }

    // ON SALE ITEM: the first item of the sale the calendar has live today (champion sales first); absent outside every window.
    private static string RenderOnSale(PlatformStoreCatalog? catalog)
    {
        if (catalog is null) return string.Empty;
        var sale = catalog.ActiveSales().OrderBy(entry => entry.Kind == "CHAMPION" ? 0 : 1).FirstOrDefault();
        if (sale is null) return string.Empty;
        foreach (var id in sale.ItemIds)
        {
            if (!catalog.TryGet(id, out var item) || item.SalePercent <= 0) continue;
            return $"<div id=sale class=pnl><h2>On Sale Item</h2>{ItemIcon(catalog, item)}<span class=inm>{WebUtility.HtmlEncode(item.Name)}</span>" +
                $"<span class=ipr>{RpLine(item)}</span>" +
                $"<a class=btn style=\"left:98px\" href=\"#\" onclick=\"{InfoCall(item)}\">Info</a>" +
                $"<a class=btn style=\"left:196px\" href=\"#\" onclick=\"return store('{StorePath(item)}')\">Unlock</a></div>";
        }
        return string.Empty;
    }

    // The Info and Unlock pair of a video caption row (the champion window and the store's unlock dialog for the product).
    private static string RenderButtons(PlatformStoreCatalog? catalog, string id) => catalog is not null && catalog.TryGet(id, out var item)
        ? $"<a class=btn style=\"left:186px;width:88px\" href=\"#\" onclick=\"{InfoCall(item)}\">Info</a>" +
          $"<a class=btn style=\"left:283px\" href=\"#\" onclick=\"return store('{StorePath(item)}')\">Unlock</a>"
        : string.Empty;

    /// <summary>
    /// The client's RSS feeds (<c>rssFeedJoJURL</c>, <c>rssFeedStoreURL</c>): the model's notices, else the operator's dated items of
    /// that feed (none in the neutral default). RSSParser hands the newest item to the client's own notice bubble (RSSAlertController).
    /// </summary>
    public static string RenderRss(string feed, PlatformHomeModel? model = null)
    {
        model ??= PlatformHomeModel.Testing();
        var normalized = (feed ?? "").Trim().ToLowerInvariant();
        var content = Content();
        var configured = content.Feeds?.GetValueOrDefault(normalized);
        var title = configured?.Title ?? normalized switch
        {
            "joj" => "Journal",
            "store" => "Store",
            _ => "Local testing platform"
        };
        IReadOnlyList<PlatformHomeNotice> notices = model.Notices.Count > 0 ? model.Notices : configured?.Items ?? [];
        // Keep the items' own publication dates; RSSParser accepts an absent expireDate.
        var items = notices.Where(item => item.PublishedAt is not null).Select(item =>
        {
            var element = new XElement("item",
                new XElement("title", item.Title), new XElement("description", item.Description),
                new XElement("pubDate", item.PublishedAt!.Value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)),
                new XElement("guid", item.Link ?? "local-platform:" + item.Title));
            if (!string.IsNullOrWhiteSpace(item.Link)) element.Add(new XElement("link", item.Link));
            if (item.ExpiresAt is { } expires)
                element.Add(new XElement("expireDate", expires.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)));
            return element;
        });
        var channel = new XElement("channel", new XElement("title", title),
            new XElement("link", normalized == "joj" ? "/story" : "/landing"),
            new XElement("description", model.Notices.Count == 0 ? content.FeedDescription ?? "Server notices." : "Local testing-platform notices."), items);
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("rss", new XAttribute("version", "2.0"), channel)).ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyDictionary<string, object?> StatusPayload(IEnumerable<string>? messages = null)
    {
        var rows = (messages ?? Array.Empty<string>()).Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => (object)new Dictionary<string, object?> { ["text"] = text.Trim() }).ToArray();
        return new Dictionary<string, object?>
        {
            ["messagesContainer"] = new[] { new Dictionary<string, object?> { ["messages"] = rows } }
        };
    }

    // One row of the News list: thumbnail, blue title (an http(s) link, opened in the system browser) and the text.
    private static string RenderNotice(PlatformHomeNotice notice)
    {
        var date = notice.PublishedAt?.ToString("MM/dd", CultureInfo.InvariantCulture);
        var title = WebUtility.HtmlEncode(notice.Title);
        var text = WebUtility.HtmlEncode(notice.Description);
        var link = notice.Link is { } raw && Uri.TryCreate(raw, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
            ? parsed.AbsoluteUri : null;
        var thumb = $"<span class=nth style=\"background-image:url('{WebUtility.HtmlEncode(notice.Thumb ?? "/store/img/layout/home_bg.jpg")}')\"></span>";
        var heading = link is not null
            ? $"<a class=title href=\"{WebUtility.HtmlEncode(link)}\" title=\"{title}\">{title}</a>"
            : $"<span class=title title=\"{title}\">{title}</span>";
        return $"<li>{thumb}{heading}<span class=date>{date}</span><span class=text>{text}</span></li>";
    }

    // The neutral Home: the News list and the store's Featured and On Sale panels, in the panel style of the stock client's frame.
    private const string NeutralLanding = """
        <!doctype html><html lang=en><head><meta charset=utf-8><title>Home</title><style>
        {{friz-face-css}}
        html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#04070d;color:#d4d8e1;font:12px Arial,Helvetica,sans-serif}
        body{background:#04070d url('/img/home/background') center top no-repeat;-webkit-background-size:100% 100%;background-size:100% 100%;text-align:left}
        a{color:#3f94e0;text-decoration:none}a:hover{color:#7cc0f5}img{border:0}ul{margin:0;padding:0;list-style:none}
        #home{position:absolute;left:0;top:0;width:1262px;height:670px}
        .pnl{position:absolute;background:#07152a;background:rgba(6,20,40,.94);border:1px solid #1b3e6a;-webkit-border-radius:5px;border-radius:5px}
        .pnl h2{margin:0;height:25px;padding:0 10px;color:#daeffe;font:normal 12px/25px {{friz-stack}};text-transform:uppercase;letter-spacing:.5px}
        #news{left:20px;top:14px;width:606px;height:640px}
        #list{position:absolute;left:8px;right:8px;top:30px;bottom:8px;border:1px solid #1b3e6a;background:#040d1c;overflow-y:auto;overflow-x:hidden}
        #list li{position:relative;height:88px;border-bottom:1px solid #0d2342}
        #list .nth{position:absolute;left:12px;top:9px;width:98px;height:70px;border:1px solid #1b3e6a;background:#000 no-repeat 50% 50%;-webkit-background-size:auto 70px;background-size:auto 70px}
        #list .title{position:absolute;left:128px;right:60px;top:10px;height:18px;overflow:hidden;white-space:nowrap;font:14px/18px Arial,sans-serif;color:#3f94e0}
        #list .date{position:absolute;right:12px;top:12px;color:#5f7a99;font-size:11px}
        #list .text{position:absolute;left:128px;right:16px;top:34px;height:38px;overflow:hidden;color:#8fa7c2;font:12px/19px Arial,sans-serif}
        #side{position:absolute;left:648px;top:14px;width:391px}#side .pnl{position:relative;margin:0 0 14px}#feat{height:140px}#sale{height:150px}
        .ico{position:absolute;left:12px;top:34px;width:70px;height:70px;border:2px solid #1e4272;background:#000 no-repeat 50% 0;-webkit-background-size:70px auto;background-size:70px auto}
        .ico.wide{-webkit-background-size:auto 70px;background-size:auto 70px;background-position:50% 50%}
        .inm{position:absolute;left:98px;right:12px;top:36px;color:#fff;font:bold 15px/18px Arial,sans-serif;white-space:nowrap;overflow:hidden}
        .isub{position:absolute;left:98px;right:12px;top:57px;color:#6c94bb;font:11px/14px Arial,sans-serif;white-space:nowrap;overflow:hidden}
        .ipr{position:absolute;left:98px;top:78px;color:#fff;font:12px/16px Arial,sans-serif;white-space:nowrap}
        .ipr img{width:13px;height:13px;vertical-align:-2px;margin-right:4px}.ipr s{color:#7a8798}.ipr em{font-style:normal;color:#3f94e0}#sale .ipr{top:62px}
        .btn{position:absolute;display:block;width:90px;height:25px;line-height:25px;border:1px solid #3a78c0;background:#164a86;-webkit-border-radius:2px;border-radius:2px;color:#fff;font:12px/25px {{friz-stack}};text-transform:uppercase;text-align:center;letter-spacing:.5px}
        .btn:hover{color:#fff;background:#1f66b3}#feat .btn{top:104px}#sale .btn{top:112px}
        </style></head><body><div id=home>
        <div id=news class=pnl><h2>News</h2><ul id=list>{{news}}</ul></div>
        <div id=side>{{featured}}{{on-sale}}</div>
        </div>
        <script type="text/javascript">
        function airNav(o){try{if(window.airHostNavigate){window.airHostNavigate(JSON.stringify(o));} }catch(e){}return false;}
        function store(relativeUrl){return airNav({type:'store',relativeUrl:relativeUrl,queryString:''});}
        function info(item,skin){return airNav({type:'inventory',fullItemId:item,skinId:skin||''});}
        </script></body></html>
        """;

    // The neutral Journal (StoryView opens every link of the page in the system browser; this one has none).
    private const string NeutralStory = """
        <!doctype html><html lang=en><head><meta charset=utf-8><title>Journal</title><style>
        html,body{margin:0;padding:0;background:#04070d;color:#d4d8e1;font:14px Arial,Helvetica,sans-serif}
        main{padding:40px}h1{margin:0 0 12px;color:#daeffe;font:normal 20px Arial,Helvetica,sans-serif}
        </style></head><body><main><h1>Journal</h1><p>No issue of the Journal has been published on this server.</p></main></body></html>
        """;
}
