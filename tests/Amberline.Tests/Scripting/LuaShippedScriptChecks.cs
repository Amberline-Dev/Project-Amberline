using Amberline.Data;
using Amberline.Scripting.Lua;
// Amberline.Data has a LuaValue record for the listing side; the runtime one wins here.
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// Every plain-Lua script that shipped with the game runs its main chunk on the interpreter.
/// The natives are recording stubs — this batch wires nothing into gameplay — so what the check
/// pins is that the shipped bytecode executes to the end under the VM's own semantics, including
/// the <c>dofile</c> chains the AI scripts open and the map scripts' own helper functions.
/// </summary>
public static class LuaShippedScriptChecks
{
    [Check("scripting.lua-shipped-scripts")]
    public static void EveryShippedMainChunkRuns()
    {
        var paths = GameDataPaths.Default;
        var scripts = ShippedScripts(paths).ToList();

        // The corpus is the 25 AI/map-logic scripts in DATA/Scripts plus the 3 map scripts.
        TestAssert.True(scripts.Count >= 28, $"the shipped plain-Lua corpus is still there (found {scripts.Count})");
        foreach (var expected in new[]
                 {
                     "Minion", "MinionReverse", "minionLib", "Turret", "Leashed", "Pet",
                     "UncontrollablePet", "YorickPHPet", "Hero", "Idle", "Aggro", "EndOfGame",
                     "Zone", "Bot", "LevelScript", "NeutralMinionSpawn", "CreateLevelProps",
                 })
        {
            TestAssert.True(scripts.Any(path => Path.GetFileNameWithoutExtension(path)
                    .Equals(expected, StringComparison.OrdinalIgnoreCase)),
                $"{expected}.luaobj is in the shipped corpus");
        }

        foreach (var path in scripts)
        {
            var name = Path.GetRelativePath(paths.Root, path).Replace('\\', '/');
            try
            {
                RunMainChunk(paths, path);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"{name}: the main chunk did not run to the end.", error);
            }
        }

        // The map scripts are the ones whose main chunks do real work, and they are the only ones
        // that call a host native at load time. LevelScript only builds tables out of its own
        // constants and helper function; NeutralMinionSpawn paints the camps with Make3DPoint.
        var levelScript = new LuaRecordingHost(paths);
        CoverAndRun(levelScript, Path.Combine(paths.Root, "LEVELS", "Map1", "Scripts", "LevelScript.luaobj"));
        TestAssert.Equal(0, levelScript.Calls.Count, "LevelScript's main chunk calls no host native");

        var camps = new LuaRecordingHost(paths);
        CoverAndRun(camps, Path.Combine(paths.Root, "LEVELS", "Map1", "Scripts", "NeutralMinionSpawn.luaobj"));
        TestAssert.Equal(78, camps.Calls.Count(call => call.StartsWith("Make3DPoint(", StringComparison.Ordinal)),
            "NeutralMinionSpawn places its camps with Make3DPoint");
        TestAssert.True(camps.Calls.All(call => call.StartsWith("Make3DPoint(", StringComparison.Ordinal)),
            "Make3DPoint is the only host native NeutralMinionSpawn's main chunk needs");
    }

    private static void RunMainChunk(GameDataPaths paths, string path)
    {
        var host = new LuaRecordingHost(paths);
        CoverAndRun(host, path);
    }

    private static void CoverAndRun(LuaRecordingHost host, string path)
    {
        var chunk = LuaObjectReader.ParseFile(path);
        host.CoverGlobals(chunk);
        host.State.DoChunk(chunk);
    }

    /// <summary>The plain-Lua corpus: the AI scripts and the map scripts, never the BB spell scripts.</summary>
    private static IEnumerable<string> ShippedScripts(GameDataPaths paths) =>
        Directory.EnumerateFiles(Path.Combine(paths.Root, "DATA", "Scripts"), "*.luaobj")
            .Concat(Directory.EnumerateFiles(Path.Combine(paths.Root, "LEVELS", "Map1", "Scripts"), "*.luaobj"))
            .Order(StringComparer.OrdinalIgnoreCase);
}
