using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// A script hook run that raised: the script, the hook, the block path the interpreter had reached ("2.1.3") and the
/// error. Production skips the run and logs it; the checks assert the list stays empty.
/// </summary>
public sealed record BbScriptFailure(string Script, string Hook, string BlockPath, string Message);

/// <summary>The scripts of one champion's closure by name (case-insensitive, as the client's script maps are).</summary>
internal sealed class BbScriptSet
{
    private readonly Dictionary<string, BbScript> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameDataPaths? _paths;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BbScript?> _spawned = new(StringComparer.OrdinalIgnoreCase);

    public BbScriptSet(BbClosure closure, GameDataPaths? paths = null)
    {
        _paths = paths;
        Champion = closure.Champion;
        foreach (var script in closure.Scripts)
        {
            if (script.Role == BbScriptRole.Character) Character = script.Script;
            _byName.TryAdd(script.Name, script.Script);
        }
        Closure = closure;
    }

    public string Champion { get; }
    public BbClosure Closure { get; }
    public BbScript? Character { get; }
    public BbScript? Find(string name) => _byName.TryGetValue(name, out var script) ? script : null;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BbScript?> _shippedSpells = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The script of a spell the closure may not hold: the closure's own, else the shipped <c>Spells/&lt;name&gt;.luaobj</c> (read the first time, null when
    /// the file is missing or the set was built without data). A buff of this champion that sits on another champion's hero hears that hero's casts
    /// (<c>BuffOnSpellCast</c>: Kassadin's Force Pulse sits on every hero within 1800), and the spell that was cast is not in this closure; its
    /// flags (<c>DoesntTriggerSpellCasts</c> ...) are what the buff reads from <c>SpellVars</c>.
    /// </summary>
    public BbScript? FindOrShipped(string name) => Find(name) ?? (_paths is null ? null : _shippedSpells.GetOrAdd(name, spell =>
    {
        var path = $"Spells/{spell}.luaobj";
        return File.Exists(_paths.Data(path)) ? BbScriptLoader.Load(_paths, path).Script : null;
    }));

    /// <summary>
    /// The character script (<c>CharScript&lt;Skin&gt;.luaobj</c>) of a character a script spawns with <c>BBSpawnMinion</c>, read from the shipped data
    /// the first time a unit of that character is created, or null when the character has none or the set was built without data. It is
    /// not part of the closure: the closure keeps the scripts a champion's spells and buffs name, and the character scripts of the helper
    /// units (TestCube, a placemarker) would change every champion's pin.
    /// <para>
    /// The file is not always named after the folder it sits in. The young wolf's character is <c>wolf</c> and its script is
    /// <c>Characters/wolf/Scripts/CharScriptYoungWolf.luaobj</c> (batch 159 part 2: the camp that spawns as <c>wolf</c> has no
    /// <c>CharScriptwolf.luaobj</c> at all), so a character whose own name finds nothing takes the folder's single
    /// <c>CharScript*.luaobj</c> when there is exactly one <i>and no character owns that file by its own name</i>. The one
    /// name that needs the second half of the rule is every <c>Characters/MonkeyKing*</c> folder, which all ship the
    /// champion's <c>CharScriptMonkeyKing.luaobj</c>: the flying clone and the clone are his characters and his champion
    /// module already runs that script for him, so a fallback would run it a second time on the pet. A folder with several
    /// scripts and no eponymous one answers none.
    /// </para>
    /// </summary>
    public BbScript? SpawnedCharacter(string skin) => _paths is null ? null : _spawned.GetOrAdd(skin, name =>
        SpawnedCharacterPath(name) is { } path ? BbScriptLoader.Load(_paths, path).Script : null);

    /// <summary>The shipped path <see cref="SpawnedCharacter"/> reads (a check's pin), or null when the character has none.</summary>
    internal string? SpawnedCharacterPath(string name)
    {
        if (_paths is null) return null;
        var folder = $"Characters/{name}/Scripts";
        var path = $"{folder}/CharScript{name}.luaobj";
        if (File.Exists(_paths.Data(path))) return path;
        var directory = _paths.Data(folder);
        if (!Directory.Exists(directory)) return null;
        var scripts = CaseInsensitivePaths.EnumerateFiles(directory, "CharScript*.luaobj").ToArray();
        if (scripts.Length != 1) return null;
        var file = Path.GetFileName(scripts[0]);
        var stem = file["CharScript".Length..^".luaobj".Length];
        // Batch 431 (Reconstruction): the TestCube placemarker script is shipped in Xerath's launcher folder too, and TestCube is not a champion, so no
        // champion module runs it for the launcher. Its CharOnActivate sets NoRender on the cube (the placemarker rule of BbNativeHandlers needs it).
        if (file != "CharScriptTestCube.luaobj" && File.Exists(_paths.Data($"Characters/{stem}/Scripts/{file}"))) return null;
        return $"{folder}/{file}";
    }
}
