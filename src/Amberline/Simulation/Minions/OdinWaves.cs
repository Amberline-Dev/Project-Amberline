using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Minions;

/// <summary>
/// Batch 451 part 2, batch 499: an Odin wave unit (SmallWave on Dominion, map 8). Each wave unit runs a shipped lane script through the lane
/// engine and walks a lane of the map's scene. Batch 499: the script follows the squad's lane direction, not its team: a squad that pushes its
/// lane in reverse (SpawnMinionWave's <c>ReverseMinionPath</c>, OdinScriptBehaviorTrees.xml:3802, handed to SquadPushLane's <c>Reverse</c>,
/// :3889-3896) runs <c>MinionReverse.luaobj</c>, whose only difference from <c>MinionOdin.luaobj</c> is SetStateAndMoveToBackwardNav in place of
/// SetStateAndMoveToForwardNav (batch 451, the diff of the two). Reconstruction: no shipped data names the script per squad (SmallWave.json's
/// <c>runLuaAI</c> is "default").
/// </summary>
public static class OdinWaves
{
    public const string ForwardScript = "MinionOdin";
    public const string BackwardScript = "MinionReverse";

    /// <summary>
    /// Batch 499: one wave lane of a capture point: the point that sends it (0 to 4, point A to E), its spawn marker, its scene nav list
    /// (<c>__NAV_&lt;Lane&gt;_&lt;index&gt;</c>), whether the list is walked backward, and the point it leads to.
    /// </summary>
    public sealed record Lane(int Point, string Marker, int Nav, bool Reverse, int Destination);

    /// <summary>
    /// Batch 499 (Reconstruction from the scene's own geometry, <c>LEVELS/Map8/Scene</c>; no shipped table pairs them). Each point has two
    /// minion spawn markers (<c>Info_MinionSpawn&lt;X&gt;1</c> and <c>&lt;X&gt;2</c>) and the scene has ten nav lists, <c>__NAV_0</c> to <c>__NAV_9</c>.
    /// Lists 0 to 4 start at a point's marker 1 and run to the next point of the outer ring, past its centre, to its minion graveyard
    /// (C1 to D, D1 to E, E1 to A, A1 to B, B1 to C). Lists 5 to 9 run the same way round from a point's graveyard and end at the next point's
    /// marker 2, so walked backward they take a wave from marker 2 the other way round (D2 to C, E2 to D, A2 to E, B2 to A, C2 to B). For
    /// example <c>__NAV_3_000</c> (3031, 2435) lies 400 from <c>Info_MinionSpawnA1</c> (3196, 2073), <c>__NAV_3_006</c> (2405, 7838) 128 from
    /// point B's centre (2520, 7782), and <c>__NAV_7_012</c> (4524, 1236) 257 from <c>Info_MinionSpawnA2</c> (4269, 1271).
    /// </summary>
    public static readonly IReadOnlyList<Lane> Lanes =
    [
        new(0, "Info_MinionSpawnA1", 3, false, 1),
        new(0, "Info_MinionSpawnA2", 7, true, 4),
        new(1, "Info_MinionSpawnB1", 4, false, 2),
        new(1, "Info_MinionSpawnB2", 8, true, 0),
        new(2, "Info_MinionSpawnC1", 0, false, 3),
        new(2, "Info_MinionSpawnC2", 9, true, 1),
        new(3, "Info_MinionSpawnD1", 1, false, 4),
        new(3, "Info_MinionSpawnD2", 5, true, 2),
        new(4, "Info_MinionSpawnE1", 2, false, 0),
        new(4, "Info_MinionSpawnE2", 6, true, 3),
    ];

    /// <summary>The shipped script a wave unit runs (its <see cref="Entity.AiScript"/>), or null for any other unit.</summary>
    public static string? ScriptOf(Entity unit) =>
        unit.UnitType == UnitType.Minion && (unit.AiScript == ForwardScript || unit.AiScript == BackwardScript)
            ? unit.AiScript
            : null;

    /// <summary>
    /// Batch 499: a lane's route from <paramref name="start"/> (its spawn marker): the scene's <c>__NAV_&lt;nav&gt;_&lt;index&gt;</c> points in index
    /// order, backward for a reversed lane, joined on the map's grid the way a Summoner's Rift lane is (WaveScheduler.LaneRoute: a straight leg
    /// where the grid finds none) and snapped to the client's waypoint lattice. Empty when the scene has no such list.
    /// </summary>
    public static IReadOnlyList<Position2> Route(Func<string, Position3?> locate, int nav, bool reverse, Position2 start, NavigationGrid grid)
    {
        var spine = new List<Position2>();
        for (var index = 0; locate($"__NAV_{nav}_{index:000}") is { } at; index++) spine.Add(new Position2(at.X, at.Z));
        if (spine.Count == 0) return [];
        if (reverse) spine.Reverse();
        var route = new List<Position2> { start };
        foreach (var point in spine)
        {
            var leg = grid.FindPath(route[^1], point);
            if (leg.Count > 1) route.AddRange(leg.Skip(1));
            else route.Add(point);
        }
        return Geometry.ClientWaypoints(route, new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ));
    }

    /// <summary>
    /// The birth of a wave unit: its script, its route installed the way every path is (<see cref="MovementSystem.InstallPath"/>),
    /// and the script's own timers (minionLib 0.0 <c>OnInit</c>, through <see cref="MinionAiSystem.OnInit"/>). The lane suffix stays
    /// empty at birth, as WaveScheduler leaves it (a route is not a stale rejoin anchor).
    /// </summary>
    public static void Birth(Entity unit, string script, IReadOnlyList<Position2> route, NavigationBounds bounds, MinionAiSystem minions)
    {
        unit.AiScript = script;
        MovementSystem.InstallPath(unit, route, bounds);
        unit.LaneWaypoints = new List<Position2>();
        minions.OnInit(unit);
        // Batch 499: the encounter's mutators changed the unit's stats after its spawn block went out (EncounterService.SpawnSquad).
        minions.SendStats(unit);
    }
}
