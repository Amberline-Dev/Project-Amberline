using System.Diagnostics;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Transport;
using Amberline.Network.Diagnostics;

namespace Amberline.Network.Sessions;

/// <summary>
/// Synchronous native input/output pump for one <see cref="MatchRuntime"/>.
/// A host and all of its peers are owned by the creating thread; callers must
/// invoke <see cref="Pump"/> from that same thread.
/// </summary>
public sealed class MatchRuntimeNetwork : IDisposable
{
    public const double AuthenticationTimeoutSeconds = 10.0;

    // Keep input dispatch below roughly a quarter of the 30 Hz source step;
    // one event may still run past this bound when its handler is expensive.
    public const int DefaultDrainBudgetMilliseconds = 8;

    // These packets are emitted by the stock client on channel 1 but have no
    // server-side effect in this protocol version.  Keep them named at DEBUG
    // level so a capture remains useful without turning normal camera/UI
    // traffic into WARN noise.
    private static readonly IReadOnlyDictionary<byte, string> IgnoredCh1Opcodes =
        new Dictionary<byte, string>
        {
            [0x30] = "WorldSendCameraServer",
            [0xB0] = "OnReplicationAcc",
            [0x7A] = "WaypointAcc",
            [0x86] = "WorldLockCameraServer",
            [0xB7] = "SendSelectedObjID",
        };

    // Dispatchable channel-1 input is intentionally silent here.  This set
    // prevents the receive diagnostic from warning on packets handled by the
    // session below; it is not an authorization or gameplay dispatch table.
    private static readonly IReadOnlySet<byte> KnownCh1Opcodes =
        new HashSet<byte>(IgnoredCh1Opcodes.Keys.Concat(new byte[]
        {
            0x09, 0x17, 0x19, 0x23, 0x3E, DuelSession.UseObjectOpcode, EmotePackets.ClientOpcode, 0x53, 0x55, 0x59, 0x5A, 0x60, 0x75, 0x87,
            0x94, 0xA2, 0xA4, SurrenderPackets.ClientOpcode, PausePackets.PauseOpcode,
            PausePackets.ResumeOpcode, 0xB6, 0xC5, 0xC6, 0xC7,
            TutorialPackets.PopupClosedClientOpcode, TutorialPackets.AudioEventFinishedClientOpcode, DominionPackets.QuestEventClientOpcode,
        }));

    private readonly MatchRuntime _runtime;
    private readonly LeagueEnetHost _host;
    private readonly bool _ownsHost;
    private readonly ISessionLogSink? _log;
    private readonly Dictionary<long, LeagueEnetPeer> _peers = new();
    private readonly Dictionary<long, long> _lastReceiveAt = new();
    private readonly Dictionary<long, (long At, LeagueEnetPeerStats Stats)> _peerSamples = new();
    private readonly Dictionary<long, ReplicationObserver> _observers = new();
    private readonly Dictionary<long, long> _authenticationDeadlines = new();
    private readonly Dictionary<(string Event, string Reason), int> _authenticationOutcomeCounts = new();
    private readonly Func<long> _monotonicTimestamp;
    private long _lastAuthenticationSummary;
    private bool _disposed;

    public MatchRuntimeNetwork(MatchRuntime runtime, LeagueEnetHost host, bool ownsHost = false,
        ISessionLogSink? log = null, Func<long>? monotonicTimestamp = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ownsHost = ownsHost;
        _log = log;
        _monotonicTimestamp = monotonicTimestamp ?? Stopwatch.GetTimestamp;
    }

    public MatchRuntime Runtime => _runtime;
    public LeagueEnetHost Host => _host;
    public ushort Port => _host.Port;
    /// <summary>Current native connection generations owned by this network.</summary>
    public IReadOnlyList<long> ConnectionIds => _peers.Keys.ToArray();

    /// <summary>Creates a loopback-safe network shell on an ephemeral port.</summary>
    public static MatchRuntimeNetwork CreateEphemeral(MatchRuntime runtime,
        int peerLimit = 2, int channelLimit = 255)
    {
        var host = new LeagueEnetHost(0, peerLimit, channelLimit, bindHost: "127.0.0.1",
            maximumPacketSize: LeagueEnetHost.ProductionMaximumPacketSize,
            maximumWaitingData: LeagueEnetHost.ProductionMaximumWaitingData);
        return new MatchRuntimeNetwork(runtime, host, ownsHost: true);
    }

    /// <summary>Builds a generation-bound replication transport for a peer.</summary>
    public IReplicationTransport ReplicationTransport(long connectionId) =>
        new PeerReplicationTransport(this, connectionId);

    /// <summary>
    /// Advances one already accumulated simulation step.  The caller owns
    /// wall-to-simulation accumulation; this boundary never double-scales or
    /// invents elapsed time while servicing native events.
    /// </summary>
    public void Tick(double dt)
    {
        EnsureUsable();
        _runtime.Tick(dt);
        if (dt > 1e-12) SendReconnectStates();
        // MatchRuntime.Flushes Delivery once per source simulation step. Keep
        // this boundary to the native flush so an oversized wall step does
        // not cause an extra visibility/lifecycle pass.
        _host.Flush();
    }

    /// <summary>
    /// Flushes queued observer messages after a simulation or lifecycle phase.
    /// Native event polling never calls this implicitly, so visibility is not
    /// recomputed once per empty ENet poll.
    /// </summary>
    public void FlushDelivery()
    {
        EnsureUsable();
        _runtime.Delivery.Flush();
        SendReconnectStates();
        _host.Flush();
    }

