using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Network.Replication;

public delegate IReadOnlyList<PlaintextPacket> EntityReplicationBuilder(
    Entity entity, ReplicationObserver observer);

public delegate IReadOnlyList<PlaintextPacket> BallReplicationBuilder(
    BallProjection ball, ReplicationObserver observer);

/// <summary>
/// Protocol-owned half of observer resync. Simulation-specific creation,
/// stats, buffs and balls are required callbacks; visibility and
/// terrain packets stay here so every adapter shares the reviewed wire rules.
/// </summary>
public sealed class ProtocolReplicationServices : IReplicationServices
{
    private readonly EntityReplicationBuilder _creation;
    private readonly EntityReplicationBuilder _stats;
    private readonly EntityReplicationBuilder _buffs;
    private readonly BallReplicationBuilder _ball;
    private readonly Func<uint, uint> _nextSyncId;
    private readonly Func<uint, uint> _nextWaypointSyncId;
    // The map's waypoint grid (VisionPackets.Compress): every compressed route on the wire (0xC2 movement data, 0x64, 0x67) needs it.
    private readonly NavigationBounds _wireBounds;
    private readonly Func<Entity, ForcedMoveState, Position2>? _firstCollisionDashLanding;

    public ProtocolReplicationServices(
        EntityReplicationBuilder creation,
        EntityReplicationBuilder stats,
        EntityReplicationBuilder buffs,
        BallReplicationBuilder ball,
        Func<uint, uint> nextSyncId,
        NavigationBounds wireBounds,
        Func<uint, uint>? nextWaypointSyncId = null,
        Func<Entity, ForcedMoveState, Position2>? firstCollisionDashLanding = null)
    {
        _creation = creation ?? throw new ArgumentNullException(nameof(creation));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _ball = ball ?? throw new ArgumentNullException(nameof(ball));
        _nextSyncId = nextSyncId ?? throw new ArgumentNullException(nameof(nextSyncId));
        _nextWaypointSyncId = nextWaypointSyncId ?? nextSyncId;
        _wireBounds = wireBounds;
        _firstCollisionDashLanding = firstCollisionDashLanding;
    }

    public IReadOnlyList<PlaintextPacket> BuildEntityCreation(Entity entity, ReplicationObserver observer) =>
        _creation(entity, observer);

    public IReadOnlyList<PlaintextPacket> BuildNeutralCampBaseline(Entity entity, ReplicationObserver observer) =>
        entity.Kind == EntityKind.Neutral
            ? _creation(entity, observer).Where(packet => packet.Plaintext.Span[0] == 0x66).ToArray()
            : Array.Empty<PlaintextPacket>();

    public IReadOnlyList<PlaintextPacket> BuildEntityStats(Entity entity, ReplicationObserver observer) =>
        VisibilityRules.IsShrine(entity)
            ? Array.Empty<PlaintextPacket>()
            : _stats(entity, observer);

    public IReadOnlyList<PlaintextPacket> BuildBuffSnapshot(Entity entity, ReplicationObserver observer) =>
        _buffs(entity, observer);

    public IReadOnlyList<PlaintextPacket> BuildBallProjection(BallProjection ball, ReplicationObserver observer) =>
        _ball(ball, observer).Concat(new[]
        {
            BallEnterVisibility(ball),
            VisionPackets.EnterLocalVisibility(ball.NetId, 0.0f, 0.0f),
        }).ToArray();

    // The ball's own route id. ProtocolSpellEffectSink.SendOriannaBall places the ball with a one-point 0xC1 (and a 0x64 teleport) under sync
    // id 1, and the client's per-unit gate (the unit's movement sync id field: an id older than the stored one is dropped)
    // then holds 1, so a re-entry stop with sync id 0 is dropped and the client keeps the ball where it last saw it. The stop carries the
    // ball's id instead: accepted on first sight (the client's movement manager starts the field at -1) and on every re-entry.
    private const uint BallRouteSyncId = 1;

    private static PlaintextPacket BallEnterVisibility(BallProjection ball)
    {
        var facing = new Position2(0.0, 1.0);
        return VisionPackets.EnterVisibilityClient(ball.NetId, "OriannaBall", 0, 0.0f, 0.0f, ball.Position, facing, isHero: false,
            movementData: VisionPackets.StopMovementData(BallRouteSyncId, ball.Position, facing));
    }

    public bool StatsBeforeReveal(Entity entity) =>
        VisibilityRules.IsShrine(entity) || entity.IsSceneTurret ||
        entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus ||
        entity.UnitType is UnitType.Structure;

