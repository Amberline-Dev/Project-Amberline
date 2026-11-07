using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 709 (batch 707's open question 1): the return loop of Rake (<c>TalonRakeMissileOne</c>) and of Shadow Assault
    /// (<c>TalonShadowAssaultMisOne</c>) ends each blade's placemarker (a <c>TestCube</c>, <c>Placemarker</c> true) after it takes the marker off:
    /// <c>SetInvulnerable</c> false, <c>SetTargetable</c> true, then <c>BBApplyDamage</c> 50000 physical, <c>DAMAGESOURCE_DEFAULT</c>, with the cube as
    /// both attacker and target. The engine refused that hit under its Protected flag (only a unit's own InternalRaw hit passed, batch 246), so each
    /// W left three cubes and each R eight, on the server and on every client told of them. W, R and W again: every cube dies once its blade has
    /// been sent back, and every view told of a cube is told of its death.
    /// </summary>
    [Check("spells-b709-talon-blade-placemarkers-die-when-their-blades-return")]
    public static void TalonBladePlacemarkersDieWhenTheirBladesReturn()
    {
        using var match = new Match("Talon", Team.Order, ally: true);
        var talon = match.Hero(0);
        talon.Position = new Position2(7000, 7000);
        match.Hero(1).Position = new Position2(7300, 6700);
        match.Hero(2).Position = new Position2(6800, 6800);
        match.Runtime.Tick(0.1);
        match.Runtime.Delivery.Flush();
        match.Clear();
        var cubes = new HashSet<uint>();
        var packets = match.Captures.Select(_ => new List<PlaintextPacket>()).ToArray();
        void Run(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.05)
            {
                match.Runtime.Tick(0.05);
                match.Runtime.Delivery.Flush();
                foreach (var unit in match.Runtime.World.Entities.Values)
                    if (unit is { Placemarker: true, Name: "TestCube" } && unit.Team == talon.Team) cubes.Add(unit.NetId);
                for (var view = 0; view < match.Captures.Count; view++) packets[view].AddRange(match.Captures[view].Packets);
                match.Clear();
            }
        }
        match.CastGround(0, new Position2(7000, 7600), slot: 1);
        Run(3.0);
        match.Cast(0, 3, talon);
        Run(4.5);
        talon.SpellCooldowns[1] = 0.0;
        talon.Mana = talon.MaxMana;
        match.CastGround(0, new Position2(7000, 7600), slot: 1);
        Run(3.0);
        TestAssert.Equal(3 + 8 + 3, cubes.Count, "W, R and W leave three, eight and three blade placemarkers");
        var alive = cubes.Where(id => match.Runtime.World.Get(id) is { Alive: true }).ToArray();
        TestAssert.Equal(0, alive.Length, "the server: every blade's placemarker dies by the script's 50000 hit; still alive: " +
            string.Join(",", alive.Select(id => id.ToString("X8"))));
        var told = 0;
        for (var view = 0; view < packets.Length; view++)
            foreach (var cube in cubes)
            {
                var creations = packets[view].Count(packet => packet.EntityReferences.Any(reference =>
                    reference.Role == LeaguePacketEntityRole.Create && reference.NetId == cube));
                var deaths = packets[view].Count(packet => Opcode(packet) is 0xA6 or 0x61 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == cube);
                told += creations;
                TestAssert.True(creations <= 1, $"view {view}: cube {cube:X8} is created once ({creations} creations)");
                TestAssert.Equal(creations, deaths, $"view {view}: a client told of cube {cube:X8} is told of its death once");
            }
        TestAssert.True(told > 0, "the clients are told of the cubes (announced placemarkers), so the deaths above are checked");
    }
}
