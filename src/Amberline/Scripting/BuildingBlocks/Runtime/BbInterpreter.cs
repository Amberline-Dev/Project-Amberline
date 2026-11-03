using System.Globalization;

namespace Amberline.Scripting.BuildingBlocks.Runtime;

/// <summary>A Lua runtime error of a script (indexing nil, arithmetic on nil, calling nil, a nil table key ...).</summary>
public sealed class BbRuntimeException : Exception
{
    public BbRuntimeException(string message) : base(message) { }

    /// <summary>The <c>gCurrentBuildingBlockString</c> of the failing block ("1.3.2"), filled in by the interpreter.</summary>
    public string BlockPath { get; internal set; } = "";

    public override string Message => BlockPath.Length == 0 ? base.Message : $"{base.Message} [block {BlockPath}]";

    internal bool AttachPath(string path)
    {
        if (BlockPath.Length == 0) BlockPath = path;
        return false; // exception filter: never catches
    }
}

/// <summary>Lua 5.1 value semantics for the runtime values (null, bool, double, string, <see cref="BbTable"/>, host handles).</summary>
internal static class BbRuntime
{
    /// <summary>Shipped value to runtime value; references resolve to their retail number, accessors to a function reference, undefined globals to nil.</summary>
    public static object? ToRuntime(BbValue? value) => value switch
    {
        null or BbValue.Nil => null,
        BbValue.Bool b => b.Value,
        BbValue.Number n => n.Value,
        BbValue.Text t => t.Value,
        BbValue.Reference r => ResolveReference(r.Name),
        BbValue.Closure c => c,
        BbValue.Table t => BbTable.From(t),
        _ => null,
    };

    public static object? ResolveReference(string name)
    {
        if (BbConstants.TryGetValue(name, out var number)) return number;
        return IsHostFunction(name) ? new BbFunctionRef(name) : null; // an undefined global reads as nil in Lua
    }

    public static bool IsHostFunction(string name) =>
        BbBaseLibrary.Contains(name) || BbNativeCatalog.OwnerOf(name) is not null ||
        BbNames<BbIncStat>.Contains(name) || BbNames<BbSetStatus>.Contains(name) || BbNames<BbGetStatus>.Contains(name) ||
        BbNames<BbGetStat>.Contains(name) || BbNames<BbGetResource>.Contains(name) ||
        BbNames<BbSlotSpellInfo>.Contains(name) || BbNames<BbCastInfo>.Contains(name);

    public static bool Truthy(object? value) => value is not null && value is not false;

    /// <summary>Lua <c>==</c>: numbers by value, strings by content, everything else as the handle defines equality (tables by identity).</summary>
    public static bool RawEquals(object? a, object? b) => a is double x && b is double y ? x == y : Equals(a, b);

    public static bool LessThan(object? a, object? b)
    {
        if (a is double x && b is double y) return x < y;
        if (a is string s && b is string t) return string.CompareOrdinal(s, t) < 0;
        throw CompareError(a, b);
    }

    public static bool LessEqual(object? a, object? b)
    {
        if (a is double x && b is double y) return x <= y;
        if (a is string s && b is string t) return string.CompareOrdinal(s, t) <= 0;
        throw CompareError(a, b);
    }

    /// <summary>
    /// Arithmetic operand. Lua 5.1 converts a numeric string with <c>luaV_tonumber</c>, which is <c>luaO_str2d</c>
    /// (<see cref="Amberline.Scripting.Lua.LuaStrings.StringToNumber"/>); a string that is not a number raises.
    /// </summary>
    public static double Number(object? value) =>
        value is double d ? d
        : value is string s && Amberline.Scripting.Lua.LuaStrings.StringToNumber(s) is { } converted ? converted
        : throw new BbRuntimeException($"attempt to perform arithmetic on a {TypeName(value)} value");

    public static double? NumberOrNull(object? value) => value as double?;

    /// <summary>Lua <c>..</c>: strings and numbers only.</summary>
    public static string Concat(object? a, object? b) => ConcatPart(a) + ConcatPart(b);

    private static string ConcatPart(object? value) => value switch
    {
        string s => s,
        double d => NumberToString(d),
        _ => throw new BbRuntimeException($"attempt to concatenate a {TypeName(value)} value"),
    };

    public static string ToStringValue(object? value) => value switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        double d => NumberToString(d),
        string s => s,
        BbTable => "table",
        BbFunctionRef f => "function: " + f.Name,
        _ => value.ToString() ?? "userdata",
    };

    /// <summary>Lua 5.1 prints numbers with <c>%.14g</c>.</summary>
    public static string NumberToString(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        return d.ToString("G14", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
    }

    public static string TypeName(object? value) => value switch
    {
        null => "nil",
        bool => "boolean",
        double => "number",
        string => "string",
        BbTable => "table",
        BbFunctionRef or BbValue.Closure => "function",
        _ => "userdata",
    };

    /// <summary>Lua's <c>t[key]</c> on a shipped table constant (the <c>*ByLevel</c> tables): 1-based positions, nil when out of range.</summary>
    public static object? Index(BbValue table, object? key)
    {
        if (table is not BbValue.Table t) throw new BbRuntimeException($"attempt to index a {TypeName(ToRuntime(table))} value");
        if (key is double d && d >= 1 && d <= t.Items.Count && d == Math.Floor(d)) return ToRuntime(t.Items[(int)d - 1]);
        if (key is string s && t.Get(s) is { } field) return ToRuntime(field);
        return null;
    }

    public static bool AsciiEqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (AsciiLower(a[i]) != AsciiLower(b[i])) return false;
        return true;
    }

    private static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c; // C's tolower in the "C" locale

    private static BbRuntimeException CompareError(object? a, object? b) =>
        new(TypeName(a) == TypeName(b)
            ? $"attempt to compare two {TypeName(a)} values"
            : $"attempt to compare {TypeName(a)} with {TypeName(b)}");
}

