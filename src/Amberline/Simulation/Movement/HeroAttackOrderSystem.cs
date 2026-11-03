using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Movement;

/// <summary>
/// The engine half a champion's orders need: the movement, the swing and the cast plumbing
/// <c>Hero.luaobj</c> asks for. The decisions live in the chunk — <c>OnOrder</c>,
/// <c>OnTargetLost</c> and its 0.2 s <c>TimerDistanceScan</c>/<c>TimerCheckAttack</c> — and run on
/// the AI host (<see cref="Host"/>); what stays here is everything that talks to the client and the
/// combat tick, so the wire is the one the hand-written hero machine sent. Batch 163 deleted that
/// machine's decisions (the scan queue's Update/UpdatePursuit/UpdateIdle/UpdateHold) and kept this.
/// </summary>
public sealed class HeroAttackOrderSystem
{
    public const double ScanSeconds = 0.2;
    /// <summary>
    /// Reconstruction: the native chase's pacing is not recovered (server code). This build stops starting new heroes once a step has spent this many search
    /// iterations of the client planner (about 20 ms; one search is capped at 800) and resumes with
    /// the next hero on the following step. Whether retail's units chase on their own client-side AI is not recovered; here
    /// all ten heroes share one server step, so the work is spread instead of spiking.
    /// </summary>
    internal const long ScanWorkBudget = 2400;

    /// <summary>AiGlobals' order ids, the ones Hero.txt's OnOrder switches on.</summary>
    internal const double OrderAttackMove = 200.0;
    internal const double OrderAttackTo = 201.0;
    internal const double OrderMoveTo = 202.0;

    private static readonly uint AcquisitionKey = InibinReader.KeyHash("Data*AcquisitionRange");
    private readonly WorldState world;
    private readonly MovementSystem movement;
    private readonly CombatService combat;
    private readonly ISpellEffectSink effects;
    private readonly GameData data;
    private readonly Func<Entity, Entity, bool> visible;
    private readonly FidelityTrace? fidelityTrace;
    private double _untilScan = ScanSeconds;
    private double _time;
    private readonly Queue<uint> _scanQueue = new();
    /// <summary>The seconds of Recall a hero has left (the spell system's answer, set by <c>MatchScriptSeams.AttachHeroOrders</c>); no recall by default.</summary>
    internal Func<Entity, double> RecallRemainingOf { get; set; } = _ => 0.0;
    private bool _scanSpilled;

    public HeroAttackOrderSystem(WorldState world, MovementSystem movement, CombatService combat,
        ISpellEffectSink effects, GameData data, Func<Entity, Entity, bool> visible,
        ISpellBuffService buffs, IRandomSource? random = null,
        FidelityTrace? fidelityTrace = null)
    {
        this.world = world;
        this.movement = movement;
        this.combat = combat;
        this.effects = effects;
        this.data = data;
        this.visible = visible;
        this.fidelityTrace = fidelityTrace;
        Engine = new HeroLuaAiEngine(world, this, combat, data, buffs, visible, random);
        Host = new LuaAiService(world, Engine);
    }

    /// <summary>The engine half the natives are bound to.</summary>
    public HeroLuaAiEngine Engine { get; }

    /// <summary>The script half: one <c>Hero.luaobj</c> instance per champion.</summary>
    public LuaAiService Host { get; }

    /// <summary>Whether an explicit order is being dispatched right now.</summary>
    internal bool InsideOrder { get; private set; }

    /// <summary>The clock the engine's timers run on.</summary>
    internal double Time => _time;

    /// <summary>
    /// An attack-move click (wire order 7). The script decides whether to take the enemy its
    /// <c>FindTargetInAcR</c> finds now or to walk the click first (Hero.txt OnOrder proto 0.2
    /// pc 0055-0073); the goal the player ordered is staged either way, because the postponed-order
    /// and fidelity paths read it and the attack-move scan's arrival test answers from it.
    /// </summary>
    public void StartAttackMove(Entity hero, Position2 destination)
    {
        hero.HeroAutoAcquire = true;
        hero.HeroHold = false;
        hero.HeroPursuit = null;
        hero.AttackMoveGoal = destination;
        hero.LastAttackStopPoint = null;
        Engine.StageInPosition(hero, destination);
        InsideOrder = true;
        Host.Order(hero, OrderAttackMove, null);
        InsideOrder = false;
    }

