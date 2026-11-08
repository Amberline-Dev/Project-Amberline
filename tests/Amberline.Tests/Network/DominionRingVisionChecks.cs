using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// Batch 714 (owner, 8 November 2026: on the Crystal Scar "it looks like the lanes give vision but in reality they dont"). Map8's shipped grid
/// (AIPath.aimesh_ngrid) flags the outer ring with 0x100, and every client lights those cells in its fog (the client builds the set,
/// The client writes it every 0.5 s unless the local hero has a NearSight buff). The production composition of map 8 with a unit of each
/// team on the ring, far from every sight source of the other team, then one step off the ring.
/// </summary>
public static class DominionRingVisionChecks
{
    private const double Step = 1.0 / 30.0;
    // Map8 grid cells: the bottom of the ring (0x100), the plain ground just inside it (flag 0), and the west ring (0x100).
    private static readonly Position2 Ring = new(6931.0, 1774.0);
    private static readonly Position2 RingMinion = new(7131.0, 1774.0);
    private static readonly Position2 OffRing = new(6931.0, 2750.0);
    private static readonly Position2 WestRing = new(2400.0, 6624.0);

    [Check("replication.dominion-ring-reveals-units-to-every-team", Serial = true, TimeoutSeconds = 300)]
    public static void TheRingRevealsItsUnitsToEveryTeam()
    {
        try
        {
            using var runtime = StartTwoSeatMatch();
            var order = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Hero && unit.Team == Team.Order);
            var chaos = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Hero && unit.Team == Team.Chaos);
            // Every client holds every hero from the loading screen, so a reveal is a 0xC2 and a hide a 0x54.
            var orderView = new ReplicationView();
            orderView.Known.Add(chaos.NetId);
            var chaosView = new ReplicationView();
            chaosView.Known.Add(order.NetId);
            var orderClient = new CaptureTransport();
            var chaosClient = new CaptureTransport();
            runtime.Delivery.AddObserver(new ReplicationObserver(9701UL, Team.Order, order.NetId, orderClient, view: orderView));
            runtime.Delivery.AddObserver(new ReplicationObserver(9702UL, Team.Chaos, chaos.NetId, chaosClient, view: chaosView));

            foreach (var (spot, flagged) in new[] { (Ring, true), (RingMinion, true), (WestRing, true), (OffRing, false) })
            {
                TestAssert.Equal(flagged, runtime.Navigation.HasGlobalVision(spot), $"Map8's grid flags {spot} with 0x100: {flagged}");
                TestAssert.Equal(flagged, VisibilityRules.ProvidersReveal(runtime.Delivery.Environment, [], spot),
                    $"a point at {spot} is seen with no provider (a ground effect there): {flagged}");
            }

