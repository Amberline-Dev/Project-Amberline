using Amberline.Core;

namespace Amberline.Simulation.Movement;

/// <summary>
/// DIAGNOSTICS: the waypoint trail a stock client appends to its move order (the path it planned for itself), read for the playtest
/// monitor, the fidelity trace and the client-simulation report; no unit is moved from it. The decoder is <c>ClientMovementTrail.cs</c>
/// (batch 613: a build without it reads no trail).
/// </summary>
public static partial class MoveOrderTrail
{
    public static bool TryRead(ReadOnlySpan<byte> payload, Position2 origin, out List<Position2> points)
    {
        List<Position2>? read = null;
        Decode(payload, origin, ref read);
        points = read ?? [];
        return read is not null;
    }

    static partial void Decode(ReadOnlySpan<byte> payload, Position2 origin, ref List<Position2>? points);
}
