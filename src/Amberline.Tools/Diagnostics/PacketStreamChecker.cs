using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Amberline.Core;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;

namespace Amberline.Tools.Diagnostics;

/// <summary>One plaintext gameplay packet as a peer receives it, after replication and visibility filtering.</summary>
public sealed record PacketObservation(double T, int Peer, byte Channel, bool Reliable, ReadOnlyMemory<byte> Plaintext);

/// <summary>One broken protocol rule. <see cref="Peer"/> is -1 for the end-of-match "not listed" summary rows.</summary>
public sealed record PacketRuleViolation(string Kind, double T, int Peer, byte Opcode, uint? NetId, string Detail);

/// <summary>The violation kinds <see cref="PacketStreamChecker"/> reports (also the session.jsonl <c>msg</c> values).</summary>
public static class PacketRuleKinds
{
    public const string SyncIdRegression = "sync-id-regression";
    public const string ReplicatedStatZeroed = "replicated-stat-zeroed";
    public const string UnknownUnit = "unknown-unit";
    public const string ChannelOrReliability = "channel-or-reliability";
    public const string DuplicateCreation = "duplicate-creation";
    public const string MalformedReplication = "malformed-replication";
    public const string OrderDuringDash = "order-during-dash";
    public const string UnboundTurret = "unbound-turret";
}

/// <summary>Dash bookkeeping of <see cref="PacketStreamChecker.Dashes"/>.</summary>
public readonly record struct DashStatistics(long Dashes, long StartAnchors, long LandingAnchors, long Interrupted);

/// <summary>
/// Hero routes of <see cref="PacketStreamChecker.HeroRoutes"/>, summed over every receiving peer: the 0xC1 packets and the routes and stops
/// inside reveals (<see cref="InReveals"/> of them).
/// </summary>
public readonly record struct HeroRouteStatistics(long Routes, long Stationary, long InReveals);

/// <summary>The movement data of the 0xC2 reveals (<see cref="PacketStreamChecker.RevealMovement"/>), per receiving peer.</summary>
public readonly record struct RevealMovementStatistics(long Routes, long Stops, long Dashes, long Teleports);

/// <summary>
/// DIAGNOSTICS: an emulator-only diagnostic, no retail counterpart. Replays the packets each peer
/// receives through a model of that client's view and reports violations of the rules in
/// docs/testing.md ("Packet-stream rules"). Streaming: O(1) amortized per packet, no per-packet
/// allocation unless a violation is reported, state bounded by the net-ID space per peer and by
/// <see cref="MaxListedPerKind"/> retained violations per kind.
/// </summary>
public sealed partial class PacketStreamChecker
{
    /// <summary>Violations listed per kind; further ones are only counted (see <see cref="TotalsByKind"/>).</summary>
    public const int MaxListedPerKind = 200;

    // Two packets closer than this share one server step (a step's burst has one clock value, and
    // production steps are 33 ms), so "later" never splits a burst.
    private const double SameStepSeconds = 0.005;

    // The client's waypoint grid is 2 u, so a landing anchor within a grid diagonal of the dash's end is on it.
    private const double LandingToleranceSquared = 3.0 * 3.0;

    private readonly PacketCatalog _catalog;
    private readonly Peer[] _peers;
    private readonly List<PacketRuleViolation> _listed = [];
    private readonly Dictionary<string, int> _listedByKind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly HashSet<(string Kind, int Peer, byte Opcode, uint NetId, int Variant)> _reported = [];
    private double _lastT;
    private bool _finished;
    private long _dashes, _dashStartAnchors, _dashLandingAnchors, _heroRoutes, _heroStationary, _heroRevealRoutes;
    private long _revealRoutes, _revealStops, _revealDashes, _revealTeleports;

    public PacketStreamChecker(int peers, PacketCatalog? catalog = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(peers, 1);
        _catalog = catalog ?? PacketCatalog.Default;
        _peers = new Peer[peers];
        for (var index = 0; index < peers; index++) _peers[index] = new Peer();
    }

    /// <summary>
    /// The CreateTurret names the client can bind: one <c>&lt;object&gt;_A</c> per <c>Turret_*.SCB</c> of the map's scene (<see cref="SceneTurretNames"/>).
    /// The stock client makes no turret of a 0xA5: it binds the net id to the scene turret of that name (a
    /// case-insensitive compare), and any other name asserts the turret check and binds nothing. Null checks no name.
    /// </summary>
    public IReadOnlySet<string>? SceneTurrets { get; init; }

