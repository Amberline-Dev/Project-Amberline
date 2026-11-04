using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace Amberline.Platform;

public sealed record PlatformSettings(string DataRoot, string ServerExecutable, string GameHost = "127.0.0.1",
    int HttpPort = 8390, int RtmpPort = 2099, int FirstGamePort = 5219, int MaxGames = 4, int RemotePort = 8391,
    string? PublicHost = null, string? AirRoot = null, string? PublicRegistrationUrl = null,
    string BindAddress = "0.0.0.0", int XmppPort = 5222)
{
    // Empty or blank means public registration is off (playtest B3 #11): accounts are created on local /register.
    public string? PublicRegistrationUrl { get; init; } = string.IsNullOrWhiteSpace(PublicRegistrationUrl) ? null : PublicRegistrationUrl;

    /// <summary>The home host's first-run answers (<see cref="HostSteps"/>): the server's name, where the address came from
    /// (<see cref="HostAddress"/>) and the id of the host's own login. The operator's start leaves them empty.</summary>
    public string? ServerName { get; init; }
    public string? AddressSource { get; init; }
    public long? HostAccount { get; init; }
    /// <summary>Settings' "Let people register themselves" (<see cref="HostSelfRegistration"/>, batch 656); off unless the host turned it on.</summary>
    public bool SelfRegistration { get; init; }
    /// <summary>Settings' "Share the game files with players" (<see cref="HostFileShare"/>, batch 681); off unless the host turned it on.</summary>
    public bool ShareFiles { get; init; }
    /// <summary>
    /// Retired (batch 680): batch 666's "Allow cheat commands in my games". Read only while <see cref="CheatAccounts"/> is missing, when
    /// it ticks the host's own login (<see cref="CheatLogins"/>); the list's first Save drops it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowCheats { get; init; }
    /// <summary>
    /// Settings' Cheat commands list (batch 680): the account ids that may use the cheat commands, null until its first Save. An id
    /// outlives a login's new password and its Remove access, which keep the account.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long[]? CheatAccounts { get; init; }

    /// <summary>The logins the Cheat commands list ticks; before its first Save, the host's own while batch 666's switch was on.</summary>
    public IReadOnlyList<long> CheatLogins() => CheatAccounts ?? (AllowCheats && HostAccount is { } own ? [own] : []);
    /// <summary>The data format that wrote this file (<see cref="HostChecks.DataVersion"/>; a file without it is format 1).</summary>
    public int DataVersion { get; init; } = HostChecks.DataVersion;

    public void ValidatePublicRegistrationUrl()
    {
        if (PublicRegistrationUrl is null) return;
        if (!IsPublicHost(PublicHost) ||
            !Uri.TryCreate(PublicRegistrationUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, PublicHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || uri.AbsolutePath != "/register" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("PublicRegistrationUrl must be an HTTPS /register URL on PublicHost without a query, fragment or credentials.");
    }

    private static bool IsPublicHost(string? host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Before anything reads PlatformContent: --home chooses the data folder.
        if (args is ["--home", .. var options]) return await HostMode.MainAsync(options);
        try
        {
            var home = PlatformContent.DataHome;
            Directory.CreateDirectory(home);
            var settingsFile = Path.Combine(home, "platform.json");
            if (args is ["--init", var dataRoot, var server])
            {
                if (File.Exists(settingsFile)) throw new ArgumentException("Platform settings already exist.");
                var initial = new PlatformSettings(Path.GetFullPath(dataRoot), Path.GetFullPath(server));
                new HostSettings(initial.DataRoot, initial.ServerExecutable, initial.HttpPort, initial.FirstGamePort, initial.MaxGames, BindAddress: initial.BindAddress).Validate();
                HostFiles.Write(settingsFile, initial);
                Console.WriteLine($"Platform prepared. Start it, then register at http://127.0.0.1:{initial.HttpPort}/register.");
                return 0;
            }
            if (!File.Exists(settingsFile)) throw new ArgumentException("Initialize with --init <game-data-root> <prepared-server-executable>.");
            var (settings, hostSettings) = LoadSettings(settingsFile);
            if (args.Length != 0) throw new ArgumentException("Options: --init <game-data-root> <server>, or --home.");
            await RunAsync(settings, hostSettings, home);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// Reads and checks <c>platform.json</c>. With <paramref name="home"/> (the <c>--home</c> start) both folders must be chosen and
    /// an empty <c>serverExecutable</c> is the server beside the Platform (<see cref="HostPaths.Derive"/>).
    /// </summary>
    public static (PlatformSettings Settings, HostSettings Host) LoadSettings(string settingsFile, HostPaths? home = null)
    {
        var settings = JsonSerializer.Deserialize<PlatformSettings>(File.ReadAllText(settingsFile), HostFiles.Json) ?? throw new InvalidDataException("Empty platform settings.");
        if (home is not null) settings = home.Derive(settings);
        settings.ValidatePublicRegistrationUrl();
        // NOT RETAIL: retail fixed RTMP 2099 and XMPP 5222; the installed preview keeps them (the defaults). Another port is for a
        // second, throwaway Platform beside the installed one (tools/air-page-shots.ps1), whose AIR copy names the same ports in
        // its own lol.properties (remoting_port, xmpp_muc_server_port).
        if (settings.RtmpPort is < 1 or > 65535 || settings.XmppPort is < 1 or > 65535 || settings.RtmpPort == settings.XmppPort ||
            settings.HttpPort == settings.RtmpPort || settings.HttpPort == settings.XmppPort ||
            settings.RemotePort is < 1 or > 65535 || settings.RemotePort == settings.XmppPort ||
            settings.RemotePort == settings.HttpPort || settings.RemotePort == settings.RtmpPort ||
            !IPAddress.TryParse(settings.GameHost, out var gameAddress) || gameAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("This local companion requires distinct HTTP, RTMP, XMPP and remote ports and an advertised game IPv4 address.");
        var hostSettings = new HostSettings(settings.DataRoot, settings.ServerExecutable, settings.HttpPort, settings.FirstGamePort, settings.MaxGames, BindAddress: settings.BindAddress);
        hostSettings.Validate();
        return (settings, hostSettings);
    }

    /// <summary>
    /// The full Platform until it stops. With <paramref name="host"/> (<c>--home</c>) the friends' archive comes from the
    /// <c>bootstrap</c> folder beside the program, <c>/open</c> leads to the host page, and the result says whether the loop
    /// builds the next start (Restart, a settings change) instead of exiting.
    /// </summary>
    public static async Task<bool> RunAsync(PlatformSettings settings, HostSettings hostSettings, string home, HostLoop? host = null)
    {
        var publicRegistrationUrl = settings.PublicRegistrationUrl is { } value ? new Uri(value, UriKind.Absolute) : null;
        Environment.SetEnvironmentVariable("GAME_DATA_ROOT", settings.DataRoot);
        var airRoot = Path.GetFullPath(settings.AirRoot ?? Path.Combine(AppContext.BaseDirectory, "..", "air"));
        // ChampionCatalog reads its rows from this AIR client (AirChampionData.ResolveSwf).
        Environment.SetEnvironmentVariable("AIR_CLIENT_ROOT", airRoot);
        using var accounts = new PlatformAccounts(home, airRoot);
        await using var manager = new GameManager(hostSettings, Path.Combine(home, "games"));
        accounts.RecoverResults(manager, Console.Out);
        var sessions = new PlatformSessions(accounts);
        var games = new PlatformGames(accounts, manager, settings.GameHost);
        // DIAGNOSTICS (owner, 3 and 4 November 2026): in home mode only the logins Settings' Cheat commands list ticks get cheats.
        if (host is not null) games.MayCheat = HostSettingsPage.CheatRule(host.Paths.Settings);
        var services = new PlatformServices(accounts, sessions, games, settings.HttpPort);
        await using var chat = new AirXmppHost(accounts, sessions, Path.Combine(home, "social"), settings.XmppPort);
        chat.ActiveChampion = games.ActivePresenceChampion;
        chat.RoomAccess = games.MayEnterRoom;
        games.PresenceChanged = account => { _ = chat.RefreshGamePresenceAsync(account); };
        var remoteDirectory = Path.Combine(home, "remote");
        using var tls = HostFiles.Certificate(remoteDirectory);
        // NOT RETAIL: the tunnel names the friend behind each loopback connection for the host page's Online table.
        var tunnelPeers = new AirTunnelPeers();
        // NOT RETAIL: the friend's tunnel bearer tokens, persisted beside the host certificate (see AirRemoteAccess).
        var remoteAccess = new AirRemoteAccess(remoteDirectory);
        var pin = tls.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        // NOT RETAIL: people register themselves on the friends' port while Settings' switch is on (HostSelfRegistration); home mode only.
        using var signUp = host is null ? null : new HostSelfRegistration(host, accounts, settings.RemotePort, pin, settings.SelfRegistration);
        // NOT RETAIL: the host's own game and AIR client for players who have none, while Settings' switch is on (HostFileShare); home mode only.
        var share = host is null ? null : new HostFileShare(host, settings.DataRoot, airRoot, settings.RemotePort, settings.ShareFiles);
        await using var rtmp = new AirRtmpHost(new IPEndPoint(IPAddress.Loopback, settings.RtmpPort),
            services.Route, sessions.AuthenticateChannel, sessions.AccountForConnection, services.Disconnect,
            Console.Out, services.ResponseSent, sessions.Resume, services.ConnectionClosed,
            (connection, peer) =>
            {
                sessions.ObservePeer(connection, tunnelPeers.Resolve(settings.RtmpPort, peer));
                // NOT RETAIL: which sessions came through the friends' tunnel (the dashboard's friend seats, PlatformSessions.ThroughTunnel).
                sessions.ObserveOrigin(connection, !tunnelPeers.IsDirectLocal(settings.RtmpPort, peer));
            });
        games.Push = (account, body) => rtmp.Push(account, body);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // Store entry grants are credentials in URLs. ASP.NET Core's own lines only from Warning up (batch 643: a line per request and
        // result filled the log, and so the support log's last lines); the antiforgery notice about the pages' no-store header is no news.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Antiforgery", LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 16 * 1024;
            o.Limits.MaxConcurrentConnections = 64;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            o.Listen(IPAddress.Loopback, settings.HttpPort);
            o.Listen(IPAddress.Parse(settings.BindAddress), settings.RemotePort, listen => listen.UseHttps(tls));
        });
        builder.Services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "Amberline.Forms";
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        if (host is not null) builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(host.Paths.Keys));
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            // LoginView treats any status but 403 as "login queue unavailable" and falls back to a queue-less
            // login, so the login queue's rejection is the client's own retry form instead of a bare 429.
            // Batch 656: the friends' port's sign-up says "Too many sign-ups" in a page of its own.
            o.OnRejected = (rejected, _) => signUp?.Serves(rejected.HttpContext) == true
                ? new ValueTask(HostSelfRegistration.TooMany.ExecuteAsync(rejected.HttpContext))
                : rejected.HttpContext.Request.Path != LoginQueuePath ? ValueTask.CompletedTask
                : new ValueTask(AttemptRateTooFast(rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? wait : TimeSpan.FromMinutes(1)).ExecuteAsync(rejected.HttpContext));
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(PartitionedRateLimiter.Create<HttpContext, string>(RequestPartition),
                PartitionedRateLimiter.Create<HttpContext, string>(FilesPartition));
            o.AddPolicy("registration", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 12, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        });
        await using var app = builder.Build();
        await using var dashboard = host is null ? null : new HostDashboard(host, settings,
            () => PlatformManagement.Online(accounts, sessions, games, chat), () => HostDashboard.RunningGames(manager), maxGames: () => manager.MaxGames)
            { Support = HostSupport.For(host, settings, manager, accounts, sessions) };
        UseRegistrationProxy(app, settings);
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
            if (context.Request.Path == "/landing" || context.Request.Path == "/story" || context.Request.Path.StartsWithSegments("/store"))
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self'; form-action 'self'; frame-ancestors 'self' file: app:";
            if (context.Connection.LocalPort == settings.RemotePort)
            {
                if (!context.Request.IsHttps || !(AirRemoteTunnel.IsPublicPath(context.Request.Path.Value ?? "/") || signUp?.Serves(context) == true ||
                    share?.Serves(context) == true))
                { context.Response.StatusCode = 404; return; }
                host?.Friend.Observe(context.Connection.RemoteIpAddress, context.Request.Path.Value, DateTimeOffset.UtcNow);
            }
            try { await next(context); }
            catch (Exception ex) when (context.Request.Path.StartsWithSegments("/store") && ex is ArgumentException or InvalidOperationException)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(PlatformStore.ErrorPage(ex.Message));
            }
            catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Refresh the local management page and try again." }); }
        });
        AirRemoteTunnel.Map(app, remoteAccess, settings.RtmpPort, settings.XmppPort, tunnelPeers);
        AirRemoteTunnel.MapSignIn(app, accounts, remoteAccess);
        app.MapGet("/health", () => host is null
            ? Results.Json(new { status = "ready", service = "Amberline.AirPlatform", protocol = 1, client = "1.45.11_10_04_15_28" })
            : Results.Json(new { status = "ready", service = "Amberline.AirPlatform", protocol = 1, client = "1.45.11_10_04_15_28", mode = "home" }));
        // NOT RETAIL: the home host's pages answer only the host's signed-in browser (HostGate); /open signs it in.
        if (host is not null) HostGate.Use(app, settings.HttpPort, host.Sessions, HostSteps.Start, signUp!.Serves);
        // NOT RETAIL: the home host's first-run steps; the name and the address they save reach /bootstrap/client.json at once.
        var steps = host is null ? null : HostSteps.Map(app, host, accounts);
        // NOT RETAIL: the host pages' shell and stylesheet (HostPages), after the gate so its refusal pages stay bare.
        if (host is not null) HostPages.Use(app, host, () => steps!.ServerName, nav: true);
        if (host is not null) HostPlay.Map(app, host, settings);
        if (host is not null) HostUpdates.Map(app, host, () => HostDashboard.RunningGames(manager).Count);
        // NOT RETAIL: Invite a friend and Friends: friends' logins and the Play file, under the gated /host pages.
        if (host is not null) HostInvite.Map(app, host, () => steps!.PublicHost, () => steps!.ServerName, accounts, sessions, remoteAccess,
            settings.RemotePort, pin, signUp);
        signUp?.Map(app);
        share?.Map(app);
        dashboard?.Map(app, steps, tls);
        // NOT RETAIL: Settings (what the first run asked) and Uninstall Amberline, under the gated /host pages.
        // Batch 684: a saved Games at the same time counts for the next game and tests the network again (the router's ports at once).
        if (host is not null) HostSettingsPage.Map(app, host, steps!, () => HostDashboard.RunningGames(manager).Count, signUp!, accounts, share,
            maxGames => { manager.MaxGames = maxGames; dashboard?.TestAgain(); });
        // NOT RETAIL: the host's notice (Advanced's Send a notice to players) leads the Home's News while it stands; retail's Home had none.
        PlatformHomeNotice? hostNotice = null;
        PlatformManagement.Map(app, accounts, settings.HttpPort, settings.RemotePort,
            (account, body) => rtmp.Push(account, body, "cn"), publicRegistrationUrl?.AbsoluteUri,
            () => PlatformManagement.Online(accounts, sessions, games, chat), remoteAccess,
            (text, severity) =>
            {
                var notice = AirDtos.BroadcastNotification(text, severity);
                services.Broadcast = text is null ? null : notice;
                hostNotice = text is null ? null : new PlatformHomeNotice("Notice from the host", text, DateTimeOffset.UtcNow);
                return rtmp.Broadcast(notice);
            }, sessions, host is null ? null : HostInvite.InvitePath);
        PlatformStore.Map(app, accounts, sessions, airRoot, (account, body) => rtmp.Push(account, body, "cn"));
        PlatformBootstrap.Map(app, host?.Paths.Bootstrap ?? Path.Combine(home, "bootstrap"), Path.Combine(AppContext.BaseDirectory, "air-client-bootstrap.cmd"),
            steps is null ? () => settings.PublicHost : () => steps.PublicHost, settings.RemotePort, tls, () => steps?.ServerName);
        MapRegistration(app, accounts, signUp);
        app.MapPost(LoginQueuePath, (Delegate)((HttpContext context) => AuthenticateAsync(context, accounts, sessions, remoteAccess)));
        app.MapGet("/ip", () => Results.Json(new { ip_address = "127.0.0.1" }));
        app.MapGet("/status/{**path}", () => Results.Json(PlatformHome.StatusPayload()));
        app.MapGet("/rss/{feed}.xml", (string feed) => Results.Content(PlatformHome.RenderRss(feed), "application/rss+xml"));
        PlatformHome.MapImages(app, airRoot);
        app.MapGet("/story", () => Results.Content(PlatformHome.RenderStory(), "text/html"));
        PlatformHome.MapLadder(app, accounts, settings.RemotePort, remoteAccess);
        app.MapGet("/landing", () => Results.Content(PlatformHome.RenderLanding(PlatformHomeModel.Testing(((System.Collections.ICollection)games.List()).Count) with
        {
            Stats = PlatformHome.Snapshot(accounts, PlatformManagement.Online(accounts, sessions, games, chat).Length,
                steps?.ServerName ?? settings.ServerName, hostNotice, DateTimeOffset.UtcNow),
        }, accounts.StoreCatalog), "text/html"));
        using var stop = new CancellationTokenSource();
        app.Lifetime.ApplicationStopping.Register(stop.Cancel);
        await rtmp.StartAsync(stop.Token);
        await chat.StartAsync(stop.Token);
        var ticking = TickAsync(games, stop.Token);
        Console.WriteLine($"AIR platform: loopback HTTP {settings.HttpPort}, RTMP {settings.RtmpPort}, XMPP {settings.XmppPort}; public HTTPS {settings.RemotePort}; game UDP {settings.FirstGamePort}–{settings.FirstGamePort + settings.MaxGames - 1}.");
        Console.WriteLine($"Host management: http://127.0.0.1:{settings.HttpPort}/manage");
        try
        {
            if (host is null) await app.RunAsync();
            else return await host.RunAsync(app, settings.HttpPort);
        }
        finally { stop.Cancel(); await ticking; await rtmp.StopAsync(); }
        return false;
    }

    public const string LoginQueuePath = "/login-queue/rest/queue/authenticate";

    /// <summary>
    /// The global per-address request limit: 180 requests a minute, except the public-port caps below.
    /// NOT RETAIL: security additions for the public port (retail served this art and no companion at all). The images
    /// (<c>/img</c>, <c>/store/img</c>, <c>/store/art</c>) get a bucket of 1000 refilled at 20 a second, enough for a whole
    /// store page of cards and rune icons at once, and <c>/bootstrap/companion.zip</c> six downloads an hour. The host's
    /// loopback is never held to either cap: its own AIR loads the same pages.
    /// </summary>
    public static RateLimitPartition<string> RequestPartition(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        var key = address?.ToString() ?? "local";
        var local = address is null || IPAddress.IsLoopback(address);
        var path = context.Request.Path;
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.StartsWithSegments("/img") || path.StartsWithSegments("/store/img") || path.StartsWithSegments("/store/art")))
            return local ? RateLimitPartition.GetNoLimiter("store-images")
                : RateLimitPartition.GetTokenBucketLimiter("images:" + key, _ => new TokenBucketRateLimiterOptions
                { TokenLimit = 1000, TokensPerPeriod = 20, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0 });
        if (!local && path.StartsWithSegments("/bootstrap/companion.zip"))
            return RateLimitPartition.GetFixedWindowLimiter("companion:" + key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 6, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
        // NOT RETAIL (batch 681): a download of the shared game is thousands of requests, so it is held to transfers at a time instead of
        // requests a minute; loopback too, so no address is exempt.
        if (path.StartsWithSegments(HostFileShare.Prefix))
            return RateLimitPartition.GetConcurrencyLimiter("files:" + key, _ => new ConcurrencyLimiterOptions
            { PermitLimit = HostFileShare.PerAddress, QueueLimit = 0 });
        return RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    /// <summary>NOT RETAIL (batch 681): the shared game's transfers of every address together (<see cref="HostFileShare.InAll"/>).</summary>
    public static RateLimitPartition<string> FilesPartition(HttpContext context) => context.Request.Path.StartsWithSegments(HostFileShare.Prefix)
        ? RateLimitPartition.GetConcurrencyLimiter("files", _ => new ConcurrencyLimiterOptions { PermitLimit = HostFileShare.InAll, QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("all");

    public static async Task<IResult> AuthenticateAsync(HttpContext context, PlatformAccounts accounts, PlatformSessions sessions,
        AirRemoteAccess? remoteAccess = null)
    {
        // Reconstruction (limits): retail's queue refused too many failed logins (the client's attempt_rate_too_fast text);
        // its counts are not recovered. On the pinned remote listener a failed login counts toward the address's tunnel
        // sign-in throttle, so the public queue cannot guess passwords past it. The host's own loopback AIR is not throttled.
        var remote = context.Request.IsHttps ? remoteAccess : null;
        var address = context.Connection.RemoteIpAddress;
        if (remote?.RetryAfter(address) is { } wait && wait > TimeSpan.Zero) return AttemptRateTooFast(wait);
        // A body that is not a form is a failed login like any other, never a 500 (which the client reads as "queue unavailable").
        var payload = context.Request.HasFormContentType
            ? (await context.Request.ReadFormAsync(context.RequestAborted))["payload"].ToString() : "";
        var split = payload.IndexOf(",password=", StringComparison.Ordinal);
        var account = payload.StartsWith("user=", StringComparison.Ordinal) && split >= 5
            ? accounts.Authenticate(payload[5..split], payload[(split + 10)..]) : null;
        if (account is null) remote?.Failed(address);
        // LoginView handles credential failures through HTTP 403. A successful
        // HTTP response with FAILED never opens the alert that re-enables input.
        return account is null
            ? Results.Json(new { status = "FAILED", reason = "invalid_credentials" }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Json(new { status = "LOGIN", token = sessions.IssueTicket(account), user = account.Username, summoner = account.Summoner });
    }

    /// <summary>
    /// The stock client's retry form: LoginMessageResource.getAuthFailedMessage maps reason attempt_rate_too_fast to "You have
    /// incorrectly input your login credentials on too many consecutive attempts. Please try again in {retryWait} seconds",
    /// and LoginView.onAuthFailed (403 branch) shows it and counts the login button down for retryWait seconds.
    /// </summary>
    public static IResult AttemptRateTooFast(TimeSpan wait) => Results.Json(
        new { status = "FAILED", reason = "attempt_rate_too_fast", retryWait = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) },
        statusCode: StatusCodes.Status403Forbidden);

    public static bool IsRegistrationProxyRequest(HttpContext context, int localHttpPort, Uri publicRegistrationUrl) =>
        context.Connection.LocalPort == localHttpPort && context.Connection.RemoteIpAddress is { } peer &&
        IPAddress.IsLoopback(peer) && context.Request.Path == "/register" &&
        string.Equals(context.Request.Host.Host, publicRegistrationUrl.Host, StringComparison.OrdinalIgnoreCase);

    /// <param name="signUp">Home mode: on the friends' port <c>/register</c> is the people's own sign-up instead (<see cref="HostSelfRegistration"/>,
    /// batch 656), with the same <c>registration</c> limit; on every other port it is this page, as before.</param>
    public static void MapRegistration(WebApplication app, PlatformAccounts accounts, HostSelfRegistration? signUp = null)
    {
        app.MapGet("/register", (HttpContext context, IAntiforgery antiforgery) =>
            signUp is not null && context.Connection.LocalPort == signUp.Port ? signUp.Form(context, antiforgery)
                : Results.Content(RenderRegistration(RegistrationToken(context, antiforgery)), "text/html"));
        app.MapPost("/register", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (signUp is not null && context.Connection.LocalPort == signUp.Port) return await signUp.CreateAsync(context, antiforgery);
            string username = "", summoner = "", mode = "";
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                username = form["username"].ToString();
                summoner = form["summoner"].ToString();
                mode = form["mode"].ToString();
                if (mode is not ("" or "maxed" or "fresh")) throw new ArgumentException("Choose a fresh or fully unlocked account.");
                accounts.RegisterPublic(username, form["password"].ToString(), summoner, maxed: mode != "fresh");
                return Results.Content(RenderRegistration(success: true), "text/html");
            }
            catch (Exception ex) when (ex is ArgumentException or AntiforgeryValidationException)
            {
                var message = ex is AntiforgeryValidationException
                    ? "The form expired. Please try again." : ex.Message;
                return Results.Content(RenderRegistration(RegistrationToken(context, antiforgery),
                    message, username, summoner, mode), "text/html", statusCode: 400);
            }
        }).RequireRateLimiting("registration");
    }

    internal static string RegistrationToken(HttpContext context, IAntiforgery antiforgery)
    {
        var token = antiforgery.GetAndStoreTokens(context);
        return $"<input type=hidden name=\"{WebUtility.HtmlEncode(token.FormFieldName)}\" value=\"{WebUtility.HtmlEncode(token.RequestToken)}\">";
    }

    public static void UseRegistrationProxy(WebApplication app, PlatformSettings settings)
    {
        if (settings.PublicRegistrationUrl is not { } value) return;
        var registrationUrl = new Uri(value, UriKind.Absolute);
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        forwarded.KnownProxies.Add(IPAddress.Loopback);
        forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
        app.UseWhen(context => IsRegistrationProxyRequest(context, settings.HttpPort, registrationUrl),
            branch => branch.UseForwardedHeaders(forwarded));
    }

    private static Task TickAsync(PlatformGames games, CancellationToken stop) =>
        PlatformGames.RunTimerAsync(games.Tick, TimeSpan.FromMilliseconds(250), stop);

    private static string RenderRegistration(string token = "", string? error = null,
        string username = "", string summoner = "", string mode = "", bool success = false)
    {
        var content = success ? """
            <div class=notice role=status><h1>Account created</h1><p>Return to the game client and log in with your new account.</p></div>
            """ : $$"""
            <h1>Create your account</h1>
            <p class=intro>Choose how you want to start.</p>
            {{(error is null ? "" : $"<p class=error role=alert>{WebUtility.HtmlEncode(error)}</p>")}}
            <form method=post action=/register>{{token}}
            <label for=username>Username</label><input id=username name=username required minlength=3 maxlength=24 pattern="[A-Za-z0-9_]+" autocomplete=username value="{{WebUtility.HtmlEncode(username)}}">
            <label for=summoner>Summoner name</label><input id=summoner name=summoner required maxlength=27 value="{{WebUtility.HtmlEncode(summoner)}}">
            <label for=password>Password</label><input id=password name=password type=password required minlength=10 maxlength=128 autocomplete=new-password>
            <label for=mode>Starting account</label><select id=mode name=mode><option value=maxed>Fully unlocked · level 30</option><option value=fresh {{(mode == "fresh" ? "selected" : "")}}>Fresh · level 1, earn IP and unlock content</option></select>
            <button type=submit>Create account</button></form>
            """;
        return $$"""
            <!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width">
            <title>Amberline · {{(success ? "Account created" : "Create account")}}</title>
            <style>
            *{box-sizing:border-box}html{min-height:100%;background:#111c2a}body{margin:0;min-height:100vh;padding:5vh 18px;background:radial-gradient(ellipse at top,#29405a 0,#152335 55%,#0b131e 100%);color:#d5dce2;font:14px Arial,sans-serif}
            main{max-width:500px;margin:auto;border:1px solid #65748a;background:#101b29;box-shadow:0 0 0 4px #0b1420,0 15px 40px #060b12}
            header{padding:22px 28px 19px;border-bottom:1px solid #5b5039;background:linear-gradient(#243a52,#101b29)}.brand{margin:0;color:#ecd49d;font:bold 25px Georgia,serif;letter-spacing:.05em;text-shadow:0 2px #05090f}.subtitle{margin:5px 0 0;color:#aab9c5;font-size:11px;letter-spacing:.14em;text-transform:uppercase}
            section{padding:24px 28px 30px}h1{margin:0 0 12px;color:#e8c684;font:normal 23px Georgia,serif}.intro,.notice p{margin:0 0 22px;line-height:1.5}.notice p{margin-bottom:0}
            label{display:block;margin:15px 0 6px;color:#d7c7a6;font-weight:bold}input,select{display:block;width:100%;min-height:39px;padding:9px 10px;border:1px solid #60748a;border-radius:0;background:#08111c;color:#f3f1e9;font:14px Arial,sans-serif}input:focus,select:focus,button:focus{outline:2px solid #edc66c;outline-offset:2px}
            button{display:block;width:100%;margin-top:25px;padding:11px;border:1px solid #e3b960;border-radius:0;background:#856020;color:#fff8e9;font:bold 14px Arial,sans-serif;text-transform:uppercase;cursor:pointer}button:hover{background:#a77a2e}.error{padding:11px;border:1px solid #b66755;background:#3b2020;color:#fff0e6;line-height:1.4}
            @media(max-width:550px){body{padding:18px 12px}header{padding:19px 20px}section{padding:21px 20px 25px}.brand{font-size:22px} }
            </style></head><body><main><header><p class=brand>Amberline</p><p class=subtitle>PvP.net · Create account</p></header><section>{{content}}</section></main></body></html>
            """;
    }
}
