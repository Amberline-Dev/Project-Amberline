using Amberline.Core;

namespace Amberline.Network.Protocol;

public readonly record struct CompressedFxPosition(short X, float Height, short Z);

public static class FxPackets
{
    public const byte CreateGroupOpcode = 0x8C;
    public const byte KillOpcode = 0x3D;

    public static CompressedFxPosition CompressPosition(
        Position2 position,
        NavigationBounds bounds,
        Func<double, double, double> heightProvider)
    {
        ArgumentNullException.ThrowIfNull(heightProvider);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Z))
            throw new ArgumentOutOfRangeException(nameof(position), "FX coordinates must be finite.");

        var offsetX = MidpointOffset(bounds.MinX, bounds.MaxX);
        var offsetZ = MidpointOffset(bounds.MinZ, bounds.MaxZ);
        // The client decodes these with the waypoint grid (0x8C entries: short x step 2 + origin), so
        // they are packed as GetCompressedPos packs a waypoint
        // (VisionPackets.GridCoordinate, float32; batch 316). Validate the truncated value, so 32767.9 remains the valid short 32767.
        var packedX = VisionPackets.GridCoordinate(position.X, offsetX);
        var packedZ = VisionPackets.GridCoordinate(position.Z, offsetZ);
        if (!float.IsFinite(packedX) || !float.IsFinite(packedZ) ||
            packedX < short.MinValue || packedX > short.MaxValue ||
            packedZ < short.MinValue || packedZ > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(position),
                "FX position is outside the signed-short map grid after truncation.");
        }

        var height = heightProvider(position.X, position.Z);
        if (!double.IsFinite(height) || height < float.MinValue || height > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(heightProvider), "FX height must fit a finite float.");

        return new((short)packedX, (float)height, (short)packedZ);
    }

    public static PlaintextPacket CreateGroup(
        uint effectNameHash,
        uint effectNetId,
        Position2 position,
        NavigationBounds bounds,
        Func<double, double, double> heightProvider,
        Position2? targetPosition = null,
        Position2? ownerPosition = null,
        uint targetNetId = 0,
        uint bindNetId = 0,
        ushort flags = 0,
        uint targetBoneHash = 0,
        uint boneHash = 0,
        Position3 direction = default,
        float timeSpent = 0.01f,
        uint headerNetId = 0)
    {
        ArgumentNullException.ThrowIfNull(heightProvider);
        if (!float.IsFinite(timeSpent))
            throw new ArgumentOutOfRangeException(nameof(timeSpent), "FX time must be finite.");

        var pos = CompressPosition(position, bounds, heightProvider);
        var targetPos = CompressPosition(targetPosition ?? position, bounds, heightProvider);
        var ownerPos = CompressPosition(ownerPosition ?? position, bounds, heightProvider);
        if (!float.IsFinite((float)direction.X) || !float.IsFinite((float)direction.Y) ||
            !float.IsFinite((float)direction.Z))
            throw new ArgumentOutOfRangeException(nameof(direction), "FX direction must be finite.");

        using var writer = ProtocolEncoding.Writer(73, out var stream);
        writer.Write(CreateGroupOpcode);
        writer.Write(headerNetId);
        writer.Write((byte)1);
        writer.Write(effectNameHash);
        writer.Write(flags);
        writer.Write(targetBoneHash);
        writer.Write(boneHash);
        writer.Write((byte)1);
        writer.Write(targetNetId);
        writer.Write(effectNetId);
        writer.Write(bindNetId);
        WritePosition(writer, pos);
        WritePosition(writer, targetPos);
        WritePosition(writer, ownerPos);
        writer.Write((float)direction.X);
        writer.Write((float)direction.Y);
        writer.Write((float)direction.Z);
        writer.Write(timeSpent);
        writer.Flush();
        if (stream.Length != 73)
            throw new InvalidOperationException($"FX create group must be 73 bytes, got {stream.Length}.");

        var references = new List<LeaguePacketEntityReference>(4);
        if (headerNetId != 0)
            references.Add(new(LeaguePacketEntityRole.World, headerNetId));
        if (targetNetId != 0)
            references.Add(new(LeaguePacketEntityRole.Target, targetNetId));
        if (effectNetId != 0)
            references.Add(new(LeaguePacketEntityRole.Create, effectNetId));
        if (bindNetId != 0)
            references.Add(new(LeaguePacketEntityRole.Binding, bindNetId));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.CombatChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable,
            references.ToArray());
    }

    public static PlaintextPacket Kill(uint effectNetId, uint headerNetId = 0)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write(KillOpcode);
        writer.Write(headerNetId);
        writer.Write(effectNetId);
        writer.Flush();
        if (stream.Length != 9)
            throw new InvalidOperationException($"FX kill must be 9 bytes, got {stream.Length}.");

        var references = new List<LeaguePacketEntityReference>(2);
        if (headerNetId != 0)
            references.Add(new(LeaguePacketEntityRole.World, headerNetId));
        if (effectNetId != 0)
            references.Add(new(LeaguePacketEntityRole.Effect, effectNetId));
        return ProtocolEncoding.Packet(
            LeagueProtocolConstants.CombatChannel,
            ProtocolEncoding.Finish(stream),
            encrypt: true,
            LeaguePacketReliability.Reliable,
            references.ToArray());
    }

    private static void WritePosition(BinaryWriter writer, CompressedFxPosition position)
    {
        writer.Write(position.X);
        writer.Write(position.Height);
        writer.Write(position.Z);
    }

    private static int MidpointOffset(double min, double max)
    {
        var sum = min + max;
        if (!double.IsFinite(sum) || sum < int.MinValue || sum > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(min), "Navigation bounds exceed integer midpoint range.");
        return (int)((int)sum / 2.0);
    }
}
