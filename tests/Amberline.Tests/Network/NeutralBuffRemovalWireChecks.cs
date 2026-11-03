using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 181: a jungle monster's buff goes through the match's own buff lifecycle, the path a hero's buff takes. The add and
    /// the remove must both reach the observer, in the buff's slot, under one name hash. INeutralPacketSink carries no remove by
    /// design: the monster's removal goes out through the shared spell effect sink (ISpellEffectSink.SendBuffDelta).
    /// </summary>
    [Check("neutral.buff-add-and-remove-reach-the-client")]
    public static void JungleBuffAddAndRemoveReachTheClient()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var nav = runtime.Navigation;
        var wire = new ProtocolNeutralPacketSink(packet => runtime.Delivery.Enqueue(packet), runtime.SyncIds, runtime.World.Allocate,
            new(nav.MinX, nav.MaxX, nav.MinZ, nav.MaxZ), (x, z) => nav.HeightAt(new(x, z)), runtime.GameData.ClientBuffName);
        var scheduler = new JungleScheduler(runtime.World, runtime.GameData, wire, navigation: nav);
        scheduler.Start();
        scheduler.Tick(1000);
        var hero = match.Hero(0);
        var monster = scheduler.Members.Values.First().Entity;
        // In the observer's view, as the Barons' and minions' checks place their units.
        monster.Position = hero.Position + new Position2(50, 0);
        runtime.Delivery.Flush();
        const string name = "CrestoftheAncientGolem";
        var hash = SpellPackets.BuffNameHash(name);

        match.Clear();
        var buff = runtime.SpellBuffs.Add(monster, name, BuffType.Internal, 5, hero.NetId)
            ?? throw new InvalidOperationException($"{name} was not added to the jungle monster");
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures.Any(capture => capture.Packets.Any(packet =>
                WireBuff(packet, 0xBF, monster.NetId, (byte)buff.Slot, hash))),
            "the jungle monster's buff add reaches the observer in the buff's slot");

        match.Clear();
        runtime.SpellBuffs.Remove(monster, buff.Slot);
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures.Any(capture => capture.Packets.Any(packet =>
                WireBuff(packet, 0x7F, monster.NetId, (byte)buff.Slot, hash))),
            "the jungle monster's buff remove reaches the observer in the same slot and under the same name hash");
        TestAssert.False(monster.Buffs.Any(item => item.Name == name), "the buff is gone on the server");
    }

    private static bool WireBuff(PlaintextPacket packet, byte opcode, uint netId, byte slot, uint hash)
    {
        var raw = packet.Plaintext.Span;
        return raw.Length >= (opcode == 0xBF ? 13 : 10) && raw[0] == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(raw[1..]) == netId && raw[5] == slot &&
            BinaryPrimitives.ReadUInt32LittleEndian(raw[(opcode == 0xBF ? 9 : 6)..]) == hash;
    }
}
