using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Amberline.Core;

namespace Amberline.Data;

/// <summary>
/// A Riot skeleton (<c>.skl</c>, magic <c>r3d2sklt</c>, version 2): the bind pose of each bone. Layout as read from
/// DATA/Characters/OdinNeutralGuardian/OdinNeutralGuardian.skl (batch 481b): a 20-byte header (magic, version, designer id, bone count),
/// then one 88-byte record per bone: name[32], parent int32 (-1 for a root), scale float32, and the bone's local transform as a row-major
/// 3x4 matrix [R | t] of float32. The record's scale is not applied to the translations: the stair bones of Odin_SoG_Order.skl come out at
/// the game-unit offsets batch 474d's stairs footprint used; see docs/maintenance/2026-10-29-batch-474d-stairs-footprint.md.
/// </summary>
public static class SkeletonFile
{
    private const int HeaderSize = 20;
    private const int BoneSize = 88;
    private const int NameSize = 32;
    private const int MatrixOffset = NameSize + 4 + 4;

    private static readonly ConcurrentDictionary<(string Path, string Bone), Position3?> Origins = new();

    /// <summary>The bind-pose origin of the named bone in model space (game units), or null when the file or the bone is absent.</summary>
    public static Position3? BindOrigin(string path, string bone) => Origins.GetOrAdd((path, bone), key => Read(key.Path, key.Bone));

    private static Position3? Read(string path, string bone)
    {
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        if (data.Length < HeaderSize || Encoding.ASCII.GetString(data, 0, 8) != "r3d2sklt") return null;
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        if (HeaderSize + count * BoneSize > data.Length) return null;
        var index = -1;
        for (var i = 0; i < count && index < 0; i++)
            if (string.Equals(Name(data, i), bone, StringComparison.OrdinalIgnoreCase)) index = i;
        if (index < 0) return null;
        // The world origin is M(root) * ... * M(parent) * M(bone) applied to the origin, so the bone's own matrix acts first.
        var point = new Position3(0.0, 0.0, 0.0);
        var current = index;
        for (var step = 0; step < count && current >= 0; step++)
        {
            point = Apply(data, current, point);
            current = Parent(data, current);
        }
        return point;
    }

    private static string Name(byte[] data, int bone)
    {
        var offset = HeaderSize + bone * BoneSize;
        return Encoding.ASCII.GetString(data, offset, NameSize).TrimEnd('\0');
    }

    private static int Parent(byte[] data, int bone) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(HeaderSize + bone * BoneSize + NameSize));

    private static Position3 Apply(byte[] data, int bone, Position3 p)
    {
        var o = HeaderSize + bone * BoneSize + MatrixOffset;
        float M(int i) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o + 4 * i));
        return new Position3(
            M(0) * p.X + M(1) * p.Y + M(2) * p.Z + M(3),
            M(4) * p.X + M(5) * p.Y + M(6) * p.Z + M(7),
            M(8) * p.X + M(9) * p.Y + M(10) * p.Z + M(11));
    }
}
