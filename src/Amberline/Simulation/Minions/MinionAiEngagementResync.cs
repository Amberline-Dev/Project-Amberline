using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Minions;

public sealed partial class MinionAiSystem
{
    /// <summary>
    /// Source-equivalent steady-state engage helper. Keeping target selection
    /// outside this method lets production and seeded collision regressions
    /// share the same plant, overlap, stale-route, and stop-point rules.
    /// </summary>
    private void EngageMove(Entity minion, Entity target, bool refresh = true)
    {
        // A swing planted for the next combat step owns the unit until it lands: pursuit neither
        // re-routes nor cancels it (CombatService.HasCommittedWindup), so nothing is published here
        // and the swing is never cut by a mid-windup order. What is already on the wire stands -- an
        // overlap escape above all. Erasing it is the stop that re-wedges the pair: the client's own
        // auto-repeat plants the next swing on the attack cooldown, this branch then replaced the
        // escape with a stop, and the next escape only went out on the next decision scan (b153,
        // seed 5205: the two alternated once per auto-repeat while the unit never left its seat,
        // 3.8 s of stacked around one turret).
        if (_combat.HasCommittedWindup(minion, target)) return;
        var wasPlanted = minion.Waypoints.Count == 0 && minion.Target is null;
        // Decisions run at the shipped 250 ms cadence. Movement and committed
        // attacks still advance every tick; target motion must not rebuild
        // thirty approach routes per second for every pursuing minion.
        var inPlantBand = WithinStopRange(minion, target) ||
            (wasPlanted && _combat.IsInAttackRange(minion, target));
        if (inPlantBand)
        {
            var declip = FindOverlapPoint(minion, target);
            if (declip is Position2 overlap)
            {
                if (!refresh) return;
                // One repair per seat, not one per plant tick. A completed escape whose point is
                // still the right one has already been walked; re-publishing it every 250 ms buys
                // only wire (each repeat is a WaypointList on every peer). The point is recomputed
                // every tick, so a seat that has moved with the target still re-issues.
                var shouldIssue = !_lastStop.TryGetValue(minion.NetId, out var previous) ||
                    DistanceSquared(overlap, previous.Point) > 4.0 * 4.0;
                if (shouldIssue) IssueOverlapMove(minion, overlap, "overlap-declip");
                return;
            }

            _lastStop.Remove(minion.NetId);
            if (minion.Waypoints.Count > 0 || minion.Target is not null)
                ClearMovement(minion, "plant-stop");
            else
                minion.Target = null;
            return;
        }

        if (!refresh) return;
        // Reconstruction: native CloseToTarget maintenance is unrecovered. We retry a stale planted
        // destination after a 0.5-second anchor-age gate and cancel the old attack lifecycle
        // before publishing movement.
        var stale = wasPlanted && !_combat.IsInAttackRange(minion, target) &&
                    _elapsed - minion.LastAnchorTime >= ReplantMinimumGapSeconds;
        // Three-unit replant margin; start admission itself has no slack.
        var reach = CombatService.AttackReach(minion, target) - 3.0;
        // Reconstruction: the chase runs at the edge of the target's body (AttackSeatRadius), never
        // into it, and the attack-reach band above is what seats the pursuer -- where its attack
        // reaches edge to edge. A route that runs to the target's centre ends inside the target's own
        // body, where the client's arrival seats the mover; the contact retarget then plants the pair
        // inside each other and the pair has to be walked apart by the overlap declip (b153, seed 5201:
        // one Order/Chaos pair held 11 s at 19.8 units). A route that ends at the attack seat itself
        // is the opposite failure: it is a fixed point a receding target leaves behind, so the client
        // seats the mover just outside the band and Minion.lua 0.2's next DELAY_FIND_ENEMIES scan
        // finds the target outside AttackReach, so the swing never starts (b153, minions-stale-parking).
        // Reconstruction: the route is re-issued once its target has travelled a whole stop band since
        // the route went out. Inside that travel the old seat still leads the pursuer into the same
        // band and still stands outside the target's own body, so re-planning buys only wire (the
        // source's own CloseToTarget cadence is unrecovered; minions-moving-target-cost prices it).
        // The deadband is the target's travel, not the seat's distance to it: a body-edge seat sits
        // 94 units from the target where the centre seat sat 167, so a distance rule re-plans the
        // deeper -- and the only seat that cannot end inside a body -- almost twice as often as the
        // centre seat it replaced (b153, seed 5201: 4065 "target-chase" routes, every one of them a
        // mid-route re-issue).
        var stop = StopPoint(minion, target);
        var margin = reach * reach;
        var need = !_lastStop.TryGetValue(minion.NetId, out var prior);
        var needReason = "target-chase-noprior";
        if (!need && DistanceSquared(prior.TargetAt, target.Position) > margin)
        {
            need = true;
            needReason = "target-chase-travel";
        }
        if (!need && stale) { need = true; needReason = "target-chase-stale"; }
        if (!need) return;

        // The client's attack state starts the next attack on its own, and every cast start is a StopActor that erases
        // the path being followed, so a planted attacker's attack must end before it is replanted. StopAttack sends
        // nothing for a unit that has not attacked (the stop was 3.1 packets per minion-minute before the K7 gate,
        // seed 1, 14 min, 10 bots).
        if (wasPlanted) StopAttack(minion, "stale-plant");
        // Reconstruction: native CloseToTarget is unrecovered, so this stop point is the emulator's choice.
        // The stock client rejects every frame whose destination cell is blocked, so a stop point inside
        // a turret's footprint is no seat: the unit would be seated on a blocked cell and freeze there
        // (seed 1 and seed 2 of the headless matches, 8.8 s and 40.4 s). Plan the approach with the
        // client movement view, which searches mirrored firing angles, as for a structure target.
        // Reconstruction: a chase seat has no straight-line fallback. The client is the only
        // thing that can accept such a route, and taking it skips the mirrored-angle search the
        // engine decides a completed target-path failure from: the seat is now the edge of the
        // target's body, which is often on this side of the wall the target itself is behind, so
        // the fallback line is clear while every real route is not (b153, seed-geometry pair at
        // (6999,6757)/(6913,6435): one planner call, no angle tried, no failure declared). When
        // the plan fails, the structure approach below exhausts every firing angle and only then
        // raises OnPathToTargetBlocked.
        if (target.UnitType is UnitType.Turret or UnitType.Structure || !IsStandable(stop))
            IssueStructureApproach(minion, target, needReason);
        else if (IssueMove(minion, stop, needReason, allowFallback: false))
            _lastStop[minion.NetId] = (stop, target.Position);
        else if (minion.ForcedMove is null && !minion.AnyBuffOrStatus(buff =>
                     buff.Type == BuffType.Snare || buff.Payload.MovementDisabled))
            IssueStructureApproach(minion, target, "target-chase-fallback");
    }

