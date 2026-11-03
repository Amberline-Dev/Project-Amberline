using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

/// <summary>
/// Source regressions for the explicit production NPC movement shell. Raw
/// Entity.Radius remains the gameplay and wire value in every case.
/// </summary>
public static partial class NpcMovementRegressionChecks
{
    [Check("movement-b6-production-npc-pairs-use-full-native-radii")]
    public static void ProductionNpcPairsUseFullNativeRadii()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var movement = runtime.Movement;
        var monster = new Entity(1, EntityKind.Neutral, UnitType.Monster, Team.Neutral, Position2.Zero)
            { Radius = 60 };
        var pet = new Entity(2, EntityKind.Pet, UnitType.Monster, Team.Order, Position2.Zero)
            { Radius = 100 };
        var otherPet = new Entity(3, EntityKind.ShacoClone, UnitType.Monster, Team.Chaos, Position2.Zero)
            { Radius = 80 };
        var minion = new Entity(4, EntityKind.Minion, UnitType.Minion, Team.Order, Position2.Zero)
            { Radius = 35, MinionType = MinionType.Melee };
        var otherMinion = new Entity(5, EntityKind.Minion, UnitType.Minion, Team.Chaos, Position2.Zero)
            { Radius = 35, MinionType = MinionType.Caster };
        var hero = new Entity(6, EntityKind.Hero, UnitType.Hero, Team.Order, Position2.Zero)
            { Radius = 40 };
        // DEV-03: the client's pair rule adds both stored radii and the
        // collider skin, which is clamp(.25 * min(radius), 12, 20).
        // Controlled stored radii isolate the pair rule from CharacterData loading.
        foreach (var (first, second, expected) in new[]
                 {
                     (monster, pet, 175.0), (pet, otherPet, 200.0), (minion, monster, 107.0),
                     (hero, monster, 112.0), (hero, minion, 87.0), (minion, otherMinion, 82.0),
                 })
        {
            TestAssert.Near(expected, movement.BodyClearance(first, second), 1e-9,
                $"{first.Kind}/{second.Kind}: full bodies plus native skin");
            TestAssert.Near(expected, movement.BodyClearance(second, first), 1e-9,
                "pair order does not change clearance");
        }
        TestAssert.Near(1, movement.Settings.NpcToNpcMovementRadiusScale, 0);
        TestAssert.Equal(45, movement.Settings.Collision.CanCollideRepathLimit);
        TestAssert.Equal(15, movement.Settings.Collision.CanCollideRepathLimitFlagged);
    }

    [Check("movement.committed-npc-route-uses-authoritative-anchor")]
    public static void CommittedNpcRouteDoesNotRelocateClientToPlannerCell()
    {
        // Janna native trace, mid lane at movement time 169.1: the planner
        // returned only (8331,8279) while the minion remained at this point.
        var position = new Position2(8327.11293281905, 8240.74845410159);
        var plannerCell = new Position2(8331, 8279);
        var world = new WorldState();
        var minion = world.Register(new Entity(1073807409, EntityKind.Minion,
            UnitType.Minion, Team.Order, position)
        { Alive = true, Speed = 325, Radius = 35.744 });
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
        var stopped = movement.CommitMoveOrder(minion,
            new MovementSystem.MovePlan(plannerCell, [plannerCell],
                MovementSystem.MoveStatus.Unreachable));
        TestAssert.Equal(1, stopped.Count);
        TestAssert.Equal(movement.ClientAnchor(position), stopped[0],
            "a singleton C1 route must anchor at the actual minion");
        // Restated in batch 254: the client seats every actor on the route's first point (its server-path follower),
        // the minion's own lattice anchor; the server's minion stands there too, never on the planner cell.
        TestAssert.Equal(movement.ClientAnchor(position), minion.Position,
            "committing a planner cell seats the server actor on its own anchor, not on the planner cell");
        TestAssert.Equal(movement.ClientAnchor(position), minion.Waypoints[0],
            "the stored route starts on that anchor");

        var endpoint = new Position2(8401, 8291);
        var moving = movement.CommitMoveOrder(minion,
            new MovementSystem.MovePlan(endpoint, [plannerCell, endpoint],
                MovementSystem.MoveStatus.Moving));
        TestAssert.Equal(movement.ClientAnchor(position), moving[0],
            "a moving C1 route starts at the same actor, not the planner cell");
        TestAssert.Equal(endpoint, moving[1],
            "rebasing preserves the already validated route tail");
        TestAssert.Equal(movement.ClientAnchor(position), minion.Waypoints[0],
            "the follower starts on the seated anchor");
    }

    [Check("movement.npc-radius-policy-covers-mobile-roles")]
    public static void MobileNpcRolesUseOneMovementRadiusPolicy()
    {
        var world = new WorldState();
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault(),
            new MovementSettings { NpcMovementRadiusScale = 0.5 });
        var roles = new[]
        {
            (EntityKind.Minion, UnitType.Minion),
            (EntityKind.Neutral, UnitType.Monster),
            (EntityKind.Pet, UnitType.Monster),
            (EntityKind.ShacoClone, UnitType.Monster),
            (EntityKind.YorickGhoul, UnitType.Monster),
            (EntityKind.YorickRevenant, UnitType.Monster),
        };

        var netId = 0x5000_0000u;
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (kind, unitType) in roles)
        {
            var npc = world.Register(new Entity(netId++, kind, unitType, team,
                new Position2(7000 + netId % 100, 7000))
            {
                Alive = true, Speed = 325.0, Radius = 40.0,
            });
            TestAssert.Near(20.0, movement.MovementRadius(npc), 1e-12,
                $"{team}/{kind} uses the half-radius movement shell");
            TestAssert.Near(40.0, npc.Radius, 1e-12,
                $"{team}/{kind} keeps its gameplay radius");
        }

        var hero = world.Register(new Entity(netId++, EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(7000, 7000)) { Alive = true, Speed = 325, Radius = 35 });
        var turret = world.Register(new Entity(netId++, EntityKind.Turret, UnitType.Turret,
            Team.Chaos, new Position2(7200, 7000)) { Alive = true, Speed = 0, Radius = 88 });
        var ward = world.Register(new Entity(netId++, EntityKind.Ward, UnitType.Monster,
            Team.Order, new Position2(7300, 7000)) { Alive = true, Speed = 0, Radius = 20 });
        var trap = world.Register(new Entity(netId++, EntityKind.NidaleeTrap, UnitType.Monster,
            Team.Order, new Position2(7400, 7000)) { Alive = true, Speed = 0, Radius = 20 });
        TestAssert.Near(hero.Radius, movement.MovementRadius(hero), 1e-12,
            "hero movement radius remains native");
        TestAssert.Near(turret.Radius, movement.MovementRadius(turret), 1e-12,
            "turret movement radius remains native");
        TestAssert.Near(ward.Radius, movement.MovementRadius(ward), 1e-12,
            "ward radius remains native");
        TestAssert.Near(trap.Radius, movement.MovementRadius(trap), 1e-12,
            "trap radius remains native");

        var selectedNpc = world.Entities.Values.First(entity => entity.Kind == EntityKind.Minion);
        var rawClearance = hero.Radius + selectedNpc.Radius + movement.Settings.Collision.SkinLow;
        TestAssert.Near(rawClearance - selectedNpc.Radius * 0.5,
            movement.BodyClearance(hero, selectedNpc), 1e-12,
            "hero/NPC clearance scales only the NPC body term");
    }

    [Check("movement.npc-physical-gap-and-closed-wall")]
    public static void ReducedNpcBodiesKeepRealGapsAndRejectClosedWalls()
    {
        var navigation = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var movement = new MovementSystem(world, navigation,
            new MovementSettings
            {
                NpcMovementRadiusScale = 0.5,
                NpcToNpcMovementRadiusScale = 0.75,
            });
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(7000, 7000)) { Alive = true, Radius = 35 });
        var upper = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion,
            Team.Order, Position2.Zero) { Alive = true, Speed = 325, Radius = 40 });
        var lower = world.Register(new Entity(3, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, Position2.Zero) { Alive = true, Speed = 180, Radius = 40 });
        var clearance = movement.BodyClearance(hero, upper);
        TestAssert.Near(67.0, clearance, 1e-12,
            "hero/NPC retains the existing half-shell clearance");
        var center = new Position2(7300, 7000);
        var start = new Position2(center.X - 350, center.Z);
        var goal = new Position2(center.X + 350, center.Z);

        upper.Position = new Position2(center.X, center.Z + clearance + 4.0);
        lower.Position = new Position2(center.X, center.Z - clearance - 4.0);
        var open = new[]
        {
            new NavigationObstacle(upper.Position, clearance),
            new NavigationObstacle(lower.Position, movement.BodyClearance(hero, lower)),
        };
        TestAssert.True(navigation.SegmentClearOfUnits(start, goal, open),
            "the actual eight-unit opening remains passable");

        upper.Position = new Position2(center.X, center.Z + clearance - 4.0);
        lower.Position = new Position2(center.X, center.Z - clearance + 4.0);
        var closed = new[]
        {
            new NavigationObstacle(upper.Position, clearance),
            new NavigationObstacle(lower.Position, movement.BodyClearance(hero, lower)),
        };
        TestAssert.False(navigation.SegmentClearOfUnits(start, goal, closed),
            "the same opening closes when the body shells overlap");

        var npcClearance = movement.BodyClearance(upper, lower);
        TestAssert.Near(72.0, npcClearance, 1e-12,
            "NPC/NPC uses the wider three-quarter movement shell");
        upper.Position = new Position2(center.X, center.Z + npcClearance + 4.0);
        lower.Position = new Position2(center.X, center.Z - npcClearance - 4.0);
        var npcOpen = new[]
        {
            new NavigationObstacle(upper.Position, movement.BodyClearance(upper, lower)),
            new NavigationObstacle(lower.Position, movement.BodyClearance(lower, upper)),
        };
        TestAssert.True(navigation.SegmentClearOfUnits(start, goal, npcOpen),
            "the wider NPC opening remains passable when clear");

        upper.Position = new Position2(center.X, center.Z + npcClearance - 4.0);
        lower.Position = new Position2(center.X, center.Z - npcClearance + 4.0);
        var npcClosed = new[]
        {
            new NavigationObstacle(upper.Position, movement.BodyClearance(upper, lower)),
            new NavigationObstacle(lower.Position, movement.BodyClearance(lower, upper)),
        };
        TestAssert.False(navigation.SegmentClearOfUnits(start, goal, npcClosed),
            "the same opening closes at the wider NPC separation threshold");
    }

    [Check("movement.npc-production-set-order-tick")]
    public static void ProductionNpcOrderMovesThroughTheSharedSolver()
    {
        var navigation = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var mover = world.Register(new Entity(10, EntityKind.Minion, UnitType.Minion,
            Team.Order, new Position2(7000, 7000))
        { Alive = true, Speed = 325.0, Radius = 40.0 });
        var nearby = world.Register(new Entity(11, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, new Position2(7100, 7100))
        { Alive = true, Speed = 180.0, Radius = 40.0 });
        var movement = new MovementSystem(world, navigation,
            new MovementSettings
            {
                NpcMovementRadiusScale = 0.5,
                NpcToNpcMovementRadiusScale = 0.75,
            });
        navigation.FindPathOverride = (start, goal) => [start, goal];

        var before = mover.Position;
        var route = movement.SetMoveOrder(mover.NetId, new Position2(7400, 7000));
        TestAssert.True(route.Count > 1,
            "a production NPC order must install a real route");
        movement.Tick(0.1);
        TestAssert.True(mover.Position.X > before.X,
            "the production NPC route must advance through MovementSystem.Tick");
        TestAssert.True(movement.MovementRadius(nearby) < nearby.Radius,
            "the same production solver must use the reduced nearby NPC shell");
    }

    [Check("neutral.blocked-route-retries-on-source-tick")]
    public static void NeutralBlockedRouteRetriesAfterTheNextNormalDecision()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        runtime.Tick(101.0);
        var monster = runtime.World.Entities.Values.First(entity =>
            entity.Kind == EntityKind.Neutral && entity.Speed > 0.0);
        // The old +2000 X target was behind terrain. Keep a visible target
        // outside attack reach so this isolates retained movement retries.
        PlaceVisibleNeutralTarget(runtime, monster, hero);

        runtime.Navigation.FindPathOverride = (_, _) => [];
        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, monster.NetId, 1.0,
            DamageType.True, DamageSourceKind.Spell),
            "a real combat hit must wake the neutral camp");
        TestAssert.Equal("engage", monster.NeutralState,
            "the neutral must retain its engaged state after a blocked route");
        TestAssert.Equal(0, monster.Waypoints.Count,
            "a blocked neutral route must not retain a singleton movement route");

        runtime.Tick(0.4);
        runtime.Navigation.FindPathOverride = (start, goal) => [start, goal];
        var beforeRetry = monster.Position;
        runtime.Tick(0.2);
        TestAssert.True(monster.Waypoints.Count > 0 &&
            Distance(monster.Position, beforeRetry) > 0.0,
            "the neutral must retry its retained destination and advance on the normal AI tick");
        TestAssert.Equal("engage", monster.NeutralState,
            "the successful neutral retry must keep the engaged decision alive");
        TestAssert.Equal(hero.NetId, monster.AttackTarget,
            "the successful neutral retry must keep the original target");
    }

    [Check("movement.neutral-one-moving-leg-dedup")]
    public static void NeutralSingletonMovingLegDoesNotRepublish()
    {
        var runtime = StartSolo();
        var hero = runtime.Session.Participants.Single().Hero!;
        hero.HeroAutoAcquire = false;
        runtime.Tick(101.0);
        var monster = runtime.World.Entities.Values.First(entity =>
            entity.Kind == EntityKind.Neutral && entity.Speed > 0.0);
        // The old +1000 X target was behind terrain, so the next sight check
        // correctly replaced the route with a retreat.
        PlaceVisibleNeutralTarget(runtime, monster, hero);
        runtime.Navigation.FindPathOverride = (start, goal) => [start, goal];

        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, monster.NetId, 1.0,
            DamageType.True, DamageSourceKind.Spell),
            "the production combat hook must wake the neutral");
        runtime.Tick(0.1);
        TestAssert.Equal(1, monster.Waypoints.Count,
            "movement consumes the current anchor and leaves one moving leg");
        TestAssert.True(Distance(monster.Position, monster.Waypoints[0]) > 3.0,
            "the remaining singleton is distant, so it is still a moving leg");
        var route = monster.Waypoints;

        runtime.Tick(0.4);
        TestAssert.True(ReferenceEquals(route, monster.Waypoints),
            "an unchanged one-leg neutral route is not republished");

        // Batch 230: this section used to hold the route with CongestionWait. For a non-minion that flag was a congestion hold in the client
        // solver (pathFollowerAllowed = false, a NOT RETAIL stand-in), which batch 225 removed (owner: retail even when it measures worse).
        // Without the hold the neutral walks its leg: the 2.5-unit leg is walked inside the decision tick, and the anchor lies inside attack
        // reach, so the neutral stops there and keeps no moving leg. The placement and the decision are unchanged.
        var anchor = monster.Waypoints[0];
        monster.Position = anchor + new Position2(0.0, 2.5);
        monster.NeutralHome = new Position3(monster.Position.X, 0.0, monster.Position.Z);
        // Leashed.luaobj's chase re-paths on the 0.5 s grid (TickPursuit, the stand-in for
        // SetStateAndCloseToTarget's own path request); the hand-written machine's retreat
        // clock this used to align to is gone with it, so the decision is exercised by
        // stepping that grid instead.
        runtime.Tick(Amberline.Simulation.Neutral.NeutralDefinitions.RetreatCheckSeconds);
        TestAssert.Near(0.0, Distance(monster.Position, anchor), 1e-9,
            "the 2.5-unit leg is walked during the decision tick");
        TestAssert.Equal(0, monster.Waypoints.Count,
            "the anchor is inside attack reach, so the neutral stops and keeps no moving leg");

        monster.Position = anchor;
        hero.Position += new Position2(0.0, -10.0);
        var stationaryRoute = monster.Waypoints;
        runtime.Tick(Amberline.Simulation.Neutral.NeutralDefinitions.RetreatCheckSeconds);
        TestAssert.True(monster.Waypoints.Count == 0 ||
            !ReferenceEquals(stationaryRoute, monster.Waypoints),
            "an exact singleton stop anchor is not retained as a moving leg");
    }

    private static void PlaceVisibleNeutralTarget(MatchRuntime runtime, Entity monster, Entity hero)
    {
        hero.Position = monster.Position + new Position2(0.0, -600.0);
        TestAssert.True(runtime.Navigation.IsMovementWalkable(hero.Position) &&
            runtime.Navigation.HasMovementLineOfSight(monster.Position, hero.Position) &&
            runtime.Navigation.HasVisionLos(monster.Position, hero.Position) &&
            runtime.Navigation.BrushAllowsVision(monster.Position, hero.Position),
            "the neutral movement fixture requires a visible target on clear terrain");
        TestAssert.True(Distance(monster.Position, hero.Position) >
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            CombatService.AttackReach(monster, hero) + monster.Speed * 0.6,
            "the target must stay outside attack reach through the retry window");
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Z - second.Z, 2));

    private static MatchRuntime StartSolo()
    {
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Annie", solo: true,
            originalRosterCounts: new SessionRosterCounts(1, 0),
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null, "solo authentication");
        _ = runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1), "solo ready");
        TestAssert.True(runtime.Session.Start(), "solo start");
        return runtime;
    }
}