    /// <summary>The CreateTurret names of a map's scene (batch 457): every <c>Turret_*.SCB</c> under <c>LEVELS/&lt;map&gt;/Scene</c>, with <c>_A</c>.</summary>
    public static IReadOnlySet<string> SceneTurretNames(Amberline.Data.GameDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var scene = Path.Combine(paths.Root, "LEVELS", paths.MapName, "Scene");
        return Directory.EnumerateFiles(scene, "Turret_*.SCB")
            .Select(file => Path.GetFileNameWithoutExtension(file) + "_A")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Packets observed so far.</summary>
    public long PacketsObserved { get; private set; }

    /// <summary>
    /// 0xCC blocks the client drops as stale (lower syncID) whose fields it already holds: harmless
    /// redundancy, not a violation (see rule 1 in <c>ObserveReplicationEntry</c>).
    /// </summary>
    public long StaleReplicationDropped { get; private set; }

    /// <summary>Every violation seen, including those not listed individually, by kind.</summary>
    public IReadOnlyDictionary<string, long> TotalsByKind => _totals;

    /// <summary>
    /// What the peers were told around the 0x67 dashes of lane minions, pets and monsters (not violations): the dashes,
    /// the stationary 0xC1 queued for the unit just before one (the stun's anchor) and the stationary 0xC1 that lands on
    /// the dash's end point after it. Counted per receiving peer.
    /// </summary>
    public DashStatistics Dashes => new(_dashes, _dashStartAnchors, _dashLandingAnchors,
        _totals.GetValueOrDefault(PacketRuleKinds.OrderDuringDash));

    /// <summary>
    /// The routes sent for heroes, counted per receiving peer: 0xC1 packets and a reveal's route or stop (a single point or a stop is a
    /// stationary anchor).
    /// </summary>
    public HeroRouteStatistics HeroRoutes => new(_heroRoutes, _heroStationary, _heroRevealRoutes);

    /// <summary>The movement data the reveals carried (route, stop, dash; teleport-flagged entries among them), per receiving peer.</summary>
    public RevealMovementStatistics RevealMovement => new(_revealRoutes, _revealStops, _revealDashes, _revealTeleports);
    private readonly Dictionary<uint, long> _routesByNetId = new();

    /// <summary>The routes sent per unit net id (heroes included; 0xC1 and a reveal's route or stop), counted per receiving peer like
    /// <see cref="HeroRoutes"/>.</summary>
    public IReadOnlyDictionary<uint, long> RoutesByNetId => _routesByNetId;

    /// <summary>Feeds one packet. Call in send order, per match; peers are independent.</summary>
    public void Observe(PacketObservation packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished) throw new InvalidOperationException("Finish() was already called.");
        if ((uint)packet.Peer >= (uint)_peers.Length)
            throw new ArgumentOutOfRangeException(nameof(packet), $"peer {packet.Peer} of {_peers.Length}");
        PacketsObserved++;
        _lastT = packet.T;
        var peer = _peers[packet.Peer];
        var payload = packet.Plaintext.Span;

        // Rule 4. Chat is a bare envelope (ChatPackets.Envelope), so it has no opcode to look up.
        if (PacketCatalog.IsChatChannel(packet.Channel))
        {
            if (packet.Reliable != PacketCatalog.ChatReliable &&
                Admit(PacketRuleKinds.ChannelOrReliability, packet.Peer, 0, 0, 0))
                List(PacketRuleKinds.ChannelOrReliability, packet, 0, null,
                    "chat envelope sent unreliably; the catalog sends chat reliably");
            return;
        }
        if (payload.IsEmpty) return;
        var opcode = payload[0];
        var found = _catalog.TryGet(opcode, packet.Channel, out var spec);
        var variant = packet.Channel * 2 + (packet.Reliable ? 1 : 0);
        if (!found)
        {
            if (Admit(PacketRuleKinds.ChannelOrReliability, packet.Peer, opcode, 0, variant))
                List(PacketRuleKinds.ChannelOrReliability, packet, opcode, null,
                    _catalog.KnowsOpcode(opcode)
                        ? $"0x{opcode:X2} sent on channel {packet.Channel}; the catalog sends it on channel {_catalog.ChannelsOf(opcode)}"
                        : $"0x{opcode:X2} on channel {packet.Channel} is not in the packet catalog");
        }
        else if (spec.Reliable != packet.Reliable &&
                 Admit(PacketRuleKinds.ChannelOrReliability, packet.Peer, opcode, 0, variant))
            List(PacketRuleKinds.ChannelOrReliability, packet, opcode, null,
                $"0x{opcode:X2} ({spec.Name}) sent {Delivery(packet.Reliable)}; the catalog sends it {Delivery(spec.Reliable)}");

        if (opcode == 0xCC)
        {
            ObserveReplication(peer, packet, payload);
            return;
        }
        ObserveBook(peer, packet, opcode, payload);
        if (packet.Channel == LeagueProtocolConstants.LowPriorityChannel && opcode is VisionPackets.DashOpcode or VisionPackets.WaypointListOpcode)
        {
            if (opcode == VisionPackets.WaypointListOpcode && payload.Length >= 9 &&
                NetIds.IsHero(BinaryPrimitives.ReadUInt32LittleEndian(payload[1..])))
            {
                _heroRoutes++;
                if (payload.Length == 17) _heroStationary++;
            }
            if (opcode == VisionPackets.WaypointListOpcode && payload.Length >= 9)
            {
                var routed = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                _routesByNetId[routed] = _routesByNetId.GetValueOrDefault(routed) + 1;
            }
            ObserveMovement(peer, packet, payload);
        }
        else if (opcode == VisionPackets.EnterVisibilityOpcode) ObserveRevealMovement(peer, packet);
        if (!found) return;

        Span<uint> ids = stackalloc uint[512];
        var count = spec.ReadReferences(payload, ids);
        for (var index = 0; index < count; index++) Reference(peer, packet, opcode, spec, ids[index]);
        count = spec.ReadCreations(payload, ids);
        for (var index = 0; index < count; index++) Create(peer, packet, opcode, ids[index]);
    }

