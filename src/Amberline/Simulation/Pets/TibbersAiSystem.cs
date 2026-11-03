using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The lane-less units a <c>BBSpawnMinion</c> gave <c>idle.lua</c>, <c>Aggro.lua</c> (batch 163) or
/// <c>Minion.lua</c> (Heimerdinger's turrets, batch 339): they run their shipped chunks on
/// <see cref="Host"/> over the movement and combat plumbing below, and Tibbers' own basic-attack
/// damage line. The pets themselves — Tibbers included — run theirs on <see cref="PetAiService"/>,
/// so nothing here drives a <see cref="PetState"/> unit. The hand-written Minion.lua ladder (a scan
/// every 0.25 s in every state, the lane ladder on each scan) is gone with batch 339.
/// </summary>
public sealed class TibbersAiSystem
{
    /// <summary>
    /// Tibbers' basic attack damage: <c>AnnieTibbersBasicAttack.luaobj</c> <c>TargetExecute</c> applies <c>SrcValueByLevel {80,105,130,130,130}</c>
    /// indexed by the CURRENT level of Annie's R (<c>GetSlotSpellLevel</c> of her slot 3), not by the rank Tibbers was summoned with.
    /// The engine does not run an attack script for a pet's swing, so this table is the script's damage line:
    /// <c>AnnieTibbersBasicAttack.luaobj</c> holds <c>SrcValueByLevel</c> 80, 105 and 130 (verified in the binary) and reads
    /// <c>GetSlotSpellLevel</c> on Annie's spells.
    /// </summary>
    public static double AttackDamage(Entity annie) =>
        new[] { 80.0, 105.0, 130.0 }[Math.Clamp(annie.SpellRanks[3], 1, 3) - 1];

    private readonly WorldState _world;
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly ISpellEffectSink _effects;
    private readonly IMinionVisionService? _vision;
    private readonly Func<Entity, Entity, bool>? _canSee;
    private readonly Dictionary<uint, Position2> _lastDestination = new();

    public TibbersAiSystem(WorldState world, MovementSystem movement,
        CombatService combat, ISpellEffectSink effects, IMinionVisionService? vision = null,
        Func<Entity, Entity, bool>? canSee = null, IRandomSource? random = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        if (!ReferenceEquals(movement.World, world))
            throw new ArgumentException("MovementSystem must belong to the pet world", nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _vision = vision;
        _canSee = canSee;
        Engine = new SpawnedUnitLuaAiEngine(this, movement, combat, random);
        Host = new LuaAiService(world, Engine);
    }

    public WorldState World => _world;

    /// <summary>The natives' world half, with the per-unit AI state and the ignore window.</summary>
    public SpawnedUnitLuaAiEngine Engine { get; }

    /// <summary>The script host: one instance per unit this system drives, its timers and its callbacks.</summary>
    public LuaAiService Host { get; }

    /// <summary>Where the lane-less spawned units' last path requests aimed, for the checks and the replays.</summary>
    public IReadOnlyDictionary<uint, Position2> LastDestinations => _lastDestination;

    public void Tick(double now, double dt)
    {
        if (!double.IsFinite(now) || !double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));

        foreach (var netId in _lastDestination.Keys.ToArray())
            if (_world.Get(netId)?.Alive != true) _lastDestination.Remove(netId);

        Engine.Tick(now, dt);
        // Reconstruction: the retail server stops deciding for a unit when the match is over; the
        // shipped scripts have no such hook, so the gate is the host's.
        if (_combat.GameOver) return;
        foreach (var unit in _world.Entities.Values.Where(entity => entity.Alive && Host.Drives(entity))
                     .ToArray())
        {
            ConsumeHelp(unit);
            Host.StepUnit(unit, now, dt);
            if (Host.TimerFired(unit) && Engine.Scan(unit)) Host.OnPathToTargetBlocked(unit);
            Engine.Continue(unit);
        }
    }

    /// <summary>
    /// Batch 348: the call for help <c>CombatService.CallLaneHelp</c> nominated this unit for, raised once at its next step as a lane minion's
    /// is (<c>MinionAiSystem.ConsumeHelp</c>): <c>OnCallForHelp(self, aggressor)</c>, which minionLib (proto 0.3) and Aggro (proto 0.3) answer
    /// with <c>SetStateAndCloseToTarget(AI_ATTACKMOVE_ATTACKING, aggressor)</c> in the states they accept. A held unit keeps the call pending
    /// (it lapses after a second). Reconstruction: the native admission is not in the client, so the lane minion's rule decides (the target
    /// alive, targetable and seen, a stronger call than the target held, the cfh_Stick window); the state test is the script's.
    /// </summary>
    private void ConsumeHelp(Entity unit)
    {
        if (unit.HelpTarget is not uint helpId || IsStunned(unit)) return;
        unit.HelpTarget = null;
        unit.HelpRemaining = 0.0;
        if (unit.AnyBuffOrStatus(buff => buff.Type is BuffType.Fear or BuffType.Taunt) ||
            _world.Get(helpId) is not { Alive: true } aggressor || !_combat.IsTargetable(aggressor, unit) || !CanSee(unit, aggressor) ||
            !MinionAiSystem.CanAcceptHelp(_world, unit, aggressor, unit.HelpPriority)) return;
        MinionAiSystem.StickHelp(unit, aggressor);
        Host.OnCallForHelp(unit, aggressor);
    }