    /// <summary>
    /// Attaches the authenticated participant's existing visibility state to
    /// Delivery. The observer ID is the fixed session player ID; the transport
    /// remains bound to this ENet connection generation.
    /// </summary>
    public ReplicationObserver AttachObserver(long connectionId)
    {
        EnsureUsable();
        if (!_peers.ContainsKey(connectionId))
            throw new InvalidOperationException("Cannot attach an observer before the peer connects.");
        var participant = _runtime.Session.Participant(connectionId)
            ?? throw new InvalidOperationException("Cannot attach an observer before authentication.");
        if (participant.Hero is null) throw new InvalidOperationException("Cannot attach an observer before hero creation.");
        if (_observers.TryGetValue(connectionId, out var existing))
        {
            existing.Ready = participant.Ready;
            return existing;
        }

        var observer = NewObserver(_runtime, participant, ReplicationTransport(connectionId));
        _runtime.Delivery.AddObserver(observer);
        _observers.Add(connectionId, observer);
        return observer;
    }

    /// <summary>
    /// The replication observer of an authenticated seat: its own view, its readiness, and a death recap that waits for the reconnect
    /// burst when the seat is a returning one. Shared by <see cref="AttachObserver"/> and the headless match, which has no ENet peers.
    /// </summary>
    internal static ReplicationObserver NewObserver(MatchRuntime runtime, SessionParticipant participant, IReplicationTransport transport) =>
        new(participant.PlayerId, participant.Team, participant.Hero?.NetId
                ?? throw new InvalidOperationException("Cannot attach an observer before hero creation."),
            transport, ready: participant.Ready, view: participant.View, clientId: participant.ClientId,
            deathRecapReady: !runtime.Session.IsReconnectParticipant(participant.PlayerId));

    /// <summary>
    /// Services native events, drains at most <paramref name="maxEvents"/>
    /// and <paramref name="drainBudgetMilliseconds"/>, runs the session
    /// scheduler, flushes observer delivery, and flushes ENet.
    ///
    /// The first event is always handled. A time limit can only stop between
    /// complete events, so it never discards or reorders native input.
    /// </summary>
    public int Pump(int timeoutMilliseconds = 0, int maxEvents = 32,
        bool flushDelivery = true, int? drainBudgetMilliseconds = null)
    {
        EnsureUsable();
        if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (drainBudgetMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(drainBudgetMilliseconds));

        var handled = 0;
        var first = true;
        var drainStarted = Stopwatch.GetTimestamp();
        var now = _monotonicTimestamp();
        FlushAuthenticationOutcomeSummaries(now);
        ExpirePendingAuthentications(now);
        var drainBudgetTicks = drainBudgetMilliseconds is int budget
            ? checked((long)Math.Ceiling(budget * (double)Stopwatch.Frequency / 1000.0))
            : -1;
        while (handled < maxEvents)
        {
            SamplePeerHealth();
            var serviceTimeout = first
                ? ClampAuthenticationTimeout(timeoutMilliseconds, _monotonicTimestamp())
                : 0;
            var @event = first ? _host.Service(serviceTimeout) : _host.CheckEvents();
            first = false;
            ExpirePendingAuthentications(_monotonicTimestamp());
            if (@event.Type == LeagueEnetEventType.None) break;
            handled++;
            switch (@event.Type)
            {
                case LeagueEnetEventType.Connect when @event.Peer is { } peer:
                    // The port is internet-facing: the shared one-per-second aggregation bounds this unauthenticated
                    // line (first connect each second, the rest as connection.opened.aggregate), and it carries no
                    // address. A game that never sees a seat's client still shows its attempts.
                    RecordAuthenticationOutcome("connection.opened", "connect", peer.ConnectionId);
                    _peers[peer.ConnectionId] = peer;
                    _lastReceiveAt[peer.ConnectionId] = _monotonicTimestamp();
                    StartAuthenticationDeadline(peer.ConnectionId, _monotonicTimestamp());
                    break;
                case LeagueEnetEventType.Receive when @event.Peer is { } receivePeer:
                    if (!_peers.TryGetValue(receivePeer.ConnectionId, out var activePeer) ||
                        !ReferenceEquals(activePeer, receivePeer) || !receivePeer.IsUsable)
                        break;
                    _lastReceiveAt[receivePeer.ConnectionId] = _monotonicTimestamp();

                    if (_authenticationDeadlines.ContainsKey(receivePeer.ConnectionId))
                    {
                        // Pre-auth input is intentionally opaque: only the
                        // existing channel-zero key check reaches the cipher
                        // or session. All other channels are silently dropped.
                        if (@event.Channel != 0)
                            break;
                        if (!SessionKeyCheck.TryReadPlayerId(
                                @event.Payload, _runtime.Cipher, out _))
                        {
                            RejectPendingAuthentication(receivePeer, "invalid");
                            break;
                        }

                        RetirePreviousConnection(receivePeer.ConnectionId, @event.Payload);
                        var authOutputs = _runtime.Session.Receive(
                            receivePeer.ConnectionId, 0, @event.Payload,
                            _runtime.Dependencies.Gameplay);
                        if (_runtime.Session.Participant(receivePeer.ConnectionId) is null)
                        {
                            RejectPendingAuthentication(receivePeer, "rejected");
                            break;
                        }

                        _authenticationDeadlines.Remove(receivePeer.ConnectionId);
                        _log?.Write("NET", "INFO", "auth.accepted", new Dictionary<string, object?>
                        {
                            ["connection_id"] = receivePeer.ConnectionId,
                            ["n"] = @event.Payload.Length,
                            ["redacted"] = true,
                        });
                        Send(authOutputs);
                        break;
                    }

                    // Channel-zero is never packet-traced. A retry from an
                    // authenticated generation must still be validated against
                    // that generation's current seat before the existing
                    // session boundary sees it. In particular, do not let a
                    // valid key for another or disconnected seat acquire or
                    // replace this connection after the match starts.
                    if (@event.Channel == 0)
                    {
                        var participant = _runtime.Session.Participant(receivePeer.ConnectionId);
                        if (participant is null ||
                            !SessionKeyCheck.TryReadPlayerId(
                                @event.Payload, _runtime.Cipher, out var playerId) ||
                            participant.PlayerId != playerId)
                            break;
                        Send(_runtime.Session.Receive(
                            receivePeer.ConnectionId, 0, @event.Payload,
                            _runtime.Dependencies.Gameplay));
                        break;
                    }

                    if (LogIncomingPacket(@event))
                        LogIncomingOpcode(@event);
                    var plain = @event.Channel == 1 && @event.Payload.Length >= LegacyBlowfishCipher.BlockSize
                        ? _runtime.Cipher.Decrypt(@event.Payload) : @event.Payload;
                    if (@event.Channel == 1 && plain.Length >= 5 && plain[0] == 0x94 &&
                        _runtime.Session.Participant(receivePeer.ConnectionId) is not null)
                    {
                        // The stock client raises OnLeave itself from this notice (DuelSession.Presence.cs).
                        Send(_runtime.Session.BuildExitNotice(receivePeer.ConnectionId));
                        Detach(receivePeer.ConnectionId, "client-exit");
                        receivePeer.DisconnectNow();
                        break;
                    }
                    if ((uint)@event.Channel <= byte.MaxValue)
                    {
                        var outputs = _runtime.Session.Receive(
                            receivePeer.ConnectionId,
                            (byte)@event.Channel,
                            @event.Payload,
                            _runtime.Dependencies.Gameplay);
                        Send(outputs);
                        if (@event.Channel == 1 && plain.Length == 5 && plain[0] == 0x59 &&
                            _runtime.Session.BuildClassicHeroStatsRefreshBurst(
                                receivePeer.ConnectionId, plain) is { Count: > 0 } classicStats)
                        {
                            // 0x59 first refreshes the effective 0xCC through
                            // HeroStatsService. Flush that queued absolute
                            // combat packet before the owner and public Classic
                            // 0x4B snapshots so the native HUD observes the same
                            // state order.
                            _runtime.Delivery.Flush();
                            foreach (var classicStat in classicStats)
                                Send(receivePeer, classicStat);
                        }
                    }
                    break;
                case LeagueEnetEventType.Disconnect when @event.Peer is { } disconnectPeer:
                    // Preserve the source's immediate stop/re-anchor packets
                    // for the remaining observers before detaching this
                    // connection generation.  ENet retires the wrapper while
                    // producing its disconnect event, so sending back through
                    // disconnectPeer would be an invalid stale-peer send.
                    Detach(disconnectPeer.ConnectionId, "enet-disconnect", @event.Data);
                    break;
            }

            if (drainBudgetTicks >= 0 &&
                Stopwatch.GetTimestamp() - drainStarted >= drainBudgetTicks)
                break;
        }

        EnsureObservers();
        Send(_runtime.Session.RunSchedulers());
        KickAfkPeers();
        DropLoadingTimeouts();
        // The production loop pumps without flushing and then ticks. Publish
        // scheduler readiness to the observer before either flush path runs.
        EnsureObservers();
        if (flushDelivery)
        {
            // Delivery first rebuilds the current visible world for the new
            // observer.  The seat-local state burst follows that reveal so
            // inventory/cooldown packets never precede the hero creation.
            _runtime.Delivery.Flush();
            SendReconnectStates();
        }
        _host.Flush();
        return handled;
    }

