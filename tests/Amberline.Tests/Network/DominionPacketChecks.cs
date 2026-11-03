using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests.Network;

/// <summary>
/// Byte-exact checks of the Dominion capture and score writers. Each expected array is written from the offsets decoded
/// from the client dispatcher, not from the writer, so a wrong offset fails here.
/// </summary>
public static class DominionPacketChecks
{
    [Check("dominion.capture-packet-layouts")]
    public static void CapturePacketLayouts()
    {
        // 0xDA: netid at byte 0x4D, mode 1 at byte 0x55, flag 0 at byte 0x56, value at byte 0x58; length 0x59.
        var expected = new byte[0x59];
        expected[0] = 0xDA;
        PutU32(expected, 0x4D, 0x40000001u);
        expected[0x55] = 1;
        expected[0x58] = 77;
        Expect(DominionPackets.CapturePropFlexValue(0x40000001u, 77), expected, "0xDA");

        // 0xDB: netid at 5, event at 9, value at 10, mode at 11 (0 constant, 1 PAR-driven); length 12.
        expected = new byte[12];
        expected[0] = 0xDB;
        PutU32(expected, 5, 0x40000002u);
        expected[9] = 3;
        expected[10] = 60;
        Expect(DominionPackets.CapturePointEvent(0x40000002u, 3, 60, parFlex: false), expected, "0xDB constant");
        expected[11] = 1;
        Expect(DominionPackets.CapturePointEvent(0x40000002u, 3, 60, parFlex: true), expected, "0xDB PAR");

        // 0xDC: capture index at 5, netid at 6, value at 10, mode at 15 (0 apply, other = log only); length 16.
        expected = new byte[16];
        expected[0] = 0xDC;
        expected[5] = 2;
        PutU32(expected, 6, 0x40000003u);
        expected[10] = 40;
        Expect(DominionPackets.CapturePointValue(2, 0x40000003u, 40, apply: true), expected, "0xDC apply");
        expected[15] = 1;
        Expect(DominionPackets.CapturePointValue(2, 0x40000003u, 40, apply: false), expected, "0xDC log only");

        // 0xDD: team at 5 (100 Order, 200 Chaos), signed score at 9; length 13.
        expected = new byte[13];
        expected[0] = 0xDD;
        PutU32(expected, 5, 100);
        PutU32(expected, 9, unchecked((uint)-5));
        Expect(DominionPackets.SetGameScore(Team.Order, -5), expected, "0xDD order");
        PutU32(expected, 5, 200);
        Expect(DominionPackets.SetGameScore(Team.Chaos, -5), expected, "0xDD chaos");

        // 0xDE command 0: command 0 at 5, point at 6, netid at 7; length 11.
        expected = new byte[11];
        expected[0] = 0xDE;
        expected[6] = 4;
        PutU32(expected, 7, 0x40000004u);
        Expect(DominionPackets.CreateRespawnPoint(4, 0x40000004u), expected, "0xDE create");

        // 0xDE command 1: command 1 at 5, state at 6, netid at 11; length 15.
        expected = new byte[15];
        expected[0] = 0xDE;
        expected[5] = 1;
        expected[6] = 2;
        PutU32(expected, 11, 0x40000005u);
        Expect(DominionPackets.SetRespawnPointState(0x40000005u, 2), expected, "0xDE state");

        // 0xE3 (batch 467): hero netid at 5, stat index at 9 (1 NODE_NEUTRALIZE in the client's stat table); length 10.
        expected = new byte[10];
        expected[0] = 0xE3;
        PutU32(expected, 5, 0x40000006u);
        expected[9] = 1;
        Expect(DominionPackets.IncrementPlayerStat(0x40000006u, DominionPackets.NodeNeutralizeStat), expected, "0xE3 neutralise");

        // 0xE2 (batch 518): hero netid at 5, category at 9 (2 combat), score event at byte 0xA (14 CHAMPION_KILL), callout bit at byte 0xB, float
        // points at byte 0xC (15.0 = 0x41700000); length 16.
        expected = new byte[16];
        expected[0] = 0xE2;
        PutU32(expected, 5, 0x40000007u);
        expected[9] = 2;
        expected[10] = 14;
        expected[11] = 1;
        PutU32(expected, 12, 0x41700000u);
        Expect(DominionPackets.IncrementPlayerScore(0x40000007u, DominionPackets.CombatScore, 14, callout: true, 15f), expected,
            "0xE2 champion kill");
    }

    private static void Expect(PlaintextPacket packet, byte[] expected, string what)
    {
        var actual = packet.Plaintext.Span;
        TestAssert.True(actual.SequenceEqual(expected),
            $"{what} plaintext {Convert.ToHexString(actual)} vs expected {Convert.ToHexString(expected)}");
        TestAssert.True(packet.Channel == LeagueProtocolConstants.CombatChannel, $"{what} channel {packet.Channel}");
        TestAssert.True(packet.Reliability == LeaguePacketReliability.Reliable, $"{what} delivery {packet.Reliability}");
    }

    private static void PutU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
