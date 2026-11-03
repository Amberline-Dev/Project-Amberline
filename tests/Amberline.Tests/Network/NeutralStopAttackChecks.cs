using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// Night 2 (17 Oct 2026), second pass of the stop-attack rule (<see cref="CombatPackets.StopAttack"/>): a jungle monster's client
/// runs it as an obj_AI_Minion, which repeats the last attack and erases its path until a stop-attack (0x39) ends the attack state.
/// So a monster is stopped exactly while it has attacked, with DestroyMissile alone (0x10) for a swing the server dropped and
/// nothing (0x00) for a hit it still applies; the monsters' own wire (<c>NeutralWire.StopAttack</c>) used to send 0x12 every time.
/// </summary>
public static partial class NeutralAiChecks
{
    private const byte StopAttackOpcode = 0x39;
    private const byte BasicAttackOpcode = 0x0D;


    private static List<byte> StopFlags(NeutralHarness harness, Entity monster) => harness.Queue.Packets
        .Where(packet => packet.Plaintext.Span[0] == StopAttackOpcode &&
                         BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == monster.NetId)
        .Select(packet => packet.Plaintext.Span[5]).ToList();

    private static int Swings(NeutralHarness harness, Entity monster) => harness.Queue.Packets.Count(packet =>
        packet.Plaintext.Span[0] == BasicAttackOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == monster.NetId);

}
