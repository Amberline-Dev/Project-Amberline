using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests.Spells;

public static class ChampionCastWireChecks
{
    [Check("spells-champion-targeting-type-packet")]
    public static void NativeTargetingTypePacket()
    {
        var self = SpellPackets.ChangeSlotSpellType(0x1234, 0, targetingType: 0);
        TestAssert.Equal(new byte[] { 0x1A, 0x34, 0x12, 0, 0, 0, 0 },
            self.Plaintext.ToArray(), "Janna Q self recast targeting packet");

        var location = SpellPackets.ChangeSlotSpellType(0x1234, 0, targetingType: 6);
        TestAssert.Equal((byte)6, location.Plaintext.Span[^1],
            "Janna Q restores location targeting after release");
    }

    [Check("spells-champion-missile-caster-position")]
    public static void MissileCarriesOriginAndCasterSeparately()
    {
        var info = new SpellCastInfo(1, 2, 1, 3, 4,
            new Position3(10, 0, 10), new Position3(20, 0, 20),
            Array.Empty<SpellCastTarget>(), 1, 0, 0, new Position3(10, 0, 10));
        var packet = SpellPackets.MissileReplication(3,
            new Position3(10, 0, 10), new Position3(99, 0, 99),
            new Position3(1, 0, 0), new Position3(500, 0, 0),
            new Position3(10, 0, 10), new Position3(200, 0, 10),
            new Position3(10, 0, 10), 500, 0, 0, info);
        var payload = packet.Plaintext.Span;
        TestAssert.Near(10, BinaryPrimitives.ReadSingleLittleEndian(payload[5..]), 0,
            "missile origin remains at the charge origin");
        TestAssert.Near(99, BinaryPrimitives.ReadSingleLittleEndian(payload[17..]), 0,
            "missile caster position follows the live caster");
    }
}
