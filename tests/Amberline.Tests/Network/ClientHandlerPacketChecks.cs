using System.Buffers.Binary;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests.Network;

/// <summary>
/// Byte layouts of the two stock-client packet handlers this server now feeds (G14): UnitApplyHeal (0x84)
/// and NpcBuffUpdateCount (0x1F). Offsets come from the client's own writers and readers.
/// </summary>
public static class ClientHandlerPacketChecks
{
    private const uint Unit = 0x40010001, Caster = 0x40010002;

    [Check("protocol.unit-apply-heal-layout")]
    public static void UnitApplyHealMatchesIncHealthWriter()
    {
        // The client's heal writer builds the packet like this:
        // byte [0] = 0x84, float at +5 = the target's max health, float at +9 = the heal argument,
        // size 0xd, sent through the unit's visibility object (net ID filled at +1 by the sender).
        var packet = CombatPackets.UnitApplyHeal(Unit, 2500.5f, 150.25f);
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(13, bytes.Length, "IncHealth sends 0xd bytes");
        TestAssert.Equal((byte)0x84, bytes[0]);
        TestAssert.Equal(Unit, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "net ID at +1");
        TestAssert.Equal(2500.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes[5..]), "max health at +5");
        TestAssert.Equal(150.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]), "heal at +9");
        TestAssert.Equal(LeagueProtocolConstants.CombatChannel, packet.Channel);
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
        TestAssert.Equal(Unit, packet.EntityReferences.Single().NetId, "routes by the healed unit");

        // The client's packet handler is a bare "return true",
        // so only the wire fields above matter; the diagnostics decoder reads the same offsets.
        var decoded = WirePacketDecoder.Decode(bytes, packet.Channel);
        TestAssert.Equal("UnitApplyHeal", decoded.Name);
        TestAssert.True(decoded.HasDecoder, "decoder registered");
        TestAssert.Equal(150.25f, Convert.ToSingle(decoded.Fields["heal"]));
        TestAssert.Equal(2500.5f, Convert.ToSingle(decoded.Fields["max_health"]));

        TestAssert.Throws<ArgumentOutOfRangeException>(() => CombatPackets.UnitApplyHeal(Unit, float.NaN, 1f));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => CombatPackets.UnitApplyHeal(Unit, 1f, float.PositiveInfinity));
    }

    [Check("packet-rules.heal-and-buff-count-packets")]
    public static void PacketStreamCheckerKnowsTheNewPackets()
    {
        const uint hero = 0x40000001, stranger = 0x40010009;
        var heroStats = CombatPackets.HeroStats(hero, new HeroStatsSnapshot(AttackDamage: 50, Armor: 20, SpellBlock: 30,
            MaxHp: 500, MaxMp: 300, MoveSpeed: 325, AttackRange: 575, AttackSpeedMod: 0.7f), 1);
        var create = SpawnPackets.CreateHero(hero, 1, "Annie", "Annie", teamIsOrder: true);
        var heal = CombatPackets.UnitApplyHeal(hero, 500f, 40f);
        var count = SpellPackets.UpdateBuffCount(hero, 3, 2, 5f, hero);

        // Positive: both packets on their catalog channel and delivery, about a unit the peer knows.
        var good = new PacketStreamChecker(1);
        foreach (var packet in new[] { create, heroStats, heal, count })
            good.Observe(PacketRuleChecks.Observation(packet));
        TestAssert.Equal(0, good.Finish().Count, "known unit, catalog channel and delivery");

        // Negative: an unknown healed unit and an unknown caster are found at the offsets the catalog declares,
        // and a different channel or delivery is reported.
        var bad = new PacketStreamChecker(1);
        foreach (var packet in new[] { create, heroStats, CombatPackets.UnitApplyHeal(stranger, 500f, 40f),
                     SpellPackets.UpdateBuffCount(hero, 3, 2, 5f, stranger) })
            bad.Observe(PacketRuleChecks.Observation(packet));
        var unknown = bad.Finish().Where(violation => violation.Kind == PacketRuleKinds.UnknownUnit).ToArray();
        TestAssert.Equal(new byte[] { 0x84, 0x1F }, unknown.Select(violation => violation.Opcode).ToArray());
        TestAssert.Equal(new uint?[] { stranger, stranger }, unknown.Select(violation => violation.NetId).ToArray());

        var wrong = new PacketStreamChecker(1);
        foreach (var packet in new[] { create, heroStats })
            wrong.Observe(PacketRuleChecks.Observation(packet));
        wrong.Observe(new PacketObservation(1.0, 0, heal.Channel, false, heal.Plaintext));
        wrong.Observe(new PacketObservation(1.0, 0, 4, true, count.Plaintext));
        TestAssert.Equal(new byte[] { 0x84, 0x1F },
            wrong.Finish().Where(violation => violation.Kind == PacketRuleKinds.ChannelOrReliability)
                .Select(violation => violation.Opcode).ToArray());
    }

    [Check("protocol.buff-update-count-layout")]
    public static void BuffUpdateCountMatchesClientReader()
    {
        // The client's buff manager reads: slot byte at +5 (index check < 0x40), count byte at +6,
        // duration float at +7, running-time float at +11, and the caster net ID at +15 for the script
        // Renew. NpcBuffUpdateCount: {buffSlot, count, duration, runningTime, casterNetID}.
        var packet = SpellPackets.UpdateBuffCount(Unit, slot: 9, count: 3, duration: 7.5f, Caster, runningTime: 1.25f);
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(19, bytes.Length);
        TestAssert.Equal((byte)0x1F, bytes[0]);
        TestAssert.Equal(Unit, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "unit at +1");
        TestAssert.Equal((byte)9, bytes[5], "slot at +5");
        TestAssert.Equal((byte)3, bytes[6], "count at +6");
        TestAssert.Equal(7.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes[7..]), "duration at +7");
        TestAssert.Equal(1.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes[11..]), "running time at +11");
        TestAssert.Equal(Caster, BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]), "caster at +15");
        TestAssert.Equal(LeagueProtocolConstants.CombatChannel, packet.Channel);
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
        TestAssert.Equal(new[] { Unit, Caster }, packet.EntityReferences.Select(reference => reference.NetId).ToArray());
        TestAssert.Equal(1, SpellPackets.UpdateBuffCount(Unit, 0, 1, 0f, 0).EntityReferences.Count,
            "no caster, no caster reference");

        var decoded = WirePacketDecoder.Decode(bytes, packet.Channel);
        TestAssert.Equal("BuffUpdateCount", decoded.Name);
        TestAssert.Equal(3, Convert.ToInt32(decoded.Fields["count"]));
        TestAssert.Equal(7.5f, Convert.ToSingle(decoded.Fields["duration"]));

        // Count zero would clear the instance on the client (it resizes, then clears it); a slot past the 64-entry table
        // trips the buff manager's index check. Neither is a stack update.
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SpellPackets.UpdateBuffCount(Unit, 1, 0, 1f, Caster));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SpellPackets.UpdateBuffCount(Unit, 64, 1, 1f, Caster));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SpellPackets.UpdateBuffCount(Unit, 1, 1, -1f, Caster));

        // The 0xBF -> 0x1F rewrite reads an add back through the same offsets AddBuff wrote.
        var add = SpellPackets.AddBuff(new BuffAdd(Unit, 9, 5, 0x1234ABCD, 7.5f, Caster, Count: 3, IsHidden: 1,
            RunningTime: 1.25f));
        TestAssert.True(SpellPackets.TryReadBuffAdd(add.Plaintext.Span, out var read), "0xBF reads back");
        TestAssert.Equal(new BuffAdd(Unit, 9, 5, 0x1234ABCD, 7.5f, Caster, 3, 1, 1.25f), read);
        TestAssert.False(SpellPackets.TryReadBuffAdd(bytes, out _), "a 0x1F is not an add");

        // A same-count 0xBF for a held slot is rewritten to the silent renewal 0x32 (BuffManager_pimpl: restarts the slot's timers,
        // start = now - running, end = start + duration; slot @5, running @6, duration @10, caster @14).
        var renewal = SpellPackets.RenewBuff(read.TargetNetId, read.Slot, read.Duration, read.CasterNetId, read.RunningTime);
        var renewalBytes = renewal.Plaintext.Span;
        TestAssert.Equal((byte)0x32, renewalBytes[0]);
        TestAssert.Equal(read.TargetNetId, BinaryPrimitives.ReadUInt32LittleEndian(renewalBytes[1..]), "unit at +1");
        TestAssert.Equal((byte)9, renewalBytes[5]);
        TestAssert.Equal(1.25f, BinaryPrimitives.ReadSingleLittleEndian(renewalBytes[6..]), "running time at +6");
        TestAssert.Equal(7.5f, BinaryPrimitives.ReadSingleLittleEndian(renewalBytes[10..]), "duration at +10");
        TestAssert.Equal(Caster, BinaryPrimitives.ReadUInt32LittleEndian(renewalBytes[14..]), "caster at +14");
    }
}
