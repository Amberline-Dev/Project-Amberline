using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Runtime;

// DIAGNOSTICS: Retail has no owner playtest markers or invariant event stream.
// These observations help diagnose stock-client playtests and never repair state.
internal sealed class PlaytestDiagnostics
{
    private const double ClientStopBand = 225.0;
    private const double OverspeedStepFactor = 2.0;
    // A lane minion is created on its barracks marker, and the Order right-lane marker lies 4.7 units inside the Nexus stamp
    // (348.0 against 352.8, retail geometry); the minion walks out within two movement frames. Its first second is not a defect.
    private const double SpawnGraceSeconds = 1.0;
    // DIAGNOSTICS (batch 371, finding 56): a lane minion that neither attacks nor moves for this long is reported as "minion-idle" (the
    // late-game "minions stopped moving in the enemy base" report). Eight seconds: the seed-1 headless match to minute 25 had no longer
    // stillness, and the shorter 4 s episodes (2018 in that match) were the normal engage pauses. It never changes the minion.
    private const double IdleSeconds = 8.0;
    private static readonly string[] Kinds = ["blocked-cell", "overspeed", "stacked",
        "target-beyond-retention", "client-divergence", "minion-idle"];
    private readonly WorldState _world;
    private readonly NavigationGrid _navigation;
    private readonly ISessionLogSink? _log;
    private readonly double _collisionEpsilon;
    private readonly Dictionary<uint, UnitState> _units = new();
    private readonly List<UnitState> _states = new();
    private readonly SpatialIndex<Entity> _index = new(Array.Empty<(double, double, Entity)>());
    private readonly List<Entity> _nearby = new();
    private readonly Dictionary<(uint, uint), (double Since, long Tick)> _pairs = new();
    private readonly List<(uint, uint)> _expiredPairs = new();
    private long _tick;

    private struct Sample
    {
        public double Value, Limit, Dt, Speed;
        public Position2 Position, From;
        public uint? Other;
    }

    private struct Episode
    {
        public bool Active;
        public double Since, LastWarning;
        public Sample Sample;
    }

    private sealed class UnitState(Entity unit)
    {
        public readonly Entity Unit = unit;
        public readonly Episode[] Episodes = new Episode[Kinds.Length];
        public Sample Movement, Client, Stack;
        public bool HasMovement, HasClient, Stacked;
        public double FirstSeen = double.NaN, LastMovedAt = double.NaN;
        public Position2 LastPosition;
    }

    public PlaytestDiagnostics(WorldState world, NavigationGrid navigation,
        MovementSystem movement, ISessionLogSink? log, bool enabled = true)
    {
        _world = world;
        _navigation = navigation;
        _log = log;
        _collisionEpsilon = Math.Sqrt(movement.Settings.Collision.ColliderEpsilonSquared);
        Enabled = enabled;
    }

    public bool Enabled { get; }
    public Func<double?>? HudTime { get; set; }

    public double? Mark(Entity hero, string note)
    {
        var hud = HudTime?.Invoke();
        if (_log is null) return hud;
        var nearby = new List<Dictionary<string, object?>>();
        foreach (var unit in _world.Entities.Values)
        {
            if (unit.UnitType == UnitType.Missile || Distance(hero.Position, unit.Position) > 1500) continue;
            nearby.Add(new Dictionary<string, object?>
            {
                ["netid"] = unit.NetId, ["unit_type"] = UnitTypeName(unit),
                ["team"] = unit.Team.ToString(), ["champion"] = Champion(unit),
                ["pos"] = Point(unit.Position),
                ["ai_state"] = unit.MinionType is not null ? unit.MinionState.ToString() :
                    unit.UnitType == UnitType.Monster ? unit.NeutralState : null,
                ["attack_target"] = unit.AttackTarget, ["waypoints"] = unit.Waypoints.Count,
                ["stuck_elapsed"] = Mobile(unit) ? unit.StuckElapsed : null,
            });
        }
        Write("PLAYTEST", "INFO", "mark", new Dictionary<string, object?>
        {
            ["hud"] = hud, ["note"] = note, ["hero"] = hero.NetId,
            ["champion"] = Champion(hero), ["team"] = hero.Team.ToString(),
            ["pos"] = Point(hero.Position), ["nearby"] = nearby,
        });
        return hud;
    }

