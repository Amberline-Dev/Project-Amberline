using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

// Batch 276: a revealed unit's route, dash or stop goes inside its 0xC2 (the stock client's movement data reader) and no
// reveal-time 0xC1 or 0x67 follows it. The bytes are read here as the client reads them, independently of the server's writer.
public static partial class ChampionLiveRegressionChecks
{
    [Check("replication.reveal-carries-a-walking-units-route-in-its-0xc2")]
    public static void RevealCarriesAWalkingUnitsRoute()
    {
        using var match = new Match("Annie", Team.Order);
        var enemy = HideEnemy(match);
        enemy.Position = new Position2(7600, 7000);
        // Mixed deltas: 20/5 and 5 fit a signed byte (flag bit set), 380 and 295 need a short (flag bit clear).
        enemy.Waypoints = [new(7640, 7010), new(8400, 7010), new(8410, 7600)];
        var route = new[] { enemy.Position }.Concat(enemy.Waypoints).ToArray();
        var packets = RevealFlush(match, enemy);

        var enter = EnterOf(packets, enemy.NetId);
        TestAssert.Equal(2, (int)enter[7], "a walking unit's reveal carries movement data type 2 (a 0x64 entry)");
        TestAssert.True(BinaryPrimitives.ReadUInt32LittleEndian(enter.AsSpan(8)) > 0,
            "the route carries a real sync id, not 0 (the client gates it like any route's)");
        var header = BinaryPrimitives.ReadUInt32LittleEndian(enter.AsSpan(12));
        TestAssert.Equal(route.Length, (int)((header >> 16) & 0x7f), "the entry's point count is the position plus the waypoints");
        TestAssert.Equal(0u, header & 1, "no teleport flag");
        TestAssert.Equal(enemy.NetId, BinaryPrimitives.ReadUInt32LittleEndian(enter.AsSpan(16)), "the entry names the unit");
        var points = ClientPoints(enter.AsSpan(20), route.Length, out var read);
        TestAssert.Equal(enter.Length, 20 + read, "the points end the packet");
        var bounds = MapBounds();
        for (var index = 0; index < route.Length; index++)
            TestAssert.Equal(VisionPackets.Compress(route[index], bounds), points[index], $"point {index} on the client's grid");
        AssertNoRevealRoute(packets, enemy.NetId);
    }

    [Check("replication.reveal-carries-a-standing-units-stop-with-a-route-sync-id")]
    public static void RevealCarriesAStandingUnitsStop()
    {
        using var match = new Match("Annie", Team.Order);
        var enemy = HideEnemy(match);
        enemy.Position = new Position2(7600, 7000);
        enemy.Waypoints = [];
        var packets = RevealFlush(match, enemy);

        var enter = EnterOf(packets, enemy.NetId);
        TestAssert.Equal(3, (int)enter[7], "a standing unit's reveal carries a stop (type 3)");
        // The client keeps one sync id per unit for every route and stop (its per-unit movement sync field); a stop with id 0 is dropped for
        // any unit that already had a route, so the stop takes a route id.
        TestAssert.True(BinaryPrimitives.ReadUInt32LittleEndian(enter.AsSpan(8)) > 0, "the stop carries a real sync id");
        TestAssert.Near(7600, BinaryPrimitives.ReadSingleLittleEndian(enter.AsSpan(12)), 1e-3, "stop x");
        TestAssert.Near(7000, BinaryPrimitives.ReadSingleLittleEndian(enter.AsSpan(16)), 1e-3, "stop z");
        TestAssert.Equal(28, enter.Length, "the stop's position and facing end the packet");
        AssertNoRevealRoute(packets, enemy.NetId);
    }

    [Check("replication.reveal-carries-a-dashing-units-dash-in-its-0xc2")]
    public static void RevealCarriesADashingUnitsDash()
    {
        using var match = new Match("Annie", Team.Order);
        var enemy = HideEnemy(match);
        enemy.Position = new Position2(7600, 7000);
        enemy.Waypoints = [];
        enemy.ForcedMove = new ForcedMoveState(new Position2(8200, 7000), 1200, 600, enemy.NetId, "TestDash")
        {
            DashPresentation = true,
        };
        var packets = RevealFlush(match, enemy);

        var enter = EnterOf(packets, enemy.NetId);
        TestAssert.Equal(1, (int)enter[7], "a dashing unit's reveal carries movement data type 1 (a 0x67 entry)");
        var syncId = BinaryPrimitives.ReadUInt32LittleEndian(enter.AsSpan(8));
        TestAssert.True(syncId > 0, "the dash carries a real sync id");
        var dash = VisionPackets.Dash(enemy.NetId, enemy.Position, new Position2(8200, 7000), 1200f, MapBounds(), syncId)
            .Plaintext.ToArray();
        TestAssert.True(enter.AsSpan(12).SequenceEqual(dash.AsSpan(11)),
            "the reveal's entry is the 0x67 entry of the same dash (the client's entry reader reads both)");
        AssertNoRevealRoute(packets, enemy.NetId);
    }

