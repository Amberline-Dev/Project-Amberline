using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Network.Replication;

/// <summary>A server-side vision source. The radius is measured from Position.</summary>
public readonly record struct VisionProvider
{
    public VisionProvider(Position2 position, double radius, double selectionRadius = -1.0, bool isUnit = true)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Z))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (!double.IsFinite(radius) || radius < 0.0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        Position = position;
        Radius = radius;
        SelectionRadius = selectionRadius;
        IsUnit = isUnit;
    }

    /// <summary>
    /// The client bubble's IsUnit flag (bit 2): every unit's own bubble and a unit-attached one (0x26) has it, a position bubble
    /// (0x7B) does not. The client's fog field draws a bubble without it, or with a radius
    /// over 3000, with no line-of-sight mask (<see cref="VisibilityRules"/>).
    /// </summary>
    public bool IsUnit { get; }

    public Position2 Position { get; }
    public double Radius { get; }
    /// <summary>
    /// The viewing unit's CharacterData SelectionRadius, which decides its own
    /// brush (the client's IsInWallOfGrass). Negative for a point source.
    /// </summary>
    public double SelectionRadius { get; }
}

/// <summary>Typed projection for Orianna's ball, which is not a normal world entity.</summary>
public readonly record struct BallProjection(
    uint NetId,
    uint OwnerNetId,
    Team Team,
    Position2 Position,
    bool Rendered = true,
    bool Attached = false,
    int SkinId = 0,
    bool NoRender = false);

/// <summary>Abstraction used by both capture tests and the native ENet peer adapter.</summary>
public interface IReplicationTransport
{
    void Send(PlaintextPacket packet);
}

/// <summary>
/// Optional typed extension of the combat queue. Spell/effect producers use
/// the byte-building <see cref="ICombatPacketQueue"/> contract for capture
/// queues, while a production delivery can retain target/reference metadata
/// until it routes the intent to each observer.
/// </summary>
public interface IReplicationIntentQueue : ICombatPacketQueue
{
    void EnqueueCast(SpellCastReplicationMessage message);
    void EnqueueMissile(MissileReplicationMessage message);
    void EnqueueEffectCreate(EffectCreateReplicationMessage message);
    void EnqueueEffectDestroy(EffectDestroyReplicationMessage message);
    void EnqueueChainMissileSync(ChainMissileSyncReplicationMessage message);
    void EnqueueLineMissileHitList(LineMissileHitListReplicationMessage message);
    void EnqueueCharacterData(CharacterDataReplicationMessage message);
    void EnqueueTemporaryCasterRetirement(TemporaryCasterRetirementMessage message);
}

/// <summary>Per-connection observer identity and visibility lifetime.</summary>
public sealed class ReplicationObserver
{
    public ReplicationObserver(
        ulong observerId,
        Team team,
        uint heroNetId,
        IReplicationTransport transport,
        bool ready = true,
        ReplicationView? view = null,
        uint? clientId = null,
        bool deathRecapReady = true)
    {
        if (observerId == 0) throw new ArgumentOutOfRangeException(nameof(observerId));
        ArgumentNullException.ThrowIfNull(transport);
        ObserverId = observerId;
        Team = team;
        HeroNetId = heroNetId;
        Transport = transport;
        ClientId = clientId;
        Ready = ready;
        DeathRecapReady = deathRecapReady;
        View = view ?? new ReplicationView();
    }

    public ulong ObserverId { get; }
    public Team Team { get; }
    public uint HeroNetId { get; }
    /// <summary>
    /// Authenticated participant/client identity used by native packets whose
    /// field is a player ID rather than an entity NetId.  ClientId zero is a
    /// valid seat; callers must not use HeroNetId as a fallback.
    /// </summary>
    public uint? ClientId { get; }
    public IReplicationTransport Transport { get; }
    public bool Ready { get; set; }
    /// <summary>Reconnect rehydration must finish before a retained recap.</summary>
    public bool DeathRecapReady { get; set; }
    public ReplicationView View { get; }
}

