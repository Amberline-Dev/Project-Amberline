using System.Globalization;
using System.Net;
using Amberline.Data;
using Microsoft.AspNetCore.Http;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the retail store was a Riot-hosted web application (no usable archive of its HTML was recovered), so
/// this page, its catalog layout, filters and the RP "purchase" information are a reconstruction. What the client
/// itself fixes is kept: the URL from <c>loginService.getStoreUrl</c>, the <c>/store/...</c> paths and
/// <c>parentSandboxBridge</c> hooks <c>StoreController</c> uses, and the fulfillment and balance notifications that
/// <c>Inventory</c> consumes. Payment never happens; the host grants RP.
/// Small same-origin store surface for the stock AIR store frame.  The page is
/// intentionally server-rendered: the preserved WebKit can submit ordinary
/// forms, while ownership, balance and purchase idempotency stay in the
/// account service.
/// </summary>
public static partial class PlatformStore
{
    public const string StoreCookie = "Amberline.Store";
    public const string StorePath = "/store";

    private const string FulfillmentAlias = AirProtocolNames.StoreFulfillmentNotification;
    private const string ChampionAlias = AirProtocolNames.ChampionDTO;
    private const string SkinAlias = AirProtocolNames.ChampionSkinDTO;
    private const string RuneQuantityAlias = AirProtocolNames.RuneQuantity;

    /// <summary>Register the ticket, HTML, image and purchase routes.</summary>
    public static void Map(WebApplication app, PlatformAccounts accounts, PlatformSessions sessions,
        string airRoot, Action<long, object>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(sessions);
        if (string.IsNullOrWhiteSpace(airRoot)) throw new ArgumentException("The AIR root is required.", nameof(airRoot));

        var stagedRoot = Path.GetFullPath(airRoot);
        var imageRoot = StoreImageRoot(stagedRoot);
        // ponytail: serialize fulfillment for this small host; account transactions
        // already serialize writes. This also orders incremental rune pushes.
        var purchaseGate = new object();

        // An ordinary store click uses the origin-only canonical URL. Redirect
        // into /store so its scoped cookie is available on repeat visits.
        app.MapGet("/", (HttpContext context) => Results.Redirect("/store/entry" + context.Request.QueryString));
        app.MapGet("/store/entry", (HttpContext context) =>
        {
            var access = OpenStoreSession(context, sessions);
            context.Response.Cookies.Append(StoreCookie, access.Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = StorePath,
                MaxAge = access.Expires - DateTimeOffset.UtcNow,
            });
            return Results.Redirect("/store/home");
        });

        app.MapGet("/store/home", (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            return Results.Content(RenderView(context.Request.Query, access, accounts), "text/html; charset=utf-8");
        });

        app.MapGet("/store/purchases", (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            return Results.Content(RenderPurchases(access, accounts), "text/html; charset=utf-8");
        });

        app.MapGet("/store/rp", (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            var account = accounts.Get(access.AccountId);
            return Results.Content(RenderRpInformation(account), "text/html; charset=utf-8");
        });

        // The unlock dialog over the view it was opened from (the card links carry the view's filters). No product page exists: the
        // 2011 store opened the client's own champion window for information (see HoverLinks) and this dialog for Unlock.
        app.MapGet("/store/item/{id}", (HttpContext context, string id) =>
        {
            var access = RequireOrOpen(context, sessions);
            return Results.Content(RenderView(context.Request.Query, access, accounts, id), "text/html; charset=utf-8");
        });

        // These are the relative paths emitted by StoreController in the preserved AIR client (the Home's Unlock buttons use them
        // through airHostNavigate "store"). The unmodified SWF loads one directly: it opens the unlock dialog over that item's list.
        app.MapGet("/store/items/view_item/{kind}/{file}", (HttpContext context, string kind, string file) =>
        {
            var id = StockProductId(kind, file);
            if (id is null) return Results.NotFound();
            var access = RequireOrOpen(context, sessions);
            return Results.Content(RenderView(context.Request.Query, access, accounts, id, listOfItemKind: true), "text/html; charset=utf-8");
        });

