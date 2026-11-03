using System.Buffers.Binary;

namespace Amberline.Network.Protocol;

// Stock console initialization appends these four entries in order.
public enum ChampionEmote : uint { Dance, Taunt, Laugh, Joke }

public static class EmotePackets
{
    public const byte ClientOpcode = 0x4C;
    public const byte ServerOpcode = 0x48;
    public const int Length = 9;

    public static bool TryReadRequest(ReadOnlySpan<byte> payload, out ChampionEmote emote)
    {
        emote = default;
        if (payload.Length is not (Length or 16) || payload[0] != ClientOpcode)
            return false;
        foreach (var padding in payload[Length..])
            if (padding != 0) return false;
        emote = (ChampionEmote)BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]);
        return emote <= ChampionEmote.Joke;
    }

    public static PlaintextPacket Play(uint heroNetId, ChampionEmote emote)
    {
        if (heroNetId == 0) throw new ArgumentOutOfRangeException(nameof(heroNetId));
        if (emote > ChampionEmote.Joke) throw new ArgumentOutOfRangeException(nameof(emote));
        var packet = new byte[Length];
        packet[0] = ServerOpcode;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), heroNetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), (uint)emote);
        return ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, packet, true,
            LeaguePacketReliability.Reliable, new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, heroNetId));
    }
}
