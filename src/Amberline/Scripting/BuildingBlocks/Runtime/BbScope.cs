namespace Amberline.Scripting.BuildingBlocks.Runtime;

/// <summary>
/// A mutable Lua table as the BuildingBlocks scripts use it: string and number keys, nil reads, nil writes remove.
/// Runtime values are plain objects: null (nil), bool, double, string, <see cref="BbTable"/>, <see cref="BbFunctionRef"/>
/// and whatever opaque handle the host uses for a unit.
/// </summary>
public sealed class BbTable
{
    private readonly Dictionary<object, object?> _entries = new();

    /// <summary>
    /// An empty table. Every variable table, <c>AvatarVars</c> included, reads an unset entry as Lua nil (batch 303): the
    /// stock client holds no avatar-variable table of its own (no <c>AvatarVars</c> string in the executable or the
    /// BuildingBlocks libraries), <c>GetTable</c> hands back nil for a missing entry, and the summoner scripts test their
    /// bonuses through <c>CO_NOT_EQUAL 0</c> and <c>GetMathNumber</c>, which read nil as "not 0" and as the literal.
    /// </summary>
    public BbTable()
    {
    }

    /// <summary>The Lua type of the value a <see cref="NotATable"/> stands for, or null for a real table.</summary>
    private readonly string? _notATable;

    private BbTable(string notATable) => _notATable = notATable;

    /// <summary>
    /// What <c>GetTable</c> hands back for a variable that holds something other than a table: retail returns the value itself
    /// (BuildingBlocksBase.luaobj proto 0.3), so nothing fails until the caller indexes it, and then with Lua's message.
    /// </summary>
    internal static BbTable NotATable(object value) => new(BbRuntime.TypeName(value));

    private void RequireTable()
    {
        if (_notATable is not null) throw new BbRuntimeException($"attempt to index a {_notATable} value");
    }

    public int Count { get { RequireTable(); return _entries.Count; } }

    /// <summary>Lua <c>t[key]</c>: a nil or unknown key reads nil; writing a nil key or NaN raises, writing nil removes.</summary>
    public object? this[object? key]
    {
        get
        {
            RequireTable();
            return key is null || !_entries.TryGetValue(Normalize(key), out var value) ? null : value;
        }
        set
        {
            RequireTable();
            if (key is null) throw new BbRuntimeException("table index is nil");
            var k = Normalize(key);
            if (k is double d && double.IsNaN(d)) throw new BbRuntimeException("table index is NaN");
            if (value is null) _entries.Remove(k);
            else _entries[k] = value;
        }
    }

    /// <summary>Snapshot of the entries, keys in no particular order.</summary>
    public IReadOnlyList<KeyValuePair<object, object?>> Entries { get { RequireTable(); return _entries.ToArray(); } }

    /// <summary>Copies a shipped table constant: positional items become keys 1..n, fields keep their names.</summary>
    public static BbTable From(BbValue.Table source)
    {
        var table = new BbTable();
        for (var i = 0; i < source.Items.Count; i++) table[(double)(i + 1)] = BbRuntime.ToRuntime(source.Items[i]);
        foreach (var field in source.Fields) table[field.Key] = BbRuntime.ToRuntime(field.Value);
        return table;
    }

    /// <summary>The <c>{x, y, z}</c> table positions use in the scripts (BBKnockback reads x and z).</summary>
    public static BbTable Point(double x, double y, double z)
    {
        var point = new BbTable();
        point["x"] = x;
        point["y"] = y;
        point["z"] = z;
        return point;
    }

    private static object Normalize(object key) => key switch
    {
        int i => (double)i,
        long l => (double)l,
        float f => (double)f,
        _ => key,
    };
}

/// <summary>A reference to a host function the script passes around as a value (an accessor or a registered global).</summary>
public sealed record BbFunctionRef(string Name);

