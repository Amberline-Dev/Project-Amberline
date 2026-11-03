using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Movement;

/// <summary>
/// The world half of <c>Hero.luaobj</c>: what its natives mean for one champion. The chunk decides
/// (its <c>OnOrder</c>/<c>OnTargetLost</c> callbacks and its 0.2 s <c>TimerDistanceScan</c>/
/// <c>TimerCheckAttack</c>); everything here is the plumbing those decisions ask for — the target
/// store, the swing, the walk — bound to the same <see cref="HeroAttackOrderSystem"/> and
/// <see cref="CombatService"/> the hand-written hero machine used, so the wire the client sees is
/// unchanged. Where the chunk and the retired machine disagreed the chunk won (see
/// <see cref="docs/maintenance/2026-10-23-batch-163-lua-hero-ai.md"/> for the table).
/// </summary>
public sealed class HeroLuaAiEngine : ILuaAiHeroEngine
{
    // AiGlobals' hero states (the values the chunk's GETGLOBAL reads).
    public const double StateHalted = 0.0;
    public const double StateIdle = 1.0;
    public const double StateHardIdle = 2.0;
    public const double StateAttackMove = 3.0;
    public const double StateTaunted = 5.0;
    public const double StateFeared = 6.0;
    public const double StateSoftAttack = 8.0;
    public const double StateHardAttack = 9.0;
    public const double StateStanding = 10.0;
    public const double StateCharmed = 11.0;
    public const double StateGoingToLastKnownLocation = 12.0;
    /// <summary>AiGlobals' <c>LOST_VISIBILITY</c>: the one loss reason Hero.txt switches on.</summary>
    public const double LostVisibility = 13.0;
    public const double StoppedImmediately = 100.0;
    public const double StoppedMoving = 101.0;

    private readonly WorldState _world;
    private readonly HeroAttackOrderSystem _orders;
    private readonly CombatService _combat;
    private readonly GameData _data;
    private readonly ISpellBuffService _buffs;
    private readonly IRandomSource _random;
    private readonly Func<Entity, Entity, bool> _visible;
    private readonly Dictionary<uint, HeroAiState> _state = new();
    private double _elapsed;

    /// <summary>Where a script failure raised by <c>Say</c> goes.</summary>
    public Action<Entity, string>? OnSay { get; set; }

    public HeroLuaAiEngine(WorldState world, HeroAttackOrderSystem orders, CombatService combat,
        GameData data, ISpellBuffService buffs, Func<Entity, Entity, bool> visible,
        IRandomSource? random = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _visible = visible ?? throw new ArgumentNullException(nameof(visible));
        _random = random ?? new SystemRandomSource();
    }

    public WorldState World => _world;

    /// <summary>
    /// The clock the script's timers run on — the order system's, so <c>InitTimer</c>/
    /// <c>ResetAndStartTimer</c> and the host's per-step pass agree on one axis.
    /// </summary>
    public double Now => _elapsed;

    /// <summary>
    /// Hero.luaobj's lost-target callback is the shared <c>OnTargetLost</c> (proto 0.3,
    /// <c>(lossReason, lostObject)</c>).
    /// </summary>
    public string LostTargetHook => "OnTargetLost";

    /// <summary>Advances the engine clock and drops the state of heroes that left the world.</summary>
    public void Tick(double now, double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        if (double.IsFinite(now)) _elapsed = Math.Max(_elapsed, now);
        else _elapsed += dt;
        foreach (var netId in _state.Keys.Where(id => _world.Get(id)?.Alive != true).ToArray())
            _state.Remove(netId);
    }

    /// <summary>Forgets a hero that died, so a later unit with the same id is not adopted into its state.</summary>
    public void Forget(uint netId) => _state.Remove(netId);

    /// <summary>
    /// The in-position point the caller staged for the order about to be dispatched — the click
    /// <c>AssignTargetPosInPos</c> then publishes. Hero.txt's OnOrder takes no point of its own
    /// (proto 0.2 pc 0036, pc 0070, pc 0080 call the no-argument native), so the point has to be
    /// in place before <c>OnOrder</c> runs.
    /// </summary>
    public void StageInPosition(Entity unit, Position2 point) => StateFor(unit).InPosition = point;

    // ---- ILuaAiEngine: the targeting and combat half ----

    public double AiState(Entity unit) => StateFor(unit).State;

