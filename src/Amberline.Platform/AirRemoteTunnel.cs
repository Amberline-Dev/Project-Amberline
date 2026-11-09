using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: operator tooling for the host page's Online table. The tunnel reaches the RTMP and XMPP loopback listeners
/// from a loopback source port, so those listeners see 127.0.0.1 for a friend. The tunnel knows the friend's real address
/// (the TLS peer of its remote listener, a direct TCP peer: <c>UseRegistrationProxy</c> never reads forwarded headers there),
/// so it records which source port carries which friend while the connection lasts. Memory only, never logged.
/// </summary>
public sealed class AirTunnelPeers
{
    private readonly ConcurrentDictionary<(int Port, int Source), IPAddress> _peers = new();
    internal void Add(int port, int source, IPAddress peer) =>
        _peers[(port, source)] = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
    internal void Remove(int port, int source) => _peers.TryRemove((port, source), out _);

    /// <summary>The address to attribute to a connection a loopback listener on <paramref name="port"/> accepted from <paramref name="source"/>.</summary>
    public IPAddress Resolve(int port, IPEndPoint source) => _peers.GetValueOrDefault((port, source.Port)) ?? source.Address;

    /// <summary>
    /// DIAGNOSTICS: true when the connection reached this PC directly, from loopback and not through a remote tunnel (a tunnel
    /// registers its source port while it lasts). Only such a seat may use the cheat commands in an AIR match.
    /// </summary>
    public bool IsDirectLocal(int port, IPEndPoint source) =>
        IPAddress.IsLoopback(source.Address) && !_peers.ContainsKey((port, source.Port));
}

/// <summary>
/// NOT RETAIL: the stock client connects to RTMP and XMPP directly (retail hosts on 2099 and 5222/5223). This host
/// keeps both loopback-only and carries the bytes for a remote friend through pinned HTTPS WebSockets instead. Each
/// WebSocket needs a live per-account bearer from <see cref="AirRemoteAccess"/>, checked before a connection slot is
/// taken, so strangers cannot hold the shared slots. Account/session authentication remains inside each protocol.
/// Each endpoint targets one fixed platform loopback port.
/// </summary>
public static class AirRemoteTunnel
{
    public const string Path = "/air/rtmp";
    public const string ChatPath = "/air/xmpp";
    public const string SignInPath = "/air/remote-token";
    private const int MaxConnections = 32;
    // NOT RETAIL: security addition (retail has no tunnel). A companion holds one RTMP and one XMPP tunnel; four per account
    // leave room for a reconnect that overlaps the old pair, so one token cannot take all 32 shared slots.
    public const int MaxConnectionsPerAccount = 4;
    private const int MaxMessageBytes = 256 * 1024;

    // Playtest B3 #11: /register is never served on the pinned remote port; the host creates accounts locally. Batch 656: the home
    // host's own sign-up is the one exception, outside this list, while Settings' switch is on (HostSelfRegistration.Serves).
    public static bool IsPublicPath(string path) => path is "/" or "/health" or "/ip" or "/landing" or "/story" or "/ladder" or
        "/login-queue/rest/queue/authenticate" or Path or ChatPath or SignInPath or
        "/bootstrap/client.json" or "/bootstrap/companion.zip" or "/bootstrap/Play.cmd" ||
        path.StartsWith("/status/", StringComparison.Ordinal) || path.StartsWith("/rss/", StringComparison.Ordinal) ||
        path.StartsWith("/store/", StringComparison.Ordinal) || path.StartsWith("/img/", StringComparison.Ordinal);

    /// <param name="log">DIAGNOSTICS: where the one <c>air.tunnel-end</c> line per tunnel goes (the platform's console by default).</param>
    public static void Map(WebApplication app, AirRemoteAccess access, int rtmpPort = 2099, int chatPort = 5222,
        AirTunnelPeers? peers = null, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(access);
        if (rtmpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(rtmpPort));
        if (chatPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(chatPort));

        log ??= Console.Out;
        var slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        var perAccount = new Dictionary<long, int>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Map(Path, branch => branch.Run(context => HandleAsync(context, rtmpPort, Path, slots, perAccount, peers, access, log)));
        app.Map(ChatPath, branch => branch.Run(context => HandleAsync(context, chatPort, ChatPath, slots, perAccount, peers, access, log)));
    }

