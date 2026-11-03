using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using RtmpSharp.Messaging;
using RtmpSharp.Messaging.Messages;
using RtmpSharp.Net;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Amberline.Platform;

public sealed class AirRtmpHost : IAsyncDisposable
{
    private const string AuthenticationCode = "Server.Error.Authentication";
    private const string InvalidSessionCode = "Server.Error.InvalidSession";
    private const string ValidationCode = "Server.Error.Validation";
    private const string InternalCode = "Server.Error.Internal";
    private const string MessagingDestination = "messagingDestination";
    private const int MaxConnections = 64;
    private const int MaxSubscriptionsPerConnection = 3;
    private const int MaxResumeCredentialsLength = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly object _logGate = new();
    private readonly Dictionary<RtmpClient, ConnectionState> _connections = [];
    private readonly Func<string, string, string, object?[], object?> _route;
    private readonly Func<string, string, string, bool> _authenticateChannel;
    private readonly Func<string, long?> _accountForConnection;
    private readonly Action<string> _disconnect;
    private readonly Action<string> _connectionClosed;
    private readonly Func<string, string, string, bool>? _resumeSession;
    private readonly Action<string, string, string>? _responseSent;
    private readonly Action<string, IPEndPoint>? _peerObserved;
    private readonly TextWriter _log;
    private readonly RtmpServer _server;

    public AirRtmpHost(
        IPEndPoint endPoint,
        Func<string, string, string, object?[], object?> route,
        Func<string, string, string, bool> authenticateChannel,
        Func<string, long?> accountForConnection,
        Action<string> disconnect,
        TextWriter? log = null,
        Action<string, string, string>? responseSent = null,
        Func<string, string, string, bool>? resumeSession = null,
        Action<string>? connectionClosed = null,
        Action<string, IPEndPoint>? peerObserved = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        if (!IPAddress.IsLoopback(endPoint.Address))
            throw new ArgumentException("The AIR platform listener must be loopback-only.", nameof(endPoint));
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(authenticateChannel);
        ArgumentNullException.ThrowIfNull(accountForConnection);
        ArgumentNullException.ThrowIfNull(disconnect);

        _route = route;
        _authenticateChannel = authenticateChannel;
        _accountForConnection = accountForConnection;
        _disconnect = disconnect;
        _connectionClosed = connectionClosed ?? disconnect;
        _resumeSession = resumeSession;
        _responseSent = responseSent;
        _peerObserved = peerObserved;
        _log = log ?? TextWriter.Null;
        _server = new RtmpServer(endPoint, new SerializationContext());
        _server.ClientMessageReceived += OnRemotingMessage;
        _server.ClientCommandReceived += OnCommandMessage;
        _server.ClientConnectReceived += OnConnectReceived;
        _server.ClientDisconnected += OnClientDisconnected;
    }

    public IPEndPoint BoundEndPoint => _server.BoundEndPoint;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _server.StartAsync(cancellationToken);

    public Task StopAsync() => _server.StopAsync();

    public bool Push(long accountId, object body, string? channel = null)
    {
        // Inventory/balance notifications are consumed by ClientNotificationController,
        // not MasterGameController. This includes post-game boost refreshes.
        channel ??= body is AsObject dto && dto.TypeName?.StartsWith(AirProtocolNames.MessagingPrefix, StringComparison.Ordinal) == true
            ? "cn" : "gn";
        var subtopic = channel switch
        {
            "gn" => $"gn-{accountId}",
            "cn" => $"cn-{accountId}",
            "bc" => "bc",
            _ => throw new ArgumentException("Unknown AIR message channel.", nameof(channel))
        };

        var queued = false;
        lock (_gate)
        {
            foreach (var pair in _connections.ToArray())
            {
                var state = pair.Value;
                long? connectedAccount;
                try
                {
                    connectedAccount = _accountForConnection(state.Id);
                }
                catch (Exception exception)
                {
                    Log(state, "account-lookup", $"internal-error exception={exception.GetType().Name}", TimeSpan.Zero);
                    continue;
                }

                if (connectedAccount != accountId || pair.Key.IsDisconnected)
                    continue;

                foreach (var subscription in state.Subscriptions.Where(item => item.Subtopic == subtopic).ToArray())
                {
                    try
                    {
                        queued |= pair.Key.InvokeDestReceive(
                            subscription.ClientId,
                            subscription.Subtopic,
                            MessagingDestination,
                            body);
                    }
                    catch (Exception exception)
                    {
                        Log(state, $"push channel={channel}", $"internal-error exception={exception.GetType().Name}", TimeSpan.Zero, accountId: accountId);
                    }
                }
            }
        }

        return queued;
    }

