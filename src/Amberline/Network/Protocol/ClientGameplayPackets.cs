using Amberline.Core;

namespace Amberline.Network.Protocol;

/// <summary>Small typed builders used by offline/native input-boundary checks.</summary>
public static class ClientGameplayPackets
{
    public static PlaintextPacket MoveOrder(uint actorNetId, byte orderType,
        Position2 destination, uint targetNetId = 0)
    {
        if (!double.IsFinite(destination.X) || !double.IsFinite(destination.Z))
            throw new ArgumentOutOfRangeException(nameof(destination));
        using var writer = ProtocolEncoding.Writer(22, out var stream);
        writer.Write((byte)0x75);
        writer.Write(actorNetId);
        writer.Write(orderType);
        writer.Write(ProtocolEncoding.Float32(destination.X, nameof(destination)));
        writer.Write(0.0f);
        writer.Write(ProtocolEncoding.Float32(destination.Z, nameof(destination)));
        writer.Write(targetNetId);
        writer.Flush();
        return ProtocolEncoding.Packet(1, ProtocolEncoding.Finish(stream), encrypt: true,
            LeaguePacketReliability.Reliable,
            actorNetId == 0 ? Array.Empty<LeaguePacketEntityReference>() :
            new[] { new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, actorNetId) });
    }
}
