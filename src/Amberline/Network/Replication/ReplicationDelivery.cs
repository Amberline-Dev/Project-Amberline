using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;
using System.Buffers.Binary;

namespace Amberline.Network.Replication;

/// <summary>
/// Typed observer delivery for one match. A single instance owns a world and
/// is expected to be pumped by one match thread; transports may be native or a
/// capture sink, but are never shared across observers.
/// </summary>
public sealed partial class ReplicationDelivery : IReplicationIntentQueue
{
    private readonly List<ReplicationObserver> _observers = new();
    private readonly List<ReplicationMessage> _pending = new();
    // Reconstruction (batch 389): each buff add or remove of this flush that pairs with another one, mapped to its partner and whether it is the
    // first of the two in queue order (see PairBuffDeltas). Rebuilt before each routing pass, cleared with the queue.
    private readonly Dictionary<PacketReplicationMessage, (PacketReplicationMessage Partner, bool First)> _buffPartners =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<uint, EffectCreateReplicationMessage> _persistentEffects = new();
    private readonly Dictionary<(uint Actor, uint Change), CharacterDataReplicationMessage> _characterData = new();
    // A shrine is a pre-placed turret whose team-visibility bit is managed by
    // the source's pre-combat turret-vision phase.  Keep this per team and
    // per shrine so a target-valid reveal does not get immediately undone by
    // the ordinary raw-visibility pass in the same flush.
    private readonly HashSet<(ulong ObserverId, uint NetId)> _preparedShrines = new();
    private readonly HashSet<uint> _managedBallIds = new();
    private readonly HashSet<Team> _noRenderBallTeams = new();
    private readonly HashSet<uint> _queuedTemplateIds = new();
    // Pets whose spawn broadcast (0x3C) already went out: the spawn is one event, so a viewer that learns the owner later meets the pet at first sight.
    private readonly HashSet<uint> _announcedPetSpawns = new();
    // What the stock client's per-unit, per-channel replication store holds after the
    // first-sight/reveal stats this flush sent (highest sync id and applied mask per channel).
    // A queued 0xCC that only repeats those fields under a lower sync id is dropped by the
    // client (OnNetReceive applies a block only if stored < incoming), so it is not sent.
    private readonly Dictionary<(ReplicationObserver Observer, uint NetId), (uint[] Sync, uint[] Applied)> _freshReplication = new();
    private readonly Dictionary<ReplicationObserver, List<PlaintextPacket>> _deferredLeaves = new();
    private readonly HashSet<(ReplicationObserver Observer, uint NetId)> _forcedMoveReanchors = new();
    // Lane minions whose reveal this flush already sent the observer their live route. A 0xC1 queued earlier
    // for the same minion (such as the wave-spawn route) would only repeat it.
    private readonly HashSet<(ReplicationObserver Observer, uint NetId)> _revealedMinionRoutes = new();
    private readonly Dictionary<(ReplicationObserver Observer, uint NetId),
        (ForcedMoveState Move, Position2 Position, Position2 Destination, bool TargetVisible)> _leeFollowDashes = new();
    private readonly Dictionary<(ReplicationObserver Observer, uint NetId), bool> _leeDashSent = new();
    // The last spellbook tooltip packet (0x83) each observer was sent for a unit in this flush (IsRepeatedSpellTooltips).
    private readonly Dictionary<(ReplicationObserver Observer, uint NetId), byte[]> _spellTooltipsSent = new();

    public ReplicationDelivery(
        WorldState world,
        IReplicationServices services,
        IVisionEnvironment environment,
        Func<Team, IEnumerable<VisionProvider>> additionalProviders,
        Func<Team, Position2, bool> revealsStealth,
        Func<double>? simulationTime = null,
        Func<uint, uint>? nextSyncId = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        AdditionalProviders = additionalProviders ?? throw new ArgumentNullException(nameof(additionalProviders));
        RevealsStealth = revealsStealth ?? throw new ArgumentNullException(nameof(revealsStealth));
        SimulationTime = simulationTime ?? (() => World.Clock.Now());
        NextSyncId = nextSyncId ?? (_ => checked((uint)World.Clock.SyncId()));
    }

