using Amberline.Core;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 916 (owner, 19 November 2026: Volibear Q "really rarely fails to flip the enemy VISUALLY - they just teleport"): the stock client does not
/// draw a dash whose two lattice points share a row or a column (the client, 132 the client, 126
/// The client: a dash path moves the drawn unit only while both components of its movement are non-zero), so the server sends such a
/// dash one lattice step off the axis (<c>MovementSystem.OffClientAxis</c>). docs/maintenance/2026-11-19-batch-916-knockup-arc.md.
/// </summary>
public static class BbB916Checks
{
    /// <summary>The two path points of the unit's first dash packet in <paramref name="wire"/>.</summary>
    internal static (float[] Start, float[] End)? DashOf(WireCapture wire, Entity unit) =>
        Points(wire, unit, VisionPackets.DashOpcode) is { Length: 2 } points ? (Xz(points[0]), Xz(points[1])) : null;

    /// <summary>The point of the unit's last one-point waypoint packet (its landing stop) in <paramref name="wire"/>.</summary>
    internal static float[]? LastStopOf(WireCapture wire, Entity unit) =>
        Points(wire, unit, VisionPackets.WaypointListOpcode, last: true) is { Length: 1 } points ? Xz(points[0]) : null;

    // The dash decoder gives its lattice points as float pairs, the 0xC1/0xC2 decoder its float32 points as double pairs.
    private static float[] Xz(object? point) => point switch
    {
        float[] pair => pair,
        double[] pair => [(float)pair[0], (float)pair[1]],
        _ => throw new InvalidOperationException("not a decoded point"),
    };

    private static object?[]? Points(WireCapture wire, Entity unit, byte opcode, bool last = false)
    {
        object?[]? found = null;
        foreach (var (_, packet) in wire.Packets)
        {
            if (packet.Plaintext.Span[0] != opcode) continue;
            var fields = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel).Fields;
            var id = fields.TryGetValue("unit_netid", out var dashUnit) ? dashUnit : fields.GetValueOrDefault("net_id");
            if (id is not uint netId || netId != unit.NetId || fields.GetValueOrDefault("waypoints") is not object?[] points) continue;
            found = points;
            if (!last) return found;
        }
        return found;
    }

    internal static string Show(float[] point) => $"({point[0]:0.##}, {point[1]:0.##})";

    /// <summary>
    /// The dash's smaller component keeps, at <see cref="Amberline.Simulation.Movement.MovementSystem.DashDrawnFrameRate"/>, at least the 0.005 units a
    /// frame that the client's actor update does not zero.
    /// </summary>
    internal static void AssertDrawn(string name, (float[] Start, float[] End) dash, double speed)
    {
        var (dx, dz) = (Math.Abs(dash.End[0] - dash.Start[0]), Math.Abs(dash.End[1] - dash.Start[1]));
        var length = Math.Sqrt(dx * dx + dz * dz);
        var perFrame = Math.Min(dx, dz) / length * speed / Amberline.Simulation.Movement.MovementSystem.DashDrawnFrameRate;
        TestAssert.True(perFrame >= 0.005,
            $"{name}: the dash path {Show(dash.Start)} -> {Show(dash.End)} keeps a cross component the client draws ({perFrame:0.#####} a frame at " +
            $"{Amberline.Simulation.Movement.MovementSystem.DashDrawnFrameRate} frames a second; the client zeroes one under 0.005)");
    }

    [Check("scripting.b916-a-dash-along-a-lattice-row-or-column-is-sent-off-the-axis-and-lands-where-sent")]
    public static void AxisDashIsSentOffTheAxis()
    {
        foreach (var (name, facing, type, axis, speed) in new[]
                 {
                     ("along x", new Position2(1.0, 0.0), "FURTHEST_WITHIN_RANGE", true, 1000.0),
                     ("along z", new Position2(0.0, -1.0), "FURTHEST_WITHIN_RANGE", true, 1000.0),
                     ("first collision along z", new Position2(0.0, 1.0), "FIRST_COLLISION_HIT", true, 1000.0),
                     ("a one-second trip along x", new Position2(1.0, 0.0), "FURTHEST_WITHIN_RANGE", true, 300.0),
                     ("a 3-4-5 diagonal (control)", new Position2(0.6, 0.8), "FURTHEST_WITHIN_RANGE", false, 1000.0),
                 })
        {
            using var match = new Match("Sivir");
            match.Runtime.Waves!.AutomaticSpawningEnabled = false;
            var foe = match.Foe;
            // A unit that stopped stands on its lattice anchor (MovementSystem.InstallStop), as both of batch 912's client cases did.
            foe.Position = match.Runtime.Movement.ClientAnchor(foe.Position);
            foe.Facing = facing;
            var origin = foe.Position;
            var wire = Observe(match, Team.Order, match.Hero);
            // The observer first sees the unit standing (a unit first seen mid-dash gets the dash inside its reveal instead).
            match.Advance(0.2);
            BbMoveChecks.Activate(match, foe, match.Hero,
                BbMoveChecks.Script([BbMoveChecks.Point("Dest", 300), BbMoveChecks.Move("Dest", speed, 20, type)]));
            match.Advance(Step);
            var dash = DashOf(wire, foe) ?? throw new InvalidOperationException($"{name}: no dash packet for the unit");
            var (dx, dz) = (dash.End[0] - dash.Start[0], dash.End[1] - dash.Start[1]);
            if (axis) AssertDrawn(name, dash, speed);
            else
                TestAssert.True(Math.Abs(dx - 180.0f) <= 2.0f && Math.Abs(dz - 240.0f) <= 2.0f,
                    $"{name}: a diagonal dash keeps its own end: {Show(dash.Start)} -> {Show(dash.End)}");
            match.Advance(1.3);
            TestAssert.True(foe.ForcedMove is null, $"{name}: the dash landed");
            var stop = LastStopOf(wire, foe) ?? throw new InvalidOperationException($"{name}: no landing stop");
            TestAssert.True(Math.Abs(stop[0] - dash.End[0]) < 0.01f && Math.Abs(stop[1] - dash.End[1]) < 0.01f,
                $"{name}: the landing stop {Show(stop)} is the dash's end {Show(dash.End)}, so the client is not corrected");
            var travelled = Math.Sqrt((foe.Position.X - origin.X) * (foe.Position.X - origin.X) + (foe.Position.Z - origin.Z) * (foe.Position.Z - origin.Z));
            TestAssert.Near(300.0, travelled, 4.0, $"{name}: the unit still travelled the script's 300 units");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{name}: no script failure");
        }
    }
}
