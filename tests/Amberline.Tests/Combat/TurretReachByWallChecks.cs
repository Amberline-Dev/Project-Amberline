using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Batch 702 (community report, 7 November 2026: on the autumn rift a champion beside a wall, well inside the turret's range circle, is
/// not shot). The turrets stand where the client draws them (every map reads its own scene; Map2's turret meshes and grid are Map1's
/// bytes) and reach 750 plus the target's radius, so the cause was the sight gate: the turret's own line-of-sight test refused a
/// champion with a wall between him and the turret even while the turret's team had him on screen. Each check runs a production match
/// (the composition wires the team's sight), so it also proves the wiring.
/// </summary>
public static class TurretReachByWallChecks
{
    private const double Step = 1.0 / 30.0;

    /// <summary>
    /// Every lane turret of every map, against an enemy champion on every open, walkable 25-unit lattice point inside its attack reach
    /// (past its own 200-unit body), with a champion of the turret's team beside him, so the team sees him: the turret takes him.
    /// Before the batch the turret's own test refused 71 such points at Turret_T1_C_04 and 119 at Twisted Treeline's Turret_T1_L_02.
    /// </summary>
    [Check("combat.turret-shoots-champion-its-team-sees-beside-wall-every-map", Serial = true, TimeoutSeconds = 600)]
    public static void TurretShootsChampionItsTeamSeesOnEveryMap()
    {
        var failures = new List<string>();
        try
        {
            foreach (var mapId in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.Tutorial, GameMaps.TwistedTreeline, GameMaps.CrystalScar })
            {
                using var runtime = StartTwoSeatMatch(mapId);
                var grid = NavigationGrid.Load(GameDataPaths.Default.ForMap(mapId));
                foreach (var turret in LaneTurrets(runtime))
                {
                    var (enemy, ally) = Heroes(runtime, turret);
                    var reach = CombatService.AttackReach(turret, enemy);
                    int total = 0, missed = 0;
                    for (var dx = -reach; dx <= reach; dx += 25.0)
                    for (var dz = -reach; dz <= reach; dz += 25.0)
                    {
                        var point = turret.Position + new Position2(dx, dz);
                        var distance = Math.Sqrt(dx * dx + dz * dz);
                        if (distance > reach || distance < 200.0 || !grid.IsMovementWalkable(point) ||
                            grid.BrushIdAt(point, enemy.SelectionRadius) is not null)
                            continue;
                        enemy.Position = ally.Position = point;
                        total++;
                        if (runtime.Combat.AcquireTurretTarget(turret) != enemy) missed++;
                    }
                    if (missed > 0) failures.Add($"map {mapId} {turret.Name}: {missed}/{total} seen points not taken");
                }
            }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
        TestAssert.True(failures.Count == 0, "the turret takes every champion its team sees in reach: " + string.Join(" | ", failures));
    }

    /// <summary>
    /// The report's kind of spot on the autumn rift (map 2): beside the wall at the bottom lane's jungle entrance, 650 units from the
    /// Order bottom inner turret (inside its 818 reach), a wall corner between him and the turret. An Order champion 150 units west, in
    /// the open, sees him; the turret takes him and fires.
    /// </summary>
    [Check("combat.turret-shoots-champion-beside-bottom-lane-wall-autumn", Serial = true, TimeoutSeconds = 120)]
    public static void TurretShootsChampionBesideBottomLaneWallOnTheAutumnRift()
    {
        try
        {
            using var runtime = StartTwoSeatMatch(GameMaps.Harrowing);
            var grid = NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.Harrowing));
            var turret = LaneTurrets(runtime).Single(unit => unit.Name == "Turret_T1_R_02");
            var (enemy, ally) = Heroes(runtime, turret);
            var spot = new Position2(5970, 1620);
            var beside = spot + new Position2(-150, 0);
            enemy.Position = spot;
            TestAssert.True(grid.IsMovementWalkable(spot) && grid.BrushIdAt(spot, enemy.SelectionRadius) is null, "the spot is open floor");
            TestAssert.True(CombatService.InRange(turret, enemy), "the spot is inside the turret's attack reach");
            var (selection, gameplay) = VisibilityRules.TurretBody(turret);
            TestAssert.False(grid.TurretLineOfSightTest(spot, enemy.SelectionRadius, null, turret.Position, selection, gameplay),
                "the turret's own line-of-sight test is blocked by the wall");
            TestAssert.True(grid.IsMovementWalkable(beside) &&
                grid.HasUnitVision(beside, ally.SelectionRadius, spot, enemy.SelectionRadius), "the Order champion beside him sees him");
            ally.Position = beside;

            TestAssert.Equal(enemy, runtime.Combat.AcquireTurretTarget(turret), "the turret takes the champion its team sees");
            var shot = false;
            for (var tick = 0; tick < 90 && !shot; tick++)
            {
                enemy.Position = spot;
                ally.Position = beside;
                var health = enemy.Health;
                runtime.Tick(Step);
                shot = enemy.Health < health;
            }
            TestAssert.True(shot, "the turret's shot lands on the champion within three seconds");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static Entity[] LaneTurrets(MatchRuntime runtime) => runtime.World.Entities.Values
        .Where(unit => unit.UnitType == UnitType.Turret && MapDefinitions.Layout.TurretTiers.ContainsKey(unit.Name) &&
            !TurretRules.IsShrineTurret(unit))
        .OrderBy(unit => unit.Name, StringComparer.Ordinal).ToArray();

    private static (Entity Enemy, Entity Ally) Heroes(MatchRuntime runtime, Entity turret) =>
        (runtime.World.Heroes().Single(hero => hero.Team != turret.Team), runtime.World.Heroes().Single(hero => hero.Team == turret.Team));

    private static MatchRuntime StartTwoSeatMatch(int mapId)
    {
        var paths = GameDataPaths.Default.ForMap(mapId);
        GameData.ConfigureDefault(paths);
        SessionParticipantDefinition[] roster = [new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos)];
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(paths), GameData.Default, readySettleSeconds: 0.0,
            roster: roster, mapId: mapId);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            TestAssert.True(runtime.Session.Authenticate(100L + index, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"map {mapId} seat {index} authenticates");
            runtime.Session.Spawn(100L + index);
            TestAssert.True(runtime.Session.MarkReady(100L + index), $"map {mapId} seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, $"map {mapId} starts");
        runtime.Tick(Step);
        return runtime;
    }
}
