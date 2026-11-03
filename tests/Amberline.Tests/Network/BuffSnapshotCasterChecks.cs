using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// W30 (19 October 2026): a buff packet names its holder and its caster, and the stock client tolerates a caster it cannot find (the
/// 0xBF, 0x32 and 0x1F handlers look it up by net ID and keep the buff on the holder without one). Headless 60-minute matches reported
/// <c>unknown-unit</c> for a 0xBF whose holder was revealed to the team that a hidden caster (Yorick's ghoul or revenant) was never
/// shown to. Delivery now requires the holder and sends an unknown caster as 0, for live adds, renewals, stack updates and the reveal's
/// replay alike (<c>ReplicationView.ForClient</c>, applied in <c>ReplicationDelivery.Send</c>). Written, not run (owner rule: no tests
/// until asked).
/// </summary>
public static class BuffSnapshotCasterChecks
{
    private sealed class Capture : IReplicationTransport
    {
        public readonly List<PlaintextPacket> Packets = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    [Check("replication.buff-caster-unknown-to-the-client-is-sent-as-none")]
    public static void BuffCasterUnknownToTheClientIsSentAsNone()
    {
        PacketRewrite();
        ProductionDelivery();
    }

    /// <summary>The packet rewrite itself, on the three buff packets that carry a caster.</summary>
    private static void PacketRewrite()
    {
        const uint holder = 0x40000001, known = 0x40010001, unknown = 0x40030001;
        var view = new ReplicationView();
        view.Known.Add(holder);
        view.Known.Add(known);
        var hash = SpellPackets.BuffNameHash("Silence");
        PlaintextPacket[] Builders(uint caster) =>
        [
            SpellPackets.AddBuff(new BuffAdd(holder, 3, 1, hash, 5f, caster)),
            SpellPackets.RenewBuff(holder, 3, 5f, caster),
            SpellPackets.UpdateBuffCount(holder, 3, 2, 5f, caster),
        ];

        foreach (var packet in Builders(unknown))
        {
            var opcode = packet.Plaintext.Span[0];
            var offset = SpellPackets.BuffCasterOffset(opcode);
            TestAssert.True(offset > 0, $"0x{opcode:X2} is a buff packet with a caster");
            TestAssert.Equal(unknown, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[offset..]),
                $"0x{opcode:X2} carries the caster the builder was given");
            TestAssert.True(packet.EntityReferences.Any(reference =>
                    reference.Role == LeaguePacketEntityRole.Caster && reference.NetId == unknown),
                $"0x{opcode:X2} declares the caster as optional");

            var sent = view.ForClient(packet);
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(sent.Plaintext.Span[offset..]),
                $"0x{opcode:X2} reaches a client that does not know the caster with none");
            TestAssert.Equal(holder, BinaryPrimitives.ReadUInt32LittleEndian(sent.Plaintext.Span[1..]),
                $"0x{opcode:X2} keeps its holder");
            TestAssert.Equal(1, sent.EntityReferences.Count, $"0x{opcode:X2} names only the holder afterwards");
            TestAssert.Equal(holder, sent.EntityReferences[0].NetId, $"0x{opcode:X2} declares the holder");
            TestAssert.Equal(packet.Plaintext.Length, sent.Plaintext.Length, $"0x{opcode:X2} keeps its length");
            // Nothing but the caster field changed.
            var before = packet.Plaintext.ToArray();
            var after = sent.Plaintext.ToArray();
            for (var index = 0; index < before.Length; index++)
                if (index < offset || index >= offset + 4)
                    TestAssert.Equal(before[index], after[index], $"0x{opcode:X2} byte {index} is untouched");
            TestAssert.Equal(packet.Reliability, sent.Reliability, $"0x{opcode:X2} keeps its delivery");
        }

        // A caster the client holds, and a buff with none, pass through as the very same packet.
        foreach (var packet in Builders(known).Concat(Builders(0)))
            TestAssert.True(ReferenceEquals(packet, view.ForClient(packet)),
                $"0x{packet.Plaintext.Span[0]:X2} with a known or no caster is not rewritten");

