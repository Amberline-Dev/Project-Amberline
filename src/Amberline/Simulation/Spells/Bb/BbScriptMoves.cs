using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>One displacement a script started (<c>BBMove</c>, <c>BBMoveAway</c>, <c>BBMoveToUnit</c>).</summary>
internal sealed class BbScriptMove
{
    public required Entity Unit { get; init; }
    public required ForcedMoveState Move { get; init; }
    /// <summary>
    /// The script buff of the moved unit whose hook started the move: its <c>BuffOnMove*</c> hooks run when the move is over. Null when a spell's
    /// own hook started it (<c>BBMove</c> in a <c>SelfExecute</c>): the unit's script buffs that have the hooks are told then.
    /// </summary>
    public BbBuffInstance? Buff { get; init; }
    /// <summary>
    /// The script buffs the unit already held when a spell's own hook (no buff) started the move: the buffs it holds beyond these when the move is over
    /// (the one the same hook added after the <c>BBMove</c>) get the <c>BuffOnMove*</c> hooks. Null when a buff started the move.
    /// </summary>
    public HashSet<BbBuffInstance>? HeldBefore { get; init; }
    /// <summary><c>POSTPONE_CURRENT_ORDER</c>: the hero order to hand back after the move.</summary>
    public VayneTumbleOrderIntent? Postponed { get; init; }
    /// <summary>The units this move has already reported as hit (<c>BuffOnCollision</c> runs once per unit and move).</summary>
    public HashSet<uint> Collided { get; } = new();
}

/// <summary>
/// The scripted displacements of one champion's script engine. The movement system owns the motion (<see cref="ForcedMoveState"/>,
/// <c>MovementSystem.StartForcedMove</c>, the 0x67 dash packet through <c>ISpellEffectSink.SendDash</c>); this class is what the
/// hand-written champions each repeat around it: it picks the landing, starts the move, announces it, notices when it is over
/// (arrived, stopped short, replaced, or the unit died), releases the movement system and runs the hooks of the script buff that
/// started it (<c>BuffOnMoveSuccess</c>, <c>BuffOnMoveFailure</c>, <c>BuffOnCollisionTerrain</c>, <c>BuffOnMoveEnd</c>).
/// <para>
/// Reconstruction: the client registers <c>BBMove</c> and its siblings as bare returns, so what the server
/// does with Speed, Gravity, IdealDistance, MoveBackBy, MovementType and the order flags is read from the 56 shipped call sites and
/// from what the hand-written ports of the same scripts already established in playtests (docs/scripting/building-blocks-engine.md,
/// "Movement natives"). The hook names and their order exist only in the scripts (the client has none of them): success or failure
/// first, then the end hook, because <c>BlindMonkQTwoDash</c> and <c>AkaliShadowDanceKick</c> deal their landing damage in
/// <c>BuffOnMoveSuccess</c> and remove their buff in <c>BuffOnMoveEnd</c>.
/// </para>
/// </summary>
internal sealed class BbScriptMoves
{
    // One 30 Hz step: how far a trip-sized buff and the move it started may round their ends apart at tick quantisation.
    private const double TripStep = 1.0 / 30.0;

    // Batch 371: how close a repeated update-hook move's endpoint may sit to the running dash's endpoint (wire quantisation is 2 units).
    private const double RepeatTolerance = 4.0;

    /// <summary>Reconstruction: retail's rule for a postponed hero order is not recovered; this is how long it waits for the hero to be able to walk again before it is dropped.</summary>
    private const double ResumeWindow = 5.0;

    /// <summary>
    /// Reconstruction (batches 222 and 241): a script move has no duration in retail (a dash ends at its destination, the first wall or when the
    /// unit dies; a follow by contact, target loss or track distance). <see cref="ForcedMoveState"/> carries a timer, which the lifecycle
    /// monitor requires to be finite, so a move gets this safety stop past its trip; a dash, whose speed is positive, reaches its end long
    /// before. Retail's rule for a move that can never end is not recovered.
    /// </summary>
    private const double FollowSafetyStop = 60.0;