    /// <summary>
    /// An explicit attack (wire order 3). Hero.txt's OnOrder (proto 0.2 pc 0029-0051) takes the
    /// target hard and either swings at once or closes on it.
    /// </summary>
    public void StartAttack(Entity hero, Entity target)
    {
        hero.HeroAutoAcquire = true;
        hero.HeroHold = false;
        hero.AttackMoveGoal = null;
        if (!Host.Drives(hero))
        {
            // A bot seat's hero runs Bot.luaobj and no Hero.luaobj (batch 167), so there is no
            // OnOrder to dispatch and no Hero chunk to answer it. The order the bot natives issue
            // takes the same engine path the chunk's own ATTACKTO branch does (proto 0.2 pc
            // 0029-0051: SetStateAndCloseToTarget(AI_HARDATTACK, target)) — the target, the hard
            // pursuit record and the chase the hand-written StartAttack of batch 160 performed,
            // plus the hero-engine state the shared pursuit scan re-chases on between the bot's
            // own 250 ms updates. The chunk's own order keeps its in-position point and its
            // range test, which an explicit attack has no use for.
            Engine.SetStateAndCloseToTarget(hero, HeroLuaAiEngine.StateHardAttack, target);
            return;
        }
        Engine.StageInPosition(hero, target.Position);
        InsideOrder = true;
        Host.Order(hero, OrderAttackTo, target);
        InsideOrder = false;
    }

    /// <summary>
    /// A move click (wire order 2). Hero.txt's OnOrder (proto 0.2 pc 0074-0083) drops the fight and
    /// walks the point; it never acquires before arrival (TimerDistanceScan only scans an AI_MOVE
    /// hero that has stopped).
    /// </summary>
    public void StartMove(Entity hero, Position2 destination)
    {
        hero.HeroAutoAcquire = true;
        hero.HeroHold = false;
        hero.AttackMoveGoal = null;
        Engine.StageInPosition(hero, destination);
        InsideOrder = true;
        Host.Order(hero, OrderMoveTo, null);
        InsideOrder = false;
    }

    /// <summary>
    /// Reinstates an explicit attack after a displacement or a cast that postponed it. The order is
    /// re-issued to the script (OnOrder's ATTACKTO branch); a target that cannot be seen is walked
    /// to its last known position instead, which is what OnTargetLost does for a visibility loss
    /// (Hero.txt proto 0.3 pc 0017-0020).
    /// </summary>
    public void ResumeExplicitAttack(Entity hero, uint targetNetId, Position2 lastKnownPosition,
        bool autoAcquire)
    {
        hero.HeroAutoAcquire = autoAcquire;
        hero.HeroHold = false;
        hero.AttackMoveGoal = null;
        if (world.Get(targetNetId) is { } target && Eligible(hero, target))
        {
            StartAttack(hero, target);
            return;
        }
        Engine.WalkToLastKnown(hero, targetNetId, lastKnownPosition);
    }

    /// <summary>
    /// Whether the hero can hit an enemy from where it stands, as automatic acquisition would pick
    /// one: what lets a rooted hero take an attack-move order (it fights the enemy in reach and
    /// cannot walk to the order's position).
    /// </summary>
    public bool HasEnemyInReach(Entity hero) => Acquire(hero, reachOnly: true) is not null;

    /// <summary>
    /// Keeps idle acquisition off for <paramref name="seconds"/> of this service's clock, so an
    /// automatic attack cannot replace a cast gesture (playtest B2 AE6).
    /// </summary>
    internal void HoldIdleAttacks(Entity hero, double seconds) =>
        hero.AutoAttackResumeAt = _time + seconds;

