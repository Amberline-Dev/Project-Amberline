using System.IO;
using System.Runtime.InteropServices;

namespace Amberline.Network.Transport;

public enum LeagueEnetEventType
{
    None = 0,
    Connect = 1,
    Disconnect = 2,
    Receive = 3
}

[Flags]
public enum LeagueEnetPacketFlags : uint
{
    None = 0,
    Reliable = 1u << 0,
    Unsequenced = 1u << 1,
    UnreliableFragment = 1u << 3
}

public readonly record struct LeagueEnetPeerStats(
    uint State,
    uint ChannelCount,
    uint RoundTripTimeMs,
    uint RoundTripTimeVarianceMs,
    uint PacketLoss,
    uint PacketLossVariance,
    uint PacketsSent,
    uint PacketsLost,
    uint IncomingDataTotal,
    uint OutgoingDataTotal,
    nuint WaitingDataBytes,
    uint AddressHost,
    ushort AddressPort);

public sealed class LeagueEnetEvent
{
    internal LeagueEnetEvent(
        LeagueEnetEventType type,
        LeagueEnetPeer? peer,
        int channel,
        uint data,
        byte[] payload,
        LeagueEnetPacketFlags packetFlags)
    {
        Type = type;
        Peer = peer;
        Channel = channel;
        Data = data;
        Payload = payload;
        PacketFlags = packetFlags;
    }

    public LeagueEnetEventType Type { get; }
    public LeagueEnetPeer? Peer { get; }
    public int Channel { get; }
    public uint Data { get; }
    public byte[] Payload { get; }
    public LeagueEnetPacketFlags PacketFlags { get; }
    public bool HasPayload => Payload.Length != 0;
}

public sealed class LeagueEnetHost : IDisposable
{
    public const int ProductionMaximumPacketSize = 64 * 1024;
    public const int ProductionMaximumWaitingData = 256 * 1024;

    private readonly Dictionary<IntPtr, LeagueEnetPeer> _peers = new();
    private readonly Dictionary<IntPtr, LeagueEnetPeer> _retiredPeers = new();
    private readonly int _ownerThreadId;
    private readonly nuint _maximumPacketSize;
    private readonly nuint _maximumWaitingData;
    private IntPtr _handle;
    private bool _disposed;

    public LeagueEnetHost(
        ushort port = 0,
        int peerLimit = 2,
        int channelLimit = 255,
        string? bindHost = null,
        bool enableCrc32 = false,
        uint incomingBandwidth = 0,
        uint outgoingBandwidth = 0,
        nuint maximumPacketSize = 0,
        nuint maximumWaitingData = 0)
    {
        if (peerLimit < 1 || peerLimit > 0xFFF)
            throw new ArgumentOutOfRangeException(nameof(peerLimit));
        if (channelLimit < 1 || channelLimit > 255)
            throw new ArgumentOutOfRangeException(nameof(channelLimit));
        if (maximumPacketSize != 0 && maximumPacketSize > (nuint)int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPacketSize),
                "The managed transport cannot copy packets larger than Int32.MaxValue.");
        _maximumPacketSize = maximumPacketSize;
        _maximumWaitingData = maximumWaitingData;
        LeagueEnetNative.EnsureLoaded();
        var initResult = LeagueEnetNative.Initialize();
        if (initResult < 0)
            throw LeagueEnetNative.Error("ENet initialization failed");

