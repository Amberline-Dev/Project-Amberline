using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 219: retail spawns each camp member at its scripted point. NeutralMinionSpawn.luaobj copies its literal
// Make3DPoint values straight into Positions (the Map1 bytecode listing, NeutralMinionInit);
// the engine used to move seven of them to the nearest legal wire cell. Each check below is one camp type, covering both
// mirrored camps of that type (Map1 camp n and camp n+6 for the jungle halves; Dragon and Baron are single camps).
public static class ScriptedCampSpawnChecks
{
    [Check("neutral.scripted-spawn-points.wraith")]
    public static void WraithCampsSpawnAtScriptedPoints() => AssertScriptedPoints("Wraith", 3, 9);

    [Check("neutral.scripted-spawn-points.golem")]
    public static void GolemCampsSpawnAtScriptedPoints() => AssertScriptedPoints("Golem", 5, 11);

    [Check("neutral.scripted-spawn-points.wolves")]
    public static void WolvesCampsSpawnAtScriptedPoints() => AssertScriptedPoints("Wolves", 2, 8);

    [Check("neutral.scripted-spawn-points.lizard")]
    public static void LizardCampsSpawnAtScriptedPoints() => AssertScriptedPoints("Lizard", 4, 10);

    [Check("neutral.scripted-spawn-points.ancient-golem")]
    public static void AncientGolemCampsSpawnAtScriptedPoints() => AssertScriptedPoints("Ancient Golem", 1, 7);

    [Check("neutral.scripted-spawn-points.dragon")]
    public static void DragonCampSpawnsAtScriptedPoints() => AssertScriptedPoints("Dragon", 6);

    [Check("neutral.scripted-spawn-points.baron")]
    public static void BaronCampSpawnsAtScriptedPoints() => AssertScriptedPoints("Baron", 12);

    private static void AssertScriptedPoints(string label, params int[] camps)
    {
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var scheduler = new JungleScheduler(world, GameData.Default, navigation: grid);
        scheduler.Start();
        scheduler.Tick(1000.0);

        var script = scheduler.LevelScript.NeutralCamps;
        foreach (var camp in camps)
        {
            // SpawnNeutralMinion hands member n the script's Positions[n] (LevelScriptHost, Point(Positions, memberIndex)); the
            // table can hold more points than the roster (Dragon 3 points, one member), so only the first roster-count entries apply.
            var row = script.Single(entry => entry.Number == camp);
            var scripted = row.Positions;
            var members = scheduler.MembersForCamp(camp);
            TestAssert.Equal(row.Groups[0].Count, members.Count, $"{label} camp {camp} member count");
            for (var index = 0; index < members.Count; index++)
            {
                var point = new Position2(scripted[index].X, scripted[index].Z);
                // Batch 588: every member stands on the terrain, so its Y is the grid's height there, not the script's.
                TestAssert.Near(grid.HeightAt(new Position2(members[index].Home.X, members[index].Home.Z)), members[index].Home.Y, 1e-9,
                    $"{label} camp {camp} member {index + 1} stands on the terrain");
                if (grid.IsMovementWalkable(point))
                {
                    TestAssert.True(members[index].Home == scripted[index] with { Y = members[index].Home.Y },
                        $"{label} camp {camp} member {index + 1} spawns at its scripted point {scripted[index]}, " +
                        $"not {members[index].Home}");
                    TestAssert.True(members[index].Entity.Position == point,
                        $"{label} camp {camp} member {index + 1} entity stands at its scripted point");
                    continue;
                }
                // NOT RETAIL (batch 219 interim; batch 223 removes it): a blocked scripted point keeps a nearby legal cell, the
                // placement JungleScheduler.SpawnMember gives it. The member's own cell is what its sight cannot leave.
                var home = new Position2(members[index].Home.X, members[index].Home.Z);
                TestAssert.True(grid.IsMovementWalkable(home),
                    $"{label} camp {camp} member {index + 1} moves its blocked scripted point to a legal cell");
                TestAssert.True(members[index].Entity.Position == home,
                    $"{label} camp {camp} member {index + 1} entity stands at its home");
            }
        }
    }
}