    public IReadOnlyList<PlaintextPacket> BuildBallReveal(BallProjection ball, ReplicationObserver observer) =>
        new PlaintextPacket[]
        {
            // The native ball refresh is a C2 placemarker re-entry. Its
            // 0x80/0xCC template is replayed only for an observer that has
            // never known this object (BuildBallProjection).
            BallEnterVisibility(ball),
            VisionPackets.EnterLocalVisibility(ball.NetId, 0.0f, 0.0f),
        }.Concat(ball.NoRender
            ? new[] { CombatPackets.MinionActionState(ball.NetId,
                (1u << 10) | (1u << 13) | (1u << 14),
                _nextSyncId(ball.NetId)) }
            : Array.Empty<PlaintextPacket>()).ToArray();

    public IReadOnlyList<PlaintextPacket> BuildBallLeave(BallProjection ball, ReplicationObserver observer) =>
        VisionPackets.LeaveVisibility(ball.NetId);

    public IReadOnlyList<PlaintextPacket> BuildReveal(Entity entity, ReplicationObserver observer,
        IReadOnlySet<uint>? visibleNow = null, bool staleDash = false)
    {
        var maxHealth = ToFloat(entity.MaxHealth);
        // The 0xB5 writes the client's mHP too; a guardian's goes out as 0 (batch 516, ClientReplicationStore.WithoutHealth).
        var health = ToFloat(entity.Health);
        var isShrine = VisibilityRules.IsShrine(entity);
        if (isShrine)
        {
            return new PlaintextPacket[]
            {
                VisionPackets.EnterVisibilityClient(
                    entity.NetId, entity.Name, 0, maxHealth, health,
                    entity.Position, entity.Facing, isHero: false),
                VisionPackets.EnterTeamVisibility(entity.NetId, (byte)observer.Team),
                CombatPackets.TurretProtectionFlags(entity.NetId,
                    CombatPackets.SFlagNontargetableAll, _nextSyncId(entity.NetId),
                    invulnerable: 1u, targetable: 0u),
            };
        }

        var isStructure = entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus ||
                          entity.UnitType == UnitType.Structure;
        var isTurret = entity.IsSceneTurret;
        if (isStructure)
        {
            return new PlaintextPacket[]
            {
                VisionPackets.EnterLocalVisibility(entity.NetId, maxHealth, health),
                VisionPackets.EnterTeamVisibility(entity.NetId, (byte)observer.Team),
            };
        }

        var isHero = entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0;
        var packets = new List<PlaintextPacket>();
        if (entity.Kind == EntityKind.NidaleeTrap)
        {
            // Publish Bushwhack's additive modifier before native visibility
            // on first sight, fog re-entry, and reconnect.
            packets.Add(CombatPackets.MinionVisionPercentModifier(entity.NetId, -1.0f,
                _nextSyncId(entity.NetId)));
            // The view now holds the trap's -1 (ReplicationDelivery.SyncBubbleMods sends only what differs from it).
            observer.View.BubbleMods[entity.NetId] =
                (observer.View.BubbleMods.TryGetValue(entity.NetId, out var held) ? held.Flat : 0.0f, -1.0f);
        }
        var (movement, longRoute) = isTurret || entity.Kind == EntityKind.NidaleeTrap
            ? (null, null) : RevealMovement(entity, observer, visibleNow, staleDash);
        packets.Add(VisionPackets.EnterVisibilityClient(
            entity.NetId, isHero ? entity.ChampionName :
                entity.Kind == EntityKind.NidaleeTrap ? "Nidalee_Spear" : entity.Name,
            0, maxHealth, health, entity.Position, entity.Facing, isHero: isHero, movementData: movement));
        packets.Add(VisionPackets.EnterLocalVisibility(entity.NetId, maxHealth, health));
        if (longRoute is not null) packets.Add(longRoute);
        // The trap's one-second Spell1 setup is Bushwhack's own BBPlayAnimation (the script plays it once, at the cast frame, to whoever is
        // in view); a first sight, a fog re-entry or a reconnect does not play it again.
        if (isHero && entity.NetId != observer.HeroNetId)
        {
            for (var slot = 0; slot < entity.Inventory.Count; slot++)
            {
                var item = entity.Inventory[slot];
                packets.Add(item is null ? ItemsPackets.RemoveItem(entity.NetId, slot, 0) :
                    ItemsPackets.SetItem(entity.NetId, slot, checked((uint)item.ItemId), item.Count,
                        item.Charges));
            }
        }
        if (isTurret)
            packets.Add(VisionPackets.EnterTeamVisibility(entity.NetId, (byte)observer.Team));
        if (isTurret)
            packets.Add(CombatPackets.TurretProtectionFlags(
                entity.NetId,
                !entity.Alive ? CombatPackets.SFlagNontargetableAll :
                    entity.Protected ? CombatPackets.SFlagNontargetableEnemy : CombatPackets.SFlagTargetableToAll,
                syncId: _nextSyncId(entity.NetId),
                invulnerable: entity.Protected ? 1u : 0u,
                targetable: entity.Alive ? 1u : 0u));
        return packets;
    }

