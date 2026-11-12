using System.Net;
using System.Text.RegularExpressions;
using Amberline.Core;
using Amberline.Platform;
using Amberline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 751: the host's "Offer skins and RP" switch (<see cref="PlatformControls.PaidContentOff"/>). Off, no skin reaches the champion
/// list, champion select, the game or the store, and nothing can be bought; nothing anyone owns or saved is removed, so switching it back
/// on restores every skin as it was.
/// </summary>
public static class AirPaidContentChecks
{
    private const string SkinsOff = "Skins are turned off on this server.";

    [Check("server.air-paid-content-off-skins")]
    public static void SkinsOffOffersNoSkinAndKeepsWhatIsOwned() => SkinsOffAsync().GetAwaiter().GetResult();

    private static async Task SkinsOffAsync()
    {
        var root = TemporaryDirectory("air-paid-content-skins");
        try
        {
            // The store lists a skin only where its AIR art exists (PlatformStoreCatalog.SkinProducts), so the art is seeded.
            var art = Path.Combine(root, "assets", "storeImages", "content", "skins");
            Directory.CreateDirectory(art);
            foreach (var id in new[] { 1002, 101001 })
                File.WriteAllBytes(Path.Combine(art, $"championsskin_{id}.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"), root);
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock) { Push = (_, _) => { } };
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);