    // Called at the movement owner's commit boundary, never across spell/respawn jumps.
    internal void ObserveMovement(Entity unit, Position2 from, double speed, double dt, bool forced)
    {
        if (!Enabled || !Mobile(unit)) return;
        var state = State(unit);
        var value = forced ? 0 : Distance(from, unit.Position);
        // Collision separation adds to a committed walking step: a seed-1
        // headless match measured up to 1.82x speed*dt (6068 steps, none
        // above). Only a jump past twice the step is a teleport-class fault.
        var limit = OverspeedStepFactor * speed * dt;
        if (!state.HasMovement || value - limit > state.Movement.Value - state.Movement.Limit)
            state.Movement = new Sample { Value = value, Limit = limit, Dt = dt,
                Speed = speed, From = from, Position = unit.Position };
        state.HasMovement = true;
    }

    internal void ObserveClientPosition(Entity hero, Position2 position)
    {
        if (!Enabled) return;
        var state = State(hero);
        state.Client = new Sample { Value = Distance(hero.Position, position), Limit = ClientStopBand,
            Position = hero.Position, From = position };
        state.HasClient = true;
    }

    public void Tick()
    {
        if (!Enabled) return;
        var now = _world.Clock.Monotonic();
        _tick++;
        _index.Clear();
        var maxRadius = 0.0;
        foreach (var unit in _world.Entities.Values)
        {
            if (!Mobile(unit)) continue;
            var state = State(unit);
            state.Stacked = false;
            if (!unit.Alive || unit.MinionType is null) continue;
            _index.Add(unit.Position.X, unit.Position.Z, unit);
            maxRadius = Math.Max(maxRadius, Radius(unit));
        }
        foreach (var state in _states)
        {
            var unit = state.Unit;
            if (!unit.Alive || unit.MinionType is null || !ReferenceEquals(_world.Get(unit.NetId), unit)) continue;
            _index.QueryInto(unit.Position.X, unit.Position.Z, Radius(unit) + maxRadius, _nearby);
            foreach (var other in _nearby)
            {
                if (other.NetId <= unit.NetId) continue;
                var overlap = Radius(unit) + Radius(other) - Distance(unit.Position, other.Position);
                if (overlap <= _collisionEpsilon) continue;
                var key = (unit.NetId, other.NetId);
                var since = _pairs.TryGetValue(key, out var pair) && pair.Tick == _tick - 1
                    ? pair.Since : now;
                _pairs[key] = (since, _tick);
                if (now - since <= 2.0) continue;
                SetStack(state, other, overlap);
                SetStack(_units[other.NetId], unit, overlap);
            }
        }
        _expiredPairs.Clear();
        foreach (var pair in _pairs)
            if (pair.Value.Tick != _tick) _expiredPairs.Add(pair.Key);
        foreach (var key in _expiredPairs) _pairs.Remove(key);

        for (var index = _states.Count - 1; index >= 0; index--)
        {
            var state = _states[index];
            var unit = state.Unit;
            var present = ReferenceEquals(_world.Get(unit.NetId), unit);
            if (!present || !unit.Alive || !Mobile(unit))
            {
                for (var kind = 0; kind < Kinds.Length; kind++) Evaluate(state, kind, false, default, now);
                state.HasMovement = state.HasClient = false;
                if (!present)
                {
                    if (_units.TryGetValue(unit.NetId, out var current) && ReferenceEquals(current, state))
                        _units.Remove(unit.NetId);
                    _states[index] = _states[^1];
                    _states.RemoveAt(_states.Count - 1);
                }
                continue;
            }
            if (double.IsNaN(state.FirstSeen)) state.FirstSeen = now;
            var cell = _navigation.WorldToCell(unit.Position);
            var blocked = unit.ForcedMove is null && _navigation.IsInBounds(cell) &&
                !_navigation.IsMovementWalkable(cell) && !ClipsOwnRoute(unit) &&
                !(unit.MinionType is not null && now - state.FirstSeen < SpawnGraceSeconds);
            Evaluate(state, 0, blocked, new Sample { Value = blocked ? 1 : 0, Limit = 0,
                Position = unit.Position }, now);
            if (state.HasMovement)
                Evaluate(state, 1, state.Movement.Value > state.Movement.Limit + 1e-5, state.Movement, now);
            Evaluate(state, 2, state.Stacked, state.Stack, now);
            var target = unit.MinionType is not null && unit.AttackTarget is uint id ? _world.Get(id) : null;
            var distance = target is null ? 0 : Distance(unit.Position, target.Position);
            Evaluate(state, 3, distance > MinionDefinitions.MaxEngageDistance,
                new Sample { Value = distance, Limit = MinionDefinitions.MaxEngageDistance,
                Position = unit.Position, Other = target?.NetId }, now);
            if (state.HasClient) Evaluate(state, 4, state.Client.Value > ClientStopBand, state.Client, now);
            // DIAGNOSTICS (batch 371): a lane minion that is neither attacking nor moving (no 1-unit progress) for IdleSeconds.
            // DIAGNOSTICS (batch 374): the clock also restarts on every step the minion is not idle, so a stand that is an attack
            // is not stillness: the clock counts from the attack's end. Before, the attack's end itself read as an 8-24 s stall,
            // and each of batch 373's 411 warns resumed its route within 0.37 s.
            var idle = unit.MinionType is not null && unit.MinionState != MinionAiState.Halted && unit.AttackTarget is null;
            if (!idle || double.IsNaN(state.LastMovedAt) || Distance(unit.Position, state.LastPosition) > 1.0)
            {
                state.LastPosition = unit.Position;
                state.LastMovedAt = now;
            }
            var still = idle ? now - state.LastMovedAt : 0.0;
            Evaluate(state, 5, still > IdleSeconds, new Sample { Value = still, Limit = IdleSeconds, Position = unit.Position }, now);
            state.HasMovement = state.HasClient = false;
        }
    }