    public void Tick(double dt)
    {
        _time += dt;
        Engine.Tick(_time, dt);
        // The script is stepped for every hero this host drives on every step: its timers are its
        // own clock and a hero that waits for a work budget would drift. What the budget below
        // spreads is the native chase. Batch 167: a bot seat's hero is stepped by BotController on
        // the bot host (its own Bot.luaobj instance and timers) and is skipped here — the claim
        // switch would hand it no Hero.luaobj instance either way. The trim loop and the scan
        // below stay shared with it: the bot's SetStateAndCloseToTarget installs the same pursuit
        // record, and the scan's Continue is what keeps the chase alive between the bot's 250 ms
        // updates (master's scan ran its UpdatePursuit for every hero, bots included).
        foreach (var hero in world.Heroes())
            if (Host.Drives(hero)) Host.StepUnit(hero, _time, dt);
        // Trimming keeps a blocked tail beyond attack reach from forcing a new approach angle while
        // the current prefix still works.
        foreach (var hero in world.Heroes())
        {
            if (Blocked(hero) || IsRooted(hero) || hero.HeroHold || hero.AttackTarget is not uint targetId ||
                world.Get(targetId) is not Entity target || !Eligible(hero, target)) continue;
            if (combat.CanAttack(hero, target) || combat.HasCommittedWindup(hero, target))
            {
                StopMovement(hero);
                continue;
            }
            var path = movement.ShortenHeroApproach(hero, target, AttackReach(hero, target));
            if (path is not null && effects.IsReferenceReady(hero))
                effects.SendWaypoints(hero, path, world.Clock.SyncId());
        }
        _untilScan -= dt;
        if (_scanQueue.Count == 0)
        {
            if (_untilScan > 1e-9) return;
            _untilScan = ScanSeconds;
            foreach (var hero in world.Heroes()) _scanQueue.Enqueue(hero.NetId);
        }
        var planningWorkBefore = movement.ClientPlannerWork;
        while (_scanQueue.TryDequeue(out var scannedNetId))
        {
            if (world.Get(scannedNetId) is not { UnitType: UnitType.Hero, OwnerNetId: 0 } hero) continue;
            if (!hero.Alive)
            {
                hero.AttackMoveGoal = null;
                hero.HeroPursuit = null;
                continue;
            }
            Engine.Continue(hero);
            if (movement.ClientPlannerWork - planningWorkBefore < ScanWorkBudget) continue;
            _scanSpilled = _scanQueue.Count > 0;
            return;
        }
        // A pass that spilled over several steps restarts its period when it ends, so a busy wave
        // stretches the period instead of running passes back to back.
        if (_scanSpilled) _untilScan = ScanSeconds;
        _scanSpilled = false;
    }

    // ---- plumbing the engine half calls ----

