using System.Buffers.Binary;
using Amberline.Core;

namespace Amberline.Network.Protocol;

/// <summary>
/// The patch-126 client exposes only the two map-ping categories in this
/// packet family.  The modern ping wheel is a different protocol.
/// </summary>
public enum MapPingCategory : byte
{
    Offense = 0,
    Danger = 1,
}

public readonly record struct MapPingRequest(
    uint ActorNetId,
    Position3 Position,
    uint TargetNetId,
    MapPingCategory Category);

public readonly record struct MapPingNotification(
    uint SenderNetId,
    uint SourceNetId,
    Position3 Position,
    uint TargetNetId,
    MapPingCategory Category,
    bool PlayAudio,
    bool ShowChat,
    bool PingThrottled);

/// <summary>
/// Typed patch-126 map-ping wire builders and bounded decoders.
///
/// The native structs have a five-byte packet header.  The patch-126 client
/// leaves the C2S header word zero; the authenticated session supplies the
/// actor.  S2C carries the source in its own four-byte field.  Cipher padding
/// is accepted only at the exact legacy block boundary and only as zeroes.
/// </summary>
public static class PingPackets
{
    public const byte ClientOpcode = 0x5A;
    public const byte ServerOpcode = 0x46;
    public const byte Channel = LeagueProtocolConstants.CombatChannel;
    public const int ClientLength = 22;
    public const int ServerLength = 26;
    public const int PingBurstCapacity = 5;
    public const double PingRefillSeconds = 3.0;

