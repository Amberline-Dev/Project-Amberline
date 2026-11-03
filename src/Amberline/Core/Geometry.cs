namespace Amberline.Core;

public readonly record struct NavigationBounds(double MinX, double MaxX, double MinZ, double MaxZ);

public static class Geometry
{
    /// <summary>
    /// The origin of the 2-unit waypoint lattice: the navigation bounds midpoint with
    /// both conversions truncated toward zero, which is (6991, 7223) on Map1.
    /// </summary>
    public static Position2 WaypointOrigin(NavigationBounds bounds) =>
        new(MidpointOffset(bounds.MinX, bounds.MaxX), MidpointOffset(bounds.MinZ, bounds.MaxZ));

    public static IReadOnlyList<Position2> ClientWaypoints(
        IEnumerable<Position2> waypoints,
        NavigationBounds bounds,
        int gridScale = 2)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        if (gridScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridScale));
        }

        var source = waypoints.ToArray();
        if (source.Length == 0)
        {
            return Array.Empty<Position2>();
        }

        var offsetX = MidpointOffset(bounds.MinX, bounds.MaxX);
        var offsetZ = MidpointOffset(bounds.MinZ, bounds.MaxZ);
        return source.Select(point => new Position2(
            StoreWaypointCoordinate(point.X, offsetX, gridScale),
            StoreWaypointCoordinate(point.Z, offsetZ, gridScale))).ToArray();
    }

    public static Position2? PositionAlong(IReadOnlyList<Position2> waypoints, double distanceTravelled)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        if (waypoints.Count == 0)
        {
            return null;
        }

        if (waypoints.Count == 1 || distanceTravelled <= 0.0)
        {
            return waypoints[0];
        }

        var remaining = distanceTravelled;
        for (var index = 0; index < waypoints.Count - 1; index++)
        {
            var start = waypoints[index];
            var end = waypoints[index + 1];
            var dx = start.X - end.X;
            var dz = start.Z - end.Z;
            var segment = Math.Sqrt(dx * dx + dz * dz);
            if (segment <= 1e-9)
            {
                continue;
            }

            if (remaining <= segment)
            {
                var fraction = remaining / segment;
                return new Position2(start.X + (end.X - start.X) * fraction,
                                      start.Z + (end.Z - start.Z) * fraction);
            }

            remaining -= segment;
        }

        return waypoints[^1];
    }

    public static double? SweptContact(Position2 start, Position2 end, Position2 point, double radius)
    {
        // Reconstruction: sweep this step against the sampled target position.
        // The original server's relative-motion prediction is not recovered.
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var offsetX = start.X - point.X;
        var offsetZ = start.Z - point.Z;
        var c = offsetX * offsetX + offsetZ * offsetZ - radius * radius;
        if (c <= 0.0)
        {
            return 0.0;
        }

        var a = dx * dx + dz * dz;
        var b = offsetX * dx + offsetZ * dz;
        var discriminant = b * b - a * c;
        if (a == 0.0 || discriminant < 0.0)
        {
            return null;
        }

        var fraction = (-b - Math.Sqrt(discriminant)) / a;
        return fraction is >= 0.0 and <= 1.0 ? fraction : null;
    }

    /// <summary>
    /// The fraction of the step <paramref name="start"/> to <paramref name="end"/> at which a head moving along it first comes within
    /// <paramref name="reach"/> of <paramref name="point"/>'s projection on the path, for a point no farther than <paramref name="halfWidth"/>
    /// from the path's line and not more than <paramref name="reach"/> behind the step's start: a band with square ends, unlike
    /// <see cref="SweptContact"/>'s round ones. Null when the point is beside the band or beyond this step.
    /// </summary>
    public static double? SweptHeadContact(Position2 start, Position2 end, Position2 point, double halfWidth, double reach)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length <= 1e-9) return null;
        var along = ((point.X - start.X) * dx + (point.Z - start.Z) * dz) / length;
        var across = Math.Abs((point.X - start.X) * dz - (point.Z - start.Z) * dx) / length;
        if (across > halfWidth || along < -reach) return null;
        var fraction = Math.Max(0.0, (along - reach) / length);
        return fraction <= 1.0 ? fraction : null;
    }

    public static Position2 FaceTowards(Entity entity, Position2 position)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.Facing = FaceTowards(entity.Position, position, entity.Facing);
        return entity.Facing;
    }

    public static Position2 FaceTowards(Position2 current, Position2 target, Position2 previous)
    {
        var dx = target.X - current.X;
        var dz = target.Z - current.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return double.IsFinite(length) && length > 1e-6
            ? new Position2(dx / length, dz / length)
            : previous;
    }

    private static int MidpointOffset(double min, double max)
    {
        var sum = min + max;
        if (!double.IsFinite(sum) || sum < int.MinValue || sum > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(min), "Navigation bounds are outside the supported range.");
        }

        // Match Python int(int(min + max) / 2): both conversions truncate
        // toward zero, unlike floor division for negative bounds.
        var integralSum = (int)sum;
        return (int)(integralSum / 2.0);
    }

    private static double StoreWaypointCoordinate(double value, int offset, int scale)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A waypoint coordinate must be finite.");
        }

        if (value > float.MaxValue || value < -float.MaxValue)
        {
            throw new OverflowException("A waypoint does not fit in the wire float32.");
        }

        var wire = (double)(float)value;
        return offset + Math.Truncate((wire - offset) / scale) * scale;
    }
}
