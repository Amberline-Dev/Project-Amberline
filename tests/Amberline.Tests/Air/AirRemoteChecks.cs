using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Amberline.Tests.Air;

public static class AirRemoteChecks
{
    [Check("server.air-remote-invitation-contract")]
    public static void InvitationUsesExportedBearerAndPinFields()
    {
        var invitation = Invitation();
        invitation.Validate();
        var json = JsonSerializer.Serialize(invitation, Amberline.Host.HostFiles.Json);
        TestAssert.True(json.Contains("\"token\"", StringComparison.Ordinal));
        TestAssert.True(json.Contains("\"certificateSha256\"", StringComparison.Ordinal));
        TestAssert.False(json.Contains("token64hex", StringComparison.OrdinalIgnoreCase));
        TestAssert.False(json.Contains("certificateSha25664hex", StringComparison.OrdinalIgnoreCase));

        var roundTrip = JsonSerializer.Deserialize<AirRemoteInvitation>(json, Amberline.Host.HostFiles.Json)!;
        TestAssert.Equal(invitation, roundTrip);
    }

    [Check("server.air-remote-invitation-rejects-invalid-credentials")]
    public static void InvitationRejectsWrongTokenAndPinShapes()
    {
        TestAssert.Throws<ArgumentException>(() => new AirRemoteInvitation(
            "127.0.0.1", Token64hex: new string('a', 63),
            CertificateSha25664hex: new string('b', 64)).Validate());
        TestAssert.Throws<ArgumentException>(() => new AirRemoteInvitation(
            "127.0.0.1", Token64hex: new string('a', 64),
            CertificateSha25664hex: new string('g', 64)).Validate());
        TestAssert.Throws<ArgumentException>(() => new AirRemoteInvitation(
            "http://127.0.0.1", Token64hex: new string('a', 64),
            CertificateSha25664hex: new string('b', 64)).Validate());
    }

