using System.Buffers.Binary;
using System.Text;

namespace Amberline.AirCompanion;

public enum MaestroMessageType
{
    GameClientCreate = 0,
    GameClientStopped = 1,
    GameClientCrashed = 2,
    Close = 3,
    Heartbeat = 4,
    Reply = 5,
    LauncherClient = 6,
    GameClientAbandoned = 7,
    GameClientLaunched = 8,
    GameClientVersionMismatch = 9,
    GameClientConnectedToServer = 10,
    ChatMessageToGame = 11,
    ChatMessageFromGame = 12,
    Invalid = 13,
}

public sealed record MaestroMessage(int Type, byte[] Payload)
{
    public const int HeaderSize = 16;
    public const int Version = 1;
    public const int MaxPayloadBytes = 1_048_576;

    public string PayloadText => MaestroProtocol.DecodePayload(Payload);

    public static MaestroMessage Text(MaestroMessageType type, string? payload = null) =>
        new((int)type, Encoding.UTF8.GetBytes(payload ?? string.Empty));
}

public static class MaestroProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<MaestroMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[MaestroMessage.HeaderSize];
        if (!await ReadExactlyAsync(stream, header, allowCleanEof: true, cancellationToken).ConfigureAwait(false))
            return null;

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        var payloadSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        if (headerSize != MaestroMessage.HeaderSize)
            throw new InvalidDataException($"Maestro header size must be {MaestroMessage.HeaderSize}, got {headerSize}.");
        if (version != MaestroMessage.Version)
            throw new InvalidDataException($"Maestro protocol version must be {MaestroMessage.Version}, got {version}.");
        if (payloadSize is < 0 or > MaestroMessage.MaxPayloadBytes)
            throw new InvalidDataException($"Maestro payload is outside the 0..{MaestroMessage.MaxPayloadBytes} byte bound.");

        var payload = new byte[payloadSize];
        if (payloadSize != 0)
            await ReadExactlyAsync(stream, payload, allowCleanEof: false, cancellationToken).ConfigureAwait(false);
        return new MaestroMessage(type, payload);
    }

    public static ValueTask WriteAsync(Stream stream, MaestroMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload.Length > MaestroMessage.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(message), "Maestro payload is too large.");

        var header = new byte[MaestroMessage.HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), MaestroMessage.HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), MaestroMessage.Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), message.Type);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), message.Payload.Length);
        return WriteFrameAsync(stream, header, message.Payload, cancellationToken);
    }

    public static ValueTask WriteAsync(Stream stream, MaestroMessageType type, string? payload = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(stream, MaestroMessage.Text(type, payload), cancellationToken);

    internal static string DecodePayload(byte[] payload)
    {
        try { return StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Maestro payload is not valid UTF-8.", exception); }
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, bool allowCleanEof,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (offset == 0 && allowCleanEof) return false;
                throw new EndOfStreamException("The Maestro peer closed a partial frame.");
            }
            offset += count;
        }
        return true;
    }

    private static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length != 0)
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