    /// <summary>
    /// The native half of <c>SetStateAndCloseToTarget</c>: close on the target until the combat
    /// tick can swing at it. Playtest B3 #4 (26 Sept 2026): the route is to the body and is
    /// re-planned while the target moves — an endpoint tied to its scan-time position goes stale.
    /// </summary>
    internal void Chase(Entity hero, Entity target, bool forceStop = false)
    {
        // Batch 406 follow-up: a channelling hero does not walk on its own. A held attack order (or the idle scan) that closes on the foe while
        // the channel runs would move the caster, and BbScriptChannels.Tick reads that move as a Move stop (the channel ends early). The approach
        // waits until the channel ends. Reconstruction: the shipped files do not say whether retail's channelled champion walks for its held attack.
        if (hero.ActiveScriptChannels > 0) return;
        var distance = Math.Sqrt(DistanceSquared(hero.Position, target.Position));
        var targetMoving = HasLiveRoute(target);
        // Admission and pursuit share the gameplay-radius boundary; a committed swing keeps its
        // separate cancellation leash.
        if (combat.CanAttack(hero, target) || combat.HasCommittedWindup(hero, target))
        {
            // An attack-move's opening engage corrects the walk the client predicted for the ground
            // order with a stop path (the retired machine's Update(hero, starting: true) →
            // Chase(hero, target, starting) → StopMovement(forceStop: true), and the goal the
            // dispatch staged is what marks that opening). An explicit ATTACK_TO and every later
            // scan promised the client no ground walk and stop quietly.
            StopMovement(hero, forceStop || (InsideOrder && hero.AttackMoveGoal is not null));
            return;
        }
        if (IsRooted(hero)) return;
        // A target can be reported at the hero's current cell after a crowd collision or a
        // client-grid snap. Avoid manufacturing NaN movement vectors; the combat range check above
        // remains the authority for a legal attack.
        if (distance <= 1e-9)
        {
            StopMovement(hero, force: true);
            return;
        }
        var reach = AttackReach(hero, target);
        // Retain only the actual live destination: a route that already ends inside reach is kept.
        IReadOnlyList<Position2> livePath = hero.Waypoints.Count == 1
            ? new[] { hero.Position, hero.Waypoints[0] }
            : hero.Waypoints;
        if (!targetMoving && HasLiveRoute(hero) && ApproachPathUsable(livePath, hero, target, reach) &&
            DistanceSquared(hero.Position, hero.Waypoints[^1]) > 9.0 &&
            hero.StuckElapsed < 0.5)
            return;
        InterruptWindup(hero, "chase-out-of-range", target);
        // The route the stock client plans for an AttackTo: a path to the target's position (PlanChase).
        var plan = movement.PlanChase(hero, target, unit => visible(hero, unit));
        var path = Move(hero, plan.Goal, plan);
        // A one-point route is a stationary anchor after a blocked order. Do not latch it; the next
        // pursuit scan must try again.
        var usable = ApproachPathUsable(path, hero, target, reach);
        movement.CountChasePlan(usable);
        hero.LastAttackStopPoint = usable ? path[^1] : null;
    }

    /// <summary>
    /// The walk half of <c>SetStateAndMoveInPos</c> and of the last-known walk. An order walk (the
    /// player's click) is annotated in the movement trace the way the hand-written dispatch did;
    /// a chase or a loss walk is not.
    /// </summary>
    internal IReadOnlyList<Position2> Move(Entity hero, Position2 destination,
        MovementSystem.MovePlan? plan = null, bool orderWalk = false)
    {
        var goal = hero.AttackMoveGoal;
        var pursuit = hero.HeroPursuit;
        var path = plan is null
            ? movement.SetMoveOrder(hero.NetId, destination, unit => visible(hero, unit))
            : movement.CommitMoveOrder(hero, plan);
        // Keep the requested goal after a stationary anchor. Treating the anchor as arrival
        // permanently drops an attack-move order behind a crowded wave. The goal becomes the
        // reachable endpoint of the path so the script's 100-unit arrival test can finish an
        // attack-move whose click landed inside a wall (Hero.txt TimerDistanceScan pc 0074-0085).
        hero.AttackMoveGoal = destination == goal && path.Count > 1 ? path[^1] : goal;
        hero.HeroPursuit = pursuit is HeroPursuitState last &&
            hero.AttackTarget is null && destination == last.LastKnownPosition && path.Count > 1
                ? last with { LastKnownPosition = path[^1] } : pursuit;
        if (effects.IsReferenceReady(hero))
        {
            // No route is a stop, whose one-point path is installed on receipt: seated at once.
            var route = path.Count > 0 ? path : movement.InstallStop(hero);
            effects.SendWaypoints(hero, route, world.Clock.SyncId());
            if (orderWalk)
                NoteWaypointIntent?.Invoke(hero, route,
                    path.Count > 0 ? "hero-order" : "hero-order-wait");
        }
        return path;
    }

    /// <summary>The movement trace's waypoint-intent sink, so a script-driven walk is annotated.</summary>
    internal Action<Entity, IReadOnlyList<Position2>, string>? NoteWaypointIntent { get; set; }

    internal void StopMovement(Entity hero, bool force = false)
    {
        var moving = hero.Waypoints.Count != 0 || hero.Target is not null;
        hero.Waypoints.Clear();
        hero.Target = null;
        // The stop's one-point path is installed on receipt: stopped and seated on the anchor at once.
        if ((moving || force) && effects.IsReferenceReady(hero))
            effects.SendWaypoints(hero, movement.InstallStop(hero), world.Clock.SyncId());
    }

