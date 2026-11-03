using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;

namespace Amberline.Tests;

/// <summary>
/// Batch 253: the general fog follows the client's own fog of war. The client draws its team's fog itself
/// (the perception-bubble update, with a raycast on the live nav grid): a wall is a cell
/// with NotPassable and without SeeThrough (the building stamps, flag 0x42, are the only see-through part of a building), a viewer
/// outside brush stops at grass, and a viewer in brush sees through grass only until its ray leaves the grass beyond the viewer's
/// SelectionRadius. The geometry is Map1's own terrain; each check asserts its terrain premise first.
/// </summary>
public static class ClientFogChecks
{
    [Check("replication.fog-nexus-rim-beyond-the-stamp-is-a-wall")]
    public static void NexusRimBeyondTheStampIsAWall()
    {
        var grid = NavigationGrid.LoadDefault();
        var viewer = new Position2(12346, 12765);
        var target = new Position2(13046, 12765);
        var nexus = MapDefinitions.NexusPositions["HQ_T2"];
        TestAssert.True(grid.IsMovementWalkable(viewer) && grid.IsMovementWalkable(target), "both ends stand on the floor");
        TestAssert.True(grid.BrushIdAt(viewer, 100) is null && grid.BrushIdAt(target, 65) is null, "neither end is in brush");
        var rim = Cells(grid, viewer, target).Where(cell => !grid.IsWalkable(cell) &&
            Distance(grid.CellToWorld(cell), nexus) > MapDefinitions.ChaosNexusPathfindingRadius).ToList();
        TestAssert.True(rim.Count >= 3, $"the line crosses the nexus mesh outside its {MapDefinitions.ChaosNexusPathfindingRadius} stamp");
        TestAssert.True(Cells(grid, viewer, target).All(cell => grid.BrushIdAt(grid.CellToWorld(cell)) is null), "no grass on the line");

        // Before batch 253 the server fog cleared every opaque blob within 400 of a building (ClearStructureVisionComponents), so it saw
        // through the rim; the client keeps the rim opaque and the overlay draws the far side dark.
        TestAssert.False(grid.HasUnitVision(viewer, 100, target, 65), "unit sight stops at the nexus rim");
        TestAssert.False(VisibilityRules.ProvidersReveal(new NavigationGridVisionEnvironment(grid),
            [new VisionProvider(viewer, 1350, 100)], target, 65), "the fog hides the unit behind the rim");
    }

    [Check("replication.fog-brush-allowance-ends-beyond-the-viewer-radius")]
    public static void BrushAllowanceEndsBeyondTheViewerRadius()
    {
        var grid = NavigationGrid.LoadDefault();
        var viewer = new Position2(11846, 1265);
        var hidden = new Position2(11500, 1065);
        var open = new Position2(11846, 1700);
        TestAssert.True(grid.BrushIdAt(viewer, 100) is not null, "the viewer stands in brush");
        TestAssert.True(grid.BrushIdAt(hidden, 65) is null && grid.BrushIdAt(open, 65) is null, "both targets stand outside brush");
        TestAssert.True(Cells(grid, viewer, hidden).Concat(Cells(grid, viewer, open)).All(grid.IsWalkable), "no wall on either line");
        // Along the hidden line the ray leaves the brush more than 100 units out, then crosses grass again (the brush's diagonal edge).
        var cells = Cells(grid, viewer, hidden).ToList();
        var leaves = cells.FindIndex(cell => grid.BrushIdAt(grid.CellToWorld(cell)) is null &&
            Distance(grid.CellToWorld(cell), viewer) > 100.0);
        TestAssert.True(leaves > 0 && cells.Skip(leaves).Any(cell => grid.BrushIdAt(grid.CellToWorld(cell)) is not null),
            "the hidden line re-enters grass after it leaves the brush beyond the viewer's radius");

        var environment = new NavigationGridVisionEnvironment(grid);
        var provider = new VisionProvider(viewer, 1350, 100);
        // The old label rule let the viewer see through every cell of its own brush; the client's walk ends the allowance.
        TestAssert.False(VisibilityRules.ProvidersReveal(environment, [provider], hidden, 65), "grass past the allowance blocks");
        TestAssert.False(grid.HasUnitVision(viewer, 100, hidden, 65), "unit sight agrees");
        TestAssert.True(VisibilityRules.ProvidersReveal(environment, [provider], open, 65), "the viewer still sees out of its brush");
    }

    [Check("replication.fog-position-bubble-lights-its-whole-disc")]
    public static void PositionBubbleLightsItsWholeDisc()
    {
        // The client's perception-bubble update draws a bubble without the unit flag (bit 2), or with a radius over
        // 3000, with no line-of-sight mask: its whole disc is lit, walls and brush included. A timed position bubble
        // (BBAddPosPerceptionBubble) reaches the client as 0x7B with no unit flag, so a unit inside its disc is revealed.
        var grid = NavigationGrid.LoadDefault();
        var environment = new NavigationGridVisionEnvironment(grid);
        var centre = new Position2(12346, 12765);
        var behindRim = new Position2(13046, 12765);
        var outsideBrush = new Position2(6666.1, 664.8);
        var inBrush = new Position2(7166.1, 564.8);
        TestAssert.False(grid.HasUnitVision(centre, -1, behindRim, 65), "the nexus rim blocks the walk");
        TestAssert.True(grid.BrushIdAt(outsideBrush) is null && grid.BrushIdAt(inBrush, 100) is not null, "a brush unit, a plain centre");

        var world = new WorldState();
        world.AddTimedVisionBubble(Team.Order, centre, 800.0, 5.0);
        world.AddTimedVisionBubble(Team.Order, outsideBrush, 600.0, 5.0);
        var providers = new Amberline.Simulation.Items.ItemVisionService(world).Providers(Team.Order);
        TestAssert.True(VisibilityRules.ProvidersReveal(environment, providers, behindRim, 65), "the disc is lit behind the wall");
        TestAssert.True(VisibilityRules.ProvidersReveal(environment, providers, inBrush, 100), "the disc is lit in the brush");
        TestAssert.False(VisibilityRules.ProvidersReveal(environment, providers, behindRim + new Position2(0, -900), 65),
            "nothing outside the disc");

        // A unit's own bubble (IsUnit) keeps the walk.
        TestAssert.False(VisibilityRules.ProvidersReveal(environment, [new VisionProvider(centre, 800.0, 100)], behindRim, 65),
            "a unit at the centre does not see behind the rim");
    }

    private static IEnumerable<NavCell> Cells(NavigationGrid grid, Position2 from, Position2 to)
    {
        NavCell? last = null;
        for (var step = 0; step <= 400; step++)
        {
            var t = step / 400.0;
            var cell = grid.WorldToCell(new Position2(from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t));
            if (cell == last) continue;
            last = cell;
            yield return cell;
        }
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
