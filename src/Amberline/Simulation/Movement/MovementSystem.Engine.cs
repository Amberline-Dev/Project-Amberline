using Amberline.Core;

namespace Amberline.Simulation.Movement;

/// <summary>
/// Which engine runs a frame and a plan (<see cref="MovementEngine"/>), and the server's members every engine shares (batch 613). The C#
/// port of the client's routines (the files <c>tools/export/port-files.txt</c> lists) implements the partial methods below when the build
/// carries it; a build without it compiles them away, and a <see cref="MovementSystem"/> built directly then runs the fallback.
/// </summary>
public sealed partial class MovementSystem
{
    // Update: a stopped actor's stored frame velocity decays by this each update. The server applies it to the
    // units the client's update does not run for (control effects, static and idle units).
    private const double StoppedVelocityDecay = 0.75;

    // Actors that still had a route after their last frame: one that is idle at its next frame was stopped by its owner.
    private readonly HashSet<uint> _following = new();
    // The pre-collision step of the unit's last update and whether that update bent a waypoint (a hero's route is then published), for
    // TrackHeroDrift and the diagnostics.
    private Position2 _lastIntendedStep;
    private bool _lastBent;

    /// <summary>The sequential frame of one cohort: the heroes (50 ms) or every other actor (100 ms). <paramref name="intended"/> and
    /// <paramref name="candidates"/> exist only when a diagnostic sink is attached.</summary>
    private void ActorFrame(double dt, bool heroes, Dictionary<uint, Position2>? intended,
        Dictionary<uint, Position2>? candidates,
        List<(uint NetId, double Speed, bool PathFollowerAllowed, IReadOnlyList<Entity> Colliders)>? steps)
    {
        // Batch 608: production runs the client's own update from the player's EXE (MovementSystem.ExeHost.cs), or the fallback mover.
        if (Engine == MovementEngine.Helper)
        {
            HelperActorFrame(dt, heroes, intended, candidates, steps);
            return;
        }
        var ran = false;
        if (Engine == MovementEngine.Direct) PortActorFrame(dt, heroes, intended, candidates, steps, ref ran);
        if (!ran) FallbackActorFrame(dt, heroes, steps);
    }

    partial void PortActorFrame(double dt, bool heroes, Dictionary<uint, Position2>? intended,
        Dictionary<uint, Position2>? candidates,
        List<(uint NetId, double Speed, bool PathFollowerAllowed, IReadOnlyList<Entity> Colliders)>? steps, ref bool ran);

    /// <summary>
    /// Plans the route the stock client plans for <paramref name="hero"/> clicking <paramref name="click"/>: the snapped hero
    /// position, the vertices it walks and the snapped end, or null when the client would plan nothing.
    /// </summary>
    internal IReadOnlyList<Position2>? PlanClientHeroRoute(Entity hero, Position2 click, Func<Entity, bool>? visibleTo)
    {
        // Batch 608: production plans with the client's own BuildPath from the player's EXE, or with the server's fallback planner.
        if (Engine == MovementEngine.Helper) return HelperPlan(hero, click, visibleTo);
        IReadOnlyList<Position2>? route = null;
        var ran = false;
        if (Engine == MovementEngine.Direct) PortPlan(hero, click, visibleTo, ref route, ref ran);
        return ran ? route : FallbackPlan(hero, click);
    }

    partial void PortPlan(Entity hero, Position2 click, Func<Entity, bool>? visibleTo, ref IReadOnlyList<Position2>? route, ref bool ran);

    /// <summary>A* iterations the planner has spent (the pursuit angle search budgets against it).</summary>
    internal long ClientPlannerWork
    {
        get
        {
            var work = _engineWork;
            AddPortPlannerWork(ref work);
            return work;
        }
    }

    partial void AddPortPlannerWork(ref long work);

    // Called at the end of every movement frame (TickFrame).
    partial void NoteMovementFrame(bool? heroesOnly);

    // DIAGNOSTICS: StartEngine starts no engine while the port's differential checks run their matches on the port.
    partial void KeepPortForDifferential(ref bool keep);

    /// <summary>
    /// Reconstruction: the server keeps a hero's installed route only to publish a correction when the hero's solver result has drifted
    /// from what the client will have computed. The client runs the same update on the same path, so the two agree until a neighbour's
    /// state differs by a round trip; a bent waypoint (the client's bend needs the same neighbours and may not fire) is published at once,
    /// and the displacement the solver caused is summed and published once it passes <see cref="HeroCollisionResyncDistance"/>. Retail's
    /// reconciliation of a hero route is not in the shipped files (batch 225, item 5: the evidence is the shipped client, which walks what
    /// it was sent, and the 119-unit render band), so neither the publish nor its threshold is recovered.
    /// </summary>
    private void TrackHeroDrift(Entity hero, Position2 start)
    {
        if (!_heroOrders.TryGetValue(hero.NetId, out var order) || !ReferenceEquals(hero.Waypoints, order.Route) ||
            hero.Waypoints.Count == 0) return;
        if (_lastBent)
        {
            hero.LastAttackStopPoint = null;
            order.CollisionDisplacement = Position2.Zero;
            PublishAutomaticHeroRoute(hero, ClientWaypoints(new[] { hero.Position }.Concat(hero.Waypoints)), "bend");
            return;
        }
        // A frame that moved the hero deflected it by (position - intended); a frame that did not move it adds nothing (a hero pinned
        // against a neighbour is where the client's twin is too, and its deflection is not a drift that grows with time).
        if (DistanceSquared(hero.Position, start) > 0.25)
            order.CollisionDisplacement += hero.Position - (start + _lastIntendedStep);
        if (DistanceSquared(order.CollisionDisplacement, Position2.Zero) <=
            HeroCollisionResyncDistance * HeroCollisionResyncDistance) return;
        PublishAutomaticHeroRoute(hero, ClientWaypoints(new[] { hero.Position }.Concat(hero.Waypoints)), "collision-resync");
        order.CollisionDisplacement = Position2.Zero;
    }

