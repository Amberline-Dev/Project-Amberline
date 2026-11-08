using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 713 (owner, 8 November 2026: Talon's W blade "flies towards his feet when going back, meaning it goes underground almost
    /// instantly"). A line missile the client builds from a 0x41 flies at the height of the packet's position over the terrain for its whole
    /// flight (mStartHeightFromGround). The shared DoCast_ClientAndServer launches a line 100 above a caster
    /// without a joint pose (the server) and an overriding cast at its 3D CasterPos; Talon's markers stand where the outgoing blades ended, at
    /// that height, and his returns leave from them. Every view (Talon's own, the ally's, the enemy's) must get each return's 0x41 with its
    /// position, start and end 100 above the terrain under them and the cast's launch point there too (the unit position stays Talon's ground),
    /// on flat ground and across the river bank; Talon's own client, which builds the outgoing blades from their 0x41, gets them 100 up as well.
    /// Before the fix every point was on the terrain (height 0).
    /// </summary>
    [Check("spells-b713-talon-blades-fly-at-blade-height")]
    public static void TalonBladesFlyAtBladeHeight()
    {
        var navigation = NavigationGrid.LoadDefault();
        double Above(JsonElement point)
        {
            var c = Coordinates(point);
            return c[1] - navigation.HeightAt(new Position2(c[0], c[2]));
        }
        foreach (var (slot, outgoing, returning, blades) in new[]
                 {
                     (1, "TalonRakeMissileOne", "TalonRakeMissileTwo", 3), (3, "TalonShadowAssaultMisOne", "TalonShadowAssaultMisTwo", 8),
                 })
            foreach (var (at, aim, ground) in new[]
                     {
                         (new Position2(7000, 7000), new Position2(7000, 7600), "flat"),
                         // The blades leave the lane (46) for the river (-72) and come back up.
                         Slope(navigation),
                     })
            {
                using var match = new Match("Talon", Team.Order, ally: true);
                var talon = match.Hero(0);
                talon.Position = at;
                match.Hero(1).Position = at + new Position2(300, -300);
                match.Hero(2).Position = at + new Position2(-200, -200);
                match.Runtime.Tick(0.1);
                match.Runtime.Delivery.Flush();
                match.Clear();
                if (slot == 1) match.CastGround(0, aim, slot: 1);
                else match.Cast(0, 3, talon);
                var outHash = SpellSystem.ProtocolHash(outgoing);
                var returnHash = SpellSystem.ProtocolHash(returning);
                var returns = match.Captures.Select(_ => new HashSet<uint>()).ToArray();
                var outgoingSeen = 0;
                var startTerrain = new List<double>();
                for (var step = 0; step < 90; step++)
                {
                    for (var view = 0; view < match.Captures.Count; view++)
                        foreach (var packet in match.Captures[view].Packets.Where(packet => Opcode(packet) == 0x41))
                        {
                            var hash = ChildSpellHash(packet);
                            if (hash != returnHash && !(hash == outHash && view == 0)) continue;
                            var fields = Decode(packet);
                            var name = $"{ground}: view {view}, {(hash == returnHash ? returning : outgoing)} {MissileNetId(packet):X8}";
                            foreach (var field in new[] { "position", "start_point", "end_point" })
                                TestAssert.True(Math.Abs(Above(fields.GetProperty(field)) - 100.0) < 0.01,
                                    $"{name}: its {field} is 100 above the terrain (got {Above(fields.GetProperty(field)):0.##})");
                            TestAssert.True(Math.Abs(Above(fields.GetProperty("unit_position"))) < 0.01,
                                $"{name}: the unit position is Talon's own, on the ground (got {Above(fields.GetProperty("unit_position")):0.##})");
                            if (hash == returnHash)
                            {
                                var launch = Above(fields.GetProperty("cast_info").GetProperty("launch_pos"));
                                TestAssert.True(Math.Abs(launch - 100.0) < 0.01,
                                    $"{name}: the cast's launch point (its marker) is 100 above the terrain (got {launch:0.##})");
                                returns[view].Add(MissileNetId(packet));
                                var start = Coordinates(fields.GetProperty("position"));
                                startTerrain.Add(start[1] - 100.0);
                            }
                            else outgoingSeen++;
                        }
                    match.Clear();
                    match.Runtime.Tick(0.05);
                    match.Runtime.Delivery.Flush();
                }
                // Across the bank the markers stand on other terrain than Talon (the returns climb back up). Talon's own client may not see an
                // outgoing blade there: its first refresh lies below the bank, out of his sight (open question 3 of the record).
                if (ground == "river bank")
                    TestAssert.True(startTerrain.Any(height => Math.Abs(height - navigation.HeightAt(talon.Position)) > 20.0),
                        $"river bank: some markers stand on other terrain than Talon ({string.Join(", ", startTerrain.Distinct().Select(h => h.ToString("0")))} vs {navigation.HeightAt(talon.Position):0})");
                else TestAssert.True(outgoingSeen >= blades,
                    $"{ground}: Talon's own client builds each outgoing {outgoing} from its 0x41 ({outgoingSeen} of {blades}; Talon at {talon.Position})");
                for (var view = 0; view < match.Captures.Count; view++)
                    TestAssert.Equal(blades, returns[view].Count, $"{ground}: view {view} gets every {returning}");
            }
    }

    // A walkable spot whose ground 700 to the north, and all around at Shadow Assault's 575, is walkable and lies at least 40 units lower or higher.
    private static (Position2 At, Position2 Aim, string Ground) Slope(NavigationGrid navigation)
    {
        for (var x = 1000.0; x < 13000.0; x += 125.0)
            for (var z = 1000.0; z < 13000.0; z += 125.0)
            {
                var at = new Position2(x, z);
                var aim = new Position2(x, z + 700.0);
                if (!navigation.IsWalkable(at) || !navigation.IsWalkable(aim) ||
                    Math.Abs(navigation.HeightAt(aim) - navigation.HeightAt(at)) < 40.0) continue;
                if (Enumerable.Range(0, 8).All(i => navigation.IsWalkable(at + new Position2(575.0 * Math.Cos(i * Math.PI / 4), 575.0 * Math.Sin(i * Math.PI / 4)))))
                    return (at, aim, "river bank");
            }
        throw new InvalidOperationException("no slope on the map");
    }
}
