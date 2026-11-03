using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-teleport-target-keeps-foreign-knockback-wire")]
    public static void TeleportFreezeWaitsForKnockbackOwnerWaypoint()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, enemyChampion: "Janna",
                random: new AnnieChecks.FixedRandomSource(.5));
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var janna = match.Hero(1);
            caster.SummonerSpells[0] = "SummonerTeleport";
            // Reuse the live pet/Monsoon setup: Janna at 7125 pushes a
            // target from 7000 toward the western mid-lane. The
            // Teleport caster stays outside the 700-unit initial pulse.
            caster.Position = new Position2(7500, 8000);
            janna.Position = new Position2(7125, 7000);
            var minion = runtime.Waves!.SpawnFixture(team, MinionType.Melee,
                0, "C", 0, 0);
            minion.Position = new Position2(7000, 7000);
            minion.Waypoints.Clear();
            runtime.Tick(.01);
            minion.Position = new Position2(7000, 7000);
            foreach (var point in new[] { caster.Position, janna.Position, minion.Position })
                TestAssert.True(runtime.Navigation.IsWalkable(point) &&
                    runtime.Navigation.IsMovementWalkable(point),
                    $"{team}: Teleport/Monsoon setup point {point} is clear on shipped Map1");
            runtime.Delivery.Flush();
            TestAssert.True(runtime.Effects.IsReferenceReady(minion),
                $"{team}: real Monsoon target is reference-ready before Teleport");
            match.Clear();

            match.Cast(1, 3, janna);
            // The push starts when the 0.25 s windup ends; MoveAway names its own script as the label of the displacement.
            var push = AwaitForcedMove(match, minion, "MoveAway") ?? throw new InvalidOperationException(
                $"{team}: real Monsoon did not attach a minion forced move");
            TestAssert.True(push is { Effect: "MoveAway", Completed: false },
                $"{team}: real Monsoon pushes the allied Teleport target");
            var castTargetPosition = minion.Position;
            TestAssert.True(Distance(push.Destination, castTargetPosition) > 250.0,
                $"{team}: real Monsoon projects a meaningful landing; start={castTargetPosition}, " +
                $"destination={push.Destination}");
            match.Clear();
            match.Cast(0, 0, minion, summoner: true);
            TestAssert.True(ReferenceEquals(push, minion.ForcedMove) &&
                minion.Buffs.Any(buff => buff.Name == "Teleport_Target"),
                $"{team}: Teleport freezes the ordinary route without retiring Janna's move");
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == minion.NetId)),
                    $"{team}: Teleport does not overwrite the attached knockback dash");

            match.Clear();
            runtime.Tick(.9);
            TestAssert.True(minion.ForcedMove is null,
                $"{team}: Janna retires the completed move after 0.9s; attached={minion.ForcedMove?.Effect}, " +
                $"completed={push.Completed}, remaining={push.Remaining:R}, destination={push.Destination}, position={minion.Position}");
            TestAssert.True(Distance(minion.Position, castTargetPosition) > 250.0,
                $"{team}: Janna reaches a displaced landing; start={castTargetPosition}, " +
                $"projected={push.Destination}, actual={minion.Position}, succeeded={push.Succeeded}");
            // Janna's completion owns the terminal waypoint, the dash's own last waypoint for a lane minion. Batch 270: retail sends
            // the owner's stationary anchor there like any other route (ReplicationDelivery.DropRedundantDashAnchors is gone), so it
            // reaches the observers after the dash; the unit stands on the announced landing.
            TestAssert.True(Distance(minion.Position, push.Destination) <= 3.0,
                $"{team}: Janna's move landed on its announced end; destination={push.Destination}, position={minion.Position}");
            foreach (var capture in match.Captures)
                TestAssert.True(capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode && packet.Plaintext.Length == 17 &&
                    packet.EntityReferences.Any(reference => reference.NetId == minion.NetId) &&
                    Distance(new Position2(BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[9..]),
                        BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[13..])), push.Destination) <= 3.0),
                    $"{team}: the owner's stationary anchor on the landing reaches the observer after the dash");

            runtime.Tick(3.3);
            TestAssert.Equal("SummonerTeleport", caster.SummonerSpells[0],
                $"{team}: the full channel completed normally");
            TestAssert.True(Distance(caster.Position, castTargetPosition) < 250.0 &&
                runtime.Navigation.IsMovementWalkable(caster.Position) &&
                Distance(caster.Position, minion.Position) >=
                    runtime.Movement.BodyClearance(caster, minion) - 1.0 &&
                Distance(caster.Position, janna.Position) >=
                    runtime.Movement.BodyClearance(caster, janna) - 1.0,
                $"{team}: arrival stays at the stored cast-time point with legal body clearance");

            using var stationary = new Match("Annie", team,
                random: new AnnieChecks.FixedRandomSource(.5));
            var stationaryCaster = stationary.Hero(0);
            stationaryCaster.SummonerSpells[0] = "SummonerTeleport";
            stationaryCaster.Position = new Position2(7500, 8000);
            stationary.Hero(1).Position = new Position2(7125, 7000);
            var still = stationary.Runtime.Waves!.SpawnFixture(team, MinionType.Melee,
                0, "C", 0, 0);
            still.Position = new Position2(7000, 7000);
            still.Waypoints.Clear();
            still.Target = null;
            still.AttackTarget = null;
            foreach (var point in new[] { stationaryCaster.Position, stationary.Hero(1).Position, still.Position })
                TestAssert.True(stationary.Runtime.Navigation.IsWalkable(point) &&
                    stationary.Runtime.Navigation.IsMovementWalkable(point),
                    $"{team}: stationary-control point {point} is clear on shipped Map1");
            for (var frame = 0; frame < 6 && !stationary.Runtime.Delivery.Observers.All(observer =>
                     observer.View.Known.Contains(still.NetId) && observer.View.Visible.Contains(still.NetId)); frame++)
            {
                stationary.Runtime.Tick(.01);
                still.Position = new Position2(7000, 7000);
                still.Waypoints.Clear();
                still.Target = null;
                still.AttackTarget = null;
            }
            stationary.Runtime.Delivery.Flush();
            TestAssert.True(stationary.Runtime.Delivery.Observers.All(observer =>
                observer.View.Known.Contains(still.NetId) && observer.View.Visible.Contains(still.NetId)),
                $"{team}: both normal observers know and see the stationary target");
            stationary.Clear();
            stationary.Cast(0, 0, still, summoner: true);
            TestAssert.True(still.ForcedMove is null &&
                stationary.Captures.All(capture => capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == still.NetId))),
                $"{team}: ordinary stationary Teleport target keeps its existing C1 freeze");
        }
    }
}
