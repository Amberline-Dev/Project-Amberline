using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 752: the stock client's selected-unit target bar (<c>TargetBar_AD</c>) draws attack damage as
/// <c>floor((mPercentPhysicalDamageMod + 1.0) * (mBaseAttackDamage + mFlatPhysicalDamageMod) + 0.5)</c> (the doubles 1.0 and
/// 0.5), so the percent's neutral value is 0. Turrets and minions were sent 1.0 and every selected one showed double its
/// attack damage (an outer turret 304 for 152).
/// </summary>
public static class TargetBarAttackDamageChecks
{
    [Check("protocol.target-bar-attack-damage-is-the-units-own")]
    public static void TurretAndMinionBlocksCarryNeutralPercentDamage()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0), readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        runtime.Waves!.AutomaticSpawningEnabled = false;

        // The real outer turret through the production writer of its block (LOCAL1 0 base, 4 flat, 5 percent).
        var turret = runtime.World.Entities.Values.First(unit => unit.UnitType == UnitType.Turret &&
            MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier == TurretTier.Outer);
        var queue = new Queue();
        new ProtocolCombatTransport(queue, runtime.HeroStats, _ => true, new ReplicationSyncCounter()).SendTurretStats(turret);
        AssertTargetBar(queue.Packets.Single(), 0xFFFFu, 0, 4, 5, turret.AttackDamage, "outer turret " + turret.Name);

        // A real lane melee minion through the wave's own stat block (LOCAL1 10 base, 14 flat, 15 percent).
        var capture = new Capture();
        runtime.Delivery.AddObserver(new ReplicationObserver(100, Team.Order, 0, capture, ready: true));
        var minion = runtime.Waves.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0);
        runtime.Delivery.Flush();
        var block = capture.Packets.Single(packet => IsFullBlock(packet, minion.NetId, 0x1FFFFFu));
        AssertTargetBar(block, 0x1FFFFFu, 10, 14, 15, minion.AttackDamage, "melee minion");
    }

    private static void AssertTargetBar(PlaintextPacket packet, uint mask, int baseSlot, int flatSlot, int percentSlot,
        double attackDamage, string name)
    {
        TestAssert.True(IsFullBlock(packet, ReadUnit(packet), mask), name + ": a full LOCAL1 block");
        float Slot(int bit) => BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[(19 + 4 * bit)..]);
        TestAssert.Equal(0.0f, Slot(percentSlot), name + ": mPercentPhysicalDamageMod");
        var shown = Math.Floor((double)((Slot(percentSlot) + 1.0f) * (Slot(baseSlot) + Slot(flatSlot)) + 0.5f));
        TestAssert.Equal(Math.Floor(attackDamage + 0.5), shown, name + ": the target bar's AD is the unit's own " + attackDamage);
    }

    private static uint ReadUnit(PlaintextPacket packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]);

    private static bool IsFullBlock(PlaintextPacket packet, uint netId, uint mask)
    {
        Span<uint> masks = stackalloc uint[5];
        return CombatPackets.TryReadReplicationBlock(packet.Plaintext.Span, out _, out var unit, masks) &&
            unit == netId && masks[1] == mask;
    }

    private sealed class Queue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Enqueue(PlaintextPacket packet, bool global = false) => Packets.Add(packet);
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId, Func<uint, PlaintextPacket> buildPacket) =>
            Packets.Add(buildPacket(killerNetId));
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
