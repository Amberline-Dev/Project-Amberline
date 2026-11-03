using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 583 (1 November 2026): the hand-test checklist's open item "the toss height of Pulverize". The server sends no height: the client draws it
/// from the dash it is sent. Pulverize.OnBuffActivate moves the target to a random point 10 units away (BBGetRandomPointInAreaUnit 10 / 10, then
/// BBMove Speed 10, Gravity 20, IdealDistance 10, Pulverize.luaobj), a one-second hop. The client's
/// initial-velocity routine takes the travel time T = distance / speed
/// (speed at least 0.1) and the gravity times 150, and starts the rise at ((end height - height) + T x 0.5 x g)
/// x 1.05; the parabolic-movement step moves it every 0.01 s. For T = 1 s and g = 3000 the peak is about
/// 1575^2 / (2 x 3000) = 413 units, between about 265 and 600 as the 2-unit lattice rounds the 10-unit hop. So the toss is the shipped one as long
/// as the dash carries speed 10, gravity 20 and the 10-unit end.
/// </summary>
public static class BbAlistarChecks
{
    [Check("scripting.bb-alistar-pulverize-tosses-with-a-ten-unit-hop-at-speed-ten-and-gravity-twenty")]
    public static void PulverizeTossIsTheShippedHop()
    {
        using var match = new Match("Alistar", foeChampion: "Ezreal");
        match.Learn(1);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7200, 7000);
        var origin = match.Foe.Position;
        var wire = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(0), "Pulverize is accepted");
        ForcedMoveState? toss = null;
        for (var elapsed = 0.0; elapsed < 1.0 && toss is null; elapsed += Step)
        {
            match.Advance(Step);
            toss = match.Foe.ForcedMove;
        }
        TestAssert.True(toss is not null, "the foe within 375 is tossed");
        TestAssert.Equal(10.0, toss!.Speed, "the shipped Speed 10");
        TestAssert.Equal(20.0, toss.DashGravity, "the shipped Gravity 20 goes to the dash");
        var hop = Math.Sqrt(Math.Pow(toss.Destination.X - origin.X, 2) + Math.Pow(toss.Destination.Z - origin.Z, 2));
        TestAssert.True(Math.Abs(hop - 10.0) <= 2.0 * Math.Sqrt(2.0) + 1e-6, $"a 10-unit hop, the client's lattice aside ({hop:0.00}); a zero hop would be no toss at all");
        match.Advance(Step);
        var dash = wire.Packets.Select(item => item.Packet.Plaintext.ToArray())
            .LastOrDefault(bytes => bytes[0] == VisionPackets.DashOpcode && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)) == match.Foe.NetId);
        TestAssert.True(dash is not null, "the observer is sent the 0x67 dash of the foe");
        TestAssert.Equal(10.0f, BitConverter.ToSingle(dash!, 19), "its speed field is 10");
        TestAssert.Equal(20.0f, BitConverter.ToSingle(dash!, 23), "its gravity field is 20");
        match.Advance(1.2);
        TestAssert.True(match.Foe.ForcedMove is null, "the hop is over after its second");
    }
}