        app.MapPost("/store/purchase", async (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var formToken = form["formToken"].ToString();
            if (!PlatformSessions.SecretEquals(access.FormToken, formToken))
                throw new ArgumentException("The store form has expired. Reload the item.");

            var itemId = form["itemId"].ToString().Trim();
            var currency = form["currency"].ToString().Trim().ToUpperInvariant();
            var requestId = form["requestId"].ToString().Trim();
            var quantityText = form["quantity"].ToString().Trim();
            // The unit price the page showed. It is only compared with the server's own price (a sale may have begun or
            // ended); it never sets one.
            var shownText = form["expectedPrice"].ToString().Trim();
            // The view the purchase was made from. It only decides which page the response shows and where Done returns to: it is
            // re-read through the page's own normalizers (known keys only), never echoed, and it never reaches the purchase.
            var view = ParseView(form["view"].ToString());
            long? shownPrice = null;
            if (shownText.Length > 0)
                shownPrice = long.TryParse(shownText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPrice)
                    ? parsedPrice : throw new ArgumentException("The store purchase request is invalid.");
            var quantity = quantityText.Length == 0 ? 1 :
                int.TryParse(quantityText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedQuantity)
                    ? parsedQuantity : 0;
            if (itemId.Length is < 3 or > 96 || requestId.Length is < 16 or > 96 ||
                !Guid.TryParse(requestId, out var requestGuid) || requestGuid == Guid.Empty || quantity is < 1 or > 9)
                throw new ArgumentException("The store purchase request is invalid.");
            if (currency is not ("IP" or "RP")) throw new ArgumentException("The store currency is invalid.");

            // Purchase performs catalog resolution, ownership checks, balance
            // debit and request-id replay handling in one account-store write.
            lock (purchaseGate)
            {
            var before = accounts.Get(access.AccountId);
            var wasReplay = PlatformEconomy.For(before).Purchases?.Any(purchase => purchase.RequestId == requestGuid) == true;
            var purchase = accounts.Purchase(access.AccountId, itemId, currency, requestGuid, quantity, shownPrice);
            var account = accounts.Get(access.AccountId);
            // The purchase above is already decided (made, or the recorded receipt of a replay). A limited-edition rune is no longer on
            // offer once its window closed, so a replay (or a purchase on the last second) names it from the known products instead.
            var item = accounts.StoreCatalog.TryGet(itemId, out var offered) ? offered
                : accounts.StoreCatalog.TryGetKnown(itemId, out var known) ? known : accounts.StoreCatalog.Get(itemId);
            var economy = PlatformEconomy.For(account);
            if (!wasReplay && notify is not null)
            {
                notify(account.Id, Fulfillment(item, account, economy, purchase.Quantity));
                notify(account.Id, AirDtos.AccountBalanceNotification(account));
            }

            return Results.Content(RenderPurchaseComplete(access, accounts, item, purchase, view), "text/html; charset=utf-8");
            }
        });

        MapRewardCodeRoutes(app, accounts, sessions, purchaseGate, notify);

        app.MapGet("/img/{**path}", (string? path) => ServeImage(imageRoot, path));
        // Some store builds resolve images relative to the store frame itself.
        app.MapGet("/store/img/{**path}", (string? path) => ServeImage(imageRoot, path));
        // /img is remapped to app:/assets/storeImages by the stock parent frame.
        // Art elsewhere in AIR must use a separate, catalog-bound HTTP route.
        app.MapGet("/store/art/{key}", (string key) => accounts.StoreCatalog.ArtworkFile(key) is { } path
            ? (IResult)Results.File(path, Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg")
            : Results.NotFound());
    }

