using System.Globalization;
using System.Net;
using Amberline.Data;

namespace Amberline.Platform;

public static partial class PlatformStore
{
    /// <summary>
    /// The unlock dialog, drawn over the store view it was opened from (the cards, filters and scroll position stay behind it).
    /// Retail had no product page in the 2011 store: a card opened the client's own champion window (see <see cref="HoverLinks"/>)
    /// and Unlock opened this dialog. Structure from the March 2010 purchase captures: the title "Purchase Item", the X, the item card,
    /// a balance / price / remainder table for each currency the item sells in (skins show RP only) and the "Unlock With" buttons.
    /// The submit goes to the unchanged secure <c>POST /store/purchase</c> (form token, request id, server-side price and the
    /// stale-price guard); this renderer only presents what that endpoint will charge.
    /// Reconstruction: sizes and placement over the shipped art; the wording of the refusal texts.
    /// </summary>
    private static string RenderUnlockDialog(PlatformSessions.StoreSession access, PlatformStoreCatalog catalog,
        PlatformAccount account, PlatformEconomyState economy, PlatformStoreItem item, string state)
    {
        var back = "/store/home?" + WebUtility.HtmlEncode(state) + "#" + CardId(item.Id);
        var reason = Unavailability(item, account, economy, catalog);
        var card = $"<div class=dlg-card>{RenderProductRow(item, account, economy, catalog, dialog: true)}</div>";
        var body = reason is null
            ? RenderPurchaseColumns(access, catalog, economy, item, state)
            : $"<span class=dlg-banner>{Encode(reason)}</span><div class=dlg-note>{Encode(RefusalText(item, account, economy, catalog))}</div><span class=dlg-sorry></span>";
        return $"<div class=dlg-wrap><div class=dlg><span class=dlg-title>Purchase Item</span><a class=dlg-close href=\"{back}\" title=\"Close\"></a>{card}{body}</div></div>";
    }

    // The old product page's explanations, in the same order, for an item that cannot be unlocked right now. Every card opens this dialog
    // (owner decision 18 October 2026, night), so an owned, locked, level-gated or champion-less item says why on the card's own Unlock.
    // Reconstruction: the wording of the owned sentences (the old page printed "Owned" alone).
    private static string RefusalText(PlatformStoreItem item, PlatformAccount account, PlatformEconomyState economy,
        PlatformStoreCatalog catalog)
    {
        if (item.Locked)
            return item.Kind == "BUNDLE" ? "This bundle is not available on this server, so it cannot be unlocked."
                : "This champion cannot be played on this server yet, so it cannot be unlocked.";
        if (IsOwned(item, account, economy, catalog))
            return item.Kind switch
            {
                "CHAMPION" => "You already own this champion.",
                "CHAMPION_SKIN" => "You already own this skin.",
                "RUNE" => $"You already hold the most copies of this rune that you can ({OwnershipText(item, economy, catalog)}).",
                "BUNDLE" => "You already own this bundle.",
                "SPELL_BOOK_PAGE" or "RUNE_PAGE" => "You already own this rune page.",
                _ => "You already own this item.",
            };
        if ((item.Kind is "SPELL_BOOK_PAGE" or "RUNE_PAGE") && PlatformAccounts.RunePagesFor(account).Length >= catalog.MaximumRunePages)
            return $"This server allows {catalog.MaximumRunePages} rune pages, and this account has all of them.";
        if (IsUnavailable(item, economy)) return "XP boosts are unavailable at level 30.";
        if (IsBundleUnavailable(item, account, economy, catalog)) return "Requires room for all seven additional rune pages.";
        if (economy.Level < item.MinimumLevel) return $"Requires summoner level {item.MinimumLevel}.";
        if (item.Kind == "CHAMPION_SKIN" && !IsOwned(catalog.GetChampion(item.ChampionId), account, economy, catalog))
            return $"Requires {catalog.ChampionContent(item.ChampionId)?.Name ?? "the champion"} to be owned.";
        return "This item cannot be unlocked right now.";
    }

