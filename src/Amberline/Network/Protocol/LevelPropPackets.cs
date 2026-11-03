using Amberline.Core;

namespace Amberline.Network.Protocol;

/// <summary>
/// Level-prop packets of the 126 client, for the Crystal Scar props (batch 477).
/// Reconstruction: the layouts are the offsets the client reads, traced in the 126 build's packet switch
/// (the 0xD9 and 0xDA cases):
/// <list type="bullet">
/// <item>0xD9 create: raw 52 selects the branch (0 creates a LevelPropAI at raw 10; 1 places a prop at the scene spawner whose
/// name matches raw 53, else at raw 10). Netid uint32 at raw 5 and node byte at raw 9 (the success path); position at raw 10, 14, 18;
/// raw 22 and raw 34 are vectors passed to the create; team uint32 at raw 46 masked with 0x1ff; raw 51 is the byte passed to the create;
/// Name and PropName are NUL-terminated strings at raw 53 and 117.</item>
/// <item>0xDA animation: name C-string at raw 5 (a 64-byte field); float at 69 and 73; netid uint32 at 77 (the <c>ManagedLevelProp</c> key);
/// uint32 at 81; flag byte at 85 (0 selects the component's PlayAnimation); byte at 86 must be 0. Bytes 87-88 are read but not decoded.</item>
/// </list>
/// The bytes the client does not read are written as zero. Lengths: 0xD9 is 181 bytes (the PropName field ends at 181);
/// 0xDA is 89 bytes (the last read byte is 88).
/// </summary>
public static class LevelPropPackets
{
    public const int CreateLength = 181;
    public const int AnimationLength = 89;

    /// <summary>Opcode of the level-prop create (the client's packet handler, case 0xD9).</summary>
    public const byte CreateOpcode = 0xD9;

    /// <summary>Opcode of the level-prop animation (the client's packet handler, case 0xDA).</summary>
    public const byte AnimationOpcode = 0xDA;

    /// <summary>
    /// The 0xD9 create. <paramref name="branch"/> is raw 52 (0 by name, 1 at the spawner of <paramref name="name"/>).
    /// <paramref name="facing"/> is raw 22 and <paramref name="offset"/> is raw 34 (both three floats).
    /// </summary>
    public static PlaintextPacket Create(
        uint netId,
        byte nodeId,
        Position3 position,
        Position3 facing,
        Position3 offset,
        uint team,
        byte branch,
        byte raw51,
        string name,
        string propName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(propName);
        using var writer = ProtocolEncoding.Writer(CreateLength, out var stream);
        writer.Write(CreateOpcode);
        writer.Write(0u);
        writer.Write(netId);
        writer.Write(nodeId);
        WriteVector(writer, position);
        WriteVector(writer, facing);
        WriteVector(writer, offset);
        writer.Write(team);
        writer.Write((byte)0);
        writer.Write(raw51);
        writer.Write(branch);
        ProtocolEncoding.WriteFixedAscii(writer, name, 64, 63);
        ProtocolEncoding.WriteFixedAscii(writer, propName, 64, 63);
        writer.Flush();
        if (stream.Length != CreateLength) throw new InvalidOperationException("0xD9 must be 181 bytes.");
        return ProtocolEncoding.Packet(LeagueProtocolConstants.VariantChannel, ProtocolEncoding.Finish(stream),
            encrypt: true, LeaguePacketReliability.Reliable, new[] { new LeaguePacketEntityReference(LeaguePacketEntityRole.Create, netId) });
    }

    /// <summary>
    /// The 0xDA animation of the prop with netid <paramref name="netId"/> (raw 77). Raw 69 is the mission time the clip starts at
    /// (<c>startMissionTime</c>, the client seeks to now minus it) and raw 73 the clip's duration (the client plays it in that time,
    /// the time-sync animation call); both are zero or both positive (the assert text of the component's PlayAnimation).
    /// Raw 81 is the play flags (the play routine's flags argument; the prop ORs in 0x20): 0x4 holds the last frame, 0x200 starts at full
    /// weight (batch 509, <c>DominionProps.ClipFlags</c>).
    /// </summary>
    public static PlaintextPacket Animation(uint netId, string animation, float startMissionTime, float duration, uint flags = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(animation);
        using var writer = ProtocolEncoding.Writer(AnimationLength, out var stream);
        writer.Write(AnimationOpcode);
        writer.Write(0u);
        ProtocolEncoding.WriteFixedAscii(writer, animation, 64, 63);
        writer.Write(ProtocolEncoding.Float32(startMissionTime, nameof(startMissionTime)));
        writer.Write(ProtocolEncoding.Float32(duration, nameof(duration)));
        writer.Write(netId);
        writer.Write(flags);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Flush();
        if (stream.Length != AnimationLength) throw new InvalidOperationException("0xDA must be 89 bytes.");
        return ProtocolEncoding.Packet(LeagueProtocolConstants.VariantChannel, ProtocolEncoding.Finish(stream),
            encrypt: true, LeaguePacketReliability.Reliable);
    }

    private static void WriteVector(BinaryWriter writer, Position3 vector)
    {
        writer.Write(ProtocolEncoding.Float32(vector.X, nameof(vector)));
        writer.Write(ProtocolEncoding.Float32(vector.Y, nameof(vector)));
        writer.Write(ProtocolEncoding.Float32(vector.Z, nameof(vector)));
    }
}
