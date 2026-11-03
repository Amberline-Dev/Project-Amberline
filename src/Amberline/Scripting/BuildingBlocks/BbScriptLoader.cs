using Amberline.Data;

namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// Turns the tables <see cref="LuaObjectReader"/> extracts from a shipped .luaobj into the typed
/// <see cref="BbScript"/> model. Nothing here executes; unknown shapes become <see cref="BbDiagnostic"/>
/// entries and the offending entry is dropped, so a loader never throws on corpus data.
/// </summary>
public static class BbScriptLoader
{
    private const string EventSuffix = "BuildingBlocks";
    private const string PreLoadTable = "PreLoadBuildingBlocks";
    private const string ArrayKey = "[array]"; // LuaObjectReader.Clean key for the array part of a mixed table

    /// <summary>Loads DATA-relative <paramref name="relativePath"/> (for example <c>Spells/Disintegrate.luaobj</c>).</summary>
    public static BbLoadResult Load(GameDataPaths paths, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Build(NameOf(relativePath), relativePath, LuaObjectReader.LoadData(paths, relativePath));
    }

    public static BbLoadResult Build(string name, string sourcePath, LuaLoadResult loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        switch (loaded.Status)
        {
            case LuaLoadStatus.Success:
                return Build(name, sourcePath, loaded.Globals);
            case LuaLoadStatus.Partial:
                return Failed(name, sourcePath, loaded.Status, BbDiagnosticKind.LoadPartial, loaded.Reason);
            default:
                return Failed(name, sourcePath, loaded.Status, BbDiagnosticKind.LoadFailed, loaded.Error);
        }
    }