    // One column per currency (RP left, IP right as in the 2010 dialog), each with its own purchase form and Unlock With button.
    // The form also carries `view`, the filtered view the dialog was opened from (the card links' state): the "Item Unlocked!" page returns to it.
    private static string RenderPurchaseColumns(PlatformSessions.StoreSession access, PlatformStoreCatalog catalog,
        PlatformEconomyState economy, PlatformStoreItem item, string state)
    {
        var maxQuantity = 1;
        if (item.Kind == "RUNE")
        {
            var owned = economy.OwnedRunes?.GetValueOrDefault(item.ContentId) ?? 0;
            maxQuantity = Math.Max(1, catalog.GetRuneQuantityLimit(item.ContentId) - owned);
        }
        var columns = new List<(string Currency, int Price, long Balance)>();
        if (item.RpPrice is int rp) columns.Add(("RP", rp, economy.Rp));
        if (item.IpPrice is int ip) columns.Add(("IP", ip, economy.Ip));
        var html = new System.Text.StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            var (currency, price, balance) = columns[i];
            // The right-hand area is 344 px wide from x=196: both columns (332 px with the 22 px gap) or one centred column.
            var x = columns.Count == 2 ? 202 + i * 177 : 290;
            var influence = currency == "IP";
            var remainder = balance - price;
            var affordable = remainder >= 0;
            var title = influence ? "Unlock with Influence Points" : "Unlock with Premium Points";
            html.Append($"<div class=\"dlg-table{(influence ? " ip" : string.Empty)}\" style=\"left:{x}px\">" +
                $"<span class=dlg-bal>{balance.ToString("N0", CultureInfo.InvariantCulture)}</span>" +
                $"<span class=dlg-price{(maxQuantity > 1 ? " id=dlg-price" : string.Empty)}>{price.ToString("N0", CultureInfo.InvariantCulture)}</span>" +
                $"<span class=\"dlg-rem{(affordable ? string.Empty : " neg")}\"{(maxQuantity > 1 ? " id=dlg-rem" : string.Empty)}>{remainder.ToString("N0", CultureInfo.InvariantCulture)}</span></div>");
            var labelX = x + 20;
            if (!affordable)
            {
                html.Append($"<span class=dlg-with style=\"left:{labelX}px\">Unlock With</span><span class=\"dlg-unlock{(influence ? " ip" : string.Empty)} off\" style=\"left:{labelX}px\" title=\"{title}\"></span>" +
                    $"<span class=dlg-short style=\"left:{x}px\">Not enough {(influence ? "Influence Points" : "Premium Points")}</span>");
                continue;
            }
            html.Append($"<form method=post action=\"/store/purchase\" class=dlg-form data-unit=\"{price}\" data-bal=\"{balance}\">" +
                $"<input type=hidden name=formToken value=\"{WebUtility.HtmlEncode(access.FormToken)}\">" +
                $"<input type=hidden name=itemId value=\"{WebUtility.HtmlEncode(item.Id)}\">" +
                $"<input type=hidden name=currency value=\"{currency}\">" +
                $"<input type=hidden name=expectedPrice value=\"{price}\">" +
                $"<input type=hidden name=view value=\"{WebUtility.HtmlEncode(state)}\">" +
                $"<input type=hidden name=requestId value=\"{Guid.NewGuid():D}\">" +
                (maxQuantity > 1
                    ? $"<label class=dlg-qty>Quantity<select name=quantity onchange=\"storeQty(this)\">{string.Join("", Enumerable.Range(1, maxQuantity).Select(quantity => $"<option value=\"{quantity}\">{quantity}</option>"))}</select></label>"
                    : "<input type=hidden name=quantity value=1>") +
                $"<span class=dlg-with style=\"left:{labelX}px\">Unlock With</span>" +
                $"<button type=submit class=\"dlg-unlock{(influence ? " ip" : string.Empty)}\" style=\"left:{labelX}px\" title=\"{title}\">Unlock</button></form>");
        }
        return html.ToString();
    }

    private static string RuneLabel(RuneColor color) => color switch
    {
        RuneColor.Red => "Mark", RuneColor.Blue => "Glyph", RuneColor.Yellow => "Seal", _ => "Quintessence",
    };
    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
