using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 695 (the seed-9801 Dominion bot match's packet checker: duplicate-creation of 0x4003004D, a Howling Gale TestCube, for all ten
    /// peers). The cube is an announced placemarker (Entity.Announced): the entity paths create it, update it and send its death. Its wave was
    /// cast through the temporary caster projection, which retired the cube at the wave's end with an 0xA6 while the cube lived on (it lives four
    /// seconds, the wave less than two), and the next visibility refresh created it again. Every client is told of the cube once and of its
    /// death once, when it dies.
    /// </summary>
    [Check("replication.placemarker-line-caster-is-created-once-and-dies-once")]
    public static void PlacemarkerLineCasterIsCreatedOnceAndDiesOnce()
    {
        using var match = new Match("Janna", Team.Order);
        var janna = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = janna.Position + new Position2(900, 0);
        match.Cast(0, 0, enemy);
        match.Cast(0, 0, enemy);
        var cube = JannaCubes(match).Single();
        TestAssert.True(cube.Announced, "the TestCube is an announced placemarker");
        TestAssert.True(JannaWaveMissile(match) != 0, "the release launches the wave from the cube");
        double? waveEnd = null, cubeDeath = null;
        for (var elapsed = 0.0; elapsed < 6.0 && cubeDeath is null; elapsed += 0.01)
        {
            match.Runtime.Tick(0.01);
            if (waveEnd is null && JannaWaveMissile(match) == 0) waveEnd = elapsed;
            if (!cube.Alive) cubeDeath = elapsed;
        }
        TestAssert.True(waveEnd is not null && cubeDeath is not null && waveEnd < cubeDeath,
            $"the cube outlives its wave (wave end {waveEnd}, cube death {cubeDeath})");
        for (var seat = 0; seat < match.Captures.Count; seat++)
        {
            var packets = match.Captures[seat].Packets;
            var creations = packets.Count(packet => packet.EntityReferences.Any(reference =>
                reference.Role == LeaguePacketEntityRole.Create && reference.NetId == cube.NetId));
            var deaths = packets.Count(packet => Opcode(packet) is 0xA6 or 0x61 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == cube.NetId);
            TestAssert.True(creations <= 1, $"seat {seat}: the cube is created once ({creations} creations)");
            TestAssert.Equal(creations, deaths, $"seat {seat}: a client told of the cube gets its death once");
        }
    }
}
