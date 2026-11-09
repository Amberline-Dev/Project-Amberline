using Amberline.Core;
using Amberline.Data;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 736 (owner, 9 November 2026: "Lee sin can't go through walls on TT, neither with Q or E"). Lee Sin's dashes across real walls of
/// Twisted Treeline (map 4) and, as the control, Summoner's Rift (map 1), cast through the client's 0xA2.
/// <para>
/// Resonating Strike (<c>BlindMonkQTwoDash</c>, <c>BBMoveToUnit</c>) is a follow: the shared engine's the client
/// drags the dash's end to the tracked unit's position less <c>m_TrackUnitBackDist</c> along the
/// approach, with no terrain test, so the dash runs to the point of contact across any wall. The server pulled that point back every step
/// to a floor point on Lee's side (<c>MovementSystem.FollowingLanding</c> searched back toward the dasher), so a target standing within a
/// body of a wall's far side was never reached: Lee stopped short on his side or beside the wall, and the landing hit still fired. Map 4's
/// walls are thick and its jungle corridors narrow, so there a marked target is nearly always that close to a wall; map 1 fails the same way.
/// Safeguard (<c>BBMove</c>, <c>FURTHEST_WITHIN_RANGE</c>) already crossed on both maps; its check pins it.
/// </para>
/// </summary>
public static class WallCrossingDashChecks
{
    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>
    /// A straight east-west line of a shipped grid from the centre of cell (<paramref name="Column"/>, <paramref name="Row"/>) to 600 units east:
    /// open floor at both ends and one wall run from <paramref name="WallFrom"/> to <paramref name="WallTo"/> units along it.
    /// </summary>
    private sealed record Crossing(int MapId, int Column, int Row, double WallFrom, double WallTo);

    // Map 4: the wall between the top-left jungle and the path west of the centre (LEVELS/Map4/AIPath.aimesh_ngrid).
    private static readonly Crossing TwistedTreeline = new(GameMaps.TwistedTreeline, 22, 115, 275, 525);
    // Map 1: the wall west of the dragon's pit (LEVELS/Map1/AIPath.aimesh_ngrid).
    private static readonly Crossing SummonersRift = new(GameMaps.Normal, 174, 88, 275, 525);

    private const double Length = 600.0;

    private static (Position2 From, Position2 To) Ends(NavigationGrid grid, Crossing crossing)
    {
        var from = grid.CellToWorld(new NavCell(crossing.Column, crossing.Row));
        return (from, new Position2(from.X + Length, from.Z));
    }

    /// <summary>The line is what the check says it is: floor, then the one wall run, then floor, with room for a body at both ends.</summary>
    private static void AssertWall(NavigationGrid grid, Crossing crossing, Position2 from, double body)
    {
        for (var along = 0.0; along <= Length; along += 5.0)
        {
            var wall = along > crossing.WallFrom - 5.0 && along < crossing.WallTo;
            TestAssert.Equal(!wall, grid.IsMovementWalkable(new Position2(from.X + along, from.Z)),
                $"map {crossing.MapId}: {along} units along the line is {(wall ? "wall" : "floor")}");
        }
        TestAssert.True(grid.PointClearance(from) >= body + 6.0 && grid.PointClearance(new Position2(from.X + Length, from.Z)) >= body + 6.0,
            "both ends have room for a body");
    }

    private static Position2 FarFloor(NavigationGrid grid, Position2 avoid)
    {
        for (var column = 4; column < grid.CountX - 4; column += 5)
        for (var row = 4; row < grid.CountY - 4; row += 5)
        {
            var spot = grid.CellToWorld(new NavCell(column, row));
            if (grid.IsMovementWalkable(spot) && grid.PointClearance(spot) >= 60.0 && Distance(spot, avoid) > 3000.0) return spot;
        }
        throw new InvalidOperationException("no far floor");
    }

    private static void OnMap(int mapId, Action<GameDataPaths> body)
    {
        var paths = GameDataPaths.Default.ForMap(mapId);
        GameData.ConfigureDefault(paths);
        try
        {
            body(paths);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static void ResonatingStrikeAcross(Crossing crossing) => OnMap(crossing.MapId, paths =>
    {
        using var match = new Match("LeeSin", navigation: NavigationGrid.Load(paths), mapId: crossing.MapId);
        var grid = match.Runtime.Navigation;
        var movement = match.Runtime.Movement;
        var hero = match.Hero;
        var foe = match.Foe;
        var body = movement.MovementRadius(hero);
        var (from, to) = Ends(grid, crossing);
        AssertWall(grid, crossing, from, body);
        var parked = FarFloor(grid, from);
        match.Ally.Position = parked;
        match.FarFoe.Position = parked;
        hero.Position = from;
        foe.Position = to;
        match.Learn(5);
        match.Refill();
        match.Advance(0.1);
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 0, to), "Sonic Wave is accepted");
        for (var step = 0; step < 45 && !foe.Buffs.Any(buff => buff.Name == "BlindMonkQOne"); step++) match.Advance(Step);
        TestAssert.True(foe.Buffs.Any(buff => buff.Name == "BlindMonkQOne"), "Sonic Wave flies over the wall and marks the foe");
        match.Advance(0.1);
        hero.Mana = hero.MaxMana;
        var health = foe.Health;
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 0, hero.Position), "Resonating Strike is accepted");
        for (var step = 0; step < 10 && hero.ForcedMove is null; step++) match.Advance(Step);
        TestAssert.True(hero.ForcedMove is { Effect: "BlindMonkQTwoDash" }, "the follow dash runs");

