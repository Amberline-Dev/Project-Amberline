using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Runtime;

namespace Amberline.Network.Sessions;

/// <summary>
/// Match-owned simulation phase driver.  The implementation is supplied by
/// the production gameplay composition and is attached only after the runtime
/// has constructed its world-owned services, which resolves the callback
/// cycle without a nullable or no-op phase.
/// </summary>
public interface IMatchSimulationDriver
{
    void Attach(MatchRuntime runtime);
    void Tick(double dt);
}

/// <summary>
/// Required match-owned integrations for the executable composition root.
/// Every callback is supplied by the gameplay composition; this type has no
/// test fallback or silent no-op implementation.
/// </summary>
public sealed class MatchRuntimeDependencies
{
    public MatchRuntimeDependencies(
        ICombatHooks combatHooks,
        IHeroStatsHooks heroStatsHooks,
        IChampionSpellHooks championHooks,
        IChampionSpellDispatcher championDispatcher,
        IBuffServices buffServices,
        IRandomSource random,
        IReplicationServices replicationServices,
        IVisionEnvironment visionEnvironment,
        IMatchSimulationDriver simulation,
        Func<Team, IEnumerable<VisionProvider>> additionalProviders,
        Func<Team, Position2, bool> revealsStealth,
        Func<Entity, bool> wireReady,
        ISessionShopService shop,
        ISessionCheatService cheats,
        ISessionChatService chat,
        Func<SessionGameplayContext, bool> gameplay)
    {
        CombatHooks = combatHooks ?? throw new ArgumentNullException(nameof(combatHooks));
        HeroStatsHooks = heroStatsHooks ?? throw new ArgumentNullException(nameof(heroStatsHooks));
        ChampionHooks = championHooks ?? throw new ArgumentNullException(nameof(championHooks));
        ChampionDispatcher = championDispatcher ?? throw new ArgumentNullException(nameof(championDispatcher));
        BuffServices = buffServices ?? throw new ArgumentNullException(nameof(buffServices));
        Random = random ?? throw new ArgumentNullException(nameof(random));
        ReplicationServices = replicationServices ?? throw new ArgumentNullException(nameof(replicationServices));
        VisionEnvironment = visionEnvironment ?? throw new ArgumentNullException(nameof(visionEnvironment));
        Simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        AdditionalProviders = additionalProviders ?? throw new ArgumentNullException(nameof(additionalProviders));
        RevealsStealth = revealsStealth ?? throw new ArgumentNullException(nameof(revealsStealth));
        WireReady = wireReady ?? throw new ArgumentNullException(nameof(wireReady));
        Shop = shop ?? throw new ArgumentNullException(nameof(shop));
        Cheats = cheats ?? throw new ArgumentNullException(nameof(cheats));
        Chat = chat ?? throw new ArgumentNullException(nameof(chat));
        Gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
    }

    public ICombatHooks CombatHooks { get; }
    public IHeroStatsHooks HeroStatsHooks { get; }
    public IChampionSpellHooks ChampionHooks { get; }
    public IChampionSpellDispatcher ChampionDispatcher { get; }
    public IBuffServices BuffServices { get; }
    public IRandomSource Random { get; }
    public IReplicationServices ReplicationServices { get; }
    public IVisionEnvironment VisionEnvironment { get; }
    public IMatchSimulationDriver Simulation { get; }
    public Func<Team, IEnumerable<VisionProvider>> AdditionalProviders { get; }
    public Func<Team, Position2, bool> RevealsStealth { get; }
    public Func<Entity, bool> WireReady { get; }
    public ISessionShopService Shop { get; }
    public ISessionCheatService Cheats { get; }
    public ISessionChatService Chat { get; }
    /// <summary>
    /// Typed gameplay command boundary after session ownership and visibility
    /// checks. The composition root supplies the real spell/item dispatcher;
    /// native input never falls through to a silent default.
    /// </summary>
    public Func<SessionGameplayContext, bool> Gameplay { get; }
}

