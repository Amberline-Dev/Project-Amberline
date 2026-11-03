using System.Globalization;

namespace Amberline.Scripting.Lua;

public enum LuaType
{
    Nil,
    Boolean,
    Number,
    String,
    Table,
    Function,
}

/// <summary>
/// One runtime value of the Lua 5.1 VM. A tagged union rather than a class hierarchy: the
/// interpreter's registers hold these by copy, and table keys need value equality for numbers
/// and strings and reference identity for tables and functions.
/// </summary>
public readonly struct LuaValue : IEquatable<LuaValue>
{
    private readonly double _number;
    private readonly object? _reference;

    private LuaValue(LuaType type, double number, object? reference)
    {
        Type = type;
        _number = number;
        _reference = reference;
    }

    public LuaType Type { get; }

    public static readonly LuaValue Nil = default;

    public static LuaValue Of(bool value) => new(LuaType.Boolean, value ? 1 : 0, null);
    public static LuaValue Of(double value) => new(LuaType.Number, value, null);
    public static LuaValue Of(string value) => new(LuaType.String, 0, value);
    public static LuaValue Of(LuaTable value) => new(LuaType.Table, 0, value);
    public static LuaValue Of(LuaFunction value) => new(LuaType.Function, 0, value);

    // A table or a function is already a Lua value, so registering a library entry is just
    // `table.Set("concat", new LuaNative(...))` rather than an Of() around every call.
    public static implicit operator LuaValue(LuaTable value) => Of(value);
    public static implicit operator LuaValue(LuaFunction value) => Of(value);

    public bool IsNil => Type == LuaType.Nil;

    /// <summary>Only <see langword="false"/> and <see langword="nil"/> are falsy in Lua.</summary>
    public bool Truthy => Type switch
    {
        LuaType.Nil => false,
        LuaType.Boolean => _number != 0,
        _ => true,
    };

    public bool AsBoolean => Type == LuaType.Boolean && _number != 0;

    public double AsNumber => _number;

    public string AsString => (string)_reference!;

    public LuaTable AsTable => (LuaTable)_reference!;

    public LuaFunction AsFunction => (LuaFunction)_reference!;

    public object? RawReference => _reference;

    /// <summary>The value as a Lua number, or <see langword="null"/> when it is not a number or a numeric string.</summary>
    public double? ToNumber()
    {
        switch (Type)
        {
            case LuaType.Number:
                return _number;
            case LuaType.String:
                return LuaStrings.StringToNumber(AsString);
            default:
                return null;
        }
    }

    /// <summary>Real Lua 5.1 table-key and <c>==</c> equality: numbers and strings by value, tables and functions by reference.</summary>
    public bool Equals(LuaValue other)
    {
        if (Type != other.Type) return false;
        switch (Type)
        {
            case LuaType.Nil:
                return true;
            case LuaType.Boolean:
            case LuaType.Number:
                return _number == other._number;
            default:
                return ReferenceEquals(_reference, other._reference) ||
                       (Type == LuaType.String && string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal));
        }
    }

    public override bool Equals(object? obj) => obj is LuaValue other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)Type * 397;
            return Type switch
            {
                LuaType.Nil => hash,
                LuaType.Boolean or LuaType.Number => hash ^ _number.GetHashCode(),
                _ => hash ^ (_reference?.GetHashCode() ?? 0),
            };
        }
    }

    public static bool operator ==(LuaValue left, LuaValue right) => left.Equals(right);
    public static bool operator !=(LuaValue left, LuaValue right) => !left.Equals(right);

    public string TypeName() => Type switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => "boolean",
        LuaType.Number => "number",
        LuaType.String => "string",
        LuaType.Table => "table",
        _ => "function",
    };

    /// <summary>
    /// Lua 5.1 <c>%.14g</c> for numbers and raw bytes for strings; anything else is only
    /// printable through <c>tostring</c>, which adds the type prefix.
    /// </summary>
    public string ToPrintableString() => Type switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => AsBoolean ? "true" : "false",
        LuaType.Number => LuaStrings.NumberToString(_number),
        LuaType.String => AsString,
        _ => throw new LuaError($"attempt to concatenate a {TypeName()} value"),
    };

    public override string ToString() => Type switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => AsBoolean ? "true" : "false",
        LuaType.Number => _number.ToString("R", CultureInfo.InvariantCulture),
        LuaType.String => AsString,
        LuaType.Table => "table",
        _ => "function",
    };
}
