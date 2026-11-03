using Amberline.Core;
using Amberline.Data;

namespace Amberline.Tests;

/// <summary>
/// Batch 563 (Basic Tutorial, phase 1): map 3 sets up from its shipped scene (<see cref="MapLayout.FromScene"/>). The expected numbers are lane
/// B's independent reading (.artifacts/overnight-20261101/analysis-B-evidence/Map3-scb-centralpoints.txt and Map3-CreateLevelProps.disasm.txt),
/// LEVELS/Map3/Scene/CFG/ObjectCFG.cfg and Constants.var bar_MaxHP.
/// </summary>
public static class Map3LayoutChecks
{
    [Check("map3.layout-matches-scene")]
    public static void TheTutorialLayoutIsItsShippedScene()
    {
        var map3 = GameDataPaths.Default.ForMap(GameMaps.Tutorial);
        TestAssert.Equal("Map3", map3.MapName, "map 3 reads LEVELS/Map3");
        TestAssert.Equal("TUTORIAL", GameMaps.ModeOf(GameMaps.Tutorial), "AIR GameMode.TUTORIAL");
        var layout = MapLayout.For(map3);

        // Three CreateChildTurret rows: Order T1_C_07 (tower 1, BACK_TOWER) and T1_C_08 (2, FRONT_TOWER), Chaos T2_L_01 (1), all CENTER_LANE.
        var turrets = new (string Name, Team Team, double X, double Z)[]
        {
            ("Turret_T1_C_07", Team.Order, 3218.8638, 3263.9019),
            ("Turret_T1_C_08", Team.Order, 5911.0239, 5890.9023),
            ("Turret_T2_L_01", Team.Chaos, 8157.0527, 7985.6445),
        };
        TestAssert.Equal(turrets.Length, layout.Turrets.Count, "Map3 creates three turrets");
        for (var index = 0; index < turrets.Length; index++)
        {
            var (name, team, x, z) = turrets[index];
            TestAssert.Equal(name, layout.Turrets[index].Name, $"turret {index}");
            TestAssert.Equal(team, layout.Turrets[index].Team, name + " team");
            Near(x, z, layout.Turrets[index].Position, name);
        }
        TestAssert.Equal("OrderTurretTutorial", layout.TurretRows.Single(row => row.Name == "Turret_T1_C_08").Character, "the Order rows' character");
        TestAssert.Equal("ChaosTurretTutorial", layout.TurretRows.Single(row => row.Name == "Turret_T2_L_01").Character, "the Chaos row's character");

        // HQs 1800 with no regeneration key (ObjectCFG sets none), barracks at bar_MaxHP 1500.
        Near(1891.8784, 1965.3950, layout.Nexuses[0].Position, "HQ_T1");
        Near(12518.2295, 12062.9189, layout.Nexuses[1].Position, "HQ_T2");
        foreach (var nexus in layout.Nexuses)
        {
            TestAssert.Near(1800.0, nexus.MaxHealth, 1e-9, nexus.Name + " ObjectCFG mMaxHP");
            TestAssert.Near(0.0, nexus.HealthRegen, 1e-9, nexus.Name + " has no mBaseStaticHPRegen");
        }
        TestAssert.Equal("Barracks_T1_C1,Barracks_T2_C1", string.Join(",", layout.Inhibitors.Select(inhibitor => inhibitor.Name)), "two centre barracks");
        Near(2778.2163, 2823.3391, layout.Inhibitors[0].Position, "Barracks_T1_C1");
        Near(11597.1357, 11226.8984, layout.Inhibitors[1].Position, "Barracks_T2_C1");
        foreach (var inhibitor in layout.Inhibitors) TestAssert.Near(1500.0, inhibitor.MaxHealth, 1e-9, inhibitor.Name + " bar_MaxHP");

        // One centre lane: ____P_Order_Spawn_Barracks__C01 (four underscores), __NAV_C02..C016, ____P_Chaos_Spawn_Barracks__C01.
        TestAssert.Equal("C", string.Join(",", layout.Lanes), "Map3 ships Barracks_T{1,2}_C1 only");
        var spine = layout.LanePaths["C"];
        TestAssert.Equal(17, spine.Count, "the two barracks markers and fifteen __NAV markers");
        Near(2127.0930, 2193.9551, spine[0], "____P_Order_Spawn_Barracks__C01");
        Near(2573.6414, 2410.1738, spine[1], "__NAV_C02");
        Near(11958.2412, 11744.0469, spine[15], "__NAV_C016");
        Near(12205.3223, 11954.7148, spine[16], "____P_Chaos_Spawn_Barracks__C01");

        Near(889.7014, 1111.2827, layout.TeamSpawn(Team.Order), "__Spawn_T1");
        Near(13511.0928, 12888.2637, layout.TeamSpawn(Team.Chaos), "__Spawn_T2");
        TestAssert.Equal("OrderShop01", layout.ShopName(Team.Order), "Map3's order shop mesh name");
        Near(556.7380, 1887.2766, layout.ShopPosition(Team.Order), "OrderShop01");
    }

    private static void Near(double x, double z, Position2 actual, string what)
    {
        TestAssert.Near(x, actual.X, 1e-3, what + " X");
        TestAssert.Near(z, actual.Z, 1e-3, what + " Z");
    }
}
