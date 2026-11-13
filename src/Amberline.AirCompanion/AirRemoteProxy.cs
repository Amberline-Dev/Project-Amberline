using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Amberline.AirCompanion;

public sealed record AirRemoteInvitation(
    string Host,
    int Port = 8391,
    [property: JsonPropertyName("token")] string Token64hex = "",
    [property: JsonPropertyName("certificateSha256")] string CertificateSha25664hex = "",
    string? RegistrationCode = null,
    int ChatPort = 5222)
{
    [JsonPropertyName("bearer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bearer { get; init; }

    [JsonIgnore]
    public string EffectiveToken => string.IsNullOrEmpty(Token64hex) ? Bearer ?? string.Empty : Token64hex;

    public void Validate()
    {
        if (string.IsNullOrEmpty(Host) || Host.Length > 253 ||
            Host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw new ArgumentException("The remote AIR host must be a bare IPv4 address or DNS name.");
        if (Uri.CheckHostName(Host) is not (UriHostNameType.IPv4 or UriHostNameType.Dns))
            throw new ArgumentException("The remote AIR host must be a bare IPv4 address or DNS name.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("The remote AIR HTTPS port is invalid.");
        if (ChatPort is < 1 or > 65535)
            throw new ArgumentException("The remote AIR chat port is invalid.");
        if (!string.IsNullOrEmpty(Token64hex) && !string.IsNullOrEmpty(Bearer) &&
            !string.Equals(Token64hex, Bearer, StringComparison.Ordinal))
            throw new ArgumentException("The remote bearer and token fields disagree.");
        ValidateOptionalHex(EffectiveToken, "remote bearer token");
        ValidateHex(CertificateSha25664hex, "invitation certificate fingerprint");
        if (RegistrationCode is not null &&
            (RegistrationCode.Length > 128 || RegistrationCode.Any(char.IsControl)))
            throw new ArgumentException("The registration code is invalid.");
    }

    private static void ValidateHex(string? value, string description)
    {
        if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException($"The {description} must be 64 hexadecimal characters.");
    }

    private static void ValidateOptionalHex(string? value, string description)
    {
        if (string.IsNullOrEmpty(value)) return;
        ValidateHex(value, description);
    }
}

public sealed class AirRemoteProxyHandle : IAsyncDisposable
{
    private readonly WebApplication httpApp;
    private readonly TcpListener rtmp;
    private readonly TcpListener xmpp;
    private readonly HttpClient client;
    private readonly CancellationTokenSource stop;
    private readonly SemaphoreSlim rtmpSlots = new(16, 16);
    private readonly SemaphoreSlim xmppSlots = new(16, 16);
    private readonly ConcurrentDictionary<long, Task> rtmpConnections = new();
    private readonly ConcurrentDictionary<long, Task> xmppConnections = new();
    private readonly ConcurrentDictionary<long, TcpClient> acceptedClients = new();
    private readonly Uri remoteBase;
    private readonly Uri websocketUri;
    private readonly Uri xmppWebsocketUri;
    private readonly byte[] certificateFingerprint;
    // Set once, by the friend's login through the login queue, and read per tunnel connection: volatile, so readers never see a stale empty value.
    private volatile string bearer;
    private readonly AirRemoteInvitation invitation;
    private readonly string? connectionsDirectory;
    private readonly SemaphoreSlim signInGate = new(1, 1);
    private readonly CancellationTokenRegistration cancellationRegistration;
    private long nextOperation;
    private Task? httpStopTask;
    private Task? rtmpLoop;
    private Task? xmppLoop;
    private int disposed;

    internal AirRemoteProxyHandle(WebApplication httpApp, int httpPort, TcpListener rtmp, TcpListener xmpp,
        HttpClient client, CancellationTokenSource stop, AirRemoteInvitation invitation,
        byte[] certificateFingerprint, string? connectionsDirectory)
    {
        this.httpApp = httpApp;
        this.rtmp = rtmp;
        this.xmpp = xmpp;
        this.client = client;
        this.stop = stop;
        this.certificateFingerprint = certificateFingerprint;
        this.invitation = invitation;
        this.connectionsDirectory = connectionsDirectory;
        bearer = invitation.EffectiveToken;
        remoteBase = new UriBuilder(Uri.UriSchemeHttps, invitation.Host, invitation.Port).Uri;
        websocketUri = new UriBuilder(Uri.UriSchemeWss, invitation.Host, invitation.Port, "/air/rtmp").Uri;
        xmppWebsocketUri = new UriBuilder(Uri.UriSchemeWss, invitation.Host, invitation.Port, "/air/xmpp").Uri;
        HttpPort = httpPort;
        RtmpPort = ((IPEndPoint)rtmp.LocalEndpoint).Port;
        ChatPort = ((IPEndPoint)xmpp.LocalEndpoint).Port;
        cancellationRegistration = stop.Token.Register(StopListeners);
    }

    public int HttpPort { get; }
    public int RtmpPort { get; }
    public int ChatPort { get; }

    internal void Start()
    {
        if (stop.IsCancellationRequested) throw new OperationCanceledException(stop.Token);
        rtmpLoop = AcceptRtmpAsync();
        xmppLoop = AcceptXmppAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        StopListeners();
        cancellationRegistration.Dispose();
        try { rtmp.Stop(); } catch (ObjectDisposedException) { }
        try { xmpp.Stop(); } catch (ObjectDisposedException) { }
        foreach (var connection in acceptedClients.Values) connection.Dispose();

        await AwaitQuietly(httpStopTask).ConfigureAwait(false);
        await AwaitQuietly(rtmpLoop).ConfigureAwait(false);
        await AwaitQuietly(Task.WhenAll(rtmpConnections.Values.ToArray())).ConfigureAwait(false);
        await AwaitQuietly(xmppLoop).ConfigureAwait(false);
        await AwaitQuietly(Task.WhenAll(xmppConnections.Values.ToArray())).ConfigureAwait(false);
        await httpApp.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
        rtmpSlots.Dispose();
        xmppSlots.Dispose();
        stop.Dispose();
    }

    private void StopListeners()
    {
        _ = StopHttpAsync();
        try { rtmp.Stop(); } catch (ObjectDisposedException) { }
        try { xmpp.Stop(); } catch (ObjectDisposedException) { }
        foreach (var connection in acceptedClients.Values) connection.Dispose();
    }

    private Task StopHttpAsync()
    {
        var existing = Volatile.Read(ref httpStopTask);
        if (existing is not null) return existing;
        var created = StopHttpCoreAsync();
        existing = Interlocked.CompareExchange(ref httpStopTask, created, null);
        return existing ?? created;
    }

    private async Task StopHttpCoreAsync()
    {
        try { await httpApp.StopAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    internal async Task ServeHttpAsync(HttpContext context)
    {
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, context.RequestAborted);
        var cancellationToken = requestStop.Token;
        try
        {
            var request = context.Request;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
            var path = request.Path.Value ?? "/";
            if (request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]") || !AirRemoteProxy.IsAllowedPath(path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!HttpMethodAllowed(request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var body = await ReadBodyAsync(request.Body, request.ContentLength, cancellationToken)
                .ConfigureAwait(false);
            var target = BuildTargetUri(request, path);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
            if (body.Length != 0)
            {
                var content = new ByteArrayContent(body);
                if (!string.IsNullOrWhiteSpace(request.ContentType) &&
                    MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
                    content.Headers.ContentType = mediaType;
                message.Content = content;
            }
            if (bearer.Length != 0)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            var accept = request.Headers["Accept"].ToString();
            if (!string.IsNullOrWhiteSpace(accept))
                message.Headers.TryAddWithoutValidation("Accept", accept);
            if (path.StartsWith("/store/", StringComparison.Ordinal) &&
                request.Cookies.TryGetValue("Amberline.Store", out var storeCookie) &&
                storeCookie.Length == 64 && storeCookie.All(char.IsAsciiHexDigit))
                message.Headers.TryAddWithoutValidation("Cookie", "Amberline.Store=" + storeCookie);

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var responseBody = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
            // The friend's login in AIR is the sign-in: the login-queue answer is held until the bearer is stored, so the RTMP and
            // chat connections AIR opens after it carry the bearer.
            if (path == "/login-queue/rest/queue/authenticate" && response.IsSuccessStatusCode && HttpMethods.IsPost(request.Method))
                await SignInFromLoginAsync(body, cancellationToken).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            if (path is "/" || path.StartsWith("/store/", StringComparison.Ordinal))
            {
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                    foreach (var cookie in cookies.Where(value =>
                        path.StartsWith("/store/", StringComparison.Ordinal) && value.StartsWith("Amberline.Store=", StringComparison.Ordinal)))
                        context.Response.Headers.Append("Set-Cookie", cookie);
                if (response.Headers.Location is { IsAbsoluteUri: false } location &&
                    location.OriginalString.StartsWith("/store/", StringComparison.Ordinal) &&
                    AirRemoteProxy.IsAllowedPath(location.OriginalString.Split('?')[0]))
                    context.Response.Headers.Location = location.OriginalString;
            }
            if (path is "/landing" or "/story" || path.StartsWith("/store/", StringComparison.Ordinal))
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self'; form-action 'self'; frame-ancestors 'self' file: app:";
            if (response.Content.Headers.ContentType is { } responseType)
                context.Response.ContentType = responseType.ToString();
            context.Response.ContentLength = responseBody.Length;
            await context.Response.Body.WriteAsync(responseBody, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (PayloadTooLargeException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (HttpRequestException error)
        {
            Console.Error.WriteLine($"air.remote-http-failed types={AirRemoteProxy.ExceptionTypeChain(error)}");
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        catch (IOException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    /// <summary>
    /// NOT RETAIL: retail connects AIR straight to its RTMP and chat ports after the login-queue answer; this host carries them through
    /// a pinned tunnel that needs a bearer. The friend's login is that sign-in, so this signs in with the login AIR just posted, unless
    /// a bearer is already live. The login is used for this one request and never logged; a refusal or a network failure is logged
    /// without it, and the login-queue answer still goes to AIR.
    /// </summary>
    private async Task SignInFromLoginAsync(byte[] form, CancellationToken cancellationToken)
    {
        var credentials = RemoteSignIn.ReadLoginQueueForm(form);
        if (credentials is null || bearer.Length != 0) return;
        await signInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (bearer.Length != 0) return;
            bearer = await RemoteSignIn.SignInAsync(client, invitation, connectionsDirectory, credentials, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine("Remote AIR sign-in accepted; the RTMP and chat tunnels carry the account's bearer.");
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.TooManyRequests)
        {
            Console.Error.WriteLine("Too many failed sign-in attempts from this address. Wait 15 minutes, then sign in again in the AIR client.");
        }
        catch (HttpRequestException error)
        {
            Console.Error.WriteLine($"air.remote-token-failed status={(error.StatusCode is { } code ? (int)code : 0)} " +
                                    $"types={AirRemoteProxy.ExceptionTypeChain(error)}");
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"air.remote-token-failed types={AirRemoteProxy.ExceptionTypeChain(error)}");
        }
        finally
        {
            signInGate.Release();
        }
    }

    private Uri BuildTargetUri(HttpRequest request, string path) => new(remoteBase, path + request.QueryString.Value);

    private async Task AcceptRtmpAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient connection;
                try { connection = await rtmp.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (SocketException) when (stop.IsCancellationRequested) { break; }
                connection.NoDelay = true;
                if (!rtmpSlots.Wait(0))
                {
                    connection.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref nextOperation);
                acceptedClients[id] = connection;
                var task = ServeRtmpAsync(connection);
                rtmpConnections[id] = task;
                _ = ObserveRtmpAsync(id, connection, task);
            }
        }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }

    private async Task AcceptXmppAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient connection;
                try { connection = await xmpp.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (SocketException) when (stop.IsCancellationRequested) { break; }
                connection.NoDelay = true;
                if (!xmppSlots.Wait(0))
                {
                    connection.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref nextOperation);
                acceptedClients[id] = connection;
                var task = ServeXmppAsync(connection);
                xmppConnections[id] = task;
                _ = ObserveXmppAsync(id, connection, task);
            }
        }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }

    private async Task ObserveRtmpAsync(long id, TcpClient connection, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            acceptedClients.TryRemove(id, out _);
            rtmpConnections.TryRemove(id, out _);
            connection.Dispose();
            try { rtmpSlots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ObserveXmppAsync(long id, TcpClient connection, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            acceptedClients.TryRemove(id, out _);
            xmppConnections.TryRemove(id, out _);
            connection.Dispose();
            try { xmppSlots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ServeRtmpAsync(TcpClient connection)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.Proxy = null;
        var token = bearer;
        if (token.Length != 0)
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            AirRemoteProxy.ValidateCertificate(certificate, errors, certificateFingerprint);
        try
        {
            // NOT RETAIL: retail's platform always sent its own game server address in the game start. This tunnel tells the host the IPv4
            // address this PC reaches it at (the Play file's host, resolved as for the usual start), so the host can put it in this player's
            // game start: the 2011 launcher's own Maestro starts the game with that start unchanged. Every remote start sends it (the usual
            // start keeps its own substitution); a failed lookup sends none. Resolved off the accept loop.
            try
            {
                socket.Options.SetRequestHeader(AirRemoteProxy.GameAddressHeader,
                    await Task.Run(() => StockClientLauncher.GameAddress(invitation.Host), stop.Token).ConfigureAwait(false));
            }
            catch (Exception error) when (error is IOException or SocketException or ArgumentException) { }
            await socket.ConnectAsync(websocketUri, stop.Token).ConfigureAwait(false);
            using var stream = connection.GetStream();
            await AirRemoteProxy.PumpAsync(stream, socket, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SocketException or WebSocketException)
        { Console.Error.WriteLine($"air.remote-rtmp-closed types={AirRemoteProxy.ExceptionTypeChain(error)}"); }
    }

    private async Task ServeXmppAsync(TcpClient connection)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.Proxy = null;
        var token = bearer;
        if (token.Length != 0)
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            AirRemoteProxy.ValidateCertificate(certificate, errors, certificateFingerprint);
        try
        {
            await socket.ConnectAsync(xmppWebsocketUri, stop.Token).ConfigureAwait(false);
            using var stream = connection.GetStream();
            await AirRemoteProxy.PumpAsync(stream, socket, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SocketException or WebSocketException)
        { Console.Error.WriteLine($"air.remote-xmpp-closed types={AirRemoteProxy.ExceptionTypeChain(error)}"); }
    }

    private static bool HttpMethodAllowed(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBodyAsync(Stream source, long? contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength > AirRemoteProxy.MaxBodyBytes)
            throw new PayloadTooLargeException();
        return await ReadBoundedAsync(source, AirRemoteProxy.MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            AirRemoteProxy.MaxResponseBytes, cancellationToken).ConfigureAwait(false);

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new PayloadTooLargeException();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch { }
    }

    private sealed class PayloadTooLargeException : Exception;
}

public static class AirRemoteProxy
{
    internal const int MaxBodyBytes = 64 * 1024;
    // Responses carry the home and store artwork and the store pages: the stock home background,
    // the Xerath splash and the parchment are 250-800 KB, so the request cap would answer 413 and
    // every guest client lost them. The largest shipped asset is under 1 MiB.
    internal const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxWebSocketMessageBytes = 256 * 1024;
    /// <summary>The platform's <c>AirRemoteTunnel.GameAddressHeader</c>: the IPv4 address this PC reached the host at (spec §7).</summary>
    internal const string GameAddressHeader = "X-Game-Address";
    /// <summary>The HTTPS client for the host: it accepts only the invitation's certificate and follows no redirects, cookies or proxies.</summary>
    internal static HttpClient CreatePinnedClient(byte[] fingerprint)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Browser cookies belong to the originating AIR instance, not to
            // this shared proxy's backend HTTP connection pool.
            UseCookies = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                ValidateCertificate(certificate, errors, fingerprint),
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    public static Task<AirRemoteProxyHandle> StartAsync(AirRemoteInvitation invitation,
        int httpPort = 8390, int rtmpPort = 2099, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return StartWithPortsAsync(invitation, httpPort, rtmpPort, invitation.ChatPort, cancellationToken);
    }

    public static async Task<AirRemoteProxyHandle> StartWithPortsAsync(AirRemoteInvitation invitation,
        int httpPort, int rtmpPort, int chatPort, CancellationToken cancellationToken = default, string? connectionsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        invitation.Validate();
        ValidatePort(httpPort, "HTTP");
        ValidatePort(rtmpPort, "RTMP", allowZero: true);
        ValidatePort(chatPort, "chat", allowZero: true);
        cancellationToken.ThrowIfCancellationRequested();

        var fingerprint = Convert.FromHexString(invitation.CertificateSha25664hex);
        var client = CreatePinnedClient(fingerprint);
        var rtmp = new TcpListener(IPAddress.Loopback, rtmpPort);
        var xmpp = new TcpListener(IPAddress.Loopback, chatPort);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        WebApplication? httpApp = null;
        AirRemoteProxyHandle? handle = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = MaxBodyBytes;
                options.Limits.MaxConcurrentConnections = 32;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Loopback, httpPort);
            });
            var builtHttpApp = builder.Build();
            httpApp = builtHttpApp;
            builtHttpApp.Run(context => handle!.ServeHttpAsync(context));
            rtmp.Start(32);
            xmpp.Start(32);
            handle = new AirRemoteProxyHandle(builtHttpApp, httpPort, rtmp, xmpp, client, stop, invitation,
                fingerprint, connectionsDirectory);
            await builtHttpApp.StartAsync(cancellationToken).ConfigureAwait(false);
            handle.Start();
            return handle;
        }
        catch
        {
            if (handle is not null)
                await handle.DisposeAsync().ConfigureAwait(false);
            else
            {
                stop.Cancel();
                if (httpApp is not null)
                {
                    try { await httpApp.StopAsync().ConfigureAwait(false); } catch { }
                    await httpApp.DisposeAsync().ConfigureAwait(false);
                }
                rtmp.Stop();
                xmpp.Stop();
                client.Dispose();
                stop.Dispose();
            }
            throw;
        }
    }

    public static bool IsAllowedPath(string path)
    {
        if (path is null) return false;
        // Playtest B3 #11: /register is not proxied; the host creates accounts.
        // NOT RETAIL (batch 761): the 2011 launcher's news (LauncherNews.Route) reaches its player through this proxy.
        if (path is "/" or "/health" or "/ip" or "/landing" or "/story" or "/ladder" or
            "/login-queue/rest/queue/authenticate" or "/launcher/news") return true;
        if (path.StartsWith("/status/", StringComparison.Ordinal) ||
            path.StartsWith("/rss/", StringComparison.Ordinal) ||
            path.StartsWith("/store/", StringComparison.Ordinal) ||
            path.StartsWith("/img/", StringComparison.Ordinal))
        {
            try
            {
                var decoded = Uri.UnescapeDataString(path);
                return !decoded.Contains('\\') && !decoded.Contains('%') &&
                    !decoded.Split('/').Any(segment => segment is "." or "..");
            }
            catch (UriFormatException) { return false; }
        }
        return false;
    }

    internal static bool CertificateMatches(X509Certificate? certificate, byte[] expectedFingerprint)
    {
        if (certificate is null) return false;
        using var parsed = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var now = DateTime.UtcNow;
        return now >= parsed.NotBefore.ToUniversalTime() && now <= parsed.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(parsed.GetCertHash(HashAlgorithmName.SHA256), expectedFingerprint);
    }

    internal static bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors,
        byte[] expectedFingerprint)
    {
        try
        {
            var accepted = CertificateMatches(certificate, expectedFingerprint);
            if (!accepted)
                Console.Error.WriteLine($"air.remote-tls-cert-rejected errors={errors} certificate={(certificate is null ? "missing" : certificate.GetType().Name)}");
            return accepted;
        }
        catch (CryptographicException error)
        {
            Console.Error.WriteLine($"air.remote-tls-cert-rejected errors={errors} exception={error.GetType().Name}");
            return false;
        }
    }

    internal static string ExceptionTypeChain(Exception error)
    {
        var types = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            types.Add(current.GetType().Name);
        return string.Join(">", types);
    }

    internal static async Task PumpAsync(Stream tcp, WebSocket socket, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcpToSocket = CopyTcpToSocketAsync(tcp, socket, stop.Token);
        var socketToTcp = CopySocketToTcpAsync(socket, tcp, stop.Token);
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

    private static async Task CopyTcpToSocketAsync(Stream tcp, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await tcp.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task CopySocketToTcpAsync(WebSocket socket, Stream tcp, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxWebSocketMessageBytes];
        while (true)
        {
            var total = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new WebSocketException("The AIR tunnel accepts binary frames only.");
                total += result.Count;
                if (total > MaxWebSocketMessageBytes ||
                    (total == MaxWebSocketMessageBytes && !result.EndOfMessage))
                    throw new WebSocketException("The AIR tunnel message is too large.");
            }
            while (!result.EndOfMessage);
            await tcp.WriteAsync(buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }

    private static void ValidatePort(int port, string name, bool allowZero = false)
    {
        if (port < (allowZero ? 0 : 1) || port > 65535)
            throw new ArgumentOutOfRangeException(name, $"{name} port is invalid.");
    }
}
