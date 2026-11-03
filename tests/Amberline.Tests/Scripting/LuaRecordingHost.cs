using System.Globalization;
using Amberline.Data;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Ai;
// Amberline.Data has a LuaValue record for the listing side; the runtime one wins here.
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// A host that records every native call and answers with values a script can keep using.
/// Batch 139 runs main chunks and entry points only, so this is not the engine: it is the
/// smallest stand-in that lets a shipped AI or map script run to the end without a VM error.
/// The host globals are the ones <c>docs/scripting/lua-vm.md</c> inventories; their numeric ids
/// are <c>Reconstruction:</c> because retail's are not recoverable from the shipped data.
/// </summary>
internal sealed class LuaRecordingHost
{
    private readonly GameDataPaths _paths;
    private readonly List<string> _calls = new();

    public LuaRecordingHost(GameDataPaths paths)
    {
        _paths = paths;
        State = new LuaState();
        State.OpenLibs();
        InstallHostGlobals();
        InstallScriptLoaders();
    }

    public LuaState State { get; }

    /// <summary>One line per native call, arguments included, in call order.</summary>
    public IReadOnlyList<string> Calls => _calls;

    public void Clear() => _calls.Clear();

    /// <summary>Records a call and answers with whatever the stub was given to return.</summary>
    public void Record(string name, LuaValue[] args) =>
        _calls.Add(name + "(" + string.Join(", ", args.Select(Describe)) + ")");

    public void Stub(string name, params LuaValue[] returns) =>
        State.Register(name, (state, args) =>
        {
            Record(name, args);
            return returns;
        });

    public void Stub(string name, Func<LuaValue[], LuaValue[]> answer) =>
        State.Register(name, (state, args) =>
        {
            Record(name, args);
            return answer(args);
        });

    /// <summary>Registers a recording stub for every name the chunk reads that nothing defines yet.</summary>
    public void CoverGlobals(LuaChunk chunk)
    {
        foreach (var name in ReferencedGlobals(chunk.TopLevel).Order(StringComparer.Ordinal))
        {
            if (name == "_G" || name == "_VERSION") continue;
            if (State.TryGetGlobal(name, out var existing) && !existing.IsNil) continue;
            Stub(name);
        }
    }

    /// <summary>Every <c>GETGLOBAL</c>/<c>SETGLOBAL</c> name in a prototype and its children.</summary>
    public static IEnumerable<string> ReferencedGlobals(LuaPrototype prototype)
    {
        foreach (var raw in prototype.Code)
        {
            var instruction = LuaInstruction.Decode(raw);
            if (instruction.Opcode is not ((int)LuaOpcode.GetGlobal or (int)LuaOpcode.SetGlobal)) continue;
            if (prototype.Constants[instruction.Bx] is LuaConstant.String name) yield return name.Value;
        }
        foreach (var child in prototype.Prototypes)
            foreach (var name in ReferencedGlobals(child))
                yield return name;
    }

    private void InstallHostGlobals()
    {
        // The AI id table and the `Nil` sentinel are the host's, one table for every reader
        // (this stand-in and the production AI host alike).
        AiGlobals.Install(State);
        State.SetGlobal("me", LuaValue.Of(new LuaTable()));
        State.SetGlobal("DampenerRegenerationState", LuaValue.Of(new LuaTable()));

        // The one host native with a shape the scripts depend on: a point, used as a table value.
        Stub("Make3DPoint", args =>
        {
            var point = new LuaTable();
            point.Set(LuaValue.Of("x"), args.Length > 0 ? args[0] : LuaValue.Of(0.0));
            point.Set(LuaValue.Of("y"), args.Length > 1 ? args[1] : LuaValue.Of(0.0));
            point.Set(LuaValue.Of("z"), args.Length > 2 ? args[2] : LuaValue.Of(0.0));
            return new[] { LuaValue.Of(point) };
        });

        // TaskKillHero/TaskKillMinion/TaskKillTower turn GetAttackRange into their fight and
        // engage distances in the main chunk, so the answer has to be a number. Reconstruction:
        // retail's is the owner's attack range, which the shipped data does not carry; nothing in
        // the checks asserts the value.
        Stub("GetAttackRange", LuaValue.Of(100.0));

        // `os.time` is a host clock; the interpreter has no `os` library of its own.
        var os = new LuaTable();
        os.Set(LuaValue.Of("time"), LuaValue.Of(new LuaNative("os.time", (state, args) =>
        {
            Record("os.time", args);
            return new[] { LuaValue.Of(1234567890.0) };
        })));
        State.SetGlobal("os", LuaValue.Of(os));
    }