    public WorldState World { get; }
    public IReplicationServices Services { get; }
    public IVisionEnvironment Environment { get; }
    public ReplicationInstrumentation Instrumentation { get; } = new();
    public List<BallProjection> Balls { get; } = new();
    public Func<Team, IEnumerable<VisionProvider>> AdditionalProviders { get; }
    public Func<Team, Position2, bool> RevealsStealth { get; }
    /// <summary>
    /// Optional authoritative camp state.  Production attaches the jungle
    /// scheduler after composition; capture-only deliveries can remain
    /// neutral-free.  Empty-camp packets are emitted here because this layer
    /// owns ready observers and team LOS providers.
    /// </summary>
    public IReadOnlyDictionary<int, NeutralCampState>? NeutralCamps { get; set; }
    /// <summary>Authoritative simulation clock used by fog and terrain timers.</summary>
    public Func<double> SimulationTime { get; set; }
    private Func<uint, uint> NextSyncId { get; }
    /// <summary>Optional post-visibility diagnostic callback.</summary>
    public Action? AfterVisibilityFlush { get; set; }
    /// <summary>
    /// Pulls a retained last-death snapshot for an owner after each visibility
    /// refresh. This is what lets a newly reconnected dead hero receive the
    /// same immutable snapshot without replaying it to other observers.
    /// </summary>
    public Func<uint, DeathRecapSnapshot?>? DeathRecapSnapshotProvider { get; set; }
    /// <summary>
    /// Batch 472: what an observer is owed for units it has just learned, sent after this flush's creations (map 8: the records of a capture
    /// point whose guardian a returning client did not hold at its reconnect, BehaviorTreeQuestHost.FirstSightState); null on other maps.
    /// </summary>
    public Func<ReplicationObserver, IReadOnlyList<PlaintextPacket>>? FirstSightStateProvider { get; set; }
    /// <summary>
    /// Batch 419 (Reconstruction): a non-persistent effect that Flush sent unbound because its bound unit was dead on the wire (OnWire). The
    /// composition ends it with that unit's corpse (SpellSystem.EndEffectWithUnitCorpse), the placemarker rule.
    /// </summary>
    public event Action<uint, uint>? UnboundEffectAtDeadUnit;
    public IReadOnlyList<ReplicationObserver> Observers => _observers;
    public void Enqueue(ReplicationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is EffectCreateReplicationMessage { Persistent: true, EffectNetId: not 0 } effect)
            _persistentEffects[effect.EffectNetId] = effect;
        else if (message is EffectDestroyReplicationMessage destroy)
            _persistentEffects.Remove(destroy.EffectNetId);
        else if (message is CharacterDataReplicationMessage model)
        {
            var key = (model.Actor, model.Change);
            if (model.All)
                foreach (var held in _characterData.Keys.Where(held => held.Actor == model.Actor).ToArray()) _characterData.Remove(held);
            else if (model.SkinName is null) _characterData.Remove(key);
            else _characterData[key] = model;
        }
        // Capture ground sight before later damage/movement in this tick can
        // remove it. Persistent replay keeps the original, current-sight-only
        // message above; creation sight is only valid for this queued event.
        if (message is EffectCreateReplicationMessage { Position: Position2 position } ground &&
            !ground.Packet.EntityReferences.Any(reference =>
                reference.Role != LeaguePacketEntityRole.Create && reference.NetId != 0))
        {
            RefreshManagedBallProjections();
            var teams = new HashSet<Team>();
            foreach (var team in _observers.Where(observer => observer.Ready)
                         .Select(observer => observer.Team).Distinct())
                if (VisibilityRules.ProvidersReveal(Environment,
                        VisibilityRules.BuildTeamProviders(World, team, Balls, AdditionalProviders), position))
                    teams.Add(team);
            message = ground with { CreationVisibleTeams = teams };
        }
        _pending.Add(message);
    }


    public void Flush()
    {
        RefreshManagedBallProjections();
        QueueHealAnnouncements();
        DropPreDeathStarts();
        AnnounceCastsBeforeTheirSlotRenames();
        MoveToggleCarriersBehindTheirSlotRenames();
        _queuedTemplateIds.Clear();
        _freshReplication.Clear();
        _forcedMoveReanchors.Clear();
        _revealedMinionRoutes.Clear();
        _leeDashSent.Clear();
        _spellTooltipsSent.Clear();
        foreach (var packet in _pending.OfType<PacketReplicationMessage>())
        {
            var opcode = packet.Packet.Plaintext.Span.Length == 0
                ? (byte?)null : packet.Packet.Plaintext.Span[0];
            if (opcode is 0x3C or 0x66 or 0x80 or 0xA5 or 0x4F)
                foreach (var reference in packet.Packet.EntityReferences)
                    if (reference.Role == LeaguePacketEntityRole.Create && reference.NetId != 0)
                        _queuedTemplateIds.Add(reference.NetId);
        }
        Instrumentation.BeginFrame();
        var active = _observers.Where(observer => observer.Ready).ToArray();
        _deferredLeaves.Clear();
        foreach (var observer in active)
            _deferredLeaves[observer] = new List<PlaintextPacket>();
        var teamState = BuildTeamState(active);
        var oldVisible = active.ToDictionary(
            observer => observer,
            observer => new HashSet<uint>(observer.View.Visible));

        // Death and other lifecycle packets are resolved against each
        // observer's old Known set before the visibility transition.
        foreach (var message in _pending.OfType<LifecycleReplicationMessage>())
        foreach (var observer in active)
        {
            if (!observer.View.Known.Contains(message.VictimNetId))
                continue;
            var killer = message.KillerNetId;
            if (killer == 0 || !observer.View.Known.Contains(killer))
                killer = message.VictimNetId;
            Send(observer, message.BuildPacket(killer));
        }

        foreach (var observer in active)
            RefreshObserver(observer, ObserverState(observer, teamState));
        _announcedPetSpawns.RemoveWhere(id => World.Get(id) is not { Alive: true });
        foreach (var entity in World.Entities.Values)
            if (entity.Alive && IsSpawnedAsPet(entity)) _announcedPetSpawns.Add(entity.NetId);

        PairBuffDeltas();
        foreach (var message in _pending)
        {
            if (message is LifecycleReplicationMessage)
                continue;
            var routed = message is EffectCreateReplicationMessage create ? OnWire(create) : message;
            foreach (var observer in active)
                Route(routed, observer, teamState[observer.Team], oldVisible[observer]);
        }

        RefreshLeeFollowDashes(active);

        var layers = _characterData.Values.OrderBy(model => model.Change).ToArray();
        foreach (var observer in active)
        {
            // Repair both a missed push and a missed pop after fog/reconnect. A pop clears the client's whole stack of the unit (SendCharacterData),
            // so the pushes follow it, oldest first: the newest layer ends on top.
            foreach (var key in observer.View.CharacterData.Where(key =>
                         observer.View.Visible.Contains(key.Actor) && !_characterData.ContainsKey(key)).ToArray())
                SendCharacterData(observer, new(key.Actor, key.Change, null));
            foreach (var model in layers)
                if (observer.View.Visible.Contains(model.Actor) && observer.View.Known.Contains(model.Actor))
                    SendCharacterData(observer, model);
        }

        foreach (var observer in active) SyncBubbleMods(observer);

        foreach (var observer in active) SyncModelScales(observer);

        // Only explicitly retained live effects may appear on late sight or
        // reconnect. Destroy removes them before this replay, even if creation
        // and retirement were queued in the same frame.
        foreach (var effect in _persistentEffects.Values)
        {
            var routed = OnWire(effect);
            foreach (var observer in active)
                Route(routed, observer, teamState[observer.Team], observer.View.Visible);
        }

        if (FirstSightStateProvider is { } owed)
            foreach (var observer in active)
                foreach (var packet in owed(observer))
                    Send(observer, packet);

        // Death recap is owner-private and must follow lifecycle traffic,
        // ordinary queued state (including a temporary-life ForceDead), and
        // this observer's fresh Known set. The provider covers both the death
        // frame and a ready reconnect while the hero remains dead. The
        // per-view generation cursor makes repeated pulls idempotent.
        if (DeathRecapSnapshotProvider is { } provider)
            foreach (var observer in active)
                if (provider(observer.HeroNetId) is { } snapshot)
                    EmitDeathRecap(observer, snapshot.VictimNetId, snapshot);

        // A camp-empty state is a team observation, not a global gameplay
        // packet.  Route it after death/lifecycle traffic so every observer
        // receives a valid ClientId-based 0xCB only after its existing camp
        // member registration and current team LOS are established.
        foreach (var observer in active)
            RefreshNeutralEmpty(observer, teamState[observer.Team]);

        // The source routes gameplay packets against the old visible set and
        // only then emits the leave burst for objects that fell out of view.
        // Keeping these packets deferred preserves cast/death ordering at a
        // fog edge (the old object can still receive its final event).
        foreach (var observer in active)
            foreach (var packet in _deferredLeaves[observer])
                Send(observer, packet);

        if (active.Length > 0)
        {
            foreach (var entity in World.Entities.Values)
            {
                // A post-spawn replay observer deliberately keeps the
                // authoritative entity visibility flag from its capture. It
                // still receives queued packets through Route, but must not
                // turn an offline command corpus into a synthetic world
                // visibility mutation merely because it was attached for
                // packet capture.
                if (active.All(observer => observer.View.VisibilityRefreshSuppressed))
                    continue;
                if (entity.Placemarker && !entity.Announced) continue;
                entity.Created = active.Any(observer => observer.View.Known.Contains(entity.NetId));
                entity.Visible = active.Any(observer => observer.View.Visible.Contains(entity.NetId));
            }
        }
        _pending.Clear();
        _buffPartners.Clear();
        _queuedTemplateIds.Clear();
        _freshReplication.Clear();
        _preparedShrines.Clear();
        _spellTooltipsSent.Clear();
        try { AfterVisibilityFlush?.Invoke(); }
        catch { /* diagnostics never affect observer delivery */ }
    }

    private void Route(
        ReplicationMessage message,
        ReplicationObserver observer,
        TeamVisibilityState teamState,
        IReadOnlySet<uint> oldVisible)
    {
        var routeVisible = new HashSet<uint>(oldVisible);
        routeVisible.UnionWith(observer.View.Visible);
        switch (message)
        {
            case CharacterDataReplicationMessage model:
                // ChangeCharacterData (0x9F, BBPushCharacterData) and the 0x6A pops (BBPopCharacterData,
                // BBPopAllCharacterData) are PolicyBroadcast (batch 329, see CanRoute); their handlers (the stack's Push,
                // PopAll) test no visibility, so a client that holds the unit gets them in fog too.
                if (observer.View.Known.Contains(model.Actor))
                    SendCharacterData(observer, model);
                break;
            case PacketReplicationMessage packet:
                if (packet.ObserverId is ulong observerId && observer.ObserverId != observerId)
                    break;
                // Creation, visibility, and terrain packets are reconstructed
                // by RefreshObserver for each recipient.  The source flush
                // drops these queued packets so a spawn/reveal generated by a
                // gameplay action cannot be duplicated or leak to a viewer
                // that did not see the action.
                if (IsRefreshRebuilt(packet.Packet) ||
                    IsSupersededByFreshReplication(packet.Packet, observer))
                    break;
                // The reveal already sent the remaining forced segment. A
                // queued original-start dash or fear waypoint would rewind it.
                if (packet.Packet.Plaintext.Span[0] is VisionPackets.DashOpcode or VisionPackets.WaypointListOpcode &&
                    packet.Packet.EntityReferences.Any(reference =>
                        _forcedMoveReanchors.Contains((observer, reference.NetId))))
                    break;
                // The reveal already sent this lane minion's live route; a queued route repeats it.
                if (packet.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
                    packet.Packet.EntityReferences.Any(reference =>
                        _revealedMinionRoutes.Contains((observer, reference.NetId))))
                    break;
                if (IsOwnerClientOnly(packet.Packet) && !IsOwnerClient(observer, packet.Packet.Plaintext.Span))
                    break;
                if (CanRoute(packet.Packet, observer.View, routeVisible, packet.Global))
                {
                    if (_buffPartners.TryGetValue(packet, out var buffPair) &&
                        CoalescesBuffPair(packet, buffPair, routeVisible, observer))
                        break;
                    if (packet.Packet.Plaintext.Span[0] == VisionPackets.DashOpcode &&
                        packet.Packet.EntityReferences.FirstOrDefault() is { NetId: var actorId } &&
                        World.Get(actorId) is { ForcedMove: { Effect: "BlindMonkQTwoDash",
                            Completed: false, FollowNetId: uint targetId } } actor)
                    {
                        // A target retained only in Known has a stale native
                        // position. Never route its follow id through fog.
                        var followVisible = observer.View.Visible.Contains(targetId);
                        if (followVisible)
                            Send(observer, packet.Packet);
                        else
                            SendAll(observer, Services.BuildWaypointReanchor(actor, observer));
                        _leeDashSent[(observer, actorId)] = followVisible;
                    }
                    else Send(observer, AsHeldBuffUpdate(observer.View, packet.Packet));
                    MarkCreated(observer.View, packet.Packet);
                }
                break;
            case SpellCastReplicationMessage cast:
                if (!observer.View.Known.Contains(cast.CasterNetId)) break;
                // Reconstruction (batch 393, owner findings 16, 58, 84): the caster's own client takes the child's missile as the 0x41 alone. Its view
                // stays untracked, so the missile's first RefreshOnly packet below creates the object there; the other views keep the cast answer.
                if (cast.MissileOnlyForCaster && observer.HeroNetId == cast.CasterNetId)
                {
                    // Reconstruction (batch 394, owner finding 84): a cast with no missile (MissileNetId 0, CastType 0) has no 0x41 of its own, so the
                    // caster's own client takes the cast as that 0x41 with no missile: the blob is the cast info and the client runs the spell from it
                    // alone. A missile cast's 0x41 comes from its missile's own replication, so nothing is sent here for it.
                    if (cast.Info.MissileNetId == 0)
                        Send(observer, SpellPackets.MissileReplication(cast.CasterNetId, cast.Info.LaunchPosition, cast.Info.LaunchPosition, default,
                            default, cast.Info.LaunchPosition, cast.Info.TargetPositionEnd, cast.Info.TargetPosition, 0f, 0f, 0,
                            cast.Info with { Targets = FilterSpellTargets(cast.Info.Targets, observer.View.Known) }));
                    break;
                }
                var castTargets = FilterSpellTargets(cast.Info.Targets, observer.View.Known);
                // A cast flagged as an auto attack (a script's chain attack) stands for a 0x0D, whose delivery needs the attacked unit
                // known: the chain missile of a view that never learned of it would have no target.
                if ((cast.Info.Flags & 1) != 0 && castTargets.Count != cast.Info.Targets.Count) break;
                if (!routeVisible.Contains(cast.CasterNetId) &&
                    !castTargets.Any(target => routeVisible.Contains(target.NetId)))
                    break;
                // Batch 582: an Instacast missile child's view takes the creation 0x41 alone (SpellCastReplicationMessage.MissileOnly).
                if (cast.MissileOnly)
                {
                    TrackMissile(observer.View, cast.Info.MissileNetId);
                    break;
                }
                Send(observer, SpellPackets.CastSpellAnswer(
                    cast.CasterNetId, cast.Info with { Targets = castTargets }, cast.PositionSyncId));
                TrackMissile(observer.View, cast.Info.MissileNetId);
                break;
            case MissileReplicationMessage missile:
                var projection = missile.CasterProjection;
                if (projection is { } projectedCaster)
                {
                    if (projectedCaster.NetId != missile.CasterNetId ||
                        projectedCaster.NetId != missile.Info.CasterNetId)
                        break;
                }
                else if (!observer.View.Known.Contains(missile.CasterNetId)) break;
                if (missile.RefreshOnly && missile.Info.MissileNetId != 0 &&
                    observer.View.Effects.Contains(missile.Info.MissileNetId))
                    break;
                // Batch 177 part 2: an Instacast flight's creation goes only to the views its cast answer reached (the cast was their launch);
                // a view that missed the cast is served by the refresh branch below at the live position.
                if (missile.NeedsCastAnnouncement && !observer.View.Effects.Contains(missile.Info.MissileNetId))
                    break;
                var missileTargets = FilterSpellTargets(missile.Info.Targets, observer.View.Known);
                // A homing missile cannot be constructed without its target.
                // Wait for that actor's normal template instead of erasing it.
                if (missileTargets.Count != missile.Info.Targets.Count)
                    break;
                if (missile.RefreshOnly)
                {
                    var point = new Position2(missile.Position.X, missile.Position.Z);
                    if (!VisibilityRules.ProvidersReveal(Environment, teamState.Providers, point))
                        break;
                }
                else if (!routeVisible.Contains(projection?.OwnerNetId ?? missile.CasterNetId) &&
                         !missileTargets.Any(target => routeVisible.Contains(target.NetId)) &&
                         !VisibilityRules.ProvidersReveal(Environment, teamState.Providers,
                             new Position2(missile.Position.X, missile.Position.Z)))
                    break;
                if (projection is { } caster && !observer.View.Known.Contains(caster.NetId))
                    CreateProjectedCaster(observer, caster, missile.Info);
                Send(observer, SpellPackets.MissileReplication(
                    missile.CasterNetId,
                    missile.Position,
                    missile.CasterPosition,
                    missile.Direction,
                    missile.Velocity,
                    missile.StartPoint,
                    missile.EndPoint,
                    missile.UnitPosition,
                    missile.Speed,
                    missile.LifePercentage,
                    missile.Bounced,
                    missile.Info with { Targets = missileTargets }));
                TrackMissile(observer.View, missile.Info.MissileNetId);
                if (missile.RefreshOnly && missile.Info.MissileNetId != 0)
                    observer.View.LateMissiles.Add(missile.Info.MissileNetId);
                break;
            case ChainMissileSyncReplicationMessage chain:
                {
                    // The client's missile registers for packets from its own net ID, so only a view that was told of the missile
                    // (the cast or the missile replication, TrackMissile) can use the list. A hop this view never learned of would end
                    // the chain at that slot (the client maps net IDs to object ids; an unknown one becomes empty), so the list is cut
                    // to the units the view knows. Reconstruction: how the retail server filters its hop list per client is not in the
                    // client.
                    // A chain attack's missile came with the 0x0D (CanRoute's rule: the attacker and the attacked unit known, one seen).
                    var holdsMissile = chain.Attack
                        ? chain.Targets.Count > 0 && observer.View.Known.Contains(chain.OwnerNetId) &&
                          observer.View.Known.Contains(chain.Targets[0]) &&
                          (routeVisible.Contains(chain.OwnerNetId) || routeVisible.Contains(chain.Targets[0]))
                        : observer.View.Effects.Contains(chain.MissileNetId);
                    if (!holdsMissile) break;
                    var hops = chain.Targets.Where(observer.View.Known.Contains).ToArray();
                    if (hops.Length == 0) break;
                    Send(observer, SpellPackets.ChainMissileSync(chain.MissileNetId, chain.OwnerNetId, hops));
                    break;
                }
            case LineMissileHitListReplicationMessage hitList:
                {
                    // Batch 591: only a client that holds the missile has the handler (registered under the missile's net ID); an
                    // unknown unit in the list would run the hit pass on no unit, so the list is cut to the units this view knows.
                    var holdsLine = hitList.AttackerNetId != 0
                        ? observer.View.Known.Contains(hitList.AttackerNetId) && observer.View.Known.Contains(hitList.AttackTargetNetId) &&
                          (routeVisible.Contains(hitList.AttackerNetId) || routeVisible.Contains(hitList.AttackTargetNetId))
                        : observer.View.Effects.Contains(hitList.MissileNetId);
                    if (!holdsLine) break;
                    var hits = hitList.Targets.Where(observer.View.Known.Contains).ToArray();
                    if (hits.Length > 0) Send(observer, SpellPackets.LineMissileHitList(hitList.MissileNetId, hits));
                    break;
                }
            case TemporaryCasterRetirementMessage retirement:
                if (!observer.View.Known.Remove(retirement.NetId)) break;
                Send(observer, CombatPackets.NpcDie(retirement.NetId, retirement.NetId));
                observer.View.Visible.Remove(retirement.NetId);
                foreach (var buff in observer.View.Buffs.Keys.Where(buff => buff.Actor == retirement.NetId).ToArray())
                    observer.View.Buffs.Remove(buff);
                break;
            case EffectCreateReplicationMessage effect:
                // A first-sight snapshot may already have restored this persistent
                // effect earlier in the same flush. Never create its native ID twice.
                if (effect.EffectNetId != 0 && observer.View.Effects.Contains(effect.EffectNetId)) break;
                if (CanRouteEffectCreate(effect, observer, teamState, routeVisible))
                {
                    var enemy = effect.EnemyPacket is not null && observer.Team != effect.AlliedTeam;
                    var effectPacket = effect.LivePacket?.Invoke(enemy) ??
                        (enemy ? effect.EnemyPacket! : effect.Packet);
                    Send(observer, effectPacket);
                    MarkCreated(observer.View, effectPacket);
                    if (effect.EffectNetId != 0)
                    {
                        observer.View.Known.Add(effect.EffectNetId);
                        observer.View.Effects.Add(effect.EffectNetId);
                    }
                }
                break;
            case EffectDestroyReplicationMessage destroy:
                if (observer.View.Effects.Remove(destroy.EffectNetId))
                {
                    var lateMissile = observer.View.LateMissiles.Remove(destroy.EffectNetId);
                    // Cast-created homing missiles already retire on native
                    // impact. Only viewers that missed that cast need the
                    // additional destroy; every view still drops bookkeeping.
                    if (!destroy.LateMissileOnly || lateMissile)
                        Send(observer, destroy.Packet);
                    observer.View.Known.Remove(destroy.EffectNetId);
                }
                break;
        }
    }

    private sealed record TeamVisibilityState(
        IReadOnlyList<VisionProvider> Providers,
        HashSet<uint> Raw,
        HashSet<uint> VisibleBalls);
}