            // A full account saved a skin in its loadout and picks the champion with a skin; a fresh one can afford a skin.
            var player = SignIn(accounts, sessions, "skinsoffplayer", maxed: true);
            accounts.SaveLoadout(player.Id, accounts.Get(player.Id).Loadout with { Champion = AirGameDtos.ChampionNames[1], SkinId = 2 });
            var buyer = SignIn(accounts, sessions, "skinsoffbuyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 10_000, 5_000, Guid.NewGuid());
            accounts.Purchase(buyer.Id, "champion:1", "IP", Guid.NewGuid());
            var lobby = (AsObject)games.Create(accounts.Get(player.Id), "Skins off", "", GameMaps.Normal, 10,
                AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            games.Start(player.Id, Convert.ToInt32(lobby["id"]), Convert.ToInt64(lobby["optimisticLock"]));
            games.Pick(player.Id, 1);
            games.SelectSkin(player.Id, 1, 1002);

            int ListedSkins() => ((ArrayCollection)rpc.Route(player.Username, "inventoryService", "getAvailableChampions", [])!)
                .Cast<AsObject>().Sum(champion => ((ArrayCollection)champion["championSkins"]).Count);
            int StoreSkins() => accounts.StoreCatalog.Items.Count(item => item.Kind == "CHAMPION_SKIN");
            var listedBefore = ListedSkins();
            var storeBefore = StoreSkins();
            var buyerBefore = PlatformEconomy.For(accounts.Get(buyer.Id));
            TestAssert.True(listedBefore > 0, "on: the champion list sends skins");
            TestAssert.True(storeBefore > 0 && accounts.StoreCatalog.TryGet("skin:1002", out _), "on: the store lists the seeded skins");

            accounts.Controls.PaidContentOff = true;
            TestAssert.Equal(0, ListedSkins(), "off: every ChampionDTO has an empty championSkins");
            TestAssert.Equal(0, StoreSkins(), "off: the store catalog lists no skin");
            TestAssert.False(accounts.StoreCatalog.TryGet("skin:1002", out _), "off: a skin is not on offer, like a closed limited rune");
            TestAssert.True(accounts.StoreCatalog.TryGetKnown("skin:1002", out _), "off: a receipt still names the skin it bought");
            TestAssert.True(Refused(() => games.SelectSkin(player.Id, 1, 1001)), "off: champion select refuses a skin");
            TestAssert.True(Refused(() => accounts.Purchase(buyer.Id, "skin:1002", "RP", Guid.NewGuid())),
                "off: buying a skin is refused with a plain message");
            var buyerOff = PlatformEconomy.For(accounts.Get(buyer.Id));
            TestAssert.Equal(buyerBefore.Rp, buyerOff.Rp, "a refused skin purchase takes no RP");
            TestAssert.Equal(buyerBefore.OwnedSkins ?? [], buyerOff.OwnedSkins ?? [], "a refused skin purchase grants no skin");

            // The seat picked a skin before the switch and its saved loadout names another: the game gets the default look.
            games.LockIn(player.Id);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(0, roster.Single(seat => !seat.IsBot).SkinId, "off: the seat reaches the game with skin 0");
            TestAssert.Equal(2, accounts.Get(player.Id).Loadout.SkinId, "off: the saved loadout keeps its skin");
            TestAssert.True(Refused(() => accounts.SaveLoadout(player.Id, accounts.Get(player.Id).Loadout with { SkinId = 3 })),
                "off: a loadout cannot newly save a skin");

            accounts.Controls.PaidContentOff = false;
            TestAssert.Equal(listedBefore, ListedSkins(), "on again: the champion list sends every skin");
            TestAssert.Equal(storeBefore, StoreSkins(), "on again: the store lists every skin");
            TestAssert.Equal(2, accounts.Get(player.Id).Loadout.SkinId, "on again: the saved skin is unchanged");
            accounts.Purchase(buyer.Id, "skin:1002", "RP", Guid.NewGuid());
            TestAssert.True(PlatformEconomy.OwnsSkin(accounts.Get(buyer.Id), 1002), "on again: the same skin can be bought");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-paid-content-off-rp")]
    public static void RpOffShowsNoRpAndKeepsTheBalance() => RpOffAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Off, the client is sent an RP balance of 0, the store and the host's pages show no RP, nothing is bought, given or redeemed as RP,
    /// and the stored balance stays; on again, it shows again. A code redeemed while off keeps a ledger that loads.
    /// </summary>
    private static async Task RpOffAsync()
    {
        var root = TemporaryDirectory("air-paid-content-rp");
        var directory = Path.Combine(root, "accounts");
        try
        {
            long id;
            PlatformIssuedRewardCode both, rpOnly;
            using (var accounts = new PlatformAccounts(directory, root))
            {
                await using var manager = NonLaunchingManager(root);
                var games = new PlatformGames(accounts, manager, "127.0.0.1", new ManualClock()) { Push = (_, _) => { } };
                var sessions = new PlatformSessions(accounts);
                var rpc = new PlatformServices(accounts, sessions, games);
                var player = SignIn(accounts, sessions, "rpoffplayer", maxed: false);
                id = player.Id;
                // A champion bought with RP while on: its receipt is the Purchases page's RP row, and its request is replayed while off below.
                var rpChampion = accounts.StoreCatalog.Items.First(item => item.Kind == "CHAMPION" && item.ContentId != 1 && item.RpPrice is not null);
                var rpReceipt = Guid.NewGuid();
                accounts.GrantCurrency(id, 10_000, 500 + rpChampion.RpPrice!.Value, Guid.NewGuid());
                accounts.Purchase(id, rpChampion.Id, "RP", rpReceipt);
                both = accounts.CreateRewardCode(100, 50, Guid.NewGuid());
                rpOnly = accounts.CreateRewardCode(0, 75, Guid.NewGuid());
                double RpBalance() => Convert.ToDouble(((AsObject)rpc.Route(player.Username, "clientFacadeService", "getLoginDataPacketForUser", [])!)["rpBalance"]);
                long StoredRp() => PlatformEconomy.For(accounts.Get(id)).Rp;
                TestAssert.Equal(500d, RpBalance(), "on: LoginData sends the balance");

                accounts.Controls.PaidContentOff = true;
                TestAssert.Equal(0d, RpBalance(), "off: LoginData.rpBalance is 0");
                TestAssert.Equal(0d, Convert.ToDouble(AirDtos.ActiveBoostsNotification(accounts.Get(id), accounts.Controls.PaidContentOff)["rp"]),
                    "off: ActiveBoostsNotification.rp is 0");
                var catalog = accounts.StoreCatalog;
                TestAssert.True(catalog.Items.Count > 0 && catalog.Items.All(item => item.IpPrice is not null && item.RpPrice is null && item.SalePercent == 0),
                    "off: the store lists no RP price and no item sold for RP only");
                TestAssert.False(catalog.TryGet(PlatformStoreCatalog.IpTimedBoostId, out _), "off: an RP-only item is not on offer");
                TestAssert.Equal(0, catalog.ActiveSales().Count, "off: no sale (a sale lowers the RP price)");
                TestAssert.True(Refused(() => accounts.Purchase(id, "champion:1", "RP", Guid.NewGuid()), RpOff), "off: a purchase with RP is refused");
                TestAssert.True(Refused(() => accounts.GrantCurrency(id, 0, 10, Guid.NewGuid()), RpOff), "off: GrantCurrency with RP is refused");
                TestAssert.Equal(500L, StoredRp(), "off: the stored balance is untouched");

                // The store, through its routes.
                var pushes = new List<AsObject>();
                var web = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                web.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                await using (var store = web.Build())
                {
                    PlatformStore.Map(store, accounts, sessions, root, (_, value) => pushes.Add((AsObject)value));
                    await store.StartAsync();
                    var access = sessions.RedeemStoreTicket(sessions.IssueStoreTicket(player.Username));
                    using var client = new HttpClient { BaseAddress = new Uri(store.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
                    client.DefaultRequestHeaders.Add("Cookie", PlatformStore.StoreCookie + "=" + access.Token);
                    // The home, the champion and rune lists, the unlock dialog, the codes page and the Purchases page (its RP receipt) show RP
                    // while on (so the page checks below are not empty), and none of it once off, read live.
                    string[] pages = ["/store/home", "/store/home?kind=CHAMPION", "/store/home?kind=RUNE", "/store/item/champion%3A1", "/store/codes",
                        "/store/purchases"];
                    accounts.Controls.PaidContentOff = false;
                    foreach (var path in pages) TestAssert.True(RpShown(await client.GetStringAsync(path)).Success, $"on: {path} shows RP");
                    var homeOn = await client.GetStringAsync("/store/home");
                    TestAssert.True(homeOn.Contains(">Skins</a>", StringComparison.Ordinal) && homeOn.Contains("New Skins", StringComparison.Ordinal),
                        "on: the store has its Skins tab and skins promo");
                    TestAssert.True(RpShown(PlatformHome.RenderLanding(PlatformHomeModel.Testing(), catalog)).Success, "on: the Home's featured item shows RP");
                    accounts.Controls.PaidContentOff = true;
                    foreach (var path in pages) NoRp(path, await client.GetStringAsync(path));
                    var home = await client.GetStringAsync("/store/home");
                    TestAssert.False(home.Contains(">Skins</a>", StringComparison.Ordinal), "off: the store has no Skins tab");
                    TestAssert.False(home.Contains("New Skins", StringComparison.Ordinal), "off: the store home has no skins promo");
                    TestAssert.False((await client.GetStringAsync("/store/home?kind=CHAMPION")).Contains("On Sale", StringComparison.Ordinal),
                        "off: no On Sale filter (a sale lowers the RP price)");
                    using (var getRp = await client.GetAsync("/store/rp"))
                    {
                        TestAssert.Equal("/store/home", getRp.RequestMessage!.RequestUri!.AbsolutePath, "off: the Get RP page leads to the store home");
                        NoRp("the Get RP page", await getRp.Content.ReadAsStringAsync());
                    }
                    NoRp("the Home's featured item", PlatformHome.RenderLanding(PlatformHomeModel.Testing(), catalog));
                    NoRp("the store's error page", PlatformStore.ErrorPage("Refused.", catalog));
                    TestAssert.True(PlatformEconomy.For(accounts.Get(id)).Purchases!.Any(purchase => purchase.RequestId == rpReceipt && purchase.Currency == "RP"),
                        "off: the RP receipt the Purchases page leaves out stays stored");

                    // Replaying the RP purchase made while on (its own request id) answers with its receipt and prints no RP.
                    using (var replay = await client.PostAsync("/store/purchase", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["formToken"] = access.FormToken, ["itemId"] = rpChampion.Id, ["currency"] = "RP", ["requestId"] = rpReceipt.ToString(), ["quantity"] = "1",
                    })))
                    {
                        TestAssert.Equal(HttpStatusCode.OK, replay.StatusCode, "off: the replay answers with the recorded purchase");
                        NoRp("the replayed Item Unlocked page", await replay.Content.ReadAsStringAsync());
                    }
                    TestAssert.Equal(0, pushes.Count, "a replay pushes nothing");

                    // An IP purchase: the fulfillment and the balance push send RP 0.
                    using (var bought = await client.PostAsync("/store/purchase", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["formToken"] = access.FormToken, ["itemId"] = "champion:1", ["currency"] = "IP", ["requestId"] = Guid.NewGuid().ToString(), ["quantity"] = "1",
                    })))
                    {
                        TestAssert.Equal(HttpStatusCode.OK, bought.StatusCode, "off: an IP purchase goes through");
                        NoRp("the Item Unlocked page", await bought.Content.ReadAsStringAsync());
                    }
                    TestAssert.Equal(2, pushes.Count, "the fulfillment and the balance");
                    TestAssert.True(pushes.All(push => Convert.ToDouble(push["rp"]) == 0), "off: the fulfillment's rp and StoreAccountBalanceNotification.rp are 0");

                    // Codes: IP + RP grants the IP alone and is used up; RP alone is refused in plain words and stays unused.
                    async Task<string> Redeem(string code)
                    {
                        using var answer = await client.PostAsync("/store/codes/redeem", new FormUrlEncodedContent(new Dictionary<string, string>
                        { ["formToken"] = access.FormToken, ["code"] = code }));
                        return await answer.Content.ReadAsStringAsync();
                    }
                    var ipBefore = PlatformEconomy.For(accounts.Get(id)).Ip;
                    var redeemed = await Redeem(both.Code);
                    TestAssert.True(redeemed.Contains("Code redeemed", StringComparison.Ordinal) && redeemed.Contains("100 IP", StringComparison.Ordinal),
                        "off: the IP of the code is granted");
                    NoRp("the redeemed code", redeemed);
                    TestAssert.Equal(ipBefore + 100, PlatformEconomy.For(accounts.Get(id)).Ip, "off: the code grants its IP");
                    TestAssert.Equal(500L, StoredRp(), "off: the code grants no RP");
                    TestAssert.True(accounts.ListRewardCodes().Single(code => code.Id == both.Id).RedeemedAt is not null, "off: the code is used up");
                    TestAssert.Equal(3, pushes.Count, "the redemption pushes the balance once");
                    TestAssert.Equal(0d, Convert.ToDouble(pushes[2]["rp"]), "off: the redemption's balance push sends RP 0");
                    var refused = await Redeem(rpOnly.Code);
                    TestAssert.True(refused.Contains(RpOnlyCode, StringComparison.Ordinal), "off: an RP-only code is refused in plain words");
                    NoRp("the refused code", refused.Replace(RpOnlyCode, "", StringComparison.Ordinal));
                    TestAssert.True(accounts.ListRewardCodes().Single(code => code.Id == rpOnly.Id).RedeemedAt is null, "off: the RP-only code stays unused");
                    TestAssert.Equal(3, pushes.Count, "a refused code pushes nothing");
                    await store.StopAsync();
                }

                // The host's pages: no Give RP and no RP field of a new reward code; a code is made without one.
                foreach (var homeHost in new[] { false, true })
                {
                    var port = AirBootstrapChecks.FreePort();
                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
                    builder.Services.AddAntiforgery();
                    await using var manage = builder.Build();
                    PlatformManagement.Map(manage, accounts, port, AirBootstrapChecks.FreePort(), invite: homeHost ? HostInvite.UsersPath : null);
                    await manage.StartAsync();
                    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false };
                    using var host = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                    var page = await host.GetStringAsync("/manage");
                    var which = homeHost ? "the home host's Advanced" : "the operator's page";
                    TestAssert.False(page.Contains("Give RP", StringComparison.Ordinal) || page.Contains("Grant testing RP", StringComparison.Ordinal) ||
                        page.Contains("action=/manage/grant", StringComparison.Ordinal), $"off: {which} has no Give RP");
                    TestAssert.False(Regex.IsMatch(page, @"name=rp\b") || page.Contains(" RP</option>", StringComparison.Ordinal),
                        $"off: {which} has no RP field and no RP balance in its logins");
                    var token = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                    var made = accounts.ListRewardCodes().Select(code => code.Id).ToHashSet();
                    using var create = await host.PostAsync("/manage/reward-code/create", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        [token.Groups[1].Value] = WebUtility.HtmlDecode(token.Groups[2].Value), ["ip"] = "20", ["requestId"] = Guid.NewGuid().ToString(),
                    }));
                    TestAssert.Equal(HttpStatusCode.OK, create.StatusCode, $"off: {which} makes an IP code without an RP field");
                    var newest = accounts.ListRewardCodes().Single(code => !made.Contains(code.Id));
                    TestAssert.True(newest.Ip == 20 && newest.Rp == 0, $"off: {which}'s code gives 20 IP and no RP");
                    await manage.StopAsync();
                }
                TestAssert.Equal(500L, StoredRp(), "off: the stored balance is still untouched");

