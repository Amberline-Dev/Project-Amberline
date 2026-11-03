using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>Batch 431: Karthus's Wall of Pain beam ends on its far post's point when a post stands inside a building.</summary>
public static partial class ChampionLiveRegressionChecks
{
    // Batch 431 (Part 2, reproduce): Karthus's Wall of Pain with one post inside a Chaos lane turret. The beam must start and end on the posts' points,
    // not on the other post's net id: the stock client places a unit end at the unit's own position, which a post inside a building can leave.
    [Check("scripting.bb-karthus-wall-of-pain-beam-ends-on-its-post-inside-a-turret")]
    public static void KarthusWallBeamEndsOnItsPostInsideATurret()
    {
        using var match = new Match("Karthus", Team.Order, enemyChampion: "Orianna");
        var turret = match.Runtime.World.Entities.Values.First(entity => entity.Team == Team.Chaos && TurretRules.IsLaneTurret(entity));
        var target = new Position2(turret.Position.X + 376, turret.Position.Z);
        match.Hero(0).Position = new Position2(target.X, target.Z - 1000);
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 1, target), "Wall of Pain is cast so that one post stands on the turret");
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var postCubes = match.Runtime.World.Entities.Values.Where(entity => entity.Name == "TestCubeRender" && entity.Placemarker).ToList();
        TestAssert.True(postCubes.Any(post => Math.Abs(post.Position.X - turret.Position.X) < turret.CombatRadius &&
            Math.Abs(post.Position.Z - turret.Position.Z) < turret.CombatRadius), "one post stands inside the turret");
        var postHash = SpellPackets.BuffNameHash("wallofpain_new_post_green");
        var beamHash = SpellPackets.BuffNameHash("wallofpain__new_beam");
        var posts = new List<JsonElement>();
        var beams = new List<JsonElement>();
        foreach (var packet in match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x8C))
        {
            foreach (var group in Decode(packet).GetProperty("groups").EnumerateArray())
            {
                var hash = group.GetProperty("effect_name_hash").GetUInt32();
                foreach (var effect in group.GetProperty("effects").EnumerateArray())
                {
                    if (hash == postHash) posts.Add(effect);
                    else if (hash == beamHash) beams.Add(effect);
                }
            }
        }
        TestAssert.Equal(2, posts.Count, "two posts are created on the wire");
        TestAssert.True(beams.Count >= 1, "the beam is created on the wire");
        var postPoints = posts.Select(post => post.GetProperty("position_compressed").ToString()).ToHashSet();
        foreach (var beam in beams)
        {
            TestAssert.True(postPoints.Contains(beam.GetProperty("position_compressed").ToString()), "the beam starts on a post's point");
            TestAssert.True(postPoints.Contains(beam.GetProperty("target_position_compressed").ToString()), "the beam ends on the other post's point");
            TestAssert.Equal(0u, beam.GetProperty("target_netid").GetUInt32(),
                "the beam's end is a point, not the other post's net id (batch 431, Reconstruction)");
        }
    }
}
