using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Neutral;

/// <summary>
/// The world half of Leashed.luaobj's natives, bound to the services that already own a jungle
/// monster's movement, combat and wire output. Nothing here decides anything: the script's
/// callbacks and timers are the whole rule set (leash, reset and regen, aggro, call for help, camp
/// behaviour) and these are only the operations it names.
/// The per-monster state the script only points at — the leash point, the roam state, the leash
/// counter, the valid-target list and the remembered owner of the last target — lives here, and
/// every state-carrying native re-projects the observable <see cref="Entity.NeutralState"/> the
/// combat swing gate and the checks read ("engage" / "retreat" / "idle" / "feared").
/// </summary>
public sealed class NeutralLuaAiEngine : ILuaAiLeashedEngine
{
    // Geometry.ClientWaypoints truncates each axis to a 2-unit grid, so a destination can land up to
    // 2 * sqrt(2) units short of where it was aimed.
    private static readonly double WireRoundingMargin = 2.0 * Math.Sqrt(2.0) + 1e-3;

    /// <summary>Leashed' <c>INACTIVE</c> / <c>HOSTILE</c> roam states (the script sets the globals).</summary>
    public const double RoamInactive = 0.0;
    public const double RoamHostile = 1.0;

    /// <summary>The AI states Leashed names, over <see cref="AiGlobals"/>' ids.</summary>
    public const double StateFeared = 6.0;
    public const double StateRetreat = 7.0;
    public const double StateAttack = 31.0;

    /// <summary>The stop reason Leashed passes to <c>TurnOffAutoAttack</c> from fear and <c>HaltAI</c>.</summary>
    public const double StoppedImmediately = 100.0;

    private readonly WorldState _world;
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly INeutralPacketSink _wire;
    private readonly IRandomSource _random;
    private readonly Func<Entity, Entity, bool>? _targetVisible;
    private readonly Dictionary<uint, JungleState> _state = new();
    private readonly Dictionary<uint, NeutralMoveRetry> _moveRetries = new();
    // The target object the script took, kept across a wire-id clear (fear) so GetTarget answers
    // the target the member was fighting.
    private readonly Dictionary<uint, uint?> _retained = new();
    // Leashed.luaobj OnTargetLost reads the lost target's owner; remember it for the case where the
    // target (a dead pet) has already left the world by the next decision.
    private readonly Dictionary<uint, uint> _owners = new();
    private double _elapsed;

    // Keep retries on the source's normal 500 ms neutral decision cadence.
    // The retained destination prevents a transient crowd closure from
    // stranding the camp without inventing a faster AI scheduler.
    private const double NoRouteRetryDelaySeconds = NeutralDefinitions.RetreatCheckSeconds;

    /// <summary>Where a script failure raised by <c>Error</c> goes.</summary>
    public Action<Entity, string>? OnScriptError { get; set; }

    public NeutralLuaAiEngine(WorldState world, MovementSystem movement, CombatService combat,
        INeutralPacketSink wire, IRandomSource? random = null,
        Func<Entity, Entity, bool>? targetVisible = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _wire = wire ?? throw new ArgumentNullException(nameof(wire));
        _random = random ?? new SystemRandomSource();
        _targetVisible = targetVisible;
    }

    /// <summary>Advances the per-monster bookkeeping the world owns (route retries, pursuit).</summary>
    public void Tick(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        _elapsed += dt;
        foreach (var netId in _moveRetries.Keys.Where(id => _world.Get(id) is null).ToArray())
            _moveRetries.Remove(netId);
        foreach (var netId in _state.Keys.Where(id => _world.Get(id) is null).ToArray())
            _state.Remove(netId);
        foreach (var netId in _owners.Keys.Where(id => _world.Get(id) is null &&
                     !_state.Values.Any(state => state.ValidTargets.Contains(id))).ToArray())
            _owners.Remove(netId);
    }