/// <summary>Mutable state which must never be shared between observers.</summary>
public sealed class ReplicationView
{
    public HashSet<uint> Known { get; } = new();
    public HashSet<uint> Visible { get; } = new();
    public HashSet<uint> Effects { get; } = new();
    public HashSet<(uint Actor, short FadeId)> CharacterFades { get; } = new();
    public HashSet<(uint Actor, uint Change)> CharacterData { get; } = new();
    /// <summary>Missiles created from a live snapshot after this view missed the cast.</summary>
    public HashSet<uint> LateMissiles { get; } = new();
    public Dictionary<uint, double> LostAt { get; } = new();
    public HashSet<uint> TerrainIds { get; } = new();
    public HashSet<uint> TimedTerrainIds { get; } = new();
    /// <summary>The sight radius modifiers this client holds for each allied unit it was sent a non-default pair for (flat, percent).</summary>
    public Dictionary<uint, (float Flat, float Percent)> BubbleMods { get; } = new();
    public Dictionary<uint, BallProjection> BallStates { get; } = new();
    /// <summary>The model scale this client holds for each unit it was sent a coefficient other than 1 for (<c>mScaleSkinCoef</c>).</summary>
    public Dictionary<uint, float> ModelScales { get; } = new();
    /// <summary>Last death-recap generation delivered to this connection.</summary>
    public Dictionary<uint, long> DeathRecapGenerations { get; } = new();
    /// <summary>Last empty-camp generation delivered to this observer.</summary>
    public Dictionary<int, int> NeutralEmptyGenerations { get; } = new();
    /// <summary>
    /// Buff adds this client holds. A known actor keeps its buffs in fog (the
    /// 0x7F removal still reaches it), and a repeated 0xBF renews the buff and
    /// replays its activation sound, so a reveal resends only a missed add or
    /// renewal. EndsAt is the delivery's SimulationTime plus the 0xBF duration.
    /// </summary>
    public Dictionary<(uint Actor, byte Slot), (uint Hash, byte Count, double EndsAt)> Buffs { get; } = new();
    /// <summary>Actors that left sight mid-dash; the client's copy of that dash is stale.</summary>
    public HashSet<uint> StaleDashes { get; } = new();
    /// <summary>What this client's replication store holds; filters each 0xCC block down to the fields that changed.</summary>
    public ClientReplicationStore Store { get; } = new();
    public bool TerrainBootstrapped { get; set; }

    /// <summary>
    /// The delivery rule for a unit a packet refers to: zero is no unit, and any other ID must have been created for this client
    /// (<see cref="Known"/>; a fogged unit stays known). <c>ReplicationDelivery.CanRoute</c> holds back a queued packet that names an
    /// unknown unit, and <see cref="ForClient"/> keeps an unknown unit out of the one field the client can do without.
    /// </summary>
    public bool KnowsUnit(uint netId) => netId == 0 || Known.Contains(netId);

    /// <summary>
    /// The packet as this client gets it. A buff packet's caster (<see cref="LeaguePacketEntityRole.Caster"/>) is optional: the stock
    /// client looks it up by net ID and keeps the buff on its holder without a caster when it cannot find it, so a caster this client
    /// was never told about (a summon hidden from the team that now sees the holder) is sent as 0 and the buff still arrives. The
    /// holder is not optional: <c>CanRoute</c> requires it, and the reveal's snapshot is built for a holder just created. Every
    /// packet leaves through <c>ReplicationDelivery.Send</c>, so live adds, renewals, stack updates and the reveal's replay all follow
    /// the one rule. Reconstruction: retail most likely sent the real ID; the client's
    /// failed lookup of it (the 0xBF handler, the 0x32 and 0x1F searches, see
    /// <c>SpellPackets.BuffCasterOffset</c>) leaves the buff without a caster, the state 0 gives directly. The retail server's choice is not
    /// recoverable; 0 keeps the wire free of units the client does not hold.
    /// </summary>
    public PlaintextPacket ForClient(PlaintextPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var bytes = packet.Plaintext.Span;
        if (bytes.IsEmpty || SpellPackets.BuffCasterOffset(bytes[0]) < 0) return packet;
        foreach (var reference in packet.EntityReferences)
            if (reference.Role == LeaguePacketEntityRole.Caster && !KnowsUnit(reference.NetId))
                return SpellPackets.WithoutCaster(packet);
        return packet;
    }

    /// <summary>
    /// Keeps a captured post-spawn peer's existing visibility snapshot stable
    /// while replaying its application stream.  This is intentionally opt-in
    /// for diagnostics; normal production observers always refresh visibility.
    /// </summary>
    public bool VisibilityRefreshSuppressed { get; set; }