    /// <summary>Ends the match and returns the listed violations in the order they were found.</summary>
    public IReadOnlyList<PacketRuleViolation> Finish()
    {
        if (!_finished)
        {
            _finished = true;
            foreach (var kind in _totals.Keys.Order(StringComparer.Ordinal))
            {
                var unlisted = _totals[kind] - _listedByKind.GetValueOrDefault(kind);
                if (unlisted > 0)
                    _listed.Add(new PacketRuleViolation(kind, _lastT, -1, 0, null,
                        $"{unlisted} further {kind} violations are not listed: repeats of an already listed " +
                        $"peer/opcode/unit, or beyond the first {MaxListedPerKind}"));
            }
        }
        return _listed;
    }

    // ---- rule 3: units the peer was never told about, and creation bookkeeping ----

    private void Reference(Peer peer, PacketObservation packet, byte opcode, PacketSpec spec, uint netId)
    {
        if (!NetIds.IsCharacter(netId)) return;
        if (!peer.Units.TryGetValue(netId, out var unit) || !unit.Created)
        {
            if (Admit(PacketRuleKinds.UnknownUnit, packet.Peer, opcode, netId, 0))
                List(PacketRuleKinds.UnknownUnit, packet, opcode, netId,
                    $"0x{opcode:X2} ({spec.Name}) refers to {NetIds.Describe(netId)} that this peer was never told about " +
                    "(no creation packet received)");
            return;
        }
        // A shown unit whose stat block was never replicated is zeroed by the next 0xCC flush.
        if (opcode is 0xC2 or 0xB5 && !unit.Replicated && !unit.RevealQueued)
        {
            unit.RevealQueued = true;
            peer.Revealed.Enqueue((netId, packet.T));
        }
    }