    /// <summary>Builds a script from already extracted globals (file order is the dictionary order).</summary>
    public static BbLoadResult Build(string name, string sourcePath, IReadOnlyDictionary<string, LuaValue> globals)
    {
        ArgumentNullException.ThrowIfNull(globals);
        var diagnostics = new List<BbDiagnostic>();
        var scalars = new List<BbField>();
        var events = new List<BbEventTable>();
        IReadOnlyList<BbBlock> preLoad = Array.Empty<BbBlock>();

        foreach (var (key, value) in globals)
        {
            var isEventName = key.Length > EventSuffix.Length &&
                key.EndsWith(EventSuffix, StringComparison.Ordinal);
            if (isEventName && IsBlockTable(value))
            {
                // Owner decision, 26 October 2026 ("Wiki value per spell"): the per-spell table overrides (BbScriptOverrides).
                var blocks = BbScriptOverrides.Apply(name, ConvertBlocks(value, key, diagnostics));
                if (string.Equals(key, PreLoadTable, StringComparison.Ordinal)) preLoad = blocks;
                else events.Add(new BbEventTable(key[..^EventSuffix.Length], blocks));
                continue;
            }
            if (isEventName && value is not LuaValue.Global { Name: null })
                diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.BadEventTable, key,
                    $"{key} is neither a block table nor a library function"));
            scalars.Add(new BbField(key, ConvertValue(value, key, diagnostics)));
        }

        var script = new BbScript(name, sourcePath, scalars, events, preLoad);
        return new BbLoadResult(name, sourcePath, LuaLoadStatus.Success, script, diagnostics);
    }

    private static BbLoadResult Failed(string name, string sourcePath, LuaLoadStatus status,
        BbDiagnosticKind kind, string? reason) =>
        new(name, sourcePath, status, null, [new BbDiagnostic(kind, sourcePath, reason ?? "no reason reported")]);

    private static string NameOf(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/'));

    // An empty Lua table is reconstructed as an empty map; both spellings mean "no blocks".
    private static bool IsBlockTable(LuaValue value) =>
        value is LuaValue.Array || value is LuaValue.Map { Values.Count: 0 };

    private static IReadOnlyList<BbBlock> ConvertBlocks(LuaValue table, string path, List<BbDiagnostic> diagnostics)
    {
        if (table is not LuaValue.Array array) return Array.Empty<BbBlock>();
        var blocks = new List<BbBlock>(array.Values.Count);
        for (var i = 0; i < array.Values.Count; i++)
            if (ConvertBlock(array.Values[i], $"{path}[{i}]", diagnostics) is { } block) blocks.Add(block);
        return blocks;
    }

    private static BbBlock? ConvertBlock(LuaValue value, string path, List<BbDiagnostic> diagnostics)
    {
        if (value is not LuaValue.Map map)
        {
            diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.NotABlock, path, "block entry is not a table"));
            return null;
        }

        string? function = null;
        IReadOnlyList<BbField> parameters = Array.Empty<BbField>();
        IReadOnlyList<BbBlock> subBlocks = Array.Empty<BbBlock>();
        foreach (var (key, child) in map.Values)
        {
            if (key is not LuaValue.String text)
            {
                diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.NonStringKey, path, "block key is not a string"));
                continue;
            }
            switch (text.Value)
            {
                case "Function":
                    if (child is LuaValue.Global { Name: { } name }) function = name;
                    else diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.BadFunction, path,
                        "Function is not a global name"));
                    break;
                case "Params":
                    if (child is LuaValue.Map paramMap) parameters = ConvertFields(paramMap, $"{path}.Params", diagnostics);
                    else diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.BadParams, path, "Params is not a table"));
                    break;
                case "SubBlocks":
                    if (IsBlockTable(child)) subBlocks = ConvertBlocks(child, $"{path}.SubBlocks", diagnostics);
                    else diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.BadSubBlocks, path, "SubBlocks is not a block table"));
                    break;
                default:
                    diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.UnknownBlockKey, path,
                        $"unexpected block key '{text.Value}'"));
                    break;
            }
        }

        if (function is null)
        {
            if (!diagnostics.Any(d => d.Kind == BbDiagnosticKind.BadFunction && d.Path == path))
                diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.MissingFunction, path, "block has no Function"));
            return null;
        }
        return new BbBlock(function, parameters, subBlocks);
    }

    private static IReadOnlyList<BbField> ConvertFields(LuaValue.Map map, string path, List<BbDiagnostic> diagnostics)
    {
        var fields = new List<BbField>(map.Values.Count);
        foreach (var (key, value) in map.Values)
        {
            if (key is LuaValue.String text)
            {
                if (text.Value != ArrayKey)
                    fields.Add(new BbField(text.Value, ConvertValue(value, $"{path}.{text.Value}", diagnostics)));
            }
            else diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.NonStringKey, path, "table key is not a string"));
        }
        return fields;
    }

    private static BbValue ConvertValue(LuaValue value, string path, List<BbDiagnostic> diagnostics)
    {
        switch (value)
        {
            case LuaValue.Nil: return new BbValue.Nil();
            case LuaValue.Boolean b: return new BbValue.Bool(b.Value);
            case LuaValue.Number n: return new BbValue.Number(n.Value);
            case LuaValue.String s: return new BbValue.Text(s.Value);
            case LuaValue.Global { Name: null }: return new BbValue.Closure();
            case LuaValue.Global g: return new BbValue.Reference(g.Name!);
            case LuaValue.Array a:
                return new BbValue.Table(ConvertItems(a, path, diagnostics), Array.Empty<BbField>());
            case LuaValue.Map m:
            {
                // A table with both parts is stored by the reader as a map with an "[array]" entry.
                IReadOnlyList<BbValue> items = Array.Empty<BbValue>();
                foreach (var (key, child) in m.Values)
                    if (key is LuaValue.String { Value: ArrayKey } && child is LuaValue.Array part)
                        items = ConvertItems(part, path, diagnostics);
                return new BbValue.Table(items, ConvertFields(m, path, diagnostics));
            }
            default:
                diagnostics.Add(new BbDiagnostic(BbDiagnosticKind.NotABlock, path, "unsupported Lua value"));
                return new BbValue.Nil();
        }
    }

    private static IReadOnlyList<BbValue> ConvertItems(LuaValue.Array array, string path, List<BbDiagnostic> diagnostics)
    {
        var items = new BbValue[array.Values.Count];
        for (var i = 0; i < items.Length; i++) items[i] = ConvertValue(array.Values[i], $"{path}[{i}]", diagnostics);
        return items;
    }
}