    public void Reset()
    {
        Known.Clear();
        Visible.Clear();
        Effects.Clear();
        CharacterFades.Clear();
        CharacterData.Clear();
        LateMissiles.Clear();
        LostAt.Clear();
        TerrainIds.Clear();
        TimedTerrainIds.Clear();
        BubbleMods.Clear();
        BallStates.Clear();
        ModelScales.Clear();
        DeathRecapGenerations.Clear();
        NeutralEmptyGenerations.Clear();
        Buffs.Clear();
        StaleDashes.Clear();
        Store.Clear();
        TerrainBootstrapped = false;
        VisibilityRefreshSuppressed = false;
    }
}

/// <summary>Terrain/vision hooks are explicit because item and spell providers are runtime-owned.</summary>
public interface IReplicationServices
{
    IReadOnlyList<PlaintextPacket> BuildEntityCreation(Entity entity, ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildNeutralCampBaseline(Entity entity, ReplicationObserver observer)
        => Array.Empty<PlaintextPacket>();
    IReadOnlyList<PlaintextPacket> BuildEntityStats(Entity entity, ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildBuffSnapshot(Entity entity, ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildBallProjection(BallProjection ball, ReplicationObserver observer);
    /// <summary>
    /// The reveal; a moving or standing unit's 0xC2 carries its route, dash or stop, so no reveal-time route follows it. With
    /// <paramref name="staleDash"/> (the observer lost sight of the unit mid-dash) its movement entry carries a teleport to where the unit is.
    /// </summary>
    IReadOnlyList<PlaintextPacket> BuildReveal(Entity entity, ReplicationObserver observer,
        IReadOnlySet<uint>? visibleNow = null, bool staleDash = false);
    IReadOnlyList<PlaintextPacket> BuildWaypointReanchor(Entity entity, ReplicationObserver observer,
        IReadOnlySet<uint>? visibleNow = null);
    bool StatsBeforeReveal(Entity entity);
    IReadOnlyList<PlaintextPacket> BuildBallReveal(BallProjection ball, ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildBallLeave(BallProjection ball, ReplicationObserver observer);

    IReadOnlyList<PlaintextPacket> BuildTerrainBootstrap(ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildTerrainAttach(
        Entity entity, uint bubbleId, float radius, ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildTerrainRemove(uint bubbleId, ReplicationObserver observer);
    IReadOnlyList<PlaintextPacket> BuildLeave(Entity entity, ReplicationObserver observer);
}

/// <summary>Map geometry needed by server-side vision filtering.</summary>
public interface IVisionEnvironment
{
    double CellSize { get; }
    bool HasVisionLos(Position2 source, Position2 target);
    bool BrushAllowsVision(Position2 source, Position2 target);
    int? BrushIdAt(Position2 point);
    /// <summary>A unit's brush by its CharacterData SelectionRadius (client IsWallOfGrass).</summary>
    int? BrushIdAt(Position2 point, double selectionRadius) => BrushIdAt(point);
    /// <summary>The client's unit sight walk, <see cref="NavigationGrid.UnitLineOfSightTest"/>.</summary>
    bool UnitLineOfSightTest(Position2 viewer, double viewerSelectionRadius, bool viewerInBrush, Position2 target, bool targetInBrush) =>
        (!targetInBrush || viewerInBrush) && HasVisionLos(viewer, target);
    /// <summary>The client's turret line-of-sight test, <see cref="NavigationGrid.TurretLineOfSightTest"/>.</summary>
    bool TurretLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double? viewerPerceptionRadius,
        Position2 turret, double turretSelectionRadius, double turretGameplayRadius) =>
        Math.Pow(viewer.X - turret.X, 2) + Math.Pow(viewer.Z - turret.Z, 2) <
        Math.Pow(Math.Min(viewerPerceptionRadius ?? 3000.0, 3000.0), 2) && HasVisionLos(viewer, turret);
    /// <summary>The client's camp marker line-of-sight test, <see cref="NavigationGrid.CampMarkerLineOfSightTest"/>.</summary>
    bool CampMarkerLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double viewerPathfindingRadius, Position2 marker) =>
        Math.Pow(viewer.X - marker.X, 2) + Math.Pow(viewer.Z - marker.Z, 2) < 1000000.0 && HasVisionLos(viewer, marker);
}

/// <summary>Adapter that keeps the authoritative navigation implementation in Data.</summary>
public sealed class NavigationGridVisionEnvironment : IVisionEnvironment
{
    private readonly NavigationGrid _grid;

    public NavigationGridVisionEnvironment(NavigationGrid grid)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
    }

    public double CellSize => _grid.CellSize;
    public bool HasVisionLos(Position2 source, Position2 target) => _grid.HasVisionLos(source, target);
    public bool BrushAllowsVision(Position2 source, Position2 target) => _grid.BrushAllowsVision(source, target);
    public int? BrushIdAt(Position2 point) => _grid.BrushIdAt(point);
    public int? BrushIdAt(Position2 point, double selectionRadius) => _grid.BrushIdAt(point, selectionRadius);
    public bool UnitLineOfSightTest(Position2 viewer, double viewerSelectionRadius, bool viewerInBrush, Position2 target,
        bool targetInBrush) => _grid.UnitLineOfSightTest(viewer, viewerSelectionRadius, viewerInBrush, target, targetInBrush);
    public bool TurretLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double? viewerPerceptionRadius,
        Position2 turret, double turretSelectionRadius, double turretGameplayRadius) =>
        _grid.TurretLineOfSightTest(viewer, viewerSelectionRadius, viewerPerceptionRadius, turret, turretSelectionRadius,
            turretGameplayRadius);
    public bool CampMarkerLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double viewerPathfindingRadius,
        Position2 marker) => _grid.CampMarkerLineOfSightTest(viewer, viewerSelectionRadius, viewerPathfindingRadius, marker);
}

/// <summary>Low-cost evidence that raw visibility is calculated per team per frame.</summary>
public sealed class ReplicationInstrumentation
{
    public int Frame { get; private set; }
    public int RawVisibilityCalculations { get; private set; }
    public Dictionary<Team, int> RawCalculationsByTeam { get; } = new();