    private bool IssueStructureApproach(Entity minion, Entity target, string reason)
    {
        var reach = CombatService.AttackReach(minion, target);
        var plan = _movement.ApproachWithin(minion, target, Math.Max(0.0, reach - 6.0), reach,
            out var noTerrainRoute, aimAngle: SeatAngle(minion, target));
        // Snapping can leave a route outside attack reach or at the current
        // position. Never publish that as a successful chase (playtest B4 #15).
        if (plan.Status != MovementSystem.MoveStatus.Moving ||
            DistanceSquared(minion.Position, plan.Path[^1]) <= 1.0 ||
            !_movement.ApproachPathUsable(plan.Path, minion, target, reach))
        {
            // Reconstruction: dispatch native OnPathToTargetBlocked only after
            // exhausting approach angles/fallbacks, never for contact or a live order.
            // A failed endpoint/follower validation is not proof of disconnection.
            // The planner must exhaust every angle without finding any terrain path.
            // An invalid origin still needs local recovery, not target invalidation.
            if (noTerrainRoute && _navigation.IsMovementWalkable(minion.Position) &&
                minion.AttackTarget == target.NetId &&
                minion.ForcedMove is null && !minion.AnyBuffOrStatus(buff =>
                    buff.Type == BuffType.Snare || buff.Payload.MovementDisabled))
                OnPathToTargetBlocked(minion);
            return false;
        }
        return IssueMove(minion, plan.Path[^1], reason, allowFallback: false,
            prevalidatedPath: plan.Path);
    }

    private static bool WithinStopRange(Entity minion, Entity target)
    {
        var stop = Math.Max(0.0, CombatService.AttackReach(minion, target) - 3.0);
        return DistanceSquared(minion.Position, target.Position) <= stop * stop;
    }

    private Position2 WirePoint(Position2 point) => Geometry.ClientWaypoints([point],
        new NavigationBounds(_navigation.MinX, _navigation.MaxX, _navigation.MinZ, _navigation.MaxZ))[0];

    // A route's endpoint is its 2-unit wire quantization, which can sit in the next cell: both
    // must be client floor, or the unit is seated on a blocked cell.
    private bool IsStandable(Position2 point) =>
        _navigation.IsMovementWalkable(point) && _navigation.IsMovementWalkable(WirePoint(point));

