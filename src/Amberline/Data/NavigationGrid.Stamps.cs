namespace Amberline.Data;

/// <summary>
/// A building the stock client writes into its nav grid as a filled circle with flag 0x42 (NotPassable | SeeThrough): the
/// centre is the object's position and the radius its pathfinding collision radius. The server reads the stamps from
/// <see cref="NavigationGridSettings"/> and applies them to every walkability view that is not the raw terrain.
/// </summary>
public readonly record struct NavigationStamp(string Name, Core.Position2 Center, double Radius);

public sealed partial class NavigationGrid
{
    /// <summary>
    /// The client's filled-circle stamp routine, the one routine behind
    /// every building stamp: the nexus (radius = ObjectCFG PathfindingCollisionRadius, flag 0x42),
    /// the inhibitor (same), and the turret (radius 140.0, flag 0x42). A cell is stamped when its centre lies within the radius of the building: <c>(sx - x)^2 + (sz - z)^2 &lt;= r^2</c>
    /// on grid-relative float32 coordinates, the centre of cell c being <c>c * cellSize + cellSize * 0.5</c>. The client scans
    /// the box columns <c>trunc((x - r) / cs - 0.5) .. trunc((x + r) / cs - 0.5)</c> and rows <c>trunc((z - r) / cs + 0.5) ..
    /// trunc((z + r) / cs + 0.5)</c> (the lower column and the upper row bound are one cell loose, the circle
    /// test filters them) and ORs the flags into the cell; the matching <c>RemoveFromNavGrid</c> ORs 0 and nothing calls it
    /// (the stamp lives for the whole match, a dead turret's and a dead inhibitor's included).
    /// <para>
    /// Every operation is a float32 operation in the order of the client's x87 code (the client runs at 24-bit precision).
    /// The circle test uses the sample's own centre, and a sample that passes it is written to the border cell nearest to it:
    /// the client clamps the column and the row to <c>0 .. count - 1</c> before the write (in code form:
    /// <c>if (col &lt; 0) col = 0; if (count &lt;= col) col = count - 1;</c>, the same for the row).
    /// </para>
    /// </summary>
    internal static void StampFilledCircle(byte[] cells, NavigationGridMetadata metadata, Core.Position2 center, double radius)
    {
        var cellSize = (float)metadata.CellSize;
        var half = (float)((double)cellSize * 0.5);
        var x = (float)center.X - (float)metadata.MinX;
        var z = (float)center.Z - (float)metadata.MinZ;
        var r = (float)radius;
        // The client asserts radius < 1000 and writes nothing otherwise.
        if (!(r < 1000f) || r < 0f) return;
        var r2 = r * r;
        float xLow = x - r, xHigh = x + r, zLow = z - r, zHigh = z + r;
        float a = xLow / cellSize; a -= 0.5f;
        float b = zLow / cellSize; b += 0.5f;
        float c = xHigh / cellSize; c -= 0.5f;
        float d = zHigh / cellSize; d += 0.5f;
        int column0 = (int)a, row0 = (int)b, column1 = (int)c, row1 = (int)d;
        float rowCenter = ClientProduct(row0, cellSize, half);
        for (var row = row0; row <= row1; row++)
        {
            var columnCenter = ClientProduct(column0, cellSize, half);
            for (var column = column0; column <= column1; column++)
            {
                var dx = columnCenter - x;
                var dz = rowCenter - z;
                float dz2 = dz * dz, dx2 = dx * dx;
                var distance2 = dz2 + dx2;
                if (!(r2 < distance2))
                    cells[Math.Clamp(row, 0, metadata.CountY - 1) * metadata.CountX +
                          Math.Clamp(column, 0, metadata.CountX - 1)] = 0;
                columnCenter += cellSize;
            }
            rowCenter += cellSize;
        }
    }

    private static float ClientProduct(int cell, float cellSize, float half)
    {
        var product = (float)cell * cellSize;
        return product + half;
    }

    /// <summary>
    /// The stamps of one walkability view: every turret at <paramref name="turretRadius"/> (the turret's creation has no tier
    /// test, so the two fountain shrines are stamped like any other turret), then every nexus and inhibitor at its own pathfinding radius.
    /// </summary>
    private static void StampBuildings(byte[] grid, NavigationGridMetadata metadata, NavigationGridSettings settings,
        double turretRadius)
    {
        foreach (var turret in settings.Turrets)
            StampFilledCircle(grid, metadata, turret.Position, turretRadius);
        foreach (var structure in settings.Structures)
            StampFilledCircle(grid, metadata, structure.Center, structure.Radius);
    }
}
