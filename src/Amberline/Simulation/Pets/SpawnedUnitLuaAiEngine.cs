using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The world half of the chunks a <c>BBSpawnMinion</c> gives a lane-less unit the shipped data
/// carries: <c>Idle.luaobj</c> (a ward, a wall piece, a blood drop — <c>OnInit</c>'s
/// <c>SetState(AI_HARDIDLE)</c> and an empty callback for every other hook), <c>Aggro.luaobj</c>
/// (Wukong's Nimbus clones) and <c>Minion.luaobj</c> with its <c>minionLib.luaobj</c> (Heimerdinger's
/// turrets, batch 339: the lane minion's chunk names the same natives, both halves this engine
/// carries). Nothing here decides anything: the chunks' callbacks and their timers
/// are the whole rule set and these are only the operations they name, answered from the same
/// services the hand-written ladder drove (<see cref="TibbersAiSystem"/>'s movement and combat
/// plumbing, which stays the engine's).
/// <para>
/// What Aggro.luaobj decides differs from the ladder it replaces (batch 163): <c>OnInit</c> writes
/// <c>AI_IDLE</c> and arms <c>TimerFindEnemies</c> at 0.0 s, and that timer only acquires from
/// <c>AI_ATTACKMOVESTATE</c> (proto 0.12 pc 0006-0008). Nothing walks the unit from
/// <c>AI_IDLE</c> into that state — <c>FindTargetOrMove</c> (proto 0.14) is what does, and only
/// <c>OnTargetLost</c>, <c>OnPathToTargetBlocked</c>, <c>OnCallForHelp</c>, <c>OnTauntEnd</c>,
/// <c>OnFearEnd</c>, <c>OnCanMove</c>, <c>OnCanAttack</c>, the two never-armed timers and the two
/// collision hooks call it — so a Nimbus clone stands where its spell script put it and fights only
/// what an event names. The script is the truth (owner, 20 October 2026); the hand-written
/// acquisition the clone used to do every 0.25 s is gone with its ladder.
/// </para>
/// </summary>
public sealed class SpawnedUnitLuaAiEngine : ILuaAiSpawnedEngine
{
    /// <summary>
    /// <c>STOPREASON_MOVING</c> (<c>AiGlobals</c>): the swing is dropped because the state is
    /// walking, and the target is kept with it. Every other reason the chunks name —
    /// <c>STOPREASON_IMMEDIATELY</c> (Aggro's <c>OnFearBegin</c> and <c>HaltAI</c>) and
    /// <c>STOPREASON_TARGET_LOST</c> (its <c>TimerFindEnemies</c> finding nothing to replace a
    /// gone target with) — ends the fight outright and drops the target too.
    /// </summary>
    private const double StoppedMoving = 101.0;

    private readonly TibbersAiSystem _system;
    private readonly WorldState _world;
    private readonly CombatService _combat;
    private readonly MovementSystem _movement;
    private readonly IRandomSource _random;
    private readonly Dictionary<uint, IgnoredTarget> _ignore = new();
    private double _elapsed;

    public SpawnedUnitLuaAiEngine(TibbersAiSystem system, MovementSystem movement,
        CombatService combat, IRandomSource? random = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _world = system.World;
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _random = random ?? new SystemRandomSource();
    }

    /// <summary>
    /// The clock the script's timers run on: the last <c>now</c> the world stepped this engine with
    /// (<see cref="Tick"/>), so <c>InitTimer</c>/<c>ResetAndStartTimer</c> and the host's per-step
    /// pass agree on one axis.
    /// </summary>
    public double Now => _elapsed;

    /// <summary>
    /// <c>Idle.luaobj</c>'s and <c>Aggro.luaobj</c>'s lost-target callback is the shared
    /// <c>OnTargetLost</c> (both chunks export it with no parameters).
    /// </summary>
    public string LostTargetHook => "OnTargetLost";

