using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Minions;

public sealed partial class MinionAiSystem
{
    /// <summary>
    /// Reconstruction: FindTargetInAcR is native and unrecovered. The period-informed
    /// priority ladder, acquisition geometry and stable enumeration ties are substitutes;
    /// the ai_Target* cost inside one ladder tier is <see cref="CheapestTarget"/>.
    /// </summary>
    public Entity? SelectTarget(Entity minion, double? radius = null)
    {
        ArgumentNullException.ThrowIfNull(minion);
        if (!IsLiveMinion(minion)) return null;
        var baseRange = radius ?? Math.Max(minion.AcquisitionRange, minion.AttackRange);
        var source = radius is null && _tickIndex is not null && double.IsFinite(baseRange)
            ? _world.Near(_tickIndex, minion.Position, baseRange + MapDefinitions.NexusRadius)
            : _world.Entities.Values;
        var enemies = source.Where(candidate =>
            candidate.NetId != minion.NetId && candidate.Alive &&
            !IsIgnored(minion, candidate) &&
            WorldState.IsEnemy(minion, candidate) && !candidate.Protected &&
            (candidate.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Turret or UnitType.Structure ||
             candidate.AttackTarget == minion.NetId) &&
            _combat.IsTargetable(candidate, minion) &&
            InAcquisitionRange(minion, candidate, baseRange) &&
            HasBrushVision(minion.Team, candidate)).ToArray();
        if (enemies.Length == 0) return null;

        var ranked = enemies.Select(entity => (Entity: entity, Tier: TargetPriority(_world, minion, entity))).ToArray();
        var best = ranked.Min(item => item.Tier);
        var tier = ranked.Where(item => item.Tier == best).Select(item => item.Entity).ToArray();
        return tier.Length == 1 ? tier[0] : CheapestTarget(minion, tier);
    }

    /// <summary>
    /// Reconstruction: the cost the native FindTargetInAcR weighs, from the ai_Target* constants that
    /// LEVELS/Map1/Constants.var ships (the client only initialises them; no server code read them before).
    /// Inside one <see cref="TargetPriority"/> tier a unit takes the cheapest candidate,
    /// <c>(RangeFactor*straight + PathFactor*path) * (1 + PerAttacker*min(attackers, MaxNumAttackers))</c>. The
    /// 2011 wiki describes the same in play: wave minions "spread their attacks across the three enemy melee
    /// minions regardless of distance".
    /// Attackers are every live unit currently attacking the candidate: an engaged lane minion, a hero, a turret,
    /// a pet or a camp monster, never the selecting minion itself (the constant reads "attackers already attacking
    /// target"). They are counted when asked, so a minion handled later in the same AI tick sees the picks of the
    /// earlier ones.
    /// ai_TargetMaxNumAttackers saturates the count (its text fits a per-target attacker list of that size). The
    /// other reading, skipping a target that already has that many unless the whole tier is full, was built and
    /// measured: it spread no better (attackers above 5 on a minion target: 4% against 5% saturated, 13% before)
    /// but the unit next to a full target walked a median 428 units to a free one. That doubled the stacked
    /// invariant (53 to 109 episodes in 6 headless seeds x 14 minutes) and, on the build with the client contact
    /// solver, left one minion 71 s on a blocked cell in 12 seeds (saturated: 9 s, before: 8 s).
    /// No term for two constants: ai_MinionTargetingHeroBoost ("distance added to discourage minions attacking
    /// heroes") is already infinite here, because the ladder never puts a hero in a tier with a non-hero (minions
    /// are tier 6, heroes 7); it would apply only if a tier were pooled. ai_TargetDistanceFactorPerNeightbor has
    /// no term because what a "neighbor" of a target is (units around it, or around the selecting unit) is unknown.
    /// </summary>
    private Entity CheapestTarget(Entity minion, Entity[] tier)
    {
        var cost = TargetCost;
        var attackers = new Dictionary<uint, int>();
        foreach (var unit in _world.Entities.Values)
            if (unit.NetId != minion.NetId && unit.Alive && unit.AttackTarget is uint id &&
                (unit.MinionType is null || unit.MinionState is MinionAiState.Engage or MinionAiState.Taunted))
                attackers[id] = attackers.GetValueOrDefault(id) + 1;
        return tier.MinBy(candidate =>
        {
            var straight = Distance(minion.Position, candidate.Position);
            // Reconstruction: the native cost is assumed to weigh the real path length (unrecovered). This emulator runs no path search
            // per candidate per scan; inside the acquisition circle the straight line is the path except
            // across walls, so the path term uses the straight line.
            var path = straight;
            var crowd = Math.Min(attackers.GetValueOrDefault(candidate.NetId), cost.MaxAttackers);
            return (cost.RangeFactor * straight + cost.PathFactor * path) * (1.0 + cost.PerAttacker * crowd);
        })!;
    }