    internal bool IsEligibleTarget(Entity pet, Entity candidate, double radiusSquared)
    {
        if (candidate == pet || !candidate.Alive || candidate.Protected ||
            !WorldState.IsEnemy(pet, candidate) || !_combat.IsTargetable(candidate, pet) ||
            !CanSee(pet, candidate) ||
            candidate.UnitType is not (UnitType.Hero or UnitType.Minion or UnitType.Monster or
                                       UnitType.Turret or UnitType.Structure))
            return false;
        return DistanceSquared(pet.Position, candidate.Position) <= radiusSquared;
    }

    internal bool CanSee(Entity pet, Entity candidate) =>
        _canSee?.Invoke(pet, candidate) ?? (_vision?.HasBrushVision(pet.Team, candidate) ?? true);

    internal void Move(Entity pet, Position2 destination, Entity? attackTarget = null)
    {
        // Retail re-paths whenever its script or native state asks, with no distance tolerance (batch 229): a request for the point
        // the unit already walks to is the same request, and nothing else is held back.
        if (_lastDestination.TryGetValue(pet.NetId, out var previous) && previous == destination &&
            (pet.Waypoints.Count > 0 || pet.Target is not null))
            return;
        // The request is what the unit was asked for: a flank sample below answers it, it is not a new request.
        var requested = destination;
        var path = _movement.SetMoveOrder(pet.NetId, destination,
            _canSee is null ? null : other => _canSee(pet, other));
        if (attackTarget is not null && path.Count < 2)
        {
            // A blocked radial approach must try the other sides of the attack circle, eight units inside reach for the two-unit
            // waypoint quantization (Reconstruction, as the pet engine's flank).
            var reach = Math.Max(0.0, CombatService.AttackReach(pet, attackTarget) - 8.0);
            var angle = Math.Atan2(pet.Position.Z - attackTarget.Position.Z,
                pet.Position.X - attackTarget.Position.X);
            // ponytail: 16 flank samples; replace with continuous attack-circle search if narrower
            // gaps are demonstrated.
            for (var sample = 1; sample <= 16 && path.Count < 2; sample++)
            {
                var offset = ((sample + 1) / 2) * (Math.PI / 8.0) *
                    (sample % 2 == 1 ? 1.0 : -1.0);
                destination = attackTarget.Position + new Position2(
                    Math.Cos(angle + offset) * reach, Math.Sin(angle + offset) * reach);
                path = _movement.SetMoveOrder(pet.NetId, destination,
                    _canSee is null ? null : other => _canSee(pet, other));
            }
        }
        _lastDestination[pet.NetId] = requested;
        if (!_effects.IsReferenceReady(pet)) return;
        // No route is a stop, whose one-point path is installed on receipt: seated at once.
        var points = path.Count > 0 ? path : _movement.InstallStop(pet);
        _effects.SendWaypoints(pet, points, _world.Clock.SyncId());
    }

    internal void Stop(Entity pet)
    {
        pet.Waypoints.Clear();
        pet.Target = null;
        _lastDestination.Remove(pet.NetId);
        if (_effects.IsReferenceReady(pet))
            _effects.SendWaypoints(pet, _movement.InstallStop(pet), _world.Clock.SyncId());
    }

    /// <summary>Publishes a finished path to a unit's client, the way every walk above does.</summary>
    internal void PublishWaypoints(Entity pet, IReadOnlyList<Position2> path)
    {
        if (!_effects.IsReferenceReady(pet)) return;
        _effects.SendWaypoints(pet, path, _world.Clock.SyncId());
    }

    internal void SetAttackTarget(Entity pet, Entity? target)
    {
        if (pet.AttackTarget is uint oldTarget && oldTarget != (target?.NetId ?? 0))
        {
            // PET-01: TurnOffAutoAttack(TARGET_LOST) retires only unlaunched attacks; launched hits
            // retain their own lifetime.
            _combat.CancelPendingWindups(pet.NetId);
            if (_effects.IsReferenceReady(pet)) _effects.SendStopAttack(pet);
            _lastDestination.Remove(pet.NetId);
            if (pet.Waypoints.Count > 0 || pet.Target is not null) Stop(pet);
        }
        pet.AttackTarget = target?.NetId;
    }

    internal bool HasPendingAttack(Entity pet) =>
        _combat.PendingHits.Any(hit => hit.SourceNetId == pet.NetId && hit.WindupRemaining > 0.0);

    // A unit that a script is displacing (a pull, knock-back or knock-up) is held like a stunned one: it takes no command and
    // makes no decision, so no route or stop reaches the client while its dash runs (a plain 0xC1 replaces the dash path).
    internal static bool IsStunned(Entity entity) =>
        entity.ChampionState?.BlocksActions == true || entity.AnyBuffOrStatus(buff =>
            buff.DisablesActions) || entity.ForcedMove is { Effect: not "Fear" };

    internal static bool IsRooted(Entity entity) => entity.AnyBuffOrStatus(buff =>
        buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    /// <summary>
    /// Where a chasing unit aims: the target's position, as the stock client's AttackTo plan aims (the base unit's order step
    /// hands it to the client's path builder); the planner's end-cell snap (SetToNearestGetToAbleCell) moves the end off the occupied
    /// target. It does not depend on where
    /// the unit stands, so a target that stands still is one request, not one per scan (batch 236).
    /// </summary>
    internal static Position2 ApproachPoint(Entity pet, Entity target) => target.Position;

    internal static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }
}