/// <summary>
/// One authoritative match composition. It owns every mutable simulation and
/// replication service and exposes the same objects to session and input code.
/// Cyclic gameplay callbacks are supplied through <see cref="MatchRuntimeDependencies"/>
/// and must be attached before the first tick or received packet.
/// </summary>
public sealed class MatchRuntime : IDisposable
{
    private readonly ProductionSimulationSpeed? _simulationSpeed;
    private readonly FidelityTrace? _fidelityTrace;
    private bool _disposed;
    internal PlaytestDiagnostics? PlaytestDiagnostics { get; }

    internal MatchRuntime(ProductionMatchGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Navigation = graph.Navigation;
        Dependencies = graph.Dependencies;
        GameData = graph.GameData;
        World = graph.World;
        Lifecycle = graph.Lifecycle;
        SyncIds = graph.SyncIds;
        Cipher = graph.Cipher;
        Movement = graph.Movement;
        Delivery = graph.Delivery;
        HeroStats = graph.HeroStats;
        CombatTransport = graph.CombatTransport;
        Combat = graph.Combat;
        Effects = graph.Effects;
        Buffs = graph.Buffs;
        SpellCombat = graph.SpellCombat;
        SpellBuffs = graph.SpellBuffs;
        Spells = graph.Spells;
        Bots = graph.Bots;
        Session = graph.Session;
        SessionMovement = graph.SessionMovement;
        SessionCombat = graph.SessionCombat;
        SessionEffects = graph.SessionEffects;
        Tibbers = graph.Tibbers;
        Clones = graph.Clones;
        Pets = graph.Pets;
        Minions = graph.Minions;
        Waves = graph.Waves;
        _simulationSpeed = graph.SimulationSpeed;
        MovementTrace = graph.MovementTrace;
        _fidelityTrace = graph.FidelityTrace;
        PlaytestDiagnostics = graph.PlaytestDiagnostics;
        Dependencies.Simulation.Attach(this);
    }

    /// <summary>
    /// Builds the maintained in-process gameplay graph.  The server uses this
    /// entry point directly; there is no reflection or user supplied factory
    /// involved in production startup.
    /// </summary>
    public static MatchRuntime CreateProduction(
        NavigationGrid navigation,
        GameData? gameData = null,
        string orderChampion = "Annie",
        string chaosChampion = "Orianna",
        bool solo = false,
        SessionRosterCounts? originalRosterCounts = null,
        double readySettleSeconds = 2.0,
        IRandomSource? random = null,
        LegacyBlowfishCipher? cipher = null,
        bool visibilityBaseline = false,
        bool minionFidelity = false,
        ISessionLogSink? sessionLog = null,
        IReadOnlyList<SessionParticipantDefinition>? roster = null,
        ulong soloPlayerId = 1,
        IMovementDiagnosticSink? movementDiagnostics = null,
        bool botsControlAllSeats = false,
        Func<double>? scriptClock = null,
        int mapId = GameMaps.Normal,
        string? gameMode = null)
    {
        return ProductionMatchComposition.Build(navigation, gameData, orderChampion,
            chaosChampion, solo, originalRosterCounts, readySettleSeconds, random, cipher,
            visibilityBaseline, minionFidelity, sessionLog, roster, soloPlayerId,
            movementDiagnostics, botsControlAllSeats, scriptClock, mapId, gameMode);
    }

    public MatchRuntimeDependencies Dependencies { get; }
    /// <summary>Wall-to-simulation multiplier, updated by the authenticated speed command.</summary>
    public double SimulationSpeed
    {
        get => _simulationSpeed?.Multiplier ?? 1.0;
        set
        {
            if (!ProductionSimulationSpeed.IsValid(value))
                throw new ArgumentOutOfRangeException(nameof(value),
                    "Simulation speed must be finite and between 0.1 and 10.");
            _simulationSpeed?.TrySet(value);
        }
    }

