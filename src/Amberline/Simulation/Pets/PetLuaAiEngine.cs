using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The world half of the pet scripts' natives, bound to the services the hand-written pet machine
/// drove (<see cref="TibbersAiSystem"/>, <see cref="SharedCloneService"/>, and the
/// generic <c>BBSpawnMinion</c> path that builds Yorick's ghouls since batch 164). Nothing here
/// decides anything: the script's callbacks
/// and its 0.15 s timers are the whole rule set (acquire, chase, return, teleport, taunt and fear)
/// and these are only the operations it names.
/// The per-pet state the script only points at — the AI state id, the retained target, the
/// "in position" point and the route cache — lives here.
/// </summary>
public sealed class PetLuaAiEngine : ILuaAiPetEngine
{
    // Geometry.ClientWaypoints truncates each axis to a 2-unit grid, so a destination can land up to
    // 2 * sqrt(2) units short of where it was aimed.
    private static readonly double WireRoundingMargin = 2.0 * Math.Sqrt(2.0) + 1e-3;

    /// <summary>The AI states the pet scripts name, over <see cref="AiGlobals"/>' ids.</summary>
    public const double StateFeared = 6.0;
    public const double StateTaunted = 5.0;

    /// <summary>The stop reason the pet scripts pass to <c>TurnOffAutoAttack</c> from fear and <c>HaltAI</c>.</summary>
    public const double StoppedImmediately = 100.0;

    /// <summary>The stop reason the scripts pass to <c>TurnOffAutoAttack</c> when the state is walking.</summary>
    public const double StoppedMoving = 101.0;

    /// <summary>The stop reason Pet.lua passes to <c>TurnOffAutoAttack</c> before a re-target and when nothing is left to fight.</summary>
    public const double StoppedTargetLost = 102.0;

    // The pet half of AiGlobals.Table, named here so the continuous pass and the checks read the
    // same ids the script writes (GetState/SetState/NetSetState carry these numbers).
    public const double StateHalted = 0.0;
    public const double StatePetIdle = 14.0;
    public const double StatePetSpawning = 15.0;
    public const double StatePetHardIdle = 16.0;
    public const double StatePetHardIdleAttacking = 17.0;
    public const double StatePetHardAttack = 18.0;
    public const double StatePetHardMove = 19.0;
    public const double StatePetHardReturn = 20.0;
    public const double StatePetHardStop = 21.0;
    public const double StatePetAttack = 22.0;
    public const double StatePetAttackMove = 23.0;
    public const double StatePetAttackMoveAttacking = 24.0;
    public const double StatePetReturn = 25.0;
    public const double StatePetReturnAttacking = 26.0;
    public const double StatePetHoldPosition = 27.0;
    public const double StatePetHoldPositionAttacking = 28.0;
    public const double StatePetMove = 29.0;

    private readonly WorldState _world;
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly ISpellEffectSink _effects;
    private readonly IRandomSource _random;
    private readonly IMinionVisionService? _vision;
    private readonly Func<Entity, Entity, bool>? _canSee;
    private readonly Dictionary<uint, PetAiState> _state = new();
    private readonly Dictionary<uint, uint> _owners = new();
    private double _elapsed;

    /// <summary>Where a script failure raised by <c>Say</c> goes.</summary>
    public Action<Entity, string>? OnSay { get; set; }