    private void SetStack(UnitState state, Entity other, double overlap)
    {
        if (state.Stacked && state.Stack.Value >= overlap) return;
        state.Stacked = true;
        state.Stack = new Sample { Value = overlap, Limit = _collisionEpsilon,
            Position = state.Unit.Position, Other = other.NetId };
    }

    private UnitState State(Entity unit)
    {
        if (_units.TryGetValue(unit.NetId, out var state) && ReferenceEquals(state.Unit, unit)) return state;
        state = new UnitState(unit);
        for (var kind = 0; kind < Kinds.Length; kind++) state.Episodes[kind].LastWarning = double.NegativeInfinity;
        _units[unit.NetId] = state;
        _states.Add(state);
        return state;
    }

    private void Evaluate(UnitState state, int kind, bool violated, Sample sample, double now)
    {
        ref var episode = ref state.Episodes[kind];
        if (!violated)
        {
            if (episode.Active) Emit(state.Unit, kind, episode.Sample, now - episode.Since);
            episode.Active = false;
            return;
        }
        if (!episode.Active) episode.Since = now;
        episode.Active = true;
        episode.Sample = sample;
        if (now - episode.LastWarning < 5) return;
        episode.LastWarning = now;
        Emit(state.Unit, kind, sample, null);
    }

    private void Emit(Entity unit, int kind, Sample sample, double? duration)
    {
        if (_log is null) return;
        var detail = new Dictionary<string, object?>();
        if (duration is double seconds) detail["duration"] = seconds;
        switch (kind)
        {
            case 0:
                var cell = _navigation.WorldToCell(sample.Position);
                detail["cell"] = new[] { cell.Column, cell.Row };
                break;
            case 1:
                detail["from"] = Point(sample.From);
                detail["speed"] = sample.Speed;
                detail["dt"] = sample.Dt;
                break;
            case 2: detail["other"] = sample.Other; detail["minimum_seconds"] = 2; break;
            case 3: detail["attack_target"] = sample.Other; break;
            case 4: detail["client_pos"] = Point(sample.From); break;
            case 5: IdleDetail(unit, detail); break;
        }
        Write("INVARIANT", "WARN", Kinds[kind] + (duration is null ? "" : "-end"),
            new Dictionary<string, object?>
            {
                ["unit"] = unit.NetId, ["unit_type"] = UnitTypeName(unit),
                ["team"] = unit.Team.ToString(), ["champion"] = Champion(unit),
                ["pos"] = Point(sample.Position), ["value"] = sample.Value,
                ["limit"] = sample.Limit, ["detail"] = detail,
            });
    }