    private void SamplePeerHealth()
    {
        if (_log is null) return;
        var now = _monotonicTimestamp();
        foreach (var (connectionId, peer) in _peers)
        {
            if (!peer.IsUsable || _peerSamples.TryGetValue(connectionId, out var previous) &&
                now - previous.At < Stopwatch.Frequency) continue;
            try { _peerSamples[connectionId] = (now, peer.Stats); }
            catch (InvalidOperationException) { /* A retired peer uses its last sample. */ }
        }
    }

    private void LogDisconnect(long connectionId, string reason, uint? eventData)
    {
        var participant = _runtime.Session.Participant(connectionId);
        if (_log is null || participant is null) return;
        var now = _monotonicTimestamp();
        // ENet has already retired the wrapper for a native disconnect event.
        // Read fresh stats only for our own still-usable, local transitions.
        if (_peers.TryGetValue(connectionId, out var peer) && peer.IsUsable)
            try { _peerSamples[connectionId] = (now, peer.Stats); }
            catch (InvalidOperationException) { }
        var properties = new Dictionary<string, object?>
        {
            ["connection_id"] = connectionId, ["player_id"] = participant.PlayerId,
            ["reason"] = reason, ["event_data"] = eventData,
            ["game_time"] = _runtime.Session.HudTime,
            ["last_receive_age_seconds"] = _lastReceiveAt.TryGetValue(connectionId, out var received)
                ? Math.Max(0, (now - received) / (double)Stopwatch.Frequency) : null,
        };
        if (_peerSamples.TryGetValue(connectionId, out var sample))
        {
            properties["stats_age_seconds"] = Math.Max(0, (now - sample.At) / (double)Stopwatch.Frequency);
            properties["peer_state"] = sample.Stats.State;
            properties["rtt_ms"] = sample.Stats.RoundTripTimeMs;
            properties["rtt_variance_ms"] = sample.Stats.RoundTripTimeVarianceMs;
            properties["packets_sent"] = sample.Stats.PacketsSent;
            properties["packets_lost"] = sample.Stats.PacketsLost;
            properties["packet_loss"] = sample.Stats.PacketLoss;
            properties["waiting_data_bytes"] = (ulong)sample.Stats.WaitingDataBytes;
            properties["incoming_bytes"] = sample.Stats.IncomingDataTotal;
            properties["outgoing_bytes"] = sample.Stats.OutgoingDataTotal;
        }
        _log.Write("NET", "INFO", "connection.disconnected", properties);
    }