    /// <summary>
    /// Reconstruction: the re-path cadence of the native <c>SetStateAndCloseToTarget</c>, whose own
    /// path request is unrecovered. Retail's attack state pursues its target; this emulator's
    /// stand-in re-issues the route on a 500 ms decision grid (the shipped Leashed TimerRetreat period, K5 = 0.5) so a kited
    /// camp keeps closing instead of walking to where its target was. That grid is free-running:
    /// a stun or a displacement takes the frames' action but not the phase, so the next re-path
    /// lands on the decision edge the retired machine used, not half a second after the suppression
    /// ends. The clock advances for every live member, chased or not, exactly like the free-running
    /// 0.5 s accumulator it replaces.
    /// </summary>
    /// <returns>Whether this step crossed the grid's decision edge.</returns>
    public bool AdvancePursuitClock(Entity monster, double dt)
    {
        var state = StateFor(monster);
        state.DecisionElapsed += dt;
        if (state.DecisionElapsed + 1e-9 < NeutralDefinitions.RetreatCheckSeconds) return false;
        // Keep the phase across a step that ran past the edge: the retired machine subtracted the
        // whole periods and clamped, so an overrun did not fire the edge again on the next frame.
        state.DecisionElapsed = Math.Max(0.0, state.DecisionElapsed -
            Math.Floor((state.DecisionElapsed + 1e-9) / NeutralDefinitions.RetreatCheckSeconds) *
            NeutralDefinitions.RetreatCheckSeconds);
        return true;
    }

    /// <summary>Acts on one decision edge: the chase route the re-path stand-in owns.</summary>
    public void TickPursuit(Entity monster, bool decisionEdge)
    {
        if (!decisionEdge) return;
        if (StateFor(monster).Pursuit is not { } pursuit) return;
        if (Pursuing(monster, pursuit.TargetNetId) is not { } target) return;
        CloseToTarget(monster, target, cancelAttack: true, reason: "neutral-aggro");
    }

    // ---- ILuaAiEngine: the shared targeting and combat half ----

    /// <summary>
    /// The clock the script's timers run on. It is this engine's own step clock, not the match
    /// clock: <c>InitTimer</c>/<c>ResetAndStartTimer</c> and the host's per-step pass must agree,
    /// and a jungle engine is stepped by <c>NeutralAiService.Tick</c> rather than by combat.
    /// </summary>
    public double Now => _elapsed;

    public string LostTargetHook => "OnTargetLost";

    public Entity? FindTargetInAcquisitionRange(Entity unit) =>
        // Leashed never asks for an acquisition scan: its target list is AddValidTarget's, and
        // FindValidTargetNearPosition is the selector. This is the shared ladder for other scripts.
        _combat.AcquireTurretTarget(unit);

    public void SetTarget(Entity unit, Entity? target)
    {
        StateFor(unit);
        _retained[unit.NetId] = target?.NetId;
        unit.AttackTarget = target?.NetId;
        if (target is null) return;
        _owners[target.NetId] = target.OwnerNetId;
    }

    public Entity? CurrentTarget(Entity unit)
    {
        // The object the AI took, not the wire id. Fear clears the id for the client and Leashed'
        // OnFearBegin/OnFearEnd never touch the target, so the member resumes the one it was
        // fighting when the fear ends; only a real loss is what OnTargetLost is raised for.
        var id = HeldTargetId(unit);
        return id is uint netId ? _world.Get(netId) : null;
    }

    public uint? HeldTargetId(Entity unit) =>
        // The retained id, including the one the script just dropped (a null entry is a real "holds
        // nothing"), so the host's loss test never raises for a target Leashed itself cleared in
        // Retreat()'s SetStateAndMoveToLeashedPos.
        _retained.TryGetValue(unit.NetId, out var held) ? held : unit.AttackTarget;

    public Entity? TauntTarget(Entity unit) => TauntTargetOf(unit);