    private readonly BbSpellHost _host;
    private readonly List<BbScriptMove> _moves = new();
    private readonly HashSet<Entity> _tracking = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Entity Unit, VayneTumbleOrderIntent Order, double Until)> _resumes = new();

    public BbScriptMoves(BbSpellHost host) => _host = host;

    /// <summary>The displacements this engine is following (a check reads it).</summary>
    public IReadOnlyList<BbScriptMove> Active => _moves;

    /// <summary><c>BBStartTrackingCollisions</c> (Target and Value; the body is server-only).</summary>
    public void SetCollisionTracking(Entity unit, bool on)
    {
        if (on) _tracking.Add(unit);
        else _tracking.Remove(unit);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Starting
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A dash to <paramref name="requested"/>. <c>FURTHEST_WITHIN_RANGE</c> lands on the furthest walkable point of the ray (terrain may be
    /// crossed, <see cref="MovementSystem.FurthestWalkableLanding"/>); <c>FIRST_COLLISION_HIT</c> and <c>FIRST_WALL_HIT</c> stop at the first
    /// wall or static body (<c>ForcedMoveState.FirstCollision</c>). Returns the move, or null when nothing can move (a dead unit, no speed).
    /// </summary>
    public ForcedMoveState? Start(Entity unit, Position2 requested, double speed, double gravity, BbQueryMode type, bool postpone,
        bool faceMovement, bool fitBody = false)
    {
        var firstCollision = type is BbQueryMode.FIRST_COLLISION_HIT or BbQueryMode.FIRST_WALL_HIT;
        if (!firstCollision && type != BbQueryMode.FURTHEST_WITHIN_RANGE)
            throw BbSpellHost.Unbound($"a move with MovementType {type}", "W22");
        if (!unit.Alive || !(speed > 0.0) || !double.IsFinite(speed)) return null;
        var spells = _host.Spells;
        var movement = spells.Movement;
        // Batch 460: a held unit (Dominion's setup) dashes no farther than the edge of its hold (MovementSystem.HeldInside).
        requested = movement.HeldInside(unit, requested);
        var origin = unit.Position;
        var destination = firstCollision ? requested : movement.FurthestWalkableLanding(origin, requested, unit);
        // Owner decision B3 #3 (26 September 2026, "a pulled body never lands in a wall"), kept from the hand-written Rocket Grab: a move to a
        // given point (no IdealDistance cap: a pull onto the puller's centre) whose ray has no body-clear floor, the shared landing falls back
        // to the raw point. Blitzcrank standing in a walkable notch 0.8 units from terrain made that point one the victim's body does not fit
        // (clearance 0.8 against 42.1), so the victim lands on the nearest placement that fits, as the hand-written pull did.
        if (!firstCollision && fitBody)
        {
            var radius = movement.MovementRadius(unit);
            if (movement.Navigation.PointClearance(destination) < radius + 4.0)
                destination = movement.Navigation.ClosestSummonPlacement(requested, radius);
        }
        var length = BbSpellHost.Distance(origin, destination);
        var (source, effect) = Origin(unit);
        // Batch 371 (owner finding 107, "Skarner R really stuttery when moving the enemy"): an update hook that repeats a move to the endpoint
        // the unit is already dashing to continues that dash. SkarnerImpale's BuffOnUpdateActions re-issues BBMove every update (30 Hz) at 2.6 times
        // the distance, and each re-issue superseded the running dash and sent a new one: 45 dash packets in one 1.5 s impale, the victim restarting
        // its dash every tick. The endpoint is the caster less the away distance, the same point on every update, so the first dash keeps it and its
        // trip of 1 / 2.6 s. Once the victim stands on that point, the repeat sends nothing (a zero-length move sends nothing either).
        // Reconstruction: the retail server's handling of a repeated update-hook BBMove is unrecovered; the dash count is the emulator's rule.
        if (!firstCollision && _host.HasFrame && _host.Frame.Hook == "BuffOnUpdateActions" &&
            (unit.ForcedMove is { Completed: false } running
                ? running.SourceNetId == source && BbSpellHost.Distance(running.Destination, destination) <= RepeatTolerance
                : BbSpellHost.Distance(origin, destination) <= RepeatTolerance))
            return unit.ForcedMove;
        if (length <= 1e-9)
            return Track(unit, InstantMove(destination, speed, source, effect), null, null);

        var trip = length / speed;
        var duration = trip + FollowSafetyStop;
        var announced = firstCollision ? movement.FirstCollisionDashLanding(origin, destination, speed, duration, unit) : destination;
        var postponed = postpone && unit.UnitType == UnitType.Hero ? PostponedHeroOrders.Capture(unit) : null;
        var move = new ForcedMoveState(destination, speed, duration, source, effect, firstCollision);
        if (faceMovement) Geometry.FaceTowards(unit, announced);
        Supersede(unit);
        movement.StartForcedMove(unit.NetId, move, cancelOrder: true);
        if (spells.Effects.IsReferenceReady(unit))
            spells.Effects.SendDash(unit, origin, announced, speed, gravity, spells.World.Clock.SyncId());
        return Track(unit, move, RunningBuffOf(unit), postponed, trip);
    }

    /// <summary>
    /// <c>BBMoveToUnit</c>: a dash that follows a moving unit (the 0x67 follow form: target, track distance, arrival distance). The
    /// move ends when the unit is in contact with its target, or when the target dies or leaves the track distance.
    /// </summary>
    public ForcedMoveState? StartFollow(Entity unit, Entity target, double speed, double gravity, double maxTrackDistance, double moveBackBy,
        double timeOverride, bool postpone, bool faceMovement)
    {
        if (!unit.Alive || !target.Alive || !(speed > 0.0) || !double.IsFinite(speed)) return null;
        var spells = _host.Spells;
        var movement = spells.Movement;
        var origin = unit.Position;
        // Reconstruction: BBMoveToUnit exposes no numeric arrival radius (IdealDistance is the distance at the time of the call in the
        // one shipped use that sets it); the move ends in contact, the same body shell the follow solver uses.
        var arrival = Math.Max(0.0, unit.Radius) + Math.Max(0.0, target.Radius) + movement.Settings.Collision.SkinHighCollider +
                      Math.Max(0.0, moveBackBy);
        var dx = target.Position.X - origin.X;
        var dz = target.Position.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        var destination = distance <= arrival + 1e-9
            ? origin
            : movement.ClientAnchor(new Position2(target.Position.X - dx / distance * arrival, target.Position.Z - dz / distance * arrival));
        var track = maxTrackDistance > 0.0 ? maxTrackDistance : double.PositiveInfinity;
        // Batch 222 (item 4): a script move has no duration in retail. A follow ends by contact, target loss or its track distance; the safety stop
        // below only catches a follow that can never end (Reconstruction).
        var remaining = timeOverride > 0.0 ? timeOverride : FollowSafetyStop;
        var (source, effect) = Origin(unit);
        var move = new ForcedMoveState(destination, speed, remaining, source, effect, firstCollision: false, target.NetId, track);
        var postponed = postpone && unit.UnitType == UnitType.Hero ? PostponedHeroOrders.Capture(unit) : null;
        if (faceMovement) Geometry.FaceTowards(unit, target.Position);
        Supersede(unit);
        movement.StartForcedMove(unit.NetId, move, cancelOrder: true);
        if (spells.Effects.IsReferenceReady(unit))
            spells.Effects.SendDash(unit, origin, move.Destination, speed, gravity, spells.World.Clock.SyncId(), followNetId: target.NetId,
                followDistance: double.IsFinite(track) ? track : 0.0, followBackDistance: arrival);
        return Track(unit, move, RunningBuffOf(unit), postponed);
    }

    private (uint Source, string Effect) Origin(Entity unit)
    {
        var frame = _host.HasFrame ? _host.Frame : null;
        // The caster of the running chain started it (RocketGrab2's pull is Blitzcrank's), else the mover itself; the effect is the
        // running script's name so the replication layer's per-effect rules (the Lee Sin follow dash) apply to a scripted move too.
        return ((frame?.Caster ?? frame?.Owner ?? unit).NetId, string.IsNullOrWhiteSpace(frame?.ScriptName) ? "BBMove" : frame!.ScriptName);
    }

    private BbBuffInstance? RunningBuffOf(Entity unit) =>
        _host.HasFrame && _host.Frame.Buff is { } running && ReferenceEquals(running.Owner, unit) ? running : null;

    private static ForcedMoveState InstantMove(Position2 destination, double speed, uint source, string effect)
    {
        // Already there: nothing to move or announce, the hooks still run (a pull onto the puller's own spot ends at once).
        var move = new ForcedMoveState(destination, speed, 0.0, source, effect) { Completed = true, Succeeded = true };
        return move;
    }

    private ForcedMoveState Track(Entity unit, ForcedMoveState move, BbBuffInstance? buff, VayneTumbleOrderIntent? postponed, double? trip = null)
    {
        buff ??= RunningBuffOf(unit);
        // Reconstruction: the buff that started a dash can be sized to the trip (Malphite's UFSlash: distance over speed), so it ends with the
        // move. The move ticks in steps and ends on the step that reaches its destination, up to one step after the trip time, and a buff that
        // ran out first took its BuffOnMoveSuccess with it (the Unstoppable Force never landed). Which of the two retail ended first on the same
        // server frame is not recovered; a trip-sized buff lives one step past the trip so the move's hooks run. A buff the script made
        // shorter than that keeps its life, as in retail (Power Fist's 0.5 s lock over a 0.9 s knock-up ends at 0.5 s and the unit goes on flying).
        // A buff a BuffOnAllowAdd cut on its way in is not trip-sized any more (batch 296): Fling's 0.447 s stun under Insanity Potion's 0.9
        // lands at 0.403, inside one step of the 0.42 s trip, and was stretched back to 0.453, the length it had without the potion.
        // Batch 349: a move the buff's own update hook issues is not the dash the buff was sized to. SkarnerImpale's BuffOnUpdateActions drags the
        // victim every update at 2.6 x the distance, a trip of 1 / 2.6 s whatever the distance, so the impale's remaining time met the trip window on
        // every update once it fell to 0.385 s and was stretched again: the 1.5 s SuppressionDuration never ran out.
        if (buff is not null && !buff.Shortened && trip is { } time && !move.Completed && buff.State.ExpiresAt < time + TripStep &&
            buff.State.ExpiresAt >= time - TripStep && !(_host.HasFrame && _host.Frame.Hook == "BuffOnUpdateActions"))
            buff.State.ExpiresAt = time + TripStep;
        _moves.Add(new BbScriptMove
        {
            Unit = unit, Move = move, Buff = buff, Postponed = postponed,
            // The buffs the unit held before the running hook: a buff the same hook added before its BBMove (Riven's Broken Wings adds RivenTriCleaveDamage, then moves)
            // is as fresh as one added after it (LifeTime 0, no update has run), so it is not "held" and hears the move too.
            // Not conditioned on the move having a length: a request the landing search brings back to the origin (Broken Wings
            // aimed into a wall the dash cannot pass) is still a move that ended, and the buffs the hook added are still the ones
            // whose BuffOnMoveSuccess draws the ground effect and whose BuffOnMoveEnd retires the clip. Without this the wall case
            // ran no hook at all, so the detonate was never drawn (owner finding 16, 20 October 2026: "near a wall the ground effect
            // is sometimes dropped").
            HeldBefore = buff is null ? _host.Buffs.OfUnit(unit).Where(item => item.LifeTime > 0.0).ToHashSet() : null,
        });
        return move;
    }

    /// <summary>A new move replaces the unit's current one: the old one is over, and failed.</summary>
    private void Supersede(Entity unit)
    {
        foreach (var old in _moves.Where(item => ReferenceEquals(item.Unit, unit)).ToArray())
        {
            _moves.Remove(old);
            old.Move.Completed = true;
            old.Move.Succeeded = false;
            Finish(old, attached: false);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Teleports
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBTeleportToPosition</c> / <c>BBTeleportToKeyLocation</c>: the unit appears at the point at once (the client body
    /// sets the actor's position; the server sends the teleport packet with the next teleport id). A requested point is corrected the way
    /// every shipped teleport (Flash, Arcane Shift, Shaco's Deceive) already is: <see cref="SpellSystem.CorrectTeleportDestination"/>.
    /// A move inside <c>ClientTeleportDeadband</c> is staged through an intermediate point and takes two teleport ids
    /// (<see cref="SpellSystem.ShortTeleportStaging"/>).
    /// </summary>
    public void Teleport(Entity unit, Position2 requested, bool correct)
    {
        var spells = _host.Spells;
        var launch = unit.Position;
        var landing = correct ? spells.CorrectTeleportDestination(unit, requested) : spells.Movement.ClientAnchor(requested);
        unit.ForcedMove = null;
        unit.Position = landing;
        unit.ForcedMoveGrace = 0.5;
        unit.Waypoints.Clear();
        unit.Target = null;
        unit.AttackTarget = null;
        unit.AnchorPath = [landing];
        unit.LastAnchorTime = spells.CurrentTime;
        var staging = spells.ShortTeleportStaging(unit, launch, landing);
        unit.TeleportId = unchecked((byte)(unit.TeleportId + (staging is null ? 1 : 2)));
        if (spells.Effects.IsReferenceReady(unit))
            spells.Effects.SendTeleport(unit, landing, unit.TeleportId, spells.World.Clock.SyncId(), staging);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Stopping and following up
    // ------------------------------------------------------------------------------------------------------------

    /// <summary><c>BBStopMove</c> / <c>BBStopMoveBlock</c>: the unit's displacement ends where it stands (a failed move).</summary>
    public void Stop(Entity unit)
    {
        var tracked = false;
        foreach (var entry in _moves.Where(item => ReferenceEquals(item.Unit, unit)))
        {
            tracked = true;
            entry.Move.Completed = true;
            entry.Move.Succeeded = false;
        }
        if (tracked || unit.ForcedMove is null) return;
        // A displacement another service started: release it and tell the client to stand.
        unit.ForcedMove = null;
        var spells = _host.Spells;
        // The stand is a one-point path, installed on receipt (the client's follow-path routine): seated on its anchor at once.
        if (unit.Alive && spells.Effects.IsReferenceReady(unit))
            spells.Effects.SendWaypoints(unit, spells.Movement.InstallStop(unit), spells.World.Clock.SyncId());
    }

    /// <summary>
    /// Once per spell step (<c>IChampionSpellModule.TickSharedProjectilePhase</c>, after the movement phase of the step): finishes the
    /// displacements that are over and hands postponed hero orders back.
    /// </summary>
    public void Tick()
    {
        if (_moves.Count > 0)
        {
            foreach (var entry in _moves.ToArray())
                if (entry.Buff is { } buff && entry.Unit.Alive && _tracking.Contains(entry.Unit) &&
                    buff.Script.Event("BuffOnCollision") is not null && ReferenceEquals(entry.Unit.ForcedMove, entry.Move))
                    ReportCollisions(entry, buff);
            foreach (var entry in _moves.ToArray())
            {
                var attached = ReferenceEquals(entry.Unit.ForcedMove, entry.Move);
                if (attached && !entry.Move.Completed && entry.Unit.Alive) continue;
                // A hook of an earlier entry may already have ended this one (a new move superseded it).
                if (_moves.Remove(entry)) Finish(entry, attached);
            }
        }
        if (_resumes.Count > 0) ResumeOrders();
    }

    /// <summary>
    /// <c>BuffOnCollision</c> of a tracking script (<c>BBStartTrackingCollisions</c>): every unit whose body the moving unit touches, once
    /// per move (Renekton's, Shen's, Gragas's and Tryndamere's dashes strike what they pass). Reconstruction: the hook exists only in the
    /// scripts; the contact test is the sum of the two movement radii at the end of the step, as the client's actor collision pairs them.
    /// Reconstruction (Tryndamere, 19 October 2026): only units of another team are reported. Four of the five shipped scripts with the hook (Renekton's two
    /// dashes, Shen's and Gragas's) open with <c>CO_DIFFERENT_TEAM</c>, and Spinning Slash's does not: with allies reported its dash would damage them and give
    /// Tryndamere rage for each, which no retail description of the spell has.
    /// </summary>
    private void ReportCollisions(BbScriptMove entry, BbBuffInstance buff)
    {
        var unit = entry.Unit;
        foreach (var other in _host.Spells.World.Entities.Values.ToArray())
        {
            if (ReferenceEquals(other, unit) || other.Team == unit.Team || !other.Alive || other.IsWard || other.Radius <= 0.0 ||
                other.UnitType is not (UnitType.Hero or UnitType.Minion or UnitType.Monster or UnitType.Turret or UnitType.Structure)) continue;
            if (BbSpellHost.Distance(unit.Position, other.Position) > unit.Radius + other.Radius) continue;
            if (entry.Collided.Add(other.NetId)) _host.Router.RunCollision(buff, other);
        }
    }

    private void Finish(BbScriptMove entry, bool attached)
    {
        var unit = entry.Unit;
        var move = entry.Move;
        var spells = _host.Spells;
        // Reconstruction (batch 275): a first-collision move stopped short by a static body completed its path, so a unit that does not track
        // collisions hears BuffOnMoveSuccess (the shipped Poppy push tests the distance itself); a tracking unit keeps the terrain event below.
        var success = move.Completed && (move.Succeeded || (move.StoppedShort && !_tracking.Contains(unit)));
        // A move that stopped short of its request by a wall: only a unit whose script tracks collisions (Condemn) is told.
        var terrain = move.Completed && !move.Succeeded && move.FirstCollision && _tracking.Contains(unit);
        if (attached)
        {
            unit.ForcedMove = null;
            if (unit.Alive && spells.Effects.IsReferenceReady(unit))
                spells.Effects.SendWaypoints(unit, spells.Movement.InstallStop(unit), spells.World.Clock.SyncId());
        }
        if (entry.Buff is { } buff) _host.Router.RunMoveHooks(buff, success, terrain);
        else if (entry.HeldBefore is { } held)
        {
            // A move a spell's own hook started (Amumu's Bandage Toss moves the caster and adds the buff after it; Tristana's Rocket Jump, Pantheon's
            // leap, Riven's Feint, Ashe's hawk and Alistar's Headbutt do the same): its BuffOnMove* hooks belong to the script buffs the hook left on
            // the unit, which retail's buff manager walks when the move is over. Reconstruction: only the buffs added since the move began, so a
            // long-lived buff of the unit does not hear a move it had nothing to do with.
            foreach (var added in _host.Buffs.OfUnit(unit).Where(item => !held.Contains(item) &&
                         (item.Script.Event("BuffOnMoveSuccess") is not null || item.Script.Event("BuffOnMoveFailure") is not null ||
                          item.Script.Event("BuffOnMoveEnd") is not null)))
                _host.Router.RunMoveHooks(added, success, terrain);
        }
        // A hero's action word keeps the move and attack bits cleared while a displacement runs (HeroStatsService.Snapshot), and the lock
        // buff of a knock-up ends before the move does (PowerFistSlow: 0.5 s against about 0.9 s). The landing publishes it at once, after
        // the end hooks (the pull's buff leaves in them), instead of leaving the hero stuck until the periodic refresh (playtest #5,
        // 25 September 2026: the hand-written Power Fist did the same). Heroes only: a minion's stat template would reset its health.
        if (attached && unit.Alive && unit.UnitType == UnitType.Hero && spells.Effects.IsReferenceReady(unit)) spells.Effects.SendStats(unit);
        // The hero walks on with the order the move postponed, unless it ended by a wall the script reacts to or the player ordered anew.
        if (attached && unit.Alive && !terrain && entry.Postponed is { } order)
            _resumes.Add((unit, order, spells.CurrentTime + ResumeWindow));
    }

    private void ResumeOrders()
    {
        var spells = _host.Spells;
        foreach (var item in _resumes.ToArray())
        {
            var (unit, order, until) = item;
            if (!unit.Alive || spells.CurrentTime > until || PostponedHeroOrders.HasNewerOrder(unit) ||
                unit.HeroAutoAcquire != order.AutoAcquire)
            {
                _resumes.Remove(item);
                continue;
            }
            // A control the move left behind (Condemn's 0.25 s stun) holds the order back until it ends.
            if (unit.ForcedMove is not null || !CharacterState.Of(unit).CanMove) continue;
            _resumes.Remove(item);
            PostponedHeroOrders.Resume(spells, unit, order);
        }
    }
}