    private void Detach(long connectionId, string reason, uint? eventData = null)
    {
        LogDisconnect(connectionId, reason, eventData);
        _lastReceiveAt.Remove(connectionId);
        _peerSamples.Remove(connectionId);
        _authenticationDeadlines.Remove(connectionId);
        Send(DetachSeat(_runtime, connectionId));
        if (_observers.Remove(connectionId, out var observer))
            _runtime.Delivery.RemoveObserver(observer.ObserverId);
        _peers.Remove(connectionId);
        _runtime.MovementTrace?.ForgetConnection(connectionId);
    }

    /// <summary>
    /// The session side of a lost connection, shared with the headless match: the registry removal the stock client announces "has left the
    /// game!" from (DuelSession.Presence.cs), returned for the other seats before the seat detaches, and the detached hero's stop and
    /// re-anchor packets, queued for the remaining observers.
    /// </summary>
    internal static IReadOnlyList<SessionOutboundPacket> DetachSeat(MatchRuntime runtime, long connectionId)
    {
        var notice = runtime.Session.BuildDisconnectNotice(connectionId).ToArray();
        foreach (var packet in runtime.Session.DisconnectPackets(connectionId))
            runtime.Delivery.Enqueue(packet);
        return notice;
    }

    /// <summary>
    /// A seat that did not finish loading within the session's limit starts the match detached. An attached peer
    /// is disconnected, which ends a client still in its loading screen (the stock client terminates when it is
    /// disconnected while loading); the player returns through the ordinary reconnect path. The retail
    /// announcement "was disconnected due to loading time out!" cannot be raised: no client code path reads it.
    /// </summary>
    private void DropLoadingTimeouts()
    {
        foreach (var expiry in _runtime.Session.ConsumeLoadingExpiries())
        {
            _log?.Write("NET", "INFO", "loading.timeout", new Dictionary<string, object?>
            {
                ["player_id"] = expiry.PlayerId, ["connection_id"] = expiry.ConnectionId,
                ["attached"] = expiry.ConnectionId.HasValue, ["late"] = expiry.Late,
                ["timeout_seconds"] = _runtime.Session.LoadingTimeoutSeconds,
                ["game_time"] = _runtime.Session.HudTime,
            });
            if (expiry.ConnectionId is not long connectionId ||
                !_peers.TryGetValue(connectionId, out var peer) || !peer.IsUsable) continue;
            Detach(connectionId, "loading-timeout");
            peer.DisconnectLater();
        }
    }

    private void KickAfkPeers()
    {
        foreach (var connectionId in _runtime.Session.ConsumeAfkKicks())
        {
            if (!_peers.TryGetValue(connectionId, out var peer) || !peer.IsUsable) continue;
            Detach(connectionId, "afk-kick", AfkPackets.KickDisconnectData);
            // Later, not Now: queued warnings are delivered first, then the
            // stock client shows its "prolonged inactivity" exit popup.
            peer.DisconnectLater(AfkPackets.KickDisconnectData);
        }
    }

    private void StartAuthenticationDeadline(long connectionId, long now)
    {
        var timeoutTicks = checked((long)Math.Ceiling(
            AuthenticationTimeoutSeconds * Stopwatch.Frequency));
        _authenticationDeadlines[connectionId] = checked(now + timeoutTicks);
    }

    private void RejectPendingAuthentication(LeagueEnetPeer peer, string reason)
    {
        RecordAuthenticationOutcome("auth.rejected", reason, peer.ConnectionId);
        Detach(peer.ConnectionId, "auth-" + reason);
        peer.DisconnectNow();
    }

    private void ExpirePendingAuthentications(long now)
    {
        foreach (var (connectionId, deadline) in _authenticationDeadlines.ToArray())
        {
            if (now < deadline || !_peers.TryGetValue(connectionId, out var peer))
                continue;
            RecordAuthenticationOutcome("auth.timeout", "deadline", connectionId);
            Detach(connectionId, "auth-timeout");
            peer.DisconnectNow();
        }
    }

