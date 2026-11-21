using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 931 (players, 21 November 2026: the "global sound bug", worst on Graves; batch 930's Smoke Screen finding). Graves is a 1.0.0.127
/// champion, so these run on 132 only; the shared smoke rule is B931EffectSightChecks (Akali, both lines).
/// </summary>
public static class BbB931GravesChecks
{
    private static Match Graves()
    {
        var match = new Match("Graves");
        match.Hero.Level = 9;
        match.Learn(1);
        match.Refill();
        return match;
    }

    /// <summary>
    /// Smoke Screen thrown 30 into the wall south of Graves (batch 930's wall row): the enemy standing on the floor 200 from the cloud's centre
    /// (slowed by it) gets the red cloud, and so does an enemy who stood 2500 away at the throw and walks into the smoke 1.5 s later (the
    /// persistent-effect replay). Before batch 931 neither got it: the centre in the wall is never seen.
    /// </summary>
    [Check("scripting.p132-graves-smoke-screen-in-a-wall-reaches-the-enemy-standing-in-it")]
    public static void SmokeScreenInAWallReachesTheEnemy()
    {
        var nav = NavigationGrid.LoadDefault();
        var wall = new Position2(7000.0, 6400.0);
        TestAssert.True(!nav.IsWalkable(wall), "the aim point is inside the wall");
        foreach (var late in new[] { false, true })
        {
            using var match = Graves();
            var floor = B931EffectSightChecks.FloorBeside(nav, wall, match.Hero.Position, 0.0);
            var scale = 200.0 / B931EffectSightChecks.Distance(wall, floor);
            var inside = new Position2(wall.X + (floor.X - wall.X) * scale, wall.Z + (floor.Z - wall.Z) * scale);
            TestAssert.True(nav.IsWalkable(inside), $"the enemy's spot {inside} is floor");
            match.Foe.Position = late ? new Position2(wall.X + 2500.0, wall.Z - 600.0) : inside;
            var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
            var own = Observe(match, Team.Order, match.Hero);
            match.Advance(0.5);
            var cast = match.Now;
            TestAssert.True(match.Cast(1, at: wall), "Smoke Screen is cast into the wall");
            match.Advance(1.5);
            TestAssert.True(B931EffectSightChecks.Created(own, cast, "Graves_SmokeGrenade_Cloud_Team_Green") >= 1, "Graves's team gets the green cloud");
            if (late)
            {
                TestAssert.Equal(0, B931EffectSightChecks.Created(enemy, cast, "Graves_SmokeGrenade_Cloud_Team_Red"), "the far enemy sees none of the cloud at the throw");
                match.Foe.Position = inside;
                match.Advance(0.5);
            }
            TestAssert.True(Buff(match.Foe, "GravesSmokeGrenadeBoomSlow") is not null, "the enemy stands in the smoke (slowed)");
            var red = B931EffectSightChecks.Created(enemy, cast, "Graves_SmokeGrenade_Cloud_Team_Red");
            TestAssert.True(red >= 1, $"the enemy {(late ? "who walked in" : "standing in it")} gets the red cloud ({red} creates)");
        }
    }

    /// <summary>
    /// Buckshot at the foe 450 ahead: the impact particle (Graves_ClusterShot_Tar, which carries GravesClusterShotAttack/hit) is drawn by the
    /// pellets' own script on each unit a pellet hits, after the pellets leave. Before batch 931 the server's default hit pass also drew it on every
    /// unit in Buckshot's cone at the cast frame (GravesClusterShot's inibin names it as HitEffectName), so every client heard a pellet impact
    /// before any pellet flew, and on units no pellet reaches.
    /// </summary>
    [Check("scripting.p132-graves-buckshot-draws-no-impact-before-its-pellets")]
    public static void BuckshotDrawsNoImpactBeforeItsPellets()
    {
        using var match = Graves();
        match.Foe.Position = new Position2(match.Hero.Position.X + 450.0, match.Hero.Position.Z);
        // Between the 0 and 16 degree pellet lines, inside the cone: no pellet reaches it.
        var angle = 8.0 * Math.PI / 180.0;
        match.FarFoe.Position = new Position2(match.Hero.Position.X + 850.0 * Math.Cos(angle), match.Hero.Position.Z + 850.0 * Math.Sin(angle));
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.5);
        var cast = match.Now;
        TestAssert.True(match.Cast(0, at: match.Hero.Position + new Position2(700.0, 0.0)), "Buckshot is cast");
        match.Advance(1.5);
        var pelletHash = SpellPackets.BuffNameHash("GravesClusterShotAttack");
        foreach (var (who, capture) in new[] { ("Graves's client", own), ("the foe's client", enemy) })
        {
            var firstPellet = capture.Packets.Where(entry => entry.At >= cast && MissileOf(entry.Packet) is { Spell: var spell } && spell == pelletHash)
                .Select(entry => entry.At).DefaultIfEmpty(double.NaN).Min();
            TestAssert.True(!double.IsNaN(firstPellet), $"{who} gets the pellets");
            var early = capture.Packets.Count(entry => entry.At >= cast && entry.At < firstPellet && EffectNamed(entry.Packet, "Graves_ClusterShot_Tar"));
            TestAssert.Equal(0, early, $"{who}: no pellet impact before the pellets leave ({firstPellet:0.000})");
        }
        foreach (var (unit, name) in new[] { (match.Foe, "Foe"), (match.FarFoe, "FarFoe") })
        {
            var impacts = match.Log.EffectCalls.Count(call => call.Name.Equals("Graves_ClusterShot_Tar.troy", StringComparison.OrdinalIgnoreCase) && call.Bind == unit.NetId);
            var hits = HitsOn(match, name).Count;
            TestAssert.Equal(hits, impacts, $"{name}: one impact per pellet hit ({hits} hits)");
        }
        TestAssert.True(HitsOn(match, "Foe").Count >= 1, "the foe on the pellet lane is hit");
    }
}