    // The reveal's own movement data (0xC2, read by the client's movement reader; VisionPackets.MovementData*): a dashing unit's remaining dash (type 1,
    // the 0x67 entry), a walking unit's route from where it stands (type 2, the 0x64 entry: its waypoints, or a fear walk's destination)
    // and a standing unit's stop (type 3: position and facing). The client places the unit from it, so no reveal-time 0xC1 follows: the
    // route or stop is sent once, inside the reveal (batch 270's open item for its targets 3 and 5: a lane minion's reveal route and Jack in
    // the Box's reveal). Reconstruction: the sync id; the client gates it like any route's (the unit's sync id field), and retail's value is
    // not in the client, so it is this server's route id (the match clock), which no earlier route of the unit exceeds.
    // Reconstruction: a route of more than 127 points does not fit an entry (HandleWaypoint keeps the count in 7 bits); what retail did
    // with one is unknown, so the reveal then carries the stop and the route follows as the 0xC1 this server sends for any route.
    // A client that lost sight of the unit mid-dash keeps drawing that dash (playtest B2 BJ3, 26 September 2026); the reveal's entry then
    // carries the teleport flag and a fresh teleport id (batch 291). The client reads both in the reveal: its type 1 and type 2 readers are the
    // group readers themselves (the route reader and the waypoint reader), which after
    // the sync gate and the path set snap the unit (its virtual position setter) when the id differs from the unit's last one (the stored sync id;
    // the waypoint reader does the same); only the C2S 0x7A acknowledgement of a group is missing, the wrappers' list is local. A standing
    // unit's teleport is the one-point entry the separate 0x64 used to carry, so its stop's facing is not sent. Reconstruction: whether
    // retail flagged the reveal or sent a 0x64 ahead of it is not in the client.
    private (byte[]? Movement, PlaintextPacket? LongRoute) RevealMovement(Entity entity, ReplicationObserver observer,
        IReadOnlySet<uint>? visibleNow, bool staleDash)
    {
        var syncId = _nextWaypointSyncId(entity.NetId);
        byte? teleportId = null;
        if (staleDash) teleportId = entity.TeleportId = unchecked((byte)(entity.TeleportId + 1));
        if (entity.ForcedMove is { Completed: false, Speed: > 0 } move && entity.Position != move.Destination)
        {
            if (move.DashPresentation)
            {
                var dash = DashLeg(entity, move, observer, visibleNow);
                return (VisionPackets.DashMovementData(entity.NetId, entity.Position, dash.Destination, (float)move.Speed, _wireBounds,
                    syncId, dash.FollowNetId, dash.FollowDistance, (float)move.DashGravity, dash.FollowBackDistance, teleportId), null);
            }
            return (VisionPackets.RouteMovementData(entity.NetId, [entity.Position, move.Destination], _wireBounds, syncId, teleportId), null);
        }
        var stand = teleportId is null
            ? VisionPackets.StopMovementData(syncId, entity.Position, entity.Facing)
            : VisionPackets.RouteMovementData(entity.NetId, [entity.Position], _wireBounds, syncId, teleportId)!;
        if (entity.Waypoints.Count == 0) return (stand, null);
        var route = new[] { entity.Position }.Concat(entity.Waypoints).ToArray();
        return VisionPackets.RouteMovementData(entity.NetId, route, _wireBounds, syncId, teleportId) is { } routeData
            ? (routeData, null)
            : (stand, VisionPackets.WaypointList(entity.NetId, route, syncId));
    }