    private void Acquire(Entity minion, Entity target, bool retarget = false,
        MinionAiState state = MinionAiState.Engage)
    {
        try { AcquireDecisionObserver?.Invoke(minion, target, retarget); }
        catch { /* diagnostics must never change target selection */ }
        if (minion.AttackTarget == target.NetId && minion.MinionState == state)
        {
            if (!ControlOwnsMovement(minion)) EngageMove(minion, target);
            return;
        }
        if (minion.AttackTarget is not null && minion.AttackTarget != target.NetId)
            StopAttack(minion, "retarget");
        minion.AttackTarget = target.NetId;
        _lastStop.Remove(minion.NetId);
        minion.AttackPoint = ChooseAttackPoint(minion, target);
        minion.MinionState = state;
        if (minion.InterruptedLaneWaypoints.Count > 0)
        {
            // A control effect may have installed a stationary singleton in
            // Waypoints before this acquire. The saved live suffix, captured
            // before cancellation, must win over that later stop anchor.
            minion.LaneWaypoints = minion.InterruptedLaneWaypoints.ToList();
            minion.InterruptedLaneWaypoints.Clear();
        }
        if (minion.LaneWaypoints.Count == 0 && minion.Waypoints.Count > 0)
            minion.LaneWaypoints = minion.Waypoints.ToList();
        // A control buff or a forced move owns the public order (ControlOwnsMovement): the script
        // still decides and the decision is kept above, but no route, seat or stop is published
        // while the control holds the unit — a plain 0xC1 mid-dash is the packet that snaps the
        // client (owner rule). The release decides again (OnCanMove -> FindTargetOrMove).
        if (ControlOwnsMovement(minion)) return;
        if (WithinStopRange(minion, target))
        {
            // Initial acquisition has no prior engage destination to dedupe;
            // source _acquire always gives an overlap seat one fresh route.
            var declip = FindOverlapPoint(minion, target);
            if (declip is Position2 overlap)
                IssueOverlapMove(minion, overlap, "overlap-declip");
            else
                ClearMovement(minion, "plant-stop");
        }
        else
        {
            EngageMove(minion, target);
        }
    }

    /// <param name="publishRoute">
    /// False while a control buff or a forced move owns the public order (<c>ControlOwnsMovement</c>): the decision is
    /// still made and the strategic lane route kept for the release, but nothing is planned, written to
    /// <see cref="Entity.Waypoints"/> or sent to the client until the control lets the unit walk again.
    /// </param>
    private void ReturnToLane(Entity minion, bool publishRoute = true)
    {
        try { ReturnDecisionObserver?.Invoke(minion, "target-lost"); }
        catch { /* diagnostics must never change gameplay */ }
        minion.AttackTarget = null;
        minion.MinionAutoAttackEnabled = false;
        minion.AttackPoint = null;
        minion.Target = null;
        // Forward navigation is SetStateAndMoveToForwardNav(AI_ATTACKMOVESTATE) (Minion.lua's FindTargetOrMove and TimerMoveForward):
        // the minion is in AI_ATTACKMOVESTATE from the moment it heads back, with no return state.
        minion.MinionState = MinionAiState.March;
        StopAttack(minion, "target-lost");
        AdvanceLaneEnd(minion);
        if (minion.LaneDone || minion.LaneWaypoints.Count == 0)
        {
            // A finished lane has nothing to return to: what an acquire saved here is the siege route's tail. Kept, it sent every
            // later forward step back to this branch, so the minion stood in the enemy base for good (batch 489, game 0fcfdfe8:
            // 55 idle reports, all March, lane done, saved tail, no route). Cleared, the next step re-sieges (MoveForward).
            minion.LaneWaypoints.Clear();
            ClearMovement(minion, "target-lost");
            minion.MinionState = MinionAiState.March;
            return;
        }

        if (!publishRoute)
        {
            minion.MinionState = MinionAiState.March;
            return;
        }
        var route = RestoreLaneRoute(minion);
        if (route.Count == 0)
        {
            // The old raw-suffix fallback could send a dead target's route
            // directly across a wall when A* could not reconnect. Keep the
            // same saved lane intent and let March retry after a stop.
            minion.InterruptedLaneWaypoints = minion.LaneWaypoints.ToList();
            ClearMovement(minion, "target-lost-wait-for-lane");
            minion.LaneWaypoints.Clear();
            _nextTargetScan[minion.NetId] = _elapsed + MinionDefinitions.TargetScanPeriodSeconds;
            minion.MinionState = MinionAiState.March;
            return;
        }
        EmitWaypoints(minion, _movement.InstallPath(minion, route), "target-lost");
        minion.LaneWaypoints.Clear();
        _nextTargetScan[minion.NetId] = _elapsed + MinionDefinitions.TargetScanPeriodSeconds;
        minion.MinionState = MinionAiState.March;
    }

    private IReadOnlyList<Position2> RestoreLaneRoute(Entity minion,
        IReadOnlyList<Position2>? route = null)
    {
        var lane = (route ?? minion.LaneWaypoints).ToList();
        if (lane.Count == 0) return Array.Empty<Position2>();
        while (lane.Count >= 2 && DistanceSquared(lane[0], minion.Position) <= 1.0)
            lane.RemoveAt(0);
        while (lane.Count >= 2)
        {
            var toHead = lane[0] - minion.Position;
            var forward = lane[1] - lane[0];
            if (toHead.X * forward.X + toHead.Z * forward.Z >= 0.0) break;
            lane.RemoveAt(0);
        }
        if (lane.Count == 0) return Array.Empty<Position2>();
        var restored = PlanLaneConnector(minion, lane);
        return restored.Count > 0 ? restored : EngineLaneLead(minion, lane);
    }