    /// <summary>Advances the clock and drops the ignore windows of units that left the world.</summary>
    public void Tick(double now, double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        if (double.IsFinite(now)) _elapsed = Math.Max(_elapsed, now);
        else _elapsed += dt;
        foreach (var netId in _ignore.Keys.Where(id => _world.Get(id)?.Alive != true).ToArray())
            _ignore.Remove(netId);
        foreach (var netId in _laneNode.Keys.Where(id => _world.Get(id)?.Alive != true).ToArray())
            _laneNode.Remove(netId);
    }

    /// <summary>
    /// The per-step half of the natives: the state closes on the target it holds and repeats the
    /// swing at it. Aggro.luaobj's <c>TimerFindEnemies</c> picks the target and turns the swing on
    /// or off at its own cadence (every step of an <c>AI_ATTACKMOVE_ATTACKING</c> unit) and this
    /// walks and swings in between, which is what the hand-written ladder's per-scan body did and
    /// what the brief keeps the engine's.
    /// </summary>
    public void Continue(Entity unit)
    {
        if (TibbersAiSystem.IsStunned(unit)) return;
        if (unit.MinionState is not (MinionAiState.Engage or MinionAiState.Taunted)) return;
        if (unit.AttackTarget is not uint id || _world.Get(id) is not { } target) return;
        if (_combat.CanAttack(unit, target) && unit.AttackCooldown <= 0.0) PetControl.QueueAttack(_combat, unit, target);
    }

    /// <summary>
    /// The order half, run only on the steps the unit's script timers run (<c>TibbersAiSystem.Tick</c> calls it when the host says one
    /// fired; batch 229 follow-up, coordinator, 25 October 2026): a unit in reach stops if it still walks, and one out of reach walks
    /// to its approach point. Returns true when the target is out of reach of a unit with no movement speed (Heimerdinger's turrets:
    /// <c>SetCanMove(false)</c> and an inibin speed of 0), whose approach the caller reports as the blocked path (<c>OnPathToTargetBlocked</c>).
    /// Reconstruction: the native's blocked-path report is not in the client; a unit that cannot move has no path to a target out of its
    /// reach, and minionLib's answer (AddToIgnore 0.1, FindTargetOrMove) is what lets such a unit turn to an enemy it can hit.
    /// </summary>
    public bool Scan(Entity unit)
    {
        if (TibbersAiSystem.IsStunned(unit)) return false;
        if (unit.MinionState is not (MinionAiState.Engage or MinionAiState.Taunted)) return false;
        if (unit.AttackTarget is not uint id || _world.Get(id) is not { } target) return false;
        if (_combat.CanAttack(unit, target))
        {
            if (unit.Waypoints.Count > 0 || unit.Target is not null) _system.Stop(unit);
            return false;
        }
        if (unit.Speed <= 0.0) return !_system.HasPendingAttack(unit);
        if (!TibbersAiSystem.IsRooted(unit) && !_system.HasPendingAttack(unit))
            _system.Move(unit, TibbersAiSystem.ApproachPoint(unit, target), target);
        return false;
    }

    // ---- ILuaAiEngine: the shared targeting and combat half ----

    /// <summary>
    /// <c>FindTargetInAcR</c>: the acquisition half of the ladder the two chunks share — the
    /// nearest of the heroes, minions and monsters in acquisition range, then a structure as the
    /// last resort (<see cref="MinionAiSystem.TargetPriority"/>). A unit the chunk pushed off with
    /// <c>AddToIgnore</c> is not re-picked for the window it named.
    /// </summary>
    public Entity? FindTargetInAcquisitionRange(Entity unit)
    {
        var range = unit.AcquisitionRange > 0.0 ? unit.AcquisitionRange : unit.AttackRange;
        var radiusSquared = range * range;
        var ignored = _ignore.TryGetValue(unit.NetId, out var entry) ? entry : null;
        return _world.Entities.Values
            .Where(candidate => CombatService.IsAutoAcquirable(candidate) &&
                                _system.IsEligibleTarget(unit, candidate, radiusSquared) &&
                                (ignored is null || ignored.TargetId != candidate.NetId ||
                                 _elapsed >= ignored.Until))
            // Reconstruction: FindTargetInAcR is native; the ladder is the lane minions'
            // (MinionAiSystem.TargetPriority), the nearest within a rung.
            .OrderBy(candidate => MinionAiSystem.TargetPriority(_world, unit, candidate))
            .ThenBy(candidate => TibbersAiSystem.DistanceSquared(unit.Position, candidate.Position))
            .FirstOrDefault();
    }