    // DIAGNOSTICS (batch 371): the AI state and lane-path suffix of an idle lane minion, and the nearest live enemy structure with its
    // protection (SelectStructureTarget never picks a protected one), so a stall names what the minion had left to attack.
    private void IdleDetail(Entity minion, Dictionary<string, object?> detail)
    {
        detail["ai_state"] = minion.MinionState.ToString();
        detail["lane"] = minion.MinionLane;
        detail["lane_done"] = minion.LaneDone;
        detail["lane_waypoints"] = minion.LaneWaypoints.Count;
        detail["interrupted_waypoints"] = minion.InterruptedLaneWaypoints.Count;
        detail["waypoints"] = minion.Waypoints.Count;
        detail["has_target"] = minion.Target is not null;
        Entity? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        var structures = 0;
        foreach (var candidate in _world.Entities.Values)
        {
            if (!candidate.Alive || candidate.Team == minion.Team ||
                candidate.UnitType is not (UnitType.Turret or UnitType.Structure)) continue;
            structures++;
            var distance = Distance(minion.Position, candidate.Position);
            if (distance >= nearestDistance) continue;
            nearest = candidate;
            nearestDistance = distance;
        }
        detail["enemy_structures_alive"] = structures;
        if (nearest is null) return;
        detail["nearest_structure"] = nearest.Name;
        detail["nearest_distance"] = nearestDistance;
        detail["nearest_protected"] = nearest.Protected;
    }

    private void Write(string category, string level, string message, Dictionary<string, object?> properties)
    {
        try { _log?.Write(category, level, message, properties); }
        catch { /* A diagnostic sink must not interrupt chat or gameplay. */ }
    }

    // A hero whose last frame moved it toward its next waypoint is walking its own route across a blocked corner: the stock client's
    // free-movement frame has no terrain gate and its planner's line of sight lets a chord clip such a corner (W8, 18 October 2026).
    // That frame is the client's own; a hero with no route, or one whose frames are reverted, is a wall case and is still reported.
    private static bool ClipsOwnRoute(Entity unit) =>
        unit.UnitType == UnitType.Hero && unit.Waypoints.Count > 0 &&
        (unit.Waypoints[0].X - unit.Position.X) * unit.FrameVelocity.X +
        (unit.Waypoints[0].Z - unit.Position.Z) * unit.FrameVelocity.Z > 0.0;

    private static bool Mobile(Entity unit) => unit.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Monster &&
        unit.Kind is not (EntityKind.Ward or EntityKind.ShacoBox or EntityKind.NidaleeTrap);
    private static double Radius(Entity unit) => Math.Max(0, unit.PathfindingRadius ?? unit.Radius);
    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    private static double[] Point(Position2 point) => [point.X, point.Z];
    private static string? Champion(Entity unit) => unit.UnitType == UnitType.Hero ||
        unit.Kind is EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet ? unit.ChampionName : null;
    private static string UnitTypeName(Entity unit) => unit.Kind is EntityKind.Pet or EntityKind.ShacoBox or
        EntityKind.ShacoClone or EntityKind.YorickGhoul or EntityKind.YorickRevenant or EntityKind.NidaleeTrap or EntityKind.ClonePet
        ? "pet" : unit.UnitType switch
        {
            UnitType.Hero => "hero", UnitType.Minion => "minion", UnitType.Monster => "monster",
            UnitType.Turret => "turret", _ => "structure",
        };
}
