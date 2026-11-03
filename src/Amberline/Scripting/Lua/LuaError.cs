namespace Amberline.Scripting.Lua;

/// <summary>
/// A Lua error, as a CLR exception. The message is Lua 5.1's own text with the
/// <c>chunk:line:</c> prefix <c>luaG_addinfo</c> adds when the error originates in script code.
/// </summary>
public sealed class LuaError : Exception
{
    public LuaError(string message) : base(message)
    {
        Value = LuaValue.Of(message);
    }

    public LuaError(LuaValue value) : base(Describe(value))
    {
        Value = value;
    }

    /// <summary>The value <c>error()</c> raised; scripts may raise a table or a number, not only a string.</summary>
    public LuaValue Value { get; }

    /// <summary>The chunk the error came from, when the debug info allows.</summary>
    public string? Chunk { get; init; }

    /// <summary>The line in <see cref="Chunk"/>, or 0 when the debug info does not reach there.</summary>
    public int Line { get; init; }

    /// <summary>Set once a script location has been attached, so an outer frame does not re-attach its own.</summary>
    public bool IsLocated { get; private set; }

    /// <summary>Wraps a bare message in the <c>chunk:line:</c> shape Lua reports.</summary>
    public LuaError At(string? chunk, int line)
    {
        if (IsLocated) return this;
        return new LuaError(Value)
        {
            Chunk = chunk ?? Chunk,
            Line = line,
            IsLocated = true,
        };
    }

    /// <summary>An error thrown from a native that has already produced its own position prefix.</summary>
    public static LuaError Raised(LuaValue value) => new(value) { IsLocated = true };

    public override string Message => Prefix(base.Message);

    private string Prefix(string text) =>
        Chunk is null || Line <= 0 ? text : $"{LuaErrors.ChunkLabel(Chunk)}:{Line}: {text}";

    private static string Describe(LuaValue value) => value.Type switch
    {
        LuaType.String => value.AsString,
        LuaType.Nil => "nil",
        LuaType.Boolean => value.AsBoolean ? "true" : "false",
        LuaType.Number => LuaStrings.NumberToString(value.AsNumber),
        _ => $"{value.TypeName()}: {value.RawReference!.GetHashCode():x}",
    };
}

/// <summary>The fixed part of a Lua error message; kept together so the wording is auditable.</summary>
public static class LuaErrors
{
    /// <summary>Real Lua 5.1 messages, as produced by <c>luaG_typeerror</c> and friends.</summary>
    public static LuaError TypeError(LuaValue value, string operation) =>
        new($"attempt to {operation} a {value.TypeName()} value");

    public static LuaError Arithmetic(LuaValue left, LuaValue right)
    {
        // luaG_aritherror blames the operand that is not a number-like value.
        var wrong = left.ToNumber() is null ? left : right;
        return TypeError(wrong, "perform arithmetic on");
    }

    public static LuaError Order(LuaValue left, LuaValue right)
    {
        var leftType = left.TypeName();
        var rightType = right.TypeName();
        return new(string.Equals(leftType, rightType, StringComparison.Ordinal)
            ? $"attempt to compare two {leftType} values"
            : $"attempt to compare {leftType} with {rightType}");
    }

    public static LuaError BadArgument(int index, string function, string expected, LuaValue actual) =>
        new($"bad argument #{index} to '{function}' ({expected} expected, got {actual.TypeName()})");

    /// <summary>Strips the <c>@</c>/<c>=</c> source marker off a prototype's <c>Source</c> for error prefixes.</summary>
    public static string ChunkLabel(string source) =>
        source.StartsWith('@') || source.StartsWith('=') ? source[1..] : source;
}
