using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    public void AddObserver(ReplicationObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (_observers.Any(existing => existing.ObserverId == observer.ObserverId))
            throw new InvalidOperationException($"Observer {observer.ObserverId} is already registered.");
        _observers.Add(observer);
    }

    public bool RemoveObserver(ulong observerId)
    {
        var index = _observers.FindIndex(observer => observer.ObserverId == observerId);
        if (index < 0) return false;
        var observer = _observers[index];
        _observers.RemoveAt(index);
        foreach (var key in _leeFollowDashes.Keys.Where(key => key.Observer == observer).ToArray())
            _leeFollowDashes.Remove(key);
        return true;
    }

    private Dictionary<Team, TeamVisibilityState> BuildTeamState(
        IReadOnlyList<ReplicationObserver> active)
    {
        var states = new Dictionary<Team, TeamVisibilityState>();
        foreach (var team in active.Select(observer => observer.Team).Distinct())
            states.Add(team, VisibilityState(team, VisibilityRules.BuildTeamProviders(World, team, Balls, AdditionalProviders)));
        return states;
    }

    // A nearsighted hero's client keeps only its own sight list (VisibilityRules.BuildObserverProviders) and does not light the global vision
    // cells; every other observer gets its team's.
    private TeamVisibilityState ObserverState(ReplicationObserver observer, Dictionary<Team, TeamVisibilityState> teamStates)
    {
        if (World.Get(observer.HeroNetId) is not { } hero || !VisibilityRules.IsNearSighted(hero)) return teamStates[observer.Team];
        return VisibilityState(observer.Team, VisibilityRules.BuildObserverProviders(World, hero, Balls, AdditionalProviders), globalVision: false);
    }

    private TeamVisibilityState VisibilityState(Team team, IReadOnlyList<VisionProvider> providers, bool globalVision = true)
    {
        var raw = VisibilityRules.ComputeRawVisibility(
            World,
            team,
            Environment,
            providers,
            point => RevealsStealth(team, point),
            Instrumentation,
            globalVision);
        var visibleBalls = VisibilityRules.ComputeVisibleBalls(team, Balls, Environment, providers, globalVision);
        return new TeamVisibilityState(providers, raw, visibleBalls);
    }

    private void RefreshObserver(ReplicationObserver observer, TeamVisibilityState teamState)
    {
        // Session replay can begin after the source server has already sent
        // the peer's direct spawn burst.  In that mode the capture is an
        // application stream, so keep the recorded visibility snapshot while
        // still allowing the pending typed messages below to route normally.
        if (observer.View.VisibilityRefreshSuppressed)
            return;
        var oldVisible = observer.View.Visible.ToArray();
        var now = SimulationTime();
        var desired = new HashSet<uint>();

        foreach (var entity in World.Entities.Values)
        {
            if (entity.UnitType == UnitType.Missile)
                continue;
            // An un-announced placemarker is skipped (Orianna's ball has its own projection; its missiles come with a temporary caster).
            // An announced one is served like a minion (Entity.Announced).
            if (entity.Placemarker && !entity.Announced)
                continue;

            var isOwnHero = entity.NetId == observer.HeroNetId;
            var rawSeen = teamState.Raw.Contains(entity.NetId) || isOwnHero;
            if (entity.Kind == EntityKind.Neutral && entity.Alive &&
                !observer.View.Known.Contains(entity.NetId))
            {
                // CreateNeutral also registers the camp's minimap marker.
                // Its public home/identity baseline must precede first sight.
                var baseline = Services.BuildNeutralCampBaseline(entity, observer);
                if (baseline.Count != 0)
                {
                    SendAll(observer, baseline);
                    observer.View.Known.Add(entity.NetId);
                    if (!rawSeen) SendAll(observer, Services.BuildLeave(entity, observer));
                }
            }
            // Batch 329: CharSpawnPet (0x3C) is PolicyBroadcast (see CanRoute): the spawn-pet and clone-pet helpers
            // send it through the owner's NetVisibilityObject (TryBroadcast), and the owner's handler (DoSpawnPet)
            // creates the pet with no visibility test. So every client that holds the owner creates the pet at its spawn; one that cannot see
            // it is told the pet left its sight, as a camp made known before its first sight (above). The broadcast is the spawn event only (the
            // flush of the spawn, with the spawn position): a viewer that learns the owner later creates the pet at its first sight.
            if (!rawSeen && entity.Alive && IsSpawnedAsPet(entity) && !_announcedPetSpawns.Contains(entity.NetId) &&
                !observer.View.Known.Contains(entity.NetId) && observer.View.Known.Contains(entity.OwnerNetId))
            {
                SendFreshReplication(observer, Services.BuildEntityCreation(entity, observer));
                observer.View.Known.Add(entity.NetId);
                SendAll(observer, Services.BuildLeave(entity, observer));
            }
            // Batch 457 (Reconstruction): a turret the clients hold as a minion (the Odin point turrets, Entity.ReplicatedAsMinion) is known to
            // every client from its spawn, as a scene turret is. The capture announcement names it as its source (0x4A; Announcements_ODIN.ini
            // OnCapturePointCaptured "@src your team | enemy team@"), and a client that cannot resolve the source writes "Unknown" instead
            // (the client's source-name routine); the capture point packets name it too. A client that cannot see it is told it left its sight.
            if (!rawSeen && entity.Alive && entity.ReplicatedAsMinion && !observer.View.Known.Contains(entity.NetId))
            {
                SendFreshReplication(observer, Services.BuildEntityCreation(entity, observer));
                observer.View.Known.Add(entity.NetId);
                SendAll(observer, Services.BuildLeave(entity, observer));
            }
            var seenNow = rawSeen;
            if (entity.UnitType == UnitType.Turret &&
                entity.Team != observer.Team &&
                VisibilityRules.IsShrine(entity) &&
                _preparedShrines.Contains((observer.ObserverId, entity.NetId)))
                seenNow = true;
            if (!entity.Alive)
                seenNow = isOwnHero || observer.View.Visible.Contains(entity.NetId);

            if (seenNow)
            {
                desired.Add(entity.NetId);
                if (rawSeen)
                    observer.View.LostAt.Remove(entity.NetId);
            }
        }

        foreach (var netId in desired)
        {
            var entity = World.Get(netId);
            if (entity is null) continue;
            if (!observer.View.Visible.Contains(netId))
            {
                var wasKnown = observer.View.Known.Contains(netId);
                if (!wasKnown)
                {
                    SendFreshReplication(observer, Services.BuildEntityCreation(entity, observer));
                    observer.View.Known.Add(netId);
                }
                var staleDash = observer.View.StaleDashes.Remove(netId) && wasKnown;
                if (Services.StatsBeforeReveal(entity))
                    SendFreshReplication(observer, Services.BuildEntityStats(entity, observer));
                // The reveal's 0xC2 carries the unit's route, dash or stop (ProtocolReplicationServices.RevealMovement); no reveal-time
                // 0xC1 or 0x67 follows it. After a dash this observer saw start, the entry also carries the teleport that replaces it.
                var reveal = Services.BuildReveal(entity, observer, desired, staleDash);
                SendAll(observer, reveal);
                if (!Services.StatsBeforeReveal(entity))
                    SendFreshReplication(observer, Services.BuildEntityStats(entity, observer));
                SendAll(observer, Services.BuildBuffSnapshot(entity, observer));
                // A queued original-start dash or fear waypoint would rewind the forced move the reveal carried.
                // Reconstruction: a lane minion's reveal carries its live route, so the route queued at its
                // spawn (or by an AI decision earlier in this tick), made while this observer could not see the
                // unit, is not sent to it: it would arrive right behind the reveal under a sync id no newer, with the
                // same payload (real-client telemetry, 16 Oct 2026: one duplicate about 1.6 ms after every wave
                // spawn). The client's reveal has its own route slot (0xC2 movement data type 2,
                // the waypoint reader), so retail sent the route once, in the reveal;
                // its queue order is not recoverable (batch 270).
                var movementType = RevealMovementType(reveal);
                if (movementType != 0 && entity.ForcedMove is { Completed: false })
                    _forcedMoveReanchors.Add((observer, netId));
                else if (movementType != 0 && entity.MinionType is not null)
                    _revealedMinionRoutes.Add((observer, netId));
                if (entity.ForcedMove is { Effect: "BlindMonkQTwoDash", Completed: false,
                    FollowNetId: uint targetId } && movementType != 0)
                    _leeDashSent[(observer, netId)] = observer.View.Known.Contains(targetId) &&
                        desired.Contains(targetId) &&
                        movementType == VisionPackets.MovementDataRouteWithSpeed;
                observer.View.Visible.Add(netId);
                observer.View.LostAt.Remove(netId);
            }
        }

        foreach (var netId in oldVisible)
        {
            if (desired.Contains(netId)) continue;
            // Ball projections are not World entities.  Keep them in the
            // ball-specific lifecycle below so a still-rendered ball is not
            // removed and recreated (which would resend C2 every flush), and
            // a hidden ball can emit its one leave sequence.
            if (observer.View.BallStates.ContainsKey(netId)) continue;
            var entity = World.Get(netId);
            if (entity is not null)
                _deferredLeaves[observer].AddRange(Services.BuildLeave(entity, observer));
            // The client keeps drawing a dash it saw start after the actor
            // leaves sight; the next reveal must replace that stale copy.
            if (entity?.ForcedMove is { Completed: false, DashPresentation: true })
                observer.View.StaleDashes.Add(netId);
            observer.View.Visible.Remove(netId);
            observer.View.LostAt[netId] = now;
        }

        var desiredBalls = teamState.VisibleBalls;
        foreach (var ball in Balls)
        {
            if (!desiredBalls.Contains(ball.NetId)) continue;
            observer.View.BallStates[ball.NetId] = ball;
            if (!observer.View.Visible.Contains(ball.NetId))
            {
                if (!observer.View.Known.Contains(ball.NetId))
                {
                    SendAll(observer, Services.BuildBallProjection(ball, observer));
                    observer.View.Known.Add(ball.NetId);
                }
                else
                {
                    SendAll(observer, Services.BuildBallReveal(ball, observer));
                }
                observer.View.Visible.Add(ball.NetId);
            }
        }

        foreach (var ballId in observer.View.Visible.Where(id =>
                     observer.View.BallStates.ContainsKey(id) && !desiredBalls.Contains(id)).ToArray())
        {
            SendAll(observer, Services.BuildBallLeave(observer.View.BallStates[ballId], observer));
            observer.View.Visible.Remove(ballId);
        }

        SyncTerrain(observer);
    }

    private void RefreshNeutralEmpty(
        ReplicationObserver observer,
        TeamVisibilityState teamState)
    {
        if (NeutralCamps is null || observer.Team is not (Team.Order or Team.Chaos) ||
            observer.ClientId is not uint clientId)
            return;

        foreach (var camp in NeutralCamps.Values)
        {
            if (camp.Active || !camp.PendingEmpty)
                continue;

            // A new connection has no native camp registration until it has
            // received a real CreateNeutral.  Sending 0xCB without that
            // registration would require inventing a dead member and can
            // create an alive-looking marker.  Reconnect therefore remains a
            // no-op for a fresh view, which is the safe native behavior.
            if (!camp.MemberNetIds.Any(observer.View.Known.Contains))
                continue;

            if (observer.View.NeutralEmptyGenerations.TryGetValue(
                    camp.Definition.Number, out var deliveredGeneration) &&
                deliveredGeneration == camp.SpawnCycles)
                continue;

            // The camp marker's line-of-sight test: a unit of the team sees the camp marker within 1000, the
            // walk starting its PathfindingCollisionRadius + 200 out (NavigationGrid.CampMarkerLineOfSightTest). The test takes a unit,
            // so a perception bubble without one (the shop, a timed bubble) does not see the marker.
            var group = camp.CurrentGroupPosition;
            var marker = new Position2(group.X, group.Z);
            var currentlyVisible = World.Entities.Values.Any(unit => unit.Team == observer.Team &&
                VisibilityRules.TerrainVisionRange(unit) > 0.0 &&
                Environment.CampMarkerLineOfSightTest(unit.Position, unit.SelectionRadius,
                    Math.Max(0.0, unit.PathfindingRadius ?? unit.Radius), marker));
            if (!camp.EmptyObservedTeams.Contains(observer.Team) && !currentlyVisible)
                continue;

            Send(observer, SpawnPackets.NeutralCampEmpty(
                clientId, camp.Definition.Number, state: false));
            observer.View.NeutralEmptyGenerations[camp.Definition.Number] = camp.SpawnCycles;
            camp.MarkEmptyObserved(observer.Team);
        }
    }

    /// <summary>
    /// The sight radius modifiers of the observer's team's units (<see cref="VisibilityRules.BubbleRadiusMods"/>) reach the client as the
    /// replicated <c>mFlatBubbleRadiusMod</c> and <c>mPercentBubbleRadiusMod</c>, because the client draws the fog of its own units from
    /// <c>(percent + 1) * base + flat</c> and would keep the unmodified radius otherwise (the dormant Bushwhack
    /// trap's circle, 23 September 2026). A pair is sent when it is not the default or the client holds another; the replication store of
    /// the view filters a repeat. Enemy units' modifiers do not matter to this client: it draws only its own team's bubbles.
    /// </summary>
    private void SyncBubbleMods(ReplicationObserver observer)
    {
        var view = observer.View;
        foreach (var netId in view.BubbleMods.Keys.Where(id => !view.Known.Contains(id)).ToArray())
            view.BubbleMods.Remove(netId);
        foreach (var entity in World.Entities.Values)
        {
            // A dead unit's buffs end with it; its field is brought back to the buffs' sum when it lives again.
            // Batch 462: a point turret (Entity.ReplicatedAsMinion) is a minion on the client and takes the minion block; its sight follows the
            // capture trees' IncPermanentPercentBubbleRadiusMod. The scene turrets keep their own layout.
            if (entity.Team != observer.Team || !entity.Alive || entity.Placemarker || !view.Known.Contains(entity.NetId) ||
                entity.IsSceneTurret || entity.UnitType is UnitType.Structure or UnitType.Missile) continue;
            var (flat, percent) = VisibilityRules.BubbleRadiusMods(entity);
            var wanted = ((float)flat, (float)percent);
            var held = view.BubbleMods.TryGetValue(entity.NetId, out var sent) ? sent : (0.0f, 0.0f);
            if (wanted == held) continue;
            view.BubbleMods[entity.NetId] = wanted;
            Send(observer, CombatPackets.UnitBubbleRadiusMods(entity.NetId, wanted.Item1, wanted.Item2,
                entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0, NextSyncId(entity.NetId)));
        }
    }

    /// <summary>
    /// The model scale of every unit the observer knows (<see cref="ModelScale"/>: Olaf's Ragnarok, Nasus' Fury of the Sands, Cho'Gath's Feast)
    /// reaches the client as the replicated <c>mScaleSkinCoef</c>. A coefficient other than 1 is sent when the client holds another value, and the
    /// 1 that ends a buff is sent to a client that holds a different one; the view forgets a unit it no longer knows, so a re-created unit gets its
    /// scale again. Every team sees the model, so there is no team filter.
    /// </summary>
    private void SyncModelScales(ReplicationObserver observer)
    {
        var view = observer.View;
        foreach (var netId in view.ModelScales.Keys.Where(id => !view.Known.Contains(id)).ToArray())
            view.ModelScales.Remove(netId);
        foreach (var entity in World.Entities.Values)
        {
            if (!entity.Alive || entity.Placemarker || entity.UnitType is UnitType.Missile || !view.Known.Contains(entity.NetId)) continue;
            if (entity.Buffs.Count == 0 && !view.ModelScales.ContainsKey(entity.NetId)) continue;
            var wanted = ModelScale.Of(entity);
            var held = view.ModelScales.TryGetValue(entity.NetId, out var sent) ? sent : 1.0f;
            if (wanted == held) continue;
            if (wanted == 1.0f) view.ModelScales.Remove(entity.NetId);
            else view.ModelScales[entity.NetId] = wanted;
            Send(observer, CombatPackets.UnitModelScale(entity.NetId, wanted,
                entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0, NextSyncId(entity.NetId)));
        }
    }

    private void SyncTerrain(ReplicationObserver observer)
    {
        if (!observer.View.TerrainBootstrapped)
        {
            SendAll(observer, Services.BuildTerrainBootstrap(observer));
            observer.View.TerrainBootstrapped = true;
        }

        var desired = new Dictionary<uint, (Entity? Entity, float Radius)>();
        foreach (var netId in observer.View.Visible)
        {
            var entity = World.Get(netId);
            // Every unit's own sight is the bubble its client object makes, so a retail server sends none for it: a structure's (turret,
            // inhibitor and nexus creators), a hero's (the hero creator) and every minion's, lane minions, pets,
            // wards and script units alike (the minion creator, unless its CharacterData is ServerOnly: ChampionData.ServerOnly,
            // six shipped characters such as OdinSpeedShrine and TestCube, batch 464): IsUnit, (percent + 1) * CharacterData PerceptionBubbleRadius + flat, kept current by the modifier updates and
            // drawn while the unit is alive and seen. The client never reads the spawn packet's VisibilitySize (the stored field), so that
            // bubble is the only one the server sends here (Reconstruction: VisibilitySize as a second sight bubble, VisibilitySizeRange).
            if (entity is null || entity.Team != observer.Team || entity.VisibilitySize <= 0.0)
                continue;
            var radius = VisibilityRules.VisibilitySizeRange(entity);
            if (radius > 0.0)
                desired[netId ^ 0x2000_0000u] = (entity, (float)radius);
        }
        // The Orianna ball's own bubble (OriannaBall 700) is drawn by its client object; the timed bubble its OrianaGhostMinion script adds
        // (BBAddPosPerceptionBubble, 225) is sent below.

        foreach (var entry in desired)
        {
            if (observer.View.TerrainIds.Add(entry.Key))
            {
                SendAll(observer, Services.BuildTerrainAttach(entry.Value.Entity!, entry.Key, entry.Value.Radius, observer));
            }
        }
        foreach (var bubbleId in observer.View.TerrainIds.Where(id => !desired.ContainsKey(id)).ToArray())
        {
            SendAll(observer, Services.BuildTerrainRemove(bubbleId, observer));
            observer.View.TerrainIds.Remove(bubbleId);
        }

        // Timed sight with an actual lifetime cannot wait for the one-second
        // item refresh: a short placement reveal could be missed entirely.
        // Send each timed bubble once per view, with its remaining native TTL,
        // and retire it on the first flush after authoritative expiry.
        var activeTimed = new HashSet<uint>();
        foreach (var bubble in World.TimedVisionBubbles)
        {
            if (bubble.Team != observer.Team || bubble.Remaining <= 0.0) continue;
            // Batch 452: a SpecificUnitsClientOnly bubble is shown to the client of that unit alone (the observer whose hero it is).
            if (bubble.ClientOnlyNetId != 0 && observer.HeroNetId != bubble.ClientOnlyNetId) continue;
            if (bubble.BubbleId == 0 || bubble.Radius < 0.0)
                throw new InvalidOperationException("Timed vision returned an invalid bubble.");
            if (bubble.AnchorNetId != 0 &&
                (World.Get(bubble.AnchorNetId) is null ||
                 !observer.View.Known.Contains(bubble.AnchorNetId)))
                continue;
            activeTimed.Add(bubble.BubbleId);
            if (!observer.View.TimedTerrainIds.Add(bubble.BubbleId)) continue;
            var radius = ProtocolEncoding.Float32(bubble.Radius, nameof(bubble.Radius));
            var ttl = ProtocolEncoding.Float32(bubble.Remaining, nameof(bubble.Remaining));
            if (bubble.AnchorNetId != 0)
            {
                // DEV-06 uses the same wall-mask/true-sight flags as buff-owned
                // attached bubbles. Preserve ordinary timed-bubble flags.
                // DIAGNOSTICS (batch 476): the OdinRelicBubbleGate withholds this 0x26 for a centre crystal's bubble; the bubble stays on the server.
                if (!Amberline.Network.Diagnostics.OdinRelicBubbleGate.Withholds(World.Get(bubble.AnchorNetId)?.ChampionName))
                    Send(observer, SpawnPackets.AddUnitPerceptionBubble(
                        bubble.AnchorNetId, radius, ttl, bubble.BubbleId,
                        (uint)observer.Team, flags: bubble.RevealStealth ? 5u : 0u, revealSpecificNetworkId: bubble.RevealUnitNetId));
            }
            else
                Send(observer, SpawnPackets.AddPerceptionBubble(new(
                    (uint)observer.Team,
                    new Position3(bubble.Position.X, 100.0, bubble.Position.Z),
                    radius, ttl, bubble.BubbleId, RevealSpecificNetworkId: bubble.RevealUnitNetId)));
        }
        foreach (var bubbleId in observer.View.TimedTerrainIds.Where(id => !activeTimed.Contains(id)).ToArray())
        {
            Send(observer, SpawnPackets.RemovePerceptionBubble(bubbleId));
            observer.View.TimedTerrainIds.Remove(bubbleId);
        }
        // An item's sight is the bubble its own script adds and removes (OdinLightbringer.luaobj: BBAddUnitPerceptionBubble on
        // activate, BBRemovePerceptionBubble on deactivate), sent once above; a ward is its own client object. Batch 270 retired
        // the Python server's once-a-second 1.5 s re-projection of every item vision provider (0x6FFExxxx), which retail never sent.
    }
}
