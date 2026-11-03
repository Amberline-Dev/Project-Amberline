using System.Globalization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Runtime;

namespace Amberline.Tests;

/// <summary>
/// Batch 451 part 2 step 1, restated by batch 499: map 8's Odin minion waves (SmallWave) come from the shipped SpawnMinionWave tree at the
/// shipped cadence (NextWaveSpawnTime, WaveSpawnPeriod 25; OdinScriptBehaviorTrees.xml:2039-2048): one squad per lane of each held point toward
/// a neighbour its team does not hold, on the lane's spawn marker (OdinWaves.Lanes), its units 0.75 s apart (SpawnDelay).
/// </summary>
public static class BehaviorTreeWaveChecks
{
    private const double Step = 1.0 / 30.0;

    /// <summary>
    /// No wave while every point is neutral; once Order holds A and Chaos holds E, the next period spawns both teams' waves: Order on A1 (toward
    /// B) and A2 (toward E), Chaos on E1 (toward A) and E2 (toward D), each two casters and a superminion.
    /// </summary>
    [Check("scripting.behaviour-tree-map8-first-wave-spawns-both-teams")]
    public static void MapEightFirstWaveSpawnsForBothTeams()
    {
        var world = new WorldState();
        var spawned = new List<Entity>();
        var host = MapEightHost(world, spawned);
        host.Tick(Step, Step);
        TestAssert.Equal(0, WaveUnits(spawned, Team.Order).Count + WaveUnits(spawned, Team.Chaos).Count, "no wave while every point is neutral");
        Hold(host, 0, Team.Order);
        Hold(host, 4, Team.Chaos);
        for (var tick = 2; tick <= (int)(27.0 / Step); tick++) host.Tick(tick * Step, Step);

        AssertWaves(spawned, Team.Order, 1, Marker("Info_MinionSpawnA1"));
        AssertWaves(spawned, Team.Order, 1, Marker("Info_MinionSpawnA2"));
        AssertWaves(spawned, Team.Chaos, 1, Marker("Info_MinionSpawnE1"));
        AssertWaves(spawned, Team.Chaos, 1, Marker("Info_MinionSpawnE2"));
        TestAssert.Equal(12, WaveUnits(spawned, Team.Order).Count + WaveUnits(spawned, Team.Chaos).Count, "four lanes of three units");
    }

    /// <summary>The cadence: a second wave 25 s after the first (WaveSpawnPeriod), and no third before 75 s.</summary>
    [Check("scripting.behaviour-tree-map8-waves-follow-wave-spawn-period")]
    public static void MapEightWavesFollowTheSpawnPeriod()
    {
        var world = new WorldState();
        var spawned = new List<Entity>();
        var host = MapEightHost(world, spawned);
        var orderSpawn = Marker("Info_MinionSpawnA1");
        host.Tick(Step, Step);
        Hold(host, 0, Team.Order);

        for (var tick = 2; tick <= (int)(27.0 / Step); tick++) host.Tick(tick * Step, Step);
        AssertWaves(spawned, Team.Order, 1, orderSpawn);

        for (var tick = (int)(27.0 / Step) + 1; tick <= (int)(52.0 / Step); tick++) host.Tick(tick * Step, Step);
        AssertWaves(spawned, Team.Order, 2, orderSpawn);

        for (var tick = (int)(52.0 / Step) + 1; tick <= (int)(74.5 / Step); tick++) host.Tick(tick * Step, Step);
        AssertWaves(spawned, Team.Order, 2, orderSpawn);
    }

    // A point held by a team, as the quest checks set it (DominionQuestChecks): owner and full progress.
    private static void Hold(BehaviorTreeQuestHost host, int point, Team team)
    {
        host.Captures[point].SetVariable("CurrentPointOwner", team);
        host.Captures[point].SetVariable("CaptureProgress", team == Team.Order ? 100.0 : -100.0);
    }