        // Only buff packets are rewritten: a damage packet from a unit the client does not know is untouched by the rule.
        var damage = CombatPackets.ApplyDamage(unknown, holder, 10f);
        TestAssert.True(ReferenceEquals(damage, view.ForClient(damage)), "the rule is the buff packets' caster, nothing else");
    }

    /// <summary>
    /// The production delivery: a marker buff on an Order hero is cast by a summon only the Order team was told about. The Chaos
    /// client gets the live add and the reveal's replay with no caster, and nothing it receives names a unit it has not created.
    /// </summary>
    private static void ProductionDelivery()
    {
        var roster = new List<SessionParticipantDefinition>
        {
            new(1, 0, "Holder", "Annie", Team.Order), new(2, 1, "Watcher", "Orianna", Team.Chaos),
        };
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0, roster: roster);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Count; index++)
        {
            var id = index + 1;
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
        }
        TestAssert.True(runtime.Session.Start(), "start");
        var holder = runtime.Session.Participants[0].Hero!;
        var watcher = runtime.Session.Participants[1].Hero!;
        holder.Position = new Position2(7000, 7000);
        watcher.Position = new Position2(7300, 7000);

        // A summon of the holder's team, far from the watcher, so only the Order observer is ever shown it.
        var summon = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Pet,
            UnitType.Monster, Team.Order, new Position2(1500, 1500)) { Name = "W30Summon", OwnerNetId = holder.NetId });
        TestAssert.True(runtime.GameData.HasClientBuff("YorickSummonSpectral"), "the marker is a client buff");
        var marker = SpellPackets.BuffNameHash("YorickSummonSpectral");
        // The state the replay reads, and the live add of the same buff in the same flush.
        holder.Buffs.Add(new BuffState("YorickSummonSpectral", type: BuffType.Internal, slot: 5, expiresAt: 9.0,
            casterNetId: summon.NetId));
        runtime.Delivery.Enqueue(SpellPackets.AddBuff(new BuffAdd(holder.NetId, 5, SpellPackets.BuffInternal, marker, 9f, summon.NetId)));

        var order = new Capture();
        var chaos = new Capture();
        var allies = new ReplicationObserver(101, Team.Order, holder.NetId, order, view: new ReplicationView());
        var enemies = new ReplicationObserver(102, Team.Chaos, watcher.NetId, chaos, view: new ReplicationView());
        foreach (var observer in new[] { allies, enemies })
        {
            // The spawn burst creates every hero for every seat.
            observer.View.Known.Add(holder.NetId);
            observer.View.Known.Add(watcher.NetId);
            runtime.Delivery.AddObserver(observer);
        }
        allies.View.Known.Add(summon.NetId);
        runtime.Delivery.Flush();

        var enemyAdds = BuffPackets(chaos.Packets).Where(buff => buff.Holder == holder.NetId).ToList();
        TestAssert.True(enemyAdds.Any(buff => buff.Opcode == SpellPackets.BuffAddOpcode && buff.Hash == marker && buff.Caster == 0),
            "the Chaos client gets the marker on its holder, with no caster");
        foreach (var buff in BuffPackets(chaos.Packets))
            TestAssert.True(buff.Caster == 0 || enemies.View.Known.Contains(buff.Caster),
                $"0x{buff.Opcode:X2} on 0x{buff.Holder:X8} names only a unit the Chaos client was told about");
        TestAssert.False(enemies.View.Known.Contains(summon.NetId), "the summon stayed hidden from the Chaos client");
        TestAssert.True(BuffPackets(order.Packets).Any(buff => buff.Holder == holder.NetId && buff.Hash == marker &&
                                                              buff.Caster == summon.NetId),
            "the Order client, which knows the summon, still gets the real caster");
    }

    private readonly record struct BuffPacket(byte Opcode, uint Holder, uint Hash, uint Caster);

    private static IEnumerable<BuffPacket> BuffPackets(IEnumerable<PlaintextPacket> packets)
    {
        foreach (var packet in packets)
        {
            var bytes = packet.Plaintext.Span;
            var offset = bytes.IsEmpty ? -1 : SpellPackets.BuffCasterOffset(bytes[0]);
            if (offset < 0 || bytes.Length < offset + 4) continue;
            yield return new BuffPacket(bytes[0], BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]),
                bytes[0] == SpellPackets.BuffAddOpcode ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) : 0,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
        }
    }
}
