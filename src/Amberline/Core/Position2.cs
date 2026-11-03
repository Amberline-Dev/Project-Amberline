namespace Amberline.Core;

public readonly record struct Position2(double X, double Z)
{
    public static Position2 Zero => new(0.0, 0.0);
    public static Position2 operator +(Position2 left, Position2 right) =>
        new(left.X + right.X, left.Z + right.Z);
    public static Position2 operator -(Position2 left, Position2 right) =>
        new(left.X - right.X, left.Z - right.Z);
}
