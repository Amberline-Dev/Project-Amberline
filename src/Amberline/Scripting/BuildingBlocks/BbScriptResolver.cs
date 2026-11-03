using Amberline.Data;

namespace Amberline.Scripting.BuildingBlocks;

/// <summary>How a script entered a champion's closure.</summary>
public enum BbScriptRole
{
    Character,
    /// <summary>Data*Spell1..4.</summary>
    Spell,
    /// <summary>Data*Passive1LuaName.</summary>
    Passive,
    /// <summary>Data*ExtraSpell1..16 (child spells).</summary>
    ExtraSpell,
    /// <summary>Named by a BuffName/SpellName/BBPreloadSpell parameter of another script.</summary>
    Referenced,
}

/// <summary>How a name that has no file was referenced.</summary>
public enum BbReferenceKind { Spell, Passive, ExtraSpell, Buff, Preload }

public sealed record BbClosureScript(string Name, string SourcePath, BbScriptRole Role, string ReferencedBy, BbScript Script);

/// <summary>A referenced name with neither a script nor spell data behind it.</summary>
public sealed record BbDanglingReference(string Name, BbReferenceKind Kind, string ReferencedBy);

/// <summary>
/// Everything a champion's kit needs, in discovery order (character script first). DataOnly lists
/// names that have a Spells/Items .inibin but no .luaobj (script-less spells such as plain basic attacks).
/// </summary>
public sealed record BbClosure(
    string Champion,
    IReadOnlyList<BbClosureScript> Scripts,
    IReadOnlyList<BbDanglingReference> Dangling,
    IReadOnlyList<string> DataOnly,
    IReadOnlyList<BbDiagnostic> Diagnostics)
{
    public IEnumerable<string> ScriptNames => Scripts.Select(s => s.Name);
}

/// <summary>
/// Resolves a champion's script closure from the shipped data: CharScript&lt;N&gt;.luaobj, the
/// Data*Spell1..4 / Data*Passive1LuaName / Data*ExtraSpell1..16 names of the champion inibin, and the
/// transitive BuffName / SpellName / BBPreloadSpell references, mapped by case-insensitive name to
/// Spells/&lt;name&gt;.luaobj (then Items/). Reconstruction, not a retail trace: the client resolves these
/// names at run time through its own hash maps, and this is the static closure of the same references.
/// Not thread-safe; one instance caches every script it loads.
/// </summary>
public sealed class BbScriptResolver
{
    /// <summary>Characters that satisfy the champion rule but are forms, projectiles or map dummies.</summary>
    private static readonly string[] NonChampions =
        ["CaitlynTrap", "KogMawDead", "Nidalee_Cougar", "Nidalee_Spear", "OdinOpeningBarrier",
         "OdinTestCubeRender", "SwainNoBird"];

    private static readonly string[] SpellKeys = ["Data*Spell1", "Data*Spell2", "Data*Spell3", "Data*Spell4"];

    private readonly GameDataPaths _paths;
    private readonly Dictionary<string, BbLoadResult> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _scripts; // lower-case name -> DATA-relative .luaobj path
    private HashSet<string>? _spellData;          // lower-case names that have a .inibin