    private int ClampAuthenticationTimeout(int requestedMilliseconds, long now)
    {
        if (_authenticationDeadlines.Count == 0)
            return requestedMilliseconds;
        var nearestDeadline = _authenticationDeadlines.Values.Min();
        var remaining = nearestDeadline - now;
        if (remaining <= 0)
            return 0;
        var untilDeadline = (int)Math.Min(
            int.MaxValue,
            Math.Max(0.0, Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency)));
        return Math.Min(requestedMilliseconds, untilDeadline);
    }

    private void RetirePreviousConnection(long connectionId, ReadOnlySpan<byte> keyCheck)
    {
        var session = _runtime.Session;
        if (session.Participant(connectionId) is not null ||
            !SessionKeyCheck.TryReadPlayerId(keyCheck, _runtime.Cipher, out var playerId)) return;
        if (!session.CanReplaceConnection(connectionId, playerId)) return;
        var participant = session.Participants.FirstOrDefault(seat => seat.PlayerId == playerId);
        if (participant?.ConnectionId is not long previousId) return;

        // A crashed process sends no Exit; ENet can retain it for tens of
        // seconds. A new connection with valid credentials replaces that
        // player's transport, never the hero or another roster seat. This
        // also applies while loading, before the start barrier opens. The
        // session preflight above keeps a later rejection from detaching the
        // old seat.
        _peers.TryGetValue(previousId, out var previousPeer);
        Detach(previousId, "replaced-by-reconnect");
        previousPeer?.DisconnectNow();
        _log?.Write("NET", "INFO", "reconnect.replaced-connection", new Dictionary<string, object?>
        {
            ["connection_id"] = connectionId,
            ["previous_connection_id"] = previousId,
            ["redacted"] = true,
        });
    }

    private void SendReconnectStates()
    {
        foreach (var participant in _runtime.Session.ConsumeReconnects())
        {
            if (participant.ConnectionId is not long connectionId ||
                !_peers.TryGetValue(connectionId, out var peer) || participant.Hero is null) continue;
            foreach (var packet in BuildReconnectState(_runtime, participant)) Send(peer, packet);
            // The returning seat is ready now: the other clients raise OnReconnect from this notice.
            Send(_runtime.Session.BuildReconnectNotice(participant));
            if (_observers.TryGetValue(connectionId, out var observer))
                observer.DeathRecapReady = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var connectionId in _peers.Keys)
            LogDisconnect(connectionId, "network-disposed", null);
        foreach (var peer in _peers.Values
                     .Where(peer => _authenticationDeadlines.ContainsKey(peer.ConnectionId))
                     .ToArray())
        {
            if (peer.IsUsable)
                peer.DisconnectNow();
        }
        _disposed = true;
        _runtime.Dispose();
        foreach (var observer in _observers.Values)
            _runtime.Delivery.RemoveObserver(observer.ObserverId);
        _observers.Clear();
        _peers.Clear();
        _lastReceiveAt.Clear();
        _peerSamples.Clear();
        _authenticationDeadlines.Clear();
        FlushAuthenticationOutcomeSummaries(_monotonicTimestamp(), force: true);
        if (_ownsHost) _host.Dispose();
    }

    private void Send(IEnumerable<SessionOutboundPacket> outputs)
    {
        foreach (var output in outputs)
        {
            if (!_peers.TryGetValue(output.ConnectionId, out var peer))
            {
                _runtime.MovementTrace?.NoteWaypointOutcome(output.Packet, output.ConnectionId,
                    _runtime.Session.Participant(output.ConnectionId)?.PlayerId, "missing-peer-drop");
                continue;
            }
            Send(peer, output.Packet);
        }
    }

    private void RecordAuthenticationOutcome(string eventName, string reason, long connectionId)
    {
        if (_log is null) return;
        var key = (eventName, reason);
        var count = _authenticationOutcomeCounts.GetValueOrDefault(key) + 1;
        _authenticationOutcomeCounts[key] = count;
        if (count != 1) return;
        _log.Write("NET", "INFO", eventName, new Dictionary<string, object?>
        {
            ["connection_id"] = connectionId,
            ["reason"] = reason,
            ["n"] = "channel-zero-metadata-only",
            ["redacted"] = true,
        });
    }

    private void FlushAuthenticationOutcomeSummaries(long now, bool force = false)
    {
        if (_log is null || !force && now - _lastAuthenticationSummary < Stopwatch.Frequency)
            return;
        _lastAuthenticationSummary = now;
        foreach (var ((eventName, reason), count) in _authenticationOutcomeCounts.ToArray())
        {
            if (count > 1)
                _log.Write("NET", "INFO", eventName + ".aggregate",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["count"] = count,
                        ["aggregate"] = true,
                        ["redacted"] = true,
                    });
            _authenticationOutcomeCounts.Remove((eventName, reason));
        }
    }

    private bool LogIncomingPacket(LeagueEnetEvent @event)
    {
        if (_log is null || @event.Channel == 0 || @event.Payload.Length == 0 ||
            !_log.Admit("PKT", "DEBUG")) return false;
        try
        {
            var payload = @event.Payload;
            var plaintext = @event.Channel is 1 or 5 && payload.Length >= LegacyBlowfishCipher.BlockSize
                ? _runtime.Cipher.Decrypt(payload) : payload;
            var decoded = WirePacketDecoder.Decode(plaintext, checked((byte)@event.Channel));
            var fullTrace = Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE") == "1";
            var previewLength = fullTrace ? payload.Length : Math.Min(payload.Length, 32);
            var participant = _runtime.Session.Participant(@event.Peer?.ConnectionId ?? 0);
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dir"] = "<-",
                ["op"] = decoded.Opcode is byte opcode ? $"0x{opcode:X2}" : "0x??",
                ["name"] = WirePacketDecoder.LogName(decoded.Opcode, checked((byte)@event.Channel)),
                ["n"] = decoded.Length,
                ["ch"] = @event.Channel,
                ["connection_id"] = @event.Peer?.ConnectionId,
                ["player_id"] = participant?.PlayerId,
                ["phase"] = participant?.Ready == true ? "live" : "bootstrap",
                ["raw"] = Convert.ToHexString(payload.AsSpan(0, previewLength)).ToLowerInvariant(),
                ["sampled"] = !fullTrace,
                ["truncated"] = previewLength != payload.Length,
                ["redacted"] = false,
                ["partial_replay"] = true,
            };
            foreach (var field in decoded.Fields)
            {
                if (field.Key is "op" or "len" or "name" or "raw") continue;
                properties[field.Key is "n" or "ch" ? "pkt_" + field.Key : field.Key] = field.Value;
            }
            _log.Write("PKT", "DEBUG", "<-", properties);
            return true;
        }
        catch (Exception exception)
        {
            _log.Write("NET", "WARN", "packet.diagnostic-failed",
                new Dictionary<string, object?>
                {
                    ["ch"] = @event.Channel,
                    ["n"] = @event.Payload.Length,
                    ["error"] = exception.GetType().Name,
                });
            return false;
        }
    }

    private void LogIncomingOpcode(LeagueEnetEvent @event)
    {
        if (_log is null || @event.Channel == 0 || @event.Payload.Length == 0)
            return;

        // MatchRuntimeSession applies the same short-packet rule: channel 1
        // and chat channel 5 are Blowfish-encrypted only when at least one
        // complete block is present.  Diagnostics must inspect the plaintext
        // opcode while retaining the original encrypted NET record above.
        var payload = @event.Payload;
        var plaintext = @event.Channel is 1 or 5 && payload.Length >= LegacyBlowfishCipher.BlockSize
            ? _runtime.Cipher.Decrypt(payload)
            : payload;
        if (plaintext.Length == 0)
            return;

        var opcode = plaintext[0];
        if (@event.Channel == 1)
        {
            if (opcode == 0x53 && plaintext.Length == 17)
            {
                _log.Write("NET", "DEBUG", "ignored ch1 opcode",
                    new Dictionary<string, object?>
                    {
                        ["op"] = "0x53",
                        ["name"] = "C2S_FaceDirection",
                    });
                return;
            }

            if (IgnoredCh1Opcodes.TryGetValue(opcode, out var name))
            {
                _log.Write("NET", "DEBUG", "ignored ch1 opcode",
                    new Dictionary<string, object?>
                    {
                        ["op"] = $"0x{opcode:x2}",
                        ["name"] = name,
                    });
                return;
            }

            if (opcode == 0x53 || !KnownCh1Opcodes.Contains(opcode))
            {
                _log.Write("NET", "WARN", $"unhandled ch1 opcode 0x{opcode:X2} ({plaintext.Length} bytes)",
                    new Dictionary<string, object?>
                    {
                        ["op"] = $"0x{opcode:x2}",
                        ["length"] = plaintext.Length,
                        ["prefix"] = Convert.ToHexString(plaintext[..Math.Min(plaintext.Length, 32)]),
                    });
            }
        }
        else if (opcode == 0x53 && plaintext.Length == 17)
        {
            _log.Write("NET", "WARN", "unhandled opcode on wrong channel",
                new Dictionary<string, object?>
                {
                    ["op"] = "0x53",
                    ["ch"] = @event.Channel,
                    ["n"] = plaintext.Length,
                });
        }
    }

    private void Send(LeagueEnetPeer peer, PlaintextPacket packet)
    {
        // The one place every packet to a client passes: a 0xCC block carries only what this client's replication store lacks.
        if (_runtime.Session.Participant(peer.ConnectionId) is { } receiver)
        {
            var processed = receiver.View.Store.Process(packet, receiver.Hero?.NetId ?? 0);
            if (processed is null) return;
            packet = processed;
        }
        var flags = packet.Reliability switch
        {
            LeaguePacketReliability.Reliable => LeagueEnetPacketFlags.Reliable,
            LeaguePacketReliability.Unsequenced => LeagueEnetPacketFlags.Unsequenced,
            _ => LeagueEnetPacketFlags.None,
        };
        var wire = packet.ToWireBytes(_runtime.Cipher);
        if (_log is not null && packet.Channel != 0 && _log.Admit("PKT", "DEBUG"))
        {
            try
            {
                var decoded = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
                var fullTrace = Environment.GetEnvironmentVariable("AMBERLINE_PACKET_TRACE") == "1";
                var previewLength = fullTrace ? packet.Plaintext.Length : Math.Min(packet.Plaintext.Length, 32);
                var participant = _runtime.Session.Participant(peer.ConnectionId);
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["dir"] = "->",
                    ["op"] = decoded.Opcode is byte opcode ? $"0x{opcode:X2}" : "0x??",
                    ["name"] = WirePacketDecoder.LogName(decoded.Opcode, packet.Channel),
                    ["n"] = decoded.Length,
                    ["ch"] = packet.Channel,
                    ["connection_id"] = peer.ConnectionId,
                    ["player_id"] = participant?.PlayerId,
                    ["phase"] = participant?.Ready == true ? "live" : "bootstrap",
                    ["rel"] = (int)packet.Reliability,
                    ["raw"] = Convert.ToHexString(packet.Plaintext.Span[..previewLength]).ToLowerInvariant(),
                    ["sampled"] = !fullTrace,
                    ["truncated"] = previewLength != packet.Plaintext.Length,
                    ["redacted"] = false,
                    ["partial_replay"] = true,
                };
                foreach (var field in decoded.Fields)
                {
                    if (field.Key is "op" or "len" or "name" or "raw") continue;
                    properties[field.Key is "n" or "ch" or "rel" ? "pkt_" + field.Key : field.Key] = field.Value;
                }
                _log.Write("PKT", "DEBUG", "->", properties);
            }
            catch (Exception exception)
            {
                _log.Write("NET", "WARN", "packet.diagnostic-failed",
                    new Dictionary<string, object?>
                    {
                        ["direction"] = "outbound",
                        ["ch"] = packet.Channel,
                        ["n"] = packet.Plaintext.Length,
                        ["error"] = exception.GetType().Name,
                    });
            }
        }
        TryEnqueueObserved(packet, peer.ConnectionId,
            () => peer.TrySend(packet.Channel, wire, flags));
    }

    /// <summary>Records the real queue result; this does not acknowledge native receipt.</summary>
    internal bool TryEnqueueObserved(PlaintextPacket packet, long connectionId,
        Func<bool> enqueue)
    {
        try
        {
            var accepted = enqueue();
            _runtime.MovementTrace?.NoteWaypointOutcome(packet, connectionId,
                _runtime.Session.Participant(connectionId)?.PlayerId,
                accepted ? "enqueued" : "closing-peer-drop");
            return accepted;
        }
        catch
        {
            _runtime.MovementTrace?.NoteWaypointOutcome(packet, connectionId,
                _runtime.Session.Participant(connectionId)?.PlayerId, "enqueue-exception");
            throw;
        }
    }

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MatchRuntimeNetwork));
    }

    private void EnsureObservers()
    {
        foreach (var participant in _runtime.Session.Participants)
        {
            if (participant.ConnectionId is not long connectionId ||
                participant.Hero is null || !_peers.ContainsKey(connectionId))
                continue;
            if (_observers.TryGetValue(connectionId, out var observer))
            {
                observer.Ready = participant.Ready;
                continue;
            }
            AttachObserver(connectionId);
        }
    }

    /// <summary>
    /// Rehydrates state that visibility refreshes intentionally do not own:
    /// current equipment, spell definitions, and cooldown timers.  World
    /// entities, stats, buffs, movement, transforms, and pets are rebuilt by
    /// the ordinary observer flush immediately before this burst.  The state
    /// BuildingBlocks scripts keep on the hero is covered between the two: the
    /// slot names (BBSetSpell), targeting types (BBSetTargetingType), champion,
    /// summoner, item and extra-slot cooldowns, and a character layer the flush
    /// did not push come from here; the seals ride the stats block of the flush
    /// (the cast-bit words, <c>HeroStatsService.Snapshot</c>), which a reset view
    /// sends whole.
    /// </summary>
    internal IReadOnlyList<PlaintextPacket> BuildReconnectState(SessionParticipant participant) =>
        BuildReconnectState(_runtime, participant);

    /// <summary>The same burst without a network shell (the headless match has no ENet peers).</summary>
    internal static IReadOnlyList<PlaintextPacket> BuildReconnectState(MatchRuntime runtime, SessionParticipant participant)
    {
        var hero = participant.Hero
            ?? throw new InvalidOperationException("Reconnect requires an existing hero.");
        var packets = new List<PlaintextPacket>();
        // The roster already exists on this client, including heroes in fog.
        // Score is map-wide state and carries no hidden position or health.
        foreach (var scoredHero in runtime.World.Heroes().Where(unit => unit.NeutralMinionsKilled > 0))
            packets.Add(CombatPackets.HeroNeutralKills(scoredHero.NetId, scoredHero.NeutralMinionsKilled,
                runtime.SyncIds.Next(scoredHero.NetId)));
        foreach (var (slot, index) in hero.Inventory.Select((item, index) => (item, index)))
        {
            if (slot is null)
            {
                packets.Add(ItemsPackets.RemoveItem(hero.NetId, index, 0));
                continue;
            }
            packets.Add(ItemsPackets.SetItem(hero.NetId, index,
                checked((uint)Math.Max(0, slot.ItemId)),
                Math.Clamp(slot.Count, 0, byte.MaxValue),
                Math.Clamp(slot.Charges, 0, byte.MaxValue)));
        }
        // The character layers a script pushed on the hero (BBPushCharacterData). A visible hero got them from the observer flush before this
        // burst (the persistent character-data ledger); a dead one is not part of that hydration, so they come here, from the same ledger and
        // through the same view set, so that neither path repeats the other and the flush's repair pass (which pops any layer a view holds that
        // the ledger does not) never takes back what this burst gave. They go first: a layer that carries spells (OverrideSpells) re-initialises
        // the client's book, and the names, ranks and cooldowns below are then what the hero holds now.
        foreach (var model in runtime.Delivery.CharacterDataOf(hero.NetId))
            if (participant.View.CharacterData.Add((hero.NetId, model.Change)))
                packets.Add(model.Packet);

        // The staged baseline intentionally leaves the detached hero's
        // position/visibility to ordinary replication. Its spell book is
        // stateful, though, so send the current names here rather than
        // replaying champion defaults over a transformed/recast form.
        var champion = runtime.GameData.Champion(hero.ChampionName);
        foreach (var (fallbackName, slot) in champion.SpellNames.Select((name, slot) => (name, slot)))
        {
            var spellName = hero.ActiveSpellNames.Count > slot &&
                !string.IsNullOrWhiteSpace(hero.ActiveSpellNames[slot])
                ? hero.ActiveSpellNames[slot] : fallbackName;
            if (spellName.Length != 0)
                packets.Add(SpellPackets.SetSpellData(hero.NetId,
                    ProtocolEncoding.HashString(spellName), checked((byte)slot)));
        }

        // The fresh client starts with an unlearned book. Rehydrate every
        // champion slot with the authoritative rank and remaining points;
        // this is a state packet, not a rank-up operation.
        for (var slot = 0; slot < hero.SpellRanks.Count; slot++)
            packets.Add(SpellPackets.UpgradeSpellAnswer(hero.NetId,
                checked((byte)slot),
                checked((byte)Math.Clamp(hero.SpellRanks[slot], 0, byte.MaxValue)),
                checked((byte)Math.Clamp(hero.SkillPoints, 0, byte.MaxValue))));

        // What BuildingBlocks scripts changed in the book (BBSetSpell on an inventory or extra slot, BBSetTargetingType): the same 0x35 and 0x1A
        // they sent when they ran, replayed in slot order. The four champion slot names went out above, as SetSpellData. A reconnect rebuilds
        // the book from the ordinary target types, so this stays after both book creation and rank hydration (Janna's Howling Gale charge has
        // switched slot Q to self targeting, which the next press needs to release the stored wave).
        for (var slot = Amberline.Simulation.Spells.SpellSystem.ChampionSlotCount;
             slot < hero.ActiveSpellNames.Count && slot < Amberline.Core.SpellSlotSeals.SlotCount; slot++)
            if (hero.ActiveSpellNames[slot].Length != 0)
                packets.Add(SpellPackets.ChangeSlotSpellName(hero.NetId, checked((byte)slot), hero.ActiveSpellNames[slot], isSummoner: false));
        foreach (var (slot, type) in hero.SpellTargetingOverrides.OrderBy(pair => pair.Key))
            packets.Add(SpellPackets.ChangeSlotSpellType(hero.NetId, checked((byte)slot), checked((byte)type)));
        // BBSetSpellOffsetTarget (0x37): the unit the slot's offset targeter is centred on, when the client already knows it (itself, or a unit it saw).
        foreach (var (slot, offsetTarget) in hero.SpellOffsetTargets.OrderBy(pair => pair.Key))
            if (offsetTarget == hero.NetId || participant.View.Known.Contains(offsetTarget))
                packets.Add(SpellPackets.SpellOffsetTarget(hero.NetId, checked((byte)slot), offsetTarget));

        for (var slot = 0; slot < hero.SummonerSpells.Count; slot++)
        {
            var spellName = hero.SummonerSpells[slot];
            if (string.IsNullOrWhiteSpace(spellName)) continue;
            packets.Add(SpellPackets.ChangeSlotSpellName(hero.NetId,
                checked((byte)slot), spellName, isSummoner: true));
        }

        // Batch 329: every other hero's renamed slots. This client rebuilt their books from each character's defaults (CreateHero), while a
        // client that stayed holds every rename a script made since, in sight or not (0x35 is PolicyBroadcast, batch 325): a champion slot
        // whose name is not the character's, and every extra slot a script named. Reconstruction: retail's reconnect is server-side; this
        // replays the state its broadcast left on every other client.
        foreach (var other in runtime.World.Heroes().Where(unit => unit.NetId != hero.NetId))
        {
            var defaults = runtime.GameData.Champion(other.ChampionName).SpellNames;
            for (var slot = 0; slot < other.ActiveSpellNames.Count && slot < Amberline.Core.SpellSlotSeals.SlotCount; slot++)
            {
                var name = other.ActiveSpellNames[slot];
                if (name.Length != 0 && (slot >= Amberline.Simulation.Spells.SpellSystem.ChampionSlotCount ||
                                         slot >= defaults.Count || name != defaults[slot]))
                    packets.Add(SpellPackets.ChangeSlotSpellName(other.NetId, checked((byte)slot), name, isSummoner: false));
            }
        }

        for (var slot = 0; slot < hero.SpellCooldowns.Count; slot++)
            packets.Add(SpellPackets.SetCooldown(hero.NetId, checked((byte)slot),
                ToFloat(hero.SpellCooldowns[slot])));
        for (var slot = 0; slot < hero.SummonerCooldowns.Count; slot++)
            packets.Add(SpellPackets.SetCooldown(hero.NetId, checked((byte)slot),
                ToFloat(hero.SummonerCooldowns[slot]), isSummoner: true));
        for (var slot = 0; slot < hero.ItemCooldowns.Count; slot++)
            packets.Add(SpellPackets.SetCooldown(hero.NetId, checked((byte)(slot + 4)),
                ToFloat(hero.ItemCooldowns[slot])));
        // The extra slots a script cast a child through with the cooldown check on (BBSpellCast, OverrideCoolDownCheck=false): the server
        // keeps a ready-at time, the client's record wants the time left. After the names above, because naming a slot resets its record.
        foreach (var (slot, readyAt) in hero.ScriptSlotReadyAt.OrderBy(pair => pair.Key))
        {
            var left = readyAt - runtime.Spells.CurrentTime;
            if (slot >= Amberline.Simulation.Spells.SpellSystem.ExtraSpellSlotStart && slot < Amberline.Core.SpellSlotSeals.SlotCount && left > 0.0)
                packets.Add(SpellPackets.SetCooldown(hero.NetId, checked((byte)slot), ToFloat(left)));
        }
        // Item SpellDataInst records are created by the SetItem packets above.
        // Publish dynamic tooltip variables only after inventory and the
        // authoritative spell book have been restored, otherwise a restarted
        // stock client can discard them before the item records exist.
        var itemTooltipEntries = runtime.HeroStats.BuildItemTooltipEntries(hero);
        if (itemTooltipEntries.Count > 0)
        {
            packets.Add(SpellPackets.SpellTooltipVariables(
                itemTooltipEntries.Select(entry => new SpellTooltipEntry(
                    checked((byte)entry.Slot), ToFloat(entry.First),
                    ToFloat(entry.Second), ToFloat(entry.Third))).ToArray(),
                hero.NetId));
        }
        if (!hero.Alive)
        {
            var duration = Math.Max(0.0, hero.RespawnAt - runtime.Combat.CurrentTime);
            packets.Add(CombatPackets.NpcDie(hero.NetId, hero.NetId,
                ToFloat(duration), opcode: 0x61));
        }
        // Batch 472: map 8's live state, which its clients get only when it changes (BehaviorTreeQuestHost.ReconnectState).
        if ((runtime.Dependencies.Simulation as ProductionSimulationDriver)?.Quests is { } dominion)
            packets.AddRange(dominion.ReconnectState(hero.NetId, participant.Team, participant.View.Known));
        packets.AddRange(runtime.Session.BuildReconnectMatchState(participant.ConnectionId!.Value));
        return packets;
    }

    private static float ToFloat(double value)
    {
        if (!double.IsFinite(value)) return 0.0f;
        return ProtocolEncoding.Float32(Math.Clamp(value, 0.0, float.MaxValue), nameof(value));
    }

    private sealed class PeerReplicationTransport : IReplicationTransport
    {
        private readonly MatchRuntimeNetwork _owner;
        private readonly long _connectionId;

        public PeerReplicationTransport(MatchRuntimeNetwork owner, long connectionId)
        {
            _owner = owner;
            if (connectionId <= 0) throw new ArgumentOutOfRangeException(nameof(connectionId));
            _connectionId = connectionId;
        }

        public void Send(PlaintextPacket packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            _owner.EnsureUsable();
            if (!_owner._peers.TryGetValue(_connectionId, out var peer))
            {
                _owner._runtime.MovementTrace?.NoteWaypointOutcome(packet, _connectionId,
                    _owner._runtime.Session.Participant(_connectionId)?.PlayerId, "stale-peer-drop");
                throw new InvalidOperationException("Replication peer is disconnected or stale.");
            }
            _owner.Send(peer, packet);
        }
    }
}
