using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;

namespace Amberline.Simulation.Movement;

/// <summary>
/// Authoritative movement and collision solve. The service is
/// deliberately per-world: all frame snapshots and solver state belong to
/// this instance and are never process globals.
/// </summary>
public sealed partial class MovementSystem
{
    public const double FixedTickSeconds = 0.1;
    public const double HeroTickSeconds = 0.05;
    // Hero-only: the stock client's render smoother (ProccessClientSidePath, 162.5-unit drag and 225-unit
    // stop bands) runs in the hero DeadRecon branch. Correct a hero's collision drift before it reaches the
    // drag band, leaving room for the next step. Lane minions have no such bands: every 0xC1 to a minion
    // is a hard snap (real-client telemetry, 16 Oct 2026), so no minion path has a correction threshold.
    private const double HeroCollisionResyncDistance = 119.0;

    private readonly WorldState _world;
    private readonly NavigationGrid _navigation;
    private readonly MovementSettings _settings;
    private readonly IMovementDiagnosticSink? _diagnosticSink;
    private readonly Dictionary<uint, List<(Entity Entity, Position2 Position)>> _frameColliders = new();
    private readonly Dictionary<uint, List<(Entity Entity, Position2 Position)>> _frameAvoiders = new();
    private readonly Dictionary<uint, double> _frameQueryRadii = new();
    private readonly Dictionary<uint, List<string>> _frameBranches = new();
    private readonly Dictionary<uint, int> _frameAvoidCounts = new();
    private readonly Dictionary<uint, double> _frameColliderDots = new();
    private readonly HashSet<uint> _frameHalted = new();
    // Actors whose stop arrived this frame: the seat and the floor correction move them, but the facing travels in the stop packet.
    private readonly HashSet<uint> _frameStopped = new();
    // Read once at the start of a movement frame. Diagnostic fact capture is
    // observational and must not allocate per-unit state when logging is off.
    private bool _diagnosticsEnabled;
    private readonly Dictionary<uint, HeroMoveOrder> _heroOrders = new();
    private double _heroMovementElapsed;
    private double _nonHeroMovementElapsed;
    private double _simulationElapsed;
    public Action<Entity, IReadOnlyList<Position2>>? HeroPathChanged { get; set; }
    /// <summary>The wire of a stop the update itself makes for a unit other than a hero (<see cref="ForceStopServer"/>); set by the composition.</summary>
    internal Action<Entity, IReadOnlyList<Position2>>? ServerStopPublished { get; set; }
    /// <summary>
    /// DIAGNOSTICS: diagnostics only. Routes the server published for a hero without a player order, by cause
    /// (the headless match report prints them).
    /// </summary>
    public Dictionary<string, long> HeroAutomaticRoutes { get; } = new(StringComparer.Ordinal);

    /// <summary>DIAGNOSTICS: diagnostics only. Counts the plans of the hero chase scan and those that end beyond attack reach.</summary>
    internal void CountChasePlan(bool endsWithinReach)
    {
        HeroAutomaticRoutes["chase-plan"] = HeroAutomaticRoutes.GetValueOrDefault("chase-plan") + 1;
        if (!endsWithinReach)
            HeroAutomaticRoutes["chase-plan-short"] = HeroAutomaticRoutes.GetValueOrDefault("chase-plan-short") + 1;
    }

    private void PublishAutomaticHeroRoute(Entity hero, IReadOnlyList<Position2> route, string cause)
    {
        // The route starts on the hero's lattice anchor, where the install seats the client's actor after
        // stopping it (the stored velocity is zeroed).
        if (route.Count > 0) hero.Position = route[0];
        hero.FrameVelocity = Position2.Zero;
        HeroAutomaticRoutes[cause] = HeroAutomaticRoutes.GetValueOrDefault(cause) + 1;
        HeroPathChanged?.Invoke(hero, route);
    }
    internal Action<double, IReadOnlyList<(uint NetId, double Speed,
        bool PathFollowerAllowed, IReadOnlyList<Entity> Colliders)>>? FixedStepCompleted { get; set; }
    internal Action<MovementDecisionObservation>? DecisionObserver { get; set; }
    // DIAGNOSTICS: Expose committed movement facts for the read-only playtest monitor.
    internal Action<Entity, Position2, double, double, bool>? StepObserver { get; set; }

    // Reconstruction: the server keeps a hero's installed route only to publish a correction once collision drift passes
    // HeroCollisionResyncDistance, as the stock client's render bands and docs/minions/client_path_follower_re.md record. The shipped
    // client walks the path it was sent; whether the retail server re-published a hero's route on drift is not in the
    // shipped files. Retail server reconciliation is unknown, as docs/gameplay/patch126-movement.md records, so the behaviour is kept.
    private sealed record HeroMoveOrder(Position2 Goal, List<Position2> Route)
    {
        public Position2 CollisionDisplacement { get; set; }
    }

    internal enum MoveStatus { Moving, Unreachable }
    internal sealed record MovePlan(Position2 Goal, IReadOnlyList<Position2> Path, MoveStatus Status);
    private readonly record struct HeroFollowerValidation(
        bool Clear, int BlockingObstacleIndex, string Phase);

    /// <summary>
    /// Returns the body radius used by movement only. AI NPCs use the
    /// configured movement shell; gameplay and wire consumers continue to
    /// read Entity.Radius directly.
    /// </summary>
    public double MovementRadius(Entity entity)
    {
        if (IsStamped(entity)) return 0.0;
        var radius = Math.Max(0.0, entity.PathfindingRadius ?? entity.Radius);
        return IsScaledNpc(entity) ? radius * _settings.NpcMovementRadiusScale : radius;
    }

    /// <summary>
    /// The Nexus and the inhibitors are no actors on the stock client: the nexus's nav-grid stamp and
    /// the inhibitor's nav-grid stamp write nav-cell flags and register nothing in the actor grid, so nothing collides
    /// with them, no planner tests them as an actor and no solver frame is pushed off them. Their footprint is the filled circle of
    /// their pathfinding radius the navigation views carry (<see cref="NavigationGrid.StampFilledCircle"/>). A turret is both: a
    /// registered actor and a 140-unit stamp.
    /// </summary>
    internal static bool IsStamped(Entity entity) => entity.Kind is EntityKind.Nexus or EntityKind.Inhibitor;

    /// <summary>
    /// Returns the movement body term from one member's perspective. NPC to
    /// NPC pairs use their configured separation shell; production hero/NPC
    /// pairs use shipped radii. Historical minion/champion tuning applies to
    /// the minion term afterward. Lane-minion pairs use the unscaled radius: the
    /// stock client moves them against each other with the full shipped body
    /// (playtest B2 AE5, 26 Sept 2026).
    /// </summary>
    internal double PairMovementRadius(Entity entity, Entity other)
    {
        if (IsStamped(entity)) return 0.0;
        var radius = Math.Max(0.0, entity.PathfindingRadius ?? entity.Radius);
        if (entity.MinionType is not null && other.MinionType is not null) return radius;
        var pairRadius = IsScaledNpc(entity) && IsScaledNpc(other)
            ? radius * _settings.NpcToNpcMovementRadiusScale
            : MovementRadius(entity);
        return entity.UnitType == UnitType.Minion && other.UnitType == UnitType.Hero
            ? pairRadius * _settings.MinionUnitCollisionScale
            : pairRadius;
    }

    /// <summary>
    /// Conservative broad-phase bound. The recovered maximum is intentionally
    /// retained here so a reduced NPC shell can never omit a candidate; exact
    /// pair gates below use the perspective-aware pair radius for the actual
    /// collision decision.
    /// </summary>
    public double MaximumMovementRadius => _settings.MaximumUnitRadius;

    public double BodyClearance(Entity first, Entity second) =>
        PairClearance(first, second, _settings.Collision.SkinHighCollider);

    /// <summary>
    /// The factor on the radius a mover brings to its own pair and cluster tests, 0.2 for a flagged actor (every
    /// hero) and 1 otherwise. A flagged hero is thin for its own movement and full width as an obstacle for everyone else.
    /// </summary>
    internal static float MoverFactor(Entity entity) => entity.PathingFlag290 ? 0.2f : 1f;

    /// <summary>The radius <paramref name="entity"/> brings to the pair test when it moves into <paramref name="other"/>.</summary>
    internal double MoverPairRadius(Entity entity, Entity other) =>
        ClientFloat.Mul((float)PairMovementRadius(entity, other), MoverFactor(entity));