        // The dash runs for the point of contact (TrackTargetUnit): the foe's position less both bodies and the skin, which lies inside
        // the wall here, not a point on Lee's side of it.
        var arrival = hero.Radius + foe.Radius + movement.Settings.Collision.SkinHighCollider;
        var contact = new Position2(to.X - arrival, to.Z);
        TestAssert.True(contact.X - from.X > crossing.WallFrom && contact.X - from.X < crossing.WallTo,
            $"the point of contact ({contact.X - from.X:0.0} along) lies inside the wall");
        TestAssert.True(Distance(hero.ForcedMove!.Destination, contact) <= 4.0,
            $"the dash heads for the point of contact {contact}, not {hero.ForcedMove.Destination} ({hero.ForcedMove.Destination.X - from.X:0.0} along)");
        // What every client is told: the 0x67 in its follow form at the foe, ending at the point of contact.
        var dash = match.Log.Dashes.Last(item => item.Unit == hero.NetId);
        TestAssert.Equal(foe.NetId, dash.Follow, "the 0x67 follows the foe");
        TestAssert.Near(arrival, dash.BackDistance, 1e-6, "with the contact distance as its back distance");
        TestAssert.True(Distance(dash.Destination, contact) <= 4.0, $"and ends at the point of contact ({dash.Destination})");

        match.Advance(1.0);
        TestAssert.True(hero.ForcedMove is null, "the dash is over");
        TestAssert.True(hero.Position.X - from.X > crossing.WallTo,
            $"Lee Sin lands on the foe's side of the wall ({hero.Position.X - from.X:0.0} along; the wall ends at {crossing.WallTo}; at {hero.Position}, " +
            $"{Distance(hero.Position, to):0.0} from the foe)");
        TestAssert.True(Distance(hero.Position, to) <= arrival + 10.0,
            $"beside the foe ({Distance(hero.Position, to):0.0} from it, contact {arrival:0.0})");
        TestAssert.True(grid.FitsSummonPlacement(hero.Position, body), "on floor his body fits");
        var stop = match.Log.Waypoints.Last(item => item.Unit == hero.NetId && item.Points.Length == 1);
        TestAssert.True(Distance(stop.Points[0], hero.Position) <= 2.0, $"the landing's 0xC1 stop tells every client where he stands ({stop.Points[0]})");
        TestAssert.True(foe.Health < health, "and the landing strikes the foe");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    });

    [Check("spells-live-resonating-strike-crosses-a-thick-wall-on-twisted-treeline", Serial = true)] // Serial: the match reads the process's map.
    public static void ResonatingStrikeCrossesTwistedTreeline() => ResonatingStrikeAcross(TwistedTreeline);

    [Check("spells-live-resonating-strike-crosses-a-thick-wall-on-summoners-rift", Serial = true)]
    public static void ResonatingStrikeCrossesSummonersRift() => ResonatingStrikeAcross(SummonersRift);

    /// <summary>Safeguard to an ally across the same wall of map 4: the <c>BBMove</c> lands on the ally's point (it already did).</summary>
    [Check("spells-live-safeguard-crosses-a-thick-wall-on-twisted-treeline", Serial = true)]
    public static void SafeguardCrossesTwistedTreeline() => OnMap(GameMaps.TwistedTreeline, paths =>
    {
        using var match = new Match("LeeSin", navigation: NavigationGrid.Load(paths), mapId: GameMaps.TwistedTreeline);
        var grid = match.Runtime.Navigation;
        var hero = match.Hero;
        var ally = match.Ally;
        var (from, to) = Ends(grid, TwistedTreeline);
        AssertWall(grid, TwistedTreeline, from, match.Runtime.Movement.MovementRadius(hero));
        var parked = FarFloor(grid, from);
        match.Foe.Position = parked;
        match.FarFoe.Position = parked;
        hero.Position = from;
        ally.Position = to;
        match.Learn(5);
        match.Refill();
        match.Advance(0.1);
        TestAssert.True(CastsDuringDashChecks.WireCast(match, 1, to, ally), "Safeguard on the ally is accepted");
        match.Advance(1.0);
        TestAssert.True(Distance(hero.Position, to) <= 5.0, $"Lee Sin lands on the ally past the wall ({Distance(hero.Position, to):0.0} from it)");
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"), "and shields it");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
    });
}
