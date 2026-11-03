using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;

namespace Amberline.Simulation.Minions;

/// <summary>
/// Combat decisions required by minion AI. The owner of damage, targetability,
/// and projectile hit state supplies this service; minion AI never substitutes
/// a silent local answer for a missing combat implementation.
/// </summary>
public interface IMinionCombatService
{
    bool IsTargetable(Entity target, Entity observer);
    bool IsInAttackRange(Entity attacker, Entity target);
    bool HasCommittedWindup(Entity attacker, Entity target);
    bool LastAutoAttackFinished(Entity attacker);
    void InterruptWindup(Entity attacker);
}

/// <summary>
/// Wire boundary for minion state. Implementations are responsible for the
/// packet builders and observer filtering. A waypoint/stop/reference may only
/// be sent after <see cref="IsReferenceReady"/> is true, which preserves the
/// Create-before-reference rule for fogged enemy waves.
/// </summary>
public interface IMinionPacketSink
{
    /// <summary>Reconstruction (batch 211 follow-up): sends the orders held for forced moves whose wire end has passed.</summary>
    void FlushHeldOrders() { }
    bool IsReferenceReady(Entity minion);
    bool TryGetClientPosition(Entity minion, out Position2 position);
    void SendCreate(Entity minion);
    void SendVisibility(Entity minion);
    void SendStats(Entity minion);
    void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason);
    void SendStop(Entity minion, long syncId, string reason);

    /// <summary>
    /// Ends the minion's attack on its clients (NpcInstantStopAttack) when they are in one. Whether, and with which flags,
    /// is <c>CombatPackets.StopAttack</c>'s decision: a swing the server dropped is not played, a hit it still applies keeps its shot.
    /// </summary>
    void SendStopAttack(Entity minion, string reason);
}

/// <summary>
/// Vision sources used by minion aggro and retention. Item-derived providers
/// are supplied by the owning replication/item service; an implicit empty
/// provider would silently drop detection rules.
/// </summary>
public interface IMinionVisionService
{
    bool HasBrushVision(Team team, Entity target);
}

public sealed class WorldMinionVisionService : IMinionVisionService
{
    private readonly WorldState _world;
    private readonly NavigationGrid _navigation;
    private readonly Func<Team, IEnumerable<VisionProvider>> _itemProviders;

    public WorldMinionVisionService(
        WorldState world,
        NavigationGrid navigation,
        Func<Team, IEnumerable<VisionProvider>> itemProviders)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _itemProviders = itemProviders ?? throw new ArgumentNullException(nameof(itemProviders));
    }

    public bool HasBrushVision(Team team, Entity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_navigation.BrushIdAt(target.Position, target.SelectionRadius) is null) return true;
        var providers = new List<VisionProvider>();
        foreach (var entity in _world.Entities.Values)
        {
            if (!entity.Alive) continue;
            // Basic-attack reveal is a temporary team provider in the
            // replication visibility graph. Minion help and retention must
            // see that same brush/LOS result instead of dropping a revealed
            // attacker on the next AI step.
            if (team is Team.Order or Team.Chaos &&
                entity.AttackRevealRemaining[(int)team] > 0.0)
                providers.Add(new VisionProvider(entity.AttackRevealPosition[(int)team], 400.0,
                    entity.SelectionRadius));
            if (entity.Team != team) continue;
            var radius = VisibilityRules.TerrainVisionRange(entity);
            if (radius > 0.0) providers.Add(new VisionProvider(entity.Position, radius, entity.SelectionRadius));
        }
        providers.AddRange(_itemProviders(team));
        return providers.Any(provider =>
            DistanceSquared(provider.Position, target.Position) <= provider.Radius * provider.Radius &&
            // A position bubble lights its whole disc, as in the fog (VisibilityRules, VisionProvider.IsUnit).
            (!provider.IsUnit || provider.Radius > VisibilityRules.NoMaskBubbleRadius ||
             _navigation.HasUnitVision(provider.Position, provider.SelectionRadius,
                target.Position, target.SelectionRadius)));
    }

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);
}
