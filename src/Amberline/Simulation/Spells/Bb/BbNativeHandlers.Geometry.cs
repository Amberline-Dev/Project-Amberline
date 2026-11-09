using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The geometry iterators of the wave 7 engine bundle (W27c, 19 October 2026; the new-champion wave plan, items E16 and E17): the points on a line, the
/// points around a circle and the units in a target rectangle. The rest of the bundle is in <c>BbNativeHandlers.UnitState.cs</c>;
/// <c>Wave7Bound</c> and <c>ExecuteWave7</c>, which dispatch all of them, are in this file.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    private static readonly HashSet<string> Wave7Bound = new(StringComparer.Ordinal)
    {
        "BBForceRefreshPath", "BBLinkVisibility", "BBRemoveLinkVisibility", "BBSetNotTargetableToTeam", "BBIncScaleSkinCoef", "BBForEachPointOnLine", "BBForEachPointAroundCircle", "BBForEachUnitInTargetRectangle",
    };

    private bool ExecuteWave7(BbCall call)
    {
        switch (call.Function)
        {
            case "BBForceRefreshPath": ForceRefreshPath(call); return true;
            case "BBLinkVisibility": LinkVisibility(call, link: true); return true;
            case "BBRemoveLinkVisibility": LinkVisibility(call, link: false); return true;
            case "BBSetNotTargetableToTeam": SetNotTargetableToTeam(call); return true;
            case "BBIncScaleSkinCoef": IncScaleSkinCoef(call); return true;
            case "BBForEachPointOnLine": ForEachPointOnLine(call); return true;
            case "BBForEachPointAroundCircle": ForEachPointAroundCircle(call); return true;
            case "BBForEachUnitInTargetRectangle": ForEachUnitInTargetRectangle(call); return true;
            default: return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The geometry iterators (E17). The client's bodies are the server's own code and are in the exe: the points and the box are read from them.
    // ------------------------------------------------------------------------------------------------------------

    private static string IteratorName(BbCall call) =>
        call.Value("IteratorVar") as string ?? throw new BbRuntimeException($"{call.Function} has no IteratorVar");

    /// <summary>
    /// <c>BBForEachPointOnLine</c>: <c>Center</c>, <c>FaceTowardsPos</c>, <c>Size</c>, <c>PushForward</c> (the number
    /// getters), <c>Iterations</c> (an integer, below one nothing runs) and <c>IteratorVar</c>. With <c>d</c> the unit vector from <c>Center</c> to
    /// <c>FaceTowardsPos</c> in the XZ plane (a squared length below 0.001, a length of about 0.032, runs nothing), the line is centred on <c>B = Center + d * PushForward</c>
    /// and runs across <c>d</c> along <c>p = d</c> turned 90 degrees (<c>RotateAroundY</c>): the first point is <c>B - p * Size / 2 + p * Size /
    /// Iterations</c> and each of the <c>Iterations</c> points is <c>Size / Iterations</c> further, so the last one is <c>B + p * Size / 2</c>. A point
    /// outside the navigation grid (the grid's cell test, its bounds plus a cell) is skipped; the others reach the sub-blocks with the
    /// height of the ground. Anivia's Crystallize lays its ice blocks on it (400 to 800 wide, four to eight blocks: the blocks are 100 apart),
    /// Malzahar's Call of the Void and Karthus' Wall of Pain their cubes. The turn is the standard rotation about Y
    /// (<c>x' = x cos + z sin</c>, <c>z' = z cos - x sin</c>), so <c>p = (d.z, -d.x)</c>; the step is added before the first test, so the first
    /// point is already one step in.
    /// </summary>
    private void ForEachPointOnLine(BbCall call)
    {
        var center = SpawnPosition(call, "CenterVar");
        var face = SpawnPosition(call, "FaceTowardsPosVar");
        var size = Sum(call, "Size");
        var push = Sum(call, "PushForward");
        var iterations = (int)Sum(call, "Iterations");
        var iterator = IteratorName(call);
        if (iterations < 1) return;
        var dx = face.X - center.X;
        var dz = face.Z - center.Z;
        if (dx * dx + dz * dz <= 0.001) return;
        var length = Math.Sqrt(dx * dx + dz * dz);
        dx /= length;
        dz /= length;
        var baseX = center.X + dx * push;
        var baseZ = center.Z + dz * push;
        var acrossX = dz;
        var acrossZ = -dx;
        var stepX = acrossX * size / iterations;
        var stepZ = acrossZ * size / iterations;
        var x = baseX - acrossX * size * 0.5;
        var z = baseZ - acrossZ * size * 0.5;
        var navigation = Spells.Movement.Navigation;
        for (var index = 0; index < iterations; index++)
        {
            x += stepX;
            z += stepZ;
            if (x > navigation.MaxX + navigation.CellSize || x < navigation.MinX ||
                z > navigation.MaxZ + navigation.CellSize || z < navigation.MinZ) continue;
            call.Scope.Vars[iterator] = _host.Point(new Position2(x, z));
            call.ExecuteSubBlocks();
        }
    }

    /// <summary>
    /// <c>BBForEachPointAroundCircle</c>: <c>Center</c>, <c>Radius</c>, <c>Iterations</c> (an integer, below one
    /// nothing runs) and <c>IteratorVar</c>. The points are <c>Center + Radius * (cos a, sin a)</c> for <c>a</c> from 0 in steps of the integer
    /// <c>360 / Iterations</c> degrees while <c>a</c> is below 360 (twelve walls of Jarvan's Cataclysm at 30 degrees on a 350 ring; an iteration
    /// count that does not divide 360 makes a wider step and more points than asked for, as the client does). There is no grid check.
    /// The axes are the client's: the sine lookup reads the 0.1-degree sine table at
    /// <c>90 - a</c> (the constant 90.0), so x = cx + R cos a and z = cz + R sin a; the height is the grid's.
    /// </summary>
    private void ForEachPointAroundCircle(BbCall call)
    {
        var center = SpawnPosition(call, "CenterVar");
        var radius = Sum(call, "Radius");
        var iterations = (int)Sum(call, "Iterations");
        var iterator = IteratorName(call);
        if (iterations < 1) return;
        var step = (double)(360 / iterations);
        for (var angle = 0.0; angle < 360.0; angle += step)
        {
            var radians = angle * Math.PI / 180.0;
            call.Scope.Vars[iterator] = _host.Point(new Position2(center.X + radius * Math.Cos(radians), center.Z + radius * Math.Sin(radians)));
            call.ExecuteSubBlocks();
            if (step <= 0.0) break;
        }
    }

    /// <summary>
    /// <c>BBForEachUnitInTargetRectangle</c>: <c>Attacker</c>, <c>Center</c>, <c>HalfWidth</c>, <c>HalfLength</c>, <c>Flags</c>,
    /// <c>IteratorVar</c>, <c>BuffNameFilter</c> and <c>InclusiveBuffFilter</c> (the area iterator's filters). The box points from the
    /// Attacker's position to <c>Center</c> (its long axis, half length <c>HalfLength</c>) and is <c>HalfWidth</c> to each side; a unit is in by
    /// <see cref="BbSpellTargeting.Rectangle"/>. The registered label is a thunk (it pushes 0x400000, the
    /// unnamed IgnoreVisibilityCheck flag; it calls the rectangle implementation). The body normalizes
    /// <c>Center - Attacker</c> with a plain normalise (<c>v / sqrt(|v|^2)</c>, no zero test); the list walk
    /// visits the world list in order through the box test (<c>InsideBox</c>: along = |dir.d| - L and across = |perp.d| - W against
    /// the gameplay radius, both positive also inside the radius circle) and the shared target gate, capped at 128. A Center on
    /// the Attacker normalizes to NaN, every comparison fails and no unit is taken, so nothing runs here either. The client's vectors are 3D; the
    /// server's units stand on the XZ plane, which differs only by the terrain slope along the box.
    /// </summary>
    private void ForEachUnitInTargetRectangle(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar") ?? throw new BbRuntimeException("BBForEachUnitInTargetRectangle has no Attacker");
        var center = SpawnPosition(call, "CenterVar");
        var halfWidth = Sum(call, "HalfWidth");
        var halfLength = Sum(call, "HalfLength");
        var flags = (call.Value("Flags") as string ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var buffFilter = call.Value("BuffNameFilter") as string;
        var inclusive = Flag(call, "InclusiveBuffFilter", true);
        var iterator = IteratorName(call);
        var dx = center.X - attacker.Position.X;
        var dz = center.Z - attacker.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length <= 1e-9) return;
        var forward = new Position2(dx / length, dz / length);
        var picked = BbSpellTargeting.Rectangle(Spells, attacker, center, forward, halfWidth, halfLength, flags);
        if (buffFilter is not null)
            picked = picked.Where(unit => unit.Buffs.Any(buff =>
                string.Equals(buff.Name, buffFilter, StringComparison.OrdinalIgnoreCase)) == inclusive).ToList();
        foreach (var unit in picked)
        {
            call.Scope.Vars[iterator] = unit;
            call.ExecuteSubBlocks();
        }
    }
}
