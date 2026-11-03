using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

// Batch 291: batch 276's reveal follow-ups, read against the stock client's per-unit movement sync gate.
public static partial class ChampionLiveRegressionChecks
{
    [Check("replication.ball-re-entry-stop-passes-the-clients-sync-gate")]
    public static void BallReEntryStopPassesTheClientsSyncGate()
    {
        using var match = new Match("Annie", Team.Order);
        var owner = match.Hero(0);
        var enemy = match.Hero(1);
        var at = new Position2(7300, 7000);
        var ball = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Monster,
            UnitType.Monster, Team.Order, at)
        {
            Name = "OriannaBall", ChampionName = "OriannaBall", OwnerNetId = owner.NetId, Placemarker = true, Protected = true,
            Created = false, Alive = true, Health = 1, MaxHealth = 1,
        });
        // The production announcement (BbNativeHandlers.AnnounceBall): the projection and the ball's placement 0xC1.
        match.Runtime.Effects.SendOriannaBall(new OriannaBallReplication(ball.NetId, at, 100, 1, 0, 0, 0, 0, owner.NetId),
            teleport: false, teleportId: 0);
        match.Runtime.Delivery.Flush();
        var watch = enemy.Position;
        enemy.Position = new Position2(12500, 12500);
        match.Runtime.Delivery.Flush();
        TestAssert.False(match.Runtime.Session.Participants[1].View.Visible.Contains(ball.NetId), "the ball leaves the enemy's sight");
        enemy.Position = watch;
        match.Runtime.Delivery.Flush();
        TestAssert.True(match.Runtime.Session.Participants[1].View.Visible.Contains(ball.NetId), "the ball re-enters the enemy's sight");

        var gate = ClientMovementGate(match.Captures[1].Packets, ball.NetId);
        TestAssert.Equal(2, gate.Count(entry => entry.Opcode == VisionPackets.EnterVisibilityOpcode), "first sight and re-entry");
        TestAssert.True(gate.Any(entry => entry.Opcode == VisionPackets.WaypointListOpcode), "the placement 0xC1 reached the enemy");
        TestAssert.True(gate.All(entry => entry.Accepted),
            "every movement packet of the ball passes the client's gate: " +
            string.Join(", ", gate.Select(entry => $"0x{entry.Opcode:X2}/{entry.SyncId}/{entry.Accepted}")));
    }

    [Check("replication.stale-dash-reveal-carries-the-teleport-in-its-entry")]
    public static void StaleDashRevealCarriesTheTeleportInItsEntry()
    {
        using var match = new Match("Annie", Team.Order);
        var watcher = match.Hero(0);
        var enemy = match.Hero(1);
        var view = match.Runtime.Session.Participants[0].View;
        var watchPoint = watcher.Position;
        var destination = new Position2(8200, 7000);

        // Lost mid-dash, revealed while still dashing: the dash entry (type 1, the client's dash reader) with the teleport flag.
        StartAndLoseDash(match, watcher, enemy, destination);
        enemy.Position = new Position2(7600, 7000);
        watcher.Position = watchPoint;
        match.Clear();
        match.Runtime.Delivery.Flush();
        var dashReveal = Reveal(match, enemy, view);
        TestAssert.Equal(VisionPackets.MovementDataRouteWithSpeed, dashReveal[7], "still dashing: a dash entry");
        var dashHeader = BinaryPrimitives.ReadUInt32LittleEndian(dashReveal.AsSpan(12));
        TestAssert.Equal(1u, dashHeader & 1, "the entry's teleport flag (bit 0)");
        TestAssert.Equal(2u, (dashHeader >> 16) & 0x7f, "two points");
        TestAssert.Equal(enemy.NetId, BinaryPrimitives.ReadUInt32LittleEndian(dashReveal.AsSpan(16)), "the entry names the unit");
        TestAssert.Equal(enemy.TeleportId, dashReveal[20], "the teleport id follows the net id");
        var dash = VisionPackets.Dash(enemy.NetId, enemy.Position, destination, 1200f, MapBounds(),
            BinaryPrimitives.ReadUInt32LittleEndian(dashReveal.AsSpan(8))).Plaintext.ToArray();
        TestAssert.True(dashReveal.AsSpan(21).SequenceEqual(dash.AsSpan(19)), "then the 0x67 entry's speed data and points");
        var dashTeleport = enemy.TeleportId;

        // Lost mid-dash, revealed walking: the route entry (type 2, HandleWaypoint) with the teleport flag.
        StartAndLoseDash(match, watcher, enemy, destination);
        enemy.ForcedMove = null;
        enemy.Position = new Position2(7600, 7000);
        enemy.Waypoints = [new(7640, 7010), new(8400, 7010)];
        var route = new[] { enemy.Position }.Concat(enemy.Waypoints).ToArray();
        watcher.Position = watchPoint;
        match.Clear();
        match.Runtime.Delivery.Flush();
        var walkReveal = Reveal(match, enemy, view);
        TestAssert.Equal(VisionPackets.MovementDataRoute, walkReveal[7], "walking: a route entry");
        var walkHeader = BinaryPrimitives.ReadUInt32LittleEndian(walkReveal.AsSpan(12));
        TestAssert.Equal(1u, walkHeader & 1, "the entry's teleport flag (bit 0)");
        TestAssert.Equal(route.Length, (int)((walkHeader >> 16) & 0x7f), "the route's points");
        TestAssert.Equal((byte)(dashTeleport + 1), walkReveal[20], "a fresh teleport id after the net id");
        TestAssert.Equal(enemy.TeleportId, walkReveal[20], "the unit holds the id it sent");
        var points = ClientPoints(walkReveal.AsSpan(21), route.Length, out var read);
        TestAssert.Equal(walkReveal.Length, 21 + read, "the points end the packet");
        for (var index = 0; index < route.Length; index++)
            TestAssert.Equal(VisionPackets.Compress(route[index], MapBounds()), points[index], $"point {index}");
    }

    private static void StartAndLoseDash(Match match, Entity watcher, Entity enemy, Position2 destination)
    {
        enemy.Position = new Position2(7200, 7000);
        enemy.Waypoints = [];
        enemy.ForcedMove = new ForcedMoveState(destination, 1200, 600, enemy.NetId, "TestDash") { DashPresentation = true };
        match.Runtime.Delivery.Flush();
        TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId), "the dash starts in sight");
        watcher.Position = new Position2(1000, 14000);
        match.Runtime.Delivery.Flush();
        TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId), "sight is lost mid-dash");
    }

    // The reveal's 0xC2: no 0x64 ahead of it, and its movement data passes the client's gate.
    private static byte[] Reveal(Match match, Entity enemy, ReplicationView view)
    {
        TestAssert.True(view.Visible.Contains(enemy.NetId), "the enemy is revealed");
        var packets = match.Captures[0].Packets.ToList();
        TestAssert.False(packets.Any(packet => packet.Plaintext.Span[0] == VisionPackets.TeleportOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == enemy.NetId)), "no 0x64 ahead of the reveal");
        TestAssert.True(ClientMovementGate(packets, enemy.NetId).All(entry => entry.Accepted), "the client takes the reveal");
        return EnterOf(packets, enemy.NetId);
    }

    // The client's per-unit movement sync id (a field of its movement manager): the constructor starts it at -1; the route gate and
    // the stop gate drop an id that is older (signed) by less than 0x40000000 and otherwise store it. The movement packets
    // of one unit, in arrival order: a 0xC1, a 0x64 or 0x67 group (first entry), and a 0xC2's movement data.
    private static List<(byte Opcode, uint SyncId, bool Accepted)> ClientMovementGate(IEnumerable<PlaintextPacket> packets, uint netId)
    {
        var stored = -1;
        var result = new List<(byte, uint, bool)>();
        foreach (var bytes in packets.Select(packet => packet.Plaintext.ToArray()))
        {
            uint? syncId = bytes[0] switch
            {
                VisionPackets.WaypointListOpcode when U32At(bytes, 1) == netId => U32At(bytes, 5),
                VisionPackets.TeleportOpcode or VisionPackets.DashOpcode when bytes.Length >= 19 && U32At(bytes, 15) == netId => U32At(bytes, 5),
                VisionPackets.EnterVisibilityOpcode when U32At(bytes, 1) == netId && bytes[6] == 0 && bytes.Length >= 12 => U32At(bytes, 8),
                _ => null,
            };
            if (syncId is not uint id) continue;
            var incoming = unchecked((int)id);
            var accepted = !(incoming < stored && stored - incoming < 0x40000000);
            if (accepted) stored = incoming;
            result.Add((bytes[0], id, accepted));
        }
        return result;
    }

    private static uint U32At(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    /// <summary>
    /// Whether the packet tells the client a dash of the unit: a 0x67 on the movement channel, or (batch 276) a reveal's 0xC2 with movement
    /// data type 1, which the client reads with the same entry reader (its movement data reader chain).
    /// </summary>
    internal static bool AnnouncesDash(PlaintextPacket packet, uint netId)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 19 && bytes[0] == VisionPackets.DashOpcode && packet.Channel == LeagueProtocolConstants.LowPriorityChannel &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]) == netId ||
               bytes.Length > 7 && bytes[0] == VisionPackets.EnterVisibilityOpcode && bytes[6] == 0 &&
               bytes[7] == VisionPackets.MovementDataRouteWithSpeed && BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == netId;
    }

    /// <summary>The last point of the one dash of the unit the packets announce (<see cref="AnnouncesDash"/>), as the wire decoder reads it.</summary>
    private static Position2 AnnouncedDashEnd(IEnumerable<PlaintextPacket> packets, uint netId)
    {
        var points = Decode(packets.Single(packet => AnnouncesDash(packet, netId))).GetProperty("waypoints");
        var end = points[points.GetArrayLength() - 1];
        return new Position2(end[0].GetDouble(), end[1].GetDouble());
    }
}
