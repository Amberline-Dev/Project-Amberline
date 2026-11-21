using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 930 (players, 21 November 2026): Graves's freezes after Quickdraw and after Buckshot cast while walking. Batch 929 fixed both in shared code
/// (a click in the hero's own dash is held for the landing; a walk is handed back after a cast's frame); these pin them on Graves himself.
/// </summary>
public static class BbB930GravesChecks
{
    private static Match Graves()
    {
        var match = new Match("Graves");
        match.Hero.Level = 9;
        match.Learn(5);
        match.Refill();
        match.Foe.Position = new Position2(match.Hero.Position.X + 2500.0, match.Hero.Position.Z);
        match.Advance(0.5);
        return match;
    }

    private static void Click(Match match, Position2 at)
    {
        var packet = ClientGameplayPackets.MoveOrder(match.Hero.NetId, 2, at);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span, match.Runtime.Dependencies.Gameplay), "move click");
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Quickdraw (a 425 dash, POSTPONE_CURRENT_ORDER) with a move click three steps into the dash: Graves walks there after the landing.</summary>
    [Check("scripting.p132-graves-quickdraw-click-in-the-dash-walks-at-the-landing")]
    public static void QuickdrawClickInTheDashWalksAtTheLanding()
    {
        using var match = Graves();
        var hero = match.Hero;
        var goal = hero.Position + new Position2(-500.0, -300.0);
        TestAssert.True(match.Cast(2, at: hero.Position + new Position2(0.0, 400.0)), "Quickdraw is cast");
        match.Advance(3 * Step);
        TestAssert.True(hero.ForcedMove is not null, "Graves is in the dash");
        Click(match, goal);
        match.Advance(3.5);
        TestAssert.True(hero.ForcedMove is null, "the dash has ended");
        TestAssert.True(Distance(hero.Position, goal) < 40.0, $"Graves walked to the click after the landing ({hero.Position} vs {goal})");
    }

    /// <summary>Buckshot pressed while walking a move order: the pellets leave along the aim and the walk resumes after the cast frame.</summary>
    [Check("scripting.p132-graves-buckshot-while-walking-walks-on-after-the-cast-frame")]
    public static void BuckshotWhileWalkingWalksOn()
    {
        using var match = Graves();
        var hero = match.Hero;
        var goal = hero.Position + new Position2(0.0, 1200.0);
        Click(match, goal);
        match.Advance(5 * Step);
        var pressedAt = hero.Position;
        // The foe 250 east of the press point, across the walk: the three pellets (0 and +-16 degrees) reach it only along the aim.
        match.Foe.Position = hero.Position + new Position2(250.0, 0.0);
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Buckshot is cast");
        match.Advance(2.4);
        TestAssert.Equal(3, HitsOn(match, "Foe").Count, "the three pellets hit the foe along the aim");
        TestAssert.True(Distance(hero.Position, goal) < Distance(pressedAt, goal) - 400.0,
            $"the walk resumed after the cast frame ({hero.Position}, goal {goal})");
    }
}
