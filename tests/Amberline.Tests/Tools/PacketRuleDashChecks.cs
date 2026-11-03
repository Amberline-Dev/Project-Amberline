using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// The "order-during-dash" packet-stream rule and the dash statistics next to it, listed in docs/testing.md under "Packet-stream rules",
/// built from the real packet builders. The stock client replaces a lane minion's, pet's or monster's dash with the first 0xC1
/// it receives (the client's server-path follower).
/// </summary>
public static class PacketRuleDashChecks
{
    private const uint Hero1 = 0x40000001;
    private const uint Minion1 = 0x40010001, Minion2 = 0x40010002, Minion3 = 0x40010003;
    private const uint Monster1 = 0x40030001;

    // 475 u at 2000 u/s: Condemn, 0.2375 s.
    private static readonly Position2 Start = new(3000, 9000);
    private static readonly Position2 End = new(3475, 9000);

    private static NavigationBounds Bounds()
    {
        var grid = NavigationGrid.LoadDefault();
        return new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
    }

    private static PlaintextPacket Dash(uint netId) =>
        VisionPackets.Dash(netId, Start, End, 2000f, Bounds());

    private static PlaintextPacket Route(uint netId) =>
        VisionPackets.WaypointList(netId, [Start, new Position2(3100, 9100)], 2);

    private static PlaintextPacket Anchor(uint netId, Position2 at) =>
        VisionPackets.WaypointList(netId, [at], 2);

    private static void Feed(PacketStreamChecker checker, double t, params PlaintextPacket[] packets)
    {
        foreach (var packet in packets) checker.Observe(PacketRuleChecks.Observation(packet, t));
    }

    [Check("packet-rules.order-during-dash")]
    public static void OrderDuringDash()
    {
        // Positive: a route sent while the dash is still running replaces it, for a lane minion, a monster-class pet and a
        // stationary anchor alike.
        var bad = new PacketStreamChecker(2);
        Feed(bad, 10.0, Dash(Minion1), Dash(Minion2), Dash(Monster1));
        Feed(bad, 10.1, Route(Minion1), Anchor(Minion2, Start), Anchor(Monster1, Start));
        var found = bad.Finish().Where(violation => violation.Kind == PacketRuleKinds.OrderDuringDash).ToArray();
        TestAssert.Equal(3, found.Length);
        TestAssert.Equal(new uint?[] { Minion1, Minion2, Monster1 }, found.Select(violation => violation.NetId).ToArray());
        TestAssert.True(found.All(violation => violation.T == 10.1 && violation.Opcode == 0xC1 && violation.Peer == 0),
            "each is the 0xC1 at t=10.1");
        TestAssert.True(found[0].Detail.Contains("before its dash ends", StringComparison.Ordinal), found[0].Detail);
        TestAssert.Equal(3L, bad.TotalsByKind[PacketRuleKinds.OrderDuringDash]);
        TestAssert.Equal(3L, bad.Dashes.Interrupted);

        // Negative: after the dash's travel time (475 / 2000 s), before it in the same burst, in another peer's stream, for a
        // hero (its 0xC1 answer client orders), and for a minion that had no dash.
        var good = new PacketStreamChecker(2);
        Feed(good, 10.0, Route(Minion3)); // ahead of the dash in the same burst
        Feed(good, 10.0, Dash(Minion1), Dash(Hero1), Dash(Minion3));
        Feed(good, 10.05, Route(Hero1)); // heroes are exempt
        good.Observe(PacketRuleChecks.Observation(Route(Minion1), 10.05, peer: 1)); // peer 1 never received the dash
        Feed(good, 10.2376, Route(Minion1)); // the dash took 0.2375 s
        Feed(good, 12.0, Route(Minion2)); // never dashed
        TestAssert.Equal(0, good.Finish().Count(violation => violation.Kind == PacketRuleKinds.OrderDuringDash),
            string.Join("; ", good.Finish().Select(violation => violation.Detail)));

        // A second dash replaces the first window, and a route after the second window ends is quiet again.
        var chain = new PacketStreamChecker(1);
        Feed(chain, 20.0, Dash(Minion1));
        Feed(chain, 20.2, Dash(Minion1)); // a new displacement before the first ended
        Feed(chain, 20.4, Route(Minion1)); // 0.2 s into the second: inside it
        Feed(chain, 20.5, Route(Minion1)); // past it
        TestAssert.Equal(1L, chain.TotalsByKind.GetValueOrDefault(PacketRuleKinds.OrderDuringDash));
    }

    private static PlaintextPacket Reveal(uint netId, byte[] movement) =>
        VisionPackets.EnterVisibilityClient(netId, "Unit", 0, 1, 1, Start, new Position2(0, 1), isHero: netId == Hero1,
            movementData: movement);