    /// <summary>
    /// The store page for a request's view parameters (the ones <see cref="StoreParams"/> writes), optionally with the unlock dialog
    /// of one item over it. <paramref name="listOfItemKind"/>: with no filter given, show the list of the item's own kind behind the
    /// dialog (the stock view_item path) instead of the store home.
    /// </summary>
    private static string RenderView(Microsoft.AspNetCore.Http.IQueryCollection query, PlatformSessions.StoreSession access,
        PlatformAccounts accounts, string? dialogItemId = null, bool listOfItemKind = false, string? overlay = null, string? focusCard = null)
    {
        var kind = query["kind"].ToString();
        if (listOfItemKind && kind.Length == 0 && dialogItemId is not null && accounts.StoreCatalog.TryGet(dialogItemId, out var item))
            kind = item.Kind;
        var requestedPage = int.TryParse(query["page"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : 1;
        return RenderHome(access, accounts, kind, requestedPage, query["search"].ToString(),
            query["notOwned"].ToString() is "1" or "true", query["sort"].ToString(), query["runeType"].ToString(),
            ParseRuneTier(query["runeTier"].ToString()), query["currency"].ToString(), query["tag"].ToString(),
            query["focusSearch"].ToString() is "1" or "true", query["sale"].ToString() is "1" or "true",
            query["championOwned"].ToString() is "1" or "true", dialogItemId, overlay, focusCard);
    }

    /// <summary>
    /// The view parameters a purchase form carried back (the dialog's <c>state</c>), or null when absent or too long. Only the keys the
    /// store pages read are ever taken from it, each through the same normalizer as a page request (<see cref="ViewState"/>).
    /// </summary>
    private static Microsoft.AspNetCore.Http.IQueryCollection? ParseView(string? view)
    {
        if (string.IsNullOrWhiteSpace(view) || view.Length > 512) return null;
        return new Microsoft.AspNetCore.Http.QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(view));
    }

    /// <summary>The normalized, URL-encoded view parameters of a request (what <see cref="RenderHome"/> carries as its state).</summary>
    private static string ViewState(Microsoft.AspNetCore.Http.IQueryCollection query) => StoreParams(
        NormalizeKind(query["kind"].ToString()), NormalizeSearch(query["search"].ToString()),
        query["notOwned"].ToString() is "1" or "true", NormalizeSort(query["sort"].ToString()),
        NormalizeRuneType(query["runeType"].ToString()), NormalizeRuneTier(ParseRuneTier(query["runeTier"].ToString())),
        NormalizeCurrency(query["currency"].ToString()), NormalizeChampionTag(query["tag"].ToString()),
        query["sale"].ToString() is "1" or "true", query["championOwned"].ToString() is "1" or "true");

    /// <summary>The unlock dialog of one item over the default view (kept for callers that have no request).</summary>
    public static string RenderProduct(PlatformSessions.StoreSession access, PlatformAccounts accounts, string id) =>
        RenderHome(access, accounts, dialogItemId: id);

    public static string RenderPurchases(PlatformSessions.StoreSession access, PlatformAccounts accounts)
    {
        var account = accounts.Get(access.AccountId);
        var catalog = accounts.StoreCatalog;
        var economy = PlatformEconomy.For(account);
        var purchases = (economy.Purchases ?? Array.Empty<PlatformPurchase>())
            .OrderByDescending(purchase => purchase.PurchasedAt)
            .ThenByDescending(purchase => purchase.RequestId)
            .ToArray();
        var rows = string.Join("", purchases.Select(purchase => RenderPurchaseRow(purchase, catalog)));
        var empty = purchases.Length == 0 ? "<p class=empty>No purchases have been recorded for this account.</p>" : string.Empty;
        var history = purchases.Length == 0 ? string.Empty :
            $"<div class=purchase-scroll><table class=purchase-history><thead><tr><th>Date (UTC)</th><th>Item</th><th>Type</th><th>Quantity</th><th>Currency</th><th>Amount</th></tr></thead><tbody>{rows}</tbody></table></div>";
        return Layout("Purchases", $$"""
            <section class="store-frame store-purchases">{{RenderStoreHeader(null, true)}}
              <div class="store-panel store-page"><div class=purchases><h1>Purchases</h1><p class=muted>Receipts saved to {{Encode(account.Summoner)}}. Dates are shown in UTC.</p>{{empty}}{{history}}</div></div>
            </section>
            """);
    }

    private static string RenderPurchaseRow(PlatformPurchase purchase, PlatformStoreCatalog catalog)
    {
        // A limited-edition rune (Harrowing 2011) is no longer a product once its window closed, but its receipt still names it.
        var hasItem = catalog.TryGetKnown(purchase.ItemId, out var item);
        var itemLabel = hasItem ? Encode(item.Name) : $"Catalog item unavailable ({Encode(purchase.ItemId)})";
        var itemCell = hasItem && catalog.TryGet(purchase.ItemId, out _)
            ? $"<a href=\"/store/item/{WebUtility.UrlEncode(item.Id)}\">{itemLabel}</a>"
            : itemLabel;
        var type = hasItem ? DisplayKind(item.Kind) : DisplayPurchaseKind(purchase.ItemId);
        var quantity = purchase.Quantity < 1 ? 1 : purchase.Quantity;
        return $"<tr><td>{Encode(purchase.PurchasedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}</td><td>{itemCell}</td><td>{Encode(type)}</td><td>{quantity.ToString(CultureInfo.InvariantCulture)}</td><td>{Encode(purchase.Currency)}</td><td>{purchase.Price.ToString("N0", CultureInfo.InvariantCulture)}</td></tr>";
    }

    private static string DisplayPurchaseKind(string itemId)
    {
        var prefix = itemId.Split(':', 2)[0].Trim().ToLowerInvariant();
        return prefix switch
        {
            "champion" => "Champion",
            "skin" => "Champion skin",
            "rune" => "Rune",
            "boost" => "Boost",
            "bundle" => "Rune page bundle",
            "rune_page" => "Rune page",
            _ => "Store item",
        };
    }

    private static string DisplayKind(string kind) => kind switch
    {
        "CHAMPION" => "Champion",
        "CHAMPION_SKIN" => "Champion skin",
        "RUNE" => "Rune",
        "BOOST" => "Boost",
        "BUNDLE" => "Rune page bundle",
        "SPELL_BOOK_PAGE" or "RUNE_PAGE" => "Rune page",
        _ => kind,
    };

    public static string? StockProductId(string kind, string file)
    {
        var value = Path.GetFileNameWithoutExtension(file);
        if (kind.Equals("Champion", StringComparison.OrdinalIgnoreCase) && value.StartsWith("champions_", StringComparison.Ordinal))
            return int.TryParse(value["champions_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var champion)
                ? $"champion:{champion}" : null;
        if (kind.Equals("Skin", StringComparison.OrdinalIgnoreCase) && value.StartsWith("championsskin_", StringComparison.Ordinal))
            return int.TryParse(value["championsskin_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var skin)
                ? $"skin:{skin}" : null;
        return null;
    }

    public static bool IsSafeAssetPath(string? relative, string root, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\0') >= 0) return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;
        // The request's spelling is matched to the AIR client's files on disk; the match stays inside the root, since every step is a listed name.
        var found = CaseInsensitivePaths.Resolve(candidate);
        if (!found.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(found)) return false;
        fullPath = found;
        return true;
    }

    /// <summary>The AIR client's store images: <c>assets/storeImages</c>, or <c>air/assets/storeImages</c> when the folder holds the client one level down.</summary>
    public static string StoreImageRoot(string stagedRoot)
    {
        var assets = CaseInsensitivePaths.Resolve(Path.Combine(stagedRoot, "assets", "storeImages"));
        return Directory.Exists(assets) ? assets : CaseInsensitivePaths.Resolve(Path.Combine(stagedRoot, "air", "assets", "storeImages"));
    }

    private static PlatformSessions.StoreSession RequireOrOpen(HttpContext context, PlatformSessions sessions)
    {
        var token = context.Request.Cookies[StoreCookie];
        if (!string.IsNullOrWhiteSpace(token))
        {
            try { return sessions.RequireStoreSession(token); }
            catch (ArgumentException) { }
        }

        var ticket = context.Request.Query["sessionId"].ToString();
        if (ticket.Length == 0) throw new ArgumentException("Open the store from the AIR client.");
        var access = sessions.RedeemStoreTicket(ticket);
        context.Response.Cookies.Append(StoreCookie, access.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = StorePath,
            MaxAge = access.Expires - DateTimeOffset.UtcNow,
        });
        return access;
    }

    private static PlatformSessions.StoreSession OpenStoreSession(HttpContext context, PlatformSessions sessions)
    {
        var token = context.Request.Cookies[StoreCookie];
        if (!string.IsNullOrWhiteSpace(token))
        {
            try { return sessions.RequireStoreSession(token); }
            catch (ArgumentException) { }
        }
        var ticket = context.Request.Query["sessionId"].ToString();
        if (ticket.Length == 0) throw new ArgumentException("The store session is missing.");
        return sessions.RedeemStoreTicket(ticket);
    }

    private static IResult ServeImage(string root, string? path)
    {
        if (!IsSafeAssetPath(path, root, out var fullPath)) return Results.NotFound();
        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
        return Results.File(fullPath, contentType);
    }

    private static string RenderRpInformation(PlatformAccount account)
    {
        var economy = PlatformEconomy.For(account);
        return Layout("Get RP", $$"""
            <section class="store-frame store-rp">
              {{RenderStoreHeader(null, rp: true)}}
              <div class="store-panel store-page"><div class=rp-information>
                <h1>Premium Points</h1>
                <div class=rp-overview>
                  <div class=rp-balance-card><span>Current RP</span><strong>{{economy.Rp.ToString("N0", CultureInfo.InvariantCulture)}}</strong></div>
                  <div class=rp-copy><h2>Get Premium Points</h2>
                    <p>Payment is unavailable in this historical emulator. The host grants testing RP directly to the account.</p>
                    <p><a class=back-link href="/store/home">Return to the store</a></p>
                  </div>
                </div>
              </div></div>
            </section>
            """);
    }

    /// <summary>
    /// The "Item Unlocked!" dialog (the March 2010 capture: yellow headline, the item card, a short confirmation and a Done button on the
    /// shipped sale_complete.png frame) over the list of the purchased item's kind. Reconstruction: placement and the confirmation wording
    /// (written for this emulator; the retail sentence is not reproduced).
    /// </summary>
    private static string RenderPurchaseComplete(PlatformSessions.StoreSession access, PlatformAccounts accounts,
        PlatformStoreItem item, PlatformPurchase? purchase, Microsoft.AspNetCore.Http.IQueryCollection? view = null)
    {
        var account = accounts.Get(access.AccountId);
        var economy = PlatformEconomy.For(account);
        var catalog = accounts.StoreCatalog;
        var quantity = Math.Max(1, purchase?.Quantity ?? 1);
        var what = item.Kind switch
        {
            "CHAMPION" => "You have permanently unlocked this champion. It will be available in champion select the next time you start a game.",
            "CHAMPION_SKIN" => "You have permanently unlocked this skin. Choose it in champion select the next time you start a game.",
            "RUNE" => $"Added {quantity} to your rune inventory. Equip runes in your summoner profile.",
            "BOOST" => "Your boost is active now. It is listed on your summoner profile.",
            _ => "Your purchase is complete.",
        };
        var total = purchase is null ? string.Empty
            : $"<div class=dlg-note style=\"top:160px;left:196px;width:290px;height:auto\">Paid {purchase.Price.ToString("N0", CultureInfo.InvariantCulture)} {WebUtility.HtmlEncode(purchase.Currency)}.<br>You now have {economy.Rp.ToString("N0", CultureInfo.InvariantCulture)} RP and {economy.Ip.ToString("N0", CultureInfo.InvariantCulture)} IP.</div>";
        // Done and the X return to the view the purchase was made from (sort, filters, search) and scroll to the card. Without a view (a form of
        // an older page) they return to the item's list in the default order, as before.
        var cardId = CardId(item.Id);
        var back = "/store/home?" + WebUtility.HtmlEncode(view is null ? StoreParams(item.Kind, string.Empty, false, "name") : ViewState(view)) + "#" + cardId;
        var overlay = $"<div class=dlg-wrap><div class=\"dlg done\"><span class=dlg-title>Purchase Item</span><a class=dlg-close href=\"{back}\" title=\"Close\"></a>" +
            $"<span class=dlg-head>Item Unlocked!</span><div class=dlg-card>{RenderProductRow(item, account, economy, catalog, dialog: true)}</div>" +
            $"<div class=dlg-copy>{WebUtility.HtmlEncode(what)}</div>{total}<a class=dlg-done href=\"{back}\">Done</a></div></div>";
        // The page behind the dialog is that same view, already scrolled to the card.
        return view is null ? RenderHome(access, accounts, item.Kind, overlay: overlay, focusCard: cardId)
            : RenderView(view, access, accounts, overlay: overlay, focusCard: cardId);
    }

    /// <summary>
    /// A refusal or failure (an expired session, too little RP, a locked item ...) as a dialog over the store: the shipped sorry art
    /// and red error banner. Reconstruction: placement; the messages are the platform's own.
    /// </summary>
    public static string ErrorPage(string message) => Layout("Store", $"<section class=\"store-frame store-error\">{RenderStoreHeader("ERROR")}" +
        "<div class=\"store-panel store-page\"><div class=purchases><h1>Store</h1></div></div>" +
        $"<div class=dlg-wrap><div class=dlg><span class=dlg-title>Purchase Item</span><a class=dlg-close href=\"/store/home\" title=\"Close\"></a>" +
        $"<span class=dlg-banner>Sorry</span><div class=dlg-note>{WebUtility.HtmlEncode(message)}<br><br><a href=\"/store/home\">Return to the store</a> &middot; " +
        "<a href=\"/store/purchases\">View purchases</a> &middot; <a href=\"#\" onclick=\"return reopenStore();\">Reopen store session</a></div><span class=dlg-sorry></span></div></div></section>");

    private const string StoreScript = """
        window.childSandboxBridge={childSetupBridge:function(){ } };function reopenStore(){if(window.parentSandboxBridge){window.parentSandboxBridge.sessionExpired("/store/home", "");}return false;}function restoreStoreSearchFocus(){var input=document.getElementById("store-search");if(input&&input.getAttribute("data-focus")==="1"){try{input.focus();if(input.setSelectionRange){var end=input.value.length;input.setSelectionRange(end,end);}}catch(e){}}}function storeSearchChanged(input){var current=(input.value||"").toLowerCase(),server=(input.getAttribute("data-server-search")||"").toLowerCase();if(window.__storeSearchTimer){window.clearTimeout(window.__storeSearchTimer);}if(window.__storeSearchReloadTimer){window.clearTimeout(window.__storeSearchReloadTimer);}if(server&&current.indexOf(server)<0){window.__storeSearchReloadTimer=window.setTimeout(function(){var form=input.form,focus=document.getElementById("store-focus-search");if(focus){focus.value="1";}if(form){form.submit();}},350);return;}window.__storeSearchTimer=window.setTimeout(function(){var term=(input.value||"").toLowerCase(),cards=document.getElementsByTagName("article"),i,card,text,visible=0,classes;for(i=0;i<cards.length;i++){card=cards[i];if((card.className||"").indexOf(" product")<0&&(card.className||"").indexOf("product ")<0){continue;}classes=(card.className||"").replace(/ ?last-column/g,"");text=(card.getAttribute("data-search")||"").toLowerCase();if(term.length===0||text.indexOf(term)>=0){visible++;card.style.display="";card.className=classes+(visible%5===0?" last-column":"");}else{card.style.display="none";card.className=classes;}}},120);}function storePromo(n,total){for(var i=0;i<total;i++){var s=document.getElementById("promo"+i),p=document.getElementById("promopage"+i);if(s){s.className=i===n?"promo-slide on":"promo-slide";}if(p){p.className=i===n?"on":"";}}return false;}function storeLoaded(){restoreStoreSearchFocus();storeFocusCard();}function storeFocusCard(){try{var id=(window.location.hash||'').replace('#','')||document.body.getAttribute('data-card')||'',card=id?document.getElementById(id):null,box=document.getElementById('catalog-scroll');if(card&&box&&box.scrollTop===0){box.scrollTop=Math.max(0,card.offsetTop-6);}}catch(e){}}function storeInfo(item,skin){try{var bridge=window.parentSandboxBridge;if(bridge&&bridge.openInventoryBrowser){bridge.openInventoryBrowser(item,skin||'');}}catch(e){}return false;}function storeQty(select){var form=select.form,qty=parseInt(select.value,10)||1,unit=parseInt(form.getAttribute('data-unit'),10)||0,bal=parseInt(form.getAttribute('data-bal'),10)||0,price=document.getElementById('dlg-price'),rem=document.getElementById('dlg-rem');if(price){price.innerHTML=''+(unit*qty);}if(rem){rem.innerHTML=''+(bal-unit*qty);rem.className=bal-unit*qty<0?'dlg-rem neg':'dlg-rem';}}
        """;

    private static string AssetUrl(string path) => path.StartsWith("/store/art/", StringComparison.Ordinal)
        ? path : $"/img/{WebUtility.UrlEncode(path).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";

    private static string NormalizeSearch(string? search)
    {
        var value = search?.Trim() ?? string.Empty;
        return value.Length <= 64 ? value : value[..64];
    }

    private static string NormalizeSort(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        "rp" => "rp",
        "ip" => "ip",
        _ => "name",
    };

    private static int? ParseRuneTier(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tier) && tier is >= 1 and <= 3
            ? tier : null;

    private static string? NormalizeRuneType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "red" or "mark" or "marks" => "Red",
        "blue" or "glyph" or "glyphs" => "Blue",
        "yellow" or "seal" or "seals" => "Yellow",
        "black" or "quint" or "quintessence" or "quintessences" => "Black",
        _ => null,
    };

    private static RuneTier? NormalizeRuneTier(int? value) => value is >= 1 and <= 3 ? (RuneTier)value : null;

    private static string? NormalizeCurrency(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "IP" or "RP" => value!.Trim().ToUpperInvariant(),
        _ => null,
    };

    private static string? NormalizeChampionTag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "assassin" or "carry" or "fighter" or "mage" or "melee" or "pusher" or
        "ranged" or "recommended" or "support" or "stealth" or "tank" or "jungler" => value!.Trim().ToLowerInvariant(),
        _ => null,
    };

    private static string SearchText(PlatformStoreItem item, PlatformStoreCatalog catalog)
    {
        if (item.Kind != "RUNE")
            return $"{item.Name} {string.Join(" ", PlatformStoreCatalog.ChampionSearchTags(item.ChampionId))}";
        var rune = catalog.RuneContent(item.ContentId);
        if (rune is null) return item.Name;
        var effects = string.Join(" ", rune.Modifiers.Select(modifier =>
            $"{modifier.Name} {modifier.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));
        return $"{item.Name} {rune.Description} {RuneLabel(rune.Color)} Tier {(int)rune.Tier} {effects}";
    }

    private static IEnumerable<PlatformStoreItem> SortItems(IEnumerable<PlatformStoreItem> items, string sort) => sort switch
    {
        "rp" => items.OrderBy(item => item.RpPrice ?? int.MaxValue).ThenBy(item => item.Name, NaturalNames),
        "ip" => items.OrderBy(item => item.IpPrice ?? int.MaxValue).ThenBy(item => item.Name, NaturalNames),
        _ => items.OrderBy(item => item.Name, NaturalNames),
    };

    /// <summary>
    /// Names in the order a person reads them: a run of digits counts by its value (Rune Page 3, 4 ... 10, 20; the plain text order
    /// gave 10..19, 20, 3, 4), everything else compares case-insensitively as before. Reconstruction: retail's order of the numbered
    /// rune pages is not recorded; numeric is the only order that matches how the pages are numbered.
    /// </summary>
    private static readonly IComparer<string> NaturalNames = new NaturalNameComparer();

    private sealed class NaturalNameComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var startX = i;
                    var startY = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var runX = x.AsSpan(startX, i - startX).TrimStart('0');
                    var runY = y.AsSpan(startY, j - startY).TrimStart('0');
                    if (runX.Length != runY.Length) return runX.Length < runY.Length ? -1 : 1;
                    var byValue = runX.CompareTo(runY, StringComparison.Ordinal);
                    if (byValue != 0) return byValue;
                }
                else
                {
                    var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (byChar != 0) return byChar;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    /// <summary>The view parameters (URL-encoded, not HTML-encoded) that <c>/store/home</c> and the dialog route read back.</summary>
    private static string StoreParams(string? filter, string search, bool notOwned, string sort,
        string? runeType = null, RuneTier? runeTier = null, string? currency = null, string? championTag = null,
        bool onSale = false, bool championOwned = false)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrEmpty(filter)) parameters.Add($"kind={WebUtility.UrlEncode(filter)}");
        if (!string.IsNullOrEmpty(search)) parameters.Add($"search={WebUtility.UrlEncode(search)}");
        if (notOwned) parameters.Add("notOwned=1");
        if (onSale) parameters.Add("sale=1");
        if (championOwned) parameters.Add("championOwned=1");
        if (!string.IsNullOrEmpty(runeType)) parameters.Add($"runeType={WebUtility.UrlEncode(runeType)}");
        if (runeTier is not null) parameters.Add($"runeTier={(int)runeTier.Value}");
        if (!string.IsNullOrEmpty(currency)) parameters.Add($"currency={WebUtility.UrlEncode(currency)}");
        if (!string.IsNullOrEmpty(championTag)) parameters.Add($"tag={WebUtility.UrlEncode(championTag)}");
        parameters.Add($"sort={WebUtility.UrlEncode(NormalizeSort(sort))}");
        return string.Join("&", parameters);
    }

    private static string StoreQuery(string? filter, string search, bool notOwned, string sort,
        string? runeType = null, RuneTier? runeTier = null, string? currency = null, string? championTag = null,
        bool onSale = false, bool championOwned = false) =>
        "/store/home?" + WebUtility.HtmlEncode(StoreParams(filter, search, notOwned, sort, runeType, runeTier, currency, championTag,
            onSale, championOwned));
    private static bool IsOwned(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog) => item.Kind switch
    {
        "SPELL_BOOK_PAGE" or "RUNE_PAGE" => PlatformAccounts.RunePagesFor(account).Any(page => page.PageId == item.ContentId),
        "CHAMPION" => economy.AllUnlocked || economy.OwnedChampions?.Contains(item.ContentId) == true,
        "CHAMPION_SKIN" => economy.AllUnlocked || economy.OwnedSkins?.Contains(item.ContentId) == true,
        // An all-unlocked account owns the regular runes; a window rune (Harrowing 2011) is bought like any rune.
        "RUNE" => economy.AllUnlocked && !catalog.IsWindowRune(item.ContentId) || (economy.OwnedRunes?.GetValueOrDefault(item.ContentId) is int quantity &&
            quantity >= catalog.GetRuneQuantityLimit(item.ContentId)),
        "BUNDLE" => economy.AllUnlocked,
        _ => false,
    };

    private static bool IsOwnedForFilter(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog) => item.Kind switch
    {
        // The stock Not Owned view means the player has none of the rune,
        // while IsOwned above deliberately means the purchase cap is reached.
        "RUNE" => economy.AllUnlocked && !catalog.IsWindowRune(item.ContentId) || economy.OwnedRunes?.GetValueOrDefault(item.ContentId) is > 0,
        _ => IsOwned(item, account, economy, catalog),
    };

    private static string OwnershipText(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog) => item.Kind switch
    {
        "SPELL_BOOK_PAGE" or "RUNE_PAGE" => "Owned",
        "RUNE" => $"{(economy.AllUnlocked && !catalog.IsWindowRune(item.ContentId) ? catalog.GetRuneQuantityLimit(item.ContentId) : economy.OwnedRunes?.GetValueOrDefault(item.ContentId) ?? 0)} / {catalog.GetRuneQuantityLimit(item.ContentId)}",
        "BUNDLE" => "Owned",
        "BOOST" => "Available",
        _ => "Owned",
    };

    private static bool IsUnavailable(PlatformStoreItem item, PlatformEconomyState economy) =>
        item.Kind == "BOOST" && PlatformStoreCatalog.IsXpBoost(item.Id) && economy.Level >= 30;

    private static bool IsBundleUnavailable(PlatformStoreItem item, PlatformAccount account,
        PlatformEconomyState economy, PlatformStoreCatalog catalog) =>
        item.Kind == "BUNDLE" && (economy.AllUnlocked ||
            catalog.MaximumRunePages - PlatformAccounts.RunePagesFor(account).Length < PlatformStoreCatalog.RunePageBundleSize);

    private static string? NormalizeKind(string? kind) => kind?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        "CHAMPION" => "CHAMPION",
        "CHAMPION_SKIN" => "CHAMPION_SKIN",
        "RUNE" => "RUNE",
        "BOOST" => "BOOST",
        "BUNDLE" => "BUNDLE",
        "SPELL_BOOK_PAGE" or "RUNE_PAGE" => "SPELL_BOOK_PAGE",
        _ => null,
    };

    private static object Fulfillment(PlatformStoreItem item, PlatformAccount account, PlatformEconomyState economy,
        int quantity = 1)
    {
        if (item.Kind == "BOOST") return AirDtos.ActiveBoostsNotification(account);
        var data = item.Kind switch
        {
            "CHAMPION" => AirWire.Object(ChampionAlias, ("championId", item.ContentId), ("displayName", item.Name),
                ("owned", true), ("freeToPlay", true), ("active", true)),
            "CHAMPION_SKIN" => AirWire.Object(SkinAlias, ("championId", item.ChampionId),
                ("skinId", item.ContentId), ("owned", true), ("lastSelected", false)),
            // Inventory.processStoreFulfillmentNotification expects an
            // incremental RuneQuantity object and a full SpellBook, rather than
            // the SummonerRune/SpellBookPage DTOs used by the inventory RPCs.
            "RUNE" => AirWire.Object(RuneQuantityAlias, ("runeId", item.ContentId), ("quantity", quantity)),
            "SPELL_BOOK_PAGE" or "RUNE_PAGE" => AirDtos.SpellBook(account),
            "BUNDLE" => AirDtos.SpellBook(account),
            _ => throw new ArgumentException($"Unsupported store product kind '{item.Kind}'."),
        };
        return AirWire.Object(FulfillmentAlias, ("inventoryType", InventoryType(item.Kind)),
            ("ip", (double)economy.Ip), ("rp", (double)economy.Rp),
            ("data", data));
    }

    private static string InventoryType(string kind) => kind switch
    {
        "CHAMPION" => "CHAMPION",
        "CHAMPION_SKIN" => "CHAMPION_SKIN",
        "RUNE" => "RUNE",
        "SPELL_BOOK_PAGE" or "RUNE_PAGE" => "SPELL_BOOK_PAGE",
        "BUNDLE" => "SPELL_BOOK_PAGE",
        "BOOST" => "BOOST",
        _ => throw new ArgumentException($"Unsupported store product kind '{kind}'."),
    };
}