    private void InstallScriptLoaders()
    {
        // dofile('Data\Scripts\minionLib.luaobj') runs the chunk in the caller's global env.
        Stub("dofile", args =>
        {
            var name = args.Length > 0 && args[0].Type == LuaType.String ? args[0].AsString : "";
            State.DoChunk(LuaObjectReader.ParseFile(Resolve(name)));
            return Array.Empty<LuaValue>();
        });

        // LoadScriptIntoScript merges the named chunk's globals into the caller's environment.
        Stub("LoadScriptIntoScript", args =>
        {
            var name = args.Length > 0 && args[0].Type == LuaType.String ? args[0].AsString : "";
            State.DoChunk(LuaObjectReader.ParseFile(Resolve(name)));
            return Array.Empty<LuaValue>();
        });

        // LoadScript returns the bot task module: the chunk's own environment table. Each module
        // keeps its own globals (its AI_* state ids and its UpdatePriority/BeginTask/Tick), and
        // the __index fallback is the same one LuaAiInstance builds: the natives, `math`, `os` and
        // the AI id table live in the host's globals and a module still has to see them.
        Stub("LoadScript", args =>
        {
            var name = args.Length > 0 && args[0].Type == LuaType.String ? args[0].AsString : "";
            var environment = new LuaTable();
            var metatable = new LuaTable();
            metatable.Set(LuaValue.Of("__index"), LuaValue.Of(State.Globals));
            environment.Metatable = metatable;
            var chunk = LuaObjectReader.ParseFile(Resolve(name));
            State.Call(LuaValue.Of(new LuaClosure(chunk.TopLevel, Array.Empty<LuaUpvalue>(), environment)));
            return new[] { LuaValue.Of(environment) };
        });
    }

    /// <summary>
    /// Resolves the script paths the shipped chunks use: <c>Data\Scripts\x.luaobj</c> from an AI
    /// script, bare <c>TaskX</c> from <c>LoadScript</c>, bare <c>NeutralMinionSpawn.lua</c> from a
    /// map script (relative to the map script directory). Callers spell the extension inconsistently,
    /// so every name is tried bare, with <c>.luaobj</c> and with <c>.lua</c>.
    /// </summary>
    private string Resolve(string name)
    {
        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Data/".Length..];
        var stem = normalized.EndsWith(".luaobj", StringComparison.OrdinalIgnoreCase) ? normalized[..^7]
            : normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? normalized[..^4]
            : normalized;
        foreach (var candidate in new[] { stem, stem + ".luaobj", stem + ".lua", normalized })
        {
            foreach (var root in new[]
                     {
                         Path.Combine(_paths.Root, "LEVELS", "Map1", "Scripts", Path.GetFileName(candidate)),
                         Path.Combine(_paths.Root, "LEVELS", "Map1", candidate),
                         _paths.Data(candidate),
                         _paths.Data(Path.Combine("Scripts", Path.GetFileName(candidate))),
                         Path.Combine(_paths.Root, candidate),
                     })
            {
                if (File.Exists(root)) return root;
            }
        }
        throw new InvalidOperationException($"shipped script '{name}' was not found under {_paths.Root}");
    }

    private static string Describe(LuaValue value) => value.Type switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => value.AsBoolean ? "true" : "false",
        LuaType.Number => LuaStrings.NumberToString(value.AsNumber),
        LuaType.String => "'" + value.AsString + "'",
        LuaType.Table => "table",
        _ => "function",
    };
}
