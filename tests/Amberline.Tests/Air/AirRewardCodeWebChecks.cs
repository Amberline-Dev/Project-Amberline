using System.Net;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

public static class AirRewardCodeWebChecks
{
    [Check("server.air-store-reward-code-web")]
    public static void StoreRewardCodesRequireSessionAndCsrfAreReplaySafeAndPushBalanceOnce() =>
        StoreRewardCodesAsync().GetAwaiter().GetResult();

    private static async Task StoreRewardCodesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-reward-code-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PlatformAccounts? store = null;
        try
        {
            store = new PlatformAccounts(root);
            var accounts = store!;
            var first = accounts.RegisterPublic("codefirst", "testing-password", "Code First", maxed: false);
            var second = accounts.RegisterPublic("codesecond", "testing-password", "Code Second", maxed: false);
            var firstCode = accounts.CreateRewardCode(321, 654, Guid.NewGuid()).Code;
            var sessions = new PlatformSessions(accounts);
            var firstAccess = StoreSession(sessions, first, "reward-code-first");
            var secondAccess = StoreSession(sessions, second, "reward-code-second");
            var pushes = new List<(long AccountId, AsObject Data)>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
            });
            PlatformStore.Map(app, accounts, sessions, root, (id, value) => pushes.Add((id, (AsObject)value)));
            await app.StartAsync();
            try
            {
                using var unauthenticated = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
                using var unauthenticatedResponse = await unauthenticated.GetAsync("/store/codes");
                TestAssert.Equal(HttpStatusCode.BadRequest, unauthenticatedResponse.StatusCode,
                    "code redemption requires a store session");

                using var firstClient = Client(app, firstAccess);
                using var codesPage = await firstClient.GetAsync("/store/codes");
                TestAssert.Equal(HttpStatusCode.OK, codesPage.StatusCode);
                TestAssert.True(codesPage.Headers.CacheControl?.NoStore == true,
                    "the code form and account balance must not be cached");
                var codesHtml = await codesPage.Content.ReadAsStringAsync();
                TestAssert.True(codesHtml.Contains("class=\"store-tab active\" href=\"/store/codes\">Codes", StringComparison.Ordinal),
                    "Codes remains the active store navigation tab");
                TestAssert.True(codesHtml.IndexOf(">Codes</a>", StringComparison.Ordinal) <
                    codesHtml.IndexOf(">Purchases</a>", StringComparison.Ordinal),
                    "Codes appears before Purchases in the store navigation");
                TestAssert.True(codesHtml.Contains("name=formToken", StringComparison.Ordinal));

                using var forged = await firstClient.PostAsync("/store/codes/redeem", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["code"] = firstCode }));
                TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "code redemption requires the store form token");
                TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(first.Id)).Ip);
                TestAssert.Equal(0, pushes.Count);

                using var malformed = await firstClient.PostAsync("/store/codes/redeem", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["formToken"] = firstAccess.FormToken, ["code"] = new string('A', 257) }));
                TestAssert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode, "code field length is bounded explicitly");
                TestAssert.Equal(0, pushes.Count);

                using var redeemed = await firstClient.PostAsync("/store/codes/redeem", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["formToken"] = firstAccess.FormToken, ["code"] = firstCode }));
                TestAssert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
                TestAssert.True(redeemed.Headers.CacheControl?.NoStore == true,
                    "the redemption receipt must not be cached");
                var redeemedHtml = await redeemed.Content.ReadAsStringAsync();
                TestAssert.True(redeemedHtml.Contains("Code redeemed", StringComparison.Ordinal));
                TestAssert.True(redeemedHtml.Contains("321 IP", StringComparison.Ordinal));
                TestAssert.True(redeemedHtml.Contains("654 RP", StringComparison.Ordinal));
                TestAssert.False(redeemedHtml.Contains(firstCode, StringComparison.Ordinal),
                    "the store does not echo the submitted secret");
                TestAssert.Equal(1, pushes.Count, "one balance notification follows the committed redemption");
                TestAssert.Equal(first.Id, pushes[0].AccountId);
                TestAssert.True(pushes[0].Data.TypeName.EndsWith("StoreAccountBalanceNotification", StringComparison.Ordinal));
                TestAssert.Equal(321L, PlatformEconomy.For(accounts.Get(first.Id)).Ip);
                TestAssert.Equal(654L, PlatformEconomy.For(accounts.Get(first.Id)).Rp);

                using var replay = await firstClient.PostAsync("/store/codes/redeem", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["formToken"] = firstAccess.FormToken, ["code"] = firstCode }));
                TestAssert.Equal(HttpStatusCode.OK, replay.StatusCode, "same-account redemption is repeat-safe");
                TestAssert.True((await replay.Content.ReadAsStringAsync()).Contains("already redeemed by this account", StringComparison.Ordinal));
                TestAssert.Equal(1, pushes.Count, "a replay does not push another balance notification");

                using var secondClient = Client(app, secondAccess);
                using var other = await secondClient.PostAsync("/store/codes/redeem", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["formToken"] = secondAccess.FormToken, ["code"] = firstCode }));
                TestAssert.Equal(HttpStatusCode.OK, other.StatusCode, "other-account use returns a generic store failure page");
                var otherHtml = await other.Content.ReadAsStringAsync();
                TestAssert.True(otherHtml.Contains("invalid or unavailable", StringComparison.Ordinal));
                TestAssert.False(otherHtml.Contains("Code First", StringComparison.Ordinal),
                    "other-account failures do not identify the redeemer");
                TestAssert.Equal(1, pushes.Count);
                TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(second.Id)).Ip);

                sessions.Logout("reward-code-first");
                using var expired = await firstClient.GetAsync("/store/codes");
                TestAssert.Equal(HttpStatusCode.BadRequest, expired.StatusCode, "logout invalidates the store session");
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            store?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static HttpClient Client(WebApplication app, PlatformSessions.StoreSession access)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("Cookie", PlatformStore.StoreCookie + "=" + access.Token);
        return client;
    }

    private static PlatformSessions.StoreSession StoreSession(PlatformSessions sessions, PlatformAccount account, string connection)
    {
        var session = sessions.Login(connection, account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
        TestAssert.True(sessions.AuthenticateChannel(connection, account.Username, session.Token));
        return sessions.RedeemStoreTicket(sessions.IssueStoreTicket(connection));
    }
}