    /// <summary>
    /// A plain state write (<c>SetState</c>/<c>NetSetState</c>). Hero.txt writes
    /// <c>AI_STANDING</c> where the fight is over (TimerCheckAttack proto 0.12 pc 0070,
    /// TimerDistanceScan proto 0.13 pc 0055 and pc 0079) and expects the walk to stop with it, and
    /// every state outside the attack-move pair drops the attack-move goal the player's order
    /// installed — that is what makes "an acquisition scan cannot interrupt recall" true.
    /// </summary>
    public void SetAiState(Entity unit, double state)
    {
        WriteState(unit, state);
        if (!KeepsRoute(state)) _orders.StopMovement(unit);
    }

    /// <summary>
    /// The state id and the order bookkeeping that go with it, without touching the route: the
    /// state-and-* natives write the same state in the same step as the target or the path they
    /// name, and a stop first would cost a second path request.
    /// </summary>
    private void WriteState(Entity unit, double state)
    {
        var hero = StateFor(unit);
        hero.State = state;
        // The attack-move goal is the player's order intent; the script leaves it behind the moment
        // the state is neither the walk itself nor the soft attack it engaged with.
        if (state != StateAttackMove && state != StateSoftAttack) unit.AttackMoveGoal = null;
    }

    /// <summary>Whether the state keeps walking: the two order walks, the last-known walk and the fear wander.</summary>
    private static bool KeepsRoute(double state) =>
        state is StateAttackMove or StateGoingToLastKnownLocation or StateFeared or StateMove;

    /// <summary>AiGlobals' <c>AI_MOVE</c> (32): a MOVETO's walk.</summary>
    public const double StateMove = 32.0;

    public Entity? FindTargetInAcquisitionRange(Entity unit) => Acquire(unit);

    public void SetTarget(Entity unit, Entity? target)
    {
        // One store for the target: Entity.AttackTarget is what GetTarget answers and what every
        // external clear (recall, an interrupted cast, the match hooks) writes. A transition retires
        // only unlaunched attacks, and only when the target really changes, so a repeated order
        // keeps the swing it is already winding up. The route is the state's business, not this.
        if (unit.AttackTarget is uint oldId && oldId != (target?.NetId ?? 0u))
            _orders.InterruptWindup(unit, "hero-target-changed", target);
        unit.AttackTarget = target?.NetId;
    }

    public Entity? CurrentTarget(Entity unit) =>
        unit.AttackTarget is uint netId ? _world.Get(netId) : null;

    public uint? HeldTargetId(Entity unit) => unit.AttackTarget;

    public Entity? TauntTarget(Entity unit) => PetControl.TauntTarget(_world, unit);

    public bool TargetStillValid(Entity unit, Entity target) =>
        // The target-lost test OnTargetLost is raised for: death, untargetability or a side change.
        // Leaving attack range is not a loss, but an unseen one is ("a hidden target cannot remain
        // an attack target" — the same rule the pet and jungle engines use).
        _combat.CanAttackIgnoringRange(unit, target) && CanSee(unit, target);

    public double TargetLossReason(Entity unit, Entity? target) =>
        // Hero.txt OnTargetLost (proto 0.3 pc 0009-0011) switches on LOST_VISIBILITY and walks to
        // the last known location only for that reason; everything else is a plain re-decide. The
        // reason is the sight case alone — the target is still a live enemy and only its sight left
        // — so it cannot ask targetability: MatchGameplayHooks.IsTargetable folds stealth in, and a
        // hidden target would then report a plain loss and the chunk would run TimerCheckAttack
        // instead of the walk (proto 0.3 pc 0017-0020). A dead one, an untargetable one that can
        // still be seen and a side change are plain losses.
        target is { Alive: true } && WorldState.IsEnemy(unit, target) && !CanSee(unit, target)
            ? LostVisibility
            : 0.0;

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && CombatService.InRange(unit, target);

    public bool TargetInCancelAttackRange(Entity unit, Entity? target) =>
        target is not null && _combat.InCommittedAttackRange(unit, target);

    public void TurnOnAutoAttack(Entity unit, Entity? target)
    {
        // The script passes GetTarget() (TimerCheckAttack proto 0.12 pc 0081-0084); re-affirming it
        // is what SetTarget already did, and CombatService swings at a hero with a target in range
        // on its next combat tick — no queued hit is needed.
        if (target is not null) SetTarget(unit, target);
    }

