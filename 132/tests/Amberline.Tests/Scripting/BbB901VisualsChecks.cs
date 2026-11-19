using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 901 (19 November 2026, the owner's 1.0.0.132 hand test): "Viktor E lacks ground effect (burned ground VFX)" and "Ahri W doesn't show the
/// VFX around her". docs/maintenance/2026-11-19-batch-901-visuals.md.
/// </summary>
public static class BbB901VisualsChecks
{
    // ViktorDeathRay's OnBuffActivate binds ViktorEntropicBeam_tar_beam.troy (the burning trench) and ViktorEntropicBeam_tar.troy to the
    // MaokaiSproutling the cast slides 500 along the drag. That unit is a placemarker the script hides (ViktorExpirationTimer: SetNoRender with
    // SetForceRenderParticles) of a character the client renders, so the client draws particles bound to it: both effects must stay bound to it
    // on the wire (sent unbound they stood at the drag start and drew no trench along the path).
    [Check("scripting.b901-viktor-death-ray-trench-is-bound-to-its-moving-beam")]
    public static void ViktorDeathRayTrenchIsBoundToItsMovingBeam()
    {
        using var match = new Match("Viktor", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Hero.Level = 6;
        match.Learn(2);
        match.Refill();
        var wire = Observe(match, Team.Order, match.Hero);
        // The enemy watches too: unbound, both effects reached every view; bound, each view must know the sproutling to get them.
        var enemy = Observe(match, Team.Chaos, match.Foe, id: 9002);
        match.Advance(0.2);
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "Death Ray is cast: " + match.CanCast(2, match.Foe).Reason);
        Entity? beam = null;
        for (var step = 0; step < 10 && beam is null; step++)
        {
            match.Advance(Step);
            beam = match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Name == "MaokaiSproutling" && unit.Alive);
        }
        TestAssert.True(beam is not null, "the Death Ray spawns its MaokaiSproutling");
        match.Advance(0.3);
        foreach (var (client, capture) in new[] { ("Viktor's", wire), ("the enemy's", enemy) })
            foreach (var name in new[] { "ViktorEntropicBeam_tar_beam", "ViktorEntropicBeam_tar" })
            {
                var create = capture.Packets.Select(item => item.Packet).FirstOrDefault(packet => EffectNamed(packet, name));
                TestAssert.True(create is not null, $"{name}.troy reached {client} client");
                var bind = BinaryPrimitives.ReadUInt32LittleEndian(create!.Plaintext.Span[29..]);
                TestAssert.Equal(beam!.NetId, bind, $"{name}.troy is bound to the moving MaokaiSproutling on {client} client (bind net id)");
            }
        // The beam from Viktor's hand keeps its own bind (Viktor) and its target (the sproutling): unchanged by the rule.
        var ray = wire.Packets.Select(item => item.Packet).First(packet => EffectNamed(packet, "ViktorEntropicBeam_beam"));
        TestAssert.Equal(match.Hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(ray.Plaintext.Span[29..]), "the beam is bound to Viktor");
        TestAssert.Equal(beam!.NetId, BinaryPrimitives.ReadUInt32LittleEndian(ray.Plaintext.Span[21..]), "the beam targets the sproutling");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // AhriFoxFire's SelfExecute casts three AhriFoxFireMissile (CastType 4, Instacast) around Ahri. An Instacast missile child reaches every view as its
    // missile's creation alone (batch 582), so each client, Ahri's own and the enemy's, must get the three circle missiles' 0x41 (the orbiting fox-fires).
    [Check("scripting.b901-ahri-fox-fire-orbs-reach-every-client")]
    public static void AhriFoxFireOrbsReachEveryClient()
    {
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        match.Learn(1);
        match.Refill();
        match.Foe.Position = match.Hero.Position + new Position2(600, 0);
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, id: 9002);
        match.Advance(0.2);
        TestAssert.True(match.Cast(1), "W is cast");
        // Past the fox-fires' 5 s life: each client must have built each orb exactly once (a second creation builds a second orb, batch 582).
        match.Advance(5.5);
        var spell = SpellSystem.ProtocolHash("AhriFoxFireMissile");
        foreach (var (name, wire) in new[] { ("Ahri's", own), ("the enemy's", enemy) })
        {
            var creations = wire.Packets.Select(item => MissileOf(item.Packet)).Where(missile => missile is { } found && found.Spell == spell)
                .Select(missile => missile!.Value.Missile).ToArray();
            TestAssert.Equal(3, creations.Distinct().Count(), $"{name} client gets the three orbiting fox-fires (AhriFoxFireMissile)");
            TestAssert.Equal(3, creations.Length, $"{name} client builds each fox-fire once (no second 0x41 or cast answer)");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
