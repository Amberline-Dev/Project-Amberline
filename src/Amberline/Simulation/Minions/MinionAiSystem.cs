using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Minions;

internal readonly record struct MinionFanClaimObservation(
    uint MinionNetId,
    uint TargetNetId,
    double Angle,
    double Radius,
    Position2 Origin,
    double HomeAngle,
    int RivalCount,
    double Seat,
    bool Fresh,
    bool Reclaim,
    bool PhysicalOverlapAtEntry,
    bool Successful);

/// <summary>
/// A lane minion's AI: the shipped <c>Minion.luaobj</c> and <c>minionLib.luaobj</c> decide what it
/// does (its states, its timers, its target choice) on <see cref="Host"/>, and this service keeps
/// the movement those decisions name — the route follower, the contact solver, the waypoint wire
/// and the client-path rules — plus the native answers the script asks for
/// (<see cref="MinionAiNatives"/>). Movement and combat remain separate owners: this service
/// emits typed movement/wire decisions, while <see cref="IMinionCombatService"/> owns targetability
/// and hit state.
/// </summary>
public sealed partial class MinionAiSystem : IDisposable
{
    private readonly WorldState _world;
    private readonly NavigationGrid _navigation;
    private readonly MovementSystem _movement;
    private readonly IMinionCombatService _combat;
    private readonly IMinionPacketSink _packets;
    private readonly IMinionVisionService _vision;
    private readonly Func<Team, Entity, bool>? _canSee;
    private readonly IMinionTraceSink? _trace;
    // The movement half's own 250 ms re-plan gate (TargetScanReady). The target scan is the
    // script's own timer (minionLib's DELAY_FIND_ENEMIES); this only bounds how often the lane
    // planner is asked again.
    private readonly Dictionary<uint, double> _nextTargetScan = new();
    private readonly Dictionary<uint, Dictionary<uint, double>> _ignoredTargets = new();
    // The live fear of each minion, for GetFearLeashPoint and the wander's short duration.
    private readonly Dictionary<uint, BuffState> _fears = new();
    private readonly IRandomSource _random;
    private readonly Dictionary<uint, (uint TargetNetId, double ReadyAt)> _nextSiegeRetry = new();
    // The retained order: its destination (the repeat-order deadband) and the target position it was
    // taken against (the re-issue deadband of a chase).
    private readonly Dictionary<uint, (Position2 Point, Position2 TargetAt)> _lastStop = new();
    private readonly Dictionary<uint, (uint TargetNetId, double Angle)> _fanClaims = new();
    // The anchor list each minion's client was last given, to notice a route installed outside the AI.
    private readonly Dictionary<uint, List<Position2>> _anchorSeen = new();
    // Minions whose displacement status holds their AI back: the stun a script puts on a minion with its forced move (Rocket Grab,
    // Condemn, Monsoon, a shockwave, Power Fist). The hold ends with the status, not with the dash (see HeldByForcedMove).
    private readonly HashSet<uint> _forcedHold = new();
    private readonly List<(Entity Minion, IReadOnlyList<Entity> Colliders)> _movementEvents = new();
    // The stuck-site repath requests of the last movement steps (see OnFixedStepCompleted) and each minion's attempts counter as
    // that step left it.
    private readonly List<Entity> _repathRequests = new();
    private readonly Dictionary<uint, int> _repathAttemptsSeen = new();
    private TargetCostConstants? _targetCost;
    private SpatialIndex<Entity>? _tickIndex;
    private double _elapsed;
    private bool _disposed;

    public MinionAiSystem(
        WorldState world,
        NavigationGrid navigation,
        MovementSystem movement,
        IMinionCombatService combat,
        IMinionPacketSink packets,
        IMinionVisionService vision,
        IMinionTraceSink? trace = null,
        Func<Team, Entity, bool>? canSee = null,
        IRandomSource? random = null,
        TargetCostConstants? targetCost = null)
    {
        _targetCost = targetCost;
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _packets = packets ?? throw new ArgumentNullException(nameof(packets));
        _vision = vision ?? throw new ArgumentNullException(nameof(vision));
        _trace = trace;
        _canSee = canSee;
        _random = random ?? new SystemRandomSource();
        if (!ReferenceEquals(_movement.World, _world))
            throw new ArgumentException("MovementSystem must use the same world.", nameof(movement));
        _movement.FixedStepCompleted += OnFixedStepCompleted;
        Engine = new MinionLuaAiEngine(this);
        Host = new LuaAiService(_world, Engine);
    }