    public void TurnOffAutoAttack(Entity unit, double stopReason)
    {
        // Hero.txt calls this with STOPREASON_MOVING when the target left the cancel range
        // (TimerCheckAttack pc 0091-0093) or left the ordinary attack range on a repeated ATTACK_TO
        // (OnOrder proto 0.2 pc 0047-0049), and with STOPREASON_IMMEDIATELY from OnCharmBegin and
        // HaltAI. The immediate stop ends the fight outright. The moving one only stands auto-attack
        // down for the walk ahead — the same body re-arms it with TurnOnAutoAttack(GetTarget()) once
        // the target is back in reach — so it drops just the swings that are no longer admitted: a
        // swing inside its ar_StopAttackRangeModifier leash lands (the rule CombatService.
        // HasCommittedWindup states and the chase keeps). Both send 0x39 only when a swing was cut.
        if (stopReason == StoppedImmediately)
        {
            _orders.InterruptWindup(unit, "hero-attack-stopped");
            unit.AttackTarget = null;
            return;
        }
        if (CurrentTarget(unit) is { } target && _combat.HasCommittedWindup(unit, target)) return;
        _orders.InterruptWindup(unit, "hero-attack-paused");
    }

    // ---- ILuaAiOwnedEngine: the ownership half ----

    public void SetStateAndCloseToTarget(Entity unit, double state, Entity? target)
    {
        WriteState(unit, state);
        SetTarget(unit, target);
        if (state == StateGoingToLastKnownLocation)
        {
            // OnTargetLost (proto 0.3 pc 0017-0020): walk to where the target was last seen and
            // take it again if it reappears (TimerDistanceScan pc 0089-0096). That is the pursuit
            // record's point, not the target's live one — a target that hid and ran is somewhere
            // else now — and a loss with no record behind it (no engage took the target) has no
            // better answer than the position the loss carried.
            var lostId = target?.NetId ?? 0u;
            WalkToLastKnown(unit, lostId,
                unit.HeroPursuit is HeroPursuitState last && last.TargetNetId == lostId
                    ? last.LastKnownPosition
                    : target?.Position ?? unit.Position);
            return;
        }
        if (target is null) return;
        // Only a hard engage is an explicit lock the postponed-order and fidelity paths read; a
        // soft one (idle acquisition, the attack-move scan) stays an ordinary attack target.
        unit.HeroPursuit = state is StateHardAttack or StateTaunted
            ? new HeroPursuitState(target.NetId, target.Position)
            : null;
        if (unit.HeroHold)
        {
            _orders.StopMovement(unit);
            return;
        }
        _orders.Chase(unit, target);
    }

    /// <summary>
    /// The walk to a target's last known position: the state OnTargetLost installs for a visibility
    /// loss and the one an explicit pursuit falls back to while its target is out of sight. The
    /// pursuit record is what the movement half and the postponed-order path read; the walk ends at
    /// the callback the chunk defines for arrival (proto 0.15).
    /// </summary>
    internal void WalkToLastKnown(Entity unit, uint targetNetId, Position2 lastKnownPosition)
    {
        var hero = StateFor(unit);
        WriteState(unit, StateGoingToLastKnownLocation);
        // The target is gone from the hero's sight, so the swing it never launched goes with it (the
        // retired machine's UpdatePursuit: `if (lostNow) InterruptWindup(hero,
        // "pursuit-target-ineligible")`), and the client is told (0x39) as for any cut windup.
        if (unit.AttackTarget is not null) _orders.InterruptWindup(unit, "pursuit-target-ineligible");
        unit.AttackTarget = null;
        unit.LastAttackStopPoint = null;
        hero.LostTargetId = targetNetId;
        hero.LostPosition = lastKnownPosition;
        unit.HeroPursuit = new HeroPursuitState(targetNetId, lastKnownPosition);
        if (!unit.HeroHold && !_orders.Blocked(unit)) _orders.Move(unit, lastKnownPosition);
    }

    public void SetStateAndMove(Entity unit, double state, Position2 point)
    {
        WriteState(unit, state);
        // Hero.txt names this native only from OnFearBegin and TimerFeared, always with AI_FEARED:
        // the wander is a fear displacement, not an order the hero may keep following.
        if (state == StateFeared)
        {
            _orders.WanderFeared(unit, point);
            return;
        }
        _orders.Move(unit, point);
    }

    public Position2 GetFearLeashPoint(Entity unit) => unit.Position;

