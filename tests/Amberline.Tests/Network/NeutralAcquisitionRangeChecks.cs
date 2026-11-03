using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// mp2 R1: a camp monster acquires targets at its character's shipped Data*AcquisitionRange (the CharacterData loader
/// default 750 when the key is absent), not at the 850 leash radius that NeutralContracts passed in that field.
/// </summary>
public static class NeutralAcquisitionRangeChecks
{
    [Check("neutral.camp-acquisition-range-shipped")]
    public static void CampMonstersAcquireAtTheirShippedRange()
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var sink = new ProtocolNeutralPacketSink(packets.Add, new ReplicationSyncCounter(), world.Allocate,
            new NavigationBounds(0, 15000, 0, 15000), (_, _) => 0.0);
        var scheduler = new JungleScheduler(world, GameData.Default, sink);
        scheduler.Start();
        foreach (var now in new[] { 100.0, 115.0, 150.0, 900.0 }) scheduler.Tick(now);

        var checkedMonsters = 0;
        var mismatches = new List<string>();
        foreach (var camp in NeutralDefinitions.Camps.Keys)
        {
            foreach (var member in scheduler.MembersForCamp(camp))
            {
                var shipped = GameData.Default.Champion(member.Name).AcquisitionRange;
                if (Math.Abs(shipped - member.Entity.AcquisitionRange) > 1e-9)
                    mismatches.Add($"{member.Name} (camp {camp}) shipped {shipped} got {member.Entity.AcquisitionRange}");
                checkedMonsters++;
            }
        }
        TestAssert.True(checkedMonsters > 0, "the scheduler spawned camp monsters");
        TestAssert.True(mismatches.Count == 0, "camp monsters acquire at their shipped AcquisitionRange: " + string.Join("; ", mismatches));
    }
}