    /// <summary>Consumes the one-shot HUD resynchronization requested by /speed.</summary>
    public bool ConsumeClockResyncPending()
    {
        if (_simulationSpeed is null || !_simulationSpeed.ClockResyncPending)
            return false;
        _simulationSpeed.ClockResyncPending = false;
        return true;
    }
    public GameData GameData { get; }
    public WorldState World { get; }
    /// <summary>Read-only per-match lifecycle diagnostics.</summary>
    public LifecycleMonitor Lifecycle { get; }
    public NavigationGrid Navigation { get; }
    public ReplicationSyncCounter SyncIds { get; }
    public LegacyBlowfishCipher Cipher { get; }
    public MovementSystem Movement { get; }
    public ReplicationDelivery Delivery { get; }
    public HeroStatsService HeroStats { get; }
    public ProtocolCombatTransport CombatTransport { get; }
    public CombatService Combat { get; }
    public ProtocolSpellEffectSink Effects { get; }
    public BuffLifecycle Buffs { get; }
    public ISpellCombatService SpellCombat { get; }
    public ISpellBuffService SpellBuffs { get; }
    public SpellSystem Spells { get; }
    /// <summary>Production bot controller; legacy dependency compositions have none.</summary>
    public BotController? Bots { get; }
    public DuelSession Session { get; }
    public ISessionMovementService SessionMovement { get; }
    public ISessionCombatService SessionCombat { get; }
    public ISessionEffectService SessionEffects { get; }
    /// <summary>Authoritative pet services used by replay and diagnostics.</summary>
    public TibbersAiSystem Tibbers { get; }
    public SharedCloneService Clones { get; }
    /// <summary>Every pet's shipped AI host (Pet.luaobj, UncontrollablePet.luaobj, YorickPHPet.luaobj).</summary>
    public PetAiService Pets { get; }
    /// <summary>Production-only minion AI and wave scheduler; null for injected legacy graphs.</summary>
    public MinionAiSystem? Minions { get; }
    public WaveScheduler? Waves { get; }
    internal CollisionDiagnosticSink? MovementTrace { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Movement.DecisionObserver = null;
        Movement.StepObserver = null;
        Movement.StopEngine();
        if (Minions is not null)
        {
            Minions.DecisionObserver = null;
            Minions.Dispose();
        }
        Delivery.AfterVisibilityFlush = null;
        MovementTrace?.Dispose();
        _fidelityTrace?.Dispose();
    }

    /// <summary>Runs one source-ordered simulation step through the real driver.</summary>
    public void Tick(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        if (Session.Paused)
        {
            // Loading/reconnect and the pause allowance follow wall time.
            // Authoritative gameplay and its accumulated work stay frozen.
            if (World.Clock.IsVirtual) World.Clock.Advance(dt);
            Delivery.Flush();
            return;
        }
        const double maximumStep = 1.0 / 30.0;
        const double epsilon = 1e-12;
        while (dt > maximumStep + epsilon)
        {
            RunSimulationStep(maximumStep);
            dt -= maximumStep;
        }
        if (dt > epsilon)
            RunSimulationStep(dt);
    }

    private void RunSimulationStep(double dt)
    {
        var startedBefore = Session.Started;
        Dependencies.Simulation.Tick(dt);
        if (!startedBefore && !Session.Started)
            return;
        // Visibility is a source simulation phase. Flush it before the
        // lifecycle monitor so each fixed/remaining step gets one observer
        // refresh, one fidelity publication, and one lifecycle scan. The
        // network shell only flushes ENet after the complete call.
        Delivery.Flush();
        if (Lifecycle.ShouldScan)
            Lifecycle.Check(Combat.CurrentTime);
    }
}

/// <summary>Concrete session movement adapter over the authoritative pathfinder.</summary>
public sealed class MovementSessionAdapter : ISessionMovementService
{
    private readonly MovementSystem _movement;

    public MovementSessionAdapter(MovementSystem movement) =>
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));

    public IReadOnlyList<Position2> SetMoveOrder(Entity unit, Position2 destination)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _movement.SetMoveOrder(unit.NetId, destination);
    }

    public IReadOnlyList<Position2> InstallStop(Entity unit) => _movement.InstallStop(unit);
}

/// <summary>Concrete session combat adapter; protection packets enter Delivery.</summary>
public sealed class CombatSessionAdapter : ISessionCombatService
{
    private readonly CombatService _combat;
    private readonly WorldState _world;

