using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Amberline.AirCompanion;

public sealed class MaestroServer : IAsyncDisposable
{
    private const int MaxPeers = 8;
    private const int MaxLifecycleLogEntries = 32;
    private static readonly TimeSpan HeartbeatSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MessageSendTimeout = TimeSpan.FromSeconds(5);
    private readonly TcpListener _listener;
    private readonly Func<MaestroCreateRequest, Process> _launch;
    private readonly TimeSpan _heartbeatInterval;
    private readonly object _gate = new();
    private readonly List<Peer> _peers = [];
    private readonly List<Task> _peerTasks = [];
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _acceptTask;
    private Task? _heartbeatTask;
    private Process? _ownedGame;
    private Peer? _airPeer;
    private Peer? _nativePeer;
    private long _gameCycle;
    private int _lifecycleLogEntries;
    private bool _terminalNotificationSent;
    private bool _nativeConnected;

    public MaestroServer(int port, string stockExecutable, TimeSpan? heartbeatInterval = null)
        : this(port, new StockClientLauncher(stockExecutable, port).Launch, heartbeatInterval) { }

    public MaestroServer(int port, Func<MaestroCreateRequest, Process> launch, TimeSpan? heartbeatInterval = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _listener = new TcpListener(IPAddress.Loopback, port);
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(25);
        if (_heartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
    }

    public event Action? PeerConnected;
    public event Action? NativeGameConnected;
    public event Action<MaestroMessage>? MessageReceived;
    public bool IsRunning => _stop is not null;
    public int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool HasOwnedGame
    {
        get { lock (_gate) return _ownedGame is { HasExited: false }; }
    }
    public int? OwnedGamePid
    {
        get { lock (_gate) return _ownedGame is { HasExited: false } game ? game.Id : null; }
    }
    /// <summary>The owned native game's pid (null once it exited) and whether its terminal notification (stop,
    /// crash or abandon) already reached AIR, read as one snapshot.</summary>
    public (int? Pid, bool TerminalNotified) OwnedGameState
    {
        get
        {
            lock (_gate)
            {
                int? pid = _ownedGame is { HasExited: false } game ? game.Id : null;
                return (pid, _terminalNotificationSent);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_stop is not null) throw new InvalidOperationException("The Maestro server is already running.");
        _listener.Start();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stop = stop;
        _acceptTask = AcceptLoopAsync(stop.Token);
        _heartbeatTask = HeartbeatLoopAsync(stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(bool stopOwnedGame = true)
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is null) return;
        stop.Cancel();
        _listener.Stop();
        try { if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        try { if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        Peer[] peers;
        Task[] peerTasks;
        lock (_gate)
        {
            peers = _peers.ToArray();
            peerTasks = _peerTasks.ToArray();
        }
        foreach (var peer in peers) await peer.DisposeAsync().ConfigureAwait(false);
        try { await Task.WhenAll(peerTasks).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        lock (_gate) _peerTasks.Clear();
        if (stopOwnedGame) await StopOwnedGameAsync().ConfigureAwait(false);
        stop.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var peer = new Peer(client);
                var peerLimitReached = false;
                lock (_gate)
                {
                    peerLimitReached = _peers.Count >= MaxPeers;
                    if (!peerLimitReached) _peers.Add(peer);
                }
                if (peerLimitReached)
                {
                    await peer.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                PeerConnected?.Invoke();
                var peerTask = RunPeerAsync(peer, cancellationToken);
                lock (_gate)
                {
                    _peerTasks.RemoveAll(task => task.IsCompleted);
                    _peerTasks.Add(peerTask);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunPeerAsync(Peer peer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await MaestroProtocol.ReadAsync(peer.Stream, cancellationToken).ConfigureAwait(false);
                if (message is null) break;
                MessageReceived?.Invoke(message);
                await HandleMessageAsync(peer, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException) { }
        catch (EndOfStreamException) { }
        catch (FormatException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                _peers.Remove(peer);
                if (ReferenceEquals(_airPeer, peer)) _airPeer = null;
                if (ReferenceEquals(_nativePeer, peer)) _nativePeer = null;
            }
            await peer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(Peer sender, MaestroMessage message, CancellationToken cancellationToken)
    {
        var type = message.Type > (int)MaestroMessageType.Invalid ? MaestroMessageType.Invalid : (MaestroMessageType)message.Type;
        switch (type)
        {
            case MaestroMessageType.GameClientCreate:
                if (!ClaimAirPeer(sender)) { LogLifecycle(sender, type, false); break; }
                await CreateGameAsync(sender, message.PayloadText, cancellationToken).ConfigureAwait(false);
                break;
            case MaestroMessageType.Close:
                if (!IsAirPeer(sender)) { LogLifecycle(sender, type, false); break; }
                await StopOwnedGameAsync().ConfigureAwait(false);
                break;
            case MaestroMessageType.Heartbeat:
                if (!ClaimAirPeer(sender)) { LogLifecycle(sender, type, false); break; }
                await SendBoundedAsync(sender, MaestroMessage.Text(MaestroMessageType.Heartbeat), cancellationToken,
                    HeartbeatSendTimeout).ConfigureAwait(false);
                break;
            case MaestroMessageType.ChatMessageToGame:
                if (!IsAirPeer(sender)) { LogLifecycle(sender, type, false); break; }
                await BroadcastAsync(message, sender, cancellationToken).ConfigureAwait(false);
                break;
            case MaestroMessageType.ChatMessageFromGame:
                if (!TryClaimNativePeer(sender)) { LogLifecycle(sender, type, false); break; }
                await BroadcastAsync(message, sender, cancellationToken).ConfigureAwait(false);
                break;
            case MaestroMessageType.GameClientStopped:
            case MaestroMessageType.GameClientCrashed:
            case MaestroMessageType.GameClientAbandoned:
            case MaestroMessageType.GameClientLaunched:
            case MaestroMessageType.GameClientConnectedToServer:
            case MaestroMessageType.GameClientVersionMismatch:
                await ForwardNativeLifecycleAsync(sender, type, message, cancellationToken).ConfigureAwait(false);
                break;
            case MaestroMessageType.Reply:
            case MaestroMessageType.LauncherClient:
                await BroadcastAsync(message, sender, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("Invalid Maestro message type.");
        }
    }

    private async Task ForwardNativeLifecycleAsync(Peer sender, MaestroMessageType type,
        MaestroMessage message, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accepted = type switch
            {
                MaestroMessageType.GameClientStopped or MaestroMessageType.GameClientCrashed or
                    MaestroMessageType.GameClientAbandoned => TryClaimTerminalNotification(sender),
                MaestroMessageType.GameClientConnectedToServer => TryClaimNativeConnected(sender),
                _ => TryClaimNativePeer(sender),
            };
            LogLifecycle(sender, type, accepted);
            // Create already emitted the launch transition. Native acknowledgements,
            // including duplicates, must not restart the AIR lifecycle.
            if (!accepted || type == MaestroMessageType.GameClientLaunched) return;
            if (type == MaestroMessageType.GameClientConnectedToServer) NativeGameConnected?.Invoke();
            await BroadcastAsync(message, sender, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task CreateGameAsync(Peer requester, string payload, CancellationToken cancellationToken)
    {
        var request = MaestroCreateRequest.Parse(payload);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? previous;
            lock (_gate) previous = _ownedGame;
            if (previous is { HasExited: false })
            {
                Console.Error.WriteLine("maestro.launch-failed: game-already-owned");
                await SendBoundedAsync(requester, MaestroMessage.Text(MaestroMessageType.GameClientCrashed), cancellationToken,
                    MessageSendTimeout).ConfigureAwait(false);
                return;
            }
            // The process-exit callback may still be waiting for this semaphore.
            // Finish its terminal transition before replacing the owned process.
            if (previous is not null) await FinishOwnedGameAsync(previous).ConfigureAwait(false);

            lock (_gate)
            {
                _gameCycle++;
                _nativePeer = null;
                _lifecycleLogEntries = 0;
            }

            Process process;
            try { process = _launch(request); }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"maestro.launch-failed: {exception.GetType().Name}");
                await SendBoundedAsync(requester, MaestroMessage.Text(MaestroMessageType.GameClientCrashed), cancellationToken,
                    MessageSendTimeout).ConfigureAwait(false);
                return;
            }

            lock (_gate)
            {
                _ownedGame = process;
                _terminalNotificationSent = false;
                _nativeConnected = false;
            }
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => _ = OnOwnedGameExitedAsync(process);
                if (process.HasExited)
                {
                    _ = OnOwnedGameExitedAsync(process);
                    return;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"maestro.launch-failed: {exception.GetType().Name}");
                lock (_gate)
                {
                    if (ReferenceEquals(_ownedGame, process))
                    {
                        _ownedGame = null;
                        _nativePeer = null;
                        _nativeConnected = false;
                    }
                }
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
                await SendBoundedAsync(requester, MaestroMessage.Text(MaestroMessageType.GameClientCrashed), cancellationToken,
                    MessageSendTimeout).ConfigureAwait(false);
                return;
            }
            LogLifecycle(requester, MaestroMessageType.GameClientCreate, true);
            // NOT RETAIL: retail AIR gets GameClientLaunched from the native game 2-4 s after
            // the process starts (that one is swallowed above); this synthetic broadcast fires
            // right after Process.Start, so AIR's third hide-on-launch races its own deferred
            // minimize. AirWindowTransition.Observe re-applies the hide (see its Hide rule).
            await BroadcastAsync(MaestroMessage.Text(MaestroMessageType.GameClientLaunched), null, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task OnOwnedGameExitedAsync(Process process)
    {
        // Serialize the final notification with the next Create: a slow old
        // terminal write must finish before the next game's launch arrives.
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await FinishOwnedGameAsync(process).ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    // Caller holds _lifecycleLock, including when Create wins the exit-callback race.
    private async Task FinishOwnedGameAsync(Process process)
    {
        try
        {
            var type = MaestroMessageType.GameClientStopped;
            try { if (process.ExitCode != 0) type = MaestroMessageType.GameClientCrashed; }
            catch (InvalidOperationException) { type = MaestroMessageType.GameClientCrashed; }
            bool suppressTerminalNotification;
            long cycle;
            lock (_gate)
            {
                if (!ReferenceEquals(_ownedGame, process)) return;
                cycle = _gameCycle;
                _ownedGame = null;
                _nativePeer = null;
                _nativeConnected = false;
                suppressTerminalNotification = _terminalNotificationSent;
                _terminalNotificationSent = true;
            }
            if (!suppressTerminalNotification)
                await BroadcastAsync(MaestroMessage.Text(type), null, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"maestro.lifecycle cycle={cycle} pid={SafePid(process)} role=process type={type} accepted=true");
        }
        finally
        {
            process.Dispose();
        }
    }

    private bool TryClaimTerminalNotification(Peer sender)
    {
        lock (_gate)
        {
            if (!TryClaimNativePeer(sender)) return false;
            _terminalNotificationSent = true;
            return true;
        }
    }

    private bool ClaimAirPeer(Peer peer)
    {
        lock (_gate)
        {
            if (_airPeer is null)
            {
                if (peer.Role == PeerRole.Native) return false;
                _airPeer = peer;
                peer.Role = PeerRole.Air;
            }
            return ReferenceEquals(_airPeer, peer);
        }
    }

    private bool IsAirPeer(Peer peer)
    {
        lock (_gate) return ReferenceEquals(_airPeer, peer);
    }

    private bool TryClaimNativePeer(Peer peer)
    {
        lock (_gate)
        {
            if (_ownedGame is not { HasExited: false } || _terminalNotificationSent) return false;
            if (_nativePeer is null)
            {
                if (peer.Role != PeerRole.Unknown || ReferenceEquals(_airPeer, peer)) return false;
                _nativePeer = peer;
                peer.Role = PeerRole.Native;
                peer.NativeCycle = _gameCycle;
            }
            return ReferenceEquals(_nativePeer, peer) && peer.NativeCycle == _gameCycle;
        }
    }

    private void LogLifecycle(Peer peer, MaestroMessageType type, bool accepted)
    {
        long cycle;
        int pid;
        PeerRole role;
        lock (_gate)
        {
            if (_lifecycleLogEntries >= MaxLifecycleLogEntries) return;
            _lifecycleLogEntries++;
            cycle = _gameCycle;
            pid = SafePid(_ownedGame);
            role = peer.Role;
        }
        Console.WriteLine($"maestro.lifecycle cycle={cycle} pid={pid} role={role} type={type} accepted={accepted}");
    }

    private static int SafePid(Process? process)
    {
        try { return process?.Id ?? 0; }
        catch (InvalidOperationException) { return 0; }
    }

    private bool TryClaimNativeConnected(Peer sender)
    {
        lock (_gate)
        {
            if (!TryClaimNativePeer(sender) || _nativeConnected) return false;
            _nativeConnected = true;
            return true;
        }
    }

    private async Task StopOwnedGameAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_gate) process = _ownedGame;
            if (process is null) return;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task BroadcastAsync(MaestroMessage message, Peer? except, CancellationToken cancellationToken,
        TimeSpan? sendTimeout = null)
    {
        var timeout = sendTimeout ?? MessageSendTimeout;
        Peer[] peers;
        lock (_gate) peers = _peers.Where(peer => !ReferenceEquals(peer, except)).ToArray();
        foreach (var peer in peers)
        {
            try
            {
                using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCancellation.CancelAfter(timeout);
                await peer.SendAsync(message, sendCancellation.Token).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task SendBoundedAsync(Peer peer, MaestroMessage message, CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendCancellation.CancelAfter(timeout);
        await peer.SendAsync(message, sendCancellation.Token).ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await BroadcastAsync(MaestroMessage.Text(MaestroMessageType.Heartbeat), null, cancellationToken, HeartbeatSendTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private enum PeerRole { Unknown, Air, Native }

    private sealed class Peer(TcpClient client) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _disposed;
        public NetworkStream Stream { get; } = client.GetStream();
        public PeerRole Role { get; set; }
        public long NativeCycle { get; set; }

        public async Task SendAsync(MaestroMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await MaestroProtocol.WriteAsync(Stream, message, cancellationToken).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            Stream.Dispose();
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