/// <summary>
/// Executes BuildingBlocks block lists exactly like <c>ExecuteBuildingBlocks</c> in the shipped
/// <c>DATA/BuildingBlocks/BuildingBlocksBase.luaobj</c>: blocks run in order against one shared scope table, each
/// call resets the scope's <c>___BreakExecution___</c> flag and stops when a block sets it, and flow control
/// (BBIf/BBElse/BBElseIf/BBWhile) is plain functions over sibling order and sub-block lists. Base functions run here
/// (<see cref="BbBaseLibrary"/>); native blocks are routed to the host's handler for their <see cref="BbNativeOwner"/>.
/// The interpreter does no I/O and simulates nothing itself. It is not thread safe: one instance per running hook chain.
/// </summary>
public sealed class BbInterpreter
{
    public BbInterpreter(IBbHost host) => Host = host ?? throw new ArgumentNullException(nameof(host));

    public IBbHost Host { get; }

    // The Lua globals gCurrentBuildingBlockString / Number / gCurrentBuildingBlock, kept with the same clobbering.
    public string CurrentBlockString { get; private set; } = "";
    public int CurrentBlockNumber { get; private set; } = -1;
    public BbBlock? CurrentBlock { get; private set; }

    /// <summary>How many blocks this interpreter has started, nested ones included (a diagnostic, not a Lua global).</summary>
    public long BlocksExecuted { get; private set; }

    /// <summary><c>ExecuteBuildingBlocks(blocks, scope)</c>; a null list does nothing.</summary>
    public void Execute(IReadOnlyList<BbBlock>? blocks, BbScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (blocks is null) return;
        var outer = CurrentBlockString;
        scope.Vars[BbScope.BreakExecutionKey] = false;
        RunFrom(blocks, 1, scope, outer);
    }

    private void RunFrom(IReadOnlyList<BbBlock> blocks, int first, BbScope scope, string outer)
    {
        var count = blocks.Count;
        for (var i = first; i <= count; i++)
        {
            if (scope.Vars[BbScope.BreakExecutionKey] is not false) continue; // `== false`: only the real false keeps running
            var block = blocks[i - 1];
            CurrentBlockNumber = i;
            CurrentBlockString = outer + "." + BbRuntime.NumberToString(i);
            CurrentBlock = block;
            BlocksExecuted++;
            try { Dispatch(block, scope); }
            catch (BbRuntimeException ex) when (ex.AttachPath(CurrentBlockString)) { throw; }
        }
        CurrentBlockString = outer;
        CurrentBlockNumber = -1;
        CurrentBlock = null;
    }

    /// <summary>
    /// Runs what follows the block at <paramref name="path"/> in a list that was left in the middle (an attack override's script, stopped at its
    /// primary hit so that the engine's own swing could land): the path is the 1-based index of the block in <paramref name="blocks"/>, then in the
    /// sub-blocks of that block, and so on, as <see cref="CurrentBlockString"/> reports it. Each level finishes its list after the entered block
    /// (the rest of a <c>BBIf</c> body, then the siblings after the <c>BBIf</c>), in the scope the first part left, so the variables, the flags
    /// (<c>LastIfSucceeded</c>, <c>___BreakExecution___</c>) and the outputs of the natives are exactly what an uninterrupted run would hold.
    /// Only conditional blocks may lie on the path: a loop or a native that runs its sub-blocks would have to be re-entered.
    /// </summary>
    public void ExecuteAfter(IReadOnlyList<BbBlock>? blocks, IReadOnlyList<int> path, BbScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (blocks is null || path.Count == 0) return;
        scope.Vars[BbScope.BreakExecutionKey] = false;
        Resume(blocks, path, 0, scope, CurrentBlockString);
    }

    private void Resume(IReadOnlyList<BbBlock> blocks, IReadOnlyList<int> path, int depth, BbScope scope, string outer)
    {
        var index = path[depth];
        if (index < 1 || index > blocks.Count) throw new BbRuntimeException($"cannot resume after block {string.Join(".", path)}: no such block");
        if (depth + 1 < path.Count)
            Resume(blocks[index - 1].SubBlocks, path, depth + 1, scope, outer + "." + BbRuntime.NumberToString(index));
        RunFrom(blocks, index + 1, scope, outer);
    }

    /// <summary>Runs one hook table of a script (for example <c>TargetExecute</c>); a missing hook does nothing.</summary>
    public void ExecuteHook(BbScript script, string hook, BbScope scope)
    {
        ArgumentNullException.ThrowIfNull(script);
        Execute(script.Event(hook)?.Blocks, scope);
    }

    /// <summary>Calls the block's function with <c>(scope, params, subBlocks)</c>: a base function here, a native through the host.</summary>
    internal void Dispatch(BbBlock block, BbScope scope)
    {
        var call = new BbCall(this, scope, block);
        if (BbBaseLibrary.TryGet(block.Function, out var function))
        {
            function(call);
            return;
        }
        if (BbNativeCatalog.OwnerOf(block.Function) is not { } owner)
            throw new BbRuntimeException($"attempt to call a nil value (field 'Function' = {block.Function})");
        var handler = Host.NativeHandler(owner)
            ?? throw new BbRuntimeException($"no native handler is bound for {owner} ({block.Function})");
        handler.Execute(call);
    }
}
