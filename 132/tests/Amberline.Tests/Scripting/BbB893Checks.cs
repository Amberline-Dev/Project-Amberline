using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 893 (18 November 2026): the deep audit of Ahri, Viktor and Graves on 1.0.0.132
///.
/// </summary>
public static class BbB893Checks
{
    /// <summary>
    /// Collateral Damage's explosion is a cone from where the shell stopped, not from Graves. The shipped <c>GravesChargeShotShot</c>
    /// <c>SpellOnMissileEnd</c> spawns a marker 150 units short of the missile's end, faces it at the end and casts <c>GravesChargeShotXplode</c>
    /// (a CastType 0 cone, <c>CastConeDistance</c> 800, <c>CastConeAngle</c> 30) with <c>OverrideCastPosition</c> at that marker. A unit 300 behind
    /// the champion the shell stopped on is 450 from the marker and 900 from Graves: the cone reaches it (140 at rank one, no bonus attack damage)
    /// only from the marker. The champion the shell hit is spared (its <c>GravesChargeShotShot</c> mark).
    /// </summary>
    [Check("scripting.p132-graves-collateral-damage-cone-starts-where-the-shell-stopped")]
    public static void CollateralDamageConeStartsWhereTheShellStopped()
    {
        using var match = new Match("Graves");
        match.Hero.Level = 6;
        match.Learn(1);
        match.Advance(0.3);
        match.Foe.Position = new Position2(7600, 7000);
        var behind = Enemy(match, "Behind", new Position2(7900, 7000));
        var beside = Enemy(match, "Beside", new Position2(7000, 7700));
        // 200 off the shell's line, 450 from Graves: inside a cone opened from Graves (27 degrees off its axis, 30 allowed), 50 behind the
        // marker, and clear of the shell itself (a unit 130 off its line is hit by the shell).
        Enemy(match, "Near", new Position2(7400, 7200));
        TestAssert.True(match.Cast(3, at: match.Foe.Position), "Collateral Damage is cast at the foe: " + match.CanCast(3).Reason);
        match.Advance(1.5);
        var log = string.Join(" / ", match.Log.Hits);
        TestAssert.Equal(0, HitsOn(match, "Near").Count, "a unit near Graves beside the shell's path is behind the explosion; log: " + log);
        TestAssert.Equal(1, HitsOn(match, "Foe").Count, "the shell hits the foe once and the explosion spares it; log: " + log);
        var cone = HitsOn(match, "Behind");
        TestAssert.Equal(1, cone.Count, "the explosion reaches the unit 300 behind the foe (450 from the marker, 900 from Graves); log: " + log);
        NearF32(140.0, cone[0].Raw, "the explosion deals its rank-one 140 (no bonus attack damage); log: " + log);
        TestAssert.Equal(0, HitsOn(match, "Beside").Count, "a unit off to the side of Graves is outside the cone; log: " + log);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }
}
