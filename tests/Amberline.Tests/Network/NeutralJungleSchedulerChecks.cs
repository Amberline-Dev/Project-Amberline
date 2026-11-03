using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// J-AUDIT F3: NeutralMinionSpawn.luaobj's camp timer callback (proto 0.1.0) adds HealthBonus 50.0 before every spawn it
/// makes, which includes the first spawn of a camp with a GroupDelaySpawnTime (luaNeutralInitTimer in
/// SpawnInitialNeutralMinions). Only the immediate spawns of delay-0 camps keep the initial bonus of 0.
/// </summary>
public static class NeutralJungleSchedulerChecks
{
    private static readonly int[] ImmediateCamps = [2, 3, 5, 8, 9, 11];
    private static readonly int[] DelayedCamps = [1, 4, 6, 7, 10, 12];

    [Check("neutral.n2-first-spawn-health-bonus")]
    public static void TimerDrivenSpawnsAddFiftyHealth()
    {
        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var sink = new ProtocolNeutralPacketSink(packets.Add, new ReplicationSyncCounter(), world.Allocate,
            new NavigationBounds(0, 15000, 0, 15000), (_, _) => 0.0);
        var scheduler = new JungleScheduler(world, GameData.Default, sink);
        scheduler.Start();

        foreach (var camp in ImmediateCamps) TestAssert.Equal(0, scheduler.Camps[camp].SpawnCycles);
        scheduler.Tick(100.0);
        foreach (var camp in ImmediateCamps)
            AssertGeneration(scheduler, packets, camp, expectedBonus: 0.0, "spawned immediately by SpawnInitialNeutralMinions");
        foreach (var camp in DelayedCamps)
            TestAssert.False(scheduler.Camps[camp].Active, $"camp {camp} waits for its GroupDelaySpawnTime timer");

        scheduler.Tick(115.0);
        scheduler.Tick(150.0);
        scheduler.Tick(900.0);
        foreach (var camp in DelayedCamps)
            AssertGeneration(scheduler, packets, camp, expectedBonus: 50.0, "first spawn through the camp timer");

        // Every later generation of either kind is one more timer call: +50 each.
        var now = 900.0;
        foreach (var camp in ImmediateCamps.Concat(DelayedCamps))
        {
            var cycles = scheduler.Camps[camp].SpawnCycles;
            var expected = scheduler.Camps[camp].HealthBonus + 50.0;
            // NeutralMinionSpawn.luaobj proto 0.4 marks the reported member dead in its own
            // AliveTracker and only arms the camp's respawn timer once that tracker is all false,
            // so every member of the generation is reported to the script — not only the last.
            var emptied = false;
            foreach (var member in scheduler.MembersForCamp(camp))
            {
                member.Entity.Alive = false;
                member.Entity.Health = 0.0;
                emptied = scheduler.OnDeath(member.Entity, now);
            }
            TestAssert.True(emptied, $"camp {camp} emptied");
            now += NeutralDefinitions.Camps[camp].RespawnDelay + 1.0;
            scheduler.Tick(now);
            TestAssert.Equal(cycles + 1, scheduler.Camps[camp].SpawnCycles, $"camp {camp} respawned");
            AssertGeneration(scheduler, packets, camp, expectedBonus: expected, "respawn");
        }
    }

    private static void AssertGeneration(JungleScheduler scheduler, List<PlaintextPacket> packets, int camp,
        double expectedBonus, string context)
    {
        var state = scheduler.Camps[camp];
        TestAssert.True(state.Active, $"camp {camp} is up ({context})");
        TestAssert.Near(expectedBonus, state.HealthBonus, 1e-9, $"camp {camp} health bonus ({context})");
        foreach (var member in scheduler.MembersForCamp(camp))
        {
            var entity = member.Entity;
            var baseHp = GameData.Default.Champion(member.Name).BaseHp;
            // No hero is in the world, so player-level scaling is zero; Baron adds its per-second growth.
            var growth = member.Name == "Worm"
                ? 0.0 /* batch 174 part 2: the shipped OnBuffActivate growth is added when the unit binds; this scheduler is unbound (production binds) */ : 0.0;
            TestAssert.Near(baseHp + expectedBonus + growth, entity.MaxHealth, 1e-6,
                $"camp {camp} {member.Name} max health ({context})");
            TestAssert.Near(entity.MaxHealth, entity.Health, 1e-9, $"camp {camp} {member.Name} spawns at full health");
            TestAssert.Near(expectedBonus, member.Stats.HealthBonus, 1e-9, $"camp {camp} {member.Name} stats bonus");

            // The create packet names the bonus and the 0xCC stats block carries the same max health.
            var create = packets.Last(packet => packet.Plaintext.Length == 323 && packet.Plaintext.Span[0] == 0x66 &&
                WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel).Fields["net_obj_id"] is uint id &&
                id == entity.NetId);
            var decoded = WirePacketDecoder.Decode(create.Plaintext.Span, create.Channel);
            TestAssert.Equal((int)expectedBonus, Convert.ToInt32(decoded.Fields["health_bonus"]),
                $"camp {camp} {member.Name} CreateNeutral health bonus ({context})");
            var stats = packets.Last(packet => packet.Plaintext.Length == 111 && packet.Plaintext.Span[0] == 0xCC &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span.Slice(11, 4)) == entity.NetId);
            TestAssert.Near(entity.MaxHealth,
                System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(stats.Plaintext.Span.Slice(19, 4)), 0.5,
                $"camp {camp} {member.Name} 0xCC stats HP ({context})");
        }
    }
}