                accounts.Controls.PaidContentOff = false;
                TestAssert.Equal(500d, RpBalance(), "on again: LoginData sends the balance");
                TestAssert.True(catalog.TryGet(PlatformStoreCatalog.IpTimedBoostId, out _), "on again: an RP-only item is on offer");
            }

            // The ledger of a code redeemed while off (its IP granted, its RP not) loads.
            using var reopened = new PlatformAccounts(directory, root);
            TestAssert.Equal(500L, PlatformEconomy.For(reopened.Get(id)).Rp, "the stored balance survives a restart");
            TestAssert.True(reopened.ListRewardCodes().Single(code => code.Id == both.Id).RedeemedAt is not null &&
                reopened.ListRewardCodes().Single(code => code.Id == rpOnly.Id).RedeemedAt is null, "the codes keep their state");
        }
        finally { DeleteDirectory(root); }
    }

    private const string RpOff = "RP is turned off on this server.";
    private const string RpOnlyCode = "This code gives only RP, and RP is turned off on this server.";

    /// <summary>RP on a store or Home page: an "RP" word, Premium Points, the RP logo or the Get RP link, outside its stylesheet.</summary>
    private static Match RpShown(string html) => Regex.Match(Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline),
        @".{0,80}(\bRP\b|Premium Points|rp_logo|/store/rp).{0,80}");

    private static void NoRp(string page, string html)
    {
        var found = RpShown(html);
        TestAssert.False(found.Success, $"off: {page} shows no RP: {found.Value}");
    }

    internal static PlatformAccount SignIn(PlatformAccounts accounts, PlatformSessions sessions, string name, bool maxed)
    {
        var account = accounts.RegisterPublic(name, "testing-password", name, maxed);
        var session = sessions.Login(account.Username, account.Username, sessions.IssueTicket(account), AirQueueChecks.Version);
        TestAssert.True(sessions.AuthenticateChannel(account.Username, account.Username, session.Token));
        return account;
    }

    /// <summary>Whether the action is refused with the plain message (an ArgumentException may append its parameter name).</summary>
    internal static bool Refused(Action action, string message = SkinsOff)
    {
        try { action(); }
        catch (ArgumentException refused) { return refused.Message.StartsWith(message, StringComparison.Ordinal); }
        return false;
    }
}
