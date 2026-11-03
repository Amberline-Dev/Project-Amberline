namespace Amberline.Network.Protocol;

/// <summary>
/// Stock patch-126 inactivity notices. Stock S2C IDs from 0xB5 upward sit two
/// below the AntiBot catalog (OpenAFKWarningMessage is catalog 194 = wire 0xC0).
/// </summary>
public static class AfkPackets
{
    /// <summary>DisplayLocalizedTutorialChatText: the client translates the key into a local chat line.</summary>
    public const byte LocalizedChatTextOpcode = 0x02;
    /// <summary>OpenAFKWarningMessage: header only; the stock client opens the AFK message box.</summary>
    public const byte OpenWarningMessageOpcode = 0xC0;
    public const int ChatKeyBytes = 128;
    /// <summary>ENet disconnect data the stock client reports as prolonged inactivity.</summary>
    public const uint KickDisconnectData = 6;
    // fontconfig_en_US.txt lines 6993 and 625.
    public const string MoveOrBeKickedKey = "game_warning_move_or_be_kicked";
    public const string KickCancelledKey = "game_afk_kicking_cancelled";

    public static PlaintextPacket OpenWarningMessage() =>
        Packet([OpenWarningMessageOpcode, 0, 0, 0, 0]);

    /// <summary>Opcode, zero net ID and a 128-byte NUL-padded ASCII key.</summary>
    public static PlaintextPacket LocalizedChatText(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ProtocolEncoding.RequireAsciiLength(key, ChatKeyBytes - 1, nameof(key));
        using var writer = ProtocolEncoding.Writer(5 + ChatKeyBytes, out var stream);
        writer.Write(LocalizedChatTextOpcode);
        writer.Write(0u);
        ProtocolEncoding.WriteFixedAscii(writer, key, ChatKeyBytes, ChatKeyBytes - 1);
        writer.Flush();
        return Packet(ProtocolEncoding.Finish(stream));
    }

    private static PlaintextPacket Packet(byte[] bytes) =>
        ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, bytes,
            encrypt: true, LeaguePacketReliability.Reliable);
}