    /// <summary>
    /// The <c>bc</c> channel: one message to every logged-in connection's broadcast subscription (the stock client subscribes it
    /// right after login). Returns how many connections it was queued for.
    /// </summary>
    public int Broadcast(object body)
    {
        var queued = 0;
        lock (_gate)
        {
            foreach (var (client, state) in _connections)
            {
                if (client.IsDisconnected || AccountFor(state) is null) continue;
                foreach (var subscription in state.Subscriptions.Where(item => item.Subtopic == "bc"))
                {
                    try { if (client.InvokeDestReceive(subscription.ClientId, subscription.Subtopic, MessagingDestination, body)) queued++; }
                    catch (Exception exception)
                    {
                        Log(state, "push channel=bc", $"internal-error exception={exception.GetType().Name}", TimeSpan.Zero);
                    }
                }
            }
        }
        return queued;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        _server.ClientMessageReceived -= OnRemotingMessage;
        _server.ClientCommandReceived -= OnCommandMessage;
        _server.ClientConnectReceived -= OnConnectReceived;
        _server.ClientDisconnected -= OnClientDisconnected;
    }

    internal static bool TryDecodeCredentials(object? body, out string username, out string token)
    {
        username = string.Empty;
        token = string.Empty;
        if (body is not string encoded || !TryUnfoldFlexBase64(encoded, out var canonical))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(canonical);
            if (!string.Equals(Convert.ToBase64String(bytes), canonical, StringComparison.Ordinal))
                return false;
            var decoded = StrictUtf8.GetString(bytes);
            var separator = decoded.IndexOf(':');
            if (separator <= 0 || separator != decoded.LastIndexOf(':') || separator == decoded.Length - 1)
                return false;
            username = decoded[..separator];
            token = decoded[(separator + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static bool TryUnfoldFlexBase64(string encoded, out string canonical)
    {
        canonical = string.Empty;
        if (encoded.Any(character => character != '\n' && char.IsWhiteSpace(character)))
            return false;

        canonical = encoded.Replace("\n", string.Empty, StringComparison.Ordinal);
        if (!encoded.Contains('\n'))
            return true;

        var folded = new StringBuilder(encoded.Length);
        for (var offset = 0; offset < canonical.Length; offset += 76)
        {
            var count = Math.Min(76, canonical.Length - offset);
            folded.Append(canonical, offset, count);
            if (count == 76)
                folded.Append('\n');
        }

        return string.Equals(encoded, folded.ToString(), StringComparison.Ordinal);
    }

    private void OnRemotingMessage(object? sender, RemotingMessageReceivedEventArgs args)
    {
        var client = (RtmpClient)sender!;
        var state = GetConnection(client);
        var stopwatch = Stopwatch.StartNew();
        var result = "result";
        string? internalCorrelationId = null;

        try
        {
            args.Data = _route(state.Id, args.Destination, args.Operation, ToArguments(args.Body));
            // Both replies and pushes use the same RTMP writer queue. Queue
            // the reply here before making a newly joined player matchable;
            // returning to RtmpServer first would race its scheduler tick.
            client.InvokeResult(args.InvokeId, args.MessageId, args.Data);
            args.ReturnRequired = false;
            try { _responseSent?.Invoke(state.Id, args.Destination, args.Operation); }
            catch (Exception exception)
            {
                // The caller already has its reply. Retire its session instead
                // of sending a contradictory second response for this invoke.
                Log(state, "reply-follow-up", $"internal-error exception={exception.GetType().Name}", TimeSpan.Zero);
                _disconnect(state.Id);
            }
            if (args.Destination == "loginService" && args.Operation == "logout")
            {
                _disconnect(state.Id);
                lock (_gate) state.Subscriptions.Clear();
            }
        }
        catch (ArgumentException exception)
        {
            result = $"error code={ValidationCode}";
            if (exception is AirFault fault)
            {
                // Retail raised a typed platform exception; the client shows its errorCode text (see AirFault).
                result += $" platformCode={fault.ErrorCode}";
                client.InvokeError(args.InvokeId, new ErrorMessage
                {
                    ClientId = Uuid.NewUuid(),
                    MessageId = Uuid.NewUuid(),
                    CorrelationId = args.MessageId,
                    FaultCode = ValidationCode,
                    FaultString = exception.Message,
                    FaultDetail = string.Empty,
                    RootCause = fault.RootCause()
                });
            }
            else client.InvokeError(args.InvokeId, args.MessageId, ValidationCode, exception.Message);
            result += $" reason={ReasonCode(exception.Message)}";
        }
        catch (Exception exception)
        {
            internalCorrelationId = NewOpaqueId();
            result = $"internal-error exception={exception.GetType().Name}";
            client.InvokeError(args.InvokeId, args.MessageId, InternalCode,
                $"Internal server error. Correlation ID: {internalCorrelationId}");
        }
        finally
        {
            Log(state, $"destination={args.Destination} operation={args.Operation}", result,
                stopwatch.Elapsed, internalCorrelationId, AccountFor(state));
        }
    }

    private void OnCommandMessage(object? sender, CommandMessageReceivedEventArgs args)
    {
        var client = (RtmpClient)sender!;
        var state = GetConnection(client);
        var stopwatch = Stopwatch.StartNew();
        var accountId = AccountFor(state);

        try
        {
            switch (args.Message.Operation)
            {
                case CommandOperation.Login:
                    HandleLogin(client, state, args);
                    break;
                case CommandOperation.Subscribe:
                    HandleSubscription(client, state, args, subscribe: true);
                    break;
                case CommandOperation.Unsubscribe:
                    HandleSubscription(client, state, args, subscribe: false);
                    break;
                case CommandOperation.Logout:
                    _disconnect(state.Id);
                    state.Subscriptions.Clear();
                    Succeed(args);
                    break;
            }
        }
        catch (ArgumentException exception)
        {
            SetError(args, ValidationCode, exception.Message);
        }
        catch (Exception exception)
        {
            var internalCorrelationId = NewOpaqueId();
            SetError(args, InternalCode, $"Internal server error. Correlation ID: {internalCorrelationId}");
            Log(state, $"command={args.Message.Operation}", $"internal-error exception={exception.GetType().Name}",
                stopwatch.Elapsed, internalCorrelationId, accountId ?? AccountFor(state));
            return;
        }

        accountId ??= AccountFor(state);
        var result = args.Error is null
            ? args.Handled ? "result" : "unhandled"
            : $"error code={args.Error.FaultCode}";
        Log(state, $"command={args.Message.Operation}", result, stopwatch.Elapsed, accountId: accountId);
    }

    // A reconnecting stock client presents the channel credentials of its earlier login in the connect command and
    // sends no login command afterwards (see PlatformSessions). They are the same proof the channel login takes - the
    // account name and the server-issued session token - and PlatformSessions.Resume binds the parked session to this
    // connection only for a matching token within its resume window (60 minutes after the close). Anything else leaves the connection unauthenticated,
    // exactly as a first connection is. The stock client keeps the Flex client id of its first connection and quotes it
    // in every later message, so a resumed connection adopts it for the identity checks of login and subscribe.
    private void OnConnectReceived(object? sender, ConnectReceivedEventArgs args)
    {
        if (_resumeSession is null || string.IsNullOrEmpty(args.Credentials)) return;
        var client = (RtmpClient)sender!;
        var state = GetConnection(client);
        var stopwatch = Stopwatch.StartNew();
        var resumed = false;
        try
        {
            resumed = args.Credentials.Length <= MaxResumeCredentialsLength &&
                TryDecodeCredentials(args.Credentials, out var username, out var token) &&
                _resumeSession(state.Id, username, token);
            if (resumed && Guid.TryParseExact(args.FlexClientId, "D", out _))
                client.ClientId = args.FlexClientId;
        }
        catch (Exception exception)
        {
            Log(state, "connect=resume", $"internal-error exception={exception.GetType().Name}", stopwatch.Elapsed);
            return;
        }
        Log(state, "connect=resume", resumed ? "resumed" : "refused", stopwatch.Elapsed, accountId: resumed ? AccountFor(state) : null);
    }

    private void HandleLogin(RtmpClient client, ConnectionState state, CommandMessageReceivedEventArgs args)
    {
        if (!MatchesClientIdentity(args.DSId, client.ClientId) ||
            !TryDecodeCredentials(args.Message.Body, out var username, out var token) ||
            !_authenticateChannel(state.Id, username, token))
        {
            SetError(args, AuthenticationCode, "Invalid username or token.");
            return;
        }

        Succeed(args);
    }

    private void HandleSubscription(
        RtmpClient client,
        ConnectionState state,
        CommandMessageReceivedEventArgs args,
        bool subscribe)
    {
        var accountId = AccountFor(state);
        var subtopic = args.Message.Headers is not null &&
                       args.Message.Headers.TryGetValue("DSSubtopic", out var value) ? value as string : null;
        var expectedAccount = accountId is long id ? id.ToString() : string.Empty;
        var validSubtopic = string.Equals(subtopic, $"gn-{expectedAccount}", StringComparison.Ordinal) ||
                            string.Equals(subtopic, $"cn-{expectedAccount}", StringComparison.Ordinal) ||
                            string.Equals(subtopic, "bc", StringComparison.Ordinal);
        var clientId = args.Message.ClientId;

        if (accountId is null ||
            !string.Equals(args.Message.Destination, MessagingDestination, StringComparison.Ordinal) ||
            !MatchesClientIdentity(args.DSId, client.ClientId) ||
            !validSubtopic ||
            string.IsNullOrWhiteSpace(clientId) ||
            (subtopic == "bc" && !string.Equals(clientId, $"bc-{expectedAccount}", StringComparison.Ordinal)))
        {
            SetError(args, InvalidSessionCode, "Subscription does not match the active account.");
            return;
        }

        var subscription = new Subscription(accountId.Value, clientId, subtopic!);
        lock (_gate)
        {
            if (subscribe)
            {
                if (!state.Subscriptions.Contains(subscription) && state.Subscriptions.Count >= MaxSubscriptionsPerConnection)
                {
                    SetError(args, InvalidSessionCode, "Connection subscription limit reached.");
                    return;
                }
                state.Subscriptions.Remove(subscription);
                state.Subscriptions.Add(subscription);
            }
            else if (!state.Subscriptions.Remove(subscription))
            {
                SetError(args, InvalidSessionCode, "Subscription does not match the active account.");
                return;
            }
        }

        Succeed(args);
    }

    private void OnClientDisconnected(object? sender, EventArgs args)
    {
        var client = (RtmpClient)sender!;
        ConnectionState state;
        lock (_gate)
        {
            if (!_connections.Remove(client, out var removed))
                return;
            state = removed;
            state.Subscriptions.Clear();
        }

        var accountId = AccountFor(state);
        try
        {
            // Not a logout: the session may be resumed by a brief reconnect (PlatformSessions.Suspend).
            _connectionClosed(state.Id);
        }
        catch (Exception exception)
        {
            Log(state, "connection=closed", $"internal-error exception={exception.GetType().Name}", TimeSpan.Zero);
        }
        // DIAGNOSTICS (batch 544): why the RTMP side closed, to pair with the tunnel's air.tunnel-end line for a friend.
        Log(state, "connection=closed", $"disconnected reason={client.DisconnectReason}", TimeSpan.Zero, accountId: accountId);
    }

    private ConnectionState GetConnection(RtmpClient client)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(client, out var state))
                return state;
            if (_connections.Count >= MaxConnections)
            {
                client.Close();
                throw new InvalidOperationException("AIR platform connection limit reached.");
            }

            state = new ConnectionState(NewOpaqueId());
            _connections.Add(client, state);
            // NOT RETAIL: operator tooling; the address only feeds the host page's Online table (see PlatformSessions.ObservePeer).
            if (client.RemoteEndPoint is { } peer) _peerObserved?.Invoke(state.Id, peer);
            return state;
        }
    }

