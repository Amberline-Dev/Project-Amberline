using System.Reflection;
using Amberline.Core;
using Amberline.Simulation.Spells.Bb;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, batch 222 item 4: a script follow (BBMoveToUnit) has no time cap of its own. It ends by contact, by target loss or by
/// its track distance. A static target 1200 units away at 100 units a second is reached after 12 s, so a follow still moving at 4 s has no timer.
/// </summary>
public static class BbFollowMoveChecks
{
    [Check("scripting.bb-follow-move-has-no-time-cap")]
    public static void AFollowEndsOnContactNotOnATimer()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = hero.Position + new Position2(1200.0, 0.0);
        foe.Waypoints.Clear();
        var host = (BbSpellHost)typeof(BbChampionModule).GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(match.Module)!;
        var move = host.Moves.StartFollow(hero, foe, 100.0, 0.0, 0.0, 0.0, 0.0, false, false)
            ?? throw new InvalidOperationException("the follow did not start");
        for (var step = 0; step < 80; step++) match.Runtime.Tick(0.05);
        TestAssert.False(move.Completed, "a follow with no track distance is still running at 4 s (it has no 3 s timer)");
        TestAssert.True(hero.ForcedMove is not null, "the hero is still following at 4 s");
    }

    [Check("scripting.bb-dash-move-has-no-length-based-time-cap")]
    public static void ADashEndsAtItsDestinationNotOnATimer()
    {
        // A script move has no duration in retail: a dash ends at its destination, the first wall or the unit's death. Before batch 241 the dash
        // carried length / speed + 0.05 s as its timer; it now carries only the safety stop past its trip, and still lands on time.
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var host = (BbSpellHost)typeof(BbChampionModule).GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(match.Module)!;
        var target = hero.Position + new Position2(300.0, 0.0);
        var move = host.Moves.Start(hero, target, 1000.0, 0.0, BbQueryMode.FURTHEST_WITHIN_RANGE, false, false)
            ?? throw new InvalidOperationException("the dash did not start");
        var trip = BbSpellHost.Distance(hero.Position, move.Destination) / 1000.0;
        TestAssert.True(move.Remaining > trip + 1.0, $"no timer sized to the trip: {move.Remaining} for a {trip} s trip");
        for (var step = 0; step < 12 && !move.Completed; step++) match.Runtime.Tick(1.0 / 30.0);
        TestAssert.True(move.Completed && move.Succeeded, "the dash ends when it reaches its destination");
        TestAssert.Near(move.Destination.X, hero.Position.X, 1e-6, "on its destination");
    }
}
