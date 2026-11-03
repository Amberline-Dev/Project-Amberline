using Amberline.Data;
using Amberline.Scripting.Lua;

namespace Amberline.Simulation.Ai;

/// <summary>
/// Loads a shipped AI script by name and keeps the parsed chunk: one copy per match serves every
/// unit that runs it, because each unit gets its own <see cref="LuaState"/> and the chunk only
/// supplies prototypes. The name is the file stem the engine's scripts use
/// (<c>DATA/Scripts/Turret.luaobj</c> is <c>Turret</c>); the caller spelling is inconsistent
/// across the corpus, so bare names are tried with and without the extension.
/// </summary>
public sealed class AiScriptCatalog
{
    private readonly GameDataPaths _paths;
    private readonly Dictionary<string, LuaChunk> _chunks = new(StringComparer.OrdinalIgnoreCase);

    public AiScriptCatalog(GameDataPaths? paths = null) => _paths = paths ?? GameDataPaths.Default;

    public GameDataPaths Paths => _paths;

    /// <summary>The parsed chunk for one script name.</summary>
    public LuaChunk Load(string name) => Load(name, null);

    /// <summary>
    /// The parsed chunk for one script name, with the unit's own script folder searched first.
    /// A champion that ships its own <c>TaskCastSpell.luaobj</c> runs it instead of the generic
    /// one, which is how a retail bot's champion template is reached
    /// (<c>LoadTask('TaskCastSpell.lua')</c> is a server native and this is its lookup).
    /// </summary>
    public LuaChunk Load(string name, string? folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = folder is null ? name : folder + "\n" + name;
        if (_chunks.TryGetValue(key, out var cached)) return cached;
        var chunk = LuaObjectReader.ParseFile(Resolve(name, folder));
        _chunks[key] = chunk;
        return chunk;
    }

    /// <summary>Runs <see cref="Load(string)"/> into a fresh state; the shared loader for scripts chained by <c>dofile</c>.</summary>
    public void RunIn(LuaState state, string name) => state.DoChunk(Load(name));

    /// <summary>
    /// Resolves a script name the way the shipped chunks spell them: <c>Scripts/x.luaobj</c>
    /// from an AI script, bare <c>x</c> from one that omits the directory and extension. Callers
    /// spell the extension inconsistently, so every name is tried bare, with <c>.luaobj</c> and
    /// with <c>.lua</c>. <paramref name="folder"/> is searched before the shared
    /// <c>Scripts</c> folder: the unit's own <c>Characters/&lt;Name&gt;/Scripts</c> is the one a
    /// champion-specific module lives in, and only a name it actually ships is taken from there.
    /// </summary>
    public string Resolve(string name, string? folder = null)
    {
        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Data/".Length..];
        while (normalized.StartsWith("DATA/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["DATA/".Length..];
        var stem = normalized.EndsWith(".luaobj", StringComparison.OrdinalIgnoreCase) ? normalized[..^7]
            : normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? normalized[..^4]
            : normalized;
        foreach (var candidate in new[] { stem, stem + ".luaobj", stem + ".lua", normalized })
        {
            var leaf = Path.GetFileName(candidate);
            var roots = new List<string>();
            if (folder is not null) roots.Add(_paths.Data(Path.Combine(folder, leaf)));
            roots.Add(_paths.Data(candidate));
            roots.Add(_paths.Data(Path.Combine("Scripts", leaf)));
            roots.Add(_paths.Under("LEVELS", _paths.MapName, "Scripts", leaf));
            roots.Add(_paths.Under(candidate));
            foreach (var root in roots)
            {
                if (File.Exists(root)) return root;
            }
        }
        throw new InvalidOperationException($"shipped script '{name}' was not found under {_paths.Root}");
    }
}