    // Reconstruction: period 12 November 2011 ladder; an attacking turret is help, not a
    // blanket turret-first preference. Include the responder among allies.
    internal static int TargetPriority(WorldState world, Entity minion, Entity candidate, Entity? helpVictim = null)
    {
        var victim = helpVictim ?? (candidate.AttackTarget is uint id ? world.Get(id) : null);
        var ally = victim is { Alive: true } && victim.Team == minion.Team;
        if (ally && victim!.UnitType == UnitType.Hero)
        {
            if (candidate.UnitType == UnitType.Hero) return 0;
            if (candidate.UnitType == UnitType.Minion) return 1;
        }
        if (ally && victim!.UnitType == UnitType.Minion)
        {
            if (candidate.UnitType == UnitType.Minion) return 2;
            if (candidate.UnitType == UnitType.Turret) return 3;
            if (candidate.UnitType == UnitType.Hero) return 4;
        }
        if (candidate.AttackTarget == minion.NetId) return 5;
        return candidate.UnitType switch
        {
            UnitType.Minion => 6,
            UnitType.Hero => 7,
            UnitType.Turret => 8,
            _ => 9,
        };
    }

    // LMIN-01/02: one admission rule for damage nomination and AI consumption.
    internal static bool CanAcceptHelp(WorldState world, Entity minion, Entity candidate,
        int priority, bool includePending = false)
    {
        if (includePending && minion.HelpTarget is uint pendingId && pendingId != candidate.NetId &&
            world.Get(pendingId) is { Alive: true } && minion.HelpPriority <= priority) return false;
        if (minion.AttackTarget is not uint currentId || currentId == candidate.NetId ||
            world.Get(currentId) is not { Alive: true } current) return true;
        var currentPriority = TargetPriority(world, minion, current);
        // Constants.var cfh_Stick protects the accepted priority after Move/Stop.
        if (minion.HelpStickRemaining > 0.0 && minion.HelpStickTarget == currentId)
            currentPriority = Math.Min(currentPriority, minion.HelpStickPriority);
        return priority < currentPriority;
    }

    /// <summary>
    /// Minion.lua 0.2 <c>TimerFindEnemies</c>: the 0.25 s scan's body. The named entry point the
    /// checks and the event callers ask for; the decision itself is the script's, on its own
    /// <c>DELAY_FIND_ENEMIES</c> timer rather than a C# period.
    /// </summary>
    internal void TimerFindEnemies(Entity minion) => Host.InstanceFor(minion)?.Call("TimerFindEnemies");

    private void MoveForward(Entity minion, bool refresh = true)
    {
        if (!refresh) return;
        if (minion.Waypoints.Count > 0 || minion.Target is not null) return;
        // An actual control buff or forced move cancelled the public
        // order, not the wave's remaining strategic lane route: it owns the
        // new one this step would publish too (ControlOwnsMovement).
        if (ControlOwnsMovement(minion)) return;
        if (!minion.LaneDone && minion.InterruptedLaneWaypoints.Count > 0)
        {
            var suffix = minion.InterruptedLaneWaypoints;
            if (DistanceSquared(minion.Position, suffix[^1]) > 9.0)
            {
                var restored = RestoreLaneRoute(minion, suffix);
                if (restored.Count > 1)
                {
                    minion.InterruptedLaneWaypoints.Clear();
                    EmitWaypoints(minion, _movement.InstallPath(minion, restored), "control-release-lane");
                    return;
                }
                // A transient blocked connector must not turn an unfinished
                // lane into a terminus. Retry at the next target scan.
                return;
            }
            minion.InterruptedLaneWaypoints.Clear();
        }
        minion.LaneDone = true;
        if (!SiegeRoutingEnabled) return;
        var siege = SelectStructureTarget(minion);
        if (siege is not null)
        {
            // A siege route is not a lane route. Target loss must re-siege the
            // next lane objective instead of replaying a stale lane tail.
            minion.LaneWaypoints.Clear();
            // The script arms TimerAntiKite on its own acquisitions only (Minion.lua 0.2/0.3),
            // so a siege approach does not: a structure does not kite, and the one-second siege
            // retry below bounds how long the minion keeps asking.
            Acquire(minion, siege);
        }
    }