    public bool TargetStillValid(Entity unit, Entity target) =>
        // The engine's target-lost test for a camp member: death, untargetability, a side change
        // or losing sight of it. Leashed' OnTargetLost is what answers, and the hand-written
        // machine folded the same sight rule into its loss branch. A taunt is the exception: it
        // forces the target (TimerTaunt re-takes GetTauntTarget every 0.5 s) and an unseen taunter
        // is not lost.
        TauntTargetOf(unit)?.NetId == target.NetId ||
        (_combat.CanAttackIgnoringRange(unit, target) && VisibleTarget(unit, target));

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && CombatService.InRange(unit, target);

    public void TurnOnAutoAttack(Entity unit, Entity? target)
    {
        // The swing gate is the projected NeutralState ("engage") plus the combat tick's own range
        // and cooldown tests; turning it on only re-affirms the swing target.
        if (target is not null) SetTarget(unit, target);
    }

    public void TurnOffAutoAttack(Entity unit, double stopReason)
    {
        // The stop reason (STOPREASON_*) is the engine's own; nothing in this build sends it.
        // A moving re-path cancels only a swing that has not been launched yet and says nothing to
        // the client unless one was cancelled; IMMEDIATELY (fear, HaltAI) always stops the swing.
        var cancelled = _combat.InterruptBasicAttack(unit);
        if (cancelled > 0 || stopReason == StoppedImmediately)
            if (unit.Created) _wire.StopAttack(unit);
    }

    public double AiState(Entity unit) => StateFor(unit).State;

    public void SetAiState(Entity unit, double state)
    {
        // No AI-state packet exists in this build (Reconstruction: retail put the state on the
        // wire through NetSetState). The id lives in the jungle state so an external
        // Entity.NeutralState adopted on the same step is visible to GetState.
        var jungle = StateFor(unit);
        // Leaving the retreat state is Leashed' OnStoppedMoving arrival at home: the retired
        // machine ran its Stop ("neutral-home") there, and that cleared the move order the member
        // was following. The arrival emits no stop order of its own (script = retail: the route is
        // simply consumed), so only the bookkeeping half of that Stop is reproduced.
        if (jungle.State == StateRetreat && state != StateRetreat)
        {
            unit.NeutralLastMove = null;
            _moveRetries.Remove(unit.NetId);
        }
        jungle.State = state;
        ProjectState(unit, jungle);
        // Reconstruction: re-entering the attack state re-issues the pursuit path (the same stand-in
        // family as TickPursuit: retail's SetStateAndCloseToTarget path request is unrecovered).
        // OnFearEnd and OnTauntEnd re-enter AI_ATTACK through this native with the target they
        // never dropped, and only SetStateAndCloseToTarget takes one as an argument, so without
        // this the member would stand still after a fear and never swing again. A native that does
        // take a target (SetStateAndCloseToTarget) sets the state itself and must not come through
        // here first, or the old target would get one path request before the new one.
        if (state == StateAttack && CurrentTarget(unit) is { } target)
            StartPursuit(unit, jungle, target);
    }

    // ---- ILuaAiLeashedEngine: the jungle half ----

    public void SetStateAndCloseToTarget(Entity unit, double state, Entity? target)
    {
        var jungle = StateFor(unit);
        jungle.State = state;
        if (target is not null) SetTarget(unit, target);
        ProjectState(unit, jungle);
        if (target is null) return;
        StartPursuit(unit, jungle, target);
    }

    private void StartPursuit(Entity unit, JungleState jungle, Entity target)
    {
        // The decision clock keeps running: the retired machine's 0.5 s accumulator was
        // free-running, so a new target did not move the grid (neutral-ai-corpus stunned).
        jungle.Pursuit = new PursuitState(target.NetId);
        CloseToTarget(unit, target, cancelAttack: true, reason: "neutral-aggro");
    }

    public void SetStateAndMove(Entity unit, double state, Position2 point)
    {
        var jungle = StateFor(unit);
        jungle.State = state;
        jungle.Pursuit = null;
        ProjectState(unit, jungle);
        // Leashed names this native only from OnFearBegin and TimerFeared, always with AI_FEARED:
        // the wander is a fear displacement, not an order the monster may keep following.
        if (state == StateFeared)
        {
            WanderFeared(unit, point);
            return;
        }
        SendMove(unit, point, "neutral-wander");
    }