    private long? AccountFor(ConnectionState state)
    {
        try { return _accountForConnection(state.Id); }
        catch (Exception exception)
        {
            Log(state, "account-lookup", $"internal-error exception={exception.GetType().Name}", TimeSpan.Zero);
            return null;
        }
    }

    private void Log(
        ConnectionState state,
        string operation,
        string result,
        TimeSpan elapsed,
        string? internalCorrelationId = null,
        long? accountId = null)
    {
        var account = accountId?.ToString() ?? "none";
        var correlation = internalCorrelationId is null ? string.Empty : $" correlation={internalCorrelationId}";
        try
        {
            lock (_logGate)
            {
                _log.WriteLine($"connection={state.Id} {operation} account={account} result={result} elapsedMs={(long)elapsed.TotalMilliseconds}{correlation}");
                _log.Flush();
            }
        }
        catch
        {
        }
    }

    private static object?[] ToArguments(object? body)
    {
        if (body is null)
            return [];
        if (body is object?[] arguments)
            return arguments;
        if (body is ArrayCollection collection)
            return collection.Cast<object?>().ToArray();
        if (body is Array array && array.Rank == 1)
            return array.Cast<object?>().ToArray();
        return [body];
    }

    private static bool MatchesClientIdentity(string? dsId, string clientId) =>
        string.IsNullOrEmpty(dsId) || string.Equals(dsId, clientId, StringComparison.Ordinal);

