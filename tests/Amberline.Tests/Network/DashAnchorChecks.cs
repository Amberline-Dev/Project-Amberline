using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.ControlMarchRecoveryChecks;
using static Amberline.Tests.ForcedMoveAiChecks;

namespace Amberline.Tests;

/// <summary>
/// Retail sends the stationary anchors that wrap a displacement (the stun's, ahead of the 0x67 dash, and the owner's at the
/// landing) for every unit. Batch 270 retired ReplicationDelivery.DropRedundantDashAnchors, which deleted them for lane minions,
/// pets and monsters (the stock client replaces its path with the dash and seats the unit on the dash's last waypoint, so the
/// anchors cost only the 74-76 u landing snaps of the 17 October 2026 captures): a minion's wire is now a hero's.
/// </summary>
public static class DashAnchorChecks
{
    [Check("replication.dash-anchors-are-sent-like-every-route")]
    public static void StationaryAnchorsAroundADashAreSent()
    {
        var setup = StartMidLaneMarch(Team.Order);
        using var runtime = setup.Runtime;
        var minion = setup.Minion;
        runtime.Minions!.HaltAI(minion);
        var enemy = MakeEnemy(runtime, minion, Team.Order);

        List<Sent> Send(Entity unit, int capture, params Position2[] route)
        {
            runtime.Effects.SendWaypoints(unit, route, runtime.World.Clock.SyncId());
            runtime.Delivery.Flush();
            return Movement(setup.Capture, unit.NetId, capture);
        }

        foreach (var (unit, capture) in new[] { (minion, 0), (setup.Scout, 1) })
        {
            setup.Capture.Clear();
            var (_, landing) = Displace(runtime, unit, enemy, Displacement.Condemn, $"dash of {unit.NetId:X}", withStun: true);
            runtime.Delivery.Flush();
            var sent = Movement(setup.Capture, unit.NetId, capture);
            TestAssert.True(sent.Select(packet => packet.Opcode).SequenceEqual(
                    [VisionPackets.WaypointListOpcode, VisionPackets.DashOpcode]),
                $"{unit.Kind}: the stun's anchor goes ahead of its dash ({string.Join(",", sent.Select(packet => $"{packet.Opcode:X2}"))})");
            unit.Position = landing;
            unit.ForcedMove = null;
            TestAssert.Equal(1, Send(unit, capture + 2, landing).Count, $"{unit.Kind}: the landing anchor on the dash's end is sent");
        }
    }
}
