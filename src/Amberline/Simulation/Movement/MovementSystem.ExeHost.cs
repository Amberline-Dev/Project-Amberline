using System.Buffers.Binary;
using System.Diagnostics;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Movement;

/// <summary>Which code moves and plans the match's units.</summary>
internal enum MovementEngine
{
    /// <summary>No engine was started: a <see cref="MovementSystem"/> built directly (scenarios, corpora, tools). It runs the C# port of the
    /// client's routines when the build carries it (the differential checks), otherwise the fallback.</summary>
    Direct,
    /// <summary>The client's own routines, run from the player's EXE by <see cref="ExeHostProcess"/> (production).</summary>
    Helper,
    /// <summary>The server's own planner and a plain path follower (production without a usable EXE).</summary>
    Fallback,
}

/// <summary>
/// Production movement through the helper (batch 608, Amberline phase 5a). The helper holds one persistent mirror of every body
/// (<see cref="Mirror"/>, a slot registered in the client's own actor grid); each cohort frame makes one call that carries only what changed
/// since the last call, in the frame's order: a body's row, its path, its removal, and the client's own update of each
/// unit that walks. A move order makes one call that runs the client's build-path routine on the same mirrors.
/// <para>The server keeps what the client's update does not have. The notes are in docs/gameplay/patch126-movement.md, under "Production through the helper":
/// forced moves, control effects and static units (the stored velocity decays and the route is held), the owner's stop, the server's
/// forced-stop routine, route ownership and publication, the hold zones and <see cref="TrackHeroDrift"/>.</para>
/// </summary>
public sealed partial class MovementSystem
{
    private const uint OpMirrors = 11;
    private const int MaxSlots = 512;
    // ponytail: the hero scan budget (ClientPlannerWork) counts the port's A* pops, which the helper does not report; a helper plan is charged
    // 20 pops per cell of its route (the port's own ratio: 2125 to 2988 pops over 108 to 160 cells, PlanningBudgetChecks) and the search cap
    // (10000 flagged, 800 otherwise) when the search failed or ran out. Count the pops in the helper if the pacing needs to be exact.
    private const double HelperPlanWorkPerCell = 20.0;
    private const int FailuresBeforeFallback = 3;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    internal MovementEngine Engine { get; private set; } = MovementEngine.Direct;

    private Func<ExeHostProcess>? _startHelper;
    private ExeHostProcess? _helper;
    private Action<string, string, IReadOnlyDictionary<string, object?>>? _engineLog;
    private readonly Queue<long> _helperFailures = new();
    private readonly Dictionary<uint, Mirror> _mirrors = new();
    private readonly Entity?[] _slotOwner = new Entity?[MaxSlots];
    private readonly Stack<ushort> _freeSlots = new(Enumerable.Range(0, MaxSlots).Reverse().Select(slot => (ushort)slot));
    private readonly MirrorRequest _request = new();
    private long _engineWork;

    /// <summary>One body as the helper holds it.</summary>
    private sealed class Mirror(Entity entity, ushort slot)
    {
        public readonly Entity Entity = entity;
        public readonly ushort Slot = slot;
        public bool Live;
        public MirrorRow Row;
        // The helper's path (x, absolute height, z per point) and index; null when the path is off.
        public float[]? Path;
        public uint Index;
        // The route the path was built from (the reference, its count and ends, or the target), to see when the server changed it.
        public List<Position2>? RouteList;
        public Position2? RouteTarget;
        public int RouteCount;
        public Position2 RouteHead, RouteTail;
        // The +0x60 and +0x64 stamps, and the StuckElapsed the server last read from +0x60.
        public float CleanStamp, ArrivalStamp;
        public double StuckRead = double.NaN;
        public bool UsedUp => Path is not null && Index >= Path.Length / 3;
    }

    private readonly record struct MirrorRow(float X, float Z, float Radius, float Speed, float LastDt, float VelX, float VelZ,
        float EndX, float EndZ, float CleanStamp, int RepathCount, bool Ghosted, bool GhostProof, bool Flag290, float ArrivalStamp,
        int Attempts, bool Collisions, bool UpdateFlag);

