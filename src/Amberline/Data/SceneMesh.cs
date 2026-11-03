using System;
using System.IO;
using System.Text;
using Amberline.Core;

namespace Amberline.Data;

/// <summary>Placements read from the shipped static scene meshes (<c>LEVELS/&lt;map&gt;/Scene/*.SCB</c>).</summary>
public static class SceneMesh
{
    /// <summary>
    /// The CentralPoint of a static mesh (r3d2Mesh .SCB, versions 2.2 and 3.2): after the 152-byte header (name, counts, flags), the
    /// 24-byte bounding box, the optional 4-byte vertex type of 3.2, the vertices (and per-vertex colours when the type is 1), three floats.
    /// </summary>
    public static Position3 CentralPoint(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 160 || Encoding.ASCII.GetString(data, 0, 8) != "r3d2Mesh")
            throw new InvalidDataException($"{path} is not an r3d2Mesh file");
        var major = BitConverter.ToUInt16(data, 8);
        var minor = BitConverter.ToUInt16(data, 10);
        var vertexCount = (int)BitConverter.ToUInt32(data, 140);
        var offset = 152 + 24;
        var vertexType = 0u;
        if (major == 3 && minor == 2) { vertexType = BitConverter.ToUInt32(data, offset); offset += 4; }
        offset += 12 * vertexCount;
        if (vertexType == 1) offset += 4 * vertexCount;
        return new Position3(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4),
            BitConverter.ToSingle(data, offset + 8));
    }
}