            order.Position = WestRing;
            chaos.Position = Ring;
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
                Team.Chaos, RingMinion) { Health = 1e6, MaxHealth = 1e6, Name = "Dummy" });
            // The premise: no sight source of the other team reaches the spots.
            foreach (var (team, spot, what) in new[] { (Team.Order, Ring, "the Chaos hero's ring spot"), (Team.Order, RingMinion, "the minion's"),
                         (Team.Order, OffRing, "the off-ring spot"), (Team.Chaos, WestRing, "the Order hero's ring spot") })
                foreach (var provider in VisibilityRules.BuildTeamProviders(runtime.World, team, runtime.Delivery.Balls, runtime.Delivery.AdditionalProviders))
                    TestAssert.True(Distance(provider.Position, spot) > provider.Radius + 100.0,
                        $"premise: {team}'s provider at {provider.Position} (radius {provider.Radius}) is far from {what} {spot}");
            Run(runtime);
            var lines = new List<string>();
            var orderSeesChaos = orderView.Visible.Contains(chaos.NetId);
            var orderSeesMinion = orderView.Visible.Contains(minion.NetId);
            var chaosSeesOrder = chaosView.Visible.Contains(order.NetId);
            var orderTargets = runtime.Session.CurrentTargetVisible?.Invoke(order, chaos) == true;
            var heroEntries = Count(orderClient, VisionPackets.EnterVisibilityOpcode, chaos.NetId);
            var minionEntries = Count(orderClient, VisionPackets.EnterVisibilityOpcode, minion.NetId);
            var orderEntries = Count(chaosClient, VisionPackets.EnterVisibilityOpcode, order.NetId);
            lines.Add($"ring: Order client sees the Chaos hero {orderSeesChaos} (0xC2 x{heroEntries}), the Chaos minion {orderSeesMinion} " +
                      $"(0xC2 x{minionEntries}); Chaos client sees the Order hero {chaosSeesOrder} (0xC2 x{orderEntries}); " +
                      $"Order's sight (targeting) on the Chaos hero {orderTargets}");
            var chaosAt = chaos.Position;

            orderClient.Packets.Clear();
            chaos.Position = OffRing;
            Run(runtime);
            var offRingSeen = orderView.Visible.Contains(chaos.NetId);
            var heroLeaves = Count(orderClient, VisionPackets.LeaveVisibilityOpcode, chaos.NetId);
            lines.Add($"off the ring: Order client sees the Chaos hero {offRingSeen} (0x54 x{heroLeaves})");

            chaos.Position = Ring;
            chaos.StealthHidden = true;
            Run(runtime);
            var stealthSeen = orderView.Visible.Contains(chaos.NetId);
            lines.Add($"ring, stealthed: Order client sees the Chaos hero {stealthSeen}");
            chaos.StealthHidden = false;
            Run(runtime);
            var unstealthedSeen = orderView.Visible.Contains(chaos.NetId);

            TestAssert.True(runtime.Buffs.AddBuff(order, "NearSight", BuffType.NearSight, 30.0, chaos.NetId) is not null, "the Order hero is nearsighted");
            Run(runtime);
            var nearsightedSeen = orderView.Visible.Contains(chaos.NetId);
            var chaosStillSeesOrder = chaosView.Visible.Contains(order.NetId);
            lines.Add($"ring, Order hero nearsighted: Order client sees the Chaos hero {nearsightedSeen}; Chaos client sees the Order hero {chaosStillSeesOrder}");
            foreach (var line in lines) Console.WriteLine(line);

            TestAssert.True(Distance(chaosAt, Ring) < 1.0, $"the Chaos hero stays on its ring spot ({chaosAt})");
            TestAssert.True(orderSeesChaos && heroEntries == 1, "the Order client is told about the Chaos hero on the ring (one 0xC2)");
            TestAssert.True(orderSeesMinion && minionEntries == 1, "the Order client is told about the Chaos minion on the ring (one 0xC2)");
            TestAssert.True(chaosSeesOrder && orderEntries == 1, "the Chaos client is told about the Order hero on the ring (one 0xC2)");
            TestAssert.True(orderTargets, "Order's sight (the gameplay targeting rule) holds the Chaos hero on the ring");
            TestAssert.True(!offRingSeen && heroLeaves == 1, "one step off the ring the Chaos hero is hidden again (one 0x54)");
            TestAssert.False(stealthSeen, "the ring's sight is no true sight: a stealthed unit on it stays hidden");
            TestAssert.True(unstealthedSeen, "out of stealth on the ring it is seen again");
            TestAssert.False(nearsightedSeen, "a nearsighted hero's client does not light the ring, so it is not told about units there");
            TestAssert.True(chaosStillSeesOrder, "the other team's client still sees the ring");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static void Run(MatchRuntime runtime)
    {
        for (var tick = 0; tick < 3; tick++) runtime.Tick(Step);
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static int Count(CaptureTransport client, byte opcode, uint netId) => client.Packets.Count(packet =>
        packet.Plaintext.Length >= 5 && packet.Plaintext.Span[0] == opcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span.Slice(1, 4)) == netId);

    /// <summary>The production composition of map 8 with an Order and a Chaos Annie, started and stepped once.</summary>
    private static MatchRuntime StartTwoSeatMatch()
    {
        var paths = GameDataPaths.Default.ForMap(GameMaps.CrystalScar);
        GameData.ConfigureDefault(paths);
        SessionParticipantDefinition[] roster = [new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos)];
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(paths), GameData.Default, readySettleSeconds: 0.0,
            roster: roster, mapId: GameMaps.CrystalScar);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            TestAssert.True(runtime.Session.Authenticate(100L + index, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"seat {index} authenticates");
            runtime.Session.Spawn(100L + index);
            TestAssert.True(runtime.Session.MarkReady(100L + index), $"seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "map 8 starts");
        runtime.Tick(Step);
        return runtime;
    }

    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];

        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
