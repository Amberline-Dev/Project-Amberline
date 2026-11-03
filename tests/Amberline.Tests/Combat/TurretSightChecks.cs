using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

/// <summary>
/// C08 (batch 211, restated by batch 244): a turret targets what the client's own turret sight test sees,
/// the turret's line-of-sight test with the candidate as the viewer. Batch 211 tested sight only past
/// <c>ser_ClosenessLineOfSightThresholdTurret</c> (200); that constant is a "Server Culling" value the client never reads, and the
/// client test already passes every viewer within about 357 units (SelectionRadius 130 plus the walk's 226.8 start offset), so the
/// threshold is gone. The geometry is Map1's own terrain, found by scanning the navigation grid. Each check asserts its terrain
/// premise before it checks the behaviour, so a changed grid fails on the premise, not on a misleading acquisition result.
/// </summary>
public static class TurretSightChecks
{
    // A walkable Map1 point with terrain east of it on the centre line only (z 0..50, x +30..+170).
    private static readonly Position2 WallOrigin = new(6900, 7800);
    // A walkable Map1 point with a thick wall east of it (x +30..+200 for z -200..+200): the walk reaches past it from 250 units east,
    // not from 450. The turret is the Order outer turret's tier and team, placed here for the check.
    private static readonly Position2 ThickWallOrigin = new(2400, 6600);