    /// <summary>
    /// The lane end of a target run that gives up past its lane's last approach. Reconstruction: Minion.lua FindTargetOrMove requests
    /// forward nav; its native selector is unrecovered. Passing the final approach plane spends the saved terminal even after a long chase
    /// into base; the existing siege planner still validates the next structure route. (The parked-minion advance of B4 #10 read the
    /// congestion wait, which nothing has set since batch 271; batch 279 deleted both.)
    /// </summary>
    private void AdvanceLaneEnd(Entity minion)
    {
        if (minion.LaneDone || !SiegeRoutingEnabled ||
            minion.MinionLane is not string lane ||
            !MapDefinitions.LanePaths.TryGetValue(lane, out var spine)) return;
        var retained = minion.LaneWaypoints.Count > 0 ? minion.LaneWaypoints : minion.InterruptedLaneWaypoints;
        if (retained.Count == 0) return;
        var approach = MapDefinitions.LaneSpawnApproaches[lane];
        var endIndex = minion.Team == Team.Order ? spine.Count - 1 - approach.Tail : approach.Head;
        if (Distance(retained[^1], spine[endIndex]) > _navigation.CellSize) return;
        if (!PassedLaneEnd(minion.Team, spine, endIndex, minion.Position)) return;
        ClearMovement(minion, "lane-terminal-advance");
        minion.LaneWaypoints.Clear();
        minion.InterruptedLaneWaypoints.Clear();
        minion.LaneDone = true;
    }

    /// <summary>
    /// Whether <paramref name="position"/> lies past the end of <paramref name="team"/>'s walk of a lane spine (the spine numbered from the
    /// Order side, the team's end at <paramref name="endIndex"/>): beyond the plane through the end across its final leg, and nearer the end
    /// than any earlier leg of the team's walk. Reconstruction, like <see cref="AdvanceLaneEnd"/>. Batch 578: the plane alone read every
    /// lane as heading straight at its end. Map4's L lane doubles back (L019 to L024 hook south-west into the Chaos base), so for an Order
    /// minion the lane from its own spawn to __NAV_L016 lay past that plane: each one that lost a target there was marked done and
    /// siege-routed straight at the next tower, across the jungle path through the bush beside Turret_T2_L_02 (the owner's game of
    /// 1 November). On Summoner's Rift the two tests differ only on ground 2500 units or more from the lane (map4.summoners-rift-lanes-unchanged).
    /// </summary>
    internal static bool PassedLaneEnd(Team team, IReadOnlyList<Position2> spine, int endIndex, Position2 position)
    {
        var step = team == Team.Order ? -1 : 1;
        var end = spine[endIndex];
        var forward = end - spine[endIndex + step];
        var offset = position - end;
        if (offset.X * forward.X + offset.Z * forward.Z < 0.0) return false;
        var toEnd = offset.X * offset.X + offset.Z * offset.Z;
        // The team's earlier legs, from the one before the final leg back to its start.
        for (var index = endIndex + step; index + step >= 0 && index + step < spine.Count; index += step)
            if (DistanceSquaredToSegment(position, spine[index + step], spine[index]) < toEnd) return false;
        return true;
    }