    public CombatSessionAdapter(CombatService combat, WorldState world)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public bool Surrender(Team team) => _combat.Surrender(team);

    public void NotifyHeroConnection(Entity hero, bool connected) => _combat.NotifyHeroConnection(hero, connected);

    public void CancelPendingActions(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _combat.CancelPendingWindups(unit.NetId);
        unit.PendingCast = null;
        unit.AttackTarget = null;
    }

    public IReadOnlyList<PlaintextPacket> EngageProtection(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!ReferenceEquals(world, _world))
            throw new ArgumentException("Combat adapter belongs to another world.", nameof(world));
        _combat.EngageProtection();
        // ProtocolCombatTransport queues the typed protection updates through
        // ReplicationDelivery. A session caller must flush that delivery after
        // running schedulers; returning a duplicate burst would consume IDs.
        return Array.Empty<PlaintextPacket>();
    }

}

/// <summary>
/// Protocol-only session lifecycle adapter used while the match combat graph
/// is being assembled. Protection state is still authoritative world state;
/// its typed packets enter the same ReplicationDelivery queue and therefore
/// share the match sync counter and observer visibility rules.
/// </summary>
public sealed class ProtocolProtectionSessionAdapter : ISessionCombatService
{
    private readonly WorldState _world;
    private readonly ReplicationDelivery _delivery;
    private readonly ReplicationSyncCounter _syncIds;

    public ProtocolProtectionSessionAdapter(
        WorldState world,
        ReplicationDelivery delivery,
        ReplicationSyncCounter syncIds)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _syncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
    }

    public bool Surrender(Team team) => false;

    public void CancelPendingActions(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        unit.AttackTarget = null;
        unit.PendingCast = null;
        unit.Waypoints.Clear();
    }

    public IReadOnlyList<PlaintextPacket> EngageProtection(WorldState world)
    {
        if (!ReferenceEquals(world, _world))
            throw new ArgumentException("Protection adapter belongs to another world.", nameof(world));

        ProtectionRules.Engage(_world, entity =>
        {
            PlaintextPacket packet;
            // Batch 457: a point turret the clients hold as a minion gets no turret flags block (it falls to the return below).
            if (entity.IsSceneTurret)
            {
                var targetable = entity.Alive && !TurretRules.IsShrineTurret(entity);
                packet = CombatPackets.TurretProtectionFlags(
                    entity.NetId,
                    !targetable ? CombatPackets.SFlagNontargetableAll :
                    entity.Protected ? CombatPackets.SFlagNontargetableEnemy :
                        CombatPackets.SFlagTargetableToAll,
                    _syncIds.Next(entity.NetId),
                    entity.Protected ? 1u : 0u,
                    targetable ? 1u : 0u);
            }
            else if (entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus)
            {
                packet = CombatPackets.BuildingStats(
                    entity.NetId,
                    ProtocolEncoding.Float32(entity.Health, nameof(entity.Health)),
                    entity.Protected ? CombatPackets.SFlagNontargetableEnemy :
                        CombatPackets.SFlagTargetableToAll,
                    entity.Protected ? 1u : 0u,
                    1u,
                    _syncIds.Next(entity.NetId));
            }
            else
            {
                return;
            }

            _delivery.Enqueue(packet);
        });
        return Array.Empty<PlaintextPacket>();
    }
}

/// <summary>Stateless protocol builders used by session disconnect handling.</summary>
public sealed class EffectSessionAdapter : ISessionEffectService
{
    // The detached unit is a hero, and a hero always gets its stop (CombatPackets.StopAttack).
    public PlaintextPacket BuildStopAttack(Entity unit) =>
        CombatPackets.StopAttack(unit) ?? throw new ArgumentException("A detach stops a hero.", nameof(unit));

    public PlaintextPacket BuildWaypoints(
        Entity unit, IReadOnlyList<Position2> points, uint syncId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(points);
        return VisionPackets.WaypointList(unit.NetId, points, syncId);
    }
}

internal sealed class CombatSpellAdapter : ISpellCombatService
{
    private readonly CombatService _combat;