    /// <summary>Every live actor with a body, in vector (NetId) order: the neighbour set of the NPC loop.</summary>
    internal List<Entity> NpcBodies()
    {
        var bodies = new List<Entity>();
        foreach (var entity in _world.Entities.Values)
            if (entity.Alive && MovementRadius(entity) > 0.0) bodies.Add(entity);
        bodies.Sort(static (a, b) => a.NetId.CompareTo(b.NetId));
        return bodies;
    }

    /// <summary>
    /// The client's float game clock as the actor's frame reads it: its cohort's movement clock, which every frame advances by its
    /// dt. The stopwatch fields are float stamps of it, read as clock - stamp and restarted to it
    /// Reconstruction: the retail server's clock is its game time, which the cohort clocks follow.
    /// </summary>
    internal float ClientClock(Entity entity) =>
        (float)(entity.UnitType == UnitType.Hero ? _heroMovementElapsed : _nonHeroMovementElapsed);

    // The actor's set-radius step: the unit's pathfinding collision radius, kept within [0, 250].
    private float ClientPathRadius(Entity entity) => (float)Math.Clamp(MovementRadius(entity), 0.0, 250.0);

    /// <summary>
    /// <c>ForceStopServer()</c>, which the shared update calls for an actor pressed within reach of a goal cell another body
    /// holds. Reconstruction (batch 288): the debug data declares the member (void, no arguments, beside <c>Stop(bool)</c> and
    /// <c>Update_Pathfinding(float, int, bool)</c>), and the server-only <c>ServerStop</c> is in the
    /// shipped strings, but no binary we hold carries a body: the server is taken to stop the actor where it stands and send the stop, as
    /// every server stop is sent (the one-point path, <see cref="InstallStop"/>), which the client installs and arrives on. The unit's order
    /// and AI state are left to their owner, which decides again on its own schedule.
    /// </summary>
    private void PublishForceStop(Entity entity)
    {
        var stop = InstallStop(entity);
        entity.AnchorPath = stop.ToList();
        if (entity.UnitType == UnitType.Hero) PublishAutomaticHeroRoute(entity, stop, "force-stop-server");
        else ServerStopPublished?.Invoke(entity, stop);
    }

    /// <summary>
    /// Batch 699: the server put <paramref name="unit"/> somewhere at once (a script teleport, a respawn, a pet's teleport) and publishes it as a
    /// one-point path. The client installs that path on receipt (HandleWaypoint the client -> ClientFollowServerPath the client: stopped and
    /// seated on the point), which replaces whatever path it was on, a used-up one included; so the unit's next frame here is that stop's arrival,
    /// as for a published force stop (<see cref="HelperForceStopServer"/>). Without it a teleport in the frame after an arrival ran the used-up
    /// path's top branch, whose arrival seats the actor on that path's end (<see cref="ArrivalTopBranch"/>): Shunpo's Katarina was put
    /// back where her last walk ended, and her next route started there. Reconstruction: the helper's client update also keeps re-arriving a
    /// displaced actor on its used-up path every frame (the port does it once), which widens that window in production.
    /// </summary>
    internal void Teleported(Entity unit) => _following.Add(unit.NetId);
}

/// <summary>
/// float32 arithmetic with one rounding per operation. The stock client runs its x87 code with 24-bit precision
/// control (Direct3D 9 default), so each fadd, fsub, fmul, fdiv and fsqrt yields a float; a chain of these is not
/// the same as a float rounding of the exact expression. Evidence (17 Oct 2026, real-client captures): with this
/// rounding the free movement of 115,888 of 115,892 non-pop frames is bit-exact, and the equal-speed avoidance
/// ties resolve as in the client (the double-precision spec lost 193 of 1,564 tie frames, this one 27).
/// </summary>
internal static partial class ClientFloat
{
    public static float Add(float a, float b) => (float)(a + b);
    public static float Sub(float a, float b) => (float)(a - b);
    public static float Mul(float a, float b) => (float)(a * b);
    public static float Div(float a, float b) => (float)(a / b);
    public static float Sq(float a) => (float)(a * a);
    public static float Sqrt(float a) => MathF.Sqrt(a);
}