    /// <summary>
    /// Chooses the match's engine (the composition calls it once): the helper on the player's EXE when it can run, else the fallback, with
    /// one log line naming why. <paramref name="log"/> takes (level, event, properties) and records operations only. The helper process
    /// starts with the match's first frame or plan (<see cref="CallHelper"/>): a match that never moves a unit (a spell check) starts none.
    /// </summary>
    internal void StartEngine(GameDataPaths paths, int mapId, Action<string, string, IReadOnlyDictionary<string, object?>>? log)
    {
        var keepPort = false;
        KeepPortForDifferential(ref keepPort);
        if (keepPort) return;
        _engineLog = log;
        var exe = Path.Combine(paths.Root, "League of Legends.exe");
        var gridPath = MapDefinitions.NavigationGridPath(paths.ForMap(mapId));
        var helper = ExeHostProcess.Locate();
        var reason = ExeHostProcess.Unavailable(helper, exe, gridPath);
        if (reason is not null)
        {
            UseFallback(reason);
            return;
        }
        _startHelper = () => new ExeHostProcess(helper!, exe, gridPath, _navigation);
        _mapId = mapId;
        Engine = MovementEngine.Helper;
    }

    private int _mapId;
    private bool _helperStarted;

    /// <summary>
    /// Starts the helper now instead of on the match's first movement frame. The live server calls it while the players load, so the
    /// process start and the EXE check never pause a game frame; matches that never move a unit (the spell checks) skip it (batch 617).
    /// </summary>
    public void PrewarmEngine()
    {
        if (Engine == MovementEngine.Helper) StartHelper();
    }

    // Starts the helper once; the first request holds every mirror then, as it would have at composition. A helper that does not start
    // leaves the match on the fallback from here.
    private bool StartHelper()
    {
        if (_helperStarted) return _helper is not null;
        _helperStarted = true;
        try
        {
            _helper = _startHelper!();
            _engineLog?.Invoke("INFO", "exehost.started", new Dictionary<string, object?> { ["map"] = _mapId });
            return true;
        }
        catch (ExeHostException failure)
        {
            UseFallback(failure.Message);
            return false;
        }
    }

    /// <summary>Stops the helper (the match ended).</summary>
    internal void StopEngine()
    {
        _helper?.Dispose();
        _helper = null;
    }

    /// <summary>DIAGNOSTICS: ends the helper process as a crash would (the restart checks).</summary>
    internal void KillHelperForDiagnostics() => _helper?.Kill();

    /// <summary>DIAGNOSTICS: how often the helper was restarted.</summary>
    internal int HelperRestarts { get; private set; }

    private void UseFallback(string reason)
    {
        StopEngine();
        Engine = MovementEngine.Fallback;
        _engineLog?.Invoke("WARN", "exehost.fallback", new Dictionary<string, object?> { ["reason"] = reason });
        Console.Error.WriteLine($"movement: the client's movement code is unavailable ({reason}); this match uses the server's own planner and a plain mover");
    }

    // ------------------------------------------------------------------ the frame

    private void HelperActorFrame(double dt, bool heroes, Dictionary<uint, Position2>? intended, Dictionary<uint, Position2>? candidates,
        List<(uint NetId, double Speed, bool PathFollowerAllowed, IReadOnlyList<Entity> Colliders)>? steps)
    {
        var clock = (float)(heroes ? _heroMovementElapsed : _nonHeroMovementElapsed);
        _request.Begin(clock, (float)dt);
        if (!SyncBodies())
        {
            FallbackActorFrame(dt, heroes, steps);
            return;
        }
        var turns = new List<(Entity Entity, Position2 Start, bool Forced, Mirror? Mirror, uint IndexBefore)>();
        foreach (var entity in _world.Entities.Values
                     .Where(entity => entity.Alive && (entity.UnitType == UnitType.Hero) == heroes)
                     .OrderBy(entity => entity.NetId).ToArray())
        {
            var start = entity.Position;
            var forced = entity.ForcedMove is not null;
            var mirror = HelperTurn(entity, dt, clock, steps);
            turns.Add((entity, start, forced, mirror, mirror?.Index ?? 0));
        }
        var reply = CallHelper();
        var at = 8;
        foreach (var (entity, start, forced, mirror, indexBefore) in turns)
        {
            _lastIntendedStep = Position2.Zero;
            _lastBent = false;
            if (mirror is not null)
            {
                if (reply is null) { if (entity.Waypoints.Count > 0 || entity.Target is not null) FallbackStep(entity, dt); }
                else at = ApplyUpdate(reply, at, entity, mirror, clock, (float)dt, indexBefore, start);
                if (intended is not null) intended[entity.NetId] = start + _lastIntendedStep;
                if (candidates is not null) candidates[entity.NetId] = entity.Position;
            }
            ConfineHeldHero(entity, start);
            if (entity.Waypoints.Count > 0 || entity.Target is not null) _following.Add(entity.NetId);
            if (heroes && !forced && Engine == MovementEngine.Helper) TrackHeroDrift(entity, start);
        }
    }

