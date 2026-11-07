using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 707 (owner, 7 November 2026: "Talon issues: W VFX disappears when coming back; R VFX disappears too quickly when coming back (should
    /// fly into talon)"). Rake's and Shadow Assault's return blades (TalonRakeMissileTwo, TalonShadowAssaultMisTwo) are tracking lines aimed at
    /// Talon; their TargetExecute destroys them on him (Target == Owner). The stock update meets the
    /// tracked unit when the line reaches it; the server met Talon by its width sweep, 75 + 65 and 250 + 65 units out, and the 0x5D removed
    /// each blade there on every client (W about 0.08 s, R about 0.17 s early). Every view (Talon's own, the ally's, the enemy's) must see each
    /// blade's 0x5D only once it has flown the whole way back at 1850, after the hit list that names Talon; a foe on the way is still struck.
    /// </summary>
    [Check("spells-b707-talon-returning-blades-fly-into-talon")]
    public static void TalonReturningBladesFlyIntoTalon()
    {
        foreach (var (slot, name) in new[] { (1, "TalonRakeMissileTwo"), (3, "TalonShadowAssaultMisTwo") })
        {
            using var match = new Match("Talon", Team.Order, ally: true);
            var talon = match.Hero(0);
            var foe = match.Hero(1);
            talon.Position = new Position2(7000, 7000);
            // The foe stands on the south-east return line of the ring (R) and away from Rake's cone (W, cast north).
            foe.Position = new Position2(7300, 6700);
            match.Hero(2).Position = new Position2(6800, 6800);
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();
            match.Clear();
            if (slot == 1) match.CastGround(0, new Position2(7000, 7600), slot: 1);
            else match.Cast(0, 3, talon);
            var hash = SpellSystem.ProtocolHash(name);
            var launches = match.Captures.Select(_ => new Dictionary<uint, (double Time, double Distance)>()).ToArray();
            var ends = match.Captures.Select(_ => new Dictionary<uint, double>()).ToArray();
            var hits = match.Captures.Select(_ => new List<(uint Missile, uint Unit)>()).ToArray();
            var time = 0.0;
            for (var step = 0; step < 90; step++)
            {
                for (var view = 0; view < match.Captures.Count; view++)
                    foreach (var packet in match.Captures[view].Packets)
                    {
                        var opcode = Opcode(packet);
                        if (opcode == 0x41 && ChildSpellHash(packet) == hash && !launches[view].ContainsKey(MissileNetId(packet)))
                        {
                            var start = Coordinates(Decode(packet).GetProperty("start_point"));
                            launches[view][MissileNetId(packet)] = (time, Math.Sqrt(Math.Pow(start[0] - talon.Position.X, 2) + Math.Pow(start[2] - talon.Position.Z, 2)));
                        }
                        else if (opcode == 0x5D)
                            ends[view].TryAdd(Decode(packet).GetProperty("missile_netid").GetUInt32(), time);
                        else if (opcode == 0x28)
                        {
                            var fields = Decode(packet);
                            foreach (var unit in fields.GetProperty("targets").EnumerateArray())
                                hits[view].Add((fields.GetProperty("missile_netid").GetUInt32(), unit.GetUInt32()));
                        }
                    }
                match.Clear();
                match.Runtime.Tick(0.05);
                match.Runtime.Delivery.Flush();
                time += 0.05;
            }
            var blades = slot == 1 ? 3 : 8;
            for (var view = 0; view < match.Captures.Count; view++)
            {
                TestAssert.Equal(blades, launches[view].Count, $"{name}: view {view} gets every return blade's 0x41");
                foreach (var (missile, (launched, distance)) in launches[view])
                {
                    TestAssert.True(ends[view].TryGetValue(missile, out var ended), $"{name}: view {view} is told when blade {missile:X8} ends");
                    var flight = distance / 1850.0;
                    // One 0.05 s tick of slack either way: the server's step that reaches Talon ends the blade.
                    TestAssert.True(ended - launched >= flight - 0.06 && ended - launched <= flight + 0.11,
                        $"{name}: view {view} keeps blade {missile:X8} until it reaches Talon ({distance:0} units, {flight:0.###} s at 1850); its 0x5D came after {ended - launched:0.###} s");
                    TestAssert.True(hits[view].Contains((missile, talon.NetId)),
                        $"{name}: view {view}: the blade's hit list names Talon, whose TargetExecute destroys it on arrival");
                }
            }
            if (slot == 3)
                TestAssert.True(hits[0].Any(hit => launches[0].ContainsKey(hit.Missile) && hit.Unit == foe.NetId),
                    "a foe standing on a return line is still struck on the way back");
        }
    }
}
