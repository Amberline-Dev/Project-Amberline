namespace Amberline.Network.Protocol;

/// <summary>Retail channel-5 chat envelope used for relays and private replies.</summary>
public static class ChatPackets
{
    public const byte Channel = 5;
    public const int MaxTextBytes = 128;

    public static PlaintextPacket Envelope(uint senderClientId, bool teamChat,
        ReadOnlySpan<byte> text)
    {
        if (text.Length is < 1 or > MaxTextBytes)
            throw new ArgumentOutOfRangeException(nameof(text));
        if (text.IndexOf((byte)0) >= 0)
            throw new ArgumentException("Chat text cannot contain a NUL byte.", nameof(text));

        using var writer = ProtocolEncoding.Writer(13 + text.Length, out var stream);
        writer.Write(senderClientId);
        writer.Write(teamChat ? 1u : 0u);
        writer.Write(text.Length);
        writer.Write(text.ToArray());
        writer.Write((byte)0);
        writer.Flush();
        return ProtocolEncoding.Packet(Channel, ProtocolEncoding.Finish(stream),
            encrypt: true, LeaguePacketReliability.Reliable);
    }
}
