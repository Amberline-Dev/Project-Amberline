using Amberline.Core;

namespace Amberline.Data;

public sealed partial class NavigationGrid
{
    /// <summary>
    /// Distance from the segment to the nearest terrain-blocked cell (the raw
    /// <see cref="Walkable"/> view, no turret stamps), capped at
    /// <paramref name="maxRadius"/>. Zero when the segment touches a blocked cell.
    /// </summary>
    public double SegmentClearance(Position2 source, Position2 target, double maxRadius)
    {
        EnsureFinite(source, nameof(source));
        EnsureFinite(target, nameof(target));
        if (!double.IsFinite(maxRadius) || maxRadius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maxRadius));
        var minRow = Math.Max(0, ToGridInt((Math.Min(source.Z, target.Z) - maxRadius - MinZ) / CellSize));
        var maxRow = Math.Min(CountY - 1, ToGridInt((Math.Max(source.Z, target.Z) + maxRadius - MinZ) / CellSize));
        var dz = target.Z - source.Z;
        var best = maxRadius;
        // Walk only the band within maxRadius of the segment, row by row: a
        // long chord costs O(length x band) cells, not its whole bounding box.
        for (var row = minRow; row <= maxRow; row++)
        {
            var bandTop = MinZ + row * CellSize - maxRadius;
            var bandBottom = MinZ + (row + 1) * CellSize + maxRadius;
            double from = 0.0, to = 1.0;
            if (Math.Abs(dz) > 1e-12)
            {
                var t0 = (bandTop - source.Z) / dz;
                var t1 = (bandBottom - source.Z) / dz;
                from = Math.Max(0.0, Math.Min(t0, t1));
                to = Math.Min(1.0, Math.Max(t0, t1));
                if (from > to) continue;
            }
            else if (source.Z < bandTop || source.Z > bandBottom) continue;
            var xFrom = source.X + (target.X - source.X) * from;
            var xTo = source.X + (target.X - source.X) * to;
            var minColumn = Math.Max(0, ToGridInt((Math.Min(xFrom, xTo) - maxRadius - MinX) / CellSize));
            var maxColumn = Math.Min(CountX - 1, ToGridInt((Math.Max(xFrom, xTo) + maxRadius - MinX) / CellSize));
            for (var column = minColumn; column <= maxColumn; column++)
            {
                if (_walkable[row * CountX + column] != 0) continue;
                var left = MinX + column * CellSize;
                var top = MinZ + row * CellSize;
                best = Math.Min(best, SegmentToBoxDistance(source, target,
                    left, top, left + CellSize, top + CellSize));
                if (best <= 0.0) return 0.0;
            }
        }
        return best;
    }

    // Batch 223 item 1: the client's chord walk tests only the NotPassable bit 0x02 at each sampled cell
    // and the dynamic bodies, so a hero chord may touch a wall corner. The 25-unit margin (HeroChordKeepsClearance) and
    // the 65-unit dilated chord view are retired; hero chords are tested on the movement view (HasHeroMovementLineOfSight).
    // Batch 200 measured the margin's removal alone (stalls 11 -> 15, seed 10002 jam); docs/maintenance/2026-10-25-batch-200-hero-margin.md has the numbers.

    private static double SegmentToBoxDistance(Position2 a, Position2 b,
        double left, double top, double right, double bottom)
    {
        // Liang-Barsky: a segment that enters the box is at distance zero.
        double entry = 0.0, exit = 1.0;
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        if (Clip(-dx, a.X - left, ref entry, ref exit) && Clip(dx, right - a.X, ref entry, ref exit) &&
            Clip(-dz, a.Z - top, ref entry, ref exit) && Clip(dz, bottom - a.Z, ref entry, ref exit) &&
            entry <= exit)
            return 0.0;

        // Disjoint convex shapes: the minimum is between an endpoint and the
        // box, or between a box corner and the segment.
        var best = Math.Min(PointToBox(a, left, top, right, bottom), PointToBox(b, left, top, right, bottom));
        best = Math.Min(best, PointToSegment(new Position2(left, top), a, b));
        best = Math.Min(best, PointToSegment(new Position2(right, top), a, b));
        best = Math.Min(best, PointToSegment(new Position2(left, bottom), a, b));
        return Math.Min(best, PointToSegment(new Position2(right, bottom), a, b));
    }

    // One Liang-Barsky boundary; false when the segment lies wholly outside it.
    private static bool Clip(double p, double q, ref double entry, ref double exit)
    {
        if (p == 0.0) return q >= 0.0;
        var t = q / p;
        if (p < 0.0) entry = Math.Max(entry, t);
        else exit = Math.Min(exit, t);
        return true;
    }

    private static double PointToBox(Position2 point, double left, double top, double right, double bottom)
    {
        var dx = Math.Max(Math.Max(left - point.X, 0.0), point.X - right);
        var dz = Math.Max(Math.Max(top - point.Z, 0.0), point.Z - bottom);
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static double PointToSegment(Position2 point, Position2 a, Position2 b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared == 0.0 ? 0.0
            : Math.Clamp(((point.X - a.X) * dx + (point.Z - a.Z) * dz) / lengthSquared, 0.0, 1.0);
        return Distance(point, new Position2(a.X + t * dx, a.Z + t * dz));
    }
}