    public CombatSpellAdapter(CombatService combat) =>
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));

    public bool GameOver => _combat.GameOver;

    public bool IsTargetable(Entity target, Entity? observer = null) =>
        _combat.IsTargetable(target, observer);

    public double HealingMultiplier(Entity target) =>
        _combat.HealingReceivedMultiplier(target);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind, double vampScale = 1.0) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            false, false, null, null);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind, double vampScale,
        bool isReflect, bool sourceIsPeriodic, uint? spellOwnerNetId) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale,
            isReflect, sourceIsPeriodic, spellOwnerNetId, null);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind, double vampScale,
        bool isReflect, bool sourceIsPeriodic, uint? spellOwnerNetId,
        DeathRecapOrigin? recapOrigin) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale, isReflect,
            sourceIsPeriodic, spellOwnerNetId, recapOrigin,
            ignoreDamageIncreaseMods: false, ignoreDamageCrit: false);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind, double vampScale,
        bool isReflect, bool sourceIsPeriodic, uint? spellOwnerNetId,
        DeathRecapOrigin? recapOrigin, bool ignoreDamageIncreaseMods, bool ignoreDamageCrit) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale, isReflect, sourceIsPeriodic, spellOwnerNetId,
            recapOrigin, ignoreDamageIncreaseMods, ignoreDamageCrit, callForHelpSource: null);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind, double vampScale,
        bool isReflect, bool sourceIsPeriodic, uint? spellOwnerNetId,
        DeathRecapOrigin? recapOrigin, bool ignoreDamageIncreaseMods, bool ignoreDamageCrit, Entity? callForHelpSource) =>
        ApplyDamage(source, target, amount, type, sourceKind, vampScale, isReflect, sourceIsPeriodic, spellOwnerNetId,
            recapOrigin, ignoreDamageIncreaseMods, ignoreDamageCrit, callForHelpSource, critical: false);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind, double vampScale,
        bool isReflect, bool sourceIsPeriodic, uint? spellOwnerNetId,
        DeathRecapOrigin? recapOrigin, bool ignoreDamageIncreaseMods, bool ignoreDamageCrit, Entity? callForHelpSource, bool critical)
    {
        return _combat.ApplyHit(
            source.NetId, target.NetId, amount, type, sourceKind,
            isReflect, sourceIsPeriodic, spellOwnerNetId,
            // A scripted hit's result type (batch 301): DAMAGEFLOW_CRITICAL 3 for a critical HitResult, else the ordinary 4, both with the sound bit.
            typeByte: critical ? (byte)0x83 : (byte)0x84,
            vampScale: vampScale, callForHelpSource: callForHelpSource,
            ignoreDamageIncreaseMods: ignoreDamageIncreaseMods, ignoreDamageCrit: ignoreDamageCrit,
            // DamageSourceKind.Basic is also used by Ezreal Q and other
            // proc rules. Until those producers carry typed native origin
            // metadata, spell-service damage must not inherit auto-attack
            // recap classification from that gameplay kind.
            recapOrigin: recapOrigin ?? DeathRecapOrigin.Unclassified);
    }

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind,
        DeathRecapOrigin recapOrigin) =>
        ApplyDamage(source, target, amount, type, sourceKind, 1.0,
            false, false, null, recapOrigin);

    public bool ApplyDamage(Entity source, Entity target, double amount,
        DamageType type, DamageSourceKind? sourceKind,
        DeathRecapOrigin recapOrigin, bool bypassSpellShield) =>
        _combat.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
            recapOrigin: recapOrigin, bypassSpellShield: bypassSpellShield);

    public bool ConsumeSpellShield(Entity source, Entity target, bool force) =>
        _combat.ConsumeSpellShield(source, target, DamageSourceKind.Spell, force);

    public bool AwardHeroProgress(Entity hero, double experience) =>
        _combat.AwardHeroProgress(hero, experience: experience);

    public bool AwardHeroGold(Entity hero, double gold, Entity? source) => _combat.AwardHeroGold(hero, gold, source);

    public void RecordSupportAssist(Entity source, Entity recipient, double durationSeconds = 10.0) =>
        _combat.RecordSupportAssist(source, recipient, durationSeconds);

    public void InterruptBasicAttack(Entity attacker) => _combat.InterruptBasicAttack(attacker);
    public void RevealAttack(Entity attacker, Entity target) => _combat.RevealAttack(attacker, target);

    public void CancelPendingWindups(uint sourceNetId) => _combat.CancelPendingWindups(sourceNetId);
    public int CancelPendingTargetMissiles(uint targetNetId) => _combat.CancelPendingTargetMissiles(targetNetId);
    public void Kill(Entity unit, uint killerNetId) => _combat.Kill(unit, killerNetId);

    public bool RespawnImmediately(Entity unit) => _combat.RespawnImmediately(unit);
}

