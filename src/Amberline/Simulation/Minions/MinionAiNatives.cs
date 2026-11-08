using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Minions;

/// <summary>
/// The native-answer half of a lane minion's AI. <c>Minion.luaobj</c> and the
/// <c>minionLib.luaobj</c> it dofile's decide WHAT a minion does — its states, its timers, its
/// target choice — and every question they ask is answered here from the services this system
/// already owns. The route follower, the contact solver, the waypoint wire and the client-path
/// rules below these answers are the engine's, never the script's: a script request is a
/// projection onto that existing half, not a path request of its own.
/// </summary>
public sealed partial class MinionAiSystem
{
    /// <summary>
    /// The stop reasons <c>TurnOffAutoAttack</c> is called with (<c>AiGlobals</c>). They name which
    /// of the script's branches ended the attack; the stop itself is the same one every caller
    /// already makes.
    /// </summary>
    private const double StopReasonImmediately = 100.0;
    private const double StopReasonMoving = 101.0;
    private const double StopReasonTargetLost = 102.0;

    // ---- the shared natives ----

    /// <summary>
    /// <c>FindTargetInAcR()</c>: the scan, answered by the acquisition ladder
    /// (<see cref="SelectTarget"/>). Every target choice a minion makes goes through this one
    /// native — Minion.lua 0.2's <c>TimerFindEnemies</c>, 0.3's <c>FindTargetOrMove</c>, and
    /// minionLib 0.2/0.11's re-decides — so the observation seam sits here and measures the
    /// shipped cadence rather than a hand-kept one.
    /// </summary>
    internal Entity? NativeFindTarget(Entity minion)
    {
        try { TargetDecisionObserver?.Invoke(minion); }
        catch { /* test diagnostics never change target selection */ }
        return SelectTarget(minion);
    }

    /// <summary>
    /// <c>SetTarget(object)</c>. Minion.luaobj never names it directly (it closes through
    /// <c>SetStateAndCloseToTarget</c>); the host reaches it when a held target was lost and the
    /// script did not replace it.
    /// </summary>
    internal void NativeSetTarget(Entity minion, Entity? target)
    {
        if (target is null)
        {
            minion.AttackTarget = null;
            return;
        }
        Acquire(minion, target, retarget: minion.AttackTarget is not null);
    }

    /// <summary><c>GetTarget()</c>: the id this minion is closing on, as a unit.</summary>
    internal Entity? NativeCurrentTarget(Entity minion) =>
        minion.AttackTarget is uint id ? _world.Get(id) : null;

    /// <summary>The retained target id, read even when its unit has already left the world.</summary>
    internal uint? NativeHeldTargetId(Entity minion) => minion.AttackTarget;

    /// <summary>
    /// <c>GetTauntTarget()</c>: the live taunter. minionLib's <c>OnTauntBegin</c>,
    /// <c>OnTauntEnd</c> and <c>TimerFindEnemies</c> all re-read it, so the answer is the same
    /// last-live-taunt-buff rule the hand-written machine held.
    /// </summary>
    internal Entity? NativeTauntTarget(Entity minion) => TauntTarget(minion);

    /// <summary>
    /// Whether a held target is still a target: alive, unprotected, an enemy, targetable and seen
    /// (the hand-written engage branch's <c>usable</c> test). A taunt is the exception and never
    /// counts as lost — it forces its own target and a taunter stepping out of brush is still the
    /// taunter — exactly as the camp engine reads it.
    /// </summary>
    internal bool NativeTargetStillValid(Entity minion, Entity target) =>
        NativeTauntTarget(minion)?.NetId == target.NetId ||
        (target.Alive && !target.Protected && WorldState.IsEnemy(minion, target) &&
         _combat.IsTargetable(target, minion) && HasBrushVision(minion.Team, target));

    /// <summary><c>TargetInAttackRange()</c>: the swing may start.</summary>
    internal bool NativeTargetInAttackRange(Entity minion, Entity target) =>
        _combat.IsInAttackRange(minion, target);

    /// <summary>
    /// <c>TurnOnAutoAttack(object)</c>: the client's attack state starts the next swing on its own,
    /// so this only grants the permission and re-affirms the swing target.
    /// </summary>
    internal void NativeTurnOnAutoAttack(Entity minion, Entity? target)
    {
        if (target is not null && minion.AttackTarget != target.NetId)
            Acquire(minion, target, retarget: minion.AttackTarget is not null);
        minion.MinionAutoAttackEnabled = true;
    }