    /// <summary>
    /// Reconstruction: the walk half of the native <c>SetStateAndMove</c> is unrecovered. Retail's fear owners are thought to turn a
    /// point into a displacement; this build's stand-in is the forced <c>Fear</c> move the other
    /// fear owners use (first collision landing, one short segment, the client walked along).
    /// </summary>
    private void WanderFeared(Entity unit, Position2 destination)
    {
        if (BuffLifecycle.BlocksFearLocomotion(unit)) return;
        if (Displaced(unit)) return;
        if (unit.Speed <= 0.0) return;
        var fear = FearBuffOf(unit);
        var duration = Math.Min(Math.Max(0.0, fear?.ExpiresAt ?? NeutralDefinitions.FearWanderSeconds),
            NeutralDefinitions.FearWanderSeconds);
        var route = _movement.StartFearWander(unit, destination, duration, fear?.CasterNetId ?? 0u);
        if (unit.Created)
            _wire.Waypoints(unit, route, checked((uint)_world.Clock.SyncId()));
    }

    public void SetStateAndMoveToLeashedPos(Entity unit, double state)
    {
        // Reconstruction: retail's retreat native also stops the swing and drops the retained
        // target — Leashed's Retreat() only names the state and the walk home, but nothing else in
        // the script stops a swing on the way, and the client's aggro outline follows the id.
        var jungle = StateFor(unit);
        jungle.State = state;
        jungle.Pursuit = null;
        SetTarget(unit, null);
        _combat.InterruptBasicAttack(unit);
        if (unit.Created) _wire.StopAttack(unit);
        ProjectState(unit, jungle);
        unit.NeutralLastMove = null;
        _moveRetries.Remove(unit.NetId);
        if (unit.Speed > 0.0)
            SendMove(unit, LeashPoint(unit), "neutral-retreat");
        else
            Stop(unit, reason: "neutral-retreat-stop");
    }

    public void SetMyLeashedPos(Entity unit)
    {
        var jungle = StateFor(unit);
        jungle.LeashPos = unit.Position;
        jungle.HasLeashPos = true;
        // The camp home the spawn schedule owns is the same point SetMyLeashedPos takes at OnInit.
        // Keep the entity field authoritative so a fixture that moves the member before its first
        // step (the script instance is created lazily, batch 141) still leashes to its camp.
        unit.NeutralHome ??= new Position3(unit.Position.X, 0.0, unit.Position.Z);
        ProjectState(unit, jungle);
    }

    public Position2 GetMyLeashedPos(Entity unit) => LeashPoint(unit);

    public double GetDistToLeashedPos(Entity unit) =>
        Math.Sqrt(DistanceSquared(unit.Position, LeashPoint(unit)));

    public double GetDistToRetreat(Entity unit) => GetDistToLeashedPos(unit);

    public Position2 GetFearLeashPoint(Entity unit) => LeashPoint(unit);

