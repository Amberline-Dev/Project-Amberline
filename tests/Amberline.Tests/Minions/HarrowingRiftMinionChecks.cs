using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>The Harrowing rift's lane minions: the server gives them MeshSkin1 (the Harrowing skin); Summoner's Rift keeps the stock skin.</summary>
public static class HarrowingRiftMinionChecks
{
    [Check("minions.harrowing-rift-lane-minions-wear-mesh-skin-one")]
    public static void LaneMinionsOfTheHarrowingRiftCarryTheHarrowingSkinAndSummonersRiftDoesNot()
    {
        var grid = NavigationGrid.LoadDefault();
        var normalWorld = new WorldState();
        var normal = new WaveScheduler(normalWorld, grid, new QuietPackets(), LevelScriptFixtures.Init(normalWorld));
        TestAssert.Equal(0, normal.SpawnFixture(Team.Order, MinionType.Melee, 0, "L", 0.0, 0.0).SkinId,
            "Summoner's Rift keeps the stock minion skin");

        var riftWorld = new WorldState();
        var rift = new WaveScheduler(riftWorld, grid, new QuietPackets(), LevelScriptFixtures.Init(riftWorld),
            laneMinionSkinId: GameMaps.HarrowingMinionSkinId);
        TestAssert.Equal(GameMaps.HarrowingMinionSkinId, rift.SpawnFixture(Team.Chaos, MinionType.Melee, 0, "L", 0.0, 0.0).SkinId,
            "the Harrowing rift's lane minions wear MeshSkin1");
    }

    // Packets are not under test here: the sink only has to accept what the scheduler sends.
    private sealed class QuietPackets : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }
        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }
}