    private void Create(Peer peer, PacketObservation packet, byte opcode, uint netId)
    {
        if (!peer.Units.TryGetValue(netId, out var unit)) peer.Units[netId] = unit = new Unit();
        else if (unit.Created && NetIds.IsCharacter(netId))
            // Heroes, turrets, minions and monsters are created once per peer; the allocator never reuses
            // their IDs. Missile-range IDs (temporary casters, balls) wrap by design, so they are exempt.
            if (Admit(PacketRuleKinds.DuplicateCreation, packet.Peer, opcode, netId, 0))
                List(PacketRuleKinds.DuplicateCreation, packet, opcode, netId,
                    $"0x{opcode:X2} creates {NetIds.Describe(netId)} that this peer already has");
        if (opcode == 0xA5 && SceneTurrets is not null)
        {
            // CreateTurret: the name is the NUL-terminated string at offset 10 (SpawnPackets.CreateTurret).
            var body = packet.Plaintext.Span;
            var name = body.Length > 10 ? System.Text.Encoding.ASCII.GetString(body[10..]).Split('\0')[0] : string.Empty;
            if (!SceneTurrets.Contains(name))
            {
                if (Admit(PacketRuleKinds.UnboundTurret, packet.Peer, opcode, netId, 0))
                    List(PacketRuleKinds.UnboundTurret, packet, opcode, netId,
                        $"0xA5 names {name}, no turret of the map's scene: the client asserts `turret` and binds nothing");
                return;
            }
        }
        unit.Created = true;
        unit.Class = opcode switch { 0x4F => UnitClass.Hero, 0xA5 => UnitClass.Turret, _ => UnitClass.Minion };
        // A new native object starts with stored syncIDs of zero and an unreplicated stat block.
        unit.Replicated = false;
        unit.RevealQueued = false;
        unit.ResetSync();
    }

    // ---- rule 7: a route for a unit whose dash is still running ----