    private static Entity HideEnemy(Match match)
    {
        var enemy = match.Hero(1);
        enemy.Position = new Position2(12500, 12500);
        match.Runtime.Delivery.Flush();
        TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId), "the enemy is out of sight");
        match.Runtime.World.Clock.SetVirtual(30.0);
        match.Clear();
        return enemy;
    }

    private static List<PlaintextPacket> RevealFlush(Match match, Entity enemy)
    {
        match.Runtime.Delivery.Flush();
        TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId), "the enemy is revealed");
        return match.Captures[0].Packets.ToList();
    }

    private static byte[] EnterOf(IEnumerable<PlaintextPacket> packets, uint netId) =>
        packets.Select(packet => packet.Plaintext.ToArray()).Single(bytes =>
            bytes[0] == VisionPackets.EnterVisibilityOpcode && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == netId &&
            bytes[6] == 0);

    private static void AssertNoRevealRoute(IEnumerable<PlaintextPacket> packets, uint netId) =>
        TestAssert.False(packets.Any(packet => packet.Plaintext.Span[0] is VisionPackets.WaypointListOpcode or VisionPackets.DashOpcode &&
                packet.EntityReferences.Any(reference => reference.NetId == netId)),
            "no reveal-time 0xC1 or 0x67 follows the reveal");

    // The reveal's movement entry as the one-entry group packet whose entries the client reads with the same function (type 1: a 0x67
    // entry, the dash reader; type 2: a 0x64 entry, the teleport reader), decoded by the wire decoder.
    private static JsonElement DecodeRevealEntry(byte[] enter)
    {
        var group = new byte[11 + enter.Length - 12];
        group[0] = enter[7] == VisionPackets.MovementDataRouteWithSpeed ? VisionPackets.DashOpcode : VisionPackets.TeleportOpcode;
        enter.AsSpan(8, 4).CopyTo(group.AsSpan(5));
        group[9] = 1;
        enter.AsSpan(12).CopyTo(group.AsSpan(11));
        return JsonSerializer.SerializeToElement(
            WirePacketDecoder.Decode(group, LeagueProtocolConstants.LowPriorityChannel).Fields);
    }

    private static NavigationBounds MapBounds()
    {
        var grid = NavigationGrid.LoadDefault();
        return new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
    }

    // The client's path-from-network-data reader: for n > 1, ((n - 2) >> 2) + 1 flag bytes; the first point as two
    // shorts; then per coordinate (bit 2(i - 1) for x, the next for z) a set bit reads a signed byte delta, a clear bit a short.
    private static List<(short X, short Z)> ClientPoints(ReadOnlySpan<byte> bytes, int count, out int read)
    {
        var flagBytes = count > 1 ? ((count - 2) >> 2) + 1 : 0;
        var at = flagBytes;
        short x = 0, z = 0;
        var points = new List<(short X, short Z)>();
        for (var index = 0; index < count; index++)
        {
            if (index == 0)
            {
                x = BinaryPrimitives.ReadInt16LittleEndian(bytes[at..]);
                z = BinaryPrimitives.ReadInt16LittleEndian(bytes[(at + 2)..]);
                at += 4;
            }
            else
            {
                x = ReadCoordinate(bytes, 2 * (index - 1), x, ref at);
                z = ReadCoordinate(bytes, 2 * (index - 1) + 1, z, ref at);
            }
            points.Add((x, z));
        }
        read = at;
        return points;
    }

    private static short ReadCoordinate(ReadOnlySpan<byte> bytes, int bit, short previous, ref int at)
    {
        if ((bytes[bit >> 3] & (1 << (bit & 7))) != 0)
            return (short)(previous + (sbyte)bytes[at++]);
        var value = BinaryPrimitives.ReadInt16LittleEndian(bytes[at..]);
        at += 2;
        return value;
    }
}