    public Position2 MakeWanderPoint(Entity unit, Position2 origin, double distance)
    {
        // Reconstruction: MakeWanderPoint is native; keep the uniform angle/radius draw the other
        // fear owners (MinionAiSystem, ShacoBoxService) use.
        var angle = _random.Uniform(0.0, Math.PI * 2.0);
        var radius = _random.Uniform(0.0, Math.Max(0.0, distance));
        return origin + new Position2(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    public bool IsMovementStopped(Entity unit) => !HasMovingRoute(unit);

    public double GetRoamState(Entity unit) => StateFor(unit).Roam;

    public void SetRoamState(Entity unit, double roam)
    {
        var jungle = StateFor(unit);
        jungle.Roam = roam;
        ProjectState(unit, jungle);
    }

    public double GetOriginalState(Entity unit) => StateFor(unit).OriginalRoam;

    public double GetLeashCounter(Entity unit) => unit.NeutralLeashCounter;

    public void SetLeashCounter(Entity unit, double counter) =>
        unit.NeutralLeashCounter = (int)counter;

    public void AddValidTarget(Entity unit, Entity? target)
    {
        if (target is null) return;
        var targets = StateFor(unit).ValidTargets;
        if (!targets.Contains(target.NetId)) targets.Add(target.NetId);
    }

    public void ClearValidTargets(Entity unit) => StateFor(unit).ValidTargets.Clear();

    public Entity? FindValidTargetNearPosition(Entity unit, Position2 position, double radius)
    {
        // Reconstruction: FindValidTargetNearPosition is native; the name says "near the position",
        // so the listed candidate nearest to it wins (ties keep list order). A candidate must also
        // be alive and seen by the monster, the same gate the current target has to pass.
        Entity? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var id in StateFor(unit).ValidTargets.ToArray())
        {
            var candidate = _world.Get(id);
            if (!VisibleTarget(unit, candidate)) continue;
            var distance = DistanceSquared(candidate!.Position, position);
            if (distance > radius * radius || distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    public Entity? GetOwner(uint netId) => Resolve(OwnerOf(netId));

    public Entity? GetGoldRedirectTarget(uint netId) => Resolve(GoldRedirectOf(netId));

    public double GetHP(Entity unit) => unit.Health;

    public double GetMaxHP(Entity unit) => unit.MaxHealth;

    public void SetHP(Entity unit, double hp)
    {
        unit.Health = Math.Clamp(hp, 0.0, unit.MaxHealth);
        // Leashed' TimerRegen is the retired Regenerate's tick, and that machine published its value
        // on every tick, full health included (neutral-ai-corpus chase frames 80/90/100/110). The
        // script only says SetHP, so the wire step follows the machine it replaced rather than
        // coalescing no-op sets. Retail's SetHP sent nothing for an unchanged value: the shared
        // Replicate<T> wrappers mark a field only when the assignment changes it (mValue != new,
        // the replication manager's mark; docs/network/patch126-replication-tables.md, batch 310), and
        // the connection's ClientReplicationStore drops a field equal to the client's copy, so the wire
        // already matches; this engine-level step keeps the cadence the neutral-ai-corpus records.
        if (unit.Created) _wire.ReplicationHp(unit);
    }

    public bool TargetInCancelAttackRange(Entity unit, Entity? target) =>
        target is not null && _combat.InCommittedAttackRange(unit, target);

    public double DistanceBetweenObjectAndPoint(Entity? unit, Position2 point) =>
        unit is null ? 0.0 : Math.Sqrt(DistanceSquared(unit.Position, point));

    public void Error(Entity unit, string message) => OnScriptError?.Invoke(unit, message);

    // ---- the movement mechanics the natives above name ----

    /// <summary>
    /// Reconstruction: the wire step of the native path requests (SetStateAndCloseToTarget,
    /// SetStateAndMoveToLeashedPos, SetStateAndMove). Retail is assumed to publish a path only when its AI
    /// changes it; this drops a repeat within 50 units of the last destination while the monster
    /// still follows a route, and retries a failed plan on the 0.5 s cadence instead of every step.
    /// </summary>
    public void SendMove(Entity monster, Position2 destination, string reason)
    {
        if (IsStunned(monster) || Displaced(monster)) return;
        NeutralMoveRetry? retry = null;
        if (_moveRetries.TryGetValue(monster.NetId, out var saved))
        {
            if (DistanceSquared(saved.Destination, destination) > 50.0 * 50.0)
                _moveRetries.Remove(monster.NetId);
            else retry = saved;
        }
        if (monster.NeutralLastMove is Position2 previous &&
            DistanceSquared(previous, destination) <= 50.0 * 50.0 &&
            HasMovingRoute(monster))
            return;
        if (retry is not null && _elapsed + 1e-9 < retry.RetryAt)
            return;

        var path = _movement.SetMoveOrder(monster.NetId, destination);
        monster.NeutralLastMove = destination;
        if (path.Count <= 1)
        {
            // A singleton planner result is a stationary anchor, not a
            // successful neutral move. Clear it so the next normal decision
            // cadence can retry rather than being hidden by Waypoints.Count.
            monster.Waypoints.Clear();
            monster.Target = null;
            var attempts = retry is null ? 1 :
                retry.Attempts == int.MaxValue ? int.MaxValue : retry.Attempts + 1;
            _moveRetries[monster.NetId] = new NeutralMoveRetry(destination,
                attempts, _elapsed + NoRouteRetryDelaySeconds);
            // Keep the destination in authoritative state for the next normal
            // retry. A failed route is one stop anchor, not a new stop packet
            // on every neutral cadence.
            // The stop's one-point path is installed on receipt: seated on its anchor at once.
            if (retry is null && monster.Created)
                _wire.Waypoints(monster, _movement.InstallStop(monster),
                    checked((uint)_world.Clock.SyncId()));
            _movement.ObserveNeutralDecision(monster, "no-route", destination,
                [monster.Position], attempts, phase: reason);
            return;
        }
        _moveRetries.Remove(monster.NetId);
        if (monster.Created)
            _wire.Waypoints(monster, path,
                checked((uint)_world.Clock.SyncId()));
        _movement.ObserveNeutralDecision(monster, reason, destination, path,
            phase: "accepted");
    }

    public void Stop(Entity monster, bool forceWire = false, string reason = "neutral-stop")
    {
        if (Displaced(monster)) return;
        var moving = monster.Waypoints.Count != 0 || monster.Target is not null;
        var wasOrdered = monster.NeutralLastMove is not null;
        monster.Waypoints.Clear();
        monster.Target = null;
        monster.NeutralLastMove = null;
        _moveRetries.Remove(monster.NetId);
        if ((moving || (forceWire && wasOrdered)) && monster.Created)
            _wire.Waypoints(monster, _movement.InstallStop(monster), checked((uint)_world.Clock.SyncId()));
        if (moving || wasOrdered)
            _movement.ObserveNeutralDecision(monster, reason, monster.Position,
                [monster.Position], phase: "stopped");
    }

    /// <summary>
    /// Reconstruction: stand-in for the native SetStateAndCloseToTarget's own path request, which is unrecovered. The stop
    /// point (attack reach less the wire rounding error), the hold inside reach or in a committed
    /// windup and the stop-attack before a new route are the emulator's choice.
    /// </summary>
    public void CloseToTarget(Entity monster, Entity target, bool cancelAttack, string reason)
    {
        if (IsStunned(monster) || Displaced(monster)) return;
        if (monster.Speed <= 0.0)
        {
            Stop(monster, reason: "neutral-static");
            return;
        }

        var dx = target.Position.X - monster.Position.X;
        var dz = target.Position.Z - monster.Position.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        // Aim inside the attack reach by the whole wire-rounding error. The old fixed 2 u margin let
        // the quantized stop point land 0.25 u outside the reach (GiantWolf at 220.25 of 220.00):
        // the next order rounded back to the same cell, so the monster neither moved nor swung.
        var reach = CombatService.AttackReach(monster, target) - WireRoundingMargin;
        // playtest #24: hold on the combat gates champion chase and minion AI
        // use. A committed windup keeps Map1's 100-unit stop-attack margin,
        // so a legal swing is neither re-chased nor cancelled.
        if (distance <= reach || _combat.CanAttack(monster, target) ||
            _combat.HasCommittedWindup(monster, target))
        {
            Stop(monster, reason: "neutral-in-range");
            return;
        }

        var scale = Math.Max(0.0, distance - reach) / distance;
        if (cancelAttack && monster.Waypoints.Count == 0 && monster.Target is null)
        {
            // Stop the server windup with the client's stop-attack so both
            // sides agree the swing was cancelled.
            _combat.InterruptBasicAttack(monster);
            if (monster.Created) _wire.StopAttack(monster);
        }
        SendMove(monster, new Position2(
            monster.Position.X + dx * scale,
            monster.Position.Z + dz * scale), reason);
    }

    // ---- the shared predicates and points ----

    /// <summary>Leashed' GetMyLeashedPos: the point <c>SetMyLeashedPos</c> took at OnInit.</summary>
    public Position2 LeashPoint(Entity unit)
    {
        if (unit.NeutralHome is Position3 home) return new Position2(home.X, home.Z);
        var jungle = StateFor(unit);
        return jungle.HasLeashPos ? jungle.LeashPos : unit.Position;
    }

    public bool ValidTarget(Entity monster, Entity? target) =>
        target is not null && target.Alive && !target.Protected &&
        WorldState.IsEnemy(monster, target);

    public bool VisibleTarget(Entity monster, Entity? target)
    {
        if (target is null || !ValidTarget(monster, target))
            return false;
        // A swing wound up on this member is a moment of certain contact. SpellHelper::
        // RevealSpellCaster only reveals the attacker at the cast frame (a swing dropped in its
        // windup reveals nothing on the wire), so a hero that steps into brush mid-windup is hidden
        // while its swing is still going to land here. Reconstruction: the native sight rule is
        // unrecovered; the hand-written machine's 0.5 s decision grid never looked inside that
        // window. Once the swing lands or is dropped, sight is the plain rule again.
        if (_combat.IsWindingUpOn(target, monster)) return true;
        // A damage wake establishes the target immediately. Later retention
        // follows the owning neutral's direct sight callback, including the
        // shared basic-attack reveal state; it never uses a team provider
        // union.
        return _targetVisible?.Invoke(monster, target) ?? true;
    }

    /// <summary>Leashed' GetTauntTarget: the caster of the newest live Taunt buff.</summary>
    public Entity? TauntTargetOf(Entity monster) => monster.Buffs
        .Where(buff => buff.Type == BuffType.Taunt)
        .Select(buff => _world.Get(buff.CasterNetId))
        .LastOrDefault(caster => ValidTarget(monster, caster));

    public static bool IsStunned(Entity unit) => unit.AnyBuffOrStatus(buff => buff.DisablesActions);

    /// <summary>
    /// A fear's action-disable is not a stun for the script. Leashed' own TimerFeared keeps
    /// re-picking the wander point while the fear lasts, so the instance's timers must keep
    /// running through it.
    /// </summary>
    public static bool IsFeared(Entity unit) => unit.Buffs.Any(buff =>
        buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null);

    /// <summary>
    /// Reconstruction: a forced displacement this build started owns the unit's movement until it
    /// retires (the hand-written monster machine skipped its whole step for one). Retail's
    /// <c>SetStateAndCloseToTarget</c>/<c>SetStateAndMove</c>/<c>Retreat</c> request a path
    /// regardless (assumed; unrecovered); here they are a no-op so a knockback, a dash or a scripted move already in
    /// flight is never replaced by a neutral order. Leashed' own fear wander is not a displacement
    /// to itself, so <c>AI_FEARED</c> keeps walking.
    /// </summary>
    public static bool Displaced(Entity unit) => unit.ForcedMove is { Effect: not "Fear" };

    /// <summary>The fear buff a wander segment is shortened to, when there is one.</summary>
    private static BuffState? FearBuffOf(Entity unit) => unit.Buffs
        .Where(buff => buff.Type == BuffType.Fear || buff.Name == "Fear" || buff.Payload.FearOrigin is not null)
        .LastOrDefault();

    // ---- internals ----

    private Entity? Pursuing(Entity monster, uint targetNetId)
    {
        // The retained target, not the wire id: a fear clears the id for the client while the member
        // is still chasing the target it took. The re-path rides right behind the step's target-lost
        // test, which has already asked vision and answered with OnTargetLost, so a second sight test
        // here would be two in one step.
        if (CurrentTarget(monster)?.NetId != targetNetId) return null;
        var target = _world.Get(targetNetId);
        return ValidTarget(monster, target) ? target : null;
    }

    private JungleState StateFor(Entity unit)
    {
        if (!_state.TryGetValue(unit.NetId, out var existing))
            // GetOriginalState is the roam state the unit was spawned with: NeutralMinionSpawn.luaobj's camp loop sets
            // AIState = INACTIVE for every camp (proto 0.0 pc 0043-0044), so the first state is the original one.
            _state[unit.NetId] = existing = new JungleState();
        AdoptExternalState(unit, existing);
        return existing;
    }

    /// <summary>
    /// <see cref="Entity.NeutralState"/> is what the rest of the match reads as a camp member's AI
    /// state — the combat swing gate, the wire and the checks. This build keeps Leashed' two state
    /// axes (the <c>AI_*</c> id of <c>GetState</c>/<c>SetState</c> and the INACTIVE/HOSTILE
    /// <c>GetRoamState</c>/<c>SetRoamState</c>) in the engine and publishes their projection, so a
    /// write of the string from outside (a fixture putting a member into "retreat", a corpus replay
    /// row) is adopted into those axes on the next native read and the script sees the state the
    /// rest of the match does. Reconstruction: retail netted one AI state (NetSetState), not two.
    /// </summary>
    private static void AdoptExternalState(Entity unit, JungleState jungle)
    {
        if (jungle.Projected == unit.NeutralState) return;
        switch (unit.NeutralState)
        {
            case "retreat":
                (jungle.State, jungle.Roam) = (StateRetreat, RoamHostile);
                break;
            case "feared":
                (jungle.State, jungle.Roam) = (StateFeared, RoamInactive);
                break;
            case "engage":
                (jungle.State, jungle.Roam) = (StateAttack, RoamHostile);
                break;
            default:
                (jungle.State, jungle.Roam) = (StateAttack, RoamInactive);
                break;
        }
        jungle.Projected = unit.NeutralState;
    }

    /// <summary>The observable state string the combat swing gate and the checks read.</summary>
    private static void ProjectState(Entity unit, JungleState jungle)
    {
        jungle.Projected = unit.NeutralState = jungle.State switch
        {
            StateFeared => "feared",
            StateRetreat => "retreat",
            _ when jungle.Roam == RoamHostile => "engage",
            _ => "idle",
        };
    }

    private Entity? Resolve(uint netId) => netId == 0u ? null : _world.Get(netId);

    /// <summary>
    /// Reconstruction: GetOwner and GetGoldRedirectTarget are separate natives but this build keeps
    /// one owner field (the hero a pet's kills credit is its owner), so both answer it. The value is
    /// remembered when a target is taken, because a dead pet can leave the world before its loss is
    /// observed and Leashed reads the owner of the lost unit.
    /// </summary>
    private uint OwnerOf(uint netId) =>
        _world.Get(netId)?.OwnerNetId ?? (_owners.TryGetValue(netId, out var owner) ? owner : 0u);

    private uint GoldRedirectOf(uint netId) => OwnerOf(netId);

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    /// <summary>Movement consumes the current route anchor as it advances. A remaining route can
    /// therefore legitimately contain one distant waypoint. Only a singleton at the actor's current
    /// position is a stationary stop anchor.</summary>
    public static bool HasMovingRoute(Entity monster) =>
        monster.Target is not null || monster.Waypoints.Count > 1 ||
        monster.Waypoints.Count == 1 &&
        DistanceSquared(monster.Position, monster.Waypoints[0]) > 1e-12;

    private sealed class JungleState
    {
        public double State = StateAttack;
        public double Roam = RoamInactive;
        public double OriginalRoam = RoamInactive;
        public Position2 LeashPos;
        public bool HasLeashPos;
        public List<uint> ValidTargets { get; } = new();
        public PursuitState? Pursuit;
        public double DecisionElapsed;
        /// <summary>The state string <see cref="ProjectState"/> last published.</summary>
        public string Projected = "idle";
    }

    private sealed record PursuitState(uint TargetNetId);

    private sealed record NeutralMoveRetry(Position2 Destination, int Attempts, double RetryAt);
}