    /// <summary>
    /// Drops an unlaunched basic attack of this hero and tells the client its swing is over (0x39),
    /// but only when a swing was actually cut — a repeated stop costs no packet.
    /// </summary>
    internal void InterruptWindup(Entity hero, string reason, Entity? target = null)
    {
        var pending = fidelityTrace is null ? Array.Empty<PendingHit>() : combat.PendingHits
            .Where(hit => hit.SourceNetId == hero.NetId &&
                hit.SourceKind == DamageSourceKind.Basic &&
                hit.WindupRemaining > 0.0)
            .ToArray();
        if (combat.InterruptBasicAttack(hero) > 0 && effects.IsReferenceReady(hero))
            effects.SendStopAttack(hero);
        if (fidelityTrace is null || pending.Length == 0) return;
        foreach (var hit in pending)
        {
            var liveTarget = world.Get(hit.TargetNetId) ??
                (target?.NetId == hit.TargetNetId ? target : null);
            fidelityTrace.NoteHeroAttack(hero.NetId, hit.TargetNetId,
                hero.Position, liveTarget?.Position, reason, 0,
                hero.AttackRange, hit.WindupRemaining,
                liveTarget is not null && combat.CanAttack(hero, liveTarget),
                liveTarget is not null && combat.InCommittedAttackRange(hero, liveTarget));
        }
    }

    /// <summary>
    /// Reconstruction: the fight a walk leaves behind. Hero.txt's <c>SetStateAndMoveInPos</c> is a walk
    /// order (its native is server code, not recovered) and the walk stops the swing as part of walking (the 0x39 actor stop precedes the
    /// replacement 0xC1 route); a walk order drops the target with it, which is why an ordinary
    /// move never acquires before arrival.
    /// </summary>
    internal void LeaveFight(Entity hero)
    {
        InterruptWindup(hero, "hero-leave-fight");
        Engine.SetTarget(hero, null);
    }

    /// <summary>
    /// Reconstruction: the walk half of the native <c>SetStateAndMove</c> (server code, not recovered), which Hero.txt names only
    /// from <c>OnFearBegin</c> and <c>TimerFeared</c> (always with <c>AI_FEARED</c>). Retail's fear
    /// owners turn the wander point into a displacement (not recovered here); this build's stand-in is the forced
    /// <c>Fear</c> move the other fear owners use (first collision landing, one short segment, the
    /// client walked along).
    /// </summary>
    internal void WanderFeared(Entity hero, Position2 destination)
    {
        // The pulse's own deadline, in the record the shared one-second wander kept before
        // Hero.luaobj walked a champion itself (ShacoBoxService.TickFears). OnFearBegin (proto 0.6
        // pc 0019-0021) re-arms OnInit's 1.0 s TimerFeared (proto 0.1 pc 0015-0022) and every pulse
        // of it publishes the next one here, so an owner of the route (a knockback that took it)
        // can see when the fear is due again instead of reading the application time.
        var fear = hero.Buffs.Where(buff => buff.Type == BuffType.Fear || buff.Name == "Fear" ||
            buff.Payload.FearOrigin is not null).LastOrDefault();
        if (fear is not null) fear.Payload.NextWander = _time + 1.0;
        if (BuffLifecycle.BlocksFearLocomotion(hero)) return;
        // Hero.txt's TimerFeared re-wanders every second for the whole fear. The Fear segment of the last pulse stays
        // attached after it completes (only the Fear buff's end clears it), so it is replaced here as the minion and
        // Shaco-box owners replace theirs; any other forced move (a knockback) still holds the hero.
        if (hero.ForcedMove is { Effect: not "Fear" }) return;
        if (hero.Speed <= 0.0) return;
        var duration = Math.Min(Math.Max(0.0, fear?.ExpiresAt ?? 1.0), 1.0);
        var route = movement.StartFearWander(hero, destination, duration, fear?.CasterNetId ?? 0u);
        if (effects.IsReferenceReady(hero))
            effects.SendWaypoints(hero, route, world.Clock.SyncId());
    }