    private static Position3 Marker(string name) =>
        SceneMesh.CentralPoint(Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene", name + ".SCB"));

    private static List<Entity> WaveUnits(List<Entity> spawned, Team team) =>
        spawned.Where(unit => unit.Team == team && unit.Name is "Odin_Blue_Minion_Caster" or "OdinBlueSuperminion" or "Odin_Red_Minion_Caster"
            or "OdinRedSuperminion").ToList();

    /// <summary>Batch 451 step 3: a minion of a team standing on a neutral point moves that point's capture progress (EvaluateCapturePoint).</summary>
    [Check("scripting.behaviour-tree-map8-minion-on-neutral-point-moves-its-capture")]
    public static void MapEightMinionOnNeutralPointMovesItsCapture()
    {
        var world = new WorldState();
        var spawned = new List<Entity>();
        var host = MapEightHost(world, spawned);
        host.Tick(Step, Step);
        var capture = host.Captures[0];
        var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
        var centre = SceneMesh.CentralPoint(Path.Combine(scene, "Info_PointA.SCB"));
        // A wave squad on the point: the point kills its squad minions and that kill is the progress (OdinScriptBehaviorTrees.xml:928-967).
        var encounters = host.Context.Encounters ?? throw new InvalidOperationException("map 8 has an encounter service");
        var encounter = encounters.CreateEncounter("SmallWave", 1, "") ?? throw new InvalidOperationException("SmallWave ships");
        encounters.SpawnSquad(encounter, new Position2(centre.X, centre.Z), Team.Order, "waveSpawn");

        var before = Convert.ToDouble(capture.Variable("CaptureProgress") ?? 0.0, CultureInfo.InvariantCulture);
        for (var tick = 2; tick <= 31; tick++) host.Tick(tick * Step, Step);
        var after = Convert.ToDouble(capture.Variable("CaptureProgress") ?? 0.0, CultureInfo.InvariantCulture);
        TestAssert.True(spawned.Count >= 3 && after > before,
            $"an Order wave squad on neutral point A raises its progress ({before} to {after})");
    }

    private static BehaviorTreeQuestHost MapEightHost(WorldState world, List<Entity> spawned) =>
        BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world, null, null,
            (skin, at, team) => TestSpawn(world, spawned, skin, at, team, UnitType.Monster),
            // The kill hook is what production passes (KillUnit fails without one); the test's kill leaves the unit in place.
            hooks: new CapturePointHooks(Kill: (_, _) => { }),
            spawnMinion: (skin, at, team) => TestSpawn(world, spawned, skin, at, team, UnitType.Minion))
        ?? throw new InvalidOperationException("map 8 has a quest host");

    /// <summary>
    /// Batch 451 part 2, restated by batch 499: with Order holding point A, the first Order wave on A1 leaves its marker, walks lane 3
    /// (__NAV_3) on its shipped Lua AI (MinionOdin.luaobj on the lane engine, the map's own grid) and comes within 600 of point B within 90 s,
    /// with no script failure; the A2 wave runs MinionReverse (its lane is walked backward).
    /// </summary>
    [Check("scripting.behaviour-tree-map8-wave-minions-walk-to-a-capture-point")]
    public static void MapEightWaveMinionsWalkToACapturePoint()
    {
        var paths = GameDataPaths.Default.ForMap(GameMaps.CrystalScar);
        var grid = NavigationGrid.Load(paths);
        var world = new WorldState();
        var movement = new MovementSystem(world, grid);
        var ai = new MinionAiSystem(world, grid, movement, new WaveCombat(), new WavePackets(), new WaveVision());
        var failures = new List<AiScriptFailure>();
        ai.Host.OnError = failures.Add;
        var spawned = new List<Entity>();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, paths, world, null, null,
            (skin, at, team) => TestSpawn(world, spawned, skin, at, team, UnitType.Monster),
            hooks: new CapturePointHooks(Kill: (_, _) => { }),
            spawnMinion: (skin, at, team) => TestSpawn(world, spawned, skin, at, team, UnitType.Minion),
            navigation: grid, minions: ai)
        ?? throw new InvalidOperationException("map 8 has a quest host");

        var marker = Marker("Info_MinionSpawnA1");
        var goal = Marker("Info_PointB");
        var spawnAt = new Position2(marker.X, marker.Z);
        var goalAt = new Position2(goal.X, goal.Z);
        var closest = double.MaxValue;

        // The match's own order of a step: the clock, the AI, then the movement of the non-heroes (ProductionMatchComposition).
        for (var tick = 1; tick <= 90 * 30; tick++)
        {
            host.Tick(tick * Step, Step);
            if (tick == 1) Hold(host, 0, Team.Order);
            movement.AdvanceSimulationTime(Step);
            ai.Tick(Step);
            movement.TickNonHeroes();
            foreach (var unit in spawned.Where(unit => host.Context.Encounters?.LaneOf(unit) is { Lane: 3, Reverse: false }))
                closest = Math.Min(closest, Distance(unit.Position, goalAt));
        }