    /// <summary>
    /// The forward navigation planned from wherever the minion stands: minionLib's <c>FindTargetOrMove</c> (proto 0.3) and Minion.lua's
    /// <c>TimerMoveForward</c> (proto 0.4, a 0-second repeating timer) ask <c>SetStateAndMoveToForwardNav</c> at once and again every
    /// frame, with no waiting state, and the engine planner (<see cref="EngineRoute"/>) starts its A* on the unit's own cell without
    /// testing it and tests every neighbour it expands, so a minion the avoidance steer left on a blocked cell (batch 225, item 2) is
    /// planned out to the lane head like any other unit. The lane suffix follows. Empty when the engine plans nothing; the next frame
    /// asks again. Reconstruction: the forward-navigation native is unrecovered, so its goal is the saved lane head.
    /// </summary>
    private IReadOnlyList<Position2> EngineLaneLead(Entity minion, List<Position2> lane)
    {
        var lead = EngineRoute(minion, lane[0]);
        return lead is null ? [] : Deduplicate(new[] { minion.Position }.Concat(lead.Skip(1)).Concat(lane.Skip(1)));
    }

    // Reconstruction: native forward-node selection is unrecovered. We reconnect a saved lane suffix
    // with bounded offsets and retire its passed/occupied terminal to avoid stacking and backward
    // returns; exact connector geometry is emulator policy.
    private IReadOnlyList<Position2> PlanLaneConnector(Entity minion, List<Position2> lane)
    {
        if (lane.Count == 1)
        {
            var direct = _navigation.FindPath(minion.Position, lane[0]).ToList();
            IReadOnlyList<Position2> candidate = direct.Count == 0
                ? new[] { minion.Position, lane[0] } : direct;
            return LaneConnectorClear(minion, candidate) ? candidate : HeroLaneLead(minion, lane);
        }

        var first = lane[0];
        var second = lane[1];
        var forwardLength = Distance(first, second);
        if (forwardLength <= 1e-6)
        {
            var duplicate = new[] { minion.Position }.Concat(lane).ToArray();
            return LaneConnectorClear(minion, duplicate) ? duplicate : HeroLaneLead(minion, lane);
        }
        var forwardUnit = new Position2((second.X - first.X) / forwardLength,
            (second.Z - first.Z) / forwardLength);
        var along = (minion.Position.X - first.X) * forwardUnit.X +
                    (minion.Position.Z - first.Z) * forwardUnit.Z;
        var projection = first + new Position2(forwardUnit.X * along, forwardUnit.Z * along);
        var offset = minion.Position - projection;
        foreach (var scale in new[] { 1.0, 0.5, 0.0 })
        {
            var rawJoin = new Position2(first.X + offset.X * scale,
                first.Z + offset.Z * scale);
            var join = Geometry.ClientWaypoints([rawJoin],
                new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                    _navigation.MinZ, _navigation.MaxZ))[0];
            if (!_navigation.IsPathingWalkable(join)) continue;
            var lead = _navigation.FindPath(minion.Position, join);
            var merge = _navigation.FindPath(join, second);
            if (merge.Count == 0) continue;
            if (lead.Count > 0)
            {
                var candidate = Joined(lead, merge);
                if (LaneConnectorClear(minion, candidate)) return candidate;
            }
            // The minion planner swaps the exact start back in after escaping
            // to the nearest open cell, so its first segment is never checked
            // and can clip a turret's 140-unit footprint (playtest #8). The
            // hero planner validates that exact first segment.
            var heroLead = _navigation.FindHeroPath(minion.Position, join);
            if (heroLead.Count > 0)
            {
                var candidate = Joined(heroLead, merge);
                if (LaneConnectorClear(minion, candidate)) return candidate;
            }
        }
        var fallback = new[] { minion.Position }.Concat(lane).ToArray();
        return LaneConnectorClear(minion, fallback) ? fallback : HeroLaneLead(minion, lane);

