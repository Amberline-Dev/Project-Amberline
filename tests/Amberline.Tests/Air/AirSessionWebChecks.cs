using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Amberline.AirCompanion;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;

namespace Amberline.Tests.Air;

public static class AirSessionWebChecks
{
    private const string Version = "1.45.11_10_04_15_28";

    [Check("server.air-store-session-parent-binding")]
    public static void StoreTicketsAreSingleUseAndFollowParentLifecycle()
    {
        var root = TemporaryDirectory("league-air-store-session");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("storeparent", "testing-password", "Store Parent");
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var sessions = new PlatformSessions(accounts, clock);

            Authenticate(sessions, account, "air-old");
            var ticket = sessions.IssueStoreTicket("air-old");
            var access = sessions.RedeemStoreTicket(ticket);
            TestAssert.Equal(account.Id, access.AccountId);
            TestAssert.Equal(account.Id, sessions.RequireStoreSession(access.Token).AccountId);
            TestAssert.Throws<ArgumentException>(() => sessions.RedeemStoreTicket(ticket),
                "a store ticket is a one-time grant");

            Authenticate(sessions, account, "air-new");
            TestAssert.True(sessions.AccountForConnection("air-old") is null,
                "a replacement login invalidates the old platform connection");
            TestAssert.Throws<ArgumentException>(() => sessions.RequireStoreSession(access.Token),
                "a store session cannot outlive its replaced platform parent");

            var expiringTicket = sessions.IssueStoreTicket("air-new");
            var expiring = sessions.RedeemStoreTicket(expiringTicket);
            clock.Advance(TimeSpan.FromMinutes(31));
            TestAssert.True(sessions.AccountForConnection("air-new") is null,
                "the parent session expires with the store session");
            TestAssert.Throws<ArgumentException>(() => sessions.RequireStoreSession(expiring.Token),
                "an expired parent invalidates the store session");
        }
        finally { DeleteDirectory(root); }
    }

    // Batch 436: the stock client sends nothing while its game runs, so a login session whose RTMP connection dropped at
    // game start is still parked at game end. The 120 s resume window refused that reconnect (InvalidSession, then the
    // client logged out and faulted). Retail kept the session for its whole idle timeout; see docs/air-platform.md.
    [Check("server.air-parked-session-resumes-after-in-game-absence")]
    public static void ParkedSessionResumesAfterInGameAbsence()
    {
        var root = TemporaryDirectory("league-air-parked-resume");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("parkedgamer", "testing-password", "Parked Gamer");
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var sessions = new PlatformSessions(accounts, clock);

            Authenticate(sessions, account, "rtmp-game-start");
            var token = sessions.Require("rtmp-game-start").Token;
            sessions.Suspend("rtmp-game-start");
            clock.Advance(TimeSpan.FromMinutes(59));
            TestAssert.True(sessions.Resume("rtmp-game-end", account.Username, token),
                "a parked session resumes at game end, 59 minutes after the drop");
            TestAssert.True(!sessions.Resume("rtmp-replay", account.Username, token),
                "the old token is refused after a resume (the parked binding is single-use)");
            sessions.Logout("rtmp-game-end");

            Authenticate(sessions, account, "rtmp-long-game");
            var longToken = sessions.Require("rtmp-long-game").Token;
            sessions.Suspend("rtmp-long-game");
            clock.Advance(TimeSpan.FromMinutes(61));
            TestAssert.True(!sessions.Resume("rtmp-long-late", account.Username, longToken),
                "a parked session is refused 61 minutes after the drop (60-minute window)");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-remote-store-cookie-redirect")]
    public static void RemoteStoreProxyPreservesRedirectCookieAndStoreBoundary() =>
        RemoteStoreProxyAsync().GetAwaiter().GetResult();

    private static async Task RemoteStoreProxyAsync()
    {
        var root = TemporaryDirectory("league-air-remote-store");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"), root);
            var account = accounts.RegisterPublic("proxyuser", "testing-password", "Proxy User");
            var sessions = new PlatformSessions(accounts);
            Authenticate(sessions, account, "air-proxy");
            var ticket = sessions.IssueStoreTicket("air-proxy");

            using var certificate = CreateCertificate();
            var remotePort = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
                listen => listen.UseHttps(certificate)));
            await using var app = builder.Build();
            var sawAuthorization = false;
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("Authorization"))
                {
                    sawAuthorization = true;
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                await next(context);
            });
            PlatformStore.Map(app, accounts, sessions, root);
            await app.StartAsync();
            try
            {
                var invitation = new AirRemoteInvitation("127.0.0.1", remotePort, "",
                    certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: FreePort());
                await using var proxy = await AirRemoteProxy.StartWithPortsAsync(
                    invitation, FreePort(), FreePort(), FreePort());
                var baseUri = new Uri($"http://127.0.0.1:{proxy.HttpPort}");
                var cookieContainer = new CookieContainer();
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = true,
                    CookieContainer = cookieContainer,
                    UseProxy = false,
                };
                using var client = new HttpClient(handler) { BaseAddress = baseUri };

                using var origin = await client.GetAsync("/?sessionId=" + Uri.EscapeDataString(ticket));
                TestAssert.Equal(HttpStatusCode.Redirect, origin.StatusCode);
                TestAssert.Equal("/store/entry?sessionId=" + Uri.EscapeDataString(ticket), origin.Headers.Location?.OriginalString);
                using var entry = await client.GetAsync(origin.Headers.Location);
                TestAssert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
                TestAssert.Equal("/store/home", entry.Headers.Location?.OriginalString);
                var cookie = cookieContainer.GetCookies(new Uri(baseUri, "/store/home"))[PlatformStore.StoreCookie];
                TestAssert.True(cookie is not null && cookie.Value.Length == 64,
                    "the store session is returned as an opaque cookie");

                using var home = await client.GetAsync("/store/home");
                TestAssert.Equal(HttpStatusCode.OK, home.StatusCode);
                var html = await home.Content.ReadAsStringAsync();
                TestAssert.True(html.Contains("Influence Points", StringComparison.Ordinal));
                using var repeated = await client.GetAsync(origin.Headers.Location);
                TestAssert.Equal(HttpStatusCode.Redirect, repeated.StatusCode,
                    "reopening the origin entry can reuse its scoped cookie after the ticket is consumed");

                using var coldHandler = new HttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() };
                using var coldClient = new HttpClient(coldHandler) { BaseAddress = baseUri };
                using var product = await coldClient.GetAsync("/store/items/view_item/Champion/champions_1.html?sessionId=" +
                    Uri.EscapeDataString(sessions.IssueStoreTicket("air-proxy")));
                TestAssert.Equal(HttpStatusCode.OK, product.StatusCode,
                    "the stock champion purchase link can be the first store page opened");
                TestAssert.True((await product.Content.ReadAsStringAsync()).Contains("Annie", StringComparison.Ordinal));
                TestAssert.False(sawAuthorization, "public remote store forwarding must not invent a bearer header");
                TestAssert.True(AirRemoteProxy.IsAllowedPath("/"));
                TestAssert.True(AirRemoteTunnel.IsPublicPath("/"));
                TestAssert.True(AirRemoteProxy.IsAllowedPath("/store/entry"));
                TestAssert.True(AirRemoteProxy.IsAllowedPath("/store/home"));
                TestAssert.True(AirRemoteProxy.IsAllowedPath("/story"));
                TestAssert.True(AirRemoteTunnel.IsPublicPath("/story"));
                TestAssert.False(AirRemoteProxy.IsAllowedPath("/store/%2e%2e/manage"),
                    "encoded parent traversal cannot cross the store boundary");
                TestAssert.False(AirRemoteProxy.IsAllowedPath("/manage"),
                    "host management never enters the guest proxy allowlist");
            }
            finally { await app.StopAsync(); }
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air-management-grant-csrf-boundary")]
    public static void ManagementGrantRequiresLoopbackAndCsrf() => ManagementGrantAsync().GetAwaiter().GetResult();

    private static async Task ManagementGrantAsync()
    {
        var root = TemporaryDirectory("league-air-management-grant");
        var remote = Path.Combine(root, "remote");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("grantuser", "testing-password", "Grant User", maxed: false);
            var port = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery();
            await using var app = builder.Build();
            PlatformManagement.Map(app, accounts, port, 8391);
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = true,
                    CookieContainer = new CookieContainer(),
                    UseProxy = false,
                };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                    Timeout = TimeSpan.FromSeconds(10) };
                var html = await client.GetStringAsync("/manage");
                var field = Regex.Match(html, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                TestAssert.True(field.Success, "management page must expose an antiforgery form field");
                var fieldName = field.Groups[1].Value;
                var fieldValue = WebUtility.HtmlDecode(field.Groups[2].Value);

                using var forged = await client.PostAsync("/manage/grant", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["accountId"] = account.Id.ToString(), ["rp"] = "12345", ["requestId"] = Guid.NewGuid().ToString("D") }));
                TestAssert.False(forged.IsSuccessStatusCode, "a grant without CSRF must be rejected");
                TestAssert.Equal(0L, PlatformEconomy.For(accounts.Get(account.Id)).Rp);

                var requestId = Guid.NewGuid().ToString("D");
                var form = new Dictionary<string, string>
                {
                    [fieldName] = fieldValue, ["accountId"] = account.Id.ToString(),
                    ["rp"] = "12345", ["requestId"] = requestId
                };
                using var granted = await client.PostAsync("/manage/grant", new FormUrlEncodedContent(form));
                TestAssert.Equal(HttpStatusCode.Redirect, granted.StatusCode);
                TestAssert.Equal(12_345L, PlatformEconomy.For(accounts.Get(account.Id)).Rp);

                using var replay = await client.PostAsync("/manage/grant", new FormUrlEncodedContent(form));
                TestAssert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
                TestAssert.Equal(12_345L, PlatformEconomy.For(accounts.Get(account.Id)).Rp,
                    "the management request id makes an RP grant idempotent");
            }
            finally { await app.StopAsync(); }
        }
        finally { DeleteDirectory(root); }
    }

    private static void Authenticate(PlatformSessions sessions, PlatformAccount account, string connection)
    {
        var session = sessions.Login(connection, account.Username, sessions.IssueTicket(account), Version);
        TestAssert.True(sessions.AuthenticateChannel(connection, account.Username, session.Token));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=air-store-check", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string TemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = current;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan amount) => Now += amount;
    }
}