    internal void BeginFrame()
    {
        Frame++;
        RawVisibilityCalculations = 0;
        RawCalculationsByTeam.Clear();
    }

    internal void CountRaw(Team team)
    {
        RawVisibilityCalculations++;
        RawCalculationsByTeam[team] = RawCalculationsByTeam.GetValueOrDefault(team) + 1;
    }
}

public enum ReplicationMessageKind
{
    Normal,
    Lifecycle,
    SpellCast,
    Missile,
    EffectCreate,
    EffectDestroy,
    CharacterData,
    TemporaryCasterRetirement,
    ChainMissileSync,
    LineMissileHitList,
}

public abstract record ReplicationMessage(ReplicationMessageKind Kind);

/// <summary>A persistent native model-stack entry; null SkinName removes it.</summary>
public sealed record CharacterDataReplicationMessage(uint Actor, uint Change,
    string? SkinName, bool UseSpells = false, bool All = false) : ReplicationMessage(ReplicationMessageKind.CharacterData)
{
    /// <summary>The pop-all of every entry of the unit (batch 256: <c>BBPopAllCharacterData</c>'s 5-byte 0x6A); Change is unused.</summary>
    public static CharacterDataReplicationMessage PopAll(uint actor) => new(actor, 0, null, All: true);

    public PlaintextPacket Packet => All ? SpellPackets.PopAllCharacterData(Actor)
        : SkinName is null ? SpellPackets.PopCharacterData(Actor, Change)
        : SpellPackets.PushCharacterData(Actor, Change, SkinName, UseSpells);
}

public sealed record PacketReplicationMessage(
    PlaintextPacket Packet,
    bool Global = false,
    ulong? ObserverId = null,
    ToggleCarrierMark? ToggleCarrier = null) : ReplicationMessage(ReplicationMessageKind.Normal);

/// <summary>
/// Batch 405: a toggle carrier add (see <c>ICombatPacketQueue.EnqueueToggleCarrier</c>): the unit and the 0-based slot its buff toggles on.
/// The delivery sends the add behind the last rename of that slot in its flush.
/// </summary>
public readonly record struct ToggleCarrierMark(uint UnitNetId, int Slot);

public sealed record LifecycleReplicationMessage : ReplicationMessage
{
    public LifecycleReplicationMessage(
        uint victimNetId,
        uint killerNetId,
        Func<uint, PlaintextPacket> buildPacket) : base(ReplicationMessageKind.Lifecycle)
    {
        if (victimNetId == 0) throw new ArgumentOutOfRangeException(nameof(victimNetId));
        ArgumentNullException.ThrowIfNull(buildPacket);
        VictimNetId = victimNetId;
        KillerNetId = killerNetId;
        BuildPacket = buildPacket;
    }

