using System.Globalization;
using Amberline.Core;

namespace Amberline.Network.Protocol;

public static class VisionPackets
{
    public const byte SetCameraPositionOpcode = 0x76;
    public const byte LockCameraOpcode = 0x7C;
    public const byte WaypointListOpcode = 0xC1;
    public const byte DashOpcode = 0x67;
    public const byte TeleportOpcode = 0x64;
    public const byte EnterVisibilityOpcode = 0xC2;
    public const byte EnterLocalVisibilityOpcode = 0xB5;
    public const byte LeaveVisibilityOpcode = 0x54;
    public const byte LeaveLocalVisibilityOpcode = 0x3A;

    public static Position2 FacingTowards(Position2 current, Position2 target,
        Position2 fallback = default)
    {
        if (fallback == default)
            fallback = new Position2(0.0, 1.0);
        var dx = target.X - current.X;
        var dz = target.Z - current.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return double.IsFinite(length) && length > 1e-6
            ? new Position2(dx / length, dz / length)
            : fallback;
    }

    /// <summary>
    /// SetCameraPosition (0x76, 17 bytes): the hero and the point's three floats. Batch 460: the height is the point's own, as the
    /// shared BBSetCameraPosition body writes the Position variable's whole vector; it was 0,
    /// which put the point above The Crystal Scar's ground (its base floor is about -35, AIPath.aimesh_ngrid) and below Summoner's Rift's (about 175).
    /// </summary>
    public static PlaintextPacket SetCameraPosition(uint targetNetId, Position3 position)
    {
        using var writer = ProtocolEncoding.Writer(17, out var stream);
        writer.Write(SetCameraPositionOpcode);
        writer.Write(targetNetId);
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Y, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(targetNetId));
    }

    /// <summary>
    /// LockCamera (0x7C, 6 bytes): <c>[0x7C][netid u32][lock u8]</c> (<c>LockCamera</c>: <c>bool lock</c> at +5).
    /// Batch 580: the case 0x7C handler passes the byte at +5 to the camera's slot 0xc.
    /// The Basic Tutorial's <c>LockCamera</c> native sends it (TutorialMatchEngine); the scenario director sends it to make a telemetry client's camera follow its hero.
    /// </summary>
    public static PlaintextPacket LockCamera(uint heroNetId, bool locked)
    {
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write(LockCameraOpcode);
        writer.Write(heroNetId);
        writer.Write((byte)(locked ? 1 : 0));
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(heroNetId));
    }

    public static PlaintextPacket ChangeCharacterData(
        uint netId, string skinName, bool useSpells = false, uint stackId = 0)
    {
        ProtocolEncoding.RequireAsciiLength(skinName, int.MaxValue, nameof(skinName));
        if (skinName.Contains('\0'))
            throw new ArgumentException("Skin name cannot contain NUL.", nameof(skinName));
        using var writer = ProtocolEncoding.Writer(10 + ProtocolEncoding.Ascii.GetByteCount(skinName), out var stream);
        writer.Write((byte)0x9F);
        writer.Write(netId);
        writer.Write(stackId);
        writer.Write((byte)(useSpells ? 1 : 0));
        writer.Write(ProtocolEncoding.Ascii.GetBytes(skinName));
        writer.Write((byte)0);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket AvatarInfo(
        uint netId,
        string summonerSpell1 = SummonerLoadout.DefaultFirst,
        string summonerSpell2 = SummonerLoadout.DefaultSecond,
        RunePage? runes = null,
        MasteryPage? masteries = null,
        byte accountLevel = 1)
    {
        var loadout = SummonerLoadout.ValidatePair(summonerSpell1, summonerSpell2);
        if (accountLevel is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(accountLevel), "Account level must be 1..30.");
        var runePage = runes ?? RunePage.Empty;
        var masteryPage = masteries ?? MasteryPage.Empty;
        if (runePage.WireIds.Count != RunePage.TotalSlots)
            throw new ArgumentException("AvatarInfo requires exactly 30 rune positions.", nameof(runes));
        if (masteryPage.Allocations.Count > 80)
            throw new ArgumentOutOfRangeException(nameof(masteries), "AvatarInfo supports at most 80 talent entries.");
        using var writer = ProtocolEncoding.Writer(534, out var stream);
        writer.Write((byte)0x2C);
        writer.Write(netId);
        foreach (var runeId in runePage.WireIds)
            writer.Write(runeId);
        writer.Write(ProtocolEncoding.HashString(loadout.First));
        writer.Write(ProtocolEncoding.HashString(loadout.Second));
        foreach (var (id, rank) in masteryPage.Allocations.OrderBy(pair => pair.Key))
        {
            writer.Write(ProtocolEncoding.HashString(id.ToString(CultureInfo.InvariantCulture)));
            writer.Write(checked((byte)rank));
        }
        for (var index = masteryPage.Allocations.Count; index < 80; index++)
        {
            writer.Write(0u);
            writer.Write((byte)0);
        }
        writer.Write(accountLevel);
        writer.Flush();
        if (stream.Length != 534)
            throw new InvalidOperationException("AvatarInfo must be 534 bytes.");
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    /// <summary>
    /// The 0xC2 body: <c>[item count 0][look-at 0][movement data]</c>. The movement data defaults to a stop with sync id 0, which the client
    /// takes on a unit's first sight: the client's movement manager starts the unit's sync id at -1, the spawn
    /// packets carry none, and only the movement gates write it. A unit the client may already
    /// have a route for needs its route id (ProtocolReplicationServices.RevealMovement, the ball's BallRouteSyncId).
    /// </summary>
    public static byte[] BuildEnterVisibilityBody(
        uint netId,
        string skinName,
        int skinId,
        float maxHealth,
        float health,
        Position2 position,
        Position2 facing,
        IReadOnlyList<PlaintextPacket>? embeddedPackets = null,
        byte lookAtType = 0,
        bool isHero = true,
        byte[]? movementData = null)
    {
        movementData ??= StopMovementData(0, position, facing);
        using var writer = ProtocolEncoding.Writer(2 + movementData.Length, out var stream);
        writer.Write((byte)0);
        writer.Write(lookAtType);
        writer.Write(movementData);
        writer.Flush();
        return ProtocolEncoding.Finish(stream);
    }

    // The reveal's movement data, read by the stock client's movement reader (called from the 0xC2 handler):
    // [type u8][sync id u32][payload]. The sync id goes through the same per-unit gate as a 0xC1, 0x64 or 0x67. The gate accepts an id no older than the stored one,
    // so a stale one is dropped. Type 1 is a 0x67 entry, type 2 a 0x64 entry (the HandleWaypoint reader),
    // type 3 a stop (the PausePacket reader).
    public const byte MovementDataRouteWithSpeed = 1;
    public const byte MovementDataRoute = 2;
    public const byte MovementDataStop = 3;

    /// <summary>The offset of the movement data type in a 0xC2 packet with no look-at (opcode, net id, item count, look-at flag).</summary>
    public const int EnterVisibilityMovementTypeOffset = 7;

    /// <summary>Type 3: position and facing (PausePacket).</summary>
    public static byte[] StopMovementData(uint syncId, Position2 position, Position2 facing)
    {
        using var writer = ProtocolEncoding.Writer(21, out var stream);
        writer.Write(MovementDataStop);
        writer.Write(syncId);
        writer.Write(ProtocolEncoding.Float32(position.X, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(position.Z, nameof(position)));
        writer.Write(ProtocolEncoding.Float32(facing.X, nameof(facing)));
        writer.Write(ProtocolEncoding.Float32(facing.Z, nameof(facing)));
        writer.Flush();
        return ProtocolEncoding.Finish(stream);
    }

    /// <summary>
    /// Type 2: a route as one 0x64 entry, with the teleport flag and id when given. Null when the route does not fit an entry (more than
    /// 127 points).
    /// </summary>
    public static byte[]? RouteMovementData(uint netId, IReadOnlyList<Position2> waypoints, NavigationBounds bounds, uint syncId,
        byte? teleportId = null)
    {
        if (!FitsWaypointEntry(waypoints)) return null;
        using var writer = ProtocolEncoding.Writer(14 + waypoints.Count * 4, out var stream);
        writer.Write(MovementDataRoute);
        writer.Write(syncId);
        WriteWaypointEntry(writer, netId, waypoints, bounds, teleportId);
        writer.Flush();
        return ProtocolEncoding.Finish(stream);
    }

    /// <summary>Type 1: a dash as one 0x67 entry, the same bytes <see cref="Dash"/> sends, with the teleport flag and id when given.</summary>
    public static byte[] DashMovementData(uint netId, Position2 start, Position2 destination, float speed, NavigationBounds bounds,
        uint syncId, uint followNetId = 0, float followDistance = 0.0f, float gravity = 0.0f, float followBackDistance = 0.0f,
        byte? teleportId = null)
    {
        using var writer = ProtocolEncoding.Writer(56, out var stream);
        writer.Write(MovementDataRouteWithSpeed);
        writer.Write(syncId);
        WriteDashEntry(writer, netId, start, destination, speed, bounds, followNetId, followDistance, gravity, followBackDistance,
            teleportId);
        writer.Flush();
        return ProtocolEncoding.Finish(stream);
    }

    // The entry header keeps the point count in bits 16-22 (HandleWaypoint: (header >> 16) & 0x7f; a count of 0 ends a group).
    public static bool FitsWaypointEntry(IReadOnlyList<Position2> waypoints) => waypoints.Count is > 0 and <= 0x7f;

    /// <summary>
    /// One 0x64 entry (the HandleWaypoint reader): <c>[header u32: count &lt;&lt; 16 | teleport flag][net id u32]
    /// [teleport id u8 when flagged]</c>, then the points (<see cref="WritePath"/>).
    /// The client acts on the flag (batch 316): the reader reads it and the id, and after the path set
    /// (the client's path-set routine, which keeps the path's first point in the actor) it snaps the unit to that
    /// point (the unit's position is set through its virtual setter) when the id differs from the unit's last one (kept per unit) and the
    /// unit stands more than 50 units from it (the x and z are tested apart); the 0x67 entry does the
    /// same. Without the flag the unit follows the path from where it is. The client's own copy of the
    /// encoder clears the flag, but its one caller is the C2S order send; the decoders
    /// above are what the server's flag reaches, so the server's teleport entries are retail-consistent.
    /// </summary>
    public static void WriteWaypointEntry(BinaryWriter writer, uint netId, IReadOnlyList<Position2> waypoints, NavigationBounds bounds,
        byte? teleportId = null)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        if (!FitsWaypointEntry(waypoints))
            throw new ArgumentOutOfRangeException(nameof(waypoints), "A waypoint entry carries 1..127 points.");
        writer.Write(((uint)waypoints.Count << 16) | (teleportId is null ? 0u : 1u));
        writer.Write(netId);
        if (teleportId is byte id) writer.Write(id);
        WritePath(writer, waypoints, bounds);
    }

    /// <summary>
    /// The points of a 0x64 or 0x67 entry as the client's path encoder writes them (the aipath code the client links for its
    /// own order send) and as its path decoder reads them: for n &gt; 1, ((n - 2) &gt;&gt; 2) + 1
    /// flag bytes, the first point as two shorts of the 2-unit grid (<see cref="Compress"/>), then for every
    /// later coordinate (bit 2(i - 1) for x, the next for z) a set flag bit and a signed byte delta from the previous one when
    /// |(short)delta| &lt; 0x80, else a clear bit and a short (so a delta of -128 is a short).
    /// </summary>
    private static void WritePath(BinaryWriter writer, IReadOnlyList<Position2> waypoints, NavigationBounds bounds)
    {
        var count = waypoints.Count;
        var flags = new byte[count > 1 ? ((count - 2) >> 2) + 1 : 0];
        var coordinates = new List<byte>(count * 4);
        var previous = Compress(waypoints[0], bounds, nameof(waypoints));
        AddShort(coordinates, previous.X);
        AddShort(coordinates, previous.Z);
        for (var index = 1; index < count; index++)
        {
            var point = Compress(waypoints[index], bounds, nameof(waypoints));
            AddCoordinate(coordinates, flags, 2 * (index - 1), previous.X, point.X);
            AddCoordinate(coordinates, flags, 2 * (index - 1) + 1, previous.Z, point.Z);
            previous = point;
        }
        writer.Write(flags);
        writer.Write(coordinates.ToArray());
    }

    private static void AddCoordinate(List<byte> coordinates, byte[] flags, int bit, short previous, short value)
    {
        var delta = unchecked((short)(value - previous));
        if (Math.Abs((int)delta) < 0x80)
        {
            flags[bit >> 3] |= (byte)(1 << (bit & 7));
            coordinates.Add(unchecked((byte)(sbyte)delta));
        }
        else AddShort(coordinates, value);
    }

    private static void AddShort(List<byte> bytes, short value)
    {
        bytes.Add(unchecked((byte)value));
        bytes.Add(unchecked((byte)(value >> 8)));
    }

    // Reconstruction (batch 276): every route goes out as its own 0xC1. The client also reads a group, 0x64
    // (the group reader: [0x64][u32 unused][sync id][i16 count][count x HandleWaypoint entries], one
    // C2S 0x7A acknowledgement per packet), which can hold several units' routes under one sync id, i.e. one server step. Both reach
    // the same per-unit sync gate (the unit's sync id field) and the same path set; the group's points are on the 2-unit
    // grid, the 0xC1's are floats. The client does not show which one retail used for an ordinary route, nor how it grouped them: it links
    // the entry encoder (see WritePath) only for its own order send and builds no
    // group, so the grouping is server code no binary we hold carries (batch 315). One hazard
    // is known: an entry for an object the client lacks returns without consuming its points, so the rest of
    // that group would be misread.
    public static PlaintextPacket WaypointList(
        uint netId, IReadOnlyList<Position2> waypoints, uint syncId = 1)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        using var writer = ProtocolEncoding.Writer(9 + waypoints.Count * 8, out var stream);
        writer.Write(WaypointListOpcode);
        writer.Write(netId);
        writer.Write(syncId);
        foreach (var point in waypoints)
        {
            writer.Write(ProtocolEncoding.Float32(point.X, nameof(waypoints)));
            writer.Write(ProtocolEncoding.Float32(point.Z, nameof(waypoints)));
        }
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId), channel: LeagueProtocolConstants.LowPriorityChannel);
    }

    public static PlaintextPacket Dash(
        uint netId,
        Position2 start,
        Position2 destination,
        float speed,
        NavigationBounds bounds,
        uint syncId = 1,
        uint followNetId = 0,
        float followDistance = 0.0f,
        float gravity = 0.0f,
        float followBackDistance = 0.0f)
    {
        using var writer = ProtocolEncoding.Writer(61, out var stream);
        writer.Write(DashOpcode);
        writer.Write(0u);
        writer.Write(syncId);
        writer.Write((short)1);
        WriteDashEntry(writer, netId, start, destination, speed, bounds, followNetId, followDistance, gravity, followBackDistance, null);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId), channel: LeagueProtocolConstants.LowPriorityChannel);
    }

    // One 0x67 entry: a two-point header (bit 0: the teleport flag), net id, the teleport id when flagged, the 0x21 bytes
    // of speed data, then the two points as the path encoder writes them (WritePath; the entry reader uses the same decoder), so a
    // hop under 256 units on an axis is a delta byte (batch 315; the packet is 57 to 61 bytes).
    private static void WriteDashEntry(BinaryWriter writer, uint netId, Position2 start, Position2 destination, float speed,
        NavigationBounds bounds, uint followNetId, float followDistance, float gravity, float followBackDistance, byte? teleportId)
    {
        if (!float.IsFinite(speed) || speed <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(speed));
        writer.Write((ushort)(teleportId is null ? 0 : 1));
        writer.Write((ushort)2);
        writer.Write(netId);
        if (teleportId is byte id) writer.Write(id);
        writer.Write(speed);
        writer.Write(gravity);
        writer.Write(ProtocolEncoding.Float32(start.X, nameof(start)));
        writer.Write(ProtocolEncoding.Float32(start.Z, nameof(start)));
        writer.Write((byte)0);
        writer.Write(followNetId);
        writer.Write(followDistance);
        writer.Write(followBackDistance);
        writer.Write(0.0f);
        WritePath(writer, [start, destination], bounds);
    }

    public static PlaintextPacket Teleport(
        uint netId,
        Position2 destination,
        int teleportId,
        NavigationBounds bounds,
        int syncId = 1,
        Position2? staging = null)
    {
        var points = staging.HasValue
            ? new[] { (staging.Value, teleportId - 1), (destination, teleportId) }
            : new[] { (destination, teleportId) };
        using var writer = ProtocolEncoding.Writer(11 + points.Length * 13, out var stream);
        writer.Write(TeleportOpcode);
        writer.Write(0u);
        writer.Write(syncId);
        writer.Write((short)points.Length);
        foreach (var (point, id) in points)
        {
            var packed = Compress(point, bounds, nameof(destination));
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(netId);
            writer.Write((byte)id);
            writer.Write(packed.X);
            writer.Write(packed.Z);
        }
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId), channel: LeagueProtocolConstants.LowPriorityChannel);
    }

    public static PlaintextPacket EnterLocalVisibility(uint netId, float maxHealth, float health)
    {
        using var writer = ProtocolEncoding.Writer(13, out var stream);
        writer.Write(EnterLocalVisibilityOpcode);
        writer.Write(netId);
        writer.Write(maxHealth);
        writer.Write(health);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket LeaveLocalVisibility(uint netId)
    {
        using var writer = ProtocolEncoding.Writer(5, out var stream);
        writer.Write(LeaveLocalVisibilityOpcode);
        writer.Write(netId);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId), encrypt: false);
    }

    public static PlaintextPacket EnterVisibilityClient(
        uint netId,
        string skinName = "",
        int skinId = 0,
        float maxHealth = 0.0f,
        float health = 0.0f,
        Position2? position = null,
        Position2? facing = null,
        IReadOnlyList<PlaintextPacket>? embeddedPackets = null,
        bool isHero = false,
        byte[]? movementData = null)
    {
        var body = BuildEnterVisibilityBody(netId, skinName, skinId, maxHealth, health,
            position ?? new Position2(400.0, 400.0), facing ?? new Position2(0.0, 1.0),
            embeddedPackets, isHero: isHero, movementData: movementData);
        using var writer = ProtocolEncoding.Writer(5 + body.Length, out var stream);
        writer.Write(EnterVisibilityOpcode);
        writer.Write(netId);
        writer.Write(body);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable,
            isHero ? Hero(netId) : World(netId));
    }

    public static IReadOnlyList<PlaintextPacket> EnterVisibility(
        uint netId,
        string skinName,
        int skinId,
        float maxHealth,
        float health,
        Position2? position = null,
        Position2? facing = null,
        IReadOnlyList<PlaintextPacket>? embeddedPackets = null)
        => new[]
        {
            EnterVisibilityClient(netId, skinName, skinId, maxHealth, health,
                position, facing, embeddedPackets, isHero: true),
            EnterLocalVisibility(netId, maxHealth, health)
        };

    public static IReadOnlyList<PlaintextPacket> LeaveVisibility(uint netId)
    {
        using var first = ProtocolEncoding.Writer(5, out var firstStream);
        first.Write(LeaveVisibilityOpcode);
        first.Write(netId);
        first.Flush();
        return new[]
        {
            Packet(firstStream, LeaguePacketReliability.Reliable, Hero(netId), encrypt: false),
            LeaveLocalVisibility(netId)
        };
    }

    public static PlaintextPacket EnterTeamVisibility(uint netId, byte team = 0)
    {
        if (team >= 4) throw new ArgumentOutOfRangeException(nameof(team));
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0xEE);
        writer.Write(netId);
        writer.Write(team);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static PlaintextPacket LeaveTeamVisibility(uint netId, byte team = 0)
    {
        if (team >= 4) throw new ArgumentOutOfRangeException(nameof(team));
        using var writer = ProtocolEncoding.Writer(6, out var stream);
        writer.Write((byte)0xEF);
        writer.Write(netId);
        writer.Write(team);
        writer.Flush();
        return Packet(stream, LeaguePacketReliability.Reliable, Hero(netId));
    }

    public static (short X, short Z) Compress(Position2 position, NavigationBounds bounds, string parameterName = "position")
    {
        var sumX = bounds.MinX + bounds.MaxX;
        var sumZ = bounds.MinZ + bounds.MaxZ;
        if (!double.IsFinite(sumX) || !double.IsFinite(sumZ) ||
            sumX < int.MinValue || sumX > int.MaxValue ||
            sumZ < int.MinValue || sumZ > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        var offsetX = (int)((int)sumX / 2.0);
        var offsetZ = (int)((int)sumZ / 2.0);
        var x = GridCoordinate(position.X, offsetX);
        var z = GridCoordinate(position.Z, offsetZ);
        if (!float.IsFinite(x) || !float.IsFinite(z) ||
            x < short.MinValue || x > short.MaxValue || z < short.MinValue || z > short.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Position is outside the signed-short waypoint grid.");
        return ((short)x, (short)z);
    }

    /// <summary>
    /// One coordinate on the 2-unit wire grid as GetCompressedPos (NavigationHelper) computes it: the coordinate as the
    /// float32 the engine keeps it in, minus the integer origin, over the integer step 2 (x87, exact), rounded to float32 by the
    /// <c>fstps</c> store, then truncated toward zero (<c>cvttss2si</c>). A double that rounds up to
    /// the next float32, or a quotient whose float32 rounding reaches a whole number, lands one grid step from a double truncation
    /// (batch 316).
    /// </summary>
    internal static float GridCoordinate(double value, int origin) =>
        MathF.Truncate((float)(((double)(float)value - origin) / 2.0));

    private static PlaintextPacket Packet(
        MemoryStream stream,
        LeaguePacketReliability reliability,
        LeaguePacketEntityReference reference,
        bool encrypt = true,
        byte channel = LeagueProtocolConstants.VariantChannel)
        => ProtocolEncoding.Packet(channel, ProtocolEncoding.Finish(stream), encrypt, reliability, reference.NetId == 0 ? Array.Empty<LeaguePacketEntityReference>() : new[] { reference });

    private static LeaguePacketEntityReference Hero(uint id) => new(LeaguePacketEntityRole.Hero, id);
    private static LeaguePacketEntityReference World(uint id) => new(LeaguePacketEntityRole.World, id);
}