    /// <summary>
    /// The world side of a lane minion's shipped AI, answering what <c>Minion.luaobj</c> and
    /// <c>minionLib.luaobj</c> ask (<see cref="MinionLuaAiEngine"/>).
    /// </summary>
    public MinionLuaAiEngine Engine { get; }

    /// <summary>
    /// The Lua AI host a lane minion's shipped script runs on. Every lane minion gets its own
    /// <c>Minion.luaobj</c> instance (its own globals and timers) on this host, the same way a
    /// turret, a camp monster and a pet each run theirs.
    /// </summary>
    public LuaAiService Host { get; }

    /// <summary>
    /// The host, after meeting this minion's AI object (<see cref="LuaAiService.InstanceFor"/>).
    /// Retail builds that object when the unit is created, so minionLib 0.0's <c>OnInit</c> ran
    /// before the engine wrote any state and whatever the minion already carries is newer than the
    /// birth. This host builds it at the minion's first AI step instead, so a late birth must not
    /// let <c>OnInit</c>'s <c>SetState(AI_IDLE)</c> undo that decision: the timers are still armed
    /// (that is the birth's job) and only the state is kept. The minion projection is a plain field
    /// write (<see cref="MinionLuaAiEngine.SetAiState"/>), so keeping it costs nothing; the pet and
    /// jungle projections route through their own state machines and must not be written back.
    /// An explicit <see cref="OnInit"/> is the engine's own re-init and still resets.
    /// </summary>
    private LuaAiService AiFor(Entity minion)
    {
        if (minion.MinionType is not null)
        {
            var carried = minion.MinionState;
            Host.InstanceFor(minion);
            minion.MinionState = carried;
        }
        return Host;
    }

    public WorldState World => _world;
    /// <summary>The shipped Map1 ai_Target* cost constants (the Map1 file unless the composition passed its own).</summary>
    internal TargetCostConstants TargetCost => _targetCost ??= GameData.Default.LevelConstants.TargetCost;
    public NavigationGrid Navigation => _navigation;
    public double ElapsedSeconds => _elapsed;

    /// <summary>
    /// Receives the lethal target edge before CombatService clears retained
    /// attacker IDs. The script's <c>OnTargetLost</c> is the replacement decision (minionLib 0.1);
    /// returning here preserves the lane suffix and prevents a minion from remaining in its planted
    /// engage state until its next scan.
    /// </summary>
    public void NotifyTargetLost(uint targetNetId)
    {
        if (targetNetId == 0) return;
        foreach (var minion in _world.Entities.Values
                     .Where(entity => IsLiveMinion(entity) &&
                                      entity.AttackTarget == targetNetId &&
                                      entity.MinionState is MinionAiState.Engage or MinionAiState.Taunted)
                     .ToArray())
        {
            // A minion held by its displacement status decides again when the status ends (HeldByForcedMove).
            if (HeldByForcedMove(minion)) continue;
            AiFor(minion).OnTargetLost(minion, _world.Get(targetNetId));
        }
    }