    /// <summary>
    /// NOT RETAIL: the friend's sign-in for the tunnels, on the pinned HTTPS listener only. POST exchanges the login the host
    /// created for the account's bearer token. GET checks a stored token, so the companion drops a revoked or expired one
    /// before it opens a tunnel.
    /// </summary>
    public static void MapSignIn(WebApplication app, PlatformAccounts accounts, AirRemoteAccess access)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(access);
        app.MapGet(SignInPath, (HttpContext context) =>
            !context.Request.IsHttps ? Results.NotFound()
            : AuthorizeTunnel(context, access) is not null ? Results.Json(new { valid = true })
            : Results.StatusCode(context.Response.StatusCode));
        app.MapPost(SignInPath, async (HttpContext context) =>
        {
            if (!context.Request.IsHttps) return Results.NotFound();
            var address = context.Connection.RemoteIpAddress;
            if (access.Throttled(address)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            SignInRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<SignInRequest>(context.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(); }
            var account = request is { Username: { Length: > 0 and <= 64 } user, Password: { Length: > 0 and <= 256 } secret }
                ? accounts.Authenticate(user, secret) : null;
            if (account is null)
            {
                access.Failed(address);
                return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status403Forbidden);
            }
            var (token, expires) = access.Issue(account.Id);
            return Results.Json(new { token, expires });
        });
    }

    private sealed record SignInRequest(string? Username, string? Password);

    /// <summary>
    /// Use this for requests arriving on the remote TLS listener: it accepts only an HTTPS request whose
    /// <c>Authorization</c> header is exactly "Bearer " plus 64 characters, and that <paramref name="authenticate"/> accepts.
    /// Local HTTP routes never use this authorization.
    /// </summary>
    public static bool AuthorizeBearer(HttpContext context, Func<string, bool> authenticate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticate);
        if (!context.Request.IsHttps) return false;
        var header = context.Request.Headers["Authorization"].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length != 71)
            return false;
        return authenticate(header[7..]);
    }

    /// <summary>
    /// The token's account, or null after setting 429 while the address is throttled or 401 without a live token. Runs
    /// before any slot is taken.
    /// </summary>
    private static long? AuthorizeTunnel(HttpContext context, AirRemoteAccess access)
    {
        var address = context.Connection.RemoteIpAddress;
        if (access.Throttled(address))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return null;
        }
        long? account = null;
        if (AuthorizeBearer(context, token => (account = access.AccountFor(token)) is not null)) return account;
        access.Failed(address);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return null;
    }

    private static bool TakeAccountSlot(Dictionary<long, int> perAccount, long account)
    {
        lock (perAccount)
        {
            var held = perAccount.GetValueOrDefault(account);
            if (held >= MaxConnectionsPerAccount) return false;
            perAccount[account] = held + 1;
            return true;
        }
    }

    private static void ReleaseAccountSlot(Dictionary<long, int> perAccount, long account)
    {
        lock (perAccount)
        {
            if (perAccount[account] <= 1) perAccount.Remove(account);
            else perAccount[account]--;
        }
    }

    private static async Task HandleAsync(HttpContext context, int targetPort, string expectedPath,
        SemaphoreSlim slots, Dictionary<long, int> perAccount, AirTunnelPeers? peers, AirRemoteAccess access, TextWriter log)
    {
        if (!context.Request.IsHttps || !context.Request.Method.Equals("GET", StringComparison.Ordinal) ||
            !context.WebSockets.IsWebSocketRequest ||
            !string.Equals(context.Request.PathBase.Value, expectedPath, StringComparison.Ordinal) ||
            (context.Request.Path.Value is not (null or "" or "/")))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (AuthorizeTunnel(context, access) is not long account) return;
        if (!TakeAccountSlot(perAccount, account))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (!await slots.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            ReleaseAccountSlot(perAccount, account);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var source = 0;
        var stats = new TunnelStats();
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, targetPort, context.RequestAborted).ConfigureAwait(false);
            // Recorded before the first byte is pumped, so the loopback listener finds it when the connection is first used.
            if (peers is not null && context.Connection.RemoteIpAddress is { } peer)
                peers.Add(targetPort, source = ((IPEndPoint)tcp.Client.LocalEndPoint!).Port, peer);
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await PumpAsync(tcp.GetStream(), socket, stats, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { stats.Ended("remote", "request-aborted"); }
        catch (Exception error) when (error is IOException or SocketException or WebSocketException)
        {
            stats.Ended("setup", Describe(error));
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        finally
        {
            peers?.Remove(targetPort, source); slots.Release(); ReleaseAccountSlot(perAccount, account);
            try { log.WriteLine(stats.Line(expectedPath, account)); } catch { }
        }
    }

    /// <summary>
    /// DIAGNOSTICS (batch 544): what one tunnel saw, for its <c>air.tunnel-end</c> line. Every tunnel end shows up in Kestrel's
    /// log as "the application aborted the connection", because <see cref="PumpAsync"/> always aborts the WebSocket; this line
    /// says which side ended first and why. "host" is the platform's loopback RTMP/XMPP listener, "remote" the friend's
    /// companion and the network between. Type names and error codes only: no addresses, no message or close text.
    /// </summary>
    internal sealed class TunnelStats
    {
        private readonly long _started = Environment.TickCount64;
        private long _hostLast, _remoteLast, _toRemote, _fromRemote;
        private string? _end;

        internal TunnelStats() => _hostLast = _remoteLast = _started;
        internal void FromHost(int count) { _toRemote += count; _hostLast = Environment.TickCount64; }
        internal void FromRemote(int count) { _fromRemote += count; _remoteLast = Environment.TickCount64; }
        internal void Ended(string side, string reason) => Interlocked.CompareExchange(ref _end, $"first={side} reason={reason}", null);

        // Our own stop token is cancelled only after the first direction has recorded its end, so a cancellation that
        // comes first is Kestrel's RequestAborted: the remote connection went away.
        internal void Ended(string side, Exception error) =>
            Ended(error is OperationCanceledException ? "remote" : side, error is OperationCanceledException ? "request-aborted" : Describe(error));

        internal string Line(string path, long account)
        {
            var now = Environment.TickCount64;
            return $"air.tunnel-end path={path} account={account} {_end ?? "first=none reason=none"} ageMs={now - _started} " +
                   $"idleHostMs={now - _hostLast} idleRemoteMs={now - _remoteLast} toRemoteBytes={_toRemote} fromRemoteBytes={_fromRemote}";
        }
    }

    internal static string Describe(Exception error)
    {
        var parts = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            parts.Add(current switch
            {
                WebSocketException webSocket => $"WebSocketException:{webSocket.WebSocketErrorCode}",
                SocketException socket => $"SocketException:{socket.SocketErrorCode}",
                _ => current.GetType().Name,
            });
        return string.Join('>', parts);
    }

    internal static async Task PumpAsync(Stream tcp, WebSocket socket, TunnelStats stats, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcpToSocket = CopyTcpToSocketAsync(tcp, socket, stats, stop.Token);
        var socketToTcp = CopySocketToTcpAsync(socket, tcp, stats, stop.Token);
        try { await Task.WhenAny(tcpToSocket, socketToTcp).ConfigureAwait(false); }
        finally
        {
            stop.Cancel();
            try { socket.Abort(); } catch { }
            try { tcp.Close(); } catch { }
            await AwaitQuietly(tcpToSocket).ConfigureAwait(false);
            await AwaitQuietly(socketToTcp).ConfigureAwait(false);
        }
    }

    private static async Task CopyTcpToSocketAsync(Stream tcp, WebSocket socket, TunnelStats stats, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var side = "host";
        try
        {
            while (true)
            {
                side = "host";
                var count = await tcp.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) { stats.Ended("host", "eof"); return; }
                stats.FromHost(count);
                side = "remote";
                await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error) { stats.Ended(side, error); throw; }
    }

    private static async Task CopySocketToTcpAsync(WebSocket socket, Stream tcp, TunnelStats stats, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes];
        var side = "remote";
        try
        {
            while (true)
            {
                var total = 0;
                WebSocketReceiveResult result;
                side = "remote";
                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer, total, buffer.Length - total), cancellationToken)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        stats.Ended("remote", $"close:{result.CloseStatus?.ToString() ?? "none"}");
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        stats.Ended("remote", "non-binary-frame");
                        throw new WebSocketException("The AIR tunnel accepts binary frames only.");
                    }
                    total += result.Count;
                    if (total > MaxMessageBytes || (total == MaxMessageBytes && !result.EndOfMessage))
                    {
                        stats.Ended("remote", "message-too-large");
                        throw new WebSocketException("The AIR tunnel message is too large.");
                    }
                }
                while (!result.EndOfMessage);
                stats.FromRemote(total);
                side = "host";
                await tcp.WriteAsync(buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) { stats.Ended(side, error); throw; }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }
}
