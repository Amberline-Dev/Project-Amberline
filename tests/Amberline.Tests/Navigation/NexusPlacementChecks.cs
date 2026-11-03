using Amberline.Core;
using Amberline.Data;
using Amberline.Tools.Audits;

namespace Amberline.Tests;

/// <summary>
/// Finding 27 (batch 170, owner hand-test of 20 October 2026): "the Chaos nexus may be misplaced".
/// The nexus is a pre-placed scene object: the server sends no position for it (0x0F building stats
/// and 0xB5 local visibility carry none), the stock client puts <c>obj_HQ</c> at the mesh
/// <c>CentralPoint</c> of <c>LEVELS/Map1/Scene/HQ_T1.SCB</c> / <c>HQ_T2.SCB</c>
/// (the nexus's creation handler, which calls the building's creation code and copies the
/// scene object's origin into the building), and the AI/targeting side only ever sees what the
/// client draws. So the shipped SCB is the authority, and the two checks below compare our copy with
/// it and with the baked nav grid the client itself ships.
/// </summary>
public static class NexusPlacementChecks
{


    private static double Distance(Position2 a, Position2 b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static double Distance3(Position3 a, Position3 b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>The flooded 0x42 cells (flag BlockedMask on the raw client view) around two points, overlapped after the translation.</summary>
    private static double BlockedFootprintOverlap(NavigationGrid grid, Position2 reference, Position2 candidate)
    {
        var origin = grid.WorldToCell(reference);
        var target = grid.WorldToCell(candidate);
        var shift = (Column: target.Column - origin.Column, Row: target.Row - origin.Row);
        var moved = Footprint(grid, origin);
        var theirs = Footprint(grid, target);
        var overlap = 0;
        foreach (var cell in moved)
        {
            var translated = new NavCell(cell.Column + shift.Column, cell.Row + shift.Row);
            if (theirs.Contains(translated)) overlap++;
        }
        return (double)overlap / (moved.Count + theirs.Count - overlap);
    }

    /// <summary>The connected blocked component at a nexus-sized window; both nexuses sit on solid walls.</summary>
    private static HashSet<NavCell> Footprint(NavigationGrid grid, NavCell origin)
    {
        const int radius = 12;
        var seen = new HashSet<NavCell>();
        var queue = new Queue<NavCell>();
        var start = origin;
        TestAssert.True(grid.IsInBounds(start) && !grid.IsWalkable(start), "each nexus stands on the client's own blocked cells");
        seen.Add(start);
        queue.Enqueue(start);
        var steps = new (int Column, int Row)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var (dc, dr) in steps)
            {
                var next = new NavCell(cell.Column + dc, cell.Row + dr);
                if (!grid.IsInBounds(next) || grid.IsWalkable(next) || !seen.Add(next)) continue;
                if (Math.Abs(next.Column - origin.Column) > radius || Math.Abs(next.Row - origin.Row) > radius) continue;
                queue.Enqueue(next);
            }
        }
        return seen;
    }

    private readonly record struct SceneMesh(IReadOnlyList<Position3> Vertices, Position2 Central, Position3 Central3);

    /// <summary>
    /// The vertex cloud and CentralPoint of an r3d2Mesh .SCB (version 2.2 here): the same layout
    /// <see cref="DataAudit.ScbCentralPoint"/> reads, with the vertices kept.
    /// </summary>
    private static SceneMesh ScbMesh(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 160 || System.Text.Encoding.ASCII.GetString(data, 0, 8) != "r3d2Mesh")
            throw new InvalidDataException($"{path} is not an r3d2Mesh file");
        var major = BitConverter.ToUInt16(data, 8);
        var minor = BitConverter.ToUInt16(data, 10);
        var vertexCount = (int)BitConverter.ToUInt32(data, 140);
        var offset = 152 + 24;
        var vertexType = 0u;
        if (major == 3 && minor == 2) { vertexType = BitConverter.ToUInt32(data, offset); offset += 4; }
        var vertices = new Position3[vertexCount];
        for (var index = 0; index < vertexCount; index++)
        {
            var at = offset + 12 * index;
            vertices[index] = new Position3(BitConverter.ToSingle(data, at),
                BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8));
        }
        offset += 12 * vertexCount;
        if (vertexType == 1) offset += 4 * vertexCount;
        var central = new Position2(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 8));
        var central3 = new Position3(central.X, BitConverter.ToSingle(data, offset + 4), central.Z);
        return new SceneMesh(vertices, central, central3);
    }
}