    [Check("combat.turret-sight-refuses-wall-beyond-start-offset")]
    public static void TargetBehindWallBeyondStartOffsetIsRefused()
    {
        var grid = NavigationGrid.LoadDefault();
        var far = ThickWallOrigin + new Position2(450, 0);
        TestAssert.True(grid.IsMovementWalkable(far) && grid.BrushIdAt(far, 100) is null, "the far point is open floor");
        foreach (var side in new[] { -100.0, 0.0, 100.0 })
            TestAssert.True(TerrainBetween(grid, ThickWallOrigin + new Position2(0, side), far + new Position2(0, side)),
                $"terrain sits between the turret and the far point ({side} to the side)");
        var world = new WorldState();
        var turret = Turret(world, "Turret_T1_C_05", ThickWallOrigin);
        var hero = Hero(world, far);
        TestAssert.True(CombatService.InAcquisitionRange(turret, hero), "the far point is in acquisition range");
        var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
            new FixedRandomSource(.5));
        TestAssert.True(combat.AcquireTurretTarget(turret) is null,
            "a target 450 units away behind a thick wall is refused (beyond the walk's start offset)");
    }

    // Batch 211 accepted a wall target only within 200 and refused one 250 units out; the client test starts its walk 226.8 units out
    // from the viewer, past this wall, so the 250-unit target is a legal target now.
    [Check("combat.turret-sight-accepts-wall-within-start-offset")]
    public static void TargetBehindWallWithinStartOffsetIsAccepted()
    {
        var grid = NavigationGrid.LoadDefault();
        var near = ThickWallOrigin + new Position2(250, 0);
        TestAssert.True(grid.IsMovementWalkable(near) && grid.BrushIdAt(near, 100) is null, "the near point is open floor");
        TestAssert.True(TerrainBetween(grid, ThickWallOrigin, near), "terrain sits between the turret and the near point");
        var world = new WorldState();
        var turret = Turret(world, "Turret_T1_C_05", ThickWallOrigin);
        var hero = Hero(world, near);
        var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
            new FixedRandomSource(.5));
        TestAssert.Equal(hero, combat.AcquireTurretTarget(turret),
            "a target 250 units away behind a wall is accepted (the walk starts past the wall)");
    }

    // The candidate is the viewer: one standing in brush looks out of its own brush (the walk keeps the allowance within its
    // SelectionRadius, then crosses open floor), so the turret targets it. Batch 211's fog test from the turret refused a target in brush.
    [Check("combat.turret-sight-acquires-target-in-brush")]
    public static void TargetInBrushIsAcquired()
    {
        var grid = NavigationGrid.LoadDefault();
        var brush = new Position2(1700, 9250);
        var origin = brush + new Position2(-400, 0);
        TestAssert.True(grid.IsMovementWalkable(brush) && grid.BrushIdAt(brush, 100) is not null, "the target stands in brush");
        TestAssert.True(grid.IsMovementWalkable(origin) && grid.BrushIdAt(origin, 130) is null, "the turret stands out of brush");
        var world = new WorldState();
        var turret = Turret(world, "Turret_T1_C_05", origin);
        var hero = Hero(world, brush);
        hero.SelectionRadius = 100;
        var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
            new FixedRandomSource(.5));
        TestAssert.Equal(hero, combat.AcquireTurretTarget(turret), "a target in brush 400 units away is acquired");
    }

    [Check("combat.turret-sight-open-lane-target-unchanged")]
    public static void OpenLaneTargetBeyondThresholdIsStillAcquired()
    {
        var grid = NavigationGrid.LoadDefault();
        var lane = MapDefinitions.Turrets.Single(t => t.Name == "Turret_T1_C_05").Position;
        var minionPoint = lane + new Position2(400, 0);
        TestAssert.True(grid.IsMovementWalkable(minionPoint), "the lane point is walkable");
        TestAssert.False(TerrainBetween(grid, lane, minionPoint), "no terrain between the lane turret and the minion");
        var world = new WorldState();
        var turret = Turret(world, "Turret_T1_C_05", lane);
        var minion = world.Register(new Entity(2, EntityKind.Minion, UnitType.Minion,
            MinionDefinitions.Opposing(Team.Order), minionPoint) { Health = 500, MaxHealth = 500 });
        var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
            new FixedRandomSource(.5));
        TestAssert.Equal(minion, combat.AcquireTurretTarget(turret),
            "a minion 400 units away along an open lane is acquired, as before the sight test");
    }

    [Check("combat.turret-sight-fountain-open-direction")]
    public static void FountainKeepsItsOpenDirectionBeyondThreshold()
    {
        var grid = NavigationGrid.LoadDefault();
        // The fountain stands in the corner terrain. Its body (the 140-unit footprint the movement grid stamps for a turret) is
        // not a wall of its own, so TerrainBetween starts past it; the open direction is 45 degrees.
        var shrine = MapDefinitions.Turrets.Single(t => t.Name == "Turret_OrderTurretShrine").Position;
        var target = shrine + new Position2(800 * Math.Cos(Math.PI / 4), 800 * Math.Sin(Math.PI / 4));
        TestAssert.True(grid.IsMovementWalkable(target), "the target point is walkable");
        TestAssert.False(TerrainBetween(grid, shrine, target), "no terrain beyond the body between the fountain and the target");
        var world = new WorldState();
        var turret = Turret(world, "Turret_OrderTurretShrine", shrine);
        var hero = Hero(world, target);
        var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
            new FixedRandomSource(.5));
        TestAssert.Equal(hero, combat.AcquireTurretTarget(turret),
            "the fountain acquires an 800-unit target in its open direction");
    }

    // Batch 211 follow-up: the fountain keeps its acquisition (owner, 25 October 2026: exempt from the sight test). Batch 244: the client's
    // turret test fails at all three of these base positions (the shrine mesh is opaque past the turret stamp), so the exemption stays.
    // A fountain that stopped shooting its own base would be a regression, so these base positions must stay targets.
    [Check("combat.turret-sight-fountain-shoots-base-floor")]
    public static void FountainShootsEnemiesOnItsBaseFloor()
    {
        var grid = NavigationGrid.LoadDefault();
        var shrine = MapDefinitions.Turrets.Single(t => t.Name == "Turret_OrderTurretShrine").Position;
        var inhibitorSide = MapDefinitions.Turrets.Single(t => t.Name == "Turret_T1_C_07").Position;
        var spawnEdge = new Position2(MapDefinitions.HeroSpawn.X + 300.0, MapDefinitions.HeroSpawn.Z);
        var entrance = shrine + new Position2(800 * Math.Cos(20 * Math.PI / 180.0), 800 * Math.Sin(20 * Math.PI / 180.0));
        // The inhibitor side: 900 units from the spawn marker toward the Order base's inhibitor turret T1_C_07 (open floor).
        var marker = new Position2(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);
        var towardInhibitor = Math.Atan2(inhibitorSide.Z - marker.Z, inhibitorSide.X - marker.X);
        var inhibitorBase = marker + new Position2(900 * Math.Cos(towardInhibitor), 900 * Math.Sin(towardInhibitor));
        foreach (var point in new[] { spawnEdge, entrance, inhibitorBase })
        {
            TestAssert.True(grid.IsMovementWalkable(point), $"base floor point {point} is walkable");
            TestAssert.True(Math.Sqrt((point.X - shrine.X) * (point.X - shrine.X) + (point.Z - shrine.Z) * (point.Z - shrine.Z)) <= 2250.0,
                $"base floor point {point} is within the fountain's 2250 range");
            var world = new WorldState();
            var turret = Turret(world, "Turret_OrderTurretShrine", shrine);
            var hero = Hero(world, point);
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
                new FixedRandomSource(.5));
            TestAssert.Equal(hero, combat.AcquireTurretTarget(turret), $"the fountain acquires an enemy hero at {point}");
        }
    }

    // Batch 244: who sees a turret is decided by the turret's line-of-sight test: two columns across the turret's
    // SelectionRadius (130), each walked from the viewer starting 2 * GameplayCollisionRadius + 50 = 226.8 units out. WallOrigin's
    // wall lies on the centre line only (z 0..50), so the column 91 units to the side passes it; the old footprint-edge ray did not.
    [Check("replication.turret-sight-sees-past-a-wall-corner")]
    public static void TurretIsSeenPastAWallCornerByItsColumns()
    {
        var grid = NavigationGrid.LoadDefault();
        var viewer = WallOrigin + new Position2(500, 0);
        TestAssert.True(grid.IsMovementWalkable(viewer) && grid.BrushIdAt(viewer, 100) is null, "the viewer stands on open floor");
        TestAssert.True(TerrainBetween(grid, WallOrigin, viewer), "terrain sits on the centre line");
        TestAssert.False(TerrainBetween(grid, WallOrigin + new Position2(0, -91), viewer + new Position2(0, -91), bodyRadius: 0.0),
            "the line 91 units to the side is clear");
        var world = new WorldState();
        var turret = Turret(world, "Turret_T1_C_05", WallOrigin);
        TestAssert.True(VisibilityRules.RevealsEntity(world, Team.Chaos, new NavigationGridVisionEnvironment(grid),
                [new VisionProvider(viewer, 1350, 100)], _ => false, turret),
            "an enemy 500 units east sees the turret through its side column");
    }

    // The fountain is an obj_AI_Turret too. Its shrine mesh is opaque in the shipped grid beyond the 140-unit turret stamp the client
    // writes (flag 0x42, see-through), so an enemy 412 units out on the base floor does not see the shrine: the walk ends 130 units
    // short of the column centre, inside that mesh. The old rule saw the shrine from its team's fountain marker.
    [Check("replication.turret-sight-fountain-mesh-hides-the-shrine")]
    public static void FountainMeshHidesTheShrineFromPartOfItsFloor()
    {
        var grid = NavigationGrid.LoadDefault();
        var shrine = MapDefinitions.Turrets.Single(t => t.Name == "Turret_OrderTurretShrine").Position;
        var viewer = shrine + new Position2(400, 100);
        TestAssert.True(grid.IsMovementWalkable(viewer) && grid.BrushIdAt(viewer, 100) is null, "the viewer stands on open floor");
        TestAssert.True(grid.IsMovementWalkable(shrine + new Position2(300, 100)), "the near viewer stands on the floor");
        var world = new WorldState();
        var turret = Turret(world, "Turret_OrderTurretShrine", shrine);
        TestAssert.False(VisibilityRules.RevealsEntity(world, Team.Chaos, new NavigationGridVisionEnvironment(grid),
                [new VisionProvider(viewer, 1350, 100)], _ => false, turret),
            "an enemy 412 units from the shrine does not see it");
        TestAssert.True(VisibilityRules.RevealsEntity(world, Team.Chaos, new NavigationGridVisionEnvironment(grid),
                [new VisionProvider(shrine + new Position2(300, 100), 1350, 100)], _ => false, turret),
            "an enemy 316 units out is inside the walk's start offset and sees it");
    }

    private static Entity Turret(WorldState world, string name, Position2 position)
    {
        var turret = world.Register(new Entity(1, EntityKind.Turret, UnitType.Turret, Team.Order, position) { Name = name });
        TurretRules.Initialize(turret, 1, GameData.Default);
        return turret;
    }

    private static Entity Hero(WorldState world, Position2 position) =>
        world.Register(new Entity(3, EntityKind.Hero, UnitType.Hero, MinionDefinitions.Opposing(Team.Order), position)
        { Health = 5000, MaxHealth = 5000, Radius = 65 });

    // Samples the segment past the turret's own body and reports whether any sample is inside the grid and in terrain that blocks sight.
    // The sampling is an independent check of the cell walk the engine uses.
    private static bool TerrainBetween(NavigationGrid grid, Position2 from, Position2 to, double bodyRadius = 140.0)
    {
        var length = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
        for (var along = bodyRadius; along < length; along += 10.0)
        {
            var t = along / length;
            var cell = grid.WorldToCell(new Position2(from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t));
            if (grid.IsInBounds(cell) && !grid.IsVisionClear(cell)) return true;
        }
        return false;
    }

    private sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
