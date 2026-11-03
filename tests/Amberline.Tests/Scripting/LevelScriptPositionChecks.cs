using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Ai;
// Amberline.Data has a LuaValue record for the listing side; the runtime one is meant here.
using LuaType = Amberline.Scripting.Lua.LuaType;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// Batch 212 (C11): <c>GetPosition</c> answers the object's Y with its X and Z. The shipped <c>EndOfGame.luaobj</c> calls it
/// on the losing HQ (<c>GetPosition(losingHQ)</c>) and keeps the answer in <c>losingHQPosition</c>, so one
/// ceremony call on a nexus that stands on raised ground reads the native's Y.
/// </summary>
public static class LevelScriptPositionChecks
{
    [Check("scripting.level-get-position-returns-the-unit-height")]
    public static void GetPositionReturnsTheHeightOfTheUnitsGround()
    {
        var navigation = NavigationGrid.LoadDefault();
        var ground = RaisedGround(navigation);
        var world = new WorldState();
        var nexus = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus,
            UnitType.Structure, Team.Chaos, ground));
        var host = new LevelScriptHost(world, navigation: navigation);
        host.OnLevelInit(0.0);
        TestAssert.True(host.EndOfGameCeremony(Team.Order, nexus, 0.0), "the shipped ceremony is loaded");

        var point = host.ScriptGlobal("losingHQPosition");
        TestAssert.True(point.Type == LuaType.Table, "the ceremony keeps GetPosition's answer in losingHQPosition");
        var table = point.AsTable;
        var expectedY = navigation.HeightAt(ground);
        TestAssert.True(expectedY > 1.0, "the probe point stands on raised ground");
        TestAssert.Near(ground.X, table.Get(LuaValue.Of("x")).AsNumber, 1e-9, "GetPosition keeps X");
        TestAssert.Near(expectedY, table.Get(LuaValue.Of("y")).AsNumber, 1e-9,
            "GetPosition answers the unit's height: the height the spawn and move packets send for its ground");
        TestAssert.Near(ground.Z, table.Get(LuaValue.Of("z")).AsNumber, 1e-9, "GetPosition keeps Z");
    }

    /// <summary>The first point with ground above one unit on a coarse scan of the Map1 bounds.</summary>
    private static Position2 RaisedGround(NavigationGrid navigation)
    {
        for (var x = 1000.0; x < 14000.0; x += 50.0)
            for (var z = 1000.0; z < 14000.0; z += 50.0)
                if (navigation.HeightAt(new Position2(x, z)) > 1.0) return new Position2(x, z);
        throw new InvalidOperationException("Map1 has no raised ground on the scan");
    }
}