    public void SetTarget(Entity unit, Entity? target) => _system.SetAttackTarget(unit, target);

    public Entity? CurrentTarget(Entity unit) =>
        HeldTargetId(unit) is uint id ? _world.Get(id) : null;

    public uint? HeldTargetId(Entity unit) => unit.AttackTarget;

    public Entity? TauntTarget(Entity unit) => PetControl.TauntTarget(_world, unit);

    /// <summary>
    /// The engine's target-lost test for one of these units: death, untargetability, a side change
    /// or a loss of sight. Leaving attack range is not a loss — Aggro.luaobj's own
    /// <c>MAX_ENGAGE_DISTANCE</c> rule (proto 0.12) decides that — and a target the chunk itself
    /// dropped raises nothing.
    /// </summary>
    public bool TargetStillValid(Entity unit, Entity target) =>
        _system.IsEligibleTarget(unit, target, double.PositiveInfinity);

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && _combat.CanAttack(unit, target);

    /// <summary>
    /// <c>TurnOnAutoAttack(object)</c>: the swing may start at the object the chunk named. The
    /// state repeats the swing itself from <see cref="Continue"/>, so this re-affirms the target
    /// and nothing more (the same reading the lane minions' natives take).
    /// </summary>
    public void TurnOnAutoAttack(Entity unit, Entity? target) => _system.SetAttackTarget(unit, target);

    /// <summary><c>TurnOffAutoAttack(stopReason)</c>: see <see cref="StoppedMoving"/>.</summary>
    public void TurnOffAutoAttack(Entity unit, double stopReason)
    {
        if (stopReason == StoppedMoving) return;
        _system.SetAttackTarget(unit, null);
    }

    public double AiState(Entity unit) => MinionLuaAiEngine.StateId(unit.MinionState);

    public void SetAiState(Entity unit, double state) => unit.MinionState = MinionLuaAiEngine.StateOf(state);

    // ---- ILuaAiOwnedEngine: the ownership half ----

    /// <summary>
    /// <c>SetStateAndCloseToTarget(state, object)</c>: the state, then the walk toward the object it
    /// names. Aggro names an engaging state every time (<c>AI_ATTACKMOVE_ATTACKING</c> from
    /// <c>FindTargetOrMove</c> and the two collision hooks, <c>AI_TAUNTED</c> from
    /// <c>OnTauntBegin</c>/<c>OnTauntEnd</c>), so the object is the attack target: taken as the
    /// retained target and closed on at attack reach.
    /// </summary>
    public void SetStateAndCloseToTarget(Entity unit, double state, Entity? target)
    {
        SetAiState(unit, state);
        if (target is null) return;
        _system.SetAttackTarget(unit, target);
        if (_combat.CanAttack(unit, target))
        {
            if (unit.Waypoints.Count > 0 || unit.Target is not null) _system.Stop(unit);
            return;
        }
        if (TibbersAiSystem.IsRooted(unit) || unit.Speed <= 0.0 || _system.HasPendingAttack(unit))
            return;
        _system.Move(unit, TibbersAiSystem.ApproachPoint(unit, target), target);
    }

    /// <summary>
    /// <c>SetStateAndMove(state, point)</c>: walk to a point. Both chunks name it only from
    /// <c>OnFearBegin</c> and <c>TimerFeared</c>, always with <c>AI_FEARED</c> — the wander is a
    /// fear displacement, not an order the unit may keep following.
    /// </summary>
    public void SetStateAndMove(Entity unit, double state, Position2 point)
    {
        SetAiState(unit, state);
        // AI_FEARED (6): both chunks' only caller of this native.
        if (MinionLuaAiEngine.StateOf(state) == MinionAiState.Feared) WanderFeared(unit, point);
    }