    /// <summary>
    /// <c>TurnOffAutoAttack(STOPREASON_*)</c>: the attack ends and its client is told. Every
    /// reason drops a swing still wound up, which is what the hand-written stops already did.
    /// </summary>
    internal void NativeTurnOffAutoAttack(Entity minion, double stopReason) =>
        TurnOffAutoAttack(minion, StopReasonName(stopReason));

    // ---- the ownership half ----

    /// <summary>
    /// <c>SetStateAndCloseToTarget(state, object)</c>: take the state and the target and close on
    /// it. Writing the state first would run an attack-state re-path on the target being replaced,
    /// so state, target and path go out in one step (<see cref="Acquire"/>).
    /// </summary>
    internal void NativeSetStateAndCloseToTarget(Entity minion, MinionAiState state, Entity? target)
    {
        if (!IsLiveMinion(minion)) return;
        if (target is not null)
        {
            Acquire(minion, target, retarget: minion.AttackTarget is not null, state);
            return;
        }
        minion.MinionState = state;
    }

    /// <summary>
    /// <c>SetStateAndMove(state, point)</c>: walk to a point. Minion.luaobj names it only from
    /// <c>OnFearBegin</c> and <c>TimerFeared</c>, always with <c>AI_FEARED</c> — the wander is a
    /// fear displacement, not an order the minion may keep following.
    /// </summary>
    internal void NativeSetStateAndMove(Entity minion, MinionAiState state, Position2 point)
    {
        if (!IsLiveMinion(minion)) return;
        minion.MinionState = state;
        if (state == MinionAiState.Feared) WanderFeared(minion, point);
    }

    /// <summary>
    /// <c>GetFearLeashPoint()</c>: the point a fear wander is centred on. The fear's own
    /// application position, captured when the buff arrived (a minion that was already walking is
    /// leashed from where the fear hit it, not from where it wanders next).
    /// </summary>
    internal Position2 NativeGetFearLeashPoint(Entity minion) =>
        (_fears.TryGetValue(minion.NetId, out var fear) ? fear.Payload.FearOrigin : null) ??
        minion.Position;