    [Check("server.air-public-port-rate-limits")]
    public static void PublicPortCapsSpareTheHostLoopback()
    {
        static System.Threading.RateLimiting.RateLimitPartition<string> Partition(string address, string path)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Request.Method = "GET";
            context.Request.Path = path;
            return Amberline.Platform.Program.RequestPartition(context);
        }
        const string friend = "203.0.113.9";
        TestAssert.Equal("store-images", Partition("127.0.0.1", "/store/art/card.jpg").PartitionKey);
        TestAssert.Equal("images:" + friend, Partition(friend, "/store/art/card.jpg").PartitionKey);
        TestAssert.Equal("images:" + friend, Partition(friend, "/store/img/layout/home_bg.jpg").PartitionKey);
        TestAssert.Equal("images:" + friend, Partition(friend, "/img/home/feature-101").PartitionKey);
        TestAssert.Equal("127.0.0.1", Partition("127.0.0.1", "/bootstrap/companion.zip").PartitionKey);
        TestAssert.Equal(friend, Partition(friend, "/landing").PartitionKey);
        var companion = Partition(friend, "/bootstrap/companion.zip");
        TestAssert.Equal("companion:" + friend, companion.PartitionKey);
        using var limiter = companion.Factory(companion.PartitionKey);
        for (var i = 0; i < 6; i++) TestAssert.True(limiter.AttemptAcquire().IsAcquired, "six companion downloads an hour pass");
        TestAssert.False(limiter.AttemptAcquire().IsAcquired, "the seventh download within the hour is refused");
    }

    [Check("server.air-remote-proxy-allowlist")]
    public static void ProxyDeniesManagementPathsWithoutContactingRemote() => ProxyDenialAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-tunnel-auth-boundary")]
    public static void TunnelRequiresHttpsAndExactBearer()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "Bearer " + new string('a', 64);
        TestAssert.False(AirRemoteTunnel.AuthorizeBearer(context, _ => true),
            "plain HTTP must never authorize an AIR tunnel");
        context.Request.IsHttps = true;
        context.Request.Headers["Authorization"] = "Bearer wrong";
        TestAssert.False(AirRemoteTunnel.AuthorizeBearer(context, _ => true),
            "short bearer values must be rejected before authentication");
        context.Request.Headers["Authorization"] = "Bearer " + new string('a', 64);
        TestAssert.True(AirRemoteTunnel.AuthorizeBearer(context, token => token.Length == 64));
        TestAssert.Equal("/air/rtmp", AirRemoteTunnel.Path);
        TestAssert.Equal("/air/xmpp", AirRemoteTunnel.ChatPath);
    }

    [Check("server.air-remote-byte-fidelity-and-auth")]
    public static void ProxyPinsCertificateForwardsBytesAndRejectsWrongBearer() => ProxyRoundTripAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-proxy-serves-home-and-store-assets")]
    public static void ProxyForwardsResponsesLargerThanTheRequestCap() => ProxyLargeResponseAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-wss-byte-fidelity-rtmp-xmpp")]
    public static void RtmpAndXmppWssTunnelsPreserveBytes() => WssRoundTripAsync().GetAwaiter().GetResult();

    // Batch 544: every tunnel end logs which side ended first and why (DIAGNOSTICS air.tunnel-end).
    [Check("server.air-remote-tunnel-end-names-first-side")]
    public static void TunnelEndLineNamesTheSideThatEndedFirst() => TunnelEndLineAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-token-gate")]
    public static void TunnelRefusesMissingWrongRevokedAndExpiredTokensBeforeASlot() => TokenGateAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-sign-in")]
    public static void SignInIssuesOneRevocableTokenPerAccountAndTheCompanionCachesIt() => SignInAsync().GetAwaiter().GetResult();

    // b353: the friend's login in the AIR client is the sign-in. The reply to AIR waits for the bearer, so AIR's tunnels carry it.
    [Check("server.air-remote-login-queue-token")]
    public static void LoginQueueReplyCarriesTheBearerThatTheTunnelsUse() => LoginQueueTokenAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-login-queue-refusal")]
    public static void WrongLoginIsPassedThroughWithoutABearerOrAConsoleRead() => LoginQueueRefusalAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-cached-token-skips-sign-in")]
    public static void ACachedBearerSkipsTheSignInThroughTheLoginQueue() => CachedTokenSkipsSignInAsync().GetAwaiter().GetResult();

    [Check("server.air-remote-password-never-persisted")]
    public static void TheLoginPasswordStaysOutOfTheCacheAndTheConsole() => PasswordNeverPersistedAsync().GetAwaiter().GetResult();

    [Check("server.air-seat-origin")]
    public static void ASeatMayCheatOnlyWhenItsSessionReachedThisPcDirectly() => SeatOrigin();

    private static void SeatOrigin()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-seat-origin-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(root);
        var local = accounts.RegisterPublic("origin_local", "correct-horse-1", "Local");
        var tunneled = accounts.RegisterPublic("origin_tunnel", "correct-horse-1", "Tunnel");
        var sessions = new PlatformSessions(accounts);
        const string version = "1.45.11_10_04_15_28";
        sessions.Login("direct-connection", local.Username, sessions.IssueTicket(local), version);
        sessions.Login("tunnel-connection", tunneled.Username, sessions.IssueTicket(tunneled), version);
        sessions.ObserveOrigin("direct-connection", throughTunnel: false);
        sessions.ObserveOrigin("tunnel-connection", throughTunnel: true);
        TestAssert.True(sessions.IsLocalAccount(local.Id), "a seat that reached this PC directly may cheat");
        TestAssert.False(sessions.IsLocalAccount(tunneled.Id), "a seat that came through a remote tunnel may not cheat");
        TestAssert.False(sessions.IsLocalAccount(987654321), "an account with no session may not cheat");
        sessions.Logout("direct-connection");
        TestAssert.False(sessions.IsLocalAccount(local.Id), "a logged-out seat loses its cheat seat");
    }

    private static async Task TokenGateAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        var root = Path.Combine(Path.GetTempPath(), "league-air-remote-gate-" + Guid.NewGuid().ToString("N"));
        var issuer = new AirRemoteAccess(root);
        var live = issuer.Issue(1).Token;
        // One account holds at most four tunnels, so seven more accounts fill the 32 shared slots and a ninth finds them full.
        var others = Enumerable.Range(10, 7).Select(id => issuer.Issue(id).Token).ToArray();
        var spare = issuer.Issue(20).Token;
        var revoked = issuer.Issue(2).Token;
        issuer.Revoke(2);
        // Issued a month ago, so it expired a day before the platform reads the store again.
        var expired = new AirRemoteAccess(root, new FixedClock(DateTimeOffset.UtcNow.AddDays(-31))).Issue(3).Token;
        var access = new AirRemoteAccess(root);
        var remotePort = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        AirRemoteTunnel.Map(app, access, ((IPEndPoint)rtmpBackend.LocalEndpoint).Port, ((IPEndPoint)xmppBackend.LocalEndpoint).Port);
        await app.StartAsync(stop.Token);
        var held = new List<ClientWebSocket>();
        async Task<string> ConnectAsync(string? bearer)
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            if (bearer is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
            try
            {
                await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{remotePort}{AirRemoteTunnel.Path}"), stop.Token);
                held.Add(socket);
                return "connected";
            }
            catch (WebSocketException ex)
            {
                socket.Dispose();
                return ex.Message;
            }
        }
        try
        {
            // The status code is in the handshake error, which is how the refusal reaches the companion.
            var unknown = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            TestAssert.True((await ConnectAsync(null)).Contains("'401'"), "a tunnel without a token is refused with 401");
            TestAssert.True((await ConnectAsync(unknown)).Contains("'401'"), "an unknown token is refused with 401");
            TestAssert.True((await ConnectAsync(revoked)).Contains("'401'"), "a revoked token is refused with 401");
            TestAssert.True((await ConnectAsync(expired)).Contains("'401'"), "an expired token is refused with 401");
            TestAssert.Equal("connected", await ConnectAsync(live));
            for (var i = 1; i < AirRemoteTunnel.MaxConnectionsPerAccount; i++) TestAssert.Equal("connected", await ConnectAsync(live));
            TestAssert.True((await ConnectAsync(live)).Contains("'503'"), "one account's token holds at most four tunnels");
            foreach (var token in others)
                for (var i = 0; i < AirRemoteTunnel.MaxConnectionsPerAccount; i++) TestAssert.Equal("connected", await ConnectAsync(token));
            // All 32 slots are held: the refusal must come from the token gate, not from a full pool.
            TestAssert.True((await ConnectAsync(null)).Contains("'401'"), "a tokenless request is refused before the slot check");
            TestAssert.True((await ConnectAsync(spare)).Contains("'503'"), "a valid token still waits for a free slot");
            // Five more failures bring this address to the throttle limit; a valid token is then refused with 429.
            for (var i = 0; i < 5; i++) TestAssert.True((await ConnectAsync(unknown)).Contains("'401'"), "wrong tokens are refused until the address is throttled");
            TestAssert.True((await ConnectAsync(live)).Contains("'429'"), "a throttled address is refused even with a valid token");
        }
        finally
        {
            foreach (var socket in held) socket.Dispose();
            await app.StopAsync();
        }
    }

    private static async Task SignInAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(Path.GetTempPath(), "league-air-remote-signin-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(root);
        var account = accounts.RegisterPublic("friend_login", "correct-horse-1", "Friend");
        var access = new AirRemoteAccess(root);
        var remotePort = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        // Records every bearer the platform receives, so the check can see whether a cached token was ever sent.
        var bearers = new List<string>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Authorization"))
                lock (bearers) bearers.Add(context.Request.Headers.Authorization.ToString());
            await next(context);
        });
        AirRemoteTunnel.MapSignIn(app, accounts, access);
        await app.StartAsync(stop.Token);
        try
        {
            var invitation = new AirRemoteInvitation("127.0.0.1", remotePort, "",
                certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: FreePort());
            var connections = Path.Combine(root, "connections");
            using var pinned = AirRemoteProxy.CreatePinnedClient(Convert.FromHexString(invitation.CertificateSha25664hex));
            // A refused login is answered with 403 and caches nothing; the right login signs in and caches the bearer.
            HttpStatusCode? refused = null;
            try { await RemoteSignIn.SignInAsync(pinned, invitation, connections, new RemoteSignIn.Credentials("friend_login", "wrong-horse-22"), stop.Token); }
            catch (HttpRequestException error) { refused = error.StatusCode; }
            TestAssert.Equal<HttpStatusCode?>(HttpStatusCode.Forbidden, refused, "a wrong login is refused by the platform");
            TestAssert.False(File.Exists(Path.Combine(connections, "remote-token.json")), "a refused login caches nothing");
            var signedIn = await RemoteSignIn.SignInAsync(pinned, invitation, connections,
                new RemoteSignIn.Credentials("friend_login", "correct-horse-1"), stop.Token);
            TestAssert.True(access.Authorize(signedIn), "the signed-in token is live on the platform");
            TestAssert.True(File.Exists(Path.Combine(connections, "remote-token.json")), "the token is cached in the stage");
            var reused = await RemoteSignIn.TryCachedTokenAsync(invitation, connections, stop.Token);
            TestAssert.Equal(signedIn, reused?.EffectiveToken, "the cached token is used without a login");
            // Revoked on the platform: the cached copy is refused, so there is no bearer and the friend signs in again.
            access.Revoke(account.Id);
            TestAssert.False(access.Authorize(signedIn), "a revoked token is no longer live");
            TestAssert.True((await RemoteSignIn.TryCachedTokenAsync(invitation, connections, stop.Token)) is null,
                "a revoked cached token sends the friend back to the login");
            // A cache written for one host, port or pin is never sent to another: each lookup below answers without a request.
            var signedAgain = await RemoteSignIn.SignInAsync(pinned, invitation, connections,
                new RemoteSignIn.Credentials("friend_login", "correct-horse-1"), stop.Token);
            var before = bearers.Count;
            var sameServerOtherName = new AirRemoteInvitation("localhost", remotePort, "",
                certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: FreePort());
            TestAssert.True((await RemoteSignIn.TryCachedTokenAsync(sameServerOtherName, connections, stop.Token)) is null,
                "a cache for 127.0.0.1 is not used for localhost, even on the same server and pin");
            var otherPin = new AirRemoteInvitation("127.0.0.1", remotePort, "", new string('0', 64), ChatPort: FreePort());
            TestAssert.True((await RemoteSignIn.TryCachedTokenAsync(otherPin, connections, stop.Token)) is null,
                "a cache is not used for the same host with a different pin");
            lock (bearers) TestAssert.Equal(before, bearers.Count, "no request carried a cached token to another host or pin");
            TestAssert.True(access.Authorize(signedAgain), "the account signs in again after the revoke");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task LoginQueueTokenAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = NewLoginRoot();
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        await using var platform = await LoginPlatform.StartAsync(root, certificate,
            ((IPEndPoint)rtmpBackend.LocalEndpoint).Port, ((IPEndPoint)xmppBackend.LocalEndpoint).Port, stop.Token);
        var connections = Path.Combine(root, "connections");
        await using var proxy = await AirRemoteProxy.StartWithPortsAsync(PinnedInvitation(certificate, platform.RemotePort),
            FreePort(), FreePort(), FreePort(), stop.Token, connections);
        var reply = await PostLoginAsync(proxy.HttpPort, "friend_login", "correct-horse-1");
        TestAssert.Equal(HttpStatusCode.OK, reply.Status, "a valid login gets the platform's LOGIN reply");
        TestAssert.True(reply.Body.Contains("LOGIN", StringComparison.Ordinal), "the reply is the platform's LOGIN answer");
        // The bearer is stored before the reply reaches AIR, so the RTMP and chat connections AIR opens after it carry it.
        TestAssert.True(File.Exists(Path.Combine(connections, "remote-token.json")), "the bearer is cached before the login reply is returned");
        TestAssert.Equal(1, platform.SignIns, "one sign-in request for the login");
        await TunnelCarriesBytesAsync(proxy.RtmpPort, rtmpBackend, stop.Token);
        await TunnelCarriesBytesAsync(proxy.ChatPort, xmppBackend, stop.Token);
    }

    private static async Task LoginQueueRefusalAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = NewLoginRoot();
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        await using var platform = await LoginPlatform.StartAsync(root, certificate,
            ((IPEndPoint)rtmpBackend.LocalEndpoint).Port, ((IPEndPoint)xmppBackend.LocalEndpoint).Port, stop.Token);
        var connections = Path.Combine(root, "connections");
        await using var proxy = await AirRemoteProxy.StartWithPortsAsync(PinnedInvitation(certificate, platform.RemotePort),
            FreePort(), FreePort(), FreePort(), stop.Token, connections);
        var console = new CountingReader();
        var previousIn = Console.In;
        (HttpStatusCode Status, string Body) reply;
        Console.SetIn(console);
        try { reply = await PostLoginAsync(proxy.HttpPort, "friend_login", "wrong-horse-22"); }
        finally { Console.SetIn(previousIn); }
        TestAssert.Equal(HttpStatusCode.Forbidden, reply.Status, "a wrong login gets the platform's refusal");
        TestAssert.True(reply.Body.Contains("FAILED", StringComparison.Ordinal), "the refusal reply is passed through unchanged");
        TestAssert.Equal(0, platform.SignIns, "a refused login sends no sign-in request");
        TestAssert.False(File.Exists(Path.Combine(connections, "remote-token.json")), "no bearer is cached for a refused login");
        TestAssert.Equal(0, console.Reads, "the companion never reads the console for a login");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.RtmpPort, stop.Token);
        TestAssert.Equal(0, await tcp.GetStream().ReadAsync(new byte[1], stop.Token), "without a bearer the RTMP tunnel is refused");
    }

    private static async Task CachedTokenSkipsSignInAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = NewLoginRoot();
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        await using var platform = await LoginPlatform.StartAsync(root, certificate,
            ((IPEndPoint)rtmpBackend.LocalEndpoint).Port, ((IPEndPoint)xmppBackend.LocalEndpoint).Port, stop.Token);
        var connections = Path.Combine(root, "connections");
        var invitation = PinnedInvitation(certificate, platform.RemotePort);
        // The first companion run: the login through the queue caches the bearer.
        await using (var first = await AirRemoteProxy.StartWithPortsAsync(invitation, FreePort(), FreePort(), FreePort(), stop.Token, connections))
            TestAssert.Equal(HttpStatusCode.OK, (await PostLoginAsync(first.HttpPort, "friend_login", "correct-horse-1")).Status, "the first login succeeds");
        TestAssert.Equal(1, platform.SignIns, "the first login signs in once");
        // A later run starts with the cached bearer, which the platform still accepts, so its login needs no sign-in.
        var cached = await RemoteSignIn.TryCachedTokenAsync(invitation, connections, stop.Token);
        TestAssert.True(cached is not null, "the platform still accepts the cached bearer");
        await using var second = await AirRemoteProxy.StartWithPortsAsync(cached!, FreePort(), FreePort(), FreePort(), stop.Token, connections);
        var reply = await PostLoginAsync(second.HttpPort, "friend_login", "correct-horse-1");
        TestAssert.Equal(HttpStatusCode.OK, reply.Status, "the login with a cached bearer succeeds");
        TestAssert.Equal(1, platform.SignIns, "a cached bearer skips the sign-in: no second /air/remote-token request");
        await TunnelCarriesBytesAsync(second.RtmpPort, rtmpBackend, stop.Token);
    }

    private static async Task PasswordNeverPersistedAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = NewLoginRoot();
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        await using var platform = await LoginPlatform.StartAsync(root, certificate,
            ((IPEndPoint)rtmpBackend.LocalEndpoint).Port, ((IPEndPoint)xmppBackend.LocalEndpoint).Port, stop.Token);
        var connections = Path.Combine(root, "connections");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            await using var proxy = await AirRemoteProxy.StartWithPortsAsync(PinnedInvitation(certificate, platform.RemotePort),
                FreePort(), FreePort(), FreePort(), stop.Token, connections);
            await PostLoginAsync(proxy.HttpPort, "friend_login", "wrong-horse-22");
            await PostLoginAsync(proxy.HttpPort, "friend_login", "correct-horse-1");
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
        var cache = Path.Combine(connections, "remote-token.json");
        TestAssert.True(File.Exists(cache), "the valid login leaves its bearer in the cache");
        var cached = await File.ReadAllTextAsync(cache);
        TestAssert.False(cached.Contains("correct-horse-1", StringComparison.Ordinal) || cached.Contains("wrong-horse-22", StringComparison.Ordinal),
            "the cache holds the bearer, never the login password");
        var text = captured.ToString();
        TestAssert.False(text.Contains("correct-horse-1", StringComparison.Ordinal) || text.Contains("wrong-horse-22", StringComparison.Ordinal),
            "the console never shows the login password");
    }

    private static async Task TunnelCarriesBytesAsync(int proxyPort, TcpListener backend, CancellationToken cancellationToken)
    {
        var payload = new byte[] { 0x4C, 0x00, 0xA5, 0xFF };
        var echoed = EchoBackendAsync(backend, payload.Length, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await RoundTripAsync(client, payload, cancellationToken);
        TestAssert.Equal(payload, await echoed.WaitAsync(TimeSpan.FromSeconds(5)), "a tunnel opened after the login carries its bytes");
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostLoginAsync(int proxyHttpPort, string username, string password)
    {
        using var client = new HttpClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["payload"] = $"user={username},password={password}" });
        using var response = await client.PostAsync($"http://127.0.0.1:{proxyHttpPort}{LoginPlatform.LoginPath}", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static AirRemoteInvitation PinnedInvitation(X509Certificate2 certificate, int remotePort) =>
        new("127.0.0.1", remotePort, "", certificate.GetCertHashString(HashAlgorithmName.SHA256));

    private static string NewLoginRoot() => Path.Combine(Path.GetTempPath(), "league-air-login-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The platform side of an AIR login: the real login-queue handler, the sign-in and the tunnels on a pinned HTTPS
    /// listener. It counts the sign-in requests the companion sends.
    /// </summary>
    private sealed class LoginPlatform : IAsyncDisposable
    {
        public const string LoginPath = "/login-queue/rest/queue/authenticate";
        private readonly PlatformAccounts accounts;
        private readonly WebApplication app;
        private int signIns;

        private LoginPlatform(PlatformAccounts accounts, WebApplication app, int remotePort)
        {
            this.accounts = accounts;
            this.app = app;
            RemotePort = remotePort;
        }

        public int RemotePort { get; }

        public int SignIns => Volatile.Read(ref signIns);

        public static async Task<LoginPlatform> StartAsync(string root, X509Certificate2 certificate, int rtmpPort, int chatPort,
            CancellationToken cancellationToken)
        {
            var accounts = new PlatformAccounts(root);
            accounts.RegisterPublic("friend_login", "correct-horse-1", "Friend");
            var sessions = new PlatformSessions(accounts);
            var access = new AirRemoteAccess(root);
            var remotePort = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
                listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            var platform = new LoginPlatform(accounts, app, remotePort);
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == AirRemoteTunnel.SignInPath)
                    Interlocked.Increment(ref platform.signIns);
                await next(context);
            });
            app.MapPost(LoginPath, (Delegate)((HttpContext context) => Amberline.Platform.Program.AuthenticateAsync(context, accounts, sessions)));
            AirRemoteTunnel.MapSignIn(app, accounts, access);
            AirRemoteTunnel.Map(app, access, rtmpPort, chatPort);
            await app.StartAsync(cancellationToken);
            return platform;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            accounts.Dispose();
        }
    }

    private sealed class CountingReader : TextReader
    {
        public int Reads { get; private set; }

        public override int Read()
        {
            Reads++;
            return -1;
        }

        public override int Peek()
        {
            Reads++;
            return -1;
        }

        public override string? ReadLine()
        {
            Reads++;
            return null;
        }
    }

    private static async Task ProxyDenialAsync()
    {
        await using var proxy = await AirRemoteProxy.StartWithPortsAsync(
            Invitation(), FreePort(), FreePort(), FreePort());
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await client.GetAsync($"http://127.0.0.1:{proxy.HttpPort}/manage/invitations");
        TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Playtest B3 #11, 26 Sept 2026: public registration is off, so the proxy no longer exposes
        // /register (this replaced the RegistrationUrl-with-invitation assertion) and the pinned
        // remote port no longer serves it.
        using var registration = await client.GetAsync($"http://127.0.0.1:{proxy.HttpPort}/register");
        TestAssert.Equal(HttpStatusCode.Forbidden, registration.StatusCode, "the guest proxy must not forward registration");
        TestAssert.False(AirRemoteProxy.IsAllowedPath("/register"));
        TestAssert.False(AirRemoteTunnel.IsPublicPath("/register"), "the pinned remote port must not serve registration");
        TestAssert.True(AirRemoteProxy.IsAllowedPath("/login-queue/rest/queue/authenticate"));
        TestAssert.False(AirRemoteProxy.IsAllowedPath("/manage/invitations"));
        using var rebound = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{proxy.HttpPort}/health");
        rebound.Headers.Host = "untrusted.example";
        using var reboundResponse = await client.SendAsync(rebound);
        TestAssert.Equal(HttpStatusCode.Forbidden, reboundResponse.StatusCode, "DNS rebinding cannot use the guest bearer");
    }

    private static async Task ProxyRoundTripAsync()
    {
        var payload = new byte[] { 0, 1, 2, 127, 128, 254, 255 };
        const string token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var certificate = CreateCertificate();
        var remotePort = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["Authorization"].ToString() != "Bearer " + token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapGet("/health", () => Results.Bytes(payload, "application/octet-stream"));
        await app.StartAsync();
        try
        {
            var invitation = new AirRemoteInvitation("127.0.0.1", remotePort, token,
                certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: FreePort());
            await using (var proxy = await AirRemoteProxy.StartWithPortsAsync(
                invitation, FreePort(), FreePort(), FreePort()))
            using (var client = new HttpClient())
            {
                var actual = await client.GetByteArrayAsync($"http://127.0.0.1:{proxy.HttpPort}/health");
                TestAssert.Equal(payload, actual, "remote HTTP response bytes must survive the proxy");
            }

            await using (var wrongPin = await AirRemoteProxy.StartWithPortsAsync(
                invitation with { CertificateSha25664hex = new string('0', 64) },
                FreePort(), FreePort(), FreePort()))
            using (var client = new HttpClient())
            using (var response = await client.GetAsync($"http://127.0.0.1:{wrongPin.HttpPort}/health"))
                TestAssert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

            await using (var wrongToken = await AirRemoteProxy.StartWithPortsAsync(
                invitation with { Token64hex = new string('b', 64) },
                FreePort(), FreePort(), FreePort()))
            using (var client = new HttpClient())
            using (var response = await client.GetAsync($"http://127.0.0.1:{wrongToken.HttpPort}/health"))
                TestAssert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    // The stock home background, Xerath splash and parchment are 250-800 KB and the store pages
    // exceed 64 KiB; a request-sized response cap answered 413 and guests lost all of them.
    private static async Task ProxyLargeResponseAsync()
    {
        const string token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var image = new byte[800 * 1024];
        new Random(7).NextBytes(image);
        var page = System.Text.Encoding.UTF8.GetBytes(new string('x', 200 * 1024));
        var tooLarge = new byte[5 * 1024 * 1024];
        using var certificate = CreateCertificate();
        var remotePort = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/img/home/background", () => Results.Bytes(image, "image/jpeg"));
        app.MapGet("/store/home", () => Results.Bytes(page, "text/html"));
        app.MapGet("/img/home/oversize", () => Results.Bytes(tooLarge, "image/jpeg"));
        await app.StartAsync();
        try
        {
            var invitation = new AirRemoteInvitation("127.0.0.1", remotePort, token,
                certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: FreePort());
            await using var proxy = await AirRemoteProxy.StartWithPortsAsync(
                invitation, FreePort(), FreePort(), FreePort());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            TestAssert.Equal(image, await client.GetByteArrayAsync($"http://127.0.0.1:{proxy.HttpPort}/img/home/background"),
                "an 800 KB home image must reach the guest intact");
            TestAssert.Equal(page, await client.GetByteArrayAsync($"http://127.0.0.1:{proxy.HttpPort}/store/home"),
                "a store page above the 64 KiB request cap must reach the guest intact");
            using var response = await client.GetAsync($"http://127.0.0.1:{proxy.HttpPort}/img/home/oversize");
            TestAssert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, "responses stay bounded");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task WssRoundTripAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        var rtmpPort = ((IPEndPoint)rtmpBackend.LocalEndpoint).Port;
        var xmppPort = ((IPEndPoint)xmppBackend.LocalEndpoint).Port;
        var remotePort = FreePort();
        var access = NewAccess();
        var token = access.Issue(1).Token;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        AirRemoteTunnel.Map(app, access, rtmpPort, xmppPort);
        await app.StartAsync(stop.Token);

        var invitation = new AirRemoteInvitation("127.0.0.1", remotePort, token,
            certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: FreePort());
        var rtmpPayload = new byte[] { 0, 1, 2, 127, 128, 254, 255, 0xA5 };
        var xmppPayload = new byte[] { 0x7F, 0x80, 0xFE, 0xFF, 0x10, 0x00 };
        var rtmpEcho = EchoBackendAsync(rtmpBackend, rtmpPayload.Length, stop.Token);
        var xmppEcho = EchoBackendAsync(xmppBackend, xmppPayload.Length, stop.Token);
        try
        {
            await using var proxy = await AirRemoteProxy.StartWithPortsAsync(
                invitation, FreePort(), FreePort(), FreePort(), stop.Token);
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.RtmpPort, stop.Token);
                await RoundTripAsync(client, rtmpPayload, stop.Token);
            }
            TestAssert.Equal(rtmpPayload, await rtmpEcho.WaitAsync(TimeSpan.FromSeconds(5)));

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.ChatPort, stop.Token);
                await RoundTripAsync(client, xmppPayload, stop.Token);
            }
            TestAssert.Equal(xmppPayload, await xmppEcho.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            rtmpBackend.Stop();
            xmppBackend.Stop();
            try { await rtmpEcho.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            try { await xmppEcho.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            await app.StopAsync();
        }
    }

    private static async Task TunnelEndLineAsync()
    {
        using var certificate = CreateCertificate();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var rtmpBackend = new TcpListener(IPAddress.Loopback, 0);
        using var xmppBackend = new TcpListener(IPAddress.Loopback, 0);
        rtmpBackend.Start();
        xmppBackend.Start();
        var access = NewAccess();
        var token = access.Issue(7).Token;
        var remotePort = FreePort();
        var text = new StringWriter();
        var log = TextWriter.Synchronized(text);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        AirRemoteTunnel.Map(app, access, ((IPEndPoint)rtmpBackend.LocalEndpoint).Port,
            ((IPEndPoint)xmppBackend.LocalEndpoint).Port, log: log);
        await app.StartAsync(stop.Token);

        async Task<ClientWebSocket> OpenAsync(string path)
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{remotePort}{path}"), stop.Token);
            return socket;
        }
        async Task<string> EndLineAsync(string path)
        {
            while (true)
            {
                string all;
                lock (log) all = text.ToString();
                var line = all.Split('\n').FirstOrDefault(l => l.Contains($"air.tunnel-end path={path} ", StringComparison.Ordinal));
                if (line is not null) return line.Trim();
                await Task.Delay(20, stop.Token);
            }
        }
        try
        {
            // The host's listener answers and hangs up first: the line names the host and counts the bytes each way.
            var backend = Task.Run(async () =>
            {
                using var client = await rtmpBackend.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                await ReadExactlyAsync(stream, 3, stop.Token);
                await stream.WriteAsync(new byte[] { 9, 8 }, stop.Token);
            });
            using (var socket = await OpenAsync(AirRemoteTunnel.Path))
            {
                await socket.SendAsync(new byte[] { 1, 2, 3 }.AsMemory(), WebSocketMessageType.Binary, true, stop.Token);
                var reply = new byte[16];
                TestAssert.Equal(2, (await socket.ReceiveAsync(reply.AsMemory(), stop.Token)).Count);
                await backend;
                var line = await EndLineAsync(AirRemoteTunnel.Path);
                TestAssert.True(line.Contains("account=7 first=host reason=eof ", StringComparison.Ordinal), line);
                TestAssert.True(line.Contains("toRemoteBytes=2 fromRemoteBytes=3", StringComparison.Ordinal), line);
            }

            // The companion aborts its WebSocket when its AIR side ends (AirRemoteProxy.PumpAsync): the line names the remote.
            var held = xmppBackend.AcceptTcpClientAsync(stop.Token);
            using (var socket = await OpenAsync(AirRemoteTunnel.ChatPath))
            {
                using var chat = await held;
                socket.Abort();
                var line = await EndLineAsync(AirRemoteTunnel.ChatPath);
                TestAssert.True(line.Contains(" first=remote reason=", StringComparison.Ordinal), line);
                TestAssert.True(line.Contains("toRemoteBytes=0 fromRemoteBytes=0", StringComparison.Ordinal), line);
            }
        }
        finally
        {
            rtmpBackend.Stop();
            xmppBackend.Stop();
            await app.StopAsync();
        }
    }

    private static async Task RoundTripAsync(TcpClient client, byte[] payload, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(payload, cancellationToken);
        var actual = await ReadExactlyAsync(stream, payload.Length, cancellationToken);
        TestAssert.Equal(payload, actual, "remote WSS tunnel must preserve TCP bytes");
    }

    private static async Task<byte[]> EchoBackendAsync(TcpListener listener, int length,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var received = await ReadExactlyAsync(stream, length, cancellationToken);
        await stream.WriteAsync(received, cancellationToken);
        return received;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0) throw new EndOfStreamException("The synthetic tunnel closed before the payload.");
            offset += count;
        }
        return buffer;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=air-remote-check", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        // Kestrel's Windows TLS path needs a reloadable private-key handle;
        // keep the fixture independent of the ephemeral key returned by CreateSelfSigned.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    private static AirRemoteInvitation Invitation() => new(
        "127.0.0.1", Token64hex: new string('a', 64),
        CertificateSha25664hex: new string('b', 64), RegistrationCode: "REGISTRATION");

    private static AirRemoteAccess NewAccess() =>
        new(Path.Combine(Path.GetTempPath(), "league-air-remote-" + Guid.NewGuid().ToString("N")));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