    /// <summary>
    /// The distance at which the client's collision pass stops <paramref name="mover"/> against <paramref name="other"/>: the mover's thin
    /// radius, the other's full one and the pair skin. <see cref="BodyClearance"/> stays the full-width figure the spell placements use.
    /// </summary>
    internal double ContactClearance(Entity mover, Entity other)
    {
        var first = MoverPairRadius(mover, other);
        var second = PairMovementRadius(other, mover);
        return first + second + PairSkin(first, second, _settings.Collision.SkinHighCollider);
    }

    private double PairSkin(double firstRadius, double secondRadius, double skinHigh) =>
        Math.Min(Math.Max(_settings.Collision.SkinRatio * Math.Min(firstRadius, secondRadius),
                          _settings.Collision.SkinLow), skinHigh);

    private double PairClearance(Entity first, Entity second, double skinHigh) =>
        PairMovementRadius(first, second) + PairMovementRadius(second, first) +
        PairSkin(PairMovementRadius(first, second), PairMovementRadius(second, first), skinHigh);

    public MovementSystem(
        WorldState world,
        NavigationGrid navigation,
        MovementSettings? settings = null,
        IMovementDiagnosticSink? diagnosticSink = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _settings = settings ?? new MovementSettings();
        _settings.Validate();
        _diagnosticSink = diagnosticSink;
    }

    public WorldState World => _world;
    public NavigationGrid Navigation => _navigation;
    public MovementSettings Settings => _settings;

    // These switches are an internal source-control seam for the original
    // collision attribution runs.  Live composition never changes them; the
    // test friend reruns the same production wave with one client mechanism
    // disabled at a time.
    internal bool CollisionSolverEnabled { get; set; } = true;
    internal bool MovementCellWalkabilityEnabled { get; set; } = true;
    // Source collision regressions compare the shipped no-bend control with
    // the maintained branch.  This is test-only and never changed by live
    // composition.
    internal bool BendWaypointsEnabled { get; set; } = true;

    /// <summary>Uses the fixed NPC movement cadence.</summary>
    public void Tick() => Tick(_settings.FixedTickSeconds);

    /// <summary>
    /// Advances all actors for one movement frame. Direct scenarios retain
    /// this explicit interval; production schedules hero and NPC frames apart.
    /// </summary>
    public void Tick(double dt) => TickFrame(dt, null);

    // Production supplies time before spell work and accumulated movement frames.
    // Direct movement callers advance their cohort clocks through Tick instead.
    internal void AdvanceSimulationTime(double dt) => _simulationElapsed += dt;

    internal void TickHeroes() => TickFrame(HeroTickSeconds, true);
    internal void TickNonHeroes() => TickFrame(FixedTickSeconds, false);

    private static bool IsDue(Entity entity, bool? heroesOnly) =>
        heroesOnly is null || (entity.UnitType == UnitType.Hero) == heroesOnly.Value;