    // Batch 291: since batch 276 a reveal's 0xC2 carries the unit's route, stop or dash where a 0xC1 or 0x67 followed the reveal before.
    // A route or stop counts as that 0xC1 (one point or a stop is stationary), a dash as that 0x67; a reveal is no AI order.
    [Check("packet-rules.reveal-movement-counts-as-routes-and-dashes")]
    public static void RevealMovementCountsAsRoutesAndDashes()
    {
        var bounds = Bounds();
        var checker = new PacketStreamChecker(1);
        Feed(checker, 1.0, Reveal(Hero1, VisionPackets.RouteMovementData(Hero1, [Start, new Position2(3100, 9100)], bounds, 5)!));
        Feed(checker, 2.0, Reveal(Hero1, VisionPackets.StopMovementData(6, Start, new Position2(0, 1))));
        Feed(checker, 3.0, Reveal(Minion1, VisionPackets.DashMovementData(Minion1, Start, End, 2000f, bounds, 7, teleportId: 3)));
        Feed(checker, 3.1, Route(Minion1)); // an AI order inside the dash the reveal announced
        Feed(checker, 4.0, Reveal(Minion2, VisionPackets.DashMovementData(Minion2, Start, End, 2000f, bounds, 8)));
        Feed(checker, 4.1, Reveal(Minion2, VisionPackets.StopMovementData(9, End, new Position2(0, 1)))); // a reveal ends it, on its end
        Feed(checker, 4.15, Route(Minion2)); // after a reveal replaced the dash: no order during a dash
        Feed(checker, 6.0, Reveal(Minion3, VisionPackets.RouteMovementData(Minion3, [Start], bounds, 10, teleportId: 4)!));

        TestAssert.Equal(2L, checker.HeroRoutes.Routes, "the hero's route and stop");
        TestAssert.Equal(1L, checker.HeroRoutes.Stationary, "the stop is stationary");
        TestAssert.Equal(2L, checker.RoutesByNetId.GetValueOrDefault(Hero1));
        TestAssert.Equal(2L, checker.RoutesByNetId.GetValueOrDefault(Minion2), "the reveal's stop and the 0xC1");
        TestAssert.Equal(1L, checker.RoutesByNetId.GetValueOrDefault(Minion3), "a one-point teleport entry is a route");
        var dashes = checker.Dashes;
        TestAssert.Equal(2L, dashes.Dashes, "the reveals' dashes");
        TestAssert.Equal(1L, dashes.LandingAnchors, "the reveal's stop on the dash's end");
        TestAssert.Equal(1L, dashes.Interrupted, "only the order inside the announced dash");
        TestAssert.Equal(2L, checker.HeroRoutes.InReveals, "both hero routes came in reveals");
        TestAssert.Equal(new RevealMovementStatistics(Routes: 2, Stops: 2, Dashes: 2, Teleports: 2), checker.RevealMovement);
    }

    [Check("packet-rules.dash-statistics-count-the-anchors-around-a-dash")]
    public static void DashStatisticsCountTheAnchorsAroundADash()
    {
        // A stationary 0xC1 in the same step ahead of a dash is a start anchor; the first 0xC1 after the dash, if it is stationary
        // and on the dash's end, is a landing anchor. A route, an anchor elsewhere, or a second packet is neither.
        var checker = new PacketStreamChecker(1);
        Feed(checker, 1.0, Anchor(Minion1, Start), Dash(Minion1)); // start anchor
        Feed(checker, 2.0, Anchor(Minion1, End + new Position2(1, -1))); // landing anchor, 1.4 u off the end
        Feed(checker, 3.0, Dash(Minion2)); // no start anchor
        Feed(checker, 4.0, Anchor(Minion2, End + new Position2(40, 0))); // anchor elsewhere
        Feed(checker, 5.0, Dash(Minion3));
        Feed(checker, 6.0, Route(Minion3), Anchor(Minion3, End)); // a route came first, then the anchor
        Feed(checker, 7.0, Dash(Hero1)); // heroes are not counted
        Feed(checker, 8.0, Anchor(Hero1, End));
        Feed(checker, 9.0, Anchor(Minion2, Start), Dash(Minion2)); // 4.0's anchor was too long ago: Start again, same step
        var dashes = checker.Dashes;
        TestAssert.Equal(4L, dashes.Dashes);
        TestAssert.Equal(2L, dashes.StartAnchors);
        TestAssert.Equal(1L, dashes.LandingAnchors);
        TestAssert.Equal(0L, dashes.Interrupted);
    }
}
