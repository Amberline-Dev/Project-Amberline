using System;
using System.Linq;
using System.Text.Json;
using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 433 (owner native 28 October, Talon R: "at least 2x too far"): Shadow Assault casts its eight blades as children with
    /// OverrideForceLevel 1. The wire's level is the rank minus one (SpellPackets.BuildCastInfo), and the client's level table holds
    /// the base CastRange first and CastRange1..6 after it, so a blade at level 0 flies the first entry, 575, not CastRange1 (1200).
    /// </summary>
    [Check("spells-b433-talon-shadow-assault-blades-fly-the-first-entry-range")]
    public static void TalonShadowAssaultBladesFlyTheFirstEntryRange()
    {
        using var match = new Match("Talon", Team.Order);
        var hero = match.Hero(0);
        match.Runtime.Delivery.Flush();
        match.Clear();
        match.Cast(0, 3, hero);
        RunSpellTenths(match, 20);
        var hash = SpellSystem.ProtocolHash("TalonShadowAssaultMisOne");
        var blades = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x41 && ChildSpellHash(packet) == hash).ToArray();
        TestAssert.Equal(8, blades.Length, "the eight blades of Shadow Assault leave Talon as their missiles (0x41)");
        foreach (var blade in blades)
        {
            var fields = Decode(blade);
            var start = Coordinates(fields.GetProperty("start_point"));
            var end = Coordinates(fields.GetProperty("end_point"));
            var flight = Math.Sqrt(Math.Pow(end[0] - start[0], 2) + Math.Pow(end[2] - start[2], 2));
            TestAssert.Near(575.0, flight, 2.0,
                $"a blade flies the first entry of its level table (575, the base CastRange), not CastRange1 (1200); it flew {flight:0.#}");
        }
    }

    /// <summary>
    /// Batch 690 (community report: "Talon W particles dissapear mid air"): the stock client moves a line missile with no acceleration by
    /// the 0x41's velocity times the frame time (the client, the velocity the 0x41 handler the client stores; the speed field only feeds
    /// the acceleration branch), so the velocity is per second. Talon's own client takes Rake's outgoing blades as their 0x41 alone, a live
    /// refresh one update after the launch, which carried the remaining path (about 700) as the velocity: the blade crept at a third of its
    /// 2300 and the 0x5D at the end of its flight took it about 300 out. Every blade's velocity is its speed along its line.
    /// </summary>
    [Check("spells-b690-talon-rake-blades-carry-their-speed-as-velocity")]
    public static void TalonRakeBladesCarryTheirSpeedAsVelocity()
    {
        using var match = new Match("Talon", Team.Order);
        var hero = match.Hero(0);
        match.Runtime.Delivery.Flush();
        match.Clear();
        match.CastGround(0, new Position2(hero.Position.X + 400, hero.Position.Z), slot: 1);
        RunSpellTenths(match, 20);
        foreach (var (name, speed) in new[] { ("TalonRakeMissileOne", 2300.0), ("TalonRakeMissileTwo", 1850.0) })
        {
            var hash = SpellSystem.ProtocolHash(name);
            var blades = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x41 && ChildSpellHash(packet) == hash).ToArray();
            TestAssert.Equal(3, blades.Length, $"three {name} blades reach Talon's client as their 0x41");
            foreach (var blade in blades)
            {
                var fields = Decode(blade);
                var velocity = Coordinates(fields.GetProperty("velocity"));
                var horizontal = Math.Sqrt(velocity[0] * velocity[0] + velocity[2] * velocity[2]);
                TestAssert.Near(speed, fields.GetProperty("speed").GetDouble(), 1.0, $"{name}: the speed field");
                TestAssert.Near(speed, horizontal, 1.0, $"{name}: the client moves the blade by its velocity per second; it was {horizontal:0.#}");
            }
        }
    }

    private static double[] Coordinates(JsonElement point) => point.EnumerateArray().Select(value => value.GetDouble()).ToArray();
}
