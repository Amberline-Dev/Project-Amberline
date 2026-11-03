using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

/// <summary>
/// Batch 545 (TT-2): Map4 (Twisted Treeline) sets up from its shipped scene (<see cref="MapLayout.FromScene"/>). The expected numbers are
/// lane B's independent reading of the same files (.artifacts/overnight-20261101/analysis-B-evidence: Map4-scb-centralpoints.txt from
/// scb_central.py, Map4-CreateLevelProps.disasm.txt), LEVELS/Map4/Scene/CFG/ObjectCFG.cfg and Constants.var bar_MaxHP. The layouts are
/// built from explicit paths, so the check leaves the process default alone.
/// </summary>
public static class Map4LayoutChecks
{
    [Check("map4.layout-matches-scene")]
    public static void TwistedTreelineLayoutIsItsShippedScene()
    {
        var map4 = GameDataPaths.Default.ForMap(GameMaps.TwistedTreeline);
        var layout = MapLayout.For(map4);

        // Turrets: one per CreateChildTurret row, at its Turret_*.SCB CentralPoint, in the Summoner's Rift order rule.
        var turrets = new (string Name, Team Team, TurretTier Tier, double X, double Z)[]
        {
            ("Turret_T1_C_01", Team.Order, TurretTier.Nexus, 1552.6610, 6320.8164),
            ("Turret_T1_C_06", Team.Order, TurretTier.Inhibitor, 1139.4373, 8629.6162),
            ("Turret_T1_C_07", Team.Order, TurretTier.Inhibitor, 1095.2704, 3984.5435),
            ("Turret_T1_L_02", Team.Order, TurretTier.Inner, 3946.6243, 8156.2861),
            ("Turret_T1_R_02", Team.Order, TurretTier.Inner, 3314.5413, 3375.2075),
            ("Turret_OrderTurretShrine", Team.Order, TurretTier.Fountain, -369.3660, 6362.3477),
            ("Turret_T2_C_01", Team.Chaos, TurretTier.Nexus, 11750.8838, 6288.7568),
            ("Turret_T2_L_01", Team.Chaos, TurretTier.Inhibitor, 12332.5508, 8572.4883),
            ("Turret_T2_L_02", Team.Chaos, TurretTier.Inner, 9562.3887, 8235.9336),
            ("Turret_T2_R_01", Team.Chaos, TurretTier.Inhibitor, 12355.7578, 4041.0229),
            ("Turret_T2_R_02", Team.Chaos, TurretTier.Inner, 10079.3438, 3400.3772),
            ("Turret_ChaosTurretShrine", Team.Chaos, TurretTier.Fountain, 13740.7002, 6395.2471),
        };
        TestAssert.Equal(turrets.Length, layout.Turrets.Count, "Map4's CreateLevelProps creates twelve turrets");
        for (var index = 0; index < turrets.Length; index++)
        {
            var (name, team, tier, x, z) = turrets[index];
            var turret = layout.Turrets[index];
            TestAssert.Equal(name, turret.Name, $"turret {index}");
            TestAssert.Equal(team, turret.Team, name + " team");
            TestAssert.Equal(tier, turret.Tier, name + " tier");
            TestAssert.Near(x, turret.Position.X, 1e-3, name + " X");
            TestAssert.Near(z, turret.Position.Z, 1e-3, name + " Z");
        }

        // HQs: HQ_T1/HQ_T2 CentralPoints, ObjectCFG mMaxHP 5000, mBaseStaticHPRegen 15, PathfindingCollisionRadius.
        var nexuses = new (string Name, Team Team, double X, double Z, double Pathfinding)[]
        {
            ("HQ_T1", Team.Order, 2136.0139, 6311.6104, 352.7778),
            ("HQ_T2", Team.Chaos, 11199.9639, 6312.0508, 240.7846),
        };
        TestAssert.Equal(2, layout.Nexuses.Count, "two HQs");
        for (var index = 0; index < nexuses.Length; index++)
        {
            var (name, team, x, z, pathfinding) = nexuses[index];
            var nexus = layout.Nexuses[index];
            TestAssert.Equal(name, nexus.Name, $"nexus {index}");
            TestAssert.Equal(team, nexus.Team, name + " team");
            TestAssert.Near(x, nexus.Position.X, 1e-3, name + " X");
            TestAssert.Near(z, nexus.Position.Z, 1e-3, name + " Z");
            TestAssert.Near(5000.0, nexus.MaxHealth, 1e-9, name + " ObjectCFG mMaxHP");
            TestAssert.Near(15.0, nexus.HealthRegen, 1e-9, name + " ObjectCFG mBaseStaticHPRegen");
            TestAssert.Near(pathfinding, nexus.PathfindingRadius, 1e-9, name + " ObjectCFG PathfindingCollisionRadius");
            TestAssert.Equal(MapDefinitions.BuildingNetId(name), nexus.NetId, name + " netid");
        }

        // Inhibitors: the four Barracks_T{1,2}_{L,R}1 meshes Map4 ships (ObjectCFG's C1 sections have no mesh), bar_MaxHP 3000, regen 10.
        var inhibitors = new (string Name, Team Team, double X, double Z, double Pathfinding)[]
        {
            ("Barracks_T1_L1", Team.Order, 835.5940, 8153.5283, 187.15),
            ("Barracks_T1_R1", Team.Order, 841.4701, 4564.7607, 185.4683),
            ("Barracks_T2_L1", Team.Chaos, 12563.9375, 8092.6357, 213.75),
            ("Barracks_T2_R1", Team.Chaos, 12706.2969, 4483.1538, 213.75),
        };
        TestAssert.Equal(inhibitors.Length, layout.Inhibitors.Count, "four barracks");
        for (var index = 0; index < inhibitors.Length; index++)
        {
            var (name, team, x, z, pathfinding) = inhibitors[index];
            var inhibitor = layout.Inhibitors[index];
            TestAssert.Equal(name, inhibitor.Name, $"inhibitor {index}");
            TestAssert.Equal(team, inhibitor.Team, name + " team");
            TestAssert.Near(x, inhibitor.Position.X, 1e-3, name + " X");
            TestAssert.Near(z, inhibitor.Position.Z, 1e-3, name + " Z");
            TestAssert.Near(3000.0, inhibitor.MaxHealth, 1e-9, name + " Constants.var bar_MaxHP");
            TestAssert.Near(10.0, inhibitor.HealthRegen, 1e-9, name + " ObjectCFG mBaseStaticHPRegen");
            TestAssert.Near(pathfinding, inhibitor.Radius, 1e-9, name + " ObjectCFG PathfindingCollisionRadius");
        }

        // The nav-grid stamps are the two HQs and the four barracks, each its pathfinding circle.
        TestAssert.Equal(6, layout.StructureStamps.Count, "six structure stamps");
        TestAssert.Near(352.7778, layout.StructureStamps[0].Radius, 1e-9, "HQ_T1 stamp radius");
        TestAssert.Near(185.4683, layout.StructureStamps[3].Radius, 1e-9, "Barracks_T1_R1 stamp radius");

        // Spawns and shops: __Spawn_T1/T2, and the shops under the scene's own spelling (capital O and C on Map4).
        Near(27.8138, 6360.8398, layout.TeamSpawn(Team.Order), "__Spawn_T1");
        Near(13388.1895, 6397.7666, layout.TeamSpawn(Team.Chaos), "__Spawn_T2");
        TestAssert.Equal("OrderShop01", layout.ShopName(Team.Order), "Map4's order shop mesh name");
        TestAssert.Equal("ChaosShop01", layout.ShopName(Team.Chaos), "Map4's chaos shop mesh name");
        Near(-216.8174, 7039.1987, layout.ShopPosition(Team.Order), "OrderShop01");
        Near(13585.3057, 7066.0640, layout.ShopPosition(Team.Chaos), "ChaosShop01");

        // H7: GetTurret answers from the map's own rows. HQ_TOWER2 (4) is T?_C_01 on Map4, nothing is HQ_TOWER1 (5) or a centre tower.
        var rows = MapScriptData.TurretsFor(map4);
        TestAssert.Equal("Turret_T1_C_01", LevelScriptHost.TurretName(rows, Team.Order, 0, 4), "Map4 Order HQ_TOWER2");
        TestAssert.Equal("Turret_T2_C_01", LevelScriptHost.TurretName(rows, Team.Chaos, 2, 4), "Map4 Chaos HQ_TOWER2, any lane");
        TestAssert.True(LevelScriptHost.TurretName(rows, Team.Order, 0, 5) is null, "Map4 has no HQ_TOWER1");
        TestAssert.True(LevelScriptHost.TurretName(rows, Team.Order, 1, 1) is null, "Map4 has no centre lane");
        TestAssert.Equal("Turret_T1_C_06", LevelScriptHost.TurretName(rows, Team.Order, 2, 1), "Map4 Order left BACK_TOWER");
        TestAssert.Equal("Turret_T2_R_02", LevelScriptHost.TurretName(rows, Team.Chaos, 0, 2), "Map4 Chaos right MIDDLE_TOWER");

        // The generic reader is the rule Summoner's Rift's layout follows (batch 599: Map1's scene at the exact CentralPoints): applied to Map1
        // it gives it back (names, order and positions), and Summoner's Rift's rows answer GetTurret as the old Map1 name table did.
        var map1 = GameDataPaths.Default.ForMap(GameMaps.Normal);
        var literal = MapLayout.For(map1);
        var scene = MapLayout.FromScene(map1);
        TestAssert.Equal(string.Join(",", literal.Turrets.Select(turret => $"{turret.Name}/{turret.Team}/{turret.Tier}")),
            string.Join(",", scene.Turrets.Select(turret => $"{turret.Name}/{turret.Team}/{turret.Tier}")), "Map1 turrets, order and tiers");
        for (var index = 0; index < literal.Turrets.Count; index++)
            Near(literal.Turrets[index].Position.X, literal.Turrets[index].Position.Z, scene.Turrets[index].Position, literal.Turrets[index].Name, 0.01);
        // Everything but the position is equal (the positions are too since batch 599; the tolerances are kept).
        TestAssert.Equal(literal.Nexuses.Count, scene.Nexuses.Count, "Map1 nexuses");
        for (var index = 0; index < literal.Nexuses.Count; index++)
        {
            Near(literal.Nexuses[index].Position.X, literal.Nexuses[index].Position.Z, scene.Nexuses[index].Position, literal.Nexuses[index].Name, 1e-4);
            TestAssert.Equal(literal.Nexuses[index], scene.Nexuses[index] with { Position = literal.Nexuses[index].Position }, "Map1 " + literal.Nexuses[index].Name);
        }
        TestAssert.Equal(literal.Inhibitors.Count, scene.Inhibitors.Count, "Map1 inhibitors");
        for (var index = 0; index < literal.Inhibitors.Count; index++)
        {
            Near(literal.Inhibitors[index].Position.X, literal.Inhibitors[index].Position.Z, scene.Inhibitors[index].Position, literal.Inhibitors[index].Name, 1e-4);
            TestAssert.Equal(literal.Inhibitors[index], scene.Inhibitors[index] with { Position = literal.Inhibitors[index].Position }, "Map1 " + literal.Inhibitors[index].Name);
        }
        TestAssert.Equal(string.Join(",", literal.StructureStamps.Select(stamp => $"{stamp.Name}/{stamp.Radius}")),
            string.Join(",", scene.StructureStamps.Select(stamp => $"{stamp.Name}/{stamp.Radius}")), "Map1 stamps");
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            Near(literal.TeamSpawn(team).X, literal.TeamSpawn(team).Z, scene.TeamSpawn(team), $"Map1 {team} spawn", 0.01);
            Near(literal.ShopPosition(team).X, literal.ShopPosition(team).Z, scene.ShopPosition(team), $"Map1 {team} shop", 1e-3);
            TestAssert.Equal(literal.ShopName(team), scene.ShopName(team), $"Map1 {team} shop name");
        }
        var map1Rows = MapScriptData.TurretsFor(map1);
        foreach (var (team, lane, tower, name) in new (Team, int, int, string)[]
        {
            (Team.Order, 0, 1, "Turret_T1_C_07"), (Team.Order, 1, 1, "Turret_T1_C_03"), (Team.Order, 2, 1, "Turret_T1_C_06"),
            (Team.Chaos, 0, 1, "Turret_T2_R_01"), (Team.Chaos, 1, 1, "Turret_T2_C_03"), (Team.Chaos, 2, 1, "Turret_T2_L_01"),
            (Team.Order, 1, 2, "Turret_T1_C_04"), (Team.Chaos, 2, 2, "Turret_T2_L_02"), (Team.Order, 0, 3, "Turret_T1_R_03"),
            (Team.Chaos, 1, 3, "Turret_T2_C_05"), (Team.Order, 0, 4, "Turret_T1_C_02"), (Team.Chaos, 2, 5, "Turret_T2_C_01"),
        })
            TestAssert.Equal(name, LevelScriptHost.TurretName(map1Rows, team, lane, tower), $"Map1 GetTurret({team}, {lane}, {tower})");
    }

    /// <summary>
    /// Batch 577: every Twisted Treeline camp member spawns at its Map4 NeutralMinionSpawn point (CampSpawnPoints[camp][member], the
    /// literals of the Map4 NeutralMinionSpawn bytecode listing), faces its CampFacePoints point, and
    /// stands on the terrain: the script's heights (+39 to +130) are not the jungle floor (-131), and the client draws a 0x66 unit at the
    /// packet's Y. Five scripted points lie inside the terrain (camps 1 and 2 members 3 and 4, camp 4 member 2) and keep the batch-219
    /// nearest-floor rule. Both groups of camps 1-6 are spawned (the script's own GroupsChance roll, over respawns). Batch 588: the
    /// same holds on every map, so Summoner's Rift's Baron Nashor and the Harrowing rift's camps stand on their terrain as well.
    /// </summary>
    [Check("map4.camps-spawn-at-the-script-positions")]
    public static void TwistedTreelineCampsSpawnAtTheScriptPositions()
    {
        string[] wraiths = ["Wraith", "LesserWraith", "LesserWraith", "LesserWraith"], lizards = ["Lizard", "Golem", "YoungLizard"],
            golems = ["Golem", "Golem"], wolves = ["wolf", "wolf", "GiantWolf"], rabid = ["RabidWolf", "GiantWolf", "GiantWolf"],
            ghast = ["Ghast", "Wraith", "Wraith"];
        var table = new (int Camp, string[][] Groups, double[][] Points, double FaceX, double FaceZ)[]
        {
            (1, [wraiths, lizards], [[4733, 130, 4692.49484], [4575, 115, 4726.4984], [4511, 115, 4559.4984], [4649, 116, 4495.4984]], 4692, 5474),
            (2, [wraiths, lizards], [[8700, 90, 4694.4984], [8946, 54, 4789.4984], [8850, 89, 4489.4994], [8672, 114, 4523.4984]], 8642, 5420),
            (3, [golems, wolves], [[4300, 113, 9505.4984], [4357, 100, 9354.4984], [4136, 90, 9518.4984]], 5106, 10412),
            (4, [golems, wolves], [[9176, 120, 9488.4984], [9359, 115, 9509.4984], [9117, 116, 9322.4984]], 8300, 10415),
            (5, [rabid, ghast], [[5607, 99, 9512.4984], [5747, 77, 9368.4984], [5585, 77, 9269.4984]], 6667, 10421),
            (6, [ghast, rabid], [[7617, 109, 9449.4984], [7674, 108, 9214.4984], [7906, 39, 9372.4984]], 6667, 10421),
            (7, [["blueDragon"]], [[6721, 122, 7807.4984]], 6713, 8557),
            (8, [["TwistedLizardElder"]], [[6647, 127, 5077.4984]], 6658, 4851),
        };
        var blocked = new HashSet<(int Camp, int Member)> { (1, 3), (1, 4), (2, 3), (2, 4), (4, 2) };
        var map4 = GameDataPaths.Default.ForMap(GameMaps.TwistedTreeline);
        var layout = MapLayout.For(map4);
        var grid = NavigationGrid.Load(map4, new NavigationGridSettings { Turrets = layout.Turrets, Structures = layout.StructureStamps });
        var camps = NeutralDefinitions.CampsFor(map4);
        TestAssert.Equal(table.Length, camps.Count, "Map4's eight camps");
        foreach (var (camp, groups, points, faceX, faceZ) in table)
        {
            var definition = camps[camp];
            TestAssert.Equal(string.Join("/", groups.Select(group => string.Join(",", group))),
                string.Join("/", definition.Groups.Select(group => string.Join(",", group))), $"camp {camp} groups");
            TestAssert.Equal(points.Length, definition.SpawnPoints.Count, $"camp {camp} CampSpawnPoints");
            for (var index = 0; index < points.Length; index++)
            {
                TestAssert.Near(points[index][0], definition.SpawnPoints[index].X, 1e-3, $"camp {camp}[{index + 1}] X");
                TestAssert.Near(points[index][1], definition.SpawnPoints[index].Y, 1e-3, $"camp {camp}[{index + 1}] scripted Y");
                TestAssert.Near(points[index][2], definition.SpawnPoints[index].Z, 1e-3, $"camp {camp}[{index + 1}] Z");
                TestAssert.Near(faceX, definition.FacePoints[index].X, 1e-9, $"camp {camp}[{index + 1}] face X");
                TestAssert.Near(faceZ, definition.FacePoints[index].Z, 1e-9, $"camp {camp}[{index + 1}] face Z");
                var scripted = new Position2(points[index][0], points[index][2]);
                TestAssert.Equal(blocked.Contains((camp, index + 1)), !grid.IsMovementWalkable(scripted), $"camp {camp}[{index + 1}] inside the terrain");
                TestAssert.True(points[index][1] - grid.HeightAt(scripted) > 150.0, $"camp {camp}[{index + 1}]'s scripted height is above the floor");
            }
        }

        var world = new WorldState();
        var packets = new List<PlaintextPacket>();
        var sink = new ProtocolNeutralPacketSink(packets.Add, new ReplicationSyncCounter(), world.Allocate,
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ), (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(world, new GameData(map4), sink, navigation: grid);
        var errors = new List<string>();
        scheduler.LevelScript.OnError = failure => errors.Add(failure.Hook + ": " + failure.Message);
        scheduler.Start();
        var seen = new HashSet<string>();
        var expectedGroups = table.SelectMany(row => row.Groups.Select(group => $"{row.Camp}:{string.Join(",", group)}")).ToHashSet();
        var now = 0.0;
        // Every 400 s every camp is up (the longest wait is camp 7's 100 + 180 s first spawn and its 300 s respawn) and is emptied again.
        for (var cycle = 0; cycle < 20 && !expectedGroups.IsSubsetOf(seen); cycle++)
        {
            now += 400.0;
            scheduler.Tick(now);
            foreach (var (camp, _, points, faceX, faceZ) in table)
            {
                var members = scheduler.MembersForCamp(camp).OrderBy(member => member.Member).ToArray();
                TestAssert.True(members.Length > 0, $"cycle {cycle}: camp {camp} is up");
                seen.Add($"{camp}:{string.Join(",", members.Select(member => member.Name))}");
                foreach (var member in members)
                {
                    var label = $"cycle {cycle} camp {camp} member {member.Member} {member.Name}";
                    var home = new Position2(member.Home.X, member.Home.Z);
                    var scripted = points[member.Member - 1];
                    if (blocked.Contains((camp, member.Member)))
                        TestAssert.True(grid.IsMovementWalkable(home), label + ": a point inside the terrain moves to the nearest floor");
                    else
                    {
                        TestAssert.Near(scripted[0], member.Home.X, 1e-3, label + " X is the script's");
                        TestAssert.Near(scripted[2], member.Home.Z, 1e-3, label + " Z is the script's");
                    }
                    TestAssert.Near(grid.HeightAt(home), member.Home.Y, 1e-9, label + " stands on the terrain");
                    TestAssert.True(member.Home.Y < -129.0 && member.Home.Y > -134.0, label + $" Y {member.Home.Y:0.0} is the jungle floor");
                    TestAssert.Equal(members[0].Home, member.GroupPosition, label + " group position is member 1's");
                    var facing = VisionPackets.FacingTowards(home, new Position2(faceX, faceZ));
                    TestAssert.Near(facing.X, member.Entity.Facing.X, 1e-9, label + " faces its CampFacePoints point (X)");
                    TestAssert.Near(facing.Z, member.Entity.Facing.Z, 1e-9, label + " faces its CampFacePoints point (Z)");
                    // The 0x66 the client creates the unit from: X, Y and Z at +10, +14 and +18 (SpawnPackets.CreateNeutral).
                    var wire = packets.Last(packet => packet.Plaintext.Span[0] == 0x66 &&
                        BitConverter.ToUInt32(packet.Plaintext.Span.Slice(5, 4)) == member.NetId).Plaintext.Span;
                    TestAssert.Near(member.Home.X, BitConverter.ToSingle(wire.Slice(10, 4)), 1e-3, label + " 0x66 X");
                    TestAssert.Near(member.Home.Y, BitConverter.ToSingle(wire.Slice(14, 4)), 1e-3, label + " 0x66 Y");
                    TestAssert.Near(member.Home.Z, BitConverter.ToSingle(wire.Slice(18, 4)), 1e-3, label + " 0x66 Z");
                }
                foreach (var member in members)
                {
                    member.Entity.Alive = false;
                    member.Entity.Health = 0.0;
                    scheduler.OnDeath(member.Entity, now);
                }
            }
        }
        TestAssert.Equal(0, errors.Count, "Map4's camp scripts raise nothing: " + string.Join("; ", errors));
        foreach (var group in expectedGroups) TestAssert.True(seen.Contains(group), $"camp group {group} spawned");
        TestAssert.True(seen.All(expectedGroups.Contains), "every generation is one of its camp's groups: " + string.Join(" | ", seen));
        // The red-buff camp (camp 8, TwistedLizardElder, whose script adds BlessingoftheLizardElder) faces south, to (6658, 4851).
        TestAssert.True(VisionPackets.FacingTowards(new Position2(6647, 5077.4984), new Position2(6658, 4851)).Z < -0.99, "the elder faces south");

        // Batch 588 (owner, 1 November 2026: "Yes, all maps"): Summoner's Rift's and the Harrowing rift's camps stand on the terrain too.
        // Baron Nashor (camp 12) was scripted at 24.39 over a pit floor of -72.2; the Harrowing rift's camp 11 golem 17.2 over its floor
        // and its Urf (camp 13) 0.2.
        foreach (var (mapId, rows) in new (int, (int Camp, string Name, double ScriptedY)[])[]
        {
            (GameMaps.Normal, [(1, "AncientGolem", 42.270264), (12, "Worm", 24.390015)]),
            (GameMaps.Harrowing, [(11, "Golem", 47.608), (12, "Worm", 24.390015), (13, "Urf", 0.0)]),
        })
        {
            var paths = GameDataPaths.Default.ForMap(mapId);
            var mapLayout = MapLayout.For(paths);
            var mapGrid = NavigationGrid.Load(paths, new NavigationGridSettings { Turrets = mapLayout.Turrets, Structures = mapLayout.StructureStamps });
            var mapWorld = new WorldState();
            var mapPackets = new List<PlaintextPacket>();
            var mapSink = new ProtocolNeutralPacketSink(mapPackets.Add, new ReplicationSyncCounter(), mapWorld.Allocate,
                new NavigationBounds(mapGrid.MinX, mapGrid.MaxX, mapGrid.MinZ, mapGrid.MaxZ), (x, z) => mapGrid.HeightAt(new Position2(x, z)));
            var mapCamps = NeutralDefinitions.CampsFor(paths);
            var rift = new JungleScheduler(mapWorld, new GameData(paths), mapSink, rows.Select(row => row.Camp), mapGrid);
            rift.Start();
            // The Worm's first spawn is 100 s + its GroupDelaySpawnTime 800 s.
            rift.Tick(1000.0);
            foreach (var (camp, name, scriptedY) in rows)
            {
                var member = rift.MembersForCamp(camp).Single(candidate => candidate.Member == 1);
                var label = $"map {mapId} camp {camp} {name}";
                TestAssert.Equal(name, member.Name, label);
                if (scriptedY != 0.0) TestAssert.Near(scriptedY, mapCamps[camp].SpawnPoints[0].Y, 1e-3, label + " scripted Y");
                TestAssert.Near(mapGrid.HeightAt(new Position2(member.Home.X, member.Home.Z)), member.Home.Y, 1e-9, label + " stands on the terrain");
                var wire = mapPackets.Last(packet => packet.Plaintext.Span[0] == 0x66 &&
                    BitConverter.ToUInt32(packet.Plaintext.Span.Slice(5, 4)) == member.NetId).Plaintext.Span;
                TestAssert.Near(member.Home.Y, BitConverter.ToSingle(wire.Slice(14, 4)), 1e-3, label + " 0x66 Y");
            }
            if (rows.Any(row => row.Name == "Worm"))
                TestAssert.True(rift.MembersForCamp(12)[0].Home.Y < -70.0, $"map {mapId}: Baron Nashor stands on his pit floor, not 96.6 above it");
        }
    }

    private static void Near(double x, double z, Position2 actual, string label, double tolerance = 1e-3)
    {
        TestAssert.Near(x, actual.X, tolerance, label + " X");
        TestAssert.Near(z, actual.Z, tolerance, label + " Z");
    }
}