    /// <summary>Whether the hero has a live route left (<c>IsMovementStopped</c> is its negation).</summary>
    internal bool HasLiveRoute(Entity unit) => unit.Waypoints.Count > 0 &&
        (unit.Waypoints.Count > 1 || DistanceSquared(unit.Position, unit.Waypoints[0]) > 9.0);

    /// <summary>
    /// Whether an uncancellable cast windup, a recall, a script channel or a forced move owns the hero. A running channel holds the hero's
    /// automatic attacks for its whole length (owner hand-test, 26 October 2026: the Galio R lock and the Katarina, Malzahar and Nunu channels
    /// were cut by the hero's own swing); <see cref="Entity.ActiveScriptChannels"/> counts them.
    /// </summary>
    internal bool Blocked(Entity hero) => !hero.Alive ||
        hero.ForcedMove is not null || RecallRemainingOf(hero) > 0 || hero.ActiveScriptChannels > 0 ||
        hero.PendingCast is not null || SpellWindup.IsWindingUp(hero) ||
        hero.ChampionState?.BlocksActions == true || hero.AnyBuffOrStatus(buff =>
            buff.DisablesActions);

    internal bool IsRooted(Entity hero) => hero.AnyBuffOrStatus(buff =>
        buff.Type == BuffType.Snare || buff.Payload.MovementDisabled);

    internal bool Eligible(Entity hero, Entity target) =>
        target.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Monster or UnitType.Turret or UnitType.Structure &&
        WorldState.IsEnemy(hero, target) && combat.IsTargetable(target, hero) && visible(hero, target);

    internal MovementSystem.MovePlan ApproachWithin(Entity hero, Entity target,
        double stop, double reach) =>
        movement.ApproachWithin(hero, target, stop, reach, unit => visible(hero, unit));

    /// <summary>
    /// Hero.lua's <c>FindTargetInAcR</c> body: the nearest eligible enemy by centre distance inside
    /// the champion's acquisition range. Reconstruction: the native's filter and priority are in no
    /// binary we hold (the 126 client has no selector), so this server's reading stands; the range
    /// itself is shipped data. <paramref name="reachOnly"/> narrows it to the enemies the hero can
    /// hit where it stands (a held or rooted hero cannot walk to the others).
    /// </summary>
    internal Entity? Acquire(Entity hero, bool reachOnly = false)
    {
        // A hero's automatic acquisition (idle, attack-move, hold) takes pets (Tibbers, Shaco's
        // clone, Yorick's ghouls and revenant) but never a ward (owner decision, 18 Oct 2026):
        // CombatService.IsAutoAcquirable, which every pet's scan asks too. A ward is still
        // attackable by an explicit order.
        var raw = data.Champion(hero.StatCharacterName).Raw;
        // The stock character loader reads AcquisitionRange with the default 750.0
        // (a constant); nine of the ten human-form files specify it explicitly, and both
        // Nidalee forms take the default. IncAcquisitionRangeMod is called only by the
        // Riven and Sona scripts; a script buff's payload carries what it adds (below).
        var range = raw.TryGetValue(AcquisitionKey, out var value) ? value.AsDouble() : 750.0;
        // IncAcquisitionRangeMod adds to it: Sona's Power Chord takes 900 off while the
        // charge is ready.
        range = Math.Max(0.0, range + hero.Buffs.Sum(buff => buff.Payload.AcquisitionRange));
        var inPlace = reachOnly || IsRooted(hero);
        return world.Entities.Values.Where(unit => Eligible(hero, unit) && CombatService.IsAutoAcquirable(unit) &&
                (!inPlace || combat.CanAttack(hero, unit)) &&
                DistanceSquared(hero.Position, unit.Position) <= range * range)
            .OrderBy(unit => DistanceSquared(hero.Position, unit.Position))
            .FirstOrDefault();
    }

    private bool ApproachPathUsable(IReadOnlyList<Position2> path,
        Entity hero, Entity target, double reach) =>
        movement.ApproachPathUsable(path, hero, target, reach);

    private static double AttackReach(Entity hero, Entity target) =>
        CombatService.AttackReach(hero, target);

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z);
}
