using Amberline.Data;

namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// One BuildingBlocks call: <c>{ Function = BBxxx, Params = {...}, SubBlocks = {...} }</c>. Params keep
/// file order. Flow control (BBIf/BBElse/BBElseIf/BBWhile) is expressed by sibling and sub-block order.
/// </summary>
public sealed record BbBlock(string Function, IReadOnlyList<BbField> Params, IReadOnlyList<BbBlock> SubBlocks)
{
    public BbValue? Param(string key)
    {
        foreach (var field in Params)
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return field.Value;
        return null;
    }

    public string? Text(string key) => (Param(key) as BbValue.Text)?.Value;

    public double? Number(string key) => (Param(key) as BbValue.Number)?.Value;
}

/// <summary>An event table such as <c>TargetExecuteBuildingBlocks</c>; <see cref="Hook"/> is the name without the suffix.</summary>
public sealed record BbEventTable(string Hook, IReadOnlyList<BbBlock> Blocks);

/// <summary>
/// One shipped script (spell, buff, character, item or talent). Everything that is not an event or
/// preload table stays in <see cref="Globals"/> (BuffTextureName, NotSingleTargetSpell, AutoCooldownByLevel...),
/// so no per-script schema is hard-coded. <c>AutoCooldownByLevel</c> is deliberately unread: it is a template table (the same
/// 20/16/12/8/4 and 22/18/14/10/6 rows sit in unrelated spells and buffs), the client never reads it and the spell inibin's
/// <c>Cooldown&lt;rank&gt;</c> is the cooldown. See the "Spell cooldowns" section of docs/scripting/building-blocks-engine.md.
/// </summary>
public sealed record BbScript(
    string Name,
    string SourcePath,
    IReadOnlyList<BbField> Globals,
    IReadOnlyList<BbEventTable> Events,
    IReadOnlyList<BbBlock> PreLoad)
{
    public const string PreLoadHook = "PreLoad";

    public bool HasBlocks => PreLoad.Count > 0 || Events.Any(e => e.Blocks.Count > 0);

    public BbValue? Global(string name)
    {
        foreach (var field in Globals)
            if (string.Equals(field.Key, name, StringComparison.Ordinal)) return field.Value;
        return null;
    }

    public BbEventTable? Event(string hook) =>
        Events.FirstOrDefault(e => string.Equals(e.Hook, hook, StringComparison.Ordinal));

    /// <summary>Every block, depth first in file order, tagged with its event hook (<see cref="PreLoadHook"/> for preloads).</summary>
    public IEnumerable<(string Hook, BbBlock Block)> Walk()
    {
        foreach (var block in PreLoad)
            foreach (var nested in Descend(block)) yield return (PreLoadHook, nested);
        foreach (var table in Events)
            foreach (var block in table.Blocks)
                foreach (var nested in Descend(block)) yield return (table.Hook, nested);
    }

    private static IEnumerable<BbBlock> Descend(BbBlock block)
    {
        yield return block;
        foreach (var child in block.SubBlocks)
            foreach (var nested in Descend(child)) yield return nested;
    }
}

public enum BbDiagnosticKind
{
    /// <summary>The file could not be read or is not a Lua chunk.</summary>
    LoadFailed,
    /// <summary>The chunk has real control flow, so its tables cannot be reconstructed.</summary>
    LoadPartial,
    /// <summary>A table key that is not a string.</summary>
    NonStringKey,
    NotABlock,
    MissingFunction,
    /// <summary>A block whose Function is not a plain global name.</summary>
    BadFunction,
    BadParams,
    BadSubBlocks,
    /// <summary>A block key other than Function, Params and SubBlocks.</summary>
    UnknownBlockKey,
    /// <summary>A *BuildingBlocks global that is neither a table nor a library function.</summary>
    BadEventTable,
    /// <summary>A champion's inibin could not be parsed.</summary>
    InibinFailed,
    /// <summary>A file the closure expects is missing.</summary>
    MissingFile,
}

/// <summary>An unknown or damaged script shape. Loading reports these instead of throwing.</summary>
public sealed record BbDiagnostic(BbDiagnosticKind Kind, string Path, string Message);

/// <summary>Outcome of loading one script. <see cref="Script"/> is null unless the Lua chunk loaded in full.</summary>
public sealed record BbLoadResult(
    string Name,
    string SourcePath,
    LuaLoadStatus Status,
    BbScript? Script,
    IReadOnlyList<BbDiagnostic> Diagnostics)
{
    /// <summary>Loaded in full with every entry understood.</summary>
    public bool IsClean => Status == LuaLoadStatus.Success && Script is not null && Diagnostics.Count == 0;
}
