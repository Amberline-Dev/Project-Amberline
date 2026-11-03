using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class OvernightStaticBodyMovementChecks
{
    [Check("movement.static-body-first-collision-runtime-and-landing")]
    public static void ForcedCollisionStopsAtTheSameTowerShellUsedByProjection()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cadence in new[] { 0.05, 0.1 })
        foreach (var blocked in new[] { false, true })
        {
            var world = new WorldState();
            var start = new Position2(7000, 7000);
            var requested = new Position2(7600, 7000);
            var mover = world.Register(new Entity(1, EntityKind.Hero,
                UnitType.Hero, team, start) { Alive = true, Speed = 1000, Radius = 40 });
            var tower = world.Register(new Entity(2, EntityKind.Turret,
                UnitType.Turret, Opposite(team), new Position2(7300, 7000))
            { Alive = blocked, Speed = 0, Radius = MapDefinitions.TurretRadius });
            var movement = ProductionMovement(world);
            var announced = movement.FirstCollisionDashLanding(start, requested, 1000, 1, mover);
            var move = new ForcedMoveState(requested, 1000, 1, 3, "test-tower-hit",
                firstCollision: true);
            movement.StartForcedMove(mover.NetId, move);
            for (var tick = 0; tick < 20 && !move.Completed; tick++) movement.Tick(cadence);
            TestAssert.True(move.Completed, "first-collision move terminates");
            if (!blocked)
            {
                TestAssert.True(move.Succeeded, "clear control reaches requested landing");
                TestAssert.Equal(requested, mover.Position, "clear control retains full displacement");
                TestAssert.Equal(requested, announced,
                    "clear dash keeps its original wire endpoint");
                continue;
            }
            TestAssert.False(move.Succeeded, "tower hit retains the collision result for spell callbacks");
            TestAssert.True(Distance(mover.Position, tower.Position) + 1e-6 >=
                movement.BodyClearance(mover, tower) + 4,
                "authoritative move stops outside the same projected tower shell");
            TestAssert.True(Distance(announced, tower.Position) + 1e-6 >=
                movement.BodyClearance(mover, tower) + 4,
                "client landing stops outside the tower shell");
            TestAssert.True(Distance(announced, mover.Position) <=
                movement.Navigation.CellSize + 1,
                "announced and authoritative collision landings differ by at most one cell");
        }
    }

    [Check("movement.first-collision-stops-at-map1-turret-footprint")]
    public static void FirstCollisionStopsAtTheClientTurretStamp()
    {
        // Playtest B2 YV11, 26 Sept 2026: Condemn pushed Yorick 475u toward
        // Turret_T1_C_05 after it died, into the client's 140u turret stamp,
        // where the hero could neither walk out nor route. Dead-turret
        // footprints keep blocking; the live shell check is retained.
        var start = new Position2(5091.6, 5757.5);
        var requested = new Position2(5414.8, 6105.5);
        const double duration = 475.0 / 2000.0 + .05;
        foreach (var cadence in new[] { 0.05, 0.1 })
        foreach (var alive in new[] { false, true })
        {
            var label = $"alive={alive} dt={cadence}";
            var world = new WorldState();
            var mover = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero,
                Team.Chaos, start) { Alive = true, Speed = 325, Radius = 47 });
            world.Register(new Entity(2, EntityKind.Turret, UnitType.Turret, Team.Order,
                MapDefinitions.TurretPositions["Turret_T1_C_05"])
            { Alive = alive, Speed = 0, Radius = MapDefinitions.TurretRadius });
            var movement = ProductionMovement(world);
            var announced = movement.FirstCollisionDashLanding(start, requested, 2000, duration, mover);
            var move = new ForcedMoveState(requested, 2000, duration, 3, "test-condemn",
                firstCollision: true);
            movement.StartForcedMove(mover.NetId, move);
            for (var tick = 0; tick < 20 && !move.Completed; tick++) movement.Tick(cadence);
            TestAssert.True(move.Completed, label + ": first-collision move terminates");
            TestAssert.False(move.Succeeded, label + ": the turret footprint stops the push");
            TestAssert.True(movement.Navigation.IsMovementWalkable(mover.Position),
                $"{label}: authoritative landing is movement floor: {mover.Position}");
            TestAssert.True(movement.Navigation.IsMovementWalkable(announced),
                $"{label}: announced landing is movement floor: {announced}");
            TestAssert.True(Distance(announced, mover.Position) <=
                movement.Navigation.CellSize + 1,
                label + ": announced and authoritative landings differ by at most one cell");
            // Synthetic push has no spell owner tick to retire its completed move.
            mover.ForcedMove = null;
            TestAssert.True(movement.SetMoveOrder(mover.NetId, start).Count > 1,
                label + ": one ordinary order leaves the turret footprint");
        }
    }

    [Check("movement.static-body-furthest-landing-can-leave-tower")]
    public static void FurthestLandingAvoidsTowerWithoutBlockingThinWallTravel()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var start = new Position2(7200, 7000);
            var mover = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero,
                team, start) { Alive = true, Speed = 325, Radius = 40 });
            var tower = world.Register(new Entity(2, EntityKind.Turret, UnitType.Turret,
                Opposite(team), new Position2(8000, 7000))
            { Alive = false, Speed = 0, Radius = MapDefinitions.TurretRadius });
            var movement = ProductionMovement(world);
            var requested = new Position2(8000, 7000);
            tower.Position = movement.FurthestWalkableLanding(start, requested, mover);
            tower.Alive = true;
            var landing = movement.FurthestWalkableLanding(start, requested, mover);
            TestAssert.True(Distance(landing, tower.Position) + 1e-6 >=
                movement.BodyClearance(mover, tower) + 4,
                "a floor cell inside a tower is not a legal kick endpoint");
            var move = new ForcedMoveState(landing, 1000, 4, 3, "test-kick");
            movement.StartForcedMove(mover.NetId, move, cancelOrder: true);
            for (var tick = 0; tick < 100 && !move.Completed; tick++) movement.Tick(.05);
            TestAssert.True(move.Completed && move.Succeeded,
                "non-first-collision kick reaches the projected endpoint");
            TestAssert.Equal(landing, mover.Position, "authoritative landing equals its dash endpoint");
            // Synthetic kick has no spell owner tick to retire its completed move.
            mover.ForcedMove = null;
            var escape = movement.SetMoveOrder(mover.NetId, start);
            TestAssert.True(escape.Count > 1, "one ordinary order leaves the tower boundary");
            var before = Distance(mover.Position, start);
            for (var tick = 0; tick < 10; tick++) movement.Tick(.05);
            TestAssert.True(Distance(mover.Position, start) < before - 1,
                "the post-kick route makes progress without a second click");

            tower.Alive = false;
            mover.Position = new Position2(7395, 7071);
            // The old far-side point (8147, 7345) is only about five units
            // from a blocked Map1 cell, so a 40-unit hero cannot land there.
            var acrossWall = new Position2(8300, 7400);
            var wallTravel = acrossWall - mover.Position;
            TestAssert.True(Enumerable.Range(1, 79).Any(step =>
                !movement.Navigation.IsWalkable(mover.Position +
                    new Position2(wallTravel.X * step / 80.0,
                        wallTravel.Z * step / 80.0))),
                "thin-wall control has intervening blocked terrain");
            TestAssert.True(movement.Navigation.PointClearance(acrossWall, mover.Radius + 4) >=
                mover.Radius + 4, "far-side control clears the hero body from terrain");
            var farSide = movement.FurthestWalkableLanding(mover.Position, acrossWall, mover);
            TestAssert.True(Distance(farSide, acrossWall) <= 50,
                "FURTHEST_WITHIN_RANGE still permits a clear far-side landing");
        }
    }

    [Check("movement.pet-walks-past-a-static-tower")]
    public static void PetWalksPastAStaticTower()
    {
        // Restated in batch 254 (was movement.body-aware-pet-avoids-static-tower): a pet's order is the engine planner's route
        // (the client's order handler and path builder), which plans around the turret actor; one order still
        // walks the pet past the tower, a ghosted pet too (it passes mobile bodies, not a turret), and without the tower the route is the
        // planner's straight one.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var world = new WorldState();
            var start = new Position2(7000, 7000);
            var pet = world.Register(new Entity(1, EntityKind.Pet, UnitType.Monster,
                team, start) { Alive = true, Speed = 350, Radius = 40 });
            var tower = world.Register(new Entity(2, EntityKind.Turret, UnitType.Turret,
                Opposite(team), new Position2(7300, 7000))
            { Alive = true, Speed = 0, Radius = MapDefinitions.TurretRadius });
            var movement = ProductionMovement(world);
            var goal = new Position2(7600, 7000);
            var engine = movement.PlanClientHeroRoute(pet, goal, static _ => true);
            var route = movement.SetMoveOrder(pet.NetId, goal);
            EnginePlannerRoutes.AssertSame(engine, route, "the pet's order around the tower");
            for (var tick = 0; tick < 100 && Distance(pet.Position, goal) > 5; tick++)
                movement.Tick(.05);
            TestAssert.True(Distance(pet.Position, goal) <= 5,
                "one order reaches the goal past the tower");
            TestAssert.True(Distance(pet.Position, tower.Position) >= movement.MovementRadius(tower),
                "the pet ends outside the tower");

            pet.Position = start;
            pet.Waypoints.Clear();
            pet.Buffs.Add(new BuffState("Ghost", new BuffPayload { Ghosted = true }));
            var ghostEngine = movement.PlanClientHeroRoute(pet, goal, static _ => true);
            var ghostRoute = movement.SetMoveOrder(pet.NetId, goal);
            EnginePlannerRoutes.AssertSame(ghostEngine, ghostRoute, "the ghosted pet's order");
            for (var tick = 0; tick < 100 && Distance(pet.Position, goal) > 5; tick++)
                movement.Tick(.05);
            TestAssert.True(Distance(pet.Position, goal) <= 5,
                "a ghosted pet's order reaches the goal past the tower it collides with");

            tower.Alive = false;
            pet.Position = start;
            pet.Waypoints.Clear();
            var clear = movement.SetMoveOrder(pet.NetId, goal).ToArray();
            TestAssert.Equal(2, clear.Length, "removing the tower restores the straight route");
            TestAssert.Equal(movement.ClientAnchor(goal), clear[^1], "to the goal");
        }
    }

    [Check("movement.furthest-within-range-keeps-a-request-shorter-than-one-lattice-step")]
    public static void FurthestLandingAcceptsOneLatticeStepOfOutwardRounding()
    {
        // Batch 168 finding 25 (Xin Zhao's Audacious Charge up close): the client keeps every waypoint on a 2-unit lattice
        // (Geometry.ClientWaypoints, the client's lattice rule), so the quantised point of the request may sit up to one step per axis, about
        // 2.83 units, away from it. The search used to allow no outward step at all, found no candidate for a request shorter than
        // one step and returned the raw origin, which made the script's move zero-length (no dash packet, no dash animation).
        var world = new WorldState();
        var mover = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(7000, 7000)) { Alive = true, Speed = 3000, Radius = 40 });
        var movement = ProductionMovement(world);
        var origin = new Position2(7000, 7000);
        foreach (var gap in new[] { 1.0, 2.0, 3.0, 5.0, 10.0 })
        {
            var requested = origin + new Position2(gap, 0);
            var landing = movement.FurthestWalkableLanding(origin, requested, mover);
            TestAssert.True(Distance(origin, landing) > 0.0, $"gap {gap}: the landing is not the origin (a zero-length move)");
            TestAssert.True(movement.Navigation.IsMovementWalkable(landing), $"gap {gap}: the landing is movement floor");
            TestAssert.True(Distance(origin, landing) <= gap + 2.0 + 1e-6,
                $"gap {gap}: the landing is at most one lattice step beyond the request ({Distance(origin, landing):0.00})");
        }
        // A request over a wall still backs off: the guard is one lattice step, not "any farther point".
        var blocked = default(Position2?);
        var grid = movement.Navigation;
        for (var column = 100; column < grid.CountX - 100 && blocked is null; column++)
        for (var row = 100; row < grid.CountY - 100 && blocked is null; row++)
        {
            var start = grid.CellToWorld(new NavCell(column, row));
            if (!grid.IsMovementWalkable(start)) continue;
            var far = start + new Position2(30, 0);
            if (grid.IsMovementWalkable(far)) continue;
            blocked = start;
            mover.Position = start;
            var landing = movement.FurthestWalkableLanding(start, far, mover);
            TestAssert.True(Distance(start, landing) <= 30.0 + 2.0 + 1e-6,
                "a blocked request still cannot land farther than its own length plus one lattice step");
        }
        TestAssert.True(blocked is not null, "Map1 has a walkable cell with a blocked point 30 units east of it");
    }

    private static Team Opposite(Team team) => team == Team.Order ? Team.Chaos : Team.Order;
    private static MovementSystem ProductionMovement(WorldState world) =>
        new(world, NavigationGrid.LoadDefault(), new MovementSettings
        {
            NpcMovementRadiusScale = .5,
            NpcToNpcMovementRadiusScale = .75,
            MinionUnitCollisionScale = .9,
        });
    private static double Distance(Position2 a, Position2 b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}