    /// <summary>
    /// A minion whose displacement status is on is not the AI's to order. The status a displacement puts on the unit (the stun of
    /// <c>VayneCondemnMissile</c> and <c>PowerFistSlow</c>, the SetCanMove(false) of <c>RocketGrab2</c>, <c>MoveAway</c> and
    /// <c>MoveAwayCollision</c>) holds it, and the shipped minionLib <c>OnCanMove</c> (proto 0.7: return if AI_HALTED, else
    /// NetSetState(AI_IDLE) and FindTargetOrMove) runs the moment that status ends (<see cref="ReleaseFromForcedMove"/>). The
    /// status ends before the move does, for Condemn (0.25 s of a 0.29 s move) and Power Fist (0.5 s of 0.89 s), so the order goes
    /// out while the dash still runs. A stock client replaces a dash with the first 0xC1 it receives, so that order can snap the
    /// unit toward the landing (ForcedMoveAiChecks and PacketStreamChecker rule 7 record it). The owner accepted the snaps on
    /// 25 October 2026: follow the script, with no hold for the rest of the dash. The unit's own Fear walk is the AI's movement
    /// and never holds.
    /// </summary>
    private bool HeldByForcedMove(Entity minion)
    {
        // The hold is the control status ReleaseFromForcedMove waits on (ControlOwnsMovement: actions, snare, fear or the movement
        // flag), so the release happens exactly when that status clears.
        if (minion.ForcedMove is { Completed: false } move && move.Effect != "Fear" && ControlOwnsMovement(minion))
        {
            _forcedHold.Add(minion.NetId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A control buff or a forced move owns the minion's public order: retail minionLib <c>OnCanMove</c> has a minion still
    /// stunned or snared decide when that control ends (<see cref="ReleaseFromForcedMove"/>). The script's own scans keep
    /// running under the control, but they must publish no route of their own: the Teleport freeze and the foreign knockback
    /// that outlasts it leave the unit standing where the control put it.
    /// </summary>
    private static bool ControlOwnsMovement(Entity entity) =>
        // Batch 211: the displacement's status owns the order (IsActionDisabled); its forced move alone does not, so the script's
        // OnCanMove route goes out when the status ends, inside the dash (the owner's follow-the-script decision, 25 October 2026).
        IsActionDisabled(entity) ||
        entity.AnyBuffOrStatus(buff => IsFear(buff) || buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    /// <summary>
    /// The displacement status is over: the script's order comes from where the minion stands, which may still be in its dash.
    /// Retail minionLib <c>OnCanMove</c>: NetSetState(AI_IDLE) then FindTargetOrMove. A minion still stunned or snared decides when that control ends, and a
    /// feared one has its own timer.
    /// The hand-written machine this replaced branched here to <c>OnTauntBegin</c> when a taunt target survived the
    /// displacement. minionLib's <c>OnCanMove</c> (DATA/Scripts, proto 0.7) has no such branch, so the script wins; the taunt takes its target again
    /// from <c>TimerFindEnemies</c>' TAUNTED branch.
    /// </summary>
    private void ReleaseFromForcedMove(Entity minion)
    {
        if (minion.MinionState == MinionAiState.Halted || IsActionDisabled(minion) ||
            minion.AnyBuffOrStatus(buff => IsFear(buff) || buff.Type == BuffType.Snare || buff.Payload.MovementDisabled))
            return;
        AiFor(minion).OnCanMove(minion);
    }

    /// <summary>
    /// Source regression seam for the emulator stale-plant guard. Production
    /// keeps the half-second policy gate; tests may run the same production
    /// graph with the guard disabled or removed to prove both sides of the
    /// packet-storm fix without changing gameplay defaults.
    /// </summary>
    internal double ReplantMinimumGapSeconds { get; set; } = 0.5;

    /// <summary>
    /// Test-only observation seam for the script's target scan. It fires where <c>Minion.luaobj</c>
    /// and <c>minionLib.luaobj</c> actually ask — the <c>FindTargetInAcR</c> native — so it measures
    /// the shipped cadence (<c>DELAY_FIND_ENEMIES</c>, 0.25 s) rather than a hand-kept one.
    /// Production leaves this null, so target selection remains behaviorally unchanged and no
    /// diagnostic work enters the hot path.
    /// </summary>
    internal Action<Entity>? TargetDecisionObserver { get; set; }

    /// <summary>
    /// Test-only observation seam for an actual return-to-lane decision.  The
    /// callback is invoked at the source branch, before the target and route
    /// fields are cleared; production leaves it null and pays no work here.
    /// </summary>
    internal Action<Entity, string>? ReturnDecisionObserver { get; set; }

    /// <summary>
    /// Test-only observation seam for the source acquire edge.  It runs before
    /// stop packets or target state are changed; production leaves it null.
    /// </summary>
    internal Action<Entity, Entity, bool>? AcquireDecisionObserver { get; set; }

    /// <summary>
    /// Test-only observation seam for a real movement stop.  Arguments retain
    /// the pre-clear order flags and displacement from the last anchor.
    /// </summary>
    internal Action<Entity, bool, bool, double?>? ClearMovementObserver { get; set; }

    /// <summary>
    /// Optional source-timing probe for the private fan-claim map. Production
    /// leaves this unset; the fidelity check observes claim creation and
    /// overlap reclaims at the point where the AI actually commits them.
    /// </summary>
    internal Action<MinionFanClaimObservation>? FanClaimObserver { get; set; }

    /// <summary>
    /// Optional bounded movement-decision observer. Production diagnostics may
    /// subscribe, but failures must never alter minion movement or packets.
    /// </summary>
    internal Action<MovementDecisionObservation>? DecisionObserver { get; set; }

    /// <summary>
    /// Test-only source counterfactual: the lane workload can disable just
    /// the structure-siege branch while retaining the normal march, scan, and
    /// movement phases.  Production always leaves this enabled.
    /// </summary>
    internal bool SiegeRoutingEnabled { get; set; } = true;

    /// <summary>
    /// Runs the source engagement decision for a seeded scenario without
    /// advancing the world or reordering the co-attacker snapshot. This seam
    /// is used by movement regressions that exercise the original
    /// <c>_engage_move</c> helper directly.
    /// </summary>
    internal void RunEngagementProbe(Entity minion, Entity target)
    {
        ArgumentNullException.ThrowIfNull(minion);
        ArgumentNullException.ThrowIfNull(target);
        EngageMove(minion, target);
    }

    /// <summary>Runs one AI decision frame. Call after movement committed.</summary>
    public void Tick(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        _elapsed += dt;
        // Orders held for a forced move go out when its wire end has passed (see ProductionMinionPacketSink.HoldOrder).
        _packets.FlushHeldOrders();

        _tickIndex = _world.IndexUnits();
        var minions = _world.Entities.Values
            .Where(IsLiveMinion)
            .ToArray();
        var active = minions.Select(entity => entity.NetId).ToHashSet();
        Prune(_nextTargetScan, active);
        Prune(_ignoredTargets, active);
        Prune(_fears, active);
        Prune(_nextSiegeRetry, active);
        Prune(_lastStop, active);
        Prune(_anchorSeen, active);
        _forcedHold.RemoveWhere(id => !active.Contains(id));
        Prune(_repathAttemptsSeen, active);
        foreach (var netId in _fanClaims.Keys.Where(netId => !active.Contains(netId)).ToArray())
            _fanClaims.Remove(netId);

        foreach (var (minion, colliders) in _movementEvents)
            if (IsLiveMinion(minion) && ReferenceEquals(_world.Get(minion.NetId), minion) &&
                !CollisionAiDisabled(minion))
                ConsumeMovementEvent(minion, colliders);
        _movementEvents.Clear();
        // The stuck site runs the collision hook before it repaths, so the script's own answer (a new target, a new route) comes first.
        foreach (var minion in _repathRequests)
            if (IsLiveMinion(minion) && ReferenceEquals(_world.Get(minion.NetId), minion) &&
                !CollisionAiDisabled(minion) && !ControlOwnsMovement(minion))
                RepathStuck(minion);
        _repathRequests.Clear();

        foreach (var minion in minions)
        {
            // Before any decision: a minion whose displacement status holds it is not ordered (see HeldByForcedMove); a call for
            // help stays pending (CombatAwareness drops it after a second) and the end of the status decides again.
            if (HeldByForcedMove(minion)) continue;
            if (_forcedHold.Remove(minion.NetId)) ReleaseFromForcedMove(minion);
            ConsumeHelp(minion);
            if (minion.MinionState == MinionAiState.Halted) continue;
            // The script's own frame: its target-lost test, then its due timers (TimerFindEnemies,
            // TimerMoveForward, TimerAntiKite, TimerFeared). It decides what this minion does.
            AiFor(minion).StepUnit(minion, _elapsed, dt);
            // What the script named is the engine's to maintain: the plant, the overlap seat and
            // the chase route of a closing engage. Retail has no congestion wait or detour (batch 225).
            if (minion.Buffs.Any(IsFear) || minion.ForcedMove is { Completed: false } ||
                IsActionDisabled(minion))
                continue;
            if (minion.MinionState is MinionAiState.Engage or MinionAiState.Taunted &&
                minion.AttackTarget is uint retained && _world.Get(retained) is { } target)
                EngageMove(minion, target, refresh: TargetScanReady(minion));
        }
        try { _trace?.OnMinionAiTick(); }
        catch { /* optional diagnostics never affect minion decisions */ }
        _tickIndex = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _movement.FixedStepCompleted -= OnFixedStepCompleted;
        _anchorSeen.Clear();
        _movementEvents.Clear();
        _repathRequests.Clear();
        DecisionObserver = null;
    }

    // Each completed NPC step queues its real body contacts for the next AI frame (the collision callbacks).
    // It also dates an anchor installed outside the AI, such as the singleton a Fear leaves when it ends:
    // that is a route the client was just given, so it restarts the anchor age (LastAnchorTime) that gates
    // stale replants, exactly as the AI's own anchors do.
    private void OnFixedStepCompleted(double dt,
        IReadOnlyList<(uint NetId, double Speed, bool PathFollowerAllowed,
            IReadOnlyList<Entity> Colliders)> steps)
    {
        if (_disposed || !double.IsFinite(dt) || dt <= 0.0) return;
        foreach (var (netId, speed, allowed, colliders) in steps)
        {
            if (!double.IsFinite(speed) || speed <= 0.0 ||
                _world.Get(netId) is not { } minion || !IsLiveMinion(minion))
                continue;
            if (allowed && colliders.Count > 0 && !CollisionAiDisabled(minion))
                _movementEvents.Add((minion, colliders));
            // The stuck site of the actor update: past the REPATH backoff (or on a frame refused for no progress) it
            // calls the owner's collision hook with the collider and, when the hook answers 0 or the frame was refused, raises the
            // attempts counter (RepathAttempts) and asks its pathing for a new path (the call through Actor_Pimpl that the client
            // build compiles to nothing; the server's RefreshPath). The solver raises the counter exactly there, so a rise is that request.
            var attempts = minion.RepathAttempts;
            if (allowed && attempts > _repathAttemptsSeen.GetValueOrDefault(netId) &&
                (minion.Waypoints.Count > 0 || minion.Target is not null))
                _repathRequests.Add(minion);
            _repathAttemptsSeen[netId] = attempts;
            if (minion.AnchorPath.Count == 0) continue;
            if (_anchorSeen.TryGetValue(netId, out var seen) && !ReferenceEquals(seen, minion.AnchorPath))
            {
                minion.LastAnchorTime = _elapsed;
                minion.SeparationAccumulation = Position2.Zero;
            }
            _anchorSeen[netId] = minion.AnchorPath;
        }
    }

    private static bool CollisionAiDisabled(Entity minion) =>
        minion.MinionState is MinionAiState.Halted or MinionAiState.Taunted or MinionAiState.Feared ||
        IsActionDisabled(minion) || minion.ForcedMove is not null ||
        minion.Buffs.Any(buff => buff.Type is BuffType.Fear or BuffType.Taunt ||
            buff.Name == "Fear" || buff.Payload.FearOrigin is not null);

    private void ConsumeMovementEvent(Entity minion, IReadOnlyList<Entity> colliders)
    {
        // Reconstruction: native dispatch/order is unrecovered. A completed
        // step uses its first eligible enemy body contact in movement order,
        // otherwise OnCollisionOther.
        var enemy = colliders.FirstOrDefault(other =>
            ReferenceEquals(_world.Get(other.NetId), other) && other.Alive &&
            // minionLib OnCollisionEnemy (proto 0.10) closes on the target unless the minion is HALTED, TAUNTED or FEARED, and
            // CollisionAiDisabled gates exactly those three states. Reconstruction: the native's own gate terms beyond them
            // (IsActionDisabled, ForcedMove, the Fear and Taunt buffs) are not recovered; the shared attackability/sight gates and
            // ordinary neutral self-defense acquisition are kept to avoid unavailable or idle camp targets.
            other.Team != Team.Neutral && WorldState.IsEnemy(minion, other) &&
            _combat.IsTargetable(other, minion) && HasBrushVision(minion.Team, other));
        if (enemy is not null)
        {
            // minionLib 0.10 OnCollisionEnemy(other): SetStateAndCloseToTarget(AI_ATTACKMOVE_ATTACKING, other).
            AiFor(minion).OnCollisionEnemy(minion, enemy);
            return;
        }
        // minionLib 0.11 OnCollisionOther(other) scans itself and closes only on what it finds.
        if (colliders.FirstOrDefault(other =>
                ReferenceEquals(_world.Get(other.NetId), other) && other.Alive) is { } other)
            AiFor(minion).OnCollisionOther(minion, other);
    }

    private void ConsumeHelp(Entity minion)
    {
        if (minion.HelpTarget is not uint helpId) return;
        // minionLib.OnCallForHelp is an event, not a retained order.
        // Consume rejected events too; later sight must not revive one.
        minion.HelpTarget = null;
        minion.HelpRemaining = 0.0;
        if (minion.MinionState is MinionAiState.March or MinionAiState.Engage &&
            !minion.Buffs.Any(buff => IsFear(buff) || buff.Type == BuffType.Taunt) &&
            _world.Get(helpId) is { Alive: true } aggressor &&
            _combat.IsTargetable(aggressor, minion) && HasBrushVision(minion.Team, aggressor) &&
            CanAcceptHelp(_world, minion, aggressor, minion.HelpPriority))
        {
            if (minion.MinionState == MinionAiState.March && minion.LaneWaypoints.Count == 0)
                minion.LaneWaypoints = minion.Waypoints.ToList();
            StickHelp(minion, aggressor);
            // minionLib 0.3 OnCallForHelp(self, aggressor): SetStateAndCloseToTarget(AI_ATTACKMOVE_ATTACKING,
            // aggressor) and ResetAndStartTimer('TimerAntiKite'). Admission is the engine's; the decision is the script's.
            AiFor(minion).OnCallForHelp(minion, aggressor);
        }
    }

    /// <summary>Constants.var <c>cfh_Stick</c> (1.5 s): an accepted call keeps its priority against weaker ones (<see cref="CanAcceptHelp"/>).</summary>
    internal static void StickHelp(Entity unit, Entity aggressor)
    {
        if (unit.HelpStickTarget == aggressor.NetId && unit.HelpStickRemaining > 0.0 &&
            unit.HelpStickPriority < unit.HelpPriority) return;
        unit.HelpStickTarget = aggressor.NetId;
        unit.HelpStickPriority = unit.HelpPriority;
        unit.HelpStickRemaining = 1.5;
    }

    /// <summary>
    /// minionLib 0.0 <c>OnInit</c>: SetState(AI_IDLE), the four timers, and both
    /// <c>TimerAntiKite</c> and <c>TimerFeared</c> stopped. The host runs it once, when it creates
    /// the instance; re-running it is how a halted minion is put back in service, and the body is
    /// idempotent so the second call on a fresh instance is the same state.
    /// </summary>
    public void OnInit(Entity minion)
    {
        if (!IsLiveMinion(minion)) return;
        _fears.Remove(minion.NetId);
        AiFor(minion).InstanceFor(minion)?.Call("OnInit");
    }

    /// <summary>
    /// Batch 499: re-sends a minion's stat block after its stats changed past its spawn (an Odin wave unit's encounter mutators are applied after
    /// the spawn path sent its first block; OdinWaves.Birth).
    /// </summary>
    internal void SendStats(Entity minion)
    {
        if (_packets.IsReferenceReady(minion)) _packets.SendStats(minion);
    }

    /// <summary>minionLib 0.13 <c>HaltAI</c>: stop every timer and the auto-attack, enter AI_HALTED.</summary>
    public void HaltAI(Entity minion)
    {
        if (!IsLiveMinion(minion)) return;
        _fears.Remove(minion.NetId);
        AiFor(minion).HaltAI(minion);
    }

    /// <summary>
    /// Minion.lua 0.3 <c>FindTargetOrMove</c>: scan, and take a target or walk forward. The named
    /// entry point the checks and the event callers ask for; the decision itself is the script's.
    /// </summary>
    internal void FindTargetOrMove(Entity minion) => AiFor(minion).InstanceFor(minion)?.Call("FindTargetOrMove");

    /// <summary>
    /// When the script's <c>TimerAntiKite</c> next fires, from its own timer list: minionLib 0.0
    /// arms it and every acquire re-arms it with <c>ResetAndStartTimer</c>, so a stopped timer
    /// (HaltAI, a taunt) answers null.
    /// </summary>
    internal double? AntiKiteDeadline(Entity minion) =>
        AiFor(minion).InstanceFor(minion)?.Timers
            .Where(timer => timer.Name == "TimerAntiKite")
            .Select(timer => (double?)timer.Deadline)
            .FirstOrDefault();

    private void TurnOffAutoAttack(Entity minion, string reason)
    {
        minion.MinionAutoAttackEnabled = false;
        StopAttack(minion, reason);
    }

    private bool IsIgnored(Entity minion, Entity target) =>
        _ignoredTargets.TryGetValue(minion.NetId, out var ignored) &&
        ignored.TryGetValue(target.NetId, out var until) && _elapsed < until;

    private void IgnoreTarget(Entity minion, double seconds = .1)
    {
        if (minion.AttackTarget is not uint target) return;
        if (!_ignoredTargets.TryGetValue(minion.NetId, out var ignored))
            _ignoredTargets[minion.NetId] = ignored = new();
        foreach (var id in ignored.Keys.Where(id => ignored[id] <= _elapsed).ToArray()) ignored.Remove(id);
        ignored[target] = _elapsed + seconds;
    }

    /// <summary>
    /// minionLib 0.2 <c>OnPathToTargetBlocked</c>: AddToIgnore(0.1) and FindTargetOrMove. The
    /// engine raises it once the approach has exhausted its angles; the ignore and the re-decide
    /// are the script's.
    /// </summary>
    internal void OnPathToTargetBlocked(Entity minion) => AiFor(minion).OnPathToTargetBlocked(minion);

    private Entity? TauntTarget(Entity minion) => minion.Buffs
        .Where(buff => buff.Type == BuffType.Taunt)
        .Select(buff => _world.Get(buff.CasterNetId)).LastOrDefault(target => target is { Alive: true });

    /// <summary>
    /// minion.lua 0.0 <c>OnTauntBegin</c>: SetStateAndCloseToTarget(AI_TAUNTED, GetTauntTarget()) and
    /// StopTimer('TimerAntiKite').
    /// </summary>
    public void OnTauntBegin(Entity minion) => AiFor(minion).OnTauntBegin(minion);

    /// <summary>
    /// minion.lua 0.1 <c>OnTauntEnd</c>: re-close on the remaining taunt target and re-arm
    /// <c>TimerAntiKite</c>, or run FindTargetOrMove without one.
    /// </summary>
    public void OnTauntEnd(Entity minion) => AiFor(minion).OnTauntEnd(minion);

    /// <summary>minionLib 0.7 <c>OnCanMove</c>: NetSetState(AI_IDLE) then FindTargetOrMove.</summary>
    public void OnCanMove(Entity minion) => AiFor(minion).OnCanMove(minion);

    /// <summary>minionLib 0.8 <c>OnCanAttack</c>: the same pair as OnCanMove.</summary>
    public void OnCanAttack(Entity minion) => AiFor(minion).OnCanAttack(minion);

    private static bool IsFear(BuffState buff) => buff.Type == BuffType.Fear ||
        buff.Name == "Fear" || buff.Payload.FearOrigin is not null;

    /// <summary>
    /// minionLib 0.4 <c>OnFearBegin</c>: MakeWanderPoint(GetFearLeashPoint(), FEAR_WANDER_DISTANCE),
    /// SetStateAndMove(AI_FEARED, wanderPoint), TurnOffAutoAttack(STOPREASON_IMMEDIATELY) and
    /// ResetAndStartTimer('TimerFeared'). The leash point is the application position, so it is
    /// captured here before the script asks for it.
    /// </summary>
    public void OnFearBegin(Entity minion, BuffState fear)
    {
        if (!IsLiveMinion(minion)) return;
        minion.RetainLaneRouteForControl();
        // Reconstruction: GetFearLeashPoint/MakeWanderPoint are native. Preserve
        // the application position and existing uniform angle/radius distribution. Unrecoverable
        // (batch 310): the unit's fear leash point field is only zero-initialised in the client; the debug data's
        // SetFearLeashPoint has no client caller, and MakeWanderPoint is in neither binary.
        fear.Payload.FearOrigin ??= minion.Position;
        _fears[minion.NetId] = fear;
        AiFor(minion).OnFearBegin(minion);
    }

    /// <summary>minionLib 0.5 <c>OnFearEnd</c>: StopTimer('TimerFeared') then FindTargetOrMove.</summary>
    public void OnFearEnd(Entity minion)
    {
        if (!IsLiveMinion(minion)) return;
        _fears.Remove(minion.NetId);
        AiFor(minion).OnFearEnd(minion);
    }

}