        IReadOnlyList<Position2> Joined(IReadOnlyList<Position2> leg, IReadOnlyList<Position2> mergeLeg)
        {
            var anchor = Geometry.ClientWaypoints([minion.Position],
                new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                    _navigation.MinZ, _navigation.MaxZ))[0];
            var result = new List<Position2> { anchor };
            result.AddRange(Geometry.ClientWaypoints(leg,
                new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                    _navigation.MinZ, _navigation.MaxZ)).Skip(1));
            result.AddRange(Geometry.ClientWaypoints(mergeLeg,
                new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                    _navigation.MinZ, _navigation.MaxZ)).Skip(1));
            return Deduplicate(result.Concat(lane.Skip(2)));
        }
    }

    // Hero-planned connector to the lane head plus the unchanged suffix. It
    // still passes the ordinary connector validation; FindHeroPath honours
    // FindPathOverride like the minion planner.
    private IReadOnlyList<Position2> HeroLaneLead(Entity minion, List<Position2> lane)
    {
        var lead = _navigation.FindHeroPath(minion.Position, lane[0]);
        if (lead.Count == 0) return [];
        var candidate = Deduplicate(lead.Concat(lane.Skip(1)));
        if (LaneConnectorClear(minion, candidate)) return candidate;
        // All offset joins can cross a nexus even when their terrain cells
        // are open. Route the final lane-head fallback around its live body.
        var obstacles = _movement.StaticBodyObstacles(minion, margin: 0.0);
        // Reuse the NPC body-detour turning margins; every candidate still
        // passes exact-origin and quantized follower validation.
        foreach (var margin in new[] { 1.0, 2.0, 2.5 })
        {
            lead = _navigation.FindPathAroundUnits(minion.Position, lane[0], obstacles,
                preservePhysicalGaps: true, authoritativeStart: minion.Position,
                turnClearance: margin * minion.Speed * MovementSystem.FixedTickSeconds, heroPathing: true);
            if (lead.Count == 0) continue;
            candidate = Deduplicate(lead.Concat(lane.Skip(1)));
            if (LaneConnectorClear(minion, candidate)) return candidate;
        }
        return [];
    }

    private bool LaneConnectorClear(Entity minion, IReadOnlyList<Position2> route)
    {
        if (route.Count == 0) return false;
        var start = minion.Position;
        var wire = Geometry.ClientWaypoints(route,
            new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                _navigation.MinZ, _navigation.MaxZ));
        if (!Clear(route) || !Clear(wire)) return false;
        if (route.Count == 1) return DistanceSquared(start, route[0]) <= 9.0;
        return _movement.NpcFollowerTerrainRouteClear(minion, route) &&
               _movement.NpcFollowerTerrainRouteClear(minion, wire);

        bool Clear(IReadOnlyList<Position2> points)
        {
            var previous = start;
            foreach (var point in points)
            {
                // The NPC solver normally checks only the next destination
                // cell; a long fallback chord can skip an intervening wall.
                if (!_navigation.HasHeroMovementLineOfSight(previous, point)) return false;
                previous = point;
            }
            return true;
        }
    }

    private static IReadOnlyList<Position2> Deduplicate(IEnumerable<Position2> points)
    {
        var result = new List<Position2>();
        foreach (var point in points)
            if (result.Count == 0 || DistanceSquared(result[^1], point) > 1.0)
                result.Add(point);
        return result;
    }

    private void Anchor(Entity minion, IReadOnlyList<Position2> route)
    {
        var stored = Geometry.ClientWaypoints(route,
            new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                _navigation.MinZ, _navigation.MaxZ));
        minion.LastAnchorTime = _elapsed;
        minion.AnchorPath = stored.ToList();
        minion.SeparationAccumulation = Position2.Zero;
        _anchorSeen[minion.NetId] = minion.AnchorPath;
    }

    /// <summary>
    /// A minion's attack ends: a swing still in its windup is dropped, and its client is told (the callers only say that the
    /// attack ends). Whether a stop-attack goes out, and with which flags, is <c>CombatPackets.StopAttack</c>'s decision, the same
    /// for every unit: only while the client is in its attack state (<see cref="Entity.ClientAttackArmed"/>), which repeats the
    /// attack and calls StopActor (erasing the path being followed) on its own, and with DestroyMissile only for a swing the server
    /// dropped, never for a hit it still applies.
    /// </summary>
    private void StopAttack(Entity minion, string reason)
    {
        _combat.InterruptWindup(minion);
        _packets.SendStopAttack(minion, reason);
    }

    private Position2 StopPoint(Entity minion, Entity target)
    {
        var seat = AttackSeatRadius(minion, target);
        var dx = minion.Position.X - target.Position.X;
        var dz = minion.Position.Z - target.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var stop = seat;
        if (length <= 1e-9) return target.Position;
        var fallback = new Position2(target.Position.X + dx / length * stop,
            target.Position.Z + dz / length * stop);
        var angle = minion.AttackPoint is { } point && point.TargetNetId == target.NetId
            ? point.Angle
            : Math.Atan2(dz, dx);
        var fanned = new Position2(target.Position.X + Math.Cos(angle) * stop,
            target.Position.Z + Math.Sin(angle) * stop);
        return DistanceSquared(fanned, fallback) > 1.0 &&
               !_navigation.IsPathingWalkable(fanned) ? fallback : fanned;
    }

    /// <summary>
    /// The radius a closing attacker aims at: the edge of the target's own body, its pair body
    /// clearance -- the deepest point outside both bodies, so an arrival can never seat the mover
    /// inside its target. The approach itself is stopped by the attack-reach band
    /// (<see cref="WithinStopRange"/>), never here, so a melee minion is met where its attack
    /// reaches edge to edge (attack range plus both radii) and never closer. The aim only has to
    /// stay live until that band takes the pursuer: an aim at the attack seat is a fixed point a
    /// receding target leaves behind, the route completes just outside the band, the client seats
    /// the mover there, and Minion.lua 0.2's next <c>DELAY_FIND_ENEMIES</c> scan finds the target
    /// outside <c>AttackReach</c>, so the swing never starts (b153 <c>minions-stale-parking</c>).
    /// Batch 152 aimed at the target's centre to keep that walk alive, which is inside the body the
    /// route then ends in (b153 seed 5201 stacking).
    /// </summary>
    private double AttackSeatRadius(Entity minion, Entity target) =>
        Math.Max(0.0, _movement.BodyClearance(minion, target));

    /// <summary>
    /// The reserved firing angle an engage approach closes on: the angle <see cref="StopPoint"/> seats
    /// on, which is the fan claim <see cref="ChooseAttackPoint"/> latched for this pair. Co-attackers of
    /// one target hold mutually exclusive angles at the ring the pair's body clearance needs; an
    /// approach that ignores them and aims along each unit's own bearing lands every attacker on the
    /// same radial point and the pair walks into itself there (b153, seed 5205: two melee endpoints of
    /// one turret siege 57 units apart against a 70-unit pair clearance).
    /// </summary>
    private double SeatAngle(Entity minion, Entity target)
    {
        if (minion.AttackPoint is { } point && point.TargetNetId == target.NetId)
            return point.Angle;
        return Math.Atan2(minion.Position.Z - target.Position.Z,
            minion.Position.X - target.Position.X);
    }

    // Reconstruction: retail CloseToTarget attack-point selection is unrecovered. We reserve
    // physical-clearance fan seats to prevent duplicate approach destinations; the seat search and 3/6-unit
    // margins are emulator policy.
    private MinionAttackPoint ChooseAttackPoint(Entity minion, Entity target)
    {
        if (_fanClaims.TryGetValue(minion.NetId, out var existing) &&
            existing.TargetNetId == target.NetId)
        {
            return new MinionAttackPoint(target.NetId, existing.Angle,
                Math.Min(Distance(minion.Position, target.Position),
                    Math.Max(0.0, CombatService.AttackReach(minion, target) - 3.0)));
        }
        var stopRadius = Math.Max(0.0, CombatService.AttackReach(minion, target) - 3.0);
        var radius = Math.Min(Distance(minion.Position, target.Position), stopRadius);
        var home = Math.Atan2(minion.Position.Z - target.Position.Z,
            minion.Position.X - target.Position.X);
        var required = new List<(double Angle, double Gap)>();
        foreach (var (netId, claim) in _fanClaims)
        {
            if (netId == minion.NetId || claim.TargetNetId != target.NetId) continue;
            var other = _world.Get(netId);
            if (other is null || !other.Alive || other.AttackTarget != target.NetId)
                continue;
            var arc = _movement.BodyClearance(minion, other);
            var otherRadius = FanRadius(other, target);
            var outer = Math.Max(radius, otherRadius);
            var gap = Math.Max(MinimumAngularGap(radius, otherRadius, arc),
                MinimumAngularGap(outer, outer, arc));
            if (gap > 0.0) required.Add((claim.Angle, gap));
        }

        if (required.Count == 0)
        {
            _fanClaims[minion.NetId] = (target.NetId, home);
            NotifyFanClaim(minion, target, home, radius, minion.Position, home,
                fresh: true, reclaim: false, physicalOverlapAtEntry: false,
                successful: true);
            return new MinionAttackPoint(target.NetId, home, radius);
        }

        var step = Math.Max(1e-6, required.Max(item => item.Gap));
        var chosen = home;
        for (var index = 0; index <= Math.Ceiling(Math.PI / step); index++)
        {
            var candidates = index == 0
                ? new[] { home }
                : new[] { home + index * step, home - index * step };
            double? available = null;
            foreach (var candidate in candidates)
            {
                if (required.All(item =>
                        Math.Abs(WrapPi(candidate - item.Angle)) >= item.Gap - 1e-9))
                {
                    available = candidate;
                    break;
                }
            }
            if (available is double candidateAngle)
            {
                chosen = candidateAngle;
                break;
            }
        }
        _fanClaims[minion.NetId] = (target.NetId, chosen);
        NotifyFanClaim(minion, target, chosen, radius, minion.Position, home,
            fresh: true, reclaim: false, physicalOverlapAtEntry: false,
            successful: true);
        return new MinionAttackPoint(target.NetId, chosen, radius);
    }

    private void NotifyFanClaim(Entity minion, Entity target, double angle,
        double radius, Position2 origin, double home, bool fresh, bool reclaim,
        bool physicalOverlapAtEntry, bool successful)
    {
        var observer = FanClaimObserver;
        if (observer is null) return;
        var rivals = _fanClaims
            .Where(pair => pair.Key != minion.NetId && pair.Value.TargetNetId == target.NetId)
            .Select(pair => _world.Get(pair.Key))
            .Where(other => other is not null && other.Alive &&
                            other.AttackTarget == target.NetId)
            .Cast<Entity>()
            .ToArray();
        var minionRadius = rivals.Length == 0
            ? _movement.PairMovementRadius(minion, minion)
            : _movement.PairMovementRadius(minion, rivals[0]);
        var widest = rivals.Select(other => _movement.PairMovementRadius(other, minion))
            .DefaultIfEmpty(minionRadius).Max();
        observer(new MinionFanClaimObservation(minion.NetId,
            target.NetId, angle, radius, origin, home, rivals.Length,
            (minionRadius + widest) * MinionDefinitions.SeparationTargetRatio,
            fresh, reclaim, physicalOverlapAtEntry, successful));
    }

    private static double FanRadius(Entity minion, Entity target) =>
        Math.Min(Distance(minion.Position, target.Position),
            Math.Max(0.0, CombatService.AttackReach(minion, target) - 3.0));

    private static double MinimumAngularGap(double firstRadius, double secondRadius,
        double clearance)
    {
        if (firstRadius < 1e-6 || secondRadius < 1e-6) return 0.0;
        var cosine = (firstRadius * firstRadius + secondRadius * secondRadius -
                      clearance * clearance) / (2.0 * firstRadius * secondRadius);
        return cosine >= 1.0 ? 0.0 : Math.Acos(Math.Clamp(cosine, -1.0, 1.0));
    }

    // An ally co-attacker is a body the client will never walk around on its own and the pair is
    // inside itself at contact. The same seating bug leaves an attacker inside its own target, so
    // an enemy body takes the same escape the moment the two are inside their own pair radius:
    // waiting for the playtest's own 12-unit epsilon only lets the pair settle deeper (b153: gating
    // the enemy at span-12 cost 16 s of stacked across five seeds against the full span).
    private bool OverlapsBody(Entity minion, Entity other)
    {
        var span = _movement.PairMovementRadius(minion, other) +
                   _movement.PairMovementRadius(other, minion);
        return DistanceSquared(minion.Position, other.Position) < span * span;
    }

    // Reconstruction: the recovered client does not separate idle planted co-attackers; a finished path
    // is seated on its endpoint without collision (client_path_follower_re.md, section 4a). We issue
    // a validated overlap-escape route after committed windup to prevent persistent stacked
    // attackers; native server declipping is unrecovered. The overlapped allies need not attack
    // this target (seed 1, b19: 19 of 33 stacks were planted casters with different targets), and
    // a newcomer spawned on a planted ally is waiting in congestion rather than planted (the
    // 10.5 s stack at the Order nexus): both count, and the planted minion yields. A seated pair
    // is not pushed apart by the contact solver either -- neither body is moving -- so the same
    // seating bug leaves an attacker inside its own target, which takes the same escape (b153 seed
    // 5201/5205: Order/Chaos pairs held 2-5 s at 12-17 units of overlap).
    private Position2? FindOverlapPoint(Entity minion, Entity target)
    {
        var blockers = _world.Entities.Values.Where(other =>
            other.NetId != minion.NetId && other.Alive &&
            (other.UnitType == UnitType.Minion || other.NetId == target.NetId) &&
            other.Waypoints.Count == 0 && other.Target is null &&
            _movement.PairMovementRadius(other, minion) > 0.0 &&
            OverlapsBody(minion, other)).ToArray();
        if (blockers.Length == 0) return null;

        // A planted co-attacker that acquired without needing a fan seat has
        // no claim yet. Its current bearing is nevertheless occupied; latch
        // that physical seat before selecting this minion's alternate. This
        // mirrors _overlap_arc_point's first pass and prevents the first
        // attacker from being treated as if it were still unclaimed.
        foreach (var blocker in blockers)
        {
            // A claim on this target's ring says nothing about a blocker fighting another target.
            if (blocker.AttackTarget != target.NetId) continue;
            if (!_fanClaims.TryGetValue(blocker.NetId, out var claim) ||
                claim.TargetNetId != target.NetId)
            {
                _fanClaims[blocker.NetId] = (target.NetId,
                    Math.Atan2(blocker.Position.Z - target.Position.Z,
                        blocker.Position.X - target.Position.X));
            }
        }

        // The escape ring is never inside the target's own body: a pair seated closer than their
        // clearance walks out to the attack seat, the deepest ring outside both bodies, rather than
        // orbiting the target at the overlapping distance it must leave.
        var radius = Math.Max(Distance(minion.Position, target.Position),
            AttackSeatRadius(minion, target));
        if (radius <= 1e-6) return null;

        // _overlap_arc_point first revisits the latched fan seat. A seat may
        // have become unusable after terrain or a neighbouring body changed,
        // but it is still the preferred physical firing point while it is
        // valid. ChooseAttackPoint also records the current bearing when the
        // first attacker had no explicit fan claim.
        var previousClaim = _fanClaims.TryGetValue(minion.NetId, out var prior) &&
            prior.TargetNetId == target.NetId ? prior : ((uint TargetNetId, double Angle)?)null;
        var origin = minion.Position;
        var angle = ChooseAttackPoint(minion, target).Angle;
        void NotifyOverlapReclaim()
        {
            if (previousClaim is not { } before ||
                !_fanClaims.TryGetValue(minion.NetId, out var current) ||
                current.TargetNetId != target.NetId ||
                Math.Abs(current.Angle - before.Angle) <= 1e-12) return;
            NotifyFanClaim(minion, target, current.Angle, radius, origin,
                Math.Atan2(origin.Z - target.Position.Z,
                    origin.X - target.Position.X), fresh: false,
                reclaim: true, physicalOverlapAtEntry: true, successful: true);
        }
        var bodyObstacles = LocalBodyObstacles(minion, [minion.Position, target.Position]);
        Position2 PointAt(double candidate) => new(
            target.Position.X + Math.Cos(candidate) * radius,
            target.Position.Z + Math.Sin(candidate) * radius);
        bool Separated(Position2 point) =>
            DistanceSquared(point, minion.Position) > 1.0 &&
            blockers.All(other => DistanceSquared(point, other.Position) >=
                Math.Pow(_movement.BodyClearance(minion, other), 2.0));
        bool Usable(Position2 point) => Separated(point) &&
            _navigation.IsPathingWalkable(point);
        bool MovementUsable(Position2 point) => Separated(point) &&
            LocalOverlapPath(minion, point, bodyObstacles) is not null;

        var point = PointAt(angle);
        if (Usable(point) || MovementUsable(point))
        {
            NotifyOverlapReclaim();
            return point;
        }

        // A latched seat can become unwalkable or be the point occupied by
        // this unit after another attacker plants. Re-scan the same ring from
        // the current bearing using the exact law-of-cosines gaps.
        var home = Math.Atan2(minion.Position.Z - target.Position.Z,
            minion.Position.X - target.Position.X);
        var gaps = blockers.Select(other => MinimumAngularGap(radius,
            Distance(other.Position, target.Position),
            _movement.BodyClearance(minion, other)));
        var step = gaps.DefaultIfEmpty(0.0).Max();
        if (step <= 1e-6) return null;
        foreach (var candidate in FanCandidates(home, step))
        {
            point = PointAt(candidate);
            if (!Usable(point)) continue;
            _fanClaims[minion.NetId] = (target.NetId, candidate);
            minion.AttackPoint = new MinionAttackPoint(target.NetId, candidate, radius);
            NotifyOverlapReclaim();
            return point;
        }
        foreach (var candidate in FanCandidates(home, step))
        {
            point = PointAt(candidate);
            if (!MovementUsable(point)) continue;
            _fanClaims[minion.NetId] = (target.NetId, candidate);
            minion.AttackPoint = new MinionAttackPoint(target.NetId, candidate, radius);
            NotifyOverlapReclaim();
            return point;
        }
        return null;
    }

    private IReadOnlyList<NavigationObstacle> LocalBodyObstacles(
        Entity minion, IReadOnlyList<Position2> points)
    {
        var reach = points.Max(point => Distance(minion.Position, point)) +
            _movement.MaximumMovementRadius + 20.0;
        var reachSquared = reach * reach;
        return _world.Entities.Values
            .Where(other => other.NetId != minion.NetId && other.Alive &&
                _movement.MovementRadius(other) > 0.0 && IsMobileBody(other) && !IsGhosted(other) &&
                points.Min(point => DistanceSquared(other.Position, point)) <= reachSquared)
            .Select(other => new NavigationObstacle(other.Position,
                _movement.BodyClearance(minion, other)))
            .ToArray();
    }

    private IReadOnlyList<Position2>? LocalOverlapPath(
        Entity minion, Position2 destination,
        IReadOnlyList<NavigationObstacle> bodyObstacles)
    {
        var route = Geometry.ClientWaypoints([minion.Position, destination],
            new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                _navigation.MinZ, _navigation.MaxZ));
        if (route.Count < 2 || DistanceSquared(route[0], route[^1]) <= 1e-12)
            return null;
        if (route.Any(point => !_navigation.IsMovementWalkable(point)) ||
            !_navigation.HasMovementLineOfSight(route[0], route[^1]) ||
            !_navigation.HasMovementLineOfSight(minion.Position, route[^1]) ||
            !_navigation.FirstSegmentClearOfUnits(route[0], minion.Position,
                route[^1], bodyObstacles) ||
            !_navigation.SegmentClearOfUnits(route[0], route[^1], bodyObstacles,
                escape: true))
            return null;
        return route;
    }

    private static IEnumerable<double> FanCandidates(double home, double step)
    {
        yield return home;
        var limit = (int)Math.Floor(Math.PI / step);
        for (var index = 1; index <= limit; index++)
        {
            yield return home + index * step;
            yield return home - index * step;
        }
    }

    private static bool IsMobileBody(Entity entity) =>
        entity.Speed > 0.0 && entity.UnitType is not UnitType.Turret and not UnitType.Structure;

    private static bool IsGhosted(Entity entity) =>
        entity.AnyBuffOrStatus(buff => buff.Payload.Ghosted);

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(DistanceSquared(first, second));

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);

    private static double WrapPi(double angle)
    {
        while (angle > Math.PI) angle -= Math.PI * 2.0;
        while (angle < -Math.PI) angle += Math.PI * 2.0;
        return angle;
    }
}