        try
        {
            _handle = LeagueEnetNative.HostCreate(
                bindHost,
                port,
                (nuint)peerLimit,
                (nuint)channelLimit,
                incomingBandwidth,
                outgoingBandwidth,
                enableCrc32 ? 1 : 0,
                maximumPacketSize,
                maximumWaitingData);
            if (_handle == IntPtr.Zero)
                throw LeagueEnetNative.Error("ENet host creation failed");
        }
        catch
        {
            LeagueEnetNative.Deinitialize();
            throw;
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    ~LeagueEnetHost()
    {
        ReleaseNative();
    }

    public ushort Port
    {
        get
        {
            EnsureOwner();
            return LeagueEnetNative.HostPort(_handle);
        }
    }

    /// <summary>Configured native packet cap; zero keeps ENet's generic default.</summary>
    public nuint MaximumPacketSize => _maximumPacketSize;

    /// <summary>Configured native waiting-data cap; zero keeps ENet's generic default.</summary>
    public nuint MaximumWaitingData => _maximumWaitingData;

    public LeagueEnetPeer Connect(
        string remoteHost,
        ushort port,
        int channelCount = 2,
        uint data = 0)
    {
        EnsureOwner();
        if (remoteHost is null)
            throw new ArgumentNullException(nameof(remoteHost));
        if (channelCount < 1 || channelCount > 255)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        var result = LeagueEnetNative.HostConnect(
            _handle,
            remoteHost,
            port,
            (nuint)channelCount,
            data,
            out var peerHandle);
        if (result < 0 || peerHandle == IntPtr.Zero)
            throw LeagueEnetNative.Error("ENet peer connection failed");
        return GetPeer(peerHandle);
    }

    public LeagueEnetEvent Service(int timeoutMilliseconds)
    {
        EnsureOwner();
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        var result = LeagueEnetNative.HostService(
            _handle,
            out var nativeEvent,
            checked((uint)timeoutMilliseconds));
        return ReadEvent(result, ref nativeEvent);
    }

    public LeagueEnetEvent CheckEvents()
    {
        EnsureOwner();
        var result = LeagueEnetNative.HostCheckEvents(_handle, out var nativeEvent);
        return ReadEvent(result, ref nativeEvent);
    }

    public void Flush()
    {
        EnsureOwner();
        LeagueEnetNative.HostFlush(_handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;
        EnsureOwner();
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    internal bool TrySend(
        LeagueEnetPeer peer,
        byte channel,
        byte[] payload,
        LeagueEnetPacketFlags flags)
    {
        EnsureOwner();
        EnsurePeer(peer);
        if (payload is null)
            throw new ArgumentNullException(nameof(payload));
        if (_maximumPacketSize != 0 && (nuint)payload.Length > _maximumPacketSize)
            throw new InvalidDataException($"ENet payload exceeds the configured {_maximumPacketSize}-byte limit");

        var result = LeagueEnetNative.PeerSend(
            peer.Identity,
            channel,
            payload,
            (nuint)payload.Length,
            flags);
        if (result < 0)
        {
            // Native receive/flush can enter ACKNOWLEDGING_DISCONNECT or
            // ZOMBIE before the queued disconnect event retires this wrapper.
            // Only an already-closing peer may discard a send; bad channels
            // and allocation failures on a connected peer remain errors.
            // ENET_PEER_STATE_CONNECTED is 5 in the bundled enet.h.
            if (LeagueEnetNative.PeerStats(peer.Identity, out var stats) == 0 &&
                stats.State is 0 or 6 or 7 or 8 or 9)
                return false;
            throw LeagueEnetNative.Error("ENet peer send failed");
        }
        return true;
    }

    internal void Disconnect(LeagueEnetPeer peer, uint data, LeagueEnetDisconnectMode mode)
    {
        EnsureOwner();
        EnsurePeer(peer);
        switch (mode)
        {
            case LeagueEnetDisconnectMode.Graceful:
                LeagueEnetNative.PeerDisconnect(peer.Identity, data);
                peer.Retire();
                break;
            case LeagueEnetDisconnectMode.Immediate:
                LeagueEnetNative.PeerDisconnectNow(peer.Identity, data);
                RetirePeer(peer);
                break;
            case LeagueEnetDisconnectMode.Later:
                LeagueEnetNative.PeerDisconnectLater(peer.Identity, data);
                peer.Retire();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    internal void Reset(LeagueEnetPeer peer)
    {
        EnsureOwner();
        EnsurePeer(peer);
        LeagueEnetNative.PeerReset(peer.Identity);
        RetirePeer(peer);
    }

    internal void Ping(LeagueEnetPeer peer)
    {
        EnsureOwner();
        EnsurePeer(peer);
        LeagueEnetNative.PeerPing(peer.Identity);
    }

    internal LeagueEnetPeerStats Stats(LeagueEnetPeer peer)
    {
        EnsureOwner();
        EnsurePeer(peer);
        if (LeagueEnetNative.PeerStats(peer.Identity, out var stats) < 0)
            throw LeagueEnetNative.Error("ENet peer statistics failed");
        return new LeagueEnetPeerStats(
            stats.State,
            stats.ChannelCount,
            stats.RoundTripTimeMs,
            stats.RoundTripTimeVarianceMs,
            stats.PacketLoss,
            stats.PacketLossVariance,
            stats.PacketsSent,
            stats.PacketsLost,
            stats.IncomingDataTotal,
            stats.OutgoingDataTotal,
            stats.WaitingDataBytes,
            stats.AddressHost,
            stats.AddressPort);
    }

    private LeagueEnetPeer GetPeer(IntPtr peer)
    {
        if (_peers.TryGetValue(peer, out var existing))
        {
            if (existing.IsUsable)
                return existing;
            RetirePeer(existing);
        }

        _retiredPeers.Remove(peer);
        var result = new LeagueEnetPeer(this, peer);
        _peers.Add(peer, result);
        return result;
    }

    private LeagueEnetPeer GetPeerForEvent(IntPtr peer, LeagueEnetEventType type)
    {
        if (type == LeagueEnetEventType.Connect)
            return GetPeer(peer);

        if (_peers.TryGetValue(peer, out var active))
        {
            if (type == LeagueEnetEventType.Disconnect)
                RetirePeer(active);
            return active;
        }

        return _retiredPeers.TryGetValue(peer, out var retired)
            ? retired
            : GetPeer(peer);
    }

    private void RetirePeer(LeagueEnetPeer peer)
    {
        _peers.Remove(peer.Identity);
        peer.Retire();
        _retiredPeers[peer.Identity] = peer;
    }

    private void EnsurePeer(LeagueEnetPeer peer)
    {
        if (!peer.IsUsable ||
            !_peers.TryGetValue(peer.Identity, out var active) ||
            !ReferenceEquals(active, peer))
        {
            throw new InvalidOperationException("The ENet peer is disconnected or belongs to an older connection");
        }
    }

    private LeagueEnetEvent ReadEvent(int result, ref LeagueEnetNative.NativeEvent nativeEvent)
    {
        if (result < 0)
            throw LeagueEnetNative.Error("ENet service failed");

        try
        {
            var peer = nativeEvent.Peer == IntPtr.Zero
                ? null
                : GetPeerForEvent(nativeEvent.Peer, (LeagueEnetEventType)nativeEvent.Type);
            var payload = Array.Empty<byte>();
            var packetFlags = LeagueEnetPacketFlags.None;
            if (nativeEvent.Packet != IntPtr.Zero)
            {
                packetFlags = LeagueEnetNative.PacketFlags(nativeEvent.Packet);
                var length = LeagueEnetNative.PacketLength(nativeEvent.Packet);
                if (length > int.MaxValue ||
                    _maximumPacketSize != 0 && length > _maximumPacketSize)
                {
                    // The packet is still disposed by the finally block. Do
                    // not allocate or copy an oversize native packet; retire
                    // the generation so a malformed fragment cannot keep a
                    // production peer alive after the managed guard.
                    if (peer is not null && peer.IsUsable)
                    {
                        LeagueEnetNative.PeerDisconnectNow(peer.Identity, 0);
                        RetirePeer(peer);
                    }
                }
                else
                {
                    payload = new byte[(int)length];
                    if (payload.Length != 0)
                    {
                        if (LeagueEnetNative.PacketCopy(nativeEvent.Packet, payload, (nuint)payload.Length) < 0)
                            throw LeagueEnetNative.Error("ENet packet copy failed");
                    }
                }
            }

            return new LeagueEnetEvent(
                (LeagueEnetEventType)nativeEvent.Type,
                peer,
                nativeEvent.Channel,
                nativeEvent.Data,
                payload,
                packetFlags);
        }
        finally
        {
            LeagueEnetNative.EventDispose(ref nativeEvent);
        }
    }

    private void EnsureOwner()
    {
        if (_disposed || _handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(LeagueEnetHost));
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("A League ENet host must be used by its creating thread");
    }

    private void ReleaseNative()
    {
        if (_handle == IntPtr.Zero)
            return;
        LeagueEnetNative.HostDestroy(_handle);
        LeagueEnetNative.Deinitialize();
        _handle = IntPtr.Zero;
        _disposed = true;
        foreach (var peer in _peers.Values)
            peer.Retire();
        _peers.Clear();
        _retiredPeers.Clear();
    }
}

public enum LeagueEnetDisconnectMode
{
    Graceful,
    Immediate,
    Later
}

public sealed class LeagueEnetPeer
{
    private static long _nextConnectionId;
    private readonly LeagueEnetHost _host;
    private readonly IntPtr _handle;
    private readonly long _connectionId = Interlocked.Increment(ref _nextConnectionId);

    internal LeagueEnetPeer(LeagueEnetHost host, IntPtr handle)
    {
        _host = host;
        _handle = handle;
    }

    public IntPtr Identity => _handle;

    public long ConnectionId => _connectionId;

    public void Send(
        byte channel,
        byte[] payload,
        LeagueEnetPacketFlags flags = LeagueEnetPacketFlags.None)
        => _host.TrySend(this, channel, payload, flags);

    /// <summary>Reports native enqueue acceptance, not delivery to the remote client.</summary>
    internal bool TrySend(byte channel, byte[] payload,
        LeagueEnetPacketFlags flags = LeagueEnetPacketFlags.None) =>
        _host.TrySend(this, channel, payload, flags);

    public void Disconnect(uint data = 0)
        => _host.Disconnect(this, data, LeagueEnetDisconnectMode.Graceful);

    public void DisconnectNow(uint data = 0)
        => _host.Disconnect(this, data, LeagueEnetDisconnectMode.Immediate);

    public void DisconnectLater(uint data = 0)
        => _host.Disconnect(this, data, LeagueEnetDisconnectMode.Later);

    public void Reset() => _host.Reset(this);

    public void Ping() => _host.Ping(this);

    public LeagueEnetPeerStats Stats => _host.Stats(this);

    internal bool IsUsable { get; private set; } = true;

    internal void Retire() => IsUsable = false;
}

internal static class LeagueEnetNative
{
    private const string Library = "amberline_enet";
    private static bool _resolverInstalled;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEvent
    {
        public int Type;
        public int Channel;
        public uint Data;
        public IntPtr Peer;
        public IntPtr Packet;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePeerStats
    {
        public uint State;
        public uint ChannelCount;
        public uint RoundTripTimeMs;
        public uint RoundTripTimeVarianceMs;
        public uint PacketLoss;
        public uint PacketLossVariance;
        public uint PacketsSent;
        public uint PacketsLost;
        public uint IncomingDataTotal;
        public uint OutgoingDataTotal;
        public nuint WaitingDataBytes;
        public uint AddressHost;
        public ushort AddressPort;
        public ushort Reserved;
    }

    internal static void EnsureLoaded()
    {
        if (_resolverInstalled)
            return;
        NativeLibrary.SetDllImportResolver(
            typeof(LeagueEnetNative).Assembly,
            static (name, _, _) =>
            {
                if (!string.Equals(name, Library, StringComparison.Ordinal))
                    return IntPtr.Zero;
                foreach (var path in CandidatePaths())
                {
                    if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                        return handle;
                }
                return IntPtr.Zero;
            });
        _resolverInstalled = true;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var configured = Environment.GetEnvironmentVariable("AMBERLINE_ENET_NATIVE");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        // native/enet/build.ps1 builds amberline_enet.dll, native/enet/build.sh libamberline_enet.so (batch 609).
        var file = OperatingSystem.IsWindows() ? "amberline_enet.dll" : "libamberline_enet.so";
        yield return Path.Combine(AppContext.BaseDirectory, file);
        var current = Directory.GetCurrentDirectory();
        yield return Path.Combine(current, "native", "enet", "build", file);
        yield return Path.Combine(current, "emu", "native", "enet", "build", file);
    }

    internal static Exception Error(string fallback)
    {
        var pointer = LastError();
        var message = pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
        return new InvalidOperationException(message is null or "" ? fallback : $"{fallback}: {message}");
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int amberline_enet_initialize();

    internal static int Initialize() => amberline_enet_initialize();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_deinitialize();

    internal static void Deinitialize() => amberline_enet_deinitialize();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr amberline_enet_last_error();

    private static IntPtr LastError() => amberline_enet_last_error();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)]
    internal static extern IntPtr amberline_enet_host_create(
        [MarshalAs(UnmanagedType.LPStr)] string? bindHost,
        ushort port,
        nuint peerLimit,
        nuint channelLimit,
        uint incomingBandwidth,
        uint outgoingBandwidth,
        int enableCrc32);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)]
    internal static extern IntPtr amberline_enet_host_create_bounded(
        [MarshalAs(UnmanagedType.LPStr)] string? bindHost,
        ushort port,
        nuint peerLimit,
        nuint channelLimit,
        uint incomingBandwidth,
        uint outgoingBandwidth,
        int enableCrc32,
        nuint maximumPacketSize,
        nuint maximumWaitingData);

    internal static IntPtr HostCreate(
        string? bindHost,
        ushort port,
        nuint peerLimit,
        nuint channelLimit,
        uint incomingBandwidth,
        uint outgoingBandwidth,
        int enableCrc32,
        nuint maximumPacketSize,
        nuint maximumWaitingData)
    {
        if (maximumPacketSize == 0 && maximumWaitingData == 0)
            return amberline_enet_host_create(bindHost, port, peerLimit, channelLimit,
                incomingBandwidth, outgoingBandwidth, enableCrc32);
        try
        {
            return amberline_enet_host_create_bounded(bindHost, port, peerLimit, channelLimit,
                incomingBandwidth, outgoingBandwidth, enableCrc32, maximumPacketSize,
                maximumWaitingData);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new InvalidOperationException(
                "The loaded ENet bridge does not provide bounded host creation.", exception);
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_host_destroy(IntPtr host);

    internal static void HostDestroy(IntPtr host) => amberline_enet_host_destroy(host);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ushort amberline_enet_host_port(IntPtr host);

    internal static ushort HostPort(IntPtr host) => amberline_enet_host_port(host);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)]
    internal static extern int amberline_enet_host_connect(
        IntPtr host,
        [MarshalAs(UnmanagedType.LPStr)] string remoteHost,
        ushort port,
        nuint channelCount,
        uint data,
        out IntPtr peer);

    internal static int HostConnect(
        IntPtr host,
        string remoteHost,
        ushort port,
        nuint channelCount,
        uint data,
        out IntPtr peer)
        => amberline_enet_host_connect(host, remoteHost, port, channelCount, data, out peer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int amberline_enet_host_service(IntPtr host, out NativeEvent @event, uint timeoutMs);

    internal static int HostService(IntPtr host, out NativeEvent @event, uint timeoutMs)
        => amberline_enet_host_service(host, out @event, timeoutMs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int amberline_enet_host_check_events(IntPtr host, out NativeEvent @event);

    internal static int HostCheckEvents(IntPtr host, out NativeEvent @event)
        => amberline_enet_host_check_events(host, out @event);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_host_flush(IntPtr host);

    internal static void HostFlush(IntPtr host) => amberline_enet_host_flush(host);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int amberline_enet_peer_send(
        IntPtr peer,
        byte channel,
        [In] byte[] data,
        nuint length,
        LeagueEnetPacketFlags flags);

    internal static int PeerSend(
        IntPtr peer,
        byte channel,
        byte[] data,
        nuint length,
        LeagueEnetPacketFlags flags)
        => amberline_enet_peer_send(peer, channel, data, length, flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_peer_disconnect(IntPtr peer, uint data);

    internal static void PeerDisconnect(IntPtr peer, uint data) => amberline_enet_peer_disconnect(peer, data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_peer_disconnect_now(IntPtr peer, uint data);

    internal static void PeerDisconnectNow(IntPtr peer, uint data) => amberline_enet_peer_disconnect_now(peer, data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_peer_disconnect_later(IntPtr peer, uint data);

    internal static void PeerDisconnectLater(IntPtr peer, uint data) => amberline_enet_peer_disconnect_later(peer, data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_peer_reset(IntPtr peer);

    internal static void PeerReset(IntPtr peer) => amberline_enet_peer_reset(peer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_peer_ping(IntPtr peer);

    internal static void PeerPing(IntPtr peer) => amberline_enet_peer_ping(peer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int amberline_enet_peer_stats(IntPtr peer, out NativePeerStats stats);

    internal static int PeerStats(IntPtr peer, out NativePeerStats stats)
        => amberline_enet_peer_stats(peer, out stats);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint amberline_enet_packet_length(IntPtr packet);

    internal static nuint PacketLength(IntPtr packet) => amberline_enet_packet_length(packet);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint amberline_enet_packet_flags(IntPtr packet);

    internal static LeagueEnetPacketFlags PacketFlags(IntPtr packet)
        => (LeagueEnetPacketFlags)amberline_enet_packet_flags(packet);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int amberline_enet_packet_copy(
        IntPtr packet,
        [Out] byte[] destination,
        nuint destinationLength);

    internal static int PacketCopy(IntPtr packet, byte[] destination, nuint destinationLength)
        => amberline_enet_packet_copy(packet, destination, destinationLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void amberline_enet_event_dispose(ref NativeEvent @event);

    internal static void EventDispose(ref NativeEvent @event) => amberline_enet_event_dispose(ref @event);
}
