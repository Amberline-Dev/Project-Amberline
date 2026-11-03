namespace Amberline.Scripting.BuildingBlocks;

public enum BbValueKind
{
    Nil,
    Boolean,
    Number,
    String,
    /// <summary>A global name the script does not define itself (TEAM_ENEMY, CO_EQUAL, BUFF_Internal, a native function...).</summary>
    Reference,
    /// <summary>A Lua function defined by the file; its body is not extracted (library and AI files only).</summary>
    Closure,
    Table,
}

public readonly record struct BbField(string Key, BbValue Value);

/// <summary>
/// Immutable value of one shipped script table entry. The union covers everything the
/// 1.0.0.126 corpus holds; the hierarchy is closed so a switch over the nested types is exhaustive.
/// </summary>
public abstract record BbValue
{
    private BbValue() { }

    public abstract BbValueKind Kind { get; }

    public sealed record Nil : BbValue
    {
        public override BbValueKind Kind => BbValueKind.Nil;
    }

    public sealed record Bool(bool Value) : BbValue
    {
        public override BbValueKind Kind => BbValueKind.Boolean;
    }

    public sealed record Number(double Value) : BbValue
    {
        public override BbValueKind Kind => BbValueKind.Number;
    }

    public sealed record Text(string Value) : BbValue
    {
        public override BbValueKind Kind => BbValueKind.String;
    }

    public sealed record Reference(string Name) : BbValue
    {
        public override BbValueKind Kind => BbValueKind.Reference;
    }

    public sealed record Closure : BbValue
    {
        public override BbValueKind Kind => BbValueKind.Closure;
    }

    /// <summary>Lua table: positional items plus string-keyed fields, both in file order.</summary>
    public sealed record Table(IReadOnlyList<BbValue> Items, IReadOnlyList<BbField> Fields) : BbValue
    {
        public override BbValueKind Kind => BbValueKind.Table;

        // ponytail: linear scan; tables hold at most a dozen fields, index them if a hot path shows otherwise.
        public BbValue? Get(string key)
        {
            foreach (var field in Fields)
                if (string.Equals(field.Key, key, StringComparison.Ordinal)) return field.Value;
            return null;
        }
    }
}