    /// <summary>
    /// <c>GetFearLeashPoint()</c>: the point a fear wander is centred on. Both chunks call it from
    /// a fear callback only, so the answer is where the unit stands (the same the pet scripts get).
    /// </summary>
    public Position2 GetFearLeashPoint(Entity unit) => unit.Position;

    /// <summary>
    /// <c>MakeWanderPoint(point, distance)</c>. Reconstruction: native and unrecovered; keep the
    /// uniform angle/radius draw the other fear owners (the camp engine, Shaco's box) use.
    /// </summary>
    public Position2 MakeWanderPoint(Entity unit, Position2 origin, double distance)
    {
        var angle = _random.Uniform(0.0, Math.PI * 2.0);
        var radius = _random.Uniform(0.0, Math.Max(0.0, distance));
        return origin + new Position2(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    /// <summary>The hero the unit fights for (a Nimbus clone's owner).</summary>
    public Entity? GetOwner(uint netId) =>
        _world.Get(netId) is { OwnerNetId: not 0 } unit ? _world.Get(unit.OwnerNetId) : null;

    /// <summary>The hero a kill through this unit credits: the same owner, kept in one field.</summary>
    public Entity? GetGoldRedirectTarget(uint netId) => GetOwner(netId);

    /// <summary>
    /// <c>TargetInCancelAttackRange()</c>: inside the range a swing is kept rather than restarted
    /// (the existing edge-to-edge attack geometry plus the shipped
    /// <c>ar_StopAttackRangeModifier</c>).
    /// </summary>
    public bool TargetInCancelAttackRange(Entity unit, Entity? target) =>
        target is not null && _combat.InCommittedAttackRange(unit, target);

    // ---- ILuaAiMinionEngine: the natives only a lane-less unit of these two chunks names ----

    /// <summary>
    /// <c>SetStateAndMoveToForwardNav(state)</c>: take the state and walk the lane's forward route.
    /// A ward, a wall piece and a Nimbus clone have no lane, so the state is taken and the unit
    /// stands — which is what the hand-written ladder did with the same call, and what keeps
    /// Aggro's <c>FindTargetOrMove</c> (proto 0.14's empty scan) parked in
    /// <c>AI_ATTACKMOVESTATE</c> where its own <c>TimerFindEnemies</c> can acquire from.
    /// </summary>
    public void SetStateAndMoveToForwardNav(Entity unit, double state)
    {
        SetAiState(unit, state);
        if (unit.ScriptLaneRoute is { } route)
        {
            WalkScriptLane(unit, route);
            return;
        }
        if (unit.Waypoints.Count > 0 || unit.Target is not null) _system.Stop(unit);
    }

    /// <summary>
    /// Batch 593 (Reconstruction): a level script's <c>Minion.lua</c> unit (the Basic Tutorial's skirmishes and waves) walks its lane ahead
    /// (<see cref="Entity.ScriptLaneRoute"/>) one node at a time. A node is passed once the unit is nearer the node after it than the two
    /// nodes are to each other, or when its walk to the node ended (a node it cannot reach is not waited for); the last one ends the route
    /// and the unit stands. Minion.lua names this every step of a march, and <c>TibbersAiSystem.Move</c> keeps a walk to the same point.
    /// </summary>
    private void WalkScriptLane(Entity unit, List<Position2> route)
    {
        if (route.Count > 0 && !IsMoving(unit) && _laneNode.TryGetValue(unit.NetId, out var walked) && walked == route[0]) route.RemoveAt(0);
        while (route.Count > 1 &&
               TibbersAiSystem.DistanceSquared(unit.Position, route[1]) < TibbersAiSystem.DistanceSquared(route[0], route[1]))
            route.RemoveAt(0);
        if (route.Count == 0 || TibbersAiSystem.IsStunned(unit) || TibbersAiSystem.IsRooted(unit) || unit.Speed <= 0.0)
        {
            if (route.Count == 0 && (unit.Waypoints.Count > 0 || unit.Target is not null)) _system.Stop(unit);
            return;
        }
        _laneNode[unit.NetId] = route[0];
        _system.Move(unit, route[0]);
    }

    private readonly Dictionary<uint, Position2> _laneNode = new();

    /// <summary>
    /// Batch 451, batch 499: only MinionOdin.luaobj and MinionReverse.luaobj name the use natives, and they run on the lane engine
    /// (MinionLuaAiEngine); the chunks a spawned unit runs (Idle, Aggro, Minion) never call them, so it uses nothing.
    /// </summary>
    public Entity? FindUseableObjectInAcR(Entity unit) => null;

    /// <summary>Batch 451: a spawned unit uses nothing (see FindUseableObjectInAcR).</summary>
    public bool IsObjectUseable(Entity unit, Entity? other) => false;

    /// <summary>Batch 451: a spawned unit uses nothing (see FindUseableObjectInAcR).</summary>
    public void UseTarget(Entity unit, Entity? other) { }

    /// <summary>Batch 451: a spawned unit has no lane to walk backward either; the backward nav is the forward nav.</summary>
    public void SetStateAndMoveToBackwardNav(Entity unit, double state) => SetStateAndMoveToForwardNav(unit, state);

    /// <summary><c>LastAutoAttackFinished()</c>: no swing is still wound up on anything.</summary>
    public bool LastAutoAttackFinished(Entity unit) => !_system.HasPendingAttack(unit);

    /// <summary>
    /// <c>AddToIgnore(seconds)</c>: drop the current target from selection for a moment. Aggro
    /// names it with <c>0.1</c> from <c>OnPathToTargetBlocked</c> (proto 0.2) and its never-armed
    /// <c>TimerAntiKite</c> (proto 0.13); <c>FindTargetOrMove</c> then re-picks at once, so the
    /// window is what keeps it from taking the same body again.
    /// </summary>
    public void AddToIgnore(Entity unit, double seconds)
    {
        if (unit.AttackTarget is not uint id)
        {
            _ignore.Remove(unit.NetId);
            return;
        }
        _ignore[unit.NetId] = new IgnoredTarget(id, _elapsed + Math.Max(0.0, seconds));
    }

    /// <summary><c>DistanceBetweenMeAndObject(object)</c>: the straight-line distance to a unit.</summary>
    public double DistanceBetweenMeAndObject(Entity unit, Entity? other) =>
        other is null ? 0.0 : Math.Sqrt(TibbersAiSystem.DistanceSquared(unit.Position, other.Position));

    /// <summary><c>IsMoving()</c>: the unit still follows a move order (a planted one does not).</summary>
    public bool IsMoving(Entity unit) => unit.Waypoints.Count > 0 || unit.Target is not null;

    // ---- the mechanics the natives above name ----

    /// <summary>
    /// Reconstruction: the walk half of the native <c>SetStateAndMove</c> is unrecovered. Retail's fear owners are thought to turn a
    /// point into a displacement; this build's stand-in is the forced <c>Fear</c> move the other
    /// fear owners use (first collision landing, one short segment, the client walked along).
    /// </summary>
    private void WanderFeared(Entity unit, Position2 destination)
    {
        // The pulse's own deadline, in the record the shared one-second wander kept before
        // Aggro.luaobj walked its clone itself (ShacoBoxService.TickFears leaves an Aggro unit to
        // its own OnFearBegin/TimerFeared pair). Every pulse publishes the next one, so a knockback
        // that owns the route can see when the fear is due again.
        var fear = unit.Buffs.Where(buff => buff.Type == BuffType.Fear || buff.Name == "Fear" ||
            buff.Payload.FearOrigin is not null).LastOrDefault();
        if (fear is not null) fear.Payload.NextWander = _elapsed + 1.0;
        if (BuffLifecycle.BlocksFearLocomotion(unit)) return;
        if (unit.ForcedMove is { Effect: not "Fear" }) return;
        if (unit.Speed <= 0.0) return;
        // One wander per TimerFeared second, never longer than the fear left on the unit.
        var duration = Math.Min(Math.Max(0.0, fear?.ExpiresAt ?? 1.0), 1.0);
        _system.PublishWaypoints(unit, _movement.StartFearWander(unit, destination, duration, fear?.CasterNetId ?? 0u));
    }

    private sealed record IgnoredTarget(uint TargetId, double Until);
}