    public BbScriptResolver(GameDataPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>
    /// The playable roster: a Characters/&lt;N&gt; folder whose inibin names four real spells (not BaseSpell),
    /// with a &lt;N&gt;LoadScreen*.dds and a CharScript&lt;N&gt;.luaobj, minus <see cref="NonChampions"/>. The rule is
    /// the roster study's; it yields the 84 champions of patch 126.
    /// </summary>
    public IReadOnlyList<string> DiscoverChampions()
    {
        var result = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.Data("Characters")).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (NonChampions.Contains(name, StringComparer.Ordinal)) continue;
            if (!File.Exists(_paths.Data($"Characters/{name}/{name}.inibin")) ||
                !File.Exists(_paths.Data($"Characters/{name}/Scripts/CharScript{name}.luaobj"))) continue;
            if (!CaseInsensitivePaths.EnumerateFiles(directory, "*.dds").Any(file =>
                    Path.GetFileName(file).StartsWith(name + "LoadScreen", StringComparison.OrdinalIgnoreCase))) continue;
            var inibin = InibinReader.Load(_paths, $"Characters/{name}/{name}.inibin");
            if (SpellKeys.All(key => TextOf(inibin, key) is { Length: > 0 } spell && spell != "BaseSpell"))
                result.Add(name);
        }
        return result;
    }

    public BbClosure Resolve(string champion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(champion);
        var index = _scripts ??= IndexScripts();
        var scripts = new List<BbClosureScript>();
        var dangling = new List<BbDanglingReference>();
        var dataOnly = new List<string>();
        var diagnostics = new List<BbDiagnostic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Name, BbScriptRole Role, BbReferenceKind Kind, string Origin)>();

        InibinData? inibin = null;
        try { inibin = InibinReader.Load(_paths, $"Characters/{champion}/{champion}.inibin"); }
        catch (InibinException ex)
        {
            diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.InibinFailed, $"Characters/{champion}/{champion}.inibin", ex.Message));
        }
        if (inibin is not null)
        {
            for (var i = 0; i < SpellKeys.Length; i++)
                Seed(inibin, SpellKeys[i], BbScriptRole.Spell, BbReferenceKind.Spell);
            Seed(inibin, "Data*Passive1LuaName", BbScriptRole.Passive, BbReferenceKind.Passive);
            for (var i = 1; i <= 16; i++)
                Seed(inibin, $"Data*ExtraSpell{i}", BbScriptRole.ExtraSpell, BbReferenceKind.ExtraSpell);
        }

        var charScript = $"CharScript{champion}";
        var charPath = $"Characters/{champion}/Scripts/{charScript}.luaobj";
        if (File.Exists(_paths.Data(charPath)))
        {
            seen.Add(charScript);
            Add(charScript, charPath, BbScriptRole.Character, champion);
        }
        else diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.MissingFile, charPath, "character script not found"));

        while (queue.Count > 0)
        {
            var (name, role, kind, origin) = queue.Dequeue();
            if (!seen.Add(name)) continue;
            if (index.TryGetValue(name, out var path)) Add(Path.GetFileNameWithoutExtension(path), path, role, origin);
            else if (DataNames().Contains(name)) dataOnly.Add(name);
            else dangling.Add(new BbDanglingReference(name, kind, origin));
        }
        return new BbClosure(champion, scripts, dangling, dataOnly, diagnostics);

        void Seed(InibinData data, string key, BbScriptRole role, BbReferenceKind kind)
        {
            if (TextOf(data, key) is { Length: > 0 } name) queue.Enqueue((name, role, kind, "inibin " + key));
        }

        void Add(string name, string path, BbScriptRole role, string origin)
        {
            var loaded = Load(path);
            diagnostics.AddRange(loaded.Diagnostics);
            if (loaded.Script is null) return;
            scripts.Add(new BbClosureScript(name, path, role, origin, loaded.Script));
            foreach (var (refName, kind) in References(loaded.Script))
                queue.Enqueue((refName, BbScriptRole.Referenced, kind, name));
            foreach (var character in ReferencedCharacters(loaded.Script))
                foreach (var spell in CharacterSpells(character))
                    queue.Enqueue((spell, BbScriptRole.Referenced, BbReferenceKind.Spell, $"{name} -> character {character}"));
        }
    }

    /// <summary>Loads (and caches) one DATA-relative script.</summary>
    public BbLoadResult Load(string relativePath)
    {
        if (!_loaded.TryGetValue(relativePath, out var result))
            _loaded[relativePath] = result = BbScriptLoader.Load(_paths, relativePath);
        return result;
    }

    /// <summary>Script names a script points at: BuffName, SpellName and BBPreloadSpell.Name parameters.</summary>
    public static IEnumerable<(string Name, BbReferenceKind Kind)> References(BbScript script)
    {
        foreach (var (hook, block) in script.Walk())
        {
            if (block.Text("BuffName") is { Length: > 0 } buff) yield return (buff, BbReferenceKind.Buff);
            if (block.Text("SpellName") is { Length: > 0 } spell) yield return (spell, BbReferenceKind.Spell);
            if (hook == BbScript.PreLoadHook && block.Function == "BBPreloadSpell" &&
                block.Text("Name") is { Length: > 0 } preload)
                yield return (preload, BbReferenceKind.Preload);
        }
    }

    /// <summary>
    /// Characters a script puts a spellbook of its own on, by the names its blocks carry: the <c>SkinName</c> of a <c>BBPushCharacterData</c> with
    /// <c>OverrideSpells</c> (the pushed layer re-initialises the unit's spellbook from that character: Nidalee_Cougar, the Heimerdinger turrets) and
    /// the <c>Skin</c> of a <c>BBSpawnMinion</c> or <c>BBSpawnPet</c> (the spawned unit's character record lists the spells it casts: Shaco's box,
    /// Olaf's axe, a monkey king clone). Reconstruction: the client resolves these by name when the layer or unit is created; this is the static
    /// closure of the same references.
    /// </summary>
    public static IEnumerable<string> ReferencedCharacters(BbScript script)
    {
        foreach (var (hook, block) in script.Walk())
        {
            if (hook == BbScript.PreLoadHook) continue;
            if (block.Function == "BBPushCharacterData" && block.Text("SkinName") is { Length: > 0 } skin &&
                block.Param("OverrideSpells") is BbValue.Bool { Value: true } or BbValue.Number { Value: not 0.0 })
                yield return skin;
            else if (block.Function is "BBSpawnMinion" or "BBSpawnPet" && block.Text("Skin") is { Length: > 0 } spawned)
                yield return spawned;
        }
    }

    /// <summary>
    /// The scripted spells a character record lists (<c>Data*Spell1..4</c> and <c>Data*ExtraSpell1..16</c>, <c>BaseSpell</c> and the names with no
    /// script excluded): a character reference only adds the spells the engine can run, and adds none of the dangling or data-only noise of a
    /// dummy minion.
    /// </summary>
    private IEnumerable<string> CharacterSpells(string character)
    {
        InibinData? record = null;
        try { record = InibinReader.Load(_paths, $"Characters/{character}/{character}.inibin"); }
        catch (Exception ex) when (ex is InibinException or IOException) { /* a character with no record names no spells */ }
        if (record is null) yield break;
        var index = _scripts ??= IndexScripts();
        foreach (var key in SpellKeys.Concat(Enumerable.Range(1, 16).Select(i => $"Data*ExtraSpell{i}")))
            if (TextOf(record, key) is { Length: > 0 } name && name != "BaseSpell" && index.ContainsKey(name))
                yield return name;
    }

    private static string? TextOf(InibinData data, string key) =>
        data.TryGetValue(InibinReader.KeyHash(key), out var value) && value.Kind == InibinValueKind.String
            ? value.Text : null;

    private Dictionary<string, string> IndexScripts()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Spells win over Items when a name exists in both, as the buff lookup order in GameData.HasClientBuff.
        foreach (var directory in new[] { "Spells", "Items" })
            foreach (var file in CaseInsensitivePaths.EnumerateFiles(_paths.Data(directory), "*.luaobj").OrderBy(f => f, StringComparer.Ordinal))
                index.TryAdd(Path.GetFileNameWithoutExtension(file), $"{directory}/{Path.GetFileName(file)}");
        return index;
    }

    private HashSet<string> DataNames() => _spellData ??= new[] { "Spells", "Items" }
        .SelectMany(directory => CaseInsensitivePaths.EnumerateFiles(_paths.Data(directory), "*.inibin"))
        .Select(file => Path.GetFileNameWithoutExtension(file))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
