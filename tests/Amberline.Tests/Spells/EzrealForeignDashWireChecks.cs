using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-ezreal-e-execution-origin")]
    public static void ArcaneShiftClampsFromDisplacedWindupPosition()
    {
        using var match = new Match("Ezreal", Team.Order);
        var hero = match.Hero(0);
        match.Hero(1).Position = new Position2(6000, 7000);
        match.CastGround(0, new Position2(9000, 7000), 2);
        hero.Position = new Position2(7225, 7000);
        match.Clear();
        SpellStep(match, .25);
        match.Runtime.Delivery.Flush();
        TestAssert.Equal(new Position2(7699, 7001), hero.Position,
            "EZ-01: the 475-unit clamp starts at execution, retaining the distant request");
        var departure = match.Captures[0].Packets.Single(packet =>
            Opcode(packet) == FxPackets.CreateGroupOpcode &&
            ReadUInt32(packet, 6) == SpellPackets.BuffNameHash("Ezreal_arcaneshift_cas"));
        var nav = match.Runtime.Movement.Navigation;
        var expected = FxPackets.CompressPosition(new Position2(7225, 7000),
            new NavigationBounds(nav.MinX, nav.MaxX, nav.MinZ, nav.MaxZ),
            (x, z) => nav.HeightAt(new Position2(x, z)));
        TestAssert.Equal(expected.X, BinaryPrimitives.ReadInt16LittleEndian(departure.Plaintext.Span[33..]));
        TestAssert.Equal(expected.Z, BinaryPrimitives.ReadInt16LittleEndian(departure.Plaintext.Span[39..]));
    }

    [Check("spells-live-ezreal-cast-cancel-preserves-foreign-dash-wire")]
    public static void TrueshotWindupLeavesKickRouteToItsOwner()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Ezreal", team, enemyChampion: "LeeSin");
            var runtime = match.Runtime;
            var ezreal = match.Hero(0);
            var lee = match.Hero(1);
            lee.Position = ezreal.Position + new Position2(200, 0);
            match.CastGround(0, lee.Position, slot: 3);
            TestAssert.True(EzrealFlights.WindingUp(runtime.Spells, ezreal, 3),
                $"{team}: Trueshot has an unfinished protected windup");
            match.Cast(1, 3, ezreal);
            match.Clear();
            runtime.Tick(.3);
            // Playtest B4 #8, 27 Sept 2026: CantCancelWhileWindingUp protects R.
            TestAssert.True(ezreal.ForcedMove is { Effect: "BlindMonkRKick", Completed: false } &&
                EzrealFlights.WindingUp(runtime.Spells, ezreal, 3),
                $"{team}: the kick displaced Ezreal without cancelling his pending R");
            foreach (var capture in match.Captures)
            {
                var packets = capture.Packets;
                var dashIndex = packets.ToList().FindIndex(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.DashOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == ezreal.NetId));
                TestAssert.True(dashIndex >= 0,
                    $"{team}: observer receives Ezreal's authoritative kick dash");
                TestAssert.False(packets.Skip(dashIndex + 1).Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == ezreal.NetId)),
                    $"{team}: R windup cannot overwrite the attached kick route");
            }
            match.Clear();
            for (var frame = 0; frame < 15 && ezreal.ForcedMove is not null; frame++)
                runtime.Tick(.1);
            TestAssert.True(ezreal.ForcedMove is null,
                $"{team}: Lee Sin owns normal kick completion");
            foreach (var capture in match.Captures)
                TestAssert.True(capture.Packets.Any(packet =>
                    packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.EntityReferences.Any(reference => reference.NetId == ezreal.NetId)),
                    $"{team}: kick owner publishes its terminal waypoint");
            runtime.Tick(.8);
            TestAssert.True(!EzrealFlights.WindingUp(runtime.Spells, ezreal) &&
                EzrealFlights.InFlight(runtime.Spells, ezreal).Length == 1,
                $"{team}: the protected Trueshot windup launches once after the kick");
        }
    }
}