    private static double DistanceSquaredToSegment(Position2 point, Position2 start, Position2 end)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared <= 1e-12 ? 0.0
            : Math.Clamp(((point.X - start.X) * dx + (point.Z - start.Z) * dz) / lengthSquared, 0.0, 1.0);
        var x = start.X + t * dx - point.X;
        var z = start.Z + t * dz - point.Z;
        return x * x + z * z;
    }

    private Entity? SelectStructureTarget(Entity minion)
    {
        // Reconstruction: Lua falls back to SetStateAndMoveToForwardNav;
        // its native post-terminal resolver is unrecovered. Follow our lane's
        // surviving chain, then the shared nexus defenses. Local acquisition
        // above remains free to cross lanes. Batch 556: Map4 names its lane turrets the Summoner's Rift way
        // (Turret_T1_C_06 guards Barracks_T1_L1 with Turret_T1_L_02 in front, Turret_T2_R_01 guards Barracks_T2_R1, ...), so the same tables
        // give its two lanes' chains; the _03 towers it does not build are simply not found.
        var objectives = new List<string>();
        var enemyTeam = MinionDefinitions.Opposing(minion.Team);
        var inhibitor = $"Barracks_T{(enemyTeam == Team.Order ? 1 : 2)}_{minion.MinionLane}1";
        var guard = MapDefinitions.TurretGuardsInhibitor
            .FirstOrDefault(pair => pair.Value == inhibitor).Key;
        for (string? cursor = guard; cursor is not null;)
        {
            objectives.Insert(0, cursor);
            cursor = MapDefinitions.TurretFront.GetValueOrDefault(cursor);
        }
        objectives.Add(inhibitor);
        var selected = _world.Entities.Values
            .Where(candidate => candidate.Alive && !candidate.Protected &&
                candidate.UnitType is UnitType.Turret or UnitType.Structure &&
                WorldState.IsEnemy(minion, candidate) &&
                _combat.IsTargetable(candidate, minion))
            .Select(candidate => (Entity: candidate, Rank: objectives.IndexOf(candidate.Name)))
            .Select(item => (item.Entity, Rank: item.Rank >= 0 ? item.Rank :
                MapDefinitions.TurretTiers.GetValueOrDefault(item.Entity.Name) == TurretTier.Nexus
                    ? objectives.Count : item.Entity.Kind == EntityKind.Nexus ? objectives.Count + 1 : -1))
            .Where(item => item.Rank >= 0)
            .OrderBy(item => item.Rank)
            .ThenBy(item => DistanceSquared(minion.Position, item.Entity.Position))
            .Select(item => item.Entity)
            .FirstOrDefault();
        if (selected is null)
        {
            _lastStop.Remove(minion.NetId);
            _nextSiegeRetry.Remove(minion.NetId);
            return null;
        }

        // The lane route is exhausted, so a pre-placed live structure's map
        // position remains a strategic approach goal even before team sight.
        // Only the ordinary visible-target selector may nominate it for combat.
        if (_combat.IsInAttackRange(minion, selected) &&
            HasBrushVision(minion.Team, selected))
        {
            _nextSiegeRetry.Remove(minion.NetId);
            return selected;
        }

        var destination = selected.Position;
        var sameTarget = _lastStop.TryGetValue(minion.NetId, out var previous) &&
            DistanceSquared(destination, previous.Point) <= 120.0 * 120.0;
        if (_nextSiegeRetry.TryGetValue(minion.NetId, out var retry))
        {
            if (retry.TargetNetId == selected.NetId && _elapsed + 1e-9 < retry.ReadyAt)
                return null;
            if (retry.TargetNetId != selected.NetId)
                _nextSiegeRetry.Remove(minion.NetId);
        }
        if (sameTarget) _lastStop.Remove(minion.NetId);

        var issued = IssueStructureApproach(minion, selected, "siege-move");
        if (issued) _lastStop[minion.NetId] = (destination, selected.Position);
        // Reconstruction: native siege retry cadence is unrecovered; this one-second
        // rate limit bounds repeated emulator path searches at blocked structures.
        // A successful approach and every failed route both wait for the
        // bounded retry window, preventing a 30 Hz order stream at a blocked
        // structure.
        _nextSiegeRetry[minion.NetId] =
            (selected.NetId, _elapsed + MinionDefinitions.SiegeRetryPeriodSeconds);
        return null;
    }

    /// <summary>
    /// Whether the lane planner may be asked again this frame. The target scan is the script's own
    /// timer now; this 250 ms gate is only the movement half's cost bound (the owner rule: minion
    /// decisions must not search thirty times a second at 200+ FPS).
    /// </summary>
    private bool TargetScanReady(Entity minion)
    {
        if (!_nextTargetScan.TryGetValue(minion.NetId, out var deadline))
        {
            _nextTargetScan[minion.NetId] = _elapsed + MinionDefinitions.TargetScanPeriodSeconds;
            return true;
        }
        if (_elapsed + 1e-9 < deadline) return false;
        do deadline += MinionDefinitions.TargetScanPeriodSeconds;
        while (deadline <= _elapsed);
        _nextTargetScan[minion.NetId] = deadline;
        return true;
    }

    private bool HasBrushVision(Team team, Entity target)
        => _canSee?.Invoke(team, target) ?? _vision.HasBrushVision(team, target);

    private static bool IsLiveMinion(Entity entity) =>
        // Batch 451: an Odin wave unit (OdinWaves) has no lane type; it is a live minion of the lane engine all the same.
        entity.UnitType == UnitType.Minion && (entity.MinionType is not null || OdinWaves.ScriptOf(entity) is not null) && entity.Alive;

    private static bool IsActionDisabled(Entity entity) =>
        entity.ChampionState?.BlocksActions == true || entity.AnyBuffOrStatus(buff =>
            buff.DisablesActions);

    // Reconstruction: native FindTargetInAcR range geometry is unrecovered.
    private static bool InAcquisitionRange(Entity minion, Entity target, double radius)
    {
        var reach = radius + Math.Max(0.0, target.Radius);
        return DistanceSquared(minion.Position, target.Position) <= reach * reach;
    }
}