    public static PlaintextPacket MapPingRequest(
        uint actorNetId,
        Position3 position,
        uint targetNetId,
        MapPingCategory category)
    {
        RequireCategory(category);
        RequirePosition(position);
        using var writer = ProtocolEncoding.Writer(ClientLength, out var stream);
        writer.Write(ClientOpcode);
        writer.Write(actorNetId);
        WritePosition(writer, position);
        writer.Write(targetNetId);
        writer.Write((byte)category);
        writer.Flush();
        RequireLength(stream, ClientLength, nameof(MapPingRequest));
        return ProtocolEncoding.Packet(
            1,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable,
            actorNetId == 0
                ? Array.Empty<LeaguePacketEntityReference>()
                : new[] { new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, actorNetId) });
    }

    public static PlaintextPacket MapPingNotification(
        uint senderNetId,
        Position3 position,
        uint targetNetId,
        MapPingCategory category,
        bool playAudio = true,
        bool showChat = true,
        bool pingThrottled = false)
    {
        RequireCategory(category);
        RequirePosition(position);
        using var writer = ProtocolEncoding.Writer(ServerLength, out var stream);
        writer.Write(ServerOpcode);
        writer.Write(senderNetId);
        WritePosition(writer, position);
        writer.Write(targetNetId);
        writer.Write(senderNetId); // native src field, distinct from header source
        writer.Write((byte)((byte)category |
            (playAudio ? 1 << 4 : 0) |
            (showChat ? 1 << 5 : 0) |
            (pingThrottled ? 1 << 6 : 0)));
        writer.Flush();
        RequireLength(stream, ServerLength, nameof(MapPingNotification));
        var references = new List<LeaguePacketEntityReference>(2);
        if (senderNetId != 0)
            references.Add(new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, senderNetId));
        if (targetNetId != 0)
            references.Add(new LeaguePacketEntityReference(LeaguePacketEntityRole.Target, targetNetId));
        return ProtocolEncoding.Packet(Channel, ProtocolEncoding.Finish(stream), encrypt: true,
            LeaguePacketReliability.Reliable, references.ToArray());
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> payload, out MapPingRequest request)
    {
        request = default;
        if (!HasOnlyZeroPadding(payload, ClientLength) || payload[0] != ClientOpcode)
            return false;
        var position = ReadPosition(payload[5..]);
        var categoryBits = payload[21];
        if (!TryReadCategory(categoryBits, out var category) ||
            !IsFinite(position))
            return false;
        request = new(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]),
            position,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[17..21]),
            category);
        return true;
    }

    public static bool TryReadNotification(
        ReadOnlySpan<byte> payload, out MapPingNotification notification)
    {
        notification = default;
        if (!HasOnlyZeroPadding(payload, ServerLength) || payload[0] != ServerOpcode)
            return false;
        var position = ReadPosition(payload[5..]);
        var flags = payload[25];
        var senderNetId = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]);
        var sourceNetId = BinaryPrimitives.ReadUInt32LittleEndian(payload[21..25]);
        if ((flags & 0x80) != 0 || !TryReadCategory(flags, out var category) ||
            !IsFinite(position) || senderNetId == 0 || sourceNetId != senderNetId)
            return false;
        notification = new(
            senderNetId,
            sourceNetId,
            position,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[17..21]),
            category,
            (flags & (1 << 4)) != 0,
            (flags & (1 << 5)) != 0,
            (flags & (1 << 6)) != 0);
        return true;
    }

    public static bool IsMapBounded(Position3 position, NavigationBounds bounds)
    {
        return IsFinite(position) &&
            position.X >= bounds.MinX && position.X <= bounds.MaxX &&
            position.Z >= bounds.MinZ && position.Z <= bounds.MaxZ;
    }

    private static void RequireCategory(MapPingCategory category)
    {
        if (category is not (MapPingCategory.Offense or MapPingCategory.Danger))
            throw new ArgumentOutOfRangeException(nameof(category));
    }

    private static void RequirePosition(Position3 position)
    {
        if (!IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position), "Map-ping positions must be finite.");
    }

    private static bool IsFinite(Position3 position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z);

    private static bool TryReadCategory(byte bits, out MapPingCategory category)
    {
        category = (MapPingCategory)(bits & 0x0F);
        return category is MapPingCategory.Offense or MapPingCategory.Danger;
    }

    private static bool HasOnlyZeroPadding(ReadOnlySpan<byte> payload, int length)
    {
        var paddedLength = checked((length + LegacyBlowfish.BlockSize - 1) /
            LegacyBlowfish.BlockSize * LegacyBlowfish.BlockSize);
        if (payload.Length != length && payload.Length != paddedLength)
            return false;
        for (var i = length; i < payload.Length; i++)
            if (payload[i] != 0) return false;
        return true;
    }

    private static Position3 ReadPosition(ReadOnlySpan<byte> payload)
    {
        return new(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[0..4])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[4..8])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[8..12])));
    }

    private static void WritePosition(BinaryWriter writer, Position3 position)
    {
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
    }

    private static void RequireLength(MemoryStream stream, int expected, string name)
    {
        if (stream.Length != expected)
            throw new InvalidOperationException($"{name} must be {expected} bytes, got {stream.Length}.");
    }
}

/// <summary>
/// Reconstruction: per-player fixed-window budget for the map-ping spam
/// policy.  The shipped DATA/Globals/PingLimit.inibin holds seven hashed numbers
/// (3, 4, 5, 6, 10, 15, 25; 5 and 3 are the burst and window used here), but its
/// key names and the server reader that consumed them are not in the client;
/// the stock client additionally gates local sends to 0.5 seconds. Batch 315:
/// "PingLimit" is a string in none of the client binaries or their symbol data we hold (byte searches), so no binary we hold names the seven values.
/// </summary>
public sealed class PingRateLimiter
{
    private readonly Dictionary<ulong, Window> _windows = new();

    public PingRateLimiter() { }

    public bool TryTake(ulong playerId, double now)
    {
        if (playerId == 0 || !double.IsFinite(now)) return false;
        if (!_windows.TryGetValue(playerId, out var window) ||
            now - window.StartAt >= PingPackets.PingRefillSeconds)
            window = new Window(now, 0);
        if (window.Count >= PingPackets.PingBurstCapacity)
        {
            _windows[playerId] = window;
            return false;
        }
        _windows[playerId] = window with { Count = window.Count + 1 };
        return true;
    }

    private readonly record struct Window(double StartAt, int Count);
}