    public Position2 MakeWanderPoint(Entity unit, Position2 origin, double distance)
    {
        // Reconstruction: MakeWanderPoint is native; keep the uniform angle/radius draw the other
        // fear owners (NeutralLuaAiEngine, MinionAiSystem, ShacoBoxService) use.
        var angle = _random.Uniform(0.0, Math.PI * 2.0);
        var radius = _random.Uniform(0.0, Math.Max(0.0, distance));
        return origin + new Position2(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    public Entity? GetOwner(uint netId) => null;

    public Entity? GetGoldRedirectTarget(uint netId) => null;

    // ---- ILuaAiHeroEngine: the natives only Hero.txt names ----

    public void SetStateAndMoveInPos(Entity unit, double state)
    {
        var hero = StateFor(unit);
        WriteState(unit, state);
        // The walk leaves the fight behind: an ordinary move never acquires before arrival (Hero.txt
        // TimerDistanceScan proto 0.13 pc 0030-0059 only scans an AI_MOVE hero once it has stopped).
        _orders.LeaveFight(unit);
        if (hero.InPosition is { } destination) _orders.Move(unit, destination, orderWalk: true);
    }

    public void AssignTargetPosInPos(Entity unit)
    {
        // Publishes the point the walk is going to. Reconstruction: retail's native planned the
        // click first and published the reachable endpoint, which is why the arrival test reads the
        // live walk goal when there is one — answering from the latched click would leave an
        // unreachable attack-move walking for ever.
        var hero = StateFor(unit);
        hero.TargetPos = hero.InPosition;
    }

    public void ClearTargetPosInPos(Entity unit) => StateFor(unit).TargetPos = null;

    public double DistanceBetweenObjectAndTargetPosSq(Entity unit, Entity? other)
    {
        var subject = other ?? unit;
        var goal = unit.AttackMoveGoal ?? StateFor(unit).TargetPos ?? subject.Position;
        var dx = subject.Position.X - goal.X;
        var dz = subject.Position.Z - goal.Z;
        return dx * dx + dz * dz;
    }

    public Entity? GetTargetOrFindTargetInAcquisitionRange(Entity unit) =>
        CurrentTarget(unit) ?? Acquire(unit);

    public Entity? GetLostTargetIfVisible(Entity unit)
    {
        var hero = StateFor(unit);
        if (hero.LostTargetId is not uint lostId || _world.Get(lostId) is not { } lost) return null;
        return CanSee(unit, lost) ? lost : null;
    }

    public bool CanSeeMe(Entity unit, Entity? other) => other is not null && CanSee(other, unit);

    public bool IsTargetLost(Entity unit)
    {
        if (HeldTargetId(unit) is not uint heldId) return true;
        return _world.Get(heldId) is not { } held || !TargetStillValid(unit, held);
    }

    public bool IsMovementStopped(Entity unit) => !_orders.HasLiveRoute(unit);

    public bool IsMoving(Entity unit) => unit.Waypoints.Count > 0 || unit.Target is not null;

    public bool LastAutoAttackFinished(Entity unit) => _combat.LastAutoAttackFinished(unit);

    public double GetPercentAttackSpeedMod(Entity unit) =>
        unit.AttackSpeedMod -
        _data.StatsAtLevel(_data.Champion(unit.ChampionName), unit.Level).AttackSpeedMod;

    public void SpellBuffRemoveType(Entity unit, Entity? target, double scriptType)
    {
        // TimerCheckAttack (proto 0.12 pc 0066-0069) drops BUFF_Taunt off the hero itself when a
        // charmed re-acquisition finds nothing to take. Every buff of that script type goes unless a
        // payload marks it non-dispellable — the sweep the BuildingBlocks native of the same name
        // runs (BbBuffRuntime.RemoveType).
        var subject = target ?? unit;
        var type = (BuffType)(int)scriptType;
        foreach (var buff in subject.Buffs
                     .Where(buff => buff.Type == type && !buff.Payload.NonDispellable).ToArray())
            _buffs.Remove(subject, buff.Slot);
    }

    public void Say(Entity unit, string text) => OnSay?.Invoke(unit, text);

    /// <summary>
    /// The native half of an engage: keeps closing on the held target between the script's own
    /// 0.2 s checks (Hero.txt only re-decides who to fight; the walk at a target it already took is
    /// the state's, and the state lives here), and finishes the walk to a lost target's last known
    /// location by raising the callback the chunk defines for it (proto 0.15).
    /// </summary>
    public void Continue(Entity unit)
    {
        if (_orders.Blocked(unit)) return;
        var state = AiState(unit);
        if (state == StateGoingToLastKnownLocation)
        {
            if (unit.HeroPursuit is not HeroPursuitState pursuit)
            {
                SetAiState(unit, StateStanding);
                return;
            }
            if (DistanceSquared(unit.Position, pursuit.LastKnownPosition) <= 100.0)
            {
                unit.HeroPursuit = null;
                unit.LastAttackStopPoint = null;
                _orders.StopMovement(unit);
                SetAiState(unit, StateStanding);
                _orders.Host.OnReachedDestinationForGoingToLastLocation(unit);
                return;
            }
            if (!_orders.HasLiveRoute(unit) && !unit.HeroHold)
                _orders.Move(unit, pursuit.LastKnownPosition);
            return;
        }
        if (state is StateSoftAttack or StateHardAttack or StateTaunted or StateCharmed)
        {
            if (unit.HeroHold) return;
            if (CurrentTarget(unit) is { } target && _orders.Eligible(unit, target))
            {
                // The record tracks where the target was last seen, the way the retired machine's
                // UpdatePursuit refreshed it every scan (`hero.HeroPursuit = pursuit with {
                // LastKnownPosition = target.Position }`), so a later loss walks to the point it
                // vanished from and not to the one the engage happened at.
                if (unit.HeroPursuit is { } live && live.TargetNetId == target.NetId)
                    unit.HeroPursuit = live with { LastKnownPosition = target.Position };
                _orders.Chase(unit, target);
                return;
            }
            // A hard engage whose target is out of sight keeps walking to the position it was last
            // seen at (the explicit pursuit the postponed-order path re-issues) and takes it again
            // when it reappears. A soft one has no pursuit record: the loss pass raises OnTargetLost
            // and TimerCheckAttack re-decides.
            if (unit.HeroPursuit is { } pursuit)
            {
                if (DistanceSquared(unit.Position, pursuit.LastKnownPosition) <= 100.0)
                {
                    unit.HeroPursuit = null;
                    unit.LastAttackStopPoint = null;
                    _orders.StopMovement(unit);
                }
                else if (!_orders.HasLiveRoute(unit))
                    _orders.Move(unit, pursuit.LastKnownPosition);
            }
            return;
        }
        // The two order walks keep their route; a walk the client lost is planned again.
        if ((state == StateAttackMove || state == StateMove) && !_orders.HasLiveRoute(unit) &&
            unit.AttackMoveGoal is { } goal)
            _orders.Move(unit, goal);
    }

    private bool CanSee(Entity observer, Entity subject) => _visible(observer, subject);

    /// <summary>
    /// Hero.lua's <c>FindTargetInAcR</c>: the nearest eligible enemy inside the champion's
    /// acquisition range. Reconstruction: the native's filter and priority are in no binary we
    /// hold, so the retired machine's rule stands (shipped AcquisitionRange, default 750, plus any
    /// buff payload; <see cref="CombatService.IsAutoAcquirable"/>; nearest first). The engine gates
    /// the script cannot express are here: automatic acquisition is off while a cast gesture holds
    /// the hero, while an uncancellable windup, a forced move or a recall owns it, and for a held
    /// hero (it only fights what it can already reach). An explicit order lifts the gesture hold for
    /// its own scan (playtest B2 AE6 covers automatic acquisition only).
    /// </summary>
    private Entity? Acquire(Entity unit)
    {
        if (!unit.HeroAutoAcquire || !unit.CanAutoAcquireTargets) return null;
        if (_orders.Blocked(unit)) return null;
        if (_elapsed < unit.AutoAttackResumeAt && !_orders.InsideOrder) return null;
        return _orders.Acquire(unit, unit.HeroHold);
    }

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    private HeroAiState StateFor(Entity unit) => _state.TryGetValue(unit.NetId, out var state)
        ? state
        : _state[unit.NetId] = new HeroAiState();

    /// <summary>One hero's script-visible walk and loss bookkeeping (its AI id lives here too).</summary>
    private sealed class HeroAiState
    {
        public double State = StateIdle;
        public Position2? InPosition;
        public Position2? TargetPos;
        public uint? LostTargetId;
        public Position2 LostPosition;
    }
}