    /// <summary>
    /// The server's side of one unit's turn, in the order of <see cref="StepNpc"/>: its layers run here and their result goes to the helper as
    /// the unit's row; returns the unit's mirror when the client's update runs for it.
    /// </summary>
    private Mirror? HelperTurn(Entity entity, double dt, float clock,
        List<(uint NetId, double Speed, bool PathFollowerAllowed, IReadOnlyList<Entity> Colliders)>? steps)
    {
        var start = entity.Position;
        var wasFollowing = _following.Remove(entity.NetId);
        entity.ForcedMoveGrace = Math.Max(0.0, entity.ForcedMoveGrace - dt);
        // Every body has its mirror (SyncBodies); a unit that is no body (the Nexus, an inhibitor) gets one only for an update.
        var mirror = _mirrors.GetValueOrDefault(entity.NetId);
        if (mirror is not null) ReadStuckWatch(entity, mirror, clock, dt);
        var activeForcedMove = entity.ForcedMove is { Completed: false };
        var allowed = !activeForcedMove && !IsStunned(entity) && !IsRooted(entity);
        steps?.Add((entity.NetId, entity.Speed, allowed, Array.Empty<Entity>()));
        var idle = entity.Waypoints.Count == 0 && entity.Target is null;
        if (activeForcedMove)
        {
            AdvanceForcedMove(entity, dt);
            entity.FrameVelocity = entity.Position - start;
            if (mirror is null) return null;
            SyncRow(entity, mirror);
            // A forced move is no path in the client's actor (the planner sees the unit as not moving): its path is off until a new one.
            if (mirror.Path is not null) PathOff(mirror);
            return null;
        }
        if (wasFollowing && !IsStatic(entity) && idle)
        {
            // The owner stopped the unit since its last frame: it is sent the one-point path (seated on its lattice anchor and stopped,
            // the client's path-follow routine), and the client's update arrives on it.
            entity.Position = ClientAnchor(entity.Position);
            entity.FrameVelocity = Position2.Zero;
            mirror = Updated(entity, mirror, clock, dt);
            var anchor = Point(entity.Position);
            SetPath(mirror, [.. anchor, .. anchor], route: null);
            _frameStopped.Add(entity.NetId);
            _request.Update(mirror.Slot, (float)_navigation.HeightAt(entity.Position));
            return mirror;
        }
        if (mirror is { UsedUp: true } && allowed && !IsStatic(entity) && idle)
        {
            // The arrival left its used-up path installed: the client's next update takes its top branch.
            SyncRow(entity, mirror);
            _request.Update(mirror.Slot, (float)_navigation.HeightAt(entity.Position));
            return mirror;
        }
        if (!allowed || IsStatic(entity) || idle)
        {
            // Control effects hold the route and an idle unit has nothing to march: the stored velocity decays by 0.75 per update (the client's update routine);
            // the server's own frame for the unit.
            entity.FrameVelocity = new Position2(ClientFloat.Mul((float)entity.FrameVelocity.X, (float)StoppedVelocityDecay),
                ClientFloat.Mul((float)entity.FrameVelocity.Z, (float)StoppedVelocityDecay));
            if (mirror is null) return null;
            SyncRow(entity, mirror);
            if (idle && mirror.Path is not null) PathOff(mirror);
            return null;
        }
        mirror = Updated(entity, mirror, clock, dt);
        SyncRoute(entity, mirror);
        _request.Update(mirror.Slot, (float)_navigation.HeightAt(entity.Position));
        return mirror;
    }

    // The mirror of a unit the client's update runs for, with its row sent.
    private Mirror Updated(Entity entity, Mirror? mirror, float clock, double dt)
    {
        if (mirror is null)
        {
            mirror = MirrorOf(entity);
            ReadStuckWatch(entity, mirror, clock, dt);
        }
        SyncRow(entity, mirror);
        return mirror;
    }

    // The client's stuck-watch field reads clock - stamp (the clock routine). A mirror not read yet, or a StuckElapsed the server wrote since its last reading, is stamped as
    // reading that value at the previous frame (as StepNpc stamps it).
    private static void ReadStuckWatch(Entity entity, Mirror mirror, float clock, double dt)
    {
        if (entity.StuckElapsed != mirror.StuckRead)
            mirror.CleanStamp = ClientFloat.Sub(ClientFloat.Sub(clock, (float)dt), (float)entity.StuckElapsed);
        entity.StuckElapsed = ClientFloat.Sub(clock, mirror.CleanStamp);
        mirror.StuckRead = entity.StuckElapsed;
    }

