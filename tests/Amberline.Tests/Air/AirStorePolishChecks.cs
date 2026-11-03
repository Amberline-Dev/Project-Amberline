using System.Net;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Amberline.Tests.Air;

/// <summary>
/// Store polish found by the real-client photographs of batch 96 (19 October 2026): where "Item Unlocked!" returns to, the order of the
/// Other tab, the scroll position behind a dialog and the colour of a short balance. Written, not run (owner rule).
/// </summary>
public static class AirStorePolishChecks
{
    [Check("server.air-store-other-tab-sorts-rune-pages-numerically")]
    public static void RunePagesAreOrderedByNumberInEverySort()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-polish-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The sort covers pages up to twenty; the default cap is ten, so this economy names twenty.
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"MaximumRunePages\":20}");
            using var accounts = new PlatformAccounts(root, root);
            var account = accounts.RegisterPublic("pagesorter", "testing-password", "Page Sorter", maxed: false);
            var access = new PlatformSessions.StoreSession("opaque", account.Id, "form-token", DateTimeOffset.UtcNow.AddMinutes(5));
            foreach (var sort in new[] { "name", "rp", "ip" })
            {
                var html = PlatformStore.RenderHome(access, accounts, "SPELL_BOOK_PAGE", 1, null, false, sort);
                var order = new[] { 3, 4, 9, 10, 11, 19, 20 }.Select(page => html.IndexOf($">Rune Page {page}<", StringComparison.Ordinal)).ToArray();
                TestAssert.True(order.All(index => index >= 0), $"{sort}: the pages are listed");
                TestAssert.True(order.SequenceEqual(order.Order()), $"{sort}: Rune Page 3, 4, 9, 10, 11, 19, 20 in that order");
            }
            // Other names keep their case-insensitive text order.
            var champions = PlatformStore.RenderHome(access, accounts, "CHAMPION");
            TestAssert.True(champions.IndexOf(">Annie<", StringComparison.Ordinal) < champions.IndexOf(">Janna<", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-list-scrolls-to-the-card-without-waiting-for-the-images")]
    public static void TheGridPointsAtTheCardAsSoonAsItsCardsExistAndKeepsAir25Script()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-polish-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var account = accounts.RegisterPublic("scrollbuyer", "testing-password", "Scroll Buyer", maxed: false);
            var access = new PlatformSessions.StoreSession("opaque", account.Id, "form-token", DateTimeOffset.UtcNow.AddMinutes(5));
            // The rune dialog (374 card images behind it) names its card, and its X returns to the same list and card.
            var dialog = PlatformStore.RenderHome(access, accounts, "RUNE", dialogItemId: "rune:5300");
            TestAssert.True(dialog.Contains("data-card=\"c-rune-5300\"", StringComparison.Ordinal), "the page names the card it opened from");
            TestAssert.True(dialog.Contains("class=dlg-close href=\"/store/home?kind=RUNE&amp;sort=name#c-rune-5300\"", StringComparison.Ordinal));
            var start = dialog.IndexOf("<script type=\"text/javascript\">", StringComparison.Ordinal);
            var script = dialog[start..dialog.IndexOf("</script>", start, StringComparison.Ordinal)];
            // Waiting for onload meant waiting for every image: the scroll also runs when the cards are parsed, and only moves a grid the
            // reader has not scrolled yet.
            TestAssert.True(script.EndsWith("storeFocusCard();", StringComparison.Ordinal), "called at parse time, not only from onload");
            TestAssert.True(script.Contains("box.scrollTop===0", StringComparison.Ordinal), "a later call does not pull a scrolled grid back");
            TestAssert.True(dialog.Contains("onload=\"storeLoaded()\"", StringComparison.Ordinal), "and still from onload");
            // The script stays inside AIR 2.5's WebKit (no let, const, arrow functions or classList).
            foreach (var forbidden in new[] { "=>", "let ", "const ", "classList" })
                TestAssert.False(script.Contains(forbidden, StringComparison.Ordinal), $"AIR 2.5: no '{forbidden}' in the page script");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-only-a-short-ip-balance-turns-a-card-price-red")]
    public static void ShortIpIsRedAndShortRpIsNeverRedLikeTheSeptember2011Screenshots()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = Path.Combine(Path.GetTempPath(), "league-air-polish-red-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var account = accounts.RegisterPublic("redprice", "testing-password", "Red Price", maxed: false);
            // The balances of the account in the 25 September 2011 screenshots: 640 RP and 568 IP.
            accounts.GrantCurrency(account.Id, 568, 640, Guid.NewGuid());
            var access = new PlatformSessions.StoreSession("opaque", account.Id, "form-token", DateTimeOffset.UtcNow.AddMinutes(5));
            string Card(string html, string id)
            {
                var start = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
                TestAssert.True(start >= 0, id);
                return html[start..html.IndexOf("</article>", start, StringComparison.Ordinal)];
            }

            // Orianna: 975 RP (more than the 640 RP held) next to 4800 IP (more than the 568 IP held): only the IP price is red.
            var champions = PlatformStore.RenderHome(access, accounts, "CHAMPION");
            var orianna = Card(champions, "c-champion-61");
            TestAssert.True(orianna.Contains("class=\"pr-ip short\"", StringComparison.Ordinal), "4800 IP against 568 IP is red");
            TestAssert.True(orianna.Contains("class=pr-rp", StringComparison.Ordinal) && !orianna.Contains("pr-rp short", StringComparison.Ordinal),
                "975 RP against 640 RP is not");
            TestAssert.False(champions.Contains("pr-rp short", StringComparison.Ordinal), "no RP price on any card is ever marked short");
            // Skins sell for RP only: a 975 RP skin is white at 640 RP.
            TestAssert.False(PlatformStore.RenderHome(access, accounts, "CHAMPION_SKIN").Contains("pr-rp short", StringComparison.Ordinal),
                "a 975 RP skin is white at 640 RP");
            // Runes: 410 IP is within 568 IP, 1025 IP is not.
            var runes = PlatformStore.RenderHome(access, accounts, "RUNE");
            var cheap = accounts.StoreCatalog.Items.First(item => item.Kind == "RUNE" && item.IpPrice == 410);
            var dear = accounts.StoreCatalog.Items.First(item => item.Kind == "RUNE" && item.IpPrice == 1025);
            TestAssert.True(Card(runes, "c-rune-" + cheap.ContentId).Contains("class=\"pr-ip\"", StringComparison.Ordinal), "410 IP is affordable");
            TestAssert.True(Card(runes, "c-rune-" + dear.ContentId).Contains("class=\"pr-ip short\"", StringComparison.Ordinal), "1025 IP is red");
            // The only CSS rule for a short price is the IP one.
            TestAssert.True(champions.Contains(".pr-ip.short{", StringComparison.Ordinal) && !champions.Contains(".pr-rp.short", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-item-unlocked-returns-to-the-view-the-purchase-was-made-from")]
    public static void DoneAndTheXReturnToTheViewOfThePurchaseWithItsSortFiltersAndCard() =>
        UnlockedReturnsAsync().GetAwaiter().GetResult();

    private static async Task UnlockedReturnsAsync()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = Path.Combine(Path.GetTempPath(), "league-air-polish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Pinned to a July day, outside every 2011 sale window (the calendar runs from 26 August): the live Pacific calendar put
            // Annie on sale on the day the check ran, so the price this check expects needs a clock with no sale. Test seam only.
            using var accounts = new PlatformAccounts(root, root, new FixedClock(new DateTimeOffset(2026, 7, 15, 19, 0, 0, TimeSpan.Zero)));
            var account = accounts.RegisterPublic("polishbuyer", "testing-password", "Polish Buyer", maxed: false);
            accounts.GrantCurrency(account.Id, 0, 2_000, Guid.NewGuid());
            var sessions = new PlatformSessions(accounts);
            var session = sessions.Login("polish-buyer", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
            TestAssert.True(sessions.AuthenticateChannel("polish-buyer", account.Username, session.Token));
            var access = sessions.RedeemStoreTicket(sessions.IssueStoreTicket("polish-buyer"));
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

                // The unlock dialog opened from a filtered view carries that view in its purchase form.
                var dialog = await client.GetStringAsync("/store/item/champion%3A1?kind=CHAMPION&notOwned=1&sort=rp");
                TestAssert.True(dialog.Contains("name=view value=\"kind=CHAMPION&amp;notOwned=1&amp;sort=rp\"", StringComparison.Ordinal),
                    "the form carries the view the dialog was opened from");

                async Task<(HttpStatusCode Status, string Html)> Buy(string itemId, string? view)
                {
                    var fields = new Dictionary<string, string>
                    {
                        ["itemId"] = itemId, ["currency"] = "RP", ["quantity"] = "1",
                        ["requestId"] = Guid.NewGuid().ToString(), ["formToken"] = access.FormToken,
                    };
                    if (view is not null) fields["view"] = view;
                    using var form = new FormUrlEncodedContent(fields);
                    using var response = await client.PostAsync("/store/purchase", form);
                    return (response.StatusCode, await response.Content.ReadAsStringAsync());
                }

                // Bought from a list sorted by RP price with the Not Owned filter and a search: Done and the X return to exactly that view and
                // card, and the list behind the dialog is that same view, already pointed at the card.
                var (status, page) = await Buy("champion:1", "kind=CHAMPION&search=ann&notOwned=1&sort=rp");
                TestAssert.Equal(HttpStatusCode.OK, status);
                var back = "href=\"/store/home?kind=CHAMPION&amp;search=ann&amp;notOwned=1&amp;sort=rp#c-champion-1\"";
                TestAssert.True(page.Contains("class=dlg-done " + back, StringComparison.Ordinal), "Done returns to the view of the purchase");
                TestAssert.True(page.Contains("class=dlg-close " + back, StringComparison.Ordinal), "so does the X");
                TestAssert.True(page.Contains("name=sort value=rp checked", StringComparison.Ordinal), "the list behind is sorted by RP");
                TestAssert.True(page.Contains("data-card=\"c-champion-1\"", StringComparison.Ordinal), "and scrolls to the card");
                TestAssert.True(page.Contains("Item Unlocked!", StringComparison.Ordinal));
                TestAssert.Equal(2_000L - 260, PlatformEconomy.For(accounts.Get(account.Id)).Rp, "the server charged its own price");

                // A forged view changes nothing but the page: unknown keys and values are dropped, text is encoded, nothing is echoed raw.
                var (_, forged) = await Buy("champion:40",
                    "kind=<script>&search=\"><img src=x onerror=alert(1)>&sort=evil&redirect=http://evil.example/&runeTier=9&notOwned=1");
                TestAssert.False(forged.Contains("evil.example", StringComparison.Ordinal), "no key outside the view parameters is read");
                TestAssert.False(forged.Contains("<img src=x", StringComparison.Ordinal), "the search text is encoded");
                TestAssert.True(forged.Contains("class=dlg-done href=\"/store/home?search=", StringComparison.Ordinal) &&
                    forged.Contains("&amp;notOwned=1&amp;sort=name#c-champion-40\"", StringComparison.Ordinal), "Done keeps only the normalized view");
                TestAssert.Equal(2_000L - 260 - 585, PlatformEconomy.For(accounts.Get(account.Id)).Rp, "the forged view did not touch the price");

                // Without a view (a form of an older page) or with an over-long one the page behaves as before: the item's list, default order.
                var (_, none) = await Buy("champion:13", null);
                TestAssert.True(none.Contains("class=dlg-done href=\"/store/home?kind=CHAMPION&amp;sort=name#c-champion-13\"", StringComparison.Ordinal));
                var (_, longView) = await Buy("champion:10", "kind=CHAMPION&search=" + new string('a', 600));
                TestAssert.True(longView.Contains("class=dlg-done href=\"/store/home?kind=CHAMPION&amp;sort=name#c-champion-10\"", StringComparison.Ordinal));
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Test seam: a clock fixed at one instant, so the sale calendar reads the same day every run.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