    public PetLuaAiEngine(WorldState world, MovementSystem movement, CombatService combat,
        ISpellEffectSink effects, GameData? data = null, IRandomSource? random = null,
        IMinionVisionService? vision = null, Func<Entity, Entity, bool>? canSee = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        if (!ReferenceEquals(movement.World, world))
            throw new ArgumentException("MovementSystem must belong to the pet world", nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _random = random ?? new SystemRandomSource();
        _vision = vision;
        _canSee = canSee;
    }

    public WorldState World => _world;

    /// <summary>
    /// The route cache the scripts' path requests leave behind, for the checks and the replays: the
    /// pets that hold a path right now. A pet that stood down (<see cref="Stop"/>), never asked for
    /// one or has died is not in it, which is what the hand-written machines' route caches answered
    /// (they dropped the entry at the death itself, before any later tick).
    /// </summary>
    public IReadOnlyDictionary<uint, Position2> LastDestinations =>
        _state.Where(pair => pair.Value.HasDestination && _world.Get(pair.Key)?.Alive == true)
            .ToDictionary(pair => pair.Key, pair => pair.Value.LastDestination);

    /// <summary>
    /// Advances the per-pet bookkeeping the world owns and drops the entries of pets that left it.
    /// The clock the script's timers and the plant run on is the caller's <paramref name="now"/>
    /// (the combat clock), so a check that steps an explicit time and a match tick agree; it never
    /// runs backwards.
    /// </summary>
    public void Tick(double now, double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        if (double.IsFinite(now)) _elapsed = Math.Max(_elapsed, now);
        else _elapsed += dt;
        foreach (var netId in _state.Keys.Where(id => _world.Get(id)?.Alive != true).ToArray())
            _state.Remove(netId);
        foreach (var netId in _owners.Keys.Where(id => _world.Get(id) is null).ToArray())
            _owners.Remove(netId);
    }

    /// <summary>Forgets a pet that died or expired, so a later unit with the same id is not adopted into its state.</summary>
    public void Forget(uint netId)
    {
        _state.Remove(netId);
        _owners.Remove(netId);
    }

    /// <summary>
    /// The clock the script's timers run on: the last <c>now</c> the world stepped this engine with
    /// (<see cref="Tick"/>), so <c>InitTimer</c>/<c>ResetAndStartTimer</c> and the host's per-step
    /// pass agree on one axis.
    /// </summary>
    public double Now => _elapsed;

    public string LostTargetHook => "OnTargetLost";

    // ---- ILuaAiEngine: the shared targeting and combat half ----

    public Entity? FindTargetInAcquisitionRange(Entity unit) => Acquire(unit, unit.Position);

    public Entity? FindTargetInAcquisitionRangeOfGoldRedirectTarget(Entity unit) =>
        // UncontrollablePet.lua and YorickPHPet.lua scan around the hero they fight for, so the pet
        // keeps looking where its master is even when it has walked off. Reconstruction: the name
        // says the scan is the gold-redirect target's; this build keeps one owner field, so that is
        // the owner, and the fallback to the pet's own position is for a pet whose master is gone
        // (the scripts call Die before this point, so it is only reachable from a direct native call).
        Acquire(unit, GetGoldRedirectTarget(unit.NetId)?.Position ?? unit.Position);

    /// <summary>
    /// The shared acquisition ladder: heroes, minions and monsters first (the nearest of them),
    /// then a structure as the last resort — the same order the hand-written pet machine used and
    /// the one <c>CombatService.AcquireTurretTarget</c> takes. The circle is the unit's own
    /// acquisition range around <paramref name="origin"/> (the pet for <c>FindTargetInAcR</c>, the
    /// hero it fights for when the script asks with the gold-redirect target), and the ranking is
    /// nearest to the pet. A ward is never taken (owner decision, 18 Oct 2026,
    /// <see cref="CombatService.IsAutoAcquirable"/>); a structure is the last resort, so a pet
    /// pushing a lane attacks the tower only with nothing else left in range.
    /// </summary>
    private Entity? Acquire(Entity unit, Position2 origin)
    {
        var radiusSquared = unit.AcquisitionRange * unit.AcquisitionRange;
        Entity? preferred = null;
        var preferredDistance = double.PositiveInfinity;
        Entity? structure = null;
        var structureDistance = double.PositiveInfinity;
        foreach (var candidate in _world.Entities.Values)
        {
            if (!CombatService.IsAutoAcquirable(candidate) || !IsEligibleTarget(unit, candidate) ||
                DistanceSquared(origin, candidate.Position) > radiusSquared)
                continue;
            var distance = DistanceSquared(unit.Position, candidate.Position);
            if (candidate.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Monster)
            {
                if (distance < preferredDistance)
                {
                    preferred = candidate;
                    preferredDistance = distance;
                }
            }
            else if (distance < structureDistance)
            {
                structure = candidate;
                structureDistance = distance;
            }
        }
        return preferred ?? structure;
    }

    /// <summary>The engine's target-acquisition gate: an enemy unit a pet may fight, seen and in one piece.</summary>
    private bool IsEligibleTarget(Entity unit, Entity candidate) =>
        candidate != unit && candidate.Alive && !candidate.Protected &&
        WorldState.IsEnemy(unit, candidate) && _combat.IsTargetable(candidate, unit) &&
        CanSee(unit, candidate) &&
        !candidate.AnyBuffOrStatus(buff => buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable) &&
        candidate.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Monster or
            UnitType.Turret or UnitType.Structure;

    private bool CanSee(Entity unit, Entity candidate) =>
        _canSee?.Invoke(unit, candidate) ?? (_vision?.HasBrushVision(unit.Team, candidate) ?? true);

    public void SetTarget(Entity unit, Entity? target)
    {
        var state = StateFor(unit);
        // PET-01: a target transition retires only unlaunched attacks (launched hits retain their own
        // lifetime) and drops the route of the fight it ends, in that order: the client is told its
        // swing is over (0x39) before any replacement 0xC1 reaches it. Setting the same target again
        // costs nothing, so a repeated attack order keeps the swing it is already winding up.
        if ((state.Retained ?? unit.AttackTarget) is uint oldTarget &&
            oldTarget != (target?.NetId ?? 0u))
        {
            _combat.CancelPendingWindups(unit.NetId);
            if (unit.Created && _effects.IsReferenceReady(unit)) _effects.SendStopAttack(unit);
            state.HasDestination = false;
            StopIfMoving(unit);
        }
        state.Retained = target?.NetId;
        unit.AttackTarget = target?.NetId;
        Remember(unit);
        if (target is not null) Remember(target);
    }

    public Entity? CurrentTarget(Entity unit)
    {
        // The object the AI took, not the wire id: the combat tick retires a dead target's id and
        // the script still answers for the target it was given (its OnTargetLost carries it).
        var id = HeldTargetId(unit);
        return id is uint netId ? _world.Get(netId) : null;
    }

    public uint? HeldTargetId(Entity unit) =>
        StateFor(unit).Retained ?? unit.AttackTarget;

    public Entity? TauntTarget(Entity unit) => PetControl.TauntTarget(_world, unit);

    public bool TargetStillValid(Entity unit, Entity target) =>
        // The engine's target-lost test for a pet: death, untargetability or a side change.
        // Leaving attack range is not a loss (Pet.lua's own scans decide that) - PET-02 keeps the
        // engaged target at any distance - but an unseen one is. The hand-written machine re-picked
        // its target through the same eligibility test acquisition used (sight among it), the
        // jungle engine drops a hidden target too, and the hero rule reads "a hidden target cannot
        // remain an attack target": sight is a loss here for the same reason.
        _combat.CanAttackIgnoringRange(unit, target) && CanSee(unit, target);

    public bool TargetInAttackRange(Entity unit, Entity? target) =>
        target is not null && CombatService.InRange(unit, target);

    public bool TargetInCancelAttackRange(Entity unit, Entity? target) =>
        target is not null && _combat.InCommittedAttackRange(unit, target);

    public void TurnOnAutoAttack(Entity unit, Entity? target)
    {
        // The script always passes GetTarget(); re-affirming it is what SetTarget already did.
        var pet = StateFor(unit);
        pet.AutoAttack = true;
        if (target is null) return;
        SetTarget(unit, target);
        // The swing starts here rather than at the next 0.15 s scan: the script turns the attack on
        // from the scan that found the target in attack range (Pet.lua TimerFindEnemies' tail).
        if (unit.AttackCooldown <= 0.0) PetControl.QueueAttack(_combat, unit, target);
    }

    public void TurnOffAutoAttack(Entity unit, double stopReason)
    {
        // The stop reason (STOPREASON_*) is the engine's own; nothing in this build sends it.
        // The attack ends and its clients are told, whatever the reason, the way the lane minions' native does
        // (MinionAiNatives.NativeTurnOffAutoAttack): the stock client runs a pet as an obj_AI_Minion, whose attack state starts the
        // last attack again every attack period and erases the path it follows (UpdatePimpl), and only a 0x39 (or the unit's own death)
        // ends that state. Pet.lua's TimerFindEnemies turns the attack off with STOPREASON_MOVING once the target is out of its cancel
        // range and leaves the chase to the native; a silent stop here kept every client swinging Tibbers at a target he no longer
        // reached, rooted or walking after it (batch 716). CombatPackets.StopAttack sends the packet only to a client still in that state.
        // PET-01: a re-targeting (STOPREASON_TARGET_LOST, before every ORDER_ATTACKTO/ORDER_PETHARDATTACK/ORDER_PETHARDSTOP) keeps the
        // swing it is winding up, so a repeated attack order against one target lands its hits; <see cref="SetTarget"/> retires it when
        // the target actually changes. STOPREASON_IMMEDIATELY (fear, HaltAI) drops it, and so does STOPREASON_MOVING, which Pet.lua
        // passes only for a target outside the committed range the combat tick drops a windup at anyway: dropped first, the stop tells
        // the client not to play it (0x10) rather than forcing the cast frame of a hit the server then drops.
        // The retained target survives the stop (Turret.luaobj's classified difference 2 is the same shape): the native state chases
        // it, and the states that must not keep a target take their replacement from <see cref="SetStateAndCloseToTarget"/> in the
        // same callback.
        StateFor(unit).AutoAttack = false;
        if (stopReason != StoppedTargetLost) _combat.CancelPendingWindups(unit.NetId);
        if (unit.Created && _effects.IsReferenceReady(unit)) _effects.SendStopAttack(unit);
    }

    public double AiState(Entity unit) => StateFor(unit).State;

    /// <summary>
    /// A plain state write (<c>SetState</c>/<c>NetSetState</c>) ends the route unless the new state
    /// is one that walks: Pet.lua drops into <c>AI_PET_IDLE</c>, <c>AI_PET_HARDIDLE</c> and the
    /// stand states from its timers and expects the walk to stop with them (the hand-written machine
    /// stopped on every such transition). A stop is published only for a pet that is actually
    /// walking, so a scan that rewrites the same state costs no 0xC1.
    /// </summary>
    public void SetAiState(Entity unit, double state)
    {
        var pet = StateFor(unit);
        pet.State = state;
        if (!KeepsRoute(state)) StopIfMoving(unit);
    }

    // ---- ILuaAiOwnedEngine: the ownership half the jungle shares ----

    public Entity? GetOwner(uint netId) => Resolve(OwnerOf(netId));

    public Entity? GetGoldRedirectTarget(uint netId) => Resolve(OwnerOf(netId));

    public Position2 GetFearLeashPoint(Entity unit) => unit.Position;

    public Position2 MakeWanderPoint(Entity unit, Position2 origin, double distance)
    {
        // Reconstruction: MakeWanderPoint is native; keep the uniform angle/radius draw the other
        // fear owners (NeutralLuaAiEngine, MinionAiSystem, ShacoBoxService) use.
        var angle = _random.Uniform(0.0, Math.PI * 2.0);
        var radius = _random.Uniform(0.0, Math.Max(0.0, distance));
        return origin + new Position2(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    // ---- ILuaAiPetEngine ----

    /// <summary>
    /// The state a script callback installs. Entering a different state starts a walk of its own: it clears the pet's last
    /// destination, so the state's first request plans a fresh path to whatever the new state names.
    /// </summary>
    private static void EnterState(PetAiState pet, double state)
    {
        if (pet.State != state) pet.HasDestination = false;
        pet.State = state;
    }

    /// <summary>
    /// <c>SetStateAndCloseToTarget(state, object)</c>: the state, then the walk toward the object it
    /// names. Which of the three the object is follows the state, because the script passes three
    /// different kinds of object through this one native: the attack target of an engaging state
    /// (kept as the retained target and closed on at attack reach), the goal of a follow state (the
    /// owner, walked to as an ally), and the pet itself from a stand state
    /// (<c>SetStateAndCloseToTarget(AI_PET_HARDSTOP, me)</c>, <c>…(AI_PET_HOLDPOSITION, me)</c>),
    /// which keeps no target and does not walk.
    /// </summary>
    public void SetStateAndCloseToTarget(Entity unit, double state, Entity? target)
    {
        var pet = StateFor(unit);
        EnterState(pet, state);
        if (IsEngagingState(state))
        {
            pet.Goal = null;
            if (target is null) return;
            SetTarget(unit, target);
            CloseToTarget(unit, target, reason: "pet-engage");
            return;
        }
        if (IsFollowState(state))
        {
            // The named object is the walk goal, not a target: taking the owner as the retained
            // target would report him lost on every step (TargetStillValid requires an enemy). The
            // state keeps no fight target either. Reconstruction: the script only names the state and
            // the goal, so the native's target handling is the hand-written machines' — every order
            // but the hard attack cleared the target, and "MOVE does not acquire while owner walks"
            // read that as the walk order dropping combat.
            pet.Goal = target?.NetId;
            LeaveFight(unit);
            if (target is not null) Move(unit, target.Position, reason: "pet-follow");
            return;
        }
        pet.Goal = null;
        SetTarget(unit, null);
        // The stand states reach this native from Pet.lua's OnOrder alone (ORDER_PETHARDSTOP and
        // ORDER_HOLD name the pet itself), so this is the owner's stop and its place always goes
        // out: the clients may still be walking the pet to the end of the route the order replaces,
        // and a body that stands still on the server is not a body they have been told to stop. A
        // stop that repeats one they already have goes out too: retail re-sends the path of each ask
        // (owner decision, 25 October 2026, batch 229), so three holds at one place are three stops -
        // unlike a plain state write from a scan, below, which stays silent while nothing walks.
        Stop(unit);
    }

    /// <summary>
    /// The states whose named object is the unit the pet fights: they keep it as the retained
    /// target (<see cref="HeldTargetId"/>) and close on it. <see cref="StatePetHardIdleAttacking"/>
    /// and <see cref="StatePetHoldPositionAttacking"/> engage without walking, so the script reaches
    /// them through <c>NetSetState</c> + <c>SetTarget</c> instead.
    /// </summary>
    public static bool IsEngagingState(double state) => state is StatePetAttack or StatePetHardAttack
        or StateTaunted or StatePetReturnAttacking or StatePetAttackMoveAttacking;

    /// <summary>The states that walk toward the object named with them (the owner), following him.</summary>
    public static bool IsFollowState(double state) => state is StatePetMove or StatePetAttackMove
        or StatePetReturn or StatePetHardReturn;

    /// <summary>The states that keep a route: the follow states and the hard move to the in-position point.</summary>
    private static bool KeepsRoute(double state) => IsFollowState(state) || state == StatePetHardMove;

    /// <summary>The engaging states that swing in place rather than closing on the target.</summary>
    private static bool HoldsPosition(double state) =>
        state is StatePetHardIdleAttacking or StatePetHoldPositionAttacking;

    /// <summary>
    /// The states Pet.lua's <c>OnOrder</c> swallows most orders in: the pet hard states the hard
    /// orders leave behind, the park among them (<see cref="StatePetHardIdle"/>).
    /// </summary>
    public static bool IsHardState(double state) => state is StatePetHardAttack or StatePetHardMove
        or StatePetHardIdle or StatePetHardIdleAttacking or StatePetHardReturn or StatePetHardStop;

    /// <summary>
    /// The state a pet's script left it in (<c>GetState</c>), for the checks: null until the unit
    /// has been driven, so a pet this engine never stepped is distinguishable from one standing in
    /// <see cref="StateHalted"/>.
    /// </summary>
    public double? StateOf(Entity unit) =>
        _state.TryGetValue(unit.NetId, out var state) ? state.State : null;

    /// <summary>The target a pet last stood to attack (<see cref="HoldsPlant"/>), for the checks.</summary>
    public uint PlantedTargetOf(Entity unit) =>
        _state.TryGetValue(unit.NetId, out var state) ? state.PlantedTarget : 0u;

    public void SetStateAndMove(Entity unit, double state, Position2 point)
    {
        var pet = StateFor(unit);
        EnterState(pet, state);
        // Pet.lua names this native only from OnFearBegin and TimerFeared, always with AI_FEARED:
        // the wander is a fear displacement, not an order the pet may keep following.
        if (state == StateFeared)
        {
            WanderFeared(unit, point);
            return;
        }
        Move(unit, point, reason: "pet-wander");
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
        var fear = unit.Buffs.Where(buff => buff.Type == BuffType.Fear || buff.Name == "Fear" ||
            buff.Payload.FearOrigin is not null).LastOrDefault();
        var duration = Math.Min(Math.Max(0.0, fear?.ExpiresAt ?? 1.0), 1.0);
        SendWaypoints(unit, _movement.StartFearWander(unit, destination, duration, fear?.CasterNetId ?? 0u));
    }

    public void SetStateAndMoveInPos(Entity unit, double state)
    {
        var pet = StateFor(unit);
        EnterState(pet, state);
        LeaveFight(unit);
        // An ordered move is honoured wherever it lands: a repeat of the point the pet already walks to is the same request, and
        // nothing else is held back (retail re-paths on ask, no route tolerance).
        Move(unit, pet.InPosition, reason: "pet-inpos");
    }

    /// <summary>
    /// Reconstruction: the fight a walk leaves behind. Pet.lua cancels the swing itself only where it
    /// re-targets (<c>TurnOffAutoAttack(TARGET_LOST)</c> before <c>ORDER_ATTACKTO</c> and
    /// <c>ORDER_PETHARDATTACK</c>) and before <c>ORDER_PETHARDSTOP</c>; the walking natives are assumed to stop it (unrecovered)
    /// as part of walking, which is where the hand-written machines did it (every order but the hard
    /// attack) and what the wire path pins: the 0x39 actor stop precedes the replacement 0xC1
    /// route, and a walk order drops the target with it.
    /// </summary>
    private void LeaveFight(Entity unit)
    {
        TurnOffAutoAttack(unit, StoppedMoving);
        SetTarget(unit, null);
    }

    public void SetLastPosPointWithObj(Entity unit, Entity? source)
    {
        if (source is null) return;
        StateFor(unit).InPosition = source.Position;
    }

    public double DistanceBetweenObjectAndInPosSq(Entity? unit) =>
        unit is null ? 0.0 : DistanceSquared(unit.Position, StateFor(unit).InPosition);

    public double DistanceBetweenObjects(Entity? first, Entity? second) =>
        first is null || second is null ? 0.0 :
            Math.Sqrt(DistanceSquared(first.Position, second.Position));

    public double GetPetReturnRadius(Entity? unit) =>
        // The unit's mPetReturnRadius ("PetReturnRadius = " in the debug dump). The shared obj_AI_Base
        // constructor sets it from ai_DefaultPetReturnRadius (Map1 Constants.var 200) and only
        // BBSetPetReturnRadius changes it, which no shipped script calls (batch 310), so every pet reads the default.
        Amberline.Data.GameData.Default.LevelConstants.Values["ai_DefaultPetReturnRadius"];

    public bool GetCanMove(Entity unit) => !IsStunned(unit) && !IsRooted(unit) && !Displaced(unit);

    public bool IsMoving(Entity unit) => HasMovingRoute(unit);

    public bool IsNetworkLocal(Entity unit) => true;

    public bool OwnerIsMoving(Entity unit) =>
        GetOwner(unit.NetId) is { } owner && (owner.Waypoints.Count > 0 || owner.Target is not null);

    /// <summary>
    /// <c>SetActorPositionFromObject</c>: the leash blink to the unit it names. Pet.lua's
    /// <c>TimerScanDistance</c> calls it the moment the owner is past TELEPORT_DISTANCE without
    /// asking whether the pet can move (YorickPHPet's own scan does ask); the engine answers the
    /// displacement rule for both, so a forced move of the owner's enemy is never overridden by the
    /// leash (<see cref="Teleport"/>).
    /// </summary>
    public void SetActorPositionFromObject(Entity unit, Entity? from)
    {
        if (from is null) return;
        Teleport(unit, from.Position);
    }

    public void Die(Entity unit, double damageSource) => _combat.Kill(unit, 0);

    public void Say(Entity unit, string message) => OnSay?.Invoke(unit, message);

    public void SetLastPosPoint(Entity unit, Position2 point) => StateFor(unit).InPosition = point;

    // ---- the continuous half: what the state does between the script's callbacks ----

    /// <summary>
    /// Reconstruction: the per-step half of the native pet state machine (unrecovered). Pet.lua decides at its
    /// callbacks and its 0.15 s timers (which target, which state, when the swing is on) and the
    /// native state closes on that target and repeats the swing by itself in between — this is that
    /// stand-in over the services the hand-written pet machine drove. It is a function of the state
    /// alone: the engaging states fight their retained target, the follow states walk to their goal
    /// object (the owner), <see cref="StatePetHardMove"/> keeps the route the order installed, and
    /// the states that stand keep swinging at the target they hold.
    /// </summary>
    public void Continue(Entity unit)
    {
        if (IsStunned(unit) || Displaced(unit)) return;
        var pet = StateFor(unit);
        if (IsEngagingState(pet.State) || HoldsPosition(pet.State))
        {
            if (HeldTargetId(unit) is uint id && _world.Get(id) is { } target) Swing(unit, target);
            return;
        }
        if (SwingsInPlace(pet.State) && HeldTargetId(unit) is uint held &&
            _world.Get(held) is { } heldTarget)
            Swing(unit, heldTarget);
    }

    /// <summary>
    /// The native's order half, run only on the steps the shipped script's timers run: Pet.lua's TimerScanDistance and
    /// TimerFindEnemies, both 0.15 s, armed by OnInit (<c>PetAiService.Tick</c> calls this when the host says one fired). It
    /// closes on the held target (a stop where the pet still walks, a route toward the target's ring when it does not), separates
    /// an overlapping pet, and walks a follow to its owner. Between scans the route in flight is walked by the movement system,
    /// and the script's own orders come through its natives as the script calls them. Batch 229 follow-up (coordinator, 25 October
    /// 2026): this ran on every step, which sent a stop on every step in reach.
    /// </summary>
    public void Scan(Entity unit)
    {
        if (IsStunned(unit) || Displaced(unit)) return;
        var pet = StateFor(unit);
        if (IsEngagingState(pet.State) || HoldsPosition(pet.State))
        {
            if (!HoldsPosition(pet.State) && !IsRooted(unit) && HeldTargetId(unit) is uint id &&
                _world.Get(id) is { } target)
                CloseToTarget(unit, target, reason: "pet-engage");
            return;
        }
        // Pet.lua walks a hard move to the in-position point the order named (SetStateAndMoveInPos) and parks it there; the clearance
        // below re-aims a stand state or a follow state only, so that route is not diverted at the owner. StatePetHardMove keeps its own goal.
        if (pet.State != StatePetHardMove && SeparateFromOwner(unit, pet)) return;
        if (IsFollowState(pet.State) && pet.Goal is uint goalId && _world.Get(goalId) is { } goal && !IsRooted(unit))
            Move(unit, goal.Position, reason: "pet-follow");
    }

    /// <summary>
    /// Playtest B3 #7: Pet.lua's 0.15 s timers only pick the target and the route; the native state
    /// repeats the swing at the current in-range target every step. The script's own TurnOn/TurnOff
    /// therefore only say whether a scan wants combat at all — a target in attack reach swings either
    /// way (the CanAttack bit and the attack range are the gates <see cref="PetControl.QueueAttack"/>
    /// applies), and the one that walks does not.
    /// </summary>
    private void Swing(Entity unit, Entity target)
    {
        if (unit.AttackCooldown <= 0.0 && !HasMovingRoute(unit))
            PetControl.QueueAttack(_combat, unit, target);
    }

    /// <summary>
    /// The states whose native half repeats that swing while the pet stands: the states Pet.lua's
    /// scans acquire from (AI_PET_IDLE, the AI_PET_HARDIDLE park of a hard move that got nowhere,
    /// AI_PET_HOLDPOSITION and the AI_PET_SPAWNING birth) and the engaging states above. The walk
    /// states drop the fight with their route, AI_PET_HARDSTOP is an ALT stop that never takes a
    /// target at all, AI_FEARED is running away (OnFearBegin turns the attack off with
    /// STOPREASON_IMMEDIATELY) and AI_HALTED is the HaltAI stop.
    /// </summary>
    private static bool SwingsInPlace(double state) =>
        IsEngagingState(state) || HoldsPosition(state) ||
        state is StatePetIdle or StatePetSpawning or StatePetHardIdle or StatePetHoldPosition;

    /// <summary>
    /// Reconstruction: physical overlap recovery, the half of the retired pet machines' spacing rules
    /// that PET-03 kept. Pet.lua names walk goals and says nothing about two bodies in one place, so
    /// the separation is taken to be the native pathing's (unrecovered): a pet its owner is standing in steps to a point clear
    /// of him (the hand-written <c>SharedCloneService.OwnerSafePoint</c>, kept at BodyClearance plus
    /// the waypoint quantization). The trigger is the mover's own contact distance in a stand state
    /// (a hero owner is a thin mover and may brush a held pet without an overlap) and the full body
    /// width while following, which is where the old machines drew the same two lines. There is no
    /// voluntary owner ring: a pet that is merely near its owner is left alone.
    /// </summary>
    private bool SeparateFromOwner(Entity unit, PetAiState pet)
    {
        // A forced move is not the pet's own walk: the retired machines left a displaced clone
        // alone and so does this (a fear wander is a forced move too).
        if (unit.ForcedMove is not null) return false;
        if (IsRooted(unit)) return false;
        if (GetOwner(unit.NetId) is not { } owner) return false;
        var clearance = IsFollowState(pet.State)
            ? _movement.BodyClearance(unit, owner)
            : _movement.ContactClearance(owner, unit);
        if (DistanceSquared(unit.Position, owner.Position) >= clearance * clearance) return false;
        // The ring point walks with the owner every step, and each new point re-plans (no route tolerance, retail re-paths on ask).
        Move(unit, OwnerSafePoint(unit, owner), reason: "pet-overlap");
        return true;
    }

    /// <summary>Where a pet overlapped by its owner stands instead: clear of him along the line away from him.</summary>
    private Position2 OwnerSafePoint(Entity unit, Entity owner)
    {
        var away = unit.Position - owner.Position;
        var distance = Math.Sqrt(away.X * away.X + away.Z * away.Z);
        // Leave room for the two-unit waypoint quantization: the exact ring point can round inside
        // the body and never resolve.
        var clearance = _movement.BodyClearance(unit, owner) + 3.0;
        if (distance < 1e-9)
            return owner.Position - new Position2(owner.Facing.X * clearance, owner.Facing.Z * clearance);
        return owner.Position + new Position2(away.X / distance * clearance, away.Z / distance * clearance);
    }

    // ---- the movement mechanics the natives above name ----

    /// <summary>
    /// Reconstruction: the wire step of the native path requests (unrecovered). Retail's native closes on its target by itself and
    /// re-paths whenever its script or native state asks (owner decision, 25 October 2026): a request for a point the pet is not
    /// already walking to re-plans and sends a route, with no distance tolerance (batch 229 removed the follow, chase and clone
    /// tolerances and the 1-unit dedupe). The one dedupe kept is the identical point: the follow and chase steps re-ask every step
    /// with the point they hold, and the same request is not a new one. The walk to the target's own body ring is this stand-in's.
    /// </summary>
    public void Move(Entity unit, Position2 destination, Entity? attackTarget = null, string reason = "pet-move")
    {
        // A unit its own buff holds in place (SetCanMove(false) is the payload flag the root rules
        // read) plans no path: Wukong's decoy is frozen by its shipped MonkeyKingDecoyClone buff that
        // way, so Pet.lua's scans run and decide nothing reaches the client.
        if (IsStunned(unit) || Displaced(unit) || IsRooted(unit)) return;
        // A walk to the point the pet already stands on is not a walk. The wire click that named it
        // rounds to within a couple of units of the actor, and the engine planner's straight walk keeps
        // only the start for an end within 5 units (BuildNavigationPath): no route, a stop, so
        // IsMoving() turns false and Pet.lua TimerScanDistance gets its park ("not IsMoving() and
        // state == AI_PET_HARDMOVE -> NetSetState(AI_PET_HARDIDLE)").
        if (DistanceSquared(unit.Position, destination) <= WireRoundingMargin * WireRoundingMargin)
        {
            StopIfMoving(unit);
            return;
        }
        var pet = StateFor(unit);
        var wasMoving = unit.Waypoints.Count > 0 || unit.Target is not null;
        if (wasMoving && pet.HasDestination && pet.LastDestination == destination) return;
        var unchanged = pet.HasDestination && pet.LastDestination == destination;
        // The request is what the pet was asked for: a flank sample below is how this stand-in answered it, not a new request.
        var requested = destination;
        IReadOnlyList<Position2> path;
        if (attackTarget is { UnitType: UnitType.Turret or UnitType.Structure })
        {
            // A structure is approached through its footprint (the lane minions' rule, which the
            // ghouls' chase kept): a route to a point at reach from its centre ends inside the body.
            var reach = CombatService.AttackReach(unit, attackTarget);
            path = _movement.CommitMoveOrder(unit, _movement.ApproachWithin(unit, attackTarget,
                Math.Max(0.0, reach - 6.0), reach, RouteVisibility(unit)));
        }
        else
        {
            path = _movement.SetMoveOrder(unit.NetId, destination, RouteVisibility(unit));
            if (attackTarget is not null && !_movement.ApproachPathUsable(path, unit, attackTarget,
                    CombatService.AttackReach(unit, attackTarget)))
            {
                // A blocked radial approach must try the other sides of the attack circle; copied
                // owner attacks still chase. ponytail: 16 flank samples, matching the retired pet
                // machine; replace with a continuous attack-circle search if narrower gaps are
                // demonstrated. A candidate counts only if its route ends inside the target's attack
                // circle (the admission the minion chase got from playtest B4 #15): one that ends
                // outside cannot deliver a swing from anywhere on it. So the samples run while the
                // route is rejected and not merely while it is degenerate, and a rejected route
                // survives none of them - what the planner left committed is cleared the way a failed
                // hero navigation is (playtest B4).
                var reach = Math.Max(0.0, CombatService.AttackReach(unit, attackTarget) - 8.0);
                var angle = Math.Atan2(unit.Position.Z - attackTarget.Position.Z,
                    unit.Position.X - attackTarget.Position.X);
                for (var sample = 1; sample <= 16 && !_movement.ApproachPathUsable(path, unit,
                         attackTarget, CombatService.AttackReach(unit, attackTarget)); sample++)
                {
                    var offset = ((sample + 1) / 2) * (Math.PI / 8.0) *
                        (sample % 2 == 1 ? 1.0 : -1.0);
                    destination = attackTarget.Position + new Position2(
                        Math.Cos(angle + offset) * reach, Math.Sin(angle + offset) * reach);
                    path = _movement.SetMoveOrder(unit.NetId, destination, RouteVisibility(unit));
                }
                if (!_movement.ApproachPathUsable(path, unit, attackTarget,
                        CombatService.AttackReach(unit, attackTarget)))
                {
                    unit.Waypoints.Clear();
                    unit.Target = null;
                    path = [];
                }
            }
        }
        pet.LastDestination = requested;
        pet.HasDestination = true;
        // Retry planning so changed geometry can recover, but do not resend the same stationary
        // failed order at every 0.15 s scan. Reconstruction: what retail's native sends for a request
        // that found no route is unrecovered; this keeps the silence a stationary pet had before.
        if (unchanged && !wasMoving && path.Count <= 1) return;
        if (path.Count > 0) SendWaypoints(unit, path);
        else SendStop(unit);
    }

    /// <summary>
    /// Reconstruction: stand-in for the native <c>SetStateAndCloseToTarget</c>'s own path request, which is unrecovered. The
    /// stop point (attack reach less the wire rounding error) and the hold inside reach or in a
    /// committed windup are the emulator's choice; the script only names the state and the target.
    /// </summary>
    public void CloseToTarget(Entity unit, Entity target, string reason)
    {
        if (IsStunned(unit) || Displaced(unit)) return;
        var pet = StateFor(unit);
        if (_combat.CanAttack(unit, target))
        {
            // Where the pet stood to attack is remembered so the plant below can hold it for a
            // target that steps just out of reach.
            pet.PlantedTarget = target.NetId;
            pet.PlantedAt = _elapsed;
            StopIfMoving(unit);
            return;
        }
        if (HoldsPlant(unit, target)) return;
        if (unit.Speed <= 0.0)
        {
            StopIfMoving(unit);
            return;
        }
        var distance = Math.Sqrt(DistanceSquared(unit.Position, target.Position));
        // Reconstruction: the stop threshold is attack reach less the wire rounding error (the native's own stop point is unrecovered). Eight units
        // inside admission accommodates the two-unit waypoint quantization, which would otherwise
        // leave the pet outside AttackReach for good.
        var reach = Math.Max(0.0, CombatService.AttackReach(unit, target) - 8.0);
        if (distance <= reach || _combat.HasCommittedWindup(unit, target))
        {
            StopIfMoving(unit);
            return;
        }
        // The chase aims where the stock client's AttackTo plan aims: at the target's position (the base unit's order step
        // hands it to the client's path builder, MovementSystem.PlanChase), and the planner's end-cell snap (SetToNearestGetToAbleCell,
        // moves the end off the occupied target to the nearest free cell). CanAttack stops the pet in range. The goal does not depend on
        // where the pet stands, so while the target stands still each 0.15 s scan asks for the point the pet already walks to, which
        // is no new request; a point computed from the pet's own position moved by a fraction of a unit at every scan and re-planned
        // the same route each time (batch 229 open question 1: 9 identical routes in 1.5 s from one ravenous ghoul, batch 236).
        Move(unit, target.Position, target, reason: reason);
    }

    /// <summary>
    /// Reconstruction: a pet that stopped to attack stays where it is while its target is beyond its
    /// reach by no more than one client hop (<see cref="PetOrders.ClientHop"/>), instead of walking
    /// after it the tick the target steps out. Pet.lua turns the auto attack on at
    /// TargetInAttackRange and off only outside TargetInCancelAttackRange, a larger range whose size
    /// is unrecovered. Map1 ships ar_StopAttackRangeModifier (100 units, the leash the combat
    /// service gives a committed swing) and ar_ClosingAttackRangeModifier (300), but nothing ties
    /// either to that call; a 100-unit band cut the ghouls' and the revenant's attacks by a sixth in
    /// a headless trial, a hop did not. The emulator decided every tick, so a target walking away at
    /// the edge of reach made a ghoul or a revenant alternate stop and route at up to 10 Hz: 40
    /// percent of their 0xC1 on the real client, each hard-snapping the actor. Reconstruction of the
    /// band and of the 0.5 s cap, so a target that stops just out of reach is still walked to.
    /// </summary>
    private bool HoldsPlant(Entity pet, Entity target) =>
        StateFor(pet) is { } state && state.PlantedTarget == target.NetId &&
        _elapsed - state.PlantedAt <= PetOrders.PlantHoldSeconds &&
        pet.Waypoints.Count == 0 && pet.Target is null &&
        DistanceSquared(pet.Position, target.Position) <=
        Math.Pow(CombatService.AttackReach(pet, target) + PetOrders.ClientHop(pet), 2.0);

    /// <summary>Ends the route of a pet that is walking, without republishing a stop for one that is not.</summary>
    private void StopIfMoving(Entity unit)
    {
        if (unit.Waypoints.Count > 0 || unit.Target is not null) Stop(unit);
    }

    public void Stop(Entity unit)
    {
        var pet = StateFor(unit);
        unit.Waypoints.Clear();
        unit.Target = null;
        pet.HasDestination = false;
        SendStop(unit);
    }

    private void SendWaypoints(Entity unit, IReadOnlyList<Position2> path)
    {
        if (!unit.Created || !_effects.IsReferenceReady(unit)) return;
        _effects.SendWaypoints(unit, path, _world.Clock.SyncId());
    }

    /// <summary>A stop's one-point path, installed on receipt: stopped and seated on the anchor at once.</summary>
    private void SendStop(Entity unit)
    {
        if (!unit.Created || !_effects.IsReferenceReady(unit)) return;
        _effects.SendWaypoints(unit, _movement.InstallStop(unit), _world.Clock.SyncId());
    }

    private void Teleport(Entity unit, Position2 position)
    {
        // Pet.lua TimerScanDistance: SetActorPositionFromObject, then NetSetState(AI_PET_IDLE). The
        // displacement rule is the engine's, not the script's: a pet a script is displacing (a pull,
        // knock-back or knock-up) is held like a stunned one and takes no decision, so the blink
        // waits for the dash instead of replacing it and lands on a later scan.
        if (IsStunned(unit) || Displaced(unit)) return;
        var pet = StateFor(unit);
        _combat.CancelPendingWindups(unit.NetId);
        unit.Position = position;
        // The teleport is followed by a one-point path, whose install stops the pet and seats it on
        // the path's point, its lattice anchor, at once.
        var stop = _movement.InstallStop(unit);
        _movement.Teleported(unit);
        unit.AttackTarget = null;
        pet.Retained = null;
        pet.Goal = null;
        pet.AutoAttack = false;
        pet.PlantedTarget = 0u;
        pet.HasDestination = false;
        unit.AnchorPath = [unit.Position];
        unit.LastAnchorTime = _elapsed;
        unit.TeleportId++;
        if (unit.Created && _effects.IsReferenceReady(unit))
        {
            _effects.SendStopAttack(unit);
            _effects.SendTeleport(unit, unit.Position, unit.TeleportId,
                _world.Clock.SyncId(), staging: null);
            _effects.SendWaypoints(unit, stop, _world.Clock.SyncId());
        }
    }

    private Func<Entity, bool>? RouteVisibility(Entity unit) =>
        _canSee is null ? null : other => _canSee(unit, other);

    /// <summary>
    /// A pet that a script is displacing (a pull, knock-back or knock-up) is held like a stunned one:
    /// it takes no command and makes no decision, so no route or stop reaches the client while its
    /// dash runs (a plain 0xC1 replaces the dash path). Pet.lua starts
    /// again from AI_PET_IDLE once the unit can move (OnCanMove).
    /// </summary>
    private static bool IsStunned(Entity entity) =>
        entity.ChampionState?.BlocksActions == true || entity.AnyBuffOrStatus(buff => buff.DisablesActions) ||
        entity.ForcedMove is { Effect: not "Fear" };

    private static bool IsRooted(Entity entity) =>
        entity.AnyBuffOrStatus(buff => buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    private static bool Displaced(Entity unit) => unit.ForcedMove is { Effect: not "Fear" };

    /// <summary>Movement consumes the current route anchor as it advances; a singleton at the actor's
    /// own position is a stationary stop anchor.</summary>
    private static bool HasMovingRoute(Entity unit) =>
        unit.Target is not null || unit.Waypoints.Count > 1 ||
        unit.Waypoints.Count == 1 && DistanceSquared(unit.Position, unit.Waypoints[0]) > 1e-12;

    private PetAiState StateFor(Entity unit)
    {
        if (!_state.TryGetValue(unit.NetId, out var existing))
            _state[unit.NetId] = existing = new PetAiState();
        return existing;
    }

    private Entity? Resolve(uint netId) => netId == 0u ? null : _world.Get(netId);

    /// <summary>
    /// Reconstruction: GetOwner and GetGoldRedirectTarget are separate natives but this build keeps
    /// one owner field (the hero a pet's kills credit is its owner), so both answer it.
    /// </summary>
    private uint OwnerOf(uint netId) =>
        _world.Get(netId) is { } unit ? unit.OwnerNetId :
        _owners.TryGetValue(netId, out var owner) ? owner : 0u;

    /// <summary>
    /// Keeps a unit's owner after the unit has left the world: a lost target is already gone by the
    /// time <c>OnTargetLost</c> reads its owner, and a pet that died is read by the next callback.
    /// </summary>
    private void Remember(Entity unit) => _owners[unit.NetId] = unit.OwnerNetId;

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    private sealed class PetAiState
    {
        public double State;
        /// <summary>The fight target the script kept (SetTarget / an engaging state's object).</summary>
        public uint? Retained;
        /// <summary>The object a follow state walks to (its owner); not a target.</summary>
        public uint? Goal;
        /// <summary>Whether the script's last <c>TurnOnAutoAttack</c>/<c>TurnOffAutoAttack</c> left
        /// the auto-attack on. The native state does not gate its swing on it (playtest B3 #7: the
        /// swing repeats at the current in-range target every step); it is what the script said.</summary>
        public bool AutoAttack;
        /// <summary>Where and when the pet last stood to attack (<see cref="HoldsPlant"/>).</summary>
        public uint PlantedTarget;
        public double PlantedAt;
        public Position2 InPosition;
        public bool HasDestination;
        public Position2 LastDestination;
    }
}
