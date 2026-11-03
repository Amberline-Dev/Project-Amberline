using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 212: a camp member's buff add honours the client-name gate every other buff wire applies (ProtocolSpellEffectSink
/// ReachesClient): the stock client skips an add whose name hash its BuffHashMap lacks, and a server-only marker is a buff the
/// client must never hold. The neutral wire sent every buff in a member's table before this.
/// </summary>
public static class NeutralBuffGateChecks
{
    private const string KnownBuff = "CrestoftheAncientGolem";

    [Check("neutral.buff-add-skips-a-server-only-marker")]
    public static void ServerOnlyMarkerSendsNoAdd()
    {
        var (sink, packets, monster) = Wire(null);
        sink.AddBuff(monster, new BuffState("ProbeMarker", new BuffPayload { ServerOnlyMarker = true },
            BuffType.Internal, 1, 30.0, monster.NetId));
        TestAssert.Equal(0, BuffAdds(packets), "a server-only marker sends no buff add");
        sink.AddBuff(monster, new BuffState(KnownBuff, new BuffPayload(), BuffType.Internal, 2, 30.0, monster.NetId));
        TestAssert.Equal(1, BuffAdds(packets), "a client-known buff still sends its add");
    }

    [Check("neutral.buff-add-skips-a-name-the-client-lacks")]
    public static void NameTheClientLacksSendsNoAdd()
    {
        var data = GameData.Default;
        TestAssert.True(data.HasClientBuff(KnownBuff), "the known probe buff has client data");
        TestAssert.False(data.HasClientBuff("ProbeNoClientEntry"), "the unknown probe buff has no client data");
        var (sink, packets, monster) = Wire(data.HasClientBuff);
        sink.AddBuff(monster, new BuffState("ProbeNoClientEntry", new BuffPayload(), BuffType.Internal, 1, 30.0, monster.NetId));
        TestAssert.Equal(0, BuffAdds(packets), "a buff the client holds no entry for sends no add");
        sink.AddBuff(monster, new BuffState(KnownBuff, new BuffPayload(), BuffType.Internal, 2, 30.0, monster.NetId));
        TestAssert.Equal(1, BuffAdds(packets), "a buff the client holds an entry for sends its add");
    }

    private static (ProtocolNeutralPacketSink Sink, List<PlaintextPacket> Packets, Entity Monster) Wire(
        Func<string, bool>? hasClientBuff)
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var sink = new ProtocolNeutralPacketSink(packets.Add, new ReplicationSyncCounter(), world.Allocate,
            new NavigationBounds(0, 15000, 0, 15000), (_, _) => 0.0, hasClientBuff: hasClientBuff);
        var monster = world.Register(new Entity(world.Allocate(UnitType.Minion), EntityKind.Neutral, UnitType.Minion,
            Team.Neutral, new Position2(5000, 5000)));
        return (sink, packets, monster);
    }

    private static int BuffAdds(IEnumerable<PlaintextPacket> packets) =>
        packets.Count(packet => packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode);
}
