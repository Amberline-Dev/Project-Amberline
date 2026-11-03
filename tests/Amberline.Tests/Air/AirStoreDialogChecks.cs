using System.Net;
using Amberline.AirCompanion;
using Amberline.Data;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch W14 (18 October 2026): the store has no product page; a card opens the client's own champion window through
/// <c>parentSandboxBridge.openInventoryBrowser</c> and Unlock opens a dialog over the same view. Written, not run (owner rule).
/// </summary>
public static class AirStoreDialogChecks
{
    // The store lists a skin only where the AIR art of that skin exists (SkinProducts), so a check that needs skin products seeds the art.
    private static void SeedSkinArt(string root, params int[] skinIds)
    {
        var folder = Path.Combine(root, "assets", "storeImages", "content", "skins");
        Directory.CreateDirectory(folder);
        foreach (var id in skinIds) File.WriteAllBytes(Path.Combine(folder, $"championsskin_{id}.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
    }

    private static PlatformSessions.StoreSession SessionFor(PlatformAccount account) =>
        new("opaque", account.Id, "form-token", DateTimeOffset.UtcNow.AddMinutes(10));

    [Check("server.air-store-card-hover-opens-native-champion-window-or-unlock")]
    public static void CardsCarryTheNativeWindowCallAndTheUnlockLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-dialog-cards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SeedSkinArt(root, 1002, 101001);
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var fresh = accounts.RegisterPublic("dialogcards", "secret-password", "Dialog Cards", maxed: false);
            var champions = PlatformStore.RenderHome(SessionFor(fresh), accounts, "CHAMPION");
            // The ids StoreController.parseItemId reads: champions_<id> and championsskin_<globalSkinId>.
            TestAssert.True(champions.Contains("storeInfo('champions_1','')", StringComparison.Ordinal));
            TestAssert.True(champions.Contains("class=card-buy href=\"/store/item/champion%3A1?kind=CHAMPION&amp;sort=name#c-champion-1\"", StringComparison.Ordinal),
                "the lower half opens the unlock dialog over the same view and scrolls back to the card");
            TestAssert.True(champions.Contains("id=\"c-champion-1\"", StringComparison.Ordinal));
            TestAssert.False(champions.Contains("class=card-link", StringComparison.Ordinal), "the card is no link to a product page");
            var skins = PlatformStore.RenderHome(SessionFor(fresh), accounts, "CHAMPION_SKIN");
            TestAssert.True(skins.Contains("storeInfo('champions_1','championsskin_1002')", StringComparison.Ordinal));
            // Xerath (playable since 26 October 2026, batch 341) keeps the window link and, like every card since the owner decision of
            // 18 October 2026 (night), its Unlock half too: the dialog a card opens states why its item cannot be unlocked.
            TestAssert.True(champions.Contains("storeInfo('champions_101','')", StringComparison.Ordinal));
            TestAssert.True(champions.Contains("class=card-buy href=\"/store/item/champion%3A101?", StringComparison.Ordinal));
            var runes = PlatformStore.RenderHome(SessionFor(fresh), accounts, "RUNE");
            // Every page's script defines storeInfo(); a card's magnifier is the `return storeInfo(` call of its onclick.
            TestAssert.False(runes.Contains("return storeInfo(", StringComparison.Ordinal), "runes have no champion window");
            TestAssert.True(runes.Contains("class=card-buy", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-refused-card-keeps-unlock-half-and-the-dialog-states-the-reason")]
    public static void ACardThatCannotBeBoughtStillOpensTheDialogWithTheReasonAndNothingToSubmit()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-dialog-refused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SeedSkinArt(root, 1002, 101001);
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var fresh = accounts.RegisterPublic("dialogrefused", "secret-password", "Dialog Refused", maxed: false);
            accounts.GrantCurrency(fresh.Id, 0, 5_000, Guid.NewGuid());
            var session = SessionFor(accounts.Get(fresh.Id));
            var tierTwoRune = new RuneCatalog().All.First(rune => rune.Tier == RuneTier.Two).Id;

            string Unlock(string kind, string id, string cardId) =>
                $"class=card-buy href=\"/store/item/{id.Replace(":", "%3A")}?kind={kind}&amp;sort=name#{cardId}\"";

            // A skin whose champion is not owned (Goth Annie) keeps both hover halves, the magnifier and Unlock. Every champion and skin plays
            // since batch 341, so the locked products are the three bundles whose contents are not implemented; a locked card keeps Unlock too.
            var skins = PlatformStore.RenderHome(session, accounts, "CHAMPION_SKIN");
            TestAssert.True(skins.Contains(Unlock("CHAMPION_SKIN", "skin:1002", "c-skin-1002"), StringComparison.Ordinal),
                "a skin card whose champion is not owned keeps its Unlock half");
            TestAssert.True(skins.Contains("storeInfo('champions_1','championsskin_1002')", StringComparison.Ordinal));
            var bundles = PlatformStore.RenderHome(session, accounts, "BUNDLE");
            TestAssert.True(bundles.Contains(Unlock("BUNDLE", "bundle:champions-bundle", "c-bundle-champions-bundle"), StringComparison.Ordinal),
                "a locked bundle keeps its Unlock half");
            var runes = PlatformStore.RenderHome(session, accounts, "RUNE");
            TestAssert.True(runes.Contains(Unlock("RUNE", $"rune:{tierTwoRune}", $"c-rune-{tierTwoRune}"), StringComparison.Ordinal),
                "a rune card above the account level keeps its Unlock half");
            // The card's own footer still names the reason.
            TestAssert.True(skins.Contains("<span>Requires champion</span>", StringComparison.Ordinal));

            // The dialog it opens states the reason and carries nothing to submit.
            string Dialog(string id)
            {
                var page = PlatformStore.RenderProduct(session, accounts, id);
                TestAssert.True(page.Contains("Purchase Item", StringComparison.Ordinal) && page.Contains("class=dlg-banner>", StringComparison.Ordinal), id);
                TestAssert.False(page.Contains("class=dlg-form", StringComparison.Ordinal), id + ": nothing can be submitted");
                TestAssert.False(page.Contains("action=\"/store/purchase\"", StringComparison.Ordinal), id + ": no purchase form");
                return page;
            }
            TestAssert.True(Dialog("skin:1002").Contains("Requires Annie to be owned.", StringComparison.Ordinal));
            TestAssert.True(Dialog("bundle:champions-bundle").Contains("This bundle is not available on this server, so it cannot be unlocked.", StringComparison.Ordinal));
            TestAssert.True(Dialog("bundle:champions-bundle").Contains("class=dlg-banner>Unavailable<", StringComparison.Ordinal));
            var runeDialog = Dialog($"rune:{tierTwoRune}");
            TestAssert.True(runeDialog.Contains("class=dlg-banner>Level 10<", StringComparison.Ordinal) &&
                runeDialog.Contains("Requires summoner level 10.", StringComparison.Ordinal));

            // The server alone decides: the same refusals come back from the purchase, whatever the dialog or a forged form says.
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(fresh.Id, "skin:1002", "RP", Guid.NewGuid()));
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(fresh.Id, "bundle:champions-bundle", "RP", Guid.NewGuid()));
            TestAssert.Throws<ArgumentException>(() => accounts.Purchase(fresh.Id, $"rune:{tierTwoRune}", "IP", Guid.NewGuid()));
            TestAssert.Equal(5_000L, PlatformEconomy.For(accounts.Get(fresh.Id)).Rp, "no refusal charged anything");

            // An owned item: the all-unlocked account owns every playable champion, skin and rune.
            var maxed = accounts.RegisterPublic("dialogowned", "secret-password", "Dialog Owned");
            var owned = SessionFor(accounts.Get(maxed.Id));
            var ownedChampions = PlatformStore.RenderHome(owned, accounts, "CHAMPION");
            TestAssert.True(ownedChampions.Contains(Unlock("CHAMPION", "champion:1", "c-champion-1"), StringComparison.Ordinal),
                "an owned champion card keeps its Unlock half");
            var ownedPage = PlatformStore.RenderProduct(owned, accounts, "champion:1");
            TestAssert.True(ownedPage.Contains("class=dlg-banner>Owned<", StringComparison.Ordinal) &&
                ownedPage.Contains("You already own this champion.", StringComparison.Ordinal));
            TestAssert.False(ownedPage.Contains("class=dlg-form", StringComparison.Ordinal));
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(maxed.Id, "champion:1", "RP", Guid.NewGuid()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-unlock-dialog-columns-and-refusals")]
    public static void DialogShowsOneColumnPerCurrencyAndExplainsRefusals()
    {
        if (AirStoreSalesChecks.Without2011Store()) return;
        var root = Path.Combine(Path.GetTempPath(), "league-air-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SeedSkinArt(root, 1002, 101001);
        try
        {
            // Pinned to a July day, outside every 2011 sale window: Annie's 260 RP price is the retail price the rows below expect. Test seam only.
            using var accounts = new PlatformAccounts(root, root, new FixedClock(new DateTimeOffset(2026, 7, 15, 19, 0, 0, TimeSpan.Zero)));
            var fresh = accounts.RegisterPublic("dialogfresh", "secret-password", "Dialog Fresh", maxed: false);
            accounts.GrantCurrency(fresh.Id, 568, 640, Guid.NewGuid());
            var session = SessionFor(fresh);
            var champion = PlatformStore.RenderProduct(session, accounts, "champion:1");
            TestAssert.True(champion.Contains("Purchase Item", StringComparison.Ordinal) &&
                champion.Contains("Unlock With", StringComparison.Ordinal));
            TestAssert.Equal(2, champion.Split("class=dlg-form").Length - 1);
            TestAssert.True(champion.Contains("name=currency value=\"RP\"", StringComparison.Ordinal) &&
                champion.Contains("name=currency value=\"IP\"", StringComparison.Ordinal));
            TestAssert.Equal(2, champion.Split("name=formToken value=\"form-token\"").Length - 1,
                "each currency form carries the form token once (two forms)");
            // Balance, price and remainder are printed from the account and the catalog; the server prices the purchase itself.
            TestAssert.True(champion.Contains("class=\"dlg-rem\">380<", StringComparison.Ordinal), "640 RP - 260 RP");
            // Skins sell for RP only.
            var skin = PlatformStore.RenderProduct(session, accounts, "skin:1002");
            TestAssert.False(skin.Contains("name=currency value=\"IP\"", StringComparison.Ordinal));
            // Too little RP: the column stays, the button is the disabled sprite and nothing can be submitted for it.
            var poor = accounts.RegisterPublic("dialogpoor", "secret-password", "Dialog Poor", maxed: false);
            var poorPage = PlatformStore.RenderProduct(SessionFor(poor), accounts, "champion:1");
            TestAssert.False(poorPage.Contains("class=dlg-form", StringComparison.Ordinal));
            TestAssert.True(poorPage.Contains("Not enough Premium Points", StringComparison.Ordinal));
            // Refusals keep the old product page's wording, now in the dialog's banner and note.
            TestAssert.True(skin.Contains("class=dlg-banner>Requires champion<", StringComparison.Ordinal));
            TestAssert.True(skin.Contains("Requires Annie to be owned.", StringComparison.Ordinal));
            var locked = PlatformStore.RenderProduct(session, accounts, "bundle:champions-bundle");
            TestAssert.True(locked.Contains("This bundle is not available on this server, so it cannot be unlocked.", StringComparison.Ordinal));
            TestAssert.False(locked.Contains("class=dlg-form", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-view-item-route-opens-dialog-and-bundled-art-route")]
    public static void StockViewItemPathOpensTheDialogAndTheBundledArtRouteIsAllowListed() => StockViewItemPathOpensTheDialogAndTheBundledArtRouteIsAllowListedAsync().GetAwaiter().GetResult();

    private static async Task StockViewItemPathOpensTheDialogAndTheBundledArtRouteIsAllowListedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-dialog-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var sessions = new PlatformSessions(accounts);
            var fresh = accounts.RegisterPublic("dialoghttp", "secret-password", "Dialog Http", maxed: false);
            var access = SessionFor(fresh) with { Token = "opaque" };
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
            PlatformHome.MapImages(app, root);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
                // A store ticket belongs to a logged-in platform session, as the stock client's RTMP login makes one.
                var login = sessions.Login("dialog-check", fresh.Username, sessions.IssueTicket(fresh), "1.45.11_10_04_15_28");
                TestAssert.True(sessions.AuthenticateChannel("dialog-check", fresh.Username, login.Token));
                var ticket = sessions.IssueStoreTicket("dialog-check");
                var page = await client.GetStringAsync("/store/items/view_item/Champion/champions_1?sessionId=" + Uri.EscapeDataString(ticket));
                TestAssert.True(page.Contains("Purchase Item", StringComparison.Ordinal) && page.Contains("id=\"c-champion-1\"", StringComparison.Ordinal),
                    "the Home's Unlock lands on the champions list with the dialog open");
                TestAssert.True(page.Contains("data-card=\"c-champion-1\"", StringComparison.Ordinal));
                using var unknown = await client.GetAsync("/store/item/champion%3A99999");
                TestAssert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
                using var bad = await client.GetAsync("/store/items/view_item/Champion/champions_x");
                TestAssert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
                // The store frame maps /img/ to the client's own assets, so its bundled art has its own path with the same allow-list.
                using var traversal = await client.GetAsync("/store/img/home/..%2Faccounts.json");
                TestAssert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
                using var unknownFile = await client.GetAsync("/store/img/home/not-a-bundled-file.png");
                TestAssert.Equal(HttpStatusCode.NotFound, unknownFile.StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-store-friz-font-url-matches-the-companion-server")]
    public static void PagesNameTheCompanionsLoopbackFontUrls()
    {
        var face = PlatformStore.FrizFaceCss;
        TestAssert.True(face.Contains($"http://127.0.0.1:{FrizFontServer.Port}{FrizFontServer.RegularPath}", StringComparison.Ordinal));
        TestAssert.True(face.Contains($"http://127.0.0.1:{FrizFontServer.Port}{FrizFontServer.BoldPath}", StringComparison.Ordinal));
        TestAssert.True(face.Contains($"'{FrizFont.Family}'", StringComparison.Ordinal));
        TestAssert.True(PlatformStore.FrizStack.EndsWith("Arial,Helvetica,sans-serif", StringComparison.Ordinal), "an Arial-family fallback");
        TestAssert.False(face.Contains("Georgia", StringComparison.Ordinal));
    }

    /// <summary>Test seam: a clock fixed at one instant, so the sale calendar reads the same day every run.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
