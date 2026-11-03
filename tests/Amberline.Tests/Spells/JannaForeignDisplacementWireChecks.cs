using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-janna-channel-cancel-preserves-foreign-dash-wire")]
    public static void MonsoonCancellationLeavesKickRouteToItsOwner()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team, enemyChampion: "LeeSin");
            var runtime = match.Runtime;
            var janna = match.Hero(0);
            var lee = match.Hero(1);
            // The initial Monsoon pulse must not displace Lee before he casts.
            lee.Position = janna.Position + new Position2(850, 0);
            match.Cast(0, 3, janna);
            runtime.Tick(.3);
            TestAssert.True(JannaChannel(match, janna) is not null && lee.ForcedMove is null,
                $"{team}: Monsoon began without pushing the later kicker");

            lee.Position = janna.Position + new Position2(200, 0);
            match.Cast(1, 3, janna);
            TestAssert.True(JannaChannel(match, janna) is not null,
                $"{team}: the kick's preflight root did not end Monsoon before its dash");
            match.Clear();
            runtime.Tick(.3);
            TestAssert.True(janna.ForcedMove is { Effect: "BlindMonkRKick", Completed: false } &&
                JannaChannel(match, janna) is null,
                $"{team}: the real kick displaced Janna and interrupted her channel");
            foreach (var capture in match.Captures)
            {
                var packets = capture.Packets;
                var dashIndex = packets.ToList().FindIndex(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.DashOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == janna.NetId));
                TestAssert.True(dashIndex >= 0,
                    $"{team}: each observer receives Janna's kick dash");
                TestAssert.False(packets.Skip(dashIndex + 1).Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == janna.NetId)),
                    $"{team}: channel cancellation cannot overwrite the attached kick route");
            }

            match.Clear();
            for (var frame = 0; frame < 15 && janna.ForcedMove is not null; frame++)
                runtime.Tick(.1);
            TestAssert.True(janna.ForcedMove is null,
                $"{team}: Lee Sin retires his kick after its ordinary landing");
            foreach (var capture in match.Captures)
                TestAssert.True(capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == janna.NetId)),
                    $"{team}: the kick owner still publishes the terminal waypoint");
        }
    }
}
