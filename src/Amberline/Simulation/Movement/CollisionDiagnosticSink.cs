using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.Movement;

/// <summary>Bounded movement histories and compact decisions, independent of the bulk fidelity budget.</summary>
public sealed class CollisionDiagnosticSink : IMovementDiagnosticSink, IDisposable
{
    private const int RingSize = 20;
    private const double DumpCooldown = 10.0;
    private const double SummaryPeriod = 10.0;
    private const double AiTransitionInterval = 2.0;
    internal const int DetailLimitBytes = 16 * 1024;
    private readonly WorldState _world;
    private readonly ISessionLogSink? _log;
    private readonly NavigationBounds? _bounds;
    private readonly Dictionary<uint, Queue<RingEntry>> _rings = new();
    private readonly Dictionary<uint, MovementDiagnosticSnapshot> _snapshots = new();
    private readonly Dictionary<uint, double> _snapshotTimes = new();
    private readonly HashSet<uint> _flagged = new();
    private readonly HashSet<uint> _pending = new();
    private readonly HashSet<uint> _oversized = new();
    private readonly HashSet<uint> _oversizedPending = new();
    private readonly Dictionary<uint, double> _lastDump = new();
    private readonly Queue<(double Time, bool Hero)> _recentDumps = new();
    private readonly Dictionary<uint, Position2> _summaryPositions = new();
    private readonly Dictionary<string, int> _branches = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint NetId, string Kind), SemanticState> _semantic = new();
    private readonly Dictionary<uint, Dictionary<string, object?>> _decisions = new();
    private readonly Dictionary<uint, PlannerBlocker> _plannerBlockers = new();
    private readonly Dictionary<uint, long> _orderIds = new();
    private readonly Dictionary<uint, long> _lastWaypointSync = new();
    private readonly Dictionary<uint, (string State, uint? Target)> _minionAi = new();
    private readonly Dictionary<uint, double> _lastAiTransition = new();
    private readonly Dictionary<uint, Dictionary<string, object?>> _pendingAiTransition = new();
    private readonly Dictionary<(uint NetId, long Sync), WaypointIntent> _waypointIntents = new();
    private double _diagnosticClock;
    private double _lastSummary;
    private uint _lastMinion;
    private int _deferredDetails;
    private bool _disposed;
    public Func<double?>? HudTime { get; set; }

    public CollisionDiagnosticSink(WorldState world, ISessionLogSink? log = null,
        NavigationBounds? bounds = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _log = log;
        _bounds = bounds;
    }

    public static CollisionDiagnosticSink FromEnvironment(WorldState world,
        ISessionLogSink? log, NavigationBounds? bounds = null) => new(world, log, bounds);

    public bool Enabled => !_disposed && _log is not null &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOG_COLLISION"));

    internal int RetainedActorCount => _rings.Count;
    internal int PendingDetailCount => _pending.Concat(_oversizedPending).Distinct().Count();
    internal int RetainedSemanticCount => _semantic.Count;
    internal int RetainedMinionAiCount => _minionAi.Count;
    internal double DiagnosticElapsed => _diagnosticClock;

    public void OnMovementTick(IReadOnlyList<MovementDiagnosticSnapshot> snapshots) =>
        OnMovementTick(0.1, snapshots);

    public void OnMovementTick(double dt, IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
        => OnMovementTick(dt, _diagnosticClock + dt, snapshots);

    public void OnMovementTick(double dt, double elapsed,
        IReadOnlyList<MovementDiagnosticSnapshot> snapshots)
    {
        if (!Enabled) { Clear(); return; }
        try
        {
            if (!double.IsFinite(dt) || dt < 0.0) dt = 0.1;
            if (!double.IsFinite(elapsed) || elapsed < 0.0)
                elapsed = _diagnosticClock + dt;
            _diagnosticClock = Math.Max(_diagnosticClock, elapsed);
            PruneDead();
            foreach (var snapshot in snapshots)
            {
                if (_world.Get(snapshot.NetId) is not { Alive: true, Radius: > 0.0 } entity ||
                    entity.UnitType is not (UnitType.Hero or UnitType.Minion))
                    continue;
                var previous = _snapshots.GetValueOrDefault(snapshot.NetId);
                var previousAt = _snapshotTimes.GetValueOrDefault(snapshot.NetId, double.NaN);
                _snapshots[snapshot.NetId] = snapshot;
                _snapshotTimes[snapshot.NetId] = elapsed;
                if (entity.UnitType == UnitType.Minion)
                {
                    var currentAi = (entity.AiState, entity.AttackTarget);
                    if (_minionAi.TryGetValue(entity.NetId, out var priorAi) && priorAi != currentAi)
                    {
                        var transition = ActorProperties(entity.NetId);
                        transition["from_state"] = Clip(priorAi.State);
                        transition["to_state"] = Clip(currentAi.AiState);
                        transition["from_target"] = priorAi.Target;
                        transition["to_target"] = currentAi.AttackTarget;
                        if (!_lastAiTransition.TryGetValue(entity.NetId, out var last) ||
                            _diagnosticClock - last >= AiTransitionInterval)
                        {
                            Write("minion_ai_transition", transition);
                            _lastAiTransition[entity.NetId] = _diagnosticClock;
                            _pendingAiTransition.Remove(entity.NetId);
                        }
                        else
                        {
                            var count = _pendingAiTransition.TryGetValue(entity.NetId, out var pending) &&
                                pending.GetValueOrDefault("coalesced_transitions") is int previousCount
                                    ? previousCount : 0;
                            transition["coalesced_transitions"] =
                                count + 1;
                            _pendingAiTransition[entity.NetId] = transition;
                        }
                    }
                    _minionAi[entity.NetId] = currentAi;
                }
                var start = snapshot.StartPosition ?? entity.Position;
                var march = snapshot.MarchPosition ?? entity.Position;
                var committed = snapshot.CommittedPosition ?? entity.Position;
                var entry = new RingEntry(start, march, committed, Distance(start, committed),
                    (snapshot.Branches ?? []).Take(6).Select(value => Clip(value, 48)).ToArray(),
                    snapshot.ColliderIds.Take(16).ToArray(), snapshot.AvoidCount,
                    snapshot.ColliderDot, snapshot.Halted,
                    Distance(Position2.Zero, snapshot.SeparationAccumulation),
                    snapshot.CollisionCandidatePosition, snapshot.RequestedGoal,
                    snapshot.DeltaTime, entity.Speed, snapshot.Static);
                if (!_rings.TryGetValue(snapshot.NetId, out var ring))
                    _rings[snapshot.NetId] = ring = new Queue<RingEntry>(RingSize);
                if (ring.Count == RingSize) ring.Dequeue();
                ring.Enqueue(entry);
                foreach (var branch in entry.Branches)
                    _branches[branch] = _branches.GetValueOrDefault(branch) + 1;

                var forcedMove = entity.ForcedMove;
                var budgetSpeed = forcedMove is { Completed: false, Speed: > 0.0 }
                    ? forcedMove.Speed : Math.Max(0.0, entity.Speed);
                var withinStepDistance = Distance(start, committed);
                var withinStepBudget = budgetSpeed * dt;
                var previousCommitted = previous is null
                    ? (Position2?)null
                    : previous.CommittedPosition ?? previous.MarchPosition ?? previous.StartPosition;
                var crossStepDt = previous is null || !double.IsFinite(previousAt)
                    ? 0.0 : Math.Max(0.0, elapsed - previousAt);
                var crossStepDistance = previousCommitted is Position2 prior
                    ? Distance(prior, committed) : 0.0;
                var withinStepOversized = withinStepDistance > withinStepBudget + 1e-6;
                var crossStepOversized = previousCommitted is Position2 &&
                    crossStepDistance > budgetSpeed * crossStepDt + 1e-6;
                if (withinStepOversized || crossStepOversized)
                {
                    if (_oversized.Add(entity.NetId))
                    {
                        _oversizedPending.Add(entity.NetId);
                        EmitOversizedDisplacement(entity, start, march,
                            snapshot.CollisionCandidatePosition, committed, dt,
                            budgetSpeed, withinStepDistance, withinStepBudget,
                            crossStepDistance, budgetSpeed * crossStepDt,
                            withinStepOversized, crossStepOversized, forcedMove);
                    }
                }
                else
                {
                    _oversized.Remove(entity.NetId);
                }
                if (ring.Count < RingSize) continue;

                var entries = ring.ToArray();
                var path = entries.Sum(item => Round(item.Distance, 2));
                var net = RoundedNet(entries[0], entries[^1]);
                var goal = snapshot.RequestedGoal ?? entity.Target ?? entity.AttackMoveGoal;
                var intent = entity.Waypoints.Count > 0 &&
                    Distance(entity.Position, entity.Waypoints[^1]) > 3.0 ||
                    goal is Position2 requested && Distance(entity.Position, requested) > 3.0;
                var stalled = net < 3.0 && path < 3.0 && intent;
                if (path > 30.0 && path > 1.5 * net || stalled)
                {
                    _flagged.Add(entity.NetId);
                    _pending.Add(entity.NetId);
                }
                else
                {
                    if (_flagged.Remove(entity.NetId)) _pending.Remove(entity.NetId);
                }
            }
            FlushAiTransitions();
            EmitPending();
            if (_diagnosticClock + 1e-9 >= _lastSummary + SummaryPeriod)
                EmitSummary();
        }
        catch { /* diagnostics never interrupt a movement tick */ }
    }

    /// <summary>Called after gameplay ownership, order kind and coordinate validation.</summary>
    public void NoteAcceptedHeroOrder(Entity hero, byte order, uint target, Position2 destination)
    {
        if (!Enabled) return;
        try
        {
            var id = _orderIds.GetValueOrDefault(hero.NetId) + 1;
            _orderIds[hero.NetId] = id;
            var properties = ActorProperties(hero.NetId);
            properties["order"] = order;
            properties["target"] = target;
            properties["requested_goal"] = Point(destination);
            Coalesce(hero.NetId, "hero-order",
                $"{order}/{target}/{destination.X:R}/{destination.Z:R}", properties);
        }
        catch { /* accepted commands never depend on their diagnostic consumer */ }
    }

    public void NoteDecision(MovementDecisionObservation observation)
    {
        if (!Enabled) return;
        try
        {
            if (_world.Get(observation.NetId) is not { Alive: true }) return;
            var route = observation.Route;
            var properties = ActorProperties(observation.NetId);
            properties["reason"] = Clip(observation.Reason);
            properties["phase"] = Clip(observation.Phase);
            properties["requested_goal"] = Point(observation.RequestedGoal);
            properties["route_goal"] = Point(route is { Count: > 0 } ? route[^1] : null);
            properties["route_count"] = route?.Count;
            properties["route"] = route?.Take(32).Select(point => Point(point)).ToArray();
            properties["route_truncated"] = route?.Count > 32;
            properties["attempt"] = observation.Attempt;
            properties["blocker"] = Point(observation.BlockerPosition);
            properties["blocker_radius"] = observation.BlockerRadius;
            properties["raw_anchor"] = Point(observation.RawAnchor);
            properties["quantized_anchor"] = Point(observation.QuantizedAnchor);
            properties["sync_id"] = observation.SyncId;
            _decisions[observation.NetId] = properties;
            if (observation.Kind == "hero-planner" && observation.BlockerPosition is Position2 blocker)
            {
                // The planner supplies a center/clearance, not an entity identity.
                // Preserve exact-position matches without choosing one when bodies overlap.
                var matches = _world.Entities.Values.Where(other => other.Alive &&
                    other.NetId != observation.NetId && other.Radius > 0.0 && other.Position == blocker)
                    .OrderBy(other => other.NetId).Take(17).ToArray();
                _plannerBlockers[observation.NetId] = new PlannerBlocker(
                    _world.Clock.MonotonicNanoseconds(), blocker, observation.BlockerRadius,
                    Clip(observation.Phase), matches.Take(16).Select(other => new BodyAtDecision(
                        other.NetId, other.Position, other.Radius, other.UnitType.ToString().ToUpperInvariant(),
                        Clip(other.AiState), other.RepathCount)).ToArray(), matches.Length > 16);
            }
            var routeGoal = route is { Count: > 0 } ? route[^1] : (Position2?)null;
            var signature = $"{Clip(observation.Reason)}/{Clip(observation.Phase)}/{observation.Attempt}/" +
                $"{observation.RequestedGoal}/{routeGoal}/" +
                $"{route?.Count}/{observation.BlockerPosition}/{observation.BlockerRadius}";
            Coalesce(observation.NetId, Clip(observation.Kind), signature, properties);
            if (observation.Reason.Contains("wait", StringComparison.Ordinal) ||
                observation.Reason.Contains("reject", StringComparison.Ordinal) ||
                observation.Reason.Contains("park", StringComparison.Ordinal))
                _pending.Add(observation.NetId);
        }
        catch { /* observation callbacks cannot change a decision */ }
    }

    public void NoteWaypointIntent(uint netId, string reason, long syncId,
        IReadOnlyList<Position2> route)
    {
        if (!Enabled) return;
        try
        {
            _lastWaypointSync[netId] = syncId;
            var key = (netId, syncId);
            var intent = new WaypointIntent(Clip(reason), route.Count,
                route.Count > 0 ? route[0] : null, route.Count > 0 ? route[^1] : null,
                route.Take(128).Select(point => new Position2((float)point.X, (float)point.Z)).ToArray());
            if (_waypointIntents.TryGetValue(key, out var previous))
                intent.Ambiguous = previous.Ambiguous || previous.Reason != intent.Reason ||
                    route.Count > 128 || !previous.Route.SequenceEqual(intent.Route);
            _waypointIntents[key] = intent;
            EmitWaypoint(key, intent, null, null, "queued-for-visibility");
            // A visibility flush normally empties this map. Bound a stalled consumer too.
            if (_waypointIntents.Count > 4096)
            {
                var first = _waypointIntents.First();
                EmitWaypoint(first.Key, first.Value, null, null, "unflushed-intent-cap");
                _waypointIntents.Remove(first.Key);
            }
        }
        catch { /* queue diagnostics are optional */ }
    }

    public void NoteWaypointOutcome(PlaintextPacket packet, long connectionId,
        ulong? playerId, string outcome)
    {
        if (!Enabled || packet.Plaintext.Length < 9 ||
            packet.Plaintext.Span[0] != VisionPackets.WaypointListOpcode) return;
        try
        {
            var decoded = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
            if (decoded.Fields.GetValueOrDefault("net_id") is not uint netId ||
                decoded.Fields.GetValueOrDefault("sync_id") is not uint sync ||
                decoded.Fields.GetValueOrDefault("waypoints") is not object?[] points) return;
            var key = (netId, (long)sync);
            var reason = "unattributed";
            if (_waypointIntents.TryGetValue(key, out var intent))
            {
                intent.Outcomes++;
                if (!intent.Ambiguous && points.Length <= 128 &&
                    intent.Route.SequenceEqual(points.Select(point => ReadPoint(point)!.Value)))
                    reason = intent.Reason;
            }
            EmitWaypoint(key, new WaypointIntent(reason, points.Length,
                ReadPoint(points.FirstOrDefault()), ReadPoint(points.LastOrDefault()), []),
                connectionId, playerId, outcome);
        }
        catch { /* network success or failure must not depend on logging */ }
    }

    public void FlushPostVisibility()
    {
        if (!Enabled) return;
        try
        {
            foreach (var (key, intent) in _waypointIntents)
                if (intent.Outcomes == 0)
                    EmitWaypoint(key, intent, null, null, "no-observed-enqueue");
            _waypointIntents.Clear();
            PruneDead();
        }
        catch { /* visibility delivery owns its own failure semantics */ }
    }

    public void ForgetConnection(long connectionId)
    {
        foreach (var key in _semantic.Keys.Where(key =>
                     key.Kind.StartsWith($"waypoint/{connectionId}/", StringComparison.Ordinal)).ToArray())
        {
            EmitRepeats(_semantic[key]);
            _semantic.Remove(key);
        }
    }

    private void EmitWaypoint((uint NetId, long Sync) key, WaypointIntent intent,
        long? connectionId, ulong? playerId, string outcome)
    {
        var properties = ActorProperties(key.NetId);
        properties["reason"] = intent.Reason;
        properties["sync_id"] = key.Sync;
        properties["route_count"] = intent.Count;
        properties["raw_anchor"] = Point(intent.Anchor);
        properties["quantized_anchor"] = Point(intent.Anchor is Position2 anchor && _bounds is { } bounds
            ? Geometry.ClientWaypoints([anchor], bounds)[0] : null);
        properties["route_goal"] = Point(intent.Goal);
        properties["connection_id"] = connectionId;
        properties["player_id"] = playerId;
        properties["outcome"] = outcome;
        // Each event is after observer filtering. Enqueued means native queue acceptance only.
        Coalesce(key.NetId, $"waypoint/{connectionId}/{outcome}",
            $"{intent.Reason}/{intent.Count}/{intent.Anchor}/{intent.Goal}", properties);
    }

    private void Coalesce(uint netId, string kind, string signature,
        Dictionary<string, object?> properties)
    {
        var key = (netId, kind);
        properties["kind"] = kind;
        if (_semantic.TryGetValue(key, out var previous))
        {
            if (previous.Signature == signature)
            {
                previous.Latest = properties;
                previous.Repeats++;
                return;
            }
            EmitRepeats(previous);
        }
        Write("movement_event", properties);
        _semantic[key] = new SemanticState(signature, properties);
    }

    private void EmitRepeats(SemanticState state)
    {
        if (state.Repeats == 0) return;
        var properties = new Dictionary<string, object?>(state.Latest)
        {
            ["repeats"] = state.Repeats,
        };
        Write("movement_repeat", properties);
        state.Repeats = 0;
    }

    private void EmitPending()
    {
        while (_recentDumps.TryPeek(out var old) && _diagnosticClock - old.Time >= DumpCooldown - 1e-9)
            _recentDumps.Dequeue();
        var eligible = _pending.Concat(_oversizedPending).Distinct().Where(id => _snapshots.ContainsKey(id) &&
            _rings.TryGetValue(id, out var ring) && ring.Count > 0 &&
            (!_oversizedPending.Contains(id) || ring.Count == RingSize) &&
            _diagnosticClock - _lastDump.GetValueOrDefault(id, -1e9) >= DumpCooldown - 1e-9).ToArray();
        var heroes = new Queue<uint>(eligible.Where(IsHero)
            .OrderBy(id => _lastDump.GetValueOrDefault(id, -1e9)).ThenBy(id => id));
        var minions = new Queue<uint>(eligible.Where(id => !IsHero(id))
            .OrderBy(id => id <= _lastMinion).ThenBy(id => id));
        var slots = 8 - _recentDumps.Count;
        var selected = new List<uint>(Math.Max(0, slots));
        Take(heroes, Math.Max(0, 2 - _recentDumps.Count(item => item.Hero)));
        Take(minions, Math.Max(0, 6 - _recentDumps.Count(item => !item.Hero)));
        Take(heroes, slots);
        Take(minions, slots); // Unused reserved slots may be borrowed.
        _deferredDetails += eligible.Length - selected.Count;
        foreach (var id in selected)
        {
            var hero = IsHero(id);
            if (!hero) _lastMinion = id;
            _lastDump[id] = _diagnosticClock;
            _recentDumps.Enqueue((_diagnosticClock, hero));
            _pending.Remove(id);
            _oversizedPending.Remove(id);
            EmitOscillation(_world.Get(id)!, _snapshots[id], _rings[id].ToArray());
        }

        void Take(Queue<uint> queue, int maximum)
        {
            while (maximum-- > 0 && slots > 0 && queue.TryDequeue(out var id))
            {
                selected.Add(id);
                slots--;
            }
        }
    }

    private void FlushAiTransitions()
    {
        foreach (var (netId, transition) in _pendingAiTransition.ToArray())
        {
            if (_diagnosticClock - _lastAiTransition.GetValueOrDefault(netId) < AiTransitionInterval)
                continue;
            Write("minion_ai_transition", transition);
            _lastAiTransition[netId] = _diagnosticClock;
            _pendingAiTransition.Remove(netId);
        }
    }

    private void EmitOscillation(Entity entity, MovementDiagnosticSnapshot snapshot,
        IReadOnlyList<RingEntry> ring)
    {
        var actorStart = snapshot.StartPosition ?? entity.Position;
        var actorDestination = snapshot.MarchPosition ?? actorStart;
        var colliders = snapshot.ColliderIds.ToHashSet();
        var avoiders = (snapshot.AvoiderIds ?? []).ToHashSet();
        var plannerBlocker = _plannerBlockers.GetValueOrDefault(entity.NetId);
        var plannerIds = plannerBlocker?.Bodies.Select(body => body.NetId).ToHashSet() ?? [];
        var candidates = _world.Entities.Values
            .Where(other => other.Alive && other.NetId != entity.NetId && other.Radius > 0.0)
            .Where(other => plannerIds.Contains(other.NetId) || colliders.Contains(other.NetId) || avoiders.Contains(other.NetId) ||
                Distance(_snapshots.GetValueOrDefault(other.NetId)?.StartPosition ?? other.Position,
                    actorDestination) <= snapshot.QueryRadius + other.Radius)
            .OrderBy(other => Priority(other.NetId))
            .ThenBy(other => Distance(other.Position, actorStart)).ThenBy(other => other.NetId)
            .Take(17).ToArray();
        var byId = candidates.Select(other =>
            {
                var otherSnapshot = _snapshots.GetValueOrDefault(other.NetId);
                var start = otherSnapshot?.StartPosition ?? other.Position;
                var march = otherSnapshot?.MarchPosition ?? other.Position;
                return new Dictionary<string, object?>
                {
                    ["netid"] = other.NetId,
                    ["ut"] = other.UnitType.ToString().ToUpperInvariant(),
                    ["pos"] = Point(start), ["r"] = Round(other.Radius, 2),
                    ["v"] = new[] { march.X - start.X, march.Z - start.Z },
                    ["cls"] = colliders.Contains(other.NetId) ? "col" : plannerIds.Contains(other.NetId) ? "planner-match" :
                        avoiders.Contains(other.NetId) ? "avo" : "-",
                    ["static"] = otherSnapshot?.Static, ["state"] = Clip(other.AiState),
                    ["rc"] = other.RepathCount,
                };
            }).ToDictionary(neighbour => (uint)neighbour["netid"]!);
        if (plannerBlocker is not null)
            foreach (var body in plannerBlocker.Bodies)
            {
                if (!byId.TryGetValue(body.NetId, out var neighbour))
                    byId[body.NetId] = neighbour = new Dictionary<string, object?>
                    {
                        ["netid"] = body.NetId, ["ut"] = body.UnitType,
                        ["pos"] = Point(body.Position), ["r"] = body.Radius,
                        ["state"] = body.State, ["rc"] = body.RepathCount,
                        ["cls"] = "planner-match", ["sample"] = "planner-decision",
                    };
                neighbour["planner_observed_mono_ns"] = plannerBlocker.ObservedAt;
                neighbour["planner_pos"] = Point(body.Position);
                neighbour["planner_r"] = body.Radius;
                neighbour["planner_state"] = body.State;
            }
        var orderedNeighbours = byId.OrderBy(pair => Priority(pair.Key))
            .ThenBy(pair => Distance(ReadPoint(pair.Value["pos"])!.Value, actorStart))
            .ThenBy(pair => pair.Key).Take(17).Select(pair => pair.Value).ToArray();
        var neighbours = orderedNeighbours.Take(16).ToList();
        var path = ring.Sum(item => Round(item.Distance, 2));
        var net = RoundedNet(ring[0], ring[^1]);
        var properties = ActorProperties(entity.NetId);
        properties["pos"] = Point(actorStart);
        properties["dest"] = Point(actorDestination);
        properties["speed"] = entity.Speed;
        properties["r"] = Round(entity.Radius, 2);
        properties["ut"] = entity.UnitType.ToString().ToUpperInvariant();
        properties["state"] = Clip(entity.AiState);
        properties["tgt"] = entity.AttackTarget;
        properties["requested_target"] = entity.HeroPursuit?.TargetNetId;
        properties["requested_goal"] = snapshot.RequestedGoal ?? entity.HeroPursuit?.LastKnownPosition ??
            entity.AttackMoveGoal ?? entity.Target;
        properties["route_goal"] = entity.Waypoints.Count > 0 ? entity.Waypoints[^1] : (Position2?)null;
        properties["attack_point"] = entity.AttackPoint;
        properties["anchor"] = entity.AnchorPath.Count > 0 ? entity.AnchorPath[0] : (Position2?)null;
        properties["rc"] = entity.RepathCount;
        properties["stuck"] = Round(entity.StuckElapsed, 3);
        properties["wp"] = entity.Waypoints.Take(3).Select(point => Point(point)).ToArray();
        properties["nwp"] = entity.Waypoints.Count;
        properties["path"] = Round(path, 1);
        properties["net"] = Round(net, 1);
        properties["ratio"] = Round(path / Math.Max(net, 0.01), 2);
        // Preserve the historical 13-field ring; exact frame facts have their own schema.
        properties["ring"] = ring.Select(ToProperties).ToArray();
        properties["frame_context"] = ring.Select(entry => new object?[]
        {
            entry.DeltaTime, entry.Start.X, entry.Start.Z, entry.March.X, entry.March.Z,
            entry.Candidate?.X, entry.Candidate?.Z, entry.Committed.X, entry.Committed.Z,
            entry.Speed, entry.Static, entry.Goal?.X, entry.Goal?.Z,
        }).ToArray();
        properties["decision"] = _decisions.GetValueOrDefault(entity.NetId);
        properties["planner_blocker"] = plannerBlocker is null ? null : new Dictionary<string, object?>
        {
            ["last_explicit"] = true,
            ["observed_mono_ns"] = plannerBlocker.ObservedAt,
            ["pos"] = Point(plannerBlocker.Position), ["clearance_radius"] = plannerBlocker.Clearance,
            ["phase"] = plannerBlocker.Phase,
            ["body_match_ids"] = plannerBlocker.Bodies.Select(body => body.NetId).ToArray(),
            ["body_matches_truncated"] = plannerBlocker.Truncated,
        };
        properties["nbrs"] = neighbours.ToArray();
        properties["neighbours_truncated"] = orderedNeighbours.Length > 16;
        // SessionLogger's fixed envelope/newline fits within this reserved headroom;
        // the check measures its complete emitted record, not just this payload.
        while (JsonSerializer.SerializeToUtf8Bytes(properties).Length > DetailLimitBytes - 512 &&
               neighbours.Count > 0)
        {
            neighbours.RemoveAt(neighbours.Count - 1);
            properties["nbrs"] = neighbours.ToArray();
            properties["neighbours_truncated"] = true;
        }
        if (JsonSerializer.SerializeToUtf8Bytes(properties).Length > DetailLimitBytes - 512)
        {
            properties["decision"] = null;
            properties["decision_truncated"] = true;
        }
        if (JsonSerializer.SerializeToUtf8Bytes(properties).Length <= DetailLimitBytes - 512)
            Write("osc", properties);
        else
            Write("movement_detail_suppressed", new Dictionary<string, object?>
            { ["netid"] = entity.NetId, ["reason"] = "size-limit", ["limit_bytes"] = DetailLimitBytes });

        int Priority(uint id) => plannerIds.Contains(id) ? 0 :
            colliders.Contains(id) ? 1 : avoiders.Contains(id) ? 2 : 3;
    }

    private void EmitOversizedDisplacement(Entity entity, Position2 before,
        Position2 intended, Position2? collision, Position2 committed, double dt,
        double speed, double withinStepDistance, double withinStepBudget,
        double crossStepDistance, double crossStepBudget, bool withinStep,
        bool crossStep, ForcedMoveState? forcedMove)
    {
        var forcedStatus = forcedMove is null ? "none" :
            forcedMove.Completed ? "completed" : "active";
        var properties = ActorProperties(entity.NetId);
        properties["diagnostic"] = "observed-authoritative-displacement";
        properties["before"] = Point(before);
        properties["intended"] = Point(intended);
        properties["collision"] = Point(collision);
        properties["committed"] = Point(committed);
        properties["dt"] = dt;
        properties["speed"] = entity.Speed;
        properties["budget_speed"] = speed;
        properties["within_step"] = withinStep;
        properties["within_step_distance"] = Round(withinStepDistance, 2);
        properties["within_step_budget"] = Round(withinStepBudget, 2);
        properties["cross_step"] = crossStep;
        properties["cross_step_distance"] = Round(crossStepDistance, 2);
        properties["cross_step_budget"] = Round(crossStepBudget, 2);
        properties["forced_move_status"] = forcedStatus;
        properties["forced_move_speed"] = forcedMove?.Speed;
        var signature = string.Join('/',
            forcedStatus, withinStep, crossStep,
            before.X.ToString("R"), before.Z.ToString("R"),
            committed.X.ToString("R"), committed.Z.ToString("R"),
            withinStepDistance.ToString("R"), crossStepDistance.ToString("R"));
        Coalesce(entity.NetId, "authoritative-displacement", signature, properties);
    }

    private void EmitSummary()
    {
        var elapsed = _diagnosticClock - _lastSummary;
        _lastSummary = _diagnosticClock;
        foreach (var state in _semantic.Values) EmitRepeats(state);
        var speeds = new List<double>();
        var alive = 0;
        foreach (var entity in _world.Entities.Values.Where(entity =>
                     entity.UnitType == UnitType.Minion && entity.Alive))
        {
            alive++;
            if (_summaryPositions.TryGetValue(entity.NetId, out var previous) &&
                entity.AiState == "march" && elapsed > 0.0)
                speeds.Add(Distance(previous, entity.Position) / elapsed);
            _summaryPositions[entity.NetId] = entity.Position;
        }
        speeds.Sort();
        Write("osc_summary", new Dictionary<string, object?>
        {
            ["alive"] = alive, ["osc"] = _flagged.Count, ["n"] = speeds.Count,
            ["v_med"] = speeds.Count == 0 ? null : Round(speeds[speeds.Count / 2], 1),
            ["v_p10"] = speeds.Count == 0 ? null : Round(speeds[Math.Clamp((int)(0.1 * speeds.Count), 0, speeds.Count - 1)], 1),
            ["br"] = new Dictionary<string, int>(_branches),
            ["detail_deferred"] = _deferredDetails, ["pending_details"] = PendingDetailCount,
            ["detail_limit_bytes"] = DetailLimitBytes, ["detail_slots_per_10s"] = 8,
        }, "INFO");
        _branches.Clear();
        _deferredDetails = 0;
    }

    private Dictionary<string, object?> ActorProperties(uint netId)
    {
        var entity = _world.Get(netId);
        var hudSeconds = HudTime?.Invoke();
        var hudWholeSeconds = hudSeconds is double seconds && double.IsFinite(seconds) && seconds >= 0.0
            ? (long?)Math.Floor(seconds) : null;
        return new()
        {
            // ServerLogger owns mono_ns at emission; keep the original observation
            // time separately when a repeating event is emitted in a later summary.
            ["observed_mono_ns"] = _world.Clock.MonotonicNanoseconds(), ["netid"] = netId,
            ["movement_elapsed"] = _diagnosticClock,
            ["hud_seconds"] = hudSeconds,
            ["hud_mmss"] = hudWholeSeconds is long whole
                ? $"{whole / 60:00}:{whole % 60:00}" : null,
            ["authority"] = Point(entity?.Position),
            ["unit_name"] = Clip(entity?.Name),
            ["minion_type"] = entity is null || entity.MinionType is not { } minionType
                ? null : minionType.ToString().ToUpperInvariant(),
            ["team"] = entity is null ? null : entity.Team.ToString().ToUpperInvariant(),
            ["lane"] = Clip(entity?.MinionLane),
            ["order_id"] = _orderIds.GetValueOrDefault(netId),
            ["last_waypoint_sync_id"] = _lastWaypointSync.GetValueOrDefault(netId),
            ["client_anchor"] = Point(entity is { AnchorPath.Count: > 0 }
                ? entity.AnchorPath[0] : null),
            ["ai_state"] = Clip(entity?.AiState),
            ["attack_target"] = entity?.AttackTarget,
            ["repath_count"] = entity?.RepathCount,
        };
    }

    private void PruneDead()
    {
        bool Retired(uint id) => _world.Get(id) is not { Alive: true };
        foreach (var id in _rings.Keys.Where(Retired).ToArray()) _rings.Remove(id);
        foreach (var id in _snapshots.Keys.Where(Retired).ToArray()) _snapshots.Remove(id);
        foreach (var id in _snapshotTimes.Keys.Where(Retired).ToArray()) _snapshotTimes.Remove(id);
        foreach (var id in _lastDump.Keys.Where(Retired).ToArray()) _lastDump.Remove(id);
        foreach (var id in _summaryPositions.Keys.Where(Retired).ToArray()) _summaryPositions.Remove(id);
        foreach (var id in _decisions.Keys.Where(Retired).ToArray()) _decisions.Remove(id);
        foreach (var id in _plannerBlockers.Keys.Where(Retired).ToArray()) _plannerBlockers.Remove(id);
        foreach (var id in _orderIds.Keys.Where(Retired).ToArray()) _orderIds.Remove(id);
        foreach (var id in _lastWaypointSync.Keys.Where(Retired).ToArray()) _lastWaypointSync.Remove(id);
        foreach (var id in _minionAi.Keys.Where(Retired).ToArray()) _minionAi.Remove(id);
        foreach (var id in _lastAiTransition.Keys.Where(Retired).ToArray()) _lastAiTransition.Remove(id);
        foreach (var id in _pendingAiTransition.Keys.Where(Retired).ToArray())
        {
            Write("minion_ai_transition", _pendingAiTransition[id]);
            _pendingAiTransition.Remove(id);
        }
        foreach (var key in _semantic.Keys.Where(key => Retired(key.NetId)).ToArray())
        {
            EmitRepeats(_semantic[key]);
            _semantic.Remove(key);
        }
        foreach (var key in _waypointIntents.Keys.Where(key => Retired(key.NetId)).ToArray()) _waypointIntents.Remove(key);
        _flagged.RemoveWhere(Retired);
        _pending.RemoveWhere(Retired);
        _oversized.RemoveWhere(Retired);
        _oversizedPending.RemoveWhere(Retired);
    }

    private void Write(string message, IReadOnlyDictionary<string, object?> properties,
        string level = "DEBUG")
    {
        try { _log?.Write("AI", level, message, properties); }
        catch { /* logging failures remain observational */ }
    }

    private void Clear()
    {
        _rings.Clear(); _snapshots.Clear(); _snapshotTimes.Clear();
        _flagged.Clear(); _pending.Clear(); _oversized.Clear(); _oversizedPending.Clear();
        _lastDump.Clear(); _recentDumps.Clear(); _summaryPositions.Clear();
        _branches.Clear(); _semantic.Clear(); _decisions.Clear(); _orderIds.Clear();
        _lastWaypointSync.Clear(); _minionAi.Clear();
        _lastAiTransition.Clear(); _pendingAiTransition.Clear();
        _plannerBlockers.Clear();
        _waypointIntents.Clear();
        _deferredDetails = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Enabled)
        {
            foreach (var state in _semantic.Values) EmitRepeats(state);
            foreach (var transition in _pendingAiTransition.Values)
                Write("minion_ai_transition", transition);
        }
        _disposed = true;
        Clear();
    }

    private bool IsHero(uint id) => _world.Get(id)?.UnitType == UnitType.Hero;
    private static string Clip(string? value, int length = 96) =>
        value is null ? "" : value[..Math.Min(value.Length, length)];
    private static double[]? Point(Position2? point) =>
        point is Position2 p ? [p.X, p.Z] : null;
    private static Position2? ReadPoint(object? value) =>
        value is double[] { Length: >= 2 } point ? new Position2(point[0], point[1]) : null;
    private static double Round(double value, int digits) => Math.Round(value, digits);
    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));
    private static double RoundedNet(RingEntry first, RingEntry last) =>
        Distance(new Position2(Round(first.Start.X, 2), Round(first.Start.Z, 2)),
            new Position2(Round(last.Start.X, 2) + Round(last.Committed.X - last.Start.X, 2),
                Round(last.Start.Z, 2) + Round(last.Committed.Z - last.Start.Z, 2)));
    private static object?[] ToProperties(RingEntry entry) =>
    [
        Round(entry.Start.X, 2), Round(entry.Start.Z, 2),
        Round(entry.March.X - entry.Start.X, 2), Round(entry.March.Z - entry.Start.Z, 2),
        Round(entry.Committed.X - entry.Start.X, 2), Round(entry.Committed.Z - entry.Start.Z, 2),
        Round(entry.Distance, 2), Clip(string.Join('|', entry.Branches)),
        entry.Colliders.Count, entry.AvoidCount, entry.ColliderDot,
        entry.Halted ? 1 : 0, Round(entry.Separation, 1),
    ];

    private sealed record RingEntry(Position2 Start, Position2 March, Position2 Committed,
        double Distance, IReadOnlyList<string> Branches, IReadOnlyList<uint> Colliders,
        int AvoidCount, double? ColliderDot, bool Halted, double Separation,
        Position2? Candidate, Position2? Goal, double DeltaTime, double Speed, bool Static);

    private sealed class SemanticState(string signature, Dictionary<string, object?> latest)
    {
        public string Signature { get; } = signature;
        public Dictionary<string, object?> Latest { get; set; } = latest;
        public int Repeats { get; set; }
    }

    private sealed record BodyAtDecision(uint NetId, Position2 Position, double Radius,
        string UnitType, string State, int RepathCount);

    private sealed record PlannerBlocker(long ObservedAt, Position2 Position, double? Clearance,
        string Phase, IReadOnlyList<BodyAtDecision> Bodies, bool Truncated);

    private sealed class WaypointIntent(string reason, int count, Position2? anchor, Position2? goal,
        IReadOnlyList<Position2> route)
    {
        public string Reason { get; } = reason;
        public int Count { get; } = count;
        public Position2? Anchor { get; } = anchor;
        public Position2? Goal { get; } = goal;
        public IReadOnlyList<Position2> Route { get; } = route;
        public bool Ambiguous { get; set; }
        public int Outcomes { get; set; }
    }
}