    public uint VictimNetId { get; }
    public uint KillerNetId { get; }
    public Func<uint, PlaintextPacket> BuildPacket { get; }
}

// Reconstruction (batch 393): MissileOnlyForCaster leaves the cast answer out of the caster's own client only, which takes the child's missile
// (0x41) alone; the answer would build a SpellCaster there and open the HUD cast bar. Every other viewer keeps it.
// Reconstruction (batch 582): MissileOnly leaves the cast answer out of every view; each view it would reach still tracks the missile, so the
// Instacast child's creation 0x41 (NeedsCastAnnouncement) reaches exactly those views (ProtocolSpellEffectSink.SendInstantMissileCast).
public sealed record SpellCastReplicationMessage(
    uint CasterNetId,
    SpellCastInfo Info,
    int PositionSyncId = 0,
    bool MissileOnlyForCaster = false,
    bool MissileOnly = false) : ReplicationMessage(ReplicationMessageKind.SpellCast);

public sealed record MissileReplicationMessage(
    uint CasterNetId,
    Position3 Position,
    Position3 CasterPosition,
    Position3 Direction,
    Position3 Velocity,
    Position3 StartPoint,
    Position3 EndPoint,
    Position3 UnitPosition,
    float Speed,
    float LifePercentage,
    byte Bounced,
    SpellCastInfo Info,
    bool RefreshOnly = false,
    MissileCasterProjection? CasterProjection = null,
    // An Instacast flight's creation: its cast answer built nothing on the client, so only the views that the cast answer reached
    // (TrackMissile) get it. A view that missed the cast gets the live refresh instead, never a copy of the launch point.
    bool NeedsCastAnnouncement = false) : ReplicationMessage(ReplicationMessageKind.Missile);

/// <summary>
/// The hop list of a chain missile (ChainMissileSync, FromID = the missile). It is routed to the views that hold the
/// missile (the cast or missile replication that created it was delivered) and lists, per view, the hops that view knows: the
/// client turns each net ID into an object id and a unit it never learned of would end the chain there. A chain
/// <paramref name="Attack"/>'s missile came with the basic-attack packet (the first hop is the attacked unit), whose delivery
/// rule decides the views instead.
/// </summary>
public sealed record ChainMissileSyncReplicationMessage(
    uint MissileNetId,
    uint OwnerNetId,
    IReadOnlyList<uint> Targets,
    bool Attack = false) : ReplicationMessage(ReplicationMessageKind.ChainMissileSync);

/// <summary>
/// Batch 591: the units a line missile hit (LineMissileHitList, FromID = the missile). Like the chain hop list it reaches only the views that
/// hold the missile (the client registers for it under the missile's net ID), cut to the units the view knows. An attack's line (Spray and Pray)
/// names its attacker and the attacked unit: its missile came with the 0x0D, so a view holds it when it knows both and sees one (the chain attack's rule).
/// </summary>
public sealed record LineMissileHitListReplicationMessage(
    uint MissileNetId,
    IReadOnlyList<uint> Targets,
    uint AttackerNetId = 0,
    uint AttackTargetNetId = 0) : ReplicationMessage(ReplicationMessageKind.LineMissileHitList);

public sealed record TemporaryCasterRetirementMessage(uint NetId)
    : ReplicationMessage(ReplicationMessageKind.TemporaryCasterRetirement);

/// <summary>Appearance variants share an identity, position and actor references;
/// routing checks the common dependencies before choosing the observer's packet.
/// BindNetId and the Unbound packets (batch 419) let the delivery send the unbound variant when the bound unit is dead on the wire.
/// FowTeam (batch 473) is the effect's own fog-of-war team (<c>FOWTeam</c>), whose observers get an effect that names no current unit wherever it is.</summary>
public sealed record EffectCreateReplicationMessage(
    PlaintextPacket Packet,
    uint EffectNetId,
    Position2? Position = null,
    Team? AlliedTeam = null,
    PlaintextPacket? EnemyPacket = null,
    bool Persistent = false,
    uint OwnerHeroNetId = 0,
    Func<bool, PlaintextPacket>? LivePacket = null,
    IReadOnlySet<Team>? CreationVisibleTeams = null,
    uint BindNetId = 0,
    PlaintextPacket? UnboundPacket = null,
    PlaintextPacket? UnboundEnemyPacket = null,
    uint DeadBindNetId = 0,
    Team? FowTeam = null)
    : ReplicationMessage(ReplicationMessageKind.EffectCreate);

public sealed record EffectDestroyReplicationMessage(
    PlaintextPacket Packet,
    uint EffectNetId,
    bool LateMissileOnly = false) : ReplicationMessage(ReplicationMessageKind.EffectDestroy);