    /// <summary>Reads one unit's update from the reply into the entity and its mirror, then runs the server's layers after it.</summary>
    private int ApplyUpdate(byte[] reply, int at, Entity entity, Mirror mirror, float clock, float dt, uint indexBefore, Position2 start)
    {
        var r = reply.AsSpan(at);
        var origin = at;
        float F(int offset) => BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(origin + offset));
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(origin + offset));
        float x = F(0), z = F(4), velX = F(8), velZ = F(12), cleanStamp = F(16), arrivalStamp = F(20), intendedX = F(24), intendedZ = F(28);
        int attempts = I(32), repath = I(36);
        var index = (uint)I(40);
        var points = (uint)I(44);
        bool pathOn = r[48] != 0, held = r[49] != 0, bent = r[50] != 0, intendedSeen = r[51] != 0;
        int colliderCount = BinaryPrimitives.ReadUInt16LittleEndian(r[52..]);
        at += 56;
        List<(Entity Entity, Position2 Position)>? colliders = null;
        for (var k = 0; k < colliderCount; k++, at += 2)
            if (_slotOwner[BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(at))] is { } other)
                (colliders ??= []).Add((other, other.Position));
        float[]? bentPath = null;
        if (bent)
        {
            bentPath = new float[points * 3];
            Buffer.BlockCopy(reply, at, bentPath, 0, (int)points * 12);
            at += (int)points * 12;
        }

        entity.Position = new Position2(x, z);
        entity.FrameVelocity = new Position2(velX, velZ);
        mirror.CleanStamp = cleanStamp;
        mirror.ArrivalStamp = arrivalStamp;
        entity.StuckElapsed = ClientFloat.Sub(clock, cleanStamp);
        mirror.StuckRead = entity.StuckElapsed;
        entity.RepathAttempts = attempts;
        entity.RepathCount = repath;
        var intendedStep = intendedSeen ? new Position2(intendedX, intendedZ) : entity.FrameVelocity;
        _lastIntendedStep = intendedStep;
        _lastBent = bent && pathOn;
        if (DistanceSquared(entity.Position, start) > 0.0) entity.SeparationAccumulation += entity.FrameVelocity - intendedStep;
        if (colliders is not null) _frameColliders[entity.NetId] = colliders;

        // The route: the client advanced its index (the server drops the points it walked), bent it, or used it up (the server drops it).
        var hadRoute = entity.Waypoints.Count > 0 || entity.Target is not null;
        if (bent) mirror.Path = pathOn ? bentPath : null;
        if (!pathOn) mirror.Path = null;
        mirror.Index = pathOn ? index : 0;
        if (hadRoute)
        {
            if (!pathOn || index >= points)
            {
                entity.Target = null;
                entity.Waypoints.Clear();
            }
            else if (bent)
            {
                var left = new List<Position2>();
                for (var k = (int)index; k < points; k++) left.Add(new Position2(bentPath![k * 3], bentPath[k * 3 + 2]));
                if (entity.Waypoints.Count > 0) { entity.Waypoints.Clear(); entity.Waypoints.AddRange(left); }
                else { entity.Target = null; entity.Waypoints = left; }
            }
            else if (entity.Waypoints.Count > 0)
                entity.Waypoints.RemoveRange(0, Math.Min(entity.Waypoints.Count, (int)(index - indexBefore)));
            NoteRoute(entity, mirror);
        }
        mirror.Row = mirror.Row with
        {
            X = x, Z = z, VelX = velX, VelZ = velZ, CleanStamp = cleanStamp, ArrivalStamp = arrivalStamp, Attempts = attempts,
            RepathCount = repath, LastDt = dt,
        };
        if (held) HelperForceStopServer(entity);
        return at;
    }

    /// <summary>
    /// <see cref="PublishForceStop"/> after the client's update reached one of its two forced-stop sites (the helper
    /// reports it). The stop is installed and published at once; its arrival is the unit's next frame, through the one-point path.
    /// </summary>
    private void HelperForceStopServer(Entity entity)
    {
        PublishForceStop(entity);
        _following.Add(entity.NetId);
    }

    // ------------------------------------------------------------------ mirrors

    private Mirror MirrorOf(Entity entity)
    {
        if (_mirrors.TryGetValue(entity.NetId, out var mirror))
        {
            if (ReferenceEquals(mirror.Entity, entity)) return mirror;
            RemoveMirror(mirror);
        }
        var slot = _freeSlots.Pop();
        mirror = new Mirror(entity, slot) { ArrivalStamp = ClientClock(entity) };
        mirror.CleanStamp = ClientFloat.Sub(ClientClock(entity), (float)entity.StuckElapsed);
        _mirrors[entity.NetId] = mirror;
        _slotOwner[slot] = entity;
        return mirror;
    }

    private void RemoveMirror(Mirror mirror)
    {
        if (mirror.Live) _request.Remove(mirror.Slot);
        _mirrors.Remove(mirror.Entity.NetId);
        _slotOwner[mirror.Slot] = null;
        _freeSlots.Push(mirror.Slot);
    }

    /// <summary>The bodies of the frame (<see cref="NpcBodies"/>): the departed ones leave the helper, the others send what changed. False when
    /// there are more than the helper's slots (the match then falls back).</summary>
    private bool SyncBodies()
    {
        var bodies = NpcBodies();
        if (bodies.Count > MaxSlots - 64)
        {
            UseFallback($"{bodies.Count} bodies, more than the helper's {MaxSlots - 64}");
            return false;
        }
        var present = new HashSet<uint>(bodies.Count);
        foreach (var body in bodies) present.Add(body.NetId);
        foreach (var mirror in _mirrors.Values.ToArray())
            if (!present.Contains(mirror.Entity.NetId) || !ReferenceEquals(_world.Get(mirror.Entity.NetId), mirror.Entity)) RemoveMirror(mirror);
        foreach (var body in bodies)
        {
            var mirror = MirrorOf(body);
            SyncRow(body, mirror);
            if (body.Waypoints.Count > 0 || body.Target is not null) SyncRoute(body, mirror);
        }
        return true;
    }

    private MirrorRow RowOf(Entity e, Mirror m)
    {
        var end = e.Waypoints.Count > 0 ? e.Waypoints[^1] : e.Target ?? e.Position;
        return new MirrorRow((float)e.Position.X, (float)e.Position.Z, ClientPathRadius(e), (float)e.Speed,
            m.Live ? m.Row.LastDt : (float)(e.UnitType == UnitType.Hero ? HeroTickSeconds : FixedTickSeconds),
            (float)e.FrameVelocity.X, (float)e.FrameVelocity.Z, (float)end.X, (float)end.Z, m.CleanStamp, e.RepathCount, IsGhosted(e),
            e.GhostProof, e.PathingFlag290, m.ArrivalStamp, e.RepathAttempts, e.ForcedMove is null && CollisionSolverEnabled,
            e.SpawnIgnoreCollision);
    }

    private void SyncRow(Entity entity, Mirror mirror)
    {
        var row = RowOf(entity, mirror);
        if (mirror.Live && row == mirror.Row) return;
        _request.Set(mirror.Slot, row);
        mirror.Row = row;
        mirror.Live = true;
    }

    /// <summary>The route of a unit that has one, sent as its path when the server changed it: the unit's position, then the route.</summary>
    private void SyncRoute(Entity entity, Mirror mirror)
    {
        var target = entity.Waypoints.Count > 0 ? null : entity.Target;
        var count = target is null ? entity.Waypoints.Count : 1;
        var head = target ?? entity.Waypoints[0];
        var tail = target ?? entity.Waypoints[^1];
        if (mirror.Path is not null && !mirror.UsedUp && ReferenceEquals(mirror.RouteList, target is null ? entity.Waypoints : null) &&
            mirror.RouteTarget == target && mirror.RouteCount == count && mirror.RouteHead == head && mirror.RouteTail == tail) return;
        var points = new float[(count + 1) * 3];
        Point(entity.Position).CopyTo(points, 0);
        for (var k = 0; k < count; k++) Point(target ?? entity.Waypoints[k]).CopyTo(points, (k + 1) * 3);
        SetPath(mirror, points, entity);
    }

    private void SetPath(Mirror mirror, float[] points, Entity? route)
    {
        _request.Path(mirror.Slot, 1, points);
        mirror.Path = points;
        mirror.Index = 1;
        if (route is null)
        {
            mirror.RouteList = null;
            mirror.RouteTarget = null;
            mirror.RouteCount = 0;
        }
        else NoteRoute(route, mirror);
    }

    private void PathOff(Mirror mirror)
    {
        _request.Path(mirror.Slot, 0, []);
        mirror.Path = null;
        mirror.Index = 0;
        mirror.RouteList = null;
        mirror.RouteTarget = null;
        mirror.RouteCount = 0;
    }

    private static void NoteRoute(Entity entity, Mirror mirror)
    {
        var target = entity.Waypoints.Count > 0 ? null : entity.Target;
        mirror.RouteList = target is null ? entity.Waypoints : null;
        mirror.RouteTarget = target;
        mirror.RouteCount = target is null ? entity.Waypoints.Count : 1;
        if (mirror.RouteCount == 0) return;
        mirror.RouteHead = target ?? entity.Waypoints[0];
        mirror.RouteTail = target ?? entity.Waypoints[^1];
    }

    // A path point as the helper takes it: x, the grid's height, z.
    private float[] Point(Position2 p) => [(float)p.X, (float)_navigation.HeightAt(p), (float)p.Z];

    // ------------------------------------------------------------------ the call, restarts and the fallback switch

    /// <summary>Sends the request; on a failure restarts the helper, sends every mirror again and repeats the request's updates and plans.
    /// Returns null when the failures switched the match to the fallback.</summary>
    private byte[]? CallHelper()
    {
        if (!StartHelper()) return null;
        while (true)
        {
            if (_helper is null) return null;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var reply = _helper.Call(OpMirrors, _request.Bytes).ToArray();
                _request.Measure(Stopwatch.GetElapsedTime(started).TotalMicroseconds, reply);
                return reply;
            }
            catch (ExeHostException failure)
            {
                if (!RestartHelper(failure.Message)) return null;
                // The new helper holds nothing: every mirror goes again (the server's copy is complete), then the request's updates and plans.
                _request.Resync(_mirrors.Values.Where(m => m.Live).Select(m => (m.Slot, m.Row, m.Path, m.Index)));
            }
        }
    }

    private bool RestartHelper(string reason)
    {
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            _helperFailures.Enqueue(now);
            while (_helperFailures.Count > 0 && Stopwatch.GetElapsedTime(_helperFailures.Peek(), now) > FailureWindow) _helperFailures.Dequeue();
            _engineLog?.Invoke("WARN", "exehost.failed", new Dictionary<string, object?>
            {
                ["reason"] = reason, ["failures_within_minute"] = _helperFailures.Count,
            });
            _helper?.Dispose();
            _helper = null;
            if (_helperFailures.Count >= FailuresBeforeFallback)
            {
                UseFallback($"{_helperFailures.Count} helper failures within a minute, the last: {reason}");
                return false;
            }
            try
            {
                _helper = _startHelper!();
                HelperRestarts++;
                _engineLog?.Invoke("INFO", "exehost.restarted", new Dictionary<string, object?> { ["restarts"] = HelperRestarts });
                return true;
            }
            catch (ExeHostException failure) { reason = failure.Message; }
        }
    }

    // ------------------------------------------------------------------ planning

    /// <summary>The client's own build-path routine and SmoothPath for <paramref name="mover"/>, on the helper's mirrors, with the
    /// units the mover's client cannot see out of the grid for the call (<see cref="ClientActorsAround"/>'s rule).</summary>
    private IReadOnlyList<Position2>? HelperPlan(Entity mover, Position2 click, Func<Entity, bool>? visibleTo)
    {
        _request.Begin((float)_simulationElapsed, 0f);
        if (!SyncBodies()) return FallbackPlan(mover, click);
        var mirror = MirrorOf(mover);
        SyncRow(mover, mirror);
        if (mover.Waypoints.Count > 0 || mover.Target is not null) SyncRoute(mover, mirror);
        bool Visible(Entity other) => visibleTo?.Invoke(other) ?? (other.Team == mover.Team || other.Visible);
        var hidden = new List<ushort>();
        foreach (var other in _mirrors.Values)
            if (other != mirror && (other.Entity.UnitType is UnitType.Missile ||
                                    other.Entity.UnitType != UnitType.Turret && !Visible(other.Entity)))
                hidden.Add(other.Slot);
        _request.Plan(mirror.Slot, (float)click.X, (float)click.Z, hidden);
        var reply = CallHelper();
        if (reply is null) return FallbackPlan(mover, click);
        var count = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(8));
        var partial = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(12)) != 0;
        var route = new Position2[count];
        for (var k = 0; k < count; k++)
            route[k] = new Position2(BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(16 + k * 12)),
                BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(16 + k * 12 + 8)));
        _engineWork += count == 0 || partial
            ? mover.PathingFlag290 ? 10000 : 800
            : Math.Max(1L, (long)(RouteLength(route) / _navigation.CellSize * HelperPlanWorkPerCell));
        return count == 0 ? null : route;
    }

    // ------------------------------------------------------------------ the fallback

    /// <summary>
    /// NOT RETAIL: the match has no usable copy of the client's routines (no EXE, another EXE, or a helper that kept failing). Retail moved
    /// every unit with the client's update routine (contact, avoidance, the stuck block, the arrival correction); this frame only walks each
    /// route with the server's own fixed-origin follower (<see cref="FixedOriginStep"/>), so units pass through each other, but every order,
    /// stop, dash and control effect works and the match stays playable.
    /// </summary>
    private void FallbackActorFrame(double dt, bool heroes,
        List<(uint NetId, double Speed, bool PathFollowerAllowed, IReadOnlyList<Entity> Colliders)>? steps)
    {
        foreach (var entity in _world.Entities.Values
                     .Where(entity => entity.Alive && (entity.UnitType == UnitType.Hero) == heroes)
                     .OrderBy(entity => entity.NetId).ToArray())
        {
            var start = entity.Position;
            var wasFollowing = _following.Remove(entity.NetId);
            entity.ForcedMoveGrace = Math.Max(0.0, entity.ForcedMoveGrace - dt);
            var activeForcedMove = entity.ForcedMove is { Completed: false };
            var allowed = !activeForcedMove && !IsStunned(entity) && !IsRooted(entity);
            steps?.Add((entity.NetId, entity.Speed, allowed, Array.Empty<Entity>()));
            var idle = entity.Waypoints.Count == 0 && entity.Target is null;
            if (activeForcedMove)
            {
                AdvanceForcedMove(entity, dt);
                entity.FrameVelocity = entity.Position - start;
            }
            else if (wasFollowing && !IsStatic(entity) && idle)
            {
                entity.Position = ClientAnchor(entity.Position);
                entity.FrameVelocity = Position2.Zero;
                _frameStopped.Add(entity.NetId);
            }
            else if (!allowed || IsStatic(entity) || idle)
                entity.FrameVelocity = new Position2(entity.FrameVelocity.X * StoppedVelocityDecay, entity.FrameVelocity.Z * StoppedVelocityDecay);
            else FallbackStep(entity, dt);
            ConfineHeldHero(entity, start);
            if (entity.Waypoints.Count > 0 || entity.Target is not null) _following.Add(entity.NetId);
        }
    }

    /// <summary>NOT RETAIL (see <see cref="FallbackActorFrame"/>): one frame along the route at the unit's speed, no contact.</summary>
    private void FallbackStep(Entity entity, double dt)
    {
        var route = entity.Waypoints.Count > 0 ? entity.Waypoints : [entity.Target!.Value];
        var (next, _) = FixedOriginStep(entity.Position, route, Math.Max(0.0, entity.Speed) * dt);
        if (route.Count == 0) entity.Target = null;
        entity.FrameVelocity = next - entity.Position;
        entity.Position = next;
    }

    /// <summary>NOT RETAIL (see <see cref="FallbackActorFrame"/>): the server's own planner (<see cref="NavigationGrid.FindPath"/>; the hero
    /// view for heroes) instead of the client's build-path routine, which plans around units.</summary>
    private IReadOnlyList<Position2>? FallbackPlan(Entity mover, Position2 click)
    {
        var work = _navigation.PlanningWork;
        var path = mover.UnitType == UnitType.Hero
            ? _navigation.FindHeroPath(mover.Position, click)
            : _navigation.FindPath(mover.Position, click);
        _engineWork += _navigation.PlanningWork - work;
        return path.Count > 1 ? ClientWaypoints(path) : null;
    }

    // ------------------------------------------------------------------ DIAGNOSTICS: cost

    /// <summary>DIAGNOSTICS: the helper's cost per frame and per plan (median / p95 / max), for the headless match report.</summary>
    internal string? HelperSummary() => Engine == MovementEngine.Direct ? null : _request.Summary(Engine, HelperRestarts);

    /// <summary>
    /// The request being built (exehost.c, op 11): a header, then records in order. The updates and plans are also kept apart, so a restart
    /// can send every mirror again and repeat them.
    /// </summary>
    private sealed class MirrorRequest
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _w;
        private readonly List<byte[]> _actions = new();
        private uint _count;
        private float _now, _dt;
        private readonly List<double> _frameRound = new(), _frameHelper = new(), _frameUpdates = new(), _frameKiB = new(), _frameRecords = new();
        private readonly List<double> _planRound = new(), _planHelper = new();

        public MirrorRequest() => _w = new BinaryWriter(_stream);

        public void Begin(float now, float dt)
        {
            _stream.SetLength(0);
            _now = now;
            _dt = dt;
            _w.Write(now); _w.Write(dt); _w.Write(0u);
            _count = 0;
            _actions.Clear();
        }

        public ReadOnlySpan<byte> Bytes
        {
            get
            {
                var buffer = _stream.GetBuffer();
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), _count);
                return buffer.AsSpan(0, (int)_stream.Length);
            }
        }

        private void Head(byte kind, ushort slot)
        {
            _w.Write(kind); _w.Write((byte)0); _w.Write(slot);
            _count++;
        }

        public void Set(ushort slot, MirrorRow row)
        {
            Head(1, slot);
            _w.Write(row.X); _w.Write(row.Z); _w.Write(row.Radius); _w.Write(row.Speed); _w.Write(row.LastDt); _w.Write(row.VelX);
            _w.Write(row.VelZ); _w.Write(row.EndX); _w.Write(row.EndZ); _w.Write(row.CleanStamp); _w.Write(row.RepathCount);
            _w.Write(row.Ghosted); _w.Write(row.GhostProof); _w.Write(row.Flag290); _w.Write((byte)0);
            _w.Write(row.ArrivalStamp); _w.Write(row.Attempts); _w.Write(row.Collisions); _w.Write(row.UpdateFlag); _w.Write((ushort)0);
        }

        public void Remove(ushort slot) => Head(2, slot);

        public void Path(ushort slot, uint index, float[] points)
        {
            Head(3, slot);
            _w.Write(index); _w.Write((uint)(points.Length / 3));
            foreach (var value in points) _w.Write(value);
        }

        public void Update(ushort slot, float height) => Action(4, slot, w => w.Write(height));

        public void Plan(ushort slot, float x, float z, List<ushort> hidden) => Action(5, slot, w =>
        {
            w.Write(x); w.Write(z); w.Write((uint)hidden.Count);
            foreach (var other in hidden) w.Write(other);
        });

        private void Action(byte kind, ushort slot, Action<BinaryWriter> payload)
        {
            var start = _stream.Position;
            Head(kind, slot);
            payload(_w);
            _w.Flush();
            _actions.Add(_stream.GetBuffer().AsSpan((int)start, (int)(_stream.Position - start)).ToArray());
        }

        /// <summary>A fresh helper: every mirror's row and path, then the updates and plans of the request again.</summary>
        public void Resync(IEnumerable<(ushort Slot, MirrorRow Row, float[]? Path, uint Index)> mirrors)
        {
            var actions = _actions.ToArray();
            Begin(_now, _dt);
            foreach (var (slot, row, path, index) in mirrors)
            {
                Set(slot, row);
                if (path is not null) Path(slot, index, path);
            }
            foreach (var action in actions)
            {
                _w.Write(action);
                _actions.Add(action);
                _count++;
            }
        }

        public void Measure(double roundUs, byte[] reply)
        {
            var helperUs = BinaryPrimitives.ReadUInt32LittleEndian(reply) / 1000.0;
            if (_dt == 0f)
            {
                _planRound.Add(roundUs);
                _planHelper.Add(helperUs);
                return;
            }
            _frameRound.Add(roundUs);
            _frameHelper.Add(helperUs);
            _frameUpdates.Add(BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)) / 1000.0);
            _frameKiB.Add(_stream.Length / 1024.0);
            _frameRecords.Add(_count);
        }

        public string Summary(MovementEngine engine, int restarts)
        {
            static string Stats(List<double> values)
            {
                if (values.Count == 0) return "-";
                var sorted = values.Order().ToArray();
                return FormattableString.Invariant($"{sorted[sorted.Length / 2]:0.0}/{sorted[(int)(sorted.Length * 0.95)]:0.0}/{sorted[^1]:0.0}");
            }
            var transfer = _frameRound.Zip(_frameHelper, (round, helper) => round - helper).ToList();
            return FormattableString.Invariant(
                $"movement engine={engine} restarts={restarts} frames={_frameRound.Count} (median/p95/max) updates-us={Stats(_frameUpdates)} ") +
                $"helper-us={Stats(_frameHelper)} round-trip-us={Stats(_frameRound)} transfer-us={Stats(transfer)} request-KiB={Stats(_frameKiB)} " +
                $"records={Stats(_frameRecords)} plans={_planRound.Count} plan-helper-us={Stats(_planHelper)} plan-round-trip-us={Stats(_planRound)}";
        }
    }
}
