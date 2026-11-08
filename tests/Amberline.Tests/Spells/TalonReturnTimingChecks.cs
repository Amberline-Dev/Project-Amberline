using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 713 (owner, 8 November 2026: "Talon R seems fine? But sometimes ends early"). A returning blade is an Instacast tracking line: each client
    /// builds it at its 0x41's position when that packet arrives and flies it at Talon, and the blade's 0x5D removes it at once. The
    /// server must not be ahead of that client blade: the 0x5D may only come once a blade that left with the 0x41 has flown to where Talon stands.
    /// At the production step (1/30 s), for Rake and Shadow Assault (the stealth expiring, and ended by Talon's attack), with Talon standing and walking
    /// toward the blades, every view's 0x5D of every return comes no earlier than 1850 units a second from the 0x41's point to Talon's place at that
    /// step. Before the fix each blade flew the rest of its launch step on the server and its 0x5D came one step (62 units) early.
    /// </summary>
    [Check("spells-b713-talon-returning-blades-end-no-earlier-than-the-client-blade")]
    public static void TalonReturningBladesEndNoEarlierThanTheClientBlade()
    {
        const double step = 1.0 / 30.0;
        foreach (var (slot, name, attack, walk) in new[]
                 {
                     (1, "TalonRakeMissileTwo", false, false), (1, "TalonRakeMissileTwo", false, true),
                     (3, "TalonShadowAssaultMisTwo", false, false), (3, "TalonShadowAssaultMisTwo", true, false),
                     (3, "TalonShadowAssaultMisTwo", false, true),
                 })
        {
            using var match = new Match("Talon", Team.Order, ally: true);
            var talon = match.Hero(0);
            var foe = match.Hero(1);
            talon.Position = new Position2(7000, 7000);
            // The foe stands away from the blade lines; Talon's attack (the R case) is the one that ends the stealth.
            foe.Position = new Position2(7120, 6880);
            match.Hero(2).Position = new Position2(6600, 6600);
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();
            match.Clear();
            if (slot == 1) match.CastGround(0, new Position2(7000, 7600), slot: 1);
            else match.Cast(0, 3, talon);
            var hash = SpellSystem.ProtocolHash(name);
            var launches = match.Captures.Select(_ => new Dictionary<uint, (double Time, double X, double Z)>()).ToArray();
            var checkedEnds = 0;
            var trace = new List<(uint Missile, double Time, bool Launched, double X, double Z)>();
            var time = 0.0;
            var attacked = false;
            var walking = false;
            for (var tick = 0; tick < 150; tick++)
            {
                for (var view = 0; view < match.Captures.Count; view++)
                    foreach (var packet in match.Captures[view].Packets)
                    {
                        var opcode = Opcode(packet);
                        if (opcode == 0x41 && ChildSpellHash(packet) == hash && !launches[view].ContainsKey(MissileNetId(packet)))
                        {
                            var start = Coordinates(Decode(packet).GetProperty("position"));
                            launches[view][MissileNetId(packet)] = (time, start[0], start[2]);
                        }
                        else if (opcode == 0x5D && launches[view].TryGetValue(Decode(packet).GetProperty("missile_netid").GetUInt32(), out var launch))
                        {
                            var flown = 1850.0 * (time - launch.Time);
                            var distance = Math.Sqrt(Math.Pow(talon.Position.X - launch.X, 2) + Math.Pow(talon.Position.Z - launch.Z, 2));
                            TestAssert.True(flown >= distance - 1.0,
                                $"{name} ({(attack ? "attack" : "expiry")}{(walk ? ", walking" : "")}): view {view}'s 0x5D comes when a blade that left with its 0x41 has flown {flown:0} of the {distance:0} units to Talon; trace {string.Join(" ", trace.Where(entry => entry.Missile == Decode(packet).GetProperty("missile_netid").GetUInt32()).Select(entry => $"{entry.Time:0.000}:{entry.Launched}:{entry.X:0},{entry.Z:0}"))} 0x41 at {launch.Time:0.000} ({launch.X:0},{launch.Z:0})");
                            checkedEnds++;
                        }
                    }
                match.Clear();
                // Talon walks toward the blades as they turn back: the W cone lies north, the R ring all around (he walks north into it).
                if (walk && !walking && launches[0].Count > 0)
                {
                    match.Move(0, new Position2(7000, 7900));
                    walking = true;
                }
                // R: an attack on the foe 1.2 s into the stealth ends it.
                if (attack && !attacked && time >= 1.2)
                {
                    match.Attack(0, foe);
                    attacked = true;
                }
                match.Runtime.Tick(step);
                match.Runtime.Delivery.Flush();
                time += step;
                foreach (var snapshot in match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot).Where(snapshot => snapshot.SpellName == name))
                    trace.Add((snapshot.MissileNetId, time, snapshot.Launched, snapshot.Position.X, snapshot.Position.Z));
            }
            var blades = slot == 1 ? 3 : 8;
            TestAssert.Equal(blades * match.Captures.Count, checkedEnds, $"{name} ({(attack ? "attack" : "expiry")}{(walk ? ", walking" : "")}): every view sees every blade end");
        }
    }
}