        // The first A1 squad: its three units (the second comes 25 s later); the lane of a unit is its squad's SquadPushLane.
        var lanes = host.Context.Encounters ?? throw new InvalidOperationException("map 8 has an encounter service");
        var wave = spawned.Where(unit => lanes.LaneOf(unit) is { Lane: 3, Reverse: false }).Take(3).ToList();
        TestAssert.Equal(3, wave.Count, "the first A1 wave is two casters and a superminion");
        TestAssert.True(wave.All(unit => unit.AiScript == "MinionOdin" && unit.UnitType == UnitType.Minion),
            "each forward wave unit is a minion running MinionOdin");
        TestAssert.True(spawned.Where(unit => lanes.LaneOf(unit) is { Lane: 7, Reverse: true }).All(unit => unit.AiScript == "MinionReverse"),
            "each A2 wave unit runs MinionReverse");
        TestAssert.True(wave.Any(unit => Distance(unit.Position, spawnAt) > 600.0),
            $"a wave unit leaves its marker (state={wave[0].MinionState} wp={wave[0].Waypoints.Count} lane={wave[0].LaneWaypoints.Count} pos={wave[0].Position} failures={failures.Count} first={failures.FirstOrDefault()})");
        TestAssert.True(closest < 600.0, $"a lane 3 wave unit comes within 600 of capture point B within 90 s (closest {closest:F0})");
        TestAssert.True(failures.Count == 0, $"the wave's Lua AI runs with no script failure ({failures.Count} failure(s))");
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>The combat seam of the lane AI for a wave check: a live target in range attacks; no windup is pending.</summary>
    private sealed class WaveCombat : IMinionCombatService
    {
        public bool IsTargetable(Entity target, Entity observer) => target.Alive && !target.Buffs.Any(buff => buff.Payload.Untargetable || buff.Payload.Invulnerable);
        public bool IsInAttackRange(Entity attacker, Entity target) => Distance(attacker.Position, target.Position) <= 125.0 + target.Radius;
        public bool HasCommittedWindup(Entity attacker, Entity target) => false;
        public bool LastAutoAttackFinished(Entity attacker) => true;
        public void InterruptWindup(Entity attacker) { }
    }

    /// <summary>The vision seam of the lane AI for a wave check: every team sees the wave.</summary>
    private sealed class WaveVision : IMinionVisionService
    {
        public bool HasBrushVision(Team team, Entity target) => true;
    }

    /// <summary>The packet seam of the lane AI for a wave check: nothing goes to a client (the packet counts are the match's, not this check's).</summary>
    private sealed class WavePackets : IMinionPacketSink
    {
        public bool IsReferenceReady(Entity minion) => minion.Created;
        public bool TryGetClientPosition(Entity minion, out Position2 position)
        {
            position = default;
            return false;
        }

        public void SendCreate(Entity minion) { }
        public void SendVisibility(Entity minion) { }
        public void SendStats(Entity minion) { }
        public void SendWaypoints(Entity minion, IReadOnlyList<Position2> waypoints, long syncId, string reason) { }
        public void SendStop(Entity minion, long syncId, string reason) { }
        public void SendStopAttack(Entity minion, string reason) { }
    }

    private static Entity TestSpawn(WorldState world, List<Entity> spawned, string skin, Position2 at, Team team, UnitType type)
    {
        var unit = new Entity(world.Allocate(type), type == UnitType.Minion ? EntityKind.Minion : EntityKind.Monster, type, team, at)
        {
            Name = skin,
            ChampionName = skin,
            // DIAGNOSTICS: the seam has no character record; a minion gets a lane minion's move speed and is created (WaveScheduler's Order minions).
            Speed = type == UnitType.Minion ? 325.0 : 0.0,
            Created = type == UnitType.Minion,
        };
        world.Register(unit);
        spawned.Add(unit);
        return unit;
    }

    /// <summary>Asserts <paramref name="waves"/> waves of SmallWave stand on one team: two casters and one superminion each, at <paramref name="spawn"/>.</summary>
    private static void AssertWaves(List<Entity> spawned, Team team, int waves, Position3 spawn)
    {
        var caster = team == Team.Order ? "Odin_Blue_Minion_Caster" : "Odin_Red_Minion_Caster";
        var superminion = team == Team.Order ? "OdinBlueSuperminion" : "OdinRedSuperminion";
        // The team's point turrets come through the same spawn seam (OdinOrderTurretShrine), so only the wave's own skins count.
        // Batch 499: a team's waves stand on several markers (one per lane); the harness has no movement, so a lane's units stay on its marker.
        var mine = spawned.Where(unit => unit.Team == team && (unit.Name == caster || unit.Name == superminion)
            && unit.Position.X == spawn.X && unit.Position.Z == spawn.Z).ToList();
        TestAssert.Equal(2 * waves, mine.Count(unit => unit.Name == caster), $"{team}: {waves} wave(s) of two {caster} on the marker");
        TestAssert.Equal(waves, mine.Count(unit => unit.Name == superminion), $"{team}: {waves} wave(s) of one {superminion} on the marker");
    }
}