    // The stock client applies a 0xC1 to a lane minion, pet or monster by setting the unit to the packet's first waypoint,
    // clearing the dash's override speed and replacing its path (the client's path-follow routine). A dash
    // (Rocket Grab, Condemn, Monsoon, a shockwave, Power Fist) that a 0xC1 interrupts therefore stops where the 0xC1 puts the unit,
    // and the next packet snaps it the rest of the way (365-400 u in the real-client captures of 17 October 2026). The dash
    // takes length / speed seconds at the earliest, and the server completes a move no sooner, so a 0xC1 sent inside that time
    // is an order the server's AI gave a displaced unit. Heroes are exempt: their 0xC1 answer client orders.
    private void ObserveMovement(Peer peer, PacketObservation packet, ReadOnlySpan<byte> bytes)
    {
        var dash = bytes[0] == VisionPackets.DashOpcode;
        var idAt = dash ? 15 : 1;
        if (bytes.Length < idAt + 4) return;
        var netId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[idAt..]);
        if (!NetIds.IsNpc(netId)) return;
        var unit = NpcUnit(peer, netId);
        if (dash)
        {
            NpcDash(unit, packet.T, WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel).Fields);
            return;
        }
        if (packet.T + 1e-9 < unit.DashUntil && Admit(PacketRuleKinds.OrderDuringDash, packet.Peer, 0xC1, netId, 0))
            List(PacketRuleKinds.OrderDuringDash, packet, 0xC1, netId,
                $"0xC1 for {NetIds.Describe(netId)} {Fmt(unit.DashUntil - packet.T)} s before its dash ends; the client's " +
                "dash is replaced by this route");
        // [0xC1][netId][syncId] then one 8-byte point per waypoint: a single point is a stationary anchor.
        NpcPlacement(unit, packet.T, bytes.Length == 17
            ? (BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[13..])) : null);
    }

    // DIAGNOSTICS: (batch 291) since batch 276 a reveal's 0xC2 carries the unit's movement (the client's movement reader: type 1 a dash, type 2 a route,
    // type 3 a stop) where a 0xC1 or 0x67 followed the reveal before. A route or a stop counts as the 0xC1 it replaced (a stop or a one-point
    // route is stationary), a dash as a 0x67. A reveal is no AI order, so it never breaks "order-during-dash"; it replaces the client's dash.
    private void ObserveRevealMovement(Peer peer, PacketObservation packet)
    {
        var fields = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel).Fields;
        if (fields.GetValueOrDefault("net_id") is not uint netId || fields.GetValueOrDefault("movement_type") is not byte type ||
            fields.GetValueOrDefault("waypoints") is not object?[] { Length: > 0 } points) return;
        if (fields.ContainsKey("teleport_id")) _revealTeleports++;
        if (type == VisionPackets.MovementDataRouteWithSpeed)
        {
            _revealDashes++;
            if (NetIds.IsNpc(netId)) NpcDash(NpcUnit(peer, netId), packet.T, fields);
            return;
        }
        if (type == VisionPackets.MovementDataStop) _revealStops++;
        else _revealRoutes++;
        var stationary = points.Length == 1;
        if (NetIds.IsHero(netId))
        {
            _heroRoutes++;
            _heroRevealRoutes++;
            if (stationary) _heroStationary++;
        }
        _routesByNetId[netId] = _routesByNetId.GetValueOrDefault(netId) + 1;
        if (!NetIds.IsNpc(netId)) return;
        var unit = NpcUnit(peer, netId);
        unit.DashUntil = 0.0;
        NpcPlacement(unit, packet.T, !stationary ? null : points[0] switch
        {
            float[] grid => (grid[0], grid[1]), // a route entry's point (2-unit grid)
            double[] stop => (stop[0], stop[1]), // a stop's position
            _ => null,
        });
    }

    private static Unit NpcUnit(Peer peer, uint netId)
    {
        if (!peer.Units.TryGetValue(netId, out var unit)) peer.Units[netId] = unit = new Unit();
        return unit;
    }

    private void NpcDash(Unit unit, double t, IReadOnlyDictionary<string, object?> fields)
    {
        _dashes++;
        if (t - unit.StationaryAt <= SameStepSeconds) _dashStartAnchors++;
        var (seconds, end) = DashPath(fields);
        unit.DashUntil = t + seconds;
        unit.DashEnd = end;
        unit.DashLanding = end is not null;
    }

    // A route or anchor: a stationary one is a start anchor for a dash in the same step, and a landing anchor on the end of the dash before it.
    private void NpcPlacement(Unit unit, double t, (double X, double Z)? stationaryAt)
    {
        if (stationaryAt is { } at)
        {
            unit.StationaryAt = t;
            if (unit.DashLanding && unit.DashEnd is { } end)
            {
                var dx = at.X - end.X;
                var dz = at.Z - end.Z;
                if (dx * dx + dz * dz <= LandingToleranceSquared) _dashLandingAnchors++;
            }
        }
        unit.DashLanding = false;
    }

    // A dash's travel time (seconds) and its last waypoint, from the decoded packet.
    private static (double Seconds, (double X, double Z)? End) DashPath(IReadOnlyDictionary<string, object?> fields)
    {
        if (!fields.TryGetValue("speed", out var speed) || speed is not double metres || metres <= 0.0 ||
            !fields.TryGetValue("waypoints", out var points) || points is not object?[] path) return (0.0, null);
        var length = 0.0;
        for (var index = 1; index < path.Length; index++)
            if (path[index - 1] is float[] from && path[index] is float[] to)
                length += Math.Sqrt(Math.Pow(to[0] - from[0], 2) + Math.Pow(to[1] - from[1], 2));
        return (length / metres, path.Length > 0 && path[^1] is float[] last ? (last[0], last[1]) : null);
    }

    // ---- rules 1 and 2: the 0xCC replication packet ----

    private void ObserveReplication(Peer peer, PacketObservation packet, ReadOnlySpan<byte> bytes)
    {
        // [0xCC][u32 header][u32 syncID][u8 entryCount], then per entry [u8 setMaps][u32 unit netid] and, per
        // set bit of setMaps (ascending), [u32 propMask][4 bytes per set bit]. Retail: the client's case 0xCC;
        // the maintained encoders are CombatPackets.WriteReplicationPrefix and its callers.
        if (bytes.Length < 10)
        {
            Malformed(packet, "shorter than the 10-byte replication header");
            return;
        }
        var syncId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
        int entries = bytes[9], offset = 10;
        Span<uint> masks = stackalloc uint[5];
        Span<int> values = stackalloc int[5];
        for (var entry = 0; entry < entries; entry++)
        {
            if (offset + 5 > bytes.Length) { Malformed(packet, $"entry {entry} is cut off"); return; }
            var setMaps = bytes[offset];
            var netId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 1)..]);
            offset += 5;
            if ((setMaps & 0xE0) != 0) { Malformed(packet, $"entry {entry} sets replication channels 0x{setMaps:X2}"); return; }
            for (var map = 0; map < 5; map++)
            {
                masks[map] = 0;
                if ((setMaps & (1 << map)) == 0) continue;
                if (offset + 4 > bytes.Length) { Malformed(packet, $"entry {entry} propMask is cut off"); return; }
                masks[map] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                values[map] = offset + 4;
                offset += 4 + 4 * BitOperations.PopCount(masks[map]);
                if (offset > bytes.Length) { Malformed(packet, $"entry {entry} values are cut off"); return; }
            }
            ObserveReplicationEntry(peer, packet, bytes, syncId, setMaps, netId, masks, values);
        }
        if (bytes[offset..].ContainsAnyExcept((byte)0) || bytes.Length - offset > 7)
        {
            Malformed(packet, "bytes after the last entry");
            return;
        }

        // Retail flushes every character's replicated stats on ANY 0xCC. A unit shown to the peer earlier
        // with no stat block replicated has just been zeroed; the rule is in docs/testing.md, rule 2.
        while (peer.Revealed.TryPeek(out var head) && head.At + SameStepSeconds < packet.T)
        {
            peer.Revealed.Dequeue();
            if (peer.Units.TryGetValue(head.NetId, out var unit) && !unit.Replicated)
            {
                unit.RevealQueued = false;
                if (Admit(PacketRuleKinds.ReplicatedStatZeroed, packet.Peer, 0xCC, head.NetId, 1))
                    List(PacketRuleKinds.ReplicatedStatZeroed, packet, 0xCC, head.NetId,
                        $"{NetIds.Describe(head.NetId)} was shown to this peer at t={Fmt(head.At)} but its stat block was never " +
                        "replicated (no mMoveSpeed write since creation); this 0xCC flushes all characters' replicated " +
                        "stats and zeroes it");
            }
        }
    }

    private void ObserveReplicationEntry(Peer peer, PacketObservation packet, ReadOnlySpan<byte> bytes, uint syncId,
        byte setMaps, uint netId, Span<uint> masks, Span<int> values)
    {
        if (!peer.Units.TryGetValue(netId, out var unit)) peer.Units[netId] = unit = new Unit();
        if (NetIds.IsCharacter(netId) && !unit.Created &&
            Admit(PacketRuleKinds.UnknownUnit, packet.Peer, 0xCC, netId, 0))
            List(PacketRuleKinds.UnknownUnit, packet, 0xCC, netId,
                $"0xCC replicates {NetIds.Describe(netId)} that this peer was never told about (no creation packet " +
                "received); the stock client applies an unresolved entry to a stale object");

        // Rule 1. The stock client applies a channel's block only if stored syncID < incoming, per unit and
        // per replication channel, starting at 0 (the replication manager's receive handler).
        // The server numbers 0xCC packets from one counter per unit, in build order, so a LOWER syncID is a
        // stale packet that a newer one already superseded. That is harmless when every field it carries was
        // already applied (production queues such stale duplicates; they are only counted). It is a lost update
        // when it carries a field the client never received, and a syncID that is zero or equal to the stored
        // one is never newer than what the client holds, so it can drop a genuine update.
        var dropped = 0;
        var lost = 0;
        uint latest = 0;
        for (var map = 0; map < 5; map++)
        {
            if ((setMaps & (1 << map)) == 0) continue;
            ref var stored = ref unit.Sync(map);
            ref var applied = ref unit.Applied(map);
            if (syncId > stored)
            {
                stored = syncId;
                applied |= masks[map];
                continue;
            }
            dropped |= 1 << map;
            latest = Math.Max(latest, stored);
            if (syncId == 0 || syncId == stored || (masks[map] & ~applied) != 0) lost |= 1 << map;
        }
        if (lost != 0)
        {
            if (Admit(PacketRuleKinds.SyncIdRegression, packet.Peer, 0xCC, netId, 0))
                List(PacketRuleKinds.SyncIdRegression, packet, 0xCC, netId,
                    $"syncID {syncId} is not greater than {latest}, the last one this peer received on " +
                    $"replication channel(s) {ChannelNames(lost)}; the client drops that block " +
                    (syncId == 0 || syncId == latest ? "(zero or equal syncIDs are never applied)" :
                        "and it carries fields the client never received"));
        }
        else if (dropped != 0) StaleReplicationDropped++;

        var kind = unit.Class != UnitClass.Unknown ? unit.Class : NetIds.ClassOf(netId);
        if (kind is UnitClass.Unknown or UnitClass.Building) return;
        if (kind == UnitClass.Hero && (setMaps & 1) != 0 && (dropped & 1) == 0) ObserveCastBits(unit, bytes, masks[0], values[0]);

        // Rule 2: a stat that must never be zero is written as zero.
        foreach (var field in Fields(kind))
        {
            if ((dropped & (1 << field.Map)) != 0 || (masks[field.Map] & (1u << field.Bit)) == 0) continue;
            var at = values[field.Map] + 4 * BitOperations.PopCount(masks[field.Map] & ((1u << field.Bit) - 1));
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            if (field.Stat == Stat.MoveSpeed) unit.Replicated = true;
            if ((bits & 0x7FFFFFFFu) == 0 && field.ZeroIsBug &&
                Admit(PacketRuleKinds.ReplicatedStatZeroed, packet.Peer, 0xCC, netId, 0))
                List(PacketRuleKinds.ReplicatedStatZeroed, packet, 0xCC, netId,
                    $"{kind} {field.Stat} is written as 0 for {NetIds.Describe(netId)}");
        }
        // NOTES.md "Base visual fixes": a shrine's reveal carries only the protection flags (LOCAL1 bits 12-14); its full stat block
        // comes in the map-ready burst (F3 audit, 18 October 2026), so a flags-only tower is not a missing replication.
        if (kind == UnitClass.Turret && setMaps == 0x02 && masks[1] != 0 && (masks[1] & ~0x7000u) == 0)
            unit.Replicated = true;
    }

    // Replication field positions (map index: 0 client-only, 1 LOCAL1, 3 MAP) from the retail registration
    // order: hero MAP 2 mMaxHP / 6 mMoveSpeed, LOCAL1 14 mAttackRange / 20 mAttackSpeedMod; minion LOCAL1
    // 2 mMaxHP / 13 mAttackSpeedMod, MAP 2 mMoveSpeed; turret LOCAL1 9 mMaxHP / 3 mAttackSpeedMod, MAP 2
    // mMoveSpeed. Minion and turret speed is zero by design for wards, traps, TestCube and towers. A hero's mAttackRange of 0 is a value the
    // shipped scripts ask for (VladimirSanguinePool's BuffOnUpdateStats: IncFlatAttackRangeMod -450 on his 450 base), and the client stores the
    // replicated value as sent, with no clamp (the client's intermediate representation update; nothing else in the
    // client reads mFlatAttackRangeMod), so only its flush to 0 (the never-replicated rule above) is a fault.
    private static readonly Field[] HeroFields =
    [
        new(3, 2, Stat.MaxHp, true), new(3, 6, Stat.MoveSpeed, true),
        new(1, 20, Stat.AttackSpeedMod, true), new(1, 14, Stat.AttackRange, false),
    ];
    private static readonly Field[] MinionFields =
    [
        new(1, 2, Stat.MaxHp, true), new(3, 2, Stat.MoveSpeed, false), new(1, 13, Stat.AttackSpeedMod, true),
    ];
    private static readonly Field[] TurretFields =
    [
        new(1, 9, Stat.MaxHp, true), new(3, 2, Stat.MoveSpeed, false), new(1, 3, Stat.AttackSpeedMod, true),
    ];

    private static Field[] Fields(UnitClass kind) => kind switch
    {
        UnitClass.Hero => HeroFields,
        UnitClass.Turret => TurretFields,
        _ => MinionFields,
    };

    private readonly record struct Field(int Map, int Bit, Stat Stat, bool ZeroIsBug);

    private enum Stat { MaxHp, MoveSpeed, AttackSpeedMod, AttackRange }

    private static string ChannelNames(int maps)
    {
        string[] names = ["client-only", "local1", "local2", "map", "onvisible"];
        return string.Join("+", Enumerable.Range(0, 5).Where(map => (maps & (1 << map)) != 0).Select(map => names[map]));
    }

    // ---- reporting ----

    private void Malformed(PacketObservation packet, string why)
    {
        if (Admit(PacketRuleKinds.MalformedReplication, packet.Peer, 0xCC, 0, 0))
            List(PacketRuleKinds.MalformedReplication, packet, 0xCC, null, "0xCC cannot be parsed: " + why);
    }

    // Counts the violation and says whether it should be listed. Called before the detail text is built, so a
    // repeating violation (same kind, peer, opcode, unit) costs no string once it has been listed.
    private bool Admit(string kind, int peer, byte opcode, uint netId, int variant)
    {
        _totals[kind] = _totals.GetValueOrDefault(kind) + 1;
        return _listedByKind.GetValueOrDefault(kind) < MaxListedPerKind &&
               _reported.Add((kind, peer, opcode, netId, variant));
    }

    private void List(string kind, PacketObservation packet, byte opcode, uint? netId, string detail)
    {
        _listedByKind[kind] = _listedByKind.GetValueOrDefault(kind) + 1;
        _listed.Add(new PacketRuleViolation(kind, packet.T, packet.Peer, opcode, netId, detail));
    }

    private static string Delivery(bool reliable) => reliable ? "reliably" : "unreliably";

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- per-peer model ----

    private enum UnitClass : byte { Unknown, Hero, Minion, Turret, Building }

    private sealed class Peer
    {
        public readonly Dictionary<uint, Unit> Units = [];
        public readonly Queue<(uint NetId, double At)> Revealed = new();
    }

    private sealed class Unit
    {
        // Per replication channel: the last applied syncID and the fields applied so far.
        private readonly uint[] _sync = new uint[5];
        private readonly uint[] _applied = new uint[5];
        public UnitClass Class;
        public bool Created, Replicated, RevealQueued;
        // A hero's spellbook as the client holds it (PacketStreamChecker.Reconnect.cs); null until a packet writes it.
        public Book? Book;
        // Seconds (the packet clock) before which the dash this peer last received for the unit is still running, its last
        // waypoint, whether a landing anchor for it could still follow (no other 0xC1 has), and when the unit last got a
        // stationary 0xC1.
        public double DashUntil, StationaryAt = double.NegativeInfinity;
        public (double X, double Z)? DashEnd;
        public bool DashLanding;

        public ref uint Sync(int map) => ref _sync[map];

        public ref uint Applied(int map) => ref _applied[map];

        public void ResetSync()
        {
            Array.Clear(_sync);
            Array.Clear(_applied);
        }
    }

    /// <summary>Net-ID classes from <see cref="NetIdAllocator"/>, the single source of the server's ID ranges.</summary>
    private static class NetIds
    {
        private static readonly NetIdRange Hero = NetIdAllocator.RangeFor(UnitType.Hero);
        private static readonly NetIdRange Turret = NetIdAllocator.RangeFor(UnitType.Turret);
        private static readonly NetIdRange Minion = NetIdAllocator.RangeFor(UnitType.Minion);
        private static readonly NetIdRange Monster = NetIdAllocator.RangeFor(UnitType.Monster);

        private static bool In(NetIdRange range, uint id) => id >= range.Base && id < range.Ceiling;

        /// <summary>Heroes, turrets, minions and monsters: created by a packet, never reused. Everything else
        /// (missile-range IDs that wrap, map buildings the client derives itself, bubble and client IDs) is not tracked.</summary>
        public static bool IsCharacter(uint id) => In(Hero, id) || In(Turret, id) || In(Minion, id) || In(Monster, id);

        public static bool IsHero(uint id) => In(Hero, id);

        /// <summary>Lane minions, pets and monsters: the units the server's AI moves.</summary>
        public static bool IsNpc(uint id) => In(Minion, id) || In(Monster, id);

        public static UnitClass ClassOf(uint id) =>
            In(Hero, id) ? UnitClass.Hero :
            In(Turret, id) ? UnitClass.Turret :
            In(Minion, id) || In(Monster, id) || In(NetIdAllocator.RangeFor(UnitType.Missile), id) ? UnitClass.Minion :
            id >> 24 == 0xFF || In(NetIdAllocator.RangeFor(UnitType.Structure), id) ? UnitClass.Building :
            UnitClass.Unknown;

        public static string Describe(uint id) =>
            $"{(In(Hero, id) ? "hero" : In(Turret, id) ? "turret" : In(Minion, id) ? "minion" : "monster")} 0x{id:X8}";
    }
}