    private static void Succeed(CommandMessageReceivedEventArgs args)
    {
        args.Handled = true;
        args.Result = "success";
    }

    private static void SetError(CommandMessageReceivedEventArgs args, string code, string message)
    {
        args.Handled = true;
        args.Error = new ErrorMessage
        {
            ClientId = Uuid.NewUuid(),
            MessageId = Uuid.NewUuid(),
            CorrelationId = args.Message.MessageId,
            FaultCode = code,
            FaultString = message,
            FaultDetail = string.Empty
        };
    }

    /// <summary>
    /// The refusal's reason for the operation line: the first six words of its own text, lower case and hyphenated. The
    /// platform writes refusal texts as code literals that name at most ids and code names (the operation's own destination
    /// and name, already on the line), never a request argument, so the reason carries no personal data.
    /// </summary>
    internal static string ReasonCode(string message) =>
        string.Join('-', Regex.Matches(message.ToLowerInvariant(), "[a-z0-9]+").Take(6).Select(match => match.Value)) is { Length: > 0 } reason
            ? reason : "none";

    private static string NewOpaqueId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private sealed class ConnectionState(string id)
    {
        public string Id { get; } = id;
        public HashSet<Subscription> Subscriptions { get; } = [];
    }

    private readonly record struct Subscription(long AccountId, string ClientId, string Subtopic);
}