    private void TickFrame(double dt, bool? heroesOnly)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));

        if (heroesOnly != false) _heroMovementElapsed += dt;
        if (heroesOnly != true) _nonHeroMovementElapsed += dt;
        var cohortElapsed = heroesOnly == false ? _nonHeroMovementElapsed : _heroMovementElapsed;

        var fixedStepCompleted = heroesOnly == true ? null : FixedStepCompleted;
        var fixedStepActors = fixedStepCompleted is null
            ? null
            : new List<(uint NetId, double Speed, bool PathFollowerAllowed,
                IReadOnlyList<Entity> Colliders)>();
        _diagnosticsEnabled = _diagnosticSink is { Enabled: true };
        // Every actor, heroes included, runs the stock client's sequential per-actor update (ActorFrame, MovementSystem.ClientSolver.cs).
        var runHeroes = heroesOnly != false;
        var runNpcs = heroesOnly != true;
        _frameColliders.Clear();
        _frameAvoiders.Clear();
        _frameQueryRadii.Clear();
        _frameBranches.Clear();
        _frameAvoidCounts.Clear();
        _frameColliderDots.Clear();
        _frameHalted.Clear();
        _frameStopped.Clear();
        var starts = new Dictionary<uint, Position2>();
        foreach (var entity in _world.Entities.Values)
            if (entity.Alive) starts[entity.NetId] = entity.Position;

        // The intended step and the solver's candidate of every actor, for the diagnostic sink only.
        var intendedPositions = _diagnosticsEnabled ? new Dictionary<uint, Position2>(starts) : null;
        var collisionCandidates = _diagnosticsEnabled ? new Dictionary<uint, Position2>(starts) : null;
        // The stock scheduler ticks heroes first, then every other actor in vector order.
        if (runHeroes) ActorFrame(dt, true, intendedPositions, collisionCandidates, fixedStepActors);
        if (runNpcs) ActorFrame(dt, false, intendedPositions, collisionCandidates, fixedStepActors);
        if (fixedStepActors is not null)
            for (var index = 0; index < fixedStepActors.Count; index++)
            {
                var step = fixedStepActors[index];
                var actor = _world.Get(step.NetId)!;
                if (actor.MinionType is null || !step.PathFollowerAllowed) continue;
                // Reconstruction: native AI callback dispatch is unrecovered.
                // Snapshot real collider admission (not soft avoidance).
                fixedStepActors[index] = (step.NetId, step.Speed, step.PathFollowerAllowed,
                    _frameColliders.TryGetValue(step.NetId, out var contacts)
                        ? contacts.Select(contact => contact.Entity).ToArray() : Array.Empty<Entity>());
            }
        foreach (var entity in _world.Entities.Values)
        {
            if (!IsDue(entity, heroesOnly) || !IsMobileBody(entity) || !starts.ContainsKey(entity.NetId) ||
                entity.ForcedMove is not null || _frameStopped.Contains(entity.NetId))
                continue;
            var start = starts[entity.NetId];
            Geometry.FaceTowards(entity,
                new Position2(2.0 * entity.Position.X - start.X,
                              2.0 * entity.Position.Z - start.Z));
        }

        if (_diagnosticsEnabled && _diagnosticSink is not null)
        {
            var snapshots = new List<MovementDiagnosticSnapshot>();
            foreach (var entity in _world.Entities.Values)
            {
                if (!starts.ContainsKey(entity.NetId) || !IsDue(entity, heroesOnly)) continue;
                var colliders = _frameColliders.TryGetValue(entity.NetId, out var list)
                    ? list.Select(item => item.Entity.NetId).ToArray()
                    : Array.Empty<uint>();
                var avoiders = _frameAvoiders.TryGetValue(entity.NetId, out var avoidList)
                    ? avoidList.Select(item => item.Entity.NetId).ToArray()
                    : Array.Empty<uint>();
                snapshots.Add(new MovementDiagnosticSnapshot(
                    entity.NetId, colliders, entity.StuckElapsed,
                    entity.RepathAttempts, entity.RepathCount,
                    entity.SeparationAccumulation,
                    starts[entity.NetId], intendedPositions![entity.NetId], entity.Position,
                    _frameBranches.TryGetValue(entity.NetId, out var branches)
                        ? branches.ToArray() : Array.Empty<string>(),
                    _frameAvoidCounts.TryGetValue(entity.NetId, out var avoidCount)
                        ? avoidCount : 0,
                    _frameColliderDots.TryGetValue(entity.NetId, out var colliderDot)
                        ? colliderDot : null,
                    _frameHalted.Contains(entity.NetId),
                    avoiders,
                    _frameQueryRadii.GetValueOrDefault(entity.NetId),
                    IsStatic(entity),
                    dt,
                    collisionCandidates is not null &&
                    collisionCandidates.TryGetValue(entity.NetId, out var candidate)
                        ? candidate : null,
                    entity.AttackMoveGoal ?? entity.HeroPursuit?.LastKnownPosition ??
                        RetainedHeroGoal(entity) ?? entity.Target));
            }
            try
            {
                _diagnosticSink.OnMovementTick(dt, cohortElapsed, snapshots);
            }
            catch
            {
                // A diagnostic sink must never bring down a live simulation.
            }
        }
        if (StepObserver is { } observer)
            foreach (var entity in _world.Entities.Values)
            {
                if (!entity.Alive || !IsDue(entity, heroesOnly) ||
                    !starts.TryGetValue(entity.NetId, out var start)) continue;
                observer(entity, start, entity.Speed, dt,
                    entity.ForcedMove is not null || entity.ForcedMoveGrace > 0);
            }
        NoteMovementFrame(heroesOnly);
        fixedStepCompleted?.Invoke(dt, fixedStepActors!);
    }

    /// <summary>
    /// Applies a server move order and returns the exact client-grid route.
    /// </summary>
    public IReadOnlyList<Position2> SetMoveOrder(
        uint netId,
        Position2 target,
        Func<Entity, bool>? visibleTo = null)
    {
        var entity = _world.Get(netId);
        if (entity is null) return Array.Empty<Position2>();
        if (entity.ForcedMove is not null || IsRooted(entity)) return RefuseOrder(entity);
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Z))
            throw new ArgumentOutOfRangeException(nameof(target));

        return CommitMoveOrder(entity, PlanOrder(entity, target, visibleTo));
    }

    internal MovePlan PlanOrder(Entity entity, Position2 target, Func<Entity, bool>? visibleTo = null)
    {
        var path = PlanMoveRoute(entity, target, visibleTo);
        return new MovePlan(target, path, path.Count > 1 ? MoveStatus.Moving : MoveStatus.Unreachable);
    }

    internal bool RouteClearOfObstacle(IReadOnlyList<Position2> route,
        NavigationObstacle obstacle)
    {
        if (route.Count < 2) return false;
        for (var index = 0; index + 1 < route.Count; index++)
            if (!_navigation.SegmentClearOfUnits(route[index], route[index + 1],
                    [obstacle], escape: index == 0))
                return false;
        return true;
    }

    /// <summary>
    /// The visibility rule of a hero's own planning (<c>other</c> is a unit the hero's client knows), set by the composition; the planner treats a
    /// unit the hero cannot see as no actor.
    /// </summary>
    public Func<Entity, Entity, bool>? HeroPlanVisibility { get; set; }

    /// <summary>
    /// <c>BBForceRefreshPath</c>: the unit's pathing is asked to plan again on the next update (<c>m_ForceRefreshPath</c>). A hero
    /// that walks a move order plans it again from where it stands to the same goal, with the bodies that exist now, and the new route is published
    /// like any automatic hero route; a hero without a move order, a dead or rooted one and a forced move keep what they have. The attack-move
    /// goal and the pursuit of the order survive the new plan. Jarvan's Cataclysm refreshes every hero within 1200 once its walls stand.
    /// The other units are never asked (batch 315): the native's body sets the refresh byte (AI
    /// manager) of any unit that has an AI manager, with no type test, but its one shipped caller, JarvanIVCataclysm
    /// (instructions 0840-0871), scans 1200 units with AffectEnemies AffectFriends AffectHeroes and calls it only under a BBIf
    /// <c>CO_IS_TYPE_HERO</c>, so the hero-only body covers every shipped call.
    /// </summary>
    internal void ForceRefreshPath(Entity unit)
    {
        if (unit.UnitType != UnitType.Hero || unit.OwnerNetId != 0 || !unit.Alive || unit.ForcedMove is not null || IsRooted(unit)) return;
        if (RetainedHeroGoal(unit) is not { } goal) return;
        var attackMove = unit.AttackMoveGoal;
        var pursuit = unit.HeroPursuit;
        var visible = HeroPlanVisibility;
        var route = SetMoveOrder(unit.NetId, goal, visible is null ? null : other => visible(unit, other));
        unit.AttackMoveGoal = attackMove;
        unit.HeroPursuit = pursuit;
        if (route.Count > 1) PublishAutomaticHeroRoute(unit, route, "force-refresh-path");
    }

    internal Position2? RetainedHeroGoal(Entity entity) =>
        _heroOrders.TryGetValue(entity.NetId, out var order) &&
        ReferenceEquals(entity.Waypoints, order.Route) && entity.Waypoints.Count > 0
            ? order.Goal : null;

    /// <summary>
    /// The answer to an order the unit cannot walk, which its owner publishes as the stationary one-point path. A rooted unit's client
    /// installs that path (the install has no root test), so the unit is seated at once (<see cref="InstallStop"/>); a forced move owns the
    /// unit's position and route until it ends, so that unit is left where it stands. Reconstruction: the retail server's refusal is server code.
    /// </summary>
    private IReadOnlyList<Position2> RefuseOrder(Entity entity) => entity.ForcedMove is null ? InstallStop(entity) : [entity.Position];

    internal IReadOnlyList<Position2> CommitMoveOrder(Entity entity, MovePlan plan)
    {
        if (entity.ForcedMove is not null || IsRooted(entity)) return RefuseOrder(entity);
        var path = plan.Path;

        entity.AttackMoveGoal = null;
        entity.HeroPursuit = null;
        // No route means no movement (a stop): callers may retry, but cannot leave an unreplicated straight-line order through failed
        // navigation. The planner's first cell can be offset from the actual actor, so the route starts where the unit stands.
        var route = InstallPath(entity, path);
        if (path.Count > 1 && entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0)
            _heroOrders[entity.NetId] = new HeroMoveOrder(plan.Goal, entity.Waypoints);
        else _heroOrders.Remove(entity.NetId);
        return route;
    }

    /// <summary>
    /// The install every published path runs on the client, whatever the unit and whoever planned the path:
    /// <c>ClientFollowServerPath</c> (its callers, the waypoint handlers, and
    /// the AI packet handler make no unit-type test) stops the actor first (the stop helper
    /// zeroes the stored velocity) and seats it on the path's first point with no terrain
    /// test, whatever cell that point falls in. The server's unit stands where its client copy does (batch 254 for orders, batch 263 for every
    /// other route), so every route a unit is given goes through here before it is published. <paramref name="path"/> starts where the unit
    /// stands; its first point is the unit's two-unit lattice anchor. The planner ran from the exact position before this, and a blocked anchor
    /// cell does not stop the next order: the A* starts on the actor's own cell untested and the frame gate refuses only a blocked destination
    /// cell (HandleActorCollision). An empty path clears the route (a stop; <see cref="ArriveOnStop"/> seats it on its one-point path).
    /// Returns the route as the wire carries it.
    /// </summary>
    internal IReadOnlyList<Position2> InstallPath(Entity entity, IReadOnlyList<Position2> path) => InstallPath(entity, path, Bounds);

    /// <summary>
    /// A stop published at once as a one-point path: the same install seats the unit on its anchor and stops it there, and the unit keeps no
    /// route. Every published stop goes through here (batch 279): a teleport's landing and a respawn, the hero's stop order and AI stop, an
    /// order's refusal, the minion, pet, spawned-unit, monster and bot stops, a script move's end, a channel's and a windup cast's stop, the
    /// end-of-game halt and the zombie's stand-up. A stop that publishes nothing installs nothing; its next frame's stop arrival
    /// (<see cref="ArriveOnStop"/>) seats a unit that was following a route. Returns the one-point path as the wire carries it.
    /// </summary>
    internal IReadOnlyList<Position2> InstallStop(Entity entity)
    {
        var route = InstallPath(entity, [entity.Position]);
        entity.Waypoints.Clear();
        return route;
    }

    /// <summary>
    /// A fear wander (the walk half of <c>SetStateAndMove(AI_FEARED, point)</c> for minions, pets, spawned units, monsters and heroes, and
    /// the Jack-in-the-Box fear): the forced <c>Fear</c> move to the first collision landing, published as a two-point path, which the
    /// client installs like every path (the actor stopped and seated on the first point, its lattice anchor). The landing is
    /// measured from that anchor; the forced move walks the segment, so the unit keeps no route (the cancel has saved a minion's lane).
    /// Returns the path as the wire carries it.
    /// </summary>
    internal IReadOnlyList<Position2> StartFearWander(Entity unit, Position2 point, double duration, uint casterNetId)
    {
        var landing = FirstCollisionLanding(ClientAnchor(unit.Position), point, unit.Speed, duration, unit);
        StartForcedMove(unit.NetId, new ForcedMoveState(landing, unit.Speed, duration, casterNetId, "Fear", firstCollision: true),
            cancelOrder: true);
        var route = InstallPath(unit, [unit.Position, landing]);
        unit.Waypoints.Clear();
        return route;
    }

    /// <inheritdoc cref="InstallPath(Entity, IReadOnlyList{Position2})"/>
    internal static IReadOnlyList<Position2> InstallPath(Entity entity, IReadOnlyList<Position2> path, NavigationBounds bounds)
    {
        entity.FrameVelocity = Position2.Zero;
        entity.Target = null;
        if (path.Count == 0)
        {
            entity.Waypoints.Clear();
            return [];
        }
        entity.Position = Geometry.ClientWaypoints([entity.Position], bounds)[0];
        entity.Waypoints = new[] { entity.Position }.Concat(path.Skip(1)).ToList();
        return Geometry.ClientWaypoints(entity.Waypoints, bounds);
    }

    /// <summary>
    /// The route of a move order, planned the way the engine plans every unit's order: the base unit's order step calls
    /// the client's path builder, the port of which is <see cref="PlanClientHeroRoute"/>. A hero's echo then carries the path
    /// its client computed from the same click; every other unit is planned by the same code, with every unit it knows (the server knows them
    /// all). The planner works on the client's movement grid, so no route crosses a blocked cell. When it plans nothing the order is a stop on
    /// the spot: a hero's echo is the one-point route, and another unit's route is cleared (its owner publishes the stop, and the solver's
    /// stop arrival runs). DIAGNOSTICS: a check that stands a scenario planner in for navigation
    /// (<see cref="NavigationGrid.FindPathOverride"/>) has it stand in for a non-hero's planner too.
    /// </summary>
    private IReadOnlyList<Position2> PlanMoveRoute(Entity entity, Position2 target, Func<Entity, bool>? visibleTo)
    {
        var hero = entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0;
        if (!hero && _navigation.FindPathOverride is not null)
        {
            var scenario = Restricted(entity, ClientWaypoints(_navigation.FindPath(entity.Position, target)));
            return scenario.Count > 1 ? scenario : [];
        }
        var route = PlanClientHeroRoute(entity, target, hero ? visibleTo : visibleTo ?? (static _ => true));
        route = route is null ? null : Restricted(entity, route);
        return route is { Count: > 1 } ? route : hero ? ClientWaypoints([entity.Position]) : [];
    }

    /// <summary>
    /// Batch 458: a unit under <c>SetUnitCircularMovementRestriction</c> (<see cref="Entity.MovementRestriction"/>) walks its route only up to
    /// where the route leaves the circle, and stops there. Reconstruction: the shipped node is server-only (the client has no such native;
    /// the stock table names only the level script's <c>SetCircularCameraRestriction</c>), so how retail held the unit is not recovered; cutting
    /// the order's route keeps the client, which installs the server's route, on the same path. The cut stands one lattice step inside the
    /// circle. Batch 466: a unit pushed past the circle (a collision, a knock-back) is held at the distance it stands at: it may walk back in but
    /// no further out (<see cref="HoldLimit"/>; batch 463 saw a bot pushed past the 1200 circle walk free).
    /// </summary>
    private IReadOnlyList<Position2> Restricted(Entity entity, IReadOnlyList<Position2> route)
    {
        if (route.Count < 2) return route;
        // Batch 474f: the held ground (HeldGround) holds a champion of either team in the opening window even when no circle was set.
        if (entity.MovementRestriction is not { } circle)
            return OpeningHeld(entity) ? HeldCuts(entity, route) : route;
        var limit = HoldLimit(circle, route[0]);
        static double Squared(Position2 a, Position2 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
        var kept = new List<Position2> { route[0] };
        for (var index = 1; index < route.Count; index++)
        {
            if (Squared(route[index], circle.Centre) <= limit * limit)
            {
                kept.Add(route[index]);
                continue;
            }
            var exit = ClientAnchor(CircleExit(route[index - 1], route[index], circle.Centre, limit));
            if (exit != kept[^1]) kept.Add(exit);
            break;
        }
        return HeldCuts(entity, kept);
    }

    // Batch 474: the held route stops where it leaves the held ground. Batch 514: one cut, by the hold's single limit (HeldGround).
    private IReadOnlyList<Position2> HeldCuts(Entity entity, IReadOnlyList<Position2> route) => CutToward(entity, route, HeldGround(entity, entity.Position));

    /// <summary>Batch 504: how far past the forcefield's wall a unit may stand and still be held by it (see <see cref="HeldGround"/>).</summary>
    private const double ForcefieldSlack = 16.0;

    /// <summary>
    /// The ground a held champion standing at <paramref name="from"/> may walk, dash or step onto during the opening hold. Batch 514b: the
    /// forcefield's limit is the wall less the champion's own body (<see cref="HoldFootprint.Limit"/>, <see cref="Entity.CombatRadius"/>).
    /// <list type="bullet">
    /// <item>Batch 504 (NOT RETAIL, owner decision 31 October 2026): inside the visible forcefield's wall
    /// (<see cref="HoldFootprint.ForcefieldDistance"/>), the unit is held inside it. Just past it (within <see cref="ForcefieldSlack"/>), the unit is
    /// held at the distance it stands at, so it may walk back in and never further out: a stop seats a unit on its 2-unit lattice anchor
    /// (<see cref="InstallPath(Entity, IReadOnlyList{Position2})"/>, <see cref="ArriveOnStop"/>), up to 1.4 units past the cut, and the cut's
    /// "already outside" rule (<see cref="CutToward"/>) would then free it.</item>
    /// <item>Batch 514 (owner, 31 October 2026: the visible blue ring is the only limit before 1:20): that circle is the only limit for such a
    /// unit. The owner's game of 31 October (19:37) stopped the hero at the stairs footprint (batch 474d) 135 to 200 from the barrier, deep
    /// inside the ring, and froze him at its edge: from (825, 3777) and (679, 3985) every order west was cut at the start, because the straight
    /// leg crossed the footprint. The footprint and the platform are not stricter than the ring for any reason the ring lacks: within 440 of
    /// either barrier every shipped grid cell is walkable at or above <see cref="NavigationGrid.PlatformFloor"/> (no void, no blocked cell), and
    /// the footprint is all such ground, inside the ring and out.</item>
    /// <item>Batch 474 (Reconstruction): a unit further out (a push, or a check that places it) is not held by the forcefield; it keeps the base
    /// platform (<see cref="NavigationGrid.IsPlatformPoint"/>), which keeps it off the exit ramps (the void before the stairs form), and a unit
    /// already off the platform keeps the 1200 circle only (<see cref="CutToward"/>'s early return).</item>
    /// </list>
    /// </summary>
    private Func<Position2, bool> HeldGround(Entity entity, Position2 from)
    {
        var limit = HoldFootprint.Limit(entity.CombatRadius);
        var standing = HoldFootprint.ForcefieldDistance(from);
        if (standing > limit + ForcefieldSlack) return _navigation.IsPlatformPoint;
        var held = Math.Max(limit, standing);
        return point => HoldFootprint.ForcefieldDistance(point) <= held;
    }

    /// <summary>
    /// Batch 474: while the setup hold stands, a held unit's route also stops at the base platform's edge (<see cref="NavigationGrid.IsPlatformPoint"/>).
    /// Map 8's walkable exits are ramps that descend from the platform (heights -37 to -189); the slab path that would carry the route
    /// has not formed before the release, so the route is cut where it leaves the platform. The cut is the last allowed point of the leg
    /// (<see cref="LastAllowed"/>). A unit already off the allowed ground (a push, or a test that places it) is held by the circle alone
    /// (<see cref="HoldLimit"/>), so it can walk back. Reconstruction, as the hold itself. Batch 514: the allowed ground is <see cref="HeldGround"/>.
    /// </summary>
    private IReadOnlyList<Position2> CutToward(Entity entity, IReadOnlyList<Position2> route, Func<Position2, bool> allowed)
    {
        // Batch 474c: the unit's own position decides whether it stands on the allowed ground. route[0] is the client-anchored start, which
        // can fall one lattice step into the next cell (the owner's hero stood at (1155.8, 3686.6), height -36.5, and its anchored start
        // (1157, 3687) reads -42), so it is not tested; the first leg leaves the unit's own position.
        if (route.Count < 2 || !allowed(entity.Position)) return route;
        for (var index = 1; index < route.Count; index++)
        {
            // Batch 474d: a leg is cut where it crosses the disallowed ground, not only where its end point lies in it (a stairs footprint is
            // smaller than a long leg).
            var from = index == 1 ? entity.Position : route[index - 1];
            if (!Crosses(from, route[index], allowed)) continue;
            var kept = route.Take(index).ToList();
            var exit = LastAllowed(from, route[index], allowed);
            if (exit != kept[^1]) kept.Add(exit);
            return kept;
        }
        return route;
    }

    /// <summary>
    /// The last point of the leg <paramref name="from"/> to <paramref name="to"/> that <paramref name="allowed"/> accepts, found by halving
    /// the leg, then snapped to the client lattice when the snapped point stays allowed. Reconstruction: the cut point of a held route;
    /// retail's own cut is server code that did not ship (batch 458).
    /// </summary>
    private static bool Crosses(Position2 from, Position2 to, Func<Position2, bool> allowed)
    {
        var samples = SampleCount(from, to);
        for (var i = 1; i <= samples; i++)
            if (!allowed(Along(from, to, i, samples))) return true;
        return false;
    }

    private Position2 LastAllowed(Position2 from, Position2 to, Func<Position2, bool> allowed)
    {
        // Batch 474d: the first sample on the leg that the ground disallows bounds the search; the leg before it is allowed.
        var samples = SampleCount(from, to);
        var inside = from;
        var outside = to;
        for (var i = 1; i <= samples; i++)
        {
            var at = Along(from, to, i, samples);
            if (allowed(at)) { inside = at; continue; }
            outside = at;
            break;
        }
        for (var step = 0; step < 16; step++)
        {
            var middle = new Position2((inside.X + outside.X) / 2.0, (inside.Z + outside.Z) / 2.0);
            if (allowed(middle)) inside = middle;
            else outside = middle;
        }
        var anchored = ClientAnchor(inside);
        return allowed(anchored) ? anchored : inside;
    }

    /// <summary>
    /// Batch 474e: the owner's bundle 31 hero walked out over the platform's north rim (the -40 contour near z 4800) while the platform cut
    /// and the footprint stood, because the cut lives on the order and the chase, dash and automatic routes do not all pass it. An order cut is
    /// not airtight, so every step of a held hero is checked: a step that leaves the held ground is stopped at its last allowed point, and
    /// the hero is given that point as a one-point route. A unit that already stands off the ground (a push) keeps the circle only. Only a
    /// hero with a <see cref="Entity.MovementRestriction"/> (the opening hold, until 80 s) is checked.
    /// Reconstruction: how retail confined the champion is not recovered. Batch 514: the held ground is the hold's single limit (<see cref="HeldGround"/>).
    /// </summary>
    private void ConfineHeldHero(Entity entity, Position2 start)
    {
        if (!Held(entity)) return;
        var Ground = HeldGround(entity, start);
        if (!Ground(start) || Ground(entity.Position)) return;
        var exit = LastAllowed(start, entity.Position, Ground);
        entity.Position = exit;
        entity.Waypoints = new List<Position2> { exit };
        entity.Target = null;
        entity.FrameVelocity = Position2.Zero;
        PublishAutomaticHeroRoute(entity, [exit], "hold-boundary");
    }

    /// <summary>Batch 474d: the samples taken along a leg, one per 8 units.</summary>
    private static int SampleCount(Position2 from, Position2 to) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt(DistanceSquared(from, to)) / 8.0));

    private static Position2 Along(Position2 from, Position2 to, int i, int samples) =>
        new(from.X + (to.X - from.X) * i / samples, from.Z + (to.Z - from.Z) * i / samples);

    /// <summary>
    /// Batch 460: the requested end of a dash or a blink (Flash included) of a unit under <see cref="Entity.MovementRestriction"/>, held
    /// inside the circle as its walk is (<see cref="Restricted"/>): a request outside ends where the straight line from the unit leaves the
    /// circle. Reconstruction: how retail held a displacement is server code that did not ship (batch 458); without this, Flash or a dash
    /// left Dominion's base during the setup.
    /// </summary>
    public Position2 HeldInside(Entity unit, Position2 requested)
    {
        if (unit.MovementRestriction is not { } circle) return OpeningHeld(unit) ? HeldGroundEnd(unit, requested) : requested;
        var limit = HoldLimit(circle, unit.Position);
        var inside = DistanceSquared(requested, circle.Centre) <= limit * limit
            ? requested
            : CircleExit(unit.Position, requested, circle.Centre, limit);
        return HeldGroundEnd(unit, inside);
    }

    /// <summary>
    /// Batch 474f: a held champion's displacement ends on the held ground, as its walk does (see CutToward). Batch 514: the hold's single limit
    /// (<see cref="HeldGround"/>).
    /// </summary>
    private Position2 HeldGroundEnd(Entity unit, Position2 inside)
    {
        var ground = HeldGround(unit, unit.Position);
        return ground(inside) || !ground(unit.Position) ? inside : LastAllowed(unit.Position, inside, ground);
    }

    /// <summary>
    /// Batch 474f (Reconstruction): true while the Dominion opening window stands (<see cref="OpeningHoldWindow"/>) for a player's champion of
    /// either team. The hold is the ceremony's, applied to whichever champions it reaches; this makes the boundary independent of that
    /// reach: the owner's bundle 32 hero walked out with no circle recorded for him (the trace, batch 474f).
    /// </summary>
    private bool OpeningHeld(Entity entity) =>
        OpeningHoldWindow && entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0 && entity.Team is Team.Order or Team.Chaos;

    /// <summary>The window in which a champion is held: a circle is recorded, or the opening window stands (<see cref="OpeningHeld"/>).</summary>
    private bool Held(Entity entity) => entity.MovementRestriction is not null || OpeningHeld(entity);

    /// <summary>
    /// Batch 474f: the opening window: set by the production step while the Dominion quests run and the game time is before
    /// <see cref="Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ReleaseSeconds"/> (80 s). Reconstruction, as the hold itself.
    /// </summary>
    public bool OpeningHoldWindow { get; set; }

    /// <summary>
    /// The radius a held unit at <paramref name="from"/> may move within: one lattice step inside the circle (<see cref="Restricted"/>), or, for
    /// a unit already past it, one step inside the distance it stands at, so it can come back in and never go further out. Batch 460: a unit
    /// the cut anchored between the step and the circle (ClientAnchor) keeps the circle's own limit. Reconstruction, as the hold itself.
    /// </summary>
    private static double HoldLimit((Position2 Centre, double Radius) circle, Position2 from) =>
        Math.Max(circle.Radius - 2.0, Math.Sqrt(DistanceSquared(from, circle.Centre)) - 2.0);

    /// <summary>
    /// The farthest point of the leg <paramref name="from"/> to <paramref name="to"/> (which ends outside) still inside the circle of radius
    /// <paramref name="limit"/>: the larger root of |from + t (to - from) - centre| = limit, kept in [0, 1]. A leg that starts on the ring and
    /// heads out has none ahead, so it stays at <paramref name="from"/>.
    /// </summary>
    private static Position2 CircleExit(Position2 from, Position2 to, Position2 centre, double limit)
    {
        var (dx, dz) = (to.X - from.X, to.Z - from.Z);
        var (fx, fz) = (from.X - centre.X, from.Z - centre.Z);
        var a = dx * dx + dz * dz;
        var b = 2.0 * (fx * dx + fz * dz);
        var c = fx * fx + fz * fz - limit * limit;
        var disc = b * b - 4.0 * a * c;
        var t = a <= 0.0 || disc < 0.0 ? 0.0 : Math.Clamp((-b + Math.Sqrt(disc)) / (2.0 * a), 0.0, 1.0);
        return new Position2(from.X + t * dx, from.Z + t * dz);
    }

    /// <summary>
    /// Resolves FURTHEST_WITHIN_RANGE landing: terrain may be crossed, but
    /// the quantized destination must be walkable. Search back from the
    /// requested endpoint, rather than stopping at the first wall. Random
    /// knock-ups supply their unit to prefer body-clear movement floor.
    /// <para>
    /// Batch 659 (community report of 3 November 2026, "W on target close to wall fails"): for a script dash (<paramref name="dash"/>, the
    /// BBMove landing) longer than two body clearances (not a knock-up's bounce) the body-clear preference reaches back at most one body
    /// clearance from the furthest floor point of the ray. It
    /// used to take the furthest body-clear point of the whole ray, so a Safeguard to an ally standing 30 units from a wall, whose last stretch
    /// of ray runs within a body of the wall, landed 200 units short. The ally stands there, and the client's per-frame gate is the bare cell
    /// (<see cref="NavigationGrid.IsMovementWalkable(Position2)"/>), so the floor point itself is a landing the client accepts. A short bounce
    /// keeps the whole-ray preference (playtest B3 #3: a knocked-up body lands clear of the wall). Reconstruction: the native floor search is
    /// not recovered; the budget and the split are this server's, in the body margin the preference already used. The follow form's terminal
    /// (<see cref="FollowingLanding"/>) keeps the whole-ray preference with its own placement fallback.
    /// </para>
    /// </summary>
    public Position2 FurthestWalkableLanding(Position2 origin, Position2 requested, Entity? unit = null, bool dash = false)
    {
        // Geometry.ClientWaypoints' grid scale: every waypoint the client keeps is a multiple of it.
        const double ClientLatticeStep = 2.0;
        var dx = requested.X - origin.X;
        var dz = requested.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(requested));
        // The native floor-search constants are not recovered. Reuse the
        // existing 10-unit backoff and Pounce's 4-unit body margin.
        var clearance = unit is null ? 0.0 : MovementRadius(unit) + 4.0;
        var staticBodies = unit is null ? Array.Empty<NavigationObstacle>() :
            StaticBodyObstacles(unit);
        // A quantised point may sit up to one step per axis, sqrt(2) steps diagonally, away from the raw one.
        var latticeSlack = ClientLatticeStep * Math.Sqrt(2.0);
        // The actor on the client stands on the lattice anchor of the server position, never on the raw one: a fallback that returned the raw
        // point left the caller with a zero-length move (BbScriptMoves.Start's InstantMove) whenever the request was shorter than one lattice
        // step, so the dash packet and its animation never left (Xin Zhao's E cast up close, owner finding 25, 20 October 2026: the hero
        // "walks" because the script's follow-up attack order is the only order the client gets).
        var originAnchor = ClientWaypoints([origin])[0];
        if (unit is null) return Furthest(bodyClear: true, distance, 0.0, out _) ?? originAnchor;
        // The furthest point the client's per-frame movement grid admits, before cancelling the move (playtest B3 #3, 26 Sept 2026: Power
        // Fist beside a wall never knocked up); a body-clear point within one body clearance back from it is preferred.
        if (Furthest(bodyClear: false, distance, 0.0, out var floorTravel) is not Position2 floor) return originAnchor;
        var budget = dash && distance > 2.0 * clearance ? clearance : distance;
        return Furthest(bodyClear: true, floorTravel, Math.Max(0.0, floorTravel - budget), out _) ?? floor;

        Position2? Furthest(bool bodyClear, double from, double to, out double found)
        {
            found = 0.0;
            for (var travel = from; travel > to; travel = Math.Max(to, travel - 10.0))
            {
                var candidate = ClientWaypoints([new Position2(origin.X + dx * travel / distance,
                    origin.Z + dz * travel / distance)])[0];
                // The client quantises every waypoint to a 2-unit lattice (Geometry.ClientWaypoints), so the point at the
                // request itself can snap outward by up to one lattice step on each axis. The guard allows exactly that step, not more: a
                // request shorter than one step (Xin Zhao's E cast at three units, owner finding 25) otherwise found no candidate at all
                // and fell back to the origin, and the script's move then had a zero length: no dash packet, no dash clip.
                found = travel;
                if (unit is null)
                {
                    if (_navigation.IsWalkable(candidate)) return candidate;
                }
                else if (Distance(origin, candidate) <= distance + latticeSlack &&
                         _navigation.IsMovementWalkable(candidate) &&
                         (!bodyClear || _navigation.PointClearance(candidate, clearance) >= clearance) &&
                         staticBodies.All(body =>
                             DistanceSquared(candidate, body.Center) >= body.Radius * body.Radius))
                    return candidate;
            }
            return null;
        }
    }

    /// <summary>
    /// Projects a FIRST_COLLISION_HIT request to a client-quantized, body-clear
    /// placement. Callers that retain the raw movement request must instead use
    /// FirstCollisionDashLanding for presentation: live collision tests the
    /// movement grid and static bodies, without this extra placement clearance.
    /// </summary>
    public Position2 FirstCollisionLanding(Position2 origin, Position2 requested,
        double speed, double duration, Entity? unit = null)
    {
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Z) ||
            !double.IsFinite(requested.X) || !double.IsFinite(requested.Z) ||
            !double.IsFinite(speed) || !double.IsFinite(duration) || speed < 0.0 || duration < 0.0)
            throw new ArgumentOutOfRangeException(nameof(requested));

        var dx = requested.X - origin.X;
        var dz = requested.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance <= 1e-12) return ClientAnchor(origin);

        var maxTravel = Math.Min(speed * duration, distance);
        if (maxTravel <= 0.0) return ClientAnchor(origin);
        var end = new Position2(origin.X + dx / distance * maxTravel,
            origin.Z + dz / distance * maxTravel);
        var samples = Math.Max(1, (int)Math.Ceiling(maxTravel /
            Math.Max(1.0, _navigation.CellSize * 0.5)));
        var staticBodies = unit is null ? Array.Empty<NavigationObstacle>() :
            StaticBodyObstacles(unit);

        // Try the furthest sampled point first.  A client-grid quantization
        // can move a sample by a subcell; replay the exact movement policy for
        // that quantized endpoint before accepting it.
        for (var index = samples; index >= 1; index--)
        {
            var fraction = index / (double)samples;
            var sample = new Position2(origin.X + (end.X - origin.X) * fraction,
                origin.Z + (end.Z - origin.Z) * fraction);
            var candidate = ClientAnchor(sample);
            var candidateDistance = Distance(origin, candidate);
            if (candidateDistance > maxTravel + 1e-9) continue;
            var result = FirstCollisionStop(origin, candidate, staticBodies);
            if (result.ReachedDestination && (unit is null ||
                _navigation.IsMovementWalkable(candidate) &&
                (unit.UnitType != UnitType.Hero || _navigation.IsHeroPathingWalkable(candidate)) &&
                _navigation.PointClearance(candidate, MovementRadius(unit) + 4.0) >=
                    MovementRadius(unit) + 4.0)) return candidate;
        }
        return ClientAnchor(origin);
    }

    /// <summary>Preview the same movement-grid and static-body collision used by a live forced move.</summary>
    public Position2 FirstCollisionDashLanding(Position2 origin, Position2 requested,
        double speed, double duration, Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Z) ||
            !double.IsFinite(requested.X) || !double.IsFinite(requested.Z) ||
            !double.IsFinite(speed) || !double.IsFinite(duration) || speed < 0.0 || duration < 0.0)
            throw new ArgumentOutOfRangeException(nameof(requested));
        var dx = requested.X - origin.X;
        var dz = requested.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(requested));
        if (distance <= 1e-12) return requested;
        return FirstCollisionDashLanding(origin, requested, Math.Min(speed * duration, distance), unit);
    }

    internal Position2 FirstCollisionDashLanding(Entity unit, ForcedMoveState move)
    {
        if (move.CollisionOrigin is null)
        {
            move.CollisionOrigin = unit.Position;
            move.CollisionDistance = Math.Min(move.Speed * move.Remaining,
                Distance(unit.Position, move.Destination));
        }
        return FirstCollisionDashLanding(move.CollisionOrigin.Value, move.Destination,
            move.CollisionDistance, unit);
    }

    private Position2 FirstCollisionDashLanding(Position2 origin, Position2 requested,
        double maxTravel, Entity unit)
    {
        var dx = requested.X - origin.X;
        var dz = requested.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance <= 1e-12) return requested;
        maxTravel = Math.Min(maxTravel, distance);
        var end = new Position2(origin.X + dx / distance * maxTravel,
            origin.Z + dz / distance * maxTravel);
        var bodies = StaticBodyObstacles(unit);
        var collision = FirstCollisionStop(origin, end, bodies);
        if (maxTravel >= distance && collision.ReachedDestination) return requested;

        // A two-unit client anchor can cross the collision edge. Back off on
        // the same ray until its wire point also passes the live predicate.
        for (var travel = Distance(origin, collision.Position); ; travel = Math.Max(0.0, travel - 2.0))
        {
            var candidate = ClientAnchor(new Position2(origin.X + dx / distance * travel,
                origin.Z + dz / distance * travel));
            if (Distance(origin, candidate) <= maxTravel + 1e-9 &&
                _navigation.IsMovementWalkable(candidate) &&
                bodies.All(body => DistanceSquared(candidate, body.Center) >= body.Radius * body.Radius) &&
                FirstCollisionStop(origin, candidate, bodies).ReachedDestination)
                return candidate;
            if (travel <= 0.0) return ClientAnchor(origin);
        }
    }

    public bool StartForcedMove(
        uint netId,
        Position2 destination,
        double speed,
        double duration,
        uint sourceNetId,
        string effect,
        bool cancelOrder = false,
        bool firstCollision = false,
        uint? followNetId = null,
        double maxTrackDistance = double.PositiveInfinity)
    {
        var state = new ForcedMoveState(destination, speed, duration, sourceNetId,
            effect, firstCollision, followNetId, maxTrackDistance);
        return StartForcedMove(netId, state, cancelOrder);
    }

    public bool StartForcedMove(uint netId, ForcedMoveState state, bool cancelOrder = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        var entity = _world.Get(netId);
        if (entity is null) return false;
        if (cancelOrder)
        {
            entity.RetainLaneRouteForControl();
            entity.Target = null;
            entity.Waypoints.Clear();
            entity.AttackTarget = null;
            // Native CANCEL_ORDER removes the saved explicit pursuit and
            // attack-move destination as well as the active route.  Keep
            // HeroAutoAcquire untouched: a later idle scan may still select
            // a nearby visible enemy once the displacement ends.
            entity.HeroPursuit = null;
            entity.AttackMoveGoal = null;
            entity.LastAttackStopPoint = null;
            // Forced displacement supersedes a pending walk-then-cast action;
            // this is typed state owned by the spell system, so the order
            // cannot fire after the forced move has cancelled the route.
            entity.PendingCast = null;
        }
        entity.ForcedMoveGrace = 0.0;
        state.Completed = false;
        state.Succeeded = false;
        state.StartedAt = Math.Max(_simulationElapsed, entity.UnitType == UnitType.Hero
            ? _heroMovementElapsed : _nonHeroMovementElapsed);
        entity.ForcedMove = state;
        if (state.FollowNetId is not null)
            state.Destination = FollowingLanding(entity, state.Destination);
        state.CollisionOrigin = null;
        return true;
    }

    public IReadOnlyList<uint> ColliderIds(uint netId) =>
        _frameColliders.TryGetValue(netId, out var list)
            ? list.Select(item => item.Entity.NetId).ToArray()
            : Array.Empty<uint>();

    internal void ObserveNeutralDecision(Entity entity, string reason,
        Position2 requestedGoal, IReadOnlyList<Position2> route, int attempt = 0,
        string? phase = null)
    {
        var observer = DecisionObserver;
        if (observer is null) return;
        try
        {
            observer(new MovementDecisionObservation(
                entity.NetId, "neutral-planner", reason, requestedGoal,
                route.ToArray(), attempt, Phase: phase));
        }
        catch
        {
            // Observability must never change movement acceptance.
        }
    }

    private HeroFollowerValidation HeroFollowerRouteValidation(
        IReadOnlyList<Position2> route, NavigationObstacle[] obstacles,
        Position2 authoritativeStart, double nativeStep)
    {
        if (route.Count < 2) return new(false, -1, "route");
        if (nativeStep <= _settings.Collision.Epsilon) return new(true, -1, "clear");
        var authoritative = HeroFollowerTrajectoryValidation(route, obstacles,
            authoritativeStart, nativeStep, "authoritative");
        if (!authoritative.Clear) return authoritative;
        // The packet anchor can differ from the authoritative sub-cell start.
        // Check the client's quantized origin through the full tail as well;
        // fixed-origin corner cuts can preserve that offset for an entire leg.
        return DistanceSquared(authoritativeStart, route[0]) <= 1e-12
            ? authoritative
            : HeroFollowerTrajectoryValidation(route, obstacles, route[0],
                nativeStep, "wire");
    }

    private HeroFollowerValidation HeroFollowerTrajectoryValidation(
        IReadOnlyList<Position2> route, NavigationObstacle[] obstacles,
        Position2 start, double nativeStep, string origin)
    {
        var waypoints = route.Skip(1).ToList();
        var current = start;
        var first = true;
        while (waypoints.Count > 0)
        {
            var leg = Distance(current, waypoints[0]);
            if (waypoints.Count == 1)
                return HeroFollowerChordValidation(route[0], current, waypoints[0],
                    obstacles, first, origin);

            if (leg > nativeStep)
            {
                // Straight no-pop frames stay on this same clear segment. Jump
                // to the last such frame while preserving the exact turn phase.
                var frames = Math.Ceiling(leg / nativeStep) - 1.0;
                if (!double.IsFinite(frames) || frames < 0.0)
                    return new(false, -1, $"{origin}-simulation");
                var travel = Math.Min(leg, frames * nativeStep);
                var approach = travel <= 0.0 ? current : new Position2(
                    current.X + (waypoints[0].X - current.X) / leg * travel,
                    current.Z + (waypoints[0].Z - current.Z) / leg * travel);
                var approachValidation = HeroFollowerChordValidation(route[0],
                    current, approach, obstacles, first, origin);
                if (!approachValidation.Clear) return approachValidation;
                current = approach;
                first = false;
            }

            var count = waypoints.Count;
            var step = FixedOriginStep(current, waypoints, nativeStep);
            var stepValidation = HeroFollowerChordValidation(route[0], current,
                step.Position, obstacles, first, origin);
            if (!stepValidation.Clear) return stepValidation;
            if (waypoints.Count >= count &&
                DistanceSquared(current, step.Position) <=
                _settings.Collision.Epsilon * _settings.Collision.Epsilon)
                return new(false, -1, $"{origin}-no-progress");
            current = step.Position;
            first = false;
        }
        return new(true, -1, "clear");
    }

    private HeroFollowerValidation HeroFollowerChordValidation(Position2 wireStart,
        Position2 start, Position2 end, NavigationObstacle[] obstacles, bool first,
        string origin)
    {
        var bodyClear = first
            ? _navigation.FirstSegmentClearOfUnits(wireStart, start, end, obstacles)
            : _navigation.SegmentClearOfUnits(start, end, obstacles, escape: true);
        if (!bodyClear)
        {
            for (var index = 0; index < obstacles.Length; index++)
            {
                var clear = first
                    ? _navigation.FirstSegmentClearOfUnits(wireStart, start, end,
                        [obstacles[index]])
                    : _navigation.SegmentClearOfUnits(start, end,
                        [obstacles[index]], escape: true);
                if (!clear) return new(false, index, $"{origin}-body");
            }
            return new(false, -1, $"{origin}-body");
        }
        return _navigation.HasHeroMovementLineOfSight(start, end)
            ? new(true, -1, "clear")
            : new(false, -1, $"{origin}-terrain");
    }

    private NavigationBounds Bounds => new(_navigation.MinX, _navigation.MaxX, _navigation.MinZ, _navigation.MaxZ);

    private IReadOnlyList<Position2> ClientWaypoints(IEnumerable<Position2> points) => Geometry.ClientWaypoints(points, Bounds);

    internal Position2 ClientAnchor(Position2 point) => ClientWaypoints([point])[0];

    /// <summary>Checks the existing fixed-origin follower at the NPC cadence.</summary>
    internal bool NpcFollowerTerrainRouteClear(Entity entity,
        IReadOnlyList<Position2> route) =>
        HeroFollowerRouteValidation(route, StaticBodyObstacles(entity, margin: 0.0), entity.Position,
            Math.Max(0.0, entity.Speed) * FixedTickSeconds).Clear;

    private void AdvanceForcedMove(Entity entity, double dt)
    {
        var move = entity.ForcedMove!;
        if (move.Completed) return;
        // Reconstruction: retail server scheduling is unrecovered. Consume only
        // the part of this simulation step after the move started, including catch-up.
        var elapsed = entity.UnitType == UnitType.Hero
            ? _heroMovementElapsed : _nonHeroMovementElapsed;
        dt = Math.Min(dt, Math.Max(0.0, elapsed - move.StartedAt));
        if (dt <= 0.0) return;
        if (move.FollowNetId is uint targetId)
        {
            var target = _world.Get(targetId);
            var targetDistance = target is null ? double.PositiveInfinity :
                Distance(entity.Position, target.Position);
            if (target is null || !target.Alive || targetDistance > move.MaxTrackDistance)
            {
                move.Completed = true;
                move.Succeeded = false;
                entity.Position = FollowingLanding(entity, entity.Position);
                move.Destination = entity.Position;
                entity.ForcedMoveGrace = 0.5;
                ResetMinionForcedMoveAnchor(entity);
                return;
            }
            // Follow dashes terminate at the same mobile-body shell used by
            // hero route obstacles. Chasing a center point leaves the caster
            // overlapping the target, after which the next ordinary order
            // correctly (but visibly) routes around the whole ally/enemy.
            var arrivalDistance = Math.Max(0.0, entity.Radius) +
                Math.Max(0.0, target.Radius) + _settings.Collision.SkinHighCollider;
            if (targetDistance <= arrivalDistance + 1e-9)
            {
                entity.Position = FollowingLanding(entity, entity.Position);
                move.Destination = entity.Position;
                move.Completed = true;
                move.Succeeded = true;
                entity.ForcedMoveGrace = 0.5;
                ResetMinionForcedMoveAnchor(entity);
                return;
            }
            var followDx = target.Position.X - entity.Position.X;
            var followDz = target.Position.Z - entity.Position.Z;
            move.Destination = ClientWaypoints([new Position2(
                target.Position.X - followDx / targetDistance * arrivalDistance,
                target.Position.Z - followDz / targetDistance * arrivalDistance)])[0];
            move.Destination = FollowingLanding(entity, move.Destination);
            Geometry.FaceTowards(entity, target.Position);
        }

        // Fear's C1 waypoint is ordinary walking. Its segment target stays
        // fixed until the next timer pulse, but subsequent stat changes must
        // change its travel speed just as they change any other walk order.
        // Scripted knockbacks/dashes keep their cast-bound speed.
        if (move.Effect == "Fear") move.Speed = entity.Speed;
        var activeDt = Math.Min(Math.Max(0.0, dt), move.Remaining);
        var start = entity.Position;
        // Reconstruction: sample the same full ray as the announced landing.
        // Resampling each partial frame shifts the last safe terrain sample.
        // Keep live structure checks and the raw request for collision callbacks.
        var destination = move.FirstCollision
            ? FirstCollisionDashLanding(entity, move) : move.Destination;
        var dx = destination.X - start.X;
        var dz = destination.Z - start.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        var travel = Math.Min(move.Speed * activeDt, distance);
        entity.Position = travel >= distance
            ? destination
            : new Position2(start.X + dx / distance * travel,
                            start.Z + dz / distance * travel);

        if (travel >= distance)
        {
            move.Completed = true;
            move.Succeeded = destination == move.Destination;
            move.StoppedShort = move.FirstCollision && destination != move.Destination;
        }
        move.Remaining = Math.Max(0.0, move.Remaining - activeDt);
        if (move.Remaining <= 1e-12) move.Completed = true;
        if (move.Completed)
        {
            if (move.FollowNetId is not null)
            {
                entity.Position = FollowingLanding(entity, entity.Position);
                move.Destination = entity.Position;
            }
            entity.ForcedMoveGrace = 0.5;
            ResetMinionForcedMoveAnchor(entity);
        }
    }

    internal Position2 FollowingLanding(Entity entity, Position2 requested)
    {
        if (_navigation.IsMovementWalkable(requested)) return requested;
        // PATH-01, 27 Sept 2026 base-game sweep: BBMoveToUnit may cross walls,
        // but the native terminal assignment requires movement floor.
        // Retain the existing backoff/body margin; they are emulator policy.
        var landing = FurthestWalkableLanding(entity.Position, requested, entity);
        return _navigation.FitsSummonPlacement(landing, MovementRadius(entity))
            ? landing
            : _navigation.ClosestSummonPlacement(requested, MovementRadius(entity));
    }

    private void ResetMinionForcedMoveAnchor(Entity entity)
    {
        // Only lane minions own a MinionAI anchor (dated by MinionAiSystem at the next step).
        if (entity.UnitType != UnitType.Minion || entity.MinionType is null) return;
        entity.AnchorPath = ClientWaypoints([entity.Position]).ToList();
    }

    private (Position2 Position, bool ReachedDestination) FirstCollisionStop(
        Position2 start, Position2 destination,
        IReadOnlyList<NavigationObstacle>? staticBodies = null)
    {
        var dx = destination.X - start.X;
        var dz = destination.Z - start.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance <= 1e-12) return (start, true);
        var samples = Math.Max(1, (int)Math.Ceiling(distance /
            Math.Max(1.0, _navigation.CellSize * 0.5)));
        var position = start;
        for (var index = 1; index <= samples; index++)
        {
            var fraction = index / (double)samples;
            var point = new Position2(start.X + dx * fraction,
                start.Z + dz * fraction);
            // The client grid also blocks the 140u turret stamp, dead turrets
            // included; a unit cannot walk out of a stamped cell (playtest B2
            // BJ4/YV11).
            if (!_navigation.IsMovementWalkable(point)) return (position, false);
            if (staticBodies is not null && staticBodies.Any(body =>
                    DistanceSquared(point, body.Center) < body.Radius * body.Radius))
                return (position, false);
            position = point;
        }
        return (position, true);
    }

    internal NavigationObstacle[] StaticBodyObstacles(Entity unit, double margin = 4.0) =>
        _world.Entities.Values.Where(other => other.Alive && other.NetId != unit.NetId &&
            IsHeroStaticBody(other)).Select(other =>
            new NavigationObstacle(other.Position, BodyClearance(unit, other) + margin)).ToArray();

    internal static (Position2 Position, double Leg) FixedOriginStep(
        Position2 start, List<Position2> waypoints, double budget)
    {
        if (waypoints.Count == 0) return (start, 0.0);
        var leg = Distance(start, waypoints[0]);
        while (leg <= budget)
        {
            var popped = waypoints[0];
            waypoints.RemoveAt(0);
            if (waypoints.Count == 0) return (popped, 0.0);
            budget -= leg;
            leg = Distance(start, waypoints[0]);
        }
        if (leg > 0.0)
        {
            var waypoint = waypoints[0];
            return (new Position2(
                start.X + (waypoint.X - start.X) / leg * budget,
                start.Z + (waypoint.Z - start.Z) / leg * budget), leg);
        }
        return (start, leg);
    }

    private void MarkBranch(uint netId, string branch)
    {
        if (!_diagnosticsEnabled) return;
        if (!_frameBranches.TryGetValue(netId, out var branches))
            _frameBranches[netId] = branches = new List<string>();
        branches.Add(branch);
    }

    private static bool IsStatic(Entity entity) => entity.Speed <= 0.0;

    private static bool IsHeroStaticBody(Entity entity) =>
        IsStatic(entity) && !IsStamped(entity) && entity.UnitType is UnitType.Turret or UnitType.Structure;

    private static bool IsMobileBody(Entity entity) =>
        !IsStatic(entity) && entity.UnitType is not UnitType.Turret and not UnitType.Structure;

    private static bool IsScaledNpc(Entity entity) =>
        entity.Kind is not (EntityKind.Ward or EntityKind.ShacoBox or EntityKind.NidaleeTrap) &&
        entity.UnitType is UnitType.Minion or UnitType.Monster;

    internal static bool IsGhosted(Entity entity) =>
        entity.AnyBuffOrStatus(buff => buff.Payload.Ghosted);

    private static bool IsRooted(Entity entity) =>
        entity.AnyBuffOrStatus(buff => buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    private static bool IsStunned(Entity entity) =>
        entity.ChampionState?.BlocksActions == true ||
        entity.AnyBuffOrStatus(buff => buff.DisablesActions);

    private static Position2 Normalize(Position2 value)
    {
        var length = Distance(value, Position2.Zero);
        return length <= 0.0 ? Position2.Zero : new Position2(value.X / length, value.Z / length);
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Z - second.Z) * (first.Z - second.Z));

    private static double RouteLength(IReadOnlyList<Position2> route) =>
        route.Zip(route.Skip(1), (first, second) => Distance(first, second)).Sum();

    /// <summary>
    /// Path-planning work done so far (<see cref="NavigationGrid.PlanningWork"/>, about 0.5 microsecond a unit).
    /// The automatic hero planners budget against it, because it is deterministic and tracks time.
    /// </summary>
    internal long PlanningWork => _navigation.PlanningWork;

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);
}