internal sealed class BuffLifecycleAdapter : ISpellBuffService
{
    private readonly BuffLifecycle _lifecycle;

    public BuffLifecycleAdapter(BuffLifecycle lifecycle) =>
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    public BuffState? Add(Entity unit, string name, BuffType type, double duration,
        uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true) =>
        _lifecycle.AddBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration);

    public bool Remove(Entity unit, int slot) => _lifecycle.RemoveBuff(unit, slot);
    public bool RemoveForRenewal(Entity unit, int slot, object frame) => _lifecycle.RemoveBuffForRenewal(unit, slot, frame);
    public void FlushRenewalRemovals(object frame) => _lifecycle.FlushRenewalRemovals(frame);
    public bool Has(Entity unit, string name) => _lifecycle.HasBuff(unit, name);
    public void RefreshMovementSpeed(Entity unit, bool push = true) =>
        _lifecycle.RefreshMovementSpeed(unit, push);
    public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) =>
        _lifecycle.ScriptStatusGained(unit, wasHardControlled, wasRooted);
    public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) =>
        _lifecycle.ScriptStatusLost(unit, couldMove, couldAttack);
    public BuffState? AddStacking(Entity unit, string name, BuffType type,
        double duration, uint casterNetId, int maxStacks, BuffPayload perStackPayload,
        bool replaceExistingPayload = false, bool canMitigateDuration = true) =>
        _lifecycle.AddStackingBuff(unit, name, type, duration, casterNetId, maxStacks,
            perStackPayload, replaceExistingPayload, canMitigateDuration: canMitigateDuration);
    public BuffState? AddOverlapping(Entity unit, string name, BuffType type, double duration,
        uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true,
        int? maxStack = null) =>
        _lifecycle.AddOverlappingBuff(unit, name, type, duration, casterNetId, payload,
            canMitigateDuration, maxStack, failIfFull: false);
    public BuffState? AddContinuing(Entity unit, string name, BuffType type, double duration, uint casterNetId,
        int maxStacks, BuffPayload perStackPayload, bool canMitigateDuration = true) =>
        _lifecycle.AddStackingBuff(unit, name, type, duration, casterNetId, maxStacks, perStackPayload,
            continueTimer: true, canMitigateDuration: canMitigateDuration);
    public void Tick(Entity unit, double dt) => _lifecycle.TickBuffs(unit, dt);
    public bool ReplaceScriptPayload(Entity unit, BuffState buff, BuffPayload perStackPayload) =>
        _lifecycle.ReplaceScriptPayload(unit, buff, perStackPayload);
    public bool CanAdd(Entity unit, string name, Entity? caster, BuffType type, int freeableSlots = 0) =>
        _lifecycle.CanAdd(unit, name, caster, type, freeableSlots);
    public bool CanAddOverlapping(Entity unit, int applications = 1, params string[] names) =>
        _lifecycle.CanAddOverlapping(unit, applications, names);
    public bool RefusesAdd(Entity unit, BuffType type, uint casterNetId, int? maxStack = null) =>
        _lifecycle.RefusesAdd(unit, type, casterNetId, maxStack);
    public bool DropScriptStack(Entity unit, BuffState buff, BuffPayload perStackPayload, int count = 1) =>
        _lifecycle.DropScriptStack(unit, buff, perStackPayload, count);
}
