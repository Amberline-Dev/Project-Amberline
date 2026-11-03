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

    private static double[] Coordinates(JsonElement point) => point.EnumerateArray().Select(value => value.GetDouble()).ToArray();
}