    /// <summary>
    /// <c>MakeWanderPoint(point, distance)</c>. Reconstruction: native and unrecovered; keep the
    /// uniform angle/radius draw the other fear owners (the camp engine, Shaco's box) use.
    /// </summary>
    internal Position2 NativeMakeWanderPoint(Entity minion, Position2 origin, double distance)
    {
        var angle = _random.Uniform(0.0, Math.PI * 2.0);
        var radius = _random.Uniform(0.0, Math.Max(0.0, distance));
        return origin + new Position2(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    /// <summary>
    /// <c>TargetInCancelAttackRange()</c>: inside the range a swing is kept rather than restarted.
    /// Reconstruction: the native uses the existing edge-to-edge attack geometry plus the shipped
    /// <c>ar_StopAttackRangeModifier</c> (+100), which is <c>CombatService.InCommittedAttackRange</c>. Unrecoverable from the
    /// client (batch 310): the CVar's value is written only by its static
    /// initializer and read by no client code, and the native is in neither binary.
    /// </summary>
    internal bool NativeTargetInCancelAttackRange(Entity minion, Entity? target)
    {
        if (target is null) return false;
        var reach = CombatService.AttackReach(minion, target) + 100.0;
        return DistanceSquared(minion.Position, target.Position) <= reach * reach;
    }

    // ---- the natives only a lane minion names ----

    /// <summary>
    /// <c>SetStateAndMoveToForwardNav(state)</c>: take the state and walk the lane's forward route.
    /// Minion.luaobj names it where a target run gave up and from <c>TimerMoveForward</c>, so it is
    /// the native that knows where "forward" is: the saved lane suffix, then the next structure.
    /// Reconstruction: the native's own internal cadence is unrecovered. The script asks every
    /// step (a zero-delay repeating timer) and the route is re-planned on the movement half's
    /// existing 250 ms gate, so a parked minion is not made to search again thirty times a second.
    /// </summary>
    internal void NativeSetStateAndMoveToForwardNav(Entity minion, MinionAiState state)
    {
        if (!IsLiveMinion(minion)) return;
        minion.MinionState = state;
        // A control buff or a forced move owns the public order (ControlOwnsMovement), so the
        // decision is still made and the strategic lane route kept for the release, but nothing is
        // planned or published while the control holds the unit.
        if (ControlOwnsMovement(minion))
        {
            if (minion.AttackTarget is not null || minion.LaneWaypoints.Count > 0)
                ReturnToLane(minion, publishRoute: false);
            else minion.MinionState = state;
            return;
        }
        // Minion.lua 0.4's TimerMoveForward names this every step of a march (a zero-delay
        // repeating timer), so the answer has to be idempotent: a unit that already follows a
        // route is walking forward, and publishing another one changes nothing but the wire. The
        // lane-return is for a target run that gives up -- a live attack target the script has
        // stopped closing on (minionLib 0.1's OnTargetLost reaches it with the target still
        // retained) -- or for a unit left standing on its saved lane suffix. Reading the saved
        // suffix alone as a give-up made every step of a march a full ReturnToLane: it republished
        // the lane route, disabled the swing and dropped the detour the body planner (deleted in
        // batch 271) had just issued for the same unit (b153, seed 5201: 2897 "target-lost" routes
        // against 3528 "congestion-route" replans, one per congestion publish).
        if (minion.AttackTarget is not null ||
            (minion.Waypoints.Count == 0 && minion.Target is null && minion.LaneWaypoints.Count > 0))
            ReturnToLane(minion);
        else
        {
            minion.MinionState = state;
            MoveForward(minion, refresh: TargetScanReady(minion));
        }
    }

    /// <summary><c>LastAutoAttackFinished()</c>: no swing is still wound up on anything.</summary>
    internal bool NativeLastAutoAttackFinished(Entity minion) => _combat.LastAutoAttackFinished(minion);

    /// <summary>
    /// <c>AddToIgnore(seconds)</c>: drop the current target from selection for a moment. The
    /// scripts name it with <c>0.1</c> from the anti-kite and blocked-path callbacks.
    /// </summary>
    internal void NativeAddToIgnore(Entity minion, double seconds) =>
        IgnoreTarget(minion, Math.Max(0.0, seconds));

    /// <summary>
    /// <c>DistanceBetweenMeAndObject(object)</c>: the straight-line distance to a unit. A unit that
    /// has left the world still answers from where it stood.
    /// </summary>
    internal double NativeDistanceBetweenMeAndObject(Entity minion, Entity? other) =>
        other is null ? 0.0 : Distance(minion.Position, other.Position);

    /// <summary><c>IsMoving()</c>: the unit still follows a move order (a planted one does not).</summary>
    internal bool NativeIsMoving(Entity minion) => minion.Waypoints.Count > 0 || minion.Target is not null;

    // ---- batch 499: the use natives of MinionOdin.luaobj and MinionReverse.luaobj ----

    /// <summary>
    /// <c>FindUseableObjectInAcR()</c> (MinionOdin protos 0.2 and 0.3: the first thing a scan asks, before any target): the nearest unit
    /// within the minion's acquisition range that the minion may use (<see cref="UseableBy"/>). On map 8 that is an enemy or neutral capture
    /// point guardian within 700 (Odin_*_Minion_Caster and Odin*Superminion: AcquisitionRange 700). Reconstruction: the native did not ship;
    /// centre distance, nearest first.
    /// </summary>
    internal Entity? NativeFindUseableObjectInAcR(Entity minion)
    {
        if (!IsLiveMinion(minion)) return null;
        var reach = minion.AcquisitionRange * minion.AcquisitionRange;
        Entity? best = null;
        var bestDistance = double.MaxValue;
        foreach (var unit in _world.Entities.Values)
        {
            if (unit.MinionUse is null || !UseableBy(minion, unit)) continue;
            var distance = DistanceSquared(minion.Position, unit.Position);
            if (distance > reach || distance >= bestDistance) continue;
            best = unit;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary><c>IsObjectUseable(object)</c> (MinionOdin protos 0.0 to 0.2, the taunt branches): whether the minion may use it.</summary>
    internal bool NativeIsObjectUseable(Entity minion, Entity? other) => other is not null && UseableBy(minion, other);

    /// <summary>
    /// <c>UseTarget(object)</c>: the minion goes to use the object. The scripts call it after <c>NetSetState(AI_IDLE)</c> and
    /// <c>TurnOffAutoAttack(STOPREASON_MOVING)</c> from every scan while the object stays in range, and from the taunt hooks in place of
    /// closing on the taunter. The minion leaves its lane (its rest is kept, as an acquisition keeps it) and walks to the object.
    /// <para>
    /// Batch 711: within the object's use range the minion stops and casts the object's <c>MinionUseSpell</c> at it (<see cref="UseObject"/>):
    /// OdinMinionSpellAttack, CastRange 400, Cooldown 0.8 (a cooling cast is refused, so the scans that keep calling UseTarget hit once per
    /// cooldown). Its TargetExecute moves a neutral guardian's pool 1 % toward the minion's team (1.5 % for a superminion), drains an enemy one
    /// by 2 % (3 %), and on a neutral point returns 25 % of the minion's maximum health as true damage. Reconstruction: the native did not ship;
    /// the scripts call UseTarget on every scan while the guardian taunts them (MinionOdin proto 0.2, AI_TAUNTED), so the use is one cast per call.
    /// Before batch 711 the point's tree consumed the minion at 600 and the cast was never made (BehaviorLeaves.KillUnit).
    /// </para>
    /// </summary>
    internal void NativeUseTarget(Entity minion, Entity? other)
    {
        if (!IsLiveMinion(minion) || other is null || !UseableBy(minion, other) || ControlOwnsMovement(minion)) return;
        if (minion.LaneWaypoints.Count == 0 && minion.Waypoints.Count > 0) minion.LaneWaypoints = minion.Waypoints.ToList();
        // The use replaces the attack order (the taunt hooks call it without the scan's TurnOffAutoAttack): an engage step must not chase the
        // old target over the walk.
        if (minion.AttackTarget is not null)
        {
            minion.MinionAutoAttackEnabled = false;
            StopAttack(minion, "use-object");
            minion.AttackTarget = null;
            minion.AttackPoint = null;
            minion.Target = null;
        }
        var (spell, range, _, _) = other.MinionUse!.Value;
        if (DistanceSquared(minion.Position, other.Position) <= range * range)
        {
            if (minion.Waypoints.Count > 0) ClearMovement(minion, "use-object");
            UseObject?.Invoke(minion, other, spell);
            return;
        }
        IssueMove(minion, other.Position, "use-object");
    }

    /// <summary>
    /// The useable component's allowed-to-use check (batch 470) for a minion: the unit is useable by minions (<see cref="Entity.MinionUse"/>),
    /// a unit of the minion's team needs <c>AllyCanUse</c> and any other team (a neutral guardian too) <c>EnemyCanUse</c>.
    /// </summary>
    private static bool UseableBy(Entity minion, Entity unit) =>
        unit.Alive && !ReferenceEquals(unit, minion) && unit.MinionUse is { } use && (unit.Team == minion.Team ? use.AllyCanUse : use.EnemyCanUse);

    // ---- the movement these answers name ----

    /// <summary>
    /// Reconstruction: the walk half of the native <c>SetStateAndMove</c> is unrecovered. Retail's fear owners are thought to turn a
    /// point into a displacement; this build's stand-in is the forced <c>Fear</c> move the other
    /// fear owners use (first collision landing, one short segment, the client walked along).
    /// </summary>
    private void WanderFeared(Entity minion, Position2 destination)
    {
        if (BuffLifecycle.BlocksFearLocomotion(minion)) return;
        if (minion.ForcedMove is { Effect: not "Fear" }) return;
        if (minion.Speed <= 0.0) return;
        var fear = minion.Buffs.Where(IsFear).LastOrDefault() ??
                   (_fears.TryGetValue(minion.NetId, out var held) ? held : null);
        // One wander per TimerFeared second, never longer than the fear left on the unit.
        var duration = Math.Min(Math.Max(0.0, fear?.ExpiresAt ?? 1.0), 1.0);
        EmitWaypoints(minion, _movement.StartFearWander(minion, destination, duration, fear?.CasterNetId ?? 0u), "fear");
    }

    private static string StopReasonName(double stopReason) => stopReason switch
    {
        StopReasonImmediately => "immediate",
        StopReasonMoving => "moving",
        StopReasonTargetLost => "target-lost",
        _ => "script-stop",
    };
}