/// <summary>
/// The <c>vars</c> table every hook runs with: unit slots (Owner, Attacker, Target, Caster, Unit, Nothing, Other1-3),
/// the variable tables (InstanceVars, NextBuffVars, CharVars, SpellVars, AvatarVars), the spell <c>Level</c>, and any
/// other root key a script writes when it names no table (BBSetVarInTable without DestVarTable lands here).
/// The interpreter's own flags (<c>___BreakExecution___</c>, <c>LastIfSucceeded</c>, <c>ReturnValue</c>) live in the
/// same table, exactly as in BuildingBlocksBase.luaobj.
/// </summary>
public sealed class BbScope
{
    public const string BreakExecutionKey = "___BreakExecution___";
    public const string LastIfSucceededKey = "LastIfSucceeded";
    public const string ReturnValueKey = "ReturnValue";
    public const string LevelKey = "Level";

    /// <summary>
    /// The <c>Nothing</c> slot is an explicit "no unit" handle. Shipped scripts use the name that way (UdyrPhoenixAttack.luaobj writes
    /// <c>TargetVar = "Nothing"</c> at instr 0117), and the exe has no "Nothing" literal, so there is no retail value to match. A sentinel
    /// keeps <c>TargetVar = "Nothing"</c> distinct from an unset variable; it is an implementation detail with no retail-visible value.
    /// </summary>
    public static readonly object NothingUnit = new NothingHandle();

    public BbScope() : this(new BbTable()) { }

    public BbScope(BbTable vars) => Vars = vars ?? throw new ArgumentNullException(nameof(vars));

    /// <summary>The root table; <c>scope[name]</c> in the Lua is <c>Vars[name]</c> here.</summary>
    public BbTable Vars { get; }

    /// <summary>A scope with the five variable tables created and the unit slots filled in.</summary>
    public static BbScope ForHook(object? owner = null, object? attacker = null, object? target = null,
        object? caster = null, double? level = null)
    {
        var scope = new BbScope
        {
            Owner = owner,
            Attacker = attacker,
            Target = target,
            Caster = caster,
            Nothing = NothingUnit,
            InstanceVars = new BbTable(),
            NextBuffVars = new BbTable(),
            CharVars = new BbTable(),
            SpellVars = new BbTable(),
            AvatarVars = new BbTable(),
        };
        if (level is { } l) scope.Level = l;
        return scope;
    }

    public object? Owner { get => Vars["Owner"]; set => Vars["Owner"] = value; }
    public object? Attacker { get => Vars["Attacker"]; set => Vars["Attacker"] = value; }
    public object? Target { get => Vars["Target"]; set => Vars["Target"] = value; }
    public object? Caster { get => Vars["Caster"]; set => Vars["Caster"] = value; }
    public object? Unit { get => Vars["Unit"]; set => Vars["Unit"] = value; }
    public object? Nothing { get => Vars["Nothing"]; set => Vars["Nothing"] = value; }
    public object? Other1 { get => Vars["Other1"]; set => Vars["Other1"] = value; }
    public object? Other2 { get => Vars["Other2"]; set => Vars["Other2"] = value; }
    public object? Other3 { get => Vars["Other3"]; set => Vars["Other3"] = value; }

    public BbTable? InstanceVars { get => Vars["InstanceVars"] as BbTable; set => Vars["InstanceVars"] = value; }
    public BbTable? NextBuffVars { get => Vars["NextBuffVars"] as BbTable; set => Vars["NextBuffVars"] = value; }
    public BbTable? CharVars { get => Vars["CharVars"] as BbTable; set => Vars["CharVars"] = value; }
    public BbTable? SpellVars { get => Vars["SpellVars"] as BbTable; set => Vars["SpellVars"] = value; }
    public BbTable? AvatarVars { get => Vars["AvatarVars"] as BbTable; set => Vars["AvatarVars"] = value; }

    /// <summary>The rank of the spell being cast (indexes every <c>*ByLevel</c> table); nil outside spell hooks.</summary>
    public double? Level
    {
        get => Vars[LevelKey] as double?;
        set => Vars[LevelKey] = value;
    }

    /// <summary>What <c>BBSetReturnValue</c> stored for the host (CanCast, BuffOnAllowAdd, ... read it).</summary>
    public object? ReturnValue => Vars[ReturnValueKey];

    private sealed class NothingHandle
    {
        public override string ToString() => "Nothing";
    }
}
