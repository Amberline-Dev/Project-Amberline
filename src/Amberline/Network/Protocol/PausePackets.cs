namespace Amberline.Network.Protocol;

/// <summary>Stock patch-126 match pause packets, distinct from movement opcode 0x33.</summary>
public static class PausePackets
{
    public const byte PauseOpcode = 0xA9;
    public const byte ResumeOpcode = 0x0A;

    public static bool IsRequest(ReadOnlySpan<byte> payload) =>
        payload.Length == 14 && payload[0] == PauseOpcode ||
        payload.Length == 10 && payload[0] == ResumeOpcode;

    public static PlaintextPacket Pause(uint clientId, int secondsRemaining)
    {
        if (secondsRemaining < 1) throw new ArgumentOutOfRangeException(nameof(secondsRemaining));
        using var writer = ProtocolEncoding.Writer(14, out var stream);
        writer.Write(PauseOpcode);
        writer.Write(0u);
        writer.Write(clientId);
        writer.Write(secondsRemaining);
        writer.Write((byte)1); // Tournament pause enables the native popup.
        return Packet(stream);
    }

    public static PlaintextPacket Resume(uint clientId, bool delayed)
    {
        using var writer = ProtocolEncoding.Writer(10, out var stream);
        writer.Write(ResumeOpcode);
        writer.Write(0u);
        writer.Write(clientId);
        writer.Write((byte)(delayed ? 1 : 0));
        return Packet(stream);
    }

    private static PlaintextPacket Packet(MemoryStream stream) =>
        ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, ProtocolEncoding.Finish(stream),
            encrypt: true, LeaguePacketReliability.Reliable);
}
