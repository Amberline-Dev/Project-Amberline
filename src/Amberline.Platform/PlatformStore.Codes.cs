using System.Globalization;
using System.Net;

namespace Amberline.Platform;

public static partial class PlatformStore
{
    private static void MapRewardCodeRoutes(WebApplication app, PlatformAccounts accounts,
        PlatformSessions sessions, object purchaseGate, Action<long, object>? notify)
    {
        app.MapGet("/store/codes", (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(RenderRewardCodes(access, accounts), "text/html; charset=utf-8");
        });

        app.MapPost("/store/codes/redeem", async (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!PlatformSessions.SecretEquals(access.FormToken, form["formToken"].ToString()))
                throw new ArgumentException("The code form has expired. Reload the page.");
            var code = form["code"].ToString().Trim();
            if (code.Length is < 1 or > 256 || code.Any(char.IsControl))
                throw new ArgumentException("The reward code must be 1 to 256 printable characters.");
            context.Response.Headers.CacheControl = "no-store";

            PlatformRewardCodeRedemption redemption;
            lock (purchaseGate)
            {
                try
                {
                    redemption = accounts.RedeemRewardCode(access.AccountId, code);
                }
                catch (InvalidOperationException)
                {
                    return Results.Content(RenderRewardCodeError(accounts.Get(access.AccountId), accounts.StoreCatalog,
                        "This reward would exceed the account balance limit."), "text/html; charset=utf-8");
                }
                // NOT RETAIL (batch 751): a code of RP alone while RP is off is named in plain words; it is unused and the holder typed it.
                catch (ArgumentException refused) when (refused.Message.StartsWith(PlatformAccounts.RpOnlyCode, StringComparison.Ordinal))
                {
                    return Results.Content(RenderRewardCodeError(accounts.Get(access.AccountId), accounts.StoreCatalog,
                        PlatformAccounts.RpOnlyCode), "text/html; charset=utf-8");
                }
                catch (ArgumentException)
                {
                    // Do not reflect whether a valid code belongs to another account.
                    return Results.Content(RenderRewardCodeError(accounts.Get(access.AccountId), accounts.StoreCatalog),
                        "text/html; charset=utf-8");
                }

                if (!redemption.Replayed && notify is not null)
                    notify(redemption.Account.Id, AirDtos.AccountBalanceNotification(redemption.Account, accounts.Controls.PaidContentOff));
            }

            return Results.Content(RenderRewardCodeRedemption(redemption, accounts.StoreCatalog), "text/html; charset=utf-8");
        });
    }

    private static string RenderRewardCodes(PlatformSessions.StoreSession access, PlatformAccounts accounts)
    {
        var account = accounts.Get(access.AccountId);
        var economy = PlatformEconomy.For(account);
        var catalog = accounts.StoreCatalog;
        return Layout("Codes", $$"""
            <section class="store-frame store-codes">{{RenderStoreHeader(catalog, null, false, true)}}
              <div class="store-panel store-page"><div class=purchases>
                <div class=code-panel>
                <h1>Redeem a code</h1>
                <p class=muted>Enter a host-issued reward code to add {{(catalog.PaidContentOff() ? "IP" : "IP or RP")}} to this account.</p>
                <form method=post action=/store/codes/redeem class=purchase>
                  <input type=hidden name=formToken value="{{WebUtility.HtmlEncode(access.FormToken)}}">
                  <label>Reward code <input type=text name=code maxlength=256 required autocomplete=off></label>
                  <button type=submit>Redeem code</button>
                </form>
                {{Balance(economy, catalog)}}
                </div>
              </div></div>
            </section>
            """);
    }

    private static string RenderRewardCodeRedemption(PlatformRewardCodeRedemption redemption, PlatformStoreCatalog catalog)
    {
        var reward = redemption.Reward;
        var verb = redemption.Replayed ? "This code was already redeemed by this account." : "Reward code accepted.";
        var amount = reward.Ip.ToString("N0", CultureInfo.InvariantCulture) + " IP" +
            (catalog.PaidContentOff() ? string.Empty : $" and {reward.Rp.ToString("N0", CultureInfo.InvariantCulture)} RP");
        var economy = PlatformEconomy.For(redemption.Account);
        return Layout("Code redeemed", $$"""
            <section class="store-frame store-codes">{{RenderStoreHeader(catalog, null, false, true)}}
              <div class="store-panel store-page"><div class=purchases>
                <div class=code-panel>
                <h1>Code redeemed</h1>
                <p>{{WebUtility.HtmlEncode(verb)}}</p>
                <p class=balance>Reward: <strong>{{WebUtility.HtmlEncode(amount)}}</strong></p>
                {{Balance(economy, catalog)}}
                <p><a href=/store/codes>Redeem another code</a> · <a href=/store/home>Return to the store</a></p>
                </div>
              </div></div>
            </section>
            """);
    }

    private static string RenderRewardCodeError(PlatformAccount account, PlatformStoreCatalog catalog,
        string message = "This reward code is invalid or unavailable.")
    {
        var economy = PlatformEconomy.For(account);
        return Layout("Code unavailable", $$"""
            <section class="store-frame store-codes">{{RenderStoreHeader(catalog, null, false, true)}}
              <div class="store-panel store-page"><div class=purchases>
                <div class=code-panel>
                <h1>Code unavailable</h1>
                <p>{{WebUtility.HtmlEncode(message)}}</p>
                {{Balance(economy, catalog)}}
                <p><a href=/store/codes>Return to code redemption</a> · <a href=/store/home>Return to the store</a></p>
                </div>
              </div></div>
            </section>
            """);
    }

    // The balance line of the code pages; NOT RETAIL (batch 751): without Premium Points while the host's "Offer skins and RP" switch is off.
    private static string Balance(PlatformEconomyState economy, PlatformStoreCatalog catalog) =>
        $"<p class=balance>Influence Points: <strong>{economy.Ip.ToString("N0", CultureInfo.InvariantCulture)}</strong>" +
        (catalog.PaidContentOff() ? string.Empty : $" · Premium Points: <strong>{economy.Rp.ToString("N0", CultureInfo.InvariantCulture)}</strong>") + "</p>";
}