    // A dash's remaining leg as the client is told it: the follow id only for a target this observer sees, the first-collision landing,
    // and for Lee Sin's Resonating Strike with its target in fog only the next 0.15 s (the target's future position is private in fog; the
    // server keeps following it).
    private (Position2 Destination, uint FollowNetId, float FollowDistance, float FollowBackDistance) DashLeg(
        Entity entity, ForcedMoveState move, ReplicationObserver observer, IReadOnlySet<uint>? visibleNow)
    {
        var followNetId = move.FollowNetId is uint targetId &&
            observer.View.Known.Contains(targetId) &&
            (visibleNow?.Contains(targetId) ?? observer.View.Visible.Contains(targetId))
            ? targetId : 0;
        var destination = move.FirstCollision && _firstCollisionDashLanding is not null
            ? _firstCollisionDashLanding(entity, move) : move.Destination;
        if (followNetId == 0 && move.Effect == "BlindMonkQTwoDash")
        {
            var dx = destination.X - entity.Position.X;
            var dz = destination.Z - entity.Position.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            var horizon = move.Speed * 0.15;
            if (distance > horizon && horizon > 0)
                destination = new Position2(entity.Position.X + dx * horizon / distance,
                    entity.Position.Z + dz * horizon / distance);
        }
        return (destination, followNetId,
            followNetId == 0 ? 0 : (float)move.DashFollowDistance,
            followNetId == 0 ? 0 : (float)move.DashFollowBackDistance);
    }

    public IReadOnlyList<PlaintextPacket> BuildWaypointReanchor(
        Entity entity, ReplicationObserver observer, IReadOnlySet<uint>? visibleNow = null)
    {
        if (entity.Kind == EntityKind.NidaleeTrap ||
            entity.UnitType is UnitType.Turret or UnitType.Structure ||
            VisibilityRules.IsShrine(entity))
            return Array.Empty<PlaintextPacket>();

        // A visible actor still in a forced move needs its remaining segment from the current position (Lee Sin's follow dash when its
        // target's visibility changes). Preserve the original native presentation: dash effects use 0x67; Fear walks use 0xC1.
        if (entity.ForcedMove is { Completed: false, Speed: > 0 } move &&
            entity.Position != move.Destination)
        {
            if (!move.DashPresentation)
                return [VisionPackets.WaypointList(entity.NetId,
                    [entity.Position, move.Destination], _nextWaypointSyncId(entity.NetId))];
            var dash = DashLeg(entity, move, observer, visibleNow);
            return [VisionPackets.Dash(entity.NetId, entity.Position, dash.Destination,
                (float)move.Speed, _wireBounds, _nextWaypointSyncId(entity.NetId),
                dash.FollowNetId, dash.FollowDistance, (float)move.DashGravity, dash.FollowBackDistance)];
        }

        // The current position as the reanchor, followed by the live route.
        return new[]
        {
            VisionPackets.WaypointList(
                entity.NetId, new[] { entity.Position }.Concat(entity.Waypoints).ToArray(),
                _nextWaypointSyncId(entity.NetId)),
        };
    }

    public IReadOnlyList<PlaintextPacket> BuildTerrainBootstrap(ReplicationObserver observer)
    {
        // The dark Order fountain is accepted patch-126 client behavior.
        // Synthetic lighting/texture-edge bubbles create stray fog reveals;
        // the client's own objects draw every unit's and structure's sight.
        return Array.Empty<PlaintextPacket>();
    }

    public IReadOnlyList<PlaintextPacket> BuildTerrainAttach(
        Entity entity, uint bubbleId, float radius, ReplicationObserver observer)
    {
        // Only a script unit's VisibilitySize bubble (ReplicationDelivery.SyncTerrain): every unit's own bubble is made by its client object.
        // DIAGNOSTICS (batch 476): the OdinRelicBubbleGate withholds a centre crystal's 0x26 here too.
        if (Amberline.Network.Diagnostics.OdinRelicBubbleGate.Withholds(entity.ChampionName)) return Array.Empty<PlaintextPacket>();
        return new[]
        {
            SpawnPackets.AddUnitPerceptionBubble(
                entity.NetId, radius, 1.0e9f, bubbleId, (uint)observer.Team),
        };
    }

    public IReadOnlyList<PlaintextPacket> BuildTerrainRemove(
        uint bubbleId, ReplicationObserver observer) =>
        new[] { SpawnPackets.RemovePerceptionBubble(bubbleId) };

    public IReadOnlyList<PlaintextPacket> BuildLeave(Entity entity, ReplicationObserver observer)
    {
        if (entity.IsSceneTurret || entity.UnitType == UnitType.Structure ||
            entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus)
            return new PlaintextPacket[]
            {
                VisionPackets.LeaveLocalVisibility(entity.NetId),
                VisionPackets.LeaveTeamVisibility(entity.NetId, (byte)observer.Team),
            };
        return VisionPackets.LeaveVisibility(entity.NetId);
    }

    private static float ToFloat(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (float)value;
    }
}